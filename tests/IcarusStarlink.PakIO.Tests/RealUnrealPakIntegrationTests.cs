using IcarusStarlink.PakIO.Pak;

namespace IcarusStarlink.PakIO.Tests;

/// <summary>
/// Every other UnrealPakServiceTests case runs against a fake IProcessRunner — safe and fast, but
/// it means real UnrealPak.exe quirks (the mount-point folding behavior, whether -compress
/// round-trips bytes correctly, the exact response-file mechanics) have only ever been confirmed by
/// hand-written, disposable scratch console harnesses re-created from memory each time the question
/// came up again this session. These tests shell out to the REAL bundled UnrealPak.exe via the REAL
/// ProcessRunner instead, so those quirks are re-verified automatically on every run instead of
/// needing a human to remember to re-check them by hand.
///
/// Opt-in, not part of the normal test run: a real UnrealPak.exe is a real external dependency this
/// repo doesn't (and shouldn't) check in, and its exact path is machine-specific. Set the
/// ICARUSSTARLINK_TEST_REAL_UNREALPAK_EXE environment variable to a real UnrealPak.exe's path to run
/// these — e.g. this app's own bundled Tools\UnrealPak\UnrealPak.exe once installed via Settings, or
/// classic IMM's own copy. xUnit 2.x has no built-in dynamic skip, so a test whose gate isn't met
/// just returns immediately with no assertion (shows as a trivially-passing test, not a failure) —
/// the intent is spelled out in each test's own body, not left to guesswork.
/// </summary>
public class RealUnrealPakIntegrationTests : IDisposable
{
    private const string EnvVarName = "ICARUSSTARLINK_TEST_REAL_UNREALPAK_EXE";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
    private readonly string? _realUnrealPakExePath = Environment.GetEnvironmentVariable(EnvVarName);

    private static UnrealPakService CreateRealService() => new(new ProcessRunner());

    [Fact]
    public async Task CreateThenList_MultipleFilesNoSharedPrefix_EveryEntryKeepsItsFullPath()
    {
        if (_realUnrealPakExePath is not { Length: > 0 } || !File.Exists(_realUnrealPakExePath))
        {
            return; // See this class's own doc comment — set ICARUSSTARLINK_TEST_REAL_UNREALPAK_EXE to run this for real.
        }

        var staging = Path.Combine(_tempDir, "Staging");
        WriteStaged(staging, "data/Crafting/D_ProcessorRecipes.json", "{}");
        WriteStaged(staging, "BP/Thing.uasset", "binary");
        var outputPak = Path.Combine(_tempDir, "Out_P.pak");
        var service = CreateRealService();

        await service.CreatePakAsync(_realUnrealPakExePath, staging, outputPak);
        var listed = await service.ListPakContentsAsync(_realUnrealPakExePath, outputPak);

        Assert.Contains("data/Crafting/D_ProcessorRecipes.json", listed);
        Assert.Contains("BP/Thing.uasset", listed);
    }

    [Fact]
    public async Task CreateThenList_EveryStagedFileSharesADataPrefix_MountPointFoldsItAway()
    {
        // This is the real quirk RebuildService's own VerifyEveryStagedFileWasActuallyPackedAsync
        // exists to tolerate — confirmed here directly against the real binary rather than trusted
        // from memory: when every packed entry shares a leading path segment, UnrealPak's own
        // mount-point inference (the longest common prefix across every entry) absorbs that shared
        // segment, and -List reports each entry WITHOUT it.
        if (_realUnrealPakExePath is not { Length: > 0 } || !File.Exists(_realUnrealPakExePath))
        {
            return;
        }

        var staging = Path.Combine(_tempDir, "Staging");
        WriteStaged(staging, "data/Crafting/D_ProcessorRecipes.json", "{}");
        WriteStaged(staging, "data/Traits/D_Fuel.json", "{}");
        var outputPak = Path.Combine(_tempDir, "Out_P.pak");
        var service = CreateRealService();

        await service.CreatePakAsync(_realUnrealPakExePath, staging, outputPak);
        var listed = await service.ListPakContentsAsync(_realUnrealPakExePath, outputPak);

        Assert.Contains("Crafting/D_ProcessorRecipes.json", listed);
        Assert.Contains("Traits/D_Fuel.json", listed);
        Assert.DoesNotContain(listed, path => path.StartsWith("data/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreateThenList_ExactlyOneStagedFile_FoldsAwayEveryDirectorySegment()
    {
        // Resolves a real question this session's own RebuildService fix left open: does a
        // genuinely single-file pak fold its ENTIRE directory chain into the mount point (leaving
        // just the bare filename), or only its immediate containing folder? Confirmed here directly
        // against the real binary: the whole chain folds away — "data/Crafting/D_ProcessorRecipes.
        // json" is reported back as the bare "D_ProcessorRecipes.json", nothing in between. This is
        // exactly what ComputeFoldedPrefixSegmentCount's own "a lone path trivially matches itself
        // all the way to the cap" reasoning already predicted; RebuildService's separate
        // boundary-tolerant fallback for this same case remains a reasonable safety net regardless.
        if (_realUnrealPakExePath is not { Length: > 0 } || !File.Exists(_realUnrealPakExePath))
        {
            return;
        }

        var staging = Path.Combine(_tempDir, "Staging");
        WriteStaged(staging, "data/Crafting/D_ProcessorRecipes.json", "{}");
        var outputPak = Path.Combine(_tempDir, "Out_P.pak");
        var service = CreateRealService();

        await service.CreatePakAsync(_realUnrealPakExePath, staging, outputPak);
        var listed = await service.ListPakContentsAsync(_realUnrealPakExePath, outputPak);

        var entry = Assert.Single(listed);
        Assert.Equal("D_ProcessorRecipes.json", entry);
    }

    [Fact]
    public async Task CreateThenExtract_RealFileContent_RoundTripsByteForByteThroughCompression()
    {
        // -compress is passed on every real Create (see CreatePakAsync_PassesCompressFlag's own
        // comment on why) — this confirms directly, not just by prior manual spot-check, that
        // compression is genuinely lossless for this app's own JSON-heavy output.
        if (_realUnrealPakExePath is not { Length: > 0 } || !File.Exists(_realUnrealPakExePath))
        {
            return;
        }

        var staging = Path.Combine(_tempDir, "Staging");
        var originalContent = """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","CraftTime":5}]}""";
        WriteStaged(staging, "data/Crafting/D_ProcessorRecipes.json", originalContent);
        var outputPak = Path.Combine(_tempDir, "Out_P.pak");
        var extractedDirectory = Path.Combine(_tempDir, "Extracted");
        var service = CreateRealService();

        await service.CreatePakAsync(_realUnrealPakExePath, staging, outputPak);
        await service.ExtractPakAsync(_realUnrealPakExePath, outputPak, extractedDirectory);

        var extractedFile = Directory.EnumerateFiles(extractedDirectory, "D_ProcessorRecipes.json", SearchOption.AllDirectories).Single();
        Assert.Equal(originalContent, File.ReadAllText(extractedFile));
    }

    private static void WriteStaged(string stagingDirectory, string relativePath, string content)
    {
        var path = Path.Combine(stagingDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
