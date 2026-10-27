using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.Core.Tests.Ue4ss;

public sealed class Ue4ssModConfigFilesTests : IDisposable
{
    private readonly string _mod = Path.Combine(Path.GetTempPath(), "ModConfigFiles_" + Guid.NewGuid().ToString("N"));

    public Ue4ssModConfigFilesTests() => Directory.CreateDirectory(_mod);

    public void Dispose() => Directory.Delete(_mod, recursive: true);

    private void Touch(string relative)
    {
        var path = Path.Combine(_mod, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
    }

    [Fact]
    public void Find_TheConfigsRealModsShip()
    {
        Touch("config.lua");
        Touch("config.ini");
        Touch(Path.Combine("config", "NearbyCrafting.ini"));
        Touch(Path.Combine("Scripts", "settings.lua"));
        Touch(Path.Combine("Scripts", "main.lua"));
        Touch("enabled.txt");
        Touch("README.md");
        Touch(Path.Combine("ArcFission", "mod.lua"));

        var found = Ue4ssModConfigFiles.Find(_mod);

        Assert.Equal(
            ["config.ini", "config.lua", Path.Combine("config", "NearbyCrafting.ini"), Path.Combine("Scripts", "settings.lua")],
            found.Select(f => f.RelativePath));
        Assert.Equal([Ue4ssModConfigFormat.Ini, Ue4ssModConfigFormat.Lua, Ue4ssModConfigFormat.Ini, Ue4ssModConfigFormat.Lua], found.Select(f => f.Format));
    }

    [Fact]
    public void Find_NothingForAModWithoutSettings()
    {
        Touch(Path.Combine("Scripts", "main.lua"));
        Touch("enabled.txt");

        Assert.False(Ue4ssModConfigFiles.HasAny(_mod));
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_mod, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>
    /// MkHaters Tools' layout: config.lua, its shipped defaults config.default.lua, and two Scripts
    /// modules named like configs (mk_config.lua reads the config, mt_config.lua holds the defaults
    /// in code). The main file comes first, the defaults last and marked, the code not at all
    /// (user 2026-10-27: the File list opened on config.default.lua and offered the modules).
    /// </summary>
    [Fact]
    public void Find_MkHatersLayout_MainFirst_DefaultsMarked_CodeModulesLeftOut()
    {
        Write("config.default.lua", "return {\n    Enabled = true,\n}\n");
        Write("config.lua", "return {\n    Enabled = true,\n}\n");
        Write(Path.Combine("Scripts", "mk_config.lua"), "local M = {}\nlocal function readAll(path)\nend\nreturn M\n");
        Write(Path.Combine("Scripts", "mt_config.lua"), "local C = {}\nlocal DEFAULTS = { Enabled = true }\nfunction C.get() return DEFAULTS end\nreturn C\n");
        Write(Path.Combine("Scripts", "main.lua"), "");

        var found = Ue4ssModConfigFiles.Find(_mod);

        Assert.Equal(["config.lua", "config.default.lua"], found.Select(f => f.RelativePath));
        Assert.Equal([false, true], found.Select(f => f.IsDefaults));
        Assert.True(Ue4ssModConfigFiles.HasAny(_mod));
    }

    [Fact]
    public void HasAny_TheShippedDefaultsAloneDontCount()
    {
        Write("config.default.lua", "return {}\n");

        Assert.False(Ue4ssModConfigFiles.HasAny(_mod));
    }

    [Fact]
    public void DefaultsPathFor_SitsBesideTheFile()
    {
        Assert.Equal("config.default.lua", Ue4ssModConfigFiles.DefaultsPathFor("config.lua"));
        Assert.Equal(Path.Combine("config", "Pets.default.ini"), Ue4ssModConfigFiles.DefaultsPathFor(Path.Combine("config", "Pets.ini")));
        Assert.True(Ue4ssModConfigFiles.IsDefaults("settings.defaults.ini"));
        Assert.False(Ue4ssModConfigFiles.IsDefaults("config.lua"));
    }
}
