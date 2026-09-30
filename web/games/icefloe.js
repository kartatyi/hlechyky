/*
  Крижина — сумо на крижині посеред ставка (Impl/Icefloe.cs, IcefloeCore.cs; spec docs/games/specs/icefloe.md).

  Вид (раз на подію): { phase, round, need, roundsMax, wins[8], pushouts[8], ice: { r0, v[24], iv }, pond, shore,
    bank, bodyR, lastRound: { winner, by }, out, winner, winners, series, frame }.
  Кадр (25 Гц у грі): { t, ph, left, melt, iv, crack: [s, L] | null,
    p: [8 × [x, y, vx, vy, face, fl, cd, ammo] | null], s: [[id, x, y, vx, vy]], k: [[x, y, kind]], ev: [[код, …]] }.
    fl: 1 живий · 2 шипи · 4 глек · 8 удар ривка · 16 на березі · 32 тримає напрямок · 64 ще може колоти ·
    128 ковзани · 256 кулак · 512 іній.
    kind: 0 шипи · 1 глек · 2 сніжки · 3 завірюха · 4 ковзани · 5 іній · 6 кулак.
  Ввід: Input('move', { a: −1..15 }) — сектор по 22.5° (0 праворуч, за годинниковою), Input('dash'), Input('throw').

  Своє тіло не чекає сервера: між кадрами його веде та сама формула, що й сервер (glide — рівно IcefloeCore.Glide,
  тест Prediction_matches_the_browser_fixture + docs/games/dev/arena-predict.py), а кадр лише м'яко підправляє.
  Чужих — інтерполюємо на один інтервал позаду, як у танчиках.
*/
(() => {
  // ---- фізика: ті самі числа й той самий порядок операцій, що в IcefloeCore/ArenaPhysics ----
  const H = 0.02, SUB_MS = 20, TICK_MS = 40;
  const VMAX = 1100, MU = 2.0, SPIKE_MU = 6.0, SKATE_MU = 1.2, DASH = 520, FIST_DASH = 832, DASH_CD = 25;
  const THRUST = 1800, JUG_THRUST = 1600, FAST_THRUST = 2700, JUG_FAST_THRUST = 2400;   // IcefloeBody.Thrust — таблицею
  const GUST_R = 450;                          // IcefloeCore.GustR — кого зачепила завірюха
  const CAP_TICKS = 1875, MELT_FROM = 1300;   // IcefloeCore.CapTicks, IcefloeCore.MeltFrom
  const C1 = 0.9238795325112867, S1 = 0.3826834323650898, D = 0.7071067811865476;
  const COS16 = [1, C1, D, S1, 0, -S1, -D, -C1, -1, -C1, -D, -S1, 0, S1, D, C1];
  const SIN16 = [0, S1, D, C1, 1, C1, D, S1, 0, -S1, -D, -C1, -1, -C1, -D, -S1];

  /// Один підкрок тіла: тяга → рух → тертя → стеля. Рівно IcefloeCore.Glide + ArenaPhysics.Integrate.
  function glide(b, want, mu, thrust) {
    if (want >= 0) {
      const k = thrust * H;
      b.vx += k * COS16[want];
      b.vy += k * SIN16[want];
    }
    b.x += b.vx * H;
    b.y += b.vy * H;
    const k = 1 - mu * H;
    b.vx *= k;
    b.vy *= k;
    const s2 = b.vx * b.vx + b.vy * b.vy;
    if (s2 > VMAX * VMAX) {
      const c = VMAX / Math.sqrt(s2);
      b.vx *= c;
      b.vy *= c;
    }
  }
  /// Вектор → сектор 0..15 (−1 — нуль).
  const sectorOf = (dx, dy) => (dx === 0 && dy === 0 ? -1 : ((Math.round(Math.atan2(dy, dx) / (Math.PI / 8)) % 16) + 16) % 16);
  // Для перевірки «C# = JS» (docs/games/dev/arena-predict.py). Префікс гри — щоб не зіткнутись ні з ким.
  window.IcefloeSim = { glide, sectorOf, COS16, SIN16 };

  // ---- вигляд ----
  const SIZE = 600;                    // логічний канвас: увесь ставок
  const SEND_MS = 50;                  // наміри — не частіше 20/с
  // Поки напрямок тримають, досилаємо його раз на 0.4 с: сервер без підтвердження гасить тягу за 1.2 с
  // (Icefloe.KeepTicks) — зв'язок пропав, а тіло не їде саме у воду.
  const KEEP_MS = 400;
  // Камера наближається, коли крига меншає: наприкінці раунду п'ятачок льоду — на весь канвас, а не цятка.
  const CAM_MARGIN = 170;              // см від найдальшого краю криги (чи тіла) до краю кадру
  const ZOOM_MAX = 2.2;
  const ZOOM_MS = 700;                 // стала часу плавного наїзду
  const TEAM_NAMES = ['🔵 сині', '🔴 руді'];
  const SEAT_VARS = [['--if-s0', '#5aa9ff'], ['--if-s1', '#d9825b'], ['--if-s2', '#7bd389'], ['--if-s3', '#f4c542'],
    ['--if-s4', '#b48cf2'], ['--if-s5', '#6fd6c2'], ['--if-s6', '#f08cb8'], ['--if-s7', '#b7c2bd']];
  const PICK_GLYPH = ['🥾', '🏺', '❄', '🌬', '⛸', '🧊', '💪'];
  const DASH3 = [3, 3], DASH5 = [5, 5], NO_DASH = [];
  const ringDash = [0, 0];                                 // рятувальне коло: довжини залежать від розміру
  const PICK_NAME = ['🥾 шипи', '🏺 глек', '❄ дві сніжки', '🌬 завірюха!', '⛸ ковзани', '🧊 іній!', '💪 кулак'];
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M4 2.5 10.5 2 14 5.5 13.5 11 9.5 14 3.5 13 1.5 8.5 2 4.5Z" fill="var(--if-ice, #9fd7ff)"/>'
    + '<circle cx="6" cy="7" r="2.1" fill="var(--clay)"/><circle cx="10.2" cy="9" r="2.1" fill="var(--ok)"/>'
    + '<path d="M1 15c1.5-1.2 2.5-1.2 4 0s2.5 1.2 4 0 2.5-1.2 4 0" stroke="var(--text)" stroke-width="1.1" fill="none"/></svg>';

  /// Грають падом (Steam Deck чи джойстик) — підказки в статусі кнопками пада, а не клавіатурою.
  const padOn = () => !!(window.HPad && HPad.pads > 0);
  const reduced = () => !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
  const scale = () => ((window.devicePixelRatio || 1) >= 2 ? 1 : 2);   // на DPR 1 малюємо вдвічі щільніше
  const clock = (ticks) => {
    const s = Math.max(0, Math.floor((ticks * TICK_MS) / 1000));
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  };

  // ---- звук: коротенькі «хрусь» і «шубовсь», лише синтез, після жесту ----
  const Snd = {
    on: (() => { try { return localStorage.getItem('icefloe.sound') !== '0'; } catch { return true; } })(),
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
    /// Шум через фільтр: удар, тріск, бризки.
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
    bump(s) { this.noise(40, 1800, Math.min(0.05, 0.015 + s / 20000)); },
    dash() { this.beep(220, 120, 'sawtooth', 0.025, 90); },
    splash() { this.beep(180, 350, 'sine', 0.05, 60); this.noise(260, 900, 0.03); },
    warn() { for (let i = 0; i < 3; i++) this.beep(90, 20, 'square', 0.03, 0, i * 0.09); },
    crack() { this.noise(200, 400, 0.05); },
    ball() { this.noise(60, 2500, 0.03); },
    pick() { this.beep(880, 70, 'triangle', 0.035); this.beep(1320, 90, 'triangle', 0.03, 0, 0.07); },
    gust() { this.noise(420, 700, 0.045); this.beep(160, 380, 'sine', 0.03, 520); },
    frost() { [1568, 2093, 2637].forEach((f, i) => this.beep(f, 90, 'triangle', 0.022, 0, i * 0.05)); },
    round() { [523, 659, 784].forEach((f, i) => this.beep(f, 130, 'square', 0.035, 0, i * 0.11)); },
    win() { [523, 659, 784, 1046].forEach((f, i) => this.beep(f, 150, 'square', 0.04, 0, i * 0.11)); },
    set(on) {
      this.on = on;
      try { localStorage.setItem('icefloe.sound', on ? '1' : '0'); } catch { /* приватне вікно */ }
    },
  };

  // ---- клавіатура одна на всі картки: тиснуть її в документ ----
  const live = new Set();
  const held = { up: false, down: false, left: false, right: false };
  const KEYS = {
    ArrowUp: 'up', KeyW: 'up', ArrowDown: 'down', KeyS: 'down', ArrowLeft: 'left', KeyA: 'left', ArrowRight: 'right', KeyD: 'right',
  };
  const KEYS_BY_KEY = { w: 'up', s: 'down', a: 'left', d: 'right', ц: 'up', і: 'down', ф: 'left', в: 'right' };
  const keyOf = (e) => KEYS[e.code] || KEYS_BY_KEY[String(e.key || '').toLowerCase()];
  const isDash = (e) => e.code === 'Space' || e.key === ' ';
  const isThrow = (e) => e.code === 'KeyX' || e.code === 'KeyE' || e.code === 'ShiftLeft';
  const keySector = () => sectorOf((held.right ? 1 : 0) - (held.left ? 1 : 0), (held.down ? 1 : 0) - (held.up ? 1 : 0));

  /// Усе відпустили. force — шлемо одразу, навіть якщо щойно слали: сторінка ховається чи перезавантажується
  /// (F5 із затиснутою стрілкою — blur перед цим не приходить), і другого шансу не буде.
  function releaseAll(force) {
    held.up = held.down = held.left = held.right = false;
    for (const st of live) { st.stickA = null; st.mouseA = null; st.mouseDown = false; st.padA = null; push(st, force === true); }
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
    let st = root._icefloe;
    if (!st) {
      st = root._icefloe = {
        ctx, cv: null, K: scale(), raf: 0, interp: HGames.ui.Interp(), last: null, prevEv: null,
        pal: null, palAt: 0, bg: null, bgKey: '', labels: [], picks: null,
        // світ із виду
        pond: 2600, shore: 904, bank: 941, bodyR: 60, R: new Float64Array(24), iceIv: -1, r0: 800,
        C: 1300, half: 991, sc: SIZE / 1982, cam0: 309, z: 1, zOk: false, ext: 0,
        bankOff: new Float64Array(16), ballOwner: new Map(),
        // своє тіло
        me: { x: 0, y: 0, vx: 0, vy: 0 }, meOk: false, meAcc: 0, meAt: 0, lastFrameAt: 0, pendingDash: 0,
        rtt: 60, echo: null,
        // ввід
        sent: null, sentAt: 0, want: -1, stickA: null, mouseA: null, mouseDown: false, mouseX: 0, mouseY: 0, padA: null,
        was: false,
        // соки
        parts: new Float32Array(PARTS * PF), partN: 0, pops: [], shake: 0, shakeAmp: 0, crackWas: null,
        hitFlash: new Float64Array(8), veins: null, drawMs: [], lastDraw: 0,
        // змішаний кадр — масиви без алокацій
        bx: new Float64Array(8), by: new Float64Array(8), nicks: [], phAt: 0,
        lx: new Float64Array(8), ly: new Float64Array(8), lOn: new Uint8Array(8), lr: new Float64Array(32),
      };
      live.add(st);
    }
    st.ctx = ctx;
    return st;
  }

  function palette(st) {
    const now = performance.now();
    if (st.pal && now - st.palAt < 2000) return st.pal;
    const c = (n, d) => HGames.ui.css(n, d);
    st.pal = {
      water: c('--if-water', '#173a4f'), water2: c('--if-water2', '#0f2a3b'), snow: c('--if-snow', '#dfe9ee'),
      snow2: c('--if-snow2', '#b9cbd4'), ice: c('--if-ice', '#9fd7ff'), ice2: c('--if-ice2', '#d9f1ff'),
      team0: c('--if-team0', '#3d8bff'), team1: c('--if-team1', '#ff5a3d'),
      edge: c('--if-edge', '#eaf8ff'), crack: c('--if-crack', '#ff6b5a'), reed: c('--if-reed', '#6e7f45'),
      text: c('--text', '#ecf1ea'), muted: c('--muted', '#9db3a5'), shade: c('--gshade', 'rgba(15, 31, 24, .62)'),
      bg2: c('--bg2', '#16291f'), accent: c('--accent', '#f4c542'), danger: c('--danger', '#ff6b5a'), ok: c('--ok', '#7bd389'),
      jug: c('--if-jug', '#8a5a3b'), font: c('--font', 'system-ui, sans-serif'),
      seats: SEAT_VARS.map(([n, d]) => c(n, d)),
    };
    const key = [st.pal.water, st.pal.snow, st.pal.reed, st.K, st.shore, st.half].join('|');
    if (key !== st.bgKey) { st.bgKey = key; st.bg = null; st.labels = []; st.picks = null; }
    st.palAt = now;
    return st.pal;
  }

  // ---- статичний шар: вода, берег, очерет — один раз на розмір ----
  function background(st, pal) {
    if (st.bg) return st.bg;
    const px = SIZE * st.K * Math.min(3, window.devicePixelRatio || 1);
    const bg = document.createElement('canvas');
    bg.width = bg.height = Math.round(px);
    const g = bg.getContext('2d');
    g.scale(px / SIZE, px / SIZE);
    const sc = st.sc, cx = SIZE / 2;
    // сніг на березі — усе поле, вода зверху колом
    g.fillStyle = pal.snow;
    g.fillRect(0, 0, SIZE, SIZE);
    // легкі замети
    g.fillStyle = pal.snow2;
    g.globalAlpha = 0.35;
    for (let i = 0; i < 26; i++) {
      const a = i * 2.39996, r = (st.shore + 30 + (i * 37) % 90) * sc;
      g.beginPath();
      g.ellipse(cx + Math.cos(a) * r, cx + Math.sin(a) * r, 18 + (i % 5) * 6, 6 + (i % 3) * 3, a, 0, Math.PI * 2);
      g.fill();
    }
    g.globalAlpha = 1;
    const grad = g.createRadialGradient(cx, cx, 0, cx, cx, st.shore * sc);
    grad.addColorStop(0, pal.water);
    grad.addColorStop(1, pal.water2);
    g.fillStyle = grad;
    g.beginPath();
    g.arc(cx, cx, st.shore * sc, 0, Math.PI * 2);
    g.fill();
    // брижі
    g.strokeStyle = 'rgba(255,255,255,0.06)';
    g.lineWidth = 1.2;
    for (let i = 0; i < 40; i++) {
      const a = i * 1.7, r = (300 + ((i * 97) % Math.max(200, st.shore - 330))) * sc;
      const x = cx + Math.cos(a) * r, y = cx + Math.sin(a) * r;
      g.beginPath();
      g.moveTo(x - 7, y);
      g.quadraticCurveTo(x, y - 3, x + 7, y);
      g.stroke();
    }
    // край берега
    g.strokeStyle = 'rgba(255,255,255,0.55)';
    g.lineWidth = 3;
    g.beginPath();
    g.arc(cx, cx, st.shore * sc, 0, Math.PI * 2);
    g.stroke();
    // очерет — кілька куп по колу
    g.strokeStyle = pal.reed;
    g.lineWidth = 1.6;
    g.lineCap = 'round';
    for (let k = 0; k < 7; k++) {
      const a = 0.4 + k * 0.9, r = (st.shore - 20) * sc;
      const x0 = cx + Math.cos(a) * r, y0 = cx + Math.sin(a) * r;
      for (let j = 0; j < 5; j++) {
        const dx = (j - 2) * 2.2;
        g.beginPath();
        g.moveTo(x0 + dx, y0 + 4);
        g.quadraticCurveTo(x0 + dx * 1.6, y0 - 8, x0 + dx * 2.2 + 2, y0 - 14 - (j % 2) * 4);
        g.stroke();
      }
      g.fillStyle = pal.jug;
      g.beginPath();
      g.ellipse(x0 + 4, y0 - 15, 1.6, 4, 0.2, 0, Math.PI * 2);
      g.fill();
    }
    st.bg = bg;
    return bg;
  }

  /// Нік над тілом — у спрайт один раз (fillText із тінню щокадру на вісьмох — дорого).
  function label(st, pal, seat, nick) {
    const px = st.labelPx || 12;
    const key = nick + '|' + st.K + '|' + px;
    let l = st.labels[seat];
    if (l && l.key === key) return l;
    const dpr = Math.min(3, window.devicePixelRatio || 1) * st.K;
    const cv = document.createElement('canvas');
    const g = cv.getContext('2d');
    const font = '700 ' + px + 'px ' + pal.font;
    g.font = font;
    const text = nick.length > 14 ? nick.slice(0, 13) + '…' : nick;
    const w = Math.ceil(g.measureText(text).width) + 8, h = Math.round(px * 1.5);
    cv.width = w * dpr;
    cv.height = h * dpr;
    g.scale(dpr, dpr);
    g.font = font;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.lineWidth = 3;
    g.strokeStyle = 'rgba(8, 20, 28, 0.85)';
    g.strokeText(text, w / 2, h / 2);
    g.fillStyle = pal.text;
    g.fillText(text, w / 2, h / 2);
    l = st.labels[seat] = { key, cv, w, h };
    return l;
  }

  /// Підбирачки — векторні спрайти (емодзі в різних системах малюються по-різному, а часом і квадратиком).
  function pickSprites(st, pal) {
    if (st.picks) return st.picks;
    const dpr = Math.min(3, window.devicePixelRatio || 1) * st.K;
    const tile = (paint) => {
      const cv = document.createElement('canvas');
      cv.width = cv.height = Math.round(28 * dpr);
      const g = cv.getContext('2d');
      g.scale(dpr, dpr);
      g.fillStyle = 'rgba(12, 30, 40, 0.5)';
      g.beginPath();
      g.roundRect(1, 1, 26, 26, 8);
      g.fill();
      g.strokeStyle = pal.accent;
      g.lineWidth = 1.5;
      g.stroke();
      paint(g);
      return cv;
    };
    st.picks = [
      tile((g) => {        // валянок із шипами
        g.fillStyle = '#e9e1d4';
        g.beginPath();
        g.moveTo(9, 5); g.lineTo(15, 5); g.lineTo(15, 15); g.quadraticCurveTo(22, 15, 22, 20);
        g.lineTo(22, 22); g.lineTo(7, 22); g.lineTo(9, 15); g.closePath();
        g.fill();
        g.fillStyle = '#39424a';
        for (let i = 0; i < 5; i++) { g.beginPath(); g.moveTo(8 + i * 3.4, 22); g.lineTo(9.6 + i * 3.4, 25.5); g.lineTo(11.2 + i * 3.4, 22); g.fill(); }
      }),
      tile((g) => {        // важкий глек
        g.fillStyle = pal.jug;
        g.beginPath();
        g.moveTo(11, 5); g.lineTo(17, 5); g.lineTo(16, 8);
        g.quadraticCurveTo(23, 11, 21.5, 18); g.quadraticCurveTo(20, 23.5, 14, 23.5);
        g.quadraticCurveTo(8, 23.5, 6.5, 18); g.quadraticCurveTo(5, 11, 12, 8); g.closePath();
        g.fill();
        g.strokeStyle = '#f4c542';
        g.lineWidth = 1.3;
        g.beginPath(); g.moveTo(8, 14); g.quadraticCurveTo(14, 16.5, 20, 14); g.stroke();
      }),
      tile((g) => {        // дві сніжки
        for (const [x, y, r] of [[10.5, 16, 6], [17.5, 11.5, 5.5]]) {
          g.fillStyle = '#ffffff';
          g.beginPath(); g.arc(x, y, r, 0, Math.PI * 2); g.fill();
          g.fillStyle = 'rgba(120, 170, 200, 0.55)';
          g.beginPath(); g.arc(x + r * 0.3, y + r * 0.3, r * 0.4, 0, Math.PI * 2); g.fill();
        }
      }),
      tile((g) => {        // завірюха: три хвости вітру із завитками
        g.strokeStyle = '#eaf8ff';
        g.lineWidth = 2.2;
        g.lineCap = 'round';
        g.beginPath(); g.moveTo(4.5, 10); g.lineTo(16, 10); g.arc(16, 7, 3, Math.PI / 2, -Math.PI * 0.9, true); g.stroke();
        g.beginPath(); g.moveTo(4.5, 15); g.lineTo(20, 15); g.arc(20, 11.5, 3.5, Math.PI / 2, -Math.PI * 0.9, true); g.stroke();
        g.beginPath(); g.moveTo(6.5, 20); g.lineTo(15, 20); g.arc(15, 22.5, 2.5, -Math.PI / 2, Math.PI * 0.9); g.stroke();
      }),
      tile((g) => {        // ковзан: черевик на лезі
        g.fillStyle = '#e9e1d4';
        g.beginPath();
        g.moveTo(8, 4.5); g.lineTo(14, 4.5); g.lineTo(14, 12); g.quadraticCurveTo(21, 12, 21.5, 17);
        g.lineTo(21.5, 18.5); g.lineTo(7, 18.5); g.lineTo(8, 12); g.closePath();
        g.fill();
        g.strokeStyle = '#c7d3da';
        g.lineWidth = 1.6;
        g.beginPath(); g.moveTo(9, 18.5); g.lineTo(9, 21.5); g.moveTo(19, 18.5); g.lineTo(19, 21.5); g.stroke();
        g.lineWidth = 2;
        g.beginPath(); g.moveTo(5, 22); g.lineTo(21, 22); g.quadraticCurveTo(24.5, 22, 24, 19); g.stroke();
      }),
      tile((g) => {        // іній: шестипроменева крижинка
        g.strokeStyle = '#bfeaff';
        g.lineWidth = 2;
        g.lineCap = 'round';
        for (let i = 0; i < 3; i++) {
          const a = i * Math.PI / 3, c = Math.cos(a), s = Math.sin(a);
          g.beginPath(); g.moveTo(14 - c * 9, 14 - s * 9); g.lineTo(14 + c * 9, 14 + s * 9); g.stroke();
        }
        g.lineWidth = 1.4;
        for (let i = 0; i < 6; i++) {
          const a = i * Math.PI / 3, x = 14 + Math.cos(a) * 6, y = 14 + Math.sin(a) * 6;
          g.beginPath();
          g.moveTo(x + Math.cos(a + 2.4) * 3, y + Math.sin(a + 2.4) * 3); g.lineTo(x, y);
          g.lineTo(x + Math.cos(a - 2.4) * 3, y + Math.sin(a - 2.4) * 3);
          g.stroke();
        }
      }),
      tile((g) => {        // кулак: червона рукавиця з білим манжетом
        g.fillStyle = '#d8453a';
        g.beginPath(); g.ellipse(15, 11.5, 7, 7.5, 0, 0, Math.PI * 2); g.fill();
        g.beginPath(); g.ellipse(7.5, 13, 3, 4.5, -0.5, 0, Math.PI * 2); g.fill();
        g.fillStyle = '#f2ece2';
        g.beginPath(); g.roundRect(9, 18, 12, 6, 2); g.fill();
        g.strokeStyle = 'rgba(255, 255, 255, 0.55)';
        g.lineWidth = 1.2;
        g.beginPath(); g.moveTo(11, 8); g.quadraticCurveTo(15, 5.5, 19, 8); g.stroke();
      }),
    ];
    return st.picks;
  }

  // ---- частинки: пул без алокацій (x, y, vx, vy, life, max, kind, size) ----
  const PARTS = 240, PF = 8;
  const K_SHARD = 0, K_DROP = 1, K_RING = 2, K_PUFF = 3;
  function spawn(st, kind, x, y, vx, vy, life, size) {
    let i = st.partN;
    if (i >= PARTS) {
      // пул повний — перезаписуємо найстарішу (найменше життя)
      let best = 0, bl = Infinity;
      for (let k = 0; k < PARTS; k++) { const l = st.parts[k * PF + 4]; if (l < bl) { bl = l; best = k; } }
      i = best;
    } else st.partN++;
    const o = i * PF, p = st.parts;
    p[o] = x; p[o + 1] = y; p[o + 2] = vx; p[o + 3] = vy; p[o + 4] = life; p[o + 5] = life; p[o + 6] = kind; p[o + 7] = size;
  }
  const many = () => (reduced() ? 0.5 : 1);
  function burst(st, kind, x, y, n, speed, life, size) {
    n = Math.max(1, Math.round(n * many()));
    for (let i = 0; i < n; i++) {
      const a = Math.random() * Math.PI * 2, v = speed * (0.4 + Math.random() * 0.8);
      spawn(st, kind, x, y, Math.cos(a) * v, Math.sin(a) * v, life * (0.7 + Math.random() * 0.5), size * (0.6 + Math.random() * 0.8));
    }
  }

  function drawParts(st, g, pal, dt) {
    const p = st.parts;
    let n = st.partN;
    for (let i = 0; i < n; i++) {
      const o = i * PF;
      p[o + 4] -= dt;
      if (p[o + 4] <= 0) {
        // останню на місце померлої
        n--;
        if (i !== n) { for (let k = 0; k < PF; k++) p[o + k] = p[n * PF + k]; i--; }
        continue;
      }
    }
    st.partN = n;
    const sec = dt / 1000;
    for (let i = 0; i < n; i++) {
      const o = i * PF;
      const kind = p[o + 6], k = p[o + 4] / p[o + 5];
      p[o] += p[o + 2] * sec;
      p[o + 1] += p[o + 3] * sec;
      p[o + 2] *= 0.94;
      p[o + 3] *= 0.94;
      const x = p[o], y = p[o + 1], s = p[o + 7];
      g.globalAlpha = Math.max(0, Math.min(1, k * 1.2));
      if (kind === K_SHARD) {
        // біла скалка з темнішим обідком — інакше на світлій кризі її майже не видно
        g.fillStyle = pal.edge;
        g.strokeStyle = 'rgba(30, 80, 115, 0.55)';
        g.lineWidth = 1;
        g.beginPath();
        g.moveTo(x, y - s);
        g.lineTo(x + s * 0.8, y + s * 0.6);
        g.lineTo(x - s * 0.7, y + s * 0.5);
        g.closePath();
        g.fill();
        g.stroke();
      } else if (kind === K_DROP) {
        g.fillStyle = pal.ice;
        g.beginPath();
        g.arc(x, y, s, 0, Math.PI * 2);
        g.fill();
      } else if (kind === K_RING) {
        const rr = s * (1 + (1 - k) * 3);
        g.strokeStyle = 'rgba(30, 80, 115, 0.4)';
        g.lineWidth = 3.5;
        g.beginPath();
        g.arc(x, y, rr, 0, Math.PI * 2);
        g.stroke();
        g.strokeStyle = '#ffffff';
        g.lineWidth = 1.8;
        g.stroke();
      } else {
        g.fillStyle = '#ffffff';
        g.beginPath();
        g.arc(x, y, s * (0.6 + (1 - k) * 0.8), 0, Math.PI * 2);
        g.fill();
      }
    }
    g.globalAlpha = 1;
  }

  // ---- світ ----
  function takeView(st, v) {
    if (!v) return;
    if (v.pond) st.pond = v.pond;
    if (v.shore) st.shore = v.shore;
    if (v.bank) st.bank = v.bank;
    if (v.bodyR) st.bodyR = v.bodyR;
    // Камера: ставок лише навколо криги цієї партії — до вибулих на березі й трохи снігу за ними.
    st.C = st.pond / 2;
    const half = st.bank + 70;
    if (half !== st.half) { st.half = half; st.bg = null; st.iceGrad = null; st.labels = []; st.picks = null; st.zOk = false; }
    st.sc = SIZE / (2 * half);
    st.cam0 = st.C - half;
    if (v.ice && v.ice.v && (v.ice.iv !== st.iceIv || v.phase === 'lobby')) {
      for (let i = 0; i < 24; i++) st.R[i] = v.ice.v[i] || 0;
      st.iceIv = v.ice.iv;
      st.r0 = v.ice.r0 || st.r0;
      st.veins = null;
    }
  }

  /// Фаза кадру: 0 готуйсь, 1 гра, 2 кінець раунду, 3 партію зіграно, 4 лобі.
  const phaseOf = (st) => {
    const v = st.ctx && st.ctx.view;
    if (v && v.phase === 'over') return 3;
    if (v && v.phase === 'lobby') return 4;
    return st.last ? st.last.ph : 4;
  };

  /// Змішаний кадр: позиції чужих тіл — між двома останніми кадрами. Пишемо в st.bx/by, без алокацій.
  function blend(st) {
    const at = st.interp.at();
    const b = (at && at.b) || st.last;
    if (!b || !b.p) return b;
    const a = at && at.a;
    const smooth = a && a !== b && a.p && a.ph === b.ph && a.t <= b.t && b.t - a.t <= 3;
    const t = at ? at.t : 1;
    for (let i = 0; i < 8; i++) {
      const q = b.p[i];
      if (!q) continue;
      const w = smooth ? a.p[i] : null;
      if (w && (w[5] & 1) && (q[5] & 1)) {
        st.bx[i] = w[0] + (q[0] - w[0]) * t;
        st.by[i] = w[1] + (q[1] - w[1]) * t;
      } else {
        st.bx[i] = q[0];
        st.by[i] = q[1];
      }
    }
    st.blendT = smooth ? t : 1;
    st.blendA = smooth ? a : null;
    return b;
  }

  // ---- передбачення свого тіла ----
  function mySeat(st) {
    const ctx = st.ctx;
    return ctx && ctx.mine && ctx.seat != null ? ctx.seat : -1;
  }
  function myMu(q) { return q && (q[5] & 2) ? SPIKE_MU : q && (q[5] & 128) ? SKATE_MU : MU; }
  function myThrust(q) {
    const fast = q && (q[5] & 130), jug = q && (q[5] & 4);
    return fast ? (jug ? JUG_FAST_THRUST : FAST_THRUST) : (jug ? JUG_THRUST : THRUST);
  }
  /// Замерзлий (іній) не тягне: сервер веде його з наміром −1, і передбачення теж.
  function myWant(st, q) { return q && (q[5] & 512) ? -1 : currentWant(st); }
  function myDash(q) { return q && (q[5] & 256) ? FIST_DASH : DASH; }

  /// Новий кадр: де сервер бачить моє тіло «зараз» (кадр плюс пів дороги мережею) і м'яка поправка.
  function correct(st, f) {
    const s = mySeat(st);
    const q = s >= 0 && f.p ? f.p[s] : null;
    if (!q || !(q[5] & 1) || f.ph !== 1) { st.meOk = false; return; }
    if (st.pendingDash && q[6] > 0 && q[6] >= DASH_CD - 3) st.pendingDash = 0;   // сервер уже ривкнув
    const tg = { x: q[0], y: q[1], vx: q[2], vy: q[3] };
    const now = performance.now();
    if (st.pendingDash && now - st.pendingDash > 350) st.pendingDash = 0;
    if (st.pendingDash && q[6] === 0) {
      // ривок, який я вже показав, серверу ще не долетів — докладаємо його й до цілі, інакше смикне назад
      tg.vx += myDash(q) * COS16[st.dashFace];
      tg.vy += myDash(q) * SIN16[st.dashFace];
      const s2 = tg.vx * tg.vx + tg.vy * tg.vy;
      if (s2 > VMAX * VMAX) { const c = VMAX / Math.sqrt(s2); tg.vx *= c; tg.vy *= c; }
    }
    const lead = Math.max(0, Math.min(120, (st.rtt - 20) / 2));
    const steps = Math.round(lead / SUB_MS);
    const want = myWant(st, q);
    for (let i = 0; i < steps; i++) glide(tg, want, myMu(q), myThrust(q));
    if (!st.meOk) {
      Object.assign(st.me, tg);
      st.meOk = true;
    } else {
      const dx = tg.x - st.me.x, dy = tg.y - st.me.y;
      if (dx * dx + dy * dy > 90 * 90) Object.assign(st.me, tg);
      else {
        st.me.x += dx * 0.25;
        st.me.y += dy * 0.25;
        st.me.vx = tg.vx;
        st.me.vy = tg.vy;
      }
    }
    st.meQ = q;
    // відлуння наміру: скільки йшов мій move до сервера й назад — з цього «пів дороги»
    if (st.echo && q[4] === st.echo.a && (q[5] & 32)) {
      const sample = now - st.echo.at;
      st.rtt = st.rtt * 0.7 + Math.min(400, sample) * 0.3;
      st.echo = null;
    }
  }

  /// Між кадрами тіло їде саме — тими самими підкроками по 20 мс, що й на сервері.
  function predict(st, now) {
    if (!st.meOk) { st.meAt = now; return; }
    const dt = Math.min(100, now - st.meAt);
    st.meAt = now;
    if (now - st.lastFrameAt > 400) return;     // зв'язок пропав — не фантазуємо далі
    st.meAcc += dt;
    const q = st.meQ, want = myWant(st, q);
    while (st.meAcc >= SUB_MS) {
      glide(st.me, want, myMu(q), myThrust(q));
      st.meAcc -= SUB_MS;
    }
  }

  // ---- ввід ----
  function inWater(st) {
    const s = mySeat(st);
    const q = s >= 0 && st.last && st.last.p ? st.last.p[s] : null;
    return !!q && !(q[5] & 1);
  }
  /// З берега ще можна раз відколоти кригу (прапорець 64 у кадрі).
  function canChip(st) {
    const s = mySeat(st);
    const q = s >= 0 && st.last && st.last.p ? st.last.p[s] : null;
    return !!q && !(q[5] & 1) && !!(q[5] & 64);
  }
  function currentWant(st) {
    if (st.stickA != null) return st.stickA;
    if (st.padA != null) return st.padA;
    if (st.mouseA != null) return st.mouseA;
    return keySector();
  }
  function canSend(st) {
    const ctx = st.ctx;
    return !!(ctx && ctx.mine && ctx.playing && phaseOf(st) <= 2);
  }
  /// Шлемо лише зміну сектора і не частіше 20/с; остання зміна досилається з rAF. keep — те саме ще раз, щоб
  /// сервер знав, що напрямок і досі тримають (відлуння для RTT тоді не міряємо: сервер уже показує цей сектор).
  function push(st, force, keep) {
    if (!canSend(st)) { st.sent = null; return; }
    const a = currentWant(st);
    st.want = a;
    if (!force && st.sent === a) return;
    const now = performance.now();
    if (!force && now - st.sentAt < SEND_MS) return;   // flush() дошле
    st.sent = a;
    st.sentAt = now;
    st.ctx.input('move', { a });
    if (a >= 0 && !keep) st.echo = { a, at: now };
  }
  function dash(st) {
    if (!canSend(st)) return;
    // у воді «ривок» — 🔨 відкол криги (раз за раунд, п. 183), а коли вже колов — сніжка, як і було
    if (inWater(st)) { st.ctx.input(canChip(st) ? 'chip' : 'throw'); return; }
    st.ctx.input('dash');
    // ривок видно одразу: те саме, що зробить сервер (якщо перезарядка, на наш погляд, скінчилась)
    const s = mySeat(st), q = st.last && st.last.p && st.last.p[s];
    const ticksSince = (performance.now() - st.lastFrameAt) / TICK_MS;
    if (st.meOk && q && phaseOf(st) === 1 && q[6] - ticksSince <= 0.5 && !st.pendingDash && !(q[5] & 512)) {
      const face = st.want >= 0 ? st.want : q[4];
      st.dashFace = face;
      st.pendingDash = performance.now();
      st.me.vx += myDash(q) * COS16[face];
      st.me.vy += myDash(q) * SIN16[face];
      const s2 = st.me.vx * st.me.vx + st.me.vy * st.me.vy;
      if (s2 > VMAX * VMAX) { const c = VMAX / Math.sqrt(s2); st.me.vx *= c; st.me.vy *= c; }
      trail(st, st.me.x, st.me.y, face);
    }
  }
  function toss(st) { if (canSend(st)) st.ctx.input('throw'); }

  /// Точка на канвасі (CSS-пікселі) → світ, см (з урахуванням наїзду камери на центр ставка).
  function toWorld(st, clientX, clientY) {
    const r = st.cv.el.getBoundingClientRect();
    if (!r.width) return null;
    const k = 1 / (st.sc * st.z);
    return [st.C + (((clientX - r.left) / r.width) * SIZE - SIZE / 2) * k, st.C + (((clientY - r.top) / r.height) * SIZE - SIZE / 2) * k];
  }
  /// Базові пікселі світу (x·sc) → пікселі канваса після наїзду камери.
  const viewX = (st, bx) => SIZE / 2 + (bx - st.C * st.sc) * st.z;
  /// Звідки рахувати напрямок до курсора: своє тіло на кризі або точка на березі.
  function origin(st) {
    const s = mySeat(st);
    if (s < 0) return null;
    if (st.meOk) return [st.me.x, st.me.y];
    const q = st.last && st.last.p && st.last.p[s];
    return q ? [q[0], q[1]] : null;
  }
  function aimMouse(st) {
    if (!st.mouseDown && !inWater(st)) { if (st.mouseA != null) { st.mouseA = null; push(st); } return; }
    const w = toWorld(st, st.mouseX, st.mouseY), o = origin(st);
    if (!w || !o) return;
    const dx = w[0] - o[0], dy = w[1] - o[1];
    const a = dx * dx + dy * dy < 30 * 30 ? -1 : sectorOf(dx, dy);
    if (inWater(st) && a < 0) return;
    if (a !== st.mouseA) { st.mouseA = a; push(st); }
  }

  /// Аналоговий стік пада: 16 секторів замість чотирьох синтезованих стрілок.
  function readPad(st) {
    if (!window.HPad || !(HPad.pads > 0) || !navigator.getGamepads) { st.padA = null; return; }
    const list = navigator.getGamepads() || [];
    let gp = null;
    for (let i = 0; i < list.length; i++) if (list[i] && list[i].connected !== false) { gp = list[i]; break; }
    if (!gp || !gp.axes || gp.axes.length < 2) { st.padA = null; return; }
    const x = gp.axes[0] || 0, y = gp.axes[1] || 0, m = Math.hypot(x, y);
    let a = st.padA;
    if (m >= 0.5) a = sectorOf(x, y);
    else if (m < 0.35) a = null;
    if (a !== st.padA) { st.padA = a; push(st); }
  }

  // ---- соки з подій кадру ----
  function trail(st, x, y, face) {
    const sc = st.sc;
    for (let i = 0; i < Math.round(8 * many()); i++) {
      const back = 20 + i * 12;
      spawn(st, K_PUFF, (x - COS16[face] * back) * sc + (Math.random() - 0.5) * 6, (y - SIN16[face] * back) * sc + (Math.random() - 0.5) * 6,
        -COS16[face] * 20, -SIN16[face] * 20, 250, 2.2 + Math.random() * 1.5);
    }
  }

  /// Ім'я місця: нік або «🤖 бот рудий» (соло з ботами, core.js nameOf).
  const nameAt = (ctx, s) => (ctx.nameOf ? ctx.nameOf(s) : ctx.nickOf(s));

  /// Нік місця; хто вже встав — з пам'яті (підсумок партії має казати «Оля», а не «синій»).
  function nick(st, s) {
    const n = st.ctx && nameAt(st.ctx, s);
    if (n) { st.nicks[s] = n; return n; }
    return st.nicks[s] || (st.ctx && st.ctx.seatName(s)) || String(s + 1);
  }

  function events(st, f) {
    const now = performance.now();
    const visible = st.cv && st.cv.el.offsetParent;
    const sc = st.sc, me = mySeat(st);
    // тріщина-попередження з'явилась
    const ck = f.crack ? f.crack[0] + ':' + f.crack[1] : null;
    if (ck && ck !== st.crackWas && visible) Snd.warn();
    st.crackWas = ck;
    if (!f.ev || !f.ev.length || f === st.prevEv) return;
    st.prevEv = f;
    if (!visible) return;
    for (const e of f.ev) {
      switch (e[0]) {
        case 1: {           // зіткнення
          const s = e[5] || 0, x = e[3] * sc, y = e[4] * sc;
          // скалки льоду віялом, біле кільце й пил на місці удару — видно навіть на вісьмох на Деці
          burst(st, K_SHARD, x, y, 12 + Math.min(16, s / 50), 150, 520, 4.4);
          burst(st, K_PUFF, x, y, 4 + Math.min(6, s / 150), 60, 380, 3);
          spawn(st, K_RING, x, y, 0, 0, 380, 7);
          if (s >= 450) spawn(st, K_RING, x, y, 0, 0, 520, 12);
          Snd.bump(s);
          st.hitFlash[e[1]] = st.hitFlash[e[2]] = now;
          if (e[1] === me || e[2] === me) {
            if (s >= 300) { st.shake = now; st.shakeAmp = Math.min(7, 3 + s / 250); st.shakeMs = 200; }
          } else if (s >= 650 && !(now - st.shake < (st.shakeMs || 0))) { st.shake = now; st.shakeAmp = 2; st.shakeMs = 140; }
          break;
        }
        case 2: {           // шубовсь
          const x = e[3] * sc, y = e[4] * sc;
          for (let r = 0; r < 3; r++) spawn(st, K_RING, x, y, 0, 0, 700 - r * 120, 4 + r * 3);
          burst(st, K_DROP, x, y, 14, 110, 700, 2);
          Snd.splash();
          const who = nick(st, e[1]);
          const text = e[2] >= 0 ? '💨 ' + nick(st, e[2]) + ' → 🌊 ' + who : '🌊 ' + who;
          st.pops.push({ text, x, y: y - 10, at: now, color: st.pal ? st.pal.seats[e[2] >= 0 ? e[2] : e[1]] : '#fff' });
          if (e[1] === me) { st.shake = now; st.shakeAmp = 5; st.shakeMs = 250; }
          break;
        }
        case 3: {           // ривок
          if (e[1] === me && st.pendingDash) break;     // свій уже намалювали
          const q = f.p && f.p[e[1]];
          if (q) trail(st, q[0], q[1], q[4]);
          if (e[1] === me) Snd.dash();
          break;
        }
        case 4: {           // підбирачка
          const q = f.p && f.p[e[1]];
          if (q) st.pops.push({ text: PICK_NAME[e[2]] || '', x: q[0] * sc, y: q[1] * sc - 26, at: now, color: st.pal ? st.pal.text : '#fff' });
          if (e[1] === me) Snd.pick();
          if (!q || !f.p) break;
          const vs = (s) => {                         // кого зачепило: живі суперники (у командах — чужі)
            const tm = st.ctx && st.ctx.view && st.ctx.view.teams;
            const o = f.p[s];
            return s !== e[1] && o && (o[5] & 1) && !(tm && tm[s] != null && tm[s] === tm[e[1]]);
          };
          if (e[2] === 3) {  // завірюха: хвиля від того, хто підібрав
            const x = q[0] * sc, y = q[1] * sc;
            for (let r = 0; r < 3; r++) spawn(st, K_RING, x, y, 0, 0, 420 + r * 110, (GUST_R / 4) * sc * (0.45 + r * 0.25));
            burst(st, K_PUFF, x, y, 16, 260, 520, 3);
            Snd.gust();
            for (let s = 0; s < 8; s++) {
              const o = f.p[s];
              if (vs(s) && Math.hypot(o[0] - q[0], o[1] - q[1]) <= GUST_R + 60) st.hitFlash[s] = now;
            }
          } else if (e[2] === 5) {   // іній: скалки на кожному суперникові
            for (let s = 0; s < 8; s++) {
              if (!vs(s)) continue;
              burst(st, K_SHARD, f.p[s][0] * sc, f.p[s][1] * sc, 8, 90, 480, 3.4);
              if (s === me) { st.shake = now; st.shakeAmp = 3; st.shakeMs = 160; }
            }
            Snd.frost();
          }
          break;
        }
        case 5: {           // сніжка влучила
          burst(st, K_PUFF, e[3] * sc, e[4] * sc, 8, 70, 350, 1.8);
          Snd.ball();
          st.hitFlash[e[1]] = now;
          break;
        }
        case 6: {           // відкол
          const s0 = e[1], L = e[2];
          for (let k = 0; k < L; k++) {
            const a = ((s0 + k) % 24) * (Math.PI / 12), r = st.R[(s0 + k) % 24] * sc;
            const cx = st.C * sc;
            burst(st, K_SHARD, cx + Math.cos(a) * r, cx + Math.sin(a) * r, 4, 70, 600, 3);
            spawn(st, K_RING, cx + Math.cos(a) * r * 1.05, cx + Math.sin(a) * r * 1.05, 0, 0, 600, 6);
          }
          Snd.crack();
          break;
        }
        case 7:             // кінець раунду
          if (e[1] >= 0 && e[1] === me) Snd.win(); else Snd.round();
          break;
        default: break;
      }
    }
  }

  // ---- малювання ----
  function icePath(g, st, melt, sc, grow) {
    const cx = st.C * sc;
    g.beginPath();
    for (let i = 0; i < 24; i++) {
      const r = Math.max(0, st.R[i] - melt) * sc + grow;
      const a = i * (Math.PI / 12);
      const x = cx + Math.cos(a) * r, y = cx + Math.sin(a) * r;
      if (i === 0) g.moveTo(x, y); else g.lineTo(x, y);
    }
    g.closePath();
  }

  function drawIce(st, g, pal, f, sc, now) {
    const melt = (f && f.melt) || 0;
    // тінь у воду
    g.save();
    g.translate(2.5, 4);
    g.fillStyle = 'rgba(4, 16, 24, 0.45)';
    icePath(g, st, melt, sc, 3);
    g.fill();
    g.restore();
    if (!st.iceGrad || st.iceGradKey !== pal.ice + st.r0 + ':' + st.half) {
      const cx = st.C * sc;
      st.iceGrad = g.createRadialGradient(cx - 40, cx - 60, 10, cx, cx, st.r0 * 1.1 * sc);
      st.iceGrad.addColorStop(0, pal.ice2);
      st.iceGrad.addColorStop(1, pal.ice);
      st.iceGradKey = pal.ice + st.r0 + ':' + st.half;
    }
    g.fillStyle = st.iceGrad;
    icePath(g, st, melt, sc, 0);
    g.fill();
    // прожилки — фактура, у межах криги
    g.save();
    g.clip();
    if (!st.veins) {
      const seed = st.iceIv * 7 + 3;
      let s = seed;
      const rnd = () => { s = (s * 16807) % 2147483647; return s / 2147483647; };
      st.veins = [];
      for (let k = 0; k < 4; k++) {
        const pts = [];
        let a = rnd() * Math.PI * 2, r = rnd() * 0.3 * st.r0;
        for (let j = 0; j < 6; j++) {
          pts.push(Math.cos(a) * r, Math.sin(a) * r);
          a += (rnd() - 0.5) * 0.9;
          r += 90 + rnd() * 120;
        }
        st.veins.push(pts);
      }
    }
    g.strokeStyle = 'rgba(255,255,255,0.45)';
    g.lineWidth = 1;
    for (const pts of st.veins) {
      g.beginPath();
      for (let j = 0; j < pts.length; j += 2) {
        const x = st.C * sc + pts[j] * sc, y = st.C * sc + pts[j + 1] * sc;
        if (j === 0) g.moveTo(x, y); else g.lineTo(x, y);
      }
      g.stroke();
    }
    g.restore();
    // край: світлий обідок, а поки тане — ще й мокра смуга
    g.strokeStyle = melt > 0 ? pal.ice : pal.edge;
    g.lineWidth = 2.5;
    icePath(g, st, melt, sc, 0);
    g.stroke();
    if (melt > 0) {
      g.strokeStyle = 'rgba(120, 190, 230, 0.55)';
      g.lineWidth = 5;
      icePath(g, st, melt, sc, -3);
      g.stroke();
    }
    // тріщина-попередження: зубчаста лінія там, де відколеться
    if (f && f.crack && f.ph === 1) drawCrack(st, g, pal, f, sc, now, melt);
    // тріщини від вибулих (п. 183) — так само: секунда, щоб відскочити
    if (f && f.chips && f.ph === 1) for (const c of f.chips) drawCrack(st, g, pal, { crack: c }, sc, now, melt);
  }

  function drawCrack(st, g, pal, f, sc, now, melt) {
    const s0 = f.crack[0], L = f.crack[1];
    const cx = st.C * sc;
    const pulse = reduced() ? 1 : 0.4 + 0.6 * (0.5 + 0.5 * Math.sin(now / 80));
    const steps = L * 3 + 1;
    const a0 = (s0 - 0.5) * (Math.PI / 12), a1 = (s0 + L - 0.5) * (Math.PI / 12);
    // точка краю криги під кутом (як EdgeAt — інтерполяція між вершинами)
    const edgeR = (a) => {
      const u = ((a / (Math.PI / 12)) % 24 + 24) % 24;
      const i = Math.floor(u), t = u - i;
      const ri = Math.max(0, st.R[i] - melt), rj = Math.max(0, st.R[(i + 1) % 24] - melt);
      return (ri + (rj - ri) * t) * sc;
    };
    // шматок, що відвалиться, — злегка червоніє
    g.globalAlpha = 0.18 * pulse;
    g.fillStyle = pal.crack;
    g.beginPath();
    for (let k = 0; k <= steps; k++) {
      const a = a0 + ((a1 - a0) * k) / steps, r = edgeR(a);
      const x = cx + Math.cos(a) * r, y = cx + Math.sin(a) * r;
      if (k === 0) g.moveTo(x, y); else g.lineTo(x, y);
    }
    for (let k = steps; k >= 0; k--) {
      const a = a0 + ((a1 - a0) * k) / steps, r = edgeR(a) * 0.74;
      g.lineTo(cx + Math.cos(a) * r, cx + Math.sin(a) * r);
    }
    g.closePath();
    g.fill();
    // зубчаста тріщина
    g.globalAlpha = pulse;
    g.strokeStyle = pal.crack;
    g.lineWidth = 2.2;
    g.lineJoin = 'round';
    g.beginPath();
    let zig = s0 * 31 + L;
    for (let k = 0; k <= steps; k++) {
      const a = a0 + ((a1 - a0) * k) / steps;
      zig = (zig * 1103515245 + 12345) & 0x7fffffff;
      const j = k === 0 || k === steps ? 0 : ((zig % 100) / 100 - 0.5) * 7;
      const r = edgeR(a) * 0.74 + j;
      const x = cx + Math.cos(a) * r, y = cx + Math.sin(a) * r;
      if (k === 0) {
        const re = edgeR(a);
        g.moveTo(cx + Math.cos(a) * re, cx + Math.sin(a) * re);
        g.lineTo(x, y);
      } else g.lineTo(x, y);
    }
    const re = edgeR(a1);
    g.lineTo(cx + Math.cos(a1) * re, cx + Math.sin(a1) * re);
    g.stroke();
    g.globalAlpha = 1;
  }

  function drawPickups(st, g, pal, f, sc, now) {
    if (!f.k || !f.k.length) return;
    const spr = pickSprites(st, pal);
    const k = reduced() ? 1 : 1 + 0.06 * Math.sin(now / 143);
    for (const p of f.k) {
      const img = spr[p[2]] || spr[0];
      const s = 26 * k;
      g.drawImage(img, p[0] * sc - s / 2, p[1] * sc - s / 2, s, s);
    }
  }

  /// Чия сніжка: кинута з берега — вилітає з точки падіння того, хто там стоїть. Власника в кадрі нема (зайві
  /// байти), тож угадуємо один раз, коли сніжку вперше видно, і пам'ятаємо за id.
  function ballOwner(st, f, b) {
    let o = st.ballOwner.get(b[0]);
    if (o !== undefined) return o;
    o = -1;
    let best = 150 * 150;
    for (let i = 0; i < 8; i++) {
      const q = f.p && f.p[i];
      if (!q || !(q[5] & 16)) continue;
      const d = (q[0] - b[1]) * (q[0] - b[1]) + (q[1] - b[2]) * (q[1] - b[2]);
      if (d < best) { best = d; o = i; }
    }
    if (st.ballOwner.size > 64) st.ballOwner.clear();
    st.ballOwner.set(b[0], o);
    return o;
  }

  function drawBalls(st, g, pal, f, sc) {
    if (!f.s || !f.s.length) return;
    const a = st.blendA, t = st.blendT;
    g.strokeStyle = 'rgba(255,255,255,0.45)';
    g.fillStyle = '#ffffff';
    g.lineWidth = 2.5;
    g.lineCap = 'round';
    for (const b of f.s) {
      let x = b[1], y = b[2];
      if (a && a.s) {
        for (const w of a.s) if (w[0] === b[0]) { x = w[1] + (b[1] - w[1]) * t; y = w[2] + (b[2] - w[2]) * t; break; }
      }
      // сніжка з берега, коли камера наїхала: вилітає з фігурки біля краю кадру й за мить сходиться зі справжнім шляхом
      let ox = 0, oy = 0;
      const o = ballOwner(st, f, b), q = o >= 0 && f.p[o];
      if (q && (q[5] & 16) && (st.bankOff[2 * o] || st.bankOff[2 * o + 1])) {
        const span = Math.max(80, st.bank - st.ext);
        const k = Math.max(0, 1 - Math.hypot(x - q[0], y - q[1]) / span);
        ox = st.bankOff[2 * o] * k;
        oy = st.bankOff[2 * o + 1] * k;
      }
      const sp = Math.hypot(b[3], b[4]) || 1;
      const px = x * sc + ox, py = y * sc + oy;
      g.beginPath();
      g.moveTo(px, py);
      g.lineTo(px - (b[3] / sp) * 70 * sc, py - (b[4] / sp) * 70 * sc);
      g.stroke();
      g.beginPath();
      g.arc(px, py, 3.5, 0, Math.PI * 2);
      g.fill();
    }
  }

  /// Колір темніший на k (0..1) — для шапки й обідка. Кешуємо: кольорів місць лише вісім.
  const darkCache = new Map();
  function darker(hex, k) {
    const key = hex + k;
    let v = darkCache.get(key);
    if (v) return v;
    const m = /^#?([0-9a-f]{6})$/i.exec(String(hex).trim());
    if (!m) return hex;
    const n = parseInt(m[1], 16);
    const f = (c) => Math.round(c * (1 - k));
    v = 'rgb(' + f(n >> 16) + ',' + f((n >> 8) & 255) + ',' + f(n & 255) + ')';
    darkCache.set(key, v);
    return v;
  }

  /// Кругляш у кожусі кольору місця: згори видно шапку з помпоном і номером, а спереду визирають валянки.
  function drawBody(st, g, pal, seat, x, y, face, fl, sc, now, mine, ready, speed) {
    const R = st.bodyR * sc, color = pal.seats[seat];
    const cx = x * sc, cy = y * sc;
    // команди (п. 182): кільце кольору команди довкола валянок — свого видно навіть у купі
    const tm = st.ctx && st.ctx.view && st.ctx.view.teams;
    if (tm && tm[seat] != null) {
      g.strokeStyle = tm[seat] === 0 ? pal.team0 : pal.team1;
      g.lineWidth = Math.max(3, R * 0.16);
      g.globalAlpha = 0.9;
      g.beginPath();
      g.arc(cx, cy, R * 1.22, 0, Math.PI * 2);
      g.stroke();
      g.globalAlpha = 1;
    }
    const fx = COS16[face], fy = SIN16[face];
    // на ходу валянки дрібно тупцяють: один уперед, другий назад (швидше — частіше)
    const walk = speed > 60 && !reduced() ? Math.sin(now / Math.max(45, 110 - speed / 12) + seat) * R * 0.13 : 0;
    // тінь
    g.fillStyle = 'rgba(10, 30, 45, 0.3)';
    g.beginPath();
    g.ellipse(cx + 2, cy + 3, R, R * 0.92, 0, 0, Math.PI * 2);
    g.fill();
    // валянки: два носки визирають у бік обличчя (у шипах — темні, з цвяшками)
    const bootA = Math.atan2(fy, fx);
    for (let k = -1; k <= 1; k += 2) {
      const side = 0.5 * k;
      const bx = cx + Math.cos(bootA + side) * R * 0.78 + fx * walk * k, by = cy + Math.sin(bootA + side) * R * 0.78 + fy * walk * k;
      g.fillStyle = (fl & 2) ? '#39424a' : '#efe6d6';
      g.beginPath();
      g.ellipse(bx, by, R * 0.36, R * 0.26, bootA, 0, Math.PI * 2);
      g.fill();
      if (fl & 128) {      // ковзани: срібне лезо вздовж валянка — носок стирчить з-під кожуха вперед
        g.strokeStyle = '#dfe8ee';
        g.lineWidth = Math.max(2, R * 0.13);
        g.lineCap = 'round';
        g.beginPath();
        g.moveTo(bx - fx * R * 0.3, by - fy * R * 0.3);
        g.lineTo(bx + fx * R * 0.8, by + fy * R * 0.8);
        g.stroke();
        g.strokeStyle = '#6b7c86';
        g.lineWidth = 1;
        g.stroke();
      }
    }
    // кулак: дві червоні рукавиці обабіч, напоготові
    if (fl & 256) {
      g.fillStyle = '#d8453a';
      for (let k = -1; k <= 1; k += 2) {
        const mx = cx + fx * R * 0.55 - fy * R * 0.9 * k, my = cy + fy * R * 0.55 + fx * R * 0.9 * k;
        g.beginPath();
        g.arc(mx, my, R * 0.3, 0, Math.PI * 2);
        g.fill();
      }
    }
    // глек на спині
    if (fl & 4) {
      g.fillStyle = pal.jug;
      g.beginPath();
      g.arc(cx - fx * R * 0.62, cy - fy * R * 0.62, R * 0.6, 0, Math.PI * 2);
      g.fill();
      g.strokeStyle = '#f4c542';
      g.lineWidth = 1.5;
      g.stroke();
    }
    // кожух
    g.fillStyle = color;
    g.beginPath();
    g.arc(cx, cy, R, 0, Math.PI * 2);
    g.fill();
    g.strokeStyle = darker(color, 0.45);
    g.lineWidth = 2;
    g.stroke();
    // відблиск
    g.fillStyle = 'rgba(255,255,255,0.22)';
    g.beginPath();
    g.arc(cx - R * 0.35, cy - R * 0.38, R * 0.36, 0, Math.PI * 2);
    g.fill();
    // шапка: темніша за кожух, біла в'язана смуга, помпон ззаду, номер місця
    const hx = cx + fx * R * 0.12, hy = cy + fy * R * 0.12, hr = R * 0.56;
    g.fillStyle = darker(color, 0.38);
    g.beginPath();
    g.arc(hx, hy, hr, 0, Math.PI * 2);
    g.fill();
    g.strokeStyle = 'rgba(255,255,255,0.9)';
    g.lineWidth = Math.max(1.5, R * 0.14);
    g.stroke();
    g.fillStyle = '#ffffff';
    g.beginPath();
    g.arc(hx - fx * hr * 0.95, hy - fy * hr * 0.95, R * 0.22, 0, Math.PI * 2);
    g.fill();
    g.font = '800 ' + Math.max(8, Math.round(R * 0.7)) + 'px ' + pal.font;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillText(String(seat + 1), hx, hy + 0.5);
    // очі з-під шапки, у бік обличчя; щойно зачепили — круглі від подиву
    if (R >= 9) {
      const hitNow = now - st.hitFlash[seat] < 260;
      const er = R * (hitNow ? 0.17 : 0.13), ex = cx + fx * R * 0.72, ey = cy + fy * R * 0.72;
      for (let k = -1; k <= 1; k += 2) {
        const px = ex - fy * R * 0.24 * k, py = ey + fx * R * 0.24 * k;
        g.fillStyle = '#ffffff';
        g.beginPath();
        g.arc(px, py, er, 0, Math.PI * 2);
        g.fill();
        g.fillStyle = '#1b2530';
        g.beginPath();
        g.arc(px + fx * er * 0.35, py + fy * er * 0.35, er * (hitNow ? 0.35 : 0.55), 0, Math.PI * 2);
        g.fill();
      }
    }
    // іній: блакитна кірка поверх кожуха й шапки
    if (fl & 512) {
      g.fillStyle = 'rgba(190, 235, 255, 0.55)';
      g.strokeStyle = '#eaf8ff';
      g.lineWidth = 2;
      g.beginPath();
      g.arc(cx, cy, R * 1.04, 0, Math.PI * 2);
      g.fill();
      g.stroke();
    }
    // «удар» ривка — біле кільце (з кулаком — червоне); щойно зачепили — спалах
    const lit = Math.max(0, 1 - (now - st.hitFlash[seat]) / 200);
    if ((fl & 8) || lit > 0) {
      g.strokeStyle = (fl & 8) && (fl & 256) ? '#ff6b5a' : '#ffffff';
      g.globalAlpha = (fl & 8) ? 0.9 : lit;
      g.lineWidth = 3;
      g.beginPath();
      g.arc(cx, cy, R + 3, 0, Math.PI * 2);
      g.stroke();
      g.globalAlpha = 1;
    }
    if (mine) {
      g.strokeStyle = pal.accent;
      g.lineWidth = 1.5;
      g.setLineDash(DASH3);
      g.beginPath();
      g.arc(cx, cy, R + 6, 0, Math.PI * 2);
      g.stroke();
      g.setLineDash(NO_DASH);
    }
    // нік — окремим проходом після всіх тіл (там же й розводимо їх, щоб не злипались)
    st.lx[seat] = cx;
    st.ly[seat] = cy - R;
    st.lOn[seat] = 1;
    if (mine) {
      const top = cy - R - 25;
      g.fillStyle = pal.accent;
      g.beginPath();
      g.moveTo(cx - 5, top - 6);
      g.lineTo(cx + 5, top - 6);
      g.lineTo(cx, top);
      g.closePath();
      g.fill();
      if (ready) {
        g.font = '800 13px ' + pal.font;
        g.fillStyle = pal.accent;
        g.textBaseline = 'bottom';
        g.fillText('ти', cx, top - 7);
      }
    }
  }

  /// Вибулий на березі: маленька фігурка, над нею сніжки; мені — ще й стрілка прицілу.
  function drawBank(st, g, pal, seat, q, sc, mine, aimFace) {
    // Камера наїхала — берег за кадром: ставимо фігурку біля краю кадру на тому ж промені. Там уже вода,
    // тож вибулий бултихається в рятувальному колі (і справді ж «у воді»).
    const R = st.bodyR * sc * 0.6, color = pal.seats[seat];
    const dx = (q[0] - st.C) * sc * st.z, dy = (q[1] - st.C) * sc * st.z;
    // фігурка з колом росте разом із наїздом — тримаємо її цілою в кадрі (згори ще й підпис ❄N)
    const side = (R * 1.6 + 3) * st.z;
    const limX = SIZE / 2 - side, limY = SIZE / 2 - side - (dy < 0 && q[7] > 0 ? 12 * st.z : 0);
    const k = Math.min(1, limX / Math.max(1e-6, Math.abs(dx)), limY / Math.max(1e-6, Math.abs(dy)));
    const cx = st.C * sc + (dx * k) / st.z, cy = st.C * sc + (dy * k) / st.z;
    st.bankOff[2 * seat] = cx - q[0] * sc;
    st.bankOff[2 * seat + 1] = cy - q[1] * sc;
    if (k < 1 && Math.hypot(dx * k, dy * k) / (sc * st.z) < st.shore) {
      g.lineWidth = R * 0.55;
      g.strokeStyle = '#f4f1ea';
      g.beginPath();
      g.arc(cx, cy, R * 1.25, 0, Math.PI * 2);
      g.stroke();
      g.strokeStyle = '#e0533f';
      ringDash[0] = R * 0.7;
      ringDash[1] = R * 0.95;
      g.setLineDash(ringDash);
      g.stroke();
      g.setLineDash(NO_DASH);
    }
    if (mine && q[7] > 0 && aimFace >= 0) {
      const a = aimFace;
      g.strokeStyle = pal.accent;
      g.lineWidth = 2;
      g.setLineDash(DASH5);
      g.beginPath();
      g.moveTo(cx, cy);
      g.lineTo(cx + COS16[a] * 70, cy + SIN16[a] * 70);
      g.stroke();
      g.setLineDash(NO_DASH);
      g.fillStyle = pal.accent;
      g.beginPath();
      const tx = cx + COS16[a] * 78, ty = cy + SIN16[a] * 78;
      g.moveTo(tx, ty);
      g.lineTo(tx - COS16[a] * 10 - SIN16[a] * 6, ty - SIN16[a] * 10 + COS16[a] * 6);
      g.lineTo(tx - COS16[a] * 10 + SIN16[a] * 6, ty - SIN16[a] * 10 - COS16[a] * 6);
      g.closePath();
      g.fill();
    }
    g.fillStyle = color;
    g.globalAlpha = 0.9;
    g.beginPath();
    g.arc(cx, cy, R, 0, Math.PI * 2);
    g.fill();
    g.globalAlpha = 1;
    g.strokeStyle = mine ? pal.accent : 'rgba(0,0,0,0.35)';
    g.lineWidth = mine ? 2 : 1.5;
    g.stroke();
    g.fillStyle = 'rgba(10, 20, 26, 0.9)';
    g.font = '800 ' + Math.max(7, Math.round(R * 0.9)) + 'px ' + pal.font;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillText(String(seat + 1), cx, cy + 0.5);
    if (q[7] > 0) {
      g.font = '700 10px ' + pal.font;
      g.fillStyle = '#ffffff';
      g.lineWidth = 3;
      g.strokeStyle = 'rgba(8, 20, 28, 0.8)';
      g.textBaseline = 'bottom';
      g.strokeText('❄' + q[7], cx, cy - R - 1);
      g.fillText('❄' + q[7], cx, cy - R - 1);
    }
  }

  /// Ніки над тілами: спершу свій, далі за місцями; нік, що наліз би на вже намальований, пропускаємо —
  /// у тісняві на вісьмох краще номер на шапці, ніж каша з літер. На телефоні — лише свій, коли гравців > 4.
  function drawLabels(st, g, pal, me) {
    const small = st.cv && st.cv.el.clientWidth < 420;
    let n = 0;
    for (let k = -1; k < 8; k++) {
      const s = k < 0 ? me : k;
      if (s < 0 || (k >= 0 && s === me) || !st.lOn[s]) continue;
      if (small && s !== me && st.players > 4) continue;
      const nk = st.ctx && nameAt(st.ctx, s);
      if (!nk) continue;
      const l = label(st, pal, s, nk);
      // ніки — поза наїздом камери: тіла ростуть, а літери лишаються сталими; біля краю — всередину кадру
      const x = clamp(viewX(st, st.lx[s]) - l.w / 2, 2, SIZE - l.w - 2);
      const y = clamp(viewX(st, st.ly[s]) - l.h - (s === me ? 7 * st.z : 2), 2, SIZE - l.h - 2);
      let hit = false;
      for (let j = 0; j < n; j++) {
        const o = j * 4;
        if (x < st.lr[o + 2] && x + l.w > st.lr[o] && y < st.lr[o + 3] && y + l.h > st.lr[o + 1]) { hit = true; break; }
      }
      if (hit) continue;
      g.drawImage(l.cv, x, y, l.w, l.h);
      const o = n * 4;
      st.lr[o] = x; st.lr[o + 1] = y; st.lr[o + 2] = x + l.w; st.lr[o + 3] = y + l.h;
      n++;
    }
  }

  function drawPops(st, g, pal, now) {
    if (!st.pops.length) return;
    st.pops = st.pops.filter((p) => now - p.at < 1400);
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.font = '800 15px ' + pal.font;
    g.lineWidth = 4;
    g.strokeStyle = 'rgba(8, 20, 28, 0.85)';
    for (const p of st.pops) {
      const k = (now - p.at) / 1400;
      if (p.w == null) p.w = g.measureText(p.text).width;
      const x = Math.max(p.w / 2 + 8, Math.min(SIZE - p.w / 2 - 8, viewX(st, p.x))), y = Math.max(20, Math.min(SIZE - 16, viewX(st, p.y)) - k * 24);
      g.globalAlpha = 1 - k * k;
      g.strokeText(p.text, x, y);
      g.fillStyle = p.color || pal.text;
      g.fillText(p.text, x, y);
    }
    g.globalAlpha = 1;
  }

  function shade(g, pal) {
    g.fillStyle = pal.shade;
    g.fillRect(0, 0, SIZE, SIZE);
  }

  function text(g, pal, t, x, y, size, color, weight) {
    g.font = (weight || 800) + ' ' + size + 'px ' + pal.font;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.lineWidth = Math.max(3, size / 8);
    g.strokeStyle = 'rgba(8, 20, 28, 0.7)';
    g.strokeText(t, x, y);
    g.fillStyle = color || pal.text;
    g.fillText(t, x, y);
  }

  /// Нічия раунду: на стелі 75 с на кризі ще стояли двоє й більше — чи всі шубовснули разом. Причину каже сервер
  /// (lastRound.byTime): у фазі кінця світ ще доковзує, і з живого кадру вгадувати її не можна.
  const drawnByTime = (v) => !!(v && v.lastRound && v.lastRound.byTime);

  const clamp = (v, a, b) => Math.min(Math.max(v, a), b);

  const dots = (w, need) => (need <= 1 ? (w > 0 ? '●' : '○') : '●'.repeat(Math.min(w, need)) + '○'.repeat(Math.max(0, need - w)));

  function overlays(st, g, pal, f, ph, now) {
    const ctx = st.ctx, v = ctx && ctx.view;
    const waiting = !ctx || !ctx.playing;
    // кінець раунду: спершу хай усі побачать, як останній шубовснув, — накладка виринає за 0.7 с
    const fade = ph === 2 ? Math.max(0, Math.min(1, (now - st.phAt - 700) / 300)) : 1;
    if (fade <= 0) return;
    g.globalAlpha = fade;
    if (ph === 0 && !waiting) {
      shade(g, pal);
      text(g, pal, String(Math.max(1, Math.ceil(((f.left || 0) * TICK_MS) / 1000))), SIZE / 2, SIZE / 2 - 10, 84);
      text(g, pal, 'Раунд ' + ((v && v.round) || 1) + (v && v.need > 1 ? ' · до ' + v.need + ' перемог' : ''), SIZE / 2, SIZE / 2 + 56, 20, pal.text, 700);
      return;
    }
    if (ph === 2 && v) {
      shade(g, pal);
      const w = v.lastRound ? v.lastRound.winner : -1;
      if (v.roundTeam >= 0) text(g, pal, '🧊 Раунд — ' + TEAM_NAMES[v.roundTeam] + '!', SIZE / 2, 150, 32, v.roundTeam === 0 ? pal.team0 : pal.team1);
      else if (w >= 0) text(g, pal, '🧊 Раунд — ' + nick(st, w) + '!', SIZE / 2, 150, 32, pal.seats[w]);
      else text(g, pal, drawnByTime(v) ? '⏱ Час вийшов — нічия' : 'Усі шубовснули — нічия', SIZE / 2, 150, 30);
      table(st, g, pal, v, 200, false);
      g.globalAlpha = 1;
      return;
    }
    if (ph === 3 && v) {
      shade(g, pal);
      const ws = (v.winners && v.winners.length ? v.winners : v.winner != null ? [v.winner] : []);
      if (ws.length) {
        text(g, pal, '🏆 ' + ws.map((s) => nick(st, s)).join(' і '), SIZE / 2, 130, ws.length > 1 ? 28 : 36, pal.seats[ws[0]]);
        text(g, pal, ws.length > 1 ? 'спільна перемога' : 'останній на кризі', SIZE / 2, 172, 18, pal.text, 600);
      } else text(g, pal, 'Партію зіграно — нічия', SIZE / 2, 150, 30);
      table(st, g, pal, v, 210, true);
    }
    g.globalAlpha = 1;
  }

  /// Табличка партії: нік · раунди (кружками) · випхнув.
  function table(st, g, pal, v, top, final) {
    const rows = [];
    for (let s = 0; s < 8; s++) if (v.wins && v.wins[s] != null) rows.push(s);
    rows.sort((a, b) => (v.wins[b] - v.wins[a]) || ((v.pushouts[b] || 0) - (v.pushouts[a] || 0)) || a - b);
    const lh = rows.length > 6 ? 29 : 34;
    // напівпрозора плашка: тіла на кризі не лізуть між ніками й числами
    g.fillStyle = 'rgba(8, 20, 28, 0.72)';
    g.beginPath();
    g.roundRect(112, top - 22, 408, 44 + rows.length * lh, 14);
    g.fill();
    g.font = '600 15px ' + pal.font;
    g.textBaseline = 'middle';
    g.fillStyle = pal.muted;
    g.textAlign = 'left';
    g.fillText('гравець', 150, top);
    g.textAlign = 'center';
    g.fillText(final ? 'раундів' : 'перемоги', 380, top);
    g.fillText('💨 випхнув', 480, top);
    rows.forEach((s, i) => {
      const y = top + 26 + i * lh;
      g.fillStyle = pal.seats[s];
      g.beginPath();
      g.arc(136, y, 8, 0, Math.PI * 2);
      g.fill();
      g.font = '700 19px ' + pal.font;
      g.textAlign = 'left';
      g.fillStyle = pal.text;
      const n = nick(st, s);
      g.fillText(n.length > 16 ? n.slice(0, 15) + '…' : n, 150, y);
      g.textAlign = 'center';
      g.fillStyle = pal.seats[s];
      g.fillText(final ? String(v.wins[s]) : dots(v.wins[s], v.need || 2), 380, y);
      g.fillStyle = pal.text;
      g.fillText(String(v.pushouts[s] || 0), 480, y);
    });
  }

  /// Наїзд камери: кадр тримає кригу (після танення) і живі тіла з запасом CAM_MARGIN, не ближче ZOOM_MAX.
  /// На відліку й у лобі — одразу, у грі — плавно (відкол зменшує кригу стрибком, а камера під'їжджає за секунду).
  function camera(st, f, ph, dt) {
    const melt = (f && f.melt) || 0;
    let ext = 0;
    for (let i = 0; i < 24; i++) { const r = st.R[i] - melt; if (r > ext) ext = r; }
    if (f && f.p && ph !== 4) {
      for (let i = 0; i < 8; i++) {
        const q = f.p[i];
        if (!q || !(q[5] & 1)) continue;
        const d = Math.hypot(st.bx[i] - st.C, st.by[i] - st.C) + st.bodyR;
        if (d > ext) ext = d;
      }
    }
    st.ext = ext;
    const want = Math.min(st.half, Math.max(st.half / ZOOM_MAX, ext + CAM_MARGIN));
    const zt = st.half / want;
    if (ph === 0 || ph === 4 || !st.zOk) { st.z = zt; st.zOk = true; }
    else st.z += (zt - st.z) * (1 - Math.exp(-dt / ZOOM_MS));
  }

  function draw(st, now) {
    const c = st.cv;
    if (!c) return;
    const pal = palette(st);
    const g = c.ctx;
    const f = blend(st);
    const ph = phaseOf(st);
    const sc = st.sc;
    const dt = st.lastDraw ? Math.min(100, now - st.lastDraw) : 16;
    st.lastDraw = now;
    camera(st, f, ph, dt);
    g.save();
    g.scale(st.K, st.K);
    const sh = now - st.shake;
    if (st.shakeMs && sh < st.shakeMs && !reduced()) {
      const k = st.shakeAmp * (1 - sh / st.shakeMs);
      g.translate((Math.random() - 0.5) * 2 * k, (Math.random() - 0.5) * 2 * k);
    }
    // шар світу: координати x·sc, камера зсуває ставок так, що крига посередині, і наїжджає на центр
    g.save();
    if (st.z !== 1) {
      g.translate(SIZE / 2, SIZE / 2);
      g.scale(st.z, st.z);
      g.translate(-SIZE / 2, -SIZE / 2);
    }
    g.drawImage(background(st, pal), 0, 0, SIZE, SIZE);
    g.translate(-st.cam0 * sc, -st.cam0 * sc);
    drawIce(st, g, pal, f, sc, now);
    if (f && f.p) {
      if (ph < 2) drawPickups(st, g, pal, f, sc, now);   // під підсумком раунду й партії плитки лише заважають таблиці
      const me = mySeat(st);
      let n = 0;
      for (let i = 0; i < 8; i++) if (f.p[i]) n++;
      st.players = n;
      const ready = ph === 0 && st.ctx && st.ctx.playing;
      st.lOn.fill(0);
      // спершу берег (під усім), далі тіла; своє — останнім, щоб було зверху
      for (let i = 0; i < 8; i++) {
        const q = f.p[i];
        if (q && !(q[5] & 1) && ph !== 4) drawBank(st, g, pal, i, q, sc, i === me, ph === 1 && i === me ? (st.want >= 0 ? st.want : q[4]) : -1);
      }
      for (let i = 0; i < 8; i++) {
        const q = f.p[i];
        if (!q || !(q[5] & 1) || i === me) continue;
        drawBody(st, g, pal, i, st.bx[i], st.by[i], q[4], q[5], sc, now, false, ready, Math.hypot(q[2], q[3]));
      }
      if (me >= 0 && f.p[me] && (f.p[me][5] & 1)) {
        const q = f.p[me];
        let x = st.meOk && ph === 1 ? st.me.x : st.bx[me], y = st.meOk && ph === 1 ? st.me.y : st.by[me];
        // Своє тіло передбачене, а чужі — на ~100 мс у минулому: на ривку мій кругляш на пару кадрів заходив
        // глибоко в сусіда. Зіткнення все одно судить сервер — тут лише не малюємо тіла одне в одному.
        const rr = 2 * st.bodyR;
        for (let i = 0; i < 8; i++) {
          const o = f.p[i];
          if (i === me || !o || !(o[5] & 1)) continue;
          const dx = x - st.bx[i], dy = y - st.by[i], d = Math.hypot(dx, dy);
          if (d < rr && d > 1e-6) { x = st.bx[i] + (dx / d) * rr; y = st.by[i] + (dy / d) * rr; }
        }
        const face = st.want >= 0 && ph <= 1 ? st.want : q[4];
        const sp = st.meOk && ph === 1 ? Math.hypot(st.me.vx, st.me.vy) : Math.hypot(q[2], q[3]);
        drawBody(st, g, pal, me, x, y, face, q[5], sc, now, true, ready, sp);
      }
      drawBalls(st, g, pal, f, sc);
    }
    drawParts(st, g, pal, dt);
    g.restore();
    // під підсумком раунду й партії ніки над тілами лише лізли б на таблицю
    const plate = ph === 3 || (ph === 2 && now - st.phAt > 700);
    if (f && f.p && !plate) drawLabels(st, g, pal, mySeat(st));
    if (f) overlays(st, g, pal, f, ph, now);
    drawPops(st, g, pal, now);
    const mk = (now - (st.meltAt || -1e9)) / 2200;
    if (ph === 1 && mk < 1) {
      g.globalAlpha = mk < 0.75 ? 1 : (1 - mk) / 0.25;
      text(g, pal, '🌡 Крига тане!', SIZE / 2, 64, 28, pal.danger);
      g.globalAlpha = 1;
    }
    if (ph === 1 && st.ctx && st.ctx.playing && st.lastFrameAt && now - st.lastFrameAt > 1000) text(g, pal, '⏳ зв’язок…', SIZE / 2, 28, 20, pal.text, 700);
    g.restore();
  }

  // ---- рядок над полем (чи стовпчики обабіч ставка) ----
  const secsDown = (ticks) => {
    const t = Math.max(0, Math.ceil((ticks * TICK_MS) / 1000));
    return Math.floor(t / 60) + ':' + String(t % 60).padStart(2, '0');
  };

  /// Чип годинника: скільки минуло, а за 10 с до танення — «🌡 тане за 0:10» (червоніє), а коли тане — скільки
  /// лишилось до кінця раунду. Про танення з 52-ї секунди й стелю 75 с інакше знав лише той, хто читав «що нового».
  function clockChip(f) {
    const rt = CAP_TICKS - (f.left || 0);
    if (f.melt > 0) return '<span class="ifchip ifclock hot">🌡 тане · ще ' + secsDown(f.left || 0) + '</span>';
    const toMelt = MELT_FROM - rt;
    if (toMelt <= 250) return '<span class="ifchip ifclock hot">🌡 тане за ' + secsDown(toMelt) + '</span>';
    return '<span class="ifchip ifclock">⏱ ' + clock(rt) + '</span>';
  }

  /// Відбиток того, що рядок над полем бере з кадру: фаза, секунда годинника, танення й стан кожного місця
  /// (на кризі, шипи, глек, сніжки). Число, а не рядок — HTML перебудовуємо, лише коли щось із цього змінилось.
  function hudKey(f) {
    let k = (f.ph + 1) * 7 + (f.melt > 0 ? 3 : 0) + Math.floor(((f.left || 0) * TICK_MS) / 1000) * 64;
    if (f.p) {
      for (let i = 0; i < 8; i++) {
        const q = f.p[i];
        k = (k * 31 + (q ? (q[5] & 919) * 8 + Math.min(7, q[7]) + 1 : 0)) % 1000000007;
      }
    }
    return k;
  }

  function hudEl(root, cls) {
    let el = root.querySelector(':scope > .' + cls);
    if (el) return el;
    el = document.createElement('div');
    el.className = cls;
    // звук — чипом у цьому ж рядку: окремий рядок під полем з'їдав висоту на ноутбуці
    el.addEventListener('click', (e) => {
      if (!e.target.closest('[data-snd]')) return;
      Snd.set(!Snd.on);
      if (Snd.on) Snd.pick();
      const s = root._icefloe;
      if (s) { s.hudSig = ''; hud(root, s); }
    });
    if (cls === 'ifhud') root.insertBefore(el, root.firstChild);
    else root.appendChild(el);
    return el;
  }

  function hud(root, st) {
    const ctx = st.ctx;
    const el = hudEl(root, 'ifhud');
    const meta = hudEl(root, 'ifmeta');
    const v = ctx.view || {};
    const f = st.last;
    const need = v.need || 2;
    const side = !!st.side;
    let html = '';
    let seated = 0;
    for (let s = 0; s < 8; s++) if (nameAt(ctx, s)) seated++;
    // рядком на п'ятьох і більше — лише номери (свій — із ніком); стовпчиком обабіч ставка ніки влазять усі
    const tight = seated > 4 && !side;
    for (let s = 0; s < 8; s++) {
      const n = nameAt(ctx, s);
      if (!n) continue;
      st.nicks[s] = n;
      const q = f && f.p ? f.p[s] : null;
      const plays = v.wins && v.wins[s] != null;
      let stateTxt = '';
      if (q && plays && v.phase !== 'lobby' && v.phase !== 'over') {
        if (!(q[5] & 1)) stateTxt = '🌊' + (q[7] > 0 ? ' ❄' + q[7] : '');
        else stateTxt = ((q[5] & 512) ? '🧊' : '') + ((q[5] & 2) ? '🥾' : '') + ((q[5] & 128) ? '⛸' : '') + ((q[5] & 4) ? '🏺' : '')
          + ((q[5] & 256) ? '💪' : '') + (q[7] > 0 ? '❄' + q[7] : '');
      }
      html += '<span class="ifchip if' + s + (q && !(q[5] & 1) && plays ? ' out' : '') + (s === ctx.seat ? ' me' : '') + '" title="' + ctx.esc(n) + '">'
        + '<i>' + (s + 1) + '</i>' + (v.teams && v.teams[s] != null ? (v.teams[s] === 0 ? '🔵' : '🔴') : '') + (tight && s !== ctx.seat ? '' : '<span class="ifnick">' + ctx.esc(n) + '</span>')
        + (plays && v.phase !== 'lobby' ? ' <b class="ifdots">' + dots(v.wins[s], need) + '</b>' : '')
        + (plays && v.pushouts[s] ? ' <span class="ifpush">💨' + v.pushouts[s] + '</span>' : '')
        + (stateTxt ? ' <span class="ifst">' + stateTxt + '</span>' : '') + '</span>';
    }
    let tail = '';
    if (f && ctx.playing && f.ph === 1) tail += clockChip(f);
    if (v.round && v.phase !== 'lobby') tail += '<span class="ifchip ifround">раунд ' + v.round + ' · до ' + need + '</span>';
    tail += '<button type="button" class="ifchip ifsnd" data-snd data-pad-skip title="' + (Snd.on ? 'Вимкнути звук' : 'Увімкнути звук') + '">' + (Snd.on ? '🔊' : '🔇') + '</button>';
    // стовпчиком під час гри шапку картки й рядок статусу сховано — підказку (чи підсумок) кажемо тут
    const say = side && st.play ? statusText(ctx) || overText(ctx) : '';
    if (say) tail += '<p class="ifsay">' + ctx.esc(say) + '</p>';
    const sig = (side ? 's' : 'r') + html + '|' + tail;
    if (st.hudSig === sig) return;
    st.hudSig = sig;
    if (side) { el.innerHTML = html; meta.innerHTML = tail; }
    else { el.innerHTML = html + tail; meta.innerHTML = ''; }
  }

  /// Розмір ставка під екран. На Деці (1280×800) з вісьмома шапка картки з місцями й рядок над полем з'їдали
  /// третину висоти, і ставок лишався 440 px з порожнечею обабіч. Тож: на широкій картці гравці стають стовпчиком
  /// ліворуч, годинник і підказка — праворуч, а під час гри шапка й статус картки ховаються (ті самі ніки й слова
  /// є в стовпчиках). Висоту міряємо по-справжньому: від верху ставка до низу вікна мінус кнопки під ним і
  /// смужка підказок пада. Телефон не чіпаємо — там гортають, і сторінку підкручує fitView.
  function fit(root, st) {
    const cv = st.cv && st.cv.el;
    if (!cv || !cv.isConnected || !cv.offsetParent) return;
    const ctx = st.ctx;
    const phone = window.innerWidth < 700 || HGames.ui.coarse();
    const side = !phone && root.clientWidth >= 820;
    const ph = phaseOf(st);
    // Шапку й статус картки ховаємо на всю партію, разом із підсумком (інакше в кінці ставок стрибав би меншим);
    // у лобі вони потрібні — там видно вільні місця й «Чекаємо на гравців».
    const play = ph <= 3 && !!(ctx && ctx.view && ctx.view.phase !== 'lobby');
    if (side !== !!st.side || play !== !!st.play) {
      st.side = side;
      st.play = play;
      root.classList.toggle('ifside', side);
      root.classList.toggle('ifplay', side && play);
      st.hudSig = '';
      hud(root, st);
    }
    // телефон лежачи: стік ліворуч від ставка, «💨/❄» праворуч, ставок за висотою екрана (icefloe.css, .ifland)
    const land = phone && HGames.ui.coarse() && window.innerWidth > window.innerHeight && window.innerHeight <= 500;
    if (land !== !!st.land) {
      st.land = land;
      root.classList.toggle('ifland', land);
      if (st.ctx && st.ctx.playing) setTimeout(() => fitView(root, st), 80);   // повернули посеред партії — знову в кадр
    }
    if (phone) {
      if (cv.style.maxWidth || cv.style.width) { cv.style.maxWidth = ''; cv.style.width = ''; }
      st.labelPx = 12;
      return;
    }
    const card = root.parentElement || root;
    const r = cv.getBoundingClientRect();
    const top = r.top + window.scrollY;
    const bar = document.querySelector('.padhints');
    const barH = bar && !bar.hidden && document.body.classList.contains('pad-on') ? bar.getBoundingClientRect().height + 10 : 0;
    // під ставком: кнопки картки й відступ сторінки під нею. Відступ беремо з документа, але не більше 24 px: коли
    // сторінка коротша за вікно, документ тягнеться до низу вікна, і «відступ» був би всією порожнечею — ставок
    // від цього меншав би, документ ставав би ще «довшим» знизу, і так до упору.
    const cb = card.getBoundingClientRect().bottom;
    const gap = Math.max(0, Math.min(24, document.documentElement.scrollHeight - (cb + window.scrollY)));
    const below = Math.max(0, cb - r.bottom) + gap + barH;
    const big = window.innerWidth >= 1500 && window.innerHeight >= 860;
    let size = Math.floor(window.innerHeight - top - below);
    const wide = side ? root.clientWidth - 2 * 130 - 28 : root.clientWidth;
    size = Math.max(300, Math.min(size, big ? 760 : 680, wide));
    const cur = side ? parseFloat(cv.style.width) : parseFloat(cv.style.maxWidth);
    if (!(Math.abs(cur - size) < 2)) {
      if (side) { cv.style.width = size + 'px'; cv.style.maxWidth = 'none'; }
      else { cv.style.width = ''; cv.style.maxWidth = size + 'px'; }
    }
    // ніки над тілами — не дрібніші за ~12 css-px, хоч би яким малим був ставок
    const css = side ? size : Math.min(size, cv.clientWidth || size);
    st.labelPx = Math.round(12 * Math.max(1, Math.min(1.6, SIZE / Math.max(200, css))));
  }

  // ---- керування пальцем: віртуальний стік і дві кнопки ----
  function controls(root, st) {
    const ctx = st.ctx;
    let el = root.querySelector(':scope > .ifctl');
    const want = ctx.mine && HGames.ui.coarse();
    if (!want) {
      if (el) { el.remove(); st.stickA = null; push(st); }
      return;
    }
    if (!el) {
      el = document.createElement('div');
      el.className = 'ifctl';
      el.innerHTML = '<div class="ifstick" aria-label="стік: куди ковзати"><div class="ifknob"></div></div>'
        + '<div class="ifbtns"><button type="button" class="ifbtn ifdash" aria-label="ривок">💨</button>'
        + '<button type="button" class="ifbtn ifthrow" aria-label="сніжка">❄<b></b></button></div>';
      const stick = el.querySelector('.ifstick'), knob = el.querySelector('.ifknob');
      let pid = null;
      const move = (e) => {
        const r = stick.getBoundingClientRect();
        const dx = e.clientX - (r.left + r.width / 2), dy = e.clientY - (r.top + r.height / 2);
        const m = Math.hypot(dx, dy), lim = r.width / 2 - 24;
        const k = m > lim ? lim / m : 1;
        knob.style.transform = 'translate(' + (dx * k).toFixed(1) + 'px,' + (dy * k).toFixed(1) + 'px)';
        const a = m < 14 ? null : sectorOf(dx, dy);
        const s = root._icefloe;
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
        const s = root._icefloe;
        if (s) { s.stickA = null; push(s); }
      };
      stick.addEventListener('pointerup', up);
      stick.addEventListener('pointercancel', up);
      el.querySelector('.ifdash').addEventListener('pointerdown', (e) => { e.preventDefault(); const s = root._icefloe; if (s) dash(s); });
      el.querySelector('.ifthrow').addEventListener('pointerdown', (e) => { e.preventDefault(); const s = root._icefloe; if (s) toss(s); });
      const cv = st.cv && st.cv.el;
      if (cv && cv.nextSibling) root.insertBefore(el, cv.nextSibling); else root.appendChild(el);
    }
    // лічильник сніжок і «у воді ривок = кидок»
    const s = mySeat(st), q = st.last && st.last.p && s >= 0 ? st.last.p[s] : null;
    const wet = !!q && !(q[5] & 1);
    const n = q ? q[7] : 0;
    const b = el.querySelector('.ifthrow b');
    const t = n > 0 ? String(n) : '';
    if (b.textContent !== t) b.textContent = t;
    el.classList.toggle('wet', wet);
    el.querySelector('.ifthrow').classList.toggle('has', n > 0);
    const d = el.querySelector('.ifdash');
    const dt = wet ? (q && (q[5] & 64) ? '🔨' : '❄') : '💨';
    if (d.textContent !== dt) d.textContent = dt;
  }

  function wireCanvas(root, st) {
    const el = st.cv.el;
    if (el._ifWired) return;
    el._ifWired = true;
    const S = () => root._icefloe;
    el.addEventListener('contextmenu', (e) => { if (S() && S().ctx.mine) e.preventDefault(); });
    el.addEventListener('pointerdown', (e) => {
      const s = S();
      if (!s || !s.ctx.mine || !s.ctx.playing) return;
      s.mouseX = e.clientX;
      s.mouseY = e.clientY;
      if (e.button === 2) { e.preventDefault(); if (inWater(s)) { aimMouse(s); push(s, true); } dash(s); return; }
      if (e.button === 1) { e.preventDefault(); toss(s); return; }
      if (e.button !== 0) return;
      e.preventDefault();
      if (inWater(s)) { aimMouse(s); push(s, true); toss(s); return; }   // з берега: клацнув — кинув туди
      s.mouseDown = true;
      try { el.setPointerCapture(e.pointerId); } catch { /* без capture */ }
      aimMouse(s);
    });
    el.addEventListener('pointermove', (e) => {
      const s = S();
      if (!s) return;
      s.mouseX = e.clientX;
      s.mouseY = e.clientY;
      if (s.ctx.mine && s.ctx.playing && (s.mouseDown || (inWater(s) && e.pointerType === 'mouse'))) aimMouse(s);
    });
    const up = () => {
      const s = S();
      if (!s || !s.mouseDown) return;
      s.mouseDown = false;
      s.mouseA = null;
      push(s);
    };
    el.addEventListener('pointerup', up);
    el.addEventListener('pointercancel', up);
  }

  /// Пад: партію зіграно — рамку на «Ще раз». Інакше вона лишалась там, куди її поставило перше пробудження пада
  /// (на першу кнопку сторінки — «📻 Ефір»), і Ⓐ після партії виносило зі столу.
  function padToRematch(root) {
    if (!window.HPad || !HPad.on || !HPad.focus) return;
    setTimeout(() => {
      const card = root.parentElement;
      const b = (card && card.querySelector('.gbtns [data-do="Rematch"]')) || document.querySelector('.grback');
      if (b && b.isConnected) HPad.focus(b);
    }, 80);
  }

  /// Скільки в'юпорта вільно: каркасний ui.fit(), а без нього — липка шапка й нижні панелі з CSS-змінних.
  function fitNow() {
    if (HGames.ui.fit) return HGames.ui.fit();
    const cs = getComputedStyle(document.documentElement);
    const n = (v) => parseFloat(cs.getPropertyValue(v)) || 0;
    const hd = document.querySelector('header');
    const top = hd && getComputedStyle(hd).position === 'sticky' ? hd.getBoundingClientRect().height : 0;
    const vv = window.visualViewport;
    return { w: vv ? vv.width : window.innerWidth, h: vv ? vv.height : window.innerHeight, top, dock: n('--gdock-h') || 64 };   // --tabs-h — calc(58px + safe-area), parseFloat дає NaN
  }

  /// Телефон: на старті партії підкручуємо сторінку так, щоб ставок і стік із кнопками стали між шапкою й
  /// нижніми панелями. Раз на партію (і після F5) — далі людина гортає сама.
  function fitView(root, st) {
    if (!st.cv || !HGames.ui.coarse()) return;
    const a = st.cv.el.getBoundingClientRect();
    const ctl = root.querySelector(':scope > .ifctl');
    const bottomEl = ctl ? ctl.getBoundingClientRect().bottom : a.bottom;
    const h = st.land ? a.height : bottomEl - a.top;   // лежачи керування обабіч ставка
    const f = fitNow();
    const top = f.top + 4, bottom = f.h - f.dock - 4;
    const want = h <= bottom - top ? top + (bottom - top - h) / 2 : bottom - h;
    const d = a.top - want;
    if (Math.abs(d) > 24) window.scrollBy({ top: d, behavior: reduced() ? 'auto' : 'smooth' });
  }

  /// Цикл живе, поки йде партія (кадри, передбачення свого тіла, пад, ввід), і ще AWAKE_MS після останньої
  /// події — догорають бризки, скалки й накладки. У лобі й на підсумку засинає: 60 разів на секунду
  /// перемальовувати застиглий ставок нема чого. Будять update(), frame() і зміна розміру вікна.
  const AWAKE_MS = 1500;
  function spin(root, st) {
    st.awakeUntil = performance.now() + AWAKE_MS;
    if (st.raf) return;
    const loop = () => {
      if (!st.cv || !st.cv.el.isConnected) { st.raf = 0; return; }
      if (!(st.ctx && st.ctx.playing) && performance.now() > st.awakeUntil) { st.raf = 0; return; }
      st.raf = requestAnimationFrame(loop);
      const now = performance.now();
      readPad(st);
      if (st.mouseDown) aimMouse(st);
      // відкладена зміна наміру; той самий напрямок, поки тримають, — підтверджуємо раз на KEEP_MS
      if (st.sent !== currentWant(st) && canSend(st) && now - st.sentAt >= SEND_MS) push(st);
      else if (st.sent != null && st.sent >= 0 && canSend(st) && now - st.sentAt >= KEEP_MS) push(st, true, true);
      predict(st, now);
      // схована картка чи вкладка — стан приймаємо, а малювати нема кому
      if (!st.cv.el.offsetParent || document.hidden) return;
      // смужка пада з'явилась, балачки згорнули, «на весь екран» — розмір ставка перераховуємо раз на пів секунди
      if (now - (st.fitAt || 0) > 500) { st.fitAt = now; fit(root, st); }
      const t0 = performance.now();
      draw(st, now);
      const ms = performance.now() - t0;
      st.drawMs.push(ms);
      if (st.drawMs.length > 300) st.drawMs.shift();
    };
    st.raf = requestAnimationFrame(loop);
  }

  /// Підсумок партії словами — для правого стовпчика, коли рядок статусу картки сховано.
  function overText(ctx) {
    const res = ctx.room && ctx.room.status === 'finished' && ctx.room.result;
    if (!res) return '';
    const ws = res.winners || [];
    if (res.draw || !ws.length) return 'Партію зіграно — нічия';
    return 'Перемога: ' + ws.map((i) => nameAt(ctx, i) || ctx.seatName(i)).join(', ');
  }

  /// Рядок статусу під грою (і в правому стовпчику на широкому екрані, де рядок картки сховано).
  function statusText(ctx) {
    const st = [...live].find((s) => s.ctx === ctx);
    const v = ctx.view || {};
    const f = ctx.frame || v.frame;
    const need = v.need || 2;
    if (!ctx.playing) {
      if (ctx.room && ctx.room.status === 'lobby') {
        const host = ctx.room.host && ctx.me && String(ctx.room.host).toLowerCase() === String(ctx.me.nick).toLowerCase();
        if (v.teams) return '🔵🔴 Командами: сідай по черзі — 1-й, 3-й, 5-й… сині, 2-й, 4-й… руді; треба 4, 6 чи 8. Раунд бере команда — прикривай спину';
        return host ? 'Тисни «Почати», коли всі сіли (2–8)' : 'Сумо на кризі. Стартує господар, коли зібралось 2–8';
      }
      return '';
    }
    if (st && performance.now() - st.lastFrameAt > 1000 && st.lastFrameAt && f && f.ph === 1) return '⏳ зв\'язок…';
    if (!f) return '';
    if (f.ph === 0) return 'Готуйсь… раунд ' + (v.round || 1);
    if (f.ph === 2) {
      const w = v.lastRound ? v.lastRound.winner : -1;
      if (v.roundTeam >= 0) return 'Раунд — ' + TEAM_NAMES[v.roundTeam] + '!';
      return w >= 0 ? 'Раунд — ' + (nameAt(ctx, w) || ctx.seatName(w)) + '!' : (drawnByTime(v) ? 'Час вийшов — нічия раунду' : 'Усі шубовснули — нічия раунду');
    }
    if (!ctx.mine) return 'Дивишся збоку · раунд ' + (v.round || 1) + ' · до ' + need + (need > 1 ? ' перемог' : ' перемоги');
    const q = f.p && f.p[ctx.seat];
    if (q && !(q[5] & 1)) {
      if (q[5] & 64) {
        return '🌊 Ти у воді — 🔨 раз за раунд відколи кригу: ' + (padOn() ? 'цілься стіком, Ⓐ' : HGames.ui.coarse() ? 'цілься стіком, 🔨' : 'права кнопка миші туди, де колоти (чи прицілься й пробіл)')
          + (q[7] > 0 ? ' · сніжки: ' + (padOn() ? 'Ⓧ' : HGames.ui.coarse() ? '❄' : 'клік чи X') + ' (' + q[7] + ')' : '');
      }
      return q[7] > 0 ? '🌊 Ти у воді — ' + (padOn() ? 'стік цілить, Ⓐ кидає' : HGames.ui.coarse() ? 'стік цілить, ❄ кидає' : 'стрілки чи мишка цілять, пробіл кидає') + ' сніжку (лишилось ' + q[7] + ')'
        : '🌊 Ти у воді, сніжки скінчились — дивись, хто кого';
    }
    if (q && (q[5] & 512)) return '🧊 Замерз! Ще мить — і знову ковзаєш';
    const extra = q ? ((q[5] & 2) ? ' · 🥾 шипи' : '') + ((q[5] & 128) ? ' · ⛸ ковзани' : '') + ((q[5] & 4) ? ' · 🏺 глек' : '')
      + ((q[5] & 256) ? ' · 💪 кулак' : '') : '';
    const how = padOn() ? 'Стік — ковзати, Ⓐ ривок, Ⓧ сніжка'
      : HGames.ui.coarse() ? 'Стік — ковзати, 💨 ривок, ❄ сніжка' : 'Стрілки/WASD — ковзати, пробіл — ривок, X — сніжка';
    const team = v.teams && v.teams[ctx.seat] != null ? ' · ти за ' + TEAM_NAMES[v.teams[ctx.seat]] : '';
    return how + ' · раунд ' + (v.round || 1) + ' · до ' + need + team + extra;
  }

  HGames.register({
    id: 'icefloe',
    added: '2026-09-27',
    icon: ICON,
    seatNames: ['синій', 'рудий', 'зелений', 'жовтий', 'бузковий', 'м’ятний', 'рожевий', 'сірий'],
    seatClass: ['if0', 'if1', 'if2', 'if3', 'if4', 'if5', 'if6', 'if7'],
    pad: { dirs: true, a: 'Space', x: 'KeyX', hint: '{dpad} ковзати · {a} ривок · {x} сніжка' },
    news: {
      v: '2026-09-30',
      title: 'Крижина: нові предмети, сніжки вдвічі й соло з ботами',
      items: [
        '❄ Сніжка б\'є вдвічі сильніше — дужче за ривок; з берега їх тепер чотири, а підбирачка дає одразу дві',
        '🌬 Завірюха — підібрав, і всіх поруч відкидає від тебе хвилею · 🧊 Іній — суперники секунду без керування',
        '⛸ Ковзани — розгін у півтора раза, але й несе далі · 💪 Кулак — ривок і удар ривка значно важчі',
        '🎁 Предмети з\'являються частіше (раз на 4 с, до 3–4 на кризі) і діють 10 с; глек тепер тримає й сніжки, шипи — ще міцніше',
        '🤖 Сам біля ставка? Тисни «🤖 + бот» — на кригу вийдуть двоє ботів, і штовхатимуть і тебе, й одне одного (без нагород)',
        '❄ Боти бережуть край і заходять з боку центру; шубовснули — кидають сніжки й колють лід з берега',
        '🎚 Опція «🤖 Бот»: легкий пре навпростець і сам буває у воді, сильний тримає інерцію й б’є на впередження',
      ],
    },

    mount(root, ctx) {
      const st = state(root, ctx);
      takeView(st, ctx.view);
      if (ctx.view && ctx.view.frame) st.last = ctx.view.frame;
      hud(root, st);
      st.cv = HGames.ui.canvas(root, { w: SIZE * st.K, h: SIZE * st.K, cls: 'ifboard' });
      hud(root, st);
      st.cv.el.classList.toggle('play', !!ctx.mine);
      wireCanvas(root, st);
      controls(root, st);
      st.onResize = () => { const s = root._icefloe; if (s) { fit(root, s); spin(root, s); } };
      window.addEventListener('resize', st.onResize);
      // поворот, ⛶ і шторка міняють місце під ставок без resize вікна — каркас кличе нас сам
      if (HGames.ui.onFit) HGames.ui.onFit(root, () => st.onResize());
      fit(root, st);
      spin(root, st);
    },

    update(root, ctx) {
      const st = state(root, ctx);
      const v = ctx.view;
      const ivWas = st.iceIv;
      takeView(st, v);
      // Вид — правда поза грою (лобі, кінець партії, F5). Посеред гри кадри свіжіші, але новий раунд
      // (інша крига) чи повернення після F5 — теж привід узяти кадр із виду й забути згладжування.
      const vf = v && v.frame;
      if (vf && (!ctx.playing || !st.last || v.phase === 'lobby' || v.phase === 'over' || (v.ice && v.ice.iv !== ivWas && vf.t >= (st.last.t || 0)))) {
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
      // глядач на телефоні теж бачить ставок цілком
      if (ctx.playing && !ctx.mine && !st.watchFit) { st.watchFit = true; setTimeout(() => fitView(root, st), 60); }
      if (!ctx.playing) { st.sent = null; st.meOk = false; st.watchFit = false; }
      const status = ctx.room && ctx.room.status;
      if (status === 'finished' && st.status === 'playing' && ctx.mine) padToRematch(root);
      st.status = status;
      st.was = !!(ctx.playing && ctx.mine);
      spin(root, st);
    },

    frame(root, ctx, f) {
      const st = state(root, ctx);
      if (!f) return;
      if (st.last && (f.t < st.last.t - 2 || f.iv !== st.last.iv)) st.interp.reset();
      if (!st.last || st.last.ph !== f.ph) st.phAt = performance.now();
      events(st, f);
      if (st.last && !(st.last.melt > 0) && f.melt > 0 && f.ph === 1 && st.cv && st.cv.el.offsetParent) {
        // крига почала танути — кажемо вголос банером угорі, а не лише мокрою смугою по краю
        st.meltAt = performance.now();
        Snd.crack();
      }
      st.last = f;
      st.lastFrameAt = performance.now();
      // У воді мишка цілить і без натиснутої кнопки (mouseA). Вийшов на кригу (новий раунд чи партія) — приціл мусить
      // згаснути, інакше досилання раз на 0.4 с тягло тіло туди, куди востаннє цілився з берега.
      if (st.mouseA != null && !st.mouseDown && !inWater(st)) { st.mouseA = null; push(st, true); }
      st.interp.push(f);
      correct(st, f);
      // рядок над полем — лише коли в кадрі змінилось те, що він показує
      const hk = hudKey(f);
      if (hk !== st.hudK) { st.hudK = hk; hud(root, st); }
      if (ctx.mine && HGames.ui.coarse()) controls(root, st);
      spin(root, st);
    },

    onKey(e, ctx) {
      const st = [...live].find((s) => s.ctx === ctx);
      if (!st || !ctx.mine) return false;
      const k = keyOf(e);
      // Утримання пам'ятаємо й поза грою: затиснув стрілку ще на відліку «Ще раз» — поїдеш зі старту.
      if (k && !held[k]) { held[k] = true; push(st); }
      if (!ctx.playing) return false;
      if (k) return true;
      if (isDash(e)) { if (!e.repeat) dash(st); return true; }
      if (isThrow(e)) { if (!e.repeat) toss(st); return true; }
      return false;
    },

    status: statusText,

    unmount(root) {
      const st = root._icefloe;
      if (!st) return;
      cancelAnimationFrame(st.raf);
      st.raf = 0;
      if (st.onResize) window.removeEventListener('resize', st.onResize);
      root.classList.remove('ifside', 'ifplay', 'ifland');
      live.delete(st);
      root._icefloe = null;
    },
  });
})();
