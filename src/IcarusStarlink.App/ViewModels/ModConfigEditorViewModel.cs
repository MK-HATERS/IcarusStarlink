using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Install;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One copy of a UE4SS mod whose config can be edited: the game's (or Starlink's staging, while the mod is off), or a Starlink server's.</summary>
public sealed record ModConfigLocation(string Name, string ModFolder, bool IsServer)
{
    public override string ToString() => Name;
}

/// <summary>Another copy of the mod the same changes can be saved to.</summary>
public sealed partial class ModConfigAlsoSave(ModConfigLocation location) : ObservableObject
{
    public ModConfigLocation Location { get; } = location;

    [ObservableProperty]
    private bool _isChecked;
}

/// <summary>
/// Library > right-click a UE4SS mod > Edit config. Shows the mod's config file (config.ini,
/// config.lua, config\*.ini…) as fields: on/off switches, choices, numbers and text, each with the
/// comment the mod's author wrote above it. "Edit as text" shows the whole file for anything the
/// fields don't cover. Saving changes only the values that were edited (every comment and line
/// stays), backs the old file up first, and can apply the same changes to the mod on a server.
/// </summary>
public sealed partial class ModConfigEditorViewModel : ObservableObject
{
    private readonly IReadOnlyList<ModConfigLocation> _allLocations;
    private readonly string _backupDirectory;

    private IConfigText? _document;
    private IConfigText? _diskDocument;
    private IConfigText? _defaults;
    private string _diskText = "";
    private bool _diskHasBom;
    private List<Ue4ssConfigField> _fields = [];
    private bool _loading;

    private IReadOnlyList<Ue4ssConfigReview>? _reviews;
    private readonly Action? _markReviewed;

    /// <summary>
    /// Unsaved edits of the files (or copies) shown before, until they're shown again: switching the
    /// File or "Edit the copy in" drop-down used to throw them away without a word.
    /// </summary>
    private readonly Dictionary<(string File, string Folder), Draft> _drafts = [];

    /// <summary>The file and copy the fields show now, for keeping their edits when another is picked.</summary>
    private (string File, string Folder)? _loadedKey;

    /// <param name="Text">The file as edited so far (text-view edits in it).</param>
    /// <param name="FieldValues">The fields edited and not yet written into the text, by section and key.</param>
    private sealed record Draft(string Text, bool TextMode, Dictionary<(string Section, string Key), string> FieldValues, string Label);

    /// <summary>"[MkHatersSettings] Stability [server]": every setting of the file is the server's, but those tagged [local].</summary>
    private bool _fileWideServer;

    /// <summary>The biggest config file the editor opens (a downloaded mod could ship anything under that name).</summary>
    internal const long MaxConfigBytes = 2 * 1024 * 1024;

    /// <param name="reviews">What the last update changed in the config files (settings added or gone), to point out; null when nothing's waiting.</param>
    /// <param name="markReviewed">Called by Done reviewing: the caller forgets the reviews.</param>
    public ModConfigEditorViewModel(
        string modName, IReadOnlyList<ModConfigLocation> locations, string backupDirectory,
        IReadOnlyList<Ue4ssConfigReview>? reviews = null, Action? markReviewed = null)
    {
        ModName = modName;
        _allLocations = locations;
        _backupDirectory = backupDirectory;
        _reviews = reviews is { Count: > 0 } ? reviews : null;
        _markReviewed = markReviewed;

        // Every config file any copy has, the first copy's first (Find puts the main config.lua /
        // config.ini first, so that's what opens). The shipped defaults (config.default.lua) aren't
        // offered: they're what Reset to default reads, not a file to edit (user's call 2026-10-27).
        foreach (var file in locations.SelectMany(l => Ue4ssModConfigFiles.Find(l.ModFolder)).Where(f => !f.IsDefaults))
        {
            if (!Files.Any(f => f.RelativePath.Equals(file.RelativePath, StringComparison.OrdinalIgnoreCase)))
            {
                Files.Add(file);
            }
        }

        SelectedFile = Files.FirstOrDefault();
    }

    public string ModName { get; }

    public string Title => $"Edit config: {ModName}";

    public ObservableCollection<Ue4ssModConfigFile> Files { get; } = [];

    public ObservableCollection<ModConfigLocation> Locations { get; } = [];

