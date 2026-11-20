# Publishing FlatMap & DeckView

FlatMap `0.6.3` and DeckView `0.3.2` target Slay the Spire 2 `v0.113.1`. Only publish DLLs
built against and tested with that game version. (The pre-0.2.0 combined mod shipped as id
`deckview`; since 0.2.0 the map lives in `flatmap` and `deckview` is the deck zoom only.)

## 1. Pre-release checks

On a machine with STS2 installed:

```powershell
.\scripts\build.ps1 -Install
```

From WSL, verify every private hook against the installed game:

```bash
scripts/verify-hooks.sh
```

Launch STS2 with Steam's **Play with Mods** option and check mini-cards, hover, all checkboxes with mouse and controller,
flat/classic map switching, controller map travel, and ESC/back. New users must not produce
`MAPDUMP` log lines. The expected successful load lines start:

```text
[FlatMap] loaded — flat map view
[DeckView] loaded — card scale x0.6, padding 24px
```

If an update moved a hook, the affected mod logs `DISABLED`, applies no behavior, and the
vanilla UI continues. Do not publish that build for the new game version.

## 2. Build the installable zip

After the checks above:

```powershell
.\scripts\package.ps1
```

This creates `dist/flatmap-0.6.3-sts2-0.113.1.zip` and `dist/deckview-0.3.2-sts2-0.113.1.zip`
with SHA-256 files. Each archive contains `<id>/<id>.dll` + `<id>/<id>.json`.

Upload the files to a GitHub Release named `FlatMap 0.6.3 & DeckView 0.3.2 for STS2 v0.113.1`.
Never bundle `sts2.dll`, `GodotSharp.dll`, or `0Harmony.dll`.

## 3. GitHub discovery metadata

Repository description:

```text
Two UI-only Slay the Spire 2 mods: One-Page Map (whole act map on one screen) and DeckView (shrunk cards in deck grid views).
```

Repository topics:

```text
slay-the-spire-2  sts2  sts2-mod  godot  csharp  harmony  quality-of-life  deck  map
```

After publishing, set the repository website to the Steam Workshop page and link the GitHub
Release, Workshop page, and any Nexus listing from the README.

## 4. Steam Workshop

Download `ModUploader.exe` from
https://github.com/megacrit/sts2-mod-uploader/releases and run it once to create a workspace.

Copy into its `content/` directory:

- per mod: `flatmap/bin/flatmap.dll` + `workshop/content/flatmap.json`, and
  `deckview/bin/deckview.dll` + `workshop/content/deckview.json` (one Workshop item each)

Copy `docs/images/flat-map.png` (for `flatmap-workshop`) or `docs/images/deck-view.png`
(for `deckview-workshop`) to the workspace root as `image.png`. Keep each item's `workshop.json`
in `workshop/<id>-workshop/workshop.json` up to date (title, description, visibility, tags,
changeNote).

Use:

- **Titles:** `One-Page Map — Whole Act on One Screen` and `DeckView — Zoomed-Out Deck Views`
- **Tags:** `UI`, `Quality of Life`
- **Preview:** workspace-root `image.png` (must stay below the 1 MB uploader limit)
- **Visibility:** `public` for One-Page Map (`3776618284`), `private` for DeckView (`3776619975`)

Upload with:

```text
ModUploader upload -w workshop/flatmap-workshop
ModUploader upload -w workshop/deckview-workshop
```

Subscribe to the private item, remove the local `mods\deckview\` copy, and repeat the smoke
test before changing the Workshop item to public.

## 5. Updating later

1. Bump `version` in both manifests.
2. For a game update, change `TestedGameVersion` and both `min_game_version` values.
3. Run public CI, `verify-hooks.sh`, the in-game checklist, and `package.ps1`.
4. Publish the new GitHub Release and Workshop change note.
