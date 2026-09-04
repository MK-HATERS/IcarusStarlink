using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
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
        SaveGameNames? gameNames = null, FakeBaseGameIconDecoder? baseGameIconDecoder = null) =>
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
            baseGameIconDecoder ?? new FakeBaseGameIconDecoder());

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
        public int BackupSlotCallCount { get; private set; }
        public int RestoreSlotCallCount { get; private set; }
        public IReadOnlyList<JsonObject>? LastSavedCharacters { get; private set; }
        public JsonObject? LastSavedMounts { get; private set; }

        public IReadOnlyList<SaveSlot> ListSlots() => [Slot];

        public JsonObject LoadProfile(string steamId) => Profile;

        public IReadOnlyList<JsonObject> LoadCharacters(string steamId) => Characters;

        public string? SaveProfile(string steamId, JsonObject profile, bool takeBackup = true)
        {
            Profile = profile;
            return null;
        }

        public string? SaveCharacters(string steamId, IReadOnlyList<JsonObject> characters, bool takeBackup = true)
        {
            SaveCharactersCallCount++;
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

        public JsonObject LoadMounts(string steamId) => Mounts;

        public string? SaveMounts(string steamId, JsonObject mounts, bool takeBackup = true)
        {
            LastSavedMounts = mounts;
            Mounts = mounts;
            return null;
        }

        public IReadOnlyList<int>? LoadBinaryFlags(string steamId) => BinaryFlags;

        public string? SaveBinaryFlags(string steamId, IReadOnlyList<int> flagIds, bool takeBackup = true) => null;

        public string BackupSlot(string steamId)
        {
            BackupSlotCallCount++;
            return "fake_backup.zip";
        }

        public IReadOnlyList<SaveBackupInfo> ListBackups(string steamId) => Backups;

        public string RestoreSlot(string steamId, string backupFilePath)
        {
            RestoreSlotCallCount++;
            return "fake_pre_restore.zip";
        }
    }

    private sealed class FakeDialogService(bool confirmResult) : IDialogService
    {
        public int ConfirmCallCount { get; private set; }

        public bool Confirm(string message, string title, ThemedConfirmSeverity severity)
        {
            ConfirmCallCount++;
            return confirmResult;
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
