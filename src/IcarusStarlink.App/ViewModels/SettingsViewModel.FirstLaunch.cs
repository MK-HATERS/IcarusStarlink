using System.IO;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Activity;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class SettingsViewModel
{
    // --- First-launch setup ---

    /// <summary>
    /// Set synchronously, at the start of the launch flow, whenever any prerequisite turned out
    /// to be missing. App.xaml.cs reads it right after constructing this VM to land that launch
    /// on the Settings page, so what the steps below did (found the game via Steam, installed
    /// UnrealPak, is extracting game data — all reported through this page's own status lines)
    /// is actually visible instead of happening behind the Library page. Deliberately not
    /// navigated from in here: this runs inside the constructor's own fire-and-forget call, and
    /// a NavigateToPageMessage handled mid-construction would resolve this same singleton
    /// re-entrantly.
    /// </summary>
    public bool FirstLaunchSetupNeededAttention { get; private set; }

    /// <summary>The constructor's launch flow, exposed so tests can await it instead of racing its fire-and-forget start.</summary>
    internal Task FirstLaunchSetupTask { get; }

    /// <summary>
    /// Every prerequisite in one place, in dependency order, so a fresh install is usable without
    /// a trip through Settings — a real complaint ("shouldn't all prerequisites be done at first
    /// launch?"): the game folder was never asked for at all, so the Launch game / UE4SS folder /
    /// Game folder quick-links sat greyed out with no hint why, and nothing ever offered the first
    /// data extraction either. Each step is quiet when already satisfied, so a configured install
    /// sees nothing.
    ///   1. Icarus Content folder — auto-detected through Steam and saved silently when found,
    ///      else a Browse offer. Re-checked every launch while still empty, same as UnrealPak.
    ///   2. UnrealPak.exe — the existing adopt / install / Browse offer.
    ///   3. The first "Update data folder" extraction — offered ONCE (FirstLaunchSetupCompletedAtUtc),
    ///      and only once 1 and 2 are both set, since it needs both. Declining just leaves the
    ///      button in Settings, where it always was.
    /// </summary>
    private async Task RunFirstLaunchSetupAsync()
    {
        try
        {
            EnsureIcarusContentFolderOnLaunch();
            await EnsureUnrealPakOnLaunchAsync();
            await OfferFirstDataExtractionAsync();
        }
        catch (Exception ex)
        {
            // Never let setup take the launch down — everything here can still be done by hand.
            _activityLog.Log($"First-launch setup hit an error ({ex.Message}) — finish setting up in Settings.", ActivityEntryKind.Warning);
        }
    }

    private void EnsureIcarusContentFolderOnLaunch()
    {
        if (!string.IsNullOrWhiteSpace(IcarusContentPath))
        {
            return;
        }

        FirstLaunchSetupNeededAttention = true;

        // The same Steam lookup the Auto-detect button runs — but saved on the spot rather than
        // pre-filled for a Save click, since there's nothing for the user to decide when the real
        // install was found.
        var detected = _steamInstallLocator.FindIcarusContentPath();
        if (detected is not null)
        {
            IcarusContentPath = detected;
            Save();
            SavedMessage = $"Found your Icarus install through Steam and saved it: {detected}";
            return;
        }

        var browse = _dialogService.Confirm(
            "IcarusStarlink needs to know where Icarus is installed, and couldn't find it through Steam.\n\n"
            + "Browse to the game's Content folder now? (It's Icarus\\Icarus\\Content inside the game install. Choose No to set it later in Settings — launching the game, the folder shortcuts, merging and installing all need it.)",
            "Set up your Icarus folder", ThemedConfirmSeverity.Question);
        if (browse)
        {
            BrowseIcarusContentFolder();
            if (!string.IsNullOrWhiteSpace(IcarusContentPath))
            {
                Save();
                return;
            }
        }

        SavedMessage = "Icarus folder not set yet — use Auto-detect or Browse… above, then Save settings.";
    }

    private async Task OfferFirstDataExtractionAsync()
    {
        if (_settingsService.Current.FirstLaunchSetupCompletedAtUtc is not null)
        {
            return;
        }

        // Not ready to offer yet (either prerequisite still missing, after the steps above had
        // their say) — leave it unmarked so the NEXT launch that does have both gets the offer.
        if (string.IsNullOrWhiteSpace(IcarusContentPath) || string.IsNullOrWhiteSpace(UnrealPakExePath) || !File.Exists(UnrealPakExePath))
        {
            return;
        }

        // An install that predates this flow and already extracted its data has nothing to be
        // offered — just record that setup is done so this check stops running.
        if (_settingsService.Current.LastDataFolderUpdatedAt is null)
        {
            FirstLaunchSetupNeededAttention = true;
            var extract = _dialogService.Confirm(
                "One last step: extract the game's data now?\n\n"
                + "This pulls the current mod-relevant data out of Icarus (takes a minute or so) — merging, installing, editing and checking mods all work against it. "
                + "Choose No to do it later with Refresh game data in Settings; re-run that whenever the game updates.",
                "Extract game data", ThemedConfirmSeverity.Question);
            if (extract)
            {
                await UpdateDataFolderAsync();
            }
            else
            {
                DataFolderStatusMessage = "Not extracted yet — click Refresh game data whenever you're ready (and after each game update).";
            }
        }

        // Same one-time moment: every Nexus website download depends on this app being the nxm://
        // handler, and the switch was buried in Settings → Nexus (a real complaint about setup).
        if (!_nxmProtocolRegistrar.IsRegisteredToThisApp())
        {
            FirstLaunchSetupNeededAttention = true;
            var register = _dialogService.Confirm(
                "Make IcarusStarlink handle Nexus \"Mod Manager Download\" links?\n\n"
                + "This registers the app as your nxm:// handler (a per-user Windows setting, no admin needed) so clicking Download on a Nexus mod page sends the file straight here. "
                + "You can change it any time in Settings → Nexus.",
                "Nexus download links", ThemedConfirmSeverity.Question);
            if (register)
            {
                // The registrar directly — the Settings-page command above this one asks its own
                // confirmation, and this offer already was the confirmation.
                try
                {
                    _nxmProtocolRegistrar.Register();
                    IsNxmProtocolRegisteredToThisApp = _nxmProtocolRegistrar.IsRegisteredToThisApp();
                    NxmProtocolStatusMessage = "Registered — Nexus \"Mod Manager Download\" buttons now open IcarusStarlink.";
                }
                catch (Exception ex)
                {
                    NxmProtocolStatusMessage = $"Registration failed: {ex.Message} — try again from Settings → Nexus.";
                }
            }
        }

        _settingsService.Current.FirstLaunchSetupCompletedAtUtc = DateTimeOffset.UtcNow;
        _settingsService.Save();
    }

    /// <summary>
    /// First-launch (or any launch with no working UnrealPak): silently adopt a copy that's
    /// already installed next to the app, else offer to install the bundled payload, else point
    /// the user at Browse — the "verify, locate, or install next to the exe" flow. Step 2 of
    /// RunFirstLaunchSetupAsync; quiet whenever the configured path simply works.
    /// </summary>
    private async Task EnsureUnrealPakOnLaunchAsync()
    {
        if (!string.IsNullOrWhiteSpace(UnrealPakExePath) && File.Exists(UnrealPakExePath))
        {
            return;
        }

        FirstLaunchSetupNeededAttention = true;

        // A release zip whose Tools folder was pre-extracted (or a previous install) — adopt it
        // without ceremony; there's nothing to ask.
        if (File.Exists(_unrealPakInstaller.InstalledExePath))
        {
            UnrealPakExePath = _unrealPakInstaller.InstalledExePath;
            Save();
            UnrealPakStatusMessage = "Using the copy installed next to the app.";
            return;
        }

        var answer = _dialogService.Confirm(
            "IcarusStarlink needs UnrealPak.exe to build and unpack mod paks, and none is set up yet.\n\n"
            + "Install it now? (Downloads a small, one-time payload if there isn't already a copy shipped alongside the app. Choose No to point at one you already have, in Settings.)",
            "Set up UnrealPak", ThemedConfirmSeverity.Question);
        if (answer)
        {
            await InstallBundledUnrealPakAsync();
        }
        else
        {
            UnrealPakStatusMessage = "No UnrealPak.exe set — install it, or Browse… to one you already have.";
        }
    }
}
