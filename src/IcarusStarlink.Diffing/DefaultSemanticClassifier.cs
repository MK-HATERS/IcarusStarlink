using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IcarusStarlink.Diffing;

/// <summary>
/// Classifies a changed field's merge semantics. Structural shapes (e.g. a
/// <c>{"RowName": ...}</c> struct, Unreal's FDataTableRowHandle serialization) are recognized
/// directly; everything else is matched against a small glob rule table (caller-overridable).
/// The default rules call every *TagQuery / *TagRequirements field a gameplay tag query. In the
/// game those are FGameplayTagQuery objects (D_AISetup.ValidBaitTagQuery,
/// D_WorldBosses.SpawnerTagQuery) or RowName links into D_TagQueries (caught as links above);
/// none is stored as a list, and GameplayTagQueryCombineRule only combines list values.
/// </summary>
public sealed class DefaultSemanticClassifier(IEnumerable<(string FieldNamePattern, ValueSemantic Semantic)>? rules = null) : ISemanticClassifier
{
    /// <summary>The default rules' globs, compiled once: Classify runs for every field of every queued mod.</summary>
    private static readonly (Regex Pattern, ValueSemantic Semantic)[] DefaultRules =
    [
        (Glob("*TagQuery*", RegexOptions.Compiled), ValueSemantic.GameplayTagQuery),
        (Glob("*TagRequirements*", RegexOptions.Compiled), ValueSemantic.GameplayTagQuery),
    ];

    private readonly IReadOnlyList<(Regex Pattern, ValueSemantic Semantic)> _rules =
        rules?.Select(r => (Glob(r.FieldNamePattern, RegexOptions.None), r.Semantic)).ToList() ?? (IReadOnlyList<(Regex, ValueSemantic)>)DefaultRules;

    public ValueSemantic Classify(string currentFile, string fieldName, JsonNode? value)
    {
        if (value is JsonObject obj && obj.ContainsKey("RowName"))
        {
            return ValueSemantic.RowReference;
        }

        foreach (var (pattern, semantic) in _rules)
        {
            if (pattern.IsMatch(fieldName))
            {
                return semantic;
            }
        }

        return value is JsonObject or JsonArray ? ValueSemantic.GenericCompound : ValueSemantic.Scalar;
    }

    /// <summary>A field-name glob ('*' is any run of characters), matched whole and ignoring case.</summary>
    private static Regex Glob(string pattern, RegexOptions extra) =>
        new("^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | extra);
}
