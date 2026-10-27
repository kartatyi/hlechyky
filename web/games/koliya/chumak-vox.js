// «Колія» — моделі теми «Чумацький шлях» (K25). Лише вигляд: render.js кличе ці будівники в точках-гачках «K25»,
// коли theme.js каже 'chumak'. Правила не знають про тему.
//  Потяг: makeLoco — мажа з сіллю, яку тягне пара сірих степових волів у ярмі (ноги переступають, голови кивають, курява
//   з-під копит, пара з ніздрів; «пріють» — піт; «знесилились» — лягають); makeStore — віз-комора (дрова/залізо + сіль),
//   makeCraft — віз-кузня (горн із жаром і димарем, ковадло на пні). Колеса крутяться від пройденого.
//  Колія: rail — на воді/болоті/хмизі колоди-гать, на суші битий шлях із коліями від коліс і травою посередині;
//   bridge — хмизова гребля (в'язки хмизу) на воді замість містка; logBundle — «рейки» в руках = в'язка колод.
//  Світ: findWells/well — мала калюжа (≤ 4 клітинки) стає криницею-журавлем із коритами (ті самі клітинки води);
//   grassDeco — ковила, маки, волошки; wall — земляний вал замість скелястого хребта; village — хутір на старті
//   (біла хата під стріхою, тин із глечиками, соняшники, вишні, криниця, стіг, лелека на гнізді); fair — ярмарок на
//   станції (шатра, соляні гори Криму, бочки, кавуни, вітряк, стяг, віз і воли на постої); steppe — степ за смугою
//   (кургани з кам'яними бабами, вітряки, тополі, стоги, чумацька валка, що йде шляхом) і лелеки в небі.
//  pal(look) — заміни палітри C (степ), {} поза темою.
// Інструменти рендера приходять параметром K = { THREE, cube, mat, mergeGroup, addBlob, C, hh, pick, tint, G, I }
// (render.js не імпортуємо — без циклу). cube(parent, x, y0, z, sx, sy, sz, hex, mat?) — y0 = низ, hex — число.
// Пул P рендера: P.add(list, x, y0, z, sx, sy, sz, color, ry) — поворот лише довкола Y.
// Анімації — функції f(dt, now, cam) у масиві fx (render кличе щокадру).
import { isChumak } from './theme.js';

// ---------- барви ----------
const OX = 0xcfc9bc, OX_D = 0xa69f92, OX2 = 0xbdb6a8, OX2_D = 0x938c80, HORN = 0xf0e6cc, HORN_T = 0x2b2724, HOOF = 0x2e2a26, MUZ = 0x5c544b;
const WD = 0x9a6a3c, WD_D = 0x704b2a, WD_L = 0xc29560, CAP = 0xdcb67e, RIM = 0x4e341e;
const SALT = 0xf7f5ef, SALT_B = 0xe7e3d6, CLOTH = 0xd9c9a2, CLOTH_D = 0xc4b38a, ROPE = 0xcbb07a, TAR = 0x1e1b18;
const CLAY = 0xa8583a, CLAY_D = 0x8a4630, IRON = 0x474c55, LEATHER = 0x6b4a33;
const DIRT = 0xbf9a68, DIRT_D = 0x9a7650, RUT = 0x7f6142, EARTH = 0x9c7c52;
const LOGS = [0x8f6236, 0x9d6c3c, 0x84592f, 0xa7744a];
const DUST = [0xd8c39a, 0xcdb68a, 0xe0d0ae];
const GY = -0.13;   // земля в координатах моделей потяга (render ставить групу на Y = 0,13)

export function pal(look) {
  if (!isChumak()) return {};
  const p = { station: 0xdcc9a0, stationB: 0xd0bc90, tie: 0x7a5232, plank: [0xa07a4a, 0x8f6c40, 0xb08a58] };
  if (look === 'summer') Object.assign(p, {   // літній степ: трава жовтувато-зелена, вигоріла
    grassA: 0xa9bf5e, grassB: 0xa0b657, grassDark: 0x91a84d, tuft: 0xcfc684, cap: 0xa9bf5e,
    sand: 0xe8d6a2, sandB: 0xdfcb92, leaf: [0x6f9e3c, 0x7aa844, 0x648f37, 0x86b04a],
  });
  return p;
}

const th = (K, hex, a) => K.tint(hex, a).getHex();
// модель потяга: g (render масштабує її 1 × 1,2 × 1,2) і m — внутрішня з оберненим масштабом, щоб колеса були круглі
function inner(K) { const g = new K.THREE.Group(), m = new K.THREE.Group(); m.scale.set(1, 1 / 1.2, 1 / 1.2); g.add(m); return { g, m }; }
const toG = (K, x, y, z) => new K.THREE.Vector3(x, y / 1.2, z / 1.2);

// ---------- віл (дивиться в +X; земля — y = 0 групи) ----------
function ox(K, parent, x, y, z, tone = 0, off = 0) {
  const { THREE, cube } = K;
  const root = new THREE.Group(); root.position.set(x, y, z); parent.add(root);
  const col = tone ? OX2 : OX, dk = tone ? OX2_D : OX_D;
  const legs = [];
  for (const [lx, lz] of [[0.15, -0.065], [0.15, 0.065], [-0.16, -0.065], [-0.16, 0.065]]) {
    const L = new THREE.Group(); L.position.set(lx, 0.24, lz); root.add(L);
    cube(L, 0, -0.22, 0, 0.065, 0.22, 0.065, dk);
    cube(L, 0.005, -0.24, 0, 0.075, 0.035, 0.075, HOOF);
    legs.push(L);
  }
  const body = new THREE.Group(); root.add(body);
  cube(body, -0.01, 0.2, 0, 0.48, 0.22, 0.22, col);        // тулуб
  cube(body, 0.12, 0.4, 0, 0.17, 0.06, 0.2, dk);           // холка
  cube(body, -0.03, 0.18, 0, 0.32, 0.03, 0.18, dk);        // черево
  cube(body, 0.2, 0.15, 0, 0.07, 0.12, 0.09, col);         // підгрудок
  cube(body, -0.19, 0.4, 0, 0.11, 0.035, 0.19, col);       // крижі
  const tail = new THREE.Group(); tail.position.set(-0.25, 0.39, 0); body.add(tail);
  cube(tail, -0.012, -0.2, 0, 0.025, 0.2, 0.025, dk); cube(tail, -0.012, -0.26, 0, 0.045, 0.07, 0.045, 0x3a332c);
  const head = new THREE.Group(); head.position.set(0.24, 0.33, 0); body.add(head);
  cube(head, 0.02, -0.08, 0, 0.12, 0.14, 0.15, dk);        // шия
  cube(head, 0.11, -0.1, 0, 0.13, 0.14, 0.135, col);       // голова
  cube(head, 0.19, -0.1, 0, 0.05, 0.085, 0.105, MUZ);      // писок
  for (const s of [-1, 1]) {
    cube(head, 0.13, -0.02, s * 0.069, 0.03, 0.028, 0.006, 0x151515);   // око
    cube(head, 0.08, -0.01, s * 0.092, 0.035, 0.03, 0.05, dk);          // вухо
    // роги-ліра сірої степової худоби: убік, тоді вгору, чорні кінчики
    cube(head, 0.1, 0.03, s * 0.09, 0.034, 0.034, 0.06, HORN);
    cube(head, 0.1, 0.045, s * 0.118, 0.032, 0.07, 0.03, HORN);
    cube(head, 0.115, 0.105, s * 0.124, 0.027, 0.06, 0.027, HORN);
    cube(head, 0.128, 0.16, s * 0.116, 0.021, 0.035, 0.021, HORN_T);
  }
  return { root, body, legs, head, tail, off, y };
}
// поза вола: ph — фаза кроку, amp — 0 стоїть … 1 іде, lie — 0 стоїть … 1 лежить
function poseOx(o, ph, amp, lie, now) {
  const p = ph + o.off, sw = Math.sin(p) * 0.5 * amp, f = 1.5 * lie;
  const [fl, fr, rl, rr] = o.legs;
  fl.rotation.z = sw - f; rr.rotation.z = sw + f; fr.rotation.z = -sw - f; rl.rotation.z = -sw + f;
  o.root.position.y = o.y - 0.17 * lie;
  o.body.position.y = amp * Math.abs(Math.sin(p)) * 0.012;
  o.head.rotation.z = Math.sin(p * 2) * 0.07 * amp + Math.sin(now / 1100 + o.off) * 0.05 * (1 - amp) - 0.35 * lie;
  o.tail.rotation.x = Math.sin(now / 380 + o.off * 3) * 0.35;
}

