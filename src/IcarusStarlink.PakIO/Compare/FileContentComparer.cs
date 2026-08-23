namespace IcarusStarlink.PakIO.Compare;

internal static class FileContentComparer
{
    /// <summary>
    /// Byte-for-byte file equality, streamed rather than via ReadAllBytes — a single asset inside a
    /// real merged pak can be a multi-hundred-MB .ubulk texture, which two whole-file byte arrays
    /// would double up in memory at once.
    /// </summary>
    public static bool AreIdentical(string firstPath, string secondPath)
    {
        var firstInfo = new FileInfo(firstPath);
        var secondInfo = new FileInfo(secondPath);
        if (firstInfo.Length != secondInfo.Length)
        {
            return false;
        }

        using var firstStream = File.OpenRead(firstPath);
        using var secondStream = File.OpenRead(secondPath);
        var firstBuffer = new byte[81920];
        var secondBuffer = new byte[81920];
        while (true)
        {
            var firstRead = firstStream.ReadAtLeast(firstBuffer, firstBuffer.Length, throwOnEndOfStream: false);
            var secondRead = secondStream.ReadAtLeast(secondBuffer, secondBuffer.Length, throwOnEndOfStream: false);
            if (firstRead != secondRead || !firstBuffer.AsSpan(0, firstRead).SequenceEqual(secondBuffer.AsSpan(0, secondRead)))
            {
                return false;
            }

            if (firstRead == 0)
            {
                return true;
            }
        }
    }
}
