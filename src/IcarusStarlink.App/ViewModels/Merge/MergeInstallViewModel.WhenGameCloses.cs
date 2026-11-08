using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// "Install when Icarus closes": offered when Install was refused because the game runs and a
/// whole build is staged. It never copies on its own: when the game closes it runs Copy to game,
/// which asks its usual question and checks the game again. Dropped when the staged pak changes
/// meanwhile (a new build starts, or Copy to game stops being offered).
/// </summary>
public sealed partial class MergeInstallViewModel
{
    /// <summary>Install was just refused because Icarus runs, with a whole build staged: the button that waits for the game to close shows.</summary>
    [ObservableProperty]
    private bool _canInstallWhenGameCloses;

    /// <summary>The player asked to install the staged build when Icarus closes.</summary>
    [ObservableProperty]
    private bool _isWaitingForGameToClose;

    /// <summary>Called where Install says "Icarus is running" (not in a Find the broken mod round, which has its own window).</summary>
    private void OfferInstallWhenGameCloses() => CanInstallWhenGameCloses = CanCopyToGame && !IsWaitingForGameToClose;

    [RelayCommand]
    private void InstallWhenGameCloses()
    {
        if (!CanCopyToGame)
        {
            CanInstallWhenGameCloses = false;
            return;
        }

        IsWaitingForGameToClose = true;
        CanInstallWhenGameCloses = false;
        InstallStatusMessage = "Waiting: when Icarus closes, Starlink asks before it installs the pack you built.";
    }

    [RelayCommand]
    private void StopWaitingForGameToClose()
    {
        if (!IsWaitingForGameToClose)
        {
            return;
        }

        IsWaitingForGameToClose = false;
        CanInstallWhenGameCloses = CanCopyToGame;
        InstallStatusMessage = "Not waiting for Icarus to close any more. Nothing was installed.";
    }

    /// <summary>The header's game watch saw Icarus close: a waiting install goes ahead through Copy to game (its question, its checks).</summary>
    internal async Task OnGameClosedAsync()
    {
        if (!IsWaitingForGameToClose)
        {
            return;
        }

        IsWaitingForGameToClose = false;
        if (!CanCopyToGame)
        {
            InstallStatusMessage = "Icarus closed, but the pack you built changed since, so nothing was installed. Click Install to build and install again.";
            return;
        }

        try
        {
            await CopyToGameAsync();
        }
        catch (Exception ex)
        {
            // Started from the game watch, with no button click to report to.
            InstallStatusMessage = $"Couldn't install after Icarus closed: {ex.Message} Click Copy to game to try again.";
        }
    }

    partial void OnCanCopyToGameChanged(bool value)
    {
        if (!value)
        {
            CanInstallWhenGameCloses = false;
            IsWaitingForGameToClose = false;
        }
    }

    partial void OnIsRebuildingChanged(bool value)
    {
        // A new build rewrites the staged pak: the one the player asked to install is gone.
        if (value)
        {
            CanInstallWhenGameCloses = false;
            IsWaitingForGameToClose = false;
        }
    }
}
