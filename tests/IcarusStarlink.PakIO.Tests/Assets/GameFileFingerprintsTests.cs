using IcarusStarlink.PakIO.Assets;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests.Assets;

/// <summary>The game's files per build: what the game changed between two builds, and proof a mod's copy predates it — never a claim with one build.</summary>
public sealed class GameFileFingerprintsTests : IDisposable
{
    private const string File = "BP/Systems/BP_ProspectManager";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "Fingerprints_" + Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_root, "Cache", "game_file_fingerprints.json");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static BlueprintSummary Summary(Dictionary<string, string> functions, Dictionary<string, string>? defaults = null) =>
        new(new HashSet<string>(), new HashSet<string>(), functions, defaults ?? new Dictionary<string, string> { ["Default__BP_ProspectManager_C.Radius"] = "100" });

    private static readonly DateTimeOffset Week1 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Week2 = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The old build had GetCurrentProspectLocation as "old"; the new one changed it, added OnLanded and dropped Legacy.</summary>
    private GameFileFingerprintStore TwoBuilds()
    {
        var store = GameFileFingerprintStore.Load(StorePath);
        store.Record("25900000", File + ".uasset", Summary(new() { ["GetCurrentProspectLocation"] = "old", ["Tick"] = "same", ["Legacy"] = "x" }), Week1);
        store.Record("25956278", File, Summary(new() { ["GetCurrentProspectLocation"] = "new", ["Tick"] = "same", ["OnLanded"] = "n" },
            new Dictionary<string, string> { ["Default__BP_ProspectManager_C.Radius"] = "250" }), Week2);
        return store;
    }

    [Fact]
    public void WithOneBuild_NothingIsSaid()
    {
        var store = GameFileFingerprintStore.Load(StorePath);
        store.Record("25956278", File, Summary(new() { ["GetCurrentProspectLocation"] = "new" }), Week2);

        Assert.True(store.Has("25956278", File + ".uasset"));
        Assert.Null(store.ChangeAt("25956278", File));
    }

    [Fact]
    public void TwoBuilds_NameWhatTheGameChanged()
    {
        var change = TwoBuilds().ChangeAt("25956278", File);

        Assert.NotNull(change);
        Assert.Equal("25900000", change.PreviousBuild);
        Assert.Equal(["GetCurrentProspectLocation"], change.ChangedFunctions);
        Assert.Equal(["OnLanded"], change.AddedFunctions);
        Assert.Equal(["Legacy"], change.RemovedFunctions);
        Assert.True(change.SettingsChanged);
        Assert.Equal("the game changed GetCurrentProspectLocation, added OnLanded, removed Legacy and changed its settings in this update", change.Describe());
    }

    [Fact]
    public void ModStillHoldingTheGamesOldCode_IsProvenOutOfDate()
    {
        var change = TwoBuilds().ChangeAt("25956278", File)!;
        var modCopy = Summary(new() { ["GetCurrentProspectLocation"] = "old", ["Tick"] = "same", ["OnLanded"] = "n" });

        Assert.Equal("out of date: the game changed GetCurrentProspectLocation in this update; this mod still has its own older copy", change.OutOfDateProof(modCopy));
    }

    [Fact]
    public void ModLackingAFunctionTheGameAdded_IsProvenOutOfDate()
    {
        var change = TwoBuilds().ChangeAt("25956278", File)!;
        var modCopy = Summary(new() { ["GetCurrentProspectLocation"] = "new", ["Tick"] = "same" });

        Assert.Equal("out of date: the game added OnLanded in this update; this mod's copy doesn't have it", change.OutOfDateProof(modCopy));
    }

    [Fact]
    public void ModsOwnChangeToAFunction_ProvesNothing()
    {
        // Differs from both the old and the new game's: could be the mod's own edit, made for either.
        var change = TwoBuilds().ChangeAt("25956278", File)!;
        var modCopy = Summary(new() { ["GetCurrentProspectLocation"] = "mine", ["Tick"] = "same", ["OnLanded"] = "n" });

        Assert.Null(change.OutOfDateProof(modCopy));
    }

    [Fact]
    public void SavedAndLoaded_KeepsEveryBuild()
    {
        TwoBuilds().SaveIfChanged();

        var reloaded = GameFileFingerprintStore.Load(StorePath);

        Assert.Equal(["25956278", "25900000"], reloaded.Builds);
        Assert.True(reloaded.Has("25900000", "bp/systems/bp_prospectmanager"));
        Assert.Equal(["GetCurrentProspectLocation"], reloaded.ChangeAt("25956278", File)!.ChangedFunctions);
    }

    [Fact]
    public void RecordingAgain_KeepsTheFirstCopy()
    {
        var store = GameFileFingerprintStore.Load(StorePath);
        Assert.True(store.Record("1", File, Summary(new() { ["F"] = "a" }), Week1));
        Assert.False(store.Record("1", File, Summary(new() { ["F"] = "b" }), Week2));
        store.Record("2", File, Summary(new() { ["F"] = "a" }), Week2);

        Assert.Empty(store.ChangeAt("2", File)!.ChangedFunctions);
    }

    [Fact]
    public void OnlyTheNewestBuildsAreKept()
    {
        var store = GameFileFingerprintStore.Load(StorePath);
        for (var i = 0; i < GameFileFingerprintStore.MaxBuilds + 2; i++)
        {
            store.Record($"build{i}", File, Summary(new() { ["F"] = i.ToString() }), Week1.AddDays(i));
        }

        Assert.Equal(GameFileFingerprintStore.MaxBuilds, store.Builds.Count);
        Assert.Equal($"build{GameFileFingerprintStore.MaxBuilds + 1}", store.Builds[0]);
        Assert.DoesNotContain("build0", store.Builds);
    }

    [Fact]
    public void RiskPill_SaysOutOfDateOnlyWithProof()
    {
        const string Player = "BP/Player/BP_IcarusPlayerCharacterSurvival";
        var changedOnly = ReplacedFileRiskRater.Assess([Player], [])! with
        {
            Files = [ReplacedFileRiskRater.Rate(Player) with { ChangedByGame = "the game changed Drink in this update" }],
        };
        Assert.Equal("High risk", changedOnly.Label);
        Assert.False(changedOnly.IsOutOfDate);
        Assert.True(changedOnly.IsChangedByGameUpdate);
        Assert.Contains("BP_IcarusPlayerCharacterSurvival — the game changed Drink in this update.", changedOnly.Explanation);

        var proven = changedOnly with
        {
            Files = [changedOnly.Files[0] with { OutOfDateProof = "out of date: the game changed Drink in this update; this mod still has its own older copy" }],
        };
        Assert.Equal("High risk · out of date", proven.Label);
        Assert.True(proven.IsOutOfDate);
        Assert.Contains("BP_IcarusPlayerCharacterSurvival — out of date: the game changed Drink in this update; this mod still has its own older copy.", proven.Explanation);

        // Nothing recorded about game updates: the pill is as before.
        Assert.Equal("High risk", ReplacedFileRiskRater.Assess([Player], [])!.Label);
    }
}
