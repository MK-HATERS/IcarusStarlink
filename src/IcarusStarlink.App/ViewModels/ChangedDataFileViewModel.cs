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
    public string Summary => $"{StatusLabel} · {ChangeCount} change(s)";

    public ObservableCollection<ChangedRowViewModel> Rows { get; } = [];

    public ChangedDataFileViewModel(ChangedDataFile file)
    {
        RelativePath = file.RelativePath;
        StatusLabel = file.IsNewFile ? "New file" : file.IsRemovedFile ? "Removed file" : "Modified";
        ChangeCount = file.FieldChanges.Count + file.RemovedRowNames.Count;

        foreach (var removedRowName in file.RemovedRowNames)
        {
            Rows.Add(new ChangedRowViewModel(removedRowName, "", "row removed", "", ""));
        }

        foreach (var change in file.FieldChanges)
        {
            var kind = change.IsNewItem ? "new item" : change.IsFieldRemoved ? "field removed" : "changed";
            Rows.Add(new ChangedRowViewModel(change.ItemName, change.FieldName, kind, FormatValue(change.OriginalValue), FormatValue(change.NewValue)));
        }
    }

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

public sealed record ChangedRowViewModel(string ItemName, string FieldName, string ChangeKind, string OldValueDisplay, string NewValueDisplay);
