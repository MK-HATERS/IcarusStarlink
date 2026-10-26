using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.Core.Server;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Install;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One UE4SS-settings.ini Starlink can edit: the game's, or a Starlink server's.</summary>
public sealed record Ue4ssConfigTarget(string Name, string IniPath, bool IsServer)
{
    public override string ToString() => Name;
}

/// <summary>A server (or the game) that the changes can also be saved to.</summary>
public sealed partial class Ue4ssConfigAlsoSave(Ue4ssConfigTarget target) : ObservableObject
{
    public Ue4ssConfigTarget Target { get; } = target;

    [ObservableProperty]
    private bool _isChecked;
}

/// <summary>One setting on the tab.</summary>
public sealed partial class Ue4ssConfigField : ObservableObject
{
    /// <param name="originalValue">What the file on disk says, when the setting was read from an edited copy (the mod config editor's text view).</param>
    public Ue4ssConfigField(Ue4ssSetting setting, string? originalValue = null)
    {
        Section = setting.Section;
        Key = setting.Key;
        Description = setting.Description;
        Default = setting.Default;
        Kind = setting.Kind;
        Choices = setting.Choices;
        Minimum = setting.Minimum;
        Maximum = setting.Maximum;
        Step = setting.Step;
        NeedsRestart = setting.NeedsRestart;
        OriginalValue = originalValue ?? setting.Value;
        _value = setting.Value;
    }

    public string Section { get; }
    public string Key { get; }
    public string Description { get; }
    public string? Default { get; }
    public Ue4ssSettingKind Kind { get; }
    public IReadOnlyList<string> Choices { get; }

    /// <summary>A number's range from its comment ("(0-10)", "at least 10"), when it has one.</summary>
    public double? Minimum { get; }
    public double? Maximum { get; }

    /// <summary>Set when the mod asks for a slider ("[slider 1 10 0.5]").</summary>
    public double? Step { get; }

    /// <summary>The comment says it applies after the game (or server) restarts.</summary>
    public bool NeedsRestart { get; }

    /// <summary>What the file says now (changes after a save).</summary>
    public string OriginalValue { get; private set; }

    public bool IsToggle => Kind == Ue4ssSettingKind.Toggle;
    public bool IsChoice => Kind == Ue4ssSettingKind.Choice;
    public bool IsText => Kind == Ue4ssSettingKind.Text;
    public bool IsNumber => Kind == Ue4ssSettingKind.Number;

    /// <summary>A slider and a box beside it, for a number the mod gives a slider range.</summary>
    public bool HasSlider => IsNumber && Step is not null && Minimum is not null && Maximum is not null && Maximum > Minimum;

    /// <summary>A number without a slider: just the box.</summary>
    public bool IsPlainNumber => IsNumber && !HasSlider;
    public double SliderMinimum => Minimum ?? 0;
    public double SliderMaximum => Maximum ?? 1;
    public double SliderStep => Step ?? 1;

    /// <summary>The slider's position: the value as a number (left where it was while the box holds something else).</summary>
    public double NumberValue
    {
        get => Ue4ssSettingsDocument.Number(Value) ?? SliderMinimum;
        set
        {
            // The slider's own steps, written the way a person would ("1.5", "2").
            // Plain decimals: the default ToString wrote "2E-05" for small steps; "+ 0" turns -0 into 0.
            var stepped = Math.Round(Math.Round((value - SliderMinimum) / SliderStep) * SliderStep + SliderMinimum, 6) + 0d;
            if (!double.IsFinite(stepped))
            {
                return;
            }

            var text = stepped.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
            if (text != Value.Trim())
            {
                Value = text;
            }
        }
    }

    /// <summary>"0 to 10", "at least 10", "up to 600": the range under a number's box.</summary>
    public string RangeText => (Minimum, Maximum) switch
    {
        ({ } min, { } max) => $"{Format(min)} to {Format(max)}",
        ({ } min, null) => $"at least {Format(min)}",
        (null, { } max) => $"up to {Format(max)}",
        _ => "",
    };

    public bool HasRangeText => IsNumber && RangeText.Length > 0;

    /// <summary>Why the number in the box won't do: not a number, or outside its range. Empty when it's fine.</summary>
    public string ValueProblem => !IsNumber ? ""
        : Ue4ssSettingsDocument.Number(Value) is not { } n ? "Not a number"
        : n < Minimum || n > Maximum ? $"Outside {RangeText}"
        : "";

    public bool HasValueProblem => ValueProblem.Length > 0;

