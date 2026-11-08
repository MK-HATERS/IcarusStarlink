using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Library;

namespace IcarusStarlink.App.Tests.ViewModels;

public sealed partial class LibraryViewModelTests
{
    // ---- New mod: the version typed in the dialog ----

    [Fact]
    public void NewMod_PassesTheVersionTypedInTheDialogToTheNewMod()
    {
        var harness = new TestHarness();
        var vm = harness.Build();
        vm.AskForNewMod = () => new NewModRequest("My Mod", "Me", ModTemplate.Blank, "2.5");

        vm.NewModCommand.Execute(null);

        var call = Assert.Single(harness.Repository.CreateBlankModCalls);
        Assert.Equal(("My Mod", "Me", ModTemplate.Blank, "2.5"), call);
    }

    [Fact]
    public void NewMod_DialogCancelled_MakesNothing()
    {
        var harness = new TestHarness();
        var vm = harness.Build();
        vm.AskForNewMod = () => null;

        vm.NewModCommand.Execute(null);

        Assert.Empty(harness.Repository.CreateBlankModCalls);
    }
}
