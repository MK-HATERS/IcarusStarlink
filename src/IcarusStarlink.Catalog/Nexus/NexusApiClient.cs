using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace IcarusStarlink.Catalog.Nexus;

/// <summary>
/// Nexus's own public REST API (https://api.nexusmods.com/v1) — base URL, the "apikey" header
/// name, and both endpoint paths below are confirmed against Nexus's own official node-nexus-api
/// client source during Phase 8 planning, not guessed. The API key is a per-call parameter, not a
/// fixed header baked in at construction time, since it's entered by the user at runtime and can
/// change (Settings' Authorize/Sign out flow) — a typed HttpClient's own DefaultRequestHeaders
/// wouldn't fit that.
/// </summary>
public sealed class NexusApiClient(HttpClient httpClient) : INexusApiClient
{
    private const string BaseUrl = "https://api.nexusmods.com/v1";

    public async Task<NexusUserInfo?> ValidateKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/users/validate");
        request.Headers.Add("apikey", apiKey);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<ValidateResponseDto>(cancellationToken)
            ?? throw new HttpRequestException("Nexus's validate endpoint returned an empty response.");

        return new NexusUserInfo(dto.UserId, dto.Name, dto.IsPremium, dto.IsSupporter, dto.Email, dto.ProfileUrl);
    }

    public async Task<IReadOnlyList<NexusUpdateEntry>> GetUpdatedModsAsync(
        string apiKey, string gameDomain, string period, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/games/{gameDomain}/mods/updated?period={period}");
        request.Headers.Add("apikey", apiKey);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException("Nexus rejected the stored API key.");
        }

        response.EnsureSuccessStatusCode();
        var entries = await response.Content.ReadFromJsonAsync<List<UpdateEntryDto>>(cancellationToken) ?? [];
        return [.. entries.Select(e => new NexusUpdateEntry(e.ModId, e.LatestFileUpdate, e.LatestModActivity))];
    }

    private sealed class ValidateResponseDto
    {
        [JsonPropertyName("user_id")]
        public int UserId { get; init; }

        [JsonPropertyName("name")]
        public string Name { get; init; } = "";

        [JsonPropertyName("is_premium")]
        public bool IsPremium { get; init; }

        [JsonPropertyName("is_supporter")]
        public bool IsSupporter { get; init; }

        [JsonPropertyName("email")]
        public string Email { get; init; } = "";

        [JsonPropertyName("profile_url")]
        public string ProfileUrl { get; init; } = "";
    }

    private sealed class UpdateEntryDto
    {
        [JsonPropertyName("mod_id")]
        public int ModId { get; init; }

        [JsonPropertyName("latest_file_update")]
        public long LatestFileUpdate { get; init; }

        [JsonPropertyName("latest_mod_activity")]
        public long LatestModActivity { get; init; }
    }
}
