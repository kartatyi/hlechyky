// Небо й світло «Чумаків» 0.0.5 (STYLE.md §15, §17): фізичне небо (atmo.js), об'ємні хмари (clouds.js) і пост-обробка
// (post.js) — одним конвеєром. Небо керує світлом: колір сонця на землі — пропускання атмосфери, заповнення — опромінення
// неба, відбиток від землі, туман — колір неба в напрямку погляду, експозиція — від освітленості землі, оточення PBR —
// PMREM того самого неба; LUT, bloom, вікна й ліхтарі — від висоти сонця (ключі проби 3).
//
// Пора доби: за замовчуванням — справжнє сонце над Києвом зараз (схід/захід за датою); «завжди день» — 13:00 літнього
// дня; «пришвидшена» — літній день, 1 хвилина = 1 година. Хмарність — «погода дня» (від дати), вітер несе хмари й їхні
// тіні. Перенесено з проби 4 (fura/style: probe4.js → updateSky, applyGfx, prepCam).

import { Atmosphere, sunTransmittance, atmoUniforms, makeSky, patchAtmo } from './atmo.js';
import { Clouds } from './clouds.js';
import { Post } from './post.js';
import { makeMaterials } from './materials.js';

const SUN_I = 3.4;               // сила сонця вгорі атмосфери (DirectionalLight), далі × пропускання
const SKY_K = 3.2;               // яскравість неба відносно сонця (художньо: фізичне небо ×3, інакше воно темніше за землю)
const KYIV = { lat: 50.45, lon: 30.52 };
const SUMMER = Date.UTC(2026, 6, 15);          // «літній день» для «завжди день» і пришвидшеної доби (Київ — UTC+3)
// хмари: [частка роздільності, кроків, проб світла, дрібний шум, перераховувати раз на N кадрів]
export const CLOUDQ = { off: null, low: [0.25, 36, 4, false, 2], high: [0.35, 56, 5, true, 1], ultra: [0.5, 80, 6, true, 1] };
// дальність: межа туману й чанків, множник відстаней дерев, густина туману. Ключі туману підбирали на сцені проби
// (до ~500 м); у краї видно на 1–2 км, і з тією ж густиною все далі за 400 м тонуло в «молоці» — тож ×0,5…0,8, а край
// дальності ховає повний туман на межі (uFogFar)
export const DRAW = { near: { far: 900, imp: 0.6, fog: 0.8 }, mid: { far: 1400, imp: 0.8, fog: 0.62 }, far: { far: 2000, imp: 1, fog: 0.5 } };

