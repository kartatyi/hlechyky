// «Сорочинський ярмарок» — звуки ярмаркового столу. Лише процедурний WebAudio (осцилятори + шум + фільтри + огинаючі),
// жодних файлів. Тихі, дерев'яні: стук жетонів-кружалець об стіл, шелест крамниці, дзвіночок гостя, литаври булави.
//
//   const snd = createSound();
//   snd.play('token', { n: 3 }); snd.setOn(true); snd.setVolume(0.4); snd.dispose();
//
// Правила (як у Хуторах і Скарбі):
//  - типово ВИМКНЕНО; стан — localStorage 'sor.sound' ('1'/'0'), гучність — 'sor.sound.vol' (0..1, типово 0,4);
//  - AudioContext створюється ліниво на першому play() після жесту користувача (navigator.userActivation);
//    вимкнено / нема жесту / прихована вкладка — тиша й жодного AudioContext;
//  - на виході м'який лімітер (tanh) — пік ніколи не сягає 0,95;
//  - renderOffline(name) — той самий граф в OfflineAudioContext (перевірка піку й NaN без звуку вголос).
//  Тестувати — лише без звуку (headless Chrome --mute-audio).

export const SOUND_NAMES = ['token', 'buy', 'reserve', 'guest', 'starshyna', 'bulava', 'turn', 'tick'];

const KEY_ON = 'sor.sound', KEY_VOL = 'sor.sound.vol';
const LIMIT = 0.94;
const EPS = 0.0001;

const lsGet = (k) => { try { return localStorage.getItem(k); } catch { return null; } };
const lsSet = (k, v) => { try { localStorage.setItem(k, v); } catch { /* приватне вікно — не біда */ } };
const clamp01 = (x) => Math.max(0, Math.min(1, Number(x) || 0));

// ---- вихідний ланцюг і цеглинки синтезу (як у Хуторах) ----------------------------------------------------------

const curveCache = new WeakMap();
/// Крива лімітера на вході [-2; 2] (перед нею ×0,5): y = L·tanh(s/L) — малі сигнали без змін, великі м'яко під L;
/// усе, що за ±2, — крайні значення кривої (≈ 0,91). Отже |вихід| < LIMIT завжди.
function limiterCurve(c) {
  let cv = curveCache.get(c);
  if (!cv) {
    const n = 4097;
    cv = new Float32Array(n);
    for (let i = 0; i < n; i++) { const s = ((i / (n - 1)) * 2 - 1) * 2; cv[i] = LIMIT * Math.tanh(s / LIMIT); }
    curveCache.set(c, cv);
  }
  return cv;
}

function makeMaster(c, volume) {
  const vol = c.createGain();
  vol.gain.value = volume;
  const pre = c.createGain();
  pre.gain.value = 0.5; // крива лімітера охоплює вхід ±2
  const shaper = c.createWaveShaper();
  shaper.curve = limiterCurve(c);
  shaper.oversample = 'none';
  // тепла заглушка зверху: настолка, не аркада — нічого не пищить вище ~9 кГц
  const warm = c.createBiquadFilter();
  warm.type = 'lowpass'; warm.frequency.value = 9000; warm.Q.value = 0.5;
  vol.connect(warm).connect(pre).connect(shaper).connect(c.destination);
  return { input: vol, vol, nodes: [vol, warm, pre, shaper] };
}

// ---- цеглинки синтезу ---------------------------------------------------------------------------------------------

const noiseCache = new WeakMap();
function noiseBuf(c) {
  let b = noiseCache.get(c);
  if (!b) {
    const len = Math.floor(c.sampleRate * 2);
    b = c.createBuffer(1, len, c.sampleRate);
    const d = b.getChannelData(0);
    for (let i = 0; i < len; i++) d[i] = Math.random() * 2 - 1;
    noiseCache.set(c, b);
  }
  return b;
}