// ---------- колесо (дерев'яне, 10 ободів, 8 спиць) ----------
function wheel(K, parent, x, z, r) {
  const { THREE, cube } = K, w = new THREE.Group();
  w.position.set(x, GY + 0.035 + r, z); w.userData.r = r; parent.add(w);
  const n = 10, t = 0.032, seg = 2 * r * Math.sin(Math.PI / n) + 0.012;
  for (let k = 0; k < n; k++) {
    const a = k * 2 * Math.PI / n;
    cube(w, Math.cos(a) * (r - t / 2), Math.sin(a) * (r - t / 2) - t / 2, 0, seg, t, 0.04, k % 2 ? 0x5a3c22 : RIM).rotation.z = a + Math.PI / 2;
  }
  for (let k = 0; k < 4; k++) cube(w, 0, -0.01, 0, 2 * r - 0.03, 0.02, 0.018, WD).rotation.z = k * Math.PI / 4;
  cube(w, 0, -0.035, 0, 0.07, 0.07, 0.07, 0x4a3420);
  return w;
}
// кузов мажі: підлога, розхилені драбини з кілками, передок/задок, осі, 4 колеса (задні більші). → верх підлоги
function cartBody(K, m, cx, len, wheels) {
  const { cube } = K, y0 = GY + 0.24;
  cube(m, cx, y0, 0, len, 0.045, 0.42, WD_D);
  for (const s of [-1, 1]) {
    cube(m, cx, y0 + 0.03, s * 0.215, len, 0.05, 0.03, WD);
    cube(m, cx, y0 + 0.22, s * 0.25, len + 0.08, 0.035, 0.035, WD_L);
    for (const k of [-0.48, -0.16, 0.16, 0.48]) cube(m, cx + k * len, y0 + 0.03, s * 0.232, 0.03, 0.22, 0.03, WD_D);
  }
  cube(m, cx - len / 2 + 0.015, y0 + 0.04, 0, 0.03, 0.12, 0.42, WD);
  cube(m, cx + len / 2 - 0.015, y0 + 0.04, 0, 0.03, 0.12, 0.42, WD);
  const rr = 0.165, rf = 0.14, xr = cx - len * 0.3, xf = cx + len * 0.3;
  cube(m, xr, GY + 0.035 + rr - 0.025, 0, 0.05, 0.05, 0.64, WD_D);
  cube(m, xf, GY + 0.035 + rf - 0.025, 0, 0.05, 0.05, 0.64, WD_D);
  cube(m, xr, GY + 0.035 + rr + 0.02, 0, 0.06, y0 - (GY + 0.035 + rr + 0.02), 0.36, WD_D);   // підушки між віссю й підлогою
  cube(m, xf, GY + 0.035 + rf + 0.02, 0, 0.06, y0 - (GY + 0.035 + rf + 0.02), 0.36, WD_D);
  for (const s of [-1, 1]) if (wheels) wheels.push(wheel(K, m, xr, s * 0.3, rr), wheel(K, m, xf, s * 0.3, rf));
  return y0 + 0.045;
}

// рух моделі за кадр (по світу), без стрибків перебудови
function mover() {
  let lx = null, lz = 0;
  return (g) => {
    const x = g.position.x, z = g.position.z;
    const d = lx === null ? 0 : Math.hypot(x - lx, z - lz);
    lx = x; lz = z;
    return d > 0.6 ? 0 : d;
  };
}
function spin(wheels, d) { for (const w of wheels) w.rotation.z -= d / w.userData.r; }

// ---------- паротяг → мажа з волами ----------
export function makeLoco(K) {
  const { THREE, cube, mergeGroup, addBlob } = K;
  const { g, m } = inner(K), wheels = [];
  const bed = cartBody(K, m, -0.2, 0.6, wheels);
  // вантаж: сіль під рядном, мотузки; на задку — дьогтярка (мазати осі)
  cube(m, -0.22, bed, 0, 0.5, 0.15, 0.38, SALT);
  cube(m, -0.22, bed + 0.15, 0, 0.44, 0.07, 0.32, CLOTH);
  cube(m, -0.22, bed + 0.22, 0, 0.3, 0.035, 0.2, CLOTH_D);
  for (const x of [-0.37, -0.07]) cube(m, x, bed - 0.005, 0, 0.025, 0.27, 0.41, ROPE);
  cube(m, -0.53, GY + 0.16, 0.12, 0.07, 0.08, 0.07, TAR); cube(m, -0.53, GY + 0.24, 0.12, 0.012, 0.06, 0.012, 0x3a3532);
  // воли в ярмі, дишло між ними
  const oxen = [ox(K, m, 0.42, GY, -0.17, 0, 0), ox(K, m, 0.42, GY, 0.17, 1, Math.PI)];
  const yx = 0.58;
  cube(m, yx, GY + 0.44, 0, 0.055, 0.055, 0.66, WD_D);
  for (const z of [-0.17, 0.17]) for (const d of [-0.125, 0.125]) cube(m, yx, GY + 0.3, z + d, 0.03, 0.15, 0.03, WD_L);
  cube(m, (yx + 0.1) / 2, GY + 0.3, 0, yx - 0.1, 0.04, 0.04, WD_D);
  mergeGroup(g); addBlob(g, 1.5, 0.85, 0.12, 0, -0.09);
  const boilerMat = new THREE.MeshLambertMaterial();   // render фарбує «котел» — у волів його нема
  const moved = mover();
  let ph = 0, amp = 0, lie = 0, dustT = 0, breathT = 0;
  const V = new THREE.Vector3();
  const at = (x, y, z) => V.set(x, y, z).applyMatrix4(m.matrixWorld);
  function fx(st) {
    const { dt, now, spawn, puff } = st;
    const d = moved(g);
    ph += d * 13; spin(wheels, d);
    amp += ((d > 0.0005 ? 1 : 0) - amp) * Math.min(1, dt * 6);
    const tired = st.fire || (st.lost && st.reason === 'fire');
    lie += ((tired ? 1 : 0) - lie) * Math.min(1, dt * 2.2);
    for (const o of oxen) poseOx(o, ph, amp * (1 - lie), lie, now);
    g.updateMatrixWorld(true);
    // курява з-під копит
    dustT -= dt;
    if (d > 0.0005 && dustT <= 0) {
      dustT = 0.11;
      for (const o of oxen) { const p = at(0.3 + Math.random() * 0.2, GY + 0.04, o.root.position.z); puff(p.x, p.y, p.z, 1, DUST[(Math.random() * 3) | 0], 0.17, 1.0, 0.22); }
      if (Math.random() < 0.5) { const p = at(-0.3, GY + 0.03, (Math.random() - 0.5) * 0.6); puff(p.x, p.y, p.z, 1, DUST[1], 0.13, 0.8, 0.15); }
    }
    // пара з ніздрів — частіша й густіша, коли пріють
    breathT -= dt;
    if (breathT <= 0) {
      breathT = 1.5 - 1.1 * st.hot + Math.random() * 0.3;
      for (const o of oxen) { const p = at(0.66, GY + 0.24 - 0.12 * lie, o.root.position.z); puff(p.x, p.y, p.z, 1, 0xf4f4f2, 0.07 + 0.08 * st.hot, 0.7, 0.25); }
    }
    // піт: краплі з боків; знесилились — ще й важкий видих
    const sweat = Math.max(st.hot, tired ? 1 : 0);
    if (sweat > 0.2 && Math.random() < dt * 14 * sweat) {
      const o = oxen[(Math.random() * 2) | 0], p = at(0.3 + Math.random() * 0.3, GY + 0.36 - 0.17 * lie, o.root.position.z + (o.root.position.z > 0 ? 0.11 : -0.11));
      spawn(p.x, p.y, p.z, (Math.random() - 0.5) * 0.4, 0.6, (o.root.position.z > 0 ? 0.4 : -0.4), 0.55, 0.045, 0xcdeeff, 7, 0);
    }
  }
  return { g, boilerMat, chimney: null, boiler: null, top: toG(K, 0.72, GY + 0.62, 0), fx };
}

