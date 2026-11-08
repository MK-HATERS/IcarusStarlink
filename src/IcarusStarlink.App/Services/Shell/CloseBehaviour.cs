namespace IcarusStarlink.App.Services;

/// <summary>What closing the main window does.</summary>
public enum CloseChoice
{
    /// <summary>Starlink closes (after its usual "unsaved work" question).</summary>
    Close,

    /// <summary>The window hides and Starlink stays in the tray, so server automation keeps running.</summary>
    KeepInTray,
}

/// <summary>Decides whether closing the window really closes Starlink or keeps it in the tray.</summary>
public static class CloseBehaviour
{
    /// <summary>
    /// Keeps Starlink in the tray only when the host turned that on, a server runs or automation is
    /// on, and the player closed the window themselves. Quit from the tray, the updater and Windows
    /// shutting down always close it.
    /// </summary>
    public static CloseChoice Decide(bool keepInTraySetting, bool appShuttingDown, bool quitAsked, bool serversNeedStarlink) =>
        keepInTraySetting && !appShuttingDown && !quitAsked && serversNeedStarlink ? CloseChoice.KeepInTray : CloseChoice.Close;
}
