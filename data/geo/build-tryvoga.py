"""
Мапа «🚨 Тривога» — web/static/tryvoga-map.json (docs/tryvoga.md, «Мапа районів»).

    python data/geo/build-tryvoga.py [raions.geojson oblasts.geojson]   # з кореня репозиторію, повним шляхом до python.exe

Без аргументів качає https://neptun.in.ua/raions.geojson і /oblasts.geojson у %TEMP% (у гіт не йдуть). Межі саме
звідти, а не з Natural Earth: ключі районів (`key`) у них ті самі, що в API тривог NEPTUN, тож клієнт фарбує район
за ключем без жодної таблиці відповідностей. Умова NEPTUN — видиме посилання на нього поруч із мапою (є на сторінці).

Що робить:
  1. Одиниці мапи — 136 районів і ще дві одиниці з обласного файла, яких серед районів нема: м. Київ і Севастополь
     (тривога там оголошується на все місто).
  2. Проєкція й сітка 4000×2730 — ті самі, що в «Де це?» (build-map.py), тож обласні центри й річки беремо прямо з
     web/games/geo-map.json, а клієнт рахує координати цілей тією ж формулою (proj у файлі).
  3. Область району — та, у чий полігон падає «найглибша» точка району (вона ж — точка підпису й центр «мого району»).
  4. Дуги в стилі TopoJSON: спільна межа двох районів — одна дуга, тож після спрощення Дугласом — Пекером між
     районами не лишається щілин. Межу області клієнт бачить сам: дуга між районами різних областей.
  5. Дуги — дельтами (перша точка абсолютна, далі різниці): файл удвічі менший.
"""
import importlib.util
import json
import math
import os
import sys
import tempfile
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
OUT = os.path.join(ROOT, "web", "static", "tryvoga-map.json")
GEO = os.path.join(ROOT, "web", "games", "geo-map.json")
SRC = "https://neptun.in.ua/"
UA = "Hlechyky/1.0 (https://hlechyky.pp.ua)"
TOL = 2.5             # одиниць сітки (~0,8 км): районні межі на телефоні все одно тонші за піксель

_spec = importlib.util.spec_from_file_location("buildmap", os.path.join(HERE, "build-map.py"))
bm = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(bm)

# Обласні ключі NEPTUN → коротка назва для вибору району й підписів.
SHORT = {
    "автономна республіка крим": "Крим", "м. київ": "Київ", "севастополь": "Севастополь",
}


def fetch(name):
    path = os.path.join(tempfile.gettempdir(), "neptun-" + name)
    if not os.path.exists(path) or os.path.getsize(path) < 50_000:
        print("качаю", name)
        req = urllib.request.Request(SRC + name, headers={"User-Agent": UA})
        with urllib.request.urlopen(req, timeout=120) as r, open(path + ".tmp", "wb") as f:
            f.write(r.read())
        os.replace(path + ".tmp", path)
    return path


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def rings_of(geom):
    polys = geom["coordinates"] if geom["type"] == "MultiPolygon" else [geom["coordinates"]]
    out = []
    for poly in polys:
        for ring in poly:
            pts = bm.dedupe([bm.project(lat, lon) for lon, lat in ring])
            if len(pts) >= 3:
                out.append(pts)
    return out


def inside(rings, x, y):
    """Парний-непарний по всіх кільцях одиниці (дірки теж кільця)."""
    c = False
    for pts in rings:
        n = len(pts)
        for i in range(n):
            (x1, y1), (x2, y2) = pts[i], pts[(i + 1) % n]
            if (y1 > y) != (y2 > y) and x < x1 + (y - y1) * (x2 - x1) / (y2 - y1):
                c = not c
    return c


def seg_dist2(px, py, a, b):
    dx, dy = b[0] - a[0], b[1] - a[1]
    l2 = dx * dx + dy * dy
    t = 0 if l2 == 0 else max(0.0, min(1.0, ((px - a[0]) * dx + (py - a[1]) * dy) / l2))
    ex, ey = a[0] + t * dx - px, a[1] + t * dy - py
    return ex * ex + ey * ey


