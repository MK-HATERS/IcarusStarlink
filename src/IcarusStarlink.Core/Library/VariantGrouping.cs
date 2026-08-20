using System.Text.RegularExpressions;

namespace IcarusStarlink.Core.Library;

/// <summary>
/// Groups library entries into variant families per the spec: an explicit "variantGroup" key
/// (case-insensitive) shared by 2+ entries groups them under that name; failing that, a shared
/// "_Nx" filename suffix (MyMod_2x / MyMod_5x) groups by the stripped base name. A family only
/// renders as a group when 2+ matching entries actually exist in the library — a lone match is
/// just itself, per "A family only appears when two or more matching variants are in Extracted Mods."
/// </summary>
public static partial class VariantGrouping
{
    [GeneratedRegex(@"_(\d+)x$", RegexOptions.IgnoreCase)]
    private static partial Regex MultiplierSuffixPattern();

    [GeneratedRegex(@"^(\d+)")]
    private static partial Regex LeadingNumberPattern();

    public static IReadOnlyList<LibraryGroup> Group(IEnumerable<LibraryEntry> entries)
    {
        var byKey = new Dictionary<string, (string DisplayBasis, List<LibraryEntry> Entries)>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var (key, displayBasis) = GetGroupKey(entry);
            if (!byKey.TryGetValue(key, out var bucket))
            {
                bucket = (displayBasis, []);
                byKey[key] = bucket;
            }

            bucket.Entries.Add(entry);
        }

        return byKey
            .Select(kv =>
            {
                var (displayBasis, entries) = kv.Value;
                var sorted = entries.Count > 1 ? SortVariants(entries) : entries;
                var displayName = entries.Count > 1 ? displayBasis.Replace('_', ' ').Trim() : sorted[0].Name;
                return new LibraryGroup(kv.Key, displayName, sorted);
            })
            .ToList();
    }

    private static (string Key, string DisplayBasis) GetGroupKey(LibraryEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.VariantGroup))
        {
            var basis = entry.VariantGroup.Trim();
            return ($"group:{basis.ToLowerInvariant()}", basis);
        }

        var strippedBasis = TryStripMultiplierSuffix(entry.FileName) ?? TryStripMultiplierSuffix(entry.Name);
        if (strippedBasis is not null)
        {
            return ($"suffix:{strippedBasis.ToLowerInvariant()}", strippedBasis);
        }

        // No grouping signal at all — key by FolderName, not FileName: FolderName is guaranteed
        // unique (disambiguated with a suffix on collision), but FileName is not — re-importing
        // the same mod intentionally produces two entries that share a FileName, and using it
        // here would wrongly merge them into a spurious 2-item "family".
        return ($"standalone:{entry.FolderName.ToLowerInvariant()}", entry.Name);
    }

    private static List<LibraryEntry> SortVariants(List<LibraryEntry> entries) =>
        [.. entries
            .OrderBy(e => e.VariantSort ?? int.MaxValue)
            .ThenBy(GetNumericSortValue)
            .ThenBy(e => e.Variant ?? e.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// "Labels that start with a number (2x, 5x, 10x) sort by that number" refers to the
    /// variant's own short label, not the whole mod name — "MyMod_10x" doesn't start with a
    /// digit even though it's a 10x variant. An explicit "variant" field starting with a number
    /// is used directly; a filename-suffix-guessed variant (no explicit "variant" key) derives
    /// its number from the same "_Nx" suffix GetGroupKey used to group it in the first place.
    /// </summary>
    private static double GetNumericSortValue(LibraryEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Variant) && TryParseLeadingNumber(entry.Variant, out var fromVariant))
        {
            return fromVariant;
        }

        if (TryGetMultiplierNumber(entry.FileName, out var fromFileName))
        {
            return fromFileName;
        }

        if (TryGetMultiplierNumber(entry.Name, out var fromName))
        {
            return fromName;
        }

        return double.MaxValue;
    }

    private static string? TryStripMultiplierSuffix(string value)
    {
        var match = MultiplierSuffixPattern().Match(value);
        return match.Success ? value[..match.Index] : null;
    }

    private static bool TryGetMultiplierNumber(string value, out double number)
    {
        var match = MultiplierSuffixPattern().Match(value);
        if (match.Success)
        {
            number = double.Parse(match.Groups[1].Value);
            return true;
        }

        number = 0;
        return false;
    }

    private static bool TryParseLeadingNumber(string label, out double value)
    {
        var match = LeadingNumberPattern().Match(label);
        if (match.Success)
        {
            value = double.Parse(match.Groups[1].Value);
            return true;
        }

        value = 0;
        return false;
    }
}
