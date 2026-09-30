#!/usr/bin/env python3
"""Cut a white-background character video into matted RGBA frames + metrics.

    python tools/video_frames.py NAME VIDEO [--fix-eyes] [--v3] [--keep-white A-B] [--key]

--key: the video is on a flat magenta backdrop (plus, optionally, a flat green
block): key on colour instead of finding the white background.

--fix-eyes repaints cartoon eyes (white with a pupil) as RevBot's solid
black eyes with the standard highlights; with --v3, the new design's single
highlight (upper right) instead of two. --keep-white A-B keeps large white
patches in frames A to B (something white he holds, like a sheet of paper);
everywhere else, enclosed white that matches the background is cut as a gap.

Writes (never touches the source video):
  art/video/NAME/frames/NNNN.png   RGBA, all cropped with the SAME box so
                                   motion between frames is preserved
  art/video/NAME/metrics.json      per-frame measurements used to pick loops

Frames are streamed from ffmpeg, so full-size frames never hit the disk.
"""
import json
import subprocess
import sys
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "art" / "video"
MARGIN = 24


def probe(video):
    out = subprocess.run(
        ["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries",
         "stream=width,height,r_frame_rate", "-of", "json", str(video)],
        check=True, capture_output=True, text=True).stdout
    s = json.loads(out)["streams"][0]
    num, den = s["r_frame_rate"].split("/")
    return s["width"], s["height"], float(num) / float(den)


def frames(video, w, h):
    proc = subprocess.Popen(
        ["ffmpeg", "-v", "error", "-i", str(video), "-f", "rawvideo", "-pix_fmt", "rgb24", "-"],
        stdout=subprocess.PIPE)
    size = w * h * 3
    try:
        while True:
            buf = proc.stdout.read(size)
            if len(buf) < size:
                break
            yield np.frombuffer(buf, np.uint8).reshape(h, w, 3)
    finally:
        proc.stdout.close()
        proc.wait()


def bg_like(c):
    sat = c.max(2) - c.min(2)
    return (sat < 30) & (c.min(2) > 170)


def rough_box(rgb):
    """Bounding box of anything that is not light background."""
    m = ~bg_like(rgb.astype(np.int16))
    ys, xs = np.nonzero(m)
    if len(xs) == 0:
        return None
    return xs.min(), ys.min(), xs.max() + 1, ys.max() + 1


def eye_blobs(lum, fg, height):
    """Large, round, very dark blobs (the eyes). Body grey is ~67, eyes ~15."""
    lab, _ = ndimage.label((lum < 38) & fg)
    found = []
    for j, sl in enumerate(ndimage.find_objects(lab)):
        h = sl[0].stop - sl[0].start
        w = sl[1].stop - sl[1].start
        area = (lab[sl] == j + 1).sum()
        if 0.06 * height < h < 0.3 * height and 0.5 < w / h < 1.6 and area > 0.45 * h * w:
            found.append((area, sl))
    found.sort(key=lambda t: -t[0])
    return [sl for _, sl in found[:2]]


# Highlight layout measured on good frames (offset x, offset y, radius), in
# units of the eye radius, for the eye on the left and the eye on the right.
HIGHLIGHTS = {
    "left": [(0.255, -0.47, 0.246), (-0.525, 0.59, 0.157)],
    "right": [(0.232, -0.475, 0.243), (-0.385, 0.71, 0.151)],
}
# The v3 design (2026-09-26) has only the big upper-right highlight, measured on
# art/source/revbot-v3/reference-white.png: left (0.267, -0.464, 0.248), right (0.236, -0.472, 0.241).
HIGHLIGHTS_V3 = {
    "left": [(0.267, -0.464, 0.248)],
    "right": [(0.236, -0.472, 0.241)],
}
FIX_EYES = False
KEEP_WHITE = []   # frame ranges where he holds something white (--keep-white A-B)


