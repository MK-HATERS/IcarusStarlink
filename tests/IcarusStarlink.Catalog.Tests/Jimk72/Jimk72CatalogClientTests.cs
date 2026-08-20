using IcarusStarlink.Catalog.Jimk72;

namespace IcarusStarlink.Catalog.Tests.Jimk72;

public class Jimk72CatalogClientTests
{
    // Shape (the wrapping {"mods": [...]} object, imageURL/readmeURL casing, always-"All"
    // compatibility, always-exmodz-only files) matches the live endpoint as verified during
    // Phase 4 planning.
    private const string ModInfoJson = """
        {
          "mods": [
            {
              "name": "Jimk Cupboard Signs",
              "author": "Jimk72",
              "version": "1.0",
              "compatibility": "All",
              "description": "Adds 4 new signs.",
              "imageURL": "https://example.com/signs.png",
              "readmeURL": "https://example.com/signs.txt",
              "files": { "exmodz": "https://example.com/signs.EXMODZ" }
            }
          ]
        }
        """;

    private static Jimk72CatalogClient CreateClient()
    {
        var handler = new FakeHttpMessageHandler(new Dictionary<string, string>
        {
            ["https://raw.githubusercontent.com/Jimk72/Icarus_Mods/main/modinfo.json"] = ModInfoJson,
        });
        return new Jimk72CatalogClient(new HttpClient(handler));
    }

    [Fact]
    public async Task FetchAsync_MapsFieldsDespiteImageUrlCasing()
    {
        var entries = await CreateClient().FetchAsync();
        var entry = Assert.Single(entries);

        Assert.Equal(CatalogSource.Jimk72, entry.Source);
        Assert.Equal("Jimk Cupboard Signs", entry.Name);
        Assert.Equal("Jimk72", entry.Author);
        Assert.Equal("https://example.com/signs.png", entry.ImageUrl);
        Assert.Equal("https://example.com/signs.txt", entry.ReadmeUrl);
        Assert.Equal("https://example.com/signs.EXMODZ", entry.ExmodzUrl);
        Assert.Null(entry.PakUrl);
    }

    [Fact]
    public async Task FetchAsync_AllCompatibility_HasNoParsedWeek()
    {
        var entries = await CreateClient().FetchAsync();

        Assert.Null(Assert.Single(entries).CompatibleWeek);
    }

    [Fact]
    public async Task FetchAsync_HasNoCategoryTaxonomy()
    {
        var entries = await CreateClient().FetchAsync();

        Assert.Empty(Assert.Single(entries).Categories);
    }

    [Fact]
    public async Task FetchAsync_IdIsSynthesizedFromNameAndAuthor()
    {
        var entries = await CreateClient().FetchAsync();

        Assert.Equal("jimk72:Jimk Cupboard Signs:Jimk72", Assert.Single(entries).Id);
    }
}
