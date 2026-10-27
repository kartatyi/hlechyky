// «Колія» — K32 «Погода й час доби»: лише вигляд (правила, хеш і мережа не знають; у мережі в кожного своя).
//  createWeather(ctx) → { update(dt, now, s, f), setWorld(s, root), setQuality(q), rows(), set(k, v), get state, dispose() }
//   ctx: { THREE, scene, camera, renderer, hemi, sun, waterMat, restore() } — restore() повертає світло/небо вигляду (K5).
//   update — щокадру перед render(): f = { cam, halfW, look, lowQ, loco (Group паротяга), players ([{ root }]) }.
//  Час доби: day | eve | night | flow (день → вечір → ніч за партію). Погода: clear | rain | snow | fog.
//  Типово — «День» і «Ясно»: нічого не робить і нічого не коштує (сцена та сама, що без K32).
//  Частинки (дощ, бризки, сніг, світлячки) — по одному об'єкту на вид, рух цілком у вершинному шейдері (CPU ≈ 0),
//  лише в коробці довкола камери. Ніч: фара паротяга (SpotLight) і ліхтарики над гравцями (PointLight) — лише на високій
//  якості; на низькій — плями світла на землі (текстура, без світел). Сніг на верхівках і мокра земля — uniform-и WX у
//  шейдері кубиків render.js (гачок K32 у FX_FRAG).
//  Адреса для перевірок: ?tod=night&wx=rain (не зберігається).

export const WX = { snow: { value: 0 }, snowG: { value: 0 }, wet: { value: 0 }, wetG: { value: 0 } };

export const TOD = { day: 'День', eve: 'Вечір', night: 'Ніч', flow: 'Плин часу' };
export const WEATHER = { clear: 'Ясно', rain: 'Дощ', snow: 'Сніг', fog: 'Туман' };
const KEY = 'kol-wx';
const FLOW_DAY = 45, FLOW_EVE = 150, FLOW_NIGHT = 330;   // с від початку партії: ще день, уже вечір, уже ніч

export function loadWx() {
  let o = {};
  try { o = JSON.parse(localStorage.getItem(KEY) || '{}') || {}; } catch { }
  let tod = o.tod in TOD ? o.tod : 'day', wx = o.wx in WEATHER ? o.wx : 'clear';
  try {
    const q = new URLSearchParams(location.search);
    if (q.get('tod') in TOD) tod = q.get('tod');
    if (q.get('wx') in WEATHER) wx = q.get('wx');
  } catch { }
  return { tod, wx };
}
function saveWx(p) { try { localStorage.setItem(KEY, JSON.stringify(p)); } catch { } }

const clamp01 = (v) => Math.max(0, Math.min(1, v));
const approach = (v, to, k) => v + (to - v) * k;

