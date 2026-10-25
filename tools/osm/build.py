"""Кеш (Overpass JSON + DEM) → проміжний формат fura-osm/1 (див. FORMAT.md): граф доріг із висотами, будинки, вода,
зелень, залізниця, точки інтересу й сітка висот. Сирі теги OSM тут перекладаємо в короткі поля гри.
"""
import base64
import collections
import gzip
import json
import math
import os
import re

import numpy as np

import geom
from dem import DemSet
from fetch import dem_name
from proj import Proj

# дороги, якими їде фура (клас = значення highway)
DRIVE = {'motorway', 'motorway_link', 'trunk', 'trunk_link', 'primary', 'primary_link', 'secondary', 'secondary_link',
         'tertiary', 'tertiary_link', 'unclassified', 'residential', 'living_street', 'service', 'track', 'road', 'busway'}
FOOT = {'footway', 'path', 'pedestrian', 'steps', 'cycleway', 'bridleway'}
MAJOR = {'motorway', 'trunk', 'primary', 'secondary', 'tertiary'}
RAIL = {'rail', 'tram', 'light_rail', 'narrow_gauge', 'subway', 'disused', 'abandoned'}
WATERWAY = {'river', 'stream', 'canal', 'ditch', 'drain'}
SPEED_ZONES = {'ua:urban': 50, 'ua:rural': 90, 'ua:motorway': 130, 'ua:trunk': 110, 'ua:living_street': 20,
               'ru:urban': 60, 'ru:rural': 90}

GRID_STEP = 30.0          # крок сітки висот, м (DEM ≈ 30 м)
SMOOTH_ROAD = 50.0
EMBANK_GRADE = 0.07       # найкрутіший насип під'їзду до мосту
PMF = [(3, 3.0), (5, 5.0), (9, 8.0), (17, 10.0)]   # (вікно, клітин; поріг, м) прогресивного фільтра        # вікно згладжування висоти вздовж дороги, м
EDGE_MARGIN = 0.0         # дороги обрізаємо рівно по межі мапи


def r1(v):
    return round(float(v), 1)


def num(s):
    if s is None:
        return None
    m = re.match(r'^\s*(-?\d+(?:[.,]\d+)?)', str(s))
    return float(m.group(1).replace(',', '.')) if m else None


def speed(s):
    if not s:
        return None
    if s.lower() in SPEED_ZONES:
        return SPEED_ZONES[s.lower()]
    v = num(s)
    if v is None:
        return None
    return round(v * 1.609) if 'mph' in s else round(v)


def yes(v):
    return v is not None and v not in ('no', 'false', '0')


# ---------------------------------------------------------------- рельєф

def _filt(A, f):
    p = np.pad(A, 1, mode='edge')
    st = [p[1 + dz:1 + dz + A.shape[0], 1 + dx:1 + dx + A.shape[1]] for dz in (-1, 0, 1) for dx in (-1, 0, 1)]
    return f(np.stack(st), axis=0)


def _up2(A, shape):
    """Білінійно вдвічі: центр дрібної клітини i — це (i − 0,5) / 2 у грубих."""
    def axis(n_out, n_in):
        f = np.clip((np.arange(n_out) - 0.5) / 2, 0, n_in - 1)
        i0 = np.minimum(np.floor(f).astype(int), max(0, n_in - 2))
        return i0, np.clip(f - i0, 0, 1)
    r0, rt = axis(shape[0], A.shape[0])
    c0, ct = axis(shape[1], A.shape[1])
    r1, c1 = np.minimum(r0 + 1, A.shape[0] - 1), np.minimum(c0 + 1, A.shape[1] - 1)
    top = A[r0][:, c0] * (1 - ct) + A[r0][:, c1] * ct
    bot = A[r1][:, c0] * (1 - ct) + A[r1][:, c1] * ct
    return top * (1 - rt[:, None]) + bot * rt[:, None]


