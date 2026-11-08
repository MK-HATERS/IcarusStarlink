using System.IO.Compression;
using IcarusStarlink.Core.Saves;
using IcarusStarlink.Core.Steam;
using IcarusStarlink.Storage.Saves;

namespace IcarusStarlink.Storage.Tests.Saves;

/// <summary>
/// The game's own rolling copies of the player files (Profile.json.backup, .backup_1 … _10):
/// listed newest first, put back under the real name by copying (never renaming), after a
/// pre_restore zip, and refused when the game rotated them since the list was read. Synthetic slot only.
/// </summary>
public sealed class SaveRepositoryGameBackupsTests : IDisposable
{
    private const string SteamId = "76561198000000001";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests", $"GameBackups_{Guid.NewGuid():N}");
    private readonly string _slot;
    private readonly string _backups;
    private readonly SaveRepository _repository;

    public SaveRepositoryGameBackupsTests()
    {
        var playerData = Path.Combine(_root, "PlayerData");
        _slot = Path.Combine(playerData, SteamId);
        _backups = Path.Combine(_root, "save_backups");
        Directory.CreateDirectory(_slot);
        _repository = new SaveRepository(playerData, _backups, new FakeLocator());
        File.WriteAllText(Path.Combine(_slot, "Profile.json"), """{ "UserID": "now", "Credits": 900 }""");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string WriteCopy(string name, string text, DateTime writtenUtc)
    {
        var path = Path.Combine(_slot, name);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }

    [Fact]
    public void ListGameBackups_ListsTheGamesCopiesOfPlayerFiles_NewestFirst()
    {
        var now = DateTime.UtcNow;
        WriteCopy("Profile.json.backup_3", """{ "UserID": "three" }""", now.AddHours(-3));
        WriteCopy("Profile.json.backup", """{ "UserID": "newest" }""", now.AddHours(-1));
        WriteCopy("Characters.json.backup_1", """{ "Characters.json": [] }""", now.AddHours(-2));
        // Not the game's copies, or not a player file: left out.
        WriteCopy("Profile.json.Bak", "{}", now);
        WriteCopy("Profile.json.backup_old", "{}", now);
        WriteCopy("Something.json.backup", "{}", now);

        var listed = _repository.ListGameBackups(SteamId);

        Assert.Equal(["Profile.json.backup", "Characters.json.backup_1", "Profile.json.backup_3"], listed.Select(c => c.FileName));
        Assert.Equal("Profile.json", listed[0].OriginalFileName);
        Assert.Equal(0, listed[0].Number);
        Assert.Equal(3, listed[2].Number);
    }

    [Fact]
    public void RestoreGameBackup_WritesTheCopysBytes_AfterAPreRestoreZip_AndKeepsTheCopy()
    {
        var copyText = """{ "UserID": "three", "Credits": 5 }""";
        var copyPath = WriteCopy("Profile.json.backup_3", copyText, DateTime.UtcNow.AddDays(-2));
        var copy = Assert.Single(_repository.ListGameBackups(SteamId));
        var before = DateTime.Now.AddSeconds(-5);

        var preRestore = _repository.RestoreGameBackup(SteamId, copy);

        var profile = Path.Combine(_slot, "Profile.json");
        Assert.Equal(copyText, File.ReadAllText(profile));
        // Never renamed: the game's copy is still there, unchanged.
        Assert.Equal(copyText, File.ReadAllText(copyPath));
        // Touched to now, so Steam Cloud keeps it.
        Assert.True(File.GetLastWriteTime(profile) >= before);
        // Starlink's own backup of the file as it was, first.
        Assert.Contains("pre_restore", Path.GetFileName(preRestore));
        using var zip = ZipFile.OpenRead(preRestore);
        using var reader = new StreamReader(zip.GetEntry("Profile.json")!.Open());
        Assert.Contains("900", reader.ReadToEnd());
    }

    [Fact]
    public void RestoreGameBackup_CopyChangedSinceListed_RefusesAndChangesNothing()
    {
        var copyPath = WriteCopy("Profile.json.backup_3", """{ "UserID": "three" }""", DateTime.UtcNow.AddDays(-2));
        var copy = Assert.Single(_repository.ListGameBackups(SteamId));
        // The game saved again and shifted its copies along.
        WriteCopy("Profile.json.backup_3", """{ "UserID": "shifted" }""", DateTime.UtcNow.AddDays(-1));

        Assert.Throws<InvalidDataException>(() => _repository.RestoreGameBackup(SteamId, copy));

        Assert.Contains("900", File.ReadAllText(Path.Combine(_slot, "Profile.json")));
        Assert.True(File.Exists(copyPath));
        Assert.False(Directory.Exists(_backups) && Directory.GetFiles(_backups).Length > 0);
    }

    [Fact]
    public void RestoreGameBackup_CopyIsNotJson_RefusesAndChangesNothing()
    {
        WriteCopy("Profile.json.backup_2", "half a file {", DateTime.UtcNow.AddDays(-2));
        var copy = Assert.Single(_repository.ListGameBackups(SteamId));

        Assert.Throws<InvalidDataException>(() => _repository.RestoreGameBackup(SteamId, copy));

        Assert.Contains("900", File.ReadAllText(Path.Combine(_slot, "Profile.json")));
    }

    [Fact]
    public void RestoreGameBackup_CopyFromAnotherFolder_Refused()
    {
        var elsewhere = Path.Combine(_root, "Elsewhere");
        Directory.CreateDirectory(elsewhere);
        var path = Path.Combine(elsewhere, "Profile.json.backup");
        File.WriteAllText(path, "{}");
        var info = new FileInfo(path);
        var copy = new GameBackupCopy(path, "Profile.json", 0, info.LastWriteTimeUtc, info.Length);

        Assert.Throws<InvalidDataException>(() => _repository.RestoreGameBackup(SteamId, copy));

        Assert.Contains("900", File.ReadAllText(Path.Combine(_slot, "Profile.json")));
    }

    [Theory]
    [InlineData("Profile.json.backup", true, "Profile.json", 0)]
    [InlineData("My World.json.backup_10", true, "My World.json", 10)]
    [InlineData("Profile.json.backup_", false, "", 0)]
    [InlineData("Profile.json.backup_x", false, "", 0)]
    [InlineData("Profile.json.Bak", false, "", 0)]
    [InlineData(".backup", false, "", 0)]
    public void TryParseName_ReadsTheGamesNames(string name, bool ok, string original, int number)
    {
        Assert.Equal(ok, GameBackupCopy.TryParseName(name, out var o, out var n));
        Assert.Equal(original, o);
        Assert.Equal(number, n);
    }

    private sealed class FakeLocator : ISteamInstallLocator
    {
        public string? FindIcarusContentPath() => null;

        public string? TryGetPersonaName(string steamId64) => null;
    }
}
