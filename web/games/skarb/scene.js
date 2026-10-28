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
import { GRID, BIT, DIRS, STEP, OPP, SEAT_COLOR, PAL } from './consts.js';

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

// Стилі вигляду (опція createScene({ style })). classic — як було; решта — варіанти на вибір власника.
//   warm — тепла ніч: плями світла від ліхтарів, ACES, м'які тіні, багатий ґрунт, світлячки;
//   toon — казковий: соковиті кольори, тоншейдинг сходинками й контур «вивернутою оболонкою», грайливе небо з хмарками;
//   dramatic — темне підземелля: глибока тьма, сильний ліхтар-прожектор у того, хто ходить, туман, порошинки, золото сяє.
const FX_ALL = { noise: 0.12, rim: 1, crack: 1, embeds: 1, props: 1, grass: 1, smoke: 1, halo: 1, sparkles: 1, figs2: 1, softHL: 1, figLamp: 1, decor: 1, map2: 1, bob: 1 };
const STYLES = {
  classic: { bg: '#140d09', tone: 'neutral', exp: 1.1, soft: false, hemi: ['#a8b6e0', '#5a3a22', 1.0], amb: ['#ffa860', 0.35], moon: ['#d6e0ff', 1.55],
    rim: null, fog: null, heat: { R: 1.75, base: 0.66, gain: 0.7, lamp: 0.24, lampMax: 0.32 }, glowR: 0.62, glowK: 0.55, gold: 1, dust: '#a2856a', fx: {} },
  warm: { bg: '#120b08', tone: 'aces', exp: 1.45, soft: true, hemi: ['#c4b4a6', '#5a3820', 1.0], amb: ['#ff9a50', 0.3], moon: ['#dde0f2', 1.45],
    rim: ['#ff8a4a', 0.8], fog: { c: '#120b08', near: 2, far: 24 }, heat: { R: 2.2, base: 1.0, gain: 0.65, lamp: 0.3, lampMax: 0.42 },
    glowR: 0.6, glowK: 0.14, gold: 1.5, dust: '#a2856a', lump: { c: 0.05, bul: 0.024, deep: 0.3 }, backMul: 0.38,
    sky: { skyTop: '#18223f', skyLow: '#f39a58', skyPow: 0.55, hillA: '#2a2c48', hillB: '#1a2222' },
    fx: { ...FX_ALL, fireflies: 1, lights: 'point' } },
  toon: { bg: '#2e1c16', tone: 'none', exp: 1, soft: false, hemi: ['#c8d4ff', '#7a4a30', 1.6], amb: ['#ffd0a0', 0.55], moon: ['#fff2dc', 2.0],
    rim: null, fog: null, heat: { R: 1.9, base: 1.05, gain: 0.4, lamp: 0.22, lampMax: 0.3 }, glowR: 0.6, glowK: 0.16, gold: 1.3, dust: '#e8cfa0',
    lump: { c: 0.085, bul: 0.05, deep: 0.5 }, backMul: 0.45, grade: { sat: 1.45, mul: 1.06 },
    sky: { skyTop: '#3d63c8', skyLow: '#ffbf86', skyPow: 0.75, clouds: 'toon', moonS: 1.3, moonC: '#fff6c8', stars: 34, hillA: '#6a62b8', hillB: '#3f7a52' },
    fx: { ...FX_ALL, noise: 0.06, outline: 1 } },
  dramatic: { bg: '#060509', tone: 'aces', exp: 1.25, soft: true, hemi: ['#56648c', '#140a06', 0.4], amb: ['#ff8a40', 0.1], moon: ['#9fb0e0', 0.85],
    rim: ['#ff5a2a', 0.45], fog: { c: '#060509', near: 1, far: 15 }, heat: { R: 2.4, base: 0.42, gain: 1.4, lamp: 0.42, lampMax: 0.6 },
    glowR: 0.7, glowK: 0.22, gold: 2.4, dust: '#8a7a6a', lump: { c: 0.05, bul: 0.024, deep: 0.18 }, backMul: 0.3,
    sky: { skyTop: '#04060d', skyLow: '#6e2616', skyPow: 0.4, clouds: 'dark', stars: 26, moonS: 0.85, moonC: '#e8e4d0', hillA: '#121828', hillB: '#0a0f10' },
    fx: { ...FX_ALL, noise: 0.15, motes: 1, lights: 'spot' } },
};
export const SCENE_STYLES = Object.keys(STYLES);

const clamp = (x, a, b) => Math.max(a, Math.min(b, x));
const k01 = (t, a, b) => clamp((t - a) / (b - a), 0, 1);
const easeOut = (k) => 1 - Math.pow(1 - k, 3);
const easeIn = (k) => k * k * k;
const easeInOut = (k) => k < 0.5 ? 4 * k * k * k : 1 - Math.pow(-2 * k + 2, 3) / 2;
function backOut(k) { const c1 = 2.0, c3 = c1 + 1; return 1 + c3 * Math.pow(k - 1, 3) + c1 * Math.pow(k - 1, 2); }
const key = (x, y) => x + ',' + y;
const hash = (a, b = 0) => { let h = Math.imul(a | 0, 374761393) ^ Math.imul(b | 0, 668265263); h = Math.imul(h ^ (h >>> 13), 1274126177); return ((h ^ (h >>> 16)) >>> 0) / 4294967296; };

