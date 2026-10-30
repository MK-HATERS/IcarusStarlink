using System.IO;
using System.Net.Http;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Settings;

namespace IcarusStarlink.App.Tests.Services;

public sealed class SteamCmdServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests_SteamCmd", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// The user's report (2026-10-30): Check for update never found one (server on build 25621760,
    /// Steam listing 26313077). SteamCMD's real app_info_print for the Icarus server names "public"
    /// and "experimental" as depot manifests before the branches list, and the run's de-duplication
    /// of repeated lines dropped them from the branches list, so no branch had a build.
    /// </summary>
    [Theory]
    [InlineData("public", 26313077)]
    [InlineData("experimental", 26313077)]
    public async Task GetLatestBuildId_ReadsTheBranchFromRealAppInfo_ThoughItsLinesRepeat(string branch, int expected)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "appinfo.txt"), RealAppInfo);
        var exe = Path.Combine(_folder, "steamcmd.bat");
        File.WriteAllText(exe, "@echo off\r\necho Logging in user 'anonymous' to Steam Public...OK\r\ntype \"%~dp0appinfo.txt\"\r\necho Unloading Steam API...OK\r\n");
        var settings = new FakeSettings();
        settings.Current.SteamCmdExePath = exe;
        using var http = new HttpClient();
        var service = new SteamCmdService(http, settings, _folder);

        Assert.Equal(expected, await service.GetLatestBuildIdAsync(branch));
    }

    private sealed class FakeSettings : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public bool Save() => true;
    }

    // steamcmd +login anonymous +app_info_update 1 +app_info_print 2089300 +quit, 2026-10-30.
    private const string RealAppInfo = """
        "2089300"
        {
        	"common"
        	{
        		"name"		"Icarus Dedicated Server"
        		"section_type"		"ownersonly"
        		"type"		"Tool"
        		"parent"		"1149460"
        		"ReleaseState"		"released"
        		"gameid"		"2089300"
        	}
        	"config"
        	{
        		"installdir"		"Icarus Dedicated Server"
        		"launch"
        		{
        			"0"
        			{
        				"executable"		"IcarusServer.exe"
        			}
        		}
        	}
        	"depots"
        	{
        		"2089301"
        		{
        			"manifests"
        			{
        				"public"
        				{
        					"gid"		"2047665044909114428"
        					"size"		"10475689157"
        					"download"		"7074683904"
        				}
        				"2.2.57"
        				{
        					"gid"		"5833021094036883719"
        					"size"		"9971412515"
        					"download"		"6921760928"
        				}
        				"experimental"
        				{
        					"gid"		"2047665044909114428"
        					"size"		"10475689157"
        					"download"		"7074683904"
        				}
        				"pre-dangerous-horizons"
        				{
        					"gid"		"7440749021072042167"
        					"size"		"10250598654"
        					"download"		"7059924912"
        				}
        			}
        		}
        		"branches"
        		{
        			"public"
        			{
        				"buildid"		"26313077"
        				"timeupdated"		"1793334073"
        				"timebuildupdated"		"1793325992"
        			}
        			"2.2.57"
        			{
        				"buildid"		"18602851"
        				"description"		"Pre-Ring Lake Change"
        				"timeupdated"		"1749247949"
        				"timebuildupdated"		"1748202009"
        			}
        			"experimental"
        			{
        				"buildid"		"26313077"
        				"description"		"Predeployment Testing"
        				"timeupdated"		"1793334042"
        				"timebuildupdated"		"1793325992"
        			}
        			"pre-dangerous-horizons"
        			{
        				"buildid"		"22131911"
        				"description"		"Pre-Dangerous Horizons Release"
        				"timeupdated"		"1772708173"
        				"timebuildupdated"		"1772213020"
        			}
        		}
        		"privatebranches"		"1"
        	}
        }
        """;
}
