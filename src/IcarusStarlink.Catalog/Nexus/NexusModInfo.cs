namespace IcarusStarlink.Catalog.Nexus;

/// <summary>
/// One mod as Nexus's API describes it — returned by the v1 single-mod lookup (GetModInfoAsync)
/// and browse lists (GetModListAsync), and by the v2 GraphQL search/all queries, which all carry
/// the same core fields. PictureUrl is the mod page's own header image CDN URL, null when the
/// author never set one. CreatedAt/UpdatedAt are the mod's publish and last-update times
/// (v1 created_time/updated_time, GraphQL createdAt/updatedAt) — optional so constructions that
/// predate them stay valid.
/// </summary>
public sealed record NexusModInfo(
    int ModId, string? Name, string Author, string? Summary, string Version, string? PictureUrl,
    DateTimeOffset? CreatedAt = null, DateTimeOffset? UpdatedAt = null)
{
    /// <summary>The card-ready "published … · updated …" line, local time, or null when the API gave no dates (so the UI can hide the line entirely).</summary>
    public string? DatesDisplay
    {
        get
        {
            var published = CreatedAt is { } c ? $"published {c.LocalDateTime:d MMM yyyy}" : null;
            // An update on the publish day is just the upload itself finishing — showing both
            // would read as two events when there was one.
            var updated = UpdatedAt is { } u && (CreatedAt is not { } c2 || u.Date != c2.Date)
                ? $"updated {u.LocalDateTime:d MMM yyyy}"
                : null;
            return (published, updated) switch
            {
                (null, null) => null,
                (null, _) => updated,
                (_, null) => published,
                _ => $"{published} · {updated}",
            };
        }
    }
}
