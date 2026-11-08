namespace IcarusStarlink.App.Messages;

/// <summary>
/// Sent by the header's game watch when Icarus was running and has now closed. Merge &amp; Install
/// listens for it to run an "Install when Icarus closes" the player asked for.
/// </summary>
public sealed record GameClosedMessage;
