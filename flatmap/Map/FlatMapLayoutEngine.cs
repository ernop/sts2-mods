using System;
using System.Collections.Generic;
using System.Linq;
using FlatMap.Layout;
using Godot;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;

namespace FlatMap;

internal sealed class NodeSnapshot
{
    internal Vector2 VanillaPosition { get; init; }
    internal Vector2 VanillaScale { get; init; }
    internal Vector2 VanillaCenterInPoints { get; init; }
    internal Material? VanillaIconMaterial { get; init; }
    internal Vector2 VanillaOutlinePivot { get; init; }
    internal Vector2 VanillaOutlineScale { get; init; }
    internal float NaturalExtent { get; init; }
}

internal sealed class NodePlacement
{
    internal MapCoord Coord { get; init; }
    internal NMapPoint Node { get; init; } = null!;
    internal Vector2 ScreenCenter { get; init; }
    internal Vector2 LocalCenterInPoints { get; init; }
    internal Vector2 FlatScale { get; init; }
    internal float VisualRadius { get; init; }
}

internal enum EdgeVisualState
{
    Reachable,
    Traveled,
    Traveling,
    Ghosted,
}

internal readonly struct EdgeVisual
{
    internal readonly Vector2 From;
    internal readonly Vector2 To;
    internal readonly Vector2 Ctrl1;
    internal readonly Vector2 Ctrl2;
    internal readonly bool Curved;
    internal readonly EdgeVisualState State;

    internal EdgeVisual(Vector2 from, Vector2 to, EdgeVisualState state)
    {
        From = from;
        To = to;
        Ctrl1 = from;
        Ctrl2 = to;
        Curved = false;
        State = state;
    }

    internal EdgeVisual(Vector2 from, Vector2 ctrl1, Vector2 ctrl2, Vector2 to, EdgeVisualState state)
    {
        From = from;
        Ctrl1 = ctrl1;
        Ctrl2 = ctrl2;
        To = to;
        Curved = true;
        State = state;
    }
}

internal readonly struct DrawingsAffine
{
    internal readonly Vector2 Position;
    internal readonly Vector2 Scale;

    internal DrawingsAffine(Vector2 position, Vector2 scale)
    {
        Position = position;
        Scale = scale;
    }
}

internal sealed class ComputedFlatLayout
{
    internal Dictionary<MapCoord, NodePlacement> Placements { get; } = new();
    internal DrawingsAffine DrawingsTransform { get; init; }
    internal float BaseRadius { get; init; }
}

/// <summary>
/// Pure geometry, reachability, color-language, and affine drawing-transform calculator for the
/// in-place FlatMap presentation of NMapScreen.
/// </summary>
internal static class FlatMapLayoutEngine
{
    internal const float GhostAlpha = 0.28f;

    // Vivid Neon Rim colors (strong contrast against both dark and parchment act backgrounds).
    internal static Color RimColorFor(MapPointType type) => type switch
    {
        MapPointType.Monster  => new Color(0.90f, 0.22f, 0.21f), // red
        MapPointType.Elite    => new Color(0.72f, 0.22f, 0.91f), // purple
        MapPointType.Shop     => new Color(1.00f, 0.83f, 0.00f), // pure gold
        MapPointType.RestSite => new Color(0.18f, 0.84f, 0.33f), // vivid green
        MapPointType.Treasure => new Color(1.00f, 0.54f, 0.00f), // vivid orange
        MapPointType.Unknown  => new Color(0.42f, 0.78f, 1.00f), // light sky blue
        MapPointType.Ancient  => new Color(0.95f, 0.93f, 0.88f), // ivory
        MapPointType.Boss     => new Color(0.95f, 0.93f, 0.88f), // ivory
        _                     => Colors.White,
    };

