using System;
using System.Collections.Generic;
using System.Linq;

namespace FlatMap.Layout;

// PURE, game-independent map-layout core. No Godot, no sts2 — just graph -> lane-per-node.
// This is where the "flatten the minimap" logic lives so it can be exhaustively unit-tested
// offline (see layout/Program.cs) without launching the game.
//
// Model: a layered graph. Row = map floor/depth (the game truth, kept as the X axis and NEVER
// changed). Col = the game's original lane, used ONLY to seed the vertical order we preserve.
// The layout assigns each node a display LANE (Y). We never touch edges, so connectivity — the
// set of moves a vanilla player has — is untouchable by construction. The one hazard is overlap
// (two nodes at the same spot could read as one room and hide a choice), so the hard invariant
// is: within a row, lanes are distinct AND in the same order as col.

public readonly struct LNode
{
    public readonly int Id;   // dense 0..N-1
    public readonly int Row;  // layer / floor
    public readonly int Col;  // original lane (seed for within-row order)
    public LNode(int id, int row, int col) { Id = id; Row = row; Col = col; }
}

public sealed class LGraph
{
    public readonly LNode[] Nodes;
    public readonly (int From, int To)[] Edges; // parent (lower row) -> child (higher row), by Id
    public readonly int[][] NeighborsOf;        // id -> parent+child ids
    public readonly int[][] RowsOrdered;        // compact row index -> node ids sorted by (Col, Id)
    public readonly int[] RowOf;                // id -> compact row index
    public readonly int RowCount;

    public LGraph(IReadOnlyList<LNode> nodes, IReadOnlyList<(int From, int To)> edges)
    {
        Nodes = nodes.OrderBy(n => n.Id).ToArray();
        for (int i = 0; i < Nodes.Length; i++)
            if (Nodes[i].Id != i)
                throw new ArgumentException("LGraph node Ids must be dense 0..N-1.");
        Edges = edges.ToArray();

        foreach ((int f, int t) in Edges)
        {
            if (f < 0 || f >= Nodes.Length || t < 0 || t >= Nodes.Length)
                throw new ArgumentException($"Edge references a missing node: ({f}->{t}).");
            if (Nodes[f].Row >= Nodes[t].Row)
                throw new ArgumentException($"Edge must go to a higher row: ({f}->{t}).");
        }

        var neigh = new List<int>[Nodes.Length];
        for (int i = 0; i < neigh.Length; i++) neigh[i] = new List<int>();
        foreach ((int f, int t) in Edges) { neigh[f].Add(t); neigh[t].Add(f); }
        NeighborsOf = neigh.Select(l => l.Distinct().ToArray()).ToArray();

        int[] rowValues = Nodes.Select(n => n.Row).Distinct().OrderBy(r => r).ToArray();
        RowCount = rowValues.Length;
        var rowIndex = new Dictionary<int, int>();
        for (int i = 0; i < rowValues.Length; i++) rowIndex[rowValues[i]] = i;
        RowOf = Nodes.Select(n => rowIndex[n.Row]).ToArray();
        RowsOrdered = new int[RowCount][];
        for (int r = 0; r < RowCount; r++)
            RowsOrdered[r] = Nodes.Where(n => rowIndex[n.Row] == r)
                                  .OrderBy(n => n.Col).ThenBy(n => n.Id)
                                  .Select(n => n.Id).ToArray();
    }

    public int[] BaselineLanes() => Nodes.Select(n => n.Col).ToArray();

    // First row of a trailing aligned single-node boss spine (e.g. row 16 for a single boss, or
    // Boss 1 when a second boss sits directly above it on row 17 at the same column). Returns -1
    // if the final row is not a single node or the graph has fewer than two rows.
    public int BossSpineStartRow()
    {
        if (RowCount < 2 || RowsOrdered[RowCount - 1].Length != 1) return -1;
        int r = RowCount - 1;
        while (r > 1 && RowsOrdered[r - 1].Length == 1
               && Nodes[RowsOrdered[r - 1][0]].Col == Nodes[RowsOrdered[r][0]].Col)
            r--;
        return r;
    }
}

public static class MapLayout
{
    // Assign a display lane to every node. Legal-by-construction (see the invariant checker). Every
    // step preserves each row's column order, so the crossing count is invariant (== baseline) —
    // meaning we can minimize LANES and edge length freely without ever adding a crossing.
    public static int[] AssignLanes(LGraph g) => OptimalLayout.TryAssign(g) ?? AssignLanesLegacy(g);

    // Continuous minimum-bending-energy horizontal coordinate assignment (2026-11-19).
    // Solves a strictly convex problem (vertical-rail pseudo-Huber slope + Euler-Bernoulli
    // second-derivative curvature + gentle centering) projected onto the exact non-overlapping
    // within-row column-order polytope via Pool-Adjacent-Violators. Preserves every hard invariant
    // (zero overlap, exact vanilla crossing count, aligned Start/Boss/SecondBoss spine) while
    // eliminating integer-grid sawtooth spikes and lopsided forks.
    public static double[] AssignSmoothX(LGraph g) => ContinuousMapLayout.Solve(g);

    // The pre-2026-11-16 greedy pipeline (hill-climb + lane-merge + candidate ladder). Kept as the
    // fallback for graphs the exact optimizer does not model (an edge that skips a floor) and as a
    // comparison option in the viz tooling.
    public static int[] AssignLanesLegacy(LGraph g)
    {
        // Candidate 1 — alignment-first: start from the game's columns, straighten, and merge any
        // cleanly-clearable lanes. Tends to keep the familiar shape; may leave lanes uncleared.
        int[] aligned = g.BaselineLanes();
        Normalize(g, aligned);
        for (int i = 0; i < 64; i++)
        {
            HillClimb(g, aligned, int.MaxValue);
            if (!TryMergeLane(g, aligned)) break;
        }
        Compact(aligned);

        // Candidate 2 — compactness-first: pack every floor's rooms into the fewest lanes (= the
        // widest floor), then straighten WITHIN that lane budget so it can't re-expand.
        int[] packed = MinPack(g);
        HillClimb(g, packed, packed.Max());
        Compact(packed);

        // Also build a gentle slope-1-body layout (no mid-map spikes), then pick the best of all three
        // candidates by a strict priority ladder (see Better): no steep edges -> fewest lanes -> no
        // one-floor exterior spikes -> fewest path bends -> shortest travel. Crossings are equal for
        // all, so readability of the connections is never traded away.
        int[] gentle = AssignLanesMaxSlope(g, 1);
        int[] best = aligned;
        foreach (int[] c in new[] { packed, gentle })
            if (Better(g, c, best)) best = c;
        PinEnds(g, best);
        return best;
    }

