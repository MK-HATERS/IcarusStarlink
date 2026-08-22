namespace IcarusStarlink.Core.Ue4ss;

/// <summary>Whatever this app can determine about the real, currently-installed UE4SS loader (dwmapi.dll + Binaries\Win64\ue4ss\UE4SS.dll) — not the mod folders under it, see IUe4ssModRepository for those.</summary>
public sealed record Ue4ssLoaderStatus(bool IsInstalled, string? InstalledVersion);
