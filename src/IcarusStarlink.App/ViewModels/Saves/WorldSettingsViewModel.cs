using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.PakIO.Worlds;

namespace IcarusStarlink.App.ViewModels;

/// <summary>A saved world in the list: its name and what it is.</summary>
public sealed class WorldItemViewModel(WorldSave save)
{
    public WorldSave Save { get; } = save;

    public string Name => Save.Name;

    /// <summary>
    /// A mission or story world is saved under a 32-character id ("B6E8A7604E8C4D29041549B1AE40E6DE"),
    /// not a name the player typed: it's titled by its prospect ("ELY Story 1") and the id shows faint.
    /// </summary>
    public bool HasGeneratedName => Save.Name.Length == 32 && Save.Name.All(Uri.IsHexDigit) && Save.ProspectType.Length > 0;

    /// <summary>The name shown: the world's own, or its prospect's for a world saved under an id.</summary>
    public string Title => HasGeneratedName ? Save.ProspectType.Replace('_', ' ') : Save.Name;

    /// <summary>The id, faint under a generated-name world's title; null otherwise.</summary>
    public string? RawId => HasGeneratedName ? Save.Name : null;

    /// <summary>"Outpost006_Forest · Extreme · Hardcore".</summary>
    public string Detail => string.Join(" · ", new[] { Save.ProspectType, Save.Difficulty, Save.Hardcore ? "Hardcore" : null, Save.State is "Active" or "" ? null : Save.State }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>A backup of the selected world in the Backups list: when, and what made it.</summary>
public sealed class WorldBackupItemViewModel(WorldBackup backup)
{
    public WorldBackup Backup { get; } = backup;

    /// <summary>"28 Oct 2026, 14:03:12".</summary>
    public string When => Backup.TakenAt.ToString("d MMM yyyy, HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>"before world settings save" / "before a restore" / "before Find the broken mod".</summary>
    public string What => Backup.KindText;

    /// <summary>Faint note for a zip of every world: only this one is put back.</summary>
    public string? Note => Backup.IsZip ? "Zip of every world in the slot: only this world is put back." : null;

    public string FileName => Path.GetFileName(Backup.Path);
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

            // The box shows the value Save will write: letters, or a number past the range when the
            // value was already at its end, stayed typed in the box while the old value was saved.
            OnPropertyChanged();
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
    private readonly IDialogService? _dialogService;
    private readonly Func<string?>? _backupOwner;
    private readonly Func<int>? _backupsKept;
    private IReadOnlyList<WorldSettingDefinition>? _definitions;

    /// <summary>Set while the list itself changes the selection (a reload, or putting a refused switch back): no question then.</summary>
    private bool _settingSelection;

    /// <param name="prospectsFolder">The Prospects folder of the slot or server (null: none chosen).</param>
    /// <param name="blockedReason">Why saving can't happen right now ("Close Icarus first…"), or null.</param>
    /// <param name="activityLog">Save, refusal and failure go to the app-wide activity feed too (and so pop up), not only this editor's status line.</param>
    /// <param name="dialogService">Asks before picking another world drops unsaved settings; null switches without asking.</param>
    /// <param name="backupOwner">
    /// Whose worlds these are, for the backup names (the slot's Steam id, or WorldBackups.ServerOwner):
    /// two slots with a world of the same name never mix backups. Null: no Backups list (Restore isn't offered).
    /// </param>
    /// <param name="backupsKept">Settings › Backups to keep › Save backups (each slot): each world's backups are pruned to it.</param>
    public WorldSettingsViewModel(Func<string?> prospectsFolder, Func<string?> dataFolder, Func<string?> blockedReason, string backupFolder, IActivityLog activityLog,
        IDialogService? dialogService = null, Func<string?>? backupOwner = null, Func<int>? backupsKept = null)
    {
        _backupOwner = backupOwner;
        _backupsKept = backupsKept;
        _activityLog = activityLog;
        _dialogService = dialogService;
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

    /// <summary>The selected world's backups, newest first.</summary>
    public ObservableCollection<WorldBackupItemViewModel> Backups { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreBackupCommand))]
    private WorldBackupItemViewModel? _selectedBackup;

    /// <summary>The Backups list is shown (the page said whose worlds these are).</summary>
    public bool ShowsBackups => _backupOwner is not null;

    public bool HasBackups => Backups.Count > 0;

    public bool HasNoBackups => Backups.Count == 0;

    private int BackupsKept => BackupLimits.Clamp(_backupsKept?.Invoke() ?? BackupLimits.DefaultSaveBackupsKept);

    public string EmptyText => _prospectsFolder() is null
        ? "Pick a save slot (or a server) to see its worlds."
        : "No saved worlds here yet.";

    /// <summary>Bumped by every reload, so an older one still parsing in the background doesn't overwrite a newer list.</summary>
    private int _reloadGeneration;

    /// <summary>Reads the worlds again (the slot or server changed, or a world was saved elsewhere).</summary>
    public void Reload()
    {
        var folder = _prospectsFolder();
        Show(++_reloadGeneration, folder is null ? [] : WorldSave.LoadFolder(folder));
    }

    /// <summary>
    /// Same as Reload, but parses the world files on a background thread: a slot with many big
    /// worlds took seconds on the UI thread. The list fills in once they're read.
    /// </summary>
    public async Task ReloadAsync()
    {
        var generation = ++_reloadGeneration;
        var folder = _prospectsFolder();
        IReadOnlyList<WorldSave> saves;
        try
        {
            saves = folder is null ? [] : await Task.Run(() => WorldSave.LoadFolder(folder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            saves = [];
            StatusMessage = $"Couldn't read the worlds: {ex.Message}";
        }

        Show(generation, saves);
    }

    private void Show(int generation, IReadOnlyList<WorldSave> saves)
    {
        if (generation != _reloadGeneration)
        {
            return;
        }

        // A reload replaces the list (whoever asked for it already asked about unsaved settings).
        var keep = SelectedWorld?.Save.Path;
        _settingSelection = true;
        try
        {
            Worlds.Clear();
            foreach (var save in saves)
            {
                Worlds.Add(new WorldItemViewModel(save));
            }

            SelectedWorld = Worlds.FirstOrDefault(w => string.Equals(w.Save.Path, keep, StringComparison.OrdinalIgnoreCase)) ?? Worlds.FirstOrDefault();
        }
        finally
        {
            _settingSelection = false;
        }

        LoadRows();
        OnPropertyChanged(nameof(HasWorlds));
        OnPropertyChanged(nameof(HasNoWorlds));
        OnPropertyChanged(nameof(EmptyText));
    }

    /// <summary>
    /// Forgets the cached setting list (after Refresh game data). The rows are built again from it
    /// now only when none holds an unsaved change; otherwise on the next world switch, reload or
    /// Revert (Refresh game data used to throw the edits away).
    /// </summary>
    public void InvalidateDefinitions()
    {
        _definitions = null;
        if (!HasChanges)
        {
            LoadRows();
        }
    }

    /// <summary>Picking another world asks first when this one has unsaved settings (they were dropped without a word).</summary>
    partial void OnSelectedWorldChanged(WorldItemViewModel? oldValue, WorldItemViewModel? newValue)
    {
        if (_settingSelection)
        {
            return;
        }

        if (HasChanges && oldValue is not null && _dialogService is { } dialogs
            && !dialogs.Confirm($"Switch worlds? Your unsaved settings for '{oldValue.Title}' will be lost.", "Unsaved world settings", ThemedConfirmSeverity.Warning))
        {
            // Put back after the list finishes its own selection change, or it keeps showing the new one.
            void Revert()
            {
                _settingSelection = true;
                SelectedWorld = oldValue;
                _settingSelection = false;
            }

            if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
            {
                dispatcher.BeginInvoke(Revert);
            }
            else
            {
                Revert();
            }

            return;
        }

        LoadRows();
    }

    private void LoadRows()
    {
        Rows.Clear();
        StatusMessage = null;
        LoadBackups();
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

    /// <summary>
    /// Save greys out while the blocked reason holds (the Server page, which calls
    /// RefreshSaveBlocked whenever its server starts, stops or gets busy). Off for the Saves
    /// page: nothing tells it when Icarus closes, so its Save keeps refusing after the click.
    /// </summary>
    public bool GreyOutWhenBlocked { get; init; }

    /// <summary>Why Save is greyed out right now (its tooltip), or null.</summary>
    public string? SaveBlockedReason => GreyOutWhenBlocked ? _blockedReason() : null;

    /// <summary>The owner's state changed: Save and its tooltip look at the blocked reason again.</summary>
    public void RefreshSaveBlocked()
    {
        OnPropertyChanged(nameof(SaveBlockedReason));
        SaveCommand.NotifyCanExecuteChanged();
    }

    private bool CanSave() => SaveBlockedReason is null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (SelectedWorld is not { } world || !HasChanges)
        {
            return;
        }

        if (_blockedReason() is { } reason)
        {
            StatusMessage = reason;
            _activityLog.Log($"World settings for '{world.Title}' not saved: {reason}", ActivityEntryKind.Warning);
            return;
        }

        try
        {
            var changes = Rows.Where(r => r.IsChanged).ToDictionary(r => r.Definition.Name, r => r.Value, StringComparer.OrdinalIgnoreCase);
            var backup = world.Save.Save(changes, _backupFolder, _backupOwner?.Invoke() ?? "world", BackupsKept);
            LoadRows();
            StatusMessage = $"Saved {changes.Count} setting(s) for '{world.Title}'. The world was backed up first ({Path.GetFileName(backup)}).";
            _activityLog.Log(StatusMessage, ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't save the world: {ex.Message}";
            _activityLog.Log($"Couldn't save the world '{world.Title}': {ex.Message}", ActivityEntryKind.Warning);
        }
    }

    /// <summary>Back to what the save holds.</summary>
    [RelayCommand]
    private void Revert() => LoadRows();

    /// <summary>Reads the selected world's backups again (its own copies, and Find the broken mod zips holding it).</summary>
    private void LoadBackups()
    {
        var keep = SelectedBackup?.Backup.Path;
        Backups.Clear();
        if (SelectedWorld is { } world && _backupOwner?.Invoke() is { } owner)
        {
            try
            {
                foreach (var backup in WorldBackups.List(_backupFolder, owner, Path.GetFileName(world.Save.Path)))
                {
                    Backups.Add(new WorldBackupItemViewModel(backup));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A backup folder that can't be read only means no list; the settings still work.
            }
        }

        SelectedBackup = Backups.FirstOrDefault(b => string.Equals(b.Backup.Path, keep, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(HasBackups));
        OnPropertyChanged(nameof(HasNoBackups));
    }

    private bool CanRestoreBackup() => SelectedBackup is not null && SelectedWorld is not null;

    /// <summary>
    /// Puts the selected world back to the chosen backup: refused while the game (or server)
    /// runs, asked first, the world as it is now backed up, then only this world's file written
    /// (through a temp file). From a zip of every world, only this one comes back.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRestoreBackup))]
    private void RestoreBackup()
    {
        if (SelectedWorld is not { } world || SelectedBackup is not { } item || _backupOwner?.Invoke() is not { } owner)
        {
            return;
        }

        if (RefusedToRestore(world))
        {
            return;
        }

        var question = $"Put '{world.Title}' back to how it was {item.When} ({item.What})? The world as it is now is backed up first, so this can be undone from this list."
            + (HasChanges ? " Your unsaved settings for it will be lost." : "");
        if (_dialogService is { } dialogs && !dialogs.Confirm(question, "Restore world", ThemedConfirmSeverity.Warning))
        {
            return;
        }

        // The game (or server) may have started while the question was open.
        if (RefusedToRestore(world))
        {
            return;
        }

        try
        {
            WorldBackups.Restore(item.Backup, world.Save.Path, _backupFolder, owner, BackupsKept);
            Reload();
            StatusMessage = $"Restored '{world.Title}' to how it was {item.When}. The world as it was just now is in the backups list.";
            _activityLog.Log(StatusMessage, ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            LoadBackups();
            StatusMessage = $"Couldn't restore the world: {ex.Message}";
            _activityLog.Log($"Couldn't restore the world '{world.Title}': {ex.Message}", ActivityEntryKind.Warning);
        }
    }

    private bool RefusedToRestore(WorldItemViewModel world)
    {
        if (_blockedReason() is not { } reason)
        {
            return false;
        }

        StatusMessage = reason;
        _activityLog.Log($"World '{world.Title}' not restored: {reason}", ActivityEntryKind.Warning);
        return true;
    }
}
