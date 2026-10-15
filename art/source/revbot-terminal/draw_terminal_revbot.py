"""
Hand-placed pixel-art drafts of RevBot at terminal size, as starting points for
Dave to redraw in Aseprite. One character per pixel; '.' is transparent.
Writes 1x PNGs (open them in Aseprite) and an upscaled preview on a dark and a
light terminal background.
"""
import os
import sys

from PIL import Image

OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), "terminal-drafts")

PAL = {
    "K": (20, 20, 22),     # outline (light terminals)
    "L": (124, 124, 128),  # head highlight
    "C": (88, 88, 92),     # charcoal
    "c": (62, 62, 66),     # charcoal shade, and the dark hazard band (black would vanish on a dark terminal)
    "E": (10, 10, 12),     # eye
    "W": (250, 250, 250),  # glint
    "Y": (245, 196, 48),   # hazard yellow, antenna ball, hands
    "y": (196, 148, 30),   # yellow shade
    "O": (240, 110, 30),   # orange arms and neck spring
    "o": (176, 72, 18),    # orange shade
}

# Big round eyes with a glint, ears flush on the head, no outline: 18 x 22 (11 terminal rows).
BODY = [
    "........YY........",
    "........Yy........",
    "........L.........",
    "....LLLLLLLLLL....",
    "...LCCCCCCCCCCc...",
    ".YLCCEECCCCEECCcY.",
    "YcLCEEWECCEEWECccY",
    "YYCCEEEECCEEEECcYY",
    "YcCCCEECCCCEECCccY",
    ".YCCCCCCCCCCCCCcY.",
    "..cCCCCCCCCCCCcc..",
    "...cccccccccccc...",
    "........OO........",
    "........oo........",
    ".....CCCCCCCC.....",
    "....OCYcYcYcCO....",
    "....OCcYcYcYCO....",
    "...YOCCCCCCCCOY...",
    "...Y.cccccccc.Y...",
    "......CC..CC......",
    "......YY..YY......",
    ".....cCC..CCc.....",
]


def blink(rows, top):
    """Eyes shut: the eye rows become one dark line across where the eyes were."""
    b = list(rows)
    for r in range(top, top + 4):
        b[r] = b[r].replace("E", "C").replace("W", "C")
    line = list(b[top + 2])
    for x, ch in enumerate(rows[top + 2]):
        if ch == "E":
            line[x] = "E"
    b[top + 2] = "".join(line)
    return b


BODY_BLINK = blink(BODY, 5)
HEAD = BODY[:12]          # 18 x 12: six terminal rows, for the spinner
HEAD_BLINK = BODY_BLINK[:12]


def image(rows):
    w = len(rows[0])
    assert all(len(r) == w for r in rows), [len(r) for r in rows]
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
    for name, idle, shut in (("revbot-body-18x22", BODY, BODY_BLINK), ("revbot-head-18x12", HEAD, HEAD_BLINK)):
        for tag, rows in (("idle", idle), ("blink", shut)):
            im = image(rows)
            im.save(os.path.join(OUT, f"{name}-{tag}.png"))
            tiles.append(im)
    for tag, rows in (("idle", BODY), ("blink", BODY_BLINK)):
        im = outlined(image(rows))
        im.save(os.path.join(OUT, f"revbot-body-outlined-20x24-{tag}.png"))
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
