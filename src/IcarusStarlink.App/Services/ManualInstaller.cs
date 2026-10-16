using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Import;
using IcarusStarlink.PakIO.Safety;

namespace IcarusStarlink.App.Services;

public enum ManualInstallKind
{
    /// <summary>One or more .pak files (a file, or a folder of them) → one Library mod.</summary>
    PakMod,

    /// <summary>A folder holding an .EXMOD, or an .EXMODZ/.zip/.rar/.7z holding one → one Library mod.</summary>
    ExmodMod,

    /// <summary>A UE4SS mod folder (the one holding Scripts\main.lua) → one UE4SS mod.</summary>
    Ue4ssMod,
}

/// <summary>One line of a manual install: what to take from the download, as what, under what name.</summary>
public sealed partial class ManualInstallItem : ObservableObject
{
    public ManualInstallItem(ManualInstallKind kind, string fullPath, string relativePath, string name)
    {
        _kind = kind;
        FullPath = fullPath;
        RelativePath = relativePath;
        _name = name;
        SuggestedName = name;
    }

    /// <summary>The name as first suggested — only a name the user actually changed renames the mod (an EXMOD keeps its author's own name otherwise).</summary>
    public string SuggestedName { get; }

    public IReadOnlyList<string> KindOptions { get; } = ["Pak mod", "EXMOD mod", "UE4SS mod"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KindIndex))]
    private ManualInstallKind _kind;

    /// <summary>For the kind ComboBox.</summary>
    public int KindIndex
    {
        get => (int)Kind;
        set => Kind = (ManualInstallKind)Math.Clamp(value, 0, 2);
    }

    /// <summary>Library name (a pak mod's name, or a display name for an EXMOD) or the UE4SS folder name.</summary>
    [ObservableProperty]
    private string _name;

    public string FullPath { get; }

    /// <summary>Where in the download this came from, for display ("UE4SS Mods\MiniMapa").</summary>
    public string RelativePath { get; }
}

/// <summary>
/// "Install manually" — Mod Organizer's answer to a download it can't recognise, for Icarus: the
/// user decides what in the download is kept and what each kept piece is (a pak mod, an EXMOD mod,
/// or a UE4SS mod), starting from Starlink's own guesses. Installing goes through the same
/// importers as automatic detection, so conversion, Nexus links and UE4SS staging all behave the same.
/// </summary>
public static class ManualInstaller
{
    private static readonly string[] ArchiveExtensions = [".exmodz", ".zip", ".rar", ".7z"];

    /// <summary>Starlink's best guess at what this download holds — the starting point the user edits.</summary>
    public static List<ManualInstallItem> Suggest(string root)
    {
        var items = new List<ManualInstallItem>();
        var claimed = new List<string>();

        bool IsClaimed(string path) => claimed.Any(c => path.Equals(c, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(c + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

        void Add(ManualInstallKind kind, string path, string name)
        {
            items.Add(new ManualInstallItem(kind, path, Relative(root, path), name));
            claimed.Add(path.TrimEnd(Path.DirectorySeparatorChar));
        }

        // UE4SS mod folders first: anything under them belongs to them.
        foreach (var scripts in Directory.GetDirectories(root, "Scripts", SearchOption.AllDirectories).OrderBy(p => p.Length))
        {
            var modFolder = Path.GetDirectoryName(scripts)!;
            if (!IsClaimed(modFolder) && Directory.EnumerateFiles(scripts, "*.lua", SearchOption.AllDirectories).Any())
            {
                Add(ManualInstallKind.Ue4ssMod, modFolder, SameAs(modFolder, root) ? "UE4SS mod" : Path.GetFileName(modFolder));
            }
        }

        foreach (var exmod in Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".EXMOD", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.Length))
        {
            var folder = Path.GetDirectoryName(exmod)!;
            if (!IsClaimed(folder))
            {
                Add(ManualInstallKind.ExmodMod, folder, Path.GetFileNameWithoutExtension(exmod));
            }
        }

        foreach (var archive in Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => ArchiveExtensions.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase))).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (!IsClaimed(archive))
            {
                Add(ManualInstallKind.ExmodMod, archive, Path.GetFileNameWithoutExtension(archive));
            }
        }

        foreach (var folderOfPaks in Directory.GetFiles(root, "*.pak", SearchOption.AllDirectories)
            .Where(p => !IsClaimed(p))
            .GroupBy(p => Path.GetDirectoryName(p)!, StringComparer.OrdinalIgnoreCase))
        {
            var paks = folderOfPaks.ToList();
            if (paks.Count == 1)
            {
                Add(ManualInstallKind.PakMod, paks[0], Path.GetFileNameWithoutExtension(paks[0]));
            }
            else
            {
                Add(ManualInstallKind.PakMod, folderOfPaks.Key, SameAs(folderOfPaks.Key, root) ? "Pak mod" : Path.GetFileName(folderOfPaks.Key));
            }
        }

