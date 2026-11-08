using IcarusStarlink.Core.Settings;
using IcarusStarlink.PakIO.Assets;

namespace IcarusStarlink.PakIO.Tests.Assets;

/// <summary>
/// Checks against a real ICARUS install, run only when ICARUSSTARLINK_TEST_GAME_CONTENT names its
/// Content folder (e.g. E:\SteamLibrary\steamapps\common\Icarus\Icarus\Content). Without it each
/// test returns at once: the repo has no game files.
/// </summary>
public sealed class RealGameContentIntegrationTests : IDisposable
{
    private const string GameContentVariable = "ICARUSSTARLINK_TEST_GAME_CONTENT";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "RealGame_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// The game's paks and a mod folder are read by two different readers; a byte-identical copy of
    /// a game blueprint must get the same function fingerprints from both, or every mod copy would
    /// read as "changes N functions".
    /// </summary>
    [Fact]
    public void FunctionFingerprints_AreTheSameFromTheGameAndFromAModFolder()
    {
        if (Environment.GetEnvironmentVariable(GameContentVariable) is not { Length: > 0 } content || !Directory.Exists(content))
        {
            return;
        }

        const string Blueprint = "BP/Player/BP_IcarusPlayerCharacterSurvival.uasset";
        var game = new CueBaseGameContentProvider(new FakeSettingsService(new AppSettings { IcarusContentPath = content }));
        var files = game.TryReadPackageFiles(Blueprint);
        Assert.NotNull(files);
        var modFolder = Path.Combine(_root, "Mod");
        var target = Path.Combine(modFolder, Blueprint.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        foreach (var (extension, bytes) in files)
        {
            File.WriteAllBytes(Path.ChangeExtension(target, extension), bytes);
        }

        var fromGame = game.TrySummarizeBlueprint(Blueprint);
        var fromMod = ModBlueprintReader.TrySummarize(modFolder, Blueprint);

        Assert.NotNull(fromGame);
        Assert.NotNull(fromMod);
        Assert.NotEmpty(fromGame.Functions);
        Assert.Contains(fromGame.Functions.Values, f => f.StartsWith("v2:", StringComparison.Ordinal));
        Assert.Equal(fromGame.Functions.OrderBy(f => f.Key, StringComparer.Ordinal), fromMod.Functions.OrderBy(f => f.Key, StringComparer.Ordinal));
    }

    /// <summary>
    /// CurveAssetWriter checks its output with UAssetAPI, the library that wrote it; the game reads
    /// it with the engine. CUE4Parse is the closer stand-in: it must read back exactly the keys written.
    /// </summary>
    [Fact]
    public void WrittenGrowthCurve_ReadsBackWithCue4Parse()
    {
        if (Environment.GetEnvironmentVariable(GameContentVariable) is not { Length: > 0 } content || !Directory.Exists(content))
        {
            return;
        }

        var game = new CueBaseGameContentProvider(new FakeSettingsService(new AppSettings { IcarusContentPath = content }));
        var template = game.TryReadPackageFiles("Data/Character/C_PlayerTalentGrowth.uasset");
        Assert.NotNull(template);
        (float Time, float Value)[] keys = [(1, 0), (60, 60), (500, 1000)];

        var written = CurveAssetWriter.Write(template[".uasset"], template[".uexp"], keys, "/Game/Starlink/Test/C_Starlink_TestGrowth");

        var folder = Path.Combine(_root, "Curve");
        var asset = Path.Combine(folder, "Starlink", "Test", "C_Starlink_TestGrowth.uasset");
        Directory.CreateDirectory(Path.GetDirectoryName(asset)!);
        File.WriteAllBytes(asset, written.Asset);
        File.WriteAllBytes(Path.ChangeExtension(asset, ".uexp"), written.Exports);

        var readBack = CueAssetProviderLocator.TryLoadExportAndProject<CUE4Parse.UE4.Assets.Exports.UObject, GrowthCurve>(
            folder, "Starlink/Test/C_Starlink_TestGrowth.uasset", c => GrowthCurve.From(c)!);

        Assert.NotNull(readBack);
        Assert.Equal(keys, readBack.Keys.Select(k => (k.Time, k.Value)));
    }

    private sealed class FakeSettingsService(AppSettings settings) : ISettingsService
    {
        public AppSettings Current { get; } = settings;

        public bool Save() => true;
    }
}
