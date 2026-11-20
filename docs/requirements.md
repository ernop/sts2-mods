# DeckView — requirements & product spec

Living record of everything this mod is meant to do, gathered across development. Status tags:
**[done]** shipped & in the build, **[done*]** shipped but wants in-game verification/tuning,
**[todo]** requested, not yet built.

DeckView is a Slay the Spire 2 (Godot + C# + Harmony) mod with two features: **mini-cards** (deck
views) and an **alternate map view**. Companion docs: `screen-system.md` (capstone/screen internals),
`DEVELOPMENT.md` (build + decompile), `vanilla-parity.md` (the complete flat-map ↔ vanilla-map
gap accounting and plan — including corrections to vanilla claims made below).
Cross-cutting policies in "Invariants" below are mandatory.

---

## 1. Product goals

- **Mini-cards:** STS2 draws deck-like screens at a size where few cards fit. Shrink them so more of
  the deck is visible at a glance; hovering a card still pops it to full size.
- **Map:** the game's map is a tall vertical scroll. Offer a compact, glance-able, whole-act-on-one-
  screen alternate view that is provably the *same* map — just laid out better.

---

## 2. Mini-cards (deck views)

- **[done]** Shrink cards in: deck view, draw/discard/exhaust piles, card library, deck card-select
  — scoped strictly via an **`NCardGrid` ownership allowlist** (`GridOwnership`: `InitGrid` depth
  counter + `NCardGrid` ancestor check), replacing the fragile screen denylist.
- **[done]** Leave at normal size: combat hand, inspect popup, choose-a-card, card-reward, unlock,
  shop, card-bundle, and any future/modded non-grid screens.
- **[done]** **Unified single-owner pointer hover** (`GridHoverGate`): on mini grids in pointer
  mode, rogue mouse-triggered `NGridCardHolder.OnFocus` / `NCardHolder.OnUnfocus` callbacks from
  overlapping enlarged hitboxes are suppressed and `GridHoverGate` drives both hover and unhover
  atomically from resting `SmallScale` slots (instant 0s hover/unhover, zero flicker, zero stuck-big
  cards, zero idle work on hidden grids). Controller/keyboard navigation runs 100% vanilla.
- **[done]** Visible **"Mini-cards" toggle** on **every shrunk screen family** — deck/simple view
  (above *View upgrades*), draw/discard/exhaust piles (above Back), card library (in filter
  cluster), and deck card-select screens (above Peek and hidden when peeking) — **on by default**,
  responsive on resize, proportional checkbox size (~24–28 px matching vanilla), hotkey **T**
  (guarded against text-input focus such as Card Library search, modifiers, transitions, and
  modals). Rebuilds live even when a grid has `CanScroll == false`. Persisted in `user://deckview.cfg`.

---

## 3. Map — the flat page (2026-11-19 architecture: in-place `NMapScreen`)

### Structure & integration (settled decision 2026-11-19)
- **[done]** Drives the **real `NMapScreen` in place** instead of maintaining a parallel
  `MiniMapScreen : ICapstoneScreen` or borrowing/reparenting vanilla controls (`_mapLegend`,
  `Drawings`, `_drawingTools`). Rationale: eliminates the global `M` hotkey hijack, broken
  `NMerchantRoom` shop-music signals, node-reparenting fragility, static boss fallback, missing
  visited-room history hover tips, missing multiplayer votes/pings/cursors, and per-frame redraws;
  every vanilla lifecycle, input, signal, VFX, and controller path stays 100% native.
