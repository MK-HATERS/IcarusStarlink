using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.PakIO.Install;

public interface IUe4ssLoaderInstallService
{
    /// <summary>Reads whatever's really there right now — dwmapi.dll/UE4SS.dll existence and the version UE4SS itself wrote into UE4SS.log on its last launch. Never throws; "not installed" if nothing's found.</summary>
    Ue4ssLoaderStatus GetStatus(string icarusContentPath);

    /// <summary>
    /// Backs up the whole existing ue4ss\ folder plus dwmapi.dll (keep-last-5), then extracts
    /// downloadedZipPath's real UE4SS release contents into place: dwmapi.dll goes to
    /// Binaries\Win64\, everything else (UE4SS.dll, UE4SS-settings.ini, docs) goes to
    /// Binaries\Win64\ue4ss\. The zip's own bundled Mods\ folder (the framework's built-in mods) is
    /// applied additively — a file/folder that already exists at the destination is left alone, so
    /// this never overwrites a user's own customized UE4SS-settings.ini or any already-installed
    /// mod, framework or otherwise. Caller is responsible for the real, hard-to-reverse-write gate —
    /// this method performs the write unconditionally once called.
    /// </summary>
    Task InstallOrUpdateAsync(string icarusContentPath, string downloadedZipPath, string backupDirectory, CancellationToken cancellationToken = default);
}