    /// <summary>
    /// Resolve continuous smooth display x-coordinates once per map using
    /// FlatMap.Layout.MapLayout.AssignSmoothX and assert LayoutInvariants before returning.
    /// </summary>
    internal static Dictionary<MapCoord, float> ComputeSmoothLanes(IReadOnlyDictionary<MapCoord, NMapPoint> points)
    {
        var result = new Dictionary<MapCoord, float>(points.Count);
        if (points.Count == 0)
            return result;

        // Canonical sort by (row, col) guarantees deterministic ids matching the offline test harness.
        List<MapCoord> ordered = points.Keys
            .OrderBy(c => c.row)
            .ThenBy(c => c.col)
            .ToList();
        var idOf = new Dictionary<MapCoord, int>(ordered.Count);
        var nodes = new LNode[ordered.Count];
        for (int i = 0; i < ordered.Count; i++)
        {
            MapCoord c = ordered[i];
            idOf[c] = i;
            nodes[i] = new LNode(i, c.row, c.col);
        }

        var edges = new List<(int From, int To)>();
        foreach (MapCoord from in ordered)
        {
            MapPoint? mp = points[from].Point;
            if (mp == null) continue;
            int fromId = idOf[from];
            foreach (MapPoint child in mp.Children)
            {
                if (child.coord.row > from.row && idOf.TryGetValue(child.coord, out int toId))
                    edges.Add((fromId, toId));
            }
        }

        var graph = new LGraph(nodes, edges);
        double[] xs = MapLayout.AssignSmoothX(graph);
        List<string> violations = LayoutInvariants.CheckContinuous(graph, xs);
        if (violations.Count > 0)
            throw new InvalidOperationException("MapLayout invariant violation: " + string.Join("; ", violations));

        for (int i = 0; i < ordered.Count; i++)
            result[ordered[i]] = (float)xs[i];
        return result;
    }