// ---------- склад → віз-комора ----------
export function makeStore(K) {
  const { THREE, cube, mergeGroup, addBlob } = K;
  const { g, m } = inner(K), wheels = [];
  const bed = cartBody(K, m, 0, 0.84, wheels);
  cube(m, 0.5, GY + 0.27, 0, 0.2, 0.04, 0.04, WD_D);                         // дишло до переднього воза
  cube(m, 0.36, bed, 0.13, 0.09, 0.12, 0.12, SALT_B); cube(m, 0.36, bed + 0.12, 0.13, 0.05, 0.03, 0.05, ROPE);   // мішечок солі
  cube(m, -0.45, GY + 0.16, -0.12, 0.07, 0.08, 0.07, TAR);                   // дьогтярка
  cube(m, -0.3, bed, -0.17, 0.12, 0.05, 0.05, 0xd8b862);                     // віхоть сіна
  const wood = new THREE.Group(); wood.position.set(-0.17, bed, 0); m.add(wood);
  const iron = new THREE.Group(); iron.position.set(0.17, bed, 0); m.add(iron);
  mergeGroup(g); addBlob(g, 1.1, 0.85, 0, 0, -0.09);
  const moved = mover();
  return { g, wood, iron, nW: -1, nI: -1, fx() { spin(wheels, moved(g)); } };
}

// ---------- майстерня → віз-кузня ----------
export function makeCraft(K) {
  const { THREE, cube, mat, mergeGroup, addBlob } = K;
  const { g, m } = inner(K), wheels = [];
  const bed = cartBody(K, m, 0, 0.84, wheels);
  cube(m, 0.5, GY + 0.27, 0, 0.2, 0.04, 0.04, WD_D);
  // горн: глиняна піч, жар, димар
  cube(m, 0.22, bed, 0.02, 0.28, 0.17, 0.3, CLAY);
  cube(m, 0.22, bed + 0.17, 0.02, 0.3, 0.03, 0.32, CLAY_D);
  const coal = mat(0xff7a2a, { emissive: 0xb83a08 });
  cube(m, 0.22, bed + 0.2, 0.02, 0.18, 0.02, 0.2, 0xff7a2a, coal);
  cube(m, 0.3, bed + 0.17, 0.12, 0.07, 0.42, 0.07, 0x3a3532);
  cube(m, 0.3, bed + 0.59, 0.12, 0.1, 0.03, 0.1, 0x2a2624);
  // міх
  cube(m, 0.36, bed, -0.12, 0.1, 0.07, 0.12, LEATHER); cube(m, 0.36, bed + 0.07, -0.12, 0.09, 0.025, 0.11, WD);
  // ковадло на пні й молот
  cube(m, -0.02, bed, -0.07, 0.12, 0.1, 0.12, WD_D);
  cube(m, -0.02, bed + 0.1, -0.07, 0.06, 0.04, 0.06, IRON);
  cube(m, -0.02, bed + 0.14, -0.07, 0.17, 0.05, 0.08, IRON);
  cube(m, 0.085, bed + 0.152, -0.07, 0.05, 0.03, 0.04, IRON);
  cube(m, 0.0, bed + 0.005, 0.08, 0.03, 0.03, 0.16, WD); cube(m, 0.0, bed, 0.17, 0.06, 0.05, 0.04, IRON);
  const rails = new THREE.Group(); rails.position.set(-0.27, bed, 0); m.add(rails);
  mergeGroup(g); addBlob(g, 1.1, 0.85, 0, 0, -0.09);
  const moved = mover();
  return { g, rails, nR: -1, stack: toG(K, 0.3, bed + 0.66, 0.12),
    fx({ now }) { spin(wheels, moved(g)); coal.emissiveIntensity = 0.75 + 0.25 * Math.sin(now / 90) * Math.sin(now / 37); } };
}

// ---------- гравець-чумак: бриль (широкі криси, стрічка кольору гравця) і вуса ----------
export function bryl(K, head, band) {
  const { cube } = K;
  cube(head, 0, 0.25, 0, 0.48, 0.03, 0.48, 0xe2c47a);
  cube(head, 0, 0.28, 0, 0.29, 0.1, 0.29, 0xd9b968);
  cube(head, 0, 0.28, 0, 0.3, 0.035, 0.3, band);
  cube(head, 0.142, 0.022, 0, 0.014, 0.028, 0.15, 0x3a2a1a);
  for (const s of [-1, 1]) cube(head, 0.142, -0.01, s * 0.07, 0.014, 0.04, 0.02, 0x3a2a1a);
}

// ---------- «рейки» в руках і на возі: в'язка колод ----------
export function logBundle(K, g, n) {
  for (let j = 0; j < n; j++) {
    const y = j * 0.1;
    for (const z of [-0.12, 0, 0.12]) {
      K.cube(g, 0, y, z, 0.5, 0.09, 0.1, LOGS[(j + Math.round(z * 10)) & 3]);
      K.cube(g, 0.245, y + 0.012, z, 0.02, 0.066, 0.076, CAP);
    }
    K.cube(g, -0.12, y - 0.003, 0, 0.04, 0.096, 0.38, ROPE);
  }
}

