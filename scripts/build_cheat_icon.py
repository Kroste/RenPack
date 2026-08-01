#!/usr/bin/env python3
"""
Build the Cheat-Menu icon deployed by KrosteMod (F11-Cheat-Screen).

Design: stilisierte Anonymous-/V-for-Vendetta-Maske (V-Silhouette
mit geschlossenen Slit-Augen und Kinnspitze) auf transparentem
Hintergrund. Passt zum „manipulation/cheat"-Vibe des Screens.
User-Referenz vom 2026-08-01: grau-blaue Anonymous-Maske.

Vereinfacht ggue. der Vorlage (die Wifi-Broadcast-Wellen und die drei
Personen darunter fallen weg — waeren bei 96px unlesbar). Kern-
merkmale bleiben: V-Kopf, geschlossene Augen, Kinn.

Run:
    python3 scripts/build_cheat_icon.py
"""

from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter

REPO = Path(__file__).resolve().parent.parent
OUT = REPO / "RenPack" / "Assets" / "krostemod_cheat.png"

SIZE = 96
SUPER = 4
W = SIZE * SUPER

# Farbpalette matched User's Referenz
MASK_FILL = (196, 205, 214, 255)     # helles Grau (Maskenkoerper)
MASK_DARK = (120, 128, 138, 255)     # dunkler Rand-Schatten
OUTLINE   = (18, 22, 30, 255)        # schwarze Kontur
EYE_DARK  = (18, 22, 30, 255)        # geschlossene Augen als schwarze Boegen
CHIN_DARK = (108, 118, 132, 255)     # innere Kinn-Schattierung
SHADOW    = (18, 22, 30, 140)        # weicher Schlagschatten


def draw_mask(img):
    """Zeichnet die V-Maske: breiter oben, spitz unten. Anonymous-Vibe."""
    d = ImageDraw.Draw(img)

    # V-Kontur — Polygon-Punkte (im hi-res Space W)
    top_left  = (int(W * 0.20), int(W * 0.14))
    top_bump  = (int(W * 0.50), int(W * 0.05))   # kleine Wölbung oben Mitte
    top_right = (int(W * 0.80), int(W * 0.14))
    right_curve = (int(W * 0.72), int(W * 0.55))
    bottom    = (int(W * 0.50), int(W * 0.92))   # spitzes Kinn
    left_curve  = (int(W * 0.28), int(W * 0.55))

    mask_poly = [top_left, top_bump, top_right,
                 right_curve, bottom, left_curve]

    # Fuellung
    d.polygon(mask_poly, fill=MASK_FILL)
    # Kontur (dick — 3x Supersample-Faktor)
    d.line(mask_poly + [top_left], fill=OUTLINE, width=int(W * 0.02))

    # Geschlossene Augen — zwei nach unten offene Boegen (like „closed eyes")
    eye_y = int(W * 0.30)
    for cx in (int(W * 0.35), int(W * 0.65)):
        # Halbkreis: bbox nach unten hin definiert den Bogen
        bbox = (cx - int(W * 0.07), eye_y - int(W * 0.02),
                cx + int(W * 0.07), eye_y + int(W * 0.06))
        d.arc(bbox, start=180, end=360, fill=EYE_DARK, width=int(W * 0.02))

    # Kinn-Innenschatten — kleines Dreieck (dunkler Fill) direkt ueber der Spitze
    inner_chin = [
        (int(W * 0.42), int(W * 0.55)),
        (int(W * 0.58), int(W * 0.55)),
        (int(W * 0.50), int(W * 0.75)),
    ]
    d.polygon(inner_chin, fill=CHIN_DARK)
    d.line(inner_chin + [inner_chin[0]], fill=OUTLINE, width=int(W * 0.015))


def build_master() -> Image.Image:
    img = Image.new("RGBA", (W, W), (0, 0, 0, 0))

    # Schlagschatten
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

    # Maske
    draw_mask(img)
    return img


def main():
    master = build_master()
    final = master.resize((SIZE, SIZE), Image.LANCZOS)
    OUT.parent.mkdir(parents=True, exist_ok=True)
    final.save(OUT, "PNG", optimize=True)
    print(f"wrote {OUT} ({SIZE}x{SIZE}, {OUT.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
