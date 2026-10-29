namespace IcarusStarlink.App.Services;

/// <summary>What the player saw in game after a test round.</summary>
public enum BrokenModAnswer
{
    StillBroken,
    WorksNow,
    CantTell,
}

/// <summary>How a search ended.</summary>
public enum BrokenModSearchEndKind
{
    /// <summary>Not over yet.</summary>
    None,

    /// <summary>Found: the mods in Culprits break it (one mod, or two or more that only break it together).</summary>
    Found,

    /// <summary>Still broken with none of the data mods in: not a data mod (a UE4SS mod, a merge option, or the game).</summary>
    NotADataMod,

    /// <summary>Too many "Can't tell" answers to narrow it down: it's one of Unsure.</summary>
    Unsure,
}

/// <summary>
/// Everything a search knows, saved after every step (BrokenModSearchStore) so a search survives
/// the app closing. Plain settable properties for System.Text.Json; the logic is BrokenModSearch.
/// </summary>
public sealed class BrokenModSearchState
{
    /// <summary>The mods still suspected, in test order (what a mod needs comes before it; suspects last).</summary>
    public List<string> Pool { get; set; } = [];

    /// <summary>Mods proven to be part of the cause so far: always in.</summary>
    public List<string> Found { get; set; } = [];

    /// <summary>For each mod, the other candidate mods it needs (it links to rows only they add): they always go in with it.</summary>
    public Dictionary<string, List<string>> Needs { get; set; } = [];

    /// <summary>
    /// The search looks for the smallest k where "Found + the first k of Pool" is broken. Lo: the
    /// largest k known to work (-1: none known). Hi: the smallest k known broken (Pool.Count at
    /// the start of each pass: it's broken with all of them).
    /// </summary>
    public int Lo { get; set; } = -1;

    public int Hi { get; set; }

    /// <summary>The first test of the search: every mod but the suspects. Null once used, or with no suspects.</summary>
    public int? SuspectSplit { get; set; }

    /// <summary>
    /// Right after a mod is found: test it alone (with what it needs) first. Most breaks are one
    /// mod, and that's then proven in one round instead of halving down to it.
    /// </summary>
    public bool TryFoundAlone { get; set; }

    /// <summary>Tests answered "Can't tell" in this pass: not tried again.</summary>
    public List<int> Unclear { get; set; } = [];

    /// <summary>The k being tested now (installed and waiting for an answer); null between rounds.</summary>
    public int? Testing { get; set; }

    /// <summary>Rounds answered so far (Can't tell included).</summary>
    public int Round { get; set; }

    public BrokenModSearchEndKind End { get; set; }

    /// <summary>When End is Found: the mods that break it (the order they were found in).</summary>
    public List<string> Culprits { get; set; } = [];

    /// <summary>When End is Unsure: the mods it could still be.</summary>
    public List<string> Unsure { get; set; } = [];
}

/// <summary>One test round: the mods to put in the game (what they need included), and what's tested.</summary>
/// <param name="Install">Every mod to install, in no particular order (the build keeps the queue's order).</param>
/// <param name="IsSuspectRound">The first round with suspects: every mod but them.</param>
public sealed record BrokenModTest(IReadOnlyList<string> Install, bool IsSuspectRound, int RoundsLeftAtLeast);

