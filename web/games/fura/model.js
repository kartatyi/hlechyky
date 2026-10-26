// «Фура 2.0» (хвиля E): тягач і причіп у тих самих габаритах і осях, що тіла truck.js (+Z вперед, праворуч −X), — але злиті
// за матеріалами: зовні ≤ 12 draw calls (фарба, темне, хром, скло, вогні, номер, колеса-інстанси; причіп — атлас, темне,
// вогні, опори, колеса), STYLE §9. Салон — cab.js (з камери «з кабіни»), дзеркала — mirrors.js (render-to-texture).
// Кабіна зовні з кабіни не ховається: усе в ній однобоке й дивиться назовні, тож зсередини його відкидає back-face culling,
// а тінь фури лишається й у виді з кабіни.
// Вигляд із Лавки «🚚 Фура» (setLook): фарба й «Металік», розпис на кабіні й борті, річ на панелі, наліпки й номер на задніх
// дверях, вимпел, люстра; хвиля E — чохли, кермо, шторки, свій номер, бруд і вм'ятини. Привид колони (buildGhost) — та сама
// форма грубо: фарба, наліпки й номер.

import { TRACTOR, TRAILER } from './truck.js';
import { buildCab, makeKit, rbox, chainPatch, slotOf } from './cab.js';
import { makeMirrors } from './mirrors.js';

// PBR фури (0.0.5, гібрид B, STYLE.md §15.1): шорсткість і метал — за кольором (фарба з лаком, хром, скло, гума);
// оточення — небо тієї ж миті (sky.js), тож хром і скло відбивають небо
const PBR = { '#b5482f': [0.38, 0.1], '#c9ccd1': [0.25, 1], '#9aa0a8': [0.25, 1], '#26394d': [0.05, 0.2], '#1d1d1f': [0.95, 0] };
export const truckMaterial = (THREE, o) => {
  const [roughness, metalness] = PBR[String(o.color).toLowerCase()] || [0.8, 0];
  return new THREE.MeshStandardMaterial({ roughness, metalness, ...o });
};

/** Оточення для сцен без неба (постамент гаража, garage.js): без нього хром і «Металік» PBR майже чорні. Небо гри
 *  (sky.js) ставить своє оточення раніше, ніж будується фура, — тоді цього не треба. */
function studioEnv(THREE) {
  const W = 64, H = 32, d = new Uint8Array(W * H * 4), top = new THREE.Color('#cfe0f0'), hor = new THREE.Color('#eee6d6'), low = new THREE.Color('#3a3530'), c = new THREE.Color();
  for (let y = 0; y < H; y++) {
    const v = 1 - (y + 0.5) / H;                       // 1 — зеніт (рядок 0 — верх), 0 — надир
    if (v > 0.5) c.copy(hor).lerp(top, (v - 0.5) * 2); else c.copy(low).lerp(hor, Math.pow(v * 2, 3)); c.convertLinearToSRGB();   // текстура — sRGB
    for (let x = 0; x < W; x++) { const i = (y * W + x) * 4; d[i] = c.r * 255; d[i + 1] = c.g * 255; d[i + 2] = c.b * 255; d[i + 3] = 255; }
  }
  const t = new THREE.DataTexture(d, W, H);
  t.mapping = THREE.EquirectangularReflectionMapping; t.colorSpace = THREE.SRGBColorSpace; t.magFilter = t.minFilter = THREE.LinearFilter;
  t.needsUpdate = true;
  return t;
}

// ---------------------------------------------------------------- латки матеріалів
/** Вогні одним матеріалом: колір вершини — колір світла; чисто білі (фари) світять uHead, решта (стопи, габарити) — uSig. */
function lampMaterial(THREE, Base, o) {
  const m = new Base({ vertexColors: true, emissive: '#ffffff', ...o });
  m.userData.k = { uHead: { value: 0.5 }, uSig: { value: 0.4 } };
  return chainPatch(m, 'lamp', (sh) => {
    Object.assign(sh.uniforms, m.userData.k);
    sh.fragmentShader = 'uniform float uHead, uSig;\n' + sh.fragmentShader.replace('#include <emissivemap_fragment>',
      '#include <emissivemap_fragment>\ntotalEmissiveRadiance *= vColor.rgb * mix(uSig, uHead, step(0.97, min(vColor.r, min(vColor.g, vColor.b))));');
  });
}

let grimeT = null;
/** Шум для бруду й подряпин (R — плями, G — подряпини, B — вм'ятини), тайлиться. */
function grimeTexture(THREE) {
  if (grimeT) return grimeT;
  const N = 128, c = document.createElement('canvas'); c.width = c.height = N;
  const g = c.getContext('2d'), img = g.createImageData(N, N), d = img.data;
  let s = 12345;
  const rnd = () => ((s = (s * 16807) % 2147483647) / 2147483647);
  const blobs = Array.from({ length: 40 }, () => [rnd() * N, rnd() * N, 4 + rnd() * 14]);
  const dents = Array.from({ length: 14 }, () => [rnd() * N, rnd() * N, 6 + rnd() * 10]);
  for (let y = 0; y < N; y++) for (let x = 0; x < N; x++) {
    let r = rnd() * 0.25, b = 0.5;
    for (const [bx, by, br] of blobs) for (const [ox, oy] of [[0, 0], [N, 0], [-N, 0], [0, N], [0, -N]]) { const q = Math.hypot(x - bx - ox, y - by - oy) / br; if (q < 1) r += (1 - q) * 0.6; }
    for (const [bx, by, br] of dents) { const q = Math.hypot(x - bx, y - by) / br; if (q < 1) b += Math.cos(q * Math.PI) * 0.35 * (x - bx) / br; }
    const i = (y * N + x) * 4;
    d[i] = Math.min(255, r * 255); d[i + 2] = Math.max(0, Math.min(255, b * 255)); d[i + 3] = 255;
  }
  for (let k = 0; k < 26; k++) {                               // подряпини — тонкі риски в каналі G
    let x = rnd() * N, y = rnd() * N; const a = rnd() * Math.PI, L = 10 + rnd() * 30;
    for (let t = 0; t < L; t++) { const i = ((Math.floor(y + N) % N) * N + (Math.floor(x + N) % N)) * 4; d[i + 1] = 255; x += Math.cos(a); y += Math.sin(a) * 0.4; }
  }
  g.putImageData(img, 0, 0);
  grimeT = new THREE.CanvasTexture(c); grimeT.wrapS = grimeT.wrapT = THREE.RepeatWrapping;
  return grimeT;
}
/** Бруд (низ і задок брудніші, шорсткість росте) і вм'ятини з подряпинами — лише вигляд (слоти `dirt`, `dents`). */
function grime(THREE, mat, U, low, top) {
  const R = { value: new THREE.Vector2(low, top) };
  return chainPatch(mat, 'grime', (sh) => {
    Object.assign(sh.uniforms, { uDirt: U.uDirt, uDents: U.uDents, tGrime: U.tGrime, uGrR: R });
    sh.vertexShader = 'varying vec3 vGrP;\n' + sh.vertexShader.replace('#include <begin_vertex>', '#include <begin_vertex>\nvGrP = position;');
    sh.fragmentShader = 'uniform float uDirt, uDents; uniform sampler2D tGrime; uniform vec2 uGrR; varying vec3 vGrP;\n' + sh.fragmentShader
      .replace('#include <color_fragment>', `#include <color_fragment>
        vec4 grN = texture2D(tGrime, vec2((vGrP.z + vGrP.x) * 0.3, vGrP.y * 0.42));
        float grLow = clamp((uGrR.y - vGrP.y) / (uGrR.y - uGrR.x), 0.0, 1.0);
        float grD = clamp(uDirt * (0.2 + 1.2 * grLow * grLow) * (0.45 + 1.1 * grN.r), 0.0, 1.0);
        diffuseColor.rgb = mix(diffuseColor.rgb, vec3(0.25, 0.2, 0.14), grD * 0.88);
        diffuseColor.rgb = mix(diffuseColor.rgb, vec3(0.62, 0.62, 0.6), step(0.5, grN.g) * uDents * 0.7);`)
      .replace('#include <roughnessmap_fragment>', '#include <roughnessmap_fragment>\nroughnessFactor = mix(roughnessFactor, 1.0, grD);')
      .replace('#include <metalnessmap_fragment>', '#include <metalnessmap_fragment>\nmetalnessFactor *= 1.0 - grD;')
      .replace('#include <normal_fragment_maps>', '#include <normal_fragment_maps>\nnormal = normalize(normal + (grN.b - 0.5) * 1.4 * uDents * vec3(1.0, 0.5, 1.0));');
  });
}

