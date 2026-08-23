using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// One character, editing a small known set of fields over its raw save JsonObject — the node
/// itself is what gets written back, so every field this class doesn't surface (cosmetic hashes,
/// talents, flags, timestamps) survives untouched. Edits stay in the ViewModel until
/// ApplyToNode() at save time, so closing without saving really changes nothing.
/// </summary>
public sealed partial class SaveCharacterViewModel : ObservableObject
{
    private readonly JsonObject _node;
    private readonly Action _onDirtyChanged;
    private string _originalName;
    private long _originalXp;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _xpText;

    public int ChrSlot { get; }

    public string Location { get; }

    public bool IsDead { get; }

    /// <summary>The prospect/location line the Overview card shows, e.g. "Prospect_Grasslands" → "Grasslands".</summary>
    public string LocationDisplay => Location.StartsWith("Prospect_", StringComparison.OrdinalIgnoreCase) ? Location["Prospect_".Length..] : Location;

    public string XpDisplay => long.TryParse(XpText, out var xp) ? xp.ToString("N0") : XpText;

    public bool IsDirty =>
        Name != _originalName
        || (long.TryParse(XpText, out var xp) ? xp != _originalXp : XpText != _originalXp.ToString());

    public SaveCharacterViewModel(JsonObject node, Action onDirtyChanged)
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
    }

    partial void OnNameChanged(string value) => _onDirtyChanged();

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
    }

    public void MarkClean()
    {
        _originalName = Name;
        if (long.TryParse(XpText, out var xp))
        {
            _originalXp = xp;
        }
    }
}
