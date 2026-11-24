#!/usr/bin/env python3
"""Scan server-out.txt files for desync notices, encoding-robust.

Usage: python tools/scan_desync.py <dir-or-file> [...]
Prints one line per file: desync ticks found (or 'clean').
"""
import os
import re
import sys

# "РАСХОЖДЕНИЕ" in cp1251 and cp866, plus the utf8-garbled form seen in console captures.
MARKERS = [
    "РАСХОЖДЕНИЕ".encode("utf-8"),
    "РАСХОЖДЕНИЕ".encode("cp1251"),
    "РАСХОЖДЕНИЕ".encode("cp866"),
]
TICK_RE = re.compile(rb"\d{3,7}")


def scan(path):
    data = open(path, "rb").read()
    hits = []
    for m in MARKERS:
        start = 0
        while True:
            i = data.find(m, start)
            if i < 0:
                break
            tail = data[i:i + 160]
            tm = TICK_RE.search(tail)
            hits.append(tm.group(0).decode() if tm else "?")
            start = i + len(m)
    return sorted(set(hits))


def main(argv):
    paths = []
    for a in argv[1:]:
        if os.path.isdir(a):
            for root, _, files in os.walk(a):
                for f in files:
                    if f == "server-out.txt":
                        paths.append(os.path.join(root, f))
        else:
            paths.append(a)
    for p in sorted(paths):
        hits = scan(p)
        label = os.path.relpath(p)
        print(f"{label}: {'DESYNC ticks ' + ','.join(hits) if hits else 'clean'}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))