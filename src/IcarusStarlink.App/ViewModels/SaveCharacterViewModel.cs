using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.App.Services;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// One character, editing a known set of fields over its raw save JsonObject — the node itself is
/// what gets written back, so every field this class doesn't surface (cosmetic hashes, timestamps,
/// whole subsystems) survives untouched. Edits stay in the ViewModel until ApplyToNode() at save
/// time, so closing without saving really changes nothing.
///
/// Owns its own Flags and Talents collections (names from the game's own data tables via
/// SaveGameNames): flag ints are row indexes into D_CharacterFlags, and the talent list is the
/// UNION of what the save has and everything D_Talents defines — an unlearned talent is just one
/// at rank 0, so granting is ranking up, exactly how the save format itself represents it.
/// </summary>
public sealed partial class SaveCharacterViewModel : ObservableObject, IDirtyTrackable
{
    private readonly JsonObject _node;
    private readonly Action _onDirtyChanged;
    private string _originalName;
    private long _originalXp;
    private bool _originalIsMale;

    /// <summary>The character's own live JsonObject — exposed (like SaveMountViewModel/SaveInventoryItemViewModel's own Node) so SavesViewModel can keep its private _characterNodes list in sync when a whole character is duplicated or removed, not just per-field edits applied through ApplyToNode.</summary>
    public JsonObject Node => _node;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _xpText;

    public int ChrSlot { get; }

    public string Location { get; }

    public bool IsDead { get; }

    public ObservableCollection<SaveFlagViewModel> Flags { get; } = [];

    public ObservableCollection<SaveTalentViewModel> Talents { get; } = [];

    /// <summary>Raw Customization_* hash fields — see SaveCosmeticFieldViewModel's own doc comment for why these aren't name-mapped.</summary>
    public ObservableCollection<SaveCosmeticFieldViewModel> Cosmetics { get; } = [];

    [ObservableProperty]
    private bool _isMale;

    /// <summary>The prospect/location line the Overview card shows, e.g. "Prospect_Grasslands" → "Grasslands".</summary>
    public string LocationDisplay => Location.StartsWith("Prospect_", StringComparison.OrdinalIgnoreCase) ? Location["Prospect_".Length..] : Location;

    public string XpDisplay => long.TryParse(XpText, out var xp) ? xp.ToString("N0") : XpText;

    public bool IsDirty =>
        Name != _originalName
        || (long.TryParse(XpText, out var xp) ? xp != _originalXp : XpText != _originalXp.ToString())
        || Flags.Any(f => f.IsDirty)
        || Talents.Any(t => t.IsDirty)
        || Cosmetics.Any(c => c.IsDirty)
        || IsMale != _originalIsMale;

    public SaveCharacterViewModel(JsonObject node, SaveGameNames names, Action onDirtyChanged)
    {
        _node = node;
        _onDirtyChanged = onDirtyChanged;
        _originalName = node["CharacterName"]?.GetValue<string>() ?? "";
        _originalXp = node["XP"]?.GetValue<long>() ?? 0;
        _name = _originalName;
        _xpText = _originalXp.ToString();
        ChrSlot = node["ChrSlot"]?.GetValue<int>() ?? 0;
        Location = node["Location"]?.GetValue<string>() ?? "";
        IsDead = node["IsDead"]?.GetValue<bool>() ?? false;
        _originalIsMale = node["Cosmetic"]?["IsMale"]?.GetValue<bool>() ?? true;
        _isMale = _originalIsMale;

        BuildFlags(names);
        BuildTalents(names);
        BuildCosmetics();
    }

    private void BuildCosmetics()
    {
        if (_node["Cosmetic"] is not JsonObject cosmetic)
        {
            return;
        }

        foreach (var (key, value) in cosmetic)
        {
            if (key == "IsMale" || value is null)
            {
                continue;
            }

            Cosmetics.Add(new SaveCosmeticFieldViewModel(key, value.GetValue<long>().ToString(), _onDirtyChanged));
        }
    }

    private void BuildFlags(SaveGameNames names)
    {
        var unlocked = ReadIntArray(_node["UnlockedFlags"]);

        // Every flag the game's own table defines, plus any ID the save carries that the table
        // doesn't know (an older/newer game version) — nothing in the save is ever hidden.
        var count = Math.Max(names.CharacterFlagNames.Count, unlocked.Count > 0 ? unlocked.Max() + 1 : 0);
        for (var id = 0; id < count; id++)
        {
            Flags.Add(new SaveFlagViewModel(id, names.CharacterFlagName(id), unlocked.Contains(id), _onDirtyChanged));
        }
    }

