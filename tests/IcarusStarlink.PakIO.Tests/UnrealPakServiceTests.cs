using IcarusStarlink.PakIO.Pak;

namespace IcarusStarlink.PakIO.Tests;

public class UnrealPakServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _contentPath;
    private readonly string _unrealPakExePath;
    private readonly string _outputDirectory;

    public UnrealPakServiceTests()
    {
        _contentPath = Path.Combine(_tempDir, "Content");
        _unrealPakExePath = Path.Combine(_tempDir, "UnrealPak.exe");
        _outputDirectory = Path.Combine(_tempDir, "Data");

        Directory.CreateDirectory(Path.Combine(_contentPath, "Data"));
        File.WriteAllText(Path.Combine(_contentPath, "Data", "data.pak"), "fake pak bytes");
        File.WriteAllText(_unrealPakExePath, "fake exe bytes");
    }

    private static string DataTableJson(params (string Name, int Value)[] rows)
    {
        var rowsJson = string.Join(",", rows.Select(r => $$"""{"Name":"{{r.Name}}","Amount":{{r.Value}}}"""));
        return $$"""{"RowStruct":"/Script/Icarus.Whatever","Defaults":{},"Rows":[{{rowsJson}}]}""";
    }

    private sealed class FakeProcessRunner(ProcessRunResult result, IReadOnlyDictionary<string, string> filesToCreate) : IProcessRunner
    {
        public string? LastFileName { get; private set; }
        public IReadOnlyList<string>? LastArguments { get; private set; }

        /// <summary>SynchronizationContext.Current when UnrealPak was started.</summary>
        public SynchronizationContext? ContextAtRun { get; private set; }

        /// <summary>Runs inside the fake UnrealPak with its arguments, before it writes anything.</summary>
        public Action<IReadOnlyList<string>>? DuringRun { get; set; }

        public Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            LastFileName = fileName;
            LastArguments = arguments;
            ContextAtRun = SynchronizationContext.Current;
            DuringRun?.Invoke(arguments);

            // Stands in for what a real UnrealPak.exe -Extract run does to its target directory
            // (the 3rd argument — a temp directory UnrealPakService itself picks, not necessarily
            // the final outputDirectory) — the fake runner never actually shells out, so nothing
            // else would populate it.
            if (arguments.Count < 3 || arguments[1] != "-Extract")
            {
                return Task.FromResult(result);
            }

            var extractDirectory = arguments[2];
            foreach (var (relativePath, content) in filesToCreate)
            {
                var path = Path.Combine(extractDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
            }

            return Task.FromResult(result);
        }
    }

    private static FakeProcessRunner Runner(ProcessRunResult result, params (string RelativePath, string Content)[] files) =>
        new(result, files.ToDictionary(f => f.RelativePath, f => f.Content));

    [Fact]
    public async Task ExtractDataPakAsync_MissingExePath_ThrowsFileNotFoundException()
    {
        var service = new UnrealPakService(Runner(new ProcessRunResult(0, "", "")));

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.ExtractDataPakAsync(Path.Combine(_tempDir, "NoSuchExe.exe"), _contentPath, _outputDirectory, previousUpdateAt: null));
    }

    [Fact]
    public async Task ExtractDataPakAsync_MissingDataPak_ThrowsFileNotFoundException()
    {
        var service = new UnrealPakService(Runner(new ProcessRunResult(0, "", "")));
        var contentPathWithNoDataPak = Path.Combine(_tempDir, "EmptyContent");
        Directory.CreateDirectory(contentPathWithNoDataPak);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.ExtractDataPakAsync(_unrealPakExePath, contentPathWithNoDataPak, _outputDirectory, previousUpdateAt: null));
    }

    [Fact]
    public async Task ExtractDataPakAsync_Success_ReturnsExtractedFileCount()
    {
        var runner = Runner(new ProcessRunResult(0, "ok", ""), ("Crafting/D_ProcessorRecipes.json", "{}"), ("Traits/D_Fuel.json", "{}"));
        var service = new UnrealPakService(runner);

        var result = await service.ExtractDataPakAsync(_unrealPakExePath, _contentPath, _outputDirectory, previousUpdateAt: null);

        Assert.Equal(2, result.ExtractedFileCount);
        Assert.Null(result.ChangeReport);
    }

    [Fact]
    public async Task ExtractDataPakAsync_PassesDataPakPathAndExtractFlag()
    {
        var runner = Runner(new ProcessRunResult(0, "", ""));
        var service = new UnrealPakService(runner);

        await service.ExtractDataPakAsync(_unrealPakExePath, _contentPath, _outputDirectory, previousUpdateAt: null);

        Assert.Equal(_unrealPakExePath, runner.LastFileName);
        Assert.Equal(Path.Combine(_contentPath, "Data", "data.pak"), runner.LastArguments![0]);
        Assert.Equal("-Extract", runner.LastArguments[1]);
    }

    [Fact]
    public async Task ExtractDataPakAsync_ScratchDirectoryIsASiblingOfOutputDirectory_NotUnderSystemTemp()
    {
        // Real bug, found live: the scratch directory used to live under Path.GetTempPath()
        // (always the OS drive, typically C:), then Directory.Move(scratch, outputDirectory) —
        // which .NET refuses to do across volumes — into outputDirectory (wherever this portable
        // app's own install actually is). A user who installed on a non-C: drive (very often the
        // same drive as their game) hit a real "Refresh game data" failure every time. The fix:
        // the scratch directory is a sibling of outputDirectory, guaranteeing the same volume
        // regardless of where either this app or Path.GetTempPath() happen to live.
        var runner = Runner(new ProcessRunResult(0, "", ""));
        var service = new UnrealPakService(runner);

        await service.ExtractDataPakAsync(_unrealPakExePath, _contentPath, _outputDirectory, previousUpdateAt: null);

        var scratchDirectory = runner.LastArguments![2];
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(_outputDirectory)), Path.GetDirectoryName(scratchDirectory));
    }

    [Fact]
    public async Task ExtractDataPakAsync_NonZeroExitCode_ThrowsWithStandardErrorInMessage()
    {
        var runner = Runner(new ProcessRunResult(1, "", "corrupt pak header"));
        var service = new UnrealPakService(runner);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExtractDataPakAsync(_unrealPakExePath, _contentPath, _outputDirectory, previousUpdateAt: null));
        Assert.Contains("corrupt pak header", exception.Message);
    }

    /// <summary>
    /// Regression: the old delete-then-move swap could lose BOTH extractions. A recursive delete
    /// that hit one locked file left the old Data folder half-deleted, and the finally then removed
    /// the complete new extraction. The rename-based swap must leave the old data fully intact
    /// when it can't be swapped out.
    /// </summary>
    [Fact]
    public async Task ExtractDataPakAsync_OldExtractionHasALockedFile_OldDataStaysFullyIntact()
    {
        Directory.CreateDirectory(_outputDirectory);
        var keptFile = Path.Combine(_outputDirectory, "Crafting", "D_Keep.json");
        var lockedFile = Path.Combine(_outputDirectory, "Traits", "D_Locked.json");
        Directory.CreateDirectory(Path.GetDirectoryName(keptFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(lockedFile)!);
        File.WriteAllText(keptFile, "{}");
        File.WriteAllText(lockedFile, "{}");

        var runner = Runner(new ProcessRunResult(0, "", ""), ("Crafting/D_New.json", "{}"));
        var service = new UnrealPakService(runner);

        using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() =>
                service.ExtractDataPakAsync(_unrealPakExePath, _contentPath, _outputDirectory, previousUpdateAt: null));
        }

        Assert.True(File.Exists(keptFile), "a file the delete-then-move approach would have removed before hitting the lock");
        Assert.True(File.Exists(lockedFile));
        Assert.False(File.Exists(Path.Combine(_outputDirectory, "Crafting", "D_New.json")));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(Path.GetFullPath(_outputDirectory))!, ".DataExtract_*"));
    }

    /// <summary>
    /// Every caller is a UI command; diffing the old and new Data folders and swapping them used to
    /// run on the UI thread once UnrealPak finished, freezing the window for seconds.
    /// </summary>
    [Fact]
    public async Task ExtractDataPakAsync_RunsOffTheCallersThread()
    {
        var runner = Runner(new ProcessRunResult(0, "", ""), ("Crafting/D_ProcessorRecipes.json", "{}"));
        var service = new UnrealPakService(runner);
        var marker = new MarkerSynchronizationContext();

        await marker.Run(() => service.ExtractDataPakAsync(_unrealPakExePath, _contentPath, _outputDirectory, previousUpdateAt: null));

        Assert.NotSame(marker, runner.ContextAtRun);
    }

    /// <summary>
    /// Regression: a refresh killed partway (Task Manager, a power cut) left its hidden
    /// ".DataExtract_&lt;guid&gt;" folder next to Data for good; only ".DataExtract_old_*" was swept.
    /// </summary>
    [Fact]
    public async Task ExtractDataPakAsync_LeftoverExtractionFromAnInterruptedRun_IsSwept()
    {
        var leftover = Path.Combine(_tempDir, ".DataExtract_0123456789abcdef0123456789abcdef");
        Directory.CreateDirectory(Path.Combine(leftover, "Traits"));
        File.WriteAllText(Path.Combine(leftover, "Traits", "D_Half.json"), "{}");
        var runner = Runner(new ProcessRunResult(0, "", ""), ("Traits/D_Fuel.json", "{}"));
        var currentRunExistedDuringRun = false;
        runner.DuringRun = arguments => currentRunExistedDuringRun = Directory.Exists(arguments[2]);
        var service = new UnrealPakService(runner);

        var result = await service.ExtractDataPakAsync(_unrealPakExePath, _contentPath, _outputDirectory, previousUpdateAt: null);

        Assert.False(Directory.Exists(leftover));
        Assert.True(currentRunExistedDuringRun, "the sweep must skip this run's own extraction folder");
        Assert.Equal(1, result.ExtractedFileCount);
    }

    /// <summary>ProcessRunner joins stdout with CRLF; the summary capture must stop at the '\r' or TrimEnd('.') never fires and a stray CR rides into the rebuild report.</summary>
    [Fact]
    public async Task VerifyPakAsync_SummaryCapturedFromCrLfOutput_HasNoTrailingCarriageReturnOrPeriod()
    {
        var pakPath = Path.Combine(Path.GetDirectoryName(_outputDirectory)!, "some.pak");
        Directory.CreateDirectory(Path.GetDirectoryName(pakPath)!);
        File.WriteAllText(pakPath, "pak");
        var runner = Runner(new ProcessRunResult(0, "LogPakFile: Display: Pak file \"C:\\x\\some.pak\" healthy, 3 files checked.\r\n", ""));
        var service = new UnrealPakService(runner);

        var result = await service.VerifyPakAsync(_unrealPakExePath, pakPath);

        Assert.True(result.IsHealthy);
        Assert.Equal("healthy, 3 files checked", result.Message);
    }

    [Fact]
    public async Task ExtractDataPakAsync_StaleFileFromPreviousRun_IsRemoved()
    {
        Directory.CreateDirectory(_outputDirectory);
        var staleFile = Path.Combine(_outputDirectory, "OldTable", "D_Removed.json");
        Directory.CreateDirectory(Path.GetDirectoryName(staleFile)!);
        File.WriteAllText(staleFile, "stale");

        var runner = Runner(new ProcessRunResult(0, "", ""), ("NewTable/D_Current.json", "{}"));
        var service = new UnrealPakService(runner);

        await service.ExtractDataPakAsync(_unrealPakExePath, _contentPath, _outputDirectory, previousUpdateAt: null);

        Assert.False(File.Exists(staleFile));
        Assert.True(File.Exists(Path.Combine(_outputDirectory, "NewTable", "D_Current.json")));
    }

    [Fact]
    public async Task ExtractDataPakAsync_NoPreviousUpdateAt_NeverProducesAChangeReport()
    {
        // A previous outputDirectory existing isn't enough on its own — see the interface's own
        // doc comment on why previousUpdateAt (not folder existence) is the trigger.
        Directory.CreateDirectory(_outputDirectory);
        File.WriteAllText(Path.Combine(_outputDirectory, "Old.json"), DataTableJson(("A", 1)));

        var runner = Runner(new ProcessRunResult(0, "", ""), ("Old.json", DataTableJson(("A", 2))));
        var service = new UnrealPakService(runner);

        var result = await service.ExtractDataPakAsync(_unrealPakExePath, _contentPath, _outputDirectory, previousUpdateAt: null);

        Assert.Null(result.ChangeReport);
    }

    [Fact]
    public async Task ExtractDataPakAsync_PreviousUpdateAtSetAndPriorDataExists_ProducesChangeReport()
    {
        Directory.CreateDirectory(_outputDirectory);
        File.WriteAllText(Path.Combine(_outputDirectory, "Table.json"), DataTableJson(("A", 1)));
        var previousUpdateAt = DateTimeOffset.UtcNow.AddDays(-7);

        var runner = Runner(new ProcessRunResult(0, "", ""), ("Table.json", DataTableJson(("A", 2))));
        var service = new UnrealPakService(runner);

        var result = await service.ExtractDataPakAsync(_unrealPakExePath, _contentPath, _outputDirectory, previousUpdateAt);

        Assert.NotNull(result.ChangeReport);
        var report = result.ChangeReport!;
        Assert.Equal(previousUpdateAt, report.PreviousUpdateAt);
        var changedFile = Assert.Single(report.ChangedFiles);
        Assert.Equal("Table.json", changedFile.RelativePath);
        var change = Assert.Single(changedFile.FieldChanges);
        Assert.Equal("Amount", change.FieldName);
    }

    /// <summary>
    /// The mount point parsed from REAL UnrealPak -List output (captured from Nexus paks). The
    /// pattern was missing its backslash ("S+" for "\S+") from the day it was written, so it never
    /// matched anything real; every test faked TryGetMountPointAsync and none caught it.
    /// </summary>
    [Theory]
    [InlineData("LogPakFile: Display: Mount point ../../../Icarus/Content/data/Traits/|LogPakFile: Display: \"D_Itemable.json\" offset: 0", "../../../Icarus/Content/data/Traits/")]
    [InlineData("LogInit: Display: Loading text-based GConfig....|LogPakFile: Display: Mount point ../../../|LogPakFile: Display: 2 files (963 bytes)", "../../../")]
    public async Task TryGetMountPointAsync_ParsesRealUnrealPakOutput(string output, string expected)
    {
        var pak = Path.Combine(_tempDir, "x_P.pak");
        File.WriteAllText(pak, "pak");
        var service = new UnrealPakService(new CapturingProcessRunner(new ProcessRunResult(0, output.Replace("|", Environment.NewLine), "")));

        Assert.Equal(expected, await service.TryGetMountPointAsync(_unrealPakExePath, pak));
    }

    private sealed class CapturingProcessRunner(ProcessRunResult result) : IProcessRunner
    {
        public string? LastFileName { get; private set; }
        public IReadOnlyList<string>? LastArguments { get; private set; }

        // The real method deletes its own response file in a finally block before returning, so
        // the only window to see its content is here, while RunAsync is still executing.
        public string? CapturedResponseFileContent { get; private set; }

        public Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            LastFileName = fileName;
            LastArguments = arguments;

            var createArg = arguments.FirstOrDefault(a => a.StartsWith("-Create=", StringComparison.Ordinal));
            if (createArg is not null)
            {
                var responseFilePath = createArg["-Create=".Length..];
                CapturedResponseFileContent = File.Exists(responseFilePath) ? File.ReadAllText(responseFilePath) : null;
            }

            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task CreatePakAsync_MissingExePath_ThrowsFileNotFoundException()
    {
        var stagingDirectory = Path.Combine(_tempDir, "Staging");
        Directory.CreateDirectory(Path.Combine(stagingDirectory, "data"));
        File.WriteAllText(Path.Combine(stagingDirectory, "data", "Test.json"), "{}");
        var service = new UnrealPakService(new CapturingProcessRunner(new ProcessRunResult(0, "", "")));

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.CreatePakAsync(Path.Combine(_tempDir, "NoSuchExe.exe"), stagingDirectory, Path.Combine(_tempDir, "Out_P.pak")));
    }

    [Fact]
    public async Task CreatePakAsync_EmptyStagingDirectory_ThrowsInvalidOperationException()
    {
        var stagingDirectory = Path.Combine(_tempDir, "EmptyStaging");
        Directory.CreateDirectory(stagingDirectory);
        var service = new UnrealPakService(new CapturingProcessRunner(new ProcessRunResult(0, "", "")));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreatePakAsync(_unrealPakExePath, stagingDirectory, Path.Combine(_tempDir, "Out_P.pak")));
    }

    [Fact]
    public async Task CreatePakAsync_NonZeroExitCode_ThrowsWithStandardErrorInMessage()
    {
        var stagingDirectory = Path.Combine(_tempDir, "Staging");
        Directory.CreateDirectory(Path.Combine(stagingDirectory, "data"));
        File.WriteAllText(Path.Combine(stagingDirectory, "data", "Test.json"), "{}");
        var service = new UnrealPakService(new CapturingProcessRunner(new ProcessRunResult(1, "", "index mismatch")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreatePakAsync(_unrealPakExePath, stagingDirectory, Path.Combine(_tempDir, "Out_P.pak")));
        Assert.Contains("index mismatch", exception.Message);
    }

    [Fact]
    public async Task CreatePakAsync_Success_ReturnsPackedFileCount()
    {
        var stagingDirectory = Path.Combine(_tempDir, "Staging");
        Directory.CreateDirectory(Path.Combine(stagingDirectory, "data"));
        Directory.CreateDirectory(Path.Combine(stagingDirectory, "BP"));
        File.WriteAllText(Path.Combine(stagingDirectory, "data", "Test.json"), "{}");
        File.WriteAllText(Path.Combine(stagingDirectory, "BP", "Thing.uasset"), "binary");
        var runner = new CapturingProcessRunner(new ProcessRunResult(0, "", ""));
        var service = new UnrealPakService(runner);

        var count = await service.CreatePakAsync(_unrealPakExePath, stagingDirectory, Path.Combine(_tempDir, "Out_P.pak"));

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task CreatePakAsync_ResponseFileBakesGameMountPointOntoEveryLine()
    {
        // Verified against the real UnrealPak.exe binary: a response file whose destination
        // paths don't share a common prefix crashes it outright, and folder-mode -Create (no
        // response file) auto-infers the wrong mount point (this dev machine's own absolute
        // staging path) instead of the real game-relative one. Baking the same mount point onto
        // every line is the one technique confirmed to produce a pak that both builds without
        // crashing and mounts where the game actually expects it.
        var stagingDirectory = Path.Combine(_tempDir, "Staging");
        Directory.CreateDirectory(Path.Combine(stagingDirectory, "data", "Crafting"));
        Directory.CreateDirectory(Path.Combine(stagingDirectory, "BP"));
        File.WriteAllText(Path.Combine(stagingDirectory, "data", "Crafting", "D_ProcessorRecipes.json"), "{}");
        File.WriteAllText(Path.Combine(stagingDirectory, "BP", "Thing.uasset"), "binary");
        var runner = new CapturingProcessRunner(new ProcessRunResult(0, "", ""));
        var service = new UnrealPakService(runner);

        await service.CreatePakAsync(_unrealPakExePath, stagingDirectory, Path.Combine(_tempDir, "Out_P.pak"));

        Assert.NotNull(runner.CapturedResponseFileContent);
        var lines = runner.CapturedResponseFileContent!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains(lines, l => l.Contains("\"../../../Icarus/Content/data/Crafting/D_ProcessorRecipes.json\""));
        Assert.Contains(lines, l => l.Contains("\"../../../Icarus/Content/BP/Thing.uasset\""));
    }

    [Fact]
    public async Task CreatePakAsync_PassesOutputPakPathAndCreateFlag()
    {
        var stagingDirectory = Path.Combine(_tempDir, "Staging");
        Directory.CreateDirectory(stagingDirectory);
        File.WriteAllText(Path.Combine(stagingDirectory, "Test.json"), "{}");
        var runner = new CapturingProcessRunner(new ProcessRunResult(0, "", ""));
        var service = new UnrealPakService(runner);
        var outputPakPath = Path.Combine(_tempDir, "Out_P.pak");

        await service.CreatePakAsync(_unrealPakExePath, stagingDirectory, outputPakPath);

        Assert.Equal(_unrealPakExePath, runner.LastFileName);
        Assert.Equal(outputPakPath, runner.LastArguments![0]);
        Assert.StartsWith("-Create=", runner.LastArguments[1]);
    }

    [Fact]
    public async Task CreatePakAsync_PassesCompressFlag()
    {
        // Confirmed live against the real bundled UnrealPak.exe: without -compress every entry is
        // stored raw, and this app's output is almost entirely highly-compressible JSON DataTable
        // files (~95% smaller in a real 30-file test sample) — a full extract-and-diff round trip
        // confirmed the compressed output is still byte-identical, so this is free space savings.
        var stagingDirectory = Path.Combine(_tempDir, "Staging");
        Directory.CreateDirectory(stagingDirectory);
        File.WriteAllText(Path.Combine(stagingDirectory, "Test.json"), "{}");
        var runner = new CapturingProcessRunner(new ProcessRunResult(0, "", ""));
        var service = new UnrealPakService(runner);

        await service.CreatePakAsync(_unrealPakExePath, stagingDirectory, Path.Combine(_tempDir, "Out_P.pak"));

        Assert.Contains("-compress", runner.LastArguments!);
    }

    // Real output captured by running -List against a real installed pak.
    private const string RealisticListOutput = """
        LogInit: Display: Loading text-based GConfig....
        LogPakFile: Display: Using command line for crypto configuration
        LogPakFile: Display: Mount point ../../../Icarus/Content/
        LogPakFile: Display: "Rada_CheatMenu/BP/BP_CheatMenuDummy.uasset" offset: 0, size: 9662 bytes, sha1: 3FE5A7055CEC10EA2198733BFF5C3C7BC4663A71, compression: Zlib.
        LogPakFile: Display: "Rada_CheatMenu/E_CheatCategory.uasset" offset: 14978, size: 922 bytes, sha1: 828E50FFA55669064C48DA8CC19B8DECAEC9BF27, compression: None.
        LogPakFile: Display: "UI/Components/UMG_RevisionNumber.uexp" offset: 585314, size: 881 bytes, sha1: 42FD3B56E56771A4BD99C420AAAF0EEB91585F98, compression: Zlib.
        LogPakFile: Display: 3 files (11465 bytes), (0 filtered bytes).
        LogPakFile: Display: Unreal pak executed in 0.005162 seconds
        """;

    [Fact]
    public async Task ListPakContentsAsync_RealisticOutput_ParsesEveryEntryPathAndSkipsSummaryLines()
    {
        var pakPath = Path.Combine(_tempDir, "Test_P.pak");
        File.WriteAllText(pakPath, "fake pak bytes");
        var service = new UnrealPakService(new CapturingProcessRunner(new ProcessRunResult(0, RealisticListOutput, "")));

        var paths = await service.ListPakContentsAsync(_unrealPakExePath, pakPath);

        Assert.Equal(
            ["Rada_CheatMenu/BP/BP_CheatMenuDummy.uasset", "Rada_CheatMenu/E_CheatCategory.uasset", "UI/Components/UMG_RevisionNumber.uexp"],
            paths);
    }

    [Fact]
    public async Task ListPakContentsAsync_PassesPakPathAndListFlag()
    {
        var pakPath = Path.Combine(_tempDir, "Test_P.pak");
        File.WriteAllText(pakPath, "fake pak bytes");
        var runner = new CapturingProcessRunner(new ProcessRunResult(0, "", ""));
        var service = new UnrealPakService(runner);

        await service.ListPakContentsAsync(_unrealPakExePath, pakPath);

        Assert.Equal(_unrealPakExePath, runner.LastFileName);
        Assert.Equal([pakPath, "-List"], runner.LastArguments);
    }

    [Fact]
    public async Task ListPakContentsAsync_MissingExePath_ThrowsFileNotFoundException()
    {
        var pakPath = Path.Combine(_tempDir, "Test_P.pak");
        File.WriteAllText(pakPath, "fake pak bytes");
        var service = new UnrealPakService(new CapturingProcessRunner(new ProcessRunResult(0, "", "")));

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.ListPakContentsAsync(Path.Combine(_tempDir, "NoSuchExe.exe"), pakPath));
    }

    [Fact]
    public async Task ListPakContentsAsync_MissingPakFile_ThrowsFileNotFoundException()
    {
        var service = new UnrealPakService(new CapturingProcessRunner(new ProcessRunResult(0, "", "")));

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.ListPakContentsAsync(_unrealPakExePath, Path.Combine(_tempDir, "NoSuchPak.pak")));
    }

    [Fact]
    public async Task ListPakContentsAsync_NonZeroExitCode_ThrowsWithStandardErrorInMessage()
    {
        var pakPath = Path.Combine(_tempDir, "Test_P.pak");
        File.WriteAllText(pakPath, "fake pak bytes");
        var service = new UnrealPakService(new CapturingProcessRunner(new ProcessRunResult(1, "", "corrupt pak")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ListPakContentsAsync(_unrealPakExePath, pakPath));
        Assert.Contains("corrupt pak", ex.Message);
    }

    // Real output captured by running -Extract against a real installed pak.
    private const string RealisticExtractOutput = """
        LogInit: Display: Loading text-based GConfig....
        LogPakFile: Display: Using command line for crypto configuration
        LogPakFile: Display: Mount point ../../../Icarus/Content/
        LogPakFile: Display: Extracted "Rada_CheatMenu/BP/BP_CheatMenuDummy.uasset" to "C:\Temp\out/Rada_CheatMenu/BP/BP_CheatMenuDummy.uasset" Offset 0.
        LogPakFile: Display: Extracted "UI/Components/UMG_RevisionNumber.uexp" to "C:\Temp\out/UI/Components/UMG_RevisionNumber.uexp" Offset 585314.
        LogPakFile: Display: Unreal pak executed in 0.179808 seconds
        """;

    /// <summary>Answers -List with a listing and -Extract with an extract log, and records every argument list.</summary>
    private sealed class ScriptedProcessRunner(string listOutput, ProcessRunResult? extractResult = null) : IProcessRunner
    {
        public string? LastFileName { get; private set; }
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            LastFileName = fileName;
            Calls.Add([.. arguments]);
            return Task.FromResult(arguments.Contains("-List")
                ? new ProcessRunResult(0, listOutput, "")
                : extractResult ?? new ProcessRunResult(0, RealisticExtractOutput, ""));
        }
    }

    /// <summary>-List output in UnrealPak 4.27's shape (CRLF lines, as ProcessRunner joins them) for these entry names.</summary>
    private static string Listing(params string[] entryNames) => string.Join("\r\n",
    [
        "LogInit: Display: Loading text-based GConfig....",
        "LogPakFile: Display: Mount point ../../../Icarus/Content/",
        .. entryNames.Select(name => $"LogPakFile: Display: \"{name}\" offset: 0, size: 4 bytes, sha1: 3FE5A7055CEC10EA2198733BFF5C3C7BC4663A71, compression: None."),
        $"LogPakFile: Display: {entryNames.Length} files ({entryNames.Length * 4} bytes), (0 filtered bytes).",
        "LogPakFile: Display: Unreal pak executed in 0.005162 seconds",
    ]);

    private static readonly string SafeListing = Listing("Rada_CheatMenu/BP/BP_CheatMenuDummy.uasset", "UI/Components/UMG_RevisionNumber.uexp");

    private string WriteTestPak()
    {
        var pakPath = Path.Combine(_tempDir, "Test_P.pak");
        File.WriteAllText(pakPath, "fake pak bytes");
        return pakPath;
    }

    /// <summary>
    /// UnrealPak -Extract writes each entry to "&lt;folder&gt;/&lt;entry name&gt;" and the names come from
    /// the pak's own index. A crafted pak used to be unpacked with no check, so an entry named
    /// "../../../../Roaming/…/Startup/x.cmd" landed in the Startup folder. Now nothing is unpacked.
    /// </summary>
    [Theory]
    [InlineData("../../../../Roaming/Microsoft/Windows/Start Menu/Programs/Startup/x.cmd")]
    [InlineData("C:/x.txt")]
    [InlineData("a/../../b.txt")]
    [InlineData("CON.txt")]
    [InlineData("x.txt:ads")]
    public async Task ExtractPakAsync_ListedEntryWouldLandOutsideTheFolder_RefusesBeforeExtracting(string entryName)
    {
        var pakPath = WriteTestPak();
        var runner = new ScriptedProcessRunner(Listing(entryName));
        var service = new UnrealPakService(runner);

        var ex = await Assert.ThrowsAsync<UnsafePakException>(() =>
            service.ExtractPakAsync(_unrealPakExePath, pakPath, Path.Combine(_tempDir, "Extracted")));

        Assert.Contains("Test_P.pak", ex.Message);
        Assert.DoesNotContain(runner.Calls, c => c.Contains("-Extract"));
        Assert.False(Directory.Exists(Path.Combine(_tempDir, "Extracted")));
    }

    /// <summary>A listing Starlink can't check line by line (a name that breaks its line, a count that doesn't add up, no summary) is refused too.</summary>
    [Theory]
    [InlineData("LogPakFile: Display: \"a\"b.uasset\" offset: 0, size: 4 bytes, sha1: 00, compression: None.\r\nLogPakFile: Display: 1 files (4 bytes), (0 filtered bytes).")]
    [InlineData("LogPakFile: Display: \"a.uasset\" offset: 0, size: 4 bytes, sha1: 00, compression: None.\r\nLogPakFile: Display: 2 files (8 bytes), (0 filtered bytes).")]
    [InlineData("LogPakFile: Display: \"a.uasset\" offset: 0, size: 4 bytes, sha1: 00, compression: None.\r\nLogPakFile: Display: Unreal pak executed in 0.01 seconds")]
    public async Task ExtractPakAsync_ListingStarlinkCantCheck_RefusesBeforeExtracting(string listOutput)
    {
        var pakPath = WriteTestPak();
        var runner = new ScriptedProcessRunner(listOutput);
        var service = new UnrealPakService(runner);

        await Assert.ThrowsAsync<UnsafePakException>(() =>
            service.ExtractPakAsync(_unrealPakExePath, pakPath, Path.Combine(_tempDir, "Extracted")));

        Assert.DoesNotContain(runner.Calls, c => c.Contains("-Extract"));
    }

    [Fact]
    public async Task ExtractPakAsync_SafeListing_ListsThenExtracts()
    {
        var pakPath = WriteTestPak();
        var outputDirectory = Path.Combine(_tempDir, "Extracted");
        var runner = new ScriptedProcessRunner(SafeListing);
        var service = new UnrealPakService(runner);

        var count = await service.ExtractPakAsync(_unrealPakExePath, pakPath, outputDirectory);

        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal([pakPath, "-List"], runner.Calls[0]);
        Assert.Equal([pakPath, "-Extract", outputDirectory], runner.Calls[1]);
        Assert.Equal(2, count);
    }

    /// <summary>
    /// -List shows only stored sizes, so a pak whose index declares huge unpacked sizes used to be
    /// unpacked until the drive was full. Over 8 GB it's refused before anything is written.
    /// </summary>
    [Fact]
    public async Task ExtractPakAsync_WouldUnpackToMoreThan8GB_RefusesBeforeExtracting()
    {
        var pakPath = WriteTestPak();
        var runner = new ScriptedProcessRunner(SafeListing);
        var service = new UnrealPakService(runner) { UnpackedSizeReader = _ => 9_000_000_000, FreeSpaceReader = _ => 500_000_000_000 };

        var ex = await Assert.ThrowsAsync<UnsafePakException>(() =>
            service.ExtractPakAsync(_unrealPakExePath, pakPath, Path.Combine(_tempDir, "Extracted")));

        Assert.Contains("'Test_P.pak' would unpack to 9 GB", ex.Message);
        Assert.DoesNotContain(runner.Calls, c => c.Contains("-Extract"));
    }

    [Fact]
    public async Task ExtractPakAsync_WouldLeaveLessThan1GBFree_RefusesBeforeExtracting()
    {
        var pakPath = WriteTestPak();
        var runner = new ScriptedProcessRunner(SafeListing);
        var service = new UnrealPakService(runner) { UnpackedSizeReader = _ => 2_000_000_000, FreeSpaceReader = _ => 2_500_000_000 };

        var ex = await Assert.ThrowsAsync<UnsafePakException>(() =>
            service.ExtractPakAsync(_unrealPakExePath, pakPath, Path.Combine(_tempDir, "Extracted")));

        Assert.Contains("Free up some space", ex.Message);
        Assert.DoesNotContain(runner.Calls, c => c.Contains("-Extract"));
    }

    /// <summary>When CUE4Parse can't read the pak's index the size check is skipped: UnrealPak then fails on such a pak by itself.</summary>
    [Fact]
    public async Task ExtractPakAsync_PakIndexUnreadable_SkipsTheSizeCheck()
    {
        var pakPath = WriteTestPak();
        var runner = new ScriptedProcessRunner(SafeListing);
        var service = new UnrealPakService(runner) { FreeSpaceReader = _ => 0 };

        Assert.Null(UnrealPakService.TryReadUnpackedSize(pakPath));
        await service.ExtractPakAsync(_unrealPakExePath, pakPath, Path.Combine(_tempDir, "Extracted"));

        Assert.Contains(runner.Calls, c => c.Contains("-Extract"));
    }

    /// <summary>
    /// UnrealPak logs everything to stdout, so a failed unpack used to put its whole log (dozens of
    /// lines) into the Merge status line, without the pak's name or what to do.
    /// </summary>
    [Fact]
    public async Task ExtractPakAsync_UnrealPakFailsWithALongLog_ShowsAShortReasonNamingThePak()
    {
        var pakPath = WriteTestPak();
        var longLog = string.Join("\r\n", Enumerable.Range(1, 40).Select(i => $"LogInit: Display: Loading config line {i}....")
            .Append("LogPakFile: Error: Corrupt pak index"));
        var runner = new ScriptedProcessRunner(SafeListing, new ProcessRunResult(1, longLog, ""));
        var service = new UnrealPakService(runner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExtractPakAsync(_unrealPakExePath, pakPath, Path.Combine(_tempDir, "Extracted")));

        Assert.Contains("Test_P.pak", ex.Message);
        Assert.Contains("Corrupt pak index", ex.Message);
        Assert.DoesNotContain("LogInit", ex.Message);
        Assert.True(ex.Message.Length < 500, ex.Message);
    }

    [Fact]
    public async Task ExtractPakAsync_RealisticOutput_ReturnsExtractedFileCountFromExtractedLines()
    {
        var pakPath = WriteTestPak();
        var outputDirectory = Path.Combine(_tempDir, "Extracted");
        var service = new UnrealPakService(new ScriptedProcessRunner(SafeListing, new ProcessRunResult(0, RealisticExtractOutput, "")));

        var count = await service.ExtractPakAsync(_unrealPakExePath, pakPath, outputDirectory);

        Assert.Equal(2, count);
    }

    /// <summary>Also the guard for -Filter: with no filter, exactly these three arguments, nothing trailing.</summary>
    [Fact]
    public async Task ExtractPakAsync_PassesPakPathAndExtractFlagAndOutputDirectory()
    {
        var pakPath = WriteTestPak();
        var outputDirectory = Path.Combine(_tempDir, "Extracted");
        var runner = new ScriptedProcessRunner(SafeListing);
        var service = new UnrealPakService(runner);

        await service.ExtractPakAsync(_unrealPakExePath, pakPath, outputDirectory);

        Assert.Equal(_unrealPakExePath, runner.LastFileName);
        Assert.Equal([pakPath, "-Extract", outputDirectory], runner.Calls[^1]);
    }

    [Fact]
    public async Task ExtractPakAsync_OutputDirectoryDoesNotExist_IsCreated()
    {
        var pakPath = WriteTestPak();
        var outputDirectory = Path.Combine(_tempDir, "Extracted");
        var service = new UnrealPakService(new ScriptedProcessRunner(SafeListing));

        await service.ExtractPakAsync(_unrealPakExePath, pakPath, outputDirectory);

        Assert.True(Directory.Exists(outputDirectory));
    }

    [Fact]
    public async Task ExtractPakAsync_MissingPakFile_ThrowsFileNotFoundException()
    {
        var service = new UnrealPakService(new CapturingProcessRunner(new ProcessRunResult(0, "", "")));

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.ExtractPakAsync(_unrealPakExePath, Path.Combine(_tempDir, "NoSuchPak.pak"), Path.Combine(_tempDir, "Extracted")));
    }

    [Fact]
    public async Task ExtractPakAsync_NonZeroExitCode_ThrowsWithStandardErrorInMessage()
    {
        var pakPath = Path.Combine(_tempDir, "Test_P.pak");
        File.WriteAllText(pakPath, "fake pak bytes");
        var service = new UnrealPakService(new CapturingProcessRunner(new ProcessRunResult(1, "", "corrupt pak")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExtractPakAsync(_unrealPakExePath, pakPath, Path.Combine(_tempDir, "Extracted")));
        Assert.Contains("corrupt pak", ex.Message);
    }

    [Fact]
    public async Task ExtractPakAsync_FilterProvided_ArgumentsIncludeFilterFlag()
    {
        // Confirmed live against the real bundled UnrealPak.exe: -Filter=<value> on -Extract
        // restricts extraction to just the matching entry/entries instead of the whole pak.
        var pakPath = WriteTestPak();
        var outputDirectory = Path.Combine(_tempDir, "Extracted");
        var runner = new ScriptedProcessRunner(SafeListing);
        var service = new UnrealPakService(runner);

        await service.ExtractPakAsync(_unrealPakExePath, pakPath, outputDirectory, filter: "AI/D_AIDescriptors.*");

        Assert.Equal(
            [pakPath, "-Extract", outputDirectory, "-Filter=AI/D_AIDescriptors.*"],
            runner.Calls[^1]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
