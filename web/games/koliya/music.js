// «Колія» — музика й тло синтезом WebAudio, без файлів. Власник: K29.
// Контракт: createMusic(audio) → { music, amb, vol: { music, amb, sfx }, setMusic(bool), setAmb(bool), setVolume(kind, 0..1),
//   update(state), onChange?(), dispose() }
//  ЗАВЖДИ вимкнено за замовчуванням. Вмикає лише перемикач у панелі («Пристрій» → «🎵 Музика», «🌿 Тло») — це жест,
//  у ньому audio.shared() створює спільний AudioContext. Стан і гучності зберігаються (kol-music); якщо музику вмикали
//  минулого разу — вона повертається на ПЕРШИЙ жест (клавіша/дотик), не сама; під ?test / автоматизацією — ніколи.
//  update(state) — щокадру з main.js (гачок K29); при вимкнених музиці й тлі — лише запам'ятати стан, нічого не будувати.
//
// Музика: генеративна, у дусі гуцульської/степової. Лад — «гуцульський» (дорійський з підвищеною IV: D E F G# A B C), для
//  теми «Чумацький шлях» — мінор (D E F G A Bb C), повільніше, 3/4, протяжні ноти з віддихом наприкінці фрази.
//  Голоси: сопілка (трикутник + фільтр + вібрато, форшлаги), басоля (дві пилки через фільтр, «ум-па» або бурдон),
//  бубон (глухий «дум», обідок «тек», бубонці). Фрази — з детермінованого PRNG від сіду смуги: період із 4 фраз
//  A (до V) · A′ (той самий початок, до I) · B (вище, до V) · A′, кожен період — новий матеріал.
//  Темп і щільність ростуть зі швидкістю паротяга й небезпекою (рейок на кілька секунд, перегрів, пожежа).
//  Перемога — каденція до тоніки; поразка — обрив (усе глушиться за ~30 мс).
// Чисте (для node-тестів): SCALES, STYLE, tempoOf, densityOf, speedOf, dangerOf, makePhrase, barEvents, cadence, MIX.
import { U, TPS, wagonPos } from './rules.js';

// — лади (півтони від тоніки) і тоніки
export const SCALES = {
  hutsul: [0, 2, 3, 6, 7, 9, 10],   // D E F G# A B C — збільшена секунда F–G#
  minor: [0, 2, 3, 5, 7, 8, 10],    // D E F G A Bb C
};
export const TONIC = 74;   // D5 — сопілка; басоля — D2 (38)
const BASS_TONIC = 38;

/** Стилі тем: лад, восьмих у такті, темп [база, +швидкість, +небезпека], щільність так само, віддих (восьмих) після фрази */
export const STYLE = {
  koliya: { scale: 'hutsul', bpb: 4, bpm: [96, 24, 30], dens: [0.45, 0.3, 0.25], rest: 0 },
  chumak: { scale: 'minor', bpb: 6, bpm: [58, 10, 18], dens: [0.2, 0.15, 0.25], rest: 2 },
};
const styleOf = (theme) => STYLE[theme] || STYLE.koliya;

/** Зведення (підсилення шин на вході лімітера): ефекти audio.js — master 0,5; музика тихіша, тло ще тихіше */
export const MIX = { sfx: 0.5, music: 0.09, amb: 0.05 };
/** Підсилення голосів музики всередині шини (до MIX.music) */
export const VOICE = { mel: 0.55, bass: 0.5, dum: 0.9, tek: 0.3, jingle: 0.22 };

const clamp01 = (v) => Math.max(0, Math.min(1, +v || 0));
export const tempoOf = (theme, speed01, danger) => { const b = styleOf(theme).bpm; return b[0] + b[1] * clamp01(speed01) + b[2] * clamp01(danger); };
export const densityOf = (theme, speed01, danger) => { const d = styleOf(theme).dens; return Math.min(1, d[0] + d[1] * clamp01(speed01) + d[2] * clamp01(danger)); };

/** Швидкість паротяга 0..1 від стелі (як audio.setTrain у main.js) */
export function speedOf(s) {
  if (!s || s.status !== 'run' || !s.train) return 0;
  const max = (s.params?.speedMax || 1) * U / TPS;
  return clamp01((s.train.speed || 0) / 1000 / max);
}

/** Небезпека 0..1: пожежа — 1; котел понад 60 % — росте до 1 на 100 %; рейок менше ніж на ~12 с ходу — росте до 1 на 2 с */
export function dangerOf(s) {
  if (!s || s.status !== 'run' || !s.train) return 0;
  const t = s.train;
  if ((t.fire || 0) > 0) return 1;
  const heat = clamp01((t.heat || 0) / 1000);
  const dHeat = clamp01((heat - 0.6) / 0.4);
  let dRail = 0;
  const cps = (t.speed || 0) / 1000 / U * TPS;
  if (cps > 0 && s.path?.length) {
    const ahead = Math.max(0, s.path.length - 1 - Math.floor((t.pos || 0) / U)) - ((t.pos || 0) % U) / U;
    dRail = clamp01((12 - ahead / cps) / 10);
  }
  return Math.max(dHeat, dRail);
}

