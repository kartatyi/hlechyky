// Глек-диспетчер у рації: фрази з /api/games/fura/voice (сервер озвучує їх TTS і віддає готові mp3), події рейсу
// бачить браузер (trip.js, main.js) і каже say('coupled'). Звук іде крізь «рацію»: смуга 300–3200 Гц, легкий хрип і
// шипіння на початку й наприкінці; двигун і ефір сайту на цей час притихають. Субтитр — завжди: фраза видна, навіть
// коли звук 🔇 чи сервер ще не встиг озвучити.

const REFETCH_MS = 20000, REFETCH_FOR_MS = 5 * 60000;

export class Voice {
  constructor({ sound, hud }) {
    this.sound = sound;
    this.book = null;
    this.queue = [];
    this.busy = false;
    this.last = {};            // подія → останній варіант (щоб не повторювався поспіль)
    this.at = {};              // подія → коли казали востаннє
    this.el = null; this.chain = null;
    this.stopped = false;
    const sub = document.createElement('div');
    sub.className = 'fura-panel fura-radio-sub';
    hud.appendChild(sub);
    this.sub = sub;
    this.t0 = performance.now();
    this.load();
  }

  async load() {
    try {
      const r = await fetch('/api/games/fura/voice');
      if (r.ok) this.book = await r.json();
    } catch { /* без голосу — лише субтитри, яких теж нема: книги нема */ }
    // поки сервер озвучує (перший запуск після деплою), частина фраз без mp3 — перепитаємо
    const missing = this.book && this.book.enabled && Object.values(this.book.lines).some((l) => l.some((x) => !x.url));
    if (!this.stopped && missing && performance.now() - this.t0 < REFETCH_FOR_MS) setTimeout(() => this.load(), REFETCH_MS);
  }

  /** Сказати подію. gap — не частіше ніж раз на gap мс; urgent — без черги, перериває поточну. */
  say(event, { gap = 0, urgent = false } = {}) {
    if (!this.book || !this.book.lines || !this.book.lines[event]) return;
    const now = performance.now();
    if (gap && this.at[event] && now - this.at[event] < gap) return;
    this.at[event] = now;
    const list = this.book.lines[event];
    let i = Math.floor(Math.random() * list.length);
    if (list.length > 1 && i === this.last[event]) i = (i + 1) % list.length;
    this.last[event] = i;
    const line = list[i];
    if (urgent) { this.queue = [line]; this.cut(); }
    else { if (this.queue.length >= 2) this.queue.shift(); this.queue.push(line); }
    this.next();
  }

  cut() { if (this.el) { try { this.el.pause(); } catch { /* пусто */ } } this.finish(); }

  async next() {
    if (this.busy || !this.queue.length || this.stopped) return;
    const line = this.queue.shift();
    this.busy = true;
    this.show(line.text);
    const canPlay = line.url && !this.sound.muted && this.sound.ctx;
    if (!canPlay) { this.timer = setTimeout(() => this.finish(), 2200 + line.text.length * 45); return; }
    this.duck(true);
    this.sound.burst(0.18, 'bandpass', 2400, 0.18, 1.5);         // «кшш» — натиснув тангенту
    try {
      this.ensureChain();
      this.el.src = line.url;
      this.el.onended = () => { this.sound.burst(0.12, 'bandpass', 2000, 0.12, 1.5); this.finish(); };
      this.el.onerror = () => this.finish();
      await this.el.play();
    } catch { this.timer = setTimeout(() => this.finish(), 2000 + line.text.length * 45); }
  }

  finish() {
    clearTimeout(this.timer);
    this.duck(false);
    this.busy = false;
    clearTimeout(this.subTimer);
    this.subTimer = setTimeout(() => this.sub.classList.remove('on'), 1600);
    setTimeout(() => this.next(), 350);
  }

  show(text) {
    clearTimeout(this.subTimer);
    this.sub.innerHTML = '<span class="who">📻 Дядько Глек:</span> ' + text.replace(/[&<>]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]));
    this.sub.classList.add('on');
  }

  /** Рація: медіа-елемент → смуга 300–3200 Гц → легкий хрип → гучність. Будуємо раз (джерело з елемента — лише одне). */
  ensureChain() {
    if (this.el) return;
    const ctx = this.sound.ctx;
    this.el = new Audio();
    this.el.crossOrigin = 'anonymous';
    const src = ctx.createMediaElementSource(this.el);
    const hp = ctx.createBiquadFilter(); hp.type = 'highpass'; hp.frequency.value = 300;
    const lp = ctx.createBiquadFilter(); lp.type = 'lowpass'; lp.frequency.value = 3200;
    const peak = ctx.createBiquadFilter(); peak.type = 'peaking'; peak.frequency.value = 1500; peak.gain.value = 5;
    const shaper = ctx.createWaveShaper();
    const curve = new Float32Array(256);
    for (let i = 0; i < 256; i++) { const x = i / 128 - 1; curve[i] = Math.tanh(x * 2.2) / Math.tanh(2.2); }
    shaper.curve = curve;
    const g = ctx.createGain(); g.gain.value = 1.6;
    src.connect(hp); hp.connect(lp); lp.connect(peak); peak.connect(shaper); shaper.connect(g); g.connect(this.sound.master);
  }

  /** Поки Глек говорить — двигун і ефір сайту тихіше. */
  duck(on) {
    this.sound.duck && this.sound.duck(on);
    const radio = document.getElementById('audio');
    if (!radio) return;
    if (on) { if (this.radioVol == null) { this.radioVol = radio.volume; radio.volume = Math.min(radio.volume, this.radioVol * 0.3); } }
    else if (this.radioVol != null) { radio.volume = this.radioVol; this.radioVol = null; }
  }

  stop() {
    this.stopped = true;
    clearTimeout(this.timer); clearTimeout(this.subTimer);
    if (this.el) { try { this.el.pause(); } catch { /* пусто */ } }
    this.duck(false);
  }
}
