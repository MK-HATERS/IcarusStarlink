using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using IcarusStarlink.Core.Server;

namespace IcarusStarlink.App.Services;

/// <summary>The real adapters (System.Net.NetworkInformation) and one plain GET to the public IP service.</summary>
public sealed class NetworkAddressService(HttpClient http) : INetworkAddressService
{
    public IReadOnlyList<NetworkAdapterInfo> ReadAdapters()
    {
        var adapters = new List<NetworkAdapterInfo>();
        NetworkInterface[] all;
        try
        {
            all = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return adapters;
        }

        foreach (var nic in all)
        {
            try
            {
                var props = nic.GetIPProperties();
                var hasGateway = props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any));
                var addresses = props.UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString())
                    .ToList();
                adapters.Add(new NetworkAdapterInfo(nic.Name, nic.Description, nic.OperationalStatus == OperationalStatus.Up, KindOf(nic.NetworkInterfaceType), hasGateway, addresses));
            }
            catch (NetworkInformationException)
            {
                // An adapter that vanished while being read: skip it.
            }
        }

        return adapters;
    }

    private static NetworkAdapterKind KindOf(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx
            or NetworkInterfaceType.Ethernet3Megabit => NetworkAdapterKind.Ethernet,
        NetworkInterfaceType.Wireless80211 => NetworkAdapterKind.Wireless,
        NetworkInterfaceType.Loopback => NetworkAdapterKind.Loopback,
        NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp => NetworkAdapterKind.Tunnel,
        _ => NetworkAdapterKind.Other,
    };

    public async Task<string> FetchPublicIpReplyAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ServerAddresses.PublicIpServiceUrl);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        // Read only the first few characters: an IPv4 address is short, and nothing longer is used.
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var buffer = new char[ServerAddresses.MaxPublicIpReplyLength];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
        return new string(buffer, 0, read);
    }
}
