using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One profile currency (a MetaResources entry) — same deferred-apply pattern as SaveCharacterViewModel: the box edits a copy, ApplyToNode() writes it into the raw save node only at save time.</summary>
public sealed partial class SaveCurrencyViewModel : DirtyRow
{
    private readonly JsonObject _node;
    private long _originalCount;

    public string Label { get; }

    /// <summary>A currency the game has (D_MetaCurrency). False for a leftover like "Exotic_stabilized": the game ignores it, and it can be taken out.</summary>
    public bool IsGameCurrency { get; init; } = true;

    /// <summary>The extracted D_MetaCurrency was read, so <see cref="IsGameCurrency"/> comes from the game's own table rather than the built-in list.</summary>
    public bool GameTableRead { get; init; }

    /// <summary>
    /// Not a game currency, and no mod you have adds it: it can be taken out (a mod's own currency
    /// stays). Not yet placed (Source null) counts only when the game's table wasn't read: then the
    /// built-in list is all there is to go on. With the table read, an unplaced one may be a mod's
    /// still being read, so it stays until it's placed.
    /// </summary>
    public bool IsUnknown => !IsGameCurrency && (Source is { IsRemovable: true } || (Source is null && !GameTableRead));

    /// <summary>Its MetaRow ("Exotic_stabilized").</summary>
    public string MetaRow => SaveJson.String(_node["MetaRow"]) ?? "";

    /// <summary>Where a currency the game doesn't have comes from (SaveModSources); null for the game's own, until the installed mods are read, or when nothing can be said.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnknown))]
    private Services.SaveRowSource? _source;

    /// <summary>Its MetaResources entry, for taking it out of the save.</summary>
    internal JsonObject Node => _node;

    /// <summary>What a screen reader says for the row (the class name otherwise).</summary>
    public override string ToString() => Label;

    [ObservableProperty]
    private string _countText;

    public override bool IsDirty => long.TryParse(CountText, out var count) ? count != _originalCount : CountText != _originalCount.ToString();

    public SaveCurrencyViewModel(JsonObject node, string label, Action onDirtyChanged)
        : base(onDirtyChanged)
    {
        _node = node;
        Label = label;
        _originalCount = SaveJson.Long(node["Count"]) ?? 0;
        _countText = _originalCount.ToString();
    }

    protected override string? NumberProblem => SaveNumberRules.CountProblem(CountText);

    partial void OnCountTextChanged(string value) => Changed();

    public void ApplyToNode()
    {
        // int, not long: the game stores Count as a 32-bit int.
        if (SaveNumberRules.TryParseCount(CountText, out var count))
        {
            _node["Count"] = (long)count;
        }
    }

    /// <summary>
    /// Must reject exactly what ApplyToNode() rejects — a negative CountText that Save silently
    /// declined to write must not be adopted as the new "clean" baseline either, or IsDirty would
    /// compare CountText against itself and read false, showing "Saved" with the Save button
    /// disabled while the on-disk value is actually still whatever it was before the edit.
    /// </summary>
    protected override void Snapshot()
    {
        if (SaveNumberRules.TryParseCount(CountText, out var count))
        {
            _originalCount = count;
        }
    }
}
