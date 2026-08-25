using IcarusStarlink.PakIO.Container;

namespace IcarusStarlink.PakIO.Tests;

public class AnyArchiveExtractorTests
{
    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"IcarusStarlink.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void ExtractToDirectory_RealZip_ReproducesFileTreeAndContent()
    {
        var zipPath = Path.Combine(Path.GetTempPath(), $"IcarusStarlink.Tests.{Guid.NewGuid():N}.zip");
        var destDir = CreateTempDirectory();
        try
        {
            using (var archive = new System.IO.Compression.ZipArchive(File.Create(zipPath), System.IO.Compression.ZipArchiveMode.Create))
            {
                var e1 = archive.CreateEntry("readme.txt");
                using (var s = e1.Open()) { s.Write("hello"u8); }
                var e2 = archive.CreateEntry("Mods/Sub/data.bin");
                using (var s = e2.Open()) { s.Write([1, 2, 3, 4]); }
            }

            AnyArchiveExtractor.ExtractToDirectory(zipPath, destDir);

            Assert.Equal("hello", File.ReadAllText(Path.Combine(destDir, "readme.txt")));
            Assert.Equal<byte>([1, 2, 3, 4], File.ReadAllBytes(Path.Combine(destDir, "Mods", "Sub", "data.bin")));
        }
        finally
        {
            File.Delete(zipPath);
            Directory.Delete(destDir, recursive: true);
        }
    }

    [Fact]
    public void ExtractToDirectory_ZipWithBackslashTerminatedDirectoryEntry_SkipsItInsteadOfThrowing()
    {
        // Real-world shape, found live: PowerShell's Compress-Archive cmdlet writes a directory
        // entry as e.g. "BP\Objects\" (backslash-terminated), not the ZIP spec's own forward-slash
        // convention — a plain EndsWith('/') check let this fall through as if it were a real file.
        var zipPath = Path.Combine(Path.GetTempPath(), $"IcarusStarlink.Tests.{Guid.NewGuid():N}.zip");
        var destDir = CreateTempDirectory();
        try
        {
            using (var archive = new System.IO.Compression.ZipArchive(File.Create(zipPath), System.IO.Compression.ZipArchiveMode.Create))
            {
                archive.CreateEntry("BP\\Objects\\");
                var e1 = archive.CreateEntry("BP\\Objects\\real.bin");
                using (var s = e1.Open()) { s.Write([9, 9, 9]); }
            }

            AnyArchiveExtractor.ExtractToDirectory(zipPath, destDir);

            Assert.Equal<byte>([9, 9, 9], File.ReadAllBytes(Path.Combine(destDir, "BP", "Objects", "real.bin")));
        }
        finally
        {
            File.Delete(zipPath);
            Directory.Delete(destDir, recursive: true);
        }
    }

    [Fact]
    public void ExtractToDirectory_ZipWithZipSlipEntry_ThrowsFormatExceptionInsteadOfEscaping()
    {
        var zipPath = Path.Combine(Path.GetTempPath(), $"IcarusStarlink.Tests.{Guid.NewGuid():N}.zip");
        var destDir = CreateTempDirectory();
        try
        {
            using (var archive = new System.IO.Compression.ZipArchive(File.Create(zipPath), System.IO.Compression.ZipArchiveMode.Create))
            {
                var evil = archive.CreateEntry("../../../../Windows/System32/evil.dll");
                using var s = evil.Open();
                s.Write([1, 2, 3]);
            }

            Assert.Throws<FormatException>(() => AnyArchiveExtractor.ExtractToDirectory(zipPath, destDir));
        }
        finally
        {
            File.Delete(zipPath);
            Directory.Delete(destDir, recursive: true);
        }
    }

    [Fact]
    public void ExtractToDirectory_NotARecognizedArchive_ThrowsFormatException()
    {
        var path = Path.Combine(Path.GetTempPath(), $"IcarusStarlink.Tests.{Guid.NewGuid():N}.bin");
        var destDir = CreateTempDirectory();
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8]);
            Assert.Throws<FormatException>(() => AnyArchiveExtractor.ExtractToDirectory(path, destDir));
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(destDir, recursive: true);
        }
    }

    [Fact]
    public void ExtractToDirectory_ZipEntryOverTheSizeLimit_ThrowsInsteadOfFullyDecompressingIt()
    {
        var zipPath = Path.Combine(Path.GetTempPath(), $"IcarusStarlink.Tests.{Guid.NewGuid():N}.zip");
        var destDir = CreateTempDirectory();
        try
        {
            using (var archive = new System.IO.Compression.ZipArchive(File.Create(zipPath), System.IO.Compression.ZipArchiveMode.Create))
            {
                var bigEntry = archive.CreateEntry("huge.bin", System.IO.Compression.CompressionLevel.SmallestSize);
                using var bigStream = bigEntry.Open();
                bigStream.Write(new byte[ExmodSizeLimits.MaxAssetEntryBytes + 1]);
            }

            Assert.Throws<FormatException>(() => AnyArchiveExtractor.ExtractToDirectory(zipPath, destDir));
        }
        finally
        {
            File.Delete(zipPath);
            Directory.Delete(destDir, recursive: true);
        }
    }
}
