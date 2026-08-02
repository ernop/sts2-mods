using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FlatMap.Layout;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;

namespace FlatMap;

// FlatMap — the flat whole-act map view for Slay the Spire 2.
//
// (The original "make deck-view cards smaller" feature was extracted into its own standalone
// DeckView mod — see deckview/ — so this mod carries ONLY the map view. The two mods are
// fully independent: separate DLLs, manifests, Harmony ids, configs, and hook preflights.)
[ModInitializer(nameof(Init))]
public static class FlatMapMod
{
    // F instantly flips between the two display styles of THE ONE map — flat <-> classic — in
    // place. NOT a layer/stack: whichever is showing, F swaps to the other; M / the map button
    // still dismiss the whole map from either. Only does anything while a map is displayed.
    public const Key ToggleMiniMapKey = Key.F;


    // M is the ONE global map shortcut: from anywhere, it toggles the map's visibility. When it
    // opens the map, the map shows in whatever state the two checkboxes ("Flat map" / "Compress")
    // are configured to. M = "map".
    public const Key MapKey = Key.M;

    // FlatMap was built and tested against this game version. On another build, every private
    // hook is preflighted before Harmony changes anything. Missing hooks disable FlatMap and
    // leave the game's UI untouched instead of crashing or leaving a partially patched mod.
    public const string TestedGameVersion = "v0.110.1";

    public static void Init()
    {
        if (!ModRuntime.TryEnable(typeof(FlatMapMod).Assembly))
            return;

        string? gameVersion = ReleaseInfoManager.Instance.ReleaseInfo?.Version;
        string versionNote = gameVersion == TestedGameVersion
            ? ""
            : $" — NOTE: game '{gameVersion ?? "unknown"}' is not the tested {TestedGameVersion}; re-verify";
        Log.Info($"[FlatMap] loaded — flat map view{versionNote}");
    }
}

// --- Minimap: a compact, glance-able node-graph rendering of the whole act ---------------
//
// This REPLACES the old "zoom the real map out" overview, which couldn't work: NMapScreen's
// _Process rewrites _mapContainer.Position every frame (lerping toward _targetDragPos, which
// is itself clamped to [-600, 1800]) and there is no zoom field — so any transform we set on
// the real map is fought or clamped away within a frame.
//
// Instead the minimap is a real top-level PAGE: MiniMapScreen implements ICapstoneScreen and is
// opened through NCapstoneContainer, exactly like the deck-view screen. The game then supplies the
// standard top bar, the dimmed backstop, combat pause, "current screen" focus/controller routing,
// and native ESC/back — we don't hand-roll layering or input. We render the whole act as a node
// graph (one dot per point, colored by room type with the game's own icon, edges drawn, laid out
// LEFT->RIGHT and vertically flattened by MapLayout, current position highlighted) plus an info
// panel. It only READS the live map graph; it never touches the real map's data or nodes.
internal static class MiniMapController
{
    // _mapPointDictionary is Dictionary<MapCoord, NMapPoint> and already contains EVERY on-screen
    // point (normal + boss + second boss + start), so it's the one handle we need for the graph.
    private static readonly FieldInfo PointDictField = Reflect.Field(typeof(NMapScreen), "_mapPointDictionary");
    private static readonly FieldInfo RunStateField = Reflect.Field(typeof(NMapScreen), "_runState");
    // Whether you can travel RIGHT NOW (the current room is finished). False while you still have to
    // complete the room you're in — in that case there are no legal moves yet, so we neither dim the
    // current node "done" nor highlight the downstream options.
    private static readonly MethodInfo TravelEnabledGetter = Reflect.PropertyGetter(typeof(NMapScreen), "IsTravelEnabled");
    // Recomputes each point's travel State from the run state. The game runs this inside Open(); we
    // call it ourselves so the flat page has fresh travelability WITHOUT ever opening the classic map.
    private static readonly MethodInfo RecalcTravelMethod = Reflect.Method(typeof(NMapScreen), "RecalculateTravelability", Type.EmptyTypes);
    // The game's "you are here" arrow — NMapMarker (a TextureRect) whose .Texture is the current
    // character's MapMarker art. We read that live texture so our page marks the current node the
    // exact way the real map does. (Null in multiplayer, where the game suppresses the marker.)
    private static readonly FieldInfo MarkerField = Reflect.Field(typeof(NMapScreen), "_marker");
    // The game's own "Legend" panel (parchment, header, localized icon+label rows). We BORROW the
    // real control onto the flat page while it's open — pixel-identical to vanilla — and return it
    // to the classic map screen on close. Vanilla anchors it at x = Size.X * 0.8.
    private static readonly FieldInfo MapLegendField = Reflect.Field(typeof(NMapScreen), "_mapLegend");
    // CurrentMapCoord (MapCoord?) tells us where the player is now (null before the first move).
    private static readonly MethodInfo CurrentCoordGetter =
        Reflect.PropertyGetter(RunStateField.FieldType, "CurrentMapCoord");

    // For the info panel: act index/floor + the act's display name (Act.Title is a LocString;
    // GetFormattedText() localizes it, e.g. "The Underdocks"). Resolved via ReturnType chaining
    // so we don't hardcode the ActModel/LocString namespaces.
    private static readonly MethodInfo ActIndexGetter = Reflect.PropertyGetter(RunStateField.FieldType, "CurrentActIndex");
    private static readonly MethodInfo ActFloorGetter = Reflect.PropertyGetter(RunStateField.FieldType, "ActFloor");
    private static readonly MethodInfo ActGetter = Reflect.PropertyGetter(RunStateField.FieldType, "Act");
    private static readonly MethodInfo ActTitleGetter = Reflect.PropertyGetter(ActGetter.ReturnType, "Title");
    private static readonly MethodInfo LocStringFormat = Reflect.Method(ActTitleGetter.ReturnType, "GetFormattedText", Type.EmptyTypes);

    // MapPointHistory[actIndex][row].Rooms.First().RoomType records what a visited "?" node turned
    // out to be. We resolve that so a revealed Unknown reads (colour + tally) as its real room type,
    // mirroring the game revealing the "?" icon on entry. Nested Rooms/RoomType are resolved on the
    // runtime types (fail-loud) the first time we touch one.
    private static readonly MethodInfo MapHistoryGetter = Reflect.PropertyGetter(RunStateField.FieldType, "MapPointHistory");
    private static MethodInfo? _roomsGet, _roomTypeGet;

    private static MapPointType ResolveUnknown(object runState, int actIndex, int row)
    {
        if (MapHistoryGetter.Invoke(runState, null) is not System.Collections.IList hist
            || actIndex < 0 || actIndex >= hist.Count
            || hist[actIndex] is not System.Collections.IList rows
            || row < 0 || row >= rows.Count || rows[row] is not object entry)
            return MapPointType.Unknown; // not yet recorded — leave it as "?"
        _roomsGet ??= Reflect.PropertyGetter(entry.GetType(), "Rooms");
        if (_roomsGet.Invoke(entry, null) is not System.Collections.IList rooms || rooms.Count == 0 || rooms[0] is not object room)
            return MapPointType.Unknown;
        _roomTypeGet ??= Reflect.PropertyGetter(room.GetType(), "RoomType");
        return _roomTypeGet.Invoke(room, null)?.ToString() switch
        {
            "Monster" => MapPointType.Monster,
            "Elite" => MapPointType.Elite,
            "Shop" => MapPointType.Shop,
            "Treasure" => MapPointType.Treasure,
            "RestSite" => MapPointType.RestSite,
            _ => MapPointType.Unknown,
        };
    }

    // Each on-screen point renders its room icon into a TextureRect field named "_icon", with the
    // detail stroke in a sibling "_outline" TextureRect (the game tints the fill per travel state
    // and carves the linework in Act.MapBgColor on top). We grab BOTH live textures so the minimap
    // draws nodes in the game's exact visual language (including "?" nodes that have resolved).
    private static readonly FieldInfo NormalIconField = Reflect.Field(typeof(NNormalMapPoint), "_icon");
    private static readonly FieldInfo NormalOutlineField = Reflect.Field(typeof(NNormalMapPoint), "_outline");
    private static readonly FieldInfo AncientIconField = Reflect.Field(typeof(NAncientMapPoint), "_icon");
    private static readonly FieldInfo AncientOutlineField = Reflect.Field(typeof(NAncientMapPoint), "_outline");
    // Boss nodes have no _icon; the (non-Spine) act art lives in a "%PlaceholderImage" TextureRect.
    private static readonly FieldInfo BossImageField = Reflect.Field(typeof(NBossMapPoint), "_placeholderImage");

    // The act's own map palette + the boss node art. The game loads the boss art from
    // EncounterModel.BossNodePath (+".png"/"_outline.png") even when the live node prefers Spine —
    // so we can always show the REAL boss icon. Second-boss floors use SecondBossEncounter.
    private static readonly MethodInfo MapBgColorGetter = Reflect.PropertyGetter(ActGetter.ReturnType, "MapBgColor");
    private static readonly MethodInfo MapTraveledColorGetter = Reflect.PropertyGetter(ActGetter.ReturnType, "MapTraveledColor");
    private static readonly MethodInfo MapUntraveledColorGetter = Reflect.PropertyGetter(ActGetter.ReturnType, "MapUntraveledColor");
    private static readonly MethodInfo BossEncounterGetter = Reflect.PropertyGetter(ActGetter.ReturnType, "BossEncounter");
    private static readonly MethodInfo SecondBossEncounterGetter = Reflect.PropertyGetter(ActGetter.ReturnType, "SecondBossEncounter");
    private static readonly MethodInfo BossNodePathGetter = Reflect.PropertyGetter(BossEncounterGetter.ReturnType, "BossNodePath");
    private static readonly MethodInfo EncounterIdGetter = Reflect.PropertyGetter(BossEncounterGetter.ReturnType, "Id");
    private static readonly MethodInfo RunMapGetter = Reflect.PropertyGetter(RunStateField.FieldType, "Map");
    private static readonly MethodInfo SecondBossPointGetter = Reflect.PropertyGetter(RunMapGetter.ReturnType, "SecondBossMapPoint");

    private static Texture2D? TextureOf(TextureRect? rect) =>
        rect != null && GodotObject.IsInstanceValid(rect) ? rect.Texture : null;

    private static Texture2D? IconOf(NMapPoint np) => np switch
    {
        NNormalMapPoint => TextureOf(NormalIconField.GetValue(np) as TextureRect),
        NAncientMapPoint => TextureOf(AncientIconField.GetValue(np) as TextureRect),
        NBossMapPoint => TextureOf(BossImageField.GetValue(np) as TextureRect), // fallback if asset load fails
        _ => null, // -> letter fallback
    };