def repaint_eyes(c, fg0, height):
    """Turn cartoon eyes (white with a pupil) back into RevBot eyes: a solid
    glossy-black disc with the two standard highlights. Edits c in place and
    returns how many eyes were repainted."""
    lum = c.mean(2)
    light = (c.min(2) > 185)
    lab, _ = ndimage.label((lum < 45) & fg0)
    found = []
    for j, sl in enumerate(ndimage.find_objects(lab)):
        h = sl[0].stop - sl[0].start
        w = sl[1].stop - sl[1].start
        if not (0.06 * height < h < 0.3 * height and 0.6 < w / h < 1.6):
            continue
        comp = lab[sl] == j + 1
        filled = ndimage.binary_fill_holes(comp)
        if filled.sum() < 0.6 * h * w:
            continue
        # Repaint every open eye, not just obviously wrong ones: the frames
        # where the eyes roll up or back are half-and-half and slip past any
        # threshold, and uniform highlights look steadier anyway.
        found.append((sl, filled))
    if not found:
        return 0
    found.sort(key=lambda t: t[0][1].start)
    dark = np.array([14, 14, 16], np.int16)
    for k, (sl, filled) in enumerate(found):
        region = c[sl]
        region[filled] = dark
        ys, xs = np.nonzero(filled)
        cy, cx = ys.mean(), xs.mean()
        r = np.sqrt(filled.sum() / np.pi)
        side = "left" if (len(found) == 2 and k == 0) else "right"
        # anti-aliased highlights: draw at 4x, then average down
        S = 4
        hh, ww = filled.shape
        yy, xx = np.mgrid[0:hh * S, 0:ww * S] / S
        cov = np.zeros((hh * S, ww * S))
        for ox, oy, rr in HIGHLIGHTS[side]:
            cov = np.maximum(cov, (np.hypot(xx - (cx + ox * r), yy - (cy + oy * r)) < rr * r).astype(float))
        cov = cov.reshape(hh, S, ww, S).mean((1, 3)) * filled
        white = np.array([250, 250, 250], np.float64)
        region[:] = np.round(region * (1 - cov[..., None]) + white * cov[..., None]).astype(np.int16)
    return len(found)


def matte(crop, keep_big_white=False):
    """Straight RGBA for a crop of a white-background frame, plus the fg mask.
    keep_big_white: this frame shows something white he holds (the paper), so a
    large enclosed white patch is that, not a gap."""
    c = crop.astype(np.int16)
    light = bg_like(c)
    lab, _ = ndimage.label(light)
    edge = set(np.concatenate([lab[0], lab[-1], lab[:, 0], lab[:, -1]]).tolist()) - {0}
    bg = np.isin(lab, list(edge))
    fg0 = ~bg
    ys, _ = np.nonzero(fg0)
    height = (ys.max() - ys.min() + 1) if len(ys) else c.shape[0]
    if FIX_EYES and repaint_eyes(c, fg0, height):
        # the sclera was enclosed light; re-derive what counts as light now
        light = bg_like(c)
        lab, _ = ndimage.label(light)
        edge = set(np.concatenate([lab[0], lab[-1], lab[:, 0], lab[:, -1]]).tolist()) - {0}
        bg = np.isin(lab, list(edge))
        fg0 = ~bg
    lum = c.mean(2)
    eyes = eye_blobs(lum, fg0, height)
    boxes = [(sl[0].start - 1, sl[0].stop + 1, sl[1].start - 1, sl[1].stop + 1) for sl in eyes]
    # enclosed light regions: keep eye highlights and anything above the head
    # (a lit antenna ball has a white-hot core), drop gaps between limbs
    rows0 = fg0.sum(1)
    head_top0 = int(np.argmax(rows0 > 0.35 * rows0.max())) if rows0.max() else 0
    # A gap between limbs shows the background's own white; the shine on his metal is
    # grey (180-220 against ~250), so it stays. Paper is background-white too: in the
    # frames where he holds it (keep_big_white) a large white patch stays.
    border = np.concatenate([c[0], c[-1], c[:, 0], c[:, -1]])
    bg_col = np.median(border, 0)
    fg_area = max(1, int(fg0.sum()))
    for i in set(np.unique(lab).tolist()) - {0} - edge:
        comp = lab == i
        cy, cx = np.nonzero(comp)
        if cy.max() < head_top0:
            continue
        if any(y0 <= cy.min() and cy.max() < y1 and x0 <= cx.min() and cx.max() < x1 for y0, y1, x0, x1 in boxes):
            continue
        near_bg = np.abs(c[comp] - bg_col).max(1).mean() <= 15
        if near_bg and (not keep_big_white or comp.sum() <= 0.015 * fg_area):
            bg |= comp
    fg = ~bg
    lab2, n2 = ndimage.label(fg)
    if n2 == 0:
        return None, None, []
    sizes = ndimage.sum(fg, lab2, range(1, n2 + 1))
    keep = np.isin(lab2, [i + 1 for i, s in enumerate(sizes) if s >= max(40, 0.002 * sizes.max())])
    # soft outer edge: un-mix the outline ring against white
    ring = keep & ~ndimage.binary_erosion(keep)
    out = np.zeros(c.shape[:2] + (4,), np.float64)
    out[..., :3] = c
    out[..., 3] = keep * 255.0
    bgl = 250.0
    alpha = np.clip((bgl - lum) / (bgl - 30.0), 0.0, 1.0)
    a_r = np.maximum(alpha[ring], 0.05)
    col = (c[ring] - (1 - a_r)[:, None] * 255.0) / a_r[:, None]
    out[ring, :3] = np.clip(col, 0, 255)
    out[ring, 3] = alpha[ring] * 255
    # A glow painted over white (e.g. the lit antenna ball) would come out as
    # an opaque pale-yellow patch. Un-mix it into translucent yellow so it
    # glows over any desktop. Only above the head, and never inside the ball.
    rows = keep.sum(1)
    if rows.max() > 0:
        head_top = int(np.argmax(rows > 0.35 * rows.max()))
        zone = np.zeros_like(keep)
        zone[:head_top] = True
        cand = keep & zone & (c[..., 2] > 110) & (c[..., 0] > 190) & (c[..., 1] > 170) & (c[..., 0] - c[..., 2] > 30)
        if cand.any():
            inside_ball = ndimage.binary_fill_holes((lum < 90) & zone)
            glow = cand & ~inside_ball
            a = np.clip((255.0 - c[..., 2]) / (255.0 - 70.0), 0.02, 1.0)
            unmixed = (c - (1 - a)[..., None] * 255.0) / a[..., None]
            out[glow, :3] = np.clip(unmixed[glow], 0, 255)
            out[glow, 3] = a[glow] * 255
    return out.round().astype(np.uint8), keep, eyes


