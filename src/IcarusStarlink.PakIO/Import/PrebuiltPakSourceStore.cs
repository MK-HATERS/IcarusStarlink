namespace IcarusStarlink.PakIO.Import;

public sealed class PrebuiltPakSourceStore(string storeDirectory) : IPrebuiltPakSourceStore
{
    public void Save(string folderName, string pakFilePath)
    {
        Directory.CreateDirectory(storeDirectory);
        File.Copy(pakFilePath, ResolvePath(folderName), overwrite: true);
    }

    public string? TryGetPath(string folderName)
    {
        var path = ResolvePath(folderName);
        return File.Exists(path) ? path : null;
    }

    public void Delete(string folderName)
    {
        var path = ResolvePath(folderName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    // folderName always comes from LibraryEntry.FolderName — already guaranteed a safe, simple
    // identifier (AssetPathGuard/MakeUniqueFolderName run wherever a folder name is first minted),
    // so no extra validation is needed at this, its third or fourth reuse as a bare path segment.
    private string ResolvePath(string folderName) => Path.Combine(storeDirectory, $"{folderName}.pak");
}
