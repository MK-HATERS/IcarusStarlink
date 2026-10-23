using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Versions;

namespace IcarusStarlink.PakIO.Assets;

/// <summary>
/// What a prebuilt mod's own .pak files hold, read in place with CUE4Parse (nothing extracted, no
/// UnrealPak): every file as its path under the game's Content folder — the mount point stripped,
/// the same place PakMountPoints.RestageUnderGameContent puts an extracted file — plus its bytes
/// and a blueprint's summary. Lets the risk pill rate a mod that was never converted to an EXMOD.
/// Files mounted outside Content can't be placed and are left out. Dispose when done.
/// </summary>
public sealed class ModPakContents : IDisposable
{
    private static readonly VersionContainer Versions = new(EGame.GAME_UE4_27);

    /// <summary>A mounted key under the game's Content root starts with this (mount point "../../../Icarus/Content/" and deeper).</summary>
    private const string ContentKeyPrefix = "Icarus/Content/";

    private readonly DefaultFileProvider _provider;
    private readonly Dictionary<string, string> _keyByContentPath;

    private ModPakContents(DefaultFileProvider provider, Dictionary<string, string> keyByContentPath)
    {
        _provider = provider;
        _keyByContentPath = keyByContentPath;
    }

    /// <summary>Every file the paks hold, as Content-relative paths ("BP/Player/BP_X.uasset").</summary>
    public IReadOnlyList<string> ContentPaths => [.. _keyByContentPath.Keys.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Mounts the .pak files directly in <paramref name="modFolderPath"/>; null when there are none or they can't be read.</summary>
    public static ModPakContents? TryOpen(string modFolderPath)
    {
        if (!Directory.Exists(modFolderPath) || Directory.GetFiles(modFolderPath, "*.pak", SearchOption.TopDirectoryOnly).Length == 0)
        {
            return null;
        }

        DefaultFileProvider? provider = null;
        try
        {
            provider = new DefaultFileProvider(modFolderPath, SearchOption.TopDirectoryOnly, Versions, StringComparer.OrdinalIgnoreCase)
            {
                // Function code too, for comparing the mod's copy of a game blueprint with the game's.
                ReadScriptData = true,
            };
            provider.Initialize();
            provider.Mount();

            var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in provider.Files.Keys)
            {
                var normalized = key.Replace('\\', '/');
                if (normalized.StartsWith(ContentKeyPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    keys[normalized[ContentKeyPrefix.Length..]] = key;
                }
            }

            return new ModPakContents(provider, keys);
        }
        catch (Exception)
        {
            provider?.Dispose();
            return null;
        }
    }

    /// <summary>One file's bytes; null when it isn't there or can't be read.</summary>
    public byte[]? TryRead(string contentPath)
    {
        if (!_keyByContentPath.TryGetValue(contentPath.Replace('\\', '/').TrimStart('/'), out var key))
        {
            return null;
        }

        try
        {
            return _provider.Files[key].Read();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The first export of type T in the paks' package at this Content path (a growth curve); null when it isn't there or can't be read.</summary>
    public T? TryLoadFirstExport<T>(string contentPath) where T : class
    {
        if (!_keyByContentPath.TryGetValue(contentPath.Replace('\\', '/').TrimStart('/'), out var key))
        {
            return null;
        }

        try
        {
            return _provider.LoadPackage(key).GetExports().OfType<T>().FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The pak's copy of a blueprint, summarised for comparing with the game's; null when it isn't there or can't be read.</summary>
    public BlueprintSummary? TrySummarizeBlueprint(string contentPath)
    {
        if (!_keyByContentPath.TryGetValue(contentPath.Replace('\\', '/').TrimStart('/'), out var key))
        {
            return null;
        }

        try
        {
            return BlueprintComparer.Summarize(_provider.LoadPackage(key));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose() => _provider.Dispose();
}