    // Place the start (row 0) and boss (last row) near the centre line — the map reads as entering
    // mid-left and exiting mid-right — but NEVER at the cost of the fan's direction signature (see
    // PinEnd). Both are single-node rows, so any lane is legal (no same-row neighbour to overlap)
    // and their fan-out/converge edges are already exempt from the slope rules, so this changes no
    // body metric. Skipped for a multi-node extreme row (e.g. a two-boss floor) to avoid
    // overlapping them. Re-Compact to drop any lane this emptied.
    private static void PinEnds(LGraph g, int[] lane)
    {
        if (g.RowsOrdered[0].Length == 1) PinEnd(g, lane, g.RowsOrdered[0][0], isStart: true);
        int bossRow = g.BossSpineStartRow();
        if (bossRow > 0)
        {
            int bossId = g.RowsOrdered[bossRow][0];
            PinEnd(g, lane, bossId, isStart: false);
            for (int r = bossRow + 1; r < g.RowCount; r++)
                lane[g.RowsOrdered[r][0]] = lane[bossId];
        }
        Compact(lane);
    }

    // FAN-DIRECTION RULE (2026-08-02): the raw game columns give each start fan-out edge (and each
    // boss converge-in edge) a direction — leftish, straight, or rightish of the end node. Every
    // direction class present in the raw columns must still be represented after compression: a
    // "\|/" start must never read "//" or "\\|" (the map would feel unbalanced, promising choices
    // in a direction that doesn't exist or hiding a real spread). So the end node's lane is chosen
    // to lose as few baseline direction classes as possible (0 whenever the children's lanes allow
    // an integer strictly between the left and right groups, or level with the straight child),
    // and only among those to sit nearest the centre line. When full preservation is impossible
    // (a "\/" whose children collapsed onto adjacent lanes), failures are graded per edge: a
    // flattened direction ("\" read as "|") costs 1, an outright flip ("\" read as "/") costs 2 —
    // so the least-bad "\|" / "|/" always beats the unbalanced "\\" / "//".
    private static void PinEnd(LGraph g, int[] lane, int s, bool isStart)
    {
        int min = lane.Min(), max = lane.Max();
        int center2 = min + max;
        int r = g.RowOf[s];
        int[] kids = g.NeighborsOf[s].Where(k => isStart ? g.RowOf[k] > r : g.RowOf[k] < r).ToArray();
        if (kids.Length == 0) return;
        int bestL = lane[s];
        (int missing, int graded, int offCenter, int l) bestKey =
            (int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue);
        for (int L = min; L <= max; L++)
        {
            int graded = kids.Sum(k =>
            {
                int b = LayoutMetrics.FanDir(g, s, k), a = Math.Sign(lane[k] - L);
                return b == a ? 0 : b == -a && b != 0 ? 2 : 1;
            });
            (int, int, int, int) key =
                (LayoutMetrics.MissingFanClasses(g, lane, s, L, isStart), graded, Math.Abs(2 * L - center2), L);
            if (key.CompareTo(bestKey) < 0) { bestKey = key; bestL = L; }
        }
        lane[s] = bestL;
    }

    // Lexicographic layout preference (all lower = better): (1) no steep body edges; (2) fewest
    // lanes; (3) fewest unpreservable fan-direction classes (see PinEnd — a candidate whose
    // first-floor spread still lets the start sit between its leftish and rightish children beats
    // one that collapsed them onto adjacent lanes); (4) fewest one-floor spikes in the outside
    // silhouette; (5) fewest path bends; then (6) shortest total vertical travel.
    private static bool Better(LGraph g, int[] x, int[] best)
    {
        int sx = LayoutMetrics.SteepBodyEdges(g, x), sb = LayoutMetrics.SteepBodyEdges(g, best);
        if (sx != sb) return sx < sb;
        int lx = LayoutMetrics.LanesUsed(x), lb = LayoutMetrics.LanesUsed(best);
        if (lx != lb) return lx < lb;
        int fx = LayoutMetrics.FanClassesLost(g, x), fb = LayoutMetrics.FanClassesLost(g, best);
        if (fx != fb) return fx < fb;
        int ox = LayoutMetrics.ExteriorSpikeCount(g, x), ob = LayoutMetrics.ExteriorSpikeCount(g, best);
        if (ox != ob) return ox < ob;
        int bx = LayoutMetrics.BendCount(g, x), bb = LayoutMetrics.BendCount(g, best);
        if (bx != bb) return bx < bb;
        return LayoutMetrics.VerticalEdgeLength(g, x) < LayoutMetrics.VerticalEdgeLength(g, best);
    }

    // Pack each floor's rooms into the fewest lanes (= the widest floor), CENTERED on the midline by
    // column order — so narrow floors sit in the middle and the whole map is balanced top-to-bottom
    // instead of hugging the top. Legal by construction (distinct + col-ordered per row); the lane
    // count is unchanged (still = widest floor). A constant shift to non-negative changes nothing
    // about the shape (the drawing normalizes the range) but keeps HillClimb's [0, cap] bounds valid.
    private static int[] MinPack(LGraph g)
    {
        var lane = new int[g.Nodes.Length];
        foreach (int[] row in g.RowsOrdered)
            for (int i = 0; i < row.Length; i++) lane[row[i]] = i - (row.Length - 1) / 2;
        int min = lane.Min();
        if (min != 0) for (int i = 0; i < lane.Length; i++) lane[i] -= min;
        return lane;
    }

    // Merge two vertically-adjacent lanes (a, a+1) when NO row uses both. It's a monotonic relabel
    // (everything above a shifts down one), so per-row order is preserved (no new crossing) and no
    // edge lengthens (a boundary-crossing edge only shortens). It removes one lane — this is what
    // "completely clear out a row" needs. Returns true if a merge happened.
    private static bool TryMergeLane(LGraph g, int[] lane)
    {
        int min = lane.Min(), max = lane.Max();
        for (int a = min; a < max; a++)
        {
            bool conflict = false;
            foreach (int[] row in g.RowsOrdered)
            {
                bool hasA = false, hasB = false;
                foreach (int id in row)
                {
                    if (lane[id] == a) hasA = true;
                    else if (lane[id] == a + 1) hasB = true;
                }
                if (hasA && hasB) { conflict = true; break; }
            }
            if (conflict) continue;
            for (int id = 0; id < lane.Length; id++)
                if (lane[id] > a) lane[id]--;
            return true;
        }
        return false;
    }

    // Make each row strictly increasing by col order (guarantees the hard invariant as a start).
    private static void Normalize(LGraph g, int[] lane)
    {
        foreach (int[] row in g.RowsOrdered)
            for (int i = 1; i < row.Length; i++)
                if (lane[row[i]] <= lane[row[i - 1]])
                    lane[row[i]] = lane[row[i - 1]] + 1;
    }

