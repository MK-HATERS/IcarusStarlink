using System.Text.Json.Nodes;

namespace IcarusStarlink.App.ViewModels;

/// <summary>Loading a slot: reading its files off the UI thread and building each section, skipping one that can't be read.</summary>
public sealed partial class SavesViewModel
{
    /// <summary>Test seam only — see _pendingSlotLoad's own doc comment for why this exists.</summary>
    internal Task WaitForPendingSlotLoadAsync() => _pendingSlotLoad;

    /// <summary>Test seam only — see _pendingIconLoad's own doc comment for why this exists.</summary>
    internal Task WaitForPendingIconLoadAsync() => _pendingIconLoad;

    /// <summary>One file's load result: its value, or why it couldn't be read (that section is then skipped, not the slot).</summary>
    private readonly record struct Loaded<T>(T? Value, Exception? Error)
    {
        public T Require() => Error is null ? Value! : throw Error;
    }

    private static Loaded<T> TryLoad<T>(Func<T> load)
    {
        try
        {
            return new Loaded<T>(load(), null);
        }
        catch (Exception ex)
        {
            return new Loaded<T>(default, ex);
        }
    }

    /// <summary>
    /// Builds one section; if its data can't be read (a game update changed a field's shape, a file
    /// is damaged), resets it to empty and names it in SkippedSectionsNote instead of failing the
    /// whole slot. An empty section is never dirty, so its file is left exactly as it is on Save.
    /// </summary>
    private void LoadSection(string name, Action build, Action reset)
    {
        try
        {
            build();
        }
        catch (Exception)
        {
            reset();
            _skippedSections.Add(name);
        }
    }

