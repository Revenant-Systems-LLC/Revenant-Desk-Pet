#!/usr/bin/env python3
"""Build a pet atlas from a clean "codex-style" sprite sheet (8 columns of
equal cells, one animation per row, real transparency), plus optional strips
that replace a row.

    python tools/build_codex_pet.py damien

Pets are configured in PETS below. Writes:
  src/DesktopPet/Assets/<name>.atlas.png / <name>.atlas.json / <name>.ico
  art/build/<name>-contact.png   QA sheet: every frame on its anchor, ground in red
"""
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "src" / "DesktopPet" / "Assets"
BUILD = ROOT / "art" / "build"
PAD = 3

PETS = {
    "damien": {
        "sheet": ROOT / "art/source/damien/spritesheet-extended-v8.png",
        "columns": 8,
        "rows": 11,
        # sheet row -> (atlas row name, how to anchor): "cell" keeps each frame
        # where the sheet put it in its cell; "head" steadies the body for runs
        "layout": {
            # row 3 (wave) is left out until Dave redraws it; "wave" falls back to the jump
            0: ("idle", "cell"), 1: ("walk_right", "head"), 2: ("walk_left", "head"),
            5: ("failed", "cell"), 6: ("waiting", "cell"), 7: ("working", "cell"), 8: ("review", "cell"),
            9: ("look_a", "cell"), 10: ("look_b", "cell"),
        },
        # rows replaced by separate strips: frames found left to right; "arc"
        # keeps the heights from the strip, levelled so first and last frames stand on the ground
        "strips": {"jump": (ROOT / "art/source/damien/jump-spin-fixed.webp", "arc")},
        # rows made of separate frame files, played in the listed order and kept where
        # each file puts him in its cell. The flip-and-squish, as Dave picked it
        # (2026-09-27): his frames d00, d01, d02, d04, d05, d06 (he has no d03 for it).
        # It was drawn smaller than the sheet, so it is scaled up until the roundest
        # frame's ball matches the idle ball.
        "files": {"thrown": [ROOT / "art/source/damien/flip" / n for n in
                             ["d00.png", "d01 (5).png", "d02 (5).png", "d04 (5).png", "d05 (5).png", "d06 (2).png"]]},
        # he floats: no legs, no gravity (see Pet.cs)
        "floats": True,
        # size relative to the sheet: the strip was drawn larger than the sheet cells
        "strip_match_row": "idle",
        # rows whose frames are resized so the ball matches the idle ball (he turns, he shouldn't shrink)
        "normalize_rows": ["look_a", "look_b"],
        "clips": {
            "idle": ("idle", "all", 5, True),
            "walk_right": ("walk_right", "all", 9, True),
            "walk_left": ("walk_left", "all", 9, True),
            "jump": ("jump", "all", 10, False),
            "land": ("jump", "last2", 10, False),
            "held": ("jump", [1], 1, True),
            "failed": ("failed", "all", 7, False),
            "waiting": ("waiting", "all", 5, True),
            "working": ("working", "all", 7, True),
            "review": ("review", "all", 6, True),
            "look": ("look", "all", 1, True),
            "thrown": ("thrown", "all", 10, False),
        },
    },
}


def frames_in_cells(sheet, cols, rows):
    w, h = sheet.size
    cw, ch = w // cols, h // rows
    a = np.array(sheet)
    out = {}
    for r in range(rows):
        cells = []
        for c in range(cols):
            cell = a[r * ch:(r + 1) * ch, c * cw:(c + 1) * cw]
            mask = cell[..., 3] > 16
            if mask.sum() < 60:
                continue
            cells.append((c, drop_specks(cell)))
        out[r] = cells
    return out, cw, ch


def drop_specks(cell, min_px=16, gap=12):
    """Clear stray dots floating apart from the body. Only tiny bits (under `min_px`) more than
    `gap` px from the body go: on Damien's sheet the strays are 9 and 12 px, 22 and 40 px out,
    while drawn marks such as the dizzy dashes in "failed" are 19 px and up and must stay."""
    mask = cell[..., 3] > 16
    lab, n = ndimage.label(mask)
    if n < 2:
        return cell
    sizes = ndimage.sum(mask, lab, range(1, n + 1))
    near = ndimage.binary_dilation(lab == int(np.argmax(sizes)) + 1, iterations=gap)
    out = cell.copy()
    for i in range(n):
        part = lab == i + 1
        if sizes[i] < min_px and not (part & near).any():
            out[part] = 0
    return out


def bbox(mask):
    ys, xs = np.nonzero(mask)
    return xs.min(), ys.min(), xs.max() + 1, ys.max() + 1


