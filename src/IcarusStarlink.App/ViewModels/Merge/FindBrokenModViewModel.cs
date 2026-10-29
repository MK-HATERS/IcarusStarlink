using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Views;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// Find the broken mod: the player says what's broken (optional), sees the suspects, and answers
/// rounds (Still broken / Works now / Can't tell) while Starlink installs part of their mods each
/// time, until one mod (or a pair) is named. The search itself is BrokenModSearch, the rounds and
/// putting the mods back BrokenModFinderSession; this is the window's flow and words.
/// </summary>
public sealed partial class FindBrokenModViewModel : ObservableObject
{
    private readonly FindBrokenModContext _context;
    private BrokenModFinderSession? _session;
    private IReadOnlyList<BrokenModCandidate>? _candidates;
    private Dictionary<string, IReadOnlyCollection<string>> _needs = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<BrokenModSuspect> _firstGroup = [];
    private IReadOnlyList<GameDataItemRef>? _gameRows;
    private IReadOnlyDictionary<string, int> _problems = new Dictionary<string, int>();

    /// <param name="resume">A search saved by an earlier run (the app closed mid-search): it goes on where it was.</param>
    public FindBrokenModViewModel(FindBrokenModContext context, BrokenModSearchRecord? resume = null)
    {
        _context = context;
        if (resume is not null)
        {
            _session = new BrokenModFinderSession(context.Host, context.Store, resume);
            ProblemText = resume.Problem;
            if (resume.Search.End != BrokenModSearchEndKind.None)
            {
                ShowResult();
            }
            else
            {
                Step = FindBrokenModStep.Round;
                // A round left waiting for its answer, if its install finished before the app
                // closed; one whose install failed shows Install this round again.
                ShowRound(isInstalled: resume.Search.Testing is not null && resume.RoundInstalled);
            }
        }
    }

