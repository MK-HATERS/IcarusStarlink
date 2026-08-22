using IcarusStarlink.App.Utilities;

namespace IcarusStarlink.App.Tests.Utilities;

public class DebounceTimerTests
{
    [Fact]
    public void Constructor_DoesNotRunTheActionImmediately()
    {
        var ran = false;
        _ = new DebounceTimer(TimeSpan.FromMilliseconds(500), () => ran = true);

        Assert.False(ran);
    }

    [Fact]
    public void Constructor_IsNotRunningUntilRestartIsCalled()
    {
        var timer = new DebounceTimer(TimeSpan.FromMilliseconds(500), () => { });

        Assert.False(timer.IsRunning);
    }

    [Fact]
    public void Restart_MakesTheTimerRunning()
    {
        var timer = new DebounceTimer(TimeSpan.FromMilliseconds(500), () => { });

        timer.Restart();

        Assert.True(timer.IsRunning);
    }

    [Fact]
    public void Restart_CalledAgain_StaysRunning()
    {
        // The real point of Restart (called on every keystroke/edit) is that a second call
        // doesn't let an in-flight countdown elapse early — this can't observe the actual reset
        // of elapsed time without a live Dispatcher message pump, but it does confirm Restart is
        // safe to call repeatedly and always leaves the timer in the running state.
        var timer = new DebounceTimer(TimeSpan.FromMilliseconds(500), () => { });

        timer.Restart();
        timer.Restart();
        timer.Restart();

        Assert.True(timer.IsRunning);
    }

    [Fact]
    public void Cancel_AfterRestart_StopsTheTimer()
    {
        var timer = new DebounceTimer(TimeSpan.FromMilliseconds(500), () => { });
        timer.Restart();

        timer.Cancel();

        Assert.False(timer.IsRunning);
    }

    [Fact]
    public void Cancel_NeverStarted_DoesNotThrow()
    {
        var timer = new DebounceTimer(TimeSpan.FromMilliseconds(500), () => { });

        var exception = Record.Exception(timer.Cancel);

        Assert.Null(exception);
        Assert.False(timer.IsRunning);
    }
}
