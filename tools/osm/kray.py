"""Проміжний fura-osm/1 → мапа в теперішньому форматі kray.json (docs/games/specs/fura.md, «Мапа»): лише головні
дороги (motorway…tertiary, з'їзди, unclassified) і під'їзди до «місць»; місця й вантажі — з cities.py.
Сервер (FuraMap) читає її як є; браузерний kray.js — ні (див. README.md, «Що бракує рендеру»).
"""
import collections
import heapq
import json
import math

import geom
from cities import CITIES
from preview import load

MAIN = {'motorway', 'motorway_link', 'trunk', 'trunk_link', 'primary', 'primary_link', 'secondary', 'secondary_link',
        'tertiary', 'tertiary_link', 'unclassified'}
UNPAVED = {'unpaved', 'ground', 'dirt', 'gravel', 'sand', 'grass', 'compacted', 'fine_gravel', 'earth', 'mud', 'pebblestone'}
# вартість під'їзду: дрібні дороги дорожчі, щоб під'їзд ішов вулицями, а не дворами
COST = {'service': 1.6, 'track': 3.0, 'living_street': 1.4, 'residential': 1.1}
KINDS = {
    'highway': {'title': 'траса', 'width': 10, 'speed': 90, 'bumps': 0},
    'village': {'title': 'вулиця', 'width': 7, 'speed': 60, 'bumps': 0.25},
    'dirt': {'title': 'ґрунтовка', 'width': 6, 'speed': 40, 'bumps': 0.8},
}


def kind_of(e):
    if e['cls'] == 'track' or e.get('surface') in UNPAVED:
        return 'dirt'
    if e['cls'].replace('_link', '') in ('motorway', 'trunk', 'primary'):
        return 'highway'
    return 'village'


def r1(v):
    return round(float(v), 1)


def dijkstra(adj, starts, targets):
    """Від множини стартів до першої цілі; adj[v] = [(w, u, edge)]. Повертає список ребер шляху."""
    dist, prev = {}, {}
    pq = [(0.0, s) for s in starts]
    for s in starts:
        dist[s] = 0.0
    heapq.heapify(pq)
    while pq:
        d, v = heapq.heappop(pq)
        if d > dist.get(v, math.inf):
            continue
        if v in targets:
            path = []
            while v in prev:
                v, e = prev[v]
                path.append(e)
            return path
        for w, u, e in adj[v]:
            nd = d + w
            if nd < dist.get(u, math.inf):
                dist[u] = nd
                prev[u] = (v, e)
                heapq.heappush(pq, (nd, u))
    return None


def place_geometry(spec, m, node_xy, poi):
    """Двір 80×60 (або з spec) біля вузла під'їзду, у бік точки інтересу; бокси — уздовж дальнього від вузла боку,
    передок причепа дивиться на ворота (до вузла)."""
    nx, nz = node_xy
    px, pz = poi['x'], poi['z']
    dx, dz = px - nx, pz - nz
    dl = math.hypot(dx, dz) or 1.0
    w, d = spec.get('yard', (80, 60))
    off = min(max(dl, 50.0), 90.0)
    cx, cz = nx + dx / dl * off, nz + dz / dl * off
    n = spec.get('docks', 3)
    docks = []
    if abs(dz) >= abs(dx):
        # вузол на півночі (dz < 0: двір південніше) → бокси на південному боці, передок на північ (yaw 0)
        side = -1 if dz < 0 else 1
        yaw = 0.0 if side < 0 else math.pi
        zz = cz + side * (d / 2 - 15)
        for i in range(n):
            docks.append({'id': str(i + 1), 'x': r1(cx + (i - (n - 1) / 2) * 20), 'z': r1(zz), 'yaw': round(yaw, 4)})
    else:
        side = -1 if dx < 0 else 1
        yaw = math.pi / 2 if side < 0 else -math.pi / 2
        w, d = d, w                                  # бокси вздовж Z — двір видовжений по Z
        xx = cx + side * (w / 2 - 15)
        for i in range(n):
            docks.append({'id': str(i + 1), 'x': r1(xx), 'z': r1(cz + (i - (n - 1) / 2) * 20), 'yaw': round(yaw, 4)})
    yard = {'x': r1(cx), 'z': r1(cz), 'w': w, 'd': d}
    return yard, docks


