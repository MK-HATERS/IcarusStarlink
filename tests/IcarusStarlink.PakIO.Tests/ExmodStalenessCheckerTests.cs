using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests;

public class ExmodStalenessCheckerTests : IDisposable
{
    private readonly string _dataFolder = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
    private readonly DefaultSemanticClassifier _classifier = new();

    private void WriteBaseTable(string relativePath, string json)
    {
        var path = Path.Combine(_dataFolder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private static ExmodPackage MakePackage(params ExmodFileRow[] rows) => new()
    {
        Name = "Test Mod", Author = "A", Version = "1", Description = "D", FileName = "Test_Mod", Rows = [.. rows],
    };

    private static ExmodFileItem MakeItem(string name, int fieldCount)
    {
        var item = new ExmodFileItem { Name = name };
        for (var i = 0; i < fieldCount; i++)
        {
            item.Fields[$"Field{i}"] = JsonValue.Create(i);
        }
        return item;
    }

    [Fact]
    public void FindLikelyStaleItems_NewItemWithFewFields_IsFlagged()
    {
        WriteBaseTable("Traits/D_Fuel.json", """{"RowStruct":"S","Defaults":{},"Rows":[]}""");
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "Traits-D_Fuel.json",
            FileItems = [MakeItem("Old_Item", fieldCount: 2)],
        });

        var stale = ExmodStalenessChecker.FindLikelyStaleItems(package, _dataFolder, _classifier);

        var item = Assert.Single(stale);
        Assert.Equal("Traits-D_Fuel.json", item.CurrentFile);
        Assert.Equal("Old_Item", item.ItemName);
        Assert.Equal(2, item.FieldCount);
    }

