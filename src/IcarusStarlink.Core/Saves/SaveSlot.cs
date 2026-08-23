namespace IcarusStarlink.Core.Saves;

/// <summary>One player-data slot under %LocalAppData%\Icarus\Saved\PlayerData\&lt;SteamID64&gt;\ — the unit everything in the save editor operates on. PersonaName comes from Steam's own local loginusers.vdf when resolvable (no network involved), purely so the UI can show a human name next to the numeric ID.</summary>
public sealed record SaveSlot(string SteamId, string FolderPath, string? PersonaName)
{
    public string Display => PersonaName is null ? SteamId : $"{PersonaName} ({SteamId})";
}

/// <summary>One backup zip of a whole slot, newest-first in listings.</summary>
public sealed record SaveBackupInfo(string FilePath, DateTimeOffset TakenAtUtc)
{
    public string Display => $"{TakenAtUtc.LocalDateTime:g} — {Path.GetFileName(FilePath)}";
}
