using System.IO;
using IcarusStarlink.Core.IO;

namespace IcarusStarlink.Core.Tests.IO;

public sealed class DirectoriesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DirectoriesTests_" + Guid.NewGuid().ToString("N"));

    public DirectoriesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void TryDelete_MissingFolder_ReturnsTrue()
    {
        Assert.True(Directories.TryDelete(Path.Combine(_root, "not-there")));
    }

    [Fact]
    public void TryDelete_FolderWithFiles_RemovesItAndReturnsTrue()
    {
        var folder = Path.Combine(_root, "scratch");
        Directory.CreateDirectory(Path.Combine(folder, "inner"));
        File.WriteAllText(Path.Combine(folder, "inner", "a.txt"), "a");

        Assert.True(Directories.TryDelete(folder));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void TryDelete_LockedFile_ReturnsFalseTellsTheCallerOnceAndDoesNotThrow()
    {
        var folder = Path.Combine(_root, "locked");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "held.txt");
        File.WriteAllText(file, "held");
        var failures = new List<Exception>();

        bool result;
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = Directories.TryDelete(folder, failures.Add);
        }

        Assert.False(result);
        var failure = Assert.Single(failures);
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.True(File.Exists(file));
    }
}
