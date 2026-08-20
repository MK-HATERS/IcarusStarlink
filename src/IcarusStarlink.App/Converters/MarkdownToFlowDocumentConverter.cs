using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;

namespace IcarusStarlink.App.Converters;

/// <summary>
/// Renders a small, hand-rolled Markdown subset — "# "/"## " headers, "**bold**" spans, and
/// "- "/"* " bullet lines — into a FlowDocument for a read-only RichTextBox. Not a general
/// Markdown parser: real EXMOD readmes only use these few constructs, and pulling in a full
/// Markdown library would be a lot of dependency weight for a handful of rules.
/// </summary>
public sealed class MarkdownToFlowDocumentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var document = new FlowDocument { PagePadding = new Thickness(0) };
        if (value is not string text || string.IsNullOrWhiteSpace(text))
        {
            return document;
        }

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0)
            {
                continue;
            }

            Paragraph paragraph;
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                paragraph = new Paragraph { FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) };
                AddInlines(paragraph, line[3..]);
            }
            else if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                paragraph = new Paragraph { FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 6) };
                AddInlines(paragraph, line[2..]);
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                paragraph = new Paragraph { Margin = new Thickness(12, 1, 0, 1) };
                paragraph.Inlines.Add(new Run("• "));
                AddInlines(paragraph, line[2..]);
            }
            else
            {
                paragraph = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                AddInlines(paragraph, line);
            }

            document.Blocks.Add(paragraph);
        }

        return document;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <summary>Splits on "**bold**" spans only — the one inline construct real EXMOD readmes use.</summary>
    private static void AddInlines(Paragraph paragraph, string line)
    {
        var segments = line.Split("**");
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length == 0)
            {
                continue;
            }

            paragraph.Inlines.Add(i % 2 == 1 ? new Bold(new Run(segments[i])) : new Run(segments[i]));
        }
    }
}
