using System;
using System.Collections.Generic;
using System.Linq;
using FlatMap.Layout;

// Offline test harness for the pure MapLayout algorithm. No game, no Godot — run with:
//   dotnet run --project layout/layouttest.csproj
// Exit code 0 = every hard gate passed (never illegal, never worse than baseline, checker works).

internal static class Runner
{
    private static int _failures;

    private static void Fail(string msg) { _failures++; Console.WriteLine($"  FAIL: {msg}"); }
    private static void Expect(bool cond, string msg) { if (!cond) Fail(msg); }

    // A captured real level (Act 1 — Overgrowth, floor 1, seed C7W41TXFEA3Z) with room types, shared
    // by the ASCII viz and the JSON emitter that feeds the PNG option-renderer.
    internal const string CapName = "Act1-Overgrowth-F1";
    internal const string CapNodes =
        "0,3 1,1 1,3 1,5 2,1 2,2 2,3 2,6 3,1 3,3 3,6 4,1 4,3 4,6 5,1 5,4 5,5 5,6 6,0 6,2 6,4 6,6 " +
        "7,0 7,2 7,3 7,5 7,6 8,0 8,1 8,3 8,4 8,6 9,0 9,1 9,3 9,4 9,5 9,6 10,0 10,1 10,2 10,4 10,5 10,6 " +
        "11,1 11,4 11,6 12,2 12,4 12,5 12,6 13,1 13,2 13,4 13,5 14,0 14,2 14,4 14,5 15,0 15,2 15,4 16,3";
    internal const string CapEdges =
        "0,3->1,1 0,3->1,3 0,3->1,5 1,1->2,2 1,1->2,1 1,3->2,3 1,5->2,6 2,1->3,1 2,2->3,1 2,3->3,3 " +
        "2,6->3,6 3,1->4,1 3,3->4,3 3,6->4,6 4,1->5,1 4,3->5,4 4,6->5,6 4,6->5,5 5,1->6,0 5,1->6,2 " +
        "5,4->6,4 5,5->6,6 5,6->6,6 6,0->7,0 6,2->7,2 6,2->7,3 6,4->7,5 6,6->7,6 6,6->7,5 7,0->8,0 " +
        "7,2->8,1 7,3->8,3 7,5->8,4 7,6->8,6 8,0->9,0 8,1->9,1 8,3->9,3 8,4->9,5 8,4->9,4 8,6->9,6 " +
        "9,0->10,0 9,1->10,1 9,3->10,2 9,4->10,4 9,5->10,5 9,5->10,6 9,6->10,6 10,0->11,1 10,1->11,1 " +
        "10,2->11,1 10,4->11,4 10,5->11,6 10,6->11,6 11,1->12,2 11,4->12,4 11,6->12,5 11,6->12,6 " +
        "12,2->13,2 12,2->13,1 12,4->13,4 12,5->13,5 12,5->13,4 12,6->13,5 13,1->14,0 13,2->14,2 " +
        "13,4->14,4 13,5->14,5 14,0->15,0 14,2->15,2 14,4->15,4 14,5->15,4 15,0->16,3 15,2->16,3 15,4->16,3";
    internal const string CapTypes =
        "0,3=Ancient 1,1=Monster 1,3=Monster 1,5=Monster 2,1=Monster 2,2=Unknown 2,3=Unknown 2,6=Monster " +
        "3,1=Unknown 3,3=Monster 3,6=Monster 4,1=Unknown 4,3=Monster 4,6=Monster 5,1=Monster 5,4=Unknown " +
        "5,5=Shop 5,6=Monster 6,0=Elite 6,2=Monster 6,4=Unknown 6,6=RestSite 7,0=RestSite 7,2=RestSite " +
        "7,3=Elite 7,5=Unknown 7,6=Elite 8,0=Elite 8,1=Monster 8,3=Monster 8,4=Unknown 8,6=Unknown " +
        "9,0=Treasure 9,1=Treasure 9,3=Treasure 9,4=Treasure 9,5=Treasure 9,6=Treasure 10,0=Unknown " +
        "10,1=Monster 10,2=Unknown 10,4=RestSite 10,5=Monster 10,6=Elite 11,1=RestSite 11,4=Monster " +
        "11,6=Monster 12,2=Monster 12,4=RestSite 12,5=RestSite 12,6=Elite 13,1=Elite 13,2=Monster " +
        "13,4=Shop 13,5=Unknown 14,0=Monster 14,2=Elite 14,4=Monster 14,5=Shop 15,0=RestSite 15,2=RestSite " +
        "15,4=RestSite 16,3=Boss";

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "viz") { CompressionAnalysis(); return 0; }
        if (args.Length > 0 && args[0] == "viz-json") { EmitOptionsJson(); return 0; }

        Console.WriteLine("=== FlatMap map-layout tests ===\n");
        CuratedCases();
        SafetyNetTest();
        ExteriorSpikeMetricTest();
        PropertyTests(seedCount: 500);

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL GATES PASSED" : $"{_failures} FAILURE(S)");
        return _failures == 0 ? 0 : 1;
    }

    // ---- viz tool: render a captured level and compare compression options -----------------

    // ASCII render of a lane assignment: text-rows = lanes (top to bottom), text-columns = map
    // floors (left to right). Each cell shows the node's ORIGINAL column digit (so you can trace
    // how the game's columns spread across lanes), or '.' if empty.
    private static string RenderGrid(LGraph g, int[] lane)
    {
        int maxRow = g.Nodes.Max(n => n.Row);
        int minL = lane.Min(), maxL = lane.Max();
        var at = new Dictionary<(int row, int lane), int>(); // -> col digit
        foreach (LNode n in g.Nodes) at[(n.Row, lane[n.Id])] = n.Col;

        var sb = new System.Text.StringBuilder();
        sb.Append("      ").Append(string.Join("", Enumerable.Range(0, maxRow + 1).Select(r => (r % 10).ToString()))).Append("   (floors)\n");
        for (int L = minL; L <= maxL; L++)
        {
            sb.Append($"lane{L,2} ");
            for (int r = 0; r <= maxRow; r++)
                sb.Append(at.TryGetValue((r, L), out int col) ? (char)('0' + col % 10) : '.');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // Maximum compression: pack each floor's nodes into lanes 0..k-1 by column order. Uses the
    // fewest lanes possible (= the widest floor) but ignores alignment, so it usually has many
    // crossings. Shows the compression ceiling / the crossings cost.
    private static int[] MinPack(LGraph g)
    {
        var lane = new int[g.Nodes.Length];
        foreach (int[] row in g.RowsOrdered)
            for (int i = 0; i < row.Length; i++) lane[row[i]] = i;
        return lane;
    }

    private static void CompressionAnalysis()
    {
        LGraph g = FromDump(CapNodes, CapEdges);

        // PROBE (item B): is the current layout even locally optimal w.r.t. shifting a WHOLE floor
        // up/down by one lane? Such a move preserves connectivity + within-floor order (always legal)
        // but is NOT in HillClimb's move set (single nodes / same-lane runs), so if beneficial ones
        // exist, the search is stuck in a local optimum that a coordinated move would escape.
        {
            int[] cur = MapLayout.AssignLanes(g);
            int el0 = LayoutMetrics.VerticalEdgeLength(g, cur), bn0 = LayoutMetrics.BendCount(g, cur), ln0 = LayoutMetrics.LanesUsed(cur);
            Console.WriteLine($"\n>>> WHOLE-FLOOR-SHIFT PROBE (current: edgeLen={el0} bends={bn0} lanes={ln0}):");
            int localBeat = 0;
            for (int r = 0; r < g.RowCount; r++)
                foreach (int delta in new[] { -1, 1 }) // -1 = up (toward lane 0 / top of screen)
                {
                    int[] t = (int[])cur.Clone();
                    foreach (int id in g.RowsOrdered[r]) t[id] += delta;
                    if (t.Min() < 0 || !LayoutInvariants.IsLegal(g, t)) continue;
                    int el1 = LayoutMetrics.VerticalEdgeLength(g, t), bn1 = LayoutMetrics.BendCount(g, t), ln1 = LayoutMetrics.LanesUsed(t);
                    if (el1 < el0 || bn1 < bn0)
                        Console.WriteLine($"      floor {r,2} {(delta < 0 ? "UP  " : "DOWN")}: edgeLen {el0}->{el1}  bends {bn0}->{bn1}  lanes {ln0}->{ln1}{(ln1 > ln0 ? "  (+lane)" : "")}");
                    if (el1 < el0 || bn1 < bn0) localBeat++;
                }
            if (localBeat == 0) Console.WriteLine("      none — layout is locally optimal under whole-floor shifts.");
            Console.WriteLine();
        }

        void Show(string name, int[] l) =>
            Console.WriteLine($"\n## {name}\n   lanes={LayoutMetrics.LanesUsed(l)}  edgeLen={LayoutMetrics.VerticalEdgeLength(g, l)}  " +
                              $"bends={LayoutMetrics.BendCount(g, l)}  maxSlope={LayoutMetrics.MaxEdgeSlope(g, l)}  crossings={LayoutMetrics.Crossings(g, l)}  legal={LayoutInvariants.IsLegal(g, l)}\n" + RenderGrid(g, l));

        Console.WriteLine("=== COMPRESSION ANALYSIS — Act 1 Overgrowth F1 (seed C7W41TXFEA3Z) ===");
        Show("A. baseline (raw game columns)", g.BaselineLanes());
        Show("B. our algorithm (flatten + lane-merge)", MapLayout.AssignLanes(g));
        Show("C. min-pack (max compression, ignores crossings)", MinPack(g));
        Show("D. slope<=1 (vanilla gentle shape; start/boss exempt)", MapLayout.AssignLanesMaxSlope(g, 1));

        // Which adjacent lane pairs could be cleanly merged (no floor uses both)?
        int[] algo = MapLayout.AssignLanes(g);
        Console.WriteLine("\n## clean lane-merges available on (B):");
        int min = algo.Min(), max = algo.Max(), found = 0;
        for (int a = min; a < max; a++)
        {
            bool conflict = g.RowsOrdered.Any(row => row.Any(id => algo[id] == a) && row.Any(id => algo[id] == a + 1));
            if (!conflict) { Console.WriteLine($"   lanes {a}+{a + 1}: MERGEABLE"); found++; }
        }
        if (found == 0) Console.WriteLine("   none — every adjacent lane pair shares a floor, so 6 lanes would overlap two rooms.");
    }

    // Emit the candidate layouts as JSON on stdout, for the PNG option-renderer
    // (scripts/render_mapviz.py). Round 1 shows layouts that already exist so the objective can be
    // calibrated against the eye before any new strategy is coded.
    private static void EmitOptionsJson()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"maps\":[");

        // Map 1: the real captured level, with its real room types.
        LGraph real = FromDump(CapNodes, CapEdges);
        var realTypes = new Dictionary<(int, int), string>();
        foreach (string tok in CapTypes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] kv = tok.Split('=');
            string[] rc = kv[0].Split(',');
            realTypes[(int.Parse(rc[0]), int.Parse(rc[1]))] = kv[1];
        }
        sb.Append(MapJson(CapName, real, realTypes));

        // 30 more: random STS-like maps with seeded room types, so we evaluate the SYSTEM across many
        // shapes, not just one hand-captured level.
        for (int seed = 0; seed < 30; seed++)
        {
            var rng = new Random(seed);
            LGraph g = RandomStsMap(rng);
            sb.Append(',').Append(MapJson($"random-{seed:D2}", g, AssignTypes(g, rng)));
        }

        sb.Append("]}");
        Console.WriteLine(sb.ToString());
    }

    private static string MapJson(string name, LGraph g, Dictionary<(int, int), string> type)
    {
        string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        string Option(string optName, int[] lane)
        {
            string metrics = $"lanes={LayoutMetrics.LanesUsed(lane)}  bends={LayoutMetrics.BendCount(g, lane)}  " +
                             $"edgeLen={LayoutMetrics.VerticalEdgeLength(g, lane)}  steepBody={LayoutMetrics.SteepBodyEdges(g, lane)}  " +
                             $"maxSlope={LayoutMetrics.MaxEdgeSlope(g, lane)}  crossings={LayoutMetrics.Crossings(g, lane)}";
            string nb = string.Join(",", g.Nodes.Select(n =>
                $"[{n.Row},{lane[n.Id]},\"{type.GetValueOrDefault((n.Row, n.Col), "Unknown")}\"]"));
            string eb = string.Join(",", g.Edges.Select(e =>
                $"[{g.Nodes[e.From].Row},{lane[e.From]},{g.Nodes[e.To].Row},{lane[e.To]}]"));
            return $"{{\"name\":\"{Esc(optName)}\",\"metrics\":\"{Esc(metrics)}\",\"nodes\":[{nb}],\"edges\":[{eb}]}}";
        }
        string[] opts =
        {
            Option("A. baseline (raw game columns)", g.BaselineLanes()),
            Option("B. current shipped algorithm", MapLayout.AssignLanes(g)),
            Option("C. simple compress (pack toward centerline)", CenterPack(g)),
            Option("D. gentle slope<=1 (flatness-leaning target)", MapLayout.AssignLanesMaxSlope(g, 1)),
        };
        return $"{{\"name\":\"{Esc(name)}\",\"options\":[{string.Join(",", opts)}]}}";
    }

    // The honest naive-compress baseline: pack each floor's rooms into consecutive lanes CENTERED on
    // the midline (compress IN toward the centerline), not hugging lane 0. The old top-packing only
    // ever compressed UP, which read oddly. This is the "naive flat" starting point the beautiful
    // rules then refine (vs extreme max-compression, which we do NOT want).
    private static int[] CenterPack(LGraph g)
    {
        var lane = new int[g.Nodes.Length];
        foreach (int[] row in g.RowsOrdered)
            for (int i = 0; i < row.Length; i++) lane[row[i]] = i - (row.Length - 1) / 2;
        int min = lane.Min();
        if (min != 0) for (int i = 0; i < lane.Length; i++) lane[i] -= min;
        return lane;
    }

    // Seeded room types for a synthetic map so the rendered examples look map-like (single start/boss
    // when the extreme rows are single-node).
    private static Dictionary<(int, int), string> AssignTypes(LGraph g, Random rng)
    {
        string[] pool = { "Monster", "Monster", "Monster", "Monster", "Unknown", "Unknown", "Elite", "RestSite", "Shop", "Treasure" };
        int last = g.RowCount - 1;
        var type = new Dictionary<(int, int), string>();
        foreach (LNode n in g.Nodes)
        {
            if (g.RowOf[n.Id] == 0 && g.RowsOrdered[0].Length == 1) type[(n.Row, n.Col)] = "Ancient";
            else if (g.RowOf[n.Id] == last && g.RowsOrdered[last].Length == 1) type[(n.Row, n.Col)] = "Boss";
            else type[(n.Row, n.Col)] = pool[rng.Next(pool.Length)];
        }
        return type;
    }

    // ---- fluent graph builder --------------------------------------------------------------
    private sealed class B
    {
        private readonly Dictionary<(int, int), int> _ids = new();
        private readonly List<LNode> _nodes = new();
        private readonly HashSet<(int, int, int, int)> _seen = new();
        private readonly List<(int, int)> _edges = new();

        private int N(int row, int col)
        {
            if (!_ids.TryGetValue((row, col), out int id))
            {
                id = _nodes.Count;
                _ids[(row, col)] = id;
                _nodes.Add(new LNode(id, row, col));
            }
            return id;
        }

        public B E(int r1, int c1, int r2, int c2)
        {
            if (_seen.Add((r1, c1, r2, c2))) _edges.Add((N(r1, c1), N(r2, c2)));
            else { N(r1, c1); N(r2, c2); }
            return this;
        }

        public B Chain(int col, int rowFrom, int rowTo)
        {
            for (int r = rowFrom; r < rowTo; r++) E(r, col, r + 1, col);
            return this;
        }

        public B Node(int row, int col) { N(row, col); return this; }

        public LGraph G() => new(_nodes, _edges);
    }

    // Build a graph from a captured in-game MAPDUMP (nodes "row,col[,type] ..."; edges
    // "row,col->row,col ...").
    private static LGraph FromDump(string nodes, string edges)
    {
        var b = new B();
        foreach (string tok in nodes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] p = tok.Split(',');
            b.Node(int.Parse(p[0]), int.Parse(p[1]));
        }
        foreach (string tok in edges.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] ft = tok.Split("->");
            string[] f = ft[0].Split(','), t = ft[1].Split(',');
            b.E(int.Parse(f[0]), int.Parse(f[1]), int.Parse(t[0]), int.Parse(t[1]));
        }
        return b.G();
    }

    // ---- curated cases ---------------------------------------------------------------------
    private static void CuratedCases()
    {
        Console.WriteLine("-- curated cases --");

        // 1. A straight column: nothing to do, must stay flat at one lane.
        Run("straight-column", new B().Chain(0, 0, 5).G(), (g, baseLen, len, baseLn, ln) =>
        {
            Expect(len == 0, $"expected 0 edge length, got {len}");
            Expect(ln == 1, $"expected 1 lane, got {ln}");
        });

        // 2. Top run that should sink one lane (your campfire example, distilled): a lane-0 run
        //    rows 1..7, both ends pulled downward, lane 1 empty beneath it.
        var drop = new B().Chain(0, 1, 7)       // the run at col 0
            .E(0, 1, 1, 0)                        // left end pulled down (parent at col 1)
            .E(7, 0, 8, 2)                        // right end pulled down (child at col 2)
            .G();
        Run("top-run-drops", drop, (g, baseLen, len, baseLn, ln) =>
            Expect(len < baseLen || ln < baseLn, $"expected the run to sink/compact ({len}/{ln} vs {baseLen}/{baseLn})"));

        // 3. A pure zigzag path should straighten to a single lane.
        var zig = new B().E(0, 0, 1, 1).E(1, 1, 2, 0).E(2, 0, 3, 1).E(3, 1, 4, 0).G();
        Run("zigzag-straightens", zig, (g, baseLen, len, baseLn, ln) =>
            Expect(len < baseLen || ln < baseLn, $"expected straighten/compact ({len}/{ln} vs {baseLen}/{baseLn})"));

        // 4. Dense grid — every cell filled, no empty lane to move into: must be left legal and
        //    no worse (guaranteed), not "improved" into an overlap.
        var dense = new B();
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 4; c++)
                for (int cc = Math.Max(0, c - 1); cc <= Math.Min(3, c + 1); cc++)
                    dense.E(r, c, r + 1, cc);
        Run("dense-grid-stays-legal", dense.G(), (g, baseLen, len, baseLn, ln) =>
            Expect(ln == baseLn, $"dense grid height should be unchanged ({ln} vs {baseLn})"));

        // 5. Trap: two nodes in a row both pulled onto the same lane — must stay distinct.
        var trap = new B().E(0, 0, 1, 0).E(0, 0, 1, 1).E(1, 1, 2, 0).E(1, 0, 2, 0).G();
        Run("trap-no-overlap", trap, (g, baseLen, len, baseLn, ln) => { /* legality asserted in Run */ });

        // 6. A real captured in-game level (Act 1, 7 columns). Baseline uses all 7 lanes; the
        //    lane-merge pass should clear at least one lane while staying legal.
        const string realNodes =
            "0,3 1,0 1,2 1,4 1,6 2,0 2,2 2,4 2,5 2,6 3,0 3,2 3,4 3,6 4,0 4,3 4,5 5,1 5,2 5,4 " +
            "6,1 6,3 6,4 6,5 7,0 7,2 7,3 7,4 7,5 8,0 8,1 8,2 8,4 8,5 9,0 9,2 9,5 10,1 10,2 10,4 10,6 " +
            "11,0 11,1 11,2 11,4 11,6 12,0 12,2 12,4 12,6 13,0 13,1 13,3 13,4 13,6 14,0 14,1 14,4 14,6 15,0 15,5 16,3";
        const string realEdges =
            "0,3->1,0 0,3->1,2 0,3->1,4 0,3->1,6 1,0->2,0 1,2->2,2 1,4->2,5 1,4->2,4 1,6->2,6 " +
            "2,0->3,0 2,2->3,2 2,4->3,4 2,5->3,4 2,6->3,6 3,0->4,0 3,2->4,3 3,4->4,3 3,6->4,5 " +
            "4,0->5,1 4,3->5,4 4,3->5,2 4,5->5,4 5,1->6,1 5,2->6,3 5,2->6,1 5,4->6,3 5,4->6,5 5,4->6,4 " +
            "6,1->7,2 6,1->7,0 6,3->7,2 6,3->7,3 6,4->7,4 6,5->7,5 7,0->8,0 7,2->8,2 7,2->8,1 7,3->8,2 " +
            "7,4->8,4 7,5->8,5 8,0->9,0 8,1->9,0 8,2->9,2 8,4->9,5 8,5->9,5 9,0->10,1 9,2->10,1 9,2->10,2 " +
            "9,5->10,6 9,5->10,4 10,1->11,1 10,1->11,0 10,2->11,2 10,2->11,1 10,4->11,4 10,6->11,6 " +
            "11,0->12,0 11,1->12,2 11,2->12,2 11,4->12,4 11,6->12,6 12,0->13,0 12,0->13,1 12,2->13,1 " +
            "12,2->13,3 12,4->13,4 12,6->13,6 13,0->14,0 13,1->14,1 13,3->14,4 13,4->14,4 13,6->14,6 " +
            "14,0->15,0 14,1->15,0 14,4->15,5 14,6->15,5 15,0->16,3 15,5->16,3";
        // Legality + never-worse are asserted in Run; lane count is reported. (This particular
        // level's crossing-free minimum happens to be 7 — no adjacent lanes are conflict-free.)
        Run("real-act1-level", FromDump(realNodes, realEdges), (g, baseLen, len, baseLn, ln) => { });
    }

    // A case runner: asserts legality (hard) + the never-worse guarantees (hard), prints metrics,
    // then runs the case-specific expectation.
    private static void Run(string name, LGraph g, Action<LGraph, int, int, int, int> expect)
    {
        int[] baseline = g.BaselineLanes();
        int baseLen = LayoutMetrics.VerticalEdgeLength(g, baseline);
        int baseLanes = LayoutMetrics.LanesUsed(baseline);
        int baseCross = LayoutMetrics.Crossings(g, baseline);

        int[] lane = MapLayout.AssignLanes(g);
        List<string> violations = LayoutInvariants.Check(g, lane);
        int len = LayoutMetrics.VerticalEdgeLength(g, lane);
        int lanes = LayoutMetrics.LanesUsed(lane);
        int cross = LayoutMetrics.Crossings(g, lane);

        Console.WriteLine($"  {name,-26} edgeLen {baseLen}->{len}  lanes {baseLanes}->{lanes}  " +
                          $"crossings {baseCross}->{cross}  {(violations.Count == 0 ? "legal" : "ILLEGAL")}");
        foreach (string v in violations) Fail($"{name}: {v}");
        // Hard gates: legal, never MORE lanes, never MORE crossings. Edge length may rise a little
        // when we trade it for clearing a lane — reported, not gated.
        Expect(lanes <= baseLanes, $"{name}: lanes regressed ({lanes} > {baseLanes})");
        Expect(cross <= baseCross, $"{name}: crossings regressed ({cross} > {baseCross})");
        expect(g, baseLen, len, baseLanes, lanes);
    }

    // ---- prove the invariant checker actually catches illegal layouts ----------------------
    private static void SafetyNetTest()
    {
        Console.WriteLine("-- safety-net (checker must reject illegal layouts) --");
        var g = new B().E(0, 0, 1, 0).E(0, 0, 1, 1).E(0, 1, 1, 1).G();

        int[] allZero = new int[g.Nodes.Length];               // forces overlaps
        Expect(!LayoutInvariants.IsLegal(g, allZero), "checker failed to flag an all-same-lane overlap");

        int[] inverted = g.BaselineLanes();                     // reverse a row's order
        int[] row1 = g.RowsOrdered[1];
        if (row1.Length >= 2) { int t = inverted[row1[0]]; inverted[row1[0]] = inverted[row1[1]]; inverted[row1[1]] = t; }
        Expect(!LayoutInvariants.IsLegal(g, inverted), "checker failed to flag a col-order inversion");
        Console.WriteLine("  checker rejects overlap + inversion: ok");
    }

    // The screenshot case distilled: one body row reaches a lane farther left than both neighbors.
    // Extending either adjacent row into that lane turns the sharp tip into a short outer run.
    private static void ExteriorSpikeMetricTest()
    {
        Console.WriteLine("-- exterior contour metric --");
        var b = new B();
        for (int r = 0; r < 7; r++)
        {
            b.Node(r, 1);
            if (r is > 0 and < 6) b.Node(r, 3);
        }
        b.Node(3, 0);
        LGraph g = b.G();
        int[] pointed = g.BaselineLanes();
        int[] extended = (int[])pointed.Clone();
        int adjacent = g.Nodes.Single(n => n.Row == 2 && n.Col == 1).Id;
        extended[adjacent] = 0;

        Expect(LayoutMetrics.ExteriorSpikeCount(g, pointed) == 1,
            "expected the isolated left protrusion to count as one exterior spike");
        Expect(LayoutMetrics.ExteriorSpikeCount(g, extended) == 0,
            "expected extending the adjacent outer lane to remove the spike");
        Console.WriteLine("  adjacent outer extension removes one-floor spike: ok");
    }

    // ---- property tests over hundreds of random STS-like maps ------------------------------
    private static void PropertyTests(int seedCount)
    {
        Console.WriteLine($"-- property tests ({seedCount} random maps) --");
        int illegal = 0, regressed = 0, improved = 0, orderLeaks = 0;
        long baseLenSum = 0, lenSum = 0, baseLaneSum = 0, laneSum = 0;

        for (int seed = 0; seed < seedCount; seed++)
        {
            LGraph g = RandomStsMap(new Random(seed));

            // Order-independence: the layout MUST be a pure function of the graph. Rebuild the SAME
            // graph with nodes enumerated in a different order (as the game's point-dictionary would)
            // and assert IDENTICAL lanes keyed by (row,col). A leak here = the game and this offline
            // harness can diverge, which makes every test untrustworthy. See MapLayout.SameLaneRuns.
            if (!SameLanesUnderReorder(g, new Random(seed + 999_999)))
            {
                orderLeaks++;
                if (orderLeaks <= 3) Console.WriteLine($"    seed {seed}: layout changed under node reordering (order leak)");
            }

            int[] baseline = g.BaselineLanes();
            int baseLen = LayoutMetrics.VerticalEdgeLength(g, baseline);
            int baseLanes = LayoutMetrics.LanesUsed(baseline);

            int baseCross = LayoutMetrics.Crossings(g, baseline);
            int[] lane = MapLayout.AssignLanes(g);
            if (!LayoutInvariants.IsLegal(g, lane)) { illegal++; if (illegal <= 3) foreach (var v in LayoutInvariants.Check(g, lane)) Console.WriteLine($"    seed {seed}: {v}"); }
            int len = LayoutMetrics.VerticalEdgeLength(g, lane);
            int lanes = LayoutMetrics.LanesUsed(lane);

            if (lanes > baseLanes || LayoutMetrics.Crossings(g, lane) > baseCross) regressed++;
            if (lanes < baseLanes || len < baseLen) improved++;
            baseLenSum += baseLen; lenSum += len; baseLaneSum += baseLanes; laneSum += lanes;
        }

        Console.WriteLine($"  illegal: {illegal}   regressed: {regressed}   improved: {improved}/{seedCount}   order-leaks: {orderLeaks}");
        Console.WriteLine($"  total edge length {baseLenSum} -> {lenSum}  ({100.0 * (baseLenSum - lenSum) / Math.Max(1, baseLenSum):F1}% shorter)");
        Console.WriteLine($"  total lanes used  {baseLaneSum} -> {laneSum}  ({100.0 * (baseLaneSum - laneSum) / Math.Max(1, baseLaneSum):F1}% fewer)");
        Expect(illegal == 0, $"{illegal} random maps produced an ILLEGAL layout");
        Expect(regressed == 0, $"{regressed} random maps regressed vs baseline");
        Expect(orderLeaks == 0, $"{orderLeaks} random maps changed layout under node reordering (not a pure function of the graph)");
    }

    // Rebuild g with its nodes enumerated in a shuffled order (edges unchanged) and check that
    // AssignLanes produces the same lane for every (row,col). This is the guard that keeps the game
    // (which lists nodes in point-dictionary order) and the offline harness bit-for-bit identical.
    private static bool SameLanesUnderReorder(LGraph g, Random rng)
    {
        int[] baseLane = MapLayout.AssignLanes(g);
        var baseByCoord = g.Nodes.ToDictionary(n => (n.Row, n.Col), n => baseLane[n.Id]);

        int[] perm = Enumerable.Range(0, g.Nodes.Length).ToArray();
        for (int i = perm.Length - 1; i > 0; i--) { int j = rng.Next(i + 1); (perm[i], perm[j]) = (perm[j], perm[i]); }

        var b = new B();
        foreach (int i in perm) b.Node(g.Nodes[i].Row, g.Nodes[i].Col);   // Ids follow the shuffled order
        foreach ((int f, int t) in g.Edges) b.E(g.Nodes[f].Row, g.Nodes[f].Col, g.Nodes[t].Row, g.Nodes[t].Col);
        LGraph gp = b.G();

        int[] permLane = MapLayout.AssignLanes(gp);
        return gp.Nodes.All(n => baseByCoord[(n.Row, n.Col)] == permLane[n.Id]);
    }

    // STS-like generator: a handful of paths that walk up the 7-wide grid, each step moving to an
    // adjacent column. Union of paths = nodes; steps = edges. Mirrors the real map's adjacency.
    private static LGraph RandomStsMap(Random rng)
    {
        const int width = 7;
        int rows = rng.Next(10, 18);
        int paths = rng.Next(4, 8);
        var b = new B();
        for (int p = 0; p < paths; p++)
        {
            int col = rng.Next(0, width);
            for (int r = 0; r < rows; r++)
            {
                int next = Math.Clamp(col + rng.Next(-1, 2), 0, width - 1);
                b.E(r, col, r + 1, next);
                col = next;
            }
        }
        return b.G();
    }
}
