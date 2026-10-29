using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Profiles;
using IcarusStarlink.PakIO;

namespace IcarusStarlink.App.ViewModels;

/// <summary>Profiles: the profile bar's commands, loading a profile into the page, and the auto-save that keeps the queue, options and winners in the profile in use.</summary>
public sealed partial class MergeInstallViewModel
{
    // --- Profile bar (6.3) ---
    public ObservableCollection<string> ProfileNames { get; } = [];

    [ObservableProperty]
    private string? _selectedProfileName;

    [ObservableProperty]
    private string? _profileStatusMessage;

    private DebounceTimer? _workingStateSaveTimer;
    private bool _workingStateReady;
    private bool _loadingProfile;
    private string? _unsavedProfileName;

    /// <summary>
    /// The queue and options save themselves into the profile in use (or "Default" when none is
    /// picked yet) a moment after each change, so ticking mods, reordering and options survive a
    /// restart or an update without Save profile. Save profile still makes a backup to go back to.
    /// </summary>
    private void MarkWorkingStateChanged()
    {
        if (!_workingStateReady || _loadingProfile)
        {
            return;
        }

        _unsavedProfileName = SelectedProfileName ?? "Default";
        _workingStateSaveTimer?.Restart();
    }

    /// <summary>Writes any pending change now (before switching profile, and from the timer).</summary>
    internal void SaveWorkingState()
    {
        _workingStateSaveTimer?.Cancel();
        if (_unsavedProfileName is not { } name)
        {
            return;
        }

        _unsavedProfileName = null;
        try
        {
            _profileStore.SaveWithoutBackup(CurrentWorkingProfile(name));

            if (SelectedProfileName is null)
            {
                // The first change ever: "Default" now exists, so show it as the profile in use.
                _loadingProfile = true;
                try
                {
                    ReloadProfileNames();
                    SelectedProfileName = name;
                }
                finally
                {
                    _loadingProfile = false;
                }
            }
        }
        catch (Exception)
        {
            // Best-effort: Save profile still works and says why if the folder can't be written.
        }
    }

    /// <summary>
    /// The queue (greyed missing rows included, so the profile keeps their names), options and
    /// conflict winners as they are now, as the profile named — by default the one in use (or
    /// "Default"), which is what Update everything's snapshot keeps. Every save of the page's state
    /// builds its profile here.
    /// </summary>
    internal Profile CurrentWorkingProfile(string? name = null) => new()
    {
        Name = name ?? SelectedProfileName ?? "Default",
        MergeQueueFolderNames = [.. Queue.Select(e => e.FolderName)],
        Options = BuildGameplayOptionsFromUi(),
        ConflictWinners = new(_winners),
    };

    /// <summary>
    /// Roll back after a game update: the profile as the snapshot saved it (queue order, options,
    /// conflict winners) is saved again (its version from now backed up first, as Save profile does)
    /// and loaded. False while a build runs.
    /// </summary>
    internal bool RestoreWorkingState(Profile profile)
    {
        if (RefuseWhileRebuilding())
        {
            return false;
        }

        // Anything changed but not yet saved goes into the profile in use before it's replaced.
        SaveWorkingState();
        _profileStore.Save(profile);
        if (string.Equals(SelectedProfileName, profile.Name, StringComparison.OrdinalIgnoreCase))
        {
            _loadingProfile = true;
            try
            {
                LoadProfileIntoUi(profile.Name, verb: "Rolled back");
            }
            finally
            {
                _loadingProfile = false;
            }
        }
        else
        {
            ReloadProfileNames();
            SelectedProfileName = ProfileNames.FirstOrDefault(n => string.Equals(n, profile.Name, StringComparison.OrdinalIgnoreCase)) ?? profile.Name;
        }

        _activityLog.Log($"Put profile '{profile.Name}' back as it was before the game update run.", ActivityEntryKind.Success);
        return true;
    }

    private void ReloadProfileNames()
    {
        ProfileNames.Clear();
        foreach (var name in _profileStore.ProfileNames)
        {
            ProfileNames.Add(name);
        }
    }

    /// <summary>Selecting a profile replaces the current queue with that profile's own saved one — a profile *is* "a saved merge list" per the spec, not something merged alongside the current queue.</summary>
    partial void OnSelectedProfileNameChanging(string? value)
    {
        // Whatever was changed in the profile being left goes into that profile, not the next.
        if (!_loadingProfile)
        {
            SaveWorkingState();
        }
    }

    partial void OnSelectedProfileNameChanged(string? value)
    {
        RefreshCanRestoreProfileBackup();
        if (value is null || _loadingProfile)
        {
            return;
        }

        try
        {
            _settingsService.Current.LastMergeProfileName = value;
            _settingsService.Save();
        }
        catch (Exception)
        {
            // Only a convenience for the next start.
        }

        _loadingProfile = true;
        try
        {
            LoadProfileIntoUi(value, verb: "Loaded");
        }
        finally
        {
            _loadingProfile = false;
        }
    }

