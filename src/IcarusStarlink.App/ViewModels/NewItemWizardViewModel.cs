using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One link of the source item: keep pointing at the game's row, or give the new item its own copy.</summary>
public sealed partial class ItemLinkChoiceViewModel(ItemLink link) : ObservableObject
{
    public ItemLink Link { get; } = link;

    public string Field => Link.Field;

    public string Target => $"{GameTableIndex.TableNameOf(Link.CurrentFile)} › {Link.RowName}";

    /// <summary>What this link is for, in words — so the choice doesn't need modding knowledge.</summary>
    public string Hint => Link.Field switch
    {
        "Itemable" => "Name, description, icon, weight and stack size. Almost always your own.",
        "Meshable" => "The 3D model. Keep the game's unless you ship your own model.",
        "Armour" => "Armour stats and the suit/armour model.",
        "Consumable" => "What eating or using it does, and its buff.",
        "Equippable" => "Stat bonuses while equipped (modules, backpacks).",
        "Durable" => "Durability.",
        "Deployable" => "How it's placed in the world.",
        "Audio" => "Sounds.",
        "Decayable" => "How fast it spoils.",
        "Generator" or "Thermal" or "Processing" or "Inventory" or "Fillable" => "Its machine/storage numbers.",
        _ => "Shared behaviour. Usually keep the game's.",
    };

    [ObservableProperty]
    private bool _ownCopy = link.SuggestOwnCopy;
}

/// <summary>
/// "New item from a game item": pick the item to start from, name the new one, choose which of its
/// linked rows become its own (to change them) and which keep pointing at the game's, then how
/// players get it — a recipe at a bench, or an orbital-workshop purchase in an existing tab or a
/// tab of the mod's own. The rows come from ItemCloner; this only gathers the choices.
/// </summary>
public sealed partial class NewItemWizardViewModel : ObservableObject
{
    private readonly GameTableIndex _game;
    private readonly ExmodPackage? _mod;
    private readonly IReadOnlyList<ItemChoice> _allItems;
    private readonly NewItemPreset? _preset;
    private bool _idEdited;

    public NewItemWizardViewModel(GameTableIndex game, ExmodPackage? mod, NewItemPreset? preset = null)
    {
        _game = game;
        _mod = mod;
        _preset = preset;
        _allItems = ItemCloner.ListItems(game);
        Benches = ItemCloner.ListBenches(game);
        WorkshopTrees = ItemCloner.ListWorkshopTrees(game);
        CopyBuff = preset?.CopyBuff ?? true;
        RefreshFilter();

        if (preset?.SourceItem is { } sourceName && _allItems.FirstOrDefault(i => i.Name == sourceName) is { } source)
        {
            SelectedSource = source;
            RefreshFilter(); // the list shows the first 400 items; make sure the preset one is in it
        }
        else
        {
            ApplyObtainDefaults();
        }
    }

    public ObservableCollection<ItemChoice> FilteredItems { get; } = [];

    public ObservableCollection<ItemLinkChoiceViewModel> Links { get; } = [];

    public IReadOnlyList<string> Benches { get; }

    public IReadOnlyList<string> WorkshopTrees { get; }

