#!/usr/bin/env python3
"""Сравнение двух тестовых прогонов RWMLWC (файлы hash-*.txt и, если есть, randtrace-*.txt).

Использование:
    python tools/compare_runs.py hashA.txt hashB.txt [traceA.txt traceB.txt]

Что делает:
  1. Сравнивает хеши на общих тиках по каждой карте и по глобальному Rand;
     печатает первый тик и первое поле, где прогоны расходятся.
  2. Если даны трассировки: находит первый тик, где отличается ЧИСЛО вызовов Rand
     (по контексту: global / mapN), и печатает различие цепочек вызовов на этом тике.

Окончания строк CRLF и LF считаются одинаковыми.
Имена кадров стека могут отличаться между запусками из-за встраивания методов JIT-ом,
поэтому первым признаком служит число вызовов, а не побайтное сравнение строк.
"""
import difflib
import sys
from collections import Counter, OrderedDict


def read_lines(path):
    with open(path, encoding="utf-8", errors="replace") as f:
        return [line.rstrip("\r\n") for line in f]


def parse_hash(path):
    """-> {(context, tick): {field: value}}; context = 'global' или 'mapN'."""
    out = OrderedDict()
    header = ""
    for line in read_lines(path):
        if not line:
            continue
        if line.startswith("#"):
            header = line
            continue
        parts = line.split(";")
        tick, ctx = int(parts[0]), parts[1]
        fields = OrderedDict()
        for p in parts[2:]:
            if "=" in p:
                k, v = p.split("=", 1)
                fields[k] = v
        out[(ctx, tick)] = fields
    return header, out


def compare_hashes(path_a, path_b):
    head_a, a = parse_hash(path_a)
    head_b, b = parse_hash(path_b)
    print("A:", head_a)
    print("B:", head_b)
    common = [k for k in a if k in b]
    print(f"замеров: A={len(a)}, B={len(b)}, общих={len(common)}")
    first = {}  # (ctx, field) -> tick
    for ctx, tick in common:
        fa, fb = a[(ctx, tick)], b[(ctx, tick)]
        for field in fa:
            if field in fb and fa[field] != fb[field] and (ctx, field) not in first:
                first[(ctx, field)] = tick
    if not first:
        print("ХЕШИ: совпадают на всех общих замерах")
        return None
    earliest = min(first.values())
    print("ХЕШИ: расходятся. Первое расхождение по полям:")
    for (ctx, field), tick in sorted(first.items(), key=lambda kv: (kv[1], kv[0])):
        print(f"  {ctx:7s} {field:8s} тик {tick}")
    # последний общий замер, где всё совпадало
    last_ok = None
    for ctx, tick in common:
        if tick < earliest and a[(ctx, tick)] == b[(ctx, tick)]:
            last_ok = tick if last_ok is None else max(last_ok, tick)
    print(f"последний замер без расхождений: тик {last_ok}; первый с расхождением: тик {earliest}")
    return earliest


def parse_trace(path):
    """-> OrderedDict {(tick, ctx): [цепочка, ...]}"""
    out = OrderedDict()
    for line in read_lines(path):
        if not line or line.startswith("#"):
            continue
        parts = line.split(";", 2)
        if len(parts) < 3:
            continue
        key = (int(parts[0]), parts[1])
        out.setdefault(key, []).append(parts[2])
    return out


def shorten(chain):
    # всё, что выше входа в тик, одинаково и только мешает читать
    cut = chain.find(" < TickManager.")
    return chain if cut < 0 else chain[:cut]


def compare_traces(path_a, path_b):
    a = parse_trace(path_a)
    b = parse_trace(path_b)
    ticks_a = sorted({t for t, _ in a})
    ticks_b = sorted({t for t, _ in b})
    if not ticks_a or not ticks_b:
        print("ТРАССИРОВКА: один из файлов пуст")
        return
    print(f"трассировка: A тики {ticks_a[0]}..{ticks_a[-1]} ({sum(len(v) for v in a.values())} вызовов), "
          f"B тики {ticks_b[0]}..{ticks_b[-1]} ({sum(len(v) for v in b.values())} вызовов)")
    keys = sorted(set(a) | set(b))
    for key in keys:
        la, lb = a.get(key, []), b.get(key, [])
        if len(la) != len(lb):
            tick, ctx = key
            print(f"ТРАССИРОВКА: первое отличие числа вызовов — тик {tick}, контекст {ctx}: A={len(la)}, B={len(lb)}")
            sa, sb = [shorten(x) for x in la], [shorten(x) for x in lb]
            ca, cb = Counter(sa), Counter(sb)
            only_a, only_b = ca - cb, cb - ca
            if only_a:
                print("  только в A:")
                for chain, n in only_a.most_common(15):
                    print(f"    {n}× {chain}")
            if only_b:
                print("  только в B:")
                for chain, n in only_b.most_common(15):
                    print(f"    {n}× {chain}")
            print("  (если списки выше выглядят как одни и те же вызовы с разными именами кадров — это встраивание JIT; "
                  "смотри на строки, у которых нет пары)")
            print("  порядок вызовов на этом тике (diff):")
            for line in list(difflib.unified_diff(sa, sb, "A", "B", lineterm="", n=1))[:60]:
                print("   ", line)
            return
    print("ТРАССИРОВКА: число вызовов Rand совпадает на всех тиках и во всех контекстах")


def main(argv):
    if len(argv) not in (3, 5):
        print(__doc__)
        return 2
    compare_hashes(argv[1], argv[2])
    if len(argv) == 5:
        print()
        compare_traces(argv[3], argv[4])
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
