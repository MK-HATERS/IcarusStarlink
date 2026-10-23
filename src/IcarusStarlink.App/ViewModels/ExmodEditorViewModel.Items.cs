using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Views;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One problem the reference check found; clicking it selects the row in the editor.</summary>
public sealed record ReferenceProblemViewModel(string Text, string CurrentFile, string RowName);

/// <summary>New items made from a game item (the wizard), and the "does everything it links to exist?" check.</summary>
public sealed partial class ExmodEditorViewModel
{
    private GameTableIndex? _gameTables;

    /// <summary>The game's tables, read lazily and kept until Refresh game data rewrites them (OnGameDataRefreshed).</summary>
    private GameTableIndex GameTables => _gameTables ??= new GameTableIndex(_dataFolder);

    public ObservableCollection<ReferenceProblemViewModel> ReferenceProblems { get; } = [];

    [ObservableProperty]
    private bool _showReferenceProblems;

    [ObservableProperty]
    private string? _referenceSummary;

    /// <summary>Test seam: shows the wizard and returns true when the user added the item. Null = the real window.</summary>
    internal Func<NewItemWizardViewModel, bool>? ShowWizardOverride { get; set; }

    [RelayCommand]
    private void NewItemFromGame() => StartNewItemWizard(null);

    /// <summary>Opens the new-item wizard, preset by an item template (Envirosuit, Module …) when given, and adds what it builds.</summary>
    public void StartNewItemWizard(NewItemPreset? preset)
    {
        if (!Directory.Exists(_dataFolder) || GameTables.CurrentFileFor("D_ItemsStatic") is null)
        {
            StatusMessage = "No game data found. Run Refresh game data in Settings first.";
            return;
        }

        var wizard = new NewItemWizardViewModel(GameTables, _package, preset);
        var added = ShowWizardOverride?.Invoke(wizard)
            ?? new NewItemWizardWindow(wizard) { Owner = OwnerWindow() }.ShowDialog() == true;
        if (!added || wizard.Result is not { } rows)
        {
            return;
        }

        PushUndoSnapshot();
        ItemCloner.AddToPackage(_package, rows);
        RefreshBaseDiff();
        ReloadItems();

        var item = rows.FirstOrDefault(r => Same(r.CurrentFile, "Items-D_ItemsStatic.json"))?.FileItems.FirstOrDefault();
        SelectedItem = Items.FirstOrDefault(i => Same(i.CurrentFile, "Items-D_ItemsStatic.json") && Same(i.ItemName, item?.Name)) ?? SelectedItem;
        var tables = string.Join(", ", rows.Select(r => GameTableIndex.TableNameOf(r.CurrentFile)));
        StatusMessage = $"Added '{item?.Name}': {rows.Sum(r => r.FileItems.Count)} row(s) in {tables}. Change what you like, then Save.";
    }

    [RelayCommand]
    private void CheckReferences()
    {
        if (!Directory.Exists(_dataFolder))
        {
            StatusMessage = "No game data found. Run Refresh game data in Settings first.";
            return;
        }

        RunReferenceCheck(showWhenClean: true);
    }

    [RelayCommand]
    private void HideReferenceProblems() => ShowReferenceProblems = false;

    [RelayCommand]
    private void GoToReferenceProblem(ReferenceProblemViewModel? problem)
    {
        if (problem is null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(FilterText))
        {
            FilterText = "";
            ReloadItems();
        }

        SelectedItem = Items.FirstOrDefault(i => Same(i.CurrentFile, problem.CurrentFile) && Same(i.ItemName, problem.RowName)) ?? SelectedItem;
        ViewMode = ExmodEditorViewMode.ItemFields;
    }

    /// <summary>Runs the check; returns how many problems it found. Fills and shows the panel when there are any (or always, when asked).</summary>
    private int RunReferenceCheck(bool showWhenClean)
    {
        ReferenceReport report;
        try
        {
            report = ReferenceChecker.Check(_package, GameTables, ModAssetBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ReferenceSummary = $"Couldn't check: {ex.Message}";
            ShowReferenceProblems = showWhenClean;
            return 0;
        }

        ReferenceProblems.Clear();
        foreach (var broken in report.Broken)
        {
            ReferenceProblems.Add(new ReferenceProblemViewModel(broken.Describe(), broken.CurrentFile, broken.RowName));
        }

        foreach (var unused in report.Unused)
        {
            ReferenceProblems.Add(new ReferenceProblemViewModel(unused.Describe(), unused.CurrentFile, unused.RowName));
        }

        var count = ReferenceProblems.Count;
        ReferenceSummary = count == 0
            ? "Every link in this mod points at a row that exists, in the game or in the mod."
            : $"{count} problem(s). A link to a row that doesn't exist is ignored by the game or breaks the item, with no message. Click one to go to it.";
        ShowReferenceProblems = count > 0 || showWhenClean;
        return count;
    }

    /// <summary>The mod's own blueprints, which can use a row by name.</summary>
    private List<byte[]> ModAssetBytes()
    {
        var folder = _repository.GetFolderPath(_folderName);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(folder, "*.uasset", SearchOption.AllDirectories).Select(File.ReadAllBytes)];
    }

    /// <summary>
    /// Refresh game data (Settings) rewrites the data folder under an open editor: every table,
    /// name and choice read from it is dropped and read again, so game values, change counts and
    /// "new row" marks match the new data. The mod itself (typed values included) is untouched.
    /// </summary>
    private void OnGameDataRefreshed()
    {
        _gameTables = null;
        _baseTableCache.Clear();
        _itemNames = null;
        _statChoices = null;
        _gameDataMissing = false;
        _fieldChoices.Clear();
        _fieldOptionsCache.Clear();
        _scaleTables = null;
        OnPropertyChanged(nameof(ScaleTables));
        RefreshBaseDiff();
        ApplyFilter();
    }

    /// <summary>
    /// What a dialog opens over: whichever of this editor's windows is in front (the editor, or a
    /// pane popped out of it), else its editor window. Dialogs owned by the main window opened
    /// behind the editor, and the close prompt could pick another mod's active editor.
    /// </summary>
    private Window? OwnerWindow()
    {
        var mine = Application.Current?.Windows.OfType<Window>().Where(w => ReferenceEquals(w.DataContext, this)).ToList() ?? [];
        return mine.FirstOrDefault(w => w.IsActive) ?? EditorWindow() ?? mine.FirstOrDefault() ?? Application.Current?.MainWindow;
    }

    private ExmodEditorWindow? EditorWindow() =>
        Application.Current?.Windows.OfType<ExmodEditorWindow>().FirstOrDefault(w => ReferenceEquals(w.DataContext, this));
}
