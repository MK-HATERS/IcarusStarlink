using System.Windows;
using System.Windows.Controls;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

public partial class WorldSettingsView : UserControl
{
    public WorldSettingsView()
    {
        InitializeComponent();
    }

    /// <summary>Typing in a number box ended: it shows the value Save writes.</summary>
    private void OnNumberBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WorldSettingRowViewModel row)
        {
            row.EndTyping();
        }
    }
}