// ---------- колія: гать / битий шлях ----------
const DIRS = [[1, 0], [0, 1], [-1, 0], [0, -1]];
export function rail(P, list, x, y, base, d1, d2, gk, K) {
  const { G, hh, tint } = K;
  const wet = gk === G.WATER || gk === G.BRIDGE || gk === G.SWAMP;
  const cx = x + 0.5, cz = y + 0.5, v = (k) => hh(x, y, 200 + k);
  if ((d1 + 2) % 4 === d2) {                      // пряма
    const al = d1 % 2 === 0;
    // a — уздовж шляху, b — упоперек; sa/sb — розміри вздовж/упоперек
    const put = (a, b, y0, sa, sy, sb, col) => P.add(list, cx + (al ? a : b), y0, cz + (al ? b : a), al ? sa : sb, sy, al ? sb : sa, col);
    if (wet) {
      for (let k = 0; k < 5; k++) {
        const a = -0.4 + k * 0.2 + (v(k) - 0.5) * 0.02, b = (v(k + 5) - 0.5) * 0.06, len = 0.9 + v(k + 10) * 0.12;
        put(a, b, base - 0.03, 0.17, 0.085, len, tint(LOGS[(v(k + 15) * 4) | 0], (v(k + 20) - 0.5) * 0.16));
        for (const sd of [-1, 1]) put(a, b + sd * len / 2, base - 0.022, 0.14, 0.07, 0.02, CAP);
      }
      for (const sd of [-1, 1]) put(0, sd * 0.36, base + 0.055, 1.0, 0.04, 0.05, WD_D);   // прив'язні жердини
    } else {
      put(0, 0, base, 1.0, 0.012, 0.8, tint(DIRT, (v(0) - 0.5) * 0.1));
      for (const sd of [-1, 1]) put(0, sd * 0.2, base + 0.012, 1.0, 0.004, 0.1, tint(RUT, (v(1) - 0.5) * 0.12));
      if (v(2) < 0.65) for (let j = 0; j < 2; j++) put((v(3 + j) - 0.5) * 0.8, (v(5 + j) - 0.5) * 0.12, base + 0.012, 0.05, 0.04 + v(7 + j) * 0.04, 0.05, tint(K.C.tuft, -0.1));
      for (let j = 0; j < 2; j++) put((v(9 + j) - 0.5) * 0.8, (v(11 + j) - 0.5) * 0.1, base + 0.012, 0.06, 0.003, 0.05, DIRT_D);   // сліди копит
      if (v(13) < 0.3) put((v(14) - 0.5) * 0.7, (v(15) > 0.5 ? 1 : -1) * 0.34, base + 0.012, 0.07, 0.04, 0.06, 0xb9ae96);
    }
    return;
  }
  // поворот: дуга довкола кута клітинки (як у render.js)
  const e1 = DIRS[d1], e2 = DIRS[d2];
  const kx = cx + (e1[0] + e2[0]) * 0.5, kz = cz + (e1[1] + e2[1]) * 0.5;
  const a0 = Math.atan2(-e2[1], -e2[0]), a1 = Math.atan2(-e1[1], -e1[0]);
  let da = a1 - a0; if (da > Math.PI) da -= 2 * Math.PI; if (da < -Math.PI) da += 2 * Math.PI;
  const arc = (r, n, y0, sy, w, col) => {
    for (let j = 0; j < n; j++) {
      const t0 = a0 + da * j / n, t1 = a0 + da * (j + 1) / n;
      const x0 = kx + Math.cos(t0) * r, z0 = kz + Math.sin(t0) * r, x1 = kx + Math.cos(t1) * r, z1 = kz + Math.sin(t1) * r;
      P.add(list, (x0 + x1) / 2, y0, (z0 + z1) / 2, Math.hypot(x1 - x0, z1 - z0) + 0.04, sy, w, col, -Math.atan2(z1 - z0, x1 - x0));
    }
  };
  if (wet) {
    for (let k = 0; k < 5; k++) {
      const an = a0 + da * (0.1 + k * 0.2);
      P.add(list, kx + Math.cos(an) * 0.5, base - 0.03, kz + Math.sin(an) * 0.5, 0.94, 0.085, 0.16, tint(LOGS[(v(k + 15) * 4) | 0], (v(k + 20) - 0.5) * 0.16), -an);
    }
    arc(0.14, 3, base + 0.055, 0.04, 0.05, WD_D); arc(0.86, 5, base + 0.055, 0.04, 0.05, WD_D);
  } else {
    arc(0.5, 6, base, 0.012, 0.8, tint(DIRT, (v(0) - 0.5) * 0.1));
    arc(0.3, 4, base + 0.012, 0.004, 0.1, RUT); arc(0.7, 5, base + 0.012, 0.004, 0.1, RUT);
  }
}

// хмизова гребля на воді (G.BRIDGE): в'язки хмизу, перев'язані, і кілки по кутах
export function bridge(P, list, x, y, K) {
  const { hh, tint } = K, cz = y + 0.5;
  for (let j = 0; j < 4; j++) {
    const fx = x + 0.125 + j * 0.25;
    for (let k = 0; k < 3; k++) P.add(list, fx + (k - 1) * 0.065, -0.13 + (k === 1 ? 0.02 : 0), cz + (hh(x, y, 50 + j * 3 + k) - 0.5) * 0.06, 0.07, 0.085, 1.02, tint(0x7d6a4a, (hh(x, y, 60 + j * 3 + k) - 0.5) * 0.3));
    for (const o of [0.22, 0.78]) P.add(list, fx, -0.135, y + o, 0.22, 0.1, 0.035, ROPE);
  }
  for (const [ox, oz] of [[0.05, 0.05], [0.95, 0.05], [0.05, 0.95], [0.95, 0.95]]) P.add(list, x + ox, -0.32, y + oz, 0.05, 0.4, 0.05, WD_D);
}

// ---------- криниця на малій калюжі ----------
export function findWells(s, G) {
  const { w, h, ground } = s, n = w * h, seen = new Uint8Array(n), out = new Map();
  const wet = (i) => ground[i] === G.WATER;
  for (let i0 = 0; i0 < n; i0++) {
    if (!wet(i0) || seen[i0]) continue;
    const comp = [i0]; seen[i0] = 1;
    for (let k = 0; k < comp.length; k++) {
      const i = comp[k], x = i % w;
      for (const j of [x > 0 ? i - 1 : -1, x < w - 1 ? i + 1 : -1, i - w, i + w]) if (j >= 0 && j < n && wet(j) && !seen[j]) { seen[j] = 1; comp.push(j); }
    }
    if (comp.length > 4) continue;
    const nb = (i) => [i - 1, i + 1, i - w, i + w].filter((j) => comp.includes(j)).length;
    const best = comp.reduce((a, b) => (nb(b) > nb(a) ? b : a));
    for (const c of comp) out.set(c, c === best ? 'well' : 'trough');
  }
  return out;
}
export function well(P, list, x, y, role, K) {
  const { hh, tint } = K, cx = x + 0.5, cz = y + 0.5, top = 0.03;
  if (role !== 'well') {     // корито — напувати волів
    const al = hh(x, y, 300) > 0.5;
    P.add(list, cx, top, cz, al ? 0.82 : 0.3, 0.05, al ? 0.3 : 0.82, WD_D);
    for (const s of [-1, 1]) {
      P.add(list, cx + (al ? 0 : s * 0.13), top + 0.05, cz + (al ? s * 0.13 : 0), al ? 0.82 : 0.04, 0.12, al ? 0.04 : 0.82, WD);
      P.add(list, cx + (al ? s * 0.39 : 0), top + 0.05, cz + (al ? 0 : s * 0.39), al ? 0.04 : 0.3, 0.12, al ? 0.3 : 0.04, WD);
    }
    P.add(list, cx, top + 0.1, cz, al ? 0.74 : 0.22, 0.04, al ? 0.22 : 0.74, 0x3f8fc0);
    for (let j = 0; j < 4; j++) P.add(list, cx + (hh(x, y, 310 + j) - 0.5) * 0.9, top, cz + (hh(x, y, 320 + j) - 0.5) * 0.9, 0.03, 0.1 + hh(x, y, 330 + j) * 0.15, 0.03, tint(K.C.tuft, -0.05));
    return;
  }
  // зруб із колод, вода всередині
  for (let l = 0; l < 4; l++) {
    const yy = top + l * 0.09, a = l % 2 ? WD : WD_D, b = l % 2 ? WD_D : WD;
    P.add(list, cx, yy, cz - 0.29, 0.68, 0.09, 0.09, a); P.add(list, cx, yy, cz + 0.29, 0.68, 0.09, 0.09, a);
    P.add(list, cx - 0.29, yy, cz, 0.09, 0.09, 0.5, b); P.add(list, cx + 0.29, yy, cz, 0.09, 0.09, 0.5, b);
  }
  P.add(list, cx, top + 0.24, cz, 0.5, 0.02, 0.5, 0x2f6f9a);
  // журавель: сошка, довга жердина навскіс (сходинками), відро на ланцюгу, тягар на короткому кінці
  const px = cx + 0.4, pz = cz - 0.36, py = top + 1.25;
  P.add(list, px, top, pz, 0.1, 1.25, 0.1, WD_D);
  P.add(list, px - 0.05, py, pz, 0.04, 0.1, 0.06, WD_D); P.add(list, px + 0.05, py, pz, 0.04, 0.1, 0.06, WD_D);
  const ax = cx - 0.02, ay = top + 0.78, az = cz + 0.02;
  const bx = px + (px - ax) * 0.45, by = py + 0.04 + (py + 0.04 - ay) * 0.45, bz = pz + (pz - az) * 0.45;
  const N = 16;
  for (let j = 0; j <= N; j++) {
    const f = j / N, X = ax + (bx - ax) * f, Y = ay + (by - ay) * f, Z = az + (bz - az) * f;
    P.add(list, X, Y - 0.03, Z, 0.06, 0.06, 0.06, j % 2 ? WD_L : 0xb28552);
  }
  P.add(list, ax, top + 0.42, az, 0.012, ay - top - 0.42, 0.012, 0x3a3a3a);
  P.add(list, ax, top + 0.32, az, 0.11, 0.1, 0.11, WD_L); P.add(list, ax, top + 0.42, az, 0.12, 0.015, 0.12, IRON);
  P.add(list, bx, by - 0.16, bz, 0.13, 0.15, 0.13, 0x8a8f96);
}

