using System.Net;

namespace IcarusStarlink.Catalog.Tests;

/// <summary>Routes by exact request URL to a canned response body — enough for testing the catalog clients without a real network call.</summary>
internal sealed class FakeHttpMessageHandler(Dictionary<string, string> responsesByUrl) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        if (!responsesByUrl.TryGetValue(url, out var body))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        return Task.FromResult(response);
    }
}