// — PRNG (mulberry32) і змішування ключа
export function hash32(...xs) {
  let h = 0x9e3779b9;
  for (const x of xs) { h = Math.imul(h ^ (x >>> 0), 0x85ebca6b); h ^= h >>> 13; h = Math.imul(h, 0xc2b2ae35); h ^= h >>> 16; }
  return h >>> 0;
}
export function rng(seed) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
const pickW = (r, list) => {   // [[значення, вага], …]
  let sum = 0; for (const [, w] of list) sum += w;
  let x = r() * sum;
  for (const [v, w] of list) { if (x < w) return v; x -= w; }
  return list[list.length - 1][0];
};

/** Ступінь ладу (0 — тоніка, 7 — октава, може бути від'ємним) → MIDI */
export function degToMidi(deg, scaleName = 'hutsul', tonic = TONIC) {
  const sc = SCALES[scaleName], n = sc.length;
  const o = Math.floor(deg / n), m = ((deg % n) + n) % n;
  return tonic + 12 * o + sc[m];
}
export const mtof = (m) => 440 * 2 ** ((m - 69) / 12);

// — ритми такту (у восьмих): густі / середні / рідкі; останній такт фрази — до довгої ноти
const RHYTHMS = {
  4: {
    dense: [[1, 1, 1, 1], [0.5, 0.5, 1, 1, 1], [1, 0.5, 0.5, 1, 1], [0.5, 0.5, 0.5, 0.5, 1, 1]],
    mid: [[1, 1, 2], [2, 1, 1], [1, 1, 1, 1], [1.5, 0.5, 2]],
    sparse: [[2, 2], [3, 1], [4]],
    end: [[1, 1, 2], [2, 2], [4]],
  },
  6: {
    dense: [[2, 1, 1, 2], [1, 1, 2, 2], [2, 2, 1, 1]],
    mid: [[3, 1, 2], [2, 2, 2], [4, 2]],
    sparse: [[6], [4, 2], [3, 3]],
    end: [[6], [4, 2]],
  },
};
// крок мелодії (у ступенях) — переважно поступово, зрідка стрибок
const STEPS = [[-3, 0.4], [-2, 1], [-1, 3], [0, 0.7], [1, 3], [2, 1.2], [3, 0.5], [4, 0.25]];
const LO = -2, HI = 9;

function nextDeg(r, deg, scale) {
  const m = ((deg % 7) + 7) % 7;
  // гуцульська підвищена IV тягне вгору до V або вниз збільшеною секундою до III
  if (scale === 'hutsul' && m === 3) return r() < 0.7 ? deg + 1 : deg - 1;
  let d = deg + pickW(r, STEPS);
  if (d > HI) d = deg - 1 - (r() < 0.5 ? 1 : 0);
  if (d < LO) d = deg + 1 + (r() < 0.5 ? 1 : 0);
  return d;
}

/** Мелодія на nBars тактів від startDeg (перша нота) до endDeg (остання) */
function genLine(r, nBars, bpb, density, scale, startDeg, endDeg, at0 = 0) {
  const R = RHYTHMS[bpb] || RHYTHMS[4];
  const out = [];
  let deg = startDeg, first = true;
  for (let b = 0; b < nBars; b++) {
    const lastBar = b === nBars - 1;
    const rh = lastBar ? pickW(r, R.end.map((x, i) => [x, i === 0 ? 1 + density : 1 + (1 - density)]))
      : pickW(r, [...R.dense.map((x) => [x, 3 * density * density]), ...R.mid.map((x) => [x, 1]), ...R.sparse.map((x) => [x, 3 * (1 - density) ** 2])]);
    let at = at0 + b * bpb;
    rh.forEach((len, i) => {
      const last = lastBar && i === rh.length - 1;
      if (last) deg = endDeg;
      else if (!first) deg = nextDeg(r, deg, scale);
      first = false;
      const accent = at % bpb === 0;
      out.push({ at, len, deg, vel: Math.round(((accent ? 0.9 : 0.72) + (r() - 0.5) * 0.12) * 100) / 100,
        grace: len >= 1 && !last && r() < 0.18 });   // форшлаг — швидка верхня сусідня
      at += len;
    });
  }
  return out;
}

// акорди (для басолі): I, VII, v — ступені, що в них входять
const CHORDS = [[0, [0, 2, 4]], [6, [6, 1, 3]], [4, [4, 6, 1]]];
function chordOf(notes, force) {
  if (force != null) return force;
  let best = 0, bestScore = -1;
  for (const [root, tones] of CHORDS) {
    let sc = 0;
    for (const n of notes) if (tones.includes(((n.deg % 7) + 7) % 7)) sc += n.len;
    if (sc > bestScore) { best = root; bestScore = sc; }
  }
  return best;
}

/**
 * Фраза n (детерміновано від seed): 4 такти. { theme, scale, bpb, bars, len (з віддихом), mel, bass, drum }
 * Ноти: { at, len } у восьмих від початку фрази; mel — { deg, midi, vel, grace }, bass — { midi, vel }, drum — { kind, vel }.
 */
