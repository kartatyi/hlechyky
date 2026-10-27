// «Колія» — звук синтезом WebAudio, без файлів. Власник: K4 → K16 (зведення).
// Контракт: createAudio() → { enabled, setEnabled(bool), play(events, state?) (state.ev цього тику), setTrain(speed01), dispose() }
// ЗАВЖДИ заглушено за замовчуванням; вмикає лише кнопка в грі (панель). У перевірках не вмикати.
// AudioContext створюється лише в setEnabled(true) (жест користувача). play() при enabled=false — нічого.
// setTrain(speed01) — ОПЦІЙНО (оркестратор): пихкання паротяга, 0 = стоїть (тиша), 1 = на стелі швидкості.
// state — ОПЦІЙНО: з ним кроки звучать за підлогою (болото чвакає, місток гупає).
//
// K16: кожна подія s.ev має звук (rules, extras — гуси/станція, coop — кооп-фішки K18). Подія → звук: soundOf(e, s).
//   'honk' з p — гудок паротяга (гравець гукнув, coop), без p — гуска. Гучності зведено ярусами (LEVEL, дБ відносно
//   «дії» — удару сокирою): кроки −14, дрібні «взяв/поклав» −8, дії 0, події світу −3…0, важливе +2…+3, кінець +4.
//   Перевірка — tests/koliya-js/audio.test.mjs: заглушка AudioContext міряє «гучність» кожного звуку (RMS × грубе
//   A-зважування, інтеграція ~300 мс) і звіряє з ярусом; і що кожен тип події з rules/extras/coop має звук.
import { G } from './rules.js';
import { themeSfx } from './theme.js';   // K25: у «Чумацькому шляху» воли «му» замість свистка й гудка

const MAX_VOICES = 7;          // одночасних коротких звуків
// мінімальна пауза між однаковими звуками, мс
const PER_TYPE_MS = { chop: 70, rock: 70, step: 120, squelch: 160, plank: 120, pick: 60, drop: 60, craft: 400, water: 150,
  alarm: 1000, whistle: 1500, bump: 250, honk: 700, horn: 500, throw: 120, land: 100, catch: 100, sink: 150, pass: 100,
  stun: 300, scare: 200, goose_lost: 600, upgrade: 800 };

/** Підсилення кожного звуку, дБ (зведення гучностей; міряно в audio.test.mjs) */
export const LEVEL = {
  // підсилення = ціль − «сира» гучність звуку (див. KOL_AUDIO=1 node --test tests/koliya-js/audio.test.mjs)
  step: 10, squelch: 5, plank: -1,                                         // ціль −20…−15: фон
  pick: -6, drop: 0, craft: 1, land: 4, catch: -6, pass: -4, bump: 4,      // ціль −10…−6: дрібне
  chop: 0, rock: -4, rail: -7, water: 1, douse: -8, throw: 2, sink: 2,     // ціль −3…+2: дії
  honk: 3, scare: 1, goose_lost: 5, stun: 1,                              // ціль −3…0: події світу
  whistle: 3, alarm: 2, horn: 7, upgrade: -3,                             // ціль +2…+4: важливе
  won: 1, lost: 9,                                                        // ціль +3…+5: кінець
};

/** Яким звуком озвучити подію (null — не озвучувати) */
export function soundOf(e, s) {
  if (!e) return null;
  if (e.t === 'honk') return e.p !== undefined ? 'horn' : 'honk';
  if (e.t === 'step' && s?.ground && s.w) {
    const g = s.ground[(e.y | 0) * s.w + (e.x | 0)];
    if (g === G.SWAMP) return 'squelch';
    if (g === G.BRIDGE) return 'plank';
  }
  return e.t;
}

