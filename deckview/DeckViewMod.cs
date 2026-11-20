using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;

namespace DeckView;

// DeckView — Slay the Spire 2 port of the STS1 "make deck-view cards smaller" mod.
// (The whole-act map view that older DeckView builds bundled is now the separate FlatMap mod.)
//
// STS2's card grid (NCardGrid) is already responsive: the column count and scroll
// bounds are computed from the per-card layout size and padding. So we only have to
// make the cards smaller and everything reflows to fit more per screen.
//
// Two coordinated levers, kept proportional so layout and rendering stay in sync:
//   1. NCardGrid._cardSize    — the layout cell size (drives Columns, positions, scroll).
//   2. NCardHolder.SmallScale — the *rendered* scale of each grid-owned card holder.
//
// Scoping uses a strict NCardGrid allowlist (not a screen denylist): only holders created
// inside NCardGrid.InitGrid or parented under an NCardGrid are shrunk, so combat hands,
// inspect popups, rewards, unlocks, shops, and any future/modded fixed-row screens stay
// untouched at full size automatically.
//
// On mini grids in pointer mode, GridHoverGate is the single owner of card hover — rogue
// mouse-enter/exit signals from enlarged overlapping hitboxes are suppressed so hover never
// oscillates or opens stuck-big. Controller/keyboard navigation runs 100% vanilla.
[ModInitializer(nameof(Init))]
public static class DeckViewMod
{
    // 0.6 => cards render at 60% of the vanilla deck-view size, matching the STS1 mod.
    public const float CardScaleFactor = 0.6f;

    // Vanilla NCardGrid.CardPadding is a constant 40f. Tighter spacing packs in more
    // columns/rows. Set equal to 40f to keep vanilla spacing.
    public const float CardPadding = 24f;

    // Hotkey to toggle mini <-> large card mode while a card grid screen is active.
    public const Key ToggleDeckModeKey = Key.T;

    // DeckView was built and tested against this game version. On another build, every private
    // hook and every game member our compiled code calls is preflighted before Harmony changes
    // anything. Missing members disable the mod and leave the game's UI untouched.
    public const string TestedGameVersion = "v0.113.1";

    public static void Init()
    {
        if (!ModRuntime.TryEnable(typeof(DeckViewMod).Assembly))
            return;

        string? gameVersion = ReleaseInfoManager.Instance.ReleaseInfo?.Version;
        string versionNote = gameVersion == TestedGameVersion
            ? ""
            : $" — NOTE: game '{gameVersion ?? "unknown"}' is not the tested {TestedGameVersion}; re-verify";
        Log.Info($"[DeckView] loaded — card scale x{CardScaleFactor}, padding {CardPadding}px{versionNote}");
    }
}

// --- Allowlist scoping: only NCardGrid-owned holders are ever shrunk ---------------------
internal static class GridOwnership
{
    // Depth counter around NCardGrid.InitGrid() so NGridCardHolder.Create() and the initial
    // Scale = SmallScale assignment (which happens one line before AddChildSafely) know the
    // holder belongs to an NCardGrid without needing any screen denylist.
    [ThreadStatic]
    private static int _initDepth;

    internal static void EnterGridInit() => _initDepth++;
    internal static void ExitGridInit() => _initDepth = Math.Max(0, _initDepth - 1);

    internal static bool IsGridOwned(Node node)
    {
        if (_initDepth > 0)
            return true;
        for (Node? p = node.GetParent(); p != null; p = p.GetParent())
        {
            if (p is NCardGrid)
                return true;
        }
        return false;
    }
}

[HarmonyPatch(typeof(NCardGrid), "InitGrid", new Type[0])]
internal static class NCardGrid_InitGrid_Patch
{
    private static void Prefix()
    {
        if (ModRuntime.Enabled)
            GridOwnership.EnterGridInit();
    }

    private static void Finalizer() => GridOwnership.ExitGridInit();
}

// --- Mini/large toggle: persistent state + guarded hotkey + live swap --------------------
internal static class DeckModeController
{
    private static readonly FieldInfo CardSizeField = Reflect.Field(typeof(NCardGrid), "_cardSize");
    private static readonly FieldInfo NeedsReinitField = Reflect.Field(typeof(NCardGrid), "_needsReinit");
    private static readonly FieldInfo CardsAnimatingOutField = Reflect.Field(typeof(NCardGrid), "_cardsAnimatingOutForSetCards");
    private static readonly MethodInfo CanScrollGetter = Reflect.PropertyGetter(typeof(NCardGrid), "CanScroll");
    private static readonly MethodInfo InitGridMethod = Reflect.Method(typeof(NCardGrid), "InitGrid", Type.EmptyTypes);

