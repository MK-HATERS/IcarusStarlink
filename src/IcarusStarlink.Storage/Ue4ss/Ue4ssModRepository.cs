using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Install;
using IcarusStarlink.PakIO.Safety;

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
        [.. Directory.GetDirectories(_stagedDirectory).Select(Path.GetFileName).OfType<string>().OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];

    public string ImportFromFolder(string sourceFolder, string fallbackName, IReadOnlyCollection<string>? namesAlreadyInUse = null)
    {
        var wrapping = FindSingleWrappingFolder(sourceFolder);
        var actualSource = wrapping ?? sourceFolder;
        // The fallback is often typed by the player (Install manually): a name with ':' or '\' in it,
        // or a full path, would fail to copy, stage the mod a folder too deep for UE4SS, or copy it
        // outside the staging folder. It becomes one plain folder name, as data mods' names do.
        var derivedName = AssetPathGuard.SanitizeToSimpleFileName(wrapping is not null ? Path.GetFileName(wrapping) : fallbackName.Trim());

        var folderName = MakeUniqueFolderName(derivedName, namesAlreadyInUse);
        FolderBackup.CopyDirectory(actualSource, Path.Combine(_stagedDirectory, folderName));
        return folderName;
    }

    public void Delete(string folderName) => Directory.Delete(ResolveFolder(folderName), recursive: true);

    public string GetFolderPath(string folderName) => ResolveFolder(folderName);

    public IReadOnlyList<string> ListInstalledInGame(string gameModsFolderPath) =>
        Directory.Exists(gameModsFolderPath)
            ? [.. Directory.GetDirectories(gameModsFolderPath).Select(Path.GetFileName).OfType<string>().OrderBy(n => n, StringComparer.OrdinalIgnoreCase)]
            : [];

    public string AdoptFromGame(string gameModsFolderPath, string folderName, IReadOnlyCollection<string>? namesAlreadyInUse = null)
    {
        // A name that isn't one folder ("", "..", "a\b") would read outside the game's Mods folder.
        var sourceFolder = AssetPathGuard.IsSimpleFileName(folderName) ? Path.Combine(gameModsFolderPath, folderName) : null;
        if (sourceFolder is null || !Directory.Exists(sourceFolder))
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

    /// <summary>
    /// A name that isn't one folder is refused like a missing mod: "" would be the staging folder
    /// itself (Delete("") deleted every staged mod) and ".." its parent.
    /// </summary>
    private string ResolveFolder(string folderName) =>
        AssetPathGuard.IsSimpleFileName(folderName)
            ? ModFolders.Resolve(_stagedDirectory, folderName, $"No staged UE4SS mod named '{folderName}'.")
            : throw new DirectoryNotFoundException($"No staged UE4SS mod named '{folderName}'.");
}
