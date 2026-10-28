// «Скарб Полуботка» — 3D-сцена поля («розріз землі під селом»). API — рівно контракт §10 (docs/games/specs/skarb.md):
//   const sc = await createScene(host, { onPick, onHover, quality: 'high'|'low', reducedMotion });
//   sc.setGrid(grid); sc.setState(view); sc.setLegal({ kind, cells, alt, goals, ghost }); sc.setCursor(x, y | null);
//   await sc.dig(ev); await sc.flip(ev); await sc.rockfall(ev); await sc.peek(ev, kind); await sc.finale(side);
//   sc.focus(x, y); sc.screenPos(kind, a, b); sc.resetView(); sc.resize(); sc.dispose();
// Сцена не знає правил і не шле дій — лише малює й каже, куди клацнули (лише по підсвіченому).
//
// Будова: стіна розрізу — ОДИН InstancedMesh грудок (17 × 9 клітинок × 9 грудок); прокопана карта ховає грудки на
// відкритих краях і в середині (scale 0). Усе нерухоме (поверхня, хата, зруб, задня стінка, декоративні стовпці) — один
// злитий меш із кольорами вершин; небо — ще один. Кріплення, ліхтарі, плями світла, валуни, фігурки, монети, каміння,
// пил, підсвітка — InstancedMesh. Справжніх точкових світел два (спалах анімації й сяйво скрині), решта — емісив і плями;
// тепло ліхтарів на грудках — колір екземпляра. Клік — без raycast по мешах: перетин променя з площиною Z = 0 і
// найближча підсвічена клітинка в межах 0,7 клітинки.

import * as THREE from '/static/vendor/three-0.186.1/three.module.js';
import * as M from './models.js';
import { GRID, BIT, DIRS, STEP, SEAT_COLOR, PAL } from './consts.js';

const D2R = Math.PI / 180;
const FOV = 35, DEF_EL = 8 * D2R, EL_MIN = 0, EL_MAX = 25 * D2R, AZ_LIM = 25 * D2R, ZOOM_MIN = 0.6, ZOOM_MAX = 2.5;
const BG = '#140d09';
const L = M.L;
const EDGE_LUMP = { N: 1, E: 5, S: 7, W: 3 };
const SHAFT_HOLES = (1 << 1) | (1 << 4) | (1 << 7);
const FIG_S = 1.45;          // фігурки трохи більші — щоб читались на телефоні
const FIG_Z = 0.25;
const CH_S = 1.35;         // скриня більша за «правдиву»
const CH_Z = 0.3;          // скриня стоїть перед лицем розрізу (кришка не ховається в грудки)          // перший ряд фігурок — перед лицем розрізу
const TAP_PX = 8;
const IDLE_MS = 2000;

const clamp = (x, a, b) => Math.max(a, Math.min(b, x));
const k01 = (t, a, b) => clamp((t - a) / (b - a), 0, 1);
const easeOut = (k) => 1 - Math.pow(1 - k, 3);
const easeIn = (k) => k * k * k;
const easeInOut = (k) => k < 0.5 ? 4 * k * k * k : 1 - Math.pow(-2 * k + 2, 3) / 2;
function backOut(k) { const c1 = 2.0, c3 = c1 + 1; return 1 + c3 * Math.pow(k - 1, 3) + c1 * Math.pow(k - 1, 2); }
const key = (x, y) => x + ',' + y;
const hash = (a, b = 0) => { let h = Math.imul(a | 0, 374761393) ^ Math.imul(b | 0, 668265263); h = Math.imul(h ^ (h >>> 13), 1274126177); return ((h ^ (h >>> 16)) >>> 0) / 4294967296; };

/** Які грудки клітинки (біти 0–8, 0 — лівий верхній) прибрано для карти з маскою m. Середина — завжди (глуха: там валун). */
function holesOf(m, c) {
  if (!m) return 0;
  let h = 1 << 4;
  for (const d of DIRS) if (m & BIT[d]) h |= 1 << EDGE_LUMP[d];
  return h;
}
function gridKey(g) {
  return JSON.stringify([g.x0, g.x1, g.y0, g.y1, g.start || [0, 0], g.goals || [], g.shaft || []]);
}

