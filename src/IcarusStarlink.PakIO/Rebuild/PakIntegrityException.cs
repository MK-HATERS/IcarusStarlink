namespace IcarusStarlink.PakIO.Rebuild;

/// <summary>
/// The built pak failed UnrealPak's own post-build -Verify integrity check — the one rebuild
/// failure that happens AFTER a pak file exists on disk, so RebuildService deletes that file
/// before throwing this. A distinct type so VerifyPakIntegrityAsync's "a verification tool
/// error is only a warning" catch can let this one through.
/// </summary>
public sealed class PakIntegrityException(string message) : InvalidOperationException(message);
