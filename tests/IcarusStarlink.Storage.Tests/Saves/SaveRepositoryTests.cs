using System.IO.Compression;
using System.Text.Json.Nodes;
using IcarusStarlink.Core.Steam;
using IcarusStarlink.Storage.Saves;

namespace IcarusStarlink.Storage.Tests.Saves;

/// <summary>
/// The save editor's data layer — the tests encode its two load-bearing contracts: every write is
/// preceded by a full slot backup (there is no path that changes player data without capturing
/// what was there), and fields the editor doesn't understand survive a load-edit-save round trip.
/// Fixtures use the real files' own shapes, including Characters.json's JSON-in-JSON quirk.
/// </summary>
public sealed class SaveRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlinkTests", $"Saves_{Guid.NewGuid():N}");
    private readonly string _playerData;
    private readonly string _backups;
    private readonly SaveRepository _repository;

    private const string SteamId = "76561198000000001";

    public SaveRepositoryTests()
    {
        _playerData = Path.Combine(_root, "PlayerData");
        _backups = Path.Combine(_root, "save_backups");
        Directory.CreateDirectory(Path.Combine(_playerData, SteamId));
        _repository = new SaveRepository(_playerData, _backups, new FakeLocator());

        // Shaped like the real files: Profile.json plain, Characters.json wrapping each character
        // as an escaped JSON string. Both carry fields the editor knows nothing about.
        File.WriteAllText(Path.Combine(_playerData, SteamId, "Profile.json"), """
            {
                "UserID": "76561198000000001",
                "MetaResources": [ { "MetaRow": "Credits", "Count": 100 }, { "MetaRow": "Biomass", "Count": 50 } ],
                "UnlockedFlags": [ 86, 5, 26 ],
                "Talents": [ { "RowName": "Workshop_Envirosuit", "Rank": 1 } ],
                "NextChrSlot": 1,
                "DataVersion": 4
            }
            """);

        var character = new JsonObject
        {
            ["CharacterName"] = "TESTER",
            ["ChrSlot"] = 0,
            ["XP"] = 1234,
            ["IsDead"] = false,
            ["Location"] = "Prospect_Grasslands",
            ["Cosmetic"] = new JsonObject { ["Customization_Head"] = -506683013, ["IsMale"] = true },
            ["Talents"] = new JsonArray(new JsonObject { ["RowName"] = "Solo_Stamina", ["Rank"] = 2 }),
            ["TimeLastPlayed"] = 1787399187,
        };
        var wrapper = new JsonObject { ["Characters.json"] = new JsonArray(JsonValue.Create(character.ToJsonString())) };
        File.WriteAllText(Path.Combine(_playerData, SteamId, "Characters.json"), wrapper.ToJsonString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void ListSlots_FindsRealSlotsAndSkipsStrayFolders()
    {
        Directory.CreateDirectory(Path.Combine(_playerData, "NotASlot"));
        Directory.CreateDirectory(Path.Combine(_playerData, "99999999999999999")); // digits but no Profile.json

        var slots = _repository.ListSlots();

        var slot = Assert.Single(slots);
        Assert.Equal(SteamId, slot.SteamId);
        Assert.Equal("TestPersona", slot.PersonaName);
    }

    [Fact]
    public void LoadCharacters_UnwrapsTheJsonInJsonQuirk()
    {
        var characters = _repository.LoadCharacters(SteamId);

        var character = Assert.Single(characters);
        Assert.Equal("TESTER", character["CharacterName"]!.GetValue<string>());
        Assert.Equal(1234, character["XP"]!.GetValue<int>());
    }

    [Fact]
    public void SaveCharacters_RoundTrip_PreservesEveryFieldTheEditorDoesntKnow()
    {
        var characters = _repository.LoadCharacters(SteamId).ToList();
        characters[0]["XP"] = 999999;
        characters[0]["CharacterName"] = "RENAMED";

        _repository.SaveCharacters(SteamId, characters);

        var reloaded = Assert.Single(_repository.LoadCharacters(SteamId));
        Assert.Equal(999999, reloaded["XP"]!.GetValue<int>());
        Assert.Equal("RENAMED", reloaded["CharacterName"]!.GetValue<string>());
        // The preservation contract — cosmetics hashes, talents, timestamps all survive.
        Assert.Equal(-506683013, reloaded["Cosmetic"]!["Customization_Head"]!.GetValue<long>());
        Assert.True(reloaded["Cosmetic"]!["IsMale"]!.GetValue<bool>());
        Assert.Equal("Solo_Stamina", reloaded["Talents"]![0]!["RowName"]!.GetValue<string>());
        Assert.Equal(1787399187, reloaded["TimeLastPlayed"]!.GetValue<long>());
    }

    /// <summary>An entry that isn't a character was skipped on read, so the next Save of the characters wrote the file without it.</summary>
    [Fact]
    public void LoadCharacters_AnEntryThatIsntACharacter_FailsTheReadInsteadOfDroppingIt()
    {
        var path = Path.Combine(_playerData, SteamId, "Characters.json");
        var wrapper = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        wrapper["Characters.json"]!.AsArray().Add(JsonValue.Create("null"));
        File.WriteAllText(path, wrapper.ToJsonString());

        Assert.Throws<FormatException>(() => _repository.LoadCharacters(SteamId));
    }

    [Fact]
    public void SaveCharacters_KeepsTheJsonInJsonShapeTheGameExpects()
    {
        _repository.SaveCharacters(SteamId, _repository.LoadCharacters(SteamId));

        var raw = JsonNode.Parse(File.ReadAllText(Path.Combine(_playerData, SteamId, "Characters.json")))!.AsObject();
        var element = raw["Characters.json"]!.AsArray()[0]!;
        // Still an escaped STRING element, not an inlined object — the game's own format.
        Assert.Equal(System.Text.Json.JsonValueKind.String, element.GetValueKind());
        Assert.Contains("\"CharacterName\"", element.GetValue<string>());
    }

    [Fact]
    public void SaveProfile_BacksUpTheWholeSlotBeforeWriting()
    {
        var profile = _repository.LoadProfile(SteamId);
        profile["MetaResources"]![0]!["Count"] = 777;

        var backupPath = _repository.SaveProfile(SteamId, profile);

        // The backup holds the PRE-edit value — proof it was taken before the write.
        using var zip = ZipFile.OpenRead(backupPath!);
        using var reader = new StreamReader(zip.GetEntry("Profile.json")!.Open());
        Assert.Contains("\"Count\": 100", reader.ReadToEnd());

        var reloaded = _repository.LoadProfile(SteamId);
        Assert.Equal(777, reloaded["MetaResources"]![0]!["Count"]!.GetValue<int>());
        // Untouched profile fields survive too.
        Assert.Equal(4, reloaded["DataVersion"]!.GetValue<int>());
        Assert.Equal(3, reloaded["UnlockedFlags"]!.AsArray().Count);
    }

    [Fact]
    public void SaveProfile_TakeBackupFalse_WritesButDoesNotBackUpAndReturnsNull()
    {
        var beforeCount = Directory.Exists(_backups) ? Directory.GetFiles(_backups, "*.zip").Length : 0;

        var profile = _repository.LoadProfile(SteamId);
        profile["MetaResources"]![0]!["Count"] = 777;
        var backupPath = _repository.SaveProfile(SteamId, profile, takeBackup: false);

        Assert.Null(backupPath);
        var afterCount = Directory.Exists(_backups) ? Directory.GetFiles(_backups, "*.zip").Length : 0;
        Assert.Equal(beforeCount, afterCount);

        // The write itself still happens — only the backup is skipped.
        var reloaded = _repository.LoadProfile(SteamId);
        Assert.Equal(777, reloaded["MetaResources"]![0]!["Count"]!.GetValue<int>());
    }

    /// <summary>A save file the running game holds locked fails the backup — which used to leave a half-written .zip.tmp in Backups every time.</summary>
    [Fact]
    public void SaveProfile_BackupFailsOnALockedFile_ThrowsWithoutWritingAndLeavesNoTempZip()
    {
        var profile = _repository.LoadProfile(SteamId);
        profile["NextChrSlot"] = 9;

        using (new FileStream(Path.Combine(_playerData, SteamId, "Characters.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => _repository.SaveProfile(SteamId, profile));
        }

        Assert.Empty(Directory.GetFiles(_backups));
        Assert.Equal(1, _repository.LoadProfile(SteamId)["NextChrSlot"]!.GetValue<int>());
    }

    [Fact]
    public void RestoreSlot_WritesPreRestoreZipThenReplacesTheSlot()
    {
        // A world save in the slot's Prospects subfolder — the editor never touches it, so neither
        // backup nor restore may include it (an audit finding: the old whole-folder swap rolled
        // every world back to the backup's moment).
        var worldPath = Path.Combine(_playerData, SteamId, "Prospects", "Everbark Outpost.json");
        Directory.CreateDirectory(Path.GetDirectoryName(worldPath)!);
        File.WriteAllText(worldPath, "world at backup time");

        var backupPath = _repository.BackupSlot(SteamId);
        File.WriteAllText(worldPath, "twenty hours of play later");

        // Change the live slot after the backup, plus add a file the backup doesn't have.
        var profile = _repository.LoadProfile(SteamId);
        profile["MetaResources"]![0]!["Count"] = 55555;
        File.WriteAllText(Path.Combine(_playerData, SteamId, "Profile.json"), profile.ToJsonString());
        File.WriteAllText(Path.Combine(_playerData, SteamId, "NewerFile.json"), "{}");

        var preRestorePath = _repository.RestoreSlot(SteamId, backupPath);

        // The slot is exactly the backup again: old value back, newer file gone (replace, not merge).
        Assert.Equal(100, _repository.LoadProfile(SteamId)["MetaResources"]![0]!["Count"]!.GetValue<int>());
        Assert.False(File.Exists(Path.Combine(_playerData, SteamId, "NewerFile.json")));

        // The world save is exactly as it was before the restore, and in neither zip.
        Assert.Equal("twenty hours of play later", File.ReadAllText(worldPath));
        using (var backupZip = ZipFile.OpenRead(backupPath))
        {
            Assert.DoesNotContain(backupZip.Entries, e => e.FullName.Contains("Prospects"));
        }

        // And the discarded state is itself recoverable from the pre_restore zip.
        Assert.Contains("pre_restore", Path.GetFileName(preRestorePath));
        using var zip = ZipFile.OpenRead(preRestorePath);
        using var reader = new StreamReader(zip.GetEntry("Profile.json")!.Open());
        Assert.Contains("55555", reader.ReadToEnd());
    }

    [Fact]
    public void RestoreSlot_CorruptBackupZip_LeavesLiveSlotUntouchedAndThrows()
    {
        // The whole point of extracting to a scratch folder BEFORE touching the live slot: a
        // corrupt/truncated backup (a slow-drive copy, a file mid-write when copied) must fail
        // before the real save folder is ever deleted, not after — otherwise "restore the wrong
        // backup" would leave the user with neither the old state nor a working new one.
        Directory.CreateDirectory(_backups);
        var corruptBackupPath = Path.Combine(_backups, "corrupt.zip");
        File.WriteAllBytes(corruptBackupPath, [0x50, 0x4B, 0x03, 0x04, 0x00, 0x00]);

        Assert.ThrowsAny<Exception>(() => _repository.RestoreSlot(SteamId, corruptBackupPath));

        // The live slot's real content survived completely, byte-for-byte the same as at
        // construction — never deleted, never even touched.
        Assert.Equal(100, _repository.LoadProfile(SteamId)["MetaResources"]![0]!["Count"]!.GetValue<int>());
        Assert.True(File.Exists(Path.Combine(_playerData, SteamId, "Characters.json")));

        // No leftover scratch folder either — cleaned up on failure, not left behind as clutter.
        Assert.DoesNotContain(
            Directory.GetDirectories(_playerData),
            dir => Path.GetFileName(dir).Contains("_restore_scratch_"));
    }

    /// <summary>The backup picker named each backup by its zip (a Steam ID and a timestamp): it now says what each one is.</summary>
    [Fact]
    public void ListBackups_EachSaysWhatItIs_NotItsZipName()
    {
        var beforeSave = _repository.BackupSlot(SteamId);
        _repository.BackupSlotManually(SteamId);
        _repository.RestoreSlot(SteamId, beforeSave);

        var kinds = _repository.ListBackups(SteamId).Select(b => b.Kind).Order().ToList();

        Assert.Equal(["before a restore", "before a save", "made with Backup now"], kinds);
        Assert.All(_repository.ListBackups(SteamId), b => Assert.DoesNotContain(SteamId, b.Display));
    }

    /// <summary>
    /// A zip with no Profile.json at its top (the slot folder zipped by hand, with the folder inside)
    /// replaced every player file with nothing; the slot then wasn't listed, so its Restore was gone too.
    /// </summary>
    [Fact]
    public void RestoreSlot_BackupWithoutProfileAtItsTop_RefusesAndLeavesTheSlotAlone()
    {
        Directory.CreateDirectory(_backups);
        var nested = Path.Combine(_backups, $"{SteamId}_by_hand.zip");
        using (var zip = ZipFile.Open(nested, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(Path.Combine(_playerData, SteamId, "Profile.json"), $"{SteamId}/Profile.json");
        }

        var ex = Assert.Throws<InvalidDataException>(() => _repository.RestoreSlot(SteamId, nested));

        Assert.Contains("Nothing was changed", ex.Message);
        Assert.Single(_repository.ListSlots());
        Assert.True(File.Exists(Path.Combine(_playerData, SteamId, "Characters.json")));
        Assert.DoesNotContain(Directory.GetFiles(_backups), f => f.Contains("pre_restore"));
    }

    /// <summary>A failure partway through the swap (here: the second rename into place) puts every live file back as it was.</summary>
    [Fact]
    public void ReplaceTopLevelFiles_FailureMidSwap_PutsTheLiveFilesBack()
    {
        var slot = Path.Combine(_root, "slot");
        var incoming = Path.Combine(_root, "incoming");
        var aside = Path.Combine(_root, "aside");
        Directory.CreateDirectory(slot);
        Directory.CreateDirectory(incoming);
        File.WriteAllText(Path.Combine(slot, "Profile.json"), "live profile");
        File.WriteAllText(Path.Combine(slot, "Characters.json"), "live characters");
        File.WriteAllText(Path.Combine(slot, "OnlyLive.json"), "live only");
        File.WriteAllText(Path.Combine(incoming, "Profile.json"), "old profile");
        File.WriteAllText(Path.Combine(incoming, "Characters.json"), "old characters");

        var swaps = 0;
        Assert.Throws<IOException>(() => SaveRepository.ReplaceTopLevelFiles(slot, incoming, aside, _ =>
        {
            if (++swaps == 2)
            {
                throw new IOException("locked");
            }
        }));

        Assert.Equal("live profile", File.ReadAllText(Path.Combine(slot, "Profile.json")));
        Assert.Equal("live characters", File.ReadAllText(Path.Combine(slot, "Characters.json")));
        Assert.Equal("live only", File.ReadAllText(Path.Combine(slot, "OnlyLive.json")));
        Assert.Equal(3, Directory.GetFiles(slot).Length);
        Assert.False(Directory.Exists(aside));
    }

    [Fact]
    public void ReplaceTopLevelFiles_Success_ReplacesFilesAndCleansUp()
    {
        var slot = Path.Combine(_root, "slot");
        var incoming = Path.Combine(_root, "incoming");
        var aside = Path.Combine(_root, "aside");
        Directory.CreateDirectory(slot);
        Directory.CreateDirectory(incoming);
        File.WriteAllText(Path.Combine(slot, "Profile.json"), "live profile");
        File.WriteAllText(Path.Combine(slot, "OnlyLive.json"), "live only");
        File.WriteAllText(Path.Combine(incoming, "Profile.json"), "old profile");

        SaveRepository.ReplaceTopLevelFiles(slot, incoming, aside);

        Assert.Equal("old profile", File.ReadAllText(Path.Combine(slot, "Profile.json")));
        Assert.False(File.Exists(Path.Combine(slot, "OnlyLive.json")));
        Assert.Single(Directory.GetFiles(slot));
        Assert.False(Directory.Exists(aside));
    }

    /// <summary>AssociatedProspects_* is each character's list of worlds; worlds are never restored, so neither is the list.</summary>
    [Fact]
    public void RestoreSlot_LeavesAssociatedProspectsAlone()
    {
        var listPath = Path.Combine(_playerData, SteamId, "AssociatedProspects_Slot_0.json");
        File.WriteAllText(listPath, "worlds at backup time");
        var backupPath = _repository.BackupSlot(SteamId);
        File.WriteAllText(listPath, "worlds now");

        _repository.RestoreSlot(SteamId, backupPath);

        Assert.Equal("worlds now", File.ReadAllText(listPath));
        Assert.DoesNotContain(Directory.GetDirectories(_playerData), d => Path.GetFileName(d).Contains("_restore_"));
    }

    [Fact]
    public void ListBackups_NewestFirst_OnlyThisSlots()
    {
        _repository.BackupSlot(SteamId);
        Directory.CreateDirectory(Path.Combine(_playerData, "76561198000000002"));
        File.WriteAllText(Path.Combine(_playerData, "76561198000000002", "Profile.json"), "{}");
        _repository.BackupSlot("76561198000000002");

        var backups = _repository.ListBackups(SteamId);

        var backup = Assert.Single(backups);
        Assert.StartsWith(SteamId, Path.GetFileName(backup.FilePath));
    }

    [Fact]
    public void Backups_CapAtTen_ButTheOriginalAlwaysSurvives()
    {
        // 12 backups with distinct, monotonically increasing timestamps — each set immediately
        // after its own creation, so every prune pass (which runs inside BackupSlot) sees the
        // ordering already in place. Within one test instant, real timestamps would all collide.
        var paths = new List<string>();
        for (var i = 0; i < 12; i++)
        {
            var path = _repository.BackupSlot(SteamId);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(i - 20));
            paths.Add(path);
        }

        // Asserted by the contract, not by path identity: a pruned file's NAME can be recycled by
        // the next same-second backup's collision-suffix loop (found by this very test), so "is
        // paths[N] still there" doesn't mean what it looks like. What must hold: exactly the cap
        // remains, the chronologically-oldest survivor is the original first backup, and the
        // newest backup survived too.
        var remaining = Directory.GetFiles(_backups, $"{SteamId}_*.zip");
        Assert.Equal(10, remaining.Length);
        Assert.Contains(paths[0], remaining);
        Assert.Equal(paths[0], remaining.OrderBy(File.GetLastWriteTimeUtc).First());
        Assert.Contains(paths[^1], remaining);
    }

    /// <summary>Audit finding: a deliberate "Backup now" was pruned exactly like an automatic Save/Restore backup, so it silently vanished after nine more clicks.</summary>
    [Fact]
    public void Backups_ManualBackupNow_IsNeverPrunedByTheAutomaticCap()
    {
        var manual = _repository.BackupSlotManually(SteamId);
        File.SetLastWriteTimeUtc(manual, DateTime.UtcNow.AddMinutes(-30));
        Assert.Contains("_manual_", Path.GetFileName(manual));

        for (var i = 0; i < 12; i++)
        {
            var path = _repository.BackupSlot(SteamId);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(i - 20));
        }

        Assert.True(File.Exists(manual));
        Assert.Equal(10, Directory.GetFiles(_backups, $"{SteamId}_*.zip").Count(f => !f.Contains("_manual_")));
        Assert.Contains(_repository.ListBackups(SteamId), b => b.FilePath == manual);
    }

    [Fact]
    public void LoadAccolades_NoFile_ReturnsEmptyShapedDefault()
    {
        var accolades = _repository.LoadAccolades(SteamId);

        Assert.Empty(accolades["CompletedAccolades"]!.AsArray());
    }

    [Fact]
    public void SaveAccolades_RoundTrip_BacksUpFirst()
    {
        File.WriteAllText(Path.Combine(_playerData, SteamId, "Accolades.json"), """
            {
                "CompletedAccolades": [ { "Accolade": { "RowName": "EquipFullArmour", "DataTableName": "D_Accolades" }, "TimeCompleted": "2026.01.01-00.00.00", "ProspectID": "" } ],
                "PlayerTrackers": { "(RowName=\"TimeSurvived\",DataTableName=\"D_PlayerTrackers\")": 500 }
            }
            """);

        var accolades = _repository.LoadAccolades(SteamId);
        accolades["CompletedAccolades"]!.AsArray().Add(new JsonObject
        {
            ["Accolade"] = new JsonObject { ["RowName"] = "WolvesKilled10", ["DataTableName"] = "D_Accolades" },
            ["TimeCompleted"] = "2026.02.01-00.00.00",
            ["ProspectID"] = "",
        });

        var backupPath = _repository.SaveAccolades(SteamId, accolades);

        // The backup holds the PRE-edit content — one completed accolade, not two.
        using (var zip = ZipFile.OpenRead(backupPath!))
        using (var reader = new StreamReader(zip.GetEntry("Accolades.json")!.Open()))
        {
            Assert.DoesNotContain("WolvesKilled10", reader.ReadToEnd());
        }

        var reloaded = _repository.LoadAccolades(SteamId);
        Assert.Equal(2, reloaded["CompletedAccolades"]!.AsArray().Count);
        // PlayerTrackers, which this editor never touches, survives untouched.
        Assert.Equal(500, reloaded["PlayerTrackers"]!["(RowName=\"TimeSurvived\",DataTableName=\"D_PlayerTrackers\")"]!.GetValue<int>());
    }

    [Fact]
    public void LoadBestiary_NoFile_ReturnsEmptyShapedDefault()
    {
        var bestiary = _repository.LoadBestiary(SteamId);

        Assert.Empty(bestiary["BestiaryTracking"]!.AsArray());
        Assert.Empty(bestiary["FishTracking"]!.AsArray());
    }

    [Fact]
    public void SaveBestiary_RoundTrip_PreservesFishTracking()
    {
        File.WriteAllText(Path.Combine(_playerData, SteamId, "BestiaryData.json"), """
            {
                "BestiaryTracking": [ { "BestiaryGroup": { "RowName": "Forest_Wolf", "DataTableName": "D_BestiaryData" }, "NumPoints": 500 } ],
                "FishTracking": [ { "FishRow": { "RowName": "Fish_09", "DataTableName": "D_FishData" }, "MaxQuality": 43, "CaughtCount": 4 } ]
            }
            """);

        var bestiary = _repository.LoadBestiary(SteamId);
        bestiary["BestiaryTracking"]![0]!["NumPoints"] = 1300;

        _repository.SaveBestiary(SteamId, bestiary);

        var reloaded = _repository.LoadBestiary(SteamId);
        Assert.Equal(1300, reloaded["BestiaryTracking"]![0]!["NumPoints"]!.GetValue<int>());
        // FishTracking, which this editor never touches, survives untouched.
        Assert.Equal("Fish_09", reloaded["FishTracking"]![0]!["FishRow"]!["RowName"]!.GetValue<string>());
        Assert.Equal(4, reloaded["FishTracking"]![0]!["CaughtCount"]!.GetValue<int>());
    }

    [Fact]
    public void LoadMetaInventory_NoFile_ReturnsEmptyShapedDefault()
    {
        var inventory = _repository.LoadMetaInventory(SteamId);

        Assert.Equal("MetaInventoryID_Main", inventory["InventoryID"]!.GetValue<string>());
        Assert.Empty(inventory["Items"]!.AsArray());
    }

    [Fact]
    public void SaveMetaInventory_RoundTrip_PreservesFieldsTheEditorDoesntKnow()
    {
        File.WriteAllText(Path.Combine(_playerData, SteamId, "MetaInventory.json"), """
            {
                "InventoryID": "MetaInventoryID_Main",
                "Items": [
                    {
                        "ItemStaticData": { "RowName": "Wood_Wall", "DataTableName": "D_ItemsStatic" },
                        "ItemDynamicData": [ { "PropertyType": "Durability", "Value": 10000 } ],
                        "DatabaseGUID": "B96AF2F2-377A-4171-8FAD-66900673996C",
                        "ItemOwnerLookupId": -1
                    },
                    {
                        "ItemStaticData": { "RowName": "Sulfur", "DataTableName": "D_ItemsStatic" },
                        "DatabaseGUID": "11111111-2222-3333-4444-555555555555",
                        "ItemOwnerLookupId": -1
                    }
                ]
            }
            """);

        var inventory = _repository.LoadMetaInventory(SteamId);
        // Simulate deleting the second item — the only edit this editor's Items tab actually offers.
        inventory["Items"]!.AsArray().RemoveAt(1);

        var backupPath = _repository.SaveMetaInventory(SteamId, inventory);

        // The backup holds the PRE-edit content — both items still present.
        using (var zip = ZipFile.OpenRead(backupPath!))
        using (var reader = new StreamReader(zip.GetEntry("MetaInventory.json")!.Open()))
        {
            Assert.Contains("Sulfur", reader.ReadToEnd());
        }

        var reloaded = _repository.LoadMetaInventory(SteamId);
        var remaining = Assert.Single(reloaded["Items"]!.AsArray());
        Assert.Equal("Wood_Wall", remaining!["ItemStaticData"]!["RowName"]!.GetValue<string>());
        // Fields this editor never touches survive untouched on the item that remains.
        Assert.Equal("B96AF2F2-377A-4171-8FAD-66900673996C", remaining["DatabaseGUID"]!.GetValue<string>());
        Assert.Equal(10000, remaining["ItemDynamicData"]![0]!["Value"]!.GetValue<int>());
    }

    [Fact]
    public void LoadBinaryFlags_NoFile_ReturnsNull()
    {
        Assert.Null(_repository.LoadBinaryFlags(SteamId));
    }

    [Fact]
    public void LoadBinaryFlags_ParsesTheRealFormat()
    {
        // The real format, confirmed against the user's own flags_<SteamID>.dat: int32 string
        // length (counting the null terminator), null-terminated ASCII SteamID, int32 count,
        // count * int32 flag IDs.
        WriteBinaryFlagsFile([3, 7, 22]);

        var flags = _repository.LoadBinaryFlags(SteamId);

        Assert.Equal([3, 7, 22], flags);
    }

    [Fact]
    public void SaveBinaryFlags_RoundTrips_AndBacksUpFirst()
    {
        WriteBinaryFlagsFile([3, 7]);

        var backupPath = _repository.SaveBinaryFlags(SteamId, [3, 7, 22, 27]);

        Assert.Equal([3, 7, 22, 27], _repository.LoadBinaryFlags(SteamId));

        // The backup holds the PRE-edit file — proof it was taken before the write.
        using var zip = ZipFile.OpenRead(backupPath!);
        var entry = zip.GetEntry($"flags_{SteamId}.dat");
        Assert.NotNull(entry);
        using var stream = new MemoryStream();
        entry.Open().CopyTo(stream);
        // Header (4 + 18) + count (4) + 2 IDs (8) = the original 2-flag file, not the edited one.
        Assert.Equal(4 + SteamId.Length + 1 + 4 + 2 * 4, stream.Length);
    }

    [Fact]
    public void LoadBinaryFlags_TruncatedFile_ThrowsFormatException()
    {
        File.WriteAllBytes(Path.Combine(_playerData, SteamId, $"flags_{SteamId}.dat"), [1, 0, 0]);

        Assert.Throws<FormatException>(() => _repository.LoadBinaryFlags(SteamId));
    }

    /// <summary>
    /// Regression guard: a corrupted header whose strLen sits right at/below int.MaxValue (here,
    /// exactly int.MaxValue) used to make the plain-int "4 + strLen" bounds-check arithmetic
    /// silently overflow into a negative offset, slipping past the guard and reaching
    /// BitConverter.ToInt32 with a negative index — throwing ArgumentOutOfRangeException instead of
    /// the intended FormatException. SavesViewModel.BuildBinaryFlags only ever caught FormatException,
    /// so this failed the ENTIRE save-slot load instead of just hiding the binary-flags section.
    /// </summary>
    [Fact]
    public void LoadBinaryFlags_HeaderLengthNearIntMaxValue_ThrowsFormatExceptionNotArgumentOutOfRange()
    {
        var bytes = new byte[20];
        BitConverter.GetBytes(int.MaxValue).CopyTo(bytes, 0);
        File.WriteAllBytes(Path.Combine(_playerData, SteamId, $"flags_{SteamId}.dat"), bytes);

        Assert.Throws<FormatException>(() => _repository.LoadBinaryFlags(SteamId));
    }

    private void WriteBinaryFlagsFile(int[] flagIds)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            var idBytes = System.Text.Encoding.ASCII.GetBytes(SteamId);
            writer.Write(idBytes.Length + 1);
            writer.Write(idBytes);
            writer.Write((byte)0);
            writer.Write(flagIds.Length);
            foreach (var id in flagIds)
            {
                writer.Write(id);
            }
        }

        File.WriteAllBytes(Path.Combine(_playerData, SteamId, $"flags_{SteamId}.dat"), stream.ToArray());
    }

    private sealed class FakeLocator : ISteamInstallLocator
    {
        public string? FindIcarusContentPath() => null;

        public string? TryGetPersonaName(string steamId64) => steamId64 == SteamId ? "TestPersona" : null;
    }
}
