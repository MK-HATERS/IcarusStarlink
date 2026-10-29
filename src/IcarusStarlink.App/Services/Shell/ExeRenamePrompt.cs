using System.IO;
using System.Windows;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Settings;

namespace IcarusStarlink.App.Services;

/// <summary>
/// At start: until AppSettings.ExeRenameDone is set, looks for anything still using the old exe
/// name (ExeRenameMigration) and offers the move-over window. A fresh install whose only leftover is
/// the launcher from the zip is tidied without asking; an existing user is always told, since their
/// way of starting Starlink changes.
/// </summary>
public static class ExeRenamePrompt
{
    public static async Task RunAtStartupAsync(Window owner, ISettingsService settings, INxmProtocolRegistrar nxm, IActivityLog activityLog, bool existingInstall)
    {
        // Only the renamed app does this: the old exe is the one being removed.
        if (settings.Current.ExeRenameDone
            || !string.Equals(Path.GetFileName(Environment.ProcessPath), StarlinkExe.FileName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var migration = new ExeRenameMigration(
            AppContext.BaseDirectory, new WindowsShortcutEditor(), nxm, ExeRenameMigration.WindowsShortcutFolders());
        void MarkDone()
        {
            settings.Current.ExeRenameDone = true;
            settings.Save();
        }

        ExeRenamePlan plan;
        try
        {
            plan = await Task.Run(migration.Plan);
        }
        catch (Exception ex)
        {
            activityLog.Log($"Couldn't check for the old exe name: {ex.Message}", ActivityEntryKind.Warning);
            return;
        }

        if (!plan.HasWork)
        {
            MarkDone();
            return;
        }

        if (!existingInstall && plan.Shortcuts.Count == 0 && !plan.NxmUsesOldExe)
        {
            var steps = await Task.Run(() => migration.Run(plan));
            if (steps.All(step => step.Succeeded))
            {
                MarkDone();
                return;
            }
        }

        // After any first-launch question already open (those don't queue), and one at a time with "What's new".
        while (Application.Current?.Windows.OfType<Window>().Any(window => window != owner && window.IsVisible) == true)
        {
            await Task.Delay(500);
        }

        await StartupPrompts.ShowAsync(() => new ExeRenameWindow(owner, migration, plan, () =>
        {
            MarkDone();
            activityLog.Log($"Moved over to {StarlinkExe.FileName}: shortcuts and Nexus links updated, {StarlinkExe.OldFileName} removed.");
        }));
    }
}