export function makePhrase(seed, n, { theme = 'koliya', density = 0.5 } = {}) {
  const st = styleOf(theme), bpb = st.bpb, scale = st.scale, bars = 4;
  const d = Math.round(clamp01(density) * 10) / 10;
  const p = Math.floor(n / 4), role = ((n % 4) + 4) % 4;
  const key = hash32(seed, theme === 'chumak' ? 2 : 1);
  const rA = rng(hash32(key, p, 1));
  const startA = pickW(rA, [[0, 3], [2, 2], [4, 2], [-1, 1]]);
  const A = genLine(rA, bars, bpb, d, scale, startA, 4);
  let mel;
  if (role === 0) mel = A;
  else if (role === 2) {
    const rB = rng(hash32(key, p, 3));
    mel = genLine(rB, bars, bpb, d, scale, pickW(rB, [[4, 2], [5, 2], [7, 1]]), 4);
  } else {
    const rA2 = rng(hash32(key, p, 2));
    const head = A.filter((x) => x.at < 2 * bpb);
    const tail = genLine(rA2, 2, bpb, d, scale, nextDeg(rA2, head[head.length - 1].deg, scale), 0, 2 * bpb);
    mel = [...head, ...tail];
  }
  mel = mel.map((x) => ({ ...x, midi: degToMidi(x.deg, scale) }));

  const rD = rng(hash32(key, p, role, 7));
  const bass = [], drum = [];
  for (let b = 0; b < bars; b++) {
    const at = b * bpb, inBar = mel.filter((x) => x.at >= at && x.at < at + bpb);
    const lastBar = b === bars - 1;
    const root = chordOf(inBar, lastBar ? (role === 1 || role === 3 ? 0 : 4) : null);
    const rm = degToMidi(root, scale, BASS_TONIC) - (root >= 4 ? 12 : 0);   // v і VII — нижче тоніки
    // квінта — якщо вона в ладі (у гуцульському в C нема G — тоді октава)
    const fifth = SCALES[scale].includes((rm + 7 - BASS_TONIC + 48) % 12) ? rm + 7 : rm + 12;
    if (theme === 'chumak') {   // бурдон: корінь і квінта на весь такт
      bass.push({ at, len: bpb, midi: rm, vel: 0.8 }, { at, len: bpb, midi: fifth, vel: 0.45 });
      drum.push({ at, kind: 'dum', vel: 0.55 });
      if (d > 0.4) drum.push({ at: at + 3, kind: 'jingle', vel: 0.5 });
      if (d > 0.6) drum.push({ at: at + 4, kind: 'tek', vel: 0.45 });
    } else {                    // «ум-па» коломийки
      bass.push({ at, len: 1, midi: rm, vel: 0.9 }, { at: at + 2, len: 1, midi: fifth, vel: 0.7 });
      if (d >= 0.7) bass.push({ at: at + 1, len: 0.5, midi: rm + 12, vel: 0.45 }, { at: at + 3, len: 0.5, midi: rm + 12, vel: 0.45 });
      drum.push({ at, kind: 'dum', vel: 1 }, { at: at + 2, kind: 'dum', vel: 0.7 });
      if (d > 0.4) drum.push({ at: at + 1, kind: 'tek', vel: 0.6 }, { at: at + 3, kind: 'tek', vel: 0.6 });
      if (d > 0.75) for (let k = 0; k < 4; k++) drum.push({ at: at + k + 0.5, kind: 'jingle', vel: 0.4 + 0.2 * rD() });
      if (lastBar && role === 3) drum.push({ at: at + 3.5, kind: 'jingle', vel: 0.8 }, { at: at + 3.75, kind: 'jingle', vel: 0.9 });
    }
  }
  return { theme, scale, bpb, bars, len: bars * bpb + st.rest, mel, bass, drum };
}

/** Каденція на перемогу: два такти до тоніки, наостанок — довга тоніка з октавою й квінтою */
export function cadence(theme = 'koliya') {
  const st = styleOf(theme), bpb = st.bpb, scale = st.scale;
  const degs = bpb === 6 ? [[4, 2], [3, 2], [1, 2]] : [[4, 1], [3, 1], [2, 1], [1, 1]];
  const mel = []; let at = 0;
  for (const [deg, len] of degs) { mel.push({ at, len, deg, vel: 0.8, grace: false }); at += len; }
  const hold = 2 * bpb;
  mel.push({ at: bpb, len: hold, deg: 0, vel: 0.95, grace: true }, { at: bpb, len: hold, deg: 4, vel: 0.5, grace: false },
    { at: bpb, len: hold, deg: 7, vel: 0.45, grace: false });
  const vRoot = degToMidi(4, scale, BASS_TONIC) - 12;
  const bass = [{ at: 0, len: bpb, midi: vRoot, vel: 0.85 }, { at: bpb, len: hold, midi: BASS_TONIC, vel: 0.95 }, { at: bpb, len: hold, midi: BASS_TONIC + 7, vel: 0.5 }];
  const drum = [{ at: 0, kind: 'dum', vel: 0.9 }, { at: bpb / 2, kind: 'dum', vel: 0.7 }, { at: bpb, kind: 'dum', vel: 1 }];
  for (let k = 0; k < 6; k++) drum.push({ at: bpb + k * 0.25, kind: 'jingle', vel: 0.9 - k * 0.12 });
  return { theme, scale, bpb, bars: 3, len: 3 * bpb, mel: mel.map((x) => ({ ...x, midi: degToMidi(x.deg, scale) })), bass, drum };
}

