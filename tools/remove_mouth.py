#!/usr/bin/env python3
"""Paint RevBot's mouth out of every frame in a folder.

    python tools/remove_mouth.py SRC_DIR DEST_DIR        folder of RGBA frames
    python tools/remove_mouth.py SRC_IMAGE DEST_IMAGE    one image (e.g. the reference)

The mouth is found relative to the eyes: a small dark stroke centred under
them, above the bottom edge of the head. It is filled by diffusing the
surrounding face pixels inward, so the fill follows the local shading.
Frames where the eyes cannot be found (blinks) borrow the eye positions of
the nearest frames. Sources are never modified.
"""
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage


def eyes_of(a):
    """(cx, cy, radius, spacing) of the two eyes, or None."""
    rgb = a[..., :3].astype(np.int16)
    solid = a[..., 3] > 128
    ys, _ = np.nonzero(solid)
    height = ys.max() - ys.min() + 1
    lum = rgb.mean(2)
    lab, _ = ndimage.label((lum < 38) & solid)
    blobs = []
    for j, sl in enumerate(ndimage.find_objects(lab)):
        h = sl[0].stop - sl[0].start
        w = sl[1].stop - sl[1].start
        area = (lab[sl] == j + 1).sum()
        if 0.06 * height < h < 0.3 * height and 0.5 < w / h < 1.6 and area > 0.45 * h * w:
            blobs.append((area, (sl[1].start + sl[1].stop) / 2, (sl[0].start + sl[0].stop) / 2, h / 2))
    if len(blobs) < 2:
        return None
    blobs = sorted(blobs, reverse=True)[:2]
    blobs.sort(key=lambda b: b[1])
    (_, x0, y0, r0), (_, x1, y1, r1) = blobs
    return ((x0 + x1) / 2, (y0 + y1) / 2, (r0 + r1) / 2, abs(x1 - x0))


def find_mouth(a, eyes):
    """Boolean mask of the mouth stroke (dilated to cover its soft edge), or None."""
    cx, cy, r, d = eyes
    h, w = a.shape[:2]
    x0, x1 = int(max(0, cx - 0.42 * d)), int(min(w, cx + 0.42 * d))
    y0, y1 = int(max(0, cy + 1.15 * r)), int(min(h, cy + 2.9 * r))
    if x1 - x0 < 5 or y1 - y0 < 5:
        return None
    box = a[y0:y1, x0:x1]
    lum = box[..., :3].astype(np.int16).mean(2)
    solid = box[..., 3] > 200
    lab, n = ndimage.label((lum < 52) & solid)
    best = None
    for j, sl in enumerate(ndimage.find_objects(lab)):
        bw = sl[1].stop - sl[1].start
        bh = sl[0].stop - sl[0].start
        touches_side = sl[1].start == 0 or sl[1].stop == x1 - x0
        area = (lab[sl] == j + 1).sum()
        # a short stroke: not the head's bottom outline (which spans the box),
        # not a speck, not a blob
        if touches_side or not (0.12 * d < bw < 0.7 * d) or bh > 0.55 * r or area < 0.04 * d:
            continue
        mid = (sl[1].start + sl[1].stop) / 2 + x0
        score = area - abs(mid - cx)  # prefer big and centred
        if best is None or score > best[0]:
            best = (score, j + 1)
    if best is None:
        return None
    m = np.zeros(a.shape[:2], bool)
    m[y0:y1, x0:x1] = lab == best[1]
    grown = ndimage.binary_dilation(m, iterations=3)
    # never paint over anything else dark nearby (head outline, neck)
    other_dark = (a[..., :3].astype(np.int16).mean(2) < 52) & ~ndimage.binary_dilation(m, iterations=1)
    other_dark = ndimage.binary_dilation(other_dark & ~m, iterations=1) & ~m
    return grown & ~other_dark & (a[..., 3] > 200)


