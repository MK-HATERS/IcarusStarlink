namespace IcarusStarlink.Catalog.Nexus;

/// <summary>Mirrors Nexus's own real "status" strings ("Undecided"/"Abstained"/"Endorsed") exactly, confirmed against the official node-nexus-api client's EndorsedStatus type.</summary>
public enum NexusEndorsementStatus
{
    Undecided,
    Abstained,
    Endorsed,
}
