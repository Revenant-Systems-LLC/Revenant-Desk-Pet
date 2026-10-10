"""
Render the pet's sprite clips down to terminal cells for his terminal bodies
(the Claude Code mod, the PowerShell module). Reads the embedded atlas
(src/DesktopPet/Assets/<pet>.atlas.png + .json, the same files the exe uses) and
writes mod/revbot/cells.json.

A terminal cell is about twice as tall as it is wide, so each cell holds two
pixels stacked: the top half drawn with the upper half block (U+2580) in the
foreground colour, the bottom half as the background colour. A cell with only
its lower pixel set uses the lower half block (U+2584). Colours are quantised
to one palette of at most 62 entries so every cell is three characters in the
JSON: glyph, foreground index, background index (index 0 = the terminal's own
colour, i.e. transparent).

Two crops, both sized by rows (a terminal's height is what is scarce):
  body  his whole figure, framed on his idle pose. Props and flung arms that
        reach past it are cut; a jump higher than the frame is lowered so his
        head stays in. The band above the prompt uses it.
  head  his head and antenna only, for the small spots (the spinner), where a
        whole body would be a smudge.

Shrinking painted art to 10 to 30 pixels by averaging smears his black outline
into everything and loses the small bright bits (eye glints, the antenna ball,
the yellow stripes). So each terminal pixel takes the most telling colour of
the art it covers instead: the outline is left out, bright and saturated
colours count extra, and a spot that is nothing but outline (thin legs, the
neck) shows as a dark ink. Colours are lifted a little so he reads on a dark
terminal.

    python tools/build_cells.py            # RevBot: head 8 rows, body 12 and 16 rows
    python tools/build_cells.py --pet damien --head 8 --body 12
    python tools/preview_cells.py          # look at the result as a PNG
"""
import argparse
import json
import math
import os
import sys

import numpy as np
from PIL import Image, ImageFilter

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

LIFT = 1.2               # brighten colours by this much (dark greys vanish on a dark terminal)
INK = (36, 36, 40)       # a spot of pure outline: legs, neck, the edge of a thin arm
COVER = 0.4              # less of a spot covered than this: transparent
OUTLINE_LUM = 40         # darker than this, near the outside, is outline
OUTLINE_REACH = 4        # art pixels from the outside that still count as near it
SAT_WEIGHT = 2.5         # how much extra a saturated colour counts (orange, yellow)
GLINT_WEIGHT = 3.0       # and a near-white one (eye glints, paper)
MIN_SHARE = 0.15         # a colour must cover this much of a spot to be picked


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


def crops(manifest, rows):
    """The body and head boxes around the anchor, both framed on the idle pose."""
    left, top, right, bottom = extent(manifest, rows, ["idle"])
    w, h = right - left, bottom - top
    body = (round(left - 0.18 * w), round(top - 0.07 * h), round(right + 0.18 * w), bottom + 6)
    head = (round(left - 0.04 * w), round(top - 0.03 * h), round(right + 0.04 * w), round(top + 0.6 * h))
    return {"body": body, "head": head}


def frame_canvas(sheet, f, box, pin_top=None):
    """
    One frame placed in a crop box; a jump that would leave the top is lowered to fit.
    With pin_top (the head crop), the top of the art always sits that far below the
    box top instead, so a crouch or a sit never drops his head out of it.
    """
    left, top, right, bottom = box
    canvas = Image.new("RGBA", (right - left, bottom - top), (0, 0, 0, 0))
    crop = sheet.crop((f["x"], f["y"], f["x"] + f["w"], f["y"] + f["h"]))
    if f.get("mirror"):
        crop = crop.transpose(Image.FLIP_LEFT_RIGHT)
    y = max(0, f["oy"] - top)
    if pin_top is not None and (bbox := crop.getchannel("A").getbbox()) is not None:
        y = pin_top - bbox[1]
    x = f["ox"] - left
    # whatever hangs off the left or top of the box is cut (Pillow wants non-negative positions)
    canvas.alpha_composite(crop, (max(0, x), max(0, y)), (max(0, -x), max(0, -y)))
    return canvas


