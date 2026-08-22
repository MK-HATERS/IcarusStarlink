namespace IcarusStarlink.PakIO.Safety;

/// <summary>
/// A zip entry's declared Length (from the central directory) is only a size-budget guard if the
/// actual decompressed bytes are also bounded to match — a crafted entry can declare a small
/// Length but decompress to far more, and a plain Stream.CopyTo would happily write all of it
/// before anything noticed. Used by extraction paths that stream a zip entry straight to a
/// FileStream (UE4SS mod import, UE4SS loader install) rather than buffering it in memory the way
/// ExmodzArchive.Read's own ReadExactly-into-a-fixed-buffer approach does.
/// </summary>
public static class BoundedZipEntryCopy
{
    /// <summary>Like Stream.CopyTo, but throws rather than writing past declaredLength bytes.</summary>
    public static void CopyBounded(Stream source, Stream destination, long declaredLength, string entryName)
    {
        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;
        while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            totalRead += bytesRead;
            if (totalRead > declaredLength)
            {
                throw new FormatException(
                    $"Zip entry '{entryName}' contains more data than its declared {declaredLength:N0} byte size.");
            }

            destination.Write(buffer, 0, bytesRead);
        }
    }
}