/** Колесо: профіль обертання (шина з плечима, обід із диском і маточиною) в одиницях — радіус 1, ширина 1 уздовж X. */
function wheelGeometry(THREE, seg = 12) {
  const P = [[0, 0.47], [0.3, 0.47], [0.38, 0.4], [0.6, 0.4], [0.66, 0.49], [0.9, 0.5], [1, 0.38], [1, -0.38], [0.9, -0.5], [0.4, -0.5], [0, -0.5]];
  const g = new THREE.LatheGeometry(P.map(([r, y]) => new THREE.Vector2(r, y)), seg);
  g.rotateZ(-Math.PI / 2);
  const p = g.attributes.position, n = p.count, c = new Float32Array(n * 3), cols = P.map(([r]) => (r < 0.62 ? 0.62 : 0.05));
  for (let i = 0; i < n; i++) {
    const row = i % P.length, col = Math.floor(i / P.length);
    let v = cols[row];
    if (row === 2 && col % 2) v = 0.12;                     // отвори диска — видно, що крутиться
    if (row === 1) v = 0.8;
    c[i * 3] = c[i * 3 + 1] = c[i * 3 + 2] = v;
  }
  g.setAttribute('color', new THREE.BufferAttribute(c, 3));
  return g;
}

export function buildModels(THREE, scene) {
  if (scene && !scene.environment) { scene.environment = studioEnv(THREE); scene.environmentIntensity = 0.7; }
  const GU = { uDirt: { value: 0 }, uDents: { value: 0 }, tGrime: { value: grimeTexture(THREE) } };
  const std = (o) => new THREE.MeshStandardMaterial(o);
  const paint = grime(THREE, std({ color: '#b5482f', roughness: 0.38, metalness: 0.1 }), GU, -0.3, 1.6);
  const gloss = grime(THREE, std({ color: '#b5482f', roughness: 0.16, metalness: 0.55 }), GU, -0.3, 1.6);   // «Металік»
  const dark = grime(THREE, std({ vertexColors: true, roughness: 0.78, metalness: 0 }), GU, -1.0, 0.8);
  const chrome = grime(THREE, std({ color: '#c9ccd1', roughness: 0.22, metalness: 1 }), GU, -1.0, 0.6);
  const glass = std({ color: '#26394d', roughness: 0.05, metalness: 0.2, emissive: '#0b1520' });
  const lamps = lampMaterial(THREE, THREE.MeshStandardMaterial, { roughness: 0.3 });
  const tyres = std({ vertexColors: true, roughness: 0.85, metalness: 0 });
  const wheelGeo = wheelGeometry(THREE);
  const B = (w, h, d) => new THREE.BoxGeometry(w, h, d), RB = (w, h, d, r) => rbox(THREE, w, h, d, r), PL = (w, h) => new THREE.PlaneGeometry(w, h);

  // ---------------- тягач
  const tractor = new THREE.Group();
  const ext = new THREE.Group();          // зовнішня кабіна (зсередини її не видно — однобока)
  tractor.add(ext);
  const k = makeKit(THREE), P = k.put;
  // кабіна, високий дах, бокові дефлектори
  P('paint', RB(2.48, 2.25, 2.2, 0.14), 0, 1.175, 2.1);
  P('paint', RB(2.4, 0.46, 2.0, 0.18), 0, 2.5, 2.02, { rx: -0.04 });
  for (const s of [1, -1]) P('paint', RB(0.07, 1.9, 0.5, 0.03), s * 1.205, 1.2, 0.78, { ry: s * 0.1 });
  // перед: фартух, бампер, ґрати, фари, двірники, козирок
  P('dark', RB(2.44, 0.4, 0.42, 0.06), 0, -0.12, 3.0, { c: '#2a2b2e' });
  P('dark', RB(2.52, 0.3, 0.34, 0.07), 0, -0.45, 3.07, { c: '#3b3d41' });
  P('dark', B(2.3, 0.06, 0.2), 0, -0.61, 3.12, { c: '#202124' });
  P('dark', PL(1.76, 0.86), 0, 0.55, 3.207, { c: '#141517' });
  for (let i = 0; i < 5; i++) P('chrome', B(1.66, 0.045, 0.04), 0, 0.22 + i * 0.15, 3.225);
  P('chrome', RB(1.1, 0.07, 0.03, 0.015), 0, 1.0, 3.214);
  P('chrome', new THREE.CylinderGeometry(0.07, 0.07, 0.02, 14), 0, 1.0, 3.232, { rx: Math.PI / 2 });
  P('dark', new THREE.CylinderGeometry(0.05, 0.05, 0.02, 14), 0, 1.0, 3.236, { rx: Math.PI / 2, c: '#b5482f' });   // глечик на значку
  P('dark', PL(2.3, 1.05), 0, 1.55, 3.203, { c: '#121314' });                   // гума лобового
  P('glass', PL(2.2, 0.95), 0, 1.55, 3.207);
  for (const s of [1, -1]) P('dark', B(0.8, 0.02, 0.02), s * 0.45, 1.115, 3.226, { rz: s * 0.06, c: '#18191b' });
  P('dark', RB(2.36, 0.05, 0.36, 0.02), 0, 2.1, 3.3, { rx: 0.28, c: '#1e1f22' });
  for (let i = 0; i < 5; i++) P('lamps', B(0.14, 0.05, 0.05), -0.5 + i * 0.25, 2.07, 3.48, { c: '#ffb040' });
  for (const s of [1, -1]) {
    P('dark', RB(0.5, 0.2, 0.05, 0.02), s * 0.92, -0.12, 3.21, { c: '#1a1b1d' });
    P('lamps', PL(0.44, 0.14), s * 0.92, -0.13, 3.237, { c: '#ffffff' });
    P('lamps', PL(0.44, 0.022), s * 0.92, -0.04, 3.237, { c: '#ffffff' });
    P('lamps', PL(0.1, 0.13), s * 1.18, -0.12, 3.233, { c: '#ffa630' });
    // бік: вікно, шви дверей, ручка, поручень, сходинки, крило над переднім колесом
    P('dark', PL(1.1, 0.95), s * 1.242, 1.55, 2.6, { ry: s * Math.PI / 2, c: '#121314' });
    P('glass', PL(1.0, 0.85), s * 1.246, 1.55, 2.6, { ry: s * Math.PI / 2 });
    P('dark', B(0.01, 2.0, 0.012), s * 1.244, 1.1, 1.86, { c: '#202124' });
    P('dark', B(0.01, 2.0, 0.012), s * 1.244, 1.1, 3.05, { c: '#202124' });
    P('dark', B(0.01, 0.012, 1.2), s * 1.244, 0.12, 2.46, { c: '#202124' });
    P('chrome', B(0.02, 0.04, 0.22), s * 1.252, 1.02, 2.02);
    P('chrome', new THREE.CylinderGeometry(0.015, 0.015, 0.9, 6), s * 1.262, 0.85, 1.93);
    P('lamps', B(0.012, 0.05, 0.1), s * 1.247, 0.2, 2.95, { c: '#ffa630' });
    for (const y of [-0.38, -0.75]) P('dark', B(0.42, 0.035, 0.24), s * 1.08, y, 2.84, { c: '#4a4d52' });
    P('dark', B(0.02, 0.5, 0.24), s * 0.88, -0.55, 2.84, { c: '#202124' });
    P('dark', new THREE.CylinderGeometry(0.6, 0.6, 0.44, 12, 1, true, -0.15, Math.PI + 0.3), s * 1.03, -0.62, 2.05, { rz: Math.PI / 2, c: '#1d1e20' });
    P('dark', B(0.44, 0.35, 0.02), s * 1.03, -0.72, 1.42, { c: '#141414' });
    // дзеркала: кронштейни, корпуси, мале ширококутне
    k.bar('dark', [s * 1.24, 1.98, 3.0], [s * 1.53, 1.78, 3.16], 0.035, { c: '#1e1f22' });
    k.bar('dark', [s * 1.24, 1.2, 3.05], [s * 1.53, 1.24, 3.16], 0.035, { c: '#1e1f22' });
    P('dark', RB(0.27, 0.5, 0.11, 0.035), s * 1.55, 1.52, 3.17, { c: '#1e1f22' });
    P('dark', RB(0.22, 0.2, 0.1, 0.03), s * 1.55, 1.1, 3.17, { c: '#1e1f22' });
    P('chrome', PL(0.18, 0.16), s * 1.55, 1.1, 3.118, { ry: Math.PI + s * 0.2 });
    // повітрозабірник (праворуч) / вихлоп (ліворуч)
  }
  P('dark', RB(0.22, 1.6, 0.18, 0.06), -1.1, 1.4, 0.88, { c: '#26272a' });
  P('chrome', new THREE.CylinderGeometry(0.075, 0.075, 2.1, 10), 1.06, 1.55, 0.86);
  for (const s of [1, -1]) P('chrome', new THREE.CylinderGeometry(0.05, 0.02, 0.45, 8), s * 0.35, 2.79, 2.3, { rx: -Math.PI / 2 });   // ріжки
  // рама, баки, сідло, крила, бризковики, задні вогні — видно з обох камер
  for (const s of [1, -1]) P('dark', B(0.12, 0.3, 6.2), s * 0.42, -0.24, -0.35, { c: '#1c1d1f' });
  for (const z of [2.6, 1.2, -0.4, -2.0, -3.3]) P('dark', B(0.72, 0.14, 0.1), 0, -0.24, z, { c: '#1c1d1f' });
  P('chrome', new THREE.CylinderGeometry(0.32, 0.32, 1.3, 16), 0.98, -0.4, 0.45, { rx: Math.PI / 2 });
  for (const z of [0.0, 0.9]) P('dark', new THREE.TorusGeometry(0.325, 0.014, 4, 16), 0.98, -0.4, z, { c: '#1c1d1f' });
  P('dark', RB(0.5, 0.48, 0.7, 0.04), -0.98, -0.38, 0.55, { c: '#2b2c2f' });
  P('chrome', B(0.02, 0.06, 0.12), -1.235, -0.3, 0.55);
  P('dark', new THREE.CylinderGeometry(0.55, 0.55, 0.08, 16), 0, TRACTOR.fifth.y - 0.04, TRACTOR.fifth.z, { c: '#2e2f33' });
  P('dark', B(0.9, 0.16, 0.9), 0, TRACTOR.fifth.y - 0.16, TRACTOR.fifth.z, { c: '#1c1d1f' });
  P('dark', B(0.18, 0.02, 0.5), 0, TRACTOR.fifth.y + 0.001, TRACTOR.fifth.z - 0.3, { c: '#101012' });
  for (const s of [1, -1]) {
    P('dark', RB(0.7, 0.05, 2.5, 0.02), s * 1.0, -0.02, -2.22, { c: '#1d1e20' });
    P('dark', B(0.62, 0.55, 0.02), s * 1.0, -0.82, -3.45, { c: '#141414' });
    for (const z of [-1.0, -2.2, -3.3]) P('lamps', B(0.03, 0.04, 0.09), s * 1.36, -0.02, z, { c: '#ffa630' });
    P('lamps', B(0.24, 0.1, 0.02), s * 0.85, -0.38, -3.46, { c: '#e0281c' });
    P('lamps', B(0.1, 0.1, 0.02), s * 0.6, -0.38, -3.46, { c: '#ffa630' });
    P('lamps', B(0.08, 0.1, 0.02), s * 0.45, -0.38, -3.46, { c: '#eeeee4' });
  }
  P('dark', B(2.1, 0.12, 0.1), 0, -0.38, -3.4, { c: '#222326' });
  // розпис на боках кабіни і номер спереду — свої матеріали
  const artMat = truckMaterial(THREE, { transparent: true, depthWrite: false, polygonOffset: true, polygonOffsetFactor: -2, roughness: 0.4 });
  artMat.visible = false;
  for (const s of [1, -1]) P('art', PL(1.9, 0.82), s * 1.247, 0.6, 2.42, { ry: s * Math.PI / 2 });
  const plateTex = new THREE.CanvasTexture(plateCanvas(null, ''));
  plateTex.colorSpace = THREE.SRGBColorSpace;
  P('plate', PL(0.52, 0.115), 0, -0.45, 3.243);

  const paintMesh = k.mesh('paint', paint, ext);
  k.mesh('glass', glass, ext);
  k.mesh('art', artMat, ext);
  k.mesh('plate', truckMaterial(THREE, { map: plateTex, roughness: 0.5 }), ext);
  k.mesh('dark', dark, tractor);
  k.mesh('chrome', chrome, tractor);
  const tLamps = k.mesh('lamps', lamps, tractor);
  const roof = new THREE.Group();          // вимпел / прапорці / люстра — будуються, коли вдягнуто
  ext.add(roof);

  // салон і дзеркала
  const cab = buildCab(THREE, tractor);
  const cabin = cab.group;
  const mirrors = makeMirrors(THREE, tractor, [1, -1].map((s) => ({ pos: [s * 1.55, 1.52, 3.098], w: 0.23, h: 0.44, out: s })));

  // ---------------- колеса: InstancedMesh на тіло (1 call), матриці — з контролера (syncWheels)
  const tmpM = new THREE.Matrix4(), tmpP = new THREE.Vector3(), tmpS = new THREE.Vector3(), tmpQ2 = new THREE.Quaternion(), tmpE = new THREE.Euler(0, 0, 0, 'YXZ');
  function wheelSet(parent, list, r, widthOf, yOff) {
    const inst = new THREE.InstancedMesh(wheelGeo, tyres, list.length);
    inst.frustumCulled = false;
    parent.add(inst);
    const ws = list.map(([x, y, z], i) => ({ x, y, z, r, w: widthOf(i), i, inst, len: yOff, steer: 0, spin: 0 }));
    for (const w of ws) place(w);
    return ws;
  }
  function place(w) {
    tmpE.set(w.spin, w.steer, 0, 'YXZ'); tmpQ2.setFromEuler(tmpE);
    tmpP.set(w.x, w.y - w.len, w.z); tmpS.set(w.w, w.r, w.r);
    w.inst.setMatrixAt(w.i, tmpM.compose(tmpP, tmpQ2, tmpS));
    w.inst.instanceMatrix.needsUpdate = true;
  }
  const tWheels = wheelSet(tractor, TRACTOR.wheels, TRACTOR.wheelR, (i) => (i >= 2 ? 0.62 : 0.38), TRACTOR.rest);
  // гаражний постамент (без контролера) — колеса в спокої

  // ---------------- причіп: короб одним атласом (борти, задні двері, дах), темне, вогні, опори, колеса
  const trailer = new THREE.Group();
  const atlas = document.createElement('canvas'); atlas.width = atlas.height = 1024;
  const atlasTex = new THREE.CanvasTexture(atlas);
  atlasTex.colorSpace = THREE.SRGBColorSpace; atlasTex.anisotropy = 4;
  const bodyMat = grime(THREE, truckMaterial(THREE, { map: atlasTex, roughness: 0.7 }), GU, -0.2, 1.4);
  const tk = makeKit(THREE), TP = tk.put, L = TRAILER.len, H = TRAILER.h;
  const body = new THREE.BoxGeometry(TRAILER.w, H, L);
  {
    // BoxGeometry: +X, −X, +Y, −Y, +Z, −Z — по 4 вершини; ділянки атласу в пікселях
    const R = [[0, 0, 1024, 208], [0, 208, 1024, 208], [620, 440, 380, 380], [620, 960, 380, 50], [620, 440, 380, 380], [0, 416, 512, 560]];
    const uv = body.attributes.uv;
    for (let f = 0; f < 6; f++) {
      const [x, y, w, h] = R[f];
      for (let j = 0; j < 4; j++) { const i = f * 4 + j; uv.setXY(i, (x + uv.getX(i) * w) / 1024, 1 - (y + (1 - uv.getY(i)) * h) / 1024); }
    }
    body.clearGroups();
  }
  TP('body', body, 0, H / 2, 0);
  TP('dark', B(1.1, 0.26, 13.0), 0, -0.14, 0, { c: '#1f2022' });
  for (const s of [1, -1]) {
    TP('dark', B(0.07, 0.08, L), s * 1.255, H - 0.03, 0, { c: '#9da2a8' });
    TP('dark', B(0.08, 0.14, L), s * 1.255, 0.05, 0, { c: '#8b8f96' });
    for (const z of [-L / 2 + 0.04, L / 2 - 0.04]) TP('dark', B(0.09, H, 0.09), s * 1.235, H / 2, z, { c: '#9da2a8' });
    TP('dark', B(0.05, 0.3, 5.2), s * 1.18, -0.32, 0.2, { c: '#8b8f96' });
    for (const z of [2.6, 0.2, -2.2]) TP('dark', B(0.04, 0.34, 0.06), s * 1.16, -0.3, z, { c: '#6d7178' });
    TP('dark', RB(0.68, 0.03, 3.4, 0.01), s * 1.0, -0.3, -5.0, { c: '#1d1e20' });
    TP('dark', B(0.62, 0.48, 0.02), s * 1.0, -0.86, -6.88, { c: '#141414' });
    TP('dark', B(0.1, 0.42, 0.1), s * 0.75, -0.38, -6.7, { c: '#8b8f96' });
    TP('lamps', B(0.26, 0.1, 0.02), s * 0.95, -0.36, -6.835, { c: '#e0281c' });
    TP('lamps', B(0.1, 0.1, 0.02), s * 0.7, -0.36, -6.835, { c: '#ffa630' });
    TP('lamps', B(0.08, 0.1, 0.02), s * 0.55, -0.36, -6.835, { c: '#eeeee4' });
    TP('lamps', B(0.1, 0.05, 0.03), s * 1.15, H - 0.06, -L / 2 - 0.01, { c: '#e0281c' });
    TP('lamps', B(0.1, 0.05, 0.03), s * 1.15, H - 0.06, L / 2 + 0.01, { c: '#f4f4ea' });
    for (const z of [5.5, 2.75, 0, -2.75, -5.5]) TP('lamps', B(0.03, 0.05, 0.1), s * 1.27, 0.05, z, { c: '#ffa630' });
  }
  for (const x of [-0.9, -0.3, 0.3, 0.9]) TP('dark', new THREE.CylinderGeometry(0.018, 0.018, H - 0.25, 6), x, H / 2, -L / 2 - 0.025, { c: '#9a9a9a' });
  TP('dark', RB(2.4, 0.16, 0.12, 0.02), 0, -0.62, -6.72, { c: '#8b8f96' });
  TP('dark', B(2.3, 0.12, 0.08), 0, -0.36, -6.79, { c: '#222326' });
  TP('dark', B(1.2, 0.02, 1.2), 0, -0.01 - 0.26, TRAILER.kingpin.z, { c: '#2a2b2e' });
  TP('dark', new THREE.CylinderGeometry(0.05, 0.05, 0.12, 8), TRAILER.kingpin.x, -0.06, TRAILER.kingpin.z, { c: '#55575c' });
  TP('dark', RB(0.45, 0.4, 0.8, 0.03), -0.85, -0.3, 1.2, { c: '#2b2c2f' });
  tk.mesh('body', bodyMat, trailer);
  tk.mesh('dark', dark, trailer);
  tk.mesh('lamps', lamps, trailer);
  const legs = new THREE.Group();
  const lk = makeKit(THREE);
  for (const s of [1, -1]) { lk.put('l', B(0.12, 1.15, 0.12), s * 0.85, -0.6, 3.2, { c: '#6d7178' }); lk.put('l', B(0.3, 0.06, 0.3), s * 0.85, -1.2, 3.2, { c: '#55585e' }); }
  lk.put('l', B(1.9, 0.08, 0.08), 0, -0.4, 3.2, { c: '#6d7178' });
  lk.put('l', B(0.04, 0.04, 0.3), 1.0, -0.3, 3.3, { c: '#33353a' });
  lk.mesh('l', dark, legs);
  trailer.add(legs);
  const rWheels = wheelSet(trailer, TRAILER.wheels, TRAILER.wheelR, () => 0.62, TRAILER.rest);

  for (const g of [tractor, trailer]) g.traverse((o) => { if (o.isMesh) { o.castShadow = true; o.receiveShadow = true; } });
  // салон не кидає й не ловить тіней: інакше стеля кабіни затемнює все всередині до чорного
  cabin.traverse((o) => { if (o.isMesh) { o.castShadow = false; o.receiveShadow = false; } });
  for (const m of mirrors.meshes) { m.castShadow = false; m.receiveShadow = false; }
  for (const m of [tLamps]) if (m) m.castShadow = false;
  scene.add(tractor, trailer);

  // ---------------- вигляд (setLook)
  let artNow = null, cargoNow = null, lampMat = null, lightsOn = false, atlasKey = '', stickers = [], plateNow = '';
  function drawAtlas() {
    const key = JSON.stringify([cargoNow && cargoNow.id, cargoNow && cargoNow.title, artNow, stickers, plateNow]);
    if (key === atlasKey) return;
    atlasKey = key;
    const g = atlas.getContext('2d');
    g.fillStyle = '#efe6d2'; g.fillRect(0, 0, 1024, 1024);
    g.fillStyle = '#e4dac4'; g.fillRect(620, 440, 380, 380);
    g.fillStyle = '#1f2022'; g.fillRect(620, 960, 380, 50);
    g.drawImage(sideCv(cargoNow, false, artNow), 0, 0);
    g.drawImage(sideCv(cargoNow, true, artNow), 0, 208);
    g.drawImage(backCanvas(null, stickers, plateNow), 0, 416);
    atlasTex.needsUpdate = true;
  }
  function sideCv(cargo, potRight, art) { const c = document.createElement('canvas'); sideTexture(THREE, cargo, c, potRight, art); return c; }
  function buildRoof(look) {
    while (roof.children.length) { const o = roof.children[0]; roof.remove(o); o.geometry && o.geometry.dispose(); }
    lampMat = null;
    const rk = makeKit(THREE);
    if (look.flag === 'flagvympel') {
      rk.put('c', new THREE.CylinderGeometry(0.02, 0.02, 0.9, 6), -1.05, 3.15, 1.2);
      const sh = new THREE.Shape(); sh.moveTo(0, 0); sh.lineTo(0, 0.28); sh.lineTo(-0.7, 0.14); sh.closePath();
      rk.put('f', new THREE.ShapeGeometry(sh), -1.05, 3.3, 1.2, { ry: Math.PI / 2, c: '#c8322a' });
    } else if (look.flag === 'flagua') {
      for (const s of [1, -1]) {
        rk.put('c', new THREE.CylinderGeometry(0.015, 0.015, 0.6, 6), s * 1.15, 2.5, 3.05);
        for (const [c, y] of [['#2a5bd7', 2.7], ['#f5d000', 2.58]]) rk.put('f', PL(0.34, 0.12), s * 1.15 - s * 0.17, y, 3.05, { ry: Math.PI / 2, c });
      }
    }
    if (look.lamp) {
      const gold = look.lamp === 'lampotaman';
      rk.put('d', B(2.1, 0.12, 0.16), 0, 2.86, 2.75, { c: gold ? '#b8902a' : '#2a2b2e' });
      for (let i = 0; i < 6; i++) rk.put('l', B(0.26, 0.08, 0.02), -0.82 + i * 0.33, 2.86, 2.84);
      lampMat = truckMaterial(THREE, { color: gold ? '#fff0b0' : '#fff7dd', emissive: gold ? '#ffcc40' : '#fff0c0', emissiveIntensity: lightsOn ? 2.6 : 0.5 });
      rk.mesh('l', lampMat, roof);
    }
    rk.mesh('c', chrome, roof);
    rk.mesh('f', truckMaterial(THREE, { vertexColors: true, side: THREE.DoubleSide, roughness: 0.8 }), roof);
    rk.mesh('d', dark, roof);
    roof.traverse((o) => { if (o.isMesh) o.castShadow = true; });
  }
  const num = (v) => (typeof v === 'number' && isFinite(v) ? Math.max(0, Math.min(1, v)) : 0);
  /** Вигляд із виду (`career.look` / `convoy[].car.look`): ids речей Лавки «🚚 Фура»; nick — для номера, коли свого нема. */
  function setLook(look, nick) {
    look = look || {};
    const pc = paintOf(look), rb = pc === 'rainbow';
    const col = rb ? '#ffffff' : pc || '#b5482f';
    for (const m of [paint, gloss]) {
      m.color.set(col);
      m.map = rb ? rainbowTexture(THREE) : null;
      m.needsUpdate = true;
    }
    paintMesh.material = look.gloss ? gloss : paint;
    const art = look.art || null;
    if (art !== artNow) {
      artNow = art;
      if (artMat.map) { artMat.map.dispose(); artMat.map = null; }
      if (art) { const t = new THREE.CanvasTexture(artCanvas(art, 512, 256)); t.colorSpace = THREE.SRGBColorSpace; artMat.map = t; }
      artMat.visible = !!art; artMat.needsUpdate = true;
    }
    cab.setDashItem(look.dash || null);
    cab.setSeat(slotOf(look, 'seat'));
    cab.setWheel(slotOf(look, 'wheel'));
    cab.setCurtain(slotOf(look, 'curtain'));
    plateNow = plateText(look, nick);
    plateCanvas(plateTex.image, plateNow); plateTex.needsUpdate = true;
    stickers = (look.stickers || []).slice(0, 3);
    drawAtlas();
    GU.uDirt.value = num(look.dirt); GU.uDents.value = num(look.dents);
    buildRoof(look);
  }

  const tmpQ = new THREE.Quaternion();
  let cabinOn = false;
  drawAtlas();
  return {
    tractor, trailer, ext, cabin,
    /** Око водія в осях тягача (placeCamera, main.js). */
    eye: cab.eye,
    /** Поза тіла (уже згладжена в main.js) і колеса з контролера. */
    syncBody(group, pos, quat) { group.position.copy(pos); group.quaternion.copy(quat); },
    syncWheels(vc, list, steerSign) {
      for (let i = 0; i < list.length; i++) {
        const w = list[i];
        const len = vc.wheelSuspensionLength(i);
        w.len = len == null ? 0.4 : len;
        w.steer = (vc.wheelSteering(i) || 0) * steerSign;
        w.spin = -(vc.wheelRotation(i) || 0);
        place(w);
      }
    },
    tWheels, rWheels, legs, wheelPivot: cab.wheelPivot, jug: cab.jug,
    setCabin(on) { if (on === cabinOn) return; cabinOn = on; cabin.visible = on; mirrors.setCabin(on); },
    /** Прилади в кабіні (placeCamera, ≤ 10 Гц). */
    updateCab(now, truck) { if (cabinOn) cab.update(now, truck); },
    /** Гачок кадру (main.js, перед небом): світло салону й одне дзеркало — лише з камери «з кабіни». */
    renderMirrors(renderer, sc, camera, o) {
      if (!cabinOn) return;
      cab.light(o && o.sky);
      mirrors.render(renderer, sc, camera, o, (pass) => { cabin.visible = !pass; });
    },
    /** Фари (L): у колоні їх бачать і інші — прапорець 2 у позі. Люстра з Лавки світиться з фарами; прилади — підсвітка. */
    setLights(on) {
      lamps.userData.k.uHead.value = on ? 2.4 : 0.5; lamps.userData.k.uSig.value = on ? 1.3 : 0.4;
      lightsOn = on; if (lampMat) lampMat.emissiveIntensity = on ? 2.6 : 0.5;
      cab.setNight(on);
    },
    setLook,
    /** Пружина від прискорення причепа (main.js): хитає «Глечик на панелі» й речі на нитці; кіт кивує. Без речі глечик стоїть. */
    setDash(rx, rz, now) { cab.setDash(rx, rz, now); },
    setLegs(down) { legs.position.y = down ? 0 : 0.55; legs.scale.y = down ? 1 : 0.5; },
    /** Борт причепа під вантаж замовлення: колір смуги й підпис. */
    setCargo(cargo) { cargoNow = cargo; drawAtlas(); },
    tmpQ,
  };
}

/** Номер: свій із виду (`look.plate`, ≤ 8 знаків — сервер чистить) або з ніка. */
function plateText(look, nick) {
  const p = look && typeof look.plate === 'string' ? look.plate.replace(/[^0-9A-Za-zА-ЯҐЄІЇа-яґєії ]/g, '').trim().toUpperCase().slice(0, 10) : '';
  return p || plateOf(nick);
}

/** Чужа фура колони — «привид»: та сама форма грубо (кабіна з дахом, рама, кузов, колеса), без салону, злита за матеріалами
 *  (~8 draw calls), щоб вісім фур коштували менше за одну свою. Осі ті самі, що в buildModels; фари, стоп — з прапорців кадру. */
export function buildGhost(THREE, scene, color) {
  const mats = [], geos = [];
  const M = (o) => { const m = new THREE.MeshLambertMaterial(o); mats.push(m); return m; };
  const paint = M({ color }), body = M({ vertexColors: true }), lamp = lampMaterial(THREE, THREE.MeshLambertMaterial, {});
  mats.push(lamp);
  lamp.userData.k.uHead.value = 0.25; lamp.userData.k.uSig.value = 0.15;
  const B = (w, h, d) => new THREE.BoxGeometry(w, h, d), RB = (w, h, d, r) => rbox(THREE, w, h, d, r);
  const tractor = new THREE.Group(), trailer = new THREE.Group();
  const k = makeKit(THREE), P = k.put;
  P('p', RB(2.48, 2.25, 2.2, 0.14), 0, 1.175, 2.1);
  P('p', RB(2.4, 0.46, 2.0, 0.18), 0, 2.5, 2.02);
  P('b', new THREE.PlaneGeometry(2.2, 0.95), 0, 1.55, 3.207, { c: '#26394d' });
  for (const s of [1, -1]) {
    P('b', new THREE.PlaneGeometry(1.0, 0.85), s * 1.246, 1.55, 2.6, { ry: s * Math.PI / 2, c: '#26394d' });
    P('b', B(0.27, 0.5, 0.11), s * 1.55, 1.52, 3.17, { c: '#1e1f22' });
    P('b', B(0.7, 0.05, 2.5), s * 1.0, -0.02, -2.22, { c: '#1d1e20' });
    P('l', B(0.44, 0.14, 0.02), s * 0.92, -0.13, 3.235, { c: '#ffffff' });
    P('l', B(0.24, 0.1, 0.02), s * 0.85, -0.38, -3.46, { c: '#e0281c' });
  }
  P('b', B(2.44, 0.4, 0.42), 0, -0.12, 3.0, { c: '#2a2b2e' });
  P('b', B(2.52, 0.3, 0.34), 0, -0.45, 3.07, { c: '#3b3d41' });
  P('b', B(1.0, 0.3, 6.2), 0, -0.24, -0.35, { c: '#1c1d1f' });
  P('b', new THREE.CylinderGeometry(0.32, 0.32, 1.3, 8), 0.98, -0.4, 0.45, { rx: Math.PI / 2, c: '#9aa0a8' });
  k.mesh('p', paint, tractor); k.mesh('b', body, tractor); k.mesh('l', lamp, tractor);
  const tk = makeKit(THREE), L = TRAILER.len, H = TRAILER.h;
  tk.put('b', B(TRAILER.w, H, L), 0, H / 2, 0, { c: '#efe6d2' });
  tk.put('b', B(TRAILER.w + 0.02, 0.3, L - 0.4), 0, 0.35, 0, { c: color });            // смуга колонника (колір місця)
  tk.put('b', B(1.1, 0.26, 13.0), 0, -0.14, 0, { c: '#2a2b2e' });
  for (const s of [1, -1]) tk.put('l', B(0.3, 0.14, 0.02), s * 0.95, -0.35, -6.84, { c: '#e0281c' });
  tk.mesh('b', body, trailer); tk.mesh('l', lamp, trailer);
  const wgeo = new THREE.CylinderGeometry(1, 1, 1, 10); wgeo.rotateZ(Math.PI / 2); geos.push(wgeo);
  const tyre = M({ color: '#1d1d1f' });
  const sets = [];
  const wheels = (parent, list, r, widthOf, dy) => {
    const inst = new THREE.InstancedMesh(wgeo, tyre, list.length); inst.frustumCulled = false; parent.add(inst);
    sets.push({ inst, ws: list.map(([x, y, z], i) => ({ x, y: y - dy, z, r, w: widthOf(i) })) });
  };
  wheels(tractor, TRACTOR.wheels, TRACTOR.wheelR, (i) => (i >= 2 ? 0.62 : 0.38), 0.42);
  wheels(trailer, TRAILER.wheels, TRAILER.wheelR, () => 0.62, 0.5);
  const m4 = new THREE.Matrix4(), q = new THREE.Quaternion(), e = new THREE.Euler(), p = new THREE.Vector3(), s3 = new THREE.Vector3();
  const spin = (a) => {
    q.setFromEuler(e.set(a, 0, 0));
    for (const st of sets) { st.ws.forEach((w, i) => st.inst.setMatrixAt(i, m4.compose(p.set(w.x, w.y, w.z), q, s3.set(w.w, w.r, w.r)))); st.inst.instanceMatrix.needsUpdate = true; }
  };
  spin(0);
  tractor.traverse((o) => { if (o.isMesh) { geos.push(o.geometry); o.castShadow = true; o.receiveShadow = false; } });
  trailer.traverse((o) => { if (o.isMesh) { geos.push(o.geometry); o.castShadow = true; o.receiveShadow = false; } });
  scene.add(tractor, trailer);
  let flags = -1, backMesh = null, lookKey = '';
  return {
    tractor, trailer,
    /** Колеса крутяться від швидкості кадру (ghosts.js). */
    spin,
    /** f: 1 сигнал, 2 фари, 4 стоп. */
    setFlags(f) {
      if (f === flags) return;
      flags = f;
      lamp.userData.k.uHead.value = f & 2 ? 2.2 : 0.25;
      lamp.userData.k.uSig.value = f & 4 ? 2.4 : f & 2 ? 0.8 : 0.15;
    },
    setVisible(on) { tractor.visible = trailer.visible = on; },
    /** Вигляд чужої фури з `convoy[].car.look`: фарба кабіни (смуга лишається кольором місця), наліпки й номер ззаду. */
    setLook(look, nick) {
      const key = JSON.stringify([look, nick]);
      if (key === lookKey) return;
      lookKey = key;
      const pc = paintOf(look);
      paint.color.set(pc === 'rainbow' ? '#e0b030' : pc || color);
      if (!backMesh) {
        const t = new THREE.CanvasTexture(backCanvas(null, [], ''));
        t.colorSpace = THREE.SRGBColorSpace;
        const m = M({ color: '#ffffff' }); m.map = t;
        const geo = new THREE.PlaneGeometry(TRAILER.w - 0.05, TRAILER.h - 0.05); geos.push(geo);
        backMesh = new THREE.Mesh(geo, m);
        backMesh.position.set(0, TRAILER.h / 2, -TRAILER.len / 2 - 0.02); backMesh.rotation.y = Math.PI;
        trailer.add(backMesh);
      }
      backCanvas(backMesh.material.map.image, (look && look.stickers) || [], plateText(look, nick));
      backMesh.material.map.needsUpdate = true;
    },
    dispose() {
      scene.remove(tractor, trailer);
      for (const g of geos) g.dispose();
      for (const m of mats) { if (m.map) m.map.dispose(); m.dispose(); }
    },
  };
}

