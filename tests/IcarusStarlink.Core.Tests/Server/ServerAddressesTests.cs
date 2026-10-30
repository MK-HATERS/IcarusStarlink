using IcarusStarlink.Core.Server;

namespace IcarusStarlink.Core.Tests.Server;

/// <summary>The Connect card's addresses: which adapter is "this PC on the home network", and which public IP replies are taken.</summary>
public sealed class ServerAddressesTests
{
    private static NetworkAdapterInfo Adapter(string name, string ip, bool gateway = true, bool up = true,
        NetworkAdapterKind kind = NetworkAdapterKind.Ethernet, string? description = null) =>
        new(name, description ?? name, up, kind, gateway, [ip]);

    [Fact]
    public void Lan_PicksTheUpAdapterWithAGateway()
    {
        Assert.Equal("192.168.1.20", ServerAddresses.PickLanAddress(
        [
            Adapter("Ethernet 2", "192.168.56.1", gateway: false),
            Adapter("Ethernet", "192.168.1.20"),
        ]));
    }

    /// <summary>Hyper-V, WSL, VM host and VPN adapters never win, even listed first and with a gateway.</summary>
    [Theory]
    [InlineData("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", "172.24.80.1")]
    [InlineData("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter #2", "172.17.0.1")]
    [InlineData("VirtualBox Host-Only Network", "VirtualBox Host-Only Ethernet Adapter", "192.168.56.1")]
    [InlineData("VMware Network Adapter VMnet8", "VMware Virtual Ethernet Adapter for VMnet8", "192.168.150.1")]
    [InlineData("NordLynx", "NordLynx Tunnel", "10.5.0.2")]
    [InlineData("Local Area Connection", "TAP-Windows Adapter V9", "10.8.0.6")]
    [InlineData("WireGuard Tunnel", "WireGuard Tunnel", "10.64.0.2")]
    [InlineData("Tailscale", "Tailscale Tunnel", "100.101.102.103")]
    [InlineData("ZeroTier One [abc]", "ZeroTier Virtual Port", "10.147.17.5")]
    [InlineData("Ethernet 3", "Cisco AnyConnect Secure Mobility Client Virtual Miniport Adapter", "10.20.30.40")]
    public void Lan_VirtualAndVpnAdaptersLose(string name, string description, string ip)
    {
        Assert.Equal("192.168.1.20", ServerAddresses.PickLanAddress(
        [
            Adapter(name, ip, description: description),
            Adapter("Wi-Fi", "192.168.1.20", kind: NetworkAdapterKind.Wireless),
        ]));
    }

    [Fact]
    public void Lan_DownLoopbackTunnelAndSelfAssignedAddressesLose()
    {
        Assert.Null(ServerAddresses.PickLanAddress(
        [
            Adapter("Ethernet", "192.168.1.20", up: false),
            Adapter("Loopback Pseudo-Interface 1", "127.0.0.1", kind: NetworkAdapterKind.Loopback),
            Adapter("Teredo", "10.0.0.9", kind: NetworkAdapterKind.Tunnel),
            Adapter("Ethernet 4", "169.254.12.7"),
            Adapter("Ethernet 5", "not an ip"),
        ]));
    }

    [Fact]
    public void Lan_NoGatewayMeansNotTheWayOut()
    {
        Assert.Null(ServerAddresses.PickLanAddress([Adapter("Ethernet", "192.168.1.20", gateway: false)]));
    }

    [Fact]
    public void Lan_PrivateBeatsOtherAndWiredBeatsWireless()
    {
        Assert.Equal("10.0.0.5", ServerAddresses.PickLanAddress(
        [
            Adapter("Mobile", "25.1.2.3", kind: NetworkAdapterKind.Other),
            Adapter("Wi-Fi", "192.168.1.30", kind: NetworkAdapterKind.Wireless),
            Adapter("Ethernet", "10.0.0.5"),
        ]));
    }

    [Fact]
    public void Lan_AnAdapterNamedLikeAWordWithTunInsideIsNotATunnel()
    {
        Assert.Equal("192.168.0.4", ServerAddresses.PickLanAddress([Adapter("Ethernet", "192.168.0.4", description: "Realtek Fortune Gaming NIC")]));
        Assert.True(ServerAddresses.LooksVirtual(Adapter("tun0", "10.0.0.1")));
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("  198.51.100.23\n", "198.51.100.23")]
    [InlineData("1", null)] // IPAddress.Parse alone takes this as 0.0.0.1
    [InlineData("0x7f.1", null)]
    [InlineData("256.1.1.1", null)]
    [InlineData("2001:db8::1", null)]
    [InlineData("<html>Too many requests</html>", null)]
    [InlineData("203.0.113.7 extra", null)]
    [InlineData("127.0.0.1", null)]
    [InlineData("0.0.0.0", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void PublicReply_OnlyAPlainIPv4AddressIsTaken(string? reply, string? expected) =>
        Assert.Equal(expected, ServerAddresses.ParsePublicIPv4(reply));

    [Fact]
    public void JoinAddress_IsIpColonGamePort() =>
        Assert.Equal("192.168.1.20:17777", ServerAddresses.JoinAddress("192.168.1.20", 17777));
}
