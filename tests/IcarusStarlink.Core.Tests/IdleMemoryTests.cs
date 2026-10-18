using IcarusStarlink.Core.Memory;

namespace IcarusStarlink.Core.Tests;

public class IdleMemoryTests
{
    private sealed class Cache(bool dropSomething, bool throws = false) : IIdleReleasable
    {
        public int Calls { get; private set; }

        public bool ReleaseIfIdle(TimeSpan idleFor)
        {
            Calls++;
            return throws ? throw new InvalidOperationException("boom") : dropSomething;
        }
    }

    [Fact]
    public void ReleaseIdle_AsksEveryCache_CountsWhatWasDropped_AndSurvivesOneThatThrows()
    {
        var drops = new Cache(true);
        var keeps = new Cache(false);
        var broken = new Cache(false, throws: true);
        IdleMemory.Register(drops);
        IdleMemory.Register(keeps);
        IdleMemory.Register(broken);

        var released = IdleMemory.ReleaseIdle(TimeSpan.Zero);

        Assert.True(released >= 1);
        Assert.Equal(1, drops.Calls);
        Assert.Equal(1, keeps.Calls);
        Assert.Equal(1, broken.Calls);
        GC.KeepAlive(drops);
        GC.KeepAlive(keeps);
        GC.KeepAlive(broken);
    }
}
