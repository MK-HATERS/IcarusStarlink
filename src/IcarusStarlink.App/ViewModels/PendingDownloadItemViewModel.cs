using IcarusStarlink.Core.Nexus;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One downloaded-but-not-yet-imported file — plain (not observable), since nothing here is user-editable, matching EditorItemViewModel's own precedent for a simple display-only row wrapper.</summary>
public sealed class PendingDownloadItemViewModel(PendingDownloadEntry entry)
{
    public int ModId { get; } = entry.ModId;
    public int FileId { get; } = entry.FileId;
    public string FileName { get; } = entry.FileName;
    public string LocalFilePath { get; } = entry.LocalFilePath;
    public DateTimeOffset DownloadedAtUtc { get; } = entry.DownloadedAtUtc;
}
