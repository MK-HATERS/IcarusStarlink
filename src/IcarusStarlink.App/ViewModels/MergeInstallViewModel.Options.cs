using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.App.Converters;
using IcarusStarlink.Core.Profiles;

namespace IcarusStarlink.App.ViewModels;

/// <summary>Merge options: the gameplay-option pills and sliders, the summary shown when the panel is folded, and moving options between the page and a profile (custom values the controls can't show are kept).</summary>
public sealed partial class MergeInstallViewModel
{
    // --- Gameplay-toggle merge options (6.4) ---
    public static IReadOnlyList<BoostLevel> BoostLevels { get; } = Enum.GetValues<BoostLevel>();

    public static IReadOnlyList<XpBoostLevel> XpBoostLevels { get; } = Enum.GetValues<XpBoostLevel>();

    public static IReadOnlyList<CraftCostReduction> CraftCostReductions { get; } = Enum.GetValues<CraftCostReduction>();

    [ObservableProperty]
    private BoostLevel _speedBoost;

    [ObservableProperty]
    private BoostLevel _playerBoost;

    [ObservableProperty]
    private XpBoostLevel _xpBoost;

    [ObservableProperty]
    private CraftCostReduction _craftCost;

    /// <summary>Fixed multiplier pills, not free text — classic IMM never documented an exact number for its own Stacks/Slots toggles, but discrete common values (matching the other pill selectors' own UX) beat a bare text box a user has to guess at. A real (non-nullable) enum, not a `double?` with null="Off": WPF's Selector conflates SelectedItem==null with "nothing selected" rather than "the null item is selected", so a nullable-double pill list never shows Off as highlighted on load — found live, the exact bug this enum avoids.</summary>
    public static IReadOnlyList<MultiplierLevel> MultiplierOptions { get; } = Enum.GetValues<MultiplierLevel>();

    [ObservableProperty]
    private MultiplierLevel _stacksMultiplierLevel;

    [ObservableProperty]
    private MultiplierLevel _slotsMultiplierLevel;

    /// <summary>0 = off/unchanged — the Slider's own natural "nothing set" value, so no separate null-state to track the way the multiplier pills need one.</summary>
    [ObservableProperty]
    private int _speedCraftingPercent;

    /// <summary>Same "0 = unchanged" Slider convention as SpeedCraftingPercent.</summary>
    [ObservableProperty]
    private int _tamingSpeedPercent;

    [ObservableProperty]
    private int _weightReductionPercent;

    [ObservableProperty]
    private bool _unlimitedAmmo;

    [ObservableProperty]
    private bool _disableTemperatures;

    [ObservableProperty]
    private bool _removeLevelCap;

    private static readonly GameplayLevelToShortLabelConverter LevelLabelConverter = new();

    /// <summary>
    /// A compact one-line summary of which gameplay options are currently on — shown in the
    /// collapsed "Merge options" Expander header so folding that panel away doesn't hide active
    /// state. GameplayOptions.DescribeActiveOptions over what a build would use (so a kept custom
    /// value shows as it is), with the pills' short labels. RebuildAsync's "is anything active"
    /// guard reads GameplayOptions.IsAnyActive, never this text.
    /// </summary>
    public string ActiveOptionsSummary
    {
        get
        {
            var parts = BuildGameplayOptionsFromUi().DescribeActiveOptions(ShortLabel);
            return parts.Count == 0 ? "No options active" : string.Join(" · ", parts);
        }
    }

    private static string ShortLabel(object level) =>
        (string)LevelLabelConverter.Convert(level, typeof(string), null, CultureInfo.InvariantCulture);

    // Speed/Player/XP Boost and Disable Temperatures (Category 1 — the ones that became a real
    // FieldChange in Phase 1 of the EXMOD/merge-options plan) also invalidate any existing manual
    // conflict picks and recompute the badge — toggling one of these changes whether the "Built-in
    // gameplay options" synthetic entry even exists, which can change what a stored pick's index
    // means the same way an Add/Remove/Move/Clear on Queue already does. Category 2 options
    // (Craft Cost, Stacks/Slots multiplier, ...) stay their own separate post-merge pass (unchanged
    // by that plan) and only need the summary refresh, not a conflict recompute.
    partial void OnSpeedBoostChanged(BoostLevel value) { OnPropertyChanged(nameof(ActiveOptionsSummary)); InvalidateManualPicks(); }
    partial void OnPlayerBoostChanged(BoostLevel value) { OnPropertyChanged(nameof(ActiveOptionsSummary)); InvalidateManualPicks(); }
    partial void OnXpBoostChanged(XpBoostLevel value) { OnPropertyChanged(nameof(ActiveOptionsSummary)); InvalidateManualPicks(); }
    partial void OnDisableTemperaturesChanged(bool value) { OnPropertyChanged(nameof(ActiveOptionsSummary)); InvalidateManualPicks(); }
    partial void OnRemoveLevelCapChanged(bool value) { OnPropertyChanged(nameof(ActiveOptionsSummary)); InvalidateManualPicks(); }
    partial void OnCraftCostChanged(CraftCostReduction value) => OnPropertyChanged(nameof(ActiveOptionsSummary));
    partial void OnStacksMultiplierLevelChanged(MultiplierLevel value) { KeepUnlessApplying(ref _keptStacksMultiplier); OnPropertyChanged(nameof(ActiveOptionsSummary)); }
    partial void OnSlotsMultiplierLevelChanged(MultiplierLevel value) { KeepUnlessApplying(ref _keptSlotsMultiplier); OnPropertyChanged(nameof(ActiveOptionsSummary)); }
    partial void OnSpeedCraftingPercentChanged(int value) { KeepUnlessApplying(ref _keptSpeedCraftingPercent); OnPropertyChanged(nameof(ActiveOptionsSummary)); }
    partial void OnTamingSpeedPercentChanged(int value) { KeepUnlessApplying(ref _keptTamingSpeedPercent); OnPropertyChanged(nameof(ActiveOptionsSummary)); }
    partial void OnWeightReductionPercentChanged(int value) { KeepUnlessApplying(ref _keptWeightReductionPercent); OnPropertyChanged(nameof(ActiveOptionsSummary)); }

