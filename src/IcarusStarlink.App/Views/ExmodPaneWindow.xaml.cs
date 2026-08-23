using System.Windows;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

public partial class ExmodPaneWindow : Window
{
    /// <summary>Fixed for this window's whole lifetime — which of the three views it shows. Read by the XAML's own Visibility bindings via RelativeSource=Window, since DataContext is the shared ExmodEditorViewModel, not this window.</summary>
    public ExmodEditorViewMode FixedViewMode { get; }

    public ExmodPaneWindow(ExmodEditorViewModel viewModel, ExmodEditorViewMode fixedMode)
    {
        FixedViewMode = fixedMode;
        InitializeComponent();
        DataContext = viewModel;
        PaneTitle = $"{viewModel.WindowTitle} — {PaneLabel(fixedMode)}";
    }

    public string PaneTitle { get; }

    private static string PaneLabel(ExmodEditorViewMode mode) => mode switch
    {
        ExmodEditorViewMode.ItemFields => "Item fields",
        ExmodEditorViewMode.FileJson => "File JSON",
        ExmodEditorViewMode.FullExmodJson => "Full EXMOD JSON",
        _ => mode.ToString(),
    };
}
