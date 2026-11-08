using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace IcarusStarlink.App.Services;

/// <summary>
/// The main window's side of server hosting: flashes the taskbar button when a server needs the
/// host, and keeps Starlink as an icon by the clock while the window is hidden (Open and Quit on
/// its menu). The icon exists only while the window is hidden. Every call is best-effort: a
/// failure here never stops Starlink closing or a server being watched.
/// </summary>
public sealed class HostTray : IHostNotifier, IDisposable
{
    private const int TrayId = 1;
    private const int TrayCallbackMessage = 0x8000 + 0x51; // WM_APP + 0x51
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;

    private static readonly int TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

    private Window? _window;
    private HwndSource? _source;
    private IntPtr _icon;
    private bool _ownsIcon;
    private Func<bool>? _serversNeedStarlink;

    /// <summary>Open on the icon's menu, or a click on the icon.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>Quit on the icon's menu.</summary>
    public event EventHandler? QuitRequested;

    /// <summary>The window is hidden and the icon is by the clock.</summary>
    public bool IsInTray { get; private set; }

    /// <summary>The window this flashes and hides. Set once, by the main window.</summary>
    public void Attach(Window window) => _window = window;

    public void WatchServers(Func<bool> serversNeedStarlink) => _serversNeedStarlink = serversNeedStarlink;