// ---------------- вигляд з Лавки «🚚 Фура»: id речей — Lavka.cs (група fura-*)
/** Фарби — ті самі відтінки, що кольори ніка (hue). */
export const PAINT = { paintember: 0, paintamber: 32, paintlinden: 62, paintgrass: 85, paintemerald: 110, paintmint: 135,
  paintsky: 195, paintcornflower: 212, paintsapphire: 228, paintlilac: 248, paintviolet: 270, paintraspberry: 318 };
export const STICKERS = {
  stickhlechyky: { text: 'ГЛЕЧИКИ', icon: '🏺', bg: '#b5482f', fg: '#fff3dc' },
  stickshlyakh: { text: 'ЧУМАЦЬКИЙ ШЛЯХ', icon: '✨', bg: '#23315c', fg: '#f2e6b8' },
  stickmama: { text: 'ЇДУ ДО МАМИ', icon: '💛', bg: '#f2c94c', fg: '#3a2a05' },
  stickmakitry: { text: 'ОБЕРЕЖНО: МАКІТРИ', icon: '⚠️', bg: '#f0a030', fg: '#2a1a05' },
  stickstar: { text: 'ЗОРЯНИЙ ЧУМАК', icon: '⭐', bg: '#1d2a4a', fg: '#ffd96a' },
};
/** Колір кабіни з вигляду: '#rrggbb', 'rainbow' або null («Чумак» як є). */
export function paintOf(look) {
  const id = look && look.paint;
  if (!id) return null;
  if (id === 'paintrainbow') return 'rainbow';
  const h = PAINT[id];
  return h == null ? null : hsl(h, 0.58, 0.42);
}
function hsl(h, s, l) {
  const a = s * Math.min(l, 1 - l), f = (n) => { const k = (n + h / 30) % 12; return l - a * Math.max(-1, Math.min(k - 3, 9 - k, 1)); };
  return '#' + [f(0), f(8), f(4)].map((x) => Math.round(x * 255).toString(16).padStart(2, '0')).join('');
}
/** Номерний знак з ніка (свого номера сервер ще не дає): «ГЛ 0427 ЧК» — у всіх у колоні той самий. */
export function plateOf(nick) {
  let h = 2166136261;
  for (const ch of String(nick || 'чумак').toLowerCase()) { h ^= ch.codePointAt(0); h = Math.imul(h, 16777619) >>> 0; }
  const L = ['ЧК', 'ГЛ', 'ВЗ', 'КР', 'ПЛ', 'МД', 'СЛ', 'ДН'];
  return L[h % 8] + ' ' + String(h % 10000).padStart(4, '0') + ' ' + L[(h >>> 8) % 8];
}

