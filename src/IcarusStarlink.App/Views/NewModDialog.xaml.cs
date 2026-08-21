using System.Windows;

namespace IcarusStarlink.App.Views;

public partial class NewModDialog : Window
{
    public string ModName { get; private set; } = "";
    public string ModAuthor { get; private set; } = "";

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
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
