using System.IO;
using IcarusStarlink.App.Services;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>
/// Scratch folders in %TEMP% are cleaned in finally blocks, so a crash, a kill or a power cut left
/// them (Rebuild_* holds a copy of every queued mod) forever. The startup sweep removes day-old
/// ones by their exact names and nothing else.
/// </summary>
public sealed class TempScratchSweeperTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
    private readonly DateTime _now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    public TempScratchSweeperTests() => Directory.CreateDirectory(Path.Combine(_temp, "IcarusStarlink"));

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    private static string Guid32() => Guid.NewGuid().ToString("N");

    private string Folder(string relativePath, TimeSpan age)
    {
        var path = Path.Combine(_temp, relativePath);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "inside.txt"), "x");
        Directory.SetCreationTimeUtc(path, _now - age);
        return path;
    }

    private string FileAt(string relativePath, TimeSpan age)
    {
        var path = Path.Combine(_temp, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        File.SetCreationTimeUtc(path, _now - age);
        return path;
    }

    [Fact]
    public void Sweep_RemovesOnlyDayOldScratchWithStarlinksOwnNames()
    {
        var twoDays = TimeSpan.FromDays(2);
        var oldRebuild = Folder($"IcarusStarlink\\Rebuild_{Guid32()}", twoDays);
        var oldUpdate = Folder($"IcarusStarlink\\AppUpdate_{Guid32()}", twoDays);
        var oldImport = Folder($"IcarusStarlink_Import_{Guid32()}", twoDays);
        var oldCurve = Folder($"IcarusStarlink\\CurveWriter\\{Guid32()}", twoDays);
        var oldResponse = FileAt($"IcarusStarlink\\Response_{Guid32()}.txt", twoDays);
        var oldAudio = FileAt($"IcarusStarlink_AudioPreview_{Guid32()}.wav", twoDays);
        var oldRepoint = Folder($"IcarusStarlink-repoint-{Guid32()}", twoDays);

        var newRebuild = Folder($"IcarusStarlink\\Rebuild_{Guid32()}", TimeSpan.FromMinutes(5));
        var updateBackup = Folder($"IcarusStarlink\\UpdateBackup_{Guid32()}", twoDays);
        var worldBackups = Folder("IcarusStarlink\\WorldBackups", twoDays);
        var notAGuid = Folder("IcarusStarlink_Import_notaguid", twoDays);
        var outcomeNote = FileAt("IcarusStarlink-update-outcome.txt", twoDays);
        var updaterLog = FileAt("IcarusStarlink-updater.log", twoDays);
        var ue4ssZip = FileAt($"UE4SS_{Guid32()}.zip", twoDays);
        var someoneElses = Folder($"Rebuild_{Guid32()}", twoDays);

        TempScratchSweeper.Sweep(_temp, _now, TimeSpan.FromDays(1));

        Assert.False(Directory.Exists(oldRebuild));
        Assert.False(Directory.Exists(oldUpdate));
        Assert.False(Directory.Exists(oldImport));
        Assert.False(Directory.Exists(oldCurve));
        Assert.False(File.Exists(oldResponse));
        Assert.False(File.Exists(oldAudio));
        Assert.False(Directory.Exists(oldRepoint));

        Assert.True(Directory.Exists(newRebuild), "younger than a day: may still be in use");
        Assert.True(Directory.Exists(updateBackup), "a failed update's message points to it");
        Assert.True(Directory.Exists(worldBackups));
        Assert.True(Directory.Exists(notAGuid));
        Assert.True(File.Exists(outcomeNote), "read at the next start");
        Assert.True(File.Exists(updaterLog));
        Assert.True(File.Exists(ue4ssZip), "a generic name another program could use");
        Assert.True(Directory.Exists(someoneElses), "not under IcarusStarlink");
    }

    [Fact]
    public void Sweep_ALockedFile_IsSkippedWithoutThrowing()
    {
        var twoDays = TimeSpan.FromDays(2);
        var locked = Folder($"IcarusStarlink\\Rebuild_{Guid32()}", twoDays);
        var other = Folder($"IcarusStarlink\\Peek_{Guid32()}", twoDays);

        using (new FileStream(Path.Combine(locked, "inside.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            TempScratchSweeper.Sweep(_temp, _now, TimeSpan.FromDays(1));
        }

        Assert.False(Directory.Exists(other), "one locked folder must not stop the rest");
    }

    [Fact]
    public void Sweep_AMissingTempFolder_DoesNothing()
    {
        TempScratchSweeper.Sweep(Path.Combine(_temp, "missing"), _now, TimeSpan.FromDays(1));
    }
}
