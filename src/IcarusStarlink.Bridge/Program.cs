using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace IcarusStarlink.Bridge;

/// <summary>Starts IcarusStarlink.exe from this folder with this exe's own arguments (an nxm:// link), then exits.</summary>
internal static class Program
{
    private const string AppExeName = "IcarusStarlink.exe";

    [STAThread]
    private static int Main()
    {
        var folder = AppDomain.CurrentDomain.BaseDirectory;
        var appExe = Path.Combine(folder, AppExeName);
        if (!File.Exists(appExe))
        {
            MessageBoxW(IntPtr.Zero, $"{AppExeName} isn't in {folder}.\n\nIcarus Starlink is now started from {AppExeName}; download it again if it's missing.",
                "Icarus Starlink", 0x10);
            return 1;
        }

        try
        {
            // Passed on exactly as typed: everything after this exe's own path.
            Process.Start(new ProcessStartInfo(appExe, ArgumentsAfterExe(Environment.CommandLine))
            {
                WorkingDirectory = folder,
                UseShellExecute = false,
            });
            return 0;
        }
        catch (Exception ex)
        {
            MessageBoxW(IntPtr.Zero, $"Couldn't start {appExe}:\n\n{ex.Message}", "Icarus Starlink", 0x10);
            return 1;
        }
    }

    private static string ArgumentsAfterExe(string commandLine)
    {
        var text = commandLine.TrimStart();
        if (text.StartsWith("\"", StringComparison.Ordinal))
        {
            var end = text.IndexOf('"', 1);
            return end < 0 ? "" : text.Substring(end + 1).Trim();
        }

        var space = text.IndexOf(' ');
        return space < 0 ? "" : text.Substring(space + 1).Trim();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
