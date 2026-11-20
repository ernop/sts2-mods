using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Runs;

namespace FlatMap;

// FlatMap (One-Page Map) — in-place whole-act presentation of STS2's real NMapScreen.
//
// Instead of maintaining a parallel ICapstoneScreen and borrowing/reparenting vanilla controls,
// FlatMap drives the live NMapScreen directly in place:
//   * All standard input & lifecycle paths remain 100% native: M / Tab / controller toggle,
//     top-bar map button, ESC / back, NMapRoom button lock, Opened/Closed signals (including
//     shop music), NOverlayStack, ActiveScreenContext, CombatManager.Pause, visited-room
//     history hover tips, live quest markers, run-seeded NMapCircleVfx, NMapNodeSelectVfx,
//     animated Spine NBossMapPoint (+ SecondBoss), vanilla _mapLegend + highlight, native
//     Drawings + _drawingTools, multiplayer votes/pings/cursors, and controller focus.
//   * In Flat mode, NMapPoints are placed & scaled into a single viewport-responsive screen
//     (using either raw columns or the exact DP lane compressor from layout/MapLayout.cs),
//     _mapContainer is pinned at (0, 0), scrolling & the 3s start-of-act scroll tween are
//     suppressed, Drawings receives an exact affine (Position, Scale) transform without
//     changing Drawings.Size, and lightweight event-driven overlays render the flat Act
//     backdrop, edge-trimmed dashed paths, bottom-left switches, and bottom-right act title.
//   * In Classic mode (or if disabled on an incompatible update), every transform and material
//     reverts losslessly to untouched vanilla NMapScreen.
[ModInitializer(nameof(Init))]
public static class FlatMapMod
{
    // Hotkey to flip Flat <-> Classic while NMapScreen is open and current.
    public const Key ToggleFlatMapKey = Key.F;

    // FlatMap was built and tested against this game version. On another build, every private
    // hook and every game member our compiled code calls is preflighted before Harmony changes
    // anything. Missing members disable the mod and leave vanilla NMapScreen running.
    public const string TestedGameVersion = "v0.113.1";

    public static void Init()
    {
        if (!ModRuntime.TryEnable(typeof(FlatMapMod).Assembly))
            return;

        string? gameVersion = ReleaseInfoManager.Instance.ReleaseInfo?.Version;
        string versionNote = gameVersion == TestedGameVersion
            ? ""
            : $" — NOTE: game '{gameVersion ?? "unknown"}' is not the tested {TestedGameVersion}; re-verify";
        Log.Info($"[FlatMap] loaded — flat map view (in-place NMapScreen){versionNote}");
    }
}

internal sealed class FlatMapScreenState
{
    internal FlatBackdrop? Backdrop;
    internal FlatPathsLayer? PathsLayer;
    internal FlatChromeOverlay? Chrome;

    internal Dictionary<MapCoord, NodeSnapshot> Snapshots = new();
    internal Dictionary<MapCoord, float> SmoothLanes = new();
    internal ComputedFlatLayout? Layout;
    internal HashSet<MapCoord> Reachable = new();

    internal bool IsFlatApplied;
    internal bool RestoreFlatAfterShare;
    internal (MapCoord From, MapCoord To)? TravelingEdge;
    internal Vector2 SavedClassicContainerPos;
    internal bool HasSavedClassicContainerPos;
    internal Vector2 VanillaMarkerPivot;
    internal bool HasVanillaMarkerPivot;
}

internal static class FlatMapController
{
    // NMapScreen private members (all cataloged in ModRuntime.HookCatalog).
    internal static readonly FieldInfo MapContainerField = Reflect.Field(typeof(NMapScreen), "_mapContainer");
    internal static readonly FieldInfo MapBgContainerField = Reflect.Field(typeof(NMapScreen), "_mapBgContainer");
    internal static readonly FieldInfo PathsContainerField = Reflect.Field(typeof(NMapScreen), "_pathsContainer");
    internal static readonly FieldInfo PointsField = Reflect.Field(typeof(NMapScreen), "_points");
    internal static readonly FieldInfo MarkerField = Reflect.Field(typeof(NMapScreen), "_marker");
    internal static readonly FieldInfo MapLegendField = Reflect.Field(typeof(NMapScreen), "_mapLegend");
    internal static readonly FieldInfo DrawingToolsField = Reflect.Field(typeof(NMapScreen), "_drawingTools");
    internal static readonly FieldInfo BackButtonField = Reflect.Field(typeof(NMapScreen), "_backButton");
    internal static readonly FieldInfo ShareButtonField = Reflect.Field(typeof(NMapScreen), "_shareButton");
    internal static readonly FieldInfo PointDictField = Reflect.Field(typeof(NMapScreen), "_mapPointDictionary");
    internal static readonly FieldInfo StartingPointField = Reflect.Field(typeof(NMapScreen), "_startingPointNode");
    internal static readonly FieldInfo SecondBossPointField = Reflect.Field(typeof(NMapScreen), "_secondBossPointNode");
    internal static readonly FieldInfo RunStateField = Reflect.Field(typeof(NMapScreen), "_runState");
    internal static readonly FieldInfo HasPlayedAnimField = Reflect.Field(typeof(NMapScreen), "_hasPlayedAnimation");
    internal static readonly FieldInfo TargetDragPosField = Reflect.Field(typeof(NMapScreen), "_targetDragPos");
    internal static readonly FieldInfo ActAnimTweenField = Reflect.Field(typeof(NMapScreen), "_actAnimTween");
    internal static readonly MethodInfo InitMapPromptMethod = Reflect.Method(typeof(NMapScreen), "InitMapPrompt");

    // NMapPoint / subclass private members.
    internal static readonly FieldInfo PointRunStateField = Reflect.Field(typeof(NMapPoint), "_runState");
    internal static readonly MethodInfo PointHoverScaleGetter = Reflect.PropertyGetter(typeof(NMapPoint), "HoverScale");
    internal static readonly MethodInfo PointDownScaleGetter = Reflect.PropertyGetter(typeof(NMapPoint), "DownScale");
    internal static readonly MethodInfo PointIsInputAllowedMethod = Reflect.Method(typeof(NMapPoint), "IsInputAllowed");
    internal static readonly MethodInfo ClickableIsFocusedGetter = Reflect.PropertyGetter(typeof(NClickableControl), "IsFocused");

    internal static readonly FieldInfo NormalIconContainerField = Reflect.Field(typeof(NNormalMapPoint), "_iconContainer");
    internal static readonly FieldInfo NormalIconField = Reflect.Field(typeof(NNormalMapPoint), "_icon");
    internal static readonly FieldInfo NormalOutlineField = Reflect.Field(typeof(NNormalMapPoint), "_outline");
    internal static readonly FieldInfo NormalQuestIconField = Reflect.Field(typeof(NNormalMapPoint), "_questIcon");
    internal static readonly FieldInfo NormalTweenField = Reflect.Field(typeof(NNormalMapPoint), "_tween");

    internal static readonly FieldInfo AncientIconField = Reflect.Field(typeof(NAncientMapPoint), "_icon");
    internal static readonly FieldInfo AncientOutlineField = Reflect.Field(typeof(NAncientMapPoint), "_outline");
    internal static readonly FieldInfo AncientTweenField = Reflect.Field(typeof(NAncientMapPoint), "_tween");
    internal static readonly FieldInfo AncientElapsedField = Reflect.Field(typeof(NAncientMapPoint), "_elapsedTime");

    internal static readonly FieldInfo MarkerTweenField = Reflect.Field(typeof(NMapMarker), "_tween");

    private static readonly ConditionalWeakTable<NMapScreen, FlatMapScreenState> States = new();
    private static readonly List<WeakReference<NMapScreen>> LiveScreens = new();

