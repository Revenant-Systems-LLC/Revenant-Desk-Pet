"""
Render the pet's sprite clips down to terminal cells for his terminal bodies
(the Claude Code mod, the console app). Reads the embedded atlas
(src/DesktopPet/Assets/<pet>.atlas.png + .json, the same files the exe uses) and
writes mod/revbot/cells.json.

A terminal cell is about twice as tall as it is wide, so each cell holds two
pixels stacked: the top half drawn with the upper half block (U+2580) in the
foreground colour, the bottom half as the background colour. A cell with only
its lower pixel set uses the lower half block (U+2584). Colours are quantised
to one palette of at most 63 entries so every cell is three characters in the
JSON: glyph, foreground index, background index (index 0 = the terminal's own
colour, i.e. transparent).

    python tools/build_cells.py            # RevBot, 12/16/24 columns
    python tools/build_cells.py --pet damien --columns 16
"""
import argparse
import json
import math
import os
import sys

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
ASSETS = os.path.join(ROOT, "src", "DesktopPet", "Assets")
OUT = os.path.join(ROOT, "mod", "revbot", "cells.json")

# What the terminal bodies play. Everything else stays on the desktop.
CLIPS = [
    "idle", "fidget_blink", "happy", "wave", "think", "shrug", "waiting",
    "failed", "working", "review", "doze_off", "sleeping", "wake", "walk_right", "jump",
]
ALPHABET = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-_"
UPPER, LOWER, FULL, EMPTY = "▀", "▄", "█", " "


def load(pet):
    prefix = "atlas" if pet == "revbot" else f"{pet}.atlas"
    with open(os.path.join(ASSETS, prefix + ".json"), encoding="utf-8") as f:
        manifest = json.load(f)
    sheet = Image.open(os.path.join(ASSETS, prefix + ".png")).convert("RGBA")
    return manifest, sheet


def frames_by_row(manifest):
    rows = {}
    for f in manifest["frames"]:
        rows.setdefault(f["row"], {})[f["index"]] = f
    return rows


def extent(manifest, rows, clips):
    """The box, relative to the anchor (the ground point under the pet), that holds every exported frame."""
    left = top = math.inf
    right = bottom = -math.inf
    for name in clips:
        clip = manifest["clips"].get(name)
        if clip is None:
            continue
        for idx in clip["frames"]:
            f = rows[clip["row"]][idx]
            left = min(left, f["ox"])
            top = min(top, f["oy"])
            right = max(right, f["ox"] + f["w"])
            bottom = max(bottom, f["oy"] + f["h"])
    return int(left), int(top), int(right), int(bottom)


def premultiply(img):
    """RGBA -> premultiplied RGBA, so averaging never bleeds black from transparent pixels."""
    arr = img.tobytes()
    pre = bytearray(len(arr))
    for i in range(0, len(arr), 4):
        al = arr[i + 3]
        pre[i] = arr[i] * al // 255
        pre[i + 1] = arr[i + 1] * al // 255
        pre[i + 2] = arr[i + 2] * al // 255
        pre[i + 3] = al
    return Image.frombytes("RGBA", img.size, bytes(pre))