    // Live grids -> vanilla (un-shrunk) _cardSize captured at ConnectSignals time.
    private static readonly Dictionary<NCardGrid, Vector2> _grids = new();

    private static bool _keyWasDown;
    private static ulong _lastPollFrame = ulong.MaxValue;

    static DeckModeController() => ModRuntime.Disabled += RestoreVanilla;

    internal static bool MiniEnabled => DeckViewConfig.MiniDeck;

    internal static void Register(NCardGrid grid, Vector2 vanillaCardSize) => _grids[grid] = vanillaCardSize;

    internal static void Unregister(NCardGrid grid) => _grids.Remove(grid);

    // Edge-detected hotkey check, run at most once per frame for the currently active visible grid.
    // Ignores keystrokes with modifiers, while typing in a text field (e.g. Card Library search),
    // during transitions, or while a modal/inspect screen sits on top of the grid.
    internal static void PollHotkey(NCardGrid grid)
    {
        ulong frame = Engine.GetProcessFrames();
        if (frame == _lastPollFrame)
            return;

        if (!IsGridActiveScreen(grid))
            return;

        _lastPollFrame = frame;
        bool down = Input.IsKeyPressed(DeckViewMod.ToggleDeckModeKey);
        bool rising = down && !_keyWasDown;
        _keyWasDown = down;

        if (!rising)
            return;
        if (!NGame.IsGameFocusedWindow() || NGame.Instance?.Transition?.InTransition == true)
            return;
        if (Input.IsKeyPressed(Key.Ctrl) || Input.IsKeyPressed(Key.Alt)
            || Input.IsKeyPressed(Key.Shift) || Input.IsKeyPressed(Key.Meta))
            return;

        Control? focused = grid.GetViewport()?.GuiGetFocusOwner();
        if (focused is LineEdit or TextEdit)
            return;

        Toggle();
    }

    // Rebuilds a visible grid that was flagged _needsReinit while CanScroll == false
    // (vanilla NCardGrid._Process skips _needsReinit whenever CanScroll is false, e.g. while an
    // upgrade confirmation preview is open over the grid).
    internal static void FlushLockedReinit(NCardGrid grid)
    {
        if ((bool)NeedsReinitField.GetValue(grid)!
            && !(bool)CanScrollGetter.Invoke(grid, null)!
            && !(bool)CardsAnimatingOutField.GetValue(grid)!)
        {
            InitGridMethod.Invoke(grid, null);
        }
    }

    internal static void Toggle() => SetMini(!DeckViewConfig.MiniDeck);

    internal static void SetMini(bool on)
    {
        if (DeckViewConfig.MiniDeck == on)
            return;
        DeckViewConfig.MiniDeck = on;
        MiniCardsToggleRegistry.SyncAll(on);
        float factor = MiniEnabled ? DeckViewMod.CardScaleFactor : 1f;
        Log.Info($"[DeckView] {(MiniEnabled ? "mini" : "large")} deck mode");

        foreach (KeyValuePair<NCardGrid, Vector2> kv in _grids.ToArray())
        {
            NCardGrid grid = kv.Key;
            if (!GodotObject.IsInstanceValid(grid))
            {
                _grids.Remove(grid);
                continue;
            }
            GridHoverGate.ResetGridState(grid);
            CardSizeField.SetValue(grid, kv.Value * factor);
            NeedsReinitField.SetValue(grid, true);
        }
    }

    private static bool IsGridActiveScreen(NCardGrid grid)
    {
        IScreenContext? active = ActiveScreenContext.Instance?.GetCurrentScreen();
        return active is Node screenNode && (screenNode == grid || screenNode.IsAncestorOf(grid));
    }

    private static void RestoreVanilla()
    {
        foreach (KeyValuePair<NCardGrid, Vector2> kv in _grids.ToArray())
        {
            if (!GodotObject.IsInstanceValid(kv.Key)) continue;
            GridHoverGate.ResetGridState(kv.Key);
            CardSizeField.SetValue(kv.Key, kv.Value);
            NeedsReinitField.SetValue(kv.Key, true);
        }
        _grids.Clear();
    }
}

