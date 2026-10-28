// «Скарб Полуботка» — звуки розрізу. Лише процедурний WebAudio (осцилятори + шум + фільтри + огинаючі), жодних файлів.
// Тихі, земляні: шурхіт ґрунту, стук кирки, дзенькіт злитків. Кожен звук ≤ 1,2 с (скарб — ≈ 1,8 с).
//
//   const snd = createSound();
//   snd.play('dig'); snd.play('coin', { n: 3 }); snd.setOn(true); snd.setVolume(0.4); snd.dispose();
//
// Правила (як у Хуторах — спільного вимикача на сайті нема, кожна гра тримає свій):
//  - типово ВИМКНЕНО; стан — localStorage 'skb.sound' ('1'/'0'), гучність — 'skb.sound.vol' (0..1, типово 0,4);
//  - AudioContext створюється ліниво на першому play() після жесту користувача (navigator.userActivation);
//    вимкнено / нема жесту / прихована вкладка — тиша й жодного AudioContext;
//  - на виході м'який лімітер (tanh) — пік ніколи не сягає 0,95;
//  - renderOffline(name) — той самий граф в OfflineAudioContext (перевірка піку й NaN без звуку вголос).

export const SOUND_NAMES = ['dig', 'rock', 'map', 'break', 'fix', 'coin', 'gold', 'mole', 'turn', 'tick'];

const KEY_ON = 'skb.sound', KEY_VOL = 'skb.sound.vol';
const LIMIT = 0.94;
const EPS = 0.0001;

const lsGet = (k) => { try { return localStorage.getItem(k); } catch { return null; } };
const lsSet = (k, v) => { try { localStorage.setItem(k, v); } catch { /* приватне вікно — не біда */ } };
const clamp01 = (x) => Math.max(0, Math.min(1, Number(x) || 0));
const rnd = (a, b) => a + Math.random() * (b - a);

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

const N = { C4: 261.63, G4: 392, A4: 440, C5: 523.25, D5: 587.33, E5: 659.25, G5: 783.99, A5: 880, C6: 1046.5, E6: 1318.5, G6: 1568, C7: 2093, D7: 2349.3, E7: 2637 };

const VOICES = {
  // Лопата в ґрунт: глухий «чвак» і шурхіт землі, що осипається.
  dig(c, out, t) {
    noise(c, out, t, { type: 'lowpass', f: 700, f2: 300, q: 0.7, a: 0.01, peak: 0.22, dur: 0.16 });
    thud(c, out, t, 120, 0.28, 0.12);
    for (let i = 0; i < 6; i++) noise(c, out, t + 0.08 + rnd(0, 0.3), { type: 'bandpass', f: rnd(900, 2200), q: 0.8, a: 0.002, peak: rnd(0.04, 0.09), dur: rnd(0.02, 0.05) });
  },
  // Завал: гуркіт каміння — низький шум і кілька важких ударів.
  rock(c, out, t) {
    noise(c, out, t, { type: 'lowpass', f: 400, f2: 150, q: 0.6, a: 0.03, peak: 0.3, dur: 0.55 });
    for (const [dt, pk] of [[0, 0.34], [0.12, 0.26], [0.22, 0.22], [0.36, 0.14]]) thud(c, out, t + dt + rnd(0, 0.02), rnd(70, 110), pk, 0.16);
  },
  // Стара мапа: шелест пергаменту.
  map(c, out, t) {
    noise(c, out, t, { type: 'bandpass', f: 1400, f2: 3800, q: 0.7, a: 0.05, peak: 0.09, dur: 0.3 });
    for (let i = 0; i < 8; i++) noise(c, out, t + rnd(0, 0.35), { type: 'highpass', f: rnd(2500, 4200), q: 0.7, a: 0.001, peak: rnd(0.05, 0.12), dur: rnd(0.012, 0.03) });
  },
  // Зламали: сухий тріск дерева й «дзень» заліза донизу.
  break(c, out, t) {
    noise(c, out, t, { type: 'bandpass', f: 2600, f2: 900, q: 1.4, a: 0.001, peak: 0.24, dur: 0.09 });
    wood(c, out, t + 0.02, 520, 0.24, 0.06);
    tone(c, out, t + 0.05, { f: 880, f2: 420, type: 'triangle', a: 0.002, peak: 0.08, dur: 0.3 });
  },
  // Полагодили: два стуки молотка й м'який дзвіночок.
  fix(c, out, t) {
    wood(c, out, t, 640, 0.22, 0.05);
    wood(c, out, t + 0.14, 700, 0.22, 0.05);
    bell(c, out, t + 0.28, N.E6, 0.08, 0.5);
  },
  // Злитки: n дзеньків (до 8).
  coin(c, out, t, o) {
    const n = Math.max(1, Math.min(8, Math.round(Number(o && o.n) || 1)));
    const sp = Math.min(0.11, 0.7 / n);
    const pent = [N.C7, N.D7, N.E7];
    for (let i = 0; i < n; i++) bell(c, out, t + i * sp + rnd(0, 0.02), pent[Math.floor(Math.random() * pent.length)] * rnd(0.99, 1.01), 0.05, 0.3);
  },
  // Скарб Полуботка: скриня рипить, а тоді мажорна трійка з дзвіночками.
  gold(c, out, t) {
    noise(c, out, t, { type: 'bandpass', f: 500, fMid: 900, f2: 400, q: 6, a: 0.05, peak: 0.08, dur: 0.3 });
    marimba(c, out, t + 0.3, N.C5, 0.24, 0.5);
    marimba(c, out, t + 0.42, N.E5, 0.22, 0.5);
    marimba(c, out, t + 0.54, N.G5, 0.24, 0.6);
    marimba(c, out, t + 0.7, N.C6, 0.24, 0.8);
    for (let i = 0; i < 5; i++) bell(c, out, t + 0.75 + i * 0.09, [N.C7, N.E7, N.D7][i % 3], 0.04, 0.35);
  },
  // Кроти: низьке хитре «бу-бу-бу» донизу.
  mole(c, out, t) {
    for (const [dt, f] of [[0, N.A4 / 2], [0.16, N.G4 / 2], [0.32, N.C4 / 2 * 1.5]]) marimba(c, out, t + dt, f, 0.22, 0.35);
    noise(c, out, t + 0.45, { type: 'lowpass', f: 500, q: 0.5, a: 0.02, peak: 0.08, dur: 0.25 });
  },
  // Твій хід: тихе «тук-тук» угору.
  turn(c, out, t) {
    marimba(c, out, t, N.G5, 0.16, 0.3);
    marimba(c, out, t + 0.12, N.C6, 0.16, 0.4);
  },
  // Останні 5 с таймера: ледь чутне цокання.
  tick(c, out, t) { wood(c, out, t, 1900, 0.05, 0.025); },
};

export function schedule(c, out, name, t, opts) {
  const v = VOICES[name];
  if (!v) return 0;
  v(c, out, t, opts || {});
  return name === 'gold' ? 1.8 : 1.2;
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
