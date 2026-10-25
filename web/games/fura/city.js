// «Житомир» (D2) — місто з data/fura/maps/zhytomyr.json (граф доріг, місця, бокси — те саме, що бачить сервер) і
// web/static/fura/maps/zhytomyr-city.json (tools/osm/city.mjs: рельєф Copernicus 30 м, контури будинків і вода OSM у
// рамці місць). API рівня — той самий, що в kray.js: height, ground, unstick, places, roads, колайдери, sync; плюс lanes().
//
// Простий обсяг: дороги — road.js (вулиці з бордюром, траси, ґрунтовки), рельєф — з висот (врізаний під дороги й двори,
// як у краю), будинки — прості коробки з контурів (висота — з OSM чи за поверхами), вода — пласкі площі. Без стрімінгу:
// рамка ~10 × 7 км, сітка фізики 8 м; дороги за рамкою (виїзди на траси) не будуємо — сервер їх знає, на мапі їх нема.
//
// Осі ті самі, що в kray.js: x — схід, z — північ, y — вгору; висоти — від землі біля депо (≈ 0), а не над морем.

import { makeMaterials, PAL } from './materials.js';
import { ChunkBatch } from './batch.js';
import { planRoads, drawRoads, LIFT, at as roadAt } from './road.js';

const CELL = 8;
const CH = 30;                    // чанк рельєфу: 30 клітин = 240 м (той самий квадрат, що в пакета)
const FAR = 1000;

