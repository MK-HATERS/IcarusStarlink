using IcarusStarlink.Core.Server;

namespace IcarusStarlink.Core.Tests.Server;

public sealed class ServerSettingsIniTests
{
    /// <summary>A file the way hosts ship it: the real section plus things this app doesn't know about.</summary>
    private const string RealFile = """
        ; written by a host panel
        [/Script/Icarus.DedicatedServerSettings]
        SessionName=Old Name
        JoinPassword=letmein
        MaxPlayers=8
        AdminPassword=
        ShutdownIfNotJoinedFor=300.000000
        ShutdownIfEmptyFor=300.000000
        AllowNonAdminsToLaunchProspects=True
        AllowNonAdminsToDeleteProspects=False
        LoadProspect=
        ResumeProspect=True
        LastProspectName=Olympus_Base
        CreateProspect=
        SomeFutureKey=keep me

        [/Script/Engine.SomethingElse]
        Foo=Bar
        """;

    [Fact]
    public void Read_ParsesEveryKnownKey()
    {
        var s = ServerSettingsIni.Parse(RealFile).Read();

        Assert.Equal("Old Name", s.SessionName);
        Assert.Equal("letmein", s.JoinPassword);
        Assert.Equal(8, s.MaxPlayers);
        Assert.Null(s.AdminPassword);
        Assert.Equal(300, s.ShutdownIfNotJoinedFor);
        Assert.True(s.AllowNonAdminsToLaunchProspects);
        Assert.False(s.AllowNonAdminsToDeleteProspects);
        Assert.True(s.ResumeProspect);
        Assert.Equal("Olympus_Base", s.LastProspectName);
        Assert.Null(s.LoadProspect);
        Assert.Null(s.GameSaveFrequency);
    }

    /// <summary>Writing must keep comments, other sections, unknown keys and LastProspectName (the server owns it) exactly as they were.</summary>
    [Fact]
    public void Write_UpdatesKnownKeysInPlaceAndKeepsEverythingElse()
    {
        var ini = ServerSettingsIni.Parse(RealFile);
        var s = ini.Read();
        s.AdminPassword = "s3cret";
        s.MaxPlayers = 4;
        s.ShutdownIfEmptyFor = -1;
        s.GameSaveFrequency = 10;
        s.FiberFoliageRespawn = false;

        ini.Write(s);
        var text = ini.ToString();

        Assert.Contains("AdminPassword=s3cret", text);
        Assert.Contains("MaxPlayers=4", text);
        Assert.Contains("ShutdownIfEmptyFor=-1.000000", text);
        Assert.Contains("; written by a host panel", text);
        Assert.Contains("SomeFutureKey=keep me", text);
        Assert.Contains("[/Script/Engine.SomethingElse]\r\nFoo=Bar", text);
        Assert.Contains("LastProspectName=Olympus_Base", text);

        // New keys land inside the settings section, before the blank line and the next section.
        var settingsEnd = text.IndexOf("[/Script/Engine.SomethingElse]", StringComparison.Ordinal);
        Assert.InRange(text.IndexOf("GameSaveFrequency=10.000000", StringComparison.Ordinal), 0, settingsEnd);
        Assert.InRange(text.IndexOf("FiberFoliageRespawn=False", StringComparison.Ordinal), 0, settingsEnd);

        var reread = ServerSettingsIni.Parse(text).Read();
        Assert.Equal("s3cret", reread.AdminPassword);
        Assert.Equal(4, reread.MaxPlayers);
        Assert.Equal(-1, reread.ShutdownIfEmptyFor);
        Assert.Equal(10, reread.GameSaveFrequency);
        Assert.False(reread.FiberFoliageRespawn);
    }

    /// <summary>The documented first-launch bug: the file often doesn't exist yet. Writing one from nothing must produce the section the game reads.</summary>
    [Fact]
    public void Write_OnAnEmptyFile_CreatesTheSection()
    {
        var ini = ServerSettingsIni.Parse(null);
        ini.Write(new DedicatedServerSettings { AdminPassword = "x", MaxPlayers = 8 });

        var text = ini.ToString();
        Assert.StartsWith("[/Script/Icarus.DedicatedServerSettings]\r\n", text);
        Assert.Contains("AdminPassword=x", text);
        Assert.Contains("ResumeProspect=\r\n", text);
        Assert.Equal(8, ServerSettingsIni.Parse(text).Read().MaxPlayers);
    }

    [Fact]
    public void SectionAndKeyNamesMatchCaseInsensitively()
    {
        var ini = ServerSettingsIni.Parse("[/script/icarus.dedicatedserversettings]\nmaxplayers=3\n");

        Assert.Equal(3, ini.Read().MaxPlayers);
        ini.SetRaw("MaxPlayers", "5");
        Assert.Equal("[/script/icarus.dedicatedserversettings]\r\nmaxplayers=5\r\n", ini.ToString());
    }

    [Fact]
    public void SaveAndLoad_RoundTripThroughDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"), "Config", "WindowsServer", "ServerSettings.ini");
        try
        {
            var ini = ServerSettingsIni.Load(path);
            ini.Write(new DedicatedServerSettings { JoinPassword = "pw", ResumeProspect = true });
            ini.Save(path);

            var back = ServerSettingsIni.Load(path).Read();
            Assert.Equal("pw", back.JoinPassword);
            Assert.True(back.ResumeProspect);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path)!)!)!, recursive: true);
        }
    }
}
