using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

/// <summary>Find the broken mod's window (non-modal: the player goes to the game and back). FindBrokenModLauncher opens it.</summary>
public partial class FindBrokenModWindow : Window
{
    /// <summary>Set once the view model agreed to close (the mods are back, or the player chose Later).</summary>
    private bool _closeAgreed;

    public FindBrokenModWindow()
    {
        InitializeComponent();
        Utilities.WindowSizeMemory.Remember(this, "find-broken-mod");
    }

    public void Attach(FindBrokenModViewModel viewModel)
    {
        DataContext = viewModel;
        viewModel.Close = () =>
        {
            _closeAgreed = true;
            Close();
        };

        // Starlink itself closing takes this window with it (WPF ignores a cancel then): no
        // question; the search is saved and the next start offers to put the mods back.
        if (Owner is { } owner)
        {
            void OwnerClosing(object? sender, CancelEventArgs args) => _closeAgreed |= !args.Cancel;
            owner.Closing += OwnerClosing;
            Closed += (_, _) => owner.Closing -= OwnerClosing;
        }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closeAgreed || App.IsClosing || DataContext is not FindBrokenModViewModel viewModel || (!viewModel.HasOpenSearch && viewModel.IsIdle))
        {
            return;
        }

        // Asked after the close is held: the question (and putting the mods back) can take a while.
        e.Cancel = true;
        if (await viewModel.CanCloseAsync())
        {
            _closeAgreed = true;
            Close();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void MatchList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is FindBrokenModViewModel viewModel && MatchList.SelectedItem is GameDataItemRef row)
        {
            viewModel.PickRowCommand.Execute(row);
        }
    }
}
