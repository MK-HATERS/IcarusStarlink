namespace IcarusStarlink.Core.Ue4ss;

/// <summary>One known UE4SS mod (found either in the real game Mods folder or in this app's own staging) and whether it's currently enabled — i.e. currently present in the real game Mods folder.</summary>
public sealed record Ue4ssModState(string Name, bool IsEnabled);
