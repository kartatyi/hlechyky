"""
Детальний шар мапи «🚨 Тривога» — web/static/tryvoga-detail.json (docs/tryvoga.md, «Мапа районів»), 22.10.2026.

    python data/geo/build-tryvoga-detail.py      # з кореня репозиторію, повним шляхом до python.exe

Лягає поверх web/static/tryvoga-map.json (та сама проєкція й сітка 4000×2730, що в «Де це?» і build-tryvoga.py), але
окремим файлом: основна мапа з'являється одразу, а деталі клієнт довантажує. Мапа своя — браузер нічого не тягне
назовні, усе зібрано тут.

Джерела — без умов про посилання (користувач: «забагато посилань»):
  • Natural Earth 10m, суспільне надбання — річки (основний набір і європейський додатковий) й водойми (озера,
    водосховища, лимани). Каховського водосховища нема з 06.06.2023 — його викидаємо, хоч у NE воно ще є;
  • Вікідані, CC0 — міста, смт і селища України з населенням і координатами (SPARQL нижче), райцентри — столиці
    (P36) чинних районів (Q1267632 без дати скасування); назви — українські звідти ж.
Трас нема свідомо: лінії доріг сперечалися зі слідами й курсами цілей.

Що в шарі:
  • міста рівнями: 1 — обласні центри, 2 — від 100 тис., 3 — від 30 тис. і райцентри, 4 — від 10 тис. (і міста без
    даних про населення); клієнт показує їх з наближенням і без накладань підписів;
  • водойми від WATER_MIN_KM2 — полігонами;
  • річки трьома класами: 0 — Дніпро й Дунай, 1 — головні (MAIN), 2 — решта (клієнт малює з наближенням);
  • підписи областей — з web/games/geo-detail.json (центри областей «Де це?», Natural Earth).

Файли NE качає build-map.py у %TEMP% (у гіт не йдуть), відповідь Вікіданих — у %TEMP%/tryvoga-src/. Межа України для
обрізання — райони NEPTUN з tryvoga-map.json. Лінії — дельтами, спрощення — Дуглас — Пекер.
"""
import importlib.util
import json
import math
import os
import re
import sys
import tempfile
import urllib.parse
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
BASE = os.path.join(ROOT, "web", "static", "tryvoga-map.json")
GEO_DETAIL = os.path.join(ROOT, "web", "games", "geo-detail.json")
OUT = os.path.join(ROOT, "web", "static", "tryvoga-detail.json")
CACHE = os.path.join(tempfile.gettempdir(), "tryvoga-src")
UA = "Hlechyky/1.0 (https://hlechyky.pp.ua)"
WDQS = "https://query.wikidata.org/sparql"

TOL_WATER = 1.6        # одиниць сітки (~0,5 км)
TOL_RIVER = 2.0
WATER_MIN_KM2 = 8
RIVER_MIN_LEN = 12     # коротші уривки (~4 км) — геть
RIVER_NEAR = 12        # прикордонна річка може відійти за контур на стільки одиниць (~4 км)
POP_MIN = 10_000

_spec = importlib.util.spec_from_file_location("buildmap", os.path.join(HERE, "build-map.py"))
bm = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(bm)
project = bm.project

# Назви NE (англійські) → клас річки. Безіменні з європейського набору — клас 2.
BIG = {"Dnipro", "Danube", "Bratul Chillia"}
MAIN = {"Dniester", "Prut", "Donets", "Seversky Donets", "Desna", "Southern Bug", "Bug", "Zakhidnyy Buh", "Pripyat",
        "Prypyat", "Haryn", "Styr", "Ros", "Sluch", "Oskol", "Teteriv", "Synyukha", "Seym", "Uzh", "Latorytsya", "Ubort",
        "Snov"}
SKIP_WATER = ("Kakhovka",)

# Вікідані: місто України, місто обласного значення, смт України, селище України (з 2024).
Q_PLACES = """SELECT ?i (SAMPLE(?nm) AS ?name) (MAX(?p) AS ?pop) (SAMPLE(?xy) AS ?coord) (MAX(?city) AS ?isCity) (MAX(?cap) AS ?centre) WHERE {
  VALUES (?cls ?c) { (wd:Q12131624 1) (wd:Q5123999 1) (wd:Q4100864 0) (wd:Q121366249 0) }
  ?i wdt:P31 ?cls; wdt:P17 wd:Q212; wdt:P625 ?xy; rdfs:label ?nm .
  FILTER(LANG(?nm) = "uk")
  BIND(?c AS ?city)
  OPTIONAL { ?i wdt:P1082 ?p }
  OPTIONAL { ?r wdt:P36 ?i; wdt:P31 wd:Q1267632 . FILTER NOT EXISTS { ?r wdt:P576 [] } BIND(1 AS ?cap) }
} GROUP BY ?i"""


def wikidata(name, query):
    os.makedirs(CACHE, exist_ok=True)
    path = os.path.join(CACHE, name + ".json")
    if os.path.exists(path) and os.path.getsize(path) > 1000:
        return json.load(open(path, encoding="utf-8"))
    print("Вікідані:", name)
    url = WDQS + "?" + urllib.parse.urlencode({"query": query, "format": "json"})
    req = urllib.request.Request(url, headers={"User-Agent": UA, "Accept": "application/sparql-results+json"})
    with urllib.request.urlopen(req, timeout=300) as r:
        raw = r.read()
    doc = json.loads(raw.decode("utf-8"))
    open(path + ".tmp", "wb").write(raw)
    os.replace(path + ".tmp", path)
    return doc


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


