using System.Runtime.InteropServices;

namespace IcarusStarlink.Updater;

/// <summary>
/// A plain Win32 MessageBox via P/Invoke — this project is a lightweight console app (no WPF/
/// WinForms reference at all, deliberately: it's staged and launched as a throwaway TEMP copy on
/// every single update, so keeping it small matters more here than anywhere else in this solution),
/// so this is the smallest way to show a real, user-visible dialog for the one failure mode
/// (UpdateRollbackIncompleteException) that genuinely needs one. Every other outcome — success, or a
/// normal failure the rollback fully recovered from — stays silent by design (CreateNoWindow: true),
/// matching this updater's own existing "invisible unless something needs the user's attention" UX.
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
