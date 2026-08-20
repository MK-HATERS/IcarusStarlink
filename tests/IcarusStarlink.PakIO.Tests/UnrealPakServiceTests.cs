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

    private sealed class FakeProcessRunner(ProcessRunResult result, IReadOnlyList<string> filesToCreate) : IProcessRunner
    {
        public string? LastFileName { get; private set; }
        public IReadOnlyList<string>? LastArguments { get; private set; }

        public Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            LastFileName = fileName;
            LastArguments = arguments;

            // Stands in for what a real UnrealPak.exe -Extract run does to outputDirectory (the
            // 3rd argument) — the fake runner never actually shells out, so nothing else would
            // populate it.
            var outputDirectory = arguments[2];
            foreach (var relativePath in filesToCreate)
            {
                var path = Path.Combine(outputDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "content");
            }

            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task ExtractDataPakAsync_MissingExePath_ThrowsFileNotFoundException()
    {
        var service = new UnrealPakService(new FakeProcessRunner(new ProcessRunResult(0, "", ""), []));

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.ExtractDataPakAsync(Path.Combine(_tempDir, "NoSuchExe.exe"), _contentPath, _outputDirectory));
    }

    [Fact]
    public async Task ExtractDataPakAsync_MissingDataPak_ThrowsFileNotFoundException()
    {
        var service = new UnrealPakService(new FakeProcessRunner(new ProcessRunResult(0, "", ""), []));
        var contentPathWithNoDataPak = Path.Combine(_tempDir, "EmptyContent");
        Directory.CreateDirectory(contentPathWithNoDataPak);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.ExtractDataPakAsync(_unrealPakExePath, contentPathWithNoDataPak, _outputDirectory));
    }

    [Fact]
    public async Task ExtractDataPakAsync_Success_ReturnsExtractedFileCount()
    {
        var runner = new FakeProcessRunner(new ProcessRunResult(0, "ok", ""), ["Crafting/D_ProcessorRecipes.json", "Traits/D_Fuel.json"]);
        var service = new UnrealPakService(runner);

        var result = await service.ExtractDataPakAsync(_unrealPakExePath, _contentPath, _outputDirectory);

        Assert.Equal(2, result.ExtractedFileCount);
    }

    [Fact]
    public async Task ExtractDataPakAsync_PassesDataPakPathExtractFlagAndOutputDirectory()
    {
        var runner = new FakeProcessRunner(new ProcessRunResult(0, "", ""), []);
        var service = new UnrealPakService(runner);

        await service.ExtractDataPakAsync(_unrealPakExePath, _contentPath, _outputDirectory);

        Assert.Equal(_unrealPakExePath, runner.LastFileName);
        Assert.Equal([Path.Combine(_contentPath, "Data", "data.pak"), "-Extract", _outputDirectory], runner.LastArguments);
    }

    [Fact]
    public async Task ExtractDataPakAsync_NonZeroExitCode_ThrowsWithStandardErrorInMessage()
    {
        var runner = new FakeProcessRunner(new ProcessRunResult(1, "", "corrupt pak header"), []);
        var service = new UnrealPakService(runner);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExtractDataPakAsync(_unrealPakExePath, _contentPath, _outputDirectory));
        Assert.Contains("corrupt pak header", exception.Message);
    }

    [Fact]
    public async Task ExtractDataPakAsync_StaleFileFromPreviousRun_IsRemoved()
    {
        Directory.CreateDirectory(_outputDirectory);
        var staleFile = Path.Combine(_outputDirectory, "OldTable", "D_Removed.json");
        Directory.CreateDirectory(Path.GetDirectoryName(staleFile)!);
        File.WriteAllText(staleFile, "stale");

        var runner = new FakeProcessRunner(new ProcessRunResult(0, "", ""), ["NewTable/D_Current.json"]);
        var service = new UnrealPakService(runner);

        await service.ExtractDataPakAsync(_unrealPakExePath, _contentPath, _outputDirectory);

        Assert.False(File.Exists(staleFile));
        Assert.True(File.Exists(Path.Combine(_outputDirectory, "NewTable", "D_Current.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