    static FlatMapController() => ModRuntime.Disabled += OnModDisabled;

    internal static FlatMapScreenState GetState(NMapScreen screen) => States.GetOrCreateValue(screen);

    internal static bool IsFlatActive(NMapScreen? screen) =>
        screen != null && GodotObject.IsInstanceValid(screen)
        && States.TryGetValue(screen, out FlatMapScreenState? st) && st.IsFlatApplied;

    internal static bool TryFindScreen(Node node, out NMapScreen screen, out FlatMapScreenState state)
    {
        for (Node? p = node; p != null; p = p.GetParent())
        {
            if (p is NMapScreen ms)
            {
                if (States.TryGetValue(ms, out FlatMapScreenState? st))
                {
                    screen = ms;
                    state = st;
                    return true;
                }
                // Ancestor NMapScreen has not finished _Ready yet; do not fall back to
                // NMapScreen.Instance (which dereferences NRun.Instance.GlobalUi before
                // NRun._Ready assigns GlobalUi!).
                screen = null!;
                state = null!;
                return false;
            }
        }
        NMapScreen? inst = NRun.Instance?.GlobalUi?.MapScreen;
        if (inst != null && GodotObject.IsInstanceValid(inst) && States.TryGetValue(inst, out FlatMapScreenState? stInst))
        {
            screen = inst;
            state = stInst;
            return true;
        }
        screen = null!;
        state = null!;
        return false;
    }

    internal static void AttachOverlays(NMapScreen screen)
    {
        FlatMapScreenState state = GetState(screen);
        if (state.Backdrop != null && GodotObject.IsInstanceValid(state.Backdrop))
            return;

        TrackScreen(screen);

        var mapContainer = (Control)MapContainerField.GetValue(screen)!;
        var points = (Control)PointsField.GetValue(screen)!;

        // 1. FlatBackdrop on NMapScreen right before TheMap.
        var backdrop = new FlatBackdrop();
        screen.AddChild(backdrop);
        screen.MoveChild(backdrop, mapContainer.GetIndex());
        state.Backdrop = backdrop;

        // 2. FlatPathsLayer inside TheMap right before Points (exact same coordinate frame).
        var pathsLayer = new FlatPathsLayer();
        mapContainer.AddChild(pathsLayer);
        mapContainer.MoveChild(pathsLayer, points.GetIndex());
        state.PathsLayer = pathsLayer;

        // 3. FlatChromeOverlay on top of NMapScreen (toggles + act title).
        var chrome = new FlatChromeOverlay(
            onFlatToggled: on => SetFlatMode(screen, on),
            onCompressToggled: on => SetCompress(screen, on),
            onHideUnreachableToggled: on => SetHideUnreachable(screen, on));
        screen.AddChild(chrome);
        chrome.LayoutControls();
        state.Chrome = chrome;
    }

    internal static void OnBeforeSetMap(NMapScreen screen)
    {
        if (!States.TryGetValue(screen, out FlatMapScreenState? state))
            return;
        // Clear Flat state before vanilla SetMap spawns new act nodes so NNormalMapPoint._Ready
        // runs unmodified and CaptureSnapshot records pristine vanilla ShaderMaterials on Act 2+.
        state.IsFlatApplied = false;
        state.TravelingEdge = null;
        state.RestoreFlatAfterShare = false;
        state.HasSavedClassicContainerPos = false;
        state.Snapshots.Clear();
        state.SmoothLanes.Clear();
        state.Layout = null;
        state.Reachable.Clear();
    }

    internal static void OnSetMap(NMapScreen screen)
    {
        AttachOverlays(screen);
        FlatMapScreenState state = GetState(screen);
        state.TravelingEdge = null;
        state.RestoreFlatAfterShare = false;
        state.HasSavedClassicContainerPos = false;
        state.Snapshots.Clear();
        state.Layout = null;
        state.IsFlatApplied = false;

        var dict = GetPointDictionary(screen);
        foreach (KeyValuePair<MapCoord, NMapPoint> kv in dict)
            state.Snapshots[kv.Key] = CaptureSnapshot(kv.Value);

        state.SmoothLanes = FlatMapLayoutEngine.ComputeSmoothLanes(dict);

        if (FlatMapConfig.DumpMapGraph)
            DumpGraph(screen, dict);

        if (FlatMapConfig.PreferFlatMap)
            ApplyFlat(screen, state, animateBackdrop: false);
        else
            SyncChrome(screen, state);
    }

    internal static void OnOpen(NMapScreen screen, bool wasAlreadyOpen)
    {
        if (wasAlreadyOpen)
            return;

        AttachOverlays(screen);
        FlatMapScreenState state = GetState(screen);
        state.Chrome?.ShowImmediate();

        // Always record the exact vanilla scroll position NMapScreen.Open just computed for the
        // player's current floor, so switching Flat -> Classic lands on the right floor.
        var mapContainer = (Control)MapContainerField.GetValue(screen)!;
        state.SavedClassicContainerPos = mapContainer.Position;
        state.HasSavedClassicContainerPos = true;

        // Capture snapshots if SetMap ran before our hook (defensive safety).
        if (state.Snapshots.Count == 0)
        {
            var dict = GetPointDictionary(screen);
            foreach (KeyValuePair<MapCoord, NMapPoint> kv in dict)
                state.Snapshots[kv.Key] = CaptureSnapshot(kv.Value);
            state.SmoothLanes = FlatMapLayoutEngine.ComputeSmoothLanes(dict);
        }

        if (FlatMapConfig.PreferFlatMap)
        {
            ApplyFlat(screen, state, animateBackdrop: true);
        }
        else
        {
            if (state.IsFlatApplied)
                RestoreClassic(screen, state);
            else
                SyncChrome(screen, state);
        }
    }

    internal static void OnClose(NMapScreen screen, bool animateOut)
    {
        if (!States.TryGetValue(screen, out FlatMapScreenState? state))
            return;
        if (animateOut)
        {
            state.Backdrop?.FadeOut(0.15);
            state.Chrome?.FadeOut(0.15);
        }
        else if (state.Backdrop != null && GodotObject.IsInstanceValid(state.Backdrop))
        {
            state.Backdrop.Visible = false;
        }
    }

    internal static void SetFlatMode(NMapScreen screen, bool flat)
    {
        FlatMapConfig.PreferFlatMap = flat;
        FlatMapScreenState state = GetState(screen);
        if (flat)
            ApplyFlat(screen, state, animateBackdrop: false);
        else
            RestoreClassic(screen, state);
    }

    internal static void SetCompress(NMapScreen screen, bool compress)
    {
        FlatMapConfig.CompressMap = compress;
        FlatMapScreenState state = GetState(screen);
        if (state.IsFlatApplied)
            ApplyFlat(screen, state, animateBackdrop: false);
        else
            SyncChrome(screen, state);
    }

    internal static void SetHideUnreachable(NMapScreen screen, bool hide)
    {
        FlatMapConfig.HideUnreachable = hide;
        FlatMapScreenState state = GetState(screen);
        if (state.IsFlatApplied)
            RefreshVisuals(screen, state);
        else
            SyncChrome(screen, state);
    }

    internal static void PinContainerAtOrigin(NMapScreen screen)
    {
        if (ActAnimTweenField.GetValue(screen) is Tween actTween && GodotObject.IsInstanceValid(actTween))
        {
            actTween.Kill();
            ActAnimTweenField.SetValue(screen, null);
        }
        var mapContainer = (Control)MapContainerField.GetValue(screen)!;
        mapContainer.Position = Vector2.Zero;
        TargetDragPosField.SetValue(screen, Vector2.Zero);
    }

