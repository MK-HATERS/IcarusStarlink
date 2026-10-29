using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Services;
using IcarusStarlink.PakIO.Assets;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// One saved mount. The list's own fields (Mounts.json MountName / MountLevel) are always
/// editable; the mount's real data, its RecorderBlob, is edited through MountBlob (plan step 4,
/// 2026-10-23): its own copy of the name (kept the same as the list's, which used to drift apart),
/// experience to match a new level, talents, genetics, lineage and look. Inside editing is only
/// offered when the blob writes back byte for byte unchanged (checked on load); otherwise the
/// mount stays label-only, as before. Species (MountType) is never changed: the species, its
/// talents and its stats live together in the blob.
/// </summary>
public sealed partial class SaveMountViewModel : DirtyRow
{
    private string _originalName;
    private int _originalLevel;

    /// <summary>The blob differs from <see cref="_cleanBlob"/>: an inside edit not set back.</summary>
    private bool _insideDirty;

    /// <summary>The blob as loaded or last saved; an inside edit set back to it is no edit.</summary>
    private byte[] _cleanBlob = [];

    /// <summary>The save check's name fix: write the list's name into the blob even with nothing else changed.</summary>
    private bool _syncNamePending;

    /// <summary>The experience the mount was loaded (or last saved) with, put back when the level is set back to its own.</summary>
    private int? _originalExperience;

    public JsonObject Node { get; }

    /// <summary>The blob, when it can be edited safely; null keeps the mount label-only.</summary>
    public MountBlob? Blob { get; }

    /// <summary>Why the inside can't be edited ("its data doesn't write back exactly as it was read"); null when it can.</summary>
    public string? InsideRefusal { get; }

