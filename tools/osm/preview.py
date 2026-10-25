"""Прев'ю проміжного формату: PNG (усе місто + центр крупно) і SVG, плюс перевірки графа очима й числами:
- кінці доріг біля чужого ребра (≤ 4 м), але не з'єднані — підозра на розрив;
- перетини доріг одного шару без спільного вузла — підозра на пропущене перехрестя;
- компоненти зв'язності; мости над річками.
"""
import base64
import collections
import gzip
import json
import math

import numpy as np
from PIL import Image, ImageDraw, ImageFont

import geom

STYLE = {   # клас: (ширина, м; колір)
    'motorway': (14, '#e8743b'), 'trunk': (13, '#e8743b'), 'primary': (11, '#f0a23a'), 'secondary': (9, '#f3cd4b'),
    'tertiary': (8, '#fff3a0'), 'unclassified': (6, '#ffffff'), 'residential': (6, '#ffffff'),
    'living_street': (5, '#e6e6e6'), 'service': (3.5, '#d8d8d8'), 'track': (3, '#b48a5a'), 'road': (5, '#cccccc'),
    'busway': (5, '#8888ff'),
}
AREA = {'water': '#7fb2e5', 'wood': '#8fbf7f', 'park': '#b4dca0', 'grass': '#c8e6b0', 'pitch': '#a8dcb8',
        'wetland': '#a8d0c8', 'cemetery': '#b8ccb0', 'farmland': '#e8e0b8', 'parking': '#d0d0d0', 'plaza': '#dcd6d0',
        'industrial': '#dcc8d8', 'commercial': '#ecd0d0', 'retail': '#ecd0d0', 'residential': '#e2ddd6',
        'garages': '#d6d0c8', 'railway': '#d8d0d8', 'construction': '#d8d4b8'}


def load(path):
    with gzip.open(path, 'rt', encoding='utf-8') as f:
        return json.load(f)


def grid(m):
    h = m['height']
    q = np.frombuffer(base64.b64decode(h['data']), '<u2').reshape(h['nz'], h['nx'])
    return h, h['y0'] + q.astype('f4') * h['scale']


def cls_of(e):
    return e['cls'].replace('_link', '')


# ---------------------------------------------------------------- перевірки

