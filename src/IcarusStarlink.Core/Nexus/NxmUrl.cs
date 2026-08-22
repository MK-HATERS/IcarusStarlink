namespace IcarusStarlink.Core.Nexus;

/// <summary>
/// Parses a real nxm:// URL — the protocol Nexus's own "Mod Manager Download" buttons fire,
/// confirmed against Vortex's own real, production NXMUrl.ts source during Phase 8.3b planning,
/// not guessed: "nxm://{gameDomain}/mods/{modId}/files/{fileId}?key={key}&amp;expires={expires}
/// &amp;user_id={userId}". Key/expires are present only for a non-premium account — Nexus's own
/// download_link endpoint only needs them in that case; a premium account's API key alone is
/// enough. Deliberately narrower than Vortex's own parser: this app only handles plain mod-file
/// downloads (not collections/OAuth/premium-ping variants Vortex's own client also has to parse).
/// </summary>
public sealed record NxmUrl(string GameDomain, int ModId, int FileId, string? Key, long? Expires)
{
    private static readonly System.Text.RegularExpressions.Regex ModFilePattern =
        new(@"^/mods/(\d+)/files/(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Throws FormatException for anything that isn't a plain nxm://.../mods/{id}/files/{id} URL — collection/OAuth/premium nxm variants aren't handled by this app.</summary>
    public static NxmUrl Parse(string nxmUrlText)
    {
        Uri parsed;
        try
        {
            parsed = new Uri(nxmUrlText);
        }
        catch (UriFormatException ex)
        {
            throw new FormatException($"'{nxmUrlText}' is not a valid URL.", ex);
        }

        if (!string.Equals(parsed.Scheme, "nxm", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException($"'{nxmUrlText}' is not an nxm:// URL.");
        }

        var match = ModFilePattern.Match(parsed.AbsolutePath);
        if (!match.Success)
        {
            throw new FormatException($"'{nxmUrlText}' isn't a recognized mod-file nxm URL (only nxm://<game>/mods/<id>/files/<id> is supported).");
        }

        var query = System.Web.HttpUtility.ParseQueryString(parsed.Query);
        var key = query["key"];
        var expires = long.TryParse(query["expires"], out var expiresValue) ? expiresValue : (long?)null;

        return new NxmUrl(
            GameDomain: parsed.Host,
            ModId: int.Parse(match.Groups[1].Value),
            FileId: int.Parse(match.Groups[2].Value),
            Key: string.IsNullOrEmpty(key) ? null : key,
            Expires: expires);
    }
}
