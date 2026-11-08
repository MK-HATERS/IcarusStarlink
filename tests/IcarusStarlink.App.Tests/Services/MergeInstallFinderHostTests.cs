using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Steam;
using IcarusStarlink.Storage.Saves;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>The finder's save backups: a zip of each slot's worlds before every search, over a real save folder.</summary>
public sealed class MergeInstallFinderHostTests : IDisposable
{
    private const string SteamId = "76561198000000001";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "FinderHost_" + Guid.NewGuid().ToString("N"));
    private readonly string _worlds;
    private readonly string _prospects;
    private readonly MergeInstallFinderHost _host;

    public MergeInstallFinderHostTests()
    {
        var playerData = Path.Combine(_root, "PlayerData");
        var slot = Path.Combine(playerData, SteamId);
        _prospects = Path.Combine(slot, "Prospects");
        Directory.CreateDirectory(_prospects);
        File.WriteAllText(Path.Combine(slot, "Profile.json"), """{"UserID": "76561198000000001", "DataVersion": 4}""");
        File.WriteAllText(Path.Combine(_prospects, "World1.json"), """{"ProspectInfo": {}}""");

        var backups = Path.Combine(_root, "save_backups");
        _worlds = Path.Combine(backups, "Worlds");
        _host = new MergeInstallFinderHost(
            () => throw new NotSupportedException(),
            new SaveRepository(playerData, backups, new FakeLocator()),
            new FakeGameProcess(),
            _worlds);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best-effort cleanup only.
        }
    }

    private string[] WorldZips() => Directory.Exists(_worlds) ? Directory.GetFiles(_worlds, "*_worlds_before_find_broken_mod_*.zip") : [];

    /// <summary>Regression: each search zipped every world and nothing ever removed those zips (about 6 GB after 10 searches with 3 big slots).</summary>
    [Fact]
    public void BackUpSaves_FourSearches_KeepsTheNewestThreeWorldZips()
    {
        var written = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            written.Add(_host.BackUpSaves().Single(z => z.Contains("_worlds_before_find_broken_mod_")));
        }

        var kept = WorldZips();
        Assert.Equal(3, kept.Length);
        Assert.Contains(written[^1], kept);
        Assert.DoesNotContain(written[0], kept);
    }

    /// <summary>The slot backup was a "Backup now" zip, which is never pruned, so every search left one more behind.</summary>
    [Fact]
    public void BackUpSaves_BacksUpEachSlotAsAFindBrokenModBackup_NotAManualOne()
    {
        var slotZips = _host.BackUpSaves().Where(z => !z.Contains("_worlds_")).Select(Path.GetFileName).ToList();

        var zip = Assert.Single(slotZips);
        Assert.StartsWith($"{SteamId}_before_find_broken_mod_", zip);
        Assert.DoesNotContain("_manual", zip);
    }

    /// <summary>Regression: a zip that failed half way left a .partial file behind in the backups folder.</summary>
    [Fact]
    public void BackUpSaves_WorldZipFails_LeavesNoPartialFile()
    {
        using (new FileStream(Path.Combine(_prospects, "World1.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => _host.BackUpSaves());
        }

        Assert.Empty(Directory.GetFiles(_worlds));
    }

    private sealed class FakeLocator : ISteamInstallLocator
    {
        public string? FindIcarusContentPath() => null;

        public string? TryGetPersonaName(string steamId64) => null;
    }

    private sealed class FakeGameProcess : IGameProcessChecker
    {
        public bool IsRunning() => false;
    }
}