def checks(m):
    edges, nodes = m['edges'], m['nodes']
    adj = collections.defaultdict(set)
    for e in edges:
        adj[e['a']].add(e['b'])
        adj[e['b']].add(e['a'])
    seen, comps = set(), []
    for v in nodes:
        if v in seen:
            continue
        st, c = [v], 0
        seen.add(v)
        while st:
            u = st.pop()
            c += 1
            for w in adj[u]:
                if w not in seen:
                    seen.add(w)
                    st.append(w)
        comps.append(c)
    comps.sort(reverse=True)

    # просторовий індекс відрізків
    cell = 50.0
    idx = collections.defaultdict(list)
    for ei, e in enumerate(edges):
        p = e['pts']
        for k in range(1, len(p)):
            for cx in range(int(math.floor(min(p[k - 1][0], p[k][0]) / cell)), int(math.floor(max(p[k - 1][0], p[k][0]) / cell)) + 1):
                for cz in range(int(math.floor(min(p[k - 1][1], p[k][1]) / cell)), int(math.floor(max(p[k - 1][1], p[k][1]) / cell)) + 1):
                    idx[(cx, cz)].append((ei, k))

    def near(x, z):
        cx, cz = int(math.floor(x / cell)), int(math.floor(z / cell))
        for gx in (cx - 1, cx, cx + 1):
            for gz in (cz - 1, cz, cz + 1):
                yield from idx.get((gx, gz), ())

    def seg_dist(x, z, a, b):
        dx, dz = b[0] - a[0], b[1] - a[1]
        L2 = dx * dx + dz * dz
        t = 0 if L2 < 1e-9 else max(0, min(1, ((x - a[0]) * dx + (z - a[1]) * dz) / L2))
        return math.hypot(a[0] + dx * t - x, a[1] + dz * t - z)

    deg = collections.Counter()
    for e in edges:
        deg[e['a']] += 1
        deg[e['b']] += 1
    gaps = []
    for v, d in deg.items():
        if d != 1:
            continue
        x, z = nodes[v][:2]
        best = None
        for ei, k in near(x, z):
            e = edges[ei]
            if v in (e['a'], e['b']):
                continue
            dd = seg_dist(x, z, e['pts'][k - 1], e['pts'][k])
            if dd <= 4 and (best is None or dd < best[0]):
                best = (dd, e['id'])
        if best:
            gaps.append((x, z, best[0], v, best[1]))

    # перетини без вузла: обидва не міст/тунель, однаковий шар
    cross = []
    done = set()
    for ei, e in enumerate(edges):
        if e.get('bridge') or e.get('tunnel'):
            continue
        p = e['pts']
        for k in range(1, len(p)):
            a, b = p[k - 1], p[k]
            for ej, kk in near((a[0] + b[0]) / 2, (a[1] + b[1]) / 2):
                if ej <= ei or (ei, ej, k, kk) in done:
                    continue
                o = edges[ej]
                if o.get('bridge') or o.get('tunnel') or o.get('layer', 0) != e.get('layer', 0):
                    continue
                done.add((ei, ej, k, kk))
                c, d = o['pts'][kk - 1], o['pts'][kk]
                den = (b[0] - a[0]) * (d[1] - c[1]) - (b[1] - a[1]) * (d[0] - c[0])
                if abs(den) < 1e-9:
                    continue
                t = ((c[0] - a[0]) * (d[1] - c[1]) - (c[1] - a[1]) * (d[0] - c[0])) / den
                u = ((c[0] - a[0]) * (b[1] - a[1]) - (c[1] - a[1]) * (b[0] - a[0])) / den
                if 0.02 < t < 0.98 and 0.02 < u < 0.98:
                    cross.append((a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, e['id'], o['id'], e['cls'], o['cls']))

    # мости над річками (лінія мосту перетинає водотік river/canal або лежить над водою)
    rivers = [w for w in m['waterways'] if w['k'] in ('river', 'canal')]
    waters = [a for a in m['areas'] if a['k'] == 'water']

    def near_river(x, z):
        best = min(((min(math.hypot(q[0] - x, q[1] - z) for q in w['pts']), w.get('name') or w['k']) for w in rivers),
                   default=(0, 'вода'))
        return best[1] + ' (площа)'

    bridges = []
    for e in edges:
        if not e.get('bridge'):
            continue
        p = e['pts']
        for w in rivers:
            q = w['pts']
            hit = False
            for k in range(1, len(p)):
                for kk in range(1, len(q)):
                    a, b, c, d = p[k - 1], p[k], q[kk - 1], q[kk]
                    den = (b[0] - a[0]) * (d[1] - c[1]) - (b[1] - a[1]) * (d[0] - c[0])
                    if abs(den) < 1e-9:
                        continue
                    t = ((c[0] - a[0]) * (d[1] - c[1]) - (c[1] - a[1]) * (d[0] - c[0])) / den
                    u = ((c[0] - a[0]) * (b[1] - a[1]) - (c[1] - a[1]) * (b[0] - a[0])) / den
                    if 0 <= t <= 1 and 0 <= u <= 1:
                        hit = True
                        break
                if hit:
                    break
            if hit:
                bridges.append((e, w.get('name', w['k'])))
                break
        else:
            # річка тут лише площею (водосховище, широке русло) — середина мосту над водою
            mx, mz = p[len(p) // 2][:2]
            for a in waters:
                if geom.point_in(a['outer'], mx, mz):
                    bridges.append((e, a.get('name') or near_river(mx, mz)))
                    break
    # дороги через річку без мосту: греблі, броди — або пропущений тег bridge
    wet = []
    for e in edges:
        if e.get('bridge') or e.get('tunnel') or e['cls'] in ('track', 'service'):
            continue
        p = e['pts']
        for w in rivers:
            q = w['pts']
            if any(_cross(p[k - 1], p[k], q[kk - 1], q[kk]) for k in range(1, len(p)) for kk in range(1, len(q))):
                wet.append((e, w.get('name') or w['k']))
                break
    return {'comps': comps, 'gaps': gaps, 'cross': cross, 'bridges': bridges, 'wet': wet}


def _cross(a, b, c, d):
    den = (b[0] - a[0]) * (d[1] - c[1]) - (b[1] - a[1]) * (d[0] - c[0])
    if abs(den) < 1e-9:
        return False
    t = ((c[0] - a[0]) * (d[1] - c[1]) - (c[1] - a[1]) * (d[0] - c[0])) / den
    u = ((c[0] - a[0]) * (b[1] - a[1]) - (c[1] - a[1]) * (b[0] - a[0])) / den
    return 0 <= t <= 1 and 0 <= u <= 1


# ---------------------------------------------------------------- малювання

def relief_image(m, view, s):
    h, G = grid(m)
    x0, z0, x1, z1 = view
    W, H = int((x1 - x0) * s), int((z1 - z0) * s)
    # гіпсометрія + відмивка на сітці, потім до розміру картинки
    gz, gx = np.gradient(G, h['step'])
    shade = np.clip(0.8 + (-gx * 0.7 + gz * 0.7) * 3, 0.5, 1.12)
    t = np.clip((G - G.min()) / max(1, float(np.ptp(G))), 0, 1)
    lo, hi = np.array([214, 226, 196]), np.array([236, 214, 178])
    rgb = (lo[None, None] * (1 - t[..., None]) + hi[None, None] * t[..., None]) * shade[..., None]
    img = Image.fromarray(np.clip(rgb, 0, 255).astype('u1')[::-1])      # рядок 0 — північ
    full_w = (h['nx'] - 1) * h['step']
    full_h = (h['nz'] - 1) * h['step']
    box = ((x0 - h['x0']) / full_w * img.width, (h['z0'] + full_h - z1) / full_h * img.height,
           (x1 - h['x0']) / full_w * img.width, (h['z0'] + full_h - z0) / full_h * img.height)
    box = (max(0, box[0]), max(0, box[1]), min(img.width, box[2]), min(img.height, box[3]))
    return img.resize((W, H), Image.BILINEAR, box=box)


def draw(m, chk, view, s, path, detail):
    img = relief_image(m, view, s)
    d = ImageDraw.Draw(img, 'RGBA')
    x0, z0, x1, z1 = view

    def P(x, z):
        return ((x - x0) * s, (z1 - z) * s)

    def visible(pts):
        xs = [p[0] for p in pts]
        zs = [p[1] for p in pts]
        return max(xs) >= x0 and min(xs) <= x1 and max(zs) >= z0 and min(zs) <= z1

    order = ['farmland', 'residential', 'industrial', 'commercial', 'retail', 'garages', 'railway', 'construction',
             'cemetery', 'grass', 'park', 'pitch', 'wetland', 'wood', 'parking', 'plaza', 'water']
    for k in order:
        for a in m['areas']:
            if a['k'] == k and visible(a['outer']):
                d.polygon([P(*p) for p in a['outer']], fill=AREA.get(k, '#dddddd'))
                for hole in a.get('holes', []):
                    d.polygon([P(*p) for p in hole], fill=AREA['residential'] + '00')
    for w in m['waterways']:
        if visible(w['pts']):
            wd = w.get('width') or (20 if w['k'] == 'river' else 4)
            d.line([P(*p) for p in w['pts']], fill='#5d9ad6', width=max(1, int(wd * s * 0.6)))
    for b in m['buildings']:
        if visible(b['pts']):
            col = (160, 120, 110, 255) if b['k'] in ('industrial', 'warehouse', 'garages', 'garage') else (190, 168, 150, 255)
            d.polygon([P(*p) for p in b['pts']], fill=col, outline=(120, 100, 90, 255) if detail else None)
    for r in m['rails']:
        if r['k'] in ('rail', 'tram', 'light_rail', 'narrow_gauge') and visible(r['pts']):
            d.line([P(*p[:2]) for p in r['pts']], fill='#555555' if r['k'] == 'rail' else '#7a5a8a',
                   width=max(1, int((3 if r['k'] == 'rail' else 2) * s)))
    if detail:
        for pth in m['paths']:
            if visible(pth['pts']):
                d.line([P(*p) for p in pth['pts']], fill=(200, 90, 90, 150), width=1)
    # дороги: спершу обвідка (мости чорні), потім заливка, від дрібних до великих
    rank = {c: i for i, c in enumerate(['track', 'service', 'living_street', 'road', 'busway', 'residential', 'unclassified',
                                         'tertiary', 'secondary', 'primary', 'trunk', 'motorway'])}
    es = sorted(m['edges'], key=lambda e: (e.get('layer', 0), rank.get(cls_of(e), 0)))
    for e in es:
        if not visible(e['pts']):
            continue
        w, col = STYLE.get(cls_of(e), (4, '#cccccc'))
        px = [P(*p[:2]) for p in e['pts']]
        wpx = max(1, int(w * s))
        edge_col = '#000000' if e.get('bridge') else '#9a8a7a'
        d.line(px, fill=edge_col, width=wpx + (4 if e.get('bridge') else 2), joint='curve')
    for e in es:
        if not visible(e['pts']):
            continue
        w, col = STYLE.get(cls_of(e), (4, '#cccccc'))
        px = [P(*p[:2]) for p in e['pts']]
        d.line(px, fill='#c070ff' if e.get('dual') and detail else col, width=max(1, int(w * s)), joint='curve')
        if e.get('tunnel'):
            d.line(px, fill=(80, 80, 80, 160), width=max(1, int(w * s)))
    if detail:
        for v, (x, z, _) in m['nodes'].items():
            if x0 <= x <= x1 and z0 <= z <= z1:
                d.ellipse([P(x, z)[0] - 1.5, P(x, z)[1] - 1.5, P(x, z)[0] + 1.5, P(x, z)[1] + 1.5], fill='#3060c0')
    for r in m['rings']:
        cx, cz = P(r['x'], r['z'])
        rr = max(4, r['r'] * s)
        d.ellipse([cx - rr, cz - rr, cx + rr, cz + rr], outline='#0090ff', width=3)
    for x, z, *_ in chk['gaps']:
        cx, cz = P(x, z)
        d.ellipse([cx - 7, cz - 7, cx + 7, cz + 7], outline='#ff0000', width=3)
    for x, z, *_ in chk['cross']:
        cx, cz = P(x, z)
        d.line([cx - 7, cz - 7, cx + 7, cz + 7], fill='#ff6a00', width=3)
        d.line([cx - 7, cz + 7, cx + 7, cz - 7], fill='#ff6a00', width=3)
    for p in m['pois']:
        if p['k'] in ('station', 'marketplace', 'industrial') and p.get('name'):
            cx, cz = P(p['x'], p['z'])
            d.rectangle([cx - 4, cz - 4, cx + 4, cz + 4], fill='#c00060')
    img.save(path, optimize=True)
    print(f'  {path}: {img.width}×{img.height}')


def svg(m, chk, path):
    b = m['bounds']
    W, H = b['x1'] - b['x0'], b['z1'] - b['z0']

    def pts(ps):
        return ' '.join(f"{p[0] - b['x0']:.0f},{b['z1'] - p[1]:.0f}" for p in ps)

    out = [f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" width="{W / 4:.0f}" height="{H / 4:.0f}">',
           f'<rect width="{W}" height="{H}" fill="#eef0e4"/>',
           f'<title>{m["title"]}: {m["attribution"]["osm"]}</title>']
    for a in m['areas']:
        if a['k'] in AREA:
            out.append(f'<polygon points="{pts(a["outer"])}" fill="{AREA[a["k"]]}"/>')
    for w in m['waterways']:
        out.append(f'<polyline points="{pts(w["pts"])}" fill="none" stroke="#5d9ad6" stroke-width="{w.get("width") or (20 if w["k"] == "river" else 4)}"/>')
    out.append('<g fill="#bea896">')
    for bd in m['buildings']:
        out.append(f'<polygon points="{pts(bd["pts"])}"/>')
    out.append('</g>')
    for r in m['rails']:
        if r['k'] in ('rail', 'tram'):
            out.append(f'<polyline points="{pts(r["pts"])}" fill="none" stroke="#555" stroke-width="{3 if r["k"] == "rail" else 2}"/>')
    for e in sorted(m['edges'], key=lambda e: STYLE.get(cls_of(e), (4,))[0]):
        w, col = STYLE.get(cls_of(e), (4, '#ccc'))
        extra = ' stroke-dasharray="6 4"' if e.get('tunnel') else ''
        if e.get('bridge'):
            out.append(f'<polyline points="{pts(e["pts"])}" fill="none" stroke="#000" stroke-width="{w + 4}"/>')
        out.append(f'<polyline points="{pts(e["pts"])}" fill="none" stroke="{col}" stroke-width="{w}" stroke-linecap="round"{extra}>'
                   f'<title>{e["id"]} {e["cls"]} {e.get("name", "")}</title></polyline>')
    for x, z, *_ in chk['gaps']:
        out.append(f'<circle cx="{x - b["x0"]:.0f}" cy="{b["z1"] - z:.0f}" r="12" fill="none" stroke="red" stroke-width="4"/>')
    for x, z, *_ in chk['cross']:
        out.append(f'<circle cx="{x - b["x0"]:.0f}" cy="{b["z1"] - z:.0f}" r="10" fill="none" stroke="#ff6a00" stroke-width="4"/>')
    out.append('</svg>')
    with open(path, 'w', encoding='utf-8') as f:
        f.write('\n'.join(out))
    print(f'  {path}')


def render(inter, base):
    m = load(inter)
    chk = checks(m)
    c = chk['comps']
    major = collections.Counter(e['cls'] for e, _ in chk['bridges'])
    print(f"  зв'язність: {len(c)} компонент, найбільша {c[0]} з {sum(c)} вершин ({100 * c[0] / sum(c):.1f} %), "
          f"далі {c[1:6]}")
    print(f"  кінці біля чужого ребра (≤ 4 м, підозра на розрив): {len(chk['gaps'])}")
    print(f"  перетини без вузла (той самий шар): {len(chk['cross'])} — "
          f"{collections.Counter(tuple(sorted((a, b))) for *_, a, b in chk['cross']).most_common(6)}")
    by = collections.defaultdict(set)
    for e, r in chk['bridges']:
        by[r].add(e.get('name') or e.get('ref') or e['cls'])
    print(f"  мости над водою: {len(chk['bridges'])} ребер {dict(major)}")
    for r in sorted(by):
        print(f"    {r}: {', '.join(sorted(by[r]))}")
    print(f"  через річку без мосту (гребля, брід чи пропущений bridge): {len(chk['wet'])} — "
          + ', '.join(sorted({f"{r}: {e.get('name') or e['cls']}" for e, r in chk['wet']})))
    b = m['bounds']
    draw(m, chk, (b['x0'], b['z0'], b['x1'], b['z1']), 0.25, base + '.png', detail=False)
    draw(m, chk, (-1800, -1500, 2200, 2200), 1.0, base + '-center.png', detail=True)
    svg(m, chk, base + '.svg')
    with open(base + '-checks.txt', 'w', encoding='utf-8') as f:
        f.write('# кінці біля чужого ребра: x z відстань вершина ребро\n')
        for g in chk['gaps']:
            f.write(f'{g[0]:.0f} {g[1]:.0f} {g[2]:.1f} {g[3]} {g[4]}\n')
        f.write('# перетини без вузла: x z ребро1 ребро2 клас1 клас2\n')
        for g in chk['cross']:
            f.write(f'{g[0]:.0f} {g[1]:.0f} {g[2]} {g[3]} {g[4]} {g[5]}\n')
    return chk


def render_kray(inter, kray_path, path):
    """Прев'ю експорту kray.json поверх рельєфу проміжного файлу: дороги за kind, місця з дворами й боксами."""
    m = load(inter)
    with open(kray_path, encoding='utf-8') as f:
        k = json.load(f)
    b = m['bounds']
    view = (b['x0'], b['z0'], b['x1'], b['z1'])
    s = 0.35
    img = relief_image(m, view, s)
    d = ImageDraw.Draw(img, 'RGBA')
    x0, z1 = view[0], view[3]

    def P(x, z):
        return ((x - x0) * s, (z1 - z) * s)

    for a in m['areas']:
        if a['k'] in ('water', 'wood'):
            d.polygon([P(*p) for p in a['outer']], fill=AREA[a['k']])
    d.line([P(*p) for p in k['river']['pts']], fill='#3070c0', width=3)
    d.line([P(*p) for p in k['railway']['pts']], fill='#444444', width=2)
    col = {'highway': '#e8743b', 'village': '#806040', 'dirt': '#b48a5a'}
    for r in k['roads']:
        pts = [k['nodes'][r['a']]] + r['via'] + [k['nodes'][r['b']]]
        d.line([P(*p) for p in pts], fill=col[r['kind']], width=4 if r['kind'] == 'highway' else 2)
    for v, (x, z) in k['nodes'].items():
        cx, cz = P(x, z)
        d.ellipse([cx - 2, cz - 2, cx + 2, cz + 2], fill='#204080')
    try:
        font = ImageFont.truetype('arial.ttf', 22)      # з кирилицею (вбудований шрифт PIL — лише латиниця)
    except OSError:
        font = ImageFont.load_default()
    for p in k['places']:
        y = p['yard']
        a, c = P(y['x'] - y['w'] / 2, y['z'] + y['d'] / 2), P(y['x'] + y['w'] / 2, y['z'] - y['d'] / 2)
        d.rectangle([a[0] - 3, a[1] - 3, c[0] + 3, c[1] + 3], outline='#c00060', width=3)
        for dk in p['docks']:
            cx, cz = P(dk['x'], dk['z'])
            d.line([cx, cz, cx + 6 * math.sin(dk['yaw']), cz - 6 * math.cos(dk['yaw'])], fill='#c00060', width=2)
        nx, nz = P(*k['nodes'][p['node']])
        d.text((nx + 8, nz - 10), p['short'], fill='#000000', font=font)
    img.save(path, optimize=True)
    print(f'  {path}: {img.width}×{img.height}')
