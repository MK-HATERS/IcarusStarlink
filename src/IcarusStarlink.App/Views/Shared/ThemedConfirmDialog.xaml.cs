using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace IcarusStarlink.App.Views;

public enum ThemedConfirmSeverity { Question, Warning, Information }

/// <summary>
/// The app's themed question box, used instead of MessageBox: Yes/No (Show), the caller's words on
/// the two buttons (ShowWithWords), the same plus Cancel (ShowWithCancel), or three answers plus
/// Cancel (ShowChoice). The severity picks the title colour and icon; Warning starts on the safe
/// answer.
/// </summary>
public partial class ThemedConfirmDialog : Window
{
    /// <summary>true = Yes, false = No, null = closed or Cancel.</summary>
    private bool? _result;

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

    /// <summary>Modal; true when the answer is Yes.</summary>
    public static bool Show(Window? owner, string message, string title, ThemedConfirmSeverity severity)
    {
        var dialog = new ThemedConfirmDialog(owner, message, title, severity);
        dialog.ShowDialog();
        return dialog._result == true;
    }

    /// <summary>Two answers with the caller's words on the buttons: true = the first, false = the second, Escape or the window's ✕.</summary>
    public static bool ShowWithWords(Window? owner, string message, string title, ThemedConfirmSeverity severity, string yesText, string noText)
    {
        var dialog = new ThemedConfirmDialog(owner, message, title, severity);
        dialog.YesButton.Content = yesText;
        dialog.NoButton.Content = noText;
        dialog.ShowDialog();
        return dialog._result == true;
    }

    /// <summary>
    /// Three answers (the mod editor's "Save / Don't save / Cancel" on close): true = the first
    /// button, false = the second, null = Cancel, Escape or the window's ✕.
    /// </summary>
    public static bool? ShowWithCancel(Window? owner, string message, string title, ThemedConfirmSeverity severity, string yesText, string noText)
    {
        var dialog = new ThemedConfirmDialog(owner, message, title, severity);
        dialog.YesButton.Content = yesText;
        dialog.NoButton.Content = noText;
        dialog.CancelButton.Visibility = Visibility.Visible;
        dialog.ShowDialog();
        return dialog._result;
    }

    /// <summary>
    /// One of three answers, or none: 0 = <paramref name="first"/> (the main button), 1 = <paramref name="second"/>,
    /// 2 = <paramref name="third"/>, null = Cancel, Escape or the window's ✕ (Cancel has the focus).
    /// </summary>
    public static int? ShowChoice(Window? owner, string message, string title, ThemedConfirmSeverity severity, string first, string second, string third)
    {
        var dialog = new ThemedConfirmDialog(owner, message, title, severity);
        dialog.YesButton.Content = first;
        dialog.NoButton.Content = second;
        dialog.ThirdButton.Content = third;
        dialog.ThirdButton.Visibility = Visibility.Visible;
        dialog.CancelButton.Visibility = Visibility.Visible;
        dialog.Loaded += (_, _) => dialog.CancelButton.Focus();
        dialog.ShowDialog();
        return dialog._third ? 2 : dialog._result switch { true => 0, false => 1, null => null };
    }

    private bool _third;

    private void Third_Click(object sender, RoutedEventArgs e)
    {
        _third = true;
        Close();
    }

    private void Yes_Click(object sender, RoutedEventArgs e)
    {
        _result = true;
        Close();
    }

    private void No_Click(object sender, RoutedEventArgs e)
    {
        _result = false;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

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
