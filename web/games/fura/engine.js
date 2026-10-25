// Дизель на записах: петлі справжнього тягача на кількох обертах (web/static/fura/sound/, опис — bank.json).
// Кожна петля грає безперервно; за обертами беремо дві сусідні й перехресно згасаємо їх (equal-power: cos/sin), а
// кожну підтягуємо playbackRate = оберти / оберти запису — так оберти течуть плавно, без сходинок і провалів.
// Два набори: «під газом» (on) і «скидання» (off). Окремих записів скидання нема — off робимо з тих самих петель,
// глухіше й тихіше. Гальмо двигуном — той самий звук крізь «хрип» (waveshaper) і смугу: тріскуче «тр-р-р».
// Набір on записано з кабіни; зовні поверх додаємо «вихлоп» (set: 'ext') — тягач під гору, чутний лише під газом.
// Оберти гри (600…2100) — не оберти запису: bank.rpmMap лінійно переводить їх у живий діапазон тягача.
// Петлі в файлах — із запасом періодичного продовження з обох боків, тож затримка декодера mp3 швів не ламає.

const DIR = '/static/fura/sound/';

/** Завантажити й розкодувати банк. Кидає, якщо чогось нема — тоді звучить синтез. */
export async function loadBank(ctx) {
  const r = await fetch(DIR + 'bank.json');
  if (!r.ok) throw new Error('bank ' + r.status);
  const bank = await r.json();
  const dec = async (f) => {
    const x = await fetch(DIR + f);
    if (!x.ok) throw new Error(f + ' ' + x.status);
    const ab = await x.arrayBuffer();
    return new Promise((ok, no) => ctx.decodeAudioData(ab, ok, no));
  };
  const items = [...bank.loops, ...Object.values(bank.fx || {})];
  await Promise.all(items.map(async (it) => { it.buf = await dec(it.file); }));
  return bank;
}

/** Ваги equal-power двох сусідніх за обертами петель (list відсортований за rpm). */
export function xfade(list, rpm) {
  const w = list.map(() => 0);
  if (!list.length) return w;
  if (rpm <= list[0].rpm) { w[0] = 1; return w; }
  const n = list.length - 1;
  if (rpm >= list[n].rpm) { w[n] = 1; return w; }
  let i = 0;
  while (rpm > list[i + 1].rpm) i++;
  const x = (rpm - list[i].rpm) / (list[i + 1].rpm - list[i].rpm);
  w[i] = Math.cos(x * Math.PI / 2);
  w[i + 1] = Math.sin(x * Math.PI / 2);
  return w;
}

export class SampleEngine {
  /** out — куди віддавати (вхід двигуна в Sound: далі фільтр кабіни й шина «двигун»). */
  constructor(ctx, out, bank) {
    this.ctx = ctx;
    this.bank = bank;
    this.out = ctx.createGain(); this.out.gain.value = 0; this.out.connect(out);
    const t = ctx.currentTime;
    // скидання: глухіше (менше «гавкоту» вихлопу) і без верхів
    this.offBus = ctx.createGain();
    this.offLp = ctx.createBiquadFilter(); this.offLp.type = 'lowpass'; this.offLp.frequency.value = 900; this.offLp.Q.value = 0.5;
    this.offBus.connect(this.offLp); this.offLp.connect(this.out);
    // гальмо двигуном: перегрузка + смуга 250–1800 Гц
    this.jakeIn = ctx.createGain();
    const sh = ctx.createWaveShaper();
    const c = new Float32Array(512);
    for (let i = 0; i < 512; i++) { const x = i / 256 - 1; c[i] = Math.tanh(x * 6) * 0.9; }
    sh.curve = c;
    const bp = ctx.createBiquadFilter(); bp.type = 'bandpass'; bp.frequency.value = 650; bp.Q.value = 0.7;
    this.jakeG = ctx.createGain(); this.jakeG.gain.value = 0;
    this.jakeIn.connect(sh); sh.connect(bp); bp.connect(this.jakeG); this.jakeG.connect(this.out);

    const mk = (it) => {
      const s = ctx.createBufferSource();
      s.buffer = it.buf; s.loop = true;
      s.loopStart = it.loopStart; s.loopEnd = it.loopEnd;
      const g = ctx.createGain(); g.gain.value = 0;
      s.connect(g);
      // різні петлі стартують із різних місць — щоб не «збігалися» в одну фазу
      s.start(t, it.loopStart + Math.random() * (it.loopEnd - it.loopStart) * 0.9);
      return { it, s, g, rpm: it.rpm, k: it.gain || 1 };
    };
    const pick = (set) => bank.loops.filter((l) => (l.set || 'on') === set).sort((a, b) => a.rpm - b.rpm).map(mk);
    this.on = pick('on'); this.off = pick('off'); this.ext = pick('ext');
    for (const v of this.on) v.g.connect(this.out);
    for (const v of this.ext) v.g.connect(this.out);
    const m = bank.rpmMap;
    this.map = m ? (r) => m.to[0] + (r - m.from[0]) * (m.to[1] - m.to[0]) / (m.from[1] - m.from[0]) : (r) => r;
    for (const v of this.off) v.g.connect(this.offBus);
    if (!this.off.length) {
      // своїх записів скидання нема — ті самі петлі вдруге, у глуху гілку
      for (const v of this.on) { v.gOff = ctx.createGain(); v.gOff.gain.value = 0; v.s.connect(v.gOff); v.gOff.connect(this.offBus); }
    }
    for (const v of this.on) { v.gJ = ctx.createGain(); v.gJ.gain.value = 0; v.s.connect(v.gJ); v.gJ.connect(this.jakeIn); }
  }

