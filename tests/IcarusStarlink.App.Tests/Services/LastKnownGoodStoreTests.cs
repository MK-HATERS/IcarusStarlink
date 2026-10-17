using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Profiles;
using IcarusStarlink.PakIO;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>The snapshot made before Update everything: what it saves, that only three are kept, and that Roll back's pak part puts the old merge back.</summary>
public sealed class LastKnownGoodStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "LastKnownGood_" + Guid.NewGuid().ToString("N"));

    private string Content => Path.Combine(_root, "Game", "Content");

    private string ModsFolder => Path.Combine(Content, "Paks", "mods");

    private string Backups => Path.Combine(_root, "Backups");

    private LastKnownGoodStore Store => new(Path.Combine(Backups, "LastKnownGood"));

    public LastKnownGoodStoreTests() => Directory.CreateDirectory(ModsFolder);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void Install(string pakText, string manifestText)
    {
        File.WriteAllText(Path.Combine(ModsFolder, InstallManifestNames.MergedPakFileName), pakText);
        File.WriteAllText(Path.Combine(ModsFolder, InstallManifestNames.PakManifest), manifestText);
    }

    private static Profile Profile(params string[] queue) => new()
    {
        Name = "Default",
        MergeQueueFolderNames = [.. queue],
        Options = new GameplayOptions { StacksMultiplier = 3 },
        ConflictWinners = new() { ["d_itemable.json|Canteen|Weight"] = "Lighter Canteen" },
    };

    [Fact]
    public void Create_SavesProfilePakManifestUe4ssAndBuild()
    {
        Install("old pak", "Includes the following mods:\nA\nB");
        File.WriteAllText(Path.Combine(ModsFolder, "ZZ_ArcFission_Data_3_P.pak"), "arcfission");

        var snapshot = Store.Create(Profile("A", "B"), Content, ["WirelessPower"], "25956278", new DateTimeOffset(2026, 10, 17, 9, 30, 0, TimeSpan.Zero));

        Assert.StartsWith(Path.Combine(Backups, "LastKnownGood"), snapshot.FolderPath);
        Assert.Equal([InstallManifestNames.MergedPakFileName, InstallManifestNames.PakManifest], snapshot.MergedFiles);

        var read = Assert.Single(Store.List());
        Assert.Equal("25956278", read.GameBuildId);
        Assert.Equal(["A", "B"], read.Profile!.MergeQueueFolderNames);
        Assert.Equal(3.0, read.Profile.Options.StacksMultiplier);
        Assert.Equal("Lighter Canteen", read.Profile.ConflictWinners["d_itemable.json|Canteen|Weight"]);
        Assert.Equal(["WirelessPower"], read.EnabledUe4ssMods);
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 9, 30, 0, TimeSpan.Zero), read.CreatedAtUtc);
    }

    [Fact]
    public void OnlyTheNewestThreeAreKept()
    {
        var start = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 5; i++)
        {
            Store.Create(Profile($"Mod{i}"), null, [], $"build{i}", start.AddDays(i));
        }

        var kept = Store.List();
        Assert.Equal(LastKnownGoodStore.Kept, kept.Count);
        Assert.Equal(["build4", "build3", "build2"], kept.Select(s => s.GameBuildId));
        Assert.Equal(LastKnownGoodStore.Kept, Directory.GetDirectories(Path.Combine(Backups, "LastKnownGood")).Length);
    }

    [Fact]
    public void RestoreMergedPak_PutsTheOldOneBack_AfterBackingUpTheCurrent()
    {
        Install("old pak", "old list");
        var snapshot = Store.Create(Profile("A"), Content, [], "1", DateTimeOffset.UtcNow);
        Install("new pak", "new list");

        Store.RestoreMergedPak(snapshot, Content, Backups);

        Assert.Equal("old pak", File.ReadAllText(Path.Combine(ModsFolder, InstallManifestNames.MergedPakFileName)));
        Assert.Equal("old list", File.ReadAllText(Path.Combine(ModsFolder, InstallManifestNames.PakManifest)));
        // The current one is backed up where Install keeps its backups (Restore previous install switches back).
        Assert.Contains(Directory.GetFiles(Backups), f => File.ReadAllText(f) == "new pak");
        Assert.Contains(Directory.GetFiles(Backups), f => File.ReadAllText(f) == "new list");
    }

    [Fact]
    public void RestoreMergedPak_WithNothingInstalledThen_TakesTheCurrentOneOut_LeavingArcFission()
    {
        var snapshot = Store.Create(Profile(), Content, [], "1", DateTimeOffset.UtcNow);
        Install("new pak", "new list");
        File.WriteAllText(Path.Combine(ModsFolder, "ZZ_ArcFission_Data_3_P.pak"), "arcfission");

        Store.RestoreMergedPak(snapshot, Content, Backups);

        Assert.Equal(["ZZ_ArcFission_Data_3_P.pak"], Directory.GetFiles(ModsFolder).Select(Path.GetFileName));
        Assert.Contains(Directory.GetFiles(Backups), f => File.ReadAllText(f) == "new pak");
    }

    [Fact]
    public void RunRecord_IsKeptBesideItsSnapshot()
    {
        var snapshot = Store.Create(Profile(), null, [], "1", DateTimeOffset.UtcNow);
        Assert.Null(Store.LoadRunRecord(snapshot));

        Store.SaveRunRecord(snapshot, new GameUpdateRunRecord { Completed = true, Message = "Done.", Fixed = [new("laanp-Canteens", "Canteens", 4)] });

        var record = Store.LoadRunRecord(Store.Latest()!);
        Assert.True(record!.Completed);
        Assert.Equal(4, Assert.Single(record.Fixed).FixCount);
    }
}