    public bool CanEditInside => Blob is not null;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TalentSummary), nameof(OverBudget), nameof(ExperienceDisplay))]
    private string _levelText;

    /// <summary>
    /// Shown, never edited (a review finding): changing the species label alone made a mount whose
    /// list entry said one species while the game still had the other, with that species' talents.
    /// </summary>
    public string TypeRowName { get; }

    public string TypeDisplayName => SaveGameNames.HumanizeMountType(TypeRowName);

    public string? IconPath { get; }

    [ObservableProperty]
    private BitmapImage? _icon;

    public override bool IsDirty =>
        _insideDirty
        || _syncNamePending
        || Name != _originalName
        || (int.TryParse(LevelText, out var level) ? level != _originalLevel : LevelText != _originalLevel.ToString());

    public SaveMountViewModel(
        JsonObject node, string name, int level, string typeRowName,
        string? iconPath, Action onDirtyChanged)
        : base(onDirtyChanged)
    {
        Node = node;
        _name = name;
        _originalName = name;
        _levelText = level.ToString();
        _originalLevel = level;
        TypeRowName = typeRowName;
        IconPath = iconPath;

        if (node["RecorderBlob"] is JsonObject recorder && recorder["BinaryData"] is JsonArray data)
        {
            try
            {
                Blob = MountBlob.TryRead([.. data.Select(n => (byte)(int)n!)], out var why);
                InsideRefusal = why;
            }
            catch (Exception)
            {
                InsideRefusal = "its data couldn't be read";
            }
        }
        else
        {
            InsideRefusal = "it has no saved data";
        }

        if (Blob is { } blob)
        {
            _cleanBlob = blob.Write();
            _originalExperience = blob.Experience;
            _lineage = blob.Lineage;
            _variation = blob.Variation ?? 0;
        }
    }

    // --- Inside editing (filled in by SavesViewModel once the game's tables are known) ---

    /// <summary>Its growth row as installed: XP per level, talent points per level, level cap.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TalentSummary), nameof(OverBudget), nameof(ExperienceDisplay), nameof(GrowthNote))]
    private MountGrowth? _growth;

    public ObservableCollection<MountGeneViewModel> Genes { get; } = [];

    public ObservableCollection<MountTalentViewModel> TalentRows { get; } = [];

    public IReadOnlyList<(string Row, string Title)> LineageOptions { get; private set; } = [];

    public IReadOnlyList<string> LineageTitles => [.. LineageOptions.Select(o => o.Title)];

    [ObservableProperty]
    private string? _lineage;

    /// <summary>The lineage shown by its title ("Alpha").</summary>
    public string? LineageTitle
    {
        get => LineageOptions.FirstOrDefault(o => o.Row == Lineage).Title ?? Lineage;
        set
        {
            if (LineageOptions.FirstOrDefault(o => o.Title == value).Row is { } row && row != Lineage)
            {
                Lineage = row;
                Blob!.Lineage = row;
                MarkInsideDirty();
            }
        }
    }

    /// <summary>The species' looks, and always the mount's own: one past the table's count (a mod's, or an older table) showed a blank box.</summary>
    public IReadOnlyList<int> VariationOptions => Growth is { Variations: > 0 } g
        ? [.. Enumerable.Range(0, g.Variations).Append(Variation).Distinct().Order()]
        : [Variation];

    [ObservableProperty]
    private int _variation;

    partial void OnVariationChanged(int value)
    {
        if (Blob is { } blob && blob.Variation != value)
        {
            blob.Variation = value;
            MarkInsideDirty();
        }
    }

    /// <summary>Fills the editor rows from the game's tables: genetics titles, lineages, the trees of this mount's archetype.</summary>
    public void SetGameData(SaveGameNames names, MountGrowth? growth)
    {
        Growth = growth;
        Genes.Clear();
        TalentRows.Clear();
        if (Blob is not { } blob)
        {
            return;
        }

        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (row, title) in names.GeneticValues)
        {
            titles.TryAdd(row, title);
        }

        foreach (var gene in blob.Genetics)
        {
            Genes.Add(new MountGeneViewModel(gene.Name, titles.GetValueOrDefault(gene.Name, gene.Name), gene.Value, value =>
            {
                blob.SetGene(gene.Name, value);
                MarkInsideDirty();
            }));
        }

        LineageOptions = names.Lineages;
        OnPropertyChanged(nameof(LineageTitles));
        OnPropertyChanged(nameof(LineageTitle));
        OnPropertyChanged(nameof(VariationOptions));

        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var talent in blob.Talents)
        {
            ranks.TryAdd(talent.Row, talent.Rank);
        }

        var trees = growth?.Archetype is { } archetype
            ? names.TalentTrees.Where(t => t.Value.ArchetypeRow.Equals(archetype, StringComparison.OrdinalIgnoreCase)).Select(t => t.Key).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        var rows = names.Talents.Where(t => trees.Contains(t.Value.Tree)).Select(t => (Row: t.Key, Info: t.Value)).ToList();

        // The mount's own talents the tables don't place in its trees still show (nothing in the save is hidden).
        foreach (var (row, _) in ranks.Where(r => !rows.Any(x => x.Row.Equals(r.Key, StringComparison.OrdinalIgnoreCase))))
        {
            rows.Add((row, names.Talents.GetValueOrDefault(row) ?? new TalentInfo(row, "", "", 0, false)));
        }

        foreach (var (row, info) in rows.OrderBy(r => names.TalentTrees.GetValueOrDefault(r.Info.Tree)?.Display ?? "").ThenBy(r => r.Info.DisplayName))
        {
            TalentRows.Add(new MountTalentViewModel(row, info.DisplayName, names.TalentTrees.GetValueOrDefault(info.Tree)?.Display ?? "", info.MaxRank,
                info.IsDefaultUnlocked, ranks.GetValueOrDefault(row), rank =>
                {
                    if (!blob.SetTalentRank(row, rank))
                    {
                        return false;
                    }

                    MarkInsideDirty();
                    OnPropertyChanged(nameof(TalentSummary));
                    OnPropertyChanged(nameof(OverBudget));
                    return true;
                }));
        }

        OnPropertyChanged(nameof(TalentSummary));
        OnPropertyChanged(nameof(OverBudget));
    }

    private int? Level => SaveNumberRules.TryParseCount(LevelText, out var level) ? level : null;

    protected override string? NumberProblem => SaveNumberRules.CountProblem(LevelText);

    private int TalentsUsed => TalentRows.Where(t => !t.IsDefaultUnlocked).Sum(t => t.Rank);

    private int? TalentsAvailable => Growth?.Growth?.TalentPoints is { } curve && Level is { } level ? (int)Math.Floor(curve.Evaluate(level)) : null;

    /// <summary>"Talents 22 of 50".</summary>
    public string TalentSummary => TalentsAvailable is { } available
        ? $"Talents {TalentsUsed:N0} of {available:N0}{(TalentsUsed > available ? " (over)" : "")}"
        : $"Talents {TalentsUsed:N0}";

    /// <summary>The over-budget label (the user's ask for characters, 2026-10-23, here for tames too).</summary>
    public string? OverBudget => TalentsAvailable is { } available && TalentsUsed > available
        ? $"Over budget by {TalentsUsed - available:N0}: the game keeps the ranks, but nothing more can be bought until under."
        : null;

    /// <summary>"3,890,000 XP" once the level is set from a curve.</summary>
    public string? ExperienceDisplay => Blob?.Experience is { } xp ? $"{xp:N0} XP" : null;

    public string? GrowthNote => Growth?.Growth is { } g
        ? $"Growth: {Growth.GrowthRow} (levels up to {g.MaxLevel:N0}){(g.CurvesFromMods.Count > 0 ? ", from an installed mod" : "")}."
        : null;

    partial void OnNameChanged(string value) => Changed();

    partial void OnLevelTextChanged(string value)
    {
        ApplyLevelToExperience();
        RecheckInside();
    }

    partial void OnGrowthChanged(MountGrowth? value)
    {
        // A level typed before the growth was read gets its experience now.
        if (Level != _originalLevel)
        {
            ApplyLevelToExperience();
            RecheckInside();
        }
    }

    /// <summary>
    /// A new level gets the experience that level needs, so the game doesn't put it back; the
    /// mount's own level gets its own experience back, progress into the level included (review,
    /// 2026-10-23: typing a level and back reset it to the level's start).
    /// </summary>
    private void ApplyLevelToExperience()
    {
        if (Blob is not { } blob || Level is not { } level)
        {
            return;
        }

        if (level == _originalLevel)
        {
            if (blob.Experience != _originalExperience)
            {
                blob.Experience = _originalExperience;
            }

            return;
        }

        if (Growth?.Growth?.Experience is { } xpCurve)
        {
            var max = Growth.Growth.MaxLevel > 0 ? Growth.Growth.MaxLevel : int.MaxValue;
            var target = (int)Math.Ceiling(xpCurve.Evaluate(Math.Min(level, max)));
            if (blob.Experience != target)
            {
                blob.Experience = target;
            }
        }
    }

    /// <summary>Save writes the list's name into the game's own copy too (the save check's fix for names that drifted apart).</summary>
    public void SyncNameOnSave()
    {
        if (Blob is not null)
        {
            _syncNamePending = true;
            Changed();
        }
    }

    private void MarkInsideDirty() => RecheckInside();

    /// <summary>
    /// Whether the inside differs from its clean copy, by its bytes: a talent, gene or level set
    /// back to what it was is no edit any more (the row stayed "changed" before), and an edit
    /// always shows on the row's dot and the Save bar together.
    /// </summary>
    private void RecheckInside()
    {
        _insideDirty = Blob is { } blob && !blob.Write().AsSpan().SequenceEqual(_cleanBlob);
        OnPropertyChanged(nameof(ExperienceDisplay));
        Changed();
    }

    /// <summary>
    /// Writes the list's Name/Level into the mount's own Node and, when the inside can be edited,
    /// the blob too: its name always follows the list's (they drifted apart before), plus any
    /// inside edit. A non-numeric Level box keeps the original value rather than corrupting the save.
    /// </summary>
    public void ApplyToNode()
    {
        Node["MountName"] = Name;
        if (SaveNumberRules.TryParseCount(LevelText, out var level))
        {
            Node["MountLevel"] = level;
        }

        // Only when this mount was edited: a Save never quietly rewrites a mount nobody touched.
        if (Blob is { } blob && (_insideDirty || _syncNamePending || Name != _originalName))
        {
            blob.Name = Name;
            if (Node["RecorderBlob"] is JsonObject recorder)
            {
                recorder["BinaryData"] = new JsonArray([.. blob.Write().Select(b => (JsonNode)JsonValue.Create((int)b))]);
            }
        }
    }

    /// <summary>
    /// The Level branch must reject exactly what ApplyToNode() rejects — a negative LevelText that
    /// Save silently declined to write must not be adopted as the new "clean" baseline either.
    /// The saved experience becomes the one a level set back gets: it used to stay the one from
    /// before the first Save, so level 10, Save, 12, then 10 again put the old experience back
    /// without the mount even looking changed.
    /// </summary>
    protected override void Snapshot()
    {
        _originalName = Name;
        _syncNamePending = false;
        if (SaveNumberRules.TryParseCount(LevelText, out var level))
        {
            _originalLevel = level;
        }

        if (Blob is { } blob)
        {
            _cleanBlob = blob.Write();
            _originalExperience = blob.Experience;
        }

        _insideDirty = false;
    }
}

