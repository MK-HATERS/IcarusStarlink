namespace IcarusStarlink.PakIO.Assets;

/// <summary>
/// A pak entry as the game's files list it, without reading the file. The game's pak index keeps no
/// per-file hash that's loaded, so its sizes and compression blocks stand in for its contents.
/// </summary>
/// <param name="PakName">The pak it sits in.</param>
/// <param name="Offset">Where it starts in that pak.</param>
/// <param name="CompressionMethod">How it's compressed ("None", "Zlib", "Oodle").</param>
/// <param name="Blocks">Its compression blocks, as start and end positions.</param>
public sealed record PakEntryShape(
    string PakName, long Offset, long UncompressedSize, long CompressedSize, string CompressionMethod, IReadOnlyList<(long Start, long End)> Blocks);

public static class PakContentStamp
{
    /// <summary>
    /// One line that changes when the file's contents do and stays the same when the game only
    /// repacks it: its size, its compressed size, how it's compressed and each compression block's
    /// length. Never the pak's name or where the file or its blocks sit in it: a game update moves
    /// those for files it didn't touch.
    /// </summary>
    public static string Of(string extension, PakEntryShape entry) =>
        $"{extension}:{entry.UncompressedSize}:{entry.CompressedSize}:{entry.CompressionMethod}:{string.Join(',', entry.Blocks.Select(b => b.End - b.Start))}";
}
