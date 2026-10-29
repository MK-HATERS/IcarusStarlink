using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core;

namespace IcarusStarlink.App.Views;

/// <summary>
/// The one-time move-over window: lists what ExeRenameMigration will change, runs it on "Do it now"
/// and shows each step with a tick or a cross. onDone runs only when every step worked (the caller
/// records AppSettings.ExeRenameDone); anything else is asked again at the next start.
/// </summary>
public partial class ExeRenameWindow : Window
{
    private readonly ExeRenameMigration _migration;
    private readonly Action _onDone;
    private readonly ObservableCollection<StepRow> _rows = [];
    private ExeRenamePlan _plan;
    private bool _finished;

    public ExeRenameWindow(Window? owner, ExeRenameMigration migration, ExeRenamePlan plan, Action onDone)
    {
        InitializeComponent();
        Owner = owner;
        _migration = migration;
        _plan = plan;
        _onDone = onDone;
        StepList.ItemsSource = _rows;
        ShowPlan();
    }

    private void ShowPlan()
    {
        _rows.Clear();
        foreach (var link in _plan.Shortcuts)
        {
            _rows.Add(Pending($"Point the shortcut \"{Path.GetFileNameWithoutExtension(link)}\" at {StarlinkExe.FileName}", Path.GetDirectoryName(link)));
        }

        if (_plan.NxmUsesOldExe)
        {
            _rows.Add(Pending($"Open Nexus \"Mod Manager Download\" links with {StarlinkExe.FileName}"));
        }

        if (_plan.OldExeExists)
        {
            _rows.Add(Pending($"Delete {StarlinkExe.OldFileName} and check it's gone", _migration.OldExePath));
        }
    }

    private async void Go_Click(object sender, RoutedEventArgs e)
    {
        if (_finished)
        {
            Close();
            return;
        }

        GoButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        GoButton.Content = "Working…";
        IReadOnlyList<ExeRenameStep> steps;
        try
        {
            // A retry plans again: a shortcut fixed by hand meanwhile is no longer listed.
            var plan = _plan;
            steps = await Task.Run(() => _migration.Run(plan));
        }
        catch (Exception ex)
        {
            steps = [new ExeRenameStep($"Something went wrong: {ex.Message}", false)];
        }

        _rows.Clear();
        foreach (var step in steps)
        {
            _rows.Add(new StepRow(step.Succeeded ? "✓" : "✗", (Brush)FindResource(step.Succeeded ? "SuccessBrush" : "DangerBrush"), step.Text, step.Detail));
        }

        if (steps.All(step => step.Succeeded))
        {
            _onDone();
            _finished = true;
            NoteText.Text = $"All done. Start Starlink from {StarlinkExe.FileName} from now on; updates won't bring the old exe back.";
            GoButton.Content = "Close";
            GoButton.IsEnabled = true;
            LaterButton.Visibility = Visibility.Collapsed;
            return;
        }

        NoteText.Text = $"Not everything worked, so {StarlinkExe.OldFileName} stays for now and Starlink asks again next time it starts. Fix what's marked ✗, then Try again.";
        _plan = await Task.Run(_migration.Plan);
        GoButton.Content = "Try again";
        LaterButton.Content = "Close";
        GoButton.IsEnabled = true;
        LaterButton.IsEnabled = true;
    }

    private void Later_Click(object sender, RoutedEventArgs e) => Close();

    private StepRow Pending(string text, string? detail = null) =>
        new("•", (Brush)FindResource("AccentBrush"), text, detail);

    public sealed record StepRow(string Mark, Brush MarkBrush, string Text, string? Detail);
}
