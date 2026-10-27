// K66: звук без кліпінгу — модель міксу в Node (без справжнього AudioContext і без звуку).
// Заглушка будує граф вузлів так, як його будують audio.js і music.js, і рахує пікову суму амплітуд за вікно 50 мс:
//  кожне джерело — амплітуда 1 (осцилятор) або шум після фільтра (√смуги × пік-фактор), усі у фазі (найгірше);
//  підсилення — максимум автоматизації gain у вікні (+ глибина LFO, що в неї під'єднано);
//  компресори — статична крива WebAudio (поріг/коліно/ratio) + автоматичний makeup gain зі специфікації ((1/крива(0 дБ))^0,6).
// Атаку компресора (3 мс) не моделюємо — тому й запас −3 дБ.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createAudio } from '../../web/games/koliya/audio.js';
import { createMusic } from '../../web/games/koliya/music.js';
import { newGame, defaultParams } from '../../web/games/koliya/rules.js';

const db = (a) => 20 * Math.log10(Math.max(1e-9, a));

function evalP(p, x) {
  let v = p.v0, t = 0, tgt = null;
  const at = (time) => (tgt ? tgt.v + (v - tgt.v) * Math.exp(-(time - tgt.t) / tgt.tc) : v);
  for (const e of p.ev) {
    if (e.k === 'lin' || e.k === 'exp') {
      const v0 = at(t);
      if (x < e.t) {
        if (x < t) return v0;
        const u = (x - t) / Math.max(1e-9, e.t - t);
        return e.k === 'exp' && v0 * e.v > 0 ? v0 * (e.v / v0) ** u : v0 + (e.v - v0) * u;
      }
      v = e.v; t = e.t; tgt = null;
    } else {
      if (e.t > x) break;
      if (e.k === 'set') { v = e.v; tgt = null; } else { v = at(e.t); tgt = { v: e.v, t: e.t, tc: e.tc }; }
      t = e.t;
    }
  }
  return at(x);
}

/** Заглушка WebAudio з графом; c.ctx — останній створений контекст */
function stub() {
  const c = { ctx: null };
  const param = (ctx, v0) => {
    const p = { v0, ev: [], inputs: [], isParam: true };
    const push = (e) => { p.ev.push(e); p.ev.sort((a, b) => a.t - b.t); return p; };
    Object.defineProperty(p, 'value', { get: () => evalP(p, ctx.currentTime), set: (x) => { p.v0 = x; } });
    Object.assign(p, {
      setValueAtTime: (v, t) => push({ k: 'set', v, t }), linearRampToValueAtTime: (v, t) => push({ k: 'lin', v, t }),
      exponentialRampToValueAtTime: (v, t) => push({ k: 'exp', v, t }), setTargetAtTime: (v, t, tc) => push({ k: 'tgt', v, t, tc }),
      cancelScheduledValues: (t) => { p.ev = p.ev.filter((e) => e.t < t); return p; },
    });
    return p;
  };
  const node = (ctx, kind, defs = {}) => {
    const n = { kind, inputs: [], t0: null, t1: Infinity, type: 'sine',
      connect(d) { d.inputs.push(n); return d; }, disconnect() { }, start(t = 0) { n.t0 = t; }, stop(t = 0) { n.t1 = Math.min(n.t1, t); } };
    for (const [k, v] of Object.entries(defs)) n[k] = param(ctx, v);
    return new Proxy(n, { get(o, k) { if (!(k in o) && typeof k === 'string') o[k] = param(ctx, 0); return o[k]; } });
  };
  class Ctx {
    constructor() { c.ctx = this; this.currentTime = 0; this.sampleRate = 8000; this.state = 'running'; this.destination = node(this, 'dest'); }
    createGain() { return node(this, 'gain', { gain: 1 }); }
    createOscillator() { return node(this, 'osc', { frequency: 440, detune: 0 }); }
    createBufferSource() { return node(this, 'src'); }
    createBiquadFilter() { const n = node(this, 'filter', { frequency: 350, Q: 1, gain: 0 }); n.type = 'lowpass'; return n; }
    createDynamicsCompressor() { return node(this, 'comp', { threshold: -24, knee: 30, ratio: 12, attack: 0.003, release: 0.25 }); }
    createBuffer(ch, n) { const d = new Float32Array(n); return { getChannelData: () => d }; }
    resume() { } suspend() { } close() { }
  }
  globalThis.AudioContext = Ctx;
  return c;
}

