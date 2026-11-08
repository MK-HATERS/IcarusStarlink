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

    [Fact]
    public async Task DownloadToFileAsync_Completes_WritesTheFileAndLeavesNoPartFile()
    {
        var folder = Directory.CreateTempSubdirectory("HttpFileDownloaderTests").FullName;
        try
        {
            using var client = new HttpClient(new StaticResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }));
            var destination = Path.Combine(folder, "sub", "mod.zip");

            var saved = await HttpFileDownloader.DownloadToFileAsync(client, ["https://example.invalid/mod.zip"], destination, TimeSpan.FromSeconds(5));

            Assert.Equal(destination, saved);
            Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(destination));
            Assert.Single(Directory.GetFiles(Path.Combine(folder, "sub")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadToFileAsync_ConnectionStopsSending_TimesOutAndKeepsTheEarlierCopy()
    {
        var folder = Directory.CreateTempSubdirectory("HttpFileDownloaderTests").FullName;
        try
        {
            var destination = Path.Combine(folder, "mod.zip");
            await File.WriteAllBytesAsync(destination, [9]);
            using var client = new HttpClient(new StaticResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) }));

            var download = HttpFileDownloader.DownloadToFileAsync(client, ["https://example.invalid/mod.zip"], destination, TimeSpan.FromMilliseconds(200));
            var finished = await Task.WhenAny(download, Task.Delay(TimeSpan.FromSeconds(10)));

            Assert.Same(download, finished);
            await Assert.ThrowsAsync<TimeoutException>(() => download);
            Assert.Equal([9], await File.ReadAllBytesAsync(destination));
            Assert.Equal([destination], Directory.GetFiles(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadToFileAsync_CallerCancels_ThrowsCancelledNotTimeout()
    {
        var folder = Directory.CreateTempSubdirectory("HttpFileDownloaderTests").FullName;
        try
        {
            using var cts = new CancellationTokenSource();
            using var client = new HttpClient(new StaticResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) }));

            var download = HttpFileDownloader.DownloadToFileAsync(client, ["https://example.invalid/mod.zip"], Path.Combine(folder, "mod.zip"), TimeSpan.FromMinutes(5), cts.Token);
            cts.CancelAfter(100);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
            Assert.Empty(Directory.GetFiles(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// The Downloads page's progress bar: the first chunk and the end are always reported, with the
    /// server's size, and a fast body of a thousand chunks is NOT a thousand UI updates.
    /// </summary>
    [Fact]
    public async Task DownloadToFileAsync_WithProgress_ReportsTheFirstChunkThrottledUpdatesAndTheEnd()
    {
        var folder = Directory.CreateTempSubdirectory("HttpFileDownloaderTests").FullName;
        try
        {
            using var client = new HttpClient(new StaticResponseHandler(() =>
            {
                var content = new StreamContent(new ChunkedStream(chunks: 1000, chunkSize: 10));
                content.Headers.ContentLength = 10_000;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }));
            var reports = new List<DownloadProgress>();

            await HttpFileDownloader.DownloadToFileAsync(client, ["https://example.invalid/mod.zip"], Path.Combine(folder, "mod.zip"), TimeSpan.FromSeconds(5),
                progress: new InlineProgress(reports.Add));

            Assert.Equal(new DownloadProgress(10, 10_000), reports[0]);
            Assert.Equal(new DownloadProgress(10_000, 10_000), reports[^1]);
            Assert.True(reports.Count < 50, $"{reports.Count} reports for 1000 chunks");
            Assert.Equal(10_000, new FileInfo(Path.Combine(folder, "mod.zip")).Length);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Regression: there was no size limit, so a server that never ended its body (and gave no
    /// Content-Length) kept writing the .part file until the disk was full.
    /// </summary>
    [Fact]
    public async Task DownloadToFileAsync_BodyRunsPastTheSizeLimit_StopsAndLeavesNoFile()
    {
        var folder = Directory.CreateTempSubdirectory("HttpFileDownloaderTests").FullName;
        try
        {
            using var client = new HttpClient(new StaticResponseHandler(() =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ChunkedStream(chunks: 10, chunkSize: 1000)) }));

            var ex = await Assert.ThrowsAsync<IOException>(() => HttpFileDownloader.DownloadToFileAsync(
                client, ["https://example.invalid/mod.zip"], Path.Combine(folder, "mod.zip"), TimeSpan.FromSeconds(5), maxBytes: 2500));

            Assert.Contains("too big", ex.Message);
            Assert.Empty(Directory.GetFiles(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A server that says up front the file is over the limit is refused before any of the body is read.</summary>
    [Fact]
    public async Task DownloadToFileAsync_ServerSaysTheFileIsOverTheSizeLimit_RefusesWithoutReadingIt()
    {
        var folder = Directory.CreateTempSubdirectory("HttpFileDownloaderTests").FullName;
        try
        {
            using var client = new HttpClient(new StaticResponseHandler(() =>
            {
                var content = new StreamContent(new UnreadableStream());
                content.Headers.ContentLength = 10_000;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }));

            var ex = await Assert.ThrowsAsync<IOException>(() => HttpFileDownloader.DownloadToFileAsync(
                client, ["https://example.invalid/mod.zip"], Path.Combine(folder, "mod.zip"), TimeSpan.FromSeconds(5), maxBytes: 1000));

            Assert.Contains("too big", ex.Message);
            Assert.Empty(Directory.GetFiles(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A body exactly at the limit is still saved.</summary>
    [Fact]
    public async Task DownloadToFileAsync_BodyExactlyAtTheSizeLimit_IsSaved()
    {
        var folder = Directory.CreateTempSubdirectory("HttpFileDownloaderTests").FullName;
        try
        {
            using var client = new HttpClient(new StaticResponseHandler(() =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ChunkedStream(chunks: 3, chunkSize: 1000)) }));
            var destination = Path.Combine(folder, "mod.zip");

            await HttpFileDownloader.DownloadToFileAsync(client, ["https://example.invalid/mod.zip"], destination, TimeSpan.FromSeconds(5), maxBytes: 3000);

            Assert.Equal(3000, new FileInfo(destination).Length);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>No Content-Length: the size is unknown (null), not zero, so the page shows just the bytes so far.</summary>
    [Fact]
    public async Task DownloadToFileAsync_ServerGivesNoSize_ReportsTheSizeAsUnknown()
    {
        var folder = Directory.CreateTempSubdirectory("HttpFileDownloaderTests").FullName;
        try
        {
            using var client = new HttpClient(new StaticResponseHandler(() =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ChunkedStream(chunks: 3, chunkSize: 5)) }));
            var reports = new List<DownloadProgress>();

            await HttpFileDownloader.DownloadToFileAsync(client, ["https://example.invalid/mod.zip"], Path.Combine(folder, "mod.zip"), TimeSpan.FromSeconds(5),
                progress: new InlineProgress(reports.Add));

            Assert.All(reports, r => Assert.Null(r.TotalBytes));
            Assert.Equal(15, reports[^1].BytesReceived);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Regression: every chunk's read and write went back through the caller's context (the UI
    /// thread), so a UI thread busy for a minute starved the idle timer's re-arm and a moving
    /// download failed as "stalled".
    /// </summary>
    [Fact]
    public async Task DownloadToFileAsync_ChunksDontEachGoThroughTheCallersContext()
    {
        var folder = Directory.CreateTempSubdirectory("HttpFileDownloaderTests").FullName;
        try
        {
            using var client = new HttpClient(new StaticResponseHandler(() =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new PoolChunkedStream(chunks: 30, chunkSize: 10)) }));
            var context = new CountingContext();
            var previous = SynchronizationContext.Current;
            Task<string> download;
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                download = HttpFileDownloader.DownloadToFileAsync(client, ["https://example.invalid/mod.zip"], Path.Combine(folder, "mod.zip"), TimeSpan.FromSeconds(5));
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }

            await download;

            Assert.Equal(300, new FileInfo(Path.Combine(folder, "mod.zip")).Length);
            Assert.True(context.Posts < 10, $"{context.Posts} posts to the caller's context for 30 chunks");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Stands in for the UI thread: counts each continuation posted to it, then runs it on the pool with itself current.</summary>
    private sealed class CountingContext : SynchronizationContext
    {
        private int _posts;

        public int Posts => Volatile.Read(ref _posts);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _posts);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var before = Current;
                SetSynchronizationContext(this);
                try
                {
                    d(state);
                }
                finally
                {
                    SetSynchronizationContext(before);
                }
            });
        }
    }

    /// <summary>Like ChunkedStream, but each read finishes later on a pool thread, as a socket read does.</summary>
    private sealed class PoolChunkedStream(int chunks, int chunkSize) : Stream
    {
        private int _sent;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(Task.Run(() =>
            {
                if (_sent == chunks)
                {
                    return 0;
                }

                _sent++;
                buffer.Span[..chunkSize].Fill(7);
                return chunkSize;
            }));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().Result;
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class InlineProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }

    /// <summary>A body of many small chunks that arrive at once (no seek, so no length of its own).</summary>
    private sealed class ChunkedStream(int chunks, int chunkSize) : Stream
    {
        private int _sent;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_sent == chunks)
            {
                return ValueTask.FromResult(0);
            }

            _sent++;
            buffer.Span[..chunkSize].Fill(7);
            return ValueTask.FromResult(chunkSize);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().Result;
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A body that fails the test's download if anything reads it.</summary>
    private sealed class UnreadableStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The body was read.");

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The body was read.");

        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("The body was read.");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A body that sends one byte, then nothing ever again — a connection gone quiet.</summary>
    private sealed class StallingStream : Stream
    {
        private bool _sentFirst;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sentFirst)
            {
                _sentFirst = true;
                buffer.Span[0] = 1;
                return 1;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
