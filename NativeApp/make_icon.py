# -*- coding: utf-8 -*-
"""生成"雷达/网络扫描"主题图标（纯标准库，无第三方依赖）。"""
import math
import struct
import os

OUT_DIR = os.path.dirname(os.path.abspath(__file__))
ICO_PATH = os.path.join(OUT_DIR, "app.ico")
PREVIEW_PATH = os.path.join(OUT_DIR, "icon_preview.png")


def clamp(v, lo=0.0, hi=1.0):
    return lo if v < lo else (hi if v > hi else v)


def mix(c1, c2, t):
    t = clamp(t)
    return tuple(c1[i] * (1 - t) + c2[i] * t for i in range(3))


def shade(x, y, small=False):
    """返回 (r,g,b,a)，归一化坐标 [-1,1]，y 向下为正。
    small=True 时用于 16/24px：线条加粗、元素简化，保证小尺寸清晰。"""
    r = math.hypot(x, y)
    ang = math.atan2(y, x)

    # 圆外透明
    if r > 0.99:
        return (0, 0, 0, 0)

    # 深蓝科技底：更暗更实，保证边界对比
    base = mix((6, 20, 36), (16, 52, 80), clamp((0.99 - r) * 0.7))
    col = list(base)
    alpha = 1.0

    def blend(rgb, a):
        nonlocal col, alpha
        for i in range(3):
            col[i] = col[i] * (1 - a) + rgb[i] * a
        alpha = max(alpha, a)

    # 粗外圈亮边（主体轮廓，小尺寸也清晰）
    edge_w = 0.11 if small else 0.055
    if 0.90 <= r < 0.995:
        t = 1.0 - abs(r - 0.95) / (edge_w / 2)
        if t > 0:
            blend((90, 230, 255), clamp(t) * 1.0)
    # 小尺寸额外加深外圈内侧，强化圆盘轮廓
    if small and 0.80 <= r < 0.90:
        blend((10, 30, 50), 0.6)

    # 同心雷达环：加粗 + 提高亮度
    rings = ((0.70, 0.040, 0.85), (0.44, 0.032, 0.65)) if small else \
            ((0.72, 0.030, 0.90), (0.48, 0.024, 0.72), (0.24, 0.018, 0.55))
    for ring, w, a in rings:
        d = abs(r - ring)
        if d < w:
            blend((72, 226, 245), a * (1 - d / w))
        # 内侧高光，增强立体感
        if d < w * 0.45:
            blend((170, 250, 255), 0.5 * (1 - d / (w * 0.45)))

    # 扫描扇形（指向右上，带渐隐拖尾）
    scan = -0.62
    diff = (ang - scan + math.pi) % (2 * math.pi) - math.pi
    span = 0.95 if small else 0.80
    if -span <= diff <= 0.0 and 0.12 < r < 0.92:
        strength = (1.0 - (-diff) / span) ** 2 * (0.75 if small else 0.60)
        blend((40, 235, 195), strength)
    # 扫描前沿亮线
    lw = 0.045 if small else 0.030
    if abs(diff) < lw and 0.10 < r < 0.92:
        blend((150, 255, 230), 0.95 * (1 - abs(diff) / lw))

    # 雷达节点（小尺寸只保留两个，避免糊成一团）
    node_angles = (-math.pi / 2, math.pi / 2) if small else \
                  (0.0, math.pi / 2, math.pi, -math.pi / 2)
    node_r = 0.72
    nr = 0.105 if small else 0.075
    for na in node_angles:
        nx, ny = node_r * math.cos(na), node_r * math.sin(na)
        d = math.hypot(x - nx, y - ny)
        if d < nr:
            blend((120, 238, 252), 1.0 * (1 - d / nr))
        if d < nr * 0.42:
            blend((240, 255, 255), 1.0)

    # 中心准点（缩小，避免小尺寸整体发白）
    cr = 0.055 if small else 0.070
    if r < cr:
        blend((90, 220, 245), 0.95 * (1 - r / cr))
    if r < cr * 0.45:
        blend((245, 255, 255), 1.0)

    # 边缘抗锯齿
    if r > 0.965:
        alpha *= clamp((0.99 - r) / 0.025)
    return (col[0], col[1], col[2], alpha)


