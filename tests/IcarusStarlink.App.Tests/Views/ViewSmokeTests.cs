using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace IcarusStarlink.App.Tests.Views;

/// <summary>
/// Builds every page and window that has a plain constructor against the app's real styles, so a
/// XAML mistake (a StaticResource key that doesn't exist, a bad property, a malformed template)
/// fails here instead of only when a user opens that screen. Bindings aren't checked: there's no
/// view model behind them, and WPF reports a broken binding without throwing.
/// It needs an Application for the styles, and app code sends work to Application.Current's
/// dispatcher when there is one; so it runs alone after every other test (its own collection with
/// parallel runs off) and takes the Application away again when it's done.
/// </summary>
[Collection(Name)]
public sealed class ViewSmokeTests
{
    public const string Name = "Views built alone";

    private static readonly string[] Dictionaries =
    [
        "/IcarusStarlink;component/Themes/Icons.xaml",
        "/IcarusStarlink;component/Themes/Styles.xaml",
        "/IcarusStarlink;component/Themes/Dark.xaml",
    ];

    [Fact]
    public void EveryViewAndWindow_LoadsItsXaml()
    {
        var failures = new List<string>();
        var built = 0;
        Exception? setupError = null;

        var thread = new Thread(() =>
        {
            try
            {
                // One Application per process: reuse one another test made, else make a plain one
                // (not App itself, whose startup builds the whole service host).
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                if (app.Resources.MergedDictionaries.Count == 0)
                {
                    foreach (var source in Dictionaries)
                    {
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
                    }
                }

                var types = typeof(IcarusStarlink.App.MainWindow).Assembly.GetTypes()
                    .Where(t => t is { IsAbstract: false, IsPublic: true }
                        && (typeof(Window).IsAssignableFrom(t) || typeof(UserControl).IsAssignableFrom(t))
                        && t.GetConstructor(Type.EmptyTypes) is not null)
                    .OrderBy(t => t.Name);

                foreach (var type in types)
                {
                    try
                    {
                        var view = Activator.CreateInstance(type);
                        built++;
                        if (view is Window window)
                        {
                            window.Close();
                        }
                    }
                    catch (TargetInvocationException ex) when (ex.InnerException is XamlParseException or InvalidOperationException)
                    {
                        failures.Add($"{type.Name}: {ex.InnerException!.Message}");
                    }
                }

                // Templates only load when something uses them: the Saves page's shared ones are built here.
                try
                {
                    var saves = new ResourceDictionary { Source = new Uri("/IcarusStarlink;component/Views/SavesResources.xaml", UriKind.Relative) };
                    foreach (var template in saves.Values.OfType<DataTemplate>())
                    {
                        template.LoadContent();
                    }

                    new TextBox { Style = (Style)saves["SaveSearchBoxStyle"] }.ApplyTemplate();
                    new GroupItem { Style = (Style)saves["SaveCollapsibleGroupItemStyle"] }.ApplyTemplate();
                    new System.Windows.Controls.Primitives.ToggleButton { Style = (Style)saves["AccoladeTierChipStyle"] }.ApplyTemplate();
                    new Border { Style = (Style)saves["SaveRowHoverStyle"] }.ApplyTemplate();
                }
                catch (Exception ex) when (ex is XamlParseException or InvalidOperationException)
                {
                    failures.Add($"SavesResources: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                setupError = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        ForgetTheApplication();
        Assert.Null(Application.Current);

        Assert.Null(setupError);
        Assert.True(built >= 15, $"Only {built} views were built; the type scan or the styles didn't load.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>Application.Current would otherwise point at this test's finished thread for the rest of the run.</summary>
    private static void ForgetTheApplication() =>
        typeof(Application).GetField("_appInstance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, null);
}

[CollectionDefinition(ViewSmokeTests.Name, DisableParallelization = true)]
public sealed class ViewSmokeCollection;
