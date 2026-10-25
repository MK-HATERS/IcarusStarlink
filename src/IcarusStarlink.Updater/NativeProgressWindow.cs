using System.Runtime.InteropServices;

namespace IcarusStarlink.Updater;

/// <summary>
/// A small "Installing Icarus Starlink…" window with a moving bar, shown while the updater works
/// (user request 2026-10-24: once Starlink closed, nothing was on screen until it came back). Plain
/// Win32 like NativeMessageBox (this exe has no WPF or WinForms, and is trimmed); it runs its own
/// message loop on a background thread. Best-effort: if it can't be created the update goes on
/// without it.
/// </summary>
internal sealed class NativeProgressWindow : IDisposable
{
    private const int WS_OVERLAPPED = 0x00000000, WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000, WS_VISIBLE = 0x10000000, WS_CHILD = 0x40000000;
    private const int WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080;
    private const int PBS_MARQUEE = 0x08, PBM_SETMARQUEE = 0x0400 + 10;
    private const int WM_SETFONT = 0x0030, WM_CLOSE = 0x0010, WM_DESTROY = 0x0002, WM_CTLCOLORSTATIC = 0x0138;
    private const int SW_SHOW = 5, COLOR_WINDOW = 5;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private nint _window;
    private nint _text;
    private WndProc? _wndProc;

    public NativeProgressWindow(string title, string text)
    {
        _thread = new Thread(() => Run(title, text)) { IsBackground = true, Name = "Updater window" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(3));
    }

    /// <summary>Changes the line under the title ("Copying the new files…").</summary>
    public void SetText(string text)
    {
        if (_text != 0)
        {
            SetWindowTextW(_text, text);
        }
    }

    public void Dispose()
    {
        if (_window != 0)
        {
            PostMessageW(_window, WM_CLOSE, 0, 0);
        }
    }

    private void Run(string title, string text)
    {
        try
        {
            InitCommonControls();
            var instance = GetModuleHandleW(null);
            _wndProc = (hwnd, message, wParam, lParam) =>
            {
                if (message == WM_DESTROY)
                {
                    PostQuitMessage(0);
                    return 0;
                }

                // White behind the text, like the window.
                return message == WM_CTLCOLORSTATIC ? GetSysColorBrush(COLOR_WINDOW) : DefWindowProcW(hwnd, message, wParam, lParam);
            };

            var windowClass = new WNDCLASSEXW
            {
                cbSize = Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = instance,
                hbrBackground = GetSysColorBrush(COLOR_WINDOW),
                lpszClassName = "IcarusStarlinkUpdaterWindow",
                hCursor = LoadCursorW(0, 32512),
            };
            RegisterClassExW(ref windowClass);

            const int width = 420, height = 150;
            var x = (GetSystemMetrics(0) - width) / 2;
            var y = (GetSystemMetrics(1) - height) / 2;
            _window = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, windowClass.lpszClassName, title,
                WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU, x, y, width, height, 0, 0, instance, 0);
            if (_window == 0)
            {
                return;
            }

            var font = CreateFontW(-15, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            _text = CreateWindowExW(0, "STATIC", text, WS_CHILD | WS_VISIBLE, 20, 18, width - 50, 40, _window, 0, instance, 0);
            SendMessageW(_text, WM_SETFONT, font, 1);
            var bar = CreateWindowExW(0, "msctls_progress32", "", WS_CHILD | WS_VISIBLE | PBS_MARQUEE, 20, 66, width - 58, 16, _window, 0, instance, 0);
            SendMessageW(bar, PBM_SETMARQUEE, 1, 30);

            ShowWindow(_window, SW_SHOW);
            SetForegroundWindow(_window);
            _ready.Set();

            while (GetMessageW(out var msg, 0, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }
        catch (Exception)
        {
            // No window: the update itself doesn't need one.
        }
        finally
        {
            _window = 0;
            _text = 0;
            _ready.Set();
        }
    }

    private delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public int cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("comctl32.dll")] private static extern void InitCommonControls();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowTextW(nint hwnd, string text);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint GetSysColorBrush(int index);
    [DllImport("user32.dll")] private static extern nint LoadCursorW(nint instance, int cursor);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut,
        uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
}
