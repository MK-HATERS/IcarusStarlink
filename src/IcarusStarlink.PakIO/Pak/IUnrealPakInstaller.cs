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
/// Locate-or-install for UnrealPak.exe. A release zip can still ship a local Tools\UnrealPak.zip
/// payload (added at packaging time — Epic's binaries are deliberately not committed to this repo)
/// for a fully offline install; when there isn't one, InstallAsync instead downloads the same
/// payload from a fixed, dedicated GitHub release asset that isn't tied to the app's own version —
/// UnrealPak.exe is pinned to UE 4.27 and effectively never changes, so there's no reason to
/// re-bundle a fresh copy of the same bytes into every future app release. First launch verifies
/// the configured path and offers this when there isn't one; Settings offers
/// verify/reinstall/change-location thereafter.
/// </summary>
public interface IUnrealPakInstaller
{
    /// <summary>Where the installed copy lives — Tools\UnrealPak\Engine\Binaries\Win64\UnrealPak.exe next to the app.</summary>
    string InstalledExePath { get; }

    /// <summary>
    /// Whether an install can be offered right now. Always true — a local Tools\UnrealPak.zip is
    /// used when present, and the remote fallback is always attemptable (a real failure, e.g. no
    /// network, only surfaces once InstallAsync actually tries and throws). Kept as a property
    /// rather than dropped entirely since Settings/first-launch still use it to decide whether to
    /// show the install affordance at all.
    /// </summary>
    bool PayloadAvailable { get; }

    /// <summary>Really runs the exe (bare invocation — UnrealPak prints its usage and exits), because that's the only check that catches a copy whose sibling DLLs are missing; File.Exists alone can't.</summary>
    Task<UnrealPakVerifyResult> VerifyAsync(string exePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs next to the app (deleting any previous install first, which is what makes this
    /// double as Reinstall/repair) and returns the exe path. Uses a local Tools\UnrealPak.zip when
    /// the release zip carries one; otherwise downloads the same payload from a fixed GitHub
    /// release asset, which needs a working network connection.
    /// </summary>
    Task<string> InstallAsync(CancellationToken cancellationToken = default);
}
