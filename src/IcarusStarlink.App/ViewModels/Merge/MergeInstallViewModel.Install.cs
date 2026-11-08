using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.InstallComparison;
using IcarusStarlink.Core.Settings;
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

    // --- Installed vs this list ---
    /// <summary>"+ Name" (would be added), "- Name" (would be removed), "= Name" (unchanged) — a preview of what clicking Install would actually do, not run automatically since it needs a real read of the game folder.</summary>
    public ObservableCollection<string> ComparisonEntries { get; } = [];

    [ObservableProperty]
    private string? _comparisonStatusMessage;

    /// <summary>
    /// Checks for the real installed pak's own file directly, not GetInstalledStateAsync's parsed
    /// mod-name list — a gameplay-options-only install (an empty queue: merge options added to the
    /// game with no mods selected) still produces a real pak but lists zero mod
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
                SetHasExistingInstallIfCurrent(version, FindInstalledMergedPak(_settingsService.Current.IcarusContentPath!) is not null);
            }
            catch (Exception)
            {
                // Best-effort — the button label just stays whatever it already was.
            }
        });
    }

    /// <summary>
    /// The merged pak in the game's mods folder: under the name Install writes in this game now
    /// (InstalledMergedPakFileName), else under another name it has gone by (ArcFission Library
    /// turned on or off since, an older version's name), else under the staged name. Null when none
    /// is there. Reads the disk (ue4ss\Mods, Paks\mods): call it off the UI thread.
    /// </summary>
    private string? FindInstalledMergedPak(string contentPath)
    {
        var modsDirectory = Path.Combine(contentPath, "Paks", "mods");
        return InstallManifestNames.AllMergedPakFileNames
            .Prepend(InstallManifestNames.InstalledMergedPakFileName(contentPath))
            .Append(Path.GetFileName(_outputPakPath))
            .Select(name => Path.Combine(modsDirectory, name))
            .FirstOrDefault(File.Exists);
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
        // Several run at once (a Library change, an install, the window coming forward): only the
        // latest one's answer is kept, or one that read the pak before an install could finish last
        // and mark the mods just installed "Changed".
        var version = ++_changedSinceInstallVersion;
        var contentPath = _settingsService.Current.IcarusContentPath;
        if (string.IsNullOrWhiteSpace(contentPath))
        {
            ChangedSinceInstall = new Dictionary<string, string>();
            InstalledModFolderNames = [];
            return;
        }

        var recordPath = BuildRecordPath;
        var current = _libraryRepository.GetAll().ToDictionary(e => e.FolderName, StringComparer.OrdinalIgnoreCase);
        try
        {
            var (changedNow, builtFrom) = await Task.Run(() =>
            {
                var changed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                // Found inside the Task.Run: which name Install uses depends on ue4ss\Mods.
                if (FindInstalledMergedPak(contentPath) is not { } installedPak
                    || MergedModFingerprints.ForInstalledPak(recordPath, installedPak) is not { } record)
                {
                    return (changed, (IReadOnlyList<string>)[]);
                }

                foreach (var mod in record.Mods)
                {
                    // A mod gone from the Library is the queue's business (a missing row), not "changed".
                    if (!current.TryGetValue(mod.FolderName, out var entry)
                        || HashLibraryFolder(mod.FolderName) is not { Length: > 0 } hash
                        || string.Equals(hash, mod.Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    changed[mod.FolderName] = string.Equals(entry.Version, mod.Version, StringComparison.OrdinalIgnoreCase)
                        ? "files changed"
                        : $"v{mod.Version.TrimStart('v', 'V')} → v{entry.Version.TrimStart('v', 'V')}";
                }

                return (changed, (IReadOnlyList<string>)[.. record.Mods.Select(m => m.FolderName)]);
            });
            if (version == _changedSinceInstallVersion)
            {
                ChangedSinceInstall = changedNow;
                InstalledModFolderNames = builtFrom;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Can't tell: no hint rather than a wrong one.
            if (version == _changedSinceInstallVersion)
            {
                ChangedSinceInstall = new Dictionary<string, string>();
                InstalledModFolderNames = [];
            }
        }
    }

    /// <summary>A Library mod's files hashed; empty when its folder is gone (the Library hasn't caught up yet).</summary>
    private string HashLibraryFolder(string folderName)
    {
        try
        {
            return MergedModFingerprints.HashFolder(_libraryRepository.GetFolderPath(folderName));
        }
        catch (DirectoryNotFoundException)
        {
            return "";
        }
    }

    /// <summary>
    /// The Library folders the installed merged pak was built from, in merge order, when Starlink
    /// recorded that build (ISL-Merged.mods.json); empty for a pak built elsewhere or none
    /// installed. A renamed mod, whose name is no longer the one ISL-Merged.txt lists, still reads
    /// as in the game.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<string> _installedModFolderNames = [];

    /// <summary>Guards RefreshChangedSinceInstallAsync like _checkVersion guards RecomputeChecksAsync.</summary>
    private int _changedSinceInstallVersion;

    /// <summary>
    /// Opens the pak comparison window (non-modal, like the editor's research windows): checks
    /// Starlink's built pak against another manager's merged pak, or any two paks.
    /// </summary>
    [RelayCommand]
    private void ComparePaks()
    {
        var viewModel = new PakCompareViewModel(_pakCompareService, _settingsService, _outputPakPath, _dialogs);
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
    /// there. UE4SS mods aren't part of this either — see Library's own UE4SS tab,
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

            // Each mod named the way RebuildService's WriteManifest names it, since the comparison
            // is a literal match against that list: an EXMOD by its package name (a mod renamed in
            // the Library keeps it), an opaque entry by the file name of each .pak it holds (a
            // bundle lists each one). A row whose folder is gone is left out, as a build leaves it.
            var queue = Queue.Where(IsBuildable).ToList();
            var loaded = await LoadQueuedPackagesAsync(queue);
            var currentList = queue.Where(entry => !loaded.SkippedMissing.Contains(entry)).SelectMany(entry =>
            {
                if (!entry.IsOpaquePak)
                {
                    return [loaded.Packages[loaded.Entries.IndexOf(entry)].Package.Name];
                }

                var pakNames = loaded.PrebuiltPakFilePaths.Where((_, i) => ReferenceEquals(loaded.PrebuiltPakEntries[i], entry))
                    .Select(p => Path.GetFileNameWithoutExtension(p)).ToList();
                return pakNames.Count > 0 ? pakNames : (IEnumerable<string>)[entry.Name];
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

    /// <summary>The same check, for the Library's UE4SS Apply and Delete (the Library has no process checker of its own).</summary>
    internal bool IsGameRunningNow() => IsGameRunning();

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
    /// install's question was open and the copy ran.
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
        CanInstallWhenGameCloses = false;
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
        // missing. Checked here and again right after the confirmation (ConfirmWithGameClosed). A
        // complete staged build exists at this point, so Copy to game installs it without building
        // again; a Find the broken mod round is retried from its own window instead.
        var forFinder = _finderOnly is not null || _finderRestoring;
        if (RefuseWhileGameRunning("Icarus is running — close it before installing, or the game keeps loading the old pack and the copy can fail halfway."
                + (forFinder ? "" : " Then click Copy to game under Advanced actions (no need to build again).")))
        {
            if (!forFinder)
            {
                OfferInstallWhenGameCloses();
            }

            return InstallOutcome.NotDone;
        }

        // Content\Paks\mods is meant to hold exactly one active merged pak — named here rather than
        // a bare "overwriting whatever's currently installed" claim, since that claim used to be
        // false: InstallService only ever touched its own two specifically-named files, silently
        // leaving anything else (a classic IMM merged pak, a stray leftover) loaded alongside the
        // new one. Matches the same "name every file before deleting it" transparency the FTP
        // "Install merged pak to server" action already established.
        var modsDirectory = Path.Combine(_settingsService.Current.IcarusContentPath!, "Paks", "mods");
        // The name InstallService writes the merge under in this game (its own rule), not the
        // staged name: the staged name is zz_zz_…_9999 only because a build can't know the game.
        var targetPakFileName = InstallManifestNames.InstalledMergedPakFileName(_settingsService.Current.IcarusContentPath!);

        // ArcFission Library's data pak is kept (its rows are in the merge, and the library would
        // only write it again) — unless the library itself is gone or off: then nothing rewrites it,
        // and it holds whole tables from an older game, so removing it is offered.
        var leftoverArcFission = LeftoverArcFissionDataPaks(_settingsService.Current.IcarusContentPath!);
        // A Find the broken mod round leaves it be: that's a question for a normal install.
        var keepArcFission = leftoverArcFission.Count == 0 || forFinder || !Confirm(
            $"ArcFission Library isn't installed (or is turned off), but its data pak is still in the mods folder:\n"
            + string.Join('\n', leftoverArcFission.Select(n => $"  - {n}"))
            + "\n\nNothing updates it any more: it holds whole game tables from the game version it was written for, so it can put old values back in game.\n\n"
            + "Back it up and remove it?",
            "Leftover ArcFission data");

        var filesThere = Directory.Exists(modsDirectory)
            ? Directory.GetFiles(modsDirectory)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(name => !string.Equals(name, targetPakFileName, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, InstallManifestNames.PakManifest, StringComparison.OrdinalIgnoreCase)
                    && !(keepArcFission && InstallManifestNames.IsArcFissionDataPak(name)))
                .ToList()
            : [];
        // Starlink's own merged pak under another of its names (ArcFission Library turned on or
        // off since, an older version) is replaced, not "another file": a Find the broken mod
        // round doesn't ask about it either.
        var ownOtherNames = filesThere.Where(InstallManifestNames.IsMergedPakFile).ToList();
        var otherFilesToClear = filesThere.Except(ownOtherNames).ToList();
        var clearNote = otherFilesToClear.Count > 0
            ? $"\n\nIt will also remove {otherFilesToClear.Count} other file(s) already there (backed up first, not part of this merge):\n"
                + string.Join('\n', otherFilesToClear.Select(n => $"  - {n}"))
            : "";
        if (ownOtherNames.Count > 0)
        {
            // InstallService backs up every file other than its own target, even in a finder round.
            clearNote += $"\n\nIt also replaces Starlink's merged pak saved under {(ownOtherNames.Count == 1 ? "an earlier name" : "earlier names")} ({string.Join(", ", ownOtherNames)}), backed up first.";
        }

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

        // Copy to game after a profile switch: the staged pak is still the other profile's build.
        if (_stagedBuildProfileName is { } builtFrom
            && !string.Equals(builtFrom, SelectedProfileName ?? "Default", StringComparison.OrdinalIgnoreCase))
        {
            clearNote += $"\n\nThis pak was built from profile '{builtFrom}' (you're on '{SelectedProfileName ?? "Default"}' now).";
        }

        // Same explicit Yes/No gate as every other real-machine write this app makes (the nxm://
        // registry registration, the UE4SS loader install) — this one specifically overwrites the
        // user's actual installed mod pack, so it shouldn't ever be reachable via a single
        // accidental click. A Find the broken mod round (or putting the mods back after one) was
        // asked for in its own window, which said what it installs: asked again only when the
        // install would also remove other files (FinderSkipsInstallQuestion).
        if (FinderSkipsInstallQuestion(otherFilesToClear.Count, oldArcFission))
        {
            if (RefuseWhileGameRunning("Icarus is running — close it before installing, or the game keeps loading the old pack and the copy can fail halfway."))
            {
                return InstallOutcome.NotDone;
            }
        }
        else if (!ConfirmWithGameClosed(
            $"This copies the staged pak into '{_settingsService.Current.IcarusContentPath}\\Paks\\mods', overwriting whatever's currently installed there.\n\n" +
            (_finderBacksUp
                ? $"The existing pak is backed up first (last {BackupLimits.MergedPakKept(_settingsService)} kept).{clearNote}\n\nContinue?"
                : $"The pak there now is an earlier Find the broken mod round's, so it isn't backed up (your own install was backed up by the first round).{clearNote}\n\nContinue?"),
            "Install to Icarus", retry: "Install again", declinedMessage: "Not installed: you chose not to."))
        {
            return InstallOutcome.NotDone;
        }

        InstallStatusMessage = "Installing…";

        try
        {
            using var perfScope = _performanceTracker.Track("Install");
            var result = await _installService.InstallAsync(
                _outputPakPath, _settingsService.Current.IcarusContentPath!, _backupDirectory, keepArcFissionDataPak: keepArcFission,
                backUpWhatsThere: _finderBacksUp);

            // The build already put this pak in the Library; it's only imported here when that
            // didn't happen (it failed then, or the pak was built before the app started and the
            // Library's copy isn't this one). It used to be imported again on every install, filed
            // under whichever profile was selected by then. A Find the broken mod round's pak never
            // goes in. The pak is in the game by now: a failure here (a locked Library folder) is a
            // note, not "Install failed" with the install left unrecorded.
            string? libraryNote = null;
            var isPlayersBuild = !_stagedIsFinderRound && _finderOnly is null;
            if (isPlayersBuild && !_builtPackInLibrary && LibraryHasThisBuild())
            {
                _builtPackInLibrary = true;
            }

            if (isPlayersBuild && !_builtPackInLibrary)
            {
                try
                {
                    await ImportBuiltPakIntoLibraryAsync(_stagedBuildProfileName ?? BuiltPackProfileInLibrary() ?? SelectedProfileName ?? "Default");
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
            // A file error while copying: a full drive, a game folder that's gone, a file another
            // program holds, or the staged pak itself gone. Said by what it is (the install already
            // refuses while the game runs), with Copy to game offered while the staged pak is there.
            InstallStatusMessage = DescribeInstallFileError(ex, forFinder);
            _activityLog.Log(InstallStatusMessage, ActivityEntryKind.Warning);
            CanCopyToGame = HasCompleteStagedBuild();
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
    /// What an install that hit a file error tells the player, by what went wrong: the install
    /// already refuses while the game runs, so a file error is often a full drive or a game folder
    /// that's gone, not the game holding the pak. A Find the broken mod round never points at Copy
    /// to game: that would install the round's pak outside the search.
    /// </summary>
    private string DescribeInstallFileError(IOException ex, bool forFinder)
    {
        var copyHint = forFinder ? "" : " Then click Copy to game under Advanced actions (no need to build again).";
        return ex switch
        {
            FileNotFoundException => "Install failed: the built pak isn't there any more. Click Install to build it again.",
            DirectoryNotFoundException => $"Install failed: the game's mods folder couldn't be reached ({ex.Message}). Check the Icarus Content folder in Settings (is its drive plugged in?).{copyHint}",
            _ when (ex.HResult & 0xFFFF) is 32 or 33 => $"Install failed: a file in the game's mods folder is in use ({ex.Message}). Close Icarus if it's running.{copyHint}",
            _ when (ex.HResult & 0xFFFF) is 112 or 39 => $"Install failed: a drive is full ({ex.Message}). {FreeSpaceAdvice()}{copyHint}",
            _ => $"Install failed: {ex.Message}{copyHint}",
        };
    }

    /// <summary>How much room the install needs on the game's drive: the merged pak's size, when it can be read.</summary>
    private string FreeSpaceAdvice()
    {
        try
        {
            var megabytes = Math.Max(1, (long)Math.Ceiling(new FileInfo(_outputPakPath).Length / 1048576.0));
            return $"Free at least {megabytes} MB on the game's drive (the size of the merged pak).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Free some space on the game's drive.";
        }
    }

    /// <summary>
    /// Why the staged pak may hold game tables older than the game's (Icarus updated since it was
    /// built, or the game data refreshed after it); null when it doesn't. Copy to game checks this,
    /// since unlike Install it doesn't build first.
    /// </summary>
    private async Task<string?> StagedBuildPredatesGameDataAsync()
    {
        if (_gameDataFreshness is not null)
        {
            try
            {
                if (await _gameDataFreshness.IsGameDataOutdatedAsync())
                {
                    return "Not installed: Icarus was updated after this pak was built, so it has the old game tables. Click Install to build it again (it refreshes the game data first).";
                }
            }
            catch (Exception)
            {
                // Can't tell: not a reason to refuse (EnsureGameDataCurrentAsync's rule).
            }
        }

        return _settingsService.Current.LastDataFolderUpdatedAt is { } refreshedAt && File.GetLastWriteTimeUtc(_outputPakPath) < refreshedAt.UtcDateTime
            ? "Not installed: the game data was refreshed after this pak was built, so it may have old game tables. Click Install to build it again."
            : null;
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

    /// <summary>Shown once an install has failed on a locked file, or whenever a staged pak already exists — the "Copy built pack to game" / Retry affordance.</summary>
    [ObservableProperty]
    private bool _canCopyToGame;

    /// <summary>
    /// "Copy built pack to game": installs the already-staged pak WITHOUT remerging.
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

        // The build refuses old game data (EnsureGameDataCurrentAsync); this skips the build, so it checks too.
        if (await StagedBuildPredatesGameDataAsync() is { } why)
        {
            InstallStatusMessage = why;
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
    private Task RemoveFromGameAsync() => RunInstallStepAsync(() => RemoveFromGameCoreAsync());

    /// <param name="questionIntro">Put before the question (Update install with nothing ticked says why it removes).</param>
    private async Task RemoveFromGameCoreAsync(string questionIntro = "")
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
            questionIntro + $"This removes Icarus Starlink's installed pak from '{_settingsService.Current.IcarusContentPath}\\Paks\\mods'.\n\n" +
            $"It's backed up first (last {BackupLimits.MergedPakKept(_settingsService)} kept), and your Library copy of this mod isn't touched — only the real game install.\n\nContinue?",
            "Remove from Icarus", retry: "Remove again"))
        {
            return;
        }

        InstallStatusMessage = "Removing…";

        try
        {
            var removed = await _installService.RemoveInstalledPakAsync(
                _settingsService.Current.IcarusContentPath!, _backupDirectory);

            InstallStatusMessage = removed
                ? "Removed from Icarus — backed up first."
                : "Nothing installed by Icarus Starlink was there to remove.";
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
                _settingsService.Current.IcarusContentPath!, _backupDirectory);
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
