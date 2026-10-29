using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One game row the mod doesn't change yet, in the "Not in this mod yet" view.</summary>
public sealed partial class NotInModRowViewModel(string rowName, string title, string gameValueText) : ObservableObject
{
    public string RowName { get; } = rowName;

    /// <summary>The in-game name when the row belongs to one item, else the row name.</summary>
    public string Title { get; } = title;

    /// <summary>"game: 24" for the field the view filters on; empty without one.</summary>
    public string GameValueText { get; } = gameValueText;

    public bool ShowRowName => !string.Equals(Title, RowName, StringComparison.Ordinal);

    [ObservableProperty]
    private bool _isPicked;
}

/// <summary>
/// "Not in this mod yet" (classic IMM 2.4.2's most used editor tool): the game rows of a table this
/// mod changes that it doesn't have yet, optionally only those that set one field (a stack mod: rows
/// with MaxStack). Pick some and add them in one go, with the same change the mod makes to its other
/// rows when it makes one (the Scale rule suggester proves it) — how a modder catches the items a
/// game update added.
/// </summary>
public sealed partial class ExmodEditorViewModel
{
    /// <summary>The field box's "no field" choice: every row the mod doesn't have.</summary>
    public const string AnyRowOption = "(any row)";

    public ObservableCollection<NotInModRowViewModel> NotInModRows { get; } = [];

    /// <summary>The game tables this mod changes, for the table box.</summary>
    public ObservableCollection<string> NotInModTables { get; } = [];

    /// <summary>The fields this mod sets in the picked table (most used first), plus AnyRowOption.</summary>
    public ObservableCollection<string> NotInModFieldOptions { get; } = [];

    [ObservableProperty]
    private string? _notInModTable;

    /// <summary>The field rows must set, or AnyRowOption. Null (a ComboBox clears it while its list is refilled) counts as any row.</summary>
    [ObservableProperty]
    private string? _notInModField = AnyRowOption;

    /// <summary>NotInModField as a real field name; null for "any row".</summary>
    private string? NotInModFieldName => NotInModField is null or AnyRowOption ? null : NotInModField;

    [ObservableProperty]
    private string _notInModSearch = "";

    /// <summary>Add with the mod's own change (×2) instead of the game's value.</summary>
    [ObservableProperty]
    private bool _notInModUseModChange = true;

    [ObservableProperty]
    private string? _notInModSummary;

    [ObservableProperty]
    private string? _notInModStatus;

    /// <summary>The change this mod makes to the field on its other rows ("Slots ×2"), proven by the suggester; null when it makes none consistently.</summary>
    private ScaleRuleSuggestion? _notInModChange;

    public bool HasNotInModChange => _notInModChange is not null;

    public string? NotInModChangeText => _notInModChange is { } change
        ? $"Add them with this mod's own change: {change.Rule.Label} (the change it makes on {change.RowsCovered} of its rows)"
        : null;

    /// <summary>What Add does with the current choices, for its tooltip and the line under the list.</summary>
    // Not "field": in C# 14 that word inside a property means the property's hidden backing field
    // (always null here), so the hint went blank as soon as a field was picked (final review 2026-10-28).
    public string NotInModAddHint => NotInModFieldName is not { } fieldName
        ? "Adds each ticked row with all of its game values; change what you want after."
        : NotInModUseModChange && _notInModChange is not null
            ? $"Adds each ticked row with only {FieldLabel(fieldName)}, changed the way this mod changes it."
            : $"Adds each ticked row with only {FieldLabel(fieldName)}, at the game's value: change it after (pick several on the left for mass edit). Left unchanged, Save drops it.";

    public bool HasNotInModRows => NotInModRows.Count > 0;

    private bool _notInModLoading;

    private static string FieldLabel(string field) => GameItemEditor.FieldText(field, isNumber: true).Label;

    partial void OnNotInModTableChanged(string? value)
    {
        if (_notInModLoading)
        {
            return;
        }

        LoadNotInModFieldOptions();
        RefreshNotInMod();
    }

    partial void OnNotInModFieldChanged(string? value)
    {
        if (!_notInModLoading)
        {
            RefreshNotInMod();
        }
    }