[HarmonyPatch(typeof(NCardGrid), "ConnectSignals")]
internal static class NCardGrid_ConnectSignals_Patch
{
    private static void Postfix(NCardGrid __instance, ref Vector2 ____cardSize)
    {
        if (!ModRuntime.Enabled) return;
        Vector2 vanilla = ____cardSize;
        try
        {
            DeckModeController.Register(__instance, vanilla);
            Dbg.Rearm();
            if (DeckModeController.MiniEnabled)
                ____cardSize *= DeckViewMod.CardScaleFactor;
            Log.Info($"[DeckView] grid connected: vanilla cardSize={vanilla}, mini={DeckModeController.MiniEnabled}, " +
                     $"final cardSize={____cardSize}");
        }
        catch (Exception ex)
        {
            ____cardSize = vanilla;
            ModRuntime.Disable(nameof(NCardGrid_ConnectSignals_Patch), ex);
        }
    }
}

[HarmonyPatch(typeof(NCardGrid), "_ExitTree")]
internal static class NCardGrid_ExitTree_Patch
{
    private static void Postfix(NCardGrid __instance)
    {
        if (!ModRuntime.Enabled) return;
        try
        {
            DeckModeController.Unregister(__instance);
            GridHoverGate.ResetGridState(__instance);
        }
        catch (Exception ex) { ModRuntime.Disable(nameof(NCardGrid_ExitTree_Patch), ex); }
    }
}

[HarmonyPatch(typeof(NCardHolder), "SmallScale", MethodType.Getter)]
internal static class NCardHolder_SmallScale_Patch
{
    private static void Postfix(NCardHolder __instance, ref Vector2 __result)
    {
        if (!ModRuntime.Enabled) return;
        Vector2 vanilla = __result;
        try
        {
            if (DeckModeController.MiniEnabled && __instance is NGridCardHolder && GridOwnership.IsGridOwned(__instance))
            {
                __result *= DeckViewMod.CardScaleFactor;
                Dbg.Once("smallscale", $"SmallScale shrink active (x{DeckViewMod.CardScaleFactor}) -> {__result}");
            }
        }
        catch (Exception ex)
        {
            __result = vanilla;
            ModRuntime.Disable(nameof(NCardHolder_SmallScale_Patch), ex);
        }
    }
}

[HarmonyPatch(typeof(NCardGrid), "CardPadding", MethodType.Getter)]
internal static class NCardGrid_CardPadding_Patch
{
    private static void Postfix(ref float __result)
    {
        if (!ModRuntime.Enabled) return;
        float vanilla = __result;
        try
        {
            if (DeckModeController.MiniEnabled)
                __result = DeckViewMod.CardPadding;
        }
        catch (Exception ex)
        {
            __result = vanilla;
            ModRuntime.Disable(nameof(NCardGrid_CardPadding_Patch), ex);
        }
    }
}

// --- Visible "Mini-cards" toggle across every shrunk card-grid screen --------------------
//
// Attached to:
//   * NCardsViewScreen (deck view & simple cards view) — stacked cleanly above _showUpgrades
//   * NCardPileScreen (draw / discard / exhaust piles) — anchored cleanly bottom-left above BackButton
//   * NCardLibrary — added to the filter tickbox cluster (or placed below _viewMultiplayerCards)
//   * NCardGridSelectionScreen (all 6 deck/pile card-select screens) — anchored bottom-left &
//     registered with _peekButton so Peek hides it alongside the grid.
//
// Positions recompute on viewport/screen resize, and focus-neighbor cleanup uses one static list
// instead of per-open ModRuntime.Disabled lambda subscriptions (no closure leak).
internal static class MiniCardsToggleRegistry
{
    private const string AddedMeta = "minicards_toggle";
    private const float EdgeMargin = 24f;
    private const float StackGap = 10f;

    private static readonly List<ToggleSwitch> _toggles = new();
    private static readonly List<(Control Control, NodePath PreviousTop)> _focusRestores = new();

    static MiniCardsToggleRegistry() => ModRuntime.Disabled += RestoreFocusNeighbors;

    internal static bool TryMarkHost(Control host)
    {
        if (host.HasMeta(AddedMeta))
            return false;
        host.SetMeta(AddedMeta, true);
        return true;
    }

    internal static ToggleSwitch CreateToggle()
    {
        var toggle = new ToggleSwitch("Mini-cards", DeckModeController.MiniEnabled, OnMiniToggled)
        {
            Name = "MiniCardsToggle",
        };
        _toggles.RemoveAll(t => !GodotObject.IsInstanceValid(t));
        _toggles.Add(toggle);
        toggle.Connect(Node.SignalName.TreeExiting, Callable.From(() => _toggles.Remove(toggle)));
        return toggle;
    }

