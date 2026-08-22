namespace IcarusStarlink.Storage.Library;

/// <summary>The sidecar (.immmeta.json) content — library-only metadata not already present in the mod's own EXMOD file.</summary>
internal sealed class LibraryMeta
{
    public bool IsPinned { get; set; }
    public bool IsFavorite { get; set; }
    public string Notes { get; set; } = "";
    public DateTimeOffset ImportedAtUtc { get; set; }

    /// <summary>Set once, by the EXMOD editor's own Save action — unlike Pin/Favorite/Notes, never toggled directly in the Library UI.</summary>
    public bool IsLocallyEdited { get; set; }

    /// <summary>Where this mod came from ("Nexus"/"Database") — set once at import time, never changed afterward.</summary>
    public string? Source { get; set; }

    /// <summary>Real name/author/description/version fetched from the Nexus API for an opaque .pak, which has no embedded metadata of its own — see ILibraryRepository.SetNexusMetadata.</summary>
    public string? NexusName { get; set; }
    public string? NexusAuthor { get; set; }
    public string? NexusDescription { get; set; }
    public string? NexusVersion { get; set; }

    /// <summary>The real Nexus mod ID, set at import time for a Nexus-sourced entry — see LibraryEntry.NexusModId.</summary>
    public int? NexusModId { get; set; }

    /// <summary>The community catalog's own stable entry ID, set at Download &amp; extract time for a Database-sourced entry — see LibraryEntry.CatalogEntryId.</summary>
    public string? CatalogEntryId { get; set; }

    /// <summary>User-chosen display name, overriding the EXMOD's own declared name (or, for an opaque pak, NexusName/the pak filename) without touching the mod's real folder name, FileName, or underlying file content. Null = show the default name.</summary>
    public string? DisplayNameOverride { get; set; }

    /// <summary>Set at ImportPak time when a sibling ISL-Merged.txt manifest sits next to the pak being imported — see LibraryEntry.MergedPackModNames.</summary>
    public List<string>? MergedPackModNames { get; set; }
}
