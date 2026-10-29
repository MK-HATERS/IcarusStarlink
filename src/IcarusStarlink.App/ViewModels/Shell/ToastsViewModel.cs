using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Settings;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// The pop-up message cards under the header's bell: whatever important the activity feed records
/// (a merge finished, an import failed…) shows for a few seconds, so a result reported only in a
/// page's small status line isn't missed. The rules live in ToastQueue; this only feeds it from
/// IActivityLog and runs its clock on the UI thread.
/// </summary>
public sealed partial class ToastsViewModel
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(200);

    private readonly ToastQueue _queue;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _sinceLastTick = new();

    public ToastsViewModel(IActivityLog activityLog, ISettingsService settingsService)
    {
        _queue = new ToastQueue(() => settingsService.Current.ShowPopUpMessages);

        // The UI thread's dispatcher, not the constructing thread's: entries are logged from
        // background work too, and the cards are bound to the window.
        _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = TickInterval };
        _timer.Tick += (_, _) => Tick();

        activityLog.Entries.CollectionChanged += OnEntriesChanged;
    }

    public ObservableCollection<ToastItem> Toasts => _queue.Visible;

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null)
        {
            return;
        }

        foreach (var entry in e.NewItems.OfType<ActivityEntry>())
        {
            if (entry.Kind == ActivityEntryKind.Info)
            {
                continue;
            }

            if (_dispatcher.CheckAccess())
            {
                Show(entry);
            }
            else
            {
                _dispatcher.BeginInvoke(() => Show(entry));
            }
        }
    }

    private void Show(ActivityEntry entry)
    {
        if (_queue.Offer(entry.Message, entry.Kind) is not null && !_timer.IsEnabled)
        {
            _sinceLastTick.Restart();
            _timer.Start();
        }
    }

    private void Tick()
    {
        var elapsed = _sinceLastTick.Elapsed;
        _sinceLastTick.Restart();
        _queue.Advance(elapsed);
        if (Toasts.Count == 0)
        {
            _timer.Stop();
        }
    }

    [RelayCommand]
    private void Dismiss(ToastItem? toast)
    {
        if (toast is not null)
        {
            _queue.Dismiss(toast);
        }
    }
}
