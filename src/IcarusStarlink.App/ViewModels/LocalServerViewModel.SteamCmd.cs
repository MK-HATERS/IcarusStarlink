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

    // ---- Which SteamCMD: Starlink's own copy (installed on request) or one the user already has ----

    public bool HasSteamCmd => _steamCmd?.CurrentExePath is not null;

    /// <summary>True when the setting points at the user's own steamcmd.exe rather than Starlink's copy.</summary>
    public bool UsingOwnSteamCmd =>
        _steamCmd?.CurrentExePath is { } exe && _settings?.Current.SteamCmdExePath is { Length: > 0 } chosen
        && string.Equals(exe, chosen, StringComparison.OrdinalIgnoreCase);

    public string SteamCmdSummary => _steamCmd?.CurrentExePath is { } exe
        ? $"SteamCMD: {exe}"
        : "SteamCMD isn't set up yet — install it into Starlink's folder, or point at one you already have.";

    /// <summary>
    /// SteamCMD is only needed to install or update a server. With no server yet the full box
    /// (summary + Install SteamCMD / Use my SteamCMD…) shows, since installing one is the next
    /// step; once servers exist and SteamCMD still isn't set up, it folds to one line that opens
    /// the box (Set up…). Set up, the box shows which steamcmd.exe is used.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSteamCmdBox), nameof(ShowSteamCmdLine))]
    private bool _steamCmdSetupExpanded;

    public bool ShowSteamCmdBox => HasSteamCmd || Profiles.Count == 0 || SteamCmdSetupExpanded;

    public bool ShowSteamCmdLine => !ShowSteamCmdBox;

    [RelayCommand]
    private void ExpandSteamCmdSetup() => SteamCmdSetupExpanded = true;

    private void RaiseSteamCmdChanged()
    {
        OnPropertyChanged(nameof(HasSteamCmd));
        OnPropertyChanged(nameof(UsingOwnSteamCmd));
        OnPropertyChanged(nameof(SteamCmdSummary));
        OnPropertyChanged(nameof(ShowSteamCmdBox));
        OnPropertyChanged(nameof(ShowSteamCmdLine));
    }

    /// <summary>Downloads SteamCMD from Valve into a SteamCMD folder in Starlink's own folder.</summary>
    [RelayCommand(CanExecute = nameof(CanUseSteamCmd))]
    private async Task InstallSteamCmdAsync()
    {
        if (_steamCmd is null)
        {
            return;
        }

        if (!_dialogService.Confirm(
                $"Download SteamCMD from Valve into\n{_steamCmd.OwnInstallFolder}?\n\nIt is small (a few MB) and updates itself on first run. To use a SteamCMD you already have instead, click 'Use my SteamCMD…'.",
                "Install SteamCMD", ThemedConfirmSeverity.Question))
        {
            return;
        }

        await InstallOwnSteamCmdCoreAsync();
    }

    private async Task<bool> InstallOwnSteamCmdCoreAsync()
    {
        StartSteamCmdOutput();
        IsSteamCmdBusy = true;
        SteamCmdProgress = 0;
        SteamCmdStatus = "Downloading SteamCMD…";
        try
        {
            if (_settings is { } settings && settings.Current.SteamCmdExePath is not null)
            {
                settings.Current.SteamCmdExePath = null;
                settings.Save();
            }

            await _steamCmd!.InstallOwnCopyAsync(OnSteamCmdLine);
            StatusMessage = $"SteamCMD installed in {_steamCmd.OwnInstallFolder}.";
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't install SteamCMD: {ex.Message}";
            return false;
        }
        finally
        {
            IsSteamCmdBusy = false;
            SteamCmdStatus = null;
            RaiseSteamCmdChanged();
        }
    }

    /// <summary>Points Starlink at a steamcmd.exe the user already has (for example the one their update scripts use).</summary>
    [RelayCommand]
    private void UseExistingSteamCmd()
    {
        var picked = _pickSteamCmdExe();
        if (string.IsNullOrWhiteSpace(picked))
        {
            return;
        }

        SetSteamCmdPath(picked, $"Using your SteamCMD: {picked}");
    }

    private bool SetSteamCmdPath(string exePath, string successMessage)
    {
        if (!File.Exists(exePath) || !string.Equals(Path.GetFileName(exePath), "steamcmd.exe", StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = $"'{exePath}' isn't steamcmd.exe.";
            return false;
        }

        if (_settings is null)
        {
            return false;
        }

        _settings.Current.SteamCmdExePath = exePath;
        _settings.Save();
        StatusMessage = successMessage;
        RaiseSteamCmdChanged();
        return true;
    }

    /// <summary>Before any SteamCMD action: if none is set up, ask to install Starlink's copy (the user can cancel and point at their own).</summary>
    private async Task<bool> EnsureSteamCmdReadyAsync()
    {
        if (_steamCmd is null)
        {
            return false;
        }

        if (HasSteamCmd)
        {
            return true;
        }

        if (!_dialogService.Confirm(
                $"SteamCMD isn't set up yet. Download it from Valve into\n{_steamCmd.OwnInstallFolder}?\n\nIf you already have SteamCMD, click No, then 'Use my SteamCMD…'.",
                "SteamCMD needed", ThemedConfirmSeverity.Question))
        {
            // The box under the server list opens so both ways to set it up are right there.
            SteamCmdSetupExpanded = true;
            StatusMessage = "SteamCMD is needed to install or update a server. Set it up under the server list: Install SteamCMD, or Use my SteamCMD… for one you already have.";
            return false;
        }

        return await InstallOwnSteamCmdCoreAsync();
    }

    /// <summary>An adopted server's Steam manifest names the steamcmd.exe that installed it — use that when none is chosen yet.</summary>
    private string? AdoptInstallingSteamCmd(string installFolder)
    {
        if (_steamCmd is null || HasSteamCmd || !File.Exists(DedicatedServerLayout.AppManifestPath(installFolder)))
        {
            return null;
        }

        try
        {
            var launcher = SteamCmd.ReadLauncherPath(File.ReadAllText(DedicatedServerLayout.AppManifestPath(installFolder)));
            return launcher is not null && File.Exists(launcher) && SetSteamCmdPath(launcher, "") ? launcher : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

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
                $"Download the Icarus dedicated server into\n{target}\n\nIt is about 10 GB, free, and needs no Icarus licence (SteamCMD, app 2089300, anonymous login).",
                "Install a new server", ThemedConfirmSeverity.Question)
            || !await EnsureSteamCmdReadyAsync())
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
        SaveToStore(profile);
        Profiles.Add(profile);
        SelectedProfile = profile;
        TrySave();
        StatusMessage = $"Installed the server into '{target}' and wrote its ServerSettings.ini. Set an admin password in Settings, then Start.";
        _activityLog.Log($"Installed a new dedicated server into {target}.", ActivityEntryKind.Success);
    }

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task CheckForUpdateAsync()
    {
        if (_steamCmd is null || !await EnsureSteamCmdReadyAsync())
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
        if (SelectedProfile is not { } profile || _steamCmd is null || !TrySave() || !await EnsureSteamCmdReadyAsync())
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

    /// <summary>SteamCMD's output for the current (or last) run, shown in the SteamCMD box itself — the Log tab only exists once a server is in the list, which it isn't during a first install.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> SteamCmdOutput { get; } = [];

    /// <summary>The SteamCMD box shows while SteamCMD runs, and afterwards until dismissed so the result can be read.</summary>
    public bool ShowSteamCmdPanel => IsSteamCmdBusy || SteamCmdOutput.Count > 0;

    /// <summary>Computed inverse of IsSteamCmdBusy (this project binds inverses as properties, not converter parameters).</summary>
    public bool IsSteamCmdIdle => !IsSteamCmdBusy;

    partial void OnIsSteamCmdBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSteamCmdPanel));
        OnPropertyChanged(nameof(IsSteamCmdIdle));
    }

    [RelayCommand]
    private void DismissSteamCmdOutput()
    {
        SteamCmdOutput.Clear();
        OnPropertyChanged(nameof(ShowSteamCmdPanel));
    }

    private void StartSteamCmdOutput()
    {
        SteamCmdOutput.Clear();
        OnPropertyChanged(nameof(ShowSteamCmdPanel));
    }

    private async Task<SteamCmdResultView> RunSteamCmdAsync(Func<Task<Services.SteamCmdResult>> run)
    {
        StartSteamCmdOutput();
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

    /// <summary>The view scrolls the SteamCMD box to its newest line.</summary>
    public event EventHandler? SteamCmdOutputAppended;

    private sealed record SteamCmdResultView(bool Success, string Message);

    /// <summary>SteamCMD output: progress lines drive the bar; every line also goes into the log panel, tagged.</summary>
    private void OnSteamCmdLine(string line) => OnSteamCmdLineFor(SelectedProfile?.Id, line);

    /// <summary>Automation can update a server that isn't the selected one: its SteamCMD lines go to the SteamCMD box, but not into another server's log.</summary>
    private void OnSteamCmdLineFor(Guid? profileId, string line)
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

            // A progress line replaces the previous one instead of stacking hundreds of them.
            if (SteamCmd.ParseProgressPercent(line) is not null && SteamCmdOutput.Count > 0 && SteamCmd.ParseProgressPercent(SteamCmdOutput[^1]) is not null)
            {
                SteamCmdOutput[^1] = line.Trim();
            }
            else
            {
                SteamCmdOutput.Add(line.Trim());
                while (SteamCmdOutput.Count > 300)
                {
                    SteamCmdOutput.RemoveAt(0);
                }
            }

            OnPropertyChanged(nameof(ShowSteamCmdPanel));
            SteamCmdOutputAppended?.Invoke(this, EventArgs.Empty);
            if (profileId is null || profileId == SelectedProfile?.Id)
            {
                AppendLog(["[SteamCMD] " + line]);
            }
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
