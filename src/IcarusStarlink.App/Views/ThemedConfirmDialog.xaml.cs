using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace IcarusStarlink.App.Views;

public enum ThemedConfirmSeverity { Question, Warning, Information }

/// <summary>
/// This app's own themed replacement for MessageBox.Show(message, title, MessageBoxButton.YesNo,
/// icon) — every one of the 21 real confirmation dialogs across this app used exactly that shape
/// (verified directly, not assumed), so the static Show() below only needs to support Yes/No, not
/// MessageBox's full button/result surface. Returns a plain bool (true = Yes) rather than
/// MessageBoxResult, since every call site only ever compared against Yes/No anyway.
/// </summary>
public partial class ThemedConfirmDialog : Window
{
    private bool _result;

    private ThemedConfirmDialog(Window? owner, string message, string title, ThemedConfirmSeverity severity)
    {
        InitializeComponent();
        Owner = owner;
        Title = title;
        TitleText.Text = title;
        MessageTextBlock.Text = message;

        var (geometryKey, brushKey) = severity switch
        {
            ThemedConfirmSeverity.Warning => ("IconWarning", "DangerBrush"),
            ThemedConfirmSeverity.Information => ("IconInfo", "InfoBrush"),
            // Question carries no icon — this app has no dedicated question-mark glyph, and a
            // reused Info/Warning icon would misrepresent a routine confirmation as one of those.
            _ => (null, "AccentBrush"),
        };

        var color = (Brush)FindResource(brushKey);
        TitleText.Foreground = color;
        if (geometryKey is not null)
        {
            SeverityIcon.Data = (Geometry)FindResource(geometryKey);
            SeverityIcon.Stroke = color;
        }

        // Warning defaults focus to the safe answer; a routine Question/Information confirmation
        // defaults to Yes, matching MessageBox's own first-button-focused behavior.
        Loaded += (_, _) => (severity == ThemedConfirmSeverity.Warning ? NoButton : YesButton).Focus();
    }

    /// <summary>Modal, matching MessageBox.Show's own blocking behavior — every real call site awaits/branches on this return value immediately, the same way it did on MessageBoxResult before.</summary>
    public static bool Show(Window? owner, string message, string title, ThemedConfirmSeverity severity)
    {
        var dialog = new ThemedConfirmDialog(owner, message, title, severity);
        dialog.ShowDialog();
        return dialog._result;
    }

    private void Yes_Click(object sender, RoutedEventArgs e)
    {
        _result = true;
        Close();
    }

    private void No_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
