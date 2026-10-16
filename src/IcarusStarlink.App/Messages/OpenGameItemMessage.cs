namespace IcarusStarlink.App.Messages;

/// <summary>Shows a game item on the Editor page (sent by the mod editor's "Open in Editor").</summary>
public sealed record OpenGameItemMessage(string ItemName);