    private async Task LoadSlotAsync()
    {
        // A section that fails to load must not leave the last slot's mission links behind (review, 2026-10-23).
        _rewardsOfMission.Clear();
        _missionsOfReward.Clear();
        Characters.Clear();
        Currencies.Clear();
        Backups.Clear();
        BinaryFlags.Clear();
        AccountFlags.Clear();
        WorkshopTalents.Clear();
        Accolades.Clear();
        _accoladeLines = [];
        BestiaryEntries.Clear();
        MetaInventoryItems.Clear();
        Mounts.Clear();
        _profile = null;
        _characterNodes = [];
        _binaryFlagIds = null;
        _accoladesRoot = null;
        _bestiaryRoot = null;
        _metaInventoryRoot = null;
        _metaInventoryDirty = false;
        _mountsRoot = null;
        _charactersListDirty = false;
        _mountsListDirty = false;
        _profileFieldsDirty = false;
        _loadedStamps = null;
        _skippedSections.Clear();
        SkippedSectionsNote = null;
        SelectedCharacter = null;

        // Bumped here, not just once icons are actually about to be queued below — a slot switch
        // that fails/returns early (SelectedSlot is null, the read throws) still needs to retire any
        // icon resolution left over from whatever slot was loaded before it.
        _iconLoadGeneration++;

        _lastLoadedSlot = SelectedSlot;
        _loadFailed = false;
        var loadGeneration = ++_slotLoadGeneration;

        // Started before the await below, so the world files (often many MB each) are parsed on a
        // background thread alongside the player files instead of on the UI thread first.
        var worldsLoad = WorldSettings.ReloadAsync();

        if (SelectedSlot is null)
        {
            LastBackupDisplay = null;
            RefreshFlagFilter();
            await worldsLoad;
            return;
        }

        // Captured before the await — if the user switches slots again while this load is still
        // in flight, SelectedSlot will have moved on by the time we resume, and this continuation
        // must recognize it's stale rather than populate the UI with the wrong slot's data (a real
        // race the previous fully-synchronous LoadSlot could never hit).
        var slotBeingLoaded = SelectedSlot;
        var steamId = slotBeingLoaded.SteamId;

        try
        {
            // The real JSON files a slot load reads, off the UI thread. Stamps are taken FIRST: a
            // write that lands between stamping and reading then looks like a change at Save time
            // (a needless refusal) rather than slipping through unnoticed. Each file is read on its
            // own, so one unreadable file only hides its own section. Every ObservableCollection
            // mutation below still runs back on this (UI) thread, since that's not safe off it.
            // The game tables every row is named from are read here too (Warm), once each: they
            // used to be parsed on the UI thread as each section asked, D_Talents five times.
            var files = await Task.Run(() =>
            {
                var read = (
                    Stamps: TryLoad(() => _repository.GetFileStamps(steamId)),
                    Profile: _repository.LoadProfile(steamId),
                    Characters: TryLoad(() => _repository.LoadCharacters(steamId)),
                    Accolades: TryLoad(() => _repository.LoadAccolades(steamId)),
                    Bestiary: TryLoad(() => _repository.LoadBestiary(steamId)),
                    MetaInventory: TryLoad(() => _repository.LoadMetaInventory(steamId)),
                    Mounts: TryLoad(() => _repository.LoadMounts(steamId)));
                _gameNames.Warm();
                return read;
            });

            // A newer load started meanwhile (another slot, or this one again: Reload, Restore, a
            // game-data refresh). Only the newest may fill the lists; two loads of the same slot
            // used to both add their rows, listing every character twice.
            if (!ReferenceEquals(SelectedSlot, slotBeingLoaded) || loadGeneration != _slotLoadGeneration)
            {
                return;
            }

            // Required, not optional: without the load-time stamps Save can't tell whether the game
            // wrote the files since, and would silently write an older copy over newer progress.
            _loadedStamps = files.Stamps.Require();
            _profile = files.Profile;

            LoadSection("characters", () =>
            {
                _characterNodes = [.. files.Characters.Require()];
                foreach (var node in _characterNodes)
                {
                    Characters.Add(new SaveCharacterViewModel(node, _gameNames, NotifyDirtyChanged));
                }
            }, () =>
            {
                Characters.Clear();
                _characterNodes = [];
            });

            LoadSection("currencies", () =>
            {
                if (_profile["MetaResources"] is JsonArray resources)
                {
                    foreach (var resource in resources.OfType<JsonObject>())
                    {
                        var key = SaveJson.String(resource["MetaRow"]) ?? "?";
                        // A key the game doesn't have (Exotic_stabilized, in a real save) is said to be one:
                        // it used to be labelled as if it were a real currency.
                        Currencies.Add(new SaveCurrencyViewModel(resource, CurrencyLabels.GetValueOrDefault(key, $"{key} (not a game currency)"), NotifyDirtyChanged));
                    }
                }
            }, Currencies.Clear);

            LoadSection("account flags", BuildAccountFlags, AccountFlags.Clear);
            LoadSection("unlocks (flags file)", BuildBinaryFlags, () =>
            {
                BinaryFlags.Clear();
                _binaryFlagIds = null;
                OnPropertyChanged(nameof(HasBinaryFlags));
            });
            LoadSection("workshop research", BuildWorkshopTalents, () =>
            {
                WorkshopTalents.Clear();
                RefreshAccountTalentFilters();
            });

            LoadSection("accolades", () =>
            {
                _accoladesRoot = files.Accolades.Require();
                BuildAccolades();
            }, () =>
            {
                _accoladesRoot = null;
                Accolades.Clear();
                BuildAccoladeLines();
                RefreshAccoladeFilter();
            });
            LoadSection("bestiary", () =>
            {
                _bestiaryRoot = files.Bestiary.Require();
                BuildBestiaryEntries();
            }, () =>
            {
                _bestiaryRoot = null;
                BestiaryEntries.Clear();
                RefreshBestiaryFilter();
            });
            LoadSection("items", () =>
            {
                _metaInventoryRoot = files.MetaInventory.Require();
                BuildMetaInventoryItems();
            }, () =>
            {
                _metaInventoryRoot = null;
                MetaInventoryItems.Clear();
                RefreshItemFilter();
            });
            LoadSection("mounts", () =>
            {
                _mountsRoot = files.Mounts.Require();
                BuildMounts();
            }, () =>
            {
                _mountsRoot = null;
                Mounts.Clear();
                RefreshMountFilter();
            });

            QueueIconResolution();

            SelectedCharacter = Characters.FirstOrDefault();
            RefreshBackupsList();
            StatusMessage = null;
            SkippedSectionsNote = _skippedSections.Count == 0
                ? null
                : $"Couldn't read: {string.Join(", ", _skippedSections)}. Those parts are hidden and their files are left exactly as they are when you save.";
        }
        catch (Exception ex)
        {
            // A load that threw partway left a half-populated but fully SAVEABLE state (an audit
            // finding): Save rebuilt Profile.json's Talents/UnlockedFlags from the (empty)
            // collections and wiped the account's research. Nothing from a failed load is writable.
            // Only Profile.json itself (or its file stamps) gets here now; every other section
            // skips itself (LoadSection).
            _loadFailed = true;
            _profile = null;
            _characterNodes = [];
            _loadedStamps = null;
            StatusMessage = $"Couldn't read this save: {ex.Message} Its backups are still listed under Backup & restore.";
            try
            {
                RefreshBackupsList();
            }
            catch (Exception)
            {
                // The backups folder itself unreadable: the status line already says what failed.
            }
        }

        // SelectedCharacter's change handler refreshes these, but a slot with no characters
        // leaves it null (unchanged), so the account and binary flags kept the previous slot's.
        RefreshFlagFilter();
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(ChangesSummary));
        var reread = _gameContentStale;
        _gameContentStale = false;
        _pendingModSources = ClassifyModRowsAsync(reread);
        _pendingPlayerGrowth = ReadPlayerGrowthAsync(reread);
        _pendingMountGrowth = ReadMountGrowthAsync(reread);
        ApplyPackNotes();
        RunSaveCheck();
        await worldsLoad;
    }
}
