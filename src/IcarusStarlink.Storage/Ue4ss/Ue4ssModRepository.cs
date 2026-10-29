using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Install;

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

    /// <summary>Every entry sits under one single top-level subfolder → that's a wrapping folder.</summary>
    private static string? FindSingleWrappingFolder(string sourceFolder)
    {
        var entries = Directory.GetFileSystemEntries(sourceFolder);
        if (entries.Length != 1 || !Directory.Exists(entries[0]))
        {
            return null;
        }

        // A mod whose folder holds ONLY its Scripts (or dlls) subfolder is already the mod — that
        // subfolder is the UE4SS layout, not a wrapper, and stripping it named the mod "Scripts".
        var name = Path.GetFileName(entries[0]);
        return name.Equals("Scripts", StringComparison.OrdinalIgnoreCase) || name.Equals("dlls", StringComparison.OrdinalIgnoreCase)
            ? null
            : entries[0];
    }

    private string MakeUniqueFolderName(string name, IReadOnlyCollection<string>? additionalNamesToAvoid = null) =>
        ModFolders.MakeUnique(_stagedDirectory, name, additionalNamesToAvoid);

    private string ResolveFolder(string folderName) =>
        ModFolders.Resolve(_stagedDirectory, folderName, $"No staged UE4SS mod named '{folderName}'.");
}
