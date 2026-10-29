using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.Views;
using IcarusStarlink.Catalog;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Assets;
using IcarusStarlink.PakIO.Compare;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.Import;
using IcarusStarlink.PakIO.Install;
using IcarusStarlink.PakIO.Pak;

namespace IcarusStarlink.App.ViewModels;

/// <summary>Checking and fixing mods against the game data, and the risk pill.</summary>
public sealed partial class LibraryViewModel
{
    /// <summary>The tables, old values and Library-added rows one pass of checks shares (each Check would otherwise read them again).</summary>
    private sealed record CheckPass(Dictionary<string, JsonObject?> Tables, GameValueHistory History, IReadOnlySet<string> AddedRows)
    {
        /// <summary>Reads what a pass shares (reads every mod: off the UI thread).</summary>
        public static CheckPass Create(ModFixService fixService) =>
            new(new Dictionary<string, JsonObject?>(StringComparer.OrdinalIgnoreCase), fixService.History(), fixService.LibraryAddedRows());
    }

    /// <summary>The game's recorded changes from recent Refresh game data runs; set by the app (old values a mod may still hold).</summary>
    public Func<IEnumerable<FieldChange>>? GameHistory { get; init; }

    private ModFixService? _modFixService;

    /// <summary>The app's one fix service (shared with the replacement that re-applies fixes after an update); set by the app (null: made here on first use).</summary>
    public ModFixService? SharedFixService
    {
        init => _modFixService = value;
    }

    /// <summary>Where each mod's remembered fix choices are kept; set by the app (null: nothing remembered).</summary>
    public string? FixDecisionsDirectory { get; init; }

    /// <summary>The game's own content, to tell a mod's copy of a game file from a file of its own; set by the app (null: not checked).</summary>
    public IBaseGameContentProvider? BaseGameContent { get; init; }

    private bool _fileRiskPassRunning;
    private bool _fileRiskPassAgain;

    /// <summary>The game build the first risk pass this session read (the one the game's mounted content is).</summary>
    private string? _sessionGameBuild;

    /// <summary>
    /// Rates the game files every mod replaces (the risk pill) and finds mods replacing the same
    /// file with different copies. In the background after the Library loads and after mods
    /// change; the first call mounts the game's content (about a second), every later one is
    /// quick. A pass asked for while one runs runs once more after it.
    /// </summary>
    private async Task RefreshFileRisksAsync()
    {
        if (BaseGameContent is not { } game)
        {
            return;
        }

        if (_fileRiskPassRunning)
        {
            _fileRiskPassAgain = true;
            return;
        }

        _fileRiskPassRunning = true;
        try
        {
            do
            {
                _fileRiskPassAgain = false;
                // Caught per round: a failed round must not drop the re-run asked for meanwhile.
                try
                {
                    await RateFileRisksOnceAsync(game);
                }
                catch (Exception ex)
                {
                    _activityLog.Log($"Couldn't rate the game files mods replace: {ex.Message}", ActivityEntryKind.Warning);
                }
            }
            while (_fileRiskPassAgain);
        }
        finally
        {
            _fileRiskPassRunning = false;
        }
    }

    /// <summary>One round of RefreshFileRisksAsync.</summary>
    private async Task RateFileRisksOnceAsync(IBaseGameContentProvider game)
    {
        var items = AllLibraryItems().Where(i => !i.IsMergedPack).ToList();
        var folders = items.Select(i => (i.FolderName, i.Name)).ToList();
        // The build on disk: the game's own copies of the files mods replace are recorded
        // for it, so what a later game update changes in them is known.
        var build = GameUpdates is { } updates ? await updates.ReadCurrentBuildAsync() : null;
        // The game's content is read once per app session: after Steam updated the game
        // while the app ran, what's read is still the build from before, so nothing is
        // recorded (or said) until the next start.
        _sessionGameBuild ??= build;
        var fingerprints = build is not null && string.Equals(build, _sessionGameBuild, StringComparison.OrdinalIgnoreCase) ? GameFileFingerprints : null;
        var risks = await Task.Run(() =>
        {
            var rated = FileRiskAssessor.Assess(_repository, folders, game, fingerprints, build);
            try
            {
                fingerprints?.SaveIfChanged();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Recorded again on the next pass.
            }

            return rated;
        });
        foreach (var item in items)
        {
            item.FileRisk = risks.GetValueOrDefault(item.FolderName);
        }

        // The Problems chip counts high-risk rows, and with it on, its list shows them.
        RefreshFilters();
    }

