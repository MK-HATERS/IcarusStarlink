using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IcarusStarlink.Core.Server;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.ArcFission;

/// <summary>One row an ArcFission mod adds ("Crafting-D_ProcessorRecipes.json", "Wireless_Power_Node", the row).</summary>
public sealed record ArcFissionRow(string CurrentFile, string Name, JsonObject Row);

/// <param name="RowsHash">SHA-1 of the rows file as written (what ArcFission Library's own fingerprint uses).</param>
public sealed record ArcFissionMod(string Id, string Name, string Version, string RowsHash, IReadOnlyList<ArcFissionRow> Rows);

/// <summary>The ArcFission mods turned on in the game, and what stopped any of them being read (said, never thrown).</summary>
public sealed record ArcFissionScan(IReadOnlyList<ArcFissionMod> Mods, IReadOnlyList<string> Problems)
{
    /// <summary>The mods and their rows files: changes when one is added, removed or updated.</summary>
    public string Fingerprint { get; } = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(
        string.Join('\n', Mods.Select(m => $"{m.Id}@{m.Version}:{m.RowsHash}"))))).ToLowerInvariant();

    /// <summary>How the merge's manifest (ISL-Merged.txt) lists these rows; a different one means the installed merge has older rows.</summary>
    public string ManifestName => $"ArcFission rows {Fingerprint[..12]}";

    public bool HasRows => Mods.Any(m => m.Rows.Count > 0);
}

/// <summary>
/// ArcFission mods (WirelessPower, LumberYard…) ship only the rows they add, in
/// ue4ss\Mods\&lt;Mod&gt;\ArcFission\rows.lua, and ArcFission Library builds the game's data from
/// them at each game start (ZZ_ArcFission_Data_&lt;n&gt;_P.pak: 11 whole tables, about 19 MB). The
/// merge reads the same rows straight from the mods instead of from that pak, by the library's own
/// rules (ArcFissionLibrary\Scripts\main.lua, v1.3.0): a mod counts when its identity card
/// (ArcFission\mod.lua) has arcfission = 1, author = "ArcFission" and an id, and doesn't need a
/// newer library; a row the game already has is skipped; when two mods add the same row the first
/// (by id) keeps it. So the merge has the rows before the game has ever run with them, never
/// carries the library's copies of the game's tables (old ones after a game update), and needs no
/// UnrealPak to read them.
/// </summary>
public static partial class ArcFissionRows
{
    private const string Signature = "ArcFission";

    /// <summary>The identity-card format ArcFission Library 1.x understands.</summary>
    private const double Format = 1;

    /// <summary>
    /// The ArcFission mods turned on in the game's UE4SS Mods folder, sorted by id as the library
    /// sorts them; null when ArcFission Library itself isn't installed and on (then no rows exist in game).
    /// </summary>
    public static ArcFissionScan? Scan(string icarusContentPath)
    {
        if (!InstallManifestNames.IsArcFissionLibraryActive(icarusContentPath))
        {
            return null;
        }

        var modsFolder = Ue4ssGamePaths.ResolveModsFolder(icarusContentPath);
        var problems = new List<string>();
        var mods = new List<ArcFissionMod>();
        try
        {
            var modsTxt = Ue4ssModsTxt.Load(Path.Combine(modsFolder, "mods.txt"));
            var libraryVersion = LibraryVersion(modsFolder);
            foreach (var folder in Directory.GetDirectories(modsFolder))
            {
                var folderName = Path.GetFileName(folder);
                var card = Path.Combine(folder, "ArcFission", "mod.lua");
                if (!File.Exists(card) || !(modsTxt.IsEnabled(folderName) == true || File.Exists(Path.Combine(folder, "enabled.txt"))))
                {
                    continue;
                }

                try
                {
                    mods.Add(ReadMod(folder, libraryVersion));
                }
                catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    problems.Add($"{folderName}: its ArcFission files can't be read ({ex.Message}), so its rows aren't in the merge.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"The UE4SS Mods folder can't be read ({ex.Message}), so no ArcFission rows are in the merge.");
        }

        // The library sorts by id (lowercase) and keeps the first mod of an id.
        var byId = mods.OrderBy(m => m.Id.ToLowerInvariant(), StringComparer.Ordinal).DistinctBy(m => m.Id).ToList();
        return new ArcFissionScan(byId, problems);
    }

    /// <summary>One mod folder's identity card and rows. A FormatException says why the library wouldn't take it.</summary>
    public static ArcFissionMod ReadMod(string modFolder, string? libraryVersion)
    {
        var arcFissionFolder = Path.Combine(modFolder, "ArcFission");
        if (LuaData.Parse(File.ReadAllText(Path.Combine(arcFissionFolder, "mod.lua"))) is not LuaTable card)
        {
            throw new FormatException("its identity card ArcFission/mod.lua isn't a table");
        }

        if (card.Fields.GetValueOrDefault("arcfission") is not double format || format != Format
            || card.String("author") != Signature || card.String("id") is not { Length: > 0 } id)
        {
            throw new FormatException("its ArcFission/mod.lua isn't a valid ArcFission identity card (arcfission, author, id)");
        }

        if (card.String("library") is { } needs && libraryVersion is not null && CompareVersions(needs, libraryVersion) > 0)
        {
            throw new FormatException($"it needs ArcFission Library {needs} or newer (installed: {libraryVersion}), so the library leaves it out too");
        }

        var version = card.Fields.GetValueOrDefault("version") switch
        {
            string s => s,
            double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => "",
        };
        var name = card.String("name") ?? id;

        // rows = false: a mod that adds no data (it only registers with the library).
        if (card.Fields.GetValueOrDefault("rows") is false)
        {
            return new ArcFissionMod(id, name, version, "", []);
        }

        var rowsFileName = card.String("rows") ?? "rows.lua";
        if (rowsFileName != Path.GetFileName(rowsFileName))
        {
            throw new FormatException($"its rows file '{rowsFileName}' isn't inside its ArcFission folder");
        }

        var rowsText = File.ReadAllText(Path.Combine(arcFissionFolder, rowsFileName));
        if (LuaData.Parse(rowsText) is not LuaTable rowsFile || rowsFile.Table("tables") is not { } tables)
        {
            throw new FormatException($"its {rowsFileName} has no 'tables'");
        }

        var rows = new List<ArcFissionRow>();
        foreach (var (table, list) in tables.Fields.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            if (list is not LuaTable entries || table.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(table))
            {
                throw new FormatException($"its rows for '{table}' aren't a list of rows");
            }

            // "Crafting/D_ProcessorRecipes.json" is the EXMOD CurrentFile "Crafting-D_ProcessorRecipes.json".
            var currentFile = table.Replace('\\', '/').Replace('/', '-');
            foreach (var entry in entries.Items)
            {
                if (entry is not LuaTable row || row.String("name") is not { Length: > 0 } rowName || row.String("json") is not { } json
                    || DuplicateTolerantJson.Parse(json) is not JsonObject rowJson)
                {
                    throw new FormatException($"a row in '{table}' has no name or its json isn't a row");
                }

                rows.Add(new ArcFissionRow(currentFile, rowName, rowJson));
            }
        }

        var rowsHash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(rowsText))).ToLowerInvariant();
        return new ArcFissionMod(id, name, version, rowsHash, rows);
    }

