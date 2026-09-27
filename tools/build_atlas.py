#!/usr/bin/env python3
"""Build the desktop pet sprite atlas from the source art in art/source.

Re-runnable. Outputs:
  src/DesktopPet/Assets/atlas.png   packed frames, straight RGBA
  src/DesktopPet/Assets/atlas.json  frame rects, anchor offsets, clips
  src/DesktopPet/Assets/pet.ico     tray icon
  art/build/contact.png             QA sheet with baseline and height guides

Two kinds of source:
  * 12 strips (01..12) with real alpha, drawn at inconsistent scales.
  * 13-atlas-preview.webp, a labeled contact sheet on a checkerboard. Only the
    walk rows come from it, because they exist nowhere else.

Every frame is positioned relative to an anchor: the point on the ground
directly under the pet. Offsets in atlas.json are from that anchor.
"""
import json
import os
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "art" / "source"
BUILD = ROOT / "art" / "build"
ASSETS = ROOT / "src" / "DesktopPet" / "Assets"

ALPHA_CORE = 40          # alpha threshold used to find connected shapes
PAD = 3                  # transparent padding around each packed frame

# ground: 'each' snaps every frame's feet to the ground line (all poses are
#         standing); 'row' keeps the artist's vertical layout relative to the
#         base frame (needed for jumps and for strips with floor debris).
# clamp:  in 'row' mode, never let a frame sink below the ground line.
# neutral: frames used for scale calibration against the idle strip.
STRIPS = [
    dict(row="working", file="01-working-hammer.webp", n=6, neutral=[0], ground="row"),
    dict(row="idle", file="02-idle.webp", n=6, neutral=[0, 3, 5], ground="each"),
    dict(row="review", file="03-review.webp", n=6, neutral=[1, 5], ground="each"),
    dict(row="failed", file="04-failed.webp", n=8, neutral=[0], ground="each"),
    dict(row="sleep", file="05-sleep.webp", n=6, neutral=[0], ground="each"),
    dict(row="evolve", file="06-evolve.webp", n=5, neutral=[0], ground="each"),
    dict(row="gesture", file="07-gesture.webp", n=6, neutral=[0], ground="each"),
    dict(row="flex", file="08-mech-flex.webp", n=5, neutral=[0, 4], ground="each"),
    dict(row="leap", file="09-mech-leap.webp", n=5, neutral=[4], ground="row", clamp=True),
    dict(row="look_b", file="10-look-180-337.webp", n=8, neutral=[4], ground="each"),
    dict(row="look_a", file="11-look-000-157.webp", n=8, neutral=[4], ground="each"),
    dict(row="jump", file="12-jumping.webp", n=5, neutral=[0, 4], ground="row", clamp=True),
]
PREVIEW = "13-atlas-preview.webp"
PREVIEW_ROWS = {"walk_right": 1, "walk_left": 2}
PREVIEW_IDLE_ROW = 0

