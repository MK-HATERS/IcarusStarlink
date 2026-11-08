namespace IcarusStarlink.Core.Settings;

/// <summary>
/// Settings › Maintenance › Backups to keep: the defaults (what the app kept before the setting
/// existed) and the range every count is held to where it's read, so a hand-edited 0 or -5 in
/// settings.json can never prune away the newest backup, and 10,000 can't fill the disk.
/// While settings came up as defaults after a load problem (ISettingsService.LoadProblem), the
/// automatic prunes (merged paks, save backups) keep the most, so backups the player chose to keep
/// aren't deleted on the strength of counts they never chose.
/// </summary>
public static class BackupLimits
{
    public const int Min = 1;
    public const int Max = 50;

    public const int DefaultMergedPakBackupsKept = 3;
    public const int DefaultModBackupsKept = 2;
    public const int DefaultSaveBackupsKept = 10;
    public const int DefaultServerBackupsKept = 10;

    public static int Clamp(int count) => Math.Clamp(count, Min, Max);

    /// <summary>The merged pak installs to keep: the setting when there is one, else the default; always 1..50 (50 while settings couldn't be loaded).</summary>
    public static int MergedPakKept(ISettingsService? settings) =>
        settings?.LoadProblem is not null ? Max : Clamp(settings?.Current.MergedPakBackupsKept ?? DefaultMergedPakBackupsKept);

    /// <summary>The backups of each mod Clean up backups keeps: always 1..50.</summary>
    public static int ModKept(ISettingsService? settings) =>
        Clamp(settings?.Current.ModBackupsKept ?? DefaultModBackupsKept);

    /// <summary>The automatic save backups kept for each slot: always 1..50 (50 while settings couldn't be loaded).</summary>
    public static int SaveKept(ISettingsService? settings) =>
        settings?.LoadProblem is not null ? Max : Clamp(settings?.Current.SaveBackupsKept ?? DefaultSaveBackupsKept);

    /// <summary>The keep count a new server starts with: always 1..50.</summary>
    public static int ServerKept(ISettingsService? settings) =>
        Clamp(settings?.Current.ServerBackupsKept ?? DefaultServerBackupsKept);
}
