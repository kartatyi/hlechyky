// Звук фури (WebAudio). Двигун — записи справжнього дизеля (engine.js, петлі за обертами), вантажаться ліниво після
// старту гри; поки їх нема чи не вдалося — синтез (дві пилки + шум, частота від обертів, тембр від газу). Поверх —
// турбіна (свист від тиску наддуву), гальмо двигуном, шум дороги від швидкості. З кабіни все глухіше (фільтр), зовні —
// відкритіше. Ефекти: «пшш» пневматики, сигнал, зчеп, «дзень» вантажу — записами, де є, інакше синтезом.
// Шини: двигун / ефекти / рація → загальна; рівні — з мікшера (mixer.js), 🔇 — у localStorage, як було.
// Гараж (0.0.6): ріжок із Лавки «🚚 Фура» (setHorn) — синтез, як гімни, без чужих прав; голос тягача (setVoiceOf:
// «Віл» нижче, «Ластівка» вище); «Тихий салон» — двигун у кабіні тихіше (truck.P.cabVol).
// AudioContext — лише з натиску (вимога браузерів); вкладка у фоні — контекст на паузі.

import { SampleEngine, loadBank } from './engine.js';
import { loadVol, gainOf } from './mixer.js';

const MUTE = 'fura.mute';
const BASE = 0.5;                  // загальна до мікшера — щоб «80» звучало як раніше

// Ріжки: tones — тон, поки тримаєш ([Гц, хвиля, гучність]); pulse — «га-га» ([звучить, тиша], с) з bend (кінець/початок
// висоти); seq — ноти MIDI групами (акорд), step — с на долю, hold — останній акорд тягнеться, loop — наспів по колу.
const midi = (n) => 440 * Math.pow(2, (n - 69) / 12);
const HORNS = {
  hornpneumo: { tones: [[220, 'sawtooth', 0.5], [277.2, 'sawtooth', 0.42], [110, 'square', 0.12]], lp: 1700, attack: 0.04 },
  horngoose: { tones: [[311, 'square', 0.32], [466, 'sawtooth', 0.16]], lp: 1500, attack: 0.015, pulse: [0.17, 0.09], bend: 0.82 },
  horntrembita: { tones: [[116.5, 'sawtooth', 0.42], [233, 'sawtooth', 0.36], [349.2, 'triangle', 0.24]], lp: 2100, attack: 0.3, vib: [5.2, 0.008] },
  hornthree: { seq: [[[63], 1], [[67], 1], [[70], 1], [[63, 67, 70], 2]], step: 0.15, type: 'sawtooth', lp: 2600, hold: true },
  horngold: { seq: [[[72], 1], [[76], 1], [[79], 1], [[72, 76, 79, 84], 2]], step: 0.11, type: 'square', lp: 3400, hold: true },
  // «Ой на горі два дубки» — народна; наспів на слух, не нотами з видання
  horndubky: { seq: [[[62], 1], [[65], 1], [[69], 2], [[69], 1], [[67], 1], [[65], 1], [[64], 1], [[62], 2], [[64], 1], [[65], 1], [[67], 2], [[64], 1], [[62], 3], [[0], 1]],
    step: 0.2, type: 'sawtooth', lp: 1900, loop: true },
};

export class Sound {
  /** truck — для швидкості й передачі; view() — 'cabin' | 'chase' | 'far'. Обидва необов'язкові. */
  constructor({ truck, view } = {}) {
    this.ctx = null;
    try { this.muted = localStorage.getItem(MUTE) === '1'; } catch { this.muted = false; }
    this.braking = 0;
    this.truck = truck || null;
    this.view = view || (() => 'chase');
    this.vol = loadVol();
    this.load = 0; this.boost = 0; this.jake = 0; this.gear = null;
    this.pitch = 1; this.hornKind = null; this.hv = null;
    this.onVis = () => {
      if (!this.ctx) return;
      if (document.hidden) this.ctx.suspend(); else this.ctx.resume();
    };
    document.addEventListener('visibilitychange', this.onVis);
  }

  /** Викликати з обробника натиску. */
  start() {
    if (this.ctx) { if (this.ctx.state === 'suspended' && !document.hidden) this.ctx.resume(); return; }
    const AC = window.AudioContext || window.webkitAudioContext;
    if (!AC) return;
    this.build(new AC());
    // записи — коли гра вже стартувала й намалювала перший кадр. Встигли за кілька секунд — двигун «заводиться»
    // записом старту; ні — синтез, а записи перейдуть плавно, коли докачаються.
    this.loadTimer = setTimeout(() => this.loadSamples(), 500);
    this.synthTimer = setTimeout(() => { if (!this.samples && this.ctx) this.synth.gain.setTargetAtTime(1, this.ctx.currentTime, 0.3); }, 4000);
  }

