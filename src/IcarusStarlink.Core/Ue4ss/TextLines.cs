namespace IcarusStarlink.Core.Ue4ss;

/// <summary>
/// A text file as lines, each keeping its own line ending: a config edited in different editors
/// can mix "\r\n" and "\n" (NearbyCrafting.ini does), and saving one value must not rewrite them all.
/// </summary>
internal static class TextLines
{
    /// <summary>The lines without their endings, and each line's ending ("\r\n", "\n", or "" for the last one).</summary>
    public static (List<string> Lines, List<string> Endings) Split(string text)
    {
        var lines = new List<string>();
        var endings = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            var crlf = i > start && text[i - 1] == '\r';
            lines.Add(text[start..(crlf ? i - 1 : i)]);
            endings.Add(crlf ? "\r\n" : "\n");
            start = i + 1;
        }

        lines.Add(text[start..]);
        endings.Add("");
        return (lines, endings);
    }

    public static string Join(List<string> lines, List<string> endings) =>
        string.Concat(lines.Select((line, i) => line + endings[i]));

    /// <summary>The ending new lines get: "\r\n" when the file uses it anywhere.</summary>
    public static string NewLineOf(string text) => text.Contains("\r\n") ? "\r\n" : "\n";
}
