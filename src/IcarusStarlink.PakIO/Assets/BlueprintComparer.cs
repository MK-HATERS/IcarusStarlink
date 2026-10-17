using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.UObject;
using Newtonsoft.Json;

namespace IcarusStarlink.PakIO.Assets;

/// <summary>
/// What a cooked blueprint is made of, by name: its parts (exports), variables, each function's
/// compiled code (a fingerprint of its instructions) and every part's default values. Enough to
/// compare a mod's copy of a game blueprint with the game's own, though not to show its logic.
/// </summary>
public sealed record BlueprintSummary(
    IReadOnlySet<string> Parts, IReadOnlySet<string> Variables,
    IReadOnlyDictionary<string, string> Functions, IReadOnlyDictionary<string, string> Defaults);

/// <summary>A default value that differs: "AltInteractHoldTime" 3.0 in the game, 0.5 in the mod.</summary>
public sealed record ChangedSetting(string Name, string GameValue, string ModValue);

/// <summary>
/// How a mod's copy of a game blueprint differs from the game's current one. Only names and
/// fingerprints are compared, so which side changed can't be told from the files alone: a
/// function that differs is the mod's change or old code; something only the game's copy has was
/// added by the game since, or removed by the mod on purpose. The game's own earlier versions
/// (recorded at each Refresh game data) are what settle that.
/// </summary>
public sealed record BlueprintDifference(
    IReadOnlyList<ChangedSetting> Settings, IReadOnlyList<string> ChangedFunctions,
    IReadOnlyList<string> OnlyInGame, IReadOnlyList<string> OnlyInMod)
{
    public bool IsIdentical => Settings.Count == 0 && ChangedFunctions.Count == 0 && OnlyInGame.Count == 0 && OnlyInMod.Count == 0;

    /// <summary>"changes 1 setting (AltInteractHoldTime 3 → 0.5) and 2 functions (WeightUpdated, …); adds 1 part (…)".</summary>
    public string Describe()
    {
        if (IsIdentical)
        {
            return "is identical";
        }

        var changes = new List<string>();
        if (Settings.Count > 0)
        {
            changes.Add($"{Count(Settings.Count, "setting")} ({string.Join(", ", Settings.Take(3).Select(Setting))}{More(Settings.Count, 3)})");
        }

        if (ChangedFunctions.Count > 0)
        {
            changes.Add($"{Count(ChangedFunctions.Count, "function")} ({string.Join(", ", ChangedFunctions.Take(4))}{More(ChangedFunctions.Count, 4)})");
        }

        var phrases = new List<string>();
        if (changes.Count > 0)
        {
            phrases.Add("changes " + string.Join(" and ", changes));
        }

        if (OnlyInMod.Count > 0)
        {
            phrases.Add($"adds {Count(OnlyInMod.Count, "part")} ({string.Join(", ", OnlyInMod.Take(3))}{More(OnlyInMod.Count, 3)})");
        }

        if (OnlyInGame.Count > 0)
        {
            phrases.Add($"doesn't have {Count(OnlyInGame.Count, "thing")} the game's copy has ({string.Join(", ", OnlyInGame.Take(3))}{More(OnlyInGame.Count, 3)})");
        }

        return string.Join("; ", phrases);
    }

    /// <summary>"AltInteractHoldTime 3 → 0.5" for plain values; just the name for lists and references.</summary>
    private static string Setting(ChangedSetting s) =>
        IsPlain(s.GameValue) && IsPlain(s.ModValue) ? $"{s.Name} {s.GameValue} → {s.ModValue}" : s.Name;

    private static bool IsPlain(string value) => value.Length <= 24 && !value.StartsWith('{') && !value.StartsWith('[') && !value.EndsWith('…');

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static string More(int n, int shown) => n > shown ? $" and {n - shown} more" : "";
}

