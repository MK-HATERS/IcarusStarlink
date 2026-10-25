using CommunityToolkit.Mvvm.Messaging.Messages;

namespace IcarusStarlink.App.Messages;

/// <summary>
/// "Fix…" on Merge &amp; Install's check: open this Library mod's fix window (the Library's own, with
/// its guards and badge refresh). The reply is why it can't open right now, or null when it's
/// opening, so the reason shows on the page the click came from.
/// </summary>
public sealed class OpenModFixMessage(string folderName) : RequestMessage<string?>
{
    public string FolderName { get; } = folderName;
}
