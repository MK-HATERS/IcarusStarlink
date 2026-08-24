namespace IcarusStarlink.PakIO.Exmod;

/// <summary>Classic O(n*m) dynamic-programming edit distance — used only for short DataTable row names, so no need for a memory-optimized variant.</summary>
public static class LevenshteinDistance
{
    public static int Compute(string a, string b)
    {
        var n = a.Length;
        var m = b.Length;
        if (n == 0)
        {
            return m;
        }

        if (m == 0)
        {
            return n;
        }

        var previous = new int[m + 1];
        var current = new int[m + 1];
        for (var j = 0; j <= m; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= n; i++)
        {
            current[0] = i;
            for (var j = 1; j <= m; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[m];
    }
}
