#!/usr/bin/env python3
"""Build RevBot's atlas from the v3 videos (the charcoal, yellow-striped look).

    python tools/video_frames.py v3-jump F:/Downloads/revbot-jump.mp4      (etc., see VIDEOS)
    python tools/build_v3.py

Reads the cut frames in art/video/<name>/frames (made by video_frames.py) and
writes, replacing the old red atlas:
  src/DesktopPet/Assets/atlas.png / atlas.json / pet.ico
  art/build/v3-contact.png   QA sheet: every clip's frames on their anchor, ground in red

Every video is scaled so RevBot stands TARGET_H pixels tall in its reference
frame (the neutral first pose); the sleep video, where the camera zooms, is
scaled per frame by the width of his head. Frames are placed relative to an
anchor, the ground point under him: fixed per video (the camera doesn't move,
so hops and leans keep their real motion), or per frame for the zooming video.
"""
import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage

ROOT = Path(__file__).resolve().parent.parent
VIDEO = ROOT / "art" / "video"
ASSETS = ROOT / "src" / "DesktopPet" / "Assets"
BUILD = ROOT / "art" / "build"
PAD = 3
TARGET_H = 172          # px tall standing, at display scale 1 (the red RevBot was 162)

# The source videos (Dave, 2026-09-28) and the frame each is measured from.
# zoom: the camera moved in this one, so scale and anchor are taken per frame.
VIDEOS = {
    "v3-jump":      dict(file="revbot-jump.mp4", ref=0),
    "v3-wts":       dict(file="gemini_generated_video_a693868e.mp4", ref=24, eyes="fixed with --fix-eyes --v3"),
    "v3-walk":      dict(file="revbot_walk.mp4", ref=0),
    "v3-wait":      dict(file="gemini_generated_video_5aacc8c9.mp4", ref=0),
    "v3-hammer":    dict(file="revbot_hammer.mp4", ref=0),
    "v3-box":       dict(file="revbot_box.mp4", ref=0),
    "v3-sleep":     dict(file="revbot_sleep.mp4", ref=0, zoom="median"),
    "v3-celebrate": dict(file="revbot_celebrate.mp4", ref=0),
    "v3-stretch":   dict(file="gemini_generated_video_8820c9ab.mp4", ref=0, zoom="max"),   # head turns read narrow
    # on magenta (cut with video_frames.py --key)
    "v3-pickedup":  dict(file="revbot_pickedup.mp4", ref=0),
    "v3-dizzy":     dict(file="revbot_dizzy.mp4", ref=0),
    "v3-sit":       dict(file="revbot_sitting.mp4", ref=0),                     # green block keyed out too
    "v3-360":       dict(file="gemini_generated_video_67967726.mp4", ref=0),    # legs stretch after frame ~30
    "v3-look":      dict(file="revbot_lookaround.mp4", ref=0),                  # cut with --key --fix-eyes --v3 (its "up" frames roll the eyes white)
    "v3-climb2":    dict(file="revbot_climbing2.mp4", ref=232),                 # the camera zooms in by frame 24, then only rises with him: one scale from 24 on
    "v3-looksheet": dict(file="art/source/revbot-v3/look-sheet-16.png", ref=0),  # an image (Dave, 2026-09-30), cut with tools/sheet_cells.py 8 2
    "v3-think2":    dict(file="revbot_thinking2.mp4", ref=0),
}


def span(a, b, step=2):
    return list(range(a, b + 1, step))


def pingpong(frames):
    return frames + frames[-2:0:-1]


def without(frames, *ranges):
    """Frames minus the given (a, b) ranges: squints and glares he shouldn't make."""
    return [f for f in frames if not any(a <= f <= b for a, b in ranges)]


