using System.Runtime.InteropServices;

namespace IcarusStarlink.App.Services;

/// <summary>IShortcutEditor over Windows' own WScript.Shell, the documented way to read and save .lnk files without a shell-link interop layer.</summary>
public sealed class WindowsShortcutEditor : IShortcutEditor
{
    public string? TryReadTarget(string shortcutPath)
    {
        try
        {
            return With(shortcutPath, link => (string?)link.TargetPath);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Repoint(string shortcutPath, string oldTarget, string newTarget) => With<object?>(shortcutPath, link =>
    {
        link.TargetPath = newTarget;

        // "C:\…\IcarusStarlink.App.exe,0": the icon came from the old exe, which is about to go.
        if (((string?)link.IconLocation ?? "").StartsWith(oldTarget, StringComparison.OrdinalIgnoreCase))
        {
            link.IconLocation = $"{newTarget},0";
        }

        link.Save();
        return null;
    });

    private static T With<T>(string shortcutPath, Func<dynamic, T> use)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new COMException("WScript.Shell isn't available.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic? link = null;
        try
        {
            link = shell.CreateShortcut(shortcutPath);
            return use(link);
        }
        finally
        {
            if (link is not null)
            {
                Marshal.FinalReleaseComObject(link);
            }

            Marshal.FinalReleaseComObject(shell);
        }
    }
}
