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
}
