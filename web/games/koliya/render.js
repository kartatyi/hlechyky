// «Колія» — 3D-вид із кубиків (three). Власник: K1 «Світ».
// createRenderer(canvas) → { setWorld(s), draw(s, alpha), events(ev), dispose() }
//  setWorld — будує сцену з нуля (рестарт); draw — щокадру: порівнює масиви стану з копією й оновлює лише змінені клітинки
//  (InstancedMesh-и з вільними слотами), інтерполює гравців і потяг між тиками (alpha 0..1), веде камеру й світло;
//  events — події тику (s.ev): тріски, бризки, пара, конфеті.
// Світ: 1 клітинка = 1 одиниця; x стану → X, y стану → Z; підлога на Y = 0. Моделі дивляться в +X.
import * as THREE from '/static/vendor/three-0.186.1/three.module.js';
import { U, G, O, I, WAGONS } from './rules.js';

// ---------- палітра ----------
const C = {
  sky: 0xbfe3f2,
  grassA: 0x97d15c, grassB: 0x8cc853, grassDark: 0x79b54a,
  sand: 0xead79c, sandB: 0xe1cc8c,
  bed: 0x4f8a8c, dirt: 0x8a5a3a, dirtDark: 0x6e4429,
  station: 0xd6cdb9, stationB: 0xc5bba6,
  water: 0x45aee6,
  wallA: 0x8f949c, wallB: 0x7b8088, wallC: 0xa4a8ae, cap: 0x86bf55,
  trunk: 0x7a5232, trunkB: 0x8c5f39,
  leaf: [0x4fa83f, 0x5cb848, 0x45a03c, 0x68c24f], pine: [0x2f8a4a, 0x3a9a55, 0x2b7d44],
  rock: [0x8e949c, 0x7f858e, 0x9ca1a8, 0x878a8f], ore: [0xc8642c, 0xe0843e, 0x3f434b],
  plank: [0xb07a45, 0x9a6838, 0xc08850],
  tie: 0x6e4a2e, steel: 0xb4bac2,
  flower: [0xffffff, 0xffe066, 0xff6b6b, 0xc69cff], tuft: 0x6aa843, pebble: 0xb9ae96,
};
const PLAYER = [
  { shirt: 0xffd23f, hat: 0xe04b3a }, { shirt: 0xff5fa2, hat: 0x6b4cff },
  { shirt: 0x4fc3ff, hat: 0xff9f1c }, { shirt: 0x9be15d, hat: 0x2a9d8f },
];
const SKIN = 0xf2c29b, PANTS = 0x3b4a6b;

// хеш клітинки для прикрас (детермінізм тут не обов'язковий, але сцена не має «блимати» при перебудові)
function hh(x, y, s) {
  let h = Math.imul(x | 0, 0x27D4EB2D) ^ Math.imul(y | 0, 0x165667B1) ^ Math.imul(s | 0, 0x9E3779B1);
  h = Math.imul(h ^ (h >>> 15), 0x85EBCA6B); h = Math.imul(h ^ (h >>> 13), 0xC2B2AE35);
  return ((h ^ (h >>> 16)) >>> 0) / 4294967296;
}
const pick = (arr, v) => arr[Math.min(arr.length - 1, Math.floor(v * arr.length))];

const _m = new THREE.Matrix4(), _q = new THREE.Quaternion(), _p = new THREE.Vector3(), _s = new THREE.Vector3();
const _c = new THREE.Color(), _UP = new THREE.Vector3(0, 1, 0);
const ZERO = new THREE.Matrix4().makeScale(0, 0, 0);
const BOX = new THREE.BoxGeometry(1, 1, 1);

function tint(hex, amt) { _c.setHex(hex); if (amt) _c.multiplyScalar(1 + amt); return _c; }

// ---------- пул кубиків на одному InstancedMesh (вільні слоти, ріст удвічі) ----------
class Pool {
  constructor(parent, mat, cap, { cast = true, receive = true } = {}) {
    this.parent = parent; this.mat = mat; this.cast = cast; this.receive = receive;
    this.free = []; this.n = 0; this.mesh = null; this.make(cap);
  }
  make(cap) {
    const m = new THREE.InstancedMesh(BOX, this.mat, cap);
    m.instanceMatrix.setUsage(THREE.DynamicDrawUsage);
    m.setColorAt(0, _c.setHex(0xffffff));
    m.castShadow = this.cast; m.receiveShadow = this.receive; m.frustumCulled = false;
    if (this.mesh) {
      m.instanceMatrix.array.set(this.mesh.instanceMatrix.array);
      m.instanceColor.array.set(this.mesh.instanceColor.array);
      this.parent.remove(this.mesh); this.mesh.dispose();
    }
    m.count = this.n; this.mesh = m; this.cap = cap; this.parent.add(m); this.dirty = true;
  }
  alloc() {
    if (this.free.length) return this.free.pop();
    if (this.n >= this.cap) this.make(this.cap * 2);
    const i = this.n++; this.mesh.count = this.n; return i;
  }
  release(i) { this.mesh.setMatrixAt(i, ZERO); this.free.push(i); this.dirty = true; }
  // кубик: (x, z) — центр основи, y0 — низ, розміри, колір (hex або Color), поворот довкола Y
  put(i, x, y0, z, sx, sy, sz, color, ry = 0) {
    _p.set(x, y0 + sy / 2, z); _s.set(sx, sy, sz); _q.setFromAxisAngle(_UP, ry);
    this.mesh.setMatrixAt(i, _m.compose(_p, _q, _s));
    this.mesh.setColorAt(i, typeof color === 'number' ? _c.setHex(color) : color);
    this.dirty = true; return i;
  }
  add(list, ...a) { const i = this.alloc(); this.put(i, ...a); list.push(i); return i; }
  flush() {
    if (!this.dirty) return;
    this.mesh.instanceMatrix.needsUpdate = true; this.mesh.instanceColor.needsUpdate = true; this.dirty = false;
  }
  dispose() { this.parent.remove(this.mesh); this.mesh.dispose(); }
}

// ---------- матеріали й прості моделі (звичайні Mesh для потяга, гравців, речей) ----------
const matCache = new Map();
function mat(hex, opts) {
  const key = hex + (opts ? JSON.stringify(opts) : '');
  let m = matCache.get(key);
  if (!m) { m = new THREE.MeshLambertMaterial({ color: hex, ...opts }); matCache.set(key, m); }
  return m;
}
// кубик-Mesh у групі: x,z — центр, y0 — низ
function cube(parent, x, y0, z, sx, sy, sz, hex, m) {
  const mesh = new THREE.Mesh(BOX, m || mat(hex));
  mesh.position.set(x, y0 + sy / 2, z); mesh.scale.set(sx, sy, sz);
  mesh.castShadow = true; mesh.receiveShadow = true;
  parent.add(mesh); return mesh;
}

// Річ (у руках чи на підлозі). n — скільки в стосі. Повертає Group, низ на Y = 0, «уперед» = +X.
function makeItem(k, n = 1, ground = false) {
  const g = new THREE.Group();
  n = Math.max(1, Math.min(n, 12));
  if (k === I.WOOD) {
    for (let j = 0; j < n; j++) {
      const layer = Math.floor(j / 2), side = j % 2;
      const cross = ground && layer % 2 === 1;
      const o = (side ? 0.08 : -0.08);
      const log = cube(g, cross ? o : 0, layer * 0.12, cross ? 0 : o, cross ? 0.13 : 0.44, 0.12, cross ? 0.44 : 0.13, j % 3 ? 0x9b6a3c : 0x8a5c33);
      log.rotation.y = 0;
      cube(g, cross ? o : 0.225, layer * 0.12 + 0.015, cross ? 0.225 : o, cross ? 0.1 : 0.02, 0.09, cross ? 0.02 : 0.1, 0xd9b27a);
    }
  } else if (k === I.IRON) {
    for (let j = 0; j < n; j++) {
      const layer = Math.floor(j / 2), side = j % 2;
      cube(g, 0, layer * 0.09, side ? 0.08 : -0.08, 0.32, 0.09, 0.14, j % 2 ? 0x5b6470 : 0x4b535e);
      cube(g, 0, layer * 0.09 + 0.09, side ? 0.08 : -0.08, 0.24, 0.012, 0.1, 0x8a96a6);
    }
  } else if (k === I.RAIL) {
    for (let j = 0; j < n; j++) {
      const y = j * 0.1;
      cube(g, -0.14, y, 0, 0.08, 0.05, 0.4, C.tie); cube(g, 0.14, y, 0, 0.08, 0.05, 0.4, C.tie);
      cube(g, 0, y + 0.05, -0.12, 0.5, 0.05, 0.05, C.steel); cube(g, 0, y + 0.05, 0.12, 0.5, 0.05, 0.05, C.steel);
    }
  } else if (k === I.AXE || k === I.PICK) {
    const t = new THREE.Group(); g.add(t);
    cube(t, 0, 0, 0, 0.05, 0.46, 0.05, 0x8a5e3a);
    if (k === I.AXE) { cube(t, 0.06, 0.32, 0, 0.12, 0.14, 0.04, 0xc9ced6); cube(t, 0.125, 0.3, 0, 0.02, 0.18, 0.045, 0xeef2f6); }
    else { cube(t, 0, 0.4, 0, 0.38, 0.06, 0.06, 0x6c737c); cube(t, 0.2, 0.38, 0, 0.05, 0.07, 0.05, 0xb8bec6); cube(t, -0.2, 0.38, 0, 0.05, 0.07, 0.05, 0xb8bec6); }
    if (ground) { t.rotation.x = Math.PI / 2; t.position.set(0, 0.04, -0.2); }
  } else if (k === I.BUCKET || k === I.BUCKET_FULL) {
    cube(g, 0, 0, 0, 0.22, 0.2, 0.22, 0x9aa6b4); cube(g, 0, 0.13, 0, 0.235, 0.03, 0.235, 0x6f7a88);
    cube(g, 0, 0.2, 0, 0.03, 0.08, 0.2, 0x55606c);
    if (k === I.BUCKET_FULL) cube(g, 0, 0.17, 0, 0.18, 0.025, 0.18, 0x3aa0e8, mat(0x3aa0e8, { emissive: 0x0a3050 }));
  }
  return g;
}

