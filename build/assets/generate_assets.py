"""Generates NEXORA icon/logo assets (PNG + multi-resolution ICO) from the vector logo geometry.

The geometry matches Controls/NexoraLogo.xaml (64x64 design grid). Requires Pillow:
    python -m pip install pillow
    python build/assets/generate_assets.py
"""
import os
import sys
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.normpath(os.path.join(HERE, "..", "..", "src", "Nexora.App", "Assets"))
SS = 4  # supersampling factor

C1 = (0x1F, 0xB6, 0xFF)
C2 = (0x6A, 0x5C, 0xFF)

STEM_LEFT = [(17, 47), (17, 17), (25, 17), (25, 47)]
DIAGONAL = [(17, 17), (25, 17), (47, 47), (39, 47)]
STEM_RIGHT = [(39, 17), (47, 17), (47, 40), (39, 29)]


def logo(size: int) -> Image.Image:
    s = size * SS
    scale = s / 64.0
    # Diagonal gradient tile
    grad = Image.new("RGBA", (s, s))
    px = grad.load()
    for y in range(s):
        for x in range(s):
            t = (x + y) / (2.0 * (s - 1))
            px[x, y] = tuple(int(C1[i] + (C2[i] - C1[i]) * t) for i in range(3)) + (255,)
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, s - 1, s - 1], radius=int(16 * scale), fill=255)
    tile = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    tile.paste(grad, (0, 0), mask)

    # Soft top highlight
    hl = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    hp = hl.load()
    for y in range(s // 2):
        a = int(0x66 * 0.35 * (1 - y / (s / 2)))
        for x in range(s):
            hp[x, y] = (255, 255, 255, a)
    tile = Image.alpha_composite(tile, Image.composite(hl, Image.new("RGBA", (s, s), (0, 0, 0, 0)), mask))

    pts = lambda poly: [(x * scale, y * scale) for x, y in poly]
    draw = ImageDraw.Draw(tile)
    draw.polygon(pts(STEM_LEFT), fill=(255, 255, 255, 255))
    draw.polygon(pts(DIAGONAL), fill=(255, 255, 255, 255))
    # The accent stem is blended (82% white over the gradient), exactly like the XAML Opacity.
    overlay = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    ImageDraw.Draw(overlay).polygon(pts(STEM_RIGHT), fill=(255, 255, 255, int(255 * 0.82)))
    tile = Image.alpha_composite(tile, overlay)
    return tile.resize((size, size), Image.LANCZOS)


def on_canvas(width: int, height: int, logo_size: int, background=None) -> Image.Image:
    canvas = Image.new("RGBA", (width, height), background or (0, 0, 0, 0))
    mark = logo(logo_size)
    canvas.alpha_composite(mark, ((width - logo_size) // 2, (height - logo_size) // 2))
    return canvas


def save(img: Image.Image, name: str) -> None:
    img.save(os.path.join(OUT, name))


def main() -> int:
    os.makedirs(OUT, exist_ok=True)
    for scale, factor in (("100", 1), ("200", 2)):
        save(on_canvas(44 * factor, 44 * factor, int(44 * factor * 0.84)), f"Square44x44Logo.scale-{scale}.png")
        save(on_canvas(150 * factor, 150 * factor, int(150 * factor * 0.5)), f"Square150x150Logo.scale-{scale}.png")
        save(on_canvas(310 * factor, 150 * factor, int(150 * factor * 0.5)), f"Wide310x150Logo.scale-{scale}.png")
        save(on_canvas(50 * factor, 50 * factor, int(50 * factor * 0.9)), f"StoreLogo.scale-{scale}.png")
        save(on_canvas(620 * factor, 300 * factor, int(300 * factor * 0.42)), f"SplashScreen.scale-{scale}.png")
    for target in (16, 24, 32, 48, 256):
        save(logo(target), f"Square44x44Logo.targetsize-{target}_altform-unplated.png")
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    logo(256).save(os.path.join(OUT, "Nexora.ico"), format="ICO", sizes=[(n, n) for n in sizes])
    save(logo(512), "NexoraLogo-512.png")
    print("Assets written to", OUT)
    return 0


if __name__ == "__main__":
    sys.exit(main())
