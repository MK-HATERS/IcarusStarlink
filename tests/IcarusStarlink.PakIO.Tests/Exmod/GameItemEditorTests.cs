using System.Text.Json.Nodes;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests.Exmod;

/// <summary>The Editor page's engine: changing the game's own items, writing only what changed.</summary>
public sealed class GameItemEditorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "GameItemEditor_" + Guid.NewGuid().ToString("N"));
    private readonly GameTableIndex _game;

    public GameItemEditorTests()
    {
        MiniGameData.Write(_root);
        _game = new GameTableIndex(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static ExmodPackage EmptyMod() => new()
    {
        Name = "m", Author = "a", Version = "1", Description = "", FileName = "m",
        Rows = [new ExmodFileRow { CurrentFile = "EndOfMod" }],
    };

    private static ExmodFileItem? Item(ExmodPackage mod, string file, string name) =>
        mod.Rows.FirstOrDefault(r => r.CurrentFile == file)?.FileItems.FirstOrDefault(i => i.Name == name);

    private EditableSection Section(ExmodPackage? mod, string item, string key) => GameItemEditor.Describe(_game, mod, item)!.Sections.Single(s => s.Key == key);

    private static EditableField Field(EditableSection section, string label) => section.Fields.Single(f => f.Label == label);

    /// <summary>Seen in the mod editor's Add a field (2026-10-28): "bIsClientSideOnly" read as "B is client side only".</summary>
    [Theory]
    [InlineData("bIsClientSideOnly", "Is client side only")]
    [InlineData("Some_Odd_Field", "Some odd field")]
    [InlineData("bonus", "Bonus")]
    public void FieldNames_ReadAsPlainWords_WithoutUnrealsOnOffMark(string field, string label)
    {
        Assert.Equal(label, GameItemEditor.FieldText(field, isNumber: false).Label);
    }

    /// <summary>The user asked for a tip on what each setting does: the fields mods change most get a plain name and tip (FieldTips), the editor's own list still first.</summary>
    [Fact]
    public void CommonlyModdedFields_HaveATip_AndTheEditorsOwnWordsStillWin()
    {
        var recipeSets = GameItemEditor.FieldText("RecipeSets", isNumber: false);
        Assert.Equal("Made at", recipeSets.Label);
        Assert.Contains("benches", recipeSets.Description);

        var stability = GameItemEditor.FieldText("MaxAnchoredStability", isNumber: true);
        Assert.Contains("Higher", stability.Description);

        Assert.Equal("Stack size", GameItemEditor.FieldText("MaxStack", isNumber: true).Label);
    }

    /// <summary>
    /// Tips that said the wrong thing, checked against the game's tables: ResourceCostMultipliers is
    /// a list of crafting-cost stats ("BasePickaxeCraftingCost_+%"), not a water/power factor;
    /// FatigueModifier links to a buff row (Seed_Fatigue), it isn't a number where lower is better;
    /// Transmutable is what a generator gets from the item (oxite → oxygen, stick → charcoal left);
    /// Title is also a lineage's or a stat group's name, not only a stat's.
    /// </summary>
    [Fact]
    public void Tips_SayWhatTheGamesTablesHold()
    {
        var cost = GameItemEditor.FieldText("ResourceCostMultipliers", isNumber: false).Description;
        Assert.Contains("stats", cost);
        Assert.DoesNotContain("water", cost);

        var fatigue = GameItemEditor.FieldText("FatigueModifier", isNumber: false).Description;
        Assert.StartsWith("Links to the buff", fatigue);
        Assert.DoesNotContain("Lower", fatigue);

        Assert.Contains("generator", GameItemEditor.FieldText("Transmutable", isNumber: false).Description);
        Assert.Equal("The name players see.", GameItemEditor.FieldText("Title", isNumber: false).Description);
        Assert.DoesNotContain("per second", GameItemEditor.FieldText("GenerationRate", isNumber: true).Label);
    }

    /// <summary>A stat's tip quotes the line players read in the game (D_Stats), with N for the number.</summary>
    [Fact]
    public void StatHint_QuotesTheGamesOwnLine()
    {
        var data = Path.Combine(_root, "StatsOnly");
        Directory.CreateDirectory(Path.Combine(data, "Stats"));
        File.WriteAllText(Path.Combine(data, "Stats", "D_Stats.json"), """
            {"RowStruct":"S","Defaults":{},"Rows":[{"Name":"BaseWeightCapacity_+","PositiveDescription":"NSLOCTEXT(\"D_Stats\", \"BaseWeightCapacity_+-PositiveDescription\", \"+{0}kg Weight Capacity\")"}]}
            """);

        GameStats.LearnGameText(new GameTableIndex(data));

        Assert.EndsWith("In game: \"+Nkg Weight Capacity\".", GameStats.StatHint("BaseWeightCapacity_+"));
    }

    [Fact]
    public void Describe_ShowsEachRowTheItemUses_WithItsSharing()
    {
        var view = GameItemEditor.Describe(_game, null, "Envirosuit")!;

        Assert.Equal("Xigo Envirosuit", view.DisplayName);
        Assert.Equal(["Itemable", "Armour", "Decayable", GameItemEditor.WorkshopSection], view.Sections.Select(s => s.Key));
        Assert.Equal(1, view.Sections.Single(s => s.Key == "Armour").SharedBy);
        Assert.Equal(2, view.Sections.Single(s => s.Key == "Decayable").SharedBy); // the suit and the bread
        Assert.Contains(view.KeptAsGames, k => k.StartsWith("Meshable"));

        var itemable = view.Sections[0];
        Assert.Equal("Xigo Envirosuit", NsLocText.TextOf(Field(itemable, "Name in game").GameValue!.GetValue<string>()));
        Assert.True(Field(itemable, "Stack size").Inherited); // the table default, 1
        Assert.Equal("Oxygen slots", Assert.Single(view.Sections.Single(s => s.Key == "Armour").Fields).Label);
        Assert.Equal(50, Field(view.Sections.Single(s => s.Key == GameItemEditor.WorkshopSection), "Research cost (Credits)").GameValue!.GetValue<int>());
    }

    /// <summary>Recipes and workshop items name the item's template, often not the item (Stone_Furnace makes Kit_Stone_Furnace): the Editor showed neither.</summary>
    [Fact]
    public void Describe_TemplateNamedDifferently_FindsTheRecipeAndWorkshopPrice()
    {
        var furnace = GameItemEditor.Describe(_game, null, "Kit_Stone_Furnace")!;
        var chest = GameItemEditor.Describe(_game, null, "Meta_Carbon_Chest_Alpha")!;

        Assert.Contains(furnace.Sections, s => s.Key == $"{GameItemEditor.RecipeSection}:Stone_Furnace");
        Assert.DoesNotContain("also makes", furnace.Sections.Single(s => s.Key.StartsWith(GameItemEditor.RecipeSection)).Title);
        Assert.Equal(120, Field(chest.Sections.Single(s => s.Key == GameItemEditor.WorkshopSection), "Research cost (Credits)").GameValue!.GetValue<int>());
    }

    [Fact]
    public void Describe_AConsumable_IncludesItsBuffAndItsRecipe()
    {
        var view = GameItemEditor.Describe(_game, null, "Food_Bread")!;

        var buff = view.Sections.Single(s => s.Key == GameItemEditor.BuffSection);
        Assert.Equal("Bread", buff.RowName);
        Assert.Equal(125, Field(buff, "Maximum stamina").GameValue!.GetValue<int>());
        Assert.Equal(900, Field(view.Sections.Single(s => s.Key == "Consumable"), "Buff lasts (seconds)").GameValue!.GetValue<int>());
        Assert.Contains(view.Sections.Single(s => s.Key == $"{GameItemEditor.RecipeSection}:Bread").Fields, f => f.Label == "Needs Dough bread");
    }

    /// <summary>The default Envirosuit made lighter, renamed, and given a second oxygen slot plus cold resistance.</summary>
    [Fact]
    public void Apply_WritesOnlyTheChangedFields_OnTheGamesOwnRows()
    {
        var mod = EmptyMod();
        var itemable = Section(mod, "Envirosuit", "Itemable");
        var armour = Section(mod, "Envirosuit", "Armour");

        GameItemEditor.Apply(_game, mod, "Envirosuit",
        [
            new(itemable.Key, Field(itemable, "Weight").Path, JsonValue.Create(50)),
            new(itemable.Key, Field(itemable, "Name in game").Path, JsonValue.Create("Xigo \"Mk II\"")),
            new(armour.Key, Field(armour, "Oxygen slots").Path, JsonValue.Create(2)),
            new(armour.Key, new EditPath("ArmourStats", [GameStats.StatKeyFor("BaseColdResistance_%")]), JsonValue.Create(10)),
        ], new HashSet<string>());

        var item = Item(mod, "Traits-D_Itemable.json", "Item_Envirosuit")!;
        Assert.Equal(["Weight", "DisplayName"], item.Fields.Keys);
        Assert.Equal("""NSLOCTEXT("D_Itemable", "Item_Envirosuit-DisplayName", "Xigo \"Mk II\"")""", item.Fields["DisplayName"]!.GetValue<string>());
        var stats = (JsonObject)Item(mod, "Traits-D_Armour.json", "IO_Undersuit")!.Fields["ArmourStats"]!;
        Assert.Equal(2, stats["(Value=\"BaseOxygenSlots_+\")"]!.GetValue<int>());
        Assert.Equal(10, stats["(Value=\"BaseColdResistance_%\")"]!.GetValue<int>());
        Assert.Null(Item(mod, "Items-D_ItemsStatic.json", "Envirosuit")); // nothing re-pointed, nothing new
        Assert.Equal("EndOfMod", mod.Rows[^1].CurrentFile);

        // What the page then shows: the mod's values on top of the game's.
        Assert.Equal(50, Field(Section(mod, "Envirosuit", "Itemable"), "Weight").CurrentValue!.GetValue<int>());
    }

    [Fact]
    public void Apply_ASharedRowAsTheItemsOwnCopy_ChangesOnlyThatItem()
    {
        var mod = EmptyMod();
        var decay = Section(mod, "Envirosuit", "Decayable");

        var lines = GameItemEditor.Apply(_game, mod, "Envirosuit", [new(decay.Key, Field(decay, "Decay time (seconds)").Path, JsonValue.Create(600))], new HashSet<string> { decay.Key });

        Assert.Equal(600, Item(mod, "Traits-D_Decayable.json", "Decay_General_Envirosuit")!.Fields["DecayTime"]!.GetValue<int>());
        Assert.Null(Item(mod, "Traits-D_Decayable.json", "Decay_General")); // the bread's decay is untouched
        Assert.Equal("Decay_General_Envirosuit", Item(mod, "Items-D_ItemsStatic.json", "Envirosuit")!.Fields["Decayable"]!["RowName"]!.GetValue<string>());
        Assert.Contains(lines, l => l.Contains("own copy"));

        var after = Section(mod, "Envirosuit", "Decayable");
        Assert.True(after.IsOwnCopy);
        Assert.Equal(1, after.SharedBy);
        // With the mod applied the suit uses its own copy, so the game's row is only the bread's now.
        Assert.Equal(1, Section(mod, "Food_Bread", "Decayable").SharedBy);
    }

    [Fact]
    public void Apply_ASharedRowForAll_EditsTheGamesRow()
    {
        var mod = EmptyMod();
        var decay = Section(mod, "Envirosuit", "Decayable");

        GameItemEditor.Apply(_game, mod, "Envirosuit", [new(decay.Key, Field(decay, "Decay time (seconds)").Path, JsonValue.Create(600))], new HashSet<string>());

        Assert.Equal(600, Item(mod, "Traits-D_Decayable.json", "Decay_General")!.Fields["DecayTime"]!.GetValue<int>());
        Assert.Null(Item(mod, "Items-D_ItemsStatic.json", "Envirosuit"));
    }

    [Fact]
    public void Apply_ABuffAsItsOwnCopy_RepointsTheConsumable_WithItsOwnTextKeys()
    {
        // A second food sharing Bread's buff, so it counts as shared.
        File.WriteAllText(Path.Combine(_root, "Traits", "D_Consumable.json"), """
            {"RowStruct":"S","Defaults":{},"Rows":[
              {"Name":"Food_Bread","Modifier":{"Modifier":{"RowName":"Bread"},"ModifierLifetime":900}},
              {"Name":"Food_Toast","Modifier":{"Modifier":{"RowName":"Bread"},"ModifierLifetime":600}}
            ]}
            """);
        var statics = Path.Combine(_root, "Items", "D_ItemsStatic.json");
        File.WriteAllText(statics, File.ReadAllText(statics).Replace(
            "{\"Name\":\"Food_Bread\",",
            "{\"Name\":\"Food_Toast\",\"Itemable\":{\"RowName\":\"Item_Bread\"},\"Consumable\":{\"RowName\":\"Food_Toast\"}},\n              {\"Name\":\"Food_Bread\","));
        var game = new GameTableIndex(_root);
        var mod = EmptyMod();
        var buff = GameItemEditor.Describe(game, mod, "Food_Bread")!.Sections.Single(s => s.Key == GameItemEditor.BuffSection);
        Assert.Equal(2, buff.SharedBy);

        GameItemEditor.Apply(game, mod, "Food_Bread", [new(buff.Key, Field(buff, "Maximum stamina").Path, JsonValue.Create(200))], new HashSet<string> { buff.Key });

        var copy = Item(mod, "Modifiers-D_ModifierStates.json", "Bread_Food_Bread")!;
        Assert.Equal(200, copy.Fields["GrantedStats"]!["(Value=\"BaseMaximumStamina_+\")"]!.GetValue<int>());
        Assert.Contains("Bread_Food_Bread-ModifierName", copy.Fields["ModifierName"]!.GetValue<string>());
        var consumable = Item(mod, "Traits-D_Consumable.json", "Food_Bread")!;
        Assert.Equal("Bread_Food_Bread", consumable.Fields["Modifier"]!["Modifier"]!["RowName"]!.GetValue<string>());
        Assert.Equal(900, consumable.Fields["Modifier"]!["ModifierLifetime"]!.GetValue<int>());
    }

    [Fact]
    public void Apply_BackToTheGamesValue_TakesTheFieldBackOut()
    {
        var mod = EmptyMod();
        var itemable = Section(mod, "Envirosuit", "Itemable");
        GameItemEditor.Apply(_game, mod, "Envirosuit", [new(itemable.Key, Field(itemable, "Weight").Path, JsonValue.Create(50))], new HashSet<string>());

        GameItemEditor.Apply(_game, mod, "Envirosuit", [new(itemable.Key, Field(itemable, "Weight").Path, null)], new HashSet<string>());

        Assert.Null(Item(mod, "Traits-D_Itemable.json", "Item_Envirosuit"));
        Assert.DoesNotContain(mod.Rows, r => r.CurrentFile == "Traits-D_Itemable.json");
    }

    [Fact]
    public void Apply_ARecipeIngredientAndAnInheritedStackSize()
    {
        var mod = EmptyMod();
        var recipe = Section(mod, "Food_Bread", $"{GameItemEditor.RecipeSection}:Bread");
        var itemable = Section(mod, "Envirosuit", "Itemable");

        GameItemEditor.Apply(_game, mod, "Food_Bread", [new(recipe.Key, recipe.Fields.First(f => f.Label.StartsWith("Needs")).Path, JsonValue.Create(3))], new HashSet<string>());
        GameItemEditor.Apply(_game, mod, "Envirosuit", [new(itemable.Key, Field(itemable, "Stack size").Path, JsonValue.Create(5))], new HashSet<string>());

        var inputs = (JsonArray)Item(mod, "Crafting-D_ProcessorRecipes.json", "Bread")!.Fields["Inputs"]!;
        Assert.Equal(3, inputs[0]!["Count"]!.GetValue<int>());
        Assert.Equal("Dough_Bread", inputs[0]!["Element"]!["RowName"]!.GetValue<string>());
        Assert.Equal(5, Item(mod, "Traits-D_Itemable.json", "Item_Envirosuit")!.Fields["MaxStack"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("(Value=\"BaseOxygenSlots_+\")", "Oxygen slots")]
    [InlineData("BaseColdResistance_%", "Cold resistance %")]
    [InlineData("BaseMovementSpeed_+%", "Movement speed +%")]
    [InlineData("BaseMovementSpeed_+", "Movement speed")]
    [InlineData("MaximumHealth_+", "Maximum health")]
    public void StatLabels_ReadPlainly(string stat, string expected) => Assert.Equal(expected, GameStats.StatLabel(stat));

    /// <summary>New mod never changes the game's own rows: a name the game already uses in any table it writes is refused.</summary>
    [Fact]
    public void ItemCloner_RefusesToWriteARowTheGameAlreadyHas()
    {
        // A template row with no item behind it: a new item called "Ghost" would land on it.
        File.WriteAllText(Path.Combine(_root, "Items", "D_ItemTemplate.json"), """
            {"RowStruct":"S","Defaults":{},"Rows":[
              {"Name":"Envirosuit","ItemStaticData":{"RowName":"Envirosuit"}},
              {"Name":"Food_Bread","ItemStaticData":{"RowName":"Food_Bread"}},
              {"Name":"Ghost","ItemStaticData":{"RowName":"Ghost"}}
            ]}
            """);
        var game = new GameTableIndex(_root);

        var ex = Assert.Throws<ArgumentException>(() => ItemCloner.Build(game, new ItemCloneRequest { SourceItem = "Envirosuit", NewName = "Ghost", DisplayName = "Ghost" }));

        Assert.Contains("already a row in the game's D_ItemTemplate", ex.Message);
    }

    /// <summary>A second food on Bread's consumable row: the buff is shared through it, and an own copy must not change the other food.</summary>
    private GameTableIndex WithToastSharingBreadsConsumable()
    {
        var statics = Path.Combine(_root, "Items", "D_ItemsStatic.json");
        File.WriteAllText(statics, File.ReadAllText(statics).Replace(
            "{\"Name\":\"Food_Bread\",",
            "{\"Name\":\"Food_Toast\",\"Itemable\":{\"RowName\":\"Item_Bread\"},\"Consumable\":{\"RowName\":\"Food_Bread\"}},\n              {\"Name\":\"Food_Bread\","));
        return new GameTableIndex(_root);
    }

    /// <summary>A review finding: the buff's "shared by" counted consumable rows, not items, so a buff two foods share read "this item only".</summary>
    [Fact]
    public void ABuff_IsSharedByEveryItemWhoseConsumableGivesIt()
    {
        var game = WithToastSharingBreadsConsumable();

        var buff = GameItemEditor.Describe(game, null, "Food_Bread")!.Sections.Single(s => s.Key == GameItemEditor.BuffSection);

        Assert.Equal(2, buff.SharedBy);
    }

    /// <summary>A review finding: the buff's own copy was hung on the SHARED consumable row, changing Toast too.</summary>
    [Fact]
    public void ABuffAsItsOwnCopy_OnASharedConsumable_GivesTheConsumableItsOwnCopyToo()
    {
        var game = WithToastSharingBreadsConsumable();
        var mod = EmptyMod();
        var buff = GameItemEditor.Describe(game, mod, "Food_Bread")!.Sections.Single(s => s.Key == GameItemEditor.BuffSection);

        GameItemEditor.Apply(game, mod, "Food_Bread", [new(buff.Key, Field(buff, "Maximum stamina").Path, JsonValue.Create(200))], new HashSet<string> { buff.Key });

        Assert.Null(Item(mod, "Traits-D_Consumable.json", "Food_Bread")); // Toast's row untouched
        var ownConsumable = Item(mod, "Traits-D_Consumable.json", "Food_Bread_Food_Bread")!;
        Assert.Equal("Bread_Food_Bread", ownConsumable.Fields["Modifier"]!["Modifier"]!["RowName"]!.GetValue<string>());
        Assert.Equal("Food_Bread_Food_Bread", Item(mod, "Items-D_ItemsStatic.json", "Food_Bread")!.Fields["Consumable"]!["RowName"]!.GetValue<string>());
        Assert.Equal("Bread", GameItemEditor.Describe(game, mod, "Food_Toast")!.Sections.Single(s => s.Key == GameItemEditor.BuffSection).RowName);
    }

    /// <summary>
    /// The buff's own copy also gives the item its own consumable row, so a consumable change made
    /// in the same Apply must land on that new row: it used to go to the game's shared row, changing
    /// Toast and not this bread.
    /// </summary>
    [Fact]
    public void ABuffOwnCopy_ThenAConsumableChange_InOneApply_BothLandOnTheItemsOwnRows()
    {
        var game = WithToastSharingBreadsConsumable();
        var mod = EmptyMod();
        var view = GameItemEditor.Describe(game, mod, "Food_Bread")!;
        var buff = view.Sections.Single(s => s.Key == GameItemEditor.BuffSection);
        var consumable = view.Sections.Single(s => s.Key == "Consumable");
        var lifetime = consumable.Fields.First(f => f.Path.Key.Contains("Lifetime", StringComparison.Ordinal)).Path;

        GameItemEditor.Apply(game, mod, "Food_Bread",
            [new(buff.Key, Field(buff, "Maximum stamina").Path, JsonValue.Create(200)), new(consumable.Key, lifetime, JsonValue.Create(1800))],
            new HashSet<string> { buff.Key });

        Assert.Null(Item(mod, "Traits-D_Consumable.json", "Food_Bread")); // Toast's row untouched
        var ownConsumable = Item(mod, "Traits-D_Consumable.json", "Food_Bread_Food_Bread")!;
        Assert.Equal("Bread_Food_Bread", ownConsumable.Fields["Modifier"]!["Modifier"]!["RowName"]!.GetValue<string>());
        Assert.Equal(1800, ownConsumable.Fields["Modifier"]!["ModifierLifetime"]!.GetValue<int>());
    }

    /// <summary>A recipe with no bench listed ("RecipeSets": []) still shows, titled without one, instead of failing the whole item.</summary>
    [Fact]
    public void ARecipeWithNoBench_IsShownWithoutOne()
    {
        var recipes = Path.Combine(_root, "Crafting", "D_ProcessorRecipes.json");
        File.WriteAllText(recipes, File.ReadAllText(recipes).Replace("\"RecipeSets\":[{\"RowName\":\"Kitchen_Stove\",\"DataTableName\":\"D_RecipeSets\"}]", "\"RecipeSets\":[]"));

        var view = GameItemEditor.Describe(new GameTableIndex(_root), null, "Food_Bread")!;

        Assert.Contains(view.Sections, s => s.Title == "Recipe");
    }

    /// <summary>A review finding: "back to the game's value" on an own copy read a row the game doesn't have, writing the table default.</summary>
    [Fact]
    public void OnAnOwnCopy_BackToTheGamesValue_UsesTheRowItWasCopiedFrom()
    {
        var mod = EmptyMod();
        var decay = Section(mod, "Envirosuit", "Decayable");
        var path = Field(decay, "Decay time (seconds)").Path;
        GameItemEditor.Apply(_game, mod, "Envirosuit", [new(decay.Key, path, JsonValue.Create(600))], new HashSet<string> { decay.Key });

        GameItemEditor.Apply(_game, mod, "Envirosuit", [new(decay.Key, path, null)], new HashSet<string>());

        Assert.Equal(300, Item(mod, "Traits-D_Decayable.json", "Decay_General_Envirosuit")!.Fields["DecayTime"]!.GetValue<int>());
    }

    /// <summary>A review finding: an item the mod points at another game row looked like "its own copy", so editing it changed everything sharing that row.</summary>
    [Fact]
    public void AnItemRepointedToAnotherGameRow_IsStillThatSharedRow()
    {
        var mod = EmptyMod();
        mod.Rows.Insert(0, new ExmodFileRow { CurrentFile = "Items-D_ItemsStatic.json", FileItems = [new ExmodFileItem { Name = "Envirosuit", Fields = { ["Armour"] = JsonNode.Parse("""{"RowName":"Decay_General"}""") } }] });
        File.WriteAllText(Path.Combine(_root, "Traits", "D_Armour.json"), """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"IO_Undersuit","ArmourStats":{"(Value=\"BaseOxygenSlots_+\")":1}},{"Name":"Decay_General","ArmourStats":{}}]}""");
        var game = new GameTableIndex(_root);

        var armour = GameItemEditor.Describe(game, mod, "Envirosuit")!.Sections.Single(s => s.Key == "Armour");

        Assert.False(armour.IsOwnCopy);
        Assert.Equal("Decay_General", armour.GameRowName);
    }

    /// <summary>A review finding: the recipe's "game value" was the game's ingredient at the same position, not the same ingredient.</summary>
    [Fact]
    public void RecipeIngredients_AreMatchedByIngredient_NotPosition()
    {
        var mod = EmptyMod();
        mod.Rows.Insert(0, new ExmodFileRow { CurrentFile = "Crafting-D_ProcessorRecipes.json", FileItems = [new ExmodFileItem { Name = "Bread", Fields =
        {
            ["Inputs"] = JsonNode.Parse("""[{"Element":{"RowName":"Fiber","DataTableName":"D_ItemsStatic"},"Count":9},{"Element":{"RowName":"Dough_Bread","DataTableName":"D_ItemsStatic"},"Count":5}]"""),
        } }] });
        var recipe = Section(mod, "Food_Bread", $"{GameItemEditor.RecipeSection}:Bread");

        var dough = recipe.Fields.Single(f => f.Label == "Needs Dough bread");
        Assert.Equal(1, dough.GameValue!.GetValue<int>());
        Assert.Null(recipe.Fields.Single(f => f.Label == "Needs Fiber").GameValue);

        GameItemEditor.Apply(_game, mod, "Food_Bread", [new(recipe.Key, dough.Path, null)], new HashSet<string>());
        var inputs = (JsonArray)Item(mod, "Crafting-D_ProcessorRecipes.json", "Bread")!.Fields["Inputs"]!;
        Assert.Equal(1, inputs[1]!["Count"]!.GetValue<int>());
        Assert.Equal(9, inputs[0]!["Count"]!.GetValue<int>());
    }

    /// <summary>Real data: Item_Purification_Pill's description has "\r\n" line breaks, which used to come out as "rn".</summary>
    [Theory]
    [InlineData("""NSLOCTEXT("D_Itemable", "Item_Pill-Description", "Cures effects.\r\n\r\nTo use it: eat.")""", "Cures effects.\r\n\r\nTo use it: eat.")]
    [InlineData("""NSLOCTEXT("D_Itemable", "Item_Envirosuit-Description", "Earth\'s largest \"employer\" \\ here")""", "Earth's largest \"employer\" \\ here")]
    public void GameText_RoundTrips(string raw, string shown)
    {
        Assert.Equal(shown, NsLocText.TextOf(raw));
        Assert.True(NsLocText.TryParse(raw, out var table, out var key, out _));
        Assert.Equal(raw, NsLocText.Make(table, key, shown));
        Assert.Equal(raw.Replace(key, "Copy-Description"), NsLocText.WithKey(raw, "Copy-Description"));
    }

    /// <summary>A review finding: the game's links don't always match its rows' case (FirePit → Firepit, 105 in D_ItemsStatic).</summary>
    [Fact]
    public void RowsAreFoundIgnoringCase_LikeTheGame()
    {
        Assert.NotNull(_game.Row("Traits-D_Armour.json", "io_undersuit"));
        Assert.Equal("IO_Undersuit", _game.GameRowName("Traits-D_Armour.json", "IO_UNDERSUIT"));
    }
}