// ---------- трава: ковила, маки, волошки ----------
export function grassDeco(P, list, x, y, top, v, K) {
  const { hh, tint } = K, cx = x + 0.5, cz = y + 0.5;
  if (v < 0.3) {
    const nC = v < 0.1 ? 2 : 1;
    for (let c = 0; c < nC; c++) {
      const fx = cx + (hh(x, y, 62 + c) - 0.5) * 0.55, fz = cz + (hh(x, y, 64 + c) - 0.5) * 0.55;
      const n = 4 + Math.floor(hh(x, y, 66 + c) * 3);
      for (let j = 0; j < n; j++) {
        const ox = (hh(x, y, 70 + j + c * 7) - 0.5) * 0.2, oz = (hh(x, y, 80 + j + c * 7) - 0.5) * 0.2, hg = 0.14 + hh(x, y, 90 + j + c * 7) * 0.2;
        P.add(list, fx + ox, top, fz + oz, 0.022, hg, 0.022, tint(0xb5b479, (hh(x, y, 100 + j) - 0.5) * 0.15));
        P.add(list, fx + ox + 0.04, top + hg - 0.01, fz + oz, 0.1, 0.022, 0.018, 0xf2ecd6);   // пір'їнка, хилить вітер
      }
    }
  } else if (v < 0.355) {
    const col = v < 0.33 ? 0xd8302a : 0x4a72d8;   // мак / волошка
    for (let j = 0; j < 3; j++) {
      const fx = cx + (hh(x, y, 110 + j) - 0.5) * 0.7, fz = cz + (hh(x, y, 113 + j) - 0.5) * 0.7, hg = 0.1 + hh(x, y, 116 + j) * 0.08;
      P.add(list, fx, top, fz, 0.025, hg, 0.025, 0x6f9a3c); P.add(list, fx, top + hg, fz, 0.08, 0.05, 0.08, col);
      if (col === 0xd8302a) P.add(list, fx, top + hg + 0.05, fz, 0.03, 0.006, 0.03, 0x1e1b18);
    }
  } else if (v < 0.5) {
    for (let j = 0; j < 3; j++) {
      const fx = cx + (hh(x, y, 120 + j) - 0.5) * 0.7, fz = cz + (hh(x, y, 123 + j) - 0.5) * 0.7;
      P.add(list, fx, top, fz, 0.05, 0.06 + hh(x, y, 126 + j) * 0.07, 0.05, tint(K.C.tuft, (j - 1) * 0.08));
    }
  }
}

// ---------- край смуги: земляний вал із ковилою ----------
export function wall(P, list, x, y, base, far, K) {
  const { hh, tint, C } = K, cx = x + 0.5, cz = y + 0.5;
  const v1 = hh(x, y, 11), v2 = hh(x, y, 12), v3 = hh(x, y, 13);
  const hg = far ? 0.45 + v1 * 0.35 : 0.22 + v1 * 0.15;
  P.add(list, cx, base, cz, 1.0, hg, 1.0, tint(EARTH, (v2 - 0.5) * 0.14));
  P.add(list, cx, base + hg, cz, 1.02, 0.07, 1.02, tint(C.grassA, (v2 - 0.5) * 0.1 - 0.04));
  if (far && v3 > 0.86) { P.add(list, cx, base + hg + 0.07, cz - 0.05, 0.7, 0.18, 0.6, tint(C.grassB, -0.05)); P.add(list, cx, base + hg + 0.25, cz - 0.05, 0.36, 0.12, 0.3, tint(C.grassA, 0)); }
  if (v3 < 0.55) for (let j = 0; j < 3; j++) {
    const fx = cx + (hh(x, y, 140 + j) - 0.5) * 0.7, fz = cz + (hh(x, y, 143 + j) - 0.5) * 0.7, h2 = 0.12 + hh(x, y, 146 + j) * 0.16;
    P.add(list, fx, base + hg + 0.07, fz, 0.022, h2, 0.022, 0xb5b479); P.add(list, fx + 0.04, base + hg + 0.06 + h2, fz, 0.09, 0.02, 0.018, 0xf2ecd6);
  }
}

