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

    /// <summary>Regression: a hand-edited "Colors": null came back as a null map, and applying it at launch stopped Starlink from starting.</summary>
    [Fact]
    public void Load_ColorsNull_GivesAnEmptyMapAndNoProblem()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(_store.FilePath, """{ "Colors": null }""");

        var loaded = _store.Load(out var problem);

        Assert.NotNull(loaded);
        Assert.NotNull(loaded.Colors);
        Assert.Empty(loaded.Colors);
        Assert.Null(problem);
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

    /// <summary>Regression: a skin file that isn't valid JSON loaded as an empty skin with no word why, so Reload skin file said "reloaded" over a rejected edit.</summary>
    [Fact]
    public void Load_NotValidJson_SaysWhy()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "custom_skin.json"), "{not valid json");

        var loaded = ((ICustomSkinStore)_store).Load(out var problem);

        Assert.NotNull(loaded);
        Assert.NotNull(problem);
        Assert.Contains("valid JSON", problem);
    }

    [Fact]
    public void Load_GoodFile_HasNoProblem()
    {
        _store.Save(new CustomSkin { Colors = { ["AccentBrush"] = "#FF8A3D" } });

        var loaded = ((ICustomSkinStore)_store).Load(out var problem);

        Assert.Null(problem);
        Assert.Equal("#FF8A3D", loaded!.Colors["AccentBrush"]);
    }

    /// <summary>Regression: Save skin wrote the editor's colours over a hand-made skin file that had one stray comma, with no copy kept.</summary>
    [Fact]
    public void Save_OverAFileThatIsntValidJson_KeepsTheOldFile()
    {
        Directory.CreateDirectory(_root);
        const string original = "{\"Colors\":{\"AccentBrush\":\"#FF0000\",}}";
        File.WriteAllText(Path.Combine(_root, "custom_skin.json"), original);

        _store.Save(new CustomSkin { Colors = { ["AccentBrush"] = "#00FF00" } });

        var kept = Assert.Single(Directory.GetFiles(_root, "custom_skin.json.unreadable-*.bak"));
        Assert.Equal(original, File.ReadAllText(kept));
        Assert.Equal("#00FF00", _store.Load()!.Colors["AccentBrush"]);
    }

    [Fact]
    public void Save_OverAGoodFile_KeepsNoCopy()
    {
        _store.Save(new CustomSkin { Colors = { ["AccentBrush"] = "#FF0000" } });

        _store.Save(new CustomSkin { Colors = { ["AccentBrush"] = "#00FF00" } });

        Assert.Empty(Directory.GetFiles(_root, "*.bak"));
        Assert.Equal("#00FF00", _store.Load()!.Colors["AccentBrush"]);
    }
}
