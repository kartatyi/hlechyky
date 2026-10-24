// Звук фури синтезом WebAudio, без файлів: дизель (дві пилки + шум, частота від обертів, тембр від газу),
// «пшш» пневматики, коли відпускаєш гальмо, сигнал і «дзень» розбитого вантажу. AudioContext створюємо лише
// з натиску (вимога браузерів). 🔇 пам'ятаємо в localStorage.

const MUTE = 'fura.mute';

export class Sound {
  constructor() {
    this.ctx = null;
    try { this.muted = localStorage.getItem(MUTE) === '1'; } catch { this.muted = false; }
    this.braking = 0;
  }

  /** Викликати з обробника натиску. */
  start() {
    if (this.ctx) { if (this.ctx.state === 'suspended') this.ctx.resume(); return; }
    const AC = window.AudioContext || window.webkitAudioContext;
    if (!AC) return;
    const ctx = this.ctx = new AC();
    this.master = ctx.createGain();
    this.master.gain.value = this.muted ? 0 : 0.5;
    this.master.connect(ctx.destination);

    // двигун
    this.lp = ctx.createBiquadFilter();
    this.lp.type = 'lowpass'; this.lp.frequency.value = 500; this.lp.Q.value = 2;
    this.eng = ctx.createGain(); this.eng.gain.value = 0.0;
    this.lp.connect(this.eng); this.eng.connect(this.master);
    this.o1 = ctx.createOscillator(); this.o1.type = 'sawtooth';
    this.o2 = ctx.createOscillator(); this.o2.type = 'square';
    const g1 = ctx.createGain(); g1.gain.value = 0.55;
    const g2 = ctx.createGain(); g2.gain.value = 0.25;
    this.o1.connect(g1); g1.connect(this.lp);
    this.o2.connect(g2); g2.connect(this.lp);
    this.o1.start(); this.o2.start();
    this.noise = this.makeNoise();
    const nf = ctx.createBiquadFilter(); nf.type = 'bandpass'; nf.frequency.value = 180; nf.Q.value = 0.8;
    this.ng = ctx.createGain(); this.ng.gain.value = 0;
    const ns = ctx.createBufferSource(); ns.buffer = this.noise; ns.loop = true;
    ns.connect(nf); nf.connect(this.ng); this.ng.connect(this.master); ns.start();

    // сигнал
    this.hornG = ctx.createGain(); this.hornG.gain.value = 0;
    const hl = ctx.createBiquadFilter(); hl.type = 'lowpass'; hl.frequency.value = 1400;
    for (const f of [185, 233]) { const o = ctx.createOscillator(); o.type = 'sawtooth'; o.frequency.value = f; o.connect(hl); o.start(); }
    hl.connect(this.hornG); this.hornG.connect(this.master);
  }

  makeNoise() {
    const ctx = this.ctx, n = ctx.sampleRate * 2;
    const b = ctx.createBuffer(1, n, ctx.sampleRate), d = b.getChannelData(0);
    for (let i = 0; i < n; i++) d[i] = Math.random() * 2 - 1;
    return b;
  }

  setMuted(m) {
    this.muted = m;
    try { localStorage.setItem(MUTE, m ? '1' : '0'); } catch { /* пусто */ }
    if (this.master) this.master.gain.setTargetAtTime(m ? 0 : 0.5, this.ctx.currentTime, 0.05);
  }

  /** Щокадру: rpm, газ 0…1, гальмо 0…1, сигнал. */
  update(rpm, throttle, brake, horn) {
    if (!this.ctx) return;
    const t = this.ctx.currentTime;
    const f = (rpm / 60) * 3;                     // шість циліндрів: три спалахи на оберт
    this.o1.frequency.setTargetAtTime(f, t, 0.05);
    this.o2.frequency.setTargetAtTime(f * 0.5, t, 0.05);
    this.lp.frequency.setTargetAtTime(260 + throttle * 900 + rpm * 0.12, t, 0.08);
    this.eng.gain.setTargetAtTime(0.16 + throttle * 0.16, t, 0.08);
    this.ng.gain.setTargetAtTime(0.02 + throttle * 0.07, t, 0.1);
    this.hornG.gain.setTargetAtTime(horn ? 0.22 : 0, t, 0.02);
    // «пшш» — коли довго тримав гальмо й відпустив
    if (brake > 0.3) this.braking += 1; else { if (this.braking > 40) this.hiss(); this.braking = 0; }
  }

  burst(dur, type, freq, gain, q) {
    if (!this.ctx) return;
    const ctx = this.ctx, t = ctx.currentTime;
    const s = ctx.createBufferSource(); s.buffer = this.noise;
    const f = ctx.createBiquadFilter(); f.type = type; f.frequency.value = freq; f.Q.value = q || 1;
    const g = ctx.createGain(); g.gain.setValueAtTime(gain, t); g.gain.exponentialRampToValueAtTime(0.001, t + dur);
    s.connect(f); f.connect(g); g.connect(this.master);
    s.start(t, Math.random()); s.stop(t + dur);
  }

  hiss() { this.burst(0.6, 'highpass', 3000, 0.18); }

  /** Удар по вантажу: сила 0…1 — гучність і скільки «скалок». */
  clink(power) {
    if (!this.ctx) return;
    const ctx = this.ctx, t = ctx.currentTime;
    const n = 1 + Math.round(power * 3);
    for (let i = 0; i < n; i++) {
      const o = ctx.createOscillator(); o.type = 'triangle';
      o.frequency.value = 1400 + Math.random() * 1600;
      const g = ctx.createGain();
      const at = t + i * 0.045;
      g.gain.setValueAtTime(0.0001, at); g.gain.exponentialRampToValueAtTime(0.12 + power * 0.15, at + 0.005);
      g.gain.exponentialRampToValueAtTime(0.0001, at + 0.25);
      o.connect(g); g.connect(this.master); o.start(at); o.stop(at + 0.3);
    }
    this.burst(0.12, 'bandpass', 2500, 0.1 + power * 0.2, 2);
  }

  thump() { this.burst(0.25, 'lowpass', 220, 0.5); }

  stop() { if (this.ctx) { try { this.ctx.close(); } catch { /* пусто */ } this.ctx = null; } }
}