    public ObservableCollection<ModConfigAlsoSave> AlsoSaveTo { get; } = [];

    public ObservableCollection<Ue4ssConfigSection> Sections { get; } = [];

    public bool HasSeveralFiles => Files.Count > 1;

    public bool HasSeveralLocations => Locations.Count > 1;

    public bool HasOtherLocations => AlsoSaveTo.Count > 0;

    public bool HasFiles => Files.Count > 0;

    /// <summary>False for a file only the text view can edit (config.json, or a Lua file with no plain settings).</summary>
    public bool CanUseFields => _fields.Count > 0;

    public bool ShowFields => !IsTextMode && CanUseFields;

    public bool ShowNoFieldsNote => !IsTextMode && !CanUseFields;

    public string CurrentPath => SelectedFile is { } f && SelectedLocation is { } l ? Path.Combine(l.ModFolder, f.RelativePath) : "";

    /// <summary>The last update's changes to the file shown, until Done reviewing.</summary>
    private Ue4ssConfigReview? CurrentReview =>
        SelectedFile is { } f ? _reviews?.FirstOrDefault(r => r.File.Equals(f.RelativePath, StringComparison.OrdinalIgnoreCase)) : null;

    public bool HasReview => CurrentReview is not null;

    /// <summary>
    /// "Updated to 1.4.0 on 27 Oct 2026: 2 new settings (marked below, at their defaults); 1 no
    /// longer exists: OldName = 5 …" — what to look over after an update of the mod.
    /// </summary>
    public string? ReviewText
    {
        get
        {
            if (CurrentReview is not { } review)
            {
                return null;
            }

            var when = review.UpdatedAtUtc == default ? "" : $" on {review.UpdatedAtUtc.ToLocalTime():d MMM yyyy}";
            var parts = new List<string>();
            if (review.Added.Count > 0)
            {
                parts.Add(review.Added.Count == 1
                    ? $"1 new setting (marked below, at its default): {review.Added[0]}"
                    : $"{review.Added.Count} new settings (marked below, at their defaults)");
            }

            if (review.Removed.Count > 0)
            {
                parts.Add($"{(review.Removed.Count == 1 ? "1 setting the mod no longer has" : $"{review.Removed.Count} settings the mod no longer has")} (renamed or dropped; your values were: {string.Join(", ", review.Removed)}) — if it was renamed, set the new one to match");
            }

            var kept = review.MergedIntoNewLayout
                ? "Your other settings were carried over into this version's file."
                : "Your file was kept as it was (it holds something that can't be carried over, like a table over several lines), so the new settings aren't in it yet; add them by hand or with the mod's own Add them.";
            return $"Updated{(review.Version is { Length: > 0 } v ? $" to {v}" : "")}{when}: {string.Join("; ", parts)}. {kept}";
        }
    }

    /// <summary>The user has looked over the update's changes: the marks go, here and in the Library.</summary>
    [RelayCommand]
    private void DoneReviewing()
    {
        _reviews = null;
        _markReviewed?.Invoke();
        OnPropertyChanged(nameof(HasReview));
        OnPropertyChanged(nameof(ReviewText));
        // The marks go; the edits made so far stay (rebuilding the fields used to drop them).
        var edits = EditedFieldValues();
        BuildFields();
        RestoreFieldValues(edits);
    }

    /// <summary>Where "Default:" and Reset to default come from when the mod ships its defaults in a file beside this one; null when it doesn't.</summary>
    public string? DefaultsNote => _defaults is not null && SelectedFile is { } f
        ? $"The defaults shown come from the mod's {Path.GetFileName(Ue4ssModConfigFiles.DefaultsPathFor(f.RelativePath))} (not edited here); Reset to default puts a setting back to them."
        : null;

    [ObservableProperty]
    private Ue4ssModConfigFile? _selectedFile;

