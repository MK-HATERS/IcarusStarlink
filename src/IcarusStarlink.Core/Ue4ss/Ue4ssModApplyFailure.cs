namespace IcarusStarlink.Core.Ue4ss;

/// <summary>One mod IUe4ssModStateService.Apply couldn't move to its desired state — Apply never throws for a single mod's own failure, so every OTHER named mod in the same call still gets attempted instead of the whole batch silently aborting on the first problem.</summary>
public sealed record Ue4ssModApplyFailure(string Name, string Reason);