    // Shorten edges by shifting rigid same-lane runs / single nodes ±1, never leaving [0, laneCap]
    // (the cap keeps the compact candidate from re-expanding; pass int.MaxValue for unbounded).
    // maxSlope caps how many lanes a single edge may span between adjacent floors (the vanilla map
    // only ever steps one column, i.e. slope 1). int.MaxValue = unconstrained (the default path).
    private static void HillClimb(LGraph g, int[] lane, int laneCap, int maxSlope = int.MaxValue)
    {
        // Center line (2x, to stay in integers) — the midpoint of the initial lane range. We pull the
        // layout toward it so paths sit around the middle instead of drifting to the top/bottom edge.
        int center2 = lane.Min() + lane.Max();

        // Bounded by a lexicographic potential (edge length, then bend count, then distance-from-
        // center) that strictly decreases each accepted move; the cap + guard are safety nets.
        for (int guard = 0; guard < 100_000; guard++)
        {
            bool improved = false;

            // Candidate moves, biggest rigid runs first, then single nodes. A same-lane run is a
            // connected set of nodes all currently sharing a lane; shifting it keeps it flat.
            var candidates = new List<int[]>();
            candidates.AddRange(SameLaneRuns(g, lane).Where(s => s.Length >= 2)
                .OrderByDescending(s => s.Length)
                .ThenBy(s => g.Nodes[s[0]].Row).ThenBy(s => g.Nodes[s[0]].Col));
            candidates.AddRange(g.Nodes.OrderBy(n => n.Row).ThenBy(n => n.Col).Select(n => new[] { n.Id }));

            foreach (int[] set in candidates)
            {
                foreach (int delta in new[] { 1, -1 })
                {
                    bool inBounds = set.All(id => lane[id] + delta >= 0 && lane[id] + delta <= laneCap);
                    if (!inBounds || !IsLegalShift(g, lane, set, delta) || !SlopeOk(g, lane, set, delta, maxSlope)) continue;
                    int edgeDelta = EdgeLengthDelta(g, lane, set, delta);
                    bool accept;
                    if (edgeDelta < 0)
                    {
                        accept = true; // shortens edges — always good (fewer/smaller angles)
                    }
                    else if (edgeDelta == 0)
                    {
                        // Edge-neutral (an outer diagonal traded for an inner one): first smooth the
                        // map's exterior silhouette. This deliberately permits an adjacent outer node
                        // to move away from center and turn a one-floor spike into a short outer run.
                        // Then prefer fewer path bends, and only then tighter centering. Scored by
                        // apply-measure-revert (graphs are tiny).
                        int lanesBefore = LayoutMetrics.LanesUsed(lane);
                        int spikeBefore = LayoutMetrics.ExteriorSpikeCount(g, lane);
                        int bendBefore = LayoutMetrics.BendCount(g, lane);
                        int centerBefore = CenterTotal(lane, center2);
                        foreach (int id in set) lane[id] += delta;
                        int lanesAfter = LayoutMetrics.LanesUsed(lane);
                        int spikeAfter = LayoutMetrics.ExteriorSpikeCount(g, lane);
                        int bendAfter = LayoutMetrics.BendCount(g, lane);
                        int centerAfter = CenterTotal(lane, center2);
                        foreach (int id in set) lane[id] -= delta;
                        accept = (lanesAfter <= lanesBefore && spikeAfter < spikeBefore)
                            || (spikeAfter == spikeBefore && (bendAfter < bendBefore
                                || (bendAfter == bendBefore && centerAfter < centerBefore)));
                    }
                    else accept = false;
                    if (accept)
                    {
                        foreach (int id in set) lane[id] += delta;
                        improved = true;
                        break;
                    }
                }
                if (improved) break; // re-evaluate runs from scratch after any change
            }
            if (!improved) return;
        }
    }

    // Total distance-from-center (2x units) over all nodes — lower = layout hugs the middle line more.
    private static int CenterTotal(int[] lane, int center2)
    {
        int d = 0;
        foreach (int l in lane) d += Math.Abs(2 * l - center2);
        return d;
    }

    // Connected components over edges whose two endpoints currently share a lane.
    private static List<int[]> SameLaneRuns(LGraph g, int[] lane)
    {
        int n = g.Nodes.Length;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }

        foreach ((int f, int t) in g.Edges)
            if (lane[f] == lane[t]) Union(f, t);

        var groups = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            int r = Find(i);
            if (!groups.TryGetValue(r, out List<int>? list)) groups[r] = list = new List<int>();
            list.Add(i);
        }
        // Canonical order (each run keyed by its smallest (Row,Col) member; members likewise sorted)
        // so the greedy hill-climb explores identically regardless of node Id/enumeration order. The
        // layout MUST be a pure function of the graph, not of how its nodes happened to be listed —
        // otherwise the game (point-dictionary order) and the offline harness diverge. Proven by the
        // order-dependence sweep in layout/Program.cs.
        return groups.Values
            .Select(l => l.OrderBy(id => g.Nodes[id].Row).ThenBy(id => g.Nodes[id].Col).ToArray())
            .OrderBy(a => g.Nodes[a[0]].Row).ThenBy(a => g.Nodes[a[0]].Col)
            .ToList();
    }

    // Would shifting every node in `set` by `delta` keep the layout legal? (No overlap and no
    // col-order inversion within any affected row, checked against the non-moving nodes.)
    private static bool IsLegalShift(LGraph g, int[] lane, int[] set, int delta)
    {
        var moving = new HashSet<int>(set);
        foreach (int u in set)
        {
            int newU = lane[u] + delta;
            int r = g.RowOf[u];
            foreach (int v in g.RowsOrdered[r])
            {
                if (moving.Contains(v)) continue; // moves together -> relative order preserved
                if (newU == lane[v]) return false; // overlap
                if (Math.Sign(g.Nodes[u].Col - g.Nodes[v].Col) != Math.Sign(newU - lane[v]))
                    return false;                  // would pass v -> order inversion
            }
        }
        return true;
    }

    // Change in total vertical edge length if `set` shifts by `delta`. Edges fully inside the set
    // don't change; only boundary edges do.
    private static int EdgeLengthDelta(LGraph g, int[] lane, int[] set, int delta)
    {
        var moving = new HashSet<int>(set);
        int d = 0;
        foreach ((int a, int b) in g.Edges)
        {
            bool am = moving.Contains(a), bm = moving.Contains(b);
            if (am == bm) continue;
            int la = lane[a] + (am ? delta : 0);
            int lb = lane[b] + (bm ? delta : 0);
            d += Math.Abs(la - lb) - Math.Abs(lane[a] - lane[b]);
        }
        return d;
    }

    // Would shifting `set` by `delta` leave every edge spanning at most `maxSlope` lanes? Only
    // boundary edges (one endpoint moving) can change; int.MaxValue disables the check.
    private static bool SlopeOk(LGraph g, int[] lane, int[] set, int delta, int maxSlope)
    {
        if (maxSlope >= int.MaxValue) return true;
        int bossRow = g.BossSpineStartRow();
        int bossFloor = bossRow > 0 ? bossRow : g.RowsOrdered.Length - 1;
        var moving = new HashSet<int>(set);
        foreach ((int a, int b) in g.Edges)
        {
            bool am = moving.Contains(a), bm = moving.Contains(b);
            if (am == bm) continue;
            // Start (row 0) fans out and the boss spine converges — those edges are inherently
            // steep (one node <-> many), so they're exempt from the slope cap.
            if (g.RowOf[a] == 0 || g.RowOf[b] == 0 || g.RowOf[a] >= bossFloor || g.RowOf[b] >= bossFloor)
                continue;
            int la = lane[a] + (am ? delta : 0), lb = lane[b] + (bm ? delta : 0);
            if (Math.Abs(la - lb) > maxSlope) return false;
        }
        return true;
    }

    // EXPERIMENTAL variant: compact while forbidding any edge steeper than `maxSlope` lanes/floor
    // (maxSlope 1 = the vanilla "only step one column" shape — no diagonal-2 jumps). Starts from the
    // game's own columns (already slope-1) and only straightens/merges within that cap, so it stays
    // feasible. Trades some lane-compression for the gentler vanilla slope.
    public static int[] AssignLanesMaxSlope(LGraph g, int maxSlope)
    {
        int[] lane = g.BaselineLanes();
        for (int i = 0; i < 64; i++)
        {
            HillClimb(g, lane, int.MaxValue, maxSlope);
            if (!TryMergeLane(g, lane)) break;
        }
        Compact(lane);
        return lane;
    }

    // Remove globally-unused lanes by remapping used lane values to consecutive ranks. Monotonic,
    // so it preserves all orders and never lengthens an edge (rank gaps <= value gaps).
    private static void Compact(int[] lane)
    {
        int[] used = lane.Distinct().OrderBy(v => v).ToArray();
        var rank = new Dictionary<int, int>();
        for (int i = 0; i < used.Length; i++) rank[used[i]] = i;
        for (int i = 0; i < lane.Length; i++) lane[i] = rank[lane[i]];
    }
}

