namespace IcarusStarlink.App.ViewModels;

/// <summary>One copy of a UE4SS mod whose config can be edited: the game's (or Starlink's staging, while the mod is off), or a Starlink server's.</summary>
public sealed record ModConfigLocation(string Name, string ModFolder, bool IsServer)
{
    public override string ToString() => Name;
}
