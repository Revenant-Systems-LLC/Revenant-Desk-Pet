"""
Round 3 of the terminal RevBot drafts, with Dave's notes (2026-10-11): a square
face, bigger eyes, ears one yellow pixel wide, a short neck, hazard stripes on
his chest only, everything else that was striped plain yellow.
One character per pixel; '.' is transparent. Writes 1x PNGs and a preview.
"""
import os
import sys

from PIL import Image

OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), "terminal-drafts-3")

PAL = {
    "K": (20, 20, 22),     # outline (light terminals)
    "L": (124, 124, 128),  # head highlight
    "C": (88, 88, 92),     # charcoal
    "c": (62, 62, 66),     # charcoal shade
    "E": (10, 10, 12),     # eye
    "W": (250, 250, 250),  # glint
    "Y": (245, 196, 48),   # yellow: ears, antenna ball, chest, hands, boots
    "y": (196, 148, 30),   # yellow shade
    "k": (26, 26, 28),     # hazard black (inside the chest plate only)
    "O": (240, 110, 30),   # orange: neck, arms
}

# 16 x 26 (13 terminal rows). Head 14 x 14, square; eyes 5 x 6.
BODY = [
    ".......YY.......",
    ".......Yy.......",
    ".......L........",
    "...LLLLLLLLLL...",
    "..LCCCCCCCCCCc..",
    ".LCCCCCCCCCCCCc.",
    ".LCCCCCCCCCCCCc.",
    ".LCEEECCCCEEECc.",
    "YLEEWWECCEEWWEcY",
    "YLEEWWECCEEWWEcY",
    "YLEEEEECCEEEEEcY",
    "YLEEEEECCEEEEEcY",
    ".LCEEECCCCEEECc.",
    ".CCCCCCCCCCCCCc.",
    ".cCCCCCCCCCCCcc.",
    "..cccccccccccc..",
    "...cccccccccc...",
    ".......OO.......",
    "....CCCCCCCC....",
    "...OCYYkkYYCO...",
    "...OCYkkYYkCO...",
    "...OCkkYYkkCO...",
    "...YccccccccY...",
    ".....CC..CC.....",
    ".....YY..YY.....",
    "....YYY..YYY....",
]
EYE_TOP = 7


def blink(rows):
    """Eyes shut: one dark line across the middle of each eye, the rest face."""
    b = list(rows)
    for r in range(EYE_TOP, EYE_TOP + 6):
        b[r] = b[r].replace("E", "C").replace("W", "C")
    mid = EYE_TOP + 3
    b[mid] = "".join("E" if ch in "EW" else b[mid][x] for x, ch in enumerate(rows[mid]))
    return b


BODY_BLINK = blink(BODY)
HEAD = BODY[:17] + ["." * 16]           # 16 x 18: nine terminal rows
HEAD_BLINK = BODY_BLINK[:17] + ["." * 16]


def image(rows):
    w = len(rows[0])
    assert all(len(r) == w for r in rows), [(i, len(r)) for i, r in enumerate(rows) if len(r) != w]
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
    for name, idle, shut in (("revbot-body-16x26", BODY, BODY_BLINK), ("revbot-head-16x18", HEAD, HEAD_BLINK)):
        for tag, rows in (("idle", idle), ("blink", shut)):
            im = image(rows)
            im.save(os.path.join(OUT, f"{name}-{tag}.png"))
            tiles.append(im)
    for tag, rows in (("idle", BODY), ("blink", BODY_BLINK)):
        im = outlined(image(rows))
        im.save(os.path.join(OUT, f"revbot-body-outlined-18x28-{tag}.png"))
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
