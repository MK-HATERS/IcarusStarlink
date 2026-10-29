using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Secrets;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class SettingsViewModel
{
    [ObservableProperty]
    private string _nexusApiKeyInput = "";

    /// <summary>Null when not signed in — e.g. "SomeUser (Premium)".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StableUpdateSourceText))]
    private string? _nexusSignedInAs;

    [ObservableProperty]
    private bool _isAuthorizingNexus;

    public bool CanAuthorizeNexus => !IsAuthorizingNexus;

    [ObservableProperty]
    private string? _nexusStatusMessage;

    [ObservableProperty]
    private bool _isNxmProtocolRegisteredToThisApp;

    public bool IsNxmProtocolNotRegistered => !IsNxmProtocolRegisteredToThisApp;

    [ObservableProperty]
    private string? _nxmProtocolStatusMessage;

    partial void OnIsNxmProtocolRegisteredToThisAppChanged(bool value) => OnPropertyChanged(nameof(IsNxmProtocolNotRegistered));

    /// <summary>The other app that has nxm:// links now ("Vortex"), or null when it's Starlink or nothing — the header and the Take them back button say it.</summary>
    [ObservableProperty]
    private string? _nxmHandlerOwner;

    /// <summary>Settings › Nexus › Take them back automatically (AppSettings.KeepNxmHandler; NxmHandlerWatch acts on it).</summary>
    public bool KeepNxmHandler
    {
        get => _settingsService.Current.KeepNxmHandler;
        set
        {
            if (_settingsService.SetAndSave(s => s.KeepNxmHandler, (s, v) => s.KeepNxmHandler = v, value))
            {
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Settings › Nexus › "Links for other games": the mod managers Starlink replaced and the games each one is passed.</summary>
    public ObservableCollection<NxmOtherAppRow> NxmOtherApps { get; } = [];

    public bool HasNxmOtherApps => NxmOtherApps.Count > 0;

    [RelayCommand]
    private void RemoveNxmOtherApp(NxmOtherAppRow? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            _nxmProtocolRegistrar.RemoveOtherApp(row.Command);
        }
        catch (Exception ex)
        {
            NxmProtocolStatusMessage = $"Couldn't remove it: {ex.Message}";
        }

        RefreshNxmHandlerState();
    }

    private void RefreshNxmHandlerState()
    {
        IsNxmProtocolRegisteredToThisApp = _nxmProtocolRegistrar.IsRegisteredToThisApp();
        var current = IsNxmProtocolRegisteredToThisApp ? null : _nxmProtocolRegistrar.CurrentHandlerCommand();
        NxmHandlerOwner = current is null ? null : NxmHandlerWatch.HandlerName(current);

        NxmOtherApps.Clear();
        try
        {
            var previous = _nxmProtocolRegistrar.PreviousHandlerCommand();
            foreach (var app in _nxmProtocolRegistrar.OtherApps())
            {
                NxmOtherApps.Add(new NxmOtherAppRow(app, string.Equals(app.Command, previous, StringComparison.OrdinalIgnoreCase), _nxmProtocolRegistrar));
            }
        }
        catch (Exception)
        {
            // An unreadable list just shows empty; routing falls back to the app replaced last.
        }

        OnPropertyChanged(nameof(HasNxmOtherApps));
        OnPropertyChanged(nameof(NxmHandlerHeaderState));
        OnPropertyChanged(nameof(NxmRegisterButtonText));
    }

    public string NxmHandlerHeaderState =>
        IsNxmProtocolRegisteredToThisApp ? "Registered to this app"
        : NxmHandlerOwner is { } owner ? $"{owner} has them now"
        : "Not set up (optional)";

    public string NxmRegisterButtonText => NxmHandlerOwner is { } owner ? $"Take them back from {owner}" : "Register";

    partial void OnIsAuthorizingNexusChanged(bool value) => OnPropertyChanged(nameof(CanAuthorizeNexus));

    [RelayCommand]
    private void OpenNexusApiKeyPage() => UrlOpener.TryOpen("https://www.nexusmods.com/users/myaccount?tab=api");

    /// <summary>
    /// A real, live validation against Nexus's own API — "just like a new user would," per the
    /// user's own explicit ask, not a hardcoded test key. Only saves the key to Windows Credential
    /// Manager once Nexus itself has confirmed it's genuinely valid.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task AuthorizeNexusAsync()
    {
        if (string.IsNullOrWhiteSpace(NexusApiKeyInput))
        {
            NexusStatusMessage = "Paste your API key first.";
            return;
        }

        IsAuthorizingNexus = true;
        NexusStatusMessage = null;
        try
        {
            var trimmedKey = NexusApiKeyInput.Trim();
            var user = await _nexusApiClient.ValidateKeyAsync(trimmedKey);
            if (user is null)
            {
                NexusStatusMessage = "Nexus didn't accept that key — double check you copied the whole thing.";
                return;
            }

            _credentialStore.Save(CredentialTargets.NexusApiKey, trimmedKey);
            NexusSignedInAs = user.IsPremium ? $"{user.Name} (Premium)" : user.Name;
            NexusApiKeyInput = "";
            NexusStatusMessage = "Signed in.";
            WeakReferenceMessenger.Default.Send(new NexusAccountChangedMessage());
        }
        catch (Exception ex)
        {
            // Same UI boundary as everywhere else — a network hiccup or Nexus itself being down
            // shows a status message, distinguishable from "that key is wrong" (the null case
            // above) since this is the genuine-failure path, not a rejected key.
            NexusStatusMessage = $"Couldn't reach Nexus: {ex.Message}";
        }
        finally
        {
            IsAuthorizingNexus = false;
        }
    }

    [RelayCommand]
    private void SignOutNexus()
    {
        _credentialStore.Delete(CredentialTargets.NexusApiKey);
        NexusSignedInAs = null;
        NexusStatusMessage = "Signed out.";
        WeakReferenceMessenger.Default.Send(new NexusAccountChangedMessage());
    }

    /// <summary>
    /// Re-validates a previously-saved key once per launch (not on every Settings visit) — cheap
    /// against Nexus's own generous rate limit (500/day), and catches a since-revoked key early
    /// rather than only surfacing that the next time the user happens to click Authorize again.
    /// </summary>
    private async Task InitializeNexusStatusAsync()
    {
        var storedKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
        if (storedKey is null)
        {
            return;
        }

        try
        {
            var user = await _nexusApiClient.ValidateKeyAsync(storedKey);
            if (user is null)
            {
                NexusStatusMessage = "Your saved Nexus API key is no longer valid — sign in again below.";
                return;
            }

            NexusSignedInAs = user.IsPremium ? $"{user.Name} (Premium)" : user.Name;
        }
        catch (Exception)
        {
            // Best-effort startup check — a network hiccup shouldn't block Settings from loading
            // or claim the saved key is invalid when it might still be fine; Authorize will surface
            // a clearer error if the user tries it manually.
            NexusStatusMessage = "Couldn't reach Nexus to confirm your saved key — it may still be valid.";
        }
    }

    /// <summary>
    /// Phase 8.3c: a real, hard-to-reverse registry write (this app becomes the OS's nxm:// handler
    /// for this Windows account), so this is the one place it can happen — never automatic, and
    /// gated behind the user's own explicit Yes in the confirmation dialog below, not just having
    /// clicked the button in the first place.
    /// </summary>
    [RelayCommand]
    private void RegisterNxmProtocol()
    {
        var result = _dialogService.Confirm(
            "This registers IcarusStarlink as the handler for nxm:// links (Nexus's \"Mod Manager Download\" buttons) for your Windows account.\n\n" +
            "If another mod manager (e.g. Vortex) currently handles these, Starlink takes over but passes it the links for its own games, so it keeps working. Unregister below gives the links back to it.\n\n" +
            "Continue?",
            "Register as Nexus download handler", ThemedConfirmSeverity.Question);

        if (!(result))
        {
            return;
        }

        try
        {
            _nxmProtocolRegistrar.Register();
            RefreshNxmHandlerState();
            NxmProtocolStatusMessage = IsNxmProtocolRegisteredToThisApp
                ? "Registered — Nexus \"Mod Manager Download\" buttons now open IcarusStarlink."
                : "Registration didn't take effect — try again, or check that nothing is blocking registry writes.";
        }
        catch (Exception ex)
        {
            NxmProtocolStatusMessage = $"Registration failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void UnregisterNxmProtocol()
    {
        try
        {
            _nxmProtocolRegistrar.Unregister();
            // Turned off on purpose: no "another app took them" warnings or automatic take-backs.
            _settingsService.Current.NxmHandlerSetUp = false;
            _settingsService.Save();
            RefreshNxmHandlerState();
            NxmProtocolStatusMessage = NxmHandlerOwner is { } owner
                ? $"Unregistered — Nexus download links open {owner} again."
                : "Unregistered.";
        }
        catch (Exception ex)
        {
            NxmProtocolStatusMessage = $"Couldn't unregister: {ex.Message}";
        }
    }
}