    /// <summary>A server runs or has automation on. False when the Server page was never built (no servers). Never throws.</summary>
    public bool ServersNeedStarlink()
    {
        try
        {
            return _serversNeedStarlink?.Invoke() ?? false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void NeedsAttention(string message)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (IsInTray)
                {
                    ShowNote("A server needs you", message);
                }
                else if (_window is { IsActive: false } window && new WindowInteropHelper(window).Handle is var hwnd && hwnd != IntPtr.Zero)
                {
                    // Flashes until the window comes to the front.
                    var info = new FlashWindowInfo
                    {
                        Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
                        Window = hwnd,
                        Flags = FlashAll | FlashUntilForeground,
                        Count = uint.MaxValue,
                        Timeout = 0,
                    };
                    FlashWindowEx(ref info);
                }
            }
            catch (Exception)
            {
                // Best-effort: the activity log and the server's Recent events still have it.
            }
        });
    }

    /// <summary>Hides the window and puts the icon by the clock. The first note says how to get back and how to quit.</summary>
    public void HideToTray()
    {
        if (_window is null)
        {
            return;
        }

        var shown = AddIcon();
        _window.Hide();
        IsInTray = true;
        if (shown)
        {
            ShowNote("Starlink is still running", "Your servers are still watched. Click this icon to open Starlink, or right-click it to quit.");
        }
        else
        {
            // No icon (Explorer not running): the window can't be hidden with no way back.
            ShowFromTray();
        }
    }

    /// <summary>Brings the hidden window back and removes the icon. Safe to call when it isn't hidden.</summary>
    public void ShowFromTray()
    {
        RemoveIcon();
        IsInTray = false;
        if (_window is null)
        {
            return;
        }

        if (!_window.IsVisible)
        {
            _window.Show();
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    public void Dispose()
    {
        RemoveIcon();
        _source?.RemoveHook(WndProc);
        _source = null;
        if (_ownsIcon && _icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
        }

        _icon = IntPtr.Zero;
    }

    private bool AddIcon()
    {
        try
        {
            if (_window is null)
            {
                return false;
            }

            var hwnd = new WindowInteropHelper(_window).EnsureHandle();
            if (_source is null && HwndSource.FromHwnd(hwnd) is { } source)
            {
                _source = source;
                _source.AddHook(WndProc);
            }

            if (_icon == IntPtr.Zero)
            {
                LoadAppIcon();
            }

            var data = NewData(hwnd);
            data.Flags = IconFlagMessage | IconFlagIcon | IconFlagTip;
            data.CallbackMessage = TrayCallbackMessage;
            data.Icon = _icon;
            data.Tip = "Icarus Starlink: watching your servers";
            return Shell_NotifyIcon(NotifyAdd, ref data) || Shell_NotifyIcon(NotifyModify, ref data);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void RemoveIcon()
    {
        if (!IsInTray || _window is null)
        {
            return;
        }

        try
        {
            var data = NewData(new WindowInteropHelper(_window).Handle);
            Shell_NotifyIcon(NotifyDelete, ref data);
        }
        catch (Exception)
        {
            // Windows removes it when the process ends.
        }
    }

    private void ShowNote(string title, string text)
    {
        if (_window is null)
        {
            return;
        }

        try
        {
            var data = NewData(new WindowInteropHelper(_window).Handle);
            data.Flags = IconFlagInfo;
            data.InfoTitle = title.Length > 63 ? title[..63] : title;
            data.Info = text.Length > 255 ? text[..252] + "..." : text;
            data.InfoFlags = InfoWarning;
            Shell_NotifyIcon(NotifyModify, ref data);
        }
        catch (Exception)
        {
            // Best-effort.
        }
    }

    private void LoadAppIcon()
    {
        _ownsIcon = false;
        if (Environment.ProcessPath is { } exe && ExtractIconEx(exe, 0, IntPtr.Zero, out var small, 1) > 0 && small != IntPtr.Zero)
        {
            _icon = small;
            _ownsIcon = true;
            return;
        }

        _icon = LoadIcon(IntPtr.Zero, new IntPtr(32512)); // the standard program icon
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == TrayCallbackMessage)
        {
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case WmLButtonUp:
                case WmLButtonDblClk:
                    handled = true;
                    OpenRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case WmRButtonUp:
                case WmContextMenu:
                    handled = true;
                    ShowMenu();
                    break;
            }
        }
        else if (msg == TaskbarCreatedMessage && IsInTray)
        {
            // Explorer restarted and lost every tray icon: put this one back.
            AddIcon();
        }

        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        menu.Items.Add(MenuItem("Open Starlink", "Shows the Starlink window again.", () => OpenRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Quit Starlink", "Closes Starlink. Server automation stops; running servers keep running.", () => QuitRequested?.Invoke(this, EventArgs.Empty)));
        menu.Opened += (_, _) =>
        {
            // A menu from the tray closes on a click elsewhere only when it is the window in front.
            if (PresentationSource.FromVisual(menu) is HwndSource source)
            {
                SetForegroundWindow(source.Handle);
            }
        };
        menu.IsOpen = true;
    }

    private static MenuItem MenuItem(string header, string tip, Action click)
    {
        var item = new MenuItem { Header = header, ToolTip = tip };
        item.Click += (_, _) => click();
        return item;
    }

    private static NotifyIconData NewData(IntPtr hwnd) => new()
    {
        Size = Marshal.SizeOf<NotifyIconData>(),
        Window = hwnd,
        Id = TrayId,
        Tip = string.Empty,
        Info = string.Empty,
        InfoTitle = string.Empty,
    };

    private const int NotifyAdd = 0;
    private const int NotifyModify = 1;
    private const int NotifyDelete = 2;
    private const int IconFlagMessage = 0x01;
    private const int IconFlagIcon = 0x02;
    private const int IconFlagTip = 0x04;
    private const int IconFlagInfo = 0x10;
    private const int InfoWarning = 0x02;
    private const uint FlashAll = 0x03;
    private const uint FlashUntilForeground = 0x0C;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public IntPtr Window;
        public int Id;
        public int Flags;
        public int CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public int State;
        public int StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public int TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public int InfoFlags;
        public Guid Item;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW")]
    private static extern uint ExtractIconEx(string file, int index, IntPtr large, out IntPtr small, uint count);

    [DllImport("user32.dll", EntryPoint = "LoadIconW")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    private static extern int RegisterWindowMessage(string name);
}