    internal static void ApplyFlat(NMapScreen screen, FlatMapScreenState state, bool animateBackdrop)
    {
        var dict = GetPointDictionary(screen);
        if (dict.Count == 0)
            return;

        var mapContainer = (Control)MapContainerField.GetValue(screen)!;
        var mapBg = (Control)MapBgContainerField.GetValue(screen)!;
        var pathsContainer = (Control)PathsContainerField.GetValue(screen)!;
        var points = (Control)PointsField.GetValue(screen)!;
        var runState = RunStateField.GetValue(screen) as RunState;
        var secondBoss = SecondBossPointField.GetValue(screen) as NMapPoint;

        if (!state.IsFlatApplied)
        {
            state.SavedClassicContainerPos = mapContainer.Position;
            state.HasSavedClassicContainerPos = true;
        }

        PinContainerAtOrigin(screen);

        // Hide vanilla parchment background and dotted path textures; show our flat backdrop & paths.
        mapBg.Visible = false;
        pathsContainer.Visible = false;

        Vector2 screenSize = screen.Size.X > 10f && screen.Size.Y > 10f
            ? screen.Size
            : (screen.IsInsideTree() ? screen.GetViewportRect().Size : new Vector2(1920f, 1080f));

        // Measure live left/right clearance from vanilla drawing tools, toggles, and legend.
        float leftClearance = 190f;
        float rightLegendX = screenSize.X * 0.80f - 20f;
        if (MapLegendField.GetValue(screen) is Control legend && GodotObject.IsInstanceValid(legend)
            && legend.Visible && legend.Position.X > screenSize.X * 0.5f)
        {
            rightLegendX = Math.Min(rightLegendX, legend.Position.X - 24f);
        }
        if (DrawingToolsField.GetValue(screen) is Control tools && GodotObject.IsInstanceValid(tools) && tools.Visible)
        {
            float toolsCenterX = tools.Position.X + tools.Size.X * 0.5f;
            if (toolsCenterX < screenSize.X * 0.5f)
                leftClearance = Math.Max(leftClearance, tools.Position.X + tools.Size.X + 20f);
            else
                rightLegendX = Math.Min(rightLegendX, tools.Position.X - 20f);
        }
        if (state.Chrome != null && GodotObject.IsInstanceValid(state.Chrome))
            leftClearance = Math.Max(leftClearance, state.Chrome.LeftTogglesRightEdge + 16f);

        float topClearance = 110f;
        if (NRun.Instance?.GlobalUi?.TopBar is Control topBar && GodotObject.IsInstanceValid(topBar))
            topClearance = Math.Max(topClearance, topBar.Size.Y + 42f);

        // Vanilla Drawings origin inside TheMap (exact RepositionBasedOnBackground formula).
        NMapDrawings drawings = screen.Drawings;
        Vector2 vanillaDrawingsPos = new(
            mapBg.Position.X + (mapBg.Size.X - drawings.Size.X) * 0.5f,
            mapBg.Position.Y);

        ComputedFlatLayout layout = FlatMapLayoutEngine.Compute(
            dict,
            state.Snapshots,
            state.SmoothLanes,
            FlatMapConfig.CompressMap,
            hasSecondBoss: secondBoss != null,
            screenSize,
            points.Position,
            vanillaDrawingsPos,
            leftClearance,
            rightLegendX,
            topClearance);
        state.Layout = layout;
        state.IsFlatApplied = true;

        // Place and scale every real NMapPoint in place.
        foreach (KeyValuePair<MapCoord, NodePlacement> kv in layout.Placements)
        {
            NodePlacement p = kv.Value;
            if (!GodotObject.IsInstanceValid(p.Node)) continue;
            p.Node.Position = p.LocalCenterInPoints - p.Node.PivotOffset;
            p.Node.Scale = p.FlatScale;
        }

        // Apply exact affine transform to native NMapDrawings without touching Drawings.Size.
        if (GodotObject.IsInstanceValid(drawings))
        {
            drawings.Position = layout.DrawingsTransform.Position;
            drawings.Scale = layout.DrawingsTransform.Scale;
        }

        Color bgColor = runState?.Act?.MapBgColor ?? new Color(0.84f, 0.80f, 0.70f);
        state.Backdrop?.SetActive(true, bgColor, animate: animateBackdrop);

        RefreshVisuals(screen, state);
    }

    internal static void RestoreClassic(NMapScreen screen, FlatMapScreenState state)
    {
        if (!state.IsFlatApplied)
        {
            SyncChrome(screen, state);
            return;
        }

        state.IsFlatApplied = false;
        state.TravelingEdge = null;

        var dict = GetPointDictionary(screen);
        foreach (KeyValuePair<MapCoord, NMapPoint> kv in dict)
        {
            NMapPoint node = kv.Value;
            if (!GodotObject.IsInstanceValid(node)) continue;
            if (state.Snapshots.TryGetValue(kv.Key, out NodeSnapshot? snap))
            {
                node.Position = snap.VanillaPosition;
                node.Scale = snap.VanillaScale;
                RestoreNodeMaterials(node, snap);
            }
        }

        var mapBg = (Control)MapBgContainerField.GetValue(screen)!;
        var pathsContainer = (Control)PathsContainerField.GetValue(screen)!;
        mapBg.Visible = true;
        pathsContainer.Visible = true;

        NMapDrawings drawings = screen.Drawings;
        if (GodotObject.IsInstanceValid(drawings))
        {
            drawings.Scale = Vector2.One;
            drawings.RepositionBasedOnBackground(mapBg);
        }

        state.Backdrop?.SetActive(false, Colors.Black);
        state.PathsLayer?.Clear();

        if (MarkerField.GetValue(screen) is NMapMarker marker && GodotObject.IsInstanceValid(marker))
        {
            if (MarkerTweenField.GetValue(marker) is Tween mt && GodotObject.IsInstanceValid(mt))
                mt.Kill();
            marker.Rotation = 0f;
            marker.Scale = Vector2.One;
            if (state.HasVanillaMarkerPivot)
                marker.PivotOffset = state.VanillaMarkerPivot;

            var runState = RunStateField.GetValue(screen) as RunState;
            if (runState?.CurrentMapCoord is MapCoord cur && dict.TryGetValue(cur, out NMapPoint? curNode)
                && GodotObject.IsInstanceValid(curNode) && marker.Visible)
            {
                marker.SetMapPoint(curNode);
            }
        }

        if (state.HasSavedClassicContainerPos)
        {
            var mapContainer = (Control)MapContainerField.GetValue(screen)!;
            mapContainer.Position = state.SavedClassicContainerPos;
            TargetDragPosField.SetValue(screen, state.SavedClassicContainerPos);
        }

        // Restore vanilla shader colors on all points now that IsFlatApplied == false and
        // every NNormalMapPoint._icon.Material has been restored.
        screen.RefreshAllPointVisuals();
        SyncChrome(screen, state);
    }

    internal static void RefreshVisuals(NMapScreen screen, FlatMapScreenState state)
    {
        if (!state.IsFlatApplied || state.Layout == null)
        {
            SyncChrome(screen, state);
            return;
        }

        var dict = GetPointDictionary(screen);
        var runState = RunStateField.GetValue(screen) as RunState;
        var startNode = StartingPointField.GetValue(screen) as NMapPoint;
        var points = (Control)PointsField.GetValue(screen)!;

        state.Reachable = FlatMapLayoutEngine.ComputeReachable(dict, runState, startNode);

        // Apply Neon Rims + opaque icons to every point.
        foreach (NMapPoint node in dict.Values)
        {
            if (!GodotObject.IsInstanceValid(node)) continue;
            ApplyNodeFlatVisualInstant(node, screen, state, runState);
        }

        // Redraw dashed paths layer (event-driven, zero idle work).
        if (state.PathsLayer != null && GodotObject.IsInstanceValid(state.PathsLayer))
        {
            Color traveledColor = runState?.Act?.MapTraveledColor ?? new Color(0.14f, 0.11f, 0.08f);
            Color untraveledColor = runState?.Act?.MapUntraveledColor ?? new Color(0.28f, 0.24f, 0.18f);
            List<EdgeVisual> edges = FlatMapLayoutEngine.BuildEdgeVisuals(
                dict, state.Layout, state.Reachable, runState, state.TravelingEdge,
                FlatMapConfig.HideUnreachable, FlatMapConfig.CompressMap);
            state.PathsLayer.Visible = true;
            state.PathsLayer.UpdatePaths(edges, traveledColor, untraveledColor, points.Position);
        }

        // Keep left-side marker anchored to the current visited node.
        if (MarkerField.GetValue(screen) is NMapMarker marker && GodotObject.IsInstanceValid(marker) && marker.Visible)
        {
            if (runState?.CurrentMapCoord is MapCoord cur && dict.TryGetValue(cur, out NMapPoint? curNode))
                PositionMarkerLeft(screen, state, marker, curNode);
        }

        SyncChrome(screen, state);
    }

