using System.Text.Json.Nodes;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Diffing;

namespace IcarusStarlink.App.Tests.ViewModels;

public class ConflictPickerViewModelTests
{
    private static FieldChange ScalarChange(string item, int value) =>
        new("Items-D_ItemsStatic.json", item, "Damage", OriginalValue: null, JsonValue.Create(value), ValueSemantic.Scalar);

    private static FieldConflict MakeConflict(string item) => new(
        "Items-D_ItemsStatic.json", item, "Damage",
        [new ConflictCandidate("Mod A", ScalarChange(item, 10)), new ConflictCandidate("Mod B", ScalarChange(item, 20))]);

    [Fact]
    public void Constructor_NoNewItemCollisions_OnlyFieldConflictRows()
    {
        var viewModel = new ConflictPickerViewModel([MakeConflict("Sword")], existingPicks: null);

        var row = Assert.Single(viewModel.Rows);
        Assert.NotNull(row.Conflict);
    }

    [Fact]
    public void Constructor_CollisionsAndConflictsBoth_CollisionRowsComeFirst()
    {
        var collision = new NewItemNameCollision("Items-D_ItemsStatic.json", "Wooden_Table", ["Mod A", "Mod B"]);
        var conflict = MakeConflict("Sword");

        var viewModel = new ConflictPickerViewModel([conflict], existingPicks: null, [collision]);

        Assert.Equal(2, viewModel.Rows.Count);
        Assert.Null(viewModel.Rows[0].Conflict); // the collision row
        Assert.NotNull(viewModel.Rows[1].Conflict); // the ordinary field conflict row
    }

    [Fact]
    public void BuildPicks_NewItemCollisionRowPresent_DoesNotThrowAndContributesNoPick()
    {
        // The real risk this guards against: Conflict is null for a collision row, and BuildPicks
        // must never dereference it.
        var collision = new NewItemNameCollision("Items-D_ItemsStatic.json", "Wooden_Table", ["Mod A", "Mod B"]);
        var viewModel = new ConflictPickerViewModel([], existingPicks: null, [collision]);

        var picks = viewModel.BuildPicks();

        Assert.Empty(picks);
    }

    [Fact]
    public void BuildPicks_UserPicksACandidate_KeyMatchesTheConflictsOwnFieldIdentity()
    {
        var viewModel = new ConflictPickerViewModel([MakeConflict("Sword")], existingPicks: null);
        viewModel.Rows[0].SelectedOptionIndex = 1; // pick Candidates[0] ("Mod A")

        var picks = viewModel.BuildPicks();

        var pick = Assert.Single(picks);
        Assert.Equal(("Items-D_ItemsStatic.json", "Sword", "Damage"), pick.Key);
        Assert.Equal(0, pick.Value);
    }

    [Fact]
    public void BuildPicks_LeftOnDefault_ContributesNoPick()
    {
        var viewModel = new ConflictPickerViewModel([MakeConflict("Sword")], existingPicks: null);

        var picks = viewModel.BuildPicks();

        Assert.Empty(picks);
    }

    [Fact]
    public void Constructor_ExistingPicksGiven_PreselectsTheMatchingRow()
    {
        var existingPicks = new Dictionary<(string, string, string), int>
        {
            [("Items-D_ItemsStatic.json", "Sword", "Damage")] = 1, // "Mod B"
        };

        var viewModel = new ConflictPickerViewModel([MakeConflict("Sword")], existingPicks);

        Assert.Equal(2, viewModel.Rows[0].SelectedOptionIndex); // Options[0]=Default, [1]=Mod A, [2]=Mod B
    }
}
