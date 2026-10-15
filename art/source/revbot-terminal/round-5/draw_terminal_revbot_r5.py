"""
Round 5 of the terminal RevBot drafts, drawn from Dave's reference picture
(2026-10-11): a rectangle head about 1.4 times wider than tall with barely
rounded corners, big eyes with the glint at upper right, ears one yellow pixel
wide, an orange antenna to a yellow ball off to the right, a one-row neck, a
body about half the head's width with hazard stripes on the chest plate only,
thin orange arms angled out to yellow claws, short orange legs, yellow shoes.
One character per pixel; '.' is transparent. Writes 1x PNGs and a preview.
"""
import os
import sys

from PIL import Image

OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), "terminal-drafts-5")

PAL = {
    "K": (20, 20, 22),     # outline (light terminals)
    "L": (124, 124, 128),  # head highlight
    "C": (88, 88, 92),     # charcoal
    "c": (62, 62, 66),     # charcoal shade
    "E": (10, 10, 12),     # eye
    "W": (250, 250, 250),  # glint
    "Y": (245, 196, 48),   # yellow: ears, antenna ball, chest, claws, shoes
    "y": (196, 148, 30),   # yellow shade
    "k": (26, 26, 28),     # hazard black (inside the chest plate only)
    "O": (240, 110, 30),   # orange: antenna, neck, arms, legs
}

# 20 x 28 (14 terminal rows). Head 18 x 13; eyes 5 x 6; body 10 x 7.
BODY = [
    "..........YY........",
    "..........Yy........",
    "..........O.........",
    "..LLLLLLLLLLLLLLLL..",
    ".LCCCCCCCCCCCCCCCCc.",
    ".LCCCCCCCCCCCCCCCCc.",
    "YLCCEEECCCCCCEEECCcY",
    "YLCEEWWECCCCEEWWECcY",
    "YLCEEWWECCCCEEWWECcY",
    "YLCEEEEECCCCEEEEECcY",
    "YLCEEEEECCCCEEEEECcY",
    "YLCCEEECCCCCCEEECCcY",
    ".LCCCCCCCCCCCCCCCCc.",
    ".LCCCCCCCCCCCCCCCCc.",
    ".CCCCCCCCCCCCCCCCCc.",
    "..cccccccccccccccc..",
    "........OOOO........",
    "......CCCCCCCC......",
    "....OCCCCCCCCCCO....",
    "...O.CYYkkYYkCc.O...",
    "..O..CYkkYYkkCc..O..",
    ".YYY.CkkYYkkYCc.YYY.",
    ".Y.Y.CCCCCCCCCc.Y.Y.",
    "......cccccccc......",
    ".......O....O.......",
    ".......O....O.......",
    "......YYY..YYY......",
    ".....YYYY..YYYY.....",
]
EYE_TOP = 6


def blink(rows):
    """Eyes shut: one dark line across each eye, the rest face."""
    b = list(rows)
    for r in range(EYE_TOP, EYE_TOP + 6):
        b[r] = b[r].replace("E", "C").replace("W", "C")
    mid = EYE_TOP + 3
    b[mid] = "".join("E" if ch in "EW" else b[mid][x] for x, ch in enumerate(rows[mid]))
    return b


BODY_BLINK = blink(BODY)
HEAD = BODY[:16]          # 20 x 16: eight terminal rows, for the spinner
HEAD_BLINK = BODY_BLINK[:16]


def image(rows):
    w = len(rows[0])
    assert all(len(r) == w for r in rows), [(i, len(r)) for i, r in enumerate(rows) if len(r) != w]
    assert len(rows) % 2 == 0, "a terminal cell holds two pixel rows"
    img = Image.new("RGBA", (w, len(rows)), (0, 0, 0, 0))
    for y, r in enumerate(rows):
        for x, ch in enumerate(r):
            if ch != ".":
                img.putpixel((x, y), PAL[ch] + (255,))
    return img


def outlined(img):
    """One pixel of outline round the figure, for light terminals (the canvas grows by 2)."""
    w, h = img.size
    out = Image.new("RGBA", (w + 2, h + 2), (0, 0, 0, 0))
    out.paste(img, (1, 1))
    src = out.copy()
    for y in range(h + 2):
        for x in range(w + 2):
            if src.getpixel((x, y))[3]:
                continue
            if any(0 <= x + dx < w + 2 and 0 <= y + dy < h + 2 and src.getpixel((x + dx, y + dy))[3]
                   for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1))):
                out.putpixel((x, y), PAL["K"] + (255,))
    return out


def sheet(tiles, bg, z=14, gap=24):
    w = sum(t.width * z for t in tiles) + gap * (len(tiles) + 1)
    h = max(t.height * z for t in tiles) + 2 * gap
    s = Image.new("RGBA", (w, h), bg)
    x = gap
    for t in tiles:
        big = t.resize((t.width * z, t.height * z), Image.NEAREST)
        s.alpha_composite(big, (x, h - gap - big.height))
        x += big.width + gap
    return s


def main():
    os.makedirs(OUT, exist_ok=True)
    tiles = []
    for name, idle, shut in (("revbot-body-20x28", BODY, BODY_BLINK), ("revbot-head-20x16", HEAD, HEAD_BLINK)):
        for tag, rows in (("idle", idle), ("blink", shut)):
            im = image(rows)
            im.save(os.path.join(OUT, f"{name}-{tag}.png"))
            tiles.append(im)
    for tag, rows in (("idle", BODY), ("blink", BODY_BLINK)):
        im = outlined(image(rows))
        im.save(os.path.join(OUT, f"revbot-body-outlined-22x30-{tag}.png"))
        tiles.append(im)
    dark = sheet(tiles, (12, 12, 12, 255))
    light = sheet(tiles, (238, 238, 232, 255))
    both = Image.new("RGBA", (dark.width, dark.height + light.height), (0, 0, 0, 255))
    both.paste(dark, (0, 0))
    both.paste(light, (0, dark.height))
    both.save(os.path.join(OUT, "preview.png"))
    print("wrote", OUT)


if __name__ == "__main__":
    main()
