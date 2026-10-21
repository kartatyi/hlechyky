"""
Детальний шар мапи «🚨 Тривога» — web/static/tryvoga-detail.json (docs/tryvoga.md, «Мапа районів»), 21.10.2026.

    python data/geo/build-tryvoga-detail.py      # з кореня репозиторію, повним шляхом до python.exe

Лягає поверх web/static/tryvoga-map.json (та сама проєкція й сітка 4000×2730, що в «Де це?» і build-tryvoga.py), але
окремим файлом: основна мапа з'являється одразу, а деталі клієнт довантажує. Мапа лишається своєю — браузер нічого
не тягне назовні, усе зібрано тут.

Що в шарі:
  • міста й смт від ~10 тис. людей і всі райцентри — рівнями: 1 — обласні центри, 2 — від 100 тис., 3 — від 30 тис.
    і райцентри, 4 — решта (клієнт показує їх з наближенням і без накладань підписів);
  • водосховища, великі озера й лимани — полігонами (від WATER_MIN_KM2);
  • головні річки (RIVERS) — лініями, Дніпро товще;
  • траси М — з web/games/geo-detail.json (теж OSM, збирав build-detail.py); підписи областей — звідти ж.

Джерело — OpenStreetMap (© OpenStreetMap contributors, ODbL; атрибуція — у кутку мапи й під нею). Три запити
Overpass (нижче) качаються в %TEMP%/tryvoga-osm/ (у гіт не йдуть); є файл — не качаємо вдруге, свіже — стерти теку.
Межа України для обрізання — райони NEPTUN з tryvoga-map.json. Лінії — дельтами (перша точка абсолютна, далі
різниці), як дуги основної мапи; спрощення — Дуглас — Пекер.
"""
import importlib.util
import json
import math
import os
import re
import sys
import tempfile
import time
import urllib.parse
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
BASE = os.path.join(ROOT, "web", "static", "tryvoga-map.json")
GEO_DETAIL = os.path.join(ROOT, "web", "games", "geo-detail.json")
OUT = os.path.join(ROOT, "web", "static", "tryvoga-detail.json")
CACHE = os.path.join(tempfile.gettempdir(), "tryvoga-osm")
UA = "Hlechyky/1.0 (https://hlechyky.pp.ua)"
ENDPOINTS = ["https://overpass-api.de/api/interpreter", "https://overpass.private.coffee/api/interpreter"]
BBOX = "44.0,21.9,52.5,40.4"

TOL_WATER = 1.6        # одиниць сітки (~0,5 км)
TOL_RIVER = 2.0
WATER_MIN_KM2 = 18     # Світязь (~27 км²) ще є, ставки — ні
RIVER_MIN_LEN = 12     # коротші уривки (~4 км) — геть
RIVER_NEAR = 12        # прикордонна річка може відійти за контур на стільки одиниць (~4 км)
POP_MIN = 10_000
KM_PER_UNIT = None     # з proj основної мапи

_spec = importlib.util.spec_from_file_location("buildmap", os.path.join(HERE, "build-map.py"))
bm = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(bm)
project = bm.project

RIVERS = ["Дніпро", "Дністер", "Південний Буг", "Десна", "Сіверський Донець", "Прип'ять", "Тетерів", "Рось",
          "Інгулець", "Сула", "Псел", "Ворскла", "Горинь", "Случ", "Інгул", "Західний Буг", "Прут", "Дунай", "Тиса"]
BIG = {"Дніпро", "Дунай"}

Q_PLACES = f"""[out:json][timeout:300];
node["place"~"^(city|town)$"]({BBOX});
out body qt;
rel["boundary"="administrative"]["admin_level"="6"]({BBOX});
node(r:"admin_centre");
out ids qt;"""

