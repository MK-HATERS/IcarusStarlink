namespace IcarusStarlink.Core;

/// <summary>
/// The app's exe names. It was IcarusStarlink.App.exe until 3.0; that name now ships as a small
/// launcher for IcarusStarlink.exe (IcarusStarlink.Bridge) until the app has moved the user's
/// shortcuts and nxm:// links over and removed it (ExeRenameMigration).
/// </summary>
public static class StarlinkExe
{
    public const string FileName = "IcarusStarlink.exe";

    public const string OldFileName = "IcarusStarlink.App.exe";

    /// <summary>Either name: an exe path that is some Starlink, old or new.</summary>
    public static bool IsStarlinkFileName(string? fileName) =>
        string.Equals(fileName, FileName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileName, OldFileName, StringComparison.OrdinalIgnoreCase);
}
