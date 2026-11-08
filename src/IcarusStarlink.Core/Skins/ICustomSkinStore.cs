namespace IcarusStarlink.Core.Skins;

public interface ICustomSkinStore
{
    /// <summary>Where the skin file lives on disk — surfaced so Settings can offer "open the file" for hand-editing.</summary>
    string FilePath { get; }

    /// <summary>Null when no skin file exists yet (distinct from an empty/corrupt one, which loads as an empty skin) — the caller uses that to create a starting-point file.</summary>
    CustomSkin? Load();

    /// <summary>Load, plus why an existing file couldn't be used (in words: not valid JSON, or open in another program); null when it loaded or doesn't exist.</summary>
    CustomSkin? Load(out string? problem)
    {
        problem = null;
        return Load();
    }

    void Save(CustomSkin skin);
}
