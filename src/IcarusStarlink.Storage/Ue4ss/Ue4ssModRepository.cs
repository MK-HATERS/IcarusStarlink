using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Install;
using IcarusStarlink.PakIO.Ue4ss;

namespace IcarusStarlink.Storage.Ue4ss;

public sealed class Ue4ssModRepository : IUe4ssModRepository
{
    private readonly string _stagedDirectory;

    /// <summary>Sibling of staging ("Ue4ss_Paks" next to "Staged_UE4SS"): one subfolder per mod name holding its attached paks. Never moved by enable/disable.</summary>
    private readonly string _attachedPaksDirectory;

    public Ue4ssModRepository(string stagedDirectory)
    {
        _stagedDirectory = stagedDirectory;
        Directory.CreateDirectory(stagedDirectory);
        var parent = Path.GetDirectoryName(stagedDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? stagedDirectory;
        _attachedPaksDirectory = Path.Combine(parent, "Ue4ss_Paks");
    }

    public void AttachPaks(string folderName, IReadOnlyList<string> pakFilePaths)
    {
        // Resolve validates the name (a real staged mod, no path tricks) before anything is written.
        ResolveFolder(folderName);
        var target = Path.Combine(_attachedPaksDirectory, folderName);
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        Directory.CreateDirectory(target);
        foreach (var pakFilePath in pakFilePaths)
        {
            File.Copy(pakFilePath, Path.Combine(target, Path.GetFileName(pakFilePath)));
        }
    }

    public IReadOnlyList<string> ListAttachedPaks(string folderName)
    {
        var folder = Path.Combine(_attachedPaksDirectory, folderName);
        if (string.IsNullOrWhiteSpace(folderName) || folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !Directory.Exists(folder))
        {
            return [];
        }

        return [.. Directory.GetFiles(folder, "*.pak").OrderBy(p => p, StringComparer.OrdinalIgnoreCase)];
    }

    public void DeleteAttachedPaks(string folderName)
    {
        var folder = Path.Combine(_attachedPaksDirectory, folderName);
        if (!string.IsNullOrWhiteSpace(folderName) && folderName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
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
