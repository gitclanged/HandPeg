"""Draws each game's HUD outlines (as written in HudLibrary.cs) into a grey picture at 1280 x 720, for overlaying on its screenshot."""
import math
import re
import sys

src = open(sys.argv[1], encoding='utf-8').read()
out_dir = sys.argv[2]
SCALE = 0.5  # 2560 -> 1280
WIDTH, HEIGHT = 1280, 720


def box(l, t, r, b, radius=6):
    return [(l, t), (r, t), (r, b), (l, b)]


def round_(x, y, radius):
    return [(x + radius * math.cos(2 * math.pi * i / 48), y + radius * math.sin(2 * math.pi * i / 48)) for i in range(48)]


def poly(*xy):
    return [(xy[2 * i], xy[2 * i + 1]) for i in range(len(xy) // 2)]


def fill(shapes):
    pixels = bytearray(WIDTH * HEIGHT)
    for shape in shapes:
        pts = [(x * SCALE, y * SCALE) for x, y in shape]
        ys = [p[1] for p in pts]
        for row in range(max(int(min(ys)), 0), min(int(max(ys)) + 1, HEIGHT)):
            yc = row + 0.5
            xs = []
            for i in range(len(pts)):
                (x1, y1), (x2, y2) = pts[i], pts[(i + 1) % len(pts)]
                if (y1 <= yc < y2) or (y2 <= yc < y1):
                    xs.append(x1 + (yc - y1) * (x2 - x1) / (y2 - y1))
            xs.sort()
            for a, b in zip(xs[::2], xs[1::2]):
                for col in range(max(int(a), 0), min(int(b) + 1, WIDTH)):
                    pixels[row * WIDTH + col] = 255
    return pixels


games = re.findall(r'new\("([^"]+)",\s*\[(.*?)\n        \]\),', src, re.S)
for name, block in games:
    shapes = []
    for kind, args in re.findall(r'\b(Box|Round|Poly)\(([-0-9., ]+)\)', block):
        numbers = [float(v) for v in args.split(',')]
        shapes.append({'Box': box, 'Round': round_, 'Poly': poly}[kind](*numbers))
    data = fill(shapes)
    path = f"{out_dir}/{name.replace(' ', '')}_mask.pgm"
    with open(path, 'wb') as f:
        f.write(f"P5 {WIDTH} {HEIGHT} 255\n".encode())
        f.write(bytes(data))
    print(name, len(shapes), path)
