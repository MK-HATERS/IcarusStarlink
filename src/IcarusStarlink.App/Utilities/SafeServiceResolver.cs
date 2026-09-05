using Microsoft.Extensions.DependencyInjection;

namespace IcarusStarlink.App.Utilities;

/// <summary>
/// GetService on a disposed IServiceProvider throws ObjectDisposedException — a real risk for
/// App.xaml.cs's own global exception handlers (DispatcherUnhandledException, AppDomain.
/// UnhandledException, TaskScheduler.UnobservedTaskException), which resolve IActivityLog from the
/// DI host to enrich a crash report. Those handlers' own `_host?.` null-check only guards _host being
/// null (a crash early enough in startup that the host was never built) — not _host being disposed
/// but still non-null (a crash late enough, e.g. during shutdown, that OnExit's _host?.Dispose()
/// already ran), which is exactly the case this closes: the exception would otherwise escape as an
/// argument-evaluation failure before CrashReportWriter.Write was ever even called, losing the crash
/// report entirely.
/// </summary>
public static class SafeServiceResolver
{
    public static T? TryGetService<T>(IServiceProvider? services) where T : class
    {
        try
        {
            return services?.GetService<T>();
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }
}
