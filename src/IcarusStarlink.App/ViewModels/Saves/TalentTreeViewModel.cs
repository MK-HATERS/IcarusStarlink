using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Services;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// The Talents and Workshop tabs' tree view, laid out like the game's own menu: a
/// list of trees on the left, the picked tree drawn with every node where the game
/// puts it (D_Talents Position and Size), the lines between them (RequiredTalents, bent the way
/// DrawMethodOverride says, through the game's Reroute points), and the rank rows (Novice to
/// Master, with the points each needs). The nodes are the same rows the list shows, so a change
/// in either is the same edit.
/// </summary>
public sealed partial class TalentTreeBrowserViewModel : ObservableObject
{
    private readonly Action<IEnumerable<ITreeTalent>> _wantIcons;

    public TalentTreeBrowserViewModel(string maxLabel, string resetLabel, IRelayCommand<string> max, IRelayCommand<string> reset,
        Action<IEnumerable<ITreeTalent>> wantIcons)
    {
        MaxLabel = maxLabel;
        ResetLabel = resetLabel;
        MaxCommand = max;
        ResetCommand = reset;
        _wantIcons = wantIcons;
    }

    public ObservableCollection<TalentTreeViewModel> Trees { get; } = [];

    /// <summary>"Max this tree" / "Research all in this tree".</summary>
    public string MaxLabel { get; }

    public string ResetLabel { get; }

    /// <summary>Takes the tree's Group (the heading the list shows).</summary>
    public IRelayCommand<string> MaxCommand { get; }

    public IRelayCommand<string> ResetCommand { get; }

    [ObservableProperty]
    private TalentTreeViewModel? _selectedTree;

    /// <summary>How big the tree is drawn: 1 = the game's own size, lower = smaller.</summary>
    [ObservableProperty]
    private double _zoom = 0.5;

    public bool HasTrees => Trees.Count > 0;

    /// <summary>Puts this tree's rows back to the ranks they had at the last load or save; the rest of the page keeps its changes.</summary>
    [RelayCommand]
    private void UndoTree(TalentTreeViewModel? tree)
    {
        foreach (var talent in (tree?.Talents ?? []).Where(t => t.IsDirty).ToList())
        {
            talent.Revert();
        }
    }

    /// <summary>The most a tree can be scaled down to: a blueprint tier is 50 screens wide at full size.</summary>
    public const double MinZoom = 0.04;

    /// <summary>Scales the picked tree to fit a <paramref name="width"/> by <paramref name="height"/> view (the view's own size, from the view).</summary>
    public void FitTo(double width, double height)
    {
        if (SelectedTree is not { Width: > 0, Height: > 0 } tree || width <= 0 || height <= 0)
        {
            return;
        }

        Zoom = Math.Clamp(Math.Min(width / tree.Width, height / tree.Height), MinZoom, 1);
    }

    partial void OnSelectedTreeChanged(TalentTreeViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        value.EnsureBuilt();
        _wantIcons(value.Talents);
    }

    /// <summary>Puts in the trees these rows belong to, keeping the picked tree when it's still there.</summary>
    public void Rebuild(IEnumerable<ITreeTalent> rows, IReadOnlyDictionary<string, IReadOnlyList<TalentNodeLayout>> layouts,
        IReadOnlyList<TalentRankGate> gates, string search)
    {
        var picked = SelectedTree?.Group;
        foreach (var tree in Trees)
        {
            tree.Detach();
        }

        Trees.Clear();
        foreach (var group in rows.Where(t => t.Tree.Length > 0 && layouts.ContainsKey(t.Tree))
                     .GroupBy(t => t.Group, StringComparer.Ordinal)
                     .OrderBy(g => g.First().GroupOrder))
        {
            var talents = group.ToList();
            // A group can span trees (Tier 2's bench and its blueprints): take every tree's nodes.
            var nodes = talents.Select(t => t.Tree).Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(tree => layouts[tree]).ToList();
            Trees.Add(new TalentTreeViewModel(group.Key, talents, nodes, gates));
        }

        ApplySearch(search);
        OnPropertyChanged(nameof(HasTrees));
        SelectedTree = Trees.FirstOrDefault(t => t.Group == picked && t.IsVisible)
            ?? Trees.FirstOrDefault(t => t.IsVisible)
            ?? Trees.FirstOrDefault();
    }

