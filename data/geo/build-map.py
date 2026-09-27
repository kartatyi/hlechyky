"""
Збірник мапи «Де це?» — web/games/geo-map.json з Natural Earth 10m (суспільне надбання).

    python data/geo/build-map.py            # з кореня репозиторію (повним шляхом до python.exe)

Що робить (docs/games/specs/geo.md §7.4):
  1. Качає ne_10m_admin_1_states_provinces.geojson і ne_10m_rivers_lake_centerlines.geojson у %TEMP%
     (у гіт не йдуть), якщо їх там ще нема.
  2. Бере 27 одиниць України в міжнародно визнаних кордонах: adm0_a3 == UKR або iso_3166_2 «UA-…» — у NE Крим і
     Севастополь позначено RUS, тож без другої умови півострова на мапі не було б.
  3. Проєктує кожне кільце сферичним рівновеликим Альберсом (той самий, що в GeoMap.cs) у сітку 4000×2730 і
     округлює до цілих.
  4. Ріже кільця на дуги в стилі TopoJSON: спільна межа двох областей — одна дуга, тож після спрощення між
     областями не лишається щілин. Кожну дугу спрощує Дуглас — Пекер з допуском 6 одиниць (~2 км).
  5. Річки: вісім головних, лише шматки всередині країни (або на відстані до ~4 км від контуру — Дунай, Дністер і
     Прут подекуди самі є кордоном), спрощені з допуском 8.
  6. Обласні центри — зі своєї таблиці (координати з Вікіданих, 27.09.2026).
Результат — JSON без пробілів, ціль ~90 КБ, стеля 150 КБ (перевіряє тест GeoMapTests).
"""
import json, math, os, sys, tempfile, urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
OUT = os.path.join(ROOT, "web", "games", "geo-map.json")
NE = "https://raw.githubusercontent.com/nvkelso/natural-earth-vector/master/geojson/"
UA = "HlechykyGames/1.0 (https://hlechyky.pp.ua)"

W, H = 4000, 2730
PHI1, PHI2, PHI0, LAM0 = 46.0, 51.0, 48.5, 31.5
X0, Y0, K = -0.110, 0.070, 19000.0
TOL_BORDER = 2
TOL_RIVER = 3
RIVER_NEAR = 12          # на стільки одиниць (~4 км) річка може відійти за контур — прикордонні річки
RIVER_MIN_PTS = 3

_r = math.radians
N = (math.sin(_r(PHI1)) + math.sin(_r(PHI2))) / 2
C = math.cos(_r(PHI1)) ** 2 + 2 * N * math.sin(_r(PHI1))
RHO0 = math.sqrt(C - 2 * N * math.sin(_r(PHI0))) / N


def project(lat, lon):
    rho = math.sqrt(C - 2 * N * math.sin(_r(lat))) / N
    th = N * (_r(lon) - _r(LAM0))
    x = rho * math.sin(th)
    y = RHO0 - rho * math.cos(th)
    return int(round((x - X0) * K)), int(round((Y0 - y) * K))


REGIONS = {
    "UA-05": ("Вінницька область", "Вінницька"), "UA-07": ("Волинська область", "Волинська"),
    "UA-09": ("Луганська область", "Луганська"), "UA-12": ("Дніпропетровська область", "Дніпропетровська"),
    "UA-14": ("Донецька область", "Донецька"), "UA-18": ("Житомирська область", "Житомирська"),
    "UA-21": ("Закарпатська область", "Закарпатська"), "UA-23": ("Запорізька область", "Запорізька"),
    "UA-26": ("Івано-Франківська область", "Івано-Франківська"), "UA-30": ("Київ", "Київ"),
    "UA-32": ("Київська область", "Київська"), "UA-35": ("Кіровоградська область", "Кіровоградська"),
    "UA-46": ("Львівська область", "Львівська"), "UA-48": ("Миколаївська область", "Миколаївська"),
    "UA-51": ("Одеська область", "Одеська"), "UA-53": ("Полтавська область", "Полтавська"),
    "UA-56": ("Рівненська область", "Рівненська"), "UA-59": ("Сумська область", "Сумська"),
    "UA-61": ("Тернопільська область", "Тернопільська"), "UA-63": ("Харківська область", "Харківська"),
    "UA-65": ("Херсонська область", "Херсонська"), "UA-68": ("Хмельницька область", "Хмельницька"),
    "UA-71": ("Черкаська область", "Черкаська"), "UA-74": ("Чернігівська область", "Чернігівська"),
    "UA-77": ("Чернівецька область", "Чернівецька"), "UA-40": ("Севастополь", "Севастополь"),
    "UA-43": ("Автономна Республіка Крим", "Крим"),
}

