using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Server;

namespace IcarusStarlink.App.ViewModels;

/// <summary>The Site Manager: saved FTP sites (FileZilla-style), their form, save and delete.</summary>
public sealed partial class ServerViewModel
{
    // --- Site Manager ---
    public ObservableCollection<FtpSiteProfile> Sites { get; } = [];

    [ObservableProperty]
    private FtpSiteProfile? _selectedSite;

    [ObservableProperty]
    private string _siteNameInput = "";

    [ObservableProperty]
    private string _hostInput = "";

    [ObservableProperty]
    private string _portInput = "21";

    [ObservableProperty]
    private string _usernameInput = "";

    /// <summary>Never pre-filled from a saved site — a saved password is never read back and shown. Leaving this blank while editing an existing site means "keep whatever's already saved".</summary>
    [ObservableProperty]
    private string _passwordInput = "";

    [ObservableProperty]
    private string _remotePathInput = "";

    /// <summary>Blank means "use the fixed SurvivalServers-shaped default" (DefaultRemoteModsPath) — same convention as RemotePathInput above, not a validated/required field.</summary>
    [ObservableProperty]
    private string _modsPathInput = "";

    /// <summary>Blank means "use the fixed default" (DefaultRemoteWin64Path) — same convention as ModsPathInput.</summary>
    [ObservableProperty]
    private string _win64PathInput = "";

    [ObservableProperty]
    private FtpEncryptionMode _encryptionModeInput;

    [ObservableProperty]
    private string? _siteStatusMessage;

    /// <summary>Drives the "+ New site" Expander — collapsed by default (nothing to fill in until the user asks), opened automatically by NewSite/selecting a site, two-way bound so a user can also collapse it manually without losing anything typed.</summary>
    [ObservableProperty]
    private bool _isSiteFormOpen;

    private void ReloadSites()
    {
        Sites.Clear();
        foreach (var site in _siteStore.GetAll().OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            Sites.Add(site);
        }
    }

    partial void OnSelectedSiteChanged(FtpSiteProfile? value)
    {
        OnPropertyChanged(nameof(HasKnownDeleteRestriction));

        if (value is null)
        {
            return;
        }

        SiteNameInput = value.Name;
        HostInput = value.Host;
        PortInput = value.Port.ToString();
        UsernameInput = value.Username;
        PasswordInput = "";
        RemotePathInput = value.RemotePath;
        ModsPathInput = value.ModsPath ?? "";
        Win64PathInput = value.Win64Path ?? "";
        EncryptionModeInput = value.EncryptionMode;
        SiteStatusMessage = null;
        IsSiteFormOpen = true;
    }

    [RelayCommand]
    private void NewSite()
    {
        SelectedSite = null;
        SiteNameInput = "";
        HostInput = "";
        PortInput = "21";
        UsernameInput = "";
        PasswordInput = "";
        RemotePathInput = "";
        ModsPathInput = "";
        Win64PathInput = "";
        EncryptionModeInput = FtpEncryptionMode.None;
        SiteStatusMessage = null;
        IsSiteFormOpen = true;
    }