let rainbowT = null;
function rainbowTexture(THREE) {
  if (rainbowT) return rainbowT;
  const c = document.createElement('canvas'); c.width = 8; c.height = 256;
  const g = c.getContext('2d'), gr = g.createLinearGradient(0, 0, 0, 256);
  ['#e53935', '#fb8c00', '#fdd835', '#43a047', '#1e88e5', '#3949ab', '#8e24aa'].forEach((x, i, a) => gr.addColorStop(i / (a.length - 1), x));
  g.fillStyle = gr; g.fillRect(0, 0, 8, 256);
  rainbowT = new THREE.CanvasTexture(c); rainbowT.colorSpace = THREE.SRGBColorSpace;
  return rainbowT;
}

/** Номерний знак: білий, синя смужка «UA» ліворуч. */
function plateCanvas(c, text) {
  c = c || document.createElement('canvas');
  c.width = 256; c.height = 56;
  const g = c.getContext('2d');
  g.fillStyle = '#f7f7f2'; g.fillRect(0, 0, 256, 56);
  g.strokeStyle = '#222'; g.lineWidth = 3; g.strokeRect(2, 2, 252, 52);
  g.fillStyle = '#1f4fbf'; g.fillRect(4, 4, 30, 48);
  g.fillStyle = '#f5d000'; g.fillRect(10, 10, 18, 12);
  g.fillStyle = '#fff'; g.font = '700 14px system-ui'; g.textAlign = 'center'; g.fillText('UA', 19, 44);
  g.fillStyle = '#111'; g.font = '800 34px system-ui'; g.textBaseline = 'middle';
  g.fillText(text || '', 146, 30, 206);
  return c;
}

