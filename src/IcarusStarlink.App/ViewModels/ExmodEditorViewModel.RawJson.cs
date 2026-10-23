using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>The File JSON and Full EXMOD JSON views: the mod as text, applied back with Apply.</summary>
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
        var existingIndex = _package.Rows.FindIndex(r => r.CurrentFile == SelectedItem.CurrentFile);
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
        SelectedItem = Items.FirstOrDefault(i => i.CurrentFile == appliedFile) ?? Items.FirstOrDefault();
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

    [RelayCommand]
    private void ApplyFullExmodJson()
    {
        ExmodPackage parsed;
        try
        {
            parsed = ExmodJson.Parse(FullExmodJsonText);
        }
        catch (Exception ex)
        {
            RawJsonStatusMessage = $"Invalid EXMOD JSON: {ex.Message}";
            return;
        }

        // Guards the same ambiguous-.EXMOD-files state 7.1's design already keeps FileName out of
        // the Item fields view for — Save() writes to "<FileName>.EXMOD" without deleting any
        // differently-named file already on disk, so silently accepting a changed fileName here
        // would leave two .EXMOD files in the mod's folder.
        if (parsed.FileName != _package.FileName)
        {
            RawJsonStatusMessage = $"Can't change \"fileName\" here (would leave two .EXMOD files on disk) — it must stay \"{_package.FileName}\".";
            return;
        }

        if (FractionalStatIn(parsed.Rows) is { } fractional)
        {
            RawJsonStatusMessage = $"Not applied: stats are whole numbers in the game, so {fractional} would lose its fraction.";
            return;
        }

        PushUndoSnapshot();
        ApplyParsedPackage(parsed);
        RawJsonStatusMessage = "Applied.";
    }

    private void RefreshFileJsonText()
    {
        if (SelectedItem is null)
        {
            FileJsonText = "";
            return;
        }

        var row = _package.Rows.FirstOrDefault(r => r.CurrentFile == SelectedItem.CurrentFile);
        FileJsonText = row is null ? "" : ExmodJson.RowToJsonObject(row).ToJsonString(DisplayOptions);
    }

    private void RefreshFullExmodJsonText() => FullExmodJsonText = ExmodJson.ToJsonObject(_package).ToJsonString(DisplayOptions);
}
