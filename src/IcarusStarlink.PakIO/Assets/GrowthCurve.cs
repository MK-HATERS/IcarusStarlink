using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.Engine.Curves;

namespace IcarusStarlink.PakIO.Assets;

/// <param name="IsCubic">RCIM_Cubic: eased between this key and the next (Hermite, with the keys' tangents).</param>
/// <param name="IsConstant">RCIM_Constant: holds this key's value until the next.</param>
public sealed record GrowthCurveKey(float Time, float Value, bool IsCubic, bool IsConstant, float ArriveTangent, float LeaveTangent);

/// <summary>
/// A cooked CurveFloat (the game's growth curves: XP per level, talent / blueprint / solo points
/// per level) evaluated the way the engine does. What happens past the last key matters here: the
/// game's XP and blueprint curves carry on in a straight line (PostInfinityExtrap = Linear,
/// checked 2026-10-23), the talent and solo curves stay flat (the default, Constant).
/// </summary>
public sealed record GrowthCurve(IReadOnlyList<GrowthCurveKey> Keys, bool LinearBefore, bool LinearAfter)
{
    /// <summary>The curve in a cooked CurveFloat export (its FloatCurve); null when it has no keys.</summary>
    public static GrowthCurve? From(UObject curveFloat)
    {
        try
        {
            if (!curveFloat.TryGetValue(out FRichCurve curve, "FloatCurve") || curve.Keys is not { Length: > 0 } keys)
            {
                return null;
            }

            return new GrowthCurve(
                [.. keys.Select(k => new GrowthCurveKey(k.Time, k.Value, k.InterpMode == ERichCurveInterpMode.RCIM_Cubic, k.InterpMode == ERichCurveInterpMode.RCIM_Constant, k.ArriveTangent, k.LeaveTangent))],
                curve.PreInfinityExtrap == ERichCurveExtrapolation.RCCE_Linear,
                curve.PostInfinityExtrap == ERichCurveExtrapolation.RCCE_Linear);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public GrowthCurveKey Last => Keys[^1];

    /// <summary>How much it rises per level past the last key: 0 when it stays flat.</summary>
    public double SlopeAfter => LinearAfter && Keys.Count > 1 && Last.Time > Keys[^2].Time ? (Last.Value - Keys[^2].Value) / (double)(Last.Time - Keys[^2].Time) : 0;

    public double Evaluate(double t)
    {
        var first = Keys[0];
        if (t <= first.Time)
        {
            return LinearBefore && Keys.Count > 1
                ? first.Value - (first.Time - t) * (Keys[1].Value - first.Value) / (Keys[1].Time - first.Time)
                : first.Value;
        }

        if (t >= Last.Time)
        {
            return Last.Value + (t - Last.Time) * SlopeAfter;
        }

        for (var i = 0; i < Keys.Count - 1; i++)
        {
            var (a, b) = (Keys[i], Keys[i + 1]);
            if (t > b.Time)
            {
                continue;
            }

            // At a key's own time, its own value (a Constant key before it would otherwise hold the previous one).
            if (t == b.Time || b.Time <= a.Time)
            {
                return b.Value;
            }

            var dt = b.Time - a.Time;
            var u = (t - a.Time) / dt;
            if (a.IsConstant)
            {
                return a.Value;
            }

            if (!a.IsCubic)
            {
                return a.Value + (b.Value - a.Value) * u;
            }

            var u2 = u * u;
            var u3 = u2 * u;
            return (2 * u3 - 3 * u2 + 1) * a.Value + (u3 - 2 * u2 + u) * dt * a.LeaveTangent + (-2 * u3 + 3 * u2) * b.Value + (u3 - u2) * dt * b.ArriveTangent;
        }

        return Last.Value;
    }

    /// <summary>The level this much XP reaches on an XP curve (whole levels), at most <paramref name="maxLevel"/>.</summary>
    public int LevelFor(double xp, int maxLevel)
    {
        int level;
        if (xp >= Last.Value)
        {
            level = SlopeAfter > 0 ? (int)Math.Floor(Last.Time + (xp - Last.Value) / SlopeAfter) : (int)Last.Time;
        }
        else
        {
            level = 0;
            while (level < Last.Time && Evaluate(level + 1) <= xp)
            {
                level++;
            }
        }

        return Math.Clamp(level, 0, Math.Max(0, maxLevel));
    }
}