    internal static void SyncAll(bool on)
    {
        _toggles.RemoveAll(t => !GodotObject.IsInstanceValid(t));
        foreach (ToggleSwitch t in _toggles)
            t.SetOn(on);
    }

    internal static void AnchorAboveControl(Control host, ToggleSwitch toggle, Control? anchor, bool wireFocus)
    {
        host.AddChild(toggle);
        void Reposition()
        {
            if (!GodotObject.IsInstanceValid(host) || !GodotObject.IsInstanceValid(toggle) || !host.IsInsideTree())
                return;
            if (anchor != null && GodotObject.IsInstanceValid(anchor) && anchor.IsVisibleInTree())
            {
                Vector2 anchorLocal = host.GetGlobalTransformWithCanvas().AffineInverse() * anchor.GlobalPosition;
                float maxX = Mathf.Max(EdgeMargin, host.Size.X - toggle.Size.X - EdgeMargin);
                float maxY = Mathf.Max(EdgeMargin, host.Size.Y - toggle.Size.Y - EdgeMargin);
                float x = Mathf.Clamp(anchorLocal.X, EdgeMargin, maxX);
                float y = Mathf.Clamp(anchorLocal.Y - toggle.Size.Y - StackGap, EdgeMargin, maxY);
                toggle.Position = new Vector2(x, y);
            }
            else
            {
                toggle.Position = new Vector2(EdgeMargin, Mathf.Max(EdgeMargin, host.Size.Y - toggle.Size.Y - EdgeMargin));
            }
        }

        if (wireFocus && anchor != null && GodotObject.IsInstanceValid(anchor))
        {
            NodePath previousTop = anchor.FocusNeighborTop;
            toggle.FocusNeighborBottom = toggle.GetPathTo(anchor);
            anchor.FocusNeighborTop = anchor.GetPathTo(toggle);
            _focusRestores.RemoveAll(r => !GodotObject.IsInstanceValid(r.Control));
            _focusRestores.Add((anchor, previousTop));
        }

        Reposition();
        Callable.From(Reposition).CallDeferred();
        host.Connect(Control.SignalName.Resized, Callable.From(Reposition));
    }

    internal static void AttachInLibrary(Control host, ToggleSwitch toggle, Control? siblingTickbox)
    {
        if (siblingTickbox != null && GodotObject.IsInstanceValid(siblingTickbox)
            && siblingTickbox.GetParent() is BoxContainer box)
        {
            box.AddChild(toggle);
            return;
        }

        host.AddChild(toggle);
        void Reposition()
        {
            if (!GodotObject.IsInstanceValid(host) || !GodotObject.IsInstanceValid(toggle) || !host.IsInsideTree())
                return;
            if (siblingTickbox != null && GodotObject.IsInstanceValid(siblingTickbox) && siblingTickbox.IsVisibleInTree())
            {
                Vector2 local = host.GetGlobalTransformWithCanvas().AffineInverse() * siblingTickbox.GlobalPosition;
                float y = local.Y + siblingTickbox.Size.Y + StackGap;
                if (y + toggle.Size.Y > host.Size.Y - EdgeMargin)
                    y = Mathf.Max(EdgeMargin, local.Y - toggle.Size.Y - StackGap);
                toggle.Position = new Vector2(Mathf.Max(EdgeMargin, local.X), y);
            }
            else
            {
                toggle.Position = new Vector2(EdgeMargin, Mathf.Max(EdgeMargin, host.Size.Y - toggle.Size.Y - EdgeMargin));
            }
        }

        Reposition();
        Callable.From(Reposition).CallDeferred();
        host.Connect(Control.SignalName.Resized, Callable.From(Reposition));
    }

    private static void OnMiniToggled(bool pressed) => DeckModeController.SetMini(pressed);

    private static void RestoreFocusNeighbors()
    {
        foreach ((Control control, NodePath prevTop) in _focusRestores)
        {
            if (GodotObject.IsInstanceValid(control))
                control.FocusNeighborTop = prevTop;
        }
        _focusRestores.Clear();
    }
}

[HarmonyPatch(typeof(NCardsViewScreen), "ConnectSignals")]
internal static class NCardsViewScreen_Toggle_Patch
{
    private static readonly FieldInfo ShowUpgradesField = Reflect.Field(typeof(NCardsViewScreen), "_showUpgrades");

