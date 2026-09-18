namespace IcarusStarlink.App.Messages;

/// <summary>
/// Broadcast whenever the saved Nexus API key changes — a successful Authorize or an explicit Sign
/// out in Settings. First consumer: NexusCatalogViewModel re-checking its own IsPremium flag so a
/// user who signs in (or out) while already on the Nexus page sees the Download/Open page
/// prominence update immediately, instead of only on next launch.
/// </summary>
public sealed record NexusAccountChangedMessage;