# Animation clips. frames are indices into the named row; fps is playback
# speed; loop clips repeat, others hold their last frame when done.
CLIPS = {
    "idle":          dict(row="idle", frames=[0], fps=1, loop=True),
    "fidget_tilt":   dict(row="idle", frames=[0, 1, 1, 1, 1, 1, 0], fps=5, loop=False),
    "fidget_chest":  dict(row="idle", frames=[0, 2, 2, 2, 2, 2, 0], fps=5, loop=False),
    "fidget_lean":   dict(row="idle", frames=[0, 4, 4, 4, 4, 4, 0], fps=5, loop=False),
    "walk_right":    dict(row="walk_right", frames=list(range(8)), fps=7, loop=True),
    "walk_left":     dict(row="walk_left", frames=list(range(8)), fps=7, loop=True),
    "wave":          dict(row="gesture", frames=[0, 2, 0, 2, 0, 2, 0], fps=5, loop=False),
    "think":         dict(row="gesture", frames=[0, 1, 1, 3, 3, 1, 1, 3, 3, 0], fps=4, loop=False),
    "shrug":         dict(row="gesture", frames=[0, 4, 4, 4, 4, 5, 5, 0], fps=5, loop=False),
    "waiting":       dict(row="gesture", frames=[0, 1, 1, 1, 0, 3, 3, 3, 0, 5, 5, 5], fps=3, loop=True),
    "jump":          dict(row="jump", frames=[0, 1, 2, 3, 4], fps=8, loop=False),
    "held":          dict(row="jump", frames=[2], fps=1, loop=True),
    "land":          dict(row="jump", frames=[3, 4], fps=8, loop=False),
    "failed":        dict(row="failed", frames=list(range(8)), fps=6, loop=False),
    "working":       dict(row="working", frames=[0, 1, 2, 3, 4, 5, 5, 5, 4], fps=5, loop=True),
    "review":        dict(row="review", frames=[0, 1, 2, 3, 4, 5], fps=3, loop=True),
    "doze_off":      dict(row="sleep", frames=[0, 1, 2], fps=2, loop=False),
    "sleeping":      dict(row="sleep", frames=[2], fps=1, loop=True),
    "wake":          dict(row="sleep", frames=[3, 4, 5], fps=4, loop=False),
    "evolve":        dict(row="evolve", frames=[0, 1, 2, 3, 4, 4, 4, 4, 4, 4, 3, 2, 1, 0], fps=5, loop=False),
    "flex":          dict(row="flex", frames=[0, 1, 2, 2, 2, 2, 3, 4], fps=5, loop=False),
    "leap":          dict(row="leap", frames=[0, 1, 2, 2, 3, 4], fps=6, loop=False),
    "look":          dict(row="look", frames=list(range(16)), fps=1, loop=True),
}


# ---------------------------------------------------------------- measuring

def core_bbox(mask):
    ys, xs = np.nonzero(mask)
    return xs.min(), ys.min(), xs.max() + 1, ys.max() + 1


def eye_height(rgb, mask):
    """Vertical diameter of the eyes: the two large, round, dark blobs."""
    lum = rgb[..., :3].astype(int).mean(2)
    x0, y0, x1, y1 = core_bbox(mask)
    h_total = y1 - y0
    lab, _ = ndimage.label((lum < 70) & mask)
    found = []
    for i, sl in enumerate(ndimage.find_objects(lab)):
        h = sl[0].stop - sl[0].start
        w = sl[1].stop - sl[1].start
        area = (lab[sl] == i + 1).sum()
        if 0.07 * h_total < h < 0.25 * h_total and 0.4 < w / h < 1.6 and area > 0.45 * h * w:
            found.append((h * w, h))
    found.sort(reverse=True)
    if not found:
        return None
    return float(np.mean([h for _, h in found[:2]]))


def band_center_x(mask, bottom, frac_lo, frac_hi, height):
    """Midpoint of the opaque x extent in a horizontal band above `bottom`."""
    y_lo = int(round(bottom - frac_hi * height))
    y_hi = int(round(bottom - frac_lo * height))
    band = mask[max(0, y_lo):max(1, y_hi)]
    cols = np.nonzero(band.any(0))[0]
    if len(cols) == 0:
        cols = np.nonzero(mask.any(0))[0]
    return (cols.min() + cols.max()) / 2.0


def head_center_x(mask):
    """Median x of opaque pixels in the head (skips the antenna tip)."""
    x0, y0, x1, y1 = core_bbox(mask)
    h = y1 - y0
    region = mask[int(y0 + 0.12 * h):int(y0 + 0.55 * h)]
    xs = np.nonzero(region)[1]
    return float(np.median(xs))


# ---------------------------------------------------------------- strips

