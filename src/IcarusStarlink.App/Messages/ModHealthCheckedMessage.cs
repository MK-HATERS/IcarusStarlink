using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Messages;

/// <summary>Merge &amp; Install checked these mods (by folder): the Library shows the same results on their warning badges and in Problems.</summary>
public sealed record ModHealthCheckedMessage(IReadOnlyDictionary<string, ModHealthReport> ReportsByFolder);