    private static Texture2D? OutlineOf(NMapPoint np) => np switch
    {
        NNormalMapPoint => TextureOf(NormalOutlineField.GetValue(np) as TextureRect),
        NAncientMapPoint => TextureOf(AncientOutlineField.GetValue(np) as TextureRect),
        _ => null,
    };

    // The proper boss node art (fill + outline), loaded the same way the game does. Defensive: a
    // missing asset (unexpected act data) falls back to the placeholder/glyph instead of disabling
    // the mod — this is game-data availability, not a code bug.
    private static (Texture2D? icon, Texture2D? outline) BossArt(object runState, object act, MapPoint mp)
    {
        try
        {
            object? map = RunMapGetter.Invoke(runState, null);
            object? secondBoss = map == null ? null : SecondBossPointGetter.Invoke(map, null);
            object? encounter = ReferenceEquals(mp, secondBoss)
                ? SecondBossEncounterGetter.Invoke(act, null)
                : BossEncounterGetter.Invoke(act, null);
            if (encounter == null)
                return (null, null);

            // Every boss has a unique ICON in ui/run_history/{bossid}.png — the game's own
            // "this boss" icon art (ImageHelper: "bosses and ancients have unique icons"). This is
            // the right static art for a map node; the map's animated Spine boss has no static png.
            if (EncounterIdGetter.Invoke(encounter, null) is ModelId id)
            {
                Texture2D? icon = LoadTexture(ImageHelper.GetRoomIconPath(MapPointType.Boss, RoomType.Boss, id));
                Texture2D? outline = LoadTexture(ImageHelper.GetRoomIconOutlinePath(MapPointType.Boss, RoomType.Boss, id));
                if (icon != null)
                    return (icon, outline);
            }

            // Non-Spine bosses also ship placeholder node art next to the skeleton path.
            if (BossNodePathGetter.Invoke(encounter, null) is string path && !string.IsNullOrEmpty(path))
                return (LoadTexture(path + ".png"), LoadTexture(path + "_outline.png"));
            return (null, null);
        }
        catch (Exception ex)
        {
            Dbg.Once("bossart", $"boss node art unavailable ({ex.Message}); using placeholder/glyph");
            return (null, null);
        }
    }

    internal static Texture2D? LoadTexture(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        string res = path.StartsWith("res://") ? path : "res://" + path.TrimStart('/');
        if (ResourceLoader.Exists(res) && ResourceLoader.Load(res, null, ResourceLoader.CacheMode.Reuse) is Texture2D tex)
            return tex;
        Dbg.Once($"tex:{path}", $"texture '{res}' not found");
        return null;
    }

    private const string MapToggleMeta = "deckview_mapstyle_toggle";
    private static MiniMapScreen? _screen;      // reused capstone instance, reconfigured per open
    private static ToggleSwitch? _mapToggle;    // the "Flat map" toggle bolted onto the classic map
    private static Control? _classicDefaultFocus;
    private static NodePath _classicPreviousLeft = new("");
    private static bool _hasClassicPreviousLeft;

    static MiniMapController() => ModRuntime.Disabled += OnModDisabled;

    private static void OnModDisabled()
    {
        if (_mapToggle != null && GodotObject.IsInstanceValid(_mapToggle))
            _mapToggle.Visible = false;
        if (_classicDefaultFocus != null && GodotObject.IsInstanceValid(_classicDefaultFocus)
            && _hasClassicPreviousLeft)
            _classicDefaultFocus.FocusNeighborLeft = _classicPreviousLeft;
        if (FlatOpen())
            NCapstoneContainer.Instance?.Close();
        ReturnLegend();
    }

    // Directional navigation in use? (Controller or keyboard-only mode; both need a default focus.)
    // The Instance null-check is legitimate state: no controller manager yet => pointer mode.
    internal static bool UsingController() => NControllerManager.Instance?.IsUsingDirectionalNavigation ?? false;

    // Is our flat page the currently-shown capstone?
    private static bool FlatOpen()
    {
        NCapstoneContainer? cc = NCapstoneContainer.Instance;
        return cc != null && _screen != null && ReferenceEquals(cc.CurrentCapstoneScreen, _screen);
    }

    // Runs while the CLASSIC map is processing (classic mode only — in flat mode NMapScreen is never
    // opened, so this doesn't run). Its only job is to mount the "Flat map" checkbox on the classic
    // map and keep it hidden under any capstone. The M/F keys are polled globally in GlobalTick.
    internal static void Tick(NMapScreen screen)
    {
        if (!screen.IsVisibleInTree()) return;
        EnsureMapToggle(screen);
        if (_mapToggle != null && GodotObject.IsInstanceValid(_mapToggle))
        {
            _mapToggle.Visible = NCapstoneContainer.Instance?.CurrentCapstoneScreen == null;
            // Re-apply every frame so viewport changes cannot make the classic control drift away
            // from the exact screen position occupied by "Flat map" on the flat page.
            Vector2 vp = screen.GetViewportRect().Size;
            _mapToggle.Position = MapStyleToggle.FlatMapPosition(vp, _mapToggle.Size.Y, _mapToggle.Size.Y);
        }
    }

    // The map is bound to a game key (M by default). We intercept that key in NInputManager and
    // route it here, suppressing the vanilla action so ONLY this fires — no more double-toggle with
    // the game's own map button. Fired once per press (from the shortcut prefix).
    internal static void OnMapKey() => ToggleMap();

    // O flips the MODE (flat<->classic), only meaningful while a map is showing.
    internal static void OnFlipKey()
    {
        if (MapShown()) SetFlat(!FlatMapConfig.PreferFlatMap);
    }


    // Is the map showing, in EITHER mode? Flat mode -> our capstone; classic mode -> NMapScreen.IsOpen.
    // (In flat mode the classic screen is never opened, so IsOpen stays false — the two are exclusive.)
    internal static bool MapShown() => FlatOpen() || (NMapScreen.Instance?.IsOpen ?? false);

    // Is OUR flat page the current capstone right now? (For the Open-prefix toggle.)
    internal static bool FlatShown() => FlatOpen();

    // We deliberately closed the map on this process frame — used to swallow the map room's
    // synchronous ReopenMap (fired on capstone-close) so a deliberate close actually stays closed.
    private static ulong _closeFrame = ulong.MaxValue;
    internal static bool SuppressReopenThisFrame() => Engine.GetProcessFrames() == _closeFrame;

    // HOTKEY-COLLISION SELF-CHECK (runs once). For each key WE use, log which game action (if any)
    // is bound to the same physical key. A surprise collision (like map==M) is then obvious in the
    // log at a glance instead of a 50-minute debugging spiral. Diagnostic-only: tolerant of a missing
    // field (never crashes the mod over logging).
    private static bool _keyAuditDone;
    internal static void AuditKeysOnce(NInputManager mgr)
    {
        if (_keyAuditDone) return;
        _keyAuditDone = true;
        (Key key, string label)[] ours =
            { (FlatMapMod.MapKey, "map"), (FlatMapMod.ToggleMiniMapKey, "flip/F") };
        var sb = new System.Text.StringBuilder("[FlatMap] KEY AUDIT (our key -> colliding game action):");
        foreach (var (key, label) in ours)
        {
            var hits = new List<string>();
            foreach (string input in MegaInput.AllInputs)
                if (mgr.GetCurrentHotkey(input) == key)
                    hits.Add(input);
            sb.Append($"  {label}={key}->{(hits.Count > 0 ? string.Join("+", hits) : "none")}");
        }
        Log.Info(sb.ToString());
    }

    // Compact one-line snapshot, for tracing.
    private static string MapState(string where)
    {
        NMapScreen? s = NMapScreen.Instance;
        string cap = NCapstoneContainer.Instance?.CurrentCapstoneScreen?.GetType().Name ?? "none";
        return $"[FlatMap] {where}: IsOpen={s?.IsOpen} flatOpen={FlatOpen()} " +
               $"prefFlat={FlatMapConfig.PreferFlatMap} capstone={cap}";
    }

    // Map key: if the map is showing (either mode) -> close it; else -> open it. Opening just calls
    // the game's Open(); our Open PREFIX renders it in the configured mode (flat page, or classic).
    private static void ToggleMap()
    {
        NMapScreen? screen = NMapScreen.Instance;
        if (screen == null) return;
        Log.Info(MapState("map key"));
        if (MapShown()) CloseMapCompletely();
        else screen.Open();
    }

    // Leave the map entirely -> prior view. Only ONE mode is ever open, so we close exactly that one.
    // Stamp the frame so the map room's ReopenMap (fired synchronously on capstone-close) is swallowed
    // by the Open prefix — otherwise a deliberate close would instantly bounce back open.
    internal static void CloseMapCompletely()
    {
        Log.Info(MapState("close"));
        _closeFrame = Engine.GetProcessFrames();
        if (FlatOpen()) NCapstoneContainer.Instance?.Close(); // flat mode: classic was never opened
        else NMapScreen.Instance?.Close();                    // classic mode
    }

    // Switch MODE (flat<->classic), persist it, sync the checkboxes, and re-render the *currently
    // showing* map in the new mode. Never leaves the other mode visible for a frame.
    internal static void SetFlat(bool flat)
    {
        Log.Info($"[FlatMap] SetFlat({flat}) <- {MapState("setflat")}");
        FlatMapConfig.PreferFlatMap = flat;
        MapStyleToggle.SyncAll(flat);
        NMapScreen? screen = NMapScreen.Instance;
        NCapstoneContainer? cc = NCapstoneContainer.Instance;
        if (screen == null || cc == null) return;
        if (flat)
        {
            if (screen.IsOpen) screen.Close(false); // drop classic instantly (no fade) — no flash
            if (!FlatOpen()) OpenFlat(screen);
        }
        else
        {
            if (FlatOpen()) cc.Close();             // drop the flat page
            if (!screen.IsOpen) screen.Open();      // classic opens (the Open prefix now allows it)
        }
    }

    // Rebuild + redraw the flat page in place (used when the game changes travelability while the
    // flat page is already showing — e.g. a map room enables travel just after we opened).
    internal static void RefreshFlat()
    {
        if (!FlatOpen() || _screen == null || !GodotObject.IsInstanceValid(_screen)) return;
        NMapScreen? screen = NMapScreen.Instance;
        if (screen == null) return;
        RecalcTravelMethod.Invoke(screen, null);
        _screen.Configure(BuildModel(screen), screen.GetViewportRect().Size, coord => Travel(screen, coord));
        _screen.QueueRedraw();
    }

