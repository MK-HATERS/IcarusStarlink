using System.Buffers.Binary;
using System.Text;

namespace IcarusStarlink.PakIO.Assets;

/// <summary>One entry of a package's import table: an object another package has (a class, a mesh, a material…).</summary>
/// <param name="OuterIndex">0 for a package itself; otherwise -(n+1) for import n, or n+1 for export n.</param>
public sealed record PackageImport(string ClassPackage, string ClassName, int OuterIndex, string ObjectName);

/// <summary>One entry of a package's export table: an object the package holds itself.</summary>
/// <param name="ClassIndex">The object's class: -(n+1) for import n, n+1 for export n, 0 for UClass.</param>
public sealed record PackageExport(int ClassIndex, int OuterIndex, string ObjectName, long SerialSize, long SerialOffset);

/// <summary>
/// The header of a cooked UE4.27 package (.uasset or .umap): its name map, imports and exports —
/// what the package needs from other packages and what it holds itself, without reading any of
/// the objects. Icarus's cooked files are unversioned (FileVersionUE4 0), read as 4.27; the game
/// stops at load when a package imports an object no package has (FAsyncPackage::LinkImport), so
/// this is what checking a mod's references against the game reads. Read-only, no UE library.
/// </summary>
public sealed class UnrealPackageHeader
{
    private const uint PackageTag = 0x9E2A83C1;
    private const uint FilterEditorOnly = 0x80000000;

    /// <summary>UE 4.27's file version; an unversioned cooked package is this.</summary>
    private const int Ue427 = 522;

    private const int ExportSize = 104;

    private UnrealPackageHeader(IReadOnlyList<string> names, IReadOnlyList<PackageImport> imports, IReadOnlyList<PackageExport> exports, int totalHeaderSize)
    {
        Names = names;
        Imports = imports;
        Exports = exports;
        TotalHeaderSize = totalHeaderSize;
    }

    public IReadOnlyList<string> Names { get; }

    public IReadOnlyList<PackageImport> Imports { get; }

    public IReadOnlyList<PackageExport> Exports { get; }

    /// <summary>Where the objects' data starts: the .uasset's length when the package is split into .uasset + .uexp.</summary>
    public int TotalHeaderSize { get; }

    /// <summary>The header of a package, or null when the bytes aren't a cooked UE4 package this can read.</summary>
    public static UnrealPackageHeader? TryRead(ReadOnlySpan<byte> data)
    {
        try
        {
            return Read(data);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidDataException or DecoderFallbackException or OverflowException)
        {
            return null;
        }
    }

