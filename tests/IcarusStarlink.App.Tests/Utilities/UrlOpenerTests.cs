using System.IO;
using IcarusStarlink.App.Utilities;

namespace IcarusStarlink.App.Tests.Utilities;

public sealed class UrlOpenerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "StarlinkUrlOpenerTests_" + Guid.NewGuid().ToString("N"));

    public UrlOpenerTests() => Directory.CreateDirectory(_root);

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
    public void TryOpen_FolderThatIsGone_SaysTheFolderIsMissing()
    {
        // The header's UE4SS button on a game without UE4SS: the Mods folder doesn't exist yet.
        var missing = Path.Combine(_root, "ue4ss", "Mods");

        var error = UrlOpener.TryOpen(missing);

        var notFound = Assert.IsType<DirectoryNotFoundException>(error);
        Assert.Contains(missing, notFound.Message);
    }
}
