using SkiaSharp;

namespace IcarusStarlink.PakIO.World;

/// <summary>
/// Says where a world's new picture or zone image differs from the one drawn before, by the cells
/// of a grid laid over it (the in-game grid when the world has one).
/// </summary>
public static class WorldImageCompare
{
    /// <summary>
    /// How far a pixel's red, green or blue may differ (0-255) and still count as the same. Both
    /// pictures are JPEGs made the same way, so untouched ground decodes alike; this is for the
    /// little that a changed neighbour smears across.
    /// </summary>
    public const int Tolerance = 24;

    /// <summary>The grid used for a world that has no in-game grid.</summary>
    public const int DefaultGrid = 8;

    /// <summary>
    /// The picture: null when it looks the same. Both are decoded the same way; a pixel differs when
    /// a colour is off by more than Tolerance, and a cell changed when at least a thousandth of its
    /// pixels differ. A picture that's new, gone, of another size or unreadable is "whole".
    /// </summary>
    public static WorldImageChange? Pictures(byte[]? before, byte[]? after, int columns, int rows)
    {
        const string what = "Picture";
        if (before is null && after is null)
        {
            return null;
        }

        if (before is null || after is null)
        {
            return new WorldImageChange { What = what, Whole = true, Note = before is null ? "new" : "gone" };
        }

        if (before.AsSpan().SequenceEqual(after))
        {
            return null;
        }

        using var old = Decode(before);
        using var now = Decode(after);
        if (old is null || now is null || old.Width != now.Width || old.Height != now.Height)
        {
            return new WorldImageChange { What = what, Whole = true };
        }

        var width = now.Width;
        var height = now.Height;
        (columns, rows) = Grid(columns, rows, width, height);
        var counts = new int[columns * rows];
        var a = old.GetPixelSpan();
        var b = now.GetPixelSpan();
        for (var y = 0; y < height; y++)
        {
            var cellRow = y * rows / height * columns;
            var rowA = a.Slice(y * old.RowBytes, width * 4);
            var rowB = b.Slice(y * now.RowBytes, width * 4);
            for (var x = 0; x < width; x++)
            {
                var i = x * 4;
                if (Math.Abs(rowA[i] - rowB[i]) > Tolerance || Math.Abs(rowA[i + 1] - rowB[i + 1]) > Tolerance || Math.Abs(rowA[i + 2] - rowB[i + 2]) > Tolerance)
                {
                    counts[cellRow + x * columns / width]++;
                }
            }
        }

        return Change(what, counts, columns, rows, width, height);
    }

    /// <summary>
    /// A zone image (lossless, so any pixel of another colour differs): null when it's the same. One
    /// that's new, gone or of another size is "whole".
    /// </summary>
    public static WorldImageChange? Layers(string what, WorldLayerPixels? before, WorldLayerPixels? after, int columns, int rows)
    {
        if (before is null && after is null)
        {
            return null;
        }

        if (before is null || after is null)
        {
            return new WorldImageChange { What = what, Whole = true, Note = before is null ? "new" : "gone" };
        }

        if (before.Size != after.Size)
        {
            return new WorldImageChange { What = what, Whole = true };
        }

        var size = after.Size;
        (columns, rows) = Grid(columns, rows, size, size);
        var counts = new int[columns * rows];
        for (var y = 0; y < size; y++)
        {
            var cellRow = y * rows / size * columns;
            for (var x = 0; x < size; x++)
            {
                if (before.Pixels[y * size + x] != after.Pixels[y * size + x])
                {
                    counts[cellRow + x * columns / size]++;
                }
            }
        }

        return Change(what, counts, columns, rows, size, size);
    }

    /// <summary>The grid to count in: the world's own, or DefaultGrid; never finer than the image's pixels.</summary>
    private static (int Columns, int Rows) Grid(int columns, int rows, int width, int height) =>
        columns > 0 && rows > 0
            ? (Math.Min(columns, width), Math.Min(rows, height))
            : (Math.Min(DefaultGrid, width), Math.Min(DefaultGrid, height));

    private static WorldImageChange? Change(string what, int[] counts, int columns, int rows, int width, int height)
    {
        // A cell changed when at least a thousandth of its pixels differ (one pixel in a tiny image).
        var enough = Math.Max(1, width / columns * (height / rows) / 1000);
        var cells = Enumerable.Range(0, counts.Length).Where(i => counts[i] >= enough).ToList();
        return cells.Count == 0 ? null
            : cells.Count == counts.Length ? new WorldImageChange { What = what, Whole = true }
            : new WorldImageChange { What = what, Columns = columns, Rows = rows, Cells = cells };
    }

    private static SKBitmap? Decode(byte[] encoded)
    {
        try
        {
            var bounds = SKBitmap.DecodeBounds(encoded);
            return bounds.Width <= 0 || bounds.Height <= 0
                ? null
                : SKBitmap.Decode(encoded, new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
