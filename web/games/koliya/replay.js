// «Колія» — повтор партії (K35). Власник: K35.
// Правила детерміновані: партія = newGame(seed, params, n) + команди по тиках. Тож повтор — це запис команд і програвання.
// Ядро (без DOM, тестується в Node):
//   recBegin(seed, params, n, meta?) → rec { v, seed, params, n, local, ticks: [[t, [cmd…]]…], end, hash, ... }
//   recordTick(rec, s, cmds) — ДО R.step: пише команди тику s.tick (лише тики з командами; 'in', що нічого не міняє, — ні)
//   recFinish(rec, s) — партія скінчилась: end = s.tick, hash = R.hash(s)
//   createPlayer(rec) → { s, seek(t), stepTo(t), done } — програвання (з точками відновлення для перемотки назад)
//   playAll(rec) → кінцевий стан; encode(rec) → Promise<код> (varint + deflate-raw, base64url); decode(код) → Promise<rec>
// Гачки: main.js (restart → begin, tickOnce → rec, frame → активний повтор), net.js (журнал C), hud.js (фінал),
//   menu.js («📼 Повтори»), render.js (setFollow — камера за гравцем).
// Затиснутий use з мережі (h) — як net.prep: перед кроком гравцеві ut = tick.
import * as R from './rules.js';
import { pcol, pmark } from './panel.js';   // K53: кольори гравців — з урахуванням «Кольори для дальтоніків»

export const STORE_KEY = 'kol-replays';
const STORE_MAX = 1000 * 1000;   // ~1 МБ на всі збережені
const KINDS = ['in', 'use', 'join', 'leave', 'throw', 'horn', 'pose'];   // 7 — невідомий (рядком)
const clampI = (v, a, b) => (v < a ? a : v > b ? b : v);

export function recBegin(seed, params, n, meta = {}) {
  return {
    v: 1, seed: seed >>> 0, params: { ...R.defaultParams(), ...(params || {}) }, n: Math.max(1, n | 0),
    local: meta.local ?? n, net: !!meta.net, ticks: [], end: -1, hash: 0, status: '', reason: '', at: meta.at ?? 0,
  };
}

/** Команди тику s.tick — до R.step. 'in', що не міняє наміру гравця, не пишемо (у rules це просте присвоєння). */
export function recordTick(rec, s, cmds) {
  if (!rec || rec.end >= 0 || s.status !== 'run' || !cmds || !cmds.length) return;
  const out = [], mx = [], my = [];   // mx/my — намір, уже поставлений раніше в цьому ж тику
  for (const c of cmds) {
    if (!c) continue;
    if (c.k === 'in') {
      const p = c.p | 0, pl = s.players[p];
      const x = clampI(c.mx | 0, -100, 100), y = clampI(c.my | 0, -100, 100);
      if (pl && x === (mx[p] ?? pl.mx) && y === (my[p] ?? pl.my)) continue;   // звіряємось зі станом, не з записом
      mx[p] = x; my[p] = y;
      out.push({ p, k: 'in', mx: x, my: y });
    } else out.push({ ...c });
  }
  if (out.length) rec.ticks.push([s.tick, out]);
}

export function recFinish(rec, s) {
  if (!rec || rec.end >= 0) return rec;
  rec.end = s.tick; rec.hash = R.hash(s) >>> 0; rec.status = s.status; rec.reason = s.reason || '';
  rec.stations = s.endless?.stations ?? 0; rec.cells = s.stats?.cells ?? 0;
  return rec;
}

/** Як net.prep: затиснутий use з журналу */
function prep(s, cmds) { for (const c of cmds) if (c.k === 'use' && c.h) { const pl = s.players[c.p]; if (pl) pl.ut = s.tick; } }

/** Програвач. seek(t) — до тику t (назад — від найближчої точки відновлення). onTick(s) — після кожного кроку. */
export function createPlayer(rec, { every = 400, onTick } = {}) {
  const fresh = () => R.newGame(rec.seed, rec.params, rec.n);
  const pl = {
    s: fresh(), i: 0, marks: [], done: false,
    /** один тик; false — партія вже скінчилась */
    step() {
      const s = pl.s;
      if (s.status !== 'run') { pl.done = true; return false; }
      if (s.tick % every === 0 && !pl.marks.some((m) => m.t === s.tick)) pl.marks.push({ t: s.tick, i: pl.i, s: R.clone(s) });
      let cmds = [];
      if (pl.i < rec.ticks.length && rec.ticks[pl.i][0] === s.tick) cmds = rec.ticks[pl.i++][1];
      while (pl.i < rec.ticks.length && rec.ticks[pl.i][0] < s.tick) pl.i++;   // не мало б статись
      prep(s, cmds);
      R.step(s, cmds);
      onTick?.(s);
      if (s.status !== 'run') pl.done = true;
      return true;
    },
    stepTo(t, budget = Infinity) {
      while (pl.s.tick < t && budget-- > 0) if (!pl.step()) break;
      return pl.s;
    },
    /** до тику t (назад — з точки відновлення); true — світ замінено (рендеру — setWorld) */
    seek(t) {
      t = Math.max(0, t | 0);
      if (t >= pl.s.tick) { pl.stepTo(t); return false; }
      let m = null;
      for (const x of pl.marks) if (x.t <= t && (!m || x.t > m.t)) m = x;
      if (m) { pl.s = R.clone(m.s); pl.i = m.i; } else { pl.s = fresh(); pl.i = 0; }
      pl.done = false;
      const save = onTick; onTick = null; pl.stepTo(t); onTick = save;
      return true;
    },
  };
  return pl;
}

