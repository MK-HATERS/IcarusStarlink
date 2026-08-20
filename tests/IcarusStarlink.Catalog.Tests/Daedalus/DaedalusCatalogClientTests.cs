using IcarusStarlink.Catalog.Daedalus;

namespace IcarusStarlink.Catalog.Tests.Daedalus;

public class DaedalusCatalogClientTests
{
    // Shapes match the live endpoints as verified during Phase 4 planning (field names/casing,
    // the "215" no-prefix compatibility value, one mod with no categories at all).
    private const string ModsJson = """
        [
          {
            "id": "waldo--a-wzg-balance-overhaul",
            "name": "A WZG balance overhaul",
            "author": "Waldo",
            "version": "680.0",
            "compatibility": "w117",
            "description": "Easy, casual Icarus game mode.",
            "image_url": "https://example.com/wzg.png",
            "readme_url": "https://example.com/wzg.md",
            "files": { "pak": "https://example.com/wzg.pak" },
            "week": null
          },
          {
            "id": "aberiu--aberiu-s-supply-crates",
            "name": "Aberiu's Supply Crates",
            "author": "Aberiu",
            "version": "1.0.0",
            "compatibility": "215",
            "description": "Add supply crates for Prometheus resources.",
            "image_url": null,
            "readme_url": null,
            "files": { "exmodz": "https://example.com/crates.EXMODZ" }
          },
          {
            "id": "someone--uncategorized-mod",
            "name": "Uncategorized Mod",
            "author": "Someone",
            "version": "1.0",
            "compatibility": "All",
            "description": "Not referenced by any tag bucket.",
            "files": { "pak": "https://example.com/u.pak", "exmodz": "https://example.com/u.EXMODZ" }
          }
        ]
        """;

    private const string TagsJson = """
        [
          {
            "tag": "building",
            "slug": "building",
            "count": 1,
            "mods": [
              { "name": "A WZG balance overhaul", "author": "Waldo", "author_slug": "waldo", "slug": "a-wzg-balance-overhaul" }
            ]
          },
          {
            "tag": "balance",
            "slug": "balance",
            "count": 2,
            "mods": [
              { "name": "A WZG balance overhaul", "author": "Waldo", "author_slug": "waldo", "slug": "a-wzg-balance-overhaul" },
              { "name": "A Mod Not In mods.json", "author": "Ghost", "author_slug": "ghost", "slug": "a-mod-not-in-mods-json" }
            ]
          }
        ]
        """;

    private static DaedalusCatalogClient CreateClient()
    {
        var handler = new FakeHttpMessageHandler(new Dictionary<string, string>
        {
            ["https://agentkush.github.io/daedalus-static-poc/api/v1/mods.json"] = ModsJson,
            ["https://agentkush.github.io/daedalus-static-poc/api/v1/tags.json"] = TagsJson,
        });
        return new DaedalusCatalogClient(new HttpClient(handler));
    }

    [Fact]
    public async Task FetchAsync_ReturnsAllModsFromModsJson()
    {
        var entries = await CreateClient().FetchAsync();

        Assert.Equal(3, entries.Count);
        Assert.All(entries, e => Assert.Equal(CatalogSource.Daedalus, e.Source));
    }

    [Fact]
    public async Task FetchAsync_MapsFieldsIncludingSnakeCaseUrls()
    {
        var entries = await CreateClient().FetchAsync();
        var entry = entries.Single(e => e.Id == "waldo--a-wzg-balance-overhaul");

        Assert.Equal("A WZG balance overhaul", entry.Name);
        Assert.Equal("Waldo", entry.Author);
        Assert.Equal("680.0", entry.Version);
        Assert.Equal("https://example.com/wzg.png", entry.ImageUrl);
        Assert.Equal("https://example.com/wzg.md", entry.ReadmeUrl);
        Assert.Equal("https://example.com/wzg.pak", entry.PakUrl);
        Assert.Null(entry.ExmodzUrl);
    }

    [Fact]
    public async Task FetchAsync_ParsesCompatibleWeekFromWPrefixedAndBareValues()
    {
        var entries = await CreateClient().FetchAsync();

        Assert.Equal(117, entries.Single(e => e.Id == "waldo--a-wzg-balance-overhaul").CompatibleWeek);
        Assert.Equal(215, entries.Single(e => e.Id == "aberiu--aberiu-s-supply-crates").CompatibleWeek);
        Assert.Null(entries.Single(e => e.Id == "someone--uncategorized-mod").CompatibleWeek);
    }

    [Fact]
    public async Task FetchAsync_CrossReferencesCategoriesFromTagsJsonByNameAndAuthor()
    {
        var entries = await CreateClient().FetchAsync();

        var wzg = entries.Single(e => e.Id == "waldo--a-wzg-balance-overhaul");
        Assert.Equal(["building", "balance"], wzg.Categories);
    }

    [Fact]
    public async Task FetchAsync_ModNotReferencedByAnyTag_HasEmptyCategoriesNotAnError()
    {
        var entries = await CreateClient().FetchAsync();

        Assert.Empty(entries.Single(e => e.Id == "someone--uncategorized-mod").Categories);
    }

    [Fact]
    public async Task FetchAsync_TagsJsonReferencingAModAbsentFromModsJson_DoesNotThrow()
    {
        // "A Mod Not In mods.json" / Ghost, in the "balance" tag fixture above, has no
        // corresponding mods.json entry — mirrors the ~10% mismatch rate observed on the real
        // live snapshot. Should be silently ignored, not fail the whole fetch.
        var exception = await Record.ExceptionAsync(async () => await CreateClient().FetchAsync());

        Assert.Null(exception);
    }
}
