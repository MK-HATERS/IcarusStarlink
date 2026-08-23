using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One profile currency (a MetaResources entry) — same deferred-apply pattern as SaveCharacterViewModel: the box edits a copy, ApplyToNode() writes it into the raw save node only at save time.</summary>
public sealed partial class SaveCurrencyViewModel : ObservableObject
{
    private readonly JsonObject _node;
    private readonly Action _onDirtyChanged;
    private long _originalCount;

    public string Label { get; }

    [ObservableProperty]
    private string _countText;

    public bool IsDirty => long.TryParse(CountText, out var count) ? count != _originalCount : CountText != _originalCount.ToString();

    public SaveCurrencyViewModel(JsonObject node, string label, Action onDirtyChanged)
    {
        _node = node;
        _onDirtyChanged = onDirtyChanged;
        Label = label;
        _originalCount = node["Count"]?.GetValue<long>() ?? 0;
        _countText = _originalCount.ToString();
    }

    partial void OnCountTextChanged(string value) => _onDirtyChanged();

    public void ApplyToNode()
    {
        if (long.TryParse(CountText, out var count) && count >= 0)
        {
            _node["Count"] = count;
        }
    }

    public void MarkClean()
    {
        if (long.TryParse(CountText, out var count))
        {
            _originalCount = count;
        }
    }
}
