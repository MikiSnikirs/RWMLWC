"""Разбор прогона со сборкой модов (T-084): нажатия кнопок модов, расхождения сервера, ошибки журнала игры по модам.

Запуск: python -I tools/mod_errors.py <имя прогона> [Player.log]

- нажатия: строки «modmech: press [мод] …» из automation-log автора;
- расхождения: тики DESYNC из server-out.txt и последнее нажатие перед первым из них;
- ошибки журнала: ERROR/исключения, сгруппированные по пространству имён кода из стека (первый кадр не из игры,
  не из Harmony и не из RWMLWC) — грубо, «чей код упал».
"""
import glob
import os
import re
import sys
from collections import Counter, defaultdict

GAME_NS = ("Verse.", "RimWorld.", "System.", "UnityEngine.", "HarmonyLib.", "RWMLWC.", "Waylith", "MonoMod", "Unity.", "Multiplayer.API")


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    run = sys.argv[1]
    res = os.path.join(os.environ["USERPROFILE"], "AppData", "LocalLow", "Ludeon Studios", "RimWorld by Ludeon Studios", "RWMLWC", run)
    log = sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.environ["USERPROFILE"], "AppData", "LocalLow", "Ludeon Studios", "RimWorld by Ludeon Studios", "Logs", "Player.log")
    presses = []
    for c in ("c1", "c2"):
        f = os.path.join(res, c, "automation-log.txt")
        if not os.path.exists(f):
            continue
        for line in open(f, encoding="utf-8", errors="replace"):
            m = re.search(r"modmech: press \[(.+?)\] (\S+) '(.*)' tick=(\d+) sent=(\w+)", line)
            if m:
                presses.append((int(m.group(4)), m.group(1), m.group(2), m.group(3), m.group(5)))
            elif "modmech:" in line and ("FAILED" in line or "spawned" in line or "queued" in line or "gone" in line):
                print(c, line.strip()[20:])
    presses.sort()
    per_mod = Counter(p[1] for p in presses)
    print(f"нажатий: {len(presses)}, модов: {len(per_mod)}")
    for mod, n in per_mod.most_common():
        print(f"  {n:4} {mod}")
    desync = []
    srv = os.path.join(res, "server-out.txt")
    if os.path.exists(srv):
        text = open(srv, encoding="utf-8", errors="replace").read()
        desync = sorted({int(x) for x in re.findall(r"(?:расхожд\w*|desync|DESYNC)\D{0,40}?(\d{4,})", text)})
    print("расхождения сервера (тики):", desync[:20])
    for t in desync:
        if presses and t > presses[0][0]:
            before = [p for p in presses if p[0] <= t]
            if before:
                last = before[-5:]
                print(f"  перед {t}: " + "; ".join(f"[{p[1]}] {p[2]} '{p[3]}' @{p[0]}" for p in last))
    if not os.path.exists(log):
        return
    errors = Counter()
    sample = {}
    lines = open(log, encoding="utf-8", errors="replace").read().splitlines()
    for i, line in enumerate(lines):
        if " ERROR " not in line:
            continue
        owner = None
        for j in range(i + 1, min(i + 25, len(lines))):
            m = re.match(r"\s+at ([\w.<>`+]+)\.", lines[j])
            if m and not m.group(1).startswith(GAME_NS):
                owner = m.group(1).split(".")[0]
                break
        key = owner or "(игра/без кода мода)"
        msg = re.sub(r"\d+", "N", line.split(" ERROR ", 1)[1])[:110]
        errors[(key, msg)] += 1
        sample.setdefault(key, msg)
    by_owner = defaultdict(int)
    for (k, _), n in errors.items():
        by_owner[k] += n
    print("ошибки журнала по коду:")
    for k, n in sorted(by_owner.items(), key=lambda x: -x[1])[:30]:
        print(f"  {n:5} {k}: {sample[k]}")


if __name__ == "__main__":
    main()
