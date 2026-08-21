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
}
