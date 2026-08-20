using System.Text.Json.Serialization;

namespace IcarusStarlink.Catalog.GitHub;

/// <summary>Only the one field this app actually reads out of GitHub's (much larger) repo-info response.</summary>
internal sealed class GitHubRepoDto
{
    [JsonPropertyName("pushed_at")]
    public DateTimeOffset? PushedAt { get; init; }
}