/// <summary>
/// Finds the mod (or the mods together) that breaks something in game, by test rounds the player
/// answers. Works on BrokenModSearchState and nothing else, so every step is deterministic and
/// testable: Start → (NextTest → install → Answer)… → End.
///
/// The idea: order the mods, then find the smallest k where the first k mods are broken; the k-th
/// mod is then part of the cause (with the first k-1 it works, with it it doesn't). It goes in for
/// good (Found), and the same search runs again on the mods before it, until Found alone is
/// broken. One culprit takes about log2(n) rounds; two that only break together take about twice
/// that. Suspects go last in the order, so the first round can be "everything but the suspects".
///
/// A mod that needs another (links to rows only it adds) is never installed without it: the order
/// puts what a mod needs before it, and every test installs what its mods need. A set that's
/// broken for a different reason (an add-on without the mod it's for) would give a wrong answer.
/// </summary>
public static class BrokenModSearch
{
    /// <param name="queueOrder">The queued mods (folder names) in queue order.</param>
    /// <param name="needs">For each mod, the queued mods it needs.</param>
    /// <param name="suspects">The mods the player's description points at (tested first).</param>
    public static BrokenModSearchState Start(IReadOnlyList<string> queueOrder, IReadOnlyDictionary<string, IReadOnlyCollection<string>> needs, IReadOnlyCollection<string> suspects)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var mods = queueOrder.Distinct(comparer).ToList();
        var inQueue = mods.ToHashSet(comparer);
        var needsInQueue = new Dictionary<string, List<string>>(comparer);
        foreach (var mod in mods)
        {
            if (needs.TryGetValue(mod, out var needed))
            {
                var list = needed.Where(n => inQueue.Contains(n) && !comparer.Equals(n, mod)).Distinct(comparer).ToList();
                if (list.Count > 0)
                {
                    needsInQueue[mod] = list;
                }
            }
        }

        var suspectSet = suspects.Where(inQueue.Contains).ToHashSet(comparer);
        var order = Order(mods, needsInQueue, suspectSet);

        // Every mod but the trailing suspects: a real split only when both sides have mods.
        var trailingSuspects = order.AsEnumerable().Reverse().TakeWhile(suspectSet.Contains).Count();
        var split = order.Count - trailingSuspects;

