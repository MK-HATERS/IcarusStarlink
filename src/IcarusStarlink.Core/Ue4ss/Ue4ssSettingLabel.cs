using System.Text;

namespace IcarusStarlink.Core.Ue4ss;

/// <summary>
/// Plain names for the setting keys and comment headings of a mod's config, for the editor to
/// show above the real key: "bEnableFoo" → "Enable foo", "Ammo_AssaultRifle_Auto" → "Ammo assault
/// rifle auto", "==== SPAWN AMOUNTS ====" → "Spawn amounts". Display only: the file keeps its key.
/// </summary>
public static class Ue4ssSettingLabel
{
    /// <summary>The key as words: split at underscores, dots, lower→upper and letter↔digit changes; a leading "b" before a capital (Unreal's bool prefix) goes; runs of capitals (UE4SS, FPS) stay.</summary>
    public static string FromKey(string key)
    {
        // "Config.Speed": the table it's in is not part of the name.
        var name = key.Contains('.') ? key[(key.LastIndexOf('.') + 1)..] : key;
        if (name.Length > 1 && name[0] == 'b' && char.IsUpper(name[1]))
        {
            name = name[1..];
        }

        var words = new List<string>();
        var current = new StringBuilder();
        void Flush()
        {
            if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c is '_' or '-' or ' ')
            {
                Flush();
                continue;
            }

            if (current.Length > 0)
            {
                var prev = name[i - 1];
                var next = i + 1 < name.Length ? name[i + 1] : '\0';
                var soFar = current.ToString();
                var split =
                    (char.IsUpper(c) && char.IsLower(prev))                          // spawnDensity
                    || (char.IsUpper(c) && char.IsUpper(prev) && char.IsLower(next))  // FPSLimit → FPS Limit
                    || (char.IsDigit(c) && char.IsLetter(prev) && !IsAcronym(soFar)) // Slot2, not UE4SS / F12
                    || (char.IsUpper(c) && char.IsDigit(prev) && char.IsLower(next)); // F12Key → F12 Key
                if (split)
                {
                    Flush();
                }
            }

            current.Append(c);
        }

        Flush();
        if (words.Count == 0)
        {
            return key;
        }

        var parts = words.Select((w, i) => IsAcronym(w) ? w : i == 0 ? char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant() : w.ToLowerInvariant());
        return string.Join(" ", parts);
    }

    private static bool IsAcronym(string word) => word.Any(char.IsLetter) && word.All(c => char.IsUpper(c) || char.IsDigit(c));

    /// <summary>A comment heading as a section name: "GENERAL" → "General", "WHERE THE WORLD RUNS (solo)" → "Where the world runs (solo)".</summary>
    public static string Heading(string text)
    {
        var words = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Any(char.IsLetter) && !w.Any(char.IsLower) ? w.ToLowerInvariant() : w)
            .ToList();
        if (words.Count == 0)
        {
            return "";
        }

        var joined = string.Join(" ", words);
        var first = joined.IndexOf(joined.FirstOrDefault(char.IsLetter));
        return first < 0 ? joined : joined[..first] + char.ToUpperInvariant(joined[first]) + joined[(first + 1)..];
    }
}