/** Статична крива компресора WebAudio + makeup gain (специфікація: (1/крива(1.0))^0,6) */
function compress(cn, a) {
  const T = cn.threshold.v0, K = cn.knee.v0, R = cn.ratio.v0;
  const curve = (x) => (x <= T - K / 2 ? x : x >= T + K / 2 ? T + (x - T) / R : x + (1 / R - 1) * (x - T + K / 2) ** 2 / (2 * K));
  const makeup = -curve(0) * 0.6;
  return a <= 0 ? 0 : 10 ** ((curve(db(a)) + makeup) / 20);
}
/** Пік шуму після фільтра (білий шум ±1, гребінь ~3×RMS) */
function noiseK(f) {
  const fr = f.frequency.v0, q = f.Q.v0 || 1;
  const bw = f.type === 'highpass' ? 22050 - fr : f.type === 'lowpass' ? fr : fr / q;
  return Math.min(1, 1.73 * Math.sqrt(Math.max(0, bw) / 22050));
}
/** Миттєва амплітуда виходу вузла n у момент t (огинаючі всіх джерел, усі у фазі) */
function amp(n, t, memo) {
  if (memo.has(n)) return memo.get(n);
  let r = 0;
  if (n.kind === 'osc' || n.kind === 'src') r = n.t0 != null && n.t0 <= t && n.t1 > t ? 1 : 0;
  else {
    const ins = n.kind === 'filter'
      ? n.inputs.reduce((s, i) => s + amp(i, t, memo) * (i.kind === 'src' ? noiseK(n) : 1), 0)
      : n.inputs.reduce((s, i) => s + amp(i, t, memo), 0);
    if (n.kind === 'gain') r = ins > 0 ? Math.abs(evalP(n.gain, t) + n.gain.inputs.reduce((s, i) => s + amp(i, t, memo), 0)) * ins : 0;
    else if (n.kind === 'comp') r = compress(n, ins);
    else r = ins;
  }
  memo.set(n, r);
  return r;
}
/** Пікова сума амплітуд на [from, to): вікна по 50 мс, у кожному — максимум миттєвої суми (крок 1 мс); node — що міряти */
function peak(ctx, from, to, node = ctx.destination) {
  let m = 0;
  for (let a = from; a < to; a += 0.05) for (let t = a; t < a + 0.05; t += 0.001) m = Math.max(m, amp(node, t, new Map()));
  return m;
}

/** Світ на піку небезпеки: пожежа, повний хід, біом із жабами/водою */
function hotState() {
  const s = newGame(7, defaultParams(), 4);
  s.train.fire = 1; s.train.speed = 1e12;
  return s;
}
/** Зібрати сцену: музика/тло прогріті 12 с гри (небезпека й шари тла вийшли на рівень) */
function scene({ music = false, amb = false, sfx = false }) {
  const c = stub();
  const audio = createAudio();
  const mu = createMusic(audio, { storage: null, test: true });
  const s = hotState();
  mu.update(s);
  for (const k of ['music', 'amb', 'sfx']) mu.setVolume(k, 1);   // усі повзунки на максимум — найгірше
  if (music) mu.setMusic(true);
  if (amb) mu.setAmb(true);
  if (sfx) { audio.setEnabled(true); audio.setTrain(1); }
  if (!c.ctx) audio.shared();
  const ctx = c.ctx;
  for (let i = 0; i < 135; i++) { ctx.currentTime += 0.09; mu._tick(); }
  return { audio, mu, ctx, s, done() { mu.dispose(); audio.dispose(); delete globalThis.AudioContext; } };
}
// найгірший тик: 4 гравці рубають/б'ють + рейка + крафт + тривога + свисток + гудок + кроки + штовх
const WORST = [{ t: 'chop', p: 0 }, { t: 'chop', p: 1 }, { t: 'rock', p: 2 }, { t: 'stun', p: 3 }, { t: 'rail' }, { t: 'craft' },
  { t: 'alarm' }, { t: 'whistle' }, { t: 'honk', p: 0 }, { t: 'step', p: 0 }, { t: 'bump', p: 1 }, { t: 'upgrade' }];