    /// <summary>
    /// Reads a profile's own file fresh from disk and populates Queue/options from it — shared by
    /// the ComboBox selection handler above and RestoreProfileBackup below, which needs the exact
    /// same "re-read what's now on disk" step after replacing the file with a backup.
    /// </summary>
    private void LoadProfileIntoUi(string name, string verb)
    {
        try
        {
            var profile = _profileStore.Load(name);
            LoadGameplayOptionsIntoUi(profile.Options);
            var (winners, prunedWinners) = MigrateWinners(profile.ConflictWinners ?? []);
            _winners = winners;
            OnPropertyChanged(nameof(SavedWinnerCount));

            Queue.Clear();
            var missingCount = 0;
            var islPacks = new List<string>();
            var byFolder = LibraryByFolder();
            foreach (var folderName in profile.MergeQueueFolderNames)
            {
                if (!byFolder.TryGetValue(folderName, out var entry))
                {
                    // Kept as a greyed row, so the profile keeps the name (the next auto-save writes
                    // it back) and the mod takes its place again once it's back in the Library.
                    Queue.Add(MissingEntry(folderName));
                    missingCount++;
                }
                else if (IsIslBuiltPack(entry))
                {
                    islPacks.Add(entry.Name);
                }
                else
                {
                    Queue.Add(entry);
                }
            }

            var notes = new List<string>();
            if (missingCount > 0)
            {
                notes.Add($"{missingCount} mod(s) from this profile are no longer in your Library (shown greyed, left out of the build until they're back)");
            }

            if (islPacks.Count > 0)
            {
                notes.Add($"left out {string.Join(", ", islPacks)}: a pack Merge & Install built, which can't be merged into a new one");
            }

            if (prunedWinners > 0)
            {
                notes.Add($"{prunedWinners} saved conflict pick(s) for mods no longer in your Library were removed");
            }

            ProfileStatusMessage = notes.Count > 0 ? $"{verb} '{name}' — {string.Join("; ", notes)}." : $"{verb} '{name}'.";
            // Loads follow a Save (Roll back, Import patch) or use a backup up (Restore backup).
            RefreshCanRestoreProfileBackup();
        }
        catch (Exception ex)
        {
            // Same UI boundary as everywhere else: a corrupt/unreadable profile file shows a
            // status message rather than crashing the app out of a ComboBox selection change.
            ProfileStatusMessage = $"Couldn't load profile: {ex.Message}";
        }
    }

