namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// Which flag IDs the Saves editor lists: every ID the game's own table defines (0..known-1), then
/// each ID the save carries that the table doesn't know, individually and in order. Never the dense
/// 0..max range the editor used before (an audit finding): a single corrupt or future-format ID like
/// 0x4C4F4144 meant allocating 1.28 billion rows on the UI thread and dying with OutOfMemory.
/// </summary>
internal static class SaveFlagIds
{
    public static IEnumerable<int> KnownThenExtra(int knownCount, IEnumerable<int> setIds)
    {
        for (var id = 0; id < knownCount; id++)
        {
            yield return id;
        }

        foreach (var extra in setIds.Where(id => id >= knownCount).OrderBy(id => id))
        {
            yield return extra;
        }
    }
}