/// <summary>One genetic value: 0 to 10, 3 is the middle.</summary>
public sealed partial class MountGeneViewModel(string row, string title, int value, Action<int> onChanged) : ObservableObject
{
    public string Row { get; } = row;

    public string Title { get; } = title;

    [ObservableProperty]
    private int _value = value;

    partial void OnValueChanged(int value) => onChanged(value);

    [RelayCommand]
    private void Up() => Value = Math.Min(10, Value + 1);

    [RelayCommand]
    private void Down() => Value = Math.Max(0, Value - 1);
}

/// <summary>One talent of a mount's trees, with its rank (0 = not learned).</summary>
public sealed partial class MountTalentViewModel(string row, string displayName, string tree, int maxRank, bool isDefaultUnlocked, int rank, Func<int, bool> setRank) : ObservableObject
{
    public string Row { get; } = row;

    public string DisplayName { get; } = displayName;

    public string Tree { get; } = tree;

    public int MaxRank { get; } = maxRank;

    public bool IsDefaultUnlocked { get; } = isDefaultUnlocked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RankDisplay), nameof(CanRankUp))]
    private int _rank = rank;

    public string RankDisplay => Rank == 0 && IsDefaultUnlocked ? "Default" : MaxRank > 0 ? $"{Rank}/{MaxRank}" : Rank.ToString();

    /// <summary>Below its highest rank (or no cap known): the shared rank stepper greys + out otherwise.</summary>
    public bool CanRankUp => MaxRank == 0 || Rank < MaxRank;

    [RelayCommand]
    private void RankUp()
    {
        if (CanRankUp && setRank(Rank + 1))
        {
            Rank++;
        }
    }

    [RelayCommand]
    private void RankDown()
    {
        if (Rank > 0 && setRank(Rank - 1))
        {
            Rank--;
        }
    }
}