    private static void Postfix(NCardsViewScreen __instance)
    {
        if (!ModRuntime.Enabled) return;
        try
        {
            if (!MiniCardsToggleRegistry.TryMarkHost(__instance))
                return;

            var upgrades = ShowUpgradesField.GetValue(__instance) as Control;
            Control? label = __instance.GetNodeOrNull("%ViewUpgradesLabel") as Control;
            Control? visuals = __instance.GetNodeOrNull("%TickboxVisuals") as Control;
            GameStyle.ConfigureToggleMetrics(visuals, label);

            ToggleSwitch toggle = MiniCardsToggleRegistry.CreateToggle();
            MiniCardsToggleRegistry.AnchorAboveControl(__instance, toggle, upgrades, wireFocus: true);
            Log.Info($"[DeckView] mini-cards toggle on {__instance.GetType().Name} " +
                     $"font={GameStyle.ToggleFontSize}px box={GameStyle.ToggleBoxSize:0.#}px");
        }
        catch (Exception ex)
        {
            ModRuntime.Disable(nameof(NCardsViewScreen_Toggle_Patch), ex);
        }
    }
}

[HarmonyPatch(typeof(NCardPileScreen), "_Ready")]
internal static class NCardPileScreen_Toggle_Patch
{
    private static readonly FieldInfo BackButtonField = Reflect.Field(typeof(NCardPileScreen), "_backButton");

    private static void Postfix(NCardPileScreen __instance)
    {
        if (!ModRuntime.Enabled) return;
        try
        {
            if (!MiniCardsToggleRegistry.TryMarkHost(__instance))
                return;
            var backButton = BackButtonField.GetValue(__instance) as Control;
            ToggleSwitch toggle = MiniCardsToggleRegistry.CreateToggle();
            MiniCardsToggleRegistry.AnchorAboveControl(__instance, toggle, backButton, wireFocus: false);
        }
        catch (Exception ex)
        {
            ModRuntime.Disable(nameof(NCardPileScreen_Toggle_Patch), ex);
        }
    }
}

[HarmonyPatch(typeof(NCardLibrary), "_Ready")]
internal static class NCardLibrary_Toggle_Patch
{
    private static readonly FieldInfo ViewUpgradesField = Reflect.Field(typeof(NCardLibrary), "_viewUpgrades");
    private static readonly FieldInfo ViewMultiplayerField = Reflect.Field(typeof(NCardLibrary), "_viewMultiplayerCards");

    private static void Postfix(NCardLibrary __instance)
    {
        if (!ModRuntime.Enabled) return;
        try
        {
            if (!MiniCardsToggleRegistry.TryMarkHost(__instance))
                return;

            var upgrades = ViewUpgradesField.GetValue(__instance) as Control;
            var multiplayer = ViewMultiplayerField.GetValue(__instance) as Control;
            Control? visuals = upgrades?.GetNodeOrNull("%TickboxVisuals") as Control;
            Control? label = upgrades?.GetNodeOrNull<Control>("%Label");
            if (visuals != null || label != null)
                GameStyle.ConfigureToggleMetrics(visuals, label);

            ToggleSwitch toggle = MiniCardsToggleRegistry.CreateToggle();
            MiniCardsToggleRegistry.AttachInLibrary(__instance, toggle, multiplayer ?? upgrades);
        }
        catch (Exception ex)
        {
            ModRuntime.Disable(nameof(NCardLibrary_Toggle_Patch), ex);
        }
    }
}

[HarmonyPatch(typeof(NCardGridSelectionScreen), "ConnectSignalsAndInitGrid")]
internal static class NCardGridSelectionScreen_Toggle_Patch
{
    private static readonly FieldInfo PeekButtonField = Reflect.Field(typeof(NCardGridSelectionScreen), "_peekButton");

    private static void Postfix(NCardGridSelectionScreen __instance)
    {
        if (!ModRuntime.Enabled) return;
        try
        {
            if (!MiniCardsToggleRegistry.TryMarkHost(__instance))
                return;

            var peekButton = PeekButtonField.GetValue(__instance) as NPeekButton;
            ToggleSwitch toggle = MiniCardsToggleRegistry.CreateToggle();
            MiniCardsToggleRegistry.AnchorAboveControl(__instance, toggle, peekButton, wireFocus: false);
            if (peekButton != null && GodotObject.IsInstanceValid(peekButton))
                peekButton.AddTargets(toggle);
        }
        catch (Exception ex)
        {
            ModRuntime.Disable(nameof(NCardGridSelectionScreen_Toggle_Patch), ex);
        }
    }
}

