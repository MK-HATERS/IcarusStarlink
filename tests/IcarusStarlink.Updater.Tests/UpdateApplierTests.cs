using IcarusStarlink.Updater;

namespace IcarusStarlink.Updater.Tests;

public sealed class UpdateApplierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests", $"Updater_{Guid.NewGuid():N}");
    private readonly string _installDirectory;
    private readonly string _newFilesDirectory;

    public UpdateApplierTests()
    {
        _installDirectory = Path.Combine(_root, "install");
        _newFilesDirectory = Path.Combine(_root, "new");
        Directory.CreateDirectory(_installDirectory);
        Directory.CreateDirectory(_newFilesDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void WriteInstall(string relativePath, string content)
    {
        var path = Path.Combine(_installDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void WriteNew(string relativePath, string content)
    {
        var path = Path.Combine(_newFilesDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Apply_OverwritesAppFilesWithNewVersions()
    {
        WriteInstall("IcarusStarlink.App.exe", "old exe");
        WriteInstall("IcarusStarlink.App.dll", "old dll");
        WriteNew("IcarusStarlink.App.exe", "new exe");
        WriteNew("IcarusStarlink.App.dll", "new dll");

        var copied = UpdateApplier.Apply(_installDirectory, _newFilesDirectory, _ => { });

        Assert.Equal(2, copied);
        Assert.Equal("new exe", File.ReadAllText(Path.Combine(_installDirectory, "IcarusStarlink.App.exe")));
        Assert.Equal("new dll", File.ReadAllText(Path.Combine(_installDirectory, "IcarusStarlink.App.dll")));
    }

    [Fact]
    public void Apply_NeverTouchesUserDataTheNewBuildDoesntShip()
    {
        // The core safety property: the update is copy-over-only, so everything the user
        // accumulated — mods, profiles, settings, skins — survives untouched by construction.
        WriteInstall("IcarusStarlink.App.exe", "old exe");
        WriteInstall("settings.json", "{\"user\":\"data\"}");
        WriteInstall("custom_skin.json", "{\"Colors\":{}}");
        WriteInstall("Extracted_Mods/MyMod/MyMod.EXMOD", "mod content");
        WriteInstall("Profiles/main.json", "profile");
        WriteNew("IcarusStarlink.App.exe", "new exe");

        UpdateApplier.Apply(_installDirectory, _newFilesDirectory, _ => { });

        Assert.Equal("{\"user\":\"data\"}", File.ReadAllText(Path.Combine(_installDirectory, "settings.json")));
        Assert.Equal("{\"Colors\":{}}", File.ReadAllText(Path.Combine(_installDirectory, "custom_skin.json")));
        Assert.Equal("mod content", File.ReadAllText(Path.Combine(_installDirectory, "Extracted_Mods", "MyMod", "MyMod.EXMOD")));
        Assert.Equal("profile", File.ReadAllText(Path.Combine(_installDirectory, "Profiles", "main.json")));
        Assert.Equal("new exe", File.ReadAllText(Path.Combine(_installDirectory, "IcarusStarlink.App.exe")));
    }

    [Fact]
    public void Apply_CreatesSubdirectoriesTheNewBuildAdds()
    {
        WriteNew("IcarusStarlink.App.exe", "exe");
        WriteNew("runtimes/win-x64/native/something.dll", "native");

        UpdateApplier.Apply(_installDirectory, _newFilesDirectory, _ => { });

        Assert.Equal("native", File.ReadAllText(Path.Combine(_installDirectory, "runtimes", "win-x64", "native", "something.dll")));
    }

    [Fact]
    public void Apply_RefusesAFolderThatIsntAnAppBuild()
    {
        WriteInstall("IcarusStarlink.App.exe", "old exe");
        WriteNew("README.txt", "definitely not an app build");

        Assert.Throws<InvalidOperationException>(() => UpdateApplier.Apply(_installDirectory, _newFilesDirectory, _ => { }));
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(_installDirectory, "IcarusStarlink.App.exe")));
    }
}
