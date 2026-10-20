using System.Text.Json.Nodes;
using IcarusStarlink.PakIO.Exmod;
using static IcarusStarlink.PakIO.Exmod.TalentTreeChecker;

namespace IcarusStarlink.PakIO.Tests.Exmod;

/// <summary>
/// The talent and workshop trees after a merge, on a small game tree shaped like the real one
/// (Workshop_Axes: a printed axe, then a Shengong axe that needs it, 250×250 workshop nodes and a
/// reroute joint). The real game's own trees have none of these findings; this one plants each
/// of them among game-only nodes (Game_Stacked_A/B on top of each other, waiting for each other,
/// one needing a node in another tree; Game_Lost in a tree that doesn't exist), which must never
/// be reported unless a mod changes those nodes.
/// </summary>
public sealed class TalentTreeCheckerTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "Trees_" + Guid.NewGuid().ToString("N"));

    public TalentTreeCheckerTests()
    {
        void W(string relative, string json)
        {
            var path = Path.Combine(_data, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
        }

        W("Talents/D_Talents.json", """
            {"RowStruct":"S","Defaults":{"TalentType":"Talent","TalentTree":{"RowName":"None","DataTableName":"D_TalentTrees"},
              "Position":{"X":0,"Y":0},"Size":{"X":64,"Y":64},"RequiredTalents":[]},
             "Rows":[
              {"Name":"Workshop_Axe_Printed","TalentTree":{"RowName":"Workshop_Axes"},"Position":{"X":500,"Y":1150},"Size":{"X":250,"Y":250}},
              {"Name":"Workshop_Axe_Shengong","TalentTree":{"RowName":"Workshop_Axes"},"Position":{"X":1000,"Y":1150},"Size":{"X":250,"Y":250},
               "RequiredTalents":[{"RowName":"Workshop_Axe_Printed","DataTableName":"D_Talents"}]},
              {"Name":"Workshop_Axe_Reroute","TalentType":"Reroute","TalentTree":{"RowName":"Workshop_Axes"},"Position":{"X":1000,"Y":1150}},
              {"Name":"Workshop_Pick_Printed","TalentTree":{"RowName":"Workshop_Pickaxes"},"Position":{"X":500,"Y":450},"Size":{"X":250,"Y":250}},
              {"Name":"Game_Stacked_A","TalentTree":{"RowName":"Workshop_Pickaxes"},"Position":{"X":3000,"Y":3000},"Size":{"X":250,"Y":250},
               "RequiredTalents":[{"RowName":"Game_Stacked_B","DataTableName":"D_Talents"}]},
              {"Name":"Game_Stacked_B","TalentTree":{"RowName":"Workshop_Pickaxes"},"Position":{"X":3000,"Y":3000},"Size":{"X":250,"Y":250},
               "RequiredTalents":[{"RowName":"Game_Stacked_A","DataTableName":"D_Talents"},{"RowName":"Workshop_Axe_Printed","DataTableName":"D_Talents"}]},
              {"Name":"Game_Lost","TalentTree":{"RowName":"Gone_Tree"},"Position":{"X":0,"Y":0}}
            ]}
            """);
        W("Talents/D_TalentTrees.json", """
            {"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Workshop_Axes"},{"Name":"Workshop_Pickaxes"}]}
            """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private GameTableIndex Game => new(_data);

    private static (string, ExmodPackage) Mod(string name, params ExmodFileItem[] nodes) =>
        (name, new ExmodPackage
        {
            Name = name, Author = "Me", Version = "1", Description = "", FileName = name + ".EXMOD",
            Rows = [new ExmodFileRow { CurrentFile = TalentsFile, FileItems = [.. nodes] }],
        });

    private static ExmodFileItem Node(string name, string tree, int x, int y, params string[] requires)
    {
        var fields = new Dictionary<string, JsonNode?>
        {
            ["TalentTree"] = new JsonObject { ["RowName"] = tree },
            ["Position"] = new JsonObject { ["X"] = x, ["Y"] = y },
            ["Size"] = new JsonObject { ["X"] = 250, ["Y"] = 250 },
        };
        if (requires.Length > 0)
        {
            fields["RequiredTalents"] = new JsonArray([.. requires.Select(r => (JsonNode)new JsonObject { ["RowName"] = r, ["DataTableName"] = "D_Talents" })]);
        }

        return new ExmodFileItem { Name = name, Fields = fields };
    }

    [Fact]
    public void TheGamesOwnProblems_AreNeverReported()
    {
        Assert.Empty(Check([], Game));
    }

    [Fact]
    public void AModsNodeInAFreeSpot_IsFine()
    {
        Assert.Empty(Check([Mod("Axes+", Node("Workshop_Axe_Gold", "Workshop_Axes", 1500, 1150, "Workshop_Axe_Shengong"))], Game));
    }

    [Fact]
    public void AModsNodeOnTopOfAGameNode()
    {
        var found = Assert.Single(Check([Mod("Axes+", Node("Workshop_Axe_Gold", "Workshop_Axes", 1050, 1180))], Game));

        Assert.Equal(FindingKind.Overlap, found.Kind);
        Assert.Equal(["Axes+"], found.Mods);
        Assert.Contains("Workshop_Axe_Shengong (the game)", found.Reason);
        Assert.Contains("Workshop_Axe_Gold (Axes+)", found.Reason);
    }

    /// <summary>Two mods each adding a node to the same free spot: fine alone, they hide each other together.</summary>
    [Fact]
    public void TwoModsUsingTheSameFreeSpot()
    {
        var found = Assert.Single(Check([
            Mod("Gold Axe", Node("Workshop_Axe_Gold", "Workshop_Axes", 1500, 1150)),
            Mod("Bone Axe", Node("Workshop_Axe_Bone", "Workshop_Axes", 1500, 1150)),
        ], Game));

        Assert.Equal(FindingKind.Overlap, found.Kind);
        Assert.Equal(["Gold Axe", "Bone Axe"], found.Mods);
    }

    [Fact]
    public void SameSpotInAnotherTree_IsFine()
    {
        Assert.Empty(Check([Mod("Picks+", Node("Workshop_Pick_Gold", "Workshop_Pickaxes", 1000, 1150))], Game));
    }

    /// <summary>A later mod moving a node out of the way clears it: the merge is judged, not each mod.</summary>
    [Fact]
    public void ALaterModMovesItAway()
    {
        var mover = new ExmodFileItem { Name = "Workshop_Axe_Gold", Fields = new() { ["Position"] = new JsonObject { ["X"] = 2000, ["Y"] = 1150 } } };

        Assert.Empty(Check([Mod("Axes+", Node("Workshop_Axe_Gold", "Workshop_Axes", 1000, 1150)), Mod("Axes+ fix", mover)], Game));
    }

    [Fact]
    public void ARerouteJointUnderANode_IsntAnOverlap()
    {
        var moved = new ExmodFileItem { Name = "Workshop_Axe_Reroute", Fields = new() { ["Position"] = new JsonObject { ["X"] = 500, ["Y"] = 1150 } } };

        Assert.Empty(Check([Mod("Lines", moved)], Game));
    }

    /// <summary>A mod changing only what a game node gives isn't blamed for where the game put it, or its tree.</summary>
    [Fact]
    public void ChangingOnlyAGameNodesReward_SaysNothingAboutItsPlace()
    {
        var stacked = new ExmodFileItem { Name = "Game_Stacked_A", Fields = new() { ["Rewards"] = new JsonArray() } };
        var lost = new ExmodFileItem { Name = "Game_Lost", Fields = new() { ["Rewards"] = new JsonArray() } };

        Assert.Empty(Check([Mod("Cheaper", stacked, lost)], Game));
    }

    /// <summary>The control for the test above: once the mod sets where the node sits, the overlap is its own.</summary>
    [Fact]
    public void SettingAGameNodesPosition_MakesItsOverlapTheMods()
    {
        var placed = new ExmodFileItem { Name = "Game_Stacked_A", Fields = new() { ["Position"] = new JsonObject { ["X"] = 3010, ["Y"] = 3000 } } };

        var found = Assert.Single(Check([Mod("Mover", placed)], Game));

        Assert.Equal(FindingKind.Overlap, found.Kind);
        Assert.Equal(["Mover"], found.Mods);
    }

    [Fact]
    public void NodesWaitingForEachOther()
    {
        var printed = new ExmodFileItem
        {
            Name = "Workshop_Axe_Printed",
            Fields = new() { ["RequiredTalents"] = new JsonArray(new JsonObject { ["RowName"] = "Workshop_Axe_Shengong", ["DataTableName"] = "D_Talents" }) },
        };

        var found = Assert.Single(Check([Mod("Loop", printed)], Game));

        Assert.Equal(FindingKind.Circle, found.Kind);
        Assert.Contains("Workshop_Axe_Printed", found.Reason);
        Assert.Contains("Workshop_Axe_Shengong", found.Reason);
        Assert.Contains("can ever be unlocked", found.Reason);
    }

    [Fact]
    public void NeedingANodeFromAnotherTree()
    {
        var found = Assert.Single(Check([Mod("Picks+", Node("Workshop_Pick_Gold", "Workshop_Pickaxes", 1000, 450, "Workshop_Axe_Printed"))], Game));

        Assert.Equal(FindingKind.OtherTree, found.Kind);
        Assert.Contains("Workshop_Axes", found.Reason);
    }

    [Fact]
    public void ATreeNobodyHas()
    {
        var found = Assert.Single(Check([Mod("Beasts", Node("Workshop_Buffalo", "Workshop_Beasts", 1900, 750))], Game));

        Assert.Equal(FindingKind.MissingTree, found.Kind);
        Assert.Contains("Workshop_Beasts", found.Reason);
    }

    /// <summary>Ferani's Buyable Beasts adds its own BEASTS tree next to the nodes in it: fine.</summary>
    [Fact]
    public void ATreeTheModAddsItself()
    {
        var (name, package) = Mod("Beasts", Node("Workshop_Buffalo", "Workshop_Beasts", 1900, 750));
        package.Rows.Add(new ExmodFileRow { CurrentFile = TreesFile, FileItems = [new ExmodFileItem { Name = "Workshop_Beasts" }] });

        Assert.Empty(Check([(name, package)], Game));
    }
}
