using System.Windows;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Library;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// "Keep this version" (a switch on a data or UE4SS mod: no Update badge, left out of Update all,
/// until an update newer than the skipped one comes out) and a data mod's version history (every
/// kept backup, with Put this version back and Compare with now).
/// </summary>
public sealed partial class LibraryViewModel
{
    /// <summary>Opens the version history window. Tests swap it to look at the view model.</summary>
    internal Action<ModVersionHistoryViewModel> ShowVersionHistoryWindow { get; set; } = viewModel =>
        new ModVersionHistoryWindow(viewModel) { Owner = Application.Current.MainWindow }.Show();

    /// <summary>The newest update known now becomes the skipped one; nothing known yet (no check) skips every update until it's turned off.</summary>
    private static KeptVersion KeepNow(int? latestFileId, string? latestVersion) => new()
    {
        KeptAtUtc = DateTimeOffset.UtcNow,
        SkippedFileId = latestFileId,
        SkippedVersion = string.IsNullOrWhiteSpace(latestVersion) ? null : latestVersion,
    };

    /// <summary>Turns "Keep this version" on or off for a data mod, saved in its Library info.</summary>
    [RelayCommand]
    private void ToggleKeepVersion(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var kept = item.IsVersionKept ? null : KeepNow(item.LatestNexusFileId, item.LatestVersion);
        try
        {
            _repository.SetKeptVersion(item.FolderName, kept);
        }
        catch (Exception ex)
        {
            ReportProblem($"Couldn't save Keep this version for '{item.Name}': {ex.Message}");
            return;
        }

        item.SetKeptVersion(kept);
        StatusMessage = KeptMessage(item.Name, kept, item.HasUpdateIgnoringKept, item.LatestVersion);
        _activityLog.Log(StatusMessage, ActivityEntryKind.Info);
        RefreshFilters();
    }

    /// <summary>Turns "Keep this version" on or off for a UE4SS mod, saved in its UE4SS info.</summary>
    [RelayCommand]
    private void ToggleKeepUe4ssVersion(Ue4ssModRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var kept = row.IsVersionKept ? null : KeepNow(row.LatestNexusFileId, row.LatestVersion);
        try
        {
            var meta = _ue4ssModMetaStore.Load(row.Name);
            meta.KeptVersion = kept;
            _ue4ssModMetaStore.Save(row.Name, meta);
            if (_ue4ssMetaByName.ContainsKey(row.Name))
            {
                _ue4ssMetaByName[row.Name] = meta;
            }
        }
        catch (Exception ex)
        {
            Ue4ssStatusMessage = $"Couldn't save Keep this version for '{row.Name}': {ex.Message}";
            return;
        }

        row.KeptVersion = kept;
        Ue4ssStatusMessage = KeptMessage(row.Name, kept, row.HasUpdateIgnoringKept, row.LatestVersion);
        _activityLog.Log(Ue4ssStatusMessage, ActivityEntryKind.Info);
        RefreshFilters();
    }

    private static string KeptMessage(string name, KeptVersion? kept, bool hadUpdate, string? latestVersion) =>
        kept is null
            ? $"'{name}' gets updates again: Check for updates and Update all include it."
            : hadUpdate && !string.IsNullOrWhiteSpace(latestVersion)
                ? $"Keeping your version of '{name}'. Starlink won't offer {VersionLabel.For(latestVersion)} or include it in Update all; a newer update shows again."
                : $"Keeping your version of '{name}'. Starlink won't offer its updates or include it in Update all until you turn this off.";

    /// <summary>Opens the mod's version history: every backup Starlink kept, newest first.</summary>
    [RelayCommand]
    private void ShowVersionHistory(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var folderName = item.FolderName;
        var name = item.Name;
        ShowVersionHistoryWindow(new ModVersionHistoryViewModel(
            name,
            () => ListBackupsSafely(folderName),
            backup => RestoreVersionAsync(folderName, name, backup),
            backup => CompareWithBackupAsync(name, backup.Path, folderName, "a kept copy")));
    }

    private IReadOnlyList<ModBackupInfo> ListBackupsSafely(string folderName)
    {
        try
        {
            return _repository.ListModBackups(folderName);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            ReportProblem($"Couldn't read the backups: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Puts an older version back, after asking. The copy in the Library now is backed up first
    /// (it shows at the top of the history), so this can be undone from the same list.
    /// </summary>
    internal async Task<bool> RestoreVersionAsync(string folderName, string name, ModBackupInfo backup)
    {
        if (!_itemsByFolderName.TryGetValue(folderName, out var item))
        {
            ReportProblem($"'{name}' is no longer in the Library.");
            return false;
        }

        if (RefuseWhileBusy(item))
        {
            return false;
        }

        var when = new ModVersionEntryViewModel(backup);
        var what = string.IsNullOrEmpty(when.VersionText) ? $"the copy from {when.WhenText}" : $"{when.VersionText} from {when.WhenText}";
        if (!_dialogService.Confirm(
                $"Put back {what} of '{name}'?\n\nThis replaces the copy in your Library. Starlink backs up the copy you have now first, so you can switch back from this list.",
                "Put this version back", ThemedConfirmSeverity.Question)
            || RefuseWhileBusy(item))
        {
            return false;
        }

        item.FlushPendingSave();
        try
        {
            var restored = await Task.Run(() => FixService.RestoreBackup(folderName, backup.Path));
            if (!restored)
            {
                ReportProblem($"That backup of '{name}' is gone, so nothing was changed. Close the list and open it again.");
                return false;
            }

            StatusMessage = $"Put back {what} of '{name}'. The copy you had is at the top of its version list. Run Merge & Install to use it in the game.";
            _activityLog.Log(StatusMessage, ActivityEntryKind.Success);
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
            return true;
        }
        catch (Exception ex)
        {
            ReportProblem($"Couldn't put that version of '{name}' back: {ex.Message}{LockedFileNextStep(ex)}");
            _activityLog.Log($"Putting back a version of '{name}' failed: {ex.Message}", ActivityEntryKind.Warning);
            return false;
        }
    }
}
