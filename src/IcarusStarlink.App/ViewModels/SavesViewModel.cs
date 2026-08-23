using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Saves;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// The Saves page (the spec's save editor, S1: slots + backup/restore + Overview cards +
/// character/currency editing — layout modeled on Icarus Workshop's own save editor screens:
/// character cards on an Overview beside an account snapshot, click a card to edit).
///
/// Safety posture, stricter than any other page: nothing writes without a full slot backup first
/// (the repository enforces it), Restore writes a pre_restore zip (ditto), saving is refused
/// outright while Icarus is running (the game holds these files and overwrites them on exit —
/// an edit made mid-session would be silently lost or, worse, half-read), and every destructive
/// action confirms first.
/// </summary>
public sealed partial class SavesViewModel : ObservableObject
{
    /// <summary>Real MetaRow keys → the names the game's own UI uses (per the spec's own list: "Ren, Exotics, Red, Biomass, Uranium, Licence, Respec"). An unrecognized key still shows, under its raw name — same preserve-everything philosophy as the repository.</summary>
    private static readonly Dictionary<string, string> CurrencyLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Credits"] = "Ren",
        ["Exotic1"] = "Exotics",
        ["Exotic_Red"] = "Red Exotics",
        ["Exotic_stabilized"] = "Stabilized Exotics",
        ["Biomass"] = "Biomass",
        ["Exotic_Uranium"] = "Uranium",
        ["Licence"] = "Licences",
        ["Refund"] = "Respec Points",
    };

    private readonly ISaveRepository _repository;
    private readonly IActivityLog _activityLog;

    private JsonObject? _profile;
    private List<JsonObject> _characterNodes = [];

    public string Title => "Saves";

    public ObservableCollection<SaveSlot> Slots { get; } = [];

    [ObservableProperty]
    private SaveSlot? _selectedSlot;

    public ObservableCollection<SaveCharacterViewModel> Characters { get; } = [];

    [ObservableProperty]
    private SaveCharacterViewModel? _selectedCharacter;

    public ObservableCollection<SaveCurrencyViewModel> Currencies { get; } = [];

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _lastBackupDisplay;

    public ObservableCollection<SaveBackupInfo> Backups { get; } = [];

    [ObservableProperty]
    private SaveBackupInfo? _selectedBackup;

    /// <summary>0 = Overview, 1 = Characters — set programmatically when an Overview card is clicked, per the spec's "Click a card to edit that character".</summary>
    [ObservableProperty]
    private int _selectedTabIndex;

    public bool HasSlots => Slots.Count > 0;

    public bool HasUnsavedChanges => Characters.Any(c => c.IsDirty) || Currencies.Any(c => c.IsDirty);

    public SavesViewModel(ISaveRepository repository, IActivityLog activityLog)
    {
        _repository = repository;
        _activityLog = activityLog;
        RefreshSlots();
    }

    [RelayCommand]
    private void RefreshSlots()
    {
        var previous = SelectedSlot?.SteamId;
        Slots.Clear();
        try
        {
            foreach (var slot in _repository.ListSlots())
            {
                Slots.Add(slot);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't read the game's PlayerData folder: {ex.Message}";
        }

        OnPropertyChanged(nameof(HasSlots));
        SelectedSlot = Slots.FirstOrDefault(s => s.SteamId == previous) ?? Slots.FirstOrDefault();
        StatusMessage = Slots.Count == 0
            ? "No player saves found — Icarus keeps them under %LocalAppData%\\Icarus\\Saved\\PlayerData once you've run the game."
            : null;
    }

    partial void OnSelectedSlotChanged(SaveSlot? value) => LoadSlot();

    private void LoadSlot()
    {
        Characters.Clear();
        Currencies.Clear();
        Backups.Clear();
        _profile = null;
        _characterNodes = [];
        SelectedCharacter = null;

        if (SelectedSlot is null)
        {
            LastBackupDisplay = null;
            return;
        }

        try
        {
            _profile = _repository.LoadProfile(SelectedSlot.SteamId);
            _characterNodes = [.. _repository.LoadCharacters(SelectedSlot.SteamId)];

            foreach (var node in _characterNodes)
            {
                Characters.Add(new SaveCharacterViewModel(node, NotifyDirtyChanged));
            }

            if (_profile["MetaResources"] is JsonArray resources)
            {
                foreach (var resource in resources.OfType<JsonObject>())
                {
                    var key = resource["MetaRow"]?.GetValue<string>() ?? "?";
                    Currencies.Add(new SaveCurrencyViewModel(resource, CurrencyLabels.GetValueOrDefault(key, key), NotifyDirtyChanged));
                }
            }

            SelectedCharacter = Characters.FirstOrDefault();
            RefreshBackupsList();
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't read this save: {ex.Message}";
        }

        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private void NotifyDirtyChanged() => OnPropertyChanged(nameof(HasUnsavedChanges));

    private void RefreshBackupsList()
    {
        Backups.Clear();
        if (SelectedSlot is null)
        {
            return;
        }

        foreach (var backup in _repository.ListBackups(SelectedSlot.SteamId))
        {
            Backups.Add(backup);
        }

        SelectedBackup = Backups.FirstOrDefault();
        LastBackupDisplay = Backups.FirstOrDefault() is { } newest
            ? $"Last backup: {newest.TakenAtUtc.LocalDateTime:g}"
            : "No backups yet.";
    }

    /// <summary>The spec's "Click a card to edit that character" — Overview card → Characters tab with that character selected.</summary>
    [RelayCommand]
    private void EditCharacter(SaveCharacterViewModel? character)
    {
        if (character is null)
        {
            return;
        }

        SelectedCharacter = character;
        SelectedTabIndex = 1;
    }

    [RelayCommand]
    private void BackupNow()
    {
        if (SelectedSlot is null)
        {
            return;
        }

        try
        {
            var zipPath = _repository.BackupSlot(SelectedSlot.SteamId);
            RefreshBackupsList();
            StatusMessage = $"Backed up to '{Path.GetFileName(zipPath)}'.";
            _activityLog.Log($"Backed up save slot {SelectedSlot.Display}.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Backup failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RestoreBackup()
    {
        if (SelectedSlot is null || SelectedBackup is null)
        {
            return;
        }

        if (IsGameRunning())
        {
            StatusMessage = "Icarus is running — close it before restoring, or the game will overwrite the restored files when it exits.";
            return;
        }

        var confirm = MessageBox.Show(
            $"Replace this save slot's current files with the backup from {SelectedBackup.TakenAtUtc.LocalDateTime:g}?\n\n"
            + "A pre_restore safety zip of the slot as it is right now is written first, so this is itself undoable.",
            "Restore save backup", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _repository.RestoreSlot(SelectedSlot.SteamId, SelectedBackup.FilePath);
            LoadSlot();
            StatusMessage = "Restored. The replaced state was saved as a pre_restore zip.";
            _activityLog.Log($"Restored save slot {SelectedSlot.Display} from backup.", ActivityEntryKind.Warning);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Restore failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void SaveChanges()
    {
        if (SelectedSlot is null)
        {
            return;
        }

        if (!HasUnsavedChanges)
        {
            StatusMessage = "Nothing changed.";
            return;
        }

        if (IsGameRunning())
        {
            StatusMessage = "Icarus is running — close it before saving, or the game will overwrite your edits when it exits.";
            return;
        }

        var confirm = MessageBox.Show(
            "Write your edits into the player save?\n\nA full backup of the slot is taken automatically first (Restore can undo this).",
            "Save player data", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            foreach (var character in Characters)
            {
                character.ApplyToNode();
            }

            foreach (var currency in Currencies)
            {
                currency.ApplyToNode();
            }

            // Characters first, then profile: SaveProfile's own backup then already contains the
            // just-written Characters.json, so the LAST backup zip of the pair is a complete
            // post-characters/pre-profile snapshot rather than two half-states.
            _repository.SaveCharacters(SelectedSlot.SteamId, _characterNodes);
            _repository.SaveProfile(SelectedSlot.SteamId, _profile!);

            foreach (var character in Characters)
            {
                character.MarkClean();
            }

            foreach (var currency in Currencies)
            {
                currency.MarkClean();
            }

            OnPropertyChanged(nameof(HasUnsavedChanges));
            RefreshBackupsList();
            StatusMessage = "Saved. A backup of the previous state was kept.";
            _activityLog.Log($"Edited player save {SelectedSlot.Display}.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenBackupsFolder()
    {
        if (Backups.FirstOrDefault() is not { } any)
        {
            StatusMessage = "No backups yet — take one first.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(Path.GetDirectoryName(any.FilePath)!) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Best-effort open-in-Explorer, same convention as every other open action.
        }
    }

    /// <summary>The game holds these files and rewrites them on exit — any edit made while it runs is lost or half-read, so save/restore refuse rather than race it.</summary>
    private static bool IsGameRunning() => Process.GetProcessesByName("Icarus-Win64-Shipping").Length > 0;
}