def deepest(rings):
    """«Найглибша» точка (далі за все від меж) — сіткою по рамці найбільшого кільця, потім дрібніше довкола кращої."""
    big = max(rings, key=lambda r: abs(sum(r[i][0] * r[(i + 1) % len(r)][1] - r[(i + 1) % len(r)][0] * r[i][1] for i in range(len(r)))))
    xs, ys = [p[0] for p in big], [p[1] for p in big]
    segs = [(r[i], r[(i + 1) % len(r)]) for r in rings for i in range(len(r))]

    def score(x, y):
        if not inside(rings, x, y):
            return -1
        return min(seg_dist2(x, y, a, b) for a, b in segs)

    best = (-1, (sum(xs) / len(xs), sum(ys) / len(ys)))
    x0, x1, y0, y1 = min(xs), max(xs), min(ys), max(ys)
    step = max(x1 - x0, y1 - y0) / 24 or 1
    for _ in range(3):
        y = y0
        while y <= y1:
            x = x0
            while x <= x1:
                s = score(x, y)
                if s > best[0]:
                    best = (s, (x, y))
                x += step
            y += step
        cx, cy = best[1]
        x0, x1, y0, y1 = cx - step, cx + step, cy - step, cy + step
        step /= 4
    return int(round(best[1][0])), int(round(best[1][1]))


