// Салон «Фури 2.0» (хвиля E): усе, що видно з камери «з кабіни», — злите за матеріалами (≤ 8 draw calls, ≤ 8k △, STYLE §9).
// Тут і будівник злиття (Kit), яким model.js зливає зовнішню фуру: деталь кладеться в «кошик» матеріалу з кольором вершин,
// кошик — один меш. Світло салону своє (litCab): пряме сонце приглушене (дах і стіни), розсіяне — з неба тієї ж миті, AO
// запечене у вершини, уночі — тепле світло від приладів. Тіней салон не кидає й не приймає.
// Осі — тягача (truck.js): +Z вперед, праворуч −X; водій ліворуч (+X), око — EYE.

export const EYE = [0.6, 1.66, 1.95];

// ---------------------------------------------------------------- будівник злиття
/** Кошики деталей за ключем матеріалу; put(...) — деталь у кошик (геометрія переходить у власність кошика). */
export function makeKit(THREE) {
  const lists = new Map(), o3 = new THREE.Object3D(), col = new THREE.Color();
  function put(key, geo, x = 0, y = 0, z = 0, o = {}) {
    o3.position.set(x, y, z);
    o3.rotation.set(o.rx || 0, o.ry || 0, o.rz || 0, o.order || 'XYZ');
    o3.scale.set(o.sx || 1, o.sy || 1, o.sz || 1);
    if (o.look) o3.lookAt(o.look[0], o.look[1], o.look[2]);
    o3.updateMatrix();
    geo.applyMatrix4(o3.matrix);
    const n = geo.attributes.position.count, k = o.k == null ? 1 : o.k;
    col.set(o.c || '#ffffff');
    const ca = new Float32Array(n * 3);
    for (let i = 0; i < n; i++) { ca[i * 3] = col.r * k; ca[i * 3 + 1] = col.g * k; ca[i * 3 + 2] = col.b * k; }
    geo.setAttribute('color', new THREE.BufferAttribute(ca, 3));
    if (!geo.attributes.uv) geo.setAttribute('uv', new THREE.BufferAttribute(new Float32Array(n * 2), 2));
    if (o.uv) {                                   // [u0, v0, u1, v1] — ділянка атласу
      const uv = geo.attributes.uv;
      for (let i = 0; i < uv.count; i++) uv.setXY(i, o.uv[0] + uv.getX(i) * (o.uv[2] - o.uv[0]), o.uv[1] + uv.getY(i) * (o.uv[3] - o.uv[1]));
    }
    if (!lists.has(key)) lists.set(key, []);
    lists.get(key).push(geo);
    return geo;
  }
  /** Відрізок a→b брусом товщини t (кронштейни, ремені). */
  function bar(key, a, b, t, o = {}) {
    const dx = b[0] - a[0], dy = b[1] - a[1], dz = b[2] - a[2], L = Math.hypot(dx, dy, dz);
    return put(key, new THREE.BoxGeometry(o.w || t, t, L), (a[0] + b[0]) / 2, (a[1] + b[1]) / 2, (a[2] + b[2]) / 2, { ...o, look: b });
  }
  function merged(key) {
    const list = lists.get(key);
    if (!list || !list.length) return null;
    let nv = 0, ni = 0;
    for (const g of list) { nv += g.attributes.position.count; ni += g.index ? g.index.count : g.attributes.position.count; }
    const pos = new Float32Array(nv * 3), nor = new Float32Array(nv * 3), uv = new Float32Array(nv * 2), cl = new Float32Array(nv * 3);
    const idx = nv > 65535 ? new Uint32Array(ni) : new Uint16Array(ni);
    let v = 0, i = 0;
    for (const g of list) {
      const a = g.attributes, n = a.position.count;
      pos.set(a.position.array, v * 3); nor.set(a.normal.array, v * 3); uv.set(a.uv.array, v * 2); cl.set(a.color.array, v * 3);
      if (g.index) for (let k = 0; k < g.index.count; k++) idx[i++] = g.index.array[k] + v;
      else for (let k = 0; k < n; k++) idx[i++] = k + v;
      v += n;
      g.dispose();
    }
    const out = new THREE.BufferGeometry();
    out.setAttribute('position', new THREE.BufferAttribute(pos, 3));
    out.setAttribute('normal', new THREE.BufferAttribute(nor, 3));
    out.setAttribute('uv', new THREE.BufferAttribute(uv, 2));
    out.setAttribute('color', new THREE.BufferAttribute(cl, 3));
    out.setIndex(new THREE.BufferAttribute(idx, 1));
    out.computeBoundingSphere();
    lists.delete(key);
    return out;
  }
  /** Кошик → меш у parent (null, якщо кошик порожній). ao(x, y, z, nx, ny, nz) — множник кольору вершини. */
  function mesh(key, mat, parent, ao) {
    const g = merged(key);
    if (!g) return null;
    if (ao) {
      const p = g.attributes.position, nr = g.attributes.normal, c = g.attributes.color;
      for (let k = 0; k < p.count; k++) {
        const f = ao(p.getX(k), p.getY(k), p.getZ(k), nr.getX(k), nr.getY(k), nr.getZ(k));
        c.setXYZ(k, c.getX(k) * f, c.getY(k) * f, c.getZ(k) * f);
      }
    }
    const m = new THREE.Mesh(g, mat);
    if (parent) parent.add(m);
    return m;
  }
  return { put, bar, mesh, merged };
}