// ---------------------------------------------------------------- ключі за висотою сонця (проба 3, sky3.js — лише грейд,
// bloom, вікна й густина туману; колір неба — з атмосфери)
const KEYS = [
  { el: -18, fog: 0.0011, bloom: 0.7, win: 2.2, grade: { contrast: 0.15, sat: 0.92, lift: [0, 0.01, 0.04], gain: [0.95, 1, 1.08], split: 0.1, shadow: [-0.1, 0, 0.3], high: [0, 0.05, 0.2] } },
  { el: -6, fog: 0.0015, bloom: 0.6, win: 1.8, grade: { contrast: 0.2, sat: 1.05, lift: [0.01, 0, 0.05], gain: [1, 0.98, 1.06], split: 0.12, shadow: [-0.1, -0.05, 0.4], high: [0.2, 0, 0.1] } },
  { el: 0, fog: 0.0015, bloom: 0.55, win: 1.4, grade: { contrast: 0.28, sat: 1.18, lift: [0.03, 0, 0.06], gain: [1.08, 0.98, 0.88], split: 0.16, shadow: [-0.2, -0.1, 0.5], high: [0.45, 0.2, -0.3] } },
  { el: 7, fog: 0.0011, bloom: 0.45, win: 0.6, grade: { contrast: 0.24, sat: 1.15, lift: [0.01, 0, 0.04], gain: [1.05, 1, 0.92], split: 0.12, shadow: [-0.3, 0, 0.45], high: [0.35, 0.18, -0.22] } },
  { el: 22, fog: 0.0008, bloom: 0.3, win: 0, grade: { contrast: 0.15, sat: 1.1, lift: [0, 0.005, 0.02], gain: [1.02, 1, 0.98], split: 0.06, shadow: [-0.2, 0, 0.3], high: [0.15, 0.08, -0.1] } },
  { el: 58, fog: 0.0007, bloom: 0.25, win: 0, grade: { contrast: 0.14, sat: 1.1, lift: [0, 0, 0.015], gain: [1.01, 1, 0.99], split: 0.05, shadow: [-0.2, 0, 0.3], high: [0.1, 0.06, -0.06] } },
];
const lerpN = (a, b, k) => a + (b - a) * k;
function keysAt(elev) {
  let i = 0;
  while (i < KEYS.length - 2 && elev > KEYS[i + 1].el) i++;
  const A = KEYS[i], B = KEYS[i + 1], k = Math.max(0, Math.min(1, (elev - A.el) / (B.el - A.el)));
  const grade = {};
  for (const g of Object.keys(A.grade)) grade[g] = Array.isArray(A.grade[g]) ? A.grade[g].map((v, j) => lerpN(v, B.grade[g][j], k)) : lerpN(A.grade[g], B.grade[g], k);
  return { fog: lerpN(A.fog, B.fog, k), bloom: lerpN(A.bloom, B.bloom, k), win: lerpN(A.win, B.win, k), grade };
}

/**
 * Сонце над точкою (lat, lon) у мить ms (наближення NOAA/«Astronomical Almanac», точність ~1°). Світ гри: +X — схід,
 * +Z — північ, +Y — вгору. Повертає напрям на сонце й висоту в градусах.
 */
export function sunAt(THREE, ms, lat = KYIV.lat, lon = KYIV.lon) {
  const rad = Math.PI / 180, d = ms / 86400000 - 10957.5;            // доби від J2000.0
  const g = (357.529 + 0.98560028 * d) * rad, q = 280.459 + 0.98564736 * d;
  const L = (q + 1.915 * Math.sin(g) + 0.020 * Math.sin(2 * g)) * rad, e = (23.439 - 0.00000036 * d) * rad;
  const ra = Math.atan2(Math.cos(e) * Math.sin(L), Math.cos(L)), dec = Math.asin(Math.sin(e) * Math.sin(L));
  const gmst = 18.697374558 + 24.06570982441908 * d;                  // години
  const ha = ((gmst * 15 + lon) % 360) * rad - ra, la = lat * rad;
  const el = Math.asin(Math.sin(la) * Math.sin(dec) + Math.cos(la) * Math.cos(dec) * Math.cos(ha));
  const az = Math.atan2(-Math.sin(ha), Math.tan(dec) * Math.cos(la) - Math.sin(la) * Math.cos(ha));   // від півночі, схід +
  return { dir: new THREE.Vector3(Math.sin(az) * Math.cos(el), Math.sin(el), Math.cos(az) * Math.cos(el)), elev: el / rad };
}

/** Година за Києвом (0…24) для миті ms. */
export function kyivHour(ms) {
  try {
    const p = new Intl.DateTimeFormat('en-GB', { timeZone: 'Europe/Kyiv', hour: 'numeric', minute: 'numeric', hour12: false }).formatToParts(new Date(ms));
    const h = +p.find((x) => x.type === 'hour').value, m = +p.find((x) => x.type === 'minute').value;
    return (h % 24) + m / 60;
  } catch { return ((ms / 3600000 + 2) % 24 + 24) % 24; }
}
const hh = (t) => { const h = Math.floor(t) % 24, m = Math.floor((t - Math.floor(t)) * 60); return String(h).padStart(2, '0') + ':' + String(m).padStart(2, '0'); };