    /// <summary>
    /// Compute viewport-responsive vertical flat coordinates, node scales, visual radii, and the
    /// exact affine transform mapping vanilla NMapDrawings space onto the flat node grid.
    /// </summary>
    internal static ComputedFlatLayout Compute(
        IReadOnlyDictionary<MapCoord, NMapPoint> points,
        IReadOnlyDictionary<MapCoord, NodeSnapshot> snapshots,
        IReadOnlyDictionary<MapCoord, float> smoothLanes,
        bool compress,
        bool hasSecondBoss,
        Vector2 screenSize,
        Vector2 pointsOffset,
        Vector2 vanillaDrawingsPos,
        float leftClearance,
        float rightLegendX,
        float topClearance)
    {
        var layout = new ComputedFlatLayout();
        if (points.Count == 0)
            return layout;

        // Distinct rows in ascending floor order (bottom = start row 0, top = boss / second boss).
        int[] rows = points.Keys.Select(c => c.row).Distinct().OrderBy(r => r).ToArray();
        int rowCount = rows.Length;
        var rowIndexOf = new Dictionary<int, int>(rowCount);
        for (int i = 0; i < rowCount; i++) rowIndexOf[rows[i]] = i;

        int rawMinCol = points.Keys.Min(c => c.col);
        int rawMaxCol = points.Keys.Max(c => c.col);
        float rawSpan = Math.Max(1f, rawMaxCol - rawMinCol);

        float activeMinLane = float.PositiveInfinity, activeMaxLane = float.NegativeInfinity;
        foreach (MapCoord c in points.Keys)
        {
            float l = compress && smoothLanes.TryGetValue(c, out float sl) ? sl : c.col;
            if (l < activeMinLane) activeMinLane = l;
            if (l > activeMaxLane) activeMaxLane = l;
        }
        float activeMidLane = (activeMinLane + activeMaxLane) * 0.5f;

        // Weighted vertical row spacing: Start gap 1.25x, boss-converge gap 1.62x,
        // Boss1->Boss2 gap 1.45x (when Double Boss is active), normal floor gaps 1.00x.
        float topY = Mathf.Clamp(topClearance, 64f, screenSize.Y * 0.16f);
        float botY = Math.Max(topY + 120f, screenSize.Y - 46f);
        float usableH = botY - topY;

        float[] gapWeights = new float[Math.Max(0, rowCount - 1)];
        float totalWeight = 0f;
        int bossRowIdx = hasSecondBoss && rowCount >= 3 ? rowCount - 2 : rowCount - 1;
        for (int r = 0; r < gapWeights.Length; r++)
        {
            float w = 1.0f;
            if (r == 0) w = 1.25f;
            else if (r + 1 == bossRowIdx) w = 1.62f;
            else if (hasSecondBoss && r == bossRowIdx) w = 1.45f;
            gapWeights[r] = w;
            totalWeight += w;
        }

        float rowStep = totalWeight > 0f ? usableH / totalWeight : usableH;
        float[] rowY = new float[rowCount];
        rowY[0] = botY;
        float acc = 0f;
        for (int r = 1; r < rowCount; r++)
        {
            acc += gapWeights[r - 1] * rowStep;
            rowY[r] = botY - acc;
        }

        // Horizontal safe band: clear of left tools/toggles and right vanilla legend,
        // adapting smoothly to narrow/4:3 as well as widescreen displays.
        float safeLeft = Mathf.Clamp(leftClearance, 24f, screenSize.X * 0.24f);
        float safeRight = Mathf.Clamp(rightLegendX, screenSize.X * 0.65f, screenSize.X - 24f);
        float bandWidth = Math.Max(220f, safeRight - safeLeft);
        float bandCenterX = (safeLeft + safeRight) * 0.5f;

        // Anchor lane spacing to rawSpan (never activeSpan) so Compress narrows the silhouette
        // inward toward the center instead of stretching compressed columns wider. Cap the
        // horizontal/vertical aspect ratio at 1.82x rowStep so the map breathes comfortably while
        // keeping diagonal slopes calm on both 16:9 and 21:9 screens.
        float maxGridWidth = Math.Min(bandWidth - 64f, screenSize.X * 0.56f);
        float laneSpacing = Math.Min(maxGridWidth / rawSpan, rowStep * 1.82f);

        // Visual base radius constrained by both vertical row spacing and horizontal lane spacing.
        float baseR = Mathf.Clamp(Math.Min(rowStep * 0.43f, laneSpacing * 0.34f), 12f, 22f);

        Vector2 vMin = new(float.PositiveInfinity, float.PositiveInfinity);
        Vector2 vMax = new(float.NegativeInfinity, float.NegativeInfinity);
        Vector2 fMin = new(float.PositiveInfinity, float.PositiveInfinity);
        Vector2 fMax = new(float.NegativeInfinity, float.NegativeInfinity);

        foreach (KeyValuePair<MapCoord, NMapPoint> kv in points)
        {
            MapCoord coord = kv.Key;
            NMapPoint node = kv.Value;
            int rIdx = rowIndexOf[coord.row];
            float lane = compress && smoothLanes.TryGetValue(coord, out float sl) ? sl : coord.col;

            float sx = bandCenterX + (lane - activeMidLane) * laneSpacing;
            float sy = rowY[rIdx];
            var screenCenter = new Vector2(sx, sy);

            MapPointType pt = node.Point?.PointType ?? MapPointType.Monster;
            float radius = pt switch
            {
                MapPointType.Boss    => hasSecondBoss ? baseR * 1.48f : baseR * 1.88f,
                MapPointType.Ancient => baseR * 1.42f,
                MapPointType.Elite   => baseR * 1.26f,
                _                    => baseR,
            };

            float extent = snapshots.TryGetValue(coord, out NodeSnapshot? snap) && snap.NaturalExtent > 1f
                ? snap.NaturalExtent
                : Math.Max(1f, Math.Max(node.Size.X, node.Size.Y));

            // NBossMapPoint's Spine/placeholder control has inner margins around the boss art.
            float targetDiameter = pt == MapPointType.Boss ? radius * 2.35f : radius * 2.0f;
            float uniformScale = Mathf.Clamp(targetDiameter / extent, 0.14f, 1.5f);

            layout.Placements[coord] = new NodePlacement
            {
                Coord = coord,
                Node = node,
                ScreenCenter = screenCenter,
                LocalCenterInPoints = screenCenter - pointsOffset,
                FlatScale = new Vector2(uniformScale, uniformScale),
                VisualRadius = radius,
            };

            if (snap != null)
            {
                Vector2 vCenter = pointsOffset + snap.VanillaCenterInPoints;
                if (vCenter.X < vMin.X) vMin.X = vCenter.X;
                if (vCenter.Y < vMin.Y) vMin.Y = vCenter.Y;
                if (vCenter.X > vMax.X) vMax.X = vCenter.X;
                if (vCenter.Y > vMax.Y) vMax.Y = vCenter.Y;

                if (screenCenter.X < fMin.X) fMin.X = screenCenter.X;
                if (screenCenter.Y < fMin.Y) fMin.Y = screenCenter.Y;
                if (screenCenter.X > fMax.X) fMax.X = screenCenter.X;
                if (screenCenter.Y > fMax.Y) fMax.Y = screenCenter.Y;
            }
        }

        // Exact affine transform for NMapDrawings (leaves Drawings.Size untouched so SubViewport
        // stays sharp and ToNetPosition/FromNetPosition stay 100% co-op & Classic compatible).
        Vector2 vSpan = vMax - vMin;
        Vector2 fSpan = fMax - fMin;
        float dScaleY = vSpan.Y > 1f ? fSpan.Y / vSpan.Y : 1f;
        float dScaleX = vSpan.X > 1f ? fSpan.X / vSpan.X : dScaleY;
        var dScale = new Vector2(Mathf.Clamp(dScaleX, 0.1f, 3f), Mathf.Clamp(dScaleY, 0.1f, 3f));
        Vector2 dPos = float.IsFinite(vMin.X)
            ? fMin - (vMin - vanillaDrawingsPos) * dScale
            : vanillaDrawingsPos;

        return new ComputedFlatLayout
        {
            DrawingsTransform = new DrawingsAffine(dPos, dScale),
            BaseRadius = baseR,
        }.WithPlacements(layout.Placements);
    }