    [ObservableProperty]
    private string _filterText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSource), nameof(HasBuff), nameof(SourceSummary), nameof(CanUnlockWithSource))]
    private ItemChoice? _selectedSource;

    private ItemCloneSource? _source;

    public bool HasSource => _source is not null;

    public bool HasBuff => _source?.BuffName is not null;

    public bool CanUnlockWithSource => _source?.RecipeHasUnlock == true;

    public string? SourceSummary => _source is null ? null : string.Join(" ", new[]
    {
        _source.RecipeName is null ? null : $"In the game it's crafted at {string.Join(" or ", _source.RecipeBenches)}.",
        _source.WorkshopItemName is null ? null : $"In the game it's a workshop item ({_source.ResearchCredits ?? 0} credits to research, {_source.ReplicationCredits ?? 0} to replicate).",
        _source.RecipeName is null && _source.WorkshopItemName is null ? "In the game it isn't crafted or bought." : null,
    }.Where(s => s is not null));

    /// <summary>The new item's row name (its id in every table). Follows Display name until edited.</summary>
    [ObservableProperty]
    private string _newName = "";

    [ObservableProperty]
    private string _displayName = "";

    [ObservableProperty]
    private string _description = "";

    [ObservableProperty]
    private bool _copyBuff;

    /// <summary>0 = just the item, 1 = recipe, 2 = workshop (ObtainMethod).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJustItem), nameof(IsRecipe), nameof(IsWorkshop))]
    private int _obtainIndex;

    public bool IsJustItem
    {
        get => ObtainIndex == (int)ObtainMethod.None;
        set { if (value) { ObtainIndex = (int)ObtainMethod.None; } }
    }

    public bool IsRecipe
    {
        get => ObtainIndex == (int)ObtainMethod.Recipe;
        set { if (value) { ObtainIndex = (int)ObtainMethod.Recipe; } }
    }

    public bool IsWorkshop
    {
        get => ObtainIndex == (int)ObtainMethod.Workshop;
        set { if (value) { ObtainIndex = (int)ObtainMethod.Workshop; } }
    }

    [ObservableProperty]
    private string _selectedBench = "Crafting_Bench";

    [ObservableProperty]
    private bool _unlockWithSource = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseNewTab))]
    private bool _useExistingTree = true;

    public bool UseNewTab
    {
        get => !UseExistingTree;
        set => UseExistingTree = !value;
    }

    [ObservableProperty]
    private string? _selectedTree;

    [ObservableProperty]
    private string _newTabName = "";

    [ObservableProperty]
    private string _researchCreditsText = "50";

    [ObservableProperty]
    private string _replicationCreditsText = "25";

    [ObservableProperty]
    private string? _error;

    /// <summary>The rows that Create produced; set when it succeeds.</summary>
    public IReadOnlyList<ExmodFileRow>? Result { get; private set; }

    partial void OnFilterTextChanged(string value) => RefreshFilter();

    partial void OnDisplayNameChanged(string value)
    {
        if (!_idEdited)
        {
            _settingIdFromName = true;
            NewName = ToIdentifier(value);
            _settingIdFromName = false;
        }
    }

    private bool _settingIdFromName;

    partial void OnNewNameChanged(string value)
    {
        if (!_settingIdFromName)
        {
            _idEdited = value.Length > 0;
        }
    }

    partial void OnSelectedSourceChanged(ItemChoice? value)
    {
        if (_refreshingFilter)
        {
            return; // the list is being rebuilt under the selection, not a new pick
        }

        _source = value is null ? null : ItemCloner.Inspect(_game, value.Name);
        Links.Clear();
        foreach (var link in _source?.Links ?? [])
        {
            Links.Add(new ItemLinkChoiceViewModel(link));
        }

        if (_source is null)
        {
            return;
        }

        SelectedBench = _source.RecipeBenches.FirstOrDefault() ?? "Crafting_Bench";
        ResearchCreditsText = (_source.ResearchCredits ?? 50).ToString();
        ReplicationCreditsText = (_source.ReplicationCredits ?? 25).ToString();
        ApplyObtainDefaults();
    }

    /// <summary>A template's choice wins; otherwise start from how the game itself hands the source item out.</summary>
    private void ApplyObtainDefaults()
    {
        if (_preset is not null)
        {
            ObtainIndex = (int)_preset.Obtain;
            UseExistingTree = _preset.WorkshopTree is not null;
            SelectedTree = _preset.WorkshopTree ?? WorkshopTrees.FirstOrDefault();
            return;
        }

        if (_source is null)
        {
            return;
        }

        ObtainIndex = (int)(_source.WorkshopItemName is not null ? ObtainMethod.Workshop : _source.RecipeName is not null ? ObtainMethod.Recipe : ObtainMethod.None);
        SelectedTree = _source.WorkshopTree ?? SelectedTree ?? WorkshopTrees.FirstOrDefault();
        UseExistingTree = SelectedTree is not null;
    }

    /// <summary>Builds the rows; false (with Error set) when something's missing or the name is taken.</summary>
    public bool TryCreate()
    {
        Error = null;
        if (_source is null)
        {
            Error = "Pick the game item to start from.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            Error = "Give the item a name.";
            return false;
        }

        var id = ToIdentifier(NewName);
        if (id.Length == 0)
        {
            Error = "The item id needs letters or digits.";
            return false;
        }

        int research = 0, replication = 0;
        if (IsWorkshop && (!int.TryParse(ResearchCreditsText, out research) || research < 0 || !int.TryParse(ReplicationCreditsText, out replication) || replication < 0))
        {
            Error = "Workshop costs need to be whole numbers, 0 or more.";
            return false;
        }

        if (IsWorkshop && UseExistingTree && string.IsNullOrWhiteSpace(SelectedTree))
        {
            Error = "Pick the workshop tab, or choose a new tab of your own.";
            return false;
        }

        try
        {
            Result = ItemCloner.Build(_game, new ItemCloneRequest
            {
                SourceItem = _source.ItemName,
                NewName = id,
                DisplayName = DisplayName.Trim(),
                Description = Description,
                OwnCopyFields = Links.Where(l => l.OwnCopy).Select(l => l.Field).ToHashSet(),
                CopyBuff = CopyBuff,
                Obtain = (ObtainMethod)ObtainIndex,
                RecipeBench = string.IsNullOrWhiteSpace(SelectedBench) ? "Crafting_Bench" : SelectedBench.Trim(),
                UnlockWithSource = UnlockWithSource && CanUnlockWithSource,
                WorkshopTree = UseExistingTree ? SelectedTree : null,
                NewWorkshopTab = UseExistingTree ? null : (string.IsNullOrWhiteSpace(NewTabName) ? DisplayName : NewTabName),
                ResearchCredits = research,
                ReplicationCredits = replication,
            }, _mod);
            return true;
        }
        catch (ArgumentException ex)
        {
            Error = ex.Message;
            return false;
        }
    }

    private bool _refreshingFilter;

    /// <summary>
    /// Rebuilds the list for the search text, keeping the picked item in it and picked: clearing the
    /// list makes the ListBox push a null selection back, which used to forget the pick and every
    /// tick made since.
    /// </summary>
    private void RefreshFilter()
    {
        var picked = SelectedSource;
        _refreshingFilter = true;
        try
        {
            FilteredItems.Clear();
            var filter = FilterText.Trim();
            foreach (var item in _allItems.Where(i => filter.Length == 0 || i.Display.Contains(filter, StringComparison.OrdinalIgnoreCase)).Take(400))
            {
                FilteredItems.Add(item);
            }

            if (picked is not null && !FilteredItems.Contains(picked))
            {
                FilteredItems.Insert(0, picked);
            }

            SelectedSource = picked;
        }
        finally
        {
            _refreshingFilter = false;
        }
    }

    /// <summary>"Dragon Suit" → "Dragon_Suit": game row names are letters, digits and underscores.</summary>
    internal static string ToIdentifier(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text.Trim(), "[^A-Za-z0-9]+", "_").Trim('_');
}