/// Огинаюча: тиша → пік за a → експоненційне затухання за dur.
function envGain(c, out, t, a, peak, dur) {
  const g = c.createGain();
  g.gain.setValueAtTime(EPS, t);
  g.gain.linearRampToValueAtTime(Math.max(peak, EPS * 2), t + a);
  g.gain.exponentialRampToValueAtTime(EPS, t + a + dur);
  g.connect(out);
  return g;
}

/// Тон (синус/трикутник) із можливим спадом висоти f → f2.
function tone(c, out, t, { f, f2 = 0, type = 'sine', a = 0.003, peak = 0.3, dur = 0.2 }) {
  const o = c.createOscillator();
  o.type = type;
  o.frequency.setValueAtTime(f, t);
  if (f2) o.frequency.exponentialRampToValueAtTime(f2, t + a + dur);
  o.connect(envGain(c, out, t, a, peak, dur));
  o.start(t); o.stop(t + a + dur + 0.03);
  return o;
}

/// Шум крізь фільтр (з проходом частоти f → fMid → f2), з огинаючою.
function noise(c, out, t, { type = 'bandpass', f = 2000, fMid = 0, f2 = 0, q = 1, a = 0.003, peak = 0.1, dur = 0.05 }) {
  const s = c.createBufferSource();
  s.buffer = noiseBuf(c);
  const fl = c.createBiquadFilter();
  fl.type = type; fl.Q.value = q;
  fl.frequency.setValueAtTime(f, t);
  const end = t + a + dur;
  if (fMid) { fl.frequency.exponentialRampToValueAtTime(fMid, t + (a + dur) * 0.45); fl.frequency.exponentialRampToValueAtTime(f2 || f, end); }
  else if (f2) fl.frequency.exponentialRampToValueAtTime(f2, end);
  s.connect(fl).connect(envGain(c, out, t, a, peak, dur));
  const off = Math.random() * 1.2;
  s.start(t, off); s.stop(end + 0.03);
  return s;
}

/// Дерев'яний стук: кілька негармонійних мод (як брусок) + коротке «клац» ударника.
function wood(c, out, t, f, peak, decay = 0.12) {
  const modes = [[1, 1, 1], [2.57, 0.42, 0.55], [4.31, 0.18, 0.3]];
  for (const [r, amp, dk] of modes) {
    if (f * r > 12000) continue;
    tone(c, out, t, { f: f * r, a: 0.0015, peak: peak * amp, dur: decay * dk });
  }
  noise(c, out, t, { type: 'bandpass', f: Math.min(6000, f * 5), q: 0.8, a: 0.001, peak: peak * 0.45, dur: 0.012 });
}

/// Глухий «бум» столу під фігурою.
function thud(c, out, t, f, peak, dur = 0.12) {
  tone(c, out, t, { f, f2: f * 0.55, a: 0.002, peak, dur });
}

/// Ксилофон/марімба: дерев'яний брусок, обертони 1 : 4 : 9,2, м'яка паличка.
function marimba(c, out, t, f, peak, dur = 0.45) {
  tone(c, out, t, { f, a: 0.003, peak, dur });
  tone(c, out, t, { f: f * 4, a: 0.002, peak: peak * 0.16, dur: dur * 0.28 });
  if (f * 9.2 < 11000) tone(c, out, t, { f: f * 9.2, a: 0.001, peak: peak * 0.05, dur: dur * 0.1 });
  noise(c, out, t, { type: 'bandpass', f: 1800, q: 0.7, a: 0.001, peak: peak * 0.22, dur: 0.014 });
}

/// М'який дзвіночок: негармонійні обертони, довгий хвіст.
function bell(c, out, t, f, peak, dur = 0.8) {
  const parts = [[1, 1, 1], [2.76, 0.38, 0.6], [5.4, 0.15, 0.32], [8.93, 0.06, 0.18]];
  for (const [r, amp, dk] of parts) {
    if (f * r > 11000) continue;
    tone(c, out, t, { f: f * r, a: 0.002, peak: peak * amp, dur: dur * dk });
  }
}

const N = { C4: 261.63, D4: 293.66, E4: 329.63, G4: 392, A4: 440, C5: 523.25, D5: 587.33, E5: 659.25, G5: 783.99, A5: 880, C6: 1046.5, E6: 1318.5, G6: 1568 };

const VOICES = {
  // Жетони-кружальця лягають на стіл: n глухих дерев'яних «тук» із кроком 70 мс.
  token(c, out, t, o) {
    const n = Math.max(1, Math.min(4, (o && o.n) || 1));
    for (let i = 0; i < n; i++) { wood(c, out, t + i * 0.07, 980 + i * 60, 0.16, 0.06); thud(c, out, t + i * 0.07, 160, 0.08, 0.05); }
  },
  // Купівля: шелест плашки й два ноти марімби вгору.
  buy(c, out, t) {
    noise(c, out, t, { type: 'bandpass', f: 1200, f2: 2600, q: 0.8, a: 0.02, peak: 0.07, dur: 0.16 });
    marimba(c, out, t + 0.1, N.E5, 0.2, 0.35);
    marimba(c, out, t + 0.22, N.A5, 0.2, 0.45);
  },
  // Бронь: «шух» плашки в кишеню й дзенькіт дуката.
  reserve(c, out, t) {
    noise(c, out, t, { type: 'bandpass', f: 2400, f2: 900, q: 0.9, a: 0.01, peak: 0.08, dur: 0.18 });
    bell(c, out, t + 0.16, N.E6, 0.06, 0.4);
  },
  // Гість: дверний дзвіночок, три ноти.
  guest(c, out, t) {
    bell(c, out, t, N.G5, 0.09, 0.6);
    bell(c, out, t + 0.16, N.E5, 0.08, 0.6);
    bell(c, out, t + 0.32, N.C6, 0.09, 0.8);
  },
  // Січова старшина: два удари бубна й низька нота.
  starshyna(c, out, t) {
    thud(c, out, t, 110, 0.26, 0.16);
    noise(c, out, t, { type: 'highpass', f: 3200, q: 0.6, a: 0.002, peak: 0.05, dur: 0.12 });
    thud(c, out, t + 0.18, 110, 0.26, 0.16);
    marimba(c, out, t + 0.36, N.G4, 0.2, 0.5);
  },
  // Гетьманська булава: литаври й урочиста тріада.
  bulava(c, out, t) {
    for (let i = 0; i < 3; i++) thud(c, out, t + i * 0.12, 90, 0.24, 0.18);
    marimba(c, out, t + 0.4, N.C5, 0.2, 0.6);
    marimba(c, out, t + 0.52, N.E5, 0.2, 0.6);
    marimba(c, out, t + 0.64, N.G5, 0.22, 0.7);
    bell(c, out, t + 0.82, N.C6, 0.08, 0.9);
  },
  // Твій хід: тихе «тук-тук» угору.
  turn(c, out, t) {
    marimba(c, out, t, N.G5, 0.15, 0.3);
    marimba(c, out, t + 0.12, N.C6, 0.15, 0.4);
  },
  // Останні 10 с мого строку: ледь чутне цокання.
  tick(c, out, t) { wood(c, out, t, 1900, 0.05, 0.025); },
};

export function schedule(c, out, name, t, opts) {
  const v = VOICES[name];
  if (!v) return 0;
  v(c, out, t, opts || {});
  return name === 'bulava' ? 1.8 : 1.2;
}

// ---- модуль -----------------------------------------------------------------------------------------------------

/**
 * @param {object} [cfg]  лише для перевірок: { _ctx: () => контекст замість AudioContext, _ignoreGesture: true }
 */
