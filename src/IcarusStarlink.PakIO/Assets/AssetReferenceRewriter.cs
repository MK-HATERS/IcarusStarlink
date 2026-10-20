using UAssetAPI;
using UAssetAPI.UnrealTypes;

namespace IcarusStarlink.PakIO.Assets;

/// <summary>A cooked file with its references pointed elsewhere, checked and ready to write over the old one.</summary>
public sealed record RewrittenAsset(string AssetPath, byte[] Header, byte[]? Exports, int Renamed);

/// <summary>
/// Points a mod's cooked file at the game's new place for a package it uses (the game moved
/// M_ITM_CableHand from ASS/DEP to ASS/ITM): the package's name in the file's name map is
/// rewritten, so every import of it, and any soft reference to an object in it, follows. The
/// header changes length, so UAssetAPI writes the file (it moves every offset that points past
/// the name map). Refused, leaving the file as it is, unless every check holds:
/// - nothing after the objects' data holds a position in the file (a longer header would leave it
///   pointing at the wrong bytes);
/// - UAssetAPI writes the file back byte for byte before anything is changed;
/// - read again after the change, every import is the same but the moved package.
/// </summary>
public static class AssetReferenceRewriter
{
    /// <param name="assetPath">The .uasset or .umap (its .uexp beside it is part of it).</param>
    /// <param name="moves">Old package path → new one ("/Game/ASS/DEP/M_X" → "/Game/ASS/ITM/M_X").</param>
    /// <param name="why">Why it was refused.</param>
    public static RewrittenAsset? TryRewrite(string assetPath, IReadOnlyDictionary<string, string> moves, out string? why)
    {
        why = null;
        var exportsPath = Path.ChangeExtension(assetPath, ".uexp");
        var header = File.ReadAllBytes(assetPath);
        var exports = File.Exists(exportsPath) ? File.ReadAllBytes(exportsPath) : null;
        if (UnrealPackageHeader.TryRead(header) is not { } before)
        {
            why = "it isn't a cooked package Starlink can read";
            return null;
        }

        if (!before.ExportsFillPackage(header.LongLength + (exports?.LongLength ?? 0)))
        {
            why = "it holds data after its objects whose place in the file would move";
            return null;
        }

        var work = Path.Combine(Path.GetTempPath(), "IcarusStarlink-repoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            UAsset asset;
            try
            {
                asset = new UAsset(assetPath, EngineVersion.VER_UE4_27);
            }
            catch (Exception ex)
            {
                why = $"UAssetAPI couldn't read it ({ex.Message})";
                return null;
            }

            // Written back unchanged first: anything UAssetAPI wouldn't keep exactly is a reason not to touch the file.
            var same = Write(asset, Path.Combine(work, "same" + Path.GetExtension(assetPath)), out var sameHeader, out var sameExports, out why);
            if (!same)
            {
                return null;
            }

            if (!sameHeader.AsSpan().SequenceEqual(header) || !(sameExports ?? []).AsSpan().SequenceEqual(exports ?? []))
            {
                why = "UAssetAPI doesn't write it back exactly as it is";
                return null;
            }

            var renamed = 0;
            var names = asset.GetNameMapIndexList();
            for (var i = 0; i < names.Count; i++)
            {
                if (Moved(names[i].Value, moves) is { } moved)
                {
                    asset.SetNameReference(i, new FString(moved));
                    renamed++;
                }
            }

            if (renamed == 0)
            {
                why = "it doesn't name the package";
                return null;
            }

            if (!Write(asset, Path.Combine(work, "changed" + Path.GetExtension(assetPath)), out var newHeader, out var newExports, out why))
            {
                return null;
            }

            if (UnrealPackageHeader.TryRead(newHeader) is not { } after || !SameButMoved(before, after, moves)
                || !after.ExportsFillPackage(newHeader.LongLength + (newExports?.LongLength ?? 0)))
            {
                why = "read again after the change, it didn't come out as expected";
                return null;
            }

            return new RewrittenAsset(assetPath, newHeader, newExports, renamed);
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (Exception)
            {
                // Only a temp folder.
            }
        }
    }

    /// <summary>Writes the rewritten file over the old one (each part through a temp file, so a failed write leaves the old one whole).</summary>
    public static void Commit(RewrittenAsset rewritten)
    {
        Replace(rewritten.AssetPath, rewritten.Header);
        if (rewritten.Exports is not null)
        {
            Replace(Path.ChangeExtension(rewritten.AssetPath, ".uexp"), rewritten.Exports);
        }
    }

    /// <summary>The name with a moved package in it pointed to the new place: the package itself, or an object path inside it ("/Game/A/M_X.M_X").</summary>
    private static string? Moved(string name, IReadOnlyDictionary<string, string> moves)
    {
        foreach (var (from, to) in moves)
        {
            if (string.Equals(name, from, StringComparison.OrdinalIgnoreCase))
            {
                return to;
            }

            if (name.Length > from.Length && name.StartsWith(from, StringComparison.OrdinalIgnoreCase) && name[from.Length] is '.' or ':')
            {
                return to + name[from.Length..];
            }
        }

        return null;
    }

    /// <summary>Every import as it was, but those of a moved package now in its new place.</summary>
    private static bool SameButMoved(UnrealPackageHeader before, UnrealPackageHeader after, IReadOnlyDictionary<string, string> moves)
    {
        if (before.Imports.Count != after.Imports.Count || before.Exports.Count != after.Exports.Count)
        {
            return false;
        }

        for (var i = 0; i < before.Imports.Count; i++)
        {
            var was = before.Imports[i];
            var now = after.Imports[i];
            var expected = Moved(was.ObjectName, moves) ?? was.ObjectName;
            if (!string.Equals(now.ObjectName, expected, StringComparison.Ordinal) || now.OuterIndex != was.OuterIndex
                || !string.Equals(now.ClassName, was.ClassName, StringComparison.Ordinal) || !string.Equals(now.ClassPackage, Moved(was.ClassPackage, moves) ?? was.ClassPackage, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Write(UAsset asset, string path, out byte[] header, out byte[]? exports, out string? why)
    {
        header = [];
        exports = null;
        why = null;
        try
        {
            asset.Write(path);
        }
        catch (Exception ex)
        {
            why = $"UAssetAPI couldn't write it ({ex.Message})";
            return false;
        }

        header = File.ReadAllBytes(path);
        var exportsPath = Path.ChangeExtension(path, ".uexp");
        exports = File.Exists(exportsPath) ? File.ReadAllBytes(exportsPath) : null;
        return true;
    }

    private static void Replace(string path, byte[] bytes)
    {
        var temp = path + ".starlink-tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }
}
