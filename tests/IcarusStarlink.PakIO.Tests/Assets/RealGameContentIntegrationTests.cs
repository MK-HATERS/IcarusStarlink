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

    private sealed class FakeSettingsService(AppSettings settings) : ISettingsService
    {
        public AppSettings Current { get; } = settings;

        public bool Save() => true;
    }
}