// ---- шейдери частинок ----
const HEAD = `uniform float uT; uniform vec3 uC; uniform vec3 uBox; uniform float uPx; uniform float uA;
float h1(float n) { return fract(sin(n) * 43758.5453); }
// x, прив'язаний до світу (камера їде — краплі лишаються на місці), у коробці ширини uBox.x довкола uC.x
float wrapX(float x) { float x0 = uC.x - uBox.x * 0.5; return x0 + mod(x - x0, uBox.x); }
`;
const RAIN_V = HEAD + `attribute vec4 aSeed; uniform vec2 uWind; uniform float uLen; varying float vA;
void main() {
  float H = uBox.y, sp = 9.0 * (0.85 + 0.3 * h1(aSeed.x * 71.3));
  float y = mod(aSeed.z * H - uT * sp, H);
  float x = wrapX(aSeed.x * uBox.x + uWind.x * uT);
  vec3 head = vec3(x, y, uC.z + (aSeed.y - 0.5) * uBox.z);
  vec3 dir = normalize(vec3(uWind.x, -sp, uWind.y));
  vec3 p = head - dir * uLen * aSeed.w;
  vA = (1.0 - aSeed.w) * uA * smoothstep(H, H - 1.5, y) * (0.55 + 0.45 * h1(aSeed.y * 13.7));
  gl_Position = projectionMatrix * modelViewMatrix * vec4(p, 1.0);
}`;
const RAIN_F = `uniform vec3 uCol; varying float vA; void main() { gl_FragColor = vec4(uCol, vA); }`;
const SPLASH_V = HEAD + `attribute vec2 aSeed; uniform float uRate; uniform float uSize; varying float vPh;
void main() {
  float k = uT * uRate + aSeed.y, cyc = floor(k); vPh = fract(k);
  float r1 = h1(aSeed.x * 91.7 + cyc * 13.1), r2 = h1(aSeed.x * 37.3 + cyc * 7.7);
  vec3 p = vec3(uC.x + (r1 - 0.5) * uBox.x, 0.07, uC.z + (r2 - 0.5) * uBox.z);
  vec4 mv = modelViewMatrix * vec4(p, 1.0);
  gl_Position = projectionMatrix * mv;
  gl_PointSize = uSize * (0.35 + vPh) * uPx / -mv.z;
}`;
const SPLASH_F = `uniform vec3 uCol; uniform float uA; varying float vPh;
void main() {
  vec2 d = gl_PointCoord * 2.0 - 1.0; d.y *= 1.6;       // з висоти — сплюснуте кільце
  float r = length(d), ring = smoothstep(0.22, 0.0, abs(r - 0.7)) + smoothstep(0.25, 0.0, r) * (1.0 - vPh) * 0.6;
  float a = ring * (1.0 - vPh) * uA;
  if (a < 0.01) discard;
  gl_FragColor = vec4(uCol, a);
}`;
const SNOW_V = HEAD + `attribute vec4 aSeed; uniform float uSize; uniform float uFall; varying float vA;
void main() {
  float H = uBox.y, ph = aSeed.w * 37.0;
  float y = mod(aSeed.z * H - uT * uFall * (0.7 + 0.6 * aSeed.w), H);
  float x = wrapX(aSeed.x * uBox.x + sin(uT * 0.7 + ph) * 0.45 + uT * 0.35);
  vec3 p = vec3(x, y, uC.z + (aSeed.y - 0.5) * uBox.z + cos(uT * 0.9 + ph) * 0.3);
  vec4 mv = modelViewMatrix * vec4(p, 1.0);
  gl_Position = projectionMatrix * mv;
  gl_PointSize = uSize * (0.6 + 0.8 * aSeed.w) * uPx / -mv.z;
  vA = uA * smoothstep(H, H - 1.5, y) * smoothstep(0.0, 0.25, y);
}`;
const SNOW_F = `uniform vec3 uCol; varying float vA;
void main() { float r = length(gl_PointCoord * 2.0 - 1.0); float a = smoothstep(1.0, 0.35, r) * vA; if (a < 0.01) discard; gl_FragColor = vec4(uCol, a); }`;
const FLY_V = HEAD + `attribute vec4 aSeed; uniform float uSize; varying float vA;
void main() {
  float ph = aSeed.w * 40.0;
  float x = wrapX(aSeed.x * uBox.x + sin(uT * 0.31 + ph) * 0.9);
  vec3 p = vec3(x, 0.3 + aSeed.z * 1.1 + sin(uT * 0.8 + ph) * 0.18, uC.z + (aSeed.y - 0.5) * uBox.z + cos(uT * 0.27 + ph) * 0.7);
  vec4 mv = modelViewMatrix * vec4(p, 1.0);
  gl_Position = projectionMatrix * mv;
  float b = max(0.0, sin(uT * (0.9 + aSeed.w * 1.4) + ph)); vA = uA * b * b * b;
  gl_PointSize = uSize * (0.5 + 0.5 * b) * uPx / -mv.z;
}`;
const GLOW_F = `uniform vec3 uCol; varying float vA;
void main() { float r = length(gl_PointCoord * 2.0 - 1.0); float a = exp(-r * r * 4.5) * vA; if (a < 0.01) discard; gl_FragColor = vec4(uCol * a, 1.0); }`;
// ліхтарики й фара — кілька точок, позиції з CPU (атрибут position), колір і розмір — свої
const BULB_V = `attribute float aSize; attribute vec3 aCol; uniform float uPx; uniform float uA; varying vec3 vCol;
void main() { vec4 mv = modelViewMatrix * vec4(position, 1.0); gl_Position = projectionMatrix * mv; gl_PointSize = aSize * uPx / -mv.z; vCol = aCol * uA; }`;
const BULB_F = `varying vec3 vCol;
void main() { float r = length(gl_PointCoord * 2.0 - 1.0); float a = exp(-r * r * 3.5) + smoothstep(0.3, 0.0, r); if (a < 0.01) discard; gl_FragColor = vec4(vCol * a, 1.0); }`;