    private static string Format(double n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The drop-down's pick. A value that isn't one of the choices shows as no pick, and the
    /// drop-down clearing itself never wipes the value.
    /// </summary>
    public string? ChoiceValue
    {
        get => Choices.FirstOrDefault(c => string.Equals(c, Value.Trim(), StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null)
            {
                Value = value;
            }
        }
    }

    public bool HasDescription => Description.Length > 0;
    public string DefaultText => Default is { Length: > 0 } d ? $"Default: {d}" : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged), nameof(IsOn), nameof(IsDefault), nameof(CanResetToDefault),
        nameof(NumberValue), nameof(ValueProblem), nameof(HasValueProblem), nameof(ChoiceValue))]
    private string _value;

    public bool IsChanged => Value.Trim() != OriginalValue.Trim();

    public bool IsDefault => Default is null || string.Equals(Value.Trim(), Default, StringComparison.OrdinalIgnoreCase)
        || (IsToggle && Ue4ssSettingsDocument.IsOn(Value) == Ue4ssSettingsDocument.IsOn(Default));

    /// <summary>A default the file names ("Default: 1") that isn't the current value.</summary>
    public bool CanResetToDefault => Default is { Length: > 0 } && !IsDefault;

    /// <summary>The tick box, written back in the file's own style ("1"/"0" or "true"/"false").</summary>
    public bool IsOn
    {
        get => Ue4ssSettingsDocument.IsOn(Value);
        set => Value = Ue4ssSettingsDocument.ToggleText(value, OriginalValue);
    }

    [RelayCommand]
    private void ResetToDefault()
    {
        if (Default is not null)
        {
            Value = IsToggle ? Ue4ssSettingsDocument.ToggleText(Ue4ssSettingsDocument.IsOn(Default), OriginalValue) : Default;
        }
    }

    public void MarkSaved()
    {
        OriginalValue = Value.Trim();
        OnPropertyChanged(nameof(IsChanged));
    }
}

public sealed class Ue4ssConfigSection(string name, IReadOnlyList<Ue4ssConfigField> fields)
{
    public string Name { get; } = name;
    public IReadOnlyList<Ue4ssConfigField> Fields { get; } = fields;

    /// <summary>The folding section header's count: "1 setting", "12 settings".</summary>
    public string Summary => Fields.Count == 1 ? "1 setting" : $"{Fields.Count} settings";
}

