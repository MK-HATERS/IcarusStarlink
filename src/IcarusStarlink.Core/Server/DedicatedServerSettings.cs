namespace IcarusStarlink.Core.Server;

/// <summary>
/// The [/Script/Icarus.DedicatedServerSettings] keys of ServerSettings.ini, typed. Key names and
/// meanings are from RocketWerkz's own "Server Config &amp; Launch Parameters" wiki page plus the four
/// keys hosts document beyond it (GameSaveFrequency, SaveGameOnExit, FiberFoliageRespawn,
/// LargeStonesRespawn — that spelling is what the game itself writes). A null value means "not set in the file" — written back as an empty value
/// so the game uses its own default, never invented here.
/// </summary>
public sealed class DedicatedServerSettings
{
    public string? SessionName { get; set; }
    public string? JoinPassword { get; set; }
    public int? MaxPlayers { get; set; }
    public string? AdminPassword { get; set; }

    /// <summary>Seconds after startup with nobody joining before the prospect unloads to the lobby. Negative = never, 0 = as soon as possible. Game default 300.</summary>
    public double? ShutdownIfNotJoinedFor { get; set; }

    /// <summary>Seconds after the last player leaves before returning to the lobby. Same rules. Game default 300.</summary>
    public double? ShutdownIfEmptyFor { get; set; }

    public bool? AllowNonAdminsToLaunchProspects { get; set; }
    public bool? AllowNonAdminsToDeleteProspects { get; set; }

    /// <summary>Load this prospect (a save under Saved\PlayerData\DedicatedServer\Prospects) on startup. Applied first.</summary>
    public string? LoadProspect { get; set; }

    /// <summary>Resume LastProspectName on startup. Applied after LoadProspect. Game default True.</summary>
    public bool? ResumeProspect { get; set; }

    /// <summary>"[ProspectType] [Difficulty 1-4] [Hardcore True/False] [SaveName]" — creates a world on startup. Applied last. Remove after the first start or every restart makes a fresh world.</summary>
    public string? CreateProspect { get; set; }

    /// <summary>Written by the server; read-only here.</summary>
    public string? LastProspectName { get; set; }

    /// <summary>Minutes between autosaves.</summary>
    public double? GameSaveFrequency { get; set; }

    public bool? SaveGameOnExit { get; set; }
    public bool? FiberFoliageRespawn { get; set; }
    public bool? LargeStonesRespawn { get; set; }
}

/// <summary>
/// What stopping the server does to the world, in the server's own words: with SaveGameOnExit=False
/// (as on the developer's own server) a stop loses everything since the last autosave, so no
/// dialog may promise "it saves on exit".
/// </summary>
public static class ServerShutdownNotes
{
    public static bool LosesProgressOnStop(DedicatedServerSettings? settings) => settings?.SaveGameOnExit == false;

    /// <summary>The sentence the Stop / Update / Restart dialogs show about saving.</summary>
    public static string DescribeSaving(DedicatedServerSettings? settings) => settings?.SaveGameOnExit switch
    {
        true => "It saves the world as it shuts down.",
        false => $"'Save on exit' is OFF in this server's settings, so anything since its last autosave{Every(settings.GameSaveFrequency)} is lost. Turn on 'Save on exit' in the Settings tab to keep it.",
        null => $"'Save on exit' isn't set in its settings, so the game's default decides whether it saves as it shuts down. Its last autosave{Every(settings?.GameSaveFrequency)} is kept either way.",
    };

    /// <summary>The Overview warning when automation stops the server by itself (daily restart, updates) and nothing is saved on exit; null when there's nothing to warn about.</summary>
    public static string? AutomationWarning(DedicatedServerProfile profile, DedicatedServerSettings? settings) =>
        LosesProgressOnStop(settings) && (profile.DailyRestartEnabled || profile.UpdateOnRestart || profile.AutoUpdate)
            ? $"'Save on exit' is off: every automatic restart or update loses what players did since the last autosave{Every(settings!.GameSaveFrequency)}. Turn it on in the Settings tab."
            : null;

    private static string Every(double? minutes) =>
        minutes is { } m && m > 0 ? $" (every {m.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} minute{(m == 1 ? "" : "s")})" : "";
}
