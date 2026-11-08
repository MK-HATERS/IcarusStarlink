using System.Runtime.InteropServices;

namespace IcarusStarlink.Updater;

/// <summary>
/// A plain Win32 message box (this exe is a small Native AOT program with no WPF or WinForms).
/// Used only for RollbackIncomplete, the one outcome that can leave a broken install; every other
/// outcome is reported by the relaunched app.
/// </summary>
internal static class NativeMessageBox
{
    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONERROR = 0x00000010;
    private const uint MB_TOPMOST = 0x00040000;
    private const uint MB_SETFOREGROUND = 0x00010000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    public static void Show(string text, string caption) =>
        MessageBoxW(0, text, caption, MB_OK | MB_ICONERROR | MB_TOPMOST | MB_SETFOREGROUND);
}