export function createWeather({ THREE, scene, camera, renderer, hemi, sun, waterMat, restore }) {
  const prefs = loadWx();
  // поточні рівні (плавно до цілі): t — час доби 0 день … 1 вечір … 2 ніч; a — сила кожної погоди
  const cur = { t: todTarget(0), a: { rain: 0, snow: 0, fog: 0 }, snow: 0, wet: 0 };
  if (prefs.wx in cur.a) cur.a[prefs.wx] = 1;
  if (prefs.wx === 'snow') cur.snow = 1;
  if (prefs.wx === 'rain') cur.wet = 1;
  let lowQ = false, active = false, flash = 0, flashT = 20, uTime = 0;
  const _c = new THREE.Color(), _c2 = new THREE.Color(), _v = new THREE.Vector3(), _v2 = new THREE.Vector3(), _db = new THREE.Vector2();
  const group = new THREE.Group(); group.name = 'k32-weather'; scene.add(group);

  function todTarget(sec) {
    if (prefs.tod === 'day') return 0;
    if (prefs.tod === 'eve') return 1;
    if (prefs.tod === 'night') return 2;
    if (sec <= FLOW_DAY) return 0;
    if (sec <= FLOW_EVE) return (sec - FLOW_DAY) / (FLOW_EVE - FLOW_DAY);
    return 1 + clamp01((sec - FLOW_EVE) / (FLOW_NIGHT - FLOW_EVE));
  }

  // ---- частинки ----
  const box = new THREE.Vector3(30, 9, 16), center = new THREE.Vector3();
  const common = { uT: { value: 0 }, uC: { value: center }, uBox: { value: box }, uPx: { value: 800 } };
  function seeds(n, per, dim, fill) {
    const a = new Float32Array(n * per * dim);
    for (let i = 0; i < n; i++) {
      const r = [Math.random(), Math.random(), Math.random(), Math.random()];
      for (let v = 0; v < per; v++) { fill(r, v); for (let k = 0; k < dim; k++) a[(i * per + v) * dim + k] = r[k]; }
    }
    return a;
  }
  function makeObj(Kind, n, per, dim, fill, vs, fs, u, blend) {
    const g = new THREE.BufferGeometry();
    g.setAttribute('aSeed', new THREE.BufferAttribute(seeds(n, per, dim, fill), dim));
    g.setAttribute('position', new THREE.BufferAttribute(new Float32Array(n * per * 3), 3));   // не вживається, але потрібне three
    const m = new THREE.ShaderMaterial({ uniforms: { ...common, uA: { value: 0 }, ...u }, vertexShader: vs, fragmentShader: fs,
      transparent: true, depthWrite: false, blending: blend || THREE.NormalBlending, fog: false });
    const o = new Kind(g, m); o.frustumCulled = false; o.visible = false; o.renderOrder = 5;
    o.userData.n = n; o.userData.per = per; group.add(o);
    return o;
  }
  const N = { rain: 1600, splash: 260, snow: 1300, fly: 70 };
  const rain = makeObj(THREE.LineSegments, N.rain, 2, 4, (r, v) => { r[3] = v; }, RAIN_V, RAIN_F,
    { uCol: { value: new THREE.Color(0xcfe0f4) }, uWind: { value: new THREE.Vector2(1.6, 0.4) }, uLen: { value: 0.75 } });
  const splash = makeObj(THREE.Points, N.splash, 1, 2, () => { }, SPLASH_V, SPLASH_F,
    { uCol: { value: new THREE.Color(0xe4eef8) }, uRate: { value: 1.9 }, uSize: { value: 0.32 } });
  const snow = makeObj(THREE.Points, N.snow, 1, 4, () => { }, SNOW_V, SNOW_F,
    { uCol: { value: new THREE.Color(0xffffff) }, uSize: { value: 0.085 }, uFall: { value: 1.1 } });
  const fly = makeObj(THREE.Points, N.fly, 1, 4, () => { }, FLY_V, GLOW_F,
    { uCol: { value: new THREE.Color(0xd8ff7a) }, uSize: { value: 0.22 } }, THREE.AdditiveBlending);
  const parts = [rain, splash, snow, fly];
  function showN(o, k) {   // скільки частинок малювати (низька якість — третина)
    const n = Math.round(o.userData.n * clamp01(k) * (lowQ ? 0.35 : 1));
    o.visible = n > 0; o.geometry.setDrawRange(0, n * o.userData.per);
  }

  // ---- ніч: світні точки (ліхтарики, фара), плями світла на землі, фара й ліхтарики-світла ----
  const NB = 5;   // 4 гравці + фара
  const bulbG = new THREE.BufferGeometry();
  bulbG.setAttribute('position', new THREE.BufferAttribute(new Float32Array(NB * 3), 3));
  bulbG.setAttribute('aSize', new THREE.BufferAttribute(new Float32Array(NB), 1));
  bulbG.setAttribute('aCol', new THREE.BufferAttribute(new Float32Array(NB * 3), 3));
  const bulbs = new THREE.Points(bulbG, new THREE.ShaderMaterial({ uniforms: { uPx: common.uPx, uA: { value: 0 } }, vertexShader: BULB_V, fragmentShader: BULB_F,
    transparent: true, depthWrite: false, blending: THREE.AdditiveBlending, fog: false }));
  bulbs.frustumCulled = false; bulbs.visible = false; bulbs.renderOrder = 6; group.add(bulbs);

  const radTex = (() => {
    const c = document.createElement('canvas'); c.width = c.height = 64;
    const x = c.getContext('2d'), gr = x.createRadialGradient(32, 32, 0, 32, 32, 32);
    gr.addColorStop(0, 'rgba(255,255,255,1)'); gr.addColorStop(0.45, 'rgba(255,255,255,0.55)'); gr.addColorStop(1, 'rgba(255,255,255,0)');
    x.fillStyle = gr; x.fillRect(0, 0, 64, 64);
    return new THREE.CanvasTexture(c);
  })();
  const coneTex = (() => {   // промінь фари на землі: від вузького біля паротяга (зліва) до широкого (справа)
    const c = document.createElement('canvas'); c.width = 128; c.height = 64;
    const x = c.getContext('2d');
    for (let i = 0; i < 128; i++) {
      const f = i / 127, half = 6 + f * 24, a = Math.min(1, f * 6) * (1 - f) ** 1.3;
      const gr = x.createLinearGradient(0, 32 - half, 0, 32 + half);
      gr.addColorStop(0, 'rgba(255,255,255,0)'); gr.addColorStop(0.5, `rgba(255,255,255,${a})`); gr.addColorStop(1, 'rgba(255,255,255,0)');
      x.fillStyle = gr; x.fillRect(i, 32 - half, 1, half * 2);
    }
    return new THREE.CanvasTexture(c);
  })();
  const poolOpts = { transparent: true, depthWrite: false, blending: THREE.AdditiveBlending, fog: false, polygonOffset: true, polygonOffsetFactor: -3, polygonOffsetUnits: -3 };
  const PLANE = new THREE.PlaneGeometry(1, 1).rotateX(-Math.PI / 2);
  const NP = 8;   // гравці 4 + вікна вокзалу 2 + ліхтар біля дверей
  const poolMat = new THREE.MeshBasicMaterial({ map: radTex, ...poolOpts });
  const pools = new THREE.InstancedMesh(PLANE, poolMat, NP);
  pools.instanceMatrix.setUsage(THREE.DynamicDrawUsage); pools.frustumCulled = false; pools.visible = false; pools.renderOrder = 4;
  for (let i = 0; i < NP; i++) pools.setColorAt(i, _c.setHex(0)); group.add(pools);
  const beamMat = new THREE.MeshBasicMaterial({ map: coneTex, color: 0xffe2a0, ...poolOpts });
  const beam = new THREE.Mesh(PLANE, beamMat); beam.visible = false; beam.renderOrder = 4; group.add(beam);
  const _m = new THREE.Matrix4(), _q = new THREE.Quaternion(), _s = new THREE.Vector3(), _UP = new THREE.Vector3(0, 1, 0);
  function setPool(i, x, z, sx, sz, hex, k, ry = 0) {
    _v.set(x, 0.09, z); _s.set(sx, 1, sz); _q.setFromAxisAngle(_UP, ry);
    pools.setMatrixAt(i, _m.compose(_v, _q, _s)); pools.setColorAt(i, _c.setHex(hex).multiplyScalar(k));
  }

  // справжні світла — лише на високій якості й лише коли ніч можлива (додати світло = перекомпілювати шейдери,
  // тож вони в сцені весь час, поки обрано «Ніч»/«Плин часу», а гасне лише яскравість)
  const head = new THREE.SpotLight(0xffe0a0, 0, 13, 0.42, 0.55, 1.2);
  const lamps = [0, 1, 2, 3].map(() => new THREE.PointLight(0xffc070, 0, 4.2, 1.6));
  let lightsOn = false;
  function wantLights() { return !lowQ && (prefs.tod === 'night' || prefs.tod === 'flow' || cur.t > 0.05); }
  function syncLights() {
    const on = wantLights();
    if (on === lightsOn) return;
    lightsOn = on;
    for (const l of [head, head.target, ...lamps]) { if (on) group.add(l); else group.remove(l); }
  }

  // ---- вокзал уночі: світні вікна (координати — як у buildStation render.js: група в (w, 0, mid + 0.5)) ----
  const winMat = new THREE.MeshBasicMaterial({ color: 0xffd27a, transparent: true, opacity: 0, fog: true });
  const station = new THREE.Group(); station.visible = false; group.add(station);
  for (const z of [-3.6, -1.95]) { const m = new THREE.Mesh(new THREE.BoxGeometry(0.012, 0.32, 0.29), winMat); m.position.set(0.775, 0.68, z); station.add(m); }
  let stationAt = null;   // { x, z } у світі або null (нескінченна колія — вокзалу нема)

  // ---- калюжі: плоскі темні блискучі плями на траві (на всю смугу, одним InstancedMesh) ----
  const puddleMat = new THREE.MeshPhongMaterial({ color: 0x3e5468, specular: 0xb0c4dc, shininess: 60, transparent: true, opacity: 0, depthWrite: false,
    polygonOffset: true, polygonOffsetFactor: -2, polygonOffsetUnits: -2 });
  const PUD_GEO = new THREE.CircleGeometry(0.5, 14).rotateX(-Math.PI / 2);
  let puddles = null;
  function setWorld(s, root) {
    if (puddles) { puddles.parent?.remove(puddles); puddles.dispose(); puddles = null; }
    const list = [];
    for (let y = 1; y < s.h - 1; y++) for (let x = 0; x < s.w; x++) {
      const i = y * s.w + x, g = s.ground[i];
      if ((g === 0 || g === 4 || g === 5) && s.obj[i] !== 3 && hh(x, y) < 0.075) list.push([x, y]);
    }
    if (list.length) {
      puddles = new THREE.InstancedMesh(PUD_GEO, puddleMat, list.length);
      list.forEach(([x, y], k) => {
        const r = hh(y, x), sx = 0.45 + r * 0.5, sz = 0.35 + hh(x + 7, y) * 0.4;
        _v.set(x + 0.3 + r * 0.4, 0.05, y + 0.3 + hh(x, y + 3) * 0.4); _s.set(sx, 1, sz); _q.setFromAxisAngle(_UP, r * 3);
        puddles.setMatrixAt(k, _m.compose(_v, _q, _s));
      });
      puddles.visible = false; puddles.renderOrder = 1; root.add(puddles);
    }
    stationAt = s.endless ? null : { x: s.w, z: (s.h >> 1) + 0.5 };
    if (stationAt) { station.position.set(stationAt.x, 0, stationAt.z); root.add(station); } else group.add(station);
  }
  function hh(x, y) { let h = Math.imul(x | 0, 0x27D4EB2D) ^ Math.imul(y | 0, 0x165667B1) ^ 0x5bd1e995; h = Math.imul(h ^ (h >>> 15), 0x85EBCA6B); h ^= h >>> 13; return (h >>> 0) / 4294967296; }

  // ---- небо (тло): градієнт + зорі; перемальовується лише коли помітно змінилось ----
  const skyCan = document.createElement('canvas'); skyCan.width = 512; skyCan.height = 256;
  const skyTex = new THREE.CanvasTexture(skyCan); skyTex.colorSpace = THREE.SRGBColorSpace;
  const stars = Array.from({ length: 140 }, () => [Math.random() * 512, Math.random() * 190, Math.random()]);
  let skyKey = '';
  const hex6 = (c) => '#' + c.getHexString();
  function drawSky(top, bot, fogC, starA) {
    const key = top.getHex() + ':' + bot.getHex() + ':' + fogC.getHex() + ':' + Math.round(starA * 20);
    if (key === skyKey) return; skyKey = key;
    const x = skyCan.getContext('2d'), gr = x.createLinearGradient(0, 0, 0, 256);
    gr.addColorStop(0, hex6(top)); gr.addColorStop(0.75, hex6(bot)); gr.addColorStop(1, hex6(fogC));
    x.fillStyle = gr; x.fillRect(0, 0, 512, 256);
    if (starA > 0.02) for (const [sx, sy, b] of stars) {
      x.globalAlpha = starA * (0.35 + 0.65 * b) * (1 - sy / 220);
      x.fillStyle = b > 0.85 ? '#fff6d8' : '#ffffff';
      x.fillRect(sx, sy, b > 0.7 ? 2 : 1, b > 0.7 ? 2 : 1);
    }
    x.globalAlpha = 1; skyTex.needsUpdate = true;
  }

  // ---- палітри часу доби й погоди ----
  const EVE = { hs: 0xffbf9a, hg: 0x5a4668, sc: 0xff9450, dir: [-1, 0.3, 0.28], sky: [0x4b4c9a, 0xff9a62], fog: 0xe89a78, water: 0x3a6a8a };
  const NIGHT = { hs: 0x4a5c9c, hg: 0x161c34, sc: 0x9ab4ff, dir: [-0.35, 1, 0.5], sky: [0x040818, 0x18234c], fog: 0x121a34, water: 0x163256, hi: 0.62, si: 0.5 };
  const W = {   // погода: тло, туман, множники світла
    rain: { sky: [0x5e6b78, 0x9aa5ae], fog: 0x8e9aa4, hs: 0xa6b2c0, hm: 0.78, sm: 0.3, fogK: 0.88 },
    snow: { sky: [0xaab6c6, 0xe2e8f0], fog: 0xdde4ec, hs: 0xe8f0ff, hm: 1.02, sm: 0.5, fogK: 0.92 },
    fog: { sky: [0xb4bcc2, 0xd0d6da], fog: 0xc6cdd2, hs: 0xd4dce2, hm: 0.95, sm: 0.38, fogK: 1 },
  };
  const mix3 = (out, a, b, c, t) => t <= 1 ? out.setHex(a).lerp(_c2.setHex(b), t) : out.setHex(b).lerp(_c2.setHex(c), t - 1);
  const num3 = (a, b, c, t) => t <= 1 ? a + (b - a) * t : b + (c - b) * (t - 1);
  const cSkyT = new THREE.Color(), cSkyB = new THREE.Color(), cFog = new THREE.Color(), cHs = new THREE.Color(), cHg = new THREE.Color(), cSun = new THREE.Color(), cW = new THREE.Color();
  const fogLin = scene.fog;
  // туман-головоломка: FogExp2, але «глибина» — відстань від осі погляду (центру кадру), а не від камери:
  // довкола центру ясно, до країв кадру (і попереду колії) — молоко. Лише для FOG_EXP2 (його вживає тільки K32);
  // звичайний туман вигляду (Fog) не змінюється. Чанк підміняємо до першої компіляції шейдерів.
  const fogExp = new THREE.FogExp2(0xc6cdd2, 0);
  if (!THREE.ShaderChunk.fog_vertex.includes('K32')) {
    THREE.ShaderChunk.fog_vertex = `#ifdef USE_FOG
#ifdef FOG_EXP2
  vFogDepth = length(vec2(mvPosition.x, mvPosition.y * 0.8)); // K32
#else
  vFogDepth = - mvPosition.z;
#endif
#endif`;
  }

  function update(dt, now, s, f) {
    lowQ = !!f.lowQ;
    const sec = s ? s.tick / 20 : 0;
    const k1 = 1 - Math.exp(-dt * 1.2);            // зміна перемикачем — ~2 с
    const tGoal = todTarget(sec);
    cur.t = Math.abs(tGoal - cur.t) > 0.002 ? approach(cur.t, tGoal, prefs.tod === 'flow' ? 1 - Math.exp(-dt * 3) : k1) : tGoal;
    for (const k in cur.a) { const g = prefs.wx === k ? 1 : 0; cur.a[k] = Math.abs(g - cur.a[k]) > 0.002 ? approach(cur.a[k], g, k1) : g; }
    cur.snow = approach(cur.snow, prefs.wx === 'snow' ? 1 : 0, 1 - Math.exp(-dt * (prefs.wx === 'snow' ? 0.18 : 0.4)));
    cur.wet = approach(cur.wet, prefs.wx === 'rain' ? 1 : 0, 1 - Math.exp(-dt * (prefs.wx === 'rain' ? 0.35 : 0.12)));
    if (cur.snow < 0.003 && prefs.wx !== 'snow') cur.snow = 0;
    if (cur.wet < 0.003 && prefs.wx !== 'rain') cur.wet = 0;
    WX.snow.value = cur.snow * 0.72; WX.snowG.value = cur.snow * 0.84; WX.wet.value = cur.wet * 0.14; WX.wetG.value = cur.wet * 0.26;
    syncLights();
    const aR = cur.a.rain, aS = cur.a.snow, aF = cur.a.fog, aW = aR + aS + aF;
    const busy = cur.t > 0 || aW > 0 || cur.snow > 0 || cur.wet > 0 || lightsOn;   // світла в сцені — лишаємось (інакше перекомпіляція)
    if (!busy) {
      if (active) { active = false; group.visible = false; station.visible = false; if (puddles) puddles.visible = false; scene.fog = fogLin; restore(); }
      return;
    }
    active = true; group.visible = true;
    const look = f.look, t = cur.t, n = clamp01(t - 1), cam = f.cam;
    uTime += dt; common.uT.value = uTime;

    // світло часу доби
    mix3(cHs, look.hemi.sky, EVE.hs, NIGHT.hs, t); mix3(cHg, look.hemi.ground, EVE.hg, NIGHT.hg, t);
    let hi = num3(look.hemi.i, look.hemi.i * 0.82, NIGHT.hi, t), si = num3(look.sun.i, look.sun.i * 0.85, NIGHT.si, t);
    mix3(cSun, look.sun.color, EVE.sc, NIGHT.sc, t);
    mix3(cSkyT, look.sky[0], EVE.sky[0], NIGHT.sky[0], t); mix3(cSkyB, look.sky[1], EVE.sky[1], NIGHT.sky[1], t);
    mix3(cFog, look.fog.color, EVE.fog, NIGHT.fog, t);
    const dir = t <= 1 ? _v2.fromArray(look.sun.dir).lerp(_v.fromArray(EVE.dir), t) : _v2.fromArray(EVE.dir).lerp(_v.fromArray(NIGHT.dir), t - 1);
    dir.normalize();
    let fogNear = look.fog.near * num3(1, 0.97, 0.9, t), fogFar = look.fog.far * num3(1, 0.95, 0.86, t);
    // погода поверх (кольори темнішають разом із часом доби)
    if (aW > 0) {
      const L = num3(1, 0.62, 0.15, t);
      let hm = 1, sm = 1, fk = 1;
      for (const [k, a] of [['rain', aR], ['snow', aS], ['fog', aF]]) {
        if (a <= 0) continue;
        const w = W[k];
        cSkyT.lerp(_c.setHex(w.sky[0]).multiplyScalar(L), a); cSkyB.lerp(_c.setHex(w.sky[1]).multiplyScalar(L), a);
        cFog.lerp(_c.setHex(w.fog).multiplyScalar(L), a); cHs.lerp(_c.setHex(w.hs).multiplyScalar(Math.max(L, 0.4)), a * 0.7);
        hm *= 1 + (w.hm - 1) * a; sm *= 1 + (w.sm - 1) * a; fk *= 1 + (w.fogK - 1) * a;
      }
      hi *= hm; si *= sm; fogNear *= fk; fogFar *= fk;
    }
    // блискавка в дощ (рідко, коротко)
    if (aR > 0.6) {
      flashT -= dt;
      if (flashT <= 0) { flash = 1; flashT = 14 + Math.random() * 26; }
    }
    if (flash > 0) { hi += flash * 2.6 * aR; flash = Math.max(0, flash - dt * (flash > 0.5 ? 9 : 4)); }
    hemi.color.copy(cHs); hemi.groundColor.copy(cHg); hemi.intensity = hi;
    sun.color.copy(cSun); sun.intensity = si;
    sun.position.copy(sun.target.position).addScaledVector(dir, 50);
    fogLin.color.copy(cFog); fogLin.near = cam.d * fogNear; fogLin.far = cam.d * fogFar;
    // туман: перемкнути на FogExp2 (одна перекомпіляція), густина — від ширини кадру: на півдорозі до краю ~35 %, на краю ~88 %
    if (aF > 0.004) { scene.fog = fogExp; fogExp.color.copy(cFog); fogExp.density = aF * 1.35 / Math.max(6, f.halfW); }
    else scene.fog = fogLin;
    drawSky(cSkyT, cSkyB, cFog, n * (1 - Math.min(1, aW * 1.2)));
    scene.background = aF > 0.5 ? fogExp.color : skyTex;   // у тумані край діорами тоне в тому ж кольорі
    mix3(cW, look.water.color, EVE.water, NIGHT.water, t); if (aW > 0) cW.lerp(_c.setHex(0x4a5e70).multiplyScalar(num3(1, 0.7, 0.3, t)), aR * 0.6 + aF * 0.3);
    waterMat.color.copy(cW);

    // частинки довкола камери
    const hw = Math.max(8, f.halfW * 1.25);
    box.set(hw * 2, 9, (s?.h || 14) + 3); center.set(cam.x, 0, cam.z - 0.6);
    const bh = renderer.getDrawingBufferSize(_db).y;
    common.uPx.value = bh / (2 * Math.tan(camera.fov * Math.PI / 360));
    showN(rain, aR); rain.material.uniforms.uA.value = 0.55 * Math.max(0.35, num3(1, 0.85, 0.55, t));
    rain.material.uniforms.uCol.value.setHex(n > 0.5 ? 0x8fa6c8 : 0xcfe0f4);
    showN(splash, aR); splash.material.uniforms.uA.value = 0.55 * aR * num3(1, 0.8, 0.5, t);
    showN(snow, aS); snow.material.uniforms.uA.value = 0.95 * num3(1, 0.85, 0.6, t);
    snow.material.uniforms.uCol.value.setHex(n > 0.5 ? 0xb8c8ec : 0xffffff);
    const flyK = clamp01((t - 1.3) / 0.5) * (1 - aR) * (1 - aS);
    showN(fly, flyK > 0 ? 1 : 0); fly.material.uniforms.uA.value = flyK;
    if (puddles) { puddles.visible = cur.wet > 0.01 && cur.snow < 0.5; puddleMat.opacity = 0.5 * cur.wet; }

    // ніч: фара, ліхтарики, вікна
    const nl = clamp01((t - 0.85) / 0.7);
    const dim = 1 - aF * 0.25;
    const loco = f.loco;
    let bi = 0;
    const bp = bulbG.attributes.position.array, bs = bulbG.attributes.aSize.array, bc = bulbG.attributes.aCol.array;
    const bulb = (x, y, z, size, hex) => { bp[bi * 3] = x; bp[bi * 3 + 1] = y; bp[bi * 3 + 2] = z; bs[bi] = size; _c.setHex(hex); bc[bi * 3] = _c.r; bc[bi * 3 + 1] = _c.g; bc[bi * 3 + 2] = _c.b; bi++; };
    let pi = 0;
    if (nl > 0) {
      (f.players || []).forEach((m) => {
        if (!m || pi >= 4) return;
        const r = m.root, on = r.visible;
        const lx = r.position.x, lz = r.position.z;
        if (on) {
          setPool(pi, lx, lz, 3.1, 3.1, 0xffb860, 0.5 * nl * dim * (lowQ ? 1.25 : 1));
          bulb(lx, r.position.y + 1.42, lz, 0.32, 0xffd890);
        } else setPool(pi, 0, -50, 0, 0, 0, 0);
        const L = lamps[pi];
        if (L) { L.position.set(lx, r.position.y + 1.4, lz); L.intensity = on ? 2.6 * nl : 0; }
        pi++;
      });
    }
    for (; pi < 4; pi++) { setPool(pi, 0, -50, 0, 0, 0, 0); if (lamps[pi]) lamps[pi].intensity = 0; }
    if (nl > 0 && stationAt && station.parent) {
      const sx = stationAt.x, sz = stationAt.z;
      setPool(4, sx + 0.25, sz - 3.6, 1.2, 0.8, 0xffc070, 0.55 * nl);
      setPool(5, sx + 0.25, sz - 1.95, 1.2, 0.8, 0xffc070, 0.55 * nl);
      setPool(6, sx + 0.4, sz - 2.6, 1.6, 1.6, 0xffd090, 0.35 * nl);
    } else for (let i = 4; i < 7; i++) setPool(i, 0, -50, 0, 0, 0, 0);
    setPool(7, 0, -50, 0, 0, 0, 0);
    pools.visible = nl > 0; pools.instanceMatrix.needsUpdate = true; if (pools.instanceColor) pools.instanceColor.needsUpdate = true;
    station.visible = nl > 0; winMat.opacity = Math.min(1, nl * 1.2);
    if (loco && nl > 0) {
      loco.updateMatrixWorld();
      const yaw = loco.rotation.y;
      _v.set(0.5, 0.4, 0).applyMatrix4(loco.matrixWorld);
      bulb(_v.x, _v.y, _v.z, 0.55, 0xfff0c0);
      head.position.copy(_v);
      head.target.position.set(0, -0.6, 0).add(_v.set(5, 0, 0).applyAxisAngle(_UP, yaw)).add(head.position);
      head.target.updateMatrixWorld();
      head.intensity = 40 * nl * dim;
      // промінь на землі (на низькій якості — головне світло фари, на високій — підсвітка до SpotLight)
      _v.set(3.1, 0, 0).applyAxisAngle(_UP, yaw).add(loco.position);
      beam.position.set(_v.x, 0.1, _v.z); beam.rotation.set(0, yaw, 0); beam.scale.set(5.6, 1, 2.6);
      beamMat.opacity = (lowQ ? 0.85 : 0.45) * nl * dim; beam.visible = true;
    } else { head.intensity = 0; beam.visible = false; }
    for (; bi < NB; bi++) { bs[bi] = 0; }
    bulbs.visible = nl > 0; bulbs.material.uniforms.uA.value = nl;
    bulbG.attributes.position.needsUpdate = bulbG.attributes.aSize.needsUpdate = bulbG.attributes.aCol.needsUpdate = true;
  }

  function set(k, v) {
    if (k === 'tod' && v in TOD) prefs.tod = v;
    else if (k === 'wx' && v in WEATHER) prefs.wx = v;
    else return;
    saveWx(prefs);
  }
  function setQuality(q) { lowQ = q === 'low'; syncLights(); }
  function dispose() {
    scene.remove(group);
    for (const o of parts) { o.geometry.dispose(); o.material.dispose(); }
    bulbG.dispose(); bulbs.material.dispose(); pools.dispose(); poolMat.dispose(); beamMat.dispose(); radTex.dispose(); coneTex.dispose();
    PLANE.dispose(); PUD_GEO.dispose(); puddleMat.dispose(); winMat.dispose(); skyTex.dispose();
    if (puddles) puddles.dispose();
    WX.snow.value = WX.snowG.value = WX.wet.value = WX.wetG.value = 0;
  }
  // рядки для «🎨 Вигляд» (looks.js): { label, opts, key, value, pick }
  function rows() {
    return [
      { label: 'Час доби', opts: TOD, key: 'tod', value: prefs.tod, pick: (v) => set('tod', v) },
      { label: 'Погода', opts: WEATHER, key: 'wx', value: prefs.wx, pick: (v) => set('wx', v) },
    ];
  }
  return { update, setWorld, setQuality, set, rows, dispose, get state() { return { ...prefs, t: cur.t, a: { ...cur.a }, snow: cur.snow, wet: cur.wet, active, lights: lightsOn }; } };
}