// Hard safety checks. A layout is legal iff every node is placed and, within each row, lanes are
// distinct and in the same order as col. That's exactly "no two rooms overlap" + "we never
// reorder / imply a move that isn't real". Returns the list of violations (empty == legal).
public static class LayoutInvariants
{
    public static List<string> Check(LGraph g, int[] lane)
    {
        var violations = new List<string>();
        if (lane.Length != g.Nodes.Length)
            violations.Add($"lane count {lane.Length} != node count {g.Nodes.Length}");

        for (int r = 0; r < g.RowCount; r++)
        {
            int[] row = g.RowsOrdered[r];
            for (int i = 1; i < row.Length; i++)
            {
                int a = row[i - 1], b = row[i]; // ordered by col ascending
                if (lane[a] == lane[b])
                    violations.Add($"overlap in row {r}: nodes {a},{b} both at lane {lane[a]}");
                else if (lane[a] > lane[b])
                    violations.Add($"col-order inverted in row {r}: node {a}(col {g.Nodes[a].Col}) " +
                                   $"at lane {lane[a]} above node {b}(col {g.Nodes[b].Col}) at lane {lane[b]}");
            }
        }
        return violations;
    }

    public static bool IsLegal(LGraph g, int[] lane) => Check(g, lane).Count == 0;

    // Hard safety checks for continuous horizontal coordinates (AssignSmoothX): every node is
    // placed at a finite x, and within each row consecutive nodes in vanilla column order are
    // separated by at least `minGap` (default 1.0 lane unit minus numerical tolerance), guaranteeing
    // zero overlap and exact crossing invariance.
    public static List<string> CheckContinuous(LGraph g, double[] x, double minGap = 1.0 - 1e-5)
    {
        var violations = new List<string>();
        if (x.Length != g.Nodes.Length)
        {
            violations.Add($"x count {x.Length} != node count {g.Nodes.Length}");
            return violations;
        }
        for (int i = 0; i < x.Length; i++)
            if (!double.IsFinite(x[i]))
                violations.Add($"non-finite x[{i}] = {x[i]}");

        for (int r = 0; r < g.RowCount; r++)
        {
            int[] row = g.RowsOrdered[r];
            for (int i = 1; i < row.Length; i++)
            {
                int a = row[i - 1], b = row[i];
                double gap = x[b] - x[a];
                if (gap < minGap)
                    violations.Add($"row {r}: nodes {a}(col {g.Nodes[a].Col}, x={x[a]:F4}) and " +
                                   $"{b}(col {g.Nodes[b].Col}, x={x[b]:F4}) violate separation (gap={gap:F4} < {minGap:F4})");
            }
        }
        return violations;
    }

    public static bool IsLegalContinuous(LGraph g, double[] x) => CheckContinuous(g, x).Count == 0;
}

public static class LayoutMetrics
{
    // Total vertical edge length — our zigzag proxy (a straight sub-path costs 0).
    public static int VerticalEdgeLength(LGraph g, int[] lane) =>
        g.Edges.Sum(e => Math.Abs(lane[e.From] - lane[e.To]));

    // Distinct lanes in use — the drawing's height.
    public static int LanesUsed(int[] lane) => lane.Distinct().Count();

    // Count sharp one-floor tips in the map's outside silhouette. On the left, a row is a spike
    // when its leftmost node sits farther out than both neighboring rows; the right side is
    // symmetric. Extending either neighbor into the same outer lane removes the tip and produces
    // the calmer short outer run seen in vanilla maps. Start/boss rows are excluded because their
    // single-node fan-out/convergence is structural.
    public static int ExteriorSpikeCount(LGraph g, int[] lane)
    {
        if (g.RowCount < 5) return 0;
        var min = new int[g.RowCount];
        var max = new int[g.RowCount];
        for (int r = 0; r < g.RowCount; r++)
        {
            min[r] = g.RowsOrdered[r].Min(id => lane[id]);
            max[r] = g.RowsOrdered[r].Max(id => lane[id]);
        }

        int spikes = 0;
        for (int r = 2; r < g.RowCount - 2; r++)
        {
            if (min[r] < min[r - 1] && min[r] < min[r + 1]) spikes++;
            if (max[r] > max[r - 1] && max[r] > max[r + 1]) spikes++;
        }
        return spikes;
    }

    // Steepest single edge (lanes spanned between adjacent floors). 1 == the gentle vanilla shape.
    public static int MaxEdgeSlope(LGraph g, int[] lane) =>
        g.Edges.Length == 0 ? 0 : g.Edges.Max(e => Math.Abs(lane[e.From] - lane[e.To]));

    // Count of steep BODY edges (2+ lanes between floors), excluding the inherently-steep start
    // fan-out (row 0) and boss converge-in (the boss spine, including two-boss acts).
    public static int SteepBodyEdges(LGraph g, int[] lane)
    {
        int bossRow = g.BossSpineStartRow();
        int bossFloor = bossRow > 0 ? bossRow : g.RowCount - 1;
        return g.Edges.Count(e =>
            g.RowOf[e.From] != 0 && g.RowOf[e.To] != 0 && g.RowOf[e.From] < bossFloor && g.RowOf[e.To] < bossFloor
            && Math.Abs(lane[e.From] - lane[e.To]) >= 2);
    }

    // Number of BENDS — slope CHANGES along straight-through (one-in, one-out) rooms. A smooth
    // diagonal or a single drop-then-flat has few bends; a \/^\_ wiggle has many. Fork/merge rooms
    // are skipped (their direction change is structural, not an avoidable kink). This is our proxy
    // for "how jagged does a path look", independent of total vertical travel.
    public static int BendCount(LGraph g, int[] lane)
    {
        int n = g.Nodes.Length;
        var indeg = new int[n];
        var outdeg = new int[n];
        var inOf = new int[n];
        var outOf = new int[n];
        foreach ((int f, int t) in g.Edges) { outdeg[f]++; outOf[f] = t; indeg[t]++; inOf[t] = f; }
        int bends = 0;
        for (int i = 0; i < n; i++)
            if (indeg[i] == 1 && outdeg[i] == 1 && lane[i] - lane[inOf[i]] != lane[outOf[i]] - lane[i])
                bends++;
        return bends;
    }

