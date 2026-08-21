namespace IcarusStarlink.PakIO.Install;

public sealed record InstallResult(string InstalledPakPath, string? BackupPakPath, IReadOnlyList<string> InstalledUe4ssModNames);
