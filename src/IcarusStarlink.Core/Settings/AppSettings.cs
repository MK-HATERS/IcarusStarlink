namespace IcarusStarlink.Core.Settings;

public sealed class AppSettings
{
    public string? IcarusContentPath { get; set; }
    public string? UnrealPakExePath { get; set; }

    /// <summary>A steamcmd.exe the user already has, chosen on the Server page (or picked up from an adopted server's Steam manifest). Null = Starlink's own copy in its SteamCMD folder, installed on request.</summary>
    public string? SteamCmdExePath { get; set; }
    public string ThemeName { get; set; } = "Icarus";

    /// <summary>Settings › Appearance › Show pop-up messages: results (a merge finished, an import failed…) pop up for a few seconds under the bell.</summary>
    public bool ShowPopUpMessages { get; set; } = true;

    /// <summary>Settings › Appearance › Size of text and controls: every window drawn this many times bigger (1.0 = normal, 1.5 = half again as big, for 4K screens).</summary>
    public double UiScale { get; set; } = 1.0;

    /// <summary>
    /// Map › Redraw the maps during Update everything: after a game update, Update everything also
    /// redraws the maps whose game files changed (each big map about a minute). Off: the Map page
    /// says when they're out of date and its Reload is left to the user.
    /// </summary>
    public bool RedrawMapsDuringGameUpdate { get; set; }

    /// <summary>Map: what was ticked under Show ("Creatures", "Caves", "Grid" …); null = the first-time choice (creature zones, drop sites, caves).</summary>
    public List<string>? MapShown { get; set; }

    /// <summary>Map: the cave tier filter ("1" all, "2" tier 2 and up, "3" tier 3 only) and the mission kind picked ("" all).</summary>
    public string? MapCaveTier { get; set; }

    public string? MapMissionCategory { get; set; }

    /// <summary>The Database page: which mods the player has already seen there, so mods added (or updated) since show a New tag.</summary>
    public CatalogSeenState CatalogSeen { get; set; } = new();
    public bool PerformanceTrackingEnabled { get; set; }

    /// <summary>Settings › Diagnostics › Advanced log: the log file also records debug detail and web requests.</summary>
    public bool AdvancedLogging { get; set; }

    /// <summary>When the Advanced log switches itself back to Basic (24 hours after it was turned on), so it can't bloat the logs.</summary>
    public DateTimeOffset? AdvancedLoggingUntilUtc { get; set; }

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

    // Library: whether UE4SS's own built-in mods (BPModLoaderMod, Keybinds…) are listed. Off by
    // default — they come with the loader and only cluttered the list.
    public bool LibraryShowUe4ssBuiltIns { get; set; }

    /// <summary>The left navigation is folded to icons only (its collapse button); remembered across restarts.</summary>
    public bool NavCollapsed { get; set; }

    /// <summary>Library: the data panel's share of the width (or height, when the panels are stacked) — where the splitter between the two panels was left.</summary>
    public double LibraryPanelSplit { get; set; } = 0.62;

    /// <summary>Settings › Maintenance › Update channel: "Stable" (GitHub's latest release) or "Beta" (also betas — GitHub prereleases tagged vX.Y.Z-beta.N). Stable while running a beta offers the newest stable version to switch back.</summary>
    public string UpdateChannel { get; set; } = "Stable";

    /// <summary>Settings › Maintenance › "Check for updates when Starlink starts" (on this channel). Off: only the Check for updates button looks.</summary>
    public bool AutoCheckAppUpdates { get; set; } = true;

    /// <summary>Settings › Maintenance › Backups to keep › Merged pak installs: backups of the merged pak (and its mod list) kept on every install, for Restore previous install. Read through BackupLimits.</summary>
    public int MergedPakBackupsKept { get; set; } = BackupLimits.DefaultMergedPakBackupsKept;

    /// <summary>Settings › Maintenance › Backups to keep › Mod backups: what Clean up backups keeps of each mod (and of each server's zips and removed paks). Read through BackupLimits.</summary>
    public int ModBackupsKept { get; set; } = BackupLimits.DefaultModBackupsKept;

    /// <summary>Settings › Maintenance › Backups to keep › Save backups: automatic save backups kept for each slot (the first one ever made is kept on top). Read through BackupLimits.</summary>
    public int SaveBackupsKept { get; set; } = BackupLimits.DefaultSaveBackupsKept;

    /// <summary>Settings › Maintenance › Backups to keep › Server backups: the keep count a newly added server starts with (each server's own Backups tab changes its own count). Read through BackupLimits.</summary>
    public int ServerBackupsKept { get; set; } = BackupLimits.DefaultServerBackupsKept;

