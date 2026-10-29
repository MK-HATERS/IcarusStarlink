using System.IO;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// When the server's files can be changed: the Mods, Backups, Share and access-list buttons grey
/// out (with a tooltip saying why) instead of refusing after the click. The guards inside the
/// commands (EnsureStoppedFor, EnsureInstallStoppedFor) stay as the last line of defence: the
/// state can change between the click and the run, and they look at the processes afresh.
/// </summary>
public sealed partial class LocalServerViewModel
{
    /// <summary>
    /// Why the selected server's own files (its Saved folder: restore, admin and ban lists) can't
    /// change right now, or null when they can. The same rule as EnsureStoppedFor, plus a backup or
    /// mod copy already writing to it.
    /// </summary>
    public string? ServerFilesBlockedReason
    {
        get
        {
            if (SelectedProfile is null)
            {
                return null;
            }

            if (IsAutomationBusy)
            {
                return "Waiting for the automatic restart to finish.";
            }

            if (IsBusy)
            {
                return _busyWith == "UPDATING" ? "Waiting for the update to finish."
                    : Running is null ? "Waiting for the server to finish starting."
                    : "Waiting for the server to stop.";
            }

            if (IsBackupBusy)
            {
                return "Waiting for the backup or mod copy to finish.";
            }

            return Running is not null ? "Stop the server first." : null;
        }
    }

    /// <summary>
    /// Why what the whole install shares (paks, UE4SS, its mods) can't change right now, or null.
    /// As EnsureInstallStoppedFor, but from what automation last saw of the other profiles on this
    /// install (no process scan on every CanExecute); the guard scans again on the click.
    /// </summary>
    public string? InstallFilesBlockedReason
    {
        get
        {
            if (ServerFilesBlockedReason is { } own)
            {
                return own;
            }

            if (SelectedProfile is not { } profile)
            {
                return null;
            }

            var install = Path.TrimEndingDirectorySeparator(profile.InstallFolder);
            foreach (var other in Profiles)
            {
                if (other.Id == profile.Id || !_watches.TryGetValue(other.Id, out var watch)
                    || !string.Equals(Path.TrimEndingDirectorySeparator(other.InstallFolder), install, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (watch.AutomationBusy)
                {
                    return $"Waiting for automation to finish restarting '{other.Name}' (it runs from the same install).";
                }

                if (watch.Running is not null)
                {
                    return $"Stop '{other.Name}' first: it runs from the same install.";
                }
            }

            return null;
        }
    }

    /// <summary>The selected server's own files can change now (Restore, Save lists, World settings Save).</summary>
    public bool CanChangeServerFiles => SelectedProfile is not null && ServerFilesBlockedReason is null;

    /// <summary>The install's shared files can change now (every Mods button, the UE4SS On switches, Apply a patch).</summary>
    public bool CanChangeInstallFiles => SelectedProfile is not null && InstallFilesBlockedReason is null;

    private bool CanChangeInstallFilesFor(ServerUe4ssRow? row) => row is not null && CanChangeInstallFiles;

    private bool CanInstallMergedPak() => CanChangeInstallFiles;

    private bool CanInstallUe4ssOnServer() => CanChangeInstallFiles;

    private bool CanMatchServerToGame() => CanChangeInstallFiles;

    private bool CanApplyPatchToServer() => CanChangeInstallFiles;

    private bool CanSaveAccessLists() => CanChangeServerFiles;

    /// <summary>
    /// One of the inputs changed (selection, Running, IsBusy, automation, a backup, another
    /// profile's state): every guarded button and its tooltip are looked at again.
    /// </summary>
    private void RaiseServerFilesChanged()
    {
        OnPropertyChanged(nameof(ServerFilesBlockedReason));
        OnPropertyChanged(nameof(InstallFilesBlockedReason));
        OnPropertyChanged(nameof(CanChangeServerFiles));
        OnPropertyChanged(nameof(CanChangeInstallFiles));
        InstallMergedPakCommand.NotifyCanExecuteChanged();
        RemoveSelectedPakCommand.NotifyCanExecuteChanged();
        InstallUe4ssOnServerCommand.NotifyCanExecuteChanged();
        CopyUe4ssModToServerCommand.NotifyCanExecuteChanged();
        MatchServerToGameCommand.NotifyCanExecuteChanged();
        RemoveServerUe4ssModCommand.NotifyCanExecuteChanged();
        RestoreSelectedBackupCommand.NotifyCanExecuteChanged();
        SaveAccessListsCommand.NotifyCanExecuteChanged();
        ApplyPatchToServerCommand.NotifyCanExecuteChanged();
        WorldSettings?.RefreshSaveBlocked();
    }

    partial void OnIsBusyChanged(bool value) => RaiseServerFilesChanged();

    partial void OnIsBackupBusyChanged(bool value) => RaiseServerFilesChanged();
}
