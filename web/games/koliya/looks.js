// «Колія» — вигляди світу (K5): палітра, світло, туман, небо, камера + перемикачі в панелі.
//  LOOKS[name] — опис вигляду для render.js (applyLook): pal — заміни палітри C; sky — [верх, обрій] тла;
//   fog — колір і near/far як частки відстані камери; hemi/sun — світло (dir — звідки світить сонце); shadow — 'sun'
//   (справжні тіні) або 'blob' (круглі плями під речами, без тіней сонця); edge/edgeG/ao — затемнення країв кубиків
//   (предмети/підлога) і низу стін; cam — кут (°), поле зору (°), множник відстані; vignette — темні кути (0..1);
//   leaves — листопад частинками.
//  loadPrefs()/savePrefs(p) — { look, dist, angle } у localStorage (kol-look, kol-cam). Якість — у розділі K12 «Пристрій».
//  mountLookUi(panelRoot, view) — секція «🎨 Вигляд» у панелі K4 (view.setLook / setCam — без рестарту).

export const LOOKS = {
  summer: {
    label: 'Літо',
    pal: {},
    sky: [0x7cc4ec, 0xd6eef7], fog: { color: 0xcfe9f3, near: 1.3, far: 2.7 },
    hemi: { sky: 0xd8ecff, ground: 0x6f7f48, i: 1.1 },
    sun: { color: 0xfff0d2, i: 3.1, dir: [-0.5, 1, 0.62], radius: 3 },
    water: { color: 0x45aee6, spec: 0x9fd8ff, opacity: 0.8 },
    shadow: 'sun', edge: 0.1, edgeG: 0.035, ao: 0.16,
    cam: { el: 56, fov: 32, dist: 1 }, vignette: 0.14, leaves: 0,
  },
  autumn: {
    label: 'Осінь',
    pal: {
      grassA: 0xc9c56c, grassB: 0xbfba61, grassDark: 0xa9a24e, tuft: 0x9a9440, cap: 0xb79a4a,
      sand: 0xe6c48c, sandB: 0xdcb87e, bed: 0x3f6266, dirt: 0x7c4a30, dirtDark: 0x5c3420,
      station: 0xd9c6a8, stationB: 0xc9b495,
      trunk: 0x5c3a24, trunkB: 0x6c4429,
      leaf: [0xe8862a, 0xd75a2a, 0xf2b63c, 0xc4432b], pine: [0x2c6440, 0x356c45, 0x285a3a],
      rock: [0x8e94a4, 0x7e8496, 0x9ea4b2, 0x878d9c], wallA: 0x8a8fa0, wallB: 0x767b8e, wallC: 0xa0a5b4,
      flower: [0xf3e6c8, 0xe7a73a, 0xb8402a, 0x9a6ab8], pebble: 0xb39d84,
      swamp: 0x55542a, swampB: 0x4d4c26, tussock: 0x8f8a3a,
    },
    sky: [0x8a78b6, 0xf6b48c], fog: { color: 0xeeb092, near: 0.95, far: 2.05 },
    hemi: { sky: 0xffd9c2, ground: 0x6a5c82, i: 1.3 },
    sun: { color: 0xffb47a, i: 2.6, dir: [-0.9, 0.6, 0.38], radius: 4 },
    water: { color: 0x3d7c98, spec: 0xffc4a0, opacity: 0.84 },
    shadow: 'sun', edge: 0.12, edgeG: 0.04, ao: 0.2,
    cam: { el: 47, fov: 30, dist: 0.96 }, vignette: 0.3, leaves: 1,
  },
  toy: {
    label: 'Іграшковий',
    pal: {
      grassA: 0x92e35c, grassB: 0x84d84f, grassDark: 0x74c846, tuft: 0x5fbf3e, cap: 0x92e35c,
      sand: 0xffe7a6, sandB: 0xfbdc92, bed: 0x58b4cc, dirt: 0xa86c40, dirtDark: 0x7d4c2c,
      station: 0xf0e2c8, stationB: 0xe4d3b4,
      trunk: 0x9c6640, trunkB: 0xab7248,
      leaf: [0x5cd65a, 0x78e25e, 0x4ac85c, 0xa2e862], pine: [0x2fb470, 0x3cc47c, 0x29a264],
      rock: [0xb4bdd0, 0xa4aec2, 0xc4cbda, 0xacb5c8], wallA: 0xa8aecb, wallB: 0x959dbb, wallC: 0xbcc2da,
      ore: [0xff7a3a, 0xffa04a, 0x5a5f78], plank: [0xc88a50, 0xb57a42, 0xd89a5c],
      tie: 0x8e5c3a, steel: 0xd8e0ec, water: 0x5ad6ff,
      flower: [0xffffff, 0xffe45c, 0xff6b8a, 0xb894ff], pebble: 0xd8ccb0,
      swamp: 0x6a8a3a, swampB: 0x5f7f34, tussock: 0x9ad860,
    },
    sky: [0x6fd2f6, 0xc6f1ff], fog: { color: 0xbdeaff, near: 2.1, far: 4.2 },
    hemi: { sky: 0xffffff, ground: 0x93a7bd, i: 1.75 },
    sun: { color: 0xfff6e6, i: 1.55, dir: [-0.35, 1, 0.5], radius: 1 },
    water: { color: 0x4fd2ff, spec: 0xffffff, opacity: 0.88 },
    shadow: 'blob', edge: 0.3, edgeG: 0.07, ao: 0.26,
    cam: { el: 63, fov: 26, dist: 1.04 }, vignette: 0, leaves: 0,
  },
};
export const LOOK_KEYS = Object.keys(LOOKS);
export const CAM_DIST = { near: { label: 'Близько', k: 0.78 }, mid: { label: 'Середньо', k: 1 }, far: { label: 'Далеко', k: 1.32 } };
export const CAM_ANGLE = { look: 'Як у вигляді', 45: '45°', 60: '60°', 75: '75°' };

