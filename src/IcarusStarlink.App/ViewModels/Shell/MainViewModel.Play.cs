using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;

namespace IcarusStarlink.App.ViewModels;

/// <summary>What the header's Play button shows: Play, Starting… after a click, Running while Icarus is open.</summary>
public enum GamePlayState
{
    Ready,
    Starting,
    Running,
}

/// <summary>
/// The header's Play button (the user's ask, 2026-10-30: "can we get an image change on play like
/// its running … because i could keep clicking it and it could prolly launch the game again").
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
    private Action<string> _openUrl = url => UrlOpener.TryOpen(url);
    private Func<bool> _focusGame = GameWindow.TryBringToFront;
    private DateTime _startingSince;
    private DispatcherTimer? _gameWatch;

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

    private void UseGameSeams(IGameProcessChecker? gameProcess, Func<DateTime>? clock, Action<string>? openUrl, Func<bool>? focusGame)
    {
        _gameProcess = gameProcess;
        _clock = clock ?? _clock;
        _openUrl = openUrl ?? _openUrl;
        _focusGame = focusGame ?? _focusGame;
    }

    /// <summary>
    /// Steam's own URI launch, not the exe directly: it respects the launch options and overlay set in
    /// Steam, the same as Play in the Steam library. 1149460 is Icarus's App ID. Never asks Steam
    /// twice: a click while Starting… does nothing, a click while Running brings the game forward.
    /// </summary>
    [RelayCommand]
    private void LaunchGame()
    {
        if (GamePlayState == GamePlayState.Starting && _clock() - _startingSince < StartingTimeout)
        {
            return;
        }

        if (GamePlayState == GamePlayState.Running || IsGameProcessRunning())
        {
            GamePlayState = GamePlayState.Running;
            _focusGame();
            return;
        }

        _openUrl("steam://run/1149460");
        _startingSince = _clock();
        GamePlayState = GamePlayState.Starting;
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

    /// <summary>Running wins; otherwise Starting… holds until the game appears or the timeout passes, then Play.</summary>
    internal void ApplyGameRunning(bool running)
    {
        if (running)
        {
            GamePlayState = GamePlayState.Running;
        }
        else if (GamePlayState != GamePlayState.Starting || _clock() - _startingSince >= StartingTimeout)
        {
            GamePlayState = GamePlayState.Ready;
        }
    }

    /// <summary>Watches for the game every 3 seconds while Starlink is open (App.OnStartup, after the window shows).</summary>
    public void StartGameWatch()
    {
        if (_gameProcess is null || _gameWatch is not null)
        {
            return;
        }

        _gameWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
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
