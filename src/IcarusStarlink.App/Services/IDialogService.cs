using IcarusStarlink.App.Views;

namespace IcarusStarlink.App.Services;

/// <summary>
/// The seam between a ViewModel's command logic and the WPF dialogs that logic needs a user
/// decision from. A ViewModel constructing a real Window directly (the app's established pattern
/// before this — `new RenameModDialog(...).ShowDialog()`, `ThemedMessageBox.Show(...)`) can never
/// be unit tested outside a live WPF Dispatcher, since Window construction itself needs one. Going
/// through this interface instead lets a test substitute a fake that returns a canned answer with
/// no WPF involved at all, while WpfDialogService (the real implementation) is what production DI
/// registers.
///
/// This is a PARTIAL migration — it covers the two dialog shapes the "unwired features" pass this
/// session actually touched (a plain confirm, and RenameModDialog's rename/reset/cancel prompt).
/// The app has roughly 30 more direct dialog/window construction call sites across 9 ViewModels
/// (custom pickers, file/folder dialogs, editor sub-windows) that still construct their Window
/// directly and aren't covered here — extending this interface with one method per additional
/// dialog shape, the same way Confirm/PromptRename were added, is the natural way to bring them in
/// as each one's own ViewModel logic needs real test coverage.
/// </summary>
public interface IDialogService
{
    /// <summary>Same call shape as the ThemedMessageBox.Show static it replaces.</summary>
    bool Confirm(string message, string title, ThemedConfirmSeverity severity);

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
}

public sealed record RenamePromptResult(bool Cancelled, string? NewDisplayName);