        return items;
    }

    /// <summary>Why this line can't be installed as chosen, or null when it can.</summary>
    public static string? Problem(ManualInstallItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Name))
        {
            return $"'{item.RelativePath}': give it a name.";
        }

        var isFolder = Directory.Exists(item.FullPath);
        return item.Kind switch
        {
            ManualInstallKind.PakMod when isFolder && !Directory.EnumerateFiles(item.FullPath, "*.pak", SearchOption.AllDirectories).Any() =>
                $"'{item.RelativePath}' has no .pak files in it.",
            ManualInstallKind.PakMod when !isFolder && !item.FullPath.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) =>
                $"'{item.RelativePath}' isn't a .pak file.",
            ManualInstallKind.ExmodMod when isFolder && !Directory.EnumerateFiles(item.FullPath, "*", SearchOption.AllDirectories).Any(f => f.EndsWith(".EXMOD", StringComparison.OrdinalIgnoreCase)) =>
                $"'{item.RelativePath}' has no .EXMOD file in it.",
            ManualInstallKind.ExmodMod when !isFolder && !ArchiveExtensions.Any(ext => item.FullPath.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) =>
                $"'{item.RelativePath}' isn't a folder or an archive (.EXMODZ, .zip, .rar, .7z).",
            ManualInstallKind.Ue4ssMod when !isFolder =>
                $"'{item.RelativePath}' is a file — a UE4SS mod is a folder (the one holding Scripts).",
            _ => null,
        };
    }

    /// <summary>Installs every line, in order. Throws (with the reason) on the first line that can't go in; lines already installed stay.</summary>
    public static async Task<IReadOnlyList<PendingDownloadActivatedItem>> InstallAsync(
        IReadOnlyList<ManualInstallItem> items, ILibraryRepository libraryRepository, IUe4ssModRepository ue4ssModRepository,
        IPrebuiltPakImporter prebuiltPakImporter, string dataFolder, string? unrealPakExePath,
        string? source, int? nexusModId, string? catalogEntryId, IReadOnlyCollection<string>? ue4ssNamesInUse)
    {
        if (items.Select(Problem).FirstOrDefault(p => p is not null) is { } problem)
        {
            throw new FormatException(problem);
        }

        var installed = new List<PendingDownloadActivatedItem>();
        foreach (var item in items)
        {
            var name = item.Name.Trim();
            switch (item.Kind)
            {
                case ManualInstallKind.PakMod:
                {
                    var paks = Directory.Exists(item.FullPath)
                        ? Directory.GetFiles(item.FullPath, "*.pak", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()
                        : [item.FullPath];
                    LibraryEntry entry;
                    if (paks.Count == 1)
                    {
                        entry = await prebuiltPakImporter.ImportAsync(paks[0], dataFolder, unrealPakExePath, source: source, nexusModId: nexusModId, catalogEntryId: catalogEntryId, name: name);
                    }
                    else
                    {
                        foreach (var pak in paks)
                        {
                            if (ModLints.CheckPakVersion(pak) is { } wrongVersion)
                            {
                                throw new FormatException(wrongVersion);
                            }
                        }

                        entry = libraryRepository.ImportPaks(paks, name, source, nexusModId, catalogEntryId);
                    }

                    RenameIfAsked(libraryRepository, entry, item);
                    installed.Add(new PendingDownloadActivatedItem(entry.FolderName, PendingDownloadActivationKind.Library));
                    break;
                }

                case ManualInstallKind.ExmodMod:
                {
                    var folder = item.FullPath;
                    string? scratch = null;
                    if (!Directory.Exists(folder))
                    {
                        scratch = Path.Combine(Path.GetTempPath(), "IcarusStarlink", $"Manual_{Guid.NewGuid():N}");
                        AnyArchiveExtractor.ExtractToDirectory(item.FullPath, scratch);
                        folder = scratch;
                    }

                    try
                    {
                        var entry = libraryRepository.Import(folder, source: source, nexusModId: nexusModId, catalogEntryId: catalogEntryId);
                        RenameIfAsked(libraryRepository, entry, item);
                        installed.Add(new PendingDownloadActivatedItem(entry.FolderName, PendingDownloadActivationKind.Library));
                    }
                    finally
                    {
                        TryDelete(scratch);
                    }

                    break;
                }

                case ManualInstallKind.Ue4ssMod:
                {
                    var staged = ue4ssModRepository.ImportFromFolder(item.FullPath, name, ue4ssNamesInUse);
                    installed.Add(new PendingDownloadActivatedItem(staged, PendingDownloadActivationKind.Ue4ssMod));
                    break;
                }
            }
        }

        return installed;
    }

    /// <summary>The user changed the suggested name: show theirs (the mod's files keep their own).</summary>
    private static void RenameIfAsked(ILibraryRepository libraryRepository, LibraryEntry entry, ManualInstallItem item)
    {
        var name = item.Name.Trim();
        if (!string.Equals(name, item.SuggestedName, StringComparison.Ordinal) && !string.Equals(entry.Name, name, StringComparison.Ordinal))
        {
            libraryRepository.SetDisplayNameOverride(entry.FolderName, name);
        }
    }

    public static string Relative(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." ? "(the whole download)" : relative;
    }

    private static bool SameAs(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string? folder)
    {
        try
        {
            if (folder is not null && Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