test('K66: найгірший тик (ефекти + музика на піку небезпеки + тло) не кліпає: пік ≤ −3 дБ', () => {
  const sc = scene({ music: true, amb: true, sfx: true });
  const T = sc.ctx.currentTime;
  sc.audio.play(WORST);
  sc.ctx.currentTime += 0.016; sc.audio.play([{ t: 'chop', p: 2 }, { t: 'rock', p: 3 }, { t: 'water' }, { t: 'douse' }]);
  sc.ctx.currentTime += 0.017; sc.audio.play([{ t: 'stun', p: 0 }, { t: 'throw', p: 1 }, { t: 'sink' }, { t: 'scare' }]);
  sc.ctx.currentTime = T;
  assert.ok(sc.mu.debug().danger > 0.9, `небезпека на піку (${sc.mu.debug().danger})`);
  let lim = sc.ctx.destination;
  while (lim && lim.kind !== 'comp') lim = lim.inputs[0];   // майстер-лімітер (останній компресор перед виходом)
  const pre = peak(sc.ctx, T - 0.1, T + 1.5, { kind: 'sum', inputs: lim.inputs });
  const pk = peak(sc.ctx, T - 0.1, T + 1.5);
  if (process.env.KOL_AUDIO) console.log(`вхід лімітера ${db(pre).toFixed(1)} дБ, вихід ${db(pk).toFixed(1)} дБ`);
  assert.ok(db(pk) <= -3, `пік виходу ${db(pk).toFixed(2)} дБ > −3 дБ (вхід лімітера ${db(pre).toFixed(1)} дБ)`);
  sc.done();
});

test('K66: кроки не губляться під музикою на піку (різниця ≤ 20 дБ)', () => {
  const m = scene({ music: true });
  const T = m.ctx.currentTime, pm = peak(m.ctx, T, T + 3);
  m.done();
  const st = scene({ sfx: true });
  st.audio.setTrain(0);   // без пихкання — лише крок
  st.ctx.currentTime += 1;
  const T2 = st.ctx.currentTime;
  st.audio.play([{ t: 'step', p: 0 }]);
  const ps = peak(st.ctx, T2, T2 + 0.1);
  st.done();
  if (process.env.KOL_AUDIO) console.log(`музика ${db(pm).toFixed(1)} дБ, крок ${db(ps).toFixed(1)} дБ`);
  assert.ok(pm > 0.01 && ps > 0, 'обидва звучать');
  assert.ok(db(pm) - db(ps) <= 20, `крок тихіший за музику на ${(db(pm) - db(ps)).toFixed(1)} дБ`);
});

test('K66: майстер-лімітер з makeup gain не кліпає навіть при перевантаженні входу на +6 дБ', () => {
  const sc = scene({ sfx: true });
  let lim = sc.ctx.destination;
  while (lim && lim.kind !== 'comp') lim = lim.inputs[0];
  const out = (inDb) => db(compress(lim, 10 ** (inDb / 20)));
  if (process.env.KOL_AUDIO) console.log(`лімітер: −6 → ${out(-6).toFixed(1)}, 0 → ${out(0).toFixed(1)}, +6 → ${out(6).toFixed(1)} дБ`);
  assert.ok(out(6) < 0, `+6 дБ на вході → ${out(6).toFixed(2)} дБ`);
  sc.done();
});
