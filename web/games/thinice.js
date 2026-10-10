/*
  Тонкий лід — ставок у два яруси льоду, що тріщить під ногами (Impl/Thinice.cs, ThiniceCore.cs, ThiniceBot.cs;
  spec docs/games/specs/thinice.md §4).

  Вид: { phase, round, rounds, party, n, sub, tickMs, speed, crack, air, fall, jumpCd, thawAt, capAt, points[8],
    wins[8], out, lastRound: { winner, byTime, ranks[8] }, winner, winners, series, bot, frame }.
  Кадр (25 Гц у грі): { t, ph (0 готуйсь · 1 гра · 2 кінець раунду · 3 партію зіграно · 4 лобі), left, rt, thaw,
    ice: [верхній, нижній] — рядки N·N: '.' ціла, '#' дірка, 'b'..'z' тріщить (k = код − 'a' тиків до дірки),
    p[8]: [x, y, ярус, fl, air, fall, cd, face] | null (fl: 1 на льоду · 2 рухається · 4 у воді), ev }.
  Ввід: Input('move', { a: −1..15 }) — сектор по 22,5° (0 праворуч, за годинниковою), Input('jump').

  Своє тіло не чекає сервера: рух простий (рівно Dx/Dy ядра, 16 одиниць за тик), тож малюємо його там, де сервер
  його побачить, коли дійде наш намір (останній кадр + час відтоді + RTT), і м'яко підтягуємо до правди кожним
  кадром. Чужих — інтерполюємо на інтервал позаду (ui.Interp), як у Танчиках.
  Події (падіння, шубовсь, стрибок, нові дірки) беремо з різниці кадрів, а не з ev: Broadcaster із пачки лишає
  останній кадр, і ev проміжних губляться.
*/
(() => {
  // ---- числа ядра (ThiniceCore) ----
  const TICK_MS = 40, SUB = 100, SPEED = 16, JSPEED = 18, MARGIN = 22, CRACK = 25, AIR = 12, FALL = 10, JUMP_CD = 75;
  const DX = [], DY = [], JDX = [], JDY = [];
  for (let a = 0; a < 16; a++) {
    DX.push(Math.round(Math.cos((a * Math.PI) / 8) * SPEED));
    DY.push(Math.round(Math.sin((a * Math.PI) / 8) * SPEED));
    JDX.push(Math.round(Math.cos((a * Math.PI) / 8) * JSPEED));
    JDY.push(Math.round(Math.sin((a * Math.PI) / 8) * JSPEED));
  }
  /// Тиків у повітрі за напрямом (ThiniceCore.AirOf): по осі 12, під 22,5° 13, під 45° 14, на місці 12.
  const airOf = (dir) => (dir < 0 ? AIR : AIR + [0, 1, 2, 1][dir % 4]);
  /// Напрям польоту тіла з кадру: у повітрі й «рухається» — туди, куди дивиться; інакше стрибок на місці.
  const airDir = (q) => ((q[3] & 2) ? q[7] : -1);
  /// Вектор → сектор 0..15 (−1 — нуль).
  const sectorOf = (dx, dy) => (dx === 0 && dy === 0 ? -1 : ((Math.round(Math.atan2(dy, dx) / (Math.PI / 8)) % 16) + 16) % 16);

  // ---- вигляд ----
  const SIZE = 600;            // логічний канвас: увесь ставок із берегом
  const PAD = 14;              // сніговий берег довкола
  const SEND_MS = 50;          // наміри — не частіше 20/с
  const KEEP_MS = 400;         // той самий напрям, поки тримають, — раз на 0,4 с (сервер гасить намір за 1,2 с)
  const AWAKE_MS = 1500;       // після останньої події цикл ще живе — догорають бризки й банери
  const MAX_PARTS = 320;
  const SEAT_VARS = [['--ti-s0', '#5aa9ff'], ['--ti-s1', '#e0875c'], ['--ti-s2', '#7bd389'], ['--ti-s3', '#f4c542'],
    ['--ti-s4', '#b48cf2'], ['--ti-s5', '#5fd3c0'], ['--ti-s6', '#f08cb8'], ['--ti-s7', '#c0c8c4']];
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<rect x="1" y="1" width="6.5" height="6.5" rx="1.2" fill="var(--ti-ice, #dff3ff)"/>'
    + '<rect x="8.5" y="1" width="6.5" height="6.5" rx="1.2" fill="var(--ti-ice, #dff3ff)"/>'
    + '<rect x="1" y="8.5" width="6.5" height="6.5" rx="1.2" fill="var(--ti-ice, #dff3ff)"/>'
    + '<rect x="8.5" y="8.5" width="6.5" height="6.5" rx="1.2" fill="var(--ti-water2, #1d5677)"/>'
    + '<path d="M9.5 2.5 11.5 4.6 10.6 6.3M11.5 4.6 13.8 3.8" stroke="var(--ti-water2, #1d5677)" stroke-width=".9" fill="none"/>'
    + '<circle cx="4.3" cy="11.6" r="2.3" fill="var(--clay)"/><circle cx="5.2" cy="11.1" r=".6" fill="#fff"/></svg>';

  const reduced = () => !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
  const scale = () => ((window.devicePixelRatio || 1) >= 2 ? 1 : 2);   // на DPR 1 малюємо вдвічі щільніше
  const padOn = () => !!(window.HPad && HPad.pads > 0);
  const nameAt = (ctx, s) => (ctx.nameOf ? ctx.nameOf(s) : ctx.nickOf(s));
  const clamp = (v, a, b) => (v < a ? a : v > b ? b : v);
  const secs = (ticks) => Math.max(0, Math.ceil((ticks * TICK_MS) / 1000));
  const clock = (ticks) => {
    const s = Math.max(0, Math.floor((ticks * TICK_MS) / 1000));
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  };
  /// Код плитки: 0 ціла, −1 дірка, 1..25 — тиків до дірки.
  const cellOf = (row, i) => {
    const c = row.charCodeAt(i);
    return c === 46 ? 0 : c === 35 ? -1 : c - 97;
  };

  // ---- звук: коротенькі «хрусь», «шубовсь», «хоп», лише синтез і лише після жесту ----
  const Snd = {
    on: (() => { try { return localStorage.getItem('thinice.sound') !== '0'; } catch { return true; } })(),
    ctx: null,
    noiseBuf: null,
    ensure() {
      if (!this.on) return null;
      const ua = navigator.userActivation;
      if (!this.ctx) {
        const AC = window.AudioContext || window.webkitAudioContext;
        if (!AC || (ua && !ua.hasBeenActive)) return null;
        try { this.ctx = new AC(); } catch { return null; }
      }
      if (this.ctx.state === 'suspended') this.ctx.resume().catch(() => {});
      return this.ctx;
    },
    beep(freq, ms, type, vol, to, delay) {
      const c = this.ensure();
      if (!c) return;
      const t = c.currentTime + (delay || 0), o = c.createOscillator(), g = c.createGain();
      o.type = type || 'square';
      o.frequency.setValueAtTime(freq, t);
      if (to) o.frequency.exponentialRampToValueAtTime(to, t + ms / 1000);
      g.gain.setValueAtTime(vol || 0.04, t);
      g.gain.exponentialRampToValueAtTime(0.0001, t + ms / 1000);
      o.connect(g).connect(c.destination);
      o.start(t);
      o.stop(t + ms / 1000 + 0.02);
    },
    noise(ms, cutoff, vol, delay) {
      const c = this.ensure();
      if (!c) return;
      if (!this.noiseBuf) {
        const n = Math.floor(c.sampleRate * 0.4);
        this.noiseBuf = c.createBuffer(1, n, c.sampleRate);
        const d = this.noiseBuf.getChannelData(0);
        for (let i = 0; i < n; i++) d[i] = Math.random() * 2 - 1;
      }
      const t = c.currentTime + (delay || 0);
      const src = c.createBufferSource(), f = c.createBiquadFilter(), g = c.createGain();
      src.buffer = this.noiseBuf;
      f.type = 'lowpass';
      f.frequency.value = cutoff;
      g.gain.setValueAtTime(vol, t);
      g.gain.exponentialRampToValueAtTime(0.0001, t + ms / 1000);
      src.connect(f).connect(g).connect(c.destination);
      src.start(t);
      src.stop(t + ms / 1000 + 0.02);
    },
    crack() { this.noise(90, 3200, 0.025); },
    fall() { this.noise(220, 700, 0.05); this.beep(300, 220, 'sine', 0.03, 110); },
    splash() { this.beep(180, 360, 'sine', 0.05, 60); this.noise(300, 900, 0.035); },
    jump() { this.beep(420, 110, 'triangle', 0.03, 760); },
    thaw() { [660, 520, 400].forEach((f, i) => this.beep(f, 160, 'triangle', 0.03, 0, i * 0.12)); },
    round() { [523, 659, 784].forEach((f, i) => this.beep(f, 130, 'square', 0.035, 0, i * 0.11)); },
    set(on) {
      this.on = on;
      try { localStorage.setItem('thinice.sound', on ? '1' : '0'); } catch { /* приватне вікно */ }
    },
  };

  // ---- клавіатура одна на всі картки: keyup ловимо з документа (і в режимі вечірки — keydown дає господар) ----
  const live = new Set();
  const held = { up: false, down: false, left: false, right: false };
  const KEYS = { ArrowUp: 'up', KeyW: 'up', ArrowDown: 'down', KeyS: 'down', ArrowLeft: 'left', KeyA: 'left', ArrowRight: 'right', KeyD: 'right' };
  const KEYS_BY_KEY = { w: 'up', s: 'down', a: 'left', d: 'right', ц: 'up', і: 'down', ф: 'left', в: 'right' };
  const keyOf = (e) => KEYS[e.code] || KEYS_BY_KEY[String(e.key || '').toLowerCase()];
  const isJump = (e) => e.code === 'Space' || e.key === ' ' || e.code === 'KeyJ';
  const keySector = () => sectorOf((held.right ? 1 : 0) - (held.left ? 1 : 0), (held.down ? 1 : 0) - (held.up ? 1 : 0));

  /// Усе відпустили. force — шлемо одразу: сторінка ховається, другого шансу не буде.
  function releaseAll(force) {
    held.up = held.down = held.left = held.right = false;
    for (const st of live) { st.stickA = null; st.padA = null; push(st, force === true); }
  }
  document.addEventListener('keyup', (e) => {
    const k = keyOf(e);
    if (!k || !held[k]) return;
    held[k] = false;
    for (const st of live) push(st);
  });
  window.addEventListener('blur', () => releaseAll(false));
  window.addEventListener('pagehide', () => releaseAll(true));
  document.addEventListener('visibilitychange', () => { if (document.hidden) releaseAll(true); });

  // ---- стан картки ----
  function state(root, ctx) {
    let st = root._thinice;
    if (!st) {
      st = root._thinice = {
        ctx, cv: null, K: scale(), raf: 0, interp: HGames.ui.Interp(TICK_MS), last: null, n: 10, view: null,
        // своє тіло
        me: { x: 0, y: 0 }, meOk: false, lastFrameAt: 0, rtt: 80, echo: null, jumpAt: 0, drawAt: 0,
        // ввід
        sent: null, sentAt: 0, stickA: null, padA: null, was: false,
        // соки
        parts: [], pops: [], shake: 0, banner: null, focus: 0, prev: [], phAt: 0, lastPh: -1,
        // кеш малювання
        pal: null, palAt: 0, sprites: null, bg: null, cracks: new Map(), hudSig: '', hudK: -1,
        awakeUntil: 0, cdBucket: -2, nicks: [], bodyBuf: [], textK: 1,
      };
      live.add(st);
    }
    st.ctx = ctx;
    return st;
  }

  // ---- кольори ----
  function rgb(hex) {
    const h = String(hex).trim().replace('#', '');
    const f = h.length === 3 ? h.split('').map((c) => c + c).join('') : h;
    const n = parseInt(f, 16);
    return Number.isFinite(n) ? [(n >> 16) & 255, (n >> 8) & 255, n & 255] : [128, 128, 128];
  }
  const mixRgb = (a, b, k) => 'rgb(' + Math.round(a[0] + (b[0] - a[0]) * k) + ',' + Math.round(a[1] + (b[1] - a[1]) * k) + ',' + Math.round(a[2] + (b[2] - a[2]) * k) + ')';
  const shadeHex = (hex, k) => {
    const c = rgb(hex);
    return k < 0 ? mixRgb(c, [0, 0, 0], -k) : mixRgb(c, [255, 255, 255], k);
  };

  function palette(st) {
    const now = performance.now();
    if (st.pal && now - st.palAt < 3000) return st.pal;
    const css = (v, fb) => HGames.ui.css(v, fb);
    const p = {
      ice: css('--ti-ice', '#e4f5ff'), iceEdge: css('--ti-ice-edge', '#9ccbe7'), iceHi: css('--ti-ice-hi', '#ffffff'),
      low: css('--ti-low', '#86b9d8'), lowEdge: css('--ti-low-edge', '#4f86ab'),
      dark: css('--ti-dark', '#3b6d90'), water: css('--ti-water', '#0d2a3e'), water2: css('--ti-water2', '#1d5677'),
      snow: css('--ti-snow', '#eef5f8'), snow2: css('--ti-snow2', '#c6d8e2'), crack: css('--ti-crack', '#ffffff'),
      hot: css('--ti-hot', '#ff7a5c'),
      seats: SEAT_VARS.map(([v, fb]) => css(v, fb)),
    };
    p.iceRgb = rgb(p.ice);
    p.lowRgb = rgb(p.low);
    p.darkRgb = rgb(p.dark);
    const key = Object.values(p).join('|');
    if (st.pal && st.pal.key === key) { st.palAt = now; return st.pal; }
    p.key = key;
    st.pal = p;
    st.palAt = now;
    st.sprites = null;
    st.bg = null;
    return p;
  }

  // ---- геометрія ----
  const cellPx = (st) => (SIZE - 2 * PAD) / st.n;
  const toPx = (st, u) => PAD + (u / SUB) * cellPx(st);

  /// Візерунок тріщин клітинки — свій для кожної (і ярусу), щоб ставок не тріскав «під копірку». 5 ламаних по 4 точки.
  function crackOf(st, key) {
    let c = st.cracks.get(key);
    if (c) return c;
    let s = (key * 2654435761) >>> 0;
    const rnd = () => {
      s = (s + 0x6D2B79F5) >>> 0;
      let t = s;
      t = Math.imul(t ^ (t >>> 15), t | 1);
      t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
      return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
    c = new Float32Array(40);
    const cx = 0.38 + rnd() * 0.24, cy = 0.38 + rnd() * 0.24;
    const a0 = rnd() * Math.PI * 2;
    for (let l = 0; l < 5; l++) {
      let x = cx, y = cy, a = a0 + (l * Math.PI * 2) / 5 + (rnd() - 0.5) * 0.8;
      for (let k = 0; k < 4; k++) {
        c[l * 8 + k * 2] = x;
        c[l * 8 + k * 2 + 1] = y;
        const step = 0.12 + rnd() * 0.12;
        a += (rnd() - 0.5) * 1.1;
        x = clamp(x + Math.cos(a) * step, 0.06, 0.94);
        y = clamp(y + Math.sin(a) * step, 0.06, 0.94);
      }
    }
    st.cracks.set(key, c);
    return c;
  }

  /// Ціла плитка обох ярусів — заготовкою: на 14×14 це 392 плитки щокадру, drawImage дешевший за шлях із градієнтом.
  function sprites(st, pal, ppu) {
    const cs = cellPx(st);
    const px = Math.max(8, Math.ceil(cs * ppu));
    if (st.sprites && st.sprites.px === px) return st.sprites;
    const make = (top, edge, hi, inset, edgeH) => {
      const c = document.createElement('canvas');
      c.width = c.height = px;
      const g = c.getContext('2d');
      const k = px / cs;
      g.scale(k, k);
      const r = cs * 0.13, w = cs - 2 * inset;
      g.fillStyle = edge;
      rrect(g, inset, inset, w, w, r);
      g.fill();
      const gr = g.createLinearGradient(inset, inset, inset + w, inset + w);
      gr.addColorStop(0, hi);
      gr.addColorStop(0.35, top);
      gr.addColorStop(1, top);
      g.fillStyle = gr;
      rrect(g, inset, inset, w, w - edgeH, r);
      g.fill();
      // відблиск — коротка смужка зліва вгорі
      g.globalAlpha = 0.55;
      g.strokeStyle = hi;
      g.lineWidth = Math.max(1, cs * 0.04);
      g.lineCap = 'round';
      g.beginPath();
      g.moveTo(inset + w * 0.2, inset + w * 0.16);
      g.lineTo(inset + w * 0.42, inset + w * 0.16);
      g.stroke();
      return c;
    };
    st.sprites = {
      px,
      up: make(pal.ice, pal.iceEdge, pal.iceHi, Math.max(0.8, cs * 0.025), Math.max(2, cs * 0.075)),
      low: make(pal.low, pal.lowEdge, shadeHex(pal.low, 0.35), Math.max(1.2, cs * 0.045), Math.max(1.5, cs * 0.05)),
    };
    return st.sprites;
  }

  function rrect(g, x, y, w, h, r) {
    g.beginPath();
    if (g.roundRect) { g.roundRect(x, y, w, h, r); return; }
    g.moveTo(x + r, y);
    g.arcTo(x + w, y, x + w, y + h, r);
    g.arcTo(x + w, y + h, x, y + h, r);
    g.arcTo(x, y + h, x, y, r);
    g.arcTo(x, y, x + w, y, r);
    g.closePath();
  }

  /// Берег і вода — раз на розмір ставка.
  function background(st, pal, ppu) {
    const key = st.n + '|' + Math.round(SIZE * ppu);
    if (st.bg && st.bg.key === key) return st.bg.c;
    const c = document.createElement('canvas');
    c.width = c.height = Math.round(SIZE * ppu);
    const g = c.getContext('2d');
    g.scale(ppu, ppu);
    g.fillStyle = pal.snow;
    g.fillRect(0, 0, SIZE, SIZE);
    // замети по берегу — горбики з передбачуваним кроком
    g.fillStyle = pal.snow2;
    for (let i = 0; i < 44; i++) {
      const t = (i * 0.618) % 1, side = i % 4, r = 5 + ((i * 37) % 7);
      const along = 20 + t * (SIZE - 40);
      const x = side === 0 ? along : side === 1 ? SIZE - 3 : side === 2 ? along : 3;
      const y = side === 0 ? 3 : side === 1 ? along : side === 2 ? SIZE - 3 : along;
      g.beginPath();
      g.arc(x, y, r, 0, Math.PI * 2);
      g.fill();
    }
    const w = SIZE - 2 * PAD;
    const gr = g.createRadialGradient(SIZE / 2, SIZE / 2, w * 0.1, SIZE / 2, SIZE / 2, w * 0.75);
    gr.addColorStop(0, pal.water2);
    gr.addColorStop(1, pal.water);
    g.fillStyle = gr;
    rrect(g, PAD - 3, PAD - 3, w + 6, w + 6, 12);
    g.fill();
    st.bg = { key, c };
    return c;
  }

  // ---- соки ----
  function spawn(st, kind, x, y, vx, vy, life, size, color) {
    if (st.parts.length >= MAX_PARTS) st.parts.shift();
    st.parts.push({ kind, x, y, vx, vy, life, max: life, size, color, rot: Math.random() * 6, vr: (Math.random() - 0.5) * 8 });
  }
  function burst(st, kind, x, y, n, speed, life, size, color) {
    if (reduced()) n = Math.ceil(n / 3);
    for (let i = 0; i < n; i++) {
      const a = Math.random() * Math.PI * 2, v = speed * (0.4 + Math.random() * 0.8);
      spawn(st, kind, x, y, Math.cos(a) * v, Math.sin(a) * v, life * (0.7 + Math.random() * 0.6), size * (0.6 + Math.random() * 0.8), color);
    }
  }
  function pop(st, text, x, y, color) {
    st.pops.push({ text, x, y, at: performance.now(), color });
    if (st.pops.length > 12) st.pops.shift();
  }
  function banner(st, text, sub, ms) { st.banner = { text, sub: sub || '', at: performance.now(), ms: ms || 2200 }; }

  /// Події з різниці двох кадрів: падіння на нижній, шубовсь, стрибок, приземлення, нові дірки, відлига.
  function events(st, f) {
    const old = st.last;
    if (!old || !f.p || !old.p || f.ph !== 1) return;
    const pal = palette(st);
    const cs = cellPx(st);
    const ms = mySeat(st);
    let crunch = 0;
    for (let s = 0; s < 8; s++) {
      const a = old.p[s], b = f.p[s];
      if (!a || !b) continue;
      const x = toPx(st, b[0]), y = toPx(st, b[1]);
      if (a[2] === 0 && b[2] === 1) {
        burst(st, 'chip', x, y, 14, 90, 650, cs * 0.12, pal.ice);
        spawn(st, 'ring', x, y, 0, 0, 500, cs * 0.5, pal.iceHi);
        if (s === ms) { st.shake = Math.max(st.shake, 5); banner(st, '⬇ Провалився на нижній!', 'Далі — вода. Обережно', 1600); }
        else pop(st, '⬇', x, y - cs * 0.4, pal.seats[s]);
        crunch = Math.max(crunch, 2);
      }
      if ((a[3] & 1) && (b[3] & 4)) {
        const fx = toPx(st, a[0]), fy = toPx(st, a[1]);
        burst(st, 'drop', fx, fy, 20, 130, 700, cs * 0.09, pal.water2);
        burst(st, 'drop', fx, fy, 8, 60, 500, cs * 0.07, pal.iceHi);
        spawn(st, 'ring', fx, fy, 0, 0, 900, cs * 0.9, pal.iceHi);
        spawn(st, 'ring', fx, fy, 0, 0, 1300, cs * 1.4, pal.iceHi);
        spawn(st, 'sink', fx, fy, 0, 0, 700, cs * 0.3, pal.seats[s]);
        pop(st, 'шубовсь!', fx, fy - cs * 0.5, pal.seats[s]);
        if (s === ms) { st.shake = Math.max(st.shake, 8); banner(st, '🌊 Шубовсь!', 'Дивись, хто кого', 1800); }
        crunch = 3;
      }
      if (a[4] === 0 && b[4] > 0) {
        burst(st, 'puff', x, y + cs * 0.15, 6, 40, 400, cs * 0.1, pal.snow);
        if (s === ms) { st.jumpAt = 0; crunch = Math.max(crunch, 1); }
      }
      if (a[5] > 0 && b[5] === 0 && (b[3] & 1)) burst(st, 'puff', x, y, 8, 50, 400, cs * 0.1, pal.low);
    }
    // нові дірки — крихти льоду (на відлизі їх багато: беремо не більше дюжини за кадр)
    let holes = 0;
    for (let tier = 0; tier < 2 && holes < 12; tier++) {
      const ra = old.ice && old.ice[tier], rb = f.ice && f.ice[tier];
      if (!ra || !rb || ra.length !== rb.length) continue;
      const n = st.n;
      for (let i = 0; i < rb.length && holes < 12; i++) {
        if (rb.charCodeAt(i) !== 35 || ra.charCodeAt(i) === 35) continue;
        holes++;
        const x = PAD + ((i % n) + 0.5) * cs, y = PAD + (Math.floor(i / n) + 0.5) * cs;
        burst(st, 'chip', x, y, tier === 0 ? 6 : 4, 45, 450, cs * 0.1, tier === 0 ? pal.ice : pal.low);
        if (!crunch) crunch = 1;
      }
    }
    if (!old.thaw && f.thaw) { banner(st, '🌡 Відлига!', 'Тепер лід тріщить і сам', 2400); Snd.thaw(); }
    if (crunch === 3) Snd.splash();
    else if (crunch === 2) Snd.fall();
    else if (crunch === 1 && st.ctx && st.ctx.mine) Snd.crack();
  }

  function drawParts(st, g, dt) {
    const keep = [];
    for (const p of st.parts) {
      p.life -= dt;
      if (p.life <= 0) continue;
      keep.push(p);
      const k = p.life / p.max;
      p.x += (p.vx * dt) / 1000;
      p.y += (p.vy * dt) / 1000;
      p.vx *= 0.94;
      p.vy *= 0.94;
      p.rot += (p.vr * dt) / 1000;
      g.globalAlpha = Math.min(1, k * 1.4);
      if (p.kind === 'ring') {
        g.strokeStyle = p.color;
        g.lineWidth = 2 * k;
        g.beginPath();
        g.ellipse(p.x, p.y, p.size * (1.15 - k), p.size * (1.15 - k) * 0.8, 0, 0, Math.PI * 2);
        g.stroke();
      } else if (p.kind === 'sink') {
        // тіло йде під воду: менше й темніше
        g.fillStyle = p.color;
        g.globalAlpha = k * 0.8;
        g.beginPath();
        g.arc(p.x, p.y, p.size * k, 0, Math.PI * 2);
        g.fill();
      } else if (p.kind === 'chip') {
        g.save();
        g.translate(p.x, p.y);
        g.rotate(p.rot);
        g.fillStyle = p.color;
        g.fillRect(-p.size / 2, -p.size / 3, p.size, p.size * 0.66);
        g.restore();
      } else {
        g.fillStyle = p.color;
        g.beginPath();
        g.arc(p.x, p.y, p.size * (p.kind === 'puff' ? 1.6 - k * 0.6 : 1), 0, Math.PI * 2);
        g.fill();
      }
    }
    g.globalAlpha = 1;
    st.parts = keep;
  }

  function drawPops(st, g, now) {
    const keep = [];
    for (const p of st.pops) {
      const age = now - p.at;
      if (age > 1100) continue;
      keep.push(p);
      const k = age / 1100;
      g.globalAlpha = 1 - k;
      text(g, p.text, p.x, p.y - k * 26, 15 * st.textK, p.color, 800);
    }
    g.globalAlpha = 1;
    st.pops = keep;
  }

  function text(g, t, x, y, size, color, weight, align) {
    g.font = (weight || 700) + ' ' + size + 'px system-ui, -apple-system, "Segoe UI", sans-serif';
    g.textAlign = align || 'center';
    g.textBaseline = 'middle';
    g.lineWidth = Math.max(2.5, size / 5);
    g.strokeStyle = 'rgba(8,24,36,.85)';
    g.lineJoin = 'round';
    g.strokeText(t, x, y);
    g.fillStyle = color;
    g.fillText(t, x, y);
  }

  // ---- кадр: що де ----
  function mySeat(st) {
    const c = st.ctx;
    return c && c.mine && c.seat != null ? c.seat : -1;
  }
  const phaseOf = (st) => (st.last ? st.last.ph : 4);

  /// Своє тіло: останній кадр + рух за наміром на час відтоді й RTT (див. шапку), згладжено.
  function predictMe(st, now, dt) {
    const s = mySeat(st), f = st.last;
    const q = s >= 0 && f && f.p ? f.p[s] : null;
    if (!q || !(q[3] & 1)) { st.meOk = false; return null; }
    let tx = q[0], ty = q[1];
    if (f.ph === 1 && q[5] === 0) {
      const since = Math.min(160, now - st.lastFrameAt);
      let ahead = (since + Math.min(260, st.rtt)) / TICK_MS;
      let dir;
      const air = q[4] > 0;
      if (air) { dir = airDir(q); ahead = Math.min(ahead, q[4]); }
      else dir = canSend(st) ? currentWant(st) : -1;
      if (dir >= 0) {
        const hi = st.n * SUB - MARGIN;
        tx = clamp(tx + (air ? JDX : DX)[dir] * ahead, MARGIN, hi);
        ty = clamp(ty + (air ? JDY : DY)[dir] * ahead, MARGIN, hi);
      }
    }
    if (!st.meOk || Math.hypot(tx - st.me.x, ty - st.me.y) > 160) {
      st.me.x = tx; st.me.y = ty; st.meOk = true;
    } else {
      const k = 1 - Math.exp(-dt / 55);
      st.me.x += (tx - st.me.x) * k;
      st.me.y += (ty - st.me.y) * k;
    }
    return st.me;
  }

  /// Стан тіла s на зараз: [x, y, ярус, fl, air (дробове), fall (дробове), cd, face].
  function bodyAt(st, s, it, now, out) {
    const b = it.b && it.b.p ? it.b.p[s] : null;
    if (!b) return null;
    const a = it.a && it.a.p ? it.a.p[s] : null;
    const t = it.t;
    if (a && a[2] === b[2] && Math.abs(a[0] - b[0]) + Math.abs(a[1] - b[1]) < 150) {
      out[0] = a[0] + (b[0] - a[0]) * t;
      out[1] = a[1] + (b[1] - a[1]) * t;
      out[4] = a[4] + (b[4] - a[4]) * t;
      out[5] = a[5] + (b[5] - a[5]) * t;
    } else {
      out[0] = b[0]; out[1] = b[1]; out[4] = b[4]; out[5] = b[5];
    }
    out[2] = b[2]; out[3] = b[3]; out[6] = b[6]; out[7] = b[7];
    if (s === mySeat(st) && st.last && st.last.p && st.last.p[s]) {
      const q = st.last.p[s];
      const since = (now - st.lastFrameAt) / TICK_MS;
      out[2] = q[2]; out[3] = q[3]; out[6] = Math.max(0, q[6] - since); out[7] = q[7];
      out[4] = q[4] > 0 ? Math.max(0, q[4] - since) : 0;
      out[5] = q[5] > 0 ? Math.max(0, q[5] - since) : 0;
      // стрибок видно одразу, не чекаючи відлуння сервера
      if (!out[4] && st.jumpAt && now - st.jumpAt < 260 && f1(st)) out[4] = Math.max(0.01, airOf(st.jumpDir == null ? -1 : st.jumpDir) - (now - st.jumpAt) / TICK_MS);
      if (st.meOk) { out[0] = st.me.x; out[1] = st.me.y; }
    }
    return out;
  }
  const f1 = (st) => st.last && st.last.ph === 1;

  // ---- малювання ----
  function drawTier(st, g, pal, tier, row, sp, cs, now, alpha) {
    if (!row) return;
    const n = st.n;
    const base = tier === 0 ? pal.iceRgb : pal.lowRgb;
    const inset = tier === 0 ? Math.max(0.8, cs * 0.025) : Math.max(1.2, cs * 0.045);
    g.globalAlpha = alpha;
    for (let i = 0; i < row.length; i++) {
      const c = cellOf(row, i);
      if (c < 0) continue;
      const x = PAD + (i % n) * cs, y = PAD + Math.floor(i / n) * cs;
      if (c === 0) { g.drawImage(tier === 0 ? sp.up : sp.low, x, y, cs, cs); continue; }
      // тріщить: білі тріщини → темніє → тремтить і меншає
      const s = 1 - c / CRACK;
      let ox = 0, oy = 0, shrink = 0;
      if (c <= 7 && !reduced()) {
        const j = (1 - c / 7) * cs * 0.035;
        ox = Math.sin(now / 23 + i) * j;
        oy = Math.cos(now / 29 + i * 1.7) * j;
      }
      if (c <= 4) shrink = cs * 0.05 * (1 - c / 4);
      const w = cs - 2 * inset - 2 * shrink;
      const x0 = x + inset + shrink + ox, y0 = y + inset + shrink + oy;
      const dk = clamp((s - 0.35) / 0.65, 0, 1);
      g.fillStyle = mixRgb(base, pal.darkRgb, dk * 0.85);
      rrect(g, x0, y0, w, w, cs * 0.13);
      g.fill();
      const pat = crackOf(st, i * 2 + tier);
      const lines = Math.min(5, 1 + Math.floor(s * 6));
      g.strokeStyle = pal.crack;
      g.lineWidth = Math.max(0.9, cs * 0.03);
      g.lineCap = 'round';
      g.lineJoin = 'round';
      g.globalAlpha = alpha * (0.95 - dk * 0.25);
      g.beginPath();
      for (let l = 0; l < lines; l++) {
        const segs = Math.min(3, 1 + Math.floor(s * 4));
        g.moveTo(x0 + pat[l * 8] * w, y0 + pat[l * 8 + 1] * w);
        for (let k = 1; k <= segs; k++) g.lineTo(x0 + pat[l * 8 + k * 2] * w, y0 + pat[l * 8 + k * 2 + 1] * w);
      }
      g.stroke();
      g.globalAlpha = alpha;
    }
    g.globalAlpha = 1;
  }

  /// Тінь верхнього льоду на нижній: глибина видна крізь дірки.
  function drawShadows(st, g, row, cs, alpha) {
    if (!row || alpha <= 0.02) return;
    const n = st.n, d = cs * 0.09;
    g.fillStyle = 'rgba(4,18,30,' + (0.4 * alpha).toFixed(3) + ')';
    g.beginPath();
    for (let i = 0; i < row.length; i++) {
      if (row.charCodeAt(i) === 35) continue;
      g.rect(PAD + (i % n) * cs + d, PAD + Math.floor(i / n) * cs + d * 1.3, cs, cs);
    }
    g.fill();
  }

  function drawBody(st, g, pal, s, q, cs, now, mine, ghost) {
    const x = toPx(st, q[0]), y = toPx(st, q[1]);
    const low = q[2] === 1;
    let sc = low ? 0.86 : 1;
    if (q[5] > 0) sc = 0.86 + 0.14 * (q[5] / FALL) + 0.04 * Math.sin(now / 40);   // падає на нижній — меншає
    const r = cs * 0.3 * sc;
    const h = q[4] > 0 ? Math.sin((Math.PI * Math.min(1, q[4] / airOf(airDir(q))))) * cs * 0.55 : 0;
    const col = pal.seats[s];
    if (ghost) {
      // під верхнім льодом: силует крізь лід
      g.globalAlpha = 0.42;
      g.fillStyle = col;
      g.beginPath();
      g.arc(x, y, r, 0, Math.PI * 2);
      g.fill();
      g.globalAlpha = 0.7;
      g.setLineDash([3, 3]);
      g.strokeStyle = pal.iceHi;
      g.lineWidth = 1.2;
      g.stroke();
      g.setLineDash([]);
      g.globalAlpha = 1;
      return;
    }
    // тінь — що вище стрибок, то менша й блідіша
    const shK = 1 - h / (cs * 1.1);
    g.fillStyle = 'rgba(6,20,32,' + (0.34 * shK).toFixed(3) + ')';
    g.beginPath();
    g.ellipse(x + r * 0.15, y + r * 0.55, r * 0.95 * shK, r * 0.5 * shK, 0, 0, Math.PI * 2);
    g.fill();
    const by = y - h;
    // тіло: кулька в кольорі місця з відблиском
    const gr = g.createRadialGradient(x - r * 0.35, by - r * 0.4, r * 0.15, x, by, r);
    gr.addColorStop(0, shadeHex(col, 0.45));
    gr.addColorStop(0.55, col);
    gr.addColorStop(1, shadeHex(col, -0.35));
    g.fillStyle = gr;
    g.beginPath();
    g.arc(x, by, r, 0, Math.PI * 2);
    g.fill();
    g.strokeStyle = shadeHex(col, -0.55);
    g.lineWidth = Math.max(1, r * 0.1);
    g.stroke();
    // очі дивляться туди, куди біжить; помпон шапки — ззаду
    const a = (q[7] * Math.PI) / 8, ca = Math.cos(a), sa = Math.sin(a);
    g.fillStyle = '#fff';
    g.beginPath();
    g.arc(x - ca * r * 0.55, by - sa * r * 0.55, r * 0.26, 0, Math.PI * 2);
    g.fill();
    for (const side of [-1, 1]) {
      const ex = x + ca * r * 0.42 - sa * r * 0.34 * side, ey = by + sa * r * 0.42 + ca * r * 0.34 * side;
      g.fillStyle = '#fff';
      g.beginPath();
      g.arc(ex, ey, r * 0.22, 0, Math.PI * 2);
      g.fill();
      g.fillStyle = '#16212a';
      g.beginPath();
      g.arc(ex + ca * r * 0.09, ey + sa * r * 0.09, r * 0.11, 0, Math.PI * 2);
      g.fill();
    }
    if (low) {
      // нижній ярус — трохи в тіні верхнього
      g.fillStyle = 'rgba(6,24,40,.22)';
      g.beginPath();
      g.arc(x, by, r, 0, Math.PI * 2);
      g.fill();
    }
    if (mine) {
      // своє — біле кільце, а по ньому перезарядка стрибка
      const rr = r + Math.max(3, cs * 0.07);
      g.lineWidth = Math.max(2, cs * 0.045);
      g.strokeStyle = 'rgba(255,255,255,.35)';
      g.beginPath();
      g.arc(x, by, rr, 0, Math.PI * 2);
      g.stroke();
      const k = q[6] > 0 ? 1 - q[6] / JUMP_CD : 1;
      g.strokeStyle = k >= 1 ? '#fff' : pal.iceEdge;
      g.beginPath();
      g.arc(x, by, rr, -Math.PI / 2, -Math.PI / 2 + k * Math.PI * 2);
      g.stroke();
    }
  }

  function drawLabels(st, g, pal, list, cs) {
    const me = mySeat(st);
    const many = st.n >= 14;
    for (const { s, q } of list) {
      const mine = s === me;
      if (many && !mine) continue;
      // бот — «🤖 рудий», а не «🤖 бот р…»: над тілом місця на вісім знаків
      const nm = mine ? 'ти' : String(st.nicks[s] || '').replace(/^🤖\s*бот\s*/u, '🤖').slice(0, 8);
      if (!nm) continue;
      const h = q[4] > 0 ? Math.sin((Math.PI * Math.min(1, q[4] / airOf(airDir(q))))) * cs * 0.55 : 0;
      g.globalAlpha = q[2] === 1 && st.focus < 0.5 ? 0.6 : 1;
      text(g, nm, toPx(st, q[0]), toPx(st, q[1]) - h - cs * 0.5, (mine ? 13 : 11) * st.textK, mine ? '#fff' : pal.seats[s], 800);
    }
    g.globalAlpha = 1;
  }

  function shade(g, a) {
    g.fillStyle = 'rgba(6,18,28,' + a + ')';
    g.fillRect(0, 0, SIZE, SIZE);
  }

  function overlays(st, g, pal, f, now) {
    const v = st.view || {};
    const ctx = st.ctx;
    // на телефоні ставок утричі менший за логічний — написи збільшуємо довкола центру, щоб читались
    const k = Math.min(1.45, st.textK);
    g.translate(SIZE / 2, SIZE / 2);
    g.scale(k, k);
    g.translate(-SIZE / 2, -SIZE / 2);
    if (f.ph === 0) {
      shade(g, 0.28);
      const n = secs(f.left || 0);
      const first = (v.round || 1) === 1;
      text(g, v.party ? 'Тонкий лід' : 'Раунд ' + (v.round || 1) + ' з ' + (v.rounds || 3), SIZE / 2, SIZE / 2 - 84, 26, '#fff', 800);
      text(g, n > 0 ? String(n) : 'Біжи!', SIZE / 2, SIZE / 2, 86, '#fff', 900);
      if (first) {
        text(g, 'Лід тріщить під ногами — не стій на місці', SIZE / 2, SIZE / 2 + 78, 17, pal.ice, 700);
        text(g, 'Провалився — нижній ярус, звідти — вода', SIZE / 2, SIZE / 2 + 104, 15, pal.ice, 600);
      }
    } else if (f.ph === 2 || f.ph === 3) {
      shade(g, 0.42);
      results(st, g, pal, v, ctx, f.ph === 3);
    }
    // банер угорі
    const b = st.banner;
    if (b) {
      const age = now - b.at;
      if (age > b.ms) st.banner = null;
      else {
        const k = Math.min(1, age / 180, (b.ms - age) / 300);
        g.globalAlpha = k;
        g.fillStyle = 'rgba(6,18,28,.72)';
        rrect(g, SIZE / 2 - 200, 26, 400, b.sub ? 66 : 46, 14);
        g.fill();
        text(g, b.text, SIZE / 2, 49, 24, '#fff', 900);
        if (b.sub) text(g, b.sub, SIZE / 2, 76, 14, pal.ice, 600);
        g.globalAlpha = 1;
      }
    }
  }

  /// Підсумок раунду чи партії на ставку: хто виграв і табличка місць.
  function results(st, g, pal, v, ctx, final) {
    const rows = [];
    const lr = v.lastRound;
    for (let s = 0; s < 8; s++) {
      if (v.points && v.points[s] != null) rows.push({ s, pts: v.points[s], wins: (v.wins && v.wins[s]) || 0, rank: lr && lr.ranks ? lr.ranks[s] : 0 });
    }
    if (!rows.length) return;
    const name = (s) => st.nicks[s] || ctx.seatName(s);
    let title;
    if (final) {
      const ws = v.winners || [];
      title = ws.length === 1 ? '🏆 ' + name(ws[0]) : ws.length ? '🤝 ' + ws.map(name).join(', ') : 'Нічия';
      rows.sort((a, b) => b.pts - a.pts || b.wins - a.wins || a.s - b.s);
    } else {
      const w = lr ? lr.winner : -1;
      title = w >= 0 ? (v.party ? 'Останній на льоду — ' : 'Раунд — ') + name(w) + '!' : lr && lr.byTime ? 'Час вийшов — лід витримав кількох' : 'Усі шубовснули разом';
      rows.sort((a, b) => b.rank - a.rank || b.pts - a.pts || a.s - b.s);
    }
    const top = SIZE / 2 - (rows.length * 30 + 60) / 2;
    text(g, title, SIZE / 2, top + 14, title.length > 26 ? 22 : 28, '#fff', 900);
    if (final && !v.party) text(g, 'Партію зіграно', SIZE / 2, top + 42, 14, pal.ice, 600);
    let y = top + 70;
    let place = 0, prevKey = null;
    rows.forEach((r, i) => {
      const key = final ? r.pts + ':' + r.wins : r.rank;
      if (key !== prevKey) place = i + 1;
      prevKey = key;
      g.fillStyle = r.s === mySeat(st) ? 'rgba(255,255,255,.14)' : 'rgba(255,255,255,.05)';
      rrect(g, SIZE / 2 - 170, y - 13, 340, 26, 8);
      g.fill();
      text(g, place + '.', SIZE / 2 - 150, y, 15, '#fff', 800, 'left');
      g.fillStyle = pal.seats[r.s];
      g.beginPath();
      g.arc(SIZE / 2 - 112, y, 7, 0, Math.PI * 2);
      g.fill();
      text(g, String(name(r.s)).slice(0, 16), SIZE / 2 - 98, y, 15, '#fff', 700, 'left');
      const right = v.party ? '' : (final ? r.pts + ' оч.' + (r.wins ? ' · 🏆' + r.wins : '') : '+' + r.rank + ' · Σ ' + r.pts);
      if (right) text(g, right, SIZE / 2 + 158, y, 14, pal.ice, 700, 'right');
      y += 30;
    });
  }

  function draw(st, now) {
    const cv = st.cv;
    const g = cv.ctx;
    const pal = palette(st);
    const f = st.last;
    const dt = Math.min(64, st.drawAt ? now - st.drawAt : 16);
    st.drawAt = now;
    const ppu = cv.el.width / SIZE;
    // написи на ставку — не дрібніші за ~11 css-px, хоч би яким малим був ставок
    st.textK = clamp(SIZE / Math.max(200, cv.el.clientWidth || SIZE), 1, 1.8);
    g.setTransform(1, 0, 0, 1, 0, 0);
    g.clearRect(0, 0, cv.el.width, cv.el.height);
    let sx = 0, sy = 0;
    if (st.shake > 0.2 && !reduced()) {
      sx = (Math.random() - 0.5) * st.shake;
      sy = (Math.random() - 0.5) * st.shake;
      st.shake *= Math.pow(0.86, dt / 16);
    } else st.shake = 0;
    g.setTransform(ppu, 0, 0, ppu, sx * ppu, sy * ppu);
    g.drawImage(background(st, pal, ppu), 0, 0, SIZE, SIZE);
    if (!f || !f.ice) return;
    const cs = cellPx(st);
    // брижі на воді — повільні, щоб ставок жив і в паузах між діями
    if (!reduced()) {
      g.strokeStyle = 'rgba(160,210,240,.10)';
      g.lineWidth = 1.5;
      for (let i = 0; i < 7; i++) {
        const t = (now / 5200 + i / 7) % 1;
        const x = PAD + ((i * 0.37 + 0.13) % 1) * (SIZE - 2 * PAD), y = PAD + ((i * 0.61 + 0.29) % 1) * (SIZE - 2 * PAD);
        g.globalAlpha = Math.sin(t * Math.PI);
        g.beginPath();
        g.ellipse(x, y, 8 + t * 40, 4 + t * 18, 0, 0, Math.PI * 2);
        g.stroke();
      }
      g.globalAlpha = 1;
    }
    const sp = sprites(st, pal, ppu);
    // фокус: сам на нижньому (чи на верхньому вже нікого) — верхній лід стає примарним, щоб бачити, куди ступати
    const me = mySeat(st);
    const mq = me >= 0 && f.p ? f.p[me] : null;
    let want = 0;
    if (mq && (mq[3] & 1)) want = mq[2] === 1 ? 1 : 0;
    else if (f.p && f.ph <= 2) {
      let top = 0, low = 0;
      for (const q of f.p) if (q && (q[3] & 1)) { if (q[2] === 0) top++; else low++; }
      want = top === 0 && low > 0 ? 1 : 0;
    }
    st.focus += (want - st.focus) * (1 - Math.exp(-dt / 220));
    const upA = 1 - 0.88 * st.focus;
    // тіла на зараз
    const it = st.interp.at() || { a: f, b: f, t: 1 };
    predictMe(st, now, dt);
    const list = [];
    for (let s = 0; s < 8; s++) {
      if (!st.bodyBuf[s]) st.bodyBuf[s] = new Array(8);
      const q = bodyAt(st, s, it, now, st.bodyBuf[s]);
      if (!q || !(q[3] & 1)) continue;
      list.push({ s, q });
    }
    list.sort((a, b) => a.q[1] - b.q[1]);
    const lowFirst = st.focus < 0.5;
    drawTier(st, g, pal, 1, f.ice[1], sp, cs, now, 1);
    if (lowFirst) for (const { s, q } of list) if (q[2] === 1 && q[5] <= 0) drawBody(st, g, pal, s, q, cs, now, s === me, false);
    drawShadows(st, g, f.ice[0], cs, upA);
    drawTier(st, g, pal, 0, f.ice[0], sp, cs, now, upA);
    // нижні: під цілим верхнім льодом — силуетом крізь лід; у фокусі (сам унизу) — повністю, поверх примарного верху
    for (const { s, q } of list) {
      if (q[2] !== 1 || q[5] > 0) continue;
      if (!lowFirst) drawBody(st, g, pal, s, q, cs, now, s === me, false);
      else if (!openAbove(st, f, q)) drawBody(st, g, pal, s, q, cs, now, s === me, true);
    }
    // падають (fall > 0) і верхні — поверх
    for (const { s, q } of list) if (q[2] === 1 && q[5] > 0) drawBody(st, g, pal, s, q, cs, now, s === me, false);
    for (const { s, q } of list) if (q[2] === 0) drawBody(st, g, pal, s, q, cs, now, s === me, false);
    drawParts(st, g, dt);
    drawLabels(st, g, pal, list, cs);
    drawPops(st, g, now);
    g.setTransform(ppu, 0, 0, ppu, 0, 0);
    if (f.ph !== 4) overlays(st, g, pal, f, now);
  }

  /// Над тілом нижнього ярусу дірка у верхньому — тоді його видно повністю, а не силуетом крізь лід.
  function openAbove(st, f, q) {
    const n = st.n;
    const cx = clamp(Math.floor(q[0] / SUB), 0, n - 1), cy = clamp(Math.floor(q[1] / SUB), 0, n - 1);
    return f.ice[0].charCodeAt(cy * n + cx) === 35;
  }

  // ---- ввід ----
  function currentWant(st) {
    if (st.stickA != null) return st.stickA;
    if (st.padA != null) return st.padA;
    return keySector();
  }
  function canSend(st) {
    const ctx = st.ctx;
    if (!(ctx && ctx.mine && ctx.playing)) return false;
    const ph = phaseOf(st);
    if (ph > 1) return false;
    const q = st.last && st.last.p ? st.last.p[ctx.seat] : null;
    return !!q && !(q[3] & 4);
  }
  /// Шлемо лише зміну сектора і не частіше 20/с; остання зміна досилається з rAF. keep — той самий сектор ще раз.
  function push(st, force, keep) {
    if (!canSend(st)) { st.sent = null; return; }
    const a = currentWant(st);
    if (!force && st.sent === a) return;
    const now = performance.now();
    if (!force && now - st.sentAt < SEND_MS) return;
    st.sent = a;
    st.sentAt = now;
    st.ctx.input('move', { a });
    if (a >= 0 && !keep) st.echo = { a, at: now };
  }
  function jump(st) {
    if (!canSend(st) || phaseOf(st) !== 1) return;
    st.ctx.input('jump');
    const q = st.last.p[st.ctx.seat];
    const since = (performance.now() - st.lastFrameAt) / TICK_MS;
    if (q && q[4] === 0 && q[5] === 0 && q[6] - since <= 0.5) { st.jumpAt = performance.now(); st.jumpDir = currentWant(st); Snd.jump(); }
  }
  function readPad(st) {
    if (!window.HPad || !(HPad.pads > 0) || !navigator.getGamepads) { if (st.padA != null) { st.padA = null; push(st); } return; }
    const list = HPad.list ? HPad.list() : (navigator.getGamepads() || []);   // без керма й лише у фокусі
    let gp = null;
    for (let i = 0; i < list.length; i++) if (list[i] && list[i].connected !== false) { gp = list[i]; break; }
    if (!gp || !gp.axes || gp.axes.length < 2) { st.padA = null; return; }
    const x = gp.axes[0] || 0, y = gp.axes[1] || 0, m = Math.hypot(x, y);
    let a = st.padA;
    if (m >= 0.5) a = sectorOf(x, y);
    else if (m < 0.35) a = null;
    if (a !== st.padA) { st.padA = a; push(st); }
  }

  // ---- рядок над ставком ----
  function hudEl(root) {
    let el = root.querySelector(':scope > .tihud');
    if (el) return el;
    el = document.createElement('div');
    el.className = 'tihud';
    el.addEventListener('click', (e) => {
      if (!e.target.closest('[data-snd]')) return;
      Snd.set(!Snd.on);
      const s = root._thinice;
      if (s) { s.hudSig = ''; hud(root, s); }
    });
    root.insertBefore(el, root.firstChild);
    return el;
  }

  function hudKey(f) {
    let k = (f.ph + 1) * 7 + (f.thaw ? 3 : 0) + Math.floor(((f.left || 0) * TICK_MS) / 1000) * 64 + Math.floor((f.rt || 0) / 25) * 4096;
    if (f.p) for (let i = 0; i < 8; i++) { const q = f.p[i]; k = (k * 31 + (q ? q[2] * 8 + q[3] + 1 : 0)) % 1000000007; }
    return k;
  }

  function hud(root, st) {
    const ctx = st.ctx;
    const el = hudEl(root);
    const v = ctx.view || {};
    const f = st.last;
    let html = '';
    let seated = 0;
    for (let s = 0; s < 8; s++) if (f && f.p && f.p[s]) seated++;
    // тісно: на п'ятьох і більше (а на телефоні — вже на чотирьох) чужі місця — лише номером, свій — із ніком
    const tight = seated > 4 || (seated > 3 && root.clientWidth < 480);
    for (let s = 0; s < 8; s++) {
      const q = f && f.p ? f.p[s] : null;
      const nm = nameAt(ctx, s) || (q ? ctx.seatName(s) : null);
      if (!nm || !q) continue;
      st.nicks[s] = nm;
      const playing = v.phase && v.phase !== 'lobby';
      let state = '';
      if (playing && f.ph <= 2) state = (q[3] & 4) ? '🌊' : q[2] === 1 ? '⬇' : '';
      const pts = playing && !v.party && v.points && v.points[s] != null ? ' <b class="tipts">' + v.points[s] + '</b>' : '';
      html += '<span class="tichip ti' + s + ((q[3] & 4) && playing ? ' out' : '') + (s === ctx.seat ? ' me' : '') + '" title="' + ctx.esc(nm) + '">'
        + '<i>' + (s + 1) + '</i>' + (tight && s !== ctx.seat ? '' : '<span class="tinick">' + ctx.esc(nm) + '</span>')
        + pts + (state ? ' <span class="tist">' + state + '</span>' : '') + '</span>';
    }
    let tail = '';
    if (f && ctx.playing && f.ph === 1) {
      // Годинник завжди лічить донизу: до відлиги, потім — до кінця раунду (стеля).
      const thawAt = f.ta || v.thawAt || 1500, toThaw = thawAt - (f.rt || 0);
      if (f.thaw) tail += '<span class="tichip ticlock hot">🌡 кінець за ' + clock(f.left || 0) + '</span>';
      else tail += '<span class="tichip ticlock' + (toThaw <= 250 ? ' hot' : '') + '">🌡 відлига за ' + clock(Math.max(0, toThaw)) + '</span>';
    }
    if (v.round && v.phase !== 'lobby' && !v.party) tail += '<span class="tichip tiround">раунд ' + v.round + '/' + (v.rounds || 3) + '</span>';
    if (!ctx.embedded) tail += '<button type="button" class="tichip tisnd" data-snd data-pad-skip title="' + (Snd.on ? 'Вимкнути звук' : 'Увімкнути звук') + '">' + (Snd.on ? '🔊' : '🔇') + '</button>';
    const sig = html + '|' + tail;
    if (st.hudSig === sig) return;
    st.hudSig = sig;
    el.innerHTML = html + tail;
  }

  // ---- розмір і телефон ----
  /// Ноутбук: ставок — скільки лишається від його верху до низу вікна (мінус кнопки картки під ним), 300..680.
  /// Телефон — на всю ширину, сторінку в кадр підкручує fitView.
  function fit(root, st) {
    const cv = st.cv && st.cv.el;
    if (!cv || !cv.isConnected || !cv.offsetParent) return;
    const phone = window.innerWidth < 700 || HGames.ui.coarse();
    if (phone) {
      if (cv.style.maxWidth) cv.style.maxWidth = '';
      return;
    }
    const card = root.parentElement || root;
    const r = cv.getBoundingClientRect();
    const top = r.top + window.scrollY;
    const cb = card.getBoundingClientRect().bottom;
    const gap = Math.max(0, Math.min(24, document.documentElement.scrollHeight - (cb + window.scrollY)));
    const below = Math.max(0, cb - r.bottom) + gap;
    const big = window.innerWidth >= 1500 && window.innerHeight >= 860;
    let size = Math.floor(window.innerHeight - top - below);
    size = Math.max(300, Math.min(size, big ? 760 : 680, root.clientWidth || 680));
    const cur = parseFloat(cv.style.maxWidth);
    if (!(Math.abs(cur - size) < 2)) cv.style.maxWidth = size + 'px';
  }

  function fitNow() {
    if (HGames.ui.fit) return HGames.ui.fit();
    const vv = window.visualViewport;
    return { w: vv ? vv.width : window.innerWidth, h: vv ? vv.height : window.innerHeight, top: 0, dock: 64 };
  }
  /// Телефон: на старті партії підкручуємо сторінку так, щоб ставок і стік стали між шапкою й нижніми панелями.
  function fitView(root, st) {
    if (!st.cv || !HGames.ui.coarse() || !st.cv.el.isConnected) return;
    const a = st.cv.el.getBoundingClientRect();
    const ctl = root.querySelector(':scope > .ticl');
    const h = (ctl ? ctl.getBoundingClientRect().bottom : a.bottom) - a.top;
    const fv = fitNow();
    const top = fv.top + 4, bottom = fv.h - fv.dock - 4;
    const want = h <= bottom - top ? top + (bottom - top - h) / 2 : bottom - h;
    const d = a.top - want;
    if (Math.abs(d) > 24) window.scrollBy({ top: d, behavior: reduced() ? 'auto' : 'smooth' });
  }

  /// Палець: стік ліворуч, «стрибок» праворуч. Лише поки йде партія — у лобі й після кінця вони штовхали б кнопки
  /// картки під нижнє меню телефона.
  function controls(root, st) {
    const ctx = st.ctx;
    let el = root.querySelector(':scope > .ticl');
    const want = ctx.mine && ctx.playing && HGames.ui.coarse() && !!(ctx.view && ctx.view.phase !== 'lobby');
    if (!want) {
      if (el) { el.remove(); st.stickA = null; push(st); st.cdBucket = -2; }
      return;
    }
    if (el) return;
    el = document.createElement('div');
    el.className = 'ticl';
    el.innerHTML = '<div class="tistick" aria-label="стік: куди бігти"><div class="tiknob"></div></div>'
      + '<button type="button" class="tijump" aria-label="стрибок через дірку"><span>⤴</span><small>стрибок</small></button>';
    const stick = el.querySelector('.tistick'), knob = el.querySelector('.tiknob');
    let pid = null;
    const move = (e) => {
      const r = stick.getBoundingClientRect();
      const dx = e.clientX - (r.left + r.width / 2), dy = e.clientY - (r.top + r.height / 2);
      const m = Math.hypot(dx, dy), lim = r.width / 2 - 22;
      const k = m > lim ? lim / m : 1;
      knob.style.transform = 'translate(' + (dx * k).toFixed(1) + 'px,' + (dy * k).toFixed(1) + 'px)';
      const a = m < 12 ? null : sectorOf(dx, dy);
      const s = root._thinice;
      if (s && a !== s.stickA) { s.stickA = a; push(s); }
    };
    stick.addEventListener('pointerdown', (e) => {
      e.preventDefault();
      pid = e.pointerId;
      try { stick.setPointerCapture(pid); } catch { /* без capture */ }
      move(e);
    });
    stick.addEventListener('pointermove', (e) => { if (e.pointerId === pid) move(e); });
    const up = (e) => {
      if (e.pointerId !== pid) return;
      pid = null;
      knob.style.transform = '';
      const s = root._thinice;
      if (s) { s.stickA = null; push(s); }
    };
    stick.addEventListener('pointerup', up);
    stick.addEventListener('pointercancel', up);
    stick.addEventListener('lostpointercapture', up);
    el.querySelector('.tijump').addEventListener('pointerdown', (e) => { e.preventDefault(); const s = root._thinice; if (s) jump(s); });
    el.addEventListener('contextmenu', (e) => e.preventDefault());
    const cv = st.cv && st.cv.el;
    if (cv && cv.nextSibling) root.insertBefore(el, cv.nextSibling); else root.appendChild(el);
    st.cdBucket = -2;
  }

  /// Кнопка стрибка заповнюється, поки йде перезарядка (CSS-змінна --cd 0..1), і світиться, коли готова.
  function jumpButton(root, st, now) {
    const el = root.querySelector(':scope > .ticl .tijump');
    if (!el) return;
    const s = mySeat(st), q = st.last && st.last.p && s >= 0 ? st.last.p[s] : null;
    let k = 1;
    if (!q || !(q[3] & 1) || q[4] > 0 || q[5] > 0 || phaseOf(st) !== 1) k = -1;
    else if (q[6] > 0) k = Math.floor(clamp(1 - (q[6] - (now - st.lastFrameAt) / TICK_MS) / JUMP_CD, 0, 1) * 20) / 20;
    if (k === st.cdBucket) return;
    st.cdBucket = k;
    el.style.setProperty('--cd', String(Math.max(0, k)));
    el.classList.toggle('ready', k >= 1);
    el.classList.toggle('off', k < 0);
  }

  // ---- цикл ----
  function spin(root, st) {
    st.awakeUntil = performance.now() + AWAKE_MS;
    if (st.raf) return;
    const loop = () => {
      if (!st.cv || !st.cv.el.isConnected) { st.raf = 0; return; }
      if (!(st.ctx && st.ctx.playing) && performance.now() > st.awakeUntil && !st.parts.length && !st.banner) { st.raf = 0; return; }
      st.raf = requestAnimationFrame(loop);
      const now = performance.now();
      readPad(st);
      if (st.sent !== currentWant(st) && canSend(st) && now - st.sentAt >= SEND_MS) push(st);
      else if (st.sent != null && st.sent >= 0 && canSend(st) && now - st.sentAt >= KEEP_MS) push(st, true, true);
      if (!st.cv.el.offsetParent || document.hidden) return;
      if (now - (st.fitAt || 0) > 500) { st.fitAt = now; fit(root, st); }
      jumpButton(root, st, now);
      draw(st, now);
    };
    st.raf = requestAnimationFrame(loop);
  }

  // ---- вид ----
  function takeView(st, v) {
    st.view = v || null;
    if (v && v.n) {
      if (v.n !== st.n) { st.cracks.clear(); st.sprites = null; st.bg = null; }
      st.n = v.n;
    }
  }

  function overText(ctx) {
    const res = ctx.room && ctx.room.status === 'finished' && ctx.room.result;
    if (!res) return '';
    const ws = res.winners || [];
    if (res.draw || !ws.length) return 'Партію зіграно — нічия';
    return 'Перемога: ' + ws.map((i) => nameAt(ctx, i) || ctx.seatName(i)).join(', ');
  }

  function howText() {
    return padOn() ? 'Стік — бігти, Ⓐ — стрибок через дірку'
      : HGames.ui.coarse() ? 'Стік — бігти, ⤴ — стрибок через дірку' : 'Стрілки/WASD — бігти, пробіл — стрибок через дірку';
  }

  function statusText(ctx) {
    const st = [...live].find((s) => s.ctx === ctx);
    const v = ctx.view || {};
    const f = (st && st.last) || ctx.frame || v.frame;
    const rr = v.party ? '' : ' · раунд ' + (v.round || 1) + '/' + (v.rounds || 3);
    if (!ctx.playing) {
      if (ctx.room && ctx.room.status === 'lobby') {
        const host = ctx.room.host && ctx.me && String(ctx.room.host).toLowerCase() === String(ctx.me.nick).toLowerCase();
        return host ? 'Тисни «Почати», коли всі сіли (2–8). Сам — клич «🤖 + бот»' : 'Лід тріщить під ногами. Стартує господар, коли зібралось 2–8';
      }
      return overText(ctx);
    }
    if (st && st.lastFrameAt && performance.now() - st.lastFrameAt > 1000 && f && f.ph === 1) return '⏳ зв\'язок…';
    if (!f) return '';
    if (f.ph === 0) return 'Готуйсь…' + rr + ' · ' + howText();
    if (f.ph === 2 || f.ph === 3) {
      const lr = v.lastRound, w = lr ? lr.winner : -1;
      return w >= 0 ? (v.party ? 'Останній на льоду — ' : 'Раунд — ') + (nameAt(ctx, w) || ctx.seatName(w)) + '!' : lr && lr.byTime ? 'Час вийшов — нічия раунду' : 'Нічия раунду';
    }
    if (!ctx.mine) return 'Дивишся збоку' + rr;
    const q = f.p && f.p[ctx.seat];
    if (q && (q[3] & 4)) return '🌊 Шубовсь! Дивись, хто лишиться останнім' + rr;
    const low = q && q[2] === 1 ? '⬇ Ти на нижньому — під ним вода! · ' : '';
    const cd = q && q[6] > 0 ? ' · стрибок за ' + secs(q[6]) + ' с' : ' · стрибок готовий';
    return low + howText() + cd + rr;
  }

  HGames.register({
    id: 'thinice',
    added: '2026-10-06',
    icon: ICON,
    seatNames: ['синій', 'рудий', 'зелений', 'жовтий', 'бузковий', 'м’ятний', 'рожевий', 'сірий'],
    seatClass: ['ti0', 'ti1', 'ti2', 'ti3', 'ti4', 'ti5', 'ti6', 'ti7'],
    pad: { dirs: true, a: 'Space', hint: '{dpad} бігти · {a} стрибок через дірку' },

    mount(root, ctx) {
      const st = state(root, ctx);
      takeView(st, ctx.view);
      const vf = (ctx.view && ctx.view.frame) || ctx.frame;
      if (vf) { st.last = vf; st.interp.push(vf); }
      hud(root, st);
      st.cv = HGames.ui.canvas(root, { w: SIZE * st.K, h: SIZE * st.K, cls: 'tiboard' });
      st.cv.el.classList.toggle('play', !!ctx.mine);
      controls(root, st);
      st.onResize = () => { const s = root._thinice; if (s) { s.cv.resize(); fit(root, s); spin(root, s); } };
      window.addEventListener('resize', st.onResize);
      if (HGames.ui.onFit) HGames.ui.onFit(root, () => st.onResize());
      fit(root, st);
      spin(root, st);
    },

    update(root, ctx) {
      const st = state(root, ctx);
      const v = ctx.view;
      takeView(st, v);
      // Вид — правда поза грою (лобі, кінець партії, F5); посеред гри кадри свіжіші.
      const vf = v && v.frame;
      if (vf && (!ctx.playing || !st.last || v.phase === 'lobby' || v.phase === 'over' || vf.t > (st.last.t || 0) + 2)) {
        st.last = vf;
        st.interp.reset();
        st.interp.push(vf);
        st.meOk = false;
      }
      if (!st.cv) return;
      st.cv.el.classList.toggle('play', !!ctx.mine);
      st.cv.resize();
      hud(root, st);
      controls(root, st);
      fit(root, st);
      // «Ще раз» і F5: сервер не знає про клавішу, яку не відпускали — досилаємо намір
      if (ctx.playing && ctx.mine && !st.was) { st.sent = null; push(st, true); setTimeout(() => fitView(root, st), 60); }
      if (ctx.playing && !ctx.mine && !st.watchFit) { st.watchFit = true; setTimeout(() => fitView(root, st), 60); }
      if (!ctx.playing) { st.sent = null; st.meOk = false; st.watchFit = false; }
      st.was = !!(ctx.playing && ctx.mine);
      spin(root, st);
    },

    frame(root, ctx, f) {
      const st = state(root, ctx);
      if (!f) return;
      if (st.last && f.t < st.last.t - 2) st.interp.reset();
      const ph = f.ph;
      if (ph !== st.lastPh) {
        st.phAt = performance.now();
        if (ph === 0) { st.interp.reset(); st.meOk = false; st.parts.length = 0; st.banner = null; }
        if (ph === 2 && st.lastPh === 1) Snd.round();
        st.lastPh = ph;
        // стік і стрибок — і з кадру: після F5 вид може прийти раніше, ніж картка дізнається своє місце
        controls(root, st);
      }
      // відлуння наміру: сервер уже біжить туди, куди ми сказали, — так міряємо RTT для свого тіла
      const s = mySeat(st);
      const q = s >= 0 && f.p ? f.p[s] : null;
      if (st.echo && q && ph === 1) {
        const age = performance.now() - st.echo.at;
        if (age > 1000) st.echo = null;
        else if (q[7] === st.echo.a && (q[3] & 2)) { st.rtt = st.rtt * 0.7 + age * 0.3; st.echo = null; }
      }
      events(st, f);
      st.last = f;
      st.lastFrameAt = performance.now();
      st.interp.push(f);
      const hk = hudKey(f);
      if (hk !== st.hudK) { st.hudK = hk; hud(root, st); }
      spin(root, st);
    },

    onKey(e, ctx) {
      const st = [...live].find((s) => s.ctx === ctx);
      if (!st || !ctx.mine) return false;
      const k = keyOf(e);
      // Утримання пам'ятаємо й поза грою: затиснув стрілку ще на відліку — поїдеш зі старту.
      if (k && !held[k]) { held[k] = true; push(st); }
      if (!ctx.playing) return false;
      if (k) return true;
      if (isJump(e)) { if (!e.repeat) jump(st); return true; }
      return false;
    },

    status: statusText,

    unmount(root) {
      const st = root._thinice;
      if (!st) return;
      cancelAnimationFrame(st.raf);
      st.raf = 0;
      if (st.onResize) window.removeEventListener('resize', st.onResize);
      if (HGames.ui.onFit) HGames.ui.onFit(root, null);
      live.delete(st);
      root._thinice = null;
    },
  });
})();
