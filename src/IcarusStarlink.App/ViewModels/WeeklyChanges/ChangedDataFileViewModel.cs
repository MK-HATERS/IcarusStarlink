using System.Collections.ObjectModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using IcarusStarlink.PakIO.DataChanges;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One JSON file in a WeeklyChangeReport, plus its per-row display rows — combines FieldChanges and RemovedRowNames into one flat list, since both are just "things that changed about this file" from a display standpoint even though they come from different parts of ChangedDataFile.</summary>
public sealed class ChangedDataFileViewModel
{
    public string RelativePath { get; }
    public string StatusLabel { get; }
    public int ChangeCount { get; }
    public string Summary => $"{StatusLabel} · {ChangeCount} {(ChangeCount == 1 ? "change" : "changes")}";

    /// <summary>The file's rows, built the first time they're asked for (the file is opened): a big
    /// report has thousands, and most files are never opened. Summary and ChangeCount come from the
    /// counts, so the file list doesn't build them.</summary>
    public ObservableCollection<ChangedRowViewModel> Rows => _rows ??= BuildRows();

    internal bool RowsBuilt => _rows is not null;

    /// <summary>The changes this file view was made from, so a search can look through them without building rows.</summary>
    public ChangedDataFile Source { get; }

    private ObservableCollection<ChangedRowViewModel>? _rows;

    /// <summary>The Item column's text for a change outside the rows (Defaults, RowStruct).</summary>
    public const string EveryRowItemName = "(every row)";

    public ChangedDataFileViewModel(ChangedDataFile file)
    {
        Source = file;
        RelativePath = file.RelativePath;
        StatusLabel = file.IsNewFile ? "New file" : file.IsRemovedFile ? "Removed file" : "Modified";
        ChangeCount = file.FieldChanges.Count + file.RemovedRowNames.Count + file.ChangedTableParts.Count;
    }

    private ObservableCollection<ChangedRowViewModel> BuildRows()
    {
        var rows = new ObservableCollection<ChangedRowViewModel>();

        // A change outside the rows: Defaults are what every row starts from, so it can change many items at once.
        foreach (var part in Source.ChangedTableParts)
        {
            rows.Add(new ChangedRowViewModel(EveryRowItemName, part, part == "Defaults" ? "defaults changed" : "table changed", "", ""));
        }

        foreach (var removedRowName in Source.RemovedRowNames)
        {
            rows.Add(new ChangedRowViewModel(removedRowName, "", "row removed", "", ""));
        }

        foreach (var change in Source.FieldChanges)
        {
            var kind = change.IsNewItem ? "new item" : change.IsFieldRemoved ? "field removed" : "changed";
            rows.Add(new ChangedRowViewModel(change.ItemName, change.FieldName, kind, FormatValue(change.OriginalValue), FormatValue(change.NewValue)));
        }

        return rows;
    }

    /// <summary>Item and field of every row Rows would have, in the same order, without building them.</summary>
    internal IEnumerable<(string ItemName, string FieldName)> RowKeys() =>
        Source.ChangedTableParts.Select(part => (EveryRowItemName, part))
            .Concat(Source.RemovedRowNames.Select(name => (name, "")))
            .Concat(Source.FieldChanges.Select(change => (change.ItemName, change.FieldName)));

    // Same bug class already fixed once in ExmodChangesFormatter: the default JSON encoder
    // escapes safe ASCII as \uXXXX (`"` -> ", `+` -> +) — unreadable for real field
    // values that use both constantly (e.g. Icarus's own "(Value=\"Xxx_+\")" stat convention).
    private static readonly JsonSerializerOptions DisplayJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string FormatValue(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is null)
        {
            return "";
        }

        var json = node.ToJsonString(DisplayJson);
        // A field's own value can be a deeply nested compound (real example seen live: a
        // creature's "Variations" array, hundreds of characters of mesh/material paths) —
        // truncated so one cell can't blow out the whole row; the point here is "something
        // changed", not a full diff viewer.
        const int maxLength = 200;
        return json.Length > maxLength ? json[..maxLength] + "…" : json;
    }
}

public sealed record ChangedRowViewModel(string ItemName, string FieldName, string ChangeKind, string OldValueDisplay, string NewValueDisplay)
{
    public string ModNames { get; init; } = "";
}
