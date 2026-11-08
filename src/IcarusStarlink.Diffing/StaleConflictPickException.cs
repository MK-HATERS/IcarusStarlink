namespace IcarusStarlink.Diffing;

/// <summary>
/// A pick from Check the merge that no longer lines up with the mods changing its field: the queue
/// changed since it was made. The message is written for players (the build shows it as it is).
/// </summary>
public sealed class StaleConflictPickException(string message) : InvalidOperationException(message);