    // Add the "Flat map" toggle switch onto the classic map screen (once).
    private static void EnsureMapToggle(NMapScreen screen)
    {
        if (screen.HasMeta(MapToggleMeta)) return;
        screen.SetMeta(MapToggleMeta, true);
        ToggleSwitch box = MapStyleToggle.Create();
        _mapToggle = box;
        screen.AddChild(box);
        // Bottom-left — exactly the SAME spot the flat page puts its "Flat map" toggle, so switching
        // renderings leaves the control stationary. The second height reserves the Compress row
        // that exists on the flat page; all ToggleSwitch rows share the same measured height.
        Vector2 vp = screen.GetViewportRect().Size;
        box.Position = MapStyleToggle.FlatMapPosition(vp, box.Size.Y, box.Size.Y);
        Control? mapDefault = ((IScreenContext)screen).DefaultFocusedControl;
        if (mapDefault != null && GodotObject.IsInstanceValid(mapDefault))
        {
            _classicDefaultFocus = mapDefault;
            _classicPreviousLeft = mapDefault.FocusNeighborLeft;
            _hasClassicPreviousLeft = true;
            box.FocusNeighborRight = box.GetPathTo(mapDefault);
            mapDefault.FocusNeighborLeft = mapDefault.GetPathTo(box);
        }
    }

    // Open the minimap as a capstone screen — the game parents/shows it, dims the map behind, keeps
    // the top bar, pauses combat, and routes focus/ESC to it, exactly like the deck-view screen.
    // Entry point from the NMapScreen.Open prefix: render the flat page instead of the classic map.
    // Guarded so repeated Open() calls (e.g. a map room's reopen-on-capstone-close) don't stack.
    internal static void OpenFlatFromHook(NMapScreen screen)
    {
        if (FlatOpen()) return;
        OpenFlat(screen);
    }

    private static void OpenFlat(NMapScreen screen)
    {
        NCapstoneContainer? cc = NCapstoneContainer.Instance;
        if (cc == null) return; // not in a run / no capstone container -> nothing to open into

        // The classic map is NEVER opened in flat mode, so refresh travel state ourselves (Open()
        // normally does this) before reading it — the point dictionary is already built at act start.
        RecalcTravelMethod.Invoke(screen, null);

        MiniMapModel model = BuildModel(screen);
        Vector2 viewport = screen.GetViewportRect().Size;
        Log.Info($"[FlatMap] flat open: nodes={model.Nodes.Count} edges={model.Edges.Count} " +
                 $"viewport={viewport} current={(model.Current?.ToString() ?? "none")} " +
                 $"act={model.ActIndex + 1}:'{model.ActName}' floor={model.ActFloor} travelEnabled={model.TravelEnabled}");

        if (_screen == null || !GodotObject.IsInstanceValid(_screen))
            _screen = new MiniMapScreen();
        _screen.Configure(model, viewport, coord => Travel(screen, coord));
        cc.Open(_screen);
        BorrowLegend(screen);
    }

    // --- The borrowed vanilla Legend panel -------------------------------------------------------
    private static Control? _legend;
    private static Node? _legendHome;
    private static Vector2 _legendHomePos;
    private static Color _legendHomeModulate;
    private static bool _legendHomeVisible;

    private static void BorrowLegend(NMapScreen screen)
    {
        if (_screen == null || !GodotObject.IsInstanceValid(_screen))
            return;
        if (MapLegendField.GetValue(screen) is not Control legend || !GodotObject.IsInstanceValid(legend))
            return;
        if (!ReferenceEquals(legend.GetParent(), _screen))
        {
            _legend = legend;
            _legendHome = legend.GetParent();
            _legendHomePos = legend.Position;
            _legendHomeModulate = legend.Modulate;
            _legendHomeVisible = legend.Visible;
            _legendHome?.RemoveChild(legend);
            _screen.AddChild(legend);
        }
        legend.Visible = true;
        legend.Modulate = Colors.White;
        legend.Position = new Vector2(_screen.Size.X * 0.8f, _legendHomePos.Y); // vanilla's own anchor
    }

    // The borrowed panel, for the legend-hover pulse (null when not borrowed / freed).
    internal static Control? BorrowedLegend =>
        _legend != null && GodotObject.IsInstanceValid(_legend) ? _legend : null;

    // Give the panel back to the classic map screen exactly as we found it. Runs on page close and
    // on mod disable; idempotent and validity-guarded (act transitions can free either side).
    internal static void ReturnLegend()
    {
        Control? legend = _legend;
        _legend = null;
        if (legend == null || !GodotObject.IsInstanceValid(legend))
            return;
        legend.GetParent()?.RemoveChild(legend);
        if (_legendHome != null && GodotObject.IsInstanceValid(_legendHome))
        {
            _legendHome.AddChild(legend);
            legend.Position = _legendHomePos;
            legend.Modulate = _legendHomeModulate;
            legend.Visible = _legendHomeVisible;
        }
        else
        {
            legend.QueueFree(); // its home is gone (act/screen freed) — don't leak an orphan
        }
        _legendHome = null;
    }

    // Clicking a travelable node runs the game's own selection path (identical to a real click),
    // then closes the page so the travel animates on the real map.
    private static void Travel(NMapScreen screen, MapCoord coord)
    {
        if (!(bool)TravelEnabledGetter.Invoke(screen, null)!)
            return;
        if (PointDictField.GetValue(screen) is not System.Collections.IDictionary dict
            || dict[coord] is not NMapPoint np || !GodotObject.IsInstanceValid(np)
            || np.State != MapPointState.Travelable)
            return;

        Log.Info($"[FlatMap] minimap travel -> {coord}");
        screen.OnMapPointSelectedLocally(np);
        NCapstoneContainer.Instance?.Close();
    }

    // Compacted display lane per map coord, cached per act-map signature (see ComputeLanes).
    private static readonly Dictionary<string, Dictionary<MapCoord, int>> _laneCache = new();

    private static MiniMapModel BuildModel(NMapScreen screen)
    {
        object runState = RunStateField.GetValue(screen)!;
        object act = ActGetter.Invoke(runState, null)!;
        var model = new MiniMapModel
        {
            Current = CurrentCoordGetter.Invoke(runState, null) is MapCoord c ? c : null,
            ActIndex = (int)ActIndexGetter.Invoke(runState, null)!,
            ActFloor = (int)ActFloorGetter.Invoke(runState, null)!,
            ActName = (string)LocStringFormat.Invoke(ActTitleGetter.Invoke(act, null), null)! ?? "",
            CurrentMarker = (MarkerField.GetValue(screen) as TextureRect)?.Texture,
            TravelEnabled = (bool)TravelEnabledGetter.Invoke(screen, null)!,
            MapBg = MapBgColorGetter.Invoke(act, null) is Color bg ? bg : new Color(0.05f, 0.06f, 0.09f),
            PathTraveled = MapTraveledColorGetter.Invoke(act, null) is Color tc ? tc : new Color(0.96f, 0.80f, 0.35f),
            PathUntraveled = MapUntraveledColorGetter.Invoke(act, null) is Color uc ? uc : new Color(1, 1, 1),
        };

        if (PointDictField.GetValue(screen) is not System.Collections.IDictionary dict)
            return model;

        // Pass 1: read the live graph (coords, type, state, icon+outline art) and its edges.
        var raw = new List<(MapCoord coord, MapPointType type, MapPointState state, Texture2D? icon, Texture2D? outline)>();
        foreach (System.Collections.DictionaryEntry entry in dict)
        {
            if (entry.Value is not NMapPoint np || !GodotObject.IsInstanceValid(np))
                continue;
            MapPoint mp = np.Point;
            if (mp == null)
                continue;
            Texture2D? icon = IconOf(np);
            Texture2D? outline = OutlineOf(np);
            if (np is NBossMapPoint)
            {
                (Texture2D? bossIcon, Texture2D? bossOutline) = BossArt(runState, act, mp);
                icon = bossIcon ?? icon;       // the proper boss art, not a letter/placeholder
                outline = bossOutline ?? outline;
            }
            raw.Add((mp.coord, mp.PointType, np.State, icon, outline));
            foreach (MapPoint child in mp.Children)
                model.Edges.Add((mp.coord, child.coord));
        }

        // Pass 2: flatten the layout (pure MapLayout, cached per act-map), then place nodes at the
        // computed lane instead of the raw game column.
        Dictionary<MapCoord, int> lane = ComputeLanes(raw.Select(r => r.coord), model.Edges);
        // Reachability seed = where the game says you can move RIGHT NOW (every point it flagged
        // Travelable) plus the current node. This is already relic-aware (e.g. Wing Boots widen the
        // Travelable set), so BFS-forward from it greys out only what you genuinely can't still reach.
        var seeds = new List<MapCoord>();
        if (model.Current is MapCoord cur) seeds.Add(cur);
        foreach (var r in raw)
            if (r.state == MapPointState.Travelable) seeds.Add(r.coord);
        HashSet<MapCoord> reachable = ReachableFrom(seeds, model.Edges);
        var unknownReveals = new List<string>();
        foreach (var r in raw)
        {
            // A visited "?" has revealed its real room — colour/tally it as that type.
            MapPointType eff = r.type;
            if (r.type == MapPointType.Unknown && r.state == MapPointState.Traveled)
            {
                eff = ResolveUnknown(runState, model.ActIndex, r.coord.row);
                unknownReveals.Add($"{r.coord.row},{r.coord.col}->{eff}");
            }
            if (r.icon != null && GodotObject.IsInstanceValid(r.icon))
                model.TypeArt.TryAdd(eff, (r.icon, r.outline)); // representative art per type, for the legend
            model.Nodes[r.coord] = new MiniNode
            {
                Coord = r.coord, Type = r.type, EffType = eff, State = r.state,
                Icon = r.icon, Outline = r.outline,
                Lane = lane.TryGetValue(r.coord, out int ly) ? ly : r.coord.col,
                RawLane = r.coord.col,
                // Reachable = can still be travelled to from where we are (or no current pos yet).
                Reachable = model.Current is null || reachable.Contains(r.coord),
            };
        }
        if (FlatMapConfig.DumpMapGraph)
        {
            DumpGraph(raw, model.Edges, lane);
            Log.Info($"[FlatMap] visited-? reveals: {(unknownReveals.Count > 0 ? string.Join(" ", unknownReveals) : "(none)")}");
        }
        return model;
    }

    // Forward-reachable set: BFS down the edges (parent -> child) from every seed. Seeds are the
    // nodes you can legally step to now (Travelable, relic-aware) plus the current node — so the set
    // is exactly "rooms you can still get to". Everything else you didn't visit is a dead option.
    private static HashSet<MapCoord> ReachableFrom(IEnumerable<MapCoord> seeds, List<(MapCoord From, MapCoord To)> edges)
    {
        var set = new HashSet<MapCoord>();
        var adj = new Dictionary<MapCoord, List<MapCoord>>();
        foreach (var (f, t) in edges)
        {
            if (!adj.TryGetValue(f, out List<MapCoord>? outs)) adj[f] = outs = new List<MapCoord>();
            outs.Add(t);
        }
        var queue = new Queue<MapCoord>();
        foreach (MapCoord s in seeds)
            if (set.Add(s)) queue.Enqueue(s);
        while (queue.Count > 0)
        {
            MapCoord n = queue.Dequeue();
            if (!adj.TryGetValue(n, out List<MapCoord>? kids)) continue;
            foreach (MapCoord k in kids)
                if (set.Add(k)) queue.Enqueue(k);
        }
        return set;
    }

