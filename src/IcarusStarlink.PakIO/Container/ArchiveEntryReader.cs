namespace IcarusStarlink.PakIO.Container;

/// <summary>
/// Reads one untrusted archive entry's full content by its own declared length — the shape every
/// entry read in this codebase needs (ExmodzArchive.Read's own .EXMOD and asset entries,
/// PatchService.ReadEntryBytes one layer up for a patch archive), previously copied into each
/// instead of shared: charge sizeBudget BEFORE reading (rejects a decompression-bomb entry without
/// decompressing a single byte of it), then ReadExactly into a declaredLength-sized buffer (never
/// reads more than that, regardless of what the stream actually contains, catching an OVERSTATED
/// declared size), then confirm nothing is left unread (ReadExactly alone wouldn't catch an
/// UNDERSTATED declared size — it happily stops once the buffer is full).
/// </summary>
internal static class ArchiveEntryReader
{
    public static byte[] ReadExactly(string entryDescription, long declaredLength, Func<Stream> open, ExmodSizeBudget sizeBudget)
    {
        sizeBudget.Charge(entryDescription, declaredLength);

        using var stream = open();
        var buffer = new byte[declaredLength];
        try
        {
            stream.ReadExactly(buffer);
        }
        catch (EndOfStreamException ex)
        {
            throw new FormatException(
                $"{entryDescription} is corrupt — declared {declaredLength:N0} bytes but contained fewer.", ex);
        }

        if (stream.ReadByte() != -1)
        {
            throw new FormatException(
                $"{entryDescription} is corrupt — contains more data than its declared {declaredLength:N0} byte size.");
        }

        return buffer;
    }
}