    /// <summary>
    /// The rows as one package for the merge: only rows the game doesn't have (the library skips
    /// those too), and a row two mods add only from the first. What was left out is in problems.
    /// </summary>
    public static ExmodPackage ToPackage(ArcFissionScan scan, GameTableIndex gameIndex, ICollection<string>? problems = null)
    {
        var package = new ExmodPackage
        {
            // Listed in the merge's manifest under this name: the fingerprint says which rows it has.
            Name = scan.ManifestName,
            Author = Signature,
            Version = scan.Fingerprint[..12],
            Description = "Rows the ArcFission mods in the game add, read from their rows.lua",
            FileName = "ArcFission_Rows",
        };
        var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in scan.Mods)
        {
            foreach (var row in mod.Rows)
            {
                var key = $"{row.CurrentFile}|{row.Name}";
                if (gameIndex.HasRow(row.CurrentFile, row.Name))
                {
                    problems?.Add($"{mod.Name}: the game already has row '{row.Name}' in {GameTableIndex.TableNameOf(row.CurrentFile)}, so it's skipped (ArcFission Library skips it too).");
                    continue;
                }

                if (owner.TryGetValue(key, out var first))
                {
                    problems?.Add($"{mod.Name}: row '{row.Name}' in {GameTableIndex.TableNameOf(row.CurrentFile)} is also added by {first}; {first}'s is kept.");
                    continue;
                }

                owner[key] = mod.Name;
                if (package.Rows.FirstOrDefault(r => string.Equals(r.CurrentFile, row.CurrentFile, StringComparison.OrdinalIgnoreCase)) is not { } file)
                {
                    file = new ExmodFileRow { CurrentFile = row.CurrentFile };
                    package.Rows.Add(file);
                }

                file.FileItems.Add(ExmodFileItem.FromRow(row.Name, row.Row));
            }
        }

        return package;
    }

    /// <summary>The installed library's version (LIBRARY_VERSION in its main.lua), or null when it can't be read (then a mod's "needs library X" isn't checked).</summary>
    private static string? LibraryVersion(string modsFolder)
    {
        foreach (var folder in Directory.GetDirectories(modsFolder))
        {
            var letters = new string([.. Path.GetFileName(folder).Where(char.IsLetterOrDigit)]);
            var main = Path.Combine(folder, "Scripts", "main.lua");
            if (letters.StartsWith("ArcFissionLibrary", StringComparison.OrdinalIgnoreCase) && File.Exists(main)
                && LibraryVersionLine().Match(File.ReadAllText(main)) is { Success: true } match)
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }

    [GeneratedRegex(@"local\s+LIBRARY_VERSION\s*=\s*""([^""]+)""")]
    private static partial Regex LibraryVersionLine();

    /// <summary>"1.2.0" vs "1.10": the library's own vercmp (number parts, missing ones 0).</summary>
    private static int CompareVersions(string a, string b)
    {
        static List<long> Parts(string v) => [.. Regex.Matches(v, @"\d+").Select(m => long.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture))];
        var (pa, pb) = (Parts(a), Parts(b));
        for (var i = 0; i < Math.Max(pa.Count, pb.Count); i++)
        {
            var (x, y) = (i < pa.Count ? pa[i] : 0, i < pb.Count ? pb[i] : 0);
            if (x != y)
            {
                return x < y ? -1 : 1;
            }
        }

        return 0;
    }
}
