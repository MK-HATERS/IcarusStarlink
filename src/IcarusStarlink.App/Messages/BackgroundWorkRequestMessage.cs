using CommunityToolkit.Mvvm.Messaging.Messages;

namespace IcarusStarlink.App.Messages;

/// <summary>
/// Asked before the app closes itself for an update: every page with work that must not be cut off
/// midway (SteamCMD installing a server, automation restarting one, an FTP upload) replies with a
/// short description of it ("SteamCMD is updating a server"). No replies means it is safe to close.
/// </summary>
public sealed class BackgroundWorkRequestMessage : CollectionRequestMessage<string>;
