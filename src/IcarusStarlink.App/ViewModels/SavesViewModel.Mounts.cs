using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class SavesViewModel
{
    // --- Mounts (account-wide, Mounts.json — a separate file from Profile.json) ---

    public ObservableCollection<SaveMountViewModel> Mounts { get; } = [];

    public ObservableCollection<SaveMountViewModel> FilteredMounts { get; } = [];

    [ObservableProperty]
    private string _mountSearchText = "";

    partial void OnMountSearchTextChanged(string value) => RefreshMountFilter();

    private void RefreshMountFilter() =>
        RefreshFilter(Mounts, FilteredMounts, MountSearchText, null, m => m.Name, m => m.TypeDisplayName);

    /// <summary>Name/Level editable, Type shown only — RecorderBlob (the mount's real stats/stomach/saddle state, a raw Unreal binary blob, not JSON) is preserved untouched on each mount's own live Node. See SaveMountViewModel's own doc comment for why a mount has no natural key to rebuild an array by, unlike Bestiary/Accolades.</summary>
    private void BuildMounts()
    {
        Mounts.Clear();
        if (_mountsRoot?["SavedMounts"] is JsonArray array)
        {
            foreach (var entry in array.OfType<JsonObject>())
            {
                var name = SaveJson.String(entry["MountName"]) ?? "";
                var level = SaveJson.Int(entry["MountLevel"]) ?? 0;
                var typeRowName = SaveJson.String(entry["MountType"]) ?? "";
                var iconPath = _gameNames.MountTypeIcons.GetValueOrDefault(typeRowName);
                var mount = new SaveMountViewModel(entry, name, level, typeRowName, iconPath, NotifyDirtyChanged);
                mount.SetGameData(_gameNames, _mountGrowth.GetValueOrDefault(typeRowName));
                Mounts.Add(mount);
            }
        }

        RefreshMountFilter();
        SelectedMount = Mounts.FirstOrDefault();
    }

    /// <summary>The mount whose inside is shown beside the list.</summary>
    [ObservableProperty]
    private SaveMountViewModel? _selectedMount;

    private Task _pendingMountGrowth = Task.CompletedTask;

    internal Task WaitForMountGrowthAsync() => _pendingMountGrowth;

    /// <summary>Each type's growth (XP and talent points per level), read once per load off the UI thread; mounts not edited yet get it.</summary>
    private async Task ReadMountGrowthAsync()
    {
        if (_readMountGrowth is not { } read)
        {
            return;
        }

        try
        {
            _mountGrowth = await Task.Run(read);
        }
        catch (Exception)
        {
            return;
        }

        foreach (var mount in Mounts)
        {
            var growth = _mountGrowth.GetValueOrDefault(mount.TypeRowName);
            if (mount.IsDirty)
            {
                // Its rows hold edits: only the growth (budget, and the XP for a level typed before it arrived).
                mount.Growth = growth;
            }
            else
            {
                mount.SetGameData(_gameNames, growth);
            }
        }

        RunSaveCheck();
    }

    /// <summary>Same view+delete shape as DeleteMetaInventoryItem, mirrored exactly — no confirmation prompt (a mount is one collectible among many, the same severity as one inventory item, unlike deleting a whole character), just remove-and-mark-dirty; Save's own confirm covers the actual write.</summary>
    [RelayCommand]
    private void DeleteMount(SaveMountViewModel? mount)
    {
        if (mount is null)
        {
            return;
        }

        Mounts.Remove(mount);
        FilteredMounts.Remove(mount);
        _mountsListDirty = true;
        NotifyDirtyChanged();
    }

    /// <summary>
    /// Full rebuild from the CURRENT Mounts collection, not an in-place-only foreach over it — a
    /// mount removed from Mounts (DeleteMount, above) never touched _mountsRoot itself, since each
    /// mount's own Node is still a live child of the OLD SavedMounts array at that point; an
    /// in-place-only pass (this method's own shape before mount deletion existed) would silently
    /// keep a "deleted" mount in the written file. DeepClone is required, not optional, on the way
    /// into the new array: a JsonNode can only ever belong to one parent, and mount.Node is still
    /// parented to the array this replaces. Mirrors ApplyMetaInventoryEdits' own reasoning.
    /// </summary>
    private void ApplyMountEdits()
    {
        if (_mountsRoot is null)
        {
            return;
        }

        var newArray = new JsonArray();
        foreach (var mount in Mounts)
        {
            mount.ApplyToNode();
            newArray.Add(mount.Node.DeepClone());
        }

        _mountsRoot["SavedMounts"] = newArray;
    }
}
