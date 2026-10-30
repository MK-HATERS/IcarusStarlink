namespace IcarusStarlink.Core;

/// <summary>
/// The app's exe names. It was IcarusStarlink.App.exe until 3.0; from 3.0 to 4.1 that name shipped
/// as a small launcher for IcarusStarlink.exe. Installs that still have one keep it until the app
/// has moved the user's shortcuts and nxm:// links over and removed it (ExeRenameMigration).
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