/** Задні двері причепа: стулки, до трьох наліпок, номер унизу. */
function backCanvas(c, stickers, plate) {
  c = c || document.createElement('canvas');
  c.width = 512; c.height = 560;
  const g = c.getContext('2d');
  g.fillStyle = '#e2d7bf'; g.fillRect(0, 0, 512, 560);
  g.fillStyle = '#c7bba1'; g.fillRect(253, 0, 6, 560);
  for (const x of [128, 384]) { g.fillStyle = '#a99d84'; g.fillRect(x - 3, 40, 6, 480); }
  (stickers || []).slice(0, 3).forEach((id, i) => {
    const s = STICKERS[id];
    if (!s) return;
    const w = 440, h = 64, x = 36, y = 70 + i * 86;
    g.fillStyle = s.bg; roundRect(g, x, y, w, h, 18); g.fill();
    g.fillStyle = s.fg; g.font = '800 30px system-ui'; g.textAlign = 'center'; g.textBaseline = 'middle';
    g.fillText(s.icon + ' ' + s.text, 256, y + h / 2 + 2, w - 24);
  });
  if (plate) {
    const p = plateCanvas(null, plate);
    g.drawImage(p, 128, 470, 256, 56);
  }
  return c;
}
function roundRect(g, x, y, w, h, r) { g.beginPath(); g.moveTo(x + r, y); g.arcTo(x + w, y, x + w, y + h, r); g.arcTo(x + w, y + h, x, y + h, r); g.arcTo(x, y + h, x, y, r); g.arcTo(x, y, x + w, y, r); g.closePath(); }

