using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Activity;

namespace IcarusStarlink.App.Tests.ViewModels;

public sealed class ToastQueueTests
{
    private DateTimeOffset _now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private bool _enabled = true;

    private ToastQueue Create() => new(() => _enabled, () => _now);

    private void Wait(ToastQueue queue, double seconds)
    {
        _now += TimeSpan.FromSeconds(seconds);
        queue.Advance(TimeSpan.FromSeconds(seconds));
    }

    [Fact]
    public void Info_NeverPopsUp_SuccessAndWarningDo()
    {
        var queue = Create();

        Assert.Null(queue.Offer("Catalog refreshed.", ActivityEntryKind.Info));
        Assert.Equal(ToastQueue.SuccessDuration, queue.Offer("Installed pack.", ActivityEntryKind.Success)!.Duration);
        Assert.Equal(ToastQueue.WarningDuration, queue.Offer("Install failed: locked.", ActivityEntryKind.Warning)!.Duration);

        Assert.Equal(["Install failed: locked.", "Installed pack."], queue.Visible.Select(t => t.Message));
    }

    [Fact]
    public void SameTextWithinTwoSeconds_CollapsesIntoOneCard_AndRefreshesIt()
    {
        var queue = Create();
        var first = queue.Offer("Saved.", ActivityEntryKind.Success)!;
        Wait(queue, 1.5);

        Assert.Null(queue.Offer("Saved.", ActivityEntryKind.Success));

        Assert.Same(first, Assert.Single(queue.Visible));
        Assert.Equal(first.Duration, first.Remaining);
    }

    [Fact]
    public void SameTextLater_PopsUpAgain()
    {
        var queue = Create();
        queue.Offer("Saved.", ActivityEntryKind.Success);
        Wait(queue, 5);
        Assert.Empty(queue.Visible);

        Assert.NotNull(queue.Offer("Saved.", ActivityEntryKind.Success));
    }

    [Fact]
    public void SameTextOfAnotherKind_IsNotARepeat()
    {
        var queue = Create();
        queue.Offer("Done.", ActivityEntryKind.Success);

        Assert.NotNull(queue.Offer("Done.", ActivityEntryKind.Warning));
        Assert.Equal(2, queue.Visible.Count);
    }

    [Fact]
    public void AtMostThree_TheOldestGoesFirst()
    {
        var queue = Create();
        foreach (var message in new[] { "A", "B", "C" })
        {
            queue.Offer(message, ActivityEntryKind.Warning);
            Wait(queue, 2.1); // apart: not a burst, and A still has time left when D arrives
        }

        queue.Offer("D", ActivityEntryKind.Warning);

        Assert.Equal(["D", "C", "B"], queue.Visible.Select(t => t.Message));
        Assert.DoesNotContain(queue.Visible, t => t.IsOverflow);
    }

    [Fact]
    public void ABurst_KeepsTheFirstCards_AndFoldsTheRestIntoOneMoreCard()
    {
        var queue = Create();
        queue.Offer("Imported 'Big Mod'.", ActivityEntryKind.Success);
        for (var i = 1; i <= 6; i++)
        {
            _now += TimeSpan.FromMilliseconds(50);
            queue.Offer($"Big Mod: broken part {i}", ActivityEntryKind.Warning);
        }

        Assert.Equal(ToastQueue.MaxVisible, queue.Visible.Count);
        var more = queue.Visible[0];
        Assert.True(more.IsOverflow);
        Assert.Equal(ActivityEntryKind.Warning, more.Kind);
        Assert.StartsWith("5 more messages", more.Message);
        Assert.Equal(["Big Mod: broken part 1", "Imported 'Big Mod'."], queue.Visible.Skip(1).Select(t => t.Message));
    }

    [Fact]
    public void Cards_GoWhenTheirTimeIsUp_WarningsLast()
    {
        var queue = Create();
        queue.Offer("Saved.", ActivityEntryKind.Success);
        queue.Offer("Couldn't start.", ActivityEntryKind.Warning);

        Wait(queue, 4.1);
        Assert.Equal("Couldn't start.", Assert.Single(queue.Visible).Message);

        Wait(queue, 4);
        Assert.Empty(queue.Visible);
    }

    [Fact]
    public void Hover_PausesTheCountdown()
    {
        var queue = Create();
        var toast = queue.Offer("Saved.", ActivityEntryKind.Success)!;
        toast.IsPaused = true;

        Wait(queue, 30);
        Assert.Single(queue.Visible);

        toast.IsPaused = false;
        Wait(queue, 4.1);
        Assert.Empty(queue.Visible);
    }

    [Fact]
    public void Dismiss_RemovesTheCardAtOnce_AndARepeatDoesNotBringItBack()
    {
        var queue = Create();
        var toast = queue.Offer("Saved.", ActivityEntryKind.Success)!;

        queue.Dismiss(toast);
        Assert.Empty(queue.Visible);

        Assert.Null(queue.Offer("Saved.", ActivityEntryKind.Success));
        Assert.Empty(queue.Visible);
    }

    [Fact]
    public void SwitchedOff_NothingPopsUp_AndShowingCardsGo()
    {
        var queue = Create();
        queue.Offer("Saved.", ActivityEntryKind.Success);

        _enabled = false;
        Assert.Null(queue.Offer("Install failed.", ActivityEntryKind.Warning));
        queue.Advance(TimeSpan.FromMilliseconds(200));

        Assert.Empty(queue.Visible);
    }
}
