using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.PakIO.Compare;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>Compare paks: what the window says for each outcome, its file picker, and that the paths are locked while a compare runs.</summary>
public sealed class PakCompareViewModelTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));

    public PakCompareViewModelTests() => Directory.CreateDirectory(_tempRoot);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (Exception)
        {
            // Best-effort cleanup only.
        }
    }

    private readonly FakeCompareService _service = new();
    private readonly FakeSettingsService _settings = new();
    private readonly FakeDialogService _dialogs = new();

    private string MakePak(string name)
    {
        var path = Path.Combine(_tempRoot, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    /// <summary>Two existing paks filled in and UnrealPak set: ready to compare.</summary>
    private PakCompareViewModel Ready()
    {
        _settings.Current.UnrealPakExePath = @"C:\fake\UnrealPak.exe";
        var vm = new PakCompareViewModel(_service, _settings, MakePak("first.pak"), _dialogs);
        vm.SecondPakPath = MakePak("second.pak");
        return vm;
    }

    [Fact]
    public void TheStagedPak_IsFilledInAsTheFirst_OnlyWhenItExists()
    {
        var staged = MakePak("staged.pak");

        Assert.Equal(staged, new PakCompareViewModel(_service, _settings, staged, _dialogs).FirstPakPath);
        Assert.Equal("", new PakCompareViewModel(_service, _settings, Path.Combine(_tempRoot, "none.pak"), _dialogs).FirstPakPath);
    }

    [Fact]
    public async Task Compare_APakThatIsntThere_AsksForTwoPaks()
    {
        var vm = Ready();
        vm.SecondPakPath = Path.Combine(_tempRoot, "gone.pak");

        await vm.CompareCommand.ExecuteAsync(null);

        Assert.Equal("Pick two existing .pak files to compare.", vm.SummaryMessage);
        Assert.Equal(0, _service.CallCount);
    }

    [Fact]
    public async Task Compare_WithoutUnrealPak_PointsAtSettings()
    {
        var vm = Ready();
        _settings.Current.UnrealPakExePath = null;

        await vm.CompareCommand.ExecuteAsync(null);

        Assert.Equal("Set UnrealPak.exe path in Settings first.", vm.SummaryMessage);
        Assert.Equal(0, _service.CallCount);
    }

    [Fact]
    public async Task Compare_TheServiceFails_SaysWhy()
    {
        var vm = Ready();
        _service.Error = new IOException("UnrealPak exited with code 1");

        await vm.CompareCommand.ExecuteAsync(null);

        Assert.Equal("Compare failed: UnrealPak exited with code 1", vm.SummaryMessage);
        Assert.False(vm.IsComparing);
    }

    [Fact]
    public async Task Compare_TwoEqualPaks_SaysTheyAreTheSame()
    {
        var vm = Ready();

        await vm.CompareCommand.ExecuteAsync(null);

        Assert.StartsWith("The two paks are the same", vm.SummaryMessage);
        Assert.Contains("(3 file(s) in the first pak, 3 in the second.)", vm.SummaryMessage);
        Assert.Empty(vm.ChangedFiles);
        Assert.False(vm.HasAssetDifferences);
    }

    [Fact]
    public async Task Compare_AnOtherFileDiffers_IsListed_AndCounted()
    {
        var vm = Ready();
        _service.Result = new PakCompareResult([], [new PakAssetDifference("Icarus/Content/UI/Icon.uasset", PakAssetDifferenceKind.OnlyInSecond)], 3, 4);

        await vm.CompareCommand.ExecuteAsync(null);

        Assert.StartsWith("0 game table file(s) differ, 1 other file(s) differ.", vm.SummaryMessage);
        Assert.True(vm.HasAssetDifferences);
        Assert.Equal("Icarus/Content/UI/Icon.uasset — only in the second pak", Assert.Single(vm.AssetDifferences));
    }

    [Fact]
    public void Browse_UsesThePagesFilePicker()
    {
        var vm = Ready();
        var picked = MakePak("picked.pak");
        string? title = null;
        _dialogs.OnPickFile = t =>
        {
            title = t;
            return picked;
        };

        vm.BrowseFirstPakCommand.Execute(null);

        Assert.Equal(picked, vm.FirstPakPath);
        Assert.Equal("Pick a pak", title);

        _dialogs.OnPickFile = _ => null;
        vm.BrowseSecondPakCommand.Execute(null);
        Assert.Equal(Path.Combine(_tempRoot, "second.pak"), vm.SecondPakPath);
    }

    /// <summary>The paths used to stay editable during a compare, so the results could belong to paths no longer shown.</summary>
    [Fact]
    public async Task WhileAComparisonRuns_ThePathsAreLocked()
    {
        var vm = Ready();
        var hold = new TaskCompletionSource<PakCompareResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.Hold = hold.Task;
        Assert.True(vm.IsNotComparing);

        var compare = vm.CompareCommand.ExecuteAsync(null);
        Assert.False(vm.IsNotComparing);

        hold.SetResult(new PakCompareResult([], [], 3, 3));
        await compare;
        Assert.True(vm.IsNotComparing);
    }

    private sealed class FakeCompareService : IPakCompareService
    {
        public PakCompareResult Result { get; set; } = new([], [], 3, 3);

        public Exception? Error { get; set; }

        /// <summary>When set, the compare waits for it.</summary>
        public Task<PakCompareResult>? Hold { get; set; }

        public int CallCount { get; private set; }

        public Task<PakCompareResult> CompareAsync(string unrealPakExePath, string firstPakPath, string secondPakPath, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Error is not null ? Task.FromException<PakCompareResult>(Error) : Hold ?? Task.FromResult(Result);
        }
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();

        public bool Save() => true;
    }

    private sealed class FakeDialogService : IDialogService
    {
        public Func<string, string?> OnPickFile { get; set; } = _ => null;

        public bool Confirm(string message, string title, ThemedConfirmSeverity severity) =>
            throw new InvalidOperationException($"Unexpected question: {title}");

        public RenamePromptResult PromptRename(
            string currentName, string description = "", string? resetValue = null, string resetLabel = "", string resetTooltip = "",
            string title = "", string fieldLabel = "") => throw new NotSupportedException("Not exercised by these tests.");

        public string? PickFile(string title, string filter, string? initialDirectory = null) => OnPickFile(title);
    }
}