// --- Unified single-owner hover on mini grids --------------------------------------------
//
// On mini grids in pointer mode, enlarged cards (1.0x) overhang neighbours' hitboxes, so
// Godot's mouse-enter/exit on the live enlarged hitbox fights with slot geometry and can
// leave cards stuck enlarged after layout animations.
//
// Solution: suppress rogue mouse-triggered NGridCardHolder.OnFocus / NCardHolder.OnUnfocus
// while in mini pointer mode, and let GridHoverGate drive both hover and unhover atomically
// from each card's resting SmallScale slot (with hysteresis while reading an enlarged card).
// Controller / keyboard navigation bypasses GridHoverGate completely and runs 100% vanilla.
internal static class GridHoverGate
{
    private sealed class GridHoverState
    {
        public NGridCardHolder? Hovered;
        public Vector2 LastMouse = new(float.NaN, float.NaN);
        public float LastScrollY = float.NaN;
        public bool LastPointerOver;
    }

    private static readonly ConditionalWeakTable<NCardGrid, GridHoverState> States = new();

    private static readonly FieldInfo CardRowsField = Reflect.Field(typeof(NCardGrid), "_cardRows");
    private static readonly FieldInfo ScrollContainerField = Reflect.Field(typeof(NCardGrid), "_scrollContainer");
    private static readonly FieldInfo HitboxMouseHovered = Reflect.Field(typeof(NClickableControl), "_isHovered");
    private static readonly FieldInfo HolderIsHovered = Reflect.Field(typeof(NCardHolder), "_isHovered");
    internal static readonly FieldInfo HolderIsFocused = Reflect.Field(typeof(NCardHolder), "_isFocused");
    internal static readonly FieldInfo HoverTweenField = Reflect.Field(typeof(NCardHolder), "_hoverTween");

    private static readonly MethodInfo OnFocusMethod = Reflect.Method(typeof(NGridCardHolder), "OnFocus");
    private static readonly MethodInfo OnUnfocusMethod = Reflect.Method(typeof(NCardHolder), "OnUnfocus");
    internal static readonly MethodInfo CreateHoverTipsMethod = Reflect.Method(typeof(NCardHolder), "CreateHoverTips");
    internal static readonly MethodInfo ClearHoverTipsMethod = Reflect.Method(typeof(NCardHolder), "ClearHoverTips");
    internal static readonly MethodInfo HoverScaleGetter = Reflect.PropertyGetter(typeof(NCardHolder), "HoverScale");

    // Re-entrancy flag distinguishing our own authoritative OnFocus/OnUnfocus calls from rogue
    // Godot hitbox mouse signals.
    internal static bool IsDrivingHover { get; private set; }

    // Fraction of a card's resting small slot trimmed on each side before the pointer counts
    // as claiming that card (hysteresis so the pointer can inspect an enlarged card's overhang).
    internal const float SlotInset = 0.15f;

    internal static bool UsingController() =>
        NGame.Instance?.InputManager?.ControllerManager?.IsUsingDirectionalNavigation ?? false;

    internal static void ResetGridState(NCardGrid grid)
    {
        if (States.TryGetValue(grid, out GridHoverState? state))
        {
            state.Hovered = null;
            state.LastMouse = new Vector2(float.NaN, float.NaN);
            state.LastScrollY = float.NaN;
            state.LastPointerOver = false;
        }
    }