// ---------- дрібні будівники для груп (cube → потім mergeGroup) ----------
function poplar(K, g, x, z, hg) {
  const { cube } = K;
  cube(g, x, 0.02, z, 0.12, 0.4, 0.12, 0x6a5a44);
  cube(g, x, 0.32, z, 0.42, hg * 0.5, 0.42, 0x4f7d36);
  cube(g, x, 0.32 + hg * 0.45, z, 0.34, hg * 0.35, 0.34, 0x5b8c3c);
  cube(g, x, 0.32 + hg * 0.78, z, 0.2, hg * 0.22, 0.2, 0x66984a);
}
function haystack(K, g, x, z, k) {
  const { cube } = K;
  [0.9, 0.75, 0.55, 0.3].forEach((f, l) => cube(g, x, 0.02 + l * 0.26 * k, z, f * k, 0.26 * k + 0.002, f * k, l % 2 ? 0xcaa64f : 0xd8b65c));
  cube(g, x, 0.02 + 1.04 * k, z, 0.04, 0.3 * k, 0.04, WD_D);
}
function kurgan(K, g, x, z, k, baba) {
  const { cube, C } = K;
  let y = -0.02;
  [1, 0.78, 0.55, 0.32].forEach((f, l) => { cube(g, x, y, z, 2.6 * f * k, 0.24 * k, 2.2 * f * k, th(K, C.grassB, 0.02 * l - 0.03)); y += 0.24 * k; });
  if (!baba) return;
  cube(g, x, y, z, 0.18, 0.36, 0.13, 0x9c978c);
  cube(g, x, y + 0.36, z, 0.14, 0.13, 0.12, 0xa8a398);
  cube(g, x, y + 0.16, z + 0.066, 0.16, 0.05, 0.01, 0x7f7a70);   // руки з чашею
  cube(g, x, y + 0.14, z + 0.07, 0.06, 0.06, 0.03, 0x8a857b);
}
function stork(K, parent, fly) {
  const { THREE, cube } = K, g = new THREE.Group(); parent.add(g);
  const W = 0xf7f5ef, B = 0x1d1d1d, R = 0xd8452a, wings = [];
  cube(g, 0, 0, 0, 0.32, 0.11, 0.12, W); cube(g, -0.2, 0.02, 0, 0.1, 0.06, 0.1, W);
  if (fly) {
    cube(g, 0.22, 0.03, 0, 0.16, 0.05, 0.05, W); cube(g, 0.32, 0.025, 0, 0.07, 0.065, 0.06, W); cube(g, 0.42, 0.04, 0, 0.14, 0.025, 0.025, R);
    for (const s of [-1, 1]) cube(g, -0.33, 0.03, s * 0.02, 0.22, 0.02, 0.02, R);
    for (const s of [-1, 1]) {
      const wg = new THREE.Group(); wg.position.set(0.02, 0.09, s * 0.06); g.add(wg);
      cube(wg, 0, -0.01, s * 0.2, 0.19, 0.02, 0.36, W); cube(wg, -0.02, -0.012, s * 0.47, 0.17, 0.022, 0.2, B);
      wings.push({ wg, s });
    }
  } else {
    cube(g, 0.13, 0.08, 0, 0.05, 0.18, 0.05, W); cube(g, 0.14, 0.25, 0, 0.08, 0.065, 0.06, W); cube(g, 0.23, 0.26, 0, 0.13, 0.025, 0.025, R);
    for (const s of [-1, 1]) { cube(g, 0, -0.3, s * 0.03, 0.025, 0.3, 0.025, R); cube(g, -0.05, 0.03, s * 0.062, 0.24, 0.06, 0.006, B); }
  }
  return { g, wings };
}
function nest(K, g, x, y, z) {
  const { cube } = K;
  for (const [dx, dz, sx, sz] of [[0, -0.2, 0.5, 0.1], [0, 0.2, 0.5, 0.1], [-0.2, 0, 0.1, 0.5], [0.2, 0, 0.1, 0.5]]) cube(g, x + dx, y, z + dz, sx, 0.12, sz, 0x6e5a3e);
  cube(g, x, y, z, 0.32, 0.06, 0.32, 0x5a4a32);
  const st = stork(K, g, false); st.g.position.set(x, y + 0.36, z); st.g.rotation.y = -0.6; st.g.scale.setScalar(1.3);
}
function windmill(K, parent, x, z, k, fx, ph = 0) {
  const { THREE, cube } = K;
  const g = new THREE.Group(); g.position.set(x, 0, z); g.scale.setScalar(k); parent.add(g);
  cube(g, 0, 0, 0, 0.22, 1.25, 0.22, WD_D);                                    // стовп-козли
  for (const [dx, dz] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) for (let j = 0; j < 4; j++) cube(g, dx * (0.42 - j * 0.1), j * 0.15, dz * (0.42 - j * 0.1), 0.08, 0.17, 0.08, WD_D);
  cube(g, 0, 1.15, 0, 0.9, 1.05, 0.75, 0x9b7448);                              // будка млина
  for (let j = 0; j < 4; j++) cube(g, 0, 1.3 + j * 0.24, 0.376, 0.92, 0.02, 0.01, WD_D);
  cube(g, 0.2, 1.2, -0.376, 0.22, 0.45, 0.01, 0x5a3a20);                       // двері ззаду
  for (let l = 0; l < 4; l++) cube(g, 0, 2.2 + l * 0.12, 0, 1.02 - l * 0.27, 0.12, 0.88, l % 2 ? 0x8a6a3a : 0x7a5c32);   // дах
  for (let j = 0; j < 8; j++) cube(g, 0, 1.1 - j * 0.14, -0.42 - j * 0.12, 0.08, 0.08, 0.14, WD_D);   // водило
  const hub = new THREE.Group(); hub.position.set(0, 1.85, 0.44); hub.rotation.z = ph; g.add(hub);
  cube(hub, 0, -0.07, -0.04, 0.14, 0.14, 0.12, WD_D);
  for (let a = 0; a < 4; a++) {
    const arm = new THREE.Group(); arm.rotation.z = a * Math.PI / 2; hub.add(arm);
    cube(arm, 0, 0, 0, 0.07, 1.6, 0.05, WD);
    cube(arm, 0.15, 0.35, 0.012, 0.26, 1.18, 0.02, 0xeee6d2);
    for (let q = 0; q < 5; q++) cube(arm, 0.15, 0.38 + q * 0.27, 0.028, 0.3, 0.025, 0.02, WD_D);
    cube(arm, 0.29, 0.35, 0.028, 0.02, 1.18, 0.02, WD_D);
  }
  fx.push((dt) => { hub.rotation.z -= dt * 0.55; });
  return g;
}
function wellCrane(K, g, x, z) {
  const { cube } = K;
  for (let l = 0; l < 4; l++) {
    const yy = 0.02 + l * 0.1, a = l % 2 ? WD : WD_D, b = l % 2 ? WD_D : WD;
    cube(g, x, yy, z - 0.3, 0.7, 0.1, 0.09, a); cube(g, x, yy, z + 0.3, 0.7, 0.1, 0.09, a);
    cube(g, x - 0.3, yy, z, 0.09, 0.1, 0.52, b); cube(g, x + 0.3, yy, z, 0.09, 0.1, 0.52, b);
  }
  cube(g, x, 0.36, z, 0.52, 0.02, 0.52, 0x2f6f9a);
  // сошка праворуч від зрубу, жердина в площині XY: довгий кінець над зрубом, короткий з тягарем — угору
  const px = x + 0.52, py = 1.45, zz = z - 0.05;
  cube(g, px, 0.02, zz, 0.11, py, 0.11, WD_D);
  cube(g, px, py, zz - 0.06, 0.06, 0.12, 0.04, WD_D); cube(g, px, py, zz + 0.06, 0.06, 0.12, 0.04, WD_D);
  const ax = x, ay = 0.95, bx = px + (px - ax) * 0.5, by = py + 0.06 + (py + 0.06 - ay) * 0.5;
  const pole = cube(g, 0, 0, 0, Math.hypot(bx - ax, by - ay), 0.06, 0.06, WD_L);
  pole.position.set((ax + bx) / 2, (ay + by) / 2, zz); pole.rotation.z = Math.atan2(by - ay, bx - ax);
  cube(g, ax, 0.5, zz, 0.012, ay - 0.5, 0.012, 0x3a3a3a);
  cube(g, ax, 0.4, zz, 0.12, 0.11, 0.12, WD_L);
  cube(g, bx, by - 0.24, zz, 0.15, 0.17, 0.15, 0x8a8f96);
}
function tent(K, g, x, z, sx, sz, a, b) {
  const { cube } = K, n = 6;
  for (let k = 0; k < n; k++) cube(g, x - sx / 2 + sx / n * (k + 0.5), 0.02, z, sx / n + 0.002, 0.75, sz, k % 2 ? b : a);
  [1.08, 0.82, 0.56, 0.3].forEach((f, l) => {
    for (let k = 0; k < n; k++) cube(g, x - sx * f / 2 + sx * f / n * (k + 0.5), 0.77 + l * 0.17, z, sx * f / n + 0.002, 0.17, sz * f, k % 2 ? a : b);
  });
  cube(g, x, 0.02, z + sz / 2 + 0.005, sx * 0.34, 0.56, 0.02, 0x3a2418);                // вхід
  cube(g, x, 1.45, z, 0.04, 0.5, 0.04, WD_D); cube(g, x + 0.14, 1.75, z, 0.24, 0.14, 0.02, a);
  // прилавок із товаром: глечики, хліб, сіль
  const cx = x + sx * 0.28, cz = z + sz / 2 + 0.28;
  cube(g, cx, 0.02, cz, sx * 0.38, 0.32, 0.3, WD);
  for (let k = 0; k < 3; k++) {
    const gx = cx - sx * 0.12 + k * sx * 0.12;
    if (k === 1) { cube(g, gx, 0.34, cz, 0.16, 0.07, 0.16, 0xb5773c); cube(g, gx, 0.41, cz, 0.12, 0.03, 0.12, 0xc88a48); }
    else { cube(g, gx, 0.34, cz, 0.11, 0.11, 0.11, 0xb8653a); cube(g, gx, 0.45, cz, 0.06, 0.05, 0.06, 0xa65530); }
  }
  cube(g, x - sx * 0.3, 0.02, z + sz / 2 + 0.25, 0.3, 0.2, 0.25, SALT_B);
}
function saltHeap(K, g, x, z, k) {
  const { cube } = K, L = 6;
  for (let l = 0; l < L; l++) { const f = 1 - l / L; cube(g, x + (l % 2) * 0.04, 0.02 + l * 0.2 * k / 2.4, z - (l % 3) * 0.03, k * f, 0.2 * k / 2.4 + 0.002, k * f * 0.85, l % 2 ? SALT_B : SALT); }
  for (let j = 0; j < 4; j++) cube(g, x - k * 0.55 + j * 0.2, 0.02, z + k * 0.48 - (j % 2) * 0.15, 0.1, 0.07, 0.1, SALT);
  cube(g, x + k * 0.2, 0.02 + k * 0.2, z + k * 0.2, 0.04, 0.55, 0.04, WD);   // лопата
  cube(g, x + k * 0.2, 0.02 + k * 0.2 - 0.02, z + k * 0.2, 0.14, 0.18, 0.02, WD_L);
}
function barrel(K, g, x, z, y) {
  const { cube } = K;
  cube(g, x, y, z, 0.3, 0.4, 0.3, 0x8a5a32); cube(g, x, y + 0.1, z, 0.34, 0.2, 0.34, 0x94643a);
  cube(g, x, y + 0.06, z, 0.345, 0.035, 0.345, 0x3a3532); cube(g, x, y + 0.3, z, 0.345, 0.035, 0.345, 0x3a3532);
  cube(g, x, y + 0.4, z, 0.24, 0.03, 0.24, SALT);
}
function melons(K, g, x, z) {
  const { cube } = K;
  for (let k = 0; k < 9; k++) {
    const mx = x + (k % 3) * 0.22, mz = z + Math.floor(k / 3) * 0.22, kav = k % 4 !== 3;
    cube(g, mx, 0.02, mz, 0.19, 0.17, 0.19, kav ? 0x2f6b2a : 0xe0862a);
    if (kav) cube(g, mx, 0.02, mz, 0.2, 0.172, 0.05, 0x6aa23e);
    else cube(g, mx, 0.19, mz, 0.03, 0.04, 0.03, 0x4a6a2a);
  }
  cube(g, x + 0.22, 0.19, z + 0.22, 0.19, 0.17, 0.19, 0x2f6b2a);
}
// припаркована мажа (з сіллю) — група з власним масштабом 1
function parkedCart(K, g, x, z, ry) {
  const { THREE, cube } = K, p = new THREE.Group(); p.position.set(x, -GY + 0.02, z); p.rotation.y = ry; g.add(p);
  const bed = cartBody(K, p, 0, 0.84, null);
  cube(p, 0, bed, 0, 0.74, 0.18, 0.38, SALT); cube(p, 0, bed + 0.18, 0, 0.62, 0.08, 0.3, CLOTH);
  cube(p, 0.62, GY + 0.04, 0, 0.5, 0.04, 0.04, WD_D);
  return p;
}
function cherry(K, g, x, z) {
  const { cube } = K;
  cube(g, x, 0.02, z, 0.14, 0.55, 0.14, 0x5a3c28);
  cube(g, x, 0.5, z, 0.85, 0.5, 0.8, 0x3f7a32); cube(g, x + 0.05, 0.95, z - 0.03, 0.55, 0.3, 0.55, 0x4a8a3a);
  for (let j = 0; j < 6; j++) cube(g, x + Math.cos(j * 2.1) * 0.36, 0.6 + (j % 3) * 0.12, z + 0.41, 0.06, 0.06, 0.03, 0xb01c2a);
}

