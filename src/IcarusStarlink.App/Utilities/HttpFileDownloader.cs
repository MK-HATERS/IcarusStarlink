using System.Net.Http;

namespace IcarusStarlink.App.Utilities;

/// <summary>
/// Streams an HTTP GET's response body straight to a file, rather than buffering the whole body in
/// memory first. Pulled out of DownloadsViewModel.FetchAndDownloadAsync (its one real caller) purely
/// so this exact behavior — ResponseHeadersRead vs. the default ResponseContentRead — can be tested
/// with just an HttpClient, no ViewModel construction required.
/// </summary>
public static class HttpFileDownloader
{
    /// <summary>
    /// ResponseHeadersRead — without it, HttpClient.GetAsync buffers the ENTIRE response body into
    /// memory before returning, doubling peak memory for a large mod download and delaying every
    /// byte reaching disk until the whole thing has already downloaded once into RAM. With it, the
    /// body streams straight from the socket into the file write below as it arrives.
    /// </summary>
    public static async Task<HttpResponseMessage> GetWithStreamingResponseAsync(
        HttpClient client, string requestUri, CancellationToken cancellationToken = default) =>
        await client.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
}
