using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.Views;
using IcarusStarlink.Catalog;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Assets;
using IcarusStarlink.PakIO.Compare;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.Import;
using IcarusStarlink.PakIO.Install;
using IcarusStarlink.PakIO.Pak;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One Nexus download that landed as several parts (paks plus a Lua mod), and the linked halves.</summary>
public sealed partial class LibraryViewModel
{
    /// <summary>
    /// One Nexus download that landed as several things — a Lua mod plus its asset paks (WirelessPower)
    /// — is one "package": every part carries the same Nexus mod ID and file ID (LibraryEntry /
    /// Ue4ssModMeta .NexusModId and .NexusFileId). Keyed by the download, not the mod: two variant
    /// files from one Nexus page are two downloads, so deleting one mustn't take the other, and
    /// ticking an optional pak mustn't flip the main file's Lua half. Most Nexus mods are a single
    /// part and never form one.
    /// </summary>
    private sealed record ModPackage(int NexusModId, string DisplayName, IReadOnlyList<string> LibraryFolderNames, IReadOnlyList<string> Ue4ssFolderNames)
    {
        public int PartCount => LibraryFolderNames.Count + Ue4ssFolderNames.Count;
    }

    /// <summary>The package each part belongs to, by Library folder name and by UE4SS mod name.</summary>
    private Dictionary<string, ModPackage> _packageByLibraryFolder = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, ModPackage> _packageByUe4ssName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every UE4SS mod's sidecar as ReloadInstalledUe4ssMods last read it, so BuildPackages (run on every Reload) doesn't read each one again.</summary>
    private readonly Dictionary<string, Ue4ssModMeta> _ue4ssMetaByName = new(StringComparer.OrdinalIgnoreCase);

    private ModPackage? PackageOf(LibraryItemViewModel item) => _packageByLibraryFolder.GetValueOrDefault(item.FolderName);

    private ModPackage? PackageOf(Ue4ssModRowViewModel row) => _packageByUe4ssName.GetValueOrDefault(row.Name);

    /// <summary>The UE4SS rows that are this data mod's Lua half (same download).</summary>
    private IEnumerable<Ue4ssModRowViewModel> LinkedLuaRows(LibraryItemViewModel item) =>
        Ue4ssMods.Where(r => item.LinkedUe4ssNames.Contains(r.Name, StringComparer.OrdinalIgnoreCase));

    private Ue4ssModMeta CachedUe4ssMeta(string name)
    {
        if (!_ue4ssMetaByName.TryGetValue(name, out var meta))
        {
            _ue4ssMetaByName[name] = meta = _ue4ssModMetaStore.Load(name);
        }

        return meta;
    }

