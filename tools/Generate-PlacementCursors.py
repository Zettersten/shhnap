"""Regenerate the native 32px editor placement cursors (requires Pillow)."""

from pathlib import Path
from struct import pack
import sys

from PIL import Image, ImageDraw


SIZE = 32
SCALE = 4
ASSETS = Path(__file__).resolve().parents[1] / "src" / "Shnapp.App" / "Assets"
HOTSPOT = (5, 5)
INK = (31, 36, 43, 255)
PAPER = (255, 255, 255, 255)


def scaled(points):
    return [(round(x * SCALE), round(y * SCALE)) for x, y in points]


def line(draw, points, color, width):
    draw.line(scaled(points), fill=color, width=round(width * SCALE), joint="curve")


def rectangle(draw, bounds, color, width=0):
    draw.rectangle(tuple(round(value * SCALE) for value in bounds),
                   fill=color if width == 0 else None,
                   outline=color if width else None,
                   width=round(width * SCALE) if width else 1)


def ellipse(draw, bounds, color, width=0):
    draw.ellipse(tuple(round(value * SCALE) for value in bounds),
                 fill=color if width == 0 else None,
                 outline=color if width else None,
                 width=round(width * SCALE) if width else 1)


def cursor_base():
    image = Image.new("RGBA", (SIZE * SCALE, SIZE * SCALE))
    draw = ImageDraw.Draw(image)
    # The common precise hotspot makes every cursor feel like a placement tool.
    for points in (((5, 0), (5, 3)), ((5, 7), (5, 10)),
                   ((0, 5), (3, 5)), ((7, 5), (10, 5))):
        line(draw, points, INK, 2.5)
        line(draw, points, PAPER, 1.25)
    ellipse(draw, (4.2, 4.2, 5.8, 5.8), PAPER)
    return image, draw


def shape_cursor():
    image, draw = cursor_base()
    rectangle(draw, (12, 12, 28, 26), INK, 4)
    rectangle(draw, (12, 12, 28, 26), PAPER, 2)
    return image


def line_cursor():
    image, draw = cursor_base()
    points = ((12, 26), (27, 11))
    line(draw, points, INK, 5)
    line(draw, points, PAPER, 2.75)
    for x, y in points:
        ellipse(draw, (x - 2, y - 2, x + 2, y + 2), INK)
        ellipse(draw, (x - 1, y - 1, x + 1, y + 1), PAPER)
    return image


def step_cursor():
    image, draw = cursor_base()
    ellipse(draw, (11, 10, 28, 27), INK)
    ellipse(draw, (13, 12, 26, 25), PAPER)
    line(draw, ((19.5, 16), (19.5, 22)), INK, 2.5)
    line(draw, ((16.5, 17.5), (19.5, 15.5)), INK, 2.5)
    return image


def privacy_cursor():
    image, draw = cursor_base()
    rectangle(draw, (11, 13, 28, 25), INK)
    for x in (13, 18, 23):
        line(draw, ((x, 23), (x + 4, 15)), PAPER, 1.5)
    return image


def cursor_data(image):
    rgba = image.resize((SIZE, SIZE), Image.Resampling.LANCZOS)
    dib = pack("<IiiHHIIiiII", 40, SIZE, SIZE * 2, 1, 32, 0, SIZE * SIZE * 4, 0, 0, 0, 0)
    pixels = b"".join(
        bytes((b, g, r, a))
        for y in range(SIZE - 1, -1, -1)
        for x in range(SIZE)
        for r, g, b, a in [rgba.getpixel((x, y))]
    )
    mask = b"".join(
        pack("<I", sum(1 << (31 - x) for x in range(SIZE)
                       if rgba.getpixel((x, y))[3] == 0))
        for y in range(SIZE - 1, -1, -1)
    )
    image_data = dib + pixels + mask
    header = pack("<HHH", 0, 2, 1)
    entry = pack("<BBBBHHII", SIZE, SIZE, 0, 0, *HOTSPOT, len(image_data), 22)
    return header + entry + image_data, rgba


for name, render in (("PlaceShape", shape_cursor), ("PlaceLine", line_cursor),
                     ("PlaceStep", step_cursor), ("PlacePrivacy", privacy_cursor)):
    data, preview = cursor_data(render())
    (ASSETS / f"{name}.cur").write_bytes(data)
    if "--preview" in sys.argv:
        preview.save(ASSETS / f"{name}.preview.png")
