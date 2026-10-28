// «Сорочинський ярмарок» — 3D-сцена поля («ярмарковий стіл»). API — рівно контракт §10 (docs/games/specs/sorochyntsi.md):
//   const sc = await createScene(host, { onPick, onHover, quality: 'high'|'low', reducedMotion });
//   sc.setState(view); sc.setLegal({ cards, decks, piles, guests }); sc.setMarks(marks); sc.setPick(pick); sc.setSelected(id);
//   await sc.take(ev) / reserve / buy / refill / discard / guest / starshyna / bulava; await sc.finale(over);
//   sc.screenPos(kind, id); sc.resetView(); sc.resize(); sc.dispose();
// Сцена не знає правил і не шле дій — лише малює й каже, куди клацнули (лише по підсвіченому).
//
// Будова: усе нерухоме (земля, стіл, підставка, ятки, вози, мішки, бочки, тополі, церква, мотузка) — ОДИН злитий меш із
// кольорами вершин; небо, скатертина й плями світла — ще по одному. Лиця всіх видимих плашок — ОДИН злитий меш з UV до
// атласу (перебудова лише коли змінився набір; підйом під курсором — атрибут вершин). Тіла плашок, 18 емблем, жетони
// кожного товару, плитки, підсвітка, ліхтарі гірлянди, стрічки — InstancedMesh. Справжніх світел три: небо (hemi),
// «сонце-ліхтар» (тіні лише на high) і теплий ліхтар над прилавком того, хто ходить (у фіналі — золоте світло переможцю).
// Клік — без raycast по мешах: найближча підсвічена ціль у пікселях екрана, хітбокс ≥ 1,2 плашки.

import * as THREE from '/static/vendor/three-0.186.1/three.module.js';
import * as M from './models.js';
import { GOODS, BONUSES, TOKENS, DECK, SEAT_COLOR, SEAT_LIGHT, PAL } from './consts.js';
import { wheelAction, wheelZoomFactor, twoFinger, hintDue } from '/games/khutory/gestures.js';

const D2R = Math.PI / 180;
const FOV = 40, DEF_POLAR = 35 * D2R, PORTRAIT_POLAR = 30 * D2R, POLAR_MIN = 25 * D2R, POLAR_MAX = 70 * D2R, AZ_LIM = 40 * D2R;
const ZOOM_MIN = 0.6, ZOOM_MAX = 1.8;
const TARGET = new THREE.Vector3(0, 0, -0.6);
const BG = '#1c1626';
const TAP_PX = 8;
const IDLE_MS = 2000;
const HOVER_LIFT = 0.12, SEL_LIFT = 0.22;
const PH = M.PLAQUE.h;
const UP = new THREE.Vector3(0, 1, 0);
const POS = { 1: ['bottom'], 2: ['bottom', 'top'], 3: ['bottom', 'left', 'right'], 4: ['bottom', 'left', 'top', 'right'] };

const clamp = (x, a, b) => Math.max(a, Math.min(b, x));
const lerp = (a, b, k) => a + (b - a) * k;
const k01 = (t, a, b) => clamp((t - a) / (b - a), 0, 1);
const easeOut = (k) => 1 - Math.pow(1 - k, 3);
const easeIn = (k) => k * k;
const easeInOut = (k) => k < 0.5 ? 4 * k * k * k : 1 - Math.pow(-2 * k + 2, 3) / 2;
function backOut(k) { const c1 = 1.9, c3 = c1 + 1; return 1 + c3 * Math.pow(k - 1, 3) + c1 * Math.pow(k - 1, 2); }
const hash = (a, b = 0) => { let h = Math.imul(a | 0, 374761393) ^ Math.imul(b | 0, 668265263); h = Math.imul(h ^ (h >>> 13), 1274126177); return ((h ^ (h >>> 16)) >>> 0) / 4294967296; };
function lerpAngle(a, b, k) { let d = (b - a) % (Math.PI * 2); if (d > Math.PI) d -= Math.PI * 2; if (d < -Math.PI) d += Math.PI * 2; return a + d * k; }
const emOf = (id) => DECK[id] ? { good: DECK[id][2], tier: DECK[id][1] } : null;
const deckH = (n) => PH + Math.max(0, n - 1) * 0.011;

/** Матеріал із клітинкою атласу на екземпляр (атрибут aCell): cols × rows клітинок. */
function cellMat(Base, params, cols, rows) {
  const m = new Base(params);
  m.onBeforeCompile = (sh) => {
    sh.vertexShader = 'attribute float aCell;\n' + sh.vertexShader.replace('#include <uv_vertex>',
      `#include <uv_vertex>\n  vMapUv = vec2((mod(aCell, ${cols}.0) + vMapUv.x) / ${cols}.0, 1.0 - (floor(aCell / ${cols}.0) + 1.0 - vMapUv.y) / ${rows}.0);`);
  };
  m.customProgramCacheKey = () => `cell${cols}x${rows}`;
  return m;
}

