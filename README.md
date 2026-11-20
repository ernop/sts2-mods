# One-Page Map & DeckView — Slay the Spire 2 UI mods

This repository contains two independent UI-only mods for Slay the Spire 2 (separate DLLs,
manifests, config files, and compatibility checks; install either or both):

- **One-Page Map** (`flatmap`) — displays the entire act map on one screen instead of scrolling
  vertically.
- **DeckView** (`deckview`) — shrinks cards in deck grid screens so more cards fit on screen at
  once; hovering a card enlarges it back to full size.

Neither mod changes cards, routes, travel rules, combat, rewards, saves, or any other gameplay.

| One-Page Map | DeckView |
|---|---|
| ![One-Page Map](docs/images/flat-map.png) | ![DeckView](docs/images/deck-view.png) |

> **Compatibility target: Slay the Spire 2 `v0.113.1`.**
>
> On startup, each mod checks every game hook and every game member its compiled code references.
> If a game update renames or changes a required member, the mod logs the mismatch, disables
> itself, rolls back Harmony patches, and leaves the default UI running.

## One-Page Map (`flatmap`)

Fits all floors of the current act (start node through boss, including two-boss acts) inside the
viewport without scrolling.

- **Uses the game's own map screen (`NMapScreen`).** Positions and scales the live `NMapPoint`
  nodes in place on a flat act-colored background (`Act.MapBgColor`). Keeps the game's own icons,
  legend, animated Spine bosses, map drawings (`NMapDrawings` + drawing tools), quest markers,
  visited-room hover tooltips, ink circles, current-position marker, shop music, and multiplayer
  cursors/votes.
- **Visual cues.** Room icons keep their original interior art with colored outlines by room type.
  Rooms you can currently travel to pulse with a white border (respects relics such as Wing Boots).
  Visited `?` rooms show the room type they resolved into. Hovering a legend entry highlights all
  rooms of that type.
- **Layout and paths.** Draws dashed paths between connected rooms. When **Compress** is checked
  (on by default), nodes are repositioned horizontally using a continuous layout solver
  (`ContinuousMapLayout` in `layout/MapLayout.cs`) and connected with curved dashed paths without
  altering room connectivity, swapping column order within a floor, or adding edge crossings.
  Unchecking **Compress** uses the game's original column positions with straight dashed paths.
- **Checkboxes (bottom-left, saved in `user://flatmap.cfg`):**
  - **Flat map** — switch between the one-page view and the default scrolling map.
  - **Compress** — toggle horizontal layout compression and curved paths (default: on).
  - **Hide unreachable nodes** — fade rooms that can no longer be reached from your current spot
    (default: off).
- **Controls:** **M** or the top-bar map button opens/closes the map; **F** (while the map is
  open) switches between one-page and scrolling views; **ESC**/Back closes the map. Works with
  mouse, keyboard, and controller.

Offline layout tests live in `layout/` (invariant checks, metrics, random/fan/two-boss/smooth map
tests, and captured act maps). A runtime check refuses to draw any layout that violates column
order or minimum separation.

## DeckView (`deckview`)

Shrinks cards in `NCardGrid`-based screens so more cards fit on screen at once.

- **Screens affected:** deck view, draw/discard/exhaust piles, card library, and deck
  card-selection screens (matched via an `NCardGrid` ownership allowlist).
- **Screens left at normal size:** combat hand, card inspect popup, card rewards, shop,
  choose-a-card, unlock, and card-bundle screens.
- **Hover behavior:** hovering a shrunk card enlarges it to full size; moving away restores it to
  shrunk size via a single pointer hover controller (controller/keyboard navigation uses the
  default game behavior).
- **Controls:** click the **Mini-cards** checkbox on any affected screen (hidden while peeking on
  card-select screens) or press **T** (ignored when typing in search boxes or when modals are
  blocking). Saved in `user://deckview.cfg`.

Tunables (`CardScaleFactor` 0.6, `CardPadding` 24, `ToggleDeckModeKey` T) are defined at the top
of `deckview/DeckViewMod.cs`.

## Requirements

- Slay the Spire 2 `v0.113.1`.
- No other mod dependencies. Uses the game's built-in mod loader.

## Install

Download the archives you want from
[GitHub Releases](https://github.com/ernop/sts2-mods/releases) (or subscribe on Steam Workshop:
[One-Page Map](https://steamcommunity.com/sharedfiles/filedetails/?id=3776618284)), then extract
into the game's `mods` directory:

```text
Slay the Spire 2/
└── mods/
    ├── flatmap/
    │   ├── flatmap.dll
    │   └── flatmap.json
    └── deckview/
        ├── deckview.dll
        └── deckview.json
```

Launch Slay the Spire 2 with Steam's **Play with Mods** option, enable the mod(s) in the **Mods**
menu, and restart when prompted.

## Build from source

Requires the .NET 9 SDK and an installed copy of Slay the Spire 2. Both projects reference the
game-provided `sts2.dll`, `GodotSharp.dll`, and `0Harmony.dll`; none are bundled.

```powershell
# Builds both mods and copies them into <game>\mods\<id>\
.\scripts\build.ps1 -Install
```

Or build individually:
`dotnet build flatmap/flatmap.csproj -c Release -p:Sts2Data="<path-to-data-dir>"` and
`dotnet build deckview/deckview.csproj -c Release -p:Sts2Data="<path-to-data-dir>"`.

Launch with `--nomods` to compare against the unmodified game.

See [`DEVELOPMENT.md`](DEVELOPMENT.md) for decompilation, preflight checks, and release gates, and
[`docs/requirements.md`](docs/requirements.md) for the product specification.

## Notes

- Targets Slay the Spire 2 `v0.113.1` (`TestedGameVersion` in source and `min_game_version` in
  each manifest).
- Both mods hook private game members, so every STS2 update requires rebuilding and verifying hooks
  (`scripts/verify-hooks.sh` and the startup preflight). If a hook is missing, the affected mod
  disables itself and leaves the default game UI active.
- To update for a new game version: rebuild against the new `sts2.dll`, bump `TestedGameVersion`
  and `min_game_version`, run `layouttest` and `scripts/check-metadata.py`, and test in-game.

## License

MIT License ([LICENSE](LICENSE)). Slay the Spire 2 and its assets are owned by Mega Crit.
Repository screenshots depict the game UI; release archives contain only each mod's DLL and
manifest.
