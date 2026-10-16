using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests.Exmod;

/// <summary>Sorting a mod's differences from the game into old copies, mixed lists and the author's own changes.</summary>
public sealed class ModHealthCheckerTests : IDisposable
{
    private const string Items = "Items-D_ItemsStatic.json";
    private const string Recipes = "Crafting-D_ProcessorRecipes.json";
    private readonly string _data = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    public ModHealthCheckerTests()
    {
        var rows = new JsonArray
        {
            new JsonObject { ["Name"] = "Wood_Floor", ["Manual_Tags"] = Tags("Building.Wood", "Audio.Shelter", "FieldGuide.Building.Floors"), ["Weight"] = 100 },
            new JsonObject { ["Name"] = "Cheese", ["Manual_Tags"] = Tags("Item.Food.Cooked"), ["Weight"] = 50 },
            new JsonObject { ["Name"] = "Bread", ["Weight"] = 20, ["Decayable"] = new JsonObject { ["RowName"] = "Decay_Food" } },
        };
        for (var i = 0; i < 30; i++)
        {
            rows.Add(new JsonObject { ["Name"] = $"Copied_{i}", ["Manual_Tags"] = Tags("A", "B"), ["Weight"] = 10 });
        }

        rows.Add(new JsonObject { ["Name"] = "Canteen", ["Consumable"] = new JsonObject { ["RowName"] = "BasicWater" } });
        Write("Items/D_ItemsStatic.json", rows, new JsonObject { ["Consumable"] = new JsonObject { ["RowName"] = "None", ["DataTableName"] = "D_Consumable" } });
        Write("Traits/D_Consumable.json", new JsonArray { new JsonObject { ["Name"] = "BasicWater" }, new JsonObject { ["Name"] = "Juice" } });
        Write("Crafting/D_ProcessorRecipes.json", new JsonArray
        {
            new JsonObject
            {
                ["Name"] = "Bread_Recipe",
                ["RequiredMillijoules"] = 2000,
                ["Inputs"] = new JsonArray { Input("Flour", 2) },
                ["RecipeSets"] = new JsonArray { Link("Kitchen_Bench") },
            },
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    [Fact]
    public void InAPrebuiltPak_ATagListMissingWhatTheGameAddedSince_IsASureOldCopy_AndTheFixLeavesTheGamesValue()
    {
        var package = Mod((Items, "Wood_Floor", "Manual_Tags", Tags("Building.Wood", "Audio.Shelter")), (Items, "Wood_Floor", "Weight", 5));

        var report = ModHealthChecker.Check(package, _data, fromPrebuiltPak: true);

        var issue = Assert.Single(report.Issues);
        Assert.Equal(ModIssueKind.OldCopy, issue.Kind);
        Assert.True(issue.IsSure);
        Assert.Contains("added 1 entry", issue.Why);
        Assert.Equal(1, report.AuthorChangeCount);

        Assert.Equal(1, ModHealthChecker.ApplySure(package, report));
        var item = package.Rows.Single(r => r.CurrentFile == Items).FileItems.Single();
        Assert.False(item.Fields.ContainsKey("Manual_Tags"));
        Assert.Equal(5, item.Fields["Weight"]!.GetValue<int>());
    }

    [Fact]
    public void InAPrebuiltPak_DeletingAFieldTheGameHasNow_IsAnOldCopy_AndAValueEqualToTheGames_DoesNothing()
    {
        var package = Mod((Recipes, "Bread_Recipe", "RequiredMillijoules", null), (Items, "Bread", "Weight", 20));

        var report = ModHealthChecker.Check(package, _data, fromPrebuiltPak: true);

        Assert.Contains(report.Issues, i => i.Kind == ModIssueKind.OldCopy && i.FieldName == "RequiredMillijoules");
        Assert.Contains(report.Issues, i => i.Kind == ModIssueKind.SameAsGame && i.FieldName == "Weight");
        Assert.All(report.Issues, i => Assert.True(i.IsSure));

        ModHealthChecker.ApplySure(package, report);
        Assert.DoesNotContain(package.Rows, r => r.CurrentFile is Items or Recipes);
    }

    /// <summary>
    /// Ferani's Effortless Lights: torches burn nothing because the mod empties their list of
    /// burnable items, and deleting a field is how a hand-made mod removes something. Both are the
    /// author's choices in a mod that lists only what it changed, never "old copies".
    /// </summary>
    [Fact]
    public void InAHandMadeMod_AnEmptiedListOrADeletedField_IsTheAuthorsChange()
    {
        var package = Mod(
            (Recipes, "Bread_Recipe", "RecipeSets", new JsonArray()),
            (Recipes, "Bread_Recipe", "RequiredMillijoules", null));

        var report = ModHealthChecker.Check(package, _data);

        Assert.False(report.HasIssues);
        Assert.Equal(2, report.AuthorChangeCount);
        Assert.Equal(2, ModHealthChecker.Check(package, _data, fromPrebuiltPak: true).Count(ModIssueKind.OldCopy));
    }

    /// <summary>The game ignores case in row names: "wood_floor" is the game's Wood_Floor, never a missing row to rename.</summary>
    [Fact]
    public void ARowSpelledInOtherCase_IsTheGamesRow_NotARename()
    {
        var package = Mod((Items, "wood_floor", "Weight", 7));

        var report = ModHealthChecker.Check(package, _data);

        Assert.DoesNotContain(report.Issues, i => i.Kind == ModIssueKind.MissingRow);
        Assert.Equal(1, report.AuthorChangeCount);
    }

    /// <summary>
    /// laanp's old copies linked the Canteen's drink effect to a "Canteen" consumable the game no
    /// longer has, so it couldn't be drunk from. A link to a row that doesn't exist is a sure fix in
    /// any mod; a link to a real other row, or to a row the mod adds itself, is the author's choice.
    /// </summary>
    [Fact]
    public void ALinkToARowTheGameDoesntHave_IsASureFix_ButARealOrOwnRowIsTheAuthors()
    {
        var broken = Mod((Items, "Canteen", "Consumable", new JsonObject { ["RowName"] = "Canteen" }));
        var real = Mod((Items, "Canteen", "Consumable", new JsonObject { ["RowName"] = "Juice" }));
        var own = Mod((Items, "Canteen", "Consumable", new JsonObject { ["RowName"] = "Canteen_Drink" }), ("Traits-D_Consumable.json", "Canteen_Drink", "Modifier", 5));

        var issue = Assert.Single(ModHealthChecker.Check(broken, _data).Issues);
        Assert.Equal(ModIssueKind.OldCopy, issue.Kind);
        Assert.True(issue.IsSure);
        Assert.Contains("'Canteen' in D_Consumable", issue.Why);
        Assert.Contains("'BasicWater'", issue.Why);

        Assert.False(ModHealthChecker.Check(real, _data).HasIssues);
        Assert.DoesNotContain(ModHealthChecker.Check(own, _data).Issues, i => i.ItemName == "Canteen");
    }

    [Fact]
    public void AChangedCountInARecipe_AndASwappedIngredient_AreTheAuthorsChanges()
    {
        var package = Mod(
            (Recipes, "Bread_Recipe", "Inputs", new JsonArray { Input("Flour", 1) }),
            (Items, "Bread", "Decayable", new JsonObject { ["RowName"] = "Decay_Long" }));
        var swapped = Mod((Recipes, "Bread_Recipe", "Inputs", new JsonArray { Input("Wheat", 2) }));

        Assert.False(ModHealthChecker.Check(package, _data).HasIssues);
        Assert.Equal(2, ModHealthChecker.Check(package, _data).AuthorChangeCount);
        Assert.False(ModHealthChecker.Check(swapped, _data).HasIssues);
    }

    [Fact]
    public void AMixedTagList_MergesTheGamesCurrentTagsWithTheModsAdditions()
    {
        var package = Mod((Items, "Cheese", "Manual_Tags", Tags("Item.Food", "Item.Food.Dairy")));

        var issue = Assert.Single(ModHealthChecker.Check(package, _data).Issues);

        Assert.Equal(ModIssueKind.MixedList, issue.Kind);
        Assert.False(issue.IsSure);
        Assert.Equal(ModIssueFix.Merge, issue.DefaultFix);
        Assert.Equal(Tags("Item.Food.Cooked", "Item.Food", "Item.Food.Dairy").ToJsonString(), issue.MergedValue!.ToJsonString());

        ModHealthChecker.Apply(package, [(issue, ModIssueFix.Merge)]);
        Assert.Equal(issue.MergedValue.ToJsonString(), package.Rows.Single(r => r.CurrentFile == Items).FileItems.Single().Fields["Manual_Tags"]!.ToJsonString());
    }

    [Fact]
    public void ATableCopiedWholesale_MarksItsOtherChangesOnOldRowsAsProbablyOld()
    {
        var changes = Enumerable.Range(0, 30).Select(i => (Items, $"Copied_{i}", "Manual_Tags", (JsonNode?)Tags("A"))).ToList();
        changes.Add((Items, "Copied_0", "Weight", 3));
        changes.Add((Items, "Copied_1", "Manual_Tags_Extra", 1));
        var package = Mod([.. changes]);

        var report = ModHealthChecker.Check(package, _data);

        Assert.Equal(30, report.Count(ModIssueKind.OldCopy));
        var likelyOld = Assert.Single(report.Issues, i => i.Kind == ModIssueKind.LikelyOld);
        Assert.Equal("Weight", likelyOld.FieldName);
        Assert.Equal(ModIssueFix.UseGame, likelyOld.DefaultFix);
        Assert.False(likelyOld.IsSure);
        Assert.Equal(1, report.AuthorChangeCount);
    }

    [Fact]
    public void TheGamesOwnChangeHistory_MarksAValueTheGameHadBefore_AsOld()
    {
        var package = Mod((Items, "Bread", "Weight", 25));
        var history = new GameValueHistory([new FieldChange(Items, "Bread", "Weight", JsonValue.Create(25), JsonValue.Create(20), ValueSemantic.Scalar)]);

        Assert.False(ModHealthChecker.Check(package, _data).HasIssues);
        var issue = Assert.Single(ModHealthChecker.Check(package, _data, gameHistory: history).Issues);
        Assert.Equal(ModIssueKind.OldCopy, issue.Kind);
        Assert.True(issue.IsSure);
    }

    [Fact]
    public void TheModsOwnNewItems_AreNeverFlaggedAsMissingRows()
    {
        var package = Mod(
            (Items, "My_Bag", "Weight", 5),
            (Recipes, "My_Bag", "Inputs", new JsonArray { Input("Leather", 4) }),
            (Items, "Wood_Floo", "Weight", 7));

        var report = ModHealthChecker.Check(package, _data);

        Assert.DoesNotContain(report.Issues, i => i.ItemName == "My_Bag");
        var renamed = Assert.Single(report.Issues, i => i.Kind == ModIssueKind.MissingRow);
        Assert.Equal("Wood_Floo", renamed.ItemName);
        Assert.Equal("Wood_Floor", renamed.SuggestedRow);
    }

    [Fact]
    public void AKeptValue_StaysHidden_UntilTheGameChangesItsSide()
    {
        var package = Mod((Items, "Cheese", "Manual_Tags", Tags("Item.Food", "Item.Food.Dairy")));
        var decisions = new ModFixDecisions();
        decisions.Remember(ModHealthChecker.Check(package, _data).Issues.Single(), ModIssueFix.KeepMod);

        var report = ModHealthChecker.Check(package, _data, decisions: decisions);
        Assert.False(report.HasIssues);
        Assert.Equal(1, report.AuthorChangeCount);

        // A game update adds a tag: the question is new, so it's asked again.
        AddGameTagToCheese();
        Assert.Single(ModHealthChecker.Check(package, _data, decisions: decisions).Issues);
    }

    [Fact]
    public void AnyOtherChoice_IsMadeByItselfNextTime_EvenAfterTheGameChangesItsSide()
    {
        var package = Mod((Items, "Cheese", "Manual_Tags", Tags("Item.Food", "Item.Food.Dairy")));
        var decisions = new ModFixDecisions();
        decisions.Remember(ModHealthChecker.Check(package, _data).Issues.Single(), ModIssueFix.UseGame);

        AddGameTagToCheese();
        var issue = Assert.Single(ModHealthChecker.Check(package, _data, decisions: decisions).Issues);

        Assert.True(issue.IsSure);
        Assert.Equal(ModIssueFix.UseGame, issue.DefaultFix);
        Assert.EndsWith("Done as you chose before.", issue.Why);
    }

    [Fact]
    public void Apply_FixesEveryEntryOfARowListedTwice_AndKeepsAnEmptyItemItDidntTouch()
    {
        var package = Mod((Items, "Wood_Floor", "Manual_Tags", Tags("Building.Wood", "Audio.Shelter")));
        var row = package.Rows.Single(r => r.CurrentFile == Items);
        row.FileItems.Add(new ExmodFileItem { Name = "Wood_Floor", Fields = new() { ["Manual_Tags"] = Tags("Building.Wood") } });
        row.FileItems.Add(new ExmodFileItem { Name = "Being_Built" });

        ModHealthChecker.ApplySure(package, ModHealthChecker.Check(package, _data, fromPrebuiltPak: true));

        Assert.Equal(["Being_Built"], row.FileItems.Select(i => i.Name));
    }

    private void AddGameTagToCheese()
    {
        var path = Path.Combine(_data, "Items", "D_ItemsStatic.json");
        var table = JsonNode.Parse(File.ReadAllText(path))!;
        var cheese = table["Rows"]!.AsArray().Single(r => r!["Name"]!.ToString() == "Cheese")!;
        cheese["Manual_Tags"]!["GameplayTags"]!.AsArray().Add(new JsonObject { ["TagName"] = "FieldGuide.Food" });
        File.WriteAllText(path, table.ToJsonString());
    }

    private ExmodPackage Mod(params (string File, string Row, string Field, JsonNode? Value)[] changes)
    {
        var package = new ExmodPackage { Name = "Test", Author = "Me", Version = "1", Description = "", FileName = "Test" };
        foreach (var group in changes.GroupBy(c => c.File))
        {
            var row = new ExmodFileRow { CurrentFile = group.Key };
            foreach (var item in group.GroupBy(c => c.Row))
            {
                row.FileItems.Add(new ExmodFileItem { Name = item.Key, Fields = item.ToDictionary(c => c.Field, c => c.Value) });
            }

            package.Rows.Add(row);
        }

        package.Rows.Add(new ExmodFileRow { CurrentFile = "EndOfMod" });
        return package;
    }

    private void Write(string relative, JsonArray rows, JsonObject? defaults = null)
    {
        var path = Path.Combine(_data, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, new JsonObject { ["RowStruct"] = "S", ["Defaults"] = defaults ?? new JsonObject(), ["Rows"] = rows }.ToJsonString());
    }

    private static JsonObject Tags(params string[] tags) =>
        new() { ["GameplayTags"] = new JsonArray(tags.Select(t => (JsonNode)new JsonObject { ["TagName"] = t }).ToArray()) };

    private static JsonObject Input(string item, int count) =>
        new() { ["Element"] = new JsonObject { ["RowName"] = item, ["DataTableName"] = "D_ItemsStatic" }, ["Count"] = count };

    private static JsonObject Link(string row) => new() { ["RowName"] = row, ["DataTableName"] = "D_RecipeSets" };
}
