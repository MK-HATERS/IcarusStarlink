using System.Text.Json.Nodes;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class SavesViewModel
{
    // --- Duplicate rows: the same talent, currency, flag, creature or
    //     accolade listed twice in a save file. The game didn't write them; a tool may have. The save
    //     check says so, and its fix merges each into one at the next Save, keeping the highest rank,
    //     count or points any copy had. ---

    private const string CharactersFile = "Characters.json";
    private const string ProfileFile = "Profile.json";
    private const string AccoladesFile = "Accolades.json";
    private const string BestiaryFile = "BestiaryData.json";

    /// <summary>Files whose repeated entries are merged at the next Save.</summary>
    private readonly HashSet<string> _mergeDuplicatesIn = new(StringComparer.Ordinal);

    /// <summary>One list in a file: where it is, what it holds, how an entry is told apart, and which number to keep the highest of.</summary>
    private sealed record SaveList(string File, string Area, string What, JsonArray Array, Func<JsonNode, string?> KeyOf, string? MaxField);

    private static string? RowNameOf(JsonNode node) => SaveJson.String((node as JsonObject)?["RowName"]);

    private static string? NestedRowNameOf(JsonNode node, string field) => SaveJson.String(((node as JsonObject)?[field] as JsonObject)?["RowName"]);

    private static string? ValueKeyOf(JsonNode node) => node is JsonValue ? node.ToJsonString() : null;

    private IEnumerable<SaveList> SaveLists()
    {
        foreach (var character in _characterNodes)
        {
            var name = SaveJson.String(character["CharacterName"]) ?? "A character";
            if (character["Talents"] is JsonArray talents)
            {
                yield return new(CharactersFile, name, "talents", talents, RowNameOf, "Rank");
            }

            if (character["UnlockedFlags"] is JsonArray flags)
            {
                yield return new(CharactersFile, name, "flags", flags, ValueKeyOf, null);
            }
        }

        if (_profile is { } profile)
        {
            if (profile["Talents"] is JsonArray research)
            {
                yield return new(ProfileFile, "Account", "research and missions", research, RowNameOf, "Rank");
            }

            if (profile["MetaResources"] is JsonArray currencies)
            {
                yield return new(ProfileFile, "Account", "currencies", currencies, n => SaveJson.String((n as JsonObject)?["MetaRow"]), "Count");
            }

            if (profile["UnlockedFlags"] is JsonArray flags)
            {
                yield return new(ProfileFile, "Account", "unlocks", flags, ValueKeyOf, null);
            }
        }

        if (_accoladesRoot?["CompletedAccolades"] is JsonArray accolades)
        {
            yield return new(AccoladesFile, "Accolades", "accolades", accolades, n => NestedRowNameOf(n, "Accolade"), null);
        }

        if (_bestiaryRoot?["BestiaryTracking"] is JsonArray creatures)
        {
            yield return new(BestiaryFile, "Bestiary", "creatures", creatures, n => NestedRowNameOf(n, "BestiaryGroup"), "NumPoints");
        }
    }

    /// <summary>Each list with repeats, not yet set to be merged: for the save check.</summary>
    internal IEnumerable<(string Area, string What, string Message, Action Merge)> DuplicateRows()
    {
        foreach (var list in SaveLists().Where(l => !_mergeDuplicatesIn.Contains(l.File)))
        {
            var repeated = SaveJson.DuplicateKeys(list.Array, list.KeyOf);
            if (repeated.Count > 0)
            {
                var file = list.File;
                yield return (list.Area, list.What,
                    $"{repeated.Count:N0} of its {list.What} {(repeated.Count == 1 ? "is" : "are")} listed more than once in {file} ({string.Join(", ", repeated.Take(4))}{(repeated.Count > 4 ? ", …" : "")}). The game doesn't write that; a tool may have.",
                    () => MergeDuplicatesAtSave(file));
            }
        }
    }

    private void MergeDuplicatesAtSave(string file)
    {
        if (_mergeDuplicatesIn.Add(file))
        {
            NotifyDirtyChanged();
        }
    }

    /// <summary>
    /// Before repeated currencies are merged: an edited currency's count goes into every copy of
    /// it, so the edit is what the merge keeps (it keeps the highest count, which used to undo an
    /// edit that lowered one copy). Talents already work this way (TalentArray.Write). When several
    /// copies were edited differently, the one lowest in the list wins.
    /// </summary>
    private void ApplyCurrencyEditsToRepeats()
    {
        if (_profile?["MetaResources"] is not JsonArray resources)
        {
            return;
        }

        foreach (var currency in Currencies.Where(c => c.IsDirty))
        {
            if (!SaveNumberRules.TryParseCount(currency.CountText, out var count))
            {
                continue;
            }

            foreach (var node in resources.OfType<JsonObject>()
                .Where(n => string.Equals(SaveJson.String(n["MetaRow"]), currency.MetaRow, StringComparison.OrdinalIgnoreCase)))
            {
                node["Count"] = (long)count;
            }
        }
    }

    /// <summary>At save time, after the backup and the edits: each set file's repeats become one entry.</summary>
    private void ApplyDuplicateMerges()
    {
        foreach (var list in SaveLists().Where(l => _mergeDuplicatesIn.Contains(l.File)))
        {
            SaveJson.MergeDuplicates(list.Array, list.KeyOf, list.MaxField);
        }
    }
}
