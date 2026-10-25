// Місто для браузера (D2): з <id>.osm.json.gz і <id>.json → web/static/fura/maps/<id>-city.json — те, чого браузерові
// бракує в мапі сервера: рельєф (сітка «земля» 30 м), контури будинків, вода. Лише в рамці місць мапи (+ запас): усе
// місто 17 × 14 км браузер без стрімінгу не потягне.
//
//   node tools/osm/city.mjs zhytomyr
//
// Формат (fura-city/1): bounds — рамка світу гри; height — як у fura-osm/1 (u16le-base64, iz*nx+ix, scale, y0), обрізана
// до рамки; buildings — [h, x0, z0, dx1, dz1, …] (дециметри, дельти від попередньої точки, контур без повтору першої);
// water — [x0, z0, dx1, dz1, …] (метри). Будинки — лише ближче RADIUS м до доріг мапи (далі їх за туманом не видно).
import fs from 'fs';
import zlib from 'zlib';
import path from 'path';

const id = process.argv[2] || 'zhytomyr';
const root = path.resolve(path.dirname(new URL(import.meta.url).pathname.replace(/^\/(\w:)/, '$1')), '../..');
const osm = JSON.parse(zlib.gunzipSync(fs.readFileSync(path.join(root, 'data/fura/maps', id + '.osm.json.gz'))));
const map = JSON.parse(fs.readFileSync(path.join(root, 'data/fura/maps', id + '.json'), 'utf8'));

const MARGIN = 700, RADIUS = 260, CELL = 50;
const xs = [], zs = [];
for (const p of map.places) { xs.push(p.yard.x); zs.push(p.yard.z); }
const B = {
  x0: Math.floor((Math.min(...xs) - MARGIN) / 30) * 30, x1: Math.ceil((Math.max(...xs) + MARGIN) / 30) * 30,
  z0: Math.floor((Math.min(...zs) - MARGIN) / 30) * 30, z1: Math.ceil((Math.max(...zs) + MARGIN) / 30) * 30,
};
const inB = (x, z, pad = 0) => x > B.x0 + pad && x < B.x1 - pad && z > B.z0 + pad && z < B.z1 - pad;

// рельєф: вирізати з сітки osm (той самий крок), з запасом 1 вузол
const H = osm.height, src = Buffer.from(H.data, 'base64');
const ix0 = Math.max(0, Math.floor((B.x0 - H.x0) / H.step) - 1), iz0 = Math.max(0, Math.floor((B.z0 - H.z0) / H.step) - 1);
const ix1 = Math.min(H.nx - 1, Math.ceil((B.x1 - H.x0) / H.step) + 1), iz1 = Math.min(H.nz - 1, Math.ceil((B.z1 - H.z0) / H.step) + 1);
const nx = ix1 - ix0 + 1, nz = iz1 - iz0 + 1, out = Buffer.alloc(nx * nz * 2);
for (let iz = 0; iz < nz; iz++) for (let ix = 0; ix < nx; ix++) out.writeUInt16LE(src.readUInt16LE(((iz + iz0) * H.nx + ix + ix0) * 2), (iz * nx + ix) * 2);
const height = { x0: H.x0 + ix0 * H.step, z0: H.z0 + iz0 * H.step, step: H.step, nx, nz, y0: H.y0, scale: H.scale, enc: H.enc, order: H.order, data: out.toString('base64') };

// маска «біля дороги»: клітини 50 м, де є точка дороги мапи, розширені на RADIUS
const W = Math.ceil((B.x1 - B.x0) / CELL), D = Math.ceil((B.z1 - B.z0) / CELL), mask = new Uint8Array(W * D);
const R = Math.ceil(RADIUS / CELL);
const mark = (x, z) => {
  const cx = Math.floor((x - B.x0) / CELL), cz = Math.floor((z - B.z0) / CELL);
  for (let a = -R; a <= R; a++) for (let b = -R; b <= R; b++) {
    if (a * a + b * b > R * R) continue;
    const i = cx + a, j = cz + b;
    if (i >= 0 && j >= 0 && i < W && j < D) mask[j * W + i] = 1;
  }
};
for (const r of map.roads) {
  const pts = [map.nodes[r.a], ...(r.via || []), map.nodes[r.b]];
  for (let k = 1; k < pts.length; k++) {
    const [ax, az] = pts[k - 1], [bx, bz] = pts[k], n = Math.ceil(Math.hypot(bx - ax, bz - az) / (CELL / 2));
    for (let t = 0; t <= n; t++) mark(ax + (bx - ax) * t / n, az + (bz - az) * t / n);
  }
}
const nearRoad = (x, z) => { const i = Math.floor((x - B.x0) / CELL), j = Math.floor((z - B.z0) / CELL); return i >= 0 && j >= 0 && i < W && j < D && mask[j * W + i]; };

const DEF = { house: 2, detached: 2, apartments: 5, residential: 3, industrial: 2, warehouse: 2, garage: 1, garages: 1, shed: 1, commercial: 3, retail: 2, school: 3, church: 3, kiosk: 1 };
const buildings = [];
let skipped = 0;
for (const b of osm.buildings) {
  const c = b.pts.reduce((s, p) => [s[0] + p[0] / b.pts.length, s[1] + p[1] / b.pts.length], [0, 0]);
  if (!inB(c[0], c[1], 40) || !nearRoad(c[0], c[1]) || b.pts.length < 3) { skipped++; continue; }
  const lv = b.lv || DEF[b.k] || 2;
  const h = Math.max(2.5, Math.min(80, b.h || lv * 3 + 0.6));
  let pts = b.pts;
  if (Math.hypot(pts[0][0] - pts[pts.length - 1][0], pts[0][1] - pts[pts.length - 1][1]) < 0.05) pts = pts.slice(0, -1);
  const row = [Math.round(h * 10)];
  let px = 0, pz = 0;
  for (const [x, z] of pts) { const qx = Math.round(x * 10), qz = Math.round(z * 10); row.push(qx - px, qz - pz); px = qx; pz = qz; }
  buildings.push(row);
}
const water = [];
for (const a of osm.areas) {
  if (a.k !== 'water' || !a.outer || a.outer.length < 3) continue;
  if (!a.outer.some(([x, z]) => inB(x, z))) continue;
  const row = [];
  let px = 0, pz = 0;
  for (const [x, z] of a.outer) { const qx = Math.round(x), qz = Math.round(z); row.push(qx - px, qz - pz); px = qx; pz = qz; }
  water.push(row);
}

const res = { format: 'fura-city/1', id, attribution: map.attribution || osm.attribution, bounds: B, height, buildings, water };
const file = path.join(root, 'web/static/fura/maps', id + '-city.json');
fs.mkdirSync(path.dirname(file), { recursive: true });
fs.writeFileSync(file, JSON.stringify(res));
console.log(file, (fs.statSync(file).size / 1024).toFixed(0), 'КБ;', 'рамка', B, 'рельєф', nx, '×', nz, '; будинків', buildings.length, '(поза рамкою чи далеко від доріг', skipped + ')', '; води', water.length);