    internal static void PositionMarkerLeft(NMapScreen screen, FlatMapScreenState state, NMapMarker marker, Control targetNode)
    {
        if (!state.IsFlatApplied || state.Layout == null || targetNode is not NMapPoint mp || mp.Point == null)
            return;
        if (!state.Layout.Placements.TryGetValue(mp.Point.coord, out NodePlacement? p))
            return;

        if (!state.HasVanillaMarkerPivot)
        {
            state.VanillaMarkerPivot = marker.PivotOffset;
            state.HasVanillaMarkerPivot = true;
        }

        if (MarkerTweenField.GetValue(marker) is Tween mt && GodotObject.IsInstanceValid(mt))
            mt.Kill();

        marker.PivotOffset = marker.Size * 0.5f;
        float mScale = Mathf.Clamp(p.VisualRadius / 28f, 0.45f, 0.85f);
        float halfW = Math.Max(12f, marker.Size.Y * 0.5f * mScale);
        Vector2 markerCenter = p.ScreenCenter - new Vector2(p.VisualRadius + halfW + 6f, 0f);
        marker.Position = markerCenter - marker.PivotOffset;
        marker.Rotation = -Mathf.Pi * 0.5f; // rotates downward vanilla arrow to point right (->)
        marker.Scale = new Vector2(mScale, mScale);
    }

    internal static (Color IconColor, Color RimColor, float RimScale) ComputeRimStyle(
        NMapPoint node,
        NMapScreen screen,
        FlatMapScreenState state,
        RunState? runState,
        bool isHovered)
    {
        Color bgColor = runState?.Act?.MapBgColor ?? new Color(0.84f, 0.80f, 0.70f);
        bool ghost = FlatMapLayoutEngine.IsGhosted(node, state.Reachable, FlatMapConfig.HideUnreachable);
        if (ghost)
            return (new Color(1f, 1f, 1f, FlatMapLayoutEngine.GhostAlpha), bgColor, 1.0f);

        bool frontier = node.State == MapPointState.Travelable && screen.IsTravelEnabled;
        if (frontier)
            return (Colors.White, Colors.White, isHovered ? 1.24f : 1.18f);

        MapPointType eff = FlatMapLayoutEngine.EffectiveRoomType(node, runState);
        Color rim = FlatMapLayoutEngine.RimColorFor(eff);
        if (node.State == MapPointState.Traveled)
            return (Colors.White, new Color(rim.R, rim.G, rim.B, 0.85f), isHovered ? 1.14f : 1.08f);

        return (Colors.White, rim, isHovered ? 1.22f : 1.16f);
    }

    internal static void ApplyNodeFlatVisualInstant(
        NMapPoint node,
        NMapScreen screen,
        FlatMapScreenState state,
        RunState? runState)
    {
        (Color iconColor, Color rimColor, float rimScale) = ComputeRimStyle(node, screen, state, runState, isHovered: false);

        if (node is NNormalMapPoint normal)
        {
            var icon = NormalIconField.GetValue(normal) as TextureRect;
            var outline = NormalOutlineField.GetValue(normal) as TextureRect;
            if (icon != null && GodotObject.IsInstanceValid(icon))
            {
                // Strip vanilla parchment-blend ShaderMaterial so icons stay crisp & full-color.
                if (icon.Material != null)
                    icon.Material = null;
                icon.SelfModulate = iconColor;
            }
            if (outline != null && GodotObject.IsInstanceValid(outline))
            {
                outline.PivotOffset = outline.Size * 0.5f;
                outline.Modulate = rimColor;
                outline.Scale = Vector2.One * rimScale;
            }
        }
        else if (node is NAncientMapPoint ancient)
        {
            var icon = AncientIconField.GetValue(ancient) as TextureRect;
            var outline = AncientOutlineField.GetValue(ancient) as TextureRect;
            if (icon != null && GodotObject.IsInstanceValid(icon))
                icon.SelfModulate = iconColor;
            if (outline != null && GodotObject.IsInstanceValid(outline))
            {
                outline.PivotOffset = outline.Size * 0.5f;
                outline.Modulate = rimColor;
                outline.Scale = Vector2.One * (node.State == MapPointState.Travelable && screen.IsTravelEnabled ? 1.08f : 1.04f);
            }
        }
    }

    internal static void ApplyNormalPointTween(
        NNormalMapPoint normal,
        NMapScreen screen,
        FlatMapScreenState state,
        Vector2 targetIconScale,
        bool isHovered,
        double duration,
        Tween.EaseType ease,
        Tween.TransitionType trans,
        bool resetContainer = false)
    {
        var runState = PointRunStateField.GetValue(normal) as RunState;
        (Color iconColor, Color rimColor, float rimScale) = ComputeRimStyle(normal, screen, state, runState, isHovered);

        var iconContainer = NormalIconContainerField.GetValue(normal) as Control;
        var icon = NormalIconField.GetValue(normal) as TextureRect;
        var outline = NormalOutlineField.GetValue(normal) as TextureRect;
        var questIcon = NormalQuestIconField.GetValue(normal) as TextureRect;

        if (NormalTweenField.GetValue(normal) is Tween oldTween && GodotObject.IsInstanceValid(oldTween))
            oldTween.Kill();

        Tween tween = normal.CreateTween().SetParallel();
        NormalTweenField.SetValue(normal, tween);

        if (resetContainer && iconContainer != null && GodotObject.IsInstanceValid(iconContainer))
            tween.TweenProperty(iconContainer, "scale", Vector2.One, duration).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);

        if (icon != null && GodotObject.IsInstanceValid(icon))
        {
            if (icon.Material != null) icon.Material = null;
            tween.TweenProperty(icon, "scale", targetIconScale, duration).SetEase(ease).SetTrans(trans);
            tween.TweenProperty(icon, "self_modulate", iconColor, duration).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
        }

        if (questIcon != null && GodotObject.IsInstanceValid(questIcon))
            tween.TweenProperty(questIcon, "scale", targetIconScale, duration).SetEase(ease).SetTrans(trans);

