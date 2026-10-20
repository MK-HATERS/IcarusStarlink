using IcarusStarlink.Core.Server;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.Core.Tests.Ue4ss;

/// <summary>
/// UE4SS's own rule for which mods it starts (RE-UE4SS UE4SSProgram::start_mods; every UE4SS.log
/// shows the two passes): mods.txt's "1" lines, then every folder with an enabled.txt — so
/// enabled.txt wins over a 0 in mods.txt, and a folder with neither does nothing.
/// </summary>
public sealed class Ue4ssModStartTests : IDisposable
{
    private readonly string _mods = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "Ue4ssStart_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_mods))
        {
            Directory.Delete(_mods, recursive: true);
        }
    }

    private void Mod(string name, bool enabledFile)
    {
        Directory.CreateDirectory(Path.Combine(_mods, name, "Scripts"));
        if (enabledFile)
        {
            File.WriteAllText(Path.Combine(_mods, name, "enabled.txt"), "");
        }
    }

    [Theory]
    [InlineData("1", false, true)]
    [InlineData("0", false, false)]
    [InlineData("0", true, true)]
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]
    public void Starts(string? listValue, bool enabledFile, bool starts)
    {
        Mod("M", enabledFile);
        var list = Ue4ssModsTxt.Parse(listValue is null ? "Other : 1" : $"M : {listValue}");

        Assert.Equal(starts, Ue4ssModStart.Starts(_mods, "M", list));
        Assert.Equal(starts, Ue4ssModStart.WhyNotStarted(_mods, "M", list) is null);
    }

    [Fact]
    public void SwitchOn_WritesEnabledTxt_WhichBeatsAZeroInModsTxt()
    {
        Mod("M", enabledFile: false);
        var list = Ue4ssModsTxt.Parse("M : 0");

        Ue4ssModStart.SwitchOn(_mods, "M");

        Assert.True(Ue4ssModStart.Starts(_mods, "M", list));
    }

    /// <summary>The Server page lists a server mod as on exactly when UE4SS would start it (it used to let a 0 in mods.txt win over enabled.txt).</summary>
    [Fact]
    public void ServerListing_FollowsTheSameRule()
    {
        var install = Path.Combine(_mods, "Server");
        var mods = DedicatedServerLayout.Ue4ssModsFolder(install);
        foreach (var (name, file) in new[] { ("Listed", false), ("ZeroButFile", true), ("Zero", false) })
        {
            Directory.CreateDirectory(Path.Combine(mods, name));
            if (file)
            {
                File.WriteAllText(Path.Combine(mods, name, "enabled.txt"), "");
            }
        }

        File.WriteAllText(Path.Combine(mods, "mods.txt"), "Listed : 1\nZeroButFile : 0\nZero : 0\n");

        var listed = ServerModFiles.ListUe4ssMods(install).ToDictionary(m => m.Name, m => m.Enabled);

        Assert.True(listed["Listed"]);
        Assert.True(listed["ZeroButFile"]);
        Assert.False(listed["Zero"]);
    }
}
