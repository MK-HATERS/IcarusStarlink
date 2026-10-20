using System.Globalization;

namespace IcarusStarlink.Core.Server;

/// <summary>
/// Reads and writes ServerSettings.ini without losing anything this app doesn't know about: every
/// line, comment, other section and unknown key is kept verbatim and in order; only the known keys
/// of [/Script/Icarus.DedicatedServerSettings] are rewritten in place (or appended to that section
/// when missing). Section and key names match case-insensitively, as the engine's own ini parser
/// does. Floats are written the way the game writes them ("300.000000"), bools as True/False.
/// </summary>
public sealed class ServerSettingsIni
{
    public const string SectionName = "/Script/Icarus.DedicatedServerSettings";

    private readonly List<string> _lines;

    private ServerSettingsIni(List<string> lines) => _lines = lines;

    public static ServerSettingsIni Parse(string? text)
    {
        var lines = string.IsNullOrEmpty(text)
            ? new List<string>()
            : [.. text.Replace("\r\n", "\n").Split('\n')];

        // A trailing newline produces one empty last element — drop it so ToString round-trips.
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return new ServerSettingsIni(lines);
    }

    public static ServerSettingsIni Load(string path) => Parse(File.Exists(path) ? File.ReadAllText(path) : null);

    /// <summary>Written to a temp file and moved into place, so a crash mid-save can't leave the file cut short.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, ToString());
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }
    }

    public override string ToString() => _lines.Count == 0 ? "" : string.Join("\r\n", _lines) + "\r\n";

    /// <summary>The raw value of a key in the settings section, or null when the key (or section) is absent.</summary>
    public string? GetRaw(string key)
    {
        var (start, end) = FindSection();
        if (start < 0)
        {
            return null;
        }

        for (var i = start + 1; i < end; i++)
        {
            if (TrySplit(_lines[i], out var k, out var v) && string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }

        return null;
    }

    /// <summary>Sets a key in the settings section, creating the section at the end of the file when missing.</summary>
    public void SetRaw(string key, string value)
    {
        var (start, end) = FindSection();
        if (start < 0)
        {
            if (_lines.Count > 0 && _lines[^1].Trim().Length > 0)
            {
                _lines.Add("");
            }

            _lines.Add($"[{SectionName}]");
            _lines.Add($"{key}={value}");
            return;
        }

        for (var i = start + 1; i < end; i++)
        {
            if (TrySplit(_lines[i], out var k, out _) && string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                _lines[i] = $"{k}={value}";
                return;
            }
        }

        // Insert after the section's last non-blank line, so a blank separator before the next
        // section stays where it was.
        var insertAt = end;
        while (insertAt > start + 1 && _lines[insertAt - 1].Trim().Length == 0)
        {
            insertAt--;
        }

        _lines.Insert(insertAt, $"{key}={value}");
    }

    public DedicatedServerSettings Read() => new()
    {
        SessionName = GetString(nameof(DedicatedServerSettings.SessionName)),
        JoinPassword = GetString(nameof(DedicatedServerSettings.JoinPassword)),
        MaxPlayers = GetInt(nameof(DedicatedServerSettings.MaxPlayers)),
        AdminPassword = GetString(nameof(DedicatedServerSettings.AdminPassword)),
        ShutdownIfNotJoinedFor = GetDouble(nameof(DedicatedServerSettings.ShutdownIfNotJoinedFor)),
        ShutdownIfEmptyFor = GetDouble(nameof(DedicatedServerSettings.ShutdownIfEmptyFor)),
        AllowNonAdminsToLaunchProspects = GetBool(nameof(DedicatedServerSettings.AllowNonAdminsToLaunchProspects)),
        AllowNonAdminsToDeleteProspects = GetBool(nameof(DedicatedServerSettings.AllowNonAdminsToDeleteProspects)),
        LoadProspect = GetString(nameof(DedicatedServerSettings.LoadProspect)),
        ResumeProspect = GetBool(nameof(DedicatedServerSettings.ResumeProspect)),
        CreateProspect = GetString(nameof(DedicatedServerSettings.CreateProspect)),
        LastProspectName = GetString(nameof(DedicatedServerSettings.LastProspectName)),
        GameSaveFrequency = GetDouble(nameof(DedicatedServerSettings.GameSaveFrequency)),
        SaveGameOnExit = GetBool(nameof(DedicatedServerSettings.SaveGameOnExit)),
        FiberFoliageRespawn = GetBool(nameof(DedicatedServerSettings.FiberFoliageRespawn)),
        // The game writes "LargeStonesRespawn" (seen in a real server-written file); some host
        // guides spell it without the s — accept either, always write the game's spelling.
        LargeStonesRespawn = GetBool(nameof(DedicatedServerSettings.LargeStonesRespawn)) ?? GetBool("LargeStoneRespawn"),
    };

    /// <summary>Writes every key except LastProspectName (the server owns that one). A null value is written empty so the game falls back to its own default.</summary>
    public void Write(DedicatedServerSettings s)
    {
        SetRaw(nameof(s.SessionName), s.SessionName ?? "");
        SetRaw(nameof(s.JoinPassword), s.JoinPassword ?? "");
        SetRaw(nameof(s.MaxPlayers), s.MaxPlayers?.ToString(CultureInfo.InvariantCulture) ?? "");
        SetRaw(nameof(s.AdminPassword), s.AdminPassword ?? "");
        SetRaw(nameof(s.ShutdownIfNotJoinedFor), FormatDouble(s.ShutdownIfNotJoinedFor));
        SetRaw(nameof(s.ShutdownIfEmptyFor), FormatDouble(s.ShutdownIfEmptyFor));
        SetRaw(nameof(s.AllowNonAdminsToLaunchProspects), FormatBool(s.AllowNonAdminsToLaunchProspects));
        SetRaw(nameof(s.AllowNonAdminsToDeleteProspects), FormatBool(s.AllowNonAdminsToDeleteProspects));
        SetRaw(nameof(s.LoadProspect), s.LoadProspect ?? "");
        SetRaw(nameof(s.ResumeProspect), FormatBool(s.ResumeProspect));
        SetRaw(nameof(s.CreateProspect), s.CreateProspect ?? "");
        SetRaw(nameof(s.GameSaveFrequency), FormatDouble(s.GameSaveFrequency));
        SetRaw(nameof(s.SaveGameOnExit), FormatBool(s.SaveGameOnExit));
        SetRaw(nameof(s.FiberFoliageRespawn), FormatBool(s.FiberFoliageRespawn));
        SetRaw(nameof(s.LargeStonesRespawn), FormatBool(s.LargeStonesRespawn));
    }

    private (int Start, int End) FindSection()
    {
        var start = -1;
        for (var i = 0; i < _lines.Count; i++)
        {
            var trimmed = _lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                if (start >= 0)
                {
                    return (start, i);
                }

                if (string.Equals(trimmed[1..^1].Trim(), SectionName, StringComparison.OrdinalIgnoreCase))
                {
                    start = i;
                }
            }
        }

        return (start, _lines.Count);
    }

    private static bool TrySplit(string line, out string key, out string value)
    {
        key = value = "";
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is ';' or '#' or '[')
        {
            return false;
        }

        var eq = line.IndexOf('=');
        if (eq <= 0)
        {
            return false;
        }

        key = line[..eq].Trim();
        value = line[(eq + 1)..].Trim();
        return key.Length > 0;
    }

    private string? GetString(string key)
    {
        var raw = GetRaw(key);
        return string.IsNullOrEmpty(raw) ? null : raw;
    }

    private int? GetInt(string key) =>
        GetRaw(key) is { Length: > 0 } raw && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (int)v : null;

    private double? GetDouble(string key) =>
        GetRaw(key) is { Length: > 0 } raw && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private bool? GetBool(string key) => GetRaw(key)?.Trim().ToLowerInvariant() switch
    {
        "true" or "1" => true,
        "false" or "0" => false,
        _ => null,
    };

    private static string FormatDouble(double? value) => value?.ToString("F6", CultureInfo.InvariantCulture) ?? "";

    private static string FormatBool(bool? value) => value switch { true => "True", false => "False", null => "" };
}
