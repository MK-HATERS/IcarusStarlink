namespace IcarusStarlink.App.Messages;

/// <summary>
/// Sent when Settings' Refresh game data (UpdateDataFolderAsync) finishes: the extracted game data
/// and the weekly change report are new. View models and caches built from the old data (Weekly
/// changes, Library, the editors, Merge &amp; Install, Saves, Local server, GameDataIndexCache)
/// listen for it and reload or drop what they hold, since they are built once and live on.
/// </summary>
public sealed record WeeklyChangeReportUpdatedMessage;
