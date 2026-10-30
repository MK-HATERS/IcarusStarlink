using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace IcarusStarlink.Core.Server;

/// <summary>What the app read about one network adapter: enough to pick the address friends on the home network join.</summary>
/// <param name="Name">Windows' name for it ("Ethernet", "Wi-Fi", "vEthernet (WSL)").</param>
/// <param name="Description">The driver's description ("Intel(R) Ethernet Connection", "Hyper-V Virtual Ethernet Adapter").</param>
/// <param name="IsUp">Connected right now.</param>
/// <param name="Kind">Wired, wireless, loopback, tunnel or other.</param>
/// <param name="HasGateway">It has an IPv4 default gateway (a router), so it is the way out of this PC.</param>
/// <param name="IPv4Addresses">Its IPv4 addresses, as text.</param>
public sealed record NetworkAdapterInfo(
    string Name, string Description, bool IsUp, NetworkAdapterKind Kind, bool HasGateway, IReadOnlyList<string> IPv4Addresses);

public enum NetworkAdapterKind
{
    Other,
    Ethernet,
    Wireless,
    Loopback,
    Tunnel,
}

/// <summary>
/// The addresses players type into the in-game Direct Connect box (IP:game port). Kept free of
/// the network itself so the rules are testable: the app reads the adapters, and asks a public
/// "what is my IP" service only when the owner presses the button.
/// </summary>
public static partial class ServerAddresses
{
    /// <summary>The service asked for this PC's public address, only on "Show my public IP". It answers with the bare IPv4 address as plain text.</summary>
    public const string PublicIpServiceUrl = "https://api.ipify.org";

    /// <summary>Most characters read from the public IP service's reply. An IPv4 address is at most 15; the rest is room for spaces and line breaks around it.</summary>
    public const int MaxPublicIpReplyLength = 64;

    /// <summary>
    /// Words in an adapter's name or description that mark it as virtual or a VPN: Hyper-V and WSL
    /// switches, VM host adapters, VPN and mesh tunnels. Their addresses aren't where friends on
    /// the home network can reach this PC, so they never win, even with a gateway.
    /// </summary>
    private static readonly string[] VirtualMarkers =
    [
        "virtual", "vethernet", "hyper-v", "wsl", "vmware", "virtualbox", "vbox", "docker", "loopback",
        "vpn", "tap-", "tap ", "tun", "wireguard", "wintun", "tailscale", "zerotier", "hamachi", "openvpn", "nordlynx",
        "anyconnect", "fortinet", "forticlient", "globalprotect", "pangp", "juniper", "sonicwall", "proton", "mullvad",
        "radmin", "npcap", "bluetooth",
    ];

    /// <summary>
    /// This PC's address on the home network: an IPv4 address of an adapter that is up, isn't
    /// loopback or a tunnel, has a default gateway, and isn't virtual or a VPN. A private address
    /// (192.168.x.x, 10.x.x.x, 172.16-31.x.x) beats any other; then wired beats wireless beats
    /// any other kind; otherwise the first in Windows' order. Self-assigned 169.254.x.x addresses never
    /// count. Null when no adapter qualifies.
    /// </summary>
    public static string? PickLanAddress(IEnumerable<NetworkAdapterInfo> adapters)
    {
        var candidates = new List<(string Address, int Rank, int Order)>();
        var order = 0;
        foreach (var adapter in adapters)
        {
            order++;
            if (!adapter.IsUp || !adapter.HasGateway
                || adapter.Kind is NetworkAdapterKind.Loopback or NetworkAdapterKind.Tunnel
                || LooksVirtual(adapter))
            {
                continue;
            }

            foreach (var text in adapter.IPv4Addresses)
            {
                if (!TryParseIPv4(text, out var ip) || IPAddress.IsLoopback(ip) || IsLinkLocal(ip) || ip.Equals(IPAddress.Any))
                {
                    continue;
                }

                var rank = (IsPrivate(ip) ? 0 : 10) + (adapter.Kind switch
                {
                    NetworkAdapterKind.Ethernet => 0,
                    NetworkAdapterKind.Wireless => 1,
                    _ => 2,
                });
                candidates.Add((ip.ToString(), rank, order));
            }
        }

        return candidates.OrderBy(c => c.Rank).ThenBy(c => c.Order).Select(c => c.Address).FirstOrDefault();
    }

    public static bool LooksVirtual(NetworkAdapterInfo adapter)
    {
        var text = $" {adapter.Name} {adapter.Description} ".ToLowerInvariant();
        return VirtualMarkers.Any(marker => marker == "tun" ? TunWord().IsMatch(text) : text.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>
    /// The public IP service's reply as an IPv4 address, or null when it is anything else (an
    /// error page, IPv6, extra text). Only a plain dotted quad is taken: IPAddress.Parse alone
    /// would also accept "1" or "0x7f.1".
    /// </summary>
    public static string? ParsePublicIPv4(string? reply)
    {
        var text = (reply ?? "").Trim();
        if (text.Length is 0 or > 15 || !DottedQuad().IsMatch(text) || !TryParseIPv4(text, out var ip)
            || IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.Broadcast))
        {
            return null;
        }

        return ip.ToString();
    }

    /// <summary>"192.168.1.20:17777", what the in-game Direct Connect box takes.</summary>
    public static string JoinAddress(string ip, int gamePort) => $"{ip}:{gamePort.ToString(CultureInfo.InvariantCulture)}";

    private static bool TryParseIPv4(string text, out IPAddress ip)
    {
        if (DottedQuad().IsMatch(text.Trim()) && IPAddress.TryParse(text.Trim(), out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            ip = parsed;
            return true;
        }

        ip = IPAddress.None;
        return false;
    }

    private static bool IsLinkLocal(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] == 169 && b[1] == 254;
    }

    private static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    [GeneratedRegex(@"^(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(\.(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}$")]
    private static partial Regex DottedQuad();

    /// <summary>"tun" as its own word ("TUN adapter", "tun0"), not inside "Intel Gigabit TUNed".</summary>
    [GeneratedRegex(@"[^a-z]tun\d*[^a-z]")]
    private static partial Regex TunWord();
}
