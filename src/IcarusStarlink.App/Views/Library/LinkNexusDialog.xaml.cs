using System.Windows;
using IcarusStarlink.Core.Nexus;

namespace IcarusStarlink.App.Views;

public partial class LinkNexusDialog : Window
{
    public int NexusModId { get; private set; }

    public LinkNexusDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => IdBox.Focus();
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (!NexusModWebUrl.TryParseModId(IdBox.Text, out var modId))
        {
            ErrorText.Text = "Enter a numeric Nexus mod ID, or paste a nexusmods.com/icarus/mods/<id> URL.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        NexusModId = modId;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
