using System.Text.Json;
using IcarusStarlink.Core.IO;
using Microsoft.Extensions.Logging;

namespace IcarusStarlink.PakIO.DataChanges;

/// <summary>
/// One file per report under a WeeklyChanges\ subfolder, named by the report's own CurrentUpdateAt
/// (sortable, collision-free within the same run) — replaces the original single-overwritten-file
/// design. Migrates a real pre-existing single weekly_changes.json from that older design on first
/// construction, so an existing user's one saved report isn't silently lost by the upgrade.
/// </summary>
public sealed class WeeklyChangeReportStore : IWeeklyChangeReportStore
{
    private const int RetentionDays = 30;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _historyDirectory;
    private readonly string _legacyFilePath;
    private readonly ILogger<WeeklyChangeReportStore> _logger;
    private readonly List<WeeklyChangeReport> _history = [];

    /// <summary>The file each report came from. History loads any .json in the folder, so a file's
    /// name may not match its date, and Remove must delete the file the report was read from.</summary>
    private readonly Dictionary<WeeklyChangeReport, string> _sourcePaths = new(ReferenceEqualityComparer.Instance);

    public WeeklyChangeReport? Current => _history.Count > 0 ? _history[0] : null;

    public IReadOnlyList<WeeklyChangeReport> History => _history;

    public WeeklyChangeReportStore(string appDataDirectory, ILogger<WeeklyChangeReportStore> logger)
    {
        _logger = logger;
        _historyDirectory = Path.Combine(appDataDirectory, "WeeklyChanges");
        _legacyFilePath = Path.Combine(appDataDirectory, "weekly_changes.json");
        Directory.CreateDirectory(_historyDirectory);

        MigrateLegacyFileIfPresent();
        LoadHistory();
    }

    public void Save(WeeklyChangeReport report)
    {
        var fileName = FileNameFor(report.CurrentUpdateAt);
        var path = Path.Combine(_historyDirectory, fileName);
        // AtomicFile, not a direct File.WriteAllText — that truncates the target in place, so a
        // process kill mid-write (a crash, forced shutdown, power loss) would leave a half-written
        // file. LoadHistory's own JsonException catch would then silently skip it on the very next
        // launch, losing that week's report entirely — this report is the one thing "Update data
        // folder" produces that nothing else can regenerate.
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions));

        // A report read from the same file (a second refresh in the same second) was replaced just
        // now, so it leaves the history too, or it was listed twice.
        foreach (var replaced in _history.Where(r => string.Equals(PathOf(r), path, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _history.Remove(replaced);
            _sourcePaths.Remove(replaced);
        }

        _history.Insert(0, report);
        _sourcePaths[report] = path;
        _history.Sort((a, b) => b.CurrentUpdateAt.CompareTo(a.CurrentUpdateAt));

        Prune();
    }

    /// <summary>The report itself, or failing that one with the same date (the key Save names files
    /// by). Deletes the file that report was loaded from or saved to.</summary>
    public bool Remove(WeeklyChangeReport report)
    {
        var fileName = FileNameFor(report.CurrentUpdateAt);
        var found = _history.FirstOrDefault(r => ReferenceEquals(r, report))
            ?? _history.FirstOrDefault(r => FileNameFor(r.CurrentUpdateAt) == fileName);
        if (found is null)
        {
            return false;
        }

        try
        {
            // File.Delete is a no-op for a file already gone, which still counts as removed.
            File.Delete(PathOf(found));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to delete the weekly change report for {Timestamp}", report.CurrentUpdateAt);
            return false;
        }

        _history.Remove(found);
        _sourcePaths.Remove(found);
        return true;
    }

    private string PathOf(WeeklyChangeReport report) =>
        _sourcePaths.TryGetValue(report, out var path) ? path : Path.Combine(_historyDirectory, FileNameFor(report.CurrentUpdateAt));

    /// <summary>Invariant, so the name Prune builds matches the one Save wrote whatever calendar the PC uses.</summary>
    private static string FileNameFor(DateTimeOffset currentUpdateAt) =>
        currentUpdateAt.UtcDateTime.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".json";

    /// <summary>A real pre-existing single weekly_changes.json (the store's original, pre-history
    /// design) gets folded in as this history's own first entry rather than silently discarded —
    /// only runs once, since the legacy file is removed after a successful migration.</summary>
    private void MigrateLegacyFileIfPresent()
    {
        if (!File.Exists(_legacyFilePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_legacyFilePath);
            if (JsonSerializer.Deserialize<WeeklyChangeReport>(json, JsonOptions) is { } legacyReport)
            {
                var path = Path.Combine(_historyDirectory, FileNameFor(legacyReport.CurrentUpdateAt));
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, json);
                }
            }

            File.Delete(_legacyFilePath);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to migrate legacy weekly change report from {Path}; leaving it in place", _legacyFilePath);
        }
    }

    private void LoadHistory()
    {
        _history.Clear();
        _sourcePaths.Clear();

        foreach (var path in Directory.EnumerateFiles(_historyDirectory, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(path);
                if (JsonSerializer.Deserialize<WeeklyChangeReport>(json, JsonOptions) is { } report)
                {
                    _history.Add(report);
                    _sourcePaths[report] = path;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Failed to load a weekly change report from {Path}; skipping it", path);
            }
        }

        _history.Sort((a, b) => b.CurrentUpdateAt.CompareTo(a.CurrentUpdateAt));
    }

    /// <summary>Age-based, not count-based ("keep a month's worth") —
    /// deliberately never prunes Current even if it's somehow older than the window (an update
    /// re-run after a long break shouldn't make "Current" disappear).</summary>
    private void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-RetentionDays);
        foreach (var stale in _history.Skip(1).Where(r => r.CurrentUpdateAt < cutoff).ToList())
        {
            var path = PathOf(stale);
            _history.Remove(stale);
            _sourcePaths.Remove(stale);
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Failed to delete a pruned weekly change report for {Timestamp}", stale.CurrentUpdateAt);
            }
        }
    }
}