def inpaint(a, mask, iters=400):
    """Fill mask by repeated neighbour averaging (harmonic fill) on a local crop."""
    ys, xs = np.nonzero(mask)
    pad = 6
    y0, y1 = max(0, ys.min() - pad), min(a.shape[0], ys.max() + pad + 1)
    x0, x1 = max(0, xs.min() - pad), min(a.shape[1], xs.max() + pad + 1)
    crop = a[y0:y1, x0:x1, :3].astype(np.float64)
    mk = mask[y0:y1, x0:x1]
    known = ~mk
    # start from the mean of the ring around the hole
    ring = ndimage.binary_dilation(mk, iterations=2) & known
    crop[mk] = crop[ring].mean(0)
    for _ in range(iters):
        p = np.pad(crop, ((1, 1), (1, 1), (0, 0)), mode="edge")
        avg = (p[:-2, 1:-1] + p[2:, 1:-1] + p[1:-1, :-2] + p[1:-1, 2:]) / 4
        crop[mk] = avg[mk]
    out = a.copy()
    out[y0:y1, x0:x1, :3] = np.clip(crop.round(), 0, 255).astype(np.uint8)
    return out


def process_folder(src, dest):
    files = sorted(src.glob("*.png"))
    frames = {int(p.stem): p for p in files}
    order = sorted(frames)
    geo = {}
    for i in order:
        geo[i] = eyes_of(np.array(Image.open(frames[i]).convert("RGBA")))
    found = [i for i in order if geo[i]]
    if not found:
        raise SystemExit(f"{src}: no eyes found in any frame")
    # Squinting or half-closed eyes are detected with the wrong size, which
    # misplaces the mouth search. Trust only fully open eyes and interpolate
    # everything else from the nearest open-eyed frames.
    r_med = float(np.median([geo[i][2] for i in found]))
    d_med = float(np.median([geo[i][3] for i in found]))
    known = [i for i in found if abs(geo[i][2] - r_med) < 0.12 * r_med and abs(geo[i][3] - d_med) < 0.15 * d_med]
    for i in order:
        if i not in known:
            geo[i] = None
    report = {}
    dest.mkdir(parents=True, exist_ok=True)
    for i in order:
        a = np.array(Image.open(frames[i]).convert("RGBA"))
        g = geo[i]
        borrowed = False
        if g is None:
            # blink or pose without detectable eyes: nearest frames' eyes
            prev = max((k for k in known if k < i), default=None)
            nxt = min((k for k in known if k > i), default=None)
            if prev is not None and nxt is not None:
                t = (i - prev) / (nxt - prev)
                g = tuple(pv + t * (nv - pv) for pv, nv in zip(geo[prev], geo[nxt]))
            else:
                g = geo[prev if prev is not None else nxt]
            borrowed = True
        m = find_mouth(a, g)
        if m is not None and m.any():
            a = inpaint(a, m)
            ys, xs = np.nonzero(m)
            report[i] = dict(removed=True, borrowed_eyes=borrowed, box=[int(xs.min()), int(ys.min()), int(xs.max()), int(ys.max())])
        else:
            report[i] = dict(removed=False, borrowed_eyes=borrowed)
        Image.fromarray(a, "RGBA").save(dest / frames[i].name, compress_level=3)
    (dest.parent / f"{dest.parent.name}_mouth.json").write_text(json.dumps(report, indent=0))
    missed = [i for i, r in report.items() if not r["removed"]]
    print(f"{src.parent.name}: {len(order)} frames, mouth removed in {len(order) - len(missed)}, "
          f"not found in {len(missed)}: {missed[:40]}")


def process_image(src, dest):
    im = Image.open(src)
    rgba = np.array(im.convert("RGBA"))
    if im.mode != "RGBA":
        # white background image: treat the background as transparent for measuring only
        lum = rgba[..., :3].astype(np.int16)
        rgba_m = rgba.copy()
        rgba_m[..., 3] = np.where((lum.min(2) > 235), 0, 255)
    else:
        rgba_m = rgba
    g = eyes_of(rgba_m)
    if g is None:
        raise SystemExit("no eyes found")
    m = find_mouth(rgba_m, g)
    if m is None:
        raise SystemExit("no mouth found")
    out = inpaint(rgba, m)
    Image.fromarray(out if im.mode == "RGBA" else out[..., :3], "RGBA" if im.mode == "RGBA" else "RGB").save(dest)
    ys, xs = np.nonzero(m)
    print(f"mouth removed at x {xs.min()}-{xs.max()}, y {ys.min()}-{ys.max()} -> {dest}")


def main():
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    src, dest = Path(sys.argv[1]), Path(sys.argv[2])
    if src.is_dir():
        process_folder(src, dest)
    else:
        process_image(src, dest)


if __name__ == "__main__":
    main()
