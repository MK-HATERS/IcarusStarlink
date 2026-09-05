using IcarusStarlink.App.Utilities;
using Microsoft.Extensions.DependencyInjection;

namespace IcarusStarlink.App.Tests.Utilities;

/// <summary>
/// App.xaml.cs's own OnStartup/exception handlers have zero test coverage in this codebase (WPF's
/// Application/Dispatcher lifecycle can't run in a headless xunit host) — this is the extracted,
/// WPF-free piece of their own real fix: without it, GetService on the DI host's ServiceProvider
/// AFTER OnExit's own _host?.Dispose() already ran throws ObjectDisposedException as an
/// argument-evaluation failure, before CrashReportWriter.Write is even called — silently losing the
/// crash report for any unhandled exception that fires during shutdown.
/// </summary>
public class SafeServiceResolverTests
{
    private interface IWidget;
    private sealed class Widget : IWidget;

    [Fact]
    public void TryGetService_LiveProvider_ResolvesNormally()
    {
        var services = new ServiceCollection().AddSingleton<IWidget, Widget>().BuildServiceProvider();

        var result = SafeServiceResolver.TryGetService<IWidget>(services);

        Assert.NotNull(result);
    }

    [Fact]
    public void TryGetService_NullProvider_ReturnsNullInsteadOfThrowing()
    {
        var result = SafeServiceResolver.TryGetService<IWidget>(null);

        Assert.Null(result);
    }

    /// <summary>Regression guard: a disposed ServiceProvider's own GetService throws ObjectDisposedException — this must degrade to null, not let that exception escape.</summary>
    [Fact]
    public void TryGetService_DisposedProvider_ReturnsNullInsteadOfThrowing()
    {
        var services = new ServiceCollection().AddSingleton<IWidget, Widget>().BuildServiceProvider();
        services.Dispose();

        var result = SafeServiceResolver.TryGetService<IWidget>(services);

        Assert.Null(result);
    }
}
