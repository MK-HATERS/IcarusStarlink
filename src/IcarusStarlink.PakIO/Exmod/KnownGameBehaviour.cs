using System.Text.RegularExpressions;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// What the game really does with some data, tested in game by MkHaters' Hook Scout (the dates and
/// mods are its notes'), so warnings can tell an edit that breaks something or does nothing from
/// one that works. One place for these facts: the health check, the Editor page and the mod editor
/// all read them. A fact only read from the game's code or tables says so in its words.
/// </summary>
public static partial class KnownGameBehaviour
{
    /// <summary>A table the game also has built into its code ("generated"): it counts the rows it was built with.</summary>
    /// <param name="NewRowsBreak">Rows a mod adds here load but the game can't use them (tested in game).</param>
    public sealed record BuiltInTable(string Table, bool NewRowsBreak, string What);

    public static readonly IReadOnlyDictionary<string, BuiltInTable> BuiltInTables = new Dictionary<string, BuiltInTable>(StringComparer.OrdinalIgnoreCase)
    {
        ["D_CharacterGrowth"] = new("D_CharacterGrowth", NewRowsBreak: true,
            "The game can't use rows a mod adds to this table: tames pointed at such a row showed level 0, \"-1\" XP and a broken max level "
            + "(tested in game by MkHaters' Animal Talents 2.0, 2026-10-27). Change the game's own rows here, never add new ones."),
        // The game logs a mismatch for these too when a mod adds rows; nothing visible went wrong in Hook Scout's tests.
        ["D_AIRelationships"] = new("D_AIRelationships", NewRowsBreak: false, "The game logs a row-count mismatch at start; nothing visible went wrong in tests."),
        ["D_RecipeSets"] = new("D_RecipeSets", NewRowsBreak: false, "The game logs a row-count mismatch at start; nothing visible went wrong in tests."),
        ["D_TagQueries"] = new("D_TagQueries", NewRowsBreak: false, "The game logs a row-count mismatch at start; nothing visible went wrong in tests."),
        ["D_TalentTrees"] = new("D_TalentTrees", NewRowsBreak: false, "The game logs a row-count mismatch at start; nothing visible went wrong in tests."),
    };

    /// <summary>The stat that would slow a bag's spoiling, and doesn't.</summary>
    public const string SpoilRateStat = "BaseInventorySpoilRate_+%";

    /// <summary>"GrantedAuraHealthRegen_?": a stat the game turns into an aura (on characters only).</summary>
    public static bool IsAuraStat(string statName) => statName.StartsWith("GrantedAura", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Why a stat does nothing where it is (tested in game), or null when it works as far as is known.
    /// </summary>
    /// <param name="table">"D_ItemsStatic", "D_InventoryInfo"…</param>
    /// <param name="field">The stats field: "AdditionalStats", "Stats".</param>
    /// <param name="statName">The stat's row name ("BaseInventorySpoilRate_+%"), not its "(Value=…)" key.</param>
    /// <param name="placedItem">The row is an item that is placed in the world (it has a Deployable row).</param>
    public static string? StatWithNoEffect(string table, string field, string statName, bool placedItem)
    {
        if (IsTable(table, "D_ItemsStatic") && IsField(field, "AdditionalStats") && placedItem && IsAuraStat(statName))
        {
            return "A placed item gets no aura from its stats: the game turns aura stats into auras only on characters. A bed with "
                + "GrantedAuraHealthRegen and a heater with GrantedAuraHypothermiaResistance gave nobody an aura (tested in game by MkHaters' "
                + "Hook Scout, 2026-11-01). A placed item's aura needs a UE4SS (Lua) part.";
        }

        if (string.Equals(statName, SpoilRateStat, StringComparison.OrdinalIgnoreCase)
            && ((IsTable(table, "D_InventoryInfo") && IsField(field, "Stats")) || (IsTable(table, "D_ItemsStatic") && IsField(field, "AdditionalStats"))))
        {
            return "Changes nothing: a bag's or a storage's spoil speed isn't read from this stat (tested in game by MkHaters' Hook Scout, "
                + "2026-10-23). Only a UE4SS (Lua) part can change how fast food spoils in a bag.";
        }

        return null;
    }

    private static bool IsTable(string table, string name) => string.Equals(table, name, StringComparison.OrdinalIgnoreCase);

    private static bool IsField(string field, string name) => string.Equals(field, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>One table the game said, at its last start, has more rows than it was built with.</summary>
    /// <param name="Line">The log line, for showing as proof.</param>
    public sealed record LoggedMismatch(string Table, int Loaded, int BuiltIn, string Line);

    [GeneratedRegex(@"U(?<table>\w+?)Table row count doesn't match the generated enum.*?JSON:\s*(?<json>\d+)\s*\|\s*Code:\s*(?<code>\d+)")]
    private static partial Regex MismatchLine();

    /// <summary>
    /// The game's start-up complaints about built-in tables in an Icarus.log ("UTagQueriesTable row
    /// count doesn't match the generated enum … JSON: 571 | Code: 564"), the last one per table. Read
    /// while the game or server may still be writing it; nothing when the file can't be read.
    /// </summary>
    public static IReadOnlyList<LoggedMismatch> ReadLoggedMismatches(string logPath)
    {
        var found = new Dictionary<string, LoggedMismatch>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (line.Contains("generated enum", StringComparison.Ordinal) && MismatchLine().Match(line) is { Success: true } match)
                {
                    var table = "D_" + match.Groups["table"].Value;
                    found[table] = new LoggedMismatch(table, int.Parse(match.Groups["json"].Value, System.Globalization.CultureInfo.InvariantCulture),
                        int.Parse(match.Groups["code"].Value, System.Globalization.CultureInfo.InvariantCulture), line.Trim());
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return [.. found.Values];
    }
}
