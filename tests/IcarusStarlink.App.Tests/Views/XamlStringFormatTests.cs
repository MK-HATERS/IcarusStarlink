using System.IO;
using System.Text.RegularExpressions;

namespace IcarusStarlink.App.Tests.Views;

/// <summary>
/// WPF applies a binding's StringFormat only to a text property. ToolTip, Content and Header take
/// any object, so there it's ignored and the bare value shows: the Theme button's tooltip said only
/// "Dark" instead of "Change the colours: click for the Dark theme…". Build the text in the view
/// model, or use a TextBlock (or ContentStringFormat) instead.
/// </summary>
public sealed class XamlStringFormatTests
{
    /// <summary>Known ones in pages outside the shell, left for their own reviews; take a file off once it's fixed.</summary>
    private static readonly HashSet<string> KnownElsewhere = new(StringComparer.OrdinalIgnoreCase)
    {
        "LibraryView.xaml",
        "SavesOverviewTab.xaml",
    };

    private static readonly Regex StringFormatOnObjectProperty = new(
        @"\b(ToolTip|Content|Header)=""\{Binding [^""]*StringFormat", RegexOptions.Compiled);

    [Fact]
    public void NoToolTipContentOrHeader_IsFormattedWithStringFormat()
    {
        var appFolder = Path.Combine(RepoRoot(), "src", "IcarusStarlink.App");
        var found = Directory.EnumerateFiles(appFolder, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(path => !KnownElsewhere.Contains(Path.GetFileName(path)))
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, index)))
            .Where(entry => StringFormatOnObjectProperty.IsMatch(entry.line))
            .Select(entry => $"{Path.GetRelativePath(appFolder, entry.path)}:{entry.index + 1}")
            .ToList();

        Assert.Empty(found);
    }

    private static string RepoRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "IcarusStarlink.slnx")))
        {
            folder = folder.Parent;
        }

        return folder?.FullName ?? throw new InvalidOperationException("IcarusStarlink.slnx not found above the test folder.");
    }
}