  /** Граф. Окремо від start(), щоб перевіряти беззвучно на OfflineAudioContext. */
  build(ctx) {
    this.ctx = ctx;
    this.noise = this.makeNoise();
    this.master = ctx.createGain();
    this.master.gain.value = this.muted ? 0 : BASE * gainOf(this.vol.master);
    this.master.connect(ctx.destination);
    this.bus = {};
    for (const k of ['engine', 'fx', 'voice']) {
      const g = this.bus[k] = ctx.createGain();
      g.gain.value = gainOf(this.vol[k]);
      g.connect(this.master);
    }

    // двигун: вхід (синтез чи записи) → «кабіна» (фільтр) → шина
    this.engIn = ctx.createGain();
    this.cabLp = ctx.createBiquadFilter(); this.cabLp.type = 'lowpass'; this.cabLp.frequency.value = 9000; this.cabLp.Q.value = 0.5;
    this.cabLow = ctx.createBiquadFilter(); this.cabLow.type = 'lowshelf'; this.cabLow.frequency.value = 160; this.cabLow.gain.value = 0;
    this.engIn.connect(this.cabLp); this.cabLp.connect(this.cabLow); this.cabLow.connect(this.bus.engine);

    // синтез — запаска (тиша, доки не ясно, чи будуть записи)
    this.synth = ctx.createGain(); this.synth.gain.value = 0; this.synth.connect(this.engIn);
    this.lp = ctx.createBiquadFilter();
    this.lp.type = 'lowpass'; this.lp.frequency.value = 500; this.lp.Q.value = 2;
    this.eng = ctx.createGain(); this.eng.gain.value = 0.0;
    this.lp.connect(this.eng); this.eng.connect(this.synth);
    this.o1 = ctx.createOscillator(); this.o1.type = 'sawtooth';
    this.o2 = ctx.createOscillator(); this.o2.type = 'square';
    const g1 = ctx.createGain(); g1.gain.value = 0.55;
    const g2 = ctx.createGain(); g2.gain.value = 0.25;
    this.o1.connect(g1); g1.connect(this.lp);
    this.o2.connect(g2); g2.connect(this.lp);
    this.o1.start(); this.o2.start();
    const nf = ctx.createBiquadFilter(); nf.type = 'bandpass'; nf.frequency.value = 180; nf.Q.value = 0.8;
    this.ng = ctx.createGain(); this.ng.gain.value = 0;
    const ns = this.loopNoise(); ns.connect(nf); nf.connect(this.ng); this.ng.connect(this.synth);

    // турбіна: свист (синус + вузька смуга шуму), висота й гучність — від наддуву
    this.turbo = ctx.createGain(); this.turbo.gain.value = 0; this.turbo.connect(this.engIn);
    this.tOsc = ctx.createOscillator(); this.tOsc.type = 'sine'; this.tOsc.frequency.value = 2500;
    const tg = ctx.createGain(); tg.gain.value = 0.35; this.tOsc.connect(tg); tg.connect(this.turbo); this.tOsc.start();
    this.tBp = ctx.createBiquadFilter(); this.tBp.type = 'bandpass'; this.tBp.frequency.value = 2500; this.tBp.Q.value = 9;
    const tn = this.loopNoise(); tn.connect(this.tBp); this.tBp.connect(this.turbo);

    // дорога: низький гул шин від швидкості (у шину двигуна — повзунок «двигун і дорога»)
    this.road = ctx.createGain(); this.road.gain.value = 0;
    this.roadLp = ctx.createBiquadFilter(); this.roadLp.type = 'lowpass'; this.roadLp.frequency.value = 300;
    const rn = this.loopNoise(); rn.connect(this.roadLp); this.roadLp.connect(this.road); this.road.connect(this.cabLp);

    // сигнал: синтез, доки нема запису
    this.hornG = ctx.createGain(); this.hornG.gain.value = 0;
    const hl = ctx.createBiquadFilter(); hl.type = 'lowpass'; hl.frequency.value = 1400;
    for (const f of [185, 233]) { const o = ctx.createOscillator(); o.type = 'sawtooth'; o.frequency.value = f; o.connect(hl); o.start(); }
    hl.connect(this.hornG); this.hornG.connect(this.bus.fx);
  }

