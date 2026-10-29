namespace IcarusStarlink.Core.Saves;

/// <summary>One player-data slot under %LocalAppData%\Icarus\Saved\PlayerData\&lt;SteamID64&gt;\ — the unit everything in the save editor operates on. PersonaName comes from Steam's own local loginusers.vdf when resolvable (no network involved), purely so the UI can show a human name next to the numeric ID.</summary>
public sealed record SaveSlot(string SteamId, string FolderPath, string? PersonaName)
{
    public string Display => PersonaName is null ? SteamId : $"{PersonaName} ({SteamId})";
}

/// <summary>A file's last-write time and size: enough to tell that something rewrote it.</summary>
public sealed record SaveFileStamp(DateTime LastWriteTimeUtc, long Length);

/// <summary>One backup zip of a whole slot, newest-first in listings.</summary>
public sealed record SaveBackupInfo(string FilePath, DateTimeOffset TakenAtUtc)
{
    /// <summary>"28/10/2026 14:03 — before a restore": what the backup is, not its zip name (a Steam ID and a timestamp).</summary>
    public string Display => $"{TakenAtUtc.LocalDateTime:g} — {Kind}";

    /// <summary>Told apart by the name SaveRepository gives each kind ("_pre_restore_", "_manual_"); every other one is the backup Save takes.</summary>
    public string Kind
    {
        get
        {
            var name = Path.GetFileName(FilePath);
            return name.Contains("_pre_restore_", StringComparison.OrdinalIgnoreCase) ? "before a restore"
                : name.Contains("_manual_", StringComparison.OrdinalIgnoreCase) ? "made with Backup now"
                : "before a save";
        }
    }
}
