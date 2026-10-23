namespace IcarusStarlink.App.Messages;

/// <summary>Sent by NxmHandlerWatch when it finds another app holding nxm:// links (or took them back) — Settings › Nexus re-reads who has them.</summary>
public sealed record NxmHandlerChangedMessage;
