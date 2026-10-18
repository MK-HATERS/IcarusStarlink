namespace IcarusStarlink.Core.Ue4ss;

/// <summary>Whatever this app can determine about the real, currently-installed UE4SS loader (dwmapi.dll + Binaries\Win64\ue4ss\UE4SS.dll) — not the mod folders under it, see IUe4ssModRepository for those.</summary>
/// <param name="InstalledCommit">The git commit UE4SS.log names ("Git SHA #1c1a1497"): what tells the official release and each dev build apart, since they all say "v3.0.1". Null until UE4SS has run once.</param>
public sealed record Ue4ssLoaderStatus(bool IsInstalled, string? InstalledVersion, string? InstalledCommit = null);
