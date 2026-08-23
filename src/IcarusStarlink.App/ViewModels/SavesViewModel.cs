using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Services;
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
    private readonly SaveGameNames _gameNames;
    private readonly Utilities.DebounceTimer _talentSearchDebounceTimer;

    private JsonObject? _profile;
    private List<JsonObject> _characterNodes = [];
    private List<int>? _binaryFlagIds;

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

    public bool HasUnsavedChanges =>
        Characters.Any(c => c.IsDirty)
        || Currencies.Any(c => c.IsDirty)
        || AccountFlags.Any(f => f.IsDirty)
        || BinaryFlags.Any(f => f.IsDirty)
        || WorkshopTalents.Any(t => t.IsDirty);

    public SavesViewModel(ISaveRepository repository, IActivityLog activityLog, SaveGameNames gameNames)
    {
        _repository = repository;
        _activityLog = activityLog;
        _gameNames = gameNames;
        _talentSearchDebounceTimer = new Utilities.DebounceTimer(TimeSpan.FromMilliseconds(250), RefreshTalentFilter);
        RefreshSlots();
    }

    // --- Flags (character flags on the selected character; account flags on the profile;
    //     binary flags in the slot's own flags_<SteamID>.dat — a third store the game keeps
    //     OUTSIDE the JSON, holding account-wide character-flag unlocks) ---

    public ObservableCollection<SaveFlagViewModel> AccountFlags { get; } = [];

    public ObservableCollection<SaveFlagViewModel> BinaryFlags { get; } = [];

    public ObservableCollection<SaveFlagViewModel> FilteredCharacterFlags { get; } = [];

    public ObservableCollection<SaveFlagViewModel> FilteredAccountFlags { get; } = [];

    public ObservableCollection<SaveFlagViewModel> FilteredBinaryFlags { get; } = [];

    /// <summary>The binary section shows only when the game itself has created the file — this editor never invents one for a slot that has none.</summary>
    public bool HasBinaryFlags => BinaryFlags.Count > 0;

    [ObservableProperty]
    private string _flagSearchText = "";

    partial void OnFlagSearchTextChanged(string value) => RefreshFlagFilter();

    private void RefreshFlagFilter()
    {
        // 45 + 100 rows — a straight rebuild per keystroke is cheaper than filter plumbing.
        FilteredCharacterFlags.Clear();
        foreach (var flag in SelectedCharacter?.Flags ?? [])
        {
            if (Matches(flag.Name))
            {
                FilteredCharacterFlags.Add(flag);
            }
        }

        FilteredAccountFlags.Clear();
        foreach (var flag in AccountFlags)
        {
            if (Matches(flag.Name))
            {
                FilteredAccountFlags.Add(flag);
            }
        }

        FilteredBinaryFlags.Clear();
        foreach (var flag in BinaryFlags)
        {
            if (Matches(flag.Name))
            {
                FilteredBinaryFlags.Add(flag);
            }
        }

        bool Matches(string name) => string.IsNullOrWhiteSpace(FlagSearchText) || name.Contains(FlagSearchText.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    // --- Talents (character talents on the selected character; workshop research on the profile) ---

    public ObservableCollection<SaveTalentViewModel> WorkshopTalents { get; } = [];

    public ObservableCollection<SaveTalentViewModel> FilteredTalents { get; } = [];

    [ObservableProperty]
    private string _talentSearchText = "";

    /// <summary>Off = only what the character has; on = every talent the game defines, rank 0 included — turning one up grants it.</summary>
    [ObservableProperty]
    private bool _showUnlearnedTalents;

    partial void OnTalentSearchTextChanged(string value) => _talentSearchDebounceTimer.Restart();

    partial void OnShowUnlearnedTalentsChanged(bool value) => RefreshTalentFilter();

    private void RefreshTalentFilter()
    {
        FilteredTalents.Clear();
        var search = TalentSearchText.Trim();
        foreach (var talent in SelectedCharacter?.Talents ?? [])
        {
            // "Only what this character has" includes bDefaultUnlocked talents — the game grants
            // those from the start without writing them into the save, so rank alone under-reports.
            if (!ShowUnlearnedTalents && !talent.IsUnlockedInGame && !talent.IsDirty)
            {
                continue;
            }

            if (search.Length == 0
                || talent.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || talent.RowName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || talent.Tree.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                FilteredTalents.Add(talent);
            }
        }
    }

    partial void OnSelectedCharacterChanged(SaveCharacterViewModel? value)
    {
        RefreshFlagFilter();
        RefreshTalentFilter();
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
        BinaryFlags.Clear();
        _profile = null;
        _characterNodes = [];
        _binaryFlagIds = null;
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
                Characters.Add(new SaveCharacterViewModel(node, _gameNames, NotifyDirtyChanged));
            }

            if (_profile["MetaResources"] is JsonArray resources)
            {
                foreach (var resource in resources.OfType<JsonObject>())
                {
                    var key = resource["MetaRow"]?.GetValue<string>() ?? "?";
                    Currencies.Add(new SaveCurrencyViewModel(resource, CurrencyLabels.GetValueOrDefault(key, key), NotifyDirtyChanged));
                }
            }

            BuildAccountFlags();
            BuildBinaryFlags();
            BuildWorkshopTalents();
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

    private void BuildAccountFlags()
    {
        AccountFlags.Clear();
        var unlocked = _profile?["UnlockedFlags"] is JsonArray array
            ? array.Select(n => n?.GetValue<int>() ?? -1).Where(i => i >= 0).ToHashSet()
            : [];
        var count = Math.Max(_gameNames.AccountFlagNames.Count, unlocked.Count > 0 ? unlocked.Max() + 1 : 0);
        for (var id = 0; id < count; id++)
        {
            AccountFlags.Add(new SaveFlagViewModel(id, _gameNames.AccountFlagName(id), unlocked.Contains(id), NotifyDirtyChanged));
        }
    }

    /// <summary>
    /// The slot's flags_&lt;SteamID&gt;.dat holds account-wide unlocks as CHARACTER-flag row indexes
    /// (confirmed against the real file: every ID mapped to a real D_CharacterFlags name —
    /// Talent_RepairBench, Mission_Olympus_Unlock, Unlocked_Bait…). Without reading it, unlocks
    /// the player genuinely has would show as locked — the exact bug report that led here.
    /// </summary>
    private void BuildBinaryFlags()
    {
        BinaryFlags.Clear();
        _binaryFlagIds = null;
        if (SelectedSlot is null)
        {
            OnPropertyChanged(nameof(HasBinaryFlags));
            return;
        }

        try
        {
            _binaryFlagIds = _repository.LoadBinaryFlags(SelectedSlot.SteamId) is { } ids ? [.. ids] : null;
        }
        catch (FormatException)
        {
            // An unreadable flags file just means the section stays hidden — the JSON-side editing
            // still works, and hiding beats showing toggles that couldn't be written back safely.
        }

        if (_binaryFlagIds is { } unlocked)
        {
            var set = unlocked.ToHashSet();
            var count = Math.Max(_gameNames.CharacterFlagNames.Count, set.Count > 0 ? set.Max() + 1 : 0);
            for (var id = 0; id < count; id++)
            {
                BinaryFlags.Add(new SaveFlagViewModel(id, _gameNames.CharacterFlagName(id), set.Contains(id), NotifyDirtyChanged));
            }
        }

        OnPropertyChanged(nameof(HasBinaryFlags));
    }

    private void BuildWorkshopTalents()
    {
        WorkshopTalents.Clear();
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (_profile?["Talents"] is JsonArray talentArray)
        {
            foreach (var entry in talentArray.OfType<JsonObject>())
            {
                if (entry["RowName"]?.GetValue<string>() is { } rowName)
                {
                    ranks[rowName] = entry["Rank"]?.GetValue<int>() ?? 0;
                }
            }
        }

        // The profile's own list first (its order), then any Workshop_* talent the game defines
        // that isn't researched yet — rank one up to research it.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rowName, rank) in ranks)
        {
            WorkshopTalents.Add(new SaveTalentViewModel(rowName, SaveCharacterViewModel.DisplayInfoFor(_gameNames, rowName), rank, NotifyDirtyChanged));
            seen.Add(rowName);
        }

        foreach (var (rowName, info) in _gameNames.Talents)
        {
            if (rowName.StartsWith("Workshop_", StringComparison.OrdinalIgnoreCase) && !seen.Contains(rowName))
            {
                WorkshopTalents.Add(new SaveTalentViewModel(rowName, new TalentDisplayInfo(info.DisplayName, info.Description, info.Tree, info.MaxRank, info.IsDefaultUnlocked), 0, NotifyDirtyChanged));
            }
        }
    }

    /// <summary>Writes AccountFlags/WorkshopTalents back into the profile node — the profile-level counterpart of SaveCharacterViewModel.ApplyToNode, same minimal-diff rules.</summary>
    private void ApplyProfileEdits()
    {
        if (_profile is null)
        {
            return;
        }

        var originallyUnlocked = _profile["UnlockedFlags"] is JsonArray array
            ? array.Select(n => n?.GetValue<int>() ?? -1).Where(i => i >= 0).ToList()
            : [];
        var nowUnlocked = AccountFlags.Where(f => f.IsUnlocked).Select(f => f.Id).ToHashSet();
        var flagArray = new JsonArray();
        foreach (var id in originallyUnlocked.Where(nowUnlocked.Contains))
        {
            flagArray.Add(id);
        }

        foreach (var id in nowUnlocked.Except(originallyUnlocked).OrderBy(i => i))
        {
            flagArray.Add(id);
        }

        _profile["UnlockedFlags"] = flagArray;

        var byRow = WorkshopTalents.ToDictionary(t => t.RowName, t => t.Rank, StringComparer.OrdinalIgnoreCase);
        var talentArrayNew = new JsonArray();
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_profile["Talents"] is JsonArray existing)
        {
            foreach (var entry in existing.OfType<JsonObject>())
            {
                if (entry["RowName"]?.GetValue<string>() is { } rowName && byRow.GetValueOrDefault(rowName) > 0)
                {
                    talentArrayNew.Add(new JsonObject { ["RowName"] = rowName, ["Rank"] = byRow[rowName] });
                    written.Add(rowName);
                }
            }
        }

        foreach (var talent in WorkshopTalents)
        {
            if (talent.Rank > 0 && !written.Contains(talent.RowName))
            {
                talentArrayNew.Add(new JsonObject { ["RowName"] = talent.RowName, ["Rank"] = talent.Rank });
            }
        }

        _profile["Talents"] = talentArrayNew;
    }

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

            ApplyProfileEdits();

            // Characters first, then profile: SaveProfile's own backup then already contains the
            // just-written Characters.json, so the LAST backup zip of the pair is a complete
            // post-characters/pre-profile snapshot rather than two half-states.
            _repository.SaveCharacters(SelectedSlot.SteamId, _characterNodes);
            _repository.SaveProfile(SelectedSlot.SteamId, _profile!);

            // The binary flags file is written only when actually edited — same minimal-diff
            // philosophy as the JSON arrays: the file's own ordering is kept for flags that stay
            // set, newly-set ones append.
            if (_binaryFlagIds is { } original && BinaryFlags.Any(f => f.IsDirty))
            {
                var nowSet = BinaryFlags.Where(f => f.IsUnlocked).Select(f => f.Id).ToHashSet();
                var newIds = original.Where(nowSet.Contains).ToList();
                newIds.AddRange(nowSet.Except(original).OrderBy(i => i));
                _repository.SaveBinaryFlags(SelectedSlot.SteamId, newIds);
                _binaryFlagIds = newIds;
            }

            foreach (var character in Characters)
            {
                character.MarkClean();
            }

            foreach (var currency in Currencies)
            {
                currency.MarkClean();
            }

            foreach (var flag in AccountFlags)
            {
                flag.MarkClean();
            }

            foreach (var flag in BinaryFlags)
            {
                flag.MarkClean();
            }

            foreach (var talent in WorkshopTalents)
            {
                talent.MarkClean();
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