    internal static void Update(NCardGrid grid)
    {
        if (!DeckModeController.MiniEnabled || UsingController())
            return;

        Viewport? vp = grid.GetViewport();
        if (vp == null)
            return;

        GridHoverState state = States.GetOrCreateValue(grid);
        bool pointerOver = PointerOverGrid(grid, vp);
        Vector2 mouse = vp.GetMousePosition();
        float scrollY = ScrollContainerField.GetValue(grid) is Control sc && GodotObject.IsInstanceValid(sc)
            ? sc.Position.Y
            : 0f;

        if (!pointerOver)
        {
            if (state.Hovered != null || state.LastPointerOver)
                ClearAllHovered(grid, state);
            state.LastPointerOver = false;
            state.LastMouse = mouse;
            state.LastScrollY = scrollY;
            return;
        }

        // Fast path: mouse and scroll did not move and our tracked hover is still valid & focused.
        bool moved = mouse != state.LastMouse || scrollY != state.LastScrollY || !state.LastPointerOver;
        state.LastMouse = mouse;
        state.LastScrollY = scrollY;
        state.LastPointerOver = true;

        if (!moved && state.Hovered != null && GodotObject.IsInstanceValid(state.Hovered)
            && state.Hovered.IsInsideTree() && state.Hovered.IsVisibleInTree()
            && HolderIsFocused.GetValue(state.Hovered) is true
            && MouseActuallyInside(state.Hovered.Hitbox, mouse))
        {
            return;
        }

        if (CardRowsField.GetValue(grid) is not List<List<NGridCardHolder>> rows)
            return;

        NGridCardHolder? target = null;
        for (int r = 0; r < rows.Count && target == null; r++)
        {
            List<NGridCardHolder> row = rows[r];
            for (int c = 0; c < row.Count; c++)
            {
                NGridCardHolder h = row[c];
                if (h != null && GodotObject.IsInstanceValid(h) && MouseInSlot(h, mouse, SlotInset))
                {
                    target = h;
                    break;
                }
            }
        }

        NGridCardHolder? kept = null;
        for (int r = 0; r < rows.Count; r++)
        {
            List<NGridCardHolder> row = rows[r];
            for (int c = 0; c < row.Count; c++)
            {
                NGridCardHolder holder = row[c];
                if (holder == null || !GodotObject.IsInstanceValid(holder) || holder == target)
                    continue;
                if (HolderIsFocused.GetValue(holder) is not true)
                    continue;

                // No other card's slot claimed the cursor: keep reading this card while still on its enlarged body.
                if (target == null && MouseActuallyInside(holder.Hitbox, mouse))
                {
                    kept = holder;
                    continue;
                }

                DriveUnhover(holder);
            }
        }

        if (target != null)
        {
            if (HolderIsFocused.GetValue(target) is not true)
                DriveHover(target);
            state.Hovered = target;
        }
        else
        {
            state.Hovered = kept;
        }
    }

    private static void ClearAllHovered(NCardGrid grid, GridHoverState state)
    {
        if (state.Hovered != null && GodotObject.IsInstanceValid(state.Hovered))
            DriveUnhover(state.Hovered);
        state.Hovered = null;

        if (CardRowsField.GetValue(grid) is not List<List<NGridCardHolder>> rows)
            return;
        for (int r = 0; r < rows.Count; r++)
        {
            List<NGridCardHolder> row = rows[r];
            for (int c = 0; c < row.Count; c++)
            {
                NGridCardHolder h = row[c];
                if (h != null && GodotObject.IsInstanceValid(h) && HolderIsFocused.GetValue(h) is true)
                    DriveUnhover(h);
            }
        }
    }

    private static void DriveHover(NGridCardHolder holder)
    {
        IsDrivingHover = true;
        try
        {
            if (holder.Hitbox is NClickableControl hitbox)
                HitboxMouseHovered.SetValue(hitbox, true);
            OnFocusMethod.Invoke(holder, null);
        }
        finally
        {
            IsDrivingHover = false;
        }
    }

    private static void DriveUnhover(NCardHolder holder)
    {
        IsDrivingHover = true;
        try
        {
            if (holder.Hitbox is NClickableControl hitbox)
                HitboxMouseHovered.SetValue(hitbox, false);
            OnUnfocusMethod.Invoke(holder, null);
            NHoverTipSet.Remove(holder);
        }
        finally
        {
            IsDrivingHover = false;
        }
    }

    private static bool MouseInSlot(NCardHolder holder, Vector2 mouse, float inset)
    {
        NClickableControl? hitbox = holder.Hitbox;
        if (hitbox == null || !hitbox.IsInsideTree() || !hitbox.IsVisibleInTree()
            || holder.GetParent() is not CanvasItem parent)
            return false;
        Transform2D hitboxInHolder = holder.GetGlobalTransformWithCanvas().AffineInverse() * hitbox.GetGlobalTransformWithCanvas();
        Vector2 pivot = holder.PivotOffset;
        Transform2D restingHolder = new Transform2D(holder.Rotation, holder.SmallScale, 0f, holder.Position + pivot)
                                    * new Transform2D(0f, -pivot);
        Transform2D slot = parent.GetGlobalTransformWithCanvas() * restingHolder * hitboxInHolder;
        Vector2 local = slot.AffineInverse() * mouse;
        Vector2 margin = hitbox.Size * inset;
        return new Rect2(margin, hitbox.Size - 2f * margin).HasPoint(local);
    }

    private static bool PointerOverGrid(NCardGrid grid, Viewport vp)
    {
        Control? hovered = vp.GuiGetHoveredControl();
        return hovered != null && (hovered == grid || grid.IsAncestorOf(hovered));
    }

