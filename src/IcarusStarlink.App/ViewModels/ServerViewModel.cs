using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Server;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// Phase 8.5: a narrower revival of the spec's original "Server" nav item — FTP file management
/// for a dedicated Icarus server's own mods folder, not a full remote-agent/control page (that was
/// cut from v1 entirely). "Site Manager" (saved connections, FileZilla-style) + a real connection
/// with a directory browser + upload/download/delete.
/// </summary>
public sealed partial class ServerViewModel : ObservableObject
{
    private readonly IFtpSiteStore _siteStore;
    private readonly ICredentialStore _credentialStore;
    private readonly Func<IFtpClient> _ftpClientFactory;
    private readonly IActivityLog _activityLog;
    private readonly ISettingsService _settingsService;
    private readonly IUe4ssModRepository _ue4ssModRepository;
    private readonly IDialogService _dialogService;
    private readonly string _outputPakPath;
    private readonly string _pakManifestPath;

    // Confirmed against the user's own real dedicated server (SurvivalServers): the FTP login
    // root is NOT the game project root — it's one level up, with the actual Content\/Binaries\
    // tree sitting under a fixed "Icarus" subfolder (alongside IcarusServer.exe, a "Saved" folder,
    // etc.) — SurvivalServers' own per-game-type convention, not something this user configured.
    // Below that, it's an exact mirror of the local layout, just with forward slashes. Originally
    // fixed rather than a per-site field, per the user's own call ("assume a fixed relative path
    // should be fine") — since widened into FtpSiteProfile.ModsPath/Win64Path (both optional)
    // once other hosts turned out to lay a dedicated server out differently. These two remain the
    // fallback for any site that leaves its own override unset, so nothing changes for the site
    // this was originally reverse-engineered against.
    private const string DefaultRemoteModsPath = "Icarus/Content/Paks/mods";
    private const string DefaultRemoteWin64Path = "Icarus/Binaries/Win64";

    /// <summary>Which saved site the LIVE connection (_connectedClient) actually belongs to — kept
    /// deliberately separate from SelectedSite, which only drives the site-details form and the
    /// Sites list selection and can drift away from the connected site (nothing stops selecting a
    /// different saved site in the list while still connected to another one). Set once ConnectAsync
    /// actually succeeds; cleared on DisconnectAsync. The four Remote*Path properties below resolve
    /// against THIS, not SelectedSite, so a stale selection can't upload to the wrong site's path.</summary>
    private FtpSiteProfile? _connectedSite;

    /// <summary>The connected site's own ModsPath override when it set one, else the fixed
    /// SurvivalServers-shaped default above. Internal (not private) purely so ServerViewModelTests
    /// can assert on it after a real ConnectAsync round trip — this ViewModel had no tests at all
    /// before this, and every command that actually uploads also pops a real ThemedMessageBox
    /// confirmation, which can't run in a unit test host.</summary>
    internal string RemoteModsPath
    {
        get
        {
            var siteOverride = _connectedSite?.ModsPath;
            return string.IsNullOrWhiteSpace(siteOverride) ? DefaultRemoteModsPath : siteOverride;
        }
    }

    /// <summary>Same override-or-default resolution as RemoteModsPath, against Win64Path.</summary>
    internal string RemoteWin64Path
    {
        get
        {
            var siteOverride = _connectedSite?.Win64Path;
            return string.IsNullOrWhiteSpace(siteOverride) ? DefaultRemoteWin64Path : siteOverride;
        }
    }

    /// <summary>Derived from RemoteWin64Path (not a second independent override) so a site that
    /// overrides Win64Path moves its UE4SS loader location along with it, same relative layout as
    /// the fixed default (Win64/ue4ss).</summary>
    internal string RemoteLoaderPath => $"{RemoteWin64Path}/ue4ss";

    /// <summary>Derived from RemoteLoaderPath, same reasoning — Win64/ue4ss/Mods.</summary>
    internal string RemoteModsRootPath => $"{RemoteLoaderPath}/Mods";

    private IFtpClient? _connectedClient;

    public string Title => "Server";

    /// <summary>The "Local server" tab: dedicated servers on this PC (see LocalServerViewModel). Null in tests that only exercise the FTP side.</summary>
    public LocalServerViewModel? Local { get; }

    public static IReadOnlyList<FtpEncryptionMode> EncryptionModes { get; } = Enum.GetValues<FtpEncryptionMode>();

    // --- Connection + directory browser ---
    [ObservableProperty]
    private bool _isConnected;

    // Also false while IsConnecting — the Connect button binds IsEnabled to this, so a slow
    // handshake can't be double-clicked into creating a second concurrent IFtpClient.
    public bool IsNotConnected => !IsConnected && !IsConnecting;

    partial void OnIsConnectedChanged(bool value) => OnPropertyChanged(nameof(IsNotConnected));
    partial void OnIsConnectingChanged(bool value) => OnPropertyChanged(nameof(IsNotConnected));

    [ObservableProperty]
    private bool _isConnecting;

    /// <summary>True once this site's own FTP account has been confirmed (by a real rejected
    /// delete) unable to remove or replace an existing file — some budget hosts allow only
    /// creating new files. Raised again by OnSelectedSiteChanged and RememberDeleteCapability,
    /// since FtpSiteProfile itself isn't a bindable/observable type.</summary>
    public bool HasKnownDeleteRestriction => SelectedSite?.SupportsDelete == false;

    [ObservableProperty]
    private bool _isBusy;

    // IsBusy is set around every FTP-touching command below, but was never consulted as a guard
    // and never bound in XAML — nothing stopped two of them from running concurrently against the
    // single shared _connectedClient (one FluentFtpClient), which could corrupt its session state
    // or throw ObjectDisposedException if e.g. Disconnect raced an in-flight Upload. Every command
    // below now early-returns while already busy, and every action button in ServerView.xaml binds
    // IsEnabled to this so a click can't even fire in the first place — the same belt-and-suspenders
    // shape IsNotConnected/ConnectAsync's own IsConnecting guard already established.
    public bool IsNotBusy => !IsBusy;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsNotBusy));

    [ObservableProperty]
    private string? _connectionStatusMessage;

    [ObservableProperty]
    private string _currentRemotePath = "/";

    public ObservableCollection<FtpEntry> RemoteEntries { get; } = [];

    [ObservableProperty]
    private FtpEntry? _selectedRemoteEntry;

    public ServerViewModel(
        IFtpSiteStore siteStore,
        ICredentialStore credentialStore,
        Func<IFtpClient> ftpClientFactory,
        IActivityLog activityLog,
        ISettingsService settingsService,
        IUe4ssModRepository ue4ssModRepository,
        IDialogService dialogService,
        string outputPakPath,
        LocalServerViewModel? local = null)
    {
        Local = local;
        _siteStore = siteStore;
        _credentialStore = credentialStore;
        _ftpClientFactory = ftpClientFactory;
        _activityLog = activityLog;
        _settingsService = settingsService;
        _ue4ssModRepository = ue4ssModRepository;
        _dialogService = dialogService;
        _outputPakPath = outputPakPath;
        _pakManifestPath = Path.Combine(Path.GetDirectoryName(outputPakPath)!, InstallManifestNames.PakManifest);

        ReloadSites();

        // An app update must not close Starlink in the middle of an upload to the server.
        WeakReferenceMessenger.Default.Register<BackgroundWorkRequestMessage>(this, (recipient, message) =>
        {
            if (((ServerViewModel)recipient).IsBusy)
            {
                message.Reply("the transfer to your FTP server");
            }
        });
    }
}
