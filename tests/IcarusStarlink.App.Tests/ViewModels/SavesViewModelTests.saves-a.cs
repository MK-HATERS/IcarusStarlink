using System.Collections.Specialized;
using System.IO;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Saves;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>Save check, currencies, repeated entries, a save that fails partway, and mounts.</summary>
public sealed partial class SavesViewModelTests
{
    private static SaveModSources.Index ModIndex(
        bool canTellInstalled,
        Dictionary<string, HashSet<string>>? gameRows = null,
        Dictionary<string, int>? installedTalentRanks = null) => new(
        gameRows ?? new Dictionary<string, HashSet<string>>
        {
            [SaveModSources.TalentsFile] = new(StringComparer.OrdinalIgnoreCase) { "Some_Talent" },
            [SaveModSources.ItemsFile] = new(StringComparer.OrdinalIgnoreCase) { "Some_Item" },
            [SaveModSources.CurrencyFile] = new(StringComparer.OrdinalIgnoreCase) { "Credits" },
            [SaveModSources.BestiaryFile] = new(StringComparer.OrdinalIgnoreCase) { "PackWolf" },
            [SaveModSources.AccoladesFile] = new(StringComparer.OrdinalIgnoreCase) { "Real_Accolade" },
        },
        new Dictionary<string, HashSet<string>>(),
        new Dictionary<string, Dictionary<string, List<string>>>(),
        GameRowHistory.Empty,
        canTellInstalled,
        installedTalentRanks: installedTalentRanks);

    /// <summary>A save with RockDog at 40 points and Old_Accolade earned, neither in the game's tables (PackWolf and Real_Accolade are).</summary>
    private (FakeSaveRepository Repository, string Data) RockDogAndOldAccolade()
    {
        var data = CreateGameDataFolder();
        Directory.CreateDirectory(Path.Combine(data, "Accolades"));
        File.WriteAllText(Path.Combine(data, "Accolades", "D_Accolades.json"), """{ "Rows": [ { "Name": "Real_Accolade" } ] }""");
        var repository = new FakeSaveRepository
        {
            Profile = MakeProfile(nextChrSlot: 0),
            Bestiary = new JsonObject
            {
                ["BestiaryTracking"] = new JsonArray(
                    new JsonObject { ["BestiaryGroup"] = new JsonObject { ["RowName"] = "RockDog" }, ["NumPoints"] = 40 }),
                ["FishTracking"] = new JsonArray(),
            },
            Accolades = new JsonObject
            {
                ["CompletedAccolades"] = new JsonArray(
                    new JsonObject { ["Accolade"] = new JsonObject { ["RowName"] = "Old_Accolade" }, ["TimeCompleted"] = "2024.01.01-00.00.00" }),
            },
        };
        return (repository, data);
    }