# ---------------------------------------------------------------- colour key
# The later videos (2026-09-28 on) are shot on flat magenta, and the sit video
# adds a flat green block he sits on. Nothing of RevBot's is either colour: all
# his colours have blue <= green (grey, white, black, orange, yellow) and
# green <= red, so the key never has to guess what is background.

def key_colour(c):
    """0..1 per pixel: how much it looks like the magenta backdrop or the green block."""
    c = c.astype(np.float32)
    r, g, b = c[..., 0], c[..., 1], c[..., 2]
    mag = np.clip((np.minimum(r, b) - g - 50) / 120, 0, 1)
    grn = np.clip((g - np.maximum(r, b) - 50) / 120, 0, 1)
    return mag, grn


def rough_box_key(rgb):
    mag, grn = key_colour(rgb[::2, ::2])
    m = ndimage.binary_opening(np.maximum(mag, grn) < 0.5, iterations=2)   # drop grain specks
    ys, xs = np.nonzero(m)
    if len(xs) == 0:
        return None
    return 2 * xs.min(), 2 * ys.min(), 2 * xs.max() + 2, 2 * ys.max() + 2


def matte_key(crop):
    """Straight RGBA for a crop of a magenta-backdrop frame, plus the fg mask."""
    c = crop.astype(np.float32)
    mag, grn = key_colour(c)
    k = np.maximum(mag, grn)
    if (grn > 0.9).any():
        # where the block meets the magenta the two blend into a grey-green seam
        # that neither key catches (measured: R ~ B, R + G ~ 260-310)
        near = ndimage.binary_dilation(mag > 0.9, iterations=5) & ndimage.binary_dilation(grn > 0.9, iterations=5)
        s = c[..., 0] + c[..., 1]
        k = np.where(near & (np.abs(c[..., 0] - c[..., 2]) < 40) & (s > 220) & (s < 330), 1.0, k)
    alpha = 1 - k
    solid = alpha > 0.5
    lab, n = ndimage.label(solid)
    if n == 0:
        return None, None, []
    sizes = ndimage.sum(solid, lab, range(1, n + 1))
    keep = np.isin(lab, [i + 1 for i, s in enumerate(sizes) if s >= max(40, 0.002 * sizes.max())])
    alpha = np.where(ndimage.binary_dilation(keep, iterations=2) & ~(solid & ~keep), alpha, 0)
    # un-mix each soft edge from whichever backdrop it sits on
    ref_m = np.median(c[mag > 0.95], 0) if (mag > 0.95).any() else np.array([255, 0, 255], np.float32)
    ref_g = np.median(c[grn > 0.95], 0) if (grn > 0.95).any() else np.array([0, 255, 0], np.float32)
    bgc = np.where((grn > mag)[..., None], ref_g, ref_m)
    a = np.maximum(alpha, 0.05)[..., None]
    fg = (c - (1 - a) * bgc) / a
    # spill: whatever blue exceeds green is magenta (take it off red too), whatever
    # green exceeds red is the block's green
    ex = np.maximum(0, fg[..., 2] - fg[..., 1])
    fg[..., 0] -= ex
    fg[..., 2] -= ex
    fg[..., 1] -= np.maximum(0, fg[..., 1] - fg[..., 0])
    out = np.zeros(c.shape[:2] + (4,), np.float32)
    out[..., :3] = np.clip(fg, 0, 255)
    out[..., 3] = alpha * 255
    ys, _ = np.nonzero(keep)
    height = ys.max() - ys.min() + 1
    eyes = eye_blobs(c.mean(2), keep, height)
    return out.round().astype(np.uint8), keep, eyes