# name: (video, source frames in play order, fps, loop[, "flip"][, "rest"]). Frames are
# 24 fps video frames; every 2nd one plays at 12 fps, which keeps the video's speed.
# "flip" mirrors the frames: the walk video steps toward screen left.
# "rest": anchor on the clip's own feet, not the reference frame's (he is lifted
# off the ground in the video; in the pet the cursor holds him).
# "headlock": pin the top of his head (a climb loop: the camera rose with him).
# "ledge": anchor on the green block's top edge in each frame (metrics "ledge"):
# the pull-over lines up with the window top he climbs onto.
CLIPS = {
    "idle":         ("v3-jump", pingpong(span(0, 54, 3)), 8, True),
    "fidget_blink": ("v3-jump", span(54, 72), 12, False),
    "fidget_tilt":  ("v3-celebrate", span(10, 38), 12, False),
    "fidget_chest": ("v3-celebrate", span(74, 112), 12, False),
    "fidget_lean":  ("v3-celebrate", span(152, 180), 12, False),
    "happy":        ("v3-celebrate", span(186, 216), 12, False),
    "walk_right":   ("v3-walk", "loop:100:190:20:60", 12, True, "flip"),
    "wave":         ("v3-wts", span(18, 60), 12, False),
    # thinking hard (the floating math is drawn on top in code): scratches and taps his head,
    # then tilts it round and back to the front; his eyes shut at 16-19, 122-128 and 146-150
    "think":        ("v3-think2", without(span(30, 118, 3) + span(132, 192, 3), (122, 128), (146, 150)), 8, False),
    "shrug":        ("v3-wts", span(146, 190), 12, False),
    "waiting":      ("v3-wait", pingpong(span(36, 80)), 12, True),
    "failed":       ("v3-wait", span(118, 216, 3), 8, False),
    "jump":         ("v3-jump", span(84, 150), 12, False),
    "held":         ("v3-jump", [116], 1, True),
    "land":         ("v3-jump", span(132, 150), 12, False),
    "working":      ("v3-hammer", "loop:40:230:24:72", 12, True),
    "review":       ("v3-box", span(126, 228, 3), 8, True),
    "doze_off":     ("v3-sleep", span(42, 86, 3), 8, False),   # from the droop; the arms-up stretch confuses the zoom fix
    "sleeping":     ("v3-sleep", pingpong(span(90, 180, 3)), 8, True),
    "wake":         ("v3-sleep", span(184, 222), 12, False),
    "stretch":      ("v3-stretch", span(6, 30) + span(34, 178, 4) + span(182, 222), 12, False),
    # magenta videos, 2026-09-28
    "held":         ("v3-pickedup", "loop:56:140:20:56", 12, True, "rest"),   # dangling, kicking
    "air":          ("v3-jump", pingpong(span(111, 126, 3)), 12, True, "rest"),   # his own jumps, arms up (the physics lifts him)
    # climbing a window too tall to jump onto (2026-09-30): turn to the wall and grab it,
    # climb (loop, seen from behind), then over the top edge and turn round on it
    "climb_start":  ("v3-climb2", span(26, 46), 12, False, "rest"),
    "climb_start_left": ("v3-climb2", span(26, 46), 12, False, "rest", "flip"),
    "climb":        ("v3-climb2", "loop:48:152:16:60", 12, True, "headlock"),
    "climb_top":    ("v3-climb2", span(154, 238, 2), 12, False, "ledge"),
    "land_hard":    ("v3-pickedup", span(143, 152, 3) + span(182, 224, 3), 8, False),  # skips 155-179, eyes shut
    "dizzy":        ("v3-dizzy", without(span(12, 96, 3), (38, 44), (94, 99)) + span(165, 192, 3), 8, False),
    "sit":          ("v3-sit", span(36, 150, 3) + span(207, 237, 3), 8, False),   # skips 198-206, angry glare
}

# look: Atlas.LookDirections (16) poses, clockwise from straight up, from Dave's 16-pose
# sheet (art/source/revbot-v3/look-sheet-16.png, cells numbered 0-15). Its left side has
# the full set (tipped forward, level, tilted back); its right side has no tilted-back
# poses and turns his whole body at cells 3 and 4, so up-right and right are the left
# poses mirrored. Every direction a different pose. (direction degrees: (cell, mirrored))
LOOK_VIDEO = "v3-looksheet"
LOOK = {
    0: (15, False), 22.5: (14, True), 45: (13, True), 67.5: (2, False), 90: (12, True), 112.5: (5, False),
    135: (6, False), 157.5: (7, False), 180: (8, False), 202.5: (9, False), 225: (10, False), 247.5: (11, False),
    270: (12, False), 292.5: (13, False), 315: (14, False), 337.5: (1, True),
}
LOOK_DIRECTIONS = 16


# ---------------------------------------------------------------- frames

_cache = {}


def load(video, i):
    key = (video, i)
    if key not in _cache:
        _cache[key] = np.array(Image.open(VIDEO / video / "frames" / f"{i:04d}.png").convert("RGBA"))
    return _cache[key]


def metrics(video):
    m = json.loads((VIDEO / video / "metrics.json").read_text())
    return {f["index"]: f for f in m["frames"] if "height" in f}