    // A fan edge's direction in the raw game columns: is neighbor k leftish (-1), straight (0),
    // or rightish (+1) of end node s? THE definition the whole fan-direction rule builds on.
    internal static int FanDir(LGraph g, int s, int k) =>
        Math.Sign(g.Nodes[k].Col - g.Nodes[s].Col);

    // If end node s sat at lane L, how many of its baseline fan-direction classes would have NO
    // representative among its fan-side neighbors' current lanes? 0 = full "\|/" signature preserved.
    internal static int MissingFanClasses(LGraph g, int[] lane, int s, int L, bool isStart)
    {
        int r = g.RowOf[s];
        int[] kids = g.NeighborsOf[s].Where(k => isStart ? g.RowOf[k] > r : g.RowOf[k] < r).ToArray();
        var now = kids.Select(k => Math.Sign(lane[k] - L)).ToHashSet();
        return kids.Select(k => FanDir(g, s, k)).Distinct().Count(b => !now.Contains(b));
    }

    // How many baseline fan-direction classes are UNPRESERVABLE at the map's single-node ends,
    // i.e. lost even under the best possible end-node lane (children as placed by `lane`; the end
    // node's own lane is NOT read — PinEnd chooses it last). A class is unpreservable when no
    // in-range integer lane for the end node reproduces it. 0 = the layout can keep the raw map's
    // full "\|/" spread at both ends.
    public static int FanClassesLost(LGraph g, int[] lane)
    {
        int lost = 0, min = lane.Min(), max = lane.Max();
        int bossRow = g.BossSpineStartRow();
        var endRows = new List<(int Row, bool IsStart)>();
        if (g.RowCount >= 2 && g.RowsOrdered[0].Length == 1) endRows.Add((0, true));
        if (bossRow > 0) endRows.Add((bossRow, false));
        foreach ((int r, bool isStart) in endRows)
        {
            int s = g.RowsOrdered[r][0];
            int bestMissing = int.MaxValue;
            for (int L = min; L <= max && bestMissing > 0; L++)
                bestMissing = Math.Min(bestMissing, MissingFanClasses(g, lane, s, L, isStart));
            lost += bestMissing;
        }
        return lost;
    }

    // Edge crossings between edges that span the same pair of rows (readability, report-only).
    public static int Crossings(LGraph g, int[] lane)
    {
        int c = 0;
        for (int i = 0; i < g.Edges.Length; i++)
            for (int j = i + 1; j < g.Edges.Length; j++)
            {
                var (a, b) = g.Edges[i];
                var (x, y) = g.Edges[j];
                if (g.RowOf[a] != g.RowOf[x] || g.RowOf[b] != g.RowOf[y]) continue;
                if (Math.Sign(lane[a] - lane[x]) * Math.Sign(lane[b] - lane[y]) < 0) c++;
            }
        return c;
    }

    public static int ContinuousCrossings(LGraph g, double[] x)
    {
        int c = 0;
        for (int i = 0; i < g.Edges.Length; i++)
            for (int j = i + 1; j < g.Edges.Length; j++)
            {
                var (a, b) = g.Edges[i];
                var (u, v) = g.Edges[j];
                if (g.RowOf[a] != g.RowOf[u] || g.RowOf[b] != g.RowOf[v]) continue;
                if (Math.Sign(x[a] - x[u]) * Math.Sign(x[b] - x[v]) < 0) c++;
            }
        return c;
    }

    // Mean angular turn (in degrees) across all straight-through (in=1, out=1) rooms at a given
    // lane-to-row pixel aspect ratio. Directly measures perceived jaggedness along paths.
    public static double MeanThroughTurnDeg(LGraph g, double[] x, double aspectRatio = 1.60)
    {
        int n = g.Nodes.Length;
        var indeg = new int[n];
        var outdeg = new int[n];
        var inOf = new int[n];
        var outOf = new int[n];
        foreach ((int f, int t) in g.Edges) { outdeg[f]++; outOf[f] = t; indeg[t]++; inOf[t] = f; }
        double sum = 0.0;
        int count = 0;
        for (int i = 0; i < n; i++)
        {
            if (indeg[i] != 1 || outdeg[i] != 1) continue;
            int dr1 = Math.Max(1, g.RowOf[i] - g.RowOf[inOf[i]]);
            int dr2 = Math.Max(1, g.RowOf[outOf[i]] - g.RowOf[i]);
            double a1 = Math.Atan2((x[i] - x[inOf[i]]) * aspectRatio, dr1) * (180.0 / Math.PI);
            double a2 = Math.Atan2((x[outOf[i]] - x[i]) * aspectRatio, dr2) * (180.0 / Math.PI);
            sum += Math.Abs(a2 - a1);
            count++;
        }
        return count == 0 ? 0.0 : sum / count;
    }

    // Total discrete second-derivative bending energy over straight-through chains.
    public static double ThroughBendEnergy(LGraph g, double[] x)
    {
        int n = g.Nodes.Length;
        var indeg = new int[n];
        var outdeg = new int[n];
        var inOf = new int[n];
        var outOf = new int[n];
        foreach ((int f, int t) in g.Edges) { outdeg[f]++; outOf[f] = t; indeg[t]++; inOf[t] = f; }
        double sum = 0.0;
        for (int i = 0; i < n; i++)
        {
            if (indeg[i] != 1 || outdeg[i] != 1) continue;
            double curv = x[outOf[i]] - 2.0 * x[i] + x[inOf[i]];
            sum += curv * curv;
        }
        return sum;
    }
}

// EXACT layout optimizer (2026-11-16) — the shipped "Compress" algorithm.
//
// Every STS2 map edge joins adjacent floors, and a legal layout only chooses, per floor, which
// lanes its rooms occupy (strictly increasing, in the game's column order). So the whole layout is
// a shortest path through floors: state = one floor's lane choice, transition cost = what the two
// floors look like together. We solve it exactly by dynamic programming over every legal choice
// for every floor (a 7-column map has at most C(7,3) = 35 choices per floor), for every candidate
// map width, and — when the map has a single start and a single boss — for every shared lane of
// those two. Nothing is a heuristic: the result is the global minimum of the cost below.
//
// HARD (never traded away):
//   * connectivity is untouchable — edges are never edited and each floor keeps the game's column
//     order with distinct lanes, so no two rooms overlap and the crossing count equals vanilla's;
//   * START and BOSS share one lane (the map's spine reads straight through, as in vanilla);
//   * no fan edge flips side (a start child that was left of the start never renders right of it).
// SOFT (all integer, lower total = better):
//   * steep angles — convex in the lanes an edge spans (a 2-lane jump costs 8x a 1-lane step);
//     the start fan-out / boss converge-in are inherently spread, so they pay a softer d^2;
//   * interior holes — an empty lane strictly inside one floor's span (the "weird hole");
//   * broken lane columns — two rooms on adjacent floors in the same lane that are NOT linked
//     (reads as a dotted column with a missing step), and every place a lane resumes after a gap;
//   * a lost fan direction class (a "\|/" start that could only render as "\|");
//   * map width (one cost per lane).
public static class OptimalLayout
{
    public const int LaneCost = 8;
    public const int HoleCost = 6;
    public const int BreakCost = 6;
    public const int SegmentCost = 4;
    public const int FanClassLossCost = 200;
    private const int MaxLanes = 9; // C(9,4) = 126 choices/floor keeps the DP tiny; real maps are 7 wide

