using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Timeline.UnlockScreens;

namespace DeckView;

// DeckView — Slay the Spire 2 port of the STS1 "make deck-view cards smaller" mod.
// (The whole-act map view that older DeckView builds bundled is now the separate FlatMap mod.)
//
// STS2's card grid (NCardGrid) is already responsive: the column count and scroll
// bounds are computed from the per-card layout size and padding. So we only have to
// make the cards smaller and everything reflows to fit more per screen.
//
// Two coordinated levers, kept proportional so layout and rendering stay in sync:
//   1. NCardGrid._cardSize  — the layout cell size (drives Columns, positions, scroll).
//   2. NCardHolder.SmallScale — the *rendered* scale of each grid card.
//
// Both are the vanilla 0.8 baseline (NCard.defaultSize * smallScale / SmallScale).
// We multiply both by the same factor. Merchant and card-bundle screens read the
// static `smallScale` field directly (not the SmallScale property), so they are
// untouched — the shrink is naturally scoped to card-grid views.
//
// A grid card enlarges to HoverScale (1.0) only while the mouse is *actually* over its
// (small) on-screen rect. See GridHoverGate for why: without that, a card can open
// "stuck big" from a stale mouse-over the game never cleared.
[ModInitializer(nameof(Init))]
public static class DeckViewMod
{
    // 0.6 => cards render at 60% of the vanilla deck-view size, matching the STS1 mod.
    // Lower = smaller cards / more columns. Tune to taste. (Only applied in mini mode —
    // see DeckModeController; toggle with the hotkey below, state persists across runs.)
    public const float CardScaleFactor = 0.6f;

    // Vanilla NCardGrid.CardPadding is a constant 40f. Tighter spacing packs in more
    // columns/rows. Set equal to 40f to keep vanilla spacing.
    public const float CardPadding = 24f;

    // Hotkey to toggle mini <-> large card mode. Checked while a card grid is on screen
    // (deck view, library, card-select), so open the deck and press it to flip live.
    // NOTE: this is a plain key (no modifier) because the key is *read*, not consumed —
    // if it happens to also be bound to something on that screen, both would fire. T isn't
    // a known deck-screen binding; change here if it clashes.
    public const Key ToggleDeckModeKey = Key.T;

    // DeckView was built and tested against this game version. On another build, every private
    // hook is preflighted before Harmony changes anything. Missing hooks disable the mod and
    // leave the game's UI untouched instead of crashing or leaving a partially patched mod.
    public const string TestedGameVersion = "v0.110.1";

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

// --- Mini/large toggle: persistent state + hotkey + clean live swap -------------------
//
// The shrink is not unconditional: every shrink patch is gated on
// DeckModeController.MiniEnabled, whose value is loaded from (and saved to) a small config
// file so it survives across runs. Default is mini (matches the mod's original behavior).
//
// Toggling has to be a *complete* swap, not a half-state: the layout cell size (_cardSize)
// determines Columns / positions / scroll and is only computed in ConnectSignals, which
// runs once. So on toggle we set each live grid's _cardSize from the vanilla base we
// recorded (times the mode factor) and flag it for reinit — the grid then rebuilds itself
// (InitGrid) next frame with the new size, padding, and rendered scale all in agreement.
internal static class DeckModeController
{
    private static readonly FieldInfo CardSizeField = Reflect.Field(typeof(NCardGrid), "_cardSize");
    private static readonly FieldInfo NeedsReinitField = Reflect.Field(typeof(NCardGrid), "_needsReinit");

    // Live grids -> the vanilla (un-shrunk) _cardSize captured at ConnectSignals time.
    private static readonly Dictionary<NCardGrid, Vector2> _grids = new();

    private static bool _keyWasDown;

    static DeckModeController() => ModRuntime.Disabled += RestoreVanilla;

    internal static bool MiniEnabled => DeckViewConfig.MiniDeck;

    internal static void Register(NCardGrid grid, Vector2 vanillaCardSize) => _grids[grid] = vanillaCardSize;

    internal static void Unregister(NCardGrid grid) => _grids.Remove(grid);