/** Розпис на кабіну (прозоре тло): петриківка, вишиванка, чумацький віз, трипілля. */
function artCanvas(id, w, h) {
  const c = document.createElement('canvas');
  c.width = w; c.height = h;
  drawArt(c.getContext('2d'), id, 0, 0, w, h);
  return c;
}
function drawArt(g, id, x0, y0, w, h) {
  g.save(); g.translate(x0, y0);
  const lineW = Math.max(2, h / 40);
  if (id === 'artvyshyvanka') {
    // хрестики ромбами: червоне з чорним, у два ряди
    const s = h / 10;
    for (let row = 0; row < 2; row++) for (let i = 0; i * s * 6 < w + s * 6; i++) {
      const cx = i * s * 6 + (row ? s * 3 : 0), cy = h * (row ? 0.68 : 0.32);
      for (let dy = -2; dy <= 2; dy++) for (let dx = -2; dx <= 2; dx++) {
        if (Math.abs(dx) + Math.abs(dy) > 2) continue;
        g.strokeStyle = Math.abs(dx) + Math.abs(dy) === 2 ? '#c4161c' : (dx + dy) % 2 ? '#1a1a1a' : '#c4161c';
        g.lineWidth = lineW;
        const px = cx + dx * s, py = cy + dy * s, r = s * 0.38;
        g.beginPath(); g.moveTo(px - r, py - r); g.lineTo(px + r, py + r); g.moveTo(px + r, py - r); g.lineTo(px - r, py + r); g.stroke();
      }
    }
    g.fillStyle = '#c4161c'; g.fillRect(0, h * 0.04, w, lineW); g.fillRect(0, h * 0.94, w, lineW);
  } else if (id === 'artpetrykivka') {
    // гілка з квітами-«купавками», листям і завитками
    g.strokeStyle = '#2f7d3a'; g.lineWidth = lineW * 1.4;
    g.beginPath(); g.moveTo(0, h * 0.62);
    for (let x = 0; x <= w; x += w / 8) g.quadraticCurveTo(x + w / 16, h * (x / (w / 8) % 2 ? 0.45 : 0.78), x + w / 8, h * 0.62);
    g.stroke();
    for (let i = 0; i < 5; i++) {
      const cx = w * (0.1 + i * 0.2), cy = h * (i % 2 ? 0.36 : 0.5), r = h * 0.17;
      for (let k = 0; k < 7; k++) {
        const a = -Math.PI + (k / 6) * Math.PI;
        g.fillStyle = k % 2 ? '#f0a030' : '#d9381e';
        g.beginPath(); g.ellipse(cx + Math.cos(a) * r * 0.7, cy + Math.sin(a) * r * 0.7, r * 0.55, r * 0.22, a, 0, Math.PI * 2); g.fill();
      }
      g.fillStyle = '#f5d000'; g.beginPath(); g.arc(cx, cy, r * 0.3, 0, Math.PI * 2); g.fill();
      for (const s of [-1, 1]) {
        g.fillStyle = '#3f9a45';
        g.beginPath(); g.ellipse(cx + s * r * 1.1, cy + r * 0.9, r * 0.5, r * 0.18, s * 0.6, 0, Math.PI * 2); g.fill();
        g.strokeStyle = '#2f7d3a'; g.lineWidth = lineW;
        g.beginPath(); g.arc(cx + s * r * 1.6, cy + r * 0.2, r * 0.3, 0, Math.PI * 1.5); g.stroke();
      }
    }
  } else if (id === 'arttrypillia') {
    // спіралі трипільської кераміки: охра, чорне, біле
    const n = Math.max(2, Math.round(w / (h * 0.9)));
    for (let i = 0; i < n; i++) {
      const cx = (i + 0.5) * (w / n), cy = h / 2, R = h * 0.38;
      for (const [col, wd] of [['#f3efe8', lineW * 3.2], ['#2a1d14', lineW * 1.6]]) {
        g.strokeStyle = col; g.lineWidth = wd; g.beginPath();
        for (let t = 0; t < Math.PI * 5; t += 0.1) {
          const r = R * (1 - t / (Math.PI * 5.4)), d = i % 2 ? 1 : -1;
          const px = cx + Math.cos(t * d) * r, py = cy + Math.sin(t * d) * r;
          if (t === 0) g.moveTo(px, py); else g.lineTo(px, py);
        }
        g.stroke();
      }
    }
    g.fillStyle = '#9c4a1c'; g.fillRect(0, 0, w, lineW * 2); g.fillRect(0, h - lineW * 2, w, lineW * 2);
  } else if (id === 'artviz') {
    // чумацький віз із волом і зорями Чумацького Шляху
    const brown = '#4a2f1c';
    g.fillStyle = '#f2e6b8';
    for (let i = 0; i < 14; i++) { g.beginPath(); g.arc((i * 97) % w, h * (0.08 + ((i * 37) % 30) / 100), lineW * (i % 3 ? 0.8 : 1.4), 0, Math.PI * 2); g.fill(); }
    const s = h / 100, bx = w * 0.42;
    g.fillStyle = brown;
    g.fillRect(bx, 48 * s, 60 * s, 16 * s);                                   // віз
    for (let k = 0; k < 6; k++) g.fillRect(bx + k * 11 * s, 34 * s, 3 * s, 15 * s);
    g.fillRect(bx - 34 * s, 56 * s, 36 * s, 3 * s);                             // дишло
    g.strokeStyle = brown; g.lineWidth = 3 * s;
    for (const wx of [bx + 10 * s, bx + 50 * s]) {
      g.beginPath(); g.arc(wx, 70 * s, 12 * s, 0, Math.PI * 2); g.stroke();
      for (let a = 0; a < 6; a++) { g.beginPath(); g.moveTo(wx, 70 * s); g.lineTo(wx + Math.cos(a) * 12 * s, 70 * s + Math.sin(a) * 12 * s); g.stroke(); }
    }
    const ox = bx - 80 * s;                                                    // віл
    g.beginPath(); g.ellipse(ox + 22 * s, 56 * s, 24 * s, 12 * s, 0, 0, Math.PI * 2); g.fill();
    g.beginPath(); g.ellipse(ox - 4 * s, 50 * s, 9 * s, 7 * s, -0.4, 0, Math.PI * 2); g.fill();
    for (const k of [6, 14, 30, 38]) g.fillRect(ox + k * s, 62 * s, 4 * s, 18 * s);
    g.lineWidth = 2.5 * s; g.beginPath(); g.moveTo(ox - 8 * s, 44 * s); g.quadraticCurveTo(ox - 14 * s, 34 * s, ox - 4 * s, 34 * s); g.stroke();
    g.fillStyle = brown; g.font = '800 ' + Math.round(16 * s) + 'px system-ui'; g.textAlign = 'center';
    g.fillText('ЧУМАКИ', w * 0.82, 66 * s);
  }
  g.restore();
}


