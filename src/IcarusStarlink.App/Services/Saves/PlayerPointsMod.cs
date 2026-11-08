using System.IO;
using System.Text.Json.Nodes;
using IcarusStarlink.Core.IO;
using IcarusStarlink.Core.Library;
using IcarusStarlink.PakIO.Assets;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Services;

/// <param name="Field">The D_CharacterGrowth field: TalentPointsPerLevel, BlueprintPointsPerLevel or SoloPointsPerLevel.</param>
/// <param name="Multiplier">The game's points at every level times this (2 = twice as many).</param>
/// <param name="KeepRising">Past the curve's last level, points keep coming at the same rate (else they stop there).</param>
public sealed record PointsCurveChange(string Field, double Multiplier, bool KeepRising);

/// <summary>The points mod built in memory (PlayerPointsMod.Prepare), ready to write into the Library (Commit).</summary>
public sealed record PreparedPointsMod(ExmodPackage Package, IReadOnlyList<ExmodAssetEntry> Assets);

/// <summary>
/// "Change points per level" on the Saves page: a Library mod, "Starlink Player Points", made the
/// way MkHaters Animal Talents 2.0 is. For each tree changed, its own cooked curve under /Game/Starlink/PlayerPoints/ (a copy of one of the game's with the game's keys
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
    /// it before it's installed never drops another tree's change or rounds a multiplier (it used
    /// to start from the installed curves, which don't have it yet).
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
    public string? Build(IReadOnlyList<PointsCurveChange> changes, PlayerGrowth growth) => Commit(Prepare(changes, growth), changes);

    /// <summary>
    /// Builds the mod's package and curves in memory: reads the game's curves and writes nothing,
    /// so it can run off the UI thread. Null when nothing differs from the game.
    /// </summary>
    public PreparedPointsMod? Prepare(IReadOnlyList<PointsCurveChange> changes, PlayerGrowth growth)
    {
        var real = changes.Where(c => growth.GameCurvesByField.TryGetValue(c.Field, out var gameCurve)
                && (Math.Abs(c.Multiplier - 1) > 1e-9 || c.KeepRising != gameCurve.LinearAfter))
            .ToList();
        if (real.Count == 0)
        {
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

        return new PreparedPointsMod(package, assets);
    }

    /// <summary>
    /// Writes a prepared mod into the Library (made there the first time) and returns its folder.
    /// Null prepared: nothing differs from the game, so an old copy is taken out and null returned.
    /// A failed write leaves the old copy as it was.
    /// </summary>
    public string? Commit(PreparedPointsMod? prepared, IReadOnlyList<PointsCurveChange> changes)
    {
        var existing = Existing;
        if (prepared is null)
        {
            if (existing is not null)
            {
                library.Delete(existing.FolderName);
            }

            return null;
        }

        var folderName = existing?.FolderName ?? library.CreateBlankMod(DisplayName, "Icarus Starlink").FolderName;
        try
        {
            WriteWithRollback(library.GetFolderPath(folderName), new ExmodPackageContents(prepared.Package, prepared.Assets),
                System.Text.Json.JsonSerializer.Serialize(changes));
        }
        catch (Exception) when (existing is null)
        {
            // Made by this call: a blank mod left behind would show in the Library as a broken copy.
            try
            {
                library.Delete(folderName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }

        library.Refresh();
        return folderName;
    }

    /// <summary>
    /// Writes the mod over its last copy, all or nothing: the old .EXMOD, curves and settings (a few
    /// KB) are kept in memory first, and a failed write puts them back and removes what it added.
    /// The old curves used to be deleted first, so a failed write left the Player row pointing at
    /// curves that were gone. Once written, curves the new mod no longer uses are removed.
    /// </summary>
    internal static void WriteWithRollback(string folder, ExmodPackageContents contents, string settingsJson)
    {
        var exmodFolder = Path.Combine(folder, "Extracted Mods");
        var assetFolder = Path.Combine(folder, AssetFolder.Replace('/', Path.DirectorySeparatorChar));
        var settingsPath = Path.Combine(folder, SettingsFile);
        List<string> FilesNow() =>
        [
            .. new[] { exmodFolder, assetFolder }.Where(Directory.Exists)
                .SelectMany(d => Directory.GetFiles(d, "*", SearchOption.AllDirectories))
                .Append(settingsPath)
                .Where(File.Exists),
        ];

        var before = FilesNow().ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        try
        {
            ExmodFolder.Write(folder, contents);
            AtomicFile.WriteAllText(settingsPath, settingsJson);
        }
        catch (Exception)
        {
            foreach (var added in FilesNow().Where(path => !before.ContainsKey(path)))
            {
                TryDelete(added);
            }

            foreach (var (path, bytes) in before)
            {
                try
                {
                    if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                    {
                        AtomicFile.WriteAllBytes(path, bytes);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            throw;
        }

        var used = contents.Assets
            .Select(a => Path.GetFullPath(Path.Combine(folder, a.RelativePath.Replace('/', Path.DirectorySeparatorChar))))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(assetFolder))
        {
            foreach (var old in Directory.GetFiles(assetFolder, "*", SearchOption.AllDirectories).Where(f => !used.Contains(Path.GetFullPath(f))))
            {
                TryDelete(old);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
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
