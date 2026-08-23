using IcarusStarlink.Core.Ue4ss;
using Microsoft.Extensions.Logging;

namespace IcarusStarlink.Storage.Ue4ss;

/// <summary>File-backed IUe4ssModMetaStore — one JSON sidecar per UE4SS mod folder, under its own directory (parallel to Library_Meta), using the same JsonFileStore helper every other file-based store in this project shares.</summary>
public sealed class Ue4ssModMetaStore(string metaDirectory, ILogger<Ue4ssModMetaStore> logger) : IUe4ssModMetaStore
{
    public Ue4ssModMeta Load(string folderName) =>
        JsonFileStore.Load(GetPath(folderName), () => new Ue4ssModMeta(), logger);

    public void Save(string folderName, Ue4ssModMeta meta)
    {
        Directory.CreateDirectory(metaDirectory);
        JsonFileStore.Save(GetPath(folderName), meta);
    }

    public void Delete(string folderName)
    {
        var path = GetPath(folderName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string GetPath(string folderName) => Path.Combine(metaDirectory, $"{folderName}.json");
}
