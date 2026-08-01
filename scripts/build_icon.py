#!/usr/bin/env python3
"""
Build RenPack's app icon (PNG + ICO) from scratch — reproducible design
via Pillow instead of shipping opaque binary files.

Motif (v0.10.2, updated from the "package" design in v0.7.0):
Anonymous-/V-for-Vendetta-style mask silhouette on a dark rounded-square
base. Passt zum "RenPack modded Ren'Py"-Vibe und ist in Familie mit den
Cheat- und Info-Overlay-Icons (siehe build_cheat_icon.py, build_hint_icon.py).

Design rules (from kroste-avalonia/references/design.md):
- 256x256 canvas, rounded corners (radius ~48).
- App accent colour on dark background.
- Multi-res ICO (16/24/32/48/64/128/256) for the Windows exe.

Run:
    python3 scripts/build_icon.py
"""

from pathlib import Path
from PIL import Image, ImageDraw

REPO = Path(__file__).resolve().parent.parent
OUT_DIR = REPO / "RenPack" / "Assets"

CANVAS = 256
RADIUS = 48
BG        = (22, 28, 35, 255)        # dark backdrop
BLUE      = (18, 62, 107, 255)       # base outline
MASK_FILL = (196, 205, 214, 255)     # helles Grau (Maskenkoerper)
OUTLINE   = (10, 12, 18, 255)        # schwarze Kontur
EYE_DARK  = (10, 12, 18, 255)        # geschlossene Augen
CHIN_DARK = (108, 118, 132, 255)     # innere Kinn-Schattierung
GOLD_HI   = (224, 177, 76, 255)      # Kroste-Gold-Akzent (Kinn-Highlight)


def draw_mask(draw: ImageDraw.ImageDraw, W: int, xoff: int = 0, yoff: int = 0):
    """Zeichnet die Anonymous-V-Maske skaliert auf W (im Canvas W x W).
    Wird auch von build_hint_icon.py und build_cheat_icon.py verwendet."""
    top_left    = (int(W * 0.20) + xoff, int(W * 0.14) + yoff)
    top_bump    = (int(W * 0.50) + xoff, int(W * 0.05) + yoff)
    top_right   = (int(W * 0.80) + xoff, int(W * 0.14) + yoff)
    right_curve = (int(W * 0.72) + xoff, int(W * 0.55) + yoff)
    bottom      = (int(W * 0.50) + xoff, int(W * 0.92) + yoff)
    left_curve  = (int(W * 0.28) + xoff, int(W * 0.55) + yoff)

    poly = [top_left, top_bump, top_right, right_curve, bottom, left_curve]
    draw.polygon(poly, fill=MASK_FILL)
    draw.line(poly + [top_left], fill=OUTLINE, width=max(2, int(W * 0.02)))

    # Geschlossene Augen — Halbkreise, nach unten offen
    eye_y = int(W * 0.30) + yoff
    for cx in (int(W * 0.35) + xoff, int(W * 0.65) + xoff):
        bbox = (cx - int(W * 0.07), eye_y - int(W * 0.02),
                cx + int(W * 0.07), eye_y + int(W * 0.06))
        draw.arc(bbox, start=180, end=360, fill=EYE_DARK,
                 width=max(2, int(W * 0.02)))

    # Kinn-Innendreieck
    inner_chin = [
        (int(W * 0.42) + xoff, int(W * 0.55) + yoff),
        (int(W * 0.58) + xoff, int(W * 0.55) + yoff),
        (int(W * 0.50) + xoff, int(W * 0.75) + yoff),
    ]
    draw.polygon(inner_chin, fill=CHIN_DARK)
    draw.line(inner_chin + [inner_chin[0]], fill=OUTLINE,
              width=max(2, int(W * 0.015)))


def build_master() -> Image.Image:
    """Draw the 256x256 master. Higher resolution scales down cleanly."""
    img = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    # Rounded square base
    draw.rounded_rectangle((0, 0, CANVAS - 1, CANVAS - 1),
                           radius=RADIUS, fill=BG,
                           outline=BLUE, width=3)

    # Maske zentriert in der Rounded-Base — leichter Rand oben/unten,
    # damit sie nicht am Bezel klebt
    draw_mask(draw, CANVAS)

    # Kleiner Kroste-Gold-Akzent unter dem Kinn (Marker "Kroste-App")
    draw.line((CANVAS // 2 - 22, CANVAS - 24,
               CANVAS // 2 + 22, CANVAS - 24),
              fill=GOLD_HI, width=5)

    return img


def main() -> None:
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    master = build_master()

    png_path = OUT_DIR / "RenPack.png"
    master.save(png_path, format="PNG")
    print(f"wrote {png_path.relative_to(REPO)}")

    ico_path = OUT_DIR / "RenPack.ico"
    master.save(ico_path, format="ICO",
                sizes=[(16, 16), (24, 24), (32, 32), (48, 48),
                       (64, 64), (128, 128), (256, 256)])
    print(f"wrote {ico_path.relative_to(REPO)}")


if __name__ == "__main__":
    main()
