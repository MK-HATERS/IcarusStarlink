namespace IcarusStarlink.Catalog;

/// <summary>One "last updated" date the GitHub lookup resolved, keyed the way the UI keys it.</summary>
public sealed record RepoPushedDate(string Owner, string Repo, DateTimeOffset PushedAtUtc);

/// <summary>
/// The last successful catalog fetch, written to disk so the Downloads tab opens instantly on the
/// next launch instead of re-fetching both community catalogs and ~40 GitHub repo dates every time
/// (which is also what kept tripping GitHub's 60/hour unauthenticated cap). Refresh still goes to
/// the network; this only answers the automatic load.
/// </summary>
public sealed record CatalogSnapshot(DateTimeOffset FetchedAtUtc, IReadOnlyList<CatalogEntry> Entries, IReadOnlyList<RepoPushedDate> RepoDates);

public interface ICatalogSnapshotStore
{
    /// <summary>Null when there is no snapshot yet or it can't be read — never throws.</summary>
    CatalogSnapshot? TryLoad();

    /// <summary>Best-effort — a snapshot that can't be written is simply not cached.</summary>
    void Save(CatalogSnapshot snapshot);
}
