from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


root = Path(__file__).resolve().parent
lines = (root / "sample-letter.txt").read_text(encoding="utf-8").splitlines()
image = Image.new("RGB", (1600, 1050), "white")
draw = ImageDraw.Draw(image)
font = ImageFont.truetype("C:/Windows/Fonts/arial.ttf", 34)
title_font = ImageFont.truetype("C:/Windows/Fonts/arialbd.ttf", 44)
y = 125
for index, line in enumerate(lines):
    draw.text((125, y), line, font=title_font if index == 0 else font, fill="#151515")
    y += 98 if not line else 72
image.save(root / "sample-letter.png")
