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
    /// — is one "package": every part carries the same Nexus mod ID (LibraryEntry.NexusModId,
    /// Ue4ssModMeta.NexusModId). Most Nexus mods are a single part and never form one.
    /// </summary>
    private sealed record ModPackage(int NexusModId, string DisplayName, IReadOnlyList<string> LibraryFolderNames, IReadOnlyList<string> Ue4ssFolderNames)
    {
        public int PartCount => LibraryFolderNames.Count + Ue4ssFolderNames.Count;
    }

    private IReadOnlyDictionary<int, ModPackage> _packagesByNexusModId = new Dictionary<int, ModPackage>();

    /// <summary>Groups every Library entry and staged UE4SS mod by Nexus mod ID; only groups with two or more parts count.</summary>
    private Dictionary<int, ModPackage> BuildPackages()
    {
        var libraryByNexusId = _repository.GetAll()
            .Where(e => e.NexusModId is not null)
            .GroupBy(e => e.NexusModId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var ue4ssByNexusId = new Dictionary<int, List<(string Folder, string? NexusName)>>();
        foreach (var folderName in SafeStagedUe4ssNames())
        {
            var meta = _ue4ssModMetaStore.Load(folderName);
            if (meta.NexusModId is { } id)
            {
                if (!ue4ssByNexusId.TryGetValue(id, out var list))
                {
                    ue4ssByNexusId[id] = list = [];
                }

                list.Add((folderName, meta.NexusName));
            }
        }

        var packages = new Dictionary<int, ModPackage>();
        foreach (var id in libraryByNexusId.Keys.Union(ue4ssByNexusId.Keys))
        {
            var libraryEntries = libraryByNexusId.GetValueOrDefault(id) ?? [];
            var ue4ss = ue4ssByNexusId.GetValueOrDefault(id) ?? [];
            if (libraryEntries.Count + ue4ss.Count < 2)
            {
                continue;
            }

            // The Nexus title: the UE4SS sidecar records it verbatim; a Library part's name is
            // "<title> — <part>" (see DownloadsViewModel.RecordNexusMetadataAsync), so cut at the dash.
            var displayName = ue4ss.Select(u => u.NexusName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                ?? libraryEntries.Select(e => e.Name.Split(" — ")[0]).FirstOrDefault()
                ?? $"Nexus mod #{id}";
            packages[id] = new ModPackage(id, displayName, [.. libraryEntries.Select(e => e.FolderName)], [.. ue4ss.Select(u => u.Folder)]);
        }

        foreach (var item in AllLibraryItems())
        {
            item.LinkedUe4ssNames = item.NexusModId is { } id && packages.TryGetValue(id, out var package) && package.LibraryFolderNames.Count > 0
                ? package.Ue4ssFolderNames
                : [];
            item.HasLuaConfig = Ue4ssMods.Any(r => r.HasConfig && item.LinkedUe4ssNames.Contains(r.Name, StringComparer.OrdinalIgnoreCase));
        }

        foreach (var row in Ue4ssMods)
        {
            row.LinkedLibraryModName = row.NexusModId is { } id && packages.TryGetValue(id, out var package) && package.LibraryFolderNames.Count > 0
                ? package.DisplayName
                : null;
        }

        return packages;
    }

    /// <summary>Staged UE4SS names, or none when staging can't be read — package grouping must never break the Library reload.</summary>
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
    /// Pairs each UE4SS mod with the data mod that is its other half: the same Nexus mod, or the same
    /// name once spaces, case and "&amp;"/"and" are ignored (MkHaters Bags / MkHatersBags, Minimap &amp;
    /// Map Icons / MiniMapa's Nexus title "Minimap and Map Icons"). Only a hint for the eye.
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
            if (row.IsLinkedToLibraryMod)
            {
                continue; // shown inside its data row ("Pak + UE4SS"), not as a row of its own
            }

            var keys = new[] { LinkKey(row.Name), row.NexusName is null ? null : LinkKey(row.NexusName) };
            var partner = items.FirstOrDefault(i => row.NexusModId is { } id && i.NexusModId == id)
                ?? items.FirstOrDefault(i => keys.Contains(LinkKey(i.Name)));
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
