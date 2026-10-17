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
}

/// <summary>A window's last size, restored the next time it opens.</summary>
public sealed class SavedWindowSize
{
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}
