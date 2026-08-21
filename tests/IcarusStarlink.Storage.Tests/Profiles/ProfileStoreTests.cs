using IcarusStarlink.Core.Profiles;
using IcarusStarlink.Storage.Profiles;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcarusStarlink.Storage.Tests.Profiles;

public class ProfileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    private ProfileStore CreateStore() => new(_dir, NullLogger<ProfileStore>.Instance);

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

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