// ---------- хутір на старті (замість гори з тунелем) ----------
export function village(K, root, P, list, x0, s, fx) {
  const { THREE, cube, mergeGroup, hh, G } = K, mid = s.h >> 1, my = mid + 0.5;
  for (let x = x0; x < 0; x++) rail(P, list, x, mid, 0.015 + Math.floor(hh(x, mid, 3) * 3) * 0.012, 2, 0, G.GRASS, K);
  const g = new THREE.Group(); root.add(g);
  // хата-біленька під стріхою: призьба, вікна з віконницями, двері, димар, лелече гніздо
  const hx = -7.2, hz = my - 3.1;
  cube(g, hx, 0.02, hz, 2.6, 0.16, 1.7, 0xc49a62);
  cube(g, hx, 0.18, hz, 2.4, 0.95, 1.5, 0xf4f1e8);
  cube(g, hx, 0.18, hz + 0.751, 2.42, 0.1, 0.01, 0x3d6fb0);                            // синя смуга знизу
  for (const wx of [-0.65, 0.65]) {
    cube(g, hx + wx, 0.5, hz + 0.752, 0.38, 0.38, 0.02, 0x3d6fb0); cube(g, hx + wx, 0.53, hz + 0.762, 0.3, 0.32, 0.01, 0xbfe3f0);
    cube(g, hx + wx, 0.53, hz + 0.768, 0.02, 0.32, 0.01, 0x3d6fb0);
    for (const sd of [-1, 1]) cube(g, hx + wx + sd * 0.27, 0.5, hz + 0.76, 0.14, 0.38, 0.02, 0x2f8a5a);   // віконниці
  }
  cube(g, hx, 0.18, hz + 0.752, 0.42, 0.7, 0.02, 0x6a4428);
  for (let l = 0; l < 5; l++) cube(g, hx, 1.12 + l * 0.17, hz, 2.75 - l * 0.12, 0.17, 1.85 - l * 0.38, l % 2 ? 0xbf9f50 : 0xc8a95a);   // стріха
  cube(g, hx, 1.97, hz, 2.3, 0.09, 0.2, 0xa88a40);
  cube(g, hx - 0.6, 1.4, hz - 0.2, 0.22, 0.8, 0.22, 0xf4f1e8);
  nest(K, g, hx + 0.9, 2.06, hz);
  // тин із глечиками на кілках, ворота навпроти хати
  const fz = my - 1.45;
  for (let x = -11.2; x < -3.4; x += 0.42) {
    if (Math.abs(x - hx) < 0.55) continue;
    cube(g, x, 0.02, fz, 0.06, 0.62, 0.06, WD_D);
    const k = Math.round((x + 20) / 0.42);
    if (k % 3 === 0) { cube(g, x, 0.64, fz, 0.13, 0.12, 0.13, k % 2 ? 0xb8653a : 0x8a4a2a); cube(g, x, 0.6, fz, 0.06, 0.05, 0.06, 0xa65530); }
    for (const yy of [0.12, 0.3, 0.46]) cube(g, x + 0.21, yy + (k % 2) * 0.02, fz, 0.42, 0.1, 0.05, (k + yy * 10) % 2 > 1 ? 0x8a6a42 : 0x7a5c38);
  }
  // соняшники за тином
  for (let x = -10.8; x < -4; x += 0.55) {
    if (Math.abs(x - hx) < 0.9) continue;
    const z = fz - 0.35 + hh(Math.round(x * 10), 5, 7) * 0.12, hg = 0.95 + hh(Math.round(x * 10), 5, 8) * 0.3;
    cube(g, x, 0.02, z, 0.05, hg, 0.05, 0x5c8a2e); cube(g, x + 0.08, hg * 0.5, z, 0.14, 0.03, 0.08, 0x6a9a34);
    cube(g, x, hg, z + 0.04, 0.3, 0.3, 0.06, 0xf2c21a); cube(g, x, hg + 0.06, z + 0.075, 0.16, 0.16, 0.02, 0x5a3a1a);
  }
  cherry(K, g, hx - 1.9, hz + 0.2); cherry(K, g, hx + 2.0, hz - 0.2);
  wellCrane(K, g, -4.4, my + 2.4);
  haystack(K, g, -11, my + 2.8, 1.2); haystack(K, g, -12.6, my + 3.4, 0.9);
  poplar(K, g, -13.5, my - 3.5, 2.6); poplar(K, g, -12.6, my - 3.9, 2.9); poplar(K, g, -2.4, my - 4.3, 2.4);
  parkedCart(K, g, -9.4, my + 2.2, 0.35);
  // колесо, приперте до тину
  const wl = wheel(K, g, -5.2, fz + 0.1, 0.17); wl.position.y = 0.19; wl.rotation.x = -0.25;
  mergeGroup(g);
}

