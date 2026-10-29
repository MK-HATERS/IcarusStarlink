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

        public Task<string?> InstallOnlyAsync(IReadOnlyCollection<string> folderNames)
        {
            Rounds.Add(folderNames);
            return Task.FromResult<string?>(null);
        }

        public Task<string?> InstallWholeQueueAsync(IReadOnlyCollection<string> putBack)
        {
            PutBacks.Add(putBack);
            return Task.FromResult<string?>(null);
        }

        public void RemoveFromQueue(string folderName) => Queue.Remove(folderName);

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

    [Fact]
    public async Task ClosingMidSearch_Cancel_KeepsTheWindowOpen()
    {
        var (vm, _, dialogs, store) = Create();
        await vm.StartCommand.ExecuteAsync(null);
        dialogs.CloseAnswer = null;

        Assert.False(await vm.CanCloseAsync());
        Assert.NotNull(store.Load());
    }
}
