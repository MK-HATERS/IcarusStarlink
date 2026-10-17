using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

public partial class ToastHostView : UserControl
{
    public ToastHostView()
    {
        InitializeComponent();
    }

    // Hover keeps a card on screen (its countdown pauses) so a long message can be read through.
    private void OnCardMouseEnter(object sender, MouseEventArgs e) => SetPaused(sender, true);

    private void OnCardMouseLeave(object sender, MouseEventArgs e) => SetPaused(sender, false);

    private static void SetPaused(object sender, bool paused)
    {
        if (sender is FrameworkElement { DataContext: ToastItem toast })
        {
            toast.IsPaused = paused;
        }
    }
}