def render(size):
    ss = 6 if size <= 24 else 4
    W = size * ss
    small = size <= 24
    rows = []
    for py in range(size):
        row = []
        for px in range(size):
            acc = [0.0, 0.0, 0.0, 0.0]
            for sy in range(ss):
                for sx in range(ss):
                    x = ((px * ss + sx + 0.5) / W) * 2 - 1
                    y = ((py * ss + sy + 0.5) / W) * 2 - 1
                    c = shade(x, y, small)
                    # alpha 加权累积，避免透明边缘把颜色拉白
                    wgt = c[3]
                    acc[0] += c[0] * wgt
                    acc[1] += c[1] * wgt
                    acc[2] += c[2] * wgt
                    acc[3] += wgt
            if acc[3] > 0:
                row.append((acc[0] / acc[3], acc[1] / acc[3], acc[2] / acc[3],
                            acc[3] / (ss * ss)))
            else:
                row.append((0.0, 0.0, 0.0, 0.0))
        rows.append(row)
    return rows


def to_bgra(rows):
    """返回自下而上的 BGRA 数据 + 1bpp AND mask。"""
    size = len(rows)
    xor = bytearray()
    for y in range(size - 1, -1, -1):
        for r, g, b, a in rows[y]:
            xor += bytes((int(clamp(b, 0, 255)), int(clamp(g, 0, 255)),
                          int(clamp(r, 0, 255)), int(clamp(a, 0, 1) * 255)))
    stride = ((size + 31) // 32) * 4
    mask = bytearray(stride * size)
    return bytes(xor), bytes(mask), len(xor), len(mask)


def dib(rows):
    size = len(rows)
    xor, mask, xlen, mlen = to_bgra(rows)
    hdr = struct.pack(
        "<IiiHHIIiiII",
        40, size, size * 2, 1, 32, 0, xlen, 0, 0, 0, 0
    )
    return hdr + xor + mask


def png_chunk(tag, data):
    c = struct.pack(">I", len(data)) + tag + data
    return c + struct.pack(">I", zlib_crc(tag + data))


def zlib_crc(data):
    import zlib
    return zlib.crc32(data) & 0xFFFFFFFF


def make_png(rows):
    import zlib
    size = len(rows)
    raw = bytearray()
    for row in rows:
        raw.append(0)
        for r, g, b, a in row:
            raw += bytes((int(clamp(r, 0, 255)), int(clamp(g, 0, 255)),
                          int(clamp(b, 0, 255)), int(clamp(a, 0, 1) * 255)))
    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n"
            + png_chunk(b"IHDR", ihdr)
            + png_chunk(b"IDAT", zlib.compress(bytes(raw), 9))
            + png_chunk(b"IEND", b""))


def build_ico(sizes):
    images = []
    for s in sizes:
        rows = render(s)
        images.append((s, dib(rows)))
    out = bytearray(struct.pack("<HHH", 0, 1, len(images)))
    offset = 6 + 16 * len(images)
    for s, data in images:
        out += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32,
                           len(data), offset)
        offset += len(data)
    for s, data in images:
        out += data
    with open(ICO_PATH, "wb") as f:
        f.write(out)
    # 预览图（128px）
    with open(PREVIEW_PATH, "wb") as f:
        f.write(make_png(render(128)))
    print("ICO: %s (%d bytes)" % (ICO_PATH, len(out)))
    print("PNG: %s" % PREVIEW_PATH)


if __name__ == "__main__":
    build_ico([16, 24, 32, 48, 64, 128])
