// «Колія» — звук синтезом WebAudio, без файлів. Власник: K4.
// Контракт: createAudio() → { enabled, setEnabled(bool), play(events) (state.ev цього тику), setTrain(speed01), dispose() }
// ЗАВЖДИ заглушено за замовчуванням; вмикає лише кнопка в грі (панель). У перевірках не вмикати.
// AudioContext створюється лише в setEnabled(true) (жест користувача). play() при enabled=false — нічого.
// setTrain(speed01) — ОПЦІЙНО (оркестратор): пихкання паротяга, 0 = стоїть (тиша), 1 = на стелі швидкості.

const MAX_VOICES = 6;          // одночасних коротких звуків
const PER_TYPE_MS = { chop: 70, rock: 70, step: 120, pick: 60, drop: 60, craft: 400, water: 150, alarm: 1000, whistle: 1500 };

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
    g.gain.exponentialRampToValueAtTime(peak, t + a);
    g.gain.exponentialRampToValueAtTime(0.0001, t + dur);
  }
  function tone(t, freq, dur, { type = 'sine', vol = 0.3, a = 0.005, to = null } = {}) {
    const o = ctx.createOscillator(), g = ctx.createGain();
    o.type = type; o.frequency.setValueAtTime(freq, t);
    if (to) o.frequency.exponentialRampToValueAtTime(to, t + dur);
    env(g, t, a, vol, dur);
    o.connect(g); g.connect(master); o.start(t); o.stop(t + dur + 0.02);
  }
  function noise(t, dur, { vol = 0.3, f = 1500, q = 1, kind = 'bandpass', a = 0.002 } = {}) {
    const s = ctx.createBufferSource(), fl = ctx.createBiquadFilter(), g = ctx.createGain();
    s.buffer = noiseBuf; s.loop = true;
    fl.type = kind; fl.frequency.value = f; fl.Q.value = q;
    env(g, t, a, vol, dur);
    s.connect(fl); fl.connect(g); g.connect(master);
    s.start(t, Math.random() * 0.5); s.stop(t + dur + 0.02);
  }

  // — звуки подій; кожен повертає тривалість (с) для лічильника голосів
  const SFX = {
    chop(t) { noise(t, 0.08, { vol: 0.35, f: 1800, q: 0.8 }); tone(t, 140, 0.1, { vol: 0.35, to: 70 }); return 0.12; },
    rock(t) { noise(t, 0.06, { vol: 0.3, f: 3500, q: 2 }); tone(t, 900, 0.12, { type: 'triangle', vol: 0.15, to: 700 }); tone(t, 1430, 0.08, { type: 'triangle', vol: 0.08 }); return 0.14; },
    pick(t) { tone(t, 500, 0.06, { type: 'triangle', vol: 0.12, to: 800 }); return 0.07; },
    drop(t) { tone(t, 300, 0.07, { type: 'triangle', vol: 0.12, to: 180 }); noise(t, 0.04, { vol: 0.08, f: 600 }); return 0.08; },
    rail(t) { tone(t, 1250, 0.45, { type: 'square', vol: 0.05 }); tone(t, 1870, 0.35, { vol: 0.1 }); tone(t, 2650, 0.25, { vol: 0.06 }); noise(t, 0.03, { vol: 0.15, f: 4000 }); return 0.45; },
    craft(t) { tone(t, 220, 0.05, { type: 'triangle', vol: 0.1 }); tone(t + 0.13, 240, 0.05, { type: 'triangle', vol: 0.1 }); return 0.2; },
    water(t) { noise(t, 0.25, { vol: 0.15, f: 900, q: 3, a: 0.03 }); tone(t, 400, 0.15, { vol: 0.06, to: 900 }); return 0.25; },
    douse(t) { noise(t, 0.6, { vol: 0.3, f: 3000, q: 0.5, kind: 'highpass', a: 0.02 }); return 0.6; },
    whistle(t) {
      for (const [f, d] of [[587, 0.35], [740, 0.5]]) {
        const st = t + (f === 740 ? 0.38 : 0);
        tone(st, f, d, { type: 'sawtooth', vol: 0.06, a: 0.04 }); tone(st, f * 1.5, d, { vol: 0.05, a: 0.04 });
        noise(st, d, { vol: 0.04, f: f * 2, q: 4, a: 0.04 });
      }
      return 0.9;
    },
    alarm(t) { tone(t, 880, 0.12, { type: 'square', vol: 0.07 }); tone(t + 0.18, 880, 0.12, { type: 'square', vol: 0.07 }); return 0.3; },
    won(t) { [523, 659, 784, 1047].forEach((f, i) => tone(t + i * 0.12, f, i === 3 ? 0.5 : 0.14, { type: 'triangle', vol: 0.15 })); return 0.9; },
    lost(t) { [392, 349, 311, 233].forEach((f, i) => tone(t + i * 0.18, f, i === 3 ? 0.6 : 0.2, { type: 'sawtooth', vol: 0.06 })); return 1.2; },
    step(t) { noise(t, 0.03, { vol: 0.04, f: 500, q: 1 }); return 0.03; },
  };
  const PRIO = { won: 1, lost: 1, whistle: 1, alarm: 1 };   // ці не ріжуться лімітом голосів

  function fire(type) {
    const fn = SFX[type]; if (!fn) return;
    const now = ctx.currentTime * 1000;
    const gap = PER_TYPE_MS[type] ?? 50;
    if (lastAt[type] != null && now - lastAt[type] < gap) return;
    if (voices >= MAX_VOICES && !PRIO[type]) return;
    lastAt[type] = now;
    const dur = fn(ctx.currentTime + 0.005);
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
    play(events) {
      if (!this.enabled || !ctx || !events || !events.length) return;
      const seen = new Set();          // один тип — один раз за тик (4 гравці рубають разом — один клац)
      for (const e of events) {
        if (!e || seen.has(e.t)) continue;
        seen.add(e.t); fire(e.t);
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
