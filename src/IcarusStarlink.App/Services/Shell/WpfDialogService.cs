using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using IcarusStarlink.App.Views;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.PakIO.Install;

namespace IcarusStarlink.App.Services;

/// <summary>Real, production implementation of IDialogService — the only place that actually constructs these WPF Windows.</summary>
public sealed class WpfDialogService : IDialogService
{
    public bool Confirm(string message, string title, ThemedConfirmSeverity severity) =>
        ThemedConfirmDialog.Show(OwnerWindow(), message, title, severity);

    public bool? ConfirmWithCancel(string message, string title, ThemedConfirmSeverity severity, string yesText, string noText) =>
        ThemedConfirmDialog.ShowWithCancel(OwnerWindow(), message, title, severity, yesText, noText);

    public ReplaceExistingAnswer AskReplaceExisting(string message) =>
        ConfirmWithCancel(message, "Already in your Library", ThemedConfirmSeverity.Question, "Replace it", "Keep both") switch
        {
            true => ReplaceExistingAnswer.Replace,
            false => ReplaceExistingAnswer.KeepBoth,
            null => ReplaceExistingAnswer.Cancel,
        };

    public int? Choose(string message, string title, ThemedConfirmSeverity severity, string first, string second, string third) =>
        ThemedConfirmDialog.ShowChoice(OwnerWindow(), message, title, severity, first, second, third);

    public bool ConfirmWithWords(string message, string title, ThemedConfirmSeverity severity, string yesText, string noText) =>
        ThemedConfirmDialog.ShowWithWords(OwnerWindow(), message, title, severity, yesText, noText);

    public RenamePromptResult PromptRename(
        string currentName,
        string description = "Changes how this mod displays in your Library only — its real folder, file name, and mod content are never touched.",
        string? resetValue = null,
        string resetLabel = "Reset to default",
        string resetTooltip = "Clears the override — goes back to the mod's own declared name",
        string title = "Rename mod",
        string fieldLabel = "Display name")
    {
        var dialog = new RenameModDialog(currentName, description, resetValue, resetLabel, resetTooltip, title, fieldLabel)
        {
            Owner = OwnerWindow(),
        };

        return dialog.ShowDialog() == true
            ? new RenamePromptResult(Cancelled: false, dialog.NewDisplayName)
            : new RenamePromptResult(Cancelled: true, NewDisplayName: null);
    }

    public string? PickFile(string title, string filter, string? initialDirectory = null) =>
        PickFiles(title, filter, initialDirectory, multiselect: false).FirstOrDefault();

    public IReadOnlyList<string> PickFiles(string title, string filter, string? initialDirectory = null) =>
        PickFiles(title, filter, initialDirectory, multiselect: true);

    private static IReadOnlyList<string> PickFiles(string title, string filter, string? initialDirectory, bool multiselect)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, Multiselect = multiselect };
        if (initialDirectory is { Length: > 0 } && System.IO.Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return Show(dialog) == true ? dialog.FileNames : [];
    }

    public string? PickFolder(string title, string? initialDirectory = null)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (initialDirectory is { Length: > 0 } && System.IO.Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return Show(dialog) == true ? dialog.FolderName : null;
    }

    public string? PickSaveFile(string title, string filter, string defaultFileName, string? initialDirectory = null)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = title, Filter = filter, FileName = defaultFileName };
        if (initialDirectory is { Length: > 0 } && System.IO.Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return Show(dialog) == true ? dialog.FileName : null;
    }

    public NexusModFile? PickNexusFile(string modName, IReadOnlyList<NexusModFile> files, NexusModFile? preselect)
    {
        var dialog = new PickNexusFileDialog(modName, files, preselect) { Owner = OwnerWindow() };
        return dialog.ShowDialog() == true ? dialog.SelectedFile : null;
    }

    public PreviousInstall? PickPreviousInstall(IReadOnlyList<PreviousInstall> kept, int keptSetting)
    {
        var dialog = new PickPreviousInstallDialog(kept, keptSetting) { Owner = OwnerWindow() };
        return dialog.ShowDialog() == true ? dialog.SelectedInstall : null;
    }

    public void ShowNexusChangelog(string modName, IReadOnlyList<ChangelogVersionEntry> versions) =>
        new NexusChangelogWindow(modName, versions) { Owner = Application.Current?.MainWindow }.Show();

    private static bool? Show(Microsoft.Win32.CommonDialog dialog) => OwnerWindow() is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();

    /// <summary>
    /// The active window (the Find the broken mod window, an editor over the main one), so a
    /// question or picker opens over what the user is looking at. When Starlink has no active
    /// window (the user is in another program while a long job runs), the front-most of its other
    /// open windows, not the main window: a question owned by the main window opened behind the
    /// Game update window, which sits over it, and the job waited on an answer nobody could see.
    /// That window is brought forward (or flashes in the taskbar when Windows won't let it take
    /// the focus). Only a window that is loaded and still has its handle: a closed window must
    /// never be an owner. Null when none is usable, and the dialog then opens in the middle of
    /// the screen.
    /// </summary>
    private static Window? OwnerWindow()
    {
        if (Application.Current is not { } app)
        {
            return null;
        }

        var windows = app.Windows.OfType<Window>().ToList();
        var order = ZOrderOfTopLevelWindows();
        var index = PickOwner([.. windows.Select(w =>
        {
            var handle = new WindowInteropHelper(w).Handle;
            return new OwnerCandidate(
                w.IsActive,
                IsUsable: w.IsLoaded && PresentationSource.FromVisual(w) is not null,
                w.IsVisible,
                IsMain: ReferenceEquals(w, app.MainWindow),
                ZOrder: handle != IntPtr.Zero && order.TryGetValue(handle, out var z) ? z : int.MaxValue);
        })]);
        if (index < 0)
        {
            return null;
        }

        var owner = windows[index];
        if (!owner.IsActive)
        {
            owner.Activate();
        }

        return owner;
    }

    /// <summary>A window as OwnerWindow sees it. ZOrder: 0 is the front-most window on screen, higher is further back.</summary>
    internal readonly record struct OwnerCandidate(bool IsActive, bool IsUsable, bool IsVisible, bool IsMain, int ZOrder);

    /// <summary>
    /// Which window owns a dialog: the active one; else the front-most visible window other than
    /// the main one (those sit over the main window, so a dialog owned by the main window would
    /// open behind them); else the main window. -1 when no window is usable.
    /// </summary>
    internal static int PickOwner(IReadOnlyList<OwnerCandidate> windows)
    {
        var best = -1;
        for (var i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            if (!window.IsUsable)
            {
                continue;
            }

            if (window.IsActive)
            {
                return i;
            }

            if (best < 0 || Rank(window).CompareTo(Rank(windows[best])) < 0)
            {
                best = i;
            }
        }

        return best;

        // Visible windows over the main one first, front-most first; the main window after them.
        static (int Group, int ZOrder) Rank(OwnerCandidate w) => (w.IsMain ? 1 : w.IsVisible ? 0 : 2, w.ZOrder);
    }

    /// <summary>Every top-level window on screen by its handle, front-most first (0).</summary>
    private static Dictionary<IntPtr, int> ZOrderOfTopLevelWindows()
    {
        var order = new Dictionary<IntPtr, int>();
        const uint next = 2; // GW_HWNDNEXT
        var handle = GetTopWindow(IntPtr.Zero);
        // The cap is only against a list that changes while it is walked.
        for (var i = 0; handle != IntPtr.Zero && i < 20000; i++)
        {
            order.TryAdd(handle, i);
            handle = GetWindow(handle, next);
        }

        return order;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr parent);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr handle, uint command);
}
