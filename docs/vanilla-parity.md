# FlatMap ↔ vanilla map: parity plan

**The governing principle (maintainer, 2026-08-01): the flat map is vanilla-exact in every
behavior — pulse, borders, hover, sounds, tips, banners, rules, including future ones where
we can inherit them — EXCEPT the short list of divergences in §2 that exist to serve this
mod's purpose (one-screen compression, colour recognizability, distraction reduction, and a
few forced practicalities).** When in doubt: copy vanilla.

Built 2026-08-01 by decompiling every map class from STS2 `v0.110.1` (`NMapScreen`,
`NMapPoint`, `NNormalMapPoint`, `NBossMapPoint`, `NAncientMapPoint`, `NMapMarker`,
`NMapLegendItem`, `NMapBg`, `NMapCircleVfx`, `NMapNodeSelectVfx`, `NMapPingVfx`,
`NMapDrawings`, `NMapSelectFtue`) and diffing each behavior against
`flatmap/FlatMapMod.cs`. Reviewed and decided by the maintainer the same day; the
statuses below reflect those decisions.

Status tags: **[approved]** gap confirmed, build it · **[decide]** still needs a
maintainer decision · **[conscious]** deliberate divergence (kept) · **[retired]** was a
proposed gap, resolved as conscious instead · **[fixdoc]** our docs described vanilla
wrong; corrected here.

---

## 1. Corrections to our own record ([fixdoc] — accepted 2026-08-01)

Things `docs/requirements.md` asserts about vanilla that the decompile disproves. Small
individually, but they redirected several §3 outcomes; kept as the accurate record.

1. **Vanilla paths are NOT dashed lines.** `NMapScreen.CreatePath` instantiates a
   `map_dot.tscn` `TextureRect` (an ink-dab sprite) every **22 px** along the leg, each
   with ±3 px position jitter, ±0.1 rad rotation jitter, and a random horizontal flip.
   Untraveled legs are modulated `Act.MapUntraveledColor`; walked legs
   `Act.MapTraveledColor` **and scaled 1.2×**. (Our dashed lines stay anyway — §2.)
2. **Legend hover does not pulse in vanilla.** `NMapLegendItem.OnFocus` →
   `HighlightPointType` → every matching `NNormalMapPoint.AnimHover()`: a **held swell to
   1.45×** (plus the white outline flash on travelable ones), released on unfocus.
3. **Vanilla dims *every* node that isn't your trail or the frontier.**
   `NMapPoint.TargetColor`: Traveled & Travelable icons are white (full natural art);
   *all* other nodes — including reachable future rooms — get
   `StsColors.halfTransparentWhite` (50 % alpha). Vanilla has no reachability logic;
   our ghosting of dead rooms is our own addition (kept — §2).
4. **The vanilla marker floats ABOVE the node** (centered, −35 px), pops in with an
   elastic tween, hides during travel, and is **not shown when the current row is the
   start or boss row** (`NMapScreen.Open`). Our left-side placement is conscious (§2);
   the suppression rules are vanilla behavior we adopt (§3.11).
5. **The ink-circle's "randomness" is seeded from the run**, not a coord hash:
   `new Rng(runSeed + row + 131*row)` (vanilla quirk: col unused), rotation 0–360°,
   scale 0.85–0.90, alpha 0.95. Adopted verbatim in §3.6.

---

## 2. Conscious divergences (confirmed 2026-08-01 — the complete list)

These are the ONLY intended differences from vanilla. Everything else matches.

1. **One screen, no scrolling** — the entire point. No drag/scroll/wheel, no scroll
   clamps, no start-of-act scroll-down animation (the act *banner* IS kept — §3.7);
   rows compressed to fit; the Compress lane system + toggle.
2. **Exact grid placement** (no ±21/±25 position jitter): compressed spacing is far
   tighter than vanilla's cells; position jitter would destroy row/lane legibility.
   (Icon *rotation* jitter is adopted — §3.4.)
