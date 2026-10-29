using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.Core.Activity;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One pop-up message card: what the activity feed said, and how long it has left on screen.</summary>
public sealed partial class ToastItem : ObservableObject
{
    public ToastItem(string message, ActivityEntryKind kind, TimeSpan duration, bool isOverflow = false)
    {
        _message = message;
        _kind = kind;
        Duration = duration;
        Remaining = duration;
        IsOverflow = isOverflow;
    }

    /// <summary>Changes only on the "N more messages" card, as more arrive.</summary>
    [ObservableProperty]
    private string _message;

    /// <summary>Changes only on the "N more messages" card: a Warning folded into it makes it one.</summary>
    [ObservableProperty]
    private ActivityEntryKind _kind;

    public TimeSpan Duration { get; internal set; }

    /// <summary>The card standing in for a burst of messages too many to show one by one.</summary>
    public bool IsOverflow { get; }

    /// <summary>How many messages the "N more messages" card stands for.</summary>
    internal int OverflowCount { get; set; }

    /// <summary>Counts down only while the card isn't paused; the card goes at zero.</summary>
    public TimeSpan Remaining { get; internal set; }

    /// <summary>The mouse is over the card: it stays until the mouse leaves.</summary>
    [ObservableProperty]
    private bool _isPaused;
}

/// <summary>
/// The rules behind the pop-up messages, kept free of WPF so they can be tested: which activity
/// entries pop up (Success and Warning, never Info, so background passes that log a lot of Info
/// stay quiet), how long each stays, at most three at once (the oldest goes first), and the same
/// text again within two seconds collapsing into the card already showing rather than stacking
/// copies. A burst of different messages (an import logging a warning per broken part) keeps the
/// first cards and folds the rest into one "N more messages" card instead of flicking through
/// them. Time only moves when Advance is called, so the caller owns the timer.
/// </summary>
public sealed class ToastQueue
{
    public const int MaxVisible = 3;

    public static readonly TimeSpan CollapseWindow = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan SuccessDuration = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan WarningDuration = TimeSpan.FromSeconds(8);

    private readonly Func<bool> _isEnabled;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>When each recent text last arrived, for the collapse rule; pruned as it goes.</summary>
    private readonly Dictionary<(string Message, ActivityEntryKind Kind), DateTimeOffset> _lastSeen = [];

    /// <summary>When the last card arrived: one arriving within CollapseWindow of it is part of the same burst.</summary>
    private DateTimeOffset? _lastAdded;

    /// <param name="isEnabled">Settings › Appearance › Show pop-up messages, read each time so a change applies at once.</param>
    /// <param name="now">The clock (tests pass their own).</param>
    public ToastQueue(Func<bool> isEnabled, Func<DateTimeOffset>? now = null)
    {
        _isEnabled = isEnabled;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>Newest first, at most MaxVisible.</summary>
    public ObservableCollection<ToastItem> Visible { get; } = [];

    /// <returns>The new card, or null when the entry doesn't pop up (Info, switched off, or a repeat that refreshed the card already showing).</returns>
    public ToastItem? Offer(string message, ActivityEntryKind kind)
    {
        if (!_isEnabled() || string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        TimeSpan duration;
        switch (kind)
        {
            case ActivityEntryKind.Success:
                duration = SuccessDuration;
                break;
            case ActivityEntryKind.Warning:
                duration = WarningDuration;
                break;
            default:
                return null;
        }

        var now = _now();
        foreach (var stale in _lastSeen.Where(p => now - p.Value > CollapseWindow).Select(p => p.Key).ToList())
        {
            _lastSeen.Remove(stale);
        }

        var key = (message, kind);
        var isRepeat = _lastSeen.ContainsKey(key);
        _lastSeen[key] = now;
        if (isRepeat)
        {
            // Still showing: give it its full time again. Already gone (dismissed): stays gone.
            if (Visible.FirstOrDefault(t => !t.IsOverflow && t.Message == message && t.Kind == kind) is { } showing)
            {
                showing.Remaining = showing.Duration;
            }

            return null;
        }

        var inBurst = _lastAdded is { } last && now - last <= CollapseWindow;
        _lastAdded = now;
        if (inBurst && Visible.Count >= MaxVisible)
        {
            FoldIntoOverflow(kind, duration);
            return null;
        }

        var toast = new ToastItem(message, kind, duration);
        Visible.Insert(0, toast);
        while (Visible.Count > MaxVisible)
        {
            Visible.RemoveAt(Visible.Count - 1);
        }

        return toast;
    }

    /// <summary>
    /// The newest card becomes (or already is) the "N more messages" card: the first cards of the
    /// burst stay readable, and everything is still listed in full under the bell.
    /// </summary>
    private void FoldIntoOverflow(ActivityEntryKind kind, TimeSpan duration)
    {
        var overflow = Visible[0];
        if (!overflow.IsOverflow)
        {
            var replaced = overflow;
            overflow = new ToastItem("", replaced.Kind, replaced.Duration, isOverflow: true) { OverflowCount = 1 };
            Visible[0] = overflow;
        }

        overflow.OverflowCount++;
        if (kind == ActivityEntryKind.Warning)
        {
            overflow.Kind = ActivityEntryKind.Warning;
        }

        if (duration > overflow.Duration)
        {
            overflow.Duration = duration;
        }

        overflow.Remaining = overflow.Duration;
        overflow.Message = $"{overflow.OverflowCount} more messages: click the bell to see them all.";
    }

    /// <summary>Counts every unpaused card down by <paramref name="elapsed"/> and removes the ones whose time is up; everything goes when the setting is off.</summary>
    public void Advance(TimeSpan elapsed)
    {
        if (!_isEnabled())
        {
            Visible.Clear();
            return;
        }

        foreach (var toast in Visible.ToList())
        {
            if (toast.IsPaused)
            {
                continue;
            }

            toast.Remaining -= elapsed;
            if (toast.Remaining <= TimeSpan.Zero)
            {
                Visible.Remove(toast);
            }
        }
    }

    /// <summary>Clicked: gone at once.</summary>
    public void Dismiss(ToastItem toast) => Visible.Remove(toast);
}
