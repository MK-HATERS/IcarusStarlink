namespace IcarusStarlink.PakIO.Assets;

/// <summary>
/// Plain words for where a file sits in the game's content and what kind of file it is, so a
/// path like "/Game/ASS/DEP/DEP_Turret_T4/SM_Turret" can be shown as "a mesh (3D model) in
/// Deployables" instead of a folder code nobody knows. The folder names come from the game's own
/// layout (read from its paks): ASS holds the art, split by three-letter codes (DEP deployables,
/// ITM items, BLD building pieces…); BP the blueprints; FMOD the sounds; UI the menus.
/// </summary>
public static class GameContentNames
{
    // Longest match wins, so "ASS/ENV/TRE" beats "ASS/ENV". Keys are Content-relative folders.
    private static readonly (string Folder, string Name)[] Folders =
    [
        ("ASS", "Art (models, textures, materials)"),
        ("ASS/BLD", "Building pieces (walls, floors, roofs)"),
        ("ASS/CHA", "Characters (player, armour, NPCs)"),
        ("ASS/CHA/1ST", "Characters, first-person view"),
        ("ASS/CHA/3RD", "Characters, third-person view"),
        ("ASS/CHA/ARM", "Armour"),
        ("ASS/CHA/ARMOR", "Armour"),
        ("ASS/CHA/Suits", "Suits"),
        ("ASS/CRE", "Creatures"),
        ("ASS/DCO", "Decorations (furniture, signs)"),
        ("ASS/DEP", "Deployables (benches, machines, things you place)"),
        ("ASS/DPS", "Drop ship"),
        ("ASS/ENV", "Environment (trees, rocks, plants)"),
        ("ASS/ENV/ATM", "Environment, sky and weather"),
        ("ASS/ENV/FOL", "Environment, foliage"),
        ("ASS/ENV/GRS", "Environment, grass"),
        ("ASS/ENV/HRB", "Environment, plants and herbs"),
        ("ASS/ENV/LND", "Environment, landscape"),
        ("ASS/ENV/RCK", "Environment, rocks"),
        ("ASS/ENV/TRE", "Environment, trees"),
        ("ASS/ENV/VOX", "Environment, ore and voxels"),
        ("ASS/ENV/WAT", "Environment, water"),
        ("ASS/ITM", "Items (tools, food, resources)"),
        ("ASS/KIT", "Kits"),
        ("ASS/MAT", "Shared materials"),
        ("ASS/MAT/MF", "Shared material parts"),
        ("ASS/MF", "Shared material parts"),
        ("ASS/MTK", "Drones"),
        ("ASS/ORB", "Orbital station"),
        ("ASS/PRP", "Props"),
        ("ASS/VC", "Vehicles"),
        ("ASS/VFX", "Visual effects"),
        ("ASS/VHC", "Vehicles"),
        ("ASS/WEP", "Weapons"),
        ("Assets", "Art"),
        ("Assets/2DArt", "Icons and 2D art"),
        ("BP", "Blueprints (game logic)"),
        ("BP/AI", "Blueprints, creature behaviour"),
        ("BP/Audio", "Blueprints, sound"),
        ("BP/Building", "Blueprints, building"),
        ("BP/Mounts", "Blueprints, mounts"),
        ("BP/Objects", "Blueprints, objects in the world"),
        ("BP/Player", "Blueprints, player"),
        ("BP/Quests", "Blueprints, missions"),
        ("BP/Tools", "Blueprints, tools"),
        ("BP/UI", "Blueprints, menus"),
        ("BP/World", "Blueprints, world"),
        ("Data", "Game data"),
        ("Developers", "Developers' test files"),
        ("FMOD", "Sounds"),
        ("FMOD/Events", "Sounds"),
        ("Fonts", "Fonts"),
        ("Maps", "Maps"),
        ("Prototypes", "Unfinished prototypes"),
        ("ThirdPartyAssets", "Bought-in art packs"),
        ("TRD", "Bought-in art packs"),
        ("UI", "Menus and HUD"),
    ];

    /// <summary>
    /// "ASS/DEP/DEP_Turret_T4" (or "/Game/ASS/DEP/…", or a file in it) → "Deployables (benches,
    /// machines, things you place)"; null for a folder with no known name. /Engine is the engine's own content.
    /// </summary>
    public static string? AreaOf(string folderOrPath)
    {
        var path = folderOrPath.Replace('\\', '/').Trim('/');
        if (path.StartsWith("Engine", StringComparison.OrdinalIgnoreCase) && (path.Length == 6 || path[6] == '/'))
        {
            return "Unreal Engine's own content";
        }

        if (path.StartsWith("Game/", StringComparison.OrdinalIgnoreCase))
        {
            path = path["Game/".Length..];
        }

        string? best = null;
        var bestLength = -1;
        foreach (var (folder, name) in Folders)
        {
            if (folder.Length > bestLength
                && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                && (path.Length == folder.Length || path[folder.Length] == '/'))
            {
                best = name;
                bestLength = folder.Length;
            }
        }

        return best;
    }

