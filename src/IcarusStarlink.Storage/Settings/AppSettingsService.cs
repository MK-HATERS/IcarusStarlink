using System.Diagnostics;
using IcarusStarlink.Core.IO;
using IcarusStarlink.Core.Settings;
using Microsoft.Extensions.Logging;

namespace IcarusStarlink.Storage.Settings;

public sealed class AppSettingsService : ISettingsService
{
    private readonly string _settingsFilePath;
    private readonly string _backupFilePath;
    private readonly ILogger<AppSettingsService> _logger;

    // True when settings.json EXISTED but couldn't be read at startup, or wasn't valid JSON (see
    // JsonFileStore.Load): Current holds defaults (or settings.json.bak), and the first Save would
    // overwrite their real file with them (a OneDrive/antivirus lock at launch wiped every
    // setting; a hand-edit with one stray comma did the same).
    // The original is preserved beside the file before that first write so nothing is lost.
    private bool _originalCouldNotBeRead;

    public AppSettings Current { get; }

    /// <inheritdoc />
    /// <remarks>Stays set for the whole session (unlike _originalCouldNotBeRead, which Save clears): Current still holds defaults (or the backup's settings) after that save.</remarks>
    public string? LoadProblem { get; }

    public AppSettingsService(string appDataDirectory, ILogger<AppSettingsService> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(appDataDirectory);
        _settingsFilePath = Path.Combine(appDataDirectory, "settings.json");
        _backupFilePath = _settingsFilePath + ".bak";
        Current = JsonFileStore.Load(_settingsFilePath, () => new AppSettings(), _logger, out ListLoadProblem problem);

        // A sync client or antivirus usually lets go within a second or two, so a locked file is
        // tried again for up to 3 s. This runs on the startup thread behind the splash, so the
        // wait never freezes the window, and a normal start doesn't wait at all.
        var sinceFirstTry = Stopwatch.StartNew();
        while (problem == ListLoadProblem.Unreadable && sinceFirstTry.Elapsed < TimeSpan.FromSeconds(3))
        {
            Thread.Sleep(250);
            Current = JsonFileStore.Load(_settingsFilePath, () => new AppSettings(), _logger, out problem);
        }

        _originalCouldNotBeRead = problem != ListLoadProblem.None;

        // The copy every save keeps (settings.json.bak) is what the player last had but one save:
        // far better than defaults when settings.json is broken (a power cut mid-save, a bad
        // hand-edit) or still locked.
        var startedFromBackup = false;
        if (_originalCouldNotBeRead && File.Exists(_backupFilePath))
        {
            var backup = JsonFileStore.Load<AppSettings?>(_backupFilePath, () => null, _logger, out ListLoadProblem backupProblem);
            if (backup is not null && backupProblem == ListLoadProblem.None)
            {
                Current = backup;
                startedFromBackup = true;
                _logger.LogWarning("settings.json couldn't be used at startup; started from {Path} instead", _backupFilePath);
            }
        }

        LoadProblem = (problem, startedFromBackup) switch
        {
            (ListLoadProblem.Unreadable, true) =>
                $"Starlink couldn't open its settings file ({_settingsFilePath}) because another program, such as OneDrive or antivirus, was using it, "
                + "so it started with the copy from before your last save (settings.json.bak). A change made just before you closed Starlink may be missing. "
                + "To get the newest file back, close Starlink and start it again before changing any setting.",
            (ListLoadProblem.Unreadable, false) =>
                $"Starlink couldn't open its settings file ({_settingsFilePath}) because another program, such as OneDrive or antivirus, was using it, "
                + "so it started with default settings. To get yours back, close Starlink and start it again. If settings were saved meanwhile, "
                + "your old file is kept beside it as settings.json.unreadable-….bak: rename it to settings.json while Starlink is closed.",
            (ListLoadProblem.Corrupt, true) =>
                $"Starlink's settings file ({_settingsFilePath}) was broken, so it started with the copy from before your last save (settings.json.bak). "
                + "Check your settings: a change made just before that may be missing. The broken file is kept beside it as settings.json.unreadable-….bak "
                + "the first time settings are saved.",
            (ListLoadProblem.Corrupt, false) =>
                $"Starlink's settings file ({_settingsFilePath}) isn't valid JSON (edited by hand?), so it started with default settings. "
                + "The first time settings are saved, your old file is kept beside it as settings.json.unreadable-….bak: fix it, then rename it "
                + "to settings.json while Starlink is closed, or set things again in Settings.",
            _ => null,
        };
    }

    public bool Save()
    {
        try
        {
            if (_originalCouldNotBeRead && File.Exists(_settingsFilePath))
            {
                var preserved = $"{_settingsFilePath}.unreadable-{DateTime.UtcNow:yyyyMMdd-HHmmss}.bak";
                File.Copy(_settingsFilePath, preserved, overwrite: true);
                _logger.LogWarning("settings.json couldn't be read at startup; the original was preserved at {Path} before saving over it", preserved);
                _originalCouldNotBeRead = false;
            }
            else if (File.Exists(_settingsFilePath))
            {
                // Only a file that loaded fine is copied, so a broken one never replaces the good backup.
                KeepBackupCopy();
            }

            JsonFileStore.Save(_settingsFilePath, Current);
            return true;
        }
        catch (Exception ex)
        {
            // Deliberately broader than Load()'s scoped catch: Save is just serialize+write, with
            // much less surface for a masked programming bug, so catching anything here (disk
            // full, a removable drive unplugged mid-write, etc.) and reporting failure via the
            // bool return is the safer default.
            _logger.LogWarning(ex, "Failed to save settings to {Path}", _settingsFilePath);
            return false;
        }
    }

    /// <summary>settings.json as it is before this save, as settings.json.bak. A copy that fails doesn't stop the save.</summary>
    private void KeepBackupCopy()
    {
        try
        {
            AtomicFile.Copy(_settingsFilePath, _backupFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't keep {Path} as {Backup} before saving", _settingsFilePath, _backupFilePath);
        }
    }
}
