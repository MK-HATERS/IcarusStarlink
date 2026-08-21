using IcarusStarlink.Core.Steam;

namespace IcarusStarlink.Core.Tests;

public class SteamLibraryVdfTests
{
    // Real shape, captured from the user's own libraryfolders.vdf during Phase 7.5 planning
    // (values below are trimmed/re-ordered but the format — tab-indented, backslash-escaped
    // paths, numbered top-level blocks — is exactly as Steam itself writes it).
    private const string RealisticVdf = """
        "libraryfolders"
        {
        	"0"
        	{
        		"path"		"C:\\Program Files (x86)\\Steam"
        		"label"		""
        		"apps"
        		{
        			"228980"		"440750965"
        		}
        	}
        	"1"
        	{
        		"path"		"E:\\SteamLibrary"
        		"label"		""
        		"apps"
        		{
        			"1149460"		"53626945400"
        		}
        	}
        	"2"
        	{
        		"path"		"G:\\SteamLibrary"
        		"label"		""
        		"apps"
        		{
        		}
        	}
        }
        """;

    [Fact]
    public void ParseLibraryPaths_RealisticVdf_ExtractsEveryLibraryWithBackslashesUnescaped()
    {
        var paths = SteamLibraryVdf.ParseLibraryPaths(RealisticVdf);

        Assert.Equal(
            [@"C:\Program Files (x86)\Steam", @"E:\SteamLibrary", @"G:\SteamLibrary"],
            paths);
    }

    [Fact]
    public void ParseLibraryPaths_EmptyContent_ReturnsEmpty()
    {
        Assert.Empty(SteamLibraryVdf.ParseLibraryPaths(""));
    }

    [Fact]
    public void ParseLibraryPaths_NoPathKeys_ReturnsEmpty()
    {
        const string vdf = """
            "libraryfolders"
            {
            	"0"
            	{
            		"label"		"no path key here"
            	}
            }
            """;

        Assert.Empty(SteamLibraryVdf.ParseLibraryPaths(vdf));
    }
}
