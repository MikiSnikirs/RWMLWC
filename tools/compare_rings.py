#!/usr/bin/env python3
"""Compare two ring-dump files (T-076 ringdump=1): things of one map at one tick.

Usage: python tools/compare_rings.py <ring-A.txt> <ring-B.txt> [--limit N]

Line format: thingID;defShortHash;x;z;stackCount;hitPoints
Special line: -1;randLo;randHi;globalRandLo;globalRandHi;0
"""
import sys


def load(path):
    things = {}
    rand = None
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            p = line.strip().split(";")
            if len(p) != 6:
                continue
            if p[0] == "-1":
                rand = p[1:]
            else:
                things[int(p[0])] = tuple(p[1:])
    return things, rand


def main(argv):
    if len(argv) < 3:
        print(__doc__)
        return 2
    limit = 40
    if "--limit" in argv:
        limit = int(argv[argv.index("--limit") + 1])
    a, ra = load(argv[1])
    b, rb = load(argv[2])
    print(f"A: {len(a)} things, rand={ra}")
    print(f"B: {len(b)} things, rand={rb}")
    print("RAND:", "same" if ra == rb else "DIFFERENT")
    only_a = sorted(set(a) - set(b))
    only_b = sorted(set(b) - set(a))
    print(f"only A: {len(only_a)} {only_a[:limit]}")
    print(f"only B: {len(only_b)} {only_b[:limit]}")
    diff = [(k, a[k], b[k]) for k in sorted(set(a) & set(b)) if a[k] != b[k]]
    print(f"differing fields: {len(diff)}")
    fields = ("defHash", "x", "z", "stack", "hp")
    by_field = {}
    for k, x, y in diff:
        for i, name in enumerate(fields):
            if x[i] != y[i]:
                by_field.setdefault(name, []).append(k)
    for name, ids in by_field.items():
        print(f"  {name}: {len(ids)} things, e.g. {ids[:10]}")
    for k, x, y in diff[:limit]:
        print(f"  {k}: {x} -> {y}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))