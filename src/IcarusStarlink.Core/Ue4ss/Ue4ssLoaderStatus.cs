namespace IcarusStarlink.Core.Ue4ss;

/// <summary>Whatever this app can determine about the real, currently-installed UE4SS loader (dwmapi.dll + Binaries\Win64\ue4ss\UE4SS.dll) — not the mod folders under it, see IUe4ssModRepository for those.</summary>
/// <param name="InstalledCommit">The git commit UE4SS.log names ("Git SHA #1c1a1497"): what tells an official release and the dev builds after it apart, since they all name the same version. Null until UE4SS has run once.</param>
/// <param name="HasOldLayout">UE4SS is there the old way (UE4SS.dll, Mods\ and UE4SS-settings.ini directly in Binaries\Win64, see Ue4ssOldLayout) and not in the ue4ss folder: IsInstalled is false, the version is the old setup's, and an install moves it over.</param>
public sealed record Ue4ssLoaderStatus(bool IsInstalled, string? InstalledVersion, string? InstalledCommit = null, bool HasOldLayout = false);