    /// <summary>
    /// Regression: a creature or accolade the extracted tables don't have, from a source Starlink
    /// couldn't place (older game data, or installed mods it couldn't read), was offered as
    /// "Take X out": a newly added game accolade could be deleted that way.
    /// </summary>
    [Fact]
    public async Task CreatureAndAccoladeItCantPlace_AreNotOfferedForRemoval()
    {
        var (repository, data) = RockDogAndOldAccolade();
        var vm = CreateViewModel(repository, gameNames: new SaveGameNames(data), readModSources: () => ModIndex(canTellInstalled: false));
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            await vm.WaitForModSourcesAsync();

            var creature = Assert.Single(vm.CheckFindings, f => f.Message.StartsWith("RockDog", StringComparison.Ordinal));
            Assert.False(creature.CanFix);
            var accolade = Assert.Single(vm.CheckFindings, f => f.Message.StartsWith("Old_Accolade", StringComparison.Ordinal));
            Assert.False(accolade.CanFix);
            Assert.Contains("left as it is", accolade.Message);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>Regression: while the first read of the installed mods was still running, every creature a mod adds had a "Take X out" fix.</summary>
    [Fact]
    public async Task CreatureFinding_HasNoFix_WhileTheModSourcesAreBeingRead()
    {
        var (repository, data) = RockDogAndOldAccolade();
        using var gate = new ManualResetEventSlim(false);
        var vm = CreateViewModel(repository, gameNames: new SaveGameNames(data), readModSources: () =>
        {
            gate.Wait();
            return ModIndex(canTellInstalled: true);
        });
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            Assert.DoesNotContain(vm.CheckFindings, f => f.Message.StartsWith("RockDog", StringComparison.Ordinal) && f.CanFix);

            gate.Set();
            await vm.WaitForModSourcesAsync();

            var creature = Assert.Single(vm.CheckFindings, f => f.Message.StartsWith("RockDog", StringComparison.Ordinal));
            Assert.Equal("Take RockDog out", creature.FixLabel);
            Assert.True(creature.SafeInFixAll);
        }
        finally
        {
            gate.Set();
            await vm.WaitForModSourcesAsync();
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    private string GameDataWithCurrencyTable(params string[] currencyRows)
    {
        var data = CreateGameDataFolder();
        Directory.CreateDirectory(Path.Combine(data, "Currency"));
        File.WriteAllText(Path.Combine(data, "Currency", "D_MetaCurrency.json"),
            new JsonObject { ["Rows"] = new JsonArray([.. currencyRows.Select(r => (JsonNode)new JsonObject { ["Name"] = r })]) }.ToJsonString());
        return data;
    }

    /// <summary>Regression: a currency a game update added (in the game's own table) read as "not a game currency" and could be taken out.</summary>
    [Fact]
    public async Task CurrencyInTheGamesTable_IsNotOfferedForRemoval()
    {
        var data = GameDataWithCurrencyTable("Credits", "Exotic_New");
        var profile = MakeProfile(nextChrSlot: 0);
        profile["MetaResources"] = new JsonArray(new JsonObject { ["MetaRow"] = "Exotic_New", ["Count"] = 50 });
        var vm = CreateViewModel(new FakeSaveRepository { Profile = profile }, gameNames: new SaveGameNames(data));
        try
        {
            await vm.WaitForPendingSlotLoadAsync();

            var currency = Assert.Single(vm.Currencies);
            Assert.True(currency.IsGameCurrency);
            Assert.False(currency.IsUnknown);
            Assert.DoesNotContain("not a game currency", currency.Label);
            Assert.DoesNotContain(vm.CheckFindings, f => f.FixLabel == "Take Exotic_New out");
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>Regression: until the first read of the installed mods finished, a mod's own currency showed a Remove button.</summary>
    [Fact]
    public async Task ModCurrency_IsNotRemovable_WhileTheModSourcesAreBeingRead()
    {
        var data = GameDataWithCurrencyTable("Credits");
        var profile = MakeProfile(nextChrSlot: 0);
        profile["MetaResources"] = new JsonArray(new JsonObject { ["MetaRow"] = "Mod_Coin", ["Count"] = 7 });
        using var gate = new ManualResetEventSlim(false);
        var vm = CreateViewModel(new FakeSaveRepository { Profile = profile }, gameNames: new SaveGameNames(data), readModSources: () =>
        {
            gate.Wait();
            return ModIndex(canTellInstalled: true);
        });
        try
        {
            await vm.WaitForPendingSlotLoadAsync();

            var currency = Assert.Single(vm.Currencies);
            Assert.False(currency.IsUnknown);
            Assert.DoesNotContain(vm.CheckFindings, f => f.FixLabel == "Take Mod_Coin out");
        }
        finally
        {
            gate.Set();
            await vm.WaitForModSourcesAsync();
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>A talent "Big" the game's table gives 3 ranks, at rank 5 on a character.</summary>
    private (FakeSaveRepository Repository, string Data) BigAtRankFive()
    {
        var data = CreateGameDataFolder();
        Directory.CreateDirectory(Path.Combine(data, "Talents"));
        File.WriteAllText(Path.Combine(data, "Talents", "D_Talents.json"), """
            { "Rows": [ { "Name": "Big", "TalentTree": { "RowName": "Tree_A" }, "Rewards": [ {}, {}, {} ] } ] }
            """);
        File.WriteAllText(Path.Combine(data, "Talents", "D_TalentTrees.json"), """{ "Rows": [ { "Name": "Tree_A", "Archetype": { "RowName": "Survival" } } ] }""");
        File.WriteAllText(Path.Combine(data, "Talents", "D_TalentArchetypes.json"), """{ "Rows": [ { "Name": "Survival", "Model": { "RowName": "Player" } } ] }""");
        var character = MakeCharacter("Alpha", 0);
        character["Talents"] = new JsonArray(new JsonObject { ["RowName"] = "Big", ["Rank"] = 5 });
        return (new FakeSaveRepository { Characters = [character], Profile = MakeProfile(nextChrSlot: 1) }, data);
    }

    private static Dictionary<string, HashSet<string>> BigGameRows() => new()
    {
        [SaveModSources.TalentsFile] = new(StringComparer.OrdinalIgnoreCase) { "Big" },
        [SaveModSources.ItemsFile] = new(StringComparer.OrdinalIgnoreCase) { "x" },
    };

    /// <summary>Regression: a mod that adds Rewards tiers makes rank 5 legal, but Fix all set it back to the base game's 3.</summary>
    [Fact]
    public async Task RankAModAllows_IsNotLoweredByFixAll()
    {
        var (repository, data) = BigAtRankFive();
        var vm = CreateViewModel(repository, new FakeDialogService(confirmResult: true), gameNames: new SaveGameNames(data),
            readModSources: () => ModIndex(canTellInstalled: true, BigGameRows(), new Dictionary<string, int> { ["Big"] = 5 }));
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            await vm.WaitForModSourcesAsync();

            Assert.DoesNotContain(vm.CheckFindings, f => f.FixLabel?.Contains("to rank", StringComparison.Ordinal) == true);
            vm.FixAllFindingsCommand.Execute(null);
            Assert.Equal(5, vm.Characters.Single().Talents.Single(t => t.RowName == "Big").Rank);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>Above what the installed mods allow it's still a problem with a fix; when they couldn't be read it's only a note.</summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task RankAboveWhatAnythingAllows_IsAProblemOnlyWhenTheInstalledModsWereRead(bool canTellInstalled, bool expectFix)
    {
        var (repository, data) = BigAtRankFive();
        var vm = CreateViewModel(repository, gameNames: new SaveGameNames(data),
            readModSources: () => ModIndex(canTellInstalled, BigGameRows(), new Dictionary<string, int> { ["Big"] = 3 }));
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            await vm.WaitForModSourcesAsync();

            var finding = Assert.Single(vm.CheckFindings, f => f.Message.Contains("rank 5", StringComparison.Ordinal));
            Assert.Equal(expectFix, finding.CanFix);
            Assert.Equal(expectFix ? SaveCheckSeverity.Problem : SaveCheckSeverity.Note, finding.Severity);
            if (expectFix)
            {
                Assert.EndsWith("to rank 3", finding.FixLabel);
            }
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    private static FakeSaveRepository CreditsTwice()
    {
        var profile = MakeProfile(nextChrSlot: 0);
        profile["MetaResources"] = new JsonArray(
            new JsonObject { ["MetaRow"] = "Credits", ["Count"] = 10 },
            new JsonObject { ["MetaRow"] = "Credits", ["Count"] = 50 });
        return new FakeSaveRepository { Profile = profile };
    }

    /// <summary>Regression: merging repeated currencies at Save raised the edited copy back to the highest count, and left a row bound to the removed copy.</summary>
    [Fact]
    public async Task MergedCurrencies_KeepTheEdit_AndLeaveOneRow()
    {
        var repository = CreditsTwice();
        var vm = CreateViewModel(repository);
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            vm.Currencies[0].CountText = "0";
            vm.FixFindingCommand.Execute(Assert.Single(vm.CheckFindings, f => f.Area == "Account" && f.Message.Contains("listed more than once")));

            vm.SaveChangesCommand.Execute(null);

            Assert.Equal(["Credits:0"], ((JsonArray)repository.Profile["MetaResources"]!).Select(r => $"{r!["MetaRow"]}:{r["Count"]}"));
            var row = Assert.Single(vm.Currencies, c => c.MetaRow == "Credits");
            Assert.Equal("0", row.CountText);
            Assert.False(vm.HasUnsavedChanges);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>Regression: after a merge-only save the page kept showing the first copy's 10 while the file held 50.</summary>
    [Fact]
    public async Task MergedCurrencies_TheRowLeftShowsWhatTheFileKept()
    {
        var repository = CreditsTwice();
        var vm = CreateViewModel(repository);
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            vm.FixFindingCommand.Execute(Assert.Single(vm.CheckFindings, f => f.Area == "Account" && f.Message.Contains("listed more than once")));

            vm.SaveChangesCommand.Execute(null);

            Assert.Equal(["Credits:50"], ((JsonArray)repository.Profile["MetaResources"]!).Select(r => $"{r!["MetaRow"]}:{r["Count"]}"));
            Assert.Equal("50", Assert.Single(vm.Currencies).CountText);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>Regression: a talent or creature listed twice showed the last copy's value, while merging the repeats keeps the highest.</summary>
    [Fact]
    public async Task RepeatedTalentAndCreature_ShowTheHighestValue()
    {
        var character = MakeCharacter("Alpha", 0);
        character["Talents"] = new JsonArray(
            new JsonObject { ["RowName"] = "Talent_A", ["Rank"] = 3 },
            new JsonObject { ["RowName"] = "Talent_A", ["Rank"] = 1 });
        var repository = new FakeSaveRepository
        {
            Characters = [character],
            Profile = MakeProfile(nextChrSlot: 1),
            Bestiary = new JsonObject
            {
                ["BestiaryTracking"] = new JsonArray(
                    new JsonObject { ["BestiaryGroup"] = new JsonObject { ["RowName"] = "RockDog" }, ["NumPoints"] = 40 },
                    new JsonObject { ["BestiaryGroup"] = new JsonObject { ["RowName"] = "RockDog" }, ["NumPoints"] = 10 }),
                ["FishTracking"] = new JsonArray(),
            },
        };
        var vm = CreateViewModel(repository);
        try
        {
            await vm.WaitForPendingSlotLoadAsync();

            Assert.Equal(3, vm.Characters.Single().Talents.Single(t => t.RowName == "Talent_A").Rank);
            Assert.Equal(40, vm.BestiaryEntries.Single(b => b.RowName == "RockDog").EffectivePoints);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>
    /// Regression: when the last file of a Save failed, the files already written stayed written
    /// (a half-saved slot) and their sections were marked saved, so a retry skipped them.
    /// </summary>
    [Fact]
    public async Task SaveFailingPartway_PutsBackWhatItWrote_AndTheRetryWritesEverything()
    {
        var profile = MakeProfile(nextChrSlot: 0);
        profile["MetaResources"] = new JsonArray(
            new JsonObject { ["MetaRow"] = "Credits", ["Count"] = 10 },
            new JsonObject { ["MetaRow"] = "Exotic_stabilized", ["Count"] = 5 });
        var repository = new FakeSaveRepository
        {
            Profile = profile,
            Mounts = MakeMountsRoot(MakeMount("Rex", 3, "Wolf"), MakeMount("Bob", 4, "Wolf")),
            SaveMountsError = new IOException("held by Steam"),
        };
        var vm = CreateViewModel(repository);
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            vm.RemoveCurrencyCommand.Execute(vm.Currencies.Single(c => c.MetaRow == "Exotic_stabilized"));
            vm.DeleteMountCommand.Execute(vm.Mounts.Single(m => m.Name == "Bob"));

            vm.SaveChangesCommand.Execute(null);

            var putBack = Assert.Single(repository.PutBacks);
            Assert.Equal("fake_backup.zip", putBack.Backup);
            Assert.Equal(["Profile.json"], putBack.Files);
            Assert.Contains("put back", vm.StatusMessage);
            Assert.True(vm.HasUnsavedChanges);

            vm.SaveChangesCommand.Execute(null);

            Assert.Equal(2, repository.SaveProfileCallCount);
            Assert.Equal(2, repository.SaveMountsCallCount);
            Assert.Equal(["Credits"], ((JsonArray)repository.Profile["MetaResources"]!).Select(r => (string?)r!["MetaRow"]));
            Assert.Single(repository.LastSavedMounts!["SavedMounts"]!.AsArray());
            Assert.StartsWith("Saved", vm.StatusMessage);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>Regression: deleting one mount rewrote every other mount's name and level, so a level of 12.5 became 0 and a missing name became "".</summary>
    [Fact]
    public async Task DeletingOneMount_LeavesTheOtherMountsAlone()
    {
        var rex = MakeMount("Rex", 3, "Wolf");
        rex["MountLevel"] = 12.5;
        var nameless = MakeMount("x", 2, "Wolf");
        nameless.Remove("MountName");
        var repository = new FakeSaveRepository
        {
            Profile = MakeProfile(nextChrSlot: 0),
            Mounts = MakeMountsRoot(rex, MakeMount("Bob", 4, "Wolf"), nameless),
        };
        var vm = CreateViewModel(repository);
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            vm.DeleteMountCommand.Execute(vm.Mounts.Single(m => m.Name == "Bob"));

            vm.SaveChangesCommand.Execute(null);

            var saved = repository.LastSavedMounts!["SavedMounts"]!.AsArray();
            Assert.Equal(2, saved.Count);
            Assert.Equal("12.5", saved[0]!["MountLevel"]!.ToJsonString());
            Assert.False(saved[1]!.AsObject().ContainsKey("MountName"));
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>
    /// Regression: a mount's picture name from Mounts.json went straight into a file path, so a
    /// full path (or a network one, which freezes the page while Windows tries the host) was looked
    /// up wherever it pointed instead of in the slot's Mounts folder.
    /// </summary>
    [Fact]
    public async Task MountPictureNameThatIsAPath_IsNeverLookedUp()
    {
        var outside = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests_Outside", Guid.NewGuid().ToString("N"));
        _tempFoldersToCleanUp.Add(outside);
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "pic.exr"), [1]);
        var rex = MakeMount("Rex", 3, "Wolf");
        rex["MountIconName"] = Path.Combine(outside, "pic");
        var bob = MakeMount("Bob", 4, "Wolf");
        bob["MountIconName"] = @"..\..\pic";
        var repository = new FakeSaveRepository { Profile = MakeProfile(nextChrSlot: 0), Mounts = MakeMountsRoot(rex, bob) };
        var vm = CreateViewModel(repository);
        try
        {
            await vm.WaitForPendingSlotLoadAsync();

            Assert.Contains(vm.CheckFindings, f => f.Area == "Mounts" && f.Message.StartsWith("Rex", StringComparison.Ordinal) && f.Message.Contains("isn't a plain file name"));
            Assert.Contains(vm.CheckFindings, f => f.Area == "Mounts" && f.Message.StartsWith("Bob", StringComparison.Ordinal) && f.Message.Contains("isn't a plain file name"));
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>
    /// Regression: PlayerData was listed once, when the page was made. A folder that couldn't be
    /// read then (or a first game run after Starlink opened) stayed "no saves" until restart, and
    /// neither showing the page again nor Reload looked again.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlayerDataUnreadableAtFirst_IsListedOnceItCanBeRead(bool throughReload)
    {
        var repository = new FakeSaveRepository
        {
            ListSlotsError = new IOException("not yet"),
            Profile = MakeProfile(nextChrSlot: 1),
            Characters = [MakeCharacter("Alpha", 0)],
        };
        var vm = CreateViewModel(repository);
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            Assert.Empty(vm.Slots);

            repository.ListSlotsError = null;
            if (throughReload)
            {
                vm.ReloadCommand.Execute(null);
            }
            else
            {
                vm.OnPageShown();
            }

            await vm.WaitForPendingSlotLoadAsync();

            Assert.Single(vm.Slots);
            Assert.NotNull(vm.SelectedSlot);
            Assert.Single(vm.Characters);
            Assert.DoesNotContain("Couldn't read the game's PlayerData folder", vm.StatusMessage ?? "");
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    [Fact]
    public async Task SlotAddedLater_IsListed_AndTheSelectionStays()
    {
        var first = new SaveSlot(SteamId, $@"C:\fake\PlayerData\{SteamId}", "Tester");
        var repository = new FakeSaveRepository { Profile = MakeProfile(nextChrSlot: 0), SlotList = [first] };
        var dialogs = new FakeDialogService(confirmResult: true);
        var vm = CreateViewModel(repository, dialogs);
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            var confirmsBefore = dialogs.ConfirmCallCount;
            var earlier = new SaveSlot("76561198000000000", @"C:\fake\PlayerData\76561198000000000", "Other");
            repository.SlotList = [earlier, first];

            vm.OnPageShown();
            await vm.WaitForPendingSlotLoadAsync();

            Assert.Equal([earlier, first], vm.Slots);
            Assert.Equal(first, vm.SelectedSlot);
            Assert.Equal(confirmsBefore, dialogs.ConfirmCallCount);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>
    /// Regression: the save check's "Use X for both" only took effect at Save, so its warning stayed
    /// (and stayed in Fix all) after the click; and two mounts with the same name shared one row
    /// whose fix reached only the first of them.
    /// </summary>
    [Fact]
    public async Task MountNameFix_ClearsItsFinding_AndReachesEachMount()
    {
        static JsonObject Buddy(string blobName) => new()
        {
            ["MountName"] = "Buddy",
            ["MountLevel"] = 3,
            ["MountType"] = "Wolf",
            ["RecorderBlob"] = new JsonObject { ["BinaryData"] = MountBlobFixture.Blob(blobName, 10) },
        };

        var repository = new FakeSaveRepository { Profile = MakeProfile(nextChrSlot: 0), Mounts = MakeMountsRoot(Buddy("Old1"), Buddy("Old2")) };
        var vm = CreateViewModel(repository);
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            const string fix = "Use \"Buddy\" for both";
            Assert.Equal(2, vm.CheckFindings.Count(f => f.FixLabel == fix));

            vm.FixAllFindingsCommand.Execute(null);
            Assert.DoesNotContain(vm.CheckFindings, f => f.FixLabel == fix);

            vm.SaveChangesCommand.Execute(null);
            var saved = repository.LastSavedMounts!["SavedMounts"]!.AsArray();
            Assert.Equal(2, saved.Count);
            foreach (var mount in saved)
            {
                var bytes = mount!["RecorderBlob"]!["BinaryData"]!.AsArray().Select(b => (byte)b!.GetValue<int>()).ToArray();
                Assert.Equal("Buddy", MountBlob.TryRead(bytes, out var why)?.Name ?? why);
            }
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>Regression: each search keystroke cleared the mount list, the list dropped its selection, and the mount pane closed.</summary>
    [Fact]
    public async Task MountSearch_KeepsTheSelectedMount()
    {
        var repository = new FakeSaveRepository { Profile = MakeProfile(nextChrSlot: 0), Mounts = MakeMountsRoot(MakeMount("Rex", 3, "Wolf"), MakeMount("Bob", 4, "Wolf")) };
        var vm = CreateViewModel(repository);
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            var rex = vm.Mounts.Single(m => m.Name == "Rex");
            // What the ListBox does: a cleared source drops its selection.
            vm.FilteredMounts.CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Reset)
                {
                    vm.SelectedMount = null;
                }
            };
            vm.SelectedMount = rex;

            vm.MountSearchText = "R";

            Assert.Same(rex, vm.SelectedMount);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    [Fact]
    public async Task DeletingTheSelectedMount_SelectsAnother()
    {
        var repository = new FakeSaveRepository { Profile = MakeProfile(nextChrSlot: 0), Mounts = MakeMountsRoot(MakeMount("Rex", 3, "Wolf"), MakeMount("Bob", 4, "Wolf")) };
        var vm = CreateViewModel(repository);
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            var rex = vm.Mounts.Single(m => m.Name == "Rex");
            vm.SelectedMount = rex;
            vm.FilteredMounts.CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems!.Contains(vm.SelectedMount))
                {
                    vm.SelectedMount = null;
                }
            };

            vm.DeleteMountCommand.Execute(rex);

            Assert.Same(vm.Mounts.Single(m => m.Name == "Bob"), vm.SelectedMount);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>Regression: the list dropped the deleted character's selection before the "select the first remaining" step looked, so none stayed selected.</summary>
    [Fact]
    public async Task DeletingTheSelectedCharacter_SelectsAnother()
    {
        var repository = new FakeSaveRepository { Profile = MakeProfile(nextChrSlot: 2), Characters = [MakeCharacter("Alpha", 0), MakeCharacter("Beta", 1)] };
        var vm = CreateViewModel(repository);
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            var alpha = vm.Characters.Single(c => c.Name == "Alpha");
            vm.SelectedCharacter = alpha;
            vm.Characters.CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems!.Contains(vm.SelectedCharacter))
                {
                    vm.SelectedCharacter = null;
                }
            };

            vm.DeleteCharacterCommand.Execute(alpha);

            Assert.Same(vm.Characters.Single(c => c.Name == "Beta"), vm.SelectedCharacter);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>
    /// Regression: typing another level and then the character's own level back set the XP to that
    /// level's start (4,034,000), so Save lost the character's progress into the level; and the
    /// Level box's error stayed after the XP box was put right.
    /// </summary>
    [Fact]
    public async Task LevelSetBack_PutsTheCharactersOwnXpBack()
    {
        static IcarusStarlink.PakIO.Assets.GrowthCurveKey Key(float t, float v) => new(t, v, false, false, 0, 0);
        var growth = new IcarusStarlink.PakIO.Assets.PlayerGrowth(
            1000, 60, false,
            new([Key(0, 0), Key(51, 4_034_000), Key(52, 4_200_000)], false, true),
            new([Key(0, 0), Key(50, 75), Key(60, 90)], false, false),
            null, null, []);
        var character = MakeCharacter("Alpha", 0);
        character["XP"] = 4_100_000L;
        var repository = new FakeSaveRepository { Characters = [character], Profile = MakeProfile(nextChrSlot: 1) };
        var vm = new SavesViewModel(repository, new FakeActivityLog(),
            new SaveGameNames(Path.Combine(Path.GetTempPath(), $"IcarusStarlinkTests_NoGameData_{Guid.NewGuid():N}")),
            new FakeDialogService(confirmResult: true), new FakeGameProcessChecker(false), new FakeBaseGameIconDecoder(),
            readPlayerGrowth: () => growth);
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            await vm.WaitForPlayerGrowthAsync();
            var alpha = Assert.Single(vm.Characters);
            vm.SelectedCharacter = alpha;
            Assert.Equal("51", vm.SelectedLevelText);

            vm.SelectedLevelText = "60";
            Assert.NotEqual("4100000", alpha.XpText);
            vm.SelectedLevelText = "51";

            Assert.Equal("4100000", alpha.XpText);
            Assert.False(alpha.IsDirty);

            vm.SelectedLevelText = "ten";
            Assert.NotNull(vm.LevelError);
            Assert.True(vm.HasLevelError);
            alpha.XpText = "4200000";

            Assert.Null(vm.LevelError);
            Assert.False(vm.HasLevelError);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>
    /// Regression: a load that a newer one had already overtaken still ran its failure path when its
    /// own read failed, marking the newer, good load as failed: Save said "Nothing changed." over
    /// real edits and switching slot never asked about them.
    /// </summary>
    [Fact]
    public async Task OvertakenLoadThatFails_DoesNotSpoilTheNewerLoad()
    {
        var repository = new FakeSaveRepository { Characters = [MakeCharacter("Alpha", 0)], Profile = MakeProfile(nextChrSlot: 1) };
        var vm = CreateViewModel(repository);
        using var enteredFirst = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var releaseSecond = new ManualResetEventSlim();
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            repository.ProfileLoadHook = call =>
            {
                if (call == 1)
                {
                    enteredFirst.Set();
                    releaseFirst.Wait(TimeSpan.FromSeconds(30));
                    throw new IOException("held by Steam");
                }

                releaseSecond.Wait(TimeSpan.FromSeconds(30));
                return repository.Profile;
            };

            vm.ReloadCommand.Execute(null);
            var first = vm.WaitForPendingSlotLoadAsync();
            Assert.True(enteredFirst.Wait(TimeSpan.FromSeconds(10)));
            vm.ReloadCommand.Execute(null);
            var second = vm.WaitForPendingSlotLoadAsync();
            releaseSecond.Set();
            await second;
            releaseFirst.Set();
            await first;

            vm.Characters.Single().Name = "Edited";

            Assert.True(vm.HasUnsavedChanges);
            Assert.DoesNotContain("Couldn't read this save", vm.StatusMessage ?? "");
        }
        finally
        {
            releaseFirst.Set();
            releaseSecond.Set();
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    /// <summary>
    /// Regression: with every multiplier back at 1x, Make the mod took "Starlink Player Points" out
    /// of the Library without asking, even when it was turned on in Merge &amp; Install.
    /// </summary>
    [Fact]
    public async Task PointsAllBackToTheGames_AsksBeforeTakingTheModOutOfTheLibrary()
    {
        var root = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests_Points", Guid.NewGuid().ToString("N"));
        _tempFoldersToCleanUp.Add(root);
        using var library = new IcarusStarlink.Storage.Library.FolderLibraryRepository(
            Path.Combine(root, "Extracted_Mods"), Path.Combine(root, "Library_Meta"), Path.Combine(root, "Backups"),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IcarusStarlink.Storage.Library.FolderLibraryRepository>.Instance);
        library.CreateBlankMod(PlayerPointsMod.DisplayName, "Icarus Starlink");
        static IcarusStarlink.PakIO.Assets.GrowthCurveKey Key(float t, float v) => new(t, v, false, false, 0, 0);
        var talents = new IcarusStarlink.PakIO.Assets.GrowthCurve([Key(0, 0), Key(50, 75)], false, false);
        var growth = new IcarusStarlink.PakIO.Assets.PlayerGrowth(
            1000, 60, false, null, talents, null, null, [],
            GameCurves: new Dictionary<string, IcarusStarlink.PakIO.Assets.GrowthCurve> { ["TalentPointsPerLevel"] = talents });
        var dialog = new FakeDialogService(false, false, true);
        var repository = new FakeSaveRepository { Characters = [MakeCharacter("Alpha", 0)], Profile = MakeProfile(nextChrSlot: 1) };
        var vm = new SavesViewModel(repository, new FakeActivityLog(),
            new SaveGameNames(Path.Combine(Path.GetTempPath(), $"IcarusStarlinkTests_NoGameData_{Guid.NewGuid():N}")),
            dialog, new FakeGameProcessChecker(false), new FakeBaseGameIconDecoder(),
            readPlayerGrowth: () => growth, pointsMod: new PlayerPointsMod(library, new NoGameContent()));
        async Task MakeTheMod()
        {
            vm.MakePointsModCommand.Execute(null);
            if (vm.MakePointsModCommand is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand { ExecutionTask: { } running })
            {
                await running;
            }
        }

        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            await vm.WaitForPlayerGrowthAsync();
            Assert.Equal(1, Assert.Single(vm.PointsEditors).Multiplier);

            await MakeTheMod();

            Assert.Equal(1, dialog.ConfirmCallCount);
            Assert.Contains("Merge & Install", dialog.LastConfirmMessage);
            Assert.Contains(library.GetAll(), e => e.FileName == PlayerPointsMod.FileName);
            Assert.NotNull(vm.PointsModFolder);

            await MakeTheMod();

            Assert.Equal(2, dialog.ConfirmCallCount);
            Assert.DoesNotContain(library.GetAll(), e => e.FileName == PlayerPointsMod.FileName);
            Assert.Null(vm.PointsModFolder);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }

    private sealed class NoGameContent : IcarusStarlink.PakIO.Assets.IBaseGameContentProvider
    {
        public T? TryLoadExport<T>(string assetPath) where T : class => null;

        public TResult? TryLoadExportAndProject<T, TResult>(string assetPath, Func<T, TResult> project) where T : class where TResult : class => null;
    }

    /// <summary>The Unlocks tab's Advanced section mixes account flags with one character's own: it now names that character.</summary>
    [Fact]
    public async Task AdvancedFlagsNote_NamesTheCharacterWhoseFlagsItShows()
    {
        var repository = new FakeSaveRepository { Characters = [MakeCharacter("Alpha", 0), MakeCharacter("Beta", 1)], Profile = MakeProfile(nextChrSlot: 2) };
        var vm = CreateViewModel(repository);
        try
        {
            await vm.WaitForPendingSlotLoadAsync();
            var alpha = vm.Characters.Single(c => c.Name == "Alpha");
            vm.SelectedCharacter = vm.Characters.Single(c => c.Name == "Beta");
            Assert.Contains("Beta's own flags", vm.AdvancedFlagsNote);
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.SelectedCharacter = alpha;
            Assert.Contains("Alpha's own flags", vm.AdvancedFlagsNote);
            Assert.Contains(nameof(SavesViewModel.AdvancedFlagsNote), raised);

            raised.Clear();
            alpha.Name = "Ann";
            Assert.Contains("Ann's own flags", vm.AdvancedFlagsNote);
            Assert.Contains(nameof(SavesViewModel.AdvancedFlagsNote), raised);

            vm.SelectedCharacter = null;
            Assert.DoesNotContain("own flags", vm.AdvancedFlagsNote);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }
}
