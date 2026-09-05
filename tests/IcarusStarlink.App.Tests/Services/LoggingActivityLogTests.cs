using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Activity;
using Microsoft.Extensions.Logging;

namespace IcarusStarlink.App.Tests.Services;

public class LoggingActivityLogTests
{
    [Fact]
    public void Log_InfoEntry_AddsToInnerCollectionAndLogsAsInformation()
    {
        var inner = new ActivityLog();
        var fakeLogger = new FakeLogger();
        var sut = new LoggingActivityLog(inner, fakeLogger);

        sut.Log("Rebuild completed");

        Assert.Contains(inner.Entries, e => e.Message == "Rebuild completed");
        Assert.Contains(inner.Entries, e => e.Message == "Rebuild completed" && e.Kind == ActivityEntryKind.Info);
        var logged = Assert.Single(fakeLogger.Entries);
        Assert.Equal(LogLevel.Information, logged.Level);
        Assert.Contains("Rebuild completed", logged.Message);
    }

    [Fact]
    public void Log_WarningEntry_LogsAsWarningNotInformation()
    {
        var inner = new ActivityLog();
        var fakeLogger = new FakeLogger();
        var sut = new LoggingActivityLog(inner, fakeLogger);

        sut.Log("Install failed for one file", ActivityEntryKind.Warning);

        var logged = Assert.Single(fakeLogger.Entries);
        Assert.Equal(LogLevel.Warning, logged.Level);
        Assert.Contains("Install failed for one file", logged.Message);
    }

    [Fact]
    public void Entries_ReflectsTheSameCollectionInnerActivityLogExposes()
    {
        // A real caller (e.g. ActivityPanelViewModel binding to IActivityLog.Entries) needs the
        // SAME ObservableCollection instance the wrapped ActivityLog mutates — a copy would never
        // reflect later Log() calls in a live binding.
        var inner = new ActivityLog();
        var sut = new LoggingActivityLog(inner, new FakeLogger());

        Assert.Same(inner.Entries, sut.Entries);
    }

    [Fact]
    public void Log_MultipleEntries_EachOneReachesTheLoggerToo()
    {
        var inner = new ActivityLog();
        var fakeLogger = new FakeLogger();
        var sut = new LoggingActivityLog(inner, fakeLogger);

        sut.Log("First");
        sut.Log("Second", ActivityEntryKind.Success);
        sut.Log("Third", ActivityEntryKind.Warning);

        Assert.Equal(3, fakeLogger.Entries.Count);
        Assert.Equal(3, inner.Entries.Count);
    }

    /// <summary>Hand-written fake, not a mocking framework, matching this project's own established convention — captures every Log call's level and formatted message for assertion.</summary>
    private sealed class FakeLogger : ILogger<LoggingActivityLog>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
