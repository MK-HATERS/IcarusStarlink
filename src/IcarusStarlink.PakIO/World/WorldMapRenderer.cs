using System.Security.Cryptography;
using System.Text;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse_Conversion.Textures;
using IcarusStarlink.PakIO.Assets;
using SkiaSharp;

namespace IcarusStarlink.PakIO.World;

/// <summary>Where a map's picture came from.</summary>
public enum WorldPictureSource
{
    /// <summary>The game's own map picture (D_WorldData MapTextures).</summary>
    Picture,

    /// <summary>The game has no picture for it yet: drawn as a relief map from its height tiles.</summary>
    Relief,

    /// <summary>Neither is in the game.</summary>
    None,
}

/// <summary>A zone image's pixels (0xRRGGBB, row by row), Size x Size.</summary>
public sealed record WorldLayerPixels(int Size, uint[] Pixels)
{
    /// <summary>As a PNG: lossless, so every zone keeps its exact colour.</summary>
    public byte[] ToPng()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(Size, Size, SKColorType.Bgra8888, SKAlphaType.Opaque));
        bitmap.Pixels = [.. Pixels.Select(p => new SKColor((byte)(p >> 16), (byte)(p >> 8), (byte)p))];
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Back from ToPng; null when it isn't a square image.</summary>
    public static WorldLayerPixels? FromPng(byte[] png)
    {
        using var bitmap = SKBitmap.Decode(png);
        if (bitmap is null || bitmap.Width != bitmap.Height || bitmap.Width == 0)
        {
            return null;
        }

        return new WorldLayerPixels(bitmap.Width, [.. bitmap.Pixels.Select(c => ((uint)c.Red << 16) | ((uint)c.Green << 8) | c.Blue)]);
    }
}

/// <summary>Draws a world's picture and reads its zone images from the game's paks.</summary>
public interface IWorldMapRenderer
{
    /// <summary>The map's picture as a JPEG, or null with Source None when the game has neither picture nor height tiles.</summary>
    (byte[]? Jpeg, WorldPictureSource Source) RenderPicture(WorldMapInfo map);

    /// <summary>A zone image (a GameplayTexture's TextureData); null when the game doesn't have it.</summary>
    WorldLayerPixels? ReadLayer(string heatmapAssetPath);

    /// <summary>One line naming where every file the map is drawn from sits in the paks: changes when a game update replaces any of them.</summary>
    string Stamp(WorldMapInfo map);
}

/// <summary>
/// How the game's map tiles fit together. Found on Olympus by matching tile edges, then checked
/// against the biome image (sand under desert, snow under arctic): tile _xX_yY goes to column X,
/// row (n-1-Y), and each tile is turned a quarter clockwise.
/// </summary>
public static class WorldTilePlacement
{
    /// <summary>The tiles per side (the largest x or y + 1).</summary>
    public static int GridSize(IReadOnlyList<WorldTile> tiles) =>
        tiles.Count == 0 ? 0 : Math.Max(tiles.Max(t => t.X), tiles.Max(t => t.Y)) + 1;

    public static (int Column, int Row) Place(int x, int y, int gridSize) => (x, gridSize - 1 - y);

    /// <summary>Which pixel of the tile shows at (u, v) once it's turned: a quarter clockwise.</summary>
    public static (int X, int Y) SourcePixel(int u, int v, int tileSize) => (v, tileSize - 1 - u);
}

public sealed class WorldMapRenderer(IBaseGameContentProvider game) : IWorldMapRenderer
{
    /// <summary>A map picture is never drawn larger than this per side (Olympus: 8 tiles of 512 = 4096).</summary>
    private const int MaxPictureSize = 4096;

    public (byte[]? Jpeg, WorldPictureSource Source) RenderPicture(WorldMapInfo map)
    {
        if (map.PictureTiles.Count > 0 && map.PictureTiles.All(t => game.HasAsset(t.AssetPath) == true)
            && StitchPicture(map.PictureTiles) is { } picture)
        {
            using (picture)
            {
                return (Encode(picture), WorldPictureSource.Picture);
            }
        }

        if (map.HeightTiles.Count > 0 && map.HeightTiles.All(t => game.HasAsset(t.AssetPath) == true)
            && DrawRelief(map.HeightTiles) is { } relief)
        {
            using (relief)
            {
                return (Encode(relief), WorldPictureSource.Relief);
            }
        }

        return (null, WorldPictureSource.None);
    }

    public WorldLayerPixels? ReadLayer(string heatmapAssetPath) =>
        game.TryLoadExportAndProject<UObject, WorldLayerPixels>(heatmapAssetPath, texture =>
        {
            var colors = texture.GetOrDefault<FColor[]>("TextureData");
            if (colors is null || colors.Length == 0)
            {
                return null!;
            }

            var size = (int)Math.Round(Math.Sqrt(colors.Length));
            if (size * size != colors.Length || size > MaxPictureSize)
            {
                return null!;
            }

            var pixels = new uint[colors.Length];
            for (var i = 0; i < colors.Length; i++)
            {
                pixels[i] = ((uint)colors[i].R << 16) | ((uint)colors[i].G << 8) | colors[i].B;
            }

            return new WorldLayerPixels(size, pixels);
        });

