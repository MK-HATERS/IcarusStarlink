namespace IcarusStarlink.Core.Nexus;

/// <summary>
/// One thing an activated download put somewhere: a Library entry (an EXMOD or a prebuilt pak) or
/// a staged UE4SS mod. A single Nexus download can legitimately produce SEVERAL — a real mod found
/// live (WirelessPower) ships a UE4SS Lua mod plus two asset paks in one zip laid out like the
/// game folder — so the pending entry tracks all of them, not just one.
/// </summary>
public sealed record PendingDownloadActivatedItem(string FolderName, PendingDownloadActivationKind Kind);
