using System.IO;
using System.Net;
using System.Net.Http;
using IcarusStarlink.Core.IO;

namespace IcarusStarlink.Core.Tests.IO;

/// <summary>
/// Regression guard: DownloadsViewModel.FetchAndDownloadAsync used to call HttpClient.GetAsync with
/// its default HttpCompletionOption (ResponseContentRead) — the whole response body gets buffered
/// into memory before GetAsync's own Task even completes, doubling peak memory for a large mod
/// download and delaying every byte reaching disk until the whole thing already downloaded once into
/// RAM. GatedContent below never finishes being read until the test releases it — with
/// ResponseHeadersRead, GetAsync still completes (headers alone are enough); with the old default,
/// it would hang forever waiting for a body that never finishes.
/// </summary>
public class HttpFileDownloaderTests
{
    [Fact]
    public async Task GetWithStreamingResponseAsync_ResponseBodyStillStreaming_CompletesOnHeadersAlone()
    {
        var gatedContent = new GatedContent();
        using var client = new HttpClient(new StaticResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = gatedContent }));

        var responseTask = HttpFileDownloader.GetWithStreamingResponseAsync(client, "https://example.invalid/mod.zip");
        var completed = await Task.WhenAny(responseTask, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(responseTask, completed);
        using var response = await responseTask;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        gatedContent.Release();
    }

    [Fact]
    public async Task GetFromFirstAnsweringAsync_FirstServerTimesOut_UsesTheNextOne()
    {
        var asked = new List<string>();
        using var client = new HttpClient(new PerHostHandler(request =>
        {
            asked.Add(request.RequestUri!.Host);
            return request.RequestUri.Host switch
            {
                "la.example" => throw new HttpRequestException("A connection attempt failed (la.example:443)"),
                "slow.example" => throw new TaskCanceledException("timed out"),
                "gone.example" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([7]) },
            };
        }));

        using var response = await HttpFileDownloader.GetFromFirstAnsweringAsync(client,
            ["https://la.example/f.zip", "https://slow.example/f.zip", "https://gone.example/f.zip", "https://ok.example/f.zip", "https://never.example/f.zip"]);

        Assert.Equal("ok.example", response.RequestMessage!.RequestUri!.Host);
        Assert.Equal(["la.example", "slow.example", "gone.example", "ok.example"], asked);
    }

    [Fact]
    public async Task GetFromFirstAnsweringAsync_NoServerAnswers_SaysSoWithTheLastError()
    {
        using var client = new HttpClient(new PerHostHandler(request =>
            throw new HttpRequestException($"A connection attempt failed ({request.RequestUri!.Host}:443)")));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            HttpFileDownloader.GetFromFirstAnsweringAsync(client, ["https://a.example/f.zip", "https://b.example/f.zip"]));

        Assert.Contains("None of the 2 download servers answered", ex.Message);
        Assert.Contains("b.example:443", ex.Message);
    }

    [Fact]
    public async Task GetFromFirstAnsweringAsync_CallerCancels_StopsInsteadOfTryingTheNextServer()
    {
        using var cts = new CancellationTokenSource();
        var asked = 0;
        using var client = new HttpClient(new PerHostHandler(_ =>
        {
            asked++;
            cts.Cancel();
            throw new TaskCanceledException();
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HttpFileDownloader.GetFromFirstAnsweringAsync(client, ["https://a.example/f.zip", "https://b.example/f.zip"], cts.Token));
        Assert.Equal(1, asked);
    }

    private sealed class PerHostHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class StaticResponseHandler(Func<HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder());
    }

    /// <summary>HttpContent whose stream never signals end-of-data until the test calls Release — mirrors a real large in-flight download body.</summary>
    private sealed class GatedContent : HttpContent
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _released.SetResult();

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(new byte[] { 1, 2, 3 });
            await _released.Task;
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