    private static bool MouseActuallyInside(NClickableControl? hitbox, Vector2 mouse)
    {
        if (hitbox == null || !hitbox.IsInsideTree() || !hitbox.IsVisibleInTree())
            return false;
        Vector2 local = hitbox.GetGlobalTransformWithCanvas().AffineInverse() * mouse;
        return new Rect2(Vector2.Zero, hitbox.Size).HasPoint(local);
    }

    internal static void ScrubHoverFlags(NCardHolder holder)
    {
        if (holder.Hitbox is NClickableControl hitbox)
            HitboxMouseHovered.SetValue(hitbox, false);
        HolderIsHovered.SetValue(holder, false);
        HolderIsFocused.SetValue(holder, false);
        if (HoverTweenField.GetValue(holder) is Tween tween && GodotObject.IsInstanceValid(tween))
            tween.Kill();
        NHoverTipSet.Remove(holder);
    }
}

[HarmonyPatch(typeof(NGridCardHolder), "OnFocus")]
internal static class NGridCardHolder_OnFocus_Patch
{
    private static bool Prefix(NGridCardHolder __instance)
    {
        if (!ModRuntime.Enabled || GridHoverGate.IsDrivingHover) return true;
        try
        {
            if (DeckModeController.MiniEnabled && GridOwnership.IsGridOwned(__instance) && !GridHoverGate.UsingController())
                return false;
            return true;
        }
        catch (Exception ex)
        {
            ModRuntime.Disable(nameof(NGridCardHolder_OnFocus_Patch), ex);
            return true;
        }
    }
}

[HarmonyPatch(typeof(NCardHolder), "OnUnfocus")]
internal static class NCardHolder_OnUnfocus_Patch
{
    private static bool Prefix(NCardHolder __instance)
    {
        if (!ModRuntime.Enabled || GridHoverGate.IsDrivingHover) return true;
        try
        {
            if (DeckModeController.MiniEnabled && __instance is NGridCardHolder
                && GridOwnership.IsGridOwned(__instance) && !GridHoverGate.UsingController())
                return false;
            return true;
        }
        catch (Exception ex)
        {
            ModRuntime.Disable(nameof(NCardHolder_OnUnfocus_Patch), ex);
            return true;
        }
    }
}

[HarmonyPatch(typeof(NCardGrid), "_Process")]
internal static class NCardGrid_Process_Patch
{
    private static void Postfix(NCardGrid __instance)
    {
        if (!ModRuntime.Enabled || !__instance.IsVisibleInTree()) return;
        try
        {
            DeckModeController.PollHotkey(__instance);
            DeckModeController.FlushLockedReinit(__instance);
            GridHoverGate.Update(__instance);
        }
        catch (Exception ex)
        {
            ModRuntime.Disable(nameof(NCardGrid_Process_Patch), ex);
        }
    }
}

[HarmonyPatch(typeof(NGridCardHolder), "Create")]
internal static class Create_Reset_Patch
{
    private static void Postfix(NGridCardHolder __result)
    {
        if (!ModRuntime.Enabled || __result == null) return;
        try { GridHoverGate.ScrubHoverFlags(__result); }
        catch (Exception ex) { ModRuntime.Disable(nameof(Create_Reset_Patch), ex); }
    }
}

[HarmonyPatch(typeof(NCardHolder), "DoCardHoverEffects")]
internal static class NCardHolder_DoCardHoverEffects_Patch
{
    private static bool Prefix(NCardHolder __instance, bool isHovered)
    {
        if (!ModRuntime.Enabled) return true;
        try
        {
            if (!DeckModeController.MiniEnabled || __instance is not NGridCardHolder
                || !GridOwnership.IsGridOwned(__instance))
                return true;

            (GridHoverGate.HoverTweenField.GetValue(__instance) as Tween)?.Kill();
            GridHoverGate.HoverTweenField.SetValue(__instance, null);
            if (isHovered)
            {
                __instance.Scale = (Vector2)GridHoverGate.HoverScaleGetter.Invoke(__instance, null)!;
                if (__instance.CardNode?.Visibility == ModelVisibility.Visible)
                    GridHoverGate.CreateHoverTipsMethod.Invoke(__instance, null);
            }
            else
            {
                __instance.Scale = __instance.SmallScale;
                GridHoverGate.ClearHoverTipsMethod.Invoke(__instance, null);
            }
            return false;
        }
        catch (Exception ex)
        {
            ModRuntime.Disable(nameof(NCardHolder_DoCardHoverEffects_Patch), ex);
            return true;
        }
    }
}
