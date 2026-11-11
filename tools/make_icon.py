#!/usr/bin/env python3
"""Draws AVAPageStream's icon (a page with two columns of text and a picture, on a dark tile) and writes
src/AVAPageStream/Assets/AVAPageStream.png and .ico. Needs only the standard library."""
import struct
import zlib
from pathlib import Path

SIZE = 256
SS = 4  # supersampling for smooth edges


def inside_round_rect(x, y, x0, y0, x1, y1, r):
    if x < x0 or x > x1 or y < y0 or y > y1:
        return False
    cx = min(max(x, x0 + r), x1 - r)
    cy = min(max(y, y0 + r), y1 - r)
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r


def colour_at(x, y):
    """The colour (r, g, b, a) at a point in 0..256 coordinates."""
    # The tile.
    if not inside_round_rect(x, y, 8, 8, 248, 248, 44):
        return (0, 0, 0, 0)
    colour = (32, 36, 46, 255)
    # The page, tilted a touch by a folded corner.
    px0, py0, px1, py1 = 56, 34, 200, 222
    fold = 34
    on_page = px0 <= x <= px1 and py0 <= y <= py1 and not (x > px1 - fold and y < py0 + fold and (x - (px1 - fold)) > (y - py0))
    if on_page:
        colour = (250, 250, 248, 255)
        # The folded corner.
        if x > px1 - fold and y < py0 + fold and (x - (px1 - fold)) <= (y - py0):
            colour = (205, 210, 220, 255)
        # A headline bar.
        if 72 <= x <= 160 and 56 <= y <= 70:
            colour = (255, 106, 61, 255)
        # Two columns of text lines.
        for col_x0, col_x1, start in ((72, 122, 86), (134, 184, 86)):
            for i in range(9):
                ly = start + i * 14
                last = i in (4, 8)
                if col_x0 <= x <= (col_x1 - (18 if last else 0)) and ly <= y <= ly + 6:
                    colour = (70, 78, 96, 255)
        # A picture in the right column's foot.
        if 134 <= x <= 184 and 156 <= y <= 204:
            colour = (0, 168, 232, 255)
            if y > 186 - (x - 134) * 0.5 and y > 176 + (184 - x) * 0.3:
                colour = (20, 110, 170, 255)
    return colour


def render():
    rows = []
    for y in range(SIZE):
        row = bytearray([0])
        for x in range(SIZE):
            acc = [0, 0, 0, 0]
            for sy in range(SS):
                for sx in range(SS):
                    r, g, b, a = colour_at(x + (sx + 0.5) / SS, y + (sy + 0.5) / SS)
                    acc[0] += r * a
                    acc[1] += g * a
                    acc[2] += b * a
                    acc[3] += a
            n = SS * SS
            alpha = acc[3] / n
            if acc[3] > 0:
                row += bytes([round(acc[0] / acc[3]), round(acc[1] / acc[3]), round(acc[2] / acc[3]), round(alpha)])
            else:
                row += bytes([0, 0, 0, 0])
        rows.append(bytes(row))
    raw = b"".join(rows)

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", SIZE, SIZE, 8, 6, 0, 0, 0)) + \
        chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def main():
    assets = Path(__file__).resolve().parent.parent / "src" / "AVAPageStream" / "Assets"
    png = render()
    (assets / "AVAPageStream.png").write_bytes(png)
    # An .ico holding the PNG as its one (256×256) image.
    ico = struct.pack("<HHH", 0, 1, 1) + struct.pack("<BBBBHHII", 0, 0, 0, 0, 1, 32, len(png), 22) + png
    (assets / "AVAPageStream.ico").write_bytes(ico)


if __name__ == "__main__":
    main()
