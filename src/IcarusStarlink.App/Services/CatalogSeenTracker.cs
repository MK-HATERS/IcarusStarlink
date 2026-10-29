using IcarusStarlink.Catalog;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Settings;

namespace IcarusStarlink.App.Services;

/// <summary>How a Database page row compares with what the player saw on their last visit.</summary>
public enum CatalogNewKind
{
    None,
    New,
    Updated,
}

/// <summary>
/// The Database page's "New" tags: which IMM Database entries and Nexus mods arrived (or changed)
/// since the player last looked (classic IMM's Show New). Pure rules over AppSettings.CatalogSeen;
/// the page VMs decide when to record.
/// </summary>
public static class CatalogSeenTracker
{
    /// <summary>
    /// The first load of each IMM Database source counts as seen, so a first run shows no wall of
    /// several hundred "new" mods. Per source: one that was down at that first load gets its own
    /// baseline the first time it answers. True when the state changed (worth saving).
    /// </summary>
    public static bool EnsureImmBaseline(CatalogSeenState state, IEnumerable<CatalogEntry> entries)
    {
        var changed = false;
        foreach (var group in entries.GroupBy(e => e.Source))
        {
            var source = group.Key.ToString();
            if (state.ImmDatabaseSources.Contains(source))
            {
                continue;
            }

            foreach (var entry in group)
            {
                state.ImmDatabaseVersions[entry.Id] = entry.Version;
            }

            state.ImmDatabaseSources.Add(source);
            changed = true;
        }

        return changed;
    }

    /// <summary>New: an entry never seen. Updated: seen before, but the database now lists a newer version (the same "1.0" = "1" rule as everywhere else).</summary>
    public static CatalogNewKind Classify(CatalogSeenState state, CatalogEntry entry)
    {
        if (!state.ImmDatabaseSources.Contains(entry.Source.ToString()))
        {
            return CatalogNewKind.None;
        }

        if (!state.ImmDatabaseVersions.TryGetValue(entry.Id, out var seenVersion))
        {
            return CatalogNewKind.New;
        }

        return UpdateVersionComparer.HasUpdate(entry.Version, seenVersion, recordedSourceVersion: null)
            ? CatalogNewKind.Updated
            : CatalogNewKind.None;
    }

    /// <summary>
    /// Records every given entry (and its version) as seen. Entries no longer listed are kept, so a
    /// source that's briefly down doesn't make its whole list "new" again when it comes back.
    /// </summary>
    public static void MarkImmSeen(CatalogSeenState state, IEnumerable<CatalogEntry> entries)
    {
        foreach (var entry in entries)
        {
            state.ImmDatabaseVersions[entry.Id] = entry.Version;
            var source = entry.Source.ToString();
            if (!state.ImmDatabaseSources.Contains(source))
            {
                state.ImmDatabaseSources.Add(source);
            }
        }
    }

    /// <summary>New: published after the last visit. Updated: published before it, changed after it. No dates (or no visit recorded yet): neither.</summary>
    public static CatalogNewKind ClassifyNexus(DateTimeOffset? seenUpToUtc, NexusModInfo mod)
    {
        if (seenUpToUtc is not { } seenUpTo)
        {
            return CatalogNewKind.None;
        }

        if (mod.CreatedAt is { } created && created > seenUpTo)
        {
            return CatalogNewKind.New;
        }

        return mod.CreatedAt is not null && mod.UpdatedAt is { } updated && updated > seenUpTo
            ? CatalogNewKind.Updated
            : CatalogNewKind.None;
    }

    /// <summary>The short tag a row shows, or null for none.</summary>
    public static string? TagText(CatalogNewKind kind) => kind switch
    {
        CatalogNewKind.New => "New",
        CatalogNewKind.Updated => "Updated",
        _ => null,
    };
}
