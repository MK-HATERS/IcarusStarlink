using System.Text.RegularExpressions;

namespace IcarusStarlink.Core.Nexus;

/// <summary>
/// Nexus names every downloaded file "&lt;upload name&gt; &lt;modId&gt; &lt;fileId&gt; &lt;yyyy-MM-ddTHH-mmZ&gt; &lt;hash&gt;.ext"
/// (confirmed against real downloads: "ArcFissionLibrary V1.2.2 356 3 2026-10-12T13-50Z b0gYfPbb2.zip").
/// That whole tail rode into staged UE4SS mod names and opaque pak names — a real complaint. The
/// upload name is the part a person recognizes.
/// A manual download from the website uses a second form instead:
/// "&lt;upload name&gt;-&lt;modId&gt;-&lt;version parts&gt;-&lt;id&gt;-&lt;unix time&gt;.ext" (real:
/// "Cheat S5-II and Larkwell Haulier underwears-59-2-3-25-146718-1767981001.zip").
/// </summary>
public static partial class NexusDownloadFileName
{
    [GeneratedRegex(@"^(?<name>.+?)\s+\d+\s+\d+\s+\d{4}-\d{2}-\d{2}T\d{2}-\d{2}Z\s+\S+$")]
    private static partial Regex NexusSuffixPattern();

    // Anchored on the trailing 10-digit unix time; every dash part after the mod id starts with a
    // digit ("5-2SE" versions included), so a real hyphenated name ("Mod-Fix", "S5-II") stays whole.
    [GeneratedRegex(@"^(?<name>.+?)-\d+(?:-\d[0-9A-Za-z]*)+-[1-4]\d{9}$")]
    private static partial Regex ManualDownloadSuffixPattern();

    /// <summary>
    /// "ArcFissionLibrary V1.2.2 356 3 2026-10-12T13-50Z b0gYfPbb2" → "ArcFissionLibrary V1.2.2";
    /// "My Mod-59-2-3-25-146718-1767981001" → "My Mod". When the mod id is known, the manual form
    /// only counts with that id right after the name. Anything without either suffix comes back unchanged.
    /// </summary>
    public static string StripNexusSuffix(string nameWithoutExtension, int? modId = null)
    {
        var trimmed = nameWithoutExtension.Trim();
        var match = NexusSuffixPattern().Match(trimmed);
        if (match.Success)
        {
            return match.Groups["name"].Value.Trim();
        }

        // A known mod id pins where the name ends, even when the name itself holds a "-<digits>" part.
        var manual = modId is > 0
            ? Regex.Match(trimmed, $@"^(?<name>.+?)-{modId.Value}(?:-\d[0-9A-Za-z]*)+-[1-4]\d{{9}}$")
            : ManualDownloadSuffixPattern().Match(trimmed);
        return manual.Success ? manual.Groups["name"].Value.Trim() : nameWithoutExtension;
    }
}
