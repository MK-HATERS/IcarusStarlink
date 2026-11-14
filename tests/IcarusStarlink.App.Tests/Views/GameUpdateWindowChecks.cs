using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.App.Views;

namespace IcarusStarlink.App.Tests.Views;

/// <summary>
/// The Game update window built against the app's styles (run by ViewSmokeTests through
/// SharedViewChecks): the ticks and Start before a run, and the list of fixes with Continue, Skip
/// fixes and Stop while a Show me first run waits. The window takes a view model, so the type scan
/// doesn't build it.
/// </summary>
internal static class GameUpdateWindowChecks
{
    internal static string? ChoicesThenFixList()
    {
        var viewModel = new GameUpdateReviewViewModel(new NoActions());
        var choosing = viewModel.ChooseAsync();
        var window = new GameUpdateWindow(viewModel);
        try
        {
            LayOut(window);
            foreach (var label in new[] { "Show me first", "Fix by itself", "Don't fix", "Redraw the maps that changed", "Check for mod updates", "Build the merge", "Install it in the game", "Start" })
            {
                if (!Shows(window, label))
                {
                    return $"before Start, '{label}' isn't shown";
                }
            }

            if (Shows(window, "Continue"))
            {
                return "the fix list's Continue shows before a run";
            }

            viewModel.StartCommand.Execute(null);
            if (!choosing.IsCompleted)
            {
                return "harness: Start didn't end the choosing";
            }

            var run = viewModel.RunAsync(new StepsWithOneFix());
            LayOut(window);
            if (!viewModel.IsReviewingFixes)
            {
                return "harness: the run didn't stop to show the fixes";
            }

            foreach (var label in new[] { "Continue", "Skip fixes", "Stop" })
            {
                if (!Shows(window, label))
                {
                    return $"while the fixes are listed, '{label}' isn't shown";
                }
            }

            if (Shows(window, "Start"))
            {
                return "Start still shows while the run waits on the fixes";
            }

            if (FindAll<CheckBox>(window).Count(c => IsShown(c) && c.DataContext is GameUpdateFixRow) != 1)
            {
                return "the listed mod has no tick of its own";
            }

            viewModel.StopRunCommand.Execute(null);
            _ = run;
            return viewModel.IsReviewingFixes ? "harness: Stop didn't end the wait" : null;
        }
        finally
        {
            window.Close();
        }
    }

    private static void LayOut(Window window)
    {
        if (window.Content is FrameworkElement content)
        {
            content.Measure(new Size(980, 760));
            content.Arrange(new Rect(0, 0, 980, 760));
            content.UpdateLayout();
        }
    }

    /// <summary>A button, tick or choice with this label that nothing above it hides.</summary>
    private static bool Shows(Window window, string label) =>
        FindAll<ContentControl>(window).Any(c => c is Button or CheckBox or RadioButton && Equals(c.Content, label) && IsShown(c));

    private static bool IsShown(DependencyObject element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }
        }

        return true;
    }

    private static List<T> FindAll<T>(DependencyObject parent) where T : DependencyObject
    {
        var found = new List<T>();
        if (parent is Window { Content: DependencyObject content })
        {
            parent = content;
        }

        Collect(parent, found);
        return found;

        static void Collect(DependencyObject node, List<T> into)
        {
            if (node is T match)
            {
                into.Add(match);
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                Collect(VisualTreeHelper.GetChild(node, i), into);
            }
        }
    }

    private sealed class NoActions : IGameUpdateReviewActions
    {
        public Task OpenFixWindowAsync(string folderName) => Task.CompletedTask;

        public Task GetUpdateAsync(string folderName, bool isUe4ss) => Task.CompletedTask;

        public Task<KeepOffResult> KeepOffAsync(string folderName) => Task.FromResult(new KeepOffResult(true, ""));

        public Task<string?> RollBackAsync(LastKnownGoodSnapshot snapshot) => Task.FromResult<string?>(null);
    }

    private sealed class StepsWithOneFix : IGameUpdateSteps
    {
        public Task<LastKnownGoodSnapshot> SnapshotAsync() => Task.FromResult(new LastKnownGoodSnapshot { CreatedAtUtc = DateTimeOffset.UtcNow });

        public Task RecordGameFingerprintsAsync() => Task.CompletedTask;

        public Task<GameUpdateStepResult> RefreshGameDataAsync() => Task.FromResult(GameUpdateStepResult.Done());

        public Task CheckModsAsync() => Task.CompletedTask;

        public Task<IReadOnlyList<GameUpdateFixCandidate>> ListSureFixesAsync() => Task.FromResult<IReadOnlyList<GameUpdateFixCandidate>>(
            [new("Bows", "Better Bows", 2, ["Old copies of game data: 2 in D_Itemable"])]);

        public Task<IReadOnlyList<GameUpdateFixedMod>> FixSureProblemsAsync() => Task.FromResult<IReadOnlyList<GameUpdateFixedMod>>([]);

        public Task CheckForModUpdatesAsync() => Task.CompletedTask;

        public Task<GameUpdateStepResult> RebuildAsync() => Task.FromResult(GameUpdateStepResult.Done());

        public bool IsGameRunning() => false;

        public Task<GameUpdateStepResult> InstallAsync() => Task.FromResult(GameUpdateStepResult.Done());
    }
}