  /** rpm — оберти гри; load 0…1 — газ (згладжений); jake 0…1 — гальмо двигуном; outside — камера не в кабіні. */
  update(rpmGame, load, jake, t, outside) {
    const T = 0.04;
    const loud = 0.55 + 0.45 * Math.max(0, Math.min(1, (rpmGame - 600) / 1500));   // вище оберти — гучніше
    const rpm = this.map(rpmGame);
    const gOn = Math.sin(load * Math.PI / 2), gOff = Math.cos(load * Math.PI / 2);
    const rate = (v) => Math.max(0.5, Math.min(2, rpm / v.rpm));
    const wOn = xfade(this.on, rpm);
    for (let i = 0; i < this.on.length; i++) {
      const v = this.on[i], w = wOn[i];
      if (w > 0 || v.g.gain.value > 0.0005) v.s.playbackRate.setTargetAtTime(rate(v), t, T);
      v.g.gain.setTargetAtTime(w * v.k * loud * (this.off.length ? gOn : 0.25 + 0.75 * gOn), t, T);
      if (v.gOff) v.gOff.gain.setTargetAtTime(w * v.k * loud * gOff * 0.9, t, T);
      v.gJ.gain.setTargetAtTime(w * v.k * jake, t, T);
    }
    if (this.off.length) {
      const wOff = xfade(this.off, rpm);
      for (let i = 0; i < this.off.length; i++) {
        const v = this.off[i];
        v.s.playbackRate.setTargetAtTime(rate(v), t, T);
        v.g.gain.setTargetAtTime(wOff[i] * v.k * loud * gOff, t, T);
      }
    }
    // вихлоп зовні: під газом на робочих обертах; з кабіни ледь
    const wExt = xfade(this.ext, rpm);
    const kExt = load * Math.max(0, Math.min(1, (rpm - 1050) / 400)) * (outside ? 0.75 : 0.15);
    for (let i = 0; i < this.ext.length; i++) {
      const v = this.ext[i];
      v.s.playbackRate.setTargetAtTime(rate(v), t, T);
      v.g.gain.setTargetAtTime(wExt[i] * v.k * loud * kExt, t, 0.08);
    }
    this.offLp.frequency.setTargetAtTime(500 + rpm * 0.35, t, 0.1);
    this.jakeG.gain.setTargetAtTime(jake * 0.32, t, 0.05);
  }

  /** Плавно до рівня to (0 — прибрати). at — з якого часу контексту. */
  fade(to, tau, at) { this.out.gain.setTargetAtTime(to, Math.max(at || 0, this.ctx.currentTime), tau || 0.3); }
}