/** Події такту b фрази ph у секундах: t0 — початок такту, spe — секунд на восьму. → [{ t, dur, kind, midi?, vel, grace? }] */
export function barEvents(ph, b, spe, t0) {
  const a0 = b * ph.bpb, a1 = a0 + ph.bpb, out = [];
  const inBar = (x) => x.at >= a0 && x.at < a1;
  for (const x of ph.mel) if (inBar(x)) out.push({ t: t0 + (x.at - a0) * spe, dur: x.len * spe, kind: 'mel', midi: x.midi, vel: x.vel, grace: x.grace });
  for (const x of ph.bass) if (inBar(x)) out.push({ t: t0 + (x.at - a0) * spe, dur: x.len * spe, kind: 'bass', midi: x.midi, vel: x.vel });
  for (const x of ph.drum) if (inBar(x)) out.push({ t: t0 + (x.at - a0) * spe, dur: 0.2, kind: x.kind, vel: x.vel });
  return out.sort((p, q) => p.t - q.t);
}

/** Тема зараз: ?theme= в адресі, інакше kol-theme (K25, theme.js) — без залежності від файлу */
export function themeNow() {
  try { const q = new URLSearchParams(globalThis.location?.search || '').get('theme'); if (q === 'chumak' || q === 'koliya') return q; } catch { }
  try { if (globalThis.localStorage?.getItem('kol-theme') === 'chumak') return 'chumak'; } catch { }
  return 'koliya';
}

// — тло за біомом під камерою: шари wind (вітер), birds (птахи), frogs (жаби), water (плюскіт), 0..1
export const AMB_LAYERS = ['wind', 'birds', 'frogs', 'water'];
export const AMB = {
  steppe: { wind: 0.8, birds: 0.15 },             // степ: вітер, зрідка жайворонок
  grove: { birds: 0.9, wind: 0.2 },               // гай: птахи
  stone: {},                                      // кам'яне поле: тиша
  river: { water: 0.7, birds: 0.3, frogs: 0.1 },  // річкова долина
  swamp: { frogs: 0.9, water: 0.2, wind: 0.1 },   // болото: жаби
  pass: { wind: 0.3 },                            // прохід між скелями: майже тиша, вітер свистить вище
  winter: { wind: 0.7 },                          // K26: зима
  yar: { wind: 0.25, birds: 0.2 },                // K26: яр
};
const AMB_OTHER = { wind: 0.4 };
const WATER = 1;   // G.WATER (rules.js)

/** Де камера, у клітинках по x: посередині між паротягом і гуртом (камера render.js тримає обох) */
export function focusX(s) {
  if (!s?.path?.length || !s.w) return 0;
  let head = 0;
  try { head = wagonPos(s, 0).x / U; } catch { }
  const ps = s.players || [];
  if (!ps.length) return head;
  let sx = 0; for (const p of ps) sx += p.x / U;
  return (head + sx / ps.length) / 2;
}
export function biomeAt(s, x) {
  const z = (s?.zones || []).find((q) => x >= q.x0 && x < q.x1);
  return z ? z.kind : null;
}
/** Частка води в стовпцях x±r (уся висота смуги) */
export function waterNear(s, x, r = 4) {
  if (!s?.ground || !s.w || !s.h) return 0;
  let k = 0, all = 0;
  for (let cx = Math.max(0, Math.floor(x - r)); cx <= Math.min(s.w - 1, Math.floor(x + r)); cx++)
    for (let cy = 0; cy < s.h; cy++) { all++; if (s.ground[cy * s.w + cx] === WATER) k++; }
  return all ? k / all : 0;
}
/** Цілі рівні шарів тла для стану (біом під камерою + вода поруч) → { biome, wind, birds, frogs, water, windHi } */
export function ambienceFor(s) {
  const out = { biome: null, wind: 0, birds: 0, frogs: 0, water: 0, windHi: false };
  if (!s?.zones) return out;
  const x = focusX(s), b = biomeAt(s, x);
  const t = b ? AMB[b] ?? AMB_OTHER : AMB_OTHER;
  out.biome = b;
  for (const k of AMB_LAYERS) out[k] = t[k] || 0;
  out.water = Math.max(out.water, Math.min(0.8, waterNear(s, x) * 4));
  out.windHi = b === 'pass' || b === 'winter';
  return out;
}

// ————————————————————————————————————————————— звук (браузер) —————————————————————————————————————————————
const KEY = 'kol-music';
const LOOKAHEAD = 0.4, TICK = 90;