    // Edge-detected hotkey poll (called each frame a card grid processes).
    internal static void PollHotkey()
    {
        bool down = Input.IsKeyPressed(DeckViewMod.ToggleDeckModeKey);
        if (down && !_keyWasDown)
            Toggle();
        _keyWasDown = down;
    }

    internal static void Toggle() => SetMini(!DeckViewConfig.MiniDeck);

    // Set mini mode to an explicit value and rebuild every live grid to match. Used by both the
    // hotkey (Toggle) and the on-screen "Mini-cards" tickbox, so they stay in agreement.
    internal static void SetMini(bool on)
    {
        if (DeckViewConfig.MiniDeck == on)
            return; // already there — nothing to rebuild
        DeckViewConfig.MiniDeck = on;
        MiniCardsToggle_Patch.SyncAll(on); // keep the on-screen checkbox(es) in agreement with the hotkey
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
            // Resize the layout cell from the recorded vanilla base, then let the grid rebuild
            // itself so columns/positions/scroll and the rendered card scale all flip together.
            CardSizeField.SetValue(grid, kv.Value * factor);
            NeedsReinitField.SetValue(grid, true);
        }
    }

    private static void RestoreVanilla()
    {
        foreach (KeyValuePair<NCardGrid, Vector2> kv in _grids.ToArray())
        {
            if (!GodotObject.IsInstanceValid(kv.Key)) continue;
            CardSizeField.SetValue(kv.Key, kv.Value);
            NeedsReinitField.SetValue(kv.Key, true);
        }
        _grids.Clear();
    }
}

