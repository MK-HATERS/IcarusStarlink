using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Saves;
using IcarusStarlink.PakIO.Assets;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>
/// Covers the Save editor's character duplicate/delete, mount delete, and the late,
/// immediately-before-the-write IsGameRunning re-check — the three unwired/hardening items added
/// alongside this test file. Fixtures mirror SaveRepositoryTests' own real-shape approach
/// (Storage.Tests): a character carries CharacterName/ChrSlot/XP/Cosmetic, a profile carries
/// NextChrSlot (the game's own per-profile "next slot to hand out" counter, confirmed against a
/// real save — see AllocateNextChrSlot's own doc comment on SavesViewModel).
///
/// SavesViewModel's own confirm dialogs go through IDialogService here, not the ThemedMessageBox
/// static it used to call directly for Save/Restore's own confirms — that constructs a real WPF
/// Window and blocks on ShowDialog(), which has no live Dispatcher/Application to run against in a
/// test host. Migrating those two call sites to IDialogService (alongside the brand new
/// DeleteCharacter confirm, which never had any other option) is what actually makes SaveChanges
/// and RestoreBackupAsync testable at all here — see SavesViewModel's own updated doc comments at
/// those two call sites.
/// </summary>
[Collection(MessengerCollection.Name)]
public sealed class SavesViewModelTests : IDisposable
{
    private const string SteamId = "76561198000000001";

    private readonly List<string> _tempFoldersToCleanUp = [];

    // A real, minimal, valid 1x1 PNG — same fixture LibraryItemViewModelTests already uses for the
    // same reason: the icon-resolution success path doesn't just check "bytes came back non-null",
    // it decodes them into a real BitmapImage (SavesViewModel's own TryDecodeImage) before treating
    // a row's icon as resolved.
    private static readonly byte[] MinimalPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private static JsonObject MakeCharacter(string name, int chrSlot) => new()
    {
        ["CharacterName"] = name,
        ["ChrSlot"] = chrSlot,
        ["XP"] = 100L,
        ["IsDead"] = false,
        ["Location"] = "Prospect_Grasslands",
        ["Cosmetic"] = new JsonObject { ["Customization_Head"] = 111L, ["IsMale"] = true },
    };

    private static JsonObject MakeProfile(int nextChrSlot) => new()
    {
        ["UserID"] = SteamId,
        ["MetaResources"] = new JsonArray(),
        ["UnlockedFlags"] = new JsonArray(),
        ["Talents"] = new JsonArray(),
        ["NextChrSlot"] = nextChrSlot,
    };

    private static JsonObject MakeMount(string name, int level, string type) => new()
    {
        ["MountName"] = name,
        ["MountLevel"] = level,
        ["MountType"] = type,
        ["RecorderBlob"] = "opaque-binary-stand-in",
    };

    private static JsonObject MakeMountsRoot(params JsonObject[] mounts)
    {
        var array = new JsonArray();
        foreach (var mount in mounts)
        {
            array.Add(mount);
        }

        return new JsonObject { ["SavedMounts"] = array };
    }

    private static SavesViewModel CreateViewModel(
        FakeSaveRepository repository, FakeDialogService? dialogService = null, FakeGameProcessChecker? gameProcessChecker = null,
        SaveGameNames? gameNames = null, FakeBaseGameIconDecoder? baseGameIconDecoder = null, Func<SaveModSources.Index>? readModSources = null) =>
        new(
            repository,
            new FakeActivityLog(),
            // A folder that doesn't exist — SaveGameNames degrades every table to empty rather than
            // throwing (see its own doc comment), which is exactly what a test with no real
            // extracted game data needs. A test that actually needs an item/mount/creature to carry
            // a real IconPath passes its own SaveGameNames built over CreateGameDataFolder below.
            gameNames ?? new SaveGameNames(Path.Combine(Path.GetTempPath(), $"IcarusStarlinkTests_NoGameData_{Guid.NewGuid():N}")),
            dialogService ?? new FakeDialogService(confirmResult: true),
            gameProcessChecker ?? new FakeGameProcessChecker(false),
            baseGameIconDecoder ?? new FakeBaseGameIconDecoder(),
            readModSources: readModSources);

    /// <summary>
    /// A minimal real extracted-Data folder — just enough of D_Itemable/D_ItemsStatic/D_Mounts/
    /// D_BestiaryData for SaveGameNames to resolve one real Icon/Image path per table, mirroring the
    /// exact row shape confirmed against a real game extraction (see SaveGameNames' own doc
    /// comments for the two-hop Item resolution and the Icon/Image field names per table).
    /// </summary>
    private string CreateGameDataFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests_GameData", Guid.NewGuid().ToString("N"));
        _tempFoldersToCleanUp.Add(folder);
        Directory.CreateDirectory(Path.Combine(folder, "Traits"));
        Directory.CreateDirectory(Path.Combine(folder, "Items"));
        Directory.CreateDirectory(Path.Combine(folder, "AI"));
        Directory.CreateDirectory(Path.Combine(folder, "Bestiary"));

        File.WriteAllText(Path.Combine(folder, "Traits", "D_Itemable.json"), """
            { "Rows": [ { "Name": "Item_Fiber", "DisplayName": "NSLOCTEXT(\"D_Itemable\", \"Item_Fiber-DisplayName\", \"Fiber\")", "Icon": "/Game/Assets/2DArt/UI/Items/Item_Icons/Resources/ITEM_Fibre.ITEM_Fibre", "Weight": 10, "MaxStack": 200 } ] }
            """);

        File.WriteAllText(Path.Combine(folder, "Items", "D_ItemsStatic.json"), """
            { "Rows": [ { "Name": "ItemStatic_Fiber", "Itemable": { "RowName": "Item_Fiber" } } ] }
            """);

        File.WriteAllText(Path.Combine(folder, "AI", "D_Mounts.json"), """
            { "Rows": [ { "Name": "Wolf", "Icon": "/Game/Assets/2DArt/UI/Talents/Companion/T_Talent_Base_Wolf.T_Talent_Base_Wolf" } ] }
            """);

        File.WriteAllText(Path.Combine(folder, "Bestiary", "D_BestiaryData.json"), """
            { "Rows": [ { "Name": "PackWolf", "CreatureName": "NSLOCTEXT(\"D_BestiaryData\", \"PackWolf-CreatureName\", \"Pack Wolf\")", "Image": "/Game/Assets/2DArt/UI/FieldGuide/Creatures/Conifer/T_Bestiary_PackWolf.T_Bestiary_PackWolf", "TotalPointsRequired": 100 } ] }
            """);

