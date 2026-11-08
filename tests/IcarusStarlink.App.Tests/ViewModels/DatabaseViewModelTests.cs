using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>The Database page: "nexus" and "imm-database" hand-offs pick its tab.</summary>
public sealed class DatabaseViewModelTests
{
    [Fact]
    public void Handoffs_PickTheTab()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new DatabaseViewModel(null!, null!, messenger);

        messenger.Send(new NavigateToPageMessage("imm-database"));
        Assert.Equal(DatabaseViewModel.ImmDatabaseTab, vm.SelectedTab);

        messenger.Send(new NavigateToPageMessage("nexus"));
        Assert.Equal(DatabaseViewModel.NexusTab, vm.SelectedTab);

        vm.SelectedTab = DatabaseViewModel.ImmDatabaseTab;
        messenger.Send(new NavigateToPageMessage("library"));
        Assert.Equal(DatabaseViewModel.ImmDatabaseTab, vm.SelectedTab);
    }
}