# Лише названі водосховища, озера й лимани (без «,i» і без ставків: важкий запит Overpass віддає 502).
_WATER_RE = "[Вв]одосховище|[Оо]зеро|[Лл]иман|Світязь|Ялпуг|Кугурлуй|Кагул|Сасик|Сиваш"
Q_WATER = f"""[out:json][timeout:300];
(
  way["natural"="water"]["water"~"^(reservoir|lake|lagoon)$"]["name"~"{_WATER_RE}"]({BBOX});
  rel["natural"="water"]["water"~"^(reservoir|lake|lagoon)$"]["name"~"{_WATER_RE}"]({BBOX});
  rel["natural"="water"][!"water"]["name"~"{_WATER_RE}"]({BBOX});
);
out geom qt;"""

_RIV_RE = "^(" + "|".join(n.replace("'", ".") for n in RIVERS) + ")$"
Q_RIVERS = f"""[out:json][timeout:300];
(
  rel["waterway"="river"]["name"~"{_RIV_RE}"]({BBOX});
  rel["waterway"="river"]["name:uk"~"{_RIV_RE}"]({BBOX});
);
out geom qt;"""


def overpass(name, query):
    os.makedirs(CACHE, exist_ok=True)
    path = os.path.join(CACHE, name + ".json")
    if os.path.exists(path) and os.path.getsize(path) > 1000:
        return json.load(open(path, encoding="utf-8"))
    body = urllib.parse.urlencode({"data": query}).encode()
    last = None
    for url in ENDPOINTS:
        for attempt in range(2):
            try:
                print("Overpass:", name, url)
                req = urllib.request.Request(url, data=body, headers={"User-Agent": UA})
                with urllib.request.urlopen(req, timeout=400) as r:
                    raw = r.read()
                doc = json.loads(raw.decode("utf-8"))
                if "elements" not in doc:
                    raise RuntimeError("без elements: " + str(doc.get("remark", ""))[:200])
                open(path + ".tmp", "wb").write(raw)
                os.replace(path + ".tmp", path)
                return doc
            except Exception as e:      # noqa: BLE001 — пробуємо інший сервер
                last = e
                print("  не вийшло:", e)
                time.sleep(5)
    raise RuntimeError(f"Overpass {name}: {last}")


# ---------------------------------------------------------------------------------------------------------------
# межа України — з районів NEPTUN основної мапи
# ---------------------------------------------------------------------------------------------------------------

def load_units():
    doc = json.load(open(BASE, encoding="utf-8"))
    arcs = []
    for d in doc["arcs"]:
        pts = [(d[0], d[1])]
        for i in range(2, len(d), 2):
            pts.append((pts[-1][0] + d[i], pts[-1][1] + d[i + 1]))
        arcs.append(pts)
    units = []
    for u in doc["units"]:
        rings = []
        for ring in u["rings"]:
            pts = []
            for idx in ring:
                a = arcs[abs(idx) - 1]
                p = a if idx > 0 else a[::-1]
                pts.extend(p if not pts else p[1:])
            rings.append(pts)
        xs = [p[0] for r in rings for p in r]
        ys = [p[1] for r in rings for p in r]
        units.append((min(xs), min(ys), max(xs), max(ys), rings))
    return doc, units


def in_ring(ring, x, y):
    c = False
    n = len(ring)
    for i in range(n):
        x1, y1 = ring[i]
        x2, y2 = ring[(i + 1) % n]
        if (y1 > y) != (y2 > y) and x < x1 + (y - y1) * (x2 - x1) / (y2 - y1):
            c = not c
    return c


def make_inside(units):
    def inside(x, y):
        for x0, y0, x1, y1, rings in units:
            if x < x0 or x > x1 or y < y0 or y > y1:
                continue
            c = False
            for r in rings:
                if in_ring(r, x, y):
                    c = not c
            if c:
                return True
        return False

    def near(x, y, d=RIVER_NEAR):
        if inside(x, y):
            return True
        return any(inside(x + dx * d, y + dy * d) for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1), (.7, .7), (-.7, .7), (.7, -.7), (-.7, -.7)))

    return inside, near


