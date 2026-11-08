using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Install;
using IcarusStarlink.Storage.Library;
using IcarusStarlink.Storage.Ue4ss;
using Microsoft.Extensions.Logging.Abstractions;

namespace IcarusStarlink.App.Tests.Services;

/// <summary>
/// The one replacement path (Downloads' Activate/Reinstall, Library's Get update and Reinstall
/// original) over the REAL Library repository, UE4SS staging, sidecar store and enable/disable
/// mover — only the game folder is a temp folder.
/// </summary>
public sealed class ModReplacementServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "Replace_" + Guid.NewGuid().ToString("N"));
    private readonly FolderLibraryRepository _library;
    private readonly Ue4ssModRepository _ue4ss;
    private readonly Ue4ssModMetaStore _ue4ssMeta;
    private readonly FakeSettingsService _settings = new();
    private readonly string _fixChoices;

    public ModReplacementServiceTests()
    {
        _library = new FolderLibraryRepository(Path.Combine(_root, "Extracted_Mods"), Path.Combine(_root, "Library_Meta"), Path.Combine(_root, "Backups", "Mods"), NullLogger<FolderLibraryRepository>.Instance);
        _ue4ss = new Ue4ssModRepository(Path.Combine(_root, "Staged_UE4SS"));
        _ue4ssMeta = new Ue4ssModMetaStore(Path.Combine(_root, "Ue4ss_Meta"), NullLogger<Ue4ssModMetaStore>.Instance);
        _fixChoices = Path.Combine(_root, "Library_Meta", "Fix_Choices");
    }

    public void Dispose()
    {
        _library.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ModReplacementService Service(FakeDialogService? dialogs = null, IUe4ssModRepository? ue4ss = null) =>
        new(_library, ue4ss ?? _ue4ss, _ue4ssMeta, new Ue4ssModStateService(ue4ss ?? _ue4ss), _settings, Path.Combine(_root, "Backups"), dialogs,
            new ModFixService(_library, Path.Combine(_root, "Data"), decisionsDirectory: _fixChoices));

    /// <summary>Writes a UE4SS mod folder named <paramref name="name"/> holding <paramref name="files"/> (relative path, text) under its own new parent folder; returns the parent.</summary>
    private string Ue4ssSource(string name, params (string Path, string Text)[] files)
    {
        var parent = Path.Combine(_root, "Sources", Guid.NewGuid().ToString("N"));
        foreach (var (relative, text) in files)
        {
            var path = Path.Combine(parent, name, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        return parent;
    }

    /// <summary>Stages a UE4SS mod that is off (only in Staged_UE4SS).</summary>
    private void StageUe4ss(string name, params (string Path, string Text)[] files) =>
        _ue4ss.ImportFromFolder(Ue4ssSource(name, files), name);

    /// <summary>Installs a new UE4SS copy the way Activate does: remove the old copies, then stage the new one.</summary>
    private Func<Action, Task<IReadOnlyList<PendingDownloadActivatedItem>>> InstallUe4ss(string name, params (string Path, string Text)[] files) => removeOld =>
    {
        removeOld();
        var landed = _ue4ss.ImportFromFolder(Ue4ssSource(name, files), name);
        return Task.FromResult<IReadOnlyList<PendingDownloadActivatedItem>>([new PendingDownloadActivatedItem(landed, PendingDownloadActivationKind.Ue4ssMod)]);
    };

    /// <summary>Holds the file open for reading only: it can still be read and copied, but not deleted.</summary>
    private static FileStream HoldOpen(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <summary>A prebuilt pak named <paramref name="name"/>.pak holding <paramref name="content"/>, in its own folder.</summary>
    private string Pak(string name, string content)
    {
        var folder = Path.Combine(_root, "Downloads", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".pak");
        File.WriteAllText(path, content);
        return path;
    }

    private LibraryEntry Entry(string folder) => _library.GetAll().Single(e => e.FolderName == folder);

    private string PakContent(string folder) =>
        File.ReadAllText(Directory.GetFiles(_library.GetFolderPath(folder), "*.pak").Single());

    /// <summary>Installs a new copy of the pak the way Activate does: remove the old copies, then import.</summary>
    private Func<Action, Task<IReadOnlyList<PendingDownloadActivatedItem>>> InstallPak(string name, string content) => removeOld =>
    {
        removeOld();
        var entry = _library.ImportPak(Pak(name, content), source: "Nexus", nexusModId: 223);
        return Task.FromResult<IReadOnlyList<PendingDownloadActivatedItem>>([new PendingDownloadActivatedItem(entry.FolderName, PendingDownloadActivationKind.Library)]);
    };

    [Fact]
    public async Task Replace_CarriesPinNotesAndTheRenameOntoTheNewCopy_AndBacksTheOldOneUp()
    {
        var old = _library.ImportPak(Pak("Homestead", "v1"), source: "Nexus", nexusModId: 223);
        _library.UpdateMetadata(old.FolderName, isPinned: true, isFavorite: true, notes: "keep the big barn");
        _library.SetDisplayNameOverride(old.FolderName, "My Homestead");

        var result = await Service().ReplaceAsync(new ModReplacementRequest("Homestead", [old.FolderName], []), InstallPak("Homestead", "v2"));

        Assert.True(result.Succeeded);
        var replaced = Entry(Assert.Single(result.Items).FolderName);
        Assert.Equal("v2", PakContent(replaced.FolderName));
        Assert.True(replaced.IsPinned);
        Assert.True(replaced.IsFavorite);
        Assert.Equal("keep the big barn", replaced.Notes);
        Assert.Equal("My Homestead", replaced.Name);
        Assert.True(_library.HasModBackup(old.FolderName));
        Assert.Single(_library.GetAll());
    }

    /// <summary>The real Ferani_Homestead_Expanded case: an edited (✎) mod was silently replaced by a Reinstall.</summary>
    [Fact]
    public async Task Replace_EditedCopy_AsksFirst_AndKeepingItChangesNothing()
    {
        var old = _library.ImportPak(Pak("Homestead", "my edits"), source: "Nexus", nexusModId: 223);
        _library.MarkLocallyEdited(old.FolderName);
        var dialogs = new FakeDialogService(confirmResult: false);
        var installed = false;

        var result = await Service(dialogs).ReplaceAsync(
            new ModReplacementRequest("Homestead", [old.FolderName], []),
            removeOld => { installed = true; return InstallPak("Homestead", "v2")(removeOld); });

        Assert.True(result.Cancelled);
        Assert.False(result.Succeeded);
        Assert.False(installed);
        Assert.Contains("Kept your edited copy", result.Message);
        Assert.Contains("changes made in Starlink", Assert.Single(dialogs.ConfirmMessages));
        Assert.Equal("my edits", PakContent(old.FolderName));
        Assert.True(Entry(old.FolderName).IsLocallyEdited);
    }

    [Fact]
    public async Task Replace_EditedCopy_ReplacedWhenTheUserSaysSo()
    {
        var old = _library.ImportPak(Pak("Homestead", "my edits"), source: "Nexus", nexusModId: 223);
        _library.MarkLocallyEdited(old.FolderName);

        var result = await Service(new FakeDialogService(confirmResult: true)).ReplaceAsync(
            new ModReplacementRequest("Homestead", [old.FolderName], []), InstallPak("Homestead", "v2"));

        Assert.True(result.Succeeded);
        Assert.Equal("v2", PakContent(Assert.Single(result.Items).FolderName));
    }

    [Fact]
    public async Task Replace_InstallFails_PutsTheOldCopyBackWithItsLinkAndPin()
    {
        var old = _library.ImportPak(Pak("Homestead", "v1"), source: "Nexus", nexusModId: 223);
        _library.UpdateMetadata(old.FolderName, isPinned: true, isFavorite: false, notes: "n");
        _library.SetNexusFile(old.FolderName, 4242, "1.7");

        var result = await Service().ReplaceAsync(
            new ModReplacementRequest("Homestead", [old.FolderName], []),
            removeOld =>
            {
                removeOld();
                throw new IOException("the disk is full");
            });

        Assert.False(result.Succeeded);
        Assert.True(result.RolledBack);
        Assert.Contains("the disk is full", result.Message);
        Assert.Contains("your previous copy is back", result.Message);
        var restored = Entry(old.FolderName);
        Assert.Equal("v1", PakContent(old.FolderName));
        Assert.Equal("Nexus", restored.Source);
        Assert.Equal(223, restored.NexusModId);
        Assert.Equal(4242, restored.NexusFileId);
        Assert.True(restored.IsPinned);
    }

    [Fact]
    public async Task Replace_AFailureBeforeAnythingWasRemoved_LeavesTheOldCopyAlone()
    {
        var old = _library.ImportPak(Pak("Homestead", "v1"), source: "Nexus", nexusModId: 223);

        var result = await Service().ReplaceAsync(
            new ModReplacementRequest("Homestead", [old.FolderName], []),
            _ => throw new InvalidDataException("not an archive"));

        Assert.False(result.Succeeded);
        Assert.False(result.RolledBack);
        Assert.Equal("v1", PakContent(old.FolderName));
    }

    /// <summary>
    /// Reinstalling an ENABLED UE4SS mod never replaced the copy the game runs (only staging was
    /// cleared); a later Disable then landed the old game copy as "MiniMapa_2".
    /// </summary>
    [Fact]
    public async Task Replace_EnabledUe4ssMod_ReplacesTheGamesCopyAndKeepsItEnabled()
    {
        _settings.Current.IcarusContentPath = Path.Combine(_root, "Game", "Icarus", "Content");
        var gameMods = Ue4ssGamePaths.ResolveModsFolder(_settings.Current.IcarusContentPath);
        Directory.CreateDirectory(Path.Combine(gameMods, "MiniMapa", "Scripts"));
        File.WriteAllText(Path.Combine(gameMods, "MiniMapa", "Scripts", "main.lua"), "old");
        _ue4ssMeta.Save("MiniMapa", new Ue4ssModMeta { NexusModId = 345, MinUe4ssVersion = "3.0.1" });

        var result = await Service().ReplaceAsync(
            new ModReplacementRequest("Minimap", [], ["MiniMapa"]),
            removeOld =>
            {
                removeOld();
                var source = Path.Combine(_root, "Unpacked", "MiniMapa");
                Directory.CreateDirectory(Path.Combine(source, "Scripts"));
                File.WriteAllText(Path.Combine(source, "Scripts", "main.lua"), "new");
                var name = _ue4ss.ImportFromFolder(Path.GetDirectoryName(source)!, "MiniMapa");
                _ue4ssMeta.Save(name, new Ue4ssModMeta { NexusModId = 345 });
                return Task.FromResult<IReadOnlyList<PendingDownloadActivatedItem>>([new PendingDownloadActivatedItem(name, PendingDownloadActivationKind.Ue4ssMod)]);
            });

        Assert.True(result.Succeeded);
        Assert.Equal("new", File.ReadAllText(Path.Combine(gameMods, "MiniMapa", "Scripts", "main.lua")));
        Assert.Equal(["MiniMapa"], _ue4ss.ListInstalledInGame(gameMods));
        Assert.Empty(_ue4ss.GetAll()); // enabled = in the game, not staging; no "MiniMapa_2" anywhere
        Assert.Equal("3.0.1", _ue4ssMeta.Load("MiniMapa").MinUe4ssVersion);
        Assert.True(Directory.Exists(Path.Combine(_root, "Backups", "UE4SS")));
    }

    [Fact]
    public async Task Replace_EnabledUe4ssModInstallFails_PutsTheOldCopyBackInTheGame()
    {
        _settings.Current.IcarusContentPath = Path.Combine(_root, "Game", "Icarus", "Content");
        var gameMods = Ue4ssGamePaths.ResolveModsFolder(_settings.Current.IcarusContentPath);
        Directory.CreateDirectory(Path.Combine(gameMods, "MiniMapa", "Scripts"));
        File.WriteAllText(Path.Combine(gameMods, "MiniMapa", "Scripts", "main.lua"), "old");
        _ue4ssMeta.Save("MiniMapa", new Ue4ssModMeta { NexusModId = 345 });

        var result = await Service().ReplaceAsync(
            new ModReplacementRequest("Minimap", [], ["MiniMapa"]),
            removeOld =>
            {
                removeOld();
                throw new IOException("locked");
            });

        Assert.True(result.RolledBack);
        Assert.Equal("old", File.ReadAllText(Path.Combine(gameMods, "MiniMapa", "Scripts", "main.lua")));
        Assert.Empty(_ue4ss.GetAll());
        Assert.Equal(345, _ue4ssMeta.Load("MiniMapa").NexusModId);
    }

    /// <summary>
    /// Activating a new Nexus version of a UE4SS mod replaced the player's
    /// config.lua with the shipped one. The update keeps the installed config (and names the new
    /// settings it lacks); the shipped defaults file is the new version's.
    /// </summary>
    [Fact]
    public async Task Replace_EnabledUe4ssMod_KeepsThePlayersConfig()
    {
        _settings.Current.IcarusContentPath = Path.Combine(_root, "Game", "Icarus", "Content");
        var gameMods = Ue4ssGamePaths.ResolveModsFolder(_settings.Current.IcarusContentPath);
        var gameMod = Path.Combine(gameMods, "MkHatersTools");
        Directory.CreateDirectory(Path.Combine(gameMod, "Scripts"));
        File.WriteAllText(Path.Combine(gameMod, "Scripts", "main.lua"), "old");
        File.WriteAllText(Path.Combine(gameMod, "config.lua"), "return {\n    MaxScale = 2.0, -- mine\n}\n");
        File.WriteAllText(Path.Combine(gameMod, "config.default.lua"), "return {\n    MaxScale = 4.0,\n}\n");

        var result = await Service().ReplaceAsync(
            new ModReplacementRequest("MkHaters Tools", [], ["MkHatersTools"]),
            removeOld =>
            {
                removeOld();
                var source = Path.Combine(_root, "Unpacked", "MkHatersTools");
                Directory.CreateDirectory(Path.Combine(source, "Scripts"));
                File.WriteAllText(Path.Combine(source, "Scripts", "main.lua"), "new");
                File.WriteAllText(Path.Combine(source, "config.lua"), "return {\n    MaxScale = 4.0,\n    MinScale = 0.25, -- new\n}\n");
                File.WriteAllText(Path.Combine(source, "config.default.lua"), "return {\n    MaxScale = 4.0,\n    MinScale = 0.25,\n}\n");
                var name = _ue4ss.ImportFromFolder(Path.GetDirectoryName(source)!, "MkHatersTools");
                return Task.FromResult<IReadOnlyList<PendingDownloadActivatedItem>>([new PendingDownloadActivatedItem(name, PendingDownloadActivationKind.Ue4ssMod)]);
            });

        Assert.True(result.Succeeded);
        Assert.Equal("new", File.ReadAllText(Path.Combine(gameMod, "Scripts", "main.lua")));
        // The new layout with the player's value in it, the new setting at its default.
        Assert.Equal("return {\n    MaxScale = 2.0,\n    MinScale = 0.25, -- new\n}\n", File.ReadAllText(Path.Combine(gameMod, "config.lua")));
        Assert.Equal("return {\n    MaxScale = 4.0,\n    MinScale = 0.25,\n}\n", File.ReadAllText(Path.Combine(gameMod, "config.default.lua")));
        Assert.Equal(["config.lua"], result.KeptConfigs);
        Assert.Equal(["config.lua: MinScale"], result.NewSettings);
        Assert.Contains("Kept your settings (config.lua).", result.Summary);
        Assert.Contains("Look over its settings in Edit config", result.Summary);
        // Remembered on the mod, for the Library row's mark and the editor's review banner.
        var review = Assert.Single(_ue4ssMeta.Load("MkHatersTools").ConfigReviews!);
        Assert.Equal(["MinScale"], review.Added);
    }

    /// <summary>Reinstall original is the copy as downloaded: the shipped config, not the player's.</summary>
    [Fact]
    public async Task Replace_PristineUe4ssMod_TakesTheShippedConfig()
    {
        var old = Path.Combine(_root, "Old", "Pets");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "config.lua"), "mine");
        _ue4ss.ImportFromFolder(Path.GetDirectoryName(old)!, "Pets");

        var result = await Service().ReplaceAsync(
            new ModReplacementRequest("Pets", [], ["Pets"], Pristine: true),
            removeOld =>
            {
                removeOld();
                var source = Path.Combine(_root, "Unpacked", "Pets");
                Directory.CreateDirectory(source);
                File.WriteAllText(Path.Combine(source, "config.lua"), "shipped");
                var name = _ue4ss.ImportFromFolder(Path.GetDirectoryName(source)!, "Pets");
                return Task.FromResult<IReadOnlyList<PendingDownloadActivatedItem>>([new PendingDownloadActivatedItem(name, PendingDownloadActivationKind.Ue4ssMod)]);
            });

        Assert.True(result.Succeeded);
        Assert.Equal("shipped", File.ReadAllText(Path.Combine(_ue4ss.GetFolderPath("Pets"), "config.lua")));
        Assert.Empty(result.KeptConfigs);
    }

    /// <summary>Reinstall original forgets the fix choices — but only once it worked (it used to forget them first, then report success either way).</summary>
    [Fact]
    public async Task Replace_Pristine_ForgetsTheFixChoicesOnlyAfterItWorked()
    {
        var old = _library.ImportPak(Pak("Homestead", "v1"), source: "Nexus", nexusModId: 223);
        _library.SetDisplayNameOverride(old.FolderName, "Renamed");
        Directory.CreateDirectory(_fixChoices);
        var choices = Path.Combine(_fixChoices, old.FolderName + ".json");
        File.WriteAllText(choices, "{}");

        var failed = await Service().ReplaceAsync(
            new ModReplacementRequest("Homestead", [old.FolderName], [], Pristine: true),
            removeOld =>
            {
                removeOld();
                throw new IOException("boom");
            });
        Assert.False(failed.Succeeded);
        Assert.True(File.Exists(choices));

        var worked = await Service().ReplaceAsync(new ModReplacementRequest("Homestead", [old.FolderName], [], Pristine: true), InstallPak("Homestead", "v1"));

        Assert.True(worked.Succeeded);
        Assert.False(File.Exists(choices));
        Assert.Null(Entry(Assert.Single(worked.Items).FolderName).DisplayNameOverride); // pristine: the rename isn't carried
    }

    /// <summary>Regression: an old UE4SS copy that was off went only to a temp folder that the replacement then deleted, so nothing of it survived.</summary>
    [Fact]
    public async Task Replace_DisabledUe4ssMod_BacksTheOldCopyUpUnderBackupsUe4ss()
    {
        StageUe4ss("Pets", ("Scripts/main.lua", "my build 0.8.0"));

        var result = await Service().ReplaceAsync(new ModReplacementRequest("Pets", [], ["Pets"]), InstallUe4ss("Pets", ("Scripts/main.lua", "nexus 0.7.0")));

        Assert.True(result.Succeeded);
        Assert.Equal("nexus 0.7.0", File.ReadAllText(Path.Combine(_ue4ss.GetFolderPath("Pets"), "Scripts", "main.lua")));
        var backup = Assert.Single(FolderBackup.EnumerateFolderBackups(Path.Combine(_root, "Backups", "UE4SS"), "Pets"));
        Assert.Equal("my build 0.8.0", File.ReadAllText(Path.Combine(backup, "Scripts", "main.lua")));
    }

    [Fact]
    public async Task Replace_DisabledUe4ssModThatCantBeBackedUp_ReplacesNothing()
    {
        StageUe4ss("Pets", ("Scripts/main.lua", "my build 0.8.0"));
        // A file where the backup folder should be: the backup can't be made.
        Directory.CreateDirectory(Path.Combine(_root, "Backups"));
        File.WriteAllText(Path.Combine(_root, "Backups", "UE4SS"), "");
        var installed = false;

        var result = await Service().ReplaceAsync(
            new ModReplacementRequest("Pets", [], ["Pets"]),
            removeOld =>
            {
                installed = true;
                return InstallUe4ss("Pets", ("Scripts/main.lua", "nexus 0.7.0"))(removeOld);
            });

        Assert.False(result.Succeeded);
        Assert.Contains("Couldn't back up", result.Message);
        Assert.False(installed);
        Assert.Equal("my build 0.8.0", File.ReadAllText(Path.Combine(_ue4ss.GetFolderPath("Pets"), "Scripts", "main.lua")));
    }

    /// <summary>Regression: a staged copy whose delete stopped partway (a file held open) was left gutted, while the result said it was restored.</summary>
    [Fact]
    public async Task Replace_StagedUe4ssModWhoseDeleteStopsPartway_PutsTheWholeCopyBack()
    {
        StageUe4ss("Pets", ("Scripts/main.lua", "old"), ("config.lua", "mine"));
        var held = Path.Combine(_ue4ss.GetFolderPath("Pets"), "Scripts", "main.lua");

        var result = await Service().ReplaceAsync(
            new ModReplacementRequest("Pets", [], ["Pets"]),
            removeOld =>
            {
                using (HoldOpen(held))
                {
                    removeOld();
                }

                return InstallUe4ss("Pets", ("Scripts/main.lua", "new"))(() => { });
            });

        Assert.False(result.Succeeded);
        Assert.True(result.RolledBack);
        var config = Path.Combine(_ue4ss.GetFolderPath("Pets"), "config.lua");
        Assert.True(File.Exists(config), "config.lua is gone from the staged copy");
        Assert.Equal("mine", File.ReadAllText(config));
        Assert.Equal("old", File.ReadAllText(Path.Combine(_ue4ss.GetFolderPath("Pets"), "Scripts", "main.lua")));
        Assert.Equal(["Pets"], _ue4ss.GetAll());
    }

    /// <summary>Regression: an enabled mod whose sidecar couldn't be deleted ended up in neither the game nor staging, while the result said it was restored.</summary>
    [Fact]
    public async Task Replace_EnabledUe4ssModWhoseSidecarIsLocked_PutsItBackInTheGame()
    {
        _settings.Current.IcarusContentPath = Path.Combine(_root, "Game", "Icarus", "Content");
        var gameMods = Ue4ssGamePaths.ResolveModsFolder(_settings.Current.IcarusContentPath);
        Directory.CreateDirectory(Path.Combine(gameMods, "MiniMapa", "Scripts"));
        File.WriteAllText(Path.Combine(gameMods, "MiniMapa", "Scripts", "main.lua"), "old");
        _ue4ssMeta.Save("MiniMapa", new Ue4ssModMeta { NexusModId = 345 });

        var result = await Service().ReplaceAsync(
            new ModReplacementRequest("Minimap", [], ["MiniMapa"]),
            removeOld =>
            {
                using (HoldOpen(Path.Combine(_root, "Ue4ss_Meta", "MiniMapa.json")))
                {
                    removeOld();
                }

                throw new InvalidOperationException("removeOld should have failed");
            });

        Assert.True(result.RolledBack);
        var gameCopy = Path.Combine(gameMods, "MiniMapa", "Scripts", "main.lua");
        Assert.True(File.Exists(gameCopy), "MiniMapa is no longer in the game");
        Assert.Equal("old", File.ReadAllText(gameCopy));
        Assert.Equal(345, _ue4ssMeta.Load("MiniMapa").NexusModId);
        Assert.Empty(_ue4ss.GetAll());
    }

    /// <summary>Regression: a rollback that couldn't finish deleted the temp folder holding the only whole copy, and pointed at Backups\Mods (where UE4SS mods never go).</summary>
    [Fact]
    public async Task Replace_RollbackThatCantFinish_KeepsTheScratchCopyAndSaysWhere()
    {
        StageUe4ss("CoolLua", ("Scripts/main.lua", "old (my edit)"));
        var ue4ss = new FailingImportUe4ssRepository(_ue4ss);

        var result = await Service(ue4ss: ue4ss).ReplaceAsync(
            new ModReplacementRequest("CoolLua", [], ["CoolLua"]),
            removeOld =>
            {
                removeOld();
                ue4ss.FailImports = true;
                throw new IOException("disk full");
            });

        Assert.False(result.RolledBack);
        Assert.DoesNotContain("Backups\\Mods", result.Message);
        Assert.Contains("Backups\\UE4SS", result.Message);
        var at = result.Message!.IndexOf("IcarusStarlink_Replace_", StringComparison.Ordinal);
        Assert.True(at >= 0, $"The message doesn't say where the whole copy is: {result.Message}");
        var scratch = Path.Combine(Path.GetTempPath(), result.Message.Substring(at, "IcarusStarlink_Replace_".Length + 32));
        try
        {
            Assert.True(Directory.Exists(scratch), "The temp folder holding the whole copy was deleted");
            Assert.Equal("old (my edit)", File.ReadAllText(Path.Combine(scratch, "CoolLua", "CoolLua", "Scripts", "main.lua")));
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    /// <summary>Regression: a failed install's rollback deleted every Library folder that wasn't there before, a mod made on the Editor page meanwhile too.</summary>
    [Fact]
    public async Task Replace_UntrackedInstallFails_NeverDeletesAModMadeInStarlink()
    {
        var old = _library.ImportPak(Pak("Homestead", "v1"), source: "Nexus", nexusModId: 223);

        var result = await Service().ReplaceAsync(
            new ModReplacementRequest("Homestead", [old.FolderName], []),
            removeOld =>
            {
                removeOld();
                _library.CreateBlankMod("Mine", "me");
                throw new IOException("disk full");
            });

        Assert.True(result.RolledBack);
        Assert.Contains(_library.GetAll(), e => e.FolderName == "Mine");
        Assert.True(Directory.Exists(Path.Combine(_root, "Extracted_Mods", "Mine")));
        Assert.Equal("v1", PakContent(old.FolderName));
    }

    /// <summary>Regression: a failed install's rollback deleted mods another page added while it ran (it took every new folder for its own).</summary>
    [Fact]
    public async Task Replace_TrackedInstallFails_KeepsAModAddedElsewhereMeanwhile()
    {
        var old = _library.ImportPak(Pak("Homestead", "v1"), source: "Nexus", nexusModId: 223);

        var result = await Service().ReplaceAsync(
            new ModReplacementRequest("Homestead", [old.FolderName], []),
            (removeOld, writes) =>
            {
                removeOld();
                _library.CreateBlankMod("Mine", "me");
                _library.ImportPak(Pak("Other", "x"));
                var partial = _library.ImportPak(Pak("Homestead_v2", "v2"));
                writes.Wrote(partial.FolderName, PendingDownloadActivationKind.Library);
                throw new IOException("disk full");
            });

        Assert.True(result.RolledBack);
        Assert.Equal(["Homestead", "Mine", "Other"], _library.GetAll().Select(e => e.FolderName).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal("v1", PakContent(old.FolderName));
    }

    [Fact]
    public async Task Replace_UntrackedInstallFails_BacksUpWhatItSweeps()
    {
        var old = _library.ImportPak(Pak("Homestead", "v1"), source: "Nexus", nexusModId: 223);

        var result = await Service().ReplaceAsync(
            new ModReplacementRequest("Homestead", [old.FolderName], []),
            removeOld =>
            {
                removeOld();
                _library.ImportPak(Pak("Other", "x"));
                throw new IOException("disk full");
            });

        Assert.True(result.RolledBack);
        Assert.Single(FolderBackup.EnumerateFolderBackups(Path.Combine(_root, "Backups", "Mods"), "Other"));
    }

    /// <summary>Staging that can be told to fail every import, for a rollback that can't put a copy back.</summary>
    private sealed class FailingImportUe4ssRepository(IUe4ssModRepository inner) : IUe4ssModRepository
    {
        public bool FailImports { get; set; }

        public IReadOnlyList<string> GetAll() => inner.GetAll();

        public string ImportFromFolder(string sourceFolder, string fallbackName, IReadOnlyCollection<string>? namesAlreadyInUse = null) =>
            FailImports ? throw new IOException("disk full") : inner.ImportFromFolder(sourceFolder, fallbackName, namesAlreadyInUse);

        public void Delete(string folderName) => inner.Delete(folderName);

        public string GetFolderPath(string folderName) => inner.GetFolderPath(folderName);

        public IReadOnlyList<string> ListInstalledInGame(string gameModsFolderPath) => inner.ListInstalledInGame(gameModsFolderPath);

        public string AdoptFromGame(string gameModsFolderPath, string folderName, IReadOnlyCollection<string>? namesAlreadyInUse = null) =>
            inner.AdoptFromGame(gameModsFolderPath, folderName, namesAlreadyInUse);
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public bool Save() => true;
    }

    private sealed class FakeDialogService(bool confirmResult) : IDialogService
    {
        public List<string> ConfirmMessages { get; } = [];

        public bool Confirm(string message, string title, ThemedConfirmSeverity severity)
        {
            ConfirmMessages.Add(message);
            return confirmResult;
        }

        public RenamePromptResult PromptRename(string currentName, string description = "", string? resetValue = null, string resetLabel = "", string resetTooltip = "", string title = "", string fieldLabel = "") =>
            throw new NotSupportedException();
    }
}
