namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// The mod editor's Undo/Redo: whole-mod snapshots, taken before each discrete structural action
/// (Add/Remove field, Add item, Duplicate, mass edit, a raw-JSON Apply, Apply rules) and before
/// the first write to a field after anything else happened — deliberately NOT per keystroke, so
/// Undo takes back one meaningful change instead of one character. Each entry is a full
/// serialized copy of the mod, never a reference: JsonNode/ExmodPackage are mutable, so anything
/// less would let a later edit corrupt an already-pushed snapshot. Capped so a long session
/// can't grow it without limit.
/// </summary>
public sealed class ExmodUndoHistory(int maxDepth = 20)
{
    private readonly List<string> _undo = [];

    /// <summary>What Undo took away, newest last. Any new change empties it (classic undo/redo).</summary>
    private readonly List<string> _redo = [];

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Remembers the mod as it is before a change; a new change (clearRedo) ends any redo chain.</summary>
    public void Push(string snapshot, bool clearRedo = true)
    {
        _undo.Add(snapshot);
        if (_undo.Count > maxDepth)
        {
            _undo.RemoveAt(0);
        }

        if (clearRedo)
        {
            _redo.Clear();
        }
    }

    /// <summary>The snapshot to go back to (null: nothing to undo); <paramref name="current"/> goes on the redo stack.</summary>
    public string? Undo(string current)
    {
        if (_undo.Count == 0)
        {
            return null;
        }

        var snapshot = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(current);
        return snapshot;
    }

    /// <summary>The snapshot the last Undo took away (null: nothing to redo); <paramref name="current"/> goes back on the undo stack.</summary>
    public string? Redo(string current)
    {
        if (_redo.Count == 0)
        {
            return null;
        }

        var snapshot = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Push(current, clearRedo: false);
        return snapshot;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
