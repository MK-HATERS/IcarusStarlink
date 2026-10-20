namespace IcarusStarlink.Core.Ue4ss;

/// <summary>One known UE4SS mod, found in the real game Mods folder or in this app's own staging.</summary>
/// <param name="IsEnabled">UE4SS starts it: its folder is in the game and mods.txt says 1 or it has an enabled.txt (Ue4ssModStart).</param>
/// <param name="IsInGame">Its folder is in the game's Mods folder, started or not.</param>
/// <param name="WhyNotStarted">For a mod in the game that UE4SS doesn't start: why ("mods.txt switches it off…").</param>
public sealed record Ue4ssModState(string Name, bool IsEnabled, bool IsInGame = false, string? WhyNotStarted = null)
{
    /// <summary>In the game folder, but UE4SS doesn't start it: it does nothing.</summary>
    public bool IsSwitchedOffInGame => IsInGame && !IsEnabled;
}
