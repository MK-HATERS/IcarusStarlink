using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace IcarusStarlink.PakIO.DataChanges;

/// <summary>
/// File-based, same load-on-construct/save-on-mutate shape as AppSettingsService/
/// NexusWatchlistStore — kept in IcarusStarlink.PakIO rather than split into a separate Storage
/// implementation, matching how ExmodFolder already does its own real file I/O directly in this
/// project rather than through a Core/Storage interface split. (A Core-level interface isn't an
/// option here anyway: WeeklyChangeReport embeds IcarusStarlink.Diffing.FieldChange, and Core is a
/// dependency of Diffing, not the other way around.)
/// </summary>
public sealed class WeeklyChangeReportStore : IWeeklyChangeReportStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly ILogger<WeeklyChangeReportStore> _logger;

    public WeeklyChangeReport? Current { get; private set; }

    public WeeklyChangeReportStore(string appDataDirectory, ILogger<WeeklyChangeReportStore> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(appDataDirectory);
        _filePath = Path.Combine(appDataDirectory, "weekly_changes.json");
        Current = Load();
    }

    public void Save(WeeklyChangeReport report)
    {
        var json = JsonSerializer.Serialize(report, JsonOptions);
        File.WriteAllText(_filePath, json);
        Current = report;
    }

    private WeeklyChangeReport? Load()
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<WeeklyChangeReport>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to load weekly change report from {Path}; starting with none", _filePath);
            return null;
        }
    }
}