def unpremultiply_pixel(p):
    pr, pg, pb, pa = p
    if pa < 128:
        return None
    return (min(255, pr * 255 // pa), min(255, pg * 255 // pa), min(255, pb * 255 // pa))


def render_frame(sheet, f, box, columns, rows):
    """One frame as (glyph, fg_rgb_or_None, bg_rgb_or_None) per cell, row-major."""
    left, top, right, bottom = box
    width, height = right - left, bottom - top
    canvas = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    crop = sheet.crop((f["x"], f["y"], f["x"] + f["w"], f["y"] + f["h"]))
    if f.get("mirror"):
        crop = crop.transpose(Image.FLIP_LEFT_RIGHT)
    canvas.alpha_composite(crop, (f["ox"] - left, f["oy"] - top))
    # Two pixels per cell: a columns x (rows*2) image, box-filtered on premultiplied colour.
    small = premultiply(canvas).resize((columns, rows * 2), Image.BOX)
    px = small.load()
    cells = []
    for r in range(rows):
        for c in range(columns):
            t = unpremultiply_pixel(px[c, 2 * r])
            b = unpremultiply_pixel(px[c, 2 * r + 1])
            if t is None and b is None:
                cells.append((EMPTY, None, None))
            elif t is not None and b is not None:
                cells.append((FULL, t, None) if t == b else (UPPER, t, b))
            elif t is not None:
                cells.append((UPPER, t, None))
            else:
                cells.append((LOWER, b, None))
    return cells


def build_palette(all_cells, limit=63):
    """Quantise every colour seen to at most `limit` entries (median cut via Pillow)."""
    seen = {}
    for frame in all_cells:
        for _, fg, bg in frame:
            for col in (fg, bg):
                if col is not None:
                    seen[col] = seen.get(col, 0) + 1
    if not seen:
        return [], {}
    colours = list(seen.items())
    n_out = min(limit, len(colours))
    # A strip holding each colour as many times as it was used (capped), quantised.
    total = min(sum(min(n, 64) for _, n in colours), 1 << 20)
    strip = Image.new("RGB", (total, 1))
    i = 0
    for col, n in colours:
        for _ in range(min(n, 64)):
            if i < total:
                strip.putpixel((i, 0), col)
                i += 1
    q = strip.quantize(colors=n_out, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)
    pal = q.getpalette()[: 3 * n_out]
    palette = [(pal[j], pal[j + 1], pal[j + 2]) for j in range(0, len(pal), 3)]
    mapping = {}
    for col in seen:
        best = min(range(len(palette)), key=lambda k: dist(palette[k], col))
        mapping[col] = best + 1  # 0 is transparent
    return palette, mapping


def dist(a, b):
    return (a[0] - b[0]) ** 2 + (a[1] - b[1]) ** 2 + (a[2] - b[2]) ** 2


def encode(cells, mapping):
    g = "".join(c[0] for c in cells)
    f = "".join(ALPHABET[mapping[c[1]]] if c[1] is not None else "0" for c in cells)
    b = "".join(ALPHABET[mapping[c[2]]] if c[2] is not None else "0" for c in cells)
    return {"g": g, "f": f, "b": b}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pet", default="revbot")
    ap.add_argument("--columns", type=int, nargs="*", default=[12, 16, 24])
    ap.add_argument("--out", default=OUT)
    args = ap.parse_args()

    manifest, sheet = load(args.pet)
    rows = frames_by_row(manifest)
    clips = [c for c in CLIPS if c in manifest["clips"]]
    box = extent(manifest, rows, clips)
    width, height = box[2] - box[0], box[3] - box[1]
    print(f"{args.pet}: {len(clips)} clips, frame box {width}x{height} px around the anchor")

    sizes = []
    rendered = []  # every frame's cells, to build one palette afterwards
    for columns in args.columns:
        cell_px = width / columns
        nrows = max(1, math.ceil(height / (2 * cell_px)))
        size = {"columns": columns, "rows": nrows, "clips": {}}
        for name in clips:
            clip = manifest["clips"][name]
            frames = []
            for idx in clip["frames"]:
                cells = render_frame(sheet, rows[clip["row"]][idx], box, columns, nrows)
                rendered.append(cells)
                frames.append(cells)
            size["clips"][name] = {"fps": clip["fps"], "loop": clip["loop"], "frames": frames}
        sizes.append(size)
        print(f"  {columns} columns x {nrows} rows: {sum(len(c['frames']) for c in size['clips'].values())} frames")

    palette, mapping = build_palette(rendered)
    for size in sizes:
        for clip in size["clips"].values():
            clip["frames"] = [encode(cells, mapping) for cells in clip["frames"]]

    out = {
        "version": 1,
        "pet": args.pet,
        "note": "Each frame: g = glyphs (space, upper half block, lower half block, full block), "
                "f/b = foreground/background palette index per cell as one character of "
                "0-9A-Za-z-_ (0 = terminal default). Built by tools/build_cells.py.",
        "palette": ["#%02x%02x%02x" % c for c in palette],
        "sizes": sizes,
    }
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(out, f, separators=(",", ":"), ensure_ascii=False)
    print(f"wrote {args.out} ({os.path.getsize(args.out) // 1024} KB, {len(palette)} colours)")


if __name__ == "__main__":
    sys.exit(main())
