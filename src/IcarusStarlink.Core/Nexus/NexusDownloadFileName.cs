using System.Text.RegularExpressions;

namespace IcarusStarlink.Core.Nexus;

/// <summary>
/// Nexus names every downloaded file "&lt;upload name&gt; &lt;modId&gt; &lt;fileId&gt; &lt;yyyy-MM-ddTHH-mmZ&gt; &lt;hash&gt;.ext"
/// (confirmed against real downloads: "ArcFissionLibrary V1.2.2 356 3 2026-10-12T13-50Z b0gYfPbb2.zip").
/// That whole tail rode into staged UE4SS mod names and opaque pak names — a real complaint. The
/// upload name is the part a person recognizes.
/// </summary>
public static partial class NexusDownloadFileName
{
    [GeneratedRegex(@"^(?<name>.+?)\s+\d+\s+\d+\s+\d{4}-\d{2}-\d{2}T\d{2}-\d{2}Z\s+\S+$")]
    private static partial Regex NexusSuffixPattern();

    /// <summary>"ArcFissionLibrary V1.2.2 356 3 2026-10-12T13-50Z b0gYfPbb2" → "ArcFissionLibrary V1.2.2"; anything without the suffix comes back unchanged.</summary>
    public static string StripNexusSuffix(string nameWithoutExtension)
    {
        var match = NexusSuffixPattern().Match(nameWithoutExtension.Trim());
        return match.Success ? match.Groups["name"].Value.Trim() : nameWithoutExtension;
    }
}
