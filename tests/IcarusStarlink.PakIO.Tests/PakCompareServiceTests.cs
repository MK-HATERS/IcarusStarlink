using IcarusStarlink.PakIO.Compare;
using IcarusStarlink.PakIO.DataChanges;
using IcarusStarlink.PakIO.Pak;

namespace IcarusStarlink.PakIO.Tests;

public sealed class PakCompareServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests", $"PakCompare_{Guid.NewGuid():N}");
    private readonly string _firstSource;
    private readonly string _secondSource;
    private readonly PakCompareService _service;

    public PakCompareServiceTests()
    {
        _firstSource = Path.Combine(_root, "first_source");
        _secondSource = Path.Combine(_root, "second_source");
        Directory.CreateDirectory(_firstSource);
        Directory.CreateDirectory(_secondSource);
        _service = new PakCompareService(new FakeUnrealPakService(new Dictionary<string, string>
        {
            ["first.pak"] = _firstSource,
            ["second.pak"] = _secondSource,
        }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void Write(string side, string relativePath, string content)
    {
        var path = Path.Combine(side == "first" ? _firstSource : _secondSource, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private Task<PakCompareResult> CompareAsync() => _service.CompareAsync("unused.exe", "first.pak", "second.pak");

    private static string Table(params (string Name, int Rate)[] rows)
    {
        var rowJson = string.Join(",", rows.Select(r => $"{{\"Name\":\"{r.Name}\",\"Rate\":{r.Rate}}}"));
        return $"{{\"RowStruct\":\"/Script/Icarus.Test\",\"Defaults\":{{}},\"Rows\":[{rowJson}]}}";
    }

    [Fact]
    public async Task IdenticalPaks_ReportNoDifferences()
    {
        Write("first", "data/Traits/D_Fuel.json", Table(("Composter", 10)));
        Write("second", "data/Traits/D_Fuel.json", Table(("Composter", 10)));
        Write("first", "BP/Thing.uasset", "binary-bytes");
        Write("second", "BP/Thing.uasset", "binary-bytes");

        var result = await CompareAsync();

        Assert.Empty(result.DataDifferences);
        Assert.Empty(result.AssetDifferences);
        Assert.Equal(2, result.FirstFileCount);
        Assert.Equal(2, result.SecondFileCount);
    }

    [Fact]
    public async Task EquivalentTableWithDifferentFormatting_ReportsNoDifference()
    {
        // Same rows/fields, different whitespace and field order — the whole point of diffing at
        // table level instead of byte level.
        Write("first", "data/Traits/D_Fuel.json", "{\"RowStruct\":\"x\",\"Rows\":[{\"Name\":\"Composter\",\"Rate\":10,\"Flow\":\"Produce\"}]}");
        Write("second", "data/Traits/D_Fuel.json", "{ \"RowStruct\": \"x\", \"Rows\": [ { \"Flow\": \"Produce\", \"Rate\": 10, \"Name\": \"Composter\" } ] }");

        var result = await CompareAsync();

        Assert.Empty(result.DataDifferences);
        Assert.Empty(result.AssetDifferences);
    }

    [Fact]
    public async Task ChangedRowField_ReportedWithBothValues()
    {
        Write("first", "data/Traits/D_Fuel.json", Table(("Composter", 10)));
        Write("second", "data/Traits/D_Fuel.json", Table(("Composter", 77)));

        var result = await CompareAsync();

        var file = Assert.Single(result.DataDifferences);
        Assert.Equal("data/Traits/D_Fuel.json", file.RelativePath);
        var change = Assert.Single(file.FieldChanges);
        Assert.Equal("Composter", change.ItemName);
        Assert.Equal("Rate", change.FieldName);
        Assert.Equal("10", change.OriginalValue!.ToJsonString());
        Assert.Equal("77", change.NewValue!.ToJsonString());
    }

    [Fact]
    public async Task RowOnlyInFirst_ReportedAsRemovedRow()
    {
        Write("first", "data/Traits/D_Fuel.json", Table(("Composter", 10), ("Generator", 10)));
        Write("second", "data/Traits/D_Fuel.json", Table(("Composter", 10)));

        var result = await CompareAsync();

        var file = Assert.Single(result.DataDifferences);
        Assert.Equal("Generator", Assert.Single(file.RemovedRowNames));
        Assert.Empty(file.FieldChanges);
    }

    [Fact]
    public async Task TableFileOnlyInSecond_ReportedAsNewFileWithItsRows()
    {
        Write("first", "data/Traits/D_Fuel.json", Table(("Composter", 10)));
        Write("second", "data/Traits/D_Fuel.json", Table(("Composter", 10)));
        Write("second", "data/Crafting/D_Recipes.json", Table(("Axe", 5)));

        var result = await CompareAsync();

        var file = Assert.Single(result.DataDifferences);
        Assert.True(file.IsNewFile);
        Assert.Equal("data/Crafting/D_Recipes.json", file.RelativePath);
        var change = Assert.Single(file.FieldChanges);
        Assert.Equal("Axe", change.ItemName);
    }

    [Fact]
    public async Task TableFileOnlyInFirst_ReportedAsRemovedFileWithRowNames()
    {
        Write("first", "data/Crafting/D_Recipes.json", Table(("Axe", 5)));

        var result = await CompareAsync();

        var file = Assert.Single(result.DataDifferences);
        Assert.True(file.IsRemovedFile);
        Assert.Equal("Axe", Assert.Single(file.RemovedRowNames));
    }

    [Fact]
    public async Task BinaryAssetDifferences_ReportedByKind()
    {
        Write("first", "BP/Changed.uasset", "old-bytes");
        Write("second", "BP/Changed.uasset", "new-bytes!");
        Write("first", "BP/FirstOnly.uasset", "x");
        Write("second", "BP/SecondOnly.uasset", "y");

        var result = await CompareAsync();

        Assert.Empty(result.DataDifferences);
        Assert.Equal(3, result.AssetDifferences.Count);
        Assert.Equal(PakAssetDifferenceKind.DifferentContent, result.AssetDifferences.Single(a => a.RelativePath == "BP/Changed.uasset").Kind);
        Assert.Equal(PakAssetDifferenceKind.OnlyInFirst, result.AssetDifferences.Single(a => a.RelativePath == "BP/FirstOnly.uasset").Kind);
        Assert.Equal(PakAssetDifferenceKind.OnlyInSecond, result.AssetDifferences.Single(a => a.RelativePath == "BP/SecondOnly.uasset").Kind);
    }

    [Fact]
    public async Task NonDataTableJson_ComparedAsRawContentNotSilentlyEqual()
    {
        // RowsToKeyedObject maps a JSON with no "Rows" array to an empty table — if these went
        // down the table-diff path, two genuinely different files would compare as identical.
        Write("first", "config/settings.json", "{\"foo\":1}");
        Write("second", "config/settings.json", "{\"foo\":2}");

        var result = await CompareAsync();

        Assert.Empty(result.DataDifferences);
        var asset = Assert.Single(result.AssetDifferences);
        Assert.Equal(PakAssetDifferenceKind.DifferentContent, asset.Kind);
    }

    [Fact]
    public async Task DuplicateJsonKey_FallsBackToRawComparison()
    {
        // Seen in a real classic-IMM merged pak: one row object carrying the same property name
        // twice. JsonNode throws ArgumentException (not JsonException) on materializing that —
        // the file must fall back to raw content comparison, not kill the whole compare.
        const string duplicated = "{\"Rows\":[{\"Name\":\"X\",\"ResourceCostMultipliers\":1,\"ResourceCostMultipliers\":2}]}";
        Write("first", "data/Dup.json", duplicated);
        Write("second", "data/Dup.json", duplicated);
        Write("first", "data/Traits/D_Fuel.json", Table(("Composter", 10)));
        Write("second", "data/Traits/D_Fuel.json", Table(("Composter", 77)));

        var result = await CompareAsync();

        // The duplicate-key file is byte-identical, so nothing is reported for it — and the rest
        // of the compare still ran.
        Assert.Empty(result.AssetDifferences);
        var file = Assert.Single(result.DataDifferences);
        Assert.Equal("data/Traits/D_Fuel.json", file.RelativePath);
    }

    [Fact]
    public async Task MalformedJson_FallsBackToRawComparison()
    {
        Write("first", "data/Broken.json", "{not valid json");
        Write("second", "data/Broken.json", "{not valid json");

        var result = await CompareAsync();

        Assert.Empty(result.DataDifferences);
        Assert.Empty(result.AssetDifferences);
    }

    private sealed class FakeUnrealPakService(IReadOnlyDictionary<string, string> pakToSourceFolder) : IUnrealPakService
    {
        public Task<int> ExtractPakAsync(string unrealPakExePath, string pakFilePath, string outputDirectory, CancellationToken cancellationToken = default)
        {
            var source = pakToSourceFolder[pakFilePath];
            var count = 0;
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(outputDirectory, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
                count++;
            }

            return Task.FromResult(count);
        }

        public Task<UnrealPakExtractResult> ExtractDataPakAsync(string unrealPakExePath, string icarusContentPath, string outputDirectory, DateTimeOffset? previousUpdateAt, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<string?> TryGetDataPakHashAsync(string icarusContentPath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> CreatePakAsync(string unrealPakExePath, string stagingDirectory, string outputPakPath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListPakContentsAsync(string unrealPakExePath, string pakFilePath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PakVerifyResult> VerifyPakAsync(string unrealPakExePath, string pakFilePath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
