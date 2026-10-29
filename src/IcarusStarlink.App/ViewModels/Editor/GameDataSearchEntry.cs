namespace IcarusStarlink.App.ViewModels;

/// <summary>One row plus its own serialized JSON — the searchable unit for the Search game data window's reference search. Kept separate from GameDataItemRef so the (much lighter) add-item picker never pays for the serialized text it doesn't use.</summary>
public sealed record GameDataSearchEntry(string RealPath, string ItemName, string RowJson);