    /// <summary>Dims nodes that don't match, hides trees with no match, and moves off a tree that has none.</summary>
    public void ApplySearch(string search)
    {
        foreach (var tree in Trees)
        {
            tree.ApplySearch(search);
        }

        if (SelectedTree is { IsVisible: false } && Trees.FirstOrDefault(t => t.IsVisible) is { } first)
        {
            SelectedTree = first;
        }
    }
}

/// <summary>One tree: its rows, and (once it's shown) its nodes, lines and rank rows.</summary>
public sealed partial class TalentTreeViewModel : ObservableObject
{
    /// <summary>Room around the nodes, in the game's tree units.</summary>
    private const double Margin = 60;

    /// <summary>Room on the left for the rank names, in the game's tree units.</summary>
    private const double RankColumn = 260;

    private readonly IReadOnlyList<TalentNodeLayout> _layout;
    private readonly IReadOnlyList<TalentRankGate> _gates;
    private bool _built;

    public TalentTreeViewModel(string group, IReadOnlyList<ITreeTalent> talents, IReadOnlyList<TalentNodeLayout> layout, IReadOnlyList<TalentRankGate> gates)
    {
        Group = group;
        Talents = talents;
        _layout = layout;
        _gates = gates;
        foreach (var talent in talents)
        {
            talent.PropertyChanged += OnTalentChanged;
        }
    }

    /// <summary>The heading the list shows ("Survival · Resources").</summary>
    public string Group { get; }

    public IReadOnlyList<ITreeTalent> Talents { get; }

    public ObservableCollection<TalentNodeViewModel> Nodes { get; } = [];

    public ObservableCollection<TalentLinkViewModel> Links { get; } = [];

    public ObservableCollection<TalentRankBandViewModel> Bands { get; } = [];

    public double Width { get; private set; }

    public double Height { get; private set; }

    /// <summary>Not hidden by the search.</summary>
    [ObservableProperty]
    private bool _isVisible = true;

    /// <summary>Rows the game's tree doesn't place (a mod's talent): only the list shows those.</summary>
    public int NotPlacedCount { get; private set; }

    public string? NotPlacedNote => NotPlacedCount > 0
        ? $"{NotPlacedCount:N0} not in the game's tree (from a mod, or removed): switch to List to see {(NotPlacedCount == 1 ? "it" : "them")}."
        : null;

    /// <summary>"20/24": learned of all, for the tree list.</summary>
    public string Progress => $"{Talents.Count(t => t.IsUnlockedInGame):N0}/{Talents.Count:N0}";

    /// <summary>Every row at its highest rank.</summary>
    public bool IsComplete => Talents.Count > 0 && Talents.All(t => t.IsMaxed || (t.Rank == 0 && t.IsDefaultUnlocked));

    /// <summary>Ranks spent in this tree: what the rank rows need.</summary>
    public int Invested => Talents.Sum(t => t.Rank);

    /// <summary>"20 of 24 learned, 18 at their highest rank" for the tree's header.</summary>
    public string Summary => SaveGroupCounts.DescribeTree(Talents);

    /// <summary>A tree with rank rows: "59 points in this tree, next row Master needs 12" (the rows go by points spent in the tree).</summary>
    public string? PointsNote
    {
        get
        {
            if (Bands.Count == 0)
            {
                return null;
            }

            var invested = Invested;
            var next = Bands.FirstOrDefault(b => b.Investment > invested);
            var points = invested == 1 ? "1 point" : $"{invested:N0} points";
            return next is null
                ? $"{points} in this tree: every row open"
                : $"{points} in this tree: {next.Name} needs {next.Investment:N0}";
        }
    }

