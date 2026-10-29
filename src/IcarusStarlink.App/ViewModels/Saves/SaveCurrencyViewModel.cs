using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One profile currency (a MetaResources entry) — same deferred-apply pattern as SaveCharacterViewModel: the box edits a copy, ApplyToNode() writes it into the raw save node only at save time.</summary>
public sealed partial class SaveCurrencyViewModel : DirtyRow
{
    private readonly JsonObject _node;
    private long _originalCount;

    public string Label { get; }

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

    partial void OnCountTextChanged(string value) => Changed();

    public void ApplyToNode()
    {
        // int, not long (an audit finding): the game stores Count as a 32-bit int.
        if (int.TryParse(CountText, out var count) && count >= 0)
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
        if (int.TryParse(CountText, out var count) && count >= 0)
        {
            _originalCount = count;
        }
    }
}
