using System.Net.Http;

namespace IcarusStarlink.Core.IO;

/// <summary>How far a download has got: bytes written so far, and the whole size when the server said (Content-Length).</summary>
public readonly record struct DownloadProgress(long BytesReceived, long? TotalBytes);

/// <summary>
/// Streams an HTTP GET's response body straight to a file, rather than buffering the whole body in
/// memory first. The one place downloads are written to disk: Nexus and IMM Database downloads,
/// SteamCMD, UnrealPak and UE4SS used to each do it by hand (a review on 2026-10-23), some with no
/// .part file, one buffering the whole file in memory, none noticing a stalled connection.
/// </summary>
public static class HttpFileDownloader
{
    /// <summary>How long a download may go without a single byte arriving before it counts as stalled.</summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Progress is reported at most this often (plus the first chunk and the end), not once per
    /// 80 KB buffer — a fast connection fills hundreds of buffers a second, and each report is a
    /// UI update on the Downloads page.
    /// </summary>
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Downloads to <paramref name="destinationPath"/> from the first server that answers (see
    /// GetFromFirstAnsweringAsync), through a .part file renamed into place only once complete — so
    /// a failure never leaves a truncated file, or truncates an earlier good copy at that path.
    /// </summary>
    /// <param name="idleTimeout">The body isn't covered by HttpClient.Timeout (ResponseHeadersRead), so
    /// a connection that stops sending would otherwise hang forever: no data for this long throws a
    /// TimeoutException. The clock restarts on every chunk, so a slow but moving download is fine.</param>
    public static Task<string> DownloadToFileAsync(
        HttpClient client, IReadOnlyList<string> requestUris, string destinationPath, TimeSpan idleTimeout,
        CancellationToken cancellationToken = default, IProgress<DownloadProgress>? progress = null) =>
        DownloadToFileAsync(client, requestUris, _ => destinationPath, idleTimeout, cancellationToken, progress);

    /// <param name="chooseDestination">Picks the final path once a server has answered — a Nexus
    /// download is named by the response's own Content-Disposition header.</param>
    /// <param name="progress">Told the bytes written so far (and the size, when known), throttled
    /// to ProgressInterval. Reported on whatever thread the copy runs on; a UI caller marshals.</param>
    /// <returns>The path the file was saved to.</returns>
    public static async Task<string> DownloadToFileAsync(
        HttpClient client, IReadOnlyList<string> requestUris, Func<HttpResponseMessage, string> chooseDestination, TimeSpan idleTimeout,
        CancellationToken cancellationToken = default, IProgress<DownloadProgress>? progress = null)
    {
        using var response = await GetFromFirstAnsweringAsync(client, requestUris, cancellationToken);
        var destinationPath = chooseDestination(response);
        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var partPath = $"{destinationPath}.{Guid.NewGuid():N}.part";
        try
        {
            await using (var file = File.Create(partPath))
            {
                await CopyWithIdleTimeoutAsync(response.Content, file, idleTimeout, progress, cancellationToken);
            }

            File.Move(partPath, destinationPath, overwrite: true);
            return destinationPath;
        }
        finally
        {
            // Guarded: a .part locked by antivirus must not replace the real error (a network
            // failure, say) with "can't access the file".
            try
            {
                if (File.Exists(partPath))
                {
                    File.Delete(partPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Copies the body chunk by chunk, throwing TimeoutException when no chunk arrives within idleTimeout.</summary>
    private static async Task CopyWithIdleTimeoutAsync(
        HttpContent content, Stream destination, TimeSpan idleTimeout, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(idleTimeout);
        // The size is the server's word only: a missing or nonsense Content-Length shows as unknown.
        var total = content.Headers.ContentLength is > 0 and var length ? length : (long?)null;
        long received = 0;
        long lastReport = 0;
        var reportedOnce = false;
        try
        {
            await using var source = await content.ReadAsStreamAsync(idle.Token);
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, idle.Token)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                idle.CancelAfter(idleTimeout);
                received += read;
                if (progress is not null && (!reportedOnce || System.Diagnostics.Stopwatch.GetElapsedTime(lastReport) >= ProgressInterval))
                {
                    reportedOnce = true;
                    lastReport = System.Diagnostics.Stopwatch.GetTimestamp();
                    progress.Report(new DownloadProgress(received, total));
                }
            }

            progress?.Report(new DownloadProgress(received, total));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The download stalled — nothing arrived for {idleTimeout.TotalSeconds:0} seconds. Try again.");
        }
    }

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