RIVERS = [  # назва в NE → українська, чи головна
    ("Dnipro", "Дніпро", True), ("Dniester", "Дністер", False), ("Southern Bug", "Південний Буг", False),
    ("Desna", "Десна", False), ("Donets", "Сіверський Донець", False), ("Pripyat", "Прип'ять", False),
    ("Prut", "Прут", False), ("Danube", "Дунай", False),
]

# Острови, яких у Natural Earth 10m нема (або вони менші за допуск спрощення), — додаємо восьмикутником
# справжнього розміру: (область, назва, широта, довгота, радіус в одиницях сітки). Зміїний — ~0,6 км.
ISLANDS = [("UA-51", "Острів Зміїний", 45.2553, 30.2044, 1.8)]

CITIES = [  # обласні центри (традиційні): назва, широта, довгота, рівень підказки
    ("Київ", 50.4500, 30.5236, 1), ("Львів", 49.8420, 24.0316, 1), ("Одеса", 46.4775, 30.7326, 1),
    ("Харків", 49.9925, 36.2311, 1), ("Дніпро", 48.4675, 35.0400, 1), ("Донецьк", 48.0028, 37.8053, 1),
    ("Сімферополь", 44.9484, 34.1000, 1), ("Запоріжжя", 47.8500, 35.1175, 2), ("Луганськ", 48.5717, 39.2973, 2),
    ("Полтава", 49.5894, 34.5514, 2), ("Суми", 50.9067, 34.7992, 2), ("Чернігів", 51.4939, 31.2947, 2),
    ("Херсон", 46.6425, 32.6250, 2), ("Луцьк", 50.7478, 25.3244, 2), ("Рівне", 50.6197, 26.2514, 2),
    ("Житомир", 50.2544, 28.6578, 2), ("Вінниця", 49.2372, 28.4672, 2), ("Хмельницький", 49.4200, 27.0000, 2),
    ("Тернопіль", 49.5667, 25.6000, 2), ("Івано-Франківськ", 48.9228, 24.7106, 2), ("Ужгород", 48.6239, 22.2950, 2),
    ("Чернівці", 48.2908, 25.9344, 2), ("Черкаси", 49.4444, 32.0597, 2), ("Кропивницький", 48.5103, 32.2667, 2),
    ("Миколаїв", 46.9750, 31.9950, 2), ("Севастополь", 44.6050, 33.5225, 2),
]


def fetch(name):
    path = os.path.join(tempfile.gettempdir(), name)
    if not os.path.exists(path) or os.path.getsize(path) < 1_000_000:
        print("качаю", name)
        req = urllib.request.Request(NE + name, headers={"User-Agent": UA})
        with urllib.request.urlopen(req, timeout=120) as r, open(path + ".tmp", "wb") as f:
            f.write(r.read())
        os.replace(path + ".tmp", path)
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def dedupe(pts):
    out = []
    for p in pts:
        if not out or out[-1] != p:
            out.append(p)
    if len(out) > 1 and out[0] == out[-1]:
        out.pop()
    return out


