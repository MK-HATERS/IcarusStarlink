namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// One finding from either ExmodFieldValidityChecker (Kind = "Field") or ExmodReferenceChecker
/// (Kind = "Reference"), flattened for ValidationIssueReportWindow's one shared DataGrid — both
/// answer the same real underlying question ("will this mod's change actually do anything in-game")
/// and are computed from the same per-mod pass over the queue, so one combined report reads better
/// than two near-identical warning strips in Merge & Install.
/// </summary>
public sealed record ValidationIssueRowViewModel(string Kind, string ModName, string File, string ItemName, string FieldPath, string Reason);