- **[done]** Layout is **VERTICAL — the vanilla map's own orientation** (floors run bottom→top,
  start at the bottom, boss / second boss at the top). Every live `NMapPoint` is placed & scaled
  in place into a single viewport-responsive screen (no scrolling, no 3s start-of-act scroll tween,
  act banner & first-run FTUE preserved), with weighted row spacing, a calm widescreen aspect-ratio
  cap (`laneSpacing <= rowStep * 1.55f`), and `Compress` (**on by default**, 2026-11-19):
  - **Compress ON (default):** smooth continuous vertical-rail & curvature QP layout
    (`MapLayout.AssignSmoothX`) with rim-fanned smooth cubic Bézier dashed footpaths (dead-straight
    vertical dashes when `|dx| < 2px`, smooth S-curves on forks/merges/fans; supports single-boss
    and two-boss acts via pinned Start/Boss/SecondBoss spine).
  - **Compress OFF:** exact 1:1 vanilla columns with straight dashed footpaths (same calm aspect
    ratio, connectivity untouched).
- **[done]** Native `NMapDrawings` keeps its vanilla `Size` untouched and receives an exact affine
  `(Position, Scale)` transform aligning vanilla node bounds onto flat node bounds, so strokes,
  erasing, co-op remote cursors, and `F`-mode flips align cleanly without reparenting.
- **[done]** `NMapShareButton` temporarily restores Classic layout while taking a map screenshot
  and re-applies Flat mode automatically when done.
- **[done]** Light event-driven overlays on `NMapScreen` (`FlatBackdrop`, `FlatPathsLayer`,
  `FlatChromeOverlay`): zero per-frame redraws; bottom-left switches sit cleanly above `_backButton`
  with a fixed `"Flat map"` position across modes and automatic pure-black/pure-white text contrast
  against `Act.MapBgColor` vs. dark Classic backstop.

### Node appearance (design directives of 2026-07-28 — the intended way we draw the map)
- **[done*]** **Background = the act's own map colour** (`Act.MapBgColor`), so the game's icon art
  sits on exactly the surface it was drawn for.
- **[done*]** Each node = two layers: the icon's **main body** (the game's `_outline` texture — a
  dilated solid mask of the icon shape) **redrawn in our scheme colour, slightly lightened**,
  drawn BEHIND; the game's icon art on top with its **NATURAL interior colours** (never tint the
  interior — that made campfires black). So the node's outline is the actual item shape carrying
  the type colour, and the interior looks exactly like the official map. Icons drawn LARGE.
- **[done*]** The original game's **size language**: elites draw larger (×1.35), boss largest
  (×1.9), start enlarged (×1.3). Applied to drawing, hover hit-testing, and controller focus rects
  alike (`TypeScale`/`RadiusOf`, keyed on the effective type so a revealed `?`-elite grows too).
- **[done*]** The **proper boss icon** (never a letter/ball): every boss has unique icon art at
  `ui/run_history/{bossid}.png` (+`_outline`), resolved via
  `ImageHelper.GetRoomIconPath(Boss, Boss, EncounterModel.Id)` — the map's animated Spine boss has
  no static png, so this is the right source. Second-boss floors use `SecondBossEncounter`;
  fallbacks: `BossNodePath + ".png"` placeholder art, then glyph.
- **[done*]** The **proper start icon** (`Act.Ancient.MapIcon` via the live node), sized up (×1.3).
- **[done]** Colors: monster **red**, elite **purple**, shop **yellow**, camp/rest **green**,
  treasure **orange**, unknown **grey**, start **teal**, boss **red**.
- **[done]** Two-boss levels draw both bosses (both are in the point dictionary).
- **[done*]** **Legend = the game's own Legend panel, used exactly**: the real `NMapScreen`
  `_mapLegend` control is **borrowed** onto the flat page while it's open (vanilla anchor,
  x = width × 0.8) and returned to the classic screen untouched on close/disable.
- **[done]** **Quest markers (Fur Coat / Spoils Map)** (2026-08-08): any node with
  `MapPoint.Quests.Count > 0` shows vanilla's red-X `%QuestIcon` (`map_spoils_map_marker`)
  upper-right. Winged Boots already via relic-aware `Travelable`.
- **[done]** **Map drawings** (2026-08-08): the real `NMapDrawings` + DrawingTools are
  borrowed onto the flat page — same draw / erase / clear / right-middle-click path as
  classic (`NMapDrawingInput`).