    [Fact]
    public void FindLikelyStaleItems_NewItemWithManyFields_IsNotFlagged()
    {
        // A real new item defines many fields — this is exactly the "adds new content" case that
        // must NOT be treated as a stale edit of a removed row.
        WriteBaseTable("Traits/D_Fuel.json", """{"RowStruct":"S","Defaults":{},"Rows":[]}""");
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "Traits-D_Fuel.json",
            FileItems = [MakeItem("New_Building", fieldCount: 12)],
        });

        var stale = ExmodStalenessChecker.FindLikelyStaleItems(package, _dataFolder, _classifier);

        Assert.Empty(stale);
    }

    [Fact]
    public void FindLikelyStaleItems_ItemStillInBase_IsNotFlagged()
    {
        WriteBaseTable("Traits/D_Fuel.json", """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Item_Wood","Weight":150}]}""");
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "Traits-D_Fuel.json",
            FileItems = [new ExmodFileItem { Name = "Item_Wood", Fields = { ["Weight"] = JsonValue.Create(0) } }],
        });

        var stale = ExmodStalenessChecker.FindLikelyStaleItems(package, _dataFolder, _classifier);

        Assert.Empty(stale);
    }

    [Fact]
    public void FindLikelyStaleItems_DuplicateFieldsAcrossRepeatedItemEntries_CountedAsOneItem()
    {
        // Real EXMOD mods can list the same item name more than once in FileItems (see the field
        // notes: "A single mod's File_Items can list the SAME item name more than once, with
        // different values" — a legal, observed pattern). Both entries feed TableDiffer against the
        // same missing base row, so they must group into one StaleItem, not two.
        WriteBaseTable("Traits/D_Fuel.json", """{"RowStruct":"S","Defaults":{},"Rows":[]}""");
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "Traits-D_Fuel.json",
            FileItems =
            [
                new ExmodFileItem { Name = "Old_Item", Fields = { ["A"] = JsonValue.Create(1) } },
                new ExmodFileItem { Name = "Old_Item", Fields = { ["A"] = JsonValue.Create(2), ["B"] = JsonValue.Create(3) } },
            ],
        });

        var stale = ExmodStalenessChecker.FindLikelyStaleItems(package, _dataFolder, _classifier);

        var item = Assert.Single(stale);
        Assert.Equal("Old_Item", item.ItemName);
    }

    [Fact]
    public void FindLikelyStaleItems_ItemNameMatchesOwnBundledAsset_IsNotFlagged()
    {
        // A whole new building piece/weapon/etc. needs real compiled assets — a DataTable edit
        // alone can't create one — so a "new item" that correlates with one of the mod's own
        // bundled files is real content the author added, not a stale edit of a removed row.
        WriteBaseTable("Building/D_BuildingPieces.json", """{"RowStruct":"S","Defaults":{},"Rows":[]}""");
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "Building-D_BuildingPieces.json",
            FileItems = [MakeItem("BP_Custom_Tower", fieldCount: 1)],
        });
        var ownAssetPaths = new[] { "BP/Building/BP_Custom_Tower.uasset", "BP/Building/BP_Custom_Tower.uexp" };

        var stale = ExmodStalenessChecker.FindLikelyStaleItems(package, _dataFolder, _classifier, ownAssetPaths: ownAssetPaths);

        Assert.Empty(stale);
    }

    [Fact]
    public void FindLikelyStaleItems_ItemNameHasNoMatchingAsset_StillFlagged()
    {
        WriteBaseTable("Building/D_BuildingPieces.json", """{"RowStruct":"S","Defaults":{},"Rows":[]}""");
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "Building-D_BuildingPieces.json",
            FileItems = [MakeItem("Old_Building_Piece", fieldCount: 1)],
        });
        var ownAssetPaths = new[] { "BP/Building/BP_Unrelated_Thing.uasset" };

        var stale = ExmodStalenessChecker.FindLikelyStaleItems(package, _dataFolder, _classifier, ownAssetPaths: ownAssetPaths);

        Assert.Single(stale);
    }

    [Fact]
    public void FindLikelyStaleItems_ItemNameReorderedAcrossAssetPrefixAndSuffix_IsNotFlagged()
    {
        // Real case, traced against the user's own library: row "Reinforced_Int_Floor" correlates
        // with the real bundled asset "APEX_BLD_Floor_Iron_Reinforced_Wood_INT" — same meaningful
        // words ("reinforced"/"int"/"floor"), different order, wrapped in an unrelated category
        // prefix/suffix. A literal substring check misses this entirely.
        WriteBaseTable("Items/D_ItemTemplate.json", """{"RowStruct":"S","Defaults":{},"Rows":[]}""");
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "Items-D_ItemTemplate.json",
            FileItems = [MakeItem("Reinforced_Int_Floor", fieldCount: 1)],
        });
        var ownAssetPaths = new[] { "ASS/BLD/DES/APEX_BLD_Floor_Iron_Reinforced_Wood_INT.uasset" };

        var stale = ExmodStalenessChecker.FindLikelyStaleItems(package, _dataFolder, _classifier, ownAssetPaths: ownAssetPaths);

        Assert.Empty(stale);
    }

    [Fact]
    public void FindLikelyStaleItems_OnlyOneGenericTokenSharedWithUnrelatedAsset_StillFlagged()
    {
        // Real case, same library: row "Prop_Specimens" shares only the single generic word "prop"
        // with an unrelated bundled asset "T_ITEM_Prop_Surgical_Masks_B" from the same mod — one
        // shared token must NOT be enough to correlate, or a genuinely stale item goes undetected
        // just because the mod happens to carry an unrelated real asset with a common word in it.
        WriteBaseTable("Traits/D_Deployable.json", """{"RowStruct":"S","Defaults":{},"Rows":[]}""");
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "Traits-D_Deployable.json",
            FileItems = [MakeItem("Prop_Specimens", fieldCount: 1)],
        });
        var ownAssetPaths = new[] { "ASS/ITEM/T_ITEM_Prop_Surgical_Masks_B.uasset" };

        var stale = ExmodStalenessChecker.FindLikelyStaleItems(package, _dataFolder, _classifier, ownAssetPaths: ownAssetPaths);

        Assert.Single(stale);
    }

    [Fact]
    public void FindLikelyStaleItems_ItemNameAndAssetBothCamelCaseCompound_IsNotFlagged()
    {
        // Real case, same library: row "Petes_BeaconTeleportRemote" and its own real asset
        // "BP_Petes_BeaconTeleport" both cram multiple words into one camelCase blob with no
        // underscore between them ("BeaconTeleportRemote" / "BeaconTeleport") — a plain
        // underscore-only split leaves those as single, non-matching tokens even though this is
        // obviously a genuine custom item with its own real assets, not a stale edit.
        WriteBaseTable("Items/D_ItemTemplate.json", """{"RowStruct":"S","Defaults":{},"Rows":[]}""");
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "Items-D_ItemTemplate.json",
            FileItems = [MakeItem("Petes_BeaconTeleportRemote", fieldCount: 1)],
        });
        var ownAssetPaths = new[] { "BP/BP_Petes_BeaconTeleport.uasset" };

        var stale = ExmodStalenessChecker.FindLikelyStaleItems(package, _dataFolder, _classifier, ownAssetPaths: ownAssetPaths);

        Assert.Empty(stale);
    }

    [Fact]
    public void FindLikelyStaleItems_NoAssetPathsProvided_BehavesAsBeforeWithoutFiltering()
    {
        WriteBaseTable("Building/D_BuildingPieces.json", """{"RowStruct":"S","Defaults":{},"Rows":[]}""");
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "Building-D_BuildingPieces.json",
            FileItems = [MakeItem("BP_Custom_Tower", fieldCount: 1)],
        });

        var stale = ExmodStalenessChecker.FindLikelyStaleItems(package, _dataFolder, _classifier);

        Assert.Single(stale);
    }

    [Fact]
    public void FindLikelyStaleItems_SharedCacheAcrossCalls_StillProducesCorrectResults()
    {
        WriteBaseTable("Traits/D_Fuel.json", """{"RowStruct":"S","Defaults":{},"Rows":[]}""");
        var package = MakePackage(new ExmodFileRow
        {
            CurrentFile = "Traits-D_Fuel.json",
            FileItems = [MakeItem("Old_Item", fieldCount: 1)],
        });
        var cache = new Dictionary<string, JsonObject?>();

        var first = ExmodStalenessChecker.FindLikelyStaleItems(package, _dataFolder, _classifier, cache);
        var second = ExmodStalenessChecker.FindLikelyStaleItems(package, _dataFolder, _classifier, cache);

        Assert.Single(first);
        Assert.Single(second);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataFolder))
        {
            Directory.Delete(_dataFolder, recursive: true);
        }
    }
}
