using IcarusStarlink.Catalog.GitHub;

namespace IcarusStarlink.Catalog.Tests.GitHub;

public class GitHubAssetIntegrityTests
{
    // The real SHA-256 of the ASCII bytes "zip-bytes" (confirmed with `sha256sum`, not guessed) —
    // same fixture value AppUpdateClientTests already relies on for the same reason.
    private const string MatchingDigestForZipBytes = "sha256:4b9a4ac59f3c3aa32273260df6cf4bf358d1c46f8415126aa35b6380d0abb8f7";

    [Fact]
    public async Task VerifySha256Async_NullDigest_SkipsVerificationAndKeepsFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.zip");
        await File.WriteAllTextAsync(path, "zip-bytes");

        try
        {
            await GitHubAssetIntegrity.VerifySha256Async(null, path);

            Assert.True(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task VerifySha256Async_DigestMatches_KeepsFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.zip");
        await File.WriteAllTextAsync(path, "zip-bytes");

        try
        {
            await GitHubAssetIntegrity.VerifySha256Async(MatchingDigestForZipBytes, path);

            Assert.True(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task VerifySha256Async_DigestMismatch_DeletesFileAndThrows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.zip");
        await File.WriteAllTextAsync(path, "zip-bytes");
        var wrongDigest = "sha256:" + new string('0', 64);

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => GitHubAssetIntegrity.VerifySha256Async(wrongDigest, path));

            Assert.Contains("integrity verification", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
