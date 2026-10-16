using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// The values the game had before its recent updates (from the Refresh game data change reports),
/// indexed once so a whole-library check can ask "did the game have this value before?" per field
/// without regrouping the history for every mod.
/// </summary>
public sealed class GameValueHistory
{
    public static GameValueHistory Empty { get; } = new([]);

    private readonly Dictionary<string, List<JsonNode>> _oldValues = new(StringComparer.OrdinalIgnoreCase);

    public GameValueHistory(IEnumerable<FieldChange> changes)
    {
        foreach (var change in changes)
        {
            if (change.OriginalValue is null || change.IsNewItem)
            {
                continue;
            }

            var key = Key(change.CurrentFile, change.ItemName, change.FieldName);
            if (!_oldValues.TryGetValue(key, out var values))
            {
                _oldValues[key] = values = [];
            }

            values.Add(change.OriginalValue);
        }
    }

    public int Count => _oldValues.Count;

    public bool WasOldValue(string currentFile, string row, string field, JsonNode? value) =>
        _oldValues.TryGetValue(Key(currentFile, row, field), out var values) && values.Any(old => JsonNode.DeepEquals(old, value));

    private static string Key(string file, string row, string field) => $"{file}|{row}|{field}";
}

/// <summary>
/// One mod's remembered fix choices, so the manual part teaches the automatic part:
/// a value the user chose to keep stops being flagged (until the mod's or the game's value
/// changes, when it's worth asking again); any other choice (use the game's, merge, rename) is
/// made by itself from then on, including after every re-conversion of a prebuilt pak, keyed by
/// the mod's own value so it survives the game changing its side again.
/// </summary>
public sealed class ModFixDecisions
{
    public ModFixDecisions(IDictionary<string, ModIssueFix>? entries = null)
    {
        Entries = new Dictionary<string, ModIssueFix>(entries ?? new Dictionary<string, ModIssueFix>(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Stored as is (the key is opaque: kind of choice, place, value fingerprints).</summary>
    public Dictionary<string, ModIssueFix> Entries { get; }

    public int Count => Entries.Count;

    public void Remember(ModIssue issue, ModIssueFix fix)
    {
        Entries.Remove(KeepKey(issue));
        Entries.Remove(FixKey(issue));
        if (fix == ModIssueFix.KeepMod)
        {
            Entries[KeepKey(issue)] = fix;
        }
        else
        {
            Entries[FixKey(issue)] = fix;
        }
    }

    public bool TryGet(ModIssue issue, out ModIssueFix fix) =>
        Entries.TryGetValue(KeepKey(issue), out fix) || Entries.TryGetValue(FixKey(issue), out fix);

    private static string Place(ModIssue issue) => $"{issue.CurrentFile}|{issue.ItemName}|{issue.FieldName}";

    /// <summary>A kept value is about both sides: if either changes, the question is new.</summary>
    private static string KeepKey(ModIssue issue) =>
        $"keep|{Place(issue)}|{Fingerprint(issue.ModValue)}|{Fingerprint(issue.GameValue)}|{issue.SuggestedRow}";

    /// <summary>A fix is about the mod's value ("this old value isn't wanted"), whatever the game has now.</summary>
    private static string FixKey(ModIssue issue) =>
        $"fix|{Place(issue)}|{Fingerprint(issue.ModValue)}|{issue.SuggestedRow}";

    private static string Fingerprint(JsonNode? value) =>
        value is null ? "-" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToJsonString())))[..16];
}