    // The search only narrows the list: the mod's own change for the field stays what it was (the
    // suggester takes most of a second on a big mod's recipes, too slow for each search).
    partial void OnNotInModSearchChanged(string value)
    {
        if (!_notInModLoading)
        {
            RefreshNotInMod(findChange: false);
        }
    }

    partial void OnNotInModUseModChangeChanged(bool value) => OnPropertyChanged(nameof(NotInModAddHint));

    /// <summary>Called when the view opens: the tables this mod changes, starting on the selected item's.</summary>
    private void OpenNotInMod()
    {
        _notInModLoading = true;
        try
        {
            var previous = NotInModTable;
            NotInModTables.Clear();
            foreach (var file in _package.Rows.Where(r => r.FileItems.Count > 0 && HasGameTable(r.CurrentFile)).Select(r => r.CurrentFile)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                NotInModTables.Add(file);
            }

            NotInModTable = NotInModTables.FirstOrDefault(t => Same(t, previous))
                ?? NotInModTables.FirstOrDefault(t => Same(t, SelectedItem?.CurrentFile))
                ?? NotInModTables.FirstOrDefault();
            LoadNotInModFieldOptions();
        }
        finally
        {
            _notInModLoading = false;
        }

        RefreshNotInMod();
    }

    private void LoadNotInModFieldOptions()
    {
        var wasLoading = _notInModLoading;
        _notInModLoading = true;
        try
        {
            var previous = NotInModField;
            NotInModFieldOptions.Clear();
            var row = _package.Rows.FirstOrDefault(r => Same(r.CurrentFile, NotInModTable));
            var fields = (row?.FileItems ?? []).SelectMany(i => i.Fields.Keys).Where(k => k != "Name")
                .GroupBy(k => k, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).ToList();
            foreach (var field in fields)
            {
                NotInModFieldOptions.Add(field);
            }

            NotInModFieldOptions.Add(AnyRowOption);

            // A mod that changes one field (most do) starts filtered on it: a stack mod sees only the rows that stack.
            NotInModField = previous is not null && previous != AnyRowOption && NotInModFieldOptions.Contains(previous) ? previous : NotInModFieldOptions[0];
        }
        finally
        {
            _notInModLoading = wasLoading;
        }
    }

