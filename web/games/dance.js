/*
  Вечорниці (dance) — Unspottable на сільських танцях. Реалтайм 25 Гц: сервер тикає раз на 40 мс і шле кадр, ми
  згладжуємо його й малюємо. Усіх танцюристів — і ботів, і гравців, і себе — малюємо однаково, з кадрів через
  інтерполяцію: так свого не видасть ні лаг, ні «підсмикування». Передбачаємо лише косметику свого: поворот, ходу й
  фігуру на натиск (до 250 мс, поки кадр не підтвердить).

  Кадр (Impl/Dance.cs): { t, ph: 'start'|'go'|'reveal'|'over', left, v: [x, y, d, s] × N, m: [fig, beat, lead, tempo], ev }
    s: 0 стоїть, 1 іде, 2 ображений бот сидить, 3 вибулий гравець, 4 отетерів, 5…8 фігура 0…3 у такт, 9…12 фігура 0…3
       не в такт чи не та, 13 збився без фігури («?»); d: 0 праворуч, 1 вниз, 2 ліворуч, 3 вгору.
    m: фігура, яку кличуть (-1 — ніякої), тик такту (між викликами — останнього), тиків від виклику до такту, темп 0…2.
    ev: [1, хто, кого, 0 бот | 1 гравець, місце або -1] — ляпас.
  Вид (Hidden, свій кожному): { phase, round, of, left, t, n, map, circle, need, looks (5 на танцюриста), names, v, m,
    seats, dead, me, reveal, result }; me — лише своєму місцю: { id, alive, streak, good, bad, circle, danced, last, slapCool, stun }.

  Ввід: Input('move', { dir }) лише на зміну (-1 — відпустив); Act('fig', { f }); Act('slap', {} | { id }).
*/
(() => {
  const TICK_MS = 40, CELL = 32, WW = 960, WH = 640, PW = 480, PH = 360;
  const DX = [1, 0, -1, 0], DY = [0, 1, 0, -1];
  const SLAP_RANGE = 36, SLAP_MAX = 52, SLAP_CONE = 500;
  // вікно такту (тики від такту): −5…+10 — у такт, до +25 — ще приймають, але вже «не в такт»
  const EARLY = 5, LATE = 10, LATE_MAX = 25;
  const SLAP_COOL_MS = 2000, STUN_MS = 1520;
  const PULSE = [12, 10, 8];
  const PEEK_MS = 1500, NEWS_MS = 6000, LOCAL_MS = 250, SLAP_FX_MS = 450, TIP_MS = 1500, F5_PEEK_MS = 3000;
  // стіл стоїть (лобі, партію зіграно): стільки ще малюємо після останньої події — довше за найдовшу анімацію
  const IDLE_MS = 2000;
  const HOLD_MS = 1000;
  const TAU = Math.PI * 2;

  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true"><circle cx="6" cy="3.2" r="1.9" fill="var(--accent)"/>'
    + '<path d="M6 5.6v4.8M6 7.2 3.2 4.4M6 7.2l2.8-2.8M6 10.4l-2.3 4M6 10.4l2.3 4" fill="none" stroke="var(--text)" stroke-width="1.4" stroke-linecap="round"/>'
    + '<path d="M12.6 2.6v6.6l2-1" fill="none" stroke="var(--clay)" stroke-width="1.3" stroke-linecap="round"/><ellipse cx="11.5" cy="9.6" rx="1.6" ry="1.2" fill="var(--clay)"/></svg>';

  const SEAT_NAMES = ['жовтий', 'зелений', 'рудий', 'сірий', 'синій', 'рожевий', 'фіолетовий', 'червоний'];
  const SEAT_VARS = [['--accent', '#f4c542'], ['--ok', '#7bd389'], ['--clay', '#c5763a'], ['--muted', '#9db3a5'],
    ['--dance-blue', '#6fb3e8'], ['--dance-pink', '#e88ac0'], ['--dance-violet', '#b48ae8'], ['--dance-red', '#e86a6a']];

  // Фігури: що кричать музики, як підписано кнопку, яка клавіша. Порядок — як на сервері (0…3).
  const FIGS = [
    { call: 'Плескай!', name: 'Плескай', emoji: '👏', key: '1', alt: 'J', pad: 'Ⓧ' },
    { call: 'Присядь!', name: 'Присядь', emoji: '🧎', key: '2', alt: 'K', pad: 'Ⓐ' },
    { call: 'Крутись!', name: 'Крутись', emoji: '🌀', key: '3', alt: 'L', pad: 'RB' },
    { call: 'Руки вгору!', name: 'Руки вгору', emoji: '🙌', key: '4', alt: 'I', pad: 'LB' },
  ];
  const FIG_COLOR = ['#f0b429', '#5fb0e8', '#b48ae8', '#7bd389'];

  // Одяг — палітра вечорниць: червоний, синій, зелений, жовтий, вишневий, фіолетовий, помаранчевий, чорний.
  const CLOTH = ['#c0392b', '#2f6db5', '#3f9142', '#e8c33a', '#8e2442', '#7d4fa8', '#e07b2a', '#2a2a2a'];
  const HAIR = ['#2b1d14', '#4a2f1d', '#7a4b27', '#c9a15a', '#8c4a2f', '#3b2a20', '#6b4a2e', '#1a1410'];
  const SKIN = ['#f3d0b0', '#e2b48c', '#c48a5e', '#8d5a3b'];
  const SHIRT = '#f6f1e4';
  const CLOTH_F = ['червоній', 'синій', 'зеленій', 'жовтій', 'вишневій', 'фіолетовій', 'помаранчевій', 'чорній'];
  const CLOTH_PL = ['червоними', 'синіми', 'зеленими', 'жовтими', 'вишневими', 'фіолетовими', 'помаранчевими', 'чорними'];
  const CLOTH_M = ['червоному', 'синьому', 'зеленому', 'жовтому', 'вишневому', 'фіолетовому', 'помаранчевому', 'чорному'];

  const DIRS = { ArrowRight: 0, KeyD: 0, ArrowDown: 1, KeyS: 1, ArrowLeft: 2, KeyA: 2, ArrowUp: 3, KeyW: 3 };
  const KEY_DIRS = { d: 0, 'в': 0, s: 1, 'і': 1, a: 2, 'ф': 2, w: 3, 'ц': 3 };
  // 1–4 і ромб J K L I (як на паді: ліва — плескай, нижня — присядь, права — крутись, верхня — руки вгору)
  const FIG_CODES = { Digit1: 0, Numpad1: 0, KeyJ: 0, Digit2: 1, Numpad2: 1, KeyK: 1, Digit3: 2, Numpad3: 2, KeyL: 2, Digit4: 3, Numpad4: 3, KeyI: 3 };
  const FIG_KEYS = { 1: 0, j: 0, 'о': 0, 2: 1, k: 1, 'л': 1, 3: 2, l: 2, 'д': 2, 4: 3, i: 3, 'ш': 3 };
  const dirOf = (e) => {
    const d = DIRS[e.code];
    return d !== undefined ? d : KEY_DIRS[String(e.key || '').toLowerCase()];
  };
  const figOf = (e) => {
    const f = FIG_CODES[e.code];
    return f !== undefined ? f : e.code ? undefined : FIG_KEYS[String(e.key || '').toLowerCase()];
  };
  const isSlap = (e) => e.code === 'Space' || e.key === ' ' || e.key === 'Spacebar';
  const isPeek = (e) => e.code === 'KeyQ' || e.code === 'Tab' || (!e.code && ['q', 'й'].includes(String(e.key || '').toLowerCase()));

  const reduced = () => window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  const clamp = (x, a, b) => (x < a ? a : x > b ? b : x);
  const clock = (ticks) => {
    const s = Math.max(0, Math.ceil((ticks * TICK_MS) / 1000));
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  };
  const readMute = () => { try { return localStorage.getItem('danceMute') === '1'; } catch { return false; } };
  const padOn = () => !!(window.HPad && window.HPad.on);

  // ---------------------------------------------------------------------------------------------
  // Палітра і статика (спориш, тин, хата, поміст, коло, лави, столи) — один раз в offscreen-канвас
  // ---------------------------------------------------------------------------------------------

  function palette() {
    const c = HGames.ui.css;
    return {
      grass: c('--dance-grass', '#557f3e'),
      grass2: c('--dance-grass2', '#4a7236'),
      path: c('--dance-path', '#b89868'),
      wood: c('--dance-wood', '#7a4f2c'),
      wall: c('--dance-wall', '#f3ecdc'),
      thatch: c('--dance-thatch', '#c9a24e'),
      ring: c('--dance-ring', '#a58a5c'),
      shadow: c('--dance-shadow', 'rgba(20, 25, 10, .34)'),
      text: c('--text', '#ecf1ea'),
      accent: c('--accent', '#f4c542'),
      danger: c('--danger', '#e57373'),
      ok: c('--ok', '#7bd389'),
      ink: '#1d1a14',
      seats: SEAT_VARS.map(([n, f]) => c(n, f)),
    };
  }

  function lcg(seed) {
    let s = seed >>> 0;
    return () => ((s = (Math.imul(s, 1103515245) + 12345) >>> 0) / 4294967296);
  }

  function drawStatic(map, circle, pal, S) {
    const c = document.createElement('canvas');
    c.width = Math.round(WW * S);
    c.height = Math.round(WH * S);
    const g = c.getContext('2d');
    g.scale(S, S);
    const at = (x, y) => (map[y] && map[y][x]) || '#';
    const rnd = lcg(1709);

    // спориш із плямами й травинками
    g.fillStyle = pal.grass;
    g.fillRect(0, 0, WW, WH);
    g.fillStyle = pal.grass2;
    for (let i = 0; i < 650; i++) {
      g.beginPath();
      g.ellipse(rnd() * WW, rnd() * WH, 3 + rnd() * 7, 2 + rnd() * 3, rnd() * 3, 0, TAU);
      g.fill();
    }
    g.strokeStyle = 'rgba(255, 255, 210, .13)';
    g.lineWidth = 1;
    g.beginPath();
    for (let i = 0; i < 380; i++) {
      const x = rnd() * WW, y = rnd() * WH;
      g.moveTo(x, y); g.lineTo(x - 1.5, y - 4);
      g.moveTo(x + 2, y); g.lineTo(x + 3, y - 3.5);
    }
    g.stroke();

    // стежки
    g.fillStyle = pal.path;
    for (let y = 0; y < map.length; y++)
      for (let x = 0; x < map[y].length; x++)
        if (at(x, y) === '=') { g.beginPath(); g.roundRect(x * CELL - 1, y * CELL - 1, CELL + 2, CELL + 2, 7); g.fill(); }

    // коло: утоптана земля, де танцюють, і чорнобривці по краю
    const cx = circle.x, cy = circle.y, R = circle.r;
    g.fillStyle = pal.ring;
    g.globalAlpha = 0.85;
    g.beginPath(); g.arc(cx, cy, R, 0, TAU); g.fill();
    g.globalAlpha = 1;
    g.fillStyle = 'rgba(80, 55, 30, .18)';
    for (let i = 0; i < 90; i++) {
      const a = rnd() * TAU, r = Math.sqrt(rnd()) * (R - 6);
      g.beginPath(); g.arc(cx + Math.cos(a) * r, cy + Math.sin(a) * r, 0.8 + rnd() * 1.6, 0, TAU); g.fill();
    }
    g.strokeStyle = 'rgba(60, 40, 20, .35)';
    g.lineWidth = 2;
    g.beginPath(); g.arc(cx, cy, R, 0, TAU); g.stroke();
    for (let i = 0; i < 44; i++) {
      const a = (i / 44) * TAU + rnd() * 0.05, r = R + 7;
      const x = cx + Math.cos(a) * r, y = cy + Math.sin(a) * r;
      g.fillStyle = '#2f5e22';
      g.beginPath(); g.arc(x, y + 1.5, 3, 0, TAU); g.fill();
      g.fillStyle = i % 3 ? '#e8892a' : '#f2c230';
      g.beginPath(); g.arc(x, y, 2.4, 0, TAU); g.fill();
    }

    // тин по краю: плетений, на кілках — глечики (це ж Глечики)
    fence(g, pal, rnd);

    for (let y = 0; y < map.length; y++)
      for (let x = 0; x < map[y].length; x++) {
        const ch = at(x, y), px = x * CELL + 16, py = y * CELL + 16;
        if (ch === 'T') tree(g, px, py, rnd);
        else if (ch === 'Y') hay(g, px, py);
      }

    const first = (ch) => {
      for (let y = 0; y < map.length; y++) { const x = map[y].indexOf(ch); if (x >= 0) return [x, y]; }
      return null;
    };
    const blocks = (ch) => {
      // усі прямокутники з літерою ch: [x, y, w, h] у клітинках
      const out = [], seen = new Set();
      for (let y = 0; y < map.length; y++)
        for (let x = 0; x < map[y].length; x++) {
          if (at(x, y) !== ch || seen.has(y * 100 + x)) continue;
          let w = 0, h = 0;
          while (at(x + w, y) === ch) w++;
          while (at(x, y + h) === ch) h++;
          for (let yy = y; yy < y + h; yy++) for (let xx = x; xx < x + w; xx++) seen.add(yy * 100 + xx);
          out.push([x, y, w, h]);
        }
      return out;
    };
    const h = first('H'); if (h) hut(g, h[0] * CELL, h[1] * CELL, pal);
    const w = first('W'); if (w) well(g, w[0] * CELL + 32, w[1] * CELL + 32, pal);
    const m = first('M'); if (m) stage(g, m[0] * CELL, m[1] * CELL, pal);
    for (const b of blocks('L')) bench(g, b, pal);
    for (const b of blocks('S')) table(g, b, pal);

    // вечір: тепла віньєтка по краях
    const vg = g.createRadialGradient(WW / 2, WH / 2, WH * 0.35, WW / 2, WH / 2, WW * 0.62);
    vg.addColorStop(0, 'rgba(40, 20, 0, 0)');
    vg.addColorStop(1, 'rgba(40, 20, 0, .28)');
    g.fillStyle = vg;
    g.fillRect(0, 0, WW, WH);
    return c;
  }

  function fence(g, pal, rnd) {
    g.fillStyle = 'rgba(0, 0, 0, .2)';
    g.fillRect(0, 0, WW, CELL); g.fillRect(0, WH - CELL, WW, CELL); g.fillRect(0, 0, CELL, WH); g.fillRect(WW - CELL, 0, CELL, WH);
    const band = (x, y, w, h, horiz) => {
      g.fillStyle = '#8a6036';
      g.fillRect(x, y, w, h);
      g.strokeStyle = '#5e3d1f';
      g.lineWidth = 1.2;
      g.beginPath();
      if (horiz) for (let i = x; i < x + w; i += 6) { g.moveTo(i, y); g.quadraticCurveTo(i + 3, y + h / 2, i, y + h); }
      else for (let j = y; j < y + h; j += 6) { g.moveTo(x, j); g.quadraticCurveTo(x + w / 2, j + 3, x + w, j); }
      g.stroke();
    };
    band(CELL - 12, CELL - 12, WW - 2 * CELL + 24, 8, true);
    band(CELL - 12, WH - CELL + 4, WW - 2 * CELL + 24, 8, true);
    band(CELL - 12, CELL - 12, 8, WH - 2 * CELL + 24, false);
    band(WW - CELL + 4, CELL - 12, 8, WH - 2 * CELL + 24, false);
    // кілки з глечиками
    const pot = (x, y) => {
      g.fillStyle = '#4a2f18'; g.fillRect(x - 2, y - 4, 4, 10);
      g.fillStyle = rnd() < 0.5 ? '#c5763a' : '#a85a2a';
      g.beginPath(); g.ellipse(x, y - 8, 5, 5.5, 0, 0, TAU); g.fill();
      g.fillRect(x - 2.5, y - 15, 5, 3);
      g.fillStyle = 'rgba(255, 240, 200, .35)';
      g.beginPath(); g.arc(x - 1.8, y - 9.5, 1.5, 0, TAU); g.fill();
    };
    for (let x = CELL * 2; x < WW - CELL; x += CELL * 3) { pot(x, CELL - 6); pot(x, WH - CELL + 10); }
    for (let y = CELL * 3; y < WH - CELL; y += CELL * 3) { pot(CELL - 8, y); pot(WW - CELL + 8, y); }
  }

  function tree(g, x, y, rnd) {
    g.fillStyle = 'rgba(10, 20, 5, .3)';
    g.beginPath(); g.ellipse(x + 5, y + 7, 17, 12, 0, 0, TAU); g.fill();
    g.fillStyle = '#6b4424';
    g.beginPath(); g.arc(x, y + 2, 4, 0, TAU); g.fill();
    const greens = ['#2c5f28', '#37712f', '#46853c'];
    for (let i = 0; i < 3; i++) {
      g.fillStyle = greens[i];
      g.beginPath(); g.arc(x + (i - 1) * 5 + rnd() * 2, y - 3 + (i === 1 ? -4 : 0), 11 - i * 2, 0, TAU); g.fill();
    }
    // вишні
    g.fillStyle = '#b3202b';
    for (let i = 0; i < 4; i++) { g.beginPath(); g.arc(x - 7 + rnd() * 14, y - 9 + rnd() * 10, 1.4, 0, TAU); g.fill(); }
  }

  function hay(g, x, y) {
    g.fillStyle = 'rgba(20, 20, 0, .25)';
    g.beginPath(); g.ellipse(x + 3, y + 6, 15, 10, 0, 0, TAU); g.fill();
    g.fillStyle = '#d9b44a';
    g.beginPath(); g.ellipse(x, y, 14, 11, 0, 0, TAU); g.fill();
    g.strokeStyle = '#a8842c';
    g.lineWidth = 1;
    g.beginPath();
    for (let i = -10; i <= 10; i += 4) { g.moveTo(x + i, y - 8); g.lineTo(x + i * 0.7, y + 8); }
    g.stroke();
  }

  function well(g, x, y, pal) {
    g.fillStyle = 'rgba(0, 0, 0, .25)';
    g.beginPath(); g.arc(x + 3, y + 5, 27, 0, TAU); g.fill();
    g.fillStyle = '#9b958a';
    g.beginPath(); g.arc(x, y, 26, 0, TAU); g.fill();
    g.fillStyle = '#1f3d57';
    g.beginPath(); g.arc(x, y, 16, 0, TAU); g.fill();
    g.fillStyle = 'rgba(160, 210, 255, .35)';
    g.beginPath(); g.ellipse(x - 4, y - 5, 6, 3, -0.5, 0, TAU); g.fill();
    g.fillStyle = pal.wood;
    g.fillRect(x - 30, y - 3, 60, 6);
    g.fillStyle = '#5e3b20';
    g.fillRect(x - 32, y - 6, 6, 12); g.fillRect(x + 26, y - 6, 6, 12);
  }

  /// Хата: солом'яна стріха, білені стіни, синя призьба, вікна світяться — вечір.
  function hut(g, x0, y0, pal) {
    const w = 7 * CELL, h = 3 * CELL;
    g.fillStyle = 'rgba(0, 0, 0, .28)';
    g.fillRect(x0 + 6, y0 + 10, w, h);
    // стіни
    g.fillStyle = pal.wall;
    g.fillRect(x0 + 6, y0 + 40, w - 12, h - 42);
    g.fillStyle = '#3d6fa8';
    g.fillRect(x0 + 6, y0 + h - 12, w - 12, 10);
    // вікна з теплим світлом
    for (const wx of [x0 + 34, x0 + w - 70]) {
      g.fillStyle = '#ffd98a';
      g.fillRect(wx, y0 + 52, 30, 22);
      g.strokeStyle = '#2f5d93';
      g.lineWidth = 3;
      g.strokeRect(wx, y0 + 52, 30, 22);
      g.beginPath(); g.moveTo(wx + 15, y0 + 52); g.lineTo(wx + 15, y0 + 74); g.moveTo(wx, y0 + 63); g.lineTo(wx + 30, y0 + 63); g.stroke();
    }
    // двері — навпроти стежки
    g.fillStyle = '#6b4424';
    g.fillRect(x0 + 4 * CELL + 6, y0 + 50, 22, h - 50);
    g.fillStyle = '#e8c33a';
    g.beginPath(); g.arc(x0 + 4 * CELL + 24, y0 + 72, 1.8, 0, TAU); g.fill();
    // стріха
    g.fillStyle = pal.thatch;
    g.beginPath();
    g.moveTo(x0 - 2, y0 + 46); g.lineTo(x0 + 22, y0 + 2); g.lineTo(x0 + w - 22, y0 + 2); g.lineTo(x0 + w + 2, y0 + 46);
    g.closePath(); g.fill();
    g.strokeStyle = 'rgba(110, 80, 20, .55)';
    g.lineWidth = 1;
    g.beginPath();
    for (let i = x0 + 6; i < x0 + w - 4; i += 5) { g.moveTo(i, y0 + 6); g.lineTo(i + (i - x0 - w / 2) * 0.08, y0 + 45); }
    g.stroke();
    g.fillStyle = 'rgba(0, 0, 0, .15)';
    g.fillRect(x0 - 2, y0 + 42, w + 4, 5);
  }

  /// Поміст для музик (самих музик малюємо щокадру — вони грають у такт).
  function stage(g, x0, y0, pal) {
    const w = 4 * CELL, h = 2 * CELL;
    g.fillStyle = 'rgba(0, 0, 0, .28)';
    g.fillRect(x0 + 4, y0 + 7, w, h);
    g.fillStyle = '#a57443';
    g.fillRect(x0 + 1, y0 + 1, w - 2, h - 2);
    g.strokeStyle = 'rgba(60, 35, 15, .45)';
    g.lineWidth = 1;
    g.beginPath();
    for (let yy = y0 + 9; yy < y0 + h; yy += 9) { g.moveTo(x0 + 2, yy); g.lineTo(x0 + w - 2, yy); }
    g.stroke();
    g.strokeStyle = pal.wood;
    g.lineWidth = 2;
    g.strokeRect(x0 + 1, y0 + 1, w - 2, h - 2);
    // рушник по краю помосту
    g.fillStyle = '#f6f1e4';
    g.fillRect(x0 + 2, y0 + h - 7, w - 4, 5);
    g.fillStyle = '#c0392b';
    for (let i = x0 + 4; i < x0 + w - 4; i += 6) g.fillRect(i, y0 + h - 6, 3, 3);
  }

  function bench(g, b, pal) {
    const [x, y, w, h] = b, X = x * CELL, Y = y * CELL, W = w * CELL, H = h * CELL;
    const vert = h > w;
    g.fillStyle = 'rgba(0, 0, 0, .25)';
    g.fillRect(X + (vert ? 9 : 3), Y + (vert ? 3 : 13), vert ? 16 : W, vert ? H : 14);
    g.fillStyle = pal.wood;
    if (vert) g.fillRect(X + 7, Y + 2, 16, H - 4);
    else g.fillRect(X + 2, Y + 9, W - 4, 14);
    g.strokeStyle = 'rgba(40, 20, 5, .5)';
    g.lineWidth = 1;
    g.beginPath();
    if (vert) { g.moveTo(X + 15, Y + 4); g.lineTo(X + 15, Y + H - 4); } else { g.moveTo(X + 4, Y + 16); g.lineTo(X + W - 4, Y + 16); }
    g.stroke();
  }

  /// Стіл із наїдками: скатерка-рушник, глечик, миска вареників, паляниця.
  function table(g, b, pal) {
    const [x, y, w] = b, X = x * CELL, Y = y * CELL, W = w * CELL;
    g.fillStyle = 'rgba(0, 0, 0, .28)';
    g.fillRect(X + 4, Y + 6, W, CELL);
    g.fillStyle = pal.wood;
    g.fillRect(X + 1, Y + 2, W - 2, CELL - 4);
    g.fillStyle = '#f6f1e4';
    g.fillRect(X + 4, Y + 4, W - 8, CELL - 8);
    g.fillStyle = '#c0392b';
    for (let i = X + 6; i < X + W - 6; i += 6) { g.fillRect(i, Y + 5, 3, 2); g.fillRect(i, Y + CELL - 7, 3, 2); }
    const items = W / CELL;
    for (let i = 0; i < items; i++) {
      const cx = X + i * CELL + 16, cy = Y + 16;
      if (i % 3 === 0) {           // глечик
        g.fillStyle = '#c5763a';
        g.beginPath(); g.ellipse(cx, cy + 1, 6, 7, 0, 0, TAU); g.fill();
        g.fillRect(cx - 2.5, cy - 9, 5, 4);
      } else if (i % 3 === 1) {    // миска вареників
        g.fillStyle = '#d4e0ea';
        g.beginPath(); g.ellipse(cx, cy, 9, 6, 0, 0, TAU); g.fill();
        g.fillStyle = '#f3e3b6';
        for (let k = -1; k <= 1; k++) { g.beginPath(); g.ellipse(cx + k * 4, cy - 1 + (k & 1), 3, 2, 0.3, 0, TAU); g.fill(); }
      } else {                     // паляниця
        g.fillStyle = '#c98b3c';
        g.beginPath(); g.ellipse(cx, cy, 9, 6.5, 0, 0, TAU); g.fill();
        g.strokeStyle = 'rgba(90, 50, 10, .6)';
        g.beginPath(); g.moveTo(cx - 5, cy - 2); g.lineTo(cx + 5, cy - 2); g.moveTo(cx - 5, cy + 2); g.lineTo(cx + 5, cy + 2); g.stroke();
      }
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Музики на помості — грають у такт
  // ---------------------------------------------------------------------------------------------

  function musicians(g, x0, y0, bump, playing, now) {
    const man = (cx, cy, shirt, hat) => {
      g.fillStyle = shirt; g.beginPath(); g.roundRect(cx - 7, cy - 4, 14, 12, 4); g.fill();
      g.fillStyle = SKIN[1]; g.beginPath(); g.arc(cx, cy - 9, 5.5, 0, TAU); g.fill();
      g.fillStyle = hat; g.beginPath(); g.ellipse(cx, cy - 13, 5.5, 3.5, 0, Math.PI, TAU); g.fill();
    };
    const b = playing ? bump : 0.15 * (1 + Math.sin(now / 400));
    // скрипаль: смичок ходить туди-сюди
    const sx = x0 + 34, sy = y0 + 34 - b * 2;
    man(sx, sy, SHIRT, '#2a2a2a');
    g.fillStyle = '#7a3e14';
    g.beginPath(); g.ellipse(sx + 8, sy - 2, 7, 3.5, -0.6, 0, TAU); g.fill();
    const bow = playing ? Math.sin(now / 90) * 6 : 0;
    g.strokeStyle = '#e8d9b0'; g.lineWidth = 1.2;
    g.beginPath(); g.moveTo(sx + 2 + bow, sy + 8); g.lineTo(sx + 16 + bow, sy - 8); g.stroke();
    // цимбаліст: палички вгору-вниз по черзі
    const cx = x0 + 64, cy = y0 + 28;
    man(cx, cy - b * 1.5, '#c0392b', '#3b2a20');
    g.fillStyle = '#8a5a33';
    g.beginPath(); g.moveTo(cx - 16, cy + 8); g.lineTo(cx + 16, cy + 8); g.lineTo(cx + 12, cy + 20); g.lineTo(cx - 12, cy + 20); g.closePath(); g.fill();
    g.strokeStyle = '#e8d9b0'; g.lineWidth = 0.8;
    g.beginPath();
    for (let i = 0; i < 4; i++) { g.moveTo(cx - 13 + i, cy + 11 + i * 2.4); g.lineTo(cx + 13 - i, cy + 11 + i * 2.4); }
    g.stroke();
    const hm = playing ? (Math.sin(now / 70) > 0 ? 1 : -1) : 0;
    g.strokeStyle = '#3b2a20'; g.lineWidth = 1.5;
    g.beginPath(); g.moveTo(cx - 5, cy + 4); g.lineTo(cx - 8, cy + 12 - hm * 3); g.moveTo(cx + 5, cy + 4); g.lineTo(cx + 8, cy + 12 + hm * 3); g.stroke();
    // бубніст: б'є в бубон на кожен пульс
    const bx = x0 + 96, by = y0 + 34 - b * 2;
    man(bx, by, '#2f6db5', '#2a2a2a');
    g.fillStyle = '#e9d8a6';
    g.beginPath(); g.arc(bx + 9, by - 6 - b * 3, 7 + b * 1.5, 0, TAU); g.fill();
    g.strokeStyle = '#7a4f2c'; g.lineWidth = 1.5; g.stroke();
    // ноти злітають на пульс
    if (playing && b > 0.4) {
      g.fillStyle = 'rgba(255, 240, 200, ' + (b * 0.9).toFixed(2) + ')';
      g.font = '12px system-ui, sans-serif';
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.fillText('♪', x0 + 20, y0 + 8 - b * 6);
      g.fillText('♫', x0 + 110, y0 + 8 - b * 6);
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Танцюристи
  // ---------------------------------------------------------------------------------------------

  const HANDS = [
    // [лівої x, y, правої x, y] відносно точки на землі (з урахуванням присідання)
    [-1.5, -12, 1.5, -12],   // плескай: долоні разом перед грудьми
    [-9, -4, 9, -4],         // присядь: низько, руки на колінах — найнижча й найкомпактніша постать
    [-12, -20, 12, -20],     // крутись: руки розкинуті
    [-7, -35, 7, -35],       // руки вгору
  ];

  /// Танцюрист у виді 3/4. (x, y) — точка на землі між ногами. d — куди дивиться, s — стан з кадру, mv — іде.
  /// Без save/restore і без алокацій.
  function dancer(g, x, y, d, s, mv, look, now, id, ink) {
    if (s === 2 || s === 3) return sitting(g, x, y, d, s, look, ink, now);
    const kind = look[0], head = look[1], c1 = CLOTH[look[2]] || CLOTH[0], c2 = CLOTH[look[3]] || CLOTH[1], skinC = SKIN[look[4]] || SKIN[0];
    let fig = -1;
    if (s >= 5 && s <= 12) fig = (s - 5) % 4;
    if (fig === 2) d = (d + Math.floor(now / 85 + id)) & 3;          // крутиться — обличчя біжить по колу
    const side = d === 0 ? 1 : d === 2 ? -1 : 0;
    let bob = 0, step = 0;
    if (mv && fig < 0 && s !== 4) {
      const ph = Math.sin(now * 0.0503 + id * 1.7);
      bob = ph > 0 ? -1 : 0;
      step = ph > 0 ? 1 : -1;
    }
    if (s === 4) x += Math.sin(now / 70 + id) * 2;                   // отетерів — хитається
    const low = fig === 1 ? 5 : 0;
    const flare = fig === 2 ? 1 : 0;

    // ноги
    g.fillStyle = '#3a2618';
    g.beginPath();
    if (fig === 1) {               // присядка: коліна врізнобіч
      g.ellipse(x - 7, y, 3, 2, 0, 0, TAU);
      g.ellipse(x + 7, y, 3, 2, 0, 0, TAU);
    } else if (side) {
      g.ellipse(x + side * 2.5 * step, y + 0.5, 2.6, 2, 0, 0, TAU);
      g.ellipse(x - side * 2.5 * step, y + 0.5, 2.6, 2, 0, 0, TAU);
    } else {
      g.ellipse(x - 3, y + 0.5 - (step > 0 ? 1.5 : 0), 2.3, 2.1, 0, 0, TAU);
      g.ellipse(x + 3, y + 0.5 - (step < 0 ? 1.5 : 0), 2.3, 2.1, 0, 0, TAU);
    }
    g.fill();
    y += bob;
    const yb = y + low;            // «низ тулуба» з урахуванням присідання

    // низ: плахта (дівчина) чи шаровари (парубок)
    g.strokeStyle = ink;
    g.lineWidth = 1;
    if (kind === 0) {
      // присіла — плахта низьким широким віялом по землі
      const top = 6, bot = fig === 1 ? 12 : 8 + flare * 5, y0 = fig === 1 ? y - 6 : yb - 10, y1 = fig === 1 ? y : yb - 1;
      g.fillStyle = c1;
      g.beginPath();
      g.moveTo(x - top, y0); g.lineTo(x + top, y0); g.lineTo(x + bot, y1); g.lineTo(x - bot, y1); g.closePath();
      g.fill(); g.stroke();
      g.fillStyle = 'rgba(0, 0, 0, .25)';
      g.fillRect(x - bot + 1, y1 - 3, bot * 2 - 2, 1.6);
    } else {
      g.fillStyle = c1;
      g.beginPath();
      if (fig === 1) { g.ellipse(x - 5, y - 3, 5, 3.5, 0, 0, TAU); g.ellipse(x + 5, y - 3, 5, 3.5, 0, 0, TAU); }
      else { g.roundRect(x - 6, yb - 10, 5.5, 10, 2.5); g.roundRect(x + 0.5, yb - 10, 5.5, 10, 2.5); }
      g.fill(); g.stroke();
    }
    // сорочка з вишивкою
    g.fillStyle = SHIRT;
    g.beginPath(); g.roundRect(x - 6.5, yb - 19, 13, 10, 4); g.fill(); g.stroke();
    if (kind === 1) {              // пояс
      g.fillStyle = c2;
      g.fillRect(x - 6.5, yb - 10.5, 13, 2.2);
    }
    if (d !== 3) {                 // вишиванка спереду
      g.fillStyle = kind === 0 ? c2 : '#b3202b';
      g.fillRect(x - 1 + side * 2, yb - 18, 2, 5);
    }

    // руки: від плечей до кистей, за позою
    let lx, ly, rx, ry;
    if (fig >= 0) {
      const hnd = HANDS[fig];
      const sq = side && fig === 0 ? side * 5 : 0;            // плескай у профіль — долоні попереду
      lx = x + hnd[0] + sq; ly = y + hnd[1] + (fig === 0 || fig === 2 ? low : 0); rx = x + hnd[2] + sq; ry = y + hnd[3] + (fig === 0 || fig === 2 ? low : 0);
    } else if (s === 4) {
      lx = x - 7; ly = yb - 5; rx = x + 7; ry = yb - 5;
    } else {
      lx = x - 8; ly = yb - 9 + step; rx = x + 8; ry = yb - 9 - step;
    }
    const shy = yb - 16;
    g.lineCap = 'round';
    g.strokeStyle = ink;
    g.lineWidth = 4.2;
    g.beginPath(); g.moveTo(x - 5, shy); g.lineTo(lx, ly); g.moveTo(x + 5, shy); g.lineTo(rx, ry); g.stroke();
    g.strokeStyle = SHIRT;
    g.lineWidth = 2.6;
    g.stroke();
    g.fillStyle = skinC;
    g.beginPath(); g.arc(lx, ly, 2.1, 0, TAU); g.arc(rx, ry, 2.1, 0, TAU); g.fill();

    // голова
    const hx = x + side * 1.5, hy = yb - 24;
    g.fillStyle = skinC;
    g.beginPath(); g.arc(hx, hy, 6, 0, TAU); g.fill();
    g.strokeStyle = ink;
    g.lineWidth = 0.9;
    g.stroke();
    g.fillStyle = ink;
    if (s === 4) {                 // отетерів: очі кружальцями
      g.lineWidth = 0.8;
      g.beginPath(); g.arc(hx - 2.2, hy, 1.3, 0, TAU); g.moveTo(hx + 3.5, hy); g.arc(hx + 2.2, hy, 1.3, 0, TAU); g.stroke();
    } else if (d === 1) {
      g.fillRect(hx - 2.7, hy - 0.2, 1.5, 1.7);
      g.fillRect(hx + 1.2, hy - 0.2, 1.5, 1.7);
    } else if (side) {
      g.fillRect(hx + side * 2.2 - 0.8, hy - 0.4, 1.5, 1.7);
    }
    headwear(g, kind, head, hx, hy, d, side, c2, look[2], id, fig, now);

    // позначки над головою
    if (s >= 9 && s <= 12) mark(g, hx, hy - 14 - (fig === 3 ? 6 : 0), '!', '#e0473c');
    else if (s === 13) mark(g, hx, hy - 14, '?', '#e0473c');
    else if (s === 4) stunStars(g, hx, hy - 9, now);
    if (fig === 0 && s <= 8) sparks(g, (lx + rx) / 2, ly, now, id);
    if (fig === 2) swirl(g, x, yb - 12, now, id);
  }

  function headwear(g, kind, head, hx, hy, d, side, c2, c1i, id, fig, now) {
    if (kind === 0) {
      if (head === 0) {            // вінок і стрічки
        const flutter = fig === 2 ? 7 : 0;
        const back = d === 3 ? 0 : side ? -side : 0;
        g.lineWidth = 1.6;
        g.lineCap = 'round';
        for (let i = 0; i < 3; i++) {
          g.strokeStyle = i === 1 ? c2 : CLOTH[(c1i + i * 3) & 7];
          const sx = hx + back * 5 + (i - 1) * 1.8;
          g.beginPath(); g.moveTo(sx, hy - 2);
          g.quadraticCurveTo(sx + back * 3 + Math.sin(now / 120 + i + id) * (1 + flutter * 0.4), hy + 7, sx + back * (4 + flutter) + (i - 1), hy + 13 - flutter);
          g.stroke();
        }
        const cols = ['#e0473c', '#f2c230', '#f6f1e4', '#e0473c', '#5aa0e0'];
        for (let i = 0; i < 5; i++) {
          const a = Math.PI * (1.08 + i * 0.21);
          g.fillStyle = cols[(i + id) % 5];
          g.beginPath(); g.arc(hx + Math.cos(a) * 6, hy - 1 + Math.sin(a) * 5.5, 2, 0, TAU); g.fill();
        }
      } else if (head === 1) {     // хустка
        g.fillStyle = c2;
        g.beginPath();
        if (d === 3) g.arc(hx, hy, 6.7, 0, TAU);
        else if (side) g.ellipse(hx - side * 1.6, hy - 1.6, 6.3, 6, 0, Math.PI * 0.95, Math.PI * 2.05);
        else g.arc(hx, hy - 0.4, 6.7, Math.PI * 1.02, Math.PI * 1.98);
        g.fill();
        g.fillStyle = 'rgba(255, 255, 255, .5)';
        g.fillRect(hx - 3, hy - 4.5, 1.3, 1.3); g.fillRect(hx + 1.5, hy - 3.2, 1.3, 1.3);
      } else {                     // коса з кісником
        g.fillStyle = HAIR[id & 7];
        g.beginPath();
        if (d === 3) g.arc(hx, hy, 6.2, 0, TAU);
        else if (side) g.ellipse(hx - side * 1.4, hy - 1.8, 6, 5.4, 0, Math.PI * 0.9, Math.PI * 2.1);
        else g.arc(hx, hy - 0.8, 6.2, Math.PI * 1.05, Math.PI * 1.95);
        g.fill();
        const bxo = d === 3 ? 0 : side ? -side * 5 : 5;
        for (let i = 0; i < 4; i++) { g.beginPath(); g.ellipse(hx + bxo, hy + 3 + i * 3, 1.8, 1.6, 0, 0, TAU); g.fill(); }
        g.fillStyle = c2;
        g.fillRect(hx + bxo - 2, hy + 14, 4, 2.4);
      }
    } else if (head === 0) {       // смушева шапка
      g.fillStyle = '#26221f';
      g.beginPath(); g.roundRect(hx - 6, hy - 14, 12, 10, 3); g.fill();
      g.fillStyle = 'rgba(255, 255, 255, .12)';
      for (let i = 0; i < 4; i++) g.fillRect(hx - 4 + i * 2.5, hy - 12 + (i & 1) * 3, 1.2, 1.2);
    } else if (head === 1) {       // бриль
      g.fillStyle = '#e3c77a';
      g.beginPath(); g.ellipse(hx, hy - 3.5, 10, 3.4, 0, 0, TAU); g.fill();
      g.strokeStyle = 'rgba(0, 0, 0, .3)'; g.lineWidth = 0.8; g.stroke();
      g.beginPath(); g.ellipse(hx, hy - 6, 5.2, 4, 0, Math.PI, TAU); g.closePath(); g.fill();
      g.fillStyle = '#b3202b';
      g.fillRect(hx - 5.2, hy - 6.4, 10.4, 1.5);
    } else {                       // чуб
      g.fillStyle = HAIR[(id + 3) & 7];
      g.beginPath();
      if (d === 3) g.arc(hx, hy, 6.2, 0, TAU);
      else if (side) g.ellipse(hx - side * 1.4, hy - 1.8, 6, 5.4, 0, Math.PI * 0.9, Math.PI * 2.1);
      else g.arc(hx, hy - 0.8, 6.2, Math.PI * 1.05, Math.PI * 1.95);
      g.fill();
    }
  }

  /// Сидить на землі: ображений бот (2) — руки схрещені, відвернувся, над головою хмарка; вибулий гравець (3) — похнюпився.
  function sitting(g, x, y, d, s, look, ink, now) {
    const kind = look[0], c1 = CLOTH[look[2]] || CLOTH[0], c2 = CLOTH[look[3]] || CLOTH[1], skinC = SKIN[look[4]] || SKIN[0];
    if (s === 3) g.globalAlpha = 0.8;
    g.strokeStyle = ink;
    g.lineWidth = 1;
    g.fillStyle = kind === 0 ? c1 : c1;
    g.beginPath(); g.ellipse(x, y - 2, 10, 4.5, 0, 0, TAU); g.fill(); g.stroke();
    g.fillStyle = '#3a2618';
    g.beginPath(); g.ellipse(x - 9, y + 1, 2.4, 1.8, 0, 0, TAU); g.ellipse(x + 9, y + 1, 2.4, 1.8, 0, 0, TAU); g.fill();
    g.fillStyle = SHIRT;
    g.beginPath(); g.roundRect(x - 6, y - 13, 12, 10, 4); g.fill(); g.stroke();
    // руки схрещені на грудях
    g.strokeStyle = ink; g.lineWidth = 3.6; g.lineCap = 'round';
    g.beginPath(); g.moveTo(x - 5, y - 9); g.lineTo(x + 5, y - 7); g.moveTo(x + 5, y - 9); g.lineTo(x - 5, y - 7); g.stroke();
    g.strokeStyle = SHIRT; g.lineWidth = 2.2; g.stroke();
    const hy = s === 3 ? y - 16 : y - 18;
    g.fillStyle = skinC;
    g.beginPath(); g.arc(x, hy, 5.8, 0, TAU); g.fill();
    g.strokeStyle = ink; g.lineWidth = 0.9; g.stroke();
    // відвернувся (потилиця) — зачіска на все обличчя
    g.fillStyle = kind === 0 ? c2 : HAIR[look[2] & 7];
    g.beginPath(); g.arc(x, hy - 0.5, 6, Math.PI * (s === 3 ? 1.0 : 0.85), Math.PI * (s === 3 ? 2.0 : 2.15)); g.fill();
    if (s === 2) {
      // сердита позначка
      const ax = x + 8, ay = hy - 8;
      g.strokeStyle = '#e0473c'; g.lineWidth = 1.6;
      g.beginPath();
      g.moveTo(ax - 3, ay - 1); g.quadraticCurveTo(ax - 1, ay - 1, ax - 1, ay - 3);
      g.moveTo(ax + 3, ay - 1); g.quadraticCurveTo(ax + 1, ay - 1, ax + 1, ay - 3);
      g.moveTo(ax - 3, ay + 1); g.quadraticCurveTo(ax - 1, ay + 1, ax - 1, ay + 3);
      g.moveTo(ax + 3, ay + 1); g.quadraticCurveTo(ax + 1, ay + 1, ax + 1, ay + 3);
      g.stroke();
    }
    g.globalAlpha = 1;
  }

  function mark(g, x, y, ch, color) {
    g.fillStyle = 'rgba(255, 255, 255, .95)';
    g.beginPath(); g.arc(x, y, 5.5, 0, TAU); g.fill();
    g.strokeStyle = color; g.lineWidth = 1.5; g.stroke();
    g.fillStyle = color;
    g.font = '800 9px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillText(ch, x, y + 0.5);
  }

  function stunStars(g, x, y, now) {
    g.fillStyle = '#ffe28a';
    g.font = '9px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    for (let s = 0; s < 3; s++) {
      const a = now / 110 + (s * TAU) / 3;
      g.fillText('✦', x + Math.cos(a) * 9, y + Math.sin(a) * 3.5);
    }
  }

  function sparks(g, x, y, now, id) {
    if ((Math.floor(now / 90) + id) & 1) return;
    g.strokeStyle = '#ffe28a';
    g.lineWidth = 1.3;
    g.beginPath();
    g.moveTo(x - 4, y - 4); g.lineTo(x - 7, y - 7);
    g.moveTo(x + 4, y - 4); g.lineTo(x + 7, y - 7);
    g.moveTo(x, y - 5); g.lineTo(x, y - 9);
    g.stroke();
  }

  function swirl(g, x, y, now, id) {
    const a = now / 80 + id;
    g.strokeStyle = 'rgba(255, 255, 255, .55)';
    g.lineWidth = 1.3;
    g.beginPath(); g.ellipse(x, y, 15, 6, 0, a, a + 2); g.stroke();
    g.beginPath(); g.ellipse(x, y, 15, 6, 0, a + Math.PI, a + Math.PI + 2); g.stroke();
  }

  // ---------------------------------------------------------------------------------------------
  // Стан модуля
  // ---------------------------------------------------------------------------------------------

  function state(root, ctx) {
    let st = root._dance;
    if (!st) {
      st = root._dance = {
        root, ctx: null, cv: null, interp: HGames.ui.Interp(), mode: 'full', S: 1,
        view: null, n: 0, looks: [], names: [], map: null, mapKey: '', circle: { x: 480, y: 320, r: 112 }, meId: -1, me: null,
        round: -1, vphase: '', last: null, lastAt: 0, fph: '', m: [-1, 0, 36, 0],
        px: new Float64Array(64), py: new Float64Array(64), pd: new Int8Array(64), ps: new Int8Array(64), pm: new Int8Array(64), order: [],
        stat: null, statKey: '', mini: null, pal: null, palAt: -1e9,
        cam: { x: WW / 2, y: WH / 2 }, box: [0, 0, WW, WH], drag: null, hover: false,
        keys: [], touchDir: -1, dir: -1, localDir: -1, localUntil: 0, localFig: -1, localFigUntil: 0,
        peekUntil: 0, sentAt: 0, dancedBeat: -1, slapAt: -1e9, stunUntil: 0, tip: null, tipUntil: 0, tipKind: '', autoPeek: false,
        lastSeen: null, slaps: [], pip: -1, figSig: '',
        raf: 0, keyup: null, blur: null, ro: null, io: null, onResize: null, visible: true,
        padOn: false, padH: 0, padAt: 0, cssK: 1, lab: [],
        hudEl: null, clockEl: null, newsEl: null, sumEl: null, ctlEl: null, stageEl: null, seatsEl: null,
        audio: null, mute: readMute(),
        perf: { sum: 0, n: 0, max: 0 }, wakeAt: 0,
      };
    }
    st.ctx = ctx;
    ctx._dance = st;
    return st;
  }

  const seatOf = (st, i) => (st.view && st.view.seats || []).find((s) => s.seat === i) || null;
  const nickOfSeat = (st, i) => { const s = seatOf(st, i); return (s && s.nick) || (st.ctx && st.ctx.nickOf(i)) || SEAT_NAMES[i] || '?'; };
  const lookOf = (st, id) => { const l = st.looks, j = id * 5; return [l[j] | 0, l[j + 1] | 0, l[j + 2] | 0, l[j + 3] | 0, l[j + 4] | 0]; };
  const nameOf = (st, id) => (st.names && st.names[id]) || 'танцюрист';
  const alive = (st) => !!(st.me && st.me.alive);
  const phaseOf = (st) => (st.vphase === 'over' ? 'over' : st.fph || st.vphase);
  /// Тик сервера «зараз»: останній кадр плюс скільки минуло (не далі двох тиків уперед).
  const tickNow = (st, now) => (st.last ? st.last.t + Math.min(2, Math.max(0, (now - st.lastAt) / TICK_MS)) : 0);

  function refuse(st, text, toasted) {
    if (!text) return;
    if (!toasted && st.ctx) st.ctx.toast(text, 'err');
    tip(st, text, 'err');
  }

  function tip(st, text, kind) {
    st.tip = text;
    st.tipKind = kind;
    st.tipUntil = performance.now() + TIP_MS;
    wake(st);
  }

  /// «дівчина у вінку з червоними стрічками» / «парубок у смушевій шапці й синьому поясі» — щоб описати в новинах.
  function looksLike(st, id) {
    const [kind, head, c1, c2] = lookOf(st, id);
    if (kind === 0) {
      if (head === 0) return 'дівчина у вінку з ' + CLOTH_PL[c2] + ' стрічками';
      if (head === 1) return 'дівчина в ' + CLOTH_F[c2] + ' хустці';
      return 'дівчина з косою, у ' + CLOTH_F[c1] + ' плахті';
    }
    const what = head === 0 ? 'смушевій шапці' : head === 1 ? 'брилі' : 'чубатий';
    return head === 2 ? 'чубатий парубок у ' + CLOTH_M[c2] + ' поясі' : 'парубок у ' + what + ' й ' + CLOTH_M[c2] + ' поясі';
  }

  // ---------------------------------------------------------------------------------------------
  // Звуки (WebAudio, тихо, лише після першого жесту): відлік, «гоп», ляпас
  // ---------------------------------------------------------------------------------------------

  function unlock(st) {
    if (st.audio || st.mute) return;
    try {
      const A = window.AudioContext || window.webkitAudioContext;
      if (A) st.audio = new A();
    } catch { st.audio = null; }
  }

  function sfx(st, kind) {
    const A = st.audio;
    if (!A || st.mute || document.hidden) return;
    try {
      const t = A.currentTime;
      const tone = (f0, f1, dur, vol, type) => {
        const o = A.createOscillator();
        o.type = type || 'sine';
        o.frequency.setValueAtTime(f0, t);
        if (f1 !== f0) o.frequency.exponentialRampToValueAtTime(f1, t + dur);
        const gn = A.createGain();
        gn.gain.setValueAtTime(vol, t);
        gn.gain.exponentialRampToValueAtTime(0.0001, t + dur);
        o.connect(gn); gn.connect(A.destination);
        o.start(t); o.stop(t + dur + 0.02);
      };
      if (kind === 'pip') tone(660, 660, 0.06, 0.03, 'triangle');
      else if (kind === 'hop') { tone(880, 880, 0.12, 0.04, 'triangle'); tone(1320, 1320, 0.1, 0.025, 'sine'); }
      else if (kind === 'slap') {
        const len = Math.floor(A.sampleRate * 0.06);
        const buf = A.createBuffer(1, len, A.sampleRate);
        const ch = buf.getChannelData(0);
        for (let i = 0; i < len; i++) ch[i] = (Math.random() * 2 - 1) * Math.pow(1 - i / len, 3);
        const src = A.createBufferSource();
        src.buffer = buf;
        const gn = A.createGain();
        gn.gain.value = 0.08;
        src.connect(gn); gn.connect(A.destination);
        src.start(t);
      } else if (kind === 'out') tone(420, 110, 0.2, 0.05, 'triangle');
    } catch { /* без звуку гра повна */ }
  }

  // ---------------------------------------------------------------------------------------------
  // Вид і кадри
  // ---------------------------------------------------------------------------------------------

  function applyView(st, v) {
    if (!v) return;
    const prevMe = st.me;
    st.view = v;
    if (v.map) {
      const key = v.map.join('');
      if (key !== st.mapKey) { st.mapKey = key; st.map = v.map; st.statKey = ''; }
    }
    if (v.circle) st.circle = v.circle;
    st.n = v.n | 0;
    st.looks = v.looks || [];
    st.names = v.names || [];
    st.me = v.me || null;
    st.meId = st.me ? st.me.id : -1;
    if (v.m) st.m = v.m;
    if (v.round !== st.round || (v.phase === 'start' && st.vphase && st.vphase !== 'start')) {
      st.round = v.round;
      st.camRound = -1;
      st.interp.reset();
      st.last = null;
      st.slaps.length = 0;
      st.dancedBeat = -1;
      st.slapAt = -1e9;
      st.stunUntil = 0;
      st.lastSeen = null;
    }
    if (st.me) {
      if (st.me.stun > 0) st.stunUntil = Math.max(st.stunUntil, performance.now() + st.me.stun * TICK_MS);
      if (st.me.danced && st.m[0] >= 0) st.dancedBeat = st.m[1];
      // суд пройшов — підпис над собою: у такт і в колі / у такт, але поза колом / збився
      const key = st.round + ':' + st.me.good + ':' + st.me.bad;
      if (prevMe && st.lastSeen !== null && key !== st.lastSeen && st.me.alive && v.phase === 'go') {
        const need = v.need | 0 || 6;
        if (st.me.last === 'ring') tip(st, '✓ У такт! Коло ' + st.me.streak + '/' + need, 'ok');
        else if (st.me.last === 'ok') tip(st, '✓ У такт — але поза колом', 'ok');
        else if (st.me.last === 'miss') tip(st, '✗ Збився — серія з нуля', 'err');
      }
      st.lastSeen = key;
    }
    if (st.autoPeek && st.me) {
      if (v.phase === 'go' && st.me.alive) { st.peekUntil = performance.now() + F5_PEEK_MS; st.autoPeek = false; }
      else if (v.phase === 'start') st.autoPeek = false;
    }
    if (v.v && v.v.length) {
      const f = { t: v.t | 0, ph: v.phase, left: v.left | 0, v: v.v, m: v.m || st.m, ev: [] };
      const last = st.last;
      if (!last || last.v.length !== f.v.length || f.t > last.t || (f.t < last.t && f.ph === 'start')) push(st, f, false);
    }
    st.vphase = v.phase;
  }

  function push(st, f, fromFrame) {
    const last = st.last;
    if (last && (f.t < last.t || last.v.length !== f.v.length)) st.interp.reset();
    st.interp.push(f);
    st.last = f;
    st.lastAt = performance.now();
    if (f.m) st.m = f.m;
    if (fromFrame) st.frameAt = st.lastAt;
  }

  function onFrame(st, f) {
    if (!f || !f.v) return;
    const now = performance.now();
    push(st, f, true);
    if (f.ev && f.ev.length) events(st, f, now);
    const walk = (f.ph === 'start' || f.ph === 'go') && st.dir >= 0 && st.ctx && st.ctx.mine && st.ctx.playing;
    if (f.ph !== st.fph) {
      if (walk) { st.ctx.input('move', { dir: st.dir }); st.sentAt = now; }
      st.fph = f.ph;
    }
    if (walk && now - st.sentAt > HOLD_MS) { st.ctx.input('move', { dir: st.dir }); st.sentAt = now; }
    if (st.localUntil && st.meId >= 0 && f.v[st.meId * 4 + 2] === st.localDir) st.localUntil = 0;
    if (st.localFigUntil && st.meId >= 0 && f.v[st.meId * 4 + 3] >= 5) st.localFigUntil = 0;
    paintClock(st, f);
  }

  function events(st, f, now) {
    for (const e of f.ev) {
      if (e[0] !== 1) continue;
      const a = e[1], b = e[2], kind = e[3], seat = e[4];
      st.slaps.push({ ax: f.v[a * 4], ay: f.v[a * 4 + 1], bx: f.v[b * 4], by: f.v[b * 4 + 1], at: now, b });
      if (st.slaps.length > 8) st.slaps.shift();
      const name = nameOf(st, b), she = lookOf(st, b)[0] === 0, heShe = lookOf(st, a)[0] === 0;
      const hitter = st.ctx.esc(looksLike(st, a));
      if (kind === 1) {
        news(st, '💥 <b class="dance-s' + seat + '">' + st.ctx.esc(nickOfSeat(st, seat)) + '</b> вибуває з танцю — це ' + (she ? 'була ' : 'був ')
          + st.ctx.esc(name) + '! ' + (a === st.meId ? 'Твій ляпас' : (heShe ? 'Ляснула ' : 'Ляснув ') + hitter));
        sfx(st, 'out');
      } else {
        news(st, '✋ Ляп! ' + st.ctx.esc(name) + (she ? ' ображено сіла' : ' ображено сів') + ' — '
          + (a === st.meId ? 'це був бот, а ти отетерів' : 'а ' + hitter + (heShe ? ' отетеріла' : ' отетерів')));
        if (a === st.meId) st.stunUntil = now + STUN_MS;
      }
      sfx(st, 'slap');
    }
  }

  function news(st, html) {
    const el = st.newsEl;
    if (!el) return;
    const item = document.createElement('div');
    item.className = 'dance-ni';
    item.innerHTML = html;
    el.insertBefore(item, el.firstChild);
    while (el.children.length > 2) el.lastChild.remove();
    setTimeout(() => item.remove(), NEWS_MS);
  }

  // ---------------------------------------------------------------------------------------------
  // Позиції, камера, досяжність ляпаса
  // ---------------------------------------------------------------------------------------------

  function grow(st, n) {
    if (st.px.length >= n) return;
    const m = Math.max(n, st.px.length * 2);
    st.px = new Float64Array(m); st.py = new Float64Array(m);
    st.pd = new Int8Array(m); st.ps = new Int8Array(m); st.pm = new Int8Array(m);
  }

  function positions(st, now) {
    const cur = st.interp.at();
    const b = (cur && cur.b) || st.last;
    if (!b || !b.v) return 0;
    let a = cur && cur.a;
    if (!a || !a.v || a.v.length !== b.v.length) a = b;
    const k = cur ? cur.t : 1;
    const va = a.v, vb = b.v, n = vb.length >> 2;
    grow(st, n);
    const { px, py, pd, ps, pm } = st;
    const over = st.vphase === 'over';
    for (let i = 0, j = 0; i < n; i++, j += 4) {
      const x0 = va[j], y0 = va[j + 1], x1 = vb[j], y1 = vb[j + 1];
      const jump = Math.abs(x1 - x0) > 12 || Math.abs(y1 - y0) > 12;
      px[i] = jump ? x1 : x0 + (x1 - x0) * k;
      py[i] = jump ? y1 : y0 + (y1 - y0) * k;
      pd[i] = vb[j + 2];
      ps[i] = vb[j + 3];
      pm[i] = !over && !jump && (x1 !== x0 || y1 !== y0 || vb[j + 3] === 1) ? 1 : 0;
    }
    const me = st.meId;
    if (me >= 0 && me < n && ps[me] < 2) {
      if (now < st.localUntil) { pd[me] = st.localDir; if (st.dir >= 0) pm[me] = 1; }
      if (now < st.localFigUntil) { ps[me] = 5 + st.localFig; pm[me] = 0; }
    }
    const order = st.order;
    if (order.length !== n) { order.length = 0; for (let i = 0; i < n; i++) order.push(i); }
    for (let i = 1; i < n; i++) {
      const id = order[i], y = py[id];
      let j = i - 1;
      while (j >= 0 && py[order[j]] > y) { order[j + 1] = order[j]; j--; }
      order[j + 1] = id;
    }
    return n;
  }

  /// Той самий вибір, що й на сервері (DanceCore.Reach): найближчий на ногах у ±45° на 36 перед мною.
  function reachTarget(st, n) {
    const me = st.meId;
    if (me < 0 || me >= n) return -1;
    const d = st.pd[me], fx = DX[d] || 0, fy = DY[d] || 0, x = st.px[me], y = st.py[me];
    let best = -1, bestD = Infinity;
    for (let i = 0; i < n; i++) {
      if (i === me || st.ps[i] === 2 || st.ps[i] === 3) continue;
      const dx = st.px[i] - x, dy = st.py[i] - y, d2 = dx * dx + dy * dy;
      if (d2 > SLAP_RANGE * SLAP_RANGE) continue;
      const dot = dx * fx + dy * fy;
      if (dot <= 0 || dot * dot * 1000 < SLAP_CONE * d2) continue;
      if (d2 < bestD) { bestD = d2; best = i; }
    }
    return best;
  }

  function camera(st, n) {
    const box = st.box;
    if (st.mode !== 'port') { box[0] = 0; box[1] = 0; box[2] = WW; box[3] = WH; return box; }
    const me = st.meId;
    const follow = me >= 0 && me < n && !st.drag && st.ctx && st.ctx.mine && (alive(st) || st.vphase === 'start');
    if (follow) { st.cam.x = st.px[me]; st.cam.y = st.py[me]; }
    else if (!st.drag && st.vphase === 'reveal' && st.view && st.view.reveal && st.camRound !== st.round) {
      st.camRound = st.round;
      const r = st.view.reveal, w = (r.winners || [])[0];
      const hit = (r.ids || []).find((p) => p.seat === w) || (r.ids || [])[0];
      if (hit && hit.id >= 0 && hit.id < n) { st.cam.x = st.px[hit.id]; st.cam.y = st.py[hit.id]; }
    }
    box[0] = clamp(st.cam.x - PW / 2, 0, WW - PW);
    box[1] = clamp(st.cam.y - PH / 2, 0, WH - PH);
    box[2] = PW;
    box[3] = PH;
    return box;
  }

  // ---------------------------------------------------------------------------------------------
  // Малювання кадру
  // ---------------------------------------------------------------------------------------------

  function ensureStatic(st, now) {
    if (st.stat && st.statKey && now - st.palAt <= 1000) return true;
    st.pal = palette();
    st.palAt = now;
    if (!st.map) return false;
    const dpr = Math.min(3, window.devicePixelRatio || 1);
    const S = Math.min(2, dpr);
    const key = S + '|' + st.mapKey.length + '|' + st.pal.grass + st.pal.path + st.pal.wood + st.pal.wall + st.pal.ring;
    if (st.stat && st.statKey === key) return true;
    st.S = S;
    st.stat = drawStatic(st.map, st.circle, st.pal, S);
    st.statKey = key;
    const m = document.createElement('canvas');
    m.width = Math.round(96 * dpr);
    m.height = Math.round(64 * dpr);
    const mg = m.getContext('2d');
    mg.imageSmoothingQuality = 'high';
    mg.drawImage(st.stat, 0, 0, m.width, m.height);
    st.mini = m;
    return true;
  }

  /// Музика: пульс (0…1, згасає після кожного удару), виклик і відлік.
  function music(st, now, phase) {
    const m = st.m || [-1, 0, 36, 0];
    const tn = tickNow(st, now);
    const pulse = PULSE[m[3]] || 12;
    const playing = phase === 'go';
    let bump = 0;
    if (playing) {
      const ph = (((tn - m[1]) % pulse) + pulse) % pulse;
      bump = Math.max(0, 1 - ph / (pulse * 0.6));
    }
    const fig = m[0], beat = m[1], lead = m[2];
    const toBeat = beat - tn;            // тиків до такту (після такту — від'ємне)
    return { tn, pulse, bump, playing, fig, beat, lead, toBeat };
  }

  function draw(st) {
    const t0 = performance.now();
    const cv = st.cv;
    if (!cv || !ensureStatic(st, t0)) return;
    const g = cv.ctx, pal = st.pal, now = t0;
    const n = positions(st, now);
    const box = camera(st, n), cx = box[0], cy = box[1], vw = box[2], vh = box[3];
    const bw = cv.el.width, bh = cv.el.height, k = bw / cv.w;
    const S = st.S;

    g.setTransform(1, 0, 0, 1, 0, 0);
    g.imageSmoothingEnabled = true;
    g.drawImage(st.stat, cx * S, cy * S, vw * S, vh * S, 0, 0, bw, bh);
    g.setTransform(k, 0, 0, k, -cx * k, -cy * k);

    const phase = phaseOf(st);
    const playing = !!(st.ctx && st.ctx.playing);
    const mine = !!(st.ctx && st.ctx.mine) && st.meId >= 0 && st.meId < n;
    const mu = music(st, now, phase);
    countdownSound(st, mu, phase);

    ringPulse(st, g, mu);
    const mm = st.map && st.map[1] ? st.map[1].indexOf('M') : -1;
    if (mm >= 0) musicians(g, mm * CELL, CELL, mu.bump, mu.playing, now);
    trails(st, g, pal, phase);

    const peek = mine && (phase === 'start' || (phase === 'go' && now < st.peekUntil));
    if (peek) ring(g, st.px[st.meId], st.py[st.meId], pal, now);

    g.fillStyle = pal.shadow;
    g.beginPath();
    for (let i = 0; i < n; i++) {
      const x = st.px[i], y = st.py[i];
      if (x < cx - 20 || x > cx + vw + 20 || y < cy - 20 || y > cy + vh + 40) continue;
      g.moveTo(x + 11, y + 4);
      g.ellipse(x + 1, y + 4, 10, 4, 0, 0, TAU);
    }
    g.fill();
    const look = [0, 0, 0, 0, 0];
    const L = st.looks;
    for (let o = 0; o < n; o++) {
      const i = st.order[o];
      const x = st.px[i], y = st.py[i];
      if (x < cx - 20 || x > cx + vw + 20 || y < cy - 20 || y > cy + vh + 40) continue;
      const j = i * 5;
      look[0] = L[j] | 0; look[1] = L[j + 1] | 0; look[2] = L[j + 2] | 0; look[3] = L[j + 3] | 0; look[4] = L[j + 4] | 0;
      dancer(g, x, y + 4, st.pd[i], st.ps[i], st.pm[i], look, now, i, pal.ink);
    }
    slapFx(st, g, now);

    if (mine && alive(st) && phase === 'go' && playing && now >= st.stunUntil) {
      const t = reachTarget(st, n);
      if (t >= 0) chevron(g, st.px[t], st.py[t] - 34, pal);
    }
    labels(st, g, pal, n, phase);

    g.setTransform(k, 0, 0, k, 0, 0);
    shade(st, g, pal, cv.w, cv.h, cx, cy, phase, playing, mine, now);
    g.setTransform(k, 0, 0, k, -cx * k, -cy * k);
    if (peek) meArrow(g, st.px[st.meId], st.py[st.meId], pal, true);
    if (mine && now < st.tipUntil && st.tip) tipPlate(g, st, st.px[st.meId], st.py[st.meId], pal);
    g.setTransform(k, 0, 0, k, 0, 0);
    if (phase === 'go') banner(st, g, pal, cv.w, cv.h, mu);
    if (st.mode === 'port') minimap(st, g, pal, cv.w, cx, cy, vw, vh, mine);
    if (playing && st.frameAt && now - st.frameAt > 600 && phase !== 'over') {
      g.font = '600 12px system-ui, sans-serif';
      g.textAlign = 'left';
      g.textBaseline = 'top';
      outlined(g, 'з\'єднання…', 8, 8, pal.text, pal.ink);
    }
    figButtons(st, mu, phase);

    const dt = performance.now() - t0;
    st.perf.sum += dt;
    st.perf.n++;
    if (dt > st.perf.max) st.perf.max = dt;
  }

  /// Тихий «тук» на кожен пульс відліку і «гоп» на такт — лише коли звук увімкнено.
  function countdownSound(st, mu, phase) {
    if (!st.audio || st.mute || phase !== 'go' || mu.fig < 0) { st.pip = -1; return; }
    const k = Math.floor((mu.tn - (mu.beat - mu.lead)) / mu.pulse);      // 0, 1, 2 — відлік, 3 — такт
    if (k === st.pip || k < 0 || k > 3) return;
    st.pip = k;
    sfx(st, k === 3 ? 'hop' : 'pip');
  }

  /// Коло дихає в такт музиці, а на такт виклику — спалахує кольором фігури.
  function ringPulse(st, g, mu) {
    if (!mu.playing) return;
    const c = st.circle;
    const hot = mu.fig >= 0 && mu.toBeat <= 0 && mu.toBeat > -LATE;
    g.strokeStyle = hot ? FIG_COLOR[mu.fig] : 'rgba(255, 226, 138, 1)';
    g.globalAlpha = hot ? 0.9 : 0.12 + mu.bump * 0.35;
    g.lineWidth = hot ? 5 : 2 + mu.bump * 2;
    g.beginPath(); g.arc(c.x, c.y, c.r + (hot ? 2 : mu.bump * 3), 0, TAU); g.stroke();
    g.globalAlpha = 1;
  }

  function fitFont(g, text, maxW, px, weight) {
    g.font = weight + ' ' + px + 'px system-ui, sans-serif';
    const w = g.measureText(text).width;
    if (w > maxW) g.font = weight + ' ' + Math.max(9, Math.floor((px * maxW) / w)) + 'px system-ui, sans-serif';
  }

  function outlined(g, text, x, y, fill, ink) {
    g.lineWidth = 3.5;
    g.lineJoin = 'round';
    g.strokeStyle = ink;
    g.strokeText(text, x, y);
    g.fillStyle = fill;
    g.fillText(text, x, y);
  }

  function ring(g, x, y, pal, now) {
    g.strokeStyle = pal.accent;
    g.lineWidth = 2.5;
    g.setLineDash([5, 4]);
    g.lineDashOffset = -now / 60;
    g.beginPath(); g.ellipse(x, y + 5, 15, 7.5, 0, 0, TAU); g.stroke();
    g.setLineDash([]);
    g.lineDashOffset = 0;
  }

  function meArrow(g, x, y, pal, big) {
    const top = y - (big ? 40 : 34);
    g.fillStyle = pal.accent;
    g.strokeStyle = pal.ink;
    g.lineWidth = 1.5;
    g.beginPath();
    g.moveTo(x - 7, top - 9); g.lineTo(x + 7, top - 9); g.lineTo(x, top); g.closePath();
    g.fill(); g.stroke();
    if (big) {
      g.font = '800 13px system-ui, sans-serif';
      g.textAlign = 'center';
      g.textBaseline = 'bottom';
      outlined(g, 'ти', x, top - 10, pal.accent, pal.ink);
    }
  }

  function tipPlate(g, st, x, y, pal) {
    const px = Math.max(12, 13 / (st.cssK || 1));
    g.font = '700 ' + px + 'px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    const w = g.measureText(st.tip).width + px, h = px + 8;
    const vx = st.box[0], vw = st.box[2];
    const tx = clamp(x, vx + w / 2 + 4, vx + vw - w / 2 - 4), ty = Math.max(st.box[1] + h, y - 58);
    g.fillStyle = 'rgba(14, 18, 12, .88)';
    g.beginPath(); g.roundRect(tx - w / 2, ty - h / 2, w, h, h / 2); g.fill();
    g.strokeStyle = st.tipKind === 'ok' ? pal.ok : pal.danger;
    g.lineWidth = 1.5;
    g.stroke();
    g.fillStyle = pal.text;
    g.fillText(st.tip, tx, ty + 0.5);
  }

  function trails(st, g, pal, phase) {
    const v = st.view, r = v && v.reveal;
    if (!r || !r.trails || (phase !== 'reveal' && phase !== 'over')) return;
    g.lineJoin = 'round';
    g.lineCap = 'round';
    for (const t of r.trails) {
      const p = t.pts || [];
      if (p.length < 4) continue;
      const col = pal.seats[t.seat] || pal.text;
      g.globalAlpha = 0.85;
      g.strokeStyle = 'rgba(12, 16, 10, .55)';
      g.lineWidth = 5;
      g.beginPath();
      g.moveTo(p[0], p[1]);
      for (let i = 2; i < p.length; i += 2) g.lineTo(p[i], p[i + 1]);
      g.stroke();
      g.strokeStyle = col;
      g.lineWidth = 2.5;
      g.setLineDash([6, 5]);
      g.stroke();
      g.setLineDash([]);
      g.fillStyle = col;
      g.beginPath(); g.arc(p[0], p[1], 3.5, 0, TAU); g.fill();
      g.globalAlpha = 1;
    }
  }

  function chevron(g, x, y, pal) {
    g.fillStyle = pal.danger;
    g.strokeStyle = pal.ink;
    g.lineWidth = 1.5;
    g.beginPath();
    g.moveTo(x - 5, y - 5); g.lineTo(x + 5, y - 5); g.lineTo(x, y + 1); g.closePath();
    g.fill(); g.stroke();
  }

  /// Ляпас: дуга-змах від того, хто вдарив, і «ЛЯП!» над тим, кого вдарили.
  function slapFx(st, g, now) {
    const list = st.slaps;
    for (let i = list.length - 1; i >= 0; i--) if (now - list[i].at > SLAP_FX_MS) list.splice(i, 1);
    for (const s of list) {
      const p = (now - s.at) / SLAP_FX_MS;
      g.globalAlpha = 1 - p;
      g.strokeStyle = '#fff';
      g.lineWidth = 2;
      g.beginPath();
      const mx = (s.ax + s.bx) / 2, my = (s.ay + s.by) / 2 - 22;
      g.moveTo(s.ax, s.ay - 16); g.quadraticCurveTo(mx, my, s.bx, s.by - 16); g.stroke();
      g.font = '900 ' + Math.round(13 + p * 6) + 'px system-ui, sans-serif';
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      outlined(g, 'ЛЯП!', s.bx, s.by - 40 - p * 10, '#ffe28a', '#5a1a10');
      g.globalAlpha = 1;
    }
  }

  /// Ніки над гравцями: над вибулими — завжди, над усіма — на розкритті.
  function labels(st, g, pal, n, phase) {
    const v = st.view;
    if (!v) return;
    const open = v.reveal && (phase === 'reveal' || phase === 'over' || v.phase === 'reveal' || v.phase === 'over');
    const rows = open ? v.reveal.ids || [] : v.dead || [];
    if (!rows.length) return;
    const over = phase === 'over' || v.phase === 'over';
    const win = !open ? [] : over ? (v.result && v.result.winners) || [] : v.reveal.winners || [];
    const markS = over ? '🏆 ' : v.reveal && v.reveal.why === 'ribbon' ? '🎀 ' : '⭐ ';
    g.lineWidth = 2.5;
    for (let r = 0; r < rows.length; r++) {
      const id = rows[r].id, seat = rows[r].seat;
      if (id < 0 || id >= n) continue;
      g.strokeStyle = pal.seats[seat] || pal.text;
      g.beginPath(); g.ellipse(st.px[id], st.py[id] + 5, 14, 7, 0, 0, TAU); g.stroke();
    }
    const px = st.mode === 'port' ? 15 : 13, h = px + 5;
    g.font = '700 ' + px + 'px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    const lab = st.lab, pool = st.labPool || (st.labPool = []);
    lab.length = 0;
    for (let r = 0; r < rows.length; r++) {
      const id = rows[r].id, seat = rows[r].seat;
      if (id < 0 || id >= n) continue;
      const a = pool[lab.length] || (pool[lab.length] = { seat: 0, text: '', x: 0, y: 0, w: 0 });
      a.seat = seat;
      a.text = (win.includes(seat) ? markS : '') + nickOfSeat(st, seat);
      a.x = st.px[id];
      a.y = st.py[id] - (st.ps[id] === 2 || st.ps[id] === 3 ? 30 : 44);
      a.w = g.measureText(a.text).width + 12;
      lab.push(a);
    }
    lab.sort((a, b) => b.y - a.y);
    for (let i = 1; i < lab.length; i++) {
      const a = lab[i];
      for (let moved = true, guard = 0; moved && guard < 8; guard++) {
        moved = false;
        for (let j = 0; j < i; j++) {
          const b = lab[j];
          if (Math.abs(a.x - b.x) * 2 < a.w + b.w && Math.abs(a.y - b.y) < h + 2) { a.y = b.y - h - 2; moved = true; }
        }
      }
    }
    for (const a of lab) {
      g.fillStyle = 'rgba(14, 18, 12, .84)';
      g.beginPath(); g.roundRect(a.x - a.w / 2, a.y - h / 2, a.w, h, h / 2); g.fill();
      g.fillStyle = pal.seats[a.seat] || pal.text;
      g.fillText(a.text, a.x, a.y + 0.5);
    }
  }

  /// Виклик музик: плашка з фігурою, три пульси відліку і «ГОП!» на такт. Бачать усі однаково.
  function banner(st, g, pal, w, h, mu) {
    if (mu.fig < 0) return;
    const f = FIGS[mu.fig], col = FIG_COLOR[mu.fig];
    const port = st.mode === 'port';
    const passed = mu.lead - mu.toBeat;                    // тиків від виклику
    const pips = clamp(Math.floor(passed / mu.pulse), 0, 3);
    const hop = mu.toBeat <= 0;
    // вікно такту закрилось — плашка згасає до кінця виклику (спізнених іще приймають)
    const fade = clamp(1 - Math.max(0, -mu.toBeat - LATE) / (LATE_MAX - LATE), 0.25, 1);
    const minPx = 15 / (st.cssK || 1);
    const big = Math.round(Math.max(minPx * 1.25, port ? 26 : 30));
    // уся мапа — «бульбашка» з вуст музик праворуч від помосту (сам поміст і коло лишаються на виду);
    // в'юпорт телефона — плашка вгорі ліворуч від мінімапи
    const mm = st.map && st.map[1] ? st.map[1].indexOf('M') : 13;
    const sx = (mm + 4) * CELL;
    const bw = port ? Math.min(w - 118, 300) : Math.min(w - sx - 44, 380), bh = big * 2.1;
    const bx = port ? 6 : sx + 14, by = port ? 6 : 30;
    const pop = hop && mu.toBeat > -6 && !reduced() ? 1 + (6 + mu.toBeat) * 0.012 : 1;
    g.globalAlpha = fade;
    g.fillStyle = 'rgba(14, 18, 12, .84)';
    g.beginPath(); g.roundRect(bx, by, bw, bh, 12);
    if (!port) { g.moveTo(bx + 1, by + bh * 0.35); g.lineTo(bx - 13, by + bh * 0.5); g.lineTo(bx + 1, by + bh * 0.62); }
    g.fill();
    g.strokeStyle = col;
    g.lineWidth = hop ? 4 : 2;
    g.beginPath(); g.roundRect(bx, by, bw, bh, 12); g.stroke();
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    const tx = bx + bw / 2;
    fitFont(g, f.emoji + ' ' + f.call, bw - 24, Math.round(big * pop), 900);
    outlined(g, f.emoji + ' ' + f.call, tx, by + bh * 0.36, hop ? col : pal.text, pal.ink);
    // відлік: три кружальця й «ГОП»
    const r = Math.max(4, big * 0.18), gap = r * 3.2, py = by + bh * 0.78;
    for (let i = 0; i < 3; i++) {
      g.fillStyle = i < pips ? col : 'rgba(255, 255, 255, .22)';
      g.beginPath(); g.arc(tx - gap * 2 + i * gap, py, r, 0, TAU); g.fill();
    }
    g.font = '900 ' + Math.round(r * 2.6) + 'px system-ui, sans-serif';
    g.fillStyle = hop ? col : 'rgba(255, 255, 255, .3)';
    g.fillText('ГОП', tx + gap * 1.4, py + 0.5);
    g.globalAlpha = 1;
    // на такт — рамка кольору фігури по краю мапи
    if (hop && mu.toBeat > -LATE && !reduced()) {
      g.strokeStyle = col;
      g.globalAlpha = 0.6 * (1 + mu.toBeat / LATE);
      g.lineWidth = 8;
      g.strokeRect(4, 4, w - 8, h - 8);
      g.globalAlpha = 1;
    }
  }

  function shade(st, g, pal, w, h, cx, cy, phase, playing, mine, now) {
    const v = st.view;
    if (!v) return;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    const room = st.ctx && st.ctx.room;
    if (!playing && room && room.status === 'lobby') {
      g.fillStyle = 'rgba(12, 16, 8, .45)';
      g.fillRect(0, 0, w, h);
      if (st.mode === 'port') {
        fitFont(g, 'щойно господар натисне «Почати»', w * 0.9, 22, 700);
        outlined(g, 'Музики заграють,', w / 2, h / 2 - 14, pal.text, pal.ink);
        outlined(g, 'щойно господар натисне «Почати»', w / 2, h / 2 + 14, pal.text, pal.ink);
      } else {
        const msg = 'Музики заграють, щойно господар натисне «Почати»';
        fitFont(g, msg, w * 0.9, Math.round(w / 34), 700);
        outlined(g, msg, w / 2, h / 2, pal.text, pal.ink);
      }
      return;
    }
    const minPx = 14 / (st.cssK || 1);
    if (phase === 'start') {
      if (mine) spotlight(st, g, w, h, cx, cy, 0.42);
      const left = st.last ? st.last.left : v.left;
      g.font = '800 ' + Math.round(h / 5) + 'px system-ui, sans-serif';
      outlined(g, String(Math.max(1, Math.ceil((left * TICK_MS) / 1000))), w / 2, h / 2, pal.text, pal.ink);
      const sub = mine ? 'Роздивись: ти — під стрілкою' : 'Музики настроюються…';
      const px = Math.round(Math.max(h / 26, minPx));
      fitFont(g, sub, w * 0.92, px, 700);
      outlined(g, sub, w / 2, h / 2 + h / 7, pal.text, pal.ink);
      if (mine) {
        const need = v.need | 0 || 6;
        const how = st.mode === 'port' ? 'Танцюй фігури в такт · ' + need + ' поспіль у колі — стрічка 🎀'
          : 'Танцюй фігури в такт, як усі · ' + need + ' поспіль у колі — стрічка 🎀 · ✋ — ляпас';
        fitFont(g, how, w * 0.92, Math.round(px * 0.92), 600);
        outlined(g, how, w / 2, h / 2 + h / 7 + px * 1.5, pal.accent, pal.ink);
      }
      return;
    }
    if (phase === 'go' && mine && now < st.peekUntil) spotlight(st, g, w, h, cx, cy, 0.46 * Math.min(1, (st.peekUntil - now) / 300));
    if (phase === 'reveal' || phase === 'over' || (!playing && v.phase === 'over')) {
      g.fillStyle = 'rgba(12, 16, 8, .28)';
      g.fillRect(0, 0, w, h);
      const title = phase === 'reveal' ? revealTitle(st, st.mode === 'port') : overTitle(st);
      const port = st.mode === 'port', left = port ? w - 108 : w, mid = left / 2;
      fitFont(g, title, left - (port ? 24 : 40), Math.round(Math.max(minPx, h / 22)), 800);
      const tw = g.measureText(title).width + 28;
      const th = Math.max(24, h / 13), ty = port ? 8 : Math.round(h * 0.2 - th / 2);
      g.fillStyle = 'rgba(14, 18, 12, .78)';
      g.beginPath(); g.roundRect(mid - tw / 2, ty, tw, th, 10); g.fill();
      g.fillStyle = pal.text;
      g.fillText(title, mid, ty + th / 2);
    }
  }

  function spotlight(st, g, w, h, cx, cy, a) {
    const x = st.px[st.meId] - cx, y = st.py[st.meId] - cy - 12;
    g.fillStyle = 'rgba(12, 16, 8, ' + a.toFixed(3) + ')';
    g.beginPath();
    g.rect(0, 0, w, h);
    g.arc(x, y, 60, 0, TAU, true);
    g.fill('evenodd');
  }

  function revealTitle(st, short) {
    const v = st.view, r = v && v.reveal;
    if (!r) return '';
    const head = short ? '' : 'Раунд ' + v.round + ' з ' + v.of + ' · ';
    const who = (r.winners || []).map((s) => nickOfSeat(st, s)).join(' і ');
    switch (r.why) {
      case 'ribbon': return head + '🎀 Стрічка — ' + who + ': ' + ((v.need | 0) || 6) + ' фігур поспіль у колі!';
      case 'last': return head + '✋ На ногах лише ' + who;
      case 'time': return head + '🎻 Музики стомились — найкраще в колі: ' + who;
      default: return head + '🎻 Музики стомились — ніхто не взяв раунду';
    }
  }

  function overTitle(st) {
    const v = st.view, res = v && v.result;
    if (!res) return 'Вечорниці скінчились';
    if (res.why === 'left') return res.winners.length ? '🚪 Суперники розійшлись — перемога: ' + res.winners.map((s) => nickOfSeat(st, s)).join(', ') : '🚪 Усі розійшлись';
    if (!res.winners.length) return '🤝 Нічия';
    return '🏆 Перемога: ' + res.winners.map((s) => nickOfSeat(st, s) + ' ' + (res.totals[s] | 0)).join(', ');
  }

  function minimap(st, g, pal, w, cx, cy, vw, vh, mine) {
    if (!st.mini) return;
    const mw = 96, mh = 64, x0 = w - mw - 6, y0 = 6;
    g.globalAlpha = 0.92;
    g.drawImage(st.mini, x0, y0, mw, mh);
    g.globalAlpha = 1;
    g.strokeStyle = 'rgba(0, 0, 0, .6)';
    g.lineWidth = 1;
    g.strokeRect(x0 - 0.5, y0 - 0.5, mw + 1, mh + 1);
    const sx = mw / WW, sy = mh / WH;
    g.strokeStyle = '#fff';
    g.lineWidth = 1.2;
    g.strokeRect(x0 + cx * sx, y0 + cy * sy, vw * sx, vh * sy);
    if (mine) {
      g.fillStyle = pal.accent;
      g.beginPath(); g.arc(x0 + st.px[st.meId] * sx, y0 + st.py[st.meId] * sy, 2.2, 0, TAU); g.fill();
    }
  }

  // ---------------------------------------------------------------------------------------------
  // HUD, підсумок, годинник
  // ---------------------------------------------------------------------------------------------

  function paintClock(st, f) {
    const el = st.clockEl;
    if (!el) return;
    const ph = st.vphase === 'over' ? 'over' : f ? f.ph : st.vphase;
    const left = f ? f.left : (st.view && st.view.left) || 0;
    const text = ph === 'go' ? '⏱ ' + clock(left) : ph === 'start' ? '⏱ 2:00' : '⏱ —';
    if (el.textContent !== text) el.textContent = text;
    const hot = ph === 'go' && left * TICK_MS <= 10000;
    const chip = el.parentNode;
    if (chip.classList.contains('hot') !== hot) chip.classList.toggle('hot', hot);
    const tempo = f && f.m ? f.m[3] | 0 : 0;
    const te = st.tempoEl;
    if (te) {
      const tt = '🎻' + '♪'.repeat(tempo + 1);
      if (te.textContent !== tt) { te.textContent = tt; te.title = ['Музики грають помірно', 'Музики розігрались', 'Музики шкварять щосили!'][tempo] || ''; }
      if (te.classList.contains('fast') !== (tempo === 2)) te.classList.toggle('fast', tempo === 2);
    }
  }

  function hud(st) {
    const ctx = st.ctx, v = st.view, el = st.hudEl;
    if (!el || !v) return;
    const me = v.me, need = (v.need | 0) || 6;
    let html = (v.of | 0) > 1 ? '<span class="dance-chip">Раунд ' + Math.max(1, v.round | 0) + '/' + (v.of | 0) + '</span>' : '';
    if (me) {
      let dots = '';
      for (let i = 0; i < need; i++) dots += '<i' + (i < me.streak ? ' class="on"' : '') + '></i>';
      html += '<span class="dance-chip dance-streak" title="Фігур поспіль у колі: ' + need + ' — і стрічка твоя">🎀 <span>' + dots + '</span></span>';
    }
    const seats = v.seats || [];
    const active = seats.filter((s) => !s.out);
    if (v.phase !== 'lobby') html += '<span class="dance-chip">на ногах ' + active.filter((s) => s.alive).length + '/' + active.length + '</span>';
    html += '<button type="button" class="dance-mute" data-pad-skip title="Звук">' + (st.mute ? '🔇' : '🔊') + '</button>';
    if (el.dataset.sig !== html) {
      el.dataset.sig = html;
      el.querySelector('.dance-chips').innerHTML = html;
    }
    // Низьке вікно (Дек, ноут) і йде раунд: ніки й так у шапці картки — фішка лише кольором і рахунком (нік у підказці),
    // а мапа отримує цілий рядок висоти. На розкритті й у кінці — знову з ніками.
    const compact = st.mode !== 'port' && window.innerHeight < 900 && (v.phase === 'start' || v.phase === 'go');
    let row = '';
    for (const s of seats) {
      row += '<span class="dance-seat dance-s' + s.seat + (s.alive ? '' : ' dead') + (s.out ? ' out' : '') + (s.seat === ctx.seat ? ' me' : '') + '"'
        + (compact ? ' title="' + ctx.esc(s.nick) + '"' : '') + '>'
        + '<i></i>' + (compact ? '' : ctx.esc(s.nick) + ' ') + '<b>' + (s.total | 0) + '</b>'
        + (s.good != null ? ' <small>✓' + (s.good | 0) + ' ✗' + (s.bad | 0) + '</small>' : '') + '</span>';
    }
    const se = st.seatsEl;
    if (se && se.dataset.sig !== row) {
      se.dataset.sig = row;
      se.innerHTML = row;
    }
  }

  function placeSeats(root, st) {
    const se = st.seatsEl;
    if (!se) return;
    if (st.mode === 'port') {
      if (se.parentNode !== root || se.nextSibling) root.appendChild(se);
    } else if (se.parentNode !== st.hudEl) st.hudEl.appendChild(se);
  }

  /// Підсумок раунду — фішки за очками партії: разом, що дав раунд, скільки фігур вціляв і скільки схибив.
  function summary(st) {
    const el = st.sumEl, v = st.view;
    if (!el || !v) return;
    let html = '';
    const r = v.reveal;
    const open = !!(r && (v.phase === 'reveal' || v.phase === 'over'));
    if (open) {
      const over = v.phase === 'over';
      const champ = over ? (v.result && v.result.winners) || [] : r.winners || [];
      const total = (s) => { const x = seatOf(st, s); return x ? x.total | 0 : 0; };
      const rows = (r.rows || []).slice().sort((a, b) => total(b.seat) - total(a.seat) || b.pts - a.pts || a.seat - b.seat);
      html = rows.map((x) => '<span class="dance-sc dance-s' + x.seat + (champ.includes(x.seat) ? ' win' : '') + '">'
        + (champ.includes(x.seat) ? (over ? '🏆 ' : r.why === 'ribbon' ? '🎀 ' : '⭐ ') : '') + '<b>' + st.ctx.esc(nickOfSeat(st, x.seat)) + '</b> '
        + '<em>' + total(x.seat) + '</em> <small title="за раунд: вціляв фігур, схибив, у колі; вибив гравців">+' + x.pts
        + ' · ✓' + x.good + ' ✗' + x.bad + ' · ⭕' + x.circle + (x.kills ? ' · ✋' + x.kills : '') + '</small></span>').join('');
    }
    if (el.dataset.sig !== html) {
      el.dataset.sig = html;
      el.innerHTML = html;
      el.hidden = !html;
    }
    if (st.stageEl) st.stageEl.classList.toggle('dance-open', open);
  }

  function placeSum(root, st) {
    const el = st.sumEl;
    if (!el) return;
    if (st.mode === 'port') { if (el.parentNode !== root || el.previousSibling !== st.stageEl) st.stageEl.after(el); }
    else if (el.parentNode !== st.stageEl) st.stageEl.appendChild(el);
  }

  function placeCtl(root, st) {
    const el = st.ctlEl;
    if (!el) return;
    const after = st.sumEl && st.sumEl.parentNode === root ? st.sumEl : st.stageEl;
    if (el.previousSibling !== after) after.after(el);
  }

  // ---------------------------------------------------------------------------------------------
  // Керування
  // ---------------------------------------------------------------------------------------------

  function want(st) {
    const d = st.touchDir >= 0 ? st.touchDir : st.keys.length ? st.keys[st.keys.length - 1] : -1;
    if (d === st.dir) return;
    st.dir = d;
    if (d >= 0) { st.localDir = d; st.localUntil = performance.now() + LOCAL_MS; }
    const ctx = st.ctx;
    if (ctx && ctx.mine && ctx.playing) { ctx.input('move', { dir: d }); st.sentAt = performance.now(); }
  }

  /// Фігура: відразу показуємо свою позу (косметика), а сервер судить за своїм тиком.
  function figure(st, f) {
    const ctx = st.ctx;
    if (!ctx || !ctx.mine || !ctx.playing) return;
    const now = performance.now();
    const me = st.me;
    const phase = phaseOf(st);
    if (me && !me.alive) { refuse(st, 'Тебе вже вивели з танцю — дивись, хто кого'); return; }
    if (phase === 'start') { refuse(st, 'Музики ще настроюються'); return; }
    if (phase !== 'go') return;
    if (now < st.stunUntil) { refuse(st, 'Ти ще отетерілий — не до танців'); return; }
    const m = st.m;
    if (m[0] >= 0 && st.dancedBeat === m[1]) { refuse(st, 'Ти вже відтанцював цю фігуру'); return; }
    const beat = m[1];
    st.localFig = f;
    st.localFigUntil = now + LOCAL_MS;
    ctx.act('fig', { f }).then((r) => {
      if (r && r.ok) st.dancedBeat = beat;
      else { st.localFigUntil = 0; if (r) refuse(st, r.message, true); }
    });
  }

  function slap(st, id) {
    const ctx = st.ctx;
    if (!ctx || !ctx.mine || !ctx.playing) return;
    const now = performance.now();
    const me = st.me;
    if (me && !me.alive) { refuse(st, 'Тебе вже вивели з танцю — дивись, хто кого'); return; }
    if (now < st.stunUntil) { refuse(st, 'Ти ще отетерілий — не до танців'); return; }
    if (now - st.slapAt < SLAP_COOL_MS) { refuse(st, 'Рука ще не відійшла'); return; }
    if (id == null) {
      const n = positions(st, now);
      const t = reachTarget(st, n);
      id = t >= 0 ? t : null;
    }
    ctx.act('slap', id == null ? {} : { id }).then((r) => {
      if (r && r.ok) st.slapAt = performance.now();
      else if (r) refuse(st, r.message, true);
    });
  }

  function peek(st) { st.peekUntil = performance.now() + PEEK_MS; wake(st); }

  function toWorld(st, e) {
    const r = st.cv.el.getBoundingClientRect();
    const [cx, cy, vw, vh] = st.box;
    return [cx + ((e.clientX - r.left) / r.width) * vw, cy + ((e.clientY - r.top) / r.height) * vh, r];
  }

  function pickDancer(st, x, y, reach) {
    const n = st.last ? st.last.v.length >> 2 : 0;
    let best = -1, bestD = reach * reach;
    for (let i = 0; i < n; i++) {
      if (i === st.meId || st.ps[i] === 2 || st.ps[i] === 3) continue;
      const dx = st.px[i] - x, dy = st.py[i] - 12 - y, d2 = dx * dx + dy * dy;
      if (d2 <= bestD) { bestD = d2; best = i; }
    }
    return best;
  }

  function canAct(st) {
    const ctx = st.ctx;
    return !!(ctx && ctx.mine && ctx.playing && alive(st) && phaseOf(st) === 'go');
  }

  /// Чи дотягнуся до цього танцюриста (як сервер: до 52 одиниць у будь-який бік).
  function reachable(st, id) {
    const me = st.meId;
    if (me < 0 || id < 0) return false;
    const dx = st.px[id] - st.px[me], dy = st.py[id] - st.py[me];
    return dx * dx + dy * dy <= SLAP_MAX * SLAP_MAX;
  }

  function wireCanvas(st) {
    const el = st.cv.el;
    el.addEventListener('pointerdown', (e) => {
      wake(st);
      unlock(st);
      st.down = { id: e.pointerId, x: e.clientX, y: e.clientY, moved: false, cam: { x: st.cam.x, y: st.cam.y } };
      if (st.mode === 'port') {
        const [, , r] = toWorld(st, e);
        const lx = ((e.clientX - r.left) / r.width) * PW, ly = ((e.clientY - r.top) / r.height) * PH;
        if (lx >= PW - 102 && ly <= 72 && !(st.ctx && st.ctx.mine && alive(st))) {
          st.cam.x = ((lx - (PW - 102)) / 96) * WW;
          st.cam.y = ((ly - 6) / 64) * WH;
          st.down.moved = true;
        }
      }
    });
    el.addEventListener('pointermove', (e) => {
      wake(st);
      const d = st.down;
      if (d && d.id === e.pointerId) {
        const dx = e.clientX - d.x, dy = e.clientY - d.y;
        if (Math.abs(dx) + Math.abs(dy) > 6) d.moved = true;
        if (st.mode === 'port' && d.moved && !(st.ctx && st.ctx.mine && alive(st))) {
          const r = el.getBoundingClientRect();
          st.drag = true;
          st.cam.x = clamp(d.cam.x - (dx / r.width) * PW, PW / 2, WW - PW / 2);
          st.cam.y = clamp(d.cam.y - (dy / r.height) * PH, PH / 2, WH - PH / 2);
        }
        return;
      }
      if (e.pointerType !== 'mouse' || !canAct(st)) { if (st.hover) { st.hover = false; el.style.cursor = ''; } return; }
      const [x, y] = toWorld(st, e);
      const id = pickDancer(st, x, y, 16);
      const on = id >= 0 && reachable(st, id);
      if (on !== st.hover) { st.hover = on; el.style.cursor = on ? 'pointer' : ''; }
    });
    const up = (e) => {
      const d = st.down;
      if (!d || d.id !== e.pointerId) return;
      st.down = null;
      st.drag = false;
      if (d.moved || e.type === 'pointercancel' || !canAct(st)) return;
      const [x, y] = toWorld(st, e);
      const id = pickDancer(st, x, y, e.pointerType === 'mouse' ? 16 : 22);
      // ляпас лише тому, до кого дотягнешся: випадковий тиц по далекому нікого не б'є
      if (id >= 0 && reachable(st, id)) slap(st, id);
    };
    el.addEventListener('pointerup', up);
    el.addEventListener('pointercancel', up);
  }

  /// Панель керування під мапою: фігури (і мишкою, і пальцем), ляпас, а на тачі — ще й хрестовина з «де я?».
  function controls(root, st) {
    const ctx = st.ctx;
    let el = st.ctlEl;
    if (!ctx.mine) {
      if (el) { el.remove(); st.ctlEl = null; }
      return;
    }
    if (el) return;
    el = document.createElement('div');
    el.className = 'dance-ctl';
    const label = { 0: '→', 1: '↓', 2: '←', 3: '↑' };
    el.innerHTML = '<div class="dance-dirs" role="group" aria-label="хрестовина: тримай і веди пальцем">'
      + [3, 2, 0, 1].map((d) => '<span class="dance-arr" data-dir="' + d + '">' + label[d] + '</span>').join('')
      + '<button type="button" class="dance-peek" data-act="peek" data-pad-skip aria-label="де я">👁</button></div>'
      + '<div class="dance-figs">'
      + FIGS.map((f, i) => '<button type="button" class="dance-f dance-f' + i + '" data-f="' + i + '" data-pad-skip title="' + f.name + '">'
        + '<b>' + f.emoji + '</b><span>' + f.name + '</span><kbd></kbd></button>').join('')
      + '<button type="button" class="dance-slap" data-act="slap" data-pad-skip title="Ляпас тому, хто перед тобою"><b>✋</b><span>Ляпас</span><kbd></kbd></button>'
      + '</div>';
    const dirs = el.querySelector('.dance-dirs');
    const arrows = dirs.querySelectorAll('.dance-arr');
    const light = (d) => arrows.forEach((a) => a.classList.toggle('on', +a.dataset.dir === d));
    const aim = (e) => {
      const r = dirs.getBoundingClientRect();
      const dx = e.clientX - (r.left + r.width / 2), dy = e.clientY - (r.top + r.height / 2);
      if (Math.abs(dx) < r.width * 0.12 && Math.abs(dy) < r.height * 0.12) return st.touchDir;
      return Math.abs(dx) > Math.abs(dy) ? (dx > 0 ? 0 : 2) : (dy > 0 ? 1 : 3);
    };
    // pointerdown, а не click: фігуру судять за тиком — клік лунає лише на відпускання, а це вже запізно
    el.addEventListener('pointerdown', (e) => {
      const b = e.target.closest('button');
      unlock(st);
      if (b) {
        e.preventDefault();
        if (b.dataset.f != null) return figure(st, +b.dataset.f);
        if (b.dataset.act === 'slap') return slap(st, null);
        if (b.dataset.act === 'peek') return peek(st);
        return;
      }
      if (!e.target.closest('.dance-dirs')) return;
      e.preventDefault();
      try { dirs.setPointerCapture(e.pointerId); } catch { /* старий браузер */ }
      st.touchPid = e.pointerId;
      st.touchDir = aim(e);
      light(st.touchDir);
      want(st);
    });
    // клавіатурою по кнопці (Enter/пробіл на фокусі) — теж фігура
    el.addEventListener('click', (e) => {
      if (e.detail !== 0) return;
      const b = e.target.closest('button');
      if (!b) return;
      if (b.dataset.f != null) figure(st, +b.dataset.f);
      else if (b.dataset.act === 'slap') slap(st, null);
      else if (b.dataset.act === 'peek') peek(st);
    });
    dirs.addEventListener('pointermove', (e) => {
      if (st.touchPid !== e.pointerId) return;
      const d = aim(e);
      if (d === st.touchDir) return;
      st.touchDir = d;
      light(d);
      want(st);
    });
    const release = (e) => {
      if (st.touchPid !== e.pointerId) return;
      st.touchPid = null;
      st.touchDir = -1;
      light(-1);
      want(st);
    };
    dirs.addEventListener('pointerup', release);
    dirs.addEventListener('pointercancel', release);
    dirs.addEventListener('lostpointercapture', release);
    st.ctlEl = el;
    st.figSig = '';
    keyLabels(st);
    placeCtl(root, st);
  }

  /// Підписи клавіш на кнопках: клавіатура — «1·J», на Деку — кнопки пада.
  function keyLabels(st) {
    const el = st.ctlEl;
    if (!el) return;
    const pad = padOn();
    if (el.dataset.pad === String(pad)) return;
    el.dataset.pad = String(pad);
    // на Деку кнопки під мапою дублюють смужку підказок пада — ховаємо їх, мапа стає більшою
    el.classList.toggle('dance-padmode', pad && !HGames.ui.coarse());
    if (st.root) fit(st.root, st);
    el.querySelectorAll('.dance-f').forEach((b) => {
      const f = FIGS[+b.dataset.f];
      b.querySelector('kbd').textContent = pad ? f.pad : f.key + '·' + f.alt;
    });
    el.querySelector('.dance-slap kbd').textContent = pad ? 'RT' : 'пробіл';
  }

  /// Кнопка фігури, яку зараз кличуть, — світиться; решта стоять тихо. Клас міняємо лише на зміну.
  function figButtons(st, mu, phase) {
    const el = st.ctlEl;
    if (!el) return;
    const on = phase === 'go' && mu.fig >= 0 && mu.toBeat > -LATE_MAX ? mu.fig : -1;
    const hop = on >= 0 && mu.toBeat <= 0 && mu.toBeat > -LATE;
    const sig = on + (hop ? 'h' : '');
    if (sig === st.figSig) return;
    st.figSig = sig;
    el.querySelectorAll('.dance-f').forEach((b) => {
      const i = +b.dataset.f;
      b.classList.toggle('on', i === on);
      b.classList.toggle('hop', i === on && hop);
    });
  }

  function padStrip(st) {
    const hints = document.querySelector('.padhints');
    const on = !!(hints && !hints.hidden);
    if (on === st.padOn) return false;
    st.padOn = on;
    st.padH = on ? hints.offsetHeight + 8 : 0;
    return true;
  }

  function sizeStage(st, mode) {
    const el = st.stageEl;
    if (!el || !el.isConnected) return;
    if (mode === 'port') { if (el.style.maxWidth) el.style.maxWidth = ''; return; }
    const r = el.getBoundingClientRect();
    const card = el.closest('.gtable');
    const below = Math.max(48, card ? card.getBoundingClientRect().bottom - r.bottom : 150) + 22;
    const h = window.innerHeight - (r.top + (window.scrollY || 0)) - below - st.padH;
    const w = clamp(Math.floor(h * 1.5), 480, 960);
    const cur = parseFloat(el.style.maxWidth) || 0;
    if (Math.abs(cur - w) >= 3) el.style.maxWidth = w + 'px';
  }

  function fit(root, st) {
    wake(st);
    padStrip(st);
    const cw = root.clientWidth;
    let mode = st.mode;
    if (cw) mode = cw < 600 ? 'port' : cw > 640 ? 'full' : st.mode;
    sizeStage(st, mode);
    if (!st.cv || mode !== st.mode) {
      st.mode = mode;
      st.cv = HGames.ui.canvas(st.stageEl, mode === 'port' ? { w: PW, h: PH, cls: 'dance-board dance-port' } : { w: WW, h: WH, cls: 'dance-board' });
      st.stageEl.classList.toggle('dance-portmode', mode === 'port');
      placeSum(root, st);
      placeCtl(root, st);
      placeSeats(root, st);
    } else st.cv.resize();
    const css = st.cv.el.clientWidth;
    if (css) st.cssK = css / (mode === 'port' ? PW : WW);
  }

  /// Телефон: шапка столу з вісьмома місцями штовхала мапу вниз, і кнопки опинялись під нижнім меню — видно було
  /// або мапу, або кнопки. Раз на партію (room.startedAt), коли вона пішла, прокручуємо сторінку так, щоб рядок стану
  /// гри став під шапку сайту: тоді мапа й кнопки вміщаються разом. Якщо й так усе видно — не чіпаємо.
  function fitPhone(st) {
    const ctx = st.ctx, padEl = st.ctlEl;
    if (!ctx || !ctx.mine || !ctx.playing || !ctx.room || !st.hudEl || !padEl || !HGames.ui.coarse()) return;
    const key = ctx.room.startedAt || '';
    if (st.fitFor === key) return;
    const a = st.hudEl.getBoundingClientRect(), b = padEl.getBoundingClientRect();
    if (!a.height || !b.height) return;              // картку чи кнопки зараз не видно — спробуємо на наступному виді
    st.fitFor = key;
    const head = document.querySelector('header');
    const top = (head ? head.getBoundingClientRect().bottom : 0) + 4;
    const tabs = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--tabs-h')) || 0;
    // кнопки мають стати над нижнім меню й над плаваючою кнопкою балачки столу («💬 Стіл»)
    const fab = document.querySelector('.tchat.drawer:not(.open) .tc-head');
    const fr = fab && fab.getBoundingClientRect();
    const limit = (fr && fr.height ? Math.min(fr.top, innerHeight - tabs) : innerHeight - tabs) - 6;
    const lo = b.bottom - limit, hi = a.top - top;   // на скільки прокрутити: не менше lo, не більше hi
    const dy = lo <= hi ? Math.min(Math.max(0, lo), hi) : lo;   // не влазить усе — кнопки важливіші за рядок стану
    if (Math.abs(dy) < 2) return;
    const calm = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    window.scrollBy({ top: dy, behavior: calm ? 'auto' : 'smooth' });
  }

  /// rAF живе, лише поки є що малювати: іде партія (кадри 25 Гц і інтерполяція між ними) або ще доживають анімації
  /// після останньої події (усе коротше за IDLE_MS). Лобі й дограний стіл — статичні: цикл засинає (раніше малював ту
  /// саму картинку 60 разів на секунду). Мапи не видно (інша вкладка сайту, прокрутили геть) — теж спить. Будять вид,
  /// кадр, розкладка, мишка чи палець на мапі, «де я?», відмова з підписом і поява мапи на екрані.
  function wake(st) { st.wakeAt = performance.now(); spin(st); }

  function spin(st) {
    if (st.raf) return;
    const loop = (now) => {
      if (!st.cv || !st.cv.el.isConnected) { st.raf = 0; return; }
      if (now - st.padAt > 1000) { st.padAt = now; if (padStrip(st)) fit(st.root, st); keyLabels(st); }
      const live = !!(st.ctx && st.ctx.playing) && phaseOf(st) !== 'over';
      if (!st.visible || (!live && now - st.wakeAt > IDLE_MS)) { st.raf = 0; return; }
      if (!document.hidden) draw(st);
      st.raf = requestAnimationFrame(loop);
    };
    st.raf = requestAnimationFrame(loop);
  }

  HGames.register({
    id: 'dance',
    added: '2026-09-27',
    icon: ICON,
    seatNames: SEAT_NAMES,
    seatClass: ['x', 'o', 'c', 'd', 'dance-b', 'dance-p', 'dance-v', 'dance-r'],
    // Дек: стік і хрестовина — іти; Ⓧ (ліва) — плескай, Ⓐ (нижня) — присядь, RB — крутись, LB — руки вгору (обидві
    // «верхні»), RT — ляпас (курок — навмисний натиск), LT — де я. Ⓑ (вихід) і Ⓨ (підказки пада) не займаємо.
    pad: {
      dirs: true,
      a: 'Digit2',
      x: 'Digit1',
      on(btn, ctx) {
        const st = ctx && ctx._dance;
        if (!st) return false;
        if (btn === 'rb') { figure(st, 2); return true; }
        if (btn === 'lb') { figure(st, 3); return true; }
        if (btn === 'rt') { slap(st, null); return true; }
        if (btn === 'lt') { peek(st); return true; }
        return false;
      },
      hint: '{dpad} іти · {x} 👏 · {a} 🧎 · {rb} 🌀 · {lb} 🙌 · {rt} ляпас · {lt} де я?',
    },
    added: '2026-09-27',
    news: {
      v: '2026-09-27',
      title: 'Нова гра: Вечорниці',
      items: [
        '💃 Вечорниці на подвір\'ї: музики кличуть фігури, а ти — один із танцюристів. Ніхто не знає, хто живий',
        '👏 На такт станцюй фігуру, як усі: 1–4 або J K L I (на Деку Ⓧ Ⓐ RB LB). Хто схибив — над тим знак питання, і всі це бачать',
        '🎀 Шість фігур поспіль у колі посеред подвір\'я — і стрічка твоя: раунд виграно',
        '✋ Або вибий суперників ляпасом (пробіл, RT). Ляснув бота — сам отетерів, і всі бачать, хто це',
        '🎻 Під кінець раунду музики пришвидшуються — тримай такт!',
      ],
    },

    mount(root, ctx) {
      const st = state(root, ctx);
      root.classList.add('dance-body');
      st.hudEl = document.createElement('div');
      st.hudEl.className = 'dance-hud';
      st.hudEl.innerHTML = '<span class="dance-chip dance-clock"><span class="dance-time">⏱ —</span><span class="dance-tempo">🎻♪</span></span><span class="dance-chips"></span>';
      st.clockEl = st.hudEl.querySelector('.dance-time');
      st.tempoEl = st.hudEl.querySelector('.dance-tempo');
      st.seatsEl = document.createElement('div');
      st.seatsEl.className = 'dance-seats';
      st.hudEl.appendChild(st.seatsEl);
      st.hudEl.addEventListener('click', (e) => {
        if (!e.target.closest('.dance-mute')) return;
        st.mute = !st.mute;
        try { localStorage.setItem('danceMute', st.mute ? '1' : ''); } catch { /* приватне вікно */ }
        if (!st.mute) unlock(st);
        hud(st);
      });
      st.newsEl = document.createElement('div');
      st.newsEl.className = 'dance-news';
      st.newsEl.setAttribute('aria-live', 'polite');
      st.sumEl = document.createElement('div');
      st.sumEl.className = 'dance-sum';
      st.sumEl.hidden = true;
      st.stageEl = document.createElement('div');
      st.stageEl.className = 'dance-stage';
      st.stageEl.appendChild(st.newsEl);
      root.append(st.hudEl, st.stageEl, st.sumEl);
      st.cv = HGames.ui.canvas(st.stageEl, { w: WW, h: WH, cls: 'dance-board' });
      wireCanvas(st);
      if (window.ResizeObserver) {
        st.ro = new ResizeObserver(() => fit(root, st));
        st.ro.observe(root);
      }
      st.onResize = () => fit(root, st);
      window.addEventListener('resize', st.onResize);
      if (window.IntersectionObserver) {
        st.io = new IntersectionObserver((es) => { for (const e of es) st.visible = e.isIntersecting; if (st.visible) wake(st); });
        st.io.observe(st.cv.el);
      }
      st.autoPeek = true;
      st.keyup = (e) => {
        const d = dirOf(e);
        if (d === undefined) return;
        const i = st.keys.indexOf(d);
        if (i >= 0) { st.keys.splice(i, 1); want(st); }
      };
      st.blur = () => {
        st.keys.length = 0;
        st.touchDir = -1;
        want(st);
      };
      document.addEventListener('keyup', st.keyup);
      window.addEventListener('blur', st.blur);
      if (ctx.mine && ctx.playing) ctx.input('move', { dir: -1 });
      wake(st);
    },

    update(root, ctx) {
      const st = state(root, ctx);
      applyView(st, ctx.view);
      hud(st);            // спершу фішки: від їхньої висоти залежить, скільки місця лишається мапі
      fit(root, st);
      controls(root, st);
      placeSum(root, st);
      placeCtl(root, st);
      placeSeats(root, st);
      if (st.ctlEl) {
        const ph = st.vphase, off = !(alive(st) && (ph === 'start' || ph === 'go'));
        if (st.ctlEl.classList.contains('dance-off') !== off) st.ctlEl.classList.toggle('dance-off', off);
        if (off && st.touchDir >= 0) { st.touchDir = -1; st.touchPid = null; want(st); }
      }
      hud(st);
      summary(st);
      paintClock(st, st.last && st.last.ph ? st.last : null);
      fitPhone(st);
      wake(st);
    },

    frame(root, ctx, f) {
      const st = state(root, ctx);
      onFrame(st, f);
      wake(st);
    },

    onKey(e, ctx) {
      const st = ctx._dance;
      if (!st || !ctx.mine || !ctx.playing) return false;
      unlock(st);
      const d = dirOf(e);
      if (d !== undefined) {
        if (!st.keys.includes(d)) st.keys.push(d);
        else if (st.keys[st.keys.length - 1] !== d) { st.keys.splice(st.keys.indexOf(d), 1); st.keys.push(d); }
        want(st);
        return true;
      }
      const f = figOf(e);
      if (f !== undefined) { if (!e.repeat) figure(st, f); return true; }
      if (isSlap(e)) { if (!e.repeat) slap(st, null); return true; }
      if (isPeek(e)) { peek(st); return true; }
      return false;
    },

    status(ctx) {
      const st = ctx._dance;
      const f = ctx.frame || ctx.view;
      if (!ctx.playing || !f) return '';
      const ph = f.ph || f.phase;
      if (ph === 'start') return ctx.mine ? 'Роздивись: стрілка показує тебе' : 'Музики настроюються…';
      if (ph === 'reveal') {
        const v = st && st.view, s = Math.max(1, Math.ceil(((f.left | 0) * TICK_MS) / 1000));
        return v && v.round >= v.of ? 'Підсумок вечорниць за ' + s + ' с' : 'Наступний раунд за ' + s + ' с';
      }
      if (ph !== 'go') return '';
      if (!ctx.mine) return HGames.ui.coarse() ? 'Вгадуй разом із гравцями, хто з танцюристів живий · тягни мапу пальцем' : 'Вгадуй разом із гравцями, хто з танцюристів живий';
      if (st && st.me && !st.me.alive) return 'Тебе вивели з танцю — дивись, хто кого';
      if (padOn()) return 'Фігуру — на «ГОП», як усі · 6 поспіль у колі — стрічка 🎀 · кнопки — у смужці внизу';
      return HGames.ui.coarse()
        ? 'Хрестовина — іти · кнопки фігур — у такт · ✋ ляпас · 👁 де я?'
        : 'Стрілки/WASD — іти · 1–4 або J K L I — фігура в такт · пробіл — ляпас · Q — де я?';
    },

    unmount(root) {
      const st = root._dance;
      if (!st) return;
      cancelAnimationFrame(st.raf);
      st.raf = 0;
      if (st.keyup) document.removeEventListener('keyup', st.keyup);
      if (st.blur) window.removeEventListener('blur', st.blur);
      if (st.onResize) window.removeEventListener('resize', st.onResize);
      if (st.ro) st.ro.disconnect();
      if (st.io) st.io.disconnect();
      if (st.audio) { try { st.audio.close(); } catch { /* уже закритий */ } }
      root._dance = null;
    },
  });
})();
