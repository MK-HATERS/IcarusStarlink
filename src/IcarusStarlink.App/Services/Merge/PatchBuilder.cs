using System.IO;
using IcarusStarlink.Catalog;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Patches;
using IcarusStarlink.Core.Profiles;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Patches;

namespace IcarusStarlink.App.Services;

/// <summary>
/// Builds a patch (format 2) from a list of Library mods and a set of UE4SS mod folders: Merge &amp;
/// Install's Export patch (the queue, the game's UE4SS mods) and the Server page's Export for
/// players (the server's installed list, the server's UE4SS mods) share it. A mod that came from
/// Nexus or the community catalog is referenced by its ids (a friend downloads it themselves);
/// one found in the catalog by name and author is referenced too; anything else (a local edit, a
/// hand-made mod, a pak from nowhere) is bundled, since a friend genuinely couldn't get it
/// elsewhere. A UE4SS mod is referenced by its Nexus id or bundled from its folder, with its
/// config files as text when asked.
/// </summary>
public sealed class PatchBuilder(ILibraryRepository library, IUe4ssModMetaStore? ue4ssMetaStore)
{
    /// <summary>One UE4SS mod to put in a patch: its folder name and where its files are (the game's or a server's copy).</summary>
    public sealed record Ue4ssModSource(string Name, string Folder);

    /// <param name="Skipped">Mods left out: a pak mod with no source and no pak files on disk (nothing to reference, nothing to bundle).</param>
    public sealed record Result(PatchManifest Manifest, PatchBundle Bundle, IReadOnlyList<string> Skipped);

    public Result Build(
        string profileName, IReadOnlyList<LibraryEntry> mods, GameplayOptions options, IReadOnlyList<Ue4ssModSource> ue4ssMods,
        bool includeConfigs, IReadOnlySet<(string Name, string Author)> catalogKeys)
    {
        var entries = new List<PatchModEntry>();
        var bundledExmods = new Dictionary<string, ExmodPackageContents>();
        var bundledPaks = new Dictionary<string, IReadOnlyList<string>>();
        var skipped = new List<string>();
        foreach (var entry in mods)
        {
            if (entry.IsMissingFromLibrary)
            {
                continue;
            }

            var hasSource = entry.NexusModId is not null || entry.CatalogEntryId is not null
                || catalogKeys.Contains(CatalogKey.Normalize(entry.Name, entry.Author));
            var mod = new PatchModEntry
            {
                FolderName = entry.FolderName, Name = entry.Name, Author = entry.Author, Version = entry.Version,
                Bundled = !hasSource, NexusModId = entry.NexusModId, NexusFileId = entry.NexusFileId, CatalogEntryId = entry.CatalogEntryId,
                IsPak = entry.IsOpaquePak,
            };
            if (entry.IsOpaquePak)
            {
                var paks = PakFilesOf(entry);
                mod.PakFileNames = [.. paks.Select(Path.GetFileName)!];
                if (!hasSource)
                {
                    if (paks.Count == 0)
                    {
                        // Marked bundled with nothing to bundle, the import would refuse the whole patch.
                        skipped.Add(entry.Name);
                        continue;
                    }

                    bundledPaks[entry.FolderName] = paks;
                }
            }
            else if (!hasSource)
            {
                bundledExmods[entry.FolderName] = ExmodFolder.Read(library.GetFolderPath(entry.FolderName));
            }

            entries.Add(mod);
        }

        var ue4ssEntries = new List<PatchUe4ssEntry>();
        var bundledUe4ss = new Dictionary<string, string>();
        foreach (var (name, folder) in ue4ssMods)
        {
            var meta = ue4ssMetaStore?.Load(name);
            var entry = new PatchUe4ssEntry
            {
                Name = name, NexusName = meta?.NexusName, NexusModId = meta?.NexusModId, NexusFileId = meta?.NexusFileId,
                Version = Ue4ssModVersion.TryRead(folder), Enabled = true, Bundled = meta?.NexusModId is null,
            };
            if (entry.Bundled)
            {
                bundledUe4ss[name] = folder;
            }

            if (includeConfigs)
            {
                foreach (var config in Ue4ssModConfigFiles.Find(folder).Where(c => !c.IsDefaults))
                {
                    try
                    {
                        entry.Configs.Add(new PatchConfigFile { Path = config.RelativePath.Replace('\\', '/'), Text = File.ReadAllText(Path.Combine(folder, config.RelativePath)) });
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // A config that can't be read right now is left out; the mod still travels.
                    }
                }
            }

            ue4ssEntries.Add(entry);
        }

        var manifest = new PatchManifest
        {
            FormatVersion = PatchManifest.CurrentFormatVersion,
            ProfileName = profileName,
            ExportedAtUtc = DateTime.UtcNow,
            StarlinkVersion = typeof(PatchBuilder).Assembly.GetName().Version?.ToString(3),
            Mods = entries,
            Options = options,
            Ue4ssMods = ue4ssEntries,
            IncludesConfigs = includeConfigs && ue4ssEntries.Any(m => m.Configs.Count > 0),
        };
        return new Result(manifest, new PatchBundle(bundledExmods, bundledPaks, bundledUe4ss), skipped);
    }

    /// <summary>The UE4SS mods on in a Mods folder (the game's or a server's), the framework's own left out, as sources for <see cref="Build"/>.</summary>
    public static IReadOnlyList<Ue4ssModSource> Ue4ssModsOnIn(string modsFolder)
    {
        if (!Directory.Exists(modsFolder))
        {
            return [];
        }

        var list = Ue4ssModStart.ReadList(modsFolder);
        return [.. Directory.GetDirectories(modsFolder)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(n => !Ue4ssServerFiles.FrameworkMods.Contains(n) && Ue4ssModStart.IsMod(modsFolder, n) && Ue4ssModStart.Starts(modsFolder, n, list))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(n => new Ue4ssModSource(n, Path.Combine(modsFolder, n)))];
    }

    /// <summary>A pak mod's files: the ones its entry names, else every pak in its folder.</summary>
    private IReadOnlyList<string> PakFilesOf(LibraryEntry entry)
    {
        var folder = library.GetFolderPath(entry.FolderName);
        var named = entry.PakFileNames.Select(n => Path.Combine(folder, n)).Where(File.Exists).ToList();
        return named.Count > 0 ? named : Directory.Exists(folder) ? [.. Directory.GetFiles(folder, "*.pak").Order(StringComparer.OrdinalIgnoreCase)] : [];
    }
}
