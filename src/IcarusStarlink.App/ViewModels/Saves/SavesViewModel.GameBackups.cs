using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Saves;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// Backup &amp; restore › "Kept by the game": the game's own rolling copies of the player files
/// (Profile.json.backup, Characters.json.backup_1 … _10) beside them in the slot folder. One is
/// put back under the file's real name only by copying, after Starlink's own pre_restore zip,
/// and only while the game is closed.
/// </summary>
public sealed partial class SavesViewModel
{
    /// <summary>The game's own copies of this slot's player files, newest first.</summary>
    public ObservableCollection<GameBackupCopy> GameBackups { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreGameBackupCommand))]
    private GameBackupCopy? _selectedGameBackup;

    public bool HasGameBackups => GameBackups.Count > 0;

    public bool HasNoGameBackups => GameBackups.Count == 0;

    /// <summary>Shown when there are no copies: "no copies yet", or why the slot folder couldn't be read.</summary>
    public string GameBackupsNote { get; private set; } = "no copies yet";

    /// <summary>
    /// True while a game copy is put back in the background: Save changes, Restore, Backup now and
    /// Put back wait, so two writes never touch the slot's files at once.
    /// </summary>
    private bool _restoringGameCopy;

    private const string WaitForGameCopyText = "Wait until the game's copy has been put back, then try again.";

    /// <summary>
    /// Lists the game's copies again. The game shifts them along at every save, so this runs with
    /// every backups refresh and each time the page is shown. A pick stays only while that exact
    /// copy (same file, same time, same size) is still there.
    /// </summary>
    private void RefreshGameBackupsList()
    {
        var picked = SelectedGameBackup;
        GameBackups.Clear();
        GameBackupsNote = "no copies yet";
        if (SelectedSlot is { } slot)
        {
            try
            {
                foreach (var copy in _repository.ListGameBackups(slot.SteamId))
                {
                    GameBackups.Add(copy);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // No list then; the rest of the page still works, and the empty text says why.
                GameBackupsNote = $"Couldn't read the game's copies: {ex.Message}";
            }
        }

        SelectedGameBackup = picked is not null && GameBackups.Contains(picked) ? picked : null;
        OnPropertyChanged(nameof(HasGameBackups));
        OnPropertyChanged(nameof(HasNoGameBackups));
        OnPropertyChanged(nameof(GameBackupsNote));
    }

    private bool CanRestoreGameBackup() => SelectedGameBackup is not null && !_restoringGameCopy;

    [RelayCommand(CanExecute = nameof(CanRestoreGameBackup), FlowExceptionsToTaskScheduler = true)]
    private async Task RestoreGameBackupAsync()
    {
        if (_restoringGameCopy || SelectedSlot is not { } slot || SelectedGameBackup is not { } copy)
        {
            return;
        }

        if (IsGameRunning())
        {
            StatusMessage = "Icarus is running. Close it before putting back a game copy, or the game will write over it when it exits.";
            return;
        }

        var confirm = _dialogService.Confirm(
            $"Put back {copy.OriginalFileName} from the game's own copy of {copy.WrittenUtc.ToLocalTime():g}?\n\n"
            + "Starlink first saves a pre_restore zip of this account's player data as it is now, so you can undo this. "
            + "The game's copy stays where it is. Your worlds aren't touched."
            + (HasAnyUnsavedEdits ? "\n\nYour unsaved edits on this page will be lost." : ""),
            "Put back the game's copy", ThemedConfirmSeverity.Warning);
        if (!confirm)
        {
            return;
        }

        // Checked again just before the write: the game may have started while the question was open.
        if (IsGameRunning())
        {
            StatusMessage = "Icarus started running while this was about to write. Nothing was changed. Close it and try again.";
            return;
        }

        _restoringGameCopy = true;
        RestoreGameBackupCommand.NotifyCanExecuteChanged();
        try
        {
            await Task.Run(() => _repository.RestoreGameBackup(slot.SteamId, copy));
            await LoadSlotAsync();
            StatusMessage = _loadFailed
                ? $"Put back {copy.OriginalFileName}, but the save couldn't be read back: {StatusMessage} The replaced state was saved as a pre_restore zip."
                : $"Put back {copy.OriginalFileName} from the game's copy of {copy.WrittenUtc.ToLocalTime():g}. The replaced state was saved as a pre_restore zip.";
            _activityLog.Log($"Put back {copy.OriginalFileName} for {slot.Display} from the game's own copy.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't put the game's copy back: {ex.Message}";
            _activityLog.Log($"Putting back the game's copy of {copy.OriginalFileName} failed: {ex.Message}", ActivityEntryKind.Warning);
            RefreshBackupsList();
        }
        finally
        {
            _restoringGameCopy = false;
            RestoreGameBackupCommand.NotifyCanExecuteChanged();
        }
    }
}
