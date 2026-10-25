namespace IcarusStarlink.App.Messages;

/// <summary>A download of Starlink itself from its Nexus page (mod 330): Settings offers it as an app update instead of it being imported as a mod.</summary>
public sealed record StarlinkUpdateLinkMessage(string NxmUrl);
