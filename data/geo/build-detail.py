"""
Детальна мапа «Де це?» — web/games/geo-detail.json (прохід №3, 29.09.2026). Лягає поверх geo-map.json:
  • підписи областей (коротко: «Львівська») — у «найглибшій» точці області (полюс недоступності, сіткою);
  • більші міста, що не обласні центри (рівні 3–4: показуються з наближенням) — координати з Natural Earth
    ne_10m_populated_places_simple (суспільне надбання), назви — українською, своєю таблицею;
  • головні дороги — траси М-01…М-30 з OpenStreetMap (© OpenStreetMap contributors, ODbL): Overpass-запит нижче,
    лише відрізки всередині України, склеєні за номером і спрощені Дугласом — Пекером.

Запуск з кореня репозиторію (дані OSM — з файла, бо Overpass відповідає по кілька хвилин):
  C:/Users/Ya/AppData/Local/Python/pythoncore-3.14-64/python.exe data/geo/build-detail.py <roads.json>
roads.json — відповідь Overpass (https://overpass.private.coffee/api/interpreter чи overpass-api.de) на:
  [out:json][timeout:280];
  way["highway"~"^(motorway|trunk|primary)$"]["ref"~"М"](44.2,22.0,52.5,40.3);
  out geom qt;
"""
import importlib.util
import json
import math
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
MAP = os.path.join(ROOT, "web", "games", "geo-map.json")
OUT = os.path.join(ROOT, "web", "games", "geo-detail.json")

_spec = importlib.util.spec_from_file_location("buildmap", os.path.join(HERE, "build-map.py"))
bm = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(bm)
project = bm.project
dp = bm.dp

TOL_ROAD = 2          # ~0,7 км: з наближенням траса лишається плавною, а файл — десятки КБ
ROAD_MIN_LEN = 30     # коротші шматки (~10 км) — геть: це з'їзди й уривки

# Міста, що не обласні центри: координати — Natural Earth (перевірено 29.09.2026), назви — українські.
# Рівень 3 — видно з k ≥ 2,6, рівень 4 — з k ≥ 4.
CITIES = [
    ("Кривий Ріг", 47.928, 33.345, 3), ("Маріуполь", 47.096, 37.556, 3), ("Горлівка", 48.300, 38.055, 3),
    ("Макіївка", 48.030, 37.975, 4), ("Кременчук", 49.084, 33.430, 3), ("Біла Церква", 49.774, 30.131, 3),
    ("Краматорськ", 48.719, 37.534, 3), ("Мелітополь", 46.838, 35.377, 3), ("Нікополь", 47.567, 34.406, 4),
    ("Лисичанськ", 48.920, 38.427, 4), ("Дрогобич", 49.344, 23.499, 4), ("Бердянськ", 46.757, 36.787, 3),
    ("Ніжин", 51.054, 31.890, 4), ("Кам'янець-Подільський", 48.684, 26.581, 3), ("Конотоп", 51.242, 33.209, 4),
    ("Шостка", 51.873, 33.480, 4), ("Бровари", 50.494, 30.781, 4), ("Умань", 48.754, 30.211, 3),
    ("Ізмаїл", 45.350, 28.837, 3), ("Куп'янськ", 49.722, 37.598, 4), ("Коростень", 50.950, 28.650, 4),
    ("Ковель", 51.217, 24.717, 4), ("Чорноморськ", 46.300, 30.667, 4), ("Вознесенськ", 47.550, 31.333, 4),
    ("Чорнобиль", 51.389, 30.099, 4),
]


def point_in(rings, x, y):
    inside = False
    for ring in rings:
        n = len(ring)
        j = n - 1
        for i in range(n):
            xi, yi = ring[i]
            xj, yj = ring[j]
            if (yi > y) != (yj > y) and x < (xj - xi) * (y - yi) / (yj - yi) + xi:
                inside = not inside
            j = i
    return inside


def seg_dist2(px, py, ax, ay, bx, by):
    dx, dy = bx - ax, by - ay
    if dx == 0 and dy == 0:
        return (px - ax) ** 2 + (py - ay) ** 2
    t = max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy)))
    return (px - ax - t * dx) ** 2 + (py - ay - t * dy) ** 2


def edge_dist(rings, x, y):
    best = 1e18
    for ring in rings:
        for i in range(len(ring)):
            a = ring[i - 1]
            b = ring[i]
            d = seg_dist2(x, y, a[0], a[1], b[0], b[1])
            if d < best:
                best = d
    return math.sqrt(best)


