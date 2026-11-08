using IcarusStarlink.Core.Skins;
using Microsoft.Extensions.Logging;

namespace IcarusStarlink.Storage.Skins;

public sealed class CustomSkinStore(string filePath, ILogger<CustomSkinStore> logger) : ICustomSkinStore
{
    public string FilePath => filePath;

    public CustomSkin? Load() => Load(out _);

    public CustomSkin? Load(out string? problem)
    {
        problem = null;
        if (!File.Exists(filePath))
        {
            return null;
        }

        var skin = JsonFileStore.Load(filePath, () => new CustomSkin(), logger, out ListLoadProblem kind);
        problem = Describe(kind);
        return skin;
    }

    public void Save(CustomSkin skin)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        // A hand-edited file that doesn't load (a stray comma) is the player's work: keep it beside
        // the new one as custom_skin.json.unreadable-<time>.bak. If that copy fails, this throws
        // and the file isn't overwritten either.
        if (File.Exists(filePath))
        {
            JsonFileStore.Load(filePath, () => new CustomSkin(), logger, out ListLoadProblem kind);
            JsonFileStore.KeepUnreadableOriginal(filePath, ref kind, logger);
        }

        JsonFileStore.Save(filePath, skin);
    }

    private static string? Describe(ListLoadProblem kind) => kind switch
    {
        ListLoadProblem.Corrupt => "it isn't valid JSON (look for a missing or extra comma or quote)",
        ListLoadProblem.Unreadable => "another program has it open",
        _ => null,
    };
}
