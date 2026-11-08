using System.IO;
using IcarusStarlink.App.Services;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>The shared index of the game's rows ("Add item from game data", "Search game data", Find the broken mod).</summary>
[Collection(MessengerCollection.Name)]
public sealed class GameDataIndexCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "GameDataIndex_" + Guid.NewGuid().ToString("N"));

    public GameDataIndexCacheTests()
    {
        var path = Path.Combine(_root, "Items", "D_Test.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"A","Stats":{"(Value=\"X_+%\")":1}}]}""");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>Regression: asking for the row names (Find the broken mod, "Add item from game data") also built and kept every row's JSON.</summary>
    [Fact]
    public async Task TheRowNames_AreBuiltWithoutTheRowsJson_UntilSearchIsAskedFor()
    {
        var cache = new GameDataIndexCache();

        var items = await cache.GetItemIndexAsync(_root);

        Assert.Equal("A", Assert.Single(items).ItemName);
        Assert.False(cache.HasSearchIndex);

        Assert.Single(await cache.GetSearchIndexAsync(_root));
        Assert.True(cache.HasSearchIndex);
        Assert.Equal("A", Assert.Single(await cache.GetItemIndexAsync(_root)).ItemName);
    }

    /// <summary>Regression: the index stayed for the whole session; now it's let go while the app sits unused and built again when asked.</summary>
    [Fact]
    public async Task AnIdleIndex_IsLetGo_AndBuiltAgainWhenAsked()
    {
        var cache = new GameDataIndexCache();
        Assert.False(cache.ReleaseIfIdle(TimeSpan.Zero));
        await cache.GetSearchIndexAsync(_root);

        Assert.False(cache.ReleaseIfIdle(TimeSpan.FromHours(1)));
        Assert.True(cache.ReleaseIfIdle(TimeSpan.Zero));
        Assert.False(cache.HasSearchIndex);

        Assert.Single(await cache.GetSearchIndexAsync(_root));
        Assert.True(cache.HasSearchIndex);
    }

    /// <summary>Regression: rows were kept as escaped JSON, so searching for a stat key with "+" or quotes found nothing.</summary>
    [Fact]
    public async Task SearchIndex_KeepsPlusAndQuotesAsTheyAre()
    {
        var entry = Assert.Single(await new GameDataIndexCache().GetSearchIndexAsync(_root));

        Assert.Contains("(Value=\\\"X_+%\\\")", entry.RowJson, StringComparison.Ordinal);
    }
}
