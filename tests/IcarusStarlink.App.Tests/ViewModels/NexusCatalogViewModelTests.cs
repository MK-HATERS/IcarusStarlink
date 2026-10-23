using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.App.Views;
using IcarusStarlink.Catalog;
using IcarusStarlink.Catalog.Daedalus;
using IcarusStarlink.Catalog.GitHub;
using IcarusStarlink.Catalog.Jimk72;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Catalog;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Import;
using CommunityToolkit.Mvvm.Messaging;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>
/// Only covers IsPremium/IsKnownNonPremium — the flag driving whether the Nexus page's Download
/// button (which can never succeed for a non-Premium account, see FetchAndDownloadAsync's own doc
/// comment) is shown as available. Everything else on this large VM (browse/search/track/badges)
/// isn't exercised here.
/// </summary>
public class NexusCatalogViewModelTests
{
    private sealed class Harness
    {
        public FakeNexusApiClient NexusApiClient { get; } = new();
        public FakeCredentialStore CredentialStore { get; } = new();

        public NexusCatalogViewModel Build()
        {
            var settingsService = new FakeSettingsService();
            var downloads = new DownloadsViewModel(
                new FakeDaedalusClient(), new FakeJimk72Client(), new FakeGitHubRepoDateClient(),
                new FakeLibraryRepository(), new FakeUe4ssModRepository(), new FakeUe4ssModMetaStore(),
                settingsService, NexusApiClient, CredentialStore, new FakePendingDownloadStore(),
                new HttpClient(), new PerformanceTracker(settingsService, Path.GetTempPath()),
                new ActivityLog(), new ActiveDownloadsTracker(),
                Path.GetTempPath(), new FakePrebuiltPakImporter(), Path.GetTempPath());

            return new NexusCatalogViewModel(
                NexusApiClient, CredentialStore, new FakeNexusWatchlistStore(), new FakeLibraryRepository(),
                new FakePendingDownloadStore(), downloads, new ActivityLog(), new FakeDialogService());
        }
    }

    [Fact]
    public void EmptyStateText_NoCards_SaysLoadingWhileLoadingThenTheStatusReason()
    {
        var vm = new Harness().Build();

        vm.IsLoading = true;
        Assert.Equal("Loading mods from Nexus…", vm.EmptyStateText);

        vm.IsLoading = false;
        vm.StatusMessage = "Nothing matches the filters.";
        Assert.Equal("Nothing matches the filters.", vm.EmptyStateText);
    }

    [Fact]
    public void Construction_NoSavedApiKey_IsPremiumStaysNull()
    {
        var harness = new Harness();

        var vm = harness.Build();

        Assert.Null(vm.IsPremium);
        Assert.False(vm.IsKnownNonPremium);
    }

    [Fact]
    public async Task Construction_SavedKeyBelongsToNonPremiumAccount_IsKnownNonPremiumBecomesTrue()
    {
        // A real bug found live: users reported needing Nexus Premium just to use the Nexus
        // browser at all — really, only Download (which builds a bare nxm:// URL with no
        // key/expires, see FetchAndDownloadAsync) can never work for a non-Premium account, but
        // nothing on this page said so until after a failed click. IsPremium is what a visible
        // banner and the Download/Open page button styling key off of instead.
        var harness = new Harness();
        harness.CredentialStore.Save(CredentialTargets.NexusApiKey, "free-account-key");
        harness.NexusApiClient.UserToReturn = new NexusUserInfo(1, "SomeUser", IsPremium: false, IsSupporter: false, "u@example.test", "https://example.test");

        var vm = harness.Build();
        await harness.NexusApiClient.ValidateKeyCompletion.Task;

        Assert.False(vm.IsPremium);
        Assert.True(vm.IsKnownNonPremium);
    }

    [Fact]
    public async Task Construction_SavedKeyBelongsToPremiumAccount_IsKnownNonPremiumStaysFalse()
    {
        var harness = new Harness();
        harness.CredentialStore.Save(CredentialTargets.NexusApiKey, "premium-account-key");
        harness.NexusApiClient.UserToReturn = new NexusUserInfo(1, "SomeUser", IsPremium: true, IsSupporter: false, "u@example.test", "https://example.test");

        var vm = harness.Build();
        await harness.NexusApiClient.ValidateKeyCompletion.Task;

        Assert.True(vm.IsPremium);
        Assert.False(vm.IsKnownNonPremium);
    }

