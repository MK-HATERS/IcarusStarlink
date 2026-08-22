namespace IcarusStarlink.PakIO.Rebuild;

/// <summary>One reported step of RebuildAsync's own pipeline — Stage is a short user-facing label, PercentComplete a coarse 0-100 milestone (not a fine-grained per-file count; UnrealPak itself gives no progress callback to report finer than "packing" as a single step).</summary>
public readonly record struct RebuildStageProgress(string Stage, int PercentComplete);
