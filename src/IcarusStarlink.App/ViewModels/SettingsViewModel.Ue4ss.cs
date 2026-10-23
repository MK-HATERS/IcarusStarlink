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

    /// <summary>The installed build in words: "3.0.1 dev build 1c1a1497", "3.0.1 (official release)", or "3.0.1 dev build 1181" when it's a build Starlink knows the number of.</summary>
    public string Ue4ssInstalledLabel => Ue4ssBuild.Describe(Ue4ssInstalledVersion, Ue4ssInstalledCommit, KnownBuildNumber(Ue4ssInstalledCommit));

    public string Ue4ssStatusText
    {
        get
        {
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
                Ue4ssBuildOrder.Same when HasCheckedUe4ssRelease => $"{Ue4ssInstalledLabel} installed (up to date).",
                Ue4ssBuildOrder.Newer => $"{Ue4ssInstalledLabel} installed — {Ue4ssInstallLabel} available.",
                Ue4ssBuildOrder.Older => $"{Ue4ssInstalledLabel} installed (newer than {Ue4ssInstallLabel}).",
                Ue4ssBuildOrder.Unknown when HasCheckedUe4ssRelease => $"{Ue4ssInstalledLabel} installed. The newest is {Ue4ssInstallLabel}.",
                _ => $"{Ue4ssInstalledLabel} installed.",
            };
        }
    }

    /// <summary>Install uses the UE4SS built into Starlink (no download) unless a manual check found a newer dev build on GitHub.</summary>
    public string Ue4ssInstallButtonLabel => !IsUe4ssInstalled
        ? $"Install {Ue4ssInstallLabel}"
        : Ue4ssInstallOrder switch
        {
            Ue4ssBuildOrder.Same => "Reinstall",
            Ue4ssBuildOrder.Newer => $"Update to {Ue4ssInstallLabel}",
            Ue4ssBuildOrder.Older => $"Install {Ue4ssInstallLabel} (older)",
            _ => $"Install {Ue4ssInstallLabel}",
        };

    /// <summary>The build Install would put in, in words.</summary>
    private string Ue4ssInstallLabel => UseGitHubUe4ss
        ? Ue4ssBuild.Describe(Ue4ssLatestRelease!.Version, Ue4ssLatestRelease.Commit, Ue4ssLatestRelease.BuildNumber)
        : BundledUe4ss.Label;

    private string Ue4ssInstallVersion => UseGitHubUe4ss ? Ue4ssLatestRelease!.Version : BundledUe4ss.Version;

    private string Ue4ssInstallCommit => UseGitHubUe4ss ? Ue4ssLatestRelease!.Commit! : BundledUe4ss.Commit;

    /// <summary>Download only when the manual check found a dev build other than the one inside Starlink (the bundled one was the newest when this Starlink was released, and experimental-latest only moves forward).</summary>
    private bool UseGitHubUe4ss => Ue4ssLatestRelease is { Commit: { } commit } && !Ue4ssBuild.SameCommit(commit, BundledUe4ss.Commit);

    /// <summary>How the build Install would put in compares with the installed one.</summary>
    private Ue4ssBuildOrder Ue4ssInstallOrder =>
        Ue4ssInstalledCommit is null ? Ue4ssBuildOrder.Unknown
        : Ue4ssBuild.SameCommit(Ue4ssInstalledCommit, Ue4ssInstallCommit) ? Ue4ssBuildOrder.Same
        // Every dev build comes after the v3.0.1 tag.
        : Ue4ssBuild.IsOfficialRelease(Ue4ssInstalledCommit) ? Ue4ssBuildOrder.Newer
        : Ue4ssCheckedOrder;

    private int? KnownBuildNumber(string? commit) =>
        Ue4ssBuild.SameCommit(commit, BundledUe4ss.Commit) ? BundledUe4ss.BuildNumber
        : Ue4ssLatestRelease is { Commit: { } latest, BuildNumber: { } number } && Ue4ssBuild.SameCommit(commit, latest) ? number
        : null;

    private void NotifyUe4ssLabels()
    {
        OnPropertyChanged(nameof(Ue4ssInstalledLabel));
        OnPropertyChanged(nameof(Ue4ssStatusText));
        OnPropertyChanged(nameof(Ue4ssInstallButtonLabel));
    }

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
    /// so it could install into a folder the rest of the app wasn't using (review 2026-10-23).
    /// </summary>
    private string? Ue4ssContentPath => string.IsNullOrWhiteSpace(_settingsService.Current.IcarusContentPath) ? null : _settingsService.Current.IcarusContentPath;

    private const string Ue4ssNeedsGameFolder = "Set the Icarus Content folder and click Save settings first.";

    /// <summary>
    /// A UE4SS refusal or failure: on this page's status line and in the activity log (a pop-up),
    /// since the Library's "Install UE4SS" bar runs these commands with Settings not showing, and
    /// used to give no answer at all (review 2026-10-23).
    /// </summary>
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
            Ue4ssInstalledVersion = null;
            Ue4ssInstalledCommit = null;
            return;
        }

        try
        {
            var status = _ue4ssLoaderInstallService.GetStatus(contentPath);
            IsUe4ssInstalled = status.IsInstalled;
            Ue4ssInstalledVersion = status.InstalledVersion;
            Ue4ssInstalledCommit = status.InstalledCommit;
        }
        catch (Exception)
        {
            // A path with no parent (a drive root picked in Browse) makes the game-path resolver
            // throw; from inside a property-changed hook that took the whole app down (an audit
            // finding). Not a real install = not installed.
            IsUe4ssInstalled = false;
            Ue4ssInstalledVersion = null;
            Ue4ssInstalledCommit = null;
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

            var latest = await _ue4ssReleaseClient.GetLatestDevBuildAsync();
            if (latest is null)
            {
                Ue4ssStatusMessage = GitHubApiCache.RateLimitMessage is { } limit
                    ? $"Couldn't check for a newer UE4SS. {limit}"
                    : "Couldn't check for a newer UE4SS: GitHub didn't answer (offline, or GitHub is down).";
                return;
            }

            Ue4ssLatestRelease = latest;
            Ue4ssCheckedOrder = Ue4ssInstalledCommit is { } installed
                ? await _ue4ssReleaseClient.CompareBuildsAsync(installed, Ue4ssInstallCommit)
                : Ue4ssBuildOrder.Unknown;
            HasCheckedUe4ssRelease = true;

            var newest = Ue4ssBuild.Describe(latest.Version, latest.Commit, latest.BuildNumber);
            Ue4ssStatusMessage = !IsUe4ssInstalled ? $"The newest UE4SS is {newest}."
                : Ue4ssInstallOrder switch
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

    /// <summary>The paragraph about the user's C++ mods for an install/update confirmation; empty when there are none or the build isn't changing.</summary>
    private string NativeModsWarning() => NativeModsWarningFor(changing: Ue4ssInstallOrder != Ue4ssBuildOrder.Same);

    private string NativeModsWarningFor(bool changing)
    {
        if (!changing || !IsUe4ssInstalled || Ue4ssContentPath is not { } contentPath)
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
    /// Phase 8.5: a real, hard-to-reverse write into Binaries\Win64 — the game's own executable
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

        var fromGitHub = UseGitHubUe4ss;
        var version = Ue4ssInstallVersion;
        var commit = Ue4ssInstallCommit;
        var label = Ue4ssInstallLabel;
        var wasInstalled = IsUe4ssInstalled;
        var order = Ue4ssInstallOrder;
        var verb = !wasInstalled ? "Install" : order == Ue4ssBuildOrder.Newer ? "Update" : "Reinstall";
        var current = wasInstalled && Ue4ssInstalledVersion is not null ? $"You have UE4SS {Ue4ssInstalledLabel}. " : "";
        var downgrade = order == Ue4ssBuildOrder.Older
            ? "Yours is newer: this replaces it with an older build. To keep yours, cancel.\n\n"
            : "";
        var result = _dialogService.Confirm(
            current + downgrade + (fromGitHub
                ? $"This downloads UE4SS {label} from GitHub and installs it into your game's Binaries\\Win64 folder "
                : $"This installs UE4SS {label}, which comes with Starlink (no download), into your game's Binaries\\Win64 folder ") +
            "— the loader that lets Lua/scripting mods run.\n\n" +
            NativeModsWarning() +
            "Your existing UE4SS.dll/dwmapi.dll are backed up first (last 5 kept). Any mods already in your Mods folder, " +
            "and your own UE4SS-settings.ini if you've customized it, are left untouched.\n\n" +
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
            if (fromGitHub)
            {
                var release = Ue4ssLatestRelease!;
                var bytes = await _httpClient.GetByteArrayAsync(release.DownloadUrl);
                await File.WriteAllBytesAsync(tempZipPath, bytes);

                // A real bug found live: unlike this app's own self-update (AppUpdateClient), this
                // download had no integrity check at all — it writes a third-party DLL loader straight
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
            await _ue4ssLoaderInstallService.InstallOrUpdateAsync(contentPath, tempZipPath, _backupDirectory, version, commit);

            RefreshUe4ssStatus();
            Ue4ssCheckedOrder = Ue4ssBuildOrder.Unknown;
            // From the same verb the confirm used: a reinstall or an older build isn't an "update".
            Ue4ssStatusMessage = verb switch
            {
                "Install" => $"Installed UE4SS {label}.",
                "Update" => $"Updated to UE4SS {label}.",
                _ => $"Reinstalled UE4SS {label}.",
            };
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
            || _dialogService.PickFile("Pick a UE4SS release zip (for example UE4SS_v3.0.1-1181-g83330635.zip)", "UE4SS release (*.zip)|*.zip") is not { } zipPath)
        {
            return;
        }

        // A release zip keeps GitHub's name ("UE4SS_v3.0.1-1176-g1ba38db4.zip"), which says which build it is.
        var knownBuild = Ue4ssBuild.TryParseDevAssetName(Path.GetFileName(zipPath), out var zipVersion, out _, out var zipCommit);
        var changesBuild = !knownBuild || !Ue4ssBuild.SameCommit(zipCommit, Ue4ssInstalledCommit);
        if (!_dialogService.Confirm(
                $"Install UE4SS from\n{zipPath}\ninto your game's Binaries\\Win64 folder?\n\n" +
                (changesBuild ? NativeModsWarningFor(changing: true) : "") +
                "Your existing UE4SS.dll/dwmapi.dll are backed up first (last 5 kept). Mods already in your Mods folder, " +
                "and your own UE4SS-settings.ini, are left untouched.",
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
            await _ue4ssLoaderInstallService.InstallOrUpdateAsync(
                contentPath, zipPath, _backupDirectory, knownBuild ? zipVersion : null, knownBuild ? zipCommit : null);
            RefreshUe4ssStatus();
            Ue4ssCheckedOrder = Ue4ssBuildOrder.Unknown;
            Ue4ssStatusMessage = $"Installed UE4SS{(Ue4ssInstalledVersion is not null ? $" {Ue4ssInstalledLabel}" : "")} from {Path.GetFileName(zipPath)}.";
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
    /// The last item of the big plan, deliberately saved for last: removes the UE4SS loader
    /// entirely (dwmapi.dll + the ue4ss folder). The safety story, in order: the user's own mods —
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
