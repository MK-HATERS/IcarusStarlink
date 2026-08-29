namespace IcarusStarlink.App.ViewModels;

/// <summary>One ExmodFieldValidityChecker.InvalidField finding, flattened for FieldIssueReportWindow's DataGrid.</summary>
public sealed record FieldIssueRowViewModel(string ModName, string File, string ItemName, string FieldName, string Reason);