/** «Погода дня»: хмарність 15–60 % від номера доби (у всіх гравців того самого дня — те саме небо). */
function coverOfDay(ms) {
  const day = Math.floor((ms + 3 * 3600e3) / 86400e3);
  let x = Math.sin(day * 12.9898) * 43758.5453; x -= Math.floor(x);
  return 0.15 + 0.45 * x;
}

// ---------------------------------------------------------------- LUT (колірне грейдування, з проби 2 — paint.js)
/** 3D-LUT 32³ з параметрів (у просторі дисплея 0…1): контраст (S-крива), насиченість, lift/gain, спліт-тонування. */
export function makeLUT(THREE, g) {
  const N = 32, data = new Uint8Array(N * N * N * 4);
  const o = { contrast: 0, sat: 1, lift: [0, 0, 0], gain: [1, 1, 1], shadow: [0, 0, 0], high: [0, 0, 0], split: 0, ...g };
  for (let b = 0; b < N; b++) for (let gg = 0; gg < N; gg++) for (let r = 0; r < N; r++) {
    let c = [r / (N - 1), gg / (N - 1), b / (N - 1)];
    c = c.map((v) => v + (v * v * (3 - 2 * v) - v) * o.contrast);                       // S-крива
    c = c.map((v, i) => v * o.gain[i] + o.lift[i] * (1 - v));
    const l = 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2];
    c = c.map((v) => l + (v - l) * o.sat);
    c = c.map((v, i) => v + (o.shadow[i] * (1 - l) * (1 - l) + o.high[i] * l * l) * o.split);
    const k = ((b * N + gg) * N + r) * 4;
    for (let i = 0; i < 3; i++) data[k + i] = Math.max(0, Math.min(255, Math.round(c[i] * 255)));
    data[k + 3] = 255;
  }
  const t = new THREE.Data3DTexture(data, N, N, N);
  t.format = THREE.RGBAFormat; t.type = THREE.UnsignedByteType;
  t.minFilter = t.magFilter = THREE.LinearFilter; t.wrapS = t.wrapT = t.wrapR = THREE.ClampToEdgeWrapping;
  t.unpackAlignment = 1; t.needsUpdate = true;
  return t;
}

const lum = (c) => 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b;
const GROUND_ALB = [0.22, 0.2, 0.13];