    /// <summary>
    /// Groups every Library entry and UE4SS mod (staged or on in the game: the user's rule is that a
    /// download's two halves stay linked either way, 2026-10-28) by the Nexus download they came from; only
    /// groups with two or more parts count. A part recorded before file IDs were (no NexusFileId)
    /// goes with its mod's one download when the Library knows only one; with several it can't tell
    /// which, so the parts without a file ID group only with each other.
    /// </summary>
    private void BuildPackages()
    {
        var parts = new List<(int ModId, int? FileId, string? Library, string? Ue4ss, string? NexusName, string? EntryName)>();
        foreach (var entry in _repository.GetAll().Where(e => e.NexusModId is not null))
        {
            parts.Add((entry.NexusModId!.Value, entry.NexusFileId, entry.FolderName, null, null, entry.Name));
        }

        foreach (var folderName in SafeUe4ssNames())
        {
            var meta = CachedUe4ssMeta(folderName);
            if (meta.NexusModId is { } id)
            {
                parts.Add((id, meta.NexusFileId, null, folderName, meta.NexusName, null));
            }
        }

        var byLibrary = new Dictionary<string, ModPackage>(StringComparer.OrdinalIgnoreCase);
        var byUe4ss = new Dictionary<string, ModPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in parts.GroupBy(p => p.ModId))
        {
            var fileIds = mod.Where(p => p.FileId is not null).Select(p => p.FileId).Distinct().ToList();
            var onlyFile = fileIds.Count == 1 ? fileIds[0] : null;
            foreach (var download in mod.GroupBy(p => p.FileId ?? onlyFile))
            {
                if (download.Count() < 2)
                {
                    continue;
                }

                var id = mod.Key;
                // The Nexus title: the UE4SS sidecar records it verbatim; a Library part's name is
                // "<title> — <part>" (see DownloadsViewModel.RecordNexusMetadataAsync), so cut at the dash.
                var displayName = download.Select(p => p.NexusName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                    ?? download.Where(p => p.EntryName is not null).Select(p => p.EntryName!.Split(" — ")[0]).FirstOrDefault()
                    ?? $"Nexus mod #{id}";
                var package = new ModPackage(id, displayName,
                    [.. download.Where(p => p.Library is not null).Select(p => p.Library!)],
                    [.. download.Where(p => p.Ue4ss is not null).Select(p => p.Ue4ss!)]);
                foreach (var folder in package.LibraryFolderNames)
                {
                    byLibrary[folder] = package;
                }

                foreach (var name in package.Ue4ssFolderNames)
                {
                    byUe4ss[name] = package;
                }
            }
        }

        _packageByLibraryFolder = byLibrary;
        _packageByUe4ssName = byUe4ss;

        foreach (var item in AllLibraryItems())
        {
            item.LinkedUe4ssNames = PackageOf(item)?.Ue4ssFolderNames ?? [];
            item.HasLuaConfig = LinkedLuaRows(item).Any(r => r.HasConfig);
        }
    }

    /// <summary>
    /// Every UE4SS mod name, staged (off) and in the game (on): only staged ones were grouped before,
    /// so turning a download's Lua half on split it from its data half (two unlinked rows; deleting
    /// the data row left the Lua half in the game). None when they can't be read — package grouping
    /// must never break the Library reload.
    /// </summary>
    private IReadOnlyList<string> SafeUe4ssNames()
    {
        try
        {
            return new Ue4ssInstalledNames(_ue4ssModRepository, _settingsService).All();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Staged (off) UE4SS names only, or none when staging can't be read: what Import UE4SS mod… compares to find the mod it just staged.</summary>
    private IReadOnlyList<string> SafeStagedUe4ssNames()
    {
        try
        {
            return _ue4ssModRepository.GetAll();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Pairs each UE4SS mod with the data mod that is its other half, shown as two rows (one per
    /// panel) each saying which row it's linked to: first the same Nexus download (BuildPackages),
    /// else the same name once spaces, case and "&amp;"/"and" are ignored (MkHaters Bags /
    /// MkHatersBags, Minimap &amp; Map Icons / MiniMapa's Nexus title "Minimap and Map Icons").
    /// The two halves are never folded into one row (library-model rule 2).
    /// </summary>
    private void LinkHalves()
    {
        var items = _itemsByFolderName.Values.ToList();
        foreach (var item in items)
        {
            item.LinkedPartner = null;
        }

        foreach (var row in Ue4ssMods)
        {
            row.LinkedPartner = null;

            // The same Nexus download first; else by name (a row from a different file of the same
            // mod is a separate mod, so the name has to match too).
            var partner = PackageOf(row) is { LibraryFolderNames.Count: > 0 } package
                ? items.FirstOrDefault(i => package.LibraryFolderNames.Contains(i.FolderName, StringComparer.OrdinalIgnoreCase))
                : null;
            if (partner is null)
            {
                var keys = new[] { LinkKey(row.Name), row.NexusName is null ? null : LinkKey(row.NexusName) };
                partner = items.FirstOrDefault(i => keys.Contains(LinkKey(i.Name)));
            }

            row.LinkedPartner = partner;
            if (partner is not null)
            {
                partner.LinkedPartner = row;
            }
        }
    }

    internal static string LinkKey(string name) =>
        new([.. name.Replace("&", "and", StringComparison.Ordinal).ToLowerInvariant().Where(char.IsLetterOrDigit)]);
}
