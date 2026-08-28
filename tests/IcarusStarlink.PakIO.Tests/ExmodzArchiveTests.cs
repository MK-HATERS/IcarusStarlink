using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests;

public class ExmodzArchiveTests
{
    private static ExmodPackageContents BuildFixture()
    {
        var package = ExmodJson.Parse("""
            {
                "name": "Faster Processors", "author": "A", "version": "1", "description": "D",
                "fileName": "Faster_Processors",
                "Rows": [
                    {"CurrentFile": "Crafting-D_ProcessorRecipes.json",
                     "File_Items": [{"Name": "SmelterRecipe", "CraftTime": 5}]}
                ]
            }
            """);

        var assets = new List<ExmodAssetEntry>
        {
            new("Faster_Processors/Icarus/Content/Data/Crafting.uasset", [1, 2, 3, 4]),
            new("Faster_Processors/Icarus/Content/Data/Crafting.uexp", [5, 6]),
            new("readme.md", "# Faster Processors"u8.ToArray()),
        };

        return new ExmodPackageContents(package, assets);
    }

    [Fact]
    public void RoundTrip_WriteThenRead_ReproducesPackageAndAssets()
    {
        var original = BuildFixture();
        using var stream = new MemoryStream();

        ExmodzArchive.Write(stream, original);
        stream.Position = 0;
        var result = ExmodzArchive.Read(stream);

        Assert.Equal(original.Package.Name, result.Package.Name);
        Assert.Equal(original.Package.FileName, result.Package.FileName);
        Assert.Single(result.Package.Rows);
        Assert.Equal(3, result.Assets.Count);
    }

    [Fact]
    public void Write_PlacesExmodUnderExtractedModsWithFileNameAsFilename()
    {
        var original = BuildFixture();
        using var stream = new MemoryStream();

        ExmodzArchive.Write(stream, original);
        stream.Position = 0;

        using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
        Assert.Contains(archive.Entries, e => e.FullName == "Extracted Mods/Faster_Processors.EXMOD");
    }

    [Fact]
    public void RoundTrip_AssetBytesArePreservedExactly()
    {
        var original = BuildFixture();
        using var stream = new MemoryStream();

        ExmodzArchive.Write(stream, original);
        stream.Position = 0;
        var result = ExmodzArchive.Read(stream);

        var uasset = Assert.Single(result.Assets, a => a.RelativePath.EndsWith(".uasset"));
        Assert.Equal<byte>([1, 2, 3, 4], uasset.Content);
    }

