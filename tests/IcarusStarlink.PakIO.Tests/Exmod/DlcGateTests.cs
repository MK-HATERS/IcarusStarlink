using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.DataChanges;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests.Exmod;

/// <summary>
/// A paid DLC never holds a mod back, and the merge never takes a DLC gate off either: a mod row's
/// Metadata.RequiredFeatureLevel reaches the merged table as the mod has it.
/// </summary>
public sealed class DlcGateTests : IDisposable
{
    private const string ItemsStatic = "Items-D_ItemsStatic.json";
    private readonly string _data = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "DlcGate_" + Guid.NewGuid().ToString("N"));

    public DlcGateTests()
    {
        var path = Path.Combine(_data, "Items", "D_ItemsStatic.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {"RowStruct":"S","Defaults":{"Weight":0},"Rows":[
              {"Name":"Bread","Weight":20},
              {"Name":"Hunter_Suit","Weight":30,"Metadata":{"RequiredFeatureLevel":{"RowName":"GreatHunts","DataTableName":"D_FeatureLevels"}}}
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

    private static JsonObject Gate(string level) => new() { ["RequiredFeatureLevel"] = new JsonObject { ["RowName"] = level, ["DataTableName"] = "D_FeatureLevels" } };

    private static ExmodPackage Mod(string name, params ExmodFileItem[] items) => new()
    {
        Name = name, Author = "Me", Version = "1", Description = "d", FileName = name,
        Rows = [new ExmodFileRow { CurrentFile = ItemsStatic, FileItems = [.. items] }],
    };

    [Fact]
    public void TheMerge_KeepsEveryDlcGateAModRowSets()
    {
        // A whole copy of a DLC row with one change, a new row copied from it, and a gate put on a row that had none.
        var copy = Mod("Copy",
            new ExmodFileItem { Name = "Hunter_Suit", Fields = { ["Weight"] = 10, ["Metadata"] = Gate("GreatHunts") } },
            new ExmodFileItem { Name = "Hunter_Suit_Light", Fields = { ["Weight"] = 5, ["Metadata"] = Gate("GreatHunts") } });
        var gated = Mod("Gated", new ExmodFileItem { Name = "Bread", Fields = { ["Metadata"] = Gate("NewFrontiers") } });
        var classifier = new DefaultSemanticClassifier();
        var report = new MergeReport();
        var baseTables = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase)
        {
            [ItemsStatic] = BaseDataFileReader.ReadKeyedTable(_data, ItemsStatic, report)!,
        };

        var resolved = MergeEngine.Merge(
            [ExmodFieldChangeMapper.ToFieldChanges(copy, classifier), ExmodFieldChangeMapper.ToFieldChanges(gated, classifier)],
            new MergeRuleRegistry(), null, baseTables);
        var merged = MultiFileMerger.Apply(baseTables, resolved, report)[ItemsStatic];

        Assert.Equal("GreatHunts", merged["Hunter_Suit"]!["Metadata"]!["RequiredFeatureLevel"]!["RowName"]!.GetValue<string>());
        Assert.Equal(10, merged["Hunter_Suit"]!["Weight"]!.GetValue<int>());
        Assert.Equal("GreatHunts", merged["Hunter_Suit_Light"]!["Metadata"]!["RequiredFeatureLevel"]!["RowName"]!.GetValue<string>());
        Assert.Equal("NewFrontiers", merged["Bread"]!["Metadata"]!["RequiredFeatureLevel"]!["RowName"]!.GetValue<string>());
    }
}