    private static ComputedFlatLayout WithPlacements(
        this ComputedFlatLayout target,
        Dictionary<MapCoord, NodePlacement> placements)
    {
        foreach (KeyValuePair<MapCoord, NodePlacement> kv in placements)
            target.Placements[kv.Key] = kv.Value;
        return target;
    }

    /// <summary>
    /// Forward reachability BFS from the player's current position and currently travelable
    /// frontier (relic-aware, including Wing Boots). Before any room is visited, all nodes
    /// reachable from StartingMapPoint are reachable.
    /// </summary>
    internal static HashSet<MapCoord> ComputeReachable(
        IReadOnlyDictionary<MapCoord, NMapPoint> points,
        RunState? runState,
        NMapPoint? startingPoint)
    {
        var reachable = new HashSet<MapCoord>();
        var queue = new Queue<MapCoord>();

        void Enqueue(MapCoord coord)
        {
            if (points.ContainsKey(coord) && reachable.Add(coord))
                queue.Enqueue(coord);
        }

        if (runState?.CurrentMapCoord is MapCoord cur)
            Enqueue(cur);

        foreach (KeyValuePair<MapCoord, NMapPoint> kv in points)
        {
            if (kv.Value.State == MapPointState.Travelable)
                Enqueue(kv.Key);
        }

        if (queue.Count == 0 && startingPoint?.Point != null)
            Enqueue(startingPoint.Point.coord);

        while (queue.Count > 0)
        {
            MapCoord c = queue.Dequeue();
            if (!points.TryGetValue(c, out NMapPoint? node) || node.Point == null)
                continue;
            foreach (MapPoint child in node.Point.Children)
                Enqueue(child.coord);
        }

        return reachable;
    }

    /// <summary>
    /// Resolve a visited Unknown ('?') room's revealed room type from RunState.MapPointHistory so
    /// visited '?' rooms wear the Neon Rim color of what they turned out to be.
    /// </summary>
    internal static MapPointType EffectiveRoomType(NMapPoint node, RunState? runState)
    {
        MapPointType raw = node.Point?.PointType ?? MapPointType.Monster;
        if (raw != MapPointType.Unknown || node.State != MapPointState.Traveled || runState == null)
            return raw;

        int actIdx = runState.CurrentActIndex;
        if (actIdx < 0 || actIdx >= runState.MapPointHistory.Count)
            return raw;
        IReadOnlyList<MapPointHistoryEntry> actHistory = runState.MapPointHistory[actIdx];
        int row = node.Point!.coord.row;
        if (row < 0 || row >= actHistory.Count || actHistory[row].Rooms.Count == 0)
            return raw;

        return actHistory[row].Rooms[0].RoomType switch
        {
            RoomType.Monster  => MapPointType.Monster,
            RoomType.Elite    => MapPointType.Elite,
            RoomType.Shop     => MapPointType.Shop,
            RoomType.Treasure => MapPointType.Treasure,
            RoomType.RestSite => MapPointType.RestSite,
            RoomType.Boss     => MapPointType.Boss,
            _                 => MapPointType.Unknown,
        };
    }