def dp(pts, tol):
    """Дуглас — Пекер на відкритій ламаній; кінці лишаються на місці."""
    if len(pts) < 3:
        return list(pts)
    keep = [False] * len(pts)
    keep[0] = keep[-1] = True
    stack = [(0, len(pts) - 1)]
    t2 = tol * tol
    while stack:
        a, b = stack.pop()
        ax, ay = pts[a]
        bx, by = pts[b]
        dx, dy = bx - ax, by - ay
        l2 = dx * dx + dy * dy
        best, bi = -1.0, -1
        for i in range(a + 1, b):
            px, py = pts[i]
            if l2 == 0:
                d2 = (px - ax) ** 2 + (py - ay) ** 2
            else:
                cr = (px - ax) * dy - (py - ay) * dx
                d2 = cr * cr / l2
            if d2 > best:
                best, bi = d2, i
        if best > t2:
            keep[bi] = True
            stack.append((a, bi))
            stack.append((bi, b))
    return [p for p, k in zip(pts, keep) if k]


def dp_closed(pts, tol):
    """Замкнена дуга (кільце без вузлів): ріжемо в найдальшій від початку точці й спрощуємо половинки."""
    ring = pts[:-1] if pts[0] == pts[-1] else pts[:]
    if len(ring) < 4:
        return ring + [ring[0]]
    x0, y0 = ring[0]
    far = max(range(len(ring)), key=lambda i: (ring[i][0] - x0) ** 2 + (ring[i][1] - y0) ** 2)
    a = dp(ring[: far + 1], tol)
    b = dp(ring[far:] + [ring[0]], tol)
    return a + b[1:]


