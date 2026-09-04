using System.Windows;

namespace IcarusStarlink.App.Views;

/// <summary>
/// Generic rename prompt, reused across a few different "override this thing's display name"
/// features (Library mods, tracked Nexus watchlist entries) — description and Reset's target
/// value/wording differ per caller since what "reset" means isn't the same in each: Library's own
/// default (resetValue left null) clears the override entirely, going back to the mod's own
/// declared name; a caller with a real fallback value to revert to (e.g. the live Nexus name)
/// passes it as resetValue instead, so NewDisplayName after Reset is that value, not null.
/// NewDisplayName is only ever null when the dialog closes via Reset with no resetValue given —
/// distinct from Cancel (DialogResult stays false, caller does nothing at all). Despite the name,
/// this is really this app's one generic "prompt for a single line of text" dialog — title is
/// parameterized too so a non-rename caller (e.g. declaring a UE4SS mod's minimum loader version)
/// doesn't show a literal "Rename mod" window title.
/// </summary>
public partial class RenameModDialog : Window
{
    public string? NewDisplayName { get; private set; }

    private readonly string? _resetValue;

    public RenameModDialog(
        string currentDisplayName,
        string description = "Changes how this mod displays in your Library only — its real folder, file name, and mod content are never touched.",
        string? resetValue = null,
        string resetLabel = "Reset to default",
        string resetTooltip = "Clears the override — goes back to the mod's own declared name",
        string title = "Rename mod",
        string fieldLabel = "Display name")
    {
        InitializeComponent();
        Title = title;
        DescriptionText.Text = description;
        FieldLabelText.Text = fieldLabel;
        NameBox.Text = currentDisplayName;
        NameBox.SelectAll();
        Loaded += (_, _) => NameBox.Focus();

        _resetValue = resetValue;
        ResetButton.Content = resetLabel;
        ResetButton.ToolTip = resetTooltip;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        NewDisplayName = NameBox.Text.Trim();
        DialogResult = true;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        NewDisplayName = _resetValue;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