        return folder;
    }

    private static JsonObject MakeMetaInventoryRoot(params JsonObject[] items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            array.Add(item);
        }

        return new JsonObject { ["Items"] = array };
    }

    private static JsonObject MakeMetaInventoryItem(string itemStaticRowName) => new()
    {
        ["ItemStaticData"] = new JsonObject { ["RowName"] = itemStaticRowName },
    };

    /// <summary>
    /// Two real bugs at once: names stayed cached (empty, here) after Refresh game data, and a slot
    /// with no characters never refreshed the shown account flags, since SelectedCharacter stays null.
    /// </summary>
    [Fact]
    public async Task GameDataRefreshed_ReloadsTheSlotWithTheNewNames_EvenWithNoCharacters()
    {
        var dataFolder = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests_GameData", Guid.NewGuid().ToString("N"));
        _tempFoldersToCleanUp.Add(dataFolder);
        var repository = new FakeSaveRepository { Profile = MakeProfile(nextChrSlot: 0) };
        var vm = CreateViewModel(repository, gameNames: new SaveGameNames(dataFolder));
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            Assert.Empty(vm.FilteredAccountFlags);

            Directory.CreateDirectory(Path.Combine(dataFolder, "Flags"));
            File.WriteAllText(Path.Combine(dataFolder, "Flags", "D_AccountFlags.json"), """
                { "Rows": [ { "Name": "Flag_A" }, { "Name": "Flag_B" } ] }
                """);
            WeakReferenceMessenger.Default.Send(new WeeklyChangeReportUpdatedMessage());
            await vm.WaitForPendingSlotLoadAsync();

            Assert.Empty(vm.Characters);
            Assert.Equal(["Flag_A", "Flag_B"], vm.FilteredAccountFlags.Select(f => f.RowName));
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>The user's ask (2026-10-23): a mission and its reward switch each other ON, never off.</summary>
    [Fact]
    public async Task MissionAndItsReward_SwitchEachOtherOn_ButNotOff()
    {
        var dataFolder = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests_GameData", Guid.NewGuid().ToString("N"));
        _tempFoldersToCleanUp.Add(dataFolder);
        Directory.CreateDirectory(Path.Combine(dataFolder, "Flags"));
        Directory.CreateDirectory(Path.Combine(dataFolder, "Talents"));
        File.WriteAllText(Path.Combine(dataFolder, "Flags", "D_AccountFlags.json"), """
            { "Rows": [ { "Name": "GrantedBlueprint_Bait", "BlueprintUnlocks": [ { "RowName": "Bait" } ],
                          "RewardedFromMissions": [ { "RowName": "Tier3_Ext", "DataTableName": "D_ProspectList" } ] } ] }
            """);
        File.WriteAllText(Path.Combine(dataFolder, "Talents", "D_Talents.json"), """
            { "Rows": [ { "Name": "Prospect_RL_Ext", "ExtraData": { "RowName": "Tier3_Ext", "DataTableName": "D_ProspectList" }, "TalentTree": { "RowName": "Prospect_Olympus" } } ] }
            """);
        var repository = new FakeSaveRepository { Profile = MakeProfile(nextChrSlot: 0) };
        var vm = CreateViewModel(repository, gameNames: new SaveGameNames(dataFolder));
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            var reward = Assert.Single(vm.AccountFlags);
            var mission = Assert.Single(vm.WorkshopTalents, t => t.RowName == "Prospect_RL_Ext");
            Assert.NotNull(reward.LinkHint);
            Assert.NotNull(mission.LinkHint);

            mission.IsDone = true;
            Assert.True(reward.IsUnlocked);

            mission.IsDone = false;
            Assert.True(reward.IsUnlocked);

            reward.IsUnlocked = false;
            reward.IsUnlocked = true;
            Assert.True(mission.IsDone);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>The user's ask (2026-10-23): a character with more ranks than points is labelled over budget.</summary>
    [Fact]
    public async Task OverBudget_IsLabelled_OnTheCharacterAndTheTalentsTab()
    {
        static IcarusStarlink.PakIO.Assets.GrowthCurveKey Key(float t, float v) => new(t, v, false, false, 0, 0);
        var growth = new IcarusStarlink.PakIO.Assets.PlayerGrowth(
            1000, 60, false,
            new([Key(0, 0), Key(51, 4_034_000)], false, true),
            new([Key(0, 0), Key(50, 75), Key(60, 90)], false, false),
            null, null, []);
        var character = MakeCharacter("Alpha", 0);
        character["XP"] = 4_034_000L;
        character["Talents"] = new JsonArray(new JsonObject { ["RowName"] = "Some_Talent", ["Rank"] = 100 });
        var repository = new FakeSaveRepository { Characters = [character], Profile = MakeProfile(nextChrSlot: 1) };
        var data = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests_GameData", Guid.NewGuid().ToString("N"));
        _tempFoldersToCleanUp.Add(data);
        Directory.CreateDirectory(Path.Combine(data, "Talents"));
        File.WriteAllText(Path.Combine(data, "Talents", "D_Talents.json"), """
            { "Rows": [ { "Name": "Some_Talent", "TalentTree": { "RowName": "Tree_A" }, "Rewards": [ {}, {}, {} ] } ] }
            """);
        File.WriteAllText(Path.Combine(data, "Talents", "D_TalentTrees.json"), """{ "Rows": [ { "Name": "Tree_A", "Archetype": { "RowName": "Survival" } } ] }""");
        File.WriteAllText(Path.Combine(data, "Talents", "D_TalentArchetypes.json"), """{ "Rows": [ { "Name": "Survival", "Model": { "RowName": "Player" } } ] }""");
        var vm = new SavesViewModel(repository, new FakeActivityLog(), new SaveGameNames(data),
            new FakeDialogService(confirmResult: true), new FakeGameProcessChecker(false), new FakeBaseGameIconDecoder(),
            readPlayerGrowth: () => growth);
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            await vm.WaitForPlayerGrowthAsync();
            var alpha = Assert.Single(vm.Characters);
            Assert.Equal((Label: "Talents", Used: 100, Available: 76), vm.BudgetOf(alpha)!.Single()); // level 51: 75 + 1.5, rounded down
            Assert.Equal("Talents by 24", alpha.OverBudget);
            vm.SelectedCharacter = alpha;
            Assert.StartsWith("Over budget: Talents by 24.", vm.OverBudgetNote);
            Assert.Contains(vm.CheckFindings, f => f.Message.Contains("Talents by 24"));

            alpha.Talents.Single(t => t.RowName == "Some_Talent").Rank = 70;
            Assert.Null(alpha.OverBudget);
            Assert.Null(vm.OverBudgetNote);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>Plan step 3: a finished mission whose reward is off is found, and its fix turns the reward on.</summary>
    [Fact]
    public async Task SaveCheck_FindsAMissionWhoseRewardIsOff_AndFixesIt()
    {
        var dataFolder = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests_GameData", Guid.NewGuid().ToString("N"));
        _tempFoldersToCleanUp.Add(dataFolder);
        Directory.CreateDirectory(Path.Combine(dataFolder, "Flags"));
        Directory.CreateDirectory(Path.Combine(dataFolder, "Talents"));
        File.WriteAllText(Path.Combine(dataFolder, "Flags", "D_AccountFlags.json"), """
            { "Rows": [ { "Name": "GrantedBlueprint_Bait", "BlueprintUnlocks": [ { "RowName": "Bait" } ],
                          "RewardedFromMissions": [ { "RowName": "Tier3_Ext", "DataTableName": "D_ProspectList" } ] } ] }
            """);
        File.WriteAllText(Path.Combine(dataFolder, "Talents", "D_Talents.json"), """
            { "Rows": [ { "Name": "Prospect_RL_Ext", "ExtraData": { "RowName": "Tier3_Ext", "DataTableName": "D_ProspectList" }, "TalentTree": { "RowName": "Prospect_Olympus" } } ] }
            """);
        var profile = MakeProfile(nextChrSlot: 0);
        profile["Talents"] = new JsonArray(new JsonObject { ["RowName"] = "Prospect_RL_Ext", ["Rank"] = 1 });
        var repository = new FakeSaveRepository { Profile = profile };
        var vm = CreateViewModel(repository, gameNames: new SaveGameNames(dataFolder));
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            var finding = Assert.Single(vm.CheckFindings);
            Assert.Equal(SaveCheckSeverity.Warning, finding.Severity);
            Assert.True(vm.HasFixableFindings);

            vm.FixFindingCommand.Execute(finding);

            Assert.True(Assert.Single(vm.AccountFlags).IsUnlocked);
            Assert.Empty(vm.CheckFindings);
            Assert.True(vm.HasUnsavedChanges);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>
    /// Seen on the user's save (2026-10-29): Olympus' Glacier Extraction mission gives every character
    /// Voxel_Meta_Resources_Visible, and missions live in the profile's Talents, so the check said "no
    /// learned talent gives it" and offered to turn a real mission reward off. A done mission counts;
    /// an undone one still gets the note.
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public async Task SaveCheck_CharacterFlagFromADoneMission_IsNotAnOrphan(int missionRank, bool expectNote)
    {
        var data = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests_GameData", Guid.NewGuid().ToString("N"));
        _tempFoldersToCleanUp.Add(data);
        Directory.CreateDirectory(Path.Combine(data, "Flags"));
        Directory.CreateDirectory(Path.Combine(data, "Talents"));
        File.WriteAllText(Path.Combine(data, "Flags", "D_CharacterFlags.json"), """
            { "Rows": [ { "Name": "Voxel_Meta_Resources_Visible" } ] }
            """);
        File.WriteAllText(Path.Combine(data, "Talents", "D_Talents.json"), """
            { "Rows": [ { "Name": "Prospect_OLY_Glacier_Extraction", "TalentTree": { "RowName": "Prospect_Olympus" },
                          "Rewards": [ { "GrantedFlags": [ { "RowName": "Voxel_Meta_Resources_Visible", "DataTableName": "D_CharacterFlags" } ] } ] } ] }
            """);
        var character = MakeCharacter("Alpha", 0);
        character["UnlockedFlags"] = new JsonArray(0);
        var profile = MakeProfile(nextChrSlot: 1);
        profile["Talents"] = new JsonArray(new JsonObject { ["RowName"] = "Prospect_OLY_Glacier_Extraction", ["Rank"] = missionRank });
        var repository = new FakeSaveRepository { Characters = [character], Profile = profile };
        var vm = CreateViewModel(repository, gameNames: new SaveGameNames(data));
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            Assert.True(Assert.Single(vm.Characters).Flags.Single(f => f.RowName == "Voxel_Meta_Resources_Visible").IsUnlocked);
            Assert.Equal(expectNote, vm.CheckFindings.Any(f => f.Message.Contains("no learned talent gives it")));
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>Where mod rows come from doesn't depend on the slot: read again only once the Library (or the game's content) may have changed.</summary>
    [Fact]
    public async Task Reload_ReusesTheModSourcesRead_UntilTheLibraryChanges()
    {
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = MakeProfile(nextChrSlot: 1),
        };
        var reads = 0;
        var vm = CreateViewModel(repository, readModSources: () =>
        {
            reads++;
            return SaveModSources.Index.Empty;
        });
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            await vm.WaitForModSourcesAsync();
            vm.ReloadCommand.Execute(null);
            await vm.WaitForPendingSlotLoadAsync();
            await vm.WaitForModSourcesAsync();
            Assert.Equal(1, reads);

            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
            vm.ReloadCommand.Execute(null);
            await vm.WaitForPendingSlotLoadAsync();
            await vm.WaitForModSourcesAsync();
            Assert.Equal(2, reads);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>Complete all shown used to re-check every list per creature; the page now hears about it once.</summary>
    [Fact]
    public async Task CompleteShownBestiary_TellsThePageOnce()
    {
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = MakeProfile(nextChrSlot: 1),
        };
        var data = CreateGameDataFolder();
        File.WriteAllText(Path.Combine(data, "Bestiary", "D_BestiaryData.json"), """
            { "Defaults": { "TotalPointsRequired": 100 }, "Rows": [ { "Name": "Wolf" }, { "Name": "Bear" }, { "Name": "Moa" } ] }
            """);
        var vm = CreateViewModel(repository, gameNames: new SaveGameNames(data));
        await vm.WaitForPendingSlotLoadAsync();
        Assert.Equal(3, vm.FilteredBestiaryEntries.Count);
        var summaries = 0;
        vm.PropertyChanged += (_, e) => summaries += e.PropertyName == nameof(SavesViewModel.ChangesSummary) ? 1 : 0;

        vm.CompleteShownBestiaryCommand.Execute(null);

        Assert.Equal(1, summaries);
        Assert.All(vm.BestiaryEntries.Where(b => b.CanSetMax), b => Assert.True(b.IsComplete));
    }

    /// <summary>The user's ask (2026-10-28): the Talents tab lists trees in the game's order (Survival before Combat) and blueprints Tier 1 to Tier 3, not A to Z by heading.</summary>
    [Fact]
    public async Task Talents_ListTreesInTheGamesOrder_BlueprintTiersInOrder()
    {
        var character = MakeCharacter("Alpha", 0);
        character["Talents"] = new JsonArray(
            new JsonObject { ["RowName"] = "Drill", ["Rank"] = 1 },
            new JsonObject { ["RowName"] = "Slash", ["Rank"] = 1 },
            new JsonObject { ["RowName"] = "Bench_Thing", ["Rank"] = 1 },
            new JsonObject { ["RowName"] = "Gather", ["Rank"] = 1 },
            new JsonObject { ["RowName"] = "Bone_Knife", ["Rank"] = 1 });
        var repository = new FakeSaveRepository { Characters = [character], Profile = MakeProfile(nextChrSlot: 1) };
        var data = CreateGameDataFolder();
        Directory.CreateDirectory(Path.Combine(data, "Talents"));
        File.WriteAllText(Path.Combine(data, "Talents", "D_TalentArchetypes.json"), """
            { "Rows": [
                { "Name": "Survival", "Model": { "RowName": "Player" }, "DisplayName": "NSLOCTEXT(\"a\", \"b\", \"Survival\")" },
                { "Name": "Combat", "Model": { "RowName": "Player" }, "DisplayName": "NSLOCTEXT(\"a\", \"b\", \"Combat\")" },
                { "Name": "Machine", "Model": { "RowName": "Blueprint" }, "DisplayName": "NSLOCTEXT(\"a\", \"b\", \"Tier 3\")" },
                { "Name": "Crafting", "Model": { "RowName": "Blueprint" }, "DisplayName": "NSLOCTEXT(\"a\", \"b\", \"Tier 2\")" },
                { "Name": "Player", "Model": { "RowName": "Blueprint" }, "DisplayName": "NSLOCTEXT(\"a\", \"b\", \"Tier 1\")" } ] }
            """);
        File.WriteAllText(Path.Combine(data, "Talents", "D_TalentTrees.json"), """
            { "Rows": [
                { "Name": "Combat_Blades", "Archetype": { "RowName": "Combat" }, "DisplayName": "NSLOCTEXT(\"a\", \"b\", \"Blades\")" },
                { "Name": "Survival_Resources", "Archetype": { "RowName": "Survival" }, "DisplayName": "NSLOCTEXT(\"a\", \"b\", \"Resources\")" },
                { "Name": "Blueprint_T3_Machine", "Archetype": { "RowName": "Machine" } },
                { "Name": "Blueprint_T2_Crafting", "Archetype": { "RowName": "Crafting" } },
                { "Name": "Blueprint_T1_Player", "Archetype": { "RowName": "Player" } } ] }
            """);
        File.WriteAllText(Path.Combine(data, "Talents", "D_Talents.json"), """
            { "Rows": [
                { "Name": "Slash", "TalentTree": { "RowName": "Combat_Blades" } },
                { "Name": "Gather", "TalentTree": { "RowName": "Survival_Resources" } },
                { "Name": "Drill", "TalentTree": { "RowName": "Blueprint_T3_Machine" } },
                { "Name": "Bench_Thing", "TalentTree": { "RowName": "Blueprint_T2_Crafting" } },
                { "Name": "Bone_Knife", "TalentTree": { "RowName": "Blueprint_T1_Player" } } ] }
            """);
        var vm = CreateViewModel(repository, gameNames: new SaveGameNames(data));
        await vm.WaitForPendingSlotLoadAsync();
        vm.SelectedCharacter = vm.Characters.Single();

        Assert.Equal(["Survival · Resources", "Combat · Blades", "Tier 1", "Tier 2", "Tier 3"], vm.FilteredTalents.Select(t => t.Group));
    }

    /// <summary>A biome heading's "Complete all" fills that biome's creatures only; biomes list in the table's order.</summary>
    [Fact]
    public async Task CompleteBestiaryGroup_FillsOnlyThatBiome_AndBiomesKeepTheTablesOrder()
    {
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = MakeProfile(nextChrSlot: 1),
        };
        var data = CreateGameDataFolder();
        File.WriteAllText(Path.Combine(data, "Bestiary", "D_BestiaryData.json"), """
            { "Defaults": { "TotalPointsRequired": 100 }, "Rows": [
                { "Name": "Wolf", "Biomes": [ { "RowName": "Conifer" } ] },
                { "Name": "Polar_Bear", "Biomes": [ { "RowName": "Arctic" } ] },
                { "Name": "Deer", "Biomes": [ { "RowName": "Conifer" } ] } ] }
            """);
        var vm = CreateViewModel(repository, gameNames: new SaveGameNames(data));
        await vm.WaitForPendingSlotLoadAsync();
        Assert.Equal(["Forest", "Forest", "Arctic"], vm.FilteredBestiaryEntries.Select(b => b.Group));

        vm.CompleteBestiaryGroupCommand.Execute("Forest");

        Assert.Equal(["Wolf", "Deer"], vm.BestiaryEntries.Where(b => b.IsComplete).Select(b => b.RowName));
        Assert.True(vm.HasUnsavedChanges);
    }

    /// <summary>A duplicated character's talents had no Source, so Remove leftovers missed the copy's leftovers.</summary>
    [Fact]
    public async Task DuplicateCharacter_ItsLeftoverTalents_AreLabelledAndCounted()
    {
        var character = MakeCharacter("Alpha", 0);
        character["Talents"] = new JsonArray(new JsonObject { ["RowName"] = "Old_Mod_Talent", ["Rank"] = 1 });
        var repository = new FakeSaveRepository
        {
            Characters = [character],
            Profile = MakeProfile(nextChrSlot: 1),
        };
        var index = new SaveModSources.Index(
            new Dictionary<string, HashSet<string>>(),
            new Dictionary<string, HashSet<string>>(),
            new Dictionary<string, Dictionary<string, List<string>>>(),
            IcarusStarlink.PakIO.Exmod.GameRowHistory.Empty,
            canTellInstalled: true);
        var vm = CreateViewModel(repository, readModSources: () => index);
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            await vm.WaitForModSourcesAsync();

            vm.DuplicateCharacterCommand.Execute(vm.Characters.Single());

            var copy = vm.Characters.Single(c => c.Name == "Alpha (Copy)");
            Assert.True(copy.Talents.Single(t => t.RowName == "Old_Mod_Talent").Source?.IsRemovable);
            Assert.Contains("Alpha (Copy): 1", vm.LeftoversSummary);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>The user's ask (2026-10-23): talents and items from mods that are gone can be taken out; a mod's that's installed stay.</summary>
    [Fact]
    public async Task RemoveLeftovers_TakesOutOnlyWhatNoModGives()
    {
        var profile = MakeProfile(nextChrSlot: 0);
        profile["Talents"] = new JsonArray(
            new JsonObject { ["RowName"] = "Workshop_Moa", ["Rank"] = 1 },
            new JsonObject { ["RowName"] = "Workshop_Petes_AuxSlot", ["Rank"] = 1 });
        var repository = new FakeSaveRepository
        {
            Profile = profile,
            MetaInventory = MakeMetaInventoryRoot(MakeMetaInventoryItem("Old_Mod_Item")),
        };
        var talents = SaveModSources.TalentsFile;
        var index = new SaveModSources.Index(
            new Dictionary<string, HashSet<string>>(),
            new Dictionary<string, HashSet<string>> { [talents] = new(StringComparer.OrdinalIgnoreCase) { "Workshop_Moa" } },
            new Dictionary<string, Dictionary<string, List<string>>>(),
            IcarusStarlink.PakIO.Exmod.GameRowHistory.Empty,
            canTellInstalled: true);
        var vm = CreateViewModel(repository, readModSources: () => index);
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            await vm.WaitForModSourcesAsync();
            Assert.True(vm.HasLeftovers);

            vm.RemoveLeftoversCommand.Execute(null);

            Assert.Equal(1, vm.WorkshopTalents.Single(t => t.RowName == "Workshop_Moa").Rank);
            Assert.Equal(0, vm.WorkshopTalents.Single(t => t.RowName == "Workshop_Petes_AuxSlot").Rank);
            Assert.Empty(vm.MetaInventoryItems);
            Assert.False(vm.HasLeftovers);
            Assert.True(vm.HasUnsavedChanges);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>Regression: an unreadable PlayerData folder was reported as "No player saves found" (the read error was overwritten at once).</summary>
    [Fact]
    public async Task UnreadablePlayerDataFolder_IsReportedAsSuch()
    {
        var repository = new FakeSaveRepository { ListSlotsError = new UnauthorizedAccessException("Access denied") };
        var vm = CreateViewModel(repository);
        await vm.WaitForPendingSlotLoadAsync();

        Assert.Contains("Couldn't read the game's PlayerData folder", vm.StatusMessage);
        Assert.DoesNotContain("No player saves found", vm.StatusMessage);
    }

    /// <summary>Regression: the Characters list's " *" unsaved marker never showed, because nothing raised IsDirty.</summary>
    [Fact]
    public async Task EditingACharacter_RaisesIsDirty_AndSavingClearsIt()
    {
        var repository = new FakeSaveRepository { Characters = [MakeCharacter("Alpha", 0)], Profile = MakeProfile(nextChrSlot: 1) };
        var vm = CreateViewModel(repository);
        await vm.WaitForPendingSlotLoadAsync();
        var character = vm.Characters.Single();
        var raised = 0;
        character.PropertyChanged += (_, e) => raised += e.PropertyName == nameof(SaveCharacterViewModel.IsDirty) ? 1 : 0;

        character.Name = "Renamed";
        Assert.True(character.IsDirty);
        Assert.Equal(1, raised);

        vm.SaveChangesCommand.Execute(null);
        Assert.False(character.IsDirty);
        Assert.True(raised >= 2, "saving didn't tell the list the marker cleared");
    }

    /// <summary>
    /// Regression: when Profile.json failed after Characters.json was written, the next Save took
    /// the app's own Characters.json write for the game's and refused, leaving "reload and lose
    /// your edits" as the only way out.
    /// </summary>
    [Fact]
    public async Task SaveFailingPartway_SaysSo_AndTheRetryIsNotRefusedAsAnOutsideChange()
    {
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = MakeProfile(nextChrSlot: 1),
            BumpStampOnWrite = true,
            SaveProfileError = new IOException("disk full"),
        };
        var dialogs = new FakeDialogService(confirmResult: true);
        var vm = CreateViewModel(repository, dialogs);
        await vm.WaitForPendingSlotLoadAsync();
        vm.DuplicateCharacterCommand.Execute(vm.Characters.Single()); // changes Characters.json and Profile.json (NextChrSlot)

        vm.SaveChangesCommand.Execute(null);
        Assert.Contains("partway", vm.StatusMessage);
        Assert.Contains("Restore", vm.StatusMessage);

        var confirmsBefore = dialogs.ConfirmCallCount;
        vm.SaveChangesCommand.Execute(null);

        Assert.Equal(2, repository.SaveProfileCallCount);
        Assert.Equal(confirmsBefore + 1, dialogs.ConfirmCallCount); // just the save confirm, no "changed on disk"
        Assert.StartsWith("Saved", vm.StatusMessage);
    }

    /// <summary>Regression: when the file stamps couldn't be read at load, the "changed on disk" guard switched off silently and Save could write over newer progress.</summary>
    [Fact]
    public async Task UnreadableFileStamps_BlockSaving()
    {
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = MakeProfile(nextChrSlot: 1),
            StampsError = new IOException("locked"),
        };
        var vm = CreateViewModel(repository);
        await vm.WaitForPendingSlotLoadAsync();

        Assert.Contains("Couldn't read this save", vm.StatusMessage);
        Assert.False(vm.HasUnsavedChanges);
    }

    /// <summary>Regression: an unreadable backups folder failed the whole load, which blocked saving a save that read fine.</summary>
    [Fact]
    public async Task UnreadableBackupsFolder_StillLoadsAndSaves()
    {
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = MakeProfile(nextChrSlot: 1),
            BackupsError = new UnauthorizedAccessException("denied"),
        };
        var vm = CreateViewModel(repository);
        await vm.WaitForPendingSlotLoadAsync();

        Assert.Single(vm.Characters);
        Assert.Contains("Couldn't list backups", vm.LastBackupDisplay);
        vm.Characters.Single().Name = "Renamed";
        vm.SaveChangesCommand.Execute(null);

        Assert.Equal(1, repository.SaveCharactersCallCount);
        Assert.StartsWith("Saved", vm.StatusMessage);
    }

    /// <summary>Regression: two loads of the same slot in flight (Reload clicked twice on a slow disk) both added their rows, listing every character twice.</summary>
    [Fact]
    public async Task OverlappingLoadsOfTheSameSlot_FillTheListsOnce()
    {
        var repository = new FakeSaveRepository { Characters = [MakeCharacter("Alpha", 0), MakeCharacter("Beta", 1)], Profile = MakeProfile(nextChrSlot: 2) };
        var vm = CreateViewModel(repository);
        await vm.WaitForPendingSlotLoadAsync();

        repository.LoadGate = new SemaphoreSlim(0);
        vm.ReloadCommand.Execute(null);
        var first = vm.WaitForPendingSlotLoadAsync();
        vm.ReloadCommand.Execute(null);
        var second = vm.WaitForPendingSlotLoadAsync();
        repository.LoadGate.Release(2);
        await Task.WhenAll(first, second);

        Assert.Equal(2, vm.Characters.Count);
    }

    [Fact]
    public async Task DuplicateCharacterCommand_ProducesADistinctEntryWithAFreshChrSlot()
    {
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0), MakeCharacter("Beta", 1)],
            Profile = MakeProfile(nextChrSlot: 2),
        };
        var vm = CreateViewModel(repository);
        await vm.WaitForPendingSlotLoadAsync();

        var original = vm.Characters.Single(c => c.Name == "Alpha");

        vm.DuplicateCharacterCommand.Execute(original);

        Assert.Equal(3, vm.Characters.Count);
        var duplicate = vm.Characters.Single(c => c.Name == "Alpha (Copy)");
        Assert.NotSame(original, duplicate);
        Assert.NotEqual(original.ChrSlot, duplicate.ChrSlot);
        // Every character now on the profile has its own distinct slot, not just duplicate-vs-source.
        Assert.Equal(vm.Characters.Count, vm.Characters.Select(c => c.ChrSlot).Distinct().Count());
        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task DuplicateCharacterCommand_CalledTwiceOnTheSameCharacter_EachDuplicateGetsItsOwnSlot()
    {
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = MakeProfile(nextChrSlot: 1),
        };
        var vm = CreateViewModel(repository);
        await vm.WaitForPendingSlotLoadAsync();

        var original = vm.Characters.Single();
        vm.DuplicateCharacterCommand.Execute(original);
        vm.DuplicateCharacterCommand.Execute(original);

        Assert.Equal(3, vm.Characters.Count);
        Assert.Equal(3, vm.Characters.Select(c => c.ChrSlot).Distinct().Count());
    }

    [Fact]
    public async Task DeleteCharacterCommand_Confirmed_RemovesExactlyTheSelectedCharacter()
    {
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0), MakeCharacter("Beta", 1)],
            Profile = MakeProfile(nextChrSlot: 2),
        };
        var dialogService = new FakeDialogService(confirmResult: true);
        var vm = CreateViewModel(repository, dialogService: dialogService);
        await vm.WaitForPendingSlotLoadAsync();

        var toDelete = vm.Characters.Single(c => c.Name == "Alpha");
        vm.DeleteCharacterCommand.Execute(toDelete);

        var remaining = Assert.Single(vm.Characters);
        Assert.Equal("Beta", remaining.Name);
        Assert.Equal(1, dialogService.ConfirmCallCount);
        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task DeleteCharacterCommand_Declined_LeavesBothCharactersUntouched()
    {
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0), MakeCharacter("Beta", 1)],
            Profile = MakeProfile(nextChrSlot: 2),
        };
        var vm = CreateViewModel(repository, dialogService: new FakeDialogService(confirmResult: false));
        await vm.WaitForPendingSlotLoadAsync();

        var toDelete = vm.Characters.Single(c => c.Name == "Alpha");
        vm.DeleteCharacterCommand.Execute(toDelete);

        Assert.Equal(2, vm.Characters.Count);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task SaveChangesCommand_AfterDuplicateCharacter_PersistsTheNewCharacterThroughTheRepository()
    {
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = MakeProfile(nextChrSlot: 1),
        };
        var vm = CreateViewModel(
            repository,
            dialogService: new FakeDialogService(confirmResult: true),
            gameProcessChecker: new FakeGameProcessChecker(false, false));
        await vm.WaitForPendingSlotLoadAsync();

        vm.DuplicateCharacterCommand.Execute(vm.Characters.Single());
        vm.SaveChangesCommand.Execute(null);

        Assert.Equal(1, repository.SaveCharactersCallCount);
        Assert.NotNull(repository.LastSavedCharacters);
        Assert.Equal(2, repository.LastSavedCharacters!.Count);
        Assert.Equal(2, repository.LastSavedCharacters.Select(c => c["ChrSlot"]!.GetValue<int>()).Distinct().Count());
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task DeleteMountCommand_RemovesExactlyTheSelectedMount_LeavesOthersUntouched()
    {
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = MakeProfile(nextChrSlot: 1),
            Mounts = MakeMountsRoot(MakeMount("Rex", 3, "Wolf"), MakeMount("Bessie", 5, "Cow")),
        };
        var vm = CreateViewModel(repository);
        await vm.WaitForPendingSlotLoadAsync();

        var toDelete = vm.Mounts.Single(m => m.Name == "Rex");
        vm.DeleteMountCommand.Execute(toDelete);

        var remaining = Assert.Single(vm.Mounts);
        Assert.Equal("Bessie", remaining.Name);
        Assert.True(vm.HasUnsavedChanges);
    }

    /// <summary>Also the regression test for ApplyMountEdits' own rebuild fix — before that fix, a deleted mount's Node stayed a live child of the OLD SavedMounts array and would have been written back regardless of having been removed from the Mounts collection.</summary>
    [Fact]
    public async Task SaveChangesCommand_AfterDeleteMount_ActuallyRemovesItFromTheWrittenFile()
    {
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = MakeProfile(nextChrSlot: 1),
            Mounts = MakeMountsRoot(MakeMount("Rex", 3, "Wolf"), MakeMount("Bessie", 5, "Cow")),
        };
        var vm = CreateViewModel(
            repository,
            dialogService: new FakeDialogService(confirmResult: true),
            gameProcessChecker: new FakeGameProcessChecker(false, false));
        await vm.WaitForPendingSlotLoadAsync();

        vm.DeleteMountCommand.Execute(vm.Mounts.Single(m => m.Name == "Rex"));
        vm.SaveChangesCommand.Execute(null);

        Assert.NotNull(repository.LastSavedMounts);
        var savedArray = repository.LastSavedMounts!["SavedMounts"]!.AsArray();
        var remaining = Assert.Single(savedArray);
        Assert.Equal("Bessie", remaining!["MountName"]!.GetValue<string>());
    }

    // --- Icons: SavesViewModel's own background resolution of item/mount/creature thumbnails
    //     through the new IBaseGameIconDecoder, via FakeBaseGameIconDecoder below (no real game
    //     install or binary asset needed — same fake-per-dependency convention as everywhere else
    //     in this file). CreateGameDataFolder gives one real IconPath per table so the "was this
    //     row's icon actually queued, with the right path" behavior is genuinely exercised, not just
    //     the "no path, nothing happens" no-op branch. ---

    [Fact]
    public async Task LoadSlot_ItemWithARealIconPath_ResolvesItThroughTheBaseGameIconDecoderAndFillsInIcon()
    {
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = MakeProfile(nextChrSlot: 1),
            MetaInventory = MakeMetaInventoryRoot(MakeMetaInventoryItem("ItemStatic_Fiber")),
        };
        var iconDecoder = new FakeBaseGameIconDecoder { PngBytesToReturn = MinimalPng };
        var vm = CreateViewModel(repository, gameNames: new SaveGameNames(CreateGameDataFolder()), baseGameIconDecoder: iconDecoder);
        await vm.WaitForPendingSlotLoadAsync();
        await vm.WaitForPendingIconLoadAsync();

        var item = vm.MetaInventoryItems.Single();
        Assert.Equal("/Game/Assets/2DArt/UI/Items/Item_Icons/Resources/ITEM_Fibre.ITEM_Fibre", item.IconPath);
        Assert.Contains(item.IconPath, iconDecoder.RequestedPaths);
        Assert.NotNull(item.Icon);
    }

    [Fact]
    public async Task LoadSlot_MountWithARealIconPath_ResolvesItThroughTheBaseGameIconDecoderAndFillsInIcon()
    {
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = MakeProfile(nextChrSlot: 1),
            Mounts = MakeMountsRoot(MakeMount("Rex", 3, "Wolf")),
        };
        var iconDecoder = new FakeBaseGameIconDecoder { PngBytesToReturn = MinimalPng };
        var vm = CreateViewModel(repository, gameNames: new SaveGameNames(CreateGameDataFolder()), baseGameIconDecoder: iconDecoder);
        await vm.WaitForPendingSlotLoadAsync();
        await vm.WaitForPendingIconLoadAsync();

        var mount = vm.Mounts.Single();
        Assert.Equal("/Game/Assets/2DArt/UI/Talents/Companion/T_Talent_Base_Wolf.T_Talent_Base_Wolf", mount.IconPath);
        Assert.Contains(mount.IconPath, iconDecoder.RequestedPaths);
        Assert.NotNull(mount.Icon);
    }

    [Fact]
    public async Task LoadSlot_BestiaryCreatureWithARealImagePath_ResolvesItThroughTheBaseGameIconDecoderAndFillsInIcon()
    {
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = MakeProfile(nextChrSlot: 1),
        };
        var iconDecoder = new FakeBaseGameIconDecoder { PngBytesToReturn = MinimalPng };
        var vm = CreateViewModel(repository, gameNames: new SaveGameNames(CreateGameDataFolder()), baseGameIconDecoder: iconDecoder);
        await vm.WaitForPendingSlotLoadAsync();
        await vm.WaitForPendingIconLoadAsync();

        // BestiaryEntries lists every creature the current data tables define, not just ones the
        // save itself tracks yet — see BuildBestiaryEntries' own doc comment.
        var creature = vm.BestiaryEntries.Single(b => b.RowName == "PackWolf");
        Assert.Equal("/Game/Assets/2DArt/UI/FieldGuide/Creatures/Conifer/T_Bestiary_PackWolf.T_Bestiary_PackWolf", creature.ImagePath);
        Assert.Contains(creature.ImagePath, iconDecoder.RequestedPaths);
        Assert.NotNull(creature.Icon);
    }

    /// <summary>One Bestiary edit used to rebuild every entry as bare {BestiaryGroup, NumPoints}, dropping other fields and RowName-less entries.</summary>
    [Fact]
    public async Task SaveBestiary_ChangesOnlyTheEditedEntry_InPlace()
    {
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = MakeProfile(nextChrSlot: 1),
        };
        repository.Bestiary["BestiaryTracking"] = JsonNode.Parse("""
            [ { "Odd": 1 },
              { "BestiaryGroup": { "RowName": "OldCreature", "DataTableName": "D_BestiaryData" }, "NumPoints": 7 },
              { "BestiaryGroup": { "RowName": "PackWolf", "DataTableName": "D_BestiaryData" }, "NumPoints": 10, "Extra": "kept" } ]
            """);
        var vm = CreateViewModel(repository, gameNames: new SaveGameNames(CreateGameDataFolder()));
        await vm.WaitForPendingSlotLoadAsync();

        vm.BestiaryEntries.Single(b => b.RowName == "PackWolf").PointsText = "50";
        vm.SaveChangesCommand.Execute(null);

        Assert.Equal(
            """[{"Odd":1},{"BestiaryGroup":{"RowName":"OldCreature","DataTableName":"D_BestiaryData"},"NumPoints":7},{"BestiaryGroup":{"RowName":"PackWolf","DataTableName":"D_BestiaryData"},"NumPoints":50,"Extra":"kept"}]""",
            repository.Bestiary["BestiaryTracking"]!.ToJsonString());
    }

    [Fact]
    public async Task LoadSlot_ItemWithNoIconPath_NeverCallsTheIconDecoder_StaysTextOnly()
    {
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = MakeProfile(nextChrSlot: 1),
            // No matching D_ItemsStatic/D_Itemable entry (default NoGameData folder) — DisplayName
            // falls back to the raw RowName, and IconPath has nothing to resolve from.
            MetaInventory = MakeMetaInventoryRoot(MakeMetaInventoryItem("Unknown_RowName")),
        };
        var iconDecoder = new FakeBaseGameIconDecoder { PngBytesToReturn = MinimalPng };
        var vm = CreateViewModel(repository, baseGameIconDecoder: iconDecoder);
        await vm.WaitForPendingSlotLoadAsync();
        await vm.WaitForPendingIconLoadAsync();

        var item = vm.MetaInventoryItems.Single();
        Assert.Null(item.IconPath);
        Assert.Null(item.Icon);
        Assert.Empty(iconDecoder.RequestedPaths);
    }

    /// <summary>The exact "missing/unresolvable icon degrades to text-only" contract — a real IconPath exists, gets requested, but the decoder can't actually produce a picture for it (unmounted base game, unmatched asset, whatever) — the row must be left showing its plain text, never crash or hang the load.</summary>
    [Fact]
    public async Task LoadSlot_IconDecoderReturnsNull_RowStaysTextOnlyWithoutThrowing()
    {
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = MakeProfile(nextChrSlot: 1),
            Mounts = MakeMountsRoot(MakeMount("Rex", 3, "Wolf")),
        };
        var iconDecoder = new FakeBaseGameIconDecoder { PngBytesToReturn = null };
        var vm = CreateViewModel(repository, gameNames: new SaveGameNames(CreateGameDataFolder()), baseGameIconDecoder: iconDecoder);
        await vm.WaitForPendingSlotLoadAsync();
        await vm.WaitForPendingIconLoadAsync();

        var mount = vm.Mounts.Single();
        Assert.NotNull(mount.IconPath);
        Assert.Contains(mount.IconPath, iconDecoder.RequestedPaths);
        Assert.Null(mount.Icon);
        Assert.False(vm.HasUnsavedChanges);
    }

    /// <summary>Same degrade-to-text-only contract, but for bytes the decoder DID return that just aren't a real decodable image — TryDecodeImage's own catch branch, exercised end to end rather than in isolation.</summary>
    [Fact]
    public async Task LoadSlot_IconDecoderReturnsUndecodableBytes_RowStaysTextOnlyWithoutThrowing()
    {
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = MakeProfile(nextChrSlot: 1),
            Mounts = MakeMountsRoot(MakeMount("Rex", 3, "Wolf")),
        };
        var iconDecoder = new FakeBaseGameIconDecoder { PngBytesToReturn = [0x01, 0x02, 0x03] };
        var vm = CreateViewModel(repository, gameNames: new SaveGameNames(CreateGameDataFolder()), baseGameIconDecoder: iconDecoder);
        await vm.WaitForPendingSlotLoadAsync();
        await vm.WaitForPendingIconLoadAsync();

        Assert.Null(vm.Mounts.Single().Icon);
    }

    [Fact]
    public async Task LoadSlot_TwoMountsOfTheSameType_OnlyDecodesTheSharedIconPathOnce()
    {
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = MakeProfile(nextChrSlot: 1),
            Mounts = MakeMountsRoot(MakeMount("Rex", 3, "Wolf"), MakeMount("Fang", 7, "Wolf")),
        };
        var iconDecoder = new FakeBaseGameIconDecoder { PngBytesToReturn = MinimalPng };
        var vm = CreateViewModel(repository, gameNames: new SaveGameNames(CreateGameDataFolder()), baseGameIconDecoder: iconDecoder);
        await vm.WaitForPendingSlotLoadAsync();
        await vm.WaitForPendingIconLoadAsync();

        Assert.All(vm.Mounts, m => Assert.NotNull(m.Icon));
        // Both mounts share the exact same MountType, so the exact same IconPath — the session
        // icon cache should mean the real decode only ever runs once for it. Counted by path
        // rather than asserting RequestedPaths.Count outright, since CreateGameDataFolder's own
        // Bestiary row (PackWolf, a different IconPath entirely) is requested too and isn't what
        // this test is about.
        var mountIconPath = vm.Mounts.First().IconPath;
        Assert.Equal(1, iconDecoder.RequestedPaths.Count(p => p == mountIconPath));
    }

    [Fact]
    public async Task SaveChangesCommand_GameLaunchesWhileConfirmDialogIsUp_AbortsWithoutWriting()
    {
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = MakeProfile(nextChrSlot: 1),
        };
        // false at the FIRST check (before the confirm dialog), true at the SECOND — simulating
        // Icarus being launched while the user was still answering the confirm dialog.
        var gameProcessChecker = new FakeGameProcessChecker(false, true);
        var vm = CreateViewModel(
            repository, dialogService: new FakeDialogService(confirmResult: true), gameProcessChecker: gameProcessChecker);
        await vm.WaitForPendingSlotLoadAsync();

        vm.Characters.Single().Name = "Changed";
        Assert.True(vm.HasUnsavedChanges);

        vm.SaveChangesCommand.Execute(null);

        Assert.Equal(0, repository.SaveCharactersCallCount);
        Assert.Equal(0, repository.BackupSlotCallCount);
        Assert.Contains("running", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        // The edit is still there, unsaved, ready to retry once the game closes.
        Assert.True(vm.HasUnsavedChanges);
        Assert.Equal(2, gameProcessChecker.CallCount);
    }

    /// <summary>
    /// Review finding (2026-10-23): the edits were put into the loaded save BEFORE the late checks,
    /// so after an aborted save an edit the user then undid was still written by the next Save.
    /// </summary>
    [Fact]
    public async Task AbortedSave_ThenUndoneEdit_IsNotWrittenByTheNextSave()
    {
        var repository = new FakeSaveRepository { Characters = [MakeCharacter("Alpha", 0)], Profile = MakeProfile(nextChrSlot: 1) };
        // Not running before the confirm, running at the late check; then never again.
        var gameProcessChecker = new FakeGameProcessChecker(false, true, false, false);
        var vm = CreateViewModel(repository, dialogService: new FakeDialogService(confirmResult: true), gameProcessChecker: gameProcessChecker);
        await vm.WaitForPendingSlotLoadAsync();
        var character = vm.Characters.Single();

        character.Name = "Changed";
        vm.SaveChangesCommand.Execute(null);
        Assert.Equal(0, repository.SaveCharactersCallCount);

        character.Name = "Alpha";   // undone by hand
        character.XpText = "500";   // and another edit, so Characters.json is written
        vm.SaveChangesCommand.Execute(null);

        var saved = Assert.Single(repository.LastSavedCharacters!);
        Assert.Equal("Alpha", saved["CharacterName"]!.GetValue<string>());
        Assert.Equal(500L, saved["XP"]!.GetValue<long>());
    }

    /// <summary>Review finding: "couldn't read the files' times" passed as "unchanged", so Save could write an older copy over the game's newer one.</summary>
    [Fact]
    public async Task SaveWhenTheFilesCantBeChecked_RefusesToWrite()
    {
        var repository = new FakeSaveRepository { Characters = [MakeCharacter("Alpha", 0)], Profile = MakeProfile(nextChrSlot: 1) };
        var vm = CreateViewModel(repository, dialogService: new FakeDialogService(confirmResult: false));
        await vm.WaitForPendingSlotLoadAsync();
        vm.Characters.Single().Name = "Changed";
        repository.StampsError = new IOException("locked");

        vm.SaveChangesCommand.Execute(null);

        Assert.Equal(0, repository.SaveCharactersCallCount);
        Assert.Equal(0, repository.BackupSlotCallCount);
        Assert.True(vm.HasUnsavedChanges);
    }

    /// <summary>Review finding: Duplicate copied the character as last saved while naming it after the edited one.</summary>
    [Fact]
    public async Task Duplicate_CopiesTheCharacterAsEdited()
    {
        var repository = new FakeSaveRepository { Characters = [MakeCharacter("Alpha", 0)], Profile = MakeProfile(nextChrSlot: 1) };
        var vm = CreateViewModel(repository);
        await vm.WaitForPendingSlotLoadAsync();
        var character = vm.Characters.Single();
        character.Name = "Edited";
        character.XpText = "777";

        vm.DuplicateCharacterCommand.Execute(character);

        var copy = vm.Characters.Single(c => c.Name == "Edited (Copy)");
        Assert.Equal(777L, copy.Node["XP"]!.GetValue<long>());
        Assert.Equal(100L, character.Node["XP"]!.GetValue<long>()); // the source's own node isn't changed until Save
    }

    /// <summary>Review finding: when Profile.json couldn't be read the backups list was emptied — exactly when Restore is needed.</summary>
    [Fact]
    public async Task UnreadableSlot_StillListsItsBackups()
    {
        var repository = new FakeSaveRepository { Characters = [MakeCharacter("Alpha", 0)], ProfileLoadError = new InvalidDataException("bad json") };
        var vm = CreateViewModel(repository);
        await vm.WaitForPendingSlotLoadAsync();

        Assert.Contains("Couldn't read this save", vm.StatusMessage);
        Assert.NotEmpty(vm.Backups);
    }

    /// <summary>Researched 2026-10-23: "Exotic_stabilized" was never a currency in the game; it was labelled "Stabilized Exotics" as if it were.</summary>
    [Fact]
    public async Task CurrencyTheGameDoesntHave_IsSaidToBeOne()
    {
        var profile = MakeProfile(nextChrSlot: 0);
        profile["MetaResources"] = new JsonArray(
            new JsonObject { ["MetaRow"] = "Credits", ["Count"] = 10 },
            new JsonObject { ["MetaRow"] = "Exotic_stabilized", ["Count"] = 200000 },
            new JsonObject { ["MetaRow"] = "Biomass_Converter", ["Count"] = 3 });
        var vm = CreateViewModel(new FakeSaveRepository { Profile = profile });
        await vm.WaitForPendingSlotLoadAsync();

        Assert.Equal(["Ren", "Exotic_stabilized (not a game currency)", "Flux"], vm.Currencies.Select(c => c.Label));
    }

    /// <summary>
    /// Researched 2026-10-23: flags_&lt;SteamID&gt;.dat is the game's cached list of owned DLC
    /// (D_DLCPackageData rows), not character flags. Each pack is a toggle under its real name
    /// (the user wants to turn DLC / Supporter Edition content on); toggling one writes the list.
    /// </summary>
    [Fact]
    public async Task FlagsFile_IsTheOwnedDlcList_ByRealName_AndToggleable()
    {
        var data = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests_GameData", Guid.NewGuid().ToString("N"));
        _tempFoldersToCleanUp.Add(data);
        Directory.CreateDirectory(Path.Combine(data, "DLC"));
        Directory.CreateDirectory(Path.Combine(data, "Flags"));
        File.WriteAllText(Path.Combine(data, "DLC", "D_DLCPackageData.json"), """
            { "Rows": [ { "Name": "Deluxe_Edition_Envirosuit" }, { "Name": "Preorder_Content" }, { "Name": "New_Frontiers" }, { "Name": "Dangerous_Horizons" } ] }
            """);
        File.WriteAllText(Path.Combine(data, "Flags", "D_CharacterFlags.json"), """
            { "Rows": [ { "Name": "TestDataFlag" }, { "Name": "TutorialTestFlag" }, { "Name": "FirstScanComplete" }, { "Name": "Talent_RepairBench" } ] }
            """);
        var repository = new FakeSaveRepository { Profile = MakeProfile(nextChrSlot: 0), BinaryFlags = [3, 1] };
        var vm = CreateViewModel(repository, gameNames: new SaveGameNames(data));
        await vm.WaitForPendingSlotLoadAsync();

        Assert.Equal(["Deluxe Edition Envirosuit", "Preorder Content", "New Frontiers", "Dangerous Horizons"], vm.BinaryFlags.Select(f => f.Name));
        Assert.Equal("2 of 4 owned", vm.OwnedDlcSummary);

        vm.BinaryFlags.Single(f => f.Name == "Deluxe Edition Envirosuit").IsUnlocked = true;
        Assert.Equal("3 of 4 owned", vm.OwnedDlcSummary);
        vm.SaveChangesCommand.Execute(null);

        Assert.Equal([3, 1, 0], repository.LastSavedBinaryFlags); // the file's own order kept, new ones after
    }

    /// <summary>
    /// The user's ask (2026-10-23): research that's for a pack shows which ("For the Supporters
    /// Edition"), the save check notes a pack that's off in the cache, and Fix all turns it on only
    /// with its switch on.
    /// </summary>
    [Fact]
    public async Task SaveCheck_DlcNote_NamesThePack_AndIsInFixAllOnlyWithItsSwitch()
    {
        var data = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests_GameData", Guid.NewGuid().ToString("N"));
        _tempFoldersToCleanUp.Add(data);
        Directory.CreateDirectory(Path.Combine(data, "DLC"));
        Directory.CreateDirectory(Path.Combine(data, "Talents"));
        File.WriteAllText(Path.Combine(data, "DLC", "D_DLCPackageData.json"), """
            { "Rows": [ { "Name": "Deluxe_Edition_Envirosuit", "DLCName": "NSLOCTEXT(\"D_DLCPackageData\", \"x\", \"Supporters Edition\")" },
                        { "Name": "Dangerous_Horizons", "DLCName": "NSLOCTEXT(\"D_DLCPackageData\", \"y\", \"Dangerous Horizons\")" } ] }
            """);
        File.WriteAllText(Path.Combine(data, "Talents", "D_Talents.json"), """
            { "Rows": [ { "Name": "Workshop_Supporter_Suit", "RequiredFlags": [ { "RowName": "Deluxe_Edition_Envirosuit", "DataTableName": "D_DLCPackageData" } ] } ] }
            """);
        var profile = MakeProfile(nextChrSlot: 0);
        profile["Talents"] = new JsonArray(new JsonObject { ["RowName"] = "Workshop_Supporter_Suit", ["Rank"] = 1 });
        var repository = new FakeSaveRepository { Profile = profile, BinaryFlags = [1] };
        var vm = CreateViewModel(repository, gameNames: new SaveGameNames(data));
        try
        {
            await vm.WaitForPendingSlotLoadAsync();

            Assert.Equal("For the Supporters Edition", vm.WorkshopTalents.Single().PackNote);
            var note = Assert.Single(vm.CheckFindings);
            Assert.True(note.IsDlc);
            Assert.Equal(SaveCheckSeverity.Note, note.Severity);
            Assert.Contains("the Supporters Edition", note.Message);
            Assert.False(vm.HasFixableFindings);

            vm.FixAllIncludesDlc = true;
            Assert.True(vm.HasFixableFindings);
            vm.FixAllFindingsCommand.Execute(null);

            Assert.True(vm.BinaryFlags.Single(f => f.RowName == "Deluxe_Edition_Envirosuit").IsUnlocked);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>Control for the test above — proves the late re-check doesn't false-positive-block an otherwise-normal save.</summary>
    [Fact]
    public async Task SaveChangesCommand_GameNeverRunning_WritesNormally()
    {
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = MakeProfile(nextChrSlot: 1),
        };
        var gameProcessChecker = new FakeGameProcessChecker(false, false);
        var vm = CreateViewModel(
            repository, dialogService: new FakeDialogService(confirmResult: true), gameProcessChecker: gameProcessChecker);
        await vm.WaitForPendingSlotLoadAsync();

        vm.Characters.Single().Name = "Changed";

        vm.SaveChangesCommand.Execute(null);

        Assert.Equal(1, repository.SaveCharactersCallCount);
        Assert.Equal(1, repository.BackupSlotCallCount);
        Assert.False(vm.HasUnsavedChanges);
        Assert.Equal(2, gameProcessChecker.CallCount);
    }

    /// <summary>RestoreBackupAsync got the exact same late-recheck treatment as SaveChanges (same class of bug, same fix) — this is its regression test.</summary>
    [Fact]
    public async Task RestoreBackupAsyncCommand_GameLaunchesWhileConfirmDialogIsUp_AbortsWithoutRestoring()
    {
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = MakeProfile(nextChrSlot: 1),
            Backups = [new SaveBackupInfo("fake_backup.zip", DateTimeOffset.Now)],
        };
        var gameProcessChecker = new FakeGameProcessChecker(false, true);
        var vm = CreateViewModel(
            repository, dialogService: new FakeDialogService(confirmResult: true), gameProcessChecker: gameProcessChecker);
        await vm.WaitForPendingSlotLoadAsync();
        Assert.NotNull(vm.SelectedBackup);

        await vm.RestoreBackupCommand.ExecuteAsync(null);

        Assert.Equal(0, repository.RestoreSlotCallCount);
        Assert.Contains("running", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    // --- Played with the page open: the save changed on disk after it was read ---

    private static void SimulateGameSaved(FakeSaveRepository repository, string newName)
    {
        repository.Characters = [MakeCharacter(newName, 0)];
        repository.Stamps["Characters.json"] = new SaveFileStamp(new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc), 999);
    }

    /// <summary>The review finding: Save wrote the pre-play copy over newer progress. Now it refuses, and (when the user agrees) reads the newer save.</summary>
    [Fact]
    public async Task SaveChangesCommand_SaveChangedOnDiskSinceLoad_WritesNothingAndReloads()
    {
        var repository = new FakeSaveRepository { Characters = [MakeCharacter("Alpha", 0)], Profile = MakeProfile(nextChrSlot: 1) };
        var vm = CreateViewModel(repository, dialogService: new FakeDialogService(confirmResult: true));
        await vm.WaitForPendingSlotLoadAsync();

        vm.Characters.Single().Name = "Edited";
        SimulateGameSaved(repository, "AfterPlaying");
        vm.SaveChangesCommand.Execute(null);
        await vm.WaitForPendingSlotLoadAsync();

        Assert.Equal(0, repository.SaveCharactersCallCount);
        Assert.Equal(0, repository.SaveProfileCallCount);
        Assert.Equal(0, repository.BackupSlotCallCount);
        Assert.Equal("AfterPlaying", vm.Characters.Single().Name);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task SaveChangesCommand_SaveChangedOnDisk_ReloadDeclined_KeepsTheEditsAndWritesNothing()
    {
        var repository = new FakeSaveRepository { Characters = [MakeCharacter("Alpha", 0)], Profile = MakeProfile(nextChrSlot: 1) };
        var vm = CreateViewModel(repository, dialogService: new FakeDialogService(confirmResult: false));
        await vm.WaitForPendingSlotLoadAsync();

        vm.Characters.Single().Name = "Edited";
        SimulateGameSaved(repository, "AfterPlaying");
        vm.SaveChangesCommand.Execute(null);

        Assert.Equal(0, repository.SaveCharactersCallCount);
        Assert.Equal("Edited", vm.Characters.Single().Name);
        Assert.True(vm.HasUnsavedChanges);
        Assert.Contains("changed on disk", vm.StatusMessage);
    }

    [Fact]
    public async Task OnPageShown_SaveChangedOnDisk_NothingUnsaved_ReadsItAgain()
    {
        var repository = new FakeSaveRepository { Characters = [MakeCharacter("Alpha", 0)], Profile = MakeProfile(nextChrSlot: 1) };
        var vm = CreateViewModel(repository);
        await vm.WaitForPendingSlotLoadAsync();

        vm.OnPageShown();
        await vm.WaitForPendingSlotLoadAsync();
        Assert.Equal("Alpha", vm.Characters.Single().Name);

        SimulateGameSaved(repository, "AfterPlaying");
        vm.OnPageShown();
        await vm.WaitForPendingSlotLoadAsync();

        Assert.Equal("AfterPlaying", vm.Characters.Single().Name);
    }

    [Fact]
    public async Task OnPageShown_SaveChangedOnDisk_WithUnsavedEdits_KeepsThemAndSaysSo()
    {
        var repository = new FakeSaveRepository { Characters = [MakeCharacter("Alpha", 0)], Profile = MakeProfile(nextChrSlot: 1) };
        var vm = CreateViewModel(repository);
        await vm.WaitForPendingSlotLoadAsync();

        vm.Characters.Single().Name = "Edited";
        SimulateGameSaved(repository, "AfterPlaying");
        vm.OnPageShown();
        await vm.WaitForPendingSlotLoadAsync();

        Assert.Equal("Edited", vm.Characters.Single().Name);
        Assert.Contains("changed on disk", vm.StatusMessage);
    }

    [Fact]
    public async Task SaveChangesCommand_AfterASave_TheNextSaveIsNotMistakenForAnOutsideChange()
    {
        var repository = new FakeSaveRepository { Characters = [MakeCharacter("Alpha", 0)], Profile = MakeProfile(nextChrSlot: 1) };
        // Our own write changes the file's stamp; the page must take the new one as its baseline.
        repository.BumpStampOnWrite = true;
        var vm = CreateViewModel(repository);
        await vm.WaitForPendingSlotLoadAsync();

        vm.Characters.Single().Name = "One";
        vm.SaveChangesCommand.Execute(null);
        vm.Characters.Single().Name = "Two";
        vm.SaveChangesCommand.Execute(null);

        Assert.Equal(2, repository.SaveCharactersCallCount);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task ReloadCommand_WithUnsavedEdits_AsksFirstAndReadsTheSaveAgain()
    {
        var repository = new FakeSaveRepository { Characters = [MakeCharacter("Alpha", 0)], Profile = MakeProfile(nextChrSlot: 1) };
        var dialogService = new FakeDialogService(confirmResult: true);
        var vm = CreateViewModel(repository, dialogService: dialogService);
        await vm.WaitForPendingSlotLoadAsync();

        vm.Characters.Single().Name = "Edited";
        vm.ReloadCommand.Execute(null);
        await vm.WaitForPendingSlotLoadAsync();

        Assert.Equal(1, dialogService.ConfirmCallCount);
        Assert.Equal("Alpha", vm.Characters.Single().Name);
        Assert.False(vm.HasUnsavedChanges);
    }

    // --- Review 2026-10-28 (saves journey) ---

    /// <summary>"1,000,000" in the XP box: Save used to say "Saved." while the XP was skipped, leaving the page "Unsaved" with no reason given.</summary>
    [Fact]
    public async Task SaveChangesCommand_XpWithCommas_WritesNothingAndSaysWhichBox()
    {
        var repository = new FakeSaveRepository { Characters = [MakeCharacter("Alpha", 0)], Profile = MakeProfile(nextChrSlot: 1) };
        var vm = CreateViewModel(repository);
        try
        {
            await vm.WaitForPendingSlotLoadAsync();

            vm.Characters.Single().XpText = "1,000,000";
            vm.SaveChangesCommand.Execute(null);

            Assert.Equal(0, repository.SaveCharactersCallCount);
            Assert.Equal(0, repository.BackupSlotCallCount);
            Assert.Contains("Not saved", vm.StatusMessage);
            Assert.Contains("Alpha: XP", vm.StatusMessage);
            Assert.True(vm.HasUnsavedChanges);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>
    /// A bad number box is marked while typing (red border, a line under it): exactly the boxes
    /// Save refuses, by the same rules, and the mark goes when the box is fixed.
    /// </summary>
    [Fact]
    public async Task NumberBoxes_MarkedWhileTyping_ExactlyTheOnesSaveRefuses()
    {
        var profile = MakeProfile(nextChrSlot: 1);
        profile["MetaResources"] = new JsonArray(
            new JsonObject { ["MetaRow"] = "Credits", ["Count"] = 100 },
            new JsonObject { ["MetaRow"] = "Exotic", ["Count"] = 5 });
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = profile,
            Mounts = MakeMountsRoot(MakeMount("Rex", 3, "Wolf")),
        };
        var vm = CreateViewModel(repository);
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            var character = vm.Characters.Single();
            var head = character.Cosmetics.Single();
            var credits = vm.Currencies[0];
            var exotic = vm.Currencies[1];
            var rex = vm.Mounts.Single();
            var told = new List<string>();
            character.PropertyChanged += (_, e) => told.Add("character." + e.PropertyName);
            credits.PropertyChanged += (_, e) => told.Add("credits." + e.PropertyName);

            character.XpText = "1,000";
            head.ValueText = "-12";          // an appearance value may be negative: fine
            credits.CountText = "-5";
            exotic.CountText = "9999999999";
            rex.LevelText = "ten";

            Assert.True(character.HasXpError);
            Assert.Equal("Whole number, digits only", character.XpError);
            Assert.False(head.HasNumberError);
            Assert.Equal("0 to 2,147,483,647", credits.NumberError);
            Assert.Equal("0 to 2,147,483,647", exotic.NumberError);
            Assert.Equal("Whole number, digits only", rex.NumberError);
            Assert.Contains("character.HasXpError", told);
            Assert.Contains("credits.HasNumberError", told);
            Assert.Equal(["Alpha: XP", credits.Label, exotic.Label, "Rex: level"], vm.BadNumberBoxes());

            head.ValueText = "12x";
            Assert.Equal("Whole number, digits only", head.NumberError);
            Assert.Contains($"Alpha: {head.Key}", vm.BadNumberBoxes());

            // Fixed boxes lose the mark, and Save stops naming them.
            character.XpText = "1000";
            head.ValueText = "-12";
            credits.CountText = "0";
            exotic.CountText = "5";
            rex.LevelText = "10";
            Assert.False(character.HasXpError);
            Assert.All(new DirtyRow[] { head, credits, exotic, rex }, r => Assert.Null(r.NumberError));
            Assert.Empty(vm.BadNumberBoxes());
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>A box Save would refuse, typed back to the loaded text, is no edit any more: no mark, just like Save.</summary>
    [Fact]
    public void NumberBox_UneditedIsNeverMarked_EditedBadIs()
    {
        var bestiary = new SaveBestiaryEntryViewModel("Wolf", "Wolf", 1000, false, 40, null, () => { });
        Assert.Null(bestiary.NumberError);

        bestiary.PointsText = "";
        Assert.Equal("Whole number, digits only", bestiary.NumberError);
        Assert.True(bestiary.HasNumberError);

        bestiary.PointsText = "40";
        Assert.False(bestiary.HasNumberError);
    }

    [Theory]
    [InlineData("0", null)]
    [InlineData("2147483647", null)]
    [InlineData(" 12 ", null)]
    [InlineData("", "Whole number, digits only")]
    [InlineData("1,000", "Whole number, digits only")]
    [InlineData("1 000", "Whole number, digits only")]
    [InlineData("12.5", "Whole number, digits only")]
    [InlineData("-1", "0 to 2,147,483,647")]
    [InlineData("2147483648", "0 to 2,147,483,647")]
    public void SaveNumberRules_Count_SaysWhatsWrong_AndAgreesWithTheParse(string text, string? problem)
    {
        Assert.Equal(problem, SaveNumberRules.CountProblem(text));
        Assert.Equal(problem is null, SaveNumberRules.TryParseCount(text, out _));
    }

    [Theory]
    [InlineData("-2147483648", null)]
    [InlineData("-12", null)]
    [InlineData("abc", "Whole number, digits only")]
    [InlineData("-2147483649", "-2,147,483,648 to 2,147,483,647")]
    public void SaveNumberRules_Whole_SaysWhatsWrong_AndAgreesWithTheParse(string text, string? problem)
    {
        Assert.Equal(problem, SaveNumberRules.WholeProblem(text));
        Assert.Equal(problem is null, SaveNumberRules.TryParseWhole(text, out _));
    }

    /// <summary>
    /// A backup that failed (the game held a file) left the edits applied in the loaded save: an
    /// edit the user then set back was written anyway by the next Save of another character.
    /// </summary>
    [Fact]
    public async Task SaveChangesCommand_BackupFailed_AnEditSetBackIsNotWrittenByTheNextSave()
    {
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0), MakeCharacter("Beta", 1)],
            Profile = MakeProfile(nextChrSlot: 2),
            BackupError = new IOException("The process cannot access the file because it is being used by another process."),
        };
        var vm = CreateViewModel(repository);
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            var alpha = vm.Characters[0];

            alpha.Name = "Renamed";
            vm.SaveChangesCommand.Execute(null);
            Assert.Equal(0, repository.SaveCharactersCallCount);
            Assert.Contains("nothing was written", vm.StatusMessage);

            alpha.Name = "Alpha";
            vm.Characters[1].Name = "Beta Two";
            vm.SaveChangesCommand.Execute(null);

            var saved = repository.LastSavedCharacters!;
            Assert.Equal("Alpha", saved[0]["CharacterName"]!.GetValue<string>());
            Assert.Equal("Beta Two", saved[1]["CharacterName"]!.GetValue<string>());
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>Two characters with the same name: the save check's merged row fixed only the first of them.</summary>
    [Fact]
    public async Task SaveCheck_TwoCharactersWithOneName_FixAllFixesBoth()
    {
        var first = MakeCharacter("Bob", 0);
        first["XP"] = -5L;
        var second = MakeCharacter("Bob", 1);
        second["XP"] = -7L;
        var vm = CreateViewModel(new FakeSaveRepository { Characters = [first, second], Profile = MakeProfile(nextChrSlot: 2) });
        try
        {
            await vm.WaitForPendingSlotLoadAsync();

            vm.FixAllFindingsCommand.Execute(null);

            Assert.All(vm.Characters, c => Assert.Equal("0", c.XpText));
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>An accolade entry the page can't name isn't listed, so nobody took it out: one accolade edit used to drop it from the file.</summary>
    [Fact]
    public async Task SaveChangesCommand_AccoladeEdited_KeepsEntriesThePageCantName()
    {
        var odd = new JsonObject { ["Accolade"] = new JsonObject { ["DataTableName"] = "D_Accolades" }, ["TimeCompleted"] = "2026.01.01-00.00.00" };
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = MakeProfile(nextChrSlot: 1),
            Accolades = new JsonObject
            {
                ["CompletedAccolades"] = new JsonArray(
                    new JsonObject { ["Accolade"] = new JsonObject { ["RowName"] = "EquipFullArmour", ["DataTableName"] = "D_Accolades" } },
                    odd.DeepClone()),
                ["PlayerTrackers"] = new JsonArray(),
            },
        };
        var vm = CreateViewModel(repository);
        try
        {
            await vm.WaitForPendingSlotLoadAsync();

            vm.Accolades.Single(a => a.RowName == "EquipFullArmour").IsCompleted = false;
            vm.SaveChangesCommand.Execute(null);

            var written = repository.Accolades["CompletedAccolades"]!.AsArray();
            Assert.Equal(odd.ToJsonString(), Assert.Single(written)!.ToJsonString());
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>A talent edit rebuilt every entry as a bare {RowName, Rank} and dropped one it couldn't read: a field a game update adds was lost on the first edit.</summary>
    [Fact]
    public async Task SaveChangesCommand_TalentRanked_KeepsTheEntrysOtherFieldsAndEntriesItCantRead()
    {
        var character = MakeCharacter("Alpha", 0);
        character["Talents"] = new JsonArray(
            new JsonObject { ["RowName"] = "Solo_Stamina", ["Rank"] = 2, ["AddedByAGameUpdate"] = "keep me" },
            new JsonObject { ["Unreadable"] = 1 });
        var repository = new FakeSaveRepository { Characters = [character], Profile = MakeProfile(nextChrSlot: 1) };
        var vm = CreateViewModel(repository);
        try
        {
            await vm.WaitForPendingSlotLoadAsync();

            vm.Characters.Single().Talents.Single(t => t.RowName == "Solo_Stamina").RankUpCommand.Execute(null);
            vm.SaveChangesCommand.Execute(null);

            var talents = repository.LastSavedCharacters!.Single()["Talents"]!.AsArray();
            Assert.Equal(2, talents.Count);
            Assert.Equal(3, talents[0]!["Rank"]!.GetValue<int>());
            Assert.Equal("keep me", talents[0]!["AddedByAGameUpdate"]!.GetValue<string>());
            Assert.Equal(1, talents[1]!["Unreadable"]!.GetValue<int>());
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>Remove leftovers sets a rank straight to 0: a talent that was at its highest rank kept + greyed out.</summary>
    [Fact]
    public void TalentRow_RankSetFromOutside_RefreshesWhetherItCanRankUp()
    {
        var talent = new SaveTalentViewModel("Mod_Talent", new TalentDisplayInfo("Mod Talent", "", "Tree", 1, false), 1, () => { });
        var raised = new List<string?>();
        talent.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.False(talent.CanRankUp);

        talent.Rank = 0;

        Assert.True(talent.CanRankUp);
        Assert.Contains(nameof(SaveTalentViewModel.CanRankUp), raised);
    }

    // --- Each file is written only when its own section changed ---

    [Fact]
    public async Task SaveChangesCommand_OnlyACharacterEdited_WritesCharactersNotProfile()
    {
        var repository = new FakeSaveRepository { Characters = [MakeCharacter("Alpha", 0)], Profile = MakeProfile(nextChrSlot: 1) };
        var vm = CreateViewModel(repository);
        await vm.WaitForPendingSlotLoadAsync();

        vm.Characters.Single().Name = "Edited";
        vm.SaveChangesCommand.Execute(null);

        Assert.Equal(1, repository.SaveCharactersCallCount);
        Assert.Equal(0, repository.SaveProfileCallCount);
    }

    [Fact]
    public async Task SaveChangesCommand_OnlyACurrencyEdited_WritesProfileNotCharacters()
    {
        var profile = MakeProfile(nextChrSlot: 1);
        profile["MetaResources"] = new JsonArray(new JsonObject { ["MetaRow"] = "Credits", ["Count"] = 100 });
        var repository = new FakeSaveRepository { Characters = [MakeCharacter("Alpha", 0)], Profile = profile };
        var vm = CreateViewModel(repository);
        await vm.WaitForPendingSlotLoadAsync();

        vm.Currencies.Single().CountText = "250";
        vm.SaveChangesCommand.Execute(null);

        Assert.Equal(0, repository.SaveCharactersCallCount);
        Assert.Equal(1, repository.SaveProfileCallCount);
        Assert.Equal(250, repository.Profile["MetaResources"]![0]!["Count"]!.GetValue<long>());
    }

    [Fact]
    public async Task SaveChangesCommand_AfterDuplicate_WritesProfileTooForTheAdvancedNextChrSlot()
    {
        var repository = new FakeSaveRepository { Characters = [MakeCharacter("Alpha", 0)], Profile = MakeProfile(nextChrSlot: 1) };
        var vm = CreateViewModel(repository);
        await vm.WaitForPendingSlotLoadAsync();

        vm.DuplicateCharacterCommand.Execute(vm.Characters.Single());
        vm.SaveChangesCommand.Execute(null);

        Assert.Equal(1, repository.SaveCharactersCallCount);
        Assert.Equal(1, repository.SaveProfileCallCount);
        Assert.Equal(2, repository.Profile["NextChrSlot"]!.GetValue<int>());
    }

    // --- Tolerant reads: a changed field shape hides one section, not the slot ---

    [Fact]
    public async Task LoadSlot_NumbersAndBoolsInOtherShapes_StillRead()
    {
        var character = MakeCharacter("Alpha", 0);
        character["XP"] = 1500.0;
        character["ChrSlot"] = "0";
        character["IsDead"] = 1;
        var profile = MakeProfile(nextChrSlot: 1);
        profile["UnlockedFlags"] = new JsonArray(3.0, "4", 5);
        var repository = new FakeSaveRepository
        {
            Characters = [character],
            Profile = profile,
            Mounts = MakeMountsRoot(new JsonObject { ["MountName"] = "Rex", ["MountLevel"] = "7", ["MountType"] = "Wolf" }),
        };
        var vm = CreateViewModel(repository);
        await vm.WaitForPendingSlotLoadAsync();

        var loaded = vm.Characters.Single();
        Assert.Equal("1500", loaded.XpText);
        Assert.True(loaded.IsDead);
        Assert.Equal("7", vm.Mounts.Single().LevelText);
        Assert.Equal([3, 4, 5], vm.AccountFlags.Where(f => f.IsUnlocked).Select(f => f.Id));
        Assert.Null(vm.SkippedSectionsNote);
    }

    [Fact]
    public async Task LoadSlot_OneUnreadableFile_HidesOnlyThatSection_AndSaveLeavesItAlone()
    {
        var repository = new FakeSaveRepository
        {
            Characters = [MakeCharacter("Alpha", 0)],
            Profile = MakeProfile(nextChrSlot: 1),
            MountsLoadError = new FormatException("Mounts.json isn't a JSON object."),
        };
        var vm = CreateViewModel(repository);
        await vm.WaitForPendingSlotLoadAsync();

        Assert.Single(vm.Characters);
        Assert.Empty(vm.Mounts);
        Assert.Contains("mounts", vm.SkippedSectionsNote);
        Assert.Null(vm.StatusMessage);

        vm.Characters.Single().Name = "Edited";
        vm.SaveChangesCommand.Execute(null);

        Assert.Equal(1, repository.SaveCharactersCallCount);
        Assert.Equal(0, repository.SaveMountsCallCount);
    }

    /// <summary>Profile.json lists are rebuilt only when edited: a section that failed to load (empty) or simply wasn't touched keeps the save's own list exactly (here, a duplicate entry a rebuild would merge).</summary>
    [Fact]
    public async Task SaveChangesCommand_CurrencyEdited_LeavesTheResearchListExactlyAsItWas()
    {
        var profile = MakeProfile(nextChrSlot: 1);
        profile["MetaResources"] = new JsonArray(new JsonObject { ["MetaRow"] = "Credits", ["Count"] = 100 });
        var research = new JsonArray(new JsonObject { ["RowName"] = "Workshop_Envirosuit", ["Rank"] = 1 }, new JsonObject { ["RowName"] = "Workshop_Envirosuit", ["Rank"] = 1 });
        profile["Talents"] = research;
        var before = research.ToJsonString();
        var repository = new FakeSaveRepository { Characters = [MakeCharacter("Alpha", 0)], Profile = profile };
        var vm = CreateViewModel(repository);
        await vm.WaitForPendingSlotLoadAsync();

        vm.Currencies.Single().CountText = "250";
        vm.SaveChangesCommand.Execute(null);

        // The same list object with the same text, not just the same count: a rebuild that kept
        // both duplicate entries would still have 2, so a count alone couldn't fail.
        Assert.Same(research, repository.Profile["Talents"]);
        Assert.Equal(before, repository.Profile["Talents"]!.ToJsonString());
    }

    /// <summary>The user's slot showed a blank Items tab: its bank was simply empty, so the tab says what would be there.</summary>
    [Fact]
    public async Task ItemsTab_SaysWhyItsEmpty_AnEmptyBankOrASearchThatMatchesNothing()
    {
        var repository = new FakeSaveRepository { Characters = [MakeCharacter("Alpha", 0)], Profile = MakeProfile(nextChrSlot: 1) };
        var vm = CreateViewModel(repository);
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            Assert.Contains("item bank is empty", vm.ItemsEmptyText);

            repository.MetaInventory = MakeMetaInventoryRoot(MakeMetaInventoryItem("Unknown_RowName"));
            vm.ReloadCommand.Execute(null);
            await vm.WaitForPendingSlotLoadAsync();
            Assert.Null(vm.ItemsEmptyText);

            vm.ItemSearchText = "nothing like it";
            Assert.Contains("matches the search", vm.ItemsEmptyText);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>Missions' "Only not done", and a mission the game's tables don't list read without its "Prospect_" prefix.</summary>
    [Fact]
    public async Task Missions_OnlyNotDone_ListsTheUndone_AndAnUnknownMissionReadsPlainly()
    {
        var dataFolder = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests_GameData", Guid.NewGuid().ToString("N"));
        _tempFoldersToCleanUp.Add(dataFolder);
        Directory.CreateDirectory(Path.Combine(dataFolder, "Talents"));
        File.WriteAllText(Path.Combine(dataFolder, "Talents", "D_Talents.json"), """
            { "Rows": [ { "Name": "Prospect_RL_Ext", "TalentTree": { "RowName": "Prospect_Olympus" } } ] }
            """);
        var profile = MakeProfile(nextChrSlot: 0);
        profile["Talents"] = new JsonArray(new JsonObject { ["RowName"] = "Prospect_OLY_Glacier_Exploration", ["Rank"] = 1 });
        var vm = CreateViewModel(new FakeSaveRepository { Profile = profile }, gameNames: new SaveGameNames(dataFolder));
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            var unknown = Assert.Single(vm.FilteredMissions, m => m.RowName == "Prospect_OLY_Glacier_Exploration");
            Assert.Equal("OLY Glacier Exploration", unknown.DisplayName);
            Assert.False(unknown.IsInGameTables);
            Assert.Equal(2, vm.FilteredMissions.Count);

            vm.ShowUndoneMissionsOnly = true;

            Assert.Equal(["Prospect_RL_Ext"], vm.FilteredMissions.Select(m => m.RowName));
            Assert.Equal("1 of 2 done", vm.MissionsSummary);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>Unlocks: a one-mission reward's link is said once in the section, not on every row; the headings count what's on.</summary>
    [Fact]
    public async Task Unlocks_AOneMissionRewardKeepsItsLinkOffTheRow_AndTheHeadingCountsWhatsOn()
    {
        var dataFolder = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests_GameData", Guid.NewGuid().ToString("N"));
        _tempFoldersToCleanUp.Add(dataFolder);
        Directory.CreateDirectory(Path.Combine(dataFolder, "Flags"));
        Directory.CreateDirectory(Path.Combine(dataFolder, "Talents"));
        File.WriteAllText(Path.Combine(dataFolder, "Flags", "D_AccountFlags.json"), """
            { "Rows": [ { "Name": "GrantedBlueprint_Bait", "BlueprintUnlocks": [ { "RowName": "Bait" } ],
                          "RewardedFromMissions": [ { "RowName": "Tier3_Ext", "DataTableName": "D_ProspectList" } ] } ] }
            """);
        File.WriteAllText(Path.Combine(dataFolder, "Talents", "D_Talents.json"), """
            { "Rows": [ { "Name": "Prospect_RL_Ext", "ExtraData": { "RowName": "Tier3_Ext", "DataTableName": "D_ProspectList" }, "TalentTree": { "RowName": "Prospect_Olympus" } } ] }
            """);
        var vm = CreateViewModel(new FakeSaveRepository { Profile = MakeProfile(nextChrSlot: 0) }, gameNames: new SaveGameNames(dataFolder));
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            var reward = Assert.Single(vm.RewardFlags);
            Assert.NotNull(reward.LinkHint);
            Assert.False(reward.ShowLinkHint);
            Assert.Equal("0 of 1 on", vm.RewardFlagsSummary);

            reward.IsUnlocked = true;

            Assert.Equal("1 of 1 on", vm.RewardFlagsSummary);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    private sealed class FakeSaveRepository : ISaveRepository
    {
        public SaveSlot Slot { get; set; } = new(SteamId, $@"C:\fake\PlayerData\{SteamId}", "Tester");
        public JsonObject Profile { get; set; } = new();
        public List<JsonObject> Characters { get; set; } = [];
        public JsonObject Mounts { get; set; } = new() { ["SavedMounts"] = new JsonArray() };
        public JsonObject MetaInventory { get; set; } = new() { ["Items"] = new JsonArray() };
        public JsonObject Accolades { get; set; } = new() { ["CompletedAccolades"] = new JsonArray() };
        public JsonObject Bestiary { get; set; } = new() { ["BestiaryTracking"] = new JsonArray(), ["FishTracking"] = new JsonArray() };
        public List<SaveBackupInfo> Backups { get; set; } = [new("fake_backup.zip", DateTimeOffset.Now)];
        public IReadOnlyList<int>? BinaryFlags { get; set; }

        public int SaveCharactersCallCount { get; private set; }
        public int SaveProfileCallCount { get; private set; }
        public int SaveMountsCallCount { get; private set; }
        public int BackupSlotCallCount { get; private set; }

        /// <summary>What GetFileStamps returns; a test changes it to stand in for the game rewriting a file.</summary>
        public Dictionary<string, SaveFileStamp?> Stamps { get; set; } = new() { ["Profile.json"] = new(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 100) };

        /// <summary>When set, LoadMounts throws it (an unreadable Mounts.json).</summary>
        public Exception? MountsLoadError { get; set; }

        public IReadOnlyDictionary<string, SaveFileStamp?> GetFileStamps(string steamId) =>
            StampsError is { } error ? throw error : new Dictionary<string, SaveFileStamp?>(Stamps);

        /// <summary>When set, GetFileStamps throws it.</summary>
        public Exception? StampsError { get; set; }

        /// <summary>When set, ListSlots throws it (an unreadable PlayerData folder).</summary>
        public Exception? ListSlotsError { get; set; }

        /// <summary>When set, SaveProfile throws it once (a write that fails after Characters.json was written).</summary>
        public Exception? SaveProfileError { get; set; }

        /// <summary>When set, LoadProfile waits for it (a slow disk), so a second load can start meanwhile.</summary>
        public SemaphoreSlim? LoadGate { get; set; }
        public int RestoreSlotCallCount { get; private set; }
        public IReadOnlyList<JsonObject>? LastSavedCharacters { get; private set; }
        public JsonObject? LastSavedMounts { get; private set; }

        public IReadOnlyList<SaveSlot> ListSlots() => ListSlotsError is { } error ? throw error : [Slot];

        /// <summary>When set, LoadProfile throws it (an unreadable Profile.json: the whole slot fails to load).</summary>
        public Exception? ProfileLoadError { get; set; }

        public JsonObject LoadProfile(string steamId)
        {
            LoadGate?.Wait();
            return ProfileLoadError is { } error ? throw error : Profile;
        }

        public IReadOnlyList<JsonObject> LoadCharacters(string steamId) => Characters;

        public string? SaveProfile(string steamId, JsonObject profile, bool takeBackup = true)
        {
            SaveProfileCallCount++;
            if (SaveProfileError is { } error)
            {
                SaveProfileError = null;
                throw error;
            }

            Profile = profile;
            return null;
        }

        /// <summary>SaveCharacters changes the Characters.json stamp, like a real write does.</summary>
        public bool BumpStampOnWrite { get; set; }

        public string? SaveCharacters(string steamId, IReadOnlyList<JsonObject> characters, bool takeBackup = true)
        {
            SaveCharactersCallCount++;
            if (BumpStampOnWrite)
            {
                Stamps["Characters.json"] = new SaveFileStamp(DateTime.UtcNow, SaveCharactersCallCount);
            }

            LastSavedCharacters = characters;
            return null;
        }

        public JsonObject LoadAccolades(string steamId) => Accolades;

        public string? SaveAccolades(string steamId, JsonObject accolades, bool takeBackup = true)
        {
            Accolades = accolades;
            return null;
        }

        public JsonObject LoadBestiary(string steamId) => Bestiary;

        public string? SaveBestiary(string steamId, JsonObject bestiary, bool takeBackup = true)
        {
            Bestiary = bestiary;
            return null;
        }

        public JsonObject LoadMetaInventory(string steamId) => MetaInventory;

        public string? SaveMetaInventory(string steamId, JsonObject metaInventory, bool takeBackup = true)
        {
            MetaInventory = metaInventory;
            return null;
        }

        public JsonObject LoadMounts(string steamId) => MountsLoadError is { } error ? throw error : Mounts;

        public string? SaveMounts(string steamId, JsonObject mounts, bool takeBackup = true)
        {
            SaveMountsCallCount++;
            LastSavedMounts = mounts;
            Mounts = mounts;
            return null;
        }

        public IReadOnlyList<int>? LoadBinaryFlags(string steamId) => BinaryFlags;

        public IReadOnlyList<int>? LastSavedBinaryFlags { get; private set; }

        public string? SaveBinaryFlags(string steamId, IReadOnlyList<int> flagIds, bool takeBackup = true)
        {
            LastSavedBinaryFlags = flagIds;
            return null;
        }

        /// <summary>When set, BackupSlot throws it once (a save file the game held while zipping).</summary>
        public Exception? BackupError { get; set; }

        public string BackupSlot(string steamId)
        {
            BackupSlotCallCount++;
            if (BackupError is { } error)
            {
                BackupError = null;
                throw error;
            }

            return "fake_backup.zip";
        }

        public string BackupSlotManually(string steamId)
        {
            BackupSlotCallCount++;
            return "fake_manual_backup.zip";
        }

        public IReadOnlyList<SaveBackupInfo> ListBackups(string steamId) => BackupsError is { } error ? throw error : Backups;

        /// <summary>When set, ListBackups throws it (an unreadable backups folder).</summary>
        public Exception? BackupsError { get; set; }

        public string RestoreSlot(string steamId, string backupFilePath)
        {
            RestoreSlotCallCount++;
            return "fake_pre_restore.zip";
        }
    }

    /// <summary>Answers confirmResult to every question, or the given answers in order (then the last one again).</summary>
    private sealed class FakeDialogService(bool confirmResult, params bool[] answers) : IDialogService
    {
        public int ConfirmCallCount { get; private set; }

        public bool Confirm(string message, string title, ThemedConfirmSeverity severity)
        {
            ConfirmCallCount++;
            return answers.Length == 0 ? confirmResult : answers[Math.Min(ConfirmCallCount - 1, answers.Length - 1)];
        }

        public RenamePromptResult PromptRename(
            string currentName,
            string description = "",
            string? resetValue = null,
            string resetLabel = "",
            string resetTooltip = "",
            string title = "",
            string fieldLabel = "") => new(true, null);
    }

    /// <summary>Returns each answer in responses in order, then repeats the last one — lets a test say "not running when SaveChanges/RestoreBackupAsync first checks, but running by the time of the late, immediately-before-the-write recheck".</summary>
    private sealed class FakeGameProcessChecker(params bool[] responses) : IGameProcessChecker
    {
        private int _index;

        public int CallCount { get; private set; }

        public bool IsRunning()
        {
            CallCount++;
            if (responses.Length == 0)
            {
                return false;
            }

            var value = responses[Math.Min(_index, responses.Length - 1)];
            _index++;
            return value;
        }
    }

    private sealed class FakeActivityLog : IActivityLog
    {
        public ObservableCollection<ActivityEntry> Entries { get; } = [];

        public void Log(string message, ActivityEntryKind kind = ActivityEntryKind.Info) =>
            Entries.Insert(0, new ActivityEntry(message, kind, DateTimeOffset.Now));
    }

    /// <summary>
    /// Stands in for CueBaseGameIconDecoder in every icon test above — records every gameIconPath
    /// it was actually asked to decode (RequestedPaths), so a test can assert an icon-resolution
    /// attempt genuinely happened (or genuinely didn't, for a row with no IconPath at all) — the
    /// same "the call itself is the signal under test, not what a real decode would produce" posture
    /// CueUassetDecoderTests' own FakeBaseGameContentProvider already establishes for
    /// ApplyBaseGameFallbackIfNeeded. Returns whatever PngBytesToReturn is currently set to — null
    /// by default (an unresolvable icon), swappable per test to exercise a real decode instead.
    /// </summary>
    private sealed class FakeBaseGameIconDecoder : IBaseGameIconDecoder
    {
        public byte[]? PngBytesToReturn { get; set; }

        public List<string?> RequestedPaths { get; } = [];

        public byte[]? TryDecodeIconToPng(string? gameIconPath)
        {
            RequestedPaths.Add(gameIconPath);
            return PngBytesToReturn;
        }
    }

    public void Dispose()
    {
        foreach (var folder in _tempFoldersToCleanUp)
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}