/** Брус із заокругленими (скошеними) ребрами, гладкі нормалі: 108 △. */
export function rbox(THREE, w, h, d, r = 0.04) {
  r = Math.max(0.002, Math.min(r, w / 2 - 0.001, h / 2 - 0.001, d / 2 - 0.001));
  const g = new THREE.BoxGeometry(1, 1, 1, 3, 3, 3), p = g.attributes.position, nr = g.attributes.normal, H = [w / 2, h / 2, d / 2];
  const a = [0, 0, 0], inn = [0, 0, 0];
  for (let i = 0; i < p.count; i++) {
    a[0] = p.getX(i); a[1] = p.getY(i); a[2] = p.getZ(i);
    let L = 0;
    for (let k = 0; k < 3; k++) {
      a[k] = Math.sign(a[k]) * (Math.abs(a[k]) > 0.4 ? H[k] : H[k] - r);   // рівні сітки ±1/2 → край, ±1/6 → початок заокруглення
      inn[k] = Math.max(-(H[k] - r), Math.min(H[k] - r, a[k]));
      a[k] -= inn[k]; L += a[k] * a[k];
    }
    L = Math.sqrt(L) || 1;
    p.setXYZ(i, inn[0] + a[0] / L * r, inn[1] + a[1] / L * r, inn[2] + a[2] / L * r);
    nr.setXYZ(i, a[0] / L, a[1] / L, a[2] / L);
  }
  return g;
}

/** Латка шейдера ланцюжком (небо sky.adopt кладе свою поверх і бере наш ключ програми). */
export function chainPatch(mat, key, fn) {
  const prev = mat.onBeforeCompile, k0 = mat.customProgramCacheKey && prev ? mat.customProgramCacheKey() : '';
  mat.onBeforeCompile = (sh, r) => { if (prev) prev(sh, r); fn(sh); };
  mat.customProgramCacheKey = () => k0 + '-' + key;
  return mat;
}

// ---------------------------------------------------------------- світло салону
function litCab(THREE, mat, U) {
  mat.userData.noAtmo = true;                   // туман і тінь хмар у кабіні не потрібні
  return chainPatch(mat, 'cab', (sh) => {
    Object.assign(sh.uniforms, U);
    sh.vertexShader = 'varying vec3 vCabP;\n' + sh.vertexShader.replace('#include <begin_vertex>', '#include <begin_vertex>\nvCabP = position;');
    sh.fragmentShader = 'uniform float uCabSun; uniform vec3 uCabAmb, uCabGlow; varying vec3 vCabP;\n' + sh.fragmentShader.replace('#include <lights_fragment_end>',
      `#include <lights_fragment_end>
      reflectedLight.directDiffuse *= uCabSun; reflectedLight.directSpecular *= uCabSun;
      reflectedLight.indirectDiffuse += diffuseColor.rgb * (uCabAmb + uCabGlow * (0.25 + smoothstep(2.0, 3.0, vCabP.z)));`);
  });
}

// ---------------------------------------------------------------- косметика салону (G3: слоти fura-seat, fura-wheel, fura-curtain)
const norm = (v) => String(v || '').toLowerCase().replace(/[^a-z]/g, '');
/** Ключ речі зі слота: приймає і «seat», і «fura-seat», і id з приставкою (seatleatherbrown). */
export function slotOf(look, k) { return norm(look && (look[k] != null ? look[k] : look['fura-' + k])); }
const SEATS = {
  cloth: { c: '#4f586c', r: 0.95 }, leatherbrown: { c: '#7a4628', r: 0.42 }, leatherblack: { c: '#232326', r: 0.4 },
  vyshyvanka: { c: '#ebe3d0', r: 0.9 }, sheepskin: { c: '#e7ddcc', r: 1 },
};
const WHEELS = { plain: { c: '#1d1d20', r: 0.6 }, wood: { c: '#8a5530', r: 0.3 }, leather: { c: '#2c2522', r: 0.5 }, braided: { c: '#262626', r: 0.7 } };
export const CURTAINS = { red: '#8b3b2e', blue: '#2f4f8b', green: '#3c6b3a', yellow: '#c9a227', none: null };
function pick(table, v, dflt) {
  if (table[v] !== undefined) return v;
  for (const k of Object.keys(table)) if (v && (v.endsWith(k) || v.includes(k))) return k;
  if (v && v.includes('leather')) return v.includes('black') ? 'leatherblack' : 'leatherbrown';
  return dflt;
}

