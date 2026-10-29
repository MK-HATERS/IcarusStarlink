using System.IO;
using System.Text.Json;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Assets;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Services;

/// <summary>Where a save row the game doesn't have comes from.</summary>
public enum SaveRowOrigin
{
    /// <summary>A mod installed in the game right now adds it: in use, kept.</summary>
    InstalledMod,

    /// <summary>A mod in the Library adds it, but it isn't installed: kept, it may be turned back on.</summary>
    LibraryModNotInstalled,

    /// <summary>The game had it once and removed it: nothing reads it any more.</summary>
    RemovedByGame,

    /// <summary>No installed mod, Library mod or game version ever had it: left behind by a mod that's gone.</summary>
    Leftover,

    /// <summary>
    /// A banked item named by its item template instead of its item row ("LegendaryWeapon_ApeClub"
    /// where the game's row is "LegendaryWeapon_Ape_Club", found in the user's bank 2026-10-23):
    /// a real game item written wrongly, probably by a tool, so never taken out as mod junk.
    /// </summary>
    Misnamed,
}

/// <param name="Label">"From Ferani Buyable Beasts (installed)", "From a mod you no longer have".</param>
public sealed record SaveRowSource(SaveRowOrigin Origin, string Label)
{
    /// <summary>Safe to take out of the save: nothing installed or in the Library gives it.</summary>
    public bool IsRemovable => Origin is SaveRowOrigin.RemovedByGame or SaveRowOrigin.Leftover;
}