    /// <summary>A row in this tree changed since the last load or save: Undo for this tree is on.</summary>
    public bool HasChanges => Talents.Any(t => t.IsDirty);

    internal void Detach()
    {
        foreach (var talent in Talents)
        {
            talent.PropertyChanged -= OnTalentChanged;
        }
    }

    /// <summary>Lays the tree out the first time it's shown (about 2,000 nodes over every tree: only build what's looked at).</summary>
    public void EnsureBuilt()
    {
        if (_built)
        {
            return;
        }

        _built = true;
        var placed = _layout.Where(n => n.Width > 0 || n.IsRoutePoint).ToList();
        if (placed.Count == 0)
        {
            NotPlacedCount = Talents.Count;
            return;
        }

        var talentsByRow = Talents.ToDictionary(t => t.RowName, StringComparer.OrdinalIgnoreCase);
        var hasRanks = placed.Where(n => !n.IsRoutePoint).Select(n => n.RequiredRank).Distinct().Count() > 1;
        var left = placed.Min(n => n.X) - Margin - (hasRanks ? RankColumn : 0);
        var top = placed.Min(n => n.Y) - Margin;
        Width = placed.Max(n => n.X + n.Width) - left + Margin;
        Height = placed.Max(n => n.Y + n.Height) - top + Margin;

        var byRow = new Dictionary<string, TalentNodeViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in placed)
        {
            var talent = node.IsRoutePoint ? null : talentsByRow.GetValueOrDefault(node.Row);
            if (!node.IsRoutePoint && talent is null)
            {
                // A game row the save editor doesn't offer here (another model's): not drawn.
                continue;
            }

            var gate = _gates.FirstOrDefault(g => g.Row.Equals(node.RequiredRank, StringComparison.OrdinalIgnoreCase));
            var vm = new TalentNodeViewModel(talent, node.X - left, node.Y - top, node.Width, node.Height, gate?.Display);
            byRow.TryAdd(node.Row, vm);
            Nodes.Add(vm);
        }

        NotPlacedCount = Talents.Count(t => !byRow.ContainsKey(t.RowName));
        foreach (var node in placed)
        {
            if (!byRow.TryGetValue(node.Row, out var to))
            {
                continue;
            }

            foreach (var required in node.Requires)
            {
                if (byRow.TryGetValue(required, out var from))
                {
                    Links.Add(new TalentLinkViewModel(from, to, Route(from.Center, to.Center, node.DrawMethod)));
                }
            }
        }

        if (hasRanks)
        {
            foreach (var gate in _gates)
            {
                var rankNodes = Nodes.Where(n => n.Talent is not null && string.Equals(n.RequiredRank, gate.Display, StringComparison.Ordinal)).ToList();
                if (rankNodes.Count > 0)
                {
                    Bands.Add(new TalentRankBandViewModel(gate, rankNodes.Min(n => n.Top) - Margin / 2, Width));
                }
            }
        }

