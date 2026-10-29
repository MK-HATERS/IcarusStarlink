using System.IO;
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
                    var saves = new ResourceDictionary { Source = new Uri("/IcarusStarlink;component/Views/Saves/SavesResources.xaml", UriKind.Relative) };
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

                // The mod editor's windows take a view model, so they aren't built above: build their shared templates.
                try
                {
                    var editor = new ResourceDictionary { Source = new Uri("/IcarusStarlink;component/Views/Editor/ExmodEditorResources.xaml", UriKind.Relative) };
                    foreach (var template in editor.Values.OfType<DataTemplate>())
                    {
                        template.LoadContent();
                    }

                    new GroupItem { Style = (Style)editor["ExmodFieldGroupItemStyle"] }.ApplyTemplate();
                    new TextBox { Style = (Style)editor["ExmodChangedTextBoxStyle"] }.ApplyTemplate();
                }
                catch (Exception ex) when (ex is XamlParseException or InvalidOperationException)
                {
                    failures.Add($"ExmodEditorResources: {ex.Message}");
                }

                try
                {
                    BuildModEditorWindows();
                }
                catch (Exception ex) when (ex is XamlParseException or InvalidOperationException)
                {
                    failures.Add($"Mod editor windows: {ex.Message}");
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

    /// <summary>
    /// The mod editor and its pop-out pane over a real (temporary) mod, laid out in every view, so
    /// each view's templates are built (a missing resource key there throws only when shown).
    /// </summary>
    private static void BuildModEditorWindows()
    {
        var root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "EditorSmoke_" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "Data", "Traits");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "D_Itemable.json"), """
            {"RowStruct":"S","Defaults":{"Weight":0,"MaxStack":1,"bCanStack":false},"Rows":[
              {"Name":"Item_Fiber","Weight":10,"MaxStack":100,"DisplayName":"NSLOCTEXT(\"D_Itemable\", \"Item_Fiber-DisplayName\", \"Fiber\")"},
              {"Name":"Item_Stone","Weight":20,"MaxStack":50},{"Name":"Item_Wood","Weight":30,"MaxStack":40},{"Name":"Item_Stick","Weight":5,"MaxStack":20}
            ]}
            """);
        using var library = new IcarusStarlink.Storage.Library.FolderLibraryRepository(Path.Combine(root, "Extracted_Mods"), Path.Combine(root, "Library_Meta"), Path.Combine(root, "Backups"),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IcarusStarlink.Storage.Library.FolderLibraryRepository>.Instance);
        try
        {
            var entry = library.CreateBlankMod("Smoke", "Me", IcarusStarlink.Core.Library.ModTemplate.Blank);
            var folder = library.GetFolderPath(entry.FolderName);
            var package = IcarusStarlink.PakIO.Container.ExmodFolder.Read(folder).Package;
            package.Rows.Insert(0, new IcarusStarlink.PakIO.Exmod.ExmodFileRow
            {
                CurrentFile = "Traits-D_Itemable.json",
                FileItems =
                [
                    new() { Name = "Item_Fiber", Fields = { ["Weight"] = System.Text.Json.Nodes.JsonValue.Create(20), ["MaxStack"] = System.Text.Json.Nodes.JsonValue.Create(1000), ["bCanStack"] = System.Text.Json.Nodes.JsonValue.Create(true) } },
                    new() { Name = "Item_Stone", Fields = { ["MaxStack"] = System.Text.Json.Nodes.JsonValue.Create(500) } },
                    new() { Name = "Item_Wood", Fields = { ["MaxStack"] = System.Text.Json.Nodes.JsonValue.Create(400) } },
                ],
            });
            IcarusStarlink.PakIO.Container.ExmodFolder.Write(folder, new IcarusStarlink.PakIO.Container.ExmodPackageContents(package, []));

            var viewModel = new IcarusStarlink.App.ViewModels.ExmodEditorViewModel(entry.FolderName, library, Path.Combine(root, "Data"), new IcarusStarlink.App.Services.GameDataIndexCache());
            var editor = new IcarusStarlink.App.Views.ExmodEditorWindow(viewModel);
            var pane = new IcarusStarlink.App.Views.ExmodPaneWindow(viewModel, IcarusStarlink.App.ViewModels.ExmodEditorViewMode.ItemFields);
            foreach (var mode in Enum.GetValues<IcarusStarlink.App.ViewModels.ExmodEditorViewMode>())
            {
                viewModel.ViewMode = mode;
                LayOut(editor);
                LayOut(pane);
            }

            pane.Close();
            editor.Close();
            viewModel.Detach();
        }
        finally
        {
            library.Dispose();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static void LayOut(Window window)
    {
        if (window.Content is FrameworkElement content)
        {
            content.Measure(new Size(1200, 760));
            content.Arrange(new Rect(0, 0, 1200, 760));
            content.UpdateLayout();
        }
    }

    /// <summary>Application.Current would otherwise point at this test's finished thread for the rest of the run.</summary>
    private static void ForgetTheApplication() =>
        typeof(Application).GetField("_appInstance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, null);
}

[CollectionDefinition(ViewSmokeTests.Name, DisableParallelization = true)]
public sealed class ViewSmokeCollection;
