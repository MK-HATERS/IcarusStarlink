using IcarusStarlink.Core.Server;

namespace IcarusStarlink.App.Services;

/// <summary>
/// Where friends join a server on this PC: the network adapters (for the home network address)
/// and, only when the owner asks, this PC's public address from a "what is my IP" service. An
/// interface so the view model's tests never read the real adapters or go online.
/// </summary>
public interface INetworkAddressService
{
    /// <summary>This PC's network adapters as Windows reports them right now (none when they can't be read).</summary>
    IReadOnlyList<NetworkAdapterInfo> ReadAdapters();

    /// <summary>
    /// Asks <see cref="ServerAddresses.PublicIpServiceUrl"/> for this PC's public address and returns
    /// its reply text, at most <see cref="ServerAddresses.MaxPublicIpReplyLength"/> characters (it is
    /// checked by the caller). Throws when the request fails or the token is cancelled.
    /// </summary>
    Task<string> FetchPublicIpReplyAsync(CancellationToken cancellationToken);
}
