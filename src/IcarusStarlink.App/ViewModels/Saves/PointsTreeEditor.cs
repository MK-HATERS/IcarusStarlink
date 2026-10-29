using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.PakIO.Assets;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// One tree's row in "Change points per level" (Talents, Blueprints, Solo): how many times the
/// game's points, and whether they keep coming past the curve's last level, with what that would
/// give the selected character. Starts from what's installed now (a Starlink Player Points mod
/// made before shows its own settings).
/// </summary>
public sealed partial class PointsTreeEditor : ObservableObject
{
    private readonly Func<int?> _level;
    private readonly Func<int> _bonus;

    public PointsTreeEditor(string field, string label, GrowthCurve gameCurve, GrowthCurve? installed, Func<int?> level, Func<int> bonus,
        Services.PointsCurveChange? lastMade = null)
    {
        Field = field;
        Label = label;
        GameCurve = gameCurve;
        Installed = installed;
        _level = level;
        _bonus = bonus;
        var ratio = installed is not null && installed.Keys.Count == gameCurve.Keys.Count && gameCurve.Last.Value != 0
            ? installed.Last.Value / gameCurve.Last.Value
            : 1;
        _multiplierText = (lastMade?.Multiplier ?? Math.Round(ratio, 2)).ToString(CultureInfo.CurrentCulture);
        _keepRising = lastMade?.KeepRising ?? installed?.LinearAfter ?? gameCurve.LinearAfter;
    }

    public string Field { get; }

    public string Label { get; }

    public GrowthCurve GameCurve { get; }

    public GrowthCurve? Installed { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview), nameof(IsValid))]
    private string _multiplierText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview))]
    private bool _keepRising;

    /// <summary>"Keep rising past level 60".</summary>
    public string KeepRisingLabel => $"Keep rising past level {GameCurve.Last.Time:0}";

    /// <summary>A number above 0 and at most 1,000.</summary>
    public bool IsValid => TryMultiplier(out _);

    public double Multiplier => TryMultiplier(out var m) ? m : 1;

    private bool TryMultiplier(out double multiplier) =>
        double.TryParse(MultiplierText, NumberStyles.Float, CultureInfo.CurrentCulture, out multiplier) && multiplier > 0 && multiplier <= 1000;

    /// <summary>The curve this row would make: the game's keys times the multiplier.</summary>
    public GrowthCurve NewCurve => GameCurve with
    {
        Keys = [.. GameCurve.Keys.Select(k => k with { Value = (float)(k.Value * Multiplier) })],
        LinearAfter = KeepRising,
    };

    /// <summary>"At level 313: 106 now → 228".</summary>
    public string Preview
    {
        get
        {
            if (!IsValid)
            {
                return "Enter a number above 0 (1 = the game's own, 2 = twice as many).";
            }

            var level = _level() ?? (int)GameCurve.Last.Time;
            var bonus = _bonus();
            var now = (int)Math.Floor((Installed ?? GameCurve).Evaluate(level)) + bonus;
            var after = (int)Math.Floor(NewCurve.Evaluate(level)) + bonus;
            return $"At level {level:N0}: {now:N0} now → {after:N0}";
        }
    }

    public void Refresh() => OnPropertyChanged(nameof(Preview));
}
