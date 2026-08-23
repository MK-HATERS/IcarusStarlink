namespace IcarusStarlink.PakIO.Pak;

/// <summary>What VerifyAsync concluded about an UnrealPak.exe path.</summary>
public enum UnrealPakHealth
{
    /// <summary>No file at the path at all.</summary>
    Missing,

    /// <summary>The exe exists but wouldn't run (a missing sibling DLL, a corrupt copy) — the case Reinstall exists for.</summary>
    Broken,

    /// <summary>The exe runs.</summary>
    Ok,
}

/// <param name="EngineVersion">From the sibling UnrealPak.version file when present (e.g. "4.27.0"), else null. Icarus is a UE 4.27 title — a 4.x version is the compatibility signal that matters, NOT recency: a newer UE5 UnrealPak writes pak formats a 4.27 engine can't mount, which is why nothing here ever chases "latest".</param>
public sealed record UnrealPakVerifyResult(UnrealPakHealth Health, string? EngineVersion, string? Detail);

/// <summary>
/// Locate-or-install for UnrealPak.exe: the release zip can ship a Tools\UnrealPak.zip payload
/// (added at packaging time — Epic's binaries are deliberately not committed to this repo), which
/// installs next to the app. First launch verifies the configured path and offers this when
/// there isn't one; Settings offers verify/reinstall/change-location thereafter.
/// </summary>
public interface IUnrealPakInstaller
{
    /// <summary>Where the bundled copy lives once installed — Tools\UnrealPak\Engine\Binaries\Win64\UnrealPak.exe next to the app.</summary>
    string InstalledExePath { get; }

    /// <summary>Whether the release's Tools\UnrealPak.zip payload is present — a dev build (or a stripped-down zip) may not carry it, and every install/reinstall affordance greys out without it.</summary>
    bool PayloadAvailable { get; }

    /// <summary>Really runs the exe (bare invocation — UnrealPak prints its usage and exits), because that's the only check that catches a copy whose sibling DLLs are missing; File.Exists alone can't.</summary>
    Task<UnrealPakVerifyResult> VerifyAsync(string exePath, CancellationToken cancellationToken = default);

    /// <summary>Extracts the payload next to the app (deleting any previous bundled install first, which is what makes this double as Reinstall/repair) and returns the exe path.</summary>
    Task<string> InstallAsync(CancellationToken cancellationToken = default);
}
