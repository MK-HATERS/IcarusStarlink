using IcarusStarlink.Core.Skins;
using Microsoft.Extensions.Logging;

namespace IcarusStarlink.Storage.Skins;

public sealed class CustomSkinStore(string filePath, ILogger<CustomSkinStore> logger) : ICustomSkinStore
{
    public string FilePath => filePath;

    public CustomSkin? Load() =>
        File.Exists(filePath) ? JsonFileStore.Load(filePath, () => new CustomSkin(), logger) : null;

    public void Save(CustomSkin skin)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        JsonFileStore.Save(filePath, skin);
    }
}