3. **Colour boundaries — the one visual addition.** The icon's `_outline` mask is drawn
   in a bright type colour behind the natural-colour icon (Neon Rims), where vanilla
   tints it `MapBgColor` (invisible carving). This is an *addition for recognizability*;
   every dynamic behavior of the outline (white flash on travelable hover, press/unhover
   returns) still copies vanilla, layered on our colour instead of the bg colour (§3.9).
   **Palette update (2026-08-01): Shop = pure gold; Unknown `?` = light blue** —
   supersedes the 2026-07-30 "warm coin-gold, never white/near-white" directive for `?`.
4. **Distraction dimming of unreachable-and-unvisited rooms**: dead rooms are faint
   ghosts and their edges fade (vanilla has no reachability concept at all). This is an
   extra dimming layer *below* whatever the base tint table says (see D1 in §4).
5. **Size language**: elite ×1.35, boss ×1.9, start ×1.45 (vanilla: same-size normals;
   hover does the enlarging). Applied to drawing, hitboxes, focus rects.
6. **Static boss icon art** (`ui/run_history/{bossid}.png`) instead of the animated
   Spine boss — forced (a `_Draw` pass can't render Spine); intent is the closest
   possible static match to the vanilla node's look.
7. **Marker at the node's left, rotated, small** — forced: in the compressed layout the
   vanilla above-the-node placement occludes the node one floor up. Vanilla's
   suppression rules still apply (§3.11).
8. **Info panel** (act name bottom-right), **two checkboxes** bottom-left, **M/F keys**,
   and the whole two-modes-one-map architecture (capstone page, Open-prefix, etc.).
9. **No background art** (decided 2026-08-01; was gap §3.1): the flat `Act.MapBgColor`
   fill stays; vanilla's `MapTopBg/MidBg/BotBg` painted art is not reproduced.
10. **Paths stay `DrawDashedLine`** (decided 2026-08-01; was gap §3.2): current look is
    good; we no longer claim it is vanilla's ink-dot style (see §1.1).
11. **No debug-travel support** (decided 2026-08-01; was gap §3.12): vanilla's
    dev-console `IsDebugTravelEnabled` click-anywhere is not honored on the flat page.
    Player-facing travel rules are vanilla-identical.
12. **No map drawings on the flat page** (decided 2026-08-01, was D2): draw/erase/clear/
    share stay classic-only (lines are stored in bg-relative net-space and would land
    wrong on the compressed layout). F flips to classic to see them.
13. **No multiplayer visuals on the flat page** (decided 2026-08-01, was D3): vote
    badges, ping ripples, split-vote animation, and remote cursors are not drawn. The
    underlying systems keep working untouched — flat-page clicks cast real votes, pings
    still sound — the information is just not *shown*. Vote badges are the natural
    future addition if MP use ever matters.
14. **Retired divergences (2026-08-01)** — these were ours and are being REMOVED in
    favor of exact vanilla behavior: the permanent white frontier border rings, the
    continuous legend-hover pulse (→ vanilla held swell), the visited-node dimming, the
    bright-future brightness scheme, and the current-node "done" dimming (→ vanilla's
    tint table, §3.16). See §3.9 / §3.10 / §3.16.

---

## 3. Approved work queue ([approved] 2026-08-01 unless marked)

### 3.1 Background art — [retired → conscious §2.9]

### 3.2 Ink-dot paths — [retired → conscious §2.10]

### 3.3 Quest icons — information loss, top priority
Vanilla shows `_questIcon` on any node whose `MapPoint.Quests.Count > 0` (live-updated
via `Point.NodeMarkedChanged`). The flat page drops quest markers entirely. Read
`np.Point.Quests.Count` in `BuildModel`, grab the live `_questIcon` texture (like
`_icon`/`_outline`), draw it at vanilla's relative offset/scale on the node.

### 3.4 Per-node icon rotation
Vanilla rotates every normal node's icon container by `NextGaussianFloat(0, 8)` degrees
(`SetAngle`) — the hand-inked look. Match with a deterministic per-coord gaussian angle
in `DrawNodeShape` (`DrawSetTransform` is already used for the ink circle).

### 3.5 Open/close sound & fade
`NMapScreen.Open`/`Close` play `event:/sfx/ui/map/map_open` / `map_close` and run
0.15–0.25 s fades. The flat page opens silent and instant. Play the same sfx via
`SfxCmd.Play` on flat open/close and give the page a ~0.25 s modulate fade-in matching
`_tween` timings (skip the position slide).

### 3.6 Ink circles: vanilla seeding + the travel brushstroke (expanded 2026-08-01)
- Seed stamped-circle rotation/scale exactly as `NMapCircleVfx._Ready`:
  `new Rng(runSeed + row + 131*row)` (reproduce the col-unused quirk verbatim), so
  F-flipping shows the *same* circles as the classic map. Run seed via
  `_runState.Rng.Seed` reflection (add to `HookCatalog`).
- **On travel-click, draw the brushstroke like vanilla does**: play the ink-circle
  flipbook (`map_circle_0..4` at 1/24 s per frame, scale-in from 2× with expo-out,
  alpha 0→0.95) around the chosen node *on the flat page* before it closes, and attempt
  the genuine brush-burst by instancing the real `NMapNodeSelectVfx.Create(scale)` scene
  as a page child (public API; auto-frees after 1 s). The game's own `map_select` /
  `wipe_map` sfx already play via `TravelToMapCoord`. Close the page after the beat
  (~0.5 s), not instantly.

### 3.7 Act banner at act start
Vanilla shows `NActBanner.Create(act, actIndex)` on the first map open of an act; our
Open-prefix skips classic `Open()`, so flat mode never shows it. Create the same banner
over the flat page on the first flat open of each act (track per `(runId, actIndex)`).

### 3.8 Visited-node history hover tips
Hovering a Traveled node in vanilla (mouse only) shows
`NHoverTipSet.CreateAndShowMapPointHistory(NMapPointHistoryHoverTip.Create(floorNum,
netId, historyEntry))` — the absolute floor number and what happened in that room
(`NMapPoint.OnFocus`). Wire the same call on hover-enter of a visited node on the flat
page; `NHoverTipSet.Remove` on hover-exit.

### 3.9 Motion & border parity — copy vanilla exactly (expanded 2026-08-01)
The user directive: *utterly copy the original on ALL pulse and colour/border aspects,
except the added colour boundaries.* Concretely:
- **Remove the permanent white frontier rings** (our invention).
- **Hover** = tween to **1.45×** in 0.05 s; release to 1× in 0.5 s cubic-out (replaces
  our 1.2× hold with custom timings).
- **Hovering a travelable node flashes its outline white** (`_outlineColor`
  white-0.75) for the hover's duration — layered on our coloured rim exactly where
  vanilla layers it on the bg-coloured outline.
  **Restyled 2026-08-02 (maintainer)**: the travelable border is now white *at rest*
  (matching vanilla's system) and the hover cue is that white border growing THICKER,
  replacing the rim-to-white lerp. Boss stays thin (badge-blob rule, §3.16 note).
- **Press-down** squashes to 0.9× (0.3 s expo-out) on a travelable node.
- **Frontier pulse**: vanilla's `sin(t*4)*0.25+1.2` on the icon container (formula
  already matches), including vanilla's rule that a focused/hovered node stops pulsing
  and lerps toward rest, and the unfocus phase reset (`_elapsedTime = 5π/4`).
- **Start node**: vanilla's gentle ±0.05 pulse when it is the frontier (act start).
- If 1.45× hover collides with a neighbour at compressed spacing, **shrink base radii**
  (sizing policy) rather than weaken the motion.

### 3.10 Legend parity (updated 2026-08-01)
- **Hover reaction = vanilla's**: held `AnimHover` swell (1.45× + white outline flash on
  travelable) on every node of the hovered type, released on unfocus — replaces our
  continuous pulse.
