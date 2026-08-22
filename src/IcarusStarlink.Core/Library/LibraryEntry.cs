namespace IcarusStarlink.Core.Library;

/// <summary>One imported mod in the Library — the EXMOD's own header fields plus library-only metadata (pin/favorite/notes) that lives in a sidecar, not the EXMOD itself.</summary>
public sealed class LibraryEntry
{
    /// <summary>The on-disk subfolder name under Extracted_Mods\ — the stable identity used for repository operations.</summary>
    public required string FolderName { get; set; }

    public required string Name { get; set; }
    public required string Author { get; set; }
    public required string Version { get; set; }
    public required string Description { get; set; }

    /// <summary>The EXMOD's own internal identifier (its "fileName" field), independent of FolderName — they can diverge if a folder-name collision forced a suffix at import time.</summary>
    public required string FileName { get; set; }

    public string? VariantGroup { get; set; }
    public string? Variant { get; set; }
    public int? VariantSort { get; set; }

    /// <summary>
    /// True for a prebuilt ".pak" imported directly (no .EXMOD, so no per-field diff data) — an
    /// opaque black-box mod the UI can list/pin/favorite/note, but can't browse files for, show a
    /// readme for, or open in the EXMOD editor.
    /// </summary>
    public bool IsOpaquePak { get; set; }

    public bool IsPinned { get; set; }
    public bool IsFavorite { get; set; }
    public string Notes { get; set; } = "";
    public DateTimeOffset ImportedAtUtc { get; set; }

    /// <summary>Where this mod came from — "Nexus" or "Database", set at import time by the code path that knows. Null for a manual/local import — shown as a small provenance badge, or no badge at all.</summary>
    public string? Source { get; set; }

    /// <summary>The real Nexus mod ID, set at import time for a Source == "Nexus" entry — needed to look up its current version for update-checking. Null for anything not Nexus-sourced.</summary>
    public int? NexusModId { get; set; }

    /// <summary>The community catalog's own stable entry ID, set at Download &amp; extract time for a Source == "Database" entry — the Database counterpart of NexusModId, so the Downloaded badge and update-checking survive a Rename the way name-matching can't. Null for older imports and anything not Database-sourced (those fall back to name-matching).</summary>
    public string? CatalogEntryId { get; set; }

    /// <summary>The raw user-chosen rename, when one exists — Name above already reflects it, but a consumer replacing this entry (Get update's delete-then-reimport) needs the override itself to re-apply, since it can't reconstruct "was Name overridden or natural?" from Name alone.</summary>
    public string? DisplayNameOverride { get; set; }

    /// <summary>Set once the EXMOD editor's own Save action runs against this mod — the ✎ glyph.</summary>
    public bool IsLocallyEdited { get; set; }

    /// <summary>
    /// Set when this opaque pak was imported alongside a sibling ISL-Merged.txt manifest — meaning
    /// it's a previously-Rebuild-and-Installed IcarusStarlink pak, re-imported as its own Library
    /// entry (RebuildService/InstallService's own established behavior since Phase 6.2). Holds the
    /// display Name of every mod folded into it, read straight from that manifest — drives Merge &amp;
    /// Install's queue rules ("skip mods already inside it", "pack can replace individuals") and the
    /// 📦 glyph. Null for every other entry, including a plain prebuilt pak with no such manifest.
    /// </summary>
    public IReadOnlyList<string>? MergedPackModNames { get; set; }
}
