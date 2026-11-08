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
        foreach (var ingredient in new[] { "Flour", "Wheat", "Leather" })
        {
            rows.Add(new JsonObject { ["Name"] = ingredient, ["Weight"] = 1 });
        }

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
        Assert.Equal(1, report.SureProblemCount);

        // Tidying is never automatic: the value the game already has stays until you take it out.
        ModHealthChecker.ApplySure(package, report);
        Assert.DoesNotContain(package.Rows, r => r.CurrentFile is Recipes);
        Assert.Contains(package.Rows, r => r.CurrentFile is Items);
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
    }

    /// <summary>
    /// laanp-WorkshopFree makes workshop items free by emptying their cost lists, in its prebuilt pak
    /// too. An old game version seldom had an empty list where today's game has entries, so an
    /// emptied list in a pak is the author's change (it used to be dropped as an old copy, so the
    /// pak made nothing free). A deleted field and a shortened list stay old copies there.
    /// </summary>
    [Fact]
    public void InAPrebuiltPak_AnEmptiedList_IsTheAuthorsChange_SinceFreeMakingModsEmptyCostLists_ButADeletedFieldIsAnOldCopy()
    {
        var package = Mod(
            (Recipes, "Bread_Recipe", "RecipeSets", new JsonArray()),
            (Recipes, "Bread_Recipe", "RequiredMillijoules", null),
            (Items, "Cheese", "Manual_Tags", Tags()));

        var report = ModHealthChecker.Check(package, _data, fromPrebuiltPak: true);

        var old = Assert.Single(report.Issues, i => i.Kind == ModIssueKind.OldCopy);
        Assert.Equal("RequiredMillijoules", old.FieldName);
        Assert.Equal(2, report.AuthorChangeCount);

        ModHealthChecker.ApplySure(package, report);
        var recipe = package.Rows.Single(r => r.CurrentFile == Recipes).FileItems.Single();
        Assert.Equal("[]", recipe.Fields["RecipeSets"]!.ToJsonString());
        Assert.False(recipe.Fields.ContainsKey("RequiredMillijoules"));
    }

    /// <summary>
    /// laanp-WorkshopFree empties ResearchCost and ReplicationCost on 339 workshop rows. In a
    /// hand-made mod that's the author's change on every row, not 339 signs of a table copied from
    /// an older game: it used to be judged a wholesale copy, and Fix sure problems took every
    /// emptied list out again (an emptied list inside an object and a deleted field the same).
    /// </summary>
    [Fact]
    public void InAHandMadeMod_EmptyingOneListOnManyRows_IsTheAuthorsChange_NotAWholesaleCopy()
    {
        var recipes = new JsonArray();
        for (var i = 0; i < 30; i++)
        {
            recipes.Add(new JsonObject { ["Name"] = $"R_{i}", ["RecipeSets"] = new JsonArray { Link("Kitchen_Bench") } });
        }

        Write("Crafting/D_ProcessorRecipes.json", recipes);
        var emptied = Mod([.. Enumerable.Range(0, 30).Select(i => (Recipes, $"R_{i}", "RecipeSets", (JsonNode?)new JsonArray()))]);
        var emptiedInObject = Mod([.. Enumerable.Range(0, 30).Select(i => (Items, $"Copied_{i}", "Manual_Tags", (JsonNode?)Tags()))]);
        var deleted = Mod([.. Enumerable.Range(0, 30).Select(i => (Items, $"Copied_{i}", "Weight", (JsonNode?)null))]);

        foreach (var package in new[] { emptied, emptiedInObject, deleted })
        {
            var report = ModHealthChecker.Check(package, _data);

            Assert.Equal(0, report.Count(ModIssueKind.OldCopy));
            Assert.Equal(0, report.SureProblemCount);
            Assert.Equal(30, report.AuthorChangeCount);
            Assert.Equal(0, ModHealthChecker.ApplySure(package, report));
        }
    }

    /// <summary>
    /// A hand-made mod's table that still looks copied from an older game (most rows carry a
    /// shortened tag list): its shape-only findings are listed with "Use the game's" chosen, but
    /// never made by themselves, since taking one tag off 30 rows looks just like an old copy.
    /// They used to be made by Fix sure problems. A prebuilt pak's stay automatic.
    /// </summary>
    [Fact]
    public void InAHandMadeMod_ACopiedTablesShapeOnlyFindings_AreListedWithTheGamesChosen_ButNeverMadeByThemselves()
    {
        var changes = Enumerable.Range(0, 30).Select(i => (Items, $"Copied_{i}", "Manual_Tags", (JsonNode?)Tags("A"))).ToList();
        changes.Add((Items, "Copied_0", "Weight", null));

        var report = ModHealthChecker.Check(Mod([.. changes]), _data);

        var old = report.Issues.Where(i => i.Kind == ModIssueKind.OldCopy).ToList();
        Assert.Equal(31, old.Count);
        Assert.All(old, i =>
        {
            Assert.False(i.IsSure);
            Assert.Equal(ModIssueFix.UseGame, i.DefaultFix);
        });
        Assert.Equal(0, ModHealthChecker.ApplySure(Mod([.. changes]), report));

        var prebuilt = Mod([.. changes]);
        Assert.Equal(31, ModHealthChecker.ApplySure(prebuilt, ModHealthChecker.Check(prebuilt, _data, fromPrebuiltPak: true)));
    }

    /// <summary>
    /// A "revert the nerf" mod puts 25+ rows of one table back to the game's old values, which is
    /// what a copied table looks like too. Its old values are listed and kept unless you choose,
    /// as in any other hand-made table; they used to be put back to the game's by Fix sure problems.
    /// </summary>
    [Fact]
    public void InAHandMadeMod_ACopiedTablesValuesTheGameHadBefore_AreOnlyListed_AndKept()
    {
        var changes = Enumerable.Range(0, 30).Select(i => (Items, $"Copied_{i}", "Manual_Tags", (JsonNode?)Tags("A"))).ToList();
        changes.Add((Items, "Copied_0", "Weight", 3));
        var history = new GameValueHistory([new FieldChange(Items, "Copied_0", "Weight", JsonValue.Create(3), JsonValue.Create(10), ValueSemantic.Scalar)]);

        var report = ModHealthChecker.Check(Mod([.. changes]), _data, gameHistory: history);

        var reverted = Assert.Single(report.Issues, i => i.FieldName == "Weight");
        Assert.Equal(ModIssueKind.OldCopy, reverted.Kind);
        Assert.False(reverted.IsSure);
        Assert.Equal(ModIssueFix.KeepMod, reverted.DefaultFix);
    }

    private const string Armour = "Traits-D_Armour.json";
    private const string StatA = "(Value=\"A_+\")";
    private const string StatB = "(Value=\"B_+\")";

    private void WriteArmour() =>
        Write("Traits/D_Armour.json", new JsonArray { new JsonObject { ["Name"] = "Armour_X", ["ArmourStats"] = new JsonObject { [StatA] = 5, [StatB] = 10 } } });

    /// <summary>
    /// The merge replaces a whole object field, so an armour map without one of the game's stats
    /// takes that stat away in game. A hand-made mod leaving keys out is the author's change; it
    /// used to be called "same as the game", and its "Take it out" brought the stat back.
    /// </summary>
    [Fact]
    public void InAHandMadeMod_AnObjectMissingSomeOfTheGamesKeys_IsTheAuthorsChange_NotSameAsTheGame()
    {
        WriteArmour();
        var fewer = Mod((Armour, "Armour_X", "ArmourStats", new JsonObject { [StatA] = 5 }));
        var none = Mod((Armour, "Armour_X", "ArmourStats", new JsonObject()));

        foreach (var package in new[] { fewer, none })
        {
            var report = ModHealthChecker.Check(package, _data);

            Assert.DoesNotContain(report.Issues, i => i.FieldName == "ArmourStats");
            Assert.Equal(1, report.AuthorChangeCount);
        }
    }

    /// <summary>The player's starting stats merge one stat at a time: a stat the mod leaves out keeps the game's value, so listing fewer stats changes nothing.</summary>
    [Fact]
    public void StartingStatsListingFewerStats_AreStillSameAsTheGame_SinceTheyMergeStatByStat()
    {
        Write("Stats/D_CharacterStartingStats.json", new JsonArray { new JsonObject { ["Name"] = "Base_Stats", ["StatsGranted"] = new JsonObject { [StatA] = 1, [StatB] = 2 } } });
        var package = Mod(("Stats-D_CharacterStartingStats.json", "Base_Stats", "StatsGranted", new JsonObject { [StatA] = 1 }));

        var issue = Assert.Single(ModHealthChecker.Check(package, _data).Issues);

        Assert.Equal(ModIssueKind.SameAsGame, issue.Kind);
    }

    /// <summary>A prebuilt pak ships whole rows: an object missing a key the game has is the old game version's, and the fix leaves the game's value.</summary>
    [Fact]
    public void InAPrebuiltPak_AnObjectMissingAKeyTheGameHas_IsASureOldCopy()
    {
        WriteArmour();
        var package = Mod((Armour, "Armour_X", "ArmourStats", new JsonObject { [StatA] = 5 }));

        var report = ModHealthChecker.Check(package, _data, fromPrebuiltPak: true);

        var issue = Assert.Single(report.Issues);
        Assert.Equal(ModIssueKind.OldCopy, issue.Kind);
        Assert.True(issue.IsSure);
        Assert.Contains("added 1 entry", issue.Why);
        Assert.Equal(1, ModHealthChecker.ApplySure(package, report));
        Assert.DoesNotContain(package.Rows, r => r.CurrentFile == Armour);
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
    /// longer has, so it couldn't be drunk from. A link to a row nothing has is listed; without the
    /// game's history proving the game had that row (the proven case is in GameRowHistoryTests), it
    /// may be one a mod outside the Library adds, so it's kept unless you choose otherwise. A link
    /// to a real other row, or to a row the mod adds itself, is the author's choice.
    /// </summary>
    [Fact]
    public void ALinkToARowNothingHas_IsListed_ButSureOnlyWithProof_AndARealOrOwnRowIsTheAuthors()
    {
        var broken = Mod((Items, "Canteen", "Consumable", new JsonObject { ["RowName"] = "Canteen" }));
        var real = Mod((Items, "Canteen", "Consumable", new JsonObject { ["RowName"] = "Juice" }));
        var own = Mod((Items, "Canteen", "Consumable", new JsonObject { ["RowName"] = "Canteen_Drink" }), ("Traits-D_Consumable.json", "Canteen_Drink", "Modifier", 5));

        var issue = Assert.Single(ModHealthChecker.Check(broken, _data, rowsOtherModsAdd: new HashSet<string>()).Issues);
        Assert.Equal(ModIssueKind.BrokenLink, issue.Kind);
        Assert.False(issue.IsSure);
        Assert.Equal(ModIssueFix.KeepMod, issue.DefaultFix);
        Assert.Contains("'Canteen' in D_Consumable", issue.Why);
        Assert.Contains("needs a mod that adds 'Canteen'", issue.Why);
        Assert.Contains("'BasicWater'", issue.Why);

        Assert.False(ModHealthChecker.Check(real, _data).HasIssues);
        Assert.DoesNotContain(ModHealthChecker.Check(own, _data).Issues, i => i.ItemName == "Canteen");

        // An add-on linking to a row another Library mod adds is a dependency, not a broken link.
        var addOnFor = new HashSet<string> { ModHealthChecker.AddedRowKey("Traits-D_Consumable.json", "Canteen") };
        Assert.False(ModHealthChecker.Check(broken, _data, rowsOtherModsAdd: addOnFor).HasIssues);
    }

    /// <summary>What a mod adds, collected over the Library for rowsOtherModsAdd: rows the game doesn't have, not edits of its own (Canteen, Juice).</summary>
    [Fact]
    public void AddedRows_ListsTheRowsAModAdds_NotTheGamesOwn()
    {
        var mod = Mod((Items, "Canteen", "Weight", 3), ("Traits-D_Consumable.json", "Canteen_Drink", "Modifier", 5), ("Traits-D_Consumable.json", "Juice", "Modifier", 1));

        var added = ModHealthChecker.AddedRows(mod, new GameTableIndex(_data)).ToList();

        Assert.Equal([ModHealthChecker.AddedRowKey("Traits-D_Consumable.json", "Canteen_Drink")], added);
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
        Assert.Equal(1, ModHealthChecker.Check(swapped, _data).AuthorChangeCount);
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
        var issue = Assert.Single(ModHealthChecker.Check(package, _data, gameHistory: history, fromPrebuiltPak: true).Issues);
        Assert.Equal(ModIssueKind.OldCopy, issue.Kind);
        Assert.True(issue.IsSure);
    }

    /// <summary>A hand-made "revert the nerf" mod puts back the value the game had: listed, but kept unless you choose otherwise.</summary>
    [Fact]
    public void InAHandMadeMod_AValueTheGameHadBefore_IsOnlyListed_AndKept()
    {
        var package = Mod((Items, "Bread", "Weight", 25));
        var history = new GameValueHistory([new FieldChange(Items, "Bread", "Weight", JsonValue.Create(25), JsonValue.Create(20), ValueSemantic.Scalar)]);

        var report = ModHealthChecker.Check(package, _data, gameHistory: history);

        var issue = Assert.Single(report.Issues);
        Assert.Equal(ModIssueKind.OldCopy, issue.Kind);
        Assert.False(issue.IsSure);
        Assert.Equal(ModIssueFix.KeepMod, issue.DefaultFix);
        Assert.Equal(0, report.SureProblemCount);
        Assert.Equal(0, ModHealthChecker.ApplySure(package, report));
    }

    /// <summary>
    /// Ferani's Effortless Lights sets AlwaysActive and AutoActivate to what the game has now: the
    /// check says it fits, so nothing in it is ever changed by itself (taking them out is tidying).
    /// </summary>
    [Fact]
    public void InAHandMadeMod_ValuesTheGameAlreadyHas_AreNeverFixedByThemselves()
    {
        var package = Mod((Items, "Bread", "Weight", 20), (Items, "Cheese", "Weight", 50));

        var report = ModHealthChecker.Check(package, _data);

        Assert.Equal(2, report.SureCount);
        Assert.Equal(0, report.SureProblemCount);
        Assert.False(report.HasProblems);
        Assert.Equal(0, ModHealthChecker.ApplySure(package, report));
        Assert.Equal(2, package.Rows.Single(r => r.CurrentFile == Items).FileItems.Count);
    }

    /// <summary>
    /// Links are followed at any depth and in the mod's own new rows too (Diezel's Salt Collector
    /// links its own item to a mesh the game removed; a recipe input can name an item nothing has).
    /// Only listed: there's no game value to go back to.
    /// </summary>
    [Fact]
    public void ALinkInsideAField_OrInTheModsOwnRow_ToARowNothingHas_IsListed()
    {
        var package = Mod(
            (Recipes, "Bread_Recipe", "Inputs", new JsonArray { Input("Flour", 2), Input("Removed_Grain", 1) }),
            (Items, "My_Own_Bottle", "Consumable", new JsonObject { ["RowName"] = "Gone_Drink" }),
            (Items, "My_Own_Bottle", "Weight", 3),
            (Recipes, "My_Own_Bottle", "Inputs", new JsonArray { Input("Leather", 1) }));

        var report = ModHealthChecker.Check(package, _data, rowsOtherModsAdd: new HashSet<string>());

        var links = report.Issues.Where(i => i.Kind == ModIssueKind.BrokenLink).OrderBy(i => i.ItemName).ToList();
        Assert.Equal(2, links.Count);
        Assert.Equal(("Bread_Recipe", "Inputs[1].Element"), (links[0].ItemName, links[0].FieldName));
        Assert.Equal(("My_Own_Bottle", "Consumable"), (links[1].ItemName, links[1].FieldName));
        Assert.StartsWith("This row of the mod's own links to 'Gone_Drink'", links[1].Why);
        Assert.All(links, l =>
        {
            Assert.False(l.IsSure);
            Assert.Equal([ModIssueFix.KeepMod], l.Choices);
        });

        // A row another Library mod adds is a dependency, not a broken link.
        var withDependency = new HashSet<string> { ModHealthChecker.AddedRowKey(Items, "Removed_Grain"), ModHealthChecker.AddedRowKey("Traits-D_Consumable.json", "Gone_Drink") };
        Assert.DoesNotContain(ModHealthChecker.Check(package, _data, rowsOtherModsAdd: withDependency).Issues, i => i.Kind == ModIssueKind.BrokenLink);
    }

    [Fact]
    public void TheModsOwnNewItems_AreNeverFlaggedAsMissingRows()
    {
        var package = Mod(
            (Items, "My_Bag", "Weight", 5),
            (Recipes, "My_Bag", "Inputs", new JsonArray { Input("Leather", 4) }),
            (Items, "Wood_Floo", "Weight", 7));

        var report = ModHealthChecker.Check(package, _data);

        Assert.DoesNotContain(report.Issues, i => i.ItemName == "My_Bag" && i.Kind is ModIssueKind.MissingRow or ModIssueKind.MisspeltRow or ModIssueKind.OldGameRow);
        var renamed = Assert.Single(report.Issues, i => i.Kind == ModIssueKind.MissingRow);
        Assert.Equal("Wood_Floo", renamed.ItemName);
        Assert.Equal("Wood_Floor", renamed.SuggestedRow);

        // A similar name alone isn't proof of a rename (no game history here): offered, not made.
        Assert.False(renamed.IsSure);
        Assert.Equal(ModIssueFix.KeepMod, renamed.DefaultFix);
        Assert.DoesNotContain("was renamed", renamed.Why);
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

    private const string Consumables = "Traits-D_Consumable.json";

    /// <summary>laanp's paks both carry Cobalt Ingot, Electric Motor…: rows the game removed, not new items.</summary>
    [Fact]
    public void InAPrebuiltPak_RowsTheGameRemoved_ThatAnotherModCarriesToo_AreOldGameRows_ToTidy()
    {
        var package = Mod(
            (Items, "Cobalt_Ingot", "Consumable", new JsonObject { ["RowName"] = "Cobalt_Drink" }),
            (Consumables, "Cobalt_Drink", "Stats", new JsonObject()),
            (Items, "Old_Motor", "Weight", 10),
            (Items, "Bread", "Weight", 5),
            (Items, "Own_New_Item", "Weight", 1));
        var several = new HashSet<string>
        {
            ModHealthChecker.AddedRowKey(Items, "Cobalt_Ingot"), ModHealthChecker.AddedRowKey(Consumables, "Cobalt_Drink"), ModHealthChecker.AddedRowKey(Items, "Old_Motor"),
        };

        var report = ModHealthChecker.Check(package, _data, fromPrebuiltPak: true, rowsSeveralModsAdd: several);

        var old = report.Issues.Where(i => i.Kind == ModIssueKind.OldGameRow).Select(i => i.ItemName).Order().ToList();
        Assert.Equal(["Cobalt_Drink", "Cobalt_Ingot", "Old_Motor"], old);
        Assert.All(report.Issues.Where(i => i.Kind == ModIssueKind.OldGameRow), i => Assert.False(i.IsSure));
        Assert.Equal(0, report.ProblemCount);

        ModHealthChecker.Apply(package, report.Issues.Where(i => i.Kind == ModIssueKind.OldGameRow).Select(i => (i, ModIssueFix.RemoveRow)).ToList());
        Assert.Equal(["Bread", "Own_New_Item"], package.Rows.Single(r => r.CurrentFile == Items).FileItems.Select(i => i.Name).Order());
        Assert.DoesNotContain(package.Rows, r => r.CurrentFile == Consumables);
    }

    [Fact]
    public void OldGameRows_AreNeverFlagged_WhenUsed_OrOnlyThisModHasThem_OrInAHandMadeMod()
    {
        var package = Mod(
            (Items, "Bread", "Decayable", new JsonObject { ["RowName"] = "Old_Motor" }),
            (Items, "Old_Motor", "Consumable", new JsonObject { ["RowName"] = "Cobalt_Drink" }),
            (Consumables, "Cobalt_Drink", "Stats", new JsonObject()),
            (Items, "Only_Mine", "Weight", 1));
        var several = new HashSet<string> { ModHealthChecker.AddedRowKey(Items, "Old_Motor"), ModHealthChecker.AddedRowKey(Consumables, "Cobalt_Drink") };

        // Bread (a real edit) uses Old_Motor, which uses Cobalt_Drink: both are real content. Only_Mine is in no other mod.
        Assert.DoesNotContain(ModHealthChecker.Check(package, _data, fromPrebuiltPak: true, rowsSeveralModsAdd: several).Issues, i => i.Kind == ModIssueKind.OldGameRow);

        var unused = Mod((Items, "Old_Motor", "Weight", 10));
        Assert.DoesNotContain(ModHealthChecker.Check(unused, _data, fromPrebuiltPak: false, rowsSeveralModsAdd: several).Issues, i => i.Kind == ModIssueKind.OldGameRow);
    }

    [Fact]
    public void ATableNamedWithoutItsFolder_IsSureToMove_AndTheFixMovesItsRows()
    {
        // Real: This_Weeks_Changes wrote "D_FieldGuideSubcategories.json"; the merge skipped all 26 rows.
        var package = Mod(("D_ItemsStatic.json", "Bread", "Weight", 5));

        var report = ModHealthChecker.Check(package, _data);

        var issue = Assert.Single(report.Issues, i => i.Kind == ModIssueKind.UnknownTable);
        Assert.Equal("D_ItemsStatic.json", issue.CurrentFile);
        Assert.Equal(Items, issue.SuggestedRow);
        Assert.True(issue.IsAutomatic);
        Assert.Contains("skips its row", issue.Why);

        ModHealthChecker.ApplySure(package, report);
        Assert.Equal(5, package.Rows.Single(r => r.CurrentFile == Items).FileItems.Single(i => i.Name == "Bread").Fields["Weight"]!.GetValue<int>());
        Assert.DoesNotContain(package.Rows, r => r.CurrentFile == "D_ItemsStatic.json");
    }

    [Fact]
    public void ATableNamedWithoutItsFolder_WhenTheGameHasThatNameInTwoFolders_IsNotMovedOnAGuess()
    {
        Write("Items/D_Shared.json", new JsonArray { new JsonObject { ["Name"] = "Bread" } });
        Write("Other/D_Shared.json", new JsonArray { new JsonObject { ["Name"] = "Bread" } });
        var package = Mod(("D_Shared.json", "Bread", "Weight", 5));

        var report = ModHealthChecker.Check(package, _data);

        var issue = Assert.Single(report.Issues, i => i.Kind == ModIssueKind.UnknownTable);
        Assert.False(issue.IsSure);
        Assert.Null(issue.SuggestedRow);
        Assert.Equal([ModIssueFix.KeepMod], issue.Choices);
        Assert.Contains("more than one folder", issue.Why);
        Assert.Equal(0, ModHealthChecker.ApplySure(package, report));
        Assert.Contains(package.Rows, r => r.CurrentFile == "D_Shared.json");
    }

    [Fact]
    public void MovingATableIntoOneTheModAlreadyHas_KeepsTheRowsThatWorkToday_Winning()
    {
        // The mod has Bread in the real table (applied in game today) and an older copy under the
        // folderless name (skipped today). Moving the old copy in must not make it win.
        var package = Mod((Items, "Bread", "Weight", 5), ("D_ItemsStatic.json", "Bread", "Weight", 9));

        var report = ModHealthChecker.Check(package, _data);
        ModHealthChecker.ApplySure(package, report);

        var breads = package.Rows.Single(r => r.CurrentFile == Items).FileItems.Where(i => i.Name == "Bread").ToList();
        Assert.Equal(5, breads[^1].Fields["Weight"]!.GetValue<int>());
    }

    [Fact]
    public void ATableTheGameDoesntHaveAnywhere_IsOnlyListed_AndNoGameDataMeansNoTableIsUnknown()
    {
        var package = Mod(("Items-D_NoSuchThing.json", "Bread", "Weight", 5));

        var issue = Assert.Single(ModHealthChecker.Check(package, _data).Issues, i => i.Kind == ModIssueKind.UnknownTable);
        Assert.False(issue.IsSure);
        Assert.Equal([ModIssueFix.KeepMod], issue.Choices);

        var empty = Path.Combine(_data, "empty");
        Directory.CreateDirectory(empty);
        Assert.DoesNotContain(ModHealthChecker.Check(package, empty).Issues, i => i.Kind == ModIssueKind.UnknownTable);
    }

    [Fact]
    public void AFieldInOtherCase_IsSureToRename_ATypoIsOnlySuggested_AndMetadataIsAlwaysAField()
    {
        var package = Mod((Items, "Bread", "weight", 5), (Items, "Cheese", "Wieght", 6), (Items, "Wood_Floor", "Banana", 1),
            (Items, "Canteen", "Metadata", new JsonObject { ["RequiredFeatureLevel"] = "Base" }));

        var fields = ModHealthChecker.Check(package, _data).Issues.Where(i => i.Kind == ModIssueKind.UnknownField).ToDictionary(i => i.ItemName);

        Assert.Equal(3, fields.Count);
        Assert.True(fields["Bread"].IsAutomatic);
        Assert.Equal("Weight", fields["Bread"].SuggestedRow);
        Assert.False(fields["Cheese"].IsSure);
        Assert.Equal(ModIssueFix.RenameField, fields["Cheese"].DefaultFix);
        Assert.Equal("Weight", fields["Cheese"].SuggestedRow);
        Assert.Null(fields["Wood_Floor"].SuggestedRow);
        Assert.Equal([ModIssueFix.UseGame, ModIssueFix.KeepMod], fields["Wood_Floor"].Choices);

        ModHealthChecker.Apply(package, [(fields["Bread"], ModIssueFix.RenameField), (fields["Wood_Floor"], ModIssueFix.UseGame)]);
        var items = package.Rows.Single(r => r.CurrentFile == Items).FileItems;
        Assert.Equal(5, items.Single(i => i.Name == "Bread").Fields["Weight"]!.GetValue<int>());
        Assert.DoesNotContain(items, i => i.Name == "Wood_Floor");
    }

    [Fact]
    public void DeletingAFieldInOtherCase_IsNotRenamed_SinceTheRenameWouldDeleteTheGamesField()
    {
        // "weight": null deletes a field the game doesn't have (nothing happens in game); renamed to
        // "Weight": null it would delete the game's Weight from the row.
        var package = Mod((Items, "Bread", "weight", null));

        var report = ModHealthChecker.Check(package, _data);
        ModHealthChecker.ApplySure(package, report);

        Assert.DoesNotContain(report.Issues, i => i.Kind == ModIssueKind.UnknownField && i.DefaultFix == ModIssueFix.RenameField);
        Assert.DoesNotContain(package.Rows.SelectMany(r => r.FileItems), i => i.Fields.ContainsKey("Weight"));
    }

    [Fact]
    public void ARowListedTwice_GivesOneFinding_AndTheRenameKeepsACopyThatAlreadyHasTheRightName()
    {
        var package = Mod((Items, "Bread", "Wieght", 5));
        var items = package.Rows.Single(r => r.CurrentFile == Items).FileItems;
        items.Insert(0, new ExmodFileItem { Name = "Bread", Fields = new() { ["Weight"] = 7, ["Wieght"] = 6 } });

        var issue = Assert.Single(ModHealthChecker.Check(package, _data).Issues, i => i.Kind == ModIssueKind.UnknownField);
        ModHealthChecker.Apply(package, [(issue, ModIssueFix.RenameField)]);

        Assert.Equal(7, items[0].Fields["Weight"]!.GetValue<int>());
        Assert.False(items[0].Fields.ContainsKey("Wieght"));
        Assert.Equal(5, items[1].Fields["Weight"]!.GetValue<int>());
    }

    [Fact]
    public void ARowListedTwice_AProblemOnlyInTheEarlierCopy_IsFound_AndTheSureFixGoesToThatCopy()
    {
        // The merge takes every copy's fields, so a misspelt field in the first copy reaches the game
        // as much as one in the last; it used to be judged on the last copy only and never listed.
        var package = Mod((Items, "Bread", "Weight", 5));
        var items = package.Rows.Single(r => r.CurrentFile == Items).FileItems;
        items.Insert(0, new ExmodFileItem { Name = "Bread", Fields = new() { ["weight"] = 7 } });

        var report = ModHealthChecker.Check(package, _data);

        var issue = Assert.Single(report.Issues, i => i.Kind == ModIssueKind.UnknownField);
        Assert.Equal("weight", issue.FieldName);
        Assert.Equal(7, issue.ModValue!.GetValue<int>());
        Assert.True(issue.IsAutomatic);

        ModHealthChecker.ApplySure(package, report);
        Assert.Equal(7, items[0].Fields["Weight"]!.GetValue<int>());
        Assert.False(items[0].Fields.ContainsKey("weight"));
        Assert.Equal(5, items[1].Fields["Weight"]!.GetValue<int>());
    }

    [Fact]
    public void ARowListedTwice_TheSameMisspellingInBothCopies_GivesOneFinding_AndAnOldCopyInTheEarlierOneIsFound()
    {
        var package = Mod((Items, "Wood_Floor", "Wieght", 5));
        var items = package.Rows.Single(r => r.CurrentFile == Items).FileItems;
        items.Insert(0, new ExmodFileItem { Name = "Wood_Floor", Fields = new() { ["Wieght"] = 6, ["Manual_Tags"] = Tags("Building.Wood", "Audio.Shelter") } });

        var report = ModHealthChecker.Check(package, _data, fromPrebuiltPak: true);

        Assert.Single(report.Issues, i => i.Kind == ModIssueKind.UnknownField);
        Assert.Equal(5, report.Issues.Single(i => i.Kind == ModIssueKind.UnknownField).ModValue!.GetValue<int>());
        var old = Assert.Single(report.Issues, i => i.Kind == ModIssueKind.OldCopy);
        Assert.Equal("Manual_Tags", old.FieldName);

        ModHealthChecker.ApplySure(package, report);
        Assert.False(items[0].Fields.ContainsKey("Manual_Tags"));
    }

    [Fact]
    public void TextUnderAnotherRowsKeyWithOtherWords_IsListed_AndTheFixGivesItItsOwnKey()
    {
        Write("Traits/D_Itemable.json", new JsonArray
        {
            new JsonObject { ["Name"] = "Item_Fiber", ["DisplayName"] = NsLocText.Make("D_Itemable", "Item_Fiber-DisplayName", "Fiber") },
        });
        const string Itemable = "Traits-D_Itemable.json";
        var package = Mod(
            // Copied from the game's Fiber with the words changed but not the key.
            (Itemable, "Item_Rope", "DisplayName", NsLocText.Make("D_Itemable", "Item_Fiber-DisplayName", "Rope")),
            // Reusing the game's text as it is: the game's words and translations, fine.
            (Itemable, "Item_Fiber_Bundle", "DisplayName", NsLocText.Make("D_Itemable", "Item_Fiber-DisplayName", "Fiber")),
            // Two of the mod's own rows under one key.
            (Itemable, "Item_Lamp", "DisplayName", NsLocText.Make("D_Itemable", "Item_Lamp-DisplayName", "Lamp")),
            (Itemable, "Item_Lamp_Small", "DisplayName", NsLocText.Make("D_Itemable", "Item_Lamp-DisplayName", "Small lamp")));

        var shared = ModHealthChecker.Check(package, _data).Issues.Where(i => i.Kind == ModIssueKind.SharedTextKey).ToDictionary(i => i.ItemName);

        Assert.Equal(["Item_Lamp_Small", "Item_Rope"], shared.Keys.Order());
        Assert.Contains("the game's 'Item_Fiber'", shared["Item_Rope"].Why);
        Assert.False(shared["Item_Rope"].IsSure);

        ModHealthChecker.Apply(package, [(shared["Item_Rope"], ModIssueFix.OwnTextKey)]);
        var rope = package.Rows.Single(r => r.CurrentFile == Itemable).FileItems.Single(i => i.Name == "Item_Rope").Fields["DisplayName"]!.GetValue<string>();
        Assert.True(NsLocText.TryParse(rope, out _, out var key, out var text));
        Assert.Equal(("Item_Rope-DisplayName", "Rope"), (key, text));
    }

    private void AddGameTagToCheese()
    {
        var path = Path.Combine(_data, "Items", "D_ItemsStatic.json");
        var table = JsonNode.Parse(File.ReadAllText(path))!;
        var cheese = table["Rows"]!.AsArray().Single(r => r!["Name"]!.ToString() == "Cheese")!;
        cheese["Manual_Tags"]!["GameplayTags"]!.AsArray().Add(new JsonObject { ["TagName"] = "FieldGuide.Food" });
        File.WriteAllText(path, table.ToJsonString());
    }

    /// <summary>
    /// A name no game version ever had, a letter from a game row with the fields it sets: a misspelt
    /// edit, which in game adds a useless new row instead. A really new
    /// row (a name like nothing in the game) isn't flagged.
    /// </summary>
    [Fact]
    public void ARowNameOneLetterFromAGameRow_IsAProbablyMisspeltEdit()
    {
        var history = GameRowHistory.Load(localFile: null);

        var issue = Assert.Single(ModHealthChecker.Check(Mod((Items, "Wod_Floor", "Weight", 5)), _data, rowHistory: history).Issues);
        Assert.Equal(ModIssueKind.MisspeltRow, issue.Kind);
        Assert.Equal("Wood_Floor", issue.SuggestedRow);
        Assert.Equal("ISL-114", issue.Code);
        Assert.Contains(ModIssueFix.RenameRow, issue.Choices);
        Assert.False(issue.IsSure);
        Assert.Contains("adds a new row nothing uses", issue.Why);

        Assert.DoesNotContain(ModHealthChecker.Check(Mod((Items, "MkHaters_Lantern", "Weight", 5)), _data, rowHistory: history).Issues, i => i.Kind == ModIssueKind.MisspeltRow);
    }

    /// <summary>Tested in game: new rows in D_CharacterGrowth load but the game can't use them; editing its own rows is fine. The game's log is quoted as proof.</summary>
    [Fact]
    public void NewRowsInABuiltInTable_AreFlagged_WithTheGamesLogAsProof()
    {
        Write("Character/D_CharacterGrowth.json", new JsonArray { new JsonObject { ["Name"] = "Player", ["MaxLevel"] = 60 } });
        var package = Mod(("Character-D_CharacterGrowth.json", "Player", "MaxLevel", 70), ("Character-D_CharacterGrowth.json", "MkHaters_Wolf_Growth", "MaxLevel", 50));
        var logged = new[] { new KnownGameBehaviour.LoggedMismatch("D_CharacterGrowth", 7, 6, "line") };

        var issue = Assert.Single(ModHealthChecker.Check(package, _data, loggedMismatches: logged).Issues);

        Assert.Equal(ModIssueKind.RowsInBuiltInTable, issue.Kind);
        Assert.Equal("MkHaters_Wolf_Growth", issue.ItemName);
        Assert.Contains("level 0", issue.Why);
        Assert.Contains("7 rows loaded, 6 built in", issue.Why);
        Assert.Equal([ModIssueFix.RemoveRow, ModIssueFix.KeepMod], issue.Choices);
    }

    /// <summary>Tested in game: an aura stat on a placed item gives no aura (on a character's gear it isn't flagged); a bag's spoil-rate stat changes nothing.</summary>
    [Fact]
    public void StatsTheGameIgnores_AreListedAsChangingNothing()
    {
        var rows = new JsonArray
        {
            new JsonObject { ["Name"] = "Wood_Bed", ["Deployable"] = new JsonObject { ["RowName"] = "Bed" } },
            new JsonObject { ["Name"] = "Envirosuit", ["Weight"] = 100 },
            // Regression: a lower-case "none" link used to count as a placed item, so its aura stats were called useless.
            new JsonObject { ["Name"] = "Lantern", ["Deployable"] = new JsonObject { ["RowName"] = "none" } },
        };
        Write("Items/D_ItemsStatic.json", rows);
        Write("Inventory/D_InventoryInfo.json", new JsonArray { new JsonObject { ["Name"] = "Food_Bag", ["Stats"] = new JsonObject() } });
        static JsonObject Stat(string name) => new() { [$"(Value=\"{name}\")"] = 25 };
        var package = Mod(
            (Items, "Wood_Bed", "AdditionalStats", Stat("GrantedAuraHealthRegen_?")),
            (Items, "Envirosuit", "AdditionalStats", Stat("GrantedAuraHealthRegen_?")),
            (Items, "Lantern", "AdditionalStats", Stat("GrantedAuraHealthRegen_?")),
            ("Inventory-D_InventoryInfo.json", "Food_Bag", "Stats", Stat(KnownGameBehaviour.SpoilRateStat)));

        var noEffect = ModHealthChecker.Check(package, _data).Issues.Where(i => i.Kind == ModIssueKind.NoEffect).ToList();

        Assert.Equal(["Food_Bag", "Wood_Bed"], noEffect.Select(i => i.ItemName).Order());
        Assert.Contains("no aura", noEffect.Single(i => i.ItemName == "Wood_Bed").Why);
        Assert.Contains("spoil", noEffect.Single(i => i.ItemName == "Food_Bag").Why);
        Assert.All(noEffect, i => Assert.Equal([ModIssueFix.KeepMod], i.Choices));
    }

    /// <summary>
    /// A new item nothing hands out: no template at all, or a template no recipe or reward names.
    /// (A missing Traits tag isn't flagged: 20 items in 75 real mods lack one and work, e.g. Larkwell's care packages.)
    /// </summary>
    [Fact]
    public void NewItems_ThatNobodyCanGet_AreListed()
    {
        var rows = new JsonArray();
        for (var i = 0; i < 5; i++)
        {
            rows.Add(new JsonObject { ["Name"] = $"Tool_{i}", ["Durable"] = new JsonObject { ["RowName"] = "Tool_Durability" }, ["Generated_Tags"] = Tags("Traits.Durable") });
        }

        Write("Items/D_ItemsStatic.json", rows);
        Write("Items/D_ItemTemplate.json", new JsonArray { new JsonObject { ["Name"] = "Tool_0", ["ItemStaticData"] = new JsonObject { ["RowName"] = "Tool_0" } } });
        var item = new JsonObject { ["RowName"] = "Tool_Durability" };
        var template = new JsonObject { ["RowName"] = "MkHaters_Tool" };

        var unmade = Mod((Items, "MkHaters_Tool", "Durable", item), (Items, "MkHaters_Tool", "Generated_Tags", Tags("Item.Tool")),
            ("Items-D_ItemTemplate.json", "MkHaters_Tool", "ItemStaticData", template));
        var issues = ModHealthChecker.Check(unmade, _data).Issues;
        Assert.Contains("Nothing in this mod hands it out", Assert.Single(issues, i => i.Kind == ModIssueKind.NothingGivesItem).Why);
        Assert.Equal("ISL-117", issues.Single(i => i.Kind == ModIssueKind.NothingGivesItem).Code);

        var crafted = Mod((Items, "MkHaters_Tool", "Durable", item.DeepClone()), (Items, "MkHaters_Tool", "Generated_Tags", Tags("Traits.Durable")),
            ("Items-D_ItemTemplate.json", "MkHaters_Tool", "ItemStaticData", template.DeepClone()),
            (Recipes, "MkHaters_Tool_Recipe", "Outputs", new JsonArray { new JsonObject { ["Element"] = new JsonObject { ["RowName"] = "MkHaters_Tool", ["DataTableName"] = "D_ItemTemplate" }, ["Count"] = 1 } }));
        Assert.DoesNotContain(ModHealthChecker.Check(crafted, _data).Issues, i => i.Kind == ModIssueKind.NothingGivesItem);

        var noTemplate = Mod((Items, "MkHaters_Tool", "Durable", item.DeepClone()), (Items, "MkHaters_Tool", "Generated_Tags", Tags("Traits.Durable")));
        Assert.Contains("has no item template", Assert.Single(ModHealthChecker.Check(noTemplate, _data).Issues, i => i.Kind == ModIssueKind.NothingGivesItem).Why);
    }

    /// <summary>The lines the game and the dedicated server write at start (copied from a real server log): the last one per table counts.</summary>
    [Fact]
    public void TheGamesLog_TellsWhichBuiltInTablesGotRowsTheGameWasNotBuiltWith()
    {
        var log = Path.Combine(_data, "Icarus.log");
        Directory.CreateDirectory(_data);
        File.WriteAllLines(log,
        [
            "[2026.11.01-14.42.48:084][  0]LogTemp: Error: URecipeSetsTable row count doesn't match the generated enum, this probably means the generated code is out of date. JSON: 70 | Code: 68",
            "[2026.11.02-15.46.53:864][  0]LogTemp: Error: UAIRelationshipsTable row count doesn't match the generated enum, this probably means the generated code is out of date. JSON: 19 | Code: 16",
            "[2026.11.02-15.46.54:889][  0]LogTemp: Error: URecipeSetsTable row count doesn't match the generated enum, this probably means the generated code is out of date. JSON: 69 | Code: 68",
            "[2026.11.02-15.46.55:000][  0]LogInit: something else",
        ]);

        var found = KnownGameBehaviour.ReadLoggedMismatches(log).OrderBy(f => f.Table).ToList();

        Assert.Equal(["D_AIRelationships:19/16", "D_RecipeSets:69/68"], found.Select(f => $"{f.Table}:{f.Loaded}/{f.BuiltIn}"));
        Assert.Empty(KnownGameBehaviour.ReadLoggedMismatches(Path.Combine(_data, "missing.log")));
    }

    /// <summary>
    /// From the game's code (not tested in game): a hammer repair of a placed item or building
    /// piece ignores its repair needs, so changing them there gets a note; a tool's doesn't. Notes
    /// are never problems: Check leaves them out.
    /// </summary>
    [Fact]
    public void GameCodeNotes_RepairNeedsOfABuildingPiece_GetANote_AToolsDont()
    {
        Write("Items/D_ItemsStatic.json", new JsonArray
        {
            new JsonObject { ["Name"] = "Stone_Wall", ["Buildable"] = new JsonObject { ["RowName"] = "Stone_Wall" }, ["Durable"] = new JsonObject { ["RowName"] = "Stone_Building" } },
            new JsonObject { ["Name"] = "Stone_Axe", ["Durable"] = new JsonObject { ["RowName"] = "Stone_Axe" } },
            new JsonObject { ["Name"] = "Kit_Lamp", ["Durable"] = new JsonObject { ["RowName"] = "Basic" } },
        });
        Write("Traits/D_Durable.json", new JsonArray
        {
            new JsonObject { ["Name"] = "Stone_Building", ["ItemsForRepair"] = new JsonArray { Repair("Stone", 1) } },
            new JsonObject { ["Name"] = "Stone_Axe", ["ItemsForRepair"] = new JsonArray { Repair("Stone", 1) } },
            new JsonObject { ["Name"] = "Basic" },
        });
        const string durable = "Traits-D_Durable.json";
        var package = Mod(
            (durable, "Stone_Building", "ItemsForRepair", new JsonArray { Repair("Stone", 5) }),
            (durable, "Stone_Axe", "ItemsForRepair", new JsonArray { Repair("Stone", 5) }),
            (durable, "Basic", "ItemsForRepair", new JsonArray { Repair("Wood", 2) }),
            (Items, "Kit_Lamp", "Deployable", new JsonObject { ["RowName"] = "Lamp" }));

        var notes = ModHealthChecker.GameCodeNotes(package, new GameTableIndex(_data));

        Assert.Equal(["Basic", "Stone_Building"], notes.Select(n => n.ItemName).Order());
        Assert.All(notes, n =>
        {
            Assert.Equal(ModIssueKind.GameCodeNote, n.Kind);
            Assert.True(n.IsNote);
            Assert.Equal("ItemsForRepair", n.FieldName);
            Assert.Equal([ModIssueFix.KeepMod], n.Choices);
            Assert.StartsWith(KnownGameBehaviour.FromGameCodePrefix, n.Why, StringComparison.Ordinal);
        });
        Assert.DoesNotContain(ModHealthChecker.Check(package, _data).Issues, i => i.Kind == ModIssueKind.GameCodeNote);
    }

    /// <summary>From the game's code (not tested in game): seeds are numbered by place, so a mod's new seed rows get a note; editing a game seed doesn't.</summary>
    [Fact]
    public void GameCodeNotes_NewSeedRows_GetANote()
    {
        Write("Farming/D_FarmingSeeds.json", new JsonArray { new JsonObject { ["Name"] = "Wheat", ["GrowthTime"] = 10 } });
        const string seeds = "Farming-D_FarmingSeeds.json";
        var package = Mod((seeds, "Grape", "GrowthTime", 5), (seeds, "wheat", "GrowthTime", 5));

        var note = Assert.Single(ModHealthChecker.GameCodeNotes(package, new GameTableIndex(_data)));

        Assert.Equal("Grape", note.ItemName);
        Assert.Equal(KnownGameBehaviour.SeedNumbers.Note, note.Why);
        Assert.Equal("ISL-118", note.Code);
    }

    private static JsonObject Repair(string item, int amount) =>
        new() { ["Item"] = new JsonObject { ["RowName"] = item, ["DataTableName"] = "D_ItemsStatic" }, ["Amount"] = amount };

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
