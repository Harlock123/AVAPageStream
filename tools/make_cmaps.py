#!/usr/bin/env python3
"""
Makes src/AVAPageStream.Core/CMaps/cmaps.bin from Adobe's CMap resources
(https://github.com/adobe-type-tools/cmap-resources, BSD-3-Clause; their LICENSE.md is kept beside it):

    python3 tools/make_cmaps.py path/to/cmap-resources

Each CMap's code space ranges and its codes to CIDs (cidrange, cidchar), with the CMap it uses; and each character
collection's CIDs to Unicode, from its UTF-32 CMap, so text in a font a PDF only names can be drawn from a font on
the computer. Written compactly and deflated.
"""
import os, re, struct, sys, zlib

src = sys.argv[1]
collections = ['Adobe-CNS1-7', 'Adobe-GB1-6', 'Adobe-Japan1-7', 'Adobe-Korea1-2', 'Adobe-KR-9']
token = re.compile(rb'<([0-9A-Fa-f]*)>|/([^\s/<>\[\]()]+)|(-?\d+)|([A-Za-z]+)')

def parse(path):
    data = open(path, 'rb').read()
    spaces, ranges, use, words = [], [], None, []
    for m in token.finditer(data):
        hexs, name, num, word = m.groups()
        if hexs is not None: words.append(bytes.fromhex(hexs.decode()))
        elif name is not None: words.append(('name', name.decode()))
        elif num is not None: words.append(int(num))
        else:
            w = word.decode()
            if w == 'usecmap' and words and isinstance(words[-1], tuple): use = words[-1][1]
            elif w == 'endcodespacerange':
                for i in range(0, len(words) - 1, 2):
                    if isinstance(words[i], bytes) and isinstance(words[i + 1], bytes): spaces.append((words[i], words[i + 1]))
            elif w == 'endcidrange':
                for i in range(0, len(words) - 2, 3):
                    lo, hi, cid = words[i], words[i + 1], words[i + 2]
                    if isinstance(lo, bytes) and isinstance(hi, bytes) and isinstance(cid, int): ranges.append((len(lo), int.from_bytes(lo, 'big'), int.from_bytes(hi, 'big'), cid))
            elif w == 'endcidchar':
                for i in range(0, len(words) - 1, 2):
                    code, cid = words[i], words[i + 1]
                    if isinstance(code, bytes) and isinstance(cid, int): ranges.append((len(code), int.from_bytes(code, 'big'), int.from_bytes(code, 'big'), cid))
            if w in ('begincodespacerange', 'begincidrange', 'begincidchar', 'endcodespacerange', 'endcidrange', 'endcidchar', 'usecmap', 'beginnotdefrange', 'endnotdefrange', 'beginnotdefchar', 'endnotdefchar'):
                words = []
    return spaces, ranges, use

def varint(v):
    o = bytearray()
    while True:
        b = v & 0x7F
        v >>= 7
        if v: o.append(b | 0x80)
        else: o.append(b); return bytes(o)

def text(s):
    b = s.encode()
    return varint(len(b)) + b

out = bytearray(b'AVCM')
cmaps = []
unicode = {}
for c in collections:
    folder = os.path.join(src, c, 'CMap')
    ordering = c.split('-')[1]  # CNS1, GB1, Japan1, Korea1, KR
    for name in sorted(os.listdir(folder)):
        spaces, ranges, use = parse(os.path.join(folder, name))
        if not spaces and not ranges and use is None: continue
        cmaps.append((name, use, spaces, ranges))
        if 'UTF32-H' in name and not name.startswith('UniJIS2004') and not 'X' in name.replace('UTF', ''):
            # CIDs to Unicode: the first code for each CID.
            m = unicode.setdefault(ordering, {})
            for n, lo, hi, cid in ranges:
                for k in range(hi - lo + 1):
                    m.setdefault(cid + k, lo + k)
out += varint(len(cmaps))
for name, use, spaces, ranges in cmaps:
    out += text(name) + text(use or '')
    out += varint(len(spaces))
    for lo, hi in spaces: out += bytes([len(lo)]) + lo + hi
    out += varint(len(ranges))
    prev = 0
    for n, lo, hi, cid in sorted(ranges, key=lambda r: (r[0], r[1])):
        out += bytes([n]) + varint(lo) + varint(hi - lo) + varint(cid)
out += varint(len(unicode))
for ordering, m in sorted(unicode.items()):
    out += text(ordering) + varint(len(m))
    prev_cid = prev_u = 0
    for cid in sorted(m):
        out += varint(cid - prev_cid) + varint(m[cid])
        prev_cid = cid
dest = os.path.join(os.path.dirname(__file__), '..', 'src', 'AVAPageStream.Core', 'CMaps')
os.makedirs(dest, exist_ok=True)
open(os.path.join(dest, 'cmaps.bin'), 'wb').write(zlib.compress(bytes(out), 9))
open(os.path.join(dest, 'LICENSE.md'), 'wb').write(open(os.path.join(src, 'LICENSE.md'), 'rb').read())
print(len(cmaps), 'CMaps;', {k: len(v) for k, v in unicode.items()}, 'CIDs to Unicode;', len(out), 'bytes,', os.path.getsize(os.path.join(dest, 'cmaps.bin')), 'deflated')
