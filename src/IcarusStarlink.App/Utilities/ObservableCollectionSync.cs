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

        for (var i = 0; i < target.Count; i++)
        {
            if (i < current.Count && ReferenceEquals(current[i], target[i]))
            {
                continue;
            }

            var existingIndex = current.IndexOf(target[i]);
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
