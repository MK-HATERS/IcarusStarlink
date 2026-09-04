using System.Diagnostics;

namespace IcarusStarlink.App.Services;

/// <summary>Real, production implementation of IGameProcessChecker — matches the game's own real process name, "Icarus-Win64-Shipping" (confirmed against a live install), unchanged from what SavesViewModel checked directly before this seam existed.</summary>
public sealed class GameProcessChecker : IGameProcessChecker
{
    public bool IsRunning() => Process.GetProcessesByName("Icarus-Win64-Shipping").Length > 0;
}