    // Main window bounds — all four null until the first real close, so a fresh install still gets
    // MainWindow.xaml's own WindowStartupLocation="CenterScreen" default rather than snapping to 0,0.
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }

    /// <summary>Null until the first real close, same as the four bounds above — MainWindow treats null as "never chosen yet" and defaults to Maximized, rather than always defaulting an unmaximized bool to false on a fresh install.</summary>
    public bool? WindowMaximized { get; set; }

    /// <summary>Last size of the other windows (mod editor, mod details, wizard…), by window name.</summary>
    public Dictionary<string, SavedWindowSize> WindowSizes { get; set; } = [];

    /// <summary>
    /// The app version last confirmed running, used to detect "you were just updated" for the
    /// What's New dialog. Null on a fresh install AND on an existing install's first launch after
    /// this field was introduced — both cases are deliberately silent (see
    /// SettingsViewModel.CheckForAppUpdatesOnLaunchAsync), so the dialog only ever appears on a
    /// genuine detected version change going forward, never out of nowhere.
    /// </summary>
    public string? LastSeenAppVersion { get; set; }

    /// <summary>The Merge &amp; Install profile last in use, selected again on the next start.</summary>
    public string? LastMergeProfileName { get; set; }

    /// <summary>
    /// Set right after a successful real Install (MergeInstallViewModel.InstallAsync) — read back
    /// on the NEXT app launch (SettingsViewModel) to check whether a real game crash happened since
    /// then via GameSessionHealthCheck.FindCrashesSince, the same "did that merge actually work"
    /// signal the Validate mods pre-flight check can't provide on its own (that one only inspects
    /// static data; this one observes whether the real engine actually crashed). Null means no
    /// install has happened yet this app has ever recorded — nothing to check against.
    /// </summary>
    public DateTimeOffset? LastInstallAtUtc { get; set; }

    /// <summary>
    /// Set once SettingsViewModel's first-launch setup has had its one-time say — i.e. once the
    /// game folder and UnrealPak were both in place and the first "Update data folder" extraction
    /// was offered (taken or declined). Null on a fresh install and for every install that predates
    /// the flow, so those get the one-time offer on their next launch too. The game-folder and
    /// UnrealPak steps themselves are NOT gated on this: they re-run on every launch while still
    /// unset, the way the UnrealPak offer always has.
    /// </summary>
    public DateTimeOffset? FirstLaunchSetupCompletedAtUtc { get; set; }

    /// <summary>
    /// The game build the mod setup was last made ready for: Steam's build id from
    /// appmanifest_1149460.acf ("25956278"), or "data-" + part of data.pak's hash when the
    /// manifest can't be found. Recorded on the first check, then moved on by Update everything
    /// (or the Library bar's Dismiss). Another build on disk means "Icarus updated".
    /// </summary>
    public string? LastSeenGameBuildId { get; set; }

    /// <summary>Starlink has handled nxm:// links on this PC (set when it's registered, cleared by Unregister): another app taking them over is then worth a warning.</summary>
    public bool NxmHandlerSetUp { get; set; }

    /// <summary>Settings › Nexus › "Take them back automatically": when another mod manager takes nxm:// links, Starlink takes them back (at start, when its window is brought forward, and the moment it happens while Starlink runs) and passes that app its own games' links. On by default: the other app keeps getting its links either way.</summary>
    public bool KeepNxmHandler { get; set; } = true;

    /// <summary>
    /// The move from IcarusStarlink.App.exe to IcarusStarlink.exe (3.0) is finished on this PC:
    /// shortcuts and the nxm:// handler point at the new exe and the old-name launcher is deleted
    /// (checked gone). Until then the move-over window asks again at each start; once set, the
    /// in-app update tells the updater not to copy the launcher back (ExeRenameMigration).
    /// </summary>
    public bool ExeRenameDone { get; set; }
}

/// <summary>
/// What the player has already seen on the Database page. Recorded when Starlink closes, for each
/// tab opened that session, or straight away by that tab's Mark all seen. The very first load of
/// each catalog counts as seen, so a first run (or the update that brings this) shows no wall of "new".
/// </summary>
public sealed class CatalogSeenState
{
    /// <summary>IMM Database entries seen: catalog entry id → the version listed then (a different version later reads as Updated).</summary>
    public Dictionary<string, string> ImmDatabaseVersions { get; set; } = [];

    /// <summary>IMM Database sources (Daedalus, Jimk72) already given their first-run baseline. A source that was down at the first load gets its own baseline later.</summary>
    public List<string> ImmDatabaseSources { get; set; } = [];

    /// <summary>Nexus mods published (or updated) after this time are new. Null until the Nexus tab is first built (it is then set to that moment).</summary>
    public DateTimeOffset? NexusSeenUpToUtc { get; set; }
}

/// <summary>A window's last size, restored the next time it opens.</summary>
public sealed class SavedWindowSize
{
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}
