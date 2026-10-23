using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Views;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class SavesViewModel
{
    /// <summary>Real MetaRow keys → the names the game's own UI uses (per the spec's own list: "Ren, Exotics, Red, Biomass, Uranium, Licence, Respec"). An unrecognized key still shows, under its raw name — same preserve-everything philosophy as the repository.</summary>
    private static readonly Dictionary<string, string> CurrencyLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Credits"] = "Ren",
        ["Exotic1"] = "Exotics",
        ["Exotic_Red"] = "Red Exotics",
        ["Biomass"] = "Biomass",
        ["Biomass_Converter"] = "Flux",
        ["Exotic_Uranium"] = "Uranium",
        ["Licence"] = "Licences",
        ["Refund"] = "Respec Points",
    };

    partial void OnSelectedCharacterChanged(SaveCharacterViewModel? value)
    {
        RefreshFlagFilter();
        RefreshTalentFilter();
    }

    /// <summary>The spec's "Click a card to edit that character" — Overview card → Characters tab with that character selected.</summary>
    [RelayCommand]
    private void EditCharacter(SaveCharacterViewModel? character)
    {
        if (character is null)
        {
            return;
        }

        SelectedCharacter = character;
        SelectedTabIndex = 1;
    }

    /// <summary>
    /// Clones the selected character's own JsonObject wholesale (deep clone — a JsonNode can only
    /// ever belong to one parent, so the clone must be fully independent before it's added
    /// anywhere, same reasoning every other DeepClone call in this class already relies on). ChrSlot
    /// is the real identifier the game itself uses to keep characters distinct in Characters.json
    /// (confirmed against a real save: every character carries one, and the profile's own
    /// NextChrSlot is exactly the counter the game increments each time it hands out a new one) —
    /// two characters sharing a ChrSlot would be a genuine collision, not just a cosmetic one, so
    /// the duplicate gets a freshly allocated slot via AllocateNextChrSlot() rather than a copy of
    /// the source's. CharacterName carries no such uniqueness requirement in the format, but
    /// "(Copy)" is still appended so the new entry doesn't look identical to its source in every
    /// list this page shows it in (Overview cards, the Characters list).
    /// </summary>
    [RelayCommand]
    private void DuplicateCharacter(SaveCharacterViewModel? character)
    {
        if (character is null)
        {
            return;
        }

        // The character as edited, not as last saved (the "(Copy)" name already followed the edit).
        var clone = character.Node.DeepClone().AsObject();
        character.ApplyTo(clone);
        clone["ChrSlot"] = AllocateNextChrSlot();
        var newName = $"{character.Name} (Copy)";
        clone["CharacterName"] = newName;

        var newCharacter = new SaveCharacterViewModel(clone, _gameNames, NotifyDirtyChanged);

        // Kept adjacent to the source in both lists — purely cosmetic (order has no meaning to the
        // game; SaveCharacters below writes the whole array regardless of order), but it keeps the
        // duplicate easy to find right next to what it was duplicated from.
        var nodeIndex = _characterNodes.IndexOf(character.Node);
        _characterNodes.Insert(nodeIndex >= 0 ? nodeIndex + 1 : _characterNodes.Count, clone);
        var vmIndex = Characters.IndexOf(character);
        Characters.Insert(vmIndex >= 0 ? vmIndex + 1 : Characters.Count, newCharacter);

        _charactersListDirty = true;
        SelectedCharacter = newCharacter;
        NotifyDirtyChanged();
        StatusMessage = $"Duplicated '{character.Name}' as '{newName}' (slot {SaveJson.Int(clone["ChrSlot"])}) — click Save player data to make it permanent.";
    }

    /// <summary>
    /// The next ChrSlot value nothing currently on this profile is using. Starts from the
    /// profile's own NextChrSlot (exactly what the game itself would hand out next) but never
    /// trusts it blindly — same defensive posture as BuildAccountFlags/BuildBinaryFlags' own
    /// Math.Max fallbacks elsewhere in this class — in case a save's NextChrSlot is stale, missing,
    /// or a prior duplicate already consumed it without the profile having been reloaded. Also
    /// advances NextChrSlot past the value just handed out, so the game itself (or a second
    /// Duplicate right after this one) can't be handed the same slot next.
    /// </summary>
    private int AllocateNextChrSlot()
    {
        var used = _characterNodes.Select(n => SaveJson.Int(n["ChrSlot"]) ?? -1).Where(id => id >= 0).ToHashSet();
        var candidate = SaveJson.Int(_profile?["NextChrSlot"]) ?? (used.Count == 0 ? 0 : used.Max() + 1);
        while (used.Contains(candidate))
        {
            candidate++;
        }

        if (_profile is not null)
        {
            _profile["NextChrSlot"] = candidate + 1;
            _profileFieldsDirty = true;
        }

        return candidate;
    }

    /// <summary>
    /// Deleting a whole character is far more consequential than any other destructive list-edit on
    /// this page (talents, flags, cosmetics, XP — the character's entire progress, gone), which is
    /// why this one confirms first via IDialogService — unlike DeleteMetaInventoryItem/DeleteMount,
    /// which mirror this page's usual "staged edit, Save's own confirm covers it" pattern, since
    /// losing one item or one mount is a much smaller loss to walk back from.
    /// </summary>
    [RelayCommand]
    private void DeleteCharacter(SaveCharacterViewModel? character)
    {
        if (character is null)
        {
            return;
        }

        var confirm = _dialogService.Confirm(
            $"Permanently remove '{character.Name}' from this save slot?\n\n"
            + "This deletes the character's entire progress — talents, flags, cosmetics, XP, everything — the next time you save.",
            "Delete character", ThemedConfirmSeverity.Warning);
        if (!confirm)
        {
            return;
        }

        _characterNodes.Remove(character.Node);
        Characters.Remove(character);
        if (ReferenceEquals(SelectedCharacter, character))
        {
            SelectedCharacter = Characters.FirstOrDefault();
        }

        _charactersListDirty = true;
        NotifyDirtyChanged();
        StatusMessage = $"Removed '{character.Name}' — click Save player data to make it permanent.";
    }
}
