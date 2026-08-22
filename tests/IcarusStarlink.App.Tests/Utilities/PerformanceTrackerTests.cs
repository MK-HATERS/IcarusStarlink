using IcarusStarlink.App.Utilities;
using IcarusStarlink.Core.Settings;

namespace IcarusStarlink.App.Tests.Utilities;

public class PerformanceTrackerTests : IDisposable
{
    private readonly string _logsDirectory = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_logsDirectory))
        {
            Directory.Delete(_logsDirectory, recursive: true);
        }
    }

    private sealed class FakeSettingsService(bool performanceTrackingEnabled) : ISettingsService
    {
        public AppSettings Current { get; } = new() { PerformanceTrackingEnabled = performanceTrackingEnabled };

        public bool Save() => true;
    }

    [Fact]
    public void Track_DisabledSetting_DoesNotCreateALogFile()
    {
        var tracker = new PerformanceTracker(new FakeSettingsService(performanceTrackingEnabled: false), _logsDirectory);

        using (tracker.Track("SomeOperation"))
        {
        }

        Assert.False(Directory.Exists(_logsDirectory));
    }

    [Fact]
    public void Track_EnabledSetting_WritesALineWithTheOperationNameOnDispose()
    {
        var tracker = new PerformanceTracker(new FakeSettingsService(performanceTrackingEnabled: true), _logsDirectory);

        using (tracker.Track("SomeOperation"))
        {
        }

        var perfLogFile = Assert.Single(Directory.GetFiles(_logsDirectory, "app.perf-*.log"));
        var content = File.ReadAllText(perfLogFile);
        Assert.Contains("SomeOperation", content);
        Assert.Contains("ms", content);
    }

    [Fact]
    public void Track_EnabledSetting_MultipleOperationsAppendRatherThanOverwrite()
    {
        var tracker = new PerformanceTracker(new FakeSettingsService(performanceTrackingEnabled: true), _logsDirectory);

        using (tracker.Track("FirstOperation"))
        {
        }
        using (tracker.Track("SecondOperation"))
        {
        }

        var perfLogFile = Assert.Single(Directory.GetFiles(_logsDirectory, "app.perf-*.log"));
        var lines = File.ReadAllLines(perfLogFile);
        Assert.Equal(2, lines.Length);
        Assert.Contains(lines, l => l.Contains("FirstOperation"));
        Assert.Contains(lines, l => l.Contains("SecondOperation"));
    }
}