export function createSound(cfg = {}) {
  let on = lsGet(KEY_ON) === '1';
  const v0 = lsGet(KEY_VOL);
  let volume = v0 == null ? 0.4 : clamp01(v0);
  let ctx = null, master = null, disposed = false;

  const hidden = () => typeof document !== 'undefined' && document.hidden;
  const onVis = () => { if (ctx && hidden() && ctx.state === 'running' && ctx.suspend) ctx.suspend().catch(() => {}); };
  if (typeof document !== 'undefined') document.addEventListener('visibilitychange', onVis);

  function ensure() {
    if (ctx) return ctx;
    if (!cfg._ignoreGesture) {
      const ua = typeof navigator !== 'undefined' ? navigator.userActivation : null;
      if (ua && !ua.hasBeenActive) return null; // без жесту браузер однаково не дасть звуку — і контексту не плодимо
    }
    try {
      if (cfg._ctx) ctx = cfg._ctx();
      else {
        const AC = typeof window !== 'undefined' && (window.AudioContext || window.webkitAudioContext);
        if (!AC) return null;
        ctx = new AC({ latencyHint: 'interactive' });
      }
    } catch { ctx = null; return null; }
    master = makeMaster(ctx, volume);
    return ctx;
  }

  const api = {
    /// Програти звук. Повертає true, якщо звук поставлено в чергу (false — вимкнено, без жесту, прихована вкладка, невідомий).
    play(name, opts) {
      if (disposed || !on || !VOICES[name] || hidden()) return false;
      const c = ensure();
      if (!c) return false;
      if (c.state === 'suspended' && c.resume) c.resume().catch(() => {});
      try { schedule(c, master.input, name, c.currentTime + 0.015, opts); return true; } catch { return false; }
    },
    setOn(v) {
      on = !!v;
      lsSet(KEY_ON, on ? '1' : '0');
      if (!on && ctx && ctx.state === 'running' && ctx.suspend) ctx.suspend().catch(() => {});
    },
    get on() { return on; },
    setVolume(v) {
      volume = clamp01(v);
      lsSet(KEY_VOL, String(volume));
      if (master && ctx) master.vol.gain.setTargetAtTime(volume, ctx.currentTime, 0.02);
    },
    get volume() { return volume; },
    /// Чи вже створено AudioContext (для перевірок: вимкнено → має лишатися false).
    get hasContext() { return !!ctx; },
    dispose() {
      disposed = true;
      if (typeof document !== 'undefined') document.removeEventListener('visibilitychange', onVis);
      if (ctx && ctx.close) ctx.close().catch(() => {});
      ctx = null; master = null;
    },
  };
  return api;
}

/**
 * Відрендерити звук у OfflineAudioContext (тиша — нічого не грає вголос) і поміряти.
 * @returns {Promise<{name, peak, rms, nan, dur, len}>}  dur — до останнього семпла гучнішого за −60 дБ.
 */
export async function renderOffline(name, opts, { volume = 0.4, sampleRate = 44100, seconds = 2.4 } = {}) {
  const OAC = globalThis.OfflineAudioContext || globalThis.webkitOfflineAudioContext;
  if (!OAC) throw new Error('нема OfflineAudioContext');
  const c = new OAC(1, Math.ceil(sampleRate * seconds), sampleRate);
  const m = makeMaster(c, volume);
  schedule(c, m.input, name, 0.01, opts);
  const buf = await c.startRendering();
  const d = buf.getChannelData(0);
  let peak = 0, sum = 0, nan = 0, last = 0;
  for (let i = 0; i < d.length; i++) {
    const x = d[i];
    if (!Number.isFinite(x)) { nan++; continue; }
    const ax = Math.abs(x);
    if (ax > peak) peak = ax;
    if (ax > 0.001) last = i;
    sum += x * x;
  }
  return { name, peak: +peak.toFixed(4), rms: +Math.sqrt(sum / d.length).toFixed(4), nan, dur: +(last / sampleRate).toFixed(3), len: d.length };
}