def split_strip(path, n):
    """Return n frames: dict(rgba, mask(core), full(mask incl. soft edges))."""
    rgba = np.array(Image.open(path).convert("RGBA"))
    solid = rgba[..., 3] > ALPHA_CORE
    lab, count = ndimage.label(solid)
    sizes = ndimage.sum(solid, lab, range(1, count + 1))
    objs = ndimage.find_objects(lab)
    order = np.argsort(-sizes)
    cores = sorted(order[:n], key=lambda i: objs[i][1].start)
    if len(cores) != n or sizes[cores].min() < 0.25 * sizes[cores].max():
        raise SystemExit(f"{path.name}: expected {n} frames, segmentation disagrees")
    owner = {int(c): k for k, c in enumerate(cores)}
    ranges = [(objs[c][1].start, objs[c][1].stop) for c in cores]
    # attach every other blob (dropped parts, debris, motion lines) to the
    # frame whose core x range contains its center, else the nearest one
    for i in order[n:]:
        if sizes[i] < 4:
            continue
        cx = (objs[i][1].start + objs[i][1].stop) / 2
        dists = [0 if a <= cx < b else min(abs(cx - a), abs(cx - b)) for a, b in ranges]
        owner[int(i)] = int(np.argmin(dists))
    frames = []
    for k in range(n):
        ids = [i + 1 for i, o in owner.items() if o == k]
        solid_k = np.isin(lab, ids)
        core_k = lab == (cores[k] + 1)
        # grow by 3px to keep the anti-aliased edge that sits below ALPHA_CORE
        full = ndimage.binary_dilation(solid_k, iterations=3) & (rgba[..., 3] > 0)
        frames.append(dict(rgba=rgba, core=core_k, full=full))
    return frames


# ---------------------------------------------------------------- preview

def preview_grid(a):
    h, w, _ = a.shape
    dark = a.mean(axis=(1, 2)) < 60
    bars = []
    for y in range(h):
        if dark[y]:
            if bars and y - bars[-1][1] <= 1:
                bars[-1][1] = y
            else:
                bars.append([y, y])
    green = ((a[:, :, 1] - a[:, :, 0]) > 40).mean(axis=0) > 0.3
    lines = []
    for x in range(w):
        if green[x]:
            if lines and x - lines[-1][1] <= 1:
                lines[-1][1] = x
            else:
                lines.append([x, x])
    return bars, lines


def preview_cell(a, bars, lines, r, c):
    y0 = bars[r][1] + 2
    y1 = (bars[r + 1][0] if r + 1 < len(bars) else a.shape[0]) - 1
    x0 = lines[c][1] + 1
    x1 = lines[c + 1][0]
    return a[y0:y1, x0:x1]


def matte_checker(cell):
    """Remove the checkerboard around a sprite; returns straight RGBA."""
    c = cell.astype(int)
    sat = c.max(2) - c.min(2)
    bg_like = ((sat < 22) & (c.min(2) > 200)) | ((c[..., 1] - c[..., 0] > 25) & (c[..., 1] - c[..., 2] > 10))
    bg_like[:15, :14] = True  # frame index digit in the corner
    lab, _ = ndimage.label(bg_like)
    edge = np.concatenate([lab[0], lab[-1], lab[:, 0], lab[:, -1]])
    bg = np.isin(lab, list(set(edge.tolist()) - {0}))
    # Enclosed checker (gaps between legs, arm and body) is not connected to
    # the border, and neither are the eye highlights. Keep an enclosed light
    # region only if it sits inside the bounding box of an eye (a large, round,
    # dark blob); everything else enclosed is a gap.
    lum = c.mean(2)
    fg0 = ~bg
    ys, _ = np.nonzero(fg0)
    h_total = ys.max() - ys.min() + 1 if len(ys) else c.shape[0]
    dlab, _ = ndimage.label((lum < 70) & fg0)
    eye_boxes = []
    for j, sl in enumerate(ndimage.find_objects(dlab)):
        h = sl[0].stop - sl[0].start
        w = sl[1].stop - sl[1].start
        if 0.07 * h_total < h < 0.25 * h_total and 0.4 < w / h < 1.6 and (dlab[sl] == j + 1).sum() > 0.45 * h * w:
            eye_boxes.append((sl[0].start - 1, sl[0].stop + 1, sl[1].start - 1, sl[1].stop + 1))
    for i in set(np.unique(lab).tolist()) - {0} - set(edge.tolist()):
        comp = lab == i
        cy, cx = np.nonzero(comp)
        inside = any(y0 <= cy.min() and cy.max() < y1 and x0 <= cx.min() and cx.max() < x1
                     for y0, y1, x0, x1 in eye_boxes)
        if not inside:
            bg |= comp
    fg = ~bg
    lab2, n2 = ndimage.label(fg)
    if n2 == 0:
        raise SystemExit("empty preview cell")
    sizes = ndimage.sum(fg, lab2, range(1, n2 + 1))
    keep = np.isin(lab2, [i + 1 for i, s in enumerate(sizes) if s >= max(25, 0.004 * sizes.max())])
    # Un-mix the outermost ring against the local checker colour so the black
    # outline gets a soft edge instead of a grey halo.
    ring = keep & ~ndimage.binary_erosion(keep)
    bgf = (~keep).astype(float)
    kernel = np.ones((3, 3))
    cnt = ndimage.convolve(bgf, kernel, mode="nearest")
    bgcol = np.stack([ndimage.convolve(c[..., k] * bgf, kernel, mode="nearest") for k in range(3)], -1)
    bgcol = bgcol / np.maximum(cnt, 1)[..., None]
    out = np.zeros(c.shape[:2] + (4,), float)
    out[..., :3] = c
    out[..., 3] = keep * 255.0
    lum = c.mean(2)
    bgl = bgcol.mean(2)
    fg_l = 35.0
    alpha = np.clip((bgl - lum) / np.maximum(bgl - fg_l, 1), 0, 1)
    rr = ring & (cnt > 0)
    a_r = np.maximum(alpha[rr], 0.05)
    col = (c[rr] - (1 - a_r)[:, None] * bgcol[rr]) / a_r[:, None]
    out[rr, :3] = np.clip(col, 0, 255)
    out[rr, 3] = alpha[rr] * 255
    return out.round().astype(np.uint8), keep


