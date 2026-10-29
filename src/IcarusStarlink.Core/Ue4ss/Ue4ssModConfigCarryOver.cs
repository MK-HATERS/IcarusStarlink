namespace IcarusStarlink.Core.Ue4ss;

/// <summary>What <see cref="Ue4ssModConfigCarryOver.KeepInstalledConfigs"/> did.</summary>
/// <param name="Kept">The config files the player's settings were carried into ("config.lua"), relative to the mod folder.</param>
/// <param name="Reviews">Per file, what changed between the player's file and the new version's; only files with something to look at (settings added or gone).</param>
public sealed record Ue4ssModConfigCarryOverResult(IReadOnlyList<string> Kept, IReadOnlyList<Ue4ssConfigReview> Reviews)
{
    public static readonly Ue4ssModConfigCarryOverResult Nothing = new([], []);

    /// <summary>"config.lua: Speed, Range" per file with new settings.</summary>
    public IReadOnlyList<string> NewSettings => [.. Reviews.Where(r => r.Added.Count > 0).Select(r => $"{r.File}: {string.Join(", ", r.Added)}")];

    /// <summary>"config.lua: OldName = 5" per file with settings that no longer exist.</summary>
    public IReadOnlyList<string> RemovedSettings => [.. Reviews.Where(r => r.Removed.Count > 0).Select(r => $"{r.File}: {string.Join(", ", r.Removed)}")];
}

/// <summary>
/// An update of a UE4SS mod keeps the player's settings (user report 2026-10-27: activating a new
/// Nexus version replaced config.lua with the shipped one, and the settings were only in
/// Backups\UE4SS). The new version's config file is the layout (its comments, order and new
/// settings at their defaults), and the player's value is written into every setting that still
/// exists; a setting the new version no longer has (renamed or dropped) is left out and reported
/// with the player's value, so it can be set again under its new name. A player's file that holds
/// something the merge can't carry (a table over several lines, code) is kept exactly as it was
/// instead, and the new settings are only named. The shipped defaults (config.default.lua) always
/// come from the new version: they are what "Reset to default" reads, and MkHaters Mod Settings'
/// "Add them" fills missing settings from them.
/// </summary>
public static class Ue4ssModConfigCarryOver
{
    public static Ue4ssModConfigCarryOverResult KeepInstalledConfigs(string previousModFolder, string newModFolder, string? newVersion = null)
    {
        if (!Directory.Exists(previousModFolder) || !Directory.Exists(newModFolder))
        {
            return Ue4ssModConfigCarryOverResult.Nothing;
        }

        var kept = new List<string>();
        var reviews = new List<Ue4ssConfigReview>();
        foreach (var config in Ue4ssModConfigFiles.Find(previousModFolder).Where(c => !c.IsDefaults))
        {
            var previous = Path.Combine(previousModFolder, config.RelativePath);
            var target = Path.Combine(newModFolder, config.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target))
            {
                // The new version doesn't ship this file: the player's copy comes along as it is.
                File.Copy(previous, target, overwrite: true);
                kept.Add(config.RelativePath);
                continue;
            }

            var review = new Ue4ssConfigReview { File = config.RelativePath, UpdatedAtUtc = DateTime.UtcNow, Version = newVersion };
            // Each file read in its own encoding (an "ANSI" config.lua with "é" in a comment came
            // back as "�"); the merged file is written in the player's file's encoding, BOM and all.
            var players = ConfigTextFile.Read(previous);
            var merged = Merge(config.Format, players.Text, ConfigTextFile.Read(target).Text, review);
            if (merged is null)
            {
                File.Copy(previous, target, overwrite: true);
            }
            else
            {
                ConfigTextFile.Write(target, merged, players.Encoding);
            }

            kept.Add(config.RelativePath);
            if (review.Added.Count > 0 || review.Removed.Count > 0)
            {
                reviews.Add(review);
            }
        }