/// <summary>
/// Tells apart the save rows the game doesn't have (talents, workshop research, missions, banked
/// items): which come from a mod in use, which from a Library mod that isn't installed, which the
/// game removed itself, and which are leftovers from mods the user no longer has (the user's ask,
/// 2026-10-23: "remove unused/leftover mod junk"; their save had 48 such talents).
///
/// "In use" is what's installed in the game: the paks in Content\Paks\mods (the merge and any
/// other pak there), read in place. A row is named after the Library mod that adds it: its EXMOD
/// rows, or, for a pak-only mod, its own paks' tables. Rows the game once had come from the
/// shipped row history. UE4SS mods that add rows through an ArcFission rows.lua are read too
/// (installed in the game, and the Library's copies in <paramref name="ue4ssLibraryFolder"/>);
/// other script mods can't be read, which the page says.
/// </summary>
/// <param name="gameNames">The app's shared game-table reader: the game's own row names come from it instead of parsing D_Talents, D_ItemsStatic and D_ItemTemplate again on every read. Null reads the files here.</param>
public sealed class SaveModSources(ILibraryRepository library, ISettingsService settings, string dataFolder, string? ue4ssLibraryFolder = null,
    Func<bool>? isGameDataOutdated = null, SaveGameNames? gameNames = null)
{
    public const string TalentsFile = "Talents-D_Talents.json";
    public const string ItemsFile = "Items-D_ItemsStatic.json";

    private static readonly string[] Files = [TalentsFile, ItemsFile];

    /// <summary>Reads everything once (seconds at most: every Library mod and the installed paks); call off the UI thread.</summary>
    public Index Read()
    {
        var installed = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var canTellInstalled = false;
        if (settings.Current.IcarusContentPath is { Length: > 0 } content)
        {
            var mods = Path.Combine(content, "Paks", "mods");
            if (HasPaksItCantRead(content))
            {
                // A pak in a subfolder of mods, or loose in Paks beside the game's own: the game loads
                // it but it isn't read here, so its rows can't be told from leftovers (review, 2026-10-23).
                canTellInstalled = false;
            }
            else if (!Directory.Exists(mods) || Directory.GetFiles(mods, "*.pak").Length == 0)
            {
                canTellInstalled = Directory.Exists(content);
            }
            else if (ModPakContents.TryOpen(mods) is { } paks)
            {
                using (paks)
                {
                    foreach (var file in Files)
                    {
                        installed[file] = RowNamesIn(paks.TryRead(PakPath(file)));
                    }
                }

                canTellInstalled = true;
            }
        }

        var byMod = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
        void Add(string file, string row, string mod)
        {
            var rows = byMod.TryGetValue(file, out var r) ? r : byMod[file] = new(StringComparer.OrdinalIgnoreCase);
            (rows.TryGetValue(row, out var list) ? list : rows[row] = []).Add(mod);
        }

        var anyUnreadable = false;
        var libraryUnreadable = false;
        IReadOnlyList<LibraryEntry> entries;
        try
        {
            entries = library.GetAll();
        }
        catch (Exception)
        {
            entries = [];
            libraryUnreadable = true;
        }

        foreach (var entry in entries.Where(e => !e.IsMissingFromLibrary))
        {
            var name = entry.DisplayNameOverride ?? entry.Name;
            try
            {
                var folder = library.GetFolderPath(entry.FolderName);
                if (!entry.IsOpaquePak)
                {
                    foreach (var row in ExmodFolder.Read(folder).Package.Rows.Where(r => Files.Contains(r.CurrentFile, StringComparer.OrdinalIgnoreCase)))
                    {
                        foreach (var item in row.FileItems)
                        {
                            Add(row.CurrentFile, item.Name, name);
                        }
                    }
                }
                else
                {
                    using var paks = ModPakContents.TryOpen(folder) ?? throw new IOException($"Can't read the paks of {name}.");
                    foreach (var file in Files)
                    {
                        foreach (var row in RowNamesIn(paks.TryRead(PakPath(file))))
                        {
                            Add(file, row, name);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // A mod that can't be read might be where a row comes from, so nothing is called a leftover then.
                anyUnreadable = true;
            }
        }

        // UE4SS mods that add rows the ArcFission way (an ArcFission\rows.lua): WirelessPower adds its
        // Wireless_Power_Node talent like this, which looked like a leftover before (2026-10-23).
        // The game's installed ones count as installed; the Library's own copies name the mod.
        if (settings.Current.IcarusContentPath is { Length: > 0 } contentPath)
        {
            foreach (var (file, row, mod) in ArcFissionRowsUnder(SafeModsFolder(contentPath)))
            {
                (installed.TryGetValue(file, out var set) ? set : installed[file] = new(StringComparer.OrdinalIgnoreCase)).Add(row);
                Add(file, row, mod);
            }
        }

        foreach (var (file, row, mod) in ArcFissionRowsUnder(ue4ssLibraryFolder))
        {
            Add(file, row, mod);
        }

        canTellInstalled &= !anyUnreadable && !libraryUnreadable;

        // Without the game's own tables, or with them older than the game (an update not refreshed
        // yet), real game rows would read as leftovers: nothing is called one then (review, 2026-10-23).
        var gameRows = Files.ToDictionary(f => f, GameRowNames, StringComparer.OrdinalIgnoreCase);
        if (gameRows.Values.Any(rows => rows.Count == 0) || IsGameDataOutdated())
        {
            canTellInstalled = false;
        }

        return new Index(gameRows, installed, byMod, GameRowHistory.Load(GameRowHistory.LocalFileFor(dataFolder)), canTellInstalled,
            gameNames?.TemplateStaticRows ?? TemplateItemRows(ReadGameFile("Items-D_ItemTemplate.json")));
    }

    /// <summary>The game's own rows of one table: from the shared reader (already parsed, and locked for this background read) when there is one.</summary>
    private HashSet<string> GameRowNames(string currentFile) =>
        gameNames is { } names
            ? new HashSet<string>(names.RowNameSet(currentFile.Replace('-', '/')), StringComparer.OrdinalIgnoreCase)
            : RowNamesIn(ReadGameFile(currentFile));

    /// <summary>D_ItemTemplate row -> the D_ItemsStatic row it uses ("LegendaryWeapon_ApeClub" -> "LegendaryWeapon_Ape_Club").</summary>
    private static Dictionary<string, string> TemplateItemRows(byte[]? json)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (json is null)
        {
            return map;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var row in document.RootElement.GetProperty("Rows").EnumerateArray())
            {
                if (row.TryGetProperty("Name", out var n) && n.GetString() is { } name
                    && row.TryGetProperty("ItemStaticData", out var s) && s.ValueKind == JsonValueKind.Object
                    && s.TryGetProperty("RowName", out var r) && r.GetString() is { Length: > 0 } staticRow)
                {
                    map[name] = staticRow;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
        }

        return map;
    }

    private static string? SafeModsFolder(string contentPath)
    {
        try
        {
            return Ue4ssGamePaths.ResolveModsFolder(contentPath);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Every row an ArcFission rows.lua adds, under one folder per UE4SS mod: a file of
    /// <c>["Talents/D_Talents.json"] = { { name = "Wireless_Power_Node", json = [[...]] }, ... }</c>
    /// blocks ("format 1"), read as text; the mod is the folder's name.
    /// </summary>
    internal static IEnumerable<(string File, string Row, string Mod)> ArcFissionRowsUnder(string? modsFolder)
    {
        if (modsFolder is null || !Directory.Exists(modsFolder))
        {
            yield break;
        }

        foreach (var modFolder in Directory.EnumerateDirectories(modsFolder))
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(modFolder, "rows.lua", SearchOption.AllDirectories);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var path in files)
            {
                string[] lines;
                try
                {
                    lines = File.ReadAllLines(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                string? table = null;
                foreach (var line in lines)
                {
                    if (TableLine.Match(line) is { Success: true } t)
                    {
                        table = t.Groups[1].Value.Replace('/', '-');
                    }
                    else if (table is not null && RowLine.Match(line) is { Success: true } r)
                    {
                        yield return (table, r.Groups[1].Value, Path.GetFileName(modFolder));
                    }
                }
            }
        }
    }

    private static readonly System.Text.RegularExpressions.Regex TableLine = new(@"^\s*\[""([^""]+\.json)""\]\s*=\s*\{");
    private static readonly System.Text.RegularExpressions.Regex RowLine = new(@"^\s*\{\s*name\s*=\s*""([^""]+)""");

    private bool IsGameDataOutdated()
    {
        try
        {
            return isGameDataOutdated?.Invoke() ?? false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>Paks the game loads that aren't read here: in subfolders of Paks\mods, or loose in Paks (not the game's own pakchunks).</summary>
    private static bool HasPaksItCantRead(string contentPath)
    {
        try
        {
            var paks = Path.Combine(contentPath, "Paks");
            var mods = Path.Combine(paks, "mods");
            var nested = Directory.Exists(mods) && Directory.GetDirectories(mods).Any(d => Directory.EnumerateFiles(d, "*.pak", SearchOption.AllDirectories).Any());
            var loose = Directory.Exists(paks) && Directory.GetFiles(paks, "*.pak").Any(f => !Path.GetFileName(f).StartsWith("pakchunk", StringComparison.OrdinalIgnoreCase));
            return nested || loose;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private byte[]? ReadGameFile(string currentFile)
    {
        try
        {
            var path = Path.Combine(dataFolder, currentFile.Replace('-', Path.DirectorySeparatorChar));
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>"Talents-D_Talents.json" as it sits in a pak under Content: "data/Talents/D_Talents.json".</summary>
    private static string PakPath(string currentFile) => "data/" + currentFile.Replace('-', '/');

    private static HashSet<string> RowNamesIn(byte[]? json)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (json is null)
        {
            return names;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("Rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    if (row.TryGetProperty("Name", out var n) && n.GetString() is { Length: > 0 } name)
                    {
                        names.Add(name);
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return names;
    }

    /// <param name="CanTellInstalled">False when the game folder or its installed paks couldn't be read: then nothing is called a leftover.</param>
    public sealed class Index(
        IReadOnlyDictionary<string, HashSet<string>> gameRows,
        IReadOnlyDictionary<string, HashSet<string>> installed,
        IReadOnlyDictionary<string, Dictionary<string, List<string>>> byMod,
        GameRowHistory history,
        bool canTellInstalled,
        IReadOnlyDictionary<string, string>? templateItemRows = null)
    {
        public static Index Empty { get; } = new(new Dictionary<string, HashSet<string>>(), new Dictionary<string, HashSet<string>>(),
            new Dictionary<string, Dictionary<string, List<string>>>(), GameRowHistory.Empty, false);

        /// <summary>Null for a row the game has today (not a mod's), or when nothing can be said.</summary>
        public SaveRowSource? Classify(string currentFile, string rowName)
        {
            if (gameRows.GetValueOrDefault(currentFile)?.Contains(rowName) == true)
            {
                return null;
            }

            if (currentFile.Equals(ItemsFile, StringComparison.OrdinalIgnoreCase) && templateItemRows?.GetValueOrDefault(rowName) is { } itemRow
                && !itemRow.Equals(rowName, StringComparison.OrdinalIgnoreCase))
            {
                return new SaveRowSource(SaveRowOrigin.Misnamed,
                    $"A game item saved under its template name; the game's own name for it is {itemRow}, so it may not load. Not taken out.");
            }

            var mods = byMod.GetValueOrDefault(currentFile)?.GetValueOrDefault(rowName) is { Count: > 0 } list
                ? string.Join(", ", list.Distinct(StringComparer.OrdinalIgnoreCase))
                : null;
            if (installed.GetValueOrDefault(currentFile)?.Contains(rowName) == true)
            {
                return new SaveRowSource(SaveRowOrigin.InstalledMod, mods is null ? "From an installed mod" : $"From {mods} (installed)");
            }

            if (mods is not null)
            {
                return new SaveRowSource(SaveRowOrigin.LibraryModNotInstalled, $"From {mods}, which is in your Library but not installed");
            }

            if (!canTellInstalled)
            {
                return null;
            }

            if (history.Find(currentFile, rowName) is { } seen)
            {
                // Still in the newest game version known: the extracted tables are behind, not the game.
                return seen.Last < history.Latest
                    ? new SaveRowSource(SaveRowOrigin.RemovedByGame, $"The game removed this after {history.LabelOf(seen.Last)}; nothing uses it now")
                    : null;
            }

            return new SaveRowSource(SaveRowOrigin.Leftover, "From a mod you no longer have");
        }
    }
}