def head_x(mask):
    x0, y0, x1, y1 = bbox(mask)
    band = mask[y0 + int(0.15 * (y1 - y0)):y0 + int(0.6 * (y1 - y0))]
    return float(np.median(np.nonzero(band)[1]))


def ball_radius(mask):
    """Radius of the thickest round part (the body): horns and tail are thin, so they don't count."""
    return float(ndimage.distance_transform_edt(mask).max())


def strip_frames(path):
    a = np.array(Image.open(path).convert("RGBA"))
    solid = a[..., 3] > 40
    lab, n = ndimage.label(solid)
    sizes = ndimage.sum(solid, lab, range(1, n + 1))
    objs = ndimage.find_objects(lab)
    big = [i for i in range(n) if sizes[i] > 0.05 * sizes.max()]
    big.sort(key=lambda i: objs[i][1].start)
    owner = {i: k for k, i in enumerate(big)}
    ranges = [(objs[i][1].start, objs[i][1].stop) for i in big]
    for i in range(n):
        if i in owner or sizes[i] < 4:
            continue
        cx = (objs[i][1].start + objs[i][1].stop) / 2
        d = [0 if a0 <= cx < a1 else min(abs(cx - a0), abs(cx - a1)) for a0, a1 in ranges]
        owner[i] = int(np.argmin(d))
    frames = []
    for k, core in enumerate(big):
        ids = [i + 1 for i, o in owner.items() if o == k]
        m = ndimage.binary_dilation(np.isin(lab, ids), iterations=3) & (a[..., 3] > 0)
        x0, y0, x1, y1 = bbox(m)
        img = a.copy()
        img[~m] = 0
        frames.append((img[y0:y1, x0:x1], x0, y0, x1, y1))
    return frames


