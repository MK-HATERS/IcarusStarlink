using IcarusStarlink.Core.Skins;
using IcarusStarlink.Storage.Skins;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcarusStarlink.Storage.Tests.Skins;

public sealed class CustomSkinStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests", $"Skins_{Guid.NewGuid():N}");
    private readonly CustomSkinStore _store;

    public CustomSkinStoreTests()
    {
        _store = new CustomSkinStore(Path.Combine(_root, "custom_skin.json"), NullLogger<CustomSkinStore>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Load_NoFile_ReturnsNull()
    {
        Assert.Null(_store.Load());
    }

    [Fact]
    public void SaveAndLoad_RoundTripsEveryColor()
    {
        _store.Save(new CustomSkin
        {
            Colors = new Dictionary<string, string>
            {
                ["AccentBrush"] = "#FF8A3D",
                ["AccentSoftBrush"] = "#26FF8A3D",
            },
        });

        var loaded = _store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Colors.Count);
        Assert.Equal("#FF8A3D", loaded.Colors["AccentBrush"]);
        Assert.Equal("#26FF8A3D", loaded.Colors["AccentSoftBrush"]);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmptySkinNotNull()
    {
        // Distinct from the no-file case: a file that exists but can't be parsed degrades to an
        // empty skin (every token falls back to Icarus values) rather than looking like "no skin
        // yet", which would silently overwrite the user's file with a fresh template.
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "custom_skin.json"), "{not valid json");

        var loaded = _store.Load();

        Assert.NotNull(loaded);
        Assert.Empty(loaded.Colors);
    }
}
