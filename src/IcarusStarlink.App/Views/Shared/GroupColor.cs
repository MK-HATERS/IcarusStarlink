using System.Windows;

namespace IcarusStarlink.App.Views;

/// <summary>
/// Which of the six heading colours (GroupColor0Brush … GroupColor5Brush) a group or section
/// heading uses: its place in the list, so the headings run through the colours in order (Saves'
/// Tier 1 to Tier 5, the mod editor's field sections, a config file's groups). Set once on the
/// group's root from the list's AlternationIndex (AlternationCount="6"); it's inherited, so the
/// stripe and title in the heading (GroupStripeStyle, GroupTitleStyle) read it themselves.
/// </summary>
public static class GroupColor
{
    public static readonly DependencyProperty IndexProperty = DependencyProperty.RegisterAttached(
        "Index", typeof(int), typeof(GroupColor),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.Inherits));

    public static int GetIndex(DependencyObject element) => (int)element.GetValue(IndexProperty);

    public static void SetIndex(DependencyObject element, int value) => element.SetValue(IndexProperty, value);
}
