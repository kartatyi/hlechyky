// «Чумацький край» — мапа етапу 1 з data/fura/maps/kray.json. Сервер бере з того самого файлу лише граф і зони,
// а рельєф, дороги в ньому й декор браузер будує сам, детерміновано із seed — тож у всіх однаково.
//
// Порядок: пагорби (шум) → долина річки → профілі доріг (згладжені, з обмеженням ухилу, рівні на вузлах) →
// дороги «врізаються» в рельєф (насип/виїмка) → двори вирівнюються → Rapier heightfield 4 м + чанки мешів →
// мости над річкою, вибоїни на сільських і ґрунтовках, хати, міські будинки, склади, лісосмуги.
//
// Осі ті самі, що в truck.js: x — схід, z — північ, y — вгору. Висоти heightfield Rapier бере стовпцями по X:
// індекс = ix·(N+1) + iz (перевірено на малому полі).
//
// Дорога 2.0 (B2): дороги, перехрестя, мости, відбійники — road.js (функція від ребра графа, не від краю); усе
// нерухоме — у пакет по чанках 240 м (batch.js), матеріали — materials.js. Рельєф під корідором дороги рівний на
// всю ширину з узбіччям (+6 м), біля перехресть — на площині перехрестя: фізика й око бачать той самий профіль.

import { makeMaterials, PAL } from './materials.js';
import { ChunkBatch } from './batch.js';
import { planRoads, drawRoads, LIFT } from './road.js';

const CELL = 4;
const CH = 60;                    // чанк рельєфу й пакета: 60 клітин = 240 м
const FAR = 1000;                 // далі чанк не малюємо, поки нема туману; з туманом — межа = кінець туману (sync)

// ---------------------------------------------------------------- шум
function hash2(ix, iz, seed) {
  let h = (ix * 374761393 + iz * 668265263 + seed * 2147483647) | 0;
  h = Math.imul(h ^ (h >>> 13), 1274126177);
  return ((h ^ (h >>> 16)) >>> 0) / 4294967296;
}
function vnoise(x, z, seed) {
  const ix = Math.floor(x), iz = Math.floor(z), fx = x - ix, fz = z - iz;
  const u = fx * fx * (3 - 2 * fx), v = fz * fz * (3 - 2 * fz);
  const a = hash2(ix, iz, seed), b = hash2(ix + 1, iz, seed), c = hash2(ix, iz + 1, seed), d = hash2(ix + 1, iz + 1, seed);
  return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v;
}
function rng(seed) {
  let s = seed >>> 0;
  return () => { s = (s + 0x6D2B79F5) >>> 0; let t = s; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };
}
const smooth = (a, b, x) => { const t = Math.max(0, Math.min(1, (x - a) / (b - a))); return t * t * (3 - 2 * t); };
const lerp = (a, b, t) => a + (b - a) * t;

// ---------------------------------------------------------------- криві
/** Catmull-Rom через точки, крок ~step м: [{x, z}] з дотичною. */
function curve(points, step) {
  const out = [];
  const P = points;
  for (let i = 0; i < P.length - 1; i++) {
    const p0 = P[Math.max(0, i - 1)], p1 = P[i], p2 = P[i + 1], p3 = P[Math.min(P.length - 1, i + 2)];
    const len = Math.hypot(p2[0] - p1[0], p2[1] - p1[1]);
    const n = Math.max(1, Math.ceil(len / step));
    for (let k = 0; k < n; k++) {
      const t = k / n, t2 = t * t, t3 = t2 * t;
      const f = (a, b, c, d) => 0.5 * (2 * b + (-a + c) * t + (2 * a - 5 * b + 4 * c - d) * t2 + (-a + 3 * b - 3 * c + d) * t3);
      out.push({ x: f(p0[0], p1[0], p2[0], p3[0]), z: f(p0[1], p1[1], p2[1], p3[1]) });
    }
  }
  const last = P[P.length - 1];
  out.push({ x: last[0], z: last[1] });
  let s = 0;
  for (let i = 0; i < out.length; i++) {
    const a = out[Math.max(0, i - 1)], b = out[Math.min(out.length - 1, i + 1)];
    const l = Math.hypot(b.x - a.x, b.z - a.z) || 1;
    out[i].tx = (b.x - a.x) / l; out[i].tz = (b.z - a.z) / l;
    if (i > 0) s += Math.hypot(out[i].x - out[i - 1].x, out[i].z - out[i - 1].z);
    out[i].s = s;
  }
  return out;
}
/** Ковзне середнє з вікном ±w зразків (тричі — майже Гаус), кінці не тягнемо. */
function blur(y, w, passes = 3) {
  const n = y.length;
  let a = Float64Array.from(y);
  for (let p = 0; p < passes; p++) {
    const pre = new Float64Array(n + 1);
    for (let i = 0; i < n; i++) pre[i + 1] = pre[i] + a[i];
    const b = new Float64Array(n);
    for (let i = 0; i < n; i++) { const l = Math.max(0, i - w), r = Math.min(n - 1, i + w); b[i] = (pre[r + 1] - pre[l]) / (r - l + 1); }
    a = b;
  }
  return a;
}

