#!/usr/bin/env python3
"""Rebuild an antenna that left the top of the video frame.

    python tools/fix_antenna.py SOURCE FIRST_CUT LAST_CUT
    python tools/fix_antenna.py SOURCE --validate A B

Reads art/video/SOURCE/frames, writes art/video/SOURCE-fixed/frames with
PAD_TOP transparent rows added above every frame (so an antenna has room),
and leaves the source frames untouched.

For each cut frame the attachment point is measured directly (the bottom of
the stalk and the head top are still in view); the angle and length are
interpolated between the last intact frame before the gap and the first
intact frame after it; the antenna itself is lifted from the nearer of
those two frames and rotated/scaled about its base.

--validate A B pretends frames A..B are cut, rebuilds them from A-1 and
B+1, and reports the error against the real antenna.
"""
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage

ROOT = Path(__file__).resolve().parent.parent
VIDEO = ROOT / "art" / "video"
PAD_TOP = 120
BAND = 60          # half-width of the column band that holds the antenna
HEAD_ROW_WIDTH = 150


def load(src, i):
    return np.array(Image.open(VIDEO / src / "frames" / f"{i:04d}.png"))


def measure(a):
    """Head top row, stalk base point, ball centre (None if cut), angle, length."""
    al = a[..., 3] > 128
    head_top = int(np.argmax(al.sum(1) > HEAD_ROW_WIDTH))
    xs = np.nonzero(al[max(0, head_top - 8)])[0]
    base = (float(xs.mean()), float(head_top))
    if al[0].any():
        return dict(head_top=head_top, base=base, ball=None)
    top = int(np.argmax(al.any(1)))
    band = al[top:top + 30]
    ys, xs2 = np.nonzero(band)
    ball = (float(xs2.mean()), float(ys.mean() + top))
    dx, dy = ball[0] - base[0], ball[1] - base[1]
    return dict(head_top=head_top, base=base, ball=ball,
                angle=float(np.degrees(np.arctan2(dx, -dy))), length=float(np.hypot(dx, dy)))


STALK_R = 9        # corridor half-width around the stalk
BALL_R = 24        # radius around the ball centre that counts as antenna


def corridor(shape, base, angle, length, y_limit):
    """Pixels on the antenna's path: a thin band along the stalk plus a disc
    around the ball, above y_limit (so the head outline is never included)."""
    h, w = shape[:2]
    rad = np.radians(angle)
    ux, uy = np.sin(rad), -np.cos(rad)                 # unit vector base -> ball
    ball = (base[0] + ux * length, base[1] + uy * length)
    yy, xx = np.mgrid[0:h, 0:w]
    px, py = xx - base[0], yy - base[1]
    along = np.clip(px * ux + py * uy, 0, length)
    dist = np.hypot(px - along * ux, py - along * uy)
    near_ball = np.hypot(xx - ball[0], yy - ball[1]) < BALL_R
    return ((dist < STALK_R) | near_ball) & (yy < y_limit)


def antenna_layer(a, m):
    """RGBA of just the antenna: stalk and ball, nothing from the head."""
    layer = np.zeros_like(a)
    mask = corridor(a.shape, m["base"], m["angle"], m["length"], m["head_top"] - 2)
    layer[mask] = a[mask]
    return layer


def transform(layer, base_from, base_to, d_angle, scale, out_shape):
    """Rotate (clockwise-positive degrees) and scale about base_from, move to base_to."""
    im = Image.fromarray(layer, "RGBA").convert("RGBa")  # premultiplied for clean edges
    # scale about the base, then rotate about the base
    if abs(scale - 1) > 1e-3:
        w, h = im.size
        im = im.resize((max(1, round(w * scale)), max(1, round(h * scale))), Image.BICUBIC)
        base_from = (base_from[0] * scale, base_from[1] * scale)
    im = im.rotate(-d_angle, resample=Image.BICUBIC, center=base_from)
    canvas = Image.new("RGBa", (out_shape[1], out_shape[0]), (0, 0, 0, 0))
    offset = (round(base_to[0] - base_from[0]), round(base_to[1] - base_from[1]))
    canvas.paste(im, offset)
    return np.array(canvas.convert("RGBA"))


