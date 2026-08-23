using System.IO;
using System.Text.Json;

namespace IcarusStarlink.App.Services;

/// <summary>
/// Names for the save editor, straight from the game's OWN extracted data tables: a flag int in a
/// save is a row INDEX into D_CharacterFlags (per character) or D_AccountFlags (per profile), and
/// a talent RowName groups under its tree via D_Talents. Loaded lazily from the app's Data folder
/// and tolerant of it being absent (fresh install, data not extracted yet) — everything degrades
/// to raw IDs/names rather than failing, since the save data itself is still perfectly editable.
/// Reading live from the user's own extraction (rather than shipping a hardcoded list) means the
/// names stay correct across game updates for free.
/// </summary>
public sealed class SaveGameNames(string dataFolder)
{
    private IReadOnlyList<string>? _characterFlagNames;
    private IReadOnlyList<string>? _accountFlagNames;
    private IReadOnlyDictionary<string, TalentInfo>? _talents;

    public IReadOnlyList<string> CharacterFlagNames => _characterFlagNames ??= LoadRowNames(Path.Combine("Flags", "D_CharacterFlags.json"));

    public IReadOnlyList<string> AccountFlagNames => _accountFlagNames ??= LoadRowNames(Path.Combine("Flags", "D_AccountFlags.json"));

    /// <summary>Everything the save editor wants to know about each talent, keyed by the RowName the save stores. MaxRank comes from the row's own Rewards tier count (a reward-less row — blueprint/workshop unlocks, 1312 of 2227 — is a single-rank unlock).</summary>
    public IReadOnlyDictionary<string, TalentInfo> Talents => _talents ??= LoadTalents();

    public string CharacterFlagName(int id) => id >= 0 && id < CharacterFlagNames.Count ? CharacterFlagNames[id] : $"Flag {id}";

    public string AccountFlagName(int id) => id >= 0 && id < AccountFlagNames.Count ? AccountFlagNames[id] : $"Flag {id}";

    private IReadOnlyList<string> LoadRowNames(string relativePath)
    {
        try
        {
            var path = Path.Combine(dataFolder, relativePath);
            if (!File.Exists(path))
            {
                return [];
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return [.. document.RootElement.GetProperty("Rows").EnumerateArray()
                .Select(row => row.TryGetProperty("Name", out var name) ? name.GetString() ?? "?" : "?")];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private IReadOnlyDictionary<string, TalentInfo> LoadTalents()
    {
        var talents = new Dictionary<string, TalentInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(dataFolder, "Talents", "D_Talents.json");
            if (!File.Exists(path))
            {
                return talents;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var row in document.RootElement.GetProperty("Rows").EnumerateArray())
            {
                if (!row.TryGetProperty("Name", out var name) || name.GetString() is not { } talentName)
                {
                    continue;
                }

                // "Reroute" (92 rows) and "MutuallyExclusive" (6 rows) are invisible tree-plumbing
                // nodes — 0x0 size, no rewards, no display name — that route lines between real
                // talents in the game's own tree UI. They are not talents a player can have, so
                // they never belong in an editor's list. (A save that somehow carries one still
                // shows it, via the fallback path — nothing in the save is ever hidden.)
                if (row.TryGetProperty("TalentType", out var talentType)
                    && talentType.GetString() is "Reroute" or "MutuallyExclusive")
                {
                    continue;
                }

                var tree = row.TryGetProperty("TalentTree", out var treeRef) && treeRef.TryGetProperty("RowName", out var treeName)
                    ? treeName.GetString() ?? ""
                    : "";
                var maxRank = row.TryGetProperty("Rewards", out var rewards) && rewards.ValueKind == JsonValueKind.Array && rewards.GetArrayLength() > 0
                    ? rewards.GetArrayLength()
                    : 1;

                // 49 real talents (Stone Axe, basic stacks, ...) are unlocked from the start and
                // NEVER written into a save — without this bit the editor would show them "locked".
                var defaultUnlocked = row.TryGetProperty("bDefaultUnlocked", out var b) && b.ValueKind == JsonValueKind.True;

                talents.TryAdd(talentName, new TalentInfo(
                    ParseLocText(row, "DisplayName") ?? talentName,
                    ParseLocText(row, "Description") ?? "",
                    tree,
                    maxRank,
                    defaultUnlocked));
            }
        }
        catch (Exception)
        {
            // Missing/corrupt table just means talents show by RowName with an uncapped rank.
        }

        return talents;
    }

    /// <summary>The tables wrap localized text as NSLOCTEXT("Table", "Key", "The English text") — the last quoted argument is the display string.</summary>
    private static string? ParseLocText(JsonElement row, string property)
    {
        if (!row.TryGetProperty(property, out var value) || value.GetString() is not { } raw)
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(raw, "\"((?:[^\"\\\\]|\\\\.)*)\"\\s*\\)\\s*$");
        return match.Success ? match.Groups[1].Value.Replace("\\\"", "\"") : raw;
    }
}

/// <param name="MaxRank">The most ranks this talent can hold, from its own Rewards tiers — what the editor caps rank controls at.</param>
/// <param name="IsDefaultUnlocked">The row's own bDefaultUnlocked — the game grants it from the start WITHOUT writing it into the save, so the editor must show it as unlocked even at rank 0.</param>
public sealed record TalentInfo(string DisplayName, string Description, string Tree, int MaxRank, bool IsDefaultUnlocked);