    public static int BodySlopeCost(int d) => d switch { 0 => 0, 1 => 2, 2 => 16, 3 => 40, _ => 40 + 30 * (d - 3) };
    public static int FanSlopeCost(int d) => d * d;

    // Null = the graph is outside this model (an edge skipping a floor, an over-wide map, or no
    // feasible layout); the caller falls back to the legacy greedy pipeline.
    public static int[]? TryAssign(LGraph g)
    {
        int rowCount = g.RowCount;
        if (g.Nodes.Length == 0) return Array.Empty<int>();
        foreach ((int f, int t) in g.Edges)
            if (g.RowOf[t] != g.RowOf[f] + 1) return null;
        int[][] rows = g.RowsOrdered;
        int widest = rows.Max(r => r.Length);
        int baseLanes = LayoutMetrics.LanesUsed(g.BaselineLanes());
        if (baseLanes > MaxLanes) return null;

        int last = rowCount - 1;
        bool startFan = rowCount >= 2 && rows[0].Length == 1;
        int bossRow = g.BossSpineStartRow();
        bool bossFan = bossRow > 0;
        bool alignEnds = startFan && bossFan;

        var pos = new int[g.Nodes.Length];
        foreach (int[] row in rows)
            for (int i = 0; i < row.Length; i++) pos[row[i]] = i;

        // Per floor gap r -> r+1: edges as (index in r, index in r+1, isFan, baseline fan dir as
        // seen from the end node), plus a link matrix for the straight-column test.
        var gapEdges = new List<(int I, int J, bool Fan, int Dir)>[Math.Max(0, last)];
        var linked = new bool[Math.Max(0, last)][,];
        for (int r = 0; r < last; r++)
        {
            gapEdges[r] = new List<(int, int, bool, int)>();
            linked[r] = new bool[rows[r].Length, rows[r + 1].Length];
        }
        foreach ((int f, int t) in g.Edges.OrderBy(e => g.Nodes[e.From].Row).ThenBy(e => g.Nodes[e.From].Col)
                                          .ThenBy(e => g.Nodes[e.To].Col))
        {
            int r = g.RowOf[f];
            bool fromStart = startFan && r == 0, intoBoss = bossFan && r + 1 == bossRow;
            int dir = fromStart ? Math.Sign(g.Nodes[t].Col - g.Nodes[f].Col)
                    : intoBoss ? Math.Sign(g.Nodes[f].Col - g.Nodes[t].Col) : 0;
            gapEdges[r].Add((pos[f], pos[t], fromStart || intoBoss, dir));
            linked[r][pos[f], pos[t]] = true;
        }

        long bestCost = long.MaxValue;
        int[]? best = null;
        for (int width = widest; width <= baseLanes; width++)
        {
            List<int[]>[] choices = rows.Select(row => Combinations(width, row.Length)).ToArray();
            long[][] rowCost = new long[rowCount][];
            for (int r = 0; r < rowCount; r++)
                rowCost[r] = choices[r].Select(c => (long)HoleCost * (c[^1] - c[0] + 1 - c.Length)).ToArray();

            // trans[r][a][b] = cost of floor r in choice a next to floor r+1 in choice b (-1 = illegal).
            long[][][] trans = new long[Math.Max(0, last)][][];
            for (int r = 0; r < last; r++)
            {
                trans[r] = new long[choices[r].Count][];
                for (int a = 0; a < choices[r].Count; a++)
                {
                    trans[r][a] = new long[choices[r + 1].Count];
                    for (int b = 0; b < choices[r + 1].Count; b++)
                        trans[r][a][b] = Transition(width, choices[r][a], choices[r + 1][b], gapEdges[r], linked[r],
                                                    startFan && r == 0, bossFan && r + 1 == bossRow);
                }
            }

            int[] endLanes = alignEnds ? Enumerable.Range(0, width).ToArray() : new[] { -1 };
            foreach (int endLane in endLanes)
            {
                // Forward DP. With aligned ends, row 0 and every trailing boss-spine row (r >= bossRow,
                // covering both single-boss and two-boss acts) may only take [endLane].
                long[] cost = new long[choices[0].Count];
                for (int a = 0; a < cost.Length; a++)
                    cost[a] = endLane >= 0 && a != endLane ? -1 : rowCost[0][a];
                var back = new int[rowCount][];
                for (int r = 1; r < rowCount; r++)
                {
                    var next = new long[choices[r].Count];
                    back[r] = new int[next.Length];
                    for (int b = 0; b < next.Length; b++)
                    {
                        next[b] = -1;
                        back[r][b] = -1;
                        if (endLane >= 0 && r >= bossRow && b != endLane) continue;
                        for (int a = 0; a < cost.Length; a++)
                        {
                            long t = trans[r - 1][a][b];
                            if (cost[a] < 0 || t < 0) continue;
                            long c = cost[a] + t + rowCost[r][b];
                            if (next[b] < 0 || c < next[b]) { next[b] = c; back[r][b] = a; }
                        }
                    }
                    cost = next;
                }

                int end = -1;
                for (int b = 0; b < cost.Length; b++)
                    if (cost[b] >= 0 && (end < 0 || cost[b] < cost[end])) end = b;
                if (end < 0) continue;
                long total = cost[end] + (long)LaneCost * width;
                if (total >= bestCost) continue; // ties keep the narrower map, then the lower end lane

                bestCost = total;
                best = new int[g.Nodes.Length];
                for (int r = last, ch = end; r >= 0; r--)
                {
                    int[] c = choices[r][ch];
                    for (int i = 0; i < c.Length; i++) best[rows[r][i]] = c[i];
                    if (r > 0) ch = back[r][ch];
                }
            }
        }
        return best;
    }

    private static long Transition(int width, int[] a, int[] b, List<(int I, int J, bool Fan, int Dir)> edges,
                                   bool[,] linked, bool startGap, bool bossGap)
    {
        long cost = 0;
        int startSeen = 0, startNow = 0, bossSeen = 0, bossNow = 0; // bitmasks over dir classes {-1,0,1}
        foreach ((int i, int j, bool fan, int dir) in edges)
        {
            int d = Math.Abs(a[i] - b[j]);
            if (!fan) { cost += BodySlopeCost(d); continue; }
            cost += FanSlopeCost(d);
            // Direction as seen from the end node: start is on floor r (a), boss on floor r+1 (b).
            int now = startGap ? Math.Sign(b[j] - a[i]) : Math.Sign(a[i] - b[j]);
            if (dir != 0 && now == -dir) return -1; // a flipped fan edge is never acceptable
            if (startGap) { startSeen |= 1 << (dir + 1); startNow |= 1 << (now + 1); }
            else { bossSeen |= 1 << (dir + 1); bossNow |= 1 << (now + 1); }
        }
        cost += FanClassLossCost * (PopCount(startSeen & ~startNow) + PopCount(bossSeen & ~bossNow));

        // Lane-column continuity: walk each lane across the two floors.
        int ia = 0, ib = 0;
        for (int lane = 0; lane < width; lane++)
        {
            int atA = ia < a.Length && a[ia] == lane ? ia++ : -1;
            int atB = ib < b.Length && b[ib] == lane ? ib++ : -1;
            if (atA >= 0 && atB >= 0 && !linked[atA, atB]) cost += BreakCost;
            else if (atA < 0 && atB >= 0) cost += SegmentCost;
        }
        return cost;
    }