def keep_robot(rgba, ground):
    """Keep RevBot and whatever touches or sits right beside him (the box, sparks,
    his antenna glow); drop small far-off bits (tossed bolts, stray specks) and
    anything lying below the floor line."""
    solid = rgba[..., 3] > 24
    lab, n = ndimage.label(solid)
    if n <= 1:
        return rgba
    sizes = ndimage.sum(solid, lab, range(1, n + 1))
    objs = ndimage.find_objects(lab)
    big = int(np.argmax(sizes))
    y0, y1 = objs[big][0].start, objs[big][0].stop
    x0, x1 = objs[big][1].start, objs[big][1].stop
    gy, gx = 0.25 * (y1 - y0), 0.25 * (x1 - x0)
    keep = np.zeros(n + 1, bool)
    for i, o in enumerate(objs):
        oy0, oy1, ox0, ox1 = o[0].start, o[0].stop, o[1].start, o[1].stop
        near = oy1 > y0 - gy and oy0 < y1 + gy and ox1 > x0 - gx and ox0 < x1 + gx
        if i == big or (near and sizes[i] >= 12 and oy0 < ground - 2):
            keep[i + 1] = True
    out = rgba.copy()
    faint = (rgba[..., 3] > 0) & ~solid
    # soft edge pixels belong to whatever solid part is next to them
    grown = ndimage.grey_dilation(np.where(keep[lab], lab, 0), size=3)
    out[~(keep[lab] | (faint & (grown > 0)))] = 0
    return out


def best_loop(video, lo, hi, min_len, max_len):
    """Start and end frames whose silhouettes match best, for a seamless loop."""
    def mask(i):
        a = load(video, i)[..., 3] > 128
        return np.asarray(Image.fromarray(a.astype(np.uint8) * 255).resize((96, 96))) > 127
    masks = {i: mask(i) for i in range(lo, hi + 1)}
    best = None
    for a in range(lo, hi - min_len + 1):
        for b in range(a + min_len, min(hi, a + max_len) + 1):
            d = float(np.logical_xor(masks[a], masks[b]).mean())
            if best is None or d < best[0]:
                best = (d, a, b)
    d, a, b = best
    print(f"  loop {video} {a}..{b} (mismatch {d:.4f})")
    return span(a, b - 2)   # the end frame matches the start: don't play both


def smooth(values, width=15):
    v = np.array(values, float)
    out = v.copy()
    h = width // 2
    for i in range(len(v)):
        out[i] = np.median(v[max(0, i - h):i + h + 1])
    return out


def placement(video):
    """Per frame: (scale, anchor x, ground y) in the video's crop coordinates."""
    cfg = VIDEOS[video]
    m = metrics(video)
    ref = m[cfg["ref"]]
    s0 = TARGET_H / ref["height"]
    if not cfg.get("zoom"):
        return lambda i: (s0, ref["head_cx"], ref["bbox"][3])
    idx = sorted(m)
    raw = [m[i]["head_w"] for i in idx]
    # median: steady zoom; max over +-20 frames: a head turned sideways is narrower, not further away
    hw = smooth(raw) if cfg["zoom"] == "median" else np.array([max(raw[max(0, k - 20):k + 21]) for k in range(len(raw))], float)
    by = dict(zip(idx, hw))
    ref_w = by[cfg["ref"]]
    return lambda i: (s0 * ref_w / by[i], m[i]["head_cx"], m[i]["bbox"][3])


# ---------------------------------------------------------------- build

def cut(video, f, s, ax, ground, flip=False):
    """One frame, scaled and cropped, with its offset from the anchor."""
    rgba = keep_robot(load(video, f), ground)
    ys, xs = np.nonzero(rgba[..., 3] > 0)
    x0, y0, x1, y1 = xs.min(), ys.min(), xs.max() + 1, ys.max() + 1
    im = Image.fromarray(rgba[y0:y1, x0:x1], "RGBA").convert("RGBa")
    im = im.resize((max(1, round(im.width * s)), max(1, round(im.height * s))), Image.LANCZOS).convert("RGBA")
    ox, oy = int(round((x0 - ax) * s)), int(round((y0 - ground) * s))
    if flip:
        im = im.transpose(Image.FLIP_LEFT_RIGHT)
        ox = -(ox + im.width)  # mirrored about the anchor
    return np.array(im), ox, oy


