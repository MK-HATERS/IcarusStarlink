using System.Security.Cryptography;

namespace IcarusStarlink.Catalog.GitHub;

/// <summary>
/// Shared SHA-256 verification for a file downloaded from a GitHub release asset, against that
/// asset's own "sha256:&lt;hex&gt;" digest field (when GitHub's API happens to include one) — used
/// by both AppUpdateClient (this app's own self-update) and the UE4SS loader install flow, so a
/// tampered/corrupted download is caught the same way regardless of which GitHub repo the asset
/// came from. Previously only the self-update path had this check; the UE4SS loader download
/// (writes a third-party DLL into the game's own Binaries\Win64 folder) had none at all.
/// </summary>
public static class GitHubAssetIntegrity
{
    /// <summary>
    /// A null digest — field absent from the response, an older cached response, or GitHub
    /// changing the shape again — is deliberately NOT a failure: this only ever hard-fails on a
    /// digest that IS present and does not match, which means the bytes just written to disk are
    /// not what GitHub says it published and must not be handed off to whatever installs them.
    /// Deletes the file before throwing so a caller can't accidentally proceed with an
    /// unverified download.
    /// </summary>
    public static async Task VerifySha256Async(string? digest, string filePath, CancellationToken cancellationToken = default)
    {
        const string sha256Prefix = "sha256:";
        if (digest is null || !digest.StartsWith(sha256Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var expectedHex = digest[sha256Prefix.Length..];

        byte[] actualHashBytes;
        await using (var fileStream = File.OpenRead(filePath))
        {
            actualHashBytes = await SHA256.HashDataAsync(fileStream, cancellationToken);
        }

        var actualHex = Convert.ToHexString(actualHashBytes);
        if (string.Equals(expectedHex, actualHex, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            File.Delete(filePath);
        }
        catch (Exception)
        {
            // Best-effort — the integrity failure below is what actually matters; a leftover temp
            // file on top of it doesn't change that this download must be rejected.
        }

        throw new InvalidOperationException("Downloaded file failed integrity verification — aborting.");
    }
}
