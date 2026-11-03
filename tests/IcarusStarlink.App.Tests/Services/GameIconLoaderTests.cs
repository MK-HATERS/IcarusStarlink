using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IcarusStarlink.App.Services;
using IcarusStarlink.PakIO.Assets;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>The Editor list's icons: decoded once each, newest request first, older ones dropped past the queue's limit.</summary>
public sealed class GameIconLoaderTests
{
    private static readonly byte[] Png = MakePng();

    private static byte[] MakePng()
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Records each decode; holds the first one until released, so requests pile up behind it.</summary>
    private sealed class FakeDecoder(bool holdFirst = false) : IBaseGameIconDecoder
    {
        private readonly ManualResetEventSlim _release = new(!holdFirst);
        public readonly ManualResetEventSlim Started = new();
        public readonly ConcurrentQueue<string> Decoded = new();

        public void Release() => _release.Set();

        public byte[]? TryDecodeIconToPng(string? gameIconPath)
        {
            Started.Set();
            _release.Wait(TimeSpan.FromSeconds(10));
            Decoded.Enqueue(gameIconPath!);
            return gameIconPath!.Contains("broken") ? null : Png;
        }
    }

    private static Task<ImageSource> Load(GameIconLoader loader, string path, Action? dropped = null)
    {
        var done = new TaskCompletionSource<ImageSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        loader.Request(path, image => done.TrySetResult(image), dropped ?? (() => { }));
        return done.Task;
    }

    [Fact]
    public async Task AnIcon_IsDecodedOnce_ThenComesFromTheCache()
    {
        var decoder = new FakeDecoder();
        var loader = new GameIconLoader(decoder);

        var first = await Load(loader, "/Game/A.A").WaitAsync(TimeSpan.FromSeconds(10));
        var second = await Load(loader, "/Game/A.A").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Same(first, second);
        Assert.Equal(["/Game/A.A"], decoder.Decoded);
    }

    /// <summary>The newest request goes first (the rows on screen now); past MaxQueued the oldest waiting gives way and is told so, to be asked again when its row shows.</summary>
    [Fact]
    public async Task NewestRequestsGoFirst_AndTheOldestWaitingAreDropped()
    {
        var decoder = new FakeDecoder(holdFirst: true);
        var loader = new GameIconLoader(decoder);
        var busy = Load(loader, "/Game/Busy.Busy");
        Assert.True(decoder.Started.Wait(TimeSpan.FromSeconds(10)));

        var dropped = new ConcurrentBag<string>();
        var waiting = new List<Task<ImageSource>>();
        for (var i = 0; i <= GameIconLoader.MaxQueued; i++)
        {
            var path = $"/Game/I{i}.I{i}";
            waiting.Add(Load(loader, path, () => dropped.Add(path)));
        }

        decoder.Release();
        await busy.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.WhenAll(waiting.Skip(1)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["/Game/I0.I0"], dropped);
        Assert.Equal($"/Game/I{GameIconLoader.MaxQueued}.I{GameIconLoader.MaxQueued}", decoder.Decoded.Skip(1).First());
        Assert.DoesNotContain("/Game/I0.I0", decoder.Decoded);
    }

    [Fact]
    public async Task AnIconThatDoesntDecode_IsNeverDelivered_AndNotTriedAgain()
    {
        var decoder = new FakeDecoder();
        var loader = new GameIconLoader(decoder);

        var broken = Load(loader, "/Game/broken.broken");
        Assert.True(SpinWait.SpinUntil(() => decoder.Decoded.Contains("/Game/broken.broken"), TimeSpan.FromSeconds(10)));
        _ = Load(loader, "/Game/broken.broken");
        await Load(loader, "/Game/Fine.Fine").WaitAsync(TimeSpan.FromSeconds(10)); // decoded after the broken one's result was kept

        Assert.False(broken.IsCompleted);
        Assert.Single(decoder.Decoded, p => p == "/Game/broken.broken");
    }
}
