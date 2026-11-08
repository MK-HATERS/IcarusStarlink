using System.IO;
using System.Text;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO;
using IcarusStarlink.PakIO.ArcFission;

namespace IcarusStarlink.App.Services;

/// <summary>The Merge page as it is now: its queue in page order, its options and its profile.</summary>
public sealed record MergeQueueSnapshot(IReadOnlyList<string> Queue, string OptionsSummary, string? ProfileName);

/// <summary>
/// mod-setup.txt for the diagnostics zip: the questions every mod bug report needs answered.
/// Library mods with versions, sources and Nexus ids; the Merge queue and options; what's installed
/// in the game's mods folder (ISL-Merged.txt and the pak names); the UE4SS build and its mods;
/// ArcFission; the game build and the last data refresh. Names and versions only: no keys, no
/// settings values, no save data. Paths stay as they are here; DiagnosticsExporter hides the user
/// folder in the whole entry.
/// </summary>
/// <param name="libraryMods">The Library's mods (ILibraryRepository.GetAll).</param>
/// <param name="ue4ssMods">The UE4SS mods for a game Mods folder (IUe4ssModStateService.GetAll); null leaves them out.</param>
/// <param name="mergeQueue">The Merge page's queue and options. It reads view-model state, so Capture calls it on the UI thread; null leaves them out.</param>
public sealed class ModSetupReport(
    Func<IReadOnlyList<LibraryEntry>> libraryMods,
    Func<AppSettings> settings,
    Func<string, IReadOnlyList<Ue4ssModState>>? ue4ssMods = null,
    Func<MergeQueueSnapshot>? mergeQueue = null)
{
    /// <summary>The settings values the report uses, copied on the UI thread so the build never reads AppSettings while it can change.</summary>
    private sealed record SettingsValues(
        string? IcarusContentPath, string? LastSeenGameBuildId, DateTimeOffset? LastDataFolderUpdatedAt,
        DateTimeOffset? LastInstallAtUtc, string? LastMergeProfileName);

    /// <summary>Call on the UI thread: reads the Merge page and the settings now and returns the rest of the work (file reads), to run off it.</summary>
    public Func<string> Capture()
    {
        MergeQueueSnapshot? merge = null;
        string? mergeProblem = null;
        try
        {
            merge = mergeQueue?.Invoke();
        }
        catch (Exception ex)
        {
            mergeProblem = ex.Message;
        }

        var s = settings();
        var values = new SettingsValues(
            s.IcarusContentPath, s.LastSeenGameBuildId, s.LastDataFolderUpdatedAt, s.LastInstallAtUtc, s.LastMergeProfileName);
        return () => Build(values, merge, mergeProblem);
    }

    /// <summary>Capture, as Markdown for pasting into Discord or a Nexus bug report (Library › Check › Copy my setup).</summary>
    public Func<string> CaptureMarkdown()
    {
        var build = Capture();
        return () => ToMarkdown(build());
    }

    /// <summary>
    /// The plain report as Markdown: a title, each "== part ==" in bold and each line a list item
    /// (indented lines nested under the one above). Characters Markdown would read as formatting
    /// in a mod name are escaped.
    /// </summary>
    public static string ToMarkdown(string report)
    {
        var text = new StringBuilder();
        text.AppendLine("## My Icarus mod setup");
        text.AppendLine();
        foreach (var raw in report.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                text.AppendLine();
            }
            else if (line.StartsWith("== ", StringComparison.Ordinal) && line.EndsWith(" ==", StringComparison.Ordinal) && line.Length > 6)
            {
                text.AppendLine($"**{EscapeMarkdown(line[3..^3])}**");
            }
            else if (line.StartsWith("  ", StringComparison.Ordinal))
            {
                var inner = line.TrimStart();
                text.AppendLine(char.IsDigit(inner[0]) && inner.Contains(". ", StringComparison.Ordinal)
                    ? $"  {EscapeMarkdown(inner)}"
                    : $"  - {EscapeMarkdown(inner)}");
            }
            else
            {
                text.AppendLine($"- {EscapeMarkdown(line.StartsWith("- ", StringComparison.Ordinal) ? line[2..] : line)}");
            }
        }

        return text.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string EscapeMarkdown(string text)
    {
        var escaped = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '*' or '_' or '`' or '~' or '|' or '<' or '>' or '#')
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.ToString();
    }

    private string Build(SettingsValues current, MergeQueueSnapshot? merge, string? mergeProblem)
    {
        var text = new StringBuilder();
        var content = current.IcarusContentPath is { Length: > 0 } path ? path : null;

        Section(text, "Game", () =>
        {
            text.AppendLine($"Game folder: {content ?? "not set"}");
            text.AppendLine($"Game build: {current.LastSeenGameBuildId ?? "unknown"}");
            text.AppendLine($"Last game data refresh: {(current.LastDataFolderUpdatedAt is { } at ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "never")}");
            text.AppendLine($"Last Merge & Install: {(current.LastInstallAtUtc is { } installed ? installed.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "never")}");
        });

        Section(text, "Library mods", () =>
        {
            var entries = libraryMods().OrderBy(e => e.DisplayNameOverride ?? e.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (entries.Count == 0)
            {
                text.AppendLine("(none)");
            }

            foreach (var entry in entries)
            {
                text.AppendLine($"- {DescribeMod(entry)}");
            }
        });

        Section(text, "Merge queue", () =>
        {
            if (mergeProblem is not null)
            {
                text.AppendLine($"Couldn't be read: {mergeProblem}");
                return;
            }

            if (merge is null)
            {
                text.AppendLine("(not available)");
                return;
            }

            text.AppendLine($"Profile: {merge.ProfileName ?? current.LastMergeProfileName ?? "none"}");
            text.AppendLine($"Options: {merge.OptionsSummary}");
            text.AppendLine(merge.Queue.Count == 0 ? "Queue: empty" : "Queue, in the order the Merge page lists it:");
            for (var i = 0; i < merge.Queue.Count; i++)
            {
                text.AppendLine($"  {i + 1}. {merge.Queue[i]}");
            }
        });

        Section(text, "Installed in the game (Content\\Paks\\mods)", () =>
        {
            var modsFolder = content is null ? null : Path.Combine(content, "Paks", "mods");
            if (modsFolder is null || !Directory.Exists(modsFolder))
            {
                text.AppendLine("(no mods folder)");
                return;
            }

            var files = Directory.GetFiles(modsFolder).Select(Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            text.AppendLine($"Files: {(files.Count == 0 ? "none" : string.Join(", ", files))}");
            var manifest = Path.Combine(modsFolder, InstallManifestNames.PakManifest);
            if (File.Exists(manifest))
            {
                text.AppendLine($"{InstallManifestNames.PakManifest}:");
                foreach (var line in File.ReadAllLines(manifest))
                {
                    text.AppendLine($"  {line}");
                }
            }
            else
            {
                text.AppendLine($"No {InstallManifestNames.PakManifest}: no merged pak from Starlink is installed.");
            }
        });

        Section(text, "UE4SS", () =>
        {
            if (content is null)
            {
                text.AppendLine("(game folder not set)");
                return;
            }

            var loader = Ue4ssGamePaths.ResolveLoaderFolder(content);
            if (!File.Exists(Ue4ssGamePaths.ResolveLoaderDllPath(content)) && !Directory.Exists(loader))
            {
                text.AppendLine("UE4SS: not installed");
                return;
            }

            var (version, commit) = Ue4ssInstalledBuild.Read(loader);
            text.AppendLine($"UE4SS build: {version ?? "unknown"}{(commit is null ? "" : $" ({commit})")}");
            if (ue4ssMods is null)
            {
                return;
            }

            var states = ue4ssMods(Ue4ssGamePaths.ResolveModsFolder(content)).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
            text.AppendLine(states.Count == 0 ? "UE4SS mods: none" : "UE4SS mods:");
            foreach (var state in states)
            {
                var where = state.IsInGame ? "" : " (only in Starlink's staging)";
                text.AppendLine($"  {state.Name}: {(state.IsEnabled ? "on" : "off")}{where}");
            }
        });

        Section(text, "ArcFission", () =>
        {
            if (content is null)
            {
                text.AppendLine("(game folder not set)");
                return;
            }

            if (ArcFissionRows.Scan(content) is not { } scan)
            {
                text.AppendLine("ArcFission Library: not on");
                if (InstallManifestNames.MisnamedArcFissionLibraryFolder(content) is { } misnamed)
                {
                    text.AppendLine($"A copy is in a folder with the wrong name: {misnamed}");
                }

                return;
            }

            text.AppendLine("ArcFission Library: on");
            foreach (var mod in scan.Mods)
            {
                text.AppendLine($"  {mod.Name} {mod.Version}: {mod.Rows.Count} row(s)");
            }

            foreach (var problem in scan.Problems)
            {
                text.AppendLine($"  Problem: {problem}");
            }
        });

        return text.ToString();
    }

    private static string DescribeMod(LibraryEntry entry)
    {
        var parts = new List<string> { $"{entry.DisplayNameOverride ?? entry.Name} {entry.Version}".TrimEnd() };
        parts.Add(entry.IsOpaquePak ? "pak" : "EXMOD");
        if (entry.Source is { Length: > 0 } source && entry.NexusModId is null)
        {
            parts.Add(source);
        }

        if (entry.NexusModId is { } nexusId)
        {
            parts.Add($"Nexus mod {nexusId}{(entry.NexusFileVersion is { Length: > 0 } fileVersion ? $", file {fileVersion}" : "")}");
        }

        if (entry.IsLocallyEdited)
        {
            parts.Add("edited here");
        }

        if (entry.IsMissingFromLibrary)
        {
            parts.Add("missing from the Library folder");
        }

        return string.Join(" | ", parts);
    }

    /// <summary>One heading and its lines; a part that throws says why and the next part still runs.</summary>
    private static void Section(StringBuilder text, string heading, Action write)
    {
        text.AppendLine($"== {heading} ==");
        try
        {
            write();
        }
        catch (Exception ex)
        {
            text.AppendLine($"Couldn't be read: {ex.Message}");
        }

        text.AppendLine();
    }
}
