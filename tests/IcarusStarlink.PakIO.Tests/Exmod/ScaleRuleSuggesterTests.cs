using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests.Exmod;

/// <summary>
/// "Suggest rules from this mod": a hand-made mod that changes one field the same way on many rows
/// (Deyvid's Balanced Storage Capacity x2: StartingSlots ×2 on 22 containers, 120 on 19 big ones)
/// becomes Scale rules that write back exactly what it has, and catch new rows after an update.
/// </summary>
public sealed class ScaleRuleSuggesterTests : IDisposable
{
    private const string Inventory = "Inventory-D_InventoryInfo.json";
    private const string Itemable = "Traits-D_Itemable.json";

    private readonly string _data = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "ScaleSuggest_" + Guid.NewGuid().ToString("N"));

    public ScaleRuleSuggesterTests()
    {
        Write("Inventory/D_InventoryInfo.json", """
            {"RowStruct":"S","Defaults":{"StartingSlots":0},"Rows":[
              {"Name":"Storage_Wood_Crate_Small","StartingSlots":10},
              {"Name":"Storage_Wood_Crate_Medium","StartingSlots":20},
              {"Name":"Storage_Metal_Crate_Medium","StartingSlots":25},
              {"Name":"Storage_Wood_Cupboard","StartingSlots":40},
              {"Name":"Storage_Metal_Cupboard","StartingSlots":50},
              {"Name":"Deep_Freeze","StartingSlots":24},
              {"Name":"Quickbar","StartingSlots":12},
              {"Name":"Backpack","StartingSlots":24},
              {"Name":"Generator_Storage","StartingSlots":5}
            ]}
            """);
        Write("Traits/D_Itemable.json", """
            {"RowStruct":"S","Defaults":{"MaxStack":1,"Weight":100},"Rows":[
              {"Name":"Item_Fiber","MaxStack":100,"Weight":7},
              {"Name":"Item_Stone","MaxStack":50,"Weight":9},
              {"Name":"Item_Wood","MaxStack":100,"Weight":11},
              {"Name":"Item_Stick","MaxStack":20,"Weight":13},
              {"Name":"Item_NewOre","MaxStack":40},
              {"Name":"Item_Axe"}
            ]}
            """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private void Write(string relative, string json)
    {
        var path = Path.Combine(_data, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private GameTableIndex Game() => new(_data);

    private static ExmodPackage Mod(string file, string field, params (string Row, int Value)[] rows) => new()
    {
        Name = "M", Author = "Me", Version = "1.0", Description = "", FileName = "M",
        Rows =
        [
            new ExmodFileRow
            {
                CurrentFile = file,
                FileItems = [.. rows.Select(r => new ExmodFileItem { Name = r.Row, Fields = { [field] = JsonValue.Create(r.Value) } })],
            },
            new ExmodFileRow { CurrentFile = "EndOfMod" },
        ],
    };

    /// <summary>The real storage mod's shape: ×2 on the crates, a set value on big ones, the player's own inventories left alone.</summary>
    [Fact]
    public void StorageMod_TwoPatterns_BecomeTwoRules_NarrowedByName_ThatNeverTouchTheRowsTheModLeavesAlone()
    {
        var mod = Mod(Inventory, "StartingSlots",
            ("Storage_Wood_Crate_Small", 20), ("Storage_Wood_Crate_Medium", 40), ("Storage_Metal_Crate_Medium", 50),
            ("Storage_Wood_Cupboard", 120), ("Storage_Metal_Cupboard", 120), ("Deep_Freeze", 120));

        var suggestions = ScaleRuleSuggester.Suggest(mod, Game());

        Assert.Equal(2, suggestions.Count);
        var doubled = suggestions.Single(s => s.Rule.Operation == ScaleOperation.Multiply);
        Assert.Equal(2, doubled.Rule.Value);
        Assert.Equal("StartingSlots", doubled.Rule.Field);
        Assert.Equal("Crate", doubled.Rule.RowFilter);
        Assert.False(doubled.Rule.IncludeInherited);
        Assert.Equal(3, doubled.RowsCovered);
        Assert.Equal(6, doubled.RowsWithField);
        Assert.Equal("Slots ×2", doubled.Rule.Label);

        var set = suggestions.Single(s => s.Rule.Operation == ScaleOperation.Set);
        Assert.Equal(120, set.Rule.Value);
        Assert.Equal(3, set.RowsCovered);
        // "Storage" would also catch Generator_Storage, which the mod leaves at 5.
        Assert.DoesNotContain("Storage", set.Rule.RowFilter!.Split(", "));
        Assert.Contains("Cupboard", set.Rule.RowFilter!.Split(", "));
    }

    /// <summary>The point of the feature: turned into rules, the mod writes exactly what it had; nothing else changes.</summary>
    [Fact]
    public void SuggestedRules_AppliedToTheMod_WriteBackExactlyWhatItHad()
    {
        var mod = Mod(Inventory, "StartingSlots",
            ("Storage_Wood_Crate_Small", 20), ("Storage_Wood_Crate_Medium", 40), ("Storage_Metal_Crate_Medium", 50),
            ("Storage_Wood_Cupboard", 120), ("Storage_Metal_Cupboard", 120), ("Deep_Freeze", 120),
            ("Quickbar", 13), ("My_Own_Crate", 99));
        var before = Values(mod);

        var rules = ScaleRuleSuggester.Suggest(mod, Game()).Select(s => s.Rule).ToList();
        ScaleRules.ApplyToPackage(mod, rules, _data, new MergeReport(), Game());

        Assert.Equal(2, mod.ScaleRules.Count);
        Assert.Equal(before, Values(mod));
    }

    /// <summary>A stack mod made before a game update: the rule covers the whole table and says how many new rows it would also change.</summary>
    [Fact]
    public void MostRowsOfTheTable_ARuleForTheWholeTable_CountsTheRowsTheModMissed()
    {
        var mod = Mod(Itemable, "MaxStack", ("Item_Fiber", 1000), ("Item_Stone", 500), ("Item_Wood", 1000), ("Item_Stick", 200));

        var suggestion = Assert.Single(ScaleRuleSuggester.Suggest(mod, Game()));

        Assert.Equal(ScaleOperation.Multiply, suggestion.Rule.Operation);
        Assert.Equal(10, suggestion.Rule.Value);
        Assert.Null(suggestion.Rule.RowFilter);
        // Tools inherit a stack of 1: the mod didn't stack them, and the rule mustn't either.
        Assert.False(suggestion.Rule.IncludeInherited);
        Assert.Equal(4, suggestion.RowsCovered);
        Assert.Equal(1, suggestion.ExtraGameRows); // Item_NewOre
        Assert.Contains("1 game row(s) this mod doesn't change yet", suggestion.Text);
    }

    /// <summary>Whole numbers are rounded the way Apply rounds them (7 × 1.5 is 11), so the factor is still found.</summary>
    [Fact]
    public void RoundedWholeNumbers_StillShowTheirFactor()
    {
        var mod = Mod(Itemable, "Weight", ("Item_Fiber", 11), ("Item_Stone", 14), ("Item_Wood", 17), ("Item_Stick", 20));
        Write("Traits/D_Itemable.json", """
            {"RowStruct":"S","Defaults":{"Weight":100},"Rows":[
              {"Name":"Item_Fiber","Weight":7},{"Name":"Item_Stone","Weight":9},{"Name":"Item_Wood","Weight":11},{"Name":"Item_Stick","Weight":13}
            ]}
            """);

        var suggestion = Assert.Single(ScaleRuleSuggester.Suggest(mod, Game()));

        Assert.Equal(1.5, suggestion.Rule.Value);
        Assert.Equal(4, suggestion.RowsCovered);
    }

    [Fact]
    public void TooFewRows_OrNoPattern_SuggestNothing()
    {
        Assert.Empty(ScaleRuleSuggester.Suggest(Mod(Itemable, "MaxStack", ("Item_Fiber", 1000), ("Item_Stone", 500)), Game()));
        Assert.Empty(ScaleRuleSuggester.Suggest(Mod(Itemable, "MaxStack", ("Item_Fiber", 101), ("Item_Stone", 77), ("Item_Wood", 3), ("Item_Stick", 64)), Game()));
    }

    /// <summary>Rows the mod adds itself aren't game rows: they never count, whatever their value.</summary>
    [Fact]
    public void RowsTheModAdds_AreNeverJudged()
    {
        Assert.Empty(ScaleRuleSuggester.Suggest(Mod(Itemable, "MaxStack", ("My_Item_A", 10), ("My_Item_B", 10), ("My_Item_C", 10)), Game()));
    }

    [Fact]
    public void AFieldARuleAlreadyOwns_IsNotSuggestedAgain()
    {
        var mod = Mod(Itemable, "MaxStack", ("Item_Fiber", 1000), ("Item_Stone", 500), ("Item_Wood", 1000), ("Item_Stick", 200));
        mod.ScaleRules = [new ScaleRule { CurrentFile = Itemable, Field = "MaxStack", Value = 10 }];

        Assert.Empty(ScaleRuleSuggester.Suggest(mod, Game()));
    }

    /// <summary>Every value in the mod by file, row and field (Apply may reorder rows; the game doesn't care).</summary>
    private static string Values(ExmodPackage mod) => string.Join("; ", mod.Rows
        .SelectMany(r => r.FileItems.SelectMany(i => i.Fields.Select(f => $"{r.CurrentFile}/{i.Name}/{f.Key}={(ScaleRules.TryReadNumber(f.Value, out var n) ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : f.Value?.ToJsonString())}")))
        .Order(StringComparer.Ordinal));
}
