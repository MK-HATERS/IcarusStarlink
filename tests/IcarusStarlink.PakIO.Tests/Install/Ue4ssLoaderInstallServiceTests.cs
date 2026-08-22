using System.IO.Compression;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Install;

namespace IcarusStarlink.PakIO.Tests.Install;

public class Ue4ssLoaderInstallServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _fakeContentPath;
    private readonly string _backupDirectory;
    private readonly Ue4ssLoaderInstallService _service = new();

    public Ue4ssLoaderInstallServiceTests()
    {
        _fakeContentPath = Path.Combine(_tempDir, "FakeIcarusContent");
        _backupDirectory = Path.Combine(_tempDir, "Backups");
        Directory.CreateDirectory(_fakeContentPath);
    }

    private string Win64Folder => Ue4ssGamePaths.ResolveWin64Folder(_fakeContentPath);
    private string LoaderFolder => Ue4ssGamePaths.ResolveLoaderFolder(_fakeContentPath);

    /// <summary>Matches the real UE4SS_v3.0.1.zip structure confirmed during Phase 8.5 planning: dwmapi.dll/UE4SS.dll/UE4SS-settings.ini at the zip root, a Mods\ folder with a built-in mod plus mods.txt.</summary>
    private string CreateFakeReleaseZip(string dwmapiContent = "new dwmapi bytes", string dllContent = "new UE4SS.dll bytes")
    {
        var zipPath = Path.Combine(_tempDir, $"UE4SS_{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        WriteEntry(archive, "dwmapi.dll", dwmapiContent);
        WriteEntry(archive, "UE4SS.dll", dllContent);
        WriteEntry(archive, "UE4SS-settings.ini", "new default settings");
        WriteEntry(archive, "README.md", "new readme");
        WriteEntry(archive, "Mods/mods.txt", "ActorDumperMod : 1");
        WriteEntry(archive, "Mods/ActorDumperMod/Scripts/main.lua", "-- new built-in mod script");
        return zipPath;
    }

    private static void WriteEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }

    [Fact]
    public void GetStatus_NothingInstalled_ReturnsNotInstalled()
    {
        var status = _service.GetStatus(_fakeContentPath);

        Assert.False(status.IsInstalled);
        Assert.Null(status.InstalledVersion);
    }

    [Fact]
    public async Task InstallOrUpdateAsync_FreshInstall_PlacesDwmapiAtWin64RootAndEverythingElseUnderNestedFolder()
    {
        var zipPath = CreateFakeReleaseZip();

        await _service.InstallOrUpdateAsync(_fakeContentPath, zipPath, _backupDirectory);

        Assert.Equal("new dwmapi bytes", await File.ReadAllTextAsync(Path.Combine(Win64Folder, "dwmapi.dll")));
        Assert.Equal("new UE4SS.dll bytes", await File.ReadAllTextAsync(Path.Combine(LoaderFolder, "UE4SS.dll")));
        Assert.True(File.Exists(Path.Combine(LoaderFolder, "UE4SS-settings.ini")));
        Assert.True(File.Exists(Path.Combine(LoaderFolder, "Mods", "ActorDumperMod", "Scripts", "main.lua")));
    }

    [Fact]
    public async Task GetStatus_AfterFreshInstall_ReportsInstalled()
    {
        await _service.InstallOrUpdateAsync(_fakeContentPath, CreateFakeReleaseZip(), _backupDirectory);

        var status = _service.GetStatus(_fakeContentPath);

        Assert.True(status.IsInstalled);
        // No UE4SS.log in the fake zip (UE4SS itself writes that on launch, not the installer) —
        // version comes back null, which GetStatus must handle without throwing.
        Assert.Null(status.InstalledVersion);
    }

    [Fact]
    public async Task InstallOrUpdateAsync_ExistingModFolder_NeverOverwritesIt()
    {
        var existingModFile = Path.Combine(LoaderFolder, "Mods", "ActorDumperMod", "Scripts", "main.lua");
        Directory.CreateDirectory(Path.GetDirectoryName(existingModFile)!);
        await File.WriteAllTextAsync(existingModFile, "USER'S OWN CUSTOMIZED SCRIPT");

        await _service.InstallOrUpdateAsync(_fakeContentPath, CreateFakeReleaseZip(), _backupDirectory);

        Assert.Equal("USER'S OWN CUSTOMIZED SCRIPT", await File.ReadAllTextAsync(existingModFile));
    }

    [Fact]
    public async Task InstallOrUpdateAsync_ExistingModsTxt_NeverOverwritesIt()
    {
        var modsTxtPath = Path.Combine(LoaderFolder, "Mods", "mods.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(modsTxtPath)!);
        await File.WriteAllTextAsync(modsTxtPath, "SomeOtherMod : 1\nAnotherOne : 0");

        await _service.InstallOrUpdateAsync(_fakeContentPath, CreateFakeReleaseZip(), _backupDirectory);

        Assert.Equal("SomeOtherMod : 1\nAnotherOne : 0", await File.ReadAllTextAsync(modsTxtPath));
    }

    [Fact]
    public async Task InstallOrUpdateAsync_ExistingSettingsIni_NeverOverwritesIt()
    {
        Directory.CreateDirectory(LoaderFolder);
        var settingsPath = Path.Combine(LoaderFolder, "UE4SS-settings.ini");
        await File.WriteAllTextAsync(settingsPath, "GraphicsAPI=dx11 ; user's own customization");

        await _service.InstallOrUpdateAsync(_fakeContentPath, CreateFakeReleaseZip(), _backupDirectory);

        Assert.Equal("GraphicsAPI=dx11 ; user's own customization", await File.ReadAllTextAsync(settingsPath));
    }

    [Fact]
    public async Task InstallOrUpdateAsync_ExistingLoaderDll_AlwaysOverwritesIt()
    {
        Directory.CreateDirectory(LoaderFolder);
        await File.WriteAllTextAsync(Path.Combine(LoaderFolder, "UE4SS.dll"), "old UE4SS.dll bytes");

        await _service.InstallOrUpdateAsync(_fakeContentPath, CreateFakeReleaseZip(dllContent: "updated UE4SS.dll bytes"), _backupDirectory);

        Assert.Equal("updated UE4SS.dll bytes", await File.ReadAllTextAsync(Path.Combine(LoaderFolder, "UE4SS.dll")));
    }

    [Fact]
    public async Task InstallOrUpdateAsync_ExistingLoaderFolderAndDwmapi_BacksUpBothBeforeOverwriting()
    {
        Directory.CreateDirectory(LoaderFolder);
        await File.WriteAllTextAsync(Path.Combine(LoaderFolder, "UE4SS.dll"), "old dll");
        Directory.CreateDirectory(Win64Folder);
        await File.WriteAllTextAsync(Path.Combine(Win64Folder, "dwmapi.dll"), "old dwmapi");

        await _service.InstallOrUpdateAsync(_fakeContentPath, CreateFakeReleaseZip(), _backupDirectory);

        var backupRoot = Path.Combine(_backupDirectory, "UE4SS-Loader");
        Assert.True(Directory.Exists(backupRoot));
        Assert.Contains(Directory.GetDirectories(backupRoot), d => Path.GetFileName(d).StartsWith("ue4ss_"));
        Assert.Contains(Directory.GetFiles(backupRoot), f => Path.GetFileName(f).StartsWith("dwmapi_"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