// — звуки; кожен отримує (t, A) — A = { tone, noise } з уже врахованим LEVEL — і повертає тривалість (с)
export const SFX = {
  // кроки: трава — глухий шурх; болото — «чвак» (низький шум зі спадом + булька); місток — дерев'яне «гуп»
  step(t, A) { A.noise(t, 0.03, { vol: 0.05, f: 500, q: 1 }); return 0.03; },
  squelch(t, A) { A.noise(t, 0.09, { vol: 0.12, f: 350, q: 2.5, a: 0.01 }); A.tone(t + 0.02, 180, 0.08, { vol: 0.05, to: 90 }); return 0.1; },
  plank(t, A) { A.tone(t, 160, 0.06, { type: 'triangle', vol: 0.12, to: 120 }); A.noise(t, 0.03, { vol: 0.05, f: 900 }); return 0.06; },

  chop(t, A) { A.noise(t, 0.08, { vol: 0.35, f: 1800, q: 0.8 }); A.tone(t, 140, 0.1, { vol: 0.35, to: 70 }); return 0.12; },
  rock(t, A) { A.noise(t, 0.06, { vol: 0.3, f: 3500, q: 2 }); A.tone(t, 900, 0.12, { type: 'triangle', vol: 0.15, to: 700 }); A.tone(t, 1430, 0.08, { type: 'triangle', vol: 0.08 }); return 0.14; },
  pick(t, A) { A.tone(t, 500, 0.06, { type: 'triangle', vol: 0.12, to: 800 }); return 0.07; },
  drop(t, A) { A.tone(t, 300, 0.07, { type: 'triangle', vol: 0.12, to: 180 }); A.noise(t, 0.04, { vol: 0.08, f: 600 }); return 0.08; },
  rail(t, A) { A.tone(t, 1250, 0.45, { type: 'square', vol: 0.05 }); A.tone(t, 1870, 0.35, { vol: 0.1 }); A.tone(t, 2650, 0.25, { vol: 0.06 }); A.noise(t, 0.03, { vol: 0.15, f: 4000 }); return 0.45; },
  craft(t, A) { A.tone(t, 220, 0.05, { type: 'triangle', vol: 0.1 }); A.tone(t + 0.13, 240, 0.05, { type: 'triangle', vol: 0.1 }); return 0.2; },
  water(t, A) { A.noise(t, 0.25, { vol: 0.15, f: 900, q: 3, a: 0.03 }); A.tone(t, 400, 0.15, { vol: 0.06, to: 900 }); return 0.25; },
  douse(t, A) { A.noise(t, 0.6, { vol: 0.3, f: 3000, q: 0.5, kind: 'highpass', a: 0.02 }); return 0.6; },
  whistle(t, A) {
    for (const [f, d] of [[587, 0.35], [740, 0.5]]) {
      const st = t + (f === 740 ? 0.38 : 0);
      A.tone(st, f, d, { type: 'sawtooth', vol: 0.06, a: 0.04 }); A.tone(st, f * 1.5, d, { vol: 0.05, a: 0.04 });
      A.noise(st, d, { vol: 0.04, f: f * 2, q: 4, a: 0.04 });
    }
    return 0.9;
  },
  alarm(t, A) { A.tone(t, 880, 0.12, { type: 'square', vol: 0.07 }); A.tone(t + 0.18, 880, 0.12, { type: 'square', vol: 0.07 }); return 0.3; },
  won(t, A) { [523, 659, 784, 1047].forEach((f, i) => A.tone(t + i * 0.12, f, i === 3 ? 0.5 : 0.14, { type: 'triangle', vol: 0.15 })); return 0.9; },
  lost(t, A) { [392, 349, 311, 233].forEach((f, i) => A.tone(t + i * 0.18, f, i === 3 ? 0.6 : 0.2, { type: 'sawtooth', vol: 0.06 })); return 1.2; },

  // — гуси й станція (K11)
  honk(t, A) {   // гуска: два гнусаві «га-га» з підскоком висоти
    for (const [dt, f] of [[0, 520], [0.17, 470]]) {
      A.tone(t + dt, f, 0.12, { type: 'sawtooth', vol: 0.07, a: 0.01, to: f * 0.8 });
      A.noise(t + dt, 0.1, { vol: 0.05, f: f * 2.2, q: 5, a: 0.01 });
    }
    return 0.3;
  },
  scare(t, A) {   // ляк: тріпотіння крил (серія шумових пухів) + верескливе «га!»
    for (let k = 0; k < 4; k++) A.noise(t + k * 0.06, 0.05, { vol: 0.12, f: 1200, q: 0.7 });
    A.tone(t, 760, 0.14, { type: 'sawtooth', vol: 0.06, to: 980 });
    return 0.28;
  },
  goose_lost(t, A) {   // гуска впустила крадене десь попереду: далеке «га» і стук
    A.tone(t, 500, 0.12, { type: 'sawtooth', vol: 0.04, to: 420 });
    A.tone(t + 0.16, 260, 0.08, { type: 'triangle', vol: 0.1, to: 160 });
    return 0.25;
  },
  upgrade(t, A) {   // покращення: висхідне «дзінь-дзінь-дзінь»
    [784, 988, 1175, 1568].forEach((f, i) => { A.tone(t + i * 0.08, f, 0.3, { vol: 0.09 }); A.tone(t + i * 0.08, f * 2, 0.15, { vol: 0.03 }); });
    return 0.6;
  },

  // — кооп-фішки (K18)
  horn(t, A) {   // гудок паротяга: низький акорд з «видихом»
    A.tone(t, 233, 0.45, { type: 'sawtooth', vol: 0.05, a: 0.03 }); A.tone(t, 294, 0.45, { type: 'sawtooth', vol: 0.045, a: 0.03 });
    A.noise(t, 0.4, { vol: 0.04, f: 700, q: 2, a: 0.03 });
    return 0.45;
  },
  throw(t, A) { A.noise(t, 0.16, { vol: 0.12, f: 1400, q: 1.5, a: 0.04 }); A.tone(t, 300, 0.16, { vol: 0.05, to: 600 }); return 0.16; },
  land(t, A) { A.tone(t, 200, 0.08, { type: 'triangle', vol: 0.14, to: 110 }); A.noise(t, 0.05, { vol: 0.08, f: 500 }); return 0.08; },
  catch(t, A) { A.tone(t, 620, 0.06, { type: 'triangle', vol: 0.12, to: 900 }); A.tone(t + 0.05, 930, 0.08, { vol: 0.06 }); return 0.13; },
  sink(t, A) { A.tone(t, 900, 0.12, { vol: 0.08, to: 250 }); A.noise(t + 0.03, 0.25, { vol: 0.1, f: 1000, q: 2, a: 0.02 }); return 0.28; },
  pass(t, A) { A.tone(t, 440, 0.05, { type: 'triangle', vol: 0.1 }); A.tone(t + 0.06, 660, 0.06, { type: 'triangle', vol: 0.1 }); return 0.12; },
  bump(t, A) { A.tone(t, 120, 0.07, { vol: 0.2, to: 80 }); return 0.07; },
  stun(t, A) {   // «бац» + зірочки
    A.noise(t, 0.05, { vol: 0.25, f: 1500, q: 1 }); A.tone(t, 180, 0.08, { vol: 0.25, to: 90 });
    [1319, 1568, 1760].forEach((f, i) => A.tone(t + 0.08 + i * 0.07, f, 0.12, { vol: 0.04 }));
    return 0.35;
  },
};
const PRIO = { won: 1, lost: 1, whistle: 1, alarm: 1, horn: 1, upgrade: 1 };   // ці не ріжуться лімітом голосів