    // Log the live graph so a real random level can be reconstructed offline and dropped into the
    // layout test harness (layout/Program.cs) as a real-world case.
    private static void DumpGraph(
        List<(MapCoord coord, MapPointType type, MapPointState state, Texture2D? icon, Texture2D? outline)> raw,
        List<(MapCoord From, MapCoord To)> edges, Dictionary<MapCoord, int> lane)
    {
        var nb = new System.Text.StringBuilder("[FlatMap] MAPDUMP nodes(row,col,type):");
        foreach (var r in raw.OrderBy(r => r.coord.row).ThenBy(r => r.coord.col))
            nb.Append($" {r.coord.row},{r.coord.col},{r.type}");
        Log.Info(nb.ToString());
        var eb = new System.Text.StringBuilder("[FlatMap] MAPDUMP edges(row,col->row,col):");
        foreach (var (f, t) in edges.OrderBy(e => e.From.row).ThenBy(e => e.From.col))
            eb.Append($" {f.row},{f.col}->{t.row},{t.col}");
        Log.Info(eb.ToString());
        var lb = new System.Text.StringBuilder("[FlatMap] MAPDUMP lanes(row,col=lane):");
        foreach (var kv in lane.OrderBy(k => k.Key.row).ThenBy(k => k.Key.col))
            lb.Append($" {kv.Key.row},{kv.Key.col}={kv.Value}");
        Log.Info(lb.ToString());
    }

    // Run the pure flatten algorithm on this level's graph and return coord -> display lane. Cached
    // by a signature of the coords+edges, so each random act-map is solved once and reused.
    private static Dictionary<MapCoord, int> ComputeLanes(
        IEnumerable<MapCoord> coords, List<(MapCoord From, MapCoord To)> edges)
    {
        MapCoord[] cs = coords.ToArray();
        var sig = new System.Text.StringBuilder();
        foreach (MapCoord c in cs.OrderBy(c => c.row).ThenBy(c => c.col))
            sig.Append(c.col).Append(',').Append(c.row).Append(';');
        sig.Append('|');
        foreach (var (f, t) in edges.OrderBy(e => e.From.row).ThenBy(e => e.From.col)
                                    .ThenBy(e => e.To.row).ThenBy(e => e.To.col))
            sig.Append(f.col).Append(',').Append(f.row).Append('-').Append(t.col).Append(',').Append(t.row).Append(';');
        string key = sig.ToString();
        if (_laneCache.TryGetValue(key, out Dictionary<MapCoord, int>? cached))
            return cached;

        var idOf = new Dictionary<MapCoord, int>();
        var nodes = new List<LNode>();
        foreach (MapCoord c in cs)
            if (!idOf.ContainsKey(c)) { idOf[c] = nodes.Count; nodes.Add(new LNode(nodes.Count, c.row, c.col)); }

        var ledges = new List<(int, int)>();
        foreach (var (f, t) in edges)
        {
            if (!idOf.TryGetValue(f, out int fi) || !idOf.TryGetValue(t, out int ti))
                continue; // endpoint not in the point set (shouldn't happen) -> nothing to draw
            // Orient parent(lower row) -> child(higher row); LGraph requires a strict layering.
            if (nodes[fi].Row <= nodes[ti].Row) ledges.Add((fi, ti));
            else ledges.Add((ti, fi));
        }

        var graph = new LGraph(nodes, ledges);
        int[] lanes = MapLayout.AssignLanes(graph);

        // Runtime safety net: the layout is legal by construction, but never DRAW a misleading map.
        // If a lane assignment ever overlaps or reorders a row, crash loud instead (work-or-crash).
        List<string> violations = LayoutInvariants.Check(graph, lanes);
        if (violations.Count > 0)
            throw new InvalidOperationException(
                "[FlatMap] minimap layout produced an ILLEGAL placement: " + string.Join("; ", violations));

        var result = new Dictionary<MapCoord, int>();
        foreach (var kv in idOf) result[kv.Key] = lanes[kv.Value];
        _laneCache[key] = result;
        Log.Info($"[FlatMap] minimap layout: {nodes.Count} nodes, {ledges.Count} edges -> " +
                 $"{lanes.Distinct().Count()} lanes (cached)");
        return result;
    }
}

// The "Flat map" flip-flop toggle, shown on BOTH the classic map and the flat page. All instances
// stay in agreement via SyncAll; toggling any routes through MiniMapController.SetFlat.
internal static class MapStyleToggle
{
    private static readonly List<ToggleSwitch> _boxes = new();

    // Shared placement for the "Flat map" row in BOTH renderings. On the classic page the
    // Compress row is absent, but its measured row height remains reserved so this control does
    // not jump when the user switches map styles.
    internal static Vector2 FlatMapPosition(Vector2 viewport, float flatHeight, float compressHeight) =>
        new(8f, viewport.Y - 16f - compressHeight - 2f - flatHeight);

    internal static ToggleSwitch Create()
    {
        var box = new ToggleSwitch("Flat map", FlatMapConfig.PreferFlatMap, OnToggled)
        {
            ZIndex = 60,
            Name = "FlatMapMapStyleToggle",
        };
        _boxes.Add(box);
        return box;
    }

    internal static void SyncAll(bool flat)
    {
        _boxes.RemoveAll(b => !GodotObject.IsInstanceValid(b));
        foreach (ToggleSwitch b in _boxes)
            b.SetOn(flat);
    }

    private static void OnToggled(bool pressed) => MiniMapController.SetFlat(pressed);
}

internal struct MiniNode
{
    public MapCoord Coord;
    public MapPointType Type;      // the raw point type (Unknown stays Unknown here)
    public MapPointType EffType;   // effective type for colour/legend: a visited "?" resolves to its real room
    public MapPointState State;
    public Texture2D? Icon;    // the game's own room icon (solid fill shape); null -> letter glyph
    public Texture2D? Outline; // the game's detail-stroke texture, carved in the map bg colour on top
    public int Lane;        // compacted display lane from MapLayout — NOT the raw game col
    public int RawLane;     // uncompressed lane == the game's column (for the "raw 1:1" view)
    public bool Reachable;  // can still be travelled to from the current position
}

internal sealed class MiniMapModel
{
    public readonly Dictionary<MapCoord, MiniNode> Nodes = new();
    public readonly List<(MapCoord From, MapCoord To)> Edges = new();
    // Representative art per type, for the legend (icon + optional outline stroke).
    public readonly Dictionary<MapPointType, (Texture2D Icon, Texture2D? Outline)> TypeArt = new();
    public MapCoord? Current;
    public bool TravelEnabled;       // can you move right now (current room finished)?
    public Texture2D? CurrentMarker; // the game's "you are here" arrow art (null in multiplayer)
    public Color MapBg;              // the act's own map background colour (Act.MapBgColor)
    public Color PathTraveled;       // the act's traveled-path colour (Act.MapTraveledColor)
    public Color PathUntraveled;     // the act's untraveled-path colour (Act.MapUntraveledColor)
    public string ActName = "";
    public int ActIndex;
    public int ActFloor;
}

internal sealed partial class MapNodeFocusControl : Control
{
    private readonly Action _activate;

    internal MapNodeFocusControl(Action activate, Action focused, Action blurred)
    {
        _activate = activate;
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Ignore;
        Connect(Control.SignalName.FocusEntered, Callable.From(focused));
        Connect(Control.SignalName.FocusExited, Callable.From(blurred));
        Connect(Control.SignalName.GuiInput, Callable.From<InputEvent>(OnGuiInput));
    }

    private void OnGuiInput(InputEvent e)
    {
        if (!e.IsActionPressed(MegaInput.confirm))
            return;
        try { _activate(); }
        catch (Exception ex) { ModRuntime.Disable(nameof(MapNodeFocusControl), ex); }
        AcceptEvent();
    }
}

// The minimap PAGE: a capstone screen opened via NCapstoneContainer (like the deck-view screen),
// so the game supplies the top bar, dim backstop, combat pause, and native focus/ESC routing.
//
// IMPORTANT: we draw via the `Draw` SIGNAL and take input via the `gui_input` SIGNAL, NOT by
// overriding _Draw()/_GuiInput(). This mod builds with the plain Microsoft.NET.Sdk (referencing
// GodotSharp.dll directly), so Godot's source generators don't run and custom node virtual
// overrides never fire. Signal connections and interface methods (ICapstoneScreen, invoked
// directly by the container) still work, so everything is wired through those. MouseFilter.Stop
// lets us take hover/click on the page.
internal sealed partial class MiniMapScreen : Control, ICapstoneScreen
{
    private MiniMapModel? _model;
    private Action<MapCoord>? _onTravel;
    private readonly Dictionary<MapCoord, Vector2> _positions = new(); // filled each draw, for hit-testing
    private readonly Dictionary<MapCoord, MapNodeFocusControl> _nodeFocusControls = new();
    private float _nodeRadius = 12f;
    private MapCoord? _hovered;
    private MapCoord? _focused;
    private Control? _defaultFocusedControl;
    private bool _logDrawOnce;
    private bool _compress = true;               // compressed layout vs raw 1:1 with game columns
    private readonly ToggleSwitch _styleToggle;  // "Flat map" (on for this page)
    private readonly ToggleSwitch _compressToggle; // "Compress" (on = flattened; off = raw 1:1)

    // --- ICapstoneScreen ---
    public NetScreenType ScreenType => NetScreenType.Map;
    public bool UseSharedBackstop => true;                      // use the standard dimmed backstop
    public Control? DefaultFocusedControl => _defaultFocusedControl ?? _styleToggle;

    internal MiniMapScreen()
    {
        MouseFilter = MouseFilterEnum.Stop;
        _tick = Callable.From(OnPageTick);
        Connect(CanvasItem.SignalName.Draw, Callable.From(OnDraw));
        Connect(Control.SignalName.GuiInput, Callable.From<InputEvent>(OnGuiInput));
        _styleToggle = MapStyleToggle.Create();
        _compressToggle = new ToggleSwitch("Compress", FlatMapConfig.CompressMap, OnCompressToggled) { ZIndex = 60 };
        AddChild(_styleToggle);
        AddChild(_compressToggle);
        _styleToggle.Connect(Control.SignalName.FocusEntered, Callable.From(ClearNodeFocus));
        _compressToggle.Connect(Control.SignalName.FocusEntered, Callable.From(ClearNodeFocus));
    }

