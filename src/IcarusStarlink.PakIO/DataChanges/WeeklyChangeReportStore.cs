using System.Text.Json;
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
        var path = Path.Combine(_historyDirectory, FileNameFor(report.CurrentUpdateAt));
        var json = JsonSerializer.Serialize(report, JsonOptions);
        File.WriteAllText(path, json);

        _history.Insert(0, report);
        _history.Sort((a, b) => b.CurrentUpdateAt.CompareTo(a.CurrentUpdateAt));

        Prune();
    }

    private static string FileNameFor(DateTimeOffset currentUpdateAt) => $"{currentUpdateAt.UtcDateTime:yyyyMMdd-HHmmss}.json";

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

        foreach (var path in Directory.EnumerateFiles(_historyDirectory, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(path);
                if (JsonSerializer.Deserialize<WeeklyChangeReport>(json, JsonOptions) is { } report)
                {
                    _history.Add(report);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Failed to load a weekly change report from {Path}; skipping it", path);
            }
        }

        _history.Sort((a, b) => b.CurrentUpdateAt.CompareTo(a.CurrentUpdateAt));
    }

    /// <summary>Age-based, not count-based, per the user's own "keep a month's worth" framing —
    /// deliberately never prunes Current even if it's somehow older than the window (an update
    /// re-run after a long break shouldn't make "Current" disappear).</summary>
    private void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-RetentionDays);
        foreach (var stale in _history.Skip(1).Where(r => r.CurrentUpdateAt < cutoff).ToList())
        {
            _history.Remove(stale);
            try
            {
                File.Delete(Path.Combine(_historyDirectory, FileNameFor(stale.CurrentUpdateAt)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Failed to delete a pruned weekly change report for {Timestamp}", stale.CurrentUpdateAt);
            }
        }
    }
}