// ---------- ярмарок у Криму на станції (g — група станції в (w, 0, mid + 0,5)) ----------
export function fair(K, g, s, fx) {
  const { cube } = K;
  for (const z of [-0.42, 0.42]) cube(g, -0.12, 0, z, 0.09, 0.52, 0.09, WD_D);         // коновʼязь у кінці шляху
  cube(g, -0.12, 0.42, 0, 0.08, 0.06, 0.96, WD);
  tent(K, g, 2.3, -3.0, 2.0, 1.5, 0xc8322a, 0xf3e6c8);
  tent(K, g, 4.9, -3.4, 1.5, 1.2, 0x2f6fb5, 0xf2c94c);
  saltHeap(K, g, 5.3, 0.4, 2.4);
  saltHeap(K, g, 3.5, 2.7, 1.3);
  for (const [x, z, y] of [[1.3, 1.9, 0], [1.68, 2.18, 0], [1.3, 2.5, 0], [1.45, 2.2, 0.42]]) barrel(K, g, x, z, y + 0.02);
  melons(K, g, 2.3, 1.4);
  wellCrane(K, g, 1.0, -1.7);
  windmill(K, g, 6.6, -2.3, 1.15, fx, 0.4);
  // козацький стяг
  cube(g, 0.75, 0.02, 2.9, 0.07, 2.5, 0.07, WD_L); cube(g, 0.75, 2.5, 2.9, 0.12, 0.12, 0.12, 0xf2c94c);
  cube(g, 0.75, 1.85, 3.24, 0.025, 0.55, 0.62, 0xa0163c); cube(g, 0.75, 1.83, 3.24, 0.03, 0.04, 0.64, 0xf2c94c);
  // віз і воли на постої: один лежить, другий пасеться
  parkedCart(K, g, 2.6, 3.7, -0.15);
  const o1 = ox(K, g, 4.1, 0.02, 3.4, 0, 0.7); poseOx(o1, 0, 0, 1, 0); o1.root.rotation.y = 0.5;
  const o2 = ox(K, g, 4.7, 0.02, 4.1, 1, 2.1); poseOx(o2, 0, 0, 0, 0); o2.root.rotation.y = 2.4; o2.head.rotation.z = -0.7;
  // лелече гніздо на стовпі, тополі
  cube(g, 6.9, 0.02, 2.8, 0.13, 1.9, 0.13, WD_D); nest(K, g, 6.9, 1.92, 2.8);
  poplar(K, g, 7.3, -4.1, 2.6); poplar(K, g, 0.4, -4.2, 2.3); poplar(K, g, 7.6, 4.0, 2.2);
}

// ---------- степ за далеким краєм смуги + валка + лелеки ----------
export function steppe(K, root, s, fx, lowQ) {
  const { THREE, cube, mergeGroup, hh, C } = K;
  const X0 = -16, X1 = s.w + 22, mid = s.h >> 1;
  for (let x0 = X0; x0 < X1; x0 += 24) {
    const x1 = Math.min(X1, x0 + 24), cx = (x0 + x1) / 2, L = x1 - x0, g = new THREE.Group(); root.add(g);
    const r = (k) => hh(x0, 91, k);
    cube(g, cx, -0.62, -3, L, 0.6, 6, th(K, C.grassB, -0.05));
    cube(g, cx, -0.66, -9, L, 0.6, 6, th(K, C.grassB, -0.12));
    // чумацький шлях за валом
    cube(g, cx, -0.022, -1.7, L, 0.012, 0.75, DIRT);
    for (const o of [-0.2, 0.2]) cube(g, cx, -0.01, -1.7 + o, L, 0.004, 0.09, RUT);
    kurgan(K, g, x0 + 3 + r(1) * 17, -3.4 - r(2) * 1.6, 0.8 + r(3) * 0.5, r(4) < 0.6);
    if (r(5) < 0.8) windmill(K, g, x0 + 2 + r(6) * 20, -4.4 - r(7) * 1.2, 1.0 + r(8) * 0.25, fx, r(9) * 3);
    const pn = 2 + Math.floor(r(10) * 4), px = x0 + 2 + r(11) * 18, pz = -2.6 - r(12) * 1.4;
    for (let j = 0; j < pn; j++) poplar(K, g, px + j * 0.75, pz + (j % 2) * 0.3, 2.2 + hh(x0, j, 92) * 0.8);
    haystack(K, g, x0 + 1 + r(13) * 21, -2.7 - r(14) * 1.5, 0.9 + r(15) * 0.4);
    if (!lowQ) for (let j = 0; j < 36; j++) {
      const fx2 = x0 + hh(x0, j, 93) * L, fz = -0.4 - hh(x0, j, 94) * 4.6;
      if (Math.abs(fz + 1.7) < 0.5) continue;
      for (let q = 0; q < 3; q++) { const hg = 0.15 + hh(j, q, 95) * 0.18; cube(g, fx2 + (q - 1) * 0.06, -0.02, fz + ((q * 7) % 3 - 1) * 0.05, 0.022, hg, 0.022, 0xb5b479); cube(g, fx2 + (q - 1) * 0.06 + 0.04, hg - 0.03, fz + ((q * 7) % 3 - 1) * 0.05, 0.09, 0.02, 0.018, 0xf2ecd6); }
    }
    mergeGroup(g);
  }
  // валка: три мажі з волами повільно йдуть шляхом за валом (по колу вздовж усієї смуги)
  const cv = new THREE.Group(); cv.position.set(X0, 0.13 - 0.02, -1.7); root.add(cv);
  const oxen = [], wheels = [];
  for (let k = 0; k < 3; k++) {
    const c = new THREE.Group(); c.position.x = -k * 1.55; cv.add(c);
    const bed = cartBody(K, c, -0.2, 0.6, wheels);
    cube(c, -0.22, bed, 0, 0.5, 0.16, 0.38, k === 1 ? 0xd8b862 : SALT); cube(c, -0.22, bed + 0.16, 0, 0.42, 0.06, 0.3, CLOTH);
    oxen.push(ox(K, c, 0.42, GY, -0.17, k & 1, k), ox(K, c, 0.42, GY, 0.17, (k + 1) & 1, k + Math.PI));
    cube(c, 0.58, GY + 0.44, 0, 0.055, 0.055, 0.66, WD_D); cube(c, 0.34, GY + 0.3, 0, 0.48, 0.04, 0.04, WD_D);
  }
  mergeGroup(cv);
  const span = X1 - X0 + 4, SP = 0.3;
  let ph = 0, d0 = (s.seed % 97) / 97 * span;
  fx.push((dt, now) => {
    d0 = (d0 + dt * SP) % span; cv.position.x = X0 + d0;
    ph += dt * SP * 13; for (const w of wheels) w.rotation.z -= dt * SP / w.userData.r;
    for (const o of oxen) poseOx(o, ph, 1, 0, now);
  });
  // лелеки кружляють над смугою за камерою
  const birds = [0, 1, 2].map((k) => {
    const b = stork(K, root, true); b.g.scale.setScalar(1.7);
    return { ...b, a: k * 2.1, R: 5 + k * 1.6, y: 3.6 + k * 0.6, w: 0.16 + k * 0.03, cx: 0, cz: mid - 2 + k, off: k * 1.3, init: false };
  });
  fx.push((dt, now, cam) => {
    for (const b of birds) {
      const tx = (cam?.x ?? 0) + (b.off - 1.3) * 3;
      if (!b.init) { b.cx = tx; b.init = true; }
      b.cx += (tx - b.cx) * Math.min(1, dt * 0.3);
      b.a += dt * b.w;
      const x = b.cx + Math.cos(b.a) * b.R, z = b.cz + Math.sin(b.a) * b.R * 0.45;
      b.g.position.set(x, b.y + Math.sin(now / 1700 + b.off) * 0.25, z);
      b.g.rotation.y = -Math.atan2(Math.cos(b.a) * b.R * 0.45, -Math.sin(b.a) * b.R);
      const flap = Math.sin(now / 2300 + b.off * 2) > 0.35 ? 0.6 : 0.08;
      for (const { wg, s: sd } of b.wings) wg.rotation.x = sd * Math.sin(now / 140 + b.off) * flap;
    }
  });
}
