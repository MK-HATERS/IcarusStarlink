using System.Text.Json;

namespace IcarusStarlink.App.Services;

public sealed partial class SaveGameNames
{
    // --- DLC ---

    /// <summary>
    /// D_DLCPackageData's rows in order: flags_&lt;SteamID&gt;.dat holds 0-based indexes into it —
    /// the game's cached list of the DLC this account owns (OfflinePackageFlagsSubsystem in the
    /// game's code; researched 2026-10-23). It was shown as character flags before.
    /// </summary>
    public IReadOnlyList<string> DlcPackageNames => Cached(ref _dlcPackageNames, () => LoadRowNames(DlcTable));

    /// <summary>The D_DLCPackageData row for a flags-file index ("Dangerous_Horizons"); null when the table doesn't have it.</summary>
    public string? DlcPackageRowName(int id) => id >= 0 && id < DlcPackageNames.Count ? DlcPackageNames[id] : null;

    /// <summary>"Dangerous Horizons", "Supporters Edition" for a flags-file index (the row's own DLCName); "DLC 31" when the table doesn't have it.</summary>
    public string DlcPackageName(int id) => id >= 0 && id < DlcPackageNames.Count ? DlcDisplayName(DlcPackageNames[id]) : $"DLC {id}";

    private IReadOnlyDictionary<string, string> DlcDisplayNames => Cached(ref _dlcDisplayNames, LoadDlcDisplayNames);

    /// <summary>A D_DLCPackageData row's own name: "Deluxe_Edition_Envirosuit" -> "Supporters Edition".</summary>
    public string DlcDisplayName(string rowName) => DlcDisplayNames.GetValueOrDefault(rowName) ?? HumanizeMountType(rowName);

    /// <summary>"the Supporters Edition", "the Dangerous Horizons DLC": what a talent or research row is for.</summary>
    public string DlcPackLabel(string rowName)
    {
        var name = DlcDisplayName(rowName);
        return name.Contains("Supporter", StringComparison.OrdinalIgnoreCase) || name.Contains("Edition", StringComparison.OrdinalIgnoreCase)
            ? $"the {name}"
            : $"the {name} DLC";
    }

    private IReadOnlyDictionary<string, string> LoadDlcDisplayNames() => Safe<IReadOnlyDictionary<string, string>>(() =>
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var defaults = DefaultsOf(DlcTable);
        foreach (var (rowName, row) in RowsOf(DlcTable))
        {
            if (ParseLocText(row, "DLCName", defaults)?.Trim() is { Length: > 0 } display)
            {
                names[rowName] = display;
            }
        }

        return names;
    }, () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}