    private void BuildTalents(SaveGameNames names)
    {
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (_node["Talents"] is JsonArray talentArray)
        {
            foreach (var entry in talentArray.OfType<JsonObject>())
            {
                if (entry["RowName"]?.GetValue<string>() is { } rowName)
                {
                    ranks[rowName] = entry["Rank"]?.GetValue<int>() ?? 0;
                }
            }
        }

        // Learned first (the save's own order), then everything else the game defines at rank 0.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rowName, rank) in ranks)
        {
            Talents.Add(new SaveTalentViewModel(rowName, DisplayInfoFor(names, rowName), rank, _onDirtyChanged));
            seen.Add(rowName);
        }

        foreach (var (rowName, info) in names.Talents.OrderBy(kv => kv.Value.Tree, StringComparer.OrdinalIgnoreCase).ThenBy(kv => kv.Value.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            if (!seen.Contains(rowName))
            {
                Talents.Add(new SaveTalentViewModel(rowName, new TalentDisplayInfo(info.DisplayName, info.Description, info.Tree, info.MaxRank, info.IsDefaultUnlocked), 0, _onDirtyChanged));
            }
        }
    }

    internal static TalentDisplayInfo DisplayInfoFor(SaveGameNames names, string rowName) =>
        names.Talents.TryGetValue(rowName, out var info)
            ? new TalentDisplayInfo(info.DisplayName, info.Description, info.Tree, info.MaxRank, info.IsDefaultUnlocked)
            : TalentDisplayInfo.Fallback(rowName);

    partial void OnNameChanged(string value) => _onDirtyChanged();

    partial void OnIsMaleChanged(bool value) => _onDirtyChanged();

    partial void OnXpTextChanged(string value)
    {
        OnPropertyChanged(nameof(XpDisplay));
        _onDirtyChanged();
    }

    /// <summary>Writes the edited fields into the raw node. A non-numeric XP box keeps the original value rather than corrupting the save — the UI validates, this is the backstop.</summary>
    public void ApplyToNode()
    {
        _node["CharacterName"] = Name;
        if (long.TryParse(XpText, out var xp) && xp >= 0)
        {
            _node["XP"] = xp;
        }

        // UnlockedFlags: keep the save's own ordering for flags that stay set, append newly-set
        // ones — minimal diff against what the game itself wrote.
        var originallyUnlocked = ReadIntArray(_node["UnlockedFlags"]);
        var nowUnlocked = Flags.Where(f => f.IsUnlocked).Select(f => f.Id).ToHashSet();
        var flagArray = new JsonArray();
        foreach (var id in originallyUnlocked.Where(nowUnlocked.Contains))
        {
            flagArray.Add(id);
        }

        foreach (var id in nowUnlocked.Except(originallyUnlocked).OrderBy(i => i))
        {
            flagArray.Add(id);
        }

        _node["UnlockedFlags"] = flagArray;

        // Talents: same minimal-diff shape — existing entries keep their position (rank updated in
        // place), rank-0 entries drop out, newly-learned append. Rank 0 IS "doesn't have it" in
        // the format, so it's never written.
        var byRow = Talents.ToDictionary(t => t.RowName, t => t.Rank, StringComparer.OrdinalIgnoreCase);
        var talentArrayNew = new JsonArray();
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_node["Talents"] is JsonArray existing)
        {
            foreach (var entry in existing.OfType<JsonObject>())
            {
                if (entry["RowName"]?.GetValue<string>() is { } rowName && byRow.GetValueOrDefault(rowName) > 0)
                {
                    talentArrayNew.Add(new JsonObject { ["RowName"] = rowName, ["Rank"] = byRow[rowName] });
                    written.Add(rowName);
                }
            }
        }

        foreach (var talent in Talents)
        {
            if (talent.Rank > 0 && !written.Contains(talent.RowName))
            {
                talentArrayNew.Add(new JsonObject { ["RowName"] = talent.RowName, ["Rank"] = talent.Rank });
            }
        }

        _node["Talents"] = talentArrayNew;

        // Cosmetics: values updated in place on the existing sub-object — every field this class
        // doesn't know about (there are none here, since Cosmetics enumerates the whole object on
        // load) stays exactly as-is, and IsMale is written alongside the raw hashes since it lives
        // in the same sub-object.
        if (_node["Cosmetic"] is JsonObject cosmetic)
        {
            foreach (var field in Cosmetics)
            {
                if (long.TryParse(field.ValueText, out var hash))
                {
                    cosmetic[field.Key] = hash;
                }
            }

            cosmetic["IsMale"] = IsMale;
        }
    }

    public void MarkClean()
    {
        _originalName = Name;
        if (long.TryParse(XpText, out var xp))
        {
            _originalXp = xp;
        }

        foreach (var flag in Flags)
        {
            flag.MarkClean();
        }

        foreach (var talent in Talents)
        {
            talent.MarkClean();
        }

        foreach (var cosmetic in Cosmetics)
        {
            cosmetic.MarkClean();
        }

        _originalIsMale = IsMale;
    }

    private static List<int> ReadIntArray(JsonNode? node) =>
        node is JsonArray array ? [.. array.Select(n => n?.GetValue<int>() ?? -1).Where(i => i >= 0)] : [];
}
