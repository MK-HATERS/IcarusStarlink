using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Views;
using IcarusStarlink.Catalog.GitHub;
using IcarusStarlink.Catalog.Ue4ss;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.IO;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class SettingsViewModel
{
    [ObservableProperty]
    private bool _isUe4ssInstalled;

    [ObservableProperty]
    private string? _ue4ssInstalledVersion;

    /// <summary>The UE4SS config tab (UE4SS-settings.ini for the game and Starlink's servers). Set by the app; null in tests that don't need it.</summary>
    public Ue4ssConfigViewModel? Ue4ssConfig { get; init; }

    /// <summary>The installed build's git commit (from UE4SS.log or Starlink's install marker): what tells "3.0.1" builds apart.</summary>
    [ObservableProperty]
    private string? _ue4ssInstalledCommit;

    /// <summary>The newest UE4SS dev build on GitHub. Null until the user presses Check for updates (never checked on its own) or when GitHub didn't answer; Install then uses the bundled build.</summary>
    [ObservableProperty]
    private Ue4ssReleaseInfo? _ue4ssLatestRelease;

    /// <summary>How the build Install would put in compares with the installed one, as worked out by the last manual check (Unknown before one).</summary>
    [ObservableProperty]
    private Ue4ssBuildOrder _ue4ssCheckedOrder;

    [ObservableProperty]
    private bool _hasCheckedUe4ssRelease;

    [ObservableProperty]
    private bool _isCheckingUe4ssRelease;

    [ObservableProperty]
    private bool _isInstallingUe4ss;

    /// <summary>
    /// Install/Update and Uninstall both write into the exact same real game files (dwmapi.dll,
    /// Binaries\Win64\ue4ss\), so they must never run concurrently with each other OR with
    /// themselves (a double-click) — CanInstallOrUpdateUe4ss/CanUninstallUe4ss both check both this
    /// AND IsInstallingUe4ss for real mutual exclusion in both directions.
    /// </summary>
    [ObservableProperty]
    private bool _isUninstallingUe4ss;

    [ObservableProperty]
    private string? _ue4ssStatusMessage;

    /// <summary>The game has UE4SS set up the old way (its files directly in Binaries\Win64, not in the ue4ss folder): Install moves the mods and settings over.</summary>
    [ObservableProperty]
    private bool _ue4ssHasOldLayout;

    /// <summary>Which build the game and each of Starlink's servers has, one sentence each; null when there are no servers (the status line already names the game's).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUe4ssInstalledBuildsText))]
    private string? _ue4ssInstalledBuildsText;

    public bool HasUe4ssInstalledBuildsText => !string.IsNullOrEmpty(Ue4ssInstalledBuildsText);

    /// <summary>Starlink's servers (name, install folder), for the line that says which UE4SS each has. Set by the app; null in tests that don't need it.</summary>
    public Func<IReadOnlyList<(string Name, string InstallFolder)>>? Ue4ssServers
    {
        get => _ue4ssServers;
        init
        {
            // Arrives after the constructor's first status read: say the line again with the servers.
            _ue4ssServers = value;
            RefreshInstalledBuildsText();
        }
    }

    /// <summary>The last read of the servers' folders for <see cref="Ue4ssInstalledBuildsText"/>; done when the text is set. For tests to wait on.</summary>
    internal Task Ue4ssInstalledBuildsRead { get; private set; } = Task.CompletedTask;

    private int _ue4ssInstalledBuildsReadNumber;

    private readonly Func<IReadOnlyList<(string Name, string InstallFolder)>>? _ue4ssServers;

    private const string NewestDevNotAskedLabel = "Newest dev build (asks GitHub when you install)";

    /// <summary>
    /// The UE4SS tab's "Which UE4SS to install" list. Always: the build inside Starlink (no
    /// download) and GitHub's newest dev build. Show older builds adds the archived dev builds and
    /// the official 3.0.1. A zip the user has is its own button. GitHub is only asked on a click.
    /// </summary>
    public ObservableCollection<Ue4ssBuildChoice> Ue4ssBuildChoices { get; } =
    [
        new(Ue4ssBuildChoiceKind.Bundled, $"Built into Starlink: {BundledUe4ss.Label} (no download)"),
        new(Ue4ssBuildChoiceKind.NewestDev, NewestDevNotAskedLabel),
    ];

    private Ue4ssBuildChoice BundledUe4ssChoice => Ue4ssBuildChoices[0];

    private Ue4ssBuildChoice NewestDevUe4ssChoice => Ue4ssBuildChoices[1];

    private Ue4ssBuildChoice? _selectedUe4ssBuildChoice;

    /// <summary>The build Install puts in. Starts on the one inside Starlink; a list that hands back nothing means that one too.</summary>
    public Ue4ssBuildChoice SelectedUe4ssBuildChoice
    {
        get => _selectedUe4ssBuildChoice ?? BundledUe4ssChoice;
        set
        {
            var choice = value ?? BundledUe4ssChoice;
            if (ReferenceEquals(choice, SelectedUe4ssBuildChoice))
            {
                return;
            }

            _selectedUe4ssBuildChoice = choice;
            // What GitHub said about the last build's age says nothing about this one.
            Ue4ssCheckedOrder = Ue4ssBuildOrder.Unknown;
            OnPropertyChanged();
            NotifyUe4ssLabels();
        }
    }

    [ObservableProperty]
    private bool _isLoadingOlderUe4ssBuilds;

    /// <summary>The installed build in words: "4.0.0 (official release)", "3.0.1 dev build 1c1a1497", or "4.0.0 dev build 6" when it's a build Starlink knows the number of.</summary>
    public string Ue4ssInstalledLabel => Ue4ssBuild.Describe(Ue4ssInstalledVersion, Ue4ssInstalledCommit, KnownBuildNumber(Ue4ssInstalledCommit));

    public string Ue4ssStatusText
    {
        get
        {
            if (Ue4ssHasOldLayout)
            {
                var old = Ue4ssInstalledVersion is null ? "UE4SS" : $"UE4SS {Ue4ssInstalledLabel}";
                return $"{old} is set up the old way, with its files directly in Binaries\\Win64. Install moves your mods and settings into the ue4ss folder, where UE4SS 4 looks for them.";
            }

            if (!IsUe4ssInstalled)
            {
                return $"Not installed. Starlink comes with UE4SS {BundledUe4ss.Label}.";
            }

            if (Ue4ssInstalledVersion is null)
            {
                return "Installed (its build shows once the game has started with it).";
            }

            return Ue4ssInstallOrder switch
            {
                Ue4ssBuildOrder.Same when HasCheckedUe4ssRelease && SelectedUe4ssBuildChoice.Kind == Ue4ssBuildChoiceKind.NewestDev => $"{Ue4ssInstalledLabel} installed (up to date).",
                Ue4ssBuildOrder.Newer => $"{Ue4ssInstalledLabel} installed — {Ue4ssInstallLabel} available.",
                Ue4ssBuildOrder.Older => $"{Ue4ssInstalledLabel} installed (newer than {Ue4ssInstallLabel}).",
                Ue4ssBuildOrder.Unknown when HasCheckedUe4ssRelease && SelectedUe4ssBuildChoice.Kind == Ue4ssBuildChoiceKind.NewestDev => $"{Ue4ssInstalledLabel} installed. The newest is {Ue4ssInstallLabel}.",
                _ => $"{Ue4ssInstalledLabel} installed.",
            };
        }
    }

    /// <summary>Install puts in the build picked in the list: the one inside Starlink (no download) unless the user picked another, or a manual check found a newer dev build.</summary>
    public string Ue4ssInstallButtonLabel => !IsUe4ssInstalled
        ? $"Install {Ue4ssInstallLabel}"
        : Ue4ssInstallOrder switch
        {
            Ue4ssBuildOrder.Same => "Reinstall",
            Ue4ssBuildOrder.Newer => $"Update to {Ue4ssInstallLabel}",
            Ue4ssBuildOrder.Older => $"Install {Ue4ssInstallLabel} (older)",
            _ => $"Install {Ue4ssInstallLabel}",
        };

    /// <summary>The GitHub download Install would use; null for the build inside Starlink, and for the newest dev build until GitHub has been asked which one that is.</summary>
    private Ue4ssReleaseInfo? Ue4ssChosenRelease => UseGitHubUe4ss ? SelectedUe4ssBuildChoice.Release : null;

    /// <summary>The build Install would put in, in words.</summary>
    private string Ue4ssInstallLabel => !UseGitHubUe4ss ? BundledUe4ss.Label
        : Ue4ssChosenRelease is { } release ? Ue4ssBuild.Describe(release.Version, release.Commit, release.BuildNumber)
        : "the newest dev build";

    private string? Ue4ssInstallVersion => UseGitHubUe4ss ? Ue4ssChosenRelease?.Version : BundledUe4ss.Version;

    private string? Ue4ssInstallCommit => UseGitHubUe4ss ? Ue4ssChosenRelease?.Commit : BundledUe4ss.Commit;

    private int? Ue4ssInstallBuildNumber => UseGitHubUe4ss ? Ue4ssChosenRelease?.BuildNumber : BundledUe4ss.BuildNumber;

    /// <summary>Download unless the pick is the build inside Starlink, or turns out to be that very build (then the copy inside is used).</summary>
    private bool UseGitHubUe4ss => SelectedUe4ssBuildChoice is { Kind: not Ue4ssBuildChoiceKind.Bundled } choice
        && !Ue4ssBuild.SameCommit(choice.Release?.Commit, BundledUe4ss.Commit);

    /// <summary>
    /// How the build Install would put in compares with the installed one. The version and build
    /// number answer most of it without GitHub (4.0.0 is newer than any 3.0.1 build); what is left
    /// is what the last manual check learned from the two commits' dates.
    /// </summary>
    private Ue4ssBuildOrder Ue4ssInstallOrder
    {
        get
        {
            var known = Ue4ssBuild.Compare(
                Ue4ssInstalledVersion, Ue4ssInstalledCommit, KnownBuildNumber(Ue4ssInstalledCommit),
                Ue4ssInstallVersion, Ue4ssInstallCommit, Ue4ssInstallBuildNumber);
            return known != Ue4ssBuildOrder.Unknown || Ue4ssInstalledCommit is null || Ue4ssInstallCommit is null ? known : Ue4ssCheckedOrder;
        }
    }

    /// <summary>The build number of a commit Starlink has seen named: the build inside it, one in the list, or a dev build an earlier Starlink came with.</summary>
    private int? KnownBuildNumber(string? commit)
    {
        if (Ue4ssBuild.SameCommit(commit, BundledUe4ss.Commit))
        {
            return BundledUe4ss.BuildNumber;
        }

        foreach (var choice in Ue4ssBuildChoices)
        {
            if (choice.Release is { Commit: { } known, BuildNumber: { } number } && Ue4ssBuild.SameCommit(commit, known))
            {
                return number;
            }
        }

        return Ue4ssBuild.ShippedBuildNumber(commit);
    }

    private void NotifyUe4ssLabels()
    {
        OnPropertyChanged(nameof(Ue4ssInstalledLabel));
        OnPropertyChanged(nameof(Ue4ssStatusText));
        OnPropertyChanged(nameof(Ue4ssInstallButtonLabel));
    }

    partial void OnUe4ssHasOldLayoutChanged(bool value) => NotifyUe4ssLabels();

    partial void OnUe4ssInstalledCommitChanged(string? value) => NotifyUe4ssLabels();

    partial void OnUe4ssCheckedOrderChanged(Ue4ssBuildOrder value) => NotifyUe4ssLabels();

    partial void OnHasCheckedUe4ssReleaseChanged(bool value) => NotifyUe4ssLabels();

    /// <summary>Never waits on GitHub: the bundled copy is always there.</summary>
    public bool CanInstallOrUpdateUe4ss => !IsInstallingUe4ss && !IsUninstallingUe4ss;

    public bool CanUninstallUe4ss => !IsInstallingUe4ss && !IsUninstallingUe4ss;

    partial void OnIsUe4ssInstalledChanged(bool value) => NotifyUe4ssLabels();

    partial void OnUe4ssInstalledVersionChanged(string? value) => NotifyUe4ssLabels();

    partial void OnUe4ssLatestReleaseChanged(Ue4ssReleaseInfo? value)
    {
        // The list's "Newest dev build" line now names the build.
        NewestDevUe4ssChoice.Release = value;
        NewestDevUe4ssChoice.Label = value is null
            ? NewestDevNotAskedLabel
            : $"Newest dev build: {Ue4ssBuild.Describe(value.Version, value.Commit, value.BuildNumber)} (download)";
        NotifyUe4ssLabels();
        OnPropertyChanged(nameof(CanInstallOrUpdateUe4ss));
    }

    partial void OnIsInstallingUe4ssChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInstallOrUpdateUe4ss));
        OnPropertyChanged(nameof(CanUninstallUe4ss));
    }

    partial void OnIsUninstallingUe4ssChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInstallOrUpdateUe4ss));
        OnPropertyChanged(nameof(CanUninstallUe4ss));
    }

    /// <summary>
    /// The game folder UE4SS is installed into, checked and removed from: the SAVED one. Browse and
    /// Auto-detect only fill the box until Save settings (the page's model), but UE4SS used the box,
    /// so it could install into a folder the rest of the app wasn't using.
    /// </summary>
    private string? Ue4ssContentPath => string.IsNullOrWhiteSpace(_settingsService.Current.IcarusContentPath) ? null : _settingsService.Current.IcarusContentPath;

    private const string Ue4ssNeedsGameFolder = "Set the Icarus Content folder and click Save settings first.";

    /// <summary>
    /// A UE4SS refusal or failure: on this page's status line and in the activity log (a pop-up),
    /// since the Library's "Install UE4SS" bar runs these commands with Settings not showing, and
    /// used to give no answer at all.
    /// </summary>
    /// <summary>The player's UE4SS settings and mods as they are before an install (null when the game has no ue4ss folder yet).</summary>
    private static Task<Ue4ssSetupSnapshot?> TakeUe4ssSetupAsync(string contentPath) =>
        Task.Run(() => Ue4ssSetupSnapshot.TryTake(Ue4ssGamePaths.ResolveLoaderFolder(contentPath)));

    /// <summary>
    /// After an install: whether the player's settings and mods are as they were, and where the
    /// UE4SS they had is kept. Says nothing for a first install, or for an old setup that was moved
    /// (the move describes itself).
    /// </summary>
    private async Task<Ue4ssSetupCheck> CheckUe4ssSetupAsync(Ue4ssSetupSnapshot? before, Ue4ssOldLayout.Outcome movedOldSetup, string contentPath)
    {
        if (before is null || movedOldSetup.Changed)
        {
            return Ue4ssSetupCheck.Nothing;
        }

        var check = await Task.Run(() => before.CheckAgainst(Ue4ssGamePaths.ResolveLoaderFolder(contentPath)));
        return check with { Summary = $"{check.Summary} The UE4SS you had before is in {Path.Combine(_backupDirectory, "UE4SS-Loader")}." };
    }

    private void ReportUe4ssProblem(string status, string? logged = null)
    {
        Ue4ssStatusMessage = status;
        _activityLog.Log(logged ?? status, ActivityEntryKind.Warning);
    }

    private void RefreshUe4ssStatus()
    {
        if (Ue4ssContentPath is not { } contentPath)
        {
            IsUe4ssInstalled = false;
            Ue4ssHasOldLayout = false;
            Ue4ssInstalledVersion = null;
            Ue4ssInstalledCommit = null;
            RefreshInstalledBuildsText();
            return;
        }

        try
        {
            var status = _ue4ssLoaderInstallService.GetStatus(contentPath);
            IsUe4ssInstalled = status.IsInstalled;
            Ue4ssHasOldLayout = status.HasOldLayout;
            Ue4ssInstalledVersion = status.InstalledVersion;
            Ue4ssInstalledCommit = status.InstalledCommit;
        }
        catch (Exception)
        {
            // A path with no parent (a drive root picked in Browse) makes the game-path resolver
            // throw, and from inside a property-changed hook that took the whole app down. Not a
            // real install = not installed.
            IsUe4ssInstalled = false;
            Ue4ssHasOldLayout = false;
            Ue4ssInstalledVersion = null;
            Ue4ssInstalledCommit = null;
        }

        RefreshInstalledBuildsText();
    }

    /// <summary>
    /// Sets <see cref="Ue4ssInstalledBuildsText"/>: "Your game has 4.0.0 (official release). Server
    /// Home has 3.0.1 dev build 1181." Null when Starlink has no servers. Each server's folder and
    /// UE4SS.log are read off the UI thread (a server can be on a network or sleeping drive); the
    /// text is set when that read is done, and a read overtaken by a newer one sets nothing.
    /// </summary>
    private void RefreshInstalledBuildsText()
    {
        IReadOnlyList<(string Name, string InstallFolder)> servers;
        try
        {
            servers = _ue4ssServers?.Invoke() ?? [];
        }
        catch (Exception)
        {
            servers = [];
        }

        var readNumber = ++_ue4ssInstalledBuildsReadNumber;
        if (servers.Count == 0)
        {
            Ue4ssInstalledBuildsText = null;
            return;
        }

        Ue4ssInstalledBuildsRead = ReadAsync();

        async Task ReadAsync()
        {
            var builds = await Task.Run(() => servers.Select(server => (server.Name, Build: ReadServerBuild(server.InstallFolder))).ToList());
            if (readNumber != _ue4ssInstalledBuildsReadNumber)
            {
                return;
            }

            var game = Ue4ssHasOldLayout ? "an old UE4SS setup"
                : !IsUe4ssInstalled ? "no UE4SS"
                : Ue4ssInstalledVersion is null ? "UE4SS (its build shows once the game has started with it)"
                : Ue4ssInstalledLabel;
            var parts = new List<string> { $"Your game has {game}." };
            foreach (var (name, build) in builds)
            {
                var has = build.Other ?? Ue4ssBuild.Describe(build.Version, build.Commit, KnownBuildNumber(build.Commit));
                parts.Add($"Server {name} has {has}.");
            }

            parts.Add("To give a server your game's UE4SS, use Install UE4SS from my game on the Server page.");
            Ue4ssInstalledBuildsText = string.Join(" ", parts);
        }
    }

    /// <summary>What a server's folder says about its UE4SS: the build's version and commit, or, in Other, the words for a server with no build to name. Reads disk; never throws.</summary>
    private static (string? Version, string? Commit, string? Other) ReadServerBuild(string installFolder)
    {
        try
        {
            var win64 = IcarusStarlink.Core.Server.DedicatedServerLayout.Win64Folder(installFolder);
            if (!IcarusStarlink.Core.Server.DedicatedServerLayout.IsUe4ssInstalled(installFolder))
            {
                return (null, null, Ue4ssOldLayout.Find(win64) is null ? "no UE4SS" : "an old UE4SS setup");
            }

            var (version, commit) = Ue4ssInstalledBuild.Read(Path.Combine(win64, "ue4ss"));
            return version is null ? (null, null, "UE4SS (its build shows once the server has started with it)") : (version, commit, null);
        }
        catch (Exception)
        {
            return (null, null, "a folder Starlink couldn't read");
        }
    }

    /// <summary>
    /// Only when the user asks (the UE4SS tab's button, or the app's own Check for updates):
    /// fetches GitHub's newest UE4SS dev build and works out whether it is newer than the
    /// installed one. Never installs anything.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task CheckForUe4ssUpdateAsync()
    {
        if (IsCheckingUe4ssRelease)
        {
            return;
        }

        IsCheckingUe4ssRelease = true;
        try
        {
            RefreshUe4ssStatus();

            if (await FetchNewestDevBuildAsync() is not { } latest)
            {
                return;
            }

            // Offer it when it is newer than the build inside Starlink (or Starlink can't tell);
            // when the one inside is as new or newer, the list stays where the user left it.
            var againstBundled = Ue4ssBuild.Compare(
                BundledUe4ss.Version, BundledUe4ss.Commit, BundledUe4ss.BuildNumber, latest.Version, latest.Commit, latest.BuildNumber);
            if (againstBundled is Ue4ssBuildOrder.Newer or Ue4ssBuildOrder.Unknown)
            {
                SelectedUe4ssBuildChoice = NewestDevUe4ssChoice;
            }

            await CompareChosenBuildByDateAsync();
            HasCheckedUe4ssRelease = true;

            var newest = Ue4ssBuild.Describe(latest.Version, latest.Commit, latest.BuildNumber);
            var installOrder = Ue4ssInstallOrder;
            Ue4ssStatusMessage = !IsUe4ssInstalled ? $"The newest UE4SS dev build is {newest}."
                : SelectedUe4ssBuildChoice.Kind != Ue4ssBuildChoiceKind.NewestDev
                    ? $"The newest UE4SS dev build is {newest}. The one built into Starlink ({BundledUe4ss.Label}) is as new or newer."
                : installOrder switch
                {
                    Ue4ssBuildOrder.Same => "You have the newest UE4SS dev build.",
                    Ue4ssBuildOrder.Newer => $"UE4SS {newest} is available.",
                    Ue4ssBuildOrder.Older => $"Your UE4SS is newer than the newest dev build on GitHub ({newest}).",
                    _ => $"The newest UE4SS is {newest}. Starlink couldn't tell how yours compares.",
                };
        }
        finally
        {
            IsCheckingUe4ssRelease = false;
        }
    }

    /// <summary>Asks GitHub which dev build is the newest and names it in the list. Null (with the reason on the status line) when GitHub didn't answer.</summary>
    private async Task<Ue4ssReleaseInfo?> FetchNewestDevBuildAsync()
    {
        var latest = await _ue4ssReleaseClient.GetLatestDevBuildAsync();
        if (latest is null)
        {
            Ue4ssStatusMessage = GitHubApiCache.RateLimitMessage is { } limit
                ? $"Couldn't check for a newer UE4SS. {limit}"
                : "Couldn't check for a newer UE4SS: GitHub didn't answer (offline, or GitHub is down).";
            return null;
        }

        Ue4ssLatestRelease = latest;
        return latest;
    }

    /// <summary>When the version and build number can't say whether the picked build is newer than the installed one, asks GitHub for the two commits' dates.</summary>
    private async Task CompareChosenBuildByDateAsync()
    {
        var undecided = Ue4ssBuild.Compare(
            Ue4ssInstalledVersion, Ue4ssInstalledCommit, KnownBuildNumber(Ue4ssInstalledCommit),
            Ue4ssInstallVersion, Ue4ssInstallCommit, Ue4ssInstallBuildNumber) == Ue4ssBuildOrder.Unknown;
        Ue4ssCheckedOrder = undecided && Ue4ssInstalledCommit is { } installed && Ue4ssInstallCommit is { } candidate
            ? await _ue4ssReleaseClient.CompareBuildsAsync(installed, candidate)
            : Ue4ssBuildOrder.Unknown;
    }

    /// <summary>
    /// "Show older builds": asks GitHub (only on this click) for the builds it still keeps, the
    /// archived dev builds and the official 3.0.1, and adds them to the list. Installs nothing.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ShowOlderUe4ssBuildsAsync()
    {
        if (IsLoadingOlderUe4ssBuilds)
        {
            return;
        }

        IsLoadingOlderUe4ssBuilds = true;
        try
        {
            var builds = await _ue4ssReleaseClient.GetOlderBuildsAsync();
            if (builds.Count == 0)
            {
                Ue4ssStatusMessage = GitHubApiCache.RateLimitMessage is { } limit
                    ? $"Couldn't list the older UE4SS builds. {limit}"
                    : "Couldn't list the older UE4SS builds: GitHub didn't answer (offline, or GitHub is down).";
                return;
            }

            var added = 0;
            foreach (var build in builds)
            {
                if (Ue4ssBuild.SameCommit(build.Commit, BundledUe4ss.Commit)
                    || Ue4ssBuildChoices.Any(c => Ue4ssBuild.SameCommit(c.Release?.Commit, build.Commit)))
                {
                    continue; // already in the list
                }

                // The archive also keeps dev builds made after the one inside Starlink.
                var older = Ue4ssBuild.Compare(
                    BundledUe4ss.Version, BundledUe4ss.Commit, BundledUe4ss.BuildNumber, build.Version, build.Commit, build.BuildNumber) == Ue4ssBuildOrder.Older;
                var name = Ue4ssBuild.Describe(build.Version, build.Commit, build.BuildNumber);
                Ue4ssBuildChoices.Add(new Ue4ssBuildChoice(Ue4ssBuildChoiceKind.Archived, $"{(older ? "Older" : "Dev build")}: {name} (download)", build));
                added++;
            }

            // A build number learned here may name the installed build.
            NotifyUe4ssLabels();
            RefreshInstalledBuildsText();
            Ue4ssStatusMessage = added == 0
                ? "The older builds are already in the list."
                : $"Added {added} {(added == 1 ? "build" : "builds")} to the list. Pick one, then press Install.";
        }
        finally
        {
            IsLoadingOlderUe4ssBuilds = false;
        }
    }

    /// <summary>
    /// What an install changes for the player, for the confirmation (notes, never a refusal): an
    /// old setup's mods and settings moving into the ue4ss folder, and, when the first number of the
    /// version changes (3 to 4), the C++ mods that may not load until their authors update them.
    /// Without a version change it is the usual C++ mod warning.
    /// </summary>
    private string Ue4ssChangeNotes(string contentPath, Ue4ssOldLayout.Found? old, string? targetVersion, bool changing)
    {
        var notes = "";
        if (old is not null)
        {
            var what = old.UserMods.Count switch
            {
                0 => "your UE4SS settings",
                1 => $"your mod {old.UserMods[0]}, whether it is on, and your UE4SS settings",
                _ => $"your {old.UserMods.Count} mods ({string.Join(", ", old.UserMods)}), which ones are on, and your UE4SS settings",
            };
            notes += "Your UE4SS is set up the old way, with its files directly in Binaries\\Win64. UE4SS 4 looks in the ue4ss folder instead, " +
                     $"so Starlink moves {what} there. The old setup is backed up first and moved back if the install fails. " +
                     "A mod or settings file the ue4ss folder already has one of stays where it is. " +
                     "UE4SS's own old mods (Keybinds, BPModLoaderMod and the like) are replaced by the new build's; " +
                     "if you changed one, your copy is in the backup.\n\n";
        }

        if (Ue4ssBuild.Major(Ue4ssInstalledVersion) is { } from && Ue4ssBuild.Major(targetVersion) is { } to && from != to)
        {
            IReadOnlyList<string> native;
            try
            {
                native = _ue4ssLoaderInstallService.ListNativeMods(contentPath);
            }
            catch (Exception)
            {
                native = [];
            }

            // Going down (4 to 3) nobody is waiting on an author: a mod made for the newer UE4SS may not load on the older one.
            var nativeNote = native.Count == 0 ? ""
                : to < from ? $"C++ mods made for UE4SS {from} may not load on UE4SS {to}. Your C++ mods: {string.Join(", ", native)}. "
                : $"These C++ mods may not load until their authors update them for UE4SS {to}: {string.Join(", ", native)}. ";
            notes += $"This changes UE4SS {from} to UE4SS {to}. All your mods are kept. " +
                     nativeNote +
                     "Play once, then look at the Library: each UE4SS mod shows Started or Didn't start, so you can see what works. " +
                     "If something breaks, the UE4SS you have now is in Backups\\UE4SS-Loader.\n\n";
            return notes;
        }

        return notes + NativeModsWarningFor(changing);
    }

    /// <summary>The old setup an install would move, for the confirmation; null when there is none or the folder can't be read right now (the install still moves what it finds).</summary>
    private Ue4ssOldLayout.Found? FindOldUe4ssSetup(string contentPath)
    {
        try
        {
            return _ue4ssLoaderInstallService.FindOldLayout(contentPath);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The confirmation's last note: what an install leaves alone. With an old setup the Mods folder meant is the ue4ss folder's, since UE4SS's own old mods are replaced.</summary>
    private static string Ue4ssKeptNote(Ue4ssOldLayout.Found? old) => old is null
        ? "Your existing UE4SS.dll/dwmapi.dll are backed up first (last 5 kept). Any mods already in your Mods folder, " +
          "and your own UE4SS-settings.ini if you've customized it, are left untouched."
        : "Your existing UE4SS.dll/dwmapi.dll are backed up first (last 5 kept). Mods and a UE4SS-settings.ini already in the ue4ss folder are left untouched.";

    /// <summary>The paragraph about the user's C++ mods for an install/update confirmation; empty when there are none or the build isn't changing.</summary>
    private string NativeModsWarningFor(bool changing)
    {
        if (!changing || !(IsUe4ssInstalled || Ue4ssHasOldLayout) || Ue4ssContentPath is not { } contentPath)
        {
            return "";
        }

        IReadOnlyList<string> native;
        try
        {
            native = _ue4ssLoaderInstallService.ListNativeMods(contentPath);
        }
        catch (Exception)
        {
            return "";
        }

        return native.Count == 0 ? ""
            : $"These mods are C++ mods: {string.Join(", ", native)}. A C++ mod is made for one UE4SS build, so after this change it may stop loading " +
              "until it's rebuilt for the new build. Lua mods aren't affected. If something breaks, the UE4SS you have now is in Backups\\UE4SS-Loader.\n\n";
    }

    /// <summary>
    /// A real, hard-to-reverse write into Binaries\Win64 — the game's own executable
    /// folder, more sensitive than either target this app has written to before (Content\Paks\mods,
    /// the UE4SS Mods folder). Same gating philosophy as the real pak install and the nxm://
    /// protocol registration: never automatic, and only proceeds behind this explicit confirmation.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task InstallOrUpdateUe4ssAsync()
    {
        // The Library's "install UE4SS" bar calls this command directly, so the button's own
        // IsEnabled isn't the only way in: never two installs (or install + uninstall) at once.
        if (IsInstallingUe4ss || IsUninstallingUe4ss)
        {
            return;
        }

        if (Ue4ssContentPath is not { } contentPath)
        {
            ReportUe4ssProblem(Ue4ssNeedsGameFolder, $"Couldn't install UE4SS: {Ue4ssNeedsGameFolder} (Settings › Game & tools)");
            return;
        }

        // "Newest dev build" picked without a check: this click is the user asking GitHub which
        // build that is, so the confirmation below can name it.
        if (SelectedUe4ssBuildChoice is { Kind: Ue4ssBuildChoiceKind.NewestDev, Release: null })
        {
            if (IsCheckingUe4ssRelease)
            {
                return;
            }

            IsCheckingUe4ssRelease = true;
            try
            {
                Ue4ssStatusMessage = "Asking GitHub for the newest dev build…";
                if (await FetchNewestDevBuildAsync() is null)
                {
                    _activityLog.Log($"Couldn't install UE4SS: {Ue4ssStatusMessage}", ActivityEntryKind.Warning);
                    return;
                }

                await CompareChosenBuildByDateAsync();
                Ue4ssStatusMessage = null;
            }
            finally
            {
                IsCheckingUe4ssRelease = false;
            }
        }

        var fromGitHub = UseGitHubUe4ss;
        var release = Ue4ssChosenRelease;
        var version = Ue4ssInstallVersion;
        var commit = Ue4ssInstallCommit;
        var label = Ue4ssInstallLabel;
        var wasInstalled = IsUe4ssInstalled;
        var order = Ue4ssInstallOrder;
        // An older build, or one Starlink can't place, isn't an "update" or a "reinstall".
        var verb = !wasInstalled ? "Install"
            : order == Ue4ssBuildOrder.Newer ? "Update"
            : order == Ue4ssBuildOrder.Same || Ue4ssInstalledVersion is null ? "Reinstall"
            : "Install";
        var current = (wasInstalled || Ue4ssHasOldLayout) && Ue4ssInstalledVersion is not null ? $"You have UE4SS {Ue4ssInstalledLabel}. " : "";
        var downgrade = order == Ue4ssBuildOrder.Older
            ? "Yours is newer: this replaces it with an older build. To keep yours, cancel.\n\n"
            : "";
        var oldSetup = FindOldUe4ssSetup(contentPath);
        var result = _dialogService.Confirm(
            current + downgrade + (fromGitHub
                ? $"This downloads UE4SS {label} from GitHub and installs it into your game's Binaries\\Win64 folder "
                : $"This installs UE4SS {label}, which comes with Starlink (no download), into your game's Binaries\\Win64 folder ") +
            "— the loader that lets Lua/scripting mods run.\n\n" +
            Ue4ssChangeNotes(contentPath, oldSetup, version, changing: order != Ue4ssBuildOrder.Same) +
            Ue4ssKeptNote(oldSetup) + "\n\n" +
            "Continue?",
            $"{verb} UE4SS", ThemedConfirmSeverity.Question);

        if (!(result))
        {
            return;
        }

        if (IsGameRunning())
        {
            ReportUe4ssProblem("Icarus is running — close it before installing UE4SS, or the game may lock the files being replaced.");
            return;
        }

        IsInstallingUe4ss = true;
        Ue4ssStatusMessage = fromGitHub ? "Downloading…" : "Preparing…";
        var tempZipPath = Path.Combine(Path.GetTempPath(), $"UE4SS_{Guid.NewGuid():N}.zip");
        try
        {
            if (fromGitHub && release is not null)
            {
                await HttpFileDownloader.DownloadToFileAsync(_httpClient, [release.DownloadUrl], tempZipPath, HttpFileDownloader.DefaultIdleTimeout);

                // Unlike this app's own self-update (AppUpdateClient), this download used to have
                // no integrity check at all — it writes a third-party DLL loader straight
                // into the game's own Binaries\Win64 folder. Same GitHubAssetIntegrity check, same
                // "null digest skips rather than fails" tolerance — see its own doc comment.
                await GitHubAssetIntegrity.VerifySha256Async(release.Digest, tempZipPath);
            }
            else
            {
                // The bundled copy checks its own hash on the way out.
                tempZipPath = await BundledUe4ss.ExtractToTempAsync();
            }

            // A SECOND, LATE check, immediately before the real write below — the first
            // IsGameRunning() check above (before the confirm dialog and the download itself) can go
            // stale for however long those take, and Icarus could be launched in that window. This
            // doesn't close the race entirely — it only shrinks the exposed window down to
            // "immediately before the write" instead of "however long the dialog+download took".
            if (IsGameRunning())
            {
                ReportUe4ssProblem("Icarus started running while this install was about to write — nothing was touched. Close it and try again.",
                    "UE4SS wasn't installed: Icarus started running just before. Close it and try again.");
                return;
            }

            Ue4ssStatusMessage = "Installing…";
            var setupBefore = await TakeUe4ssSetupAsync(contentPath);
            var movedOldSetup = await _ue4ssLoaderInstallService.InstallOrUpdateAsync(contentPath, tempZipPath, _backupDirectory, version, commit);
            var setupCheck = await CheckUe4ssSetupAsync(setupBefore, movedOldSetup, contentPath);

            RefreshUe4ssStatus();
            Ue4ssCheckedOrder = Ue4ssBuildOrder.Unknown;
            // From the same verb the confirm used: a reinstall or an older build isn't an "update".
            // What the install says it moved (and left) of an old setup follows.
            Ue4ssStatusMessage = verb switch
            {
                "Install" => $"Installed UE4SS {label}.",
                "Update" => $"Updated to UE4SS {label}.",
                _ => $"Reinstalled UE4SS {label}.",
            } + movedOldSetup.Describe() + setupCheck.Summary;
            _activityLog.Log(Ue4ssStatusMessage, setupCheck.Problems.Count > 0 ? ActivityEntryKind.Warning : ActivityEntryKind.Success);
            WeakReferenceMessenger.Default.Send(new Ue4ssLoaderChangedMessage($"UE4SS {label} is installed in the game."));
        }
        catch (Exception ex)
        {
            ReportUe4ssProblem($"{verb} failed: {ex.Message}", $"UE4SS {verb.ToLowerInvariant()} failed: {ex.Message}");
        }
        finally
        {
            IsInstallingUe4ss = false;
            try
            {
                File.Delete(tempZipPath);
            }
            catch (Exception)
            {
                // Best-effort cleanup of a temp file — leaving a stray one behind isn't worth
                // surfacing over whatever the install itself already reported.
            }
        }
    }

    /// <summary>
    /// Installs UE4SS from a zip the user downloaded (a version GitHub no longer lists as latest,
    /// or a PC offline). Same safety as Install: confirmation, game-running checks, backup first,
    /// mods and a customised UE4SS-settings.ini kept. The installer refuses a zip without
    /// UE4SS.dll and dwmapi.dll at its root before writing anything.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task InstallUe4ssFromZipAsync()
    {
        if (Ue4ssContentPath is not { } contentPath)
        {
            ReportUe4ssProblem(Ue4ssNeedsGameFolder, $"Couldn't install UE4SS: {Ue4ssNeedsGameFolder} (Settings › Game & tools)");
            return;
        }

        if (IsInstallingUe4ss || IsUninstallingUe4ss
            || _dialogService.PickFile("Pick a UE4SS release zip (for example UE4SS_v4.0.0.zip)", "UE4SS release (*.zip)|*.zip") is not { } zipPath)
        {
            return;
        }

        // A release zip keeps GitHub's name ("UE4SS_v4.0.0.zip", "UE4SS_v3.0.1-1176-g1ba38db4.zip"), which says which build it is.
        var knownBuild = Ue4ssBuild.TryParseAssetName(Path.GetFileName(zipPath), out var zipVersion, out _, out var zipCommit);
        var changesBuild = !knownBuild || !Ue4ssBuild.SameCommit(zipCommit, Ue4ssInstalledCommit);
        var oldSetup = FindOldUe4ssSetup(contentPath);
        if (!_dialogService.Confirm(
                $"Install UE4SS from\n{zipPath}\ninto your game's Binaries\\Win64 folder?\n\n" +
                Ue4ssChangeNotes(contentPath, oldSetup, knownBuild ? zipVersion : null, changesBuild) +
                Ue4ssKeptNote(oldSetup),
                "Install UE4SS from a zip", ThemedConfirmSeverity.Question))
        {
            return;
        }

        if (IsGameRunning())
        {
            ReportUe4ssProblem("Icarus is running — close it before installing UE4SS, or the game may lock the files being replaced.");
            return;
        }

        IsInstallingUe4ss = true;
        Ue4ssStatusMessage = "Installing…";
        try
        {
            var setupBefore = await TakeUe4ssSetupAsync(contentPath);
            var movedOldSetup = await _ue4ssLoaderInstallService.InstallOrUpdateAsync(
                contentPath, zipPath, _backupDirectory, knownBuild ? zipVersion : null, knownBuild ? zipCommit : null);
            var setupCheck = await CheckUe4ssSetupAsync(setupBefore, movedOldSetup, contentPath);
            RefreshUe4ssStatus();
            Ue4ssCheckedOrder = Ue4ssBuildOrder.Unknown;
            Ue4ssStatusMessage = $"Installed UE4SS{(Ue4ssInstalledVersion is not null ? $" {Ue4ssInstalledLabel}" : "")} from {Path.GetFileName(zipPath)}.{movedOldSetup.Describe()}{setupCheck.Summary}";
            _activityLog.Log(Ue4ssStatusMessage, setupCheck.Problems.Count > 0 ? ActivityEntryKind.Warning : ActivityEntryKind.Success);
            WeakReferenceMessenger.Default.Send(new Ue4ssLoaderChangedMessage(Ue4ssStatusMessage));
        }
        catch (Exception ex)
        {
            ReportUe4ssProblem($"Install failed: {ex.Message}", $"UE4SS install failed: {ex.Message}");
        }
        finally
        {
            IsInstallingUe4ss = false;
        }
    }

    /// <summary>
    /// Removes the UE4SS loader entirely (dwmapi.dll + the ue4ss folder). The safety story, in order: the user's own mods —
    /// everything NOT in UE4SS's own mods.json manifest — are moved to this app's staging first,
    /// the whole install is backed up (keep-last-5), and only then does anything get deleted.
    /// Icarus's own files are never touched; UE4SS lives entirely in those two paths.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task UninstallUe4ssAsync()
    {
        if (Ue4ssContentPath is not { } contentPath)
        {
            Ue4ssStatusMessage = Ue4ssNeedsGameFolder;
            return;
        }

        if (!IsUe4ssInstalled)
        {
            Ue4ssStatusMessage = "UE4SS isn't installed — nothing to remove.";
            return;
        }

        if (IsInstallingUe4ss || IsUninstallingUe4ss)
        {
            return;
        }

        // The user's own mods are named in the warning itself, so what's about to be preserved is
        // visible BEFORE anything happens, not discovered from a report afterwards.
        IReadOnlyList<string> userMods;
        try
        {
            userMods = _ue4ssLoaderInstallService.ListUserAddedMods(contentPath);
        }
        catch (Exception ex)
        {
            // Outside the try below, a failure here ended up only in the crash log with nothing on screen.
            Ue4ssStatusMessage = $"Couldn't read UE4SS's Mods folder: {ex.Message}";
            return;
        }

        var userModsLine = userMods.Count > 0
            ? $"Your own {userMods.Count} mod(s) — {string.Join(", ", userMods)} — are moved back to this app's staging first (re-enable them if you ever reinstall UE4SS).\n\n"
            : "No mods of your own were found in its Mods folder.\n\n";

        var result = _dialogService.Confirm(
            "This removes the UE4SS loader completely from your game's Binaries\\Win64 folder (dwmapi.dll and the ue4ss folder) — "
            + "Lua/scripting mods stop working until it's reinstalled.\n\n"
            + userModsLine
            + "Everything removed is backed up first (last 5 kept), and Icarus's own files are not touched.\n\nContinue?",
            "Uninstall UE4SS", ThemedConfirmSeverity.Warning);
        if (!(result))
        {
            return;
        }

        if (IsGameRunning())
        {
            Ue4ssStatusMessage = "Icarus is running — close it before uninstalling UE4SS, or the game may lock the files being removed.";
            return;
        }

        IsUninstallingUe4ss = true;
        try
        {
            // A SECOND, LATE check, immediately before the real removal below — the first
            // IsGameRunning() check above (before the confirm dialog) can go stale for however long
            // the user takes to answer that dialog, and Icarus could be launched in that window. This
            // doesn't close the race entirely — it only shrinks the exposed window down to
            // "immediately before the write" instead of "however long the confirm dialog was up".
            if (IsGameRunning())
            {
                Ue4ssStatusMessage = "Icarus started running while this uninstall was about to write — nothing was touched. Close it and try again.";
                return;
            }

            Ue4ssStatusMessage = "Uninstalling…";
            var uninstallResult = await _ue4ssLoaderInstallService.UninstallAsync(contentPath, _stagedUe4ssDirectory, _backupDirectory);

            RefreshUe4ssStatus();
            Ue4ssStatusMessage = uninstallResult.PreservedUserMods.Count > 0
                ? $"UE4SS removed. Your {uninstallResult.PreservedUserMods.Count} mod(s) were kept in this app's staging; the rest is backed up."
                : "UE4SS removed. A backup was kept.";
            _activityLog.Log(Ue4ssStatusMessage, ActivityEntryKind.Success);
            WeakReferenceMessenger.Default.Send(new Ue4ssLoaderChangedMessage(Ue4ssStatusMessage));
        }
        catch (Exception ex)
        {
            ReportUe4ssProblem($"Uninstall failed: {ex.Message}", $"UE4SS uninstall failed: {ex.Message}");
        }
        finally
        {
            IsUninstallingUe4ss = false;
        }
    }

    /// <summary>
    /// UE4SS install/uninstall write into the game's own Binaries\Win64 folder (dwmapi.dll,
    /// UE4SS.dll, the ue4ss folder) — files the running game process can hold open, the same class of
    /// risk SavesViewModel already guards with IGameProcessChecker. Checked once before the confirm
    /// dialog and again immediately before the real write, for the same reason: the first check can
    /// go stale for however long the user takes to answer that dialog (or, for install, the download
    /// on top of that).
    /// </summary>
    private bool IsGameRunning() => _gameProcessChecker.IsRunning();
}