// ---------------------------------------------------------------- будівник
export function buildKray(THREE, RAPIER, scene, world, quality, map) {
  const t0 = performance.now();
  const seed = map.seed | 0;
  const rnd = rng(seed * 7 + 3);
  const B = map.bounds;
  const N = Math.round((B.x1 - B.x0) / CELL);         // клітин по X і по Z (мапа квадратна)
  const S = N + 1;
  const X = (ix) => B.x0 + ix * CELL, Z = (iz) => B.z0 + iz * CELL;
  const I = (ix, iz) => ix * S + iz;

  const M = makeMaterials(THREE, quality);
  const batch = new ChunkBatch(THREE, { x0: B.x0, z0: B.z0, size: CH * CELL, uvKeys: ['road'], uvScale: 4 });
  const fixed = (x, y, z, yaw) => {
    const d = RAPIER.RigidBodyDesc.fixed().setTranslation(x, y, z);
    if (yaw) d.setRotation({ x: 0, y: Math.sin(yaw / 2), z: 0, w: Math.cos(yaw / 2) });
    return world.createRigidBody(d);
  };

  // ---- 1. пагорби
  const base = (x, z) =>
    62 * (vnoise(x / 760, z / 760, seed) - 0.5) + 24 * (vnoise(x / 280 + 31, z / 280 - 17, seed + 1) - 0.5)
    + 4 * (vnoise(x / 90 + 7, z / 90 + 3, seed + 2) - 0.5) + (z - x) * 0.008;
  const H0 = new Float32Array(S * S);                  // без річки й доріг — з нього беремо профілі доріг
  for (let ix = 0; ix < S; ix++) for (let iz = 0; iz < S; iz++) H0[I(ix, iz)] = base(X(ix), Z(iz));
  const H = Float32Array.from(H0);
  const sampleGrid = (A, x, z) => {
    const fx = Math.max(0, Math.min(N - 1e-6, (x - B.x0) / CELL)), fz = Math.max(0, Math.min(N - 1e-6, (z - B.z0) / CELL));
    const ix = Math.floor(fx), iz = Math.floor(fz), u = fx - ix, v = fz - iz;
    const a = A[I(ix, iz)], b = A[I(ix + 1, iz)], c = A[I(ix, iz + 1)], d = A[I(ix + 1, iz + 1)];
    return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v;
  };

  // штамп: для кожної точки полілінії — клітини в радіусі r; лишаємо найближчу (відстань і номер точки)
  function stamp(pts, r) {
    const dist = new Float32Array(S * S).fill(Infinity), who = new Int32Array(S * S).fill(-1);
    const rc = Math.ceil(r / CELL);
    pts.forEach((p, k) => {
      const cx = Math.round((p.x - B.x0) / CELL), cz = Math.round((p.z - B.z0) / CELL);
      for (let ix = Math.max(0, cx - rc); ix <= Math.min(N, cx + rc); ix++) {
        for (let iz = Math.max(0, cz - rc); iz <= Math.min(N, cz + rc); iz++) {
          const d = Math.hypot(X(ix) - p.x, Z(iz) - p.z);
          const j = I(ix, iz);
          if (d < r && d < dist[j]) { dist[j] = d; who[j] = k; }
        }
      }
    });
    return { dist, who };
  }

  // ---- 2. долина річки: дно спадає за течією, береги положисті
  const river = curve(map.river.pts, 6);
  const rw = map.river.width;
  {
    let bed = blur(river.map((p) => sampleGrid(H0, p.x, p.z)), 40).map((y) => y - 7);
    for (let i = 1; i < bed.length; i++) bed[i] = Math.min(bed[i], bed[i - 1] - 0.002 * 6);   // лише вниз за течією
    river.forEach((p, i) => { p.y = bed[i]; });
    const st = stamp(river, rw / 2 + 120);
    for (let j = 0; j < H.length; j++) {
      if (st.who[j] < 0) continue;
      const d = st.dist[j], bedY = river[st.who[j]].y;
      const bank = bedY + 2.2 + Math.max(0, d - rw / 2) * 0.09;      // береги ~5° від води вгору
      const k = 1 - smooth(rw / 2, rw / 2 + 120, d);
      H[j] = d <= rw / 2 ? bedY : lerp(H[j], Math.min(H[j], bank), k);
    }
  }
  const riverDist = stamp(river, rw / 2 + 14).dist;

  // ---- 3. дороги: криві, профілі, рівні вузли
  const kinds = map.kinds;
  const nodeY = {};
  for (const [id, [x, z]] of Object.entries(map.nodes)) {
    let s = 0, n = 0;
    for (let a = 0; a < 8; a++) for (const r of [0, 15, 30]) { s += sampleGrid(H0, x + Math.cos(a) * r, z + Math.sin(a) * r); n++; }
    nodeY[id] = s / n;
  }
  const places = map.places.map((p) => ({ ...p }));
  const roads = map.roads.map((r) => ({ ...r }));
  // під'їзди: від вузла місця до найближчого краю двору
  for (const p of places) {
    const [nx, nz] = map.nodes[p.node];
    const y = p.yard;
    const ex = Math.max(y.x - y.w / 2 + 6, Math.min(y.x + y.w / 2 - 6, nx));
    const ez = Math.max(y.z - y.d / 2 + 6, Math.min(y.z + y.d / 2 - 6, nz));
    p.gate = [ex, ez];
    p.y = nodeY[p.node];
    roads.push({ id: 'yard-' + p.id, kind: 'yard', a: p.node, bxz: [ex, ez], via: [] });
  }
  const kindOf = (r) => (r.kind === 'yard' ? { width: 9, speed: 30, bumps: 0, title: 'під\'їзд' } : kinds[r.kind]);
  // профіль: згладити (вікно ±w зразків по 4 м) і обмежити ухил
  const profile = (pts, w, grade) => {
    const y = blur(pts.map((p) => sampleGrid(H0, p.x, p.z)), w);
    const n = y.length, step = pts[n - 1].s / Math.max(1, n - 1) || 4;
    for (let i = 1; i < n; i++) y[i] = Math.max(y[i - 1] - grade * step, Math.min(y[i - 1] + grade * step, y[i]));
    for (let i = n - 2; i >= 0; i--) y[i] = Math.max(y[i + 1] - grade * step, Math.min(y[i + 1] + grade * step, y[i]));
    return y;
  };
  for (const r of roads) {
    const a = map.nodes[r.a], b = r.bxz || map.nodes[r.b];
    r.pts = curve([a, ...(r.via || []), b], 4);
    r.width = kindOf(r).width;
  }
  // Траса — одним профілем через усі свої вузли: кожен шматок окремо згладжувався до висоти вузла зі своїм ухилом,
  // і у вузлі ухил ламався — на 80 км/год причіп підкидало (25.10, j4). Вузли траси беруть висоту з цього профілю.
  const chain = roads.filter((r) => r.kind === 'highway');
  if (chain.length) {
    const all = [];
    chain.forEach((r, ci) => r.pts.forEach((p, i) => { if (ci === 0 || i > 0) all.push({ p, r, i }); }));
    let s = 0;
    const seq = all.map((q, i) => { if (i) s += Math.hypot(q.p.x - all[i - 1].p.x, q.p.z - all[i - 1].p.z); return { x: q.p.x, z: q.p.z, s }; });
    const y = profile(seq, 34, 0.05);
    all.forEach((q, i) => { q.p.y = y[i]; });
    // перша точка кожного наступного шматка — та сама, що остання попереднього (у all її не брали)
    for (let ci = 1; ci < chain.length; ci++) chain[ci].pts[0].y = chain[ci - 1].pts[chain[ci - 1].pts.length - 1].y;
    for (const r of chain) { nodeY[r.a] = r.pts[0].y; nodeY[r.b] = r.pts[r.pts.length - 1].y; }
  }
  for (const r of roads) {
    if (r.kind !== 'highway') {
      const w = r.kind === 'dirt' ? 6 : 14, grade = r.kind === 'dirt' ? 0.1 : 0.07;
      const y = profile(r.pts, w, grade), n = y.length;
      // кінці — точно на висоту вузлів (лінійна поправка вздовж дороги): так бічна дорога сходиться з трасою рівно
      const ya = nodeY[r.a], yb = r.kind === 'yard' ? nodeY[r.a] : nodeY[r.b];
      const da = ya - y[0], db = yb - y[n - 1], L = r.pts[n - 1].s || 1;
      r.pts.forEach((p, i) => { p.y = y[i] + lerp(da, db, p.s / L); });
    }
    // міст: де дорога над річкою
    r.pts.forEach((p) => { p.bridge = riverDist[I(Math.round((p.x - B.x0) / CELL), Math.round((p.z - B.z0) / CELL))] < rw / 2 + 8; });
  }
  for (const p of places) p.y = nodeY[p.node];

  // дорога 2.0: ребра графа з атрибутами (клас — kind; смуги, узбіччя, відбійник — необов'язкові поля kinds у
  // kray.json, яких сервер не читає), мости — з позначок над річкою (як колайдери настилу нижче), під'їзд до двору
  // — розширюється на в'їзді й закінчується на межі двору
  const placeOf = Object.fromEntries(places.map((p) => [p.id, p]));
  const net = { nodes: Object.fromEntries(Object.entries(map.nodes).map(([id, [x, z]]) => [id, { x, z }])), edges: [] };
  for (const r of roads) {
    const k = r.kind === 'yard' ? {} : kinds[r.kind] || {};
    const e = { id: r.id, cls: r.kind, a: r.a, b: r.kind === 'yard' ? null : r.b, pts: r.pts, width: r.width, lanes: k.lanes, shoulder: k.shoulder, barrier: k.barrier };
    const bi = r.pts.map((p, i) => (p.bridge ? i : -1)).filter((i) => i >= 0);
    if (bi.length) e.bridges = [[r.pts[Math.max(0, bi[0] - 2)].s, r.pts[Math.min(r.pts.length - 1, bi[bi.length - 1] + 2)].s]];
    if (r.kind === 'yard') {
      const y = placeOf[r.id.slice(5)].yard;
      const inYard = (p) => Math.abs(p.x - y.x) < y.w / 2 && Math.abs(p.z - y.z) < y.d / 2;
      const k = r.pts.findIndex(inYard), inside = k > 0 ? r.pts[k] : null;
      e.endB = r.pts[r.pts.length - 1].s;
      if (inside) {            // точно на межі двору: далі — площина двору, накладка мерехтіла б
        const o = r.pts[k - 1];
        let lo = 0, hi = 1;
        for (let it = 0; it < 20; it++) { const m = (lo + hi) / 2; if (inYard({ x: o.x + (inside.x - o.x) * m, z: o.z + (inside.z - o.z) * m })) hi = m; else lo = m; }
        e.endB = o.s + (inside.s - o.s) * hi;
      }
      // розширення на в'їзді: з кожного боку — скільки влазить у двір (край розширення не звисає над травою)
      const end = inside || r.pts[r.pts.length - 1];
      const fit = (sg) => {
        let best = 0;
        for (let x = 0.5; x <= 6; x += 0.5) {
          const o = sg * (r.width / 2 + x), px = end.x - end.tz * o, pz = end.z + end.tx * o;
          if (Math.abs(px - y.x) < y.w / 2 + 1 && Math.abs(pz - y.z) < y.d / 2 + 1) best = x;
        }
        return best;
      };
      e.flareB = { len: 16, extraR: fit(1), extraL: fit(-1) };
    }
    r.edge = e;
    net.edges.push(e);
  }
  const plan = planRoads(net);           // перехрестя; висоти ребер біля вершин — на площину перехрестя

  // врізати дороги: корідор (проїзна частина + узбіччя + 6 м: так жодна клітина 4 м під узбіччям не стирчить) рівно
  // на профіль, далі плавний укіс; біля перехресть і на в'їзді у двір — ширше; над річкою — не засипати (міст)
  const roadPts = [];
  roads.forEach((r, ri) => {
    const e = r.edge, S = e.sec, L = e.L;
    r.pts.forEach((p, i) => {
      let core = S.cw + 6;
      const ta = p.s, tb = L - p.s;
      if (e.trimA > 2.5) core += 8 * (1 - smooth(e.trimA, e.trimA + 12, ta));
      if (e.trimB > 2.5 || e.flareB) core += (e.flareB ? 6 : 8) * (1 - smooth(e.trimB, e.trimB + (e.flareB ? 20 : 12), tb));
      roadPts.push({ x: p.x, z: p.z, y: p.y, w: r.width, hw: S.hw, cw: S.cw, core, ri, i, bridge: p.bridge });
    });
  });
  const roadSt = stamp(roadPts, 46);
  // Висота під дорогою — не найближчої точки профілю (на ухилі 7 % це сходинки по 28 см кожні 4 м — причіп трусило
  // безперервно), а проєкція клітини на відрізок до сусідньої точки тієї самої дороги. Біля перехрестя — ще й нахил
  // його площини вбік від осі (той самий, що в стрічки й латки road.js).
  const roadY = (j, k) => {
    const p = roadPts[k], x = X(Math.floor(j / S)), z = Z(j % S), r = roads[p.ri], rp = r.pts[p.i];
    let best = p.y, bd = Infinity, at = rp;
    for (const q of [roadPts[k - 1], roadPts[k + 1]]) {
      if (!q || q.ri !== p.ri) continue;
      const ax = q.x - p.x, az = q.z - p.z, l2 = ax * ax + az * az || 1;
      const u = Math.max(0, Math.min(1, ((x - p.x) * ax + (z - p.z) * az) / l2));
      const d = Math.hypot(p.x + ax * u - x, p.z + az * u - z);
      if (d < bd) { bd = d; best = p.y + (q.y - p.y) * u; at = { x: p.x + ax * u, z: p.z + az * u, s: rp.s + (r.pts[q.i].s - rp.s) * u }; }
    }
    return best + r.edge.tilt(at, x, z);
  };
  for (let j = 0; j < H.length; j++) {
    const k = roadSt.who[j];
    if (k < 0) continue;
    const p = roadPts[k];
    if (riverDist[j] < rw / 2 + 4) continue;
    const core = p.core, d = roadSt.dist[j];
    H[j] = lerp(roadY(j, k) - 0.02, H[j], smooth(core, core + 16, d));
  }
  // двори: рівний майданчик на висоті вузла, плавний край
  for (const p of places) {
    const y = p.yard, pad = 22;
    for (let ix = Math.max(0, Math.floor((y.x - y.w / 2 - pad - B.x0) / CELL)); ix <= Math.min(N, Math.ceil((y.x + y.w / 2 + pad - B.x0) / CELL)); ix++) {
      for (let iz = Math.max(0, Math.floor((y.z - y.d / 2 - pad - B.z0) / CELL)); iz <= Math.min(N, Math.ceil((y.z + y.d / 2 + pad - B.z0) / CELL)); iz++) {
        const dx = Math.max(0, Math.abs(X(ix) - y.x) - y.w / 2), dz = Math.max(0, Math.abs(Z(iz) - y.z) - y.d / 2);
        const d = Math.hypot(dx, dz);
        H[I(ix, iz)] = lerp(p.y - 0.02, H[I(ix, iz)], smooth(0, pad, d));
      }
    }
  }
  // залізниця — теж насип, згладжений профіль
  const rail = curve(map.railway.pts, 6);
  {
    const y = blur(rail.map((p) => sampleGrid(H, p.x, p.z)), 30);
    rail.forEach((p, i) => { p.y = y[i] + 0.6; });
    const st = stamp(rail, 22);
    for (let j = 0; j < H.length; j++) if (st.who[j] >= 0 && riverDist[j] > rw / 2 + 4) H[j] = lerp(rail[st.who[j]].y - 0.5, H[j], smooth(3, 22, st.dist[j]));
  }

  const height = (x, z) => sampleGrid(H, x, z);
  const nearRoad = (x, z) => { const j = I(Math.round((x - B.x0) / CELL), Math.round((z - B.z0) / CELL)); return roadSt.dist[j]; };
  // далі за край корідору (узбіччя) на pad м? — щоб дерева лісосмуг не ставали на узбіччя траси й на відбійник
  const offRoad = (x, z, pad) => { const j = I(Math.round((x - B.x0) / CELL), Math.round((z - B.z0) / CELL)); return roadSt.who[j] < 0 || roadSt.dist[j] > roadPts[roadSt.who[j]].cw + pad; };

  // ---- 4. фізика рельєфу
  const ground = fixed((B.x0 + B.x1) / 2, 0, (B.z0 + B.z1) / 2);
  world.createCollider(RAPIER.ColliderDesc.heightfield(N, N, H, { x: B.x1 - B.x0, y: 1, z: B.z1 - B.z0 }).setFriction(0.9), ground);

  // ---- 5. рельєф для ока: чанки з кольорами полів
  // зелень, пшениця, рілля, соняшник, лука, стерня
  const FIELD = [[0.36, 0.56, 0.24], [0.84, 0.69, 0.28], [0.48, 0.34, 0.22], [0.7, 0.66, 0.18], [0.3, 0.5, 0.22], [0.86, 0.76, 0.42]];
  const fieldOf = (x, z) => {
    const fx = Math.floor((x + 37) / 170), fz = Math.floor((z - 11) / 120);
    return FIELD[Math.floor(hash2(fx, fz, seed + 9) * FIELD.length)];
  };
  // Чанки 240 м (той самий квадрат, що в пакета доріг) із трьома рівнями деталізації: поруч — сітка 4 м (та сама,
  // що у фізики), далі — 8 і 20 м, за туманом — нічого. Між чанками різних рівнів були б щілини — їх закриває
  // «спідниця»: край чанка, опущений на 4 м.
  const LODS = quality === 'low' ? [[1, 0], [2, 200], [5, 410]] : [[1, 0], [2, 290], [5, 590]];
  const terrainMat = M.terrain;
  // під асфальтом — темне (здалеку, де глибини не вистачає, рельєф проблискує крізь дорогу — хай того ж тону),
  // під узбіччям — його колір, далі — трав'яний укіс
  const colorAt = (gx, gz) => {
    const x = X(gx), z = Z(gz), j = I(gx, gz);
    let c = fieldOf(x, z);
    const jit = (hash2(gx, gz, seed + 5) - 0.5) * 0.05;
    const rd = roadSt.dist[j], wd = riverDist[j], rp = roadPts[roadSt.who[j]];
    if (wd < rw / 2 + 10) c = [0.76, 0.7, 0.52];                           // пісок біля води
    else if (rp && rd < rp.hw) c = [0.3, 0.31, 0.32];                      // під асфальтом
    else if (rp && rd < rp.cw + 1) c = [0.5, 0.48, 0.4];                   // під узбіччям
    else if (rp && rd < rp.cw + 7) c = [c[0] * 0.6 + 0.16, c[1] * 0.7 + 0.15, c[2] * 0.6 + 0.04];   // укіс
    else if (rd < 26) c = [c[0] * 0.8 + 0.08, c[1] * 0.9 + 0.06, c[2] * 0.8];   // смуга трави вздовж дороги
    return [c[0] + jit, c[1] + jit, c[2] + jit];
  };
  const chunkGeo = (cx, cz, nx, nz, st) => {
    const mx = nx / st, mz = nz / st, row = mz + 1;
    const pos = [], col = [], idx = [];
    for (let ix = 0; ix <= mx; ix++) for (let iz = 0; iz <= mz; iz++) {
      const gx = cx + ix * st, gz = cz + iz * st;
      pos.push(X(gx), H[I(gx, gz)], Z(gz));
      col.push(...colorAt(gx, gz));
    }
    for (let ix = 0; ix < mx; ix++) for (let iz = 0; iz < mz; iz++) {
      const a = ix * row + iz, b = (ix + 1) * row + iz;
      idx.push(a, a + 1, b, b, a + 1, b + 1);
    }
    // спідниця: по периметру — опущена копія краю
    const edge = [];
    for (let iz = 0; iz <= mz; iz++) edge.push(iz);
    for (let ix = 1; ix <= mx; ix++) edge.push(ix * row + mz);
    for (let iz = mz - 1; iz >= 0; iz--) edge.push(mx * row + iz);
    for (let ix = mx - 1; ix >= 0; ix--) edge.push(ix * row);
    const base0 = pos.length / 3;
    edge.forEach((v) => { pos.push(pos[v * 3], pos[v * 3 + 1] - 4, pos[v * 3 + 2]); col.push(col[v * 3], col[v * 3 + 1], col[v * 3 + 2]); });
    for (let e = 0; e < edge.length - 1; e++) idx.push(edge[e], edge[e + 1], base0 + e + 1, edge[e], base0 + e + 1, base0 + e);
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
    geo.setAttribute('color', new THREE.Float32BufferAttribute(col, 3));
    geo.setIndex(idx);
    geo.computeVertexNormals();
    geo.computeBoundingSphere();
    return geo;
  };
  const terrainLods = [];
  for (let cx = 0; cx < N; cx += CH) {
    for (let cz = 0; cz < N; cz += CH) {
      const nx = Math.min(CH, N - cx), nz = Math.min(CH, N - cz);
      const lod = new THREE.LOD();
      for (const [st, dist] of LODS) {
        if (nx % st || nz % st) continue;
        const m = new THREE.Mesh(chunkGeo(cx, cz, nx, nz, st), terrainMat);
        m.receiveShadow = st === 1;
        lod.addLevel(m, dist);
      }
      lod.addLevel(new THREE.Object3D(), FAR + CH * CELL * 0.71);
      terrainLods.push(lod);
      lod.position.set(0, 0, 0);
      // відстань LOD рахує від позиції об'єкта — ставимо його в центр чанка, а сітку зсуваємо назад
      const ccx = X(cx + nx / 2), ccz = Z(cz + nz / 2);
      lod.position.set(ccx, 0, ccz);
      for (const l of lod.levels) l.object.position.set(-ccx, 0, -ccz);
      lod.autoUpdate = true;
      scene.add(lod);
    }
  }

  // ---- 6. дороги для ока (road.js у пакет), колайдери мостів і вибоїни
  const roadOut = drawRoads(THREE, plan, batch, { ground0: (x, z) => sampleGrid(H0, x, z), ground: height });
  const BUMP = new THREE.BoxGeometry(1.3, 0.035, 0.8), bumpGeo = new THREE.Object3D();
  for (const r of roads) {
    // міст: колайдери настилу й поручнів по відрізках (як були); вигляд — у road.js
    const bi = r.pts.map((p, i) => (p.bridge ? i : -1)).filter((i) => i >= 0);
    if (bi.length) {
      const from = Math.max(0, bi[0] - 2), to = Math.min(r.pts.length - 1, bi[bi.length - 1] + 2);
      for (let i = from; i < to; i++) {
        const a = r.pts[i], b = r.pts[i + 1];
        const len = Math.hypot(b.x - a.x, b.z - a.z), yaw = Math.atan2(b.x - a.x, b.z - a.z);
        const cx = (a.x + b.x) / 2, cz = (a.z + b.z) / 2, cy = (a.y + b.y) / 2;
        const body = fixed(cx, cy - 0.3, cz, yaw);
        const pitch = Math.atan2(b.y - a.y, len);
        body.setRotation(new THREE.Quaternion().setFromEuler(new THREE.Euler(-pitch, yaw, 0, 'YXZ')), false);
        world.createCollider(RAPIER.ColliderDesc.cuboid(r.width / 2 + 0.5, 0.3, len / 2 + 0.05).setFriction(0.9), body);
        for (const s of [1, -1]) {
          const ox = Math.cos(yaw) * s * (r.width / 2 + 0.4), oz = -Math.sin(yaw) * s * (r.width / 2 + 0.4);
          const rb = fixed(cx + ox, cy + 0.5, cz + oz, yaw);
          world.createCollider(RAPIER.ColliderDesc.cuboid(0.08, 0.5, len / 2), rb);
        }
      }
    }

    // вибоїни: невеликі горбики під одним колесом
    const kb = kindOf(r).bumps;
    if (kb > 0) {
      const every = Math.round(14 / kb);          // м між вибоїнами: сільська ~56, ґрунтовка ~18
      for (let i = 6; i < r.pts.length - 6; i += Math.max(3, Math.round(every / 4 * (0.6 + rnd() * 0.8)))) {
        const p = r.pts[i];
        if (p.bridge) continue;
        // Лягає в колію: колеса їдуть у своїй смузі (центр смуги ±¼ ширини, колеса ±1 м від нього) або посередині
        // дороги — 1,3-метрова вибоїна на ±0,75 / ±2,75 м від осі ловить і тих, і тих. Раніше — будь-де поперек,
        // і колесо то влучало, то ні: на 40 км/год било більше, ніж на 60 (25.10).
        const tracks = [-2.75, -0.75, 0.75, 2.75].filter((t) => Math.abs(t) < r.width / 2 - 0.4);
        const pick = rnd() < 0.3 ? tracks : [tracks[Math.floor(rnd() * tracks.length)]];
        for (const off of pick) {
          const x = p.x - p.tz * off, z = p.z + p.tx * off, yaw = Math.atan2(p.tx, p.tz);
          // виступає над дорогою на 6 см; півтовщина горбика з заокругленням — 6 см
          const rise = 0.06;
          const body = fixed(x, p.y + rise - 0.06, z, yaw);
          world.createCollider(RAPIER.ColliderDesc.roundCuboid(0.61, 0.02, 0.3, 0.04).setFriction(0.9), body);
          // для ока — латка темнішого асфальту (або ґрунту) з горбиком на 3 см над смугою
          bumpGeo.position.set(x, p.y + LIFT + 0.004, z); bumpGeo.rotation.set(0, yaw, 0); bumpGeo.updateMatrix();
          batch.add('road', BUMP, bumpGeo.matrix, r.kind === 'dirt' ? PAL.rut : PAL.asphaltPatch);
        }
      }
    }
  }
  // відбійники: колайдери-стіни (фура б'ється, як об стіну, — датчик вантажу рахує удар) — одне тіло на всі
  if (roadOut.guards.length) {
    const gb = fixed(0, 0, 0);
    const q = new THREE.Quaternion(), eu = new THREE.Euler();
    for (const g of roadOut.guards) {
      q.setFromEuler(eu.set(-g.pitch, g.yaw, 0, 'YXZ'));
      world.createCollider(RAPIER.ColliderDesc.cuboid(g.hx, g.hy, g.hz).setTranslation(g.x, g.y, g.z).setRotation({ x: q.x, y: q.y, z: q.z, w: q.w }).setFriction(0.3), gb);
    }
  }

  // ---- 7. річка й залізниця
  {
    const pos = [], idx = [];
    river.forEach((p, i) => {
      const nx = -p.tz, nz = p.tx, hw = rw / 2 + 3, y = p.y + 1.6;
      pos.push(p.x + nx * hw, y, p.z + nz * hw, p.x - nx * hw, y, p.z - nz * hw);
      if (i > 0) { const a = (i - 1) * 2; idx.push(a, a + 2, a + 1, a + 1, a + 2, a + 3); }
    });
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
    geo.setIndex(idx); geo.computeVertexNormals();
    scene.add(new THREE.Mesh(geo, new THREE.MeshLambertMaterial({ color: '#4f8fb8', transparent: true, opacity: 0.88 })));
  }
  {
    // залізниця — у пакет: насип-баласт (з укосами), шпали через 2,4 м, рейки коробочками
    const P = (p, o, dy) => ({ x: p.x - p.tz * o, y: p.y + dy, z: p.z + p.tx * o });
    const bal = new THREE.Color(PAL.ballast), balS = new THREE.Color(PAL.ballast).multiplyScalar(0.85);
    const steel = new THREE.Color(PAL.railSteel), sl = new THREE.Color(PAL.sleeper);
    for (let i = 1; i < rail.length; i++) {
      const a = rail[i - 1], b = rail[i];
      batch.quad('road', P(a, -1.6, 0.04), P(b, -1.6, 0.04), P(b, 1.6, 0.04), P(a, 1.6, 0.04), bal);
      batch.quad('road', P(a, 1.6, 0.04), P(b, 1.6, 0.04), P(b, 2.8, -0.45), P(a, 2.8, -0.45), balS);
      batch.quad('road', P(a, -1.6, 0.04), P(b, -1.6, 0.04), P(b, -2.8, -0.45), P(a, -2.8, -0.45), balS);
      for (const o of [-0.75, 0.75]) {
        batch.quad('metal', P(a, o - 0.04, 0.2), P(b, o - 0.04, 0.2), P(b, o + 0.04, 0.2), P(a, o + 0.04, 0.2), steel);
        batch.quad('metal', P(a, o - 0.04, 0.05), P(b, o - 0.04, 0.05), P(b, o - 0.04, 0.2), P(a, o - 0.04, 0.2), steel);
        batch.quad('metal', P(a, o + 0.04, 0.05), P(b, o + 0.04, 0.05), P(b, o + 0.04, 0.2), P(a, o + 0.04, 0.2), steel);
      }
    }
    // шпали: квади поперек через 2,4 м (лише верх — збоку їх ховає баласт)
    const L = rail[rail.length - 1].s;
    for (let s = 1; s < L; s += 2.4) {
      let k = 1; while (k < rail.length - 1 && rail[k].s < s) k++;
      const a = rail[k - 1], b = rail[k], t = (s - a.s) / (b.s - a.s || 1);
      const p = { x: a.x + (b.x - a.x) * t, z: a.z + (b.z - a.z) * t, y: a.y + (b.y - a.y) * t, tx: a.tx, tz: a.tz };
      const f = (o, d) => { const q = P(p, o, 0.09); return { x: q.x + p.tx * d, y: q.y, z: q.z + p.tz * d }; };
      batch.quad('matte', f(-1.3, -0.12), f(1.3, -0.12), f(1.3, 0.12), f(-1.3, 0.12), sl);
    }
  }

  // ---- 8. двори, вивіски, будинки
  const add = (m, shadow = true) => { m.castShadow = shadow; m.receiveShadow = true; scene.add(m); return m; };
  // будівлі — у пакет (один меш «matte» на чанк), колайдери — як були; низ стін темніший (фейкове AO, STYLE §3.4)
  const o3 = new THREE.Object3D();
  const place = (x, y, z, yaw, sx = 1, sy = 1, sz = 1) => { o3.position.set(x, y, z); o3.rotation.set(0, yaw || 0, 0); o3.scale.set(sx, sy, sz); o3.updateMatrix(); return o3.matrix; };
  const BOX = new THREE.BoxGeometry(1, 1, 1), CONE4 = new THREE.ConeGeometry(Math.SQRT1_2, 1, 4).rotateY(Math.PI / 4);
  const solid = (w, h, d, x, z, yaw, color, y0) => {
    const y = y0 != null ? y0 : Math.min(height(x - w / 2, z - d / 2), height(x + w / 2, z + d / 2), height(x - w / 2, z + d / 2), height(x + w / 2, z - d / 2)) - 0.3;
    const H2 = h + 0.3;
    batch.add('matte', BOX, place(x, y + H2 / 2, z, yaw, w, H2, d), color, (ly) => (ly < 0 ? 0.8 + 0.2 * smooth(0, 1.5, ly * H2 + H2 / 2 - 0.3) : 1));
    world.createCollider(RAPIER.ColliderDesc.cuboid(w / 2, H2 / 2, d / 2), fixed(x, y + H2 / 2, z, yaw));
    return { y };
  };
  // чотирисхилий дах: конус на 4 грані, розтягнутий на прямокутник стін із виносом ~10 %
  const roof = (w, d, x, z, yaw, y, color, h = 3.4) => batch.add('matte', CONE4, place(x, y + h / 2, z, yaw, w * 1.1, h, d * 1.1), color);
  const cyl = (rt, rb, h, seg, x, y, z, color) => batch.add('matte', new THREE.CylinderGeometry(rt, rb, h, seg), place(x, y, z, 0), color);
  const hut = (x, z, yaw) => {
    const w = 7 + rnd() * 3, d = 5.5 + rnd() * 2;
    const { y } = solid(w, 2.8, d, x, z, yaw, rnd() < 0.8 ? '#f2ede0' : '#e8dcc0');
    roof(w, d, x, z, yaw, y + 3.1, rnd() < 0.6 ? '#9a7a45' : '#8b3b2e');
  };
  const free = (x, z, r) => nearRoad(x, z) > r && riverDist[I(Math.round((x - B.x0) / CELL), Math.round((z - B.z0) / CELL))] > rw / 2 + 25
    && places.every((p) => Math.abs(x - p.yard.x) > p.yard.w / 2 + r || Math.abs(z - p.yard.z) > p.yard.d / 2 + r)
    && x > B.x0 + 20 && x < B.x1 - 20 && z > B.z0 + 20 && z < B.z1 - 20;
  const taken = [];
  const clear = (x, z, r) => taken.every((t) => Math.hypot(t[0] - x, t[1] - z) > t[2] + r);

  const yardTex = (p) => {
    const y = p.yard, c = document.createElement('canvas');
    c.width = 512; c.height = Math.round(512 * y.d / y.w);
    const g = c.getContext('2d'), r2 = rng(seed + p.id.length * 13);
    g.fillStyle = '#5f6166'; g.fillRect(0, 0, c.width, c.height);
    for (let i = 0; i < 4000; i++) { const v = (r2() - 0.5) * 0.14; g.fillStyle = v > 0 ? `rgba(255,255,255,${v})` : `rgba(0,0,0,${-v})`; g.fillRect(r2() * c.width, r2() * c.height, 2, 2); }
    // Площина лягає так само, як на полігоні: лівий край — захід, верх canvas — північ; написи — на 180°
    const px = (x) => ((x - (y.x - y.w / 2)) / y.w) * c.width, pz = (z) => (((y.z + y.d / 2) - z) / y.d) * c.height;
    g.strokeStyle = 'rgba(245,240,225,.9)'; g.lineWidth = 3; g.fillStyle = 'rgba(245,240,225,.9)';
    g.font = 'bold 22px system-ui'; g.textAlign = 'center'; g.textBaseline = 'middle';
    for (const d of p.docks) {
      const fy = Math.cos(d.yaw), half = 8;
      for (const sx of [-2.25, 2.25]) { g.beginPath(); g.moveTo(px(d.x + sx), pz(d.z - half * fy)); g.lineTo(px(d.x + sx), pz(d.z + half * fy)); g.stroke(); }
      g.beginPath(); g.moveTo(px(d.x - 2.25), pz(d.z - half * fy)); g.lineTo(px(d.x + 2.25), pz(d.z - half * fy)); g.stroke();
      g.save(); g.translate(px(d.x), pz(d.z + (half + 1.5) * fy)); g.scale(-1, -1); if (fy < 0) g.rotate(Math.PI); g.fillText(d.id, 0, 0); g.restore();
    }
    const t = new THREE.CanvasTexture(c); t.colorSpace = THREE.SRGBColorSpace; t.anisotropy = 4;
    return t;
  };
  for (const p of places) {
    const y = p.yard;
    const pl = new THREE.Mesh(new THREE.PlaneGeometry(y.w, y.d), new THREE.MeshLambertMaterial({ map: yardTex(p) }));
    pl.rotation.x = -Math.PI / 2; pl.position.set(y.x, p.y + 0.05, y.z); add(pl, false);
    taken.push([y.x, y.z, Math.hypot(y.w, y.d) / 2]);
    sign(THREE, scene, batch, p.gate[0] + 6, p.gate[1] + 6, p.y, p.short.toUpperCase(), Math.atan2(map.nodes[p.node][0] - p.gate[0], map.nodes[p.node][1] - p.gate[1]));
  }
  // будівлі місць
  const placeById = Object.fromEntries(places.map((p) => [p.id, p]));
  const behind = (p, dist) => { const y = p.yard, [gx, gz] = p.gate; const ax = y.x - gx, az = y.z - gz, l = Math.hypot(ax, az) || 1; return [y.x + ax / l * dist, y.z + az / l * dist]; };
  if (placeById.dvir) { const p = placeById.dvir, [x, z] = behind(p, 50); solid(26, 7, 14, x, z, 0, '#b5582f'); solid(10, 4, 8, x + 22, z, 0, '#efe6d2'); taken.push([x, z, 30]); }
  if (placeById.cegel) {
    const p = placeById.cegel, [x, z] = behind(p, 50);
    solid(30, 6, 12, x, z, 0, '#a5482f'); taken.push([x, z, 30]);
    for (const dx of [-8, 8]) { cyl(1.1, 1.6, 26, 8, x + dx, height(x + dx, z + 10) + 13, z + 10, PAL.brickDark); world.createCollider(RAPIER.ColliderDesc.cylinder(13, 1.4), fixed(x + dx, height(x + dx, z + 10) + 13, z + 10)); }
  }
  if (placeById.sil) {
    const p = placeById.sil, [x, z] = behind(p, 50);
    solid(40, 8, 16, x, z, 0, '#d9d4c7'); taken.push([x, z, 34]);
    for (const dz of [-12, 0, 12]) { const yy = height(x + 30, z + dz); cyl(4, 4, 16, 12, x + 30, yy + 8, z + dz, PAL.silo); world.createCollider(RAPIER.ColliderDesc.cylinder(8, 4), fixed(x + 30, yy + 8, z + dz)); }
  }
  if (placeById.medove) { const p = placeById.medove, [x, z] = behind(p, 45); const { y } = solid(22, 4, 10, x, z, 0, '#c9a46a'); roof(22, 10, x, z, 0, y + 4.3, '#8a6a3a', 3); taken.push([x, z, 25]); }
  if (placeById.honchar) { const p = placeById.honchar, [x, z] = behind(p, 45); const { y } = solid(18, 3.5, 9, x, z, 0, '#efe6d2'); roof(18, 9, x, z, 0, y + 3.8, '#9a7a45', 3); taken.push([x, z, 22]); }

  // села вздовж доріг біля Гончарівки й Медового, хутірці на трасі; місто — квартали
  const villageAt = (cx, cz, radius, count) => {
    for (let n = 0, tries = 0; n < count && tries < count * 40; tries++) {
      const r = roadPts[Math.floor(rnd() * roadPts.length)];
      if (Math.hypot(r.x - cx, r.z - cz) > radius || r.bridge) continue;
      const pr = roads[r.ri].pts[r.i];
      const side = rnd() < 0.5 ? -1 : 1, off = r.w / 2 + 12 + rnd() * 6;
      const x = r.x - pr.tz * off * side, z = r.z + pr.tx * off * side;
      if (!free(x, z, 9) || !clear(x, z, 8)) continue;
      hut(x, z, Math.atan2(pr.tx, pr.tz) + (side > 0 ? Math.PI / 2 : -Math.PI / 2));
      taken.push([x, z, 7]); n++;
    }
  };
  if (placeById.honchar) villageAt(...map.nodes[placeById.honchar.node], 320, 26);
  if (placeById.medove) villageAt(...map.nodes[placeById.medove.node], 240, 14);
  villageAt(...map.nodes.dvir, 200, 6);
  if (placeById.poltavka) {
    const [cx, cz] = map.nodes[placeById.poltavka.node];
    const colors = ['#e9dcc4', '#d8c3a5', '#c9d6df', '#e7cfc0', '#f0e6d0', '#cfd8c4'];
    for (let n = 0, tries = 0; n < (quality === 'low' ? 30 : 48) && tries < 2000; tries++) {
      const x = cx + (rnd() - 0.3) * 360, z = cz + (rnd() - 0.2) * 320;
      if (!free(x, z, 14) || !clear(x, z, 12)) continue;
      const w = 10 + rnd() * 8, d = 10 + rnd() * 6, h = 6 + Math.floor(rnd() * 4) * 3;
      const { y } = solid(w, h, d, x, z, 0, colors[Math.floor(rnd() * colors.length)]);
      batch.add('matte', BOX, place(x, y + h + 0.5, z, 0, w + 0.4, 0.4, d + 0.4), PAL.slab);
      taken.push([x, z, Math.max(w, d) * 0.7]); n++;
    }
  }

  // ---- 9. дерева: лісосмуги вздовж полів і доріг + гаї
  const trees = [];
  const tryTree = (x, z, s) => { if (free(x, z, 7) && offRoad(x, z, 2) && clear(x, z, 3)) trees.push({ x, z, y: height(x, z), s, pine: hash2(Math.round(x), Math.round(z), seed) < 0.25 }); };
  const maxTrees = quality === 'low' ? 2200 : 4200;
  for (let fx = -9; fx <= 9 && trees.length < maxTrees; fx++) {
    if (hash2(fx, 1, seed + 21) < 0.45) continue;               // не на кожній межі поля
    const x0 = fx * 170 - 37;
    for (let z = B.z0 + 30; z < B.z1 - 30; z += 7 + rnd() * 3) tryTree(x0 + (rnd() - 0.5) * 3, z, 0.8 + rnd() * 0.5);
  }
  for (let fz = -12; fz <= 12 && trees.length < maxTrees; fz++) {
    if (hash2(3, fz, seed + 22) < 0.6) continue;
    const z0 = fz * 120 + 11;
    for (let x = B.x0 + 30; x < B.x1 - 30; x += 7 + rnd() * 3) tryTree(x, z0 + (rnd() - 0.5) * 3, 0.8 + rnd() * 0.5);
  }
  for (const r of roadPts) if (trees.length < maxTrees && r.i % 3 === 0 && rnd() < 0.18) {
    const pr = roads[r.ri].pts[r.i], side = rnd() < 0.5 ? -1 : 1, off = r.w / 2 + 9 + rnd() * 4;
    tryTree(r.x - pr.tz * off * side, r.z + pr.tx * off * side, 0.9 + rnd() * 0.4);
  }
  for (let g = 0; g < 26 && trees.length < maxTrees; g++) {
    const gx = B.x0 + 100 + rnd() * (B.x1 - B.x0 - 200), gz = B.z0 + 100 + rnd() * (B.z1 - B.z0 - 200);
    for (let k = 0; k < 40; k++) tryTree(gx + (rnd() - 0.5) * 120, gz + (rnd() - 0.5) * 120, 0.8 + rnd() * 0.6);
  }
  {
    // Квартали по 600 м: інакше всі дерева мапи — один меш на весь край, і його малює кожен кадр (і тінь) повністю.
    // Стовбур і крону злито в одну геометрію з вертексним кольором: одне дерево = один інстанс, тип = один draw call.
    const merged = (parts) => {
      const pos = [], col = [], c = new THREE.Color();
      for (const [geo, dy, hex] of parts) {
        const g = geo.toNonIndexed(), P = g.attributes.position;
        c.set(hex);
        for (let i = 0; i < P.count; i++) { pos.push(P.getX(i), P.getY(i) + dy, P.getZ(i)); col.push(c.r, c.g, c.b); }
      }
      const g = new THREE.BufferGeometry();
      g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
      g.setAttribute('color', new THREE.Float32BufferAttribute(col, 3));
      g.computeVertexNormals();
      return g;
    };
    const trunk = new THREE.CylinderGeometry(0.18, 0.28, 2.2, 5);
    const leafGeo = merged([[trunk, 0, PAL.trunk], [new THREE.IcosahedronGeometry(1.8, 0), 2.2, PAL.crown]]);
    const pineGeo = merged([[trunk, 0, PAL.trunk], [new THREE.ConeGeometry(1.5, 4.4, 6), 2.6, PAL.pine]]);
    const cells = new Map();
    for (const t of trees) {
      const k = Math.floor((t.x - B.x0) / 600) + ':' + Math.floor((t.z - B.z0) / 600);
      if (!cells.has(k)) cells.set(k, []);
      cells.get(k).push(t);
      world.createCollider(RAPIER.ColliderDesc.cylinder(1.1 * t.s, 0.3 * t.s), fixed(t.x, t.y + 1.1 * t.s, t.z));
    }
    const tmp = new THREE.Object3D();
    for (const list of cells.values()) {
      const leaf = new THREE.InstancedMesh(leafGeo, M.tree, list.length);
      const pines = new THREE.InstancedMesh(pineGeo, M.tree, list.length);
      let nc = 0, np = 0;
      list.forEach((t) => {
        tmp.position.set(t.x, t.y + 1.1 * t.s, t.z); tmp.scale.setScalar(t.s); tmp.rotation.set(0, t.x % 6, 0); tmp.updateMatrix();
        if (t.pine) pines.setMatrixAt(np++, tmp.matrix); else leaf.setMatrixAt(nc++, tmp.matrix);
      });
      leaf.count = nc; pines.count = np;
      for (const m of [leaf, pines]) { if (!m.count) continue; m.castShadow = true; m.receiveShadow = true; m.computeBoundingSphere(); scene.add(m); }
    }
  }

  // ---- 10. пакет: меші по чанках (дрібне — розмітка, світлоповертачі — лише ближче 600 м)
  const packed = batch.build(scene, M, {
    shadow: { matte: [true, true], metal: [true, true], concrete: [false, true], road: [false, true], mark: [false, true], reflect: [false, false] },
    near: 600, far: FAR,
  });

  const ms = Math.round(performance.now() - t0);
  console.info('[fura] край зібрано за', ms, 'мс:', trees.length, 'дерев,', roads.length, 'доріг,', plan.junctions.length, 'перехресть,',
    Math.round(roadOut.stats.guardM), 'м відбійника,', packed.meshes, 'мешів пакета (', packed.tris, '△)');

  const depot = places.find((p) => p.depot) || places[0];
  let fogFar = 0;
  return {
    map, places, roads, river, height, nearRoad, buildMs: ms, roadPlan: plan, packed,
    spawn: { x: depot.spawn.x, z: depot.spawn.z, yaw: depot.spawn.yaw, y: height(depot.spawn.x, depot.spawn.z) },
    // кожен кадр: якщо туман посунули (небо 2.0), чанки малюються до нової межі
    sync() {
      const f = scene.fog && scene.fog.far;
      if (!f || f === fogFar) return;
      fogFar = f;
      const far = f;                    // за туманом усе одно не видно: чанк ховається, щойно весь за ним
      batch.setFar(far);
      for (const lod of terrainLods) lod.levels[lod.levels.length - 1].distance = far + CH * CELL * 0.71;
    },
    resetCones() {},
  };
}

