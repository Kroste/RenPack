#!/usr/bin/env python3
"""
Build the "!" hint icon deployed inside KrosteMod walkthroughs.

Design (v0.10.2, geaendert vom bauchigen "!" in v0.9.4):
Anonymous-V-Maske in KROSTE-GOLD statt grau-blau — so bleibt das
Icon in visueller Familie mit App-Icon und Cheat-Overlay (siehe
build_icon.py, build_cheat_icon.py), unterscheidet sich aber durch
die Farbe klar vom grauen Cheat-Icon. Zusaetzlich ein kleines "!"-
Badge oben rechts an der Maske als „Info-Kontext"-Marker.

Semantik: Info-Screen (F10) ist der KONTEXTUELLE Overlay — nur bei
Choice-Menus sichtbar (siehe krostemod_menu_hint_visible()), zeigt
die Variablen die die aktuellen Choices setzen + Consumer-Liste.

Sizes: 96px master (RGBA, transparent). Ren'Py's Screen skaliert.

Run:
    python3 scripts/build_hint_icon.py
"""

from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter

REPO = Path(__file__).resolve().parent.parent
OUT = REPO / "RenPack" / "Assets" / "krostemod_hint.png"

SIZE = 96
SUPER = 4
W = SIZE * SUPER

# Gold-Palette (matched Kroste-App-Accent)
MASK_FILL = (224, 177, 76, 255)      # Kroste-Gold
MASK_HL   = (255, 208, 108, 255)     # heller Rand fuer 3D-Hint
OUTLINE   = (60, 40, 8, 255)         # dunkel-braun-schwarz Kontur
EYE_DARK  = (60, 40, 8, 255)
CHIN_DARK = (168, 132, 52, 255)      # tiefer-Gold-Ton fuers Kinn
BADGE_BG  = (200, 45, 45, 255)       # rotes Badge fuer "!"
BADGE_TX  = (255, 255, 255, 255)     # weisses "!"
SHADOW    = (30, 20, 6, 140)         # weicher Schlagschatten


def draw_mask_shape(img):
    d = ImageDraw.Draw(img)

    top_left    = (int(W * 0.20), int(W * 0.14))
    top_bump    = (int(W * 0.50), int(W * 0.05))
    top_right   = (int(W * 0.80), int(W * 0.14))
    right_curve = (int(W * 0.72), int(W * 0.55))
    bottom      = (int(W * 0.50), int(W * 0.92))
    left_curve  = (int(W * 0.28), int(W * 0.55))

    poly = [top_left, top_bump, top_right, right_curve, bottom, left_curve]
    d.polygon(poly, fill=MASK_FILL)
    d.line(poly + [top_left], fill=OUTLINE, width=int(W * 0.02))

    # Highlight-Streifen an der linken Seite fuer 3D-Impression
    hl_poly = [
        (int(W * 0.22), int(W * 0.18)),
        (int(W * 0.48), int(W * 0.08)),
        (int(W * 0.43), int(W * 0.16)),
        (int(W * 0.32), int(W * 0.50)),
    ]
    d.polygon(hl_poly, fill=MASK_HL)

    # Geschlossene Augen
    eye_y = int(W * 0.30)
    for cx in (int(W * 0.35), int(W * 0.65)):
        bbox = (cx - int(W * 0.07), eye_y - int(W * 0.02),
                cx + int(W * 0.07), eye_y + int(W * 0.06))
        d.arc(bbox, start=180, end=360, fill=EYE_DARK, width=int(W * 0.02))

    # Kinn-Innendreieck
    inner_chin = [
        (int(W * 0.42), int(W * 0.55)),
        (int(W * 0.58), int(W * 0.55)),
        (int(W * 0.50), int(W * 0.75)),
    ]
    d.polygon(inner_chin, fill=CHIN_DARK)
    d.line(inner_chin + [inner_chin[0]], fill=OUTLINE, width=int(W * 0.015))


def draw_badge(img):
    """Kleines rotes „!"-Badge oben-rechts an der Maske als Info-Marker."""
    d = ImageDraw.Draw(img)
    cx, cy = int(W * 0.78), int(W * 0.18)
    r = int(W * 0.13)
    d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=BADGE_BG,
              outline=OUTLINE, width=int(W * 0.012))
    # Ein einfaches "!" — vertikaler Strich + Punkt
    stroke = int(W * 0.03)
    d.line((cx, cy - int(r * 0.55), cx, cy + int(r * 0.10)),
           fill=BADGE_TX, width=stroke)
    d.ellipse((cx - stroke // 2, cy + int(r * 0.30),
               cx + stroke // 2, cy + int(r * 0.55)),
              fill=BADGE_TX)


def build_master() -> Image.Image:
    img = Image.new("RGBA", (W, W), (0, 0, 0, 0))

    # Schlagschatten unter der Maske
    shadow = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    sd = ImageDraw.Draw(shadow)
    sd.polygon([
        (int(W * 0.22), int(W * 0.16)),
        (int(W * 0.50), int(W * 0.07)),
        (int(W * 0.78), int(W * 0.16)),
        (int(W * 0.70), int(W * 0.56)),
        (int(W * 0.50), int(W * 0.93)),
        (int(W * 0.30), int(W * 0.56)),
    ], fill=SHADOW)
    shadow = shadow.filter(ImageFilter.GaussianBlur(radius=W * 0.02))
    img.alpha_composite(shadow, dest=(int(W * 0.02), int(W * 0.025)))

    draw_mask_shape(img)
    draw_badge(img)
    return img


def main():
    master = build_master()
    final = master.resize((SIZE, SIZE), Image.LANCZOS)
    OUT.parent.mkdir(parents=True, exist_ok=True)
    final.save(OUT, "PNG", optimize=True)
    print(f"wrote {OUT} ({SIZE}x{SIZE}, {OUT.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
