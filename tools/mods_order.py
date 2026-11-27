"""Собрать порядок загрузки модов для тестового профиля (T-082).

Запуск: python -I tools/mods_order.py <папка workshop 294100> <шаблон ModsConfig.xml> <выходной ModsConfig.xml> [--exclude id,id]

- берёт все моды из папки workshop, плюс ядро, DLC, Harmony и RWMLWC;
- выкидывает rwmt.multiplayer и rwmt.multiplayercompatibility (другой сетевой мод) и моды без 1.7 в supportedVersions,
  а затем всё, что от выкинутых зависит;
- сортирует по loadAfter/loadBefore/forceLoad*/modDependencies (включая …ByVersion для 1.7), при равенстве —
  по packageId; ядро и DLC первыми после Harmony, RWMLWC последним;
- печатает выкинутое и причину.
"""
import os
import sys
import xml.etree.ElementTree as ET

ALWAYS_OUT = {"rwmt.multiplayer": "другой сетевой мод", "rwmt.multiplayercompatibility": "другой сетевой мод"}
CORE = ["brrainz.harmony", "ludeon.rimworld", "ludeon.rimworld.royalty", "ludeon.rimworld.ideology",
        "ludeon.rimworld.biotech", "ludeon.rimworld.anomaly", "ludeon.rimworld.odyssey"]
OURS = "mikisnikirs.rwmlwc"


def ids(node, path):
    out = []
    if node is None:
        return out
    for el in node.findall(path):
        t = (el.text or "").strip().lower()
        if t:
            out.append(t)
    return out


def read(about):
    r = ET.parse(about).getroot()
    pid = (r.findtext("packageId") or "").strip().lower()
    vers = [(li.text or "").strip() for li in r.findall("supportedVersions/li")]
    after = ids(r, "loadAfter/li") + ids(r, "forceLoadAfter/li") + ids(r, "loadAfterByVersion/v1.7/li")
    before = ids(r, "loadBefore/li") + ids(r, "forceLoadBefore/li") + ids(r, "loadBeforeByVersion/v1.7/li")
    deps = [(li.findtext("packageId") or "").strip().lower() for li in r.findall("modDependencies/li")]
    deps += [(li.findtext("packageId") or "").strip().lower() for li in r.findall("modDependenciesByVersion/v1.7/li")]
    incompat = ids(r, "incompatibleWith/li") + ids(r, "incompatibleWithByVersion/v1.7/li")
    return dict(pid=pid, name=(r.findtext("name") or "").strip(), v17="1.7" in vers,
                after=after, before=before, deps=[d for d in deps if d], incompat=incompat)


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    ws, template, out = sys.argv[1], sys.argv[2], sys.argv[3]
    extra_out = set()
    if "--exclude" in sys.argv:
        extra_out = {x.strip().lower() for x in sys.argv[sys.argv.index("--exclude") + 1].split(",") if x.strip()}
    mods = {}
    for d in sorted(os.listdir(ws)):
        about = os.path.join(ws, d, "About", "About.xml")
        if os.path.exists(about):
            try:
                m = read(about)
            except ET.ParseError as e:
                print(f"не разобран About.xml {d}: {e}")
                continue
            m["dir"] = d
            mods[m["pid"]] = m
    out_reason = {}
    for pid, m in mods.items():
        if pid in ALWAYS_OUT:
            out_reason[pid] = ALWAYS_OUT[pid]
        elif pid in extra_out:
            out_reason[pid] = "исключён вручную"
        elif not m["v17"]:
            out_reason[pid] = "нет 1.7 в supportedVersions"
    known = set(mods) | set(CORE) | {OURS}
    changed = True
    while changed:
        changed = False
        for pid, m in mods.items():
            if pid in out_reason:
                continue
            for dep in m["deps"]:
                if dep in out_reason or (dep not in known and not dep.startswith("ludeon.")):
                    out_reason[pid] = f"зависит от {dep}" + (" (выкинут)" if dep in out_reason else " (не установлен)")
                    changed = True
                    break
    active = [p for p in mods if p not in out_reason]
    for pid in active:
        for bad in mods[pid]["incompat"]:
            if bad in active:
                print(f"несовместимы: {pid} и {bad}")
    # граф: a -> b значит a раньше b
    edges = {p: set() for p in active}
    indeg = {p: 0 for p in active}

    def edge(a, b):
        if a in edges and b in edges and a != b and b not in edges[a]:
            edges[a].add(b)
            indeg[b] += 1

    for pid in active:
        m = mods[pid]
        for a in m["after"] + m["deps"]:
            edge(a, pid)
        for b in m["before"]:
            edge(pid, b)
    order = []
    ready = sorted(p for p in active if indeg[p] == 0)
    while ready:
        p = ready.pop(0)
        order.append(p)
        for q in sorted(edges[p]):
            indeg[q] -= 1
            if indeg[q] == 0:
                ready.append(q)
                ready.sort()
    cyclic = [p for p in active if p not in order]
    if cyclic:
        print("цикл в порядке, добавлены в конец:", ", ".join(sorted(cyclic)))
        order += sorted(cyclic)
    final = CORE + [p for p in order if p not in CORE and p != OURS] + [OURS]
    tree = ET.parse(template)
    root = tree.getroot()
    am = root.find("activeMods")
    for li in list(am):
        am.remove(li)
    for p in final:
        ET.SubElement(am, "li").text = p
    tree.write(out, encoding="utf-8", xml_declaration=True)
    print(f"включено {len(final)} (из них модов мастерской {len(order)}), выкинуто {len(out_reason)}")
    for p, why in sorted(out_reason.items()):
        print(f"  - {mods[p]['name']} [{p}]: {why}")


if __name__ == "__main__":
    main()
