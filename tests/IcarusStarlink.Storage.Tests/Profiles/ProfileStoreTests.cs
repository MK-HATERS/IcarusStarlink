using IcarusStarlink.Core.Profiles;
using IcarusStarlink.Storage.Profiles;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcarusStarlink.Storage.Tests.Profiles;

public class ProfileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _backupsDir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N") + "_Backups");

    private ProfileStore CreateStore() => new(_dir, _backupsDir, NullLogger<ProfileStore>.Instance);

    [Fact]
    public void ProfileNames_NoProfilesYet_IsEmpty()
    {
        Assert.Empty(CreateStore().ProfileNames);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsMergeQueueFolderNames()
    {
        var store = CreateStore();
        store.Save(new Profile { Name = "Main", MergeQueueFolderNames = ["ModA", "ModB"] });

        var loaded = CreateStore().Load("Main");

        Assert.Equal(["ModA", "ModB"], loaded.MergeQueueFolderNames);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsGameplayOptions()
    {
        var store = CreateStore();
        var options = new GameplayOptions
        {
            SpeedBoost = BoostLevel.Level2,
            CraftCost = CraftCostReduction.TwentyFivePercent,
            StacksMultiplier = 3.5,
            RemoveWeight = true,
        };
        store.Save(new Profile { Name = "Main", MergeQueueFolderNames = [], Options = options });

        var loaded = CreateStore().Load("Main");

        Assert.Equal(BoostLevel.Level2, loaded.Options.SpeedBoost);
        Assert.Equal(CraftCostReduction.TwentyFivePercent, loaded.Options.CraftCost);
        Assert.Equal(3.5, loaded.Options.StacksMultiplier);
        Assert.True(loaded.Options.RemoveWeight);
    }

    [Fact]
    public void Save_PersistsAcrossStoreInstances()
    {
        CreateStore().Save(new Profile { Name = "Main", MergeQueueFolderNames = [] });

        Assert.Equal(["Main"], CreateStore().ProfileNames);
    }

    [Fact]
    public void Save_SameNameTwice_OverwritesRatherThanDuplicating()
    {
        var store = CreateStore();
        store.Save(new Profile { Name = "Main", MergeQueueFolderNames = ["ModA"] });
        store.Save(new Profile { Name = "Main", MergeQueueFolderNames = ["ModB"] });

        Assert.Single(store.ProfileNames);
        Assert.Equal(["ModB"], store.Load("Main").MergeQueueFolderNames);
    }

    [Fact]
    public void Load_UnknownProfile_ThrowsFileNotFoundException()
    {
        Assert.Throws<FileNotFoundException>(() => CreateStore().Load("NoSuchProfile"));
    }

    [Fact]
    public void Delete_RemovesTheProfile()
    {
        var store = CreateStore();
        store.Save(new Profile { Name = "Main", MergeQueueFolderNames = [] });

        store.Delete("Main");

        Assert.Empty(store.ProfileNames);
    }

    [Fact]
    public void Delete_UnknownProfile_DoesNotThrow()
    {
        var exception = Record.Exception(() => CreateStore().Delete("NoSuchProfile"));
        Assert.Null(exception);
    }

    [Fact]
    public void Rename_UpdatesNameAndRemovesOldFile()
    {
        var store = CreateStore();
        store.Save(new Profile { Name = "Main", MergeQueueFolderNames = ["ModA"] });

        store.Rename("Main", "Weekend Build");

        Assert.Equal(["Weekend Build"], store.ProfileNames);
        Assert.Equal(["ModA"], store.Load("Weekend Build").MergeQueueFolderNames);
        Assert.Throws<FileNotFoundException>(() => store.Load("Main"));
    }

    [Fact]
    public void Rename_ToAnExistingDifferentProfileName_ThrowsWithoutModifyingEither()
    {
        var store = CreateStore();
        store.Save(new Profile { Name = "Main", MergeQueueFolderNames = ["ModA"] });
        store.Save(new Profile { Name = "Backup", MergeQueueFolderNames = ["ModB"] });

        Assert.Throws<InvalidOperationException>(() => store.Rename("Main", "Backup"));
        Assert.Equal(["ModA"], store.Load("Main").MergeQueueFolderNames);
        Assert.Equal(["ModB"], store.Load("Backup").MergeQueueFolderNames);
    }

    [Fact]
    public void ResolvePath_NameWithInvalidFileNameCharacter_ThrowsArgumentException()
    {
        var store = CreateStore();

        Assert.Throws<ArgumentException>(() => store.Save(new Profile { Name = "Weird/Name", MergeQueueFolderNames = [] }));
    }

    [Fact]
    public void Save_FirstTimeForAName_NoBackupIsMade()
    {
        // Nothing existed under this name before, so there's nothing to protect — matches
        // FolderBackup.BackupFile's own no-op-when-source-doesn't-exist-yet contract.
        CreateStore().Save(new Profile { Name = "Main", MergeQueueFolderNames = ["ModA"] });

        Assert.False(Directory.Exists(_backupsDir) && Directory.EnumerateFileSystemEntries(_backupsDir).Any());
    }

    [Fact]
    public void Save_OverwritingAnExistingProfile_BacksUpThePreviousContentFirst()
    {
        var store = CreateStore();
        store.Save(new Profile { Name = "Main", MergeQueueFolderNames = ["ModA"] });

        store.Save(new Profile { Name = "Main", MergeQueueFolderNames = ["ModB"] });

        var backupFile = Assert.Single(Directory.GetFiles(_backupsDir, "Main_*.json"));
        var backedUp = System.Text.Json.JsonSerializer.Deserialize<Profile>(File.ReadAllText(backupFile))!;
        Assert.Equal(["ModA"], backedUp.MergeQueueFolderNames);
        // The live file already reflects the new save — the backup is a copy, not a swap.
        Assert.Equal(["ModB"], store.Load("Main").MergeQueueFolderNames);
    }

    [Fact]
    public void Save_RepeatedlyPastTheCap_KeepsOnlyTheMostRecentBackups()
    {
        var store = CreateStore();
        for (var i = 0; i < 6; i++)
        {
            store.Save(new Profile { Name = "Main", MergeQueueFolderNames = [$"Mod{i}"] });
        }

        // 6 saves => 5 backups taken (the very first save had nothing to back up yet) => capped
        // at MaxProfileBackups (3), the smaller-than-FolderBackup's-usual-5 cap the user asked for
        // ("a couple of backups") since a profile is small, frequently-saved editor state, not a
        // rare real-game-folder write.
        Assert.Equal(3, Directory.GetFiles(_backupsDir, "Main_*.json").Length);
    }

    [Fact]
    public void Save_TwoDifferentProfiles_BackupsDoNotCrossContaminate()
    {
        var store = CreateStore();
        store.Save(new Profile { Name = "Main", MergeQueueFolderNames = ["ModA"] });
        store.Save(new Profile { Name = "Weekend", MergeQueueFolderNames = ["ModX"] });

        store.Save(new Profile { Name = "Main", MergeQueueFolderNames = ["ModA2"] });
        store.Save(new Profile { Name = "Weekend", MergeQueueFolderNames = ["ModX2"] });

        Assert.Single(Directory.GetFiles(_backupsDir, "Main_*.json"));
        Assert.Single(Directory.GetFiles(_backupsDir, "Weekend_*.json"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }

        if (Directory.Exists(_backupsDir))
        {
            Directory.Delete(_backupsDir, recursive: true);
        }
    }
}