- **Hover tips**: real `NMapLegendItem`s show localized tips on focus; verify they fire
  while the panel is borrowed onto the flat page; forward if not.
- **Slide-in**: reuse vanilla's open tween (from x+120, 0.1 s delay) when borrowing.
- **Controller**: wire `confirm` to jump focus into the legend list and up/down between
  items, as `OnLegendHotkeyPressed` does on the classic map.

### 3.11 Marker suppression parity
Never show the marker when the current row is the start or boss row; hide it during
travel; (already null in MP since we read the live `_marker` texture). Left-side
placement itself stays conscious (§2.7).

### 3.12 Debug travel — [retired → conscious §2.11]

### 3.13 Top-bar map button oscillation
Call `TopBar.Map.StartOscillation()` on flat open and `StopOscillation()` on close, as
vanilla does, so the top bar signals "this button closes what you're looking at" in both
modes.

### 3.14 First-run FTUE ("Select a starting room")
On the first flat open with `map_select_ftue` unseen, anchor the real
`NMapSelectFtue.Create(control)` on our start node's rect, mark seen, and gate row-0
travel until confirmed — the same rule `NMapPoint.OnRelease` enforces.

### 3.15 Palette change (new 2026-08-01)
`BrightFor`: **Shop → pure gold**, **Unknown `?` → light blue**. Tune exact shades
in-game (one screenshot pass); update the §2.3 record and `requirements.md` colour list
when settled.