    public string Stamp(WorldMapInfo map)
    {
        var files = map.PictureTiles.Concat(map.HeightTiles).Select(t => t.AssetPath)
            .Concat(map.Layers.Select(l => l.HeatmapAssetPath).OfType<string>())
            .Order(StringComparer.OrdinalIgnoreCase);
        var text = string.Join("\n", files.Select(f => $"{f}={game.FileStamp(f)}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    }

    private SKBitmap? StitchPicture(IReadOnlyList<WorldTile> tiles)
    {
        var n = WorldTilePlacement.GridSize(tiles);
        SKBitmap? canvas = null;
        var tileSize = 0;
        foreach (var tile in tiles)
        {
            using var bitmap = DecodeTile(tile.AssetPath);
            if (bitmap is null || bitmap.Width != bitmap.Height || (canvas is not null && bitmap.Width != tileSize))
            {
                canvas?.Dispose();
                return null;
            }

            if (canvas is null)
            {
                tileSize = bitmap.Width;
                if (tileSize * n > MaxPictureSize * 2)
                {
                    return null;
                }

                canvas = new SKBitmap(tileSize * n, tileSize * n);
            }

            var (column, row) = WorldTilePlacement.Place(tile.X, tile.Y, n);
            using var turned = new SKBitmap(tileSize, tileSize);
            var source = bitmap.Pixels;
            var target = new SKColor[tileSize * tileSize];
            for (var v = 0; v < tileSize; v++)
            {
                for (var u = 0; u < tileSize; u++)
                {
                    var (sx, sy) = WorldTilePlacement.SourcePixel(u, v, tileSize);
                    target[v * tileSize + u] = source[sy * tileSize + sx];
                }
            }

            turned.Pixels = target;
            using var draw = new SKCanvas(canvas);
            draw.DrawBitmap(turned, column * tileSize, row * tileSize);
        }

        if (canvas is not null && canvas.Width > MaxPictureSize)
        {
            var smaller = canvas.Resize(new SKImageInfo(MaxPictureSize, MaxPictureSize), SKFilterQuality.Medium);
            canvas.Dispose();
            return smaller;
        }

        return canvas;
    }

    private SKBitmap? DecodeTile(string assetPath)
    {
        var decoded = game.TryLoadExportAndProject<UTexture2D, CTexture>(assetPath, t => TextureDecoder.Decode(t, ETexturePlatform.DesktopMobile)!);
        if (decoded is null || !UassetTexturePngEncoder.HasSaneDimensions(decoded.Width, decoded.Height, decoded.Data?.Length ?? 0))
        {
            return null;
        }

        return TextureEncoder.ToSkBitmap(decoded);
    }

    /// <summary>
    /// The height tiles (half-float RGBA, height in red) drawn as a shaded relief map: lit from the
    /// top left, tinted low to high. The game's own height tiles stop at the largest half-float
    /// (65504), so the highest ground comes out flat.
    /// </summary>
    private SKBitmap? DrawRelief(IReadOnlyList<WorldTile> tiles)
    {
        var n = WorldTilePlacement.GridSize(tiles);
        float[]? heights = null;
        var tileSize = 0;
        foreach (var tile in tiles)
        {
            var decoded = game.TryLoadExportAndProject<UTexture2D, CTexture>(tile.AssetPath, t => TextureDecoder.Decode(t, ETexturePlatform.DesktopMobile)!);
            if (decoded?.Data is not { } data || decoded.Width != decoded.Height || decoded.Width * decoded.Width * 8 != data.Length
                || (heights is not null && decoded.Width != tileSize))
            {
                return null;
            }

            if (heights is null)
            {
                tileSize = decoded.Width;
                if (tileSize * n > MaxPictureSize)
                {
                    return null;
                }

                heights = new float[tileSize * n * tileSize * n];
            }

            var size = tileSize * n;
            var (column, row) = WorldTilePlacement.Place(tile.X, tile.Y, n);
            for (var v = 0; v < tileSize; v++)
            {
                for (var u = 0; u < tileSize; u++)
                {
                    var (sx, sy) = WorldTilePlacement.SourcePixel(u, v, tileSize);
                    heights[(row * tileSize + v) * size + column * tileSize + u] = (float)BitConverter.ToHalf(data, (sy * tileSize + sx) * 8);
                }
            }
        }

        return heights is null ? null : ShadeRelief(heights, tileSize * n);
    }

    internal static SKBitmap ShadeRelief(float[] heights, int size)
    {
        var low = heights.Where(float.IsFinite).DefaultIfEmpty(0).Min();
        var high = heights.Where(float.IsFinite).DefaultIfEmpty(1).Max();
        var range = Math.Max(1f, high - low);
        var steep = range / size * 12f;
        float H(int x, int y) => heights[Math.Clamp(y, 0, size - 1) * size + Math.Clamp(x, 0, size - 1)] is var h && float.IsFinite(h) ? h : high;

        var pixels = new SKColor[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var nx = -(H(x + 1, y) - H(x - 1, y)) / steep;
                var ny = -(H(x, y + 1) - H(x, y - 1)) / steep;
                var length = MathF.Sqrt(nx * nx + ny * ny + 1);
                var light = Math.Clamp((-0.6f * nx - 0.6f * ny + 0.55f) / length + 0.25f, 0f, 1f);
                var z = (H(x, y) - low) / range;
                var (r, g, b) = z < 0.08f ? (40, 90, 110) : z < 0.4f ? (90, 110, 70) : z < 0.7f ? (125, 110, 85) : (190, 190, 195);
                pixels[y * size + x] = new SKColor((byte)(r * light), (byte)(g * light), (byte)(b * light));
            }
        }

        var bitmap = new SKBitmap(size, size);
        bitmap.Pixels = pixels;
        return bitmap;
    }

    private static byte[] Encode(SKBitmap bitmap)
    {
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 88);
        return data.ToArray();
    }
}