def lines_of(geom):
    if not geom:
        return []
    if geom["type"] == "LineString":
        return [geom["coordinates"]]
    if geom["type"] == "MultiLineString":
        return geom["coordinates"]
    return []


def polys_of(geom):
    if not geom:
        return []
    if geom["type"] == "Polygon":
        return [geom["coordinates"]]
    if geom["type"] == "MultiPolygon":
        return geom["coordinates"]
    return []


# ---------------------------------------------------------------------------------------------------------------
# шари
# ---------------------------------------------------------------------------------------------------------------

def build_places(inside, base):
    doc = wikidata("places", Q_PLACES)
    capital_at = {t["name"]: (t["x"], t["y"]) for t in base.get("towns", [])}
    found = []
    for b in doc["results"]["bindings"]:
        name = b.get("name", {}).get("value", "").replace("’", "'").replace("ʼ", "'").strip()
        m = re.match(r"Point\(([-\d.]+) ([-\d.]+)\)", b.get("coord", {}).get("value", ""))
        if not name or not m:
            continue
        lon, lat = float(m.group(1)), float(m.group(2))
        try:
            pop = int(float(b.get("pop", {}).get("value", "0")))
        except ValueError:
            pop = 0
        city = b.get("isCity", {}).get("value") == "1"
        centre = b.get("centre", {}).get("value") == "1"
        x, y = project(lat, lon)
        cap = capital_at.get(name)
        if cap and abs(cap[0] - x) + abs(cap[1] - y) < 60:
            lvl = 1
        elif pop >= 100_000:
            lvl = 2
        elif pop >= 30_000 or centre:
            lvl = 3
        elif pop >= POP_MIN or (city and not pop):
            lvl = 4
        else:
            continue
        if not inside(x, y):
            continue
        found.append((lvl, -pop, name, x, y))
    found.sort()
    out = []
    for lvl, _, name, x, y in found:
        if any(p["n"] == name and abs(p["x"] - x) + abs(p["y"] - y) < 30 for p in out):
            continue            # той самий пункт двічі (місто й громада з тією ж назвою поруч)
        out.append({"n": name, "x": x, "y": y, "l": lvl})
    have = {p["n"] for p in out if p["l"] == 1}
    for t in base.get("towns", []):
        if t["name"] not in have:
            print("  обласного центру нема у Вікіданих-вибірці, беру з основної мапи:", t["name"])
            out.insert(0, {"n": t["name"], "x": t["x"], "y": t["y"], "l": 1})
    return out


def build_water(near):
    km2 = (6371 / bm.K) ** 2
    out = []
    for src in ("ne_10m_lakes.geojson", "ne_10m_lakes_europe.geojson"):
        for f in bm.fetch(src)["features"]:
            name = f["properties"].get("name") or ""
            if any(s in name for s in SKIP_WATER):
                continue
            for poly in polys_of(f["geometry"]):
                r = bm.dedupe([project(lat, lon) for lon, lat, *_ in poly[0]])
                if len(r) < 4 or area(r) * km2 < WATER_MIN_KM2:
                    continue
                probe = r[:: max(1, len(r) // 40)]
                if sum(1 for p in probe if near(*p, d=6)) * 2 < len(probe):
                    continue                        # здебільшого за кордоном
                s = bm.dedupe(bm.dp_closed(r + [r[0]], TOL_WATER))
                if len(s) >= 4:
                    out.append(s)
    return out


def build_rivers(near):
    out = {0: [], 1: [], 2: []}
    for src in ("ne_10m_rivers_lake_centerlines.geojson", "ne_10m_rivers_europe.geojson"):
        for f in bm.fetch(src)["features"]:
            name = f["properties"].get("name") or ""
            cls = 0 if name in BIG else 1 if name in MAIN else 2
            for line in lines_of(f["geometry"]):
                pts = bm.dedupe([project(lat, lon) for lon, lat, *_ in line])
                if len(pts) < 2:
                    continue
                s = bm.dp(pts, TOL_RIVER)
                run = []
                for p in s:
                    if near(*p):
                        run.append(p)
                    else:
                        if len(run) >= 2 and length(run) >= RIVER_MIN_LEN:
                            out[cls].append(run)
                        run = []
                if len(run) >= 2 and length(run) >= RIVER_MIN_LEN:
                    out[cls].append(run)
    return out


def main():
    sys.stdout.reconfigure(encoding="utf-8")     # консоль Windows — cp1252
    base, units = load_units()
    inside, near = make_inside(units)
    geo = json.load(open(GEO_DETAIL, encoding="utf-8"))

    places = build_places(inside, base)
    water = build_water(near)
    rivers = build_rivers(near)

    doc = {
        "v": 2,
        "src": "Міста: Вікідані (CC0). Річки й водойми: Natural Earth (суспільне надбання).",
        "labels": geo.get("labels", []),
        "places": places,
        "water": [delta(r) for r in water],
        "rivers": [{"c": c, "lines": [delta(l) for l in lines]} for c, lines in sorted(rivers.items()) if lines],
    }
    text = json.dumps(doc, ensure_ascii=False, separators=(",", ":"))
    open(OUT, "w", encoding="utf-8", newline="\n").write(text)
    lv = [sum(1 for p in places if p["l"] == i) for i in range(1, 5)]
    print(f"{OUT}: {len(text.encode('utf-8'))} байт")
    print(f"  міст {len(places)} (рівні 1–4: {lv}), водойм {len(water)}, річок (ліній за класами 0/1/2): "
          + "/".join(str(len(rivers[c])) for c in (0, 1, 2)) + f", підписів областей {len(doc['labels'])}")


if __name__ == "__main__":
    main()
