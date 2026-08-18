namespace IcarusStarlink.PakIO.Container;

/// <summary>
/// One non-EXMOD file inside an EXMODZ package — already-compiled binary Unreal assets
/// (.uasset/.uexp/.ubulk), a readme, a preview image. We never parse these, only copy them
/// through verbatim, per the real EXMODZ samples inspected during planning.
/// </summary>
public sealed record ExmodAssetEntry(string RelativePath, byte[] Content)
{
    // A record's auto-generated equality compares byte[] by reference, not content, which breaks
    // the value semantics records are meant to have — override both explicitly. Note this makes
    // Equals/GetHashCode O(Content.Length): fine for the current "compare two entries" usage, but
    // worth knowing before putting these in a HashSet/Dictionary or calling .Distinct() over a
    // large Assets list, since each hash/compare then costs up to MaxAssetEntryBytes per asset.
    public bool Equals(ExmodAssetEntry? other) =>
        other is not null && RelativePath == other.RelativePath && Content.AsSpan().SequenceEqual(other.Content);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(RelativePath);
        hash.AddBytes(Content);
        return hash.ToHashCode();
    }
}
