using System.IO;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Activity;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class SavesViewModel
{
    private void RefreshBackupsList()
    {
        Backups.Clear();
        if (SelectedSlot is null)
        {
            return;
        }

        // A problem with the backups folder must not fail what called this: a load (which then
        // blocked saving a save that read fine) or a save that already succeeded ("Save failed").
        try
        {
            foreach (var backup in _repository.ListBackups(SelectedSlot.SteamId))
            {
                Backups.Add(backup);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SelectedBackup = null;
            LastBackupDisplay = $"Couldn't list backups: {ex.Message}";
            return;
        }

        SelectedBackup = Backups.FirstOrDefault();
        LastBackupDisplay = Backups.FirstOrDefault() is { } newest
            ? $"Last backup: {newest.TakenAtUtc.LocalDateTime:g}"
            : "No backups yet.";
    }

    [RelayCommand]
    private void BackupNow()
    {
        if (SelectedSlot is null)
        {
            return;
        }

        try
        {
            var zipPath = _repository.BackupSlotManually(SelectedSlot.SteamId);
            RefreshBackupsList();
            StatusMessage = $"Backed up to '{Path.GetFileName(zipPath)}'.";
            _activityLog.Log($"Backed up save slot {SelectedSlot.Display}.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Backup failed: {ex.Message}";
            _activityLog.Log($"Save backup failed: {ex.Message}", ActivityEntryKind.Warning);
        }
    }

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task RestoreBackupAsync()
    {
        if (SelectedSlot is null || SelectedBackup is null)
        {
            return;
        }

        if (IsGameRunning())
        {
            StatusMessage = "Icarus is running — close it before restoring, or the game will overwrite the restored files when it exits.";
            return;
        }

        // Via IDialogService, not ThemedMessageBox.Show directly — this method's real write below
        // needs to be testable without a live WPF Dispatcher, same reasoning DeleteCharacter's own
        // confirm relies on.
        var confirm = _dialogService.Confirm(
            $"Replace this save slot's current files with the backup from {SelectedBackup.TakenAtUtc.LocalDateTime:g}?\n\n"
            + "A pre_restore safety zip of the slot as it is right now is written first, so this is itself undoable.\n\n"
            + "Your single-player worlds, each character's list of worlds and map data are never restored: they stay as they are now."
            + (HasAnyUnsavedEdits ? "\n\nYour unsaved edits on this page will be lost." : ""),
            "Restore save backup", ThemedConfirmSeverity.Warning);
        if (!(confirm))
        {
            return;
        }

        try
        {
            // A SECOND, LATE check, immediately before the real write below — the first
            // IsGameRunning() check above (before the confirm dialog) can go stale for however long
            // the user takes to answer that dialog, and Icarus could be launched in that window.
            // This doesn't close the race entirely (no check here can, without an OS-level file
            // lock held across processes for the whole write) — it only shrinks the exposed window
            // down to "immediately before the write" instead of "however long the confirm dialog
            // was up", aborting late-and-safe (nothing has been touched yet) rather than
            // early-and-stale.
            if (IsGameRunning())
            {
                StatusMessage = "Icarus started running while this restore was about to write — nothing was touched. Close it and try again.";
                return;
            }

            _repository.RestoreSlot(SelectedSlot.SteamId, SelectedBackup.FilePath);
            await LoadSlotAsync();

            // The reload's own "Couldn't read this save" must not be covered by "Restored."
            StatusMessage = _loadFailed
                ? $"Restored, but the restored save couldn't be read back: {StatusMessage} The replaced state was saved as a pre_restore zip."
                : "Restored. The replaced state was saved as a pre_restore zip.";
            _activityLog.Log($"Restored save slot {SelectedSlot.Display} from backup.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Restore failed: {ex.Message}";
            _activityLog.Log($"Save restore failed: {ex.Message}", ActivityEntryKind.Warning);
        }
    }

    [RelayCommand]
    private void OpenBackupsFolder()
    {
        if (Backups.FirstOrDefault() is not { } any)
        {
            StatusMessage = "No backups yet — take one first.";
            return;
        }

        UrlOpener.TryOpen(Path.GetDirectoryName(any.FilePath)!);
    }
}
