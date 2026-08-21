using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// One editable field row in the EXMOD editor's right-hand pane. Writes straight back into the
/// owning item's own Fields dictionary on every successfully-parsed edit — the in-memory
/// ExmodPackage the editor holds is always live/current, so ExmodEditorViewModel.Save doesn't
/// need a separate "commit the currently-displayed pane" step when the user switches items
/// without saving first.
/// </summary>
public sealed partial class EditorFieldViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions DisplayOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Dictionary<string, JsonNode?> _fields;
    private readonly JsonNode? _baseValue;
    private readonly JsonNode? _originalValue;

    public string FieldName { get; }
    public string BaseValueText { get; }

    /// <summary>What this field held when the editor was opened (or last saved) — "was: X", shown only when it differs from the live value. A separate reference point from base/vanilla-game.</summary>
    public string OriginalValueText { get; }

    [ObservableProperty]
    private string _valueText;

    [ObservableProperty]
    private bool _isChanged;

    /// <summary>True when the live value differs from OriginalValueText — drives whether the "was: X" line shows at all.</summary>
    [ObservableProperty]
    private bool _isChangedFromOriginal;

    [ObservableProperty]
    private string? _parseError;

    public EditorFieldViewModel(string fieldName, Dictionary<string, JsonNode?> fields, JsonNode? baseValue, JsonNode? originalValue)
    {
        FieldName = fieldName;
        _fields = fields;
        _baseValue = baseValue;
        _originalValue = originalValue;
        BaseValueText = FormatForDisplay(baseValue);
        OriginalValueText = FormatForDisplay(originalValue);
        _valueText = FormatForDisplay(fields.TryGetValue(fieldName, out var current) ? current : null);
        RecomputeChanged();
    }

    partial void OnValueTextChanged(string value)
    {
        try
        {
            var parsed = string.IsNullOrWhiteSpace(value) ? null : JsonNode.Parse(value);
            _fields[FieldName] = parsed;
            ParseError = null;
        }
        catch (Exception ex)
        {
            // Deliberately doesn't write back — _fields keeps whatever the last successfully-
            // parsed value was until this becomes valid JSON again, so a mid-typing invalid state
            // never corrupts the in-memory package.
            ParseError = ex.Message;
        }

        RecomputeChanged();
    }

    private void RecomputeChanged()
    {
        try
        {
            var parsed = string.IsNullOrWhiteSpace(ValueText) ? null : JsonNode.Parse(ValueText);
            IsChanged = !JsonNode.DeepEquals(parsed, _baseValue);
            IsChangedFromOriginal = !JsonNode.DeepEquals(parsed, _originalValue);
        }
        catch
        {
            IsChanged = true;
            IsChangedFromOriginal = true;
        }
    }

    private static string FormatForDisplay(JsonNode? value) => value?.ToJsonString(DisplayOptions) ?? "null";
}
