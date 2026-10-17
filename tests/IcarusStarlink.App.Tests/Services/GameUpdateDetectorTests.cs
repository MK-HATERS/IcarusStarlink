using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.PakIO.Pak;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>The game build from Steam's appmanifest (the real file's shape), found from the Content folder; data.pak's hash when there's none.</summary>
public sealed class GameUpdateDetectorTests : IDisposable
{
    /// <summary>The user's real E:\SteamLibrary\steamapps\appmanifest_1149460.acf, as Steam writes it (TargetBuildID after buildid).</summary>
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
        	"LastOwner"		"76561198851388131"
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

    /// <summary>A Steam library like the user's: &lt;root&gt;\steamapps\common\Icarus\Icarus\Content, the manifest in steamapps.</summary>
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