def pole(rings):
    """Полюс недоступності сіткою з двома уточненнями: точка всередині, найдальша від меж."""
    xs = [p[0] for r in rings for p in r]
    ys = [p[1] for r in rings for p in r]
    x0, x1, y0, y1 = min(xs), max(xs), min(ys), max(ys)
    best = ((x0 + x1) / 2, (y0 + y1) / 2, -1)
    step = max(x1 - x0, y1 - y0) / 24
    cx, cy, span = best[0], best[1], max(x1 - x0, y1 - y0) / 2 + step
    for _ in range(3):
        n = int(2 * span / step) + 1
        for i in range(n):
            for j in range(n):
                x = cx - span + i * step
                y = cy - span + j * step
                if not point_in(rings, x, y):
                    continue
                d = edge_dist(rings, x, y)
                if d > best[2]:
                    best = (x, y, d)
        cx, cy = best[0], best[1]
        span = step * 1.5
        step /= 4
    return round(best[0]), round(best[1])


def rings_of(doc):
    arcs = doc["arcs"]
    out = {}
    for r in doc["regions"]:
        rings = []
        for ring in r["rings"]:
            pts = []
            for idx in ring:
                a = arcs[abs(idx) - 1]
                p = [(a[k], a[k + 1]) for k in range(0, len(a), 2)]
                if idx < 0:
                    p.reverse()
                pts.extend(p if not pts else p[1:])
            rings.append(pts)
        out[r["id"]] = (r, rings)
    return out


def merge(lines):
    """Склеїти шматки однієї траси кінець-у-кінець (OSM ріже дорогу на сотні way)."""
    lines = [l[:] for l in lines if len(l) >= 2]
    changed = True
    while changed:
        changed = False
        ends = {}
        for i, l in enumerate(lines):
            ends.setdefault(l[0], []).append((i, 0))
            ends.setdefault(l[-1], []).append((i, 1))
        used = set()
        out = []
        for i, l in enumerate(lines):
            if i in used:
                continue
            used.add(i)
            cur = l
            grown = True
            while grown:
                grown = False
                for side in (1, 0):
                    key = cur[-1] if side else cur[0]
                    for j, e in ends.get(key, []):
                        if j in used:
                            continue
                        o = lines[j]
                        if side:
                            cur = cur + (o[1:] if e == 0 else o[::-1][1:])
                        else:
                            cur = (o[:-1] if e == 1 else o[::-1][:-1]) + cur
                        used.add(j)
                        grown = changed = True
                        break
                    if grown:
                        break
            out.append(cur)
        lines = out
    return lines


def main():
    doc = json.load(open(MAP, encoding="utf-8"))
    regions = rings_of(doc)
    all_rings = [ring for _, rings in regions.values() for ring in rings]

    labels = []
    for rid, (r, rings) in regions.items():
        if rid in ("UA-30", "UA-40"):      # Київ і Севастополь — підписані містом
            continue
        x, y = pole(rings)
        labels.append({"n": r["short"], "x": x, "y": y})

    cities = []
    for name, lat, lon, lvl in CITIES:
        x, y = project(lat, lon)
        cities.append({"name": name, "x": x, "y": y, "lvl": lvl})

    roads = {}
    if len(sys.argv) > 1:
        data = json.load(open(sys.argv[1], encoding="utf-8"))
        for e in data["elements"]:
            refs = [m.group(1) for m in (re.match(r"^М[- ]?(\d{2})$", s.strip()) for s in e.get("tags", {}).get("ref", "").split(";")) if m]
            if not refs or "geometry" not in e:
                continue
            pts = [project(g["lat"], g["lon"]) for g in e["geometry"]]
            # лише відрізки всередині України; решту ріжемо
            run = []
            for p in pts:
                if point_in(all_rings, p[0], p[1]):
                    if not run or run[-1] != p:
                        run.append(p)
                else:
                    if len(run) >= 2:
                        roads.setdefault(refs[0], []).append(run)
                    run = []
            if len(run) >= 2:
                roads.setdefault(refs[0], []).append(run)
    out_roads = []
    total = 0
    for ref in sorted(roads, key=int):
        lines = []
        for l in merge(roads[ref]):
            s = dp(l, TOL_ROAD)
            length = sum(math.hypot(s[i][0] - s[i - 1][0], s[i][1] - s[i - 1][1]) for i in range(1, len(s)))
            if length < ROAD_MIN_LEN:
                continue
            flat = [c for p in s for c in p]
            lines.append(flat)
            total += len(s)
        if lines:
            out_roads.append({"ref": "М-" + ref, "lines": lines})

    doc = {"v": 1, "attr": "© OpenStreetMap contributors (дороги), Natural Earth (міста)",
           "labels": labels, "cities": cities, "roads": out_roads}
    text = json.dumps(doc, ensure_ascii=False, separators=(",", ":"))
    open(OUT, "w", encoding="utf-8").write(text)
    print(f"{OUT}: {len(text.encode('utf-8'))} байт; підписів {len(labels)}, міст {len(cities)}, трас {len(out_roads)}, точок доріг {total}")


if __name__ == "__main__":
    main()
