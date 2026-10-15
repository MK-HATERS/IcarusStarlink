namespace IcarusStarlink.App.Messages;

/// <summary>Asks the Nexus page to search for the given text (and the shell to show that page) — sent by the pending-downloads list's "Find on Nexus" for a requirement a download's README names.</summary>
public sealed record SearchNexusMessage(string SearchText);
