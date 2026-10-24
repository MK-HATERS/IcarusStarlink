using IcarusStarlink.PakIO.Install;

namespace IcarusStarlink.PakIO.Tests.Install;

/// <summary>User request 2026-10-24: the Library says when a mod changed since the merge in the game was built.</summary>
public sealed class MergedModFingerprintsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests", $"Fingerprints_{Guid.NewGuid():N}");

    public MergedModFingerprintsTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void HashFolder_ChangesWithAnyFile_AndNotOtherwise()
    {
        var mod = Path.Combine(_root, "Mod");
        Write("Mod/mod.EXMOD", "a");
        var before = MergedModFingerprints.HashFolder(mod);

        Assert.Equal(before, MergedModFingerprints.HashFolder(mod));
        Write("Mod/mod.EXMOD", "b");
        Assert.NotEqual(before, MergedModFingerprints.HashFolder(mod));
        Assert.Equal("", MergedModFingerprints.HashFolder(Path.Combine(_root, "Gone")));
    }

    [Fact]
    public void ForInstalledPak_FindsTheBuildWhosePakIsInTheGame_ElseNull()
    {
        var record = Path.Combine(_root, "Staged", MergedModFingerprints.FileName);
        var oldPak = Write("old.pak", "old build");
        var newPak = Write("new.pak", "new build");
        MergedModFingerprints.Add(record, new MergedBuildRecord(MergedModFingerprints.HashFile(oldPak), DateTimeOffset.UtcNow, [new("A", "1.0", "h1")]));
        MergedModFingerprints.Add(record, new MergedBuildRecord(MergedModFingerprints.HashFile(newPak), DateTimeOffset.UtcNow, [new("A", "1.1", "h2")]));

        // Restore previous install put the old pak back: its own record is used.
        var installed = Write("game/ISL-Merged.pak", "old build");
        Assert.Equal("1.0", MergedModFingerprints.ForInstalledPak(record, installed)!.Mods.Single().Version);

        // A pak this app has no record of: nothing to compare against.
        File.WriteAllText(installed, "built elsewhere");
        Assert.Null(MergedModFingerprints.ForInstalledPak(record, installed));
    }
}
