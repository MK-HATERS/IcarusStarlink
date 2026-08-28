using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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
    private const string GraphQlUrl = "https://api.nexusmods.com/v2/graphql";

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

    public async Task<IReadOnlyList<NexusDownloadLink>> GetDownloadLinksAsync(
        string apiKey, string gameDomain, int modId, int fileId, string? key, long? expires, CancellationToken cancellationToken = default)
    {
        // key/expires are appended together or not at all — matches the official client's own
        // request-building logic exactly (a premium account's API key alone is sufficient; a
        // non-premium account needs both, taken from the nxm:// link itself).
        var url = $"{BaseUrl}/games/{gameDomain}/mods/{modId}/files/{fileId}/download_link";
        if (key is not null && expires is not null)
        {
            url += $"?key={Uri.EscapeDataString(key)}&expires={expires}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("apikey", apiKey);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException("Nexus rejected this download request — the key may be wrong, expired, or you may need to be signed in as a premium member to use manager downloads without one.");
        }

        response.EnsureSuccessStatusCode();
        var links = await response.Content.ReadFromJsonAsync<List<DownloadLinkDto>>(cancellationToken) ?? [];
        return [.. links.Select(l => new NexusDownloadLink(l.Uri, l.Name, l.ShortName))];
    }

    public async Task<NexusModInfo?> GetModInfoAsync(string apiKey, string gameDomain, int modId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/games/{gameDomain}/mods/{modId}");
        request.Headers.Add("apikey", apiKey);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<ModInfoResponseDto>(cancellationToken)
            ?? throw new HttpRequestException("Nexus's mod-info endpoint returned an empty response.");

        return ToModInfo(dto);
    }

    public async Task<IReadOnlyList<NexusModInfo>> GetModListAsync(
        string apiKey, string gameDomain, NexusModList list, CancellationToken cancellationToken = default)
    {
        var path = list switch
        {
            NexusModList.Trending => "trending",
            NexusModList.Latest => "latest_added",
            _ => "latest_updated",
        };

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/games/{gameDomain}/mods/{path}");
        request.Headers.Add("apikey", apiKey);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException("Nexus rejected the stored API key.");
        }

        response.EnsureSuccessStatusCode();
        var dtos = await response.Content.ReadFromJsonAsync<List<ModInfoResponseDto>>(cancellationToken) ?? [];
        return [.. dtos.Select(ToModInfo)];
    }

    // Nexus's own web UI renders "name"/"summary" as HTML, so entities like "&amp;" are real
    // content, not an API quirk — decoded here, at the boundary, rather than leaking raw HTML
    // entities into a plain-text UI (Library's detail pane) that never otherwise deals in HTML.
    private static NexusModInfo ToModInfo(ModInfoResponseDto dto) => new(
        dto.ModId, WebUtility.HtmlDecode(dto.Name), dto.Author, WebUtility.HtmlDecode(dto.Summary), dto.Version, dto.PictureUrl,
        dto.CreatedTime, dto.UpdatedTime);

    public async Task<IReadOnlyList<NexusModFile>> GetModFilesAsync(
        string apiKey, string gameDomain, int modId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/games/{gameDomain}/mods/{modId}/files");
        request.Headers.Add("apikey", apiKey);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException("Nexus rejected the stored API key.");
        }

        response.EnsureSuccessStatusCode();
        // The response wraps the array — { "files": [...], "file_updates": [...] } — per the
        // official client's own IModFiles shape, not a bare array like the browse lists.
        var dto = await response.Content.ReadFromJsonAsync<ModFilesResponseDto>(cancellationToken)
            ?? throw new HttpRequestException("Nexus's mod-files endpoint returned an empty response.");
        return [.. dto.Files.Select(f => new NexusModFile(
            f.FileId, f.FileName, f.Name, f.Version, f.CategoryName, f.IsPrimary,
            CleanHtmlNotes(f.ChangelogHtml) ?? CleanHtmlNotes(f.Description)))];
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetChangelogsAsync(
        string apiKey, string gameDomain, int modId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/games/{gameDomain}/mods/{modId}/changelogs");
        request.Headers.Add("apikey", apiKey);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException("Nexus rejected the stored API key.");
        }

        response.EnsureSuccessStatusCode();
        // The response is a flat { "1.1": ["line one", "line two"], "1.0": [...] } object — no
        // wrapper, unlike GetModFilesAsync's own {"files": [...]} shape — so a plain dictionary
        // deserializes it directly, no DTO type needed.
        var dto = await response.Content.ReadFromJsonAsync<Dictionary<string, List<string>>>(cancellationToken)
            ?? [];
        return dto.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
    }

    /// <summary>Nexus's changelog_html/description fields are real (if simple) HTML — a
    /// &lt;ul&gt;/&lt;li&gt; list or &lt;br&gt;-separated lines, most commonly. This app has no HTML
    /// renderer and only needs plain text here, so tags are stripped rather than rendered — the
    /// same "small hand-rolled subset, not a full parser" approach already used for Markdown
    /// elsewhere in this app.</summary>
    private static string? CleanHtmlNotes(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var withBreaks = Regex.Replace(html, "<br\\s*/?>|</p>|</li>", "\n", RegexOptions.IgnoreCase);
        var textOnly = Regex.Replace(withBreaks, "<[^>]+>", "");
        var decoded = WebUtility.HtmlDecode(textOnly);
        var cleaned = string.Join('\n', decoded.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        return cleaned.Length == 0 ? null : cleaned;
    }

    public async Task<IReadOnlyList<NexusEndorsement>> GetEndorsementsAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/user/endorsements");
        request.Headers.Add("apikey", apiKey);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException("Nexus rejected the stored API key.");
        }

        response.EnsureSuccessStatusCode();
        var dtos = await response.Content.ReadFromJsonAsync<List<EndorsementDto>>(cancellationToken) ?? [];
        return [.. dtos.Select(d => new NexusEndorsement(d.ModId, d.DomainName, ParseEndorsementStatus(d.Status)))];
    }

    public async Task<NexusEndorsementStatus> SetEndorsementAsync(
        string apiKey, string gameDomain, int modId, string modVersion, bool endorse, CancellationToken cancellationToken = default)
    {
        // Path segment is literally "endorse" or "abstain" — confirmed against the official
        // client's own endorseMod, which builds the URL the exact same way.
        var verb = endorse ? "endorse" : "abstain";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/games/{gameDomain}/mods/{modId}/{verb}")
        {
            Content = JsonContent.Create(new { Version = modVersion }),
        };
        request.Headers.Add("apikey", apiKey);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // Unlike a plain read, a 401/403 here doesn't necessarily mean the key itself is bad —
            // Nexus is also known to reject an endorsement for a mod you authored yourself. Same
            // "list every plausible cause" honesty GetDownloadLinksAsync's own message already uses
            // for its own ambiguous 401/403, rather than confidently blaming the key alone.
            throw new InvalidOperationException(
                "Nexus rejected this request — the key may be wrong, or this action may not be allowed (e.g. you can't endorse your own upload).");
        }

        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<EndorseResponseDto>(cancellationToken)
            ?? throw new HttpRequestException("Nexus's endorse endpoint returned an empty response.");
        return ParseEndorsementStatus(dto.Status);
    }

    private static NexusEndorsementStatus ParseEndorsementStatus(string? status) => status switch
    {
        "Endorsed" => NexusEndorsementStatus.Endorsed,
        "Abstained" => NexusEndorsementStatus.Abstained,
        _ => NexusEndorsementStatus.Undecided,
    };

    private sealed class EndorsementDto
    {
        [JsonPropertyName("mod_id")]
        public int ModId { get; init; }

        [JsonPropertyName("domain_name")]
        public string DomainName { get; init; } = "";

        [JsonPropertyName("status")]
        public string Status { get; init; } = "Undecided";
    }

    private sealed class EndorseResponseDto
    {
        [JsonPropertyName("status")]
        public string Status { get; init; } = "Undecided";
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

    private sealed class DownloadLinkDto
    {
        [JsonPropertyName("URI")]
        public string Uri { get; init; } = "";

        [JsonPropertyName("name")]
        public string Name { get; init; } = "";

        [JsonPropertyName("short_name")]
        public string ShortName { get; init; } = "";
    }

    private sealed class ModInfoResponseDto
    {
        [JsonPropertyName("mod_id")]
        public int ModId { get; init; }

        // Absent when the mod is under moderation, per Nexus's own documented IModInfo shape.
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("author")]
        public string Author { get; init; } = "";

        [JsonPropertyName("summary")]
        public string? Summary { get; init; }

        [JsonPropertyName("version")]
        public string Version { get; init; } = "";

        [JsonPropertyName("picture_url")]
        public string? PictureUrl { get; init; }

        [JsonPropertyName("created_time")]
        public DateTimeOffset? CreatedTime { get; init; }

        [JsonPropertyName("updated_time")]
        public DateTimeOffset? UpdatedTime { get; init; }
    }

    public async Task<IReadOnlyList<NexusModInfo>> SearchModsAsync(
        string? apiKey, string gameDomain, string searchText, CancellationToken cancellationToken = default)
    {
        // Nexus's newer v2 GraphQL endpoint — the only place mod search exists at all (v1 has no
        // search endpoint). Query shape and the WILDCARD-as-substring behavior both confirmed by
        // live probing the real endpoint during planning, not guessed; user input travels as a
        // GraphQL VARIABLE, never string-interpolated into the query text, so no quoting/injection
        // concerns. Works unauthenticated (also confirmed live) — the key is attached when
        // available anyway, since authenticated requests get the account's own rate limits.
        var payload = new Dictionary<string, object?>
        {
            ["query"] = "query Search($filter: ModsFilter, $count: Int) { mods(filter: $filter, count: $count) { nodes { modId name version summary pictureUrl author createdAt updatedAt } totalCount } }",
            ["variables"] = new Dictionary<string, object?>
            {
                ["filter"] = new Dictionary<string, object?>
                {
                    ["gameDomainName"] = new Dictionary<string, string> { ["value"] = gameDomain, ["op"] = "EQUALS" },
                    ["name"] = new Dictionary<string, string> { ["value"] = searchText, ["op"] = "WILDCARD" },
                },
                ["count"] = 30,
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, GraphQlUrl) { Content = JsonContent.Create(payload) };
        if (apiKey is not null)
        {
            request.Headers.Add("apikey", apiKey);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<GraphResponseDto>(cancellationToken);

        return [.. (dto?.Data?.Mods?.Nodes ?? []).Select(n => new NexusModInfo(
            n.ModId, WebUtility.HtmlDecode(n.Name), n.Author, WebUtility.HtmlDecode(n.Summary), n.Version, n.PictureUrl,
            n.CreatedAt, n.UpdatedAt))];
    }

    public async Task<NexusModPage> ListAllModsAsync(
        string? apiKey, string gameDomain, int offset, int count, CancellationToken cancellationToken = default)
    {
        // Same v2 GraphQL endpoint as SearchModsAsync, without the name filter: offset/count
        // paging and the endorsements-DESC sort were both confirmed by live probes against the
        // real endpoint (icarus answered 229 total, page slices genuinely advanced) — not guessed.
        var payload = new Dictionary<string, object?>
        {
            ["query"] = "query All($filter: ModsFilter, $count: Int, $offset: Int, $sort: [ModsSort!]) { mods(filter: $filter, count: $count, offset: $offset, sort: $sort) { nodes { modId name version summary pictureUrl author createdAt updatedAt } totalCount } }",
            ["variables"] = new Dictionary<string, object?>
            {
                ["filter"] = new Dictionary<string, object?>
                {
                    ["gameDomainName"] = new Dictionary<string, string> { ["value"] = gameDomain, ["op"] = "EQUALS" },
                },
                ["count"] = count,
                ["offset"] = offset,
                ["sort"] = new[] { new Dictionary<string, object?> { ["endorsements"] = new Dictionary<string, string> { ["direction"] = "DESC" } } },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, GraphQlUrl) { Content = JsonContent.Create(payload) };
        if (apiKey is not null)
        {
            request.Headers.Add("apikey", apiKey);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<GraphResponseDto>(cancellationToken);

        var mods = (dto?.Data?.Mods?.Nodes ?? []).Select(n => new NexusModInfo(
            n.ModId, WebUtility.HtmlDecode(n.Name), n.Author, WebUtility.HtmlDecode(n.Summary), n.Version, n.PictureUrl,
            n.CreatedAt, n.UpdatedAt)).ToList();
        return new NexusModPage(mods, dto?.Data?.Mods?.TotalCount ?? mods.Count);
    }

    private sealed class ModFilesResponseDto
    {
        [JsonPropertyName("files")]
        public List<ModFileDto> Files { get; init; } = [];
    }

    private sealed class ModFileDto
    {
        [JsonPropertyName("file_id")]
        public int FileId { get; init; }

        [JsonPropertyName("file_name")]
        public string FileName { get; init; } = "";

        [JsonPropertyName("name")]
        public string Name { get; init; } = "";

        [JsonPropertyName("version")]
        public string Version { get; init; } = "";

        [JsonPropertyName("category_name")]
        public string? CategoryName { get; init; }

        [JsonPropertyName("is_primary")]
        public bool IsPrimary { get; init; }

        [JsonPropertyName("changelog_html")]
        public string? ChangelogHtml { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }
    }

    // The v2 GraphQL response wrapper — camelCase field names, unlike v1's snake_case.
    private sealed class GraphResponseDto
    {
        [JsonPropertyName("data")]
        public GraphDataDto? Data { get; init; }
    }

    private sealed class GraphDataDto
    {
        [JsonPropertyName("mods")]
        public GraphModsDto? Mods { get; init; }
    }

    private sealed class GraphModsDto
    {
        [JsonPropertyName("nodes")]
        public List<GraphModDto> Nodes { get; init; } = [];

        [JsonPropertyName("totalCount")]
        public int TotalCount { get; init; }
    }

    private sealed class GraphModDto
    {
        [JsonPropertyName("modId")]
        public int ModId { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("version")]
        public string Version { get; init; } = "";

        [JsonPropertyName("summary")]
        public string? Summary { get; init; }

        [JsonPropertyName("pictureUrl")]
        public string? PictureUrl { get; init; }

        [JsonPropertyName("author")]
        public string Author { get; init; } = "";

        [JsonPropertyName("createdAt")]
        public DateTimeOffset? CreatedAt { get; init; }

        [JsonPropertyName("updatedAt")]
        public DateTimeOffset? UpdatedAt { get; init; }
    }
}
