using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>Help's search box over the topic list.</summary>
public class HelpViewModelTests
{
    private static readonly HelpTopic[] Topics =
    [
        new("Library", "# Library\nYour mods, with Pin and Notes."),
        new("Saves", "# Saves\nCharacters and the mounts in your Library prospects."),
        new("Server", "# Server\nRun a dedicated server."),
    ];

    [Fact]
    public void Filter_Empty_KeepsEveryTopicInOrder() =>
        Assert.Equal(Topics, HelpViewModel.Filter(Topics, "  "));

    [Fact]
    public void Filter_TitleMatchesComeBeforeTextMatches() =>
        Assert.Equal(["Library", "Saves"], HelpViewModel.Filter(Topics, "library").Select(t => t.Title));

    [Fact]
    public void Filter_EveryWordMustMatch_IgnoringCase() =>
        Assert.Equal(["Saves"], HelpViewModel.Filter(Topics, "MOUNTS library").Select(t => t.Title));

    [Fact]
    public void Filter_NoMatch_IsEmpty() => Assert.Empty(HelpViewModel.Filter(Topics, "zzzz"));

    [Fact]
    public void SearchText_KeepsTheOpenTopicWhileItMatches_ElsePicksTheFirstMatch()
    {
        var vm = new HelpViewModel();
        var library = vm.Topics.First(t => t.Title == "Library");
        vm.SelectedTopic = library;

        vm.SearchText = "library";
        Assert.Same(library, vm.SelectedTopic);

        vm.SearchText = "keyboard shortcuts";
        Assert.Equal("Keyboard shortcuts", vm.SelectedTopic?.Title);
        Assert.False(vm.HasNoMatches);

        vm.SearchText = "no topic has this zzqq";
        Assert.True(vm.HasNoMatches);
        Assert.Null(vm.SelectedTopic);

        vm.SearchText = "";
        Assert.Equal(vm.Topics.Count, vm.VisibleTopics.Count);
        Assert.Equal(vm.Topics[0], vm.SelectedTopic);
    }
}
