using IcarusStarlink.Core.Ue4ss;
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
        var folderName = MakeUniqueFolderName(Ue4ssModArchive.DeriveFolderName(zipFilePath));
        var targetFolder = Path.Combine(_stagedDirectory, folderName);
        Ue4ssModArchive.Extract(zipFilePath, targetFolder);
        return folderName;
    }

    public void Delete(string folderName) => Directory.Delete(ResolveFolder(folderName), recursive: true);

    public string GetFolderPath(string folderName) => ResolveFolder(folderName);

    public IReadOnlyList<string> ListInstalledInGame(string gameModsFolderPath) =>
        Directory.Exists(gameModsFolderPath)
            ? [.. Directory.GetDirectories(gameModsFolderPath).Select(Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)!]
            : [];

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
