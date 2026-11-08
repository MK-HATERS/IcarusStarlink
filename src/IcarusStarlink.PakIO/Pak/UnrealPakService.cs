using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CUE4Parse.UE4.Pak;
using CUE4Parse.UE4.Pak.Objects;
using CUE4Parse.UE4.Versions;
using IcarusStarlink.Core.IO;
using IcarusStarlink.PakIO.DataChanges;
using IcarusStarlink.PakIO.Safety;
using Microsoft.Extensions.Logging;

namespace IcarusStarlink.PakIO.Pak;

/// <summary>
/// Wraps UnrealPak.exe (Epic's own pak (un)packing tool, not something this app implements itself)
/// to extract the one pak file this app actually needs: Content\Data\data.pak. Confirmed against a
/// real Icarus install — the game ships 33 separate pakchunk*-WindowsNoEditor.pak files under
/// Content\Paks (assets: models/textures/audio, per the Notes5SQL.ini mapping), entirely
/// separate from Content\Data\data.pak, which holds just the gameplay DataTable JSON
/// (Crafting\D_ProcessorRecipes.json, Traits\D_Fuel.json, ~300 files across ~75 category folders)
/// that EXMOD mods actually target and this app's diff/merge engine actually reads. Also confirmed:
/// no AES key is needed to extract it — UnrealPak.exe pulls it apart directly.
/// </summary>
public sealed class UnrealPakService(IProcessRunner processRunner, ILogger<UnrealPakService>? logger = null) : IUnrealPakService
{
    /// <summary>The most one pak may unpack to, the same cap patch imports use.</summary>
    internal const long MaxUnpackedBytes = 8_000_000_000;

    /// <summary>Space left free on the drive after unpacking a pak.</summary>
    internal const long FreeSpaceReserveBytes = 1_000_000_000;

    private static readonly VersionContainer PakVersions = new(EGame.GAME_UE4_27);

    /// <summary>A pak's total unpacked size from its own index, or null when the index can't be read. Replaced in tests.</summary>
    internal Func<string, long?> UnpackedSizeReader { get; init; } = TryReadUnpackedSize;

    /// <summary>Free bytes on the drive that holds a folder, or null when that can't be told. Replaced in tests.</summary>
    internal Func<string, long?> FreeSpaceReader { get; init; } = TryReadFreeSpace;

    // Off the caller's thread: diffing two whole Data folders and swapping them is seconds of work,
    // and every caller is a UI command (Refresh game data, Update everything, Merge & Install).
    public Task<UnrealPakExtractResult> ExtractDataPakAsync(
        string unrealPakExePath, string icarusContentPath, string outputDirectory,
        DateTimeOffset? previousUpdateAt, CancellationToken cancellationToken = default) =>
        Task.Run(() => ExtractDataPakCoreAsync(unrealPakExePath, icarusContentPath, outputDirectory, previousUpdateAt, cancellationToken));

    private async Task<UnrealPakExtractResult> ExtractDataPakCoreAsync(
        string unrealPakExePath, string icarusContentPath, string outputDirectory,
        DateTimeOffset? previousUpdateAt, CancellationToken cancellationToken)
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
            SweepLeftoverExtractions(outputDirectory, tempExtractDirectory);
            var result = await processRunner.RunAsync(unrealPakExePath, [dataPakPath, "-Extract", tempExtractDirectory], cancellationToken);
            EnsureSuccess(result, "unpack the game's data.pak", "Check the game's files in Steam (Properties › Installed Files › Verify), then try again.");

            WeeklyChangeReport? changeReport = null;
            if (previousUpdateAt is { } previousAt && Directory.Exists(outputDirectory))
            {
                changeReport = DataFolderChangeTracker.Compute(outputDirectory, tempExtractDirectory, previousAt, DateTimeOffset.UtcNow);
            }

