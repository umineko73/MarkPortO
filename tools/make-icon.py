# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 umineko73
#
# This file is part of MarkPortO.
#
# MarkPortO is free software: you can redistribute it and/or modify it under the terms of
# the GNU General Public License, version 3 only, as published by the Free Software Foundation.
#
# MarkPortO is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
# without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
# See the GNU General Public License for more details.
#
# You should have received a copy of the GNU General Public License along with MarkPortO.
# If not, see <https://www.gnu.org/licenses/gpl-3.0.html>.

"""Makes src/MarkPortO.App/Assets/MarkPortO.ico (used by the app window, markporto.exe and MarkPortO.exe).

Run:  python tools/make-icon.py        (needs Pillow)
A blue rounded square, a white page with a folded corner, a bold "M" and a down arrow (document -> Markdown).
"""
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

SIZE = 1024
BLUE = (37, 99, 235, 255)
DARK_BLUE = (29, 78, 216, 255)
WHITE = (255, 255, 255, 255)
FOLD = (191, 219, 254, 255)
INK = (30, 64, 175, 255)


def draw() -> Image.Image:
    image = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    d = ImageDraw.Draw(image)
    d.rounded_rectangle((32, 32, SIZE - 32, SIZE - 32), radius=200, fill=BLUE)
    # a darker band at the bottom gives a little depth
    band = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    bd = ImageDraw.Draw(band)
    bd.rounded_rectangle((32, 32, SIZE - 32, SIZE - 32), radius=200, fill=DARK_BLUE)
    mask = Image.new("L", (SIZE, SIZE), 0)
    ImageDraw.Draw(mask).rectangle((0, 700, SIZE, SIZE), fill=255)
    image.paste(band, (0, 0), Image.composite(mask, Image.new("L", (SIZE, SIZE), 0), mask))

    # the page: left 210..814, top 140..884, folded corner at the top right
    left, top, right, bottom, fold = 210, 140, 814, 884, 170
    d.polygon([(left, top), (right - fold, top), (right, top + fold), (right, bottom), (left, bottom)], fill=WHITE)
    d.polygon([(right - fold, top), (right, top + fold), (right - fold, top + fold)], fill=FOLD)

    # "M"
    font = ImageFont.truetype("C:/Windows/Fonts/segoeuib.ttf", 380)
    d.text((SIZE / 2, 470), "M", font=font, fill=INK, anchor="mm")

    # down arrow under the M
    cx = SIZE / 2
    d.rectangle((cx - 28, 650, cx + 28, 740), fill=INK)
    d.polygon([(cx - 90, 730), (cx + 90, 730), (cx, 830)], fill=INK)
    return image


def main() -> None:
    target = Path(__file__).resolve().parent.parent / "src" / "MarkPortO.App" / "Assets"
    target.mkdir(parents=True, exist_ok=True)
    image = draw()
    image.save(target / "MarkPortO.ico", format="ICO", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    print("written", target / "MarkPortO.ico")


if __name__ == "__main__":
    main()
