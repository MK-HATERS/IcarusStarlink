using System.Text.Json;
using CUE4Parse.UE4.Assets.Exports;

namespace IcarusStarlink.PakIO.Assets;

/// <param name="MaxLevel">D_CharacterGrowth Player MaxLevel as installed (Starlink's Remove Level Cap sets 50,000).</param>
/// <param name="MaxDisplayLevel">The level the game shows as its top (60).</param>
/// <param name="TableFromMods">The Player row came from the installed mods' copy of the table, not the game's.</param>
/// <param name="CurvesFromMods">Which of the four curves an installed mod supplies ("Talent points", ...).</param>
/// <param name="GameMaxLevel">The game's own MaxLevel (its own table), to tell whether a mod changed it.</param>
/// <param name="StarlinkMergeInstalled">Starlink's merge (ISL-Merged) is in Paks\mods: a cap of 50,000 then is its Remove Level Cap.</param>
public sealed record PlayerGrowth(
    int MaxLevel, int MaxDisplayLevel, bool TableFromMods,
    GrowthCurve? Experience, GrowthCurve? TalentPoints, GrowthCurve? BlueprintPoints, GrowthCurve? SoloPoints,
    IReadOnlyList<string> CurvesFromMods, int GameMaxLevel = 0, bool StarlinkMergeInstalled = false,
    IReadOnlyDictionary<string, string>? GameCurvePaths = null, IReadOnlyDictionary<string, GrowthCurve>? GameCurves = null)
{
    /// <summary>The game's own curves by D_CharacterGrowth field ("TalentPointsPerLevel"), whatever mods install: what "Change points" scales.</summary>
    public IReadOnlyDictionary<string, GrowthCurve> GameCurvesByField { get; } = GameCurves ?? new Dictionary<string, GrowthCurve>();

    /// <summary>Where the game keeps each of its own curves ("Data/Character/C_PlayerTalentGrowth.uasset").</summary>
    public IReadOnlyDictionary<string, string> GameCurvePathsByField { get; } = GameCurvePaths ?? new Dictionary<string, string>();

    /// <summary>The value Starlink's Remove Level Cap writes (GameplayOptionsFieldChangeGenerator).</summary>
    public const int RemovedLevelCap = 50000;

    /// <summary>The cap is Starlink's own Remove Level Cap, not another mod's.</summary>
    public bool IsStarlinkLevelCap => StarlinkMergeInstalled && MaxLevel == RemovedLevelCap;

    /// <summary>Something installed changed the cap (Starlink's option or another mod).</summary>
    public bool LevelCapChanged => TableFromMods && GameMaxLevel > 0 && MaxLevel != GameMaxLevel;
}

/// <param name="GrowthRow">Its D_CharacterGrowth row (AI_Mounts, AI_Pets, or a mod's own).</param>
/// <param name="Archetype">Its talent archetype (Creature_Raptor): which trees its talents come from.</param>
/// <param name="Variations">How many looks (D_Mounts Variations) it has: a saved Variation is 0 to this - 1.</param>
/// <param name="Growth">Its growth row's curves as installed (XP and talent points per level).</param>
public sealed record MountGrowth(string GrowthRow, string? Archetype, int Variations, PlayerGrowth? Growth);

/// <summary>
/// A player's level and talent-point budgets as the game has them installed: D_CharacterGrowth's
/// "Player" row (the installed mods' copy in Paks\mods when there is one: the merge carries
/// Remove Level Cap and any mod's change) and the four CurveFloats it names, each from the
/// installed mods' paks when a mod supplies it, else from the game's own paks.
/// </summary>
public static class PlayerGrowthReader
{
    private const string TablePath = "Data/Character/D_CharacterGrowth.json";

    public static PlayerGrowth? Read(string dataFolder, string? contentPath, IBaseGameContentProvider game) =>
        ReadRows(dataFolder, contentPath, game, ["Player"]).GetValueOrDefault("Player");