/** Вивіска на двох стовпчиках з написом: щит і стовпчики — у пакет, окремим мешем лише лице з текстурою напису. */
function sign(THREE, scene, batch, x, z, y, text, rot) {
  const c = document.createElement('canvas');
  c.width = 512; c.height = 128;
  const g = c.getContext('2d');
  g.fillStyle = PAL.signBlue; g.fillRect(0, 0, 512, 128);
  g.strokeStyle = PAL.plaster; g.lineWidth = 8; g.strokeRect(8, 8, 496, 112);
  g.fillStyle = PAL.plaster; g.font = 'bold 54px system-ui'; g.textAlign = 'center'; g.textBaseline = 'middle';
  g.fillText(text, 256, 68, 470);
  const tex = new THREE.CanvasTexture(c);
  tex.colorSpace = THREE.SRGBColorSpace;
  const grp = new THREE.Group();
  grp.position.set(x, y, z);
  grp.rotation.y = rot;
  grp.updateMatrixWorld();
  const box = new THREE.BoxGeometry(1, 1, 1), o = new THREE.Object3D();
  const put = (px, py, pz, sx, sy, sz, color) => {
    o.position.set(px, py, pz); o.scale.set(sx, sy, sz); o.updateMatrix();
    batch.add('matte', box, new THREE.Matrix4().multiplyMatrices(grp.matrixWorld, o.matrix), color);
  };
  put(0, 2.6, 0, 5, 1.25, 0.1, PAL.signBlue);
  for (const sx of [-2, 2]) put(sx, 1.3, -0.07, 0.12, 2.6, 0.12, PAL.signPost);
  const face = new THREE.Mesh(new THREE.PlaneGeometry(5, 1.25), new THREE.MeshLambertMaterial({ map: tex }));
  face.position.set(0, 2.6, 0.052);
  grp.add(face);
  scene.add(grp);
}
