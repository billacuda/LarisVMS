"""Regenerates the PWA/favicon icons in src/LarisVMS.Web/wwwroot from logos/larisvms1.png.

Crops to the warrior/shield mark (the wordmark is unreadable at icon size). Requires Pillow.
Usage: python tools/generate-pwa-icons.py
"""
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "logos" / "larisvms1.png"
WWW = ROOT / "src" / "LarisVMS.Web" / "wwwroot"
ICONS = WWW / "icons"
CROP = (295, 205, 735, 645)  # square around the mark
BG = (253, 253, 253, 255)    # matches the logo's own near-white ground, so no visible seam

ICONS.mkdir(parents=True, exist_ok=True)
mark = Image.open(SRC).convert("RGBA").crop(CROP)

def flat(size, inset=0.0):
    canvas = Image.new("RGBA", (size, size), BG)
    inner = round(size * (1 - 2 * inset))
    m = mark.resize((inner, inner), Image.LANCZOS)
    off = (size - inner) // 2
    canvas.alpha_composite(m, (off, off))
    return canvas.convert("RGB")

flat(192, inset=0.05).save(ICONS / "icon-192.png", optimize=True)
flat(512, inset=0.05).save(ICONS / "icon-512.png", optimize=True)
# Maskable: keep the mark inside the central safe zone so Android's adaptive mask can't clip it.
flat(512, inset=0.14).save(ICONS / "icon-maskable-512.png", optimize=True)
flat(180, inset=0.05).save(ICONS / "apple-touch-icon.png", optimize=True)
flat(32).save(ICONS / "favicon-32.png", optimize=True)
flat(256).save(WWW / "favicon.ico", sizes=[(16, 16), (32, 32), (48, 48)])
print("icons written to", ICONS)