function seatCanvas(id) {
  const c = document.createElement('canvas'); c.width = c.height = 128;
  const g = c.getContext('2d'), s = SEATS[id];
  g.fillStyle = '#ffffff'; g.fillRect(0, 0, 128, 128);         // колір дає матеріал; тут — візерунок (множиться)
  let seed = 7;
  const rnd = () => ((seed = (seed * 16807) % 2147483647) / 2147483647);
  if (id === 'cloth' || id === 'sheepskin') {
    for (let i = 0; i < (id === 'cloth' ? 900 : 260); i++) {
      const x = rnd() * 128, y = rnd() * 128, v = 200 + rnd() * 55 | 0;
      g.fillStyle = `rgb(${v},${v},${v})`;
      if (id === 'cloth') g.fillRect(x, y, 2, 1);
      else { g.beginPath(); g.arc(x, y, 3 + rnd() * 4, 0, Math.PI * 2); g.fill(); }
    }
    if (id === 'cloth') { g.fillStyle = '#c9ced8'; g.fillRect(40, 0, 3, 128); g.fillRect(85, 0, 3, 128); }
  } else if (id === 'vyshyvanka') {
    for (let row = 0; row < 2; row++) for (let i = 0; i < 6; i++) {
      const cx = 10 + i * 22 + row * 11, cy = 36 + row * 56;
      for (let dy = -2; dy <= 2; dy++) for (let dx = -2; dx <= 2; dx++) {
        if (Math.abs(dx) + Math.abs(dy) > 2) continue;
        g.fillStyle = (dx + dy) % 2 ? '#2a1a1a' : '#c4161c';
        g.fillRect(cx + dx * 4 - 1.5, cy + dy * 4 - 1.5, 3, 3);
      }
    }
    g.fillStyle = '#c4161c'; g.fillRect(0, 8, 128, 3); g.fillRect(0, 117, 128, 3);
  } else {                                                       // шкіра: стьобка ромбами
    g.strokeStyle = id === 'leatherblack' ? '#8a8a8a' : '#e9c9a8'; g.lineWidth = 1.2; g.setLineDash([3, 3]);
    for (let k = -128; k < 256; k += 32) { g.beginPath(); g.moveTo(k, 0); g.lineTo(k + 128, 128); g.stroke(); g.beginPath(); g.moveTo(k + 128, 0); g.lineTo(k, 128); g.stroke(); }
    g.setLineDash([]);
    const gr = g.createLinearGradient(0, 0, 128, 128); gr.addColorStop(0, 'rgba(255,255,255,0.12)'); gr.addColorStop(1, 'rgba(0,0,0,0.12)');
    g.fillStyle = gr; g.fillRect(0, 0, 128, 128);
  }
  return c;
}
function wheelCanvas(id) {
  const c = document.createElement('canvas'); c.width = 64; c.height = 256;
  const g = c.getContext('2d');
  g.fillStyle = '#fff'; g.fillRect(0, 0, 64, 256);
  if (id === 'wood') for (let y = 0; y < 256; y += 3) { const v = 190 + (Math.sin(y * 0.21) * 30 + Math.sin(y * 0.047) * 25) | 0; g.fillStyle = `rgb(${v},${v},${v})`; g.fillRect(0, y, 64, 2); }
  else if (id === 'braided') for (let y = -64; y < 256; y += 10) { g.fillStyle = y % 20 ? '#bdbdbd' : '#d23a24'; g.beginPath(); g.moveTo(0, y); g.lineTo(64, y + 32); g.lineTo(64, y + 37); g.lineTo(0, y + 5); g.fill(); }
  else if (id === 'leather') { g.strokeStyle = '#9a8f88'; g.setLineDash([4, 3]); g.lineWidth = 2; g.beginPath(); g.moveTo(32, 0); g.lineTo(32, 256); g.stroke(); }
  return c;
}

// ---------------------------------------------------------------- прилади (canvas 512×256, ≤ 10 Гц)
// ділянки атласу [x, y, w, h] у пікселях полотна
const R_CLUSTER = [0, 0, 512, 160], R_SCREEN = [0, 160, 256, 96], R_RADIO = [256, 160, 256, 64], R_LED = [392, 228, 112, 24];
const uvOf = ([x, y, w, h]) => [x / 512, 1 - (y + h) / 256, (x + w) / 512, 1 - y / 256];

function dialBg(g, cx, cy, r, max, step, label, red, eco) {
  const a0 = Math.PI * 0.75, sweep = Math.PI * 1.5, ang = (v) => a0 + (v / max) * sweep;
  const gr = g.createRadialGradient(cx, cy - r * 0.3, r * 0.1, cx, cy, r);
  gr.addColorStop(0, '#1f252d'); gr.addColorStop(1, '#090b0e');
  g.fillStyle = gr; g.beginPath(); g.arc(cx, cy, r, 0, Math.PI * 2); g.fill();
  g.strokeStyle = '#5b6470'; g.lineWidth = 2; g.stroke();
  if (eco) { g.strokeStyle = '#2f9a56'; g.lineWidth = 5; g.beginPath(); g.arc(cx, cy, r - 9, ang(eco[0]), ang(eco[1])); g.stroke(); }
  if (red) { g.strokeStyle = '#d23a24'; g.lineWidth = 5; g.beginPath(); g.arc(cx, cy, r - 9, ang(red), ang(max)); g.stroke(); }
  g.fillStyle = '#e8eef5'; g.textAlign = 'center'; g.textBaseline = 'middle';
  for (let v = 0; v <= max + 1e-6; v += step / 2) {
    const a = ang(v), major = Math.abs(v / step - Math.round(v / step)) < 1e-6;
    g.strokeStyle = '#e8eef5'; g.lineWidth = major ? 2.5 : 1.2;
    g.beginPath(); g.moveTo(cx + Math.cos(a) * (r - (major ? 16 : 11)), cy + Math.sin(a) * (r - (major ? 16 : 11))); g.lineTo(cx + Math.cos(a) * (r - 4), cy + Math.sin(a) * (r - 4)); g.stroke();
    if (major) { g.font = '700 13px system-ui'; g.fillText(label(v), cx + Math.cos(a) * (r - 28), cy + Math.sin(a) * (r - 28)); }
  }
}
function needle(g, cx, cy, r, v, max) {
  const a = Math.PI * 0.75 + Math.max(0, Math.min(1.02, v / max)) * Math.PI * 1.5;
  g.strokeStyle = '#ff5a26'; g.lineWidth = 4; g.lineCap = 'round';
  g.beginPath(); g.moveTo(cx - Math.cos(a) * 12, cy - Math.sin(a) * 12); g.lineTo(cx + Math.cos(a) * (r - 12), cy + Math.sin(a) * (r - 12)); g.stroke();
  g.fillStyle = '#2b2f35'; g.beginPath(); g.arc(cx, cy, 9, 0, Math.PI * 2); g.fill();
}