    private static int PopCount(int x) { int n = 0; for (; x != 0; x &= x - 1) n++; return n; }

    // All strictly increasing k-subsets of [0, n), in lexicographic order.
    private static List<int[]> Combinations(int n, int k)
    {
        var all = new List<int[]>();
        var cur = new int[k];
        void Rec(int idx, int from)
        {
            if (idx == k) { all.Add((int[])cur.Clone()); return; }
            for (int v = from; v <= n - (k - idx); v++) { cur[idx] = v; Rec(idx + 1, v + 1); }
        }
        Rec(0, 0);
        return all;
    }
}

// CONTINUOUS MINIMUM-BENDING-ENERGY LAYOUT (2026-11-19) — Hybrid Vertical-Rail + Curvature QP.
//
// Solves for continuous horizontal coordinates x in R^N minimizing a strictly convex objective:
//   J(x) = sum_{(u,v) in E} [ wL2 * e_uv * (x_v - x_u)^2 + wHuber * body_uv * sqrt((x_v - x_u)^2 + eps^2) ]
//        + sum_{u->v->w}    wBend * w_uvw * (x_w - 2*x_v + x_u)^2
//        + sum_{v in V}     wCompact * x_v^2
// subject to the exact convex polytope P:
//   * Within every floor r, consecutive rooms in vanilla column order satisfy x[i+1] - x[i] >= sep[i] >= 1.0
//     (guarantees zero overlap and exact crossing invariance by construction).
//   * Single-node Start (row 0) and trailing Boss spine (Boss, plus SecondBoss if present) are pinned
//     to x = 0 so the act enters and exits along a single vertical axis.
//
// Why this eliminates jagged diagonals:
//   * Second-derivative curvature (x_w - 2*x_v + x_u)^2 directly penalizes path kinks and zigzags
//     across 3 consecutive floors (a first-order DP cannot see bends at all).
//   * Continuous coordinates allow symmetric 2-way forks, diamonds, and centered merges instead of
//     forcing lopsided integer-column spikes.
//   * Pseudo-Huber slope snaps parallel straight-through runs to crisp vertical tracks while keeping
//     required horizontal transitions smooth and gently angled.
//   * Subtle extra separation (UnlinkedGap = 1.15) between non-sibling neighbors adds Gestalt breathing
//     room between independent macro-routes.
//
// Solved to high precision (<1e-8) in <1 ms via O'Donoghue-Candes adaptive-restart FISTA with exact
// O(k_r) Pool-Adjacent-Violators (PAV) isotonic projection per floor. Runs strictly in canonical
// (Row, Col) node order so results are bit-for-bit independent of input node enumeration order.
public static class ContinuousMapLayout
{
    public const double SlopeL2Weight = 0.55;
    public const double SlopeHuberWeight = 2.20;
    public const double HuberEps = 0.025;
    public const double FanSlopeFactor = 0.22;
    public const double BendWeight = 3.80;
    public const double BranchBendFactor = 0.22;
    public const double CompactWeight = 0.025;
    public const double UnlinkedGap = 1.15;
    private const int MaxIterations = 2500;
    private const double ConvergenceTol = 1e-8;

