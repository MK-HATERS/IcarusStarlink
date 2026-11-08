namespace IcarusStarlink.App.ViewModels;

/// <summary>The queue as the Editor page reads it: the same parts, order, game values and saved winners as the conflict check and the build.</summary>
public sealed partial class MergeInstallViewModel : IMergeQueueReader
{
    public async Task<MergeQueueState> ReadQueueAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = TakeSnapshot();
        var loaded = await LoadQueuedPackagesAsync(snapshot.Queue);
        var (changes, names, ids) = await QueueChangesAsync(snapshot, loaded, cancellationToken);
        var baseTables = await Task.Run(() => TryBaseTablesFor(changes), cancellationToken);
        return new MergeQueueState(ids, names, changes, baseTables, snapshot.Winners, snapshot.Options.StacksMultiplier, snapshot.Options.SlotsMultiplier);
    }

    /// <summary>The Editor's "Move my mod last" (asked there first): the same move as dragging the row to the bottom.</summary>
    public bool MoveToEnd(string folderName)
    {
        if (RefuseWhileRebuilding() || Queue.FirstOrDefault(e => string.Equals(e.FolderName, folderName, StringComparison.OrdinalIgnoreCase)) is not { } entry)
        {
            return false;
        }

        var index = Queue.IndexOf(entry);
        if (index < Queue.Count - 1)
        {
            Queue.Move(index, Queue.Count - 1);
        }

        return true;
    }
}
