using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// The editor's "Scale rules" view: change one field across a whole game table by a factor (or
/// set it), the way real quality-of-life mods do — "Weight ×0.5 on every item". The rules live
/// in the mod (ExmodPackage.ScaleRules) and Apply rebuilds their rows from CURRENT game data, so
/// after a game update one click brings the mod up to date instead of it going stale.
/// </summary>
public sealed partial class ExmodEditorViewModel
{
    public ObservableCollection<ScaleRuleViewModel> ScaleRuleRows { get; } = [];

    private IReadOnlyList<string>? _scaleTables;

    /// <summary>Every game table, for each rule's table box.</summary>
    public IReadOnlyList<string> ScaleTables => _scaleTables ??= ScaleRules.ListTables(_dataFolder);

    [ObservableProperty]
    private string? _scaleStatusMessage;

    /// <summary>A rule was edited since the last Apply — Save applies first so the file never holds rules that disagree with its rows.</summary>
    private bool _scaleRulesDirty;

    private readonly Dictionary<string, IReadOnlyList<string>> _fieldOptionsCache = new(StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyList<string> FieldOptionsFor(string currentFile)
    {
        if (string.IsNullOrWhiteSpace(currentFile))
        {
            return [];
        }

        if (!_fieldOptionsCache.TryGetValue(currentFile, out var options))
        {
            options = ScaleRules.ListNumericFields(_dataFolder, currentFile.Trim());
            _fieldOptionsCache[currentFile] = options;
        }

        return options;
    }

    private void LoadScaleRules()
    {
        ScaleRuleRows.Clear();
        foreach (var rule in _package.ScaleRules)
        {
            ScaleRuleRows.Add(new ScaleRuleViewModel(this, rule));
        }

        _scaleRulesDirty = false;
        OnPropertyChanged(nameof(HasScaleRules));
        OnPropertyChanged(nameof(HasNoScaleRules));
    }

    public bool HasScaleRules => ScaleRuleRows.Count > 0;

    /// <summary>Computed inverse (this project binds inverses as properties, not converter parameters).</summary>
    public bool HasNoScaleRules => ScaleRuleRows.Count == 0;

    internal void MarkScaleRulesDirty() => _scaleRulesDirty = true;

    [RelayCommand]
    private void AddScaleRule()
    {
        var row = new ScaleRuleViewModel(this, new ScaleRule { CurrentFile = "Traits-D_Itemable.json", Field = "Weight", Value = 0.5, Label = "New rule" });
        ScaleRuleRows.Add(row);
        _scaleRulesDirty = true;
        OnPropertyChanged(nameof(HasScaleRules));
        OnPropertyChanged(nameof(HasNoScaleRules));
        row.RefreshPreview();
    }

    [RelayCommand]
    private void RemoveScaleRule(ScaleRuleViewModel? row)
    {
        if (row is not null && ScaleRuleRows.Remove(row))
        {
            _scaleRulesDirty = true;
            OnPropertyChanged(nameof(HasScaleRules));
            OnPropertyChanged(nameof(HasNoScaleRules));
        }
    }

    /// <summary>Builds the rules from the rows; null (with a message) when one doesn't make sense yet.</summary>
    private List<ScaleRule>? BuildScaleRules()
    {
        var rules = new List<ScaleRule>();
        foreach (var row in ScaleRuleRows)
        {
            if (row.ToRule() is not { } rule)
            {
                ScaleStatusMessage = $"'{row.Label}': {row.Problem}";
                return null;
            }

            rules.Add(rule);
        }

        return rules;
    }

    /// <summary>Writes every rule's rows from current game data (also how a game update is caught up with). Undoable.</summary>
    [RelayCommand]
    private void ApplyScaleRules() => ApplyScaleRulesCore();

    private bool ApplyScaleRulesCore()
    {
        if (BuildScaleRules() is not { } rules)
        {
            return false;
        }

        if (rules.Count == 0 && _package.ScaleRules.Count == 0)
        {
            ScaleStatusMessage = "No rules yet — Add rule, pick a table and field, then Apply.";
            return true;
        }

        PushUndoSnapshot();
        var report = new MergeReport();
        var result = ScaleRules.ApplyToPackage(_package, rules, _dataFolder, report, GameTables);
        for (var i = 0; i < ScaleRuleRows.Count && i < result.PerRule.Count; i++)
        {
            ScaleRuleRows[i].ShowPreview(result.PerRule[i]);
        }

        _scaleRulesDirty = false;
        RefreshBaseDiff();
        ReloadItems();
        SelectedItem ??= Items.FirstOrDefault();
        var enabled = rules.Count(r => r.Enabled);
        ScaleStatusMessage = $"Applied {enabled} rule(s) from current game data: {result.RowsWritten} item change(s) written. Save to keep them."
            + (report.Warnings.Count > 0 ? $" {report.Warnings[0]}" : "");
        return true;
    }

    /// <summary>Refreshes every rule's "would change N rows" line without touching the mod.</summary>
    [RelayCommand]
    private void PreviewScaleRules()
    {
        foreach (var row in ScaleRuleRows)
        {
            row.RefreshPreview();
        }

        ScaleStatusMessage = "Preview only — nothing changed yet. Apply writes these into the mod.";
    }

    /// <summary>Called by Save: unapplied rule edits are applied first. False when a rule is invalid (Save stops and says why).</summary>
    private bool ApplyPendingScaleRules() => !_scaleRulesDirty || ApplyScaleRulesCore();
}

/// <summary>One rule in the Scale rules view.</summary>
public sealed partial class ScaleRuleViewModel : ObservableObject
{
    private readonly ExmodEditorViewModel _owner;

