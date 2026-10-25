using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.InstallComparison;
using IcarusStarlink.PakIO;
using IcarusStarlink.PakIO.ArcFission;
using IcarusStarlink.PakIO.Install;

namespace IcarusStarlink.App.ViewModels;

/// <summary>The game's mods folder: Install, Copy to game, Remove from game, Restore previous install, what is installed now (and ArcFission Library's data pak), and Compare.</summary>
public sealed partial class MergeInstallViewModel
{
    /// <summary>Whether this app's own pak is currently installed in the real game folder — refreshed at construction and after a successful install/remove, never live-polled. Drives InstallButtonLabel; deliberately coarse (not "has the queue changed since the last install", which would need a live diff every queue edit) — see the combined Install button's own doc comment for why.</summary>
    [ObservableProperty]
    private bool _hasExistingInstall;

    public string InstallButtonLabel => HasExistingInstall ? "Update install" : "Install";

    partial void OnHasExistingInstallChanged(bool value) => OnPropertyChanged(nameof(InstallButtonLabel));

    [ObservableProperty]
    private string? _installStatusMessage;

    // --- Installed vs this list (6.6) ---
    /// <summary>"+ Name" (would be added), "- Name" (would be removed), "= Name" (unchanged) — a preview of what clicking Install would actually do, not run automatically since it needs a real read of the game folder.</summary>
    public ObservableCollection<string> ComparisonEntries { get; } = [];

    [ObservableProperty]
    private string? _comparisonStatusMessage;

    /// <summary>
    /// Checks for the real installed pak's own file directly, not GetInstalledStateAsync's parsed
    /// mod-name list — a gameplay-options-only install (an empty queue, per the spec's own "add
    /// merge options to game with no mods selected") still produces a real pak but lists zero mod
    /// names in the manifest, which would make a Count-based check wrongly say nothing's installed.
    /// </summary>
    internal Task RefreshHasExistingInstallAsync()
    {
        // An install, Remove or Restore finishing while this runs knows better: its answer stays
        // (this one, started at construction, used to land after an install and say "not installed").
        var version = _existingInstallVersion;
        return Task.Run(() =>
        {
            // A staged build on disk is all Copy to game needs — independent of whether anything is
            // currently installed in the game folder.
            CanCopyToGame = HasCompleteStagedBuild();

            if (string.IsNullOrWhiteSpace(_settingsService.Current.IcarusContentPath))
            {
                SetHasExistingInstallIfCurrent(version, false);
                return;
            }

            try
            {
                // The merged pak under an earlier name (left by an older version) is an install too:
                // it still loads, and Install/Remove clear it.
                var modsDirectory = Path.Combine(_settingsService.Current.IcarusContentPath!, "Paks", "mods");
                SetHasExistingInstallIfCurrent(version, InstallManifestNames.AllMergedPakFileNames
                    .Prepend(Path.GetFileName(_outputPakPath))
                    .Any(name => File.Exists(Path.Combine(modsDirectory, name))));
            }
            catch (Exception)
            {
                // Best-effort — the button label just stays whatever it already was.
            }
        });
    }

    /// <summary>Bumped whenever Install, Remove or Restore sets HasExistingInstall itself.</summary>
    private int _existingInstallVersion;

    private void SetHasExistingInstallIfCurrent(int version, bool value)
    {
        if (version == _existingInstallVersion)
        {
            HasExistingInstall = value;
        }
    }

    /// <summary>What an install, Remove or Restore just did to the game's mods folder.</summary>
    private void SetHasExistingInstall(bool value)
    {
        _existingInstallVersion++;
        HasExistingInstall = value;
    }

    /// <summary>
    /// The staged pak AND its mod list — RebuildService writes the list only after a verified build,
    /// so a pak without one is a leftover of a failed build, not something to install (and its
    /// install would leave the Library's "In game" reading the previous build's list).
    /// </summary>
    private bool HasCompleteStagedBuild() =>
        File.Exists(_outputPakPath)
        && File.Exists(Path.Combine(Path.GetDirectoryName(_outputPakPath)!, InstallManifestNames.PakManifest));

    /// <summary>
    /// The mod names the installed merged pak's own manifest lists (ISL-Merged.txt), as last read —
    /// what the Library's Status column compares each enabled mod against ("In game" vs "Needs
    /// Merge &amp; Install"). Empty until RefreshInstalledModNamesAsync has run, or when no Content
    /// path is set. Raises PropertyChanged so the Library can recompute.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<string> _lastInstalledModNames = [];