def main():
    if len(sys.argv) == 3:
        raions_path, oblasts_path = sys.argv[1], sys.argv[2]
    else:
        raions_path, oblasts_path = fetch("raions.geojson"), fetch("oblasts.geojson")
    raions, oblasts = load(raions_path), load(oblasts_path)

    obl = []        # (key, name, rings)
    for f in oblasts["features"]:
        p = f["properties"]
        name = p.get("region") or p.get("name") or p["key"]
        obl.append((p["key"], name, rings_of(f["geometry"])))
    by_key = {k: (n, r) for k, n, r in obl}

    units = []      # (key, name, oblast_key, rings)
    for f in raions["features"]:
        p = f["properties"]
        units.append([p["key"], p.get("rayon") or p["key"], None, rings_of(f["geometry"])])
    assert len(units) == 136, f"районів {len(units)}, а має бути 136"
    for k in ("м. київ", "севастополь"):
        units.append([k, by_key[k][0], k, by_key[k][1]])

    centers = {}
    plain = [(k, r) for k, _, r in obl if k not in ("м. київ", "севастополь")]
    for u in units:
        c = deepest(u[3])
        centers[u[0]] = c
        if u[2] is None:
            hits = [k for k, r in plain if inside(r, *c)]
            assert len(hits) == 1, f"{u[0]}: області {hits}"
            u[2] = hits[0]
    # район, що накриває Київ (джерело не вирізає місто з району): малюємо Київ поверх — порядок і так такий
    kc = centers["м. київ"]
    over = [u[0] for u in units[:-2] if inside(u[3], *kc)]
    if over:
        print("Київ лежить і всередині району:", over, "— малюється поверх")

    # ---- дуги, як у build-map.py: вузол там, де змінюється набір кілець по обидва боки ребра
    all_rings = [(ui, r) for ui, u in enumerate(units) for r in u[3]]
    edge_rings = {}
    for ri, (_, pts) in enumerate(all_rings):
        n = len(pts)
        for i in range(n):
            a, b = pts[i], pts[(i + 1) % n]
            edge_rings.setdefault((a, b) if a < b else (b, a), set()).add(ri)

    def eset(pts, i):
        n = len(pts)
        a, b = pts[i % n], pts[(i + 1) % n]
        return frozenset(edge_rings[(a, b) if a < b else (b, a)])

    arcs, arc_index, ring_arcs = [], {}, []

    def add_arc(seq):
        t = tuple(seq)
        if t in arc_index:
            return arc_index[t], False
        rt = tuple(reversed(seq))
        if rt in arc_index:
            return arc_index[rt], True
        arc_index[t] = len(arcs)
        arcs.append(list(seq))
        return len(arcs) - 1, False

    for _, pts in all_rings:
        n = len(pts)
        nodes = [i for i in range(n) if eset(pts, i - 1) != eset(pts, i)]
        if not nodes:
            start = min(range(n), key=lambda i: pts[i])
            seq = pts[start:] + pts[:start]
            fwd = seq + [seq[0]]
            back = [seq[0]] + list(reversed(seq[1:])) + [seq[0]]
            if tuple(back) < tuple(fwd):
                idx, _ = add_arc(back)
                ring_arcs.append([(idx, True)])
            else:
                ring_arcs.append([add_arc(fwd)])
            continue
        refs = []
        for k in range(len(nodes)):
            a, b = nodes[k], nodes[(k + 1) % len(nodes)]
            if b <= a:
                b += n
            refs.append(add_arc([pts[i % n] for i in range(a, b + 1)]))
        ring_arcs.append(refs)

    raw_pts = sum(len(a) for a in arcs)
    simple = [bm.dp_closed(a, TOL) if a[0] == a[-1] else bm.dp(a, TOL) for a in arcs]
    print(f"дуг: {len(arcs)}, точок до спрощення: {raw_pts}, після: {sum(len(s) for s in simple)}")

    def ring_len(refs):
        pts = []
        for idx, rev in refs:
            s = simple[idx][::-1] if rev else simple[idx]
            pts.extend(s if not pts else s[1:])
        return len(set(pts))

    out_units, used = [], set()
    for ui, u in enumerate(units):
        mine = [ring_arcs[ri] for ri, (owner, _) in enumerate(all_rings) if owner == ui]
        keep = [refs for refs in mine if ring_len(refs) >= 3] or mine
        for refs in keep:
            used.update(idx for idx, _ in refs)
        out_units.append((u, keep))
    remap = {old: new for new, old in enumerate(sorted(used))}

    def delta(a):
        out = [a[0][0], a[0][1]]
        for (x0, y0), (x1, y1) in zip(a, a[1:]):
            out += [x1 - x0, y1 - y0]
        return out

    geo = load(GEO)
    oblast_list = []
    for k, name, _ in obl:
        short = SHORT.get(k) or name.replace(" область", "")
        oblast_list.append({"k": k, "n": name, "s": short})
    oblast_list.sort(key=lambda o: (o["k"] in ("м. київ", "севастополь", "автономна республіка крим"), o["s"]))

    out = {
        "v": 1, "w": bm.W, "h": bm.H,
        "proj": {"phi1": bm.PHI1, "phi2": bm.PHI2, "phi0": bm.PHI0, "lam0": bm.LAM0, "x0": bm.X0, "y0": bm.Y0, "k": bm.K},
        "src": "Межі районів: NEPTUN (neptun.in.ua). Обласні центри й річки: Natural Earth (суспільне надбання).",
        "arcs": [delta(simple[old]) for old in sorted(used)],
        "units": [{
            "k": u[0], "n": u[1], "o": u[2], "c": list(centers[u[0]]),
            "rings": [[(remap[idx] + 1) * (-1 if rev else 1) for idx, rev in refs] for refs in keep],
        } for u, keep in out_units],
        "oblasts": oblast_list,
        "rivers": [r for r in geo.get("rivers", []) if r.get("big") or r["name"] in ("Десна", "Сіверський Донець", "Південний Буг", "Дністер")],
        "cities": [c for c in geo.get("cities", []) if c.get("lvl", 9) <= 1],
    }
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        json.dump(out, f, ensure_ascii=False, separators=(",", ":"))
    size = os.path.getsize(OUT)
    print(f"одиниць: {len(out['units'])}, областей: {len(oblast_list)}, дуг: {len(out['arcs'])}, {size // 1024} КБ → {OUT}")
    assert size < 200_000, "мапа понад 200 КБ — збільш TOL"


if __name__ == "__main__":
    main()
