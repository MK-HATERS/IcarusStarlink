using System.Windows;
using IcarusStarlink.Core.Library;

namespace IcarusStarlink.App.Views;

public partial class NewModDialog : Window
{
    public string ModName { get; private set; } = "";
    public string ModAuthor { get; private set; } = "";
    public ModTemplate SelectedTemplate { get; private set; } = ModTemplate.Blank;

    /// <summary>The version typed in the Version box; 1.0 when it was left empty.</summary>
    public string ModVersion { get; private set; } = "1.0";

    public NewModDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => NameBox.Focus();
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text) || string.IsNullOrWhiteSpace(AuthorBox.Text))
        {
            ErrorText.Text = "Name and author are both required.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        // The name becomes the mod's file name: refused here in plain words, not later as an EXMOD error.
        if (ViewModels.GameEditorViewModel.NewModNameProblem(NameBox.Text) is { } problem)
        {
            ErrorText.Text = problem + " Change the name.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        ModName = NameBox.Text.Trim();
        ModAuthor = AuthorBox.Text.Trim();
        ModVersion = string.IsNullOrWhiteSpace(VersionBox.Text) ? "1.0" : VersionBox.Text.Trim();
        SelectedTemplate = CraftableRadio.IsChecked == true ? ModTemplate.CraftableOrDeployableItem
            : ConsumableRadio.IsChecked == true ? ModTemplate.ConsumableItem
            : BuildingPieceRadio.IsChecked == true ? ModTemplate.BuildingPiece
            : ElectricGeneratorRadio.IsChecked == true ? ModTemplate.ElectricGenerator
            : WaterPumpRadio.IsChecked == true ? ModTemplate.WaterPump
            : QualityOfLifeRadio.IsChecked == true ? ModTemplate.QualityOfLife
            : EnvirosuitRadio.IsChecked == true ? ModTemplate.Envirosuit
            : ModuleRadio.IsChecked == true ? ModTemplate.EnvirosuitModule
            : ConsumableBuffRadio.IsChecked == true ? ModTemplate.ConsumableWithBuff
            : WorkshopItemRadio.IsChecked == true ? ModTemplate.WorkshopItem
            : MapIconRadio.IsChecked == true ? ModTemplate.MapIcon
            : ModTemplate.Blank;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
