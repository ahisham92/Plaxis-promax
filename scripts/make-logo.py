"""Draws the Plaxis ProMax mark: soil layers in a rounded square, with a wall and anchor, in the Sketch ProMax blues."""
import os
from PIL import Image, ImageDraw

OUT = os.path.join(os.path.dirname(__file__), "..", "assets", "logo")
NAVY, BLUE, SKY = (11, 30, 79), (29, 78, 216), (96, 165, 250)
SAND, CLAY = (217, 180, 120), (166, 120, 76)


def mark(size):
    s = size * 4  # drawn large, scaled down smooth
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    r = s // 5
    d.rounded_rectangle([0, 0, s - 1, s - 1], radius=r, fill=NAVY)
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, s - 1, s - 1], radius=r, fill=255)
    layers = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    l = ImageDraw.Draw(layers)
    l.rectangle([0, int(s * 0.42), s, int(s * 0.62)], fill=SAND)
    l.rectangle([0, int(s * 0.62), s, int(s * 0.80)], fill=CLAY)
    l.rectangle([0, int(s * 0.80), s, s], fill=(120, 86, 56))
    # excavation on the right of the wall
    l.rectangle([int(s * 0.56), int(s * 0.42), s, int(s * 0.62)], fill=NAVY)
    img.paste(layers, (0, 0), Image.composite(layers, Image.new("RGBA", (s, s)), mask).split()[3])
    # the wall and its anchor
    d.rectangle([int(s * 0.50), int(s * 0.22), int(s * 0.56), int(s * 0.86)], fill=SKY)
    d.line([int(s * 0.50), int(s * 0.34), int(s * 0.18), int(s * 0.54)], fill=SKY, width=max(2, s // 28))
    d.ellipse([int(s * 0.12), int(s * 0.50), int(s * 0.24), int(s * 0.60)], fill=BLUE)
    return img.resize((size, size), Image.LANCZOS)


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    for size in (16, 24, 32, 48, 64, 128, 256):
        mark(size).save(os.path.join(OUT, "plaxis-promax-{}.png".format(size)))
    mark(256).save(os.path.join(OUT, "plaxis-promax.ico"), sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
