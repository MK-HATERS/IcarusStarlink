using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace IcarusStarlink.App.Views;

/// <summary>
/// Keeps a collapsed group collapsed on the Saves page's grouped lists. Every search keystroke
/// rebuilds the list, and with it each group's Expander, which would open them all again. Set
/// Scope on the list ("Talents"); the group Expander in SaveCollapsibleGroupItemStyle reads it.
/// </summary>
public static class SaveGroupExpansion
{
    // Session-only, per list and group name: "Talents|Adventure · Fishing".
    private static readonly HashSet<string> Collapsed = new(StringComparer.Ordinal);

    public static readonly DependencyProperty ScopeProperty = DependencyProperty.RegisterAttached(
        "Scope", typeof(string), typeof(SaveGroupExpansion), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    public static string? GetScope(DependencyObject element) => (string?)element.GetValue(ScopeProperty);

    public static void SetScope(DependencyObject element, string? value) => element.SetValue(ScopeProperty, value);

    public static readonly DependencyProperty RememberProperty = DependencyProperty.RegisterAttached(
        "Remember", typeof(bool), typeof(SaveGroupExpansion), new PropertyMetadata(false, OnRememberChanged));

    public static bool GetRemember(DependencyObject element) => (bool)element.GetValue(RememberProperty);

    public static void SetRemember(DependencyObject element, bool value) => element.SetValue(RememberProperty, value);

    private static void OnRememberChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Expander expander || e.NewValue is not true)
        {
            return;
        }

        expander.Loaded += (_, _) =>
        {
            if (KeyOf(expander) is { } key)
            {
                expander.IsExpanded = !Collapsed.Contains(key);
            }
        };
        expander.Collapsed += (_, _) => Remember(expander, collapsed: true);
        expander.Expanded += (_, _) => Remember(expander, collapsed: false);
    }

    private static void Remember(Expander expander, bool collapsed)
    {
        if (KeyOf(expander) is not { } key)
        {
            return;
        }

        if (collapsed)
        {
            Collapsed.Add(key);
        }
        else
        {
            Collapsed.Remove(key);
        }
    }

    private static string? KeyOf(Expander expander) =>
        GetScope(expander) is { } scope && expander.DataContext is CollectionViewGroup { Name: { } name } ? $"{scope}|{name}" : null;
}
