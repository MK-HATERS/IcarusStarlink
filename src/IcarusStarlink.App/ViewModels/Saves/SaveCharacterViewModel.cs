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

    /// <summary>What a screen reader says for the character's card (the class name otherwise).</summary>
    public override string ToString() => Name;

    [ObservableProperty]
    private string _xpText;

    public int ChrSlot { get; }

    public string Location { get; }

    public bool IsDead { get; }

    public ObservableCollection<SaveFlagViewModel> Flags { get; } = [];

    public ObservableCollection<SaveTalentViewModel> Talents { get; } = [];

    /// <summary>"Talents by 477, Solo by 15": trees with more ranks than the installed budget (set by SavesViewModel); null when none.</summary>
    [ObservableProperty]
    private string? _overBudget;

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

    /// <summary>The line under the XP box while typing: set exactly when Save would refuse it (see SavesViewModel.BadNumberBoxes), else null.</summary>
    public string? XpError => IsDirty ? SaveNumberRules.CountProblem(XpText) : null;

    /// <summary>The red border on the XP box.</summary>
    public bool HasXpError => XpError is not null;

    private void NotifyDirtyState()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(XpError));
        OnPropertyChanged(nameof(HasXpError));
    }

    public SaveCharacterViewModel(JsonObject node, SaveGameNames names, Action onDirtyChanged)
    {
        _node = node;
        // Every edit (this character's own fields and its flags/talents/cosmetics) also tells the
        // Characters list its " *" unsaved marker may have changed; nothing raised IsDirty before.
        _onDirtyChanged = () =>
        {
            NotifyDirtyState();
            onDirtyChanged();
        };
        _originalName = SaveJson.String(node["CharacterName"]) ?? "";
        _originalXp = SaveJson.Long(node["XP"]) ?? 0;
        _name = _originalName;
        _xpText = _originalXp.ToString();
        ChrSlot = SaveJson.Int(node["ChrSlot"]) ?? 0;
        Location = SaveJson.String(node["Location"]) ?? "";
        IsDead = SaveJson.Bool(node["IsDead"]) ?? false;
        _originalIsMale = SaveJson.Bool((node["Cosmetic"] as JsonObject)?["IsMale"]) ?? true;
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
            // Only the number fields (the Customization_* hashes). Anything else, like the on/off
            // IsReworkedCharacter a game update added, is kept exactly as it is when saving.
            if (key == "IsMale" || value is not JsonValue number || number.GetValueKind() != System.Text.Json.JsonValueKind.Number
                || !long.TryParse(number.ToJsonString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var hash))
            {
                continue;
            }

            Cosmetics.Add(new SaveCosmeticFieldViewModel(key, hash.ToString(), _onDirtyChanged));
        }
    }

    private void BuildFlags(SaveGameNames names)
    {
        var unlocked = SaveJson.IdArray(_node["UnlockedFlags"]);

        // Every flag the game's own table defines, plus any ID the save carries that the table
        // doesn't know (an older/newer game version) — nothing in the save is ever hidden.
        // Every known ID, then each UNKNOWN set ID individually — never a dense 0..max range (an
        // audit finding: one corrupt/huge ID in a save meant allocating that many rows and dying).
        foreach (var id in SaveFlagIds.KnownThenExtra(names.CharacterFlagNames.Count, unlocked))
        {
            Flags.Add(new SaveFlagViewModel(id, names.CharacterFlagInfo(id), unlocked.Contains(id), _onDirtyChanged));
        }
    }

    private void BuildTalents(SaveGameNames names)
    {
        // Learned first (the save's own order), then everything else a CHARACTER can learn at rank
        // 0 — only the character trees (SaveGameNames.CharacterTalentTrees), never workshop,
        // prospect or mount talents, which live elsewhere and do nothing on a character.
        var characterTrees = names.CharacterTalentTrees;
        var learnable = names.Talents
            .Where(kv => characterTrees.Count == 0 || characterTrees.Contains(kv.Value.Tree))
            .OrderBy(kv => kv.Value.Tree, StringComparer.OrdinalIgnoreCase).ThenBy(kv => kv.Value.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(kv => kv.Key);

        // Same display info for a learned and an unlearned talent (tree group, Player/Blueprint/Solo):
        // without it, ranks given here counted towards no budget and hid under Show (review, 2026-10-23).
        foreach (var (rowName, rank) in TalentArray.LearnedThenOthers(TalentArray.Read(_node["Talents"]), learnable))
        {
            Talents.Add(new SaveTalentViewModel(rowName, DisplayInfoFor(names, rowName), rank, _onDirtyChanged));
        }
    }

    internal static TalentDisplayInfo DisplayInfoFor(SaveGameNames names, string rowName) =>
        names.Talents.TryGetValue(rowName, out var info)
            ? DisplayInfoFor(names, info)
            : TalentDisplayInfo.Fallback(rowName);

    internal static TalentDisplayInfo DisplayInfoFor(SaveGameNames names, TalentInfo info)
    {
        var tree = names.TalentTrees.GetValueOrDefault(info.Tree);
        return new TalentDisplayInfo(info.DisplayName, info.Description, info.Tree, info.MaxRank, info.IsDefaultUnlocked,
            tree?.Group ?? info.Tree, tree?.Model ?? "", info.Cost, tree?.Order ?? int.MaxValue, info.Order);
    }

    partial void OnNameChanged(string value) => _onDirtyChanged();

    partial void OnIsMaleChanged(bool value) => _onDirtyChanged();

    partial void OnXpTextChanged(string value)
    {
        OnPropertyChanged(nameof(XpDisplay));
        _onDirtyChanged();
    }

    /// <summary>Writes the edited fields into the raw node. A non-numeric XP box keeps the original value rather than corrupting the save — the UI validates, this is the backstop.</summary>
    public void ApplyToNode() => ApplyTo(_node);

    /// <summary>Writes the edits into <paramref name="target"/>: the character's own node, or a copy of it (Duplicate copies the character as edited, not as last saved).</summary>
    public void ApplyTo(JsonObject target)
    {
        target["CharacterName"] = Name;
        // int, not long (an audit finding): the game stores XP as a 32-bit int, so a value past
        // 2,147,483,647 "saved" fine here and then wrapped or was discarded in-game.
        if (SaveNumberRules.TryParseCount(XpText, out var xp))
        {
            target["XP"] = (long)xp;
        }

        // UnlockedFlags: keep the save's own ordering for flags that stay set, append newly-set
        // ones — minimal diff against what the game itself wrote.
        target["UnlockedFlags"] = SaveJson.MergeIds(target["UnlockedFlags"], Flags.Where(f => f.IsUnlocked).Select(f => f.Id));

        // Talents: same minimal-diff shape (TalentArray.Write).
        target["Talents"] = TalentArray.Write(target["Talents"], [.. Talents.Select(t => (t.RowName, t.Rank))]);

        // Cosmetics: values updated in place on the existing sub-object — every field this class
        // doesn't know about (non-number fields like IsReworkedCharacter, which BuildCosmetics
        // skips) stays exactly as-is, and IsMale is written alongside the raw hashes since it lives
        // in the same sub-object.
        if (target["Cosmetic"] is JsonObject cosmetic)
        {
            foreach (var field in Cosmetics)
            {
                if (SaveNumberRules.TryParseWhole(field.ValueText, out var hash))
                {
                    cosmetic[field.Key] = (long)hash;
                }
            }

            cosmetic["IsMale"] = IsMale;
        }
    }

    /// <summary>
    /// The XP branch must reject exactly what ApplyToNode() rejects — a negative XpText that Save
    /// silently declined to write must not be adopted as the new "clean" baseline either, or
    /// IsDirty would compare XpText against itself and read false, showing "Saved" with the Save
    /// button disabled while the on-disk XP is actually still whatever it was before the edit.
    /// </summary>
    public void MarkClean()
    {
        _originalName = Name;
        if (SaveNumberRules.TryParseCount(XpText, out var xp))
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
        NotifyDirtyState();
    }
}
