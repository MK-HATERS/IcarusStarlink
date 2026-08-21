namespace IcarusStarlink.App.Messages;

/// <summary>
/// Broadcast whenever SettingsViewModel's Update data folder finishes successfully — WeeklyChangesViewModel
/// is a DI singleton constructed once on first navigation to that page, so without this it would
/// keep showing whatever report existed (or didn't) at that moment forever, the same staleness
/// LibraryChangedMessage exists to solve for Library/Downloads.
/// </summary>
public sealed record WeeklyChangeReportUpdatedMessage;
