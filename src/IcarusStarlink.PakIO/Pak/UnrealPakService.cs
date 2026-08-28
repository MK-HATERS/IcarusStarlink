using System.Security.Cryptography;
using System.Text.RegularExpressions;
using IcarusStarlink.PakIO.DataChanges;

namespace IcarusStarlink.PakIO.Pak;

/// <summary>
/// Wraps UnrealPak.exe (Epic's own pak (un)packing tool, not something this app implements itself)
/// to extract the one pak file this app actually needs: Content\Data\data.pak. Confirmed against a
/// real Icarus install — the game ships 33 separate pakchunk*-WindowsNoEditor.pak files under
/// Content\Paks (assets: models/textures/audio, per the user's own Notes5SQL.ini mapping), entirely
/// separate from Content\Data\data.pak, which holds just the gameplay DataTable JSON
/// (Crafting\D_ProcessorRecipes.json, Traits\D_Fuel.json, ~300 files across ~75 category folders)
/// that EXMOD mods actually target and this app's diff/merge engine actually reads. Also confirmed:
/// no AES key is needed to extract it — UnrealPak.exe pulls it apart directly.
/// </summary>
public sealed class UnrealPakService(IProcessRunner processRunner) : IUnrealPakService
{
    public async Task<UnrealPakExtractResult> ExtractDataPakAsync(
        string unrealPakExePath, string icarusContentPath, string outputDirectory,
        DateTimeOffset? previousUpdateAt, CancellationToken cancellationToken = default)
    {
        var dataPakPath = ResolveDataPakPath(unrealPakExePath, icarusContentPath);

        // Extracted to a fresh temp directory first, not straight into outputDirectory: the
        // previous extraction has to stay intact long enough to diff against, and a half-finished
        // UnrealPak run failing partway through must never leave outputDirectory itself in a
        // broken in-between state. A SIBLING of outputDirectory, not System.IO.Path.GetTempPath()
        // — that's always the OS drive (typically C:), while this app is a portable "unzip
        // anywhere" install and outputDirectory lives wherever the user put it. Directory.Move
        // below refuses to cross volumes (a real, confirmed .NET limitation, not guessed), so a
        // user who installed this app on the same drive as their game — very often NOT the OS
        // drive — hit a real "Update data folder" failure every time. A sibling folder is always
        // on the same volume as outputDirectory by construction, so the move always succeeds.
        var tempExtractDirectory = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(outputDirectory))!, $".DataExtract_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempExtractDirectory);

