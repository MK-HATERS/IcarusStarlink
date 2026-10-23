using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Tests.ViewModels;

public class ExmodUndoHistoryTests
{
    [Fact]
    public void UndoThenRedo_WalkBackAndForth()
    {
        var history = new ExmodUndoHistory();
        history.Push("A");
        history.Push("B");

        Assert.Equal("B", history.Undo("C"));
        Assert.Equal("A", history.Undo("B"));
        Assert.Null(history.Undo("A"));
        Assert.Equal("B", history.Redo("A"));
        Assert.Equal("C", history.Redo("B"));
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void ANewChange_EndsTheRedoChain()
    {
        var history = new ExmodUndoHistory();
        history.Push("A");
        history.Undo("B");

        history.Push("A");

        Assert.False(history.CanRedo);
        Assert.Null(history.Redo("X"));
    }

    [Fact]
    public void OnlyTheNewestSnapshotsAreKept()
    {
        var history = new ExmodUndoHistory(maxDepth: 2);
        history.Push("A");
        history.Push("B");
        history.Push("C");

        Assert.Equal("C", history.Undo("D"));
        Assert.Equal("B", history.Undo("C"));
        Assert.False(history.CanUndo);
    }
}