    /// <summary>
    /// The ArcFission mods turned on in the game (WirelessPower…) and the rows they add, read from
    /// their own rows.lua (ArcFissionRows): merged in at each build, lowest priority. Null while
    /// ArcFission Library itself isn't installed and on (then those rows aren't in game either).
    /// Read straight from the mods rather than from the data pak the library writes at game start,
    /// so a build has them before the game has run, and never the pak's copies of the game's tables.
    /// </summary>
    internal ArcFissionScan? ScanArcFission()
    {
        if (_settingsService.Current.IcarusContentPath is not { Length: > 0 } content)
        {
            return null;
        }

        try
        {
            return ArcFissionRows.Scan(content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>ArcFission data paks in the game's mods folder while ArcFission Library isn't installed or is off: old data nothing writes again, offered for removal at Install.</summary>
    private static List<string> LeftoverArcFissionDataPaks(string contentPath)
    {
        var modsDirectory = Path.Combine(contentPath, "Paks", "mods");
        if (!Directory.Exists(modsDirectory) || InstallManifestNames.IsArcFissionLibraryActive(contentPath))
        {
            return [];
        }

        return [.. Directory.GetFiles(modsDirectory).Select(Path.GetFileName).OfType<string>().Where(InstallManifestNames.IsArcFissionDataPak)];
    }

    /// <summary>
    /// An ArcFission mod was added, removed or updated since the installed merge was built: the
    /// fingerprint of the rows the mods add now (ArcFissionScan.ManifestName) isn't the one in the
    /// merge's manifest, so their rows aren't in the game until the next Merge &amp; Install.
    /// </summary>
    [ObservableProperty]
    private bool _arcFissionNeedsMerge;

    /// <summary>
    /// Re-reads the installed merge's manifest (LastInstalledModNames) and whether ArcFission Library
    /// wrote newer data since (ArcFissionNeedsMerge). Runs when the Library subscribes, after
    /// Install/Remove, after Settings are saved, and when the app's window is activated again (the
    /// game may have run meanwhile and ArcFission written a new pak).
    /// </summary>
    public async Task RefreshInstalledModNamesAsync()
    {
        var contentPath = _settingsService.Current.IcarusContentPath;
        if (string.IsNullOrWhiteSpace(contentPath))
        {
            LastInstalledModNames = [];
            ArcFissionNeedsMerge = false;
            return;
        }

        try
        {
            var installed = await _installService.GetInstalledStateAsync(contentPath);
            // Only with a merge installed (its manifest lists mods): with none, there's nothing for
            // ArcFission's newer rows to be missing from.
            ArcFissionNeedsMerge = installed.ModNames.Count > 0
                && ScanArcFission() is { HasRows: true } arcFission
                && !installed.ModNames.Contains(arcFission.ManifestName, StringComparer.OrdinalIgnoreCase);
            LastInstalledModNames = installed.ModNames;
        }
        catch (Exception)
        {
            // A missing/unreadable manifest just means "nothing known to be installed".
            LastInstalledModNames = [];
            ArcFissionNeedsMerge = false;
        }

        await RefreshChangedSinceInstallAsync();
    }

    /// <summary>
    /// Mods in the installed merge whose Library copy changed since it was built (folder name →
    /// "v1.0 → v1.1" or "files changed"): updated in Starlink, re-imported, fixed, or edited
    /// outside it. Empty when the game's pak isn't one this app recorded a build of.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyDictionary<string, string> _changedSinceInstall = new Dictionary<string, string>();

    /// <summary>
    /// Re-works out ChangedSinceInstall: on every Library change, after installs, and when the
    /// window comes forward again (a mod may have been edited outside the app meanwhile).
    /// </summary>
    public async Task RefreshChangedSinceInstallAsync()
    {
        var contentPath = _settingsService.Current.IcarusContentPath;
        if (string.IsNullOrWhiteSpace(contentPath))
        {
            ChangedSinceInstall = new Dictionary<string, string>();
            return;
        }

        var installedPak = Path.Combine(contentPath, "Paks", "mods", Path.GetFileName(_outputPakPath));
        var recordPath = BuildRecordPath;
        var current = _libraryRepository.GetAll().ToDictionary(e => e.FolderName, StringComparer.OrdinalIgnoreCase);
        try
        {
            ChangedSinceInstall = await Task.Run(() =>
            {
                var changed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (MergedModFingerprints.ForInstalledPak(recordPath, installedPak) is not { } record)
                {
                    return changed;
                }

                foreach (var mod in record.Mods)
                {
                    // A mod gone from the Library is the queue's business (a missing row), not "changed".
                    if (!current.TryGetValue(mod.FolderName, out var entry)
                        || MergedModFingerprints.HashFolder(_libraryRepository.GetFolderPath(mod.FolderName)) is not { Length: > 0 } hash
                        || string.Equals(hash, mod.Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    changed[mod.FolderName] = string.Equals(entry.Version, mod.Version, StringComparison.OrdinalIgnoreCase)
                        ? "files changed"
                        : $"v{mod.Version.TrimStart('v', 'V')} → v{entry.Version.TrimStart('v', 'V')}";
                }

                return changed;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Can't tell: no hint rather than a wrong one.
            ChangedSinceInstall = new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// Big-plan item 8b: opens the pak-vs-pak comparison window (non-modal, like the editor's own
    /// research windows) — built for verifying this app's rebuilt pak against classic IMM's own
    /// installed merged pak during migration, but works for any two paks.
    /// </summary>
    [RelayCommand]
    private void ComparePaks()
    {
        var viewModel = new PakCompareViewModel(_pakCompareService, _settingsService, _outputPakPath);
        var window = new PakCompareWindow(viewModel) { Owner = Application.Current.MainWindow };
        window.Show();
    }

    /// <summary>
    /// "Installed vs this list" — reads what this app can positively identify as currently
    /// installed (its own pak manifest in the real game folder) and diffs it against the current
    /// queue plus any attached prebuilt paks (both now folded into the one merged pak by Rebuild,
    /// so both are listed in the same manifest), so a user can see exactly what Install would
    /// change before clicking it. Gameplay options aren't part of this comparison — they're not a
    /// "mod" with a name to diff, they overwrite specific fields regardless of what's currently
    /// there. UE4SS mods aren't part of this either (Phase 8.5) — see Library's own UE4SS tab,
    /// which shows their real state directly.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task CompareToInstalledAsync()
    {
        if (string.IsNullOrWhiteSpace(_settingsService.Current.IcarusContentPath))
        {
            ComparisonStatusMessage = "Set the Icarus Content folder in Settings first.";
            return;
        }

        try
        {
            var installed = await _installService.GetInstalledStateAsync(_settingsService.Current.IcarusContentPath!);

            // For an opaque entry, the name RebuildService's own WriteManifest actually records is
            // Path.GetFileNameWithoutExtension of its resolved .pak file, not its (possibly Nexus-
            // enriched) display Name — the comparison below is a literal string match against what
            // the real manifest on disk says, so opaque entries need that same derivation here.
            // An opaque entry can hold several paks (a bundle) — the manifest lists each one, so
            // the comparison expands the entry the same way.
            var currentList = Queue.Where(e => !e.IsMissingFromLibrary).SelectMany(entry =>
            {
                if (!entry.IsOpaquePak)
                {
                    return [entry.Name];
                }

                var pakPaths = Directory.GetFiles(_libraryRepository.GetFolderPath(entry.FolderName), "*.pak").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                return pakPaths.Count > 0 ? pakPaths.Select(Path.GetFileNameWithoutExtension).Select(n => n!) : [entry.Name];
            }).ToList();
            // Rebuild merges the ArcFission mods' rows in too, and the manifest lists them.
            if (ScanArcFission() is { HasRows: true } arcFission)
            {
                currentList.Insert(0, arcFission.ManifestName);
            }

            var comparison = InstalledVsListComparer.Compare(installed.ModNames, currentList);

            ComparisonEntries.Clear();
            foreach (var entry in comparison.OrderBy(e => e.State).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                var symbol = entry.State switch
                {
                    InstalledComparisonState.Added => "+",
                    InstalledComparisonState.Removed => "-",
                    _ => "=",
                };
                ComparisonEntries.Add($"{symbol}  {entry.Name}");
            }

            ComparisonStatusMessage = ComparisonEntries.Count == 0 ? "Nothing installed yet to compare against." : null;
        }
        catch (Exception ex)
        {
            ComparisonStatusMessage = $"Compare failed: {ex.Message}";
        }
    }

    /// <summary>Whether Icarus is running; a failing process query counts as not running.</summary>
    private bool IsGameRunning()
    {
        try
        {
            return _gameProcessChecker?.IsRunning() == true;
        }
        catch (Exception)
        {
            // A process query failing must never block an install on its own.
            return false;
        }
    }

    /// <summary>
    /// The game-running guard every action that writes the game's mods folder has (Install, Remove
    /// from game, Restore previous install): Icarus holds its paks open while it runs, so a pak
    /// replaced or deleted under it is how a user ends up with a half-replaced mod pack. True (and
    /// InstallStatusMessage says why) when the game is running.
    /// </summary>
    private bool RefuseWhileGameRunning(string message)
    {
        if (!IsGameRunning())
        {
            return false;
        }

        InstallStatusMessage = message;
        return true;
    }

    /// <summary>
    /// The confirmation before a mods-folder write, then the game-running check again: the question
    /// gives the game time to start. True to go ahead. On No, InstallStatusMessage becomes
    /// declinedMessage (left as it is when null).
    /// </summary>
    private bool ConfirmWithGameClosed(string question, string title, string retry, string? declinedMessage = null)
    {
        if (!Confirm(question, title))
        {
            if (declinedMessage is not null)
            {
                InstallStatusMessage = declinedMessage;
            }

            return false;
        }

        return !RefuseWhileGameRunning($"Icarus started while the confirmation was open — close it, then {retry}.");
    }

    /// <summary>How an install ended: in the game, not done (the user said No, the game started meanwhile, or another install or build was running), or failed. The reason is in InstallStatusMessage.</summary>
    internal enum InstallOutcome
    {
        Installed,
        NotDone,
        Failed,
    }

    /// <summary>
    /// While Install, Copy to game, Remove from game or Restore previous install is changing the
    /// game's mods folder (its confirmation included). Each of those, a build and Roll back refuse
    /// to start meanwhile: the build step used to clear IsRebuilding before the install that
    /// follows it, so Copy to game, Remove from game and Restore stayed clickable while the
    /// install's question was open and the copy ran (review 2026-10-23).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstallIdle))]
    private bool _isInstalling;

    /// <summary>Neither building nor installing: the mods-folder buttons are enabled.</summary>
    public bool IsInstallIdle => !IsRebuilding && !IsInstalling;

    /// <summary>
    /// The one method in this whole app that writes into the real game's Content\Paks\mods.
    /// No [RelayCommand] — reached through RebuildAndInstallAsync's chain (the Install button),
    /// Copy to game (the staged pak again, without rebuilding) and Update everything.
    /// </summary>
    internal async Task<InstallOutcome> InstallAsync()
    {
        if (RefuseInstallWhileBusy())
        {
            return InstallOutcome.NotDone;
        }

        IsInstalling = true;
        try
        {
            return await InstallCoreAsync();
        }
        finally
        {
            IsInstalling = false;
        }
    }

    private async Task<InstallOutcome> InstallCoreAsync()
    {
        if (string.IsNullOrWhiteSpace(_settingsService.Current.IcarusContentPath))
        {
            InstallStatusMessage = "Set the Icarus Content folder in Settings first.";
            return InstallOutcome.Failed;
        }

        if (!HasCompleteStagedBuild())
        {
            InstallStatusMessage = "Nothing built yet — click Install to build one.";
            return InstallOutcome.Failed;
        }

        // The same guard the saves editor and the UE4SS tab already have, and the one place it was
        // missing. Checked here and again right after the confirmation (ConfirmWithGameClosed).
        if (RefuseWhileGameRunning("Icarus is running — close it before installing, or the game keeps loading the old pack and the copy can fail halfway."))
        {
            return InstallOutcome.NotDone;
        }

        // Content\Paks\mods is meant to hold exactly one active merged pak — named here rather than
        // a bare "overwriting whatever's currently installed" claim, since that claim used to be
        // false: InstallService only ever touched its own two specifically-named files, silently
        // leaving anything else (a classic IMM merged pak, a stray leftover) loaded alongside the
        // new one. Matches the same "name every file before deleting it" transparency the FTP
        // "Install merged pak to server" action already established.
        var modsDirectory = Path.Combine(_settingsService.Current.IcarusContentPath!, "Paks", "mods");
        var targetPakFileName = Path.GetFileName(_outputPakPath);

        // ArcFission Library's data pak is kept (its rows are in the merge, and the library would
        // only write it again) — unless the library itself is gone or off: then nothing rewrites it,
        // and it holds whole tables from an older game, so removing it is offered.
        var leftoverArcFission = LeftoverArcFissionDataPaks(_settingsService.Current.IcarusContentPath!);
        var keepArcFission = leftoverArcFission.Count == 0 || !Confirm(
            $"ArcFission Library isn't installed (or is turned off), but its data pak is still in the mods folder:\n"
            + string.Join('\n', leftoverArcFission.Select(n => $"  - {n}"))
            + "\n\nNothing updates it any more: it holds whole game tables from the game version it was written for, so it can put old values back in game.\n\n"
            + "Back it up and remove it?",
            "Leftover ArcFission data");

        var otherFilesToClear = Directory.Exists(modsDirectory)
            ? Directory.GetFiles(modsDirectory)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(name => !string.Equals(name, targetPakFileName, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, InstallManifestNames.PakManifest, StringComparison.OrdinalIgnoreCase)
                    && !(keepArcFission && InstallManifestNames.IsArcFissionDataPak(name)))
                .ToList()
            : [];
        var clearNote = otherFilesToClear.Count > 0
            ? $"\n\nIt will also remove {otherFilesToClear.Count} other file(s) already there (backed up first, not part of this merge):\n"
                + string.Join('\n', otherFilesToClear.Select(n => $"  - {n}"))
            : "";

        // ArcFission's older data copies go too (InstallService keeps only the newest).
        var newestArcFission = Path.GetFileName(InstallManifestNames.CurrentArcFissionDataPak(_settingsService.Current.IcarusContentPath!));
        var oldArcFission = keepArcFission && Directory.Exists(modsDirectory)
            ? Directory.GetFiles(modsDirectory).Select(Path.GetFileName).OfType<string>()
                .Count(n => InstallManifestNames.IsArcFissionDataPak(n) && !string.Equals(n, newestArcFission, StringComparison.OrdinalIgnoreCase))
            : 0;
        if (oldArcFission > 0)
        {
            clearNote += $"\n\nIt also removes {oldArcFission} older cop{(oldArcFission == 1 ? "y" : "ies")} of ArcFission's data (not used; ArcFission Library only needs its newest).";
        }

        // Same explicit Yes/No gate as every other real-machine write this app makes (the nxm://
        // registry registration, the UE4SS loader install) — this one specifically overwrites the
        // user's actual installed mod pack, so it shouldn't ever be reachable via a single
        // accidental click.
        if (!ConfirmWithGameClosed(
            $"This copies the staged pak into '{_settingsService.Current.IcarusContentPath}\\Paks\\mods', overwriting whatever's currently installed there.\n\n" +
            $"The existing pak is backed up first (last {BackupCleanup.MergedPakBackupsKept} kept).{clearNote}\n\nContinue?",
            "Install to Icarus", retry: "Install again", declinedMessage: "Not installed: you chose not to."))
        {
            return InstallOutcome.NotDone;
        }

        InstallStatusMessage = "Installing…";

        try
        {
            using var perfScope = _performanceTracker.Track("Install");
            var result = await _installService.InstallAsync(
                _outputPakPath, _settingsService.Current.IcarusContentPath!, _backupDirectory, keepArcFissionDataPak: keepArcFission);

            // The build already put this pak in the Library; it's only imported here when that
            // didn't happen (it failed then, or the pak was built before the app started). It used
            // to be imported again on every install, filed under whichever profile was selected by
            // then and dropping the conflict picks on the way (review 2026-10-23). The pak is in
            // the game by now: a failure here (a locked Library folder) is a note, not "Install
            // failed" with the install left unrecorded.
            string? libraryNote = null;
            if (!_builtPackInLibrary)
            {
                try
                {
                    ImportBuiltPakKeepingPicks(_stagedBuildProfileName ?? BuiltPackProfileInLibrary() ?? SelectedProfileName ?? "Default");
                }
                catch (Exception ex)
                {
                    libraryNote = $" The built pack couldn't be added to your Library ({ex.Message}).";
                }
            }

            InstallStatusMessage = (result.BackupPakPath is not null
                ? $"Installed to '{result.InstalledPakPath}'. Backed up the previous pak to '{result.BackupPakPath}'."
                : $"Installed to '{result.InstalledPakPath}'.") + libraryNote;
            _activityLog.Log($"Installed pack to {_settingsService.Current.IcarusContentPath}\\Paks\\mods.", ActivityEntryKind.Success);
            SetHasExistingInstall(true);
            _ = RefreshInstalledModNamesAsync();

            // Read back on the NEXT app launch (SettingsViewModel) to check whether a real game
            // crash happened since — see AppSettings.LastInstallAtUtc's own doc comment for why.
            _settingsService.Current.LastInstallAtUtc = DateTimeOffset.UtcNow;
            _settingsService.Save();
            return InstallOutcome.Installed;
        }
        catch (IOException ex)
        {
            // Overwhelmingly the "Icarus is still running" case, which is worth naming rather than
            // showing a raw file-sharing error — and it's exactly when Copy to game (no rebuild)
            // is the right next click, so offer it.
            InstallStatusMessage =
                $"Install failed — the pak is in use: {ex.Message} "
                + "Close Icarus if it's running, then click Copy to game under Advanced actions (no need to rebuild).";
            _activityLog.Log(InstallStatusMessage, ActivityEntryKind.Warning);
            CanCopyToGame = true;
            return InstallOutcome.Failed;
        }
        catch (Exception ex)
        {
            // Same UI boundary as everywhere else — a missing/wrong Content path or a permissions
            // issue should show a status message, not crash the app.
            InstallStatusMessage = $"Install failed: {ex.Message}";
            _activityLog.Log(InstallStatusMessage, ActivityEntryKind.Warning);
            return InstallOutcome.Failed;
        }
    }

    /// <summary>
    /// Install, Copy to game, Remove from game and Restore previous install while a build or another
    /// of them runs: refused with a reason (true). A build is rewriting the staged pak, so Copy to
    /// game could put a half-written pak in the game, and the other two would be undone by the
    /// install that follows the build; two of them at once would back up and replace the same files.
    /// </summary>
    private bool RefuseInstallWhileBusy()
    {
        if (IsRebuilding)
        {
            InstallStatusMessage = "Merge & Install is building — wait for it to finish first.";
            return true;
        }

        if (IsInstalling)
        {
            InstallStatusMessage = "Merge & Install is already changing the game's mods folder — wait for it to finish first.";
            return true;
        }

        return false;
    }

    /// <summary>Runs a Remove from game / Restore previous install with IsInstalling set throughout, unless something else is building or installing.</summary>
    private async Task RunInstallStepAsync(Func<Task> step)
    {
        if (RefuseInstallWhileBusy())
        {
            return;
        }

        IsInstalling = true;
        try
        {
            await step();
        }
        finally
        {
            IsInstalling = false;
        }
    }

    /// <summary>Shown once an install has failed on a locked file, or whenever a staged pak already exists — the spec's own "Copy built pack to game" / Retry affordance.</summary>
    [ObservableProperty]
    private bool _canCopyToGame;

    /// <summary>
    /// The spec's "Copy built pack to game": installs the already-staged pak WITHOUT remerging.
    /// Mechanically the same as Install's second half — it shares InstallAsync rather than
    /// duplicating it (which also refuses while a build or another install runs) — but reachable
    /// on its own, which matters in the case it exists for: the game held the file open, nothing
    /// about the staged pak changed, and rebuilding it again to retry the copy is pure waste.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task CopyToGameAsync()
    {
        if (RefuseInstallWhileBusy())
        {
            return;
        }

        if (!HasCompleteStagedBuild())
        {
            InstallStatusMessage = "Nothing built yet — click Install to build one.";
            return;
        }

        await InstallAsync();
    }

    /// <summary>
    /// A standalone uninstall — deletes the real installed pak from Content\Paks\mods without
    /// Installing anything in its place. Deliberately leaves the Library entry Install's own
    /// "Replace, not accumulate" step created untouched: removing a mod from the live game isn't
    /// the same request as deleting it from the local library, so this only ever touches the real
    /// game folder.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private Task RemoveFromGameAsync() => RunInstallStepAsync(RemoveFromGameCoreAsync);

    private async Task RemoveFromGameCoreAsync()
    {
        if (string.IsNullOrWhiteSpace(_settingsService.Current.IcarusContentPath))
        {
            InstallStatusMessage = "Set the Icarus Content folder in Settings first.";
            return;
        }

        if (RefuseWhileGameRunning("Icarus is running — close it before removing the pack, or the delete can fail halfway."))
        {
            return;
        }

        // Same explicit Yes/No gate InstallAsync itself already uses — this deletes a real file
        // from the user's actual game folder, so it shouldn't be reachable via a single accidental
        // click any more than overwriting it already isn't.
        if (!ConfirmWithGameClosed(
            $"This removes IcarusStarlink's installed pak from '{_settingsService.Current.IcarusContentPath}\\Paks\\mods'.\n\n" +
            $"It's backed up first (last {BackupCleanup.MergedPakBackupsKept} kept), and your Library copy of this mod isn't touched — only the real game install.\n\nContinue?",
            "Remove from Icarus", retry: "Remove again"))
        {
            return;
        }

        InstallStatusMessage = "Removing…";

        try
        {
            var removed = await _installService.RemoveInstalledPakAsync(
                Path.GetFileName(_outputPakPath), _settingsService.Current.IcarusContentPath!, _backupDirectory);

            InstallStatusMessage = removed
                ? "Removed from Icarus — backed up first."
                : "Nothing installed by IcarusStarlink was there to remove.";
            if (removed)
            {
                _activityLog.Log($"Removed installed pack from {_settingsService.Current.IcarusContentPath}\\Paks\\mods.", ActivityEntryKind.Success);
                SetHasExistingInstall(false);
            }

            // The manifest is gone too, so the Library's "In game" status has to drop with it.
            await RefreshInstalledModNamesAsync();
        }
        catch (Exception ex)
        {
            InstallStatusMessage = $"Remove failed: {ex.Message}";
            _activityLog.Log(InstallStatusMessage, ActivityEntryKind.Warning);
        }
    }

    /// <summary>
    /// Puts the install before the current one back: the newest merged pak backup and the mod list
    /// backed up with it (Install and Remove back both up first). What's installed now is backed up
    /// first, so running this again undoes it.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private Task RestorePreviousInstallAsync() => RunInstallStepAsync(RestorePreviousInstallCoreAsync);

    private async Task RestorePreviousInstallCoreAsync()
    {
        if (string.IsNullOrWhiteSpace(_settingsService.Current.IcarusContentPath))
        {
            InstallStatusMessage = "Set the Icarus Content folder in Settings first.";
            return;
        }

        if (RefuseWhileGameRunning("Icarus is running — close it before restoring an earlier install."))
        {
            return;
        }

        try
        {
            if (await _installService.FindPreviousInstallAsync(_backupDirectory) is not { } previous)
            {
                InstallStatusMessage = "There's no earlier install backed up to restore.";
                return;
            }

            var listNote = previous.ManifestBackupPath is null
                ? "\n\nIts mod list wasn't backed up with it, so the Library won't show which mods are in the game until the next Merge & Install."
                : "";
            if (!ConfirmWithGameClosed(
                $"This puts back the merged pak that was installed before (backed up {previous.BackedUpAtUtc.ToLocalTime():g}), with its mod list.{listNote}\n\n"
                + "What's installed now is backed up first, so you can switch back the same way.\n\nContinue?",
                "Restore previous install", retry: "try again"))
            {
                return;
            }

            InstallStatusMessage = "Restoring…";
            var restored = await _installService.RestorePreviousInstallAsync(
                Path.GetFileName(_outputPakPath), _settingsService.Current.IcarusContentPath!, _backupDirectory);
            if (restored is null)
            {
                InstallStatusMessage = "There's no earlier install backed up to restore.";
                return;
            }

            InstallStatusMessage = $"Restored the install from {restored.BackedUpAtUtc.ToLocalTime():g}. What was installed is backed up. Your ticks stay as they are: the next Install builds from them again.";
            _activityLog.Log($"Restored the previous install ({Path.GetFileName(restored.PakBackupPath)}).", ActivityEntryKind.Success);
            SetHasExistingInstall(true);
            await RefreshInstalledModNamesAsync();
        }
        catch (Exception ex)
        {
            InstallStatusMessage = $"Restore failed: {ex.Message}";
            _activityLog.Log(InstallStatusMessage, ActivityEntryKind.Warning);
        }
    }
}