### 3.16 Base brightness revert to vanilla's tint table (approved 2026-08-01, was D1)
Adopt vanilla's `TargetColor` rules, try in-game and screenshot-judge:
- **Visited** (incl. current) = full natural art (the ink circle alone says "past");
- **Travelable** = full art (+ pulse only while travel is enabled, per vanilla);
- **All other unvisited rooms = 50 % alpha** (vanilla `halfTransparentWhite`);
- **EXCEPT** unreachable-and-unvisited rooms keep our stronger ghosting (§2.4), *and
  lose their coloured rims* — "no sense letting colour be active where no further
  decision shall ever be made" (maintainer, 2026-08-01).
Retires (supersedes 2026-07-30 directives): visited-slightly-dimmer, and the
current-node "done vs not-done" dimming — the marker carries "you are here", the ink
circle carries "done".

**SUPERSEDED 2026-08-02 (maintainer directive — "no bleedthrough")**: the 50 %-alpha
rule for unvisited rooms is retired. Every room's icon body is now the full opaque
dark art; state lives entirely in the surround:
- **future unvisited (non-frontier)** = opaque body + THICK type-colour band (the
  style travelable rooms previously wore);
- **frontier (travelable + travel enabled)** = opaque body, WHITE border, vanilla
  pulse; the border thickens on hover (§3.9 note);
- **visited** = opaque body + thin type rim + ink circle (unchanged);
- **ghosts (opt-in)** = the one remaining translucent state (unchanged).

---

## 4. Decisions still needed ([decide])

*(none open — D1→§3.16, D2→§2.12, D3→§2.13, all decided 2026-08-01)*

### D4 — (investigation, not a decision) Icon shader `map_color`
Vanilla's `_icon` renders through a `ShaderMaterial` with
`map_color = MapBgColor.Lerp(Gray, 0.5)`. We draw the raw texture. A/B screenshot the
same node type flat vs classic during batch 1; if interiors visibly differ, port the
material; else record as immaterial and close.

---

## 5. Implementation order (updated 2026-08-01)

Small, verifiable steps; every visual change gets a screenshot A/B against the classic
map of the same run (F makes this a one-key comparison).

1. **De-divergence & colour batch**: §3.9 (motion/border parity incl. white-ring
   removal), §3.10 hover-reaction swap, §3.15 palette, §3.16 brightness revert (+ D4
   A/B check).
