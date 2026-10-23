namespace IcarusStarlink.Core.Server;

/// <summary>Checks for the admin list (AdminWhitelist.txt): one Steam64 id per line.</summary>
public static class ServerAccessLists
{
    /// <summary>A Steam64 id of a user account: 17 digits, and every one starts 7656119 — "7656" catches typos without being picky.</summary>
    public static bool IsSteam64Id(string text) =>
        text.Length == 17 && text.StartsWith("7656", StringComparison.Ordinal) && text.All(char.IsAsciiDigit);

    /// <summary>The non-blank lines that aren't Steam64 ids, with their 1-based line numbers, for a hint under the box.</summary>
    public static IReadOnlyList<(int Line, string Text)> InvalidAdminLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var bad = new List<(int, string)>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length > 0 && !IsSteam64Id(line))
            {
                bad.Add((i + 1, line));
            }
        }

        return bad;
    }
}
