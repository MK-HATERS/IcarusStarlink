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

/// <summary>Checks operation of a user-selected tool. Automatic payload installation is disabled.</summary>
public interface IUnrealPakInstaller
{
    /// <summary>Legacy install location; never automatically adopted.</summary>
    string InstalledExePath { get; }

    /// <summary>False: no authenticated Epic payload is provided.</summary>
    bool PayloadAvailable { get; }

    /// <summary>Runs the selected executable. This checks operation, not provenance.</summary>
    Task<UnrealPakVerifyResult> VerifyAsync(string exePath, CancellationToken cancellationToken = default);

    /// <summary>Refuses automatic installation; select your own UE 4.27 tool.</summary>
    Task<string> InstallAsync(CancellationToken cancellationToken = default);
}
