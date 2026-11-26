"""Сравнить диагностику мира (строки `world` файлов хешей) двух клиентов прогона.

Хеш мира, который проверяет сервер, уже, чем диагностика: в режиме заглушки (sleepremove) часть состава мира
в хеш не входит (мировые пешки, сказания). Этот сценарий находит расхождения таких частей.

Запуск: python -I tools/compare_world.py <имя прогона> [c1 c2]
Части, которые у каждого игрока свои по замыслу, пропускаются (LOCAL).
"""
import glob
import os
import sys

# Свои у каждого: блоки номеров карт, список своих карт, журналы, письма и история (события своей карты).
LOCAL = {"ids", "pMaps", "playLog", "letters", "archive", "history", "hash"}


def load(run, client):
    root = os.path.join(os.environ["LOCALAPPDATA"], "RWMLWC-test", run, client, "RWMLWC")
    files = sorted(glob.glob(os.path.join(root, f"hash-{client}*.txt")))
    if not files:
        sys.exit(f"нет файла хешей в {root}")
    rows = {}
    with open(files[-1], encoding="utf-8", errors="replace") as f:
        for line in f:
            parts = line.strip().split(";")
            if len(parts) > 2 and parts[1] == "world":
                rows[int(parts[0])] = dict(p.split("=", 1) for p in parts[2:] if "=" in p)
    return rows


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    run = sys.argv[1]
    a_name, b_name = (sys.argv[2], sys.argv[3]) if len(sys.argv) > 3 else ("c1", "c2")
    a, b = load(run, a_name), load(run, b_name)
    ticks = sorted(set(a) & set(b))
    print(f"{len(ticks)} общих замеров, последний {ticks[-1] if ticks else '-'}")
    keys = sorted(set().union(*(a[t].keys() for t in ticks)) - LOCAL) if ticks else []
    for k in keys:
        bad = [t for t in ticks if a[t].get(k) != b[t].get(k)]
        if not bad:
            continue
        # Устойчивое расхождение: последний замер тоже расходится.
        tail = "до конца" if bad[-1] == ticks[-1] else f"последний {bad[-1]}"
        t0 = bad[0]
        print(f"{k}: {len(bad)} замеров, первый {t0} ({a[t0].get(k)} / {b[t0].get(k)}), {tail}")


if __name__ == "__main__":
    main()