export class Sky {
  /** progress(text) — рядок для екрана завантаження. */
  constructor(THREE, renderer, scene, { progress } = {}) {
    const T = THREE;
    this.T = T; this.r = renderer; this.scene = scene;
    renderer.shadowMap.enabled = true; renderer.shadowMap.type = T.PCFShadowMap; renderer.shadowMap.autoUpdate = false;
    renderer.toneMapping = T.NoToneMapping;               // тон — у пості (Neutral + LUT)
    this.hemi = new T.HemisphereLight('#ffffff', '#888888', 1);
    this.sun = new T.DirectionalLight('#ffffff', 3);
    this.sun.castShadow = true;
    this.sun.shadow.bias = -0.0005; this.sun.shadow.normalBias = 0.04; this.sun.shadow.radius = 2;
    scene.add(this.hemi, this.sun, this.sun.target);
    // туман three лишається «табличкою» для kray.sync (межа чанків = far) і для матеріалів без латки
    scene.fog = new T.Fog('#c8d8e8', 200, DRAW.far.far);
    progress && progress('☁ Печемо хмари…');
    this.atmo = new Atmosphere(T, renderer);
    this.clouds = new Clouds(T, renderer);
    this.U = atmoUniforms(T);
    this.U.tSkyView.value = this.atmo.view.texture;
    this.dome = new T.Mesh(new T.SphereGeometry(5000, 48, 24), makeSky(T, this.U));
    this.dome.renderOrder = -1; this.dome.frustumCulled = false; this.dome.userData.noAtmo = true;
    scene.add(this.dome);
    this.post = new Post(T, renderer);
    this.pmrem = new T.PMREMGenerator(renderer);
    this.envScene = new T.Scene();
    this.envScene.add(new T.Mesh(new T.SphereGeometry(100, 32, 16), makeSky(T, { ...this.U, uCloudsOn: { value: 0 } })));
    this.M = makeMaterials(T);
    this.rcam = new T.PerspectiveCamera(); this.rcam.layers.enable(1);
    this.off = new T.Vector2(); this._vR = new T.Vector3(); this._vU = new T.Vector3();
    this.light = { sunCol: new T.Color(), ambTop: new T.Color(), ambBot: new T.Color(), keyE: 1, keyDir: new T.Vector3(0, 1, 0) };
    this.irr = new T.Color(0.05, 0.08, 0.15);
    this.patched = new WeakSet();
    this.mode = 'kyiv'; this.fastT = kyivHour(Date.now()); this.fixedMs = null; this.coverOverride = null;
    this.clock = 0; this.cloudTick = 0; this.lightsOn = false;
    this._lut = null; this._lutEl = -999; this._envDir = new T.Vector3(0, -1, 0); this._env = null; this._atmoEl = -999;
    this.G = { clouds: 'high', cloudShadows: true, bloom: true, grade: true, aa: 'fxaa', pixel: 1, scale: 1, shadows: 2048, draw: 'far' };
    this.W = 2; this.H = 2;
    // фари: одна пляма світла вперед від тягача (без тіні), удень — нуль (кількість світел стала: інакше перекомпіляція)
    this.head = new T.SpotLight('#ffe9c4', 0, 95, 0.46, 0.55, 1.25);
    this.head.position.set(0, 0.1, 3.4);
    this.head.target.position.set(0, -2.2, 30);
    // опора експозиції — освітленість землі опівдні літнього дня
    this.noonE = 0;
    this.fixedMs = SUMMER + 10 * 3600e3;
    this._apply(true);
    this.noonE = this._Eg;
    this.fixedMs = null;
  }

  // ---------------------------------------------------------------- час і стан
  /** mode: 'kyiv' — зараз над Києвом, 'day' — завжди 13:00, 'fast' — пришвидшена доба (1 хв = 1 год). */
  setMode(mode) {
    if (mode === this.mode) return;
    if (mode === 'fast') this.fastT = kyivHour(Date.now());
    this.mode = mode; this.fixedMs = null; this._apply(true);
  }
  /** Для перевірки: застигла година h (літній день) або null — назад до режиму. cover — хмарність 0…1 або null (погода дня). */
  setHour(h, cover) { this.fixedMs = h == null ? null : SUMMER + (h - 3) * 3600e3; if (cover !== undefined) this.coverOverride = cover; this._apply(true); }
  _now() {
    if (this.fixedMs != null) return this.fixedMs;
    if (this.mode === 'day') return SUMMER + 10 * 3600e3;
    if (this.mode === 'fast') return SUMMER + (this.fastT - 3) * 3600e3;
    return Date.now();
  }
  _state(ms) {
    const T = this.T, s = sunAt(T, ms);
    const st = { ms, sunDir: s.dir, elev: s.elev, ...keysAt(s.elev) };
    st.night = s.elev < -4;
    st.moonDir = new T.Vector3(-s.dir.x, Math.max(0.45, -s.dir.y), -s.dir.z).normalize();
    st.moonVis = Math.max(0, Math.min(1, (-s.elev - 2) / 8));
    st.cover = this.coverOverride != null ? this.coverOverride : coverOfDay(ms);
    st.hour = this.fixedMs != null || this.mode !== 'kyiv' ? ((ms - SUMMER) / 3600e3 + 3) % 24 : kyivHour(ms);
    this.st = st;
    return st;
  }
  get hourText() { return this.st ? hh(this.st.hour) : ''; }
  get dark() { return !!this.st && this.st.win > 0.5; }

