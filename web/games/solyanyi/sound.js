// «Соляний шлях» — звуки столу. Лише процедурний WebAudio (осцилятори + шум + фільтри + огинаючі), жодних файлів. Тихі:
// шелест карти, рип воза, сургуч. Кожен звук ≤ 1,4 с.
//
//   const snd = createSound();
//   snd.play('card'); snd.setOn(true); snd.setVolume(0.4); snd.dispose();
//
// Правила (як у Хуторах — спільного вимикача на сайті нема, кожна гра тримає свій):
//  - типово ВИМКНЕНО; стан — localStorage 'sol.sound' ('1'/'0'), гучність — 'sol.sound.vol' (0..1, типово 0,4);
//  - AudioContext створюється ліниво на першому play() після жесту користувача (navigator.userActivation);
//    вимкнено / нема жесту / прихована вкладка — тиша й жодного AudioContext;
//  - на виході м'який лімітер (tanh): пік ніколи не сягає 0,95.

export const SOUND_NAMES = ['card', 'wipe', 'claim', 'ticket', 'done', 'last', 'longest', 'turn', 'tick'];

const KEY_ON = 'sol.sound', KEY_VOL = 'sol.sound.vol';
const LIMIT = 0.94, EPS = 0.0001;
const lsGet = (k) => { try { return localStorage.getItem(k); } catch { return null; } };
const lsSet = (k, v) => { try { localStorage.setItem(k, v); } catch { /* приватне вікно — не біда */ } };
const clamp01 = (x) => Math.max(0, Math.min(1, Number(x) || 0));

function makeMaster(c, volume) {
  const vol = c.createGain();
  vol.gain.value = volume;
  const pre = c.createGain();
  pre.gain.value = 0.5;
  const shaper = c.createWaveShaper();
  const n = 2049, cv = new Float32Array(n);
  for (let i = 0; i < n; i++) { const s = ((i / (n - 1)) * 2 - 1) * 2; cv[i] = LIMIT * Math.tanh(s / LIMIT); }
  shaper.curve = cv;
  const warm = c.createBiquadFilter();
  warm.type = 'lowpass'; warm.frequency.value = 8500;
  vol.connect(pre); pre.connect(warm); warm.connect(shaper); shaper.connect(c.destination);
  return vol;
}
function env(c, out, t, a, peak, d) {
  const g = c.createGain();
  g.gain.setValueAtTime(EPS, t);
  g.gain.exponentialRampToValueAtTime(peak, t + a);
  g.gain.exponentialRampToValueAtTime(EPS, t + a + d);
  g.connect(out);
  return g;
}
function tone(c, out, t, type, f0, f1, a, peak, d) {
  const o = c.createOscillator();
  o.type = type;
  o.frequency.setValueAtTime(f0, t);
  if (f1 && f1 !== f0) o.frequency.exponentialRampToValueAtTime(f1, t + a + d);
  o.connect(env(c, out, t, a, peak, d));
  o.start(t); o.stop(t + a + d + 0.03);
}
let noiseBuf = null;
function noise(c, out, t, ftype, freq, a, peak, d, q = 0.9) {
  if (!noiseBuf || noiseBuf.sampleRate !== c.sampleRate) {
    noiseBuf = c.createBuffer(1, Math.floor(c.sampleRate * 1.2), c.sampleRate);
    const data = noiseBuf.getChannelData(0);
    for (let i = 0; i < data.length; i++) data[i] = Math.random() * 2 - 1;
  }
  const s = c.createBufferSource();
  s.buffer = noiseBuf;
  const f = c.createBiquadFilter();
  f.type = ftype; f.frequency.value = freq; f.Q.value = q;
  s.connect(f); f.connect(env(c, out, t, a, peak, d));
  s.start(t, Math.random() * 0.4, a + d + 0.05);
}
const RECIPES = {
  card(c, o, t) { noise(c, o, t, 'bandpass', 2600, 0.008, 0.28, 0.09, 1.4); },
  wipe(c, o, t) { for (let i = 0; i < 5; i++) noise(c, o, t + i * 0.06, 'bandpass', 2200 + i * 180, 0.008, 0.22, 0.08, 1.3); },
  claim(c, o, t) { for (let i = 0; i < 3; i++) { tone(c, o, t + i * 0.13, 'triangle', 150 - i * 12, 110, 0.008, 0.3, 0.1); noise(c, o, t + i * 0.13, 'lowpass', 700, 0.004, 0.14, 0.06); } },
  ticket(c, o, t) { noise(c, o, t, 'highpass', 3000, 0.02, 0.16, 0.18, 0.7); tone(c, o, t + 0.1, 'sine', 620, 640, 0.01, 0.08, 0.12); },
  done(c, o, t) { [523, 659, 784].forEach((f, i) => tone(c, o, t + i * 0.09, 'triangle', f, f, 0.01, 0.2, 0.3)); },
  last(c, o, t) { tone(c, o, t, 'sine', 196, 196, 0.03, 0.26, 0.5); tone(c, o, t + 0.26, 'sine', 147, 147, 0.03, 0.26, 0.7); },
  longest(c, o, t) { [392, 494, 587, 784].forEach((f, i) => tone(c, o, t + i * 0.12, 'triangle', f, f, 0.012, 0.22, 0.36)); },
  turn(c, o, t) { tone(c, o, t, 'sine', 740, 740, 0.008, 0.13, 0.18); tone(c, o, t + 0.1, 'sine', 988, 988, 0.008, 0.12, 0.22); },
  tick(c, o, t) { noise(c, o, t, 'bandpass', 1800, 0.002, 0.14, 0.03, 3); },
};

export function createSound() {
  let on = lsGet(KEY_ON) === '1';
  let volume = lsGet(KEY_VOL) != null ? clamp01(lsGet(KEY_VOL)) : 0.4;
  let ctx = null, master = null, dead = false;
  const gesture = () => !navigator.userActivation || navigator.userActivation.hasBeenActive;
  const api = {
    get on() { return on; },
    get volume() { return volume; },
    setOn(v) { on = !!v; lsSet(KEY_ON, on ? '1' : '0'); if (on) api.play('card'); },
    setVolume(v) { volume = clamp01(v); lsSet(KEY_VOL, String(volume)); if (master) master.gain.value = volume; },
    play(name) {
      if (dead || !on || !RECIPES[name] || document.hidden || !gesture()) return;
      const AC = window.AudioContext || window.webkitAudioContext;
      if (!AC) return;
      if (!ctx) { ctx = new AC(); master = makeMaster(ctx, volume); }
      if (ctx.state === 'suspended') ctx.resume().catch(() => {});
      RECIPES[name](ctx, master, ctx.currentTime + 0.005);
    },
    dispose() { dead = true; if (ctx) { ctx.close().catch(() => {}); ctx = null; } },
  };
  return api;
}
