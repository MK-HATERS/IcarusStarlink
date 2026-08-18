namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// The parsed contents of an .EXMOD file's JSON — confirmed against two real downloaded
/// EXMODZ samples plus AgentKush/icarus-modinfo-validator's schema constants during planning.
/// Required fields per that validator: name/author/version/description/fileName. Optional:
/// week/Level2/Rows. variantGroup/variant/variantSort are documented by the source spec
/// (icarusworkshop.txt) but weren't present in either sample we inspected.
/// </summary>
public sealed class ExmodPackage
{
    public required string Name { get; set; }
    public required string Author { get; set; }
    public required string Version { get; set; }
    public required string Description { get; set; }
    public required string FileName { get; set; }

    public string? ImageUrl { get; set; }
    public string? ReadmeUrl { get; set; }

    /// <summary>Observed as the literal string "True" in real samples, not a JSON boolean.</summary>
    public string? Level2 { get; set; }

    public string? Week { get; set; }
    public string? VariantGroup { get; set; }
    public string? Variant { get; set; }
    public int? VariantSort { get; set; }

    public List<ExmodFileRow> Rows { get; set; } = [];
}