  /** Перерахувати світло з неба. force — усе, інакше лише те, що помітно зрушило. */
  _apply(force) {
    const T = this.T, st = this._state(this._now()), U = this.U, G = this.G, M = this.M, cv = st.cover, L = this.light;
    // ключове світило для неба: сонце до −10° (сутінки світяться ним), далі — місяць
    const useSun = st.elev > -10;
    L.keyDir.copy(useSun ? st.sunDir : st.moonDir);
    L.keyE = (useSun ? SUN_I : SUN_I * 0.004) * SKY_K;
    const kel = Math.asin(Math.max(-1, Math.min(1, L.keyDir.y))) * 180 / Math.PI;
    if (force || Math.abs(kel - this._atmoEl) > 0.1) { this._atmo(L.keyDir, force); this._atmoEl = kel; }
    U.uKeyDir.value.copy(L.keyDir); U.uKeyE.value = L.keyE;
    // сонце на землі = сила × пропускання атмосфери (тепле на заході — фізично); уночі — місяць
    const Tr = sunTransmittance(T, st.sunDir.y), sun = this.sun;
    if (!st.night) { sun.color.copy(Tr); sun.intensity = SUN_I * (1 - 0.5 * cv * cv); }
    else { sun.color.set('#9fb4e6'); sun.intensity = 0.3 * (1 - 0.5 * cv); }
    // заповнення: опромінення неба (π·L), трохи сіріше й сильніше під хмарами; знизу — відбиток від землі
    const skyE = this.irr.clone().multiplyScalar(L.keyE * Math.PI * (1 + 0.4 * cv));
    const grey = (skyE.r + skyE.g + skyE.b) / 3;
    skyE.lerp(new T.Color(grey, grey, grey), 0.35 * cv);
    const groundE = skyE.clone().add(sun.color.clone().multiplyScalar(sun.intensity * Math.max(0, (st.night ? st.moonDir : st.sunDir).y)));
    this.hemi.color.copy(skyE); this.hemi.groundColor.copy(groundE).multiply(new T.Color(...GROUND_ALB)); this.hemi.intensity = 0.85;
    // хмари на висоті 1,5–4 км бачать сонце ще кілька хвилин після заходу (+0,045 до косинуса)
    if (useSun) L.sunCol.copy(sunTransmittance(T, st.sunDir.y + 0.045)).multiplyScalar(SUN_I * 3.2 * (1 - 0.3 * cv));
    else L.sunCol.set('#9fb4e6').multiplyScalar(0.9);
    L.ambTop.copy(skyE).multiplyScalar(0.6); L.ambBot.copy(this.hemi.groundColor).multiplyScalar(0.5);
    // експозиція — від освітленості землі відносно полудня (ніч не чорна, полудень не «вигорає»)
    this._Eg = lum(groundE) + 0.002;
    const exp = this.noonE ? Math.min(25, Math.max(0.9, Math.pow(this.noonE / this._Eg, 0.72))) : 1;
    this.exposure = exp;
    U.uSunDir.value.copy(st.sunDir); U.uSunDisc.value.copy(Tr).multiplyScalar(st.night ? 0 : 60);
    U.uMoonDir.value.copy(st.moonDir); U.uMoonVis.value = st.moonVis; U.uStars.value = Math.max(0, Math.min(1, (-st.elev - 6) / 8)) * (1 - cv * 0.8);
    U.uCover.value = cv; U.uCloudsOn.value = G.clouds === 'off' ? 0 : 1; U.uCloudShadowAmt.value = G.cloudShadows && G.clouds !== 'off' ? 0.7 : 0;
    const D = DRAW[G.draw] || DRAW.far;
    U.uFogDens.value = st.fog * D.fog * (1 + 0.6 * cv); U.uFogFar.value = D.far;
    U.uRimCol.value.copy(sun.color).multiplyScalar(st.night ? 0.3 : 0.8);
    this.scene.fog.far = D.far; this.scene.fog.near = D.far * 0.3;
    this.scene.fog.color.copy(skyE).multiplyScalar(exp * 0.3);
    // вікна, ліхтарі, світлоповертачі — від висоти сонця (уночі експозиція до ×25: світять «у пікселях екрана» помірно)
    const lit = st.win > 0.5;
    M.win.emissiveIntensity = st.win * 0.55;
    M.lamp.emissiveIntensity = lit ? 3.2 : 0;
    M.reflect.emissiveIntensity = lit ? 0.8 : 0.15;
    M.pool.visible = lit; M.pool.color.set('#ffcf8a').multiplyScalar(lit ? 1.6 / exp : 0);
    // LUT — з неба тієї ж миті (32³ ~ кілька мс) — не частіше, ніж сонце зрушить на 0,4°
    if (force || Math.abs(st.elev - this._lutEl) > 0.4) { if (this._lut) this._lut.dispose(); this._lut = makeLUT(T, st.grade); this._lutEl = st.elev; }
    const pu = this.post.uber.uniforms;
    pu.tLut.value = this._lut; pu.uLutAmt.value = G.grade ? 0.7 : 0; pu.uExposure.value = exp; pu.uTone.value = 0;
    pu.uBloom.value = G.bloom ? st.bloom : 0; this.post.bloomOn = G.bloom;
    pu.uDither.value = 1; pu.uQuant.value = 0;
    pu.uFxaa.value = G.aa === 'fxaa' && G.pixel === 1 ? 1 : 0;
    // небо-оточення PBR — коли сонце зрушило на 3° (PMREM ~1–30 мс)
    if (force || st.sunDir.angleTo(this._envDir) > 0.05) {
      const e = this.pmrem.fromScene(this.envScene, 0.02);
      if (this._env) this._env.dispose();
      this._env = e.texture; this.scene.environment = this._env; this._envDir.copy(st.sunDir);
    }
    this.scene.environmentIntensity = st.night ? 0.2 : 0.3;
    this._applyHead();
  }
  /** Таблиця неба — на GPU; опромінення читаємо назад: при завантаженні — одразу, далі — без зупинки GPU (async). */
  _atmo(keyDir, sync) {
    const a = this.atmo;
    a.sMat.uniforms.uKeyDir.value.copy(keyDir); a._pass(a.sMat, a.view);
    a.iMat.uniforms.uKeyDir.value.copy(keyDir); a._pass(a.iMat, a.irr);
    const set = (px) => { this.irr.setRGB(px[0], px[1], px[2]); };
    if (sync || !this.r.readRenderTargetPixelsAsync) { this.r.readRenderTargetPixels(a.irr, 0, 0, 1, 1, a.px); set(a.px); return; }
    if (this._irrBusy) return;
    this._irrBusy = true;
    this.r.readRenderTargetPixelsAsync(a.irr, 0, 0, 1, 1, a.px).then(() => { set(a.px); this._irrBusy = false; }, () => { this._irrBusy = false; });
  }
  _applyHead() { this.head.intensity = this.lightsOn ? 34 : 0; }
  setHeadlights(on) { if (on !== this.lightsOn) { this.lightsOn = on; this._applyHead(); } }
  /** Фари фури: пляма світла прив'язана до тягача (model.tractor). */
  attachHeadlights(group) { group.add(this.head, this.head.target); }
  /** Дрібнота (болти, ручки, прилади < r м) тіні не кидає: її тінь у карті 1–2 тис. текселів не видно, а прохід тіні
   *  фури — ~90 draw calls (≈ 0,3 мс CPU кадру). Лише прапорець, геометрію не чіпаємо. */
  trimShadows(root, r = 0.35) {
    const v = new this.T.Vector3();
    let n = 0;
    root.updateMatrixWorld(true);
    root.traverse((o) => {
      if (!o.isMesh || !o.castShadow) return;
      if (!o.geometry.boundingSphere) o.geometry.computeBoundingSphere();
      o.getWorldScale(v);
      if (o.geometry.boundingSphere.radius * Math.max(v.x, v.y, v.z) < r) { o.castShadow = false; n++; }
    });
    return n;
  }

