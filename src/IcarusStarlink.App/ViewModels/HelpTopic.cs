namespace IcarusStarlink.App.ViewModels;

/// <summary>One entry in the Help page's topic list — Markdown restricted to what MarkdownToFlowDocumentConverter actually renders (# / ## headers, **bold**, - bullets), same subset EXMOD readmes already use.</summary>
public sealed record HelpTopic(string Title, string Markdown)
{
    /// <summary>What a screen reader says for the topic (the record's own text, the whole topic, otherwise).</summary>
    public override string ToString() => Title;
}
