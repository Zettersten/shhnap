"""Regenerate the two native 32px pan cursors (requires Pillow)."""

from pathlib import Path
from struct import pack
import sys

from PIL import Image, ImageDraw, ImageFilter


SIZE = 32
SCALE = 4
ASSETS = Path(__file__).resolve().parents[1] / "src" / "Shnapp.App" / "Assets"


def px(value: float) -> int:
    return round(value * SCALE)


def rectangle(draw: ImageDraw.ImageDraw, box: tuple[float, float, float, float], radius: float = 2) -> None:
    draw.rounded_rectangle(tuple(map(px, box)), radius=px(radius), fill=255)


def hand_mask(closed: bool) -> Image.Image:
    mask = Image.new("L", (SIZE * SCALE, SIZE * SCALE))
    draw = ImageDraw.Draw(mask)
    if closed:
        rectangle(draw, (8, 9, 25, 19), 3)
        rectangle(draw, (8, 13, 26, 28), 5)
        draw.polygon([(px(x), px(y)) for x, y in
                      [(8, 16), (5, 17), (4, 20), (9, 25), (16, 25), (18, 21), (13, 18)]], fill=255)
    else:
        rectangle(draw, (8, 7, 12, 20), 2)
        rectangle(draw, (12, 3, 16, 20), 2)
        rectangle(draw, (16, 2, 20, 20), 2)
        rectangle(draw, (20, 5, 24, 21), 2)
        rectangle(draw, (24, 10, 28, 22), 2)
        rectangle(draw, (9, 15, 27, 29), 5)
        draw.polygon([(px(x), px(y)) for x, y in
                      [(10, 18), (6, 14), (3, 14), (3, 18), (9, 26), (14, 28)]], fill=255)
    return mask


def render(closed: bool) -> Image.Image:
    mask = hand_mask(closed)
    border = mask.filter(ImageFilter.MaxFilter(11))
    image = Image.new("RGBA", mask.size)
    image.paste((35, 39, 46, 255), mask=border)
    image.paste((247, 248, 250, 255), mask=mask)
    draw = ImageDraw.Draw(image)
    if closed:
        for x in (11, 15, 19, 23):
            draw.line([(px(x), px(10)), (px(x), px(15))], fill=(155, 161, 169, 255), width=px(0.6))
        draw.arc(tuple(map(px, (9, 16, 22, 25))), 188, 345, fill=(155, 161, 169, 255), width=px(0.7))
    else:
        for x in (12, 16, 20, 24):
            draw.line([(px(x), px(16)), (px(x), px(22))], fill=(181, 186, 193, 255), width=px(0.5))
        draw.arc(tuple(map(px, (11, 15, 26, 29))), 12, 155, fill=(181, 186, 193, 255), width=px(0.6))
    return image.resize((SIZE, SIZE), Image.Resampling.LANCZOS)


def cursor_data(image: Image.Image, hotspot: tuple[int, int]) -> bytes:
    # CUR file: icon directory with hotspots, followed by a 32-bit DIB and AND mask.
    rgba = image.convert("RGBA")
    dib = pack("<IiiHHIIiiII", 40, SIZE, SIZE * 2, 1, 32, 0, SIZE * SIZE * 4, 0, 0, 0, 0)
    pixels = b"".join(
        bytes((b, g, r, a))
        for y in range(SIZE - 1, -1, -1)
        for x in range(SIZE)
        for r, g, b, a in [rgba.getpixel((x, y))]
    )
    mask = b"".join(
        pack("<I", sum((1 << (31 - x)) for x in range(SIZE)
                        if rgba.getpixel((x, y))[3] == 0))
        for y in range(SIZE - 1, -1, -1)
    )
    image_data = dib + pixels + mask
    header = pack("<HHH", 0, 2, 1)
    entry = pack("<BBBBHHII", SIZE, SIZE, 0, 0, *hotspot, len(image_data), 22)
    return header + entry + image_data


for name, closed in (("PanOpen", False), ("PanClosed", True)):
    rendered = render(closed)
    (ASSETS / f"{name}.cur").write_bytes(cursor_data(rendered, (15, 16)))
    if "--preview" in sys.argv:
        rendered.save(ASSETS / f"{name}.preview.png")
