using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Safety;

namespace IcarusStarlink.PakIO.Tests.Safety;

/// <summary>"Was this pak packed right?" — each case shaped on a real Nexus pak.</summary>
public sealed class PakContentLintsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "Lints_" + Guid.NewGuid().ToString("N"));
    private string Pak => Path.Combine(_root, "pak");
    private string Data => Path.Combine(_root, "Data");

    public PakContentLintsTests()
    {
        Write(Data, "Traits/D_Itemable.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Item_Fiber","MaxStack":200},{"Name":"Item_Bandage_Poultice","MaxStack":10}]}""");
        Write(Data, "Traits/D_Deployable.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Campfire","Health":100}]}""");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private MergeReport Inspect(params string[] outside)
    {
        var report = new MergeReport();
        PakContentLints.Inspect(Pak, Data, "Mod_P", outside, report);
        return report;
    }

    /// <summary>BetterSupplies: the author's .git folder, Notepad++ session and build script packed in. All-in-One: a blueprint dumped to .json outside data/.</summary>
    [Fact]
    public void JunkFiles_AreReported_ButARootExmodIsNot()
    {
        Write(Pak, ".git/config", "x");
        Write(Pak, "BetterSuppliesSession.np++", "x");
        Write(Pak, "BuildMod.cmd", "x");
        Write(Pak, "BP/Player/BP_IcarusPlayerCharacterSurvival.json", "[]");
        Write(Pak, "BetterSupplies.EXMOD", "{}");
        Write(Pak, "BP/Thing.uasset", "x");

        var warning = Assert.Single(Inspect().Warnings);

        Assert.Contains("4 file(s)", warning);
        Assert.Contains(".git/config", warning);
        Assert.DoesNotContain(".EXMOD", warning);
    }

    /// <summary>Ultimate Envirosuit / BiggerStacks: a full table copy built before the game added rows.</summary>
    [Fact]
    public void FullTableMissingTodaysRows_IsFlaggedAsOlderThanTheGame()
    {
        Write(Pak, "data/Traits/D_Itemable.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Item_Fiber","MaxStack":1000}]}""");

        var warning = Assert.Single(Inspect().Warnings);

        Assert.Contains("older game version", warning);
        Assert.Contains("D_Itemable lacks 1 row(s)", warning);
        Assert.Contains("Item_Bandage_Poultice", warning);
    }

    /// <summary>The All-in-One shipped D_Deployable without a single change.</summary>
    [Fact]
    public void TableShippedUnchanged_IsReported()
    {
        Write(Pak, "data/Traits/D_Deployable.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Campfire","Health":100}]}""");

        Assert.Contains("doesn't change at all (D_Deployable)", Assert.Single(Inspect().Warnings));
    }

    [Fact]
    public void FilesOutsideContent_AreReported()
    {
        Write(Pak, "Engine/Config/Thing.ini", "x");

        Assert.Contains(Inspect("Engine/Config/Thing.ini").Warnings, w => w.Contains("outside the game's Content folder"));
    }

    [Fact]
    public void ACleanPak_HasNothingToSay()
    {
        Write(Pak, "data/Traits/D_Itemable.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Item_Fiber","MaxStack":1000},{"Name":"Item_Bandage_Poultice","MaxStack":10}]}""");
        Write(Pak, "BP/Thing.uasset", "x");

        var report = Inspect();

        Assert.Empty(report.Warnings);
        Assert.Empty(report.Notes);
    }

    [Theory]
    [InlineData(".git/objects/ab/cdef", true)]
    [InlineData(".gitignore", true)]
    [InlineData("BetterSuppliesSession.np++", true)]
    [InlineData("BuildMod.cmd", true)]
    [InlineData("Readme.txt", false)]
    [InlineData("banner.png", false)]
    [InlineData("BP/Thing.uasset", false)]
    public void BuildLeftovers_AreRecognised(string path, bool expected) =>
        Assert.Equal(expected, PakContentLints.IsBuildLeftover(path));
}
