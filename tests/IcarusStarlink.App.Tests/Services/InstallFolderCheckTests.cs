using System.IO;
using IcarusStarlink.App.Services;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>
/// Started from a folder Windows won't let it write to (Program Files, Controlled folder access),
/// Starlink died at launch with a crash box saying a report was written, which it couldn't be.
/// Startup now checks the folder first and says what to do.
/// </summary>
public sealed class InstallFolderCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    public InstallFolderCheckTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void CanWrite_UnderAFile_IsFalse()
    {
        var file = Path.Combine(_root, "not-a-folder");
        File.WriteAllText(file, "x");

        Assert.False(InstallFolderCheck.CanWrite(Path.Combine(file, "Logs")));
    }

    [Fact]
    public void CanWrite_ANewFolder_IsTrue_AndLeavesNothing()
    {
        var folder = Path.Combine(_root, "Logs");

        Assert.True(InstallFolderCheck.CanWrite(folder));

        Assert.Empty(Directory.GetFileSystemEntries(folder));
    }
}
