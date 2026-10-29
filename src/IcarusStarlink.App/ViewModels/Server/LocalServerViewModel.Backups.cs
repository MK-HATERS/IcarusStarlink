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

    /// <summary>Lines in the admin box that aren't Steam64 ids, said as you type (Save still refuses them).</summary>
    public string? AdminListHint => ServerAccessLists.InvalidAdminLines(AdminWhitelistText) switch
    {
        [] => null,
        [var one] => $"Line {one.Line}: \"{one.Text}\" isn't a Steam64 id (17 digits starting 7656).",
        var many => $"{many.Count} lines aren't Steam64 ids (17 digits starting 7656): line {string.Join(", ", many.Take(5).Select(b => b.Line))}{(many.Count > 5 ? "…" : "")}.",
    };

    partial void OnAdminWhitelistTextChanged(string value) => OnPropertyChanged(nameof(AdminListHint));

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

        // Saved on every keystroke, but nothing is deleted here: typing "15" over "10" passes
        // through "1", which used to prune down to one backup of each kind there and then.
        BackupsToKeepError = null;
        SaveAutomation(p => p.BackupsToKeep = keep);
    }

    /// <summary>
    /// Applies the keep count to the backups already there: when the box loses focus (the view
    /// calls this), and after every new backup anyway. Not while the typed text is invalid.
    /// </summary>
    internal void ApplyBackupsToKeep()
    {
        if (BackupsToKeepError is not null || SelectedProfile is not { BackupsToKeep: > 0 } profile)
        {
            return;
        }

        PruneBackups(profile, ServerBackupsFolder(profile));
        RefreshBackups();
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

    /// <summary>A backup that must never block the action it precedes — a failure is reported, the action goes on. The zip is written off the UI thread (worlds can be hundreds of MB).</summary>
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

    /// <summary>A backup, restore or mod change is writing to a server: Start and Update wait for it (Start used to be allowed in the middle of a restore), and so does automation for that server.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackUpNowCommand), nameof(RestoreSelectedBackupCommand), nameof(StartCommand), nameof(UpdateServerCommand))]
    private bool _isBackupBusy;

    /// <summary>Which server IsBackupBusy is about (the selection can move on meanwhile).</summary>
    private Guid? _backupBusyFor;

    private void BeginBackupWork(DedicatedServerProfile p)
    {
        _backupBusyFor = p.Id;
        IsBackupBusy = true;
    }

    private void EndBackupWork()
    {
        IsBackupBusy = false;
        _backupBusyFor = null;
    }

    private bool CanBackUp() => !IsBackupBusy;

    [RelayCommand(CanExecute = nameof(CanBackUp))]
    private async Task BackUpNowAsync()
    {
        if (SelectedProfile is not { } p)
        {
            return;
        }

        BeginBackupWork(p);
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
            EndBackupWork();
        }

        RefreshBackups();
    }

    private bool CanRestore() => SelectedBackup is not null && !IsBackupBusy && CanChangeServerFiles;

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

        BeginBackupWork(p);
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
            EndBackupWork();
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
            _adminListOnLoad = _banListOnLoad = "";
            return;
        }

        _adminListOnLoad = TryReadList(DedicatedServerLayout.AdminWhitelistPath(p.InstallFolder, p.SavedDirSuffix));
        _banListOnLoad = TryReadList(DedicatedServerLayout.BanListPath(p.InstallFolder, p.SavedDirSuffix));
        AdminWhitelistText = _adminListOnLoad ?? "";
        BanListText = _banListOnLoad ?? "";
    }

    /// <summary>The lists as read when the boxes were filled; null when the file couldn't be read.</summary>
    private string? _adminListOnLoad;

    private string? _banListOnLoad;

    /// <summary>
    /// The boxes are filled when the server is selected, which can be days before Save: meanwhile
    /// the running server's BanPlayer/UnbanPlayer rewrote the ban list, and Save put the old list
    /// back over those bans. A list the owner didn't touch takes what is on disk now; one they
    /// edited asks before it replaces the newer file. False when nothing should be written.
    /// </summary>
    private bool ReconcileListWithDisk(string path, string label, string? onLoad, string boxText, Action<string> setBox, Action<string> setOnLoad)
    {
        var now = TryReadList(path);
        if (now is null)
        {
            StatusMessage = $"Couldn't read the {label} on disk, so neither list was saved (saving would replace it). Close whatever has it open, then try again.";
            return false;
        }

        if (onLoad is not null && SameListLines(now, onLoad))
        {
            return true;
        }

        if (onLoad is not null && SameListLines(boxText, onLoad))
        {
            // Not edited here: keep what the server (or another program) wrote.
            setOnLoad(now);
            setBox(now);
            return true;
        }

        if (_dialogService.Confirm(
                $"The {label} on disk {(onLoad is null ? "couldn't be read when this page opened" : "changed since this page read it (the server's BanPlayer and UnbanPlayer chat commands edit the ban list)")}.\n\n" +
                $"Replace it with the list shown here? Choose No to load the list that is on disk now instead; nothing is saved then.",
                "Admin and ban lists", ThemedConfirmSeverity.Warning))
        {
            setOnLoad(now);
            return true;
        }

        setOnLoad(now);
        setBox(now);
        StatusMessage = $"Nothing was saved. The {label} box now shows the file as it is on disk.";
        return false;
    }

    private static bool SameListLines(string a, string b)
    {
        static IEnumerable<string> Lines(string text) => text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
        return Lines(a).SequenceEqual(Lines(b), StringComparer.Ordinal);
    }

    /// <summary>Writes both lists. The server reads them on start, and its own BanPlayer/UnbanPlayer commands rewrite the ban list while running — so edit while stopped.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveAccessLists))]
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

        var adminPath = DedicatedServerLayout.AdminWhitelistPath(p.InstallFolder, p.SavedDirSuffix);
        var banPath = DedicatedServerLayout.BanListPath(p.InstallFolder, p.SavedDirSuffix);
        if (!ReconcileListWithDisk(banPath, "ban list", _banListOnLoad, BanListText, t => BanListText = t, t => _banListOnLoad = t)
            || !ReconcileListWithDisk(adminPath, "admin list", _adminListOnLoad, AdminWhitelistText, t => AdminWhitelistText = t, t => _adminListOnLoad = t))
        {
            return;
        }

        // Read again: a list not edited here may have just taken the newer text from disk.
        admins = AdminWhitelistText.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        try
        {
            WriteList(adminPath, admins);
            var bans = BanListText.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
            WriteList(banPath, bans);
            _adminListOnLoad = TryReadList(adminPath);
            _banListOnLoad = TryReadList(banPath);
            StatusMessage = $"Saved {admins.Count} admin(s) and {bans.Count} ban entr{(bans.Count == 1 ? "y" : "ies")}. They apply on the next start.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't save the lists: {ex.Message}";
        }
    }

    /// <summary>The file's text, "" when there is none, null when it exists but can't be read (an empty box for a locked file used to be saved over it).</summary>
    private static string? TryReadList(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
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
