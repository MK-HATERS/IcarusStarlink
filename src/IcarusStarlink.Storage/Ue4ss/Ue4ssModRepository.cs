using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Install;
using IcarusStarlink.PakIO.Ue4ss;

namespace IcarusStarlink.Storage.Ue4ss;

public sealed class Ue4ssModRepository : IUe4ssModRepository
{
    private readonly string _stagedDirectory;

    public Ue4ssModRepository(string stagedDirectory)
    {
        _stagedDirectory = stagedDirectory;
        Directory.CreateDirectory(stagedDirectory);
    }

    public IReadOnlyList<string> GetAll() =>
        [.. Directory.GetDirectories(_stagedDirectory).Select(Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)!];

    public string Import(string zipFilePath)
    {
        // Opens and scans the zip's entries once — DeriveFolderName+Extract used to each do this
        // independently, opening and scanning the same archive twice for every import.
        using var archive = Ue4ssModArchive.Open(zipFilePath);
        var folderName = MakeUniqueFolderName(archive.DerivedFolderName);
        var targetFolder = Path.Combine(_stagedDirectory, folderName);
        archive.ExtractTo(targetFolder);
        return folderName;
    }

    public void Delete(string folderName) => Directory.Delete(ResolveFolder(folderName), recursive: true);

    public string GetFolderPath(string folderName) => ResolveFolder(folderName);

    public IReadOnlyList<string> ListInstalledInGame(string gameModsFolderPath) =>
        Directory.Exists(gameModsFolderPath)
            ? [.. Directory.GetDirectories(gameModsFolderPath).Select(Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)!]
            : [];

    public string AdoptFromGame(string gameModsFolderPath, string folderName)
    {
        var sourceFolder = Path.Combine(gameModsFolderPath, folderName);
        if (!Directory.Exists(sourceFolder))
        {
            throw new DirectoryNotFoundException($"'{folderName}' isn't in the game's UE4SS Mods folder.");
        }

        var targetName = MakeUniqueFolderName(folderName);
        FolderBackup.CopyDirectory(sourceFolder, Path.Combine(_stagedDirectory, targetName));
        return targetName;
    }

    private string MakeUniqueFolderName(string name)
    {
        var candidate = name;
        var suffix = 1;
        while (Directory.Exists(Path.Combine(_stagedDirectory, candidate)))
        {
            candidate = $"{name}_{++suffix}";
        }

        return candidate;
    }

    private string ResolveFolder(string folderName)
    {
        var folder = Path.Combine(_stagedDirectory, folderName);
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"No staged UE4SS mod named '{folderName}'.");
        }

        return folder;
    }
}
