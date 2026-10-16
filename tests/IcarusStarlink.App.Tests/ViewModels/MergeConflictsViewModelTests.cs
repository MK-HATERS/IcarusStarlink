using System.Text.Json.Nodes;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Diffing;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>Merge &amp; Install's conflict review: grouped by colliding mods, a winner per line or group, saved by mod name.</summary>
public sealed class MergeConflictsViewModelTests
{
    private const string Items = "Items-D_ItemsStatic.json";
    private const string Recipes = "Crafting-D_ProcessorRecipes.json";

    private static FieldConflict Conflict(string file, string row, string field, JsonNode? baseValue, params (string Mod, JsonNode? Value)[] candidates) =>
        new(file, row, field,
            [.. candidates.Select(c => new ConflictCandidate(c.Mod, new FieldChange(file, row, field, baseValue, c.Value, ValueSemantic.Scalar)))],
            HasBaseValue: baseValue is not null, BaseValue: baseValue);

    private static JsonArray Links(params string[] rows) =>
        new([.. rows.Select(r => (JsonNode)new JsonObject { ["RowName"] = r, ["DataTableName"] = "D_RecipeSets" })]);

    [Fact]
    public void ConflictsAreGroupedByWhichModsCollide_AndAGroupCanBeWonAtOnce()
    {
        var vm = new MergeConflictsViewModel(
            [
                Conflict(Items, "Wood", "Weight", 10, ("Mod A", 5), ("Mod B", 20)),
                Conflict(Items, "Stone", "Weight", 10, ("Mod A", 5), ("Mod B", 20)),
                Conflict(Items, "Fiber", "Weight", 10, ("Mod A", 5), ("Mod C", 1)),
            ],
            [], null);

        var group = vm.Groups[0];
        Assert.Equal("Mod A  ↔  Mod B", group.Title);
        Assert.Equal(2, group.Count);

        group.WinAllCommand.Execute("Mod A");

        Assert.Equal(2, vm.PickedCount);
        Assert.Equal(
            new Dictionary<string, string> { ["items-d_itemsstatic.json|Wood|Weight"] = "Mod A", ["items-d_itemsstatic.json|Stone|Weight"] = "Mod A" },
            vm.BuildWinners());
    }

    [Fact]
    public void EarlierWinners_ArePreselected_AndKeptWhenTheirConflictIsntThereNow()
    {
        var earlier = new Dictionary<string, string>
        {
            ["items-d_itemsstatic.json|Wood|Weight"] = "Mod B",
            ["items-d_itemsstatic.json|Gone|Weight"] = "Mod C",
        };

        var vm = new MergeConflictsViewModel([Conflict(Items, "Wood", "Weight", 10, ("Mod A", 5), ("Mod B", 20))], [], earlier);

        var line = Assert.Single(vm.Groups.SelectMany(g => g.Conflicts));
        Assert.Equal("Mod B", line.Winner);
        Assert.Equal("Mod B (your pick)", line.WinnerText);
        Assert.Equal(earlier, vm.BuildWinners());

        line.Pick(null);
        Assert.Equal(new Dictionary<string, string> { ["items-d_itemsstatic.json|Gone|Weight"] = "Mod C" }, vm.BuildWinners());
    }

    [Fact]
    public void TheDefault_SaysWhoWins_LastInTheQueueOrEveryonesAdditions()
    {
        var number = new MergeConflictsViewModel([Conflict(Items, "Wood", "Weight", 10, ("Mod A", 5), ("Mod B", 20))], [], null)
            .Groups[0].Conflicts[0];
        Assert.Equal("Default: last in the queue wins (Mod B)", number.DefaultLabel);
        Assert.Equal(20, number.Result!.GetValue<int>());
        Assert.True(number.Columns.Single(c => c.ModName == "Mod B").IsWinner);

        // Two mods each adding an entry to a brand-new list: the merge takes both.
        var list = new FieldConflict(Recipes, "New_Recipe", "Inputs",
        [
            new ConflictCandidate("Mod A", new FieldChange(Recipes, "New_Recipe", "Inputs", null, JsonNode.Parse("""[{"Element":"X"}]"""), ValueSemantic.GenericCompound)),
            new ConflictCandidate("Mod B", new FieldChange(Recipes, "New_Recipe", "Inputs", null, JsonNode.Parse("""[{"Element":"Y"}]"""), ValueSemantic.GenericCompound)),
        ]);
        var combined = new MergeConflictsViewModel([list], [], null).Groups[0].Conflicts[0];
        Assert.Equal("Default: combine every mod's additions", combined.DefaultLabel);
        Assert.Equal("combined (default)", combined.WinnerText);
    }

    [Fact]
    public void ListsLineUpEntryByEntry_AcrossTheGameEveryModAndTheResult()
    {
        var line = new MergeConflictsViewModel(
            [Conflict(Recipes, "Stove", "RecipeSets", Links("Crafting_Bench"), ("Mod A", Links("Carpentry_Bench")), ("Mod B", Links("Crafting_Bench", "Kitchen_Bench")))],
            [], null).Groups[0].Conflicts[0];

        Assert.True(line.IsList);
        Assert.Equal(4, line.ColumnCount);
        line.PickColumnCommand.Execute(line.Columns[1]);
        Assert.Equal("Mod A", line.Winner);

        var rows = line.Matrix.Select(r => string.Join(" | ", r.Cells.Select(c => $"{c.Text}:{c.State}"))).ToList();
        Assert.Equal(
        [
            "Crafting_Bench:Same | Crafting_Bench:Missing | Crafting_Bench:Same | Crafting_Bench:Missing",
            ":Empty | Carpentry_Bench:Added | :Empty | Carpentry_Bench:Added",
            ":Empty | :Empty | Kitchen_Bench:Added | :Empty",
        ], rows);
    }

    [Fact]
    public void NewItemsSharingAName_AreListedForInformationOnly()
    {
        var vm = new MergeConflictsViewModel([], [new NewItemNameCollision(Items, "Wooden_Table", ["Mod A", "Mod B"])], null);

        Assert.True(vm.HasCollisions);
        Assert.Contains("Wooden_Table", vm.Collisions[0]);
        Assert.Contains("Mod A, Mod B", vm.Collisions[0]);
        Assert.Empty(vm.BuildWinners());
    }
}