            // Replacing rather than merging into whatever's already there: a stale DataTable file
            // left over from a previous game version (renamed/removed field, dropped table)
            // sitting alongside this run's fresh extract would silently misrepresent the game's
            // *current* data. The diff above already captured what changed before this happens.
            //
            // Swapped by RENAME, never delete-then-move: a recursive
            // Directory.Delete that fails partway — one D_*.json held open by another tool, an
            // antivirus scan — used to leave outputDirectory half-deleted AND the finally below
            // then deleted the complete new extraction too, so one locked file cost the user both
            // copies. Renaming the old folder aside is all-or-nothing: it either fully moves or
            // (an open handle inside) fails before anything is touched, leaving the old data intact.
            string? retiredDirectory = null;
            if (Directory.Exists(outputDirectory))
            {
                retiredDirectory = Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(outputDirectory))!, $".DataExtract_old_{Guid.NewGuid():N}");
                Directory.Move(outputDirectory, retiredDirectory);
            }

            try
            {
                Directory.Move(tempExtractDirectory, outputDirectory);
            }
            catch when (retiredDirectory is not null && !Directory.Exists(outputDirectory))
            {
                // Put the old data back so the slot is never left empty; the new extraction is
                // cleaned up by the finally below, same as any other failure.
                Directory.Move(retiredDirectory, outputDirectory);
                throw;
            }

            if (retiredDirectory is not null)
            {
                TryDeleteRetiredDirectory(retiredDirectory);
            }

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

    /// <summary>
    /// Best-effort: the retired folder is the PREVIOUS extraction, already replaced and already
    /// diffed, so a locked file inside it is never worth failing an otherwise successful update
    /// over — it's left behind (inert, hidden by its leading dot) and retried by the next run's
    /// sweep of leftover ".DataExtract_*" siblings.
    /// </summary>
    private static void TryDeleteRetiredDirectory(string retiredDirectory) => Directories.TryDelete(retiredDirectory);

    /// <summary>
    /// Removes every ".DataExtract_*" sibling of outputDirectory except this run's own: a retired
    /// copy a locked file kept, or a whole extraction left by a run that was killed or lost power
    /// partway (its finally never ran). Only one refresh runs at a time, so any other one is a leftover.
    /// </summary>
    private static void SweepLeftoverExtractions(string outputDirectory, string currentRun)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(outputDirectory));
        if (parent is null || !Directory.Exists(parent))
        {
            return;
        }

        var currentRunFullPath = Path.GetFullPath(currentRun);
        foreach (var leftover in Directory.GetDirectories(parent, ".DataExtract_*"))
        {
            if (!string.Equals(Path.GetFullPath(leftover), currentRunFullPath, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteRetiredDirectory(leftover);
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

    // The real game's own paks mount their content at this path (confirmed against a
    // real, already-installed IMM_Merged_Mod_P.pak via `-List`) — a pak whose files aren't
    // reachable under this mount point won't actually be found by the game at runtime, even
    // though the pak itself would build and extract just fine.
    private const string GameContentMountPoint = PakMountPoints.GameContent;

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
            EnsureSuccess(result, "build the merged pak", "Check there's free disk space, then try again.");

            return files.Length;
        }
        finally
        {
            File.Delete(responseFilePath);
        }
    }

    // Real output format confirmed by running -List against a real installed pak:
    // `LogPakFile: Display: "Rada_CheatMenu/BP/BP_CheatMenuDummy.uasset" offset: 0, size: 9662
    // bytes, sha1: ..., compression: Zlib.` — every real entry line starts this way; the trailing
    // summary lines ("291 files (...)", "Unreal pak executed in ...") don't have a quote right
    // after "Display: " so this pattern naturally skips them without special-casing. Lenient on
    // purpose: the Files tab and the packed-file check read Starlink's own paks with it.
    private static readonly Regex ListEntryPattern = new("""^LogPakFile: Display: "(?<path>[^"]+)" offset:""", RegexOptions.Compiled);

    private static readonly Regex MountPointPattern = new(@"Mount point (?<mount>\S+)", RegexOptions.Compiled);

    // The strict forms EnsureEveryEntryStaysInside accepts: a whole entry line, a delete record,
    // and the summary line UE 4.27 prints after the last entry.
    private const string ListEntryLinePrefix = "LogPakFile: Display: \"";

    private static readonly Regex StrictListEntryPattern = new(
        """^LogPakFile: Display: "(?<path>[^"\r\n]+)" offset: \d+, size: -?\d+ bytes, sha1: [0-9A-Fa-f]+, compression: [\w-]*\.$""",
        RegexOptions.Compiled);

    private static readonly Regex DeletedListEntryPattern = new(
        """^LogPakFile: Display: "(?<path>[^"\r\n]+)" deleted\.$""", RegexOptions.Compiled);

    private static readonly Regex ListSummaryPattern = new(
        """^LogPakFile: Display: (?<count>\d+) files \(""", RegexOptions.Compiled);

    public async Task<string?> TryGetMountPointAsync(string unrealPakExePath, string pakFilePath, CancellationToken cancellationToken = default)
    {
        ValidateUnrealPakExePath(unrealPakExePath);
        if (!File.Exists(pakFilePath))
        {
            return null;
        }

        try
        {
            var result = await processRunner.RunAsync(unrealPakExePath, [pakFilePath, "-List"], cancellationToken);
            var match = MountPointPattern.Match(result.StandardOutput);
            return match.Success ? match.Groups["mount"].Value.Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> ListPakContentsAsync(string unrealPakExePath, string pakFilePath, CancellationToken cancellationToken = default)
    {
        ValidateUnrealPakExePath(unrealPakExePath);
        if (!File.Exists(pakFilePath))
        {
            throw new FileNotFoundException($"'{pakFilePath}' doesn't exist.");
        }

        var result = await processRunner.RunAsync(unrealPakExePath, [pakFilePath, "-List"], cancellationToken);
        EnsureSuccess(result, $"read '{Path.GetFileName(pakFilePath)}'", "The pak may be damaged: download that mod again.");

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

    public async Task<int> ExtractPakAsync(
        string unrealPakExePath, string pakFilePath, string outputDirectory,
        CancellationToken cancellationToken = default, string? filter = null)
    {
        ValidateUnrealPakExePath(unrealPakExePath);
        if (!File.Exists(pakFilePath))
        {
            throw new FileNotFoundException($"'{pakFilePath}' doesn't exist.");
        }

        var pakFileName = Path.GetFileName(pakFilePath);

        // UnrealPak -Extract writes each entry to "<outputDirectory>/<entry name>" as a plain string
        // join, and the names come from the pak's own index, which anyone can write. So every name
        // is read with -List and checked first, and nothing is unpacked when one would land outside
        // outputDirectory, or when the whole pak would unpack to more than the cap or the free space.
        var listing = await processRunner.RunAsync(unrealPakExePath, [pakFilePath, "-List"], cancellationToken);
        EnsureSuccess(listing, $"read '{pakFileName}'", "The pak may be damaged: download that mod again.");
        EnsureEveryEntryStaysInside(pakFilePath, listing.StandardOutput);
        await EnsureUnpackedSizeFitsAsync(pakFilePath, outputDirectory, cancellationToken);

        Directory.CreateDirectory(outputDirectory);

        // filter is appended, not baked into the literal above, so the null case (every existing
        // caller today) builds the exact same 3-argument list as before this parameter existed.
        var arguments = new List<string> { pakFilePath, "-Extract", outputDirectory };
        if (filter is not null)
        {
            arguments.Add($"-Filter={filter}");
        }

        var result = await processRunner.RunAsync(unrealPakExePath, arguments, cancellationToken);
        EnsureSuccess(result, $"unpack '{pakFileName}'", "The pak may be damaged: download that mod again, or leave it out of the merge.");

        // Counted from stdout's own "Extracted "..."" lines, not a directory-wide file count —
        // outputDirectory can already hold other files (extraction is additive), which a bare
        // Directory.GetFiles count would wrongly include.
        return result.StandardOutput.Split('\n').Count(line => line.Contains("LogPakFile: Display: Extracted \"", StringComparison.Ordinal));
    }

    /// <summary>
    /// Fails closed on UnrealPak's -List output: every line that starts like an entry must be a
    /// whole entry line (or a delete record) whose name is a plain relative path (no root, no "..",
    /// no ':' or other invalid characters, no device names), and the number of entries must match
    /// the summary line. A name holding a quote, CR or LF breaks its line and so is refused too.
    /// </summary>
    internal static void EnsureEveryEntryStaysInside(string pakFilePath, string listOutput)
    {
        var pakFileName = Path.GetFileName(pakFilePath);
        int? declaredCount = null;
        var entryCount = 0;
        var deletedCount = 0;

        foreach (var rawLine in listOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (ListSummaryPattern.Match(line) is { Success: true } summary)
            {
                if (declaredCount is not null || !int.TryParse(summary.Groups["count"].Value, out var count))
                {
                    throw CantCheckListing(pakFileName);
                }

                declaredCount = count;
                continue;
            }

            if (!line.StartsWith(ListEntryLinePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            Match entry;
            if ((entry = StrictListEntryPattern.Match(line)).Success)
            {
                entryCount++;
            }
            else if ((entry = DeletedListEntryPattern.Match(line)).Success)
            {
                deletedCount++;
            }
            else
            {
                throw new UnsafePakException(
                    $"'{pakFileName}' lists a file name Starlink can't read safely, so it wasn't unpacked. Don't use this pak; tell its author.");
            }

            var name = entry.Groups["path"].Value;
            if (!AssetPathGuard.IsSafeRelativePath(name))
            {
                throw new UnsafePakException(
                    $"'{pakFileName}' holds a file named '{Shorten(name)}' that would be unpacked outside its folder, so it wasn't unpacked. Don't use this pak; tell its author.");
            }
        }

        // UnrealPak counts delete records in some builds and not in others, so either total is accepted.
        if (declaredCount is not { } declared || (declared != entryCount && declared != entryCount + deletedCount))
        {
            throw CantCheckListing(pakFileName);
        }
    }

    private static UnsafePakException CantCheckListing(string pakFileName) =>
        new($"'{pakFileName}' lists its files in a way Starlink can't check, so it wasn't unpacked.");

    private static string Shorten(string text) => text.Length <= 120 ? text : text[..117] + "...";

    /// <summary>
    /// Refuses a pak whose entries add up to more than MaxUnpackedBytes, or to more than the
    /// destination drive's free space minus FreeSpaceReserveBytes: -List shows only each entry's
    /// stored size, so a crafted index could otherwise fill the drive during -Extract. The sizes
    /// come from the pak's own index; when that can't be read the check is skipped, since
    /// UnrealPak then fails on the pak anyway.
    /// </summary>
    private async Task EnsureUnpackedSizeFitsAsync(string pakFilePath, string outputDirectory, CancellationToken cancellationToken)
    {
        var unpackedSize = await Task.Run(() => UnpackedSizeReader(pakFilePath), cancellationToken);
        if (unpackedSize is not { } size)
        {
            return;
        }

        var pakFileName = Path.GetFileName(pakFilePath);
        if (size > MaxUnpackedBytes)
        {
            throw new UnsafePakException(
                $"'{pakFileName}' would unpack to {Gigabytes(size)}, more than Starlink allows ({Gigabytes(MaxUnpackedBytes)}), so it wasn't unpacked. Don't use this pak; tell its author.");
        }

        if (FreeSpaceReader(outputDirectory) is { } freeBytes && size > freeBytes - FreeSpaceReserveBytes)
        {
            throw new UnsafePakException(
                $"'{pakFileName}' would unpack to {Gigabytes(size)}, but the drive has only {Gigabytes(freeBytes)} free and Starlink keeps {Gigabytes(FreeSpaceReserveBytes)} spare, so it wasn't unpacked. Free up some space, then try again.");
        }
    }

    private static string Gigabytes(long bytes) => $"{bytes / 1_000_000_000d:0.##} GB";

    /// <summary>
    /// Adds up every entry's unpacked size from the pak's index with CUE4Parse (nothing is
    /// unpacked). An impossible negative size or a total too big to add up counts as too big.
    /// Null when the index can't be read.
    /// </summary>
    internal static long? TryReadUnpackedSize(string pakFilePath)
    {
        try
        {
            // The stream is opened here, not by PakFileReader: when its constructor throws on a
            // damaged pak it never disposes, and the pak stayed locked until the process exited.
            using var stream = new FileStream(pakFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new PakFileReader(pakFilePath, stream, PakVersions);
            reader.Mount(StringComparer.OrdinalIgnoreCase);

            long total = 0;
            foreach (var file in reader.Files.Values)
            {
                if (file is not FPakEntry { IsDeleted: false } entry)
                {
                    continue;
                }

                if (entry.UncompressedSize < 0 || entry.UncompressedSize > long.MaxValue - total)
                {
                    return long.MaxValue;
                }

                total += entry.UncompressedSize;
            }

            return total;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static long? TryReadFreeSpace(string directory)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A network share (no drive letter) or a drive that went away: the cap still applies.
            return null;
        }
    }

    // Confirmed live against the real binary — a healthy pak's summary line reads exactly
    // `Pak file "<path>" healthy, 1515 files checked.`; a corrupted one reads
    // `Pak file "<path>" corrupted (1 errors out of 1515 files checked.).` — the group captured
    // here is everything after the quoted pak path, which is the part actually worth surfacing.
    // Stops at '\r': ProcessRunner joins stdout with CRLF, and a plain `.+$` captured the CR (so
    // the TrimEnd('.') below never fired and a stray CR rode into the rebuild report).
    private static readonly Regex VerifySummaryPattern = new(
        """^LogPakFile: Display: Pak file "[^"]+" (?<summary>[^\r\n]+)\r?$""", RegexOptions.Compiled | RegexOptions.Multiline);

    public async Task<PakVerifyResult> VerifyPakAsync(string unrealPakExePath, string pakFilePath, CancellationToken cancellationToken = default)
    {
        ValidateUnrealPakExePath(unrealPakExePath);
        if (!File.Exists(pakFilePath))
        {
            throw new FileNotFoundException($"'{pakFilePath}' doesn't exist.");
        }

        var result = await processRunner.RunAsync(unrealPakExePath, [pakFilePath, "-Verify"], cancellationToken);

        // Unlike every other method here, a non-zero exit code from -Verify specifically (confirmed
        // live: exit code 1 against a deliberately byte-corrupted real pak) means "the pak is
        // corrupt", a real, expected OUTCOME to report back — not a tool failure to throw for.
        var isHealthy = result.ExitCode == 0;
        var summaryMatch = VerifySummaryPattern.Match(result.StandardOutput);
        string message;
        if (summaryMatch.Success)
        {
            message = summaryMatch.Groups["summary"].Value.TrimEnd('.');
        }
        else if (isHealthy)
        {
            message = "Pak file healthy.";
        }
        else
        {
            LogFullOutput("-Verify", result);
            message = $"UnrealPak exited with code {result.ExitCode}: {ShortDetail(result)}";
        }

        return new PakVerifyResult(isHealthy, message);
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

    /// <summary>
    /// Shared by every method here except VerifyPakAsync, whose own non-zero exit code means
    /// something different (a real "pak is corrupt" outcome to report, not a tool failure to throw
    /// for) — see its own doc comment. UnrealPak logs everything to stdout, so the message carries
    /// only a short reason (ShortDetail) plus what to do next; the whole log goes to the debug log.
    /// </summary>
    private void EnsureSuccess(ProcessRunResult result, string what, string nextStep)
    {
        if (result.ExitCode != 0)
        {
            LogFullOutput(what, result);
            throw new InvalidOperationException($"UnrealPak couldn't {what} (exit code {result.ExitCode}): {ShortDetail(result)}. {nextStep}");
        }
    }

    private void LogFullOutput(string what, ProcessRunResult result) =>
        logger?.LogDebug(
            "UnrealPak ({What}) exited with code {ExitCode}. Output: {StandardOutput} Errors: {StandardError}",
            what, result.ExitCode, result.StandardOutput, result.StandardError);

    private const int MaxDetailLength = 300;

    /// <summary>
    /// A short reason for a failed run: StandardError when there is any; otherwise the last 3
    /// output lines that mention an error or warning, or else the last 3 lines. Joined with " / "
    /// and cut to about 300 characters.
    /// </summary>
    internal static string ShortDetail(ProcessRunResult result)
    {
        static List<string> Lines(string text) =>
            [.. text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)];

        var errorLines = Lines(result.StandardError);
        List<string> picked;
        if (errorLines.Count > 0)
        {
            picked = errorLines;
        }
        else
        {
            var outputLines = Lines(result.StandardOutput);
            var flagged = outputLines
                .Where(l => l.Contains("Error", StringComparison.OrdinalIgnoreCase) || l.Contains("Warning", StringComparison.OrdinalIgnoreCase))
                .ToList();
            picked = [.. (flagged.Count > 0 ? flagged : outputLines).TakeLast(3)];
        }

        var detail = picked.Count == 0 ? "it gave no reason" : string.Join(" / ", picked).TrimEnd('.');
        return detail.Length <= MaxDetailLength ? detail : detail[..(MaxDetailLength - 3)] + "...";
    }
}
