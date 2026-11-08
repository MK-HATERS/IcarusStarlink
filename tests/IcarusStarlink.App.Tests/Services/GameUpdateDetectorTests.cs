using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.PakIO.Pak;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>The game build from Steam's appmanifest (the real file's shape), found from the Content folder; data.pak's hash when there's none.</summary>
public sealed class GameUpdateDetectorTests : IDisposable
{
    /// <summary>A real E:\SteamLibrary\steamapps\appmanifest_1149460.acf, as Steam writes it (TargetBuildID after buildid).</summary>
    private const string RealManifest = """
        "AppState"
        {
        	"appid"		"1149460"
        	"universe"		"1"
        	"LauncherPath"		"C:\\Program Files (x86)\\Steam\\steam.exe"
        	"name"		"Icarus"
        	"StateFlags"		"4"
        	"installdir"		"Icarus"
        	"lastupdated"		"1792106749"
        	"LastPlayed"		"1792196098"
        	"SizeOnDisk"		"54242039144"
        	"StagingSize"		"0"
        	"buildid"		"25956278"
        	"LastOwner"		"76561198000000001"
        	"DownloadType"		"1"
        	"UpdateResult"		"0"
        	"TargetBuildID"		"25956278"
        	"AutoUpdateBehavior"		"0"
        	"InstalledDepots"
        	{
        		"1149461"
        		{
        			"manifest"		"7024613107574759463"
        			"size"		"54242039144"
        		}
        	}
        	"UserConfig"
        	{
        		"language"		"english"
        		"BetaKey"		"public"
        	}
        }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "GameBuild_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeSettings : ISettingsService
    {
        public AppSettings Current { get; } = new();

        public int Saves { get; private set; }

        public bool Save()
        {
            Saves++;
            return true;
        }
    }

    private sealed class FakeUnrealPak(string? hash) : IUnrealPakService
    {
        public Task<string?> TryGetDataPakHashAsync(string icarusContentPath, CancellationToken cancellationToken = default) => Task.FromResult(hash);

        public Task<int> ExtractPakAsync(string unrealPakExePath, string pakFilePath, string outputDirectory, CancellationToken cancellationToken = default, string? filter = null) => throw new NotSupportedException();

        public Task<UnrealPakExtractResult> ExtractDataPakAsync(string unrealPakExePath, string icarusContentPath, string outputDirectory, DateTimeOffset? previousUpdateAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> CreatePakAsync(string unrealPakExePath, string stagingDirectory, string outputPakPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListPakContentsAsync(string unrealPakExePath, string pakFilePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PakVerifyResult> VerifyPakAsync(string unrealPakExePath, string pakFilePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>A typical Steam library:&lt;root&gt;\steamapps\common\Icarus\Icarus\Content, the manifest in steamapps.</summary>
    private string SteamLibrary(string manifestText)
    {
        var content = Path.Combine(_root, "SteamLibrary", "steamapps", "common", "Icarus", "Icarus", "Content");
        Directory.CreateDirectory(content);
        File.WriteAllText(Path.Combine(_root, "SteamLibrary", "steamapps", "appmanifest_1149460.acf"), manifestText);
        return content;
    }

    [Fact]
    public void ReadsTheBuildIdFromTheRealManifestShape() =>
        Assert.Equal("25956278", GameUpdateDetector.ReadBuildId(RealManifest));

    [Fact]
    public void FindsTheManifestFromTheContentFolder()
    {
        var content = SteamLibrary(RealManifest);

        Assert.Equal(Path.Combine(_root, "SteamLibrary", "steamapps", "appmanifest_1149460.acf"), GameUpdateDetector.FindAppManifest(content));
        Assert.Null(GameUpdateDetector.FindAppManifest(Path.Combine(_root, "Elsewhere")));
    }

    [Fact]
    public async Task FirstCheck_RecordsTheBuild_ThenAChangedBuildIsAnUpdate()
    {
        var content = SteamLibrary(RealManifest);
        var settings = new FakeSettings();
        settings.Current.IcarusContentPath = content;
        var detector = new GameUpdateDetector(settings, new FakeUnrealPak(null));

        var first = await detector.CheckAsync();
        Assert.False(first!.IsUpdated);
        Assert.Equal("25956278", settings.Current.LastSeenGameBuildId);

        File.WriteAllText(GameUpdateDetector.FindAppManifest(content)!, RealManifest.Replace("25956278", "26010101"));
        var second = await detector.CheckAsync();
        Assert.True(second!.IsUpdated);
        Assert.Equal("26010101", second.CurrentBuild);
        Assert.Equal("25956278", second.PreviousBuild);
        Assert.Equal(" (build 26010101)", second.BuildLabel);

        // Still updated until Update everything (or Dismiss) moves the mods on to it.
        Assert.True((await detector.CheckAsync())!.IsUpdated);
        detector.MarkReady("26010101");
        Assert.False((await detector.CheckAsync())!.IsUpdated);
    }

    /// <summary>
    /// Regression: a manifest that was there but locked (Steam rewriting it, an antivirus) or empty
    /// was replaced by data.pak's hash. The recorded Steam build was overwritten, and the next check
    /// recorded the new build without calling it an update, so an Icarus update was never offered.
    /// </summary>
    [Fact]
    public async Task AManifestThatCantBeRead_IsUnknown_AndAnUpdateIsStillSeen()
    {
        var content = SteamLibrary(RealManifest);
        var settings = new FakeSettings();
        settings.Current.IcarusContentPath = content;
        settings.Current.LastSeenGameBuildId = "25956278";
        var detector = new GameUpdateDetector(settings, new FakeUnrealPak("ABCDEF0123456789ABCDEF"));
        var manifest = GameUpdateDetector.FindAppManifest(content)!;

        using (new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Null(await detector.ReadCurrentBuildAsync());
            Assert.Null(await detector.CheckAsync());
        }

        File.WriteAllText(manifest, "");
        Assert.Null(await detector.CheckAsync());

        Assert.Equal("25956278", settings.Current.LastSeenGameBuildId);
        Assert.Equal(0, settings.Saves);

        File.WriteAllText(manifest, RealManifest.Replace("25956278", "26010101"));
        Assert.True((await detector.CheckAsync())!.IsUpdated);
    }

    /// <summary>Update everything skips Refresh game data only on a sure match: never when the hash can't be read or none was recorded.</summary>
    [Fact]
    public async Task IsGameDataCurrent_OnlyWhenDataPaksHashMatchesTheLastRefresh()
    {
        var settings = new FakeSettings();
        settings.Current.IcarusContentPath = Path.Combine(_root, "Game", "Content");
        Directory.CreateDirectory(settings.Current.IcarusContentPath);
        settings.Current.LastDataPakHash = "ABCDEF";

        Assert.True(await new GameUpdateDetector(settings, new FakeUnrealPak("abcdef")).IsGameDataCurrentAsync());
        Assert.False(await new GameUpdateDetector(settings, new FakeUnrealPak("012345")).IsGameDataCurrentAsync());
        Assert.False(await new GameUpdateDetector(settings, new FakeUnrealPak(null)).IsGameDataCurrentAsync());

        settings.Current.LastDataPakHash = null;
        Assert.False(await new GameUpdateDetector(settings, new FakeUnrealPak("abcdef")).IsGameDataCurrentAsync());
    }

    [Fact]
    public async Task WithoutAManifest_DataPaksHashStandsIn()
    {
        var settings = new FakeSettings();
        settings.Current.IcarusContentPath = Path.Combine(_root, "Copied", "Content");
        Directory.CreateDirectory(settings.Current.IcarusContentPath);
        var detector = new GameUpdateDetector(settings, new FakeUnrealPak("ABCDEF0123456789ABCDEF"));

        Assert.Equal("data-abcdef0123456789", await detector.ReadCurrentBuildAsync());

        // A Steam build id seen before can't be compared with a hash: recorded, not called an update.
        settings.Current.LastSeenGameBuildId = "25956278";
        var state = await detector.CheckAsync();
        Assert.False(state!.IsUpdated);
        Assert.Equal("", state.BuildLabel);
    }
}
