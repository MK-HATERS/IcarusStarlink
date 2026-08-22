using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using IcarusStarlink.App.Converters;

namespace IcarusStarlink.App.Views;

/// <summary>
/// Shown once after a detected app-version change (SettingsViewModel.CheckForAppUpdatesOnLaunchAsync)
/// — reuses the same GitHub release notes already fetched for the "update available?" prompt, and
/// the same Markdown renderer Help's own topic pane uses, rather than a second rendering path.
/// </summary>
public partial class WhatsNewWindow : Window
{
    public WhatsNewWindow(string version, string releaseNotesMarkdown)
    {
        InitializeComponent();
        HeaderText.Text = $"What's new in v{version}";
        NotesViewer.Document = (FlowDocument)new MarkdownToFlowDocumentConverter()
            .Convert(releaseNotesMarkdown, typeof(FlowDocument), null, CultureInfo.InvariantCulture)!;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