    [ObservableProperty]
    private ModConfigLocation? _selectedLocation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFields), nameof(ShowNoFieldsNote))]
    private bool _isTextMode;

    [ObservableProperty]
    private string _rawText = "";

    [ObservableProperty]
    private string _filter = "";

    [ObservableProperty]
    private string? _statusMessage;

    partial void OnSelectedFileChanged(Ue4ssModConfigFile? value)
    {
        Locations.Clear();
        if (value is not null)
        {
            foreach (var location in _allLocations.Where(l => File.Exists(Path.Combine(l.ModFolder, value.RelativePath))))
            {
                Locations.Add(location);
            }
        }

        OnPropertyChanged(nameof(HasSeveralLocations));
        var previous = SelectedLocation;
        SelectedLocation = Locations.FirstOrDefault(l => l == previous) ?? Locations.FirstOrDefault();
        if (SelectedLocation == previous)
        {
            Load(keepDraft: true);
        }
    }

    partial void OnSelectedLocationChanged(ModConfigLocation? value) => Load(keepDraft: true);

    partial void OnFilterChanged(string value) => BuildSections();

    partial void OnRawTextChanged(string value) => NotifyUnsaved();

    /// <summary>The file shown has edits that aren't saved.</summary>
    public bool HasChangesHere => _document is not null && (IsTextMode
        ? RawText != _diskText
        : _fields.Any(f => f.IsChanged) || _document.ToText() != _diskText);

    /// <summary>Edits not saved yet, in the file shown or in another file or copy picked before.</summary>
    public bool HasUnsavedChanges => HasChangesHere || _drafts.Count > 0;

    /// <summary>"Not saved yet" beside Save, naming the other files that hold unsaved edits too.</summary>
    public string? UnsavedText
    {
        get
        {
            var others = _drafts.Values.Select(d => d.Label).ToList();
            var here = HasChangesHere ? "Not saved yet." : null;
            var elsewhere = others.Count == 0 ? null : $"Also not saved: {string.Join(", ", others)}.";
            return here is null && elsewhere is null ? null : string.Join(" ", new[] { here, elsewhere }.Where(t => t is not null));
        }
    }

    private void NotifyUnsaved()
    {
        OnPropertyChanged(nameof(HasChangesHere));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(UnsavedText));
        OnPropertyChanged(nameof(CanResetAllToDefault));
    }

    /// <summary>Asks what to do with unsaved edits when the window closes: true = Save, false = Don't save, null = Cancel. Set by the window (tests set their own answer).</summary>
    public Func<string, bool?>? AskSaveOnClose { get; set; }

    /// <summary>
    /// Called as the window closes: true when it may close. Unsaved edits (here or in another file
    /// picked before) ask Save / Don't save / Cancel; a Save that doesn't go through keeps the window
    /// open with the reason showing.
    /// </summary>
    public bool ConfirmClose()
    {
        if (!HasUnsavedChanges || AskSaveOnClose is null)
        {
            return true;
        }

        switch (AskSaveOnClose($"Your changes to {ModName}'s config aren't saved. Save them before closing?"))
        {
            case null:
                return false;
            case false:
                return true;
            default:
                return SaveAll();
        }
    }

    /// <summary>Saves the file shown, then each file or copy with edits kept aside. False when one couldn't be saved (it's shown, with why).</summary>
    private bool SaveAll()
    {
        if (HasChangesHere && !SaveHere())
        {
            return false;
        }

        while (_drafts.Count > 0)
        {
            var key = _drafts.Keys.First();
            if (Files.FirstOrDefault(f => f.RelativePath.Equals(key.File, StringComparison.OrdinalIgnoreCase)) is not { } file)
            {
                _drafts.Remove(key);
                continue;
            }

            SelectedFile = file;
            if (Locations.FirstOrDefault(l => l.ModFolder == key.Folder) is { } location)
            {
                SelectedLocation = location;
            }

            if (_loadedKey != key)
            {
                return false; // the copy is gone: its edits stay listed as not saved
            }

            if (HasChangesHere && !SaveHere())
            {
                return false;
            }
        }

        return !HasUnsavedChanges;
    }

    /// <summary>A setting differs from the mod's shipped default (shown as Reset to default).</summary>
    public bool CanResetAllToDefault => _fields.Any(f => f.CanResetToDefault);

    /// <summary>Puts every shown setting back to the mod's default (not saved until Save; hidden settings stay as they are).</summary>
    [RelayCommand]
    private void ResetAllToDefault()
    {
        var reset = _fields.Where(f => f.CanResetToDefault).ToList();
        foreach (var field in reset)
        {
            field.ResetToDefaultCommand.Execute(null);
        }

        StatusMessage = reset.Count == 0 ? "Every setting is at its default."
            : $"{(reset.Count == 1 ? "1 setting" : $"{reset.Count} settings")} put back to the default. Save to keep it, or Discard changes.";
    }

    /// <summary>The fields edited so far, by section and key.</summary>
    private Dictionary<(string Section, string Key), string> EditedFieldValues() =>
        _fields.Where(f => f.Value != f.OriginalValue).ToDictionary(f => (f.Section, f.Key), f => f.Value);

    private void RestoreFieldValues(Dictionary<(string Section, string Key), string> values)
    {
        foreach (var field in _fields)
        {
            if (values.TryGetValue((field.Section, field.Key), out var value))
            {
                field.Value = value;
            }
        }
    }

    /// <summary>Puts the shown file's unsaved edits aside (or forgets them when there are none) before another file or copy is shown.</summary>
    private void KeepDraft()
    {
        if (_loadedKey is not { } key || _document is null)
        {
            return;
        }

        if (!HasChangesHere)
        {
            _drafts.Remove(key);
            return;
        }

        var label = _allLocations.Count(l => File.Exists(Path.Combine(l.ModFolder, key.File))) > 1
            ? $"{key.File} ({_allLocations.FirstOrDefault(l => l.ModFolder == key.Folder)?.Name})"
            : key.File;
        _drafts[key] = IsTextMode
            ? new Draft(RawText, true, [], label)
            : new Draft(_document.ToText(), false, EditedFieldValues(), label);
    }

    partial void OnIsTextModeChanged(bool value)
    {
        if (_loading || _document is null)
        {
            return;
        }

        if (value)
        {
            // Into the text view: the file as it would be saved, with the field edits in it.
            var problems = ApplyFields();
            RawText = _document.ToText();
            StatusMessage = problems.Count > 0 ? $"Not carried over (fix them in the text): {string.Join("; ", problems)}" : null;
        }
        else
        {
            // Back to the fields: read them from the edited text. Text with no settings left in it
            // stays in the text view: with no fields, "Edit as text" greys out and the text would be
            // out of reach.
            var parsed = Parse(SelectedFile!.Format, RawText);
            if (!parsed.Settings.Any())
            {
                StatusMessage = "No settings found in the text, so it stays in the text view.";
                // _loading: switching back must not rebuild the text from the fields (losing the edit).
                _loading = true;
                try
                {
                    IsTextMode = true;
                }
                finally
                {
                    _loading = false;
                }

                return;
            }

            _document = parsed;
            BuildFields();
        }

        NotifyUnsaved();
    }

    /// <param name="keepDraft">A different file or copy is being shown: the edits of the one shown so far are kept aside. False for Save and Discard changes.</param>
    private void Load(bool keepDraft = false)
    {
        if (keepDraft)
        {
            KeepDraft();
        }

        _loading = true;
        try
        {
            _document = _diskDocument = _defaults = null;
            _fields = [];
            _loadedKey = null;
            AlsoSaveTo.Clear();
            StatusMessage = null;
            Draft? draft = null;
            if (SelectedFile is { } file && SelectedLocation is { } location)
            {
                try
                {
                    // A downloaded mod's file: real configs are a few KB (Wildlife's, the biggest, is 18 KB).
                    if (new FileInfo(CurrentPath).Length > MaxConfigBytes)
                    {
                        throw new IOException($"it's over {MaxConfigBytes / 1024 / 1024} MB, too big for a config. Open it in a text editor.");
                    }

                    var bytes = File.ReadAllBytes(CurrentPath);
                    _diskHasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                    _diskText = Encoding.UTF8.GetString(bytes, _diskHasBom ? 3 : 0, bytes.Length - (_diskHasBom ? 3 : 0));
                    _fileWideServer = FileWideServerTag().IsMatch(_diskText);
                    _diskDocument = Parse(file.Format, _diskText);
                    _loadedKey = (file.RelativePath, location.ModFolder);
                    // Edits made before another file was picked: back, still unsaved.
                    _drafts.Remove(_loadedKey.Value, out draft);
                    _document = Parse(file.Format, draft?.Text ?? _diskText);
                    _defaults = ReadDefaults(location, file);
                    RawText = draft?.Text ?? _diskText;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    StatusMessage = $"Couldn't read {CurrentPath}: {ex.Message}";
                }

                foreach (var other in Locations.Where(l => l != location))
                {
                    AlsoSaveTo.Add(new ModConfigAlsoSave(other));
                }
            }

            BuildFields();
            if (draft is not null)
            {
                RestoreFieldValues(draft.FieldValues);
            }

            IsTextMode = (draft?.TextMode ?? false) || (!CanUseFields && _document is not null);
            if (draft is not null)
            {
                StatusMessage = "Your unsaved changes to this file are back.";
            }

            NotifyUnsaved();
            OnPropertyChanged(nameof(HasOtherLocations));
            OnPropertyChanged(nameof(CurrentPath));
            OnPropertyChanged(nameof(DefaultsNote));
            OnPropertyChanged(nameof(HasReview));
            OnPropertyChanged(nameof(ReviewText));
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// The values the mod ships with, from the file beside this one (config.default.lua next to
    /// config.lua, the way the MkHaters mods keep them), read the same way; null when there is none.
    /// </summary>
    private static IConfigText? ReadDefaults(ModConfigLocation location, Ue4ssModConfigFile file)
    {
        var path = Path.Combine(location.ModFolder, Ue4ssModConfigFiles.DefaultsPathFor(file.RelativePath));
        if (file.Format == Ue4ssModConfigFormat.Text || !File.Exists(path) || new FileInfo(path).Length > MaxConfigBytes)
        {
            return null;
        }

        try
        {
            return Parse(file.Format, File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A setting with no "Default:" of its own takes the shipped defaults file's value.</summary>
    private Ue4ssSetting WithShippedDefault(Ue4ssSetting setting) =>
        setting.Default is null && _defaults?.Get(setting.Section, setting.Key) is { } shipped ? setting with { Default = shipped } : setting;

    private void BuildFields()
    {
        // A setting the mod marks [hidden] (internal or testing) stays in the file, out of the fields.
        var added = CurrentReview?.Added ?? [];
        static string Name(Ue4ssSetting s) => s.Section.Length == 0 ? s.Key : $"[{s.Section}] {s.Key}";
        var document = _document;
        _fields = document is null
            ? []
            : [.. document.Settings.Where(s => !s.Hidden).Select(s => new Ue4ssConfigField(WithShippedDefault(s), _diskDocument?.Get(s.Section, s.Key))
            {
                IsNewInVersion = added.Contains(Name(s), StringComparer.OrdinalIgnoreCase),
                Group = document.GroupOf(s.Section, s.Key),
                Hint = document.HintOf(s.Key),
                ServerDecides = (s.ServerDecides || _fileWideServer) && !s.LocalOnly,
            })];
        foreach (var field in _fields)
        {
            field.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(Ue4ssConfigField.Value))
                {
                    NotifyUnsaved();
                }
            };
        }

        OnPropertyChanged(nameof(CanUseFields));
        OnPropertyChanged(nameof(ShowFields));
        OnPropertyChanged(nameof(ShowNoFieldsNote));
        OnPropertyChanged(nameof(CanResetAllToDefault));
        BuildSections();
    }

    private void BuildSections()
    {
        Sections.Clear();
        var filter = Filter.Trim();
        // Grouped by the ini's [Section] or the Lua file's comment headings ("==== GUESTS ===="), in file order.
        foreach (var group in _fields
                     .Where(f => filter.Length == 0
                         || f.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || f.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || f.Group.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || f.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
                     .GroupBy(f => f.Group))
        {
            Sections.Add(new Ue4ssConfigSection(group.Key.Length == 0 ? "Settings" : Ue4ssSettingLabel.Heading(group.Key), [.. group]));
        }

        OnPropertyChanged(nameof(ShowNoMatches));
    }

    /// <summary>The search hides every setting.</summary>
    public bool ShowNoMatches => ShowFields && Sections.Count == 0 && Filter.Trim().Length > 0;

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
        if (textChanged)
        {
            try
            {
                Write(location, file, text, _diskHasBom);
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
                var path = Path.Combine(target.ModFolder, file.RelativePath);
                if (new FileInfo(path).Length > MaxConfigBytes)
                {
                    throw new IOException($"its copy is over {MaxConfigBytes / 1024 / 1024} MB, too big for a config");
                }

                var bytes = File.ReadAllBytes(path);
                var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                var targetText = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
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
                    Write(target, file, newText, bom);
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
            + (problemsSaving.Count > 0 ? $"{(saved.Count > 0 ? " " : "")}Couldn't save: {string.Join("; ", problemsSaving)}" : "");
        return true;
    }

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

    private void Write(ModConfigLocation location, Ue4ssModConfigFile file, string text, bool bom)
    {
        var path = Path.Combine(location.ModFolder, file.RelativePath);
        FolderBackup.BackupFile(path, Path.Combine(_backupDirectory, "UE4SS-Mod-Configs", SafeName(ModName), SafeName(location.Name)));
        IcarusStarlink.Core.IO.AtomicFile.WriteAllText(path, text, new UTF8Encoding(bom));
    }

    private static string SafeName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));

    private static IConfigText Parse(Ue4ssModConfigFormat format, string text) => format switch
    {
        Ue4ssModConfigFormat.Ini => new IniText(Ue4ssSettingsDocument.Parse(text, keepCommentLines: true)),
        Ue4ssModConfigFormat.Lua => new LuaText(LuaConfigDocument.Parse(text)),
        _ => new PlainText(text),
    };

    /// <summary>The three kinds of config file behind one shape: settings to show, a way to change one, and the text.</summary>
    private interface IConfigText
    {
        IReadOnlyList<Ue4ssSetting> Settings { get; }

        string? Get(string section, string key);

        /// <summary>Null when it worked, else why not.</summary>
        string? Set(string section, string key, string value);

        string ToText();

        /// <summary>The heading a setting is listed under: its [Section], or the Lua comment heading above it.</summary>
        string GroupOf(string section, string key) => section;

        /// <summary>A short note on how to write the value, when the box needs one (a Lua list).</summary>
        string? HintOf(string key) => null;
    }

    // "; [MkHatersSettings] Stability [server]" / "-- [MkHatersSettings] Wildlife [server]": the mod's whole file is read where the world runs.
    [System.Text.RegularExpressions.GeneratedRegex(@"^[ \t]*(?:--|;|#).*\[MkHatersSettings\][^\r\n]*\[server\]", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex FileWideServerTag();

    private sealed class IniText(Ue4ssSettingsDocument document) : IConfigText
    {
        public IReadOnlyList<Ue4ssSetting> Settings => document.Settings;

        public string? Get(string section, string key) => document.GetValue(section, key);

        public string? Set(string section, string key, string value)
        {
            try
            {
                document.SetValue(section, key, value);
                return null;
            }
            catch (ArgumentException ex)
            {
                return $"{key}: {ex.Message}";
            }
        }

        public string ToText() => document.ToText();
    }

    private sealed class LuaText(LuaConfigDocument document) : IConfigText
    {
        // Numbers, sliders, choices, [restart] and [hidden] from the comments, as MkHaters Mod Settings reads them.
        public IReadOnlyList<Ue4ssSetting> Settings =>
            [.. document.Settings.Select(s => Ue4ssSettingsDocument.DescribeLuaSetting(s.Key, s.Value, s.Kind, s.Comments, s.Note))];

        public string? Get(string section, string key) => document.GetValue(key);

        public string? Set(string section, string key, string value)
        {
            _byKey = null;
            return document.SetValue(key, value);
        }

        public string ToText() => document.ToText();

        // The settings by key, read once per change (GroupOf/HintOf run for every field).
        private Dictionary<string, LuaConfigSetting>? _byKey;

        private LuaConfigSetting? Raw(string key)
        {
            _byKey ??= document.Settings.GroupBy(s => s.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            return _byKey.GetValueOrDefault(key);
        }

        public string GroupOf(string section, string key) => Raw(key)?.Group ?? "";

        public string? HintOf(string key) =>
            Raw(key) is { Kind: LuaConfigValueKind.Table } s && !Ue4ssKeyBind.TryParse(s.Value, out _)
                ? "A Lua list: keep the { } and the commas, and names in quotes."
                : null;
    }

    private sealed class PlainText(string text) : IConfigText
    {
        public IReadOnlyList<Ue4ssSetting> Settings => [];

        public string? Get(string section, string key) => null;

        public string? Set(string section, string key, string value) => $"{key} isn't in this file";

        public string ToText() => text;
    }
}
