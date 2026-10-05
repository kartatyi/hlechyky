// Брід (brid) — клієнт. Річка з каменями, що ховаються під водою: безпечну стежку не видно, ступив не туди —
// шубовсть і назад на берег. Правила й числа — на сервері (Impl/Brid.cs, BridCore.cs); тут лише малюнок і ввід.
// Вид і кадр — docs/games/specs/brid.md §4: координати — цілі клітинки, ряд −1 — наш берег (унизу), rows — той (угорі).
//
// Малюємо одним канвасом: статичне тло (вода, береги, камені під водою) — раз у буфер, поверх щокадру — течія, сухі
// камені під гравцями, мокрі сліди, затоплені камені з хвилями, гравці зі стрибком-дугою, бризки. Цикл rAF живе,
// поки картку видно й іде гра (або догоряють бризки); у лобі й після партії — лише перемальовка на зміну.
// Модуль не дивиться нікуди поза ctx і свій root — так його вбудовує й «Глечикова вечірка» (HGames.embed).
(() => {
  'use strict';

  const S = 60;                 // логічний розмір клітинки
  const MX = 0.35;              // поля ліворуч/праворуч від каменів, у клітинках
  const BANK = 0.85;            // висота кожного берега, у клітинках (на телефоні кожен піксель висоти — каменям)
  const SH = 50;                // логічна висота клітинки: камені трохи приплюснуті — річка нижча, камінь ширший
  const SEAT_VARS = [['--br-s0', '#5aa9ff'], ['--br-s1', '#e0875c'], ['--br-s2', '#7bd389'], ['--br-s3', '#f4c542'],
    ['--br-s4', '#b48cf2'], ['--br-s5', '#6fd6c2'], ['--br-s6', '#f08cb8'], ['--br-s7', '#b7c2bd']];
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M0 4.2c2-1.3 3.4-1.3 5.4 0s3.3 1.3 5.3 0 3.3-1.3 5.3 0V16H0Z" fill="var(--br-water, #2a6a88)"/>'
    + '<ellipse cx="4" cy="13.2" rx="2.5" ry="1.5" fill="var(--br-dry, #d6ccb9)"/>'
    + '<ellipse cx="9" cy="10" rx="2.2" ry="1.3" fill="var(--br-dry, #d6ccb9)" opacity=".8"/>'
    + '<ellipse cx="12.6" cy="6.8" rx="1.8" ry="1.1" fill="var(--br-dry, #d6ccb9)" opacity=".45"/>'
    + '<circle cx="9" cy="8" r="1.7" fill="var(--clay)"/></svg>';
  const DIRS = [[1, 0], [0, -1], [-1, 0], [0, 1]];   // 0 → вправо, 1 ↓ назад, 2 ← вліво, 3 ↑ вперед (як ui.dpad)
  const KEYS = { ArrowRight: 0, ArrowDown: 1, ArrowLeft: 2, ArrowUp: 3, KeyD: 0, KeyS: 1, KeyA: 2, KeyW: 3 };
  const REPEAT_MS = 140;        // затиснута стрілка: досилати крок, поки летимо (сервер тримає один у черзі)
  const ORD = ['', '1-й', '2-й', '3-й', '4-й', '5-й', '6-й', '7-й', '8-й'];
  const MEDAL = ['', '🥇', '🥈', '🥉'];

  const live = new Set();
  const padOn = () => !!(window.HPad && HPad.pads > 0);
  const reduced = () => !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
  const coarse = () => !!(HGames.ui.coarse && HGames.ui.coarse());
  const clamp = (v, a, b) => (v < a ? a : v > b ? b : v);
  const hash = (x, y, k) => { const s = Math.sin(x * 127.1 + y * 311.7 + k * 74.7) * 43758.5453; return s - Math.floor(s); };
  const nameAt = (ctx, s) => {
    const n = ctx.nameOf ? ctx.nameOf(s) : ctx.nickOf && ctx.nickOf(s);
    const v = ctx.view;
    const m = n || (ctx.seatName ? ctx.seatName(s) : 'гравець ' + (s + 1));
    // у лобі бот ще не сів — каркас дає назву місця («рудий»); кажемо, що там бот
    return v && v.bot && v.bot.includes(s) && !/🤖/.test(m) ? '🤖 бот ' + m : m;
  };
  const short = (n, k) => (n.length > k ? n.slice(0, k - 1) + '…' : n);
  /// «🤖 бот рудий» → «🤖 рудий»: у тісному чипі й над головою інакше всі боти однакові «🤖 бот…».
  const botless = (n) => String(n).replace(/^🤖\s*бот\s+/, '🤖 ');

  // ---- звук: тихий, лише після першого дотику до сторінки, вимикається 🔊 у рядку гравців ----
  const Snd = {
    on: (() => { try { return localStorage.getItem('brid.sound') !== '0'; } catch { return true; } })(),
    ac: null,
    ensure() {
      if (!this.on) return null;
      const ua = navigator.userActivation;
      if (!this.ac) {
        const AC = window.AudioContext || window.webkitAudioContext;
        if (!AC || (ua && !ua.hasBeenActive)) return null;
        try { this.ac = new AC(); } catch { return null; }
      }
      if (this.ac.state === 'suspended') this.ac.resume().catch(() => {});
      return this.ac;
    },
    tone(freq, ms, vol, type, to) {
      const c = this.ensure();
      if (!c) return;
      const t = c.currentTime, o = c.createOscillator(), g = c.createGain();
      o.type = type || 'sine';
      o.frequency.setValueAtTime(freq, t);
      if (to) o.frequency.exponentialRampToValueAtTime(to, t + ms / 1000);
      g.gain.setValueAtTime(vol, t);
      g.gain.exponentialRampToValueAtTime(0.0001, t + ms / 1000);
      o.connect(g).connect(c.destination);
      o.start(t);
      o.stop(t + ms / 1000 + 0.02);
    },
    splash(vol) {
      const c = this.ensure();
      if (!c) return;
      const n = Math.floor(c.sampleRate * 0.35), b = c.createBuffer(1, n, c.sampleRate), d = b.getChannelData(0);
      for (let i = 0; i < n; i++) d[i] = (Math.random() * 2 - 1) * Math.pow(1 - i / n, 2.2);
      const s = c.createBufferSource(), f = c.createBiquadFilter(), g = c.createGain();
      s.buffer = b;
      f.type = 'lowpass';
      f.frequency.setValueAtTime(1800, c.currentTime);
      f.frequency.exponentialRampToValueAtTime(300, c.currentTime + 0.3);
      g.gain.value = vol;
      s.connect(f).connect(g).connect(c.destination);
      s.start();
      this.tone(520, 120, vol * 0.5, 'sine', 180);
    },
    hop() { this.tone(330, 70, 0.035, 'triangle', 520); },
    home(mine) { this.tone(mine ? 660 : 520, 110, mine ? 0.07 : 0.03, 'triangle'); if (mine) setTimeout(() => this.tone(990, 160, 0.06, 'triangle'), 110); },
    tick(hi) { this.tone(hi ? 880 : 440, 90, 0.04, 'square'); },
  };

  // ---- клавіші: затиснута стрілка веде без пауз; keyup — на весь документ (раз на модуль, не на картку) ----
  const held = { d: -1, since: 0 };
  const HOLD_MS = 260;          // повтор — лише коли стрілку справді тримають, а не коротко тиснули (подвійний крок = у воду)
  document.addEventListener('keyup', (e) => { if (KEYS[e.code] === held.d) held.d = -1; });
  window.addEventListener('blur', () => { held.d = -1; });

  // ---- стан картки ----
  function state(root, ctx) {
    let st = root._brid;
    if (!st) {
      st = root._brid = {
        root, ctx, cv: null, cols: 5, rows: 9, f: null, fAt: 0, view: null,
        anim: [], prev: [], look: [], sk: new Set(), splashAt: new Map(),
        parts: [], rings: [], pops: [], shake: 0, phAt: 0, raf: 0, lastDraw: 0, dirty: true,
        bg: null, bgKey: '', pal: null, palAt: 0, hover: null, down: null, lastSend: 0, secs: -1,
        revealAt: 0, hudSig: '', inited: false,
      };
      live.add(st);
    }
    st.ctx = ctx;
    return st;
  }
  const mySeat = (st) => (st.ctx && st.ctx.mine && st.ctx.seat != null ? st.ctx.seat : -1);

  function palette(st) {
    const now = performance.now();
    if (st.pal && now - st.palAt < 3000) return st.pal;
    const c = (n, d) => HGames.ui.css(n, d);
    st.pal = {
      seats: SEAT_VARS.map(([n, d]) => c(n, d)),
      water: c('--br-water', '#2a6a88'), deep: c('--br-deep', '#173f56'), stone: c('--br-stone', '#4f6670'),
      dry: c('--br-dry', '#d6ccb9'), dry2: c('--br-dry2', '#9c917e'), sand: c('--br-sand', '#d9c18a'),
      grass: c('--br-grass', '#5e8c45'), grass2: c('--br-grass2', '#3f6b31'), path: c('--br-path', '#f4c542'),
      dead: c('--br-dead', '#d2775a'), wet: c('--br-wet', '#2d2a25'), text: c('--text', '#eee'),
      font: c('--font', 'system-ui, sans-serif'),
    };
    st.palAt = now;
    return st.pal;
  }

  // ---- геометрія: центр клітинки в логічних пікселях ----
  const cx = (x) => (MX + x + 0.5) * S;
  const cy = (st, y) => (y >= st.rows ? BANK / 2 : y < 0 ? (BANK + st.rows + BANK / 2) : (BANK + (st.rows - 1 - y) + 0.5)) * SH;
  const jit = (x, y) => [(hash(x, y, 1) - 0.5) * 0.1 * S, (hash(x, y, 2) - 0.5) * 0.08 * S];

  /// Камінь — неправильний овал за сідом клітинки, щоб брід не виглядав шахівницею.
  function blob(g, x, y, r, seed) {
    const n = 9, pts = [];
    for (let i = 0; i < n; i++) {
      const a = (i / n) * Math.PI * 2;
      const k = 0.84 + 0.16 * hash(seed, i, 3);
      pts.push([x + Math.cos(a) * r * k * 1.08, y + Math.sin(a) * r * k * 0.86]);
    }
    g.beginPath();
    for (let i = 0; i <= n; i++) {
      const p = pts[i % n], q = pts[(i + 1) % n];
      const mx = (p[0] + q[0]) / 2, my = (p[1] + q[1]) / 2;
      if (i === 0) g.moveTo(mx, my); else g.quadraticCurveTo(p[0], p[1], mx, my);
    }
    g.closePath();
  }

  // ---- тло: вода, береги, камені під водою — у буфер, раз на розмір і тему ----
  function background(st, pal) {
    const c = st.cv;
    const dpr = Math.min(3, window.devicePixelRatio || 1);
    const key = [st.cols, st.rows, dpr, pal.water, pal.sand, pal.stone].join('|');
    if (st.bg && st.bgKey === key) return st.bg;
    const bg = document.createElement('canvas');
    bg.width = Math.round(c.w * dpr);
    bg.height = Math.round(c.h * dpr);
    const g = bg.getContext('2d');
    g.scale(dpr, dpr);
    const W = c.w, top = BANK * SH, bot = (BANK + st.rows) * SH;
    // вода: посередині глибше
    const wg = g.createLinearGradient(0, top, 0, bot);
    wg.addColorStop(0, pal.water); wg.addColorStop(0.5, pal.deep); wg.addColorStop(1, pal.water);
    g.fillStyle = wg;
    g.fillRect(0, top - 6, W, bot - top + 12);
    // береги: трава з піщаною кромкою до води
    const bank = (y0, y1, edgeY, down) => {
      g.fillStyle = pal.grass;
      g.fillRect(0, y0, W, y1 - y0);
      g.fillStyle = pal.sand;
      g.beginPath();
      g.moveTo(0, edgeY);
      for (let x = 0; x <= W; x += 12) g.lineTo(x, edgeY + (down ? -1 : 1) * (9 + 4 * Math.sin(x / 23 + y0)));
      g.lineTo(W, edgeY); g.closePath();
      g.fill();
      // смужка піску вздовж води
      g.fillRect(0, down ? edgeY - 1 : edgeY - 1, W, 2);
      // травинки й камінці на березі
      for (let i = 0; i < W / 9; i++) {
        const x = hash(i, y0, 5) * W, y = y0 + 4 + hash(i, y0, 6) * (y1 - y0 - 8);
        if (Math.abs(y - edgeY) < 16) continue;
        g.strokeStyle = pal.grass2;
        g.lineWidth = 1.4;
        g.beginPath(); g.moveTo(x, y + 4); g.lineTo(x - 2 + hash(i, 1, 7) * 4, y - 4); g.stroke();
      }
    };
    bank(0, top, top, true);
    bank(bot, c.h, bot, false);
    // очерет у кутках
    g.strokeStyle = pal.grass2;
    g.lineWidth = 2;
    for (const [x0, y0, dir] of [[6, top + 4, 1], [W - 6, top + 4, -1], [6, bot - 4, 1], [W - 6, bot - 4, -1]]) {
      for (let k = 0; k < 4; k++) {
        g.beginPath();
        g.moveTo(x0 + dir * k * 4, y0 + (y0 > c.h / 2 ? 0 : 0));
        g.quadraticCurveTo(x0 + dir * (k * 4 + 3), y0 - 16 - k * 3, x0 + dir * (k * 5 + 6), y0 - 26 - k * 4);
        g.stroke();
      }
    }
    // камені під водою — однакові: який з них безпечний, не видно
    for (let y = 0; y < st.rows; y++) {
      for (let x = 0; x < st.cols; x++) {
        const [jx, jy] = jit(x, y);
        const px = cx(x) + jx, py = cy(st, y) + jy;
        g.globalAlpha = 0.55;
        g.fillStyle = pal.stone;
        blob(g, px, py + 2, S * 0.36, x * 31 + y * 7);
        g.fill();
        g.globalAlpha = 0.18;
        g.fillStyle = '#fff';
        blob(g, px - 3, py - 4, S * 0.2, x * 31 + y * 7 + 1);
        g.fill();
        g.globalAlpha = 1;
      }
    }
    st.bg = bg;
    st.bgKey = key;
    return bg;
  }

  // ---- кадри: що змінилось — бризки, вихід на берег, стрибки ----
  function newRound(st) {
    st.sk = new Set();
    st.anim = [];
    st.prev = [];
    st.splashAt.clear();
    st.rings.length = 0;
    st.revealAt = 0;
  }

  function take(st, f, fresh) {
    const now = performance.now();
    const v = st.ctx.view || {};
    const cols = v.cols || st.cols, rows = v.rows || st.rows;
    const old = st.f;
    if (cols !== st.cols || rows !== st.rows) { st.cols = cols; st.rows = rows; newRound(st); sizeCanvas(st); }
    else if (old && ((f.ph === 0 && old.ph !== 0) || (old.ph >= 2 && f.ph <= 1) || f.t < old.t - 40)) newRound(st);
    const effects = st.inited && fresh;
    if (!old || old.ph !== f.ph) {
      st.phAt = now;
      if (f.ph === 2 || f.ph === 3) st.revealAt = now;
    }
    // відлік — тихий цок щосекунди
    if (f.ph === 0) {
      const s = Math.ceil(((f.left || 0) * tickMs(st)) / 1000);
      if (s !== st.secs && effects && st.ctx.mine) Snd.tick(false);
      st.secs = s;
    } else if (f.ph === 1 && old && old.ph === 0 && effects && st.ctx.mine) Snd.tick(true);
    // нові затоплені камені — бризки (кадр міг загубитись у пачці, тож дивимось на стан, а не лише на ev)
    const sk = new Set();
    for (const c of f.sk || []) {
      const k = c[0] + ':' + c[1];
      sk.add(k);
      if (!st.sk.has(k) && effects) splash(st, c[0], c[1], whoAt(f, c[0], c[1]));
    }
    st.sk = sk;
    const me = mySeat(st);
    const jt = (v.jumpTicks || 6) * tickMs(st);
    const p = f.p || [];
    for (let s = 0; s < p.length; s++) {
      const q = p[s], pr = st.prev[s];
      if (!q) { st.anim[s] = null; st.prev[s] = null; continue; }
      // стрибок: від тиків, що лишились, — початок у нашому часі; той самий стрибок не перезапускаємо
      if (q[4] > 0) {
        const key = q[2] + ',' + q[3] + '>' + q[0] + ',' + q[1];
        const a = st.anim[s];
        if (!a || a.key !== key || a.done) {
          st.anim[s] = { key, fx: q[2], fy: q[3], x: q[0], y: q[1], t0: now - ((v.jumpTicks || 6) - q[4]) * tickMs(st), dur: jt };
          st.look[s] = [q[0] - q[2], q[1] - q[3]];
          if (s === me && effects) Snd.hop();
        }
      } else {
        const a = st.anim[s];
        if (a && (a.x !== q[0] || a.y !== q[1])) st.anim[s] = null;   // телепорт (берег після води, новий раунд)
      }
      if (pr && effects) {
        if (!pr[5] && q[5] > 0) splash(st, q[0], q[1], s);
        if (!pr[6] && q[6] > 0) home(st, s, q);
        if (pr[5] > 0 && !q[5]) puff(st, q[0], q[1], s);
      }
      st.prev[s] = q.slice();
    }
    st.f = f;
    st.fAt = now;
    st.inited = true;
    st.dirty = true;
  }
  const tickMs = (st) => (st.ctx.view && st.ctx.view.tickMs) || 50;
  function whoAt(f, x, y) {
    const p = f.p || [];
    for (let s = 0; s < p.length; s++) if (p[s] && p[s][5] > 0 && p[s][0] === x && p[s][1] === y) return s;
    return -1;
  }

  // ---- ефекти ----
  function splash(st, x, y, seat) {
    const k = x + ':' + y, now = performance.now();
    if (now - (st.splashAt.get(k) || -1e9) < 600) return;
    st.splashAt.set(k, now);
    const px = cx(x), py = cy(st, y);
    const n = reduced() ? 8 : 18;
    for (let i = 0; i < n; i++) {
      const a = -Math.PI / 2 + (Math.random() - 0.5) * 2.4;
      const sp = 60 + Math.random() * 160;
      st.parts.push({ x: px, y: py, vx: Math.cos(a) * sp, vy: Math.sin(a) * sp - 40, life: 0, max: 500 + Math.random() * 350, r: 2 + Math.random() * 3, c: '#cfefff' });
    }
    for (let i = 0; i < 3; i++) st.rings.push({ x: px, y: py, t0: now + i * 140, dur: 900, r: S * 0.55 });
    const me = mySeat(st);
    st.pops.push({ x: px, y: py - S * 0.5, text: 'шубовсть!', c: seat >= 0 ? palette(st).seats[seat] : '#cfefff', t0: now, dur: 1100 });
    if (seat === me && me >= 0 && !reduced()) st.shake = now;
    Snd.splash(seat === me ? 0.16 : 0.05);
    if (st.parts.length > 260) st.parts.splice(0, st.parts.length - 260);
  }
  function home(st, seat, q) {
    const px = cx(q[0]), py = cy(st, st.rows), pal = palette(st);
    const n = reduced() ? 10 : 22;
    for (let i = 0; i < n; i++) {
      const a = Math.random() * Math.PI * 2, sp = 40 + Math.random() * 140;
      st.parts.push({ x: px, y: py, vx: Math.cos(a) * sp, vy: Math.sin(a) * sp - 60, life: 0, max: 700 + Math.random() * 500, r: 2 + Math.random() * 2.5, c: i % 3 ? pal.path : pal.seats[seat], g: 1 });
    }
    st.pops.push({ x: px, y: py + S * 0.15, text: (MEDAL[q[6]] || '🏁') + ' ' + ORD[q[6]] + '!', c: pal.seats[seat], t0: performance.now(), dur: 1500, big: q[6] === 1 });
    Snd.home(seat === mySeat(st));
  }
  function puff(st, x, y, seat) {
    const px = cx(x), py = cy(st, -1);
    for (let i = 0; i < (reduced() ? 3 : 8); i++) {
      const a = -Math.PI / 2 + (Math.random() - 0.5) * 2;
      st.parts.push({ x: px, y: py, vx: Math.cos(a) * 40, vy: Math.sin(a) * 50, life: 0, max: 450, r: 1.6 + Math.random() * 1.6, c: '#cfefff' });
    }
    void seat;
  }

  // ---- що бачить гравець: де стоїть кожен (і в польоті), сухі камені, сусіди ----
  function posOf(st, s, q, now) {
    const a = st.anim[s];
    if (a && !a.done) {
      const t = clamp((now - a.t0) / a.dur, 0, 1);
      if (t >= 1 && !(q && q[4] > 0)) a.done = true;
      const e = t < 0.5 ? 2 * t * t : 1 - Math.pow(-2 * t + 2, 2) / 2;
      const x0 = cx(a.fx), y0 = cy(st, a.fy), x1 = cx(a.x), y1 = cy(st, a.y);
      return { x: x0 + (x1 - x0) * e, y: y0 + (y1 - y0) * e, lift: Math.sin(Math.PI * t) * S * 0.42, air: t < 1 };
    }
    return { x: cx(q[0]), y: cy(st, q[1]), lift: 0, air: false };
  }

  /// Куди можна стрибнути з (x, y): сусіди без діагоналей; з берега — лише вздовж і вперед; на той берег — з останнього ряду.
  function neighbours(st, x, y) {
    const out = [];
    for (let d = 0; d < 4; d++) {
      const nx = x + DIRS[d][0], ny = y + DIRS[d][1];
      if (nx < 0 || nx >= st.cols) continue;
      if (ny < -1 || ny > st.rows) continue;
      if (y === -1 && d === 1) continue;
      if (ny === st.rows && y !== st.rows - 1) continue;
      if (ny >= 0 && ny < st.rows && st.sk.has(nx + ':' + ny)) continue;
      out.push({ d, x: nx, y: ny });
    }
    return out;
  }
  function myQ(st) {
    const s = mySeat(st), f = st.f;
    const q = s >= 0 && f && f.p ? f.p[s] : null;
    return q || null;
  }
  const canMove = (st) => {
    const q = myQ(st);
    return !!(q && st.f && st.f.ph === 1 && st.ctx.playing && !q[5] && !q[6]);
  };

  // ---- малюнок ----
  function drawStoneUp(g, st, pal, x, y, alpha, tint) {
    const [jx, jy] = jit(x, y);
    const px = cx(x) + jx, py = cy(st, y) + jy;
    g.globalAlpha = alpha * 0.5;
    g.strokeStyle = '#d8f1ff';
    g.lineWidth = 2;
    blob(g, px, py + 3, S * 0.4, x * 31 + y * 7);
    g.stroke();
    g.globalAlpha = alpha;
    g.fillStyle = pal.dry2;
    blob(g, px, py + 3, S * 0.37, x * 31 + y * 7);
    g.fill();
    g.fillStyle = tint || pal.dry;
    blob(g, px, py, S * 0.35, x * 31 + y * 7);
    g.fill();
    g.globalAlpha = alpha * 0.35;
    g.fillStyle = '#fff';
    blob(g, px - 5, py - 5, S * 0.14, x * 31 + y * 7 + 2);
    g.fill();
    g.globalAlpha = 1;
  }
  function drawSteps(g, st, pal, x, y, alpha) {
    const [jx, jy] = jit(x, y);
    const px = cx(x) + jx, py = cy(st, y) + jy;
    g.globalAlpha = alpha * 0.75;
    g.fillStyle = pal.wet;
    for (const [ox, oy, rot] of [[-7, 5, -0.25], [7, -5, 0.25]]) {
      g.beginPath();
      g.ellipse(px + ox, py + oy, 4.2, 7, rot, 0, Math.PI * 2);
      g.fill();
      g.beginPath();
      g.arc(px + ox + (rot > 0 ? 1 : -1), py + oy - 10, 2.6, 0, Math.PI * 2);
      g.fill();
    }
    g.globalAlpha = 1;
  }
  function drawSunk(g, st, x, y, now) {
    const [jx, jy] = jit(x, y);
    const px = cx(x) + jx, py = cy(st, y) + jy;
    g.fillStyle = 'rgba(5,20,30,.55)';
    blob(g, px, py + 2, S * 0.34, x * 31 + y * 7);
    g.fill();
    const k = reduced() ? 0.5 : ((now / 1600 + hash(x, y, 9)) % 1);
    for (const off of [0, 0.5]) {
      const t = (k + off) % 1;
      g.globalAlpha = 0.55 * (1 - t);
      g.strokeStyle = '#bfe6ff';
      g.lineWidth = 1.6;
      g.beginPath();
      g.ellipse(px, py + 2, S * (0.12 + 0.3 * t), S * (0.08 + 0.2 * t), 0, 0, Math.PI * 2);
      g.stroke();
    }
    g.globalAlpha = 0.85;
    g.fillStyle = '#bfe6ff';
    g.font = '700 ' + Math.round(S * 0.26) + 'px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillText('≈', px, py + 2);
    g.globalAlpha = 1;
  }
  function drawCurrent(g, st, now) {
    // течія: короткі світлі риски між рядами, пливуть праворуч
    if (reduced()) return;
    const W = st.cv.w;
    g.strokeStyle = 'rgba(220,245,255,.13)';
    g.lineWidth = 1.5;
    g.beginPath();
    for (let r = 0; r <= st.rows; r++) {
      const y = (BANK + r) * SH + (r === 0 ? 5 : r === st.rows ? -5 : 0);
      const sp = 18 + hash(r, 0, 4) * 14;
      for (let i = 0; i < 3; i++) {
        const x = ((now / 1000) * sp + hash(r, i, 8) * W + i * W / 3) % (W + 40) - 20;
        g.moveTo(x, y + Math.sin(now / 700 + i + r) * 2);
        g.quadraticCurveTo(x + 10, y - 3, x + 22, y);
      }
    }
    g.stroke();
  }
  function drawHints(g, st, now) {
    if (!canMove(st)) return;
    const q = myQ(st);
    const coarseOn = coarse();
    const pulse = reduced() ? 0.7 : 0.55 + 0.25 * Math.sin(now / 260);
    for (const n of neighbours(st, q[0], q[1])) {
      const px = cx(n.x), py = cy(st, n.y);
      const hov = st.hover && st.hover.x === n.x && st.hover.y === n.y;
      g.globalAlpha = hov ? 0.95 : pulse * (coarseOn ? 0.9 : 0.6);
      g.strokeStyle = hov ? '#fff' : 'rgba(255,255,255,.85)';
      g.lineWidth = hov ? 3 : 2;
      g.setLineDash(hov ? [] : [5, 5]);
      g.beginPath();
      if (n.y === st.rows || n.y === -1) g.ellipse(px, py, S * 0.34, S * 0.24, 0, 0, Math.PI * 2);
      else g.ellipse(px, py + 1, S * 0.42, S * 0.34, 0, 0, Math.PI * 2);
      g.stroke();
      g.setLineDash([]);
      // стрілочка в бік стрибка
      const [dx, dy] = DIRS[n.d];
      const ax = px - dx * S * 0.16, ay = py + dy * S * 0.16;
      g.fillStyle = '#fff';
      g.beginPath();
      g.moveTo(ax + dx * 7, ay - dy * 7);
      g.lineTo(ax - dy * 6 - dx * 2, ay - dx * 6 + dy * 2);
      g.lineTo(ax + dy * 6 - dx * 2, ay + dx * 6 + dy * 2);
      g.closePath();
      g.fill();
    }
    g.globalAlpha = 1;
  }
  function drawReveal(g, st, pal, v, now) {
    const rv = v && v.reveal;
    if (!rv || !st.revealAt) return;
    const t = now - st.revealAt;
    const path = rv.path || [], dead = rv.dead || [];
    // тупики
    for (const [x, y] of dead) drawStoneUp(g, st, pal, x, y, clamp((t - 300) / 300, 0, 1) * 0.9, pal.dead);
    // стежка — камінь за каменем
    const shown = Math.min(path.length, Math.floor(t / 70) + 1);
    for (let i = 0; i < shown; i++) drawStoneUp(g, st, pal, path[i][0], path[i][1], clamp((t - i * 70) / 200, 0, 1), pal.path);
    if (shown > 1) {
      g.strokeStyle = 'rgba(255,255,255,.75)';
      g.lineWidth = 2.5;
      g.setLineDash([6, 6]);
      g.lineDashOffset = reduced() ? 0 : -now / 40;
      g.beginPath();
      const p0 = path[0];
      g.moveTo(cx(p0[0]), cy(st, -1));
      for (let i = 0; i < shown; i++) g.lineTo(cx(path[i][0]) + jit(path[i][0], path[i][1])[0], cy(st, path[i][1]) + jit(path[i][0], path[i][1])[1]);
      if (shown === path.length) { const e = path[path.length - 1]; g.lineTo(cx(e[0]), cy(st, st.rows)); }
      g.stroke();
      g.setLineDash([]);
    }
  }
  function drawBody(g, pal, seat, x, y, r, look, mine, now, alpha) {
    const color = pal.seats[seat];
    g.globalAlpha = alpha;
    g.fillStyle = color;
    g.beginPath();
    g.arc(x, y, r, 0, Math.PI * 2);
    g.fill();
    g.lineWidth = 2;
    g.strokeStyle = 'rgba(0,0,0,.45)';
    g.stroke();
    g.fillStyle = 'rgba(255,255,255,.28)';
    g.beginPath();
    g.arc(x - r * 0.3, y - r * 0.35, r * 0.38, 0, Math.PI * 2);
    g.fill();
    const lx = look ? clamp(look[0], -1, 1) : 0, ly = look ? -clamp(look[1], -1, 1) : -1;
    for (const sx of [-1, 1]) {
      const ex = x + sx * r * 0.36 + lx * r * 0.12, ey = y - r * 0.12 + ly * r * 0.12;
      g.fillStyle = '#fff';
      g.beginPath();
      g.ellipse(ex, ey, r * 0.22, r * 0.27, 0, 0, Math.PI * 2);
      g.fill();
      g.fillStyle = '#1b1b1b';
      g.beginPath();
      g.arc(ex + lx * r * 0.09, ey + ly * r * 0.1, r * 0.11, 0, Math.PI * 2);
      g.fill();
    }
    if (mine) {
      g.strokeStyle = '#fff';
      g.lineWidth = 2.5;
      g.globalAlpha = alpha * (reduced() ? 0.9 : 0.65 + 0.35 * Math.sin(now / 200));
      g.beginPath();
      g.arc(x, y, r + 4, 0, Math.PI * 2);
      g.stroke();
    }
    g.globalAlpha = 1;
  }
  function label(g, pal, text, x, y, color, bold) {
    g.font = (bold ? '800 ' : '700 ') + '12px ' + pal.font;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    const w = g.measureText(text).width + 8;
    g.fillStyle = 'rgba(10,18,24,.72)';
    g.beginPath();
    if (g.roundRect) g.roundRect(x - w / 2, y - 8, w, 16, 8); else g.rect(x - w / 2, y - 8, w, 16);
    g.fill();
    g.fillStyle = color;
    g.fillText(text, x, y + 0.5);
  }
  function drawRunners(g, st, pal, f, now) {
    const p = f.p || [];
    const me = mySeat(st);
    // хто де стоїть: однакова клітинка — купкою
    const spots = new Map(), list = [];
    for (let s = 0; s < p.length; s++) {
      const q = p[s];
      if (!q) continue;
      const pos = posOf(st, s, q, now);
      const it = { s, q, pos };
      list.push(it);
      if (pos.air || q[5] > 0) continue;
      const k = q[0] + ':' + q[1];
      if (!spots.has(k)) spots.set(k, []);
      spots.get(k).push(it);
    }
    for (const arr of spots.values()) {
      const n = arr.length;
      if (n < 2) continue;
      const rad = S * (n > 4 ? 0.27 : 0.2);
      arr.forEach((it, i) => {
        const a = -Math.PI / 2 + (i / n) * Math.PI * 2;
        it.pos.x += Math.cos(a) * rad;
        it.pos.y += Math.sin(a) * rad * 0.8;
        it.small = n > 2;
      });
    }
    // спершу дальні (угорі), потім ближчі; свій — останнім, щоб не губився в купці
    list.sort((a, b) => (a.s === me) - (b.s === me) || a.pos.y - b.pos.y);
    for (const it of list) {
      const { s, q, pos } = it;
      const r = S * (it.small ? 0.2 : 0.25);
      if (q[5] > 0) {
        // у воді: видно лише верх, довкола хвилі
        const px = cx(q[0]), py = cy(st, q[1]);
        const k = (now / 500) % 1;
        g.strokeStyle = 'rgba(210,240,255,.7)';
        g.lineWidth = 1.5;
        for (const o of [0, 0.5]) {
          const t = (k + o) % 1;
          g.globalAlpha = 1 - t;
          g.beginPath();
          g.ellipse(px, py + 6, r * (1 + t * 1.2), r * (0.5 + t * 0.5), 0, 0, Math.PI * 2);
          g.stroke();
        }
        g.save();
        g.beginPath();
        g.rect(px - S, py - S, S * 2, S + 6);
        g.clip();
        const bob = reduced() ? 0 : Math.sin(now / 160 + s) * 2;
        drawBody(g, pal, s, px, py + 9 + bob, r, [0, 1], s === me, now, 0.9);
        g.restore();
        const total = (st.ctx.view && st.ctx.view.wetTicks) || 30;
        g.strokeStyle = '#bfe6ff';
        g.lineWidth = 2.5;
        g.beginPath();
        g.arc(px, py + 9, r + 7, -Math.PI / 2, -Math.PI / 2 + Math.PI * 2 * (q[5] / total));
        g.stroke();
        g.globalAlpha = 1;
        continue;
      }
      // тінь — на землі, тіло — вище на висоту стрибка
      const sh = 1 - pos.lift / (S * 0.9);
      g.fillStyle = 'rgba(0,0,0,.28)';
      g.beginPath();
      g.ellipse(pos.x, pos.y + r * 0.7, r * 0.95 * sh, r * 0.42 * sh, 0, 0, Math.PI * 2);
      g.fill();
      const sq = pos.air ? 1 + 0.08 * Math.sin((pos.lift / (S * 0.42)) * Math.PI) : 1;
      drawBody(g, pal, s, pos.x, pos.y - pos.lift, r * sq, st.look[s] || [0, 1], s === me, now, 1);
      if (q[6] > 0) {
        g.font = Math.round(r * 0.95) + 'px system-ui, sans-serif';
        g.textAlign = 'center';
        g.textBaseline = 'middle';
        g.fillText(MEDAL[q[6]] || '🏁', pos.x + r * 0.9, pos.y - r * 0.9);
      }
      it.ly = pos.y - pos.lift;
    }
    // імена — окремим шаром поверх усіх тіл; у тісній купці — лише своє
    for (const it of list) {
      if (it.q[5] > 0 || it.ly == null) continue;
      if (it.small && it.s !== me) continue;
      const nm = it.s === me ? 'ти' : short(botless(nameAt(st.ctx, it.s)), 10);
      label(g, pal, nm, it.pos.x, it.ly + S * (it.small ? 0.36 : 0.42), pal.seats[it.s], it.s === me);
    }
  }
  function drawParts(g, st, dt) {
    const sec = dt / 1000;
    let w = 0;
    for (const p of st.parts) {
      p.life += dt;
      if (p.life >= p.max) continue;
      p.vy += (p.g ? 120 : 420) * sec;
      p.x += p.vx * sec;
      p.y += p.vy * sec;
      g.globalAlpha = 1 - p.life / p.max;
      g.fillStyle = p.c;
      g.beginPath();
      g.arc(p.x, p.y, p.r, 0, Math.PI * 2);
      g.fill();
      st.parts[w++] = p;
    }
    st.parts.length = w;
    const now = performance.now();
    let k = 0;
    for (const r of st.rings) {
      const t = (now - r.t0) / r.dur;
      if (t >= 1) continue;
      st.rings[k++] = r;
      if (t < 0) continue;
      g.globalAlpha = 0.7 * (1 - t);
      g.strokeStyle = '#d8f1ff';
      g.lineWidth = 2;
      g.beginPath();
      g.ellipse(r.x, r.y + 3, r.r * (0.3 + t), r.r * (0.2 + t * 0.6), 0, 0, Math.PI * 2);
      g.stroke();
    }
    st.rings.length = k;
    g.globalAlpha = 1;
  }
  function drawPops(g, st, pal, now) {
    let k = 0;
    for (const p of st.pops) {
      const t = (now - p.t0) / p.dur;
      if (t >= 1) continue;
      st.pops[k++] = p;
      const size = p.big ? 22 : 15;
      g.globalAlpha = t < 0.7 ? 1 : 1 - (t - 0.7) / 0.3;
      g.font = '800 ' + Math.round(size * (t < 0.12 ? 0.6 + t * 3.3 : 1)) + 'px ' + pal.font;
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.lineWidth = 4;
      g.strokeStyle = 'rgba(0,0,0,.6)';
      const y = p.y - t * 26;
      const x = clamp(p.x, 50, st.cv.w - 50);
      g.strokeText(p.text, x, y);
      g.fillStyle = p.c;
      g.fillText(p.text, x, y);
    }
    st.pops.length = k;
    g.globalAlpha = 1;
  }
  function fitText(g, pal, text, maxW, size, weight) {
    let s = size;
    g.font = (weight || 800) + ' ' + s + 'px ' + pal.font;
    while (s > 9 && g.measureText(text).width > maxW) { s -= 1; g.font = (weight || 800) + ' ' + s + 'px ' + pal.font; }
    return s;
  }
  function banner(g, st, pal, l1, l2, y) {
    const W = st.cv.w;
    const h = l2 ? 54 : 34;
    g.fillStyle = 'rgba(8,16,22,.8)';
    g.beginPath();
    if (g.roundRect) g.roundRect(8, y, W - 16, h, 12); else g.rect(8, y, W - 16, h);
    g.fill();
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    fitText(g, pal, l1, W - 32, 17);
    g.fillStyle = '#fff';
    g.fillText(l1, W / 2, y + (l2 ? 17 : h / 2));
    if (l2) {
      fitText(g, pal, l2, W - 32, 13, 700);
      g.fillStyle = 'rgba(255,255,255,.85)';
      g.fillText(l2, W / 2, y + 38);
    }
  }
  function overlays(g, st, pal, f, v, now) {
    const W = st.cv.w, H = st.cv.h;
    const ctx = st.ctx;
    // смужка часу над тим берегом
    if (f.ph === 1) {
      const total = v.roundTicks || 1200;
      const k = clamp((f.left || 0) / total, 0, 1);
      const hot = (f.left || 0) * tickMs(st) <= 10000;
      g.fillStyle = 'rgba(0,0,0,.35)';
      g.fillRect(0, 0, W, 5);
      g.fillStyle = hot ? (Math.floor(now / 300) % 2 ? '#ff6b5a' : '#ffb35a') : pal.path;
      g.fillRect(0, 0, W * k, 5);
    }
    if (f.ph === 0 && ctx.playing) {
      const s = Math.max(1, Math.ceil(((f.left || 0) * tickMs(st)) / 1000));
      const into = ((f.left || 0) * tickMs(st)) % 1000 / 1000;
      const sc = reduced() ? 1 : 1 + 0.5 * Math.pow(into, 3);
      g.fillStyle = 'rgba(0,0,0,.28)';
      g.fillRect(0, 0, W, H);
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.font = '900 ' + Math.round(84 * sc) + 'px ' + pal.font;
      g.lineWidth = 6;
      g.strokeStyle = 'rgba(0,0,0,.55)';
      g.strokeText(String(s), W / 2, H / 2);
      g.fillStyle = '#fff';
      g.fillText(String(s), W / 2, H / 2);
      const l1 = v.party ? 'Брід: перейди першим!' : 'Раунд ' + (v.round || 1) + ' з ' + (v.rounds || 3) + ' · брід ' + st.cols + '×' + st.rows;
      fitText(g, pal, l1, W - 30, 18);
      g.lineWidth = 4;
      g.strokeText(l1, W / 2, H / 2 - 70);
      g.fillText(l1, W / 2, H / 2 - 70);
      const l2 = 'стежку видно лише по слідах';
      fitText(g, pal, l2, W - 30, 14, 700);
      g.strokeText(l2, W / 2, H / 2 + 66);
      g.fillStyle = 'rgba(255,255,255,.88)';
      g.fillText(l2, W / 2, H / 2 + 66);
    }
    if ((f.ph === 2 || f.ph === 3) && v.phase !== 'lobby') {
      const order = v.last || v.order || [];
      const pts = v.points || [3, 2, 1];
      const who = order.slice(0, 3).map((s, i) => (MEDAL[i + 1]) + ' ' + short(nameAt(ctx, s), 12) + (v.party ? '' : ' +' + (pts[i] || 0)));
      if (f.ph === 2 || v.party) {
        const l1 = order.length ? (v.party ? 'Перейшли' : 'Раунд ' + (v.round || 1)) + ': першим — ' + short(nameAt(ctx, order[0]), 14) : 'Ніхто не перейшов — стежка була така';
        banner(g, st, pal, l1, order.length ? who.join(' · ') : '', SH * BANK + 6);
      } else {
        const ws = v.winners || [];
        const l1 = ws.length ? '🏆 ' + ws.map((s) => short(nameAt(ctx, s), 14)).join(', ') + (ws.length > 1 ? ' — поділили перемогу' : ' — перемога!') : 'Ніхто не перейшов';
        const tot = [];
        (v.pts || []).forEach((p, s) => { if (p != null) tot.push([s, p]); });
        tot.sort((a, b) => b[1] - a[1]);
        banner(g, st, pal, l1, tot.slice(0, 5).map(([s, p]) => short(nameAt(ctx, s), 10) + ' ' + p).join(' · '), SH * BANK + 6);
      }
    }
  }

  function draw(st, now) {
    const c = st.cv;
    if (!c) return;
    const pal = palette(st);
    const g = c.ctx;
    const f = st.f || { ph: 4, p: [], tr: [], sk: [] };
    const v = st.ctx.view || {};
    const dt = st.lastDraw ? Math.min(100, now - st.lastDraw) : 16;
    st.lastDraw = now;
    c.resize();
    g.save();
    if (st.shake && now - st.shake < 260) {
      const k = 1 - (now - st.shake) / 260;
      g.translate((Math.random() - 0.5) * 7 * k, (Math.random() - 0.5) * 5 * k);
    }
    g.drawImage(background(st, pal), 0, 0, c.w, c.h);
    drawCurrent(g, st, now);
    // затоплені — хвилі до кінця раунду
    for (const k of st.sk) { const [x, y] = k.split(':').map(Number); drawSunk(g, st, x, y, now); }
    drawReveal(g, st, pal, v, now);
    // сухі камені: під тими, хто стоїть, і з мокрим слідом (слід бліде — камінь знов ховається)
    const trail = (v.trailTicks || 100);
    const up = new Map();
    for (const t of f.tr || []) up.set(t[0] + ':' + t[1], { x: t[0], y: t[1], a: clamp(t[2] / trail, 0, 1), tr: t[2] / trail });
    for (const q of f.p || []) {
      if (!q || q[5] > 0 || q[1] < 0 || q[1] >= st.rows) continue;
      const k = q[0] + ':' + q[1];
      const a = st.anim[(f.p || []).indexOf(q)];
      if (q[4] > 0 && a && !a.done) continue;   // ще летить — камінь проявиться, коли стане
      const o = up.get(k);
      up.set(k, { x: q[0], y: q[1], a: 1, tr: o ? o.tr : 0 });
    }
    for (const u of up.values()) drawStoneUp(g, st, pal, u.x, u.y, 0.25 + 0.75 * u.a);
    for (const u of up.values()) if (u.tr > 0) drawSteps(g, st, pal, u.x, u.y, u.tr);
    drawHints(g, st, now);
    drawRunners(g, st, pal, f, now);
    drawParts(g, st, dt);
    drawPops(g, st, pal, now);
    g.restore();
    overlays(g, st, pal, f, v, now);
    st.dirty = false;
  }

  /// Цикл малювання: поки грають, щось летить чи догорає; інакше — один кадр на зміну.
  function busy(st) {
    const f = st.f;
    return !!((f && (f.ph <= 2) && st.ctx.playing) || st.parts.length || st.pops.length || st.rings.length
      || (st.revealAt && performance.now() - st.revealAt < 3000));
  }
  function spin(st) {
    if (st.raf) return;
    const loop = (now) => {
      st.raf = 0;
      if (!st.root.isConnected || !st.cv) return;
      const b = busy(st);
      if (b || st.dirty) {
        // у спокої (ніхто не летить) вистачить ~30 к/с — вода тече й так
        const calm = !st.parts.length && !st.anim.some((a) => a && !a.done) && st.f && st.f.ph !== 0;
        if (!calm || now - st.lastDraw > 30 || st.dirty) draw(st, now);
        // затиснута стрілка: досилаємо крок, поки летимо
        if (held.d >= 0 && st.ctx.mine && canMove(st) && now - held.since >= HOLD_MS && now - st.lastSend >= REPEAT_MS) step(st, { d: held.d });
      }
      if (b || held.d >= 0) st.raf = requestAnimationFrame(loop);
    };
    st.raf = requestAnimationFrame(loop);
  }

  // ---- ввід ----
  function step(st, payload) {
    st.lastSend = performance.now();
    st.ctx.input('step', payload);
  }
  function cellAt(st, ev) {
    const r = st.cv.el.getBoundingClientRect();
    if (!r.width) return null;
    const lx = ((ev.clientX - r.left) * st.cv.w) / r.width, ly = ((ev.clientY - r.top) * st.cv.h) / r.height;
    const x = Math.floor(lx / S - MX);
    const fy = ly / SH;
    const y = fy < BANK ? st.rows : fy >= BANK + st.rows ? -1 : st.rows - 1 - Math.floor(fy - BANK);
    return { x: clamp(x, 0, st.cols - 1), y, lx, ly };
  }
  function tapTo(st, c) {
    const q = myQ(st);
    if (!q || !c) return;
    const n = neighbours(st, q[0], q[1]).find((k) => k.x === c.x && k.y === c.y);
    if (n) { step(st, n.y === st.rows ? { d: 3 } : n.y === -1 && q[1] === -1 ? { d: n.d } : { x: n.x, y: n.y }); return true; }
    return false;
  }
  function towards(st, c) {
    // не сусідня клітинка — крок у її бік (переважний напрямок)
    const q = myQ(st);
    if (!q || !c) return;
    const dx = c.x - q[0], dy = c.y - q[1];
    if (!dx && !dy) return;
    step(st, { d: Math.abs(dy) >= Math.abs(dx) ? (dy > 0 ? 3 : 1) : dx > 0 ? 0 : 2 });
  }
  function wire(st) {
    const el = st.cv.el;
    if (el._brWired) return;
    el._brWired = true;
    const S2 = () => st.root._brid || st;
    el.addEventListener('pointerdown', (e) => {
      const s = S2();
      if (!canMove(s) || (e.button != null && e.button > 0)) return;
      const c = cellAt(s, e);
      s.down = { x: e.clientX, y: e.clientY, c, done: false, id: e.pointerId };
      // сусідній камінь — одразу, без чекання відпускання пальця
      if (tapTo(s, c)) s.down.done = true;
      e.preventDefault();
      spin(s);
    });
    el.addEventListener('pointerup', (e) => {
      const s = S2();
      const d = s.down;
      s.down = null;
      if (!d || d.done || !canMove(s)) return;
      const mx = e.clientX - d.x, my = e.clientY - d.y;
      if (Math.hypot(mx, my) > 22) step(s, { d: Math.abs(my) >= Math.abs(mx) ? (my < 0 ? 3 : 1) : mx > 0 ? 0 : 2 });   // свайп
      else towards(s, d.c);
    });
    el.addEventListener('pointercancel', () => { S2().down = null; });
    el.addEventListener('pointermove', (e) => {
      const s = S2();
      if (e.pointerType !== 'mouse') return;
      const c = canMove(s) ? cellAt(s, e) : null;
      const k = c ? c.x + ':' + c.y : '';
      if (k !== (s.hover ? s.hover.x + ':' + s.hover.y : '')) { s.hover = c; s.dirty = true; spin(s); }
    });
    el.addEventListener('pointerleave', () => { const s = S2(); if (s.hover) { s.hover = null; s.dirty = true; spin(s); } });
  }

  // ---- розмір: поле міряємо по root і висоті екрана, камінь на телефоні — від ~44 px ----
  function sizeCanvas(st) {
    const w = Math.round((st.cols + 2 * MX) * S), h = Math.round((st.rows + 2 * BANK) * SH);
    if (st.cv && st.cv.w === w && st.cv.h === h) return;
    st.cv = HGames.ui.canvas(st.root, { w, h, cls: 'brboard' });
    st.bg = null;
    fit(st);
  }
  function fit(st) {
    const cv = st.cv, root = st.root;
    if (!cv) return;
    const rw = root.clientWidth || 360;
    const F = HGames.ui.fit ? HGames.ui.fit() : { w: innerWidth, h: innerHeight, top: 0, dock: 0 };
    const wide = rw >= 700 && !F.land;
    root.classList.toggle('brwide', wide);
    const hud = root.querySelector(':scope > .brhud');
    const hudH = wide ? 0 : (hud ? hud.offsetHeight + 6 : 34);
    // Над полем — шапка сайту й картки (міряємо, де root у документі), під ним — рядок статусу картки (~44 px).
    const rt = root.getBoundingClientRect().top + window.scrollY;
    let above;
    if (st.ctx && st.ctx.embedded) {
      // У вечірці над нами чужа шапка (дошка, картка правил) — не вгадуємо «100 px сайту», а беремо, де справді
      // стоїть наш root; лише не віддаємо шапці більше половини екрана, щоб поле не стало крихітним.
      above = Math.min(Math.max(rt, F.top), F.top + F.h * 0.5);
    } else above = rt > F.top && rt < F.h * 0.6 ? rt : F.top + (F.imm ? 10 : 100);
    const maxH = Math.max(300, F.h - above - F.dock - hudH - 44);
    const room = wide ? rw - 240 : rw;
    // Вузький екран: камінь не нижчий за ~40 px (палець; ширший він і так ~48) — хай краще сторінка трохи
    // прокрутиться, ніж мазати. 40, а не 44: на 360×780 з шістьма гравцями берег тоді ще не ховається під док.
    const minW = rw < 480 ? Math.ceil(40 * cv.w / SH) : 240;
    const w = Math.max(Math.min(rw, minW), Math.floor(Math.min(room, maxH * (cv.w / cv.h), 620)));
    cv.el.style.width = w + 'px';
    cv.el.style.maxWidth = '100%';
    st.dirty = true;
  }

  // ---- рядок гравців і легенда (DOM) ----
  function hud(st) {
    const root = st.root, ctx = st.ctx, v = ctx.view || {}, f = st.f;
    let el = root.querySelector(':scope > .brhud');
    if (!el) {
      el = document.createElement('div');
      el.className = 'brhud';
      root.insertBefore(el, root.firstChild);
      el.addEventListener('click', (e) => {
        if (!e.target.closest('.brsnd')) return;
        Snd.on = !Snd.on;
        try { localStorage.setItem('brid.sound', Snd.on ? '1' : '0'); } catch { /* приватне вікно */ }
        st.hudSig = '';
        hud(root._brid || st);
      });
    }
    const p = (f && f.p) || [];
    const me = mySeat(st);
    const pal = palette(st);
    const lobby = v.phase === 'lobby';
    let html = '';
    for (let s = 0; s < p.length; s++) {
      const q = p[s];
      if (!q) continue;
      const pts = !v.party && v.pts && v.pts[s] != null ? v.pts[s] : null;
      let tag = '';
      if (!lobby && q[6] > 0) tag = (MEDAL[q[6]] || '🏁') + (q[6] > 3 ? ORD[q[6]] : '');
      else if (!lobby && q[5] > 0) tag = '💦';
      else if (!lobby && f && f.ph === 1 && q[8] > 0) tag = '<small>' + q[8] + '/' + st.rows + '</small>';
      html += '<span class="brchip' + (s === me ? ' me' : '') + (q[6] > 0 ? ' home' : '') + '" style="--c:' + pal.seats[s] + '">'
        + '<i></i><span class="nm">' + ctx.esc(short(botless(nameAt(ctx, s)), 16)) + '</span>'
        + (pts != null && !lobby ? '<b title="очки партії">' + pts + '</b>' : '') + (tag ? '<em>' + tag + '</em>' : '') + '</span>';
    }
    // багато гравців на вузькому екрані — чипи тісніші, щоб рядок гравців не з'їдав висоту річки
    root.classList.toggle('brtight', p.filter(Boolean).length >= 5 && root.clientWidth < 480);
    let clock = '';
    if (f && !lobby && ctx.playing) {
      const secs = Math.ceil(((f.left || 0) * tickMs(st)) / 1000);
      if (f.ph === 1) clock = '⏱ ' + Math.floor(secs / 60) + ':' + String(secs % 60).padStart(2, '0');
      else if (f.ph === 0) clock = '⏳ ' + secs;
      if (!v.party && v.rounds > 1) clock += ' · раунд ' + (v.round || 1) + '/' + v.rounds;
    }
    const hot = f && f.ph === 1 && (f.left || 0) * tickMs(st) <= 10000;
    const tail = (clock ? '<span class="brclock' + (hot ? ' hot' : '') + '">' + clock + '</span>' : '')
      + '<button type="button" class="brsnd ghost" title="звук гри">' + (Snd.on ? '🔊' : '🔇') + '</button>';
    const sig = html + '|' + tail;
    if (sig !== st.hudSig) {
      st.hudSig = sig;
      el.innerHTML = '<div class="brppl">' + html + '</div><div class="brmeta">' + tail + '</div>';
    }
    let lg = root.querySelector(':scope > .brlegend');
    if (!lg) {
      lg = document.createElement('div');
      lg.className = 'brlegend';
      lg.innerHTML = '<span><i class="lg-dry"></i>сухий камінь — тут стоять</span><span><i class="lg-wet"></i>мокрий слід — сохне за 5 с</span>'
        + '<span><i class="lg-sunk"></i>≈ тут шубовснули</span>';
      root.appendChild(lg);
    }
    // легенда — завжди під полем (канвас каркас додає в кінець root, тож після першого виміру переставляємо)
    if (st.cv && st.cv.el.nextElementSibling !== lg) root.appendChild(lg);
  }

  // ---- статус під грою ----
  function statusText(ctx) {
    const st = [...live].find((s) => s.ctx === ctx);
    const v = ctx.view || {};
    const f = (st && st.f) || ctx.frame || v.frame;
    if (!ctx.playing) {
      if (ctx.room && ctx.room.status === 'lobby') {
        const host = ctx.room.host && ctx.me && String(ctx.room.host).toLowerCase() === String(ctx.me.nick).toLowerCase();
        if (v.botWanted) return '🤖 Боти вже на березі — тисни «Почати». Без нагород';
        return host ? 'Тисни «Почати», коли всі сіли (1–8; самому — «🤖 + бот»)' : 'Брід на 1–8 гравців. Стартує господар';
      }
      return '';
    }
    if (!f) return '';
    if (f.ph === 0) return v.party ? 'Готуйсь… перейди річку першим' : 'Готуйсь… раунд ' + (v.round || 1) + ' з ' + (v.rounds || 3);
    if (f.ph === 2) {
      const o = v.last || v.order || [];
      return o.length ? (v.party ? 'Першим' : 'Раунд ' + (v.round || 1) + ': першим') + ' перейшов ' + nameAt(ctx, o[0]) : 'Ніхто не перейшов — так ішла стежка';
    }
    if (f.ph === 3) {
      const ws = v.winners || [];
      return ws.length ? '🏆 ' + ws.map((s) => nameAt(ctx, s)).join(', ') : 'Ніхто не перейшов';
    }
    if (!ctx.mine) return 'Дивишся з берега · раунд ' + (v.round || 1);
    const q = f.p && f.p[ctx.seat];
    if (q && q[6] > 0) return '🏁 Ти на тому березі — ' + ORD[q[6]] + '. Дивись, як бредуть інші';
    if (q && q[5] > 0) return '💦 Шубовсть! Камінь затоплено — зараз знову на березі';
    const how = padOn() ? 'Хрестовина — стрибок на сусідній камінь' : coarse() ? 'Тапни сусідній камінь (чи свайпни)' : 'Стрілки/WASD — стрибок на сусідній камінь';
    return how + ' · мокрі сліди підказують стежку' + (v.party ? '' : ' · раунд ' + (v.round || 1) + '/' + (v.rounds || 3));
  }

  HGames.register({
    id: 'brid',
    added: '2026-10-06',
    icon: ICON,
    seatNames: ['синій', 'рудий', 'зелений', 'жовтий', 'бузковий', 'м’ятний', 'рожевий', 'сірий'],
    seatClass: ['br0', 'br1', 'br2', 'br3', 'br4', 'br5', 'br6', 'br7'],
    pad: { dirs: true, hint: '{dpad} стрибок на сусідній камінь' },

    mount(root, ctx) {
      const st = state(root, ctx);
      root.classList.add('brroot');
      const v = ctx.view || {};
      st.cols = v.cols || 5;
      st.rows = v.rows || 9;
      hud(st);
      sizeCanvas(st);
      wire(st);
      if (v.frame) take(st, v.frame, false);
      hud(st);
      st.onFit = () => { const s = root._brid; if (s) { fit(s); s.dirty = true; spin(s); } };
      if (HGames.ui.onFit) HGames.ui.onFit(root, st.onFit);
      else window.addEventListener('resize', st.onFit);
      fit(st);
      spin(st);
    },

    update(root, ctx) {
      const st = state(root, ctx);
      const v = ctx.view || {};
      const vf = v.frame;
      if (v.cols && v.rows && (v.cols !== st.cols || v.rows !== st.rows)) { st.cols = v.cols; st.rows = v.rows; newRound(st); sizeCanvas(st); }
      // вид — правда на зміні фази (новий раунд, кінець, F5); посеред гри кадри свіжіші
      if (vf && (!st.f || vf.t >= st.f.t || vf.ph !== st.f.ph || !ctx.playing)) take(st, vf, !!st.f && vf.ph !== st.f.ph);
      if (!st.cv) return;
      st.cv.el.classList.toggle('play', !!(ctx.mine && ctx.playing));
      hud(st);
      if (HGames.ui.onFit) HGames.ui.onFit(root, st.onFit);
      fit(st);
      st.dirty = true;
      spin(st);
    },

    frame(root, ctx, f) {
      const st = state(root, ctx);
      if (!f) return;
      take(st, f, true);
      hud(st);
      spin(st);
    },

    onKey(e, ctx) {
      const st = [...live].find((s) => s.ctx === ctx);
      const d = KEYS[e.code];
      if (d == null || !st || !ctx.mine) return false;
      if (!ctx.playing) return false;
      if (held.d !== d) { held.d = d; held.since = performance.now(); }
      if (!e.repeat && canMove(st)) step(st, { d });
      spin(st);
      return true;
    },

    status: statusText,

    unmount(root) {
      const st = root._brid;
      if (!st) return;
      cancelAnimationFrame(st.raf);
      st.raf = 0;
      if (HGames.ui.onFit) HGames.ui.onFit(root, null);
      else window.removeEventListener('resize', st.onFit);
      root.classList.remove('brroot', 'brwide');
      live.delete(st);
      st.cv = null;
      root._brid = null;
    },
  });
})();