/// <summary>
/// Settings > UE4SS config: edit UE4SS-settings.ini for the game or one of Starlink's servers,
/// and optionally apply the same changes to other targets. Saving only rewrites the values that
/// changed (every comment and line stays), backs the file up first, and for the "also save to"
/// targets applies only the changed settings, so each server keeps its own other settings.
/// UE4SS reads this file when the game or server starts.
/// </summary>
public sealed partial class Ue4ssConfigViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly IDedicatedServerProfileStore _serverStore;
    private readonly string _backupDirectory;

    public Ue4ssConfigViewModel(ISettingsService settingsService, IDedicatedServerProfileStore serverStore, string backupDirectory)
    {
        _settingsService = settingsService;
        _serverStore = serverStore;
        _backupDirectory = backupDirectory;
        RefreshTargets();

        // The targets were only read once at start, so installing UE4SS (or saving a new game
        // folder) left the tab saying "No UE4SS-settings.ini found" until a restart (review
        // 2026-10-23). Not while there are unsaved edits: re-reading would throw them away.
        WeakReferenceMessenger.Default.Register<Ue4ssLoaderChangedMessage>(this, (recipient, _) => ((Ue4ssConfigViewModel)recipient).RefreshTargetsUnlessEdited());
        WeakReferenceMessenger.Default.Register<SettingsSavedMessage>(this, (recipient, _) => ((Ue4ssConfigViewModel)recipient).RefreshTargetsUnlessEdited());
    }

    /// <summary>True while a setting on the tab has been changed and not saved.</summary>
    public bool HasUnsavedChanges => _fields.Any(f => f.IsChanged);

    private void RefreshTargetsUnlessEdited()
    {
        if (!HasUnsavedChanges)
        {
            RefreshTargets();
        }
    }

    public ObservableCollection<Ue4ssConfigTarget> Targets { get; } = [];

    public ObservableCollection<Ue4ssConfigSection> Sections { get; } = [];

    public ObservableCollection<Ue4ssConfigAlsoSave> AlsoSaveTo { get; } = [];

    [ObservableProperty]
    private Ue4ssConfigTarget? _selectedTarget;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string _filter = "";

    public bool HasTargets => Targets.Count > 0;

    public bool HasNoTargets => Targets.Count == 0;

    public bool HasOtherTargets => AlsoSaveTo.Count > 0;

    public string NoTargetsText => "No UE4SS-settings.ini found. Install UE4SS on the UE4SS tab first (or on a server from the Server page).";

    partial void OnSelectedTargetChanged(Ue4ssConfigTarget? value) => Load();

    partial void OnFilterChanged(string value) => BuildSections();

    private Ue4ssSettingsDocument? _document;
    private List<Ue4ssConfigField> _fields = [];

    /// <summary>The game and every Starlink server that has a UE4SS-settings.ini.</summary>
    [RelayCommand]
    private void RefreshTargets()
    {
        var previous = SelectedTarget?.IniPath;
        Targets.Clear();
        if (_settingsService.Current.IcarusContentPath is { Length: > 0 } content)
        {
            try
            {
                var ini = Path.Combine(Ue4ssGamePaths.ResolveLoaderFolder(content), "UE4SS-settings.ini");
                if (File.Exists(ini))
                {
                    Targets.Add(new Ue4ssConfigTarget("Game", ini, IsServer: false));
                }
            }
            catch (Exception)
            {
                // A Content path the resolver can't use: no game target.
            }
        }

        foreach (var profile in _serverStore.GetAll())
        {
            var ini = Path.Combine(DedicatedServerLayout.Win64Folder(profile.InstallFolder), "ue4ss", "UE4SS-settings.ini");
            // Two profiles on one install (different Saved suffixes) share one UE4SS-settings.ini:
            // list it once, or "also save to" would write and back up the same file twice.
            if (File.Exists(ini) && !Targets.Any(t => string.Equals(t.IniPath, ini, StringComparison.OrdinalIgnoreCase)))
            {
                Targets.Add(new Ue4ssConfigTarget($"Server: {profile.Name}", ini, IsServer: true));
            }
        }

        OnPropertyChanged(nameof(HasTargets));
        OnPropertyChanged(nameof(HasNoTargets));
        SelectedTarget = Targets.FirstOrDefault(t => t.IniPath == previous) ?? Targets.FirstOrDefault();
        if (SelectedTarget is null)
        {
            Load();
        }
    }

    private void Load()
    {
        _document = null;
        _fields = [];
        AlsoSaveTo.Clear();
        if (SelectedTarget is { } target)
        {
            try
            {
                _document = Ue4ssSettingsDocument.Load(target.IniPath);
                _fields = [.. _document.Settings.Select(s => new Ue4ssConfigField(s))];
                StatusMessage = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                StatusMessage = $"Couldn't read {target.IniPath}: {ex.Message}";
            }

            foreach (var other in Targets.Where(t => t != target))
            {
                AlsoSaveTo.Add(new Ue4ssConfigAlsoSave(other));
            }
        }

        OnPropertyChanged(nameof(HasOtherTargets));
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
            Sections.Add(new Ue4ssConfigSection(group.Key.Length == 0 ? "(no section)" : group.Key, [.. group]));
        }
    }

    [RelayCommand]
    private void Save()
    {
        if (SelectedTarget is not { } target || _document is null)
        {
            return;
        }

        var changed = _fields.Where(f => f.IsChanged).ToList();
        var extra = AlsoSaveTo.Where(a => a.IsChecked).Select(a => a.Target).ToList();
        if (changed.Count == 0)
        {
            StatusMessage = "Nothing changed.";
            return;
        }

        var saved = new List<string>();
        var problems = new List<string>();
        foreach (var t in new[] { target }.Concat(extra))
        {
            try
            {
                var document = t == target ? _document : Ue4ssSettingsDocument.Load(t.IniPath);
                foreach (var field in changed)
                {
                    document.SetValue(field.Section, field.Key, field.Value);
                }

                FolderBackup.BackupFile(t.IniPath, Path.Combine(_backupDirectory, "UE4SS-Settings", SafeName(t.Name)));
                document.Save(t.IniPath);
                saved.Add(t.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                problems.Add($"{t.Name}: {ex.Message}");
            }
        }

        if (saved.Contains(target.Name))
        {
            foreach (var field in changed)
            {
                field.MarkSaved();
            }
        }

        var what = changed.Count == 1 ? $"{changed[0].Key}" : $"{changed.Count} settings";
        var savedText = saved.Count > 0
            ? $"Saved {what} to {string.Join(" and ", saved)} (the old file is in Backups\\UE4SS-Settings). UE4SS reads these when the game or server starts: restart it to use them."
            : null;
        var problemText = problems.Count > 0 ? $"Couldn't save to {string.Join("; ", problems)}." : null;
        StatusMessage = string.Join(" ", new[] { savedText, problemText }.Where(t => t is not null));
    }

    [RelayCommand]
    private void DiscardChanges() => Load();

    private static string SafeName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));
}