const lerp = (a, b, t) => a + (b - a) * t;
const smooth = (a, b, x) => { const t = Math.max(0, Math.min(1, (x - a) / (b - a))); return t * t * (3 - 2 * t); };
function hash2(ix, iz, seed) {
  let h = (ix * 374761393 + iz * 668265263 + seed * 2147483647) | 0;
  h = Math.imul(h ^ (h >>> 13), 1274126177);
  return ((h ^ (h >>> 16)) >>> 0) / 4294967296;
}
/** Catmull-Rom через точки, крок ~step м: [{x, z, tx, tz, s}] (як у kray.js). */
function curve(points, step) {
  const out = [], P = points;
  for (let i = 0; i < P.length - 1; i++) {
    const p0 = P[Math.max(0, i - 1)], p1 = P[i], p2 = P[i + 1], p3 = P[Math.min(P.length - 1, i + 2)];
    const n = Math.max(1, Math.ceil(Math.hypot(p2[0] - p1[0], p2[1] - p1[1]) / step));
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
/** Дельти (×q) → [[x, z], …]. */
function undelta(row, from, q) {
  const out = [];
  let x = 0, z = 0;
  for (let i = from; i + 1 < row.length; i += 2) { x += row[i]; z += row[i + 1]; out.push([x / q, z / q]); }
  return out;
}

// класи доріг мапи → класи road.js: «вулиця» міста — з бордюром і тротуаром
const CLS = { highway: 'highway', village: 'street', dirt: 'dirt', yard: 'yard' };
// швидкість трафіку по смугах, км/год (місто)
const LANE_KMH = { highway: 70, village: 50, dirt: 30 };

export function buildCity(THREE, RAPIER, scene, world, quality, map, city) {
  const t0 = performance.now();
  const seed = map.seed | 0;
  const CB = city.bounds;
  const NX = Math.ceil((CB.x1 - CB.x0) / CELL), NZ = Math.ceil((CB.z1 - CB.z0) / CELL);
  const B = { x0: CB.x0, z0: CB.z0, x1: CB.x0 + NX * CELL, z1: CB.z0 + NZ * CELL };
  const SX = NX + 1, SZ = NZ + 1;
  const X = (ix) => B.x0 + ix * CELL, Z = (iz) => B.z0 + iz * CELL;
  const I = (ix, iz) => ix * SZ + iz;
  const cellOf = (x, z) => I(Math.max(0, Math.min(NX, Math.round((x - B.x0) / CELL))), Math.max(0, Math.min(NZ, Math.round((z - B.z0) / CELL))));
  const inside = (x, z, pad = 0) => x > B.x0 + pad && x < B.x1 - pad && z > B.z0 + pad && z < B.z1 - pad;

  const M = makeMaterials(THREE, quality);
  const batch = new ChunkBatch(THREE, { x0: B.x0, z0: B.z0, size: CH * CELL, uvKeys: ['road'], uvScale: 4 });
  const fixed = (x, y, z, yaw) => {
    const d = RAPIER.RigidBodyDesc.fixed().setTranslation(x, y, z);
    if (yaw) d.setRotation({ x: 0, y: Math.sin(yaw / 2), z: 0, w: Math.cos(yaw / 2) });
    return world.createRigidBody(d);
  };

  // ---- 1. рельєф: сітка Copernicus 30 м → 8 м білінійно; нуль — земля біля депо
  const HG = city.height, raw = Uint8Array.from(atob(HG.data), (c) => c.charCodeAt(0));
  const dv = new DataView(raw.buffer);
  const dem = (x, z) => {
    const fx = Math.max(0, Math.min(HG.nx - 1.000001, (x - HG.x0) / HG.step)), fz = Math.max(0, Math.min(HG.nz - 1.000001, (z - HG.z0) / HG.step));
    const ix = Math.floor(fx), iz = Math.floor(fz), u = fx - ix, v = fz - iz;
    const g = (i, j) => dv.getUint16(((iz + j) * HG.nx + ix + i) * 2, true);
    return HG.y0 + HG.scale * (g(0, 0) * (1 - u) * (1 - v) + g(1, 0) * u * (1 - v) + g(0, 1) * (1 - u) * v + g(1, 1) * u * v);
  };
  const depot0 = map.places.find((p) => p.depot) || map.places[0];
  const Y0 = dem(depot0.yard.x, depot0.yard.z);
  const H0 = new Float32Array(SX * SZ);
  for (let ix = 0; ix < SX; ix++) for (let iz = 0; iz < SZ; iz++) H0[I(ix, iz)] = dem(X(ix), Z(iz)) - Y0;
  const H = Float32Array.from(H0);
  const sampleGrid = (A, x, z) => {
    const fx = Math.max(0, Math.min(NX - 1e-6, (x - B.x0) / CELL)), fz = Math.max(0, Math.min(NZ - 1e-6, (z - B.z0) / CELL));
    const ix = Math.floor(fx), iz = Math.floor(fz), u = fx - ix, v = fz - iz;
    const a = A[I(ix, iz)], b = A[I(ix + 1, iz)], c = A[I(ix, iz + 1)], d = A[I(ix + 1, iz + 1)];
    return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v;
  };
  function stamp(pts, r) {
    const dist = new Float32Array(SX * SZ).fill(Infinity), who = new Int32Array(SX * SZ).fill(-1);
    const rc = Math.ceil(r / CELL);
    pts.forEach((p, k) => {
      const cx = Math.round((p.x - B.x0) / CELL), cz = Math.round((p.z - B.z0) / CELL);
      for (let ix = Math.max(0, cx - rc); ix <= Math.min(NX, cx + rc); ix++) {
        for (let iz = Math.max(0, cz - rc); iz <= Math.min(NZ, cz + rc); iz++) {
          const d = Math.hypot(X(ix) - p.x, Z(iz) - p.z), j = I(ix, iz);
          if (d < r && d < dist[j]) { dist[j] = d; who[j] = k; }
        }
      }
    });
    return { dist, who };
  }

  // ---- 2. дороги: лише ті, що цілком у рамці; профілі з рельєфу, кінці — на висоту вузлів
  const kinds = map.kinds;
  const nodeY = {};
  for (const [id, [x, z]] of Object.entries(map.nodes)) {
    let s = 0, n = 0;
    for (let a = 0; a < 8; a++) for (const r of [0, 15, 30]) { s += sampleGrid(H0, x + Math.cos(a) * r, z + Math.sin(a) * r); n++; }
    nodeY[id] = s / n;
  }
  const places = map.places.map((p) => ({ ...p }));
  const roads = map.roads.filter((r) => [map.nodes[r.a], ...(r.via || []), map.nodes[r.b]].every(([x, z]) => inside(x, z, 40))).map((r) => ({ ...r }));
  for (const p of places) {
    const [nx, nz] = map.nodes[p.node], y = p.yard;
    const ex = Math.max(y.x - y.w / 2 + 6, Math.min(y.x + y.w / 2 - 6, nx));
    const ez = Math.max(y.z - y.d / 2 + 6, Math.min(y.z + y.d / 2 - 6, nz));
    p.gate = [ex, ez];
    p.y = nodeY[p.node];
    roads.push({ id: 'yard-' + p.id, kind: 'yard', a: p.node, bxz: [ex, ez], via: [] });
  }
  const kindOf = (r) => (r.kind === 'yard' ? { width: 9, speed: 30 } : kinds[r.kind] || kinds.village);
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
    const w = r.kind === 'highway' ? 30 : r.kind === 'dirt' ? 6 : 14, grade = r.kind === 'dirt' ? 0.1 : 0.07;
    const y = profile(r.pts, w, grade), n = y.length;
    const ya = nodeY[r.a], yb = r.kind === 'yard' ? nodeY[r.a] : nodeY[r.b];
    const da = ya - y[0], db = yb - y[n - 1], L = r.pts[n - 1].s || 1;
    r.pts.forEach((p, i) => { p.y = y[i] + lerp(da, db, p.s / L); });
  }

  const placeOf = Object.fromEntries(places.map((p) => [p.id, p]));
  const net = { nodes: Object.fromEntries(Object.entries(map.nodes).map(([id, [x, z]]) => [id, { x, z }])), edges: [] };
  for (const r of roads) {
    const k = r.kind === 'yard' ? {} : kinds[r.kind] || {};
    const e = { id: r.id, cls: CLS[r.kind] || 'street', a: r.a, b: r.kind === 'yard' ? null : r.b, pts: r.pts, width: r.width, lanes: k.lanes, shoulder: k.shoulder, barrier: k.barrier };
    if (r.kind === 'yard') {
      const y = placeOf[r.id.slice(5)].yard;
      const inYard = (p) => Math.abs(p.x - y.x) < y.w / 2 && Math.abs(p.z - y.z) < y.d / 2;
      const kk = r.pts.findIndex(inYard), inn = kk > 0 ? r.pts[kk] : null;
      e.endB = r.pts[r.pts.length - 1].s;
      if (inn) {
        const o = r.pts[kk - 1];
        let lo = 0, hi = 1;
        for (let it = 0; it < 20; it++) { const m = (lo + hi) / 2; if (inYard({ x: o.x + (inn.x - o.x) * m, z: o.z + (inn.z - o.z) * m })) hi = m; else lo = m; }
        e.endB = o.s + (inn.s - o.s) * hi;
      }
      const end = inn || r.pts[r.pts.length - 1];
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
  const plan = planRoads(net);

  // ---- 3. врізати дороги й двори в рельєф (як у kray.js)
  const roadPts = [];
  roads.forEach((r, ri) => {
    const e = r.edge, S = e.sec, L = e.L;
    r.pts.forEach((p, i) => {
      let core = S.cw + 6;
      if (e.trimA > 2.5) core += 8 * (1 - smooth(e.trimA, e.trimA + 12, p.s));
      if (e.trimB > 2.5 || e.flareB) core += (e.flareB ? 6 : 8) * (1 - smooth(e.trimB, e.trimB + (e.flareB ? 20 : 12), L - p.s));
      roadPts.push({ x: p.x, z: p.z, y: p.y, w: r.width, hw: S.hw, cw: S.cw, core, ri, i });
    });
  });
  const roadSt = stamp(roadPts, 46);
  const roadY = (j, k) => {
    const p = roadPts[k], x = X(Math.floor(j / SZ)), z = Z(j % SZ), r = roads[p.ri], rp = r.pts[p.i];
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
    H[j] = lerp(roadY(j, k) - 0.02, H[j], smooth(p.core, p.core + 16, roadSt.dist[j]));
  }
  for (const p of places) {
    const y = p.yard, pad = 24;
    for (let ix = Math.max(0, Math.floor((y.x - y.w / 2 - pad - B.x0) / CELL)); ix <= Math.min(NX, Math.ceil((y.x + y.w / 2 + pad - B.x0) / CELL)); ix++) {
      for (let iz = Math.max(0, Math.floor((y.z - y.d / 2 - pad - B.z0) / CELL)); iz <= Math.min(NZ, Math.ceil((y.z + y.d / 2 + pad - B.z0) / CELL)); iz++) {
        const dx = Math.max(0, Math.abs(X(ix) - y.x) - y.w / 2), dz = Math.max(0, Math.abs(Z(iz) - y.z) - y.d / 2);
        H[I(ix, iz)] = lerp(p.y - 0.02, H[I(ix, iz)], smooth(0, pad, Math.hypot(dx, dz)));
      }
    }
  }
  const height = (x, z) => sampleGrid(H, x, z);
  const nearRoad = (x, z) => roadSt.dist[cellOf(x, z)];
  const offRoad = (x, z, pad) => { const j = cellOf(x, z); return roadSt.who[j] < 0 || roadSt.dist[j] > roadPts[roadSt.who[j]].cw + pad; };

  // ---- 4. фізика рельєфу (рядки heightfield — уздовж Z, стовпці — X: індекс ix·(NZ+1) + iz)
  const surface = new Set();
  const groundBody = fixed((B.x0 + B.x1) / 2, 0, (B.z0 + B.z1) / 2);
  surface.add(world.createCollider(RAPIER.ColliderDesc.heightfield(NZ, NX, H, { x: B.x1 - B.x0, y: 1, z: B.z1 - B.z0 }).setFriction(0.9), groundBody).handle);

  // ---- 5. рельєф для ока: чанки 240 м, рівні 8 / 16 / 40 м, «спідниця» між рівнями
  const LODS = quality === 'low' ? [[1, 0], [2, 220], [5, 440]] : [[1, 0], [2, 320], [5, 620]];
  const GREEN = [[0.42, 0.52, 0.3], [0.47, 0.55, 0.32], [0.5, 0.5, 0.36], [0.38, 0.48, 0.28]];
  const colorAt = (gx, gz) => {
    const j = I(gx, gz);
    let c = GREEN[Math.floor(hash2(Math.floor(gx / 9), Math.floor(gz / 9), seed + 9) * GREEN.length)];
    const jit = (hash2(gx, gz, seed + 5) - 0.5) * 0.05;
    const rd = roadSt.dist[j], rp = roadPts[roadSt.who[j]];
    if (rp && rd < rp.hw) c = [0.3, 0.31, 0.32];
    else if (rp && rd < rp.cw + 1) c = [0.55, 0.54, 0.5];                        // тротуар / узбіччя
    else if (rp && rd < rp.cw + 8) c = [c[0] * 0.8 + 0.08, c[1] * 0.85 + 0.06, c[2] * 0.8 + 0.04];
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
  for (let cx = 0; cx < NX; cx += CH) {
    for (let cz = 0; cz < NZ; cz += CH) {
      const nx = Math.min(CH, NX - cx), nz = Math.min(CH, NZ - cz);
      const lod = new THREE.LOD();
      for (const [st, dist] of LODS) {
        if (nx % st || nz % st) continue;
        const m = new THREE.Mesh(chunkGeo(cx, cz, nx, nz, st), M.terrain);
        m.receiveShadow = st === 1;
        lod.addLevel(m, dist);
      }
      if (!lod.levels.length) lod.addLevel(new THREE.Mesh(chunkGeo(cx, cz, nx, nz, 1), M.terrain), 0);
      lod.addLevel(new THREE.Object3D(), FAR + CH * CELL * 0.71);
      terrainLods.push(lod);
      const ccx = X(cx + nx / 2), ccz = Z(cz + nz / 2);
      lod.position.set(ccx, 0, ccz);
      for (const l of lod.levels) l.object.position.set(-ccx, 0, -ccz);
      scene.add(lod);
    }
  }

  // ---- 6. дороги для ока й відбійники
  const roadOut = drawRoads(THREE, plan, batch, { ground0: (x, z) => sampleGrid(H0, x, z), ground: height });
  if (roadOut.guards.length) {
    const gb = fixed(0, 0, 0);
    const q = new THREE.Quaternion(), eu = new THREE.Euler();
    for (const g of roadOut.guards) {
      q.setFromEuler(eu.set(-g.pitch, g.yaw, 0, 'YXZ'));
      world.createCollider(RAPIER.ColliderDesc.cuboid(g.hx, g.hy, g.hz).setTranslation(g.x, g.y, g.z).setRotation({ x: q.x, y: q.y, z: q.z, w: q.w }).setFriction(0.3), gb);
    }
  }

  // ---- 7. вода: пласкі площі на висоті найнижчого берега (дороги через воду — на насипі, мостів у простому обсязі нема)
  {
    const pos = [];
    for (const row of city.water) {
      const pts = undelta(row, 0, 1);
      if (pts.length < 3) continue;
      let y = Infinity;
      for (const [x, z] of pts) y = Math.min(y, height(x, z));
      y += 0.25;
      const v2 = pts.map(([x, z]) => new THREE.Vector2(x, z));
      for (const [a, b, c] of THREE.ShapeUtils.triangulateShape(v2, [])) for (const k of [a, b, c]) pos.push(pts[k][0], y, pts[k][1]);
    }
    if (pos.length) {
      const geo = new THREE.BufferGeometry();
      geo.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
      geo.computeVertexNormals();
      const w = new THREE.Mesh(geo, new THREE.MeshStandardMaterial({ color: PAL.water, transparent: true, opacity: 0.9, roughness: 0.12, metalness: 0, side: THREE.DoubleSide }));
      w.name = 'water';
      scene.add(w);
    }
  }

  // ---- 8. двори й вивіски
  const yardTex = (p) => {
    const y = p.yard, c = document.createElement('canvas');
    c.width = 512; c.height = Math.round(512 * y.d / y.w);
    const g = c.getContext('2d');
    g.fillStyle = '#5f6166'; g.fillRect(0, 0, c.width, c.height);
    for (let i = 0; i < 4000; i++) { const v = (hash2(i, p.id.length, seed) - 0.5) * 0.14; g.fillStyle = v > 0 ? `rgba(255,255,255,${v})` : `rgba(0,0,0,${-v})`; g.fillRect(hash2(i, 1, seed) * c.width, hash2(i, 2, seed) * c.height, 2, 2); }
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
    const pl = new THREE.Mesh(new THREE.PlaneGeometry(y.w, y.d), new THREE.MeshStandardMaterial({ map: yardTex(p), roughness: 0.9 }));
    pl.rotation.x = -Math.PI / 2; pl.position.set(y.x, p.y + 0.05, y.z); pl.receiveShadow = true;
    scene.add(pl);
    sign(THREE, scene, batch, p.gate[0] + 6, p.gate[1] + 6, p.y, p.short.toUpperCase(), Math.atan2(map.nodes[p.node][0] - p.gate[0], map.nodes[p.node][1] - p.gate[1]));
  }

  // ---- 9. будинки: коробки з контурів (стіни + плаский дах), низ — на найнижчу точку землі під контуром.
  // Не ставимо на дорогу (OSM і граф сервера розходяться на метри) і в двори; колайдер — повернутий прямокутник уздовж
  // найдовшої стіни, лише ближче 46 м до дороги (далі фура не доїде — колайдерів менше).
  const COLORS = ['#e9dcc4', '#d8c3a5', '#c9d6df', '#e7cfc0', '#f0e6d0', '#cfd8c4', '#d9d4c7', '#bfb6a8'];
  const ROOFS = ['#6f6a64', '#7d5a48', '#8b8780', '#5f6a70'];
  const inYard = (x, z, pad) => places.some((p) => Math.abs(x - p.yard.x) < p.yard.w / 2 + pad && Math.abs(z - p.yard.z) < p.yard.d / 2 + pad);
  const blocked = (x, z) => { const j = cellOf(x, z), k = roadSt.who[j]; return (k >= 0 && roadSt.dist[j] < roadPts[k].cw + 2.5) || inYard(x, z, 6); };
  let nb = 0, ncol = 0;
  const houses = fixed(0, 0, 0);       // усі колайдери будинків — на одному тілі: тисячі тіл коштували ~1 мс на крок світу
  for (const row of city.buildings) {
    const h = row[0] / 10, pts = undelta(row, 1, 10);
    if (pts.length < 3) continue;
    let cx = 0, cz = 0;
    for (const [x, z] of pts) { cx += x / pts.length; cz += z / pts.length; }
    if (!inside(cx, cz, 10)) continue;
    // ребра теж: короткі стіни (дрібні будинки) — вершин досить, довгі — ще й середина
    let bad = blocked(cx, cz);
    for (let i = 0; i < pts.length && !bad; i++) {
      const [ax, az] = pts[i], [bx, bz] = pts[(i + 1) % pts.length];
      const n = Math.max(1, Math.ceil(Math.hypot(bx - ax, bz - az) / 4));
      for (let t = 0; t < n && !bad; t++) bad = blocked(ax + (bx - ax) * t / n, az + (bz - az) * t / n);
    }
    if (bad) continue;
    let y0 = Infinity;
    for (const [x, z] of pts) y0 = Math.min(y0, height(x, z));
    y0 -= 0.4;
    const y1 = y0 + 0.4 + h, hc = hash2(Math.round(cx), Math.round(cz), seed + 41);
    const wall = new THREE.Color(COLORS[Math.floor(hc * COLORS.length)]), low = wall.clone().multiplyScalar(0.8);
    for (let i = 0; i < pts.length; i++) {
      const [ax, az] = pts[i], [bx, bz] = pts[(i + 1) % pts.length];
      const a = { x: ax, y: y0, z: az }, b = { x: bx, y: y0, z: bz }, c = { x: bx, y: y1, z: bz }, d = { x: ax, y: y1, z: az };
      batch.tri('matte', a, b, c, [low, low, wall]); batch.tri('matte', a, c, d, [low, wall, wall]);
    }
    const roofC = h > 10 ? PAL.slab || '#8b8780' : ROOFS[Math.floor(hash2(Math.round(cz), Math.round(cx), seed + 3) * ROOFS.length)];
    const v2 = pts.map(([x, z]) => new THREE.Vector2(x, z));
    for (const [a, b, c] of THREE.ShapeUtils.triangulateShape(v2, [])) {
      batch.tri('matte', { x: pts[a][0], y: y1, z: pts[a][1] }, { x: pts[b][0], y: y1, z: pts[b][1] }, { x: pts[c][0], y: y1, z: pts[c][1] }, roofC);
    }
    nb++;
    if (nearRoad(cx, cz) < 46) {
      // прямокутник уздовж найдовшої стіни, що охоплює контур
      let best = 0, ux = 1, uz = 0;
      for (let i = 0; i < pts.length; i++) {
        const [ax, az] = pts[i], [bx, bz] = pts[(i + 1) % pts.length], l = Math.hypot(bx - ax, bz - az);
        if (l > best) { best = l; ux = (bx - ax) / l; uz = (bz - az) / l; }
      }
      let a0 = Infinity, a1 = -Infinity, b0 = Infinity, b1 = -Infinity;
      for (const [x, z] of pts) { const a = (x - cx) * ux + (z - cz) * uz, b = -(x - cx) * uz + (z - cz) * ux; a0 = Math.min(a0, a); a1 = Math.max(a1, a); b0 = Math.min(b0, b); b1 = Math.max(b1, b); }
      const am = (a0 + a1) / 2, bm = (b0 + b1) / 2;
      const px = cx + am * ux - bm * uz, pz = cz + am * uz + bm * ux;
      // курс коробки: її локальна X — уздовж стіни (ux, uz); поворот навколо Y на yaw дає X → (cos, −sin)
      const yaw = Math.atan2(-uz, ux);
      world.createCollider(RAPIER.ColliderDesc.cuboid(Math.max(0.5, (a1 - a0) / 2), (y1 - y0) / 2, Math.max(0.5, (b1 - b0) / 2))
        .setTranslation(px, (y0 + y1) / 2, pz).setRotation({ x: 0, y: Math.sin(yaw / 2), z: 0, w: Math.cos(yaw / 2) }), houses);
      ncol++;
    }
  }

  // ---- 10. пакет
  const packed = batch.build(scene, M, {
    shadow: { matte: [true, true], metal: [true, true], concrete: [false, true], road: [false, true], mark: [false, true], reflect: [false, false],
      win: [false, true], glass: [false, true], lamp: [false, false] },
    near: 600, far: FAR,
  });
  const ms = Math.round(performance.now() - t0);
  console.info('[fura] Житомир зібрано за', ms, 'мс:', roads.length, 'доріг,', plan.junctions.length, 'перехресть,', nb, 'будинків (',
    ncol, 'колайдерів),', Math.round(roadOut.stats.guardM), 'м відбійника,', packed.meshes, 'мешів пакета (', packed.tris, '△)');

  // ---- фура в перешкоді → на найближчу вільну смугу (як у kray.js)
  const Q = (r) => new THREE.Quaternion(r.x, r.y, r.z, r.w);
  const obstacle = (c) => { const b = c.parent(); return !!b && b.isFixed() && !surface.has(c.handle); };
  const KING = 7.85;
  function unstick(truck, { maxMove = 25, ahead = 2, side = 0.3 } = {}) {
    world.propagateModifiedBodyPositionsToColliders();
    const coupled = truck.coupled;
    const rel = (b) => {
      const bt = b.translation(), iq = Q(b.rotation()).invert(), out = [];
      for (let i = 0; i < b.numColliders(); i++) {
        const c = b.collider(i);
        if (!c.isEnabled() || c.isSensor()) continue;
        const ct = c.translation();
        out.push({ c, p: new THREE.Vector3(ct.x - bt.x, ct.y - bt.y, ct.z - bt.z).applyQuaternion(iq), q: iq.clone().multiply(Q(c.rotation())) });
      }
      return out;
    };
    const parts = [rel(truck.tb)].concat(coupled ? [rel(truck.rb)] : []);
    const t0 = truck.tb.translation(), near = [];
    world.forEachCollider((c) => {
      if (!obstacle(c)) return;
      const t = c.translation(), sh = c.shape;
      const r = sh.halfExtents ? Math.hypot(sh.halfExtents.x, sh.halfExtents.y, sh.halfExtents.z) : sh.halfHeight != null ? Math.hypot(sh.halfHeight, sh.radius || 0) : sh.radius != null ? sh.radius : 40;
      if (Math.hypot(t.x - t0.x, t.z - t0.z) < maxMove + 25 + r) near.push({ c, x: t.x, z: t.z, r });
    });
    const grow = (sh) => (sh.halfExtents ? new RAPIER.Cuboid(sh.halfExtents.x + side, sh.halfExtents.y, sh.halfExtents.z) : sh);
    for (const list of parts) for (const r of list) r.shape = grow(r.c.shape);
    const hits = (poses) => {
      let hit = false;
      poses.forEach((ps, k) => {
        const fwd = new THREE.Vector3(0, 0, 1).applyQuaternion(ps.q);
        for (const r of parts[k]) {
          const q = ps.q.clone().multiply(r.q);
          for (let a = 0; a <= ahead + 1e-6 && !hit; a += 0.5) {
            const p = r.p.clone().applyQuaternion(ps.q).add(ps.p).addScaledVector(fwd, k === 0 ? a : 0);
            for (const o of near) {
              if (Math.hypot(o.x - p.x, o.z - p.z) > o.r + 9) continue;
              if (o.c.intersectsShape(r.shape, p, q)) { hit = true; break; }
            }
          }
        }
      });
      return hit;
    };
    const now = (b) => { const t = b.translation(); return { p: new THREE.Vector3(t.x, t.y, t.z), q: Q(b.rotation()) }; };
    if (!hits(coupled ? [now(truck.tb), now(truck.rb)] : [now(truck.tb)])) return null;
    const yaw0 = truck.yawOf(truck.tb), cand = [];
    const up = new THREE.Vector3(0, 1, 0);
    for (const r of roads) {
      const S = r.edge.sec, o = S.shift + S.lane * (S.lanes / 2 - 0.5), pts = r.pts, L = pts[pts.length - 1].s;
      for (let i = 0; i < pts.length; i++) {
        const p = pts[i];
        if (Math.hypot(p.x - t0.x, p.z - t0.z) > maxMove + 4) continue;
        for (const dir of [1, -1]) {
          const lane = (s) => { const a = roadAt(pts, Math.max(0, Math.min(L, s))), fx = a.tx * dir, fz = a.tz * dir; return { x: a.x - fz * o, z: a.z + fx * o, yaw: Math.atan2(fx, fz) }; };
          const tr = lane(p.s), tl = lane(p.s - dir * KING);
          const d = Math.hypot(tr.x - t0.x, tr.z - t0.z);
          if (d > maxMove) continue;
          const turn = Math.abs(Math.atan2(Math.sin(tr.yaw - yaw0), Math.cos(tr.yaw - yaw0)));
          cand.push({ tr, tl, d: d + turn * 2 });
        }
      }
    }
    cand.sort((a, b) => a.d - b.d);
    for (const c of cand.slice(0, 300)) {
      const pose = (x, z, yaw, lift) => ({ p: new THREE.Vector3(x, height(x, z) + lift, z), q: new THREE.Quaternion().setFromAxisAngle(up, yaw) });
      const poses = [pose(c.tr.x, c.tr.z, c.tr.yaw, 1.18)].concat(coupled ? [pose(c.tl.x, c.tl.z, c.tr.yaw, 1.3)] : []);
      if (hits(poses)) continue;
      const k = truck.cargo, rt = truck.rb.translation();
      const trailer = coupled ? { x: c.tl.x, z: c.tl.z, yaw: c.tr.yaw, y: height(c.tl.x, c.tl.z) } : { x: rt.x, z: rt.z, yaw: truck.yawOf(truck.rb), y: height(rt.x, rt.z) };
      if (coupled) truck.place({ tractor: { x: 0, z: 0 }, trailer }, true);
      else truck.place({ tractor: { x: c.tr.x, z: c.tr.z, yaw: c.tr.yaw, y: height(c.tr.x, c.tr.z) }, trailer }, false);
      truck.cargo = k;
      return { moved: +Math.hypot(c.tr.x - t0.x, c.tr.z - t0.z).toFixed(1), x: c.tr.x, z: c.tr.z, yaw: c.tr.yaw };
    }
    return false;
  }

  // ---- смуги для трафіку (контракт «Хвиля D (продовження)»): осі правих смуг уздовж руху, крок ~4 м, висота — асфальт
  let laneCache = null;
  function lanes() {
    if (laneCache) return laneCache;
    laneCache = [];
    for (const r of roads) {
      if (r.kind === 'yard' || r.pts.length < 3) continue;
      const S = r.edge.sec, o = S.shift + S.lane * (S.lanes / 2 - 0.5), speed = (LANE_KMH[r.kind] || 50) / 3.6;
      for (const dir of S.oneway ? [1] : [1, -1]) {
        const src = dir > 0 ? r.pts : r.pts.slice().reverse();
        const pts = src.map((p) => {
          const fx = p.tx * dir, fz = p.tz * dir, x = p.x - fz * o, z = p.z + fx * o;
          return { x, y: height(x, z) + 0.02 + LIFT, z };
        });
        laneCache.push({ id: r.id + (dir > 0 ? ':f' : ':b'), pts, speed, loop: false });
      }
    }
    return laneCache;
  }

  const depot = places.find((p) => p.depot) || places[0];
  const att = map.attribution || city.attribution || {};
  let fogFar = 0;
  return {
    map, places, roads, river: [], height, ground: height, nearRoad, buildMs: ms, roadPlan: plan, packed, unstick, treeList: null, pools: null, offRoad, lanes,
    credits: [att.osm, att.dem].filter(Boolean).join(' · '),
    spawn: { x: depot.spawn.x, z: depot.spawn.z, yaw: depot.spawn.yaw, y: height(depot.spawn.x, depot.spawn.z) },
    sync() {
      const f = scene.fog && scene.fog.far;
      if (!f || f === fogFar) return;
      fogFar = f;
      batch.setFar(f);
      for (const lod of terrainLods) lod.levels[lod.levels.length - 1].distance = f + CH * CELL * 0.71;
    },
    resetCones() {},
  };
}

/** Вивіска на двох стовпчиках з написом (як у kray.js). */
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
  const face = new THREE.Mesh(new THREE.PlaneGeometry(5, 1.25), new THREE.MeshStandardMaterial({ map: tex, roughness: 0.6 }));
  face.position.set(0, 2.6, 0.052);
  grp.add(face);
  scene.add(grp);
}
