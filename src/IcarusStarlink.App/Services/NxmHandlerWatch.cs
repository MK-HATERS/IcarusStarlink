using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Settings;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace IcarusStarlink.App.Services;

/// <summary>
/// Notices another mod manager taking nxm:// links (Windows keeps one handler for every game; the
/// user's Crimson Desert manager re-registers itself on start — user report 2026-10-23). With
/// Settings › Nexus › "Take them back automatically" on (the default), Starlink takes them back —
/// the registrar remembers the other app, and links for its games are passed to it
/// (App.TryForwardOtherGameLink). Otherwise a warning pop-up says which app has them, once per
/// takeover. Runs at start, when the window is brought forward, and — while Starlink runs — the
/// moment the registry changes (StartWatching). While Starlink is closed another app can still win.
/// </summary>
public sealed class NxmHandlerWatch(INxmProtocolRegistrar registrar, ISettingsService settingsService, IActivityLog activityLog, Func<DateTimeOffset>? now = null)
{
    /// <summary>More take-backs than this inside FightWindow means another app is taking them back too: stop instead of trading the key forever.</summary>
    public const int MaxTakeBacks = 3;

    public static readonly TimeSpan FightWindow = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan FightPause = TimeSpan.FromMinutes(10);

    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    private readonly Queue<DateTimeOffset> _recentTakeBacks = new();
    private DateTimeOffset? _pausedUntil;

    /// <summary>The other app's command already warned about, so coming back to the window doesn't repeat it.</summary>
    private string? _warnedAbout;

    private Thread? _watchThread;
    private readonly ManualResetEvent _stop = new(false);

    public void Check()
    {
        try
        {
            CheckCore();
        }
        catch (Exception ex)
        {
            // A registry read or write refused: the Settings page still shows the state and offers the button.
            activityLog.Log($"Couldn't check the Nexus download handler: {ex.Message}");
        }
    }

    private void CheckCore()
    {
        var settings = settingsService.Current;
        if (registrar.IsRegisteredToThisApp())
        {
            _warnedAbout = null;
            if (!settings.NxmHandlerSetUp)
            {
                settings.NxmHandlerSetUp = true;
                settingsService.Save();
            }

            return;
        }

        // Never set up (the first-launch offer and Settings cover that), or turned off on purpose.
        if (!settings.NxmHandlerSetUp)
        {
            return;
        }

        var current = registrar.CurrentHandlerCommand();

        // Another Starlink install has them (two installs open at once): links still open in a
        // Starlink, and taking them back would trade the key between the two forever (review, 2026-10-23).
        if (NxmHandlerCommand.IsStarlink(current))
        {
            return;
        }

        var owner = HandlerName(current);
        if (settings.KeepNxmHandler && TakeBackAllowed())
        {
            registrar.Register();
            _warnedAbout = null;
            activityLog.Log(current is null
                ? "Nexus download links weren't set up any more — Starlink handles them again."
                : $"Took Nexus download links back from {owner}. Links for its games are passed on to it.");
            WeakReferenceMessenger.Default.Send(new NxmHandlerChangedMessage());
            return;
        }

        if (string.Equals(current ?? "", _warnedAbout, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _warnedAbout = current ?? "";
        activityLog.Log(
            _pausedUntil is not null && settings.KeepNxmHandler
                ? $"{owner} keeps taking Nexus download links back, so Starlink stopped taking them for now. Settings › Nexus › Take them back."
                : current is null
                    ? "Nexus \"Mod Manager Download\" links no longer open Starlink. Settings › Nexus › Register sets them up again."
                    : $"Nexus \"Mod Manager Download\" links now open {owner}, not Starlink. Settings › Nexus › Take them back (or turn on Take them back automatically).",
            ActivityEntryKind.Warning);
        WeakReferenceMessenger.Default.Send(new NxmHandlerChangedMessage());
    }

    private bool TakeBackAllowed()
    {
        var time = _now();
        if (_pausedUntil is { } until)
        {
            if (time < until)
            {
                return false;
            }

            _pausedUntil = null;
        }

        while (_recentTakeBacks.Count > 0 && time - _recentTakeBacks.Peek() > FightWindow)
        {
            _recentTakeBacks.Dequeue();
        }

        if (_recentTakeBacks.Count >= MaxTakeBacks)
        {
            _recentTakeBacks.Clear();
            _pausedUntil = time + FightPause;
            return false;
        }

        _recentTakeBacks.Enqueue(time);
        return true;
    }

    /// <summary>
    /// Checks the moment anything changes under HKCU\Software\Classes (where nxm lives; the whole
    /// tree, since a manager may delete and recreate the nxm key). A changing app writes several
    /// values in a row, so the check waits half a second after the last change. Runs until Stop.
    /// </summary>
    public void StartWatching(Action<Action> runOnUiThread)
    {
        if (_watchThread is not null)
        {
            return;
        }

        _watchThread = new Thread(() => WatchLoop(runOnUiThread)) { IsBackground = true, Name = "nxm handler watch" };
        _watchThread.Start();
    }

    public void Stop() => _stop.Set();

    private void WatchLoop(Action<Action> runOnUiThread)
    {
        try
        {
            using var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes");
            if (classes is null)
            {
                return;
            }

            using var changed = new AutoResetEvent(false);
            while (true)
            {
                // Asynchronous: Windows signals `changed` once; asked again each time round.
                if (RegNotifyChangeKeyValue(classes.Handle, true, RegNotifyChangeName | RegNotifyChangeLastSet, changed.SafeWaitHandle, true) != 0)
                {
                    return;
                }

                if (WaitHandle.WaitAny([changed, _stop]) == 1)
                {
                    return;
                }

                // Settle: let the other app finish writing before looking.
                while (changed.WaitOne(TimeSpan.FromMilliseconds(500)))
                {
                }

                runOnUiThread(Check);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"nxm handler watch stopped: {ex.Message}");
        }
    }

    private const uint RegNotifyChangeName = 0x1;
    private const uint RegNotifyChangeLastSet = 0x4;

    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle hKey, bool bWatchSubtree, uint dwNotifyFilter, SafeWaitHandle hEvent, bool fAsynchronous);

    /// <summary>A readable name for a handler command: the exe's product name ("Vortex"), else its file name; "nothing" when none is set.</summary>
    public static string HandlerName(string? command)
    {
        var exe = NxmHandlerCommand.ExePathOf(command);
        if (exe is null)
        {
            return "nothing";
        }

        try
        {
            if (File.Exists(exe) && FileVersionInfo.GetVersionInfo(exe) is { ProductName: { Length: > 0 } product })
            {
                return product.Trim();
            }
        }
        catch (Exception)
        {
            // Unreadable version info — the file name below is fine.
        }

        return Path.GetFileNameWithoutExtension(exe);
    }
}