export async function createScene(host, opts = {}) {
  const quality = opts.quality === 'low' ? 'low' : 'high';
  const hi = quality === 'high';
  const reduced = !!opts.reducedMotion;
  const bgAnim = hi && !reduced;
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
  renderer.toneMappingExposure = 1.05;
  renderer.shadowMap.enabled = hi;
  renderer.shadowMap.type = THREE.PCFShadowMap;
  const canvas = renderer.domElement;
  canvas.style.cssText = 'display:block;width:100%;height:100%;touch-action:pan-y;outline:none;-webkit-tap-highlight-color:transparent;user-select:none';
  if (getComputedStyle(host).position === 'static') host.style.position = 'relative';
  host.appendChild(canvas);

  const scene = new THREE.Scene();
  scene.background = new THREE.Color(BG);
  scene.fog = new THREE.Fog('#3a2c44', 26, 60);
  const cam = new THREE.PerspectiveCamera(FOV, 1, 0.1, 200);

  const hemi = new THREE.HemisphereLight('#ffe6c8', '#4a3424', 1.25);
  scene.add(hemi);
  const sun = new THREE.DirectionalLight('#ffd8a6', 2.3);
  sun.position.set(-5, 12, 7);
  sun.target.position.set(0, 0, -0.8);
  sun.castShadow = hi;
  if (hi) {
    sun.shadow.mapSize.set(1024, 1024);
    Object.assign(sun.shadow.camera, { left: -8.5, right: 8.5, top: 7, bottom: -7, near: 2, far: 30 });
    sun.shadow.bias = -0.0006; sun.shadow.normalBias = 0.02;
  }
  scene.add(sun); scene.add(sun.target);
  const lamp = new THREE.PointLight(PAL.lantern, 0, 7, 1.4);
  scene.add(lamp);

  // ------------------------------------------------------------------------------------------------ матеріали
  const matVC = new THREE.MeshStandardMaterial({ vertexColors: true, flatShading: true, roughness: 0.88, metalness: 0 });
  const matMetal = new THREE.MeshStandardMaterial({ vertexColors: true, flatShading: true, roughness: 0.38, metalness: 0.55, emissive: new THREE.Color('#3a2600') });
  const fernPulse = { value: 0.6 };
  const matFern = new THREE.MeshStandardMaterial({ vertexColors: true, flatShading: true, roughness: 0.7, emissive: new THREE.Color(PAL.lantern) });
  matFern.onBeforeCompile = (sh) => {
    sh.uniforms.uPulse = fernPulse;
    sh.fragmentShader = 'uniform float uPulse;\n' + sh.fragmentShader.replace('#include <emissivemap_fragment>',
      '#include <emissivemap_fragment>\n  totalEmissiveRadiance = vec3(0.62, 0.32, 1.0) * uPulse * smoothstep(0.3, 0.55, vColor.b - vColor.g);');
  };
  matFern.customProgramCacheKey = () => 'fern';
  const matSky = new THREE.MeshBasicMaterial({ vertexColors: true, fog: false, depthWrite: false, side: THREE.DoubleSide });
  const matAddVC = new THREE.MeshBasicMaterial({ vertexColors: true, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, toneMapped: false });
  const matLamp = new THREE.MeshBasicMaterial({ vertexColors: true, toneMapped: false });
  const clothTex = M.clothTexture(hi);
  const matCloth = new THREE.MeshStandardMaterial({ map: clothTex, roughness: 0.95, polygonOffset: true, polygonOffsetFactor: -1, polygonOffsetUnits: -1 });
  const atlas = M.makeCardAtlas(hi);
  const matFaces = new THREE.MeshStandardMaterial({ map: atlas.tex, roughness: 0.72, polygonOffset: true, polygonOffsetFactor: -1, polygonOffsetUnits: -2 });
  matFaces.onBeforeCompile = (sh) => {
    sh.vertexShader = 'attribute float aLift;\n' + sh.vertexShader.replace('#include <begin_vertex>', '#include <begin_vertex>\n  transformed.y += aLift;');
  };
  matFaces.customProgramCacheKey = () => 'faces-lift';
  const tileTex = M.makeTileAtlas();
  const matTileFace = cellMat(THREE.MeshStandardMaterial, { map: tileTex, roughness: 0.75, polygonOffset: true, polygonOffsetFactor: -1, polygonOffsetUnits: -2 }, M.TILE_ATLAS.cols, M.TILE_ATLAS.rows);
  const iconTex = M.makeIconAtlas();
  const matIcon = cellMat(THREE.MeshBasicMaterial, { map: iconTex, transparent: true, alphaTest: 0.25, toneMapped: false, depthWrite: false }, M.ICON_CELLS, 1);
  const matFrame = new THREE.MeshBasicMaterial({ vertexColors: true, transparent: true, opacity: 0.95, depthWrite: false, toneMapped: false });
  const matNear = new THREE.MeshBasicMaterial({ vertexColors: true, transparent: true, opacity: 0.95, depthWrite: false, toneMapped: false });
  const rectGlowTex = M.rectGlowTexture(), roundGlowTex = M.glowTexture(), warmGlowTex = M.glowTexture('255,190,110');
  const matGlow = new THREE.MeshBasicMaterial({ map: rectGlowTex, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, toneMapped: false, opacity: 0.8 });
  const matRound = new THREE.MeshBasicMaterial({ map: roundGlowTex, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, toneMapped: false, opacity: 0.85 });
  const matWarm = new THREE.MeshBasicMaterial({ map: warmGlowTex, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, toneMapped: false, opacity: 0.32 });
  const matLampGlow = new THREE.MeshBasicMaterial({ map: warmGlowTex, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, toneMapped: false, fog: false });
  const matFlare = new THREE.MeshBasicMaterial({ map: roundGlowTex, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, toneMapped: false, opacity: 0, side: THREE.DoubleSide });
  const matRibbon = new THREE.MeshStandardMaterial({ roughness: 0.6, side: THREE.DoubleSide });
  const matDim = new THREE.MeshBasicMaterial({ color: '#0c0810', transparent: true, opacity: 0, depthWrite: false });
  const mats = [matVC, matMetal, matFern, matSky, matAddVC, matLamp, matCloth, matFaces, matTileFace, matIcon, matFrame, matNear, matGlow, matRound,
    matWarm, matLampGlow, matFlare, matRibbon, matDim];
  const texs = [clothTex, atlas.tex, tileTex, iconTex, rectGlowTex, roundGlowTex, warmGlowTex];

  // ------------------------------------------------------------------------------------------------ інстанси
  const _m4 = new THREE.Matrix4(), _m5 = new THREE.Matrix4(), _q = new THREE.Quaternion(), _eu = new THREE.Euler(), _pv = new THREE.Vector3(), _sv = new THREE.Vector3();
  const _c = new THREE.Color();
  function inst(geo, cap, { mat = matVC, color = false, cast = false, recv = false, order = 0, cell = false } = {}) {
    if (cell) geo.setAttribute('aCell', new THREE.InstancedBufferAttribute(new Float32Array(cap), 1));
    const m = new THREE.InstancedMesh(geo, mat, Math.max(1, cap));
    m.instanceMatrix.setUsage(THREE.DynamicDrawUsage);
    m.frustumCulled = false;
    m.castShadow = cast && hi; m.receiveShadow = recv && hi;
    if (color) { const w = new THREE.Color(1, 1, 1); for (let i = 0; i < cap; i++) m.setColorAt(i, w); m.instanceColor.setUsage(THREE.DynamicDrawUsage); }
    m.count = 0; m.visible = false; m.renderOrder = order;
    scene.add(m);
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
    const a = mesh.geometry.attributes.aCell; if (a) a.needsUpdate = true;
  }
  const setCell = (mesh, i, c) => { mesh.geometry.attributes.aCell.setX(i, c); };

  // Нерухоме.
  const stat = M.staticGeo(hi);
  const statMesh = new THREE.Mesh(stat.geo, matVC);
  statMesh.receiveShadow = hi; statMesh.matrixAutoUpdate = false;
  scene.add(statMesh);
  const spotMesh = new THREE.Mesh(stat.spots, matAddVC); spotMesh.matrixAutoUpdate = false; spotMesh.renderOrder = 2;
  scene.add(spotMesh);
  const sky = new THREE.Mesh(M.skyGeo(), matSky); sky.renderOrder = -1; sky.matrixAutoUpdate = false;
  sky.visible = hi;   // на low неба з типового кута не видно — заощаджуємо виклик
  scene.add(sky);
  const cloth = new THREE.Mesh(M.clothGeo(), matCloth); cloth.receiveShadow = hi; cloth.matrixAutoUpdate = false;
  scene.add(cloth);

  // Гірлянда: ліхтарі й ореоли.
  const lamps = inst(M.lampGeo(), M.GARLAND.n, { mat: matLamp });
  const lampGlows = inst(M.flatGeo(), M.GARLAND.n, { mat: matLampGlow, color: true, order: 3 });
  const lampAt = Array.from({ length: M.GARLAND.n }, (_, i) => ({ p: M.garlandPoint((i + 0.5) / M.GARLAND.n), ph: hash(i, 3) * 6.28 }));

  // Плашки.
  const plaqueGeo = M.plaqueGeo();
  const bodies = inst(plaqueGeo, 150, { cast: true, recv: true });
  const facesMesh = new THREE.Mesh(new THREE.BufferGeometry(), matFaces);
  facesMesh.frustumCulled = false; facesMesh.receiveShadow = hi; facesMesh.matrixAutoUpdate = false;
  scene.add(facesMesh);
  const emblems = new Map();   // 'fish2' → InstancedMesh
  function emblemMesh(good, tier) {
    const k = good + tier;
    let m = emblems.get(k);
    if (!m) { m = inst(M.emblemGeo(good, tier), 28, { mat: good === 'fern' ? matFern : matVC }); emblems.set(k, m); }
    return m;
  }
  // Жетони: на кожен товар (і дукат) — один InstancedMesh: купа в банку, стосики на прилавках, польоти.
  const tokens = {};
  for (const g of TOKENS) tokens[g] = inst(M.tokenGeo(g), 24, { mat: g === 'gold' ? matMetal : matVC, cast: true, recv: true });
  // Плитки (гості й старшина), їхні лиця (клітинка атласу на екземпляр).
  const tileBodies = inst(M.tileGeo(), 12, { cast: true, recv: true });
  const tileFaces = inst(M.tileFaceGeo(), 12, { mat: matTileFace, cell: true });
  tileFaces.receiveShadow = hi;
  const guestMeshes = new Map();
  function guestMesh(id) {
    let m = guestMeshes.get(id);
    if (!m) { m = new THREE.Mesh(M.guestGeo(id), matVC); m.castShadow = hi; m.visible = false; scene.add(m); guestMeshes.set(id, m); }
    return m;
  }
  const cossack = new THREE.Mesh(M.cossackGeo(), matVC); cossack.castShadow = hi; cossack.visible = false; scene.add(cossack);
  const bulavaM = new THREE.Mesh(M.bulavaGeo(), matMetal); bulavaM.castShadow = hi; bulavaM.visible = false; scene.add(bulavaM);
  const flare = new THREE.Mesh(new THREE.PlaneGeometry(1, 1), matFlare); flare.visible = false; flare.renderOrder = 9; scene.add(flare);

  // Підсвітка й позначки.
  const frames = inst(M.frameGeo(M.PLAQUE.w, M.PLAQUE.d), 32, { mat: matFrame, order: 5 });
  const tileFrames = inst(M.frameGeo(M.TILE, M.TILE, 0.06), 8, { mat: matFrame, order: 5 });
  const nears = inst(M.dashedFrameGeo(M.PLAQUE.w, M.PLAQUE.d), 32, { mat: matNear, order: 5 });
  const rings = inst(M.pileRingGeo(), 6, { mat: matFrame, order: 5 });
  const glows = inst(M.flatGeo(), 40, { mat: matGlow, order: 4 });
  const roundGlows = inst(M.flatGeo(), 16, { mat: matRound, order: 4 });
  const icons = inst(M.flatGeo(), 40, { mat: matIcon, cell: true, order: 6 });
  // Прилавок того, хто ходить: тепла пляма (і справжній ліхтар — lamp).
  const turnGlow = new THREE.Mesh(M.flatGeo(), matWarm); turnGlow.visible = false; turnGlow.renderOrder = 3; scene.add(turnGlow);
  // Фінал: стрічки й «тьмяні» прилавки.
  const ribbons = inst(M.ribbonGeo(), 60, { mat: matRibbon, color: true });
  const dims = inst(M.flatGeo(), 4, { mat: matDim, order: 7 });

  // Літаючі плашки (тіло, лице згори, лице знизу — для перевертання).
  function quadGeo(down) {
    const g = new THREE.PlaneGeometry(M.FACE.w, M.FACE.d);
    g.applyMatrix4(new THREE.Matrix4().makeRotationX(down ? Math.PI / 2 : -Math.PI / 2));
    if (down) g.applyMatrix4(new THREE.Matrix4().makeRotationY(Math.PI));
    g.translate(0, down ? -0.0015 : PH + 0.0015, 0);
    g.setAttribute('aLift', new THREE.Float32BufferAttribute(new Float32Array(g.attributes.position.count), 1));
    return g;
  }
  function setQuadCell(geo, cell) {
    const [u0, v0, su, sv] = M.atlasUv(cell, atlas.size);
    const uv = geo.attributes.uv;
    // PlaneGeometry: (0,1) (1,1) (0,0) (1,0)
    const base = [[0, 1], [1, 1], [0, 0], [1, 0]];
    for (let i = 0; i < 4; i++) uv.setXY(i, u0 + base[i][0] * su, v0 + base[i][1] * sv);
    uv.needsUpdate = true;
  }
  const flyers = [0, 1].map(() => {
    const g = new THREE.Group();
    const body = new THREE.Mesh(plaqueGeo, matVC); body.castShadow = hi;
    const top = new THREE.Mesh(quadGeo(false), matFaces), bot = new THREE.Mesh(quadGeo(true), matFaces);
    g.add(body); g.add(top); g.add(bot);
    g.visible = false; g.rotation.order = 'YXZ';
    scene.add(g);
    return { g, top, bot, em: null, busy: false };
  });
  function takeFlyer(topCell, botCell, em) {
    const f = flyers.find((x) => !x.busy) || flyers[0];
    f.busy = true; f.em = em;
    setQuadCell(f.top.geometry, topCell); setQuadCell(f.bot.geometry, botCell);
    f.bot.visible = botCell !== topCell;   // однакове лице знизу не потрібне: під час перевороту видно дерево
    f.g.visible = true;
    return f;
  }
  function freeFlyer(f) { f.busy = false; f.g.visible = false; f.em = null; }
  function poseFlyer(f, p) {
    f.g.position.set(p.x, p.y, p.z);
    f.g.rotation.set(p.rx || 0, p.ry || 0, p.rz || 0, 'YXZ');
    f.g.scale.setScalar(p.s);
    f.g.updateMatrixWorld(true);
  }

  // ------------------------------------------------------------------------------------------------ модель показу
  // S — що зараз намальовано: з виду (setState) або «після події» (анімації міняють його самі).
  let S = emptyS();
  function emptyS() {
    return { me: null, seats: [], rows: [[], [], []], decks: [0, 0, 0], bank: {}, tableGuests: [], starSeat: null, bulavaSeat: null, turn: null, over: false };
  }
  const seatOf = (s) => S.seats.find((x) => x.seat === s) || null;
  let view = null;
  let seatKey = '';
  let seatingMesh = null;

  function setState(v) {
    if (!v || !Array.isArray(v.players)) return;
    view = v;
    const order = Array.isArray(v.order) && v.order.length ? v.order : v.players.map((p) => p.seat);
    const me = v.me?.seat ?? order[0] ?? null;
    const i0 = Math.max(0, order.indexOf(me));
    const rot = order.slice(i0).concat(order.slice(0, i0));
    const posList = POS[Math.min(4, Math.max(1, rot.length))];
    const N = emptyS();
    N.me = me;
    N.seats = rot.map((seat, k) => {
      const p = v.players.find((q) => q.seat === seat) || { seat };
      const cards = {};
      for (const b of BONUSES) cards[b] = [];
      for (const id of p.cards || []) { const d = DECK[id]; if (d) cards[d[2]].push(id); }
      const tk = {};
      for (const g of TOKENS) tk[g] = Math.max(0, p.tokens?.[g] | 0);
      const color = ((p.color ?? seat) % 4 + 4) % 4;
      return { seat, pos: posList[k] || 'top', color, tokens: tk, cards,
        reserved: (p.reserved || []).map((r) => ({ id: r.id ?? null, tier: r.tier | 0 || 1, blind: !!r.blind })),
        guests: (p.guests || []).slice() };
    });
    N.rows = [0, 1, 2].map((t) => [0, 1, 2, 3].map((s) => v.rows?.[t]?.[s] ?? null));
    N.decks = [0, 1, 2].map((t) => Math.max(0, v.decks?.[t] | 0));
    for (const g of TOKENS) N.bank[g] = Math.max(0, v.bank?.[g] | 0);
    N.tableGuests = (v.guests || []).map((g) => ({ id: g.id, by: g.by ?? null }));
    // Гість, що вже в когось (players[].guests), — на прилавку.
    for (const st of N.seats) for (const gid of st.guests) { const tg = N.tableGuests.find((x) => x.id === gid); if (tg && tg.by == null) tg.by = st.seat; }
    for (const tg of N.tableGuests) if (tg.by != null) { const st = N.seats.find((x) => x.seat === tg.by); if (st && !st.guests.includes(tg.id)) st.guests.push(tg.id); }
    N.starSeat = v.starshyna?.seat ?? (v.players.find((p) => p.starshyna)?.seat ?? null);
    N.bulavaSeat = v.bulava ?? (v.players.find((p) => p.bulava)?.seat ?? null);
    N.turn = v.phase === 'over' ? null : (v.turn ?? null);
    N.over = v.phase === 'over';
    S = N;
    if (!S.over && fin) clearFinale();
    rebuildSeating();
    layoutAll();
  }

  function rebuildSeating() {
    const key = S.seats.map((s) => s.pos + s.color).join(',');
    if (key === seatKey) return;
    seatKey = key;
    if (seatingMesh) { seatingMesh.geometry.dispose(); scene.remove(seatingMesh); seatingMesh = null; }
    if (!S.seats.length) return;
    const b = new M.Builder();
    for (const st of S.seats) M.addCounter(b, M.COUNTER[st.pos], SEAT_COLOR[st.color], st.color, SEAT_LIGHT[st.color]);
    seatingMesh = new THREE.Mesh(b.geometry(), matVC);
    seatingMesh.castShadow = seatingMesh.receiveShadow = hi; seatingMesh.matrixAutoUpdate = false;
    scene.add(seatingMesh);
    fit();
  }

  // Локальне місце на прилавку → світ.
  function cpos(P, lx, lz, out = {}) {
    const c = Math.cos(P.ry), s = Math.sin(P.ry);
    out.x = P.x + lx * c + lz * s; out.z = P.z - lx * s + lz * c;
    return out;
  }
  const counterOf = (seat) => { const st = seatOf(seat); return st ? M.COUNTER[st.pos] : null; };

  // ------------------------------------------------------------------------------------------------ плашки
  // Позиції: ряд, колода, бронь, стос на прилавку.
  function rowPose(t, slot) { return { x: M.SLOT_X[slot], y: 0.004, z: M.ROW_Z[t], ry: 0, s: 1 }; }
  function deckTop(t) { const n = S.decks[t - 1] | 0; return { x: M.DECK_X, y: 0.004 + (n > 0 ? deckH(n) - PH : 0), z: M.ROW_Z[t], ry: 0, s: 1 }; }
  function resPose(seat, i) {
    const P = counterOf(seat); if (!P) return null;
    const w = cpos(P, M.SPOT.resX(i), M.SPOT.resZ);
    return { x: w.x, y: M.BOARD_H, z: w.z, ry: P.ry, s: M.CS };
  }
  function stackPose(seat, bonus, k, n) {
    const P = counterOf(seat); if (!P) return null;
    const i = BONUSES.indexOf(bonus), j = n - 1 - k;
    const w = cpos(P, M.SPOT.colX(i), M.SPOT.cardZ - Math.min(j, 6) * 0.08);
    return { x: w.x, y: M.BOARD_H + k * PH * M.CS, z: w.z, ry: P.ry, s: M.CS };
  }

  let PL = [];          // плашки на столі цього кадру
  let faceKey = '';
  const faceIdx = new Map();   // ключ плашки → номер у злитому меші
  function plaqueList() {
    const L = [];
    for (const t of [1, 2, 3]) {
      const n = S.decks[t - 1] | 0;
      if (n > 0) { const h = deckH(n); L.push({ key: 'd' + t, kind: 'deck', tier: t, cell: M.BACK_CELL(t), x: M.DECK_X, y: 0.004, z: M.ROW_Z[t], ry: 0, s: 1, sy: h / PH, h, pk: 'deck', pid: t }); }
      for (let slot = 0; slot < 4; slot++) {
        const id = S.rows[t - 1][slot]; if (id == null) continue;
        const p = rowPose(t, slot);
        L.push({ key: 'c' + id, kind: 'row', id, tier: t, slot, cell: id, ...p, sy: 1, h: PH, em: emOf(id), pk: 'card', pid: id });
      }
    }
    for (const st of S.seats) {
      for (const b of BONUSES) {
        const ids = st.cards[b], n = ids.length;
        ids.forEach((id, k) => {
          const p = stackPose(st.seat, b, k, n);
          L.push({ key: 'c' + id, kind: 'own', id, seat: st.seat, cell: id, ...p, sy: M.CS, h: PH * M.CS, em: k === n - 1 ? emOf(id) : null, pk: null, pid: id });
        });
      }
      st.reserved.forEach((r, i) => {
        const p = resPose(st.seat, i);
        const mine = st.seat === S.me;
        L.push({ key: r.id != null ? 'c' + r.id : 'b' + st.seat + '.' + i, kind: 'res', id: r.id, seat: st.seat, ri: i, cell: r.id != null ? r.id : M.BACK_CELL(r.tier), ...p,
          sy: M.CS, h: PH * M.CS, em: r.id != null ? emOf(r.id) : null, pk: mine && r.id != null ? 'card' : null, pid: r.id, blindMark: mine && r.blind && r.id != null });
      });
    }
    return L;
  }
  function liftOf(e) {
    if (e.pk === 'card' && selected != null && e.pid === selected) return SEL_LIFT;
    if (hover && e.pk === hover.kind && e.pid === hover.id && isLegal(hover.kind, hover.id)) return HOVER_LIFT;
    return 0;
  }
  function placePlaques() {
    PL = plaqueList();
    let n = 0;
    for (const e of PL) {
      e.lift = liftOf(e);
      if (n < bodies.instanceMatrix.count) setM(bodies, n++, e.x, e.y + e.lift, e.z, 0, e.ry, 0, e.s, e.sy, e.s);
    }
    finish(bodies, n);
    // Лиця — один злитий меш; перебудова, лише коли змінився набір.
    const key = PL.map((e) => `${e.cell}@${e.x.toFixed(3)},${(e.y + e.h).toFixed(3)},${e.z.toFixed(3)},${e.ry.toFixed(2)},${e.s}`).join('|');
    if (key !== faceKey) { faceKey = key; rebuildFaces(); }
    const la = facesMesh.geometry.attributes.aLift;
    if (la) {
      let ch = false;
      PL.forEach((e) => { const i = faceIdx.get(e.key); if (i == null) return; if (la.getX(i * 6) !== e.lift) { for (let k = 0; k < 6; k++) la.setX(i * 6 + k, e.lift); ch = true; } });
      if (ch) la.needsUpdate = true;
    }
    placeEmblems();
  }
  function rebuildFaces() {
    const n = PL.length;
    const pos = new Float32Array(n * 18), nor = new Float32Array(n * 18), uv = new Float32Array(n * 12), lift = new Float32Array(n * 6);
    faceIdx.clear();
    PL.forEach((e, i) => {
      faceIdx.set(e.key, i);
      const [u0, v0, su, sv] = M.atlasUv(e.cell, atlas.size);
      const hw = M.FACE.w / 2 * e.s, hd = M.FACE.d / 2 * e.s, y = e.y + e.h + 0.0015;
      const c = Math.cos(e.ry), s = Math.sin(e.ry);
      const P = (lx, lz) => [e.x + lx * c + lz * s, y, e.z - lx * s + lz * c];
      const TL = P(-hw, -hd), TR = P(hw, -hd), BR = P(hw, hd), BL = P(-hw, hd);
      const tTL = [u0, v0 + sv], tTR = [u0 + su, v0 + sv], tBR = [u0 + su, v0], tBL = [u0, v0];
      const vs = [TL, BL, BR, TL, BR, TR], ts = [tTL, tBL, tBR, tTL, tBR, tTR];
      for (let k = 0; k < 6; k++) {
        pos.set(vs[k], (i * 6 + k) * 3); nor.set([0, 1, 0], (i * 6 + k) * 3); uv.set(ts[k], (i * 6 + k) * 2); lift[i * 6 + k] = e.lift || 0;
      }
    });
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.BufferAttribute(pos, 3));
    g.setAttribute('normal', new THREE.BufferAttribute(nor, 3));
    g.setAttribute('uv', new THREE.BufferAttribute(uv, 2));
    g.setAttribute('aLift', new THREE.BufferAttribute(lift, 1));
    facesMesh.geometry.dispose();
    facesMesh.geometry = g;
    facesMesh.visible = n > 0;
  }
  const _em = new THREE.Matrix4();
  function placeEmblems() {
    const cnt = new Map();
    const put = (em, mat4) => {
      const m = emblemMesh(em.good, em.tier);
      const i = cnt.get(m) || 0;
      if (i >= m.instanceMatrix.count) return;
      m.setMatrixAt(i, mat4); cnt.set(m, i + 1);
    };
    for (const e of PL) {
      if (!e.em) continue;
      _eu.set(0, e.ry, 0, 'YXZ');
      _m4.compose(_pv.set(e.x, e.y + e.h + e.lift, e.z), _q.setFromEuler(_eu), _sv.set(e.s, e.s, e.s));
      put(e.em, _m5.copy(_m4).multiply(_em.makeTranslation(M.EMBLEM_AT[0], 0, M.EMBLEM_AT[1])));
    }
    for (const f of flyers) {
      if (!f.busy || !f.em || !f.g.visible) continue;
      _pv.set(0, 1, 0).applyQuaternion(f.g.quaternion);
      if (_pv.y < 0.97) continue;
      put(f.em, _m5.copy(f.g.matrixWorld).multiply(_em.makeTranslation(M.EMBLEM_AT[0], PH, M.EMBLEM_AT[1])));
    }
    for (const m of emblems.values()) finish(m, cnt.get(m) || 0);
  }

  // ------------------------------------------------------------------------------------------------ жетони
  const tokFly = [];    // { g, x, y, z, s, rx, rz }
  let pick = null;
  function bankTok(g, i) {
    const gi = TOKENS.indexOf(g);
    return { x: M.BANK_X + (hash(gi, i) - 0.5) * 0.05, y: i * M.TOKEN.h, z: M.bankZ(gi) + (hash(gi + 7, i) - 0.5) * 0.05, s: 1, ry: hash(gi, i + 30) * 6.28 };
  }
  function seatTok(seat, g, i) {
    const P = counterOf(seat); if (!P) return null;
    const w = cpos(P, M.SPOT.colX(TOKENS.indexOf(g)), M.SPOT.tokZ);
    return { x: w.x + (hash(seat, i) - 0.5) * 0.02, y: M.BOARD_H + i * M.TOKEN.h * M.TS, z: w.z + (hash(seat + 5, i) - 0.5) * 0.02, s: M.TS, ry: hash(seat * 7, i) * 6.28 };
  }
  function placeTokens(t = clockT) {
    for (const g of TOKENS) {
      const m = tokens[g];
      let n = 0;
      const nb = S.bank[g] | 0, np = Math.min(nb, pick?.[g] | 0);
      for (let i = 0; i < nb && n < m.instanceMatrix.count; i++) {
        const p = bankTok(g, i);
        let y = p.y;
        if (i >= nb - np) { const k = i - (nb - np); y = (nb - np) * M.TOKEN.h + 0.34 + k * 0.1 + (reduced ? 0 : 0.03 * Math.sin(t * 3 + k)); }
        setM(m, n++, p.x, y, p.z, 0, p.ry, 0, 1);
      }
      for (const st of S.seats) {
        const c = st.tokens[g] | 0;
        for (let i = 0; i < c && n < m.instanceMatrix.count; i++) { const p = seatTok(st.seat, g, i); setM(m, n++, p.x, p.y, p.z, 0, p.ry, 0, p.s); }
      }
      for (const f of tokFly) if (f.g === g && f.on && n < m.instanceMatrix.count) setM(m, n++, f.x, f.y, f.z, f.rx || 0, f.ry || 0, f.rz || 0, f.s);
      finish(m, n);
    }
  }

  // ------------------------------------------------------------------------------------------------ плитки, гості, нагороди
  const guestOver = new Map();   // id → поза під час анімації { x, y, z, s, ry }
  let starOver = null, bulavaOver = null;
  const hideTile = new Set();
  function tableSlot(i) { const n = S.tableGuests.length; return { x: (i - (n - 1) / 2) * M.GUEST_STEP, z: M.GUEST_Z }; }
  function guestHome(id) {
    const i = S.tableGuests.findIndex((g) => g.id === id);
    if (i < 0) return null;
    const g = S.tableGuests[i];
    if (g.by == null) { const p = tableSlot(i); return { x: p.x, y: M.TILE_H, z: p.z - 0.12, s: 1, ry: 0, onTable: true, i }; }
    const st = seatOf(g.by); if (!st) return null;
    const k = Math.max(0, st.guests.indexOf(id));
    const w = cpos(M.COUNTER[st.pos], M.SPOT.guestX(k), M.SPOT.guestZ(k));
    return { x: w.x, y: M.BOARD_H, z: w.z, s: M.GS, ry: 0, onTable: false };
  }
  function starHome() {
    if (S.starSeat == null || !seatOf(S.starSeat)) return { x: M.STAR_HOME.x, y: M.STAR_HOME.y, z: M.STAR_HOME.z, s: 0.95, ry: 0 };
    const P = counterOf(S.starSeat), w = cpos(P, M.SPOT.starX, M.SPOT.starZ);
    return { x: w.x, y: M.BOARD_H, z: w.z, s: 0.5, ry: P.ry };
  }
  function bulavaHome() {
    if (S.bulavaSeat == null || !seatOf(S.bulavaSeat)) return { x: M.BULAVA_HOME.x, y: M.BULAVA_HOME.y, z: M.BULAVA_HOME.z, s: 1, ry: M.BULAVA_HOME.ry, rx: 0, rz: 0 };
    const P = counterOf(S.bulavaSeat), w = cpos(P, M.SPOT.bulavaX, M.SPOT.bulavaZ);
    return { x: w.x, y: M.BOARD_H + 0.07, z: w.z, s: 0.55, ry: P.ry + 0.5, rx: 0, rz: 0 };
  }
  function placeTiles() {
    let n = 0;
    S.tableGuests.forEach((g, i) => {
      if (g.by != null || hideTile.has(g.id)) return;
      const p = tableSlot(i);
      const sc = g.sink ?? 1;
      setM(tileBodies, n, p.x, 0.004, p.z, 0, 0, 0, sc, 1, sc); setM(tileFaces, n, p.x, 0.004, p.z, 0, 0, 0, sc, 1, sc); setCell(tileFaces, n, g.id); n++;
    });
    const sp = starOver || starHome();
    setM(tileBodies, n, sp.x, sp.y, sp.z, sp.rx || 0, sp.ry, sp.rz || 0, sp.s); setM(tileFaces, n, sp.x, sp.y, sp.z, sp.rx || 0, sp.ry, sp.rz || 0, sp.s); setCell(tileFaces, n, M.STARSHYNA_CELL); n++;
    finish(tileBodies, n); finish(tileFaces, n);
    // козак на плитці старшини — позаду, лицем до глядача
    _eu.set(sp.rx || 0, sp.ry, sp.rz || 0, 'YXZ'); _q.setFromEuler(_eu);
    _pv.set(0, M.TILE_H, -0.18).applyQuaternion(_q).multiplyScalar(sp.s);
    cossack.position.set(sp.x + _pv.x, sp.y + _pv.y, sp.z + _pv.z);
    cossack.quaternion.copy(_q).multiply(new THREE.Quaternion().setFromAxisAngle(UP, -sp.ry));
    cossack.scale.setScalar(sp.s * 1.05);
    cossack.visible = true;
    // гості
    const seen = new Set();
    for (const g of S.tableGuests) {
      const p = guestOver.get(g.id) || guestHome(g.id);
      const m = guestMesh(g.id);
      seen.add(g.id);
      if (!p) { m.visible = false; continue; }
      m.position.set(p.x, p.y, p.z); m.rotation.set(0, p.ry || 0, 0); m.scale.setScalar(p.s * (g.sink ?? 1)); m.visible = true;
    }
    for (const [id, m] of guestMeshes) if (!seen.has(id)) m.visible = false;
    // булава
    const bp = bulavaOver || bulavaHome();
    bulavaM.position.set(bp.x, bp.y, bp.z); bulavaM.rotation.set(bp.rx || 0, bp.ry, bp.rz || 0, 'YXZ'); bulavaM.scale.setScalar(bp.s);
    bulavaM.visible = true;
  }

  // ------------------------------------------------------------------------------------------------ хто ходить
  function placeTurn() {
    const P = S.turn != null ? counterOf(S.turn) : null;
    if (fin) return;
    if (!P) { turnGlow.visible = false; lamp.intensity = 0; return; }
    const w = cpos(P, 0, -0.1);
    turnGlow.position.set(w.x, 0.012, w.z);
    turnGlow.rotation.set(0, P.ry, 0);
    turnGlow.scale.set(M.COUNTER_W + 1.6, 1, M.COUNTER_D + 1.4);
    turnGlow.visible = true;
    const l = cpos(P, 0, -0.9);
    lamp.color.set(PAL.lantern);
    lamp.position.set(l.x, 2.1, l.z);
    lamp.distance = 6.5;
    lamp.intensity = 4.5;
  }

  // ------------------------------------------------------------------------------------------------ підсвітка
  let legal = { cards: new Set(), decks: new Set(), piles: new Set(), guests: new Set() };
  let marks = {};
  let selected = null;
  let hover = null;   // { kind, id }
  function isLegal(kind, id) {
    return kind === 'card' ? legal.cards.has(id) : kind === 'deck' ? legal.decks.has(id) : kind === 'pile' ? legal.piles.has(id) : kind === 'guest' ? legal.guests.has(id) : false;
  }
  const anyLegal = () => legal.cards.size + legal.decks.size + legal.piles.size + legal.guests.size > 0;
  function setLegal(l) {
    const ids = (a) => new Set(Array.isArray(a) ? a : []);
    legal = l ? { cards: ids(l.cards), decks: ids(l.decks), piles: ids(l.piles), guests: ids(l.guests) } : { cards: new Set(), decks: new Set(), piles: new Set(), guests: new Set() };
    if (hover && !isLegal(hover.kind, hover.id) && hover.kind !== 'card') setHover(null);
    placePlaques(); placeHL();
  }
  function setMarks(m) { marks = m && typeof m === 'object' ? { ...m } : {}; placeHL(); }
  function setPick(p) {
    pick = null;
    if (p && typeof p === 'object') { pick = {}; for (const g of GOODS) if ((p[g] | 0) > 0) pick[g] = p[g] | 0; }
    placeTokens();
  }
  function setSelected(id) { selected = id ?? null; placePlaques(); placeHL(); }

  function placeHL(t = clockT) {
    const pulse = reduced ? 1 : 0.5 + 0.5 * Math.sin(t * 4.2);
    let nf = 0, ng = 0, nn = 0, ni = 0, nr = 0, nt = 0, nrg = 0;
    for (const e of PL) {
      const lg = e.pk && isLegal(e.pk, e.pid);
      const sel = e.pk === 'card' && selected != null && e.pid === selected;
      const top = e.y + e.h + e.lift;
      if (lg) {
        const s = e.s * (1 + 0.025 * pulse);
        setM(frames, nf++, e.x, top + 0.004, e.z, 0, e.ry, 0, s, 1, s);
      }
      if (lg || sel) {
        const k = sel ? 1.25 : 1;
        setM(glows, ng++, e.x, 0.008 + (e.kind === 'res' ? M.BOARD_H : 0), e.z, 0, e.ry, 0, e.s * 1.7 * k, 1, e.s * 2.1 * k);
      }
      const mk = e.id != null && (e.kind === 'row' || (e.kind === 'res' && e.seat === S.me)) ? marks[e.id] : null;
      if (mk === 'near') setM(nears, nn++, e.x, top + 0.003, e.z, 0, e.ry, 0, e.s, 1, e.s);
      if (mk === 'buy' || mk === 'gold') {
        _eu.set(0, e.ry, 0); _q.setFromEuler(_eu);
        _pv.set(0.47, 0, -0.67).applyQuaternion(_q).multiplyScalar(e.s);
        setM(icons, ni, e.x + _pv.x, top + 0.035, e.z + _pv.z, 0, e.ry, 0, 0.36 * e.s); setCell(icons, ni++, mk === 'buy' ? 0 : 1);
      }
      if (e.blindMark) {
        _eu.set(0, e.ry, 0); _q.setFromEuler(_eu);
        _pv.set(-0.47, 0, 0.67).applyQuaternion(_q).multiplyScalar(e.s);
        setM(icons, ni, e.x + _pv.x, top + 0.03, e.z + _pv.z, 0, e.ry, 0, 0.4 * e.s); setCell(icons, ni++, 2);
      }
    }
    for (const g of legal.piles) {
      const gi = TOKENS.indexOf(g); if (gi < 0) continue;
      const hov = hover && hover.kind === 'pile' && hover.id === g;
      const s = (hov ? 1.12 : 1) * (1 + 0.04 * pulse);
      setM(rings, nr++, M.BANK_X, 0.006, M.bankZ(gi), 0, 0, 0, s, 1, s);
      setM(roundGlows, nrg++, M.BANK_X, 0.007, M.bankZ(gi), 0, 0, 0, 1.15, 1, 1.15);
    }
    S.tableGuests.forEach((g, i) => {
      if (g.by != null || !legal.guests.has(g.id)) return;
      const p = tableSlot(i);
      const hov = hover && hover.kind === 'guest' && hover.id === g.id;
      const s = (hov ? 1.06 : 1) * (1 + 0.025 * pulse);
      setM(tileFrames, nt++, p.x, 0.004 + M.TILE_H + 0.004, p.z, 0, 0, 0, s, 1, s);
      setM(roundGlows, nrg++, p.x, 0.007, p.z, 0, 0, 0, 1.9, 1, 1.9);
    });
    finish(frames, nf); finish(glows, ng); finish(nears, nn); finish(icons, ni); finish(rings, nr); finish(tileFrames, nt); finish(roundGlows, nrg);
    matFrame.opacity = reduced ? 1 : 0.78 + 0.22 * pulse;
    matGlow.opacity = reduced ? 0.75 : 0.55 + 0.35 * pulse;
    matRound.opacity = reduced ? 0.7 : 0.45 + 0.3 * pulse;
    matNear.opacity = reduced ? 0.95 : 0.7 + 0.3 * (0.5 + 0.5 * Math.sin(t * 2.6));
  }

  function layoutAll() {
    placePlaques(); placeTokens(); placeTiles(); placeTurn(); placeHL();
  }

  // ------------------------------------------------------------------------------------------------ тло: гірлянда
  function animBg(t) {
    lampAt.forEach((o, i) => {
      const [x, y, z] = o.p;
      const a = bgAnim ? 0.09 * Math.sin(t * 1.3 + o.ph) : 0, b = bgAnim ? 0.05 * Math.sin(t * 0.9 + o.ph * 1.3) : 0;
      setM(lamps, i, x, y, z, b, 0, a, 1);
      const lx = x - Math.sin(a) * 0.16, ly = y - 0.16 * Math.cos(a), lz = z + Math.sin(b) * 0.16;
      setM(lampGlows, i, lx, ly, lz + 0.2, Math.PI / 2 - 0.45, 0, 0, 1.3, 1, 1.3);
      const f = bgAnim ? 0.82 + 0.1 * Math.sin(t * 7.3 + o.ph) + 0.08 * Math.sin(t * 13.7 + o.ph * 2.1) : 0.9;
      lampGlows.setColorAt(i, _c.setRGB(f, f, f));
    });
    finish(lamps, lampAt.length); finish(lampGlows, hi ? lampAt.length : 0);   // low: без ореолів (бюджет ≤ 50 викликів)
    fernPulse.value = bgAnim ? 0.45 + 0.4 * (0.5 + 0.5 * Math.sin(t * 2.2)) : 0.65;
  }

  // ------------------------------------------------------------------------------------------------ камера
  const cs = { az: 0, pol: DEF_POLAR, zoom: 1, target: TARGET.clone() };
  const vel = { az: 0, pol: 0 };
  let baseDist = 16, W = 1, H = 1, defPol = DEF_POLAR, touchedView = false;
  const defTarget = TARGET.clone();
  function applyCam() {
    const d = baseDist / cs.zoom, sp = Math.sin(cs.pol);
    cam.position.set(cs.target.x + d * sp * Math.sin(cs.az), cs.target.y + d * Math.cos(cs.pol), cs.target.z + d * sp * Math.cos(cs.az));
    cam.lookAt(cs.target);
    cam.updateMatrixWorld();
    scene.fog.near = d + 10; scene.fog.far = d + 42;
  }
  const _pj = new THREE.Vector3();
  function fitPts() {
    const pts = [];
    const add = (x, z, y = 0) => pts.push(new THREE.Vector3(x, y, z));
    // ринок: колоди, ряди, банк, гості, підставка
    for (const x of [-3.85, 4.2]) for (const z of [-4.35, 1.75]) add(x, z);
    add(0, -4.3, 0.75);
    // мій прилавок
    const P = M.COUNTER.bottom;
    for (const x of [-3.9, 3.6]) for (const z of [P.z - 0.8, P.z + 0.85]) add(x, z);
    return pts;
  }
  function fit() {
    const save = { az: cs.az, pol: cs.pol, zoom: cs.zoom, t: cs.target.clone() };
    const portrait = W / H < 0.9;
    defPol = portrait ? PORTRAIT_POLAR : DEF_POLAR;
    cs.az = 0; cs.pol = defPol; cs.zoom = 1; cs.target.copy(TARGET);
    const pts = fitPts(), limX = portrait ? 0.99 : 0.94, limY = portrait ? 0.97 : 0.95;
    let lo = 3, hiD = 120;
    for (let it = 0; it < 32; it++) {
      baseDist = (lo + hiD) / 2; applyCam();
      let ok = true;
      for (const p of pts) { _pj.copy(p).project(cam); if (Math.abs(_pj.x) > limX || Math.abs(_pj.y) > limY || _pj.z > 1) { ok = false; break; } }
      if (ok) hiD = baseDist; else lo = baseDist;
    }
    baseDist = hiD;
    // Портрет: ширина впирається раніше за висоту — опускаємо ринок і прилавок до низу рамки (згори — ятки ярмарку),
    // щоб унизу не лишалось порожньої стільниці.
    defTarget.copy(TARGET);
    if (portrait) {
      for (let pass = 0; pass < 3; pass++) {
        cs.target.copy(defTarget); applyCam();
        let y0 = 1;
        for (const p of pts) { _pj.copy(p).project(cam); y0 = Math.min(y0, _pj.y); }
        defTarget.z += (-0.94 - y0) / 2 * baseDist * Math.tan(cam.fov * D2R / 2) / Math.cos(defPol) * 0.9;
      }
    }
    cs.az = save.az; cs.pol = save.pol; cs.zoom = save.zoom; cs.target.copy(save.t);
    if (!touchedView) { cs.target.copy(defTarget); cs.pol = defPol; cs.az = 0; cs.zoom = 1; }
    applyCam();
  }
  let resetAnim = null;
  function resetView() {
    touchedView = false;
    vel.az = vel.pol = 0;
    if (resetAnim) resetAnim.cancel();
    const from = { az: cs.az, pol: cs.pol, zoom: cs.zoom, t: cs.target.clone() };
    const step = (k) => {
      const e = easeInOut(k);
      cs.az = lerp(from.az, 0, e); cs.pol = lerp(from.pol, defPol, e); cs.zoom = lerp(from.zoom, 1, e);
      cs.target.lerpVectors(from.t, defTarget, e);
      applyCam();
    };
    if (reduced) { step(1); return; }
    resetAnim = tween(450, step);
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
  const _v = new THREE.Vector3();
  function targets(onlyLegal) {
    const T = [];
    for (const e of PL) {
      if (!e.pk) continue;
      if (onlyLegal && !isLegal(e.pk, e.pid)) continue;
      if (!onlyLegal && e.pk !== 'card') continue;
      T.push({ kind: e.pk, id: e.pid, x: e.x, y: e.y + e.h + e.lift, z: e.z, ry: e.ry, hw: M.PLAQUE.w / 2 * e.s, hd: M.PLAQUE.d / 2 * e.s });
    }
    if (onlyLegal) {
      for (const g of legal.piles) {
        const gi = TOKENS.indexOf(g); if (gi < 0) continue;
        T.push({ kind: 'pile', id: g, x: M.BANK_X, y: Math.max(1, S.bank[g] | 0) * M.TOKEN.h * 0.5, z: M.bankZ(gi), ry: 0, hw: 0.4, hd: 0.4 });
      }
      S.tableGuests.forEach((g, i) => {
        if (g.by != null || !legal.guests.has(g.id)) return;
        const p = tableSlot(i);
        T.push({ kind: 'guest', id: g.id, x: p.x, y: M.TILE_H + 0.2, z: p.z, ry: 0, hw: M.TILE / 2, hd: M.TILE / 2 + 0.1 });
      });
    }
    return T;
  }
  function inQuad(px, py, q) {
    let sign = 0;
    for (let i = 0; i < 4; i++) {
      const a = q[i], b = q[(i + 1) % 4];
      const c = (b.x - a.x) * (py - a.y) - (b.y - a.y) * (px - a.x);
      if (c !== 0) { if (sign === 0) sign = Math.sign(c); else if (Math.sign(c) !== sign) return false; }
    }
    return true;
  }
  function hitIn(T, x, y, touch) {
    let best = null, bd = Infinity;
    const thr = touch ? 34 : 16;
    for (const t of T) {
      const c = Math.cos(t.ry), s = Math.sin(t.ry), k = 1.2;
      const q = [[-1, -1], [1, -1], [1, 1], [-1, 1]].map(([a, b]) => {
        const lx = a * t.hw * k, lz = b * t.hd * k;
        return project(_v.set(t.x + lx * c + lz * s, t.y, t.z - lx * s + lz * c), {});
      });
      const cc = project(_v.set(t.x, t.y, t.z), {});
      const d = Math.hypot(cc.x - x, cc.y - y);
      const inside = inQuad(x, y, q);
      // Найближче рахуємо від центру; ззовні хітбокса — лише в межах «пальця» від краю.
      let edge = Infinity;
      if (!inside) for (let i = 0; i < 4; i++) {
        const a = q[i], b = q[(i + 1) % 4], dx = b.x - a.x, dy = b.y - a.y, L2 = dx * dx + dy * dy || 1;
        const u = clamp(((x - a.x) * dx + (y - a.y) * dy) / L2, 0, 1);
        edge = Math.min(edge, Math.hypot(a.x + dx * u - x, a.y + dy * u - y));
      }
      if (!inside && edge > thr) continue;
      const score = inside ? d : d + 1000 + edge;
      if (score < bd) { bd = score; best = { kind: t.kind, id: t.id }; }
    }
    return best;
  }
  const hitAt = (x, y, touch) => anyLegal() ? hitIn(targets(true), x, y, touch) : null;
  const hoverAt = (x, y) => hitAt(x, y, false) || hitIn(targets(false), x, y, false);

  function setHover(h) {
    const same = (h?.kind ?? null) === (hover?.kind ?? null) && (h?.id ?? null) === (hover?.id ?? null);
    hover = h;
    canvas.style.cursor = h && isLegal(h.kind, h.id) ? 'pointer' : '';
    if (!same) { placePlaques(); placeHL(); onHover(h ? h.kind : null, h ? h.id : null); }
  }

  function screenPos(kind, id) {
    let P = null;
    if (kind === 'card') {
      const e = PL.find((q) => q.id === id && q.id != null);
      if (e) P = new THREE.Vector3(e.x, e.y + e.h + (e.lift || 0), e.z);
    } else if (kind === 'deck') {
      const t = id | 0; if (t >= 1 && t <= 3) { const p = deckTop(t); P = new THREE.Vector3(p.x, p.y + PH, p.z); }
    } else if (kind === 'pile') {
      const gi = TOKENS.indexOf(id); if (gi >= 0) P = new THREE.Vector3(M.BANK_X, (S.bank[id] | 0) * M.TOKEN.h + 0.05, M.bankZ(gi));
    } else if (kind === 'guest') {
      const p = guestOver.get(id) || guestHome(id); if (p) P = new THREE.Vector3(p.x, p.y + 0.62 * p.s, p.z);
    } else if (kind === 'seat') {
      const C = counterOf(id); if (C) { const w = cpos(C, 0, -0.1); P = new THREE.Vector3(w.x, 0.35, w.z); }
    } else if (kind === 'starshyna') {
      const p = starOver || starHome(); P = new THREE.Vector3(p.x, p.y + 0.3 * p.s, p.z);
    } else if (kind === 'bulava') {
      const p = bulavaOver || bulavaHome(); P = new THREE.Vector3(p.x, p.y + 0.05, p.z);
    }
    if (!P) return null;
    const s = project(P);
    if (s.z > 1) return null;
    return { x: Math.round(s.x * 10) / 10, y: Math.round(s.y * 10) / 10 };
  }

  // ------------------------------------------------------------------------------------------------ вказівник
  // Як у «Хуторах»: поле не забирає прокрутку сторінки (touch-action: pan-y). Дотик: один палець угору-вниз гортає
  // сторінку, убік — азимут, тицяння — вибір; два пальці — щипок = наближення, ведення = зсув, поворот = азимут. Миша:
  // тягнути — обертати, права/середня/Shift — зсув, колесо гортає сторінку, Ctrl/⌘ + колесо (і щипок тачпада) — наближення.
  const ptrs = new Map();
  let drag = null, pinch = null, lastPointerType = 'mouse', lastTap = 0, multi = false;
  const local = (e) => { const r = canvas.getBoundingClientRect(); return [e.clientX - r.left, e.clientY - r.top]; };
  const ROT_DEAD = 0.14;
  function startPinch() {
    const [a, b] = [...ptrs.values()];
    pinch = { a0: { ...a }, b0: { ...b }, a: { ...a }, b: { ...b }, z0: cs.zoom, rotAcc: 0, rotating: false };
  }
  function onDown(e) {
    wake();
    lastPointerType = e.pointerType || 'mouse';
    try { canvas.setPointerCapture(e.pointerId); } catch { }
    const [x, y] = local(e);
    ptrs.set(e.pointerId, { x, y });
    vel.az = vel.pol = 0;
    if (resetAnim) { resetAnim.cancel(); resetAnim = null; }
    if (ptrs.size === 1) drag = { x0: x, y0: y, x, y, moved: false, swiped: false, pan: e.button === 1 || e.button === 2 || e.shiftKey, lt: performance.now() };
    else if (ptrs.size === 2) { startPinch(); if (drag) drag.moved = true; seenHint2f(); }
  }
  function onMove(e) {
    lastPointerType = e.pointerType || 'mouse';
    const [x, y] = local(e);
    if (!ptrs.has(e.pointerId)) {
      if (lastPointerType === 'mouse') { setHover(hoverAt(x, y)); wake(); }
      return;
    }
    wake();
    ptrs.get(e.pointerId).x = x; ptrs.get(e.pointerId).y = y;
    if (ptrs.size >= 2 && pinch) {
      const [a, b] = [...ptrs.values()];
      cs.zoom = clamp(pinch.z0 * twoFinger(pinch.a0, pinch.b0, a, b).scale, ZOOM_MIN, ZOOM_MAX);
      const g = twoFinger(pinch.a, pinch.b, a, b);
      panBy(g.dx, g.dy);
      if (pinch.rotating) cs.az = clamp(cs.az + g.rot, -AZ_LIM, AZ_LIM);
      else if (Math.abs(pinch.rotAcc += g.rot) > ROT_DEAD) pinch.rotating = true;
      pinch.a = { ...a }; pinch.b = { ...b };
      touchedView = true; applyCam();
      return;
    }
    if (!drag) return;
    const dx = x - drag.x, dy = y - drag.y;
    const touch = lastPointerType !== 'mouse';
    if (!drag.moved) {
      const ox = Math.abs(x - drag.x0), oy = Math.abs(y - drag.y0);
      if (touch) { if (ox > TAP_PX && ox > oy) { drag.moved = true; setHover(null); } else if (oy > TAP_PX) drag.swiped = true; }
      else if (Math.hypot(ox, oy) >= TAP_PX) { drag.moved = true; setHover(null); }
    }
    if (drag.moved) {
      const now = performance.now(), dt = Math.max(1, now - drag.lt);
      if (drag.pan) panBy(dx, dy);
      else {
        const da = -dx * 0.006, dp = touch ? 0 : -dy * 0.005;
        cs.az = clamp(cs.az + da, -AZ_LIM, AZ_LIM); cs.pol = clamp(cs.pol + dp, POLAR_MIN, POLAR_MAX);
        vel.az = 0.6 * vel.az + 0.4 * da / dt; vel.pol = 0.6 * vel.pol + 0.4 * dp / dt;
      }
      drag.lt = now;
      touchedView = true; applyCam();
    }
    drag.x = x; drag.y = y;
  }
  function panBy(dx, dy) {
    const d = baseDist / cs.zoom, k = 2 * d * Math.tan(cam.fov * D2R / 2) / H;
    const right = new THREE.Vector3(Math.cos(cs.az), 0, -Math.sin(cs.az)), fwd = new THREE.Vector3(-Math.sin(cs.az), 0, -Math.cos(cs.az));
    cs.target.addScaledVector(right, -dx * k).addScaledVector(fwd, dy * k / Math.cos(cs.pol) * 0.6);
    const ox = cs.target.x - defTarget.x, oz = cs.target.z - defTarget.z, r = Math.hypot(ox, oz), R = 3.5;
    if (r > R) { cs.target.x = defTarget.x + ox * R / r; cs.target.z = defTarget.z + oz * R / r; }
  }
  function onUp(e) {
    wake();
    const p = ptrs.get(e.pointerId);
    ptrs.delete(e.pointerId);
    try { canvas.releasePointerCapture(e.pointerId); } catch { }
    if (ptrs.size < 2) pinch = null;
    else if (ptrs.size >= 2) { startPinch(); return; }
    if (ptrs.size === 1) { const [q] = [...ptrs.values()]; drag = { x0: q.x, y0: q.y, x: q.x, y: q.y, moved: true, pan: false, lt: performance.now() }; return; }
    const touch = (e.pointerType || 'mouse') !== 'mouse';
    if (e.type === 'pointercancel' && touch && drag && !drag.moved && !multi) showHint2f();
    if (!drag || !p || e.type === 'pointercancel') { drag = null; return; }
    const d = drag; drag = null;
    if (d.moved) { if (performance.now() - d.lt > 80) vel.az = vel.pol = 0; return; }
    if (d.swiped) return;
    const hit = hitAt(p.x, p.y, touch);
    if (hit) { lastTap = 0; onPick(hit.kind, hit.id); return; }
    if (touch) {
      const now = performance.now();
      if (now - lastTap < 320) { lastTap = 0; resetView(); } else lastTap = now;
    }
  }
  function onWheel(e) {
    if (wheelAction(e) !== 'zoom') { if (Math.abs(e.deltaY) >= Math.abs(e.deltaX)) showHintWheel(); return; }
    e.preventDefault(); wake();
    cs.zoom = clamp(cs.zoom * wheelZoomFactor(e.deltaY, e.deltaMode), ZOOM_MIN, ZOOM_MAX);
    touchedView = true; applyCam();
  }
  function onTouch(e) {
    if (e.touches.length >= 2) multi = true;
    else if (!e.touches.length && (e.type === 'touchend' || e.type === 'touchcancel')) multi = false;
    if (multi && e.type === 'touchmove' && e.cancelable) e.preventDefault();
  }
  const noGesture = (e) => e.preventDefault();
  function onDbl(e) { e.preventDefault(); if (!hitAt(...local(e), false)) resetView(); }
  function onLeave() { if (!ptrs.size) { setHover(null); wake(); } }
  const noCtx = (e) => e.preventDefault();

  const hintEl = document.createElement('div');
  hintEl.className = 'sor-hint';
  hintEl.setAttribute('aria-hidden', 'true');
  hintEl.style.cssText = 'position:absolute;left:50%;top:50%;transform:translate(-50%,-50%);z-index:4;pointer-events:none;'
    + 'max-width:80%;padding:9px 16px;border-radius:12px;background:rgba(30,18,10,.86);color:#fff;text-align:center;'
    + 'font:600 14px/1.3 system-ui,sans-serif;box-shadow:0 4px 18px rgba(0,0,0,.35);opacity:0;transition:opacity .2s;white-space:nowrap';
  host.appendChild(hintEl);
  const isMac = /Mac|iP(hone|ad|od)/.test((typeof navigator !== 'undefined' && (navigator.platform || navigator.userAgent)) || '');
  const WHEEL_HINT = (isMac ? '⌘' : 'Ctrl') + ' + коліщатко — наблизити';
  let hintTimer = 0, lastWheelHint = 0;
  function showHint(text, ms) {
    hintEl.textContent = text; hintEl.style.opacity = '1';
    clearTimeout(hintTimer);
    hintTimer = setTimeout(() => { hintEl.style.opacity = '0'; }, ms);
  }
  function showHintWheel() {
    const now = performance.now();
    if (!hintDue(now, lastWheelHint, 5000)) return;
    lastWheelHint = now;
    showHint(WHEEL_HINT, 1500);
  }
  const HINT2F_KEY = 'sor.hint2f';
  let hint2fSeen = false;
  try { hint2fSeen = sessionStorage.getItem(HINT2F_KEY) === '1'; } catch { }
  function seenHint2f() { if (hint2fSeen) return; hint2fSeen = true; try { sessionStorage.setItem(HINT2F_KEY, '1'); } catch { } }
  function showHint2f() { if (hint2fSeen) return; seenHint2f(); showHint('Двома пальцями — крутити й наближати', 2200); }

  canvas.addEventListener('pointerdown', onDown);
  canvas.addEventListener('pointermove', onMove);
  canvas.addEventListener('pointerup', onUp);
  canvas.addEventListener('pointercancel', onUp);
  canvas.addEventListener('pointerleave', onLeave);
  canvas.addEventListener('wheel', onWheel, { passive: false });
  canvas.addEventListener('dblclick', onDbl);
  canvas.addEventListener('contextmenu', noCtx);
  for (const t of ['touchstart', 'touchmove', 'touchend', 'touchcancel']) canvas.addEventListener(t, onTouch, { passive: false });
  canvas.addEventListener('gesturestart', noGesture);
  canvas.addEventListener('gesturechange', noGesture);

  // ------------------------------------------------------------------------------------------------ анімації
  const tweens = new Set();
  function tween(dur, fn) {
    let resolve;
    const p = new Promise((r) => { resolve = r; });
    const tw = { t0: performance.now(), dur: Math.max(1, dur), fn, done: false, resolve, promise: p,
      cancel() { if (tw.done) return; tw.done = true; tweens.delete(tw); resolve(); } };
    tweens.add(tw);
    wake();
    return tw;
  }
  function runTweens(now) {
    for (const tw of [...tweens]) {
      const k = clamp((now - tw.t0) / tw.dur, 0, 1);
      try { tw.fn(k, now - tw.t0); } catch (e) { console.error(e); tw.done = true; tweens.delete(tw); tw.resolve(); continue; }
      if (k >= 1) { tw.done = true; tweens.delete(tw); tw.resolve(); }
    }
    return tweens.size > 0;
  }
  const quick = () => tween(reduced ? 90 : 1, () => {}).promise;   // reducedMotion: «миттєво» (≤ 150 мс)
  const arcPt = (a, b, e, h) => ({ x: lerp(a.x, b.x, e), y: lerp(a.y, b.y, e) + Math.sin(Math.PI * e) * h, z: lerp(a.z, b.z, e) });

  /** Політ жетонів: list — [{ g, from, to }], по одному з затримкою stagger; onLand(item) — коли сів. */
  function flyTokens(list, { dur = 480, stagger = 80, h = 0.9, delay = 0, onLand = () => {} } = {}) {
    if (!list.length) return Promise.resolve();
    const total = delay + stagger * (list.length - 1) + dur;
    const items = list.map((o, i) => ({ ...o, f: { g: o.g, on: false, x: o.from.x, y: o.from.y, z: o.from.z, s: o.from.s, ry: o.from.ry || 0, rx: 0, rz: 0 }, t0: delay + i * stagger, landed: false }));
    for (const it of items) tokFly.push(it.f);
    return tween(total, (k, ms) => {
      for (const it of items) {
        const kk = clamp((ms - it.t0) / dur, 0, 1);
        it.f.on = !it.landed;
        const e = easeInOut(kk), p = arcPt(it.from, it.to, e, h);
        it.f.x = p.x; it.f.y = p.y; it.f.z = p.z; it.f.s = lerp(it.from.s, it.to.s, e);
        it.f.rx = Math.sin(Math.PI * e) * 0.6; it.f.ry = lerpAngle(it.from.ry || 0, it.to.ry || 0, e);
        if (kk >= 1 && !it.landed) { it.landed = true; it.f.on = false; onLand(it); }
      }
      if (k >= 1) for (const it of items) { if (!it.landed) { it.landed = true; onLand(it); } const j = tokFly.indexOf(it.f); if (j >= 0) tokFly.splice(j, 1); }
      placeTokens();
    }).promise;
  }
  /** Політ плашки: from/to — пози; top/bot — клітинки атласу; rz0→rz1 — перевертання навколо довгої осі. */
  function flyPlaque({ from, to, topCell, botCell, em, dur = 650, h = 1.0, rz0 = 0, rz1 = 0, delay = 0, slide = 0, onEnd = () => {} }) {
    const f = takeFlyer(topCell, botCell, em);
    poseFlyer(f, { ...from, rz: rz0 });
    return tween(dur + delay, (k, ms) => {
      const kk = clamp((ms - delay) / dur, 0, 1);
      let p;
      if (slide > 0 && kk < slide) {   // зісковзує з колоди вбік, потім летить
        const e = easeOut(kk / slide);
        p = { x: from.x + 0.35 * e, y: from.y + 0.02, z: from.z, ry: from.ry, s: from.s, rz: rz0 };
      } else {
        const k2 = slide > 0 ? (kk - slide) / (1 - slide) : kk, e = easeInOut(k2);
        const a = slide > 0 ? { ...from, x: from.x + 0.35 } : from;
        const q = arcPt(a, to, e, h);
        p = { ...q, ry: lerpAngle(a.ry, to.ry, e), s: lerp(a.s, to.s, e), rz: lerp(rz0, rz1, easeInOut(k01(k2, 0.1, 0.85))) };
      }
      poseFlyer(f, p);
      placeEmblems();
      if (k >= 1) { freeFlyer(f); onEnd(); placePlaques(); placeHL(); }
    }).promise;
  }

  function take(ev) {
    const st = seatOf(ev?.seat); const got = ev?.got || {};
    if (!st) return quick();
    pick = null;
    const list = [];
    for (const g of TOKENS) {
      const n = Math.max(0, got[g] | 0);
      for (let k = 0; k < n; k++) {
        const bi = (S.bank[g] | 0) - 1 - k;
        list.push({ g, from: bankTok(g, Math.max(0, bi)), to: seatTok(st.seat, g, (st.tokens[g] | 0) + k) });
      }
    }
    for (const g of TOKENS) S.bank[g] = Math.max(0, (S.bank[g] | 0) - (got[g] | 0));
    if (reduced) { for (const o of list) st.tokens[o.g]++; placeTokens(); return quick(); }
    placeTokens();
    return flyTokens(list, { dur: 520, stagger: 80, h: 1.0, onLand: (it) => { st.tokens[it.g]++; } });
  }

  function reserve(ev) {
    const st = seatOf(ev?.seat); if (!st) return quick();
    const t = clamp(ev.tier | 0 || 1, 1, 3);
    const blind = ev.slot == null;
    const id = blind ? (ev.card ?? null) : (ev.card ?? S.rows[t - 1][ev.slot]);
    const mine = st.seat === S.me;
    let from;
    if (blind) { from = deckTop(t); S.decks[t - 1] = Math.max(0, S.decks[t - 1] - 1); }
    else { from = rowPose(t, ev.slot); S.rows[t - 1][ev.slot] = null; }
    const ri = st.reserved.length;
    const to = resPose(st.seat, ri);
    const entry = { id: blind && !mine ? null : id, tier: t, blind };
    const gold = (ev.gold | 0) > 0 && (S.bank.gold | 0) > 0;
    const gList = gold ? [{ g: 'gold', from: bankTok('gold', (S.bank.gold | 0) - 1), to: seatTok(st.seat, 'gold', st.tokens.gold | 0) }] : [];
    if (gold) S.bank.gold--;
    if (reduced || !to) { st.reserved.push(entry); if (gold) st.tokens.gold++; layoutAll(); return quick(); }
    placePlaques(); placeTokens(); placeHL();
    const faceCell = id != null ? id : M.BACK_CELL(t);
    const p1 = blind
      ? flyPlaque({ from, to, topCell: M.BACK_CELL(t), botCell: faceCell, em: null, dur: 680, h: 1.2, rz0: 0, rz1: mine && id != null ? Math.PI : 0,
        onEnd: () => st.reserved.push(entry) })
      : flyPlaque({ from, to, topCell: id, botCell: id, em: emOf(id), dur: 700, h: 1.3, rz0: 0, rz1: Math.PI * 2, onEnd: () => st.reserved.push(entry) });
    const p2 = flyTokens(gList, { dur: 480, delay: 260, h: 0.9, onLand: () => { st.tokens.gold++; } });
    return Promise.all([p1, p2]).then(() => { layoutAll(); });
  }

  function buy(ev) {
    const st = seatOf(ev?.seat); if (!st) return quick();
    const id = ev.card;
    const d = DECK[id]; if (!d) return quick();
    const [, t, bonus] = d;
    let from = null;
    if (ev.from === 'reserve') {
      let ri = Number.isInteger(ev.slot) ? ev.slot : st.reserved.findIndex((r) => r.id === id);
      if (ri < 0 || ri >= st.reserved.length) ri = st.reserved.findIndex((r) => r.id === id || r.id == null);
      if (ri >= 0) { from = resPose(st.seat, ri); st.reserved.splice(ri, 1); }
    } else {
      let slot = Number.isInteger(ev.slot) ? ev.slot : S.rows[t - 1].indexOf(id);
      if (slot < 0) slot = S.rows[t - 1].indexOf(id);
      if (slot >= 0) { from = rowPose(t, slot); S.rows[t - 1][slot] = null; }
    }
    // плата
    const pay = ev.pay || {}, list = [];
    for (const g of TOKENS) {
      const n = Math.min(Math.max(0, pay[g] | 0), st.tokens[g] | 0);
      for (let k = 0; k < n; k++) list.push({ g, from: seatTok(st.seat, g, (st.tokens[g] | 0) - 1 - k), to: bankTok(g, (S.bank[g] | 0) + k) });
      st.tokens[g] = (st.tokens[g] | 0) - n;
    }
    const n0 = st.cards[bonus].length;
    if (reduced || !from) { for (const o of list) S.bank[o.g]++; st.cards[bonus].push(id); layoutAll(); return quick(); }
    layoutAll();
    const stagger = list.length > 1 ? Math.min(60, 280 / (list.length - 1)) : 0;
    const p1 = flyTokens(list, { dur: 400, stagger, h: 0.8, onLand: (it) => { S.bank[it.g]++; } });
    const to = stackPose(st.seat, bonus, n0, n0 + 1);
    const p2 = flyPlaque({ from, to, topCell: id, botCell: id, em: emOf(id), dur: 620, delay: list.length ? 420 : 0, h: 1.1,
      onEnd: () => { st.cards[bonus].push(id); } });
    return Promise.all([p1, p2]).then(() => layoutAll());
  }

  function refill(ev) {
    const t = clamp(ev?.tier | 0 || 1, 1, 3), slot = ev?.slot;
    if (!Number.isInteger(slot) || slot < 0 || slot > 3) return quick();
    if (ev.card == null) { S.rows[t - 1][slot] = null; layoutAll(); return quick(); }
    const from = deckTop(t);
    S.decks[t - 1] = Math.max(0, S.decks[t - 1] - 1);
    S.rows[t - 1][slot] = null;
    if (reduced) { S.rows[t - 1][slot] = ev.card; layoutAll(); return quick(); }
    placePlaques(); placeHL();
    return flyPlaque({ from, to: rowPose(t, slot), topCell: M.BACK_CELL(t), botCell: ev.card, em: emOf(ev.card), dur: 560, h: 0.35, rz0: 0, rz1: Math.PI, slide: 0.2,
      onEnd: () => { S.rows[t - 1][slot] = ev.card; } });
  }

  function discard(ev) {
    const st = seatOf(ev?.seat); if (!st) return quick();
    const gave = ev.gave || {}, list = [];
    for (const g of TOKENS) {
      const n = Math.min(Math.max(0, gave[g] | 0), st.tokens[g] | 0);
      for (let k = 0; k < n; k++) list.push({ g, from: seatTok(st.seat, g, (st.tokens[g] | 0) - 1 - k), to: bankTok(g, (S.bank[g] | 0) + k) });
      st.tokens[g] = (st.tokens[g] | 0) - n;
    }
    if (reduced) { for (const o of list) S.bank[o.g]++; placeTokens(); return quick(); }
    placeTokens();
    const stagger = list.length > 1 ? Math.min(70, 220 / (list.length - 1)) : 0;
    return flyTokens(list, { dur: 460, stagger, h: 0.8, onLand: (it) => { S.bank[it.g]++; } });
  }

  function guest(ev) {
    const st = seatOf(ev?.seat); const gid = ev?.guest;
    const tg = S.tableGuests.find((g) => g.id === gid);
    if (!st || !tg) return quick();
    const from = guestHome(gid);
    if (tg.by === st.seat && st.guests.includes(gid)) return quick();
    const apply = () => { tg.by = st.seat; if (!st.guests.includes(gid)) st.guests.push(gid); };
    if (reduced || !from) { apply(); layoutAll(); return quick(); }
    const k = st.guests.length;
    const w = cpos(M.COUNTER[st.pos], M.SPOT.guestX(k), M.SPOT.guestZ(k));
    const to = { x: w.x, y: M.BOARD_H, z: w.z, s: M.GS };
    const hops = 4, D = 1150;
    return tween(D, (kk) => {
      const e = easeInOut(kk);
      const hop = Math.abs(Math.sin(kk * Math.PI * hops)) * (0.28 + 0.25 * Math.sin(Math.PI * kk));
      const lift = Math.sin(Math.PI * e) * 0.5;
      const dir = Math.atan2(to.x - from.x, to.z - from.z);
      const face = kk < 0.12 ? lerpAngle(0, dir, kk / 0.12) : kk > 0.86 ? lerpAngle(dir, 0, (kk - 0.86) / 0.14) : dir;
      guestOver.set(gid, { x: lerp(from.x, to.x, e), y: lerp(from.y, to.y, e) + hop + lift, z: lerp(from.z, to.z, e), s: lerp(from.s, to.s, e), ry: face });
      tg.sink = 1;
      if (kk > 0.05) hideTile.add(gid);
      placeTiles();
      if (kk >= 1) { guestOver.delete(gid); hideTile.delete(gid); delete tg.sink; apply(); layoutAll(); }
    }).promise;
  }

  function starshyna(ev) {
    const st = seatOf(ev?.seat); if (!st) return quick();
    if (S.starSeat === st.seat) return quick();
    const from = starHome();
    S.starSeat = st.seat;
    const to = starHome();
    if (reduced) { layoutAll(); return quick(); }
    return tween(950, (k) => {
      const e = easeInOut(k), p = arcPt(from, to, e, 1.6);
      starOver = { ...p, s: lerp(from.s, to.s, e), ry: lerpAngle(from.ry, to.ry, e) + Math.sin(Math.PI * e) * Math.PI * 2, rx: Math.sin(Math.PI * e) * -0.5 };
      placeTiles();
      if (k >= 1) { starOver = null; layoutAll(); }
    }).promise;
  }

  function bulava(ev) {
    const st = seatOf(ev?.seat); if (!st) return quick();
    if (S.bulavaSeat === st.seat) return quick();
    const from = bulavaHome();
    S.bulavaSeat = st.seat;
    const to = bulavaHome();
    if (reduced) { layoutAll(); return quick(); }
    const D = 1550, top = { x: from.x, y: from.y + 1.3, z: from.z + 0.2 };
    return tween(D, (k, ms) => {
      let p;
      if (ms < 380) {   // підйом
        const e = easeOut(ms / 380);
        p = { x: from.x, y: lerp(from.y, top.y, e), z: lerp(from.z, top.z, e), s: lerp(from.s, 1.4, e), ry: from.ry, rx: 0, rz: lerp(0, Math.PI / 2, e) };
      } else if (ms < 900) {   // обертання й сяйво
        const e = (ms - 380) / 520;
        p = { x: top.x, y: top.y + Math.sin(e * Math.PI) * 0.1, z: top.z, s: 1.4, ry: from.ry + e * Math.PI * 3, rx: 0, rz: Math.PI / 2 };
      } else {   // переліт
        const e = easeInOut((ms - 900) / 650);
        const q = arcPt(top, to, e, 0.8);
        p = { ...q, s: lerp(1.4, to.s, e), ry: lerpAngle(from.ry + Math.PI * 3, to.ry, e), rx: 0, rz: lerp(Math.PI / 2, 0, e) };
      }
      bulavaOver = p;
      const glow = ms < 380 ? ms / 380 : ms < 900 ? 1 : 1 - (ms - 900) / 650;
      flare.visible = glow > 0.01;
      flare.position.set(p.x, p.y + 0.02, p.z);
      flare.quaternion.copy(cam.quaternion);
      flare.scale.setScalar(1.6 + 0.9 * glow + (ms > 380 && ms < 900 ? 0.25 * Math.sin(ms / 40) : 0));
      matFlare.opacity = 0.9 * glow;
      placeTiles();
      if (k >= 1) { bulavaOver = null; flare.visible = false; matFlare.opacity = 0; layoutAll(); }
    }).promise;
  }

  // Фінал: стрічки над переможцем(ями), золоте світло, решта прилавків тьмяніє. Лишається до setState не в 'over'.
  let fin = null;
  function clearFinale() {
    fin = null;
    finish(ribbons, 0); finish(dims, 0); matDim.opacity = 0;
    placeTurn();
  }
  function finale(over) {
    const winners = new Set(Array.isArray(over?.winners) ? over.winners : []);
    const win = S.seats.filter((s) => winners.has(s.seat)), lose = S.seats.filter((s) => !winners.has(s.seat));
    fin = { winners };
    turnGlow.visible = false;
    let nd = 0;
    for (const st of lose) {
      const P = M.COUNTER[st.pos];
      setM(dims, nd++, P.x, 0.62, P.z, 0, P.ry, 0, M.COUNTER_W + 1.3, 1, M.COUNTER_D + 1.0);
    }
    finish(dims, nd);
    const W0 = win[0] ? M.COUNTER[win[0].pos] : null;
    const setLight = (k) => {
      if (!W0) { lamp.intensity = 0; return; }
      const l = cpos(W0, 0, -0.7);
      lamp.color.set(PAL.gild); lamp.position.set(l.x, 2.4, l.z); lamp.distance = 8; lamp.intensity = 10 * k;
      turnGlow.visible = k > 0.01; turnGlow.position.set(W0.x, 0.012, W0.z); turnGlow.rotation.set(0, W0.ry, 0);
      turnGlow.scale.set(M.COUNTER_W + 2.4, 1, M.COUNTER_D + 2.2); matWarm.opacity = 0.32 + 0.3 * k;
    };
    // стрічки
    const R = [];
    const cols = [PAL.gild, PAL.embroideryRed, '#ffffff', '#3d7cc9', '#4f9a3a', PAL.legal];
    const nR = win.length ? 60 : 0;
    for (let i = 0; i < nR; i++) {
      const st = win[i % win.length], P = M.COUNTER[st.pos];
      const w = cpos(P, (hash(i, 1) - 0.5) * M.COUNTER_W, (hash(i, 2) - 0.5) * M.COUNTER_D);
      R.push({ x: w.x, z: w.z, y0: 3 + hash(i, 3) * 2.2, d: hash(i, 4) * 700, sp: 0.8 + hash(i, 5), ph: hash(i, 6) * 6.28, c: cols[i % cols.length] });
      ribbons.setColorAt(i, _c.set(R[i].c));
    }
    if (reduced) {
      R.forEach((o, i) => setM(ribbons, i, o.x, M.BOARD_H + 0.02, o.z, Math.PI / 2, o.ph, 0, 1));
      finish(ribbons, R.length); matDim.opacity = 0.5; setLight(1);
      return quick();
    }
    const D = 2400;
    return tween(D, (k, ms) => {
      matDim.opacity = 0.5 * easeOut(k01(ms, 0, 700));
      setLight(easeOut(k01(ms, 0, 900)));
      R.forEach((o, i) => {
        const tt = Math.max(0, ms - o.d) / 1000;
        const y = Math.max(M.BOARD_H + 0.03, o.y0 - tt * 1.9 * o.sp);
        const landed = y <= M.BOARD_H + 0.031;
        const sway = landed ? 0 : Math.sin(tt * 5 + o.ph) * 0.25;
        setM(ribbons, i, o.x + sway, ms < o.d ? -5 : y, o.z + Math.cos(tt * 4 + o.ph) * 0.15,
          landed ? Math.PI / 2 : tt * 6 + o.ph, o.ph + tt * 2, landed ? 0 : Math.sin(tt * 7) * 0.8, 1);
      });
      finish(ribbons, R.length);
    }).promise;
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
    if (!ptrs.size && (Math.abs(vel.az) > 1e-5 || Math.abs(vel.pol) > 1e-5)) {
      const ms = dt * 1000;
      cs.az = clamp(cs.az + vel.az * ms, -AZ_LIM, AZ_LIM); cs.pol = clamp(cs.pol + vel.pol * ms, POLAR_MIN, POLAR_MAX);
      const f = Math.exp(-dt * 6); vel.az *= f; vel.pol *= f;
      if (Math.abs(vel.az) < 1e-5) vel.az = 0;
      if (Math.abs(vel.pol) < 1e-5) vel.pol = 0;
      applyCam(); active = true;
    }
    if (runTweens(now)) active = true;
    const hl = !reduced && (anyLegal() || Object.values(marks).includes('near') || !!pick);
    const bg = bgAnim || hl;
    const due = bg && now - lastRender >= 32;
    if (active || dirty || due) {
      if (bgAnim) animBg(clockT);
      if (hl) { placeHL(clockT); if (pick) placeTokens(clockT); }
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
  function wake() { dirty = true; lastActive = performance.now(); kick(); }
  function onVis() { if (document.hidden) { if (raf) cancelAnimationFrame(raf); raf = 0; } else wake(); }
  document.addEventListener('visibilitychange', onVis);
  const ro = typeof ResizeObserver === 'function' ? new ResizeObserver(() => { resize(); wake(); }) : null;
  ro?.observe(host);

  animBg(0);
  resize();
  applyCam();
  layoutAll();
  wake();

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
    for (const t of ['touchstart', 'touchmove', 'touchend', 'touchcancel']) canvas.removeEventListener(t, onTouch, { passive: false });
    canvas.removeEventListener('gesturestart', noGesture);
    canvas.removeEventListener('gesturechange', noGesture);
    clearTimeout(hintTimer);
    hintEl.remove();
    const geos = new Set();
    scene.traverse((o) => { if (o.geometry) geos.add(o.geometry); if (o.isInstancedMesh) o.dispose(); });
    geos.forEach((g) => g.dispose());
    mats.forEach((m) => m.dispose());
    texs.forEach((t) => t.dispose());
    sun.dispose?.(); lamp.dispose?.(); hemi.dispose?.();
    renderer.renderLists.dispose();
    renderer.dispose();
    renderer.forceContextLoss?.();
    canvas.remove();
  }

  const wrap = (fn) => (...a) => { if (!alive) return undefined; const r = fn(...a); wake(); return r; };
  const anim = (fn) => (...a) => { if (!alive) return Promise.resolve(); wake(); try { return (fn(...a) || Promise.resolve()).then(() => { wake(); }); } catch (e) { console.error(e); return Promise.resolve(); } };
  const api = {
    setState: wrap(setState),
    setLegal: wrap(setLegal),
    setMarks: wrap(setMarks),
    setPick: wrap(setPick),
    setSelected: wrap(setSelected),
    take: anim(take),
    reserve: anim(reserve),
    buy: anim(buy),
    refill: anim(refill),
    discard: anim(discard),
    guest: anim(guest),
    starshyna: anim(starshyna),
    bulava: anim(bulava),
    finale: anim(finale),
    screenPos: (kind, id) => alive ? screenPos(kind, id) : null,
    resetView: wrap(resetView),
    resize: wrap(() => { W = H = 0; resize(); }),
    dispose,
  };
  // Лише для стенду (dev/scene.html): рендерер, сцена, камера, лічильник кадрів. У контракт не входить.
  Object.defineProperty(api, '_dbg', { value: { renderer, scene, camera: cam, stats, get raf() { return raf; }, get state() { return S; },
    render() { renderer.render(scene, cam); } }, enumerable: false });
  return api;
}