    // A profile's value the pills/sliders can't show (Stacks x2.5 from a hand-edited profile or a
    // patch, Speed Crafting 33.3%), kept as it is until the user changes that option: the UI shows
    // the nearest it can (Off, 33%), and the auto-save used to write that over the real value the
    // moment the profile loaded (review 2026-10-23). Null: the UI's own value is the real one.
    private double? _keptStacksMultiplier;
    private double? _keptSlotsMultiplier;
    private double? _keptSpeedCraftingPercent;
    private double? _keptTamingSpeedPercent;
    private double? _keptWeightReductionPercent;

    /// <summary>True while LoadGameplayOptionsIntoUi sets the controls (a change then isn't the user's).</summary>
    private bool _applyingOptions;

    /// <summary>The user changed this option: whatever it now shows is the value to save.</summary>
    private void KeepUnlessApplying(ref double? kept)
    {
        if (!_applyingOptions)
        {
            kept = null;
        }
    }
    partial void OnUnlimitedAmmoChanged(bool value) => OnPropertyChanged(nameof(ActiveOptionsSummary));

    /// <summary>The options as the page has them: what a build uses and what the profile saves (a kept custom value as it is).</summary>
    private GameplayOptions BuildGameplayOptionsFromUi() => new()
    {
        SpeedBoost = SpeedBoost,
        PlayerBoost = PlayerBoost,
        XpBoost = XpBoost,
        CraftCost = CraftCost,
        StacksMultiplier = _keptStacksMultiplier ?? StacksMultiplierLevel.ToMultiplier(),
        SlotsMultiplier = _keptSlotsMultiplier ?? SlotsMultiplierLevel.ToMultiplier(),
        SpeedCraftingReductionPercent = _keptSpeedCraftingPercent ?? (SpeedCraftingPercent > 0 ? SpeedCraftingPercent : null),
        TamingSpeedReductionPercent = _keptTamingSpeedPercent ?? (TamingSpeedPercent > 0 ? TamingSpeedPercent : null),
        RemoveWeight = _keptWeightReductionPercent is null && WeightReductionPercent >= 100,
        WeightReductionPercent = _keptWeightReductionPercent ?? (WeightReductionPercent is > 0 and < 100 ? WeightReductionPercent : null),
        UnlimitedAmmo = UnlimitedAmmo,
        DisableTemperatures = DisableTemperatures,
        RemoveLevelCap = RemoveLevelCap,
    };

    private void LoadGameplayOptionsIntoUi(GameplayOptions options)
    {
        _applyingOptions = true;
        try
        {
            SpeedBoost = options.SpeedBoost;
            PlayerBoost = options.PlayerBoost;
            XpBoost = options.XpBoost;
            CraftCost = options.CraftCost;
            StacksMultiplierLevel = MultiplierLevelExtensions.FromMultiplier(options.StacksMultiplier);
            SlotsMultiplierLevel = MultiplierLevelExtensions.FromMultiplier(options.SlotsMultiplier);
            SpeedCraftingPercent = options.SpeedCraftingReductionPercent is { } percent ? (int)Math.Round(percent) : 0;
            TamingSpeedPercent = options.TamingSpeedReductionPercent is { } tamingPercent ? (int)Math.Round(tamingPercent) : 0;
            // Older profiles saved Remove Weight as on/off — that's 100% on the slider.
            WeightReductionPercent = options.RemoveWeight ? 100 : options.WeightReductionPercent is { } lighter ? (int)Math.Round(Math.Clamp(lighter, 0, 100)) : 0;
            UnlimitedAmmo = options.UnlimitedAmmo;
            DisableTemperatures = options.DisableTemperatures;
            RemoveLevelCap = options.RemoveLevelCap;

            // What the controls couldn't show exactly is kept as it is (see _keptStacksMultiplier).
            _keptStacksMultiplier = KeptIfNot(options.StacksMultiplier, v => StacksMultiplierLevel.ToMultiplier() == v);
            _keptSlotsMultiplier = KeptIfNot(options.SlotsMultiplier, v => SlotsMultiplierLevel.ToMultiplier() == v);
            _keptSpeedCraftingPercent = KeptIfNot(options.SpeedCraftingReductionPercent, v => SpeedCraftingPercent == v);
            _keptTamingSpeedPercent = KeptIfNot(options.TamingSpeedReductionPercent, v => TamingSpeedPercent == v);
            _keptWeightReductionPercent = options.RemoveWeight ? null : KeptIfNot(options.WeightReductionPercent, v => WeightReductionPercent == v);
        }
        finally
        {
            _applyingOptions = false;
        }

        OnPropertyChanged(nameof(ActiveOptionsSummary));
    }

    /// <summary>A stored option value (on: above 0) the control doesn't show exactly; null when it does, or when it's off.</summary>
    private static double? KeptIfNot(double? stored, Func<double, bool> shownExactly) =>
        stored is > 0 and var value && !shownExactly(value) ? value : null;
}