/** Останній тик запису: кінець партії, а без нього (запис обірвано) — тик після останньої команди */
export const lastTick = (rec) => rec.end >= 0 ? rec.end : rec.ticks.length ? rec.ticks[rec.ticks.length - 1][0] + 1 : 0;
export function playAll(rec) { const p = createPlayer(rec, { every: 1e9 }); p.stepTo(lastTick(rec)); return p.s; }

/** Чи повтор зійшовся: { ok, tick, want, got } (want 0 — партія без звірки) */
export function verify(rec, s) {
  if (!(rec.end >= 0)) return { ok: null, tick: s.tick, want: 0, got: R.hash(s) >>> 0 };
  const got = R.hash(s) >>> 0;
  return { ok: s.tick === rec.end && got === rec.hash, tick: s.tick, want: rec.hash, got };
}

// ---------------- кодування: varint, дельта тиків, deflate-raw (CompressionStream), base64url ----------------
class W { constructor() { this.a = new Uint8Array(1024); this.n = 0; }
  b(v) { if (this.n >= this.a.length) { const x = new Uint8Array(this.a.length * 2); x.set(this.a); this.a = x; } this.a[this.n++] = v & 255; }
  u(v) { v = Math.max(0, Math.floor(v)); while (v >= 128) { this.b((v % 128) | 128); v = Math.floor(v / 128); } this.b(v); }
  z(v) { v = Math.round(v); this.u(v >= 0 ? v * 2 : -v * 2 - 1); }
  str(t) { const e = new TextEncoder().encode(t); this.u(e.length); for (const x of e) this.b(x); }
  out() { return this.a.subarray(0, this.n); } }
class Rd { constructor(a) { this.a = a; this.i = 0; }
  b() { if (this.i >= this.a.length) throw new Error('код повтору обірвано'); return this.a[this.i++]; }
  u() { let v = 0, m = 1, x; do { x = this.b(); v += (x & 127) * m; m *= 128; } while (x & 128); return v; }
  z() { const v = this.u(); return v % 2 ? -(v + 1) / 2 : v / 2; }
  str() { const n = this.u(); const s = this.a.subarray(this.i, this.i + n); this.i += n; return new TextDecoder().decode(s); } }

const STATUS = ['', 'run', 'won', 'lost'];
// Стовпцями (окремі потоки — deflate тисне краще): 0 — шапка й тики (дельта тику, к-сть команд), 1 — заголовки команд
// (p | вид<<2 | поза 32 | h 64 | p>3 128), 2/3 — mx/my як різниця з попереднім наміром того ж гравця (zigzag varint),
// 4 — решта (поза, рядки невідомих видів, p > 3).
export function encodeBytes(rec) {
  const w = new W(), hd = new W(), mx = new W(), my = new W(), ex = new W();
  w.u(2); w.u(rec.seed >>> 0); w.u(rec.n); w.u(rec.local ?? rec.n); w.u(rec.net ? 1 : 0);
  w.u(rec.end + 1); w.u(rec.hash >>> 0); w.u(Math.max(0, STATUS.indexOf(rec.status))); w.str(rec.reason || '');
  w.u(Math.floor((rec.at || 0) / 1000)); w.u(rec.stations | 0); w.u(rec.cells | 0);
  // params — лише відмінні від типових (типові дописуються з defaultParams при декодуванні)
  const d = R.defaultParams(), diff = {};
  for (const k in rec.params) if (JSON.stringify(rec.params[k]) !== JSON.stringify(d[k])) diff[k] = rec.params[k];
  w.str(JSON.stringify(diff));
  w.u(rec.ticks.length);
  let prev = -1;
  const lx = [], ly = [];
  for (const [t, cmds] of rec.ticks) {
    w.u(t - prev - 1); prev = t;
    w.u(cmds.length);
    for (const c of cmds) {
      let k = KINDS.indexOf(c.k); if (k < 0) k = 7;
      const p = c.p | 0, pose = c.x !== undefined || c.fx !== undefined;
      hd.b(p & 3 | (k << 2) | (pose ? 32 : 0) | (c.h ? 64 : 0) | (p > 3 ? 128 : 0));
      if (p > 3) ex.u(p);
      if (k === 7) ex.str(String(c.k));
      if (c.k === 'in') { mx.z((c.mx | 0) - (lx[p] ?? 0)); my.z((c.my | 0) - (ly[p] ?? 0)); lx[p] = c.mx | 0; ly[p] = c.my | 0; }
      if (pose) { ex.z(c.x ?? 0); ex.z(c.y ?? 0); ex.b((Math.sign(c.fx | 0) + 1) | ((Math.sign(c.fy | 0) + 1) << 2) | (c.x === undefined ? 16 : 0)); }
    }
  }
  for (const s of [hd, mx, my, ex]) { const o = s.out(); w.u(o.length); for (const x of o) w.b(x); }
  return w.out();
}

