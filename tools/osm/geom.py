"""Геометрія на площині гри (x — схід, z — північ): спрощення, обрізання прямокутником, кільця мультиполігонів."""
import math

import numpy as np


def simplify(pts, tol):
    """Дуглас — Пекер для полілінії [(x, z), …]; кінці лишаються завжди. Повертає індекси точок, що лишились."""
    n = len(pts)
    if n < 3 or tol <= 0:
        return list(range(n))
    p = np.asarray(pts, 'f8')
    keep = np.zeros(n, bool)
    keep[0] = keep[-1] = True
    stack = [(0, n - 1)]
    while stack:
        i, j = stack.pop()
        if j <= i + 1:
            continue
        a, b = p[i], p[j]
        ab = b - a
        L = math.hypot(*ab)
        seg = p[i + 1:j] - a
        if L < 1e-9:
            d = np.hypot(seg[:, 0], seg[:, 1])
        else:
            d = np.abs(seg[:, 0] * ab[1] - seg[:, 1] * ab[0]) / L
        k = int(np.argmax(d))
        if d[k] > tol:
            m = i + 1 + k
            keep[m] = True
            stack += [(i, m), (m, j)]
    return [int(i) for i in np.nonzero(keep)[0]]


def length(pts):
    return sum(math.hypot(pts[i][0] - pts[i - 1][0], pts[i][1] - pts[i - 1][1]) for i in range(1, len(pts)))


def inside(b, x, z):
    return b[0] <= x <= b[2] and b[1] <= z <= b[3]


def clip_line(pts, b):
    """Полілінія → шматки всередині прямокутника b = (x0, z0, x1, z1) (Лянг — Барскі по відрізках)."""
    out, cur = [], []
    for i in range(1, len(pts)):
        (x0, z0), (x1, z1) = pts[i - 1][:2], pts[i][:2]
        t0, t1 = 0.0, 1.0
        dx, dz = x1 - x0, z1 - z0
        ok = True
        for p, q in ((-dx, x0 - b[0]), (dx, b[2] - x0), (-dz, z0 - b[1]), (dz, b[3] - z0)):
            if abs(p) < 1e-12:
                if q < 0:
                    ok = False
                    break
            else:
                t = q / p
                if p < 0:
                    t0 = max(t0, t)
                else:
                    t1 = min(t1, t)
        if not ok or t0 > t1:
            if cur:
                out.append(cur)
                cur = []
            continue
        a = (x0 + t0 * dx, z0 + t0 * dz)
        c = (x0 + t1 * dx, z0 + t1 * dz)
        if not cur:
            cur = [a]
        cur.append(c)
        if t1 < 1.0:
            out.append(cur)
            cur = []
    if cur:
        out.append(cur)
    return [c for c in out if len(c) >= 2]


def clip_ring(ring, b):
    """Сазерленд — Ходжмен: замкнене кільце (без повтору першої точки) ∩ прямокутник."""
    def cut(pts, inside_f, inter):
        res = []
        for i in range(len(pts)):
            cur, prev = pts[i], pts[i - 1]
            ci, pi = inside_f(cur), inside_f(prev)
            if ci:
                if not pi:
                    res.append(inter(prev, cur))
                res.append(cur)
            elif pi:
                res.append(inter(prev, cur))
        return res

    def ix(xv):
        return lambda p, q: (xv, p[1] + (q[1] - p[1]) * (xv - p[0]) / (q[0] - p[0]))

    def iz(zv):
        return lambda p, q: (p[0] + (q[0] - p[0]) * (zv - p[1]) / (q[1] - p[1]), zv)

    pts = list(ring)
    for f, g in ((lambda p: p[0] >= b[0], ix(b[0])), (lambda p: p[0] <= b[2], ix(b[2])),
                 (lambda p: p[1] >= b[1], iz(b[1])), (lambda p: p[1] <= b[3], iz(b[3]))):
        if not pts:
            break
        pts = cut(pts, f, g)
    return pts


def area(ring):
    """Знакова площа (додатна — проти годинникової в осях x-схід, z-північ)."""
    s = 0.0
    for i in range(len(ring)):
        x0, z0 = ring[i - 1][:2]
        x1, z1 = ring[i][:2]
        s += x0 * z1 - x1 * z0
    return s / 2


def point_in(ring, x, z):
    c = False
    n = len(ring)
    for i in range(n):
        x0, z0 = ring[i - 1][:2]
        x1, z1 = ring[i][:2]
        if (z0 > z) != (z1 > z) and x < x0 + (z - z0) * (x1 - x0) / (z1 - z0):
            c = not c
    return c


def assemble_rings(ways):
    """Відкриті шляхи-члени мультиполігона (списки (id_вузла, точка)) → замкнені кільця точок.
    Зшиваємо кінець до кінця за id вузла; що не замкнулось — відкидаємо."""
    rings, open_ = [], []
    for w in ways:
        if len(w) >= 4 and w[0][0] == w[-1][0]:
            rings.append([p for _, p in w[:-1]])
        elif len(w) >= 2:
            open_.append(list(w))
    while open_:
        cur = open_.pop()
        changed = True
        while changed and cur[0][0] != cur[-1][0]:
            changed = False
            for k, w in enumerate(open_):
                if w[0][0] == cur[-1][0]:
                    cur += w[1:]
                elif w[-1][0] == cur[-1][0]:
                    cur += w[::-1][1:]
                elif w[-1][0] == cur[0][0]:
                    cur = w[:-1] + cur
                elif w[0][0] == cur[0][0]:
                    cur = w[::-1][:-1] + cur
                else:
                    continue
                open_.pop(k)
                changed = True
                break
        if cur[0][0] == cur[-1][0] and len(cur) >= 4:
            rings.append([p for _, p in cur[:-1]])
    return rings
