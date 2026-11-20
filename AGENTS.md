# stsmod2: agent entry point

Two independent, visibility-only Slay the Spire 2 mods (Godot 4, C#, Harmony):

- **One-Page Map**, id `flatmap`, source `flatmap/`. Public Steam Workshop item (`3776618284`).
- **DeckView**, id `deckview`, source `deckview/`. Public Steam Workshop item (`3776619975`).

Remote: [ernop/sts2-mods](https://github.com/ernop/sts2-mods), public.

| Document | Contents |
|---|---|
| [README.md](README.md) | What each mod does, controls, compatibility target |
| [docs/requirements.md](docs/requirements.md) | Product requirements and invariants |
| [DEVELOPMENT.md](DEVELOPMENT.md) | Toolchain, decompiling the game, compatibility preflight, strict vs public builds |
| [PUBLISHING.md](PUBLISHING.md) | Release gates, packaging, GitHub Release, Workshop upload |
| [docs/screen-system.md](docs/screen-system.md), [docs/vanilla-parity.md](docs/vanilla-parity.md) | Game internals the map relies on |

## Test

From the repo root, the two gates CI runs (`.github/workflows/ci.yml`):

```powershell
dotnet run --project layout/layouttest.csproj --configuration Release   # last line: ALL GATES PASSED
python scripts/check-metadata.py                                        # metadata OK: ...
```

## Install into the local game (this project's restart step)

Close Slay the Spire 2 first; the running game has the mod DLLs loaded.

```powershell
.\scripts\build.ps1 -Install
```

This builds both mods against the installed game's `sts2.dll` and copies each DLL and manifest
into `<game>\mods\<id>\`. On the next launch, `%APPDATA%\SlayTheSpire2\logs\godot.log` must show
`[FlatMap] loaded` and `[DeckView] loaded` and no `DISABLED` line.

## After a game update

Rebuild and reinstall even when no mod code changed. The compiled DLLs call the game's public
methods by exact signature, so an update that changes one breaks an old build: the startup
preflight then turns that mod off with a `DISABLED` log line. (Before that check existed, STS2
v0.113.0 changed `NHotkeyManager`'s hotkey methods and the v0.110.1 One-Page Map build left the
map stuck open, 2026-11-12.) Then follow `PUBLISHING.md`, "Updating later".

## Ship first (settled workflow, 2026-11-20)

Push, create GitHub Releases, and upload Steam Workshop updates directly once local build/test
gates pass — do not pause to ask for permission first. Ernest checks things himself afterward and
we fix anything quickly if needed.
