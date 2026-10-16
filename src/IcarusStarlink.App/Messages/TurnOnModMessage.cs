namespace IcarusStarlink.App.Messages;

/// <summary>"Turn on and go to Merge & Install": queue this Library mod and show that page (sent after saving in the Editor page or the mod editor).</summary>
public sealed record TurnOnModMessage(string FolderName);
