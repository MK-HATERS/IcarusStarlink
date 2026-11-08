using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Services;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// Library › Check › Copy my setup: the bug-report kit's mod setup (ModSetupReport, the same as
/// mod-setup.txt in the diagnostics zip) as Markdown on the clipboard, for Discord or a Nexus bug report.
/// </summary>
public sealed partial class LibraryViewModel
{
    /// <summary>The mod setup to copy; set by the app (null: Copy my setup says it isn't available).</summary>
    public ModSetupReport? ModSetupReport { get; init; }

    /// <summary>Puts text on the clipboard (tests swap in a fake).</summary>
    internal Action<string> CopyToClipboard { get; set; } = System.Windows.Clipboard.SetText;

    [RelayCommand]
    private async Task CopyMySetupAsync()
    {
        if (ModSetupReport is not { } report)
        {
            StatusMessage = "Copy my setup isn't available here. Use Settings › Maintenance › Export diagnostics zip instead.";
            return;
        }

        try
        {
            // The Merge page and settings are read here, on the UI thread; the file reads run off it.
            // CaptureMarkdown hides the user folder itself, before the Markdown escaping.
            var build = report.CaptureMarkdown();
            var markdown = await Task.Run(build);
            CopyToClipboard(markdown);
            StatusMessage = "Copied your mod setup. Paste it into Discord or a Nexus bug report.";
        }
        catch (Exception ex)
        {
            // Another program can hold the clipboard open.
            StatusMessage = $"Couldn't copy your mod setup: {ex.Message} Try again in a moment.";
        }
    }
}
