using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.App.Views;

namespace IcarusStarlink.App.Services;

/// <summary>
/// Opens Find the broken mod (one window at a time) from Merge &amp; Install's More ▾ menu
/// (OpenFindBrokenModMessage), and at start offers to put the mods back when a search didn't
/// finish last time (the app closed mid-search, so the game may still hold only some of them).
/// </summary>
public sealed class FindBrokenModLauncher
{
    private readonly Func<FindBrokenModContext> _context;
    private FindBrokenModWindow? _window;

    public FindBrokenModLauncher(Func<FindBrokenModContext> context)
    {
        _context = context;
        WeakReferenceMessenger.Default.Register<OpenFindBrokenModMessage>(this, (recipient, _) => ((FindBrokenModLauncher)recipient).Open());
    }

    /// <summary>Opens the window: a saved search goes on where it was. <paramref name="putBackNow"/> starts putting the mods back at once.</summary>
    public void Open(bool putBackNow = false)
    {
        if (_window is { } open)
        {
            open.Activate();
            return;
        }

        var context = _context();
        var viewModel = new FindBrokenModViewModel(context, context.Store.Load());
        var window = new FindBrokenModWindow { Owner = Application.Current.MainWindow };
        window.Attach(viewModel);
        window.Closed += (_, _) => _window = null;
        _window = window;
        window.Show();
        if (putBackNow)
        {
            viewModel.PutModsBackCommand.Execute(null);
        }
    }

    /// <summary>At start: a search saved by an earlier run asks to put the mods back now, or go on with it (closing the question asks again next start).</summary>
    public async Task OfferPutBackAtStartupAsync()
    {
        var context = _context();
        if (context.Store.Load() is null)
        {
            return;
        }

        // After any other startup window, as the exe move-over prompt does.
        while (!App.IsClosing && Application.Current?.Windows.OfType<Window>().Any(w => w != Application.Current.MainWindow && w.IsVisible) == true)
        {
            await Task.Delay(500);
        }

        if (App.IsClosing || _window is not null)
        {
            return;
        }

        var answer = context.Dialogs.ConfirmWithCancel(
            "Find the broken mod didn't finish last time, so the game may still have only some of your mods in it.\n\n"
            + "Put all your mods back now, or go on with the search?",
            "Put your mods back", ThemedConfirmSeverity.Warning, "Put my mods back", "Go on with the search");
        if (answer is not null)
        {
            Open(putBackNow: answer == true);
        }
    }
}