    [Fact]
    public async Task NexusAccountChangedMessage_ReSignedInAsNonPremium_UpdatesIsKnownNonPremiumWithoutRestart()
    {
        // The scenario a user actually hits: already on the Nexus page, then signs in via Settings
        // (or switches accounts) — this page's own Download/Open page prominence must catch up
        // immediately, not only on next launch.
        var harness = new Harness();
        var vm = harness.Build();
        await Task.Yield();
        Assert.Null(vm.IsPremium);

        harness.CredentialStore.Save(CredentialTargets.NexusApiKey, "free-account-key");
        harness.NexusApiClient.UserToReturn = new NexusUserInfo(1, "SomeUser", IsPremium: false, IsSupporter: false, "u@example.test", "https://example.test");
        harness.NexusApiClient.ValidateKeyCompletion = new TaskCompletionSource();
        WeakReferenceMessenger.Default.Send(new NexusAccountChangedMessage());
        await harness.NexusApiClient.ValidateKeyCompletion.Task;

        Assert.True(vm.IsKnownNonPremium);
    }

    [Fact]
    public async Task SignOut_ClearsSavedKey_IsKnownNonPremiumGoesBackToFalse()
    {
        var harness = new Harness();
        harness.CredentialStore.Save(CredentialTargets.NexusApiKey, "free-account-key");
        harness.NexusApiClient.UserToReturn = new NexusUserInfo(1, "SomeUser", IsPremium: false, IsSupporter: false, "u@example.test", "https://example.test");
        var vm = harness.Build();
        await harness.NexusApiClient.ValidateKeyCompletion.Task;
        Assert.True(vm.IsKnownNonPremium);

        harness.CredentialStore.Delete(CredentialTargets.NexusApiKey);
        harness.NexusApiClient.ValidateKeyCompletion = new TaskCompletionSource();
        WeakReferenceMessenger.Default.Send(new NexusAccountChangedMessage());
        await Task.Yield();

        Assert.Null(vm.IsPremium);
        Assert.False(vm.IsKnownNonPremium);
    }

    /// <summary>
    /// A real bug: switching to a curated list with no API key superseded the load in flight and
    /// returned early, and the superseded load's finally skips clearing IsLoading — "Loading…"
    /// stuck forever.
    /// </summary>
    [Fact]
    public void CuratedListWithoutAKey_WhileAllIsStillLoading_ClearsIsLoading()
    {
        var harness = new Harness();
        harness.NexusApiClient.AllModsPage = new TaskCompletionSource<NexusModPage>().Task;
        var vm = harness.Build();
        Assert.True(vm.IsLoading);

        vm.SelectedKind = "Trending";

        Assert.False(vm.IsLoading);
        Assert.Contains("API key", vm.StatusMessage);
    }

    /// <summary>
    /// User report 2026-10-23: Library's Update filled the Nexus search with the mod's Library
    /// name, which often isn't its Nexus title, so the mod wasn't found. The ID (or a pasted mod
    /// link) now finds that exact mod.
    /// </summary>
    [Theory]
    [InlineData("412")]
    [InlineData("#412")]
    [InlineData("https://www.nexusmods.com/icarus/mods/412?tab=files")]
    public async Task Search_ModIdOrLink_FindsThatModById(string searchText)
    {
        var harness = new Harness();
        harness.NexusApiClient.ModsById[412] = new NexusModInfo(412, "Wireless Power (Nexus title)", "x", null, "2.0", null);
        var vm = harness.Build();

        vm.SearchText = searchText;
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(412, Assert.Single(vm.Mods).Mod.ModId);
        Assert.Empty(harness.NexusApiClient.SearchedNames);
    }

    [Fact]
    public async Task Search_NumberThatIsNoMod_FallsBackToNameSearch()
    {
        var harness = new Harness();
        var vm = harness.Build();

        vm.SearchText = "100";
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(["100"], harness.NexusApiClient.SearchedNames);
        Assert.Single(vm.Mods);
    }

    private sealed class FakeNexusApiClient : INexusApiClient
    {
        public NexusUserInfo? UserToReturn { get; set; }
        public TaskCompletionSource ValidateKeyCompletion { get; set; } = new();

        public Task<NexusUserInfo?> ValidateKeyAsync(string apiKey, CancellationToken cancellationToken = default)
        {
            ValidateKeyCompletion.TrySetResult();
            return Task.FromResult(UserToReturn);
        }

