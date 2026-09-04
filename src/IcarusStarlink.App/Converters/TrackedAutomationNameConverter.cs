using System.Globalization;
using System.Windows.Data;

namespace IcarusStarlink.App.Converters;

/// <summary>true (IsTracked) -&gt; "Untrack mod", false -&gt; "Track mod" — for the Nexus catalog
/// card's Track/Untrack button. AutomationProperties.Name has to be a live Binding here rather than
/// a Style.Triggers Setter: a Setter inside a DataTrigger doesn't reliably re-announce to UI
/// Automation clients when IsTracked flips at runtime on an already-realized button (confirmed with
/// a UI-automation test tool — the client kept reporting the stale name after toggling live), while
/// a direct Binding update goes through the normal dependency-property value pipeline that the
/// automation peer's name-changed notification actually depends on.</summary>
public sealed class TrackedAutomationNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Untrack mod" : "Track mod";

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
