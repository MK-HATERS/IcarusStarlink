using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Server;

namespace IcarusStarlink.App.ViewModels;

/// <summary>Phase 3: world backups (on demand and automatic before updates and mod changes), restore, and the admin and ban lists.</summary>
public sealed partial class LocalServerViewModel
{
    public ObservableCollection<ServerBackupInfo> Backups { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreSelectedBackupCommand))]
    private ServerBackupInfo? _selectedBackup;

    /// <summary>Steam64 ids, one per line — admin without /AdminLogin.</summary>
    [ObservableProperty]
    private string _adminWhitelistText = "";

    /// <summary>The server's ban list as it stores it (BanPlayer/UnbanPlayer in chat edit it too).</summary>
    [ObservableProperty]
    private string _banListText = "";

    private void RefreshServerExtras()
    {
        ReadInstalledBuild();
        RefreshMods();
        RefreshBackups();
        LoadAccessLists();
    }

    /// <summary>Found by the profile's id, so a renamed profile keeps (and renames) its backups folder.</summary>
    private string ServerBackupsFolder(DedicatedServerProfile profile) => ServerBackups.ResolveFolder(Path.Combine(_backupsRoot, "Server"), profile);

    /// <summary>"Keep the newest N of each kind" (Backups tab), saved straight into the profile. Blank or 0 keeps every backup.</summary>
    [ObservableProperty] private string _backupsToKeepText = "10";

    [ObservableProperty] private string? _backupsToKeepError;

    /// <summary>The once-a-day automatic backup (Overview, Automation).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AutomationSummary))] private bool _dailyBackup;

    partial void OnDailyBackupChanged(bool value) => SaveAutomation(p => p.DailyBackup = value);

    partial void OnBackupsToKeepTextChanged(string value)
    {
        if (_loadingAutomation)
        {
            return;
        }

        var text = value.Trim();
        var keep = 0;
        if (text.Length > 0
            && (!int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out keep) || keep < 0))
        {
            BackupsToKeepError = "A whole number: how many of each kind to keep (0 keeps all).";
            return;
        }

        BackupsToKeepError = null;
        SaveAutomation(p => p.BackupsToKeep = keep);
        if (SelectedProfile is { } profile && keep > 0)
        {
            PruneBackups(profile, ServerBackupsFolder(profile));
            RefreshBackups();
        }
    }

    /// <summary>Deletes backups beyond the profile's keep count (never the one just made). A failure only costs disk space, so it is logged, not shown.</summary>
    private void PruneBackups(DedicatedServerProfile profile, string folder, params string[] keep)
    {
        try
        {
            var deleted = ServerBackups.Prune(folder, profile.BackupsToKeep, keep);
            if (deleted.Count > 0)
            {
                _activityLog.Log($"Deleted {deleted.Count} old backup(s) of '{profile.Name}' (keeping the newest {profile.BackupsToKeep} of each kind).", ActivityEntryKind.Info);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _activityLog.Log($"Couldn't tidy old backups of '{profile.Name}': {ex.Message}", ActivityEntryKind.Warning);
        }
    }

    /// <summary>Makes a backup and applies the keep count; the zip itself is written off the UI thread.</summary>
    private async Task<string> CreateBackupAsync(DedicatedServerProfile p, string reason)
    {
        var folder = ServerBackupsFolder(p);
        var path = await OffUiThread(() => ServerBackups.CreateBackup(p.InstallFolder, p.SavedDirSuffix, folder, reason));
        PruneBackups(p, folder, path);
        return path;
    }

    public string BackupsFolderPath => SelectedProfile is { } p ? ServerBackupsFolder(p) : "";

    private void RefreshBackups()
    {
        Backups.Clear();
        OnPropertyChanged(nameof(BackupsFolderPath));
        if (SelectedProfile is not { } p)
        {
            return;
        }

        foreach (var backup in ServerBackups.List(ServerBackupsFolder(p)))
        {
            Backups.Add(backup);
        }
    }

    /// <summary>A backup that must never block the action it precedes — a failure is reported, the action goes on.</summary>
    private string? TryBackup(string reason) => SelectedProfile is { } p ? TryBackup(p, reason) : null;

    private string? TryBackup(DedicatedServerProfile p, string reason)
    {
        try
        {
            var folder = ServerBackupsFolder(p);
            var path = ServerBackups.CreateBackup(p.InstallFolder, p.SavedDirSuffix, folder, reason);
            PruneBackups(p, folder, path);
            return path;
        }
        catch (Exception ex)
        {
            _activityLog.Log($"Couldn't back up '{p.Name}' before {reason}: {ex.Message}", ActivityEntryKind.Warning);
            return null;
        }
    }

    /// <summary>The same, with the zip written off the UI thread (worlds can be hundreds of MB).</summary>
    private async Task<string?> TryBackupAsync(DedicatedServerProfile p, string reason)
    {
        try
        {
            return await CreateBackupAsync(p, reason);
        }
        catch (Exception ex)
        {
            _activityLog.Log($"Couldn't back up '{p.Name}' before {reason}: {ex.Message}", ActivityEntryKind.Warning);
            return null;
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackUpNowCommand), nameof(RestoreSelectedBackupCommand))]
    private bool _isBackupBusy;

    private bool CanBackUp() => !IsBackupBusy;

    [RelayCommand(CanExecute = nameof(CanBackUp))]
    private async Task BackUpNowAsync()
    {
        if (SelectedProfile is not { } p)
        {
            return;
        }

        IsBackupBusy = true;
        StatusMessage = $"Backing up '{p.Name}'...";
        try
        {
            var path = await CreateBackupAsync(p, "manual");
            StatusMessage = $"Backed up '{p.Name}' to {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Backup failed: {ex.Message}";
        }
        finally
        {
            IsBackupBusy = false;
        }

        RefreshBackups();
    }

    private bool CanRestore() => SelectedBackup is not null && !IsBackupBusy;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreSelectedBackupAsync()
    {
        if (SelectedProfile is not { } p || SelectedBackup is not { } backup || !EnsureStoppedFor("restore a backup"))
        {
            return;
        }

        if (!_dialogService.Confirm(
                $"Restore '{backup.Name}' ({backup.CreatedLocal:g}) to '{p.Name}'?\n\nIts worlds, settings, admin and ban lists overwrite the current ones. The current state is backed up first, so this can be undone.",
                "Restore backup", ThemedConfirmSeverity.Warning))
        {
            return;
        }

        IsBackupBusy = true;
        StatusMessage = $"Restoring '{backup.Name}'...";
        try
        {
            var folder = ServerBackupsFolder(p);
            var safety = await OffUiThread(() => ServerBackups.Restore(backup.Path, p.InstallFolder, p.SavedDirSuffix, folder));
            PruneBackups(p, folder, safety, backup.Path);
            StatusMessage = $"Restored '{backup.Name}'. The state before it is in {Path.GetFileName(safety)}.";
            _activityLog.Log($"Restored server backup '{backup.Name}' to '{p.Name}'.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Restore failed: {ex.Message}";
        }
        finally
        {
            IsBackupBusy = false;
        }

        LoadForm();
        RefreshBackups();
        LoadAccessLists();
    }

    [RelayCommand]
    private void OpenBackupsFolder()
    {
        if (SelectedProfile is { } p)
        {
            Directory.CreateDirectory(ServerBackupsFolder(p));
            UrlOpener.TryOpen(ServerBackupsFolder(p));
        }
    }

    private void LoadAccessLists()
    {
        if (SelectedProfile is not { } p)
        {
            AdminWhitelistText = BanListText = "";
            return;
        }

        AdminWhitelistText = ReadOrEmpty(DedicatedServerLayout.AdminWhitelistPath(p.InstallFolder, p.SavedDirSuffix));
        BanListText = ReadOrEmpty(DedicatedServerLayout.BanListPath(p.InstallFolder, p.SavedDirSuffix));
    }

    /// <summary>Writes both lists. The server reads them on start, and its own BanPlayer/UnbanPlayer commands rewrite the ban list while running — so edit while stopped.</summary>
    [RelayCommand]
    private void SaveAccessLists()
    {
        if (SelectedProfile is not { } p || !EnsureStoppedFor("edit the admin and ban lists (the running server rewrites the ban list itself)"))
        {
            return;
        }

        var admins = AdminWhitelistText.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var bad = admins.Where(id => id.Length != 17 || !id.All(char.IsDigit)).ToList();
        if (bad.Count > 0)
        {
            StatusMessage = $"Admin list entries must be Steam64 ids (17 digits): {string.Join(", ", bad)}";
            return;
        }

        try
        {
            WriteList(DedicatedServerLayout.AdminWhitelistPath(p.InstallFolder, p.SavedDirSuffix), admins);
            var bans = BanListText.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
            WriteList(DedicatedServerLayout.BanListPath(p.InstallFolder, p.SavedDirSuffix), bans);
            StatusMessage = $"Saved {admins.Count} admin(s) and {bans.Count} ban entr{(bans.Count == 1 ? "y" : "ies")}. They apply on the next start.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't save the lists: {ex.Message}";
        }
    }

    private static string ReadOrEmpty(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch (IOException)
        {
            return "";
        }
    }

    private static void WriteList(string path, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0 && !File.Exists(path))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, lines.Count == 0 ? "" : string.Join("\r\n", lines) + "\r\n");
    }
}
