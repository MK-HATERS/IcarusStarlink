using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IcarusStarlink.PakIO.World;

/// <summary>
/// Mission spots in the names players see. A spot's tag (as read from the level: category "Faction
/// Mission · Forest", name "Range · Bear · Popup") is matched to the game's own mission names:
/// D_ProspectList's prospects on the world (Tier1_Forest_Range → "POTSHOT", GH_RG_B → "HABITAT",
/// OLY_SQ_Norex → "BLACK MARKET") and D_Quests' BW story quests (BW5_Quest → "Blacksand").
/// Spots shared by many missions (Objective · Base Location) keep a plain description.
/// </summary>
public static partial class WorldMissionNames
{
    /// <summary>The names to match against, for one world: "region|type", "hunt|code|letter", "npc|who", "bw|n" → the mission's name(s).</summary>
    public static IReadOnlyDictionary<string, string> Read(WorldTableSource source, string terrain)
    {
        var names = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(string key, string name)
        {
            if (!names.TryGetValue(key, out var list))
            {
                names[key] = list = [];
            }

            if (!list.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(name);
            }
        }

        var prospects = source.Table("Prospects/D_ProspectList.json");
        foreach (var (row, data) in prospects?.Rows ?? [])
        {
            var onThisWorld = prospects!.Get(data, "Terrain") is JsonObject t && t["RowName"]?.GetValue<string>() is { } r
                && string.Equals(r, terrain, StringComparison.OrdinalIgnoreCase);
            if (!onThisWorld || WorldMapCatalog.TextOf(prospects.Get(data, "DropName")) is not { Length: > 0 } name)
            {
                continue;
            }

            if (Faction().Match(row) is { Success: true } f)
            {
                // Under both "stockpile" and "stockpile2" (a spot's tag names the second one so), and "wsstockpile" for WS_Stockpile.
                var type = Squash(f.Groups[2].Value);
                Add($"region|{f.Groups[1].Value}|{type}", name);
                if (f.Groups[3].Success && f.Groups[3].Value != "0")
                {
                    Add($"region|{f.Groups[1].Value}|{type}{f.Groups[3].Value}", name);
                }
            }
            else if (Hunt().Match(row) is { Success: true } h)
            {
                Add($"hunt|{h.Groups[1].Value}|{h.Groups[2].Value}", name);
            }
            else if (SideQuest().Match(row) is { Success: true } q)
            {
                Add($"npc|{q.Groups[1].Value}", name);
            }
        }

        var quests = source.Table("Quests/D_Quests.json");
        foreach (var (row, data) in quests?.Rows ?? [])
        {
            if (StoryQuest().Match(row) is { Success: true } s && WorldMapCatalog.TextOf(quests!.Get(data, "Description")) is { Length: > 0 } name)
            {
                Add($"bw|{s.Groups[1].Value}", name);
            }
        }

        return names.ToDictionary(kv => kv.Key, kv => string.Join(" or ", kv.Value), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Tier1_Forest_Range, Tier4_Desert_Extermination_2 (→ Desert, Extermination, 2), Tier1_Forest_WS_Stockpile (→ Forest, WS_Stockpile).</summary>
    [GeneratedRegex(@"^Tier\d+_([A-Za-z]+)_([A-Za-z_]+?)(?:_(\d+))?$")]
    private static partial Regex Faction();

    /// <summary>GH_RG_B, GH_Ape_C2, GH_IM_Final → RG, B.</summary>
    [GeneratedRegex(@"^GH_([A-Za-z]+)_([A-Za-z0-9]+)$")]
    private static partial Regex Hunt();

    /// <summary>OLY_SQ_Norex → Norex.</summary>
    [GeneratedRegex(@"^[A-Z]+_SQ_([A-Za-z]+)$")]
    private static partial Regex SideQuest();

    /// <summary>BW5_Quest → 5.</summary>
    [GeneratedRegex(@"^BW(\d+)_Quest$")]
    private static partial Regex StoryQuest();

    /// <summary>
    /// A mission spot in plain words: the kind to filter by ("POTSHOT (Forest range)", "Mission
    /// objective spots: base location") and a line about the spot itself ("Bear · Popup").
    /// </summary>
    public static (string Category, string Spot) Describe(string? category, string name, IReadOnlyDictionary<string, string> missions)
    {
        var parts = (category ?? "").Split(" · ", StringSplitOptions.TrimEntries);
        var group = parts.Length > 0 ? Squash(parts[0]) : "";
        var area = parts.Length > 1 ? parts[1] : "";
        var bits = name.Split(" · ", StringSplitOptions.TrimEntries);

        switch (group)
        {
            case "factionmission" when string.Equals(area, "NPC", StringComparison.OrdinalIgnoreCase):
            {
                var who = bits.FirstOrDefault() ?? "";
                var match = missions.FirstOrDefault(kv => kv.Key.StartsWith("npc|", StringComparison.OrdinalIgnoreCase)
                    && (Squash(kv.Key[4..]).StartsWith(Squash(who), StringComparison.Ordinal) || Squash(who).StartsWith(Squash(kv.Key[4..]), StringComparison.Ordinal)));
                return match.Value is { } mission
                    ? ($"{mission} (side mission)", Rest(bits, 1))
                    : ("Side missions", name);
            }

            case "factionmission":
            {
                var type = bits.FirstOrDefault() ?? "";
                return missions.TryGetValue($"region|{Squash(area)}|{Squash(type)}", out var mission)
                    || missions.TryGetValue($"region|{Squash(area)}|{Squash(type).TrimEnd("0123456789".ToCharArray())}", out mission)
                    ? ($"{mission} ({area} {SplitDigits(type).ToLowerInvariant()})", Rest(bits, 1))
                    : ($"{area} mission spots{(bits.Length > 1 ? $": {SplitDigits(type)}" : "")}", bits.Length > 1 ? Rest(bits, 1) : name);
            }

            case "greathunts":
            {
                var code = HuntCode(area);
                var letter = bits.FirstOrDefault() ?? "";
                return missions.TryGetValue($"hunt|{code}|{Squash(letter)}", out var mission)
                    ? ($"{mission} ({area} hunt)", Rest(bits, 1))
                    : ($"{area} hunt spots{(bits.Length > 1 ? $": {letter}" : "")}", bits.Length > 1 ? Rest(bits, 1) : name);
            }

            case "objective" when StoryNumber().Match(area) is { Success: true } bw:
                return missions.TryGetValue($"bw|{bw.Groups[1].Value}", out var story)
                    ? ($"{story} (story mission)", name)
                    : ($"Story mission ({area})", name);

            case "objective":
                return ($"Mission objective spots: {area.ToLowerInvariant()}", "Used by many missions");

            default:
                return ("Other quest spots", name);
        }
    }

    [GeneratedRegex(@"^BW\s*(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex StoryNumber();

    /// <summary>"Rock Golem" → "RG", "Ice Mammoth" → "IM", "Ape" → "Ape" (as the hunts' prospects are named).</summary>
    private static string HuntCode(string hunt)
    {
        var words = hunt.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 1 ? string.Concat(words.Select(w => char.ToLowerInvariant(w[0]))) : Squash(hunt);
    }

    /// <summary>"Faction Mission" / "Factionmission" → "factionmission"; "defense" as the prospects spell it ("defence").</summary>
    private static string Squash(string text) =>
        new string([.. text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]).Replace("defense", "defence");

    /// <summary>"Stockpile2" → "Stockpile 2".</summary>
    private static string SplitDigits(string text) => Regex.Replace(text, @"(?<=[A-Za-z])(?=\d)", " ");

    private static string Rest(string[] bits, int skip) => bits.Length > skip ? string.Join(" · ", bits.Skip(skip)) : "";
}