    /// <summary>Closes the window (set by it).</summary>
    public Action? Close { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDescribeStep), nameof(IsRoundStep), nameof(IsResultStep), nameof(IsDoneStep))]
    private FindBrokenModStep _step = FindBrokenModStep.Describe;

    public bool IsDescribeStep => Step == FindBrokenModStep.Describe;

    public bool IsRoundStep => Step == FindBrokenModStep.Round;

    public bool IsResultStep => Step == FindBrokenModStep.Result;

    public bool IsDoneStep => Step == FindBrokenModStep.Done;

    /// <summary>A search is under way: the game may hold only some of the mods.</summary>
    public bool HasOpenSearch => _session is { IsClosed: false };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(StillBrokenCommand), nameof(WorksNowCommand), nameof(CantTellCommand))]
    private bool _isWorking;

    public bool IsIdle => !IsWorking;

    [ObservableProperty]
    private string? _statusText;

    // ---- Step 1: what's broken, and the suspects ----

    [ObservableProperty]
    private string _problemText = "";

    public ObservableCollection<GameDataItemRef> MatchingRows { get; } = [];

    public ObservableCollection<GameDataItemRef> PickedRows { get; } = [];

    [ObservableProperty]
    private GameDataItemRef? _selectedMatch;

    public ObservableCollection<FindBrokenModSuspectRow> Suspects { get; } = [];

    [ObservableProperty]
    private string? _suspectSummary;

    partial void OnProblemTextChanged(string value) => _ = RefreshMatchesAsync(value);

    private int _matchVersion;

    /// <summary>The game rows named like what's typed (at least 3 letters), for picking the exact row.</summary>
    private async Task RefreshMatchesAsync(string text)
    {
        var version = ++_matchVersion;
        var term = text.Trim();
        if (term.Length < 3)
        {
            MatchingRows.Clear();
            return;
        }

        try
        {
            _gameRows ??= await LoadGameRowsAsync();
        }
        catch (Exception)
        {
            _gameRows = [];
        }

        if (version != _matchVersion)
        {
            return;
        }

        MatchingRows.Clear();
        foreach (var row in _gameRows.Where(r => r.ItemName.Contains(term, StringComparison.OrdinalIgnoreCase)
                     || (r.Label?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)).Take(100))
        {
            MatchingRows.Add(row);
        }
    }

    /// <summary>
    /// The row list in plain words: the item's in-game name and the part of the game the row is
    /// ("Canteen — Eating or using"), the row's own name in brackets when it differs. Was
    /// "Accolades/D_Accolades.json — GlassBuildingsCrafted20" (seen in the app, 2026-10-28).
    /// </summary>
    private async Task<IReadOnlyList<GameDataItemRef>> LoadGameRowsAsync()
    {
        var rows = await _context.GameRows();
        var index = _context.GameIndex();
        return await Task.Run(() => WithPlainLabels(rows, index));
    }

    internal static IReadOnlyList<GameDataItemRef> WithPlainLabels(IReadOnlyList<GameDataItemRef> rows, GameTableIndex? index)
    {
        var names = index is not null && index.CurrentFileFor("D_ItemsStatic") is not null ? new GameItemNames(index) : null;
        return [.. rows.Select(row =>
        {
            var part = ExmodEditorViewModel.PartOfGame(GameTableIndex.TableNameOf(row.CurrentFile));
            var gameItem = names?.ItemFor(row.CurrentFile, row.ItemName);
            var title = gameItem is null ? null : names!.DisplayName(gameItem);
            return row with
            {
                Label = title is null || string.Equals(title, row.ItemName, StringComparison.OrdinalIgnoreCase)
                    ? $"{row.ItemName} — {part}"
                    : $"{title} — {part}  ({row.ItemName})",
            };
        })];
    }

    [RelayCommand]
    private void PickRow(GameDataItemRef? row)
    {
        row ??= SelectedMatch;
        if (row is null || PickedRows.Contains(row))
        {
            return;
        }

        PickedRows.Add(row);
        _candidates = null;
    }

    [RelayCommand]
    private void UnpickRow(GameDataItemRef? row)
    {
        if (row is not null && PickedRows.Remove(row))
        {
            _candidates = null;
        }
    }

    /// <summary>Reads the queue and works out the suspects (and what each mod needs) for what the player described.</summary>
    [RelayCommand]
    private async Task FindSuspectsAsync()
    {
        IsWorking = true;
        StatusText = "Looking at your mods…";
        try
        {
            var candidates = await _context.ReadCandidates();
            var index = _context.GameIndex();
            var words = ProblemText;
            var picked = PickedRows.Select(r => new BrokenModTarget(r.CurrentFile, r.ItemName)).ToList();
            if (picked.Count == 0 && words.Trim().Length >= 3)
            {
                _gameRows ??= await LoadGameRowsAsync();
            }

            var gameRows = _gameRows ?? [];
            var (needs, suspects) = await Task.Run(() =>
            {
                var targets = picked.Count > 0
                    ? picked
                    : BrokenModClues.TargetsFromWords(words, gameRows.Select(r => (r.CurrentFile, r.ItemName)), candidates);
                var needs = index is null ? new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase) : BrokenModClues.Needs(candidates, index);
                return (needs, BrokenModClues.Suspects(candidates, targets, index));
            });

            try
            {
                _problems = await _context.ProblemCounts();
            }
            catch (Exception)
            {
                // Only a hint.
                _problems = new Dictionary<string, int>();
            }

            _candidates = candidates;
            _needs = needs;
            _firstGroup = BrokenModClues.FirstGroup(suspects, candidates.Count);
            var first = _firstGroup.Select(s => s.FolderName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Suspects.Clear();
            foreach (var suspect in suspects)
            {
                Suspects.Add(new FindBrokenModSuspectRow(suspect.FolderName, suspect.Name, suspect.Reason, ProblemCount(suspect.FolderName), first.Contains(suspect.FolderName)));
            }

            SuspectSummary = candidates.Count == 0
                ? "Your merge queue is empty: there's nothing to test. Turn your mods on in the Library first."
                : suspects.Count == 0
                    ? $"No mod points at it, so the search halves all {candidates.Count} queued mods. About {RoundsFor(candidates.Count)} rounds."
                    : _firstGroup.Count == 0
                        ? $"Every queued mod touches it, so the search halves all {candidates.Count}. About {RoundsFor(candidates.Count)} rounds."
                        : $"{_firstGroup.Count} of your {candidates.Count} queued mods point at it. The first round takes those out; then it halves. About {RoundsFor(candidates.Count)} rounds.";
            StatusText = null;
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't read your mods: {ex.Message}";
        }
        finally
        {
            IsWorking = false;
        }
    }

    private int ProblemCount(string folderName) =>
        _problems.FirstOrDefault(p => string.Equals(p.Key, folderName, StringComparison.OrdinalIgnoreCase)).Value;

    private static int RoundsFor(int mods) => Math.Max(1, (int)Math.Ceiling(Math.Log2(mods + 1)));

    [RelayCommand]
    private async Task OpenSuspectFixAsync(FindBrokenModSuspectRow? row)
    {
        if (row is not null)
        {
            await OpenFixAsync(row.FolderName);
        }
    }

    private async Task OpenFixAsync(string folderName)
    {
        try
        {
            if (await _context.OpenFixWindow(folderName) is { } failed)
            {
                StatusText = $"Couldn't open the fix window: {failed}";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't open the fix window: {ex.Message}";
        }
    }

    /// <summary>Backs up the saves (asks first, saying why), saves the search, and installs round 1.</summary>
    [RelayCommand]
    private async Task StartAsync()
    {
        if (_candidates is null)
        {
            await FindSuspectsAsync();
        }

        if (_candidates is not { Count: > 0 } candidates)
        {
            StatusText ??= "Your merge queue is empty: there's nothing to test.";
            return;
        }

        if (_context.Host.IsGameRunning())
        {
            StatusText = BrokenModFinderSession.GameRunningMessage;
            return;
        }

        if (!_context.Dialogs.Confirm(
            "Each round puts only some of your mods in the game. If you load a world or character in a round, items and buildings from the mods that are out can be lost from it.\n\n"
            + "So Starlink backs up your saves first: your characters and account (Saves › Restore puts them back) and a zip of your worlds (in the save backups folder).\n\n"
            + "Even so, test on a new world if you can. When you finish or stop, Starlink puts all your mods back.\n\n"
            + "Back up your saves and start?",
            "Find the broken mod", ThemedConfirmSeverity.Question))
        {
            return;
        }

        var record = new BrokenModSearchRecord
        {
            StartedAtUtc = DateTimeOffset.UtcNow,
            Problem = ProblemText.Trim(),
            OriginalQueue = [.. candidates.Select(c => c.FolderName)],
            Names = candidates.ToDictionary(c => c.FolderName, c => c.Name, StringComparer.OrdinalIgnoreCase),
            SuspectReasons = _firstGroup.ToDictionary(s => s.FolderName, s => s.Reason, StringComparer.OrdinalIgnoreCase),
            Search = BrokenModSearch.Start([.. candidates.Select(c => c.FolderName)], _needs, [.. _firstGroup.Select(s => s.FolderName)]),
        };
        var session = new BrokenModFinderSession(_context.Host, _context.Store, record);

        IsWorking = true;
        StatusText = "Backing up your saves…";
        var backupFailed = await Task.Run(session.BackUpSaves);
        IsWorking = false;
        if (backupFailed is not null)
        {
            StatusText = $"{backupFailed} The search didn't start.";
            return;
        }

        _session = session;
        OnPropertyChanged(nameof(HasOpenSearch));
        Step = FindBrokenModStep.Round;
        await InstallRoundAsync();
    }

    // ---- Step 2: the rounds ----

    [ObservableProperty]
    private string? _roundTitle;

    [ObservableProperty]
    private string? _roundText;

    public ObservableCollection<string> InGameMods { get; } = [];

    public ObservableCollection<string> OutOfGameMods { get; } = [];

    [ObservableProperty]
    private string? _outOfGameHeader;

    /// <summary>The round is in the game and waits for the player's answer.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StillBrokenCommand), nameof(WorksNowCommand), nameof(CantTellCommand))]
    private bool _isRoundInstalled;

    private void ShowRound(bool isInstalled)
    {
        if (_session?.CurrentTest is not { } test)
        {
            ShowResult();
            return;
        }

        var record = _session.Record;
        var inGame = test.Install.ToHashSet(StringComparer.OrdinalIgnoreCase);
        RoundTitle = $"Round {record.Search.Round + 1}";
        InGameMods.Clear();
        OutOfGameMods.Clear();
        foreach (var folder in record.OriginalQueue)
        {
            (inGame.Contains(folder) ? InGameMods : OutOfGameMods).Add(record.NameOf(folder));
        }

        var left = test.RoundsLeftAtLeast <= 1 ? "This may be the last round." : $"About {test.RoundsLeftAtLeast} rounds to go.";
        RoundText = (test.IsSuspectRound ? $"This round takes out the {OutOfGameMods.Count} suspects. " : "")
            + $"{InGameMods.Count} of your {record.OriginalQueue.Count} mods are in the game. {left}";

        OutOfGameHeader = $"OUT FOR THIS ROUND ({OutOfGameMods.Count})";
        IsRoundInstalled = isInstalled;
        StatusText = isInstalled ? "Start the game, check, then come back and say what you saw." : null;

        var (added, gone) = _session.QueueChangesSinceStart();
        if (added.Count > 0 || gone.Count > 0)
        {
            StatusText = (StatusText is null ? "" : StatusText + " ")
                + "Your merge queue changed since the search started, so the answers may not fit. Stop and start again to be sure.";
        }
    }

    /// <summary>Installs the round to test now. Refused while the game runs (said in the status line).</summary>
    [RelayCommand]
    private async Task InstallRoundAsync()
    {
        if (_session is null)
        {
            return;
        }

        ShowRound(isInstalled: false);
        if (_session.CurrentTest is null)
        {
            return;
        }

        IsWorking = true;
        StatusText = $"Installing {RoundTitle?.ToLowerInvariant()}…";
        string? failed;
        try
        {
            failed = await _session.InstallRoundAsync();
        }
        catch (Exception ex)
        {
            failed = ex.Message;
        }
        finally
        {
            IsWorking = false;
        }

        if (failed is not null)
        {
            StatusText = $"Not installed: {failed} Then click Install this round.";
            return;
        }

        ShowRound(isInstalled: true);
    }

    private bool CanAnswer() => IsRoundInstalled && !IsWorking;

    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private Task StillBrokenAsync() => AnswerAsync(BrokenModAnswer.StillBroken);

    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private Task WorksNowAsync() => AnswerAsync(BrokenModAnswer.WorksNow);

    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private Task CantTellAsync() => AnswerAsync(BrokenModAnswer.CantTell);

    private async Task AnswerAsync(BrokenModAnswer answer)
    {
        if (_session is null || !IsRoundInstalled)
        {
            return;
        }

        _session.Answer(answer);
        IsRoundInstalled = false;
        if (_session.CurrentTest is null)
        {
            ShowResult();
            return;
        }

        await InstallRoundAsync();
    }

    /// <summary>Stops the search (asks first) and puts every mod back.</summary>
    [RelayCommand]
    private async Task StopAsync()
    {
        if (_session is null)
        {
            Close?.Invoke();
            return;
        }

        if (!_context.Dialogs.Confirm("Stop the search and put all your mods back in the game?", "Find the broken mod", ThemedConfirmSeverity.Question))
        {
            return;
        }

        await PutModsBackAsync();
    }

    // ---- Step 3: the answer ----

    [ObservableProperty]
    private string? _resultTitle;

    [ObservableProperty]
    private string? _resultText;

    public ObservableCollection<FindBrokenModCulpritRow> Culprits { get; } = [];

    private void ShowResult()
    {
        if (_session is null)
        {
            return;
        }

        var record = _session.Record;
        var search = record.Search;
        Culprits.Clear();
        switch (search.End)
        {
            case BrokenModSearchEndKind.Found:
                ResultTitle = search.Culprits.Count == 1
                    ? $"Found it: {record.NameOf(search.Culprits[0])}"
                    : $"Found it: these {search.Culprits.Count} mods break it together";
                ResultText = search.Culprits.Count == 1
                    ? "With it in, it's broken; with it out, it works."
                    : "Each works without the other; with all of them in, it's broken. Turning one off is enough.";
                AddCulprits(search.Culprits, record);
                break;
            case BrokenModSearchEndKind.NotADataMod:
                ResultTitle = "It isn't one of your data mods";
                ResultText = "It was still broken with none of them in. It may be a UE4SS (Lua) mod: turn those off half at a time on the Library's right panel. "
                    + "Or a merge option on Merge & Install, or the game itself.";
                break;
            case BrokenModSearchEndKind.Unsure:
                ResultTitle = "Couldn't narrow it down";
                ResultText = "Too many rounds were \"Can't tell\". It's one of these"
                    + (search.Found.Count > 0 ? $", together with {string.Join(", ", search.Found.Select(record.NameOf))}." : ".");
                AddCulprits(search.Unsure, record);
                break;
        }

        ResultText += " Put your mods back when you're done here.";
        Step = FindBrokenModStep.Result;
        StatusText = null;
    }

    private void AddCulprits(IEnumerable<string> folders, BrokenModSearchRecord record)
    {
        foreach (var folder in folders)
        {
            var notes = new List<string>();
            if (record.SuspectReasons.FirstOrDefault(r => string.Equals(r.Key, folder, StringComparison.OrdinalIgnoreCase)).Value is { } reason)
            {
                notes.Add($"It {reason}.");
            }

            if (record.Search.Needs.TryGetValue(folder, out var needs) && needs.Count > 0)
            {
                notes.Add($"It needs {string.Join(", ", needs.Select(record.NameOf))}, so that was always in with it.");
            }

            var problems = ProblemCount(folder);
            if (problems > 0)
            {
                notes.Add(problems == 1 ? "The health check found 1 problem in it." : $"The health check found {problems} problems in it.");
            }

            Culprits.Add(new FindBrokenModCulpritRow(folder, record.NameOf(folder), string.Join(' ', notes), problems > 0, SafeNexusId(folder))
            {
                IsTurnedOff = record.TurnedOff.Contains(folder, StringComparer.OrdinalIgnoreCase),
            });
        }
    }

    private int? SafeNexusId(string folder)
    {
        try
        {
            return _context.NexusModIdOf(folder);
        }
        catch (Exception)
        {
            return null;
        }
    }

    [RelayCommand]
    private async Task OpenCulpritFixAsync(FindBrokenModCulpritRow? row)
    {
        if (row is not null)
        {
            await OpenFixAsync(row.FolderName);
        }
    }

    /// <summary>Takes the mod out of the merge queue: it stays out when the mods are put back.</summary>
    [RelayCommand]
    private void TurnOff(FindBrokenModCulpritRow? row)
    {
        if (row is null || _session is null || row.IsTurnedOff)
        {
            return;
        }

        _session.TurnOff(row.FolderName);
        row.IsTurnedOff = true;
        StatusText = $"'{row.Name}' is off in your merge queue. Put your mods back to install the rest without it.";
    }

    [RelayCommand]
    private void OpenNexusPage(FindBrokenModCulpritRow? row)
    {
        if (row?.NexusModId is { } id)
        {
            _context.OpenUrl(IcarusStarlink.Core.Nexus.NexusModWebUrl.For(id));
        }
    }

    // ---- The end: every mod back in the game ----

    [ObservableProperty]
    private string? _doneText;

    /// <summary>Puts the player's whole queue back (all but what they turned off) and installs it. On a failure the search stays saved.</summary>
    [RelayCommand]
    private async Task PutModsBackAsync()
    {
        if (_session is null)
        {
            Close?.Invoke();
            return;
        }

        IsWorking = true;
        StatusText = "Putting your mods back…";
        string? failed;
        try
        {
            failed = await _session.PutModsBackAsync();
        }
        catch (Exception ex)
        {
            failed = ex.Message;
        }
        finally
        {
            IsWorking = false;
        }

        if (failed is not null)
        {
            StatusText = $"Your mods aren't back yet: {failed} Then click Put my mods back again. (If you close Starlink first, it offers this at the next start.)";
            return;
        }

        var turnedOff = _session.Record.TurnedOff.Select(_session.Record.NameOf).ToList();
        DoneText = "All your mods are back in the game."
            + (turnedOff.Count > 0 ? $" {string.Join(", ", turnedOff)} stay{(turnedOff.Count == 1 ? "s" : "")} off: turn {(turnedOff.Count == 1 ? "it" : "them")} back on in the Library if you want." : "");
        StatusText = null;
        OnPropertyChanged(nameof(HasOpenSearch));
        Step = FindBrokenModStep.Done;
    }

    /// <summary>
    /// The window is closing: with a search under way, asks whether to put the mods back now
    /// (Later keeps the search saved; the next start offers it). False keeps the window open.
    /// </summary>
    public async Task<bool> CanCloseAsync()
    {
        if (!HasOpenSearch || IsWorking)
        {
            return !IsWorking;
        }

        var answer = _context.Dialogs.ConfirmWithCancel(
            "Some of your mods may still be out of the game.\n\nPut them all back now? (Later: Starlink keeps the search and offers to put them back next time it starts.)",
            "Find the broken mod", ThemedConfirmSeverity.Warning, "Put them back", "Later");
        if (answer is null)
        {
            return false;
        }

        if (answer == false)
        {
            return true;
        }

        await PutModsBackAsync();
        return !HasOpenSearch;
    }
}