/** Гладкий шум значень 0..1 (дві октави) — для «живого» кольору ґрунту. */
function vnoise(x, y) {
  const xi = Math.floor(x), yi = Math.floor(y), fx = x - xi, fy = y - yi;
  const sx = fx * fx * (3 - 2 * fx), sy = fy * fy * (3 - 2 * fy);
  const a = hash(xi, yi), b = hash(xi + 1, yi), c = hash(xi, yi + 1), d = hash(xi + 1, yi + 1);
  return a + (b - a) * sx + (c - a) * sy + (a - b - c + d) * sx * sy;
}
const fbm = (x, y) => vnoise(x, y) * 0.65 + vnoise(x * 2.1 + 5.3, y * 2.1 - 1.7) * 0.35;

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
  const STY = STYLES[opts.style] || STYLES.classic, FX = STY.fx;
  const SOIL = STY !== STYLES.classic;
  const HT = STY.heat;   // суцільний мальований ґрунт замість грудок (усі стилі, крім classic)
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
  renderer.toneMapping = STY.tone === 'aces' ? THREE.ACESFilmicToneMapping : STY.tone === 'none' ? THREE.NoToneMapping : THREE.NeutralToneMapping;
  renderer.toneMappingExposure = STY.exp;
  renderer.shadowMap.enabled = hi;
  renderer.shadowMap.type = THREE.PCFShadowMap;
  const canvas = renderer.domElement;
  canvas.style.cssText = 'display:block;width:100%;height:100%;touch-action:none;outline:none;-webkit-tap-highlight-color:transparent;user-select:none';
  host.appendChild(canvas);

  const scene = new THREE.Scene();
  scene.background = new THREE.Color(STY.bg);
  if (STY.fog) scene.fog = new THREE.Fog(STY.fog.c, 30, 60);
  const cam = new THREE.PerspectiveCamera(FOV, 1, 0.1, 300);

  const hemi = new THREE.HemisphereLight(STY.hemi[0], STY.hemi[1], STY.hemi[2]);
  scene.add(hemi);
  const warm = new THREE.AmbientLight(STY.amb[0], STY.amb[1]);
  scene.add(warm);
  const moon = new THREE.DirectionalLight(STY.moon[0], STY.moon[1]);
  moon.castShadow = hi && FX.lights !== 'spot';
  if (moon.castShadow) {
    moon.shadow.mapSize.set(1024, 1024);
    moon.shadow.bias = -0.0006; moon.shadow.normalBias = 0.015;
    if (STY.soft) moon.shadow.radius = 3.5;
  }
  scene.add(moon); scene.add(moon.target);
  // Захід/місячний відсвіт з-за хати (без тіней) — лише для стилів, де він є.
  const rimL = STY.rim ? new THREE.DirectionalLight(STY.rim[0], STY.rim[1]) : null;
  if (rimL) { scene.add(rimL); scene.add(rimL.target); }
  // Справжні ліхтарі (лише high): A — у руці того, хто ходить (dramatic — прожектор із тінню), B — найсвіжіший ліхтар ходу.
  let lampA = null, lampB = null;
  if (hi && FX.lights) {
    if (FX.lights === 'spot') {
      lampA = new THREE.SpotLight('#ffc070', 0, 6.5, 0.95, 0.75, 1.2);
      lampA.castShadow = true; lampA.shadow.mapSize.set(1024, 1024); lampA.shadow.bias = -0.0008; lampA.shadow.normalBias = 0.01; lampA.shadow.radius = 4;
      lampA.shadow.camera.near = 0.05; lampA.shadow.camera.far = 7;
      scene.add(lampA.target);
    } else lampA = new THREE.PointLight('#ffc070', 0, 2.8, 1.3);
    lampB = new THREE.PointLight(PAL.lamp, 0, 2.4, 1.4);
    scene.add(lampA); scene.add(lampB);
  }
  const flash = new THREE.PointLight(PAL.lamp, 0, 3.2, 1.5);
  const chestLight = new THREE.PointLight(PAL.gold, 0, 3.6, 1.5);
  scene.add(flash); scene.add(chestLight);

  const toon = !!FX.outline;
  let gradTex = null;
  if (toon) {
    gradTex = new THREE.DataTexture(new Uint8Array([90, 175, 255]), 3, 1, THREE.RedFormat);
    gradTex.minFilter = gradTex.magFilter = THREE.NearestFilter; gradTex.generateMipmaps = false; gradTex.needsUpdate = true;
  }
  const litMat = (o) => toon ? new THREE.MeshToonMaterial({ vertexColors: true, gradientMap: gradTex, ...(o.emissive ? { emissive: o.emissive } : {}) })
    : new THREE.MeshStandardMaterial({ vertexColors: true, flatShading: true, roughness: 0.92, metalness: 0, ...o });
  const matVC = litMat({});
  const matMetal = toon ? litMat({ emissive: new THREE.Color('#4a3000') })
    : new THREE.MeshStandardMaterial({ vertexColors: true, flatShading: true, roughness: 0.42, metalness: 0.3, emissive: new THREE.Color('#3a2400') });
  // Трава колихається у вершинному шейдері (час — одна уніформа; без перебудови матриць).
  const grassU = { value: 0 };
  const matGrass = litMat({});
  matGrass.onBeforeCompile = (sh) => {
    sh.uniforms.uTime = grassU;
    sh.vertexShader = 'uniform float uTime;\n' + sh.vertexShader.replace('#include <begin_vertex>', `#include <begin_vertex>
#ifdef USE_INSTANCING
  float ph = instanceMatrix[3].x * 1.7 + instanceMatrix[3].z * 0.9;
  transformed.x += sin(uTime * 1.6 + ph) * 1.4 * position.y * position.y;
#endif`);
  };
  matGrass.customProgramCacheKey = () => 'skarb-grass';
  // Контур «вивернутою оболонкою»: задні грані, зсунуті вздовж згладженої нормалі.
  const olMat = (w) => {
    const m = new THREE.MeshBasicMaterial({ color: '#1c0f14', side: THREE.BackSide });
    m.onBeforeCompile = (sh) => { sh.vertexShader = sh.vertexShader.replace('#include <begin_vertex>', `vec3 transformed = vec3( position ) + normal * ${w.toFixed(4)};`); };
    m.customProgramCacheKey = () => 'skarb-ol' + w;
    return m;
  };
  const matOL = toon ? olMat(0.0085) : null, matOLs = toon ? olMat(0.016) : null;
  const matHLA = new THREE.MeshBasicMaterial({ vertexColors: true, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, toneMapped: false, fog: false });
  const matSmoke = new THREE.MeshStandardMaterial({ color: '#9a948e', flatShading: true, transparent: true, opacity: 0.42, depthWrite: false, fog: false });
  const matLampG = new THREE.MeshBasicMaterial({ color: new THREE.Color('#ffe7a6').multiplyScalar(1.4), toneMapped: false });
  const matGlowVC = new THREE.MeshBasicMaterial({ vertexColors: true, toneMapped: false });
  const matSky = new THREE.MeshBasicMaterial({ vertexColors: true, fog: false, depthWrite: false });
  const matAdd = new THREE.MeshBasicMaterial({ vertexColors: true, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, toneMapped: false });
  const matBeam = new THREE.MeshBasicMaterial({ vertexColors: true, transparent: true, blending: THREE.AdditiveBlending, depthWrite: false, toneMapped: false, opacity: 0, side: THREE.DoubleSide });
  const matHL = new THREE.MeshBasicMaterial({ vertexColors: true, transparent: true, depthWrite: false, toneMapped: false, opacity: 0.95 });
  const matCursor = new THREE.MeshBasicMaterial({ vertexColors: true, transparent: true, depthWrite: false, toneMapped: false });
  const matGhost = new THREE.MeshStandardMaterial({ vertexColors: true, transparent: true, opacity: 0.45, depthWrite: false, flatShading: true,
    color: new THREE.Color(PAL.alt), emissive: new THREE.Color('#8a5c18') });
  const matDust = new THREE.MeshStandardMaterial({ color: STY.dust, flatShading: true, transparent: true, opacity: 0.8, depthWrite: false });
  const mats = [matVC, matMetal, matGlowVC, matSky, matAdd, matBeam, matHL, matCursor, matGhost, matDust, matGrass, matHLA, matSmoke, matLampG];
  if (matOL) mats.push(matOL, matOLs);

  // Кольорова «градація» стилю (toon — соковитіше): насиченість і яскравість кольорів вершин.
  const grade = (g) => {
    const gr = STY.grade, c = g && g.attributes.color;
    if (!gr || !c || g.userData.graded) return g;
    g.userData.graded = true;
    const a = c.array;
    for (let i = 0; i < a.length; i += 3) {
      const l = a[i] * 0.2126 + a[i + 1] * 0.7152 + a[i + 2] * 0.0722;
      for (let k = 0; k < 3; k++) a[i + k] = Math.max(0, (l + (a[i + k] - l) * gr.sat) * gr.mul);
    }
    c.needsUpdate = true;
    return g;
  };
  const gradeC = (c) => {
    const gr = STY.grade; if (!gr) return c;
    const l = c.r * 0.2126 + c.g * 0.7152 + c.b * 0.0722;
    c.r = Math.max(0, (l + (c.r - l) * gr.sat) * gr.mul); c.g = Math.max(0, (l + (c.g - l) * gr.sat) * gr.mul); c.b = Math.max(0, (l + (c.b - l) * gr.sat) * gr.mul);
    return c;
  };

  // ------------------------------------------------------------------------------------------------ інстанси
  const _m4 = new THREE.Matrix4(), _q = new THREE.Quaternion(), _eu = new THREE.Euler(), _pv = new THREE.Vector3(), _sv = new THREE.Vector3();
  const _c = new THREE.Color(), _c2 = new THREE.Color();
  const ZERO = new THREE.Matrix4().makeScale(0, 0, 0);
  function inst(geo, cap, { mat = matVC, color = false, cast = false, recv = false, parent = scene, order = 0, ol = null } = {}) {
    if (mat === matVC || mat === matMetal || mat === matGrass) grade(geo);
    const m = new THREE.InstancedMesh(geo, mat, Math.max(1, cap));
    m.instanceMatrix.setUsage(THREE.DynamicDrawUsage);
    m.frustumCulled = false;
    m.castShadow = cast && hi; m.receiveShadow = recv && hi;
    if (color) { const w = new THREE.Color(1, 1, 1); for (let i = 0; i < m.count; i++) m.setColorAt(i, w); m.instanceColor.setUsage(THREE.DynamicDrawUsage); }
    m.count = 0; m.visible = false; m.renderOrder = order;
    parent.add(m);
    if (ol && toon) {
      const o = new THREE.InstancedMesh(M.smoothNormals(geo.clone()), ol, Math.max(1, cap));
      o.instanceMatrix = m.instanceMatrix; o.frustumCulled = false; o.count = 0; o.visible = false;
      m.userData.ol = o; parent.add(o);
    }
    return m;
  }
  /** Контур для звичайного меша — дочірній меш зі згладженими нормалями. */
  function outlineOf(mesh, ol = matOL) {
    if (!toon || !ol) return;
    const o = new THREE.Mesh(M.smoothNormals(mesh.geometry.clone()), ol);
    mesh.add(o);
  }
  function setM(mesh, i, x, y, z, rx, ry, rz, sx, sy = sx, sz = sx) {
    _eu.set(rx, ry, rz, 'YXZ');
    mesh.setMatrixAt(i, _m4.compose(_pv.set(x, y, z), _q.setFromEuler(_eu), _sv.set(sx, sy, sz)));
  }
  function finish(mesh, n) {
    mesh.count = n; mesh.visible = n > 0;
    mesh.instanceMatrix.needsUpdate = true;
    if (mesh.instanceColor) mesh.instanceColor.needsUpdate = true;
    const o = mesh.userData.ol; if (o) { o.count = n; o.visible = n > 0; }
  }

  // Незалежне від поля.
  const figShirt = inst(M.figShirtGeo(), 10, { color: true, cast: true, ol: matOL });
  const figBody = inst(FX.figs2 ? M.figBodyGeo2() : M.figBodyGeo(), 10, { cast: true, ol: matOL });
  const figPick = inst(FX.figs2 ? M.pickGeo2() : M.pickGeo(), 10, { cast: true, ol: matOL });
  const figBroken = FX.figs2 ? inst(M.brokenPickGeo(), 10, { cast: true }) : null;
  const boards = inst(FX.figs2 ? M.goalBoardGeo2() : M.goalBoardGeo(), 6, { cast: true, recv: true, ol: hi ? matOL : null });
  const peekCoin = inst(M.peekCoinGeo(), 4, { mat: matMetal });
  const peekStone = inst(M.peekStoneGeo(), 4);
  const rockGeo = M.rockGeo();
  const pileRocks = inst(rockGeo, 14, { color: true, cast: true });
  const fallRocks = inst(rockGeo, 14, { color: true, cast: true });
  const coinGeo = M.coinGeo();
  const pileCoins = inst(coinGeo, 16, { mat: matMetal });
  const flyCoins = inst(coinGeo, 56, { mat: matMetal });
  const dust = inst(M.dustGeo(), 64, { mat: matDust, order: 4 });
  // Іскорки золота, світлячки/порошинки, дим із димаря, ліхтар у руці того, хто ходить.
  const sparks = FX.sparkles && hi ? inst(M.sparkleGeo(), 28, { mat: matAdd, color: true, order: 5 }) : null;
  const motes = (FX.fireflies || FX.motes) && hi && !reduced ? inst(M.glowGeo(8), 48, { mat: matAdd, color: true, order: 4 }) : null;
  const smoke = FX.smoke && hi ? inst(M.dustGeo(), 8, { mat: matSmoke, color: true, order: 2 }) : null;
  let figLamp = null;
  if (FX.figLamp) {
    const f = new THREE.Mesh(grade(M.lanternFrameGeo()), matVC), gl = new THREE.Mesh(M.lanternGlassGeo(), matLampG);
    const halo = new THREE.Mesh(M.glowGeo(20, true), matAdd.clone());
    halo.material.color = new THREE.Color(PAL.lamp).multiplyScalar(0.85); halo.renderOrder = 3;
    mats.push(halo.material);
    f.castShadow = hi;
    figLamp = new THREE.Group(); figLamp.add(f, gl); figLamp.visible = false;
    figLamp.halo = halo; halo.visible = false;
    scene.add(figLamp); scene.add(halo);
  }
  const moles = inst(M.moleGeo(), 6, { cast: true });
  const chestBody = new THREE.Mesh(grade(M.chestBodyGeo()), matMetal);
  const chestLid = new THREE.Mesh(grade(M.chestLidGeo()), matMetal);
  outlineOf(chestBody); outlineOf(chestLid);
  chestBody.castShadow = chestLid.castShadow = hi; chestBody.visible = chestLid.visible = false;
  scene.add(chestBody); scene.add(chestLid);
  const mapSheet = new THREE.Mesh(grade(FX.map2 ? M.mapSheetGeo2() : M.mapSheetGeo()), matVC);
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
  let hlGlow = null, junctions = null, tracks = null, ladders = null, rubble = null, grass = null, embMeshes = [], embList = [], embOf = null, crack = null, rimK = null;
  let craneArm = null, craneRope = null, craneTip = null;
  // базові дані грудок
  let bX, bY, bZ, bRx, bRy, bRz, bS, bCol, nLumps = 0;
  let holes = null;   // Uint16Array: які грудки клітинки сховані (поточний стан моделі)

  function cellIndex(x, y) {
    if (!G || x < G.x0 || x > G.x1 || y < G.y0 || y > G.y1) return -1;
    return (y - G.y0) * G.W + (x - G.x0);
  }

  /** Вкраплення в ґрунт (камінці, коріння, кістки, черепки) — прив'язані до грудок: зникають разом із ними. */
  function buildEmbeds() {
    const goalSet = new Set(G.goals.map(([x, y]) => key(x, y)));
    const st = key(G.start[0], G.start[1]);
    const kinds = { pebble: [], root: [], bone: [], pot: [] };
    embOf = new Int32Array(nLumps).fill(-1);
    for (let y = G.y0; y <= G.y1; y++) for (let x = G.x0; x <= G.x1; x++) {
      const kk = key(x, y);
      if (G.shaft.has(kk) || goalSet.has(kk) || kk === st) continue;
      const ci = cellIndex(x, y);
      for (let j = 0; j < 9; j++) {
        const i = ci * 9 + j, r = (q) => hash(i * 13 + q, 4243);
        const u = r(1);
        let kind = null;
        if (y <= -3 && u < 0.07) kind = 'root';
        else if (y >= 0 && y <= 3 && u > 0.993) kind = 'bone';
        else if (y >= -3 && y <= 1 && u > 0.975 && u <= 0.986) kind = 'pot';
        else if (u > 0.07 && u < 0.07 + (y >= 2 ? 0.16 : 0.09)) kind = 'pebble';
        if (!kind || (!hi && kind !== 'pebble')) continue;
        const e = { i, kind, dx: (r(2) - 0.5) * 0.14, dy: (r(3) - 0.5) * 0.14, dz: 0.012, rx: 0, ry: 0, rz: r(4) * 6.28, s: 1 };
        if (kind === 'pebble') { e.s = 0.028 + r(5) * 0.03; e.rx = r(6) * 3; e.ry = r(7) * 3; e.dz = 0.0; }
        else if (kind === 'root') { e.dx *= 0.5; e.rz = (r(4) - 0.5) * 0.8; e.s = 0.8 + r(5) * 0.5; }
        else if (kind === 'bone') e.s = 0.55 + r(5) * 0.25;
        else e.s = 0.85 + r(5) * 0.3;
        kinds[kind].push(e);
      }
    }
    const geos = { pebble: M.rockGeo(), root: M.rootGeo(), bone: M.boneGeo(), pot: M.potGeo() };
    for (const k of Object.keys(kinds)) {
      const list = kinds[k];
      if (!list.length) { geos[k].dispose(); continue; }
      const mesh = inst(geos[k], list.length, { color: k === 'pebble', recv: true, parent: gridGroup });
      list.forEach((e, n) => {
        e.mesh = mesh; e.idx = n; embOf[e.i] = embList.length; embList.push(e);
        if (k === 'pebble') {
          const y = Math.round(-bY[e.i]);
          mesh.setColorAt(n, gradeC(_c.set(y >= 4 ? '#9a9890' : y >= 2 ? '#c9b48a' : hash(e.i, 5) < 0.5 ? '#7c7468' : '#a08a6a').multiplyScalar(0.75 + hash(e.i, 9) * 0.4)));
        }
      });
      finish(mesh, list.length);
      embMeshes.push(mesh);
    }
  }
  function writeEmbed(e, s, ox, oy, oz) {
    const i = e.i;
    if (s <= 0.0005) e.mesh.setMatrixAt(e.idx, ZERO);
    else setM(e.mesh, e.idx, bX[i] + ox + e.dx, bY[i] + oy + e.dy, bZ[i] + oz + e.dz, e.rx, e.ry, e.rz, e.s * s);
    e.mesh.instanceMatrix.needsUpdate = true;
  }

  /** Травинки на поверхні: густо вздовж краю розрізу, рідше вглиб; колихаються у шейдері. */
  function buildGrass() {
    const XL = G.x0 - 0.5, XR = G.x1 + 0.5, sx = G.start[0];
    const pts = [];
    for (let k = 0; k < 1100; k++) {
      const a = hash(k, 31), b = hash(k, 37), c = hash(k, 41);
      const front = k < 700;
      const x = XL - 3 + a * (XR - XL + 6), z = front ? 0.01 - b * b * 1.4 : -1.2 - b * 5;
      if (Math.abs(x - sx) < 0.42 && z > -0.75) continue;                                     // зруб
      if (x > sx + 2.4 && x < sx + 4.4 && z < -1.1 && z > -2.4) continue;                     // хата
      pts.push([x, z, c, a]);
    }
    grass = inst(M.grassBladeGeo(), pts.length, { mat: matGrass, color: true, parent: gridGroup });
    pts.forEach(([x, z, c, a], n) => {
      const out = Math.max(0, Math.max(XL - x, x - XR)) / 3;
      const s = (0.75 + c * 0.7) * (z < -1.2 ? 1.25 : 1);
      setM(grass, n, x, G.surfY - 0.005, z, 0, (a - 0.5) * 1.4, (c - 0.5) * 0.35, s * 0.9, s, s);
      grass.setColorAt(n, gradeC(_c.setRGB(0.85 + c * 0.3, 0.9 + a * 0.2, 0.7 + c * 0.2)).lerp(new THREE.Color(STY.bg), Math.min(0.8, out * 0.7 + (z < -3 ? 0.25 : 0))));
    });
    finish(grass, pts.length);
  }

  // ------------------------------------------------------------------------------------------------ суцільний ґрунт
  // Лице розрізу — одна площина з мальованою (у <canvas>) текстурою шарів. Ходи вирізає маска (DataTexture, RGBA):
  //   G — земля (0 — хід; м'який край дає темний «вирізьблений» обідок), R — привид карти під курсором,
  //   B — тепло ліхтарів (плями світла), A — золоте сяйво. Маска перемальовується лише коли щось змінилось.
  let soil = null;
  const soilD = { earth: false, ghost: false, heat: false };
  const HW = 0.19, SHAFT_HW = 0.2, FEATHER = 0.14;
  const soilU = { uHeat: { value: new THREE.Vector4(HT.base, HT.gain, HT.lamp, HT.lampMax) }, uLampC: { value: new THREE.Color(PAL.lamp) },
    uGoldC: { value: new THREE.Color(PAL.gold) }, uGhostC: { value: new THREE.Color('#140c08') }, uRim: { value: STY.soilRim ?? 0.32 } };
  function buildSoil() {
    const ext = { X0: G.x0 - 3.5, X1: G.x1 + 3.5, Y0: G.botY, Y1: G.surfY };
    const appu = hi ? 56 : 30, mppu = hi ? 32 : 20;
    const cvA = document.createElement('canvas');
    cvA.width = Math.round((ext.X1 - ext.X0) * appu); cvA.height = Math.round((ext.Y1 - ext.Y0) * appu);
    const tone = STY.grade ? (hex) => gradeC(new THREE.Color(hex)).getStyle() : (hex) => hex;
    M.paintSoil(cvA, ext, appu, { XL: G.x0 - 0.5, XR: G.x1 + 0.5, surfY: G.surfY, botY: G.botY, y0: G.y0 }, tone, STY.bg);
    const albedo = new THREE.CanvasTexture(cvA);
    albedo.colorSpace = THREE.SRGBColorSpace; albedo.anisotropy = hi ? 4 : 1;
    const MW = Math.round((ext.X1 - ext.X0) * mppu), MH = Math.round((ext.Y1 - ext.Y0) * mppu);
    const mk = () => { const c = document.createElement('canvas'); c.width = MW; c.height = MH; return c.getContext('2d', { willReadFrequently: true }); };
    const data = new Uint8Array(MW * MH * 4);
    const tex = new THREE.DataTexture(data, MW, MH, THREE.RGBAFormat);
    tex.minFilter = tex.magFilter = THREE.LinearFilter; tex.generateMipmaps = false; tex.needsUpdate = true;
    const mat = toon ? new THREE.MeshToonMaterial({ map: albedo, alphaMap: tex, alphaTest: 0.5, gradientMap: gradTex })
      : new THREE.MeshStandardMaterial({ map: albedo, alphaMap: tex, alphaTest: 0.5, roughness: 0.96, metalness: 0 });
    mat.onBeforeCompile = (sh) => {
      Object.assign(sh.uniforms, soilU);
      sh.fragmentShader = 'uniform vec4 uHeat; uniform vec3 uLampC; uniform vec3 uGoldC; uniform vec3 uGhostC; uniform float uRim;\n' +
        sh.fragmentShader.replace('#include <alphatest_fragment>', `#include <alphatest_fragment>
  {
    vec4 sk = texture2D( alphaMap, vAlphaMapUv );
    float rim = smoothstep( 0.5, 0.96, sk.g );
    diffuseColor.rgb *= mix( uRim, 1.0, rim * rim );
    diffuseColor.rgb = mix( diffuseColor.rgb, uGhostC, sk.r * 0.72 );
    float h = sk.b * 1.25;
    diffuseColor.rgb *= uHeat.x + uHeat.y * h;
    diffuseColor.rgb = mix( diffuseColor.rgb, diffuseColor.rgb * uLampC * 1.7, min( uHeat.w, uHeat.z * h ) );
    diffuseColor.rgb = mix( diffuseColor.rgb, diffuseColor.rgb * uGoldC * 2.0, min( 0.5, 0.5 * sk.a ) );
  }`);
    };
    mat.customProgramCacheKey = () => 'skarb-soil' + (toon ? 't' : 's');
    const mesh = new THREE.Mesh(M.soilPlaneGeo(ext, hi ? 6 : 3), mat);
    mesh.receiveShadow = hi; mesh.castShadow = hi; mesh.matrixAutoUpdate = false;
    gridGroup.add(mesh);
    soil = { ext, mppu, MW, MH, data, tex, albedo, mat, mesh, earth: mk(), ghost: mk(), heat: mk(), gold: mk(), img: {}, emb: [] };
    soilD.earth = soilD.ghost = soilD.heat = true;
    if (FX.embeds && hi) buildSoilEmbeds();
  }
  function disposeSoil() {
    if (!soil) return;
    soil.albedo.dispose(); soil.tex.dispose(); soil.mat.dispose();
    soil = null;
  }
  const mX = (X) => (X - soil.ext.X0) * soil.mppu, mY = (Y) => (soil.ext.Y1 - Y) * soil.mppu;
  /** М'який мазок ходу (кілька проходів «darken»: обідок → серцевина). */
  function seg(g, X1, Y1, X2, Y2, hw, cap, passes) {
    g.lineCap = cap;
    for (const [w, v] of passes) {
      g.strokeStyle = `rgb(${v},${v},${v})`; g.lineWidth = 2 * (hw + w) * soil.mppu;
      g.beginPath(); g.moveTo(mX(X1), mY(Y1)); g.lineTo(mX(X2) + 0.01, mY(Y2)); g.stroke();
    }
  }
  function dot(g, X, Y, R, passes) {
    for (const [w, v] of passes) { g.fillStyle = `rgb(${v},${v},${v})`; g.beginPath(); g.arc(mX(X), mY(Y), (R + w) * soil.mppu, 0, Math.PI * 2); g.fill(); }
  }
  const EARTH_P = [[FEATHER, 222], [FEATHER * 0.45, 170], [0, 0]], GHOST_P = [[0.04, 120], [0, 255]];
  const opensTo = (x, y, d) => {
    const c = S.cards.get(key(x, y)) || leaving.get(key(x, y)) || S.goals.find((g) => g.open && g.m && g.x === x && g.y === y);
    if (c) return !!(c.m & BIT[d]);
    return G.shaft.has(key(x, y)) && (d === 'S' || d === 'N');
  };
  /** Форма ходу карти: середина (якщо c), відкриті краї до межі клітинки; p — прогрес (0…1) для анімацій. */
  function drawShape(g, x, y, m, cc, p, passes, solo = false) {
    if (!m || p <= 0.001) return;
    const X = x, Y = -y;
    // глуха карта теж вирізана повністю — посередині її перегороджує валун (і осипище)
    dot(g, X, Y, HW * (cc ? 1.12 : 1.05) * p, passes);
    for (const d of DIRS) if (m & BIT[d]) {
      const dx = STEP[d][0], dy = -STEP[d][1];
      const joins = !solo && opensTo(x + STEP[d][0], y + STEP[d][1], OPP[d]);
      const r = (joins ? 0.5 : 0.5 - HW) * p;         // круглий кінець: у сусідній хід або рівно до межі клітинки
      seg(g, X, Y, X + dx * r, Y + dy * r, HW, 'round', passes);
    }
  }
  function drawEarth() {
    const g = soil.earth;
    g.globalCompositeOperation = 'source-over'; g.fillStyle = '#fff'; g.fillRect(0, 0, soil.MW, soil.MH);
    g.globalCompositeOperation = 'darken';
    // шахта колодязя: від колодязя до поверхні
    const st = G.start;
    const shaftTop = Math.max(...[...G.shaft].map((k) => -Number(k.split(',')[1])), -st[1]);
    if (G.shaft.size) seg(g, st[0], -st[1], st[0], shaftTop + 0.7, SHAFT_HW, 'butt', EARTH_P);
    const one = (c, kk) => { const fx = cellFx.get(kk); drawShape(g, c.x, c.y, c.m, c.c, fx?.p ?? 1, EARTH_P); };
    for (const [kk, c] of S.cards) one(c, kk);
    for (const [kk, c] of leaving) one(c, kk);
    for (const gl of S.goals) if (gl.open && gl.m) one(gl, key(gl.x, gl.y));
    for (const [kk, fx] of cellFx) if (fx.hole > 0.01) { const [x, y] = kk.split(',').map(Number); dot(g, x, -y, 0.2 * fx.hole, EARTH_P); }
  }
  function drawGhost() {
    const g = soil.ghost;
    g.globalCompositeOperation = 'source-over'; g.fillStyle = '#000'; g.fillRect(0, 0, soil.MW, soil.MH);
    g.globalCompositeOperation = 'lighten';
    if (ghost && ghost.m) drawShape(g, ghost.x, ghost.y, ghost.m, ghost.c, 1, GHOST_P, true);
  }
  function drawHeat() {
    for (const [g, gold] of [[soil.heat, false], [soil.gold, true]]) {
      g.globalCompositeOperation = 'source-over'; g.fillStyle = '#000'; g.fillRect(0, 0, soil.MW, soil.MH);
      g.globalCompositeOperation = 'lighter';
      for (const h of heat) {
        if (!!h.gold !== gold) continue;
        const R = HT.R * (gold ? 1.1 : 1) * soil.mppu, cx = mX(h.X), cy = mY(h.Y), a = Math.min(1, h.w / 1.25);
        const gr = g.createRadialGradient(cx, cy, 0, cx, cy, R);
        gr.addColorStop(0, `rgba(255,255,255,${a})`); gr.addColorStop(0.25, `rgba(255,255,255,${a * 0.56})`);
        gr.addColorStop(0.5, `rgba(255,255,255,${a * 0.25})`); gr.addColorStop(0.75, `rgba(255,255,255,${a * 0.06})`); gr.addColorStop(1, 'rgba(255,255,255,0)');
        g.fillStyle = gr; g.fillRect(cx - R, cy - R, 2 * R, 2 * R);
      }
    }
  }
  function soilUpload() {
    if (!soil) return;
    const { MW, MH, data, img } = soil;
    if (soilD.earth) { drawEarth(); img.e = soil.earth.getImageData(0, 0, MW, MH).data; }
    if (soilD.ghost) { drawGhost(); img.g = soil.ghost.getImageData(0, 0, MW, MH).data; }
    if (soilD.heat) { drawHeat(); img.h = soil.heat.getImageData(0, 0, MW, MH).data; img.a = soil.gold.getImageData(0, 0, MW, MH).data; }
    const E = img.e, Gh = img.g, Hh = img.h, A = img.a;
    for (let row = 0; row < MH; row++) {
      const src = row * MW * 4, dst = (MH - 1 - row) * MW * 4;
      for (let i = 0; i < MW * 4; i += 4) {
        data[dst + i] = Gh[src + i]; data[dst + i + 1] = E[src + i]; data[dst + i + 2] = Hh[src + i]; data[dst + i + 3] = A[src + i];
      }
    }
    soil.tex.needsUpdate = true;
    if (soilD.earth && soil.emb.length) soilEmbedsVisible();
    soilD.earth = soilD.ghost = soilD.heat = false;
  }
  /** Вкраплення (камінці, кістки, черепки, коріння) у суцільному ґрунті — ховаються, коли їх перетинає хід. */
  function buildSoilEmbeds() {
    const goalSet = G.goals.map(([x, y]) => [x, -y]);
    const kinds = { pebble: [], root: [], bone: [], pot: [] };
    for (let k = 0; k < 150; k++) {
      const a = hash(k, 501), b = hash(k, 503), c = hash(k, 509);
      const X = G.x0 - 0.4 + a * (G.x1 - G.x0 + 0.8), Y = G.botY + 0.9 + b * (G.surfY - G.botY - 1.1), y = -Y;
      if (goalSet.some(([gx, gy]) => Math.abs(gx - X) < 0.6 && Math.abs(gy - Y) < 0.6)) continue;
      if (Math.abs(X - G.start[0]) < 0.35 && Y > -G.start[1] - 0.4) continue;
      let kind = 'pebble';
      if (y <= -2.6 && c < 0.35) kind = 'root';
      else if (y >= 0 && c > 0.9) kind = 'bone';
      else if (y >= -3 && y <= 2 && c > 0.82) kind = 'pot';
      else if (c > 0.55) continue;
      kinds[kind].push({ X, Y, kind, rz: hash(k, 511) * 6.28, rx: hash(k, 513) * 3, ry: hash(k, 517) * 3,
        s: kind === 'pebble' ? 0.03 + hash(k, 519) * 0.035 : kind === 'bone' ? 0.6 + hash(k, 519) * 0.3 : kind === 'root' ? 0.9 + hash(k, 519) * 0.6 : 0.85 + hash(k, 519) * 0.3 });
    }
    const geos = { pebble: M.rockGeo(), root: M.rootGeo(), bone: M.boneGeo(), pot: M.potGeo() };
    for (const k of Object.keys(kinds)) {
      const list = kinds[k];
      if (!list.length) { geos[k].dispose(); continue; }
      const mesh = inst(geos[k], list.length, { color: k === 'pebble', recv: true, parent: gridGroup });
      list.forEach((e, n) => {
        e.mesh = mesh; e.idx = n; soil.emb.push(e);
        if (k === 'pebble') mesh.setColorAt(n, gradeC(_c.set(-e.Y >= 2 ? '#c9b48a' : hash(n, 5) < 0.5 ? '#7c7468' : '#a08a6a').multiplyScalar(0.75 + hash(n, 9) * 0.4)));
      });
      finish(mesh, list.length);
    }
  }
  function soilEmbedsVisible() {
    const { MW, MH, img } = soil;
    for (const e of soil.emb) {
      const px = Math.round(mX(e.X)), py = Math.round(mY(e.Y));
      const v = px >= 0 && py >= 0 && px < MW && py < MH ? img.e[(py * MW + px) * 4] : 255;
      if (v < 235) e.mesh.setMatrixAt(e.idx, ZERO);
      else setM(e.mesh, e.idx, e.X, e.Y, e.kind === 'pebble' ? -0.01 : 0.004, e.kind === 'pebble' ? e.rx : 0, e.kind === 'pebble' ? e.ry : 0, e.kind === 'root' ? (e.rz - 3.14) * 0.15 : e.rz, e.s);
      e.mesh.instanceMatrix.needsUpdate = true;
    }
  }

  function disposeGrid() {
    if (!gridGroup) return;
    disposeSoil();
    gridGroup.traverse((o) => { if (o.isInstancedMesh) o.dispose(); if (o.geometry) o.geometry.dispose(); });
    scene.remove(gridGroup);
    gridGroup = null; wall = supports = lanternF = lanternG = glows = boulders = hlFrames = altIcons = null;
    junctions = tracks = ladders = rubble = grass = hlGlow = null; embMeshes = []; embList = []; embOf = null;
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

    // Стіна розрізу: classic — грудки; інші стилі — суцільна мальована площина з вирізаними ходами.
    holes = new Uint16Array(G.n);
    if (SOIL) { nLumps = 0; buildSoil(); }
    else {
    nLumps = G.n * 9;
    wall = inst(STY.lump ? M.lumpGeo(STY.lump) : M.lumpGeo(), nLumps, { color: true, cast: !toon, recv: true, parent: gridGroup });
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
        if (FX.noise) {
          // Шари з «живою» землею: шум за світовими координатами, плавний перехід між шарами, дрібний розкид.
          const X = bX[i], Y = bY[i], amp = FX.noise;
          const nz = fbm(X * 0.9, Y * 0.9) * 0.65 + fbm(X * 2.7 + 13, Y * 2.7) * 0.35 - 0.5;
          const ly = Math.floor(j / 3), edge = ly === 0 ? -1 : ly === 2 ? 1 : 0;
          _c.copy(base);
          if (edge && M.strataHex(y + edge) !== M.strataHex(y)) _c.lerp(M.col(M.strataHex(y + edge)), 0.22 + 0.12 * r(8));
          const n = 1 + nz * 2 * amp + (r(7) - 0.5) * 0.05;
          gradeC(_c);
          bCol[i * 3] = _c.r * n * (1 + nz * 0.05); bCol[i * 3 + 1] = _c.g * n; bCol[i * 3 + 2] = _c.b * n * (1 - nz * 0.06);
        } else {
          const n = 1 + (r(7) - 0.5) * 0.08;
          bCol[i * 3] = base.r * n; bCol[i * 3 + 1] = base.g * n; bCol[i * 3 + 2] = base.b * n;
        }
      }
    }
    wall.count = nLumps; wall.visible = true;
    if (wall.userData.ol) { wall.userData.ol.count = nLumps; wall.userData.ol.visible = true; }
    crack = new Float32Array(nLumps); rimK = new Float32Array(nLumps).fill(1);
    if (FX.embeds && hi) buildEmbeds();
    }

    // Нерухоме.
    const st = M.buildStatic(g, STY.bg, STY === STYLES.classic ? {} : { ...(STY.sky || {}), soil: SOIL, lump: STY.lump, backMul: STY.backMul, halo: FX.halo, decor: FX.decor, outline: toon });
    grade(st.lit); grade(st.sky); grade(st.craneArm); grade(st.craneRope);
    const lit = new THREE.Mesh(st.lit, matVC);
    lit.castShadow = lit.receiveShadow = hi; lit.matrixAutoUpdate = false;
    const glowM = new THREE.Mesh(st.glow, matGlowVC); glowM.matrixAutoUpdate = false;
    const sky = new THREE.Mesh(st.sky, matSky); sky.matrixAutoUpdate = false; sky.renderOrder = -10;
    craneArm = new THREE.Mesh(st.craneArm, matVC); craneArm.position.copy(st.pivot); craneArm.castShadow = hi;
    craneRope = new THREE.Mesh(st.craneRope, matVC); craneRope.castShadow = hi;
    craneTip = st.tip.clone();
    gridGroup.add(lit, glowM, sky, craneArm, craneRope);
    if (st.halo) { const h = new THREE.Mesh(st.halo, matAdd); h.matrixAutoUpdate = false; h.renderOrder = 3; gridGroup.add(h); }
    if (st.outline) { const o = new THREE.Mesh(st.outline, matOLs); o.matrixAutoUpdate = false; gridGroup.add(o); outlineOf(craneArm, matOLs); outlineOf(craneRope, matOL); }
    G.pivot = st.pivot.clone(); G.chimney = st.chimney.clone();

    supports = inst(M.supportGeo(), G.n * 4 + 8, { cast: true, recv: true, parent: gridGroup });
    if (FX.props && hi) {
      junctions = inst(M.junctionGeo(), G.n, { cast: true, recv: true, parent: gridGroup, ol: hi ? matOL : null });
      tracks = inst(M.trackGeo(), G.n * 3, { recv: true, parent: gridGroup });
      if (hi) ladders = inst(M.ladderGeo(), G.n * 3, { recv: true, parent: gridGroup });
      rubble = inst(M.rockGeo(), G.n * 4, { color: true, cast: hi, recv: true, parent: gridGroup });
    }
    if (FX.grass && hi) buildGrass();
    lanternF = inst(M.lanternFrameGeo(), G.n, { parent: gridGroup });
    lanternG = inst(M.lanternGlassGeo(), G.n, { mat: matGlowVC, color: true, parent: gridGroup });
    glows = inst(hi && STY !== STYLES.classic ? M.glowGeo(16, true) : M.glowGeo(), G.n * 3 + 8, { mat: matAdd, color: true, parent: gridGroup, order: 3 });
    boulders = inst(M.boulderGeo(), G.n, { cast: true, recv: true, parent: gridGroup, ol: hi ? matOL : null });
    hlFrames = inst(SOIL ? M.frameRoundGeo() : M.frameGeo(), G.n, { mat: matHL, color: true, parent: gridGroup, order: 5 });
    hlGlow = FX.softHL && (hi || SOIL) ? inst(SOIL ? M.glowRoundGeo() : M.frameSoftGeo(), G.n, { mat: matHLA, color: true, parent: gridGroup, order: 4 }) : null;
    altIcons = inst(M.altIconGeo(), G.n, { mat: matHL, color: true, parent: gridGroup, order: 6 });

    // Місяць: зліва згори спереду; тінь — лише на high, 1024², рамка — усе поле з поверхнею.
    const cx = (G.x0 + G.x1) / 2, cy = (G.surfY + G.botY) / 2 + 0.6;
    moon.target.position.set(cx, cy, 0);
    moon.position.set(cx - 7, cy + 18, 10);
    if (rimL) { rimL.target.position.set(cx, G.surfY, 0); rimL.position.set(cx + 12, G.surfY + 5, -14); }
    if (moon.castShadow) {
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
    if (FX.rim || FX.crack) computeRim();
  }
  /** Темніший обідок довкола прокопаного (вирізьблена глибина) і потріскані грудки довкола глухих кутів. */
  function computeRim() {
    if (!wall) return;
    const GW = G.W * 3, GH = G.H * 3;
    const hid = (gx, gy) => {
      if (gx < 0 || gy < 0 || gx >= GW || gy >= GH) return 0;
      const ci = Math.floor(gy / 3) * G.W + Math.floor(gx / 3), j = (gy % 3) * 3 + (gx % 3);
      return (holes[ci] >> j) & 1;
    };
    crack.fill(0);
    if (FX.crack) for (const c of S.cards.values()) if (!c.c && c.m) {
      const ci = cellIndex(c.x, c.y); if (ci < 0) continue;
      for (let j = 0; j < 9; j++) if (!((holes[ci] >> j) & 1)) crack[ci * 9 + j] = 1;
    }
    if (!FX.rim) return;
    for (let ci = 0; ci < G.n; ci++) {
      const cx = ci % G.W, cy = Math.floor(ci / G.W);
      for (let j = 0; j < 9; j++) {
        const gx = cx * 3 + (j % 3), gy = cy * 3 + Math.floor(j / 3);
        const n4 = hid(gx - 1, gy) + hid(gx + 1, gy) + hid(gx, gy - 1) + hid(gx, gy + 1);
        const nd = hid(gx - 1, gy - 1) + hid(gx + 1, gy - 1) + hid(gx - 1, gy + 1) + hid(gx + 1, gy + 1);
        rimK[ci * 9 + j] = n4 ? 0.74 - 0.05 * Math.min(2, n4 - 1) : nd ? 0.88 : 1;
      }
    }
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
    // ліхтар у руці того, хто ходить
    const ts = turnSeat();
    if (ts != null) { figureHomes(); const h = figHome.get(ts); if (h) heat.push({ X: h.x - 0.12, Y: h.y + 0.3, w: FX.lights === 'spot' ? 1.1 : 0.75, gold: false }); }
  }
  function turnSeat() {
    if (!FX.figLamp || !view || view.turn == null || view.phase === 'over') return null;
    return S.players.some((p) => p.seat === view.turn) ? view.turn : null;
  }
  const LAMP = new THREE.Color(PAL.lamp), GOLD = new THREE.Color(PAL.gold);
  function writeWallColors() {
    if (!wall) { soilD.heat = true; dirty = true; return; }
    const R = HT.R;
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
      const k = (HT.base + HT.gain * w) * rimK[i];
      _c.setRGB(bCol[i * 3] * k, bCol[i * 3 + 1] * k, bCol[i * 3 + 2] * k);
      _c.lerp(LAMP, Math.min(HT.lampMax, HT.lamp * w) * rimK[i]);
      if (gw > 0) _c.lerp(GOLD, Math.min(0.4, 0.35 * gw));
      wall.setColorAt(i, _c);
    }
    wall.instanceColor.needsUpdate = true;
  }

  function writeLump(i, s = 1, ox = 0, oy = 0, oz = 0) {
    if (embOf && embOf[i] >= 0) writeEmbed(embList[embOf[i]], s, ox, oy, oz);
    if (s <= 0.0005) { wall.setMatrixAt(i, ZERO); return; }
    const k = crack[i];
    if (k) {
      // потріскана грудка біля глухого кута: зсунута, перекошена, трохи менша
      const a = hash(i, 71) - 0.5, b = hash(i, 73) - 0.5;
      _eu.set(bRx[i] + a * 0.22, bRy[i] + b * 0.22, bRz[i] + a * 0.3, 'XYZ');
      wall.setMatrixAt(i, _m4.compose(_pv.set(bX[i] + ox + b * 0.025, bY[i] + oy - Math.abs(a) * 0.03, bZ[i] + oz + 0.02 + a * 0.02), _q.setFromEuler(_eu),
        _sv.set(bS[i] * s * 0.93, bS[i] * s * 0.93, Math.max(s, 0.001))));
      return;
    }
    _eu.set(bRx[i], bRy[i], bRz[i], 'XYZ');
    wall.setMatrixAt(i, _m4.compose(_pv.set(bX[i] + ox, bY[i] + oy, bZ[i] + oz), _q.setFromEuler(_eu), _sv.set(bS[i] * s, bS[i] * s, Math.max(s, 0.001))));
  }
  function wallDirty() { if (wall) wall.instanceMatrix.needsUpdate = true; else { soilD.earth = true; dirty = true; } }
  function writeCellLumps(ci, kk) {
    if (!wall) { soilD.earth = true; dirty = true; return; }
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
    if (!wall) { soilD.earth = true; dirty = true; return; }
    for (let y = G.y0; y <= G.y1; y++) for (let x = G.x0; x <= G.x1; x++) writeCellLumps(cellIndex(x, y), key(x, y));
    wallDirty();
  }

  // Кріплення, ліхтарі, плями світла, валуни — щоразу наново (їх ≤ кількох сотень).
  const lampSlots = [];   // { gi (індекс плями), gb (база кольору), glass i, lit }
  function writeProps(t = clockT) {
    let ns = 0, nl = 0, ng = 0, nb = 0, nj = 0, nt = 0, nd = 0, nr = 0;
    lampSlots.length = 0;
    const one = (c, kk, isGoal) => {
      const fx = cellFx.get(kk);
      const ss = fx?.sup ?? 1, ls = fx?.lamp ?? 1, bs = fx?.boulder ?? 1;
      if (c.m) for (const d of DIRS) {
        if (!(c.m & BIT[d]) || ns >= supports.instanceMatrix.count) continue;
        const X = c.x + STEP[d][0] * L, Y = -c.y - STEP[d][1] * L;
        if (ss > 0.001) setM(supports, ns++, X, Y, 0, 0, 0, (d === 'N' || d === 'S') ? Math.PI / 2 : 0, ss, ss * (SOIL ? 1.14 : 1), ss);
      }
      if (tracks && c.m && ss > 0.001) {
        const EW = c.m & (BIT.E | BIT.W), NS = c.m & (BIT.N | BIT.S);
        const floor = -c.y - L / 2;
        for (const d of DIRS) if (c.m & BIT[d]) {
          if ((d === 'E' || d === 'W') && nt < tracks.instanceMatrix.count) setM(tracks, nt++, c.x + STEP[d][0] * L, floor, 0, 0, 0, 0, 1, ss, 1);
          if ((d === 'N' || d === 'S') && ladders && c.kind !== 'start' && nd < ladders.instanceMatrix.count) setM(ladders, nd++, c.x, -c.y - STEP[d][1] * L, 0, 0, 0, 0, 1, ss, 1);
        }
        if (c.c && EW && nt < tracks.instanceMatrix.count) setM(tracks, nt++, c.x, floor, 0, 0, 0, 0, 1, ss, 1);
        if (c.c && NS && !EW && ladders && nd < ladders.instanceMatrix.count) setM(ladders, nd++, c.x, -c.y, 0, 0, 0, 0, 1, ss, 1);
        let ne = 0; for (const d of DIRS) if (c.m & BIT[d]) ne++;
        if (c.c && ne >= 3 && kk !== key(G.start[0], G.start[1])) setM(junctions, nj++, c.x, -c.y, 0, 0, 0, 0, ss);
      }
      if (rubble && !c.c && c.m && bs > 0.001) {
        // глухий кут: осипище довкола валуна й на кінцях огризків
        let q = 0;
        const put = (x, y, z, sz) => { if (nr >= rubble.instanceMatrix.count) return; const h = hash(c.x * 7 + q, c.y * 5 + q++);
          setM(rubble, nr, x, y + sz * 0.6, z, h * 6, h * 4, h * 3, sz * bs, sz * 0.7 * bs, sz * bs);
          rubble.setColorAt(nr++, gradeC(_c.set(h < 0.5 ? '#6f6258' : '#8a7462').multiplyScalar(0.8 + h * 0.4))); };
        const floor = -c.y - L / 2;
        for (const s2 of [-1, 1]) put(c.x + s2 * (0.1 + hash(c.x, c.y + s2) * 0.04), floor, -0.08 + hash(c.y, c.x + s2) * 0.1, 0.035 + hash(s2, c.x) * 0.02);
        for (const d of DIRS) if (c.m & BIT[d]) put(c.x + STEP[d][0] * L * 0.8, -c.y - STEP[d][1] * L - L / 2, -0.12, 0.04);
      }
      if (!c.c && c.m && bs > 0.001) setM(boulders, nb++, c.x, -c.y + 0.01, -0.16, hash(c.x, c.y) * 3, hash(c.y, c.x) * 3, 0.3, bs);
      if (c.c && c.m && ls > 0.001) {
        const lin = isGoal || S.linked.has(kk);
        const X = c.x, Y = -c.y + L / 2;
        setM(lanternF, nl, X, Y, M.TUN_Z, 0, 0.4, 0, ls);
        setM(lanternG, nl, X, Y, M.TUN_Z, 0, 0.4, 0, ls);
        const gb = lin ? 1 : 0.16;
        lanternG.setColorAt(nl, _c.set(lin ? '#ffe7a6' : '#6a4a22').multiplyScalar(lin ? 1.35 * ls : ls));
        setM(glows, ng, X, -c.y + 0.04, M.BACK_Z + 0.01, 0, 0, 0, STY.glowR * ls);
        lampSlots.push({ gi: ng, gb: STY.glowK * gb, glass: nl, lin });
        glows.setColorAt(ng++, _c.copy(LAMP).multiplyScalar(STY.glowK * gb));
        setM(glows, ng, X, Y - 0.122, M.TUN_Z + 0.06, 0, 0, 0, 0.15 * ls);
        lampSlots.push({ gi: ng, gb: 0.9 * gb, glass: -1, lin });
        glows.setColorAt(ng++, _c.copy(LAMP).multiplyScalar(0.9 * gb));
        if (STY.pool && lin && hi) {
          // «пляма світла» на лиці розрізу довкола ліхтаря (адитивна наліпка)
          setM(glows, ng, X, -c.y + 0.02, 0.035, 0, 0, 0, STY.pool[0] * ls);
          lampSlots.push({ gi: ng, gb: STY.pool[1], glass: -1, lin });
          glows.setColorAt(ng++, _c.copy(LAMP).multiplyScalar(STY.pool[1]));
        }
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
        setM(glows, ng, g.x, -g.y - 0.08, 0.05, 0, 0, 0, 0.62 * Math.sqrt(STY.gold) * (0.75 + 0.25 * gs));
        glows.setColorAt(ng++, _c.copy(GOLD).multiplyScalar(0.26 * Math.min(gs, 1.6) * Math.min(1.6, STY.gold)));
        if (STY.gold > 1.2 && ng < glows.instanceMatrix.count) {
          // широке м'яке сяйво позаду скрині — на задній стінці
          setM(glows, ng, g.x, -g.y - 0.05, M.BACK_Z + 0.02, 0, 0, 0, 1.25 * Math.sqrt(STY.gold) * (0.7 + 0.3 * Math.min(gs, 1.4)));
          glows.setColorAt(ng++, _c.copy(GOLD).multiplyScalar(0.2 * Math.min(gs, 1.6) * STY.gold));
        }
      }
    }
    finish(supports, ns); finish(lanternF, nl); finish(lanternG, nl); finish(glows, ng); finish(boulders, nb);
    if (tracks) { finish(tracks, nt); finish(junctions, nj); finish(rubble, nr); if (ladders) finish(ladders, nd); }
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
    chestLight.intensity = li * STY.gold;
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
  const _lp = new THREE.Vector3();
  function placeFigures(now = performance.now(), animate = false, t = clockT) {
    figureHomes();
    let n = 0, jumping = false, np = 0, nbk = 0, lampAt = null;
    const ts = turnSeat(), bobOn = FX.bob && bgAnim;
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
      // ледь помітне «дихання» на місці (лише high без reducedMotion)
      const bob = bobOn && !hop && !sit ? 0.006 * (1 + Math.sin(t * 2.3 + p.seat * 1.9)) : 0, rz = bobOn && !hop ? 0.035 * Math.sin(t * 1.1 + p.seat * 2.3) : 0;
      const Y = cur.y + bob;
      setM(figShirt, n, cur.x, Y, cur.z, rx, ry, rz, FIG_S, FIG_S * sy, FIG_S);
      setM(figBody, n, cur.x, Y, cur.z, rx, ry, rz, FIG_S, FIG_S * sy, FIG_S);
      figShirt.setColorAt(n, gradeC(_c.set(SEAT_COLOR[p.color] ?? SEAT_COLOR[p.seat % 10] ?? '#9aa3ad')));
      // кирка: на плечі або лежить поруч (зламана — надвоє)
      _eu.set(rx, ry, rz, 'YXZ'); _q.setFromEuler(_eu);
      if (sit) {
        const off = new THREE.Vector3(0.13, 0.02, 0.07).multiplyScalar(FIG_S).applyQuaternion(_q);
        if (figBroken) setM(figBroken, nbk++, cur.x + off.x, cur.y + 0.002, cur.z + off.z + 0.02, 0, ry + 0.3, 0, FIG_S);
        else setM(figPick, np++, cur.x + off.x, cur.y + 0.012, cur.z + off.z, 0.1, ry + 0.3, Math.PI / 2, FIG_S);
      } else {
        const off = new THREE.Vector3(0.075, 0.25, -0.04).multiplyScalar(FIG_S).applyQuaternion(_q);
        setM(figPick, np++, cur.x + off.x, Y + off.y, cur.z + off.z, 0, ry, 0.62, FIG_S);
      }
      if (p.seat === ts && figLamp) {
        const off = new THREE.Vector3(-0.115, sit ? 0.2 : 0.3, 0.05).multiplyScalar(FIG_S).applyQuaternion(_q);
        lampAt = _lp.set(cur.x + off.x, Y + off.y, cur.z + off.z);
      }
      n++;
    }
    finish(figShirt, n); finish(figBody, n); finish(figPick, np); if (figBroken) finish(figBroken, nbk);
    if (figLamp) {
      figLamp.visible = figLamp.halo.visible = !!lampAt;
      if (lampAt) {
        const s = FIG_S * 0.8, sw = bobOn ? 0.12 * Math.sin(t * 1.7) : 0;
        figLamp.position.copy(lampAt); figLamp.scale.setScalar(s); figLamp.rotation.set(0, 0.4, sw);
        figLamp.halo.position.set(lampAt.x - Math.sin(sw) * 0.1 * s, lampAt.y - 0.122 * s, lampAt.z - 0.04);
        figLamp.halo.scale.setScalar(0.34 + (bgAnim ? 0.02 * Math.sin(t * 9.1) : 0));
      }
    }
    updateLights(lampAt, t);
    return jumping;
  }
  /** Справжні ліхтарі (high): A — за ліхтарем у руці, B — найсвіжіший з'єднаний ліхтар ходу. */
  function updateLights(lampAt, t = clockT) {
    if (!lampA) return;
    const fl = bgAnim ? 0.92 + 0.06 * Math.sin(t * 7.7) + 0.04 * Math.sin(t * 12.9) : 1;
    if (lampAt) {
      if (FX.lights === 'spot') {
        lampA.position.set(lampAt.x, lampAt.y - 0.05, lampAt.z + 0.55);
        lampA.target.position.set(lampAt.x + 0.05, lampAt.y - 0.3, -0.45);
        lampA.target.updateMatrixWorld();
        lampA.intensity = 9 * fl;
      } else { lampA.position.set(lampAt.x, lampAt.y - 0.1, lampAt.z + 0.15); lampA.intensity = 0.8 * fl; }
    } else lampA.intensity = 0;
    let last = null;
    for (let i = S.order.length - 1; i >= 0 && !last; i--) {
      const c = S.cards.get(S.order[i]);
      if (c && c.c && c.m && c.kind === 'path' && S.linked.has(S.order[i])) last = c;
    }
    if (last) { lampB.position.set(last.x, -last.y + 0.05, 0.12); lampB.intensity = (FX.lights === 'spot' ? 1.4 : 0.6) * fl; }
    else lampB.intensity = 0;
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
    const pulse = reduced || FX.softHL ? 0 : Math.sin(t * 4.2);
    for (let i = 0; i < hlItems.length && n < hlFrames.instanceMatrix.count; i++) {
      const it = hlItems[i];
      const hov = hover && hover.kind === it.kind && hover.x === it.x && hover.y === it.y;
      const c = legal.kind === 'rockfall' ? C_BAD : it.alt ? C_ALT : C_LEGAL;
      if (hlGlow) {
        // тонка рамка + м'який ореол (адитивно); повільна хвиля яскравості біжить полем
        const w = reduced ? 0.5 : 0.5 + 0.5 * Math.sin(t * 2.6 - (it.x + it.y) * 0.7);
        const s = (hov ? 1.05 : 1) * (1 + 0.012 * w);
        setM(hlFrames, n, it.x, -it.y, hov ? 0.12 : 0.05, 0, 0, 0, s * 0.97, s * 0.97, 1);
        hlFrames.setColorAt(n, _c.copy(c).multiplyScalar(hov ? 1.25 : 0.9 + 0.15 * w));
        setM(hlGlow, n, it.x, -it.y, hov ? 0.11 : 0.045, 0, 0, 0, s, s, 1);
        hlGlow.setColorAt(n++, _c.copy(c).multiplyScalar((it.alt ? 0.35 : 0.6) * (hov ? 1.6 : 0.55 + 0.6 * w)));
      } else {
        const s = (hov ? 1.07 : 1) * (1 + 0.025 * (reduced ? 0 : Math.sin(t * 4.2 + i * 0.5)));
        setM(hlFrames, n, it.x, -it.y, hov ? 0.13 : 0.04, 0, 0, 0, s, s, 1);
        hlFrames.setColorAt(n++, _c.copy(c).multiplyScalar(hov ? 1.25 : 1));
      }
      if (it.alt) {
        setM(altIcons, na, it.x, -it.y, hov ? 0.16 : 0.07, 0, 0, reduced ? 0 : -t * 0.8, 1);
        altIcons.setColorAt(na++, _c.copy(C_ALT).multiplyScalar(1.3));
      }
    }
    finish(hlFrames, n); finish(altIcons, na); if (hlGlow) finish(hlGlow, n);
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
    if (!wall) {
      if (ghost) ghost.m = at.alt ? legal.ghost.mAlt : legal.ghost.m;
      soilD.ghost = true;
    }
    if (ghost && wall) {
      for (let j = 0; j < 9; j++) if ((ghost.mask >> j) & 1) {
        const i = ghost.ci * 9 + j;
        _eu.set(bRx[i], bRy[i], bRz[i], 'XYZ');
        ghostLumps.setMatrixAt(n++, _m4.compose(_pv.set(bX[i], bY[i], bZ[i] + 0.004), _q.setFromEuler(_eu), _sv.set(bS[i], bS[i], 1)));
      }
    }
    if (ghost) {
      ghostBoulder.visible = !ghost.c;
      if (!ghost.c) ghostBoulder.position.set(ghost.x, -ghost.y + 0.01, -0.12);
    } else ghostBoulder.visible = false;
    finish(ghostLumps, n);
    if (wall) {
      if (prev && (!ghost || prev.ci !== ghost.ci)) writeCellLumps(prev.ci, null);
      if (ghost) writeCellLumps(ghost.ci, null);
      if (prev || ghost) wallDirty();
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
    if (scene.fog) { scene.fog.near = d + STY.fog.near; scene.fog.far = d + STY.fog.far; }
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
  /** Що вмістити: уся висота (від смуги поверхні з хатою до низу поля), а по ширині — скільки дозволяє формат хоста:
   *  не менше ядра (колодязь…мітки, §11) і не більше всього поля 17 клітинок. Широкий ПК — усе поле; телефон — ядро. */
  function fitBox() {
    const portrait = W / H < 0.9;
    const surf = G ? G.surfY : 4.5, bot = G ? G.botY : -4.5;
    const y0 = bot - 0.15, y1 = surf + (portrait ? 1.0 : 1.95);
    const core = portrait ? [-1, 9] : [-1.5, 9.5], full = G ? [G.x0 - 0.5, G.x1 + 0.5] : [-4.5, 12.5];
    const cx = (core[0] + core[1]) / 2;
    const span = clamp((y1 - y0) * (W / H) * 0.97, core[1] - core[0], Math.max(core[1] - core[0], full[1] - full[0]));
    return { x0: cx - span / 2, x1: cx + span / 2, y0, y1 };
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
    parts.length = 0; finish(dust, 0); finish(flyCoins, 0); finish(fallRocks, 0); finish(moles, 0); if (sparks) finish(sparks, 0);
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
      } else if (q.kind === 'coin') {
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
      sup: 0, lamp: 0, boulder: 0, p: 0,
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
      const t = k * D; fx.t = t; fx.p = easeOut(k01(t, 220, 520));
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
      writeCellLumps(ci, kk); wallDirty();
      writeProps();
      if (k >= 1) { cellFx.delete(kk); flash.intensity = 0; writeCellLumps(ci, kk); writeProps(); wallDirty(); }
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
    const fx = { t: 0, sup: 0, lamp: 0, boulder: 1, p: 0,
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
      const t = k * D; fx.t = t; fx.p = easeIn(k01(t, t0lump, t0lump + 280));
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
        if (FX.props && !gold) spawnDust(g.x, -g.y - 0.15, 0.12, 14, 0.7, 0.45);
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
          for (let i = 0; i < 16; i++) {
            const a = Math.random() * Math.PI * 2, sp = 0.5 + Math.random() * 0.9;
            spawnSpark(new THREE.Vector3(g.x + (Math.random() - 0.5) * 0.3, -g.y - L / 2 + 0.3, 0.45), new THREE.Vector3(Math.cos(a) * sp, Math.abs(Math.sin(a)) * sp + 0.4, 0.3), 600 + Math.random() * 500);
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
      writeCellLumps(ci, kk); wallDirty();
      writeProps(); writeGoals();
      if (k >= 1) { cellFx.delete(kk); goalFx.delete(g.i); writeCellLumps(ci, kk); writeProps(); writeGoals(); wallDirty(); }
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
    const fx = { t: 0, sup: 1, lamp: 1, boulder: 1, p: 1,
      lump(j) {
        if (!((back >> j) & 1)) return null;
        const e = k01(fx.t, 380 + j * 18, 680 + j * 18);
        return e <= 0 ? [0, 0, 0, 0] : [backOut(e), 0, 0, (1 - e) * 0.08];
      } };
    cellFx.set(kk, fx);
    writeWallColors();
    let dusted = false, moved = false;
    const tw = tween(D, (k) => {
      const t = k * D; fx.t = t; fx.p = 1 - easeInOut(k01(t, 380, 700));
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
        if (FX.props) spawnDust(ev.x, -ev.y - 0.25, 0.15, 14, 1.0, 0.35);
        if (!reduced) shake = { t0: performance.now(), dur: 200, amp: 0.06 };
      }
      if (t >= 450 && !moved) { moved = true; placeFigures(performance.now(), true); }
      fx.sup = 1 - easeIn(k01(t, 360, 560));
      fx.lamp = 1 - k01(t, 340, 460);
      fx.boulder = 1 - k01(t, 400, 600);
      writeCellLumps(ci, kk); wallDirty();
      writeProps();
      if (k >= 1) {
        cellFx.delete(kk); leaving.delete(kk); finish(fallRocks, 0);
        writeCellLumps(ci, kk); writeProps(); wallDirty();
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
          m.open = e > 0.02; m.fx.hole = e;
          if (m.open && !m.dusted) { m.dusted = true; spawnDust(m.x, -m.y, 0.08, 4, 0.2, 0.5); }
          if (e > 0.001) setM(moles, n++, m.x, -m.y - 0.04, -0.3 + 0.55 * e, 0, Math.sin(t * 0.006 + m.t0) * 0.6 * e, Math.sin(t * 0.011 + m.t0) * 0.12, e * 1.5);
          writeCellLumps(m.ci, m.kk);
        }
        finish(moles, n);
        wallDirty();
        if (k >= 1) { for (const m of ml) { cellFx.delete(m.kk); writeCellLumps(m.ci, m.kk); } finish(moles, 0); wallDirty(); }
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
      warm.intensity = STY.amb[1] * (1 - 0.5 * Math.sin(Math.PI * k));
      if (k >= 1) warm.intensity = STY.amb[1];
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
    if (grass) grassU.value = bgAnim ? t : 0;
    if (smoke) writeSmoke(t);
    if (motes) writeMotes(t);
    if (FX.bob && bgAnim && !figJump.size && S.players.length) placeFigures(performance.now(), false, t);
    else if (lampA && bgAnim) updateLights(figLamp && figLamp.visible ? figLamp.position : null, t);
  }
  const C_SMOKE = new THREE.Color('#c4bcb2');
  function writeSmoke(t) {
    const C = G.chimney, n = 7, tt = bgAnim ? t : 0;
    for (let k = 0; k < n; k++) {
      const ph = (tt * 0.2 + k / n) % 1;
      const s = (0.05 + ph * 0.17) * (ph > 0.7 ? (1 - ph) / 0.3 : Math.min(1, ph * 6));
      setM(smoke, k, C.x + ph * 0.5 + 0.05 * Math.sin(tt * 0.9 + k * 1.7), C.y + ph * 1.5, C.z - ph * 0.3, k, k * 1.3, 0, Math.max(0.001, s), s * 0.85, s);
      smoke.setColorAt(k, _c.copy(C_SMOKE).multiplyScalar(0.7 + 0.5 * ph));
    }
    finish(smoke, n);
  }
  const C_FLY = new THREE.Color('#d8ff6a'), C_MOTE = new THREE.Color('#ffe2b0');
  function writeMotes(t) {
    let n = 0;
    if (FX.fireflies) {
      for (let k = 0; k < 36; k++) {
        const a = hash(k, 301), b = hash(k, 303), c = hash(k, 307);
        const blink = Math.pow(Math.max(0, Math.sin(t * (1.2 + c) + k * 2.1)), 3);
        if (blink < 0.02) continue;
        setM(motes, n, G.x0 - 1 + a * (G.x1 - G.x0 + 2) + 0.3 * Math.sin(t * 0.45 + k), G.surfY + 0.2 + b * 1.4 + 0.15 * Math.sin(t * 0.8 + k * 1.3),
          -0.3 - c * 3.2, 0, 0, 0, 0.05 + 0.05 * blink);
        motes.setColorAt(n++, _c.copy(C_FLY).multiplyScalar(0.25 + 0.9 * blink));
      }
    }
    if (FX.motes && heat.length) {
      // порошинки в світлі ліхтарів: повільно здіймаються й мерехтять
      for (let k = 0; k < 44 && n < motes.instanceMatrix.count; k++) {
        const h = heat[k % heat.length], a = hash(k, 311), b = hash(k, 313), c = hash(k, 317);
        const ph = (t * (0.04 + c * 0.05) + a) % 1;
        const x = h.X + (a - 0.5) * 1.2 + 0.08 * Math.sin(t * 0.7 + k), y = h.Y - 0.5 + ph * 1.0;
        const d = Math.hypot(x - h.X, y - h.Y), f = Math.max(0, 1 - d / 0.75) * Math.sin(Math.PI * ph);
        if (f < 0.03) continue;
        setM(motes, n, x, y, 0.05 + b * 0.3, 0, 0, 0, 0.014 + 0.012 * c);
        motes.setColorAt(n++, _c.copy(h.gold ? GOLD : C_MOTE).multiplyScalar(0.9 * f * (0.7 + 0.3 * Math.sin(t * 5 + k))));
      }
    }
    finish(motes, n);
  }
  function writeSparks(now, t) {
    let n = 0;
    const cap = sparks.instanceMatrix.count;
    if (bgAnim) for (const g of S.goals) {
      if (g.kind !== 'gold' || !(g.open || goalFx.get(g.i)?.chest)) continue;
      const fx = goalFx.get(g.i), gs = fx?.glow ?? 1;
      if (gs < 0.05) continue;
      for (let k = 0; k < 12 && n < cap; k++) {
        const a = hash(g.i * 31 + k, 5), b = hash(k, g.i + 7);
        const tw = Math.pow(Math.max(0, Math.sin(t * (2.2 + a * 1.6) + k * 2.4)), 6) * Math.min(1, gs);
        if (tw < 0.02) continue;
        setM(sparks, n, g.x + (a - 0.5) * 0.85, -g.y - L / 2 + 0.05 + b * 0.5, CH_Z + 0.12 + a * 0.1, 0, 0, t * 0.7 + k, 0.03 + 0.07 * tw);
        sparks.setColorAt(n++, _c.copy(GOLD).multiplyScalar(1.7 * tw));
      }
    }
    for (const q of parts) {
      if (q.kind !== 'spark' || n >= cap) continue;
      const k = (now - q.t0) / q.life, f = Math.sin(Math.PI * Math.min(1, k)) * (0.6 + 0.4 * Math.sin(now * 0.04 + q.rot * 9));
      setM(sparks, n, q.p.x, q.p.y, q.p.z, 0, 0, q.rot, Math.max(0.001, 0.05 + 0.06 * f));
      sparks.setColorAt(n++, _c.copy(GOLD).multiplyScalar(2 * Math.max(0, f)));
    }
    finish(sparks, n);
  }
  function spawnSpark(p, v, life = 700) {
    if (!sparks || reduced || parts.filter((q) => q.kind === 'spark').length >= 18) return;
    parts.push({ kind: 'spark', p, v, t0: performance.now(), life, s: 1, g: -1.2, rot: Math.random() * 6, spin: (Math.random() - 0.5) * 8 });
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
      if (sparks) writeSparks(now, clockT);
      if (soil && (soilD.earth || soilD.ghost || soilD.heat)) soilUpload();
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
    moon.dispose?.(); flash.dispose?.(); chestLight.dispose?.(); rimL?.dispose?.(); lampA?.dispose?.(); lampB?.dispose?.(); gradTex?.dispose();
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
