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
    IReadOnlyList<string> CurvesFromMods, int GameMaxLevel = 0, bool StarlinkMergeInstalled = false)
{
    /// <summary>The value Starlink's Remove Level Cap writes (GameplayOptionsFieldChangeGenerator).</summary>
    public const int RemovedLevelCap = 50000;

    /// <summary>The cap is Starlink's own Remove Level Cap, not another mod's.</summary>
    public bool IsStarlinkLevelCap => StarlinkMergeInstalled && MaxLevel == RemovedLevelCap;

    /// <summary>Something installed changed the cap (Starlink's option or another mod).</summary>
    public bool LevelCapChanged => TableFromMods && GameMaxLevel > 0 && MaxLevel != GameMaxLevel;
}

/// <summary>
/// A player's level and talent-point budgets as the game has them installed: D_CharacterGrowth's
/// "Player" row (the installed mods' copy in Paks\mods when there is one: the merge carries
/// Remove Level Cap and any mod's change) and the four CurveFloats it names, each from the
/// installed mods' paks when a mod supplies it, else from the game's own paks.
/// </summary>
public static class PlayerGrowthReader
{
    private const string TablePath = "Data/Character/D_CharacterGrowth.json";

    public static PlayerGrowth? Read(string dataFolder, string? contentPath, IBaseGameContentProvider game)
    {
        ModPakContents? mods = null;
        try
        {
            var modsFolder = contentPath is { Length: > 0 } ? Path.Combine(contentPath, "Paks", "mods") : null;
            mods = modsFolder is not null ? ModPakContents.TryOpen(modsFolder) : null;

            var fromMods = PlayerRow(mods?.TryRead(TablePath));
            var gameRow = PlayerRow(ReadFile(Path.Combine(dataFolder, "Character", "D_CharacterGrowth.json")));
            var row = fromMods ?? gameRow;
            var starlinkMerge = modsFolder is not null && Directory.Exists(modsFolder)
                && Directory.EnumerateFiles(modsFolder, "*ISL-Merged*.pak").Any();
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
                starlinkMerge);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            mods?.Dispose();
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

    private static (int MaxLevel, int MaxDisplayLevel, Dictionary<string, string> Curves)? PlayerRow(byte[]? json)
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
                if (!row.TryGetProperty("Name", out var n) || n.GetString() != "Player")
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
