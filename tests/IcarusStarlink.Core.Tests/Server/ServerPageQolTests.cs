using IcarusStarlink.Core.Server;

namespace IcarusStarlink.Core.Tests.Server;

/// <summary>The small checks behind the Server page's polish: log line colours, the admin list hint, world details.</summary>
public sealed class ServerPageQolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("[2026.10.15-20.11.19:000][  0]LogIcarus: Error: Failed to load prospect", ServerLogLevel.Error)]
    [InlineData("Fatal error: [File:D:/build/Engine.cpp]", ServerLogLevel.Error)]
    [InlineData("[2026.10.15-20.11.19:000][  0]LogPakFile: Warning: Pak is not signed", ServerLogLevel.Warning)]
    [InlineData("[2026.10.15-20.11.41:000][  0]LogNet: Join succeeded: James", ServerLogLevel.Normal)]
    [InlineData("LogIcarus: Display: no Error: here without the category colon", ServerLogLevel.Normal)]
    [InlineData("", ServerLogLevel.Normal)]
    [InlineData(null, ServerLogLevel.Normal)]
    public void LevelOf_ColoursErrorsAndWarningsOnly(string? line, ServerLogLevel expected) =>
        Assert.Equal(expected, ServerLogFilter.LevelOf(line));

    [Theory]
    [InlineData("76561198000000000", true)]
    [InlineData("7656119800000000", false)]   // 16 digits
    [InlineData("765611980000000000", false)] // 18 digits
    [InlineData("12345678901234567", false)]  // 17 digits, not a Steam account id
    [InlineData("7656119800000000x", false)]
    [InlineData("James", false)]
    public void IsSteam64Id_Needs17DigitsStarting7656(string text, bool expected) =>
        Assert.Equal(expected, ServerAccessLists.IsSteam64Id(text));

    [Fact]
    public void InvalidAdminLines_NamesTheBadLinesByNumber_AndSkipsBlanks()
    {
        var bad = ServerAccessLists.InvalidAdminLines("76561198000000000\r\n\r\n  James  \n76561198000000001\nabc");

        Assert.Equal([(3, "James"), (5, "abc")], bad);
        Assert.Empty(ServerAccessLists.InvalidAdminLines(""));
        Assert.Empty(ServerAccessLists.InvalidAdminLines(null));
    }

    [Fact]
    public void ListProspectFiles_NewestFirst_WithDateAndSize()
    {
        var folder = DedicatedServerLayout.ProspectsFolder(_root, null);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Old.json"), "12345");
        File.WriteAllText(Path.Combine(folder, "New.json"), "1");
        File.WriteAllText(Path.Combine(folder, "New.json.backup"), "ignored");
        File.SetLastWriteTimeUtc(Path.Combine(folder, "Old.json"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(folder, "New.json"), new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        var files = DedicatedServerLayout.ListProspectFiles(_root, null);

        Assert.Equal(["New", "Old"], files.Select(f => f.Name));
        Assert.Equal(5, files[1].SizeBytes);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), files[1].ModifiedUtc);
        Assert.Empty(DedicatedServerLayout.ListProspectFiles(Path.Combine(_root, "nothing"), null));
    }
}
