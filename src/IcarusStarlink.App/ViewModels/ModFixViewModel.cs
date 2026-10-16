using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One way to handle an issue, as the dropdown shows it.</summary>
public sealed record ModFixChoice(ModIssueFix Fix, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One flagged difference between the mod and the game, with the chosen fix.</summary>
public sealed partial class ModFixIssueViewModel : ObservableObject
{
    public ModFixIssueViewModel(ModIssue issue)
    {
        Issue = issue;
        Choices = [.. issue.Choices.Select(f => new ModFixChoice(f, ModFixViewModel.ChoiceLabel(f, issue)))];
        _selectedChoice = Choices.First(c => c.Fix == issue.DefaultFix);
    }

    public ModIssue Issue { get; }

    public string Row => Issue.ItemName;

    public string Table => GameTableIndex.TableNameOf(Issue.CurrentFile).Replace("D_", "", StringComparison.Ordinal);

    public string Field => Issue.FieldName ?? "(whole row)";

    public string ModText => Issue.Kind == ModIssueKind.MissingRow ? $"changes '{Issue.ItemName}'"
        : ModFixViewModel.OnlyIn(Issue.ModValue, Issue.GameValue) ?? ModFixViewModel.Describe(Issue.ModValue);

    public string GameText => Issue.Kind == ModIssueKind.MissingRow ? $"has '{Issue.SuggestedRow}'"
        : ModFixViewModel.OnlyIn(Issue.GameValue, Issue.ModValue) ?? ModFixViewModel.Describe(Issue.GameValue);

    public string ModJson => ModFixViewModel.Pretty(Issue.ModValue);

    public string GameJson => ModFixViewModel.Pretty(Issue.GameValue);

    public string? MergedJson => Issue.MergedValue is null ? null : ModFixViewModel.Pretty(Issue.MergedValue);

    public bool HasMerged => Issue.MergedValue is not null;

    /// <summary>A renamed row has no values to compare, only the names.</summary>
    public bool ShowValues => Issue.Kind != ModIssueKind.MissingRow;

    public string KindLabel => ModFixViewModel.KindTitle(Issue.Kind);

    public IReadOnlyList<ModFixChoice> Choices { get; }

    [ObservableProperty]
    private ModFixChoice _selectedChoice;

    public bool WillChange => SelectedChoice.Fix != ModIssueFix.KeepMod;

    partial void OnSelectedChoiceChanged(ModFixChoice value) => OnPropertyChanged(nameof(WillChange));

    public void Choose(ModIssueFix fix)
    {
        if (Choices.FirstOrDefault(c => c.Fix == fix) is { } choice)
        {
            SelectedChoice = choice;
        }
    }
}

/// <summary>A summary card per kind: count, what it means, and "set all".</summary>
public sealed partial class ModFixGroupViewModel(ModIssueKind kind, IReadOnlyList<ModFixIssueViewModel> issues) : ObservableObject
{
    public ModIssueKind Kind { get; } = kind;

    public IReadOnlyList<ModFixIssueViewModel> Issues { get; } = issues;

    public string Title => ModFixViewModel.KindTitle(Kind);

    public string Explanation => ModFixViewModel.KindExplanation(Kind);

    public int Count => Issues.Count;

    public bool IsSure => Issues.All(i => i.Issue.IsSure);

    public string Badge => IsSure ? "sure" : "your call";

    /// <summary>The fixes every issue in this group offers, for "set all".</summary>
    public IReadOnlyList<ModFixChoice> Choices { get; } =
        [.. issues.SelectMany(i => i.Choices).DistinctBy(c => c.Fix).Select(c => new ModFixChoice(c.Fix, ModFixViewModel.GroupChoiceLabel(c.Fix)))];

    [RelayCommand]
    private void SetAll(ModFixChoice? choice)
    {
        if (choice is not null)
        {
            foreach (var issue in Issues)
            {
                issue.Choose(choice.Fix);
            }
        }
    }
}

/// <summary>
/// The fix window: what in this mod doesn't fit the current game, sorted by kind, each with a
/// default fix (the sure ones need no thought; the rest are best guesses to look over), the mod's
/// value next to the game's, and one Apply that backs the mod up first.
/// </summary>
public sealed partial class ModFixViewModel : ObservableObject
{
    private readonly ModFixService _service;
    private readonly string _folderName;
    private readonly Func<bool> _isOpenInEditor;
    private readonly ObservableCollection<ModFixIssueViewModel> _all = [];

    public ModFixViewModel(string folderName, string modName, ModHealthReport report, ModFixService service, Func<bool>? isOpenInEditor = null)
    {
        _folderName = folderName;
        _service = service;
        _isOpenInEditor = isOpenInEditor ?? (() => false);
        ModName = modName;
        IssuesView = CollectionViewSource.GetDefaultView(_all);
        IssuesView.Filter = o => o is ModFixIssueViewModel i
            && (SelectedGroup is null || i.Issue.Kind == SelectedGroup.Kind)
            && (SearchText.Length == 0 || i.Row.Contains(SearchText, StringComparison.OrdinalIgnoreCase) || i.Field.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        Load(report);
    }

    /// <summary>Shows a (new) check result: after Forget, everything is suggested afresh.</summary>
    private void Load(ModHealthReport report)
    {
        AuthorChangeCount = report.AuthorChangeCount;
        RememberedCount = _service.RememberedCount(_folderName);
        HasBackup = _service.HasBackup(_folderName);
        _all.Clear();
        foreach (var issue in report.Issues.Select(i => new ModFixIssueViewModel(i)))
        {
            issue.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ModFixIssueViewModel.SelectedChoice))
                {
                    OnPropertyChanged(nameof(ChangeCount));
                    OnPropertyChanged(nameof(ApplyLabel));
                }
            };
            _all.Add(issue);
        }

        // The ones that matter most first; "already the game's value" is only tidying up.
        Groups.Clear();
        foreach (var group in _all.GroupBy(i => i.Issue.Kind).OrderBy(g => KindOrder(g.Key)))
        {
            Groups.Add(new ModFixGroupViewModel(group.Key, [.. group]));
        }

        SelectedGroup = null;
        SelectedIssue = _all.FirstOrDefault();
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(ChangeCount));
        OnPropertyChanged(nameof(ApplyLabel));
    }

    public string WindowTitle => $"Fix mod — {ModName}";

    public string ModName { get; }

    [ObservableProperty]
    private int _authorChangeCount;

    /// <summary>Choices made before for this mod (they're done by themselves; a kept value isn't listed).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRemembered), nameof(RememberedText))]
    private int _rememberedCount;

    public bool HasRemembered => RememberedCount > 0;

    /// <summary>The mod has a backup from before its last change: Restore backup puts it back.</summary>
    [ObservableProperty]
    private bool _hasBackup;

    public string RememberedText => $"Forget my {RememberedCount} earlier choice(s)";

    public ObservableCollection<ModFixGroupViewModel> Groups { get; } = [];

    public ICollectionView IssuesView { get; }

    public bool HasIssues => _all.Count > 0;

    public string Summary => _all.Count == 0
        ? $"Nothing to fix: this mod fits the current game. Its {AuthorChangeCount} change(s) are the author's own"
          + (RememberedCount > 0 ? ", or ones you chose to keep." : ".")
        : $"{_all.Count} thing(s) in this mod don't fit the current game. {_all.Count(i => i.Issue.IsSure)} are sure fixes; "
          + $"the other {_all.Count(i => !i.Issue.IsSure)} are best guesses, already chosen, for you to look over. "
          + $"The author's own {AuthorChangeCount} change(s) are kept and not listed. "
          + "Your choices are remembered for this mod: what you keep isn't asked again, and the rest is done by itself next time.";

    public int ChangeCount => _all.Count(i => i.WillChange);

    public string ApplyLabel => ChangeCount == 0 ? "Keep all as they are" : $"Apply {ChangeCount} fix(es)";

    [ObservableProperty]
    private ModFixGroupViewModel? _selectedGroup;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private ModFixIssueViewModel? _selectedIssue;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isApplied;

    /// <summary>Set by the window: closes it after a successful Apply.</summary>
    public Action? Close { get; set; }

    /// <summary>Set by the Library: opens the mod in the mod editor.</summary>
    public Action? OpenInModEditor { get; set; }

    partial void OnSelectedGroupChanged(ModFixGroupViewModel? value)
    {
        IssuesView.Refresh();
        SelectedIssue = IssuesView.Cast<ModFixIssueViewModel>().FirstOrDefault();
    }

    partial void OnSearchTextChanged(string value) => IssuesView.Refresh();

    [RelayCommand]
    private void ShowAll() => SelectedGroup = null;

    /// <summary>Only the sure fixes; everything else stays as the mod has it.</summary>
    [RelayCommand]
    private void OnlySure()
    {
        foreach (var issue in _all)
        {
            issue.Choose(issue.Issue.IsSure ? issue.Issue.DefaultFix : ModIssueFix.KeepMod);
        }
    }

    /// <summary>Back to the suggested fix for everything.</summary>
    [RelayCommand]
    private void UseSuggestions()
    {
        foreach (var issue in _all)
        {
            issue.Choose(issue.Issue.DefaultFix);
        }
    }

    [RelayCommand]
    private void Apply()
    {
        if (_isOpenInEditor())
        {
            StatusMessage = "This mod is open in the mod editor. Close it there first, so its next Save doesn't undo these fixes.";
            return;
        }

        try
        {
            var applied = _service.Apply(_folderName, [.. _all.Select(i => (i.Issue, i.SelectedChoice.Fix))]);
            IsApplied = true;
            StatusMessage = applied == 0 ? "Nothing changed." : $"Fixed {applied} thing(s). The mod was backed up first: Restore backup in this window, or right-click the mod › Restore latest backup, undoes it.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't fix the mod: {ex.Message}";
            return;
        }

        WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
        Close?.Invoke();
    }

    [RelayCommand]
    private void OpenEditor() => OpenInModEditor?.Invoke();

    /// <summary>Puts the mod back as it was before its last change, then checks it again.</summary>
    [RelayCommand]
    private void RestoreBackup()
    {
        if (_isOpenInEditor())
        {
            StatusMessage = "This mod is open in the mod editor. Close it there first.";
            return;
        }

        try
        {
            if (!_service.RestoreBackup(_folderName))
            {
                StatusMessage = "There's no backup of this mod to restore.";
                return;
            }

            Load(_service.Check(_folderName));
            StatusMessage = "Restored the mod as it was before its last change.";
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't restore the backup: {ex.Message}";
        }
    }

    /// <summary>Forgets this mod's earlier choices and checks it again, so everything is suggested afresh.</summary>
    [RelayCommand]
    private void ForgetChoices()
    {
        try
        {
            _service.Forget(_folderName);
            Load(_service.Check(_folderName));
            StatusMessage = "Earlier choices forgotten: everything below is suggested afresh.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't check the mod again: {ex.Message}";
        }
    }

    private static int KindOrder(ModIssueKind kind) => kind switch
    {
        ModIssueKind.OldCopy => 0,
        ModIssueKind.LikelyOld => 1,
        ModIssueKind.MixedList => 2,
        ModIssueKind.MissingRow => 3,
        _ => 4,
    };

    internal static string KindTitle(ModIssueKind kind) => kind switch
    {
        ModIssueKind.OldCopy => "Old copies of game data",
        ModIssueKind.SameAsGame => "Already the game's value",
        ModIssueKind.LikelyOld => "Probably old",
        ModIssueKind.MixedList => "Lists both changed",
        ModIssueKind.MissingRow => "Rows the game renamed",
        _ => kind.ToString(),
    };

    internal static string KindExplanation(ModIssueKind kind) => kind switch
    {
        ModIssueKind.OldCopy => "The mod was made for an older game version and copied these from it. They would only bring old data back (tags the game added since go missing, new fields get deleted). The fix leaves the game's current value.",
        ModIssueKind.SameAsGame => "These already match the game, so they do nothing. Taking them out makes the mod smaller and clearer.",
        ModIssueKind.LikelyOld => "Changes on rows the mod copied wholesale from an older game version. Most likely old values, not the author's idea; the suggestion is the game's current value.",
        ModIssueKind.MixedList => "The mod added entries to a list, and the game has entries the mod lacks (most likely added since). Merge keeps both.",
        ModIssueKind.MissingRow => "The mod changes a row the game no longer has under that name.",
        _ => "",
    };

    internal static string ChoiceLabel(ModIssueFix fix, ModIssue issue) => fix switch
    {
        ModIssueFix.UseGame => issue.Kind == ModIssueKind.SameAsGame ? "Take it out" : "Use the game's",
        ModIssueFix.KeepMod => "Keep the mod's",
        ModIssueFix.Merge => "Merge both",
        ModIssueFix.RenameRow => $"Rename to {issue.SuggestedRow}",
        ModIssueFix.RemoveRow => "Remove the row",
        _ => fix.ToString(),
    };

    internal static string GroupChoiceLabel(ModIssueFix fix) => fix switch
    {
        ModIssueFix.UseGame => "Use the game's for all",
        ModIssueFix.KeepMod => "Keep the mod's for all",
        ModIssueFix.Merge => "Merge all",
        ModIssueFix.RenameRow => "Rename all",
        ModIssueFix.RemoveRow => "Remove all",
        _ => fix.ToString(),
    };

    /// <summary>A value in a few words: tags and links by name, a deleted field as "(deleted)".</summary>
    internal static string Describe(JsonNode? value)
    {
        var text = value switch
        {
            null => "(deleted)",
            JsonObject o when o["GameplayTags"] is JsonArray tags => tags.Count == 0 ? "no tags" : string.Join(", ", tags.Select(t => t?["TagName"]?.ToString())),
            JsonObject o when o.Count <= 2 && o["RowName"] is JsonValue row => row.ToString(),
            JsonArray a when a.All(e => e is JsonObject { } o && o["RowName"] is JsonValue) => string.Join(", ", a.Select(e => e!["RowName"]!.ToString())),
            JsonValue v => v.ToString(),
            _ => value.ToJsonString(),
        };
        return text.Length > 140 ? text[..140] + "…" : text;
    }

    /// <summary>
    /// For two lists (tags, links): what this side has that the other lacks, "only here: A, B", or
    /// "nothing the other lacks". Null when they aren't lists, so the plain value is shown.
    /// </summary>
    internal static string? OnlyIn(JsonNode? side, JsonNode? other)
    {
        if (Entries(side) is not { } mine || Entries(other) is not { } theirs)
        {
            return null;
        }

        var remaining = theirs.ToList();
        var only = new List<string>();
        foreach (var entry in mine)
        {
            var match = remaining.FindIndex(e => string.Equals(e, entry, StringComparison.OrdinalIgnoreCase));
            if (match >= 0)
            {
                remaining.RemoveAt(match);
            }
            else
            {
                only.Add(entry);
            }
        }

        var text = only.Count == 0 ? "nothing the other lacks" : $"only here: {string.Join(", ", only)}";
        return text.Length > 140 ? text[..140] + "…" : text;
    }

    private static List<string>? Entries(JsonNode? value) => value switch
    {
        JsonObject o when o["GameplayTags"] is JsonArray tags => [.. tags.Select(t => t?["TagName"]?.ToString() ?? "")],
        JsonArray a when a.All(e => e is JsonValue) => [.. a.Select(e => e!.ToString())],
        JsonArray a when a.All(e => e is JsonObject { } o && (o["RowName"] ?? o["TagName"]) is JsonValue) => [.. a.Select(e => (e!["RowName"] ?? e["TagName"])!.ToString())],
        _ => null,
    };

    internal static string Pretty(JsonNode? value) =>
        value is null ? "(deleted / not set)" : value.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}