// ---------------------------------------------------------------- салон
export function buildCab(THREE, parent) {
  const group = new THREE.Group();
  group.visible = false;
  parent.add(group);
  const U = { uCabSun: { value: 0.35 }, uCabAmb: { value: new THREE.Color(0.18, 0.18, 0.19) }, uCabGlow: { value: new THREE.Color(0, 0, 0) } };
  const mat = (o) => litCab(THREE, new THREE.MeshStandardMaterial({ roughness: 0.85, metalness: 0, ...o }), U);
  const trimMat = mat({ vertexColors: true });
  const kit = makeKit(THREE), P = kit.put, B = (w, h, d) => new THREE.BoxGeometry(w, h, d), RB = (w, h, d, r) => rbox(THREE, w, h, d, r);
  const eye = new THREE.Vector3(...EYE), E = EYE;

  // кольори
  const DASH = '#2d2f33', DASH2 = '#46484c', DOOR = '#5c5d60', INSERT = '#b3a68f', PILLAR = '#a29c90', WALL = '#8a8274',
    ROOF = '#d3cab8', FLOOR = '#26272a', CHROME = '#c8ccd2', DARK = '#1f2023';

  // ---- підлога, тунель, стіни, стеля
  P('trim', B(2.34, 0.05, 2.06), 0, 0.125, 2.1, { c: FLOOR });
  P('trim', RB(0.62, 0.18, 0.9, 0.05), 0, 0.22, 2.62, { c: '#2c2d31' });
  for (const [x, z] of [[0.68, 2.78], [0.5, 2.8]]) P('trim', RB(0.09, 0.03, 0.2, 0.01), x, 0.3, z, { c: '#3a3b3f', rx: -0.5 });   // педалі
  P('trim', B(2.34, 2.07, 0.05), 0, 1.18, 1.075, { c: WALL });
  P('trim', B(2.36, 0.04, 2.08), 0, 2.215, 2.1, { c: ROOF });
  P('trim', RB(0.64, 0.03, 0.64, 0.01), 0, 2.19, 1.95, { c: '#bfb5a2' });                     // люк
  for (const s of [1, -1]) {
    const x = s * 1.17;
    P('trim', B(0.05, 0.97, 1.96), x, 0.62, 2.1, { c: DOOR });                               // двері до вікна
    P('trim', B(0.05, 0.92, 0.95), x, 1.56, 1.575, { c: WALL });                              // за вікном
    P('trim', B(0.05, 0.92, 0.09), x, 1.56, 3.085, { c: PILLAR });
    P('trim', B(0.05, 0.2, 2.06), x, 2.11, 2.1, { c: ROOF });
    P('trim', B(0.02, 0.42, 1.05), s * 1.142, 0.74, 2.5, { c: INSERT });                     // вставка дверей
    P('trim', RB(0.11, 0.07, 0.56, 0.025), s * 1.1, 0.98, 2.45, { c: '#3b3c3f' });            // підлокітник
    P('trim', B(0.05, 0.015, 0.14), s * 1.08, 1.018, 2.3, { c: '#16171a' });                   // кнопки склопідйомника
    P('trim', RB(0.03, 0.035, 0.15, 0.01), s * 1.13, 1.06, 2.86, { c: CHROME });              // ручка
    P('trim', new THREE.CylinderGeometry(0.075, 0.075, 0.02, 12), s * 1.143, 0.38, 2.75, { c: '#202124', rz: Math.PI / 2 });   // динамік
    P('trim', RB(0.1, 0.035, 1.02, 0.01), s * 1.15, 1.1, 2.56, { c: DARK });                  // підвіконня
    P('trim', RB(0.1, 0.98, 0.1, 0.03), s * 1.12, 1.56, 3.07, { c: PILLAR, rz: -s * 0.05 }); // стійка лобового
    P('trim', RB(0.03, 0.03, 0.5, 0.012), s * 1.12, 2.0, 2.55, { c: DARK });                 // поручень над дверима
    kit.bar('trim', [s * 1.12, 1.66, 1.62], [s * (1.12 - 0.5), 0.92, 1.92], 0.012, { c: '#2a2b30', w: 0.05 });   // ремінь
  }
  // ---- спальник і полиця ззаду
  P('trim', B(2.3, 0.45, 0.7), 0, 0.37, 1.43, { c: '#5e5850' });
  P('trim', RB(2.24, 0.16, 0.66, 0.05), 0, 0.66, 1.43, { c: '#4c5366' });
  P('trim', RB(2.3, 0.26, 0.42, 0.04), 0, 2.04, 1.32, { c: '#8a8275' });
  for (const x of [-0.6, 0.0, 0.6]) P('trim', B(0.5, 0.18, 0.01), x, 2.03, 1.535, { c: '#77705f' });
  // ---- панель приладів
  P('trim', RB(2.34, 0.14, 0.44, 0.05), 0, 1.05, 2.9, { c: DASH });
  P('trim', RB(2.3, 0.6, 0.32, 0.06), 0, 0.72, 2.92, { c: DASH2 });
  P('trim', RB(0.72, 0.3, 0.12, 0.05), 0.6, 0.78, 2.72, { c: '#3e4044' });                   // коліна водія
  P('trim', RB(0.8, 0.3, 0.26, 0.06), 0.6, 1.2, 2.86, { c: DASH });                           // щиток приладів
  P('trim', RB(0.84, 0.05, 0.2, 0.02), 0.6, 1.372, 2.86, { c: '#232528', rx: 0.1 });         // козирок над приладами
  P('trim', RB(0.6, 0.44, 0.16, 0.05), 0.02, 1.0, 2.82, { c: '#3a3c40', ry: 0.35 });          // крило з екраном
  P('trim', B(0.62, 0.28, 0.012), -0.6, 0.86, 2.758, { c: '#3d3f43' });                       // бардачок
  P('trim', B(0.08, 0.015, 0.012), -0.6, 0.96, 2.75, { c: '#202124' });
  for (const [x, y, z, ry] of [[0.17, 1.12, 2.71, 0.3], [1.02, 1.12, 2.73, -0.2], [-0.25, 1.06, 2.76, 0], [-0.95, 1.06, 2.76, 0]]) {   // дефлектори
    P('trim', RB(0.17, 0.08, 0.03, 0.01), x, y, z, { c: '#1b1c1f', ry });
    for (let k = 0; k < 3; k++) P('trim', B(0.15, 0.006, 0.02), x, y - 0.025 + k * 0.025, z - 0.012, { c: '#55585e', ry });
  }
  for (let k = 0; k < 6; k++) P('trim', RB(0.035, 0.022, 0.02, 0.005), -0.12 + k * 0.045, 0.86, 2.79, { c: k === 2 ? '#c23b22' : '#2b2c30', ry: 0.35 });   // кнопки
  P('trim', RB(0.035, 0.06, 0.035, 0.01), 0.24, 0.97, 2.73, { c: '#e0b020' });              // ручник
  // ---- верхня консоль із рацією, козирки
  P('trim', RB(2.32, 0.24, 0.44, 0.05), 0, 2.08, 2.88, { c: '#8f897e' });
  for (const x of [-0.75, 0.75]) P('trim', B(0.6, 0.15, 0.012), x, 2.08, 2.657, { c: '#7f796e' });
  P('trim', RB(0.38, 0.11, 0.08, 0.015), -0.12, 1.995, 2.67, { c: '#18191b' });              // рація
  P('trim', RB(0.05, 0.09, 0.03, 0.012), 0.12, 1.86, 2.66, { c: '#18191b' });                // тангента
  const coil = new THREE.CatmullRomCurve3(Array.from({ length: 41 }, (_, i) => {
    const t = i / 40, a = t * Math.PI * 16;
    return new THREE.Vector3(-0.02 + t * 0.14 + Math.cos(a) * 0.012, 1.95 - t * 0.06 - Math.sin(t * Math.PI) * 0.05, 2.68 + Math.sin(a) * 0.012);
  }));
  P('trim', new THREE.TubeGeometry(coil, 60, 0.004, 3), 0, 0, 0, { c: '#18191b' });
  for (const s of [1, -1]) P('trim', RB(0.8, 0.026, 0.26, 0.012), s * 0.55, 1.952, 3.0, { c: '#b9af9c', rx: 0.12 });   // козирки
  // ---- сидіння (каркас; оббивка — свій матеріал)
  const seatKit = makeKit(THREE);
  for (const x of [0.6, -0.62]) {
    P('trim', B(0.42, 0.5, 0.42), x, 0.43, 1.98, { c: '#232427' });
    P('trim', RB(0.46, 0.06, 0.46, 0.02), x, 0.7, 1.98, { c: '#2c2d31' });
    seatKit.put('seat', RB(0.54, 0.12, 0.52, 0.05), x, 0.8, 1.98);
    for (const s of [1, -1]) seatKit.put('seat', RB(0.08, 0.1, 0.48, 0.035), x + s * 0.24, 0.87, 1.98, { k: 0.9 });
    seatKit.put('seat', RB(0.52, 0.72, 0.13, 0.06), x, 1.24, 1.66, { rx: -0.15 });
    for (const s of [1, -1]) seatKit.put('seat', RB(0.08, 0.62, 0.17, 0.035), x + s * 0.25, 1.22, 1.69, { rx: -0.15, k: 0.9 });
    seatKit.put('seat', RB(0.3, 0.2, 0.11, 0.05), x, 1.74, 1.58, { rx: -0.1 });
    P('trim', RB(0.07, 0.06, 0.32, 0.02), x - 0.3, 1.04, 1.92, { c: '#2c2d31' });           // підлокітник
  }
  // ---- шторки: зібрані біля стійок, ламбрекен із китицями над лобовим, біля бокових вікон і спальника
  const curtKit = makeKit(THREE);
  const drape = (w, h, nx, amp, waves) => {
    const g = new THREE.PlaneGeometry(w, h, nx, 1), p = g.attributes.position, n = p.count, sh = new Float32Array(n * 3);
    for (let i = 0; i < n; i++) {
      const ph = (p.getX(i) / w + 0.5) * waves * Math.PI * 2;
      p.setZ(i, Math.sin(ph) * amp);
      const f = 0.72 + 0.28 * Math.cos(ph);                     // складки темніють у западинах
      sh[i * 3] = sh[i * 3 + 1] = sh[i * 3 + 2] = f;
    }
    g.computeVertexNormals();
    g.userData.shade = sh;
    return g;
  };
  const curt = (g, x, y, z, o) => { curtKit.put('c', g, x, y, z, o); const c = g.attributes.color, sh = g.userData.shade; for (let i = 0; i < c.count; i++) c.setXYZ(i, sh[i * 3], sh[i * 3], sh[i * 3]); };
  for (const s of [1, -1]) {
    curt(drape(0.17, 0.82, 8, 0.022, 3.5), s * 1.03, 1.52, 3.05, { ry: Math.PI });
    P('trim', RB(0.19, 0.035, 0.07, 0.012), s * 1.03, 1.42, 3.03, { c: '#c9a54a' });          // підхват
    curt(drape(0.14, 0.82, 6, 0.018, 3), s * 1.135, 1.54, 2.12, { ry: -s * Math.PI / 2 });
    curt(drape(0.34, 1.25, 10, 0.03, 4), s * 0.98, 1.55, 1.12, {});
  }
  const val = drape(2.16, 0.13, 36, 0.012, 14), vp = val.attributes.position;
  for (let i = 0; i < vp.count; i++) if (vp.getY(i) < 0) vp.setY(i, vp.getY(i) - 0.035 * Math.abs(Math.sin((vp.getX(i) / 2.16 + 0.5) * Math.PI * 9)));
  curt(val, 0, 1.885, 3.06, { ry: Math.PI });
  for (let k = 0; k < 9; k++) P('trim', new THREE.OctahedronGeometry(0.017), -0.96 + k * 0.24, 1.8, 3.05, { c: '#ecdca8' });   // китиці

  // ---- прилади, екран, рація, підсвітка — один атлас
  const gcv = document.createElement('canvas'); gcv.width = 512; gcv.height = 256;
  const gtex = new THREE.CanvasTexture(gcv); gtex.colorSpace = THREE.SRGBColorSpace; gtex.anisotropy = 4;
  const gaugeMat = mat({ map: gtex, emissiveMap: gtex, emissive: '#ffffff', emissiveIntensity: 0.55, roughness: 0.3 });
  const gk = makeKit(THREE);
  gk.put('g', new THREE.PlaneGeometry(0.66, 0.206), 0.6, 1.232, 2.728, { look: E, uv: uvOf(R_CLUSTER) });
  gk.put('g', new THREE.PlaneGeometry(0.36, 0.135), 0.03, 1.1, 2.735, { look: E, uv: uvOf(R_SCREEN) });
  gk.put('g', new THREE.PlaneGeometry(0.34, 0.085), -0.12, 1.995, 2.629, { look: E, uv: uvOf(R_RADIO) });
  gk.put('g', new THREE.PlaneGeometry(2.1, 0.012), 0, 1.957, 2.9, { rx: Math.PI / 2, uv: uvOf(R_LED) });   // стрічка під консоллю
  const bgCv = document.createElement('canvas'); bgCv.width = 512; bgCv.height = 160;
  {
    const g = bgCv.getContext('2d');
    g.fillStyle = '#0b0d10'; g.fillRect(0, 0, 512, 160);
    dialBg(g, 84, 82, 74, 2500, 500, (v) => String(v / 100), 2100, [1000, 1500]);
    dialBg(g, 428, 82, 74, 120, 20, (v) => String(v), null, null);
    g.fillStyle = '#9aa6b4'; g.font = '600 10px system-ui'; g.textAlign = 'center';
    g.fillText('×100 об/хв', 84, 128); g.fillText('км/год', 428, 128);
  }

  // ---- інтер'єр зібрано: злиття і AO
  const ao = (x, y, z, nx, ny) => {
    let f = 0.5 + 0.5 * Math.min(1, Math.max(0, (y - 0.12) / 1.15));             // низ темніший
    f *= 0.72 + 0.28 * Math.min(1, Math.max(0, (z - 1.08) / 0.55));               // глибина спальника
    if (z > 2.62 && y < 1.02) f *= 0.72;                                            // під панеллю
    f *= 0.86 + 0.14 * Math.min(1, Math.max(0, (1.17 - Math.abs(x)) / 0.25));     // біля стін
    return f * (0.9 + 0.1 * ny);
  };
  const trim = kit.mesh('trim', trimMat, group, ao);
  const seatMat = mat({ vertexColors: true, color: SEATS.cloth.c });
  const seat = seatKit.mesh('seat', seatMat, group, ao);
  const curtMat = mat({ vertexColors: true, color: CURTAINS.red, side: THREE.DoubleSide, roughness: 1 });
  const curtain = curtKit.mesh('c', curtMat, group, ao);
  const gauges = gk.mesh('g', gaugeMat, group);

  // ---- кермо: обід + спиці + маточина (обертається — children[0] у wheelPivot, main.js), колонка не крутиться
  const wheelPivot = new THREE.Group();
  wheelPivot.position.set(0.6, 1.17, 2.4);
  wheelPivot.rotation.x = -2.0;                                         // площина керма ~25° від горизонту, лицем до водія
  group.add(wheelPivot);
  const wk = makeKit(THREE);
  wk.put('w', new THREE.TorusGeometry(0.225, 0.026, 6, 20), 0, 0, 0, {});
  for (const a of [0, Math.PI, Math.PI / 2]) wk.put('w', RB(0.18, 0.045, 0.02, 0.008), Math.cos(a) * 0.13, Math.sin(a) * 0.13, -0.01, { rz: a, c: '#2a2a2d' });
  wk.put('w', RB(0.15, 0.12, 0.05, 0.03), 0, 0.02, -0.02, { c: '#26272a' });
  wk.put('w', new THREE.CylinderGeometry(0.025, 0.025, 0.01, 12), 0, 0.02, 0.006, { rx: Math.PI / 2, c: '#c9813f' });   // глечик-значок
  const wheelMat = mat({ vertexColors: true, color: WHEELS.plain.c, roughness: 0.6 });
  const wheelMesh = wk.mesh('w', wheelMat, null);
  wheelPivot.add(wheelMesh);
  const ck = makeKit(THREE);
  ck.put('c', new THREE.CylinderGeometry(0.035, 0.045, 0.36, 10), 0, 0, -0.2, { rx: Math.PI / 2, c: '#232427' });
  for (const s of [1, -1]) ck.put('c', RB(0.12, 0.018, 0.018, 0.006), s * 0.09, -0.01, -0.07, { c: '#1a1b1d', rz: s * 0.15 });
  wheelPivot.add(ck.mesh('c', trimMat, null));

  // ---- глечик на панелі і речі з Лавки (кіт, вервечка, ялинка)
  const clay = mat({ color: '#c26a3a', roughness: 0.7 });
  const jk = makeKit(THREE);
  jk.put('j', new THREE.SphereGeometry(0.045, 10, 8), 0, 0, 0, { sy: 1.15 });
  jk.put('j', new THREE.CylinderGeometry(0.02, 0.026, 0.04, 10), 0, 0.06, 0, {});
  jk.put('j', new THREE.TorusGeometry(0.022, 0.006, 4, 10, Math.PI * 1.2), 0.038, 0.035, 0, { rz: -1.2 });
  const jug = new THREE.Group();
  jug.add(jk.mesh('j', clay, null));
  jug.position.set(-0.3, 1.172, 2.98);
  group.add(jug);
  const dashBox = new THREE.Group();
  group.add(dashBox);
  const dashObj = {};
  function buildDash(id) {
    if (dashObj[id]) return dashObj[id];
    const g = new THREE.Group(), pivot = new THREE.Group(), dk = makeKit(THREE), pk = makeKit(THREE);
    g.add(pivot);
    const m = mat({ vertexColors: true });
    if (id === 'dashcat') {
      const fur = '#d08a4c';
      g.position.set(-0.3, 1.16, 2.98);
      dk.put('d', new THREE.SphereGeometry(0.045, 8, 6), 0, 0, 0, { sx: 0.9, sy: 0.8, sz: 1.2, c: fur });
      pivot.position.set(0, 0.04, 0.035);
      pk.put('d', new THREE.SphereGeometry(0.032, 8, 6), 0, 0.02, 0.012, { c: fur });
      for (const s of [1, -1]) {
        pk.put('d', new THREE.ConeGeometry(0.012, 0.024, 4), s * 0.018, 0.05, 0.01, { c: fur });
        pk.put('d', new THREE.SphereGeometry(0.005, 4, 3), s * 0.012, 0.025, -0.03, { c: '#3a2a20' });
      }
    } else {
      g.position.set(0.05, 1.955, 2.95);                   // на нитці під верхньою консоллю — маятник
      pk.put('d', new THREE.CylinderGeometry(0.002, 0.002, 0.2, 4), 0, -0.1, 0, { c: '#eae2cf' });
      if (id === 'dashtree') pk.put('d', new THREE.ConeGeometry(0.045, 0.12, 6), 0, -0.26, 0, { sz: 0.25, c: '#2f7d3a' });
      else ['#2f6fd0', '#f3efe8', '#2f6fd0', '#f2c94c', '#2f6fd0'].forEach((c, i) => pk.put('d', new THREE.SphereGeometry(i === 2 ? 0.022 : 0.011, 6, 4), 0, -0.2 - i * 0.022, 0, { c }));
    }
    dk.mesh('d', m, g); pk.mesh('d', m, pivot);
    g.visible = false;
    dashBox.add(g);
    return (dashObj[id] = { g, pivot });
  }

  for (const m of [trim, seat, curtain, gauges, wheelMesh]) if (m) { m.castShadow = false; m.receiveShadow = false; }
  group.traverse((o) => { if (o.isMesh) { o.castShadow = false; o.receiveShadow = false; } });

  // ---- стан і оновлення
  let night = false, last = -1e9, odo = 0, lastT = 0, key = '', dashNow = null;
  const ctx = gcv.getContext('2d');
  function draw(s) {
    const g = ctx;
    g.drawImage(bgCv, 0, 0);
    needle(g, 84, 82, 74, s.rpm, 2500);
    needle(g, 428, 82, 74, Math.abs(s.kmh), 120);
    // середній екран: передача, ціле %, круїз, фари
    g.fillStyle = '#121a22'; g.fillRect(172, 10, 168, 140);
    g.strokeStyle = '#2b3a48'; g.lineWidth = 2; g.strokeRect(172, 10, 168, 140);
    g.textAlign = 'center'; g.textBaseline = 'middle';
    g.fillStyle = '#e8f1fa'; g.font = '800 46px system-ui'; g.fillText(s.gear, 256, 52);
    g.fillStyle = s.cargo < 90 ? '#ff8a5c' : '#9fe3a8'; g.font = '700 17px system-ui'; g.fillText('🏺 ' + s.cargo.toFixed(1).replace('.', ',') + ' %', 256, 96);
    g.fillStyle = '#9fb3c6'; g.font = '600 13px system-ui'; g.fillText(s.cruise ? '⏩ ' + s.cruise + ' км/год' : Math.round(Math.abs(s.kmh)) + ' км/год', 256, 124);
    g.fillStyle = s.lights ? '#4fd36a' : '#2a3540'; g.beginPath(); g.arc(186, 140, 4, 0, Math.PI * 2); g.fill();
    g.fillStyle = s.cruise ? '#4fd36a' : '#2a3540'; g.beginPath(); g.arc(326, 140, 4, 0, Math.PI * 2); g.fill();
    // екран у крилі: годинник, пробіг, «Глечики»
    const [sx, sy, sw, sh] = R_SCREEN;
    const gr = g.createLinearGradient(0, sy, 0, sy + sh); gr.addColorStop(0, '#16263a'); gr.addColorStop(1, '#0c1420');
    g.fillStyle = gr; g.fillRect(sx, sy, sw, sh);
    g.fillStyle = '#e9f2ff'; g.font = '800 34px system-ui'; g.textAlign = 'left'; g.fillText(s.clock, sx + 14, sy + 34);
    g.fillStyle = '#9fb3c6'; g.font = '600 13px system-ui'; g.fillText('рейс ' + s.odo.toFixed(1).replace('.', ',') + ' км', sx + 14, sy + 70);
    g.textAlign = 'right'; g.fillStyle = '#d08a2c'; g.font = '700 14px system-ui'; g.fillText('🏺 ГЛЕЧИКИ', sx + sw - 10, sy + 70);
    // рація: Глек на 19-му каналі
    const [rx, ry, rw, rh] = R_RADIO;
    g.fillStyle = '#141414'; g.fillRect(rx, ry, rw, rh);
    g.fillStyle = '#2a1a08'; g.fillRect(rx + 10, ry + 10, 150, 44);
    g.fillStyle = '#ffb347'; g.font = '700 22px ui-monospace, monospace'; g.textAlign = 'left'; g.fillText('CH19', rx + 18, ry + 33);
    g.font = '700 13px system-ui'; g.fillText('ГЛЕК', rx + 92, ry + 26); g.fillText('27,185', rx + 92, ry + 44);
    for (let k = 0; k < 3; k++) { g.fillStyle = '#3a3a3a'; g.beginPath(); g.arc(rx + 186 + k * 22, ry + 32, 8, 0, Math.PI * 2); g.fill(); }
    g.fillStyle = '#e04030'; g.beginPath(); g.arc(rx + 172, ry + 14, 3, 0, Math.PI * 2); g.fill();
    // стрічка підсвітки: світить лише вночі
    g.fillStyle = night ? '#ffb45a' : '#3a3530'; g.fillRect(R_LED[0], R_LED[1], R_LED[2], R_LED[3]);
    gtex.needsUpdate = true;
  }
  draw({ rpm: 0, kmh: 0, gear: 'N', cargo: 100, cruise: 0, lights: false, clock: '--:--', odo: 0 });

  const api = {
    group, wheelPivot, jug, dashObj,
    /** Прилади з фури, ≤ 10 Гц (з камери «з кабіни»). */
    update(now, truck) {
      if (now - last < 100) return;
      const dt = Math.min(1, (now - (lastT || now)) / 1000); lastT = now;
      last = now;
      if (!truck) return;
      const kmh = truck.speed() * 3.6;
      odo += Math.abs(kmh) / 3600 * dt;
      const d = new Date(), gm = truck.gearMode === 'auto' ? 'D' : '';
      const s = { rpm: truck.rpm || 0, kmh, gear: truck.gear < 0 ? 'R' : truck.gear === 0 ? 'N' : gm + truck.gear, cargo: Math.floor((truck.cargo ?? 100) * 10) / 10,
        cruise: truck.cruiseV ? Math.round(truck.cruiseV * 3.6) : 0, lights: !!truck.lights, clock: String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0'), odo };
      const k = [Math.round(s.rpm / 20), Math.round(kmh), s.gear, s.cargo, s.cruise, s.lights, s.clock, odo.toFixed(1), night].join();
      if (k === key) return;
      key = k;
      draw(s);
    },
    /** Світло салону з неба (sky.js): розсіяне — з опромінення неба, пряме сонце — приглушене. */
    light(sky) {
      if (!sky || !sky.irr) return;
      const i = sky.irr;
      U.uCabAmb.value.setRGB(Math.min(1.5, i.r * 9), Math.min(1.5, i.g * 9), Math.min(1.5, i.b * 9));   // irr неба — у малих одиницях (сонце ~3)
      U.uCabSun.value = 0.45;
    },
    setNight(on) {
      night = on;
      gaugeMat.emissiveIntensity = on ? 1.25 : 0.55;
      U.uCabGlow.value.setRGB(on ? 0.05 : 0, on ? 0.032 : 0, on ? 0.016 : 0);
      key = '';
    },
    setSeat(v) { const id = pick(SEATS, v, 'cloth'), s = SEATS[id]; if (seatMat.userData.id === id) return; seatMat.userData.id = id;
      seatMat.color.set(s.c); seatMat.roughness = s.r; if (seatMat.map) seatMat.map.dispose();
      const t = new THREE.CanvasTexture(seatCanvas(id)); t.colorSpace = THREE.SRGBColorSpace; t.wrapS = t.wrapT = THREE.RepeatWrapping; seatMat.map = t; seatMat.needsUpdate = true; },
    setWheel(v) { const id = pick(WHEELS, v, 'plain'), s = WHEELS[id]; if (wheelMat.userData.id === id) return; wheelMat.userData.id = id;
      wheelMat.color.set(s.c); wheelMat.roughness = s.r; if (wheelMat.map) wheelMat.map.dispose();
      const t = new THREE.CanvasTexture(wheelCanvas(id)); t.colorSpace = THREE.SRGBColorSpace; t.wrapS = t.wrapT = THREE.RepeatWrapping; wheelMat.map = t; wheelMat.needsUpdate = true; },
    setCurtain(v) { const id = pick(CURTAINS, v, 'red'); curtain.visible = !!CURTAINS[id]; if (CURTAINS[id]) curtMat.color.set(CURTAINS[id]); },
    /** Річ на панелі з Лавки (fura-dash): без речі глечик стоїть, «Глечик на панелі» хитається, кіт кивує, решта — маятник. */
    setDashItem(dash) {
      if (dash === dashNow) return;
      dashNow = dash;
      for (const k of Object.keys(dashObj)) dashObj[k].g.visible = false;
      if (dash && dash !== 'dashjug') buildDash(dash).g.visible = true;
      jug.visible = dash !== 'dashcat';
      jug.rotation.set(0, 0, 0);
    },
    setDash(rx, rz, now) {
      if (dashNow === 'dashjug') jug.rotation.set(rx, 0, rz);
      const d = dashNow && dashObj[dashNow];
      if (!d) return;
      if (dashNow === 'dashcat') d.pivot.rotation.set(0.18 * Math.sin(now / 520) + rx * 3, 0, rz * 1.5);
      else d.pivot.rotation.set(rx * 5, 0, rz * 5);
    },
    eye,
  };
  api.setSeat('cloth'); api.setWheel('plain');
  return api;
}