def main():
    name = sys.argv[1] if len(sys.argv) > 1 else "damien"
    cfg = PETS[name]
    sheet = Image.open(cfg["sheet"]).convert("RGBA")
    cells, cw, ch = frames_in_cells(sheet, cfg["columns"], cfg["rows"])
    placed = []  # (row, index, RGBA array, ox, oy)
    idle_row = next((r for r, (name, _) in cfg["layout"].items() if name == "idle"), None)
    idle_radius = float(np.median([ball_radius(c[1][..., 3] > 16) for c in cells.get(idle_row, [])])) if idle_row is not None else 0.0

    for r, (row, mode) in cfg["layout"].items():
        cs = cells.get(r, [])
        if not cs:
            continue
        masks = [c[1][..., 3] > 16 for c in cs]
        ground = max(bbox(m)[3] for m in masks)
        if mode == "head":
            heads = [head_x(m) for m in masks]
            centre = np.mean(heads)
        for k, ((c, cell), m) in enumerate(zip(cs, masks)):
            x0, y0, x1, y1 = bbox(m)
            ax = heads[k] + (cw / 2 - centre) if mode == "head" else cw / 2  # "head": keep the head steady, centred on average
            img = cell[y0:y1, x0:x1]
            ox, oy = x0 - ax, y0 - ground
            if row in cfg.get("normalize_rows", []) and idle_radius:
                f = idle_radius / max(1.0, ball_radius(m))
                if abs(f - 1) > 0.02:
                    im = Image.fromarray(img, "RGBA").convert("RGBa")
                    im = im.resize((max(1, round(im.width * f)), max(1, round(im.height * f))), Image.LANCZOS).convert("RGBA")
                    img, ox, oy = np.array(im), ox * f, oy * f  # scaled about the ground point under it
            placed.append((row, k, img, int(round(ox)), int(round(oy))))

    for row, paths in cfg.get("files", {}).items():
        cellsf = [drop_specks(np.array(Image.open(pth).convert("RGBA"))) for pth in paths]
        masks = [c[..., 3] > 16 for c in cellsf]
        ground = max(bbox(m)[3] for m in masks)
        f = idle_radius / max(ball_radius(m) for m in masks) if idle_radius else 1.0
        for k, (cell, m) in enumerate(zip(cellsf, masks)):
            x0, y0, x1, y1 = bbox(m)
            ax = cell.shape[1] / 2
            im = Image.fromarray(cell[y0:y1, x0:x1], "RGBA").convert("RGBa")
            im = im.resize((max(1, round(im.width * f)), max(1, round(im.height * f))), Image.LANCZOS).convert("RGBA")
            placed.append((row, k, np.array(im), int(round((x0 - ax) * f)), int(round((y0 - ground) * f))))

    # size strips to match the sheet: compare the strip's first frame height with the reference row
    ref_row = cfg["strip_match_row"]
    ref_heights = [p[2].shape[0] for p in placed if p[0] == ref_row]
    ref_h = float(np.median(ref_heights))
    for row, (path, mode) in cfg.get("strips", {}).items():
        fr = strip_frames(path)
        scale = ref_h / fr[0][0].shape[0]
        n = len(fr)
        b0, bn = fr[0][4], fr[-1][4]
        for k, (img, x0, y0, x1, y1) in enumerate(fr):
            # arc: the ground slopes from the first frame's feet to the last frame's
            ground = b0 + (bn - b0) * (k / (n - 1) if n > 1 else 0)
            lift = ground - y1  # how far above that ground this frame is
            im = Image.fromarray(img, "RGBA").convert("RGBa")
            im = im.resize((max(1, round(im.width * scale)), max(1, round(im.height * scale))), Image.LANCZOS).convert("RGBA")
            placed.append((row, k, np.array(im), -im.width // 2, int(round(-im.height - lift * scale))))

    # merge the two look rows into one 16-direction row
    placed = [(("look", i) if r == "look_a" else ("look", i + 8) if r == "look_b" else (r, i)) + tuple(rest)
              for (r, i, *rest) in placed]
    placed = [(p[0], p[1], p[2], p[3], p[4]) for p in placed]

    # pack
    placed.sort(key=lambda p: (p[0], p[1]))
    atlas_w, x, y, shelf = 2048, 0, 0, 0
    pos = []
    for row, idx, img, ox, oy in placed:
        h, w = img.shape[:2]
        if x + w + 2 * PAD > atlas_w:
            x, y, shelf = 0, y + shelf, 0
        pos.append((x + PAD, y + PAD))
        x += w + 2 * PAD
        shelf = max(shelf, h + 2 * PAD)
    atlas = Image.new("RGBA", (atlas_w, y + shelf), (0, 0, 0, 0))
    frames = []
    for (row, idx, img, ox, oy), (px, py) in zip(placed, pos):
        atlas.paste(Image.fromarray(img, "RGBA"), (px, py))
        frames.append(dict(row=row, index=idx, x=px, y=py, w=img.shape[1], h=img.shape[0], ox=ox, oy=oy))

    counts = {}
    for f in frames:
        counts[f["row"]] = max(counts.get(f["row"], 0), f["index"] + 1)
    clips = {}
    for clip, (row, sel, fps, loop) in cfg["clips"].items():
        n = counts.get(row, 0)
        if n == 0:
            continue
        idx = list(range(n)) if sel == "all" else list(range(max(0, n - 2), n)) if sel == "last2" else [i for i in sel if i < n]
        clips[clip] = dict(row=row, frames=idx, fps=fps, loop=loop)

    ASSETS.mkdir(parents=True, exist_ok=True)
    atlas.save(ASSETS / f"{name}.atlas.png", optimize=True)
    manifest = dict(version=1, pad=PAD, scale=1.0, floats=bool(cfg.get("floats")), frames=frames, clips=clips)
    (ASSETS / f"{name}.atlas.json").write_text(json.dumps(manifest, indent=1))

    idle0 = next(f for f in frames if f["row"] == "idle" and f["index"] == 0)
    icon_src = atlas.crop((idle0["x"], idle0["y"], idle0["x"] + idle0["w"], idle0["y"] + idle0["h"]))
    side = max(icon_src.size)
    sq = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    sq.paste(icon_src, ((side - icon_src.width) // 2, side - icon_src.height))
    sq.save(ASSETS / f"{name}.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])

    # contact sheet
    left = min(f["ox"] for f in frames); right = max(f["ox"] + f["w"] for f in frames)
    top = min(f["oy"] for f in frames); bottom = max(f["oy"] + f["h"] for f in frames)
    cellw, cellh = right - left + 8, bottom - top + 8
    rows = sorted(counts, key=lambda r: list(cfg["clips"]).index(r) if r in cfg["clips"] else 99)
    cols = max(counts.values())
    sheet_img = Image.new("RGBA", (80 + cellw * cols, cellh * len(rows)), (38, 42, 50, 255))
    d = ImageDraw.Draw(sheet_img)
    for ri, row in enumerate(rows):
        gy = ri * cellh + 4 - top
        d.text((4, ri * cellh + 4), row, fill=(230, 230, 230, 255))
        d.line([80, gy, sheet_img.width, gy], fill=(220, 60, 60, 255))
        for f in (f for f in frames if f["row"] == row):
            im = atlas.crop((f["x"], f["y"], f["x"] + f["w"], f["y"] + f["h"]))
            ax = 80 + f["index"] * cellw + 4 - left
            sheet_img.alpha_composite(im, (ax + f["ox"], gy + f["oy"]))
    BUILD.mkdir(parents=True, exist_ok=True)
    sheet_img.save(BUILD / f"{name}-contact.png")
    print(json.dumps(dict(pet=name, frames=len(frames), rows=counts, clips=list(clips), atlas=[atlas.width, atlas.height])))


if __name__ == "__main__":
    main()