/// <summary>Summarises cooked blueprints (CUE4Parse, with function code read) and compares two.</summary>
public static partial class BlueprintComparer
{
    public static BlueprintSummary Summarize(IPackage package)
    {
        var parts = new HashSet<string>(StringComparer.Ordinal);
        var variables = new HashSet<string>(StringComparer.Ordinal);
        var functions = new Dictionary<string, string>(StringComparer.Ordinal);
        var defaults = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var lazy in package.ExportsLazy)
        {
            UObject export;
            try
            {
                export = lazy.Value;
            }
            catch (Exception)
            {
                continue;
            }

            parts.Add($"{export.ExportType} {export.Name}");
            if (export is UFunction function)
            {
                functions[function.Name] = Fingerprint(function);
                continue;
            }

            if (export is UStruct type && export.ExportType.Contains("BlueprintGeneratedClass", StringComparison.Ordinal))
            {
                foreach (var field in type.ChildProperties ?? [])
                {
                    variables.Add(field.Name.Text);
                }

                continue;
            }

            foreach (var property in export.Properties)
            {
                defaults[$"{export.Name}.{property.Name.Text}"] = Value(property.Tag);
            }
        }

        return new BlueprintSummary(parts, variables, functions, defaults);
    }

    public static BlueprintDifference Compare(BlueprintSummary game, BlueprintSummary mod)
    {
        // Missing on one side and empty on the other is the same thing.
        static string? Of(BlueprintSummary b, string k) => b.Defaults.GetValueOrDefault(k) is "null" ? null : b.Defaults.GetValueOrDefault(k);
        var settings = game.Defaults.Keys.Union(mod.Defaults.Keys)
            .Where(k => Of(game, k) != Of(mod, k))
            .Select(k => new ChangedSetting(SettingName(k), Short(game.Defaults.GetValueOrDefault(k)), Short(mod.Defaults.GetValueOrDefault(k))))
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
        var changed = game.Functions.Keys.Intersect(mod.Functions.Keys)
            .Where(f => game.Functions[f] != mod.Functions[f])
            .Order(StringComparer.Ordinal)
            .ToList();
        var onlyInGame = game.Functions.Keys.Except(mod.Functions.Keys).Select(f => $"function {f}")
            .Concat(game.Variables.Except(mod.Variables).Select(v => $"variable {v}"))
            .Concat(game.Parts.Except(mod.Parts).Where(p => !p.StartsWith("Function ", StringComparison.Ordinal)).Select(PartName))
            .Distinct().ToList();
        var onlyInMod = mod.Functions.Keys.Except(game.Functions.Keys).Select(f => $"function {f}")
            .Concat(mod.Variables.Except(game.Variables).Select(v => $"variable {v}"))
            .Concat(mod.Parts.Except(game.Parts).Where(p => !p.StartsWith("Function ", StringComparison.Ordinal)).Select(PartName))
            .Distinct().ToList();
        return new BlueprintDifference(settings, changed, onlyInGame, onlyInMod);
    }

    /// <summary>A function's compiled code as a stable fingerprint (instruction count and their sequence).</summary>
    private static string Fingerprint(UFunction function)
    {
        if (function.ScriptBytecode is not { Length: > 0 } code)
        {
            return "0";
        }

        var tokens = string.Join(",", code.Select(e => e.Token.ToString()));
        return $"{code.Length}:{Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(tokens)))}";
    }

    /// <summary>A default value as text, with references by name only (two readers write their paths differently).</summary>
    private static string Value(object? tag)
    {
        try
        {
            return ObjectPath().Replace(JsonConvert.SerializeObject(tag, Formatting.None), "");
        }
        catch (Exception)
        {
            return "?";
        }
    }

    /// <summary>"Default__BP_X_C.AltInteractHoldTime" → "AltInteractHoldTime"; "CharMoveComp.LadderExitMontage" as is.</summary>
    private static string SettingName(string key) =>
        key.StartsWith("Default__", StringComparison.Ordinal) && key.IndexOf('.') is > 0 and var dot ? key[(dot + 1)..] : key;

    private static string PartName(string part) => part[(part.IndexOf(' ') + 1)..];

    /// <summary>A number as a number ("3.0" → "3"); anything long trimmed.</summary>
    private static string Short(string? value)
    {
        if (value is null)
        {
            return "(none)";
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return number.ToString("0.###", CultureInfo.InvariantCulture);
        }

        return value.Length > 40 ? value[..40] + "…" : value;
    }

    [GeneratedRegex("\"ObjectPath\":\"[^\"]*\"")]
    private static partial Regex ObjectPath();
}