def main():
    admin = fetch("ne_10m_admin_1_states_provinces.geojson")
    rivers_src = fetch("ne_10m_rivers_lake_centerlines.geojson")

    units = []
    for f in admin["features"]:
        p = f["properties"]
        iso = p.get("iso_3166_2") or ""
        if p.get("adm0_a3") != "UKR" and not iso.startswith("UA-"):
            continue
        g = f["geometry"]
        polys = g["coordinates"] if g["type"] == "MultiPolygon" else [g["coordinates"]]
        rings = []
        for poly in polys:
            for ring in poly:
                pts = dedupe([project(lat, lon) for lon, lat in ring])
                rings.append(pts)
        good = [r for r in rings if len(r) >= 4]
        units.append((iso, good if good else rings))
    units.sort(key=lambda u: u[0])
    assert len(units) == 27, f"одиниць {len(units)}, а має бути 27"
    for iso, _ in units:
        assert iso in REGIONS, iso

    # ---- дуги: ребро → множина кілець; вузол там, де множина ребер зліва й справа різна
    all_rings = []          # (unit_index, points)
    for ui, (_, rings) in enumerate(units):
        for r in rings:
            all_rings.append((ui, r))
    edge_rings = {}
    for ri, (_, pts) in enumerate(all_rings):
        n = len(pts)
        for i in range(n):
            a, b = pts[i], pts[(i + 1) % n]
            key = (a, b) if a < b else (b, a)
            edge_rings.setdefault(key, set()).add(ri)

    def eset(pts, i):
        n = len(pts)
        a, b = pts[i % n], pts[(i + 1) % n]
        return frozenset(edge_rings[(a, b) if a < b else (b, a)])

    arcs = []               # список точок (не спрощених)
    arc_index = {}          # tuple(points) → індекс
    ring_arcs = []          # на кожне кільце — список знакових індексів (0-based з ознакою напрямку)

    def add_arc(seq):
        t = tuple(seq)
        if t in arc_index:
            return (arc_index[t], False)
        rt = tuple(reversed(seq))
        if rt in arc_index:
            return (arc_index[rt], True)
        arc_index[t] = len(arcs)
        arcs.append(list(seq))
        return (len(arcs) - 1, False)

    for ri, (_, pts) in enumerate(all_rings):
        n = len(pts)
        nodes = [i for i in range(n) if eset(pts, i - 1) != eset(pts, i)]
        if not nodes:
            # кільце без вузлів (острів, Київ усередині області): одна замкнена дуга з канонічним початком
            start = min(range(n), key=lambda i: pts[i])
            seq = pts[start:] + pts[:start]
            # напрям канонічний теж: інакше кільце Києва й «дірка» в області дали б дві різні дуги
            fwd = seq + [seq[0]]
            back = [seq[0]] + list(reversed(seq[1:])) + [seq[0]]
            if tuple(back) < tuple(fwd):
                idx, _ = add_arc(back)
                ring_arcs.append([(idx, True)])
            else:
                idx, rev = add_arc(fwd)
                ring_arcs.append([(idx, rev)])
            continue
        refs = []
        for k in range(len(nodes)):
            a = nodes[k]
            b = nodes[(k + 1) % len(nodes)]
            if b <= a:
                b += n
            seq = [pts[i % n] for i in range(a, b + 1)]
            refs.append(add_arc(seq))
        ring_arcs.append(refs)

    raw_pts = sum(len(a) for a in arcs)
    simple = []
    for a in arcs:
        s = dp_closed(a, TOL_BORDER) if a[0] == a[-1] else dp(a, TOL_BORDER)
        simple.append(s)
    print(f"дуг: {len(arcs)}, точок до спрощення: {raw_pts}, після: {sum(len(s) for s in simple)}")

    def ring_points(refs):
        out = []
        for idx, rev in refs:
            s = simple[idx][::-1] if rev else simple[idx]
            if out:
                s = s[1:]
            out.extend(s)
        return out

    # кільця, що після спрощення вироджуються (крихітні острівці), — геть, якщо в області є інші
    regions = []
    used = set()
    for ui, (iso, _) in enumerate(units):
        mine = [ring_arcs[ri] for ri, (u, _) in enumerate(all_rings) if u == ui]
        keep = [refs for refs in mine if len(set(ring_points(refs))) >= 3]
        if not keep:
            keep = mine
        for refs in keep:
            for idx, _ in refs:
                used.add(idx)
        regions.append((iso, keep))
    remap = {old: new for new, old in enumerate(sorted(used))}
    out_arcs = [simple[old] for old in sorted(used)]

    # острови поза джерелом: окрема замкнена дуга без спрощення, одне кільце своєї області
    extra = {}
    for iso, _name, lat, lon, rad in ISLANDS:
        cx, cy = project(lat, lon)
        ring = dedupe([(int(round(cx + rad * math.cos(a * math.pi / 4))), int(round(cy + rad * math.sin(a * math.pi / 4)))) for a in range(8)])
        out_arcs.append(ring + [ring[0]])
        extra.setdefault(iso, []).append([len(out_arcs)])

    # ---- рахунок посилань: контур країни — дуги, на які посилається рівно одне кільце
    refcount = {}
    for _, rings in regions:
        for refs in rings:
            for idx, _ in refs:
                refcount[remap[idx]] = refcount.get(remap[idx], 0) + 1
    for rings in extra.values():
        for ring in rings:
            refcount[ring[0] - 1] = 1

    # ---- річки: точка всередині об'єднання областей (парний-непарний) або біля контуру
    polys = [ring_points(refs) for _, rings in regions for refs in rings] + [out_arcs[r[0] - 1] for rings in extra.values() for r in rings]
    band = 16
    bands = {}
    for pts in polys:
        n = len(pts)
        for i in range(n):
            (x1, y1), (x2, y2) = pts[i], pts[(i + 1) % n]
            if y1 == y2:
                continue
            for b in range(min(y1, y2) // band, max(y1, y2) // band + 1):
                bands.setdefault(b, []).append((x1, y1, x2, y2))

    def inside(x, y):
        c = False
        for x1, y1, x2, y2 in bands.get(int(y) // band, ()):
            if (y1 > y) != (y2 > y):
                xi = x1 + (y - y1) * (x2 - x1) / (y2 - y1)
                if x < xi:
                    c = not c
        return c

    outline = []
    for idx, cnt in refcount.items():
        if cnt == 1:
            a = out_arcs[idx]
            for i in range(len(a) - 1):
                outline.append((a[i], a[i + 1]))
    cell = 32
    grid = {}
    for (p, q) in outline:
        for gx in range(min(p[0], q[0]) // cell - 1, max(p[0], q[0]) // cell + 2):
            for gy in range(min(p[1], q[1]) // cell - 1, max(p[1], q[1]) // cell + 2):
                grid.setdefault((gx, gy), []).append((p, q))

    def near(x, y, d):
        d2 = d * d
        for (p, q) in grid.get((int(x) // cell, int(y) // cell), ()):
            dx, dy = q[0] - p[0], q[1] - p[1]
            l2 = dx * dx + dy * dy
            t = 0 if l2 == 0 else max(0, min(1, ((x - p[0]) * dx + (y - p[1]) * dy) / l2))
            ex, ey = p[0] + t * dx - x, p[1] + t * dy - y
            if ex * ex + ey * ey <= d2:
                return True
        return False

    def ok(p):
        return 0 <= p[0] <= W and 0 <= p[1] <= H and (inside(p[0] + 0.5, p[1] + 0.5) or near(p[0], p[1], RIVER_NEAR))

    rivers = []
    for ne_name, uk, big in RIVERS:
        lines = []
        for f in rivers_src["features"]:
            pr = f["properties"]
            if ne_name not in (pr.get("name"), pr.get("name_en")):
                continue
            if pr.get("featurecla") not in ("River", "Lake Centerline"):
                continue
            g = f["geometry"]
            parts = g["coordinates"] if g["type"] == "MultiLineString" else [g["coordinates"]]
            for part in parts:
                pts = []
                for lon, lat in part:
                    p = project(lat, lon)
                    if pts and pts[-1] == p:
                        continue
                    pts.append(p)
                cur = []
                for i, p in enumerate(pts):
                    if ok(p):
                        cur.append(p)
                    else:
                        if len(cur) >= 2:
                            lines.append(cur)
                        cur = []
                if len(cur) >= 2:
                    lines.append(cur)
        lines = [dp(l, TOL_RIVER) for l in lines]
        lines = [l for l in lines if len(l) >= 2 and math.dist(l[0], l[-1]) + sum(math.dist(l[i], l[i + 1]) for i in range(len(l) - 1)) > 40]
        if lines:
            rivers.append({"name": uk, "big": big, "lines": [[c for p in l for c in p] for l in lines]})
        print(f"річка {uk}: шматків {len(lines)}, точок {sum(len(l) for l in lines)}")

    cities = []
    for name, lat, lon, lvl in CITIES:
        x, y = project(lat, lon)
        cities.append({"name": name, "x": x, "y": y, "lvl": lvl})

    doc = {
        "v": 1, "w": W, "h": H,
        "proj": {"phi1": PHI1, "phi2": PHI2, "phi0": PHI0, "lam0": LAM0, "x0": X0, "y0": Y0, "k": K},
        "arcs": [[c for p in a for c in p] for a in out_arcs],
        "regions": [
            {"id": iso, "name": REGIONS[iso][0], "short": REGIONS[iso][1],
             "rings": [[(remap[idx] + 1) * (-1 if rev else 1) for idx, rev in refs] for refs in rings] + extra.get(iso, [])}
            for iso, rings in regions
        ],
        "rivers": rivers,
        "cities": cities,
    }
    text = json.dumps(doc, ensure_ascii=False, separators=(",", ":"))
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    allx = [p[0] for a in out_arcs for p in a]
    ally = [p[1] for a in out_arcs for p in a]
    print(f"записав {OUT}: {len(text.encode('utf-8'))} байт; дуг {len(out_arcs)}, точок {sum(len(a) for a in out_arcs)}; "
          f"контурних дуг {sum(1 for c in refcount.values() if c == 1)}; X {min(allx)}..{max(allx)}, Y {min(ally)}..{max(ally)}")


if __name__ == "__main__":
    sys.exit(main())