  // ---------------------------------------------------------------- матеріали
  /** Латка неба на один матеріал (раз). Для дерев: латку білборда листя trees.js кладе поверх. */
  patch(m, key) {
    if (!this.patched.has(m)) { this.patched.add(m); patchAtmo(this.T, m, this.U, key || m.type); }
    return m;
  }
  /** Підлатати всі матеріали в дереві root (раз на матеріал): тінь хмар, rim, туман неба. Нові (привиди, позначки) — при наступному виклику. */
  adopt(root) {
    let n = 0;
    root.traverse((o) => {
      if (!o.material || o.userData.noAtmo) return;
      for (const m of Array.isArray(o.material) ? o.material : [o.material]) {
        if (this.patched.has(m) || m.isShaderMaterial || m.userData.noAtmo) continue;
        if (!(m.isMeshStandardMaterial || m.isMeshLambertMaterial || m.isMeshPhongMaterial || m.isMeshBasicMaterial)) continue;
        if (m.blending === this.T.AdditiveBlending) continue;
        this.patch(m, m.type + (m.name || ''));
        n++;
      }
    });
    return n;
  }

  // ---------------------------------------------------------------- налаштування
  /** G — налаштування графіки (gfx.js): clouds, cloudShadows, bloom, grade, aa, pixel, scale, shadows, draw. */
  setGfx(G, W = this.W, H = this.H) {
    this.G = { ...this.G, ...G };
    this.W = W; this.H = H;
    const g = this.G, sun = this.sun;
    sun.castShadow = g.shadows > 0;
    if (g.shadows > 0 && sun.shadow.mapSize.x !== g.shadows) { sun.shadow.mapSize.set(g.shadows, g.shadows); if (sun.shadow.map) { sun.shadow.map.dispose(); sun.shadow.map = null; } }
    const half = g.shadows >= 2048 ? 70 : 50;               // коробка тіні довкола точки перед камерою
    Object.assign(sun.shadow.camera, { left: -half, right: half, top: half, bottom: -half, near: 1, far: 420 });
    sun.shadow.camera.updateProjectionMatrix();
    this.shadowHalf = half;
    this.post.setSize(W, H, g.pixel, g.scale, g.aa === 'msaa');
    const cq = CLOUDQ[g.clouds];
    if (cq) this.clouds.setSize(this.post.rw, this.post.rh, cq[0]);
    this._apply(true);
  }
  resize(W, H) { this.setGfx({}, W, H); }