  async loadSamples() {
    if (!this.ctx || this.samples || this.loading) return;
    this.loading = true;
    try {
      const bank = await loadBank(this.ctx);
      if (!this.ctx) return;
      this.bank = bank;
      this.samples = new SampleEngine(this.ctx, this.engIn, bank);
      const t = this.ctx.currentTime;
      if (this.synth.gain.value < 0.01 && this.play('start', 1, this.bus.engine)) this.samples.fade(1, 0.25, t + 1.5);   // крутить стартер — і схопив
      else this.samples.fade(1, 0.4);
      this.synth.gain.cancelScheduledValues(t);
      this.synth.gain.setTargetAtTime(0, t, 0.4);
      setTimeout(() => { try { this.o1.stop(); this.o2.stop(); } catch { /* уже */ } }, 3000);
      if (bank.fx && bank.fx.horn) this.hornSample(bank.fx.horn);
    } catch (e) {
      console.warn('fura: записи двигуна не завантажились — лишаю синтез', e);
    } finally { this.loading = false; }
  }

  /** Сигнал записом: петля середини гудка, поки тримаєш, і хвіст, коли відпустив. */
  hornSample(it) {
    const ctx = this.ctx;
    this.hornFx = it;
    this.hornS = ctx.createGain(); this.hornS.gain.value = 0; this.hornS.connect(this.bus.fx);
    const s = ctx.createBufferSource(); s.buffer = it.buf; s.loop = true; s.loopStart = it.loopStart; s.loopEnd = it.loopEnd;
    s.connect(this.hornS); s.start(0, it.loopStart);
  }

  makeNoise() {
    const ctx = this.ctx, n = ctx.sampleRate * 2;
    const b = ctx.createBuffer(1, n, ctx.sampleRate), d = b.getChannelData(0);
    for (let i = 0; i < n; i++) d[i] = Math.random() * 2 - 1;
    return b;
  }

  loopNoise() {
    const s = this.ctx.createBufferSource(); s.buffer = this.noise; s.loop = true;
    s.start(0, Math.random() * 1.5);
    return s;
  }

  setMuted(m) {
    this.muted = m;
    try { localStorage.setItem(MUTE, m ? '1' : '0'); } catch { /* пусто */ }
    if (this.master) this.master.gain.setTargetAtTime(m ? 0 : BASE * gainOf(this.vol.master), this.ctx.currentTime, 0.05);
  }

  /** Рівень каналу з мікшера: master | engine | fx | voice, 0…100. */
  setVol(k, p) {
    this.vol[k] = p;
    if (!this.ctx) return;
    const t = this.ctx.currentTime;
    if (k === 'master') { if (!this.muted) this.master.gain.setTargetAtTime(BASE * gainOf(p), t, 0.05); }
    else if (this.bus[k]) this.bus[k].gain.setTargetAtTime(gainOf(p), t, 0.05);
  }