    /// <summary>
    /// Build edge-trimmed path segments (or smooth rim-fanned cubic S-curves when useCurves is true)
    /// in _points local coordinates for FlatPathsLayer.
    /// </summary>
    internal static List<EdgeVisual> BuildEdgeVisuals(
        IReadOnlyDictionary<MapCoord, NMapPoint> points,
        ComputedFlatLayout layout,
        HashSet<MapCoord> reachable,
        RunState? runState,
        (MapCoord From, MapCoord To)? travelingEdge,
        bool hideUnreachable,
        bool useCurves)
    {
        var traveledEdges = new HashSet<(MapCoord, MapCoord)>();
        if (runState != null)
        {
            IReadOnlyList<MapCoord> visited = runState.VisitedMapCoords;
            for (int i = 0; i < visited.Count - 1; i++)
                traveledEdges.Add((visited[i], visited[i + 1]));
        }

        var edges = new List<EdgeVisual>();
        foreach (KeyValuePair<MapCoord, NMapPoint> kv in points)
        {
            MapCoord fromCoord = kv.Key;
            NMapPoint fromNode = kv.Value;
            if (fromNode.Point == null || !layout.Placements.TryGetValue(fromCoord, out NodePlacement? fromP))
                continue;

            bool fromGhost = IsGhosted(fromNode, reachable, hideUnreachable);
            MapPointType fromType = fromNode.Point.PointType;
            foreach (MapPoint child in fromNode.Point.Children)
            {
                MapCoord toCoord = child.coord;
                if (!points.TryGetValue(toCoord, out NMapPoint? toNode)
                    || !layout.Placements.TryGetValue(toCoord, out NodePlacement? toP))
                    continue;

                Vector2 a = fromP.LocalCenterInPoints;
                Vector2 b = toP.LocalCenterInPoints;
                Vector2 delta = b - a;
                float dist = delta.Length();
                if (dist < 1f) continue;

                EdgeVisualState state;
                if (travelingEdge is { } te && te.From.Equals(fromCoord) && te.To.Equals(toCoord))
                    state = EdgeVisualState.Traveling;
                else if (traveledEdges.Contains((fromCoord, toCoord)))
                    state = EdgeVisualState.Traveled;
                else if (fromGhost || IsGhosted(toNode, reachable, hideUnreachable))
                    state = EdgeVisualState.Ghosted;
                else
                    state = EdgeVisualState.Reachable;

                // Trim neatly outside each icon's neon rim while leaving clear visible path length
                // even on tight vertical hops.
                float trimA = Math.Min(fromP.VisualRadius * 0.88f + 2.5f, dist * 0.39f);
                float trimB = Math.Min(toP.VisualRadius * 0.88f + 2.5f, dist * 0.39f);

                if (useCurves && MathF.Abs(delta.X) >= 2.0f && delta.Y < -2.0f)
                {
                    MapPointType toType = toNode.Point?.PointType ?? MapPointType.Monster;
                    bool isFan = fromType == MapPointType.Ancient || toType == MapPointType.Boss;
                    float rawAng = MathF.Atan2(delta.X, -delta.Y);
                    // Exit near the node-to-node shoulder direction so wide diagonals keep vertical
                    // breathing room and multi-edge junctions separate cleanly around each icon rim.
                    float maxAng = (isFan ? 52f : 56f) * (MathF.PI / 180f);
                    float ang = Mathf.Clamp(rawAng * (isFan ? 0.78f : 0.86f), -maxAng, maxAng);

                    Vector2 start = new(a.X + MathF.Sin(ang) * trimA, a.Y - MathF.Cos(ang) * trimA);
                    Vector2 end = new(b.X - MathF.Sin(ang) * trimB, b.Y + MathF.Cos(ang) * trimB);
                    float spanX = end.X - start.X;
                    float spanY = start.Y - end.Y;
                    float chordLen = MathF.Sqrt(spanX * spanX + spanY * spanY);
                    if (spanY >= 4f && chordLen >= 4f)
                    {
                        // Subtle chord-relative S-curve: keep tangents within a small bounded bend of
                        // the rim-to-rim chord instead of forcing near-vertical tangents, so wide
                        // diagonals glide smoothly without flattening into horizontal shelves.
                        float chordAng = MathF.Atan2(spanX, spanY);
                        float maxBend = (isFan ? 14f : 10f) * (MathF.PI / 180f);
                        float bend = Mathf.Clamp(chordAng * (isFan ? 0.28f : 0.22f), -maxBend, maxBend);
                        float tanAng = chordAng - bend;
                        float cosTan = MathF.Cos(tanAng);
                        float ctrlLen = chordLen * 0.30f;
                        if (cosTan > 0.05f)
                            ctrlLen = Math.Min(ctrlLen, (spanY * 0.42f) / cosTan);
                        Vector2 c1 = new(start.X + MathF.Sin(tanAng) * ctrlLen, start.Y - cosTan * ctrlLen);
                        Vector2 c2 = new(end.X - MathF.Sin(tanAng) * ctrlLen, end.Y + cosTan * ctrlLen);
                        edges.Add(new EdgeVisual(start, c1, c2, end, state));
                        continue;
                    }
                }

                Vector2 dir = delta / dist;
                edges.Add(new EdgeVisual(a + dir * trimA, b - dir * trimB, state));
            }
        }
        return edges;
    }

    internal static bool IsGhosted(NMapPoint node, HashSet<MapCoord> reachable, bool hideUnreachable)
    {
        if (!hideUnreachable || node.Point == null)
            return false;
        if (node.State == MapPointState.Traveled || node.State == MapPointState.Travelable)
            return false;
        return !reachable.Contains(node.Point.coord);
    }
}
