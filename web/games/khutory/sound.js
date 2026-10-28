// «Хутори» — звуки настолки. Лише процедурний WebAudio (осцилятори + шум + фільтри + огинаючі), жодних файлів.
// Теплі, дерев'яні, тихі: це дерев'яна настолка, не аркада. Кожен звук ≤ 1,2 с (перемога — ≈ 1,9 с).
//
//   const snd = createSound();
//   snd.play('dice'); snd.play('coin', { n: 3 }); snd.setOn(true); snd.setVolume(0.4); snd.dispose();
//
// Правила (як у Кістках і Доміно — спільного вимикача/гучності на сайті нема, кожна гра тримає свій):
//  - типово ВИМКНЕНО; стан — localStorage 'kh.sound' ('1'/'0'), гучність — 'kh.sound.vol' (0..1, типово 0,5);
//  - AudioContext створюється ліниво на першому play() після жесту користувача (navigator.userActivation);
//    вимкнено / нема жесту / прихована вкладка — тиша й жодного AudioContext;
//  - на виході м'який лімітер (tanh) — пік ніколи не сягає 0,95, кліпінгу нема;
//  - renderOffline(name) — той самий граф в OfflineAudioContext (для перевірки піку й NaN без звуку вголос).

export const SOUND_NAMES = ['dice', 'place', 'road', 'city', 'coin', 'robber', 'steal', 'trade', 'card', 'award', 'win', 'turn', 'tick'];

const KEY_ON = 'kh.sound', KEY_VOL = 'kh.sound.vol';
const LIMIT = 0.94; // стеля лімітера: |вихід| < LIMIT
const EPS = 0.0001;

const lsGet = (k) => { try { return localStorage.getItem(k); } catch { return null; } };
const lsSet = (k, v) => { try { localStorage.setItem(k, v); } catch { /* приватне вікно — не біда */ } };
const clamp01 = (x) => Math.max(0, Math.min(1, Number(x) || 0));
const rnd = (a, b) => a + Math.random() * (b - a);

// ---- вихідний ланцюг: гучність → м'який лімітер → динаміки -------------------------------------------------------

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

/// Сопілка: м'який трикутник + синус, вібрато, подих (шум біля другого обертону). Тримає ноту dur, відпускає за rel.
function sopilka(c, out, t, f, peak, dur = 0.6, rel = 0.25) {
  const g = c.createGain();
  g.gain.setValueAtTime(EPS, t);
  g.gain.linearRampToValueAtTime(peak, t + 0.06);
  g.gain.setValueAtTime(peak, t + dur);
  g.gain.exponentialRampToValueAtTime(EPS, t + dur + rel);
  g.connect(out);
  const lp = c.createBiquadFilter(); lp.type = 'lowpass'; lp.frequency.value = f * 3; lp.Q.value = 0.4;
  lp.connect(g);
  const lfo = c.createOscillator(); lfo.frequency.value = 5.3;
  const depth = c.createGain(); depth.gain.setValueAtTime(0, t); depth.gain.linearRampToValueAtTime(f * 0.011, t + 0.25);
  lfo.connect(depth);
  const end = t + dur + rel + 0.03;
  for (const [type, amp] of [['triangle', 0.7], ['sine', 0.5]]) {
    const o = c.createOscillator(); o.type = type; o.frequency.setValueAtTime(f, t);
    depth.connect(o.frequency);
    const og = c.createGain(); og.gain.value = amp;
    o.connect(og).connect(lp);
    o.start(t); o.stop(end);
  }
  lfo.start(t); lfo.stop(end);
  noise(c, out, t, { type: 'bandpass', f: f * 2, q: 2, a: 0.04, peak: peak * 0.07, dur: dur + rel * 0.5 });
}

// ---- самі звуки -------------------------------------------------------------------------------------------------

const N = { G4: 392, C5: 523.25, D5: 587.33, E5: 659.25, G5: 783.99, A5: 880, C6: 1046.5, E6: 1318.5, G6: 1568, C7: 2093, D7: 2349.3, E7: 2637 };

