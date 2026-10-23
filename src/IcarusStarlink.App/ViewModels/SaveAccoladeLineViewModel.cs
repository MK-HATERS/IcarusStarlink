using System.ComponentModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One tier of a tiered accolade: its numeral ("III") and the accolade row it switches.</summary>
public sealed record SaveAccoladeTier(string Label, SaveAccoladeViewModel Accolade);

/// <summary>
/// One cell on the Accolades tab: a single accolade, or a tiered set ("Wolf Killer I" to "V") as one
/// name with a switch per tier. Each tier is still its own accolade row with its own switch, so
/// saving writes exactly the tiers that are on; nothing here turns a tier on or off by itself.
/// </summary>
public sealed partial class SaveAccoladeLineViewModel : ObservableObject
{
    private static readonly string[] Numerals = ["I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X"];

    private static readonly Regex TierName = new(@"^(?<name>.*\S)\s+(?<tier>I|II|III|IV|V|VI|VII|VIII|IX|X)$", RegexOptions.CultureInvariant);

    public string Name { get; }

    public string Category { get; }

    public IReadOnlyList<SaveAccoladeTier> Tiers { get; }

    public bool IsTiered => Tiers.Count > 1;

    /// <summary>The accolade itself when it has no tiers.</summary>
    public SaveAccoladeViewModel? Single => IsTiered ? null : Tiers[0].Accolade;

    public bool IsDirty => Tiers.Any(t => t.Accolade.IsDirty);

    public bool AnyNotEarned => Tiers.Any(t => !t.Accolade.IsCompleted);

    /// <summary>The tooltip: a single accolade's description, or each tier's.</summary>
    public string Description => IsTiered
        ? string.Join(Environment.NewLine, Tiers.Select(t => t.Accolade.Description.Length > 0 ? $"{t.Label}: {t.Accolade.Description}" : t.Accolade.DisplayName))
        : Tiers[0].Accolade.Description;

    private SaveAccoladeLineViewModel(string name, string category, IReadOnlyList<SaveAccoladeTier> tiers)
    {
        Name = name;
        Category = category;
        Tiers = tiers;
        foreach (var tier in tiers)
        {
            tier.Accolade.PropertyChanged += OnTierChanged;
        }
    }

    private void OnTierChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DirtyRow.IsDirty))
        {
            OnPropertyChanged(nameof(IsDirty));
        }
    }

    /// <summary>
    /// The cells for <paramref name="accolades"/>, in their order. Rows named "X I", "X II"... in the
    /// same category become one tiered cell at the first one's place; a set with a repeated numeral,
    /// or a numeral on its own, stays as separate cells.
    /// </summary>
    public static List<SaveAccoladeLineViewModel> Build(IEnumerable<SaveAccoladeViewModel> accolades)
    {
        var rows = accolades.ToList();
        var parsed = rows.Select(a => TierName.Match(a.DisplayName) is { Success: true } m
            ? (Key: (a.Category, m.Groups["name"].Value), Tier: Array.IndexOf(Numerals, m.Groups["tier"].Value))
            : (Key: (a.Category, (string?)null), Tier: -1)).ToList();
        var sets = new Dictionary<(string, string?), List<int>>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (parsed[i].Key.Item2 is not null)
            {
                (sets.TryGetValue(parsed[i].Key, out var list) ? list : sets[parsed[i].Key] = []).Add(i);
            }
        }

        var lines = new List<SaveAccoladeLineViewModel>();
        var placed = new HashSet<int>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (!placed.Add(i))
            {
                continue;
            }

            if (parsed[i].Key.Item2 is { } name && sets[parsed[i].Key] is { Count: > 1 } members
                && members.Select(m => parsed[m].Tier).Distinct().Count() == members.Count)
            {
                placed.UnionWith(members);
                var tiers = members.OrderBy(m => parsed[m].Tier).Select(m => new SaveAccoladeTier(Numerals[parsed[m].Tier], rows[m])).ToList();
                lines.Add(new SaveAccoladeLineViewModel(name, rows[i].Category, tiers));
                continue;
            }

            lines.Add(new SaveAccoladeLineViewModel(rows[i].DisplayName, rows[i].Category, [new SaveAccoladeTier("", rows[i])]));
        }

        return lines;
    }
}
