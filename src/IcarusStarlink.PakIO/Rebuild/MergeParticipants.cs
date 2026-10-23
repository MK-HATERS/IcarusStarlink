namespace IcarusStarlink.PakIO.Rebuild;

/// <summary>What one merge participant is: Index is into the low-priority paks, the queued EXMOD mods or the prebuilt paks (unused for Options).</summary>
public enum MergeParticipantKind
{
    LowPriorityPak,
    Mod,
    Pak,
    Options,
}

/// <summary>One entry of the merge's participant list, lowest priority first.</summary>
public readonly record struct MergeParticipant(MergeParticipantKind Kind, int Index);

/// <summary>
/// The order every merge participant takes part in, decided in one place. A conflict pick is an
/// index into a field's candidates, counted in this order, so the build (RebuildService) and the
/// conflict check behind the badge, the review window and the saved-winner picks (Merge &amp;
/// Install) must lay participants out the same way: each used to build this list itself, and the
/// picks were only right while the two copies agreed.
/// </summary>
public static class MergeParticipants
{
    /// <summary>
    /// Low-priority paks (ArcFission Library's data pak) first, so anything queued wins over them;
    /// then the queue in its true order (QueueOrder); then the built-in gameplay options last, so
    /// they win by default. A caller leaves out a participant with no changes the same way either
    /// way — that doesn't move any candidate index, only the relative order does.
    /// </summary>
    public static IReadOnlyList<MergeParticipant> Build(
        int lowPriorityPakCount, int queuedModCount, int prebuiltPakCount, IReadOnlyList<int>? prebuiltPakQueuePositions)
    {
        var participants = new List<MergeParticipant>();
        for (var i = 0; i < lowPriorityPakCount; i++)
        {
            participants.Add(new MergeParticipant(MergeParticipantKind.LowPriorityPak, i));
        }

        foreach (var (modIndex, pakIndex) in QueueOrder(queuedModCount, prebuiltPakCount, prebuiltPakQueuePositions))
        {
            participants.Add(modIndex >= 0
                ? new MergeParticipant(MergeParticipantKind.Mod, modIndex)
                : new MergeParticipant(MergeParticipantKind.Pak, pakIndex));
        }

        participants.Add(new MergeParticipant(MergeParticipantKind.Options, -1));
        return participants;
    }

    /// <summary>
    /// The queue's true order: each prebuilt pak sits after the number of queued EXMOD mods its
    /// position says (prebuiltPakQueuePositions, one per pak; null puts every pak after the mods,
    /// the order before positions were passed). One entry per participant, lowest priority first:
    /// ModIndex into the queued mods, or PakIndex into the prebuilt paks (the other is -1).
    /// </summary>
    public static IReadOnlyList<(int ModIndex, int PakIndex)> QueueOrder(
        int queuedModCount, int prebuiltPakCount, IReadOnlyList<int>? prebuiltPakQueuePositions)
    {
        if (prebuiltPakQueuePositions is not null && prebuiltPakQueuePositions.Count != prebuiltPakCount)
        {
            throw new ArgumentException("prebuiltPakQueuePositions must have exactly one entry per prebuilt pak.", nameof(prebuiltPakQueuePositions));
        }

        var order = new List<(int ModIndex, int PakIndex)>();
        var nextPak = 0;
        int PositionOf(int pak) => prebuiltPakQueuePositions?[pak] ?? int.MaxValue;
        for (var mod = 0; mod <= queuedModCount; mod++)
        {
            // Paks are taken in their own list order; a position lower than an earlier pak's is clamped up to it.
            while (nextPak < prebuiltPakCount && (mod == queuedModCount || PositionOf(nextPak) <= mod))
            {
                order.Add((-1, nextPak++));
            }

            if (mod < queuedModCount)
            {
                order.Add((mod, -1));
            }
        }

        return order;
    }
}