// ---------- гравець із кубиків ----------
function makePlayer(n) {
  const pc = PLAYER[n % PLAYER.length];
  const root = new THREE.Group(), body = new THREE.Group(); root.add(body);
  const leg = (z) => { const p = new THREE.Group(); p.position.set(0, 0.24, z); body.add(p); cube(p, 0, -0.24, 0, 0.13, 0.24, 0.12, PANTS); cube(p, 0.02, -0.24, 0, 0.16, 0.06, 0.13, 0x3a2a20); return p; };
  const legL = leg(-0.08), legR = leg(0.08);
  cube(body, 0, 0.22, 0, 0.26, 0.3, 0.34, pc.shirt);
  cube(body, 0, 0.22, 0, 0.27, 0.05, 0.35, 0x5a3b28);                // ремінь
  const head = new THREE.Group(); head.position.set(0, 0.52, 0); body.add(head);
  cube(head, 0, 0, 0, 0.28, 0.27, 0.28, SKIN);
  cube(head, 0.141, 0.12, -0.065, 0.01, 0.05, 0.04, 0x222222); cube(head, 0.141, 0.12, 0.065, 0.01, 0.05, 0.04, 0x222222);
  cube(head, 0.141, 0.05, 0, 0.012, 0.025, 0.08, 0xd88a6a);
  cube(head, 0, 0.25, 0, 0.3, 0.07, 0.3, pc.hat); cube(head, 0.1, 0.25, 0, 0.18, 0.035, 0.3, pc.hat);
  const arm = (z) => { const p = new THREE.Group(); p.position.set(0, 0.48, z); body.add(p); cube(p, 0, -0.26, 0, 0.1, 0.26, 0.1, pc.shirt); cube(p, 0, -0.3, 0, 0.09, 0.05, 0.09, SKIN); return p; };
  const armL = arm(-0.215), armR = arm(0.215);
  const hand = new THREE.Group(); hand.position.set(0, -0.3, 0); armR.add(hand);
  const top = new THREE.Group(); top.position.set(0.02, 0.86, 0); body.add(top);
  // тінь-пляма під ногами не потрібна — є справжні тіні
  root.scale.setScalar(1.35);
  return { root, body, legL, legR, armL, armR, hand, top, holdKey: '', held: null, phase: 0, swing: 0, yaw: 0, x: 0, z: 0 };
}

// ---------- паротяг і вагони ----------
function makeLoco() {
  const g = new THREE.Group();
  const red = 0xd23c32, black = 0x2a2d34, gold = 0xe8b84a;
  const boilerMat = new THREE.MeshLambertMaterial({ color: red });
  cube(g, 0, 0.12, 0, 0.94, 0.1, 0.6, black);
  for (const x of [-0.28, 0.02, 0.3]) for (const z of [-0.3, 0.3]) {
    const wh = cube(g, x, 0.02, z, 0.22, 0.22, 0.06, 0x3a3d44); cube(g, x, 0.1, z * 1.08, 0.08, 0.08, 0.02, 0xc0453a);
    wh.userData.wheel = true;
  }
  const boiler = cube(g, 0.12, 0.22, 0, 0.58, 0.36, 0.44, red, boilerMat);
  cube(g, 0.12, 0.22, 0, 0.6, 0.05, 0.46, gold); cube(g, 0.12, 0.5, 0, 0.6, 0.04, 0.46, gold);
  cube(g, -0.02, 0.22, 0, 0.04, 0.37, 0.46, gold); cube(g, 0.26, 0.22, 0, 0.04, 0.37, 0.46, gold);
  cube(g, 0.43, 0.2, 0, 0.06, 0.4, 0.42, black);                          // димова коробка
  cube(g, 0.465, 0.38, 0, 0.03, 0.08, 0.1, 0xfff1a8, mat(0xfff1a8, { emissive: 0xffd76a }));   // ліхтар
  const chimney = cube(g, 0.32, 0.58, 0, 0.13, 0.22, 0.13, black);
  cube(g, 0.32, 0.78, 0, 0.18, 0.05, 0.18, 0x3a3d44);
  cube(g, 0.1, 0.58, 0, 0.1, 0.08, 0.1, gold);                            // дзвін/купол
  cube(g, 0.52, 0.04, 0, 0.1, 0.12, 0.5, 0x5a5f68);                       // скотовідбійник
  // кабіна
  cube(g, -0.3, 0.22, 0, 0.34, 0.5, 0.56, red, boilerMat);
  cube(g, -0.3, 0.72, 0, 0.42, 0.06, 0.64, black);
  cube(g, -0.3, 0.45, -0.281, 0.18, 0.16, 0.01, 0x9fd3f0); cube(g, -0.3, 0.45, 0.281, 0.18, 0.16, 0.01, 0x9fd3f0);
  cube(g, -0.125, 0.48, 0, 0.012, 0.14, 0.32, 0x9fd3f0);
  return { g, boilerMat, chimney, boiler, top: new THREE.Vector3(0.32, 0.85, 0) };
}
function makeStore() {
  const g = new THREE.Group();
  cube(g, 0, 0.12, 0, 0.9, 0.08, 0.6, 0x2a2d34);
  for (const x of [-0.28, 0.28]) for (const z of [-0.3, 0.3]) cube(g, x, 0.02, z, 0.2, 0.2, 0.06, 0x3a3d44);
  cube(g, 0, 0.2, 0, 0.86, 0.06, 0.58, 0x8a5a34);
  cube(g, 0, 0.26, -0.28, 0.86, 0.2, 0.04, 0x9c6a3e); cube(g, 0, 0.26, 0.28, 0.86, 0.2, 0.04, 0x9c6a3e);
  cube(g, -0.42, 0.26, 0, 0.04, 0.2, 0.56, 0x9c6a3e); cube(g, 0.42, 0.26, 0, 0.04, 0.2, 0.56, 0x9c6a3e);
  cube(g, 0, 0.26, 0, 0.02, 0.16, 0.52, 0x7a4e2c);
  const wood = new THREE.Group(); wood.position.set(-0.2, 0.26, 0); g.add(wood);
  const iron = new THREE.Group(); iron.position.set(0.2, 0.26, 0); g.add(iron);
  return { g, wood, iron, nW: -1, nI: -1 };
}
function makeCraft() {
  const g = new THREE.Group();
  cube(g, 0, 0.12, 0, 0.9, 0.08, 0.6, 0x2a2d34);
  for (const x of [-0.28, 0.28]) for (const z of [-0.3, 0.3]) cube(g, x, 0.02, z, 0.2, 0.2, 0.06, 0x3a3d44);
  cube(g, 0.12, 0.2, 0, 0.62, 0.42, 0.56, 0x3d7cc9);
  cube(g, 0.12, 0.62, 0, 0.7, 0.06, 0.64, 0x2b4f80);
  cube(g, 0.12, 0.36, -0.281, 0.4, 0.12, 0.01, 0xffe9a8, mat(0xffe9a8, { emissive: 0x6a4a10 }));
  cube(g, 0.12, 0.36, 0.281, 0.4, 0.12, 0.01, 0xffe9a8, mat(0xffe9a8, { emissive: 0x6a4a10 }));
  cube(g, 0.3, 0.68, 0.15, 0.08, 0.16, 0.08, 0x2a2d34);                    // труба горна
  cube(g, -0.3, 0.2, 0, 0.26, 0.06, 0.56, 0x7a5232);                      // відкритий задок під рейки
  const rails = new THREE.Group(); rails.position.set(-0.3, 0.26, 0); g.add(rails);
  return { g, rails, nR: -1, stack: new THREE.Vector3(0.3, 0.84, 0.15) };
}