    // Which lane to draw a node at: compressed (flattened) or the raw game column (1:1 view).
    private int LaneOf(MiniNode n) => _compress ? n.Lane : n.RawLane;

    // The original game's size language: elites draw LARGER than ordinary rooms and the boss larger
    // still, so danger reads at a glance even before colour does. Applied to drawing, hit-testing,
    // and the focus rects alike (EffType, so a revealed "?" that was an elite grows too).
    private static float TypeScale(MapPointType t) => t switch
    {
        MapPointType.Boss => 1.9f,
        MapPointType.Elite => 1.35f,
        MapPointType.Ancient => 1.45f, // the vanilla start illustration is prominently large
        _ => 1f,
    };

    private float RadiusOf(MiniNode n) => _nodeRadius * TypeScale(n.EffType);

    // Text/highlight colour with guaranteed contrast against the act's map background: dark ink on
    // a light surface (Act 1's tan parchment), pale on a dark one.
    private Color Ink(float alpha)
    {
        Color bg = _model?.MapBg ?? new Color(0.05f, 0.06f, 0.09f);
        float lum = 0.299f * bg.R + 0.587f * bg.G + 0.114f * bg.B;
        return lum > 0.5f ? new Color(0.20f, 0.14f, 0.08f, alpha) : new Color(0.86f, 0.88f, 0.93f, alpha);
    }

    private void OnCompressToggled(bool on)
    {
        _compress = on;
        FlatMapConfig.CompressMap = on;
        UpdateLayoutPositions();
        RebuildNodeFocusControls();
        QueueRedraw();
    }

    // Set the level to draw + viewport size + travel callback (called just before the page opens).
    internal void Configure(MiniMapModel model, Vector2 viewport, Action<MapCoord> onTravel)
    {
        MapCoord? restoreCoord = _focused;
        bool restoreNodeFocus = restoreCoord is MapCoord oldCoord
            && _nodeFocusControls.TryGetValue(oldCoord, out MapNodeFocusControl? oldFocus)
            && oldFocus.HasFocus();
        _model = model;
        _onTravel = onTravel;
        _hovered = null;
        _focused = null;
        _logDrawOnce = true;
        _compress = FlatMapConfig.CompressMap;
        Position = Vector2.Zero;
        Size = viewport; // capstone fills the screen; the game's top bar renders above us
        _styleToggle.SetOn(FlatMapConfig.PreferFlatMap);
        _compressToggle.SetOn(_compress);
        // Toggles bottom-left: one tight column, FULLY left-aligned (hard against the screen edge)
        // and FLUSH to the bottom — each checkbox exactly one line below the previous (measured
        // heights, no blank lines), stacked upward from the bottom edge.
        _styleToggle.Position = MapStyleToggle.FlatMapPosition(
            viewport, _styleToggle.Size.Y, _compressToggle.Size.Y);
        _compressToggle.Position = new Vector2(
            _styleToggle.Position.X, _styleToggle.Position.Y + _styleToggle.Size.Y + 2f);
        _styleToggle.FocusNeighborBottom = _styleToggle.GetPathTo(_compressToggle);
        _compressToggle.FocusNeighborTop = _compressToggle.GetPathTo(_styleToggle);
        UpdateLayoutPositions();
        RebuildNodeFocusControls();
        if (restoreNodeFocus && restoreCoord is MapCoord target
            && _nodeFocusControls.TryGetValue(target, out MapNodeFocusControl? replacement))
            replacement.GrabFocus();
        else if (restoreNodeFocus)
            DefaultFocusedControl?.GrabFocus();
        QueueRedraw();
    }

    // Capstone lifecycle — invoked by NCapstoneContainer through the interface (not engine hooks).
    // All the "back/cancel" hotkeys — the same set NBackButton uses. We claim ALL of them while
    // open so a single ESC backs out exactly one level (our page). If we only claimed `cancel`,
    // the map's still-active back button would also catch `pauseAndBack` on the same ESC and close
    // the whole map underneath us.
    private static readonly StringName[] BackHotkeys = { MegaInput.cancel, MegaInput.pauseAndBack, MegaInput.back };

    public void AfterCapstoneOpened()
    {
        Visible = true; // draw when shown (in case the container left the reused node hidden)
        foreach (StringName hk in BackHotkeys)
            NHotkeyManager.Instance?.PushHotkeyReleasedBinding(hk, OnBack);
        if (MiniMapController.UsingController())
            DefaultFocusedControl?.GrabFocus();
        // Per-frame tick (SceneTree signal — _Process overrides don't fire without source
        // generators) driving all page motion. Connected only while the page is open.
        if (!_tickConnected)
        {
            GetTree().Connect(SceneTree.SignalName.ProcessFrame, _tick);
            _tickConnected = true;
        }
        QueueRedraw();
    }

    public void AfterCapstoneClosed()
    {
        // The container disables our ProcessMode on close but leaves the node parented; hide it
        // ourselves so our opaque page can't linger over the game once closed.
        Visible = false;
        MiniMapController.ReturnLegend(); // the borrowed vanilla legend goes home
        if (_tickConnected)
        {
            GetTree()?.Disconnect(SceneTree.SignalName.ProcessFrame, _tick);
            _tickConnected = false;
        }
        _legendHighlight = null;
        _hoverAnim.Clear();
        _pulsePhase.Clear();
        _prevHover = null;
        _pressed = null;
        foreach (StringName hk in BackHotkeys)
            NHotkeyManager.Instance?.RemoveHotkeyReleasedBinding(hk, OnBack);
    }

    // --- The page tick (per-frame while open) drives all MOTION: the hover swell for every node
    // (fast in, gradual out), the frontier pulse, and the legend-hover type pulse. We host the
    // game's REAL legend items, so we hit-test them directly; their fixed node names carry the
    // type mapping (see NMapLegendItem.SetMapPointType).
    private MapPointType? _legendHighlight;
    private bool _tickConnected;
    private readonly Callable _tick;
    private readonly Dictionary<MapCoord, float> _hoverAnim = new(); // coord -> 0..1 swell progress
    private readonly Dictionary<MapCoord, float> _pulsePhase = new(); // per-node phase after an unfocus reset
    private MapCoord? _prevHover;  // to detect hover-exit for the vanilla pulse-phase reset
    private MapCoord? _pressed;    // travelable node currently held down (vanilla 0.9x squash)
    private ulong _lastTickMs;

    private void OnPageTick()
    {
        if (!ModRuntime.Enabled) return;
        try
        {
            ulong now = Time.GetTicksMsec();
            float dt = Mathf.Clamp((now - _lastTickMs) / 1000f, 0f, 0.1f);
            _lastTickMs = now;

            _legendHighlight = HoveredLegendType();

            // Hover swell — vanilla's exact curve (2026-08-01): AnimHover reaches full size in
            // 0.05s, AnimUnhover releases over 0.5s. A hovered LEGEND row holds the same swell
            // on every node of that type (vanilla's OnHighlightPointType -> AnimHover), matched
            // on the RAW type like vanilla does (a revealed "?" still answers to the "?" row).
            // Inaccessible (greyed) rooms never swell from the legend — no attention cue where
            // no further decision can be made (directive 2026-08-02).
            if (_model != null)
            {
                MapCoord? hov = _hovered ?? _focused;
                // Vanilla resets a node's pulse timer on unfocus (_elapsedTime = 5π/4) so the
                // frontier pulse restarts from a trough instead of popping. Emulate by giving
                // the departed node a phase that lands sin(t*4 + phase) at that same point now.
                if (_prevHover is MapCoord ph && (hov is not MapCoord nh || nh.col != ph.col || nh.row != ph.row))
                    _pulsePhase[ph] = 3.926991f - Time.GetTicksMsec() / 1000f * 4f;
                _prevHover = hov;
                foreach (KeyValuePair<MapCoord, MiniNode> kv in _model.Nodes)
                {
                    MapCoord c = kv.Key;
                    MiniNode node = kv.Value;
                    bool legendHit = _legendHighlight is MapPointType hl && node.Type == hl
                        && !IsDead(node);
                    bool over = (hov is MapCoord h && h.col == c.col && h.row == c.row) || legendHit;
                    float cur = _hoverAnim.GetValueOrDefault(c);
                    float next = over ? Mathf.Min(1f, cur + dt / 0.05f) : Mathf.Max(0f, cur - dt / 0.5f);
                    if (next <= 0f) _hoverAnim.Remove(c);
                    else _hoverAnim[c] = next;
                }
            }

            // The page is animated whenever it's open (pulsing frontier), so redraw each frame.
            QueueRedraw();
        }
        catch (Exception ex)
        {
            ModRuntime.Disable(nameof(MiniMapScreen) + ".tick", ex);
        }
    }

    private MapPointType? HoveredLegendType()
    {
        Control? legend = MiniMapController.BorrowedLegend;
        if (legend == null || !legend.Visible || !legend.IsInsideTree())
            return null;
        // The real legend scene nests its NMapLegendItems; do not assume LegendItems is a direct
        // child. Test each actual item in its own local coordinate space, which remains correct
        // after the whole panel is reparented onto our capstone.
        foreach (NMapLegendItem item in DescendantLegendItems(legend))
        {
            if (!GodotObject.IsInstanceValid(item)
                || !new Rect2(Vector2.Zero, item.Size).HasPoint(item.GetLocalMousePosition()))
                continue;
            return item.Name.ToString() switch
            {
                "UnknownLegendItem" => MapPointType.Unknown,
                "MerchantLegendItem" => MapPointType.Shop,
                "TreasureLegendItem" => MapPointType.Treasure,
                "RestSiteLegendItem" => MapPointType.RestSite,
                "EnemyLegendItem" => MapPointType.Monster,
                "EliteLegendItem" => MapPointType.Elite,
                _ => null,
            };
        }
        return null;
    }

    private static IEnumerable<NMapLegendItem> DescendantLegendItems(Node root)
    {
        foreach (Node child in root.GetChildren())
        {
            if (child is NMapLegendItem item)
                yield return item;
            foreach (NMapLegendItem nested in DescendantLegendItems(child))
                yield return nested;
        }
    }

    // ESC/back from the flat page LEAVES THE MAP ENTIRELY -> prior view (fight/reward/room). The flat
    // map is never a sub-layer you peel back to the classic map from: ESC exits the whole map, exactly
    // as it does from the classic map. (Switching flat<->classic only happens via F or the checkbox.)
    private void OnBack()
    {
        NCapstoneContainer? cc = NCapstoneContainer.Instance;
        if (cc != null && ReferenceEquals(cc.CurrentCapstoneScreen, this))
            MiniMapController.CloseMapCompletely();
    }