2. **§3.3 quest icons** (information correctness).
3. **Look & feel batch**: §3.4 rotation, §3.5 sfx+fade, §3.7 act banner, §3.6 circle
   seeding + travel brushstroke.
4. **Info & integration batch**: §3.8 history tips, §3.10 legend tips/slide/controller,
   §3.11 marker rules, §3.13 top-bar oscillation, §3.14 FTUE.
5. Decided items from §4 as they resolve.

New hooks each step must be added to `HookCatalog` + `scripts/verify-hooks.sh`
(preflight-and-disable policy, `DEVELOPMENT.md`). Pure-logic pieces (seeding math) get
offline cases where applicable; visual placement follows the standard
one-tuning-pass-with-logs approach.

---

## 6. Alternative architecture under evaluation: the point modification (2026-08-01)

Instead of (or alongside) the parallel renderer, patch the **real** `NMapScreen` so the
vanilla map itself fits one screen, inheriting every behavior — including future game
features — automatically:

- **Tier A — pure scale (the prototype):** `_mapContainer` ("TheMap") holds *everything*
  (points, path dots, drawings, marker, votes, vfx). `NMapScreen._Process` rewrites its
  **Position** each frame but never **Scale**. So: set `Scale ≈ viewportH / ~2925`
  (fit-height, ~0.37), pin the position centered, and no-op `UpdateScrollPosition` +
  `ProcessScrollEvent` (also disables drag). Hit-testing follows the global transform,
  so hover/click/tips/votes/quests/drawings all just work. Hook surface: ~4–6 members
  vs the renderer's ~25. Known follow-ups: skip/adapt the start-of-act fly-down tween
  (assumes scroll geometry); verify drawings/remote-cursor alignment (their net-space
  routes through the unscaled `NMapBg`).
- **Tier B — reposition (middle rung):** keep the vanilla screen, but in a `SetMap`
  postfix reassign real `NMapPoint.Position`s from `MapLayout` lanes + compressed rows
  and rebuild the dot paths as `CreatePath` does. Restores lane compression and bigger
  nodes while still inheriting node behavior; costs more hooks and forks drawing coords.
- **Tier C — the parallel renderer (this repo's `flatmap/`):** full design freedom
  (colour boundaries, size language, one-screen layout), at the price of §3's work queue
  and re-earning every future vanilla feature by hand.

Maintainability ranks A > B > C. **Status (2026-08-01): the parallel renderer remains
the going concern and §5 proceeds; the Tier-A prototype stays recommended as a cheap
parallel evaluation** (build behind a config flag, screenshot-A/B on a real mid-act
save) — a Tier-A/B win would still make much of §3 moot, so sooner is cheaper.

---

## 7. Verified matched (no action)

- Node icon + outline art read live from the real nodes (incl. resolved-`?` art). ✓
- Revealed `?` type resolution via `MapPointHistory` (identical rule to `UpdateIcon`). ✓
- Frontier pulse formula `sin(t*4)*0.25+1.2`, independent phase per node. ✓
- Travelability: game's own `RecalculateTravelability` + `MapPointState.Travelable`
  (relic-aware), refreshed via the `SetTravelEnabled` postfix. ✓
- Travel routed through `OnMapPointSelectedLocally` (votes/actions identical). ✓
- Legend = the real borrowed panel at vanilla's x = 0.8 × width anchor. ✓
- Marker texture = the character's real `MapMarker` art (null in MP, matching vanilla's
  suppression). ✓
- Boss / second-boss encounter art resolution; both bosses drawn on two-boss floors;
  boss→second-boss travel step (handled by the game's own travelability). ✓
- Combat pause while the map is up (capstone container behavior, matches `Open`). ✓
- Start node: act's `Ancient.MapIcon` + outline, ink tint. ✓ (its travelable pulse is
  part of §3.9)
