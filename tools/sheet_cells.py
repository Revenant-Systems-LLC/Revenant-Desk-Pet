#!/usr/bin/env python3
"""Cut a pose sheet (a grid of cells on a baked-in grey checkerboard) into matted frames.

    python tools/sheet_cells.py NAME IMAGE COLS ROWS

Made for the 16-direction look sheet (8 x 2 cells, 2026-09-30), on plain white or on a
painted-in "transparent" checkerboard (two light greys, dark green lines between the
cells, a number in each corner). Writes, like video_frames.py:
  art/video/NAME/frames/NNNN.png   RGBA, one per cell, row by row, every cell on the
                                   same canvas with his feet at the same point
  art/video/NAME/metrics.json      bbox, height, head_cx per frame
"""
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "art" / "video"
CANVAS = (360, 420)          # every frame: his feet centred at (180, 400)
FEET = (180, 400)


def light(c):
    """The checkerboard's greys (248 and 224) and anything near white or grey-green line."""
    sat = c.max(2) - c.min(2)
    return (c.min(2) >= 200) & (sat < 22)


def line(c):
    """The dark green grid lines."""
    r, g, b = c[..., 0], c[..., 1], c[..., 2]
    return (g > r + 20) & (g > b - 10) & (c.max(2) < 170)


def cut_cell(cell):
    c = cell.astype(np.int16)
    bgish = light(c) | line(c)
    lab, _ = ndimage.label(bgish)
    border = set(np.concatenate([lab[0], lab[-1], lab[:, 0], lab[:, -1]]).tolist()) - {0}
    bg = np.isin(lab, list(border))
    # Enclosed light: an eye highlight sits inside a black eye; anything else (a gap
    # between an arm and his body, showing the checkerboard or plain white) is background.
    lum = c.mean(2)
    for i in set(np.unique(lab).tolist()) - {0} - border:
        comp = lab == i
        # the ring a few pixels out, past the highlight's own soft edge
        rim = ndimage.binary_dilation(comp, iterations=5) & ~ndimage.binary_dilation(comp, iterations=2)
        in_eye = (lum[rim] < 90).mean() > 0.6
        if not in_eye or comp.sum() > 2500:
            bg |= comp
    fg = ~bg
    lab2, n = ndimage.label(fg)
    if n == 0:
        return None
    sizes = ndimage.sum(fg, lab2, range(1, n + 1))
    big = int(np.argmax(sizes)) + 1
    objs = ndimage.find_objects(lab2)
    y0, y1, x0, x1 = objs[big - 1][0].start, objs[big - 1][0].stop, objs[big - 1][1].start, objs[big - 1][1].stop
    keep = np.zeros_like(fg)
    for i, o in enumerate(objs):
        # the robot, and bits of him touching his box (the antenna ball); not the cell's number
        oy0, oy1, ox0, ox1 = o[0].start, o[0].stop, o[1].start, o[1].stop
        inside = oy0 >= y0 - 4 and oy1 <= y1 + 4 and ox0 >= x0 - 4 and ox1 <= x1 + 4
        if i + 1 == big or (inside and sizes[i] >= 20):
            keep |= lab2 == i + 1
    # soft edge: un-mix from the background (white, or the checkerboard's mid-grey)
    ring = keep & ~ndimage.binary_erosion(keep)
    bgl = float(np.median(lum[bg])) if bg.any() else 245.0
    alpha = np.where(ring, np.clip((bgl - lum) / (bgl - 40.0), 0.0, 1.0), keep * 1.0)
    a = np.maximum(alpha, 0.05)[..., None]
    col = np.where(ring[..., None], (c - (1 - a) * bgl) / a, c)
    out = np.zeros(c.shape[:2] + (4,), np.float64)
    out[..., :3] = np.clip(col, 0, 255)
    out[..., 3] = alpha * 255
    return out.round().astype(np.uint8), keep


def main():
    if len(sys.argv) != 5:
        raise SystemExit(__doc__)
    name, image, cols, rows = sys.argv[1], Path(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4])
    im = np.array(Image.open(image).convert("RGB"))
    H, W = im.shape[:2]
    dest = OUT / name
    (dest / "frames").mkdir(parents=True, exist_ok=True)
    metrics = []
    for r in range(rows):
        for k in range(cols):
            i = r * cols + k
            # a few pixels in from each cell edge, clear of the grid lines
            x0, x1 = round(k * W / cols) + 3, round((k + 1) * W / cols) - 3
            y0, y1 = round(r * H / rows) + 3, round((r + 1) * H / rows) - 3
            got = cut_cell(im[y0:y1, x0:x1])
            if got is None:
                metrics.append(dict(index=i, empty=True))
                continue
            rgba, keep = got
            ys, xs = np.nonzero(keep)
            fy1 = ys.max() + 1
            band = keep[max(0, fy1 - 12):fy1]
            bx = np.nonzero(band.any(0))[0]
            feet_cx = (bx.min() + bx.max()) / 2
            canvas = Image.new("RGBA", CANVAS, (0, 0, 0, 0))
            ox, oy = int(round(FEET[0] - feet_cx)), int(FEET[1] - fy1)
            canvas.alpha_composite(Image.fromarray(rgba, "RGBA"), (ox, oy))
            canvas.save(dest / "frames" / f"{i:04d}.png")
            a = np.array(canvas)[..., 3] > 0
            cy, cx = np.nonzero(a)
            bbox = [int(cx.min()), int(cy.min()), int(cx.max()) + 1, int(cy.max()) + 1]
            metrics.append(dict(index=i, bbox=bbox, height=bbox[3] - bbox[1], head_cx=float(FEET[0]), head_w=0, feet_cx=float(FEET[0])))
    (dest / "metrics.json").write_text(json.dumps(dict(image=str(image), cells=[cols, rows], frames=metrics), indent=1))
    print(json.dumps(dict(name=name, cells=len(metrics), heights=[m.get("height") for m in metrics])))


if __name__ == "__main__":
    main()
