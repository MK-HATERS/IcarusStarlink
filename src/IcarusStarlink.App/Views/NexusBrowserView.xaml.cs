using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using IcarusStarlink.App.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace IcarusStarlink.App.Views;

public partial class NexusBrowserView : UserControl
{
    // The officially Microsoft-documented "redirect your end users here" page for the WebView2
    // Runtime, per https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution —
    // deliberately not a direct .exe link, which isn't guaranteed stable.
    private const string RuntimeDownloadPage = "https://developer.microsoft.com/microsoft-edge/webview2/consumer/";

    public NexusBrowserView()
    {
        InitializeComponent();
        Loaded += NexusBrowserView_Loaded;
    }

    private async void NexusBrowserView_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= NexusBrowserView_Loaded;

        // Per Microsoft's own documented "Approach 2" for detecting the WebView2 Runtime: a null
        // (or an exception — observed to vary by SDK version) means the Runtime genuinely isn't
        // installed, not just "this specific call failed" — this app's own machine confirmed
        // to have it during Phase 8.3 planning, so this fallback path itself is unverified live,
        // built directly from Microsoft's own documented behavior instead.
        string? availableVersion;
        try
        {
            availableVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            availableVersion = null;
        }

        if (string.IsNullOrEmpty(availableVersion))
        {
            Browser.Visibility = Visibility.Collapsed;
            RuntimeMissingPanel.Visibility = Visibility.Visible;
            return;
        }

        if (DataContext is not NexusBrowserViewModel viewModel)
        {
            return;
        }

        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: viewModel.UserDataFolder);
            await Browser.EnsureCoreWebView2Async(environment);

            Browser.CoreWebView2.SourceChanged += (_, _) => UpdateChrome();
            Browser.CoreWebView2.HistoryChanged += (_, _) => UpdateChrome();
            Browser.Source = new Uri(NexusBrowserViewModel.HomeUrl);
        }
        catch (Exception)
        {
            // Same fallback as a genuinely missing Runtime — a corrupt install or a permissions
            // issue on the user-data folder both leave the browser unusable either way, and the
            // same "get the Runtime" guidance is the most useful next step to show either way.
            Browser.Visibility = Visibility.Collapsed;
            RuntimeMissingPanel.Visibility = Visibility.Visible;
        }
    }

    private void UpdateChrome()
    {
        UrlText.Text = Browser.Source?.ToString() ?? "";
        BackButton.IsEnabled = Browser.CoreWebView2?.CanGoBack ?? false;
        ForwardButton.IsEnabled = Browser.CoreWebView2?.CanGoForward ?? false;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2?.CanGoBack == true)
        {
            Browser.CoreWebView2.GoBack();
        }
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2?.CanGoForward == true)
        {
            Browser.CoreWebView2.GoForward();
        }
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.Reload();

    private void HomeButton_Click(object sender, RoutedEventArgs e) => Browser.Source = new Uri(NexusBrowserViewModel.HomeUrl);

    private void GetRuntimeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(RuntimeDownloadPage) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Best-effort UX, not a core operation — same swallow-and-move-on convention already
            // used everywhere else in this app for opening an external link.
        }
    }
}
