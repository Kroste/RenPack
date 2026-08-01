#!/usr/bin/env python3
"""
Build the "!" hint icon deployed inside KrosteMod-Walkthroughs.

Design: bauchiges 3D-"!" mit blauem Gradient auf transparentem Hintergrund,
eigenständiger runder Punkt darunter — matched User's Design-Referenz vom
2026-08-01. Kein Text, kein Rand — der Icon steht frei oben rechts im
Spiel als Overlay-Button.

Sizes: 96px master (RGBA, transparent). Ren'Py's Screen skaliert bei Bedarf.

Run:
    python3 scripts/build_hint_icon.py
"""

from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter

REPO = Path(__file__).resolve().parent.parent
OUT = REPO / "RenPack" / "Assets" / "krostemod_hint.png"

SIZE = 96                # Zielgroesse (final)
SUPER = 4                # Supersample-Faktor fuer glatte Kanten
W = SIZE * SUPER

# Blau-Palette (matched User-Design: bauchig, 3D)
BLUE_CORE = (86, 138, 224, 255)      # hell (Highlight-Seite)
BLUE_DEEP = (44, 82, 168, 255)       # dunkel (Schatten-Seite)
BLUE_MID  = (68, 112, 200, 255)
SHADOW    = (18, 34, 78, 150)        # weicher Schlagschatten


def ellipse_gradient(box, color_light, color_dark, direction="tl"):
    """Zeichnet eine Ellipse mit weichem links-oben → rechts-unten-Gradient
    (Illusion von 3D-Wölbung). Ren'Py kann keine Gradients — also machen
    wir das statisch beim Build."""
    x0, y0, x1, y1 = box
    w, h = x1 - x0, y1 - y0
    layer = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    # Basis: dunkler Ton
    d.ellipse((0, 0, w - 1, h - 1), fill=color_dark)
    # Mehrere aufeinander skalierte hellere Ellipsen fuer Verlauf
    for i in range(1, 8):
        f = i / 8
        # Hellere Farbe interpoliert
        rr = int(color_dark[0] + (color_light[0] - color_dark[0]) * f)
        gg = int(color_dark[1] + (color_light[1] - color_dark[1]) * f)
        bb = int(color_dark[2] + (color_light[2] - color_dark[2]) * f)
        # Nach oben-links versetzen (Lichteinfall von links-oben)
        offset_x = int(-w * 0.05 * f)
        offset_y = int(-h * 0.07 * f)
        shrink = int(w * 0.06 * f)
        d.ellipse((shrink + offset_x, shrink + offset_y,
                   w - 1 - shrink + offset_x, h - 1 - shrink + offset_y),
                  fill=(rr, gg, bb, 255))
    return layer


def rounded_bar_gradient(box, color_light, color_dark, radius):
    """Bauchiges Stab-Element (der lange Teil des "!"). Statischer Gradient
    per horizontale Bänder."""
    x0, y0, x1, y1 = box
    w, h = x1 - x0, y1 - y0
    layer = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    d.rounded_rectangle((0, 0, w - 1, h - 1), radius=radius, fill=color_dark)
    # Highlight-Streifen von oben (bauchig-shine)
    for i in range(1, 7):
        f = i / 7
        rr = int(color_dark[0] + (color_light[0] - color_dark[0]) * f)
        gg = int(color_dark[1] + (color_light[1] - color_dark[1]) * f)
        bb = int(color_dark[2] + (color_light[2] - color_dark[2]) * f)
        shrink_x = int(w * 0.10 * f)
        shrink_y = int(h * 0.05 * f)
        # Highlight schmaler + nach oben-links versetzt
        offset_y = -int(h * 0.04 * f)
        d.rounded_rectangle(
            (shrink_x, shrink_y + offset_y,
             w - 1 - shrink_x, h - 1 - shrink_y + offset_y),
            radius=max(1, radius - int(radius * 0.2 * f)),
            fill=(rr, gg, bb, 255),
        )
    return layer


def build_master() -> Image.Image:
    img = Image.new("RGBA", (W, W), (0, 0, 0, 0))

    # --- Schlagschatten (unter beiden Elementen) --------------------------
    shadow = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    sd = ImageDraw.Draw(shadow)
    # Lang-Stab-Schatten
    sd.rounded_rectangle(
        (int(W * 0.42), int(W * 0.13), int(W * 0.66), int(W * 0.60)),
        radius=int(W * 0.11), fill=SHADOW)
    # Punkt-Schatten
    sd.ellipse(
        (int(W * 0.44), int(W * 0.70), int(W * 0.64), int(W * 0.90)),
        fill=SHADOW)
    shadow = shadow.filter(ImageFilter.GaussianBlur(radius=W * 0.02))
    # Etwas nach unten-rechts verschieben
    dx, dy = int(W * 0.015), int(W * 0.02)
    img.alpha_composite(shadow, dest=(dx, dy))

    # --- Lang-Stab (bauchig, mit Gradient) --------------------------------
    bar_box = (int(W * 0.40), int(W * 0.10), int(W * 0.64), int(W * 0.58))
    bar_w = bar_box[2] - bar_box[0]
    bar_h = bar_box[3] - bar_box[1]
    # Der Stab ist oben etwas dicker als unten (bauchig-tropfen-form).
    # Wir bauen zwei ueberlappende Rundrechtecke fuer den Effekt.
    bar_top = rounded_bar_gradient(
        (0, 0, bar_w, int(bar_h * 0.75)),
        BLUE_CORE, BLUE_DEEP,
        radius=int(bar_w * 0.48))
    img.alpha_composite(bar_top, dest=(bar_box[0], bar_box[1]))
    # Unterer schmaler Auslauf
    tip_w = int(bar_w * 0.80)
    tip_h = int(bar_h * 0.40)
    tip_x = bar_box[0] + (bar_w - tip_w) // 2
    tip_y = bar_box[1] + int(bar_h * 0.55)
    bar_tip = rounded_bar_gradient(
        (0, 0, tip_w, tip_h),
        BLUE_MID, BLUE_DEEP,
        radius=int(tip_w * 0.48))
    img.alpha_composite(bar_tip, dest=(tip_x, tip_y))

    # --- Punkt (eigenstaendig, mit 3D-Kugel-Optik) ------------------------
    dot_box = (int(W * 0.42), int(W * 0.68), int(W * 0.62), int(W * 0.88))
    dot_w = dot_box[2] - dot_box[0]
    dot = ellipse_gradient((0, 0, dot_w, dot_w), BLUE_CORE, BLUE_DEEP)
    img.alpha_composite(dot, dest=(dot_box[0], dot_box[1]))

    # --- Feiner Highlight-Glanzpunkt oben auf dem Stab --------------------
    hi = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    hd = ImageDraw.Draw(hi)
    hd.ellipse(
        (int(W * 0.44), int(W * 0.14), int(W * 0.53), int(W * 0.22)),
        fill=(255, 255, 255, 130))
    hi = hi.filter(ImageFilter.GaussianBlur(radius=W * 0.008))
    img.alpha_composite(hi)

    return img


def main():
    master = build_master()
    # Downsample auf Zielgroesse (glatte Kanten dank Supersampling)
    final = master.resize((SIZE, SIZE), Image.LANCZOS)
    OUT.parent.mkdir(parents=True, exist_ok=True)
    final.save(OUT, "PNG", optimize=True)
    print(f"wrote {OUT} ({SIZE}x{SIZE}, {OUT.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
