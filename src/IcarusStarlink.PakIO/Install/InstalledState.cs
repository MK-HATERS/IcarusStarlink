namespace IcarusStarlink.PakIO.Install;

/// <summary>
/// What this app can positively identify as currently installed in the real game folder, read
/// from its own two small manifests (ISL-Merged.txt, ISL-Installed-Mods.txt) — empty lists if
/// nothing's been installed yet, or if the manifest predates this app's own knowledge of it (e.g.
/// a pak installed by hand, or classic IMM's own IMM_Merged_Mod_P.pak, has no ISL-Merged.txt at
/// all, so its contents genuinely can't be known without parsing the pak itself).
/// </summary>
public sealed record InstalledState(IReadOnlyList<string> ModNames, IReadOnlyList<string> Ue4ssModFolderNames);
