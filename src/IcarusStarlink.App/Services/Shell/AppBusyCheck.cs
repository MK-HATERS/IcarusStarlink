using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;

namespace IcarusStarlink.App.Services;

/// <summary>
/// Asks every page what closing the app now would lose, for both the in-app update and the window
/// closing (they asked differently before, and neither asked most pages: review 2026-10-23 found an
/// update could close mid-merge or mid-download). A page answers UnsavedEditsRequestMessage with
/// edits not saved yet ("your Saves page edits") and BackgroundWorkRequestMessage with work that
/// must not be cut off ("a merge is being built").
/// </summary>
public static class AppBusyCheck
{
    public static IReadOnlyList<string> UnsavedEdits() =>
        [.. WeakReferenceMessenger.Default.Send(new UnsavedEditsRequestMessage()).Responses.Distinct()];

    public static IReadOnlyList<string> RunningWork() =>
        [.. WeakReferenceMessenger.Default.Send(new BackgroundWorkRequestMessage()).Responses.Distinct()];

    /// <summary>"your Saves page edits and an open mod editor's changes"; null when nothing is unsaved.</summary>
    public static string? DescribeUnsaved() => Join(UnsavedEdits());

    /// <summary>"a merge is being built and a Nexus download"; null when nothing is running.</summary>
    public static string? DescribeRunning() => Join(RunningWork());

    private static string? Join(IReadOnlyList<string> parts) => parts.Count switch
    {
        0 => null,
        1 => parts[0],
        _ => $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}",
    };
}