- **[done*]** **Legend-hover pulse** (the original's lovely detail, 2026-07-30): mousing over a
  legend item toggles the same **continuous pulse** used for travelable nodes, for **every node
  of that type**. Implemented by hit-testing the borrowed real `NMapLegendItem`s (their fixed node
  names carry the type mapping) on the page tick. **No state-cue row** in or near the legend:
  the display itself carries the states (motion + the marker arrow — see "Node state clarity").
- **[done*]** **Connections use vanilla's dashed-footpath style** (`DrawDashedLine`), in the act's
  own path palette (`MapTraveledColor` walked / `MapUntraveledColor` open, faded when dead),
  trimmed to stop at each node's edge instead of running underneath.
- **[done*]** **The taken path is stamped with vanilla's Japanese ensō-style ink
  circle/chevron** (2026-07-30):
  `NMapCircleVfx`'s settled frame (`map_circle_4.tres` — frames 0..3 are its flipbook) drawn
  around every visited node except the start, with vanilla's own per-node deterministic rotation
  and 0.85–0.90 scale jitter (alpha 0.95), behind the icon. Reproduces the classic map's journal
  look exactly.
- **[done*]** **No "Floor N" label** and **no visited tally / "none visited yet"** — the
  visualization carries that information itself.

### Node state clarity
- **[done*]** **Current position: the red marker arrow ONLY** (2026-07-30 — "remove all
  highlighting from all nodes": the blue glow and blue double-ring are gone). The game's own
  per-character `MapMarker` texture points at the node **from the left side**, rotated onto its
  side and kept small (1×r wide) — above-the-node it obscured the node one floor up. Falls back
  to a small chevron, also from the left, if the marker art is absent.
- **[done]** **Current node "done vs not-done" (gated on `NMapScreen.IsTravelEnabled`):**
  - *Room finished, travel enabled* → the current node reads as a **done** node (dimmed like your
    past rooms); attention shifts to the highlighted next options.
  - *Room NOT finished yet* → the current node stays **full/active**, so it's obvious your next
    action is *within* the current node. It is not dimmed-as-done.
- **[done*]** **Start:** drawn exactly as the vanilla map draws it (2026-07-30): the dark ink
  illustration (icon tinted the act's traveled/ink colour) with its **pale outline stroke**,
  enlarged (×1.45), in EVERY style — **no invented rings or recolours**. Position + size + unique
  art are its cues.
- **[done*]** **MOTION is the primary attention language** (the current node has no
  halo/ring/glow; permanent white borders are reserved for actionable next nodes):
  - Hovering **ANY node** (past, present or future) subtly **swells it and holds it large** under
    the mouse; moving away lets it settle back **gradually** (fast in ~0.15s, out ~0.35s) — the
    vanilla map's hover feel. Visual only; hitboxes unchanged.
  - The **1+ nodes you may travel to RIGHT NOW always have WHITE borders and continuously
    pulse** using the real map's exact scale function (`sin(elapsed × 4) × 0.25 + 1.2`, independent
    phase per node). Shown only when travel is enabled. The set is the game's own **relic-aware**
    `MapPointState.Travelable` (Wing Boots respected automatically). White is the game's
    "next-step option" language, RESERVED for these borders (never a room-type rim).
  - Hovering a **legend row** toggles the continuous pulse for **all nodes of that type**.
  - Driven by a per-frame SceneTree tick that runs only while the page is open.
- **[done]** **Unreachable-and-unvisited** rooms: greyed out; their edges fade. Reachability =
  forward BFS seeded from {current ∪ every Travelable node}, so with Wing Boots more stays lit, and
  without them parallel/future tracks you can't currently reach are greyed.
- **[done*]** **Visited (past)** rooms are **visibly and clearly the same colour/rim/art as the
  live future nodes, just a bit dimmer** (re-affirmed 2026-07-30 after an ink-silhouette
  experiment proved far too dark — the trail's position already says "past"). Utterly unreachable
  rooms remain the strongly-faded ghosts.
- **[done*]** The `?` rim colour is warm coin-gold, never white/near-white — a white band reads as
  a UI element, not a room type (2026-07-30).
- **Sizing policy (2026-07-30):** it is always OK to **shrink large icons as needed** — this mod's
  whole point is fitting the act on one screen. When icon size conflicts with layout clarity
  (visible connections between stacked nodes, marker/neighbour crowding, top-bar clearance),
  shrink rather than fight.
- **[done]** A visited **`?` node adopts its revealed room** (item F): resolved via
  `_runState.MapPointHistory[act][row].Rooms.First().RoomType`, so it re-colours + tallies as the real
  room (monster/shop/elite/treasure/rest), matching the game's on-entry reveal. The live node already
  carries the resolved icon art. The old "been here" dot is gone — dimming + the resolved icon now
  signal "visited".

### Look experiment (2026-07-29 → resolved 2026-07-30)
- **[done]** Six named looks were built and cycled in-game with S. **Neon Rims won and is now THE
  style**; the other five, the S-cycler, the HUD line, and the `map.style` config slot are all
  **removed**. Along the way the rims' coloured area was **expanded slightly** (thicker offset
  passes on live nodes), per directive.

### Interaction
- **[done]** Hover highlights a node (pointer cursor over travelable ones).
- **[done]** Click a **travelable** node → travels via the game's own selection path
  (`OnMapPointSelectedLocally`), then closes the page so the animation plays on the real map.

### Info panel (bottom-right)
- **[done]** Act name, current floor, and a per-type tally of rooms visited so far.
- **[done]** The tally uses the actual room **icons** (the game's own art) + counts, right-aligned,
  instead of letter labels (item E). Counts use the effective type, so revealed `?`s tally as their
  real room.

### Bottom-left labels
- **[done]** Removed the "MAP" subtitle (item D) and the "O or Esc: back to the map" hint (item C);
  the bottom-left is now clear. Page identity comes from the info panel's act name.

### Style toggle & keys
- **[done]** **"Flat map" toggle** on BOTH the classic map and the flat page, at the same bottom-left
  spot (reads as one control), never bleeding onto other screens (hidden when any capstone is up).
- **[done]** **"Compress" toggle** on the flat page, **on by default**: off = raw layout 1:1 with the
  game's columns (proves compression changed nothing but spacing); on = compacted.
- **[done*]** Exactly **two** checkboxes on the flat page — **"Flat map"** and **"Compress"** —
  stacked bottom-left, **fully left-aligned (hard against the screen edge)** and **flush to the
  bottom**, each exactly one line below the previous (measured heights, no blank lines). The
  classic map's "Flat map" toggle shares the same hard-left x. (A third "Vertical" toggle existed
  briefly on 2026-07-28; retired the same day when vertical became the only orientation.)
- **[done*]** **The standard map controls always work**: the top-bar map button (next to the deck),
  the map room, and the M key all toggle the map open/closed in either mode. A press while the map
  is shown hides it and returns to the game state underneath; a press while it is hidden shows it.
  M runs the mod's own toggle. The button keeps the game's `NTopBarMapButton.OnRelease`, which picks
  close-or-open from `IsOpen()`, i.e. the classic map's `Visible`; a postfix also counts the flat
  page as open, so a click while it is up closes it through OnRelease's capstone branch.
  History (2026-11-15): since STS2 v0.113, OnRelease closes every capstone before it calls
  `Open(isOpenedFromTopBar: true)`. The earlier mechanism, an `Open` prefix that treated Open()
  while our page was current as a close, therefore never saw the page, and a click closed and
  reopened it. An `Open()` while the flat page is up is now a no-op, as vanilla `Open()` is while
  `IsOpen`: `RunManager.Proceed` calls it to start travel whether or not the map is showing.
- **[done]** **Flat and classic are two co-equal MODES of the one map, never layered** — the
  load-bearing design rule (night-mode/day-mode analogy). The map is a single slot with two
  renderings; exactly one is ever open. Opening renders it *according to the current mode*; the other
  mode never appears — no flash, no fall-back, no "toggle-after-loading."
- **[done]** Implemented as a **prefix on `NMapScreen.Open`**: whenever anything opens the map (map
  room, top-bar button, our M key), if flat mode is on we render the flat page and **skip the classic
  `Open()` entirely** — the classic `NMapScreen` is *never opened* in flat mode (`IsOpen` stays
  false). Nothing underneath to bleed through or fall back to.
- **[done]** The flat page refreshes its **own** travelability (`RecalculateTravelability` via
  reflection) and reads the point dictionary directly — both exist from act start
  (`SetActInternal → GenerateMap → SetMap`) without the classic map ever opening; travel works closed
  (`OnMapPointSelectedLocally` ignores `IsOpen`). A `SetTravelEnabled` postfix rebuilds the flat page
  in place so its "can move here" highlights stay correct.
- **[done]** The two checkboxes (**Flat map** / **Compress**) are the saved single source of truth
  for the mode.
- **[done]** **M** is the only global shortcut (caught on key press in
  `NInputManager.ProcessHotkeyInput`, where the game turns keys into actions; its release is
  swallowed there too, so the game's own map action never fires for M, neither press nor release):
  from any screen it opens the map in the current mode, or closes it to the **prior view**.
  (2026-11-15: the release used to pass through as a `mega_view_map` release; the map button acts on
  any release that follows a press it registered earlier, such as a mouse press on the icon let go
  elsewhere, so that one M press could toggle the map twice and leave it unchanged.) **ESC/back** likewise exits the whole map — in flat mode the classic map was never
  opened, so falling back to it is *structurally impossible*. **F** — only while a map is showing —
  **instantly flips** flat↔classic in place (2026-07-30; was O). The two views are NOT layers:
  they are two display styles of the ONE map slot, so from either view M / the map button / ESC
  dismiss the whole map, and F merely swaps the rendering.
- **[done]** All toggles are one self-drawn `ToggleSwitch` (game checkbox art, shared font size,
  consistent positions) — item C "all UIs match".

### Compaction algorithm
- **[done]** **Hard invariant:** every layout preserves each floor's column order and minimum lane
  separation (`x[i+1] - x[i] >= 1.0`, so zero overlaps and zero added crossings; connectivity is
  never modified). A runtime assert (`LayoutInvariants.CheckContinuous`) refuses to draw an illegal
  layout.
- **[done]** **Continuous smooth vertical-rail & curvature QP (2026-11-19, `ContinuousMapLayout` /
  `MapLayout.AssignSmoothX` in `layout/MapLayout.cs` — shipped in `flatmap` 0.6.0 as `Compress` ON,
  default ON).** Replaced discrete integer DP as the active `Compress` layout after side-by-side
  evaluation showed integer quantization caused jagged 3-floor bends and lopsided forks, while
  uncapped ultrawide horizontal stretching flattened diagonals into ~76° slashes. Solved via
  adaptive-restart FISTA with exact O(k) PAV isotonic-separation projection per row:
  - **Hard:** exact connectivity, strict within-row order with `>= 1.0` separation (0 overlaps,
    0 added crossings), canonical `(Row, Col)` determinism (bit-for-bit order-independent), and
    aligned Start / Boss / SecondBoss spine (`x = 0`).
  - **Soft:** pseudo-Huber vertical-rail pull on body edges (snaps near-vertical chains dead-straight),
    Euler–Bernoulli second-derivative curvature penalty `(x_{r+1} - 2x_r + x_{r-1})^2` across 3-floor
    through-paths (eliminates sawtooth zigzags and centers forks/merges), `UnlinkedGap = 1.15` subtle
    visual separation between adjacent rooms that share neither parent nor child, and gentle compactness.
  - **Rendering (`FlatMapLayoutEngine` + `FlatPathsLayer`):** `laneSpacing <= rowStep * 1.55f` calm
    aspect-ratio cap across 16:9 and 21:9 displays, paired with rim-fanned cubic-Hermite Bézier
    S-curve dashed paths (arc-length parameterized for uniform dashes; straight vertical dashes when
    `|dx| < 2px`). Cuts mean through-turn angle from 50.5° → 29.5° (-70.7% bend energy) with 0
    illegal, 0 crossing regressions, 0 spine misalignments, and 0 order leaks across 300 random maps
    and captured real acts.
- **[done]** **Discrete exact DP optimizer (2026-11-16, `OptimalLayout` / `MapLayout.AssignLanes`)**
  remains kept and regression-tested in `layout/MapLayout.cs` (500 + 300 + 200 property/fan/two-boss
  tests). `dotnet run --project layout/layouttest.csproj -- viz-file <dump>` inspects any level
  (make a dump from a run save with `scripts/save2dump.py <current_run.save>`).

---

## 4. Invariants & policies (mandatory)

- **Preflight and disable safely.** Validate every private/string-named hook, and every game
  member the compiled code references, before patching.
  Missing members or setup failures log a clear incompatibility and preserve the vanilla UI;
  never leave a partially enabled feature.
- **View-only / connectivity sanctity.** Never modify the real map's data or nodes; the moves we draw
  ARE the vanilla player's moves. We only reassign *display lanes*. Never overlap two nodes (would
  hide a choice). A runtime assert crashes rather than draw an illegal layout.
- **Never persist to the game's save.** Our only file is `user://deckview.cfg` (our preferences).

---

## 5. Tooling & process

- **Build/decompile:** Windows dotnet from WSL; `ilspycmd` for decompiling `sts2.dll`. See
  `DEVELOPMENT.md`.
- **Offline layout harness** (`layout/`, no game): pure `MapLayout` algorithm + invariant checker +
  metrics + a 500-map property test + curated cases + a **viz tool** (`layouttest -- viz` renders any
  captured level and compression options as ASCII). This is where the algorithm is proven before it
  ships.
- **`MAPDUMP`** logging: developer opt-in only via `[debug] dump_map_graph=true` in
  `user://deckview.cfg`; it is off for new and upgraded users.
- **Diagnostics:** toggle positions/sizes/font-size and per-open state are logged so one play-test
  pinpoints tuning. Screenshots load from `c:\screenshots`.
- **Reliability principle:** pure logic → offline tests (gold standard); game-integration → verify
  hooks + runtime asserts + loud logs; visual/positional → accept one tuning pass, made cheap by logs.

---

## 6. Iteration batch (from the Overgrowth screenshot) — all shipped

- **[done*] A** Visited nodes keep colour, partially dimmed.
- **[done*] B** "You are here" = the game's native map-marker arrow beside the node (was a gold
  ring; originally above the node, moved to the left side and shrunk 2026-07-29).
- **[done*] C** Removed the "O or Esc: back to the map" hint text.
- **[done*] D** Removed the "MAP" subtitle.
- **[done*] E** Info-panel tally uses real room icons + counts.
- **[done*] F** Visited `?` nodes re-colour/re-icon to their revealed room (via `MapPointHistory`).

Research notes (from decompiling `sts2.dll`, for future reference): the game has **no dedicated reveal
morph** — `NNormalMapPoint.UpdateIcon()` swaps the icon texture instantly; the surrounding flourish is
`OnSelected()`'s scale/colour tween + the `NMapCircleVfx` ink-circle (`map_circle_0..4.tres`) +
`NMapNodeSelectVfx` brush-burst. Resolved-`?` art is a distinct set
(`map/icons/map_unknown_{monster,shop,elite,chest}.tres`). The current-location marker is `NMapMarker`
(a `TextureRect` = `Character.MapMarker`), single-player only, popped in with an elastic tween. The
selectable frontier isn't a separate vfx — reachable `NNormalMapPoint`s self-pulse in `_Process`.
