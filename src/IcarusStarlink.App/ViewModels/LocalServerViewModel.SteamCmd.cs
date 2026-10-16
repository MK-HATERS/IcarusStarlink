using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Server;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// Phase 2: install, update and branch-switch the server through SteamCMD (app 2089300, anonymous),
/// and tell the user when Steam has a newer build than the one installed.
/// </summary>
public sealed partial class LocalServerViewModel
{
    public IReadOnlyList<string> BranchOptions => SteamCmd.Branches;

    /// <summary>The branch this profile should be on (Settings). Differs from InstalledBranch until Update runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BuildSummary), nameof(UpdateAvailable))]
    private string _branch = "public";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BuildSummary), nameof(UpdateAvailable))]
    private int? _installedBuildId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BuildSummary), nameof(UpdateAvailable))]
    private string? _installedBranch;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BuildSummary), nameof(UpdateAvailable))]
    private int? _latestBuildId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(RestartCommand), nameof(UpdateServerCommand),
        nameof(CheckForUpdateCommand), nameof(InstallNewServerCommand))]
    private bool _isSteamCmdBusy;

    /// <summary>0-100 while SteamCMD reports progress.</summary>
    [ObservableProperty]
    private double _steamCmdProgress;

    /// <summary>The latest meaningful SteamCMD line ("downloading 39.8%").</summary>
    [ObservableProperty]
    private string? _steamCmdStatus;

    /// <summary>Steam has a newer build for the profile's branch, or the profile asks for a different branch than the one installed.</summary>
    public bool UpdateAvailable =>
        (LatestBuildId is { } latest && InstalledBuildId is { } installed && latest != installed)
        || (InstalledBranch is not null && !string.Equals(InstalledBranch, SteamCmd.NormalizeBranch(Branch), StringComparison.OrdinalIgnoreCase));

    /// <summary>"Build 25621760 · public · up to date" — the status card's second line.</summary>
    public string BuildSummary
    {
        get
        {
            if (InstalledBuildId is null)
            {
                return "Installed build unknown (no SteamCMD manifest in this folder)";
            }

            var text = $"Build {InstalledBuildId} · {InstalledBranch ?? "public"}";
            if (InstalledBranch is not null && !string.Equals(InstalledBranch, SteamCmd.NormalizeBranch(Branch), StringComparison.OrdinalIgnoreCase))
            {
                return $"{text} · switch to {SteamCmd.NormalizeBranch(Branch)} with Update";
            }

            return LatestBuildId switch
            {
                null => text,
                { } latest when latest == InstalledBuildId => $"{text} · up to date",
                { } latest => $"{text} · update available (build {latest})",
            };
        }
    }

    private void ReadInstalledBuild()
    {
        LatestBuildId = null;
        if (SelectedProfile is not { } p || !File.Exists(DedicatedServerLayout.AppManifestPath(p.InstallFolder)))
        {
            InstalledBuildId = null;
            InstalledBranch = null;
            return;
        }

        try
        {
            var (buildId, branch) = SteamCmd.ReadAppManifest(File.ReadAllText(DedicatedServerLayout.AppManifestPath(p.InstallFolder)));
            InstalledBuildId = buildId;
            InstalledBranch = branch;
        }
        catch (IOException)
        {
            InstalledBuildId = null;
            InstalledBranch = null;
        }
    }

    private bool CanUseSteamCmd() => _steamCmd is not null && !IsSteamCmdBusy && !IsBusy;

    private bool CanUpdate() => CanUseSteamCmd() && SelectedProfile is not null;

    /// <summary>Downloads a brand-new server with SteamCMD into a folder you pick (a subfolder when it isn't empty), then adds it here.</summary>
    [RelayCommand(CanExecute = nameof(CanUseSteamCmd))]
    private async Task InstallNewServerAsync()
    {
        var picked = _pickFolder("Choose where to install the new server (an empty folder, or one to create it inside)");
        if (string.IsNullOrWhiteSpace(picked) || _steamCmd is null)
        {
            return;
        }

        if (DedicatedServerLayout.FindInstallRoot(picked) is not null)
        {
            StatusMessage = "That folder already holds a server — use 'Add existing server…' instead.";
            return;
        }

        var target = Directory.Exists(picked) && Directory.EnumerateFileSystemEntries(picked).Any()
            ? Path.Combine(picked, "IcarusServer")
            : picked;
        if (!_dialogService.Confirm(
                $"Download the Icarus dedicated server into\n{target}\n\nIt is about 10 GB, free, and needs no Icarus licence (SteamCMD, app 2089300, anonymous login). SteamCMD itself is downloaded from Valve on first use.",
                "Install a new server", ThemedConfirmSeverity.Question))
        {
            return;
        }

        var result = await RunSteamCmdAsync(() => _steamCmd.InstallOrUpdateAsync(target, "public", OnSteamCmdLine));
        if (!result.Success)
        {
            StatusMessage = $"Install failed: {result.Message}";
            return;
        }

        var profile = new DedicatedServerProfile { Name = "Icarus Server", InstallFolder = target, SteamServerName = "Icarus Server" };
        _store.Save(profile);
        Profiles.Add(profile);
        SelectedProfile = profile;
        TrySave();
        StatusMessage = $"Installed the server into '{target}' and wrote its ServerSettings.ini. Set an admin password in Settings, then Start.";
        _activityLog.Log($"Installed a new dedicated server into {target}.", ActivityEntryKind.Success);
    }

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task CheckForUpdateAsync()
    {
        if (_steamCmd is null)
        {
            return;
        }

        ReadInstalledBuild();
        SteamCmdStatus = "Asking Steam for the latest build…";
        IsSteamCmdBusy = true;
        try
        {
            LatestBuildId = await _steamCmd.GetLatestBuildIdAsync(Branch);
            SteamCmdStatus = null;
            StatusMessage = LatestBuildId is null ? "Steam didn't list a build for that branch — try again in a moment." : BuildSummary;
        }
        catch (Exception ex)
        {
            SteamCmdStatus = null;
            StatusMessage = $"Couldn't check for an update: {ex.Message}";
        }
        finally
        {
            IsSteamCmdBusy = false;
        }
    }

    /// <summary>Stops the server if needed, backs up its worlds and settings, then runs SteamCMD for the profile's branch (also how a branch switch happens).</summary>
    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task UpdateServerAsync()
    {
        if (SelectedProfile is not { } profile || _steamCmd is null || !TrySave())
        {
            return;
        }

        profile = SelectedProfile!;
        if (Running is not null)
        {
            if (!_dialogService.Confirm(
                    $"'{profile.Name}' is running. Stop it to update?\n\nConnected players are disconnected; the world saves on exit.",
                    "Update server", ThemedConfirmSeverity.Warning)
                || !await StopCoreAsync(ask: false))
            {
                return;
            }
        }

        var backup = TryBackup("before-update");
        var result = await RunSteamCmdAsync(() => _steamCmd.InstallOrUpdateAsync(profile.InstallFolder, profile.Branch, OnSteamCmdLine));
        ReadInstalledBuild();
        if (result.Success)
        {
            LatestBuildId = InstalledBuildId;
        }

        StatusMessage = result.Success
            ? $"Updated '{profile.Name}' ({BuildSummary}).{(backup is null ? "" : " Worlds and settings were backed up first.")}"
            : $"Update failed: {result.Message}";
        _activityLog.Log(StatusMessage, result.Success ? ActivityEntryKind.Success : ActivityEntryKind.Warning);
        RefreshBackups();
    }

    private async Task<SteamCmdResultView> RunSteamCmdAsync(Func<Task<Services.SteamCmdResult>> run)
    {
        IsSteamCmdBusy = true;
        SteamCmdProgress = 0;
        SteamCmdStatus = "Starting SteamCMD…";
        try
        {
            var result = await run();
            return new SteamCmdResultView(result.Success, result.Message);
        }
        catch (Exception ex)
        {
            return new SteamCmdResultView(false, ex.Message);
        }
        finally
        {
            IsSteamCmdBusy = false;
            SteamCmdStatus = null;
        }
    }

    private sealed record SteamCmdResultView(bool Success, string Message);

    /// <summary>SteamCMD output: progress lines drive the bar; every line also goes into the log panel, tagged.</summary>
    private void OnSteamCmdLine(string line)
    {
        void Apply()
        {
            if (SteamCmd.ParseProgressPercent(line) is { } pct)
            {
                SteamCmdProgress = pct;
                SteamCmdStatus = $"{SteamCmd.ParseProgressPhase(line)} {pct:0.0}%";
            }
            else if (SteamCmd.IsErrorLine(line) || line.StartsWith("[Starlink]", StringComparison.Ordinal))
            {
                SteamCmdStatus = line.Trim();
            }

            AppendLog(["[SteamCMD] " + line]);
        }

        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(Apply);
        }
        else
        {
            Apply();
        }
    }
}
