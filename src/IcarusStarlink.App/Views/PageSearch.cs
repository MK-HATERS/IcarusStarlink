using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace IcarusStarlink.App.Views;

/// <summary>
/// Ctrl+F on any page: the page's own search or filter box. Found by what the box is called (its
/// name, screen-reader name or placeholder says "search" or "filter"), the first one showing, so a
/// page needs nothing extra: Library, Database (both tabs), Editor, the Saves tab that's open,
/// Settings › UE4SS config, the server log, Help.
/// </summary>
public static class PageSearch
{
    private static readonly string[] Words = ["search", "filter"];

    public static TextBox? Find(DependencyObject root)
    {
        if (root is UIElement { Visibility: not Visibility.Visible })
        {
            return null;
        }

        if (root is TextBox { IsEnabled: true, IsReadOnly: false } box && IsSearchBox(box))
        {
            return box;
        }

        var count = root is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(root) : 0;
        for (var i = 0; i < count; i++)
        {
            if (Find(VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static bool IsSearchBox(TextBox box) =>
        new[] { box.Name, AutomationProperties.GetName(box), AutomationProperties.GetAutomationId(box), box.Tag as string }
            .Any(text => text is { Length: > 0 } && Words.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase)));
}