  /** Щокадру: rpm, газ 0…1, гальмо 0…1, сигнал. */
  update(rpm, throttle, brake, horn) {
    if (!this.ctx) return;
    const t = this.ctx.currentTime;
    const dt = Math.min(0.1, Math.max(0, t - (this.lastT || t))); this.lastT = t;
    const tr = this.truck;
    const v = tr && tr.speed ? Math.abs(tr.speed()) : 0;
    const gear = tr ? tr.gear : 1;
    const inGear = gear !== 0;
    const k = this.ducked ? 0.35 : 1;                // говорить Глек — двигун тихіше
    const cab = this.view() === 'cabin';
    const cv = cab && tr && tr.P && tr.P.cabVol ? tr.P.cabVol : 1;   // «Тихий салон»
    rpm *= this.pitch;                               // голос тягача: «Віл» нижче, «Ластівка» вище

    // навантаження: газ, згладжений (педаль не клацає тембром)
    this.load += (throttle - this.load) * Math.min(1, dt * 7);
    // гальмо двигуном: скидання на швидкості з обертами вище ~1000 і з педаллю (brakeNow 0,06 — це саме двигун)
    const coast = throttle < 0.03 && inGear && v > 2 ? 1 : 0;
    const jakeGoal = coast * Math.max(0, Math.min(1, (rpm - 1000) / 700)) * (brake > 0.1 ? 1 : 0.35);
    this.jake += (jakeGoal - this.jake) * Math.min(1, dt * 6);
    // наддув: росте з газом на обертах (≈0,8 с), спадає швидше
    const bGoal = throttle * Math.max(0, Math.min(1, (rpm - 800) / 1100));
    this.boost += (bGoal - this.boost) * Math.min(1, dt * (bGoal > this.boost ? 1.3 : 3));

    if (this.samples) {
      this.samples.update(rpm, this.load, this.jake, t, this.view() !== 'cabin');
      this.engIn.gain.setTargetAtTime(0.9 * k * cv, t, 0.08);
    } else {
      const f = (rpm / 60) * 3;                     // шість циліндрів: три спалахи на оберт
      this.o1.frequency.setTargetAtTime(f, t, 0.05);
      this.o2.frequency.setTargetAtTime(f * 0.5, t, 0.05);
      this.lp.frequency.setTargetAtTime(260 + throttle * 900 + rpm * 0.12, t, 0.08);
      this.eng.gain.setTargetAtTime(0.16 + throttle * 0.16, t, 0.08);
      this.ng.gain.setTargetAtTime(0.02 + throttle * 0.07, t, 0.1);
      this.engIn.gain.setTargetAtTime(k * cv, t, 0.08);
    }
    // турбіна
    const tf = 1800 + this.boost * 3800 + rpm * 0.4;
    this.tOsc.frequency.setTargetAtTime(tf, t, 0.08);
    this.tBp.frequency.setTargetAtTime(tf * 1.02, t, 0.08);
    this.turbo.gain.setTargetAtTime(this.boost * this.boost * 0.05, t, 0.1);
    // дорога
    const roadK = Math.min(1, v / 22);
    this.road.gain.setTargetAtTime(roadK * 0.16, t, 0.2);
    this.roadLp.frequency.setTargetAtTime(160 + v * 14, t, 0.2);

    // кабіна: глухіше, більше низу; зовні — відкрито
    if (cab !== this.cab) {
      this.cab = cab;
      this.cabLp.frequency.setTargetAtTime(cab ? 1300 : 9000, t, 0.08);
      this.cabLow.gain.setTargetAtTime(cab ? 5 : 0, t, 0.08);
    }

    // сигнал: ріжок із Лавки — свій синтез; інакше запис (чи синтез, доки запису нема)
    if (this.hornKind) {
      this.hornCustom(horn, t);
      if (this.hornS) this.hornS.gain.setTargetAtTime(0, t, 0.02);
      this.hornG.gain.setTargetAtTime(0, t, 0.02);
    } else if (this.hornS) {
      this.hornS.gain.setTargetAtTime(horn ? 0.7 * (this.hornFx.gain || 1) : 0, t, horn ? 0.015 : 0.06);
      this.hornG.gain.setTargetAtTime(0, t, 0.02);
    } else this.hornG.gain.setTargetAtTime(horn ? 0.22 : 0, t, 0.02);

    // «пшш» — коли довго тримав гальмо й відпустив
    if (brake > 0.3) this.braking += 1; else { if (this.braking > 40) this.hiss(); this.braking = 0; }
    // перемикання передачі — коротке «пш» пневматики коробки
    if (this.gear != null && gear !== this.gear && gear > 0 && this.gear > 0) this.burst(0.12, 'highpass', 4000, 0.035);
    this.gear = gear;
  }

  /** Ріжок із вигляду фури (id речі Лавки) чи null — звичайний сигнал. */
  setHorn(id) {
    const k = id && HORNS[id] ? id : null;
    if (k === this.hornKind) return;
    this.hornOff(this.ctx ? this.ctx.currentTime : 0);
    this.hornKind = k;
  }

  /** Голос тягача: 'diesel' (Чумак), 'v8' (Віл — нижче), 'six' (Ластівка — вища «шістка»). */
  setVoiceOf(kind) { this.pitch = kind === 'v8' ? 0.8 : kind === 'six' ? 1.15 : 1; }

