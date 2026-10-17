using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.PakIO.Worlds;

namespace IcarusStarlink.App.ViewModels;

/// <summary>A saved world in the list: its name and what it is.</summary>
public sealed class WorldItemViewModel(WorldSave save)
{
    public WorldSave Save { get; } = save;

    public string Name => Save.Name;

    /// <summary>"Outpost006_Forest · Extreme · Hardcore".</summary>
    public string Detail => string.Join(" · ", new[] { Save.ProspectType, Save.Difficulty, Save.Hardcore ? "Hardcore" : null, Save.State is "Active" or "" ? null : Save.State }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>One custom setting of the selected world, editable when the game allows it after creation.</summary>
public sealed partial class WorldSettingRowViewModel : ObservableObject
{
    private readonly int _saved;

    public WorldSettingRowViewModel(WorldSettingDefinition definition, int? savedValue)
    {
        Definition = definition;
        IsListedInSave = savedValue.HasValue;
        _saved = savedValue ?? definition.DefaultValue;
        _value = _saved;
    }

    public WorldSettingDefinition Definition { get; }

    public string Label => Definition.DisplayName;

    public string Description => Definition.Description;

    /// <summary>The save doesn't list it yet (made before the setting existed): the game uses its default.</summary>
    public bool IsListedInSave { get; }

    public bool IsEditable => Definition.ChangeableAnytime;

    public string Note => !IsEditable ? "Only set when a world is created." : !IsListedInSave ? "Not in this save yet: the game's default applies." : "";

    public bool HasNote => Note.Length > 0;

    public bool IsBool => Definition.Type == "Bool";

    public bool IsDropDown => Definition.Type == "DropDown" && Definition.Options.Count > 0;

    public bool IsNumber => !IsBool && !IsDropDown;

    public IReadOnlyList<string> Options => Definition.Options;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BoolValue), nameof(ValueText), nameof(IsChanged))]
    private int _value;

    public bool BoolValue
    {
        get => Value != 0;
        set => Value = value ? 1 : 0;
    }

    /// <summary>A number setting, kept within the game's own range.</summary>
    public string ValueText
    {
        get => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        set
        {
            if (int.TryParse(value, out var n))
            {
                Value = Definition.MaxValue > Definition.MinValue ? Math.Clamp(n, Definition.MinValue, Definition.MaxValue) : n;
            }
        }
    }

    public bool IsChanged => Value != _saved;
}

/// <summary>
/// "World settings" on the Saves page and the Server page: each saved world, and its custom
/// settings (the game's own D_CustomGameStats list: Arachnophobia, Mission Structure Cleanup…)
/// with the ones the game lets change after creation editable. Save writes only the save's
/// CustomSettings list (WorldSave), after a backup, and only while the game (or the server)
/// isn't running, since it rewrites world saves when it exits. Difficulty and Hardcore are shown,
/// not edited: the game itself never changes them after a world is made.
/// </summary>
public sealed partial class WorldSettingsViewModel : ObservableObject
{
    private readonly Func<string?> _prospectsFolder;
    private readonly Func<string?> _dataFolder;
    private readonly Func<string?> _blockedReason;
    private readonly string _backupFolder;
    private readonly IActivityLog _activityLog;
    private IReadOnlyList<WorldSettingDefinition>? _definitions;

    /// <param name="prospectsFolder">The Prospects folder of the slot or server (null: none chosen).</param>
    /// <param name="blockedReason">Why saving can't happen right now ("Close Icarus first…"), or null.</param>
    /// <param name="activityLog">Save, refusal and failure go to the app-wide activity feed too (and so pop up), not only this editor's status line.</param>
    public WorldSettingsViewModel(Func<string?> prospectsFolder, Func<string?> dataFolder, Func<string?> blockedReason, string backupFolder, IActivityLog activityLog)
    {
        _activityLog = activityLog;
        _prospectsFolder = prospectsFolder;
        _dataFolder = dataFolder;
        _blockedReason = blockedReason;
        _backupFolder = backupFolder;
    }

    public ObservableCollection<WorldItemViewModel> Worlds { get; } = [];

    public ObservableCollection<WorldSettingRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    private WorldItemViewModel? _selectedWorld;

    [ObservableProperty]
    private string? _statusMessage;

    public bool HasWorlds => Worlds.Count > 0;

    public bool HasNoWorlds => Worlds.Count == 0;

    public bool HasChanges => Rows.Any(r => r.IsChanged);

    public string EmptyText => _prospectsFolder() is null
        ? "Pick a save slot (or a server) to see its worlds."
        : "No saved worlds here yet.";

    /// <summary>Reads the worlds again (the slot or server changed, or a world was saved elsewhere).</summary>
    public void Reload()
    {
        var keep = SelectedWorld?.Save.Path;
        Worlds.Clear();
        if (_prospectsFolder() is { } folder)
        {
            foreach (var save in WorldSave.LoadFolder(folder))
            {
                Worlds.Add(new WorldItemViewModel(save));
            }
        }

        SelectedWorld = Worlds.FirstOrDefault(w => string.Equals(w.Save.Path, keep, StringComparison.OrdinalIgnoreCase)) ?? Worlds.FirstOrDefault();
        OnPropertyChanged(nameof(HasWorlds));
        OnPropertyChanged(nameof(HasNoWorlds));
        OnPropertyChanged(nameof(EmptyText));
        if (SelectedWorld is null)
        {
            Rows.Clear();
        }
    }

    /// <summary>Forgets the cached setting list (after Refresh game data).</summary>
    public void InvalidateDefinitions()
    {
        _definitions = null;
        LoadRows();
    }

    partial void OnSelectedWorldChanged(WorldItemViewModel? value) => LoadRows();

    private void LoadRows()
    {
        Rows.Clear();
        StatusMessage = null;
        if (SelectedWorld is not { } world)
        {
            return;
        }

        _definitions ??= _dataFolder() is { } data ? WorldSettingsCatalog.Load(data) : [];
        if (_definitions.Count == 0)
        {
            StatusMessage = "No game data yet: run Refresh game data in Settings to see the world settings.";
        }

        foreach (var definition in _definitions)
        {
            var row = new WorldSettingRowViewModel(definition, world.Save.Settings.TryGetValue(definition.Name, out var v) ? v : null);
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(WorldSettingRowViewModel.IsChanged))
                {
                    OnPropertyChanged(nameof(HasChanges));
                }
            };
            Rows.Add(row);
        }

        OnPropertyChanged(nameof(HasChanges));
    }

    [RelayCommand]
    private void Save()
    {
        if (SelectedWorld is not { } world || !HasChanges)
        {
            return;
        }

        if (_blockedReason() is { } reason)
        {
            StatusMessage = reason;
            _activityLog.Log($"World settings for '{world.Name}' not saved: {reason}", ActivityEntryKind.Warning);
            return;
        }

        try
        {
            var changes = Rows.Where(r => r.IsChanged).ToDictionary(r => r.Definition.Name, r => r.Value, StringComparer.OrdinalIgnoreCase);
            var backup = world.Save.Save(changes, _backupFolder);
            LoadRows();
            StatusMessage = $"Saved {changes.Count} setting(s) for '{world.Name}'. The world was backed up first ({Path.GetFileName(backup)}).";
            _activityLog.Log(StatusMessage, ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't save the world: {ex.Message}";
            _activityLog.Log($"Couldn't save the world '{world.Name}': {ex.Message}", ActivityEntryKind.Warning);
        }
    }

    /// <summary>Back to what the save holds.</summary>
    [RelayCommand]
    private void Revert() => LoadRows();
}
