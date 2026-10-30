using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Server;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// The Overview's Connect card: the address friends type into the in-game Direct Connect box
/// (IP:game port). The home network address comes from this PC's adapters; the public one is
/// asked of a "what is my IP" service only when the owner presses Show my public IP, and is kept
/// for this session only (never saved).
/// </summary>
public sealed partial class LocalServerViewModel
{
    private readonly INetworkAddressService? _network;

    /// <summary>How long Show my public IP waits for the service to answer. Tests shorten it.</summary>
    internal TimeSpan PublicIpTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>This PC's address on the home network (no port), or null when none was found.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LanJoinAddress), nameof(LanAddressText), nameof(HasLanAddress), nameof(PortForwardHint))]
    [NotifyCanExecuteChangedFor(nameof(CopyLanAddressCommand))]
    private string? _lanAddress;

    /// <summary>This PC's public address (no port), once Show my public IP has asked. Session only.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PublicJoinAddress), nameof(HasPublicAddress))]
    [NotifyCanExecuteChangedFor(nameof(CopyPublicAddressCommand))]
    private string? _publicAddress;

    /// <summary>Why the public address couldn't be found, in plain words; null when it was (or hasn't been asked).</summary>
    [ObservableProperty]
    private string? _publicAddressProblem;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowPublicAddressCommand))]
    private bool _isLookingUpPublicAddress;

    /// <summary>The selected server's game port (what Direct Connect takes), as saved.</summary>
    public int ConnectGamePort => SelectedProfile?.Port ?? DedicatedServerDefaults.GamePort;

    /// <summary>The selected server's query port (what the server browser uses), as saved.</summary>
    public int ConnectQueryPort => SelectedProfile?.QueryPort ?? DedicatedServerDefaults.QueryPort;

    public bool HasLanAddress => LanAddress is not null;

    public bool HasPublicAddress => PublicAddress is not null;

    public string? LanJoinAddress => LanAddress is { } ip ? ServerAddresses.JoinAddress(ip, ConnectGamePort) : null;

    public string? PublicJoinAddress => PublicAddress is { } ip ? ServerAddresses.JoinAddress(ip, ConnectGamePort) : null;

    public string LanAddressText => LanJoinAddress
        ?? "No home network address found. Is this PC connected to your router (by cable or Wi-Fi)?";

    public string PortForwardHint
    {
        get
        {
            var game = ConnectGamePort.ToString(CultureInfo.InvariantCulture);
            var query = ConnectQueryPort.ToString(CultureInfo.InvariantCulture);
            var to = LanAddress is { } ip ? $"this PC ({ip})" : "this PC";
            return $"Friends outside your home network need UDP {game} and UDP {query} forwarded on your router to {to}. "
                + "In game they type the address exactly as shown into the Direct Connect box.";
        }
    }

    private void RaiseConnectChanged()
    {
        OnPropertyChanged(nameof(ConnectGamePort));
        OnPropertyChanged(nameof(ConnectQueryPort));
        OnPropertyChanged(nameof(LanJoinAddress));
        OnPropertyChanged(nameof(LanAddressText));
        OnPropertyChanged(nameof(PublicJoinAddress));
        OnPropertyChanged(nameof(PortForwardHint));
    }

    /// <summary>Reads this PC's adapters again (off the UI thread) and picks the home network address.</summary>
    [RelayCommand]
    internal async Task RefreshLanAddressAsync()
    {
        if (_network is null)
        {
            return;
        }

        try
        {
            var adapters = await OffUiThread(_network.ReadAdapters);
            LanAddress = ServerAddresses.PickLanAddress(adapters);
        }
        catch (Exception)
        {
            LanAddress = null;
        }
    }

    private bool CanShowPublicAddress() => _network is not null && !IsLookingUpPublicAddress;

    /// <summary>
    /// The only time Starlink asks the public IP service: this button. Its answer must be a plain
    /// IPv4 address; anything else is refused. Waits at most PublicIpTimeout.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanShowPublicAddress))]
    private async Task ShowPublicAddressAsync()
    {
        if (_network is null)
        {
            return;
        }

        IsLookingUpPublicAddress = true;
        PublicAddressProblem = null;
        using var timeout = new CancellationTokenSource(PublicIpTimeout);
        try
        {
            var reply = await _network.FetchPublicIpReplyAsync(timeout.Token);
            if (ServerAddresses.ParsePublicIPv4(reply) is { } ip)
            {
                PublicAddress = ip;
            }
            else
            {
                PublicAddressProblem = "The address service sent back something that isn't an IP address, so it wasn't used. Try again later.";
            }
        }
        catch (OperationCanceledException)
        {
            PublicAddressProblem = $"The address service ({ServerAddresses.PublicIpServiceUrl}) didn't answer within {PublicIpTimeout.TotalSeconds:0} seconds. Check your internet connection and try again.";
        }
        catch (Exception)
        {
            PublicAddressProblem = $"Couldn't reach the address service ({ServerAddresses.PublicIpServiceUrl}). Check your internet connection and try again.";
        }
        finally
        {
            IsLookingUpPublicAddress = false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasLanAddress))]
    private void CopyLanAddress()
    {
        if (LanJoinAddress is { } address)
        {
            CopyText(address, $"Copied {address}. Friends on your home network paste it into Direct Connect in game.");
        }
    }

    [RelayCommand(CanExecute = nameof(HasPublicAddress))]
    private void CopyPublicAddress()
    {
        if (PublicJoinAddress is { } address)
        {
            CopyText(address, $"Copied {address}. Friends outside your home network paste it into Direct Connect in game (the ports must be forwarded on your router).");
        }
    }
}
