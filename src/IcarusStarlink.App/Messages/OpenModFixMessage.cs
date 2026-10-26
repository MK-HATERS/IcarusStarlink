using CommunityToolkit.Mvvm.Messaging.Messages;

namespace IcarusStarlink.App.Messages;

/// <summary>
/// "Fix…" on Merge &amp; Install's check: open this Library mod's fix window (the Library's own, with
/// its guards and badge refresh). The reply finishes with the real outcome: why it couldn't open or
/// why the mod's check failed, or null once the window was shown. The reason shows on the page the
/// click came from, since the Library's own notice isn't on screen there.
/// </summary>
public sealed class OpenModFixMessage(string folderName) : AsyncRequestMessage<string?>
{
    public string FolderName { get; } = folderName;
}
