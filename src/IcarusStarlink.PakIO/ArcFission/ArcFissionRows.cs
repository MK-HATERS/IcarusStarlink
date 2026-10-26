using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IcarusStarlink.Core.Server;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.ArcFission;

/// <summary>
/// One row an ArcFission mod adds ("Crafting-D_ProcessorRecipes.json", "Wireless_Power_Node", the row).
/// Name is the row's Lua name, which the library checks (row already in the game, row added by two
/// mods); the game knows the row by its json's "Name", which ToPackage names it by.
/// </summary>
public sealed record ArcFissionRow(string CurrentFile, string Name, JsonObject Row);

/// <param name="RowsHash">SHA-1 of the rows file as written (what ArcFission Library's own fingerprint uses).</param>
public sealed record ArcFissionMod(string Id, string Name, string Version, string RowsHash, IReadOnlyList<ArcFissionRow> Rows);

/// <summary>The ArcFission mods turned on in the game, and what stopped any of them being read (said, never thrown).</summary>
public sealed record ArcFissionScan(IReadOnlyList<ArcFissionMod> Mods, IReadOnlyList<string> Problems)
{
    /// <summary>
    /// The mods that add rows and their rows files: changes when one is added, removed or updated.
    /// A mod with no rows (QuickSave only registers with the library) is left out, as the library
    /// leaves it out of its own fingerprint: updating it changes nothing in the data.
    /// </summary>
    public string Fingerprint { get; } = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(
        string.Join('\n', Mods.Where(m => m.Rows.Count > 0).Select(m => $"{m.Id}@{m.Version}:{m.RowsHash}"))))).ToLowerInvariant();

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

                // These files come from downloads: one that can't be read (whatever it throws) is
                // said, and every other mod's rows still count.
                try
                {
                    mods.Add(ReadModCached(folder, libraryVersion));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    problems.Add($"{folderName}: its ArcFission files can't be read ({ex.Message}), so its rows aren't in the merge.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            problems.Add($"The UE4SS Mods folder can't be read ({ex.Message}), so no ArcFission rows are in the merge.");
        }

        // The library sorts by id (lowercase) and keeps the first mod of an id.
        var byId = mods.OrderBy(m => m.Id.ToLowerInvariant(), StringComparer.Ordinal).DistinctBy(m => m.Id).ToList();
        return new ArcFissionScan(byId, problems);
    }

    /// <summary>A file larger than this isn't read (WirelessPower's rows.lua is 11 KB).</summary>
    private const long MaxFileBytes = 4 * 1024 * 1024;

    /// <summary>Each mod as last read, by folder: read again only when its card or rows file changed (the scan runs on every queue change).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Stamp, ArcFissionMod Mod)> ReadCache = new(StringComparer.OrdinalIgnoreCase);

    private static ArcFissionMod ReadModCached(string modFolder, string? libraryVersion)
    {
        var arcFissionFolder = Path.Combine(modFolder, "ArcFission");
        var stamp = libraryVersion + "|" + string.Join("|", Directory.GetFiles(arcFissionFolder).Order(StringComparer.OrdinalIgnoreCase)
            .Select(f => new FileInfo(f)).Select(f => $"{f.Name}:{f.Length}:{f.LastWriteTimeUtc.Ticks}"));
        if (ReadCache.TryGetValue(modFolder, out var cached) && cached.Stamp == stamp)
        {
            return cached.Mod;
        }

        var mod = ReadMod(modFolder, libraryVersion);
        ReadCache[modFolder] = (stamp, mod);
        return mod;
    }

    private static string ReadText(string path)
    {
        if (new FileInfo(path).Length > MaxFileBytes)
        {
            throw new FormatException($"{Path.GetFileName(path)} is larger than {MaxFileBytes / (1024 * 1024)} MB");
        }

        return File.ReadAllText(path);
    }

    /// <summary>One mod folder's identity card and rows. A FormatException says why the library wouldn't take it.</summary>
    public static ArcFissionMod ReadMod(string modFolder, string? libraryVersion)
    {
        var arcFissionFolder = Path.Combine(modFolder, "ArcFission");
        if (LuaData.Parse(ReadText(Path.Combine(arcFissionFolder, "mod.lua"))) is not LuaTable card)
        {
            throw new FormatException("its identity card ArcFission/mod.lua isn't a table");
        }

        if (card.Fields.GetValueOrDefault("arcfission") is not double format || format != Format
            || card.String("author") != Signature || card.String("id") is not { Length: > 0 } id)
        {
            throw new FormatException("its ArcFission/mod.lua isn't a valid ArcFission identity card (arcfission, author, id)");
        }

        // The library's vercmp takes whatever the card says (tostring): library = 2, a number, needs 2.
        var needs = card.Fields.GetValueOrDefault("library") switch
        {
            string s => s,
            double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => null,
        };
        if (needs is not null && libraryVersion is not null && CompareVersions(needs, libraryVersion) > 0)
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

        // The library reads join(dir, "ArcFission", rows or "rows.lua"): only a missing rows means
        // rows.lua. rows = true or a table makes that join throw, which stops the library's whole
        // check (no data rebuilt for any mod); a number names a file "5" that a mod never ships.
        var rowsFileName = card.Fields.GetValueOrDefault("rows") switch
        {
            null => "rows.lua",
            string s => s,
            double => throw new FormatException("its ArcFission/mod.lua says rows = a number, not a file name, so the ArcFission Library finds no rows file and leaves it out"),
            var other => throw new FormatException($"its ArcFission/mod.lua says rows = {(other is LuaTable ? "a table" : "true")}, which the ArcFission Library can't use as a file name: the library stops with an error and builds no ArcFission data until it's fixed"),
        };
        if (rowsFileName.Length == 0 || rowsFileName != Path.GetFileName(rowsFileName)
            || rowsFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || rowsFileName.Any(char.IsControl))
        {
            throw new FormatException($"its rows file '{rowsFileName}' isn't a file name inside its ArcFission folder");
        }

        var rowsText = ReadText(Path.Combine(arcFissionFolder, rowsFileName));
        if (LuaData.Parse(rowsText) is not LuaTable rowsFile || rowsFile.Table("tables") is not { } tables)
        {
            throw new FormatException($"its {rowsFileName} has no 'tables'");
        }

        var rows = new List<ArcFissionRow>();
        foreach (var (table, list) in tables.Fields.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            if (list is not LuaTable entries || table.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(table) || table.Any(char.IsControl))
            {
                throw new FormatException($"its rows for '{table}' aren't a list of rows");
            }

            // "Crafting/D_ProcessorRecipes.json" is the EXMOD CurrentFile "Crafting-D_ProcessorRecipes.json".
            var currentFile = table.Replace('\\', '/').Replace('/', '-');
            // As the library's ipairs reads it: { a, b } or { [1] = a, [2] = b }, up to the first nil.
            foreach (var entry in entries.Sequence())
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
    /// Those checks use the row's Lua name, as the library's do; the row itself is named by its
    /// json's "Name", because the library splices the json in as written and that's the name the
    /// game reads (so the merge and the game can't know the row by two names).
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
        var files = new Dictionary<string, ExmodFileRow>(StringComparer.OrdinalIgnoreCase);
        // "table|name" of each row put in the package, by the name the game knows it by.
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

                // The library checks the row by its Lua name but adds the json text as written, so
                // the game knows the row by the json's "Name". The merge names it the same way.
                var tableName = GameTableIndex.TableNameOf(row.CurrentFile);
                if ((row.Row["Name"] as JsonValue)?.TryGetValue<string>(out var gameName) != true || string.IsNullOrEmpty(gameName))
                {
                    problems?.Add($"{mod.Name}: row '{row.Name}' in {tableName} has no \"Name\" in its json, so the game gets a row with no name; it's left out of the merge.");
                    continue;
                }

                var renamed = !string.Equals(gameName, row.Name, StringComparison.Ordinal);
                if ((renamed && gameIndex.HasRow(row.CurrentFile, gameName)) || !emitted.Add($"{row.CurrentFile}|{gameName}"))
                {
                    problems?.Add($"{mod.Name}: row '{row.Name}' in {tableName} is named '{gameName}' in its json, a row that's already there, so the game would have two rows of that name; it's left out of the merge.");
                    continue;
                }

                if (renamed)
                {
                    problems?.Add($"{mod.Name}: row '{row.Name}' in {tableName} is named '{gameName}' in its json; the game knows it as '{gameName}', so the merge uses that name.");
                }

                if (!files.TryGetValue(row.CurrentFile, out var file))
                {
                    files[row.CurrentFile] = file = new ExmodFileRow { CurrentFile = row.CurrentFile };
                    package.Rows.Add(file);
                }

                file.FileItems.Add(ExmodFileItem.FromRow(gameName, row.Row));
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
            try
            {
                if (letters.StartsWith("ArcFissionLibrary", StringComparison.OrdinalIgnoreCase) && File.Exists(main)
                    && LibraryVersionLine().Match(ReadText(main)) is { Success: true } match)
                {
                    return match.Groups[1].Value;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Unreadable: the version check is skipped, as when there's no version line.
            }
        }

        return null;
    }

    [GeneratedRegex(@"local\s+LIBRARY_VERSION\s*=\s*""([^""]+)""")]
    private static partial Regex LibraryVersionLine();

    /// <summary>"1.2.0" vs "1.10": the library's own vercmp (number parts, missing ones 0).</summary>
    private static int CompareVersions(string a, string b)
    {
        // Compared as digit strings (leading zeros off, then the longer is bigger, then digit by
        // digit), so a card with a 30-digit number can't overflow anything. ASCII digits only, as
        // Lua's %d.
        static List<string> Parts(string v) => [.. AsciiDigits().Matches(v).Select(m => m.Value.TrimStart('0'))];
        var (pa, pb) = (Parts(a), Parts(b));
        for (var i = 0; i < Math.Max(pa.Count, pb.Count); i++)
        {
            var (x, y) = (i < pa.Count ? pa[i] : "", i < pb.Count ? pb[i] : "");
            var order = x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x, y);
            if (order != 0)
            {
                return Math.Sign(order);
            }
        }

        return 0;
    }

    [GeneratedRegex("[0-9]+")]
    private static partial Regex AsciiDigits();
}
