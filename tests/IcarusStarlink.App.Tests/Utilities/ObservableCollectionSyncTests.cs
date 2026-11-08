using System.Collections.ObjectModel;
using IcarusStarlink.App.Utilities;

namespace IcarusStarlink.App.Tests.Utilities;

public class ObservableCollectionSyncTests
{
    private sealed record Row(string Name);

    /// <summary>Two equal record rows in the target (two downloads of the same mod) used to Move past the end of the list and throw.</summary>
    [Fact]
    public void SyncTo_TargetWithTwoEqualItems_DoesNotThrowAndMatches()
    {
        var current = new ObservableCollection<Row>([new Row("A")]);

        ObservableCollectionSync.SyncTo(current, [new Row("A"), new Row("A")]);

        Assert.Equal([new Row("A"), new Row("A")], current);
    }

    /// <summary>Regression: with more equal record rows than the target had, the extras stayed at the end of the list.</summary>
    [Fact]
    public void SyncTo_CurrentWithMoreEqualItemsThanTheTarget_DropsTheExtras()
    {
        var twice = new ObservableCollection<Row>([new Row("A"), new Row("A")]);
        ObservableCollectionSync.SyncTo(twice, [new Row("A")]);
        Assert.Equal([new Row("A")], twice);

        var mixed = new ObservableCollection<Row>([new Row("A"), new Row("B"), new Row("A")]);
        ObservableCollectionSync.SyncTo(mixed, [new Row("B"), new Row("A")]);
        Assert.Equal([new Row("B"), new Row("A")], mixed);
    }

    [Fact]
    public void SyncTo_ReordersRemovesAndInserts_KeepingExistingInstances()
    {
        var a = new Row("A");
        var b = new Row("B");
        var current = new ObservableCollection<Row>([a, b, new Row("C")]);

        ObservableCollectionSync.SyncTo(current, [b, new Row("D"), a]);

        Assert.Equal([b, new Row("D"), a], current);
        Assert.Same(a, current[2]);
        Assert.Same(b, current[0]);
    }
}
