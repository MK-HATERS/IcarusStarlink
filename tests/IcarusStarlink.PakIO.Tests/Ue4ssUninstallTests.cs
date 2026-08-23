using IcarusStarlink.PakIO.Install;

namespace IcarusStarlink.PakIO.Tests;

/// <summary>
/// Full UE4SS uninstall — the highest-risk action in the app, so these tests encode its safety
/// contract directly: user mods are preserved before anything is deleted, classification errs
/// toward "user-added", and the whole install is backed up.
/// </summary>
public sealed class Ue4ssUninstallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests", $"Ue4ssUn_{Guid.NewGuid():N}");
    private readonly string _contentPath;
    private readonly string _stagingDirectory;
    private readonly string _backupDirectory;
    private readonly Ue4ssLoaderInstallService _service = new();

    public Ue4ssUninstallTests()
    {
        // Mirrors the real layout: <game>\Icarus\Content beside <game>\Icarus\Binaries\Win64.
        _contentPath = Path.Combine(_root, "Icarus", "Content");
        _stagingDirectory = Path.Combine(_root, "Staged_UE4SS");
        _backupDirectory = Path.Combine(_root, "Backups");
        Directory.CreateDirectory(_contentPath);
        Directory.CreateDirectory(_stagingDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Win64 => Path.Combine(_root, "Icarus", "Binaries", "Win64");

    private string ModsFolder => Path.Combine(Win64, "ue4ss", "Mods");

    /// <summary>Builds an install shaped like the user's real one: dwmapi + ue4ss folder, framework mods listed in mods.json, user mods not.</summary>
    private void CreateInstall(string[] frameworkMods, string[] userMods)
    {
        Directory.CreateDirectory(ModsFolder);
        File.WriteAllText(Path.Combine(Win64, "dwmapi.dll"), "hook");
        File.WriteAllText(Path.Combine(Win64, "ue4ss", "UE4SS.dll"), "loader");
        File.WriteAllText(Path.Combine(Win64, "ue4ss", "UE4SS-settings.ini"), "settings");
        Directory.CreateDirectory(Path.Combine(ModsFolder, "shared"));

        foreach (var mod in frameworkMods.Concat(userMods))
        {
            Directory.CreateDirectory(Path.Combine(ModsFolder, mod));
            File.WriteAllText(Path.Combine(ModsFolder, mod, "main.lua"), $"-- {mod}");
        }

        var manifest = "[" + string.Join(",", frameworkMods.Select(m => $$"""{"mod_name":"{{m}}","mod_enabled":true}""")) + "]";
        File.WriteAllText(Path.Combine(ModsFolder, "mods.json"), manifest);
    }

    [Fact]
    public void ListUserAddedMods_TellsUserModsApartFromFrameworkOnes()
    {
        CreateInstall(frameworkMods: ["ConsoleEnablerMod", "Keybinds"], userMods: ["NearbyCrafting", "IcarusStutterFix"]);

        var userMods = _service.ListUserAddedMods(_contentPath);

        Assert.Equal(["IcarusStutterFix", "NearbyCrafting"], userMods);
    }

    [Fact]
    public void ListUserAddedMods_SharedInfrastructureFolderIsNeverUserAdded()
    {
        CreateInstall(frameworkMods: ["Keybinds"], userMods: []);

        Assert.Empty(_service.ListUserAddedMods(_contentPath));
    }

    [Fact]
    public async Task Uninstall_RemovesLoaderAndDwmapiCompletely()
    {
        CreateInstall(frameworkMods: ["Keybinds"], userMods: []);

        await _service.UninstallAsync(_contentPath, _stagingDirectory, _backupDirectory);

        Assert.False(File.Exists(Path.Combine(Win64, "dwmapi.dll")));
        Assert.False(Directory.Exists(Path.Combine(Win64, "ue4ss")));
        // The game's own folder structure survives — only UE4SS's two footprints go.
        Assert.True(Directory.Exists(Win64));
    }

    [Fact]
    public async Task Uninstall_MovesUserModsToStagingBeforeDeleting()
    {
        CreateInstall(frameworkMods: ["Keybinds"], userMods: ["NearbyCrafting"]);

        var result = await _service.UninstallAsync(_contentPath, _stagingDirectory, _backupDirectory);

        Assert.Equal(["NearbyCrafting"], result.PreservedUserMods);
        Assert.True(File.Exists(Path.Combine(_stagingDirectory, "NearbyCrafting", "main.lua")));
    }

    [Fact]
    public async Task Uninstall_UserModAlreadyInStaging_GetsASuffixInsteadOfColliding()
    {
        CreateInstall(frameworkMods: [], userMods: ["NearbyCrafting"]);
        Directory.CreateDirectory(Path.Combine(_stagingDirectory, "NearbyCrafting"));
        File.WriteAllText(Path.Combine(_stagingDirectory, "NearbyCrafting", "main.lua"), "existing staged copy");

        var result = await _service.UninstallAsync(_contentPath, _stagingDirectory, _backupDirectory);

        Assert.Equal(["NearbyCrafting_2"], result.PreservedUserMods);
        Assert.Equal("existing staged copy", File.ReadAllText(Path.Combine(_stagingDirectory, "NearbyCrafting", "main.lua")));
        Assert.True(File.Exists(Path.Combine(_stagingDirectory, "NearbyCrafting_2", "main.lua")));
    }

    [Fact]
    public async Task Uninstall_MalformedModsJson_TreatsEveryModAsUserAddedRatherThanDeletingIt()
    {
        // The deliberate failure direction: unable to classify -> preserve. Worst case framework
        // mods survive in staging; a user's mod is never deleted on a bad manifest.
        CreateInstall(frameworkMods: [], userMods: ["SomeMod"]);
        File.WriteAllText(Path.Combine(ModsFolder, "mods.json"), "{corrupt");

        var result = await _service.UninstallAsync(_contentPath, _stagingDirectory, _backupDirectory);

        Assert.Contains("SomeMod", result.PreservedUserMods);
        Assert.True(Directory.Exists(Path.Combine(_stagingDirectory, "SomeMod")));
    }

    [Fact]
    public async Task Uninstall_BacksUpTheWholeInstallIncludingFrameworkMods()
    {
        CreateInstall(frameworkMods: ["Keybinds"], userMods: []);

        var result = await _service.UninstallAsync(_contentPath, _stagingDirectory, _backupDirectory);

        var backups = Directory.GetDirectories(result.BackupPath);
        Assert.NotEmpty(backups);
        var backedUpLoader = backups.FirstOrDefault(b => File.Exists(Path.Combine(b, "UE4SS.dll")));
        Assert.NotNull(backedUpLoader);
        Assert.True(File.Exists(Path.Combine(backedUpLoader, "Mods", "Keybinds", "main.lua")));
    }
}