# ---------------------------------------------------------------- resampling

def place(rgba, full_mask, scale, ax, gy):
    """Crop, scale about the anchor (ax, gy), return (image, ox, oy).

    Output pixels are on a grid aligned to the anchor, so frames that share
    an anchor line up exactly with no rounding drift.
    """
    ys, xs = np.nonzero(full_mask)
    sx0, sx1, sy0, sy1 = xs.min(), xs.max() + 1, ys.min(), ys.max() + 1
    src = rgba.copy()
    src[~full_mask] = 0
    ox = int(np.floor((sx0 - ax) * scale)) - 1
    oy = int(np.floor((sy0 - gy) * scale)) - 1
    w = int(np.ceil((sx1 - ax) * scale)) + 1 - ox
    h = int(np.ceil((sy1 - gy) * scale)) + 1 - oy
    box = (ax + ox / scale, gy + oy / scale, ax + (ox + w) / scale, gy + (oy + h) / scale)
    im = Image.fromarray(src, "RGBA").convert("RGBa")
    if abs(scale - 1.0) < 1e-9:
        # exact copy; box is integral when scale is 1
        im = im.crop(tuple(int(round(v)) for v in box))
    else:
        im = im.resize((w, h), Image.LANCZOS, box=box)
    return im.convert("RGBA"), ox, oy


def trim(im, ox, oy):
    a = np.array(im)[..., 3]
    ys, xs = np.nonzero(a > 0)
    if len(xs) == 0:
        return im, ox, oy
    x0, y0, x1, y1 = xs.min(), ys.min(), xs.max() + 1, ys.max() + 1
    return im.crop((x0, y0, x1, y1)), ox + int(x0), oy + int(y0)


def bottom_offset(im, oy):
    a = np.array(im)[..., 3]
    ys = np.nonzero((a > 128).any(1))[0]
    return oy + int(ys.max()) + 1


# ---------------------------------------------------------------- main