    // Nearest node whose (type-scaled) circle contains pt (a touch generous), else null.
    private MapCoord? NodeAt(Vector2 pt)
    {
        if (_model == null) return null;
        foreach (KeyValuePair<MapCoord, Vector2> kv in _positions)
            if (_model.Nodes.TryGetValue(kv.Key, out MiniNode n)
                && pt.DistanceTo(kv.Value) <= RadiusOf(n) * 1.4f)
                return kv.Key;
        return null;
    }

    private bool IsTravelable(MapCoord c) =>
        _model != null && _model.TravelEnabled
        && _model.Nodes.TryGetValue(c, out MiniNode n) && n.State == MapPointState.Travelable;

    private void ClearNodeFocus()
    {
        if (_focused is null) return;
        _focused = null;
        QueueRedraw();
    }

    private void RebuildNodeFocusControls()
    {
        foreach (MapNodeFocusControl old in _nodeFocusControls.Values)
        {
            if (!GodotObject.IsInstanceValid(old)) continue;
            RemoveChild(old);
            old.QueueFree();
        }
        _nodeFocusControls.Clear();
        _defaultFocusedControl = null;

        if (_model == null)
            return;

        if (!_model.TravelEnabled)
            return;

        foreach (MiniNode node in _model.Nodes.Values
                     .Where(n => n.State == MapPointState.Travelable)
                     .OrderBy(n => n.Coord.row).ThenBy(n => LaneOf(n)))
        {
            MapCoord coord = node.Coord;
            float fr = RadiusOf(node);
            var focus = new MapNodeFocusControl(
                () => _onTravel?.Invoke(coord),
                () => { _focused = coord; _hovered = null; QueueRedraw(); },
                () => { if (_focused is MapCoord c && c.Equals(coord)) { _focused = null; QueueRedraw(); } })
            {
                Name = $"MapNode_{coord.row}_{coord.col}",
                Position = _positions[coord] - new Vector2(fr * 1.5f, fr * 1.5f),
                Size = new Vector2(fr * 3f, fr * 3f),
                TooltipText = "Travel here",
            };
            AddChild(focus);
            _nodeFocusControls[coord] = focus;
            _defaultFocusedControl ??= focus;
        }

        foreach ((MapCoord coord, MapNodeFocusControl focus) in _nodeFocusControls)
        {
            focus.FocusNeighborLeft = NeighborPath(coord, Vector2.Left);
            focus.FocusNeighborRight = NeighborPath(coord, Vector2.Right);
            focus.FocusNeighborTop = NeighborPath(coord, Vector2.Up);
            focus.FocusNeighborBottom = NeighborPath(coord, Vector2.Down);
        }

        if (_defaultFocusedControl is MapNodeFocusControl)
        {
            MapNodeFocusControl leftmost = _nodeFocusControls
                .OrderBy(kv => _positions[kv.Key].X).ThenBy(kv => _positions[kv.Key].Y)
                .First().Value;
            leftmost.FocusNeighborLeft = leftmost.GetPathTo(_styleToggle);
            _styleToggle.FocusNeighborRight = _styleToggle.GetPathTo(leftmost);
            _compressToggle.FocusNeighborRight = _compressToggle.GetPathTo(leftmost);
        }
    }

    private NodePath NeighborPath(MapCoord from, Vector2 direction)
    {
        Vector2 origin = _positions[from];
        MapNodeFocusControl? best = null;
        float bestScore = float.MaxValue;
        foreach ((MapCoord coord, MapNodeFocusControl candidate) in _nodeFocusControls)
        {
            if (coord.Equals(from)) continue;
            Vector2 delta = _positions[coord] - origin;
            float forward = delta.Dot(direction);
            if (forward <= 0f) continue;
            float lateral = Mathf.Abs(delta.Cross(direction));
            float score = delta.Length() + lateral * 1.5f;
            if (score >= bestScore) continue;
            bestScore = score;
            best = candidate;
        }
        return best == null ? new NodePath("") : _nodeFocusControls[from].GetPathTo(best);
    }

    private void UpdateLayoutPositions()
    {
        MiniMapModel? model = _model;
        if (model == null || model.Nodes.Count == 0)
            return;

        int minLane = int.MaxValue, maxLane = int.MinValue, minRow = int.MaxValue, maxRow = int.MinValue;
        int minRaw = int.MaxValue, maxRaw = int.MinValue;
        foreach (MiniNode n in model.Nodes.Values)
        {
            minLane = Math.Min(minLane, LaneOf(n));
            maxLane = Math.Max(maxLane, LaneOf(n));
            minRaw = Math.Min(minRaw, n.RawLane);   // uncompressed span = the vertical-height reference
            maxRaw = Math.Max(maxRaw, n.RawLane);
            minRow = Math.Min(minRow, n.Coord.row);
            maxRow = Math.Max(maxRow, n.Coord.row);
        }
        float rowSpan = Math.Max(1, maxRow - minRow);
        float refLaneSpan = Math.Max(1, maxRaw - minRaw); // the raw/uncompressed lane count
        float marginX = Size.X * 0.05f, marginTop = Size.Y * 0.14f, marginBottom = Size.Y * 0.06f;
        float drawW = Size.X - 2f * marginX;
        float drawH = Size.Y - marginTop - marginBottom;

        // THE flat map: the vanilla map's own orientation — floors run bottom->top (start at the
        // bottom, boss at the top) — with exactly one change: it's compressed vertically so the
        // WHOLE act fits on one screen, every node visible, no scrolling. Lanes run across X,
        // centered; lane spacing is fixed to the raw span (so Compress shows as a narrower map,
        // never a stretched one) and capped to a readable aspect.
        float rowSpacing = drawH / rowSpan;
        float laneSpacing = Math.Min(drawW / refLaneSpan, rowSpacing * 2.4f);
        _nodeRadius = Mathf.Clamp(Math.Min(rowSpacing, laneSpacing) * 0.44f, 9f, 30f);
        float midLane = (minLane + maxLane) * 0.5f;
        float centerX = marginX + drawW * 0.5f;
        _positions.Clear();
        foreach (MiniNode n in model.Nodes.Values)
        {
            _positions[n.Coord] = new Vector2(
                centerX + (LaneOf(n) - midLane) * laneSpacing,
                marginTop + (maxRow - n.Coord.row) * rowSpacing); // row 0 (start) at the bottom
        }
    }

    // Input via the gui_input SIGNAL (override _GuiInput wouldn't fire — same source-gen reason
    // as _Draw). Hover highlights the node under the cursor; a left click on a *travelable* node
    // travels there.
    private void OnGuiInput(InputEvent e)
    {
        if (!ModRuntime.Enabled) return;
        try { HandleGuiInput(e); }
        catch (Exception ex) { ModRuntime.Disable(nameof(MiniMapScreen) + ".input", ex); }
    }

    private void HandleGuiInput(InputEvent e)
    {
        if (_model == null)
            return;
        if (e is InputEventMouseMotion mm)
        {
            MapCoord? was = _hovered;
            _hovered = NodeAt(mm.Position);
            MouseDefaultCursorShape = _hovered is MapCoord h && IsTravelable(h)
                ? CursorShape.PointingHand : CursorShape.Arrow;
            bool changed = (was is null) != (_hovered is null)
                || (was is MapCoord w && _hovered is MapCoord hh && (w.col != hh.col || w.row != hh.row));
            if (changed)
                QueueRedraw();
        }
        else if (e is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            // Vanilla presses DOWN (0.9x squash, NMapPoint.DownScale) and travels on RELEASE
            // over the same node (NButton.OnRelease) — not on press.
            if (mb.Pressed)
            {
                _pressed = NodeAt(mb.Position) is MapCoord c && IsTravelable(c) ? c : null;
                if (_pressed is not null) QueueRedraw();
            }
            else
            {
                if (_pressed is MapCoord p && NodeAt(mb.Position) is MapCoord r
                    && r.col == p.col && r.row == p.row && IsTravelable(r))
                    _onTravel?.Invoke(r);
                _pressed = null;
            }
        }
    }

    private void OnDraw()
    {
        if (!ModRuntime.Enabled) return;
        try { Render(); }
        catch (Exception ex) { ModRuntime.Disable(nameof(MiniMapScreen) + ".draw", ex); }
    }

    private void Render()
    {
        MiniMapModel? model = _model;
        Vector2 size = Size;
        GameStyle.EnsureLoaded();
        Font font = GameStyle.Font ?? GetThemeDefaultFont(); // the game's Kreon UI font
        if (_logDrawOnce)
        {
            Log.Info($"[FlatMap] minimap _Draw size={size} nodes={model?.Nodes.Count ?? -1}");
            _logDrawOnce = false;
        }
        if (model == null)
            return;

        // Opaque backdrop (drawn ALWAYS, before any early-out) hides the real map completely.
        // The act's OWN map background colour (Act.MapBgColor), so the game's icon art sits on
        // exactly the surface it was drawn for.
        Color bg = model.MapBg == default ? new Color(0.05f, 0.06f, 0.09f) : model.MapBg;
        DrawRect(new Rect2(Vector2.Zero, size), new Color(bg.R, bg.G, bg.B, 1f));

        if (model.Nodes.Count == 0)
        {
            DrawString(font, new Vector2(size.X * 0.06f, size.Y * 0.5f),
                "(no map points found)", HorizontalAlignment.Left, -1, 20, new Color(1, 0.6f, 0.6f));
            return;
        }

        UpdateLayoutPositions();

        // Edges first, so nodes draw on top. Traveled->traveled = the path taken (gold); an edge
        // into an unreachable room is faded so it recedes.
        foreach ((MapCoord from, MapCoord to) in model.Edges)
        {
            if (!model.Nodes.TryGetValue(from, out MiniNode a) || !model.Nodes.TryGetValue(to, out MiniNode b))
                continue;
            bool traveled = a.State == MapPointState.Traveled && b.State == MapPointState.Traveled;
            bool dead = IsDead(a) || IsDead(b);
            // Vanilla's connection style: DASHED footpath legs in the act's own path palette
            // (MapTraveledColor for walked legs, MapUntraveledColor for open ones), trimmed so the
            // dashes stop at each node's edge instead of running underneath it.
            Color tp = model.PathTraveled, up = model.PathUntraveled;
            Color edgeColor = traveled ? new Color(tp.R, tp.G, tp.B, 0.95f)
                                       : dead ? new Color(up.R, up.G, up.B, 0.18f) : new Color(up.R, up.G, up.B, 0.80f);
            float edgeWidth = traveled ? 3f : 2.5f;
            Vector2 pa = _positions[from], pb = _positions[to];
            Vector2 dir = (pb - pa).Normalized();
            // Trim to the icons' visual edge (drawn diameter is 2.05 * radius). For vertically-
            // adjacent nodes the remaining gap is only a few px — a connection MUST still show
            // there (its absence is information), so when trimming would erase the leg entirely,
            // draw it untrimmed underneath the icons: the sliver in the gap carries the signal.
            Vector2 ta = pa + dir * (RadiusOf(a) * 1.025f + 2f);
            Vector2 tb = pb - dir * (RadiusOf(b) * 1.025f + 2f);
            if ((tb - ta).Dot(dir) > 4f)
                DrawDashedLine(ta, tb, edgeColor, edgeWidth, 8f, true);
            else
                DrawDashedLine(pa, pb, edgeColor, edgeWidth, 4f, true);
        }

        foreach (MiniNode n in model.Nodes.Values)
        {
            bool isCurrent = model.Current is MapCoord cc && cc.col == n.Coord.col && cc.row == n.Coord.row;
            DrawNode(font, _positions[n.Coord], n, isCurrent);
        }

        DrawInfoPanel(font, size, model);
    }