// колір смуги на борту за вантажем
const STRIPE = { jugs: '#b5482f', tiles: '#2f6fa8', roof: '#a5352a', honey: '#d39a1c', hay: '#8a9a2a', salt: '#8b96a3', clay: '#7a5234', goods: '#2f8a7a' };

/** Борт причепа. potRight — глечик праворуч, напис ліворуч від нього (для борту, де кабіна праворуч). */
function sideTexture(THREE, cargo, canvas, potRight, art) {
  const c = canvas || document.createElement('canvas');
  c.width = 1024; c.height = 208;
  const g = c.getContext('2d');
  g.fillStyle = '#efe6d2'; g.fillRect(0, 0, 1024, 208);
  if (art) drawArt(g, art, 0, 2, 1024, 22);         // розпис — смугою вгорі борту
  g.fillStyle = (cargo && STRIPE[cargo.id]) || '#b5482f'; g.fillRect(0, 168, 1024, 18);
  g.fillStyle = '#d08a2c'; g.fillRect(0, 186, 1024, 8);
  // глечик (вушко — у бік напису)
  const px = potRight ? 1024 - 130 : 130, dir = potRight ? -1 : 1;
  g.fillStyle = '#c26a3a';
  g.beginPath(); g.ellipse(px, 100, 52, 58, 0, 0, Math.PI * 2); g.fill();
  g.fillRect(px - 18, 28, 36, 30);
  g.beginPath(); g.ellipse(px, 28, 26, 8, 0, 0, Math.PI * 2); g.fill();
  g.strokeStyle = '#c26a3a'; g.lineWidth = 9;
  g.beginPath(); g.arc(px + dir * 48, 78, 24, potRight ? Math.PI - 1.4 : -1.2, potRight ? Math.PI + 1.2 : 1.4); g.stroke();
  g.fillStyle = '#efe6d2'; g.fillRect(px - 46, 98, 92, 6);
  g.textAlign = potRight ? 'right' : 'left';
  g.fillStyle = '#2f3a4a'; g.font = '900 88px system-ui'; g.textBaseline = 'middle';
  g.fillText('ГЛЕЧИКИ', potRight ? 1024 - 230 : 230, 92);
  g.fillStyle = '#6b5c46'; g.font = '600 30px system-ui';
  g.fillText(cargo ? 'вантаж: ' + cargo.title.toLowerCase() + ' · чумацька служба' : 'перевезення крихкого · чумацька служба', potRight ? 1024 - 236 : 236, 146);
  if (canvas) return null;
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = THREE.SRGBColorSpace;
  t.anisotropy = 4;
  return t;
}