const VOICES = {
  // Два кубики котяться дерев'яним столом: шурхіт кочення, по 6 «тук» на кубик із затуханням (кожен трохи інший).
  dice(c, out, t) {
    noise(c, out, t, { type: 'bandpass', f: 2800, f2: 1600, q: 0.9, a: 0.03, peak: 0.07, dur: 0.5 });
    const times = [0, 0.12, 0.215, 0.29, 0.345, 0.39], gains = [0.42, 0.32, 0.24, 0.17, 0.11, 0.07];
    for (const [f0, off] of [[1150, 0], [930, 0.04]]) {
      const stretch = rnd(0.95, 1.12);
      times.forEach((tm, i) => {
        const tt = t + off + tm * stretch + rnd(0, 0.012);
        wood(c, out, tt, f0 * rnd(0.95, 1.05), gains[i], 0.045);
        if (i < 3) thud(c, out, tt, 150, gains[i] * 0.45, 0.07);
      });
    }
  },
  // Дерев'яна фігура лягає на поле: глухий низький стук, трохи «повсті».
  place(c, out, t) {
    wood(c, out, t, 175, 0.42, 0.16);
    thud(c, out, t, 115, 0.4, 0.14);
    noise(c, out, t, { type: 'lowpass', f: 900, q: 0.5, a: 0.002, peak: 0.14, dur: 0.05 });
  },
  // Дошка-дорога: коротший і вищий стук.
  road(c, out, t) {
    wood(c, out, t, 390, 0.38, 0.07);
    thud(c, out, t, 230, 0.18, 0.06);
  },
  // Місто: стук фігури + м'який дзвіночок над ним.
  city(c, out, t) {
    wood(c, out, t, 200, 0.4, 0.15);
    thud(c, out, t, 120, 0.36, 0.13);
    bell(c, out, t + 0.09, N.E6, 0.11, 0.85);
  },
  // Роздача ресурсів: шелест карти + тихе «дзинь»; opts.n — кілька поспіль із розкидом (до 8, усе вкладається в ~0,9 с).
  coin(c, out, t, o) {
    const n = Math.max(1, Math.min(8, Math.round(Number(o && o.n) || 1)));
    const sp = Math.min(0.11, 0.7 / n);
    const pent = [N.C7, N.D7, N.E7];
    for (let i = 0; i < n; i++) {
      const tt = t + i * sp + rnd(0, 0.025);
      noise(c, out, tt, { type: 'highpass', f: 3500, q: 0.6, a: 0.003, peak: 0.1, dur: 0.045 });
      bell(c, out, tt + 0.01, pent[Math.floor(Math.random() * pent.length)] * rnd(0.99, 1.01), 0.055, 0.28);
    }
  },
  // Кармелюк: низьке «вух» (шум, що розгортається вгору й назад) і три важкі кроки.
  robber(c, out, t) {
    noise(c, out, t, { type: 'bandpass', f: 220, fMid: 900, f2: 200, q: 1.2, a: 0.12, peak: 0.3, dur: 0.28 });
    tone(c, out, t, { f: 95, f2: 50, a: 0.08, peak: 0.22, dur: 0.3 });
    for (const [dt, pk] of [[0.42, 0.42], [0.62, 0.36], [0.82, 0.3]]) {
      wood(c, out, t + dt, 92, pk, 0.12);
      thud(c, out, t + dt, 80, pk * 0.8, 0.1);
    }
  },
  // Крадіжка карти: два швидкі шелести.
  steal(c, out, t) {
    noise(c, out, t, { type: 'bandpass', f: 2500, f2: 6000, q: 1.2, a: 0.04, peak: 0.26, dur: 0.13 });
    noise(c, out, t + 0.17, { type: 'bandpass', f: 5500, f2: 2600, q: 1.2, a: 0.03, peak: 0.21, dur: 0.15 });
  },
  // Обмін: два короткі «дзинь», вгору.
  trade(c, out, t) {
    bell(c, out, t, N.G6, 0.12, 0.45);
    bell(c, out, t + 0.13, N.C7, 0.12, 0.55);
  },
  // Купівля грамоти: шурхіт паперу — дрібні тріски й ковзання аркуша.
  card(c, out, t) {
    noise(c, out, t, { type: 'bandpass', f: 1500, f2: 4500, q: 0.7, a: 0.05, peak: 0.11, dur: 0.24 });
    for (let i = 0; i < 10; i++) {
      noise(c, out, t + rnd(0, 0.3), { type: 'highpass', f: rnd(2500, 4500), q: 0.7, a: 0.001, peak: rnd(0.08, 0.18), dur: rnd(0.012, 0.03) });
    }
  },
  // Нагорода (найдовша дорога / найбільше військо): мажорна трійка на ксилофоні.
  award(c, out, t) {
    marimba(c, out, t, N.C5, 0.32, 0.5);
    marimba(c, out, t + 0.1, N.E5, 0.3, 0.5);
    marimba(c, out, t + 0.2, N.G5, 0.32, 0.75);
  },
  // Перемога: весела мелодія на ксилофоні, остання нота — ще й сопілкою (≈ 1,9 с).
  win(c, out, t) {
    const mel = [[N.C5, 0], [N.E5, 0.13], [N.G5, 0.26], [N.C6, 0.39], [N.A5, 0.58], [N.G5, 0.71], [N.E5, 0.84], [N.G5, 0.97]];
    for (const [f, dt] of mel) marimba(c, out, t + dt, f, 0.28, 0.42);
    marimba(c, out, t + 1.16, N.C6, 0.3, 0.7);
    sopilka(c, out, t + 1.16, N.C6, 0.1, 0.42, 0.28);
    tone(c, out, t + 1.16, { f: N.C5 / 2, a: 0.01, peak: 0.12, dur: 0.6 });
  },
  // Твій хід: тихе дерев'яне «тук-тук» угору.
  turn(c, out, t) {
    marimba(c, out, t, N.G5, 0.2, 0.32);
    marimba(c, out, t + 0.12, N.C6, 0.2, 0.42);
  },
  // Останні 5 с таймера: ледь чутне цокання.
  tick(c, out, t) {
    wood(c, out, t, 1900, 0.06, 0.025);
  },
};

/// Розкласти звук name у контекст c, у вузол out, від часу t. Повертає тривалість звуку (с) — для довідки.
export function schedule(c, out, name, t, opts) {
  const v = VOICES[name];
  if (!v) return 0;
  v(c, out, t, opts || {});
  return name === 'win' ? 1.95 : 1.2;
}

// ---- модуль -----------------------------------------------------------------------------------------------------

/**
 * @param {object} [cfg]  лише для перевірок: { _ctx: () => контекст замість AudioContext, _ignoreGesture: true }
 */
export function createSound(cfg = {}) {
  let on = lsGet(KEY_ON) === '1';
  const v0 = lsGet(KEY_VOL);
  let volume = v0 == null ? 0.5 : clamp01(v0);
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
export async function renderOffline(name, opts, { volume = 0.5, sampleRate = 44100, seconds = 2.4 } = {}) {
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