    public static double[] Solve(LGraph g)
    {
        int n = g.Nodes.Length;
        if (n == 0) return Array.Empty<double>();
        if (n == 1) return new[] { 0.0 };

        // Map LGraph nodes to canonical (Row, Col) indices 0..n-1 so floating-point accumulation
        // order is a pure function of graph topology (100% invariant to input node ID order).
        int rCount = g.RowCount;
        var canonToOrig = new int[n];
        var origToCanon = new int[n];
        var canonRows = new int[rCount][];
        var canonRowOf = new int[n];
        int nextCanon = 0;
        for (int r = 0; r < rCount; r++)
        {
            int[] origRow = g.RowsOrdered[r];
            var crow = new int[origRow.Length];
            for (int i = 0; i < origRow.Length; i++)
            {
                int orig = origRow[i];
                int c = nextCanon++;
                canonToOrig[c] = orig;
                origToCanon[orig] = c;
                crow[i] = c;
                canonRowOf[c] = r;
            }
            canonRows[r] = crow;
        }

        // Canonical sorted edge list (by canonical u, then v).
        var edgeSet = new List<(int U, int V)>(g.Edges.Length);
        foreach ((int f, int t) in g.Edges)
            edgeSet.Add((origToCanon[f], origToCanon[t]));
        edgeSet.Sort((a, b) => a.U != b.U ? a.U.CompareTo(b.U) : a.V.CompareTo(b.V));
        // Deduplicate defensively.
        var edges = new List<(int U, int V)>(edgeSet.Count);
        for (int i = 0; i < edgeSet.Count; i++)
            if (i == 0 || edgeSet[i] != edgeSet[i - 1])
                edges.Add(edgeSet[i]);

        var parents = new List<int>[n];
        var children = new List<int>[n];
        for (int i = 0; i < n; i++) { parents[i] = new List<int>(); children[i] = new List<int>(); }
        foreach ((int u, int v) in edges)
        {
            children[u].Add(v);
            parents[v].Add(u);
        }

        bool hasSingleStart = rCount >= 2 && canonRows[0].Length == 1;
        int bossStart = g.BossSpineStartRow();
        bool hasBossSpine = bossStart > 0;
        var pinned = new bool[n];
        bool anyPinned = false;
        if (hasSingleStart)
        {
            pinned[canonRows[0][0]] = true;
            anyPinned = true;
        }
        if (hasBossSpine)
        {
            for (int r = bossStart; r < rCount; r++)
            {
                pinned[canonRows[r][0]] = true;
                anyPinned = true;
            }
        }

        // Within-row minimum separation: 1.0 between siblings sharing a parent or child,
        // UnlinkedGap between unrelated adjacent strands for subtle macro-route clarity.
        var rowSeps = new double[rCount][];
        for (int r = 0; r < rCount; r++)
        {
            int[] row = canonRows[r];
            var seps = new double[Math.Max(0, row.Length - 1)];
            for (int i = 0; i < seps.Length; i++)
            {
                int a = row[i], b = row[i + 1];
                bool shared = SharesAny(parents[a], parents[b]) || SharesAny(children[a], children[b]);
                seps[i] = shared ? 1.0 : UnlinkedGap;
            }
            rowSeps[r] = seps;
        }

        // Precompute 2-edge chains (u -> v -> w) for second-derivative curvature energy.
        var chains = new List<(int U, int V, int W, double Weight)>();
        for (int v = 0; v < n; v++)
        {
            List<int> ps = parents[v];
            List<int> cs = children[v];
            if (ps.Count == 0 || cs.Count == 0) continue;
            bool isThru = ps.Count == 1 && cs.Count == 1;
            double wc = isThru ? 1.0 : BranchBendFactor / (ps.Count * cs.Count);
            foreach (int u in ps)
                foreach (int w in cs)
                    chains.Add((u, v, w, wc));
        }

        // Edge weights & Lipschitz upper bound for guaranteed monotone FISTA step size.
        var edgeIsFan = new bool[edges.Count];
        var deg = new int[n];
        for (int e = 0; e < edges.Count; e++)
        {
            (int u, int v) = edges[e];
            deg[u]++;
            deg[v]++;
            edgeIsFan[e] = (hasSingleStart && canonRowOf[u] == 0)
                        || (hasBossSpine && canonRowOf[v] >= bossStart);
        }
        int maxDeg = 0;
        for (int i = 0; i < n; i++) if (deg[i] > maxDeg) maxDeg = deg[i];
        double lSlope = 2.0 * maxDeg * (2.0 * SlopeL2Weight + SlopeHuberWeight / HuberEps);

        var bendDiag = new double[n];
        foreach ((int u, int v, int w, double wc) in chains)
        {
            double c = 2.0 * BendWeight * wc;
            bendDiag[u] += 4.0 * c;
            bendDiag[v] += 8.0 * c;
            bendDiag[w] += 4.0 * c;
        }
        double lBend = 0.0;
        for (int i = 0; i < n; i++) if (bendDiag[i] > lBend) lBend = bendDiag[i];
        double step = 1.0 / (lSlope + lBend + 2.0 * CompactWeight + 1.0);

        // Deterministic initial guess from centered raw columns.
        int minCol = int.MaxValue, maxCol = int.MinValue;
        for (int c = 0; c < n; c++)
        {
            int col = g.Nodes[canonToOrig[c]].Col;
            if (col < minCol) minCol = col;
            if (col > maxCol) maxCol = col;
        }
        double midCol = 0.5 * (minCol + maxCol);

        var x = new double[n];
        for (int c = 0; c < n; c++)
            x[c] = g.Nodes[canonToOrig[c]].Col - midCol;
        ProjectAll(x, canonRows, rowSeps, pinned);

        var y = (double[])x.Clone();
        var xNext = new double[n];
        var grad = new double[n];
        double momT = 1.0;

        for (int iter = 0; iter < MaxIterations; iter++)
        {
            Array.Clear(grad, 0, n);

            // 1. Slope gradient (quadratic + pseudo-Huber vertical-rail pull on body edges).
            for (int e = 0; e < edges.Count; e++)
            {
                (int u, int v) = edges[e];
                bool isFan = edgeIsFan[e];
                double ew = isFan ? FanSlopeFactor : 1.0;
                double d = y[v] - y[u];
                double gd = 2.0 * SlopeL2Weight * ew * d;
                if (!isFan)
                    gd += SlopeHuberWeight * (d / Math.Sqrt(d * d + HuberEps * HuberEps));
                grad[u] -= gd;
                grad[v] += gd;
            }

            // 2. Curvature / bending energy gradient: d/dy [ BendWeight * wc * (y_w - 2*y_v + y_u)^2 ].
            for (int ci = 0; ci < chains.Count; ci++)
            {
                (int u, int v, int w, double wc) = chains[ci];
                double curv = y[w] - 2.0 * y[v] + y[u];
                double c = 2.0 * BendWeight * wc * curv;
                grad[u] += c;
                grad[v] -= 2.0 * c;
                grad[w] += c;
            }

            // 3. Gentle centering / compactness.
            for (int i = 0; i < n; i++)
                grad[i] += 2.0 * CompactWeight * y[i];

            for (int i = 0; i < n; i++)
                xNext[i] = y[i] - step * grad[i];
            ProjectAll(xNext, canonRows, rowSeps, pinned);

            // O'Donoghue-Candes adaptive momentum restart + convergence check.
            double dot = 0.0;
            double maxDiff = 0.0;
            for (int i = 0; i < n; i++)
            {
                double diff = xNext[i] - x[i];
                double absDiff = Math.Abs(diff);
                if (absDiff > maxDiff) maxDiff = absDiff;
                dot += (y[i] - xNext[i]) * diff;
            }

            if (dot > 0.0)
            {
                momT = 1.0;
                Array.Copy(xNext, y, n);
            }
            else
            {
                double tNext = 0.5 * (1.0 + Math.Sqrt(1.0 + 4.0 * momT * momT));
                double beta = (momT - 1.0) / tNext;
                for (int i = 0; i < n; i++)
                    y[i] = xNext[i] + beta * (xNext[i] - x[i]);
                momT = tNext;
            }

            Array.Copy(xNext, x, n);
            if (maxDiff < ConvergenceTol && iter > 20)
                break;
        }

        if (!anyPinned)
        {
            double lo = x[0], hi = x[0];
            for (int i = 1; i < n; i++) { if (x[i] < lo) lo = x[i]; if (x[i] > hi) hi = x[i]; }
            double shift = 0.5 * (lo + hi);
            for (int i = 0; i < n; i++) x[i] -= shift;
        }

        // Map canonical solution back to LGraph's node indices.
        var result = new double[n];
        for (int c = 0; c < n; c++)
            result[canonToOrig[c]] = x[c];
        return result;
    }

    private static bool SharesAny(List<int> a, List<int> b)
    {
        for (int i = 0; i < a.Count; i++)
            for (int j = 0; j < b.Count; j++)
                if (a[i] == b[j]) return true;
        return false;
    }

    private static void ProjectAll(double[] x, int[][] rows, double[][] rowSeps, bool[] pinned)
    {
        for (int i = 0; i < x.Length; i++)
            if (pinned[i]) x[i] = 0.0;

        for (int r = 0; r < rows.Length; r++)
        {
            int[] row = rows[r];
            int k = row.Length;
            if (k <= 1) continue;
            ProjectRowPAV(x, row, rowSeps[r]);
        }

        for (int i = 0; i < x.Length; i++)
            if (pinned[i]) x[i] = 0.0;
    }

    // Exact Euclidean projection onto { x[row[i+1]] - x[row[i]] >= seps[i] } via Pool-Adjacent-Violators.
    private static void ProjectRowPAV(double[] x, int[] row, double[] seps)
    {
        int k = row.Length;
        Span<double> offsets = stackalloc double[k];
        Span<double> blockSum = stackalloc double[k];
        Span<int> blockCount = stackalloc int[k];

        offsets[0] = 0.0;
        for (int i = 1; i < k; i++)
            offsets[i] = offsets[i - 1] + seps[i - 1];

        int bLen = 0;
        for (int i = 0; i < k; i++)
        {
            blockSum[bLen] = x[row[i]] - offsets[i];
            blockCount[bLen] = 1;
            bLen++;
            while (bLen >= 2 && blockSum[bLen - 2] * blockCount[bLen - 1] > blockSum[bLen - 1] * blockCount[bLen - 2])
            {
                blockSum[bLen - 2] += blockSum[bLen - 1];
                blockCount[bLen - 2] += blockCount[bLen - 1];
                bLen--;
            }
        }

        int idx = 0;
        for (int b = 0; b < bLen; b++)
        {
            double mean = blockSum[b] / blockCount[b];
            int cnt = blockCount[b];
            for (int c = 0; c < cnt; c++, idx++)
                x[row[idx]] = mean + offsets[idx];
        }
    }
}