    // A room is "dead" when you can no longer reach it AND you never visited it — those get greyed.
    private static bool IsDead(MiniNode n) => n.State != MapPointState.Traveled && !n.Reachable;

    // --- THE LOOK (decided 2026-08-01; see docs/vanilla-parity.md §2/§3) ----------------------
    // Vanilla-exact in every dynamic behavior — the tint table (NMapPoint.TargetColor), the
    // frontier pulse, the 1.45x hover swell, the 0.9x press squash, the white outline flash on
    // travelable hover, the legend-row held swell — with exactly TWO colour divergences layered
    // on top:
    //   1. the type-coloured rim: the icon's main body drawn in a bright recognizability colour
    //      where vanilla carves it in the (invisible) map-bg colour, and
    //   2. unreachable-and-unvisited rooms ghosted (vanilla has no reachability concept), their
    //      rims removed — no colour where no further decision will ever be made.
    // The only fixed cue is the red "you are here" marker arrow.

    // High-luminance type palette for rims that must pop on light AND dark act backgrounds.
    private static Color BrightFor(MapPointType t) => t switch
    {
        MapPointType.Monster => new Color(1.00f, 0.45f, 0.40f),
        MapPointType.Elite => new Color(0.95f, 0.45f, 1.00f),
        MapPointType.Boss => new Color(1.00f, 0.35f, 0.35f),
        MapPointType.Shop => new Color(1.00f, 0.84f, 0.00f),     // pure gold (directive 2026-08-01)
        MapPointType.RestSite => new Color(0.45f, 1.00f, 0.55f),
        MapPointType.Treasure => new Color(1.00f, 0.70f, 0.25f),
        // Light blue (directive 2026-08-01; supersedes the 2026-07-30 warm coin-gold rule).
        MapPointType.Unknown => new Color(0.60f, 0.80f, 1.00f),
        MapPointType.Ancient => new Color(0.40f, 1.00f, 0.95f),
        _ => new Color(0.75f, 0.75f, 0.80f),
    };

    private void DrawNode(Font font, Vector2 p, MiniNode n, bool isCurrent)
    {
        float rr = RadiusOf(n); // type-scaled: elites larger, boss largest (hitboxes use this too)
        bool isStart = n.Type == MapPointType.Ancient;
        bool visited = n.State == MapPointState.Traveled;
        bool dead = IsDead(n); // unreachable & never visited
        bool travelEnabled = _model?.TravelEnabled ?? false;
        bool frontier = n.State == MapPointState.Travelable && travelEnabled;

        // VANILLA'S TINT TABLE (NMapPoint.TargetColor — adopted 2026-08-01): the trail
        // (Traveled, incl. the current node) and the next-step nodes (Travelable) carry the
        // FULL natural art; every other unvisited room sits at half alpha
        // (StsColors.halfTransparentWhite). The ink circle alone says "past"; the marker alone
        // says "you are here". Our ghost layer then pushes unreachable-and-unvisited rooms
        // further down AND removes their rim — no colour where no further decision will ever
        // be made (directive 2026-08-01).
        Color bright = BrightFor(n.EffType);
        Color? rim;
        bool thickRim = false;
        Color iconTint;
        if (dead)
        {
            rim = null; // ghosts lose the rim entirely
            iconTint = new Color(0.45f, 0.45f, 0.50f, 0.35f);
        }
        else if (visited || n.State == MapPointState.Travelable)
        {
            rim = bright;
            thickRim = !visited; // "expand the coloured area slightly" applies to live rooms
            iconTint = new Color(1, 1, 1, 1);
        }
        else
        {
            rim = bright;
            thickRim = true;
            iconTint = new Color(1f, 1f, 1f, 0.5f); // vanilla's halfTransparentWhite
        }

        // The boss badge's outline is already massive — a thickened rim turns it into a rough
        // blob. Its size IS its cue.
        if (n.EffType == MapPointType.Boss)
            thickRim = false;

        // THE START is drawn exactly as the vanilla map draws it: the dark ink illustration with
        // its pale outline stroke, enlarged — no invented rings or recolours (2026-07-30).
        if (isStart)
        {
            Color inkArt = _model?.PathTraveled ?? new Color(0.16f, 0.13f, 0.11f);
            thickRim = false;
            rim = new Color(0.96f, 0.95f, 0.90f); // pale stroke, like the original
            iconTint = new Color(inkArt.R, inkArt.G, inkArt.B, 1f);
        }

        // MOTION — vanilla's, exactly (2026-08-01). Frontier nodes pulse on the game's own
        // curve, sin(elapsed*4)*0.25+1.2 (the start pulses gently at ±0.05 around 1, like
        // NAncientMapPoint); the hover swell rises to vanilla's 1.45x in 0.05s and releases
        // over 0.5s (driven by the page tick), dominating the pulse while held; a pressed
        // travelable node squashes to vanilla's 0.9x. Visual only: hitboxes stay at rr.
        float swell = 1f;
        if (frontier)
        {
            float t = Time.GetTicksMsec() / 1000f;
            swell = isStart
                ? Mathf.Sin(t * 4f + PhaseOf(n.Coord)) * 0.05f + 1f
                : Mathf.Sin(t * 4f + PhaseOf(n.Coord)) * 0.25f + 1.2f;
        }
        float hover = _hoverAnim.GetValueOrDefault(n.Coord); // 0..1
        swell = Mathf.Max(swell, 1f + 0.45f * hover);        // vanilla HoverScale = 1.45
        if (_pressed is MapCoord pc && pc.col == n.Coord.col && pc.row == n.Coord.row)
            swell = 0.9f;                                     // vanilla DownScale = 0.9
        float vr = rr * swell;

        // Hovering a TRAVELABLE node flashes its outline white — vanilla's _outlineColor
        // (white, 0.75) — layered on our coloured rim exactly where vanilla layers it on the
        // bg-coloured outline.
        if (frontier && hover > 0f && rim is Color rc)
            rim = rc.Lerp(new Color(1f, 1f, 1f), hover * 0.75f);

        // THE TAKEN PATH — vanilla's ensō brush (NMapCircleVfx / map_circle_4). Drawn BEFORE
        // the icon so the room type stays visible inside the hollow swirl; sized from the
        // BASE radius (not the hover swell) so the circle stays put while the icon pulses.
        if (visited && !isStart)
            DrawInkCircle(p, rr, n.Coord);

        DrawNodeShape(font, p, vr, n.EffType, n.Icon, n.Outline, rim, thickRim, iconTint);

        // The one fixed cue: the game's red "you are here" marker arrow.
        if (isCurrent)
            DrawCurrentMarker(p, vr);
    }

    // A node's pulse phase: the stable per-coord phase, unless an unfocus reset (vanilla's
    // _elapsedTime = 5π/4) has stamped a replacement.
    private float PhaseOf(MapCoord coord) =>
        _pulsePhase.TryGetValue(coord, out float p) ? p : PulsePhase(coord);

    private static Color WithA(Color c, float a) => new(c.R, c.G, c.B, a);

    // Stable independent phase, equivalent in effect to vanilla initializing each point's elapsed
    // pulse time from Rng.Chaotic. Only phase modulo Tau matters to the sine.
    private static float PulsePhase(MapCoord coord)
    {
        uint h = unchecked((uint)(coord.row * 0x45d9f3b + coord.col * 0x119de1f3));
        h ^= h >> 16;
        return (h / (float)uint.MaxValue) * Mathf.Tau;
    }

    // Vanilla's Japanese ensō brush (NMapCircleVfx). Scene facts from map_circle_vfx.tscn:
    //   - Control is 200×200; TextureRect fills it; runtime Scale is 0.85..0.90 → ~170–180px
    //   - TextureRect.modulate = Color(0.141, 0.122, 0.102) — FIXED dark ink (not PathTraveled)
    //   - Atlas frames are WHITE silhouettes; the scene modulate is what makes them ink-brown
    //   - Normal map icon is 92×92, so the swirl is ~1.9× the icon — larger, hollow center
    // Drawing the icon ON TOP (caller) keeps the past room type readable inside the swirl.
    private static Texture2D? _inkCircle;
    // Exact TextureRect modulate from map_circle_vfx.tscn (RGB); alpha applied at draw time.
    private static readonly Color InkCircleTint = new(0.141176f, 0.121569f, 0.101961f);

    private void DrawInkCircle(Vector2 p, float baseRadius, MapCoord coord)
    {
        Color ink = new(InkCircleTint.R, InkCircleTint.G, InkCircleTint.B, 0.95f);
        _inkCircle ??= MiniMapController.LoadTexture(
            "res://images/atlases/compressed.sprites/map/map_circle_4.tres");
        // Icon draw diameter is baseRadius * 2.05; vanilla circle/icon ≈ 200/92 ≈ 2.17, then
        // the scene's 0.85..0.90 scale jitter. Match that so the brush sits clearly outside.
        float iconD = baseRadius * 2.05f;
        int h = coord.row * 131 + coord.col * 977;
        float rot = (h % 360) * (Mathf.Tau / 360f);
        float sc = 0.85f + 0.05f * ((h % 97) / 97f); // vanilla NextFloat(0.85, 0.90)
        float d = iconD * (200f / 92f) * sc;
        if (_inkCircle == null || !GodotObject.IsInstanceValid(_inkCircle))
        {
            // Fallback if the texture is missing: open brush arc + chevron in the same ink.
            float r = d * 0.5f;
            DrawArc(p, r, 0.35f, Mathf.Tau - 0.35f, 40, ink, 3.5f, true);
            Vector2 tip = p + new Vector2(r, -r * 0.18f);
            DrawPolyline(new[]
            {
                tip + new Vector2(-5f, -4f),
                tip,
                tip + new Vector2(-5f, 4f),
            }, ink, 3.5f, true);
            return;
        }
        DrawSetTransform(p, rot, Vector2.One);
        DrawTextureRect(_inkCircle, new Rect2(-d * 0.5f, -d * 0.5f, d, d), false, ink);
        DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
    }

