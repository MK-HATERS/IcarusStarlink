using System.Security.Cryptography;

namespace IcarusStarlink.Catalog.GitHub;

/// <summary>
/// Requires a valid SHA-256 release-asset digest before executable downloads can be used.
/// Checks integrity against GitHub metadata, not publisher safety or provenance.
/// </summary>
public static class GitHubAssetIntegrity
{
    public static async Task VerifySha256Async(string? digest, string filePath, CancellationToken cancellationToken = default)
    {
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || digest.Length != prefix.Length + 64 || !digest[prefix.Length..].All(Uri.IsHexDigit))
        {
            Reject(filePath, "Downloaded file has no valid SHA-256 integrity verification metadata — aborting.");
        }

        byte[] actualHash;
        await using (var stream = File.OpenRead(filePath))
        {
            actualHash = await SHA256.HashDataAsync(stream, cancellationToken);
        }

        if (!string.Equals(digest![prefix.Length..], Convert.ToHexString(actualHash), StringComparison.OrdinalIgnoreCase))
        {
            Reject(filePath, "Downloaded file failed integrity verification — aborting.");
        }
    }

    private static void Reject(string path, string message)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup is best-effort; rejection still prevents use of the file.
        }
        throw new InvalidOperationException(message);
    }
}
