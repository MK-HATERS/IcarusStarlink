using System.Threading;
using System.Windows;
using System.Windows.Controls;
using IcarusStarlink.App.Views;

namespace IcarusStarlink.App.Tests.Views;

/// <summary>
/// User report 2026-10-23: typing in the mod editor's "Add a stat" search made the whole editor
/// jump, and scrolling its matches scrolled the editor. ScrollChanged bubbles, so a scrollable
/// control inside the fields was taken for the fields' own scrolling and synced to the editor.
/// </summary>
public class ScrollSyncHelperTests
{
    [Fact]
    public void IsHostsOwn_OnlyTheHostOrATextBoxHostsOwnScrollViewer()
    {
        var results = new Dictionary<string, bool>();
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                // A TextBox scrolls with a ScrollViewer from its template (PART_ContentHost); a bare
                // test thread has no theme, so each control gets a template of just that.
                var host = new ScrollViewer();
                var (textBox, textBoxViewer) = WithTemplateScrollViewer();
                var (_, innerViewer) = WithTemplateScrollViewer();

                results["host"] = ScrollSyncHelper.IsHostsOwn(host, host);
                results["scrollable control inside the host"] = ScrollSyncHelper.IsHostsOwn(host, innerViewer);
                results["plain viewer inside the host"] = ScrollSyncHelper.IsHostsOwn(host, new ScrollViewer());
                results["text box host's own"] = ScrollSyncHelper.IsHostsOwn(textBox, textBoxViewer);
                results["another control inside a text box host"] = ScrollSyncHelper.IsHostsOwn(textBox, innerViewer);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(error);
        Assert.True(results["host"]);
        Assert.False(results["scrollable control inside the host"]);
        Assert.False(results["plain viewer inside the host"]);
        Assert.True(results["text box host's own"]);
        Assert.False(results["another control inside a text box host"]);
    }

    private static (Control Control, ScrollViewer Viewer) WithTemplateScrollViewer()
    {
        var template = new ControlTemplate(typeof(Control)) { VisualTree = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost") };
        var control = new Control { Template = template };
        control.ApplyTemplate();
        return (control, (ScrollViewer)template.FindName("PART_ContentHost", control));
    }
}
