"""Сравнить два отчёта об ошибке (zip из bugreports/) одного расхождения.

Запуск: python -I tools/compare_bugreports.py <отчёт A.zip> <отчёт B.zip>

- state.txt: какие части хеша мира и карт различаются;
- things-mapN.txt (есть в автоотчётах о расхождении): первые различающиеся строки списков вещей;
- report.txt обоих — тик, игроки, карты.
"""
import sys
import zipfile


def parts(line):
    return dict(p.split("=", 1) for p in line.split(";") if "=" in p)


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    za, zb = zipfile.ZipFile(sys.argv[1]), zipfile.ZipFile(sys.argv[2])
    for name, z in (("A", za), ("B", zb)):
        print(f"--- {name}: {z.filename}")
        print("\n".join(z.read("report.txt").decode("utf-8").splitlines()[:12]))
    sa = za.read("state.txt").decode("utf-8").splitlines()
    sb = zb.read("state.txt").decode("utf-8").splitlines()
    print("--- state.txt")
    keyed_a = {l.split(";")[0]: l for l in sa}
    keyed_b = {l.split(";")[0]: l for l in sb}
    for key in sorted(set(keyed_a) | set(keyed_b)):
        a, b = keyed_a.get(key), keyed_b.get(key)
        if a == b:
            continue
        if a is None or b is None:
            print(f"{key}: только в {'B' if a is None else 'A'}")
            continue
        pa, pb = parts(a), parts(b)
        diff = [k for k in sorted(set(pa) | set(pb)) if pa.get(k) != pb.get(k)]
        print(f"{key}: различаются {', '.join(diff)}")
    names = sorted(n for n in set(za.namelist()) & set(zb.namelist()) if n.startswith("things-map"))
    for n in names:
        la = za.read(n).decode("utf-8").splitlines()
        lb = zb.read(n).decode("utf-8").splitlines()
        only_a = [l for l in la if l not in set(lb)]
        only_b = [l for l in lb if l not in set(la)]
        if only_a or only_b:
            print(f"--- {n}: строк только в A {len(only_a)}, только в B {len(only_b)}")
            for l in only_a[:5]:
                print("  A:", l[:200])
            for l in only_b[:5]:
                print("  B:", l[:200])


if __name__ == "__main__":
    main()
