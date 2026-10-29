using System.IO;
using System.Windows;

namespace IcarusStarlink.App.Views;

public partial class TextFileViewerWindow : Window
{
    public TextFileViewerWindow(string title, string filePath)
    {
        InitializeComponent();
        Title = title;
        ContentTextBox.Text = File.ReadAllText(filePath);
    }
}