    /// <summary>"Deployables (ASS/DEP/DEP_Turret_T4)", or just the folder when it has no known name.</summary>
    public static string FolderWithArea(string folder)
    {
        var area = AreaOf(folder);
        if (area is null)
        {
            return folder;
        }

        // Only the short name ("Deployables"), the details are for the longer hint.
        var shortName = area.Contains(" (", StringComparison.Ordinal) ? area[..area.IndexOf(" (", StringComparison.Ordinal)] : area;
        return $"{shortName} ({folder})";
    }

    /// <summary>
    /// What kind of file it is, from the engine class when known ("StaticMesh" → "mesh (3D model)"),
    /// else from Unreal's naming habits ("SM_Cable" → the same); null when neither says.
    /// </summary>
    public static string? KindOf(string assetName, string? className = null) =>
        (className is null ? null : KindOfClass(className)) ?? KindOfName(assetName);

    private static string? KindOfClass(string className) => className switch
    {
        "StaticMesh" => "mesh (3D model)",
        "SkeletalMesh" => "animated model",
        "Skeleton" => "model skeleton",
        "PhysicsAsset" => "model physics",
        "DestructibleMesh" => "breakable mesh",
        "Material" or "MaterialInstanceConstant" => "material (how a surface looks)",
        "MaterialFunction" => "material part",
        "Texture2D" or "TextureCube" or "TextureRenderTarget2D" => "texture (image)",
        "BlueprintGeneratedClass" => "blueprint (game logic)",
        "WidgetBlueprintGeneratedClass" => "menu or HUD screen",
        "AnimBlueprintGeneratedClass" => "animation blueprint",
        "AnimSequence" or "AnimMontage" or "BlendSpace" or "BlendSpace1D" or "AimOffsetBlendSpace" => "animation",
        "SoundCue" or "SoundWave" or "FMODEvent" => "sound",
        "SoundClass" or "SoundAttenuation" or "SoundConcurrency" or "SoundMix" => "sound setting",
        "ParticleSystem" or "NiagaraSystem" or "NiagaraEmitter" => "visual effect",
        "UserDefinedEnum" => "list of choices",
        "UserDefinedStruct" => "data layout",
        "DataTable" => "data table",
        "CurveFloat" or "CurveVector" or "CurveLinearColor" => "curve",
        "Font" or "FontFace" => "font",
        "World" => "map",
        _ => null,
    };

    // Ordered so the longer prefix is tried first ("MI_" before "M_", "SKM_" before "SK_").
    private static readonly (string Prefix, string Kind)[] NamePrefixes =
    [
        ("ABP_", "animation blueprint"),
        ("SKM_", "animated model"),
        ("SK_", "animated model"),
        ("SM_", "mesh (3D model)"),
        ("MI_", "material (how a surface looks)"),
        ("MF_", "material part"),
        ("M_", "material (how a surface looks)"),
        ("T_", "texture (image)"),
        ("WBP_", "menu or HUD screen"),
        ("BP_", "blueprint (game logic)"),
        ("AM_", "animation"),
        ("AS_", "animation"),
        ("BS_", "animation"),
        ("A_", "animation"),
        ("NS_", "visual effect"),
        ("PS_", "visual effect"),
        ("P_", "visual effect"),
        ("FX_", "visual effect"),
        ("SC_", "sound"),
        ("SW_", "sound"),
        ("PHYS_", "model physics"),
        ("PA_", "model physics"),
        ("D_", "data table"),
    ];

    private static string? KindOfName(string assetName)
    {
        var name = assetName.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        foreach (var (prefix, kind) in NamePrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return kind;
            }
        }

        return null;
    }

    /// <summary>"mesh (3D model)" → "mesh": the kind without its explanation, for tight spaces.</summary>
    public static string Short(string kind) =>
        kind.Contains(" (", StringComparison.Ordinal) ? kind[..kind.IndexOf(" (", StringComparison.Ordinal)] : kind;

    /// <summary>"a mesh (3D model)" / "an animated model".</summary>
    public static string WithArticle(string kind) =>
        ("aeiou".Contains(char.ToLowerInvariant(kind[0])) ? "an " : "a ") + kind;
}