def shrink(canvas, w, h):
    """The canvas as h rows of w pixels (an rgb tuple, or None for transparent), one telling colour each."""
    a = np.asarray(canvas).astype(np.float32)
    rgb, alpha = a[..., :3], a[..., 3]
    opaque = alpha > 128
    outside = Image.fromarray((~opaque).astype(np.uint8) * 255).filter(ImageFilter.MaxFilter(2 * OUTLINE_REACH + 1))
    lum = rgb @ np.array([0.299, 0.587, 0.114], dtype=np.float32)
    outline = opaque & (lum < OUTLINE_LUM) & (np.asarray(outside) > 0)
    hi, lo = rgb.max(-1), rgb.min(-1)
    sat = (hi - lo) / np.maximum(hi, 1)
    H, W = alpha.shape
    px = []
    for ty in range(h):
        y0, y1 = int(ty * H / h), int((ty + 1) * H / h)
        row = []
        for tx in range(w):
            x0, x1 = int(tx * W / w), int((tx + 1) * W / w)
            spot = opaque[y0:y1, x0:x1]
            if spot.size == 0 or spot.mean() < COVER:
                row.append(None)
                continue
            keep = spot & ~outline[y0:y1, x0:x1]
            if keep.sum() < 0.12 * spot.size:
                row.append(INK)
                continue
            cols = rgb[y0:y1, x0:x1][keep]
            weight = 1.0 + SAT_WEIGHT * sat[y0:y1, x0:x1][keep] + GLINT_WEIGHT * (lum[y0:y1, x0:x1][keep] > 200)
            q = (cols // 48).astype(np.int32)  # coarse colour groups
            keys = q[:, 0] * 64 + q[:, 1] * 8 + q[:, 2]
            best, best_w = cols.mean(0), -1.0
            for k in np.unique(keys):
                sel = keys == k
                if sel.mean() < MIN_SHARE:
                    continue
                if weight[sel].sum() > best_w:
                    best, best_w = cols[sel].mean(0), weight[sel].sum()
            row.append(tuple(int(v) for v in np.clip(best * LIFT, 0, 255)))
        px.append(row)
    return px


def render_frame(sheet, f, box, columns, rows, pin_top=None):
    """One frame as (glyph, fg_rgb_or_None, bg_rgb_or_None) per cell, row-major."""
    px = shrink(frame_canvas(sheet, f, box, pin_top), columns, rows * 2)
    cells = []
    for r in range(rows):
        for c in range(columns):
            t, b = px[2 * r][c], px[2 * r + 1][c]
            if t is None and b is None:
                cells.append((EMPTY, None, None))
            elif t is not None and b is not None:
                cells.append((FULL, t, None) if t == b else (UPPER, t, b))
            elif t is not None:
                cells.append((UPPER, t, None))
            else:
                cells.append((LOWER, b, None))
    return cells


def build_palette(all_cells, limit=62):
    """
    Quantise every colour seen to at most `limit` entries (median cut via Pillow).
    62, not the 63 the alphabet allows: index 63 is "_", and with it a run of
    indices can spell "ghs_" and 36 letters, which secret scanners read as a
    GitHub token and rightly refuse to commit.
    """
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
    ap.add_argument("--head", type=int, nargs="*", default=[8], help="rows of the head-only sizes")
    ap.add_argument("--body", type=int, nargs="*", default=[12, 16], help="rows of the whole-body sizes")
    ap.add_argument("--out", default=OUT)
    args = ap.parse_args()

    manifest, sheet = load(args.pet)
    rows = frames_by_row(manifest)
    clips = [c for c in CLIPS if c in manifest["clips"]]
    boxes = crops(manifest, rows)
    # the head crop keeps the top of his art where the idle pose has it
    pins = {"body": None, "head": extent(manifest, rows, ["idle"])[1] - boxes["head"][1]}
    print(f"{args.pet}: {len(clips)} clips, body box {boxes['body']}, head box {boxes['head']} around the anchor")

    sizes = []
    rendered = []  # every frame's cells, to build one palette afterwards
    for crop, row_counts in (("head", args.head), ("body", args.body)):
        left, top, right, bottom = boxes[crop]
        for nrows in row_counts:
            art_px = (bottom - top) / (2 * nrows)  # art pixels per terminal pixel
            columns = max(1, round((right - left) / art_px))
            size = {"crop": crop, "columns": columns, "rows": nrows, "clips": {}}
            for name in clips:
                clip = manifest["clips"][name]
                frames = []
                for idx in clip["frames"]:
                    cells = render_frame(sheet, rows[clip["row"]][idx], boxes[crop], columns, nrows, pins[crop])
                    rendered.append(cells)
                    frames.append(cells)
                size["clips"][name] = {"fps": clip["fps"], "loop": clip["loop"], "frames": frames}
            sizes.append(size)
            print(f"  {crop}: {columns} columns x {nrows} rows, {sum(len(c['frames']) for c in size['clips'].values())} frames")

    palette, mapping = build_palette(rendered)
    for size in sizes:
        for clip in size["clips"].values():
            clip["frames"] = [encode(cells, mapping) for cells in clip["frames"]]

    out = {
        "version": 2,
        "pet": args.pet,
        "note": "Each size: crop (head or body) and columns x rows of cells. Each frame: g = glyphs (space, "
                "upper half block, lower half block, full block), f/b = foreground/background palette index "
                "per cell as one character of 0-9A-Za-z-_ (0 = terminal default). Built by tools/build_cells.py.",
        "palette": ["#%02x%02x%02x" % c for c in palette],
        "sizes": sizes,
    }
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(out, f, separators=(",", ":"), ensure_ascii=False)
    print(f"wrote {args.out} ({os.path.getsize(args.out) // 1024} KB, {len(palette)} colours)")


if __name__ == "__main__":
    sys.exit(main())
