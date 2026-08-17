using System.Text.Json.Nodes;

namespace IcarusStarlink.Diffing;

public interface ISemanticClassifier
{
    ValueSemantic Classify(string currentFile, string fieldName, JsonNode? value);
}