    private Dictionary<string, LibraryEntry> LibraryByFolder() =>
        _libraryRepository.GetAll()
            .GroupBy(e => e.FolderName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    /// <summary>The greyed queue row for a profile's mod that isn't in the Library.</summary>
    private static LibraryEntry MissingEntry(string folderName) => new()
    {
        FolderName = folderName,
        Name = folderName,
        Author = "",
        Version = "",
        Description = "",
        FileName = folderName,
        IsMissingFromLibrary = true,
    };

    /// <summary>
    /// Saved winners moved to ids (see _winners): a winner already an id is kept, one saved by display
    /// name becomes the folder name of the one Library mod (or prebuilt pak) with that name. A name
    /// several mods share is kept as it is (it still works by name while that's unambiguous in the
    /// queue); a winner that matches nothing in the Library any more is dropped and counted. With an
    /// empty Library (not loaded yet) nothing is dropped.
    /// </summary>
    private (Dictionary<string, string> Winners, int Pruned) MigrateWinners(IReadOnlyDictionary<string, string> saved)
    {
        var library = _libraryRepository.GetAll();
        var migrated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pruned = 0;
        foreach (var (key, winner) in saved)
        {
            if (library.Count == 0 || winner is OptionsWinnerId or ArcFissionWinnerId)
            {
                migrated[key] = winner;
                continue;
            }

            if (InstallManifestNames.IsArcFissionDataPak(winner + ".pak"))
            {
                migrated[key] = ArcFissionWinnerId;
                continue;
            }

            var slash = winner.IndexOf('/');
            if (library.Any(e => !e.IsOpaquePak && string.Equals(e.FolderName, winner, StringComparison.OrdinalIgnoreCase))
                || (slash > 0 && library.Any(e => e.IsOpaquePak && string.Equals(e.FolderName, winner[..slash], StringComparison.OrdinalIgnoreCase))))
            {
                migrated[key] = winner;
                continue;
            }

            var byName = library.Where(e => !e.IsOpaquePak && string.Equals(e.Name, winner, StringComparison.Ordinal)).ToList();
            var pakOwners = library.Where(e => e.IsOpaquePak
                && e.PakFileNames.Any(p => string.Equals(Path.GetFileNameWithoutExtension(p), winner, StringComparison.OrdinalIgnoreCase))).ToList();
            if (byName.Count == 1 && pakOwners.Count == 0)
            {
                migrated[key] = byName[0].FolderName;
            }
            else if (pakOwners.Count == 1 && byName.Count == 0)
            {
                migrated[key] = $"{pakOwners[0].FolderName}/{winner}";
            }
            else if (byName.Count + pakOwners.Count > 1)
            {
                migrated[key] = winner;
            }
            else
            {
                pruned++;
            }
        }

        return (migrated, pruned);
    }

    /// <summary>Replaces each greyed missing row whose mod is in the Library again with the real entry, in place.</summary>
    private void ResolveMissingQueueEntries()
    {
        if (!Queue.Any(e => e.IsMissingFromLibrary))
        {
            return;
        }

        var byFolder = LibraryByFolder();
        for (var i = 0; i < Queue.Count; i++)
        {
            if (Queue[i].IsMissingFromLibrary && byFolder.TryGetValue(Queue[i].FolderName, out var entry) && !IsIslBuiltPack(entry))
            {
                Queue[i] = entry;
            }
        }
    }

    /// <summary>Asks for a profile name (title, starting text) → the name, or null when cancelled. Tests swap in an answer.</summary>
    internal Func<string, string, string?> PromptForProfileName { get; set; } = (title, current) =>
    {
        var dialog = new Views.RenameModDialog(current, "A name for this saved set of queued mods and merge options.", title: title, fieldLabel: "Profile name", showReset: false)
        {
            Owner = Application.Current?.MainWindow,
        };
        return dialog.ShowDialog() == true ? dialog.NewDisplayName : null;
    };

    /// <summary>Captures whatever's currently in the queue under a new profile name — lets a user build a queue first, then decide to save it, rather than New always starting empty.</summary>
    [RelayCommand]
    private void NewProfile()
    {
        if (RefuseWhileRebuilding())
        {
            return;
        }

        // Asked every time: a name kept from a refused try (it already existed) used to skip the
        // question on every later click, so New profile failed the same way until a restart.
        var name = PromptForProfileName("New profile", "")?.Trim();
        if (name is null)
        {
            return;
        }

        if (name.Length == 0)
        {
            ProfileStatusMessage = "Give the profile a name.";
            return;
        }

        if (ProfileNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            ProfileStatusMessage = $"A profile named '{name}' already exists.";
            return;
        }

        try
        {
            _profileStore.Save(CurrentWorkingProfile(name));
            ReloadProfileNames();
            SelectedProfileName = name;
            ProfileStatusMessage = $"Created '{name}'.";
            _activityLog.Log($"Created profile '{name}'.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            ProfileStatusMessage = $"Couldn't create profile: {ex.Message}";
        }
    }

    [RelayCommand]
    private void SaveProfile()
    {
        if (RefuseWhileRebuilding())
        {
            return;
        }

        if (SelectedProfileName is not { } name)
        {
            ProfileStatusMessage = "Select or create a profile first.";
            return;
        }

        try
        {
            _profileStore.Save(CurrentWorkingProfile(name));
            ProfileStatusMessage = $"Saved '{name}'.";
            _activityLog.Log($"Saved profile '{name}'.", ActivityEntryKind.Success);
            RefreshCanRestoreProfileBackup();
        }
        catch (Exception ex)
        {
            ProfileStatusMessage = $"Couldn't save profile: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RenameProfile()
    {
        if (RefuseWhileRebuilding())
        {
            return;
        }

        if (SelectedProfileName is not { } oldName)
        {
            ProfileStatusMessage = "Select a profile first.";
            return;
        }

        if (PromptForProfileName("Rename profile", oldName)?.Trim() is not { } newName)
        {
            return;
        }

        if (newName.Length == 0 || string.Equals(newName, oldName, StringComparison.Ordinal))
        {
            ProfileStatusMessage = "Give the profile a new name.";
            return;
        }

        if (ProfileNames.Contains(newName, StringComparer.OrdinalIgnoreCase))
        {
            ProfileStatusMessage = $"A profile named '{newName}' already exists.";
            return;
        }

        try
        {
            // A pending auto-save would otherwise write the old name back as a second profile.
            SaveWorkingState();
            _profileStore.Rename(oldName, newName);
            ReloadProfileNames();
            // Backing field, not the property: the property setter's change hook reloads the profile
            // from disk into the UI, which threw away any queue/option edits not yet saved (an audit
            // finding) — a rename changes only the name.
            _selectedProfileName = newName;
            OnPropertyChanged(nameof(SelectedProfileName));
            RefreshCanRestoreProfileBackup();
            // The change hook is skipped above, so the next start's profile is remembered here.
            try
            {
                _settingsService.Current.LastMergeProfileName = newName;
                _settingsService.Save();
            }
            catch (Exception)
            {
                // Only a convenience for the next start.
            }

            ProfileStatusMessage = $"Renamed '{oldName}' to '{newName}'.";
        }
        catch (Exception ex)
        {
            ProfileStatusMessage = $"Couldn't rename profile: {ex.Message}";
        }
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (RefuseWhileRebuilding())
        {
            return;
        }

        if (SelectedProfileName is not { } name)
        {
            ProfileStatusMessage = "Select a profile first.";
            return;
        }

        var fallback = !string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase) && _profileStore.ProfileNames.Contains("Default", StringComparer.OrdinalIgnoreCase)
            ? "the 'Default' profile's list"
            : "an empty list";
        if (!_dialogs.Confirm(
                $"Delete profile '{name}'?\n\nYour mods stay in the Library, but the ticks switch to {fallback}. This can't be undone.",
                "Delete profile", ThemedConfirmSeverity.Warning))
        {
            return;
        }

        try
        {
            // A pending auto-save of this profile must not write it back once it's deleted (or, with
            // no profile selected, into "Default").
            _workingStateSaveTimer?.Cancel();
            _unsavedProfileName = null;

            _profileStore.Delete(name);
            ReloadProfileNames();

            if (!string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase)
                && ProfileNames.FirstOrDefault(n => string.Equals(n, "Default", StringComparison.OrdinalIgnoreCase)) is { } defaultName)
            {
                // Loads "Default" as saved, replacing the deleted profile's queue and options.
                SelectedProfileName = defaultName;
            }
            else
            {
                // Nothing to fall back to: start empty, without saving the deleted profile's queue,
                // options or picks anywhere (the next change saves into a fresh "Default").
                _loadingProfile = true;
                try
                {
                    SelectedProfileName = null;
                    LoadGameplayOptionsIntoUi(new GameplayOptions());
                    _winners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    OnPropertyChanged(nameof(SavedWinnerCount));
                    Queue.Clear();
                }
                finally
                {
                    _loadingProfile = false;
                }

                _workingStateSaveTimer?.Cancel();
                _unsavedProfileName = null;
            }

            ProfileStatusMessage = $"Deleted '{name}'.";
        }
        catch (Exception ex)
        {
            ProfileStatusMessage = $"Couldn't delete profile: {ex.Message}";
        }
    }

    /// <summary>
    /// Whether the currently-selected profile has a backup to restore — drives the Restore backup
    /// button's own IsEnabled, mirroring how Library's "Restore latest backup" only enables once
    /// HasModBackup is true. Kept, not asked of the store from the bound getter: HasBackup reads
    /// the profile's backups from disk, and WPF reads a bound getter far more often than it changes.
    /// </summary>
    [ObservableProperty]
    private bool _canRestoreProfileBackup;

    /// <summary>Asks the store again: after the selection changes and after anything that can make or use up a backup.</summary>
    private void RefreshCanRestoreProfileBackup()
    {
        try
        {
            CanRestoreProfileBackup = SelectedProfileName is { } name && _profileStore.HasBackup(name);
        }
        catch (Exception)
        {
            CanRestoreProfileBackup = false;
        }
    }

    /// <summary>
    /// Replaces the selected profile's file with its own most recent backup — the mirror of
    /// Library's "Restore latest backup" for a Profile, since ProfileStore.Save already backs up
    /// whatever was there before overwriting it (a bad edit, an accidental Clear + Save, or
    /// MergeInstallViewModel's own auto-save of an unnamed queue into "Default" right before every
    /// Rebuild — see RebuildAsync's own comment on that). Re-loads the queue/options from the
    /// restored content immediately, the same way selecting the profile fresh would.
    /// </summary>
    [RelayCommand]
    private void RestoreProfileBackup()
    {
        if (RefuseWhileRebuilding())
        {
            return;
        }

        if (SelectedProfileName is not { } name)
        {
            ProfileStatusMessage = "Select a profile first.";
            return;
        }

        // The list as it is now isn't kept (backing it up would make it the backup to restore).
        if (!_dialogs.Confirm(
                $"Replace the list and merge options of '{name}' with its last backup?\n\nWhat's ticked now is replaced, and the Library's ticks follow.",
                "Restore profile backup", ThemedConfirmSeverity.Warning))
        {
            return;
        }

        try
        {
            if (!_profileStore.RestoreLatestBackup(name))
            {
                ProfileStatusMessage = $"'{name}' has no backup to restore.";
                return;
            }

            LoadProfileIntoUi(name, verb: "Restored");
            _activityLog.Log($"Restored profile '{name}' from its own latest backup.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            ProfileStatusMessage = $"Couldn't restore profile: {ex.Message}";
        }
    }
}