export function decodeBytes(a) {
  const r = new Rd(a);
  const v = r.u(); if (v !== 2) throw new Error('невідома версія коду повтору');
  const rec = recBegin(r.u(), {}, r.u());
  rec.local = r.u(); rec.net = r.u() === 1; rec.end = r.u() - 1; rec.hash = r.u() >>> 0;
  rec.status = STATUS[r.u()] ?? ''; rec.reason = r.str(); rec.at = r.u() * 1000; rec.stations = r.u(); rec.cells = r.u();
  rec.params = { ...R.defaultParams(), ...JSON.parse(r.str() || '{}') };
  const n = r.u(), head = [];
  let prev = -1;
  for (let j = 0; j < n; j++) { const t = prev + 1 + r.u(); prev = t; head.push([t, r.u()]); }
  const sub = () => { const len = r.u(); const s = new Rd(a.subarray(r.i, r.i + len)); r.i += len; if (s.a.length !== len) throw new Error('код повтору обірвано'); return s; };
  const hd = sub(), mx = sub(), my = sub(), ex = sub();
  const lx = [], ly = [];
  for (const [t, m] of head) {
    const cmds = [];
    for (let q = 0; q < m; q++) {
      const h = hd.b();
      let p = h & 3; if (h & 128) p = ex.u();
      const ki = (h >> 2) & 7;
      const k = ki === 7 ? ex.str() : KINDS[ki];
      const c = k === 'join' ? { k } : { p, k };
      if (k === 'in') { c.mx = lx[p] = (lx[p] ?? 0) + mx.z(); c.my = ly[p] = (ly[p] ?? 0) + my.z(); }
      if (h & 32) { const x = ex.z(), y = ex.z(), f = ex.b(); if (!(f & 16)) { c.x = x; c.y = y; } c.fx = (f & 3) - 1; c.fy = ((f >> 2) & 3) - 1; }
      if (h & 64) c.h = 1;
      cmds.push(c);
    }
    rec.ticks.push([t, cmds]);
  }
  return rec;
}

