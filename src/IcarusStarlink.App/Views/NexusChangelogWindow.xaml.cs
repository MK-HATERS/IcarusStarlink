using System.Windows;

namespace IcarusStarlink.App.Views;

/// <summary>One version's worth of changelog lines, in the display order NexusChangelogWindow shows them.</summary>
public sealed record ChangelogVersionEntry(string Version, IReadOnlyList<string> Lines);

/// <summary>Shows a mod's changelog history — Nexus's own real per-version changelog data (see INexusApiClient.GetChangelogsAsync), not this app's own commit history.</summary>
public partial class NexusChangelogWindow : Window
{
    public NexusChangelogWindow(string modName, IReadOnlyList<ChangelogVersionEntry> versions)
    {
        InitializeComponent();

        HeaderText.Text = $"'{modName}' — changelog";
        VersionsList.ItemsSource = versions;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
