using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.PakIO.Assets;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// After a game update: the Library's bar says Icarus updated (the Steam build changed), and
/// Update everything gets the mods ready for it (GameUpdateCoordinator runs the steps and shows
/// what changed).
/// </summary>
public sealed partial class LibraryViewModel
{
    /// <summary>Reads the game build and remembers the one the mods were made ready for; set by the app (null: no game update bar).</summary>
    public GameUpdateDetector? GameUpdates { get; init; }

    /// <summary>The game's own copies of the files mods replace, per game build; set by the app (null: not recorded).</summary>
    public GameFileFingerprintStore? GameFileFingerprints { get; init; }

    /// <summary>Runs Update everything and opens its review; set by the app (lazily: it needs this page and Merge &amp; Install).</summary>
    public Func<GameUpdateCoordinator>? GameUpdateCoordinator { get; init; }

    /// <summary>The game build on disk and, when Icarus updated since the mods were made ready, the one before.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGameUpdate), nameof(GameUpdateText), nameof(ShowPendingBar))]
    private GameUpdateState? _gameUpdate;

    public bool HasGameUpdate => GameUpdate is { IsUpdated: true };

    public string GameUpdateText => GameUpdate is { IsUpdated: true } state ? $"Icarus updated{state.BuildLabel}." : "";

    /// <summary>The bar under the list: something isn't in the game yet, or the game updated.</summary>
    public bool ShowPendingBar => HasPendingChanges || HasGameUpdate;

    partial void OnHasPendingChangesChanged(bool value) => OnPropertyChanged(nameof(ShowPendingBar));

    /// <summary>Builds already announced this session (one pop-up per update, not one per visit).</summary>
    private readonly HashSet<string> _announcedGameBuilds = new(StringComparer.OrdinalIgnoreCase);

    private bool _checkingGameUpdate;

    /// <summary>At start and each time the Library is shown: reads the game build and shows the bar (and once, a pop-up) when it changed.</summary>
    public async Task CheckForGameUpdateAsync()
    {
        if (GameUpdates is not { } detector || _checkingGameUpdate)
        {
            return;
        }

        _checkingGameUpdate = true;
        try
        {
            var state = await detector.CheckAsync();
            GameUpdate = state;
            if (state is { IsUpdated: true } && _announcedGameBuilds.Add(state.CurrentBuild))
            {
                _activityLog.Log($"Icarus updated{state.BuildLabel}. On the Library page, Update everything… gets your mods ready for it.", ActivityEntryKind.Warning);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Couldn't read the game build");
        }
        finally
        {
            _checkingGameUpdate = false;
        }
    }

    /// <summary>The bar's Dismiss: the mods count as ready for this build (nothing is changed).</summary>
    [RelayCommand]
    private void DismissGameUpdate()
    {
        if (GameUpdate is { } state && GameUpdates is { } detector)
        {
            detector.MarkReady(state.CurrentBuild);
            GameUpdate = state with { PreviousBuild = null };
        }
    }

    /// <summary>The bar's Update everything…: snapshot, refresh, check, fix, update check, merge and install, then what changed.</summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private Task UpdateEverythingAsync() => GameUpdateCoordinator?.Invoke().RunAsync() ?? Task.CompletedTask;

    /// <summary>Check ▾ › What the last game update changed…: the review of the last Update everything, as things are now.</summary>
    [RelayCommand]
    private void ShowLastGameUpdate() => GameUpdateCoordinator?.Invoke().ShowLastReview();

    /// <summary>The run moved the mods on to this build: the bar goes.</summary>
    internal void MarkGameUpdateHandled(string build)
    {
        GameUpdates?.MarkReady(build);
        GameUpdate = new GameUpdateState(build, null);
    }

    /// <summary>Waits until no check, fix or refresh-after-game-data pass is running (they refuse to start alongside each other).</summary>
    internal async Task WaitForModPassesAsync()
    {
        while (IsCheckingStaleness || _refreshConvertedPending)
        {
            await Task.Delay(200);
        }
    }

    /// <summary>Rates the game files mods replace now (recording the game's copies for this build), waiting for a pass already running.</summary>
    internal async Task RateFileRisksNowAsync()
    {
        while (_fileRiskPassRunning)
        {
            await Task.Delay(200);
        }

        await RefreshFileRisksAsync();
    }

    /// <summary>Every pak/EXMOD row (not the UE4SS ones).</summary>
    internal IReadOnlyList<LibraryItemViewModel> ModItems => AllLibraryItems();

    internal LibraryItemViewModel? FindItem(string folderName) => _itemsByFolderName.GetValueOrDefault(folderName);

    /// <summary>Steam updated the game while Starlink was open: the game files it read this session are from the build before.</summary>
    internal bool IsGameContentFromEarlierBuild(string currentBuild) =>
        _sessionGameBuild is not null && !string.Equals(_sessionGameBuild, currentBuild, StringComparison.OrdinalIgnoreCase);
}