// =====================================================================================
export function createRenderer(canvas) {
  const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, powerPreference: 'high-performance' });
  renderer.setPixelRatio(Math.min(2, window.devicePixelRatio || 1));
  renderer.shadowMap.enabled = true;
  renderer.shadowMap.type = THREE.PCFShadowMap;
  renderer.outputColorSpace = THREE.SRGBColorSpace;

  const scene = new THREE.Scene();
  scene.background = new THREE.Color(C.sky);
  scene.fog = new THREE.Fog(C.sky, 60, 120);
  const camera = new THREE.PerspectiveCamera(32, 1, 1, 400);

  const hemi = new THREE.HemisphereLight(0xd8ecff, 0x6f7f48, 1.15); scene.add(hemi);
  const sun = new THREE.DirectionalLight(0xffeccc, 3.0);
  sun.castShadow = true;
  sun.shadow.mapSize.set(2048, 2048);
  sun.shadow.bias = -0.0004; sun.shadow.normalBias = 0.025;
  scene.add(sun); scene.add(sun.target);
  const SUN_DIR = new THREE.Vector3(-0.45, 1, 0.55).normalize();
  const fireLight = new THREE.PointLight(0xff7a2a, 0, 6, 1.5); scene.add(fireLight);

  const waterTime = { value: 0 };
  const waterMat = new THREE.MeshPhongMaterial({ color: C.water, transparent: true, opacity: 0.8, flatShading: true, shininess: 90, specular: 0x9fd8ff });
  waterMat.onBeforeCompile = (sh) => {
    sh.uniforms.uTime = waterTime;
    sh.vertexShader = 'uniform float uTime;\n' + sh.vertexShader.replace('#include <begin_vertex>',
      `#include <begin_vertex>
      transformed.z += 0.035 * sin(transformed.x * 1.9 + uTime * 1.7) + 0.03 * sin(transformed.y * 2.7 - uTime * 1.3)
                     + 0.015 * sin((transformed.x + transformed.y) * 4.1 + uTime * 2.6);`);
  };
  const boxMat = new THREE.MeshLambertMaterial({ color: 0xffffff });

  // ---- частинки ----
  const PMAX = 900;
  const pMesh = new THREE.InstancedMesh(BOX, new THREE.MeshLambertMaterial({ color: 0xffffff }), PMAX);
  pMesh.instanceMatrix.setUsage(THREE.DynamicDrawUsage); pMesh.frustumCulled = false;
  pMesh.setColorAt(0, _c.setHex(0xffffff));
  for (let i = 0; i < PMAX; i++) pMesh.setMatrixAt(i, ZERO);
  scene.add(pMesh);
  const P = { x: new Float32Array(PMAX), y: new Float32Array(PMAX), z: new Float32Array(PMAX), vx: new Float32Array(PMAX), vy: new Float32Array(PMAX), vz: new Float32Array(PMAX),
    life: new Float32Array(PMAX), max: new Float32Array(PMAX), size: new Float32Array(PMAX), grav: new Float32Array(PMAX), kind: new Uint8Array(PMAX), rot: new Float32Array(PMAX), next: 0, live: 0 };
  function spawn(x, y, z, vx, vy, vz, life, size, hex, grav = 9, kind = 0) {
    const i = P.next; P.next = (P.next + 1) % PMAX;
    P.x[i] = x; P.y[i] = y; P.z[i] = z; P.vx[i] = vx; P.vy[i] = vy; P.vz[i] = vz;
    P.life[i] = P.max[i] = life; P.size[i] = size; P.grav[i] = grav; P.kind[i] = kind; P.rot[i] = Math.random() * 3;
    pMesh.setColorAt(i, _c.setHex(hex)); pMesh.instanceColor.needsUpdate = true;
  }
  const R = (a) => (Math.random() - 0.5) * 2 * a;
  function burst(x, y, z, n, cols, sp = 2.2, up = 3, size = 0.09, life = 0.7, grav = 9) {
    for (let k = 0; k < n; k++) spawn(x + R(0.2), y + R(0.1), z + R(0.2), R(sp), up * (0.5 + Math.random()), R(sp), life * (0.6 + Math.random() * 0.6), size * (0.6 + Math.random() * 0.8), pick(cols, Math.random()), grav);
  }
  function puff(x, y, z, n, hex = 0xf2f2f2, size = 0.22, life = 1.6, rise = 0.9) {
    for (let k = 0; k < n; k++) spawn(x + R(0.08), y, z + R(0.08), R(0.25), rise * (0.7 + Math.random() * 0.6), R(0.25), life * (0.7 + Math.random() * 0.5), size * (0.7 + Math.random() * 0.6), hex, -0.2, 1);
  }
  function stepParticles(dt) {
    let any = false;
    for (let i = 0; i < PMAX; i++) {
      if (P.life[i] <= 0) continue;
      any = true;
      P.life[i] -= dt;
      if (P.life[i] <= 0) { pMesh.setMatrixAt(i, ZERO); continue; }
      P.vy[i] -= P.grav[i] * dt;
      if (P.kind[i] === 1) { P.vx[i] *= 1 - dt * 0.8; P.vz[i] *= 1 - dt * 0.8; P.vx[i] += wind * dt; }
      P.x[i] += P.vx[i] * dt; P.y[i] += P.vy[i] * dt; P.z[i] += P.vz[i] * dt;
      if (P.kind[i] === 0 && P.y[i] < 0.03) { P.y[i] = 0.03; P.vy[i] *= -0.3; P.vx[i] *= 0.5; P.vz[i] *= 0.5; }
      const f = P.life[i] / P.max[i];
      let sc = P.size[i];
      if (P.kind[i] === 1) sc *= (0.5 + (1 - f) * 1.4) * Math.min(1, f * 3);
      else sc *= Math.min(1, f * 3);
      P.rot[i] += dt * 2;
      _p.set(P.x[i], P.y[i], P.z[i]); _s.set(sc, sc, sc); _q.setFromAxisAngle(_UP, P.rot[i]);
      pMesh.setMatrixAt(i, _m.compose(_p, _q, _s));
    }
    if (any || P.live) pMesh.instanceMatrix.needsUpdate = true;
    P.live = any;
  }
  let wind = 0.25;

  // ---- стан сцени світу ----
  let W = null;   // { root, s0, pools, refs..., copies }

  function disposeWorld() {
    if (!W) return;
    for (const p of [W.pGround, W.pBox]) p.dispose();
    for (const g of W.drops) if (g) W.root.remove(g);
    W.water.geometry.dispose();
    scene.remove(W.root);
    W = null;
  }

  function setWorld(s) {
    disposeWorld();
    const { w, h } = s, n = w * h;
    const root = new THREE.Group(); scene.add(root);
    const pGround = new Pool(root, boxMat, n, { cast: false, receive: true });
    const pBox = new Pool(root, boxMat, Math.max(2048, n * 3));
    W = { root, s, w, h, n, pGround, pBox,
      obj: new Uint8Array(n), hp: new Uint8Array(n), ground: new Uint8Array(n), drop: new Uint8Array(n), dropN: new Uint8Array(n),
      onPath: new Uint8Array(n), objRefs: new Array(n), decoRefs: new Array(n), railRefs: new Array(n), railSig: new Array(n).fill(''), drops: new Array(n),
      pathLen: -1, pathLast: -1, treeMax: Math.max(1, s.params?.treeHits | 0), rockMax: Math.max(1, s.params?.rockHits | 0) };
    for (let i = 0; i < n; i++) { W.objRefs[i] = []; W.decoRefs[i] = []; W.railRefs[i] = []; }
    pGround.n = n; pGround.mesh.count = n;

    // земляна плита під смугою (діорама) і берег за кінцем
    cube(root, w / 2, -3.2, h / 2, w + 0.02, 2.7, h + 0.02, C.dirt).castShadow = false;
    cube(root, w / 2, -0.52, h / 2, w, 0.02, h, C.bed).castShadow = false;
    const strata = cube(root, w / 2, -1.6, h / 2, w + 0.06, 0.12, h + 0.06, C.dirtDark); strata.castShadow = false;
    // вода — одна площина з хвилями; трава її перекриває, видно лише в западинах
    const water = new THREE.Mesh(new THREE.PlaneGeometry(w, h, w * 2, h * 2), waterMat);
    water.rotation.x = -Math.PI / 2; water.position.set(w / 2, -0.16, h / 2); water.receiveShadow = true;
    root.add(water); W.water = water;
    buildStation(root, s);
    buildOutskirts(-16, 0, s); buildOutskirts(w, w + 22, s);
    for (let i = 0; i < n; i++) syncCell(i, true);
    syncPath(true);
    pGround.flush(); pBox.flush();

    // потяг і гравці
    const loco = makeLoco(), store = makeStore(), craft = makeCraft();
    for (const m of [loco, store, craft]) { m.g.scale.set(1, 1.2, 1.2); root.add(m.g); }
    W.train = [loco, store, craft];
    W.players = [];
    syncPlayers(s);
    W.snap = null; snapshot(s); W.prev = W.cur;
    cam.init = false;
  }

  function buildStation(root, s) {
    const { w, h } = s, mid = h >> 1;
    const g = new THREE.Group(); g.position.set(w, 0, mid + 0.5); root.add(g);
    // будиночок вокзалу
    cube(g, 1.6, 0, -2.6, 2.4, 0.14, 3.6, C.stationB);
    cube(g, 1.7, 0.14, -3.1, 1.8, 1.2, 2.2, 0xf3e3c3);
    cube(g, 1.7, 1.34, -3.1, 2.1, 0.12, 2.5, 0xb8433a);
    cube(g, 1.7, 1.46, -3.1, 1.7, 0.16, 2.1, 0xc9503f);
    cube(g, 1.7, 1.62, -3.1, 1.2, 0.16, 1.6, 0xd85e48);
    cube(g, 1.7, 1.78, -3.1, 0.6, 0.12, 1.0, 0xe36c52);
    cube(g, 0.79, 0.14, -2.6, 0.02, 0.7, 0.4, 0x7a4e2c);
    for (const z of [-3.6, -1.95]) cube(g, 0.79, 0.5, z, 0.02, 0.36, 0.32, 0x9fd3f0, mat(0x9fd3f0, { emissive: 0x203040 }));
    cube(g, 2.3, 1.5, -2.4, 0.22, 0.6, 0.22, 0x8a8f96);
    cube(g, 0.79, 1.0, -3.1, 0.03, 0.22, 0.9, 0x2b4f80);                     // табличка
    // прапор
    cube(g, 3.4, 0, 1.6, 0.08, 2.4, 0.08, 0xdddddd);
    cube(g, 3.4, 2.05, 1.95, 0.03, 0.18, 0.6, 0x1f6fd1); cube(g, 3.4, 1.87, 1.95, 0.03, 0.18, 0.6, 0xf7d23e);
    // водокачка
    cube(g, 3.2, 0, -1.4, 0.12, 1.3, 0.12, 0x6e4a2e); cube(g, 3.8, 0, -1.4, 0.12, 1.3, 0.12, 0x6e4a2e);
    cube(g, 3.5, 1.3, -1.4, 0.9, 0.7, 0.9, 0x8a5a34); cube(g, 3.5, 2.0, -1.4, 1.0, 0.1, 1.0, 0x5a3b28);
    // упор у кінці колії
    cube(g, -0.12, 0, 0, 0.16, 0.34, 0.7, 0xc0453a); cube(g, -0.12, 0.34, 0, 0.18, 0.06, 0.74, 0xf2f2f2);
    // дерева довкола
    for (const [x, z] of [[3.6, 3.5], [1.2, 4], [4, -3.8], [0.6, -5.2]]) {
      if (Math.abs(z) > h * 0.3) continue;
      cube(g, x, 0, z, 0.22, 0.5, 0.22, C.trunk); cube(g, x, 0.45, z, 0.8, 0.6, 0.8, C.leaf[1]); cube(g, x, 1.0, z, 0.5, 0.35, 0.5, C.leaf[3]);
    }
  }

  // околиці за кінцями смуги (лише вигляд): поле, ліс, скелі; на старті — тунель, з якого виходить колія
  function buildOutskirts(x0, x1, s) {
    const { w, h } = s, mid = h >> 1, list = W.outRefs || (W.outRefs = []), P = W.pBox;
    const start = x1 <= 0;
    for (let x = x0; x < x1; x++) for (let y = 0; y < h; y++) {
      const v = hh(x, y, 1), d = start ? -x : x - w + 1;           // відстань від смуги
      const top = 0.015 + Math.floor(hh(x, y, 3) * 3) * 0.012;
      const dark = hh(x >> 2, y >> 2, 9) * 0.5 + hh(x >> 1, y >> 1, 10) * 0.5;
      P.add(list, x + 0.5, -0.5, y + 0.5, 1, 0.5 + top, 1, tint((x + y) & 1 ? C.grassA : C.grassB, (v - 0.5) * 0.07 - (dark > 0.7 ? 0.08 : 0) - Math.min(0.12, d * 0.006)));
      if (y === 0 || y === h - 1) { objBoxes(list, O.WALL, 1, x, y, top, h); continue; }
      if (start && x >= -3) continue;                              // портал тунелю
      if (start && Math.abs(y - mid) <= 1) continue;
      if (!start && d < 7 && Math.abs(y - mid) <= 4) continue;     // вокзал і подвір'я
      const r = hh(x, y, 5), dens = Math.min(0.55, 0.12 + d * 0.03);
      if (r < dens) objBoxes(list, O.TREE, 1, x, y, top, h);
      else if (r < dens + 0.06) objBoxes(list, O.ROCK, 1, x, y, top, h);
      else if (r > 0.93) { P.add(list, x + 0.5, top, y + 0.5, 0.05, 0.12, 0.05, C.tuft); P.add(list, x + 0.5, top + 0.12, y + 0.5, 0.09, 0.07, 0.09, pick(C.flower, hh(x, y, 6))); }
    }
    cube(W.root, (x0 + x1) / 2, -3.2, h / 2, x1 - x0, 2.7, h + 0.02, C.dirt).castShadow = false;
    if (!start) return;
    // колія з тунелю: прямі рейки по середньому рядку
    for (let x = x0; x < 0; x++) {
      const cx = x + 0.5, cz = mid + 0.5;
      for (const o of [-0.33, 0, 0.33]) P.add(list, cx + o, 0.015, cz, 0.15, 0.06, 0.82, C.tie);
      for (const o of [-0.2, 0.2]) P.add(list, cx, 0.075, cz + o, 1.0, 0.08, 0.07, C.steel);
    }
    // гора з порталом: кам'яна стіна поперек смуги, темний отвір над колією, трава й ялинки згори
    const gx = -3.5;
    for (let y = 1; y < h - 1; y++) {
      const v = hh(y, 7, 70), hg = 2.2 + v * 1.4 + (Math.abs(y - mid) > 3 ? 0.6 : 0);
      if (Math.abs(y - mid) <= 1) {
        P.add(list, gx, 1.25, y + 0.5, 1.4, hg - 1.25, 1.0, pick([C.wallA, C.wallB], v));
        if (y === mid) P.add(list, gx + 0.05, 0, y + 0.5, 1.32, 1.25, 1.02, 0x1e1f24);
        else P.add(list, gx + 0.1, 0, y + 0.5, 1.4, 1.25, 0.6 + 0.0, 0x6b7078);
      } else P.add(list, gx, 0, y + 0.5, 1.4, hg, 1.0, pick([C.wallA, C.wallB, C.wallC], v));
      P.add(list, gx - 0.3, hg, y + 0.5, 1.0, 0.1, 1.02, tint(C.cap, (v - 0.5) * 0.15));
      for (let k = 1; k < 4; k++) P.add(list, gx - 0.7 - k, 0, y + 0.5, 1.0, hg + k * 0.5 + hh(k, y, 71) * 0.6, 1.0, pick([C.wallA, C.wallB, C.wallC], hh(k, y, 72)));
      if (v > 0.6) objBoxes(list, O.TREE, 1, Math.floor(gx - 1.2), y, hg + 0.5, h);
    }
    P.add(list, gx + 0.72, 1.18, mid + 0.5, 0.08, 0.14, 1.2, 0xb8a27a);    // замковий камінь арки
  }

  // верх підлоги клітинки (на чому стоять речі)
  function topOf(gk, x, y) {
    if (gk === G.WATER) return -0.45;
    if (gk === G.BRIDGE) return 0.0;
    if (gk === G.STATION) return 0.07;
    return 0.015 + Math.floor(hh(x, y, 3) * 3) * 0.012;
  }

  // ---- одна клітинка: підлога, прикраси, предмет, речі ----
  function syncCell(i, all) {
    const s = W.s, x = i % W.w, y = (i / W.w) | 0;
    const gk = s.ground[i], ok = s.obj[i], hp = s.hp[i], dk = s.drop[i], dn = s.dropN[i];
    const gChanged = all || gk !== W.ground[i];
    const oChanged = all || ok !== W.obj[i] || hp !== W.hp[i];
    const dChanged = all || dk !== W.drop[i] || dn !== W.dropN[i];
    if (!gChanged && !oChanged && !dChanged) return;
    W.ground[i] = gk; W.obj[i] = ok; W.hp[i] = hp; W.drop[i] = dk; W.dropN[i] = dn;
    const cx = x + 0.5, cz = y + 0.5, v = hh(x, y, 1);
    if (gChanged) {
      const top = topOf(gk, x, y);
      if (gk === G.WATER || gk === G.BRIDGE) W.pGround.put(i, cx, -0.5, cz, 1, 0.05, 1, tint(C.bed, (v - 0.5) * 0.15));
      else {
        let col;
        if (gk === G.SAND) col = tint((x + y) & 1 ? C.sand : C.sandB, (v - 0.5) * 0.06);
        else if (gk === G.STATION) col = tint((x + y) & 1 ? C.station : C.stationB, (v - 0.5) * 0.06);
        else if (gk !== G.GRASS) col = tint(0x7d8a62, (v - 0.5) * 0.08);   // невідомий тип підлоги — нейтральний
        else {
          const dark = hh(x >> 2, y >> 2, 9) * 0.5 + hh(x >> 1, y >> 1, 10) * 0.5;
          col = tint((x + y) & 1 ? C.grassA : C.grassB, (v - 0.5) * 0.07 - (dark > 0.7 ? 0.08 : 0));
        }
        W.pGround.put(i, cx, -0.5, cz, 1, 0.5 + top, 1, col);
      }
    }
    if (oChanged) buildObj(i, x, y);
    if (gChanged || oChanged || dChanged) buildDeco(i, x, y);
    if (dChanged) buildDrop(i, x, y);
  }

  function freeRefs(list, pool) { for (const k of list) pool.release(k); list.length = 0; }

  function buildObj(i, x, y) {
    const s = W.s, list = W.objRefs[i];
    freeRefs(list, W.pBox);
    const ok = s.obj[i];
    const f = ok === O.TREE ? s.hp[i] / W.treeMax : ok === O.ROCK ? s.hp[i] / W.rockMax : 1;
    objBoxes(list, ok, Math.min(1, f), x, y, topOf(s.ground[i], x, y), s.h);
  }
  // кубики предмета клітинки (дерево/скеля/стіна); f — частка міцності (менше — менший), також для околиць за смугою
  function objBoxes(list, ok, f, x, y, base, h) {
    const P = W.pBox, cx = x + 0.5, cz = y + 0.5;
    const v1 = hh(x, y, 11), v2 = hh(x, y, 12), v3 = hh(x, y, 13), v4 = hh(x, y, 14);
    if (ok === O.TREE) {
      const k = 0.55 + 0.45 * f;
      const ox = cx + (v2 - 0.5) * 0.16, oz = cz + (v3 - 0.5) * 0.16, hs = (0.85 + v4 * 0.4) * k;
      P.add(list, ox, base, oz, 0.2 * k, 0.4 * hs, 0.2 * k, v1 > 0.5 ? C.trunk : C.trunkB);
      if (v1 < 0.42) {          // ялинка ярусами
        const col = pick(C.pine, v2);
        let y0 = base + 0.28 * hs;
        for (const [sz, th] of [[0.86, 0.34], [0.66, 0.32], [0.44, 0.28], [0.2, 0.22]]) {
          P.add(list, ox, y0, oz, sz * k, th * hs, sz * k, tint(col, (th - 0.3) * 0.6)); y0 += th * hs * 0.92;
        }
      } else {                  // листяне — куб крони + менший зверху + бічний
        const col = pick(C.leaf, v2);
        P.add(list, ox, base + 0.36 * hs, oz, 0.82 * k, 0.56 * hs, 0.82 * k, col);
        P.add(list, ox + (v3 - 0.5) * 0.15, base + 0.88 * hs, oz + (v4 - 0.5) * 0.15, 0.52 * k, 0.32 * hs, 0.52 * k, tint(col, 0.08));
        if (v4 > 0.45) P.add(list, ox + (v1 - 0.5) * 0.5, base + 0.5 * hs, oz + 0.3 * (v2 > 0.5 ? 1 : -1), 0.34 * k, 0.3 * hs, 0.34 * k, tint(col, -0.06));
        if (v3 > 0.7) P.add(list, ox + 0.3 * k, base + 0.7 * hs, oz - 0.1, 0.12, 0.1, 0.12, 0xe0453a);   // яблучко
      }
    } else if (ok === O.ROCK) {
      // валун із 2–3 брил, на гранях — руда (руда, іржава) і темні вкраплення
      const k = 0.6 + 0.4 * f;
      const col = pick(C.rock, v1), hgt = (0.5 + v2 * 0.35) * k;
      const sx = v3 > 0.5 ? 1 : -1, sz = v4 > 0.5 ? 1 : -1;
      P.add(list, cx - 0.1 * sx * k, base, cz - 0.08 * sz * k, 0.74 * k, hgt, 0.72 * k, col);
      P.add(list, cx + 0.16 * sx * k, base, cz + 0.14 * sz * k, 0.6 * k, hgt * (0.6 + v1 * 0.3), 0.62 * k, tint(col, -0.07));
      P.add(list, cx - 0.06 * sx * k, base + hgt, cz - 0.04 * sz * k, 0.46 * k, (0.14 + v4 * 0.16) * k, 0.44 * k, tint(col, 0.07));
      if (v2 > 0.6) P.add(list, cx + 0.34 * sx * k, base, cz - 0.3 * sz * k, 0.22 * k, 0.16 * k, 0.22 * k, tint(col, -0.12));
      const nOre = 2 + Math.floor(v3 * 3);
      for (let j = 0; j < nOre; j++) {
        const a = hh(x, y, 20 + j), b = hh(x, y, 30 + j), side = Math.floor(hh(x, y, 40 + j) * 5);
        const oc = j === 3 ? C.ore[2] : (a < 0.6 ? C.ore[0] : C.ore[1]);
        const hx = side === 0 ? 0.37 : side === 1 ? -0.37 : (a - 0.5) * 0.5, hz = side === 2 ? 0.36 : side === 3 ? -0.36 : (b - 0.5) * 0.5;
        const hy = side === 4 ? hgt + 0.01 : (0.15 + b * 0.6) * hgt;
        P.add(list, cx - 0.1 * sx * k + hx * k, base + hy - 0.06, cz - 0.08 * sz * k + hz * k, 0.15 + a * 0.06, 0.12, 0.15 + b * 0.06, tint(oc, (a - 0.5) * 0.2));
      }
    } else if (ok === O.WALL) {
      // далекий край — скелястий хребет (вищий, з травою й ялинками), ближній — низькі камені, щоб не застували поле
      const far = y <= 0;
      const hgt = far ? 1.0 + v1 * 1.4 : 0.3 + v1 * 0.35;
      const col = pick([C.wallA, C.wallB, C.wallC], v2);
      P.add(list, cx, base, cz, 1.0, hgt, 1.0, col);
      if (far) {
        const h2 = 0.3 + v4 * 0.9;
        P.add(list, cx + (v4 - 0.5) * 0.3, base + hgt, cz - 0.15, 0.7, h2, 0.6, tint(col, 0.06));
        if (v3 > 0.5) P.add(list, cx + (v4 - 0.5) * 0.3, base + hgt + h2, cz - 0.15, 0.72, 0.08, 0.62, tint(C.cap, (v2 - 0.5) * 0.15));
        else P.add(list, cx, base + hgt, cz + 0.2, 1.0, 0.08, 0.5, tint(C.cap, (v2 - 0.5) * 0.15));
        if (v3 > 0.82) {
          const py = base + hgt + (v3 > 0.5 ? h2 + 0.08 : 0);
          P.add(list, cx, py, cz - 0.1, 0.12, 0.2, 0.12, C.trunk);
          P.add(list, cx, py + 0.18, cz - 0.1, 0.55, 0.3, 0.55, C.pine[0]); P.add(list, cx, py + 0.45, cz - 0.1, 0.32, 0.28, 0.32, C.pine[1]);
        }
      } else {
        if (v4 > 0.5) P.add(list, cx, base + hgt, cz, 1.02, 0.07, 1.02, tint(C.cap, (v2 - 0.5) * 0.1));
        if (v3 > 0.6) P.add(list, cx + (v4 - 0.5) * 0.4, base + hgt, cz, 0.4, 0.2, 0.4, tint(col, -0.05));
      }
    }
  }

  function buildDeco(i, x, y) {
    const s = W.s, list = W.decoRefs[i], P = W.pBox;
    freeRefs(list, P);
    const gk = s.ground[i], cx = x + 0.5, cz = y + 0.5;
    if (gk === G.BRIDGE) {
      for (let j = 0; j < 4; j++) P.add(list, x + 0.125 + j * 0.25, -0.1, cz, 0.22, 0.1, 1.04, pick(C.plank, hh(x, y, 50 + j)));
      P.add(list, cx, -0.14, y + 0.08, 1.0, 0.06, 0.1, 0x6e4a2e); P.add(list, cx, -0.14, y + 0.92, 1.0, 0.06, 0.1, 0x6e4a2e);
      return;
    }
    if (s.obj[i] !== O.NONE || s.drop[i] !== I.NONE || W.onPath[i]) return;
    const v = hh(x, y, 60), top = topOf(gk, x, y);
    if (gk === G.GRASS) {
      if (v < 0.07) {
        const col = pick(C.flower, hh(x, y, 61));
        for (let j = 0; j < 3; j++) {
          const fx = cx + (hh(x, y, 62 + j) - 0.5) * 0.7, fz = cz + (hh(x, y, 65 + j) - 0.5) * 0.7;
          P.add(list, fx, top, fz, 0.03, 0.1, 0.03, C.tuft); P.add(list, fx, top + 0.1, fz, 0.08, 0.06, 0.08, col);
        }
      } else if (v < 0.25) {
        for (let j = 0; j < 3; j++) {
          const fx = cx + (hh(x, y, 70 + j) - 0.5) * 0.7, fz = cz + (hh(x, y, 73 + j) - 0.5) * 0.7;
          P.add(list, fx, top, fz, 0.05, 0.08 + hh(x, y, 76 + j) * 0.1, 0.05, tint(C.tuft, (j - 1) * 0.08));
        }
      }
    } else if (gk === G.SAND && v < 0.15) {
      P.add(list, cx + (hh(x, y, 80) - 0.5) * 0.6, top, cz + (hh(x, y, 81) - 0.5) * 0.6, 0.12, 0.06, 0.1, C.pebble);
    } else if (gk === G.STATION && (x + y) % 3 === 0 && v < 0.5) {
      P.add(list, cx, top, cz, 0.96, 0.01, 0.04, 0xb0a690);
    }
  }

  function buildDrop(i, x, y) {
    const s = W.s;
    if (W.drops[i]) { W.root.remove(W.drops[i]); W.drops[i] = null; }
    const k = s.drop[i]; if (!k) return;
    const g = makeItem(k, s.dropN[i] || 1, true);
    g.scale.setScalar(1.25);
    g.position.set(x + 0.5, topOf(s.ground[i], x, y), y + 0.5);
    g.rotation.y = (hh(x, y, 90) - 0.5) * 0.5 + (k === I.RAIL ? 0 : 0);
    W.root.add(g); W.drops[i] = g;
  }

  // ---- колія ----
  const DIRS = [[1, 0], [0, 1], [-1, 0], [0, -1]];
  function dirTo(a, b, w) {
    const dx = (b % w) - (a % w), dy = ((b / w) | 0) - ((a / w) | 0);
    for (let k = 0; k < 4; k++) if (DIRS[k][0] === dx && DIRS[k][1] === dy) return k;
    return -1;
  }
  function syncPath(force) {
    const s = W.s, path = s.path, w = W.w;
    const last = path.length ? path[path.length - 1] : -1;
    if (!force && path.length === W.pathLen && last === W.pathLast) return;
    W.pathLen = path.length; W.pathLast = last;
    const sig = new Map();
    for (let k = 0; k < path.length; k++) {
      const a = k > 0 ? dirTo(path[k], path[k - 1], w) : -1, b = k < path.length - 1 ? dirTo(path[k], path[k + 1], w) : -1;
      sig.set(path[k], a + ',' + b + ',' + s.ground[path[k]]);
    }
    for (let i = 0; i < W.n; i++) {
      const want = sig.get(i) || '';
      const was = W.onPath[i];
      W.onPath[i] = want ? 1 : 0;
      if (want === W.railSig[i]) continue;
      W.railSig[i] = want;
      buildRail(i, want);
      if (was !== W.onPath[i]) buildDeco(i, i % w, (i / w) | 0);
    }
  }
  function buildRail(i, sig) {
    const list = W.railRefs[i], P = W.pBox;
    freeRefs(list, P);
    if (!sig) return;
    const [a, b] = sig.split(',').map(Number);
    const x = i % W.w, y = (i / W.w) | 0, cx = x + 0.5, cz = y + 0.5;
    const base = topOf(W.s.ground[i], x, y);
    let d1 = a, d2 = b;
    if (d1 < 0) d1 = (d2 + 2) % 4; if (d2 < 0) d2 = (d1 + 2) % 4;
    if (d1 < 0) { d1 = 2; d2 = 0; }
    if ((d1 + 2) % 4 === d2) {                     // пряма
      const along = d1 % 2 === 0;                  // уздовж X
      for (const o of [-0.33, 0, 0.33]) P.add(list, cx + (along ? o : 0), base, cz + (along ? 0 : o), along ? 0.15 : 0.82, 0.06, along ? 0.82 : 0.15, tint(C.tie, (hh(x, y, 100 + o * 10) - 0.5) * 0.2));
      for (const o of [-0.2, 0.2]) P.add(list, cx + (along ? 0 : o), base + 0.06, cz + (along ? o : 0), along ? 1.0 : 0.07, 0.08, along ? 0.07 : 1.0, C.steel);
    } else {                                       // поворот: дуга довкола кута клітинки
      const e1 = DIRS[d1], e2 = DIRS[d2];
      const kx = cx + (e1[0] + e2[0]) * 0.5, kz = cz + (e1[1] + e2[1]) * 0.5;
      const a0 = Math.atan2(-e2[1], -e2[0]), a1 = Math.atan2(-e1[1], -e1[0]);
      let da = a1 - a0; if (da > Math.PI) da -= 2 * Math.PI; if (da < -Math.PI) da += 2 * Math.PI;
      for (const t of [1 / 6, 0.5, 5 / 6]) {
        const an = a0 + da * t;
        P.add(list, kx + Math.cos(an) * 0.5, base, kz + Math.sin(an) * 0.5, 0.82, 0.06, 0.15, C.tie, -an);
      }
      const SEG = 4;
      for (const r of [0.3, 0.7]) for (let j = 0; j < SEG; j++) {
        const t0 = a0 + da * j / SEG, t1 = a0 + da * (j + 1) / SEG;
        const x0 = kx + Math.cos(t0) * r, z0 = kz + Math.sin(t0) * r, x1 = kx + Math.cos(t1) * r, z1 = kz + Math.sin(t1) * r;
        const len = Math.hypot(x1 - x0, z1 - z0) + 0.03;
        P.add(list, (x0 + x1) / 2, base + 0.06, (z0 + z1) / 2, len, 0.08, 0.07, C.steel, -Math.atan2(z1 - z0, x1 - x0));
      }
    }
  }

  // ---- гравці й потяг ----
  function syncPlayers(s) {
    while (W.players.length < s.players.length) { const p = makePlayer(W.players.length); W.root.add(p.root); W.players.push(p); }
    while (W.players.length > s.players.length) { const p = W.players.pop(); W.root.remove(p.root); }
  }
  function snapshot(s) {
    W.prev = W.cur;
    W.cur = { tick: s.tick, pos: s.train.pos, pl: s.players.map((p) => ({ x: p.x, y: p.y, fx: p.fx, fy: p.fy })) };
  }
  // точка на колії за pos (підодиниці від хвоста) → {x, z} у клітинках
  function along(s, pos) {
    const path = s.path, L = path.length;
    if (!L) return { x: 0, z: 0 };
    let i = Math.floor(pos / U), f = (pos - i * U) / U;
    if (i < 0) { i = 0; f = pos / U; }
    if (i >= L - 1) { f += i - (L - 1); i = L - 1; }
    const a = path[i], b = path[Math.min(L - 1, i + 1)];
    let ax = (a % s.w) + 0.5, az = Math.floor(a / s.w) + 0.5, bx = (b % s.w) + 0.5, bz = Math.floor(b / s.w) + 0.5;
    if (a === b) {   // за кінцем — продовжуємо напрям останнього відрізка
      const c = path[Math.max(0, L - 2)];
      const dx = ax - ((c % s.w) + 0.5), dz = az - (Math.floor(c / s.w) + 0.5);
      bx = ax + dx; bz = az + dz;
    }
    return { x: ax + (bx - ax) * f, z: az + (bz - az) * f };
  }

  // ---- камера ----
  const cam = { x: 0, z: 0, d: 30, init: false };
  const EL = 56 * Math.PI / 180;
  let shadowHalf = 0;

  // ---- кадр ----
  let lastT = performance.now(), smokeT = 0, frames = 0;
  const _v = new THREE.Vector3();

  function draw(s, alpha = 0) {
    if (!s) return;
    if (!W || W.s !== s) setWorld(s);
    const now = performance.now(), dt = Math.min(0.1, (now - lastT) / 1000); lastT = now; frames++;
    // розмір
    const cw = canvas.clientWidth | 0, ch = canvas.clientHeight | 0;
    if (cw && ch && (cw !== W.cw || ch !== W.ch)) { W.cw = cw; W.ch = ch; renderer.setSize(cw, ch, false); camera.aspect = cw / ch; camera.updateProjectionMatrix(); }

    // світ: інкрементно — порівняння з копією (≈ 5·w·h байтів, дешево), перебудова лише змінених клітинок
    const so = s.obj, sh = s.hp, sg = s.ground, sd = s.drop, sn = s.dropN;
    for (let i = 0; i < W.n; i++) {
      if (so[i] !== W.obj[i] || sh[i] !== W.hp[i] || sg[i] !== W.ground[i] || sd[i] !== W.drop[i] || sn[i] !== W.dropN[i]) syncCell(i, false);
    }
    syncPath(false);
    syncPlayers(s);
    if (s.tick !== W.tick) {
      const jump = W.tick === undefined || s.tick < W.tick || s.tick - W.tick > 3;
      W.tick = s.tick;
      snapshot(s);
      if (jump) W.prev = W.cur;
    } else if (W.cur.pos !== s.train.pos || W.cur.pl.length !== s.players.length || s.players.some((p, k) => p.x !== W.cur.pl[k].x || p.y !== W.cur.pl[k].y)) {
      snapshot(s); W.prev = W.cur;          // стан правили руками без тику — без інтерполяції
    }
    W.pGround.flush(); W.pBox.flush();
    alpha = Math.max(0, Math.min(1, alpha));
    const A = W.prev || W.cur, B = W.cur;

    // потяг
    const pos = A.pos + (B.pos - A.pos) * alpha;
    const t = s.train, lost = s.status === 'lost';
    let headX = 0, headZ = 0;
    W.train.forEach((m, k) => {
      const c = along(s, pos - k * U), f = along(s, pos - k * U + 300), b = along(s, pos - k * U - 300);
      m.g.position.set(c.x, 0.13, c.z);       // колеса на рейках (верх рейки ≈ 0,15)
      m.g.rotation.set(0, Math.atan2(-(f.z - b.z), f.x - b.x), 0);
      if (lost && s.reason === 'derail') { m.g.rotation.x = (k === 0 ? 0.25 : 0.1) * (k % 2 ? -1 : 1); m.g.rotation.order = 'YXZ'; }
      if (k === 0) { headX = c.x; headZ = c.z; }
    });
    const loco = W.train[0], store = W.train[1], craft = W.train[2];
    // котел: червоніє з нагрівом, горить при пожежі
    const heat = Math.max(0, Math.min(1, t.heat / 1000));
    const hot = Math.max(0, (heat - 0.55) / 0.45);
    loco.boilerMat.color.setHex(0xd23c32).lerp(_c.setHex(0xff5a1a), hot);
    loco.boilerMat.emissive.setHex(0x000000).lerp(_c.setHex(0xff3000), hot * (0.35 + 0.25 * Math.sin(now / (120 - 60 * hot))));
    // склад і майстерня
    syncStack(store, 'nW', store.wood, I.WOOD, t.wood);
    syncStack(store, 'nI', store.iron, I.IRON, t.iron);
    syncStack(craft, 'nR', craft.rails, I.RAIL, t.rails);

    // дим із труби, вогонь
    const moving = B.pos !== A.pos || (W.lastPos !== undefined && W.lastPos !== B.pos);
    W.lastPos = B.pos;
    smokeT -= dt;
    loco.g.updateMatrixWorld();
    if (smokeT <= 0 && !(s.status === 'won')) {
      smokeT = moving ? 0.09 : 0.32;
      _v.copy(loco.top).applyMatrix4(loco.g.matrixWorld);
      const sooty = hot > 0.4 || t.fire;
      puff(_v.x, _v.y, _v.z, 1, sooty ? 0x55585e : (Math.random() < 0.5 ? 0xf4f4f4 : 0xe2e4e8), moving ? 0.24 : 0.17, moving ? 1.5 : 2.0, moving ? 1.2 : 0.7);
    }
    if (t.fire > 0 || (lost && s.reason === 'fire')) {
      _v.set(0.1, 0.6, 0).applyMatrix4(loco.g.matrixWorld);
      for (let k = 0; k < 2; k++) spawn(_v.x + R(0.25), _v.y + R(0.05), _v.z + R(0.2), R(0.3), 1.2 + Math.random(), R(0.3), 0.45 + Math.random() * 0.3, 0.14 + Math.random() * 0.1, pick([0xff7a1a, 0xffb300, 0xff4a1a, 0xffe066], Math.random()), -0.5, 1);
      if (Math.random() < 0.15) puff(_v.x, _v.y + 0.3, _v.z, 1, 0x3a3d44, 0.3, 1.8, 1.0);
      fireLight.position.set(_v.x, _v.y + 0.5, _v.z); fireLight.intensity = 4 + Math.random() * 3;
    } else fireLight.intensity = 0;
    if (craft && t.craftT > 0 && Math.random() < dt * 6) {
      _v.copy(craft.stack).applyMatrix4(craft.g.matrixWorld);
      puff(_v.x, _v.y, _v.z, 1, 0xd0d0d0, 0.12, 1.0, 0.8);
    }

    // гравці
    let minX = headX, maxX = headX, sumX = headX, cnt = 1;
    s.players.forEach((pl, k) => {
      const m = W.players[k]; if (!m) return;
      const a = A.pl[k] || B.pl[k], b = B.pl[k] || a;
      const x = (a.x + (b.x - a.x) * alpha) / U, z = (a.y + (b.y - a.y) * alpha) / U;
      const dist = Math.hypot(x - m.x, z - m.z);
      m.x = x; m.z = z;
      const speed = dt > 0 ? dist / dt : 0;
      const gi = Math.floor(z) * s.w + Math.floor(x);
      const gy = gi >= 0 && gi < W.n ? topOf(s.ground[gi], Math.floor(x), Math.floor(z)) : 0;
      m.root.position.set(x, Math.max(0, gy), z);
      const yaw = Math.atan2(-pl.fy, pl.fx);
      let dy = yaw - m.yaw; while (dy > Math.PI) dy -= 2 * Math.PI; while (dy < -Math.PI) dy += 2 * Math.PI;
      m.yaw += dy * Math.min(1, dt * 14);
      m.root.rotation.y = m.yaw;
      const walking = speed > 0.4;
      m.phase += walking ? dt * 13 : 0;
      if (!walking) m.phase *= 0.85;
      const sw = Math.sin(m.phase) * (walking ? 0.7 : 0);
      m.legL.rotation.z = sw; m.legR.rotation.z = -sw;
      m.body.position.y = walking ? Math.abs(Math.sin(m.phase)) * 0.04 : 0;
      // річ у руках
      const hk = pl.hold.k ? pl.hold.k + ':' + Math.min(pl.hold.n, 12) : '';
      if (hk !== m.holdKey) {
        m.holdKey = hk;
        if (m.held) m.held.parent.remove(m.held);
        m.held = null;
        if (pl.hold.k) {
          const stack = pl.hold.k === I.WOOD || pl.hold.k === I.IRON || pl.hold.k === I.RAIL;
          m.held = makeItem(pl.hold.k, pl.hold.n, false);
          if (stack) { m.top.add(m.held); m.held.rotation.y = Math.PI / 2; }
          else { m.hand.add(m.held); m.held.rotation.set(0, 0, -Math.PI / 2); m.held.position.set(0.02, 0, 0); }
          m.stack = stack;
        }
      }
      m.swing = Math.max(0, m.swing - dt * 5);
      if (m.held && m.stack) {
        m.armL.rotation.z = m.armR.rotation.z = 2.75; m.armL.rotation.x = 0.15; m.armR.rotation.x = -0.15;
      } else if (m.held) {
        const sg = m.swing > 0 ? Math.sin(m.swing * Math.PI) : 0;
        m.armR.rotation.z = 1.0 + sg * 1.6 - (m.swing > 0.5 ? 0 : sg * 0.4); m.armR.rotation.x = 0;
        m.armL.rotation.z = -sw * 0.8; m.armL.rotation.x = 0;
        if (m.held) m.held.rotation.z = -Math.PI / 2 + (m.swing > 0 ? -0.6 + sg * 0.6 : 0);
      } else {
        m.armL.rotation.z = -sw * 0.8; m.armR.rotation.z = sw * 0.8; m.armL.rotation.x = m.armR.rotation.x = 0;
      }
      minX = Math.min(minX, x); maxX = Math.max(maxX, x); sumX += x; cnt++;
    });

    // камера: тримає паротяг у кадрі, тягнеться до гравців
    const aspect = camera.aspect || 1.6;
    const tanH = Math.tan(camera.fov * Math.PI / 360);
    const dH = (s.h + 1.2) * Math.sin(EL) / (2 * tanH);
    const spread = maxX - minX + 10;
    const dW = Math.max(18, Math.min(60, spread)) / (2 * tanH * aspect);
    const dGoal = Math.max(dH, Math.min(dW, dH * 1.7));
    const halfW = dGoal * tanH * aspect;
    let gx = (minX + maxX) / 2;
    gx = Math.max(headX - halfW * 0.55, Math.min(headX + halfW * 0.55, gx));
    gx = Math.max(halfW - 4, Math.min(s.w + 4 - halfW * 0.6, gx));
    const gz = s.h / 2 + 0.6;
    const kk = cam.init ? 1 - Math.exp(-dt * 2.2) : 1;
    cam.x += (gx - cam.x) * kk; cam.z += (gz - cam.z) * kk; cam.d += (dGoal - cam.d) * kk; cam.init = true;
    camera.position.set(cam.x, cam.d * Math.sin(EL), cam.z + cam.d * Math.cos(EL));
    camera.lookAt(cam.x, 0, cam.z);
    scene.fog.near = cam.d * 1.25; scene.fog.far = cam.d * 2.6;

    // сонце й тіні ходять за камерою (крок — тексель, щоб тіні не мерехтіли)
    const half = Math.ceil(Math.max(cam.d * tanH * aspect * 1.15, s.h * 0.75) + 3);
    if (half !== shadowHalf) {
      shadowHalf = half; const sc = sun.shadow.camera;
      sc.left = -half; sc.right = half; sc.top = half; sc.bottom = -half; sc.near = 1; sc.far = 120; sc.updateProjectionMatrix();
    }
    const tex = (2 * half) / 2048;
    const tx = Math.round(cam.x / tex) * tex, tz = Math.round(cam.z / tex) * tex;
    sun.target.position.set(tx, 0, tz);
    sun.position.set(tx + SUN_DIR.x * 50, SUN_DIR.y * 50, tz + SUN_DIR.z * 50);

    waterTime.value = now / 1000;
    stepParticles(dt);
    renderer.render(scene, camera);
  }

  function syncStack(m, key, group, kind, n) {
    n = Math.max(0, Math.min(16, n | 0));
    if (m[key] === n) return;
    m[key] = n;
    while (group.children.length) group.remove(group.children[0]);
    if (!n) return;
    // склад: дві купки по 8 (2 в ряд × 4 шари); майстерня — стос рейок
    if (kind === I.RAIL) { const it = makeItem(I.RAIL, Math.min(n, 8)); it.rotation.y = Math.PI / 2; it.scale.setScalar(0.85); group.add(it); return; }
    const left = makeItem(kind, Math.min(n, 8)); left.position.z = -0.12; left.scale.setScalar(0.7); left.rotation.y = Math.PI / 2; group.add(left);
    if (n > 8) { const right = makeItem(kind, n - 8); right.position.z = 0.12; right.scale.setScalar(0.7); right.rotation.y = Math.PI / 2; group.add(right); }
  }

  // ---- події тику ----
  function cellXZ(e) {
    const s = W && W.s; if (!s) return null;
    let x = e.x, y = e.y;
    if (x === undefined || y === undefined) return null;
    if (Math.abs(x) > s.w + 2 || Math.abs(y) > s.h + 2) return { x: x / U, z: y / U };   // у підодиницях
    return { x: x + 0.5, z: y + 0.5 };                                                     // у клітинках
  }
  function events(ev) {
    if (!W || !ev || !ev.length) return;
    for (const e of ev) {
      const c = cellXZ(e);
      if (e.p !== undefined && W.players[e.p] && (e.t === 'chop' || e.t === 'rock')) W.players[e.p].swing = 1;
      const loco = W.train[0];
      const lp = () => { loco.g.updateMatrixWorld(); return _v.copy(loco.top).applyMatrix4(loco.g.matrixWorld); };
      switch (e.t) {
        case 'chop': if (c) { burst(c.x, 0.6, c.z, 7, [0x8a5c33, 0xc8955a, 0xd9b27a], 1.8, 2.5, 0.08); burst(c.x, 0.9, c.z, 5, C.leaf, 1.2, 1.5, 0.09, 0.9, 4); } break;
        case 'rock': if (c) { burst(c.x, 0.5, c.z, 8, [0x9ea4ab, 0x7e858d, 0xc26a35], 2, 2.6, 0.08); burst(c.x, 0.5, c.z, 4, [0xffe066, 0xffb300], 3, 2, 0.04, 0.3, 6); } break;
        case 'pick': case 'drop': if (c) burst(c.x, 0.05, c.z, 4, [0xd8c9a0, 0xc7b88e], 1, 1, 0.06, 0.4); break;
        case 'rail': if (c) { burst(c.x, 0.1, c.z, 6, [0xd8c9a0, 0xb4bac2], 1.5, 1.5, 0.07, 0.5); } break;
        case 'craft': { const cr = W.train[2]; cr.g.updateMatrixWorld(); _v.set(-0.3, 0.4, 0).applyMatrix4(cr.g.matrixWorld); burst(_v.x, _v.y, _v.z, 8, [0xffe066, 0xffb300, 0xff7a1a], 1.8, 2.5, 0.05, 0.4, 6); break; }
        case 'water': if (c) burst(c.x, -0.1, c.z, 10, [0x7cc8f2, 0xbfe6ff, 0xffffff], 1.4, 3, 0.08, 0.6); break;
        case 'douse': { const p = lp(); puff(p.x, p.y - 0.2, p.z, 14, 0xffffff, 0.3, 1.6, 1.6); burst(p.x, p.y - 0.2, p.z, 10, [0x7cc8f2, 0xbfe6ff], 1.8, 2.5, 0.08, 0.6); break; }
        case 'whistle': { const p = lp(); puff(p.x, p.y, p.z, 8, 0xffffff, 0.26, 1.2, 2.2); break; }
        case 'alarm': { const p = lp(); burst(p.x, p.y, p.z, 5, [0xff4a1a, 0xffb300], 1.5, 2, 0.06, 0.4); break; }
        case 'step': if (c && Math.random() < 0.5) spawn(c.x + R(0.15), 0.05, c.z + R(0.15), R(0.3), 0.6, R(0.3), 0.35, 0.06, 0xd8cfa8, 4); break;
        case 'won': { const p = lp(); for (let k = 0; k < 90; k++) spawn(p.x + R(1.5), p.y + 0.5, p.z + R(1.5), R(2.5), 4 + Math.random() * 4, R(2.5), 2 + Math.random(), 0.09, pick([0xffd23f, 0xff5fa2, 0x4fc3ff, 0x9be15d, 0xffffff, 0x1f6fd1], Math.random()), 5); break; }
        case 'lost': { const p = lp(); puff(p.x, p.y, p.z, 16, 0x55585e, 0.4, 2.2, 1.3); burst(p.x, p.y - 0.3, p.z, 14, [0xff7a1a, 0xffb300, 0x3a3d44], 3, 3, 0.1, 0.8); break; }
      }
    }
  }

  function dispose() {
    disposeWorld();
    pMesh.dispose();
    renderer.dispose();
  }

  // клітинка (дробові координати, центр = x + 0.5) → { x, y } у CSS-пікселях вікна або null, якщо поза кадром
  const _pj = new THREE.Vector3();
  function project(cx, cy, hgt = 0) {
    _pj.set(cx, hgt, cy).project(camera);
    if (_pj.z < -1 || _pj.z > 1 || _pj.x < -1 || _pj.x > 1 || _pj.y < -1 || _pj.y > 1) return null;
    const r = canvas.getBoundingClientRect();
    return { x: r.left + (_pj.x + 1) / 2 * r.width, y: r.top + (1 - _pj.y) / 2 * r.height };
  }

  const api = { setWorld, draw, events, dispose, project, get frames() { return frames; }, renderer, scene, camera };
  if (globalThis.KOL) globalThis.KOL.view = api;      // для перевірок у консолі
  return api;
}
