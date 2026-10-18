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
}
