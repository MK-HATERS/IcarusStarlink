using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>The Database page: "nexus" and "imm-database" hand-offs pick its tab.</summary>
[Collection(MessengerCollection.Name)]
public sealed class DatabaseViewModelTests
{
    [Fact]
    public void Handoffs_PickTheTab()
    {
        var vm = new DatabaseViewModel(null!, null!);
        try
        {
            WeakReferenceMessenger.Default.Send(new NavigateToPageMessage("imm-database"));
            Assert.Equal(DatabaseViewModel.ImmDatabaseTab, vm.SelectedTab);

            WeakReferenceMessenger.Default.Send(new NavigateToPageMessage("nexus"));
            Assert.Equal(DatabaseViewModel.NexusTab, vm.SelectedTab);

            vm.SelectedTab = DatabaseViewModel.ImmDatabaseTab;
            WeakReferenceMessenger.Default.Send(new NavigateToPageMessage("library"));
            Assert.Equal(DatabaseViewModel.ImmDatabaseTab, vm.SelectedTab);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(vm);
        }
    }
}
