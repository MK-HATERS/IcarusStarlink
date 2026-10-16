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

    private string ServerBackupsFolder(DedicatedServerProfile profile)
    {
        var safeName = new string([.. profile.Name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')]);
        return Path.Combine(_backupsRoot, "Server", $"{(safeName.Length > 0 ? safeName : "server")}_{profile.Id.ToString("N")[..8]}");
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
    private string? TryBackup(string reason)
    {
        if (SelectedProfile is not { } p)
        {
            return null;
        }

        try
        {
            return ServerBackups.CreateBackup(p.InstallFolder, p.SavedDirSuffix, ServerBackupsFolder(p), reason);
        }
        catch (Exception ex)
        {
            _activityLog.Log($"Couldn't back up '{p.Name}' before {reason}: {ex.Message}", ActivityEntryKind.Warning);
            return null;
        }
    }

    [RelayCommand]
    private void BackUpNow()
    {
        if (SelectedProfile is not { } p)
        {
            return;
        }

        try
        {
            var path = ServerBackups.CreateBackup(p.InstallFolder, p.SavedDirSuffix, ServerBackupsFolder(p), "manual");
            StatusMessage = $"Backed up '{p.Name}' to {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Backup failed: {ex.Message}";
        }

        RefreshBackups();
    }

    private bool CanRestore() => SelectedBackup is not null;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private void RestoreSelectedBackup()
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

        try
        {
            var safety = ServerBackups.Restore(backup.Path, p.InstallFolder, p.SavedDirSuffix, ServerBackupsFolder(p));
            StatusMessage = $"Restored '{backup.Name}'. The state before it is in {Path.GetFileName(safety)}.";
            _activityLog.Log($"Restored server backup '{backup.Name}' to '{p.Name}'.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Restore failed: {ex.Message}";
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