    // ONE node's visual: the icon's MAIN BODY — the game's `_outline` texture, a dilated solid
    // mask of the icon shape — in the rim colour (thickened via offset passes for live rooms:
    // "expand the coloured area slightly", 2026-07-30), then the game's icon art with its NATURAL
    // interior colours. The node's outline is therefore always the actual item shape, never a
    // circle. No icon at all -> colour disc + glyph.
    private void DrawNodeShape(Font font, Vector2 p, float rr, MapPointType type,
        Texture2D? icon, Texture2D? outline, Color? rim, bool thickRim, Color iconTint)
    {
        if (icon != null && GodotObject.IsInstanceValid(icon))
        {
            // 2.05: as large as the icon can draw while still leaving a visible sliver of path
            // between VERTICALLY-ADJACENT nodes — at 2.4 stacked nodes touched, and a connected
            // pair was indistinguishable from an unconnected one (playtest bug, 2026-07-28).
            float d = rr * 2.05f;
            var rect = new Rect2(p.X - d * 0.5f, p.Y - d * 0.5f, d, d);
            if (outline != null && GodotObject.IsInstanceValid(outline) && rim is Color rc)
            {
                Color body = WithA(rc.Lightened(0.15f), Mathf.Min(1f, iconTint.A + 0.15f));
                if (thickRim)
                    for (int i = 0; i < 6; i++)
                    {
                        float a = Mathf.Tau * i / 6f;
                        var off = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 2.4f;
                        DrawTextureRect(outline, new Rect2(rect.Position + off, rect.Size), false, body);
                    }
                DrawTextureRect(outline, rect, false, body);
            }
            DrawTextureRect(icon, rect, false, iconTint);
            return;
        }

        Color fall = rim ?? new Color(0.50f, 0.50f, 0.55f);
        DrawCircle(p, rr, WithA(fall, iconTint.A));
        DrawArc(p, rr, 0f, Mathf.Tau, 32, new Color(0, 0, 0, 0.5f * iconTint.A), 1.5f, true);
        DrawGlyph(font, p, rr, type, new Color(1, 1, 1, Mathf.Max(0.6f, iconTint.A)));
    }

    private void DrawCurrentMarker(Vector2 p, float r)
    {
        Texture2D? marker = _model?.CurrentMarker;
        if (marker != null && GodotObject.IsInstanceValid(marker) && marker.GetWidth() > 0)
        {
            // From the SIDE, not above (user directive 2026-07-29): drawn above at 1.5×r the
            // arrow covered the node one floor up and the current node itself. The art points
            // down, so -90° turns it to point right, at the node from the left.
            float w = r * 1.0f;
            float h = w * (marker.GetHeight() / (float)marker.GetWidth());
            var center = new Vector2(p.X - r - 4f - h * 0.5f, p.Y);
            DrawSetTransform(center, -Mathf.Pi / 2f, Vector2.One);
            DrawTextureRect(marker, new Rect2(-w * 0.5f, -h * 0.5f, w, h), false, new Color(1, 1, 1, 1));
            DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
            return;
        }
        float s = r * 0.6f, tx = p.X - r - 3f;
        var red = new Color(0.90f, 0.18f, 0.14f, 1f);
        DrawColoredPolygon(new[] { new Vector2(tx - s * 1.3f, p.Y - s), new Vector2(tx - s * 1.3f, p.Y + s), new Vector2(tx, p.Y) }, red);
    }

    private void DrawGlyph(Font font, Vector2 p, float rr, MapPointType type, Color iconMod)
    {
        string glyph = GlyphFor(type);
        if (glyph.Length > 0)
        {
            int gfs = (int)(rr * 1.15f);
            Vector2 ts = font.GetStringSize(glyph, HorizontalAlignment.Left, -1, gfs);
            DrawString(font, new Vector2(p.X - ts.X * 0.5f, p.Y + gfs * 0.35f), glyph,
                HorizontalAlignment.Left, -1, gfs, iconMod);
        }
    }

    // Bottom-right: just the act name, quiet and right-aligned. The legend is the game's OWN
    // Legend panel, borrowed onto this page while it's open (see MiniMapController.BorrowLegend).
    private void DrawInfoPanel(Font font, Vector2 size, MiniMapModel model)
    {
        string actLine = string.IsNullOrEmpty(model.ActName)
            ? $"Act {model.ActIndex + 1}"
            : $"Act {model.ActIndex + 1} — {model.ActName}";
        RightLine(font, size.X * 0.96f, size.Y * 0.93f, actLine, 22, Ink(0.85f));
    }

    private void RightLine(Font font, float right, float y, string text, int fontSize, Color color)
    {
        float w = font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize).X;
        DrawString(font, new Vector2(right - w, y), text, HorizontalAlignment.Left, -1, fontSize, color);
    }

    // FlatMap's own palette (intentionally not the game's art) — picked for at-a-glance
    // contrast between room types.
    private static Color ColorFor(MapPointType t) => t switch
    {
        MapPointType.Monster => new Color(0.85f, 0.30f, 0.28f),  // red
        MapPointType.Elite => new Color(0.72f, 0.28f, 0.80f),    // purple
        MapPointType.Boss => new Color(0.85f, 0.20f, 0.22f),     // red (real boss art drawn on top when available)
        MapPointType.Shop => new Color(1.00f, 0.84f, 0.00f),     // pure gold (store, 2026-08-01)
        MapPointType.RestSite => new Color(0.35f, 0.75f, 0.42f), // green (camp)
        MapPointType.Treasure => new Color(0.95f, 0.55f, 0.18f), // orange (treasure box)
        MapPointType.Unknown => new Color(0.60f, 0.80f, 1.00f),  // light blue (?, 2026-08-01)
        MapPointType.Ancient => new Color(0.25f, 0.78f, 0.74f),  // teal (start)
        _ => new Color(0.40f, 0.42f, 0.48f),
    };

    private static string GlyphFor(MapPointType t) => t switch
    {
        MapPointType.Monster => "M",
        MapPointType.Elite => "E",
        MapPointType.Boss => "B",
        MapPointType.Shop => "$",
        MapPointType.RestSite => "R",
        MapPointType.Treasure => "T",
        MapPointType.Unknown => "?",
        MapPointType.Ancient => "A",
        _ => "",
    };
}

// Drive the minimap each frame the map screen processes. _Process isn't focus-gated (unlike
// the arrow-key scroll path), so the toggle works regardless of what has keyboard focus.
[HarmonyPatch(typeof(NMapScreen), "_Process")]
internal static class NMapScreen_Process_Patch
{
    private static void Postfix(NMapScreen __instance)
    {
        if (!ModRuntime.Enabled) return;
        try { MiniMapController.Tick(__instance); }
        catch (Exception ex) { ModRuntime.Disable(nameof(NMapScreen_Process_Patch), ex); }
    }
}

// Whenever the map opens (map room, top-bar button, or our global M key), show the configured
// style: OnMapOpened requests the flat page on the next tick if "Flat map" is checked.
// The map has TWO co-equal modes: classic and flat. This is the single place the mode is honored —
// whenever ANYTHING opens the map (a map room, the top-bar button, our M key), if flat mode is on we
// render the flat page INSTEAD and skip the classic map entirely (it never opens, so it can never
// show or be fallen back to). In classic mode this patch does nothing and the game opens normally.
[HarmonyPatch(typeof(NMapScreen), "Open")]
internal static class NMapScreen_Open_Patch
{
    private static bool Prefix(NMapScreen __instance, ref NMapScreen __result)
    {
        if (!ModRuntime.Enabled) return true;
        try
        {
            // Swallow the map room's synchronous ReopenMap when WE just deliberately closed this frame,
            // so a close actually stays closed instead of instantly bouncing back open.
            if (MiniMapController.SuppressReopenThisFrame())
            {
                Log.Info("[FlatMap] map open suppressed (deliberate close this frame)");
                __result = __instance;
                return false;
            }
            if (!FlatMapConfig.PreferFlatMap)
            {
                Log.Info("[FlatMap] classic map open");
                return true;
            }
            // The standard map controls must always toggle. The top-bar map button (next to the
            // deck) calls Open() unconditionally — the classic screen's IsOpen stays false in flat
            // mode, so the game thinks the map is closed. If our flat page IS the current capstone,
            // treat this Open() as the toggle-off it was meant to be.
            if (MiniMapController.FlatShown())
            {
                Log.Info("[FlatMap] map open while flat page shown -> toggle off");
                MiniMapController.CloseMapCompletely();
                __result = __instance;
                return false;
            }
            MiniMapController.OpenFlatFromHook(__instance);
            __result = __instance;
            return false;
        }
        catch (Exception ex)
        {
            ModRuntime.Disable(nameof(NMapScreen_Open_Patch), ex);
            return true;
        }
    }
}

// While the flat page is showing, keep it in sync if the game flips travelability (e.g. a map room
// enables travel just after we opened, or travel disables it) — rebuild + redraw so the "you can move
// here" highlights are always correct.
[HarmonyPatch(typeof(NMapScreen), "SetTravelEnabled")]
internal static class NMapScreen_SetTravelEnabled_Patch
{
    private static void Postfix()
    {
        if (!ModRuntime.Enabled) return;
        try { MiniMapController.RefreshFlat(); }
        catch (Exception ex) { ModRuntime.Disable(nameof(NMapScreen_SetTravelEnabled_Patch), ex); }
    }
}

// THE map key. The game hard-binds the map to a key (M by default) and re-broadcasts that key as the
// `mega_view_map` action here, in NInputManager.ProcessHotkeyInput — the one choke point that
// fires in EVERY context (combat, map room, deck view), regardless of the top-bar button's state.
// We intercept the map key (honoring rebinds) and our F key here, route to our own toggle, and skip
// the original so the vanilla map action is NEVER broadcast — one handler, no double-toggle.
[HarmonyPatch(typeof(NInputManager), "ProcessHotkeyInput")]
internal static class NInputManager_ShortcutKey_Patch
{
    // The method's first parameter is the base InputEvent (see HookCatalog): declare it as that
    // exact type and pattern-match, so Harmony never has to emit a downcast that would throw on a
    // non-key event flowing through this path.
    private static bool Prefix(InputEvent __0)
    {
        if (!ModRuntime.Enabled || __0 is not InputEventKey k || k.IsEcho() || !k.IsPressed())
            return true;
        try
        {
            NInputManager? mgr = NInputManager.Instance;
            if (mgr == null) return true;
            MiniMapController.AuditKeysOnce(mgr);
            if (k.Keycode == mgr.GetCurrentHotkey(MegaInput.viewMap))
            {
                MiniMapController.OnMapKey();
                return false;
            }
            if (k.Keycode == FlatMapMod.ToggleMiniMapKey && MiniMapController.MapShown())
            {
                MiniMapController.OnFlipKey();
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            ModRuntime.Disable(nameof(NInputManager_ShortcutKey_Patch), ex);
            return true;
        }
    }
}