const B64 = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_';
function toB64(a) {
  let s = '';
  for (let i = 0; i < a.length; i += 3) {
    const n = (a[i] << 16) | ((a[i + 1] ?? 0) << 8) | (a[i + 2] ?? 0);
    s += B64[(n >> 18) & 63] + B64[(n >> 12) & 63] + (i + 1 < a.length ? B64[(n >> 6) & 63] : '') + (i + 2 < a.length ? B64[n & 63] : '');
  }
  return s;
}
function fromB64(s) {
  s = s.replace(/[\s=]/g, '').replace(/\+/g, '-').replace(/\//g, '_');
  const out = new Uint8Array(Math.floor(s.length * 3 / 4));
  let o = 0;
  for (let i = 0; i < s.length; i += 4) {
    const v = [0, 1, 2, 3].map((j) => (i + j < s.length ? B64.indexOf(s[i + j]) : 0));
    if (v.some((x) => x < 0)) throw new Error('у коді повтору чужі символи');
    const n = (v[0] << 18) | (v[1] << 12) | (v[2] << 6) | v[3];
    out[o++] = (n >> 16) & 255; if (i + 2 < s.length) out[o++] = (n >> 8) & 255; if (i + 3 < s.length) out[o++] = n & 255;
  }
  return out.subarray(0, o);
}
async function pipe(a, Stream) {
  const res = new Response(new Blob([a]).stream().pipeThrough(new Stream('deflate-raw')));
  return new Uint8Array(await res.arrayBuffer());
}
const canZip = () => typeof CompressionStream === 'function' && typeof DecompressionStream === 'function';

/** Код повтору: 'K1z.' + base64url(deflate) або 'K1.' + base64url(сирі байти) */
export async function encode(rec) {
  const raw = encodeBytes(rec);
  if (canZip()) { try { return 'K1z.' + toB64(await pipe(raw, CompressionStream)); } catch { } }
  return 'K1.' + toB64(raw);
}
export async function decode(code) {
  const m = /(K1z?)\.([A-Za-z0-9\-_+/=\s]+)/.exec(String(code || ''));
  if (!m) throw new Error('це не код повтору «Колії»');
  let a = fromB64(m[2]);
  if (m[1] === 'K1z') { if (!canZip()) throw new Error('браузер не вміє розпакувати код'); a = await pipe(a, DecompressionStream); }
  return decodeBytes(a);
}

/** Кращий із двох: доїхали > ні; швидше; нескінченна — більше станцій; далі */
export function better(a, b) {
  if (!b) return true;
  const sc = (r) => [r.status === 'won' ? 1 : 0, r.status === 'won' ? -r.end : 0, r.stations | 0, r.cells | 0, r.end];
  const x = sc(a), y = sc(b);
  for (let i = 0; i < x.length; i++) if (x[i] !== y[i]) return x[i] > y[i];
  return false;
}

/** Збережені: { last: [{ code, info }…] (до 3), best: { code, info } | null } */
export function loadStore(ls = globalThis.localStorage) {
  try { const o = JSON.parse(ls?.getItem(STORE_KEY) || 'null'); if (o && Array.isArray(o.last)) return { last: o.last.filter((x) => x && typeof x.code === 'string').slice(0, 3), best: o.best && typeof o.best.code === 'string' ? o.best : null }; } catch { }
  return { last: [], best: null };
}
export function infoOf(rec) { return { seed: rec.seed, n: rec.n, local: rec.local, end: rec.end, status: rec.status, reason: rec.reason, stations: rec.stations | 0, cells: rec.cells | 0, at: rec.at || 0, mode: rec.params.mode, sm: rec.params.stationMode }; }
/** Додати партію: останні 3 + найкраща; вміщаємо в ~1 МБ (спершу викидаємо найстаріші) */
export function pushStore(st, code, rec) {
  const it = { code, info: infoOf(rec) };
  st.last = [it, ...st.last].slice(0, 3);
  if (rec.end >= 0 && rec.params.mode !== 'tutorial' && better(it.info, st.best?.info)) st.best = it;
  const size = () => JSON.stringify(st).length;
  while (size() > STORE_MAX && st.last.length) st.last.pop();
  if (size() > STORE_MAX) st.best = null;
  return st;
}
export function saveStore(st, ls = globalThis.localStorage) { try { ls?.setItem(STORE_KEY, JSON.stringify(st)); return true; } catch { return false; } }

// ---------------- перегляд у браузері (DOM) ----------------
const SPEEDS = [1, 2, 4, 8];
// K42: звук у повторі — лише на ×1–×2 і не більше SOUND_TICKS тиків за кадр (×8 чи ривок кадру — без лавини звуків)
export const SOUND_TICKS = 3;
export const soundTicks = (speed, n) => (speed > 2 ? 0 : Math.min(n, SOUND_TICKS));
const CSS = `
.kol-rp { position: fixed; inset: 0; pointer-events: none; z-index: 30; font: 600 15px/1.3 system-ui, sans-serif; color: #fff; }
.kol-rp .kol-rpbar { position: absolute; left: 50%; bottom: 14px; transform: translateX(-50%); width: min(96vw, 760px); pointer-events: auto;
  background: #141a10e6; border: 1px solid #ffffff2a; border-radius: 14px; padding: 8px 10px; display: flex; flex-direction: column; gap: 6px; box-sizing: border-box; }
.kol-rp .kol-rptop { position: absolute; left: 50%; top: 10px; transform: translateX(-50%); background: #141a10cc; border: 1px solid #ffffff2a;
  border-radius: 12px; padding: 5px 14px; white-space: nowrap; max-width: 92vw; overflow: hidden; text-overflow: ellipsis; }
.kol-rp .kol-rprow { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; justify-content: center; }
.kol-rp button { font: inherit; color: #fff; background: #2c3a22; border: 1px solid #ffffff2a; border-radius: 9px; min-height: 36px; padding: 0 10px;
  cursor: pointer; touch-action: manipulation; }
.kol-rp button.on { background: #5a8034; border-color: #9bc25a; }
.kol-rp .kol-rptrack { position: relative; height: 12px; background: #ffffff1c; border-radius: 6px; cursor: pointer; }
.kol-rp .kol-rpfill { position: absolute; left: 0; top: 0; bottom: 0; background: #ffd23f; border-radius: 6px; }
.kol-rp .kol-rpverd { text-align: center; font-size: .9em; min-height: 1.2em; }
.kol-rp .kol-rpverd.bad { color: #ff8a6a; } .kol-rp .kol-rpverd.ok { color: #b6ff5f; }
.kol-rp .kol-rpkeys { text-align: center; font-size: .72em; opacity: .7; font-weight: 500; }
.kol-rpdlg { position: fixed; inset: 0; z-index: 31; display: flex; align-items: center; justify-content: center; background: #000a; font: 600 15px/1.35 system-ui, sans-serif; color: #fff; }
.kol-rpdlg > div { width: min(92vw, 520px); box-sizing: border-box; background: #1d2418; border: 1px solid #ffffff2a; border-radius: 14px; padding: 14px; display: flex; flex-direction: column; gap: 8px; }
.kol-rpdlg h2 { margin: 0; font-size: 1.2em; text-align: center; }
.kol-rpdlg textarea { width: 100%; box-sizing: border-box; min-height: 90px; font: 500 12px/1.3 ui-monospace, monospace; color: #cfe; background: #0b0d09; border: 1px solid #ffffff2a; border-radius: 8px; }
.kol-rpdlg button { font: inherit; color: #fff; background: #2c3a22; border: 1px solid #ffffff2a; border-radius: 10px; min-height: 40px; padding: 0 12px; cursor: pointer; }
.kol-rpdlg .kol-rpmain { background: #4a6a2a; border-color: #9bc25a; }
.kol-rpdlg .kol-rperr { color: #ff8a6a; min-height: 1.2em; text-align: center; }
@media (max-width: 600px) { .kol-rp .kol-rpkeys { display: none; } .kol-rp button { padding: 0 7px; } }
`;
const fmtT = (t) => { const s = Math.max(0, Math.floor(t / R.TPS)); return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`; };
const endName = (i) => i.status === 'won' ? '🏁 доїхали' : i.reason === 'fire' ? '🔥 згоріли' : i.status === 'lost' ? '💥 з колії' : '…';
export function describe(i) {
  return `смуга №${i.seed} · ${i.n} ${i.n === 1 ? 'гравець' : 'гравці'} · ${endName(i)}${i.end >= 0 ? ' · ' + fmtT(i.end) : ''}${i.stations ? ` · 🚉 ${i.stations}` : ''}${i.mode === 'tutorial' ? ' · 🎓' : ''}`;
}

/**
 * Повтор у грі. createReplay(game, { hud? (елемент HUD — ховаємо на час повтору), input?, say?(text, ms) }) →
 *  begin(s, seed, params, n, meta) / rec(s, cmds) / watch() — запис (гачки main.js, net.js);
 *  has(), open(rec?), copyCode(rec?), paste() — фінал і меню; active, frame(dt, view) — цикл main.js, поки йде повтор;
 *  info() — для перевірок: { tick, end, speed, paused, follow, verdict }; runToEnd() — синхронно до кінця.
 */
export function createReplay(game, opts = {}) {
  let cur = null, live = null, saved = null;   // запис поточної партії, стан, який пишемо, запис, уже покладений у сховище
  let act = null;                              // перегляд: { rec, pl, speed, paused, follow, acc, verdict, … }
  let dlg = null, style = null;
  const say = (t, ms) => opts.say?.(t, ms);

  function finish() {
    if (!cur || cur.end >= 0 || !live || live.status === 'run') return;
    recFinish(cur, live);
    if (saved === cur) return;
    saved = cur;
    const r = cur;
    encode(r).then((code) => { r._code = code; saveStore(pushStore(loadStore(), code, r)); }).catch((e) => console.warn('Колія: повтор не зберігся', e));
  }
  const ctl = {
    get active() { return !!act; },
    get cur() { return cur; },
    begin(s, seed, params, n, meta = {}) { finish(); live = s; cur = recBegin(seed, params, n, { ...meta, at: Date.now() }); },
    rec(s, cmds) { if (s === live && cur) { if (s.status === 'run') recordTick(cur, s, cmds); else finish(); } },
    watch() { finish(); },
    has: () => !!cur,
    open(rec = null) {
      finish();
      rec = rec || cur;
      if (!rec) { say('📼 Повтору ще нема'); return false; }
      start(rec);
      return true;
    },
    async code(rec = null) { finish(); rec = rec || cur; if (!rec) return ''; return rec._code || (rec._code = await encode(rec)); },
    async copyCode(rec = null) {
      const code = await ctl.code(rec);
      if (!code) { say('📼 Повтору ще нема'); return ''; }
      let ok = false;
      try { await navigator.clipboard.writeText(code); ok = true; } catch { }
      if (!ok) { try { const t = document.createElement('textarea'); t.value = code; document.body.appendChild(t); t.select(); ok = document.execCommand('copy'); t.remove(); } catch { } }
      say(ok ? `📋 Код повтору скопійовано (${Math.ceil(code.length / 1024)} КБ) — надішліть другу` : '📋 Не вийшло скопіювати — браузер не дав доступу до буфера', 4000);
      return code;
    },
    paste: () => openDialog(),
    frame, exit,
    info() { return act ? { tick: act.pl.s.tick, end: act.rec.end, speed: act.speed, paused: act.paused, follow: act.follow, verdict: act.verdict, done: act.pl.done } : null; },
    runToEnd() { if (!act) return null; seekTo(lastTick(act.rec)); return act.verdict; },
  };

  function css() { if (!style) { style = document.createElement('style'); style.textContent = CSS; document.head.appendChild(style); } }
  function el(tag, cls, parent, html) { const e = document.createElement(tag); if (cls) e.className = cls; if (html != null) e.innerHTML = html; parent?.appendChild(e); return e; }

  function start(rec) {
    css();
    exit(true);
    const view = game.view;
    const pl = createPlayer(rec, { every: 400 });
    act = { rec, pl, speed: 1, paused: false, follow: -1, acc: 0, verdict: null, view, txt: {}, pad: new Map() };
    view?.setWorld?.(pl.s);
    view?.setFollow?.(-1);
    if (opts.hud) opts.hud.style.visibility = 'hidden';
    hideOverlays(true);   // K42: підказки й сенсорні кнопки — геть на час повтору
    const root = act.root = el('div', 'kol-rp', document.body);
    act.top = el('div', 'kol-rptop', root);
    const bar = el('div', 'kol-rpbar', root);
    act.verdEl = el('div', 'kol-rpverd', bar);
    act.track = el('div', 'kol-rptrack', bar); act.fill = el('div', 'kol-rpfill', act.track);
    const row = el('div', 'kol-rprow', bar);
    const b = (txt, fn, title) => { const x = el('button', '', row, txt); x.type = 'button'; x.title = title || ''; x.addEventListener('click', (e) => { e.preventDefault(); x.blur(); fn(); }); return x; };
    b('⏮', () => seekTo(0), 'З початку (Home)');
    b('⏪', () => seekTo(act.pl.s.tick - 10 * R.TPS), '−10 с (←)');
    act.playBtn = b('⏸', () => togglePause(), 'Пауза (Space)');
    b('⏩', () => seekTo(act.pl.s.tick + 10 * R.TPS), '+10 с (→)');
    act.spBtns = SPEEDS.map((v, i) => b(`×${v}`, () => setSpeed(v), `Швидкість ×${v} (${i + 1})`));
    act.camBtn = b('👥 Гурт', () => cycleCam(1), 'Камера: гурт / за гравцем (Tab, LB/RB)');
    b('📋 Код', () => ctl.copyCode(act.rec), 'Код повтору — у буфер');
    b('✕ Вийти', () => exit(), 'Вийти (Esc)');
    el('div', 'kol-rpkeys', bar, 'Space — пауза · 1–4 чи [ ] — швидкість · ← → — ±10 с · Tab — камера · Esc — вийти · 🎮 A пауза · LB/RB камера · ✚◀▶ ±10 с · ▲▼ швидкість · B вийти');
    act.track.addEventListener('click', (e) => {
      const r = act.track.getBoundingClientRect();
      seekTo(Math.round((e.clientX - r.left) / Math.max(1, r.width) * endTick()));
    });
    addEventListener('keydown', onKey, true);
    setSpeed(1);
    return act;
  }
  const endTick = () => Math.max(1, lastTick(act.rec));
  function hideOverlays(on) {
    const tp = game.tips;
    if (on) { if (tp?.hide) tp.hide(); else tp?.update?.(null, game); }   // update без стану — hideAll (tips.js)
    game.touch?.setHidden?.(on);
  }

  function exit(quiet = false) {
    if (!act) return;
    removeEventListener('keydown', onKey, true);
    act.root.remove();
    act.view?.setFollow?.(-1);
    act = null;
    hideOverlays(false);   // K42: підказки main.js покаже сам з наступного кадру
    opts.audio?.setTrain?.(0);
    if (opts.hud) opts.hud.style.visibility = '';
    if (game.state) game.view?.setWorld?.(game.state);
    if (quiet !== true) opts.input?.swallow?.();
  }
  function setSpeed(v) { if (!SPEEDS.includes(v)) return; act.speed = v; act.spBtns.forEach((x, i) => x.classList.toggle('on', SPEEDS[i] === v)); }
  function togglePause() {
    if (act.verdict || act.pl.done) { seekTo(0); act.paused = false; return; }   // скінчився — ще раз
    act.paused = !act.paused;
  }
  function cycleCam(d) {
    const m = act.pl.s.players.length + 1;
    act.follow = (((act.follow + 1 + d) % m) + m) % m - 1;
    act.view?.setFollow?.(act.follow);
  }
  function seekTo(t) {
    const end = lastTick(act.rec);
    t = Math.max(0, Math.min(end, t));
    if (act.verdict && t < end) act.paused = false;   // з кінця назад — грає далі
    if (act.pl.seek(t)) act.view?.setWorld?.(act.pl.s);
    act.acc = 0;
    check();
  }
  // кінець: тик кінця партії досягнуто чи світ сам скінчився — звіряємо hash
  function check() {
    const s = act.pl.s, rec = act.rec;
    if (!(act.pl.done || s.tick >= lastTick(rec))) { act.verdict = null; return; }
    if (!act.verdict) {
      act.verdict = verify(rec, s);
      if (act.verdict.ok === false) console.warn('Колія: повтор розійшовся', act.verdict);
    }
    act.paused = true;
  }

  function onKey(e) {
    if (!act) return;
    const k = e.code;
    let hit = true;
    if (k === 'Escape') exit();
    else if (k === 'Space' || k === 'KeyP' || k === 'Enter') { if (!e.repeat) togglePause(); }
    else if (k >= 'Digit1' && k <= 'Digit4' && k.length === 6) setSpeed(SPEEDS[+k[5] - 1]);
    else if (k === 'BracketLeft' || k === 'Minus') setSpeed(SPEEDS[Math.max(0, SPEEDS.indexOf(act.speed) - 1)]);
    else if (k === 'BracketRight' || k === 'Equal') setSpeed(SPEEDS[Math.min(3, SPEEDS.indexOf(act.speed) + 1)]);
    else if (k === 'ArrowRight' || k === 'KeyD') seekTo(act.pl.s.tick + 10 * R.TPS);
    else if (k === 'ArrowLeft' || k === 'KeyA') seekTo(act.pl.s.tick - 10 * R.TPS);
    else if (k === 'Home') seekTo(0);
    else if (k === 'Tab') cycleCam(e.shiftKey ? -1 : 1);
    else hit = !/^F\d/.test(k);   // F-клавіші (F5, F12) — браузеру; решта — нікуди: ввід гри вимкнено
    if (hit) { e.preventDefault(); e.stopPropagation(); }
  }
  // пад: A — пауза, B — вийти, LB/RB — камера, ✚ ◀▶ — ±10 с, ▲▼ — швидкість
  function pollPad() {
    let list = [];
    try { list = [...(navigator.getGamepads?.() || [])].filter((p) => p && p.connected !== false); } catch { }
    for (const p of list) {
      const key = p.index + ' ' + p.id, prev = act.pad.get(key);
      const cur = (p.buttons || []).map((x) => !!x && (x.pressed || x.value > 0.5));
      act.pad.set(key, cur);
      if (!prev) continue;   // перший погляд — лише запам'ятати (затиснуте до повтору не тисне)
      const hit = (i) => cur[i] && !prev[i];
      if (hit(1)) { exit(); return; }
      if (hit(0)) togglePause();
      if (hit(4)) cycleCam(-1);
      if (hit(5)) cycleCam(1);
      if (hit(14)) seekTo(act.pl.s.tick - 10 * R.TPS);
      if (hit(15)) seekTo(act.pl.s.tick + 10 * R.TPS);
      if (hit(12)) setSpeed(SPEEDS[Math.min(3, SPEEDS.indexOf(act.speed) + 1)]);
      if (hit(13)) setSpeed(SPEEDS[Math.max(0, SPEEDS.indexOf(act.speed) - 1)]);
    }
  }

  const put = (k, node, v, prop = 'textContent') => { if (act.txt[k] !== v) { act.txt[k] = v; node[prop] = v; } };
  function frame(dt, view = game.view) {
    if (!act) return;
    pollPad();
    if (!act) return;
    const pl = act.pl, rec = act.rec;
    if (!act.paused) {
      act.acc += dt * act.speed;
      let n = Math.min(400, Math.floor(act.acc / R.TICK_MS));
      act.acc -= n * R.TICK_MS;
      const end = lastTick(rec);
      const snd = soundTicks(act.speed, n);   // K42: звучать лише останні тики кадру
      while (n-- > 0 && pl.s.tick < end && pl.step()) {
        const s = pl.s;
        if (act.speed <= 2) view?.events?.(s.ev);
        if (n < snd) opts.audio?.play?.(s.ev, s);   // audio сам мовчить, коли 🔇 (типово)
        if (s.endless && s.ev.some((e) => e.t === 'shift')) view?.setWorld?.(s);
      }
      check();
      const tr = pl.s.train, P = pl.s.params;   // K42: пихкання — як у main.js, на ×4–×8 тихо
      opts.audio?.setTrain?.(act.speed <= 2 && pl.s.status === 'run' && tr && P ? Math.min(1, tr.speed / 1000 / (P.speedMax * R.U / R.TPS)) : 0);
    } else opts.audio?.setTrain?.(0);
    view?.draw?.(pl.s, act.paused ? 0 : Math.min(1, act.acc / R.TICK_MS));
    const s = pl.s, end = endTick();
    put('top', act.top, `📼 Повтор · смуга №${rec.seed} · ⏱ ${fmtT(s.tick)} / ${fmtT(end)} · тик ${s.tick}/${end} · ×${act.speed}${act.paused ? ' · ⏸' : ''}`);
    put('fill', act.fill.style, `${Math.min(100, s.tick / Math.max(1, end) * 100).toFixed(2)}%`, 'width');
    put('play', act.playBtn, act.paused ? '▶' : '⏸');
    put('cam', act.camBtn, act.follow < 0 ? '👥 Гурт' : `🎥 ${act.follow >= rec.local ? '🤖' : ''}${pmark(act.follow)}П${act.follow + 1}`);
    put('camc', act.camBtn.style, act.follow >= 0 ? pcol(act.follow) : '', 'color');
    const v = act.verdict, en = endName({ status: s.status, reason: s.reason });
    put('verd', act.verdEl, !v ? '' : v.ok === null ? `${en} — без звірки (запис неповний) · ▶ — ще раз`
      : v.ok ? `✔ ${en} — повтор зійшовся з партією · ▶ — ще раз` : `⚠ Повтор розійшовся з партією (тик ${v.tick}) — правила змінились після запису? · ▶ — ще раз`);
    put('verdc', act.verdEl, `kol-rpverd${v ? (v.ok === false ? ' bad' : ' ok') : ''}`, 'className');
  }

  // — «📼 Повтори» з меню: свої збережені + вставити код
  function openDialog() {
    css();
    closeDialog();
    const st = loadStore();
    dlg = el('div', 'kol-rpdlg', document.body);
    const box = el('div', '', dlg);
    el('h2', '', box, '📼 Повтори');
    const list = [...st.last.map((x, i) => ({ ...x, t: i === 0 ? 'Остання' : `${i + 1}-а з кінця` })), ...(st.best ? [{ ...st.best, t: '🏆 Найкраща' }] : [])];
    for (const it of list) {
      const b = el('button', '', box); b.type = 'button'; b.textContent = `${it.t}: ${describe(it.info || {})}`;
      b.addEventListener('click', () => watchCode(it.code));
    }
    if (!list.length) el('div', '', box, '<small style="opacity:.75">Своїх ще нема — зіграйте партію, вона запишеться сама.</small>');
    el('div', '', box, '<small style="opacity:.8">Вставити код від друга (📋 на фіналі чи в повторі):</small>');
    const ta = el('textarea', '', box); ta.placeholder = 'K1z.…'; ta.spellcheck = false;
    const err = el('div', 'kol-rperr', box);
    const row = el('div', 'kol-rprow', box); row.style.cssText = 'display:flex;gap:8px;justify-content:center';
    const go = el('button', 'kol-rpmain', row, '▶ Дивитись'); go.type = 'button';
    const no = el('button', '', row, 'Скасувати'); no.type = 'button';
    async function watchCode(code) {
      try { const r = await decode(code); closeDialog(); start(r); } catch (e) { err.textContent = '⚠ ' + (e?.message || e); }
    }
    go.addEventListener('click', () => watchCode(ta.value));
    no.addEventListener('click', () => closeDialog());
    dlg.addEventListener('click', (e) => { if (e.target === dlg) closeDialog(); });
    dlg.addEventListener('keydown', (e) => {
      if (e.key === 'Escape') closeDialog();
      else if (e.key === 'Enter' && e.target === ta && !e.shiftKey) { e.preventDefault(); watchCode(ta.value); }
      e.stopPropagation();   // меню HUD не бачить, що друкуємо
    });
    addEventListener('keydown', dlgGuard, true);
    try { navigator.clipboard?.readText?.().then((t) => { if (dlg && !ta.value && /K1z?\./.test(t || '')) ta.value = t.trim(); }, () => { }); } catch { }
    (list.length ? box.querySelector('button') : ta).focus();
  }
  function dlgGuard(e) { if (dlg && !dlg.contains(e.target)) { if (e.key === 'Escape') closeDialog(); e.stopPropagation(); } }
  function closeDialog() { if (!dlg) return; removeEventListener('keydown', dlgGuard, true); dlg.remove(); dlg = null; opts.input?.swallow?.(); }
  ctl.closeDialog = closeDialog;
  ctl.dialogOpen = () => !!dlg;
  return ctl;
}