  /** Ріжок, поки тримаєш: тон / «га-га» / ноти, наспів — по колу. */
  hornCustom(on, t) {
    const H = HORNS[this.hornKind], ctx = this.ctx;
    if (!on) { if (this.hv) this.hornOff(t); return; }
    let v = this.hv;
    if (!v) {
      const env = ctx.createGain(); env.gain.value = 0;
      const lp = ctx.createBiquadFilter(); lp.type = 'lowpass'; lp.frequency.value = H.lp; lp.Q.value = 0.9;
      lp.connect(env); env.connect(this.bus.fx);
      v = this.hv = { env, lp, oscs: [], next: t, i: 0, held: null };
      if (H.tones) {
        let lfo = null;
        if (H.vib) { lfo = ctx.createOscillator(); lfo.frequency.value = H.vib[0]; lfo.start(t); v.oscs.push(lfo); }
        for (const [hz, type, g] of H.tones) {
          const o = ctx.createOscillator(); o.type = type; o.frequency.value = hz;
          if (lfo) { const d = ctx.createGain(); d.gain.value = hz * H.vib[1]; lfo.connect(d); d.connect(o.frequency); }
          const og = ctx.createGain(); og.gain.value = g; o.connect(og); og.connect(lp); o.start(t); v.oscs.push(o);
        }
        if (!H.pulse) env.gain.setTargetAtTime(0.2, t, H.attack);
      } else env.gain.value = 0.2;
    }
    if (H.pulse) {
      // «га-га»: вмикаємо й гасимо огинаючу, висота спадає до кінця кожного «га»
      while (v.next < t + 0.15) {
        const at = Math.max(v.next, t), [onS, offS] = H.pulse;
        v.env.gain.setTargetAtTime(0.22, at, H.attack);
        v.env.gain.setTargetAtTime(0.0001, at + onS, 0.02);
        for (let i = 0; i < v.oscs.length; i++) {
          const f0 = H.tones[i][0];
          v.oscs[i].frequency.setValueAtTime(f0, at);
          v.oscs[i].frequency.linearRampToValueAtTime(f0 * H.bend, at + onS);
        }
        v.next = at + onS + offS;
      }
    }
    if (H.seq) {
      while (v.next < t + 0.15 && (H.loop || v.i < H.seq.length)) {
        const [notes, beats] = H.seq[v.i % H.seq.length], at = Math.max(v.next, t), dur = beats * H.step;
        const last = !H.loop && v.i === H.seq.length - 1;
        const g = ctx.createGain(); g.gain.setValueAtTime(0.0001, at);
        g.gain.linearRampToValueAtTime(0.8, at + 0.012);
        g.gain.setTargetAtTime(0.55, at + 0.03, 0.08);
        if (!(last && H.hold)) g.gain.setTargetAtTime(0.0001, at + dur - 0.03, 0.015);
        g.connect(v.lp);
        for (const n of notes) {
          if (!n) continue;                      // пауза
          const o = ctx.createOscillator(); o.type = H.type; o.frequency.value = midi(n);
          const og = ctx.createGain(); og.gain.value = 0.5 / Math.sqrt(notes.length);
          o.connect(og); og.connect(g); o.start(at);
          if (last && H.hold) v.oscs.push(o); else o.stop(at + dur + 0.05);
        }
        v.next = at + dur; v.i++;
      }
    }
  }

  hornOff(t) {
    const v = this.hv;
    if (!v) return;
    this.hv = null;
    try {
      v.env.gain.cancelScheduledValues(t);
      v.env.gain.setTargetAtTime(0, t, 0.05);
      for (const o of v.oscs) o.stop(t + 0.4);
    } catch { /* контекст закрито */ }
  }

  /** Зіграти запис-ефект із банку (air, couple…). Нема — false. */
  play(name, gain = 1, dest) {
    const it = this.bank && this.bank.fx && this.bank.fx[name];
    if (!this.ctx || !it) return false;
    const s = this.ctx.createBufferSource(); s.buffer = it.buf;
    const g = this.ctx.createGain(); g.gain.value = gain * (it.gain || 1);
    s.connect(g); g.connect(dest || this.bus.fx); s.start();
    return true;
  }

  /** Короткий фільтрований шум. dest — куди (типово ефекти; рація шле в свою шину). */
  burst(dur, type, freq, gain, q, dest) {
    if (!this.ctx) return;
    const ctx = this.ctx, t = ctx.currentTime;
    const s = ctx.createBufferSource(); s.buffer = this.noise;
    const f = ctx.createBiquadFilter(); f.type = type; f.frequency.value = freq; f.Q.value = q || 1;
    const g = ctx.createGain(); g.gain.setValueAtTime(gain, t); g.gain.exponentialRampToValueAtTime(0.001, t + dur);
    s.connect(f); f.connect(g); g.connect(dest || this.bus.fx);
    s.start(t, Math.random()); s.stop(t + dur);
  }

  hiss() { if (!this.play('air', 0.8)) this.burst(0.6, 'highpass', 3000, 0.18); }

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
      o.connect(g); g.connect(this.bus.fx); o.start(at); o.stop(at + 0.3);
    }
    this.burst(0.12, 'bandpass', 2500, 0.1 + power * 0.2, 2);
  }

  /** Зчеп/відчеп: записом, де є; інакше глухий удар. */
  thump() { if (!this.play('couple', 1)) this.burst(0.25, 'lowpass', 220, 0.5); }

  /** Приглушити двигун (поки говорить рація). */
  duck(on) { this.ducked = !!on; }

  stop() {
    clearTimeout(this.loadTimer); clearTimeout(this.synthTimer);
    document.removeEventListener('visibilitychange', this.onVis);
    if (this.ctx) { try { this.ctx.close(); } catch { /* пусто */ } this.ctx = null; }
  }
}
