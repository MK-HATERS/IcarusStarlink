namespace IcarusStarlink.Core.Settings;

public sealed class AppSettings
{
    public string? IcarusContentPath { get; set; }
    public string? UnrealPakExePath { get; set; }
    public string ThemeName { get; set; } = "Icarus";
    public bool PerformanceTrackingEnabled { get; set; }

    // Recorded on every successful "Update data folder" run — lets a later run detect the game's
    // data.pak has changed since then (LastDataPakHash) without re-extracting anything, and gives
    // DataFolderChangeTracker the "previous" timestamp a WeeklyChangeReport needs. Null until the
    // first successful update.
    public DateTimeOffset? LastDataFolderUpdatedAt { get; set; }
    public string? LastDataPakHash { get; set; }

    // Downloads > IMM Database column visibility. Mod Name isn't included here — it's always
    // shown, the one column a user can't hide.
    public bool CatalogShowAuthorColumn { get; set; } = true;
    public bool CatalogShowVersionColumn { get; set; } = true;
    public bool CatalogShowInstalledVersionColumn { get; set; } = true;
    public bool CatalogShowCompatibilityColumn { get; set; } = true;
    public bool CatalogShowCategoryColumn { get; set; } = true;
    public bool CatalogShowStatusColumn { get; set; } = true;
    public bool CatalogShowLastUpdatedColumn { get; set; } = true;

    // Main window bounds — all four null until the first real close, so a fresh install still gets
    // MainWindow.xaml's own WindowStartupLocation="CenterScreen" default rather than snapping to 0,0.
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    /// <summary>
    /// Folder names always merged into every rebuild — the "standard/minimum" baseline list
    /// (classic IMM's own "create a standard list and then always merge that list with your
    /// current one", ver 2.4.9). Stored in list order; prepended to the queue (lowest merge
    /// priority) so the active profile's own mods win any conflict against a baseline mod.
    /// Managed from Merge &amp; Install's "Set baseline" action.
    /// </summary>
    public List<string> BaselineModFolderNames { get; set; } = [];

    /// <summary>
    /// The app version last confirmed running, used to detect "you were just updated" for the
    /// What's New dialog. Null on a fresh install AND on an existing install's first launch after
    /// this field was introduced — both cases are deliberately silent (see
    /// SettingsViewModel.CheckForAppUpdatesOnLaunchAsync), so the dialog only ever appears on a
    /// genuine detected version change going forward, never out of nowhere.
    /// </summary>
    public string? LastSeenAppVersion { get; set; }
}
