using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>The File JSON view: one file of the mod as text, applied back with Apply.</summary>
public sealed partial class ExmodEditorViewModel
{
    [RelayCommand]
    private void ApplyFileJson()
    {
        if (SelectedItem is null)
        {
            RawJsonStatusMessage = "Select an item first.";
            return;
        }

        JsonObject parsedObject;
        try
        {
            parsedObject = JsonNode.Parse(FileJsonText) as JsonObject
                ?? throw new FormatException("Root is not a JSON object.");
        }
        catch (Exception ex)
        {
            RawJsonStatusMessage = $"Invalid JSON: {ex.Message}";
            return;
        }

        ExmodFileRow newRow;
        try
        {
            newRow = ExmodJson.ParseRow(parsedObject);
        }
        catch (Exception ex)
        {
            RawJsonStatusMessage = $"Invalid file JSON: {ex.Message}";
            return;
        }

        if (FractionalStatIn([newRow]) is { } fractional)
        {
            RawJsonStatusMessage = $"Not applied: stats are whole numbers in the game, so {fractional} would lose its fraction.";
            return;
        }

        // A changed "CurrentFile" naming another file this mod already has would leave that file
        // in the mod twice, and only one of the two would be merged.
        if (!string.Equals(newRow.CurrentFile, SelectedItem.CurrentFile, StringComparison.OrdinalIgnoreCase)
            && _package.Rows.Any(r => string.Equals(r.CurrentFile, newRow.CurrentFile, StringComparison.OrdinalIgnoreCase)))
        {
            RawJsonStatusMessage = $"Not applied: this mod already has \"{newRow.CurrentFile}\". Edit it there, or keep \"CurrentFile\" as \"{SelectedItem.CurrentFile}\".";
            return;
        }

        PushUndoSnapshot();
        var existingIndex = _package.Rows.FindIndex(r => Same(r.CurrentFile, SelectedItem.CurrentFile));
        if (existingIndex >= 0)
        {
            _package.Rows[existingIndex] = newRow;
        }
        else
        {
            _package.Rows.Add(newRow);
        }

        RefreshBaseDiff();
        var appliedFile = newRow.CurrentFile;
        ReloadItems();
        SelectedItem = Items.FirstOrDefault(i => Same(i.CurrentFile, appliedFile)) ?? Items.FirstOrDefault();
        RawJsonStatusMessage = "Applied.";
    }

    /// <summary>The first stat with a fractional value in these rows ("Cold resistance: 5.5", with its row), or null.</summary>
    private static string? FractionalStatIn(IEnumerable<ExmodFileRow> rows)
    {
        foreach (var item in rows.SelectMany(r => r.FileItems))
        {
            foreach (var (_, value) in item.Fields)
            {
                if (GameStats.FractionalStat(value) is { } found)
                {
                    return $"{found} (in {item.Name})";
                }
            }
        }

        return null;
    }

    private void RefreshFileJsonText()
    {
        if (SelectedItem is null)
        {
            FileJsonText = "";
            return;
        }

        var row = _package.Rows.FirstOrDefault(r => Same(r.CurrentFile, SelectedItem.CurrentFile));
        FileJsonText = row is null ? "" : ExmodJson.RowToJsonObject(row).ToJsonString(ExmodJson.DisplayOptions);
    }
}
