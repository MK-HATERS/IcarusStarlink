using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace IcarusStarlink.App.Views;

/// <summary>
/// views:PasswordBoxText.Text="{Binding X}" binds a PasswordBox both ways. PasswordBox.Password
/// isn't a dependency property, so this carries it: typing updates the binding, and a new value
/// from the view model (another server picked) is put back into the box.
/// </summary>
public static class PasswordBoxText
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(PasswordBoxText),
        // Default null, not "": the first bound value (even an empty one) must arrive as a change, or the box is never hooked.
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnTextChanged)
        {
            DefaultUpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });

    private static readonly DependencyProperty IsHookedProperty = DependencyProperty.RegisterAttached(
        "IsHooked", typeof(bool), typeof(PasswordBoxText), new PropertyMetadata(false));

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string value) => element.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box)
        {
            return;
        }

        if (!(bool)box.GetValue(IsHookedProperty))
        {
            box.SetValue(IsHookedProperty, true);
            box.PasswordChanged += OnPasswordChanged;
        }

        var text = e.NewValue as string ?? "";
        // Only when it differs: setting Password moves the caret to the start.
        if (box.Password != text)
        {
            box.Password = text;
        }
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        if (GetText(box) != box.Password)
        {
            SetText(box, box.Password);
        }
    }
}
