using System.Windows;

namespace IcarusStarlink.App.Views;

public partial class SplashWindow : Window
{
    public SplashWindow() => InitializeComponent();

    /// <summary>Safe to call from any thread — marshals to the UI thread itself, since the real startup work this window covers for runs in the background.</summary>
    public void UpdateStatus(string text) => Dispatcher.Invoke(() => StatusText.Text = text);
}
