namespace IcarusStarlink.Core.Server;

/// <summary>
/// The [/Script/Icarus.DedicatedServerSettings] keys of ServerSettings.ini, typed. Key names and
/// meanings are from RocketWerkz's own "Server Config &amp; Launch Parameters" wiki page plus the four
/// keys hosts document beyond it (GameSaveFrequency, SaveGameOnExit, FiberFoliageRespawn,
/// LargeStoneRespawn). A null value means "not set in the file" — written back as an empty value
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
    public bool? LargeStoneRespawn { get; set; }
}