// Shrink the layout cell size right after the grid computes it in ConnectSignals
// (vanilla: _cardSize = NCard.defaultSize * NCardHolder.smallScale). Because Columns,
// row count, positions and scroll limits all derive from _cardSize + CardPadding, the
// grid reflows to more columns automatically.
[HarmonyPatch(typeof(NCardGrid), "ConnectSignals")]
internal static class NCardGrid_ConnectSignals_Patch
{
    // Harmony injects the private field `_cardSize` as the parameter `____cardSize`
    // (three-underscore prefix + the field name, which itself starts with '_'). At postfix
    // entry it holds the vanilla base size; we record that (so a later live toggle can
    // recompute from it without re-running ConnectSignals) and shrink only in mini mode.
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

// Drop a grid from the toggle registry when it leaves the tree.
[HarmonyPatch(typeof(NCardGrid), "_ExitTree")]
internal static class NCardGrid_ExitTree_Patch
{
    private static void Postfix(NCardGrid __instance)
    {
        if (!ModRuntime.Enabled) return;
        try { DeckModeController.Unregister(__instance); }
        catch (Exception ex) { ModRuntime.Disable(nameof(NCardGrid_ExitTree_Patch), ex); }
    }
}

// Shrink the *rendered* scale of grid cards to match the smaller layout cells.
// Grid holders set their Scale from the SmallScale property (NCardGrid line ~799 and
// NGridCardHolder line ~104), and the hover-out tween returns to SmallScale, so this
// keeps rendering consistent — hover still pops to HoverScale (1.0) for readability.
//
// Guarded to NGridCardHolder so the shrink is limited to deck-grid cards. This leaves
// the combat hand (NHandCardHolder — uses its own _targetScale), the inspect popup
// (NPreviewCardHolder — overrides SmallScale, so this patch never runs for it), and
// selected-from-hand cards (NSelectedHandCardHolder) at their normal size — matching
// the STS1 mod's "leave hand/popups/normal rendering alone" scope.
[HarmonyPatch(typeof(NCardHolder), "SmallScale", MethodType.Getter)]
internal static class NCardHolder_SmallScale_Patch
{
    private static void Postfix(NCardHolder __instance, ref Vector2 __result)
    {
        if (!ModRuntime.Enabled) return;
        Vector2 vanilla = __result;
        try
        {
            if (DeckModeController.MiniEnabled && __instance is NGridCardHolder && !GridHoverGate.IsInFixedCardRow(__instance))
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

// Tighten the spacing between cards (vanilla getter returns a constant 40f).
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

// --- Visible "Mini-cards" toggle in the deck-view control cluster -------------------------
//
// A discoverable on-screen counterpart to the T hotkey, wired to DeckModeController.
//
// We build our OWN control (a self-drawn ToggleSwitch) rather than cloning the game's
// "View upgrades" NTickbox. Cloning that tickbox does NOT work: it's inlined in the deck-view
// scene and its visuals are addressed by scene-unique names ("%TickboxVisuals") OWNED BY THE
// SCREEN, so a duplicated-and-reparented copy can't resolve them — its ConnectSignals throws
// NullReferenceException, which (as a postfix of NCardsViewScreen.ConnectSignals) aborts the
// whole deck-screen build and makes every card disappear. ToggleSwitch has no such scene
// coupling. Placement and sizing are measured from the live "View upgrades" control.
[HarmonyPatch(typeof(NCardsViewScreen), "ConnectSignals")]
internal static class MiniCardsToggle_Patch
{
    private static readonly FieldInfo ShowUpgradesField = Reflect.Field(typeof(NCardsViewScreen), "_showUpgrades");
    private const string AddedMeta = "minicards_toggle";

    // Live switches, so the T hotkey and the on-screen toggle always agree (SyncAll below).
    private static readonly List<ToggleSwitch> _toggles = new();

    private static void Postfix(NCardsViewScreen __instance)
    {
        if (!ModRuntime.Enabled) return;
        try
        {
            if (__instance.HasMeta(AddedMeta))
                return;
            __instance.SetMeta(AddedMeta, true);

            var upgrades = ShowUpgradesField.GetValue(__instance) as Control;
            Control? label = __instance.GetNodeOrNull("%ViewUpgradesLabel") as Control;
            Control? visuals = __instance.GetNodeOrNull("%TickboxVisuals") as Control;
            GameStyle.ConfigureToggleMetrics(visuals, label);

            var toggle = new ToggleSwitch("Mini-cards", DeckModeController.MiniEnabled, OnMiniToggled)
            {
                Name = "MiniCardsToggle",
                ZIndex = 50,
            };
            __instance.AddChild(toggle);
            if (upgrades != null && GodotObject.IsInstanceValid(upgrades))
            {
                toggle.GlobalPosition = upgrades.GlobalPosition - new Vector2(0f, toggle.Size.Y + 8f);
                NodePath previousTop = upgrades.FocusNeighborTop;
                toggle.FocusNeighborBottom = toggle.GetPathTo(upgrades);
                upgrades.FocusNeighborTop = upgrades.GetPathTo(toggle);
                ModRuntime.Disabled += () =>
                {
                    if (GodotObject.IsInstanceValid(upgrades))
                        upgrades.FocusNeighborTop = previousTop;
                };
            }
            _toggles.Add(toggle);
            Log.Info($"[DeckView] mini-cards toggle at {toggle.GlobalPosition} size={toggle.Size} " +
                     $"font={GameStyle.ToggleFontSize}px box={GameStyle.ToggleBoxSize:0.#}px");
        }
        catch (Exception ex)
        {
            ModRuntime.Disable(nameof(MiniCardsToggle_Patch), ex);
        }
    }

    // Reflect the current mode onto every live switch WITHOUT re-firing the callback (so a T-key
    // flip updates the on-screen toggle, and vice-versa, with no feedback loop). Called by SetMini.
    internal static void SyncAll(bool on)
    {
        _toggles.RemoveAll(t => !GodotObject.IsInstanceValid(t));
        foreach (ToggleSwitch t in _toggles)
            t.SetOn(on);
    }

    private static void OnMiniToggled(bool pressed) => DeckModeController.SetMini(pressed);
}

// --- Hover reconcile: a grid card is "big" only while the mouse is really over it ------
//
// Symptom this fixes: open the deck view (press "d") and one card sometimes shows at full
// size while every other card is correctly shrunk. It's NOT under the mouse — it's the
// card that would have been under the cursor at *vanilla* card size — and it stays big
// until you mouse over it and off again.
//
// Root cause: while the grid lays out / animates in, a card's hitbox briefly passes under
// the (stationary) mouse, so Godot fires MouseEntered and the card pops to HoverScale.
// The card then settles into its small position away from the cursor, but Godot does NOT
// fire MouseExited for a control that moves out from under a stationary mouse — so the
// hitbox's _isHovered stays true and nothing shrinks the card back. Trusting _isHovered
// can't fix this; it IS the stale value.
//
// Fix: every frame the grid processes, reconcile against ground truth. For each displayed
// holder that's currently enlarged (_isFocused), if the mouse isn't actually inside the
// holder's real (scaled) on-screen rect, force it back to un-hovered SmallScale. A genuine
// mouse-over keeps the card big (the game's own MouseEntered path still enlarges it); when
// you move off, the game's normal MouseExited shrink runs (a smooth tween, left untouched
// because _isFocused is already false by then). Only the stuck/stale case is corrected.
//
// This also covers the deck view grab-focusing a default card on open (enlarge with no
// mouse on it): that card is _isFocused with the mouse elsewhere, so it's reconciled small
// within a frame. Skipped while a controller is in use, so controller focus still enlarges.
internal static class GridHoverGate
{
    // NClickableControl's *mouse* hover flag (set by MouseEntered/Exited). We clear the
    // stale value when correcting a card; we do NOT read it to decide (that's the bug).
    private static readonly FieldInfo HitboxMouseHovered =
        Reflect.Field(typeof(NClickableControl), "_isHovered");

    // The holder's own hover/focus bookkeeping (base NCardHolder).
    private static readonly FieldInfo HolderIsHovered = Reflect.Field(typeof(NCardHolder), "_isHovered");
    internal static readonly FieldInfo HolderIsFocused = Reflect.Field(typeof(NCardHolder), "_isFocused");
    private static readonly FieldInfo HolderHoverTween = Reflect.Field(typeof(NCardHolder), "_hoverTween");

    // The game's own un-hover entry point. RefreshFocusState() re-reads CanBeFocused (== the
    // holder's _isHovered) and, when it changed, flips _isFocused and calls DoCardHoverEffects,
    // which on false: kills the hover tween, starts the normal shrink tween, AND calls
    // ClearHoverTips() -> NHoverTipSet.Remove(this). Driving this is how we dismiss a card's
    // popup EXACTLY the way vanilla does (both keyword tips and related-card previews live in
    // the one NHoverTipSet keyed by the holder).
    private static readonly MethodInfo RefreshFocusStateMethod =
        Reflect.Method(typeof(NCardHolder), "RefreshFocusState");

    // Controller in use? The reconcile is skipped then, so controller focus still enlarges.
    // The Instance null-check is legitimate state (not error hiding): no controller manager
    // yet => treat as pointer mode. Directional navigation covers controller and keyboard-only.
    internal static bool UsingController() => NControllerManager.Instance?.IsUsingDirectionalNavigation ?? false;

    // Is the mouse genuinely inside this hitbox's current on-screen rect? Uses the full
    // canvas transform (which includes the holder's scale), so it's correct whether the
    // card is drawn small or popped to full size — unlike the stale _isHovered flag.
    internal static bool MouseActuallyInside(NClickableControl? hitbox)
    {
        if (hitbox == null || !hitbox.IsInsideTree() || !hitbox.IsVisibleInTree())
            return false;
        Viewport? vp = hitbox.GetViewport();
        if (vp == null)
            return false;
        Vector2 local = hitbox.GetGlobalTransformWithCanvas().AffineInverse() * vp.GetMousePosition();
        return new Rect2(Vector2.Zero, hitbox.Size).HasPoint(local);
    }

    // Drive a stuck-enlarged holder back to the clean, un-hovered small state — using the
    // game's OWN un-hover path so the shrink and, crucially, the popup dismissal are identical
    // to what happens when you normally move the mouse off a card. (The previous version
    // hand-rolled the shrink and called NHoverTipSet.Remove directly; that bypassed the game's
    // ClearHoverTips path and could leave the keyword tip / related-card preview floating.)
    internal static void ForceUnhover(NCardHolder holder)
    {
        // Clear the stale ground-truth mouse bit on the hitbox (the ROOT of the stuck-hover
        // bug: Godot never fired MouseExited, so this stayed true). Must clear it or (a) the
        // holder can't be focused again on a real future hover, and (b) RefreshFocusState below
        // wouldn't see "not hovered".
        if (holder.Hitbox is NClickableControl hitbox)
            HitboxMouseHovered.SetValue(hitbox, false);
        HolderIsHovered.SetValue(holder, false);

        // Now run the game's real un-hover: RefreshFocusState() sees _isHovered==false, flips
        // _isFocused to false, and calls DoCardHoverEffects(false) -> normal shrink tween +
        // ClearHoverTips() -> NHoverTipSet.Remove(this). This frees the whole tip set (keyword
        // tips AND related-card previews) exactly as vanilla does. The reconcile only calls us
        // for holders that are currently _isFocused, so this always drives the full dismissal.
        RefreshFocusStateMethod.Invoke(holder, null);

        // Safety net for any path where the holder was already un-focused but a tip set is
        // still registered under it (RefreshFocusState would short-circuit). Idempotent.
        NHoverTipSet.Remove(holder);
    }

    // Lightweight flag scrub for pooled reuse (NGridCardHolder.Create). The holder may not be
    // in the tree yet, so we must NOT start a tween or drive DoCardHoverEffects — just clear
    // any stale hover/focus bits and drop a stray tip so a recycled card starts clean.
    internal static void ScrubHoverFlags(NCardHolder holder)
    {
        if (holder.Hitbox is NClickableControl hitbox)
            HitboxMouseHovered.SetValue(hitbox, false);
        HolderIsHovered.SetValue(holder, false);
        HolderIsFocused.SetValue(holder, false);
        if (HolderHoverTween.GetValue(holder) is Tween tween && GodotObject.IsInstanceValid(tween))
            tween.Kill();
        NHoverTipSet.Remove(holder);
    }

    // A few screens reuse NGridCardHolder but lay a handful of cards out in a fixed-spacing
    // row instead of a scrollable NCardGrid: the choose-a-card screen, the post-combat card
    // reward, and the unlock screen. Those aren't NCardGrid (so the reconcile never touches
    // them) and we also leave them full size here.
    internal static bool IsInFixedCardRow(Node node)
    {
        for (Node? p = node; p != null; p = p.GetParent())
        {
            if (p is NChooseACardSelectionScreen or NCardRewardSelectionScreen or NUnlockCardsScreen)
                return true;
        }
        return false;
    }
}

// Every frame the grid processes, snap any "big" card the mouse isn't really over back to
// small. Cheap: only cards that are currently enlarged (usually 0–1) get the hit test.
[HarmonyPatch(typeof(NCardGrid), "_Process")]
internal static class NCardGrid_Process_Reconcile_Patch
{
    private static void Postfix(NCardGrid __instance)
    {
        if (!ModRuntime.Enabled) return;
        try
        {
            DeckModeController.PollHotkey();
            if (!DeckModeController.MiniEnabled || GridHoverGate.UsingController())
                return;
            foreach (NGridCardHolder holder in __instance.CurrentlyDisplayedCardHolders)
            {
                if (holder == null)
                    continue;
                if (GridHoverGate.HolderIsFocused.GetValue(holder) is not true)
                    continue;
                if (GridHoverGate.MouseActuallyInside(holder.Hitbox))
                    continue;
                Dbg.Once("reconcile", "hover reconcile fired: forcing a stuck-big card back to small " +
                                       "+ dismissing its popup via the game's own un-hover path");
                GridHoverGate.ForceUnhover(holder);
            }
        }
        catch (Exception ex)
        {
            ModRuntime.Disable(nameof(NCardGrid_Process_Reconcile_Patch), ex);
        }
    }
}

// Grid holders come from a pool; Create()/OnReturnedFromPool reset Scale but not the
// _isHovered/_isFocused flags. Scrub them on reuse so a recycled "hovered" card starts
// clean (avoids even a one-frame stale enlarge before the reconcile runs).
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
