namespace IcarusStarlink.App.ViewModels;

/// <summary>One entry in the Help page's topic list — Markdown restricted to what MarkdownToFlowDocumentConverter actually renders (# / ## headers, **bold**, - bullets), same subset EXMOD readmes already use.</summary>
public sealed record HelpTopic(string Title, string Markdown);