        return new Ue4ssModConfigCarryOverResult(kept, reviews);
    }

    /// <summary>
    /// The new version's text with the player's values in it, or null to keep the player's file
    /// as it is. <paramref name="review"/> gets what was added, removed and kept either way.
    /// </summary>
    public static string? Merge(Ue4ssModConfigFormat format, string playersText, string shippedText, Ue4ssConfigReview review)
    {
        try
        {
            switch (format)
            {
                case Ue4ssModConfigFormat.Lua:
                {
                    var players = LuaConfigDocument.Parse(playersText);
                    var shipped = LuaConfigDocument.Parse(shippedText);
                    var playerKeys = players.Settings.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
                    review.Added = [.. shipped.Settings.Select(s => s.Key).Where(k => !playerKeys.Contains(k))];
                    // Something in the player's file the merge would drop: keep the file, name the new settings.
                    if (players.UnknownLineCount > 0 || shipped.UnknownLineCount > 0)
                    {
                        review.MergedIntoNewLayout = false;
                        return null;
                    }

                    foreach (var setting in players.Settings)
                    {
                        var shippedValue = shipped.GetValue(setting.Key);
                        if (shippedValue is null || shipped.SetValue(setting.Key, setting.Value) is not null)
                        {
                            review.Removed.Add($"{setting.Key} = {ShowValue(setting.Value)}");
                        }
                        else if (shippedValue != setting.Value)
                        {
                            review.Kept.Add(setting.Key);
                        }
                    }

                    review.MergedIntoNewLayout = true;
                    return shipped.ToText();
                }

                case Ue4ssModConfigFormat.Ini:
                {
                    var players = Ue4ssSettingsDocument.Parse(playersText, keepCommentLines: true);
                    var shipped = Ue4ssSettingsDocument.Parse(shippedText, keepCommentLines: true);
                    static string Name(Ue4ssSetting s) => s.Section.Length == 0 ? s.Key : $"[{s.Section}] {s.Key}";
                    var playerNames = players.Settings.Select(Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    review.Added = [.. shipped.Settings.Select(Name).Where(n => !playerNames.Contains(n))];
                    foreach (var setting in players.Settings)
                    {
                        var shippedValue = shipped.GetValue(setting.Section, setting.Key);
                        if (shippedValue is null)
                        {
                            review.Removed.Add($"{Name(setting)} = {setting.Value}");
                            continue;
                        }

                        try
                        {
                            shipped.SetValue(setting.Section, setting.Key, setting.Value);
                            if (!string.Equals(shippedValue.Trim(), setting.Value.Trim(), StringComparison.Ordinal))
                            {
                                review.Kept.Add(Name(setting));
                            }
                        }
                        catch (ArgumentException)
                        {
                            review.Removed.Add($"{Name(setting)} = {setting.Value}");
                        }
                    }

                    review.MergedIntoNewLayout = true;
                    return shipped.ToText();
                }

                default:
                    return null; // plain text (config.json): the player's file, untouched
            }
        }
        catch (Exception)
        {
            return null; // a file the parsers can't read is kept as it is
        }
    }

    private static string ShowValue(string value) => value.Length > 60 ? value[..57] + "…" : value;

    /// <summary>
    /// Writes another copy's settings into this mod folder (a shared patch, a server's config): the
    /// file's own layout stays and takes the values from <paramref name="text"/>; a file the mod
    /// doesn't have yet is written as given. The replaced file is saved first under
    /// <paramref name="backupFolder"/> (the mod's name, then the time). Returns the settings
    /// <paramref name="text"/> named that this copy's file doesn't have, so the caller can say so.
    /// </summary>
    public static IReadOnlyList<string> ApplyValues(string modFolder, string relativePath, string text, string backupFolder)
    {
        var target = Path.Combine(modFolder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!File.Exists(target))
        {
            ConfigTextFile.Write(target, text, ConfigTextEncoding.Utf8);
            return [];
        }

        // Read and written back in this copy's own encoding (an "ANSI" file stays ANSI, a BOM stays).
        var (current, encoding) = ConfigTextFile.Read(target);
        if (current == text)
        {
            return [];
        }

        var review = new Ue4ssConfigReview { File = relativePath };
        var merged = Merge(Ue4ssModConfigFiles.FormatOf(relativePath), playersText: text, shippedText: current, review);
        // The same values already (only line endings or trailing blanks apart): nothing to write,
        // no backup to pile up (a patch applied twice used to make one each time).
        static string Normal(string s) => s.Replace("\r\n", "\n").TrimEnd('\n', ' ', '\t');
        if (Normal(merged ?? text) == Normal(current))
        {
            return merged is null ? [] : review.Removed;
        }

        var backup = Path.Combine(backupFolder, Path.GetFileName(Path.TrimEndingDirectorySeparator(modFolder)), DateTime.Now.ToString("yyyy-MM-dd_HHmmss"), relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Copy(target, backup, overwrite: true);
        // No layout to merge into (plain text, or a file holding tables over several lines): the
        // given text is what the other copy runs, so it's taken whole.
        ConfigTextFile.Write(target, merged ?? text, encoding);
        return merged is null ? [] : review.Removed;
    }
}
