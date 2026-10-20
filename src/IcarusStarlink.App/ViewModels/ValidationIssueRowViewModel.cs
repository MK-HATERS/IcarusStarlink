namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// One finding from ExmodFieldValidityChecker (Kind = "Field"), ExmodReferenceChecker
/// (Kind = "Reference"), ExmodAssetCollisionChecker ("Asset collision") or TalentTreeChecker
/// ("Talent tree"), flattened for ValidationIssueReportWindow's one shared DataGrid — all four
/// answer the same real underlying question ("will this mod's change actually do anything in-game")
/// and are computed from the same pass over the queue, so one combined report reads better than
/// four near-identical warning strips in Merge & Install.
/// </summary>
public sealed record ValidationIssueRowViewModel(string Kind, string ModName, string File, string ItemName, string FieldPath, string Reason);