        if (outline != null && GodotObject.IsInstanceValid(outline))
        {
            outline.PivotOffset = outline.Size * 0.5f;
            bool childOfIcon = icon != null && outline.GetParent() == icon;
            Vector2 targetOutlineScale = childOfIcon ? Vector2.One * rimScale : targetIconScale * rimScale;
            tween.TweenProperty(outline, "scale", targetOutlineScale, duration).SetEase(ease).SetTrans(trans);
            tween.TweenProperty(outline, "modulate", rimColor, duration).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
        }
    }

    internal static void ApplyAncientPointTween(
        NAncientMapPoint ancient,
        NMapScreen screen,
        FlatMapScreenState state,
        Vector2 targetIconScale,
        bool isHovered,
        double duration,
        Tween.EaseType ease,
        Tween.TransitionType trans)
    {
        var runState = PointRunStateField.GetValue(ancient) as RunState;
        (Color iconColor, Color rimColor, _) = ComputeRimStyle(ancient, screen, state, runState, isHovered);

        var icon = AncientIconField.GetValue(ancient) as TextureRect;
        var outline = AncientOutlineField.GetValue(ancient) as TextureRect;

        if (AncientTweenField.GetValue(ancient) is Tween oldTween && GodotObject.IsInstanceValid(oldTween))
            oldTween.Kill();

        Tween tween = ancient.CreateTween().SetParallel();
        AncientTweenField.SetValue(ancient, tween);

        if (icon != null && GodotObject.IsInstanceValid(icon))
        {
            tween.TweenProperty(icon, "scale", targetIconScale, duration).SetEase(ease).SetTrans(trans);
            tween.TweenProperty(icon, "self_modulate", iconColor, duration).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
        }
        if (outline != null && GodotObject.IsInstanceValid(outline))
        {
            tween.TweenProperty(outline, "modulate", rimColor, duration).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
        }
    }

    private static NodeSnapshot CaptureSnapshot(NMapPoint node)
    {
        Material? iconMat = null;
        Vector2 outlinePivot = Vector2.Zero;
        Vector2 outlineScale = Vector2.One;
        float extent = Math.Max(1f, Math.Max(node.Size.X, node.Size.Y));

        if (node is NNormalMapPoint normal)
        {
            if (NormalIconField.GetValue(normal) is TextureRect icon && GodotObject.IsInstanceValid(icon))
            {
                iconMat = icon.Material;
                float iconExtent = Math.Max(icon.Size.X, icon.Size.Y);
                if (iconExtent > 8f) extent = iconExtent;
            }
            if (NormalOutlineField.GetValue(normal) is TextureRect outline && GodotObject.IsInstanceValid(outline))
            {
                outlinePivot = outline.PivotOffset;
                outlineScale = outline.Scale;
            }
        }
        else if (node is NAncientMapPoint ancient)
        {
            if (AncientIconField.GetValue(ancient) is TextureRect icon && GodotObject.IsInstanceValid(icon))
            {
                float iconExtent = Math.Max(icon.Size.X, icon.Size.Y);
                if (iconExtent > 8f) extent = iconExtent;
            }
            if (AncientOutlineField.GetValue(ancient) is TextureRect outline && GodotObject.IsInstanceValid(outline))
            {
                outlinePivot = outline.PivotOffset;
                outlineScale = outline.Scale;
            }
        }

        return new NodeSnapshot
        {
            VanillaPosition = node.Position,
            VanillaScale = node.Scale,
            VanillaCenterInPoints = node.Position + node.PivotOffset,
            VanillaIconMaterial = iconMat,
            VanillaOutlinePivot = outlinePivot,
            VanillaOutlineScale = outlineScale,
            NaturalExtent = extent,
        };
    }

    private static void RestoreNodeMaterials(NMapPoint node, NodeSnapshot snap)
    {
        if (node is NNormalMapPoint normal)
        {
            if (NormalIconField.GetValue(normal) is TextureRect icon && GodotObject.IsInstanceValid(icon))
                icon.Material = snap.VanillaIconMaterial;
            if (NormalOutlineField.GetValue(normal) is TextureRect outline && GodotObject.IsInstanceValid(outline))
            {
                outline.PivotOffset = snap.VanillaOutlinePivot;
                outline.Scale = snap.VanillaOutlineScale;
            }
        }
        else if (node is NAncientMapPoint ancient)
        {
            if (AncientOutlineField.GetValue(ancient) is TextureRect outline && GodotObject.IsInstanceValid(outline))
            {
                outline.PivotOffset = snap.VanillaOutlinePivot;
                outline.Scale = snap.VanillaOutlineScale;
            }
        }
    }

    private static void SyncChrome(NMapScreen screen, FlatMapScreenState state)
    {
        if (state.Chrome == null || !GodotObject.IsInstanceValid(state.Chrome))
            return;
        var runState = RunStateField.GetValue(screen) as RunState;
        string actTitle = "";
        Color bgColor = Colors.Black;
        if (runState?.Act != null)
        {
            string name = runState.Act.Title.GetFormattedText();
            actTitle = $"Act {runState.CurrentActIndex + 1} — {name}";
            bgColor = runState.Act.MapBgColor;
        }
        float bottomReserved = 24f;
        Vector2 screenSize = screen.Size.Y > 10f
            ? screen.Size
            : (screen.IsInsideTree() ? screen.GetViewportRect().Size : new Vector2(1920f, 1080f));
        if (BackButtonField.GetValue(screen) is NBackButton backBtn && GodotObject.IsInstanceValid(backBtn) && backBtn.IsEnabled)
        {
            // Back button slides in horizontally at bottom-left; its Position.Y is always its true top edge.
            float clearHeight = screenSize.Y - backBtn.Position.Y + 14f;
            bottomReserved = Mathf.Clamp(clearHeight, 24f, screenSize.Y * 0.35f);
        }
        state.Chrome.SyncState(
            state.IsFlatApplied,
            FlatMapConfig.CompressMap,
            FlatMapConfig.HideUnreachable,
            actTitle,
            bgColor,
            bottomReserved);
    }

    internal static Dictionary<MapCoord, NMapPoint> GetPointDictionary(NMapScreen screen) =>
        (Dictionary<MapCoord, NMapPoint>)PointDictField.GetValue(screen)!;

    private static void TrackScreen(NMapScreen screen)
    {
        LiveScreens.RemoveAll(wr => !wr.TryGetTarget(out NMapScreen? s) || !GodotObject.IsInstanceValid(s));
        if (!LiveScreens.Any(wr => wr.TryGetTarget(out NMapScreen? s) && ReferenceEquals(s, screen)))
            LiveScreens.Add(new WeakReference<NMapScreen>(screen));
    }

    private static void OnModDisabled()
    {
        foreach (WeakReference<NMapScreen> wr in LiveScreens.ToArray())
        {
            if (!wr.TryGetTarget(out NMapScreen? screen) || !GodotObject.IsInstanceValid(screen))
                continue;
            if (States.TryGetValue(screen, out FlatMapScreenState? state))
            {
                if (state.IsFlatApplied)
                    RestoreClassic(screen, state);
                if (state.Chrome != null && GodotObject.IsInstanceValid(state.Chrome))
                    state.Chrome.Visible = false;
                if (state.Backdrop != null && GodotObject.IsInstanceValid(state.Backdrop))
                    state.Backdrop.Visible = false;
                if (state.PathsLayer != null && GodotObject.IsInstanceValid(state.PathsLayer))
                    state.PathsLayer.Visible = false;
            }
        }
        LiveScreens.Clear();
    }

    private static void DumpGraph(NMapScreen screen, Dictionary<MapCoord, NMapPoint> dict)
    {
        var runState = RunStateField.GetValue(screen) as RunState;
        string nodes = string.Join(" ", dict.Keys.OrderBy(c => c.row).ThenBy(c => c.col).Select(c => $"{c.row},{c.col}"));
        var edgeList = new List<string>();
        foreach (MapCoord c in dict.Keys.OrderBy(c => c.row).ThenBy(c => c.col))
        {
            if (dict[c].Point == null) continue;
            foreach (MapPoint ch in dict[c].Point!.Children.OrderBy(p => p.coord.row).ThenBy(p => p.coord.col))
                edgeList.Add($"{c.row},{c.col}->{ch.coord.row},{ch.coord.col}");
        }
        Log.Info($"[FlatMap] MAPDUMP act={runState?.CurrentActIndex} nodes=\"{nodes}\" edges=\"{string.Join(" ", edgeList)}\"");
    }
}

