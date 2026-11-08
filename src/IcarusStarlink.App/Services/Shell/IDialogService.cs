using IcarusStarlink.App.Views;
using IcarusStarlink.Catalog.Nexus;

namespace IcarusStarlink.App.Services;

/// <summary>
/// The seam for every dialog a view model needs an answer from — questions, pickers, the Nexus
/// file choice and changelog — so view-model logic can be tested with a fake that returns a
/// canned answer (a real Window needs a live WPF Dispatcher). WpfDialogService is the real
/// implementation.
/// </summary>
public interface IDialogService
{
    /// <summary>Same call shape as the ThemedMessageBox.Show static it replaces.</summary>
    bool Confirm(string message, string title, ThemedConfirmSeverity severity);

    /// <summary>
    /// Three answers, with the caller's words on the first two buttons: true = <paramref name="yesText"/>,
    /// false = <paramref name="noText"/>, null = Cancel (or the window closed). The default answers
    /// "cancelled", so a test fake only sets what it needs.
    /// </summary>
    bool? ConfirmWithCancel(string message, string title, ThemedConfirmSeverity severity, string yesText, string noText) => null;

    /// <summary>
    /// Three answers and Cancel, with the caller's words: 0 = <paramref name="first"/> (the main
    /// button), 1 = <paramref name="second"/>, 2 = <paramref name="third"/>, null = Cancel. The
    /// default answers "cancelled".
    /// </summary>
    int? Choose(string message, string title, ThemedConfirmSeverity severity, string first, string second, string third) => null;

    /// <summary>A plain two-button confirm with the caller's words on the buttons: true = <paramref name="yesText"/>. The default answers no.</summary>
    bool ConfirmWithWords(string message, string title, ThemedConfirmSeverity severity, string yesText, string noText) => false;

    /// <summary>
    /// Same three-outcome shape RenameModDialog itself has (see its own doc comment for what
    /// resetValue null vs a real string means): Cancelled true means the caller does nothing at
    /// all; Cancelled false with NewDisplayName null means "reset/clear the override" (only
    /// reachable when resetValue was null); Cancelled false with a NewDisplayName string means the
    /// user typed and saved a name, or clicked Reset with a non-null resetValue. Despite the
    /// method's name this is this app's one generic single-line text prompt — title defaults to
    /// "Rename mod" for the rename callers that already exist, but a caller prompting for something
    /// else entirely (e.g. a UE4SS mod's declared minimum loader version) should pass its own.
    /// </summary>
    RenamePromptResult PromptRename(
        string currentName,
        string description = "Changes how this mod displays in your Library only — its real folder, file name, and mod content are never touched.",
        string? resetValue = null,
        string resetLabel = "Reset to default",
        string resetTooltip = "Clears the override — goes back to the mod's own declared name",
        string title = "Rename mod",
        string fieldLabel = "Display name");

    /// <summary>
    /// A file to open; null when cancelled. Every page's file pickers go through these three
    /// (18 view models used to open Windows dialogs themselves, so those flows
    /// couldn't be tested). The defaults answer "cancelled", so a test fake only sets what it needs.
    /// </summary>
    /// <param name="filter">Windows filter text: "Mod files (*.zip;*.pak)|*.zip;*.pak".</param>
    string? PickFile(string title, string filter, string? initialDirectory = null) => null;

    /// <summary>Several files to open; empty when cancelled.</summary>
    IReadOnlyList<string> PickFiles(string title, string filter, string? initialDirectory = null) => [];

    /// <summary>A folder; null when cancelled.</summary>
    string? PickFolder(string title, string? initialDirectory = null) => null;

    /// <summary>Where to save a file; null when cancelled.</summary>
    string? PickSaveFile(string title, string filter, string defaultFileName, string? initialDirectory = null) => null;

    /// <summary>Which of a Nexus mod's files to download, <paramref name="preselect"/> chosen to start with; null when cancelled.</summary>
    NexusModFile? PickNexusFile(string modName, IReadOnlyList<NexusModFile> files, NexusModFile? preselect) => null;

    /// <summary>Shows a Nexus mod's changelog in its own window, without waiting for it to close.</summary>
    void ShowNexusChangelog(string modName, IReadOnlyList<ChangelogVersionEntry> versions)
    {
    }
}

public sealed record RenamePromptResult(bool Cancelled, string? NewDisplayName);
