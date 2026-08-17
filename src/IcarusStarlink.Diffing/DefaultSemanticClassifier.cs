using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IcarusStarlink.Diffing;

/// <summary>
/// Classifies a changed field's merge semantics. Structural shapes (e.g. a
/// <c>{"RowName": ...}</c> struct, Unreal's FDataTableRowHandle serialization) are recognized
/// directly; everything else is matched against a small glob rule table. No real
/// gameplay-tag-query field has been observed yet, so the rule table is caller-overridable
/// rather than hardcoded — plug in real patterns here once one shows up.
/// </summary>
public sealed class DefaultSemanticClassifier(IEnumerable<(string FieldNamePattern, ValueSemantic Semantic)>? rules = null) : ISemanticClassifier
{
    private static readonly (string Pattern, ValueSemantic Semantic)[] DefaultRules =
    [
        ("*TagQuery*", ValueSemantic.GameplayTagQuery),
        ("*TagRequirements*", ValueSemantic.GameplayTagQuery),
    ];

    private readonly IReadOnlyList<(string Pattern, ValueSemantic Semantic)> _rules =
        rules?.ToList() ?? [.. DefaultRules];

    public ValueSemantic Classify(string currentFile, string fieldName, JsonNode? value)
    {
        if (value is JsonObject obj && obj.ContainsKey("RowName"))
        {
            return ValueSemantic.RowReference;
        }

        foreach (var (pattern, semantic) in _rules)
        {
            if (MatchesGlob(fieldName, pattern))
            {
                return semantic;
            }
        }

        return value is JsonObject or JsonArray ? ValueSemantic.GenericCompound : ValueSemantic.Scalar;
    }

    private static bool MatchesGlob(string input, string pattern)
    {
        var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase);
    }
}
