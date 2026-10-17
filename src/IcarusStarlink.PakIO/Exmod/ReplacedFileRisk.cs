using IcarusStarlink.PakIO.Assets;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>How much can break when a mod replaces a game file with its own copy.</summary>
public enum FileRisk
{
    /// <summary>Art and sound (a texture, mesh, material, icon): at worst it looks or sounds wrong.</summary>
    Low,

    /// <summary>One game blueprint or screen: that one thing can stop working after a game update.</summary>
    Medium,

    /// <summary>Something the whole game builds on (the player character, a base class): many things can break.</summary>
    High,
}

/// <summary>One game file a mod replaces, how risky that is, what it is in plain words, and (a blueprint) how the mod's copy differs from the game's.</summary>
/// <param name="ChangedByGame">What the game itself changed in this file in its last update ("the game changed X in this update"); null when not known (fewer than two game builds recorded) or nothing changed.</param>
/// <param name="OutOfDateProof">Proof the mod's copy predates that update ("out of date: the game changed X in this update; this mod still has its own older copy"); null without proof.</param>
public sealed record ReplacedFile(string Path, FileRisk Risk, string What, BlueprintDifference? Difference = null, string? ChangedByGame = null, string? OutOfDateProof = null);

/// <summary>Another mod replacing the same game file with different content: only one of them can win.</summary>
public sealed record FileClash(string Path, IReadOnlyList<string> OtherMods);

/// <summary>Everything a mod replaces, rated, plus the files it fights over with other mods.</summary>
public sealed record ModFileRisk(FileRisk Level, IReadOnlyList<ReplacedFile> Files, IReadOnlyList<FileClash> Clashes)
{
    /// <summary>"High risk", and " · out of date" only with proof (see ReplacedFile.OutOfDateProof).</summary>
    public string Label => (Level switch
    {
        FileRisk.High => "High risk",
        FileRisk.Medium => "Medium risk",
        _ => "Low risk",
    }) + (IsOutOfDate ? " · out of date" : "");

    /// <summary>Proven: one of the replaced files is the game's code from before its last update.</summary>
    public bool IsOutOfDate => Files.Any(f => f.OutOfDateProof is not null);

    /// <summary>The game changed at least one of the files this mod replaces in its last update.</summary>
    public bool IsChangedByGameUpdate => Files.Any(f => f.ChangedByGame is not null);

    /// <summary>One line per replaced file the last game update changed: the proof when there is one, otherwise what the game changed.</summary>
    public IReadOnlyList<string> GameUpdateLines =>
        [.. Files.Where(f => f.ChangedByGame is not null || f.OutOfDateProof is not null)
            .Select(f => $"{FileName(f.Path)} — {f.OutOfDateProof ?? f.ChangedByGame}.")];

    /// <summary>The plain explanation: what it replaces (riskiest first) and what it clashes with.</summary>
    public string Explanation
    {
        get
        {
            var lines = new List<string>();
            var top = Files.OrderByDescending(f => f.Risk).ToList();
            if (top.Count > 0)
            {
                lines.Add($"Replaces {(top.Count == 1 ? "a game file" : $"{top.Count} game files")} with its own copy:");
                lines.AddRange(top.Take(5).Select(f => $"• {f.What}: {FileName(f.Path)}" + (f.Difference is { } d ? $". Compared with the game's copy, it {d.Describe()}" : "")));
                if (top.Count > 5)
                {
                    lines.Add($"• and {top.Count - 5} more");
                }
            }

            lines.AddRange(GameUpdateLines);

            if (Level == FileRisk.High)
            {
                lines.Add("A copy made for an older game version can break things everywhere after a game update. If something stops working, try without this mod first.");
            }
            else if (Level == FileRisk.Medium && top.Any(f => f.Risk == FileRisk.Medium))
            {
                lines.Add("A copy made for an older game version can stop that one thing working after a game update.");
            }

            foreach (var clash in Clashes)
            {
                lines.Add($"{string.Join(" and ", clash.OtherMods)} also replace{(clash.OtherMods.Count == 1 ? "s" : "")} {FileName(clash.Path)} with a different copy: only one can win (in Merge & Install, the mod lower in the queue).");
            }

            return string.Join("\n", lines);
        }
    }

    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];
}