const get = (k, d) => { try { return localStorage.getItem(k) ?? d; } catch { return d; } };
const set = (k, v) => { try { localStorage.setItem(k, v); } catch { } };

export function loadPrefs() {
  let cam = {};
  try { cam = JSON.parse(get('kol-cam', '{}')) || {}; } catch { }
  const look = get('kol-look', 'summer');
  return {
    look: LOOKS[look] ? look : 'summer',
    dist: CAM_DIST[cam.dist] ? cam.dist : 'mid',
    angle: cam.angle in CAM_ANGLE ? String(cam.angle) : 'look',
  };
}
export function savePrefs(p) {
  set('kol-look', p.look);
  set('kol-cam', JSON.stringify({ dist: p.dist, angle: p.angle }));
}

// секція в панелі K4 (класи її CSS); якщо панелі нема — нічого не робить
export function mountLookUi(panelRoot, view) {
  if (!view?.setLook || !panelRoot) return null;
  const pan = panelRoot.querySelector('.kol-pan') || panelRoot;
  const el = (tag, cls, txt) => { const e = document.createElement(tag); if (cls) e.className = cls; if (txt != null) e.textContent = txt; return e; };
  const prefs = view.prefs ? { ...view.prefs } : loadPrefs();
  const d = el('details', 'kol-grp'); d.open = true;
  d.append(el('summary', null, '🎨 Вигляд'));
  const seg = (label, opts, key, onPick) => {
    const box = el('div', 'kol-p'), lab = el('div', 'kol-lab', label), s = el('div', 'kol-seg');
    const btns = Object.entries(opts).map(([v, t]) => {
      const b = el('button', null, t);
      b.onclick = () => { prefs[key] = v; mark(); onPick(v); savePrefs(prefs); };
      s.append(b); return [v, b];
    });
    const mark = () => { for (const [v, b] of btns) b.classList.toggle('kol-on', v === String(prefs[key])); };
    mark(); box.append(lab, s); d.append(box);
  };
  seg('Вигляд', Object.fromEntries(LOOK_KEYS.map((k) => [k, LOOKS[k].label])), 'look', (v) => view.setLook(v));
  seg('Камера', Object.fromEntries(Object.entries(CAM_DIST).map(([k, v]) => [k, v.label])), 'dist', () => view.setCam({ dist: prefs.dist, angle: prefs.angle }));
  seg('Кут камери', CAM_ANGLE, 'angle', () => view.setCam({ dist: prefs.dist, angle: prefs.angle }));
  for (const r of view.weather?.rows?.() || []) { prefs[r.key] = r.value; seg(r.label, r.opts, r.key, r.pick); }   // K32: час доби й погода
  const note = pan.querySelector('.kol-note');
  if (note && pan.contains(note)) note.after(d); else pan.append(d);   // якір — у вкладці «🎧 Моє» (panel.js)
  return { dispose() { d.remove(); } };
}
