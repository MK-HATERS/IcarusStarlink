using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.App.Views;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>The finder window's flow end to end against a fake host: start, rounds, the answer, and the mods going back (or kept for the next start).</summary>
public sealed class FindBrokenModViewModelTests : IDisposable
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

    private sealed class FakeHost : IBrokenModFinderHost
    {
        public List<string> Queue { get; } = [];

        public List<IReadOnlyCollection<string>> Rounds { get; } = [];

        public List<IReadOnlyCollection<string>> PutBacks { get; } = [];

        public int Backups { get; private set; }

        public bool GameRunning { get; set; }

        public bool IsGameRunning() => GameRunning;

        public IReadOnlyList<string> QueuedFolderNames() => Queue;

        /// <summary>A round install fails with this (null: it works).</summary>
        public string? RoundFailure { get; set; }

        public Task<string?> InstallOnlyAsync(IReadOnlyCollection<string> folderNames, bool backUpWhatsThere)
        {
            Rounds.Add(folderNames);
            return Task.FromResult(RoundFailure);
        }

        /// <summary>Putting the mods back fails with this (null: it works).</summary>
        public string? PutBackFailure { get; set; }

        public Task<string?> InstallWholeQueueAsync(IReadOnlyCollection<string> putBack, bool backUpWhatsThere)
        {
            if (PutBackFailure is not null)
            {
                return Task.FromResult<string?>(PutBackFailure);
            }

            PutBacks.Add(putBack);
            return Task.FromResult<string?>(null);
        }

        /// <summary>Merge &amp; Install is building: taking a mod out of the queue is refused.</summary>
        public bool RefuseRemove { get; set; }

        public bool RemoveFromQueue(string folderName)
        {
            if (RefuseRemove)
            {
                return false;
            }

            Queue.Remove(folderName);
            return true;
        }

        public IReadOnlyList<string> BackUpSaves()
        {
            Backups++;
            return ["slot.zip"];
        }
    }

    private sealed class FakeDialogs : IDialogService
    {
        public List<string> Asked { get; } = [];

        public bool ConfirmAnswer { get; set; } = true;

        public bool? CloseAnswer { get; set; }

        public bool Confirm(string message, string title, ThemedConfirmSeverity severity)
        {
            Asked.Add(message);
            return ConfirmAnswer;
        }

        public bool? ConfirmWithCancel(string message, string title, ThemedConfirmSeverity severity, string yesText, string noText)
        {
            Asked.Add(message);
            return CloseAnswer;
        }

        public RenamePromptResult PromptRename(string currentName, string description = "", string? resetValue = null, string resetLabel = "",
            string resetTooltip = "", string title = "", string fieldLabel = "") => throw new NotSupportedException();
    }

    private (FindBrokenModViewModel Vm, FakeHost Host, FakeDialogs Dialogs, BrokenModSearchStore Store) Create(int mods = 8)
    {
        var host = new FakeHost();
        var candidates = new List<BrokenModCandidate>();
        for (var i = 1; i <= mods; i++)
        {
            var name = $"Mod{i}";
            host.Queue.Add(name);
            candidates.Add(new BrokenModCandidate(name, $"Nice {name}", ExmodJson.Parse($$"""
                {"name": "{{name}}", "author": "T", "version": "1", "description": "D", "fileName": "{{name}}", "Rows": []}
                """)));
        }

        var dialogs = new FakeDialogs();
        var store = new BrokenModSearchStore(Path.Combine(_tempRoot, "find_broken_mod.json"));
        var context = new FindBrokenModContext
        {
            Host = host,
            Store = store,
            Dialogs = dialogs,
            ReadCandidates = () => Task.FromResult<IReadOnlyList<BrokenModCandidate>>(candidates),
            NexusModIdOf = folder => folder == "Mod3" ? 123 : null,
        };
        return (new FindBrokenModViewModel(context), host, dialogs, store);
    }

    private static BrokenModCandidate Candidate(string name, string rowsJson = "") => new(name, $"Nice {name}", ExmodJson.Parse($$"""
        {"name": "{{name}}", "author": "T", "version": "1", "description": "D", "fileName": "{{name}}", "Rows": [{{rowsJson}}]}
        """));

    /// <summary>A small extracted game: the Canteen item, called "Steel Canteen" in game (its Itemable row Item_Canteen).</summary>
    private GameTableIndex WriteCanteenGameData()
    {
        var data = Path.Combine(_tempRoot, "Data");
        Directory.CreateDirectory(Path.Combine(data, "Items"));
        Directory.CreateDirectory(Path.Combine(data, "Traits"));
        File.WriteAllText(Path.Combine(data, "Items", "D_ItemsStatic.json"),
            """{"RowStruct":"S","Defaults":{"Itemable":{"RowName":"None","DataTableName":"D_Itemable"}},"Rows":[{"Name":"Canteen","Itemable":{"RowName":"Item_Canteen"}}]}""");
        File.WriteAllText(Path.Combine(data, "Traits", "D_Itemable.json"),
            """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Item_Canteen","DisplayName":"NSLOCTEXT(\"D_Itemable\", \"Item_Canteen-DisplayName\", \"Steel Canteen\")"}]}""");
        return new GameTableIndex(data);
    }

    /// <summary>Three queued mods over the canteen game data; Mod3 changes Item_Canteen.</summary>
    private (FindBrokenModViewModel Vm, FakeHost Host) CreateWithCanteenData()
    {
        var index = WriteCanteenGameData();
        var host = new FakeHost();
        var candidates = new List<BrokenModCandidate>
        {
            Candidate("Mod1"),
            Candidate("Mod2"),
            Candidate("Mod3", """{"CurrentFile": "Traits-D_Itemable.json", "File_Items": [{"Name": "Item_Canteen", "Weight": 2}]}"""),
        };
        host.Queue.AddRange(candidates.Select(c => c.FolderName));
        var vm = new FindBrokenModViewModel(new FindBrokenModContext
        {
            Host = host,
            Store = new BrokenModSearchStore(Path.Combine(_tempRoot, "find_broken_mod.json")),
            Dialogs = new FakeDialogs(),
            ReadCandidates = () => Task.FromResult<IReadOnlyList<BrokenModCandidate>>(candidates),
            GameIndex = () => index,
            GameRows = () => Task.FromResult<IReadOnlyList<GameDataItemRef>>([new GameDataItemRef("Traits-D_Itemable.json", "Traits/D_Itemable.json", "Item_Canteen")]),
        });
        return (vm, host);
    }

    /// <summary>Answers every round as a player whose game breaks while <paramref name="culprit"/> is in.</summary>
    private static async Task PlayAsync(FindBrokenModViewModel vm, FakeHost host, string culprit)
    {
        for (var i = 0; i < 50 && vm.IsRoundStep; i++)
        {
            Assert.True(vm.IsRoundInstalled);
            if (host.Rounds[^1].Contains(culprit))
            {
                await vm.StillBrokenCommand.ExecuteAsync(null);
            }
            else
            {
                await vm.WorksNowCommand.ExecuteAsync(null);
            }
        }
    }

    /// <summary>The row list showed "Accolades/D_Accolades.json — GlassBuildingsCrafted20": it reads in plain words now.</summary>
    [Fact]
    public void GameRows_ReadInPlainWords_WithTheItemsInGameName()
    {
        var data = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "FinderRows_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(data, "Items"));
            Directory.CreateDirectory(Path.Combine(data, "Traits"));
            File.WriteAllText(Path.Combine(data, "Items", "D_ItemsStatic.json"),
                """{"RowStruct":"S","Defaults":{"Itemable":{"RowName":"None","DataTableName":"D_Itemable"}},"Rows":[{"Name":"Canteen","Itemable":{"RowName":"Item_Canteen"}}]}""");
            File.WriteAllText(Path.Combine(data, "Traits", "D_Itemable.json"),
                """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Item_Canteen","DisplayName":"NSLOCTEXT(\"D_Itemable\", \"Item_Canteen-DisplayName\", \"Steel Canteen\")"}]}""");

            var rows = FindBrokenModViewModel.WithPlainLabels(
                [
                    new GameDataItemRef("Traits-D_Itemable.json", "Traits/D_Itemable.json", "Item_Canteen"),
                    new GameDataItemRef("Accolades-D_Accolades.json", "Accolades/D_Accolades.json", "GlassBuildingsCrafted20"),
                ],
                new GameTableIndex(data));

            Assert.Equal("Steel Canteen — Names, weights, stacks  (Item_Canteen)", rows[0].Display);
            Assert.Equal("GlassBuildingsCrafted20 — Accolades", rows[1].Display);
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Fact]
    public async Task Search_FromStartToPutBack_NamesTheMod_AndPutsEveryOtherModBack()
    {
        var (vm, host, dialogs, store) = Create();

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(1, host.Backups);
        Assert.Contains(dialogs.Asked, a => a.Contains("backs up your saves"));
        Assert.True(vm.IsRoundStep);
        Assert.NotNull(store.Load());

        await PlayAsync(vm, host, "Mod3");

        Assert.True(vm.IsResultStep);
        Assert.Equal("Found it: Nice Mod3", vm.ResultTitle);
        var found = Assert.Single(vm.Culprits);
        Assert.True(found.HasNexusPage);

        vm.TurnOffCommand.Execute(found);
        await vm.PutModsBackCommand.ExecuteAsync(null);

        Assert.True(vm.IsDoneStep);
        Assert.DoesNotContain("Mod3", host.PutBacks.Single());
        Assert.Equal(7, host.PutBacks.Single().Count);
        Assert.Null(store.Load());
        Assert.False(vm.HasOpenSearch);
    }

    [Fact]
    public async Task Start_SaveBackupDeclined_StartsNothing()
    {
        var (vm, host, dialogs, store) = Create();
        dialogs.ConfirmAnswer = false;

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.IsDescribeStep);
        Assert.Equal(0, host.Backups);
        Assert.Empty(host.Rounds);
        Assert.Null(store.Load());
    }

    [Fact]
    public async Task Start_WhileTheGameRuns_IsRefused()
    {
        var (vm, host, _, _) = Create();
        host.GameRunning = true;

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.IsDescribeStep);
        Assert.Empty(host.Rounds);
        Assert.Equal(BrokenModFinderSession.GameRunningMessage, vm.StatusText);
    }

    [Fact]
    public async Task ClosingMidSearch_PutThemBack_PutsTheModsBackAndCloses()
    {
        var (vm, host, dialogs, store) = Create();
        await vm.StartCommand.ExecuteAsync(null);
        dialogs.CloseAnswer = true;

        var canClose = await vm.CanCloseAsync();

        Assert.True(canClose);
        Assert.Single(host.PutBacks);
        Assert.Null(store.Load());
    }

    [Fact]
    public async Task ClosingMidSearch_Later_KeepsTheSearchSoTheNextStartCanPutTheModsBack()
    {
        var (vm, host, dialogs, store) = Create();
        await vm.StartCommand.ExecuteAsync(null);
        await vm.WorksNowCommand.ExecuteAsync(null);
        dialogs.CloseAnswer = false;

        Assert.True(await vm.CanCloseAsync());
        Assert.Empty(host.PutBacks);
        var saved = store.Load();
        Assert.NotNull(saved);
        Assert.Equal(host.Queue, saved!.OriginalQueue);

        // The next start: the window opens on the same round, already installed.
        var resumed = new FindBrokenModViewModel(new FindBrokenModContext
        {
            Host = host,
            Store = store,
            Dialogs = dialogs,
            ReadCandidates = () => Task.FromResult<IReadOnlyList<BrokenModCandidate>>([]),
        }, saved);
        Assert.True(resumed.IsRoundStep);
        Assert.True(resumed.IsRoundInstalled);
        Assert.Equal("Round 2", resumed.RoundTitle);
    }

    /// <summary>
    /// Round 2's install failed (the merge didn't build), the player closed with Later: next start
    /// used to reopen round 2 as installed, so they could answer a round that never reached the game.
    /// </summary>
    [Fact]
    public async Task ClosingMidSearch_AfterARoundFailedToInstall_ReopensAskingToInstallIt()
    {
        var (vm, host, dialogs, store) = Create();
        await vm.StartCommand.ExecuteAsync(null);
        host.RoundFailure = "The merge didn't build.";
        await vm.WorksNowCommand.ExecuteAsync(null);
        Assert.False(vm.IsRoundInstalled);
        dialogs.CloseAnswer = false;
        Assert.True(await vm.CanCloseAsync());

        var resumed = new FindBrokenModViewModel(new FindBrokenModContext
        {
            Host = host,
            Store = store,
            Dialogs = dialogs,
            ReadCandidates = () => Task.FromResult<IReadOnlyList<BrokenModCandidate>>([]),
        }, store.Load());
        Assert.True(resumed.IsRoundStep);
        Assert.Equal("Round 2", resumed.RoundTitle);
        Assert.False(resumed.IsRoundInstalled);

        host.RoundFailure = null;
        await resumed.InstallRoundCommand.ExecuteAsync(null);
        Assert.True(resumed.IsRoundInstalled);
        Assert.True(store.Load()!.RoundInstalled);
    }

    /// <summary>Regression: players type the name they see in game, as the tooltip asks; "steel canteen" listed the row but found no suspect.</summary>
    [Fact]
    public async Task FindSuspects_FromTheInGameName_FindsTheModThatChangesIt()
    {
        var (vm, _) = CreateWithCanteenData();
        vm.ProblemText = "steel canteen";

        await vm.FindSuspectsCommand.ExecuteAsync(null);

        Assert.Equal("Mod3", Assert.Single(vm.Suspects).FolderName);
        Assert.StartsWith("1 of your 3 queued mods point at it", vm.SuspectSummary);
    }

    /// <summary>Regression: after Find suspects, changing the words kept the old suspects, and Start searched with them.</summary>
    [Fact]
    public async Task ChangingTheDescription_ClearsTheSuspectsFoundForTheOldOne()
    {
        var (vm, _) = CreateWithCanteenData();
        vm.ProblemText = "canteen";
        await vm.FindSuspectsCommand.ExecuteAsync(null);
        Assert.Single(vm.Suspects);

        vm.ProblemText = "backpack";

        Assert.Empty(vm.Suspects);
        Assert.Null(vm.SuspectSummary);
    }

    /// <summary>Regression: a mod ticked between Find suspects and Start was left out of the search, then put in the game at the end.</summary>
    [Fact]
    public async Task Start_AfterTheQueueChanged_ReadsItAgain()
    {
        var host = new FakeHost();
        host.Queue.AddRange(["Mod1", "Mod2"]);
        var store = new BrokenModSearchStore(Path.Combine(_tempRoot, "find_broken_mod.json"));
        var vm = new FindBrokenModViewModel(new FindBrokenModContext
        {
            Host = host,
            Store = store,
            Dialogs = new FakeDialogs(),
            ReadCandidates = () => Task.FromResult<IReadOnlyList<BrokenModCandidate>>([.. host.Queue.Select(q => Candidate(q))]),
        });
        await vm.FindSuspectsCommand.ExecuteAsync(null);
        host.Queue.Add("Mod9");

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Contains("Mod9", store.Load()!.OriginalQueue);
    }

    /// <summary>Regression: the finder wrapped Merge &amp; Install's own text, so it read "Not installed: Not installed: you chose not to."</summary>
    [Fact]
    public async Task RoundNotInstalled_SaysWhyOnce()
    {
        var (vm, host, _, _) = Create();
        host.RoundFailure = "Not installed: you chose not to.";

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal("Not installed: you chose not to. Then click Install this round.", vm.StatusText);
    }

    /// <summary>A reason without a final full stop used to run into the next sentence.</summary>
    [Fact]
    public async Task PutBackFailed_EndsTheReasonBeforeWhatToDo()
    {
        var (vm, host, _, _) = Create();
        await vm.StartCommand.ExecuteAsync(null);
        host.PutBackFailure = "Install failed: the disk is full";

        await vm.PutModsBackCommand.ExecuteAsync(null);

        Assert.StartsWith("Your mods aren't back yet. Install failed: the disk is full. Then click Put my mods back again.", vm.StatusText);
    }

    /// <summary>Regression: an answer while find_broken_mod.json was held by an antivirus, backup or sync tool closed Starlink with a crash report.</summary>
    [Fact]
    public async Task AnswerWhileTheSearchFileIsHeld_DoesNotCrashStarlink()
    {
        var (vm, _, _, store) = Create();
        await vm.StartCommand.ExecuteAsync(null);

        using (new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await vm.WorksNowCommand.ExecuteAsync(null);
        }

        Assert.True(vm.IsRoundStep);
        Assert.Equal("Round 2", vm.RoundTitle);
        Assert.NotNull(vm.StatusText);
    }

    /// <summary>Regression: Turn it off while the search file was held closed Starlink with a crash report.</summary>
    [Fact]
    public async Task TurnOffWhileTheSearchFileIsHeld_TurnsItOffAndSaysTheSaveWaits()
    {
        var (vm, host, _, store) = Create();
        await vm.StartCommand.ExecuteAsync(null);
        await PlayAsync(vm, host, "Mod3");
        var found = Assert.Single(vm.Culprits);

        using (new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            vm.TurnOffCommand.Execute(found);
        }

        Assert.True(found.IsTurnedOff);
        Assert.DoesNotContain("Mod3", host.Queue);
        Assert.Contains("Couldn't save", vm.StatusText);
    }

    /// <summary>Regression: Turn it off while Merge &amp; Install builds was refused, yet the row said it was off.</summary>
    [Fact]
    public async Task TurnOffWhileMergeAndInstallBuilds_LeavesTheRowOnAndSaysWhy()
    {
        var (vm, host, _, _) = Create();
        await vm.StartCommand.ExecuteAsync(null);
        await PlayAsync(vm, host, "Mod3");
        var found = Assert.Single(vm.Culprits);
        host.RefuseRemove = true;

        vm.TurnOffCommand.Execute(found);

        Assert.False(found.IsTurnedOff);
        Assert.Contains("building", vm.StatusText);
    }

    [Fact]
    public async Task ClosingMidSearch_Cancel_KeepsTheWindowOpen()
    {
        var (vm, _, dialogs, store) = Create();
        await vm.StartCommand.ExecuteAsync(null);
        dialogs.CloseAnswer = null;

        Assert.False(await vm.CanCloseAsync());
        Assert.NotNull(store.Load());
    }

    /// <summary>
    /// Regression: double-clicking the matching rows list's scrollbar or the blank space under the
    /// rows picked whichever row was selected; a double-click picks only the row it lands on.
    /// </summary>
    [Fact]
    public void MatchListDoubleClick_OffTheRows_PicksNothing_OnARow_PicksThatRow()
    {
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                var first = new GameDataItemRef("Traits-D_Itemable.json", "Traits/D_Itemable.json", "Item_Canteen");
                var second = new GameDataItemRef("Traits-D_Itemable.json", "Traits/D_Itemable.json", "Item_Fiber");
                var list = new System.Windows.Controls.ListBox { Height = 60, DisplayMemberPath = nameof(GameDataItemRef.ItemName), ItemsSource = new[] { first, second } };
                var host = new System.Windows.Controls.StackPanel { Children = { list } };
                host.Measure(new System.Windows.Size(300, 200));
                host.Arrange(new System.Windows.Rect(0, 0, 300, 200));
                host.UpdateLayout();
                list.SelectedItem = first;
                var secondRow = Assert.IsType<System.Windows.Controls.ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(1));
                var secondText = FindChild<System.Windows.Controls.TextBlock>(secondRow);
                Assert.NotNull(secondText);
                var scroller = FindChild<System.Windows.Controls.ScrollViewer>(list);
                Assert.NotNull(scroller);

                Assert.Null(FindBrokenModWindow.RowToPick(list, DoubleClick(list, System.Windows.Input.MouseButton.Left)));
                Assert.Null(FindBrokenModWindow.RowToPick(list, DoubleClick(scroller!, System.Windows.Input.MouseButton.Left)));
                Assert.Same(second, FindBrokenModWindow.RowToPick(list, DoubleClick(secondText!, System.Windows.Input.MouseButton.Left)));
                Assert.Null(FindBrokenModWindow.RowToPick(list, DoubleClick(secondText!, System.Windows.Input.MouseButton.Right)));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw failure;
        }
    }

    private static System.Windows.Input.MouseButtonEventArgs DoubleClick(object source, System.Windows.Input.MouseButton button) =>
        new(System.Windows.Input.Mouse.PrimaryDevice, 0, button)
        {
            RoutedEvent = System.Windows.Controls.Control.MouseDoubleClickEvent,
            Source = source,
        };

    private static T? FindChild<T>(System.Windows.DependencyObject parent)
        where T : System.Windows.DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
            {
                return found;
            }

            if (FindChild<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }
}
