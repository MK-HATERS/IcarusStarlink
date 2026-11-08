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

    /// <summary>Retries on the same game build make more snapshots of an already-changed setup: the first one on that build (before any run changed anything) is kept and is the pre-update one.</summary>
    [Fact]
    public void RetriesOnOneBuild_KeepTheFirstSnapshotOfThatBuild()
    {
        var start = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        Store.Create(Profile("Old"), null, [], "build1", start);
        for (var i = 0; i < 5; i++)
        {
            Store.Create(Profile($"Try{i}"), null, [], "build2", start.AddDays(1).AddHours(i));
        }

        var kept = Store.List();
        Assert.Equal(["Try4", "Try3", "Try2", "Try0"], kept.Select(s => s.Profile!.MergeQueueFolderNames[0]));
        Assert.Equal(["Try0"], Store.PreUpdate()!.Profile!.MergeQueueFolderNames);
    }

    /// <summary>Regression: after builds B, C and back to B, Roll back went to the first B snapshot from weeks earlier.</summary>
    [Fact]
    public void ABuildSeenAgainAfterAnother_StartsANewPreUpdate()
    {
        var start = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        Store.Create(Profile("B first"), null, [], "B", start);
        Store.Create(Profile("C"), null, [], "C", start.AddDays(1));
        Store.Create(Profile("B again"), null, [], "B", start.AddDays(2));

        Assert.Equal(["B again"], Store.PreUpdate()!.Profile!.MergeQueueFolderNames);
    }

    /// <summary>Regression: an old snapshot of a build seen again later was kept for good as "the pre-update one".</summary>
    [Fact]
    public void AnOldSnapshotOfTheSameBuild_IsNotKeptForever()
    {
        var start = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        Store.Create(Profile("B1"), null, [], "B", start);
        Store.Create(Profile("C1"), null, [], "C", start.AddDays(1));
        Store.Create(Profile("C2"), null, [], "C", start.AddDays(2));
        Store.Create(Profile("B2"), null, [], "B", start.AddDays(3));

        var kept = Store.List();
        Assert.Equal(3, kept.Count);
        Assert.DoesNotContain(kept, s => s.Profile!.MergeQueueFolderNames[0] == "B1");
    }

    /// <summary>Regression: a pak copy that failed half-way left its snapshot folder (and the partial copy) behind for good.</summary>
    [Fact]
    public void Create_CopyFails_LeavesNoFolderBehind()
    {
        Install("pak", "list");

        using (new FileStream(Path.Combine(ModsFolder, InstallManifestNames.MergedPakFileName), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => Store.Create(Profile("A"), Content, [], "1", DateTimeOffset.UtcNow));
        }

        Assert.Empty(Directory.GetDirectories(Path.Combine(Backups, "LastKnownGood")));
    }

    /// <summary>Regression: a snapshot folder that never got its snapshot.json was never listed, so never pruned.</summary>
    [Fact]
    public void Create_RemovesAnOldUnfinishedFolder_ButNotAFreshOne()
    {
        var old = Path.Combine(Backups, "LastKnownGood", "20260101-080000");
        Directory.CreateDirectory(Path.Combine(old, "mods"));
        File.WriteAllText(Path.Combine(old, "mods", InstallManifestNames.MergedPakFileName), "half a pak");
        Directory.SetCreationTimeUtc(old, DateTime.UtcNow.AddHours(-1));
        var fresh = Path.Combine(Backups, "LastKnownGood", "20260101-090000");
        Directory.CreateDirectory(fresh);

        Store.Create(Profile("A"), null, [], "1", DateTimeOffset.UtcNow);

        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(fresh));
    }

    /// <summary>A snapshot copy that can't be read leaves the game's merged pak in place: the copies go in before anything is removed.</summary>
    [Fact]
    public void RestoreMergedPak_CopyFails_TheCurrentMergeStays()
    {
        Install("old pak", "old list");
        var snapshot = Store.Create(Profile("A"), Content, [], "1", DateTimeOffset.UtcNow);
        Install("new pak", "new list");
        var savedPak = Path.Combine(snapshot.FolderPath, "mods", InstallManifestNames.MergedPakFileName);

        using (new FileStream(savedPak, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => Store.RestoreMergedPak(snapshot, Content, Backups));
        }

        Assert.Equal("new pak", File.ReadAllText(Path.Combine(ModsFolder, InstallManifestNames.MergedPakFileName)));
        Assert.Equal("new list", File.ReadAllText(Path.Combine(ModsFolder, InstallManifestNames.PakManifest)));
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
