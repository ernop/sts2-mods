#!/usr/bin/env python3
"""Render map-layout options as connector-showing PNGs for by-eye evaluation.

Reads the JSON emitted by `dotnet run -- viz-json` (stdin or a path arg) and writes one PNG per
option plus a stacked comparison image. Nodes are colored by room type; edges are drawn as real
line segments so flat / diagonal / spike / plateau shapes are visible at a glance.

    dotnet run --project layout/layouttest.csproj -- viz-json | python3 scripts/render_mapviz.py
"""
import json
import sys
import os
from PIL import Image, ImageDraw, ImageFont

# Kept inside the checked-out repo (gitignored) so the images live next to the code.
OUT_DIR = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "layout", "mapviz")

# Room type -> fill color (matches the in-game palette from docs/requirements.md).
TYPE_COLOR = {
    "Monster":  (208, 66, 66),    # red
    "Elite":    (150, 78, 200),   # purple
    "Shop":     (226, 190, 66),   # yellow
    "RestSite": (86, 176, 96),    # green
    "Treasure": (226, 140, 60),   # orange
    "Unknown":  (150, 150, 158),  # grey
    "Ancient":  (70, 200, 200),   # teal (start)
    "Boss":     (150, 30, 30),    # dark red
}

BG = (24, 26, 34)
EDGE_FLAT = (150, 210, 150)      # green-ish: a flat connector (good)
EDGE_SLOPE = (235, 170, 90)      # orange: a diagonal (a step up/down)
EDGE_STEEP = (235, 90, 90)       # red: a steep 2+ lane jump (bad)
NODE_R = 13
X_STEP = 92
Y_STEP = 64
MARGIN_X = 60
MARGIN_TOP = 70
MARGIN_BOTTOM = 30


def font(sz):
    for p in ("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
              "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf"):
        if os.path.exists(p):
            return ImageFont.truetype(p, sz)
    return ImageFont.load_default()


def render_option(opt):
    nodes = opt["nodes"]      # [floor, lane, type]
    edges = opt["edges"]      # [f_floor, f_lane, t_floor, t_lane]
    max_floor = max(n[0] for n in nodes)
    min_lane = min(n[1] for n in nodes)
    max_lane = max(n[1] for n in nodes)
    lanes = max_lane - min_lane

    W = MARGIN_X * 2 + max_floor * X_STEP
    H = MARGIN_TOP + MARGIN_BOTTOM + lanes * Y_STEP
    img = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(img)

    def px(floor, lane):
        return (MARGIN_X + floor * X_STEP, MARGIN_TOP + (lane - min_lane) * Y_STEP)

    # edges first (under nodes)
    for ff, fl, tf, tl in edges:
        x1, y1 = px(ff, fl)
        x2, y2 = px(tf, tl)
        span = abs(tl - fl)
        col = EDGE_FLAT if span == 0 else (EDGE_SLOPE if span == 1 else EDGE_STEEP)
        d.line([(x1, y1), (x2, y2)], fill=col, width=3)

    # nodes
    for floor, lane, typ in nodes:
        x, y = px(floor, lane)
        c = TYPE_COLOR.get(typ, (180, 180, 180))
        d.ellipse([x - NODE_R, y - NODE_R, x + NODE_R, y + NODE_R], fill=c, outline=(15, 16, 22), width=2)

    # title + metrics
    d.text((MARGIN_X, 14), opt["name"], fill=(240, 240, 245), font=font(22))
    d.text((MARGIN_X, 42), opt["metrics"], fill=(170, 175, 190), font=font(16))
    return img


def render_map(m):
    """One stacked comparison PNG for a map: its options top-to-bottom."""
    imgs = [render_option(opt) for opt in m["options"]]
    w = max(im.width for im in imgs)
    gap = 16
    total_h = sum(im.height for im in imgs) + gap * (len(imgs) - 1)
    combo = Image.new("RGB", (w, total_h), (10, 11, 15))
    y = 0
    for im in imgs:
        combo.paste(im, (0, y))
        y += im.height + gap
    return combo


def main():
    # utf-8-sig: tolerate the BOM PowerShell redirects prepend to the dotnet output.
    raw = (open(sys.argv[1], encoding="utf-8-sig").read() if len(sys.argv) > 1
           else sys.stdin.read().lstrip("﻿"))
    data = json.loads(raw)
    os.makedirs(OUT_DIR, exist_ok=True)
    # Fresh run: clear old PNGs so removed maps don't linger.
    for f in os.listdir(OUT_DIR):
        if f.endswith(".png"):
            os.remove(os.path.join(OUT_DIR, f))

    maps = data["maps"] if "maps" in data else [data]
    for m in maps:
        combo = render_map(m)
        path = os.path.join(OUT_DIR, f"{m['name']}.png")
        combo.save(path)
        print(f"wrote {path}  ({combo.width}x{combo.height})")
    print(f"\n{len(maps)} maps -> {OUT_DIR}")


if __name__ == "__main__":
    main()
