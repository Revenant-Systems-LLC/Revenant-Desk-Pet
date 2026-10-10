"""
Draw mod/revbot/cells.json the way a terminal shows it, as a PNG you can look
at: each cell is a 1 x 2 block (a terminal cell is about twice as tall as it is
wide), on a dark terminal background, scaled up. One strip per size, a few
frames from each clip side by side.

    python tools/preview_cells.py                    # -> art/build/cells-preview.png
    python tools/preview_cells.py --cells other.json --out other.png --bg 30,30,30
"""
import argparse
import json
import os
import sys

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
ALPHABET = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-_"


def rgb(h):
    h = h.lstrip("#")
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))


def draw_frame(frame, size, palette, bg, cell_w):
    cols, rows = size["columns"], size["rows"]
    half = cell_w  # each half of a cell is a square: cell_w wide, cell_w tall
    img = Image.new("RGB", (cols * cell_w, rows * 2 * half), bg)
    d = ImageDraw.Draw(img)
    for r in range(rows):
        for c in range(cols):
            i = r * cols + c
            g = frame["g"][i]
            fi = ALPHABET.index(frame["f"][i])
            bi = ALPHABET.index(frame["b"][i])
            fg = palette[fi - 1] if fi else bg
            bgc = palette[bi - 1] if bi else bg
            x, y = c * cell_w, r * 2 * half
            if g == "▀":
                top, bottom = fg, bgc
            elif g == "▄":
                top, bottom = bgc, fg
            elif g == "█":
                top = bottom = fg
            else:
                top = bottom = bgc
            d.rectangle([x, y, x + cell_w - 1, y + half - 1], fill=top)
            d.rectangle([x, y + half, x + cell_w - 1, y + 2 * half - 1], fill=bottom)
    return img


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cells", default=os.path.join(ROOT, "mod", "revbot", "cells.json"))
    ap.add_argument("--out", default=os.path.join(ROOT, "art", "build", "cells-preview.png"))
    ap.add_argument("--clips", nargs="*", default=["idle", "fidget_blink", "working", "review", "waiting", "happy", "failed", "sleeping"])
    ap.add_argument("--bg", default="12,12,12")
    ap.add_argument("--cell", type=int, default=8, help="screen pixels per cell width")
    args = ap.parse_args()

    with open(args.cells, encoding="utf-8") as f:
        sheet = json.load(f)
    palette = [rgb(h) for h in sheet["palette"]]
    bg = tuple(int(v) for v in args.bg.split(","))
    strips = []
    for size in sheet["sizes"]:
        tiles = []
        for name in args.clips:
            clip = size["clips"].get(name)
            if not clip:
                continue
            n = len(clip["frames"])
            for k in sorted({0, n // 2}):
                tiles.append(draw_frame(clip["frames"][k], size, palette, bg, args.cell))
        gap = args.cell * 2
        w = sum(t.width for t in tiles) + gap * (len(tiles) + 1)
        h = max(t.height for t in tiles) + 2 * gap
        strip = Image.new("RGB", (w, h), (40, 40, 48))
        x = gap
        for t in tiles:
            strip.paste(t, (x, gap))
            x += t.width + gap
        strips.append(strip)
    out = Image.new("RGB", (max(s.width for s in strips), sum(s.height for s in strips)), (40, 40, 48))
    y = 0
    for s in strips:
        out.paste(s, (0, y))
        y += s.height
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    out.save(args.out)
    print(f"wrote {args.out} ({out.width}x{out.height})")


if __name__ == "__main__":
    sys.exit(main())
