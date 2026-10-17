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

    private ModReplacementService Service(FakeDialogService? dialogs = null) =>
        new(_library, _ue4ss, _ue4ssMeta, new Ue4ssModStateService(_ue4ss), _settings, Path.Combine(_root, "Backups"), dialogs,
            new ModFixService(_library, Path.Combine(_root, "Data"), decisionsDirectory: _fixChoices));

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
        Assert.Contains("restored from its backup", result.Message);
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
