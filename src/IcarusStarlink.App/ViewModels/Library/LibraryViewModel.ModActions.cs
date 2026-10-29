using System.Windows;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One mod's right-click actions: back up, restore a backup, reinstall the original, rename, and open the EXMOD editor.</summary>
public sealed partial class LibraryViewModel
{
    /// <summary>Snapshots the mod's whole folder — a safety net before a risky manual edit, independent of the EXMOD editor's own transient per-field preview.</summary>
    [RelayCommand]
    private void BackupMod(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            _repository.BackupMod(item.FolderName);
            item.NotifyBackupStateChanged();
            StatusMessage = $"Backed up '{item.Name}'.";
            _activityLog.Log(StatusMessage, ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Backup failed: {ex.Message}";
            _activityLog.Log($"Backing up '{item.Name}' failed: {ex.Message}", ActivityEntryKind.Warning);
        }
    }

    /// <summary>Says why and returns true when the mod can't be changed on disk right now (ModChangeBlockedReason).</summary>
    private bool RefuseWhileBusy(LibraryItemViewModel item)
    {
        if (ModChangeBlockedReason(item) is not { } blocked)
        {
            return false;
        }

        StatusMessage = blocked;
        return true;
    }

    /// <summary>
    /// Replaces the mod's current folder with its own most recent backup — a real point-in-time
    /// restore, so this is gated the same way every other hard-to-reverse action in this app is.
    /// </summary>
    [RelayCommand]
    private void RestoreModBackup(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (RefuseWhileBusy(item))
        {
            return;
        }

        var confirmResult = _dialogService.Confirm(
            $"This replaces the current content of '{item.Name}' with its most recent backup — any edit made since then is lost.\n\nContinue?",
            "Restore backup", ThemedConfirmSeverity.Question);
        if (!(confirmResult) || RefuseWhileBusy(item))
        {
            return;
        }

        try
        {
            // Through the fix service: it takes the same lock as every other writer of the mod.
            var restored = FixService.RestoreBackup(item.FolderName);
            StatusMessage = restored ? $"Restored '{item.Name}' from its latest backup." : $"No backup exists yet for '{item.Name}'.";
            if (restored)
            {
                _activityLog.Log(StatusMessage, ActivityEntryKind.Success);
                WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Restore failed: {ex.Message}";
            _activityLog.Log($"Restoring '{item.Name}' from its backup failed: {ex.Message}", ActivityEntryKind.Warning);
        }
    }

    /// <summary>
    /// Puts the mod back exactly as it was downloaded: the same Reinstall the Downloads page offers
    /// (delete the Library copy, import the kept download again), so fixes, edits and renames made
    /// since are gone, and its remembered fix choices are forgotten so the next check starts fresh.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ReinstallOriginal(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (RefuseWhileBusy(item))
        {
            return;
        }

        if (Downloads.FindOriginalDownload(item.FolderName) is not { } download)
        {
            StatusMessage = $"Starlink doesn't have the original download for '{item.Name}' (it was imported from a file, or the download was discarded). Import that file again, or use Restore latest backup.";
            return;
        }

        if (!_dialogService.Confirm(
                $"Reinstall '{item.Name}' from its original download ({download.FileName})?\n\nThis replaces the Library copy: fixes, edits and a rename made since are lost, and its remembered fix choices are forgotten. It's backed up first, so Restore latest backup can bring this copy back.",
                "Reinstall original", ThemedConfirmSeverity.Warning)
            || RefuseWhileBusy(item))
        {
            return;
        }

        item.FlushPendingSave();

        var name = item.Name;
        try
        {
            // Pristine: backed up first, nothing carried over but pin/notes, and the remembered fix
            // choices forgotten — only once it worked (a failed reinstall puts the old copy back and
            // keeps them).
            var result = await Downloads.ActivateAsync(download, pristine: true);
            if (result.Succeeded)
            {
                StatusMessage = Downloads.PendingDownloadStatusMessage is { Length: > 0 } detail ? $"Reinstalled '{name}': {detail}" : $"Reinstalled '{name}'.";
                _activityLog.Log(StatusMessage, ActivityEntryKind.Success);
            }
            else
            {
                StatusMessage = result.Cancelled ? result.Message : $"Reinstall of '{name}' failed: {result.Message}";
                _activityLog.Log(StatusMessage ?? $"Reinstall of '{name}' didn't finish.", result.Cancelled ? ActivityEntryKind.Info : ActivityEntryKind.Warning);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Reinstall failed: {ex.Message}";
            _activityLog.Log($"Reinstall of '{name}' failed: {ex.Message}", ActivityEntryKind.Warning);
        }
    }

    /// <summary>Opens a small dialog to override how a mod's own name displays in Library — never touches its real folder, FileName, or file content. Reset clears the override; Cancel does nothing.</summary>
    [RelayCommand]
    private void RenameItem(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var result = _dialogService.PromptRename(item.Name);
        if (result.Cancelled)
        {
            return;
        }

        try
        {
            _repository.SetDisplayNameOverride(item.FolderName, result.NewDisplayName);
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't rename: {ex.Message}";
        }
    }

    /// <summary>An inline per-row Edit action (row hover icons) — takes the row's own item directly rather than requiring it to be selected first.</summary>
    [RelayCommand]
    private void EditItem(LibraryItemViewModel? item)
    {
        if (item is null || item.IsOpaquePak)
        {
            return;
        }

        try
        {
            OpenEditor(item.FolderName);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't open the mod editor: {ex.Message}";
        }
    }

    /// <summary>
    /// A new, non-modal Window + a fresh, transient ExmodEditorViewModel per call — classic IMM's
    /// own real editor supports two independent sessions open at once, which .Show() (not
    /// ShowDialog) matches; this app's other ViewModels stay DI singletons, but the editor
    /// deliberately isn't one.
    /// </summary>
    /// <param name="preSelect">
    /// When given and a matching row exists in the freshly-opened editor's own Items list, selects
    /// it instead of the editor's own default (its first item) — used by the fix window's Open in mod editor so the
    /// editor's existing amber base-diff highlighting is what the user sees first, not an unrelated
    /// row.
    /// </param>
    /// <param name="startWizard">An item template's preset: the new-item wizard opens over the editor straight away.</param>
    private void OpenEditor(string folderName, (string CurrentFile, string ItemName)? preSelect = null, NewItemPreset? startWizard = null)
    {
        var editorViewModel = _editorFactory(folderName);
        if (preSelect is { } target)
        {
            var match = editorViewModel.Items.FirstOrDefault(i => i.CurrentFile == target.CurrentFile && i.ItemName == target.ItemName);
            if (match is not null)
            {
                editorViewModel.SelectedItem = match;
            }
        }

        var window = new ExmodEditorWindow(editorViewModel) { Owner = Application.Current.MainWindow };
        // Locked: Fix sure problems reads it live from its background pass (IsEditorOpenLive).
        lock (_openEditorCountByFolder)
        {
            _openEditorCountByFolder[folderName] = _openEditorCountByFolder.GetValueOrDefault(folderName) + 1;
        }

        window.Closed += (_, _) =>
        {
            lock (_openEditorCountByFolder)
            {
                var remaining = _openEditorCountByFolder[folderName] - 1;
                if (remaining <= 0)
                {
                    _openEditorCountByFolder.Remove(folderName);
                }
                else
                {
                    _openEditorCountByFolder[folderName] = remaining;
                }
            }
        };
        window.Show();
        if (startWizard is not null)
        {
            // After the editor has drawn, so the wizard opens over it.
            window.Dispatcher.BeginInvoke(() => editorViewModel.StartNewItemWizard(startWizard), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    /// <summary>Test-only seam — see AssemblyInfo.cs's own InternalsVisibleTo grant. OpenEditor itself constructs a real ExmodEditorWindow, which (like every other real Window in this app) can't run in a unit test host — this lets a test mark a folder as "has an open editor" the same way OpenEditor's own bookkeeping does, without needing a real window.</summary>
    internal void MarkFolderEditorOpenForTesting(string folderName)
    {
        lock (_openEditorCountByFolder)
        {
            _openEditorCountByFolder[folderName] = _openEditorCountByFolder.GetValueOrDefault(folderName) + 1;
        }
    }

    /// <summary>Whether the mod is open in an editor right now; safe from a background thread (the count is only changed under the same lock).</summary>
    private bool IsEditorOpenLive(string folderName)
    {
        lock (_openEditorCountByFolder)
        {
            return _openEditorCountByFolder.ContainsKey(folderName);
        }
    }
}
