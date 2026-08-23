using CommunityToolkit.Mvvm.ComponentModel;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// One raw Cosmetic field (e.g. Customization_Head) — a signed int32 hash with no known way to map
/// it to a real option name. Confirmed by direct testing: every standard string-hash variant
/// (CRC32 over the row name and over the row's own linked asset RowName, case-sensitive and
/// case-insensitive, narrow and wide encodings) was checked against every real
/// D_CharacterCreationData/D_CharacterVoices row and none matched any of a real character's own 12
/// Customization_* values. Editing the raw number is still genuinely useful — e.g. copying an exact
/// cosmetic from one of your own characters to another — just without a friendly picker.
/// </summary>
public sealed partial class SaveCosmeticFieldViewModel : ObservableObject
{
    private readonly Action _onDirtyChanged;
    private string _originalValueText;

    public string Key { get; }

    [ObservableProperty]
    private string _valueText;

    public bool IsDirty => ValueText != _originalValueText;

    public SaveCosmeticFieldViewModel(string key, string valueText, Action onDirtyChanged)
    {
        Key = key;
        _valueText = valueText;
        _originalValueText = valueText;
        _onDirtyChanged = onDirtyChanged;
    }

    partial void OnValueTextChanged(string value) => _onDirtyChanged();

    public void MarkClean() => _originalValueText = ValueText;
}