    [RelayCommand]
    private void SaveSite()
    {
        if (string.IsNullOrWhiteSpace(SiteNameInput) || string.IsNullOrWhiteSpace(HostInput) || string.IsNullOrWhiteSpace(UsernameInput))
        {
            SiteStatusMessage = "Name, host, and username are required.";
            return;
        }

        if (!int.TryParse(PortInput, out var port) || port is <= 0 or > 65535)
        {
            SiteStatusMessage = "Port must be a number between 1 and 65535.";
            return;
        }

        var id = SelectedSite?.Id ?? Guid.NewGuid();
        // Prefer _connectedSite over SelectedSite as the carry-forward source when they're the same
        // site: RememberDeleteCapability (and the certificate-trust prompt) write their freshly
        // learned facts onto _connectedSite specifically, not onto whatever object SelectedSite
        // happens to currently point at (see _connectedSite's and RememberDeleteCapability's own doc
        // comments on why those can be different objects even for "the same" site). Reading from
        // SelectedSite here could otherwise race a just-completed delete-capability check: this
        // synchronous save landing between that async check's own await and its RememberDeleteCapability
        // call would carry forward the OLD value and silently overwrite the fact that check just learned.
        var carryForwardSource = _connectedSite?.Id == id ? _connectedSite : SelectedSite?.Id == id ? SelectedSite : null;
        var site = new FtpSiteProfile
        {
            Id = id, Name = SiteNameInput, Host = HostInput, Port = port, Username = UsernameInput,
            RemotePath = RemotePathInput,
            ModsPath = string.IsNullOrWhiteSpace(ModsPathInput) ? null : ModsPathInput,
            Win64Path = string.IsNullOrWhiteSpace(Win64PathInput) ? null : Win64PathInput,
            EncryptionMode = EncryptionModeInput,
            // Neither field has a UI of its own to edit — both are learned elsewhere (a real
            // certificate-trust prompt during Connect; a real delete attempt against the server)
            // and would otherwise be silently wiped back to "never tested" every time this form
            // saves any OTHER field (e.g. a corrected RemotePath), forcing the trust prompt again
            // or losing the known delete-support state for no reason tied to what was actually
            // edited. Only applies when editing the SAME already-saved site — a brand-new site has
            // no prior state to carry forward.
            TrustedCertificateThumbprint = carryForwardSource?.TrustedCertificateThumbprint,
            SupportsDelete = carryForwardSource?.SupportsDelete,
        };

        try
        {
            _siteStore.Save(site);

            // Blank means "keep whatever's already saved" — only touches the credential when the
            // user actually typed something, so re-saving other fields (e.g. a corrected host)
            // doesn't require re-entering a password that hasn't changed.
            if (!string.IsNullOrEmpty(PasswordInput))
            {
                _credentialStore.Save(CredentialTargets.FtpSite(id), PasswordInput);
                PasswordInput = "";
            }

            ReloadSites();
            SelectedSite = Sites.FirstOrDefault(s => s.Id == id);

            // Editing the connected site (say, a corrected Mods path) applies to the live
            // connection too: the sync buttons read their folders from _connectedSite.
            if (_connectedSite is { } connected && connected.Id == site.Id)
            {
                _connectedSite = site;
            }

            SiteStatusMessage = $"Saved '{site.Name}'.";
        }
        catch (Exception ex)
        {
            // Same UI boundary as everywhere else — a locked/permission-denied ftp_sites.json, or
            // a Credential Manager failure, shows a status message instead of crashing the app.
            SiteStatusMessage = $"Couldn't save site: {ex.Message}";
        }
    }

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task DeleteSiteAsync()
    {
        // Matches every FTP-touching command's own IsBusy guard (see its doc comment above) — this
        // one can also touch the shared _connectedClient below (via DisconnectAsync), so a click
        // landing mid-Upload/Download/Delete must refuse rather than race it or delete the site's
        // saved profile out from under a still-live connection.
        if (IsBusy)
        {
            return;
        }

        if (SelectedSite is not { } site)
        {
            SiteStatusMessage = "Select a site first.";
            return;
        }

        // Every other destructive Server action already confirms first (DeleteRemoteAsync below) —
        // this one didn't, and a click meant for the file browser's own Delete landed here instead
        // during this app's own real testing, permanently wiping a real saved site + its stored
        // password with no undo. Recovered that time only because the details were still known;
        // this confirmation is the actual fix, not just a lesson noted for later.
        var confirm = _dialogService.Confirm(
            $"Delete the saved site '{site.Name}'? Its saved password will be removed too. This can't be undone from here.",
            "Delete site", ThemedConfirmSeverity.Warning);
        if (!(confirm))
        {
            return;
        }

        // A live connection to the very site being deleted would otherwise dangle: _connectedClient
        // stays connected and every Upload/Download/Delete action keeps working against it, but
        // SelectedSite is about to be cleared by NewSite() below, so the UI would show "Connected"
        // next to a blank site form with no way to tell which server it's still talking to. Checked
        // against _connectedSite (the site the LIVE connection actually belongs to), not
        // SelectedSite — site IS SelectedSite here (see above), so comparing against SelectedSite's
        // own Id is always true and would disconnect from whatever site happens to be connected even
        // when deleting a completely unrelated, never-connected site.
        //
        // Deliberately called BEFORE this method's own IsBusy=true below, not inside it:
        // DisconnectAsync manages its own IsBusy=true/false around its body (see its doc comment),
        // and early-returns if IsBusy is already true — nesting it inside our own IsBusy=true would
        // make it silently no-op instead of actually disconnecting.
        if (IsConnected && _connectedSite?.Id == site.Id)
        {
            await DisconnectAsync();
        }

        IsBusy = true;
        try
        {
            _siteStore.Delete(site.Id);
            _credentialStore.Delete(CredentialTargets.FtpSite(site.Id));
            ReloadSites();
            NewSite();
            SiteStatusMessage = $"Deleted '{site.Name}'.";
        }
        catch (Exception ex)
        {
            SiteStatusMessage = $"Couldn't delete site: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
