using System.Windows;
using IcarusStarlink.Core.Library;

namespace IcarusStarlink.App.Views;

public partial class NewModDialog : Window
{
    public string ModName { get; private set; } = "";
    public string ModAuthor { get; private set; } = "";
    public ModTemplate SelectedTemplate { get; private set; } = ModTemplate.Blank;

    public NewModDialog()
    {
        InitializeComponent();
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text) || string.IsNullOrWhiteSpace(AuthorBox.Text))
        {
            ErrorText.Text = "Name and author are both required.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        ModName = NameBox.Text.Trim();
        ModAuthor = AuthorBox.Text.Trim();
        SelectedTemplate = CraftableRadio.IsChecked == true ? ModTemplate.CraftableOrDeployableItem
            : ConsumableRadio.IsChecked == true ? ModTemplate.ConsumableItem
            : BuildingPieceRadio.IsChecked == true ? ModTemplate.BuildingPiece
            : ElectricGeneratorRadio.IsChecked == true ? ModTemplate.ElectricGenerator
            : WaterPumpRadio.IsChecked == true ? ModTemplate.WaterPump
            : ModTemplate.Blank;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
