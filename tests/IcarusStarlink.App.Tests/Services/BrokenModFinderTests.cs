using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>
/// Find the broken mod: the search (BrokenModSearch) against a simulated player who answers by a
/// hidden rule ("broken while these mods are all in"), the clues (suspects, what a mod needs) on a
/// small extracted game, and the session's promise that the player's mods always go back in.
/// </summary>
public sealed class BrokenModFinderTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best-effort cleanup only.
        }
    }

    private static List<string> Mods(int count) => [.. Enumerable.Range(1, count).Select(i => $"Mod{i:00}")];

    private static readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> NoNeeds = new Dictionary<string, IReadOnlyCollection<string>>();

    /// <summary>Runs a whole search: the player says "Still broken" exactly while every culprit is in. Returns every set installed.</summary>
    private static List<IReadOnlyList<string>> Play(BrokenModSearchState state, IReadOnlyCollection<string> culprits, Func<int, BrokenModAnswer?>? overrideAnswer = null)
    {
        var installs = new List<IReadOnlyList<string>>();
        for (var round = 0; round < 200; round++)
        {
            if (BrokenModSearch.NextTest(state) is not { } test)
            {
                return installs;
            }

            installs.Add(test.Install);
            var broken = culprits.All(c => test.Install.Contains(c, StringComparer.OrdinalIgnoreCase));
            var answer = overrideAnswer?.Invoke(round) ?? (broken ? BrokenModAnswer.StillBroken : BrokenModAnswer.WorksNow);
            BrokenModSearch.Answer(state, answer);
        }

        throw new InvalidOperationException("The search never ended.");
    }

    // ---- The search ----

    [Fact]
    public void Search_OneBrokenModAmongForty_IsFoundInAboutLogTwoRounds()
    {
        var mods = Mods(40);
        foreach (var culprit in new[] { "Mod01", "Mod17", "Mod40" })
        {
            var state = BrokenModSearch.Start(mods, NoNeeds, []);

            var installs = Play(state, [culprit]);

            Assert.Equal(BrokenModSearchEndKind.Found, state.End);
            Assert.Equal([culprit], state.Culprits);
            // 41 outcomes (any of 40, or none): 6 rounds, and one more to see it breaks alone.
            Assert.True(installs.Count <= 7, $"{culprit}: {installs.Count} rounds");
            // The last round proves it: the mod alone (the first mod's proof is a round with no mods, which works).
            Assert.Equal(culprit == "Mod01" ? [] : [culprit], installs[^1]);
        }
    }

    [Fact]
    public void Search_FirstRound_InstallsHalfTheMods()
    {
        var state = BrokenModSearch.Start(Mods(40), NoNeeds, []);

        var test = BrokenModSearch.NextTest(state)!;

        Assert.Equal(20, test.Install.Count);
        Assert.False(test.IsSuspectRound);
    }

    [Fact]
    public void Search_SameModsAndAnswers_GiveTheSameRounds()
    {
        var first = Play(BrokenModSearch.Start(Mods(23), NoNeeds, ["Mod05"]), ["Mod11"]);
        var second = Play(BrokenModSearch.Start(Mods(23), NoNeeds, ["Mod05"]), ["Mod11"]);

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i], second[i]);
        }
    }

    [Fact]
    public void Search_AskedAgainBeforeAnAnswer_GivesTheSameRound()
    {
        var state = BrokenModSearch.Start(Mods(10), NoNeeds, []);

        var first = BrokenModSearch.NextTest(state)!;
        var again = BrokenModSearch.NextTest(state)!;

        Assert.Equal(first.Install, again.Install);
        Assert.Equal(0, state.Round);
    }

    [Fact]
    public void Search_TwoModsThatOnlyBreakTogether_NamesBoth()
    {
        var mods = Mods(30);
        var state = BrokenModSearch.Start(mods, NoNeeds, []);

        Play(state, ["Mod04", "Mod26"]);

        Assert.Equal(BrokenModSearchEndKind.Found, state.End);
        Assert.Equal(["Mod04", "Mod26"], state.Culprits.Order().ToList());
    }

    [Fact]
    public void Search_EveryPairOfTwelve_IsNamedExactly()
    {
        var mods = Mods(12);
        foreach (var a in mods)
        {
            foreach (var b in mods.Where(m => string.CompareOrdinal(m, a) > 0))
            {
                var state = BrokenModSearch.Start(mods, NoNeeds, [b]);
                Play(state, [a, b]);

                Assert.Equal(BrokenModSearchEndKind.Found, state.End);
                Assert.Equal([a, b], state.Culprits.Order().ToList());
            }
        }
    }

    [Fact]
    public void Search_CulpritThatNeedsAnotherMod_NeverGoesInWithoutIt()
    {
        // Mod09 links to rows only Mod02 adds.
        var needs = new Dictionary<string, IReadOnlyCollection<string>> { ["Mod09"] = ["Mod02"] };
        var state = BrokenModSearch.Start(Mods(16), needs, []);

        var installs = Play(state, ["Mod09"]);

        Assert.Equal(["Mod09"], state.Culprits);
        Assert.All(installs.Where(i => i.Contains("Mod09")), i => Assert.Contains("Mod02", i));
    }

    [Fact]
    public void Search_BrokenOnlyWithAModAndWhatItNeeds_NamesTheMod_AndItsNeedsStayIn()
    {
        // Mod12 needs Mod03; it breaks only with both in. Mod03 can never be tested without Mod12's
        // need being met, so the answer is Mod12 (the window adds "it needs Mod03").
        var needs = new Dictionary<string, IReadOnlyCollection<string>> { ["Mod12"] = ["Mod03"] };
        var state = BrokenModSearch.Start(Mods(20), needs, []);

        Play(state, ["Mod12", "Mod03"]);

        Assert.Equal(BrokenModSearchEndKind.Found, state.End);
        Assert.Contains("Mod12", state.Culprits);
        Assert.True(state.Culprits.Count <= 2);
    }

    [Fact]
    public void Search_EveryInstalledSet_HasWhatEachOfItsModsNeeds()
    {
        var needs = new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["Mod05"] = ["Mod18"],
            ["Mod18"] = ["Mod07"],
            ["Mod10"] = ["Mod01", "Mod20"],
        };
        foreach (var culprit in Mods(20))
        {
            var state = BrokenModSearch.Start(Mods(20), needs, ["Mod18"]);
            var installs = Play(state, [culprit]);

            Assert.Equal(BrokenModSearchEndKind.Found, state.End);
            Assert.Contains(culprit, state.Culprits);
            foreach (var install in installs)
            {
                foreach (var (mod, needed) in needs)
                {
                    if (install.Contains(mod))
                    {
                        Assert.All(needed, n => Assert.Contains(n, install));
                    }
                }
            }
        }
    }

    [Fact]
    public void Search_WithSuspects_FirstRoundIsEveryModButThem()
    {
        var mods = Mods(12);
        var state = BrokenModSearch.Start(mods, NoNeeds, ["Mod03", "Mod08"]);

        var test = BrokenModSearch.NextTest(state)!;

        Assert.True(test.IsSuspectRound);
        Assert.Equal(10, test.Install.Count);
        Assert.DoesNotContain("Mod03", test.Install);
        Assert.DoesNotContain("Mod08", test.Install);
    }

    [Fact]
    public void Search_SuspectIsTheCulprit_IsFoundInFewRounds()
    {
        var state = BrokenModSearch.Start(Mods(40), NoNeeds, ["Mod21", "Mod33"]);

        var installs = Play(state, ["Mod33"]);

        Assert.Equal(["Mod33"], state.Culprits);
        // Everything but the suspects (works), then one of the two, then done.
        Assert.True(installs.Count <= 3, $"{installs.Count} rounds");
    }

    [Fact]
    public void Search_SuspectsWrong_StillFindsTheCulprit()
    {
        var state = BrokenModSearch.Start(Mods(40), NoNeeds, ["Mod21", "Mod33"]);

        Play(state, ["Mod07"]);

        Assert.Equal(["Mod07"], state.Culprits);
    }

    [Fact]
    public void Order_ASuspectANonSuspectNeeds_ComesBeforeIt()
    {
        var needs = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { ["Mod01"] = ["Mod02"] };

        var order = BrokenModSearch.Order(["Mod01", "Mod02", "Mod03"], needs, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Mod02", "Mod03" });

        Assert.True(order.IndexOf("Mod02") < order.IndexOf("Mod01"));
        Assert.Equal("Mod03", order[^1]);
    }

    [Fact]
    public void Search_BrokenWithNoModsAtAll_SaysItIsNotADataMod()
    {
        var state = BrokenModSearch.Start(Mods(8), NoNeeds, []);

        var installs = Play(state, []);

        Assert.Equal(BrokenModSearchEndKind.NotADataMod, state.End);
        Assert.Empty(installs[^1]);
    }

    [Fact]
    public void Search_OneCantTell_StillFindsTheCulprit()
    {
        var state = BrokenModSearch.Start(Mods(16), NoNeeds, []);

        Play(state, ["Mod06"], round => round == 1 ? BrokenModAnswer.CantTell : null);

        Assert.Equal(["Mod06"], state.Culprits);
    }

    [Fact]
    public void Search_OnlyCantTell_EndsUnsureWithTheModsItCouldBe()
    {
        var state = BrokenModSearch.Start(Mods(4), NoNeeds, []);

        Play(state, ["Mod02"], _ => BrokenModAnswer.CantTell);

        Assert.Equal(BrokenModSearchEndKind.Unsure, state.End);
        Assert.Equal(Mods(4), state.Unsure);
    }

    [Fact]
    public void Search_SavedAndReadBackMidway_EndsTheSame()
    {
        var store = new BrokenModSearchStore(Path.Combine(_tempRoot, "search.json"));
        var needs = new Dictionary<string, IReadOnlyCollection<string>> { ["Mod09"] = ["Mod02"] };
        var record = new BrokenModSearchRecord { Search = BrokenModSearch.Start(Mods(20), needs, ["Mod09"]) };
        // Two rounds, then the app closes.
        for (var i = 0; i < 2; i++)
        {
            var test = BrokenModSearch.NextTest(record.Search)!;
            BrokenModSearch.Answer(record.Search, test.Install.Contains("Mod14") ? BrokenModAnswer.StillBroken : BrokenModAnswer.WorksNow);
        }

        BrokenModSearch.NextTest(record.Search);
        store.Save(record);

        var resumed = store.Load()!;
        Play(resumed.Search, ["Mod14"]);

        Assert.Equal(["Mod14"], resumed.Search.Culprits);
        Assert.Equal(["Mod02"], resumed.Search.Needs["mod09"]);
    }

    // ---- The clues ----

    /// <summary>A small extracted game: items that link to drinks, a recipe table.</summary>
    private GameTableIndex WriteGameData()
    {
        var data = Path.Combine(_tempRoot, "GameData");
        void Table(string relative, string defaults, string rows)
        {
            var path = Path.Combine(data, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $$$"""{"RowStruct": "S", "Defaults": {{{defaults}}}, "Rows": [{{{rows}}}]}""");
        }

        Table(Path.Combine("Items", "D_ItemsStatic.json"), """{"Weight": 0, "Consumable": {"RowName": "None", "DataTableName": "D_Consumable"}}""",
            """{"Name": "Bread", "Weight": 20}, {"Name": "Canteen", "Weight": 5, "Consumable": {"RowName": "Canteen_Water", "DataTableName": "D_Consumable"}}""");
        Table(Path.Combine("Traits", "D_Consumable.json"), """{"Stat": 0}""", """{"Name": "Canteen_Water", "Stat": 1}, {"Name": "Bread_Food", "Stat": 2}""");
        Table(Path.Combine("Crafting", "D_ProcessorRecipes.json"), """{"Output": []}""", """{"Name": "Canteen_Recipe", "Output": [{"Element": {"RowName": "Canteen"}}]}""");
        return new GameTableIndex(data);
    }

    private static BrokenModCandidate Mod(string folder, string rowsJson) => new(folder, folder, ExmodJson.Parse($$"""
        {"name": "{{folder}}", "author": "T", "version": "1", "description": "D", "fileName": "{{folder}}", "Rows": [{{rowsJson}}]}
        """));

    [Fact]
    public void Suspects_SayWhyEachModIsSuspected_StrongestFirst()
    {
        var index = WriteGameData();
        var mods = new[]
        {
            Mod("ChangesCanteen", """{"CurrentFile": "Items-D_ItemsStatic.json", "File_Items": [{"Name": "Canteen", "Weight": 1}]}"""),
            Mod("ChangesRecipe", """{"CurrentFile": "Crafting-D_ProcessorRecipes.json", "File_Items": [{"Name": "Canteen_Recipe", "Output": [{"Element": {"RowName": "Canteen"}}, {"Element": {"RowName": "Bread"}}]}]}"""),
            Mod("ChangesTheDrink", """{"CurrentFile": "Traits-D_Consumable.json", "File_Items": [{"Name": "Canteen_Water", "Stat": 0}]}"""),
            Mod("ChangesBread", """{"CurrentFile": "Items-D_ItemsStatic.json", "File_Items": [{"Name": "Bread", "Weight": 1}]}"""),
            Mod("Unrelated", """{"CurrentFile": "Traits-D_Consumable.json", "File_Items": [{"Name": "Bread_Food", "Stat": 9}]}"""),
        };

        var suspects = BrokenModClues.Suspects(mods, [new BrokenModTarget("Items-D_ItemsStatic.json", "Canteen")], index);

        Assert.Equal("ChangesCanteen", suspects[0].FolderName);
        Assert.Equal("changes Canteen (D_ItemsStatic)", suspects[0].Reason);
        Assert.Equal("changes a recipe (Canteen_Recipe) that uses Canteen", suspects.Single(s => s.FolderName == "ChangesRecipe").Reason);
        Assert.Equal("changes Canteen_Water (D_Consumable), which Canteen uses", suspects.Single(s => s.FolderName == "ChangesTheDrink").Reason);
        Assert.Equal(1, suspects.Single(s => s.FolderName == "ChangesBread").Strength);
        Assert.DoesNotContain(suspects, s => s.FolderName == "Unrelated");

        var first = BrokenModClues.FirstGroup(suspects, mods.Length);
        Assert.Equal(["ChangesCanteen", "ChangesRecipe", "ChangesTheDrink"], first.Select(s => s.FolderName).Order().ToList());
    }

    [Fact]
    public void Suspects_FromTypedWords_MatchRowNames()
    {
        var mods = new[] { Mod("ChangesCanteen", """{"CurrentFile": "Items-D_ItemsStatic.json", "File_Items": [{"Name": "Canteen", "Weight": 1}]}""") };

        var targets = BrokenModClues.TargetsFromWords("canteen", [("Items-D_ItemsStatic.json", "Canteen"), ("Items-D_ItemsStatic.json", "Bread")], mods);

        Assert.Equal([new BrokenModTarget("Items-D_ItemsStatic.json", "Canteen")], targets);
    }

    [Fact]
    public void FirstGroup_EveryModASuspect_IsEmpty()
    {
        var suspects = new[] { new BrokenModSuspect("A", "A", "changes X", 3), new BrokenModSuspect("B", "B", "changes X", 3) };

        Assert.Empty(BrokenModClues.FirstGroup(suspects, 2));
    }

    [Fact]
    public void Needs_AModLinkingToARowOnlyAnotherModAdds_NeedsThatMod()
    {
        var index = WriteGameData();
        var mods = new[]
        {
            Mod("AddsDrink", """{"CurrentFile": "Traits-D_Consumable.json", "File_Items": [{"Name": "Mod_SuperWater", "Stat": 5}]}"""),
            Mod("UsesDrink", """{"CurrentFile": "Items-D_ItemsStatic.json", "File_Items": [{"Name": "Canteen", "Consumable": {"RowName": "Mod_SuperWater", "DataTableName": "D_Consumable"}}]}"""),
            Mod("Alone", """{"CurrentFile": "Items-D_ItemsStatic.json", "File_Items": [{"Name": "Bread", "Weight": 3}]}"""),
        };

        var needs = BrokenModClues.Needs(mods, index);

        Assert.Equal(["AddsDrink"], needs["UsesDrink"]);
        Assert.False(needs.ContainsKey("Alone"));
        Assert.False(needs.ContainsKey("AddsDrink"));
    }

    // ---- The session: the mods always go back ----

    private sealed class FakeHost : IBrokenModFinderHost
    {
        public bool GameRunning { get; set; }

        public List<string> Queue { get; set; } = [];

        public List<IReadOnlyCollection<string>> Rounds { get; } = [];

        public List<IReadOnlyCollection<string>> PutBacks { get; } = [];

        public string? FailNextInstallWith { get; set; }

        /// <summary>Runs as an install starts: a test checks the record is already on disk here.</summary>
        public Action? OnInstall { get; set; }

        public bool IsGameRunning() => GameRunning;

        public IReadOnlyList<string> QueuedFolderNames() => Queue;

        /// <summary>Each install's backUpWhatsThere, in order (rounds and put-backs together).</summary>
        public List<bool> BackUps { get; } = [];

        public Task<string?> InstallOnlyAsync(IReadOnlyCollection<string> folderNames, bool backUpWhatsThere)
        {
            OnInstall?.Invoke();
            BackUps.Add(backUpWhatsThere);
            Rounds.Add(folderNames);
            return Task.FromResult(TakeFailure());
        }

        public Task<string?> InstallWholeQueueAsync(IReadOnlyCollection<string> putBack, bool backUpWhatsThere)
        {
            OnInstall?.Invoke();
            BackUps.Add(backUpWhatsThere);
            if (TakeFailure() is { } failed)
            {
                return Task.FromResult<string?>(failed);
            }

            PutBacks.Add(putBack);
            Queue.AddRange(putBack.Where(f => !Queue.Contains(f)));
            return Task.FromResult<string?>(null);
        }

        public void RemoveFromQueue(string folderName) => Queue.Remove(folderName);

        public IReadOnlyList<string> BackUpSaves() => ["slot.zip"];

        private string? TakeFailure()
        {
            var failure = FailNextInstallWith;
            FailNextInstallWith = null;
            return failure;
        }
    }

    private (FakeHost Host, BrokenModSearchStore Store, BrokenModFinderSession Session) NewSession(int modCount = 8)
    {
        var mods = Mods(modCount);
        var host = new FakeHost { Queue = [.. mods] };
        var store = new BrokenModSearchStore(Path.Combine(_tempRoot, "find_broken_mod.json"));
        var record = new BrokenModSearchRecord { OriginalQueue = [.. mods], Search = BrokenModSearch.Start(mods, NoNeeds, []) };
        return (host, store, new BrokenModFinderSession(host, store, record));
    }

    [Fact]
    public async Task Session_TheSearchIsOnDiskBeforeTheFirstInstall()
    {
        var (host, store, session) = NewSession();
        BrokenModSearchRecord? onDisk = null;
        host.OnInstall = () => onDisk = store.Load();

        await session.InstallRoundAsync();

        Assert.NotNull(onDisk);
        Assert.NotNull(onDisk!.Installed);
        Assert.Equal(host.Rounds[0].Order(), onDisk.Installed!.Order());
    }

    /// <summary>
    /// Only the first round backs up the merged pak in the game (the player's real install); later
    /// rounds and putting the mods back replace round paks and don't, or after 3 rounds the real
    /// install was pruned out of Restore previous install.
    /// </summary>
    [Fact]
    public async Task Session_OnlyTheFirstRoundBacksUp_LaterRoundsAndPutBackDont()
    {
        var (host, _, session) = NewSession();
        await session.InstallRoundAsync();
        session.Answer(BrokenModAnswer.WorksNow);
        await session.InstallRoundAsync();
        session.Answer(BrokenModAnswer.StillBroken);
        await session.InstallRoundAsync();

        Assert.Null(await session.PutModsBackAsync());

        Assert.Equal([true, false, false, false], host.BackUps);
    }

    /// <summary>A round whose install failed may not have backed anything up: the next try backs up again.</summary>
    [Fact]
    public async Task Session_FirstRoundFailed_TheRetryStillBacksUp()
    {
        var (host, _, session) = NewSession();
        host.FailNextInstallWith = "UnrealPak is missing.";
        Assert.NotNull(await session.InstallRoundAsync());

        Assert.Null(await session.InstallRoundAsync());
        await session.InstallRoundAsync();

        Assert.Equal([true, true, false], host.BackUps);
    }

    /// <summary>The rule is saved: a restart mid-search doesn't back up a round pak over the real install.</summary>
    [Fact]
    public async Task Session_RestartedMidSearch_StillDoesntBackUpAgain()
    {
        var (host, store, session) = NewSession();
        await session.InstallRoundAsync();
        session.Answer(BrokenModAnswer.WorksNow);
        Assert.True(store.Load()!.PlayerInstallBackedUp);

        var next = new BrokenModFinderSession(host, store, store.Load()!);
        await next.InstallRoundAsync();
        Assert.Null(await next.PutModsBackAsync());

        Assert.Equal([true, false, false], host.BackUps);
    }

    /// <summary>No round ever reached the game (all failed), but one may have half-run: putting the mods back backs up first.</summary>
    [Fact]
    public async Task Session_NoRoundEverInstalled_PutBackBacksUp()
    {
        var (host, _, session) = NewSession();
        host.FailNextInstallWith = "The merge didn't build.";
        await session.InstallRoundAsync();

        Assert.Null(await session.PutModsBackAsync());

        Assert.Equal([true, true], host.BackUps);
    }

    [Fact]
    public async Task Session_StoppedMidSearch_PutsTheWholeOriginalQueueBack_AndForgetsTheSearch()
    {
        var (host, store, session) = NewSession();
        await session.InstallRoundAsync();
        session.Answer(BrokenModAnswer.WorksNow);
        await session.InstallRoundAsync();
        // The player unticked a mod in the Library meanwhile.
        host.Queue.Remove("Mod03");

        var failed = await session.PutModsBackAsync();

        Assert.Null(failed);
        Assert.Equal(Mods(8), host.PutBacks.Single());
        Assert.Contains("Mod03", host.Queue);
        Assert.Null(store.Load());
        Assert.True(session.IsClosed);
    }

    [Fact]
    public async Task Session_PutBackRefusedWhileTheGameRuns_KeepsTheSearchForTheNextStart()
    {
        var (host, store, session) = NewSession();
        await session.InstallRoundAsync();
        host.GameRunning = true;

        var failed = await session.PutModsBackAsync();

        Assert.Equal(BrokenModFinderSession.GameRunningMessage, failed);
        Assert.Empty(host.PutBacks);
        Assert.NotNull(store.Load());
        Assert.False(session.IsClosed);

        // The next start reads it back and puts the mods back then.
        host.GameRunning = false;
        var next = new BrokenModFinderSession(host, store, store.Load()!);
        Assert.Null(await next.PutModsBackAsync());
        Assert.Equal(Mods(8), host.PutBacks.Single());
        Assert.Null(store.Load());
    }

    [Fact]
    public async Task Session_PutBackInstallFails_KeepsTheSearch()
    {
        var (host, store, session) = NewSession();
        await session.InstallRoundAsync();
        host.FailNextInstallWith = "UnrealPak is missing.";

        var failed = await session.PutModsBackAsync();

        Assert.Equal("UnrealPak is missing.", failed);
        Assert.NotNull(store.Load());
        Assert.False(session.IsClosed);
    }

    [Fact]
    public async Task Session_RoundRefusedWhileTheGameRuns_InstallsNothing()
    {
        var (host, _, session) = NewSession();
        host.GameRunning = true;

        var failed = await session.InstallRoundAsync();

        Assert.Equal(BrokenModFinderSession.GameRunningMessage, failed);
        Assert.Empty(host.Rounds);
    }

    [Fact]
    public async Task Session_FoundModTurnedOff_StaysOutWhenTheModsGoBack()
    {
        var (host, _, session) = NewSession(4);
        while (session.CurrentTest is { } test)
        {
            await session.InstallRoundAsync();
            session.Answer(test.Install.Contains("Mod02") ? BrokenModAnswer.StillBroken : BrokenModAnswer.WorksNow);
        }

        Assert.Equal(["Mod02"], session.Record.Search.Culprits);
        session.TurnOff("Mod02");

        Assert.Null(await session.PutModsBackAsync());
        Assert.DoesNotContain("Mod02", host.PutBacks.Single());
        Assert.DoesNotContain("Mod02", host.Queue);
        Assert.Equal(3, host.Queue.Count);
    }

    [Fact]
    public async Task Session_NothingInstalledYet_PutBackChangesNothingInTheGame()
    {
        var (host, store, session) = NewSession();
        session.BackUpSaves();

        Assert.Null(await session.PutModsBackAsync());
        Assert.Empty(host.PutBacks);
        Assert.Null(store.Load());
    }

    [Fact]
    public async Task Session_QueueChangedSinceTheStart_IsNoticed()
    {
        var (host, _, session) = NewSession();
        await session.InstallRoundAsync();
        host.Queue.Remove("Mod05");
        host.Queue.Add("NewMod");

        var (added, gone) = session.QueueChangesSinceStart();

        Assert.Equal(["NewMod"], added);
        Assert.Equal(["Mod05"], gone);
    }
}
