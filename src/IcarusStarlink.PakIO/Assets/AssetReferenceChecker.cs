namespace IcarusStarlink.PakIO.Assets;

/// <summary>What the game's own content has, by package path ("/Game/ASS/DEP/M_X", "/Engine/…").</summary>
public interface IGameAssetCatalog
{
    /// <summary>Whether the game has this package; null when the game's content can't be read.</summary>
    bool? HasPackage(string packagePath);

    /// <summary>The objects the game's package holds (place inside the package → class), or null when it can't be read.</summary>
    IReadOnlyDictionary<string, string>? Objects(string packagePath);

    /// <summary>Every game package with this asset name, wherever it is ("M_ITM_CableHand" → "/Game/ASS/ITM/M_ITM_CableHand").</summary>
    IReadOnlyList<string> PackagesNamed(string assetName);
}

public enum MissingReferenceKind
{
    /// <summary>The game has the package under another folder, with every object the mod uses: pointing the mod there is sure.</summary>
    Moved,

    /// <summary>The game has several packages of that name that would do: one is suggested, the user decides.</summary>
    MovedMaybe,

    /// <summary>No package of the game has it any more.</summary>
    Gone,

    /// <summary>The game has the package, but not every object the mod uses from it.</summary>
    ObjectGone,
}

/// <param name="AssetFile">The mod's file that has the reference, Content-relative ("ASS/DEP/SM_X.uasset").</param>
/// <param name="Package">The package it references ("/Game/ASS/DEP/M_X").</param>
/// <param name="Objects">The objects it uses from that package (place inside the package → class); for ObjectGone, the missing ones.</param>
/// <param name="NewPackage">Where the game has it now (Moved, MovedMaybe).</param>
public sealed record MissingAssetReference(
    string AssetFile, string Package, IReadOnlyDictionary<string, string> Objects, MissingReferenceKind Kind, string? NewPackage, IReadOnlyList<string> Candidates);

/// <summary>
/// Checks every reference a mod's cooked files make to another package against the game: a
/// package the game doesn't have (or an object it doesn't hold) stops the game at load or leaves
/// a blueprint, mesh or material broken. A reference to the mod's own files, or to a file another
/// Library mod has, is fine. Only game content (/Game, /Engine) is checked: native classes
/// (/Script) and engine plugins need the game's code, which isn't read here. A package the game
/// moved (same name, same objects, another folder) can be pointed at its new place.
/// </summary>
public static class AssetReferenceChecker
{
    /// <param name="assetFiles">The mod's files, Content-relative.</param>
    /// <param name="read">Reads one of the mod's files (only .uasset/.umap are read); null when it can't be.</param>
    /// <param name="packagesElsewhere">Packages other Library mods have: a reference to one is a dependency, not a problem.</param>
    /// <returns>The problems, or null when the game's content can't be read (nothing can be said).</returns>
    public static IReadOnlyList<MissingAssetReference>? Check(
        IReadOnlyList<string> assetFiles, Func<string, byte[]?> read, IGameAssetCatalog game, IReadOnlySet<string>? packagesElsewhere = null)
    {
        var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packageFiles = new List<string>();
        foreach (var file in assetFiles.Select(f => f.Replace('\\', '/').TrimStart('/')))
        {
            if (PackagePathOf(file) is { } package)
            {
                own.Add(package);
                if (!file.EndsWith(".uexp", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".ubulk", StringComparison.OrdinalIgnoreCase))
                {
                    packageFiles.Add(file);
                }
            }
        }

        var result = new List<MissingAssetReference>();
        foreach (var file in packageFiles.Where(f => f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)))
        {
            if (read(file) is not { } bytes || UnrealPackageHeader.TryRead(bytes) is not { } header)
            {
                continue;
            }

            foreach (var (package, objects) in ReferencedObjects(header))
            {
                if (!IsGameContent(package) || own.Contains(package) || packagesElsewhere?.Contains(package) == true)
                {
                    continue;
                }

                var has = game.HasPackage(package);
                if (has is null)
                {
                    return null;
                }

                switch (has.Value)
                {
                    case true:
                        if (game.Objects(package) is { } held)
                        {
                            var missing = objects.Where(o => !held.ContainsKey(o.Key)).ToDictionary(o => o.Key, o => o.Value, StringComparer.OrdinalIgnoreCase);
                            if (missing.Count > 0)
                            {
                                result.Add(new MissingAssetReference(file, package, missing, MissingReferenceKind.ObjectGone, null, []));
                            }
                        }

                        break;
                    case false:
                        var candidates = game.PackagesNamed(AssetName(package))
                            .Where(c => !string.Equals(c, package, StringComparison.OrdinalIgnoreCase) && Holds(game.Objects(c), objects))
                            .OrderBy(c => Distance(c, package))
                            .ThenBy(c => c, StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        var kind = candidates.Count switch
                        {
                            0 => MissingReferenceKind.Gone,
                            1 => MissingReferenceKind.Moved,
                            _ => MissingReferenceKind.MovedMaybe,
                        };
                        result.Add(new MissingAssetReference(file, package, objects, kind, candidates.FirstOrDefault(), candidates));
                        break;
                }
            }
        }

        return result;
    }

    /// <summary>Each package the header imports from, with the objects it uses from it (place → class).</summary>
    public static IEnumerable<(string Package, IReadOnlyDictionary<string, string> Objects)> ReferencedObjects(UnrealPackageHeader header)
    {
        var byPackage = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Imports.Count; i++)
        {
            if (header.ImportPackage(i) is not { } package)
            {
                continue;
            }

            if (!byPackage.TryGetValue(package, out var objects))
            {
                byPackage[package] = objects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            if (header.ImportPath(i) is { } path)
            {
                objects.TryAdd(path, header.Imports[i].ClassName);
            }
        }

        return byPackage.Select(kv => (kv.Key, (IReadOnlyDictionary<string, string>)kv.Value));
    }

    /// <summary>"ASS/DEP/SM_X.uasset" → "/Game/ASS/DEP/SM_X"; null for a file that isn't part of a package.</summary>
    public static string? PackagePathOf(string contentRelativeFile)
    {
        var file = contentRelativeFile.Replace('\\', '/').TrimStart('/');
        var dot = file.LastIndexOf('.');
        if (dot <= 0)
        {
            return null;
        }

        var extension = file[dot..];
        return extension.ToLowerInvariant() is ".uasset" or ".umap" or ".uexp" or ".ubulk" or ".uptnl"
            ? "/Game/" + file[..dot]
            : null;
    }

    /// <summary>"/Game/ASS/DEP/M_X" → "M_X".</summary>
    public static string AssetName(string package) => package[(package.LastIndexOf('/') + 1)..];

    private static bool IsGameContent(string package) =>
        package.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase) || package.StartsWith("/Engine/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The game's package holds every object the mod uses, each of the same class.</summary>
    private static bool Holds(IReadOnlyDictionary<string, string>? held, IReadOnlyDictionary<string, string> used) =>
        held is not null && used.All(o => held.TryGetValue(o.Key, out var cls) && string.Equals(cls, o.Value, StringComparison.OrdinalIgnoreCase));

    /// <summary>How far apart two packages' folders are: the closer one is the likelier new home.</summary>
    private static int Distance(string a, string b)
    {
        var x = a.Split('/');
        var y = b.Split('/');
        var shared = 0;
        while (shared < x.Length && shared < y.Length && string.Equals(x[shared], y[shared], StringComparison.OrdinalIgnoreCase))
        {
            shared++;
        }

        return x.Length + y.Length - 2 * shared;
    }
}
