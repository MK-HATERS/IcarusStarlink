namespace IcarusStarlink.PakIO.Container;

/// <summary>Shared between ExmodzArchive and ExmodFolder — both read untrusted-ish content and should cap it the same way.</summary>
internal static class ExmodSizeLimits
{
    // Generous for compiled Unreal assets (individual .uasset/.uexp files are typically tens of
    // MB at most) while still capping how much a crafted or accidentally-huge source can force
    // us to buffer into memory in one go.
    public const long MaxAssetEntryBytes = 200_000_000;
    public const long MaxTotalAssetBytes = 1_000_000_000;
}

/// <summary>
/// Tracks a running total across a Read() call and enforces both the per-entry and total caps —
/// shared by ExmodzArchive.Read and ExmodFolder.Read so the two size checks (and their enforcement
/// order: per-entry before accumulating) only need to be implemented once.
/// </summary>
internal sealed class ExmodSizeBudget(string totalDescription)
{
    private long _total;

    public void Charge(string entryDescription, long declaredLength)
    {
        // A 7z/RAR header stores sizes as unsigned 64-bit numbers, which can read back negative:
        // charged as-is, one such entry gave the total budget back and let the rest of the
        // archive unpack far past the cap (review 2026-10-28).
        if (declaredLength < 0)
        {
            throw new FormatException($"{entryDescription} declares an impossible size ({declaredLength:N0} bytes) — refusing to read it.");
        }

        if (declaredLength > ExmodSizeLimits.MaxAssetEntryBytes)
        {
            throw new FormatException(
                $"{entryDescription} is {declaredLength:N0} bytes, exceeding the " +
                $"{ExmodSizeLimits.MaxAssetEntryBytes:N0} byte limit — refusing to read it.");
        }

        _total += declaredLength;
        if (_total > ExmodSizeLimits.MaxTotalAssetBytes)
        {
            throw new FormatException(
                $"{totalDescription} exceeds the maximum total size " +
                $"({ExmodSizeLimits.MaxTotalAssetBytes:N0} bytes) — refusing to read it.");
        }
    }
}