// --- Harmony patches on NMapScreen & its live children -----------------------------------

[HarmonyPatch(typeof(NMapScreen), "_Ready")]
internal static class NMapScreen_Ready_Patch
{
    private static void Postfix(NMapScreen __instance)
    {
        if (!ModRuntime.Enabled) return;
        try { FlatMapController.AttachOverlays(__instance); }
        catch (Exception ex) { ModRuntime.Disable(nameof(NMapScreen_Ready_Patch), ex); }
    }
}

[HarmonyPatch(typeof(NMapScreen), "SetMap")]
internal static class NMapScreen_SetMap_Patch
{
    private static void Prefix(NMapScreen __instance)
    {
        if (!ModRuntime.Enabled) return;
        try { FlatMapController.OnBeforeSetMap(__instance); }
        catch (Exception ex) { ModRuntime.Disable(nameof(NMapScreen_SetMap_Patch), ex); }
    }

    private static void Postfix(NMapScreen __instance)
    {
        if (!ModRuntime.Enabled) return;
        try { FlatMapController.OnSetMap(__instance); }
        catch (Exception ex) { ModRuntime.Disable(nameof(NMapScreen_SetMap_Patch), ex); }
    }
}

[HarmonyPatch(typeof(NMapScreen), "Open")]
internal static class NMapScreen_Open_Patch
{
    private static void Prefix(NMapScreen __instance, out bool __state) => __state = __instance.IsOpen;

    private static void Postfix(NMapScreen __instance, bool __state)
    {
        if (!ModRuntime.Enabled) return;
        try { FlatMapController.OnOpen(__instance, __state); }
        catch (Exception ex) { ModRuntime.Disable(nameof(NMapScreen_Open_Patch), ex); }
    }
}

[HarmonyPatch(typeof(NMapScreen), "Close")]
internal static class NMapScreen_Close_Patch
{
    private static void Prefix(NMapScreen __instance, bool animateOut)
    {
        if (!ModRuntime.Enabled || !__instance.IsOpen) return;
        try { FlatMapController.OnClose(__instance, animateOut); }
        catch (Exception ex) { ModRuntime.Disable(nameof(NMapScreen_Close_Patch), ex); }
    }
}

[HarmonyPatch(typeof(NMapScreen), "PlayStartOfActAnimation")]
internal static class NMapScreen_PlayStartOfActAnimation_Patch
{
    private static bool Prefix(NMapScreen __instance)
    {
        if (!ModRuntime.Enabled) return true;
        try
        {
            if (!FlatMapConfig.PreferFlatMap && !FlatMapController.IsFlatActive(__instance))
                return true;

            if ((bool)FlatMapController.HasPlayedAnimField.GetValue(__instance)!)
                return false;

            FlatMapController.HasPlayedAnimField.SetValue(__instance, true);
            if (FlatMapController.RunStateField.GetValue(__instance) is RunState runState && runState.Act != null)
            {
                NActBanner? banner = NActBanner.Create(runState.Act, runState.CurrentActIndex);
                if (banner != null)
                    __instance.AddChildSafely(banner);
            }
            FlatMapController.PinContainerAtOrigin(__instance);
            FlatMapController.InitMapPromptMethod.Invoke(__instance, null);
            return false;
        }
        catch (Exception ex)
        {
            ModRuntime.Disable(nameof(NMapScreen_PlayStartOfActAnimation_Patch), ex);
            return true;
        }
    }
}

[HarmonyPatch(typeof(NMapScreen), "CanScroll")]
internal static class NMapScreen_CanScroll_Patch
{
    private static void Postfix(NMapScreen __instance, ref bool __result)
    {
        if (!ModRuntime.Enabled || !__result) return;
        try
        {
            if (FlatMapController.IsFlatActive(__instance))
                __result = false;
        }
        catch (Exception ex) { ModRuntime.Disable(nameof(NMapScreen_CanScroll_Patch), ex); }
    }
}

[HarmonyPatch(typeof(NMapScreen), "UpdateScrollPosition")]
internal static class NMapScreen_UpdateScrollPosition_Patch
{
    private static bool Prefix(NMapScreen __instance)
    {
        if (!ModRuntime.Enabled) return true;
        try
        {
            if (!FlatMapController.IsFlatActive(__instance) || !__instance.IsOpen)
                return true;

            var mapContainer = (Control)FlatMapController.MapContainerField.GetValue(__instance)!;
            if (mapContainer.Position != Vector2.Zero)
                mapContainer.Position = Vector2.Zero;
            NGame.Instance?.RemoteCursorContainer?.ForceUpdateAllCursors();
            return false;
        }
        catch (Exception ex)
        {
            ModRuntime.Disable(nameof(NMapScreen_UpdateScrollPosition_Patch), ex);
            return true;
        }
    }
}

[HarmonyPatch(typeof(NMapScreen), "_Process")]
internal static class NMapScreen_Process_Patch
{
    private static void Postfix(NMapScreen __instance)
    {
        if (!ModRuntime.Enabled) return;
        try
        {
            FlatMapScreenState state = FlatMapController.GetState(__instance);
            if (state.RestoreFlatAfterShare
                && FlatMapController.ShareButtonField.GetValue(__instance) is NMapShareButton share
                && GodotObject.IsInstanceValid(share)
                && !share.IsTakingScreenshot())
            {
                state.RestoreFlatAfterShare = false;
                if (FlatMapConfig.PreferFlatMap)
                    FlatMapController.ApplyFlat(__instance, state, animateBackdrop: false);
            }
        }
        catch (Exception ex) { ModRuntime.Disable(nameof(NMapScreen_Process_Patch), ex); }
    }
}

[HarmonyPatch(typeof(NMapScreen), "_Input")]
internal static class NMapScreen_Input_Patch
{
    private static bool Prefix(NMapScreen __instance, InputEvent inputEvent)
    {
        if (!ModRuntime.Enabled) return true;
        try
        {
            if (inputEvent is not InputEventKey key || !key.Pressed || key.Echo)
                return true;
            if (key.Keycode != FlatMapMod.ToggleFlatMapKey && key.PhysicalKeycode != FlatMapMod.ToggleFlatMapKey)
                return true;
            if (key.CtrlPressed || key.AltPressed || key.ShiftPressed || key.MetaPressed)
                return true;
            if (!__instance.IsOpen || !__instance.IsVisibleInTree())
                return true;
            if (!ActiveScreenContext.Instance.IsCurrent(__instance))
                return true;
            if (!NGame.IsGameFocusedWindow() || NGame.Instance?.Transition?.InTransition == true)
                return true;
            if (__instance.GetViewport()?.GuiGetFocusOwner() is LineEdit or TextEdit)
                return true;
            // Respect custom/FKB bindings that map F to drawing tools.
            if (inputEvent.IsActionPressed(MegaInput.viewExhaustPileAndTabRight))
                return true;

            FlatMapController.SetFlatMode(__instance, !FlatMapConfig.PreferFlatMap);
            __instance.GetViewport()?.SetInputAsHandled();
            return false;
        }
        catch (Exception ex)
        {
            ModRuntime.Disable(nameof(NMapScreen_Input_Patch), ex);
            return true;
        }
    }
}

[HarmonyPatch(typeof(NMapScreen), "RecalculateTravelability")]
internal static class NMapScreen_RecalculateTravelability_Patch
{
    private static void Postfix(NMapScreen __instance)
    {
        if (!ModRuntime.Enabled) return;
        try
        {
            FlatMapScreenState state = FlatMapController.GetState(__instance);
            if (state.IsFlatApplied)
            {
                state.TravelingEdge = null;
                FlatMapController.RefreshVisuals(__instance, state);
            }
        }
        catch (Exception ex) { ModRuntime.Disable(nameof(NMapScreen_RecalculateTravelability_Patch), ex); }
    }
}

