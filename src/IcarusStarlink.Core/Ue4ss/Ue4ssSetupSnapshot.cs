using System.Security.Cryptography;
using System.Text;

namespace IcarusStarlink.Core.Ue4ss;

/// <summary>What a UE4SS install left of the player's setup: a sentence for the status line and the things that aren't as they were.</summary>
/// <param name="Summary">Starts with a space; empty when there was nothing of the player's to check.</param>
/// <param name="Problems">Each thing that changed, in a few words; empty when everything is as it was.</param>
public sealed record Ue4ssSetupCheck(string Summary, IReadOnlyList<string> Problems)
{
    public static Ue4ssSetupCheck Nothing { get; } = new("", []);
}

/// <summary>
/// The player's part of a ue4ss folder: their UE4SS-settings.ini, their mods' files, and which of
/// those mods UE4SS starts. Taken before a UE4SS install and compared after it
/// (<see cref="CheckAgainst"/>), so the install can say that all of it is still as it was, or name
/// what isn't. UE4SS's own mods and its Mods\shared library are left out: an install replaces them.
/// </summary>
public sealed class Ue4ssSetupSnapshot
{
    private sealed record ModState(bool On, string Hash);

    private readonly string? _settingsHash;
    private readonly SortedDictionary<string, ModState> _mods;

    private Ue4ssSetupSnapshot(string? settingsHash, SortedDictionary<string, ModState> mods)
    {
        _settingsHash = settingsHash;
        _mods = mods;
    }

    /// <summary>Null when there is no ue4ss folder or it can't be read right now: then there is nothing to compare.</summary>
    public static Ue4ssSetupSnapshot? TryTake(string ue4ssFolder)
    {
        try
        {
            return Directory.Exists(ue4ssFolder) ? Take(ue4ssFolder) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Ue4ssSetupSnapshot Take(string ue4ssFolder)
    {
        var settingsPath = Path.Combine(ue4ssFolder, Ue4ssServerFiles.SettingsFileName);
        var settingsHash = File.Exists(settingsPath) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(settingsPath))) : null;

        var mods = new SortedDictionary<string, ModState>(StringComparer.OrdinalIgnoreCase);
        var modsFolder = Path.Combine(ue4ssFolder, "Mods");
        if (Directory.Exists(modsFolder))
        {
            var framework = Ue4ssFrameworkMods.In(modsFolder);
            var list = Ue4ssModStart.ReadList(modsFolder);
            foreach (var folder in Directory.EnumerateDirectories(modsFolder))
            {
                var name = Path.GetFileName(folder);
                if (!framework.Contains(name) && Ue4ssModStart.IsMod(modsFolder, name))
                {
                    mods[name] = new ModState(Ue4ssModStart.Starts(modsFolder, name, list), HashFolder(folder));
                }
            }
        }

        return new Ue4ssSetupSnapshot(settingsHash, mods);
    }

    /// <summary>Every file's path and bytes; enabled.txt aside, which is the on/off switch and is compared as that.</summary>
    private static string HashFolder(string folder)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f))
            .Where(r => !string.Equals(r, Ue4ssModStart.EnabledFileName, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[81920];
        foreach (var relative in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relative.ToLowerInvariant() + "\n"));
            using var stream = File.OpenRead(Path.Combine(folder, relative));
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>Compares this snapshot (taken before the install) with the folder as it is now.</summary>
    public Ue4ssSetupCheck CheckAgainst(string ue4ssFolder)
    {
        if (_settingsHash is null && _mods.Count == 0)
        {
            return Ue4ssSetupCheck.Nothing;
        }

        if (TryTake(ue4ssFolder) is not { } after)
        {
            return new Ue4ssSetupCheck(" Check these: the ue4ss folder couldn't be read after the install.", ["the ue4ss folder couldn't be read after the install"]);
        }

        var problems = new List<string>();
        if (_settingsHash is not null && after._settingsHash != _settingsHash)
        {
            problems.Add(after._settingsHash is null ? "UE4SS-settings.ini is gone" : "UE4SS-settings.ini changed");
        }

        problems.AddRange(_mods.Keys.Where(name => !after._mods.ContainsKey(name)).Select(name => $"{name} is missing"));
        var still = _mods.Where(m => after._mods.ContainsKey(m.Key)).Select(m => (Name: m.Key, Before: m.Value, After: after._mods[m.Key])).ToList();
        problems.AddRange(still.Where(m => m.Before.On != m.After.On)
            .Select(m => m.Before.On ? $"{m.Name} was on and is now off" : $"{m.Name} was off and is now on"));
        problems.AddRange(still.Where(m => m.Before.Hash != m.After.Hash).Select(m => $"{m.Name}'s files changed"));

        if (problems.Count > 0)
        {
            return new Ue4ssSetupCheck($" Check these: {string.Join("; ", problems)}. The rest is as it was.", problems);
        }

        var on = _mods.Values.Count(m => m.On);
        var mods = _mods.Count switch
        {
            0 => null,
            1 => "your mod",
            _ => $"your {_mods.Count} mods",
        };
        var what = (_settingsHash, mods) switch
        {
            (not null, null) => "your UE4SS settings are as they were",
            (not null, _) => $"your UE4SS settings and {mods} are as they were",
            (null, _) when _mods.Count == 1 => $"{mods} is as it was",
            _ => $"{mods} are as they were",
        };
        var state = _mods.Count switch
        {
            0 => "",
            1 => on == 1 ? " (it's on)" : " (it's off)",
            _ => $" ({on} on)",
        };
        return new Ue4ssSetupCheck($" Checked: {what}{state}.", []);
    }
}
