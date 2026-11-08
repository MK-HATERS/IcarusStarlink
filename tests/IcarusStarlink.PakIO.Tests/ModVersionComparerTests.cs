using System.Text.Json.Nodes;
using IcarusStarlink.PakIO.Compare;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests;

public sealed class ModVersionComparerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests", $"ModVersion_{Guid.NewGuid():N}");
    private readonly string _oldFolder;
    private readonly string _newFolder;
    private readonly ModVersionComparer _comparer = new(new ThrowingPakCompareService());

    public ModVersionComparerTests()
    {
        _oldFolder = Path.Combine(_root, "old");
        _newFolder = Path.Combine(_root, "new");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static ExmodPackageContents BuildMod(string version, params (string CurrentFile, string ItemName, string FieldName, int Value)[] changes)
    {
        var rows = changes
            .GroupBy(c => c.CurrentFile)
            .Select(fileGroup => new ExmodFileRow
            {
                CurrentFile = fileGroup.Key,
                FileItems = [.. fileGroup.GroupBy(c => c.ItemName).Select(itemGroup => new ExmodFileItem
                {
                    Name = itemGroup.Key,
                    Fields = itemGroup.ToDictionary(c => c.FieldName, c => (JsonNode?)JsonValue.Create(c.Value)),
                })],
            })
            .ToList();

        return new ExmodPackageContents(
            new ExmodPackage
            {
                Name = "Test Mod", Author = "Author", Version = version, Description = "d", FileName = "Test_Mod", Rows = rows,
            },
            []);
    }

    private Task<ModVersionCompareResult> CompareAsync() => _comparer.CompareAsync(_oldFolder, _newFolder, unrealPakExePath: null);

    [Fact]
    public async Task IdenticalVersions_ReportNoDifferences()
    {
        ExmodFolder.Write(_oldFolder, BuildMod("1.0", ("Traits-D_Fuel.json", "Composter", "Rate", 10)));
        ExmodFolder.Write(_newFolder, BuildMod("1.0", ("Traits-D_Fuel.json", "Composter", "Rate", 10)));

        var result = await CompareAsync();

        Assert.True(result.IsIdentical);
        Assert.Empty(result.DataDifferences);
    }

    [Fact]
    public async Task ChangedFieldValue_ReportsBothVersionsValues()
    {
        ExmodFolder.Write(_oldFolder, BuildMod("1.0", ("Traits-D_Fuel.json", "Composter", "Rate", 10)));
        ExmodFolder.Write(_newFolder, BuildMod("1.1", ("Traits-D_Fuel.json", "Composter", "Rate", 25)));

        var result = await CompareAsync();

        Assert.Equal("v1.0", result.OldLabel);
        Assert.Equal("v1.1", result.NewLabel);
        var file = Assert.Single(result.DataDifferences);
        var change = Assert.Single(file.FieldChanges);
        Assert.Equal("Composter", change.ItemName);
        Assert.Equal("10", change.OriginalValue!.ToJsonString());
        Assert.Equal("25", change.NewValue!.ToJsonString());
    }

    [Fact]
    public async Task ItemTheNewVersionAdds_ReportedAsNewItem()
    {
        ExmodFolder.Write(_oldFolder, BuildMod("1.0", ("Traits-D_Fuel.json", "Composter", "Rate", 10)));
        ExmodFolder.Write(_newFolder, BuildMod("1.1",
            ("Traits-D_Fuel.json", "Composter", "Rate", 10),
            ("Traits-D_Fuel.json", "Generator", "Rate", 5)));

        var result = await CompareAsync();

        var file = Assert.Single(result.DataDifferences);
        var change = Assert.Single(file.FieldChanges);
        Assert.Equal("Generator", change.ItemName);
        Assert.True(change.IsNewItem);
    }

    [Fact]
    public async Task ItemTheNewVersionDrops_ReportedAsRemovedRow()
    {
        ExmodFolder.Write(_oldFolder, BuildMod("1.0",
            ("Traits-D_Fuel.json", "Composter", "Rate", 10),
            ("Traits-D_Fuel.json", "Generator", "Rate", 5)));
        ExmodFolder.Write(_newFolder, BuildMod("1.1", ("Traits-D_Fuel.json", "Composter", "Rate", 10)));

        var result = await CompareAsync();

        var file = Assert.Single(result.DataDifferences);
        Assert.Equal("Generator", Assert.Single(file.RemovedRowNames));
    }

    [Fact]
    public async Task WholeFileTheNewVersionAdds_ReportedAsNewFile()
    {
        ExmodFolder.Write(_oldFolder, BuildMod("1.0", ("Traits-D_Fuel.json", "Composter", "Rate", 10)));
        ExmodFolder.Write(_newFolder, BuildMod("2.0",
            ("Traits-D_Fuel.json", "Composter", "Rate", 10),
            ("Crafting-D_Recipes.json", "Axe", "Cost", 3)));

        var result = await CompareAsync();

        var file = Assert.Single(result.DataDifferences);
        Assert.True(file.IsNewFile);
        Assert.Equal("Crafting-D_Recipes.json", file.RelativePath);
    }

    [Fact]
    public async Task ChangedAssetFile_ReportedEvenWhenNoFieldChanged()
    {
        // A retextured mesh with identical field values is a real update — the author shipped
        // something different, and a data-only diff would report "nothing changed".
        var withAsset = BuildMod("1.0", ("Traits-D_Fuel.json", "Composter", "Rate", 10));
        ExmodFolder.Write(_oldFolder, new ExmodPackageContents(withAsset.Package, [new ExmodAssetEntry("BP/Thing.uasset", "old bytes"u8.ToArray())]));
        ExmodFolder.Write(_newFolder, new ExmodPackageContents(withAsset.Package, [new ExmodAssetEntry("BP/Thing.uasset", "new bytes!!"u8.ToArray())]));

        var result = await CompareAsync();

        Assert.Empty(result.DataDifferences);
        Assert.False(result.IsIdentical);
        var asset = Assert.Single(result.AssetDifferences);
        Assert.Equal("BP/Thing.uasset", asset.RelativePath);
        Assert.Equal(PakAssetDifferenceKind.DifferentContent, asset.Kind);
    }

    [Fact]
    public async Task MissingPreviousVersionFolder_ThrowsClearly()
    {
        ExmodFolder.Write(_newFolder, BuildMod("1.1", ("Traits-D_Fuel.json", "Composter", "Rate", 10)));

        var ex = await Assert.ThrowsAsync<DirectoryNotFoundException>(CompareAsync);
        Assert.Contains("no previous version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpaquePakMod_WithoutUnrealPak_ExplainsWhatsMissing()
    {
        // Both sides are prebuilt paks with no readable package — the comparison genuinely needs
        // UnrealPak, and saying so beats silently reporting "no differences".
        Directory.CreateDirectory(_oldFolder);
        Directory.CreateDirectory(_newFolder);
        File.WriteAllText(Path.Combine(_oldFolder, "mod_P.pak"), "old pak bytes");
        File.WriteAllText(Path.Combine(_newFolder, "mod_P.pak"), "new pak bytes");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(CompareAsync);
        Assert.Contains("UnrealPak", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void WritePaks(string folder, params string[] names)
    {
        Directory.CreateDirectory(folder);
        foreach (var name in names)
        {
            var path = Path.Combine(folder, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, name);
        }
    }

    /// <summary>
    /// Regression: only the first pak of a bundle was compared, so an update that changed only
    /// Mod_Model_P.pak read as "identical to its previous version".
    /// </summary>
    [Fact]
    public async Task Bundle_ChangeInSecondPakOnly_IsReported()
    {
        WritePaks(_oldFolder, "A_P.pak", "B_P.pak");
        WritePaks(_newFolder, "A_P.pak", "B_P.pak");
        var paks = new RecordingPakCompareService();

        var result = await new ModVersionComparer(paks).CompareAsync(_oldFolder, _newFolder, "UnrealPak.exe");

        Assert.False(result.IsIdentical);
        Assert.Contains(paks.Pairs, p => p.First.EndsWith("B_P.pak", StringComparison.Ordinal) && p.Second.EndsWith("B_P.pak", StringComparison.Ordinal));
        Assert.Contains(paks.Pairs, p => p.First.EndsWith("A_P.pak", StringComparison.Ordinal) && p.Second.EndsWith("A_P.pak", StringComparison.Ordinal));
        var difference = Assert.Single(result.AssetDifferences);
        Assert.Equal("B_P.pak: x.uasset", difference.RelativePath);
        Assert.Equal("previous version (2 paks)", result.OldLabel);
        Assert.Equal("this version (2 paks)", result.NewLabel);
    }

    [Fact]
    public async Task Bundle_PakAddedInNewVersion_IsReported()
    {
        WritePaks(_oldFolder, "A_P.pak");
        WritePaks(_newFolder, "A_P.pak", "B_P.pak");
        var paks = new RecordingPakCompareService();

        var result = await new ModVersionComparer(paks).CompareAsync(_oldFolder, _newFolder, "UnrealPak.exe");

        Assert.Contains(new PakAssetDifference("B_P.pak", PakAssetDifferenceKind.OnlyInSecond), result.AssetDifferences);
        Assert.Equal("previous version (1 pak)", result.OldLabel);
        var pair = Assert.Single(paks.Pairs);
        Assert.EndsWith("A_P.pak", pair.First, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bundle_PaksInSubfolders_ArePairedByTheirPlace()
    {
        WritePaks(_oldFolder, Path.Combine("Paks", "A_P.pak"), Path.Combine("Paks", "B_P.pak"), "C_P.pak");
        WritePaks(_newFolder, Path.Combine("Paks", "B_P.pak"), "C_P.pak");
        var paks = new RecordingPakCompareService();

        var result = await new ModVersionComparer(paks).CompareAsync(_oldFolder, _newFolder, "UnrealPak.exe");

        Assert.Contains(new PakAssetDifference("Paks/A_P.pak", PakAssetDifferenceKind.OnlyInFirst), result.AssetDifferences);
        Assert.Contains(new PakAssetDifference("Paks/B_P.pak: x.uasset", PakAssetDifferenceKind.DifferentContent), result.AssetDifferences);
        Assert.Equal(2, paks.Pairs.Count);
    }

    [Fact]
    public async Task SinglePakRenamed_StillComparesTheTwo()
    {
        WritePaks(_oldFolder, "v1_P.pak");
        WritePaks(_newFolder, "v2_P.pak");
        var paks = new RecordingPakCompareService();

        var result = await new ModVersionComparer(paks).CompareAsync(_oldFolder, _newFolder, "UnrealPak.exe");

        var pair = Assert.Single(paks.Pairs);
        Assert.EndsWith("v1_P.pak", pair.First, StringComparison.Ordinal);
        Assert.EndsWith("v2_P.pak", pair.Second, StringComparison.Ordinal);
        Assert.Equal("v1_P.pak", result.OldLabel);
        Assert.Equal("v2_P.pak", result.NewLabel);
    }

    /// <summary>Records each pair it's asked about; only a pair whose first pak is B_P.pak differs (one asset).</summary>
    private sealed class RecordingPakCompareService : IPakCompareService
    {
        public List<(string First, string Second)> Pairs { get; } = [];

        public Task<PakCompareResult> CompareAsync(string unrealPakExePath, string firstPakPath, string secondPakPath, CancellationToken cancellationToken = default)
        {
            lock (Pairs)
            {
                Pairs.Add((firstPakPath, secondPakPath));
            }

            IReadOnlyList<PakAssetDifference> differences = firstPakPath.EndsWith("B_P.pak", StringComparison.Ordinal)
                ? [new PakAssetDifference("x.uasset", PakAssetDifferenceKind.DifferentContent)]
                : [];
            return Task.FromResult(new PakCompareResult([], differences, 1, 1));
        }
    }

    private sealed class ThrowingPakCompareService : IPakCompareService
    {
        public Task<PakCompareResult> CompareAsync(string unrealPakExePath, string firstPakPath, string secondPakPath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("The EXMOD path must never reach the pak comparer.");
    }
}