def main():
    rows = {}          # clip -> list of (rgba, ox, oy)
    clips = {}
    for name, spec in CLIPS.items():
        video, frames, fps, loop = spec[:4]
        flip = "flip" in spec[4:]
        if isinstance(frames, str):
            _, lo, hi, mn, mx = frames.split(":")
            frames = best_loop(video, int(lo), int(hi), int(mn), int(mx))
        place = placement(video)
        unique = list(dict.fromkeys(frames))
        pos = {f: k for k, f in enumerate(unique)}
        m = metrics(video)
        rest = lock = None
        if "rest" in spec[4:]:
            rest = float(np.median([m[f]["bbox"][3] for f in unique]))
        if "headlock" in spec[4:]:
            lock = float(np.median([m[f]["height"] for f in unique]))
        steady_x = float(np.median([m[f]["head_cx"] for f in unique]))
        out = []
        for f in unique:
            s, ax, ground = place(f)
            if rest is not None:
                ground = rest
            elif lock is not None:
                ax, ground = steady_x, m[f]["bbox"][1] + lock   # head top stays put; x keeps his sway
            elif "ledge" in spec[4:]:
                ax, ground = VIDEOS[video].get("ledge_x", place(VIDEOS[video]["ref"])[1]), m[f]["ledge"]
            out.append(cut(video, f, s, ax, ground, flip))
        rows[name] = out
        clips[name] = dict(row=name, frames=[pos[f] for f in frames], fps=fps, loop=loop)
        print(f"{name:13s} {video:13s} {len(frames):3d} steps, {len(unique):3d} frames")

    # look: one image per distinct pose; the right-hand directions reuse them mirrored
    place = placement(LOOK_VIDEO)
    turns = [LOOK[360 / LOOK_DIRECTIONS * i] for i in range(LOOK_DIRECTIONS)]
    distinct = sorted({f for f, _ in turns})
    rows["look"] = [cut(LOOK_VIDEO, f, *place(f)) for f in distinct]
    print(f"{'look':13s} {LOOK_VIDEO:13s} {LOOK_DIRECTIONS:3d} directions from frames {distinct}")

    # pack
    placed = [(row, i, img, ox, oy) for row, fr in rows.items() for i, (img, ox, oy) in enumerate(fr)]
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
        if row != "look":
            frames.append(dict(row=row, index=idx, x=px, y=py, w=img.shape[1], h=img.shape[0], ox=ox, oy=oy))
    looks = {idx: (px, py, img, ox, oy) for (row, idx, img, ox, oy), (px, py) in zip(placed, pos) if row == "look"}
    for i, (f, right) in enumerate(turns):
        px, py, img, ox, oy = looks[distinct.index(f)]
        # a mirrored frame keeps the unmirrored offset; Atlas mirrors it about the anchor
        frames.append(dict(row="look", index=i, x=px, y=py, w=img.shape[1], h=img.shape[0], ox=ox, oy=oy, mirror=bool(right)))

    ASSETS.mkdir(parents=True, exist_ok=True)
    atlas.save(ASSETS / "atlas.png", optimize=True)
    (ASSETS / "atlas.json").write_text(json.dumps(dict(version=1, pad=PAD, scale=1.0, frames=frames, clips=clips), indent=1))

    idle0 = next(f for f in frames if f["row"] == "idle" and f["index"] == 0)
    icon = atlas.crop((idle0["x"], idle0["y"], idle0["x"] + idle0["w"], idle0["y"] + idle0["h"]))
    side = max(icon.size)
    sq = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    sq.paste(icon, ((side - icon.width) // 2, side - icon.height))
    sq.save(ASSETS / "pet.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])

    # QA contact sheet: up to 12 frames per clip on the ground line
    left = min(f["ox"] for f in frames); right = max(f["ox"] + f["w"] for f in frames)
    top = min(f["oy"] for f in frames); bottom = max(f["oy"] + f["h"] for f in frames)
    cw, ch = right - left + 8, bottom - top + 8
    cols = 12
    sheet = Image.new("RGBA", (90 + cw * cols, ch * len(rows)), (60, 64, 72, 255))
    d = ImageDraw.Draw(sheet)
    for r, row in enumerate(rows):
        gy = r * ch + 4 - top
        d.text((4, r * ch + 4), row, fill=(235, 235, 235, 255))
        d.line([90, gy, sheet.width, gy], fill=(220, 60, 60, 255))
        rf = [f for f in frames if f["row"] == row]
        pick = rf if len(rf) <= cols else [rf[round(k * (len(rf) - 1) / (cols - 1))] for k in range(cols)]
        for k, f in enumerate(pick):
            im = atlas.crop((f["x"], f["y"], f["x"] + f["w"], f["y"] + f["h"]))
            sheet.alpha_composite(im, (90 + k * cw + 4 - left + f["ox"], gy + f["oy"]))
    BUILD.mkdir(parents=True, exist_ok=True)
    sheet.save(BUILD / "v3-contact.png")
    print(json.dumps(dict(frames=len(frames), clips=len(clips), atlas=[atlas.width, atlas.height])))


if __name__ == "__main__":
    main()
