namespace IcarusStarlink.App.Messages;

/// <summary>
/// Sent after Merge &amp; Install builds a new merged pak, so pages that offer it (the Server page's
/// "Install my merged pak") re-check it straight away. The Server page also re-checks whenever it
/// is shown, so it stays right even if nothing sends this.
/// </summary>
public sealed record MergedPakBuiltMessage;
