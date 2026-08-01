#!/usr/bin/env python3
"""
Build RenPack's app icon (PNG + ICO) from scratch — reproducible design
via Pillow instead of shipping opaque binary files.

Motif: a stylised "package/archive" — abstract box with lid line and a
vertical tie strap, all in Kroste-Gold on a dark rounded-square base.
No text: must stay readable at 16x16.

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
BG = (22, 28, 35, 255)            # dark, matches Kroste palette
GOLD = (224, 177, 76, 255)         # Kroste gold — the app accent
BLUE = (18, 62, 107, 255)          # Kroste accent blue — subtle border


def build_master() -> Image.Image:
    """Draw the 256x256 master. Higher resolution scales down cleanly."""
    img = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    # Rounded square base
    draw.rounded_rectangle((0, 0, CANVAS - 1, CANVAS - 1),
                           radius=RADIUS, fill=BG,
                           outline=BLUE, width=3)

    # Package outline
    stroke = 10
    inset = 60
    box = (inset, inset + 18, CANVAS - inset, CANVAS - inset)
    draw.rectangle(box, outline=GOLD, width=stroke)

    # Lid line — horizontal, upper third
    lid_y = box[1] + (box[3] - box[1]) // 3
    draw.line((box[0], lid_y, box[2], lid_y), fill=GOLD, width=stroke)

    # Vertical tie strap
    mid_x = CANVAS // 2
    draw.line((mid_x, box[1], mid_x, box[3]), fill=GOLD, width=stroke)

    # Small notch at the top centre — reads as a bow / opening
    notch = 12
    draw.ellipse((mid_x - notch, box[1] - notch // 2 - 2,
                  mid_x + notch, box[1] + notch // 2 + 2),
                 fill=GOLD)

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
