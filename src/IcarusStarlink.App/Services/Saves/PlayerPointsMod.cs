using System.IO;
using System.Text.Json.Nodes;
using IcarusStarlink.Core.Library;
using IcarusStarlink.PakIO.Assets;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Services;

/// <param name="Field">The D_CharacterGrowth field: TalentPointsPerLevel, BlueprintPointsPerLevel or SoloPointsPerLevel.</param>
/// <param name="Multiplier">The game's points at every level times this (2 = twice as many).</param>
/// <param name="KeepRising">Past the curve's last level, points keep coming at the same rate (else they stop there).</param>
public sealed record PointsCurveChange(string Field, double Multiplier, bool KeepRising);

/// <summary>
/// "Change points per level" on the Saves page (the user's ask, 2026-10-23: they modded their own
/// talent and blueprint points and want the editor to do it): a Library mod, "Starlink Player
/// Points", made the way MkHaters Animal Talents 2.0 is. For each tree changed, its own cooked
/// curve under /Game/Starlink/PlayerPoints/ (a copy of one of the game's with the game's keys
/// scaled) and D_CharacterGrowth's Player row pointed at it, so no game asset is replaced and
/// Remove Level Cap (MaxLevel, same row) still merges with it. Made again from the game's own
/// curves each time, so changing it twice never multiplies twice.
/// </summary>
public sealed class PlayerPointsMod(ILibraryRepository library, IBaseGameContentProvider game)
{
    public const string FileName = "Starlink_Player_Points";
    public const string DisplayName = "Starlink Player Points";
    private const string AssetFolder = "Starlink/PlayerPoints";

    private static readonly Dictionary<string, string> CurveNames = new()
    {
        ["TalentPointsPerLevel"] = "C_Starlink_PlayerTalentGrowth",
        ["BlueprintPointsPerLevel"] = "C_Starlink_PlayerBlueprintGrowth",
        ["SoloPointsPerLevel"] = "C_Starlink_SoloTalentGrowth",
    };

    private const string SettingsFile = "starlink-player-points.json";

    /// <summary>
    /// The settings the mod was last made with, by field: what the editor starts from, so remaking
    /// it before it's installed never drops another tree's change or rounds a multiplier (review,
    /// 2026-10-23: it started from the installed curves, which don't have it yet).
    /// </summary>
    public IReadOnlyDictionary<string, PointsCurveChange> LastSettings()
    {
        try
        {
            if (Existing is { } entry && Path.Combine(library.GetFolderPath(entry.FolderName), SettingsFile) is var path && File.Exists(path)
                && System.Text.Json.JsonSerializer.Deserialize<List<PointsCurveChange>>(File.ReadAllText(path)) is { } changes)
            {
                return changes.ToDictionary(c => c.Field, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception)
        {
        }

        return new Dictionary<string, PointsCurveChange>();
    }

    /// <summary>The Library mod as it is now, if it was made before.</summary>
    public LibraryEntry? Existing => library.GetAll().FirstOrDefault(e => string.Equals(e.FileName, FileName, StringComparison.OrdinalIgnoreCase) && e.MadeInStarlink);

    /// <summary>Makes (or remakes) the mod; returns its Library folder, or null when nothing differs from the game (an old copy is then removed).</summary>
    public string? Build(IReadOnlyList<PointsCurveChange> changes, PlayerGrowth growth)
    {
        var real = changes.Where(c => growth.GameCurvesByField.TryGetValue(c.Field, out var gameCurve)
                && (Math.Abs(c.Multiplier - 1) > 1e-9 || c.KeepRising != gameCurve.LinearAfter))
            .ToList();
        var existing = Existing;
        if (real.Count == 0)
        {
            if (existing is not null)
            {
                library.Delete(existing.FolderName);
            }

            return null;
        }

        var assets = new List<ExmodAssetEntry>();
        var fields = new Dictionary<string, JsonNode?>();
        foreach (var change in real)
        {
            var gameCurve = growth.GameCurvesByField[change.Field];
            var template = TemplateFor(change.KeepRising, growth)
                ?? throw new InvalidOperationException("The game's own curves couldn't be read, so there's nothing to copy.");
            var files = game.TryReadPackageFiles(template)
                ?? throw new InvalidOperationException($"Couldn't read the game's {Path.GetFileNameWithoutExtension(template)}.");
            if (!files.TryGetValue(".uasset", out var asset) || !files.TryGetValue(".uexp", out var exports))
            {
                throw new InvalidOperationException($"The game's {Path.GetFileNameWithoutExtension(template)} isn't a cooked curve.");
            }

            var name = CurveNames[change.Field];
            var keys = gameCurve.Keys.Select(k => (k.Time, (float)(k.Value * change.Multiplier))).ToList();
            var written = CurveAssetWriter.Write(asset, exports, keys, $"/Game/{AssetFolder}/{name}");
            assets.Add(new ExmodAssetEntry($"{AssetFolder}/{name}.uasset", written.Asset));
            assets.Add(new ExmodAssetEntry($"{AssetFolder}/{name}.uexp", written.Exports));
            fields[change.Field] = JsonValue.Create($"CurveFloat'{written.PackagePath}.{name}'");
        }

        var package = new ExmodPackage
        {
            Name = DisplayName,
            Author = "Icarus Starlink",
            Version = DateTime.Now.ToString("yyyy.M.d"),
            Description = "Talent, blueprint and solo points per level, made on the Saves page: "
                + string.Join("; ", real.Select(c => $"{Label(c.Field)} ×{c.Multiplier:0.##}{(c.KeepRising ? ", keep rising" : ", stop at the last level")}")) + ".",
            FileName = FileName,
            Rows =
            [
                new ExmodFileRow
                {
                    CurrentFile = "Character-D_CharacterGrowth.json",
                    FileItems = [new ExmodFileItem { Name = "Player", Fields = fields }],
                },
            ],
        };

        var folderName = existing?.FolderName ?? library.CreateBlankMod(DisplayName, "Icarus Starlink").FolderName;
        var folder = library.GetFolderPath(folderName);
        var oldAssets = Path.Combine(folder, AssetFolder.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(oldAssets))
        {
            Directory.Delete(oldAssets, recursive: true);
        }

        ExmodFolder.Write(folder, new ExmodPackageContents(package, assets));
        File.WriteAllText(Path.Combine(folder, SettingsFile), System.Text.Json.JsonSerializer.Serialize(changes));
        library.Refresh();
        return folderName;
    }

    public static string Label(string field) => field switch
    {
        "TalentPointsPerLevel" => "Talents",
        "BlueprintPointsPerLevel" => "Blueprints",
        "SoloPointsPerLevel" => "Solo",
        _ => field,
    };

    /// <summary>One of the game's own curves that behaves as wanted past its last key (the blueprint curve rises, the talent curve stops).</summary>
    private static string? TemplateFor(bool keepRising, PlayerGrowth growth) =>
        growth.GameCurvesByField.Where(kv => kv.Value.LinearAfter == keepRising && kv.Key != "ExperienceCurve")
            .Select(kv => growth.GameCurvePathsByField.GetValueOrDefault(kv.Key))
            .FirstOrDefault(p => p is not null);
}
