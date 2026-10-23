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
    private string _diskText = "";
    private bool _diskHasBom;
    private List<Ue4ssConfigField> _fields = [];
    private bool _loading;

    public ModConfigEditorViewModel(string modName, IReadOnlyList<ModConfigLocation> locations, string backupDirectory)
    {
        ModName = modName;
        _allLocations = locations;
        _backupDirectory = backupDirectory;

        // Every config file any copy has, the first copy's first.
        foreach (var file in locations.SelectMany(l => Ue4ssModConfigFiles.Find(l.ModFolder)))
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
            Load();
        }
    }

    partial void OnSelectedLocationChanged(ModConfigLocation? value) => Load();

    partial void OnFilterChanged(string value) => BuildSections();

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
    }

    private void Load()
    {
        _loading = true;
        try
        {
            _document = _diskDocument = null;
            _fields = [];
            AlsoSaveTo.Clear();
            StatusMessage = null;
            if (SelectedFile is { } file && SelectedLocation is { } location)
            {
                try
                {
                    var bytes = File.ReadAllBytes(CurrentPath);
                    _diskHasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                    _diskText = Encoding.UTF8.GetString(bytes, _diskHasBom ? 3 : 0, bytes.Length - (_diskHasBom ? 3 : 0));
                    _diskDocument = Parse(file.Format, _diskText);
                    _document = Parse(file.Format, _diskText);
                    RawText = _diskText;
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
            IsTextMode = !CanUseFields && _document is not null;
            OnPropertyChanged(nameof(HasOtherLocations));
            OnPropertyChanged(nameof(CurrentPath));
        }
        finally
        {
            _loading = false;
        }
    }

    private void BuildFields()
    {
        _fields = _document is null
            ? []
            : [.. _document.Settings.Select(s => new Ue4ssConfigField(s, _diskDocument?.Get(s.Section, s.Key)))];
        OnPropertyChanged(nameof(CanUseFields));
        OnPropertyChanged(nameof(ShowFields));
        OnPropertyChanged(nameof(ShowNoFieldsNote));
        BuildSections();
    }

    private void BuildSections()
    {
        Sections.Clear();
        var filter = Filter.Trim();
        foreach (var group in _fields
                     .Where(f => filter.Length == 0
                         || f.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || f.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
                     .GroupBy(f => f.Section))
        {
            Sections.Add(new Ue4ssConfigSection(group.Key.Length == 0 ? "Settings" : group.Key, [.. group]));
        }
    }

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
    private void Save()
    {
        if (SelectedFile is not { } file || SelectedLocation is not { } location || _document is null || _diskDocument is null)
        {
            return;
        }

        string text;
        if (IsTextMode)
        {
            text = RawText;
            _document = Parse(file.Format, text);
        }
        else
        {
            var problems = ApplyFields();
            if (problems.Count > 0)
            {
                StatusMessage = $"Not saved: {string.Join("; ", problems)}";
                return;
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
            StatusMessage = "Nothing changed.";
            return;
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
                return;
            }
        }

        foreach (var target in extra)
        {
            try
            {
                var path = Path.Combine(target.ModFolder, file.RelativePath);
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

        var checkedNames = extra.Select(e => e.Name).ToHashSet();
        var textMode = IsTextMode;
        Load();
        // Stays in the text view after a save, the same as Discard changes.
        if (textMode && CanUseFields)
        {
            IsTextMode = true;
        }

        foreach (var also in AlsoSaveTo)
        {
            also.IsChecked = checkedNames.Contains(also.Location.Name);
        }

        StatusMessage = (saved.Count > 0
                ? $"Saved to {string.Join(" and ", saved)} (the old file is in Backups\\UE4SS-Mod-Configs). The mod reads it when the game or server starts, unless it has its own reload key."
                : "")
            + (problemsSaving.Count > 0 ? $"{(saved.Count > 0 ? " " : "")}Couldn't save: {string.Join("; ", problemsSaving)}" : "");
    }

    [RelayCommand]
    private void DiscardChanges()
    {
        var textMode = IsTextMode;
        Load();
        if (textMode && CanUseFields)
        {
            IsTextMode = true;
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
    }

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
        public IReadOnlyList<Ue4ssSetting> Settings =>
            [.. document.Settings.Select(s => new Ue4ssSetting(
                "", s.Key, s.Value, s.Description, null,
                s.Kind == LuaConfigValueKind.Boolean ? Ue4ssSettingKind.Toggle : Ue4ssSettingKind.Text, []))];

        public string? Get(string section, string key) => document.GetValue(key);

        public string? Set(string section, string key, string value) => document.SetValue(key, value);

        public string ToText() => document.ToText();
    }

    private sealed class PlainText(string text) : IConfigText
    {
        public IReadOnlyList<Ue4ssSetting> Settings => [];

        public string? Get(string section, string key) => null;

        public string? Set(string section, string key, string value) => $"{key} isn't in this file";

        public string ToText() => text;
    }
}