def main():
    BUILD.mkdir(parents=True, exist_ok=True)
    ASSETS.mkdir(parents=True, exist_ok=True)
    report = {}

    # preview: walk rows, and the idle row for the base scale
    pa = np.array(Image.open(SRC / PREVIEW).convert("RGB")).astype(int)
    bars, lines = preview_grid(pa)
    if len(bars) != 11 or len(lines) != 9:
        raise SystemExit(f"preview grid not recognised: {len(bars)} bars, {len(lines)} lines")
    p_idle_h = []
    for c in range(6):
        _, keep = matte_checker(preview_cell(pa, bars, lines, PREVIEW_IDLE_ROW, c))
        x0, y0, x1, y1 = core_bbox(keep)
        p_idle_h.append(y1 - y0)

    # strips
    strips = {}
    for spec in STRIPS:
        strips[spec["row"]] = (spec, split_strip(SRC / spec["file"], spec["n"]))

    idle_spec, idle_frames = strips["idle"]
    ref_h, ref_eye = [], []
    for k in idle_spec["neutral"]:
        f = idle_frames[k]
        x0, y0, x1, y1 = core_bbox(f["core"])
        ref_h.append(y1 - y0)
        ref_eye.append(eye_height(f["rgba"], f["core"]))
    ref_h, ref_eye = float(np.mean(ref_h)), float(np.mean(ref_eye))
    s_idle_h = float(np.mean([core_bbox(f["core"])[3] - core_bbox(f["core"])[1] for f in idle_frames]))
    base = float(np.mean(p_idle_h)) / s_idle_h   # strip-idle px -> atlas px
    report["base_scale"] = round(base, 4)

    frames = []   # dict(row, index, image, ox, oy)

    for row, (spec, fr) in strips.items():
        hs, es = [], []
        for k in spec["neutral"]:
            x0, y0, x1, y1 = core_bbox(fr[k]["core"])
            hs.append(y1 - y0)
            es.append(eye_height(fr[k]["rgba"], fr[k]["core"]))
        rel_h = ref_h / float(np.mean(hs))
        es = [e for e in es if e]
        rel_e = ref_eye / float(np.mean(es)) if es else rel_h
        rel = float(np.sqrt(rel_h * rel_e))
        scale = base * rel
        report[row] = dict(rel_height=round(rel_h, 3), rel_eye=round(rel_e, 3), scale=round(scale, 4))

        base_frame = fr[spec.get("base_frame", 0)]
        row_ground = core_bbox(base_frame["core"])[3]
        ref_height = core_bbox(base_frame["core"])[3] - core_bbox(base_frame["core"])[1]
        for k, f in enumerate(fr):
            cx0, cy0, cx1, cy1 = core_bbox(f["core"])
            own_bottom = cy1
            band_bottom = min(own_bottom, row_ground) if spec["ground"] == "row" else own_bottom
            ax = band_center_x(f["core"], band_bottom, 0.015, 0.07, ref_height)
            gy = own_bottom if spec["ground"] == "each" else row_ground
            im, ox, oy = place(f["rgba"], f["full"], scale, ax, gy)
            im, ox, oy = trim(im, ox, oy)
            if spec["ground"] == "row" and spec.get("clamp"):
                core_bottom = int(np.ceil((own_bottom - gy) * scale))
                if core_bottom > 0:
                    oy -= core_bottom
            frames.append(dict(row=row, index=k, image=im, ox=ox, oy=oy))

    # walk rows from the preview, at native scale (no resampling)
    for row, r in PREVIEW_ROWS.items():
        cells = []
        for c in range(8):
            rgba, keep = matte_checker(preview_cell(pa, bars, lines, r, c))
            cells.append((rgba, keep))
        # anchor x: keep the head steady; offset so the feet are centred on average
        heads, feet = [], []
        for rgba, keep in cells:
            x0, y0, x1, y1 = core_bbox(keep)
            heads.append(head_center_x(keep))
            feet.append(band_center_x(keep, y1, 0.015, 0.07, y1 - y0))
        shift = float(np.mean(np.array(feet) - np.array(heads)))
        ground = max(core_bbox(k)[3] for _, k in cells)
        for c, (rgba, keep) in enumerate(cells):
            ax = heads[c] + shift
            full = rgba[..., 3] > 0
            # integer anchor keeps this an exact copy
            im, ox, oy = place(rgba, full, 1.0, float(round(ax)), float(ground))
            im, ox, oy = trim(im, ox, oy)
            frames.append(dict(row=row, index=c, image=im, ox=ox, oy=oy))

    # merge the two look strips into one 16-direction row (index * 22.5 deg,
    # clockwise from straight up)
    for f in frames:
        if f["row"] == "look_a":
            f["row"], f["index"] = "look", f["index"]
        elif f["row"] == "look_b":
            f["row"], f["index"] = "look", f["index"] + 8

    order = ["idle", "walk_right", "walk_left", "gesture", "jump", "failed", "working",
             "review", "look", "sleep", "evolve", "flex", "leap"]
    frames.sort(key=lambda f: (order.index(f["row"]), f["index"]))

    # shelf-pack
    atlas_w = 2048
    x = y = shelf_h = 0
    for f in frames:
        w, h = f["image"].size
        if x + w + 2 * PAD > atlas_w:
            x, y, shelf_h = 0, y + shelf_h, 0
        f["x"], f["y"] = x + PAD, y + PAD
        x += w + 2 * PAD
        shelf_h = max(shelf_h, h + 2 * PAD)
    atlas_h = y + shelf_h
    atlas = Image.new("RGBA", (atlas_w, atlas_h), (0, 0, 0, 0))
    for f in frames:
        atlas.paste(f["image"], (f["x"], f["y"]))
    atlas.save(ASSETS / "atlas.png", optimize=True)

    rows = {}
    for f in frames:
        rows.setdefault(f["row"], 0)
        rows[f["row"]] = max(rows[f["row"]], f["index"] + 1)
    for name, clip in CLIPS.items():
        if clip["row"] not in rows or max(clip["frames"]) >= rows[clip["row"]]:
            raise SystemExit(f"clip {name} references missing frames")

    ext = dict(left=min(f["ox"] for f in frames), top=min(f["oy"] for f in frames),
               right=max(f["ox"] + f["image"].size[0] for f in frames),
               bottom=max(f["oy"] + f["image"].size[1] for f in frames))
    manifest = dict(
        version=1,
        pad=PAD,
        note="Offsets are from the anchor: the ground point under the pet. Look row index i faces i*22.5 degrees clockwise from up.",
        extent=ext,
        rows=rows,
        frames=[dict(row=f["row"], index=f["index"], x=f["x"], y=f["y"],
                     w=f["image"].size[0], h=f["image"].size[1], ox=f["ox"], oy=f["oy"]) for f in frames],
        clips=CLIPS,
    )
    (ASSETS / "atlas.json").write_text(json.dumps(manifest, indent=1))

    # icon from idle frame 0
    idle0 = next(f for f in frames if f["row"] == "idle" and f["index"] == 0)["image"]
    side = max(idle0.size)
    sq = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    sq.paste(idle0, ((side - idle0.size[0]) // 2, side - idle0.size[1]))
    sq.save(ASSETS / "pet.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])

    contact_sheet(frames, rows, order, ext)
    report["extent"] = ext
    report["frames"] = len(frames)
    report["atlas"] = [atlas_w, atlas_h]
    print(json.dumps(report, indent=1))


def contact_sheet(frames, rows, order, ext):
    cw = ext["right"] - ext["left"] + 8
    ch = ext["bottom"] - ext["top"] + 8
    ncol = max(rows.values())
    label_w = 90
    sheet = Image.new("RGBA", (label_w + cw * ncol, ch * len(order)), (255, 255, 255, 255))
    d = ImageDraw.Draw(sheet)
    idle0 = next(f for f in frames if f["row"] == "idle" and f["index"] == 0)
    head_top = idle0["oy"]
    for r, name in enumerate(order):
        ay = r * ch + 4 - ext["top"]
        d.text((4, r * ch + 4), name, fill=(0, 0, 0, 255))
        # checker so halos show up
        for f in [f for f in frames if f["row"] == name]:
            cx = label_w + f["index"] * cw
            ax = cx + 4 - ext["left"]
            for yy in range(r * ch, (r + 1) * ch, 8):
                for xx in range(cx, cx + cw, 8):
                    if ((xx - cx) // 8 + (yy - r * ch) // 8) % 2:
                        d.rectangle([xx, yy, xx + 7, yy + 7], fill=(225, 225, 235, 255))
            sheet.alpha_composite(f["image"], (ax + f["ox"], ay + f["oy"]))
            d.line([cx, ay, cx + cw, ay], fill=(255, 0, 0, 255))              # ground
            d.line([cx, ay + head_top, cx + cw, ay + head_top], fill=(0, 90, 255, 255))  # idle height
            d.line([ax, ay - 6, ax, ay + 6], fill=(0, 160, 0, 255))           # anchor x
            d.text((cx + 2, r * ch + 2), str(f["index"]), fill=(120, 120, 120, 255))
        d.line([0, (r + 1) * ch - 1, sheet.size[0], (r + 1) * ch - 1], fill=(160, 160, 160, 255))
    sheet.save(BUILD / "contact.png")


if __name__ == "__main__":
    main()