        public Task<IReadOnlyList<NexusDownloadLink>> GetDownloadLinksAsync(string apiKey, string gameDomain, int modId, int fileId, string? key, long? expires, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
        public Task<NexusModInfo?> GetModInfoAsync(string apiKey, string gameDomain, int modId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
        public Task<IReadOnlyList<NexusModInfo>> GetModListAsync(string apiKey, string gameDomain, NexusModList list, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
        public Task<IReadOnlyList<NexusModFile>> GetModFilesAsync(string apiKey, string gameDomain, int modId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
        /// <summary>Mods by ID for GetModsByIdAsync; SearchedNames records each name search.</summary>
        public Dictionary<int, NexusModInfo> ModsById { get; } = [];
        public List<string> SearchedNames { get; } = [];

        public Task<IReadOnlyList<NexusModInfo>> SearchModsAsync(string? apiKey, string gameDomain, string searchText, CancellationToken cancellationToken = default)
        {
            SearchedNames.Add(searchText);
            return Task.FromResult<IReadOnlyList<NexusModInfo>>([new NexusModInfo(1, $"Name match for {searchText}", "a", null, "1.0", null)]);
        }

        public Task<IReadOnlyList<NexusModInfo>> GetModsByIdAsync(string? apiKey, string gameDomain, IReadOnlyList<int> modIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NexusModInfo>>([.. modIds.Where(ModsById.ContainsKey).Select(id => ModsById[id])]);
        /// <summary>When set, what the All list's fetch returns (a never-finished task keeps a load in flight).</summary>
        public Task<NexusModPage>? AllModsPage { get; set; }

        public Task<NexusModPage> ListAllModsAsync(string? apiKey, string gameDomain, int offset, int count, CancellationToken cancellationToken = default) =>
            AllModsPage ?? throw new NotSupportedException("Not exercised by these tests — this VM's own construction-time load is expected to fail internally and is caught by its own try/catch.");
        public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetChangelogsAsync(string apiKey, string gameDomain, int modId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
        public Task<IReadOnlyList<NexusEndorsement>> GetEndorsementsAsync(string apiKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
        public Task<NexusEndorsementStatus> SetEndorsementAsync(string apiKey, string gameDomain, int modId, string modVersion, bool endorse, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _values = [];
        public void Save(string target, string secret) => _values[target] = secret;
        public string? Read(string target) => _values.GetValueOrDefault(target);
        public void Delete(string target) => _values.Remove(target);
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public bool Save() => true;
    }

    private sealed class FakeLibraryRepository : ILibraryRepository
    {
        public IReadOnlyList<LibraryEntry> GetAll() => [];
        public IReadOnlyList<string> UnreadableFolders => [];
        public IReadOnlyList<LibraryEntry> Search(string query) => [];
        public LibraryEntry Import(string sourcePath, string? source = null, int? nexusModId = null, string? catalogEntryId = null) =>
            throw new NotSupportedException("Not exercised by these tests.");
        public LibraryEntry ImportPaks(IReadOnlyList<string> pakFilePaths, string name, string? source = null, int? nexusModId = null, string? catalogEntryId = null) => throw new NotSupportedException("Not exercised by these tests.");
        public LibraryEntry ImportPak(string pakFilePath, string? source = null, int? nexusModId = null, string? catalogEntryId = null, string? mergedPackProfileName = null) =>
            throw new NotSupportedException("Not exercised by these tests.");
        public void SetNexusMetadata(string folderName, string? name, string? author, string? description, string? version) =>
            throw new NotSupportedException("Not exercised by these tests.");
        public void Refresh() => throw new NotSupportedException("Not exercised by these tests.");
        public void Delete(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public void UpdateMetadata(string folderName, bool isPinned, bool isFavorite, string notes) =>
            throw new NotSupportedException("Not exercised by these tests.");
        public void MarkLocallyEdited(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public void MarkConvertedFromPrebuiltPak(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public void SetDisplayNameOverride(string folderName, string? displayName) => throw new NotSupportedException("Not exercised by these tests.");
        public void LinkToNexus(string folderName, int nexusModId) => throw new NotSupportedException("Not exercised by these tests.");
        public void SetCatalogEntry(string folderName, string catalogEntryId) => throw new NotSupportedException("Not exercised by these tests.");
        public string BackupMod(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public bool HasModBackup(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public bool RestoreLatestModBackup(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public string? TryGetLatestModBackupPath(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public LibraryEntry CreateBlankMod(string name, string author, ModTemplate template = ModTemplate.Blank) =>
            throw new NotSupportedException("Not exercised by these tests.");
        public IReadOnlyList<string> ListAssetPaths(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public IReadOnlyList<string> ListAssetPaths(string folderName, IReadOnlyList<string> precomputedFiles) =>
            throw new NotSupportedException("Not exercised by these tests.");
        public byte[] ReadAssetContent(string folderName, string relativePath) => throw new NotSupportedException("Not exercised by these tests.");
        public string? ReadReadme(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public string? ReadReadme(string folderName, IReadOnlyList<string> precomputedFiles) =>
            throw new NotSupportedException("Not exercised by these tests.");
        public IReadOnlyList<string> ListFolderFiles(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public string GetFolderPath(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakePendingDownloadStore : IPendingDownloadStore
    {
        public IReadOnlyList<PendingDownloadEntry> Entries => [];
        public void Add(PendingDownloadEntry entry) => throw new NotSupportedException("Not exercised by these tests.");
        public void Remove(int modId, int fileId) => throw new NotSupportedException("Not exercised by these tests.");
        public void SetActivation(int modId, int fileId, string? folderName, PendingDownloadActivationKind? kind) =>
            throw new NotSupportedException("Not exercised by these tests.");
        public void SetActivation(int modId, int fileId, IReadOnlyList<PendingDownloadActivatedItem> items, IReadOnlyList<string> requirements) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakeNexusWatchlistStore : INexusWatchlistStore
    {
        public IReadOnlyList<NexusWatchlistEntry> Entries => [];
        public void Add(NexusWatchlistEntry entry) => throw new NotSupportedException("Not exercised by these tests.");
        public void Remove(int nexusId) => throw new NotSupportedException("Not exercised by these tests.");
        public void UpdateName(int nexusId, string name) => throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakeDaedalusClient : IDaedalusCatalogClient
    {
        public Task<IReadOnlyList<CatalogEntry>> FetchAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakeJimk72Client : IJimk72CatalogClient
    {
        public Task<IReadOnlyList<CatalogEntry>> FetchAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakeGitHubRepoDateClient : IGitHubRepoDateClient
    {
        public Task<IReadOnlyDictionary<(string Owner, string Repo), DateTimeOffset>> FetchPushedDatesAsync(
            IReadOnlyCollection<(string Owner, string Repo)> repos, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakeUe4ssModRepository : IUe4ssModRepository
    {
        public IReadOnlyList<string> GetAll() => [];
        public string Import(string zipFilePath, IReadOnlyCollection<string>? namesAlreadyInUse = null) =>
            throw new NotSupportedException("Not exercised by these tests.");
        public string ImportFromFolder(string sourceFolder, string fallbackName, IReadOnlyCollection<string>? namesAlreadyInUse = null) =>
            throw new NotSupportedException("Not exercised by these tests.");
        public void Delete(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public string GetFolderPath(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
        public IReadOnlyList<string> ListInstalledInGame(string gameModsFolderPath) => [];
        public string AdoptFromGame(string gameModsFolderPath, string folderName, IReadOnlyCollection<string>? namesAlreadyInUse = null) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakeUe4ssModMetaStore : IUe4ssModMetaStore
    {
        public IReadOnlyList<string> ListFolderNames() => [];
        public Ue4ssModMeta Load(string folderName) => new();
        public void Save(string folderName, Ue4ssModMeta meta) => throw new NotSupportedException("Not exercised by these tests.");
        public void Delete(string folderName) => throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakePrebuiltPakImporter : IPrebuiltPakImporter
    {
        public Task<LibraryEntry> ImportAsync(
            string pakFilePath, string dataFolder, string? unrealPakExePath,
            string? source = null, int? nexusModId = null, string? catalogEntryId = null,
            string? name = null, string? author = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class FakeDialogService : IDialogService
    {
        public bool Confirm(string message, string title, ThemedConfirmSeverity severity) =>
            throw new NotSupportedException("Not exercised by these tests.");
        public RenamePromptResult PromptRename(
            string currentName,
            string description = "",
            string? resetValue = null,
            string resetLabel = "Reset to default",
            string resetTooltip = "",
            string title = "Rename mod",
            string fieldLabel = "Display name") =>
            throw new NotSupportedException("Not exercised by these tests.");
    }
}
