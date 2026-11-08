using System.IO;
using IcarusStarlink.Core.Library;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.Services;

/// <summary>
/// What an import brings in, read before anything is written: its EXMOD file names (the file's own
/// name and the "fileName" it declares), its pak file names, and its UE4SS mod folder names.
/// </summary>
/// <param name="ExmodFileNames">Empty when the import holds more than one EXMOD (a pack of variants the user picks from).</param>
public sealed record IncomingModNames(IReadOnlySet<string> ExmodFileNames, IReadOnlySet<string> PakFileNames, IReadOnlyList<string> Ue4ssNames);

/// <summary>The copies of an incoming mod already installed: Library entries and UE4SS mod folders.</summary>
public sealed record ImportMatches(IReadOnlyList<LibraryEntry> Library, IReadOnlyList<string> Ue4ss)
{
    public bool IsEmpty => Library.Count == 0 && Ue4ss.Count == 0;

    /// <summary>
    /// One copy at most of each half: the only case worth a question. More than one means the user
    /// already keeps several copies on purpose, so the import adds another without asking.
    /// </summary>
    public bool IsSingle => !IsEmpty && Library.Count <= 1 && Ue4ss.Count <= 1;
}

/// <summary>
/// Finds the mod an import would duplicate, so a second drop of the same mod can offer to replace
/// it instead of making "&lt;name&gt;_2". The same mod means: the same EXMOD file name, the same set
/// of pak files, or a UE4SS mod folder of the same name. Deliberate variants ("Stacks_2x",
/// "Stacks_5x") have different file names, so they never match each other.
/// </summary>
public static class ImportMatchFinder
{
    /// <summary>Reads an unpacked import (a folder or an extracted archive). <paramref name="rootName"/> names a root that is itself a UE4SS mod, as the classifier names it.</summary>
    public static IncomingModNames ReadFolder(string root, string rootName)
    {
        var (ue4ssNames, _) = ExtractedModClassifier.ModNamesIn(root, rootName);
        var exmodFiles = new List<string>();
        var paks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(file);
            if (extension.Equals(".exmod", StringComparison.OrdinalIgnoreCase) || extension.Equals(".exmodz", StringComparison.OrdinalIgnoreCase))
            {
                exmodFiles.Add(file);
            }
            else if (extension.Equals(".pak", StringComparison.OrdinalIgnoreCase))
            {
                paks.Add(Path.GetFileName(file));
            }
        }

        var exmodNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (exmodFiles.Count == 1)
        {
            var file = exmodFiles[0];
            exmodNames.Add(Path.GetFileNameWithoutExtension(file));
            if (Path.GetExtension(file).Equals(".exmod", StringComparison.OrdinalIgnoreCase) && DeclaredFileName(file) is { } declared)
            {
                exmodNames.Add(declared);
            }
        }

        // An EXMOD import carries no paks of its own worth matching (the classifier imports the EXMOD).
        return new IncomingModNames(exmodNames, exmodFiles.Count > 0 ? new HashSet<string>() : paks, ue4ssNames);
    }

    /// <summary>A single dropped .pak.</summary>
    public static IncomingModNames ForPak(string pakPath) =>
        new(new HashSet<string>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFileName(pakPath) }, []);

    /// <summary>The installed copies of <paramref name="incoming"/>: Library entries and UE4SS names (staged or in the game).</summary>
    public static ImportMatches Find(IncomingModNames incoming, IReadOnlyList<LibraryEntry> entries, IEnumerable<string> ue4ssNames)
    {
        var library = entries.Where(e => IsSameDataMod(e, incoming)).ToList();
        var ue4ss = ue4ssNames
            .Where(name => incoming.Ue4ssNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ImportMatches(library, ue4ss);
    }

    private static bool IsSameDataMod(LibraryEntry entry, IncomingModNames incoming)
    {
        if (entry.IsOpaquePak)
        {
            return incoming.PakFileNames.Count > 0
                && entry.PakFileNames.Count == incoming.PakFileNames.Count
                && entry.PakFileNames.All(incoming.PakFileNames.Contains);
        }

        if (incoming.ExmodFileNames.Contains(entry.FileName))
        {
            return true;
        }

        // A pak Starlink turned into an EXMOD keeps the pak's name as its file name.
        return entry.ConvertedFromPrebuiltPak && incoming.ExmodFileNames.Count == 0 && incoming.PakFileNames.Count == 1
            && string.Equals(Path.GetFileNameWithoutExtension(incoming.PakFileNames.First()), entry.FileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The "fileName" an EXMOD declares, or null when it can't be read.</summary>
    private static string? DeclaredFileName(string exmodPath)
    {
        try
        {
            return ExmodJson.Parse(File.ReadAllText(exmodPath)).FileName is { Length: > 0 } name ? name : null;
        }
        catch (Exception)
        {
            // A damaged EXMOD: the classifier reports it; matching falls back to the file's name.
            return null;
        }
    }
}