    /// <summary>
    /// Each saved mount type's growth as installed: D_Mounts (the mods' copy when there is one, so a
    /// mod giving a tame its own growth row counts, like MkHaters Animal Talents 2.0) names a
    /// D_CharacterGrowth row per type, read like the player's.
    /// </summary>
    public static IReadOnlyDictionary<string, MountGrowth> ReadMounts(string dataFolder, string? contentPath, IBaseGameContentProvider game)
    {
        try
        {
            var modsFolder = contentPath is { Length: > 0 } ? Path.Combine(contentPath, "Paks", "mods") : null;
            using var mods = modsFolder is not null ? ModPakContents.TryOpen(modsFolder) : null;
            var types = MountTypes(mods?.TryRead(MountsPath)) ?? MountTypes(ReadFile(Path.Combine(dataFolder, "AI", "D_Mounts.json")));
            if (types is null)
            {
                return new Dictionary<string, MountGrowth>();
            }

            var rows = ReadRows(dataFolder, contentPath, game, types.Values.Select(t => t.GrowthRow).Distinct().ToList());
            return types.ToDictionary(kv => kv.Key, kv => kv.Value with { Growth = rows.GetValueOrDefault(kv.Value.GrowthRow) }, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return new Dictionary<string, MountGrowth>();
        }
    }

    public static IReadOnlyDictionary<string, PlayerGrowth> ReadRows(string dataFolder, string? contentPath, IBaseGameContentProvider game, IReadOnlyList<string> rowNames)
    {
        var result = new Dictionary<string, PlayerGrowth>(StringComparer.OrdinalIgnoreCase);
        ModPakContents? mods = null;
        try
        {
            var modsFolder = contentPath is { Length: > 0 } ? Path.Combine(contentPath, "Paks", "mods") : null;
            mods = modsFolder is not null ? ModPakContents.TryOpen(modsFolder) : null;
            var modsTable = mods?.TryRead(TablePath);
            var gameTable = ReadFile(Path.Combine(dataFolder, "Character", "D_CharacterGrowth.json"));
            var starlinkMerge = modsFolder is not null && Directory.Exists(modsFolder)
                && Directory.EnumerateFiles(modsFolder, "*ISL-Merged*.pak").Any();
            foreach (var rowName in rowNames)
            {
                if (ReadRow(rowName, modsTable, gameTable, starlinkMerge, mods, game) is { } growth)
                {
                    result[rowName] = growth;
                }
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            mods?.Dispose();
        }

        return result;
    }

    private static PlayerGrowth? ReadRow(string rowName, byte[]? modsTable, byte[]? gameTable, bool starlinkMerge, ModPakContents? mods, IBaseGameContentProvider game)
    {
        {
            var fromMods = Row(modsTable, rowName);
            var gameRow = Row(gameTable, rowName);
            var row = fromMods ?? gameRow;
            if (row is null)
            {
                return null;
            }

            var curvesFromMods = new List<string>();
            GrowthCurve? Curve(string field, string label)
            {
                if (row.Value.Curves.GetValueOrDefault(field) is not { } path)
                {
                    return null;
                }

                if (mods?.TryLoadFirstExport<UObject>(path) is { } modded && GrowthCurve.From(modded) is { } fromMod)
                {
                    curvesFromMods.Add(label);
                    return fromMod;
                }

                return game.TryLoadExportAndProject<UObject, GrowthCurve>(path, c => GrowthCurve.From(c)!);
            }

            return new PlayerGrowth(
                row.Value.MaxLevel, row.Value.MaxDisplayLevel, fromMods is not null,
                Curve("ExperienceCurve", "Experience"),
                Curve("TalentPointsPerLevel", "Talent points"),
                Curve("BlueprintPointsPerLevel", "Blueprint points"),
                Curve("SoloPointsPerLevel", "Solo points"),
                curvesFromMods,
                gameRow?.MaxLevel ?? 0,
                starlinkMerge,
                gameRow?.Curves,
                gameRow?.Curves
                    .Select(kv => (kv.Key, Curve: game.TryLoadExportAndProject<UObject, GrowthCurve>(kv.Value, c => GrowthCurve.From(c)!)))
                    .Where(x => x.Curve is not null)
                    .ToDictionary(x => x.Key, x => x.Curve!));
        }
    }

    private const string MountsPath = "Data/AI/D_Mounts.json";

    /// <summary>D_Mounts: each type's growth row, talent archetype and number of variations (Defaults merged in).</summary>
    private static Dictionary<string, MountGrowth>? MountTypes(byte[]? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var defaults = document.RootElement.TryGetProperty("Defaults", out var d) && d.ValueKind == JsonValueKind.Object ? d : (JsonElement?)null;
            var types = new Dictionary<string, MountGrowth>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in document.RootElement.GetProperty("Rows").EnumerateArray())
            {
                if (!row.TryGetProperty("Name", out var n) || n.GetString() is not { } name)
                {
                    continue;
                }

                JsonElement? Get(string field) =>
                    row.TryGetProperty(field, out var v) ? v : defaults is { } def && def.TryGetProperty(field, out var dv) ? dv : null;
                string? RowRef(string field) => Get(field) is { ValueKind: JsonValueKind.Object } r && r.TryGetProperty("RowName", out var rn) ? rn.GetString() : null;
                var variations = Get("Variations") is { ValueKind: JsonValueKind.Array } v ? v.GetArrayLength() : 0;
                types[name] = new MountGrowth(RowRef("GrowthCurve") ?? "AI_Mounts", RowRef("MountTalentArchetype") is { } a && a != "None" ? a : null, variations, null);
            }

            return types;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static byte[]? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>"CurveFloat'/Game/Data/Character/C_X.C_X'" -> "Data/Character/C_X.uasset".</summary>
    public static string? CurveAssetPath(string? reference)
    {
        if (reference is null)
        {
            return null;
        }

        var start = reference.IndexOf("/Game/", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        var path = reference[(start + "/Game/".Length)..].TrimEnd('\'');
        var dot = path.LastIndexOf('.');
        return (dot > path.LastIndexOf('/') ? path[..dot] : path) + ".uasset";
    }

    private static (int MaxLevel, int MaxDisplayLevel, Dictionary<string, string> Curves)? Row(byte[]? json, string rowName)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var defaults = document.RootElement.TryGetProperty("Defaults", out var d) && d.ValueKind == JsonValueKind.Object ? d : (JsonElement?)null;
            foreach (var row in document.RootElement.GetProperty("Rows").EnumerateArray())
            {
                if (!row.TryGetProperty("Name", out var n) || !string.Equals(n.GetString(), rowName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                JsonElement? Get(string field) =>
                    row.TryGetProperty(field, out var v) ? v : defaults is { } def && def.TryGetProperty(field, out var dv) ? dv : null;
                int Int(string field) => Get(field) is { ValueKind: JsonValueKind.Number } v ? (int)Math.Min(int.MaxValue, v.GetDouble()) : 0;

                var curves = new Dictionary<string, string>();
                foreach (var field in new[] { "ExperienceCurve", "TalentPointsPerLevel", "BlueprintPointsPerLevel", "SoloPointsPerLevel" })
                {
                    if (Get(field) is { ValueKind: JsonValueKind.String } v && CurveAssetPath(v.GetString()) is { } path)
                    {
                        curves[field] = path;
                    }
                }

                return (Int("MaxLevel"), Int("MaxDisplayLevel"), curves);
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
        }

        return null;
    }
}