    [Fact]
    public void Write_ToFilePath_InvalidContents_DoesNotTruncateAPreExistingValidFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"IcarusStarlink.Tests.{Guid.NewGuid():N}.EXMODZ");
        try
        {
            ExmodzArchive.Write(path, BuildFixture());
            var originalBytes = File.ReadAllBytes(path);

            var invalidContents = new ExmodPackageContents(
                BuildFixture().Package,
                [new ExmodAssetEntry("../../evil.dll", [1, 2, 3])]);

            Assert.Throws<FormatException>(() => ExmodzArchive.Write(path, invalidContents));

            // The pre-existing valid file must survive an invalid re-save attempt untouched —
            // File.Create truncates immediately, so validation has to happen before it runs.
            Assert.Equal(originalBytes, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Write_ToFilePath_InvalidPackageContentNotJustPaths_DoesNotTruncateAPreExistingValidFile()
    {
        // Same guarantee as the path-validity case above, but for a package whose Rows/FileItems
        // content is invalid (blank CurrentFile) rather than its FileName or an asset path —
        // that failure surfaces inside ExmodJson.Serialize, after ZipArchive.CreateEntry would
        // already have started writing if EnsureWritable didn't also validate package content.
        var path = Path.Combine(Path.GetTempPath(), $"IcarusStarlink.Tests.{Guid.NewGuid():N}.EXMODZ");
        try
        {
            ExmodzArchive.Write(path, BuildFixture());
            var originalBytes = File.ReadAllBytes(path);

            var package = BuildFixture().Package;
            package.Rows.Add(new ExmodFileRow { CurrentFile = "   " });
            var invalidContents = new ExmodPackageContents(package, []);

            Assert.Throws<FormatException>(() => ExmodzArchive.Write(path, invalidContents));

            Assert.Equal(originalBytes, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Write_AssetWithUnsafePathAfterValidOnes_ThrowsBeforeProducingAPartialArchive()
    {
        var package = BuildFixture().Package;
        var contents = new ExmodPackageContents(package,
        [
            new ExmodAssetEntry("valid1.txt", [1]),
            new ExmodAssetEntry("valid2.txt", [2]),
            new ExmodAssetEntry("../../evil.dll", [3]),
        ]);

        using var stream = new MemoryStream();
        Assert.Throws<FormatException>(() => ExmodzArchive.Write(stream, contents));

        // Nothing should have been written into the archive at all — not the EXMOD entry, not
        // the valid assets that happened to come before the bad one in the list.
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void Read_ArchiveWithZipSlipEntry_ThrowsFormatExceptionInsteadOfAcceptingIt()
    {
        var exmodJson = ExmodJson.Serialize(BuildFixture().Package);
        using var stream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var exmodEntry = archive.CreateEntry("Extracted Mods/Faster_Processors.EXMOD");
            using (var s = exmodEntry.Open())
            using (var w = new StreamWriter(s))
            {
                w.Write(exmodJson);
            }

            var evilEntry = archive.CreateEntry("../../../../Windows/System32/evil.dll");
            using var evilStream = evilEntry.Open();
            evilStream.Write([1, 2, 3]);
        }
        stream.Position = 0;

        Assert.Throws<FormatException>(() => ExmodzArchive.Read(stream));
    }

    [Fact]
    public void Read_ArchiveWithDuplicateAssetEntryNames_ThrowsInsteadOfSilentlyPickingOne()
    {
        using var stream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var exmodEntry = archive.CreateEntry("Extracted Mods/Faster_Processors.EXMOD");
            using (var s = exmodEntry.Open())
            using (var w = new StreamWriter(s))
            {
                w.Write(ExmodJson.Serialize(BuildFixture().Package));
            }

            // ZipArchive permits two entries with the identical name; nothing stops a crafted
            // archive from having this even though .NET's own writer wouldn't produce it.
            foreach (var content in new byte[][] { [1], [2] })
            {
                var dup = archive.CreateEntry("dup.txt");
                using var s = dup.Open();
                s.Write(content);
            }
        }
        stream.Position = 0;

        Assert.Throws<FormatException>(() => ExmodzArchive.Read(stream));
    }

    [Fact]
    public void Read_ArchiveWithEntriesDifferingOnlyBySeparatorStyle_ThrowsAsDuplicates()
    {
        using var stream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var exmodEntry = archive.CreateEntry("Extracted Mods/Faster_Processors.EXMOD");
            using (var s = exmodEntry.Open())
            using (var w = new StreamWriter(s))
            {
                w.Write(ExmodJson.Serialize(BuildFixture().Package));
            }

            // A zip entry name may technically contain a backslash (zip readers vary on how they
            // interpret it), so this is constructible even though .NET's own writer normalizes.
            var e1 = archive.CreateEntry("a/b.txt");
            using (var s1 = e1.Open()) { s1.Write([1]); }
            var e2 = archive.CreateEntry("a\\b.txt");
            using (var s2 = e2.Open()) { s2.Write([2]); }
        }
        stream.Position = 0;

        Assert.Throws<FormatException>(() => ExmodzArchive.Read(stream));
    }

    [Fact]
    public void Read_ArchiveWithTwoExmodFiles_ThrowsFormatExceptionInsteadOfPickingOne()
    {
        var package = BuildFixture().Package;
        var exmodJson = ExmodJson.Serialize(package);
        using var stream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var path in new[] { "Extracted Mods/A.EXMOD", "Extracted Mods/B.EXMOD" })
            {
                var entry = archive.CreateEntry(path);
                using var s = entry.Open();
                using var w = new StreamWriter(s);
                w.Write(exmodJson);
            }
        }
        stream.Position = 0;

        Assert.Throws<FormatException>(() => ExmodzArchive.Read(stream));
    }

    [Fact]
    public void Read_ExmodEntryItselfOverTheSizeLimit_ThrowsInsteadOfUnboundedReadToEnd()
    {
        using var stream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            // Pad the JSON itself past the limit with a highly-compressible run of whitespace
            // inside a string value, rather than needing genuinely huge real JSON content.
            var oversizedJson = ExmodJson.Serialize(BuildFixture().Package)
                .Replace("\"description\": \"D\"", "\"description\": \"" + new string(' ', (int)(ExmodSizeLimits.MaxAssetEntryBytes + 1)) + "\"");

            var exmodEntry = archive.CreateEntry("Extracted Mods/Faster_Processors.EXMOD", System.IO.Compression.CompressionLevel.SmallestSize);
            using var s = exmodEntry.Open();
            using var w = new StreamWriter(s);
            w.Write(oversizedJson);
        }
        stream.Position = 0;

        Assert.Throws<FormatException>(() => ExmodzArchive.Read(stream));
    }

    [Fact]
    public void Read_EntryOverTheSizeLimit_ThrowsInsteadOfFullyDecompressingIt()
    {
        using var stream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var exmodEntry = archive.CreateEntry("Extracted Mods/Faster_Processors.EXMOD");
            using (var s = exmodEntry.Open())
            using (var w = new StreamWriter(s))
            {
                w.Write(ExmodJson.Serialize(BuildFixture().Package));
            }

            // All-zero payload: decompresses to just over the cap but, being maximally
            // compressible, takes almost no space or time to write — a real stand-in for a
            // decompression bomb without needing a slow test.
            var bigEntry = archive.CreateEntry("huge.bin", System.IO.Compression.CompressionLevel.SmallestSize);
            using var bigStream = bigEntry.Open();
            bigStream.Write(new byte[ExmodSizeLimits.MaxAssetEntryBytes + 1]);
        }
        stream.Position = 0;

        Assert.Throws<FormatException>(() => ExmodzArchive.Read(stream));
    }

    [Fact]
    public void Read_EntryAtExactlyTheSizeLimit_Succeeds()
    {
        using var stream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var exmodEntry = archive.CreateEntry("Extracted Mods/Faster_Processors.EXMOD");
            using (var s = exmodEntry.Open())
            using (var w = new StreamWriter(s))
            {
                w.Write(ExmodJson.Serialize(BuildFixture().Package));
            }

            var atLimitEntry = archive.CreateEntry("at_limit.bin", System.IO.Compression.CompressionLevel.SmallestSize);
            using var atLimitStream = atLimitEntry.Open();
            atLimitStream.Write(new byte[ExmodSizeLimits.MaxAssetEntryBytes]);
        }
        stream.Position = 0;

        var result = ExmodzArchive.Read(stream);
        Assert.Single(result.Assets, a => a.RelativePath == "at_limit.bin");
    }

    [Fact]
    public void Write_ThenRead_AssetPathWithBackslash_IsNormalizedToForwardSlashInTheZipEntry()
    {
        var package = BuildFixture().Package;
        var contents = new ExmodPackageContents(package, [new ExmodAssetEntry("a\\b\\c.txt", [9])]);
        using var stream = new MemoryStream();

        ExmodzArchive.Write(stream, contents);
        stream.Position = 0;

        using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
        Assert.Contains(archive.Entries, e => e.FullName == "a/b/c.txt");
        Assert.DoesNotContain(archive.Entries, e => e.FullName.Contains('\\'));
    }

    [Fact]
    public void Read_StringPath_RealZipFileRegardlessOfExtension_Succeeds()
    {
        // Downloads' Activate can't trust a Nexus file's extension — a real EXMODZ zip renamed
        // to something else (or arriving with no extension at all) must still read correctly,
        // since Read(string) now sniffs the real format from content rather than the extension.
        var path = Path.Combine(Path.GetTempPath(), $"IcarusStarlink.Tests.{Guid.NewGuid():N}.notazip");
        try
        {
            ExmodzArchive.Write(path, BuildFixture());

            var result = ExmodzArchive.Read(path);

            Assert.Equal("Faster Processors", result.Package.Name);
            Assert.Equal(3, result.Assets.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_StringPath_NotARecognizedArchive_ThrowsFormatException()
    {
        var path = Path.Combine(Path.GetTempPath(), $"IcarusStarlink.Tests.{Guid.NewGuid():N}.EXMODZ");
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8]);
            Assert.Throws<FormatException>(() => ExmodzArchive.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_ArchiveWithNoExmodFile_ThrowsFormatException()
    {
        using var stream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("readme.md");
            using var entryStream = entry.Open();
            entryStream.Write("hello"u8);
        }
        stream.Position = 0;

        Assert.Throws<FormatException>(() => ExmodzArchive.Read(stream));
    }

    [Fact]
    public void Read_Stream_CorruptZipWithNoValidCentralDirectory_ThrowsFormatExceptionNotRawInvalidDataException()
    {
        // A real local file header signature (so this looks enough like a zip to get this far)
        // with no End Of Central Directory record after it — ZipArchive's constructor throws
        // InvalidDataException for this, which callers throughout this codebase catch as
        // FormatException, matching ReadFromSharpCompress's own translation for the RAR/7z path.
        using var stream = new MemoryStream([0x50, 0x4B, 0x03, 0x04, 0x00, 0x00]);

        Assert.Throws<FormatException>(() => ExmodzArchive.Read(stream));
    }
}