    /// <summary>Rebuilds the list: game rows of the table the mod has no row for, that set the field (when one is picked) and match the search.</summary>
    private void RefreshNotInMod(bool findChange = true)
    {
        NotInModRows.Clear();
        if (findChange)
        {
            _notInModChange = null;
        }

        NotInModStatus = null;
        if (NotInModTable is not { } table || GameTables.Rows(table) is not { } rows)
        {
            NotInModSummary = NotInModTables.Count == 0
                ? "This lists the game rows of the tables this mod changes that it doesn't have yet. This mod changes no game table yet."
                : null;
            NotifyNotInMod();
            return;
        }

        var field = NotInModFieldName;
        var modRow = _package.Rows.FirstOrDefault(r => Same(r.CurrentFile, table));
        var have = (modRow?.FileItems ?? []).Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var search = NotInModSearch.Trim();
        var names = ItemNames();
        foreach (var (rowName, node) in rows)
        {
            if (have.Contains(rowName))
            {
                continue;
            }

            var own = field is null ? null : (node as System.Text.Json.Nodes.JsonObject)?[field];
            if (field is not null && own is null)
            {
                continue;
            }

            var gameItem = names?.ItemFor(table, rowName);
            var title = (gameItem is null ? null : names!.DisplayName(gameItem)) ?? rowName;
            if (search.Length > 0 && !rowName.Contains(search, StringComparison.OrdinalIgnoreCase) && !title.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            NotInModRows.Add(new NotInModRowViewModel(rowName, title, own is null ? "" : $"game: {EditorValueText.Plain(own)}"));
        }

        if (field is not null && findChange)
        {
            _notInModChange = ScaleRuleSuggester.Suggest(PackageWithoutRules(), GameTables, table, field)
                .Where(s => Same(s.Rule.CurrentFile, table) && s.Rule.Field == field)
                .MaxBy(s => s.RowsCovered);
        }

        var tableName = GameTableIndex.TableNameOf(table);
        NotInModSummary = NotInModRows.Count == 0
            ? (field is null ? $"This mod has every row of {tableName}." : $"Every row of {tableName} that sets {FieldLabel(field)} is in this mod already.")
            : field is null
                ? $"{NotInModRows.Count} row(s) of {tableName} aren't in this mod."
                : $"{NotInModRows.Count} row(s) of {tableName} set {FieldLabel(field)} and aren't in this mod. After a game update, new ones show up here.";
        NotifyNotInMod();
    }

    /// <summary>The mod's rows without its rules, so the suggester also reads fields a rule owns (a rule's rows follow it by definition).</summary>
    private ExmodPackage PackageWithoutRules() => new()
    {
        Name = _package.Name, Author = _package.Author, Version = _package.Version, Description = _package.Description, FileName = _package.FileName,
        Rows = _package.Rows,
    };

    private void NotifyNotInMod()
    {
        OnPropertyChanged(nameof(HasNotInModRows));
        OnPropertyChanged(nameof(HasNotInModChange));
        OnPropertyChanged(nameof(NotInModChangeText));
        OnPropertyChanged(nameof(NotInModAddHint));
    }

    [RelayCommand]
    private void PickAllNotInMod()
    {
        foreach (var row in NotInModRows)
        {
            row.IsPicked = true;
        }
    }

    [RelayCommand]
    private void PickNoNotInMod()
    {
        foreach (var row in NotInModRows)
        {
            row.IsPicked = false;
        }
    }

    /// <summary>Adds the picked rows to the mod in one undoable step (see NotInModAddHint for what each gets).</summary>
    [RelayCommand]
    private void AddNotInModRows()
    {
        var picked = NotInModRows.Where(r => r.IsPicked).ToList();
        if (NotInModTable is not { } table || picked.Count == 0)
        {
            NotInModStatus = "Tick the rows to add first (or Tick all).";
            return;
        }

        var field = NotInModFieldName;

        // The mod's own change, run on current game data for the whole table (the picked rows may
        // not match the rule's name filter: the user picked them, so they get the change).
        Dictionary<string, Dictionary<string, System.Text.Json.Nodes.JsonNode?>>? changed = null;
        if (field is not null && NotInModUseModChange && _notInModChange is { } change)
        {
            var rule = change.Rule.Clone();
            rule.RowFilter = null;
            rule.IncludeInherited = true;
            var (generated, _) = ScaleRules.Generate([rule], _dataFolder, new MergeReport(), GameTables);
            changed = generated.GetValueOrDefault(table);
        }

        // The table box only lists tables the mod already has rows in.
        if (_package.Rows.FirstOrDefault(r => Same(r.CurrentFile, table)) is not { } modRow)
        {
            return;
        }

        PushUndoSnapshot();
        foreach (var pickedRow in picked)
        {
            if (GameTables.Row(table, pickedRow.RowName) is not { } gameRow)
            {
                continue;
            }

            ExmodFileItem item;
            if (field is null)
            {
                item = ExmodFileItem.FromRow(pickedRow.RowName, gameRow);
            }
            else
            {
                item = new ExmodFileItem { Name = pickedRow.RowName };
                item.Fields[field] = changed is not null && changed.TryGetValue(pickedRow.RowName, out var fields) && fields.TryGetValue(field, out var value)
                    ? value?.DeepClone()
                    : gameRow[field]?.DeepClone();
            }

            modRow.FileItems.Add(item);
        }

        var changeLabel = _notInModChange?.Rule.Label;
        RefreshBaseDiff();
        var first = picked[0].RowName;
        ReloadItems();
        SelectedItem = Items.FirstOrDefault(i => Same(i.CurrentFile, table) && Same(i.ItemName, first)) ?? SelectedItem;
        RefreshNotInMod();
        NotInModStatus = changed is not null
            ? $"Added {picked.Count} row(s) with {changeLabel}. Save to keep them."
            : $"Added {picked.Count} row(s) with the game's values. Change them on the left (pick several for mass edit), then Save.";
    }
}