export async function createScene(host, opts = {}) {
  const quality = opts.quality === 'low' ? 'low' : 'high';
  const hi = quality === 'high';
  const reduced = !!opts.reducedMotion;
  const bgAnim = hi && !reduced;
  const dustOn = hi && !reduced;
  const onPick = typeof opts.onPick === 'function' ? opts.onPick : () => {};
  const onHover = typeof opts.onHover === 'function' ? opts.onHover : () => {};

  // ------------------------------------------------------------------------------------------------ рендерер
  let renderer;
  try {
    renderer = new THREE.WebGLRenderer({ antialias: hi, powerPreference: 'high-performance' });
  } catch (e) {
    throw new Error('WebGL недоступний: ' + (e && e.message || e));
  }
  if (!renderer.getContext()) { renderer.dispose(); throw new Error('WebGL недоступний'); }
  renderer.setPixelRatio(hi ? Math.min(2, window.devicePixelRatio || 1) : Math.min(1, window.devicePixelRatio || 1));
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  renderer.toneMapping = THREE.NeutralToneMapping;
  renderer.toneMappingExposure = 1.1;
  renderer.shadowMap.enabled = hi;
  renderer.shadowMap.type = THREE.PCFShadowMap;
  const canvas = renderer.domElement;
  canvas.style.cssText = 'display:block;width:100%;height:100%;touch-action:none;outline:none;-webkit-tap-highlight-color:transparent;user-select:none';
  host.appendChild(canvas);

  const scene = new THREE.Scene();
  scene.background = new THREE.Color(BG);
  const cam = new THREE.PerspectiveCamera(FOV, 1, 0.1, 300);

  const hemi = new THREE.HemisphereLight('#a8b6e0', '#5a3a22', 1.0);
  scene.add(hemi);
  const warm = new THREE.AmbientLight('#ffa860', 0.35);
  scene.add(warm);
  const moon = new THREE.DirectionalLight('#d6e0ff', 1.55);
  moon.castShadow = hi;
  if (hi) {
    moon.shadow.mapSize.set(1024, 1024);
    moon.shadow.bias = -0.0006; moon.shadow.normalBias = 0.015;
  }
  scene.add(moon); scene.add(moon.target);
  const flash = new THREE.PointLight(PAL.lamp, 0, 3.2, 1.5);
  const chestLight = new THREE.PointLight(PAL.gold, 0, 3.6, 1.5);
  scene.add(flash); scene.add(chestLight);

  const matVC = new THREE.MeshStandardMaterial({ vertexColors: true, flatShading: true, roughness: 0.92, metalness: 0 });
  const matMetal = new THREE.MeshStandardMaterial({ vertexColors: true, flatShading: true, roughness: 0.42, metalness: 0.3, emissive: new THREE.Color('#3a2400') });
  const matGlowVC = new THREE.MeshBasicMaterial({ vertexColors: true, toneMapped: false });
  const matSky = new THREE.MeshBasicMaterial({ vertexColors: true, fog: false, depthWrite: false });
  const matAdd = new THREE.MeshBasicMaterial({ vertexColors: true, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, toneMapped: false });
  const matBeam = new THREE.MeshBasicMaterial({ vertexColors: true, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, toneMapped: false, opacity: 0, side: THREE.DoubleSide });
  const matHL = new THREE.MeshBasicMaterial({ vertexColors: true, transparent: true, depthWrite: false, toneMapped: false, opacity: 0.95 });
  const matCursor = new THREE.MeshBasicMaterial({ vertexColors: true, transparent: true, depthWrite: false, toneMapped: false });
  const matGhost = new THREE.MeshStandardMaterial({ vertexColors: true, transparent: true, opacity: 0.45, depthWrite: false, flatShading: true,
    color: new THREE.Color(PAL.alt), emissive: new THREE.Color('#8a5c18') });
  const matDust = new THREE.MeshStandardMaterial({ color: '#a2856a', flatShading: true, transparent: true, opacity: 0.8, depthWrite: false });
  const mats = [matVC, matMetal, matGlowVC, matSky, matAdd, matBeam, matHL, matCursor, matGhost, matDust];

  // ------------------------------------------------------------------------------------------------ інстанси
  const _m4 = new THREE.Matrix4(), _q = new THREE.Quaternion(), _eu = new THREE.Euler(), _pv = new THREE.Vector3(), _sv = new THREE.Vector3();
  const _c = new THREE.Color(), _c2 = new THREE.Color();
  const ZERO = new THREE.Matrix4().makeScale(0, 0, 0);
  function inst(geo, cap, { mat = matVC, color = false, cast = false, recv = false, parent = scene, order = 0 } = {}) {
    const m = new THREE.InstancedMesh(geo, mat, Math.max(1, cap));
    m.instanceMatrix.setUsage(THREE.DynamicDrawUsage);
    m.frustumCulled = false;
    m.castShadow = cast && hi; m.receiveShadow = recv && hi;
    if (color) { const w = new THREE.Color(1, 1, 1); for (let i = 0; i < m.count; i++) m.setColorAt(i, w); m.instanceColor.setUsage(THREE.DynamicDrawUsage); }
    m.count = 0; m.visible = false; m.renderOrder = order;
    parent.add(m);
    return m;
  }
  function setM(mesh, i, x, y, z, rx, ry, rz, sx, sy = sx, sz = sx) {
    _eu.set(rx, ry, rz, 'YXZ');
    mesh.setMatrixAt(i, _m4.compose(_pv.set(x, y, z), _q.setFromEuler(_eu), _sv.set(sx, sy, sz)));
  }
  function finish(mesh, n) {
    mesh.count = n; mesh.visible = n > 0;
    mesh.instanceMatrix.needsUpdate = true;
    if (mesh.instanceColor) mesh.instanceColor.needsUpdate = true;
  }

  // Незалежне від поля.
  const figShirt = inst(M.figShirtGeo(), 10, { color: true, cast: true });
  const figBody = inst(M.figBodyGeo(), 10, { cast: true });
  const figPick = inst(M.pickGeo(), 10, { cast: true });
  const boards = inst(M.goalBoardGeo(), 6, { cast: true, recv: true });
  const peekCoin = inst(M.peekCoinGeo(), 4, { mat: matMetal });
  const peekStone = inst(M.peekStoneGeo(), 4);
  const rockGeo = M.rockGeo();
  const pileRocks = inst(rockGeo, 14, { color: true, cast: true });
  const fallRocks = inst(rockGeo, 14, { color: true, cast: true });
  const coinGeo = M.coinGeo();
  const pileCoins = inst(coinGeo, 16, { mat: matMetal });
  const flyCoins = inst(coinGeo, 56, { mat: matMetal });
  const dust = inst(M.dustGeo(), 40, { mat: matDust, order: 4 });
  const moles = inst(M.moleGeo(), 6, { cast: true });
  const chestBody = new THREE.Mesh(M.chestBodyGeo(), matMetal);
  const chestLid = new THREE.Mesh(M.chestLidGeo(), matMetal);
  chestBody.castShadow = chestLid.castShadow = hi; chestBody.visible = chestLid.visible = false;
  scene.add(chestBody); scene.add(chestLid);
  const mapSheet = new THREE.Mesh(M.mapSheetGeo(), matVC);
  const mapRollA = new THREE.Mesh(M.mapRollGeo(), matVC), mapRollB = new THREE.Mesh(mapRollA.geometry, matVC);
  const beam = new THREE.Mesh(M.beamGeo(), matBeam);
  for (const o of [mapSheet, mapRollA, mapRollB, beam]) { o.visible = false; scene.add(o); }
  beam.renderOrder = 6;
  const cursor = new THREE.Mesh(M.cursorGeo(), matCursor);
  cursor.visible = false; cursor.renderOrder = 8; scene.add(cursor);
  const ghostLumps = inst(M.lumpGeo(), 9, { mat: matGhost, order: 7 });
  const ghostBoulder = new THREE.Mesh(M.boulderGeo(), matGhost);
  ghostBoulder.visible = false; ghostBoulder.renderOrder = 7; scene.add(ghostBoulder);

  // ------------------------------------------------------------------------------------------------ поле (сітка)
  let G = null, gkey = null, gridGroup = null;
  let wall = null, supports = null, lanternF = null, lanternG = null, glows = null, boulders = null, hlFrames = null, altIcons = null;
  let craneArm = null, craneRope = null, craneTip = null;
  // базові дані грудок
  let bX, bY, bZ, bRx, bRy, bRz, bS, bCol, nLumps = 0;
  let holes = null;   // Uint16Array: які грудки клітинки сховані (поточний стан моделі)

  function cellIndex(x, y) {
    if (!G || x < G.x0 || x > G.x1 || y < G.y0 || y > G.y1) return -1;
    return (y - G.y0) * G.W + (x - G.x0);
  }

  function disposeGrid() {
    if (!gridGroup) return;
    gridGroup.traverse((o) => { if (o.isInstancedMesh) o.dispose(); if (o.geometry) o.geometry.dispose(); });
    scene.remove(gridGroup);
    gridGroup = null; wall = supports = lanternF = lanternG = glows = boulders = hlFrames = altIcons = null;
    craneArm = craneRope = null;
  }

  function setGrid(g) {
    if (!g || g.x0 == null) return;
    const k = gridKey(g);
    if (k === gkey) return;
    disposeGrid();
    gkey = k;
    const W = g.x1 - g.x0 + 1, H = g.y1 - g.y0 + 1;
    G = { x0: g.x0, x1: g.x1, y0: g.y0, y1: g.y1, W, H, n: W * H, start: g.start || [0, 0], goals: (g.goals || []).map((p) => p.slice()),
      shaft: new Set((g.shaft || []).map(([x, y]) => key(x, y))), surfY: -g.y0 + 0.5, botY: -g.y1 - 0.5 };
    gridGroup = new THREE.Group();
    scene.add(gridGroup);

    // Стіна розрізу.
    nLumps = G.n * 9;
    wall = inst(M.lumpGeo(), nLumps, { color: true, cast: true, recv: true, parent: gridGroup });
    bX = new Float32Array(nLumps); bY = new Float32Array(nLumps); bZ = new Float32Array(nLumps);
    bRx = new Float32Array(nLumps); bRy = new Float32Array(nLumps); bRz = new Float32Array(nLumps); bS = new Float32Array(nLumps);
    bCol = new Float32Array(nLumps * 3);
    for (let y = G.y0; y <= G.y1; y++) for (let x = G.x0; x <= G.x1; x++) {
      const ci = cellIndex(x, y), base = M.col(M.strataHex(y));
      for (let j = 0; j < 9; j++) {
        const i = ci * 9 + j, r = (s) => hash(i * 7 + s, 911);
        bX[i] = x + ((j % 3) - 1) * L; bY[i] = -y - (Math.floor(j / 3) - 1) * L; bZ[i] = (r(1) - 0.5) * 0.028;
        bRx[i] = (r(2) - 0.5) * 0.05; bRy[i] = (r(3) - 0.5) * 0.05; bRz[i] = Math.floor(r(4) * 4) * Math.PI / 2 + (r(5) - 0.5) * 0.05;
        bS[i] = 0.975 + r(6) * 0.04;
        const n = 1 + (r(7) - 0.5) * 0.08;
        bCol[i * 3] = base.r * n; bCol[i * 3 + 1] = base.g * n; bCol[i * 3 + 2] = base.b * n;
      }
    }
    wall.count = nLumps; wall.visible = true;
    holes = new Uint16Array(G.n);

    // Нерухоме.
    const st = M.buildStatic(g, BG);
    const lit = new THREE.Mesh(st.lit, matVC);
    lit.castShadow = lit.receiveShadow = hi; lit.matrixAutoUpdate = false;
    const glowM = new THREE.Mesh(st.glow, matGlowVC); glowM.matrixAutoUpdate = false;
    const sky = new THREE.Mesh(st.sky, matSky); sky.matrixAutoUpdate = false; sky.renderOrder = -10;
    craneArm = new THREE.Mesh(st.craneArm, matVC); craneArm.position.copy(st.pivot); craneArm.castShadow = hi;
    craneRope = new THREE.Mesh(st.craneRope, matVC); craneRope.castShadow = hi;
    craneTip = st.tip.clone();
    gridGroup.add(lit, glowM, sky, craneArm, craneRope);
    G.pivot = st.pivot.clone();

    supports = inst(M.supportGeo(), G.n * 4 + 8, { cast: true, recv: true, parent: gridGroup });
    lanternF = inst(M.lanternFrameGeo(), G.n, { parent: gridGroup });
    lanternG = inst(M.lanternGlassGeo(), G.n, { mat: matGlowVC, color: true, parent: gridGroup });
    glows = inst(M.glowGeo(), G.n * 2 + 6, { mat: matAdd, color: true, parent: gridGroup, order: 3 });
    boulders = inst(M.boulderGeo(), G.n, { cast: true, recv: true, parent: gridGroup });
    hlFrames = inst(M.frameGeo(), G.n, { mat: matHL, color: true, parent: gridGroup, order: 5 });
    altIcons = inst(M.altIconGeo(), G.n, { mat: matHL, color: true, parent: gridGroup, order: 6 });

    // Місяць: зліва згори спереду; тінь — лише на high, 1024², рамка — усе поле з поверхнею.
    const cx = (G.x0 + G.x1) / 2, cy = (G.surfY + G.botY) / 2 + 0.6;
    moon.target.position.set(cx, cy, 0);
    moon.position.set(cx - 7, cy + 18, 10);
    if (hi) {
      const hw = W / 2 + 4.5, hh = H / 2 + 4;
      Object.assign(moon.shadow.camera, { left: -hw, right: hw, top: hh, bottom: -hh, near: 1, far: 60 });
      moon.shadow.camera.updateProjectionMatrix();
    }
    animBg(0, true);
    if (!view) readView(null);
    applyAll();
    fit();
    dirty = true;
  }

  // ------------------------------------------------------------------------------------------------ модель стану
  // cards: усі карти поля, крім міток (колодязь — теж), у порядку з view.board; goals: три мітки; peeks: мої підглядини.
  const S = { cards: new Map(), order: [], linked: new Set(), goals: [], peeks: {}, players: [] };
  let view = null;

  function readView(v) {
    S.cards = new Map(); S.order = []; S.linked = new Set();
    const st = G ? G.start : GRID.start;
    let hasStart = false;
    for (const b of (v && v.board) || []) {
      const k = key(b.x, b.y);
      const kind = b.s === 'start' ? 'start' : 'path';
      if (kind === 'start') hasStart = true;
      S.cards.set(k, { x: b.x, y: b.y, s: b.s, m: b.m | 0, c: !!b.c, by: b.by ?? null, kind });
      S.order = S.order.filter((q) => q !== k); S.order.push(k);
    }
    if (!hasStart) S.cards.set(key(st[0], st[1]), { x: st[0], y: st[1], s: 'start', m: 15, c: true, by: null, kind: 'start' });
    for (const p of (v && v.linked) || []) S.linked.add(key(p[0], p[1]));
    if (!(v && v.linked)) S.linked.add(key(st[0], st[1]));
    const gl = (G ? G.goals : GRID.goals);
    S.goals = gl.map(([x, y], i) => {
      const g = (v && v.goals || []).find((q) => q.i === i) || {};
      return { i, x: g.x ?? x, y: g.y ?? y, open: !!g.open, kind: g.kind ?? null, s: g.s ?? null, m: g.m | 0, c: g.c ?? true };
    });
    S.peeks = {};
    const pk = v && v.me && v.me.peeks;
    if (pk) for (const k of Object.keys(pk)) if (pk[k] === 'gold' || pk[k] === 'stone') S.peeks[+k] = pk[k];
    S.players = ((v && v.players) || []).slice(0, 10).map((p) => ({ seat: p.seat, color: p.color ?? p.seat, broken: Array.isArray(p.broken) && p.broken.length > 0 }));
  }

  /** Анімаційні поправки поверх моделі: cellFx — клітинка (key) → { lump(j) → [s, ox, oy, oz] | null, sup, lamp, boulder };
   *  leaving — карти, що зникають (завал), ще малюються з cellFx; goalFx — мітка i → поправки дошки/скрині/каміння. */
  const cellFx = new Map(), leaving = new Map(), goalFx = new Map();
  let ghost = null;   // { ci, mask, c, x, y } — привид під курсором

  function computeHoles() {
    holes.fill(0);
    for (const k of G.shaft) { const [x, y] = k.split(',').map(Number); const ci = cellIndex(x, y); if (ci >= 0) holes[ci] = SHAFT_HOLES; }
    for (const c of S.cards.values()) { const ci = cellIndex(c.x, c.y); if (ci >= 0) holes[ci] |= holesOf(c.m, c.c); }
    for (const g of S.goals) if (g.open && g.m) { const ci = cellIndex(g.x, g.y); if (ci >= 0) holes[ci] |= holesOf(g.m, g.c); }
  }

  // Джерела тепла для кольору грудок: ліхтарі (з'єднані — яскраві) і відкрита скриня.
  let heat = [];
  function lightSources() {
    heat = [];
    const add = (c, isGoal) => {
      if (!c.c || !c.m) return;
      const lin = S.linked.has(key(c.x, c.y)) || isGoal;
      heat.push({ X: c.x, Y: -c.y + 0.05, w: lin ? 1 : 0.28, gold: false });
    };
    for (const c of S.cards.values()) add(c, false);
    for (const g of S.goals) if (g.open && g.m) {
      add(g, true);
      if (g.kind === 'gold') heat.push({ X: g.x, Y: -g.y, w: 1.3, gold: true });
    }
  }
  const LAMP = new THREE.Color(PAL.lamp), GOLD = new THREE.Color(PAL.gold);
  function writeWallColors() {
    const R = 1.75;
    for (let i = 0; i < nLumps; i++) {
      const X = bX[i], Y = bY[i];
      let w = 0, gw = 0;
      for (const s of heat) {
        const dx = X - s.X, dy = Y - s.Y;
        if (dx > R || dx < -R || dy > R || dy < -R) continue;
        const d = Math.sqrt(dx * dx + dy * dy);
        if (d >= R) continue;
        const f = (1 - d / R) * (1 - d / R);
        w += s.w * f; if (s.gold) gw += f;
      }
      w = Math.min(1.25, w);
      const k = 0.66 + 0.7 * w;
      _c.setRGB(bCol[i * 3] * k, bCol[i * 3 + 1] * k, bCol[i * 3 + 2] * k);
      _c.lerp(LAMP, Math.min(0.32, 0.24 * w));
      if (gw > 0) _c.lerp(GOLD, Math.min(0.4, 0.35 * gw));
      wall.setColorAt(i, _c);
    }
    wall.instanceColor.needsUpdate = true;
  }

  function writeLump(i, s = 1, ox = 0, oy = 0, oz = 0) {
    if (s <= 0.0005) { wall.setMatrixAt(i, ZERO); return; }
    _eu.set(bRx[i], bRy[i], bRz[i], 'XYZ');
    wall.setMatrixAt(i, _m4.compose(_pv.set(bX[i] + ox, bY[i] + oy, bZ[i] + oz), _q.setFromEuler(_eu), _sv.set(bS[i] * s, bS[i] * s, Math.max(s, 0.001))));
  }
  function writeCellLumps(ci, kk) {
    const h = holes[ci], fx = kk ? cellFx.get(kk) : null;
    const gm = ghost && ghost.ci === ci ? ghost.mask : 0;
    for (let j = 0; j < 9; j++) {
      const i = ci * 9 + j;
      const o = fx && fx.lump ? fx.lump(j) : null;
      if (o) writeLump(i, o[0], o[1], o[2], o[3]);
      else writeLump(i, (h >> j) & 1 || (gm >> j) & 1 ? 0 : 1);
    }
  }
  function writeWall() {
    for (let y = G.y0; y <= G.y1; y++) for (let x = G.x0; x <= G.x1; x++) writeCellLumps(cellIndex(x, y), key(x, y));
    wall.instanceMatrix.needsUpdate = true;
  }

  // Кріплення, ліхтарі, плями світла, валуни — щоразу наново (їх ≤ кількох сотень).
  const lampSlots = [];   // { gi (індекс плями), gb (база кольору), glass i, lit }
  function writeProps(t = clockT) {
    let ns = 0, nl = 0, ng = 0, nb = 0;
    lampSlots.length = 0;
    const one = (c, kk, isGoal) => {
      const fx = cellFx.get(kk);
      const ss = fx?.sup ?? 1, ls = fx?.lamp ?? 1, bs = fx?.boulder ?? 1;
      if (c.m) for (const d of DIRS) {
        if (!(c.m & BIT[d]) || ns >= supports.instanceMatrix.count) continue;
        const X = c.x + STEP[d][0] * L, Y = -c.y - STEP[d][1] * L;
        if (ss > 0.001) setM(supports, ns++, X, Y, 0, 0, 0, (d === 'N' || d === 'S') ? Math.PI / 2 : 0, ss);
      }
      if (!c.c && c.m && bs > 0.001) setM(boulders, nb++, c.x, -c.y + 0.01, -0.16, hash(c.x, c.y) * 3, hash(c.y, c.x) * 3, 0.3, bs);
      if (c.c && c.m && ls > 0.001) {
        const lin = isGoal || S.linked.has(kk);
        const X = c.x, Y = -c.y + L / 2;
        setM(lanternF, nl, X, Y, M.TUN_Z, 0, 0.4, 0, ls);
        setM(lanternG, nl, X, Y, M.TUN_Z, 0, 0.4, 0, ls);
        const gb = lin ? 1 : 0.16;
        lanternG.setColorAt(nl, _c.set(lin ? '#ffe7a6' : '#6a4a22').multiplyScalar(lin ? 1.35 * ls : ls));
        setM(glows, ng, X, -c.y + 0.04, M.BACK_Z + 0.01, 0, 0, 0, 0.62 * ls);
        lampSlots.push({ gi: ng, gb: 0.55 * gb, glass: nl, lin });
        glows.setColorAt(ng++, _c.copy(LAMP).multiplyScalar(0.55 * gb));
        setM(glows, ng, X, Y - 0.122, M.TUN_Z + 0.06, 0, 0, 0, 0.15 * ls);
        lampSlots.push({ gi: ng, gb: 0.9 * gb, glass: -1, lin });
        glows.setColorAt(ng++, _c.copy(LAMP).multiplyScalar(0.9 * gb));
        nl++;
      }
    };
    for (const [kk, c] of S.cards) one(c, kk, false);
    for (const [kk, c] of leaving) one(c, kk, false);
    for (const g of S.goals) if (g.open && g.m) one(g, key(g.x, g.y), true);
    // Сяйво відкритої скрині — на лиці розрізу, адитивно.
    for (const g of S.goals) {
      const fx = goalFx.get(g.i);
      const showGold = g.kind === 'gold' && (g.open || fx?.chest);
      const gs = fx?.glow ?? (g.open && g.kind === 'gold' ? 1 : 0);
      if (showGold && gs > 0.001) {
        setM(glows, ng, g.x, -g.y - 0.08, 0.05, 0, 0, 0, 0.62 * (0.75 + 0.25 * gs));
        glows.setColorAt(ng++, _c.copy(GOLD).multiplyScalar(0.26 * Math.min(gs, 1.6)));
      }
    }
    finish(supports, ns); finish(lanternF, nl); finish(lanternG, nl); finish(glows, ng); finish(boulders, nb);
    flicker(t, true);
  }
  function flicker(t, force = false) {
    if (!glows || (!bgAnim && !force)) return;
    for (const s of lampSlots) {
      const f = bgAnim ? 0.88 + 0.08 * Math.sin(t * 7.3 + s.gi * 1.7) + 0.05 * Math.sin(t * 13.1 + s.gi * 0.9) : 1;
      glows.setColorAt(s.gi, _c.copy(LAMP).multiplyScalar(s.gb * f));
    }
    if (glows.instanceColor) glows.instanceColor.needsUpdate = true;
  }

  // ------------------------------------------------------------------------------------------------ мітки
  const goalPos = (g) => ({ X: g.x, Y: -g.y });
  function pilePlaces(i) {
    const out = [];
    for (let k = 0; k < 5; k++) {
      const a = hash(i, k), b = hash(k, i + 9);
      out.push({ x: (k - 2) * 0.12 + (a - 0.5) * 0.05, y: k === 4 ? 0.1 : (k === 2 ? 0.04 : 0), z: 0.06 + b * 0.12, s: 0.07 + a * 0.045, r: a * 6 });
    }
    return out;
  }
  function writeGoals() {
    let nb = 0, nc = 0, ns = 0, nr = 0, np = 0;
    let chestShown = false;
    for (const g of S.goals) {
      const { X, Y } = goalPos(g), fx = goalFx.get(g.i) || {};
      const floor = Y - L / 2;
      // Дошка: закрита й невідома — на місці; під час анімації — з поправками.
      if (fx.board) {
        const b = fx.board;
        if (b.s > 0.001) setM(boards, nb++, X + b.ox, Y + b.oy, 0.0 + b.oz, b.rx || 0, b.ry || 0, b.rz || 0, b.s, b.s, b.s);
        for (const h of fx.halves || []) if (h.s > 0.001) setM(boards, nb++, X + h.ox, Y + h.oy, h.oz, 0, 0, h.rz, 0.5 * h.s, h.s, h.s);
      } else if (!g.open && !g.kind) {
        setM(boards, nb++, X + (hash(g.i, 3) - 0.5) * 0.02, Y, 0.0, 0, 0, (hash(g.i, 5) - 0.5) * 0.05, 1);
      }
      // Моя підглядина над дошкою.
      const pk = S.peeks[g.i];
      if (pk && !g.open && !g.kind && !fx.noPeek) {
        const m = pk === 'gold' ? peekCoin : peekStone, n = pk === 'gold' ? nc++ : ns++;
        setM(m, n, X + 0.3, Y + 0.33, 0.12, 0, pk === 'gold' ? 0.3 : 0, 0, 1);
      }
      // Вміст: відкрита мітка, або закрита, але вже відома (кінець раунду).
      const showContent = g.open || g.kind || fx.chest || fx.rocks;
      if (!showContent) continue;
      const kind = g.kind || (fx.chest ? 'gold' : 'stone');
      if (kind === 'gold') {
        const ch = fx.chest || { s: 1, lid: g.open ? 1 : 0 };
        if (ch.s > 0.001) {
          chestShown = true;
          chestBody.position.set(X, floor, CH_Z); chestBody.scale.setScalar(ch.s * CH_S); chestBody.rotation.set(0, -0.12, 0);
          const cs2 = ch.s * CH_S;
          chestLid.position.set(X + Math.sin(-0.12) * -0.14 * cs2, floor + M.CHEST.h * cs2, CH_Z - 0.14 * cs2 * Math.cos(0.12));
          chestLid.scale.setScalar(cs2); chestLid.rotation.set(-1.7 * (ch.lid ?? 0), -0.12, 0, 'YXZ');
        }
        const pc = fx.coins ?? (g.open ? 1 : 0);
        if (pc > 0.001) for (let k = 0; k < 12 && np < pileCoins.instanceMatrix.count; k++) {
          const a = hash(g.i + 20, k), b = hash(k, g.i + 40);
          const side = k % 2 ? 1 : -1;
          setM(pileCoins, np++, X + side * (0.34 + a * 0.13), floor + 0.008 + (k > 7 ? 0.014 : 0), 0.2 + b * 0.2, (a - 0.5) * 0.5, a * 6, (b - 0.5) * 0.5, pc);
        }
      } else {
        const rs = fx.rocks;
        pilePlaces(g.i).forEach((p, k) => {
          const o = rs ? rs(k) : null;
          if (o && o.s <= 0.001) return;
          const x = X + p.x + (o ? o.ox : 0), y = floor + p.s * 0.7 + p.y + (o ? o.oy : 0);
          setM(pileRocks, nr, x, y, p.z, p.r, p.r * 1.3, o ? o.rz : 0, p.s * (o ? o.s : 1));
          pileRocks.setColorAt(nr++, _c.set(PAL.stone).multiplyScalar(0.8 + hash(g.i, k + 3) * 0.35));
        });
      }
    }
    if (!chestShown) chestBody.visible = chestLid.visible = false; else chestBody.visible = chestLid.visible = true;
    finish(boards, nb); finish(peekCoin, nc + flyIcon.coin); finish(peekStone, ns + flyIcon.stone); finish(pileRocks, nr); finish(pileCoins, np);
    flyIcon.base = { coin: nc, stone: ns };
    // світло скрині
    const og = S.goals.find((g) => g.kind === 'gold' && (g.open || goalFx.get(g.i)?.chest));
    const fxg = og ? goalFx.get(og.i) : null;
    const li = og ? (fxg?.light ?? (og.open ? 1.4 : 0)) : 0;
    chestLight.intensity = li;
    if (og) chestLight.position.set(og.x, -og.y + 0.15, 0.6);
  }
  const flyIcon = { coin: 0, stone: 0, base: { coin: 0, stone: 0 } };

  // ------------------------------------------------------------------------------------------------ фігурки
  const figPos = new Map();    // seat → V3 (поточна, з урахуванням стрибка)
  const figHome = new Map();   // seat → V3 (куди стати)
  const figJump = new Map();   // seat → { from, t0, dur }
  function figureHomes() {
    figHome.clear();
    if (!G) return;
    const last = new Map();
    for (const k of S.order) { const c = S.cards.get(k); if (c && c.by != null && c.kind === 'path') last.set(c.by, k); }
    const st = key(G.start[0], G.start[1]);
    const groups = new Map();
    for (const p of S.players) {
      const k = last.get(p.seat) ?? st;
      if (!groups.has(k)) groups.set(k, []);
      groups.get(k).push(p.seat);
    }
    for (const [k, seats] of groups) {
      const [x, y] = k.split(',').map(Number);
      seats.sort((a, b) => a - b).forEach((s, i) => {
        const row = Math.floor(i / 4), inRow = Math.min(4, seats.length - row * 4), col = i % 4;
        figHome.set(s, new THREE.Vector3(x + (col - (inRow - 1) / 2) * 0.24 + row * 0.11, -y - L / 2 + row * 0.04, FIG_Z - row * 0.15));
      });
    }
  }
  function placeFigures(now = performance.now(), animate = false) {
    figureHomes();
    let n = 0, jumping = false;
    for (const p of S.players) {
      const home = figHome.get(p.seat); if (!home || n >= 10) continue;
      let cur = figPos.get(p.seat);
      if (!cur) { cur = home.clone(); figPos.set(p.seat, cur); }
      const j = figJump.get(p.seat);
      if (animate && !j && cur.distanceToSquared(home) > 1e-4 && !reduced) figJump.set(p.seat, { from: cur.clone(), t0: now, dur: 520 });
      const jj = figJump.get(p.seat);
      let hop = 0;
      if (jj) {
        const k = clamp((now - jj.t0) / jj.dur, 0, 1), e = easeInOut(k);
        cur.lerpVectors(jj.from, home, e);
        hop = Math.sin(Math.PI * k) * (0.45 + jj.from.distanceTo(home) * 0.12);
        cur.y += hop;
        if (k >= 1) { figJump.delete(p.seat); cur.copy(home); } else jumping = true;
      } else cur.copy(home);
      const ry = (hash(p.seat, 77) - 0.5) * 0.5;
      const sit = p.broken && !hop;
      const sy = sit ? 0.74 : 1, rx = sit ? -0.12 : 0;
      setM(figShirt, n, cur.x, cur.y, cur.z, rx, ry, 0, FIG_S, FIG_S * sy, FIG_S);
      setM(figBody, n, cur.x, cur.y, cur.z, rx, ry, 0, FIG_S, FIG_S * sy, FIG_S);
      figShirt.setColorAt(n, _c.set(SEAT_COLOR[p.color] ?? SEAT_COLOR[p.seat % 10] ?? '#9aa3ad'));
      // кирка: на плечі або лежить поруч
      _eu.set(rx, ry, 0, 'YXZ'); _q.setFromEuler(_eu);
      if (sit) {
        const off = new THREE.Vector3(0.13, 0.02, 0.07).multiplyScalar(FIG_S).applyQuaternion(_q);
        setM(figPick, n, cur.x + off.x, cur.y + 0.012, cur.z + off.z, 0.1, ry + 0.3, Math.PI / 2, FIG_S);
      } else {
        const off = new THREE.Vector3(0.075, 0.25, -0.04).multiplyScalar(FIG_S).applyQuaternion(_q);
        setM(figPick, n, cur.x + off.x, cur.y + off.y, cur.z + off.z, 0, ry, 0.62, FIG_S);
      }
      n++;
    }
    finish(figShirt, n); finish(figBody, n); finish(figPick, n);
    return jumping;
  }

  // ------------------------------------------------------------------------------------------------ усе разом
  function applyAll() {
    if (!G) return;
    computeHoles();
    lightSources();
    writeWall();
    writeWallColors();
    writeProps();
    writeGoals();
    placeFigures();
    writeGhost();
    rebuildLegal();
    dirty = true;
  }

  function setState(v) {
    if (!v) return;
    if (v.grid) setGrid(v.grid);
    if (!G) setGrid(GRID);
    cancelFx();
    view = v;
    readView(v);
    applyAll();
  }

  // ------------------------------------------------------------------------------------------------ підсвітка
  let legal = { kind: null, cells: [], alt: [], goals: [], ghost: null };
  let hlItems = [];     // { kind: 'cell'|'goal', x, y, i, alt }
  let hover = null, cursorAt = null;
  function setLegal(l) {
    const kind = l && (l.kind === 'place' || l.kind === 'rockfall' || l.kind === 'goal') ? l.kind : null;
    legal = { kind, cells: kind ? (l.cells || []).map((c) => [c[0], c[1]]) : [], alt: kind ? (l.alt || []).map((c) => [c[0], c[1]]) : [],
      goals: kind ? (l.goals || []).slice() : [], ghost: kind === 'place' && l.ghost ? { m: l.ghost.m | 0, mAlt: (l.ghost.mAlt ?? l.ghost.m) | 0, c: l.ghost.c !== false } : null };
    hlItems = [];
    if (kind === 'goal') {
      for (const i of legal.goals) { const g = S.goals[i] || (G && G.goals[i] && { x: G.goals[i][0], y: G.goals[i][1] }); if (g) hlItems.push({ kind: 'goal', x: g.x, y: g.y, i, alt: false }); }
    } else if (kind) {
      const seen = new Set();
      for (const [x, y] of legal.cells) { const k = key(x, y); if (seen.has(k)) continue; seen.add(k); hlItems.push({ kind: 'cell', x, y, alt: false }); }
      for (const [x, y] of legal.alt) { const k = key(x, y); if (seen.has(k)) continue; seen.add(k); hlItems.push({ kind: 'cell', x, y, alt: true }); }
    }
    hover = null;
    canvas.style.cursor = '';
    writeGhost();
    rebuildLegal();
    dirty = true;
  }
  const C_LEGAL = new THREE.Color(PAL.legal), C_ALT = new THREE.Color(PAL.alt).multiplyScalar(0.72), C_BAD = new THREE.Color(PAL.bad).lerp(new THREE.Color('#ffb0a0'), 0.25);
  function rebuildLegal(t = clockT) {
    if (!hlFrames) return;
    let n = 0, na = 0;
    const pulse = reduced ? 0 : Math.sin(t * 4.2);
    for (let i = 0; i < hlItems.length && n < hlFrames.instanceMatrix.count; i++) {
      const it = hlItems[i];
      const hov = hover && hover.kind === it.kind && hover.x === it.x && hover.y === it.y;
      const s = (hov ? 1.07 : 1) * (1 + 0.025 * (reduced ? 0 : Math.sin(t * 4.2 + i * 0.5)));
      setM(hlFrames, n, it.x, -it.y, hov ? 0.13 : 0.04, 0, 0, 0, s, s, 1);
      const c = legal.kind === 'rockfall' ? C_BAD : it.alt ? C_ALT : C_LEGAL;
      hlFrames.setColorAt(n++, _c.copy(c).multiplyScalar(hov ? 1.25 : 1));
      if (it.alt) {
        setM(altIcons, na, it.x, -it.y, hov ? 0.16 : 0.07, 0, 0, reduced ? 0 : -t * 0.8, 1);
        altIcons.setColorAt(na++, _c.copy(C_ALT).multiplyScalar(1.3));
      }
    }
    finish(hlFrames, n); finish(altIcons, na);
    matHL.opacity = reduced ? 0.95 : 0.8 + 0.18 * pulse;
  }

  /** Привид вибраної карти: на клітинці під мишею (або під курсором клавіатури) грудки, що зникнуть, — напівпрозорі. */
  function writeGhost() {
    const prev = ghost;
    ghost = null;
    const at = hover && hover.kind === 'cell' ? hover : (cursorAt && hlItems.find((h) => h.kind === 'cell' && h.x === cursorAt[0] && h.y === cursorAt[1])) || null;
    if (G && legal.kind === 'place' && legal.ghost && at) {
      const m = at.alt ? legal.ghost.mAlt : legal.ghost.m;
      const ci = cellIndex(at.x, at.y);
      if (ci >= 0 && m) ghost = { ci, mask: holesOf(m, legal.ghost.c) & ~holes[ci], c: legal.ghost.c, x: at.x, y: at.y };
    }
    let n = 0;
    if (ghost) {
      for (let j = 0; j < 9; j++) if ((ghost.mask >> j) & 1) {
        const i = ghost.ci * 9 + j;
        _eu.set(bRx[i], bRy[i], bRz[i], 'XYZ');
        ghostLumps.setMatrixAt(n++, _m4.compose(_pv.set(bX[i], bY[i], bZ[i] + 0.004), _q.setFromEuler(_eu), _sv.set(bS[i], bS[i], 1)));
      }
      ghostBoulder.visible = !ghost.c;
      if (!ghost.c) ghostBoulder.position.set(ghost.x, -ghost.y + 0.01, -0.12);
    } else ghostBoulder.visible = false;
    finish(ghostLumps, n);
    if (wall) {
      if (prev && (!ghost || prev.ci !== ghost.ci)) writeCellLumps(prev.ci, null);
      if (ghost) writeCellLumps(ghost.ci, null);
      if (prev || ghost) wall.instanceMatrix.needsUpdate = true;
    }
    dirty = true;
  }

  function setCursor(x, y) {
    if (x == null || y == null || !G) { cursorAt = null; cursor.visible = false; }
    else { cursorAt = [x | 0, y | 0]; cursor.position.set(cursorAt[0], -cursorAt[1], 0.09); cursor.visible = true; }
    writeGhost();
    dirty = true;
  }

  function setHover(h) {
    const same = (h && hover && h.kind === hover.kind && h.x === hover.x && h.y === hover.y) || (!h && !hover);
    hover = h;
    canvas.style.cursor = h ? 'pointer' : '';
    if (same) return;
    writeGhost();
    rebuildLegal();
    if (!h) onHover(null);
    else if (h.kind === 'goal') onHover('goal', h.i);
    else onHover('cell', h.x, h.y);
  }

  // ------------------------------------------------------------------------------------------------ камера
  const cs = { az: 0, el: DEF_EL, zoom: 1, target: new THREE.Vector3(4, 0, 0) };
  const vel = { az: 0, el: 0, x: 0, y: 0 };
  let baseDist = 30, W = 1, H = 1, defTarget = new THREE.Vector3(4, 0, 0), touchedAt = -1e9, touched = false;
  let shake = null;   // { t0, dur, amp }
  function applyCam(now = performance.now()) {
    const d = baseDist / cs.zoom, ce = Math.cos(cs.el);
    cam.position.set(cs.target.x + d * ce * Math.sin(cs.az), cs.target.y + d * Math.sin(cs.el), cs.target.z + d * ce * Math.cos(cs.az));
    cam.lookAt(cs.target);
    if (shake) {
      const k = (now - shake.t0) / shake.dur;
      if (k >= 1) shake = null;
      else {
        const a = shake.amp * (1 - k);
        cam.position.x += Math.sin(now * 0.09) * a; cam.position.y += Math.cos(now * 0.113) * a * 0.8;
      }
    }
    cam.updateMatrixWorld();
  }
  const _pj = new THREE.Vector3();
  function fitBox() {
    const portrait = W / H < 0.9;
    const surf = G ? G.surfY : 4.5, bot = G ? G.botY : -4.5;
    return portrait ? { x0: -1, x1: 9, y0: bot - 0.15, y1: surf + 1.0 } : { x0: -1.5, x1: 9.5, y0: bot - 0.15, y1: surf + 1.95 };
  }
  function fit() {
    const save = { az: cs.az, el: cs.el, zoom: cs.zoom, t: cs.target.clone() };
    const b = fitBox();
    const pts = [];
    for (const x of [b.x0, b.x1]) for (const y of [b.y0, b.y1]) pts.push(new THREE.Vector3(x, y, 0));
    pts.push(new THREE.Vector3((b.x0 + b.x1) / 2, b.y1 - 0.3, -1.8));
    cs.az = 0; cs.el = DEF_EL; cs.zoom = 1;
    let ty = (b.y0 + b.y1) / 2;
    const tx = (b.x0 + b.x1) / 2;
    const lim = 0.97;
    for (let pass = 0; pass < 3; pass++) {
      cs.target.set(tx, ty, 0);
      let lo = 2, hi2 = 200;
      for (let it = 0; it < 32; it++) {
        baseDist = (lo + hi2) / 2; applyCam();
        let ok = true;
        for (const p of pts) { _pj.copy(p).project(cam); if (Math.abs(_pj.x) > lim || Math.abs(_pj.y) > lim) { ok = false; break; } }
        if (ok) hi2 = baseDist; else lo = baseDist;
      }
      baseDist = hi2; applyCam();
      let y0 = 1, y1 = -1;
      for (const p of pts) { _pj.copy(p).project(cam); y0 = Math.min(y0, _pj.y); y1 = Math.max(y1, _pj.y); }
      ty += (y0 + y1) / 2 * baseDist * Math.tan(FOV * D2R / 2);
    }
    defTarget.set(tx, ty, 0);
    cs.az = save.az; cs.el = save.el; cs.zoom = save.zoom; cs.target.copy(save.t);
    if (!touched) { cs.target.copy(defTarget); cs.az = 0; cs.el = DEF_EL; cs.zoom = 1; }
    clampTarget();
    applyCam();
    dirty = true;
  }
  function clampTarget() {
    const x0 = G ? G.x0 - 0.5 : -4.5, x1 = G ? G.x1 + 0.5 : 12.5, y0 = G ? G.botY : -4.5, y1 = G ? G.surfY + 2 : 6.5;
    cs.target.x = clamp(cs.target.x, x0, x1); cs.target.y = clamp(cs.target.y, y0, y1); cs.target.z = 0;
  }
  let camTw = null;
  function camTo(to, dur) {
    if (camTw) camTw.cancel();
    const from = { az: cs.az, el: cs.el, zoom: cs.zoom, t: cs.target.clone() };
    const step = (k) => {
      const e = easeInOut(k);
      cs.az = from.az + ((to.az ?? from.az) - from.az) * e; cs.el = from.el + ((to.el ?? from.el) - from.el) * e;
      cs.zoom = from.zoom + ((to.zoom ?? from.zoom) - from.zoom) * e;
      if (to.t) cs.target.lerpVectors(from.t, to.t, e);
      applyCam(); dirty = true;
    };
    if (reduced || dur <= 0) { step(1); return; }
    camTw = tween(dur, step, { camera: true });
  }
  function resetView() {
    touched = false; touchedAt = -1e9;
    vel.az = vel.el = vel.x = vel.y = 0;
    camTo({ az: 0, el: DEF_EL, zoom: 1, t: defTarget.clone() }, 450);
  }
  /** Плавно підвести камеру до клітинки (якщо людина не рухала камеру < 4 с тому). zoomTo — наблизити (для скрині). */
  function focus(x, y, zoomTo = null) {
    if (!G || x == null || y == null) return;
    if (performance.now() - touchedAt < 4000) return;
    const P = new THREE.Vector3(x, -y, 0);
    const zoom = zoomTo ? Math.max(cs.zoom, zoomTo) : cs.zoom;
    const d = baseDist / zoom, hh = d * Math.tan(FOV * D2R / 2), hw = hh * (W / H);
    const t = cs.target.clone();
    if (zoomTo) t.lerp(P, 0.85);
    else {
      const mx = hw * 0.55, my = hh * 0.5;
      if (P.x > t.x + mx) t.x = P.x - mx; else if (P.x < t.x - mx) t.x = P.x + mx;
      if (P.y > t.y + my) t.y = P.y - my; else if (P.y < t.y - my) t.y = P.y + my;
    }
    const save = cs.target.clone(); cs.target.copy(t); clampTarget(); t.copy(cs.target); cs.target.copy(save);
    if (t.distanceTo(cs.target) < 0.02 && Math.abs(zoom - cs.zoom) < 0.01) return;
    camTo({ t, zoom }, 650);
  }

  function resize() {
    const w = Math.max(1, host.clientWidth | 0), h = Math.max(1, host.clientHeight | 0);
    if (w === W && h === H) return;
    W = w; H = h;
    renderer.setSize(w, h, false);
    cam.aspect = w / h; cam.updateProjectionMatrix();
    fit();
  }

  // ------------------------------------------------------------------------------------------------ влучання
  function project(v, out = {}) {
    _pj.copy(v).project(cam);
    out.x = (_pj.x + 1) / 2 * W; out.y = (1 - _pj.y) / 2 * H; out.z = _pj.z;
    return out;
  }
  const _ray = new THREE.Raycaster(), _plane = new THREE.Plane(new THREE.Vector3(0, 0, 1), 0), _hit = new THREE.Vector3(), _ndc = new THREE.Vector2();
  function planeAt(px, py) {
    _ndc.set(px / W * 2 - 1, -(py / H) * 2 + 1);
    _ray.setFromCamera(_ndc, cam);
    return _ray.ray.intersectPlane(_plane, _hit);
  }
  function pxPerUnit() {
    const a = project(new THREE.Vector3(cs.target.x, cs.target.y, 0)), b = project(new THREE.Vector3(cs.target.x + 1, cs.target.y, 0));
    return Math.hypot(a.x - b.x, a.y - b.y) || 40;
  }
  function hitAt(px, py, touch) {
    if (!legal.kind || !hlItems.length) return null;
    const P = planeAt(px, py); if (!P) return null;
    const fx = P.x, fy = -P.y;
    const thr = touch ? Math.max(0.7, 26 / pxPerUnit()) : 0.7;
    let best = null, bd = thr;
    for (const it of hlItems) {
      const d = Math.hypot(it.x - fx, it.y - fy);
      if (d < bd) { bd = d; best = it; }
    }
    return best;
  }

  function screenPos(kind, a, b) {
    let P = null;
    if (kind === 'cell' && a != null && b != null) P = new THREE.Vector3(a, -b, 0.05);
    else if (kind === 'goal') { const g = S.goals[a] || (G && G.goals[a] && { x: G.goals[a][0], y: G.goals[a][1] }); if (g) P = new THREE.Vector3(g.x, -g.y, 0.05); }
    else if (kind === 'seat') { const f = figPos.get(a); if (f) P = f.clone().add(new THREE.Vector3(0, M.FIG_H * FIG_S, 0)); }
    if (!P) return null;
    const s = project(P);
    if (s.z > 1 || s.z < -1) return null;
    return { x: Math.round(s.x * 10) / 10, y: Math.round(s.y * 10) / 10 };
  }

  // ------------------------------------------------------------------------------------------------ вказівник
  const ptrs = new Map();
  let drag = null, pinch = null, lastTap = 0, lastPointerType = 'mouse';
  const local = (e) => { const r = canvas.getBoundingClientRect(); return [e.clientX - r.left, e.clientY - r.top]; };
  function touchCam() { touched = true; touchedAt = performance.now(); if (camTw) { camTw.cancel(); camTw = null; } }
  function onDown(e) {
    kick();
    lastPointerType = e.pointerType || 'mouse';
    try { canvas.setPointerCapture(e.pointerId); } catch { /* */ }
    const [x, y] = local(e);
    ptrs.set(e.pointerId, { x, y });
    vel.az = vel.el = vel.x = vel.y = 0;
    if (ptrs.size === 1) drag = { x0: x, y0: y, x, y, moved: false, rot: e.button === 2 || e.shiftKey, lt: performance.now() };
    else if (ptrs.size === 2) {
      const [a, b] = [...ptrs.values()];
      pinch = { d0: Math.hypot(a.x - b.x, a.y - b.y) || 1, z0: cs.zoom, mx: (a.x + b.x) / 2, my: (a.y + b.y) / 2 };
      if (drag) drag.moved = true;
    }
  }
  function onMove(e) {
    kick();
    lastPointerType = e.pointerType || 'mouse';
    const [x, y] = local(e);
    if (!ptrs.has(e.pointerId)) {
      if (lastPointerType === 'mouse') setHover(hitAt(x, y, false));
      return;
    }
    const p = ptrs.get(e.pointerId); p.x = x; p.y = y;
    if (ptrs.size >= 2 && pinch) {
      const [a, b] = [...ptrs.values()];
      const d = Math.hypot(a.x - b.x, a.y - b.y) || 1, mx = (a.x + b.x) / 2, my = (a.y + b.y) / 2;
      cs.zoom = clamp(pinch.z0 * d / pinch.d0, ZOOM_MIN, ZOOM_MAX);
      rotBy(mx - pinch.mx, my - pinch.my);
      pinch.mx = mx; pinch.my = my;
      touchCam(); applyCam(); dirty = true;
      return;
    }
    if (!drag) return;
    const dx = x - drag.x, dy = y - drag.y;
    if (!drag.moved && Math.hypot(x - drag.x0, y - drag.y0) > TAP_PX) { drag.moved = true; setHover(null); }
    if (drag.moved) {
      const now = performance.now(), dt = Math.max(1, now - drag.lt);
      if (drag.rot) { rotBy(dx, dy); vel.az = 0.6 * vel.az + 0.4 * (-dx * 0.005) / dt; vel.el = 0.6 * vel.el + 0.4 * (dy * 0.004) / dt; }
      else {
        const k = panBy(dx, dy);
        vel.x = 0.6 * vel.x + 0.4 * (-dx * k) / dt; vel.y = 0.6 * vel.y + 0.4 * (dy * k) / dt;
      }
      drag.lt = now;
      touchCam(); applyCam(); dirty = true;
    }
    drag.x = x; drag.y = y;
  }
  function rotBy(dx, dy) {
    cs.az = clamp(cs.az - dx * 0.005, -AZ_LIM, AZ_LIM);
    cs.el = clamp(cs.el + dy * 0.004, EL_MIN, EL_MAX);
  }
  function panBy(dx, dy) {
    const d = baseDist / cs.zoom, k = 2 * d * Math.tan(FOV * D2R / 2) / H;
    cs.target.x -= dx * k * Math.cos(cs.az); cs.target.y += dy * k;
    clampTarget();
    return k;
  }
  function onUp(e) {
    kick();
    const p = ptrs.get(e.pointerId);
    ptrs.delete(e.pointerId);
    try { canvas.releasePointerCapture(e.pointerId); } catch { /* */ }
    if (ptrs.size < 2) pinch = null;
    if (ptrs.size === 1) { const [q] = [...ptrs.values()]; drag = { x0: q.x, y0: q.y, x: q.x, y: q.y, moved: true, rot: false, lt: performance.now() }; return; }
    if (!drag || !p || e.type === 'pointercancel') { drag = null; return; }
    const d = drag; drag = null;
    if (d.moved) { if (performance.now() - d.lt > 80) vel.az = vel.el = vel.x = vel.y = 0; return; }
    const touch = (e.pointerType || 'mouse') !== 'mouse';
    const hit = hitAt(p.x, p.y, touch);
    if (hit) {
      lastTap = 0;
      if (hit.kind === 'goal') onPick('goal', hit.i); else onPick('cell', hit.x, hit.y);
      return;
    }
    if (touch) {
      const now = performance.now();
      if (now - lastTap < 320) { lastTap = 0; resetView(); } else lastTap = now;
    }
  }
  function onWheel(e) {
    e.preventDefault(); kick();
    const [x, y] = local(e);
    const before = planeAt(x, y)?.clone();
    cs.zoom = clamp(cs.zoom * Math.exp(-e.deltaY * (e.deltaMode === 1 ? 0.05 : 0.0015)), ZOOM_MIN, ZOOM_MAX);
    applyCam();
    const after = planeAt(x, y);
    if (before && after) { cs.target.x += before.x - after.x; cs.target.y += before.y - after.y; clampTarget(); }
    touchCam(); applyCam(); dirty = true;
  }
  function onDbl(e) { e.preventDefault(); if (!hitAt(...local(e), false)) resetView(); }
  function onLeave() { if (!ptrs.size) { setHover(null); kick(); } }
  const noCtx = (e) => e.preventDefault();
  canvas.addEventListener('pointerdown', onDown);
  canvas.addEventListener('pointermove', onMove);
  canvas.addEventListener('pointerup', onUp);
  canvas.addEventListener('pointercancel', onUp);
  canvas.addEventListener('pointerleave', onLeave);
  canvas.addEventListener('wheel', onWheel, { passive: false });
  canvas.addEventListener('dblclick', onDbl);
  canvas.addEventListener('contextmenu', noCtx);

  // ------------------------------------------------------------------------------------------------ анімації
  const tweens = new Set();
  function tween(dur, fn, o = {}) {
    let resolve;
    const p = new Promise((r) => { resolve = r; });
    const tw = { t0: performance.now(), dur: Math.max(1, dur), fn, done: false, fx: !o.camera, promise: p,
      cancel() { if (tw.done) return; tw.done = true; tweens.delete(tw); try { fn(1); } finally { resolve(); } } };
    tw.resolve = resolve;
    tweens.add(tw);
    kick();
    return tw;
  }
  function runTweens(now) {
    for (const tw of [...tweens]) {
      if (tw.done) continue;
      const k = clamp((now - tw.t0) / tw.dur, 0, 1);
      tw.fn(k);
      if (k >= 1) { tw.done = true; tweens.delete(tw); tw.resolve(); }
    }
    return tweens.size > 0;
  }
  /** Скасувати анімації подій (не камеру): кожна сама лишає кінцевий стан. */
  function cancelFx() {
    for (const tw of [...tweens]) if (tw.fx) tw.cancel();
    cellFx.clear(); leaving.clear(); goalFx.clear();
    parts.length = 0; finish(dust, 0); finish(flyCoins, 0); finish(fallRocks, 0); finish(moles, 0);
    flyIcon.coin = flyIcon.stone = 0;
    mapSheet.visible = mapRollA.visible = mapRollB.visible = beam.visible = false;
    flash.intensity = 0;
    figJump.clear();
  }
  const done = () => Promise.resolve();

  // Частинки: пил і монети.
  const parts = [];   // { kind: 'dust'|'coin', p, v, t0, life, s, spin, rot, g }
  function spawnDust(X, Y, Z, n, spread = 0.18, up = 0.6) {
    if (!dustOn) return;
    for (let i = 0; i < n && parts.filter((p) => p.kind === 'dust').length < dust.instanceMatrix.count; i++) {
      const a = Math.random() * Math.PI * 2;
      parts.push({ kind: 'dust', p: new THREE.Vector3(X + (Math.random() - 0.5) * spread, Y + (Math.random() - 0.5) * spread, Z + Math.random() * 0.1),
        v: new THREE.Vector3(Math.cos(a) * 0.5, up * (0.4 + Math.random() * 0.8), 0.35 + Math.random() * 0.6), t0: performance.now(), life: 550 + Math.random() * 450,
        s: 0.03 + Math.random() * 0.035, g: -0.9, rot: Math.random() * 6, spin: (Math.random() - 0.5) * 6 });
    }
  }
  function spawnCoin(p, v, life = 1200, g = -5.5) {
    if (parts.filter((q) => q.kind === 'coin').length >= flyCoins.instanceMatrix.count) return;
    parts.push({ kind: 'coin', p, v, t0: performance.now(), life, s: 1, g, rot: Math.random() * 6, spin: 6 + Math.random() * 8 });
  }
  function stepParts(dt, now) {
    if (!parts.length) return false;
    let nd = 0, nc = 0;
    for (let i = parts.length - 1; i >= 0; i--) {
      const q = parts[i], k = (now - q.t0) / q.life;
      if (k >= 1) { parts.splice(i, 1); continue; }
      q.v.y += q.g * dt;
      if (q.kind === 'dust') q.v.multiplyScalar(Math.exp(-dt * 2.2));
      q.p.addScaledVector(q.v, dt);
      q.rot += q.spin * dt;
    }
    for (const q of parts) {
      const k = (now - q.t0) / q.life;
      if (q.kind === 'dust') {
        const s = q.s * (0.6 + k * 1.4) * (k > 0.6 ? (1 - k) / 0.4 : 1);
        setM(dust, nd++, q.p.x, q.p.y, q.p.z, q.rot, q.rot * 0.7, 0, Math.max(0.001, s));
      } else {
        const s = k > 0.8 ? (1 - k) / 0.2 : Math.min(1, k * 8);
        setM(flyCoins, nc++, q.p.x, q.p.y, q.p.z, q.rot, q.rot * 0.6, 0.5, Math.max(0.001, s * 1.7));
      }
    }
    finish(dust, nd); finish(flyCoins, nc);
    return parts.length > 0;
  }

  // dig: грудки дрібно тремтять і розсипаються пилом, кріплення виростають, ліхтар спалахує; фігурка стрибає дугою.
  function dig(ev) {
    if (!G || !ev || ev.x == null) return done();
    cancelFx();
    const kk = key(ev.x, ev.y), ci = cellIndex(ev.x, ev.y);
    if (ci < 0) return done();
    const before = holes[ci];
    const m = ev.m | 0, c = ev.c !== false;
    S.cards.set(kk, { x: ev.x, y: ev.y, s: ev.s, m, c, by: ev.seat ?? null, kind: 'path' });
    S.order = S.order.filter((q) => q !== kk); S.order.push(kk);
    S.linked.add(kk);
    if (reduced) { applyAll(); return done(); }
    const removing = holesOf(m, c) & ~before;
    const D = 800;
    let dusted = false;
    const fx = {
      sup: 0, lamp: 0, boulder: 0,
      lump(j) {
        if (!((removing >> j) & 1)) return null;
        const t = fx.t;
        if (t < 260) { const a = 0.012 * Math.min(1, t / 80); return [1, Math.sin(t * 0.21 + j * 2) * a, Math.cos(t * 0.27 + j) * a, 0.006 * Math.sin(t * 0.33)]; }
        if (t < 500) { const e = easeIn((t - 260) / 240); return [1 - e, 0, -e * 0.04, e * 0.05]; }
        return [0, 0, 0, 0];
      },
      t: 0,
    };
    cellFx.set(kk, fx);
    computeHoles(); lightSources(); writeWallColors(); writeProps(); writeGoals();
    const now0 = performance.now();
    placeFigures(now0, false);
    let jumped = false;
    const tw = tween(D, (k) => {
      const t = k * D; fx.t = t;
      if (t >= 260 && !dusted) {
        dusted = true;
        for (let j = 0; j < 9; j++) if ((removing >> j) & 1) spawnDust(ev.x + ((j % 3) - 1) * L, -ev.y - (Math.floor(j / 3) - 1) * L, 0.05, j === 4 ? 6 : 4);
      }
      if (t >= 140 && !jumped) { jumped = true; placeFigures(performance.now(), true); }
      fx.sup = backOut(k01(t, 380, 660));
      fx.boulder = backOut(k01(t, 420, 680));
      fx.lamp = t >= 470 ? Math.min(1, backOut(k01(t, 470, 620))) : 0;
      flash.position.set(ev.x, -ev.y + 0.1, 0.35);
      flash.intensity = c && m ? 2.6 * Math.max(0, 1 - Math.abs(t - 540) / 260) * (t > 470 ? 1 : 0) : 0;
      writeCellLumps(ci, kk); wall.instanceMatrix.needsUpdate = true;
      writeProps();
      if (k >= 1) { cellFx.delete(kk); flash.intensity = 0; writeCellLumps(ci, kk); writeProps(); wall.instanceMatrix.needsUpdate = true; }
      dirty = true;
    });
    return tw.promise;
  }

  // flip: мітка відкривається. gold — дошка відлітає, кришка відкривається, монети пирскають, сяйво, камера підлітає;
  // stone — дошка тріскає, каміння осипається.
  function flip(ev) {
    if (!G || !ev || ev.i == null) return done();
    cancelFx();
    const g = S.goals[ev.i];
    if (!g) return done();
    const ci = cellIndex(g.x, g.y);
    const before = ci >= 0 ? holes[ci] : 0;
    const wasRevealed = !!g.kind && !g.open;
    Object.assign(g, { open: true, kind: ev.kind || g.kind || 'stone', s: ev.s ?? g.s, m: (ev.m ?? g.m) | 0, c: ev.c !== false });
    if (reduced || ci < 0) { applyAll(); return done(); }
    const gold = g.kind === 'gold';
    const D = gold ? 1600 : 1000;
    const kk = key(g.x, g.y);
    const removing = holesOf(g.m, g.c) & ~before;
    let dusted = false, sprayed = false;
    const t0lump = gold ? 320 : 260;
    const fx = { t: 0, sup: 0, lamp: 0, boulder: 1,
      lump(j) {
        if (!((removing >> j) & 1)) return null;
        const t = fx.t;
        if (t < t0lump) return [1, 0, 0, 0];
        const e = easeIn(k01(t, t0lump, t0lump + 260));
        return [1 - e, 0, -e * 0.04, e * 0.05];
      } };
    cellFx.set(kk, fx);
    const gf = { board: wasRevealed ? null : { ox: 0, oy: 0, oz: 0, rz: 0, s: 1 }, halves: null, chest: gold ? { s: 0, lid: 0 } : null,
      coins: 0, glow: 0, light: 0, rocks: gold ? null : () => ({ s: 0, ox: 0, oy: 0, rz: 0 }) };
    goalFx.set(g.i, gf);
    computeHoles(); lightSources(); writeWallColors();
    if (gold) focus(g.x, g.y, 1.55);
    const rockStart = pilePlaces(g.i).map((p, k) => ({ d: 380 + k * 70, h: 0.7 + hash(g.i, k) * 0.3 }));
    const tw = tween(D, (k) => {
      const t = k * D; fx.t = t;
      // дошка
      if (gf.board) {
        const shakeA = t < 300 ? 0.02 * Math.sin(t * 0.25) * (t / 300) : 0;
        if (gold) {
          const e = easeOut(k01(t, 300, 800));
          Object.assign(gf.board, { ox: 0.9 * e + shakeA, oy: 1.1 * e + Math.sin(Math.PI * e) * 0.4, oz: 1.6 * e, rz: shakeA * 4 - 4.2 * e, rx: -0.8 * e, s: 1 - e * 0.85 });
          if (t > 800) gf.board.s = 0;
        } else {
          Object.assign(gf.board, { ox: shakeA, oy: 0, oz: 0, rz: shakeA * 3, s: t < 250 ? 1 : 0 });
          if (t >= 250) {
            const e = easeIn(k01(t, 250, 700));
            gf.halves = [-1, 1].map((sd) => ({ ox: sd * (0.22 + 0.25 * e), oy: -0.7 * e, oz: 0.05 + 0.3 * e, rz: sd * 0.9 * e, s: 1 - k01(t, 520, 700) }));
          }
        }
      }
      if (t >= t0lump && !dusted) {
        dusted = true;
        for (let j = 0; j < 9; j++) if ((removing >> j) & 1) spawnDust(g.x + ((j % 3) - 1) * L, -g.y - (Math.floor(j / 3) - 1) * L, 0.05, 3);
      }
      fx.sup = backOut(k01(t, gold ? 700 : 600, gold ? 1000 : 880));
      fx.lamp = backOut(k01(t, gold ? 760 : 640, gold ? 1000 : 900));
      if (gold) {
        gf.chest.s = backOut(k01(t, 480, 880));
        gf.chest.lid = easeOut(k01(t, 850, 1250));
        gf.glow = k01(t, 820, 1300);
        gf.light = 1.4 + 2.2 * Math.max(0, 1 - Math.abs(t - 1150) / 450) * k01(t, 820, 1000);
        gf.light *= k01(t, 800, 1000);
        gf.coins = backOut(k01(t, 1200, 1500));
        if (t >= 950 && !sprayed) {
          sprayed = true;
          for (let i = 0; i < 22; i++) {
            const a = (Math.random() - 0.5) * 2.2;
            spawnCoin(new THREE.Vector3(g.x + (Math.random() - 0.5) * 0.2, -g.y - L / 2 + 0.3, 0.35),
              new THREE.Vector3(Math.sin(a) * (0.8 + Math.random()), 2.2 + Math.random() * 1.4, 0.6 + Math.random() * 0.8), 650 + Math.random() * 300, -7);
          }
        }
      } else {
        gf.rocks = (kq) => {
          const r = rockStart[kq], e = k01(t, r.d - 300, r.d);
          if (t < r.d - 300) return { s: 0, ox: 0, oy: 0, rz: 0 };
          const fall = easeIn(e), bounce = t > r.d ? Math.sin(k01(t, r.d, r.d + 160) * Math.PI) * 0.05 : 0;
          return { s: 1, ox: 0, oy: r.h * (1 - fall) + bounce, rz: (1 - e) * 3 };
        };
      }
      writeCellLumps(ci, kk); wall.instanceMatrix.needsUpdate = true;
      writeProps(); writeGoals();
      if (k >= 1) { cellFx.delete(kk); goalFx.delete(g.i); writeCellLumps(ci, kk); writeProps(); writeGoals(); wall.instanceMatrix.needsUpdate = true; }
      dirty = true;
    });
    return tw.promise;
  }

  // rockfall: 6–10 каменів падають згори в клітинку, грудки повертаються, пил, камера тремтить 0,2 с.
  function rockfall(ev) {
    if (!G || !ev || ev.x == null) return done();
    cancelFx();
    const kk = key(ev.x, ev.y), ci = cellIndex(ev.x, ev.y);
    const card = S.cards.get(kk);
    if (ci < 0 || !card || card.kind === 'start') return done();
    S.cards.delete(kk); S.order = S.order.filter((q) => q !== kk); S.linked.delete(kk);
    if (reduced) { applyAll(); return done(); }
    const before = holes[ci];
    computeHoles(); lightSources();
    const back = before & ~holes[ci];
    leaving.set(kk, card);
    const D = 1000, n = 8;
    const rocks = [];
    for (let i = 0; i < n; i++) rocks.push({ x: ev.x + (hash(i, 3) - 0.5) * 0.6, y: -ev.y + (hash(i, 5) - 0.5) * 0.45, z: 0.05 + hash(i, 7) * 0.15,
      t0: i * 32, dur: 300 + hash(i, 9) * 80, h: 1.6 + hash(i, 11) * 0.8, s: 0.07 + hash(i, 13) * 0.06, r: hash(i, 15) * 6 });
    const fx = { t: 0, sup: 1, lamp: 1, boulder: 1,
      lump(j) {
        if (!((back >> j) & 1)) return null;
        const e = k01(fx.t, 380 + j * 18, 680 + j * 18);
        return e <= 0 ? [0, 0, 0, 0] : [backOut(e), 0, 0, (1 - e) * 0.08];
      } };
    cellFx.set(kk, fx);
    writeWallColors();
    let dusted = false, moved = false;
    const tw = tween(D, (k) => {
      const t = k * D; fx.t = t;
      let nr = 0;
      for (let i = 0; i < n; i++) {
        const r = rocks[i], e = k01(t, r.t0, r.t0 + r.dur);
        if (t < r.t0) continue;
        const fall = easeIn(e), bounce = e >= 1 ? Math.sin(k01(t, r.t0 + r.dur, r.t0 + r.dur + 140) * Math.PI) * 0.06 : 0;
        const fade = 1 - k01(t, 720, 1000);
        setM(fallRocks, nr, r.x, r.y + r.h * (1 - fall) + bounce, r.z, r.r + t * 0.004, r.r, r.r * 0.5, r.s * fade);
        fallRocks.setColorAt(nr++, _c.set(PAL.stone).multiplyScalar(0.75 + hash(i, 2) * 0.4));
      }
      finish(fallRocks, nr);
      if (t >= 330 && !dusted) {
        dusted = true; spawnDust(ev.x, -ev.y, 0.1, 10, 0.6, 0.9);
        if (!reduced) shake = { t0: performance.now(), dur: 200, amp: 0.06 };
      }
      if (t >= 450 && !moved) { moved = true; placeFigures(performance.now(), true); }
      fx.sup = 1 - easeIn(k01(t, 360, 560));
      fx.lamp = 1 - k01(t, 340, 460);
      fx.boulder = 1 - k01(t, 400, 600);
      writeCellLumps(ci, kk); wall.instanceMatrix.needsUpdate = true;
      writeProps();
      if (k >= 1) {
        cellFx.delete(kk); leaving.delete(kk); finish(fallRocks, 0);
        writeCellLumps(ci, kk); writeProps(); wall.instanceMatrix.needsUpdate = true;
      }
      dirty = true;
    });
    return tw.promise;
  }

  // peek: над міткою розгортається пергамент із червоним пунктиром; моя — значок випливає й лишається;
  // чужа — лише мапа й промінь ліхтаря, без значка.
  function peek(ev, kind) {
    if (!G || !ev) return done();
    cancelFx();
    const i = ev.goal ?? ev.i;
    const g = S.goals[i];
    if (!g) return done();
    const mine = kind === 'gold' || kind === 'stone';
    if (reduced) { if (mine) S.peeks[i] = kind; writeGoals(); dirty = true; return done(); }
    const D = 1400, X = g.x, Y = -g.y, W0 = 0.92;
    const gf = { noPeek: true };
    goalFx.set(i, gf);
    writeGoals();
    const dst = new THREE.Vector3(X + 0.3, Y + 0.33, 0.12), src = new THREE.Vector3(X + 0.05, Y + 0.05, 0.62);
    const tw = tween(D, (k) => {
      const t = k * D;
      const u = t < 1000 ? easeOut(k01(t, 0, 450)) : 1 - easeInOut(k01(t, 1000, 1350));
      const show = u > 0.01;
      mapSheet.visible = mapRollA.visible = mapRollB.visible = show;
      const x0 = X - W0 / 2;
      mapSheet.position.set(x0, Y + 0.05, 0.55); mapSheet.scale.set(Math.max(0.001, u * W0), 1, 1); mapSheet.rotation.set(-0.12, 0, 0);
      mapRollA.position.set(x0, Y + 0.05, 0.57); mapRollB.position.set(x0 + u * W0, Y + 0.05, 0.57);
      mapRollA.rotation.set(-0.12, 0, 0); mapRollB.rotation.set(-0.12, u * 8, 0);
      if (mine) {
        const e = easeInOut(k01(t, 480, 1100));
        const m = kind === 'gold' ? 'coin' : 'stone';
        const mesh = m === 'coin' ? peekCoin : peekStone;
        flyIcon.coin = flyIcon.stone = 0;
        if (t >= 480 && k < 1) {
          flyIcon[m] = 1;
          const n = flyIcon.base[m];
          const p = src.clone().lerp(dst, e); p.y += Math.sin(Math.PI * e) * 0.35;
          setM(mesh, n, p.x, p.y, p.z, 0, (m === 'coin' ? 0.3 : 0) + (1 - e) * 6.3, 0, 1.6 - 0.6 * e);
          finish(mesh, n + 1);
        }
      } else {
        const b = k01(t, 150, 450) * (1 - k01(t, 900, 1200));
        beam.visible = b > 0.01;
        matBeam.opacity = b;
        // ліхтар десь угорі ліворуч світить на мітку; конус трохи гуляє
        const from = new THREE.Vector3(X - 1.1, Y + 1.5, 1.1), to = new THREE.Vector3(X + Math.sin(t * 0.004) * 0.12, Y, 0.05);
        const dir = to.clone().sub(from), len = dir.length();
        beam.position.copy(from); beam.scale.set(0.75, len, 0.75);
        beam.quaternion.setFromUnitVectors(new THREE.Vector3(0, -1, 0), dir.normalize());
      }
      if (k >= 1) {
        mapSheet.visible = mapRollA.visible = mapRollB.visible = beam.visible = false;
        flyIcon.coin = flyIcon.stone = 0;
        goalFx.delete(i);
        if (mine) S.peeks[i] = kind;
        writeGoals();
      }
      dirty = true;
    });
    return tw.promise;
  }

  // finale: diggers — скриня сяє, монети дощем; moles — кроти визирають із землі; none — пил.
  function finale(side) {
    if (!G) return done();
    cancelFx();
    if (reduced) return done();
    const D = side === 'diggers' ? 2400 : side === 'moles' ? 2300 : 2000;
    const og = S.goals.find((g) => g.kind === 'gold');
    const gx = og ? og.x : (G.x0 + G.x1) / 2, gy = og ? -og.y : 0;
    if (side === 'diggers') {
      let spawned = 0;
      const gf = og ? { glow: 1, light: 1.4, chest: og.open || og.kind ? { s: 1, lid: og.open ? 1 : 0 } : null, coins: og.open ? 1 : 0 } : null;
      if (og) goalFx.set(og.i, gf);
      const tw = tween(D, (k) => {
        const t = k * D;
        if (gf) {
          const p = Math.sin(Math.PI * k01(t, 0, 2200));
          gf.glow = 1 + 0.9 * p; gf.light = 1.4 + 3.6 * p;
          if (gf.chest && !og.open) gf.chest.lid = easeOut(k01(t, 100, 600));
          writeGoals(); writeProps();
        }
        const want = Math.floor(k01(t, 0, 1700) * 48);
        while (spawned < want) {
          spawned++;
          spawnCoin(new THREE.Vector3(gx + (Math.random() - 0.5) * 7, G.surfY + 0.8 + Math.random() * 1.5, 0.3 + Math.random() * 0.6),
            new THREE.Vector3((Math.random() - 0.5) * 0.4, -1.5 - Math.random() * 1.5, 0), 900 + Math.random() * 500, -9);
        }
        if (k >= 1) { goalFx.delete(og?.i); writeGoals(); writeProps(); }
        dirty = true;
      });
      return tw.promise;
    }
    if (side === 'moles') {
      const free = [];
      for (let y = G.y0; y <= G.y1; y++) for (let x = G.x0; x <= G.x1; x++) {
        const ci = cellIndex(x, y), kk = key(x, y);
        if (holes[ci] || S.cards.has(kk) || G.shaft.has(kk) || S.goals.some((g) => g.x === x && g.y === y)) continue;
        if (x < -1 || x > 9) continue;
        free.push([x, y]);
      }
      const pick = [];
      for (let i = 0; i < 6 && free.length; i++) pick.push(free.splice(Math.floor(Math.random() * free.length), 1)[0]);
      const ml = pick.map(([x, y], i) => ({ x, y, ci: cellIndex(x, y), kk: key(x, y), t0: i * 120, dusted: false }));
      for (const m of ml) {
        const fx = { lump: (j) => j === 4 ? [m.open ? 0 : 1, 0, 0, 0] : null };
        m.fx = fx; cellFx.set(m.kk, fx);
      }
      const tw = tween(D, (k) => {
        const t = k * D;
        let n = 0;
        for (const m of ml) {
          const up = easeOut(k01(t, m.t0, m.t0 + 320)), down = easeIn(k01(t, m.t0 + 1500, m.t0 + 1800));
          const e = up * (1 - down);
          m.open = e > 0.02;
          if (m.open && !m.dusted) { m.dusted = true; spawnDust(m.x, -m.y, 0.08, 4, 0.2, 0.5); }
          if (e > 0.001) setM(moles, n++, m.x, -m.y - 0.04, -0.3 + 0.55 * e, 0, Math.sin(t * 0.006 + m.t0) * 0.6 * e, Math.sin(t * 0.011 + m.t0) * 0.12, e * 1.5);
          writeCellLumps(m.ci, m.kk);
        }
        finish(moles, n);
        wall.instanceMatrix.needsUpdate = true;
        if (k >= 1) { for (const m of ml) { cellFx.delete(m.kk); writeCellLumps(m.ci, m.kk); } finish(moles, 0); wall.instanceMatrix.needsUpdate = true; }
        dirty = true;
      });
      return tw.promise;
    }
    // none: пил осідає над полем; ліхтарі на мить тьмяніють.
    let spawned = 0;
    const tw = tween(D, (k) => {
      const t = k * D;
      const want = Math.floor(k01(t, 0, 1200) * 30);
      while (spawned < want) { spawned++; spawnDust(G.x0 + 2 + Math.random() * (G.x1 - G.x0 - 4), -G.y0 - Math.random() * (G.y1 - G.y0), 0.12, 1, 0.3, 0.25); }
      warm.intensity = 0.35 * (1 - 0.5 * Math.sin(Math.PI * k));
      if (k >= 1) warm.intensity = 0.35;
      dirty = true;
    });
    return tw.promise;
  }

  // ------------------------------------------------------------------------------------------------ фон
  function animBg(t, force = false) {
    if (!craneArm || (!bgAnim && !force)) return;
    const a = bgAnim ? 0.035 * Math.sin(t * 0.55) + 0.012 * Math.sin(t * 1.3) : 0;
    craneArm.rotation.set(0, 0, a);
    const tip = craneTip.clone().applyAxisAngle(new THREE.Vector3(0, 0, 1), a).add(G.pivot);
    craneRope.position.copy(tip);
    craneRope.rotation.set(0, 0, bgAnim ? 0.04 * Math.sin(t * 0.9 + 0.5) : 0);
  }

  // ------------------------------------------------------------------------------------------------ цикл
  let raf = 0, dirty = true, clockT = 0, lastNow = performance.now(), lastRender = 0, lastActive = performance.now(), alive = true;
  const stats = { frames: 0 };
  function frame(now) {
    raf = 0;
    if (!alive || document.hidden) return;
    const dt = Math.min(0.05, Math.max(0, (now - lastNow) / 1000));
    lastNow = now; clockT += dt;
    let active = false;
    if (!ptrs.size && (Math.abs(vel.az) + Math.abs(vel.el) > 1e-5 || Math.abs(vel.x) + Math.abs(vel.y) > 1e-5)) {
      const ms = dt * 1000;
      cs.az = clamp(cs.az + vel.az * ms, -AZ_LIM, AZ_LIM); cs.el = clamp(cs.el + vel.el * ms, EL_MIN, EL_MAX);
      cs.target.x += vel.x * ms; cs.target.y += vel.y * ms; clampTarget();
      const f = Math.exp(-dt * 6);
      vel.az *= f; vel.el *= f; vel.x *= f; vel.y *= f;
      if (Math.abs(vel.az) + Math.abs(vel.el) < 1e-5) vel.az = vel.el = 0;
      if (Math.abs(vel.x) + Math.abs(vel.y) < 1e-5) vel.x = vel.y = 0;
      applyCam(now); active = true;
    }
    if (runTweens(now)) active = true;
    if (figJump.size) { if (placeFigures(now)) active = true; else dirty = true; }
    if (stepParts(dt, now)) active = true;
    if (shake) { applyCam(now); active = true; }
    const bg = (bgAnim && !!G) || (!!legal.kind && !reduced && hlItems.length > 0);
    const due = bg && now - lastRender >= 32;
    if (active || dirty || due) {
      if (bgAnim && G) { animBg(clockT); flicker(clockT); }
      if (legal.kind && !reduced) rebuildLegal(clockT);
      renderer.render(scene, cam);
      lastRender = now; dirty = false; stats.frames++;
    }
    if (active) lastActive = now;
    if (active || bg || dirty || now - lastActive < IDLE_MS) raf = requestAnimationFrame(frame);
  }
  function kick() {
    if (!alive || raf || document.hidden) return;
    lastNow = performance.now();
    raf = requestAnimationFrame(frame);
  }
  const wake = () => { dirty = true; lastActive = performance.now(); kick(); };
  function onVis() { if (document.hidden) { if (raf) cancelAnimationFrame(raf); raf = 0; } else wake(); }
  document.addEventListener('visibilitychange', onVis);
  const ro = typeof ResizeObserver === 'function' ? new ResizeObserver(() => { resize(); wake(); }) : null;
  ro?.observe(host);

  resize();
  setGrid(GRID);
  applyCam();
  kick();

  function dispose() {
    if (!alive) return;
    alive = false;
    if (raf) cancelAnimationFrame(raf); raf = 0;
    for (const tw of [...tweens]) { tw.done = true; tw.resolve(); }
    tweens.clear();
    document.removeEventListener('visibilitychange', onVis);
    ro?.disconnect();
    canvas.removeEventListener('pointerdown', onDown);
    canvas.removeEventListener('pointermove', onMove);
    canvas.removeEventListener('pointerup', onUp);
    canvas.removeEventListener('pointercancel', onUp);
    canvas.removeEventListener('pointerleave', onLeave);
    canvas.removeEventListener('wheel', onWheel);
    canvas.removeEventListener('dblclick', onDbl);
    canvas.removeEventListener('contextmenu', noCtx);
    disposeGrid();
    const geos = new Set();
    scene.traverse((o) => { if (o.geometry) geos.add(o.geometry); if (o.isInstancedMesh) o.dispose(); });
    geos.forEach((g) => g.dispose());
    mats.forEach((m) => m.dispose());
    moon.dispose?.(); flash.dispose?.(); chestLight.dispose?.();
    renderer.renderLists.dispose();
    renderer.dispose();
    renderer.forceContextLoss?.();
    canvas.remove();
  }

  const wrap = (fn) => (...a) => { if (!alive) return undefined; const r = fn(...a); wake(); return r; };
  const anim = (fn) => (...a) => { if (!alive) return Promise.resolve(); wake(); try { return fn(...a) || Promise.resolve(); } catch (e) { console.error(e); return Promise.resolve(); } };
  const api = {
    setGrid: wrap(setGrid),
    setState: wrap(setState),
    setLegal: wrap(setLegal),
    setCursor: wrap((x, y) => setCursor(x, y)),
    dig: anim(dig),
    flip: anim(flip),
    rockfall: anim(rockfall),
    peek: anim(peek),
    finale: anim(finale),
    focus: wrap((x, y) => focus(x, y)),
    screenPos: (kind, a, b) => alive ? screenPos(kind, a, b) : null,
    resetView: wrap(resetView),
    resize: wrap(() => { W = H = 0; resize(); }),
    dispose,
  };
  // Лише для стенду (dev/scene.html): рендерер, сцена, камера. У контракт не входить.
  Object.defineProperty(api, '_dbg', { value: { renderer, scene, camera: cam, stats, get raf() { return raf; }, get state() { return S; },
    render() { renderer.render(scene, cam); } }, enumerable: false });
  return api;
}
