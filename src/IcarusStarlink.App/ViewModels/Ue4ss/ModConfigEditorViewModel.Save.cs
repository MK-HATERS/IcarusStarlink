using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Install;

namespace IcarusStarlink.App.ViewModels;

/// <summary>Saving the shown file (and the ticked other copies), refusing or merging when it changed on disk, Discard, and the backed-up write.</summary>
public sealed partial class ModConfigEditorViewModel
{
    /// <summary>Writes every field's value into the working document; the ones that can't be written, with why.</summary>
    private List<string> ApplyFields()
    {
        var problems = new List<string>();
        if (_document is null)
        {
            return problems;
        }

        foreach (var field in _fields)
        {
            if (_document.Set(field.Section, field.Key, field.Value) is { } problem)
            {
                problems.Add(problem);
            }
        }

        return problems;
    }

    [RelayCommand]
    private void Save() => SaveHere();

    /// <summary>Saves the file shown (and the ticked other copies). False when it wasn't saved (the status says why).</summary>
    private bool SaveHere()
    {
        if (SelectedFile is not { } file || SelectedLocation is not { } location || _document is null || _diskDocument is null)
        {
            return false;
        }

        string text;
        if (IsTextMode)
        {
            text = RawText;
            _document = Parse(file.Format, text);
        }
        else
        {
            // A number the mod can't read ("1,5", "ten") is refused; one outside the range its
            // comment gives is only warned about, since the author may mean it (user's call 2026-10-26).
            var notNumbers = _fields.Where(f => f.IsNumber && f.IsChanged && Ue4ssSettingsDocument.Number(f.Value) is null)
                .Select(f => $"{f.Key} isn't a number (decimals take a dot: 1.5)");
            var badKeys = _fields.Where(f => f.KeyProblem.Length > 0).Select(f => $"{f.Key}: {f.KeyProblem}");
            var problems = notNumbers.Concat(badKeys).Concat(ApplyFields()).ToList();
            if (problems.Count > 0)
            {
                StatusMessage = $"Not saved: {string.Join("; ", problems)}";
                return false;
            }

            text = _document.ToText();
        }

        // The settings that differ from the file on disk: what the other copies get.
        var changes = _document.Settings
            .Where(s => !string.Equals(_diskDocument.Get(s.Section, s.Key)?.Trim(), s.Value.Trim(), StringComparison.Ordinal))
            .ToList();
        var extra = AlsoSaveTo.Where(a => a.IsChecked).Select(a => a.Location).ToList();
        var textChanged = text != _diskText;
        if (!textChanged && (extra.Count == 0 || changes.Count == 0 && file.Format != Ue4ssModConfigFormat.Text))
        {
            // Edits put back by hand: the file is as it was, so nothing is waiting to be saved.
            ReloadKeepingView();
            StatusMessage = "Nothing changed.";
            return true;
        }

        var saved = new List<string>();
        var problemsSaving = new List<string>();
        var changedOnDisk = false;
        if (textChanged)
        {
            try
            {
                // The file may have changed since it was opened (the mod's own in-game settings
                // menu writes it, so does another editor): writing the text read back then would
                // silently put every other setting back to what it was.
                var (nowText, nowEncoding) = ReadConfigText(Path.Combine(location.ModFolder, file.RelativePath));
                var encoding = _diskEncoding;
                if (nowText != _diskText)
                {
                    if (IsTextMode || file.Format == Ue4ssModConfigFormat.Text)
                    {
                        StatusMessage = "Not saved: the file was changed outside this window since it was opened (by the mod or another editor). "
                            + "Copy your text, click Discard changes to load the new file, then make your edit again.";
                        return false;
                    }

                    // The fields: only the settings changed here go into the file as it is now.
                    var current = Parse(file.Format, nowText);
                    foreach (var change in changes)
                    {
                        if (current.Set(change.Section, change.Key, change.Value) is { } problem)
                        {
                            StatusMessage = $"Not saved: the file was changed outside this window since it was opened, and {problem}. Click Discard changes to load the new file.";
                            return false;
                        }
                    }

                    text = current.ToText();
                    encoding = nowEncoding;
                    changedOnDisk = true;
                }

                if (text != nowText)
                {
                    Write(location, file, text, encoding);
                }

                saved.Add(location.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                StatusMessage = $"Not saved: {ex.Message}";
                return false;
            }
        }

        foreach (var target in extra)
        {
            try
            {
                var (targetText, targetEncoding) = ReadConfigText(Path.Combine(target.ModFolder, file.RelativePath));
                string newText;
                if (file.Format == Ue4ssModConfigFormat.Text)
                {
                    newText = text; // no settings to pick out: the whole file
                }
                else
                {
                    // Only the changed settings, so the server keeps its own other settings.
                    var document = Parse(file.Format, targetText);
                    foreach (var change in changes)
                    {
                        if (document.Set(change.Section, change.Key, change.Value) is { } problem)
                        {
                            problemsSaving.Add($"{target.Name}: {problem}");
                        }
                    }

                    newText = document.ToText();
                }

                if (newText != targetText)
                {
                    Write(target, file, newText, targetEncoding);
                }

                saved.Add(target.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problemsSaving.Add($"{target.Name}: {ex.Message}");
            }
        }

        ReloadKeepingView();
        StatusMessage = (saved.Count > 0
                ? $"Saved to {string.Join(" and ", saved)} (the old file is in Backups\\UE4SS-Mod-Configs). The mod reads it when the game or server starts, unless it has its own reload key."
                : "")
            + (changedOnDisk ? " The file had been changed outside this window since it was opened: only the settings you changed here were written into it." : "")
            + (problemsSaving.Count > 0 ? $"{(saved.Count > 0 ? " " : "")}Couldn't save: {string.Join("; ", problemsSaving)}" : "");
        return true;
    }

    /// <summary>A config file's text in its own encoding (any BOM not kept in the text, but noted); refuses one over MaxConfigBytes.</summary>
    private static ConfigText ReadConfigText(string path) => ConfigTextFile.Read(path, MaxConfigBytes);

    /// <summary>Throws away the shown file's unsaved edits (other files keep theirs).</summary>
    [RelayCommand]
    private void DiscardChanges() => ReloadKeepingView();

    /// <summary>Reads the shown file again from disk, staying in the text view when it was open and keeping the "Also save to" ticks.</summary>
    private void ReloadKeepingView()
    {
        var textMode = IsTextMode;
        var checkedNames = AlsoSaveTo.Where(a => a.IsChecked).Select(a => a.Location.Name).ToHashSet();
        Load();
        if (textMode && CanUseFields)
        {
            IsTextMode = true;
        }

        foreach (var also in AlsoSaveTo)
        {
            also.IsChecked = checkedNames.Contains(also.Location.Name);
        }
    }

    /// <param name="encoding">The file's own encoding and BOM on disk, kept.</param>
    private void Write(ModConfigLocation location, Ue4ssModConfigFile file, string text, ConfigTextEncoding encoding)
    {
        var path = Path.Combine(location.ModFolder, file.RelativePath);
        FolderBackup.BackupFile(path, Path.Combine(_backupDirectory, "UE4SS-Mod-Configs", SafeName(ModName), SafeName(location.Name)));
        ConfigTextFile.Write(path, text, encoding);
    }

    private static string SafeName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));
}
