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

    /// <summary>
    /// The rollback story: if CopyWithRetry exhausts its retries partway through (here, simulated
    /// with a real exclusively-locked file — the same real-world condition CopyWithRetry's own
    /// retry loop exists for), every file this Apply call had already overwritten must be restored
    /// to its pre-update content, not left as a mix of old and new. Whichever order the copy loop
    /// happens to visit these files in, the end state must always be "exactly as it started" —
    /// the assertions below hold either way (see UpdateApplier.Apply's own remarks).
    /// </summary>
    [Fact]
    public void Apply_RollsBackAlreadyOverwrittenFilesWhenACopyFailsPartway()
    {
        WriteInstall("IcarusStarlink.App.exe", "old exe");
        WriteInstall("GoodFile.dll", "old good");
        WriteInstall("LockedFile.dll", "old locked");
        WriteNew("IcarusStarlink.App.exe", "new exe");
        WriteNew("GoodFile.dll", "new good");
        WriteNew("LockedFile.dll", "new locked");

        var lockedPath = Path.Combine(_installDirectory, "LockedFile.dll");
        using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Held open with zero sharing for the whole Apply call — every attempt to even read
            // this file for its own backup (let alone overwrite it) hits a real sharing violation
            // until this handle closes at the end of this using block, exactly like a file the
            // just-exited main process hasn't fully released yet.
            Assert.Throws<IOException>(() => UpdateApplier.Apply(_installDirectory, _newFilesDirectory, _ => { }));
        }

        Assert.Equal("old exe", File.ReadAllText(Path.Combine(_installDirectory, "IcarusStarlink.App.exe")));
        Assert.Equal("old good", File.ReadAllText(Path.Combine(_installDirectory, "GoodFile.dll")));
        Assert.Equal("old locked", File.ReadAllText(lockedPath));
    }

    /// <summary>
    /// Regression guard: when rollback ITSELF can't fully restore a file — the real "(same lock)"
    /// case this class's own doc comment now calls out, where the exact same stuck file that broke
    /// the forward overwrite also blocks the backward restore-copy of that same file — Apply used to
    /// just re-throw the original exception unchanged, indistinguishable from an ordinary failed
    /// update whose rollback DID fully succeed. Program.cs (which runs with no visible window at
    /// all) needs to tell those two outcomes apart to know when a user-visible notification is
    /// actually warranted. FileShare.Read (not None): the backup step only needs to READ the file
    /// (succeeds, so it lands in backedUpRelativePaths), but both the forward overwrite AND the
    /// later rollback restore need to WRITE it — blocked by the same open handle for the whole call.
    /// </summary>
    [Fact]
    public void Apply_RollbackItselfCannotRestoreAFile_ThrowsUpdateRollbackIncompleteException()
    {
        WriteInstall("IcarusStarlink.App.exe", "old exe");
        WriteInstall("LockedFile.dll", "old locked");
        WriteNew("IcarusStarlink.App.exe", "new exe");
        WriteNew("LockedFile.dll", "new locked");

        var lockedPath = Path.Combine(_installDirectory, "LockedFile.dll");
        using (new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            var ex = Assert.Throws<UpdateRollbackIncompleteException>(
                () => UpdateApplier.Apply(_installDirectory, _newFilesDirectory, _ => { }));

            Assert.True(Directory.Exists(ex.BackupDirectory));
        }
    }
}