[HarmonyPatch(typeof(NMapScreen), "RefreshAllPointVisuals")]
internal static class NMapScreen_RefreshAllPointVisuals_Patch
{
    private static void Postfix(NMapScreen __instance)
    {
        if (!ModRuntime.Enabled) return;
        try
        {
            FlatMapScreenState state = FlatMapController.GetState(__instance);
            if (state.IsFlatApplied)
                FlatMapController.RefreshVisuals(__instance, state);
        }
        catch (Exception ex) { ModRuntime.Disable(nameof(NMapScreen_RefreshAllPointVisuals_Patch), ex); }
    }
}

[HarmonyPatch(typeof(NMapScreen), "TravelToMapCoord")]
internal static class NMapScreen_TravelToMapCoord_Patch
{
    private static void Prefix(NMapScreen __instance, MapCoord coord)
    {
        if (!ModRuntime.Enabled) return;
        try
        {
            FlatMapScreenState state = FlatMapController.GetState(__instance);
            if (!state.IsFlatApplied) return;
            if (FlatMapController.RunStateField.GetValue(__instance) is RunState runState
                && runState.CurrentMapCoord is MapCoord prev)
            {
                state.TravelingEdge = (prev, coord);
                FlatMapController.RefreshVisuals(__instance, state);
            }
        }
        catch (Exception ex) { ModRuntime.Disable(nameof(NMapScreen_TravelToMapCoord_Patch), ex); }
    }
}

[HarmonyPatch(typeof(NMapScreen), "GetNetPositionFromScreenPosition")]
internal static class NMapScreen_GetNetPosition_Patch
{
    private static bool Prefix(NMapScreen __instance, Vector2 screenPosition, ref Vector2 __result)
    {
        if (!ModRuntime.Enabled) return true;
        try
        {
            if (!FlatMapController.IsFlatActive(__instance))
                return true;
            NMapDrawings drawings = __instance.Drawings;
            if (!GodotObject.IsInstanceValid(drawings))
                return true;

            Vector2 local = drawings.GetGlobalTransformWithCanvas().AffineInverse() * screenPosition;
            Vector2 half = drawings.Size * 0.5f;
            __result = (local - half) / new Vector2(960f, Math.Max(1f, half.Y));
            return false;
        }
        catch (Exception ex)
        {
            ModRuntime.Disable(nameof(NMapScreen_GetNetPosition_Patch), ex);
            return true;
        }
    }
}

[HarmonyPatch(typeof(NMapScreen), "GetScreenPositionFromNetPosition")]
internal static class NMapScreen_GetScreenPosition_Patch
{
    private static bool Prefix(NMapScreen __instance, Vector2 netPosition, ref Vector2 __result)
    {
        if (!ModRuntime.Enabled) return true;
        try
        {
            if (!FlatMapController.IsFlatActive(__instance))
                return true;
            NMapDrawings drawings = __instance.Drawings;
            if (!GodotObject.IsInstanceValid(drawings))
                return true;

            Vector2 half = drawings.Size * 0.5f;
            Vector2 local = netPosition * new Vector2(960f, half.Y) + half;
            __result = drawings.GetGlobalTransformWithCanvas() * local;
            return false;
        }
        catch (Exception ex)
        {
            ModRuntime.Disable(nameof(NMapScreen_GetScreenPosition_Patch), ex);
            return true;
        }
    }
}

[HarmonyPatch(typeof(NMapBg), "OnViewportChange")]
internal static class NMapBg_OnViewportChange_Patch
{
    private static void Postfix(NMapBg __instance)
    {
        if (!ModRuntime.Enabled) return;
        try
        {
            if (FlatMapController.TryFindScreen(__instance, out NMapScreen screen, out FlatMapScreenState state)
                && state.IsFlatApplied)
            {
                FlatMapController.ApplyFlat(screen, state, animateBackdrop: false);
            }
        }
        catch (Exception ex) { ModRuntime.Disable(nameof(NMapBg_OnViewportChange_Patch), ex); }
    }
}

[HarmonyPatch(typeof(NMapMarker), "SetMapPoint")]
internal static class NMapMarker_SetMapPoint_Patch
{
    private static void Postfix(NMapMarker __instance, Control node)
    {
        if (!ModRuntime.Enabled) return;
        try
        {
            if (FlatMapController.TryFindScreen(__instance, out NMapScreen screen, out FlatMapScreenState state)
                && state.IsFlatApplied)
            {
                FlatMapController.PositionMarkerLeft(screen, state, __instance, node);
            }
        }
        catch (Exception ex) { ModRuntime.Disable(nameof(NMapMarker_SetMapPoint_Patch), ex); }
    }
}

[HarmonyPatch(typeof(NMapShareButton), "OnRelease")]
internal static class NMapShareButton_OnRelease_Patch
{
    private static void Prefix(NMapShareButton __instance)
    {
        if (!ModRuntime.Enabled) return;
        try
        {
            if (!__instance.IsTakingScreenshot()
                && FlatMapController.TryFindScreen(__instance, out NMapScreen screen, out FlatMapScreenState state)
                && state.IsFlatApplied)
            {
                state.RestoreFlatAfterShare = true;
                FlatMapController.RestoreClassic(screen, state);
            }
        }
        catch (Exception ex) { ModRuntime.Disable(nameof(NMapShareButton_OnRelease_Patch), ex); }
    }
}

// --- Neon Rims & Opaque Bodies on NNormalMapPoint & NAncientMapPoint ---------------------

[HarmonyPatch(typeof(NNormalMapPoint), "RefreshColorInstantly")]
internal static class NNormalMapPoint_RefreshColorInstantly_Patch
{
    private static bool Prefix(NNormalMapPoint __instance)
    {
        if (!ModRuntime.Enabled) return true;
        try
        {
            if (!FlatMapController.TryFindScreen(__instance, out NMapScreen screen, out FlatMapScreenState state)
                || !state.IsFlatApplied)
                return true;

            var runState = FlatMapController.PointRunStateField.GetValue(__instance) as RunState;
            FlatMapController.ApplyNodeFlatVisualInstant(__instance, screen, state, runState);
            return false;
        }
        catch (Exception ex)
        {
            ModRuntime.Disable(nameof(NNormalMapPoint_RefreshColorInstantly_Patch), ex);
            return true;
        }
    }
}

[HarmonyPatch(typeof(NNormalMapPoint), "AnimHover")]
internal static class NNormalMapPoint_AnimHover_Patch
{
    private static void Postfix(NNormalMapPoint __instance)
    {
        if (!ModRuntime.Enabled) return;
        try
        {
            if (FlatMapController.TryFindScreen(__instance, out NMapScreen screen, out FlatMapScreenState state)
                && state.IsFlatApplied)
            {
                Vector2 hoverScale = (Vector2)FlatMapController.PointHoverScaleGetter.Invoke(__instance, null)!;
                FlatMapController.ApplyNormalPointTween(
                    __instance, screen, state, hoverScale, isHovered: true,
                    duration: 0.05, Tween.EaseType.InOut, Tween.TransitionType.Linear);
            }
        }
        catch (Exception ex) { ModRuntime.Disable(nameof(NNormalMapPoint_AnimHover_Patch), ex); }
    }
}

[HarmonyPatch(typeof(NNormalMapPoint), "AnimUnhover")]
internal static class NNormalMapPoint_AnimUnhover_Patch
{
    private static void Postfix(NNormalMapPoint __instance)
    {
        if (!ModRuntime.Enabled) return;
        try
        {
            if (FlatMapController.TryFindScreen(__instance, out NMapScreen screen, out FlatMapScreenState state)
                && state.IsFlatApplied)
            {
                FlatMapController.ApplyNormalPointTween(
                    __instance, screen, state, Vector2.One, isHovered: false,
                    duration: 0.5, Tween.EaseType.Out, Tween.TransitionType.Cubic);
            }
        }
        catch (Exception ex) { ModRuntime.Disable(nameof(NNormalMapPoint_AnimUnhover_Patch), ex); }
    }
}

