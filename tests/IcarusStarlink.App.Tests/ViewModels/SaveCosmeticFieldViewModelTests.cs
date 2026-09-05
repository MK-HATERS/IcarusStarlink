using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Tests.ViewModels;

public class SaveCosmeticFieldViewModelTests
{
    /// <summary>
    /// Regression guard: MarkClean() used to be fully unconditional, adopting even unparsable
    /// ValueText as the new "clean" baseline — but SaveCharacterViewModel.ApplyToNode()'s Cosmetics
    /// loop only writes a field when `long.TryParse(field.ValueText, out var hash)` succeeds, so an
    /// unparsable edit was silently never written while MarkClean still cleared the dirty flag,
    /// showing "Saved" with the real cosmetic hash on disk unchanged.
    /// </summary>
    [Fact]
    public void MarkClean_UnparsableValueText_NotAdoptedAsBaseline_StaysDirty()
    {
        var field = new SaveCosmeticFieldViewModel("Customization_Head", "-506683013", () => { });
        field.ValueText = "not a number";

        field.MarkClean();

        Assert.True(field.IsDirty);
    }

    [Fact]
    public void MarkClean_ParsableValueText_AdoptedAsBaseline_BecomesClean()
    {
        var field = new SaveCosmeticFieldViewModel("Customization_Head", "-506683013", () => { });
        field.ValueText = "12345";

        field.MarkClean();

        Assert.False(field.IsDirty);
    }
}
