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

    /// <summary>A game file a mod's cooked file uses (MissingAsset, MissingAssetPart): the file and what it uses, not a table row.</summary>
    private bool IsAsset => Issue.Kind is ModIssueKind.MissingAsset or ModIssueKind.MissingAssetPart;

    public string Row => IsAsset ? NameOf(Issue.CurrentFile) : Issue.ItemName;

    public string Table => IsAsset ? "Mod file in " + FolderOf(Issue.CurrentFile)
        : GameTableIndex.TableNameOf(Issue.CurrentFile).Replace("D_", "", StringComparison.Ordinal);

    public string Field => IsAsset ? NameOf(Issue.ItemName) : Issue.FieldName ?? "(whole row)";

    public string ModText => IsAsset ? $"uses it from {FolderOf(Issue.ItemName)}"
        : Issue.Kind == ModIssueKind.OldGameRow ? $"carries an old copy of '{Issue.ItemName}'"
        : Issue.Kind == ModIssueKind.MissingRow ? $"changes '{Issue.ItemName}'"
        : Issue.Kind == ModIssueKind.SameAsGame ? ModFixViewModel.Describe(Issue.ModValue)
        : ModFixViewModel.OnlyIn(Issue.ModValue, Issue.GameValue) ?? ModFixViewModel.Describe(Issue.ModValue);

    public string GameText => Issue.Kind == ModIssueKind.MissingAssetPart ? $"took out: {Issue.FieldName}"
        : Issue.Kind == ModIssueKind.MissingAsset ? (Issue.SuggestedRow is null ? "doesn't have it any more" : $"has it in {FolderOf(Issue.SuggestedRow)}")
        : Issue.Kind == ModIssueKind.OldGameRow ? "doesn't have it any more"
        : Issue.Kind == ModIssueKind.MissingRow ? (Issue.SuggestedRow is null ? "doesn't have it any more" : $"has '{Issue.SuggestedRow}'")
        : Issue.Kind == ModIssueKind.BrokenLink && Issue.GameValue is null ? "doesn't have what it links to"
        : Issue.Kind == ModIssueKind.SameAsGame ? "the same (no change)"
        : ModFixViewModel.OnlyIn(Issue.GameValue, Issue.ModValue) ?? ModFixViewModel.Describe(Issue.GameValue);

    /// <summary>A renamed row has no values to compare, only the names; nor has a link with no game value to go back to.</summary>
    public bool ShowValues => Issue.Kind is not (ModIssueKind.MissingRow or ModIssueKind.OldGameRow or ModIssueKind.MissingAsset or ModIssueKind.MissingAssetPart)
        && !(Issue.Kind == ModIssueKind.BrokenLink && Issue.GameValue is null);

    /// <summary>"ASS/DEP/SM_X.uasset" or "/Game/ASS/DEP/M_X" → "SM_X" / "M_X".</summary>
    private static string NameOf(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        return name.Contains('.') ? name[..name.LastIndexOf('.')] : name;
    }

    /// <summary>"ASS/DEP/SM_X.uasset" or "/Game/ASS/DEP/M_X" → "ASS/DEP" (the game's Content folder for a file at its top).</summary>
    private static string FolderOf(string path)
    {
        var folder = path.Replace('\\', '/')[..Math.Max(0, path.LastIndexOf('/'))];
        folder = folder.StartsWith("/Game", StringComparison.OrdinalIgnoreCase) ? folder["/Game".Length..] : folder;
        folder = folder.Trim('/');
        return folder.Length == 0 ? "Content" : folder;
    }

    public string KindLabel => ModFixViewModel.KindTitle(Issue.Kind);

    public IReadOnlyList<ModFixChoice> Choices { get; }

    [ObservableProperty]
    private ModFixChoice _selectedChoice;

    public bool WillChange => SelectedChoice.Fix != ModIssueFix.KeepMod;

    partial void OnSelectedChoiceChanged(ModFixChoice value)
    {
        OnPropertyChanged(nameof(WillChange));
        _diff = null;
        OnPropertyChanged(nameof(Diff));
        OnPropertyChanged(nameof(AfterTitle));
    }

    /// <summary>The value once the chosen fix is made: the mod's kept, the game's (a field left out), or merged.</summary>
    public JsonNode? AfterValue => SelectedChoice.Fix switch
    {
        ModIssueFix.KeepMod => Issue.ModValue,
        ModIssueFix.Merge => Issue.MergedValue,
        _ => Issue.GameValue,
    };

    private IReadOnlyList<DiffRow>? _diff;

    /// <summary>The bottom panel: the mod, the game now and the result side by side, lined up (built when shown).</summary>
    public IReadOnlyList<DiffRow> Diff => _diff ??= ShowValues ? ValueDiff.Build(Issue.ModValue, Issue.GameValue, AfterValue) : [];

    /// <summary>Lists are compared entry by entry (in words); other values line by line (as code).</summary>
    public bool IsListDiff => ValueDiff.IsList(Issue.ModValue, Issue.GameValue);

    public string AfterTitle => $"AFTER YOUR FIX ({SelectedChoice.Label.ToUpperInvariant()})";

    public string DiffSummary
    {
        get
        {
            var onlyMod = Diff.Count(r => r.IsOnlyMod);
            var onlyGame = Diff.Count(r => r.IsOnlyGame);
            var changed = Diff.Count(r => r.IsChanged);
            var parts = new List<string>();
            if (onlyMod > 0) parts.Add($"{onlyMod} only the mod has");
            if (onlyGame > 0) parts.Add($"{onlyGame} only the game has");
            if (changed > 0) parts.Add($"{changed} line(s) different");
            return parts.Count == 0 ? "No difference." : string.Join(" · ", parts);
        }
    }

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
        [.. issues.SelectMany(i => i.Choices).DistinctBy(c => c.Fix).Select(c => new ModFixChoice(c.Fix, ModFixViewModel.GroupChoiceLabel(c.Fix, kind)))];

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
        _builtFor = report.BuiltFor?.OlderText;
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

    private string? _builtFor;

    public string Summary => _builtFor is null ? Body : $"{_builtFor}. {Body}";

    private string Body
    {
        get
        {
            var tidy = _all.Count(i => i.Issue.IsTidyOnly);
            var problems = _all.Count - tidy;
            var authors = $"The author's own {AuthorChangeCount} change(s) are kept and not listed.";
            if (_all.Count == 0)
            {
                return $"Nothing to fix: this mod fits the current game. Its {AuthorChangeCount} change(s) are the author's own"
                    + (RememberedCount > 0 ? ", or ones you chose to keep." : ".");
            }

            var tidyText = tidy == 0 ? "" : $"{tidy} thing(s) change nothing in game (values the game already has, or old rows the game removed): taking them out is optional tidying, and the game plays the same either way. ";
            if (problems == 0)
            {
                return "Nothing here is wrong: this mod fits the current game. " + tidyText + authors;
            }

            var sure = _all.Count(i => i.Issue.IsSure && !i.Issue.IsTidyOnly);
            return $"{problems} thing(s) in this mod don't fit the current game: {sure} sure fix(es), and {problems - sure} best guess(es), already chosen, for you to look over. "
                + tidyText + authors
                + " Your choices are remembered for this mod: what you keep isn't asked again, and the rest is done by itself next time.";
        }
    }

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

    /// <summary>How many fixes the last Apply made: 0 after "Keep all as they are" (the choices are remembered, the mod isn't touched).</summary>
    public int AppliedCount { get; private set; }

    /// <summary>Restore backup or Forget changed the mod or its remembered choices while the window was open: the Library re-checks it after.</summary>
    public bool ChangedWithoutApply { get; private set; }

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
            AppliedCount = applied;
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

            ChangedWithoutApply = true;
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
            ChangedWithoutApply = true;
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
        ModIssueKind.BrokenLink => -1,
        ModIssueKind.MissingAsset => -3,
        ModIssueKind.MissingAssetPart => -2,
        ModIssueKind.MissingRow => 3,
        ModIssueKind.OldGameRow => 5,
        _ => 4,
    };

    internal static string KindTitle(ModIssueKind kind) => kind switch
    {
        ModIssueKind.OldCopy => "Old copies of game data",
        ModIssueKind.SameAsGame => "Same as the game (tidy-up)",
        ModIssueKind.LikelyOld => "Probably old",
        ModIssueKind.MixedList => "Lists both changed",
        ModIssueKind.BrokenLink => "Links to things the game doesn't have",
        ModIssueKind.MissingRow => "Rows the game no longer has",
        ModIssueKind.OldGameRow => "Old game rows (tidy-up)",
        ModIssueKind.MissingAsset => "Game files it can't find",
        ModIssueKind.MissingAssetPart => "Parts the game took out",
        _ => kind.ToString(),
    };

    internal static string KindExplanation(ModIssueKind kind) => kind switch
    {
        ModIssueKind.OldCopy => "The mod was made for an older game version and copied these from it. They would only bring old data back (tags the game added since go missing, new fields get deleted). The fix leaves the game's current value.",
        ModIssueKind.SameAsGame => "Nothing wrong: the mod sets these to exactly what the game already has, so they change nothing. Taking them out only makes the mod smaller; the game plays the same.",
        ModIssueKind.LikelyOld => "Changes on rows the mod copied wholesale from an older game version. Most likely old values, not the author's idea; the suggestion is the game's current value.",
        ModIssueKind.MixedList => "The mod added entries to a list, and the game has entries the mod lacks (most likely added since). Merge keeps both.",
        ModIssueKind.BrokenLink => "The mod links these to something the game (and no mod in your Library) doesn't have, so in game they do nothing: a canteen you can't drink from, food with no model. Where the game removed what it links to, the fix links it back to what the game uses; otherwise it may need a mod that adds it, so it's kept as it is.",
        ModIssueKind.MissingRow => "The mod changes rows the game no longer has under that name: renamed since (it can be renamed to match), or removed (the change has nothing left to change).",
        ModIssueKind.MissingAsset => "The mod's own files (meshes, materials, blueprints) use these game files where the game doesn't have them any more, so that part of the mod is broken: a missing look, a missing sound, or, if something is built on it, the game stopping when it loads. Where the game only moved a file (same name, same contents), the fix points the mod to the new place and changes nothing else. The rest needs the mod's author.",
        ModIssueKind.MissingAssetPart => "The game still has these files, but has taken out parts the mod's files use (a component of a game blueprint, say). Those parts of the mod do nothing now; nothing to fix here, the mod's author has to update it. Keep the mod's to stop it being listed.",
        ModIssueKind.OldGameRow => "Nothing wrong in game: the mod was made from an older game version and still carries whole rows the game has since removed (the game's own row history shows it had them, or another mod in your Library carries the same rows). Nothing uses them, so they do nothing. Removing them only makes the mod smaller.",
        _ => "",
    };

    internal static string ChoiceLabel(ModIssueFix fix, ModIssue issue) => fix switch
    {
        ModIssueFix.UseGame => issue.Kind == ModIssueKind.SameAsGame ? "Take it out" : "Use the game's",
        ModIssueFix.KeepMod => issue.Kind is ModIssueKind.MissingAsset or ModIssueKind.MissingAssetPart ? "Leave it as it is" : "Keep the mod's",
        ModIssueFix.Merge => "Merge both",
        ModIssueFix.RenameRow => $"Rename to {issue.SuggestedRow}",
        ModIssueFix.RemoveRow => "Remove the row",
        ModIssueFix.Repoint => issue.SuggestedRow is null ? "Point to the new place" : $"Use the one in {FolderOfPackage(issue.SuggestedRow)}",
        _ => fix.ToString(),
    };

    /// <summary>"/Game/ASS/ITM/M_X" → "ASS/ITM".</summary>
    private static string FolderOfPackage(string package)
    {
        var folder = package[..Math.Max(0, package.LastIndexOf('/'))];
        return folder.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase) ? folder["/Game/".Length..] : folder.TrimStart('/');
    }

    internal static string GroupChoiceLabel(ModIssueFix fix, ModIssueKind kind) => fix switch
    {
        ModIssueFix.UseGame when kind == ModIssueKind.SameAsGame => "Take them all out",
        ModIssueFix.KeepMod when kind == ModIssueKind.SameAsGame => "Leave them all",
        ModIssueFix.UseGame => "Use the game's for all",
        ModIssueFix.KeepMod => "Keep the mod's for all",
        ModIssueFix.Merge => "Merge all",
        ModIssueFix.RenameRow => "Rename all",
        ModIssueFix.RemoveRow => "Remove all",
        ModIssueFix.Repoint => "Point all to the new places",
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
            // A small object in words: "RequiredFeatureLevel: GreatHunts".
            JsonObject o when o.Count is > 0 and <= 3 => string.Join(", ", o.Select(kv => $"{kv.Key}: {Describe(kv.Value)}")),
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

}