# ---------------------------------------------------------------------------------------------------------------
# геометрія
# ---------------------------------------------------------------------------------------------------------------

def merge(lines):
    """Склеїти шматки кінець-у-кінець (OSM ріже річку й берег на сотні way)."""
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
            while grown and cur[0] != cur[-1]:
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


def area(ring):
    s = 0.0
    for i in range(len(ring)):
        x1, y1 = ring[i - 1]
        x2, y2 = ring[i]
        s += x1 * y2 - x2 * y1
    return abs(s) / 2


def length(pts):
    return sum(math.hypot(pts[i][0] - pts[i - 1][0], pts[i][1] - pts[i - 1][1]) for i in range(1, len(pts)))


def delta(pts):
    out = [pts[0][0], pts[0][1]]
    for i in range(1, len(pts)):
        out += [pts[i][0] - pts[i - 1][0], pts[i][1] - pts[i - 1][1]]
    return out


def geom_pts(geometry):
    return [project(g["lat"], g["lon"]) for g in geometry if g]


def norm(s):
    """Апостроф один на всю мапу: в OSM трапляються ’ і ʼ, у нас скрізь '."""
    return s.replace("’", "'").replace("ʼ", "'").strip()


def uk_name(tags):
    return norm(tags.get("name:uk") or tags.get("name") or "")


def river_name(tags):
    """Міжнародні річки в OSM звуться «Dnieper», «Bug / Західний Буг / Заходні Буг» — шукаємо свою назву серед частин."""
    for n in [tags.get("name:uk", "")] + tags.get("name", "").split(" / "):
        n = norm(n)
        if n in RIVERS:
            return n
    return None


# ---------------------------------------------------------------------------------------------------------------
# шари
# ---------------------------------------------------------------------------------------------------------------

def build_places(inside, base):
    doc = overpass("places", Q_PLACES)
    centres = {e["id"] for e in doc["elements"] if e["type"] == "node" and not e.get("tags")}
    # обласний центр — за назвою І місцем: Миколаїв на Львівщині чи Чернівці на Вінниччині — не вони
    capital_at = {t["name"]: (t["x"], t["y"]) for t in base.get("towns", [])}
    capitals = set(capital_at)
    found = []
    for e in doc["elements"]:
        tags = e.get("tags")
        if e["type"] != "node" or not tags or "lat" not in e:
            continue
        name = uk_name(tags)
        if not name:
            continue
        m = re.match(r"\d+", re.sub(r"[\s ,.]", "", tags.get("population", "")))
        pop = int(m.group(0)) if m else 0
        centre = e["id"] in centres
        x, y = project(e["lat"], e["lon"])
        cap = capital_at.get(name)
        if cap and abs(cap[0] - x) + abs(cap[1] - y) < 60:
            lvl = 1
        elif pop >= 100_000:
            lvl = 2
        elif pop >= 30_000 or centre:
            lvl = 3
        elif pop >= POP_MIN or (tags.get("place") == "city" and not pop):
            lvl = 4
        else:
            continue
        if not inside(x, y):
            continue
        found.append((lvl, -pop, name, x, y))
    found.sort()
    out = []
    for lvl, npop, name, x, y in found:
        # той самий населений пункт двічі (вузол міста й вузол смт поруч) — лишаємо важливіший
        if any(p["n"] == name and abs(p["x"] - x) + abs(p["y"] - y) < 30 for p in out):
            continue
        out.append({"n": name, "x": x, "y": y, "l": lvl})
    have = {p["n"] for p in out if p["l"] == 1}
    missing = sorted(capitals - have)
    if missing:
        print("  обласних центрів нема в OSM-вибірці, беру з основної мапи:", ", ".join(missing))
        for t in base.get("towns", []):
            if t["name"] in missing:
                out.insert(0, {"n": t["name"], "x": t["x"], "y": t["y"], "l": 1})
    return out


