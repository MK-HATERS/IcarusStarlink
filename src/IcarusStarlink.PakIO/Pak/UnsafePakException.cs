namespace IcarusStarlink.PakIO.Pak;

/// <summary>
/// A pak Starlink refuses to unpack: a file name in it would land outside the folder it is unpacked
/// into, its file list can't be read safely, or it would unpack to more than Starlink allows or the
/// drive has free. Thrown before UnrealPak writes anything.
/// </summary>
public sealed class UnsafePakException(string message) : FormatException(message);
