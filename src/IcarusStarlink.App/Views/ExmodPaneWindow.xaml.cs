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

        // See ScrollSyncHelper's own doc comment — keeps this pane's scroll position in step with
        // the main editor window (or another pane) if it's showing the same fixed view mode.
        ScrollSyncHelper.Attach(ItemFieldsScrollViewer, ItemFieldsScrollViewer.ScrollToVerticalOffset,
            viewModel, nameof(ExmodEditorViewModel.ItemFieldsScrollOffset), () => viewModel.ItemFieldsScrollOffset, v => viewModel.ItemFieldsScrollOffset = v, this);
        ScrollSyncHelper.Attach(FileJsonTextBox, FileJsonTextBox.ScrollToVerticalOffset,
            viewModel, nameof(ExmodEditorViewModel.FileJsonScrollOffset), () => viewModel.FileJsonScrollOffset, v => viewModel.FileJsonScrollOffset = v, this);
        ScrollSyncHelper.Attach(FullExmodJsonTextBox, FullExmodJsonTextBox.ScrollToVerticalOffset,
            viewModel, nameof(ExmodEditorViewModel.FullExmodJsonScrollOffset), () => viewModel.FullExmodJsonScrollOffset, v => viewModel.FullExmodJsonScrollOffset = v, this);
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
