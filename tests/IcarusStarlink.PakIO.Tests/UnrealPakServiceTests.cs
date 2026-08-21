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

        public Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            LastFileName = fileName;
            LastArguments = arguments;

            // Stands in for what a real UnrealPak.exe -Extract run does to its target directory
            // (the 3rd argument — a temp directory UnrealPakService itself picks, not necessarily
            // the final outputDirectory) — the fake runner never actually shells out, so nothing
            // else would populate it.
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
    public async Task ExtractDataPakAsync_NonZeroExitCode_ThrowsWithStandardErrorInMessage()
    {
        var runner = Runner(new ProcessRunResult(1, "", "corrupt pak header"));
        var service = new UnrealPakService(runner);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExtractDataPakAsync(_unrealPakExePath, _contentPath, _outputDirectory, previousUpdateAt: null));
        Assert.Contains("corrupt pak header", exception.Message);
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

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
