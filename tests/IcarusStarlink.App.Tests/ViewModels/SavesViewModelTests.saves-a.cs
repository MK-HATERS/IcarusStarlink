using System.IO;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
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
}