def _fill(G, known):
    """Заповнити невідомі клітини «push-pull»: піраміда середніх відомих, назад — зважене змішування з грубішим рівнем."""
    V, W = np.where(known, G, 0.0), known.astype('f8')
    levels = [(V, W)]
    while min(V.shape) > 2:
        h, w = (V.shape[0] + 1) // 2 * 2, (V.shape[1] + 1) // 2 * 2
        V = np.pad(V, ((0, h - V.shape[0]), (0, w - V.shape[1])))
        W = np.pad(W, ((0, h - W.shape[0]), (0, w - W.shape[1])))
        V = V.reshape(h // 2, 2, w // 2, 2).sum(axis=(1, 3))
        W = W.reshape(h // 2, 2, w // 2, 2).sum(axis=(1, 3))
        levels.append((V, W))
    V, W = levels[-1]
    cur = np.where(W > 0, V / np.maximum(W, 1e-9), float(G[known].mean()) if known.any() else 0.0)
    for V, W in reversed(levels[:-1]):
        up = _up2(cur, V.shape)
        k = np.minimum(W, 1.0)
        cur = (np.where(W > 0, V / np.maximum(W, 1e-9), 0.0) * k + up * (1 - k))
    return np.where(known, G, cur)


def _raster(polys, meta):
    """Полігони (кільця в метрах) → маска сітки: клітина = вузол сітки, що всередині."""
    from PIL import Image, ImageDraw
    img = Image.new('L', (meta['nx'], meta['nz']), 0)
    d = ImageDraw.Draw(img)
    for ring in polys:
        if len(ring) >= 3:
            d.polygon([((x - meta['x0']) / meta['step'], (z - meta['z0']) / meta['step']) for x, z in ring], fill=1)
    return np.asarray(img, bool)          # [iz, ix]


def height_grid(P, dem, b, wood=(), blds=()):
    """Сітка «земля» з DSM. Copernicus — модель поверхні (дахи, крони), тож:
    1) під лісом (OSM wood/forest) віднімаємо висоту крони — її міряємо самі: медіана DSM у смузі 60–120 м усередині
       лісу мінус медіана в смузі 60–120 м зовні; край маски розмитий (~60 м), щоб не було уступу;
    2) під лісом — ще закриття 3×3 і розмиття (нерівні крони, борозни просік);
    3) клітини під будинками й те, що прогресивний морфологічний фільтр (PMF) визнав не землею, заповнюємо з сусідів
       («push-pull»), і легке розмиття 3×3.
    Повертає (мета, земля, сирий DSM, висота крони)."""
    nx = int(round((b[2] - b[0]) / GRID_STEP)) + 1
    nz = int(round((b[3] - b[1]) / GRID_STEP)) + 1
    meta = {'x0': b[0], 'z0': b[1], 'step': GRID_STEP, 'nx': nx, 'nz': nz}
    xs = b[0] + np.arange(nx) * GRID_STEP
    zs = b[1] + np.arange(nz) * GRID_STEP
    X, Z = np.meshgrid(xs, zs)                    # [iz, ix]
    lat, lon = P.inv(X.ravel(), Z.ravel())
    H = dem.sample(lat, lon).reshape(nz, nx)
    G = H.astype('f8').copy()

    canopy = 0.0
    W = _raster(wood, meta)
    B = _raster(blds, meta)
    if W.any():
        e2 = W.copy()
        for _ in range(2):
            e2 = _filt(e2, np.min)
        e4 = e2.copy()
        for _ in range(2):
            e4 = _filt(e4, np.min)
        d2 = W.copy()
        for _ in range(2):
            d2 = _filt(d2, np.max)
        d4 = d2.copy()
        for _ in range(2):
            d4 = _filt(d4, np.max)
        inner = e2 & ~e4
        outer = d4 & ~d2 & ~B
        if inner.sum() > 50 and outer.sum() > 50:
            canopy = max(0.0, float(np.median(H[inner]) - np.median(H[outer])))
        soft = W.astype('f8')
        for _ in range(2):
            soft = _filt(soft, np.mean)
        G -= canopy * soft
        # усередині лісу крона нерівна, а просіки — борозни: закриття 3×3 (максимум, потім мінімум) і тричі розмиття
        F = _filt(_filt(G, np.max), np.min)
        for _ in range(3):
            F = _filt(F, np.mean)
        G = soft * F + (1 - soft) * G
    # прогресивний морфологічний фільтр (Zhang et al. 2003): відкриття вікнами 3, 5, 9, 17 клітин; що стирчить над
    # відкритим більше за поріг — не земля (лісосмуги й гаї поза полігонами OSM, квартали, ангари)
    known = ~B
    for win, dh in PMF:
        O = G.copy()
        for _ in range(win // 2):
            O = _filt(O, np.min)
        for _ in range(win // 2):
            O = _filt(O, np.max)
        known &= (G - O) <= dh
    known = ~_filt(~known, np.max)                 # і клітину довкола: край крони / даху — теж не земля
    print(f'  не земля (будинки, дерева, PMF): {100 * (1 - known.mean()):.0f} % клітин')
    G = _fill(G, known)
    G = _filt(G, np.mean)
    return meta, G.astype('f4'), H.astype('f4'), canopy


class Ground:
    def __init__(self, meta, G):
        self.m, self.G = meta, G

    def __call__(self, x, z):
        m, G = self.m, self.G
        fx = np.clip((np.asarray(x, 'f8') - m['x0']) / m['step'], 0, m['nx'] - 1.000001)
        fz = np.clip((np.asarray(z, 'f8') - m['z0']) / m['step'], 0, m['nz'] - 1.000001)
        ix, iz = np.floor(fx).astype(int), np.floor(fz).astype(int)
        u, v = fx - ix, fz - iz
        return (G[iz, ix] * (1 - u) * (1 - v) + G[iz, ix + 1] * u * (1 - v)
                + G[iz + 1, ix] * (1 - u) * v + G[iz + 1, ix + 1] * u * v)


def encode_grid(meta, G):
    y0 = math.floor(float(G.min()))
    q = np.round((G - y0) * 10).astype('<u2')     # дециметри від y0
    return {**meta, 'y0': y0, 'scale': 0.1, 'enc': 'u16le-base64', 'order': 'iz*nx+ix, iz=0 — південь (z0)',
            'data': base64.b64encode(q.tobytes()).decode('ascii')}


def road_profile(pts, ya, yb, ground, flat):
    """Висоти точок дороги: земля, згладжена ковзним середнім за відстанню і притягнута до висот вузлів на кінцях.
    flat — міст чи тунель: лінійно між кінцями."""
    p = np.asarray(pts, 'f8')
    s = np.concatenate([[0], np.cumsum(np.hypot(*np.diff(p, axis=0).T))])
    L = s[-1]
    if flat or L < 1e-6:
        t = s / L if L > 0 else np.zeros_like(s)
        return ya + (yb - ya) * t
    # дорога густо дискретизована: точки щонайменше через 10 м, щоб середнє було за відстанню, а не за кількістю
    k = max(2, int(math.ceil(L / 10)) + 1)
    ss = np.linspace(0, L, k)
    xs, zs = np.interp(ss, s, p[:, 0]), np.interp(ss, s, p[:, 1])
    ys = ground(xs, zs)
    h = SMOOTH_ROAD / 2
    cs = np.concatenate([[0], np.cumsum(ys)])
    lo = np.searchsorted(ss, s - h, 'left')
    hi = np.searchsorted(ss, s + h, 'right')
    sm = (cs[hi] - cs[lo]) / np.maximum(1, hi - lo)
    t = s / L
    return sm + (ya - sm[0]) * (1 - t) + (yb - sm[-1]) * t


# ---------------------------------------------------------------- дороги

def road_attrs(t):
    cls = t['highway']
    a = {'cls': cls}
    if t.get('name'):
        a['name'] = t['name']
    if t.get('ref'):
        a['ref'] = t['ref']
    lanes = num(t.get('lanes'))
    if lanes:
        a['lanes'] = int(lanes)
    w = num(t.get('width'))
    if w:
        a['width'] = w
    ms = speed(t.get('maxspeed'))
    if ms:
        a['maxspeed'] = ms
    if t.get('surface'):
        a['surface'] = t['surface']
    if yes(t.get('bridge')):
        a['bridge'] = t['bridge']
    if yes(t.get('tunnel')):
        a['tunnel'] = t['tunnel']
    lay = num(t.get('layer'))
    if lay:
        a['layer'] = int(lay)
    if t.get('junction') in ('roundabout', 'circular'):
        a['ring'] = True
    if cls.endswith('_link'):
        a['link'] = True
    for k in ('service', 'access', 'tracktype', 'lit', 'sidewalk'):
        if t.get(k):
            a[k] = t[k]
    ow = t.get('oneway')
    if ow in ('yes', 'true', '1') or (ow is None and (cls in ('motorway', 'motorway_link') or 'ring' in a)):
        a['oneway'] = 1
    elif ow in ('-1', 'reverse'):
        a['oneway'] = -1
    return a


def build_roads(ways, P, b, tol):
    """OSM-шляхи доріг → вершини (перехрестя, кінці) і ребра між ними; злиття через вершини степеня 2 з однаковими
    атрибутами; спрощення полілінії. Повертає (coords nid→(x,z), vertices set, edges list)."""
    xy = {}
    seqs = []
    for w in ways:
        g = w['geometry']
        x, z = P.fwd([q['lat'] for q in g], [q['lon'] for q in g])
        a = road_attrs(w['tags'])
        nids = list(w['nodes'])
        for nid, xx, zz in zip(nids, x, z):
            xy[nid] = (float(xx), float(zz))
        if a.get('oneway') == -1:
            nids.reverse()
            a['oneway'] = 1
        # обрізання по межі: лишаємо тяглі шматки вузлів усередині
        cur = []
        for nid in nids:
            if geom.inside(b, *xy[nid]):
                cur.append(nid)
            else:
                if len(cur) >= 2:
                    seqs.append((w['id'], cur, a))
                cur = []
        if len(cur) >= 2:
            seqs.append((w['id'], cur, a))

    use = collections.Counter()
    ends = set()
    for _, nids, _ in seqs:
        ends.add(nids[0])
        ends.add(nids[-1])
        for nid in set(nids):
            use[nid] += 1
        # вузол, що двічі в одному шляху (петля) — теж вершина
        for nid, c in collections.Counter(nids).items():
            if c > 1:
                ends.add(nid)
    vert = {n for n, c in use.items() if c >= 2} | ends

    # розрізати на ребра між вершинами
    edges = []
    for wid, nids, a in seqs:
        start = 0
        k = 0
        for i in range(1, len(nids)):
            if nids[i] in vert:
                part = nids[start:i + 1]
                if part[0] == part[-1]:          # петля з одною вершиною — ріжемо надвоє
                    m = len(part) // 2
                    vert.add(part[m])
                    edges.append({'id': f'w{wid}-{k}', 'nids': part[:m + 1], **a})
                    edges.append({'id': f'w{wid}-{k + 1}', 'nids': part[m:], **a})
                    k += 2
                else:
                    edges.append({'id': f'w{wid}-{k}', 'nids': part, **a})
                    k += 1
                start = i

    # однакові ребра (той самий шлях із двох джерел або дубль у даних) — лишаємо одне
    seen, uniq = set(), []
    for e in edges:
        key = (tuple(e['nids']),)
        rkey = (tuple(reversed(e['nids'])),)
        if key in seen or (rkey in seen and not e.get('oneway')):
            continue
        seen.add(key)
        uniq.append(e)
    edges = uniq

    # злиття через вершини степеня 2 з тим самим «підписом» (і однаковим напрямком для одностороннього)
    def sig(e):
        return tuple(sorted((k, v) for k, v in e.items() if k not in ('id', 'nids')))

    alive = {i: e for i, e in enumerate(edges)}
    inc = collections.defaultdict(set)
    for i, e in alive.items():
        inc[e['nids'][0]].add(i)
        inc[e['nids'][-1]].add(i)
    merged = 0
    for v in list(inc):
        ids = inc[v]
        if len(ids) != 2:
            continue
        i, j = tuple(ids)
        ei, ej = alive[i], alive[j]
        if sig(ei) != sig(ej):
            continue
        a, c = ei['nids'], ej['nids']
        if a[0] == a[-1] or c[0] == c[-1] or {a[0], a[-1]} == {c[0], c[-1]}:
            continue
        ow = ei.get('oneway')
        if a[-1] == v and c[0] == v:
            new = a + c[1:]
        elif c[-1] == v and a[0] == v:
            new = c + a[1:]
        elif not ow and a[-1] == v and c[-1] == v:
            new = a + c[::-1][1:]
        elif not ow and a[0] == v and c[0] == v:
            new = a[::-1] + c[1:]
        else:
            continue
        ei['nids'] = new
        for end in (c[0], c[-1]):
            inc[end].discard(j)
        inc[new[0]].add(i)
        inc[new[-1]].add(i)
        del inc[v]
        del alive[j]
        vert.discard(v)
        merged += 1
    edges = list(alive.values())

    # спрощення; вершини і вузли з тегами всередині не губимо
    for e in edges:
        pts = [xy[n] for n in e['nids']]
        keep = geom.simplify(pts, tol)
        e['pts'] = [pts[i] for i in keep]
        e['a'], e['b'] = f"n{e['nids'][0]}", f"n{e['nids'][-1]}"
    print(f'  дороги: {len(seqs)} шматків OSM → {len(edges)} ребер, злито {merged}, вершин {len(vert)}')
    return xy, vert, edges


def raise_bridges(edges, nodes, xy, ground, node_tags):
    """Мости: сітка висот 30 м розмиває береги, і кінець мосту падає в долину. Тож кінець мосту — не нижче за
    найвищу землю в 0–40 м за ним (берег), а проліт — не нижче за найнижчу землю під ним + габарит (6 м для довгих,
    8 % довжини для коротких). Сусідні вершини піднімаємо насипом, не крутіше за EMBANK_GRADE (Дейкстра «вниз»)."""
    import heapq
    base = {v: p[2] for v, p in nodes.items()}
    for e in edges:
        if not e.get('bridge'):
            continue
        p = np.asarray([xy[n] for n in e['nids']], 'f8')
        L = geom.length(p.tolist())
        if L < 3:
            continue
        s = np.concatenate([[0], np.cumsum(np.hypot(*np.diff(p, axis=0).T))])
        ss = np.linspace(0, L, max(2, int(L / 5) + 1))
        under = float(np.min(ground(np.interp(ss, s, p[:, 0]), np.interp(ss, s, p[:, 1]))))
        deck = under + min(6.0, 0.08 * L)
        for end, nxt, v in ((p[0], p[1], e['a']), (p[-1], p[-2], e['b'])):
            d = end - nxt
            d = d / max(1e-9, np.hypot(*d))
            bank = max(float(ground(*(end + d * t))) for t in (0, 10, 20, 30, 40))
            y = max(nodes[v][2], bank, deck)
            nodes[v][2] = round(y, 1)
    adj = collections.defaultdict(list)
    for e in edges:
        if not e.get('bridge') and not e.get('tunnel'):
            L = geom.length([xy[n] for n in e['nids']])
            adj[e['a']].append((e['b'], L))
            adj[e['b']].append((e['a'], L))
    pq = [(-nodes[v][2], v) for v in nodes if nodes[v][2] > base[v] + 0.05]
    heapq.heapify(pq)
    while pq:
        y, v = heapq.heappop(pq)
        y = -y
        if y < nodes[v][2] - 1e-6:
            continue
        for u, L in adj[v]:
            need = y - EMBANK_GRADE * L
            if need > nodes[u][2] + 0.05:
                nodes[u][2] = round(need, 1)
                heapq.heappush(pq, (-need, u))
    raised = 0
    for v, y0 in base.items():
        if nodes[v][2] > y0 + 0.05:
            node_tags.setdefault(v, []).append({'k': 'embank', 'h': round(nodes[v][2] - y0, 1)})
            raised += 1
    print(f'  мости: піднято {raised} вершин (береги, габарит, насипи ≤ {EMBANK_GRADE:.0%})')


def find_dual(edges):
    """Розділені проїзні частини: односторонні ребра класу ≥ tertiary, що йдуть поруч (3–45 м) назустріч одне одному
    з тим самим ім'ям. Позначаємо {dual: відстань між осями, pair: [ребра навпроти]} — рендер зможе зробити одну
    дорогу з розділювачем, а граф лишається чесним (кожна проїзна частина — своє ребро)."""
    cand = [e for e in edges if e.get('oneway') and e['cls'].replace('_link', '') in MAJOR
            and not e.get('ring') and not e.get('link')]
    cell = 60.0
    idx = collections.defaultdict(list)
    for ei, e in enumerate(cand):
        p = e['pts']
        for k in range(1, len(p)):
            x0, z0 = p[k - 1]
            x1, z1 = p[k]
            for cx in range(int(math.floor(min(x0, x1) / cell)), int(math.floor(max(x0, x1) / cell)) + 1):
                for cz in range(int(math.floor(min(z0, z1) / cell)), int(math.floor(max(z0, z1) / cell)) + 1):
                    idx[(cx, cz)].append((ei, k))
    pairs = 0
    for ei, e in enumerate(cand):
        p = np.asarray(e['pts'])
        s = np.concatenate([[0], np.cumsum(np.hypot(*np.diff(p, axis=0).T))])
        L = s[-1]
        if L < 30:
            continue
        hits, partners = [], collections.Counter()
        samples = np.arange(10, L - 9, 15.0) if L > 25 else [L / 2]
        for sv in samples:
            k = min(len(p) - 1, max(1, int(np.searchsorted(s, sv))))
            t = (sv - s[k - 1]) / max(1e-9, s[k] - s[k - 1])
            q = p[k - 1] + (p[k] - p[k - 1]) * t
            d = p[k] - p[k - 1]
            d = d / max(1e-9, np.hypot(*d))
            best = None
            cx, cz = int(math.floor(q[0] / cell)), int(math.floor(q[1] / cell))
            for gx in (cx - 1, cx, cx + 1):
                for gz in (cz - 1, cz, cz + 1):
                    for ej, kk in idx.get((gx, gz), ()):
                        if ej == ei:
                            continue
                        o = cand[ej]
                        if o.get('name') != e.get('name') or o['cls'] != e['cls']:
                            continue
                        a0, a1 = np.asarray(o['pts'][kk - 1]), np.asarray(o['pts'][kk])
                        dd = a1 - a0
                        ll = np.hypot(*dd)
                        if ll < 1e-6 or np.dot(dd / ll, d) > -0.8:
                            continue
                        tt = np.clip(np.dot(q - a0, dd) / (ll * ll), 0, 1)
                        dist = float(np.hypot(*(a0 + dd * tt - q)))
                        if 3 <= dist <= 45 and (best is None or dist < best[0]):
                            best = (dist, o['id'])
            if best:
                hits.append(best[0])
                partners[best[1]] += 1
        if len(hits) >= max(1, 0.5 * len(samples)):
            e['dual'] = r1(np.median(hits))
            e['pair'] = [k for k, _ in partners.most_common(4)]
            pairs += 1
    print(f'  розділені проїзні: {pairs} ребер')


# ---------------------------------------------------------------- площі й лінії

def area_kind(t):
    if t.get('natural') == 'water' or t.get('landuse') in ('reservoir', 'basin') or t.get('waterway') in ('riverbank', 'dock'):
        return 'water'
    if t.get('landuse') == 'forest' or t.get('natural') == 'wood':
        return 'wood'
    if t.get('leisure') in ('park', 'garden'):
        return 'park'
    if t.get('leisure') in ('pitch', 'stadium', 'playground'):
        return 'pitch'
    if t.get('landuse') in ('grass', 'meadow', 'recreation_ground', 'village_green', 'flowerbed') \
            or t.get('natural') in ('scrub', 'grassland', 'heath'):
        return 'grass'
    if t.get('natural') == 'wetland':
        return 'wetland'
    if t.get('landuse') == 'cemetery':
        return 'cemetery'
    if t.get('landuse') in ('farmland', 'orchard', 'allotments', 'farmyard', 'vineyard', 'plant_nursery'):
        return 'farmland'
    if t.get('amenity') == 'parking':
        return 'parking'
    if t.get('highway') == 'pedestrian' and t.get('area') == 'yes':
        return 'plaza'
    lu = t.get('landuse')
    if lu in ('residential', 'industrial', 'commercial', 'retail', 'garages', 'railway', 'construction',
              'brownfield', 'military', 'education', 'religious', 'quarry', 'landfill'):
        return lu
    return None


def ring_xy(P, geo):
    x, z = P.fwd([q['lat'] for q in geo], [q['lon'] for q in geo])
    return list(zip(x.tolist(), z.tolist()))


def polygons(els, P, b, tags_ok):
    """Площі з замкнених шляхів і мультиполігонів → [(tags, outer, [holes]), …] у метрах, обрізані межею."""
    out = []
    for e in els:
        t = e.get('tags', {})
        if not tags_ok(t):
            continue
        if e['type'] == 'way':
            if len(e['nodes']) < 4 or e['nodes'][0] != e['nodes'][-1]:
                continue
            out.append((e, t, [ring_xy(P, e['geometry'])[:-1]], []))
        elif e['type'] == 'relation' and t.get('type') == 'multipolygon':
            outers, inners = [], []
            for m in e['members']:
                if m['type'] != 'way' or not m.get('geometry'):
                    continue
                pts = ring_xy(P, m['geometry'])
                # id вузлів членів у out geom нема — зшиваємо за округленими координатами
                w = [((round(x, 2), round(z, 2)), (x, z)) for x, z in pts]
                (inners if m.get('role') == 'inner' else outers).append(w)
            out.append((e, t, geom.assemble_rings(outers), geom.assemble_rings(inners)))
    res = []
    for e, t, outers, inners in out:
        for o in outers:
            oc = geom.clip_ring(o, b)
            if len(oc) < 3 or abs(geom.area(oc)) < 1:
                continue
            hs = [geom.clip_ring(h, b) for h in inners if geom.point_in(o, *h[0])]
            res.append((e, t, oc, [h for h in hs if len(h) >= 3]))
    return res


def fix_ring(ring, tol, ccw=True):
    keep = geom.simplify(ring + [ring[0]], tol)
    r = [ring[i] for i in keep if i < len(ring)]
    if len(r) < 3:
        return None
    if (geom.area(r) > 0) != ccw:
        r.reverse()
    return [[r1(x), r1(z)] for x, z in r]


# ---------------------------------------------------------------- збирання

def build(city, cfg, cache, out_path, tol=1.5):
    src = os.path.join(cache, city, 'overpass.json')
    with open(src, encoding='utf-8') as f:
        raw = json.load(f)
    els = raw['elements']
    P = Proj(*cfg['center'])
    s, w, n, e = cfg['bbox']
    xs, zs = P.fwd([s, s, n, n], [w, e, w, e])
    # прямокутник, що цілком у bbox (у поперечному Меркаторі сторони bbox злегка криві), з кроком 10 м
    b = (math.ceil(max(xs[0], xs[2]) / 10) * 10, math.ceil(max(zs[0], zs[1]) / 10) * 10,
         math.floor(min(xs[1], xs[3]) / 10) * 10, math.floor(min(zs[2], zs[3]) / 10) * 10)
    print(f'  межі: x {b[0]}…{b[2]}, z {b[1]}…{b[3]} ({(b[2] - b[0]) / 1000:.1f} × {(b[3] - b[1]) / 1000:.1f} км)')

    dem_paths = [os.path.join(cache, 'dem', dem_name(la, lo) + '.tif') for la, lo in cfg['dem_tiles']]
    dem = DemSet(dem_paths)
    bld_polys = polygons(els, P, b, lambda t: yes(t.get('building')))
    wood = [o for _, t, o, _ in polygons(els, P, b, lambda t: area_kind(t) == 'wood')]
    gmeta, G, _, canopy = height_grid(P, dem, b, wood, [o for _, _, o, _ in bld_polys])
    ground = Ground(gmeta, G)
    print(f'  висоти: {gmeta["nx"]}×{gmeta["nz"]} по {GRID_STEP:.0f} м, {float(G.min()):.0f}…{float(G.max()):.0f} м, '
          f'крона лісу {canopy:.1f} м')

    ways = [x for x in els if x['type'] == 'way']
    tagged_nodes = {x['id']: x for x in els if x['type'] == 'node'}

    # ---- дороги
    road_ways = [x for x in ways if x.get('tags', {}).get('highway') in DRIVE and x['tags'].get('area') != 'yes'
                 and len(x['nodes']) >= 2]
    xy, vert, edges = build_roads(road_ways, P, b, tol)
    find_dual(edges)

    # вершини: висота — земля, степінь, ознаки
    deg = collections.Counter()
    ring_v = set()
    link_v = set()
    for ed in edges:
        for k in ('a', 'b'):
            deg[ed[k]] += 1
            if ed.get('ring'):
                ring_v.add(ed[k])
            if ed.get('link'):
                link_v.add(ed[k])
    nodes = {}
    for vid in deg:
        x, z = xy[int(vid[1:])]
        nodes[vid] = [r1(x), r1(z), r1(ground(x, z))]
    node_tags = {}
    points = []
    interior = {}
    for ed in edges:
        for nid in ed['nids'][1:-1]:
            interior[nid] = ed['id']
    for nid, nd in tagged_nodes.items():
        t = nd.get('tags', {})
        k = t.get('highway')
        if k not in ('traffic_signals', 'stop', 'give_way', 'motorway_junction', 'mini_roundabout', 'crossing'):
            continue
        vid = f'n{nid}'
        info = {'k': k}
        for kk in ('ref', 'name', 'crossing'):
            if t.get(kk):
                info[kk] = t[kk]
        if vid in nodes:
            node_tags.setdefault(vid, []).append(info)
        elif nid in interior:
            x, z = xy[nid]
            points.append({**info, 'x': r1(x), 'z': r1(z), 'y': r1(ground(x, z)), 'edge': interior[nid]})

    raise_bridges(edges, nodes, xy, ground, node_tags)

    # профіль висот ребер
    for ed in edges:
        flat = bool(ed.get('bridge') or ed.get('tunnel'))
        ya, yb = nodes[ed['a']][2], nodes[ed['b']][2]
        ys = road_profile(ed['pts'], ya, yb, ground, flat)
        ed['len'] = r1(geom.length(ed['pts']))
        ed['pts'] = [[r1(x), r1(z), r1(y)] for (x, z), y in zip(ed['pts'], ys)]
        del ed['nids']

    # кільця (круговий рух): компоненти ребер ring
    parent = {}

    def root(v):
        while parent.setdefault(v, v) != v:
            parent[v] = parent[parent[v]]
            v = parent[v]
        return v

    for ed in edges:
        if ed.get('ring'):
            parent[root(ed['a'])] = root(ed['b'])
    groups = collections.defaultdict(list)
    for ed in edges:
        if ed.get('ring'):
            groups[root(ed['a'])].append(ed)
    rings = []
    for gi, (_, eds) in enumerate(sorted(groups.items(), key=lambda kv: kv[1][0]['id'])):
        pts = np.asarray([p[:2] for ed in eds for p in ed['pts']])
        c = pts.mean(axis=0)
        vs = sorted({v for ed in eds for v in (ed['a'], ed['b'])})
        exits = sorted({ed['id'] for ed in edges if not ed.get('ring') and (ed['a'] in vs or ed['b'] in vs)})
        rr = {'id': f'ring{gi + 1}', 'x': r1(c[0]), 'z': r1(c[1]), 'r': r1(np.hypot(*(pts - c).T).mean()),
              'nodes': vs, 'edges': [ed['id'] for ed in eds], 'exits': exits}
        if eds[0].get('name'):
            rr['name'] = eds[0]['name']
        rings.append(rr)
    for nid, nd in tagged_nodes.items():
        if nd.get('tags', {}).get('highway') == 'mini_roundabout' and f'n{nid}' in nodes:
            x, z, _ = nodes[f'n{nid}']
            rings.append({'id': f'mini{nid}', 'x': x, 'z': z, 'r': 0, 'nodes': [f'n{nid}'], 'edges': [], 'mini': True,
                          'exits': sorted(ed['id'] for ed in edges if f'n{nid}' in (ed['a'], ed['b']))})
    for vid in nodes:
        k = 'end' if deg[vid] == 1 else 'ring' if vid in ring_v else 'join' if deg[vid] == 2 else 'cross'
        node_tags.setdefault(vid, [])
        node_tags[vid].insert(0, {'k': k, 'deg': deg[vid], **({'link': True} if vid in link_v else {})})

    # ---- пішохідні (без графа)
    paths = []
    for x in ways:
        t = x.get('tags', {})
        if t.get('highway') not in FOOT or t.get('area') == 'yes':
            continue
        for part in geom.clip_line(ring_xy(P, x['geometry']), b):
            pts = [part[i] for i in geom.simplify(part, 1.0)]
            p = {'cls': t['highway'], 'pts': [[r1(a), r1(c)] for a, c in pts]}
            for kk in ('name', 'surface'):
                if t.get(kk):
                    p[kk] = t[kk]
            if yes(t.get('bridge')):
                p['bridge'] = t['bridge']
            paths.append(p)

    # ---- залізниця й трамвай
    rails = []
    for x in ways:
        t = x.get('tags', {})
        k = t.get('railway')
        if k not in RAIL:
            continue
        for part in geom.clip_line(ring_xy(P, x['geometry']), b):
            pts = [part[i] for i in geom.simplify(part, 1.0)]
            flat = yes(t.get('bridge')) or yes(t.get('tunnel'))
            ya, yb = float(ground(*pts[0])), float(ground(*pts[-1]))
            ys = road_profile(pts, ya, yb, ground, flat)
            r = {'k': k, 'pts': [[r1(a), r1(c), r1(y)] for (a, c), y in zip(pts, ys)]}
            for kk in ('service', 'usage', 'name', 'electrified', 'gauge'):
                if t.get(kk):
                    r[kk] = t[kk]
            if yes(t.get('bridge')):
                r['bridge'] = t['bridge']
            if yes(t.get('tunnel')):
                r['tunnel'] = t['tunnel']
            lay = num(t.get('layer'))
            if lay:
                r['layer'] = int(lay)
            rails.append(r)

    # ---- річки й струмки (лінії)
    waterways = []
    for x in ways:
        t = x.get('tags', {})
        if t.get('waterway') not in WATERWAY:
            continue
        for part in geom.clip_line(ring_xy(P, x['geometry']), b):
            pts = [part[i] for i in geom.simplify(part, 2.0)]
            r = {'k': t['waterway'], 'pts': [[r1(a), r1(c)] for a, c in pts]}
            if t.get('name'):
                r['name'] = t['name']
            wd = num(t.get('width'))
            if wd:
                r['width'] = wd
            if yes(t.get('tunnel')):
                r['tunnel'] = t['tunnel']
            waterways.append(r)

    # ---- площі
    areas = []
    for el, t, outer, holes in polygons(els, P, b, lambda t: 'building' not in t and area_kind(t) is not None):
        o = fix_ring(outer, 1.0)
        if not o:
            continue
        a = {'k': area_kind(t), 'outer': o}
        hs = [h for h in (fix_ring(h, 1.0, ccw=False) for h in holes) if h]
        if hs:
            a['holes'] = hs
        if t.get('name'):
            a['name'] = t['name']
        areas.append(a)

    # ---- будинки
    buildings = []
    for el, t, outer, holes in bld_polys:
        o = fix_ring(outer, 0.3)
        if not o or abs(geom.area(o)) < 6:
            continue
        cx = sum(p[0] for p in o) / len(o)
        cz = sum(p[1] for p in o) / len(o)
        if not geom.inside(b, cx, cz):
            continue
        bd = {'id': f"{el['type'][0]}{el['id']}", 'k': t['building'],
              'y': r1(float(np.min(ground(np.array([p[0] for p in o]), np.array([p[1] for p in o]))))), 'pts': o}
        lv = num(t.get('building:levels'))
        if lv:
            bd['lv'] = lv
        h = num(t.get('height'))
        if h:
            bd['h'] = h
        mh = num(t.get('min_height'))
        if mh:
            bd['mh'] = mh
        rl = num(t.get('roof:levels'))
        if rl:
            bd['rl'] = rl
        if t.get('roof:shape'):
            bd['roof'] = t['roof:shape']
        if t.get('name'):
            bd['name'] = t['name']
        if t.get('addr:housenumber'):
            bd['addr'] = ((t.get('addr:street') or '') + ' ' + t['addr:housenumber']).strip()
        hs = [hh for hh in (fix_ring(h, 0.3, ccw=False) for h in holes) if hh]
        if hs:
            bd['holes'] = hs
        buildings.append(bd)

    # ---- точки інтересу (кандидати на «місця» гри) і підписи районів
    pois, labels = [], []
    for el in els:
        t = el.get('tags', {})
        k = None
        if t.get('railway') in ('station', 'halt') or t.get('public_transport') == 'station':
            k = 'station'
        elif t.get('amenity') in ('marketplace', 'fuel', 'bus_station'):
            k = t['amenity']
        elif t.get('shop') in ('wholesale', 'mall', 'supermarket', 'doityourself', 'trade'):
            k = 'shop:' + t['shop']
        elif t.get('landuse') == 'industrial' and t.get('name'):
            k = 'industrial'
        elif el['type'] == 'node' and t.get('place') and t.get('name'):
            x, z = P.fwd(el['lat'], el['lon'])
            if geom.inside(b, float(x), float(z)):
                labels.append({'k': t['place'], 'name': t['name'], 'x': r1(x), 'z': r1(z)})
            continue
        if not k:
            continue
        if el['type'] == 'node':
            x, z = P.fwd(el['lat'], el['lon'])
            x, z = float(x), float(z)
            size = None
        elif el['type'] == 'way':
            pts = ring_xy(P, el['geometry'])
            x = sum(p[0] for p in pts) / len(pts)
            z = sum(p[1] for p in pts) / len(pts)
            size = abs(geom.area(pts)) if el['nodes'][0] == el['nodes'][-1] else None
        else:
            pts = [p for m in el['members'] if m.get('geometry') for p in ring_xy(P, m['geometry'])]
            if not pts:
                continue
            x = sum(p[0] for p in pts) / len(pts)
            z = sum(p[1] for p in pts) / len(pts)
            size = None
        if not geom.inside(b, x, z):
            continue
        poi = {'k': k, 'osm': f"{el['type']}/{el['id']}", 'x': r1(x), 'z': r1(z)}
        if t.get('name'):
            poi['name'] = t['name']
        if size:
            poi['area'] = round(size)
        if t.get('brand'):
            poi['brand'] = t['brand']
        pois.append(poi)

    out = {
        'format': 'fura-osm/1',
        'id': city,
        'title': cfg['title'],
        'attribution': {
            'osm': '© OpenStreetMap contributors (ODbL 1.0) — openstreetmap.org/copyright',
            'dem': 'produced using Copernicus WorldDEM-30 © DLR e.V. 2010-2014 and © Airbus Defence and Space GmbH '
                   '2014-2018 provided under COPERNICUS by the European Union and ESA; all rights reserved',
        },
        'source': {'osm_base': raw.get('osm3s', {}).get('timestamp_osm_base'), 'bbox': list(cfg['bbox']),
                   'dem': [os.path.basename(p) for p in dem_paths], 'canopy': round(canopy, 1),
                   'tool': 'tools/osm/build.py', 'tol': tol},
        'proj': {'type': 'tmerc', 'datum': 'WGS84', 'lat0': cfg['center'][0], 'lon0': cfg['center'][1],
                 '_axes': 'метри; x — схід, z — північ, y — висота над рівнем моря (Copernicus, EGM2008)'},
        'bounds': {'x0': b[0], 'z0': b[1], 'x1': b[2], 'z1': b[3]},
        'height': encode_grid(gmeta, G),
        'nodes': nodes,
        'nodeTags': {k: v for k, v in node_tags.items() if v},
        'edges': edges,
        'rings': rings,
        'points': points,
        'paths': paths,
        'rails': rails,
        'waterways': waterways,
        'areas': areas,
        'buildings': buildings,
        'pois': pois,
        'labels': labels,
    }
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    text = json.dumps(out, ensure_ascii=False, separators=(',', ':'))
    with open(out_path, 'wb') as raw_f, gzip.GzipFile(filename='', mode='wb', fileobj=raw_f, compresslevel=9, mtime=0) as f:
        f.write(text.encode('utf-8'))          # mtime=0: той самий вхід — той самий файл байт у байт
    km = sum(ed['len'] for ed in edges) / 1000
    print(f'  {len(nodes)} вершин, {len(edges)} ребер ({km:.0f} км), {len(rings)} кілець, {len(buildings)} будинків, '
          f'{len(areas)} площ, {len(rails)} колій, {len(waterways)} водотоків, {len(pois)} точок')
    print(f'  {out_path}: {len(text.encode()) / 1e6:.1f} МБ JSON, {os.path.getsize(out_path) / 1e6:.2f} МБ gzip')
    return out
