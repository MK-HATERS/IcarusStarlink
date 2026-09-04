using System.Text.Json.Nodes;

namespace IcarusStarlink.Diffing;

/// <summary>One mod's own value for a field two or more queued mods both touch — ModName is display text (e.g. a LibraryEntry.Name), not a folder name/identifier.</summary>
public sealed record ConflictCandidate(string ModName, FieldChange Change);

/// <summary>
/// A single field two or more mods in the queue both change, with each mod's own candidate value —
/// the "advanced conflict picker" needs this (mod attribution), which the leaner FieldChangeGroup
/// MergeEngine.Merge itself resolves against doesn't carry. Candidates is ordered by merge-queue
/// position (index 0 = lowest priority), matching FieldChangeGroup's own convention — Merge's own
/// manualPicks parameter takes an index into that same ordering, so a picked Candidates[i] here maps
/// directly onto the pickedIndex Merge expects, as long as both are built from the same queue
/// snapshot.
/// </summary>
/// <param name="HasBaseValue">
/// True when FindConflicts was given a live base-game value for this exact field — lets the picker
/// UI show a "base game value" reference row alongside the candidates (and, for a numeric field,
/// a per-candidate buff/nerf-relative-to-base hint) without it being mistaken for another
/// candidate to pick between. False covers both "no baseTablesByFile was given at all" and "this
/// field genuinely has no base value" (e.g. a brand-new item) — matching TryGetBaseValue's own
/// "Found" convention elsewhere in this class.
/// </param>
/// <param name="BaseValue">Only meaningful when HasBaseValue is true.</param>
public sealed record FieldConflict(
    string CurrentFile,
    string ItemName,
    string FieldName,
    IReadOnlyList<ConflictCandidate> Candidates,
    bool HasBaseValue = false,
    JsonNode? BaseValue = null);
