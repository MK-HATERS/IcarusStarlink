using System.Windows;

namespace IcarusStarlink.App.Services;

/// <summary>
/// One startup window at a time: "What's new" (after a network check) and the exe move-over
/// window (at idle) used to open on top of each other right after an update. Each waits here for
/// the one before to close.
/// </summary>
public static class StartupPrompts
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task ShowAsync(Func<Window> create)
    {
        await Gate.WaitAsync();
        try
        {
            // Starlink closed while a network check was out (closed right after launch): opening a
            // window now throws "The Application object is being shut down" (2026-10-26).
            if (App.IsClosing)
            {
                return;
            }

            create().ShowDialog();
        }
        finally
        {
            Gate.Release();
        }
    }
}
