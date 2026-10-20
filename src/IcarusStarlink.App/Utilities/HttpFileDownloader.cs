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

    /// <summary>
    /// Tries each download server in turn and returns the first one that answers with success (the
    /// caller disposes it). Nexus lists several servers for every file; only the first used to be
    /// tried, so one server timing out (seen live: la-premium.nexus-cdn.com) failed the download
    /// while the others were fine. Throws an HttpRequestException naming the last error when none answer.
    /// </summary>
    public static async Task<HttpResponseMessage> GetFromFirstAnsweringAsync(
        HttpClient client, IReadOnlyList<string> requestUris, CancellationToken cancellationToken = default)
    {
        Exception? lastError = null;
        foreach (var uri in requestUris)
        {
            try
            {
                var response = await GetWithStreamingResponseAsync(client, uri, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                lastError = new HttpRequestException($"{new Uri(uri).Host} answered {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);
                response.Dispose();
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // HttpClient's own timeout, not the caller cancelling.
                lastError = ex;
            }
        }

        if (lastError is null)
        {
            throw new HttpRequestException("No download server was given.");
        }

        throw new HttpRequestException(
            requestUris.Count > 1 ? $"None of the {requestUris.Count} download servers answered. Last error: {lastError.Message}" : lastError.Message,
            lastError);
    }
}
