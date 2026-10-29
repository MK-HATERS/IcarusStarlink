using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Catalog;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>The Downloads page's "Downloading" list (bytes, size, per-download Cancel) and Discard's question for installed downloads.</summary>
public sealed partial class DownloadsViewModelTests
{
    private static CatalogEntry PakEntry(string id, string name) =>
        new(CatalogSource.Jimk72, $"jimk72:{id}", name, "jimk72", "1.0", "D", "", null, null, null,
            PakUrl: $"https://example.com/{id}.pak", ExmodzUrl: null, Categories: []);

    /// <summary>Polls until the condition holds (the download runs on its own), failing after a few seconds.</summary>
    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), $"Timed out waiting for: {what}");
    }

    [Fact]
    public async Task CatalogDownload_WhileRunning_ShowsItsRowWithBytesAndSize_AndTheRowGoesWhenItIsIn()
    {
        var handler = new GatedHandler(firstChunk: 1000, total: 3000);
        var harness = new Harness(_root) { HttpHandler = handler };
        var vm = harness.Build();
        Assert.False(vm.HasActiveDownloads);

        var download = vm.DownloadAndExtractCommand.ExecuteAsync(new CatalogEntryViewModel(PakEntry("stacks", "Bigger Stacks"), null, null));
        await WaitUntil(() => vm.ActiveDownloads.FirstOrDefault()?.BytesReceived == 1000, "the first chunk on the row");

        var row = Assert.Single(vm.ActiveDownloads);
        Assert.True(vm.HasActiveDownloads);
        Assert.Equal("Bigger Stacks", row.Name);
        Assert.Equal(3000, row.TotalBytes);
        Assert.False(row.IsIndeterminate);
        Assert.Equal(100d / 3, row.Percent, precision: 3);
        Assert.Equal($"{0.0:0.0} of {0.0:0.0} MB", row.SizeText);
        Assert.True(row.CanCancel);

        handler.Body("stacks").Release();
        await download;

        Assert.Empty(vm.ActiveDownloads);
        Assert.False(vm.HasActiveDownloads);
        Assert.False(vm.IsDownloading);
        Assert.Single(harness.PendingStore.Entries);
    }

    /// <summary>A row's own Cancel stops that download only; the other one keeps going and lands.</summary>
    [Fact]
    public async Task CatalogDownload_CancelOnOneRow_StopsThatDownloadOnly()
    {
        var handler = new GatedHandler(firstChunk: 10, total: 20);
        var harness = new Harness(_root) { HttpHandler = handler };
        var vm = harness.Build();

        var first = vm.DownloadCatalogFileAsync(PakEntry("first", "First"));
        var second = vm.DownloadCatalogFileAsync(PakEntry("second", "Second"));
        await WaitUntil(() => vm.ActiveDownloads.Count == 2 && vm.ActiveDownloads.All(r => r.BytesReceived == 10), "both rows started");

        vm.ActiveDownloads.Single(r => r.Name == "First").CancelCommand.Execute(null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        var remaining = Assert.Single(vm.ActiveDownloads);
        Assert.Equal("Second", remaining.Name);
        Assert.True(vm.IsDownloading);

        handler.Body("second").Release();
        var landed = await second;

        Assert.Equal("Second", landed.DisplayName);
        Assert.Empty(vm.ActiveDownloads);
        Assert.False(vm.IsDownloading);
        Assert.Equal(["Second"], harness.PendingStore.Entries.Select(e => e.DisplayName));
    }

    /// <summary>Cancel downloads still stops every row (the rows' tokens are linked to it).</summary>
    [Fact]
    public async Task CatalogDownload_CancelDownloads_StopsEveryRow()
    {
        var handler = new GatedHandler(firstChunk: 10, total: 20);
        var vm = new Harness(_root) { HttpHandler = handler }.Build();

        var download = vm.DownloadAndExtractCommand.ExecuteAsync(new CatalogEntryViewModel(PakEntry("a", "Alpha"), null, null));
        await WaitUntil(() => vm.ActiveDownloads.Count == 1, "the row");

        vm.CancelDownloadsCommand.Execute(null);
        await download;

        Assert.Empty(vm.ActiveDownloads);
        Assert.Contains("Cancelled downloading 'Alpha'", vm.CatalogStatusMessage);
    }

    [Fact]
    public async Task CatalogDownload_Fails_TheRowGoesAndTheFailureIsSaid()
    {
        var handler = new GatedHandler(firstChunk: 10, total: 20);
        var vm = new Harness(_root) { HttpHandler = handler }.Build();

        var download = vm.DownloadAndExtractCommand.ExecuteAsync(new CatalogEntryViewModel(PakEntry("a", "Alpha"), null, null));
        await WaitUntil(() => vm.ActiveDownloads.FirstOrDefault()?.BytesReceived == 10, "the first chunk");

        handler.Body("a").Fail();
        await download;

        Assert.Empty(vm.ActiveDownloads);
        Assert.Contains("'Alpha' failed", vm.CatalogStatusMessage);
    }

    [Fact]
    public void DownloadRow_SizeText_ReadsPlainly()
    {
        using var row = new ActiveDownloadViewModel("Mod", CancellationToken.None);
        Assert.Equal("Starting…", row.SizeText);
        Assert.True(row.IsIndeterminate);

        row.Apply(new IcarusStarlink.Core.IO.DownloadProgress(12_897_485, null));
        Assert.Equal($"{12.3:0.0} MB", row.SizeText);
        Assert.True(row.IsIndeterminate);

        row.Apply(new IcarusStarlink.Core.IO.DownloadProgress(12_897_485, 47_815_065));
        Assert.Equal($"{12.3:0.0} of {45.6:0.0} MB", row.SizeText);
        Assert.False(row.IsIndeterminate);

        row.MarkInstalling();
        Assert.Equal("Installing…", row.SizeText);
        Assert.True(row.IsIndeterminate);
        Assert.False(row.CanCancel);
        Assert.False(row.CancelCommand.CanExecute(null));
    }

    // --- Discard: asks only for installed downloads ---

    /// <summary>Two UE4SS-only downloads, installed through a first view model (no dialogs), and one never installed.</summary>
    private (Harness Harness, PendingDownloadItemViewModel A, PendingDownloadItemViewModel B, PendingDownloadItemViewModel NotInstalled) InstalledDownloads()
    {
        var harness = new Harness(_root);
        var a = harness.AddDownload("PartA.zip", z => WriteEntry(z, "PartA/Scripts/main.lua", "a"), modId: 501, fileId: 1);
        var b = harness.AddDownload("PartB.zip", z => WriteEntry(z, "PartB/Scripts/main.lua", "b"), modId: 502, fileId: 2);
        var c = harness.AddDownload("PartC.zip", z => WriteEntry(z, "PartC/Scripts/main.lua", "c"), modId: 503, fileId: 3);
        var installer = harness.Build();
        installer.ActivateAsync(installer.PendingDownloads.Single(p => p.ModId == 501)).GetAwaiter().GetResult();
        installer.ActivateAsync(installer.PendingDownloads.Single(p => p.ModId == 502)).GetAwaiter().GetResult();
        Assert.Equal(["PartA", "PartB"], harness.Ue4ss.GetAll().Order());
        return (harness, a, b, c);
    }

    [Fact]
    public void Discard_InstalledDownload_AsksFirst_AndNoDeletesNothing()
    {
        var (harness, a, _, _) = InstalledDownloads();
        harness.Dialogs = new FakeDialogService(false);
        var vm = harness.Build();

        vm.DiscardPendingDownloadCommand.Execute(vm.PendingDownloads.Single(p => p.ModId == 501));

        var question = Assert.Single(harness.Dialogs.ConfirmMessages);
        Assert.Contains("Reinstall original won't be able to put this mod back as its author made it", question);
        Assert.Contains("stays in your Library", question);
        Assert.True(File.Exists(a.LocalFilePath));
        Assert.Contains(harness.PendingStore.Entries, e => e.ModId == 501);
    }

    [Fact]
    public void Discard_InstalledDownload_Yes_DeletesTheFileAndKeepsTheMod()
    {
        var (harness, a, _, _) = InstalledDownloads();
        harness.Dialogs = new FakeDialogService(true);
        var vm = harness.Build();

        vm.DiscardPendingDownloadCommand.Execute(vm.PendingDownloads.Single(p => p.ModId == 501));

        Assert.Single(harness.Dialogs.ConfirmMessages);
        Assert.False(File.Exists(a.LocalFilePath));
        Assert.DoesNotContain(harness.PendingStore.Entries, e => e.ModId == 501);
        Assert.Contains("PartA", harness.Ue4ss.GetAll());
    }

    [Fact]
    public void Discard_NeverInstalledDownload_GoesInOneClick()
    {
        var (harness, _, _, notInstalled) = InstalledDownloads();
        harness.Dialogs = new FakeDialogService(false);
        var vm = harness.Build();

        vm.DiscardPendingDownloadCommand.Execute(vm.PendingDownloads.Single(p => p.ModId == 503));

        Assert.Empty(harness.Dialogs.ConfirmMessages);
        Assert.False(File.Exists(notInstalled.LocalFilePath));
        Assert.DoesNotContain(harness.PendingStore.Entries, e => e.ModId == 503);
    }

    /// <summary>Discard selected asks once, naming only the installed ones; No keeps all of them.</summary>
    [Fact]
    public void DiscardSelected_AsksOnceListingTheInstalledOnes_AndNoKeepsThemAll()
    {
        var (harness, a, b, notInstalled) = InstalledDownloads();
        harness.Dialogs = new FakeDialogService(false);
        var vm = harness.Build();
        vm.SetSelectedPendingDownloads([.. vm.PendingDownloads]);
        Assert.True(vm.CanDiscardSelected);

        vm.DiscardSelectedDownloadsCommand.Execute(null);

        var question = Assert.Single(harness.Dialogs.ConfirmMessages);
        Assert.Contains("These 2 are installed", question);
        Assert.Contains($"• {vm.PendingDownloads.Single(p => p.ModId == 501).DisplayName}", question);
        Assert.Contains($"• {vm.PendingDownloads.Single(p => p.ModId == 502).DisplayName}", question);
        Assert.DoesNotContain($"• {vm.PendingDownloads.Single(p => p.ModId == 503).DisplayName}", question);
        Assert.Equal(3, harness.PendingStore.Entries.Count);
        Assert.True(File.Exists(a.LocalFilePath) && File.Exists(b.LocalFilePath) && File.Exists(notInstalled.LocalFilePath));
    }

    [Fact]
    public void DiscardSelected_Yes_DiscardsEveryOneAndTheModsStay()
    {
        var (harness, _, _, _) = InstalledDownloads();
        harness.Dialogs = new FakeDialogService(true);
        var vm = harness.Build();
        vm.SetSelectedPendingDownloads([.. vm.PendingDownloads]);

        vm.DiscardSelectedDownloadsCommand.Execute(null);

        Assert.Single(harness.Dialogs.ConfirmMessages);
        Assert.Empty(harness.PendingStore.Entries);
        Assert.Empty(Directory.GetFiles(harness.PendingDownloadsDirectory));
        Assert.Equal(["PartA", "PartB"], harness.Ue4ss.GetAll().Order());
        Assert.Equal("Discarded 3 downloads.", vm.PendingDownloadStatusMessage);
    }

    /// <summary>Hands every request its own held-back body, found by the URL's file name.</summary>
    private sealed class GatedHandler(int firstChunk, int total) : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, GatedBody> _bodies = new();

        public GatedBody Body(string id) => _bodies[id];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = _bodies.GetOrAdd(Path.GetFileNameWithoutExtension(request.RequestUri!.LocalPath), _ => new GatedBody(firstChunk, total));
            var content = new StreamContent(body);
            content.Headers.ContentLength = total;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request });
        }
    }

    /// <summary>Sends firstChunk bytes at once, then waits for Release (the rest) or Fail (a broken connection); honours cancellation while waiting.</summary>
    private sealed class GatedBody(int firstChunk, int total) : Stream
    {
        private readonly TaskCompletionSource<bool> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _sent;

        public void Release() => _gate.TrySetResult(true);

        public void Fail() => _gate.TrySetResult(false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_sent == total)
            {
                return 0;
            }

            if (_sent >= firstChunk && !await _gate.Task.WaitAsync(cancellationToken))
            {
                throw new IOException("The connection was reset.");
            }

            var count = Math.Min(buffer.Length, (_sent < firstChunk ? firstChunk : total) - _sent);
            buffer.Span[..count].Fill(1);
            _sent += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