def build_water(inside):
    doc = overpass("water", Q_WATER)
    km2 = KM_PER_UNIT ** 2
    seen = set()
    out = []
    for e in doc["elements"]:
        tags = e.get("tags", {})
        name = uk_name(tags)
        if e["type"] == "way":
            rings = [geom_pts(e.get("geometry", []))]
        elif e["type"] == "relation":
            outer = [geom_pts(m.get("geometry", [])) for m in e.get("members", [])
                     if m.get("type") == "way" and m.get("role", "outer") in ("outer", "") and m.get("geometry")]
            rings = merge(outer)
        else:
            continue
        good = []
        for r in rings:
            r = bm.dedupe(r)
            if len(r) < 4 or area(r) * km2 < WATER_MIN_KM2:
                continue
            ins = sum(1 for p in r[:: max(1, len(r) // 40)] if inside(*p))
            if ins * 2 < len(r[:: max(1, len(r) // 40)]):
                continue                       # здебільшого за кордоном
            s = bm.dp_closed(r + [r[0]], TOL_WATER)
            s = bm.dedupe(s)
            if len(s) >= 4:
                good.append(s)
        if not good:
            continue
        key = (name, round(good[0][0][0] / 20), round(good[0][0][1] / 20))
        if key in seen:
            continue
        seen.add(key)
        out.append({"n": name, "km2": round(sum(area(r) for r in good) * km2), "rings": good})
    out.sort(key=lambda w: -w["km2"])
    return out


def build_rivers(near):
    by = {}

    def take(doc):
        for e in doc["elements"]:
            name = river_name(e.get("tags", {}))
            if not name:
                continue
            if e["type"] == "relation":
                by.setdefault(name, []).extend(
                    geom_pts(m["geometry"]) for m in e.get("members", [])
                    if m.get("type") == "way" and m.get("role", "") in ("", "main_stream") and m.get("geometry"))
            elif e["type"] == "way" and e.get("geometry"):
                by.setdefault(name, []).append(geom_pts(e["geometry"]))

    take(overpass("rivers", Q_RIVERS))
    # Котрих нема відношенням (чи назва вигляду «Прыпяць / Прип'ять») — шукаємо ще раз ширше, лініями теж.
    missing = [n for n in RIVERS if n not in by]
    if missing:
        rx = "|".join(n.replace("'", ".") for n in missing)
        take(overpass("rivers-more-" + str(len(missing)), f"""[out:json][timeout:300];
(
  rel["waterway"="river"]["name"~"{rx}"]({BBOX});
  rel["waterway"="river"]["name:uk"~"{rx}"]({BBOX});
  way["waterway"="river"]["name"~"{rx}"]({BBOX});
  way["waterway"="river"]["name:uk"~"{rx}"]({BBOX});
);
out geom qt;"""))
    out = []
    for name in RIVERS:
        if name not in by:
            print("  річки нема в OSM-вибірці:", name)
            continue
        lines = []
        for l in merge(by[name]):
            s = bm.dp(bm.dedupe(l) or l, TOL_RIVER)
            run = []
            for p in s:
                if near(*p):
                    run.append(p)
                else:
                    if len(run) >= 2 and length(run) >= RIVER_MIN_LEN:
                        lines.append(run)
                    run = []
            if len(run) >= 2 and length(run) >= RIVER_MIN_LEN:
                lines.append(run)
        if lines:
            out.append({"n": name, "big": name in BIG, "lines": lines})
    return out


Q_ROADS = f"""[out:json][timeout:300];
way["highway"~"^(motorway|trunk|primary|motorway_link|trunk_link)$"]["ref"~"М"]({BBOX});
out geom qt;"""


def build_roads(inside):
    """Траси М-01…М-30. Шар «Де це?» порваний на уривки, тож збираємо тут: склеїти шматки за номером, спростити,
    а другу смугу дороги (дві паралельні лінії за 20 м — на нашій сітці одна) викинути сіткою зайнятих клітинок."""
    doc = overpass("roads", Q_ROADS)
    by = {}
    for e in doc["elements"]:
        if e["type"] != "way" or not e.get("geometry"):
            continue
        refs = [m.group(1) for m in (re.match(r"^М[- ]?(\d{2})\b", s.strip()) for s in e.get("tags", {}).get("ref", "").split(";")) if m]
        if refs:
            by.setdefault(refs[0], []).append(bm.dedupe(geom_pts(e["geometry"])) or geom_pts(e["geometry"]))
    CELL = 3
    taken = set()

    def cells(line, step=1.5):
        out = []
        for i in range(1, len(line)):
            (x1, y1), (x2, y2) = line[i - 1], line[i]
            n = max(1, int(math.hypot(x2 - x1, y2 - y1) / step))
            for k in range(n + 1):
                out.append((int((x1 + (x2 - x1) * k / n) // CELL), int((y1 + (y2 - y1) * k / n) // CELL)))
        return out

    def covered(c):
        return any((c[0] + dx, c[1] + dy) in taken for dx in (-1, 0, 1) for dy in (-1, 0, 1))

    out = []
    for ref in sorted(by, key=int):
        lines = []
        for l in sorted(merge(by[ref]), key=lambda l: -length(l)):
            s = bm.dp(l, 2)
            # лише в Україні
            run, parts = [], []
            for p in s:
                if inside(*p):
                    run.append(p)
                else:
                    if len(run) >= 2:
                        parts.append(run)
                    run = []
            if len(run) >= 2:
                parts.append(run)
            for part in parts:
                cs = cells(part)
                if not cs or length(part) < 3:
                    continue
                if sum(1 for c in cs if covered(c)) >= 0.8 * len(cs):
                    continue                      # друга смуга чи з'їзд уздовж уже взятої лінії
                taken.update(cs)
                lines.append(part)
        if lines:
            out.append({"ref": "М-" + ref, "lines": lines})
    return out


def main():
    global KM_PER_UNIT
    sys.stdout.reconfigure(encoding="utf-8")     # консоль Windows — cp1252, а з -I PYTHONIOENCODING не діє
    base, units = load_units()
    KM_PER_UNIT = 6371 / base["proj"]["k"]
    inside, near = make_inside(units)
    geo = json.load(open(GEO_DETAIL, encoding="utf-8"))

    places = build_places(inside, base)
    water = build_water(inside)
    rivers = build_rivers(near)
    roads = [{"ref": r["ref"], "lines": [delta(l) for l in r["lines"]]} for r in build_roads(inside)]

    doc = {
        "v": 1,
        "attr": "© OpenStreetMap contributors",
        "labels": geo.get("labels", []),
        "places": places,
        "water": [{"n": w["n"], "rings": [delta(r) for r in w["rings"]]} for w in water],
        "rivers": [{"n": r["n"], "big": r["big"], "lines": [delta(l) for l in r["lines"]]} for r in rivers],
        "roads": roads,
    }
    text = json.dumps(doc, ensure_ascii=False, separators=(",", ":"))
    open(OUT, "w", encoding="utf-8", newline="\n").write(text)
    lv = [sum(1 for p in places if p["l"] == i) for i in range(1, 5)]
    print(f"{OUT}: {len(text.encode('utf-8'))} байт")
    print(f"  міст {len(places)} (рівні 1–4: {lv}), водойм {len(water)}: "
          + ", ".join(f"{w['n']} {w['km2']}" for w in water[:14]))
    print(f"  річок {len(rivers)}: " + ", ".join(f"{r['n']} ({sum(len(l) for l in r['lines'])})" for r in rivers))
    print(f"  трас {len(roads)}, підписів областей {len(doc['labels'])}")


if __name__ == "__main__":
    main()
