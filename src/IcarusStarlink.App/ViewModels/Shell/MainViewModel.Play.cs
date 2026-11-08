using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.Views;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// What the game doesn't have yet, for Play's question: how many changes, whether any is a mod
/// for Merge &amp; Install (otherwise only UE4SS changes, which the Library's Apply puts in), and
/// the Library bars' own words for them.
/// </summary>
public sealed record ChangesNotInGame(int Count, bool NeedsMergeInstall, string Text);

/// <summary>What the header's Play button shows: Play, Starting… after a click, Running while Icarus is open.</summary>
public enum GamePlayState
{
    Ready,
    Starting,
    Running,
}

/// <summary>
/// The header's Play button, so repeated clicks never start the game twice.
/// A click starts Icarus through Steam and shows Starting… until the game's process appears (or 2
/// minutes pass with nothing); while it runs the button says Running and a click brings the game
/// to the front instead of asking Steam again.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>How long Starting… waits for the game to appear before Play is offered again (Steam may be updating it, or asking something).</summary>
    public static readonly TimeSpan StartingTimeout = TimeSpan.FromMinutes(2);

    private IGameProcessChecker? _gameProcess;
    private Func<DateTime> _clock = () => DateTime.UtcNow;
    private Func<string, Exception?> _open = UrlOpener.TryOpen;
    private Func<bool> _focusGame = GameWindow.TryBringToFront;
    private DateTime _startingSince;
    private DispatcherTimer? _gameWatch;
    private readonly IDialogService? _dialogs;

    /// <summary>The Library page once it has been shown this session: it tracks what the game doesn't have yet. Never built just for Play.</summary>
    private LibraryViewModel? _libraryShownThisSession;

    /// <summary>What the game doesn't have yet, for Play's question; null when it has everything. Tests swap it.</summary>
    internal Func<ChangesNotInGame?> ChangesNotInGameSource { get; set; } = () => null;

    private ChangesNotInGame? DefaultChangesNotInGame()
    {
        try
        {
            return _libraryShownThisSession?.DescribeChangesNotInGame();
        }
        catch (Exception)
        {
            // Best-effort: Play must never fail over this question.
            return null;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayButtonText), nameof(PlayButtonToolTip), nameof(IsGameRunning), nameof(IsGameStarting))]
    private GamePlayState _gamePlayState = GamePlayState.Ready;

    public bool IsGameRunning => GamePlayState == GamePlayState.Running;

    public bool IsGameStarting => GamePlayState == GamePlayState.Starting;

    public string PlayButtonText => GamePlayState switch
    {
        GamePlayState.Starting => "Starting…",
        GamePlayState.Running => "Running",
        _ => "Play",
    };

    public string PlayButtonToolTip => GamePlayState switch
    {
        GamePlayState.Starting => "Steam is starting Icarus. Play comes back if it hasn't started in 2 minutes.",
        GamePlayState.Running => "Icarus is running. Click to bring its window to the front.",
        _ => "Start Icarus through Steam",
    };

    private void UseGameSeams(IGameProcessChecker? gameProcess, Func<DateTime>? clock, Func<string, Exception?>? open, Func<bool>? focusGame)
    {
        _gameProcess = gameProcess;
        _clock = clock ?? _clock;
        _open = open ?? _open;
        _focusGame = focusGame ?? _focusGame;
        ChangesNotInGameSource = DefaultChangesNotInGame;
    }

    /// <summary>
    /// Steam's own URI launch, not the exe directly: it respects the launch options and overlay set in
    /// Steam, the same as Play in the Steam library. 1149460 is Icarus's App ID. Never asks Steam
    /// twice: a click while Starting… does nothing, a click while the game runs brings it forward.
    /// When Windows can't hand the link to Steam, the button stays on Play and the bell says why.
    /// </summary>
    [RelayCommand]
    private void LaunchGame()
    {
        if (GamePlayState == GamePlayState.Starting && _clock() - _startingSince < StartingTimeout)
        {
            return;
        }

        // Asked again even when the button says Running: the game may have closed since the last watch tick.
        if (IsGameProcessRunning())
        {
            GamePlayState = GamePlayState.Running;
            _focusGame();
            return;
        }

        if (!AskAboutChangesNotInGame())
        {
            return;
        }

        if (_open("steam://run/1149460") is { } error)
        {
            GamePlayState = GamePlayState.Ready;
            Warn($"Couldn't ask Steam to start Icarus: {error.Message} Start it from Steam, or check that Steam is installed.");
            return;
        }

        _startingSince = _clock();
        GamePlayState = GamePlayState.Starting;
    }

    /// <summary>
    /// Ticked mods or UE4SS changes the game doesn't have yet: a player who clicked Play would load
    /// in without them. Asks first (unless "Don't ask again" was chosen). True to start the game;
    /// the first button goes to where they are put in (Merge &amp; Install, or the Library's Apply
    /// for UE4SS changes only) instead of starting.
    /// </summary>
    private bool AskAboutChangesNotInGame()
    {
        if (_dialogs is null || !_settingsService.Current.AskBeforePlayWithChangesNotInGame || ChangesNotInGameSource() is not { Count: > 0 } changes)
        {
            return true;
        }

        var goFirst = changes.NeedsMergeInstall ? "Merge & Install first" : "Apply in Library first";
        var what = changes.Count == 1 ? "1 change isn't" : $"{changes.Count} changes aren't";
        var answer = _dialogs.Choose(
            $"{what} in the game yet:\n{changes.Text}\n\n"
                + (changes.NeedsMergeInstall
                    ? "Merge & Install puts them in. If you play now, Icarus starts without them."
                    : "Apply in the Library puts them in. If you play now, Icarus starts without them."),
            "Not in the game yet", ThemedConfirmSeverity.Warning, goFirst, "Play anyway", "Play, don't ask again");
        switch (answer)
        {
            case 0:
                SelectedNavItem = NavItems.First(item => item.Id == (changes.NeedsMergeInstall ? "merge" : "library"));
                return false;
            case 1:
                return true;
            case 2:
                _settingsService.SetAndSave(s => s.AskBeforePlayWithChangesNotInGame, (s, v) => s.AskBeforePlayWithChangesNotInGame = v, false);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Checks once whether Icarus is running (off the UI thread) and updates the button.</summary>
    public async Task RefreshGamePlayStateAsync()
    {
        if (_gameProcess is null)
        {
            return;
        }

        var running = await Task.Run(IsGameProcessRunning);
        ApplyGameRunning(running);
    }

    /// <summary>
    /// Running wins; otherwise Starting… holds until the game appears or the timeout passes, then Play.
    /// Running to Play means Icarus closed: GameClosedMessage tells Merge &amp; Install (its
    /// "Install when Icarus closes").
    /// </summary>
    internal void ApplyGameRunning(bool running)
    {
        if (running)
        {
            GamePlayState = GamePlayState.Running;
        }
        else if (GamePlayState != GamePlayState.Starting || _clock() - _startingSince >= StartingTimeout)
        {
            var wasRunning = GamePlayState == GamePlayState.Running;
            GamePlayState = GamePlayState.Ready;
            if (wasRunning)
            {
                WeakReferenceMessenger.Default.Send(new GameClosedMessage());
            }
        }
    }

    /// <summary>The UI thread's dispatcher (Application.Current.Dispatcher), where the watch timer must live. Tests swap it.</summary>
    internal Func<Dispatcher?> UiDispatcher { get; set; } = () => System.Windows.Application.Current?.Dispatcher;

    /// <summary>The dispatcher the game watch ticks on (null before StartGameWatch).</summary>
    internal Dispatcher? GameWatchDispatcher => _gameWatch?.Dispatcher;

    /// <summary>
    /// Watches for the game every 3 seconds while Starlink is open. App.OnStartup calls this on
    /// its background startup thread, before the main window shows, so the timer is put on the
    /// UI thread's dispatcher (UiDispatcher): a timer made on the startup thread never ticks, and
    /// the button would stay on whatever the first look saw (Starting… forever, or Running after
    /// the game closed).
    /// </summary>
    public void StartGameWatch()
    {
        if (_gameProcess is null || _gameWatch is not null)
        {
            return;
        }

        _gameWatch = new DispatcherTimer(DispatcherPriority.Normal, UiDispatcher() ?? Dispatcher.CurrentDispatcher) { Interval = TimeSpan.FromSeconds(3) };
        var checking = false;
        _gameWatch.Tick += async (_, _) =>
        {
            if (checking)
            {
                return;
            }

            checking = true;
            try
            {
                await RefreshGamePlayStateAsync();
            }
            finally
            {
                checking = false;
            }
        };
        _gameWatch.Start();
        _ = RefreshGamePlayStateAsync();
    }

    /// <summary>A failing process query counts as not running.</summary>
    private bool IsGameProcessRunning()
    {
        try
        {
            return _gameProcess?.IsRunning() == true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Brings Icarus's window to the front (restored if minimized). False when there's no window to show.</summary>
    private static class GameWindow
    {
        private const int SwRestore = 9;

        public static bool TryBringToFront()
        {
            var processes = Process.GetProcessesByName("Icarus-Win64-Shipping");
            try
            {
                foreach (var process in processes)
                {
                    var window = process.MainWindowHandle;
                    if (window == IntPtr.Zero)
                    {
                        continue;
                    }

                    if (IsIconic(window))
                    {
                        ShowWindow(window, SwRestore);
                    }

                    return SetForegroundWindow(window);
                }

                return false;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);
    }
}
