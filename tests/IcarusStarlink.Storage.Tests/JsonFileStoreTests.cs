namespace IcarusStarlink.Storage.Tests;

public sealed class JsonFileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    public JsonFileStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(_dir))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void WriteAtomically_ReplacesTheFileAndLeavesNoTempFile()
    {
        var path = Path.Combine(_dir, "data.json");
        File.WriteAllText(path, "old");

        JsonFileStore.WriteAtomically(path, "new");

        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_dir));
    }

    /// <summary>A save that can't replace the target (here, a read-only file) used to leave its temp file behind on every attempt.</summary>
    [Fact]
    public void WriteAtomically_TargetCannotBeReplaced_ThrowsAndLeavesNoTempFile()
    {
        var path = Path.Combine(_dir, "data.json");
        File.WriteAllText(path, "old");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        Assert.Throws<UnauthorizedAccessException>(() => JsonFileStore.WriteAtomically(path, "new"));
        Assert.Throws<UnauthorizedAccessException>(() => JsonFileStore.WriteBytesAtomically(path, [1, 2, 3]));

        Assert.Equal("old", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_dir));
    }
}