def rebuild(src, first_cut, last_cut):
    """Yield (index, fixed RGBA with PAD_TOP) for the cut frames."""
    before, after = first_cut - 1, last_cut + 1
    a0, a1 = load(src, before), load(src, after)
    m0, m1 = measure(a0), measure(a1)
    if m0["ball"] is None or m1["ball"] is None:
        raise SystemExit("the frames either side of the gap must have a complete antenna")
    pad = lambda arr: np.concatenate([np.zeros((PAD_TOP,) + arr.shape[1:], arr.dtype), arr], 0)
    shift = lambda m: dict(m, base=(m["base"][0], m["base"][1] + PAD_TOP), head_top=m["head_top"] + PAD_TOP)
    lay0, lay1 = antenna_layer(pad(a0), shift(m0)), antenna_layer(pad(a1), shift(m1))
    n = after - before
    for i in range(first_cut, last_cut + 1):
        t = (i - before) / n
        a = pad(load(src, i))
        m = measure(a[PAD_TOP:])
        base_to = (m["base"][0], m["base"][1] + PAD_TOP)
        angle = m0["angle"] + t * (m1["angle"] - m0["angle"])
        length = m0["length"] + t * (m1["length"] - m0["length"])
        use0 = t < 0.5
        tm, lay = (m0, lay0) if use0 else (m1, lay1)
        base_from = (tm["base"][0], tm["base"][1] + PAD_TOP)
        new = transform(lay, base_from, base_to, angle - tm["angle"], length / tm["length"], a.shape)
        # clear the old antenna remnants (they lie along the same path, a little
        # wider to be safe), never touching the head, then add the new one
        clear = corridor(a.shape, base_to, angle, length, m["head_top"] - 2 + PAD_TOP)
        clear = ndimage.binary_dilation(clear, iterations=6) & (np.mgrid[0:a.shape[0], 0:a.shape[1]][0] < m["head_top"] - 2 + PAD_TOP)
        a[clear] = 0
        out = Image.fromarray(a, "RGBA")
        out.alpha_composite(Image.fromarray(new, "RGBA"))
        yield i, np.array(out), dict(angle=round(angle, 2), length=round(length, 1), base=[round(v, 1) for v in base_to])


def antenna_mask(a, head_top):
    al = a[..., 3] > 128
    al[head_top - 2:] = False
    return al


def validate(src, a, b):
    errs = []
    for i, fixed, info in rebuild(src, a, b):
        truth = np.concatenate([np.zeros((PAD_TOP,) + load(src, i).shape[1:], np.uint8), load(src, i)], 0)
        mt = measure(truth[PAD_TOP:])
        tm = antenna_mask(truth, mt["head_top"] + PAD_TOP)
        fm = antenna_mask(fixed, mt["head_top"] + PAD_TOP)
        iou = (tm & fm).sum() / max(1, (tm | fm).sum())
        fixed_m = measure(fixed[PAD_TOP:]) if not fixed[PAD_TOP][..., 3].any() else None
        ty, tx = np.nonzero(tm); fy, fx = np.nonzero(fm)
        ball_t = (tx[ty < ty.min() + 30].mean(), ty[ty < ty.min() + 30].mean())
        ball_f = (fx[fy < fy.min() + 30].mean(), fy[fy < fy.min() + 30].mean())
        err = float(np.hypot(ball_t[0] - ball_f[0], ball_t[1] - ball_f[1]))
        errs.append(err)
        print(f"frame {i}: antenna IoU {iou:.2f}, ball centre error {err:.1f}px")
    print(f"mean ball error {np.mean(errs):.1f}px, max {np.max(errs):.1f}px (source pixels; ~x0.3 at desktop size)")


def main():
    args = sys.argv[1:]
    if len(args) == 4 and args[1] == "--validate":
        validate(args[0], int(args[2]), int(args[3]))
    elif len(args) == 3:
        src, first, last = args[0], int(args[1]), int(args[2])
        dest = VIDEO / f"{src}-fixed" / "frames"
        dest.mkdir(parents=True, exist_ok=True)
        fixed = {}
        for i, arr, info in rebuild(src, first, last):
            fixed[i] = info
            Image.fromarray(arr, "RGBA").save(dest / f"{i:04d}.png", compress_level=3)
        # copy every other frame with the same padding so the sequence is uniform
        for p in sorted((VIDEO / src / "frames").glob("*.png")):
            i = int(p.stem)
            if i in fixed:
                continue
            a = np.array(Image.open(p))
            a = np.concatenate([np.zeros((PAD_TOP,) + a.shape[1:], a.dtype), a], 0)
            Image.fromarray(a, "RGBA").save(dest / p.name, compress_level=3)
        (dest.parent / "antenna_fix.json").write_text(json.dumps(dict(source=src, pad_top=PAD_TOP, rebuilt=fixed), indent=1))
        print(f"rebuilt {len(fixed)} frames -> {dest}")
    else:
        raise SystemExit(__doc__)


if __name__ == "__main__":
    main()