/// <summary>
/// Rates the game files a mod replaces. A mod's own new file (its own blueprint, its own icon) is
/// never a risk; only a file the game already has, which the mod's copy takes the place of.
/// Data tables aren't files here at all: ISL merges those row by row.
/// </summary>
public static class ReplacedFileRiskRater
{
    /// <summary>The game's own core blueprints, by file name, and what they are.</summary>
    private static readonly Dictionary<string, string> Core = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BP_IcarusPlayerCharacterSurvival"] = "the player character (everything the player does: moving, eating, drinking, building)",
        ["BP_IcarusPlayerCharacter"] = "the player character",
        ["BP_IcarusPlayerControllerSurvival"] = "the player controller (controls and menus)",
        ["BP_IcarusPlayerController"] = "the player controller",
        ["BP_IcarusPlayerState"] = "the player's state (stats, talents, inventory)",
        ["BP_IcarusGameStateSurvival"] = "the game's state",
        ["BP_IcarusGameModeSurvival"] = "the game's rules",
        ["BP_IcarusGameMode"] = "the game's rules",
        ["BP_IcarusNPCGOAPCharacter"] = "the base of every creature",
    };

    /// <summary>Name prefixes of art and sound: textures, meshes, materials, sounds, animations.</summary>
    private static readonly string[] ArtPrefixes = ["T_", "SM_", "SK_", "M_", "MI_", "MA_", "MF_", "S_", "SC_", "A_", "AM_", "ABP_", "NS_", "P_", "Item_"];

    /// <summary>Screens that are always on.</summary>
    private static readonly HashSet<string> AlwaysOnScreens = new(StringComparer.OrdinalIgnoreCase)
    {
        "UMG_RevisionNumber", "UMG_HUD", "UMG_GameHUD",
    };

    public static ReplacedFile Rate(string path)
    {
        var normalized = path.Replace('\\', '/');
        var name = normalized[(normalized.LastIndexOf('/') + 1)..];
        var dot = name.IndexOf('.');
        if (dot > 0)
        {
            name = name[..dot];
        }

        if (Core.TryGetValue(name, out var core))
        {
            return new(normalized, FileRisk.High, core);
        }

        // What a file is shows in its name before its folder (a material can sit in a blueprint folder).
        if (ArtPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            return new(normalized, FileRisk.Low, $"art or sound ({Words(name)})");
        }

        if (name.StartsWith("C_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Curve_", StringComparison.OrdinalIgnoreCase))
        {
            return new(normalized, FileRisk.Low, $"game numbers ({Words(name)} curve)");
        }

        var isBlueprint = name.StartsWith("BP_", StringComparison.OrdinalIgnoreCase) || normalized.StartsWith("BP/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/Blueprints/", StringComparison.OrdinalIgnoreCase);
        if (isBlueprint && (name.EndsWith("Base", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_Base", StringComparison.OrdinalIgnoreCase)))
        {
            return new(normalized, FileRisk.High, $"a shared base: every {BaseWhat(name)} in the game builds on it");
        }

        if (AlwaysOnScreens.Contains(name))
        {
            return new(normalized, FileRisk.Medium, "a part of the screen that's always shown");
        }

        if (name.StartsWith("UMG_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("WB_", StringComparison.OrdinalIgnoreCase)
            || (!isBlueprint && normalized.StartsWith("UI/", StringComparison.OrdinalIgnoreCase)))
        {
            return new(normalized, FileRisk.Medium, $"a game screen ({Words(name)})");
        }

        if (isBlueprint)
        {
            return new(normalized, FileRisk.Medium, $"a game blueprint ({Words(name)})");
        }

        return new(normalized, FileRisk.Low, $"art or sound ({Words(name)})");
    }

    /// <param name="replacedPaths">The game files the mod ships a copy of (ones the game already has; its own new files left out).</param>
    /// <param name="clashes">Other mods replacing one of these with a different copy.</param>
    public static ModFileRisk? Assess(IEnumerable<string> replacedPaths, IReadOnlyList<FileClash> clashes) =>
        AssessCompared(replacedPaths.Select(p => (p, (BlueprintDifference?)null)), clashes);

    /// <param name="replaced">Each replaced game file, with how the mod's copy differs from the game's (blueprints) where known.</param>
    public static ModFileRisk? AssessCompared(IEnumerable<(string Path, BlueprintDifference? Difference)> replaced, IReadOnlyList<FileClash> clashes)
    {
        var files = replaced.DistinctBy(r => r.Path, StringComparer.OrdinalIgnoreCase).Select(r => Rate(r.Path) with { Difference = r.Difference }).ToList();
        if (files.Count == 0 && clashes.Count == 0)
        {
            return null;
        }

        var level = files.Count == 0 ? FileRisk.Low : files.Max(f => f.Risk);
        if (clashes.Count > 0 && level < FileRisk.Medium)
        {
            level = FileRisk.Medium;
        }

        return new ModFileRisk(level, files, clashes);
    }

    /// <summary>"BP_DeployableBase" → "deployable"; "BP_Building_Base" → "building piece".</summary>
    private static string BaseWhat(string name)
    {
        var core = name.StartsWith("BP_", StringComparison.OrdinalIgnoreCase) ? name[3..] : name;
        core = core.EndsWith("_Base", StringComparison.OrdinalIgnoreCase) ? core[..^5] : core[..^4];
        return core.ToLowerInvariant() switch
        {
            "building" => "building piece",
            "deployable" => "placeable item",
            "processor" => "crafting bench and processor",
            "mount" => "mount",
            "modifier" => "buff and effect",
            var other => Words(other).ToLowerInvariant(),
        };
    }

    /// <summary>"BP_Homestead_Refrigerator" → "Homestead Refrigerator".</summary>
    private static string Words(string name)
    {
        foreach (var prefix in new[] { "BP_", "UMG_", "WB_", "T_", "SM_", "SK_", "MI_", "MA_", "MF_", "M_", "SC_", "S_", "C_", "Item_" })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[prefix.Length..];
                break;
            }
        }

        return name.Replace('_', ' ').Trim();
    }
}