export function createMusic(audio, opts = {}) {
  const store = opts.storage ?? (() => { try { return globalThis.localStorage; } catch { return null; } })();
  const saved = (() => { try { return JSON.parse(store?.getItem(KEY) || '{}') || {}; } catch { return {}; } })();
  const vol0 = (v, d) => (typeof v === 'number' && Number.isFinite(v) ? clamp01(v) : d);
  const TEST = opts.test ?? (() => {
    try { return new URLSearchParams(globalThis.location?.search || '').has('test') || !!globalThis.navigator?.webdriver; } catch { return false; }
  })();

  let sh = null, ctx = null, busM = null, timer = 0;
  let state = null, nodes = [];
  // музика
  let mode = 'play', seed = 0, n = 0, ph = null, bar = 0, nextT = 0, theme = 'koliya';
  let danger = 0, speed = 0, lastStatus = 'run', lastWall = 0;
  // тло
  let busA = null, ambSrc = null, ambEv = [], ambT = ambienceFor(null), ambAt = 0;
  const lv = { wind: 0, birds: 0, frogs: 0, water: 0 };

  const api = {
    music: false, amb: false,
    vol: { music: vol0(saved.vm, 0.6), amb: vol0(saved.va, 0.5), sfx: vol0(saved.vs, 1) },
    onChange: null,
    setMusic(on) { on = !!on; if (on === api.music) return; api.music = on; on ? startMusic() : stopMusic(); save(); api.onChange?.(); },
    setAmb(on) { on = !!on; if (on === api.amb) return; api.amb = on; on ? startAmb() : stopAmb(); save(); api.onChange?.(); },
    setVolume(kind, v) {
      if (!(kind in api.vol)) return;
      api.vol[kind] = clamp01(v); save();
      if (kind === 'sfx') audio?.setVolume?.(api.vol.sfx);
      applyVol();
    },
    update(s) { state = s; },
    /** Для перевірок у консолі: що зараз (без звуку — лише числа) */
    debug: () => ({ music: api.music, amb: api.amb, ambLv: Object.fromEntries(AMB_LAYERS.map((k) => [k, +lv[k].toFixed(2)])), biome: ambT.biome, ambEv: ambEv.length, ctx: !!ctx, mode, theme, n, bar, bpm: Math.round(tempoOf(theme, speed, danger)), danger: +danger.toFixed(2), speed: +speed.toFixed(2), nodes: nodes.length }),
    dispose() {
      clearInterval(timer); timer = 0; stopAll(0);
      if (api.music) audio?.hold?.('music', false);
      if (api.amb) { stopAmb(); }
      api.music = false; api.amb = false;
      if (gestureOn) for (const e of ['pointerdown', 'keydown']) globalThis.removeEventListener?.(e, gestureOn, true);
    },
  };
  audio?.setVolume?.(api.vol.sfx);

  function save() { try { store?.setItem(KEY, JSON.stringify({ music: api.music, amb: api.amb, vm: api.vol.music, va: api.vol.amb, vs: api.vol.sfx })); } catch { } }
  function applyVol() {
    if (!ctx) return;
    busM?.gain.setTargetAtTime(MIX.music * api.vol.music, ctx.currentTime, 0.05);
    if (busA && api.amb) busA.gain.setTargetAtTime(MIX.amb * api.vol.amb, ctx.currentTime, 0.05);
  }

  // — спільний контекст: лише з жесту (перемикач у панелі або перший жест після збереженого «увімк.»)
  function ensure() {
    if (ctx) return true;
    sh = audio?.shared?.();
    if (!sh) return false;
    ctx = sh.ctx;
    busM = ctx.createGain(); busM.gain.value = MIX.music * api.vol.music; busM.connect(sh.out);
    return true;
  }
  function runTimer() {
    const need = api.music || api.amb;
    if (need && !timer) { timer = setInterval(tick, TICK); timer?.unref?.(); }   // unref — node-тести не висять
    if (!need && timer) { clearInterval(timer); timer = 0; }
  }
  function startMusic() {
    if (!ensure()) return;
    audio.hold?.('music', true);
    busM.gain.cancelScheduledValues(ctx.currentTime);
    busM.gain.setValueAtTime(MIX.music * api.vol.music, ctx.currentTime);
    mode = state && state.status !== 'run' ? 'done' : 'play';
    lastStatus = state?.status ?? 'run';
    ph = null; bar = 0; nextT = ctx.currentTime + 0.15;
    runTimer();
  }
  function stopMusic() {
    if (ctx) stopAll(0.25);
    audio?.hold?.('music', false);
    runTimer();
  }
  // — тло: два безперервні шари (вітер, вода — фільтрований шум) і події (птахи, жаби, плюскіт)
  const LAYER = { wind: 0.55, water: 0.3, bird: 0.22, frog: 0.3, plip: 0.14 };
  function startAmb() {
    if (!ensure()) return;
    audio.hold?.('amb', true);
    const now = ctx.currentTime;
    if (!busA) { busA = ctx.createGain(); busA.connect(sh.out); }
    busA.gain.cancelScheduledValues(now); busA.gain.setValueAtTime(0, now);
    busA.gain.linearRampToValueAtTime(MIX.amb * api.vol.amb, now + 1.5);   // вступає плавно
    if (!ambSrc) {
      // свій шум на 3 с (буфер ефектів — 1 с, петля чутна як «пульс»)
      const len = Math.floor(ctx.sampleRate * 3), buf = ctx.createBuffer(1, len, ctx.sampleRate), d = buf.getChannelData(0);
      let b = 0;
      for (let i = 0; i < len; i++) { b = b * 0.6 + (Math.random() * 2 - 1) * 0.4; d[i] = b * 1.6; }   // трохи «рожевіший» за білий
      const layer = (f, q, off) => {
        const src = ctx.createBufferSource(), bp = ctx.createBiquadFilter(), g = ctx.createGain(), gust = ctx.createGain();
        src.buffer = buf; src.loop = true; bp.type = 'bandpass'; bp.frequency.value = f; bp.Q.value = q;
        g.gain.value = 0; gust.gain.value = 0.65;
        src.connect(bp); bp.connect(g); g.connect(gust); gust.connect(busA); src.start(now, off);
        return { src, bp, g, gust };
      };
      const wind = layer(380, 0.7, 0), water = layer(1500, 0.5, 1.4);
      // пориви: повільні LFO на гучність і висоту вітру; вода «хлюпає» трохи швидше
      const lfo = (hz, depth, param) => { const o = ctx.createOscillator(), g = ctx.createGain(); o.frequency.value = hz; g.gain.value = depth; o.connect(g); g.connect(param); o.start(now); return o; };
      const lfos = [lfo(0.09, 0.35, wind.gust.gain), lfo(0.053, 120, wind.bp.frequency), lfo(0.7, 0.2, water.gust.gain), lfo(0.23, 300, water.bp.frequency)];
      ambSrc = { wind, water, lfos };
    }
    for (const k of AMB_LAYERS) lv[k] = 0;
    ambAt = -1;
    runTimer();
  }
  function stopAmb() {
    if (ctx && busA) {
      const now = ctx.currentTime;
      busA.gain.cancelScheduledValues(now); busA.gain.setValueAtTime(busA.gain.value, now); busA.gain.linearRampToValueAtTime(0, now + 0.6);
      const a = ambSrc; ambSrc = null;
      if (a) for (const x of [a.wind.src, a.water.src, ...a.lfos]) { try { x.stop(now + 0.7); } catch { } }
      for (const x of ambEv) { try { x.n.stop(now + 0.7); } catch { } }
      ambEv = [];
    }
    audio?.hold?.('amb', false);
    runTimer();
  }
  const keepA = (n, t1) => { ambEv.push({ n, t1 }); };
  function bird(t, k) {   // 2–5 коротких посвистів угору/вниз
    const n = 2 + Math.floor(Math.random() * 4), f0 = 2300 + Math.random() * 2200, up = Math.random() < 0.5;
    for (let i = 0; i < n; i++) {
      const st = t + i * (0.09 + Math.random() * 0.05), d = 0.05 + Math.random() * 0.05, f = f0 * (1 + (Math.random() - 0.5) * 0.15);
      const o = ctx.createOscillator(), g = ctx.createGain();
      o.frequency.setValueAtTime(up ? f * 0.8 : f * 1.15, st); o.frequency.exponentialRampToValueAtTime(up ? f * 1.2 : f * 0.85, st + d);
      g.gain.setValueAtTime(0, st); g.gain.linearRampToValueAtTime(LAYER.bird * k, st + 0.01); g.gain.setTargetAtTime(0, st + d * 0.6, 0.015);
      o.connect(g); g.connect(busA); o.start(st); o.stop(st + d + 0.1); keepA(o, st + d + 0.1);
    }
  }
  function frog(t, k) {   // «ква-ква»: серія глухих імпульсів пилки через смугу
    const n = 3 + Math.floor(Math.random() * 4), f = 110 + Math.random() * 90, gap = 0.028 + Math.random() * 0.012;
    const o = ctx.createOscillator(), bp = ctx.createBiquadFilter(), g = ctx.createGain();
    o.type = 'sawtooth'; o.frequency.value = f; bp.type = 'bandpass'; bp.frequency.value = f * 4; bp.Q.value = 3;
    g.gain.setValueAtTime(0, t);
    for (let i = 0; i < n; i++) { const st = t + i * gap; g.gain.setValueAtTime(LAYER.frog * k, st); g.gain.setTargetAtTime(0, st + 0.004, 0.006); }
    o.connect(bp); bp.connect(g); g.connect(busA); o.start(t); o.stop(t + n * gap + 0.05); keepA(o, t + n * gap + 0.05);
  }
  function plip(t, k) {   // краплинний «плюх»: синус стрибає вгору
    const o = ctx.createOscillator(), g = ctx.createGain(), f = 500 + Math.random() * 500;
    o.frequency.setValueAtTime(f, t); o.frequency.exponentialRampToValueAtTime(f * 2.6, t + 0.05);
    g.gain.setValueAtTime(0, t); g.gain.linearRampToValueAtTime(LAYER.plip * k, t + 0.005); g.gain.setTargetAtTime(0, t + 0.02, 0.015);
    o.connect(g); g.connect(busA); o.start(t); o.stop(t + 0.12); keepA(o, t + 0.12);
  }
  // подій за секунду на повному рівні шару
  const RATE = { birds: 1.2, frogs: 1.0, water: 1.5 };
  function ambTick(now, dt) {
    if (!ambSrc) return;
    if (now - ambAt > 0.3) { ambAt = now; ambT = ambienceFor(state); }
    const a = 1 - Math.exp(-dt / 1.5);   // плавно: ~1,5 с на перехід між біомами
    for (const k of AMB_LAYERS) lv[k] += (ambT[k] - lv[k]) * a;
    ambSrc.wind.g.gain.setTargetAtTime(lv.wind * LAYER.wind, now, 0.3);
    ambSrc.water.g.gain.setTargetAtTime(lv.water * LAYER.water, now, 0.3);
    ambSrc.wind.bp.frequency.setTargetAtTime(ambT.windHi ? 900 : 380, now, 2);
    ambEv = ambEv.filter((x) => x.t1 > now);
    if (ambEv.length > 40) return;
    const t = now + 0.05 + Math.random() * 0.05;
    if (Math.random() < RATE.birds * lv.birds * dt) bird(t, 0.6 + 0.4 * Math.random());
    if (Math.random() < RATE.frogs * lv.frogs * dt) frog(t, 0.6 + 0.4 * Math.random());
    if (Math.random() < RATE.water * lv.water * dt) plip(t, 0.5 + 0.5 * Math.random());
  }

  /** Заглушити все, що вже заплановано (fade — с; 0 — обрив) */
  function stopAll(fade) {
    if (!ctx) { nodes = []; return; }
    const now = ctx.currentTime, end = now + Math.max(0.03, fade);
    busM.gain.cancelScheduledValues(now);
    busM.gain.setValueAtTime(busM.gain.value, now);
    busM.gain.linearRampToValueAtTime(0, end);
    for (const x of nodes) { try { x.n.stop(end + 0.01); } catch { } }
    nodes = [];
    // шину повертаємо, коли старі ноти вже змовкли
    busM.gain.setValueAtTime(0, end + 0.02);
    busM.gain.linearRampToValueAtTime(MIX.music * api.vol.music, end + 0.08);
  }
  const keep = (n, t1) => { nodes.push({ n, t1 }); };

  // — голоси
  function env(g, t, a, peak, dur, rel) {
    g.gain.setValueAtTime(0, t);
    g.gain.linearRampToValueAtTime(peak, t + a);
    g.gain.setTargetAtTime(peak * 0.8, t + a, 0.12);
    g.gain.setTargetAtTime(0, t + Math.max(a, dur), rel);
  }
  function sopilka(t, midi, dur, vel, grace) {
    const f = mtof(midi), g = ctx.createGain(), lp = ctx.createBiquadFilter();
    lp.type = 'lowpass'; lp.frequency.value = Math.min(7000, f * 4); lp.Q.value = 0.4;
    const o = ctx.createOscillator(); o.type = 'triangle';
    if (grace) { o.frequency.setValueAtTime(f * 2 ** (2 / 12), t); o.frequency.setValueAtTime(f, t + 0.045); }
    else o.frequency.setValueAtTime(f, t);
    const o2 = ctx.createOscillator(); o2.frequency.value = f * 2;
    const g2 = ctx.createGain(); g2.gain.value = 0.12;
    o.connect(lp); o2.connect(g2); g2.connect(lp); lp.connect(g); g.connect(busM);
    env(g, t, 0.03, VOICE.mel * vel, dur * 0.92, 0.04);
    const end = t + dur + 0.3;
    if (dur > 0.25) {   // вібрато вступає не одразу
      const lfo = ctx.createOscillator(), lg = ctx.createGain();
      lfo.frequency.value = 5.3; lg.gain.setValueAtTime(0, t); lg.gain.linearRampToValueAtTime(f * 0.008, t + Math.min(0.35, dur * 0.6));
      lfo.connect(lg); lg.connect(o.frequency); lfo.start(t); lfo.stop(end); keep(lfo, end);
    }
    if (sh.noise) {     // «чуф» видиху на атаці
      const ns = ctx.createBufferSource(), bp = ctx.createBiquadFilter(), ng = ctx.createGain();
      ns.buffer = sh.noise; bp.type = 'bandpass'; bp.frequency.value = Math.min(8000, f * 1.5); bp.Q.value = 1;
      ng.gain.setValueAtTime(VOICE.mel * vel * 0.12, t); ng.gain.setTargetAtTime(0, t + 0.02, 0.03);
      ns.connect(bp); bp.connect(ng); ng.connect(busM); ns.start(t, (t * 0.37) % 0.5); ns.stop(t + 0.15); keep(ns, t + 0.15);
    }
    o.start(t); o2.start(t); o.stop(end); o2.stop(end); keep(o, end); keep(o2, end);
  }
  function basolya(t, midi, dur, vel) {
    const f = mtof(midi), g = ctx.createGain(), lp = ctx.createBiquadFilter();
    lp.type = 'lowpass'; lp.Q.value = 1.2;
    lp.frequency.setValueAtTime(250, t); lp.frequency.linearRampToValueAtTime(800, t + 0.08); lp.frequency.setTargetAtTime(500, t + 0.08, 0.2);
    lp.connect(g); g.connect(busM);
    env(g, t, 0.05, VOICE.bass * vel * 0.5, dur * 0.95, 0.08);
    const end = t + dur + 0.4;
    for (const dt of [-6, 6]) {
      const o = ctx.createOscillator(); o.type = 'sawtooth'; o.frequency.value = f; o.detune.value = dt;
      o.connect(lp); o.start(t); o.stop(end); keep(o, end);
    }
  }
  function drum(t, kind, vel) {
    const g = ctx.createGain(); g.connect(busM);
    if (kind === 'dum') {
      const o = ctx.createOscillator(); o.frequency.setValueAtTime(130, t); o.frequency.exponentialRampToValueAtTime(55, t + 0.18);
      g.gain.setValueAtTime(0, t); g.gain.linearRampToValueAtTime(VOICE.dum * vel, t + 0.004); g.gain.setTargetAtTime(0, t + 0.01, 0.09);
      o.connect(g); o.start(t); o.stop(t + 0.5); keep(o, t + 0.5);
      return;
    }
    if (!sh.noise) return;
    const ns = ctx.createBufferSource(), fl = ctx.createBiquadFilter();
    ns.buffer = sh.noise;
    if (kind === 'tek') { fl.type = 'bandpass'; fl.frequency.value = 1900; fl.Q.value = 1.3; }
    else { fl.type = 'highpass'; fl.frequency.value = 6500; }
    const d = kind === 'tek' ? 0.04 : 0.09;
    g.gain.setValueAtTime(0, t); g.gain.linearRampToValueAtTime(VOICE[kind] * vel, t + 0.002); g.gain.setTargetAtTime(0, t + 0.004, d / 3);
    ns.connect(fl); fl.connect(g); ns.start(t, (t * 0.61) % 0.5); ns.stop(t + d + 0.15); keep(ns, t + d + 0.15);
  }
  function play(e) {
    if (e.kind === 'mel') sopilka(e.t, e.midi, e.dur, e.vel, e.grace);
    else if (e.kind === 'bass') basolya(e.t, e.midi, e.dur, e.vel);
    else drum(e.t, e.kind, e.vel);
  }

  // — розклад: потакт наперед на LOOKAHEAD
  function pumpMusic(now) {
    if (mode !== 'play') return;
    if (nextT < now) { nextT = now + 0.05; }   // вкладка спала — не наздоганяти
    while (nextT < now + LOOKAHEAD) {
      if (!ph) {
        theme = themeNow();
        ph = makePhrase(seed, n, { theme, density: densityOf(theme, speed, danger) });
        bar = 0;
      }
      const spe = 60 / tempoOf(theme, speed, danger) / 2;
      if (bar < ph.bars) {
        for (const e of barEvents(ph, bar, spe, nextT)) play(e);
        nextT += ph.bpb * spe; bar++;
      } else {
        nextT += (ph.len - ph.bars * ph.bpb) * spe;   // віддих (чумацька)
        ph = null; n++;
      }
    }
  }
  function playCadence(now) {
    stopAll(0.12);
    const c = cadence(theme), spe = 60 / tempoOf(theme, 0.3, 0) / 2, t0 = now + 0.15;
    for (let b = 0; b < c.bars; b++) for (const e of barEvents(c, b, spe, t0 + b * c.bpb * spe)) play(e);
  }

  function tick() {
    if (!ctx) return;
    const now = ctx.currentTime;
    const wall = now, dt = lastWall ? Math.min(0.5, wall - lastWall) : 0.09; lastWall = wall;
    const s = state;
    if (s) {
      const sd = (s.seed ?? 0) >>> 0;
      if (sd !== seed) { seed = sd; n = 0; ph = null; }
      speed = speedOf(s);
      const dg = dangerOf(s);
      danger += (dg - danger) * (1 - Math.exp(-dt / (dg > danger ? 0.6 : 3)));   // росте швидко, спадає поволі
      if (api.music && s.status !== lastStatus) {
        if (s.status === 'won') { mode = 'done'; playCadence(now); }
        else if (s.status === 'lost') { mode = 'done'; stopAll(0); }              // обрив
        else if (s.status === 'run') { mode = 'play'; n = 0; ph = null; danger = 0; nextT = now + 0.3; }
      }
      lastStatus = s.status;
    }
    nodes = nodes.filter((x) => x.t1 > now);
    if (api.music) pumpMusic(now);
    if (api.amb) ambTick(now, dt);
  }

  // — збережене «увімк.» повертається на перший жест (не сам); під автоматизацією — ніколи
  let gestureOn = null;
  if (!TEST && (saved.music || saved.amb) && typeof globalThis.addEventListener === 'function') {
    gestureOn = () => {
      for (const e of ['pointerdown', 'keydown']) globalThis.removeEventListener(e, gestureOn, true);
      gestureOn = null;
      if (saved.music) api.setMusic(true);
      if (saved.amb) api.setAmb(true);
    };
    for (const e of ['pointerdown', 'keydown']) globalThis.addEventListener(e, gestureOn, true);
  }

  api._tick = tick;   // node-тести
  return api;
}
