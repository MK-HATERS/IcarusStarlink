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

    public string Import(string zipFilePath, IReadOnlyCollection<string>? namesAlreadyInUse = null)
    {
        // Opens and scans the zip's entries once — DeriveFolderName+Extract used to each do this
        // independently, opening and scanning the same archive twice for every import.
        using var archive = Ue4ssModArchive.Open(zipFilePath);
        var folderName = MakeUniqueFolderName(archive.DerivedFolderName, namesAlreadyInUse);
        var targetFolder = Path.Combine(_stagedDirectory, folderName);
        archive.ExtractTo(targetFolder);
        return folderName;
    }

    public string ImportFromFolder(string sourceFolder, string fallbackName, IReadOnlyCollection<string>? namesAlreadyInUse = null)
    {
        var wrapping = FindSingleWrappingFolder(sourceFolder);
        var actualSource = wrapping ?? sourceFolder;
        var derivedName = wrapping is not null ? Path.GetFileName(wrapping)! : fallbackName;

        var folderName = MakeUniqueFolderName(derivedName, namesAlreadyInUse);
        FolderBackup.CopyDirectory(actualSource, Path.Combine(_stagedDirectory, folderName));
        return folderName;
    }

    public void Delete(string folderName) => Directory.Delete(ResolveFolder(folderName), recursive: true);

    public string GetFolderPath(string folderName) => ResolveFolder(folderName);

    public IReadOnlyList<string> ListInstalledInGame(string gameModsFolderPath) =>
        Directory.Exists(gameModsFolderPath)
            ? [.. Directory.GetDirectories(gameModsFolderPath).Select(Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)!]
            : [];

    public string AdoptFromGame(string gameModsFolderPath, string folderName, IReadOnlyCollection<string>? namesAlreadyInUse = null)
    {
        var sourceFolder = Path.Combine(gameModsFolderPath, folderName);
        if (!Directory.Exists(sourceFolder))
        {
            throw new DirectoryNotFoundException($"'{folderName}' isn't in the game's UE4SS Mods folder.");
        }

        var targetName = MakeUniqueFolderName(folderName, namesAlreadyInUse);
        FolderBackup.CopyDirectory(sourceFolder, Path.Combine(_stagedDirectory, targetName));
        return targetName;
    }

    /// <summary>Every entry sits under one single top-level subfolder → that's a wrapping folder, same convention as Ue4ssModArchive's own zip-entry version of this check.</summary>
    private static string? FindSingleWrappingFolder(string sourceFolder)
    {
        var entries = Directory.GetFileSystemEntries(sourceFolder);
        return entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : null;
    }

    private string MakeUniqueFolderName(string name, IReadOnlyCollection<string>? additionalNamesToAvoid = null) =>
        ModFolders.MakeUnique(_stagedDirectory, name, additionalNamesToAvoid);

    private string ResolveFolder(string folderName) =>
        ModFolders.Resolve(_stagedDirectory, folderName, $"No staged UE4SS mod named '{folderName}'.");
}