        try
        {
            var result = await processRunner.RunAsync(unrealPakExePath, [dataPakPath, "-Extract", tempExtractDirectory], cancellationToken);
            if (result.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
                throw new InvalidOperationException($"UnrealPak.exe exited with code {result.ExitCode}: {detail}");
            }

            WeeklyChangeReport? changeReport = null;
            if (previousUpdateAt is { } previousAt && Directory.Exists(outputDirectory))
            {
                changeReport = DataFolderChangeTracker.Compute(outputDirectory, tempExtractDirectory, previousAt, DateTimeOffset.UtcNow);
            }

            // Replacing rather than merging into whatever's already there: a stale DataTable file
            // left over from a previous game version (renamed/removed field, dropped table)
            // sitting alongside this run's fresh extract would silently misrepresent the game's
            // *current* data. The diff above already captured what changed before this happens.
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
            Directory.Move(tempExtractDirectory, outputDirectory);

            var extractedFileCount = Directory.GetFiles(outputDirectory, "*", SearchOption.AllDirectories).Length;
            return new UnrealPakExtractResult(extractedFileCount, changeReport);
        }
        finally
        {
            // No-op after a successful run (Directory.Move already emptied tempExtractDirectory's
            // parent slot) — only actually cleans anything up on a failure path above.
            if (Directory.Exists(tempExtractDirectory))
            {
                Directory.Delete(tempExtractDirectory, recursive: true);
            }
        }
    }

    public async Task<string?> TryGetDataPakHashAsync(string icarusContentPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var dataPakPath = Path.Combine(icarusContentPath, "Data", "data.pak");
            if (!File.Exists(dataPakPath))
            {
                return null;
            }

            await using var stream = File.OpenRead(dataPakPath);
            var hashBytes = await SHA256.HashDataAsync(stream, cancellationToken);
            return Convert.ToHexString(hashBytes);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested is false)
        {
            // Passive/background check — a locked file, a drive that briefly went offline, or a
            // content path that isn't really an Icarus install shouldn't surface as an error here,
            // only as "no hash available" (the caller treats null as "can't tell, don't nag").
            return null;
        }
    }

    // The real game's own paks mount their content at this path (confirmed against the user's
    // real, already-installed IMM_Merged_Mod_P.pak via `-List`) — a pak whose files aren't
    // reachable under this mount point won't actually be found by the game at runtime, even
    // though the pak itself would build and extract just fine.
    private const string GameContentMountPoint = "../../../Icarus/Content/";

    /// <summary>
    /// Packs everything under stagingDirectory into one pak at outputPakPath. Built and verified
    /// against the real UnrealPak.exe binary, not just its documented flags — two things aren't
    /// obvious from -help alone: (1) UnrealPak's -Create with a response file crashes
    /// ("Assertion failed... VerifyIndexesMatch") when the response file's destination paths don't
    /// share a common prefix, which multi-folder mod content (data/... alongside BP/..., ASS/...)
    /// never would on its own — worked around by baking GameContentMountPoint onto the front of
    /// every single line, so every entry always shares that prefix. (2) Passing stagingDirectory
    /// itself as -Create=<folder> (no response file) avoids that crash entirely, but then
    /// UnrealPak auto-infers the mount point from the folder's own absolute path — wrong, and
    /// unusable in-game. The response-file form above is the only one confirmed to produce a pak
    /// whose mount point actually matches a real installed pak's own.
    /// </summary>
    public async Task<int> CreatePakAsync(
        string unrealPakExePath, string stagingDirectory, string outputPakPath, CancellationToken cancellationToken = default)
    {
        ValidateUnrealPakExePath(unrealPakExePath);

        var files = Directory.GetFiles(stagingDirectory, "*", SearchOption.AllDirectories);
        if (files.Length == 0)
        {
            throw new InvalidOperationException("Nothing to pack — the merge queue produced no files.");
        }

        var responseFilePath = Path.Combine(Path.GetTempPath(), "IcarusStarlink", $"Response_{Guid.NewGuid():N}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(responseFilePath)!);

        try
        {
            var lines = files.Select(file =>
            {
                var relativePath = Path.GetRelativePath(stagingDirectory, file).Replace('\\', '/');
                return $"\"{file}\" \"{GameContentMountPoint}{relativePath}\"";
            });
            await File.WriteAllLinesAsync(responseFilePath, lines, cancellationToken);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPakPath)!);
            // -compress: confirmed live against the real bundled UnrealPak.exe, not assumed —
            // without it every entry is stored raw, and this app's own output is almost entirely
            // JSON DataTable files (highly compressible: a real 30-file/3.9MB test sample dropped
            // to 205KB, ~95% smaller) plus whatever binary assets a queued mod bundles. A full
            // extract-and-diff round trip against the compressed output confirmed byte-identical
            // content, so this is free space savings, not a tradeoff.
            var result = await processRunner.RunAsync(unrealPakExePath, [outputPakPath, $"-Create={responseFilePath}", "-compress"], cancellationToken);
            if (result.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
                throw new InvalidOperationException($"UnrealPak.exe exited with code {result.ExitCode}: {detail}");
            }

            return files.Length;
        }
        finally
        {
            File.Delete(responseFilePath);
        }
    }

    // Real output format confirmed by running -List against a real installed pak during planning:
    // `LogPakFile: Display: "Rada_CheatMenu/BP/BP_CheatMenuDummy.uasset" offset: 0, size: 9662
    // bytes, sha1: ..., compression: Zlib.` — every real entry line starts this way; the trailing
    // summary lines ("291 files (...)", "Unreal pak executed in ...") don't have a quote right
    // after "Display: " so this pattern naturally skips them without special-casing.
    private static readonly Regex ListEntryPattern = new("""^LogPakFile: Display: "(?<path>[^"]+)" offset:""", RegexOptions.Compiled);

    public async Task<IReadOnlyList<string>> ListPakContentsAsync(string unrealPakExePath, string pakFilePath, CancellationToken cancellationToken = default)
    {
        ValidateUnrealPakExePath(unrealPakExePath);
        if (!File.Exists(pakFilePath))
        {
            throw new FileNotFoundException($"'{pakFilePath}' doesn't exist.");
        }

        var result = await processRunner.RunAsync(unrealPakExePath, [pakFilePath, "-List"], cancellationToken);
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            throw new InvalidOperationException($"UnrealPak.exe exited with code {result.ExitCode}: {detail}");
        }

        var paths = new List<string>();
        foreach (var line in result.StandardOutput.Split('\n'))
        {
            var match = ListEntryPattern.Match(line);
            if (match.Success)
            {
                paths.Add(match.Groups["path"].Value);
            }
        }

        return paths;
    }

    public async Task<int> ExtractPakAsync(string unrealPakExePath, string pakFilePath, string outputDirectory, CancellationToken cancellationToken = default)
    {
        ValidateUnrealPakExePath(unrealPakExePath);
        if (!File.Exists(pakFilePath))
        {
            throw new FileNotFoundException($"'{pakFilePath}' doesn't exist.");
        }

        Directory.CreateDirectory(outputDirectory);

        var result = await processRunner.RunAsync(unrealPakExePath, [pakFilePath, "-Extract", outputDirectory], cancellationToken);
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            throw new InvalidOperationException($"UnrealPak.exe exited with code {result.ExitCode}: {detail}");
        }

        // Counted from stdout's own "Extracted "..."" lines, not a directory-wide file count —
        // outputDirectory can already hold other files (RebuildService extracts each attached
        // prebuilt pak into the same staging folder the merge output already populated), which a
        // bare Directory.GetFiles count would wrongly include.
        return result.StandardOutput.Split('\n').Count(line => line.Contains("LogPakFile: Display: Extracted \"", StringComparison.Ordinal));
    }

    private static string ResolveDataPakPath(string unrealPakExePath, string icarusContentPath)
    {
        ValidateUnrealPakExePath(unrealPakExePath);

        var dataPakPath = Path.Combine(icarusContentPath, "Data", "data.pak");
        if (!File.Exists(dataPakPath))
        {
            throw new FileNotFoundException(
                $"'{dataPakPath}' doesn't exist — check the Icarus Content folder setting (it should be the "
                + "…\\Icarus\\Icarus\\Content folder, the one containing a Data subfolder with data.pak in it).");
        }

        return dataPakPath;
    }

    private static void ValidateUnrealPakExePath(string unrealPakExePath)
    {
        if (string.IsNullOrWhiteSpace(unrealPakExePath) || !File.Exists(unrealPakExePath))
        {
            throw new FileNotFoundException($"UnrealPak.exe not found at '{unrealPakExePath}' — check the UnrealPak.exe path setting.");
        }
    }
}
