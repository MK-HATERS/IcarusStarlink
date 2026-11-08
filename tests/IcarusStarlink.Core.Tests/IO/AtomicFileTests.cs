using System.Diagnostics;
using System.IO;
using IcarusStarlink.Core.IO;

namespace IcarusStarlink.Core.Tests.IO;

/// <summary>The one safe-write path: a target another program holds for a moment is waited for, a lasting hold or a read-only file still fails and keeps the old file.</summary>
public sealed class AtomicFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AtomicFileTests_" + Guid.NewGuid().ToString("N"));

    public AtomicFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Target() => Path.Combine(_root, "settings.json");

    private string[] TempFiles() => Directory.GetFiles(_root, "*.tmp");

    [Fact]
    public async Task WriteAllText_TargetHeldForAMoment_StillWrites()
    {
        // Regression: OneDrive or a scanner holding the previous save for a moment used to fail the
        // save, because the final move was tried only once.
        var path = Target();
        File.WriteAllText(path, "old");
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(async () =>
        {
            await Task.Delay(150);
            held.Dispose();
        });

        try
        {
            AtomicFile.WriteAllText(path, "new");
        }
        finally
        {
            await release;
        }

        Assert.Equal("new", File.ReadAllText(path));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public void WriteAllText_TargetHeldThroughout_ThrowsAndKeepsTheOldFile()
    {
        var path = Target();
        File.WriteAllText(path, "old");

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<SystemException>(() => AtomicFile.WriteAllText(path, "new"));
        }

        Assert.Equal("old", File.ReadAllText(path));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public void WriteAllText_ReadOnlyTarget_FailsWithoutWaiting()
    {
        // A read-only file never clears by itself, so it isn't retried.
        var path = Target();
        File.WriteAllText(path, "old");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        var clock = Stopwatch.StartNew();
        Assert.ThrowsAny<SystemException>(() => AtomicFile.WriteAllText(path, "new"));
        clock.Stop();

        Assert.True(clock.ElapsedMilliseconds < 300, $"took {clock.ElapsedMilliseconds} ms");
        Assert.Equal("old", File.ReadAllText(path));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public void WriteAllBytes_NewFileInAMissingFolder_IsCreated()
    {
        var path = Path.Combine(_root, "inner", "data.bin");

        AtomicFile.WriteAllBytes(path, [1, 2, 3]);

        Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
    }
}