    public ScaleRuleViewModel(ExmodEditorViewModel owner, ScaleRule rule)
    {
        _owner = owner;
        _enabled = rule.Enabled;
        _label = rule.Label;
        _currentFile = rule.CurrentFile;
        _field = rule.Field;
        _operationIndex = rule.Operation == ScaleOperation.Set ? 1 : 0;
        _valueText = rule.Value.ToString("0.######", CultureInfo.InvariantCulture);
        _rowFilter = rule.RowFilter ?? "";
        _includeInherited = rule.IncludeInherited;
    }

    public IReadOnlyList<string> Tables => _owner.ScaleTables;

    public IReadOnlyList<string> FieldOptions => _owner.FieldOptionsFor(CurrentFile);

    public IReadOnlyList<string> OperationOptions { get; } = ["Multiply by", "Set to"];

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _label;
    [ObservableProperty] private string _currentFile;
    [ObservableProperty] private string _field;
    [ObservableProperty] private int _operationIndex;
    [ObservableProperty] private string _valueText;
    [ObservableProperty] private string _rowFilter;
    [ObservableProperty] private bool _includeInherited;
    [ObservableProperty] private string? _previewText;

    public string? Problem { get; private set; }

    partial void OnEnabledChanged(bool value) => _owner.MarkScaleRulesDirty();
    partial void OnLabelChanged(string value) => _owner.MarkScaleRulesDirty();
    partial void OnFieldChanged(string value) => _owner.MarkScaleRulesDirty();
    partial void OnOperationIndexChanged(int value) => _owner.MarkScaleRulesDirty();
    partial void OnValueTextChanged(string value) => _owner.MarkScaleRulesDirty();
    partial void OnRowFilterChanged(string value) => _owner.MarkScaleRulesDirty();
    partial void OnIncludeInheritedChanged(bool value) => _owner.MarkScaleRulesDirty();

    partial void OnCurrentFileChanged(string value)
    {
        _owner.MarkScaleRulesDirty();
        OnPropertyChanged(nameof(FieldOptions));
    }

    public ScaleRule? ToRule()
    {
        Problem = null;
        if (string.IsNullOrWhiteSpace(CurrentFile) || !CurrentFile.Trim().EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            Problem = "pick a game table, like Traits-D_Itemable.json.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(Field))
        {
            Problem = "pick a field, like Weight.";
            return null;
        }

        if (!double.TryParse(ValueText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || double.IsNaN(value) || double.IsInfinity(value))
        {
            Problem = "the value must be a number, like 0.5 (use a dot for decimals).";
            return null;
        }

        if (Field.Trim().Equals("Name", StringComparison.Ordinal))
        {
            Problem = "Name is a row's identity, not a value.";
            return null;
        }

        return new ScaleRule
        {
            Enabled = Enabled,
            Label = Label.Trim(),
            CurrentFile = CurrentFile.Trim(),
            Field = Field.Trim(),
            Operation = OperationIndex == 1 ? ScaleOperation.Set : ScaleOperation.Multiply,
            Value = value,
            RowFilter = string.IsNullOrWhiteSpace(RowFilter) ? null : RowFilter.Trim(),
            IncludeInherited = IncludeInherited,
        };
    }

    [RelayCommand]
    public void RefreshPreview()
    {
        if (ToRule() is not { } rule)
        {
            PreviewText = $"Can't preview: {Problem}";
            return;
        }

        ShowPreview(ScaleRules.Preview(rule, _owner.DataFolderForScale, _owner.GameTablesForScale));
    }

    public void ShowPreview(ScalePreview preview) =>
        PreviewText = !Enabled ? "Off — changes nothing."
            : preview.RowCount == 0 ? "Changes nothing: no row in this table has a number at that field (check the table and field)."
            : $"Changes {preview.RowCount} row(s), {preview.ValueCount} value(s) — e.g. {preview.Example}";
}
