namespace IcarusStarlink.PakIO.Install;

/// <summary>
/// What this app can positively identify as currently installed in the real game folder, read
/// from its own small manifest (ISL-Merged.txt) — empty if nothing's been installed yet, or if the
/// manifest predates this app's own knowledge of it (e.g. a pak installed by hand, or classic
/// IMM's own IMM_Merged_Mod_P.pak, has no ISL-Merged.txt at all, so its contents genuinely can't be
/// known without parsing the pak itself). UE4SS mods aren't part of this — see IUe4ssModStateService.
/// </summary>
public sealed record InstalledState(IReadOnlyList<string> ModNames);