[HarmonyPatch(typeof(NNormalMapPoint), "AnimPressDown")]
internal static class NNormalMapPoint_AnimPressDown_Patch
{
    private static void Postfix(NNormalMapPoint __instance)
    {
        if (!ModRuntime.Enabled) return;
        try
        {
            if (FlatMapController.TryFindScreen(__instance, out NMapScreen screen, out FlatMapScreenState state)
                && state.IsFlatApplied)
            {
                Vector2 downScale = (Vector2)FlatMapController.PointDownScaleGetter.Invoke(__instance, null)!;
                FlatMapController.ApplyNormalPointTween(
                    __instance, screen, state, downScale, isHovered: true,
                    duration: 0.3, Tween.EaseType.Out, Tween.TransitionType.Cubic);
            }
        }
        catch (Exception ex) { ModRuntime.Disable(nameof(NNormalMapPoint_AnimPressDown_Patch), ex); }
    }
}

[HarmonyPatch(typeof(NNormalMapPoint), "OnSelected")]
internal static class NNormalMapPoint_OnSelected_Patch
{
    private static void Postfix(NNormalMapPoint __instance)
    {
        if (!ModRuntime.Enabled) return;
        try
        {
            if (FlatMapController.TryFindScreen(__instance, out NMapScreen screen, out FlatMapScreenState state)
                && state.IsFlatApplied)
            {
                FlatMapController.ApplyNormalPointTween(
                    __instance, screen, state, Vector2.One, isHovered: false,
                    duration: 0.3, Tween.EaseType.Out, Tween.TransitionType.Back, resetContainer: true);
            }
        }
        catch (Exception ex) { ModRuntime.Disable(nameof(NNormalMapPoint_OnSelected_Patch), ex); }
    }
}

[HarmonyPatch(typeof(NAncientMapPoint), "_Process")]
internal static class NAncientMapPoint_Process_Patch
{
    private static bool Prefix(NAncientMapPoint __instance, double delta)
    {
        if (!ModRuntime.Enabled) return true;
        try
        {
            if (!FlatMapController.TryFindScreen(__instance, out _, out FlatMapScreenState state)
                || !state.IsFlatApplied || state.Layout == null || __instance.Point == null
                || !state.Layout.Placements.TryGetValue(__instance.Point.coord, out NodePlacement? p))
                return true;

            if (__instance.IsEnabled)
            {
                bool isFocused = (bool)FlatMapController.ClickableIsFocusedGetter.Invoke(__instance, null)!;
                bool inputAllowed = (bool)FlatMapController.PointIsInputAllowedMethod.Invoke(__instance, null)!;
                if (!isFocused && inputAllowed)
                {
                    float elapsed = (float)FlatMapController.AncientElapsedField.GetValue(__instance)! + (float)delta * 4f;
                    FlatMapController.AncientElapsedField.SetValue(__instance, elapsed);
                    __instance.Scale = p.FlatScale * (Mathf.Sin(elapsed) * 0.05f + 1f);
                }
                else
                {
                    __instance.Scale = __instance.Scale.Lerp(p.FlatScale, 0.5f);
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            ModRuntime.Disable(nameof(NAncientMapPoint_Process_Patch), ex);
            return true;
        }
    }
}

[HarmonyPatch(typeof(NAncientMapPoint), "RefreshColorInstantly")]
internal static class NAncientMapPoint_RefreshColorInstantly_Patch
{
    private static bool Prefix(NAncientMapPoint __instance)
    {
        if (!ModRuntime.Enabled) return true;
        try
        {
            if (!FlatMapController.TryFindScreen(__instance, out NMapScreen screen, out FlatMapScreenState state)
                || !state.IsFlatApplied)
                return true;

            var runState = FlatMapController.PointRunStateField.GetValue(__instance) as RunState;
            FlatMapController.ApplyNodeFlatVisualInstant(__instance, screen, state, runState);
            return false;
        }
        catch (Exception ex)
        {
            ModRuntime.Disable(nameof(NAncientMapPoint_RefreshColorInstantly_Patch), ex);
            return true;
        }
    }
}

[HarmonyPatch(typeof(NAncientMapPoint), "AnimHover")]
internal static class NAncientMapPoint_AnimHover_Patch
{
    private static void Postfix(NAncientMapPoint __instance)
    {
        if (!ModRuntime.Enabled) return;
        try
        {
            if (FlatMapController.TryFindScreen(__instance, out NMapScreen screen, out FlatMapScreenState state)
                && state.IsFlatApplied)
            {
                Vector2 hoverScale = (Vector2)FlatMapController.PointHoverScaleGetter.Invoke(__instance, null)!;
                FlatMapController.ApplyAncientPointTween(
                    __instance, screen, state, hoverScale, isHovered: true,
                    duration: 0.05, Tween.EaseType.InOut, Tween.TransitionType.Linear);
            }
        }
        catch (Exception ex) { ModRuntime.Disable(nameof(NAncientMapPoint_AnimHover_Patch), ex); }
    }
}

[HarmonyPatch(typeof(NAncientMapPoint), "AnimUnhover")]
internal static class NAncientMapPoint_AnimUnhover_Patch
{
    private static void Postfix(NAncientMapPoint __instance)
    {
        if (!ModRuntime.Enabled) return;
        try
        {
            if (FlatMapController.TryFindScreen(__instance, out NMapScreen screen, out FlatMapScreenState state)
                && state.IsFlatApplied)
            {
                FlatMapController.ApplyAncientPointTween(
                    __instance, screen, state, Vector2.One, isHovered: false,
                    duration: 0.5, Tween.EaseType.Out, Tween.TransitionType.Cubic);
            }
        }
        catch (Exception ex) { ModRuntime.Disable(nameof(NAncientMapPoint_AnimUnhover_Patch), ex); }
    }
}

[HarmonyPatch(typeof(NAncientMapPoint), "AnimPressDown")]
internal static class NAncientMapPoint_AnimPressDown_Patch
{
    private static void Postfix(NAncientMapPoint __instance)
    {
        if (!ModRuntime.Enabled) return;
        try
        {
            if (FlatMapController.TryFindScreen(__instance, out NMapScreen screen, out FlatMapScreenState state)
                && state.IsFlatApplied)
            {
                Vector2 downScale = (Vector2)FlatMapController.PointDownScaleGetter.Invoke(__instance, null)!;
                FlatMapController.ApplyAncientPointTween(
                    __instance, screen, state, downScale, isHovered: true,
                    duration: 0.3, Tween.EaseType.Out, Tween.TransitionType.Cubic);
            }
        }
        catch (Exception ex) { ModRuntime.Disable(nameof(NAncientMapPoint_AnimPressDown_Patch), ex); }
    }
}

[HarmonyPatch(typeof(NAncientMapPoint), "OnSelected")]
internal static class NAncientMapPoint_OnSelected_Patch
{
    private static void Postfix(NAncientMapPoint __instance)
    {
        if (!ModRuntime.Enabled) return;
        try
        {
            if (FlatMapController.TryFindScreen(__instance, out NMapScreen screen, out FlatMapScreenState state)
                && state.IsFlatApplied)
            {
                FlatMapController.ApplyAncientPointTween(
                    __instance, screen, state, Vector2.One, isHovered: false,
                    duration: 0.3, Tween.EaseType.Out, Tween.TransitionType.Back);
            }
        }
        catch (Exception ex) { ModRuntime.Disable(nameof(NAncientMapPoint_OnSelected_Patch), ex); }
    }
}
