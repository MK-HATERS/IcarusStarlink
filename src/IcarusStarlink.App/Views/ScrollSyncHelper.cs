using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace IcarusStarlink.App.Views;

/// <summary>
/// Keeps one ScrollViewer (or a multi-line TextBox's own internal one — a TextBox routes
/// ScrollViewer.ScrollChangedEvent through itself via its own control template, so attaching the
/// same handler works for either) in sync with a shared scroll-offset property on the editor's
/// ViewModel. Used so the same EXMOD editor view (Item fields / File JSON / Full EXMOD JSON)
/// scrolls together when it's open in two places at once — the main editor window and a popped-out
/// pane (ExmodEditorViewModel.PopOutCurrentView), or two popped-out panes fixed to the same mode.
/// Both directions go through the same property; a re-entrancy guard stops the ViewModel-driven
/// ScrollToVerticalOffset call from itself re-raising ScrollChanged and bouncing straight back.
/// </summary>
public static class ScrollSyncHelper
{
    /// <summary>Call once per synced region, after InitializeComponent and after DataContext is set. Unhooks itself when owner closes — the ViewModel (shared across every open window for this mod) outlives any one of them.</summary>
    public static void Attach(
        UIElement scrollHost, Action<double> scrollToLocalOffset,
        INotifyPropertyChanged viewModel, string propertyName, Func<double> getSharedOffset, Action<double> setSharedOffset,
        Window owner)
    {
        var isApplyingRemoteOffset = false;

        void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (isApplyingRemoteOffset)
            {
                return;
            }

            setSharedOffset(e.VerticalOffset);
        }

        void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != propertyName)
            {
                return;
            }

            isApplyingRemoteOffset = true;
            try
            {
                scrollToLocalOffset(getSharedOffset());
            }
            finally
            {
                isApplyingRemoteOffset = false;
            }
        }

        scrollHost.AddHandler(ScrollViewer.ScrollChangedEvent, (ScrollChangedEventHandler)OnScrollChanged);
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        owner.Closed += (_, _) => viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }
}
