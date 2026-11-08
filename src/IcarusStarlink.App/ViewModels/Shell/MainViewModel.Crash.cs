using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.GameHealth;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// Right after Icarus closes (the Play watch going from Running to Play), looks for a crash the
/// game wrote during that session and says what happened: the error, whether the crashed stack
/// went through UE4SS, and which UE4SS mods logged errors. Then it offers Find the broken mod,
/// Copy crash report and Open crash folder. Each crash is told once (AppSettings.
/// LastReportedCrashAtUtc, shared with the check Settings makes when Starlink starts).
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>How long to wait before the second look: the crash reporter can still be writing the folder when the game's process is gone.</summary>
    public static readonly TimeSpan CrashRecheckWait = TimeSpan.FromSeconds(5);

    private readonly string? _gameCrashesFolder;

    /// <summary>When the watch first saw this game session running (the clock's UTC); a crash before it belongs to an earlier session.</summary>
    private DateTime? _gameSeenRunningSinceUtc;

    /// <summary>Copy crash report's clipboard. Tests swap it.</summary>
    internal Action<string> CopyToClipboard { get; set; } = System.Windows.Clipboard.SetText;

    /// <summary>The wait before the second look. Tests swap it.</summary>
    internal Func<TimeSpan, Task> CrashRecheckDelay { get; set; } = Task.Delay;

    /// <summary>The crash check the last game close started (done when there is none). Tests await it.</summary>
    internal Task CrashCheck { get; private set; } = Task.CompletedTask;

    /// <summary>Called by the Play watch each time it sees the game running.</summary>
    private void NoteGameRunning() => _gameSeenRunningSinceUtc ??= _clock();

    /// <summary>Called by the Play watch when the game has closed: starts the crash check without blocking the watch.</summary>
    private void NoteGameClosed()
    {
        if (_gameSeenRunningSinceUtc is not { } since)
        {
            return;
        }

        _gameSeenRunningSinceUtc = null;
        CrashCheck = CheckForCrashAfterGameClosedAsync(since);
    }

    private async Task CheckForCrashAfterGameClosedAsync(DateTime sessionStartUtc)
    {
        try
        {
            var found = await Task.Run(() => FindSessionCrash(sessionStartUtc));
            if (found is null)
            {
                await CrashRecheckDelay(CrashRecheckWait);
                found = await Task.Run(() => FindSessionCrash(sessionStartUtc));
            }

            if (found is { } crash)
            {
                ReportCrash(crash.Summary, crash.ModErrors);
            }
        }
        catch (Exception)
        {
            // Best-effort: a locked or unreadable crash folder never turns into an error of its own.
        }
    }

    /// <summary>Off the UI thread: the newest crash, when it is from this session and not told yet, with the session's UE4SS error counts.</summary>
    private (GameSessionHealthCheck.CrashSummary Summary, IReadOnlyList<Ue4ssModErrorCount> ModErrors)? FindSessionCrash(DateTime sessionStartUtc)
    {
        if (GameSessionHealthCheck.ReadLastCrash(_gameCrashesFolder) is not { } crash
            || crash.OccurredAtUtc.UtcDateTime < sessionStartUtc
            || _settingsService.Current.LastReportedCrashAtUtc is { } reported && crash.OccurredAtUtc <= reported)
        {
            return null;
        }

        return (crash, GameCrashReport.ReadUe4ssModErrors(_settingsService.Current.IcarusContentPath, crash.OccurredAtUtc));
    }

    private void ReportCrash(GameSessionHealthCheck.CrashSummary crash, IReadOnlyList<Ue4ssModErrorCount> modErrors)
    {
        _settingsService.Current.LastReportedCrashAtUtc = crash.OccurredAtUtc;
        // Not while settings.json couldn't be read: this save would replace the newest file before
        // the player restarts as LoadProblem asks. The value still holds this session.
        if (_settingsService.LoadProblem is null)
        {
            _settingsService.Save();
        }

        var notice = GameCrashReport.Describe(crash, modErrors);
        // Find the broken mod's own rounds ask whether the game broke: its search isn't interrupted.
        var ask = _dialogs is not null && !FindBrokenModHasSomeModsOut();

        // With the question on screen, the bell keeps the note without a pop-up on top of it.
        _activityLog?.Log(notice, ask ? ActivityEntryKind.Info : ActivityEntryKind.Warning);
        if (!ask)
        {
            return;
        }

        var crashFolder = Path.Combine(_gameCrashesFolder ?? GameSessionHealthCheck.ResolveCrashesFolder(), crash.CrashId);
        if (GameCrashReport.IsGpuDriverCrash(crash))
        {
            switch (_dialogs!.ConfirmWithCancel(notice, "Icarus crashed", ThemedConfirmSeverity.Warning, "Copy crash report", "Open crash folder"))
            {
                case true:
                    CopyCrashReport(crash, modErrors);
                    break;
                case false:
                    OpenFolder(crashFolder);
                    break;
            }

            return;
        }

        switch (_dialogs!.Choose(notice + "\n\nFind the broken mod tests your data mods in halves to find the one that breaks the game.",
                    "Icarus crashed", ThemedConfirmSeverity.Warning, "Find the broken mod", "Copy crash report", "Open crash folder"))
        {
            case 0:
                WeakReferenceMessenger.Default.Send(new OpenFindBrokenModMessage());
                break;
            case 1:
                CopyCrashReport(crash, modErrors);
                break;
            case 2:
                OpenFolder(crashFolder);
                break;
        }
    }

    private void CopyCrashReport(GameSessionHealthCheck.CrashSummary crash, IReadOnlyList<Ue4ssModErrorCount> modErrors)
    {
        try
        {
            CopyToClipboard(GameCrashReport.BuildReport(crash, modErrors));
            _activityLog?.Log("Crash report copied: paste it into your bug report.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            // Another program can hold the clipboard open.
            Warn($"Couldn't copy the crash report: {ex.Message} Try again, or use Settings › Maintenance › Export diagnostics zip.");
        }
    }
}
