using System.IO;
using System.Net;
using System.Net.Http;
using IcarusStarlink.App.Utilities;

namespace IcarusStarlink.App.Tests.Utilities;

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
