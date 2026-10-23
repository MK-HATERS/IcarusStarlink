using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace IcarusStarlink.PakIO.Assets;

/// <summary>A cooked curve ready for a mod folder: its two files, and the package path the game finds it by.</summary>
public sealed record WrittenCurve(string PackagePath, byte[] Asset, byte[] Exports);

/// <summary>
/// Makes a mod's own growth curve from one of the game's cooked CurveFloats: the same file with
/// its keys replaced and its package renamed, so no game asset is replaced (the approach the
/// user's MkHaters Animal Talents 2.0 uses, there with byte offsets and a rename tool; here with
/// UAssetAPI, which rewrites every size the new keys change). What happens past the last key
/// comes with the template: the game's blueprint curve keeps rising (PostInfinityExtrap Linear),
/// its talent curve stays flat, so the caller picks the template that behaves as wanted.
/// Read back after writing: refused unless the new file holds the new name and exactly the keys.
/// </summary>
public static class CurveAssetWriter
{
    /// <param name="templateAsset">The game curve's .uasset.</param>
    /// <param name="templateExports">Its .uexp.</param>
    /// <param name="keys">(level, value) pairs, linear between them.</param>
    /// <param name="packagePath">"/Game/Starlink/PlayerPoints/C_Starlink_PlayerTalentGrowth".</param>
    public static WrittenCurve Write(byte[] templateAsset, byte[] templateExports, IReadOnlyList<(float Time, float Value)> keys, string packagePath)
    {
        if (keys.Count == 0)
        {
            throw new ArgumentException("A curve needs at least one key.", nameof(keys));
        }

        var work = Path.Combine(Path.GetTempPath(), "IcarusStarlink", "CurveWriter", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var inPath = Path.Combine(work, "in.uasset");
            File.WriteAllBytes(inPath, templateAsset);
            File.WriteAllBytes(Path.ChangeExtension(inPath, ".uexp"), templateExports);

            var asset = new UAsset(inPath, EngineVersion.VER_UE4_27);
            var export = asset.Exports.OfType<NormalExport>().FirstOrDefault()
                ?? throw new InvalidDataException("The template has no curve in it.");
            var curve = export.Data.OfType<StructPropertyData>().FirstOrDefault(p => p.Name.Value.Value == "FloatCurve")
                ?? throw new InvalidDataException("The template isn't a CurveFloat.");
            var array = curve.Value.OfType<ArrayPropertyData>().FirstOrDefault(p => p.Name.Value.Value == "Keys")
                ?? throw new InvalidDataException("The template curve has no keys.");
            var model = array.Value.OfType<StructPropertyData>().FirstOrDefault()
                ?? throw new InvalidDataException("The template curve has no keys.");

            var newKeys = new PropertyData[keys.Count];
            for (var i = 0; i < keys.Count; i++)
            {
                var copy = (StructPropertyData)model.Clone();
                var key = copy.Value.OfType<RichCurveKeyPropertyData>().Single();
                var value = key.Value;
                // Interpolation stays the template's: the game's player curves are all linear.
                value.Time = keys[i].Time;
                value.Value = keys[i].Value;
                value.ArriveTangent = 0;
                value.LeaveTangent = 0;
                key.Value = value;
                newKeys[i] = copy;
            }

            array.Value = newKeys;
            Rename(asset, packagePath);

            var outPath = Path.Combine(work, "out.uasset");
            asset.Write(outPath);
            var written = new WrittenCurve(packagePath, File.ReadAllBytes(outPath), File.ReadAllBytes(Path.ChangeExtension(outPath, ".uexp")));
            Verify(outPath, packagePath, keys);
            return written;
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>The package's path and its object's name, the way tools/rename does it for the animal curves.</summary>
    private static void Rename(UAsset asset, string packagePath)
    {
        var names = asset.GetNameMapIndexList();
        var oldPath = names.Select(n => n.Value).Single(n => n.StartsWith("/Game/", StringComparison.Ordinal));
        var oldName = oldPath[(oldPath.LastIndexOf('/') + 1)..];
        var newName = packagePath[(packagePath.LastIndexOf('/') + 1)..];
        for (var i = 0; i < names.Count; i++)
        {
            if (names[i].Value == oldPath)
            {
                asset.SetNameReference(i, new FString(packagePath));
            }
            else if (names[i].Value == oldName)
            {
                asset.SetNameReference(i, new FString(newName));
            }
        }
    }

    private static void Verify(string path, string packagePath, IReadOnlyList<(float Time, float Value)> keys)
    {
        var back = new UAsset(path, EngineVersion.VER_UE4_27);
        var newName = packagePath[(packagePath.LastIndexOf('/') + 1)..];
        if (!back.GetNameMapIndexList().Any(n => n.Value == packagePath) || back.Exports[0].ObjectName.Value.Value != newName)
        {
            throw new InvalidDataException("The new curve didn't keep its new name.");
        }

        var read = ((NormalExport)back.Exports[0]).Data.OfType<StructPropertyData>().Single(p => p.Name.Value.Value == "FloatCurve")
            .Value.OfType<ArrayPropertyData>().Single(p => p.Name.Value.Value == "Keys").Value
            .Select(k => ((StructPropertyData)k).Value.OfType<RichCurveKeyPropertyData>().Single().Value)
            .Select(k => (k.Time, k.Value))
            .ToList();
        if (!read.SequenceEqual(keys))
        {
            throw new InvalidDataException("The new curve's keys didn't read back the same.");
        }
    }
}