def chain_lines(lines, tol=2.0):
    """Шматки ліній → найдовший ланцюг (кінець до кінця, ≤ tol м)."""
    segs = [list(map(tuple, l)) for l in lines]
    best = []
    while segs:
        cur = segs.pop()
        grown = True
        while grown:
            grown = False
            for i, s in enumerate(segs):
                if math.dist(cur[-1], s[0]) <= tol:
                    cur += s[1:]
                elif math.dist(cur[-1], s[-1]) <= tol:
                    cur += s[::-1][1:]
                elif math.dist(cur[0], s[-1]) <= tol:
                    cur = s[:-1] + cur
                elif math.dist(cur[0], s[0]) <= tol:
                    cur = s[::-1][:-1] + cur
                else:
                    continue
                segs.pop(i)
                grown = True
                break
        if geom.length(cur) > geom.length(best):
            best = cur
    return best


def export(inter, out_path, city):
    cfg = CITIES[city]
    m = load(inter)
    edges = {e['id']: e for e in m['edges']}
    nodes = m['nodes']
    adj = collections.defaultdict(list)
    for e in m['edges']:
        w = e['len'] * COST.get(e['cls'], 1.0)
        adj[e['a']].append((w, e['b'], e['id']))
        adj[e['b']].append((w, e['a'], e['id']))

    keep = {e['id'] for e in m['edges'] if e['cls'] in MAIN}
    pois = {p['osm']: p for p in m['pois']}
    places = []
    for spec in cfg['places']:
        poi = pois.get(spec['osm'])
        if not poi:
            raise SystemExit(f"місце {spec['id']}: нема точки {spec['osm']} у проміжному файлі")
        # вузол під'їзду: найближча вершина не-трекової дороги до центру точки (вантажівка заїжджає з вулиці)
        best = min((v for v in nodes if any(edges[e]['cls'] != 'track' for _, _, e in adj[v])),
                   key=lambda v: math.hypot(nodes[v][0] - poi['x'], nodes[v][1] - poi['z']))
        main_v = {v for e in keep for v in (edges[e]['a'], edges[e]['b'])}
        if best not in main_v:
            path = dijkstra(adj, [best], main_v)
            if path is None:
                raise SystemExit(f"місце {spec['id']}: не доходить до головних доріг")
            keep.update(path)
        places.append((spec, poi, best))

    # шляхи між усіма парами місць повним графом (з тими ж вагами): інакше між сусідами в промзоні фура їхала б
    # навколо головними вулицями
    for i, (_, _, va) in enumerate(places):
        for _, _, vb in places[i + 1:]:
            path = dijkstra(adj, [va], {vb})
            if path:
                keep.update(path)

    # найбільша компонента; усі місця мусять бути в ній
    sub = collections.defaultdict(list)
    for eid in sorted(keep):
        e = edges[eid]
        sub[e['a']].append((e['b'], eid))
        sub[e['b']].append((e['a'], eid))
    seen, best_comp = set(), set()
    for v in sub:
        if v in seen:
            continue
        comp, st = {v}, [v]
        seen.add(v)
        while st:
            u = st.pop()
            for w, _ in sub[u]:
                if w not in seen:
                    seen.add(w)
                    comp.add(w)
                    st.append(w)
        if len(comp) > len(best_comp):
            best_comp = comp
    for spec, _, v in places:
        if v not in best_comp:
            raise SystemExit(f"місце {spec['id']} поза найбільшою компонентою")
    keep = {eid for eid in keep if edges[eid]['a'] in best_comp}
    place_nodes = {v for _, _, v in places}

    # дороги підмножини: полілінія [(x, z)], kind; далі обрізаємо глухі хвостики < 60 м і зливаємо через степінь 2
    roads = {}
    for eid in sorted(keep):                    # множина рядків ходить щоразу інакше — сортуємо заради сталого файлу
        e = edges[eid]
        roads[eid] = {'a': e['a'], 'b': e['b'], 'kind': kind_of(e), 'pts': [tuple(p[:2]) for p in e['pts']],
                      'name': e.get('name') or e.get('ref')}

    def incid():
        inc = collections.defaultdict(set)
        for rid, r in roads.items():
            inc[r['a']].add(rid)
            inc[r['b']].add(rid)
        return inc

    changed = True
    while changed:
        changed = False
        inc = incid()
        for rid, r in list(roads.items()):
            for end, other in ((r['a'], r['b']), (r['b'], r['a'])):
                if len(inc[end]) == 1 and end not in place_nodes and geom.length(r['pts']) < 60 and len(inc[other]) > 2:
                    del roads[rid]
                    changed = True
                    break
            if changed:
                break
    inc = incid()
    for v in list(inc):
        ids = inc[v]
        if len(ids) != 2 or v in place_nodes:
            continue
        i, j = sorted(ids)
        ri, rj = roads[i], roads[j]
        if ri['kind'] != rj['kind'] or ri['a'] == ri['b'] or rj['a'] == rj['b'] or {ri['a'], ri['b']} == {rj['a'], rj['b']}:
            continue
        pi = ri['pts'] if ri['b'] == v else ri['pts'][::-1]
        pj = rj['pts'] if rj['a'] == v else rj['pts'][::-1]
        a = ri['a'] if ri['b'] == v else ri['b']
        b = rj['b'] if rj['a'] == v else rj['a']
        ri.update(a=a, b=b, pts=pi + pj[1:], name=ri['name'] or rj['name'])
        del roads[j]
        for end in (a, b):
            inc[end].discard(j)
            inc[end].add(i)
        del inc[v]

    # спрощення для мапи — 2 м (kray.js згладжує кути, сервер міряє полілінію)
    for r in roads.values():
        keep_i = geom.simplify(r['pts'], 2.0)
        r['pts'] = [r['pts'][k] for k in keep_i]

    used = {v for r in roads.values() for v in (r['a'], r['b'])}
    names = {}
    for spec, _, v in places:
        names[v] = spec['id']
    j = 0
    for v in sorted(used - set(names), key=lambda v: (nodes[v][0], nodes[v][1])):
        j += 1
        names[v] = f'j{j}'
    out_nodes = {names[v]: [nodes[v][0], nodes[v][1]] for v in sorted(used, key=lambda v: names[v])}
    out_roads = []
    cnt = collections.Counter()
    for rid in sorted(roads, key=lambda r: (roads[r]['kind'], names[roads[r]['a']], names[roads[r]['b']])):
        r = roads[rid]
        cnt[r['kind']] += 1
        rr = {'id': f"{r['kind'][0]}{cnt[r['kind']]}", 'kind': r['kind'], 'a': names[r['a']], 'b': names[r['b']],
              'via': [[r1(x), r1(z)] for x, z in r['pts'][1:-1]]}
        if r['name']:
            rr['name'] = r['name']
        out_roads.append(rr)

    out_places = []
    for spec, poi, v in places:
        yard, docks = place_geometry(spec, m, nodes[v][:2], poi)
        p = {'id': spec['id'], 'title': spec['title'], 'short': spec['short'], 'node': spec['id'], 'osm': spec['osm']}
        if spec.get('depot'):
            p['depot'] = True
            p['spawn'] = {'x': yard['x'], 'z': yard['z'], 'yaw': docks[0]['yaw']}
        p['yard'] = yard
        p['docks'] = docks
        out_places.append(p)

    teteriv = chain_lines([w['pts'] for w in m['waterways'] if w.get('name') == cfg['river']])
    rail = chain_lines([[p[:2] for p in r['pts']] for r in m['rails'] if r['k'] == 'rail' and not r.get('service')])
    xs = [p[0] for p in out_nodes.values()]
    zs = [p[1] for p in out_nodes.values()]
    cxm, czm = (min(xs) + max(xs)) / 2, (min(zs) + max(zs)) / 2
    half = math.ceil((max(max(xs) - min(xs), max(zs) - min(zs)) / 2 + 300) / 100) * 100
    out = {
        'id': city,
        'title': cfg['title'],
        'v': 1,
        '_axes': 'метри; x — схід, z — північ (поперечний Меркатор, центр міста = 0, 0). yaw — курс передка причепа: '
                 '0 = на північ (+Z), π/2 = на схід (+X). Зібрано tools/osm з OpenStreetMap — див. attribution',
        'attribution': m['attribution'],
        'source': {**m['source'], 'from': 'tools/osm/kray.py'},
        'seed': 1884,
        'bounds': {'x0': math.floor(cxm - half), 'z0': math.floor(czm - half), 'x1': math.ceil(cxm + half), 'z1': math.ceil(czm + half)},
        'kinds': KINDS,
        'nodes': out_nodes,
        'roads': out_roads,
        'places': out_places,
        'cargo': cfg['cargo'],
        'river': {'width': cfg.get('river_width', 50), 'name': cfg['river'],
                  'pts': [[r1(x), r1(z)] for x, z in (teteriv[i] for i in geom.simplify(teteriv, 8.0))]},
        'railway': {'pts': [[r1(x), r1(z)] for x, z in (rail[i] for i in geom.simplify(rail, 8.0))]},
    }
    text = json.dumps(out, ensure_ascii=False, indent=1)
    with open(out_path, 'w', encoding='utf-8', newline='\n') as f:
        f.write(text + '\n')
    km = sum(geom.length([out_nodes[r['a']]] + r['via'] + [out_nodes[r['b']]]) for r in out_roads) / 1000
    print(f'  {out_path}: {len(out_nodes)} вузлів, {len(out_roads)} доріг ({km:.0f} км, {dict(cnt)}), '
          f'{len(out_places)} місць, {len(text.encode()) / 1e3:.0f} КБ')
