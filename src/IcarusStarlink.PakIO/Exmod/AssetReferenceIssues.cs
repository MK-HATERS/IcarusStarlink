using System.Text.Json.Nodes;
using IcarusStarlink.PakIO.Assets;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// AssetReferenceChecker's findings as the mod check's issues (MissingAsset, MissingAssetPart), so
/// they share its badge, fix window, Fix sure problems and remembered choices. CurrentFile is the
/// mod's file, ItemName the game package it uses, the mod's value that package and the game's
/// value (and SuggestedRow) where the game has it now.
/// </summary>
public static class AssetReferenceIssues
{
    public static IReadOnlyList<ModIssue> From(IEnumerable<MissingAssetReference> found) =>
        [.. found.Select(ToIssue)];

    private static ModIssue ToIssue(MissingAssetReference found)
    {
        var file = FileName(found.AssetFile);
        var used = AssetReferenceChecker.AssetName(found.Package);
        return found.Kind switch
        {
            MissingReferenceKind.Moved => new(ModIssueKind.MissingAsset, found.AssetFile, found.Package, null,
                JsonValue.Create(found.Package), JsonValue.Create(found.NewPackage), null, found.NewPackage,
                ModIssueFix.Repoint, IsSure: true,
                $"{file} uses {used} from {Folder(found.Package)}, and the game moved it to {Folder(found.NewPackage!)} (same name, same contents). "
                + "Pointing the mod there makes it work again; nothing else in the file changes."),
            MissingReferenceKind.MovedMaybe => new(ModIssueKind.MissingAsset, found.AssetFile, found.Package, null,
                JsonValue.Create(found.Package), JsonValue.Create(found.NewPackage), null, found.NewPackage,
                ModIssueFix.Repoint, IsSure: false,
                $"{file} uses {used} from {Folder(found.Package)}, which the game no longer has there. The game has {found.Candidates.Count} files of that name "
                + $"that hold the same things: {string.Join(", ", found.Candidates.Select(Folder))}. The nearest is suggested; check it's the right one."),
            MissingReferenceKind.ObjectGone => new(ModIssueKind.MissingAssetPart, found.AssetFile, found.Package, string.Join(", ", found.Objects.Keys.Select(Part)),
                JsonValue.Create(found.Package), null, null, null,
                ModIssueFix.KeepMod, IsSure: false,
                $"{file} uses {Parts(found)} of the game's {used}, which the game has since taken out of it. Those parts of the mod do nothing now "
                + "(they were made for an older game version). The mod's author has to update it; Starlink can't put them back."),
            _ => new(ModIssueKind.MissingAsset, found.AssetFile, found.Package, null,
                JsonValue.Create(found.Package), null, null, null,
                ModIssueFix.KeepMod, IsSure: false,
                $"{file} uses {used} ({Kinds(found)}) from {Folder(found.Package)}, which the game doesn't have any more. Whatever in the mod uses it won't work, "
                + "and if something in the mod is built on it, the game can stop when it loads. The mod's author has to update it."),
        };
    }

    /// <summary>"ASS/DEP/SM_X.uasset" → "SM_X".</summary>
    private static string FileName(string assetFile)
    {
        var name = assetFile.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return name.Contains('.') ? name[..name.LastIndexOf('.')] : name;
    }

    /// <summary>"/Game/ASS/DEP/M_X" → "ASS/DEP".</summary>
    internal static string Folder(string package)
    {
        var withoutName = package[..Math.Max(0, package.LastIndexOf('/'))];
        return withoutName.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase) ? withoutName["/Game/".Length..]
            : withoutName.StartsWith("/Game", StringComparison.OrdinalIgnoreCase) ? "Content"
            : withoutName.TrimStart('/');
    }

    /// <summary>"BP_X_C.Mesh_GEN_VARIABLE" → "Mesh" (a component's own name).</summary>
    private static string Part(string objectPath)
    {
        var last = objectPath[(objectPath.LastIndexOf('.') + 1)..];
        return last.EndsWith("_GEN_VARIABLE", StringComparison.Ordinal) ? last[..^"_GEN_VARIABLE".Length] : last;
    }

    private static string Parts(MissingAssetReference found) =>
        found.Objects.Count == 1 ? $"the {Part(found.Objects.Keys.First())} part" : $"the {string.Join(" and ", found.Objects.Keys.Select(Part))} parts";

    /// <summary>What kind of thing it is, in plain words ("a material", "a blueprint").</summary>
    private static string Kinds(MissingAssetReference found)
    {
        var classes = found.Objects.Values.Select(Plain).Where(k => k is not null).Distinct().ToList();
        return classes.Count == 0 ? "a game file" : string.Join(", ", classes);
    }

    private static string? Plain(string className) => className switch
    {
        "Material" or "MaterialInstanceConstant" or "MaterialFunction" => "a material",
        "Texture2D" or "TextureCube" => "a texture",
        "StaticMesh" => "a mesh",
        "SkeletalMesh" or "Skeleton" or "PhysicsAsset" => "a model",
        "DestructibleMesh" => "a breakable mesh",
        "BlueprintGeneratedClass" or "WidgetBlueprintGeneratedClass" or "AnimBlueprintGeneratedClass" => "a blueprint",
        "UserDefinedEnum" => "a list of choices",
        "UserDefinedStruct" => "a data layout",
        "SoundClass" or "SoundCue" or "SoundWave" or "SoundAttenuation" => "a sound setting",
        "DataTable" => "a data table",
        _ => null,
    };
}
