using System.Windows;
using System.Windows.Input;

namespace IcarusStarlink.App.Views;

/// <summary>
/// The two whole-group buttons on a Saves list heading (SaveActionGroupItemStyle): set on the list,
/// read by every group's heading, which passes its own name. Talents: "Max" / "Reset"; Workshop:
/// "Research all" / "Clear".
/// </summary>
public static class SaveGroupActions
{
    public static readonly DependencyProperty MaxCommandProperty = DependencyProperty.RegisterAttached(
        "MaxCommand", typeof(ICommand), typeof(SaveGroupActions), new PropertyMetadata(null));

    public static ICommand? GetMaxCommand(DependencyObject element) => (ICommand?)element.GetValue(MaxCommandProperty);

    public static void SetMaxCommand(DependencyObject element, ICommand? value) => element.SetValue(MaxCommandProperty, value);

    public static readonly DependencyProperty ResetCommandProperty = DependencyProperty.RegisterAttached(
        "ResetCommand", typeof(ICommand), typeof(SaveGroupActions), new PropertyMetadata(null));

    public static ICommand? GetResetCommand(DependencyObject element) => (ICommand?)element.GetValue(ResetCommandProperty);

    public static void SetResetCommand(DependencyObject element, ICommand? value) => element.SetValue(ResetCommandProperty, value);

    public static readonly DependencyProperty MaxLabelProperty = DependencyProperty.RegisterAttached(
        "MaxLabel", typeof(string), typeof(SaveGroupActions), new PropertyMetadata("Max"));

    public static string GetMaxLabel(DependencyObject element) => (string)element.GetValue(MaxLabelProperty);

    public static void SetMaxLabel(DependencyObject element, string value) => element.SetValue(MaxLabelProperty, value);

    public static readonly DependencyProperty ResetLabelProperty = DependencyProperty.RegisterAttached(
        "ResetLabel", typeof(string), typeof(SaveGroupActions), new PropertyMetadata("Reset"));

    public static string GetResetLabel(DependencyObject element) => (string)element.GetValue(ResetLabelProperty);

    public static void SetResetLabel(DependencyObject element, string value) => element.SetValue(ResetLabelProperty, value);
}
