using System.Net;

namespace IcarusStarlink.Catalog.Tests;

/// <summary>
/// Routes by exact request URL to a canned response body — enough for testing the catalog clients
/// without a real network call. statusCodesByUrl is optional (defaults every mapped URL to 200
/// OK) — added for NexusApiClient's tests, which need to simulate a real 401 response with a body,
/// not just "not found".
/// </summary>
internal sealed class FakeHttpMessageHandler(Dictionary<string, string> responsesByUrl, Dictionary<string, HttpStatusCode>? statusCodesByUrl = null) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        if (!responsesByUrl.TryGetValue(url, out var body))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        var statusCode = statusCodesByUrl is not null && statusCodesByUrl.TryGetValue(url, out var code) ? code : HttpStatusCode.OK;
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        return Task.FromResult(response);
    }
}