        Refresh();
    }

    /// <summary>The game's line shapes: XThenY goes across then down, YThenX down then across, anything else straight.</summary>
    internal static PointCollection Route(Point from, Point to, string drawMethod)
    {
        var points = new PointCollection { from };
        if (drawMethod == "XThenY")
        {
            points.Add(new Point(to.X, from.Y));
        }
        else if (drawMethod == "YThenX")
        {
            points.Add(new Point(from.X, to.Y));
        }

        points.Add(to);
        points.Freeze();
        return points;
    }

    private void OnTalentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ITreeTalent.Rank))
        {
            Refresh();
        }
    }

    /// <summary>Lights the nodes and lines that are learned, and the rank rows the tree's points reach.</summary>
    private void Refresh()
    {
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(IsComplete));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasChanges));
        if (!_built)
        {
            return;
        }

        // A route point is lit when something past it is: the line runs on to a learned talent.
        var dependents = Links.GroupBy(l => l.From).ToDictionary(g => g.Key, g => g.Select(l => l.To).ToList());
        var lit = new Dictionary<TalentNodeViewModel, bool>();
        bool IsLit(TalentNodeViewModel node, int depth)
        {
            if (lit.TryGetValue(node, out var known))
            {
                return known;
            }

            lit[node] = false;
            var on = node.Talent is { } talent
                ? talent.IsUnlockedInGame
                : depth < 64 && dependents.GetValueOrDefault(node, []).Any(d => IsLit(d, depth + 1));
            lit[node] = on;
            return on;
        }

        foreach (var node in Nodes)
        {
            node.IsLit = IsLit(node, 0);
        }

        foreach (var link in Links)
        {
            link.IsLit = link.From.IsLit && link.To.IsLit;
        }

        var invested = Invested;
        foreach (var band in Bands)
        {
            band.Update(invested);
        }

        OnPropertyChanged(nameof(PointsNote));
    }

    public void ApplySearch(string search)
    {
        search = search.Trim();
        var matches = search.Length == 0
            ? Talents.Count
            : Talents.Count(t => Matches(t, search));
        IsVisible = matches > 0 || search.Length == 0;
        foreach (var node in Nodes)
        {
            node.IsDimmed = search.Length > 0 && (node.Talent is null || !Matches(node.Talent, search));
        }
    }

    private bool Matches(ITreeTalent talent, string search) =>
        talent.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
        || talent.RowName.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Group.Contains(search, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A node on the tree: a talent, or a bend the game's lines go through (Talent null).</summary>
public sealed partial class TalentNodeViewModel(ITreeTalent? talent, double left, double top, double width, double height, string? requiredRank) : ObservableObject
{
    public ITreeTalent? Talent { get; } = talent;

    public double Left { get; } = left;

    public double Top { get; } = top;

    public double Width { get; } = width;

    public double Height { get; } = height;

    public Point Center => new(Left + Width / 2, Top + Height / 2);

    /// <summary>"Apprentice": the rank row it's in, for its tooltip.</summary>
    public string? RequiredRank { get; } = requiredRank;

    public bool IsTalent => Talent is not null;

    /// <summary>The rank under the icon, sized to the node (the trees use 128 to 250 unit nodes).</summary>
    public double BadgeFontSize => Math.Max(16, Width * 0.15);

    /// <summary>Learned (or, for a bend, leading on to something learned).</summary>
    [ObservableProperty]
    private bool _isLit;

    /// <summary>The search is on and this isn't what it's looking for.</summary>
    [ObservableProperty]
    private bool _isDimmed;
}

/// <summary>A line from a talent to one that needs it.</summary>
public sealed partial class TalentLinkViewModel(TalentNodeViewModel from, TalentNodeViewModel to, PointCollection points) : ObservableObject
{
    public TalentNodeViewModel From { get; } = from;

    public TalentNodeViewModel To { get; } = to;

    public PointCollection Points { get; } = points;

    /// <summary>Both ends learned.</summary>
    [ObservableProperty]
    private bool _isLit;
}

/// <summary>A rank row across the tree: "Apprentice · needs 4 points in this tree".</summary>
public sealed partial class TalentRankBandViewModel(TalentRankGate gate, double top, double width) : ObservableObject
{
    public double Top { get; } = top;

    public double Width { get; } = width;

    public string Name { get; } = gate.Display;

    public int Investment { get; } = gate.Investment;

    public string Needs => Investment > 0 ? $"needs {Investment:N0} points in this tree" : "from the start";

    /// <summary>The tree has the points this row needs.</summary>
    [ObservableProperty]
    private bool _isReached;

    internal void Update(int invested) => IsReached = invested >= Investment;
}