def measure(keep, eyes, rgb):
    ys, xs = np.nonzero(keep)
    x0, y0, x1, y1 = int(xs.min()), int(ys.min()), int(xs.max()) + 1, int(ys.max()) + 1
    h = y1 - y0
    head = keep[int(y0 + 0.15 * h):int(y0 + 0.5 * h)]
    hx = np.nonzero(head)[1]
    head_cx = float(np.median(hx)) if len(hx) else (x0 + x1) / 2
    # head width: widest row in the head band
    row_w = [(np.nonzero(r)[0].max() - np.nonzero(r)[0].min() + 1) if r.any() else 0 for r in head]
    head_w = int(max(row_w)) if row_w else 0
    band = keep[max(0, int(y1 - 0.07 * h)):int(y1 - 0.015 * h) + 1]
    bx = np.nonzero(band.any(0))[0]
    feet_cx = float((bx.min() + bx.max()) / 2) if len(bx) else head_cx
    eye_h = [sl[0].stop - sl[0].start for sl in eyes]
    eye_cx = [(sl[1].start + sl[1].stop) / 2 for sl in eyes]
    facing = None
    if len(eyes) == 2 and head_w:
        # eyes shift toward the side the head faces in a 3/4 view
        facing = (float(np.mean(eye_cx)) - head_cx) / head_w
    return dict(bbox=[x0, y0, x1, y1], height=h, head_cx=round(head_cx, 1), head_w=head_w,
                feet_cx=round(feet_cx, 1), eyes=len(eyes), eye_h=round(float(np.mean(eye_h)), 1) if eye_h else None,
                facing=round(facing, 3) if facing is not None else None)


def main():
    global FIX_EYES, HIGHLIGHTS
    argv = sys.argv[1:]
    if "--keep-white" in argv:
        k = argv.index("--keep-white")
        a, b = argv[k + 1].split("-")
        KEEP_WHITE.append((int(a), int(b)))
        del argv[k:k + 2]
    args = [a for a in argv if not a.startswith("--")]
    FIX_EYES = "--fix-eyes" in sys.argv
    if "--v3" in sys.argv:
        HIGHLIGHTS = HIGHLIGHTS_V3
    keyed = "--key" in sys.argv
    if len(args) != 2:
        raise SystemExit(__doc__)
    name, video = args[0], Path(args[1])
    w, h, fps = probe(video)
    dest = OUT / name
    (dest / "frames").mkdir(parents=True, exist_ok=True)

    # pass 1: union box of the character over the whole clip
    ux0 = uy0 = 10 ** 9
    ux1 = uy1 = -1
    diffs, prev = [], None
    for f in frames(video, w, h):
        b = rough_box_key(f) if keyed else rough_box(f)
        if b:
            ux0, uy0, ux1, uy1 = min(ux0, b[0]), min(uy0, b[1]), max(ux1, b[2]), max(uy1, b[3])
        small = f[::8, ::8].astype(np.int16)
        diffs.append(float(np.abs(small - prev).mean()) if prev is not None else 0.0)
        prev = small
    box = tuple(int(v) for v in (max(0, ux0 - MARGIN), max(0, uy0 - MARGIN), min(w, ux1 + MARGIN), min(h, uy1 + MARGIN)))
    touches = dict(top=bool(uy0 <= 1), bottom=bool(uy1 >= h - 1), left=bool(ux0 <= 1), right=bool(ux1 >= w - 1))

    # pass 2: matte, measure, save
    metrics = []
    for i, f in enumerate(frames(video, w, h)):
        crop = f[box[1]:box[3], box[0]:box[2]]
        rgba, keep, eyes = matte_key(crop) if keyed else matte(crop, any(a <= i <= b for a, b in KEEP_WHITE))
        if rgba is None:
            metrics.append(dict(index=i, empty=True))
            continue
        Image.fromarray(rgba, "RGBA").save(dest / "frames" / f"{i:04d}.png", optimize=False, compress_level=3)
        m = measure(keep, eyes, crop)
        m.update(index=i, cut=round(diffs[i], 2))
        metrics.append(m)

    info = dict(video=str(video), size=[w, h], fps=fps, crop=list(box), touches=touches, frames=metrics)
    (dest / "metrics.json").write_text(json.dumps(info, indent=1))
    hs = [m["height"] for m in metrics if "height" in m]
    ew = [m["eye_h"] for m in metrics if m.get("eye_h")]
    blinks = [m["index"] for m in metrics if m.get("eyes", 2) < 2]
    cuts = [m["index"] for m in metrics if m.get("cut", 0) > 12]
    print(json.dumps(dict(name=name, frames=len(metrics), crop=box, touches=touches,
                          height=[min(hs), max(hs)] if hs else None,
                          eye_h=[min(ew), max(ew)] if ew else None,
                          no_eyes_frames=len(blinks), first_no_eyes=blinks[:12], likely_cuts=cuts)))


if __name__ == "__main__":
    main()
