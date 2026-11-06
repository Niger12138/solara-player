"""
生成 SOLARA 应用图标（多尺寸 ICO）。
风格：深色圆角方块 + 紫→青渐变 + 白色声波柱 + 音符点缀。
"""
import os
from PIL import Image, ImageDraw, ImageFilter

S = 1024
OUT_DIR = os.path.dirname(os.path.abspath(__file__))
ICO = os.path.join(OUT_DIR, "app.ico")
PNG = os.path.join(OUT_DIR, "app.png")


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


def rounded_mask(size, radius):
    m = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(m)
    d.rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    return m


def make():
    # 渐变底
    grad = Image.new("RGB", (S, S))
    px = grad.load()
    c1 = (124, 92, 255)    # violet
    c2 = (34, 211, 238)    # cyan
    c3 = (255, 95, 162)    # pink
    for y in range(S):
        for x in range(0, S, 2):
            t = (x / S) * 0.55 + (y / S) * 0.45
            if t < 0.55:
                col = lerp(c1, c2, t / 0.55)
            else:
                col = lerp(c2, c3, (t - 0.55) / 0.45 * 0.55)
            px[x, y] = col
            if x + 1 < S:
                px[x + 1, y] = col

    # 左上高光
    hi = Image.new("L", (S, S), 0)
    dh = ImageDraw.Draw(hi)
    dh.ellipse([-S * 0.35, -S * 0.5, S * 0.85, S * 0.6], fill=90)
    hi = hi.filter(ImageFilter.GaussianBlur(S * 0.12))
    white = Image.new("RGB", (S, S), (255, 255, 255))
    grad = Image.composite(white, grad, hi.point(lambda v: int(v * 0.28)))

    # 圆角
    radius = int(S * 0.235)
    mask = rounded_mask(S, radius)
    icon = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    icon.paste(grad, (0, 0), mask)

    # 内描边
    d = ImageDraw.Draw(icon)
    d.rounded_rectangle([2, 2, S - 3, S - 3], radius=radius, outline=(255, 255, 255, 46), width=6)

    # 声波柱（白色圆角竖条，中间高两边低）
    bars = [0.34, 0.58, 0.86, 1.0, 0.72, 0.46]
    bw = S * 0.086
    gap = S * 0.052
    total = len(bars) * bw + (len(bars) - 1) * gap
    x0 = (S - total) / 2
    cy = S * 0.525
    maxh = S * 0.46
    layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    dl = ImageDraw.Draw(layer)
    for i, b in enumerate(bars):
        h = maxh * b
        x = x0 + i * (bw + gap)
        y = cy - h / 2
        r = bw / 2
        dl.rounded_rectangle([x, y, x + bw, y + h], radius=r, fill=(255, 255, 255, 255))
    # 柔光
    glow = layer.filter(ImageFilter.GaussianBlur(S * 0.035))
    icon = Image.alpha_composite(icon, glow)
    icon = Image.alpha_composite(icon, layer)

    icon.save(PNG)

    sizes = [(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (24, 24), (16, 16)]
    icon.save(ICO, format="ICO", sizes=sizes)
    print("icon written:", ICO, os.path.getsize(ICO), "bytes")


if __name__ == "__main__":
    make()