    private static UnrealPackageHeader? Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 32 || BinaryPrimitives.ReadUInt32LittleEndian(data) != PackageTag)
        {
            return null;
        }

        var at = 4;
        var legacy = Int(data, ref at);
        if (legacy is > -4 or < -7)
        {
            return null; // not a UE4 package (UE5 is -8 and later, with one more version field; UE3 positive)
        }

        if (legacy != -4)
        {
            at += 4; // LegacyUE3Version
        }

        var version = Int(data, ref at);
        at += 4; // licensee version
        if (version == 0)
        {
            version = Ue427;
        }

        if (legacy <= -2)
        {
            var customVersions = Int(data, ref at);
            if (customVersions is < 0 or > 1000)
            {
                return null;
            }

            at += customVersions * 20;
        }

        var totalHeaderSize = Int(data, ref at);
        _ = String(data, ref at); // folder name
        var flags = (uint)Int(data, ref at);
        var nameCount = Int(data, ref at);
        var nameOffset = Int(data, ref at);
        var editorOnlyFiltered = (flags & FilterEditorOnly) != 0;
        if (version >= 516 && !editorOnlyFiltered)
        {
            _ = String(data, ref at); // localization id
        }

        if (version >= 459)
        {
            at += 8; // gatherable text data count + offset
        }

        var exportCount = Int(data, ref at);
        var exportOffset = Int(data, ref at);
        var importCount = Int(data, ref at);
        var importOffset = Int(data, ref at);
        if (nameCount is < 0 or > 1_000_000 || importCount is < 0 or > 1_000_000 || exportCount is < 0 or > 1_000_000)
        {
            return null;
        }

        var names = new string[nameCount];
        var p = nameOffset;
        for (var i = 0; i < nameCount; i++)
        {
            names[i] = String(data, ref p);
            if (version >= 504)
            {
                p += 4; // the two name hashes
            }
        }

        string Name(int index, int number)
        {
            if (index < 0 || index >= names.Length)
            {
                throw new InvalidDataException("A name index lies outside the name map.");
            }

            return number == 0 ? names[index] : $"{names[index]}_{number - 1}";
        }

        var imports = new PackageImport[importCount];
        p = importOffset;
        for (var i = 0; i < importCount; i++)
        {
            var classPackage = Name(Int(data, ref p), Int(data, ref p));
            var className = Name(Int(data, ref p), Int(data, ref p));
            var outer = Int(data, ref p);
            var objectName = Name(Int(data, ref p), Int(data, ref p));
            if (!editorOnlyFiltered && version >= 520)
            {
                p += 8; // package name (editor data only)
            }

            imports[i] = new PackageImport(classPackage, className, outer, objectName);
        }

        // Exports only in the layout every 4.27 cook writes; an older one keeps its imports.
        var exports = new List<PackageExport>(version >= 511 ? exportCount : 0);
        if (version >= 511)
        {
            p = exportOffset;
            for (var i = 0; i < exportCount; i++)
            {
                var start = p;
                var classIndex = Int(data, ref p);
                p += 4; // super
                p += 4; // template
                var outer = Int(data, ref p);
                var objectName = Name(Int(data, ref p), Int(data, ref p));
                p += 4; // object flags
                var serialSize = Long(data, ref p);
                var serialOffset = Long(data, ref p);
                exports.Add(new PackageExport(classIndex, outer, objectName, serialSize, serialOffset));
                p = start + ExportSize;
            }
        }

        return new UnrealPackageHeader(names, imports, exports, totalHeaderSize);
    }

    /// <summary>The package an import comes from ("/Game/ASS/DEP/M_X", "/Script/Engine"), or null when its chain of outers is broken.</summary>
    public string? ImportPackage(int importIndex)
    {
        for (var guard = 0; guard < 64 && importIndex >= 0 && importIndex < Imports.Count; guard++)
        {
            var import = Imports[importIndex];
            if (import.OuterIndex == 0)
            {
                return import.ObjectName;
            }

            if (import.OuterIndex > 0)
            {
                return null; // an import inside an export: not something another package has
            }

            importIndex = -import.OuterIndex - 1;
        }

        return null;
    }

    /// <summary>
    /// An imported object's place inside its package, outermost first, joined with dots
    /// ("WT_CaveLight_C", "WT_CaveLight_C.PointLight_GEN_VARIABLE"); null for a package itself.
    /// Compared with <see cref="ExportPath"/> of the package that should have it.
    /// </summary>
    public string? ImportPath(int importIndex)
    {
        var parts = new List<string>();
        for (var guard = 0; guard < 64 && importIndex >= 0 && importIndex < Imports.Count; guard++)
        {
            var import = Imports[importIndex];
            if (import.OuterIndex == 0)
            {
                if (parts.Count == 0)
                {
                    return null;
                }

                parts.Reverse();
                return string.Join('.', parts);
            }

            if (import.OuterIndex > 0)
            {
                return null;
            }

            parts.Add(import.ObjectName);
            importIndex = -import.OuterIndex - 1;
        }

        return null;
    }

    /// <summary>An exported object's place inside this package, outermost first, joined with dots; null when its chain of outers is broken.</summary>
    public string? ExportPath(int exportIndex)
    {
        var parts = new List<string>();
        for (var guard = 0; guard < 64 && exportIndex >= 0 && exportIndex < Exports.Count; guard++)
        {
            var export = Exports[exportIndex];
            parts.Add(export.ObjectName);
            if (export.OuterIndex == 0)
            {
                parts.Reverse();
                return string.Join('.', parts);
            }

            if (export.OuterIndex < 0)
            {
                return null;
            }

            exportIndex = export.OuterIndex - 1;
        }

        return null;
    }

    /// <summary>The class an export is an instance of, by name ("BlueprintGeneratedClass", "Material").</summary>
    public string ExportClass(int exportIndex)
    {
        var classIndex = Exports[exportIndex].ClassIndex;
        return classIndex switch
        {
            < 0 when -classIndex - 1 < Imports.Count => Imports[-classIndex - 1].ObjectName,
            > 0 when classIndex - 1 < Exports.Count => Exports[classIndex - 1].ObjectName,
            _ => "Class",
        };
    }

    /// <summary>Every object this package holds: its place inside the package → its class.</summary>
    public IReadOnlyDictionary<string, string> ExportedObjects()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < Exports.Count; i++)
        {
            if (ExportPath(i) is { } path)
            {
                result.TryAdd(path, ExportClass(i));
            }
        }

        return result;
    }

    /// <summary>
    /// Whether the objects' data fills the rest of the package exactly (it ends with the package
    /// tag): nothing after them that holds a position in the file, which a longer or shorter
    /// header would leave pointing at the wrong bytes. <paramref name="packageLength"/> is the
    /// .uasset and .uexp together.
    /// </summary>
    public bool ExportsFillPackage(long packageLength)
    {
        if (Exports.Count == 0)
        {
            return packageLength - 4 <= TotalHeaderSize;
        }

        var end = Exports.Max(e => e.SerialOffset + e.SerialSize);
        return end == packageLength - 4;
    }

    private static int Int(ReadOnlySpan<byte> data, ref int at)
    {
        var value = BinaryPrimitives.ReadInt32LittleEndian(data[at..]);
        at += 4;
        return value;
    }

    private static long Long(ReadOnlySpan<byte> data, ref int at)
    {
        var value = BinaryPrimitives.ReadInt64LittleEndian(data[at..]);
        at += 8;
        return value;
    }

    /// <summary>An FString: a length, then that many bytes (or UTF-16 units when negative) ending in a zero.</summary>
    private static string String(ReadOnlySpan<byte> data, ref int at)
    {
        var length = Int(data, ref at);
        if (length == 0)
        {
            return "";
        }

        if (length is > 4096 or < -4096)
        {
            throw new InvalidDataException("A string in the header is too long.");
        }

        if (length < 0)
        {
            var bytes = -length * 2;
            var wide = Encoding.Unicode.GetString(data.Slice(at, bytes - 2));
            at += bytes;
            return wide;
        }

        var text = Encoding.UTF8.GetString(data.Slice(at, length - 1));
        at += length;
        return text;
    }
}
