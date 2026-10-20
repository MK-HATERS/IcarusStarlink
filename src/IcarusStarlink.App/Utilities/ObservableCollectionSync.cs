using System.Collections.ObjectModel;

namespace IcarusStarlink.App.Utilities;

/// <summary>
/// Updates an ObservableCollection to match a target sequence via targeted Remove/Insert/Move
/// instead of Clear()+re-Add(): WPF's ItemsControl (TreeView included) regenerates every
/// container from scratch on the Reset notification Clear() raises, even for items whose object
/// identity didn't change — losing state (selection highlight, expanded/collapsed) tied to
/// containers that didn't actually need to go away. Add/Remove/Move notifications let it update
/// incrementally instead.
/// </summary>
internal static class ObservableCollectionSync
{
    public static void SyncTo<T>(ObservableCollection<T> current, IReadOnlyList<T> target) where T : class
    {
        var targetSet = new HashSet<T>(target);
        for (var i = current.Count - 1; i >= 0; i--)
        {
            if (!targetSet.Contains(current[i]))
            {
                current.RemoveAt(i);
            }
        }

        // Equality (not only reference) and a search from i onward: two EQUAL items in the target
        // (two record rows with the same values) used to find the first one's index again, already
        // placed before i, and Move it past the end of the list (ArgumentOutOfRangeException).
        for (var i = 0; i < target.Count; i++)
        {
            if (i < current.Count && Equals(current[i], target[i]))
            {
                continue;
            }

            var existingIndex = -1;
            for (var j = i + 1; j < current.Count; j++)
            {
                if (Equals(current[j], target[i]))
                {
                    existingIndex = j;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                current.Move(existingIndex, i);
            }
            else
            {
                current.Insert(i, target[i]);
            }
        }
    }
}