  // ---------------------------------------------------------------- кадр
  /** Кожен кадр: час, раз на ~0,5 с — світло з неба, тінь — за камерою. */
  tick(dt, camera) {
    this.clock += dt;
    if (this.mode === 'fast' && this.fixedMs == null) this.fastT = (this.fastT + dt / 60) % 24;
    this._acc = (this._acc || 0) + dt;
    if (this._acc > 0.5) { this._acc = 0; this._apply(false); }
    this.follow(camera);
  }
  /** Коробка тіні — перед камерою, прив'язана до сітки текселів тіні (тінь не «повзе» при русі). */
  follow(camera) {
    const T = this.T, st = this.st, sun = this.sun;
    const f = this._f || (this._f = new T.Vector3());
    camera.getWorldDirection(f); f.y = 0; if (f.lengthSq() < 1e-6) f.set(0, 0, 1); f.normalize();
    const c = camera.position.clone().addScaledVector(f, this.shadowHalf * 0.55);
    const Ld = st.night ? st.moonDir : st.sunDir;
    const up = Math.abs(Ld.y) > 0.99 ? new T.Vector3(1, 0, 0) : new T.Vector3(0, 1, 0);
    const r = new T.Vector3().crossVectors(up, Ld).normalize(), u = new T.Vector3().crossVectors(Ld, r);
    const tex = 2 * this.shadowHalf / Math.max(1, sun.shadow.mapSize.x);
    const pr = c.dot(r), pu = c.dot(u);
    c.addScaledVector(r, Math.round(pr / tex) * tex - pr).addScaledVector(u, Math.round(pu / tex) * tex - pu);
    sun.target.position.copy(c); sun.position.copy(c).addScaledVector(Ld, 220);
    sun.target.updateMatrixWorld(); sun.updateMatrixWorld();
  }
  _prepCam(camera) {
    const rc = this.rcam, post = this.post, g = this.G;
    rc.position.copy(camera.position); rc.quaternion.copy(camera.quaternion);
    rc.fov = camera.fov; rc.aspect = camera.aspect; rc.near = camera.near; rc.far = camera.far;
    this.off.set(0, 0);
    if (post.low) {
      if (g.pixel > 1) {                                       // піксель: камера — по сітці текселів (§15.3)
        const s = 2 * 25 * Math.tan(rc.fov * Math.PI / 360) / post.hl;
        const vR = this._vR.set(1, 0, 0).applyQuaternion(rc.quaternion), vU = this._vU.set(0, 1, 0).applyQuaternion(rc.quaternion);
        const pr = rc.position.dot(vR), pu = rc.position.dot(vU), sr = Math.round(pr / s) * s, su = Math.round(pu / s) * s;
        rc.position.addScaledVector(vR, sr - pr).addScaledVector(vU, su - pu);
        this.off.set((pr - sr) / s, (pu - su) / s);
      }
      rc.setViewOffset(post.wl, post.hl, -1, -1, post.wl + 2, post.hl + 2);
    } else rc.clearViewOffset();
    rc.updateProjectionMatrix(); rc.updateMatrixWorld();
  }
  _drawClouds() {
    const cq = CLOUDQ[this.G.clouds];
    if (!cq) return;
    if (cq[4] > 1 && (this.cloudTick++ % cq[4]) !== 0 && this.clouds.hasPrev) return;    // низькі: хмари через кадр
    const L = this.light;
    this.clouds.render(this.rcam, { sky: this.atmo, keyDir: L.keyDir, keyE: L.keyE, sunCol: L.sunCol, ambTop: L.ambTop, ambBot: L.ambBot,
      cover: this.st.cover, time: this.clock, steps: cq[1], lightSteps: cq[2], detail: cq[3], blend: 0.85 });
    this.U.tClouds.value = this.clouds.texture;
  }
  /** Сцена → HDR-ціль (хмари, купол, тінь), без посту. Розбито, щоб замір міг поставити таймери між частинами. */
  drawScene(scene, camera, q) {
    this._prepCam(camera);
    this.U.uTime.value = this.clock;
    if (q) q.begin('clouds');
    this._drawClouds();
    if (q) q.end('clouds');
    if (q) q.begin('scene');
    this.dome.position.copy(this.rcam.position);
    this.U.uRes.value.set(this.post.rw, this.post.rh);
    this.r.shadowMap.needsUpdate = true;
    this.r.setRenderTarget(this.post.rt);
    this.r.render(scene, this.rcam);
    if (q) q.end('scene');
  }
  drawPost(q) {
    if (q) q.begin('post');
    this.post.finish(this.clock, this.off);
    if (q) q.end('post');
  }
  draw(scene, camera, q) { this.drawScene(scene, camera, q); this.drawPost(q); }

  /** Після відновлення WebGL-контексту: вміст цілей пропав — таблиці, шум хмар, оточення печемо знову. */
  restore() {
    this.atmo.bake();
    this.clouds.bake();
    this.clouds.hasPrev = false;
    this._env = null;
    this._apply(true);
  }
  dispose() {
    for (const x of [this.pmrem, this._env, this._lut]) try { x && x.dispose(); } catch { /* пусто */ }
  }
  info() {
    const st = this.st;
    return { hour: this.hourText, elev: Math.round(st.elev * 10) / 10, cover: Math.round(st.cover * 100) / 100, mode: this.mode, fixed: this.fixedMs != null,
      night: st.night, dark: this.dark, exposure: Math.round(this.exposure * 100) / 100, cloudBakeMs: this.clouds.bakeMs };
  }
}
