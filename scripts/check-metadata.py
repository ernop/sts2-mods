#!/usr/bin/env python3
"""Public-CI checks that do not require proprietary STS2 assemblies.

The repo ships TWO independent mods: FlatMap (map view, repo root) and DeckView
(deck zoom, deckview/). Each has a root manifest, a synchronized Workshop manifest,
and a TestedGameVersion constant that must agree with min_game_version.
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

MODS = (
    {
        "id": "flatmap",
        "name": "FlatMap",
        "manifest": "flatmap/flatmap.json",
        "source": "flatmap/FlatMapMod.cs",
    },
    {
        "id": "deckview",
        "name": "DeckView",
        "manifest": "deckview/deckview.json",
        "source": "deckview/DeckViewMod.cs",
    },
)


def fail(message: str) -> None:
    print(f"metadata check failed: {message}", file=sys.stderr)
    raise SystemExit(1)


manifests: dict[str, dict] = {}
for mod in MODS:
    root_manifest = json.loads((ROOT / mod["manifest"]).read_text(encoding="utf-8"))
    workshop_path = ROOT / "workshop/content" / f"{mod['id']}.json"
    workshop_manifest = json.loads(workshop_path.read_text(encoding="utf-8"))
    if root_manifest != workshop_manifest:
        fail(f"{mod['manifest']} and workshop/content/{mod['id']}.json differ")
    if root_manifest.get("id") != mod["id"]:
        fail(f"{mod['manifest']} id is not '{mod['id']}'")
    manifests[mod["id"]] = root_manifest

    source = (ROOT / mod["source"]).read_text(encoding="utf-8")
    match = re.search(r'TestedGameVersion\s*=\s*"v([^"]+)"', source)
    if not match:
        fail(f"could not find TestedGameVersion in {mod['source']}")
    if match.group(1) != root_manifest["min_game_version"]:
        fail(f"{mod['name']}: TestedGameVersion and min_game_version differ")

    if root_manifest.get("affects_gameplay") is not False:
        fail(f"{mod['name']} must remain marked visibility-only")
    description = root_manifest.get("description", "").lower()
    for phrase in ("slay the spire 2", "does not change", "gameplay"):
        if phrase not in description:
            fail(f"{mod['name']} manifest description is missing '{phrase}'")

if manifests["flatmap"]["min_game_version"] != manifests["deckview"]["min_game_version"]:
    fail("the two mods target different game versions")

for relative in ("README.md", "DEVELOPMENT.md", "PUBLISHING.md", "docs/requirements.md"):
    text = (ROOT / relative).read_text(encoding="utf-8")
    # "work or crash" is intentionally NOT flagged: DEVELOPMENT.md legitimately documents the
    # strict dev-build failure mode (PUBLIC_BUILD controls the public revert-with-warning).
    for stale in ("v0.108.0", "ToggleOffset", "MiniMapView"):
        if stale.lower() in text.lower():
            fail(f"{relative} contains stale text '{stale}'")

game_version = manifests["flatmap"]["min_game_version"]
for relative in ("README.md", "DEVELOPMENT.md", "PUBLISHING.md"):
    text = (ROOT / relative).read_text(encoding="utf-8")
    if f"v{game_version}" not in text:
        fail(f"{relative} does not mention current STS2 v{game_version}")

publishing = (ROOT / "PUBLISHING.md").read_text(encoding="utf-8")
for mod_id, manifest in manifests.items():
    expected_archive = f"{mod_id}-{manifest['version']}-sts2-{game_version}.zip"
    if expected_archive not in publishing:
        fail(f"PUBLISHING.md does not name current archive {expected_archive}")

for relative in ("docs/images/deck-view.png", "docs/images/flat-map.png"):
    size = (ROOT / relative).stat().st_size
    if size >= 1_000_000:
        fail(f"{relative} is {size} bytes; Workshop preview must be below 1 MB")

if not (ROOT / "LICENSE").is_file():
    fail("LICENSE is missing")
map_source = (ROOT / "flatmap/FlatMapMod.cs").read_text(encoding="utf-8")
if "DumpMinimapGraph = true" in map_source:
    fail("production map dumps are enabled")

print(
    "metadata OK: "
    + " / ".join(f"{m['name']} {manifests[m['id']]['version']}" for m in MODS)
    + f" / STS2 {game_version}"
)
