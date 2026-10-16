namespace IcarusStarlink.App.Messages;

/// <summary>
/// Shows a game item on the Editor page (sent by the mod editor's "Open in Editor"). TargetFolder
/// is the mod it came from: when that's one of the user's own mods, the Editor page saves into it
/// and shows its values, not the game's.
/// </summary>
public sealed record OpenGameItemMessage(string ItemName, string? TargetFolder = null);