export function createAudio() {
  let ctx = null, master = null, noiseBuf = null;
  let voices = 0;
  const lastAt = {};
  let puffSpeed = 0, puffTimer = 0;

  function init() {
    if (ctx) return;
    const AC = window.AudioContext || window.webkitAudioContext;
    if (!AC) return;
    ctx = new AC();
    master = ctx.createGain(); master.gain.value = 0.5;
    const comp = ctx.createDynamicsCompressor();
    master.connect(comp); comp.connect(ctx.destination);
    noiseBuf = ctx.createBuffer(1, ctx.sampleRate, ctx.sampleRate);
    const d = noiseBuf.getChannelData(0);
    for (let i = 0; i < d.length; i++) d[i] = Math.random() * 2 - 1;
  }

  // — примітиви
  function env(g, t, a, peak, dur) {
    g.gain.setValueAtTime(0.0001, t);
    g.gain.exponentialRampToValueAtTime(Math.max(0.0002, peak), t + a);
    g.gain.exponentialRampToValueAtTime(0.0001, t + dur);
  }
  function tone(t, freq, dur, { type = 'sine', vol = 0.3, a = 0.005, to = null } = {}, k = 1) {
    const o = ctx.createOscillator(), g = ctx.createGain();
    o.type = type; o.frequency.setValueAtTime(freq, t);
    if (to) o.frequency.exponentialRampToValueAtTime(to, t + dur);
    env(g, t, a, vol * k, dur);
    o.connect(g); g.connect(master); o.start(t); o.stop(t + dur + 0.02);
  }
  function noise(t, dur, { vol = 0.3, f = 1500, q = 1, kind = 'bandpass', a = 0.002 } = {}, k = 1) {
    const s = ctx.createBufferSource(), fl = ctx.createBiquadFilter(), g = ctx.createGain();
    s.buffer = noiseBuf; s.loop = true;
    fl.type = kind; fl.frequency.value = f; fl.Q.value = q;
    env(g, t, a, vol * k, dur);
    s.connect(fl); fl.connect(g); g.connect(master);
    s.start(t, Math.random() * 0.5); s.stop(t + dur + 0.02);
  }
  const amp = (name) => {
    const k = 10 ** ((LEVEL[name] ?? 0) / 20);
    return { tone: (t, f, d, o) => tone(t, f, d, o, k), noise: (t, d, o) => noise(t, d, o, k) };
  };

  function fire(name) {
    const fn = themeSfx(name) ?? SFX[name]; if (!fn) return;   // K25
    const now = ctx.currentTime * 1000;
    const gap = PER_TYPE_MS[name] ?? 50;
    if (lastAt[name] != null && now - lastAt[name] < gap) return;
    if (voices >= MAX_VOICES && !PRIO[name]) return;
    lastAt[name] = now;
    const dur = fn(ctx.currentTime + 0.005, amp(name));
    voices++; setTimeout(() => { voices--; }, dur * 1000 + 30);
  }

  // — пихкання: шумовий «чух» з інтервалом від швидкості
  function puffLoop() {
    if (!ctx || !api.enabled || puffSpeed <= 0.01) { puffTimer = 0; return; }
    noise(ctx.currentTime + 0.005, 0.12, { vol: 0.04 + 0.06 * puffSpeed, f: 700, q: 0.7, a: 0.01 });
    puffTimer = setTimeout(puffLoop, 650 - 450 * puffSpeed);
  }

  const api = {
    enabled: false,
    setEnabled(b) {
      this.enabled = !!b;
      if (this.enabled) { init(); ctx?.resume?.(); if (!puffTimer) puffLoop(); }
      else { ctx?.suspend?.(); clearTimeout(puffTimer); puffTimer = 0; }
    },
    play(events, state) {
      if (!this.enabled || !ctx || !events || !events.length) return;
      const seen = new Set();          // один звук — один раз за тик (4 гравці рубають разом — один клац)
      for (const e of events) {
        const name = soundOf(e, state);
        if (!name || seen.has(name)) continue;
        seen.add(name); fire(name);
      }
    },
    setTrain(speed01) {
      puffSpeed = Math.max(0, Math.min(1, +speed01 || 0));
      if (this.enabled && ctx && !puffTimer && puffSpeed > 0.01) puffLoop();
    },
    dispose() {
      clearTimeout(puffTimer); puffTimer = 0; this.enabled = false;
      try { ctx?.close(); } catch { }
      ctx = null;
    },
  };
  return api;
}