        return new BrokenModSearchState
        {
            Pool = order,
            Needs = needsInQueue,
            Lo = -1,
            Hi = order.Count,
            SuspectSplit = trailingSuspects > 0 && split > 0 ? split : null,
        };
    }

    /// <summary>
    /// A stable order where every mod comes after the mods it needs, suspects as late as that
    /// allows, otherwise queue order. Mods that need each other (a loop) keep queue order.
    /// </summary>
    internal static List<string> Order(IReadOnlyList<string> mods, IReadOnlyDictionary<string, List<string>> needs, IReadOnlySet<string> suspects)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var index = mods.Select((m, i) => (m, i)).ToDictionary(x => x.m, x => x.i, comparer);
        var placed = new HashSet<string>(comparer);
        var order = new List<string>();
        // Non-suspects first, then queue order.
        var byPreference = mods.OrderBy(m => suspects.Contains(m) ? 1 : 0).ThenBy(m => index[m]).ToList();
        while (order.Count < mods.Count)
        {
            var next = byPreference.FirstOrDefault(m => !placed.Contains(m)
                && (!needs.TryGetValue(m, out var needed) || needed.All(placed.Contains)));
            // A loop: nothing is free, so the most preferred mod left goes next.
            next ??= byPreference.First(m => !placed.Contains(m));

            // A suspect that a non-suspect needs lands before it, so it isn't among the trailing
            // suspects: "everything but the suspects" then still has what each of its mods needs.
            placed.Add(next);
            order.Add(next);
        }

        return order;
    }

    /// <summary>The mods plus everything they need, any depth.</summary>
    public static HashSet<string> WithWhatTheyNeed(IEnumerable<string> mods, IReadOnlyDictionary<string, List<string>> needs)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(mods);
        while (queue.Count > 0)
        {
            var mod = queue.Dequeue();
            if (!result.Add(mod))
            {
                continue;
            }

            if (needs.TryGetValue(mod, out var needed))
            {
                foreach (var n in needed)
                {
                    queue.Enqueue(n);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// The next round to install (and marks it as the one being tested), or null when the search
    /// has ended (state.End says how). Asking again before an answer gives the same round.
    /// </summary>
    public static BrokenModTest? NextTest(BrokenModSearchState state)
    {
        Normalize(state);
        if (state.End != BrokenModSearchEndKind.None)
        {
            return null;
        }

        if (state.Testing is { } testing)
        {
            return TestFor(state, testing);
        }

        while (true)
        {
            if (state.Hi - state.Lo <= 1)
            {
                // The smallest broken k is Hi.
                if (!Conclude(state, state.Hi))
                {
                    return null;
                }

                continue;
            }

            var k = PickK(state);
            if (k is null)
            {
                // Every test left was "Can't tell".
                state.End = BrokenModSearchEndKind.Unsure;
                state.Unsure = [.. state.Pool.Skip(Math.Max(state.Lo, 0)).Take(state.Hi - Math.Max(state.Lo, 0))];
                return null;
            }

            state.Testing = k;
            return TestFor(state, k.Value);
        }
    }

    /// <summary>The player's answer to the round being tested. Does nothing when no round is.</summary>
    public static void Answer(BrokenModSearchState state, BrokenModAnswer answer)
    {
        Normalize(state);
        if (state.Testing is not { } k || state.End != BrokenModSearchEndKind.None)
        {
            return;
        }

        state.Testing = null;
        state.SuspectSplit = null;
        state.TryFoundAlone = false;
        state.Round++;
        switch (answer)
        {
            case BrokenModAnswer.StillBroken:
                state.Hi = k;
                break;
            case BrokenModAnswer.WorksNow:
                state.Lo = k;
                break;
            default:
                state.Unclear.Add(k);
                break;
        }
    }

    /// <summary>
    /// The smallest broken k is known. k = 0: Found alone is broken (the end). Otherwise the k-th
    /// mod is part of the cause: it joins Found and the search goes on over the mods before it.
    /// False when the search ended.
    /// </summary>
    private static bool Conclude(BrokenModSearchState state, int k)
    {
        if (k <= 0)
        {
            state.End = state.Found.Count == 0 ? BrokenModSearchEndKind.NotADataMod : BrokenModSearchEndKind.Found;
            state.Culprits = [.. state.Found];
            return false;
        }

        state.Found.Add(state.Pool[k - 1]);
        // What a found mod needs is always in from now on: it's not a separate suspect.
        var alwaysIn = WithWhatTheyNeed(state.Found, state.Needs);
        state.Pool = [.. state.Pool.Take(k - 1).Where(m => !alwaysIn.Contains(m))];
        state.Lo = -1;
        state.Hi = state.Pool.Count;
        state.Unclear.Clear();
        state.SuspectSplit = null;
        state.TryFoundAlone = true;
        return true;
    }

    /// <summary>The k to test next: the suspect split first, then the middle; one answered "Can't tell" is skipped for the nearest other. Null when none is left.</summary>
    private static int? PickK(BrokenModSearchState state)
    {
        var allowed = Enumerable.Range(state.Lo + 1, Math.Max(0, state.Hi - state.Lo - 1)).Where(k => !state.Unclear.Contains(k)).ToList();
        if (allowed.Count == 0)
        {
            return null;
        }

        // Half in: the middle rounded up, so the first round of 40 installs 20, not 19.
        var ideal = state.TryFoundAlone && state.Lo < 0 ? 0
            : state.SuspectSplit is { } split && split > state.Lo && split < state.Hi ? split
            : state.Lo + (state.Hi - state.Lo + 1) / 2;
        return allowed.OrderBy(k => Math.Abs(k - ideal)).ThenByDescending(k => k).First();
    }

    private static BrokenModTest TestFor(BrokenModSearchState state, int k)
    {
        var install = WithWhatTheyNeed(state.Found.Concat(state.Pool.Take(k)), state.Needs);
        var isSuspectRound = state.SuspectSplit == k;
        var span = Math.Max(1, state.Hi - state.Lo);
        var roundsLeft = (int)Math.Ceiling(Math.Log2(span));
        return new BrokenModTest([.. install.Order(StringComparer.OrdinalIgnoreCase)], isSuspectRound, Math.Max(1, roundsLeft));
    }

    /// <summary>State read back from disk: case-insensitive lookups again (JSON doesn't keep the comparer).</summary>
    private static void Normalize(BrokenModSearchState state)
    {
        if (state.Needs.Comparer != StringComparer.OrdinalIgnoreCase)
        {
            state.Needs = new Dictionary<string, List<string>>(state.Needs, StringComparer.OrdinalIgnoreCase);
        }
    }
}
