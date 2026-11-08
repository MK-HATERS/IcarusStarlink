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

    /// <summary>Matches the real UE4SS_v3.0.1.zip structure: dwmapi.dll/UE4SS.dll/UE4SS-settings.ini at the zip root, a Mods\ folder with a built-in mod plus mods.txt.</summary>
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

    /// <summary>The installer used to key everything on the entry's first path segment, so a release zip with one wrapping folder landed a broken, misplaced install after a full backup, with no error.</summary>
    [Fact]
    public async Task InstallOrUpdateAsync_ReleaseZipWithOneWrappingFolder_IsInstalledAsIfFlat()
    {
        var zipPath = Path.Combine(_tempDir, $"UE4SS_{Guid.NewGuid():N}.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "UE4SS_v3.0.2/dwmapi.dll", "wrapped dwmapi bytes");
            WriteEntry(archive, "UE4SS_v3.0.2/UE4SS.dll", "wrapped UE4SS.dll bytes");
            WriteEntry(archive, "UE4SS_v3.0.2/UE4SS-settings.ini", "defaults");
            WriteEntry(archive, "UE4SS_v3.0.2/Mods/mods.txt", "ActorDumperMod : 1");
        }

        await _service.InstallOrUpdateAsync(_fakeContentPath, zipPath, _backupDirectory);

        Assert.Equal("wrapped dwmapi bytes", await File.ReadAllTextAsync(Path.Combine(Win64Folder, "dwmapi.dll")));
        Assert.Equal("wrapped UE4SS.dll bytes", await File.ReadAllTextAsync(Path.Combine(LoaderFolder, "UE4SS.dll")));
        Assert.True(File.Exists(Path.Combine(LoaderFolder, "Mods", "mods.txt")));
        Assert.False(Directory.Exists(Path.Combine(LoaderFolder, "UE4SS_v3.0.2")));
    }

    [Fact]
    public async Task InstallOrUpdateAsync_ArchiveWithoutTheLoaderFiles_IsRefusedBeforeAnythingIsBackedUpOrWritten()
    {
        var zipPath = Path.Combine(_tempDir, $"NotUE4SS_{Guid.NewGuid():N}.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "README.md", "this is some other download");
            WriteEntry(archive, "Scripts/main.lua", "-- a mod, not the loader");
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.InstallOrUpdateAsync(_fakeContentPath, zipPath, _backupDirectory));

        Assert.Contains("expected layout", ex.Message);
        Assert.False(Directory.Exists(_backupDirectory));
        Assert.False(Directory.Exists(LoaderFolder));
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

    /// <summary>
    /// Regression: the player's UE4SS-settings.ini is kept on an update, so the settings a newer
    /// build added never reached Starlink: the release's own copy goes beside it as
    /// UE4SS-settings.default.ini (replaced on every install) for the UE4SS config tab to read.
    /// </summary>
    [Fact]
    public async Task InstallOrUpdateAsync_ExistingSettingsIni_TheReleasesCopyGoesBesideIt()
    {
        var settingsPath = Path.Combine(LoaderFolder, "UE4SS-settings.ini");
        var defaultPath = Path.Combine(LoaderFolder, "UE4SS-settings.default.ini");
        Seed(settingsPath, "[General]\nEnableHotReloadSystem = 0\n");
        Seed(defaultPath, "an older build's defaults");

        await _service.InstallOrUpdateAsync(_fakeContentPath, CreateFakeDevBuildZip(), _backupDirectory);

        Assert.Equal("[General]\nEnableHotReloadSystem = 0\n", await File.ReadAllTextAsync(settingsPath));
        Assert.Equal("dev default settings", await File.ReadAllTextAsync(defaultPath));

        // A first install gets both: the player's file and the release's copy.
        Directory.Delete(LoaderFolder, recursive: true);
        await _service.InstallOrUpdateAsync(_fakeContentPath, CreateFakeReleaseZip(), _backupDirectory);
        Assert.Equal("new default settings", await File.ReadAllTextAsync(settingsPath));
        Assert.Equal("new default settings", await File.ReadAllTextAsync(defaultPath));
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

    /// <summary>Every dev build since 3.0.1 ships dwmapi.dll plus a ue4ss\ folder that mirrors Binaries\Win64\ue4ss (the real UE4SS_v3.0.1-1181-g83330635.zip layout).</summary>
    private string CreateFakeDevBuildZip(params (string Name, string Content)[] extraEntries)
    {
        var zipPath = Path.Combine(_tempDir, $"UE4SS_v3.0.1-1181-g83330635_{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        WriteEntry(archive, "dwmapi.dll", "dev dwmapi bytes");
        WriteEntry(archive, "ue4ss/UE4SS.dll", "dev UE4SS.dll bytes");
        WriteEntry(archive, "ue4ss/LICENSE", "MIT");
        WriteEntry(archive, "ue4ss/UE4SS-settings.ini", "dev default settings");
        WriteEntry(archive, "ue4ss/Mods/mods.txt", "Keybinds : 1");
        WriteEntry(archive, "ue4ss/Mods/Keybinds/Scripts/main.lua", "-- dev keybinds");
        WriteEntry(archive, "ue4ss/UE4SS_SDK_Backends/UE4SS.json", "{}");
        foreach (var (name, content) in extraEntries)
        {
            WriteEntry(archive, name, content);
        }

        return zipPath;
    }

    private string CreateZip(params (string Name, string Content)[] entries)
    {
        var zipPath = Path.Combine(_tempDir, $"UE4SS_{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            WriteEntry(archive, name, content);
        }

        return zipPath;
    }

    private void Seed(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>
    /// Regression: an entry that would land outside the game's folders was only caught when the
    /// install reached it, after dwmapi.dll (the zip's first file) had already been replaced.
    /// </summary>
    [Fact]
    public async Task InstallOrUpdateAsync_AnEntryThatWouldLandOutsideTheFolder_IsRefusedBeforeAnythingIsWritten()
    {
        Seed(Path.Combine(LoaderFolder, "UE4SS.dll"), "old dll");
        Seed(Path.Combine(Win64Folder, "dwmapi.dll"), "old dwmapi");
        var zipPath = CreateZip(("dwmapi.dll", "new dwmapi"), ("ue4ss/UE4SS.dll", "new dll"), ("ue4ss/../evil.dll", "evil"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.InstallOrUpdateAsync(_fakeContentPath, zipPath, _backupDirectory));

        Assert.Contains("Nothing was changed", ex.Message);
        Assert.Equal("old dwmapi", await File.ReadAllTextAsync(Path.Combine(Win64Folder, "dwmapi.dll")));
        Assert.Equal("old dll", await File.ReadAllTextAsync(Path.Combine(LoaderFolder, "UE4SS.dll")));
        Assert.False(File.Exists(Path.Combine(Win64Folder, "evil.dll")));
    }

    /// <summary>An entry declared over the size budget is refused the same way, before anything is written.</summary>
    [Fact]
    public async Task InstallOrUpdateAsync_AnEntryOverTheSizeLimit_IsRefusedBeforeAnythingIsWritten()
    {
        Seed(Path.Combine(Win64Folder, "dwmapi.dll"), "old dwmapi");
        var zipPath = Path.Combine(_tempDir, $"UE4SS_{Guid.NewGuid():N}.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "dwmapi.dll", "new dwmapi");
            WriteEntry(archive, "ue4ss/UE4SS.dll", "new dll");
            using var stream = archive.CreateEntry("ue4ss/huge.bin", CompressionLevel.Fastest).Open();
            var zeros = new byte[1024 * 1024];
            for (long written = 0; written <= IcarusStarlink.PakIO.Container.ExmodSizeLimits.MaxAssetEntryBytes; written += zeros.Length)
            {
                stream.Write(zeros, 0, (int)Math.Min(zeros.Length, IcarusStarlink.PakIO.Container.ExmodSizeLimits.MaxAssetEntryBytes + 1 - written));
            }
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.InstallOrUpdateAsync(_fakeContentPath, zipPath, _backupDirectory));

        Assert.Contains("Nothing was changed", ex.Message);
        Assert.Equal("old dwmapi", await File.ReadAllTextAsync(Path.Combine(Win64Folder, "dwmapi.dll")));
        Assert.False(File.Exists(Path.Combine(LoaderFolder, "UE4SS.dll")));
    }

    /// <summary>
    /// Regression: a file that couldn't be replaced partway (locked by antivirus, a full disk) left
    /// the files written before it as they were: a new dwmapi.dll next to the old UE4SS.dll.
    /// </summary>
    [Fact]
    public async Task InstallOrUpdateAsync_AFileThatCantBeReplaced_PutsBackWhatWasWritten()
    {
        var dllPath = Path.Combine(LoaderFolder, "UE4SS.dll");
        Seed(dllPath, "old dll");
        Seed(Path.Combine(LoaderFolder, "LICENSE"), "old license");
        Seed(Path.Combine(Win64Folder, "dwmapi.dll"), "old dwmapi");
        var zipPath = CreateZip(("dwmapi.dll", "new dwmapi"), ("ue4ss/LICENSE", "MIT"), ("ue4ss/UE4SS.dll", "new dll"));

        // Readable (so the backup can copy it), but not replaceable.
        using (new FileStream(dllPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAnyAsync<IOException>(() =>
                _service.InstallOrUpdateAsync(_fakeContentPath, zipPath, _backupDirectory));
        }

        Assert.Equal("old dwmapi", await File.ReadAllTextAsync(Path.Combine(Win64Folder, "dwmapi.dll")));
        Assert.Equal("old license", await File.ReadAllTextAsync(Path.Combine(LoaderFolder, "LICENSE")));
        Assert.Equal("old dll", await File.ReadAllTextAsync(dllPath));
        Assert.Empty(Directory.GetFiles(Win64Folder, "*.tmp", SearchOption.AllDirectories));
    }

    /// <summary>
    /// Regression: Mods\shared\ (UE4SS's own Lua library) was treated like a mod and kept, so an
    /// update from 3.0.1 left the old UEHelpers.lua and mods calling the newer helpers failed to load.
    /// </summary>
    [Fact]
    public async Task InstallOrUpdateAsync_UpdatesUe4ssOwnLuaLibrary_ButNotTheModsOrThePlayersOwnFiles()
    {
        var helpers = Path.Combine(LoaderFolder, "Mods", "shared", "UEHelpers", "UEHelpers.lua");
        var ownLib = Path.Combine(LoaderFolder, "Mods", "shared", "MyLib", "lib.lua");
        var keybinds = Path.Combine(LoaderFolder, "Mods", "Keybinds", "Scripts", "main.lua");
        var modsTxt = Path.Combine(LoaderFolder, "Mods", "mods.txt");
        Seed(helpers, "-- 3.0.1 helpers");
        Seed(ownLib, "mine");
        Seed(keybinds, "my keybinds");
        Seed(modsTxt, "Keybinds : 1");

        await _service.InstallOrUpdateAsync(_fakeContentPath, CreateFakeDevBuildZip(("ue4ss/Mods/shared/UEHelpers/UEHelpers.lua", "-- 1181 helpers")), _backupDirectory);

        Assert.Equal("-- 1181 helpers", await File.ReadAllTextAsync(helpers));
        Assert.Equal("mine", await File.ReadAllTextAsync(ownLib));
        Assert.Equal("my keybinds", await File.ReadAllTextAsync(keybinds));
        Assert.Equal("Keybinds : 1", await File.ReadAllTextAsync(modsTxt));
    }

    /// <summary>The same for the flat 3.0.1 layout, where Mods\ sits at the zip's root.</summary>
    [Fact]
    public async Task InstallOrUpdateAsync_FlatLayout_UpdatesUe4ssOwnLuaLibrary()
    {
        var helpers = Path.Combine(LoaderFolder, "Mods", "shared", "UEHelpers", "UEHelpers.lua");
        Seed(helpers, "-- old helpers");
        var zipPath = CreateZip(("dwmapi.dll", "new dwmapi"), ("UE4SS.dll", "new dll"), ("Mods/shared/UEHelpers/UEHelpers.lua", "-- new helpers"));

        await _service.InstallOrUpdateAsync(_fakeContentPath, zipPath, _backupDirectory);

        Assert.Equal("-- new helpers", await File.ReadAllTextAsync(helpers));
    }

    [Fact]
    public async Task InstallOrUpdateAsync_DevBuildLayout_InstallsTheUe4ssFolderAndKeepsTheUsersSettingsAndMods()
    {
        Directory.CreateDirectory(Path.Combine(LoaderFolder, "Mods", "MyMod"));
        await File.WriteAllTextAsync(Path.Combine(LoaderFolder, "UE4SS-settings.ini"), "EnableAutoReloadingLuaMods = 1");
        await File.WriteAllTextAsync(Path.Combine(LoaderFolder, "Mods", "mods.txt"), "MyMod : 1");
        await File.WriteAllTextAsync(Path.Combine(LoaderFolder, "UE4SS.log"), "UE4SS - v3.0.1 Beta #0 - Git SHA #1c1a1497");

        await _service.InstallOrUpdateAsync(_fakeContentPath, CreateFakeDevBuildZip(), _backupDirectory, "3.0.1", "83330635");

        Assert.Equal("dev dwmapi bytes", await File.ReadAllTextAsync(Path.Combine(Win64Folder, "dwmapi.dll")));
        Assert.Equal("dev UE4SS.dll bytes", await File.ReadAllTextAsync(Path.Combine(LoaderFolder, "UE4SS.dll")));
        Assert.True(File.Exists(Path.Combine(LoaderFolder, "UE4SS_SDK_Backends", "UE4SS.json")));
        Assert.True(File.Exists(Path.Combine(LoaderFolder, "Mods", "Keybinds", "Scripts", "main.lua")));
        Assert.False(Directory.Exists(Path.Combine(LoaderFolder, "ue4ss"))); // not nested a level too deep
        Assert.Equal("EnableAutoReloadingLuaMods = 1", await File.ReadAllTextAsync(Path.Combine(LoaderFolder, "UE4SS-settings.ini")));
        Assert.Equal("MyMod : 1", await File.ReadAllTextAsync(Path.Combine(LoaderFolder, "Mods", "mods.txt")));
        Assert.True(Directory.Exists(Path.Combine(LoaderFolder, "Mods", "MyMod")));

        // The old build's log is gone; the new build is known straight away.
        var status = _service.GetStatus(_fakeContentPath);
        Assert.Equal(("3.0.1", "83330635"), (status.InstalledVersion, status.InstalledCommit));
    }

    [Fact]
    public async Task GetStatus_ReadsTheBuildsCommitFromTheLog()
    {
        await _service.InstallOrUpdateAsync(_fakeContentPath, CreateFakeDevBuildZip(), _backupDirectory);
        await File.WriteAllTextAsync(Path.Combine(LoaderFolder, "UE4SS.log"), "[x] Console created\n[x] UE4SS - v3.0.1 Beta #0 - Git SHA #1c1a1497\n");

        var status = _service.GetStatus(_fakeContentPath);

        Assert.True(status.IsInstalled);
        Assert.Equal(("3.0.1", "1c1a1497"), (status.InstalledVersion, status.InstalledCommit));
    }

    [Fact]
    public void ListNativeMods_FindsModsWithTheirOwnDll()
    {
        Directory.CreateDirectory(Path.Combine(LoaderFolder, "Mods", "NoStability", "dlls"));
        File.WriteAllText(Path.Combine(LoaderFolder, "Mods", "NoStability", "dlls", "main.dll"), "x");
        Directory.CreateDirectory(Path.Combine(LoaderFolder, "Mods", "MiniMapa", "Scripts"));

        Assert.Equal(["NoStability"], _service.ListNativeMods(_fakeContentPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