    private ModFixService FixService => _modFixService ??= ModFixService.Registered(new ModFixService(_repository, _dataFolder, GameHistory, FixDecisionsDirectory,
        BaseGameContent as IGameAssetCatalog));

    /// <summary>
    /// The row's warning badge, and right-click › Check against the game &amp; fix…: the fix window,
    /// listing what in this mod doesn't fit the current game with a fix for each. Checks the mod
    /// now when no whole-library check has yet. Not while a whole-library check or Fix sure
    /// problems is running (they write mods too), and not while the mod is open in the editor:
    /// the fix window is modal, so the editor couldn't be closed from behind it.
    /// </summary>
    /// <summary>Whether the extracted game data folder has any data in it. A folder that can't be read
    /// counts as none (said as "run Refresh game data") rather than an unhandled error from a click.</summary>
    private bool HasExtractedGameData()
    {
        try
        {
            return Directory.Exists(_dataFolder) && Directory.EnumerateFiles(_dataFolder, "*.json", SearchOption.AllDirectories).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [RelayCommand]
    private async Task OpenModFixAsync(LibraryItemViewModel? item)
    {
        if (item is null || item.IsOpaquePak || _openingModFix)
        {
            return;
        }

        if (await CheckAndShowModFixAsync(item) is { } failed)
        {
            StatusMessage = failed;
        }
    }

    /// <summary>
    /// Checks the mod and shows its fix window (modal), then refreshes its badge. Returns why it
    /// couldn't (busy, no game data, the check failed), or null once the window has been shown and
    /// closed — so whoever asked can say it where the user is.
    /// </summary>
    private async Task<string?> CheckAndShowModFixAsync(LibraryItemViewModel item)
    {
        if (ModFixBlockedReason(item) is { } blocked)
        {
            return blocked;
        }

        if (!HasExtractedGameData())
        {
            return "No game data found — run Refresh game data in Settings first.";
        }

        ModFixViewModel? viewModel = null;
        string? failed = null;
        try
        {
            var folderName = item.FolderName;
            // Off the UI thread (a whole mod against the game data); one window at a time meanwhile.
            ModHealthReport report;
            _openingModFix = true;
            try
            {
                report = await Task.Run(() => FixService.Check(folderName));
            }
            finally
            {
                _openingModFix = false;
            }

            // A check or refresh may have started while this one ran.
            if (ModFixBlockedReason(item) is { } blockedNow)
            {
                return blockedNow;
            }

            item.SetHealth(report);
            viewModel = new ModFixViewModel(folderName, item.Name, report, FixService, () => _openEditorCountByFolder.ContainsKey(folderName));
            var window = new ModFixWindow(viewModel) { Owner = Application.Current.MainWindow };
            viewModel.OpenInModEditor = () =>
            {
                var first = report.Issues.FirstOrDefault();
                window.Close();
                OpenEditor(folderName, first is null ? null : (first.CurrentFile, first.ItemName));
            };

            window.ShowDialog();
        }
        catch (Exception ex)
        {
            failed = $"Couldn't check '{item.Name}': {ex.Message}";
        }

        if (viewModel is not null)
        {
            await AfterModFixClosedAsync(item, viewModel);
        }

        return failed;
    }

    /// <summary>
    /// Fix… from Merge &amp; Install's check: checks the mod and shows its fix window, finishing with
    /// why it couldn't (refused, or the check failed), or null once the window was shown and closed.
    /// Said on the page the click came from, since this page's notice isn't on screen.
    /// </summary>
    internal async Task<string?> OpenModFixFromElsewhereAsync(string folderName)
    {
        if (FindItem(folderName) is not { } item)
        {
            return "That mod isn't in the Library any more.";
        }

        if (item.IsOpaquePak)
        {
            return $"'{item.Name}' is a prebuilt pak: make it editable in the Library first.";
        }

        if (_openingModFix)
        {
            return "A fix window is already opening.";
        }

        return await CheckAndShowModFixAsync(item);
    }

    /// <summary>What this page is rewriting mods for right now (asked before the app closes for an update); empty when nothing.</summary>
    internal IEnumerable<string> DescribeWorkInProgress()
    {
        if (IsCheckingStaleness)
        {
            yield return "checking or fixing mods against the game data";
        }

        if (IsConvertingAllOpaqueMods || _foldersCurrentlyConvertingToExmod.Count > 0)
        {
            yield return "making prebuilt paks editable";
        }
    }

    /// <summary>The fix window's own check is running (off the UI thread): a second click waits for it.</summary>
    private bool _openingModFix;

    /// <summary>Why the fix window can't open for this mod right now (null: it can).</summary>
    internal string? ModFixBlockedReason(LibraryItemViewModel item) => ModBusyReason(item, "so its next Save doesn't undo the fixes");

    /// <summary>
    /// Why the mod can't be restored, deleted, reinstalled or updated right now (null: it can) — the
    /// fix window's guards: a check or fix pass rewriting mods in the background, this mod being
    /// converted again, or its editor open (whose next Save would write the old copy back).
    /// </summary>
    internal string? ModChangeBlockedReason(LibraryItemViewModel item) => ModBusyReason(item, "so its next Save doesn't write the old copy back");

    private string? ModBusyReason(LibraryItemViewModel item, string editorReason)
    {
        if (IsCheckingStaleness)
        {
            return "Mods are being checked or fixed against the game right now. Try again when that's done.";
        }

        if (_foldersCurrentlyConvertingToExmod.Contains(item.FolderName))
        {
            return $"'{item.Name}' is being refreshed against the game data right now. Try again in a moment.";
        }

        return _openEditorCountByFolder.ContainsKey(item.FolderName)
            ? $"'{item.Name}' is open in the mod editor. Close it there first, {editorReason}."
            : null;
    }

    /// <summary>
    /// After the fix window closes, however it closed: the badge is checked again (Restore backup
    /// and Forget change what's flagged too), and only fixes actually made are logged as such
    /// ("Keep all as they are" applies nothing; it only remembers the choices).
    /// </summary>
    internal async Task AfterModFixClosedAsync(LibraryItemViewModel item, ModFixViewModel viewModel)
    {
        var folderName = item.FolderName;
        try
        {
            item.SetHealth(await Task.Run(() => FixService.Check(folderName)));
            RefreshFilters();
        }
        catch (Exception)
        {
            // The badge keeps what the window opened with.
        }

        var changedOnDisk = viewModel.AppliedCount > 0 || viewModel.ChangedWithoutApply;
        if (changedOnDisk)
        {
            item.NotifyBackupStateChanged();
            item.NotifyRepairedFromDisk();
            if (ReferenceEquals(item, SelectedItem))
            {
                item.EnsureDetailsLoaded();
            }
        }

        if (viewModel.IsApplied && viewModel.StatusMessage is { } status)
        {
            StatusMessage = status;
        }

        if (viewModel.AppliedCount > 0)
        {
            _activityLog.Log($"Fixed {viewModel.AppliedCount} thing(s) in '{item.Name}' to fit the current game (backed up first).", ActivityEntryKind.Success);
        }
    }

    /// <summary>
    /// The automatic part: every sure fix (old copies of game data, proven broken links and
    /// renames) and every choice made before, in every mod the last check flagged, each backed up
    /// first. Tidying alone (values the game already has) never: a hand-made mod that fits the game
    /// isn't rewritten. What needs a look stays flagged for the fix window.
    /// </summary>
    [RelayCommand]
    private Task FixSureProblemsAsync() => FixSureProblemsCoreAsync();

    /// <summary>Fix sure problems in every mod; what was fixed, per mod (Update everything lists it).</summary>
    internal async Task<IReadOnlyList<GameUpdateFixedMod>> FixSureProblemsCoreAsync()
    {
        var fixedMods = new List<GameUpdateFixedMod>();
        if (IsCheckingStaleness)
        {
            return fixedMods;
        }

        // A mod being converted again right now is left for that conversion (it rewrites the mod).
        var flagged = AllLibraryItems()
            .Where(i => i.Health is { SureProblemCount: > 0 } && !i.IsOpaquePak && !_foldersCurrentlyConvertingToExmod.Contains(i.FolderName))
            .ToList();
        if (flagged.Count == 0)
        {
            StalenessCheckStatusMessage = "Nothing sure to fix. Run Check mods against game data first, or click a flagged row's warning badge.";
            return fixedMods;
        }

        var folders = flagged.Select(i => i.FolderName).ToList();
        // Mods whose editor is open are skipped, checked again right before each one is written
        // (an editor can be opened while the pass runs): the editor's next Save would undo the fixes.
        var skippedFolders = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        IsCheckingStaleness = true;
        try
        {
            var results = await Task.Run(() =>
            {
                var pass = CheckPass.Create(FixService);
                var outcome = new Dictionary<string, (int Applied, ModHealthReport After)>(StringComparer.OrdinalIgnoreCase);
                foreach (var folder in folders)
                {
                    if (IsEditorOpenLive(folder))
                    {
                        skippedFolders[folder] = true;
                        continue;
                    }

                    try
                    {
                        var (applied, _) = FixService.FixSure(folder, pass.Tables, pass.History, pass.AddedRows);
                        outcome[folder] = (applied, FixService.Check(folder, pass.Tables, pass.History, pass.AddedRows));
                    }
                    catch (Exception)
                    {
                        // One unreadable mod doesn't stop the others.
                    }
                }

                return outcome;
            });

            var total = 0;
            var openedDuringPass = new List<string>();
            foreach (var item in flagged)
            {
                if (results.TryGetValue(item.FolderName, out var result))
                {
                    item.SetHealth(result.After);
                    if (result.Applied > 0)
                    {
                        total += result.Applied;
                        fixedMods.Add(new GameUpdateFixedMod(item.FolderName, item.Name, result.Applied));
                        item.NotifyBackupStateChanged();
                        item.NotifyRepairedFromDisk();
                        _activityLog.Log($"Fixed {result.Applied} thing(s) in '{item.Name}' to fit the current game (backed up first).", ActivityEntryKind.Success);

                        // Opened in the editor after this mod's own check but before the pass ended:
                        // that editor still holds the mod as it was, and its Save would undo these.
                        if (_openEditorCountByFolder.ContainsKey(item.FolderName))
                        {
                            openedDuringPass.Add(item.Name);
                        }
                    }
                }
            }

            var skipped = flagged.Where(i => skippedFolders.ContainsKey(i.FolderName)).Select(i => i.Name).ToList();
            var failed = flagged.Where(i => !skippedFolders.ContainsKey(i.FolderName) && !results.ContainsKey(i.FolderName)).Select(i => i.Name).ToList();
            var left = flagged.Count(i => i.HasModIssues);
            StalenessCheckStatusMessage = $"Fixed {total} sure thing(s) in {results.Count(r => r.Value.Applied > 0)} mod(s), each backed up first (right-click a mod › Restore latest backup undoes it)."
                + (left > 0 ? $" {left} mod(s) still have things to look over: click a row's warning badge." : "")
                + (skipped.Count > 0 ? $" Skipped (open in the mod editor): {string.Join(", ", skipped)}." : "")
                + (failed.Count > 0 ? $" Couldn't read: {string.Join(", ", failed)}." : "")
                + (openedDuringPass.Count > 0 ? $" Opened in the mod editor while fixing: {string.Join(", ", openedDuringPass)} — close that editor without saving, or its Save undoes the fixes." : "");

            RefreshFilters();
            if (total > 0)
            {
                // Like Apply and the editor's Save: the rest of the app (merge state, details) re-reads the rewritten mods.
                WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
            }

            SelectedItem?.EnsureDetailsLoaded();
            return fixedMods;
        }
        finally
        {
            IsCheckingStaleness = false;
            RunPendingRefresh();
        }
    }

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private Task CheckModsAgainstCurrentData() => CheckModsAgainstCurrentDataAsync();

    /// <summary>
    /// A separate question and a separate action from CheckForUpdatesAsync above (that's Nexus/
    /// catalog version-string checking) — this asks "does this mod's own data still match what the
    /// currently-extracted game data actually defines", independent of any catalog or version
    /// string. Explicit action (a toolbar button), not automatic on every Library load, since a
    /// whole-library base-vs-modded diff is real work a routine navigation shouldn't pay for.
    ///
    /// Looks only: what it finds (ModHealthChecker, through the fix service) is fixed from the fix
    /// window or Check ▾ › Fix sure problems. It used to rename rows silently, which renamed mods'
    /// own new rows to similar game rows (Homestead's Water_Well became the game's Waterwheel).
    /// </summary>
    internal async Task CheckModsAgainstCurrentDataAsync()
    {
        // Fix sure problems and the refresh after a game data update write mods: never at once.
        // Checked here, not via [RelayCommand]'s CanExecute, since IsCheckingStaleness itself is what's guarded.
        if (IsCheckingStaleness)
        {
            return;
        }

        // Opaque .pak entries have no .EXMOD to diff — same exclusion EnsureDetailsLoaded's own
        // ChangesContent formatting already applies.
        var candidates = AllLibraryItems().Where(i => !i.IsOpaquePak).ToList();
        if (candidates.Count == 0)
        {
            // Said, not silent: the click otherwise looked like it did nothing.
            StalenessCheckStatusMessage = "No editable mods to check — prebuilt paks can't be compared with the game data (Make prebuilt paks editable first).";
            return;
        }

        // Without this, a Data folder that's missing, empty, or was never extracted makes every
        // mod's own base-file lookup fail silently — every mod would come back as fitting the game,
        // a false all-clear. Same check, same wording, ExmodEditorViewModel's own game-data search
        // already uses for the identical underlying condition.
        if (!HasExtractedGameData())
        {
            StatusMessage = "No game data found — run Refresh game data in Settings first.";
            return;
        }

        IsCheckingStaleness = true;
        try
        {
            var folderNames = candidates.Select(i => i.FolderName).ToList();

            // Off the UI thread: every non-opaque mod against the real extracted game data is real,
            // avoidable-to-block-on work for a large library. Results are collected into a plain
            // dictionary and applied to the rows back on this (UI) thread afterward.
            var resultsByFolder = await Task.Run(() =>
            {
                // Shared for the whole pass, not per mod — many mods touch the same real file (e.g.
                // Traits-D_Itemable.json), and re-parsing it once per mod that touches it is real,
                // avoidable cost.
                var pass = CheckPass.Create(FixService);
                var results = new Dictionary<string, ModHealthReport?>();
                foreach (var folderName in folderNames)
                {
                    try
                    {
                        results[folderName] = FixService.Check(folderName, pass.Tables, pass.History, pass.AddedRows);
                    }
                    catch (Exception)
                    {
                        // Best-effort per mod, matching CheckForUpdatesAsync's own per-source
                        // isolation — a locked or malformed .EXMOD just means that one mod isn't
                        // checked this pass (named below; its badge keeps what it had).
                        results[folderName] = null;
                    }
                }

                return results;
            });

            var flaggedCount = 0;
            var couldNotCheck = new List<string>();
            foreach (var item in candidates)
            {
                if (resultsByFolder.GetValueOrDefault(item.FolderName) is not { } health)
                {
                    couldNotCheck.Add(item.Name);
                }
                else
                {
                    item.SetHealth(health);
                }

                if (item.Health is { HasProblems: true })
                {
                    flaggedCount++;
                }
            }

            var messageParts = new List<string>
            {
                flaggedCount > 0
                    ? $"{flaggedCount} mod(s) don't fully fit the current game — click a flagged row's warning badge to see and fix them, or Check ▾ › Fix sure problems in every mod."
                    : couldNotCheck.Count > 0 ? "Every mod that could be checked fits the current game." : "Every mod fits the current game.",
            };
            if (couldNotCheck.Count > 0)
            {
                messageParts.Add($"Couldn't check {couldNotCheck.Count} mod(s): {string.Join(", ", couldNotCheck)}.");
            }

            StalenessCheckStatusMessage = string.Join(" ", messageParts);
            RefreshFilters();
        }
        finally
        {
            IsCheckingStaleness = false;
            RunPendingRefresh();
        }
    }

    /// <summary>Checks one row's mod again off the UI thread and updates its badge; a failed check keeps the badge it had.</summary>
    /// <param name="pass">What a pass of checks shares (the refresh after a game data update); null reads it afresh.</param>
    private async Task RefreshHealthAsync(LibraryItemViewModel item, CheckPass? pass = null)
    {
        var folderName = item.FolderName;
        try
        {
            item.SetHealth(await Task.Run(() => FixService.Check(folderName, pass?.Tables, pass?.History, pass?.AddedRows)));
            RefreshFilters();
        }
        catch (Exception)
        {
            // Only the badge: the next check against game data tries again.
        }
    }
}
