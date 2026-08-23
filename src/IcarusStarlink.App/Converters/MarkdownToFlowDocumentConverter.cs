using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;

namespace IcarusStarlink.App.Converters;

/// <summary>
/// Renders a small, hand-rolled Markdown subset — "# "/"## " headers, "**bold**" spans,
/// "`code`" spans, and "- "/"* " bullet lines — into a FlowDocument for a read-only RichTextBox.
/// Not a general Markdown parser: real EXMOD readmes only use these few constructs, and pulling in
/// a full Markdown library would be a lot of dependency weight for a handful of rules.
/// </summary>
public sealed class MarkdownToFlowDocumentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // FlowDocument's own default is the system SERIF face — visibly out of place against the
        // rest of the app, so match the app's UI font explicitly (affects Help, Library's Readme
        // tab, and the What's New dialog alike).
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 13,
        };
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

    /// <summary>
    /// Handles "**bold**" first, then "`code`" within each resulting plain segment — the two inline
    /// constructs actually used by real EXMOD readmes and this app's own Help topics. Code spans
    /// were rendering as literal backticks before (paths like `Content\Paks\mods` appear all over
    /// the Help text), which reads as a typo rather than as markup.
    /// </summary>
    private static void AddInlines(Paragraph paragraph, string line)
    {
        var segments = line.Split("**");
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length == 0)
            {
                continue;
            }

            if (i % 2 == 1)
            {
                paragraph.Inlines.Add(new Bold(new Run(segments[i])));
                continue;
            }

            AddCodeAwareRuns(paragraph, segments[i]);
        }
    }

    private static void AddCodeAwareRuns(Paragraph paragraph, string text)
    {
        var parts = text.Split('`');
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0)
            {
                continue;
            }

            // An unpaired backtick leaves a trailing odd segment — rendering it as code anyway is
            // closer to the author's intent than showing a stray backtick.
            paragraph.Inlines.Add(i % 2 == 1
                ? new Run(parts[i]) { FontFamily = new System.Windows.Media.FontFamily("Consolas") }
                : new Run(parts[i]));
        }
    }
}
