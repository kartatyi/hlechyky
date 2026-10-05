// Крадії груш (grushi): сад згори на канвасі, рядок над ним, табличка комор під ним, стік і «👊» під палець.
// Правила — на сервері (Impl/Grushi.cs, GrushiCore.cs), spec — docs/games/specs/grushi.md. Модуль — і самостійна гра,
// і міні-гра вечірки (HGames.embed): нічого поза ctx і своїм root, таймери, rAF і слухачі — геть в unmount.
(() => {
  const TICK_MS = 40;
  const W = 1000;                       // сад 1000×1000 од (GrushiCore.Size)
  const SIZE = 600;                     // логічний канвас
  const SC = SIZE / W;
  const SEND_MS = 50;                   // наміри — не частіше 20/с
  const KEEP_MS = 400;                  // той самий напрямок підтверджуємо раз на 0,4 с (намір живе 1,2 с)
  const AWAKE_MS = 1500;                // після останньої події ще догорають бризки й «+3»
  const BODY_R = 28, PEAR_R = 14, LARDER_R = 80, PUSH_R = 80, CARRY_MAX = 5, PUSH_CD = 75, STEAL_TICKS = 25;
  const SHAKE_TICKS = 30;
  const TREES = [[500, 500], [290, 290], [710, 290], [290, 710], [710, 710]];
  const COS16 = [], SIN16 = [];
  for (let i = 0; i < 16; i++) { COS16.push(Math.cos((i * Math.PI) / 8)); SIN16.push(Math.sin((i * Math.PI) / 8)); }
  const sectorOf = (dx, dy) => (dx === 0 && dy === 0 ? -1 : ((Math.round(Math.atan2(dy, dx) / (Math.PI / 8)) % 16) + 16) % 16);
  const SEAT_VARS = [['--gr-s0', '#5aa9ff'], ['--gr-s1', '#e07b4f'], ['--gr-s2', '#6fcf7a'], ['--gr-s3', '#f4c542'],
    ['--gr-s4', '#b48cf2'], ['--gr-s5', '#5fd6c2'], ['--gr-s6', '#f08cb8'], ['--gr-s7', '#b7c2bd']];
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M8 4.3c1.3 0 2 1 2.1 2.3.1 1 .7 1.6 1.5 2.6 1.1 1.5.9 4.7-3.6 4.7s-4.7-3.2-3.6-4.7c.8-1 1.4-1.6 1.5-2.6C6 5.3 6.7 4.3 8 4.3Z" fill="var(--gr-pear, #cfd85a)"/>'
    + '<path d="M8 4.5V2.2" stroke="var(--clay)" stroke-width="1.3" stroke-linecap="round"/>'
    + '<path d="M8.3 2.8c1.5-1.2 3.1-1 3.8-.4-1 1.3-2.5 1.6-3.8.4Z" fill="var(--ok)"/></svg>';

  const padOn = () => !!(window.HPad && HPad.pads > 0);
  const reduced = () => !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
  const clamp = (v, a, b) => Math.min(Math.max(v, a), b);
  const nameAt = (ctx, s) => String((ctx.nameOf ? ctx.nameOf(s) : ctx.nickOf(s)) || ctx.seatName(s) || '');
  /// Коротке ім'я для поля: «🤖 бот зелений» → «🤖 зелений», довге — з трикрапкою.
  const shortName = (ctx, s, n) => short(nameAt(ctx, s).replace(/^🤖\s*бот\s+/u, '🤖 '), n);
  const short = (s, n) => (s.length > n ? s.slice(0, n - 1) + '…' : s);
  const clock = (ticks) => {
    const s = Math.max(0, Math.ceil((ticks * TICK_MS) / 1000));
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  };

  // ---- клавіші: утримання спільне для всіх змонтованих столів (як у Крижині) ----
  const live = new Set();
  const held = { up: false, down: false, left: false, right: false };
  const KEYS = {
    ArrowUp: 'up', KeyW: 'up', ArrowDown: 'down', KeyS: 'down', ArrowLeft: 'left', KeyA: 'left', ArrowRight: 'right', KeyD: 'right',
  };
  const KEYS_BY_KEY = { w: 'up', s: 'down', a: 'left', d: 'right', ц: 'up', і: 'down', ф: 'left', в: 'right' };
  const keyOf = (e) => KEYS[e.code] || KEYS_BY_KEY[String(e.key || '').toLowerCase()];
  const isPush = (e) => e.code === 'Space' || e.key === ' ' || e.code === 'KeyX' || e.code === 'KeyE';
  const keySector = () => sectorOf((held.right ? 1 : 0) - (held.left ? 1 : 0), (held.down ? 1 : 0) - (held.up ? 1 : 0));

  /// Усе відпустили. force — шлемо одразу: сторінка ховається, другого шансу не буде.
  function releaseAll(force) {
    held.up = held.down = held.left = held.right = false;
    for (const st of live) { st.stickA = null; st.ptrA = null; st.ptrDown = false; st.padA = null; send(st, force === true); }
  }
  document.addEventListener('keyup', (e) => {
    const k = keyOf(e);
    if (!k || !held[k]) return;
    held[k] = false;
    for (const st of live) send(st);
  });
  window.addEventListener('blur', () => releaseAll(false));
  window.addEventListener('pagehide', () => releaseAll(true));
  document.addEventListener('visibilitychange', () => { if (document.hidden) releaseAll(true); });

  // ---- стан картки ----
  function state(root, ctx) {
    let st = root._grushi;
    if (!st) {
      st = root._grushi = {
        root, ctx, cv: null, raf: 0, awakeUntil: 0, interp: HGames.ui.Interp(TICK_MS), last: null, phAt: 0,
        trees: TREES, larders: [], pal: null, palAt: 0, spr: new Map(), sprK: 0, bg: null, bgKey: '', lk: 1, lkAt: 0,
        // змішаний кадр
        bx: new Float64Array(8), by: new Float64Array(8), on: new Uint8Array(8),
        // ввід
        sent: null, sentAt: 0, stickA: null, ptrA: null, ptrDown: false, ptrX: 0, ptrY: 0, padA: null, pushAt: 0,
        // соки
        parts: [], pops: [], flyers: [], pearAnim: new Map(), pearsPrev: new Map(), bounce: new Float64Array(8).fill(-1e9),
        hit: new Float64Array(8).fill(-1e9), rings: [], shake: 0, shakeAmp: 0, lastFall: null, goAt: 0,
        hudSig: '', status: '', was: false, fitted: false,
      };
      live.add(st);
    }
    st.ctx = ctx;
    return st;
  }
  const stOf = (ctx) => { for (const s of live) if (s.ctx === ctx) return s; return null; };
  const mySeat = (st) => (st.ctx && st.ctx.mine && st.ctx.seat != null ? st.ctx.seat : -1);

  function takeView(st, v) {
    if (!v) return;
    if (Array.isArray(v.trees) && v.trees.length) st.trees = v.trees;
    st.larders = Array.isArray(v.larders) ? v.larders : [];
  }

  // ---- кольори ----
  function palette(st) {
    const now = performance.now();
    if (st.pal && now - st.palAt < 2000) return st.pal;
    const c = (n, d) => HGames.ui.css(n, d);
    st.pal = {
      seats: SEAT_VARS.map(([n, d]) => c(n, d)),
      accent: c('--accent', '#f4a340'),
      ok: c('--ok', '#7bd389'),
      bad: c('--bad', '#ff6b5a'),
      font: getComputedStyle(st.root).fontFamily || 'system-ui, sans-serif',
    };
    st.palAt = now;
    return st.pal;
  }
  const darkCache = new Map();
  function tint(hex, k) {
    const key = hex + '|' + k;
    let v = darkCache.get(key);
    if (v) return v;
    const m = /^#?([0-9a-f]{6})$/i.exec(String(hex).trim());
    if (!m) return hex;
    const n = parseInt(m[1], 16);
    const f = (x) => Math.round(k < 0 ? x * (1 + k) : x + (255 - x) * k);
    v = 'rgb(' + f(n >> 16) + ',' + f((n >> 8) & 255) + ',' + f(n & 255) + ')';
    darkCache.set(key, v);
    return v;
  }
  function alpha(hex, a) {
    const m = /^#?([0-9a-f]{6})$/i.exec(String(hex).trim());
    if (!m) return hex;
    const n = parseInt(m[1], 16);
    return 'rgba(' + (n >> 16) + ',' + ((n >> 8) & 255) + ',' + (n & 255) + ',' + a + ')';
  }

  // ---- спрайти: малюємо раз у світових одиницях, тримаємо в пікселях полотна ----
  function sprite(st, key, half, paint) {
    const k = st.sprK;
    let s = st.spr.get(key);
    if (s && s.k === k) return s;
    const px = Math.max(4, Math.ceil(half * 2 * k));
    const cv = document.createElement('canvas');
    cv.width = cv.height = px;
    const g = cv.getContext('2d');
    g.setTransform(k, 0, 0, k, half * k, half * k);
    paint(g);
    s = { cv, half, k };
    st.spr.set(key, s);
    return s;
  }
  function blit(g, s, x, y, scale, rot) {
    const h = s.half * (scale || 1);
    if (rot) {
      g.save();
      g.translate(x, y);
      g.rotate(rot);
      g.drawImage(s.cv, -h, -h, h * 2, h * 2);
      g.restore();
    } else g.drawImage(s.cv, x - h, y - h, h * 2, h * 2);
  }

  /// Груша: низ ширший, верх вужчий, хвостик і листок. Золота — з сяйвом.
  function pearPath(g, r) {
    g.beginPath();
    g.moveTo(0, -r * 1.05);
    g.bezierCurveTo(r * 0.55, -r * 1.05, r * 0.45, -r * 0.35, r * 0.78, r * 0.05);
    g.bezierCurveTo(r * 1.15, r * 0.55, r * 0.75, r * 1.05, 0, r * 1.05);
    g.bezierCurveTo(-r * 0.75, r * 1.05, -r * 1.15, r * 0.55, -r * 0.78, r * 0.05);
    g.bezierCurveTo(-r * 0.45, -r * 0.35, -r * 0.55, -r * 1.05, 0, -r * 1.05);
    g.closePath();
  }
  function pearSprite(st, gold) {
    const r = PEAR_R * 1.3;
    return sprite(st, gold ? 'pg' : 'pn', r * 2, (g) => {
      if (gold) {
        const glow = g.createRadialGradient(0, 0, r * 0.4, 0, 0, r * 1.9);
        glow.addColorStop(0, 'rgba(255,220,90,.55)');
        glow.addColorStop(1, 'rgba(255,220,90,0)');
        g.fillStyle = glow;
        g.beginPath(); g.arc(0, 0, r * 1.9, 0, Math.PI * 2); g.fill();
      }
      g.fillStyle = 'rgba(0,0,0,.22)';
      g.beginPath(); g.ellipse(r * 0.18, r * 1.05, r * 0.8, r * 0.28, 0, 0, Math.PI * 2); g.fill();
      const grad = g.createLinearGradient(-r, -r, r, r);
      if (gold) { grad.addColorStop(0, '#fff2a8'); grad.addColorStop(0.5, '#f6c53a'); grad.addColorStop(1, '#c98a12'); }
      else { grad.addColorStop(0, '#eef08a'); grad.addColorStop(0.55, '#c3cf45'); grad.addColorStop(1, '#8a9b25'); }
      pearPath(g, r);
      g.fillStyle = grad;
      g.fill();
      g.lineWidth = r * 0.12;
      g.strokeStyle = gold ? '#9a6608' : '#5f6d17';
      g.stroke();
      g.fillStyle = 'rgba(255,255,255,.45)';
      g.beginPath(); g.ellipse(-r * 0.32, r * 0.1, r * 0.16, r * 0.3, 0.3, 0, Math.PI * 2); g.fill();
      g.strokeStyle = '#6b4423';
      g.lineWidth = r * 0.14;
      g.lineCap = 'round';
      g.beginPath(); g.moveTo(0, -r * 1.0); g.quadraticCurveTo(r * 0.05, -r * 1.35, r * 0.22, -r * 1.5); g.stroke();
      g.fillStyle = '#4f9a3a';
      g.beginPath(); g.ellipse(r * 0.45, -r * 1.3, r * 0.34, r * 0.15, -0.5, 0, Math.PI * 2); g.fill();
      if (gold) {
        g.fillStyle = '#fff';
        g.beginPath(); g.arc(r * 0.35, -r * 0.2, r * 0.12, 0, Math.PI * 2); g.fill();
      }
    });
  }
  /// Крона груші: купка кружків, на ній кілька груш. Тінь — у тлі.
  function crownSprite(st) {
    return sprite(st, 'crown', 70, (g) => {
      const blobs = [[0, 0, 40], [-26, -14, 26], [24, -18, 27], [28, 14, 25], [-22, 20, 26], [2, 30, 22], [-4, -30, 22]];
      g.fillStyle = '#2c5e27';
      for (const [x, y, r] of blobs) { g.beginPath(); g.arc(x, y, r + 3, 0, Math.PI * 2); g.fill(); }
      g.fillStyle = '#3c7d33';
      for (const [x, y, r] of blobs) { g.beginPath(); g.arc(x - 2, y - 2, r, 0, Math.PI * 2); g.fill(); }
      g.fillStyle = '#58a046';
      for (const [x, y, r] of blobs) { g.beginPath(); g.arc(x - 6, y - 7, r * 0.55, 0, Math.PI * 2); g.fill(); }
      g.fillStyle = 'rgba(255,255,255,.08)';
      for (const [x, y, r] of blobs) { g.beginPath(); g.arc(x - 9, y - 10, r * 0.25, 0, Math.PI * 2); g.fill(); }
      const hang = [[-18, -4], [16, -24], [24, 20], [-10, 26], [6, 4], [-30, -24]];
      for (const [x, y] of hang) {
        g.fillStyle = '#d6dc5c';
        g.beginPath(); g.ellipse(x, y + 1, 5, 6.5, 0, 0, Math.PI * 2); g.fill();
        g.fillStyle = 'rgba(255,255,255,.4)';
        g.beginPath(); g.arc(x - 1.5, y - 1, 1.6, 0, Math.PI * 2); g.fill();
      }
    });
  }

  // ---- тло: трава, смуги покосу, квіти, тіні дерев, витоптана земля біля комор, тин ----
  function background(st, c) {
    const key = c.el.width + '|' + JSON.stringify(st.larders);
    if (st.bg && st.bgKey === key) return st.bg;
    const bg = document.createElement('canvas');
    bg.width = bg.height = c.el.width;
    const g = bg.getContext('2d');
    const k = c.el.width / W;
    g.setTransform(k, 0, 0, k, 0, 0);
    const grad = g.createRadialGradient(500, 500, 60, 500, 500, 760);
    grad.addColorStop(0, '#79b552');
    grad.addColorStop(1, '#4b8236');
    g.fillStyle = grad;
    g.fillRect(0, 0, W, W);
    // смуги покосу
    g.save();
    g.translate(500, 500);
    g.rotate(-Math.PI / 4);
    g.fillStyle = 'rgba(255,255,255,.035)';
    for (let x = -800; x < 800; x += 120) g.fillRect(x, -800, 60, 1600);
    g.restore();
    let seed = 12345;
    const rnd = () => ((seed = (seed * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff);
    // кущики трави
    for (let i = 0; i < 900; i++) {
      const x = rnd() * W, y = rnd() * W, l = 5 + rnd() * 8;
      g.strokeStyle = rnd() < 0.5 ? 'rgba(30,70,20,.35)' : 'rgba(170,220,120,.3)';
      g.lineWidth = 1.6;
      g.beginPath(); g.moveTo(x, y); g.lineTo(x + (rnd() - 0.5) * 5, y - l); g.stroke();
    }
    // витоптана земля біля комор
    st.larders.forEach((L) => {
      if (!L) return;
      const d = g.createRadialGradient(L[0], L[1], 10, L[0], L[1], LARDER_R + 12);
      d.addColorStop(0, 'rgba(150,110,60,.55)');
      d.addColorStop(1, 'rgba(150,110,60,0)');
      g.fillStyle = d;
      g.beginPath(); g.arc(L[0], L[1], LARDER_R + 12, 0, Math.PI * 2); g.fill();
    });
    // квіти
    const fl = ['#ffffff', '#fff3a0', '#d7b8ff', '#ffc0d8'];
    for (let i = 0; i < 70; i++) {
      const x = 30 + rnd() * 940, y = 30 + rnd() * 940;
      g.fillStyle = fl[i % fl.length];
      for (let j = 0; j < 5; j++) { const a = (j * Math.PI * 2) / 5; g.beginPath(); g.arc(x + Math.cos(a) * 3, y + Math.sin(a) * 3, 2.4, 0, Math.PI * 2); g.fill(); }
      g.fillStyle = '#f2b632';
      g.beginPath(); g.arc(x, y, 2, 0, Math.PI * 2); g.fill();
    }
    // тіні дерев і стовбури
    for (const [x, y] of st.trees) {
      g.fillStyle = 'rgba(20,45,15,.32)';
      g.beginPath(); g.ellipse(x + 14, y + 16, 74, 62, 0, 0, Math.PI * 2); g.fill();
      g.fillStyle = '#6b4a2b';
      g.beginPath(); g.arc(x, y, 12, 0, Math.PI * 2); g.fill();
    }
    // тин по краю
    g.strokeStyle = '#7a5631';
    g.lineWidth = 7;
    g.strokeRect(8, 8, W - 16, W - 16);
    g.strokeStyle = 'rgba(255,230,190,.25)';
    g.lineWidth = 2;
    g.strokeRect(5, 5, W - 10, W - 10);
    g.fillStyle = '#5e3f22';
    for (let t = 8; t <= W - 8; t += 62) {
      for (const [x, y] of [[t, 8], [t, W - 8], [8, t], [W - 8, t]]) { g.beginPath(); g.arc(x, y, 6, 0, Math.PI * 2); g.fill(); }
    }
    st.bg = bg;
    st.bgKey = key;
    return bg;
  }

  // ---- соки: частинки, «+3», летючі груші, кільця штовхана ----
  function spawn(st, kind, x, y, n, speed, life, size, color) {
    const many = reduced() ? 0.5 : 1;
    for (let i = 0; i < Math.round(n * many); i++) {
      if (st.parts.length > 220) st.parts.shift();
      const a = Math.random() * Math.PI * 2, v = speed * (0.4 + Math.random() * 0.8);
      st.parts.push({ kind, x, y, vx: Math.cos(a) * v, vy: Math.sin(a) * v, life: life * (0.7 + Math.random() * 0.6), age: 0,
        size: size * (0.7 + Math.random() * 0.6), color, rot: Math.random() * 6, vr: (Math.random() - 0.5) * 8 });
    }
  }
  function pop(st, x, y, text, color, size, dur) {
    if (st.pops.length > 24) st.pops.shift();
    st.pops.push({ x, y, text, color, size: size || 16, born: performance.now(), dur: dur || 900 });
  }
  function fly(st, x0, y0, x1, y1, seat, gold, delay, dur, h) {
    if (st.flyers.length > 40) st.flyers.shift();
    st.flyers.push({ x0, y0, x1, y1, seat, gold, born: performance.now() + (delay || 0), dur: dur || 300, h: h == null ? 60 : h });
  }

  /// Події кадру → ефекти. Стан гри з них не беремо: кадр, що загубився в пачці, губить і події (spec §4.3).
  function events(st, f) {
    const now = performance.now();
    const prev = st.last;
    const me = mySeat(st);
    const pal = palette(st);
    const spills = [];
    let fallTree = -1;
    const pos = (s) => {
      const q = (f.p && f.p[s]) || (prev && prev.p && prev.p[s]);
      return q ? [q[0], q[1]] : null;
    };
    const fresh = !prev || f.t !== prev.t;
    if (fresh) for (const e of f.ev || []) {
      const type = e[0], a = e[1], b = e[2];
      if (type === 1) {                                   // підібрав
        const pr = st.pearsPrev.get(b);
        const q = pos(a);
        if (pr && q) fly(st, pr[0], pr[1], q[0], q[1], a, pr[2], 0, 160, 18);
        if (pr && pr[2] && q) { spawn(st, 'spark', q[0], q[1] - 30, 10, 140, 500, 5, '#ffe27a'); pop(st, q[0], q[1] - 60, '✨', '#ffe27a', 18, 700); }
      } else if (type === 2) {                            // скинув у комору
        const L = st.larders[a], q = pos(a);
        const pq = prev && prev.p && prev.p[a];
        const n = Math.min(CARRY_MAX, pq ? pq[3] : 1) || 1, gold = pq ? pq[4] : 0;
        if (L && q) {
          for (let i = 0; i < n; i++) fly(st, q[0], q[1] - 34 - (i > 2 ? 14 : 0), L[0], L[1] - 20, -1, i < gold, i * 55, 260, 50);
          st.bounce[a] = now + n * 55 + 200;
          pop(st, L[0], L[1] - 76, '+' + b, pal.seats[a], a === me ? 26 : 20, 1200);
          setTimeout(() => spawn(st, 'spark', L[0], L[1] - 20, 8 + b * 2, 120, 600, 5, b >= 3 ? '#ffe27a' : '#fffbd0'), n * 55 + 220);
        }
      } else if (type === 3) {                            // вкрав одну
        const L = st.larders[b], q = pos(a);
        if (L && q) {
          fly(st, L[0], L[1] - 20, q[0], q[1], a, false, 0, 320, 70);
          pop(st, L[0], L[1] - 76, '−1', pal.bad, b === me ? 24 : 18, 900);
          st.bounce[b] = now;
        }
      } else if (type === 4) {                            // штовхнув і влучив
        const qa = pos(a), qb = pos(b);
        if (qb) {
          spawn(st, 'star', qb[0], qb[1], 12, 260, 450, 7, '#fff3b0');
          spawn(st, 'dust', qb[0], qb[1] + 10, 6, 60, 600, 14, 'rgba(120,90,50,.5)');
          pop(st, qb[0], qb[1] - 44, 'БАХ!', '#fff', b === me || a === me ? 22 : 17, 700);
          st.hit[b] = now;
        }
        if (qa) st.rings.push({ x: qa[0], y: qa[1], born: now, dur: 260, color: pal.seats[a] || '#fff' });
        if (b === me) { st.shake = now; st.shakeAmp = 9; } else if (a === me) { st.shake = now; st.shakeAmp = 3; }
      } else if (type === 5) {                            // висипалось
        const pq = prev && prev.p && prev.p[a];
        const q = pq ? [pq[0], pq[1]] : pos(a);
        if (q) {
          spills.push(q);
          if (b > 0) pop(st, q[0], q[1] - 70, '−' + b + ' 🍐', a === me ? pal.bad : '#fff', a === me ? 20 : 16, 1000);
        }
      } else if (type === 6) {                            // штовхнув повітря
        const q = pos(a);
        if (q) {
          st.rings.push({ x: q[0], y: q[1], born: now, dur: 260, color: 'rgba(255,255,255,.7)' });
          spawn(st, 'dust', q[0], q[1], 5, 80, 400, 10, 'rgba(230,230,210,.45)');
        }
      } else if (type === 7) {                            // дерево трусить
        const t = st.trees[a];
        if (t) spawn(st, 'leaf', t[0], t[1], 8, 90, 900, 9, '#4f9a3a');
      } else if (type === 8) {                            // хвиля впала
        fallTree = a;
        const t = st.trees[a];
        if (t) spawn(st, 'leaf', t[0], t[1], 14, 160, 1100, 9, '#5aa548');
      }
    }
    if (fallTree >= 0) st.lastFall = { tree: fallTree, at: now };
    // груші: нові — летять (з дерева чи з ноші), зниклі — забуваємо
    const cur = new Map();
    for (const q of f.g || []) cur.set(q[0], [q[1], q[2], q[3], q[4]]);
    if (prev && fresh) {
      for (const [id, q] of cur) {
        if (st.pearsPrev.has(id) || st.pearAnim.has(id)) continue;
        let from = null, dur = 220, kind = 'pop';
        if (q[3] > 0 && spills.length) {
          let best = Infinity;
          for (const s of spills) { const d = Math.hypot(s[0] - q[0], s[1] - q[1]); if (d < best) { best = d; from = s; } }
          kind = 'spill'; dur = 330;
        } else if (st.lastFall && now - st.lastFall.at < 400) {
          const t = st.trees[st.lastFall.tree];
          if (t && Math.hypot(t[0] - q[0], t[1] - q[1]) < 190) { from = [t[0] + (Math.random() - 0.5) * 40, t[1] - 20]; kind = 'fall'; dur = 420 + Math.random() * 200; }
        }
        st.pearAnim.set(id, { from, born: now, dur, kind });
      }
    }
    for (const id of st.pearAnim.keys()) if (!cur.has(id)) st.pearAnim.delete(id);
    st.pearsPrev = cur;
  }

  // ---- змішування кадрів ----
  function blend(st) {
    const at = st.interp.at();
    const b = (at && at.b) || st.last;
    if (!b) return null;
    const a = at && at.a;
    const t = at ? clamp(at.t, 0, 1) : 1;
    const ok = a && a !== b && a.p && b.p && a.ph === b.ph;
    for (let i = 0; i < 8; i++) {
      const qb = b.p && b.p[i];
      if (!qb) { st.on[i] = 0; continue; }
      st.on[i] = 1;
      let x = qb[0], y = qb[1];
      const qa = ok ? a.p[i] : null;
      if (qa && Math.abs(qa[0] - x) + Math.abs(qa[1] - y) < 200) { x = qa[0] + (x - qa[0]) * t; y = qa[1] + (y - qa[1]) * t; }
      st.bx[i] = x;
      st.by[i] = y;
    }
    return b;
  }

  // ---- малювання ----
  function text(g, st, t, x, y, size, color, weight, align, stroke) {
    g.font = (weight || 800) + ' ' + (size * st.lk) / SC + 'px ' + st.pal.font;
    g.textAlign = align || 'center';
    g.textBaseline = 'middle';
    if (stroke !== false) {
      g.lineJoin = 'round';
      g.lineWidth = (3.2 * st.lk) / SC;
      g.strokeStyle = 'rgba(20,25,15,.75)';
      g.strokeText(t, x, y);
    }
    g.fillStyle = color;
    g.fillText(t, x, y);
  }
  function roundRect(g, x, y, w, h, r) {
    g.beginPath();
    g.moveTo(x + r, y);
    g.arcTo(x + w, y, x + w, y + h, r);
    g.arcTo(x + w, y + h, x, y + h, r);
    g.arcTo(x, y + h, x, y, r);
    g.arcTo(x, y, x + w, y, r);
    g.closePath();
  }

  function drawTrees(st, g, f, now) {
    const crown = crownSprite(st);
    const sh = f && f.sh;
    for (let i = 0; i < st.trees.length; i++) {
      const [x, y] = st.trees[i];
      let dx = 0, rot = 0;
      if (sh && sh[0] === i && !reduced()) {
        const k = 1 - clamp(sh[1] / SHAKE_TICKS, 0, 1);
        const amp = 3 + 7 * k;
        dx = Math.sin(now / 38) * amp;
        rot = Math.sin(now / 52) * 0.05 * (0.4 + k);
        if (Math.random() < 0.12) spawn(st, 'leaf', x + (Math.random() - 0.5) * 80, y + (Math.random() - 0.5) * 80, 1, 40, 900, 8, '#4f9a3a');
      } else dx = reduced() ? 0 : Math.sin(now / 1400 + i * 1.7) * 1.2;
      blit(g, crown, x + dx, y, 1, rot);
    }
  }
  /// Дерево, що трусить: кільце, куди посиплеться (40–140 од), блимає жовтим — видно заздалегідь.
  function drawShakeZone(st, g, f, now) {
    const sh = f && f.sh;
    if (!sh || !st.trees[sh[0]]) return;
    const [x, y] = st.trees[sh[0]];
    const k = 1 - clamp(sh[1] / SHAKE_TICKS, 0, 1);
    const pulse = reduced() ? 0.6 : 0.5 + 0.5 * Math.sin(now / 90);
    g.save();
    g.fillStyle = 'rgba(255,236,120,' + (0.08 + 0.12 * k) + ')';
    g.beginPath(); g.arc(x, y, 146, 0, Math.PI * 2); g.arc(x, y, 38, 0, Math.PI * 2, true); g.fill();
    g.strokeStyle = 'rgba(255,240,150,' + (0.35 + 0.45 * pulse) + ')';
    g.lineWidth = 4;
    g.setLineDash([14, 12]);
    g.lineDashOffset = -now / 40;
    g.beginPath(); g.arc(x, y, 146, 0, Math.PI * 2); g.stroke();
    g.setLineDash([]);
    g.restore();
  }

  function drawLarders(st, g, f, now, me) {
    const pal = st.pal;
    const counts = (f && f.l) || [];
    for (let s = 0; s < 8; s++) {
      const L = st.larders[s];
      if (!L) continue;
      const col = pal.seats[s];
      const mine = s === me;
      const q = f && f.p && f.p[s];
      const robbed = !!(q && q[5] & 16);
      const [x, y] = L;
      g.save();
      g.fillStyle = alpha(col, mine ? 0.16 : 0.1);
      g.beginPath(); g.arc(x, y, LARDER_R, 0, Math.PI * 2); g.fill();
      g.strokeStyle = alpha(col, mine ? 0.9 : 0.55);
      g.lineWidth = mine ? 5 : 3;
      g.setLineDash([12, 10]);
      if (mine && !reduced()) g.lineDashOffset = -now / 70;
      g.stroke();
      g.setLineDash([]);
      if (robbed) {
        const p = reduced() ? 0.7 : 0.5 + 0.5 * Math.sin(now / 80);
        g.strokeStyle = 'rgba(255,70,55,' + (0.45 + 0.5 * p) + ')';
        g.lineWidth = 8;
        g.beginPath(); g.arc(x, y, LARDER_R + 2, 0, Math.PI * 2); g.stroke();
      }
      // хатка
      const bt = (now - st.bounce[s]) / 320;
      const kb = bt > 0 && bt < 1 ? 1 + 0.2 * Math.sin(bt * Math.PI) * (1 - bt) : 1;
      const jig = robbed && !reduced() ? Math.sin(now / 28) * 2.5 : 0;
      g.translate(x + jig, y);
      g.scale(kb, kb);
      g.fillStyle = 'rgba(0,0,0,.25)';
      g.beginPath(); g.ellipse(4, 26, 38, 9, 0, 0, Math.PI * 2); g.fill();
      g.fillStyle = tint(col, 0.35);
      g.strokeStyle = tint(col, -0.45);
      g.lineWidth = 3;
      g.beginPath(); g.rect(-26, -6, 52, 30); g.fill(); g.stroke();
      g.fillStyle = '#3b2716';
      roundRect(g, -8, 4, 16, 20, 5); g.fill();
      g.fillStyle = '#ffe9a8';
      g.fillRect(12, 2, 9, 9);
      g.fillStyle = '#b5763f';
      g.strokeStyle = '#6e421f';
      g.beginPath(); g.moveTo(-36, -2); g.lineTo(0, -36); g.lineTo(36, -2); g.closePath(); g.fill(); g.stroke();
      g.strokeStyle = 'rgba(110,66,31,.6)';
      g.lineWidth = 1.6;
      for (let i = -2; i <= 2; i++) { g.beginPath(); g.moveTo(i * 6, -30 + Math.abs(i) * 5); g.lineTo(i * 13, -4); g.stroke(); }
      g.restore();
      // число над дахом
      const n = counts[s] != null ? counts[s] : 0;
      const label = String(n);
      g.font = '800 ' + (14 * st.lk) / SC + 'px ' + pal.font;
      const tw = g.measureText(label).width;
      const ph = (22 * st.lk) / SC, pw = tw + ph * 1.25;
      const py = y - 50 - ph / 2 - 8 < 4 ? 4 + ph / 2 : y - 50 - ph / 2;
      g.fillStyle = robbed ? 'rgba(170,30,25,.92)' : 'rgba(25,30,20,.82)';
      roundRect(g, x - pw / 2, py - ph / 2, pw, ph, ph / 2);
      g.fill();
      g.strokeStyle = mine ? pal.accent : col;
      g.lineWidth = (mine ? 2.6 : 1.8) / SC;
      g.stroke();
      blit(g, pearSprite(st, false), x - pw / 2 + ph * 0.5, py, (ph * 0.75) / (PEAR_R * 2.6));
      text(g, st, label, x + ph * 0.22, py + 0.5, 14, '#fff', 800, 'center', false);
      // підпис під хаткою
      const who = mine ? 'твоя' : shortName(st.ctx, s, 11);
      text(g, st, who, x, y + 44, mine ? 12 : 10.5, mine ? pal.accent : tint(col, 0.25), mine ? 800 : 700);
    }
  }

  function drawPears(st, g, f, now) {
    const list = f && f.g;
    if (!list) return;
    const n = pearSprite(st, false), gs = pearSprite(st, true);
    for (const q of list) {
      const id = q[0], gold = q[3] === 1;
      let x = q[1], y = q[2], sc = 1, rot = Math.sin(id * 2.3) * 0.5;
      const an = st.pearAnim.get(id);
      if (an) {
        const t = clamp((now - an.born) / an.dur, 0, 1);
        if (an.from && t < 1) {
          const e = 1 - (1 - t) * (1 - t);
          const h = an.kind === 'fall' ? 0 : 70;
          x = an.from[0] + (q[1] - an.from[0]) * e;
          y = an.from[1] + (q[2] - an.from[1]) * e - h * 4 * t * (1 - t);
          rot += (1 - t) * 6;
          if (an.kind === 'fall') sc = 1.35 - 0.35 * t;
        } else if (!an.from && t < 1) sc = 0.4 + 0.6 * t + Math.sin(t * Math.PI) * 0.25;
        else if (an.kind === 'fall' && now - an.born < an.dur + 160) {
          const b = (now - an.born - an.dur) / 160;
          y -= Math.sin(b * Math.PI) * 8;
        }
      }
      if (q[4] > 0 && !an) sc = 1.1;
      if (gold && !reduced()) sc *= 1 + 0.06 * Math.sin(now / 160 + id);
      blit(g, gold ? gs : n, x, y, sc, rot);
    }
  }

  /// Стос груш над головою: знизу три, згори дві.
  const STACK = [[-13, 0], [13, 0], [0, 0], [-7, -15], [7, -15]];
  const STACK_ORDER = [[2], [0, 1], [0, 1, 2], [0, 1, 2, 3], [0, 1, 2, 3, 4]];
  function drawStack(st, g, x, y, carry, gold, now) {
    if (carry <= 0) return;
    const ns = pearSprite(st, false), gs = pearSprite(st, true);
    const idx = STACK_ORDER[Math.min(CARRY_MAX, carry) - 1];
    for (let i = 0; i < idx.length; i++) {
      const o = STACK[idx[i]];
      const wob = reduced() ? 0 : Math.sin(now / 160 + i) * 0.12;
      blit(g, i < gold ? gs : ns, x + o[0], y + o[1], 0.62, wob);
    }
  }

  function drawBodies(st, g, f, now, me) {
    const pal = st.pal;
    const order = [];
    for (let s = 0; s < 8; s++) if (st.on[s] && f.p[s]) order.push(s);
    order.sort((a, b) => st.by[a] - st.by[b]);
    const myQ = me >= 0 && f.p ? f.p[me] : null;
    // мій штовхан готовий і хтось у досяжності — пунктир досяжності й приціл на найближчому
    if (myQ && f.ph === 1 && myQ[6] === 0 && !(myQ[5] & 2)) {
      let best = -1, bd = PUSH_R + 1;
      for (const s of order) {
        if (s === me || (f.p[s][5] & 4)) continue;
        const d = Math.hypot(st.bx[s] - st.bx[me], st.by[s] - st.by[me]);
        if (d <= bd) { bd = d; best = s; }
      }
      if (best >= 0) {
        g.save();
        g.strokeStyle = 'rgba(255,255,255,.45)';
        g.lineWidth = 2.5;
        g.setLineDash([8, 8]);
        g.beginPath(); g.arc(st.bx[me], st.by[me], PUSH_R, 0, Math.PI * 2); g.stroke();
        g.setLineDash([]);
        const tx = st.bx[best], ty = st.by[best], r = BODY_R + 9 + (reduced() ? 0 : Math.sin(now / 90) * 2);
        g.strokeStyle = 'rgba(255,90,70,.9)';
        g.lineWidth = 3.5;
        for (let k = 0; k < 4; k++) { const a = k * Math.PI / 2 + now / 600; g.beginPath(); g.arc(tx, ty, r, a - 0.35, a + 0.35); g.stroke(); }
        g.restore();
      }
    }
    for (const s of order) {
      const q = f.p[s];
      const x = st.bx[s], y = st.by[s], face = q[2], carry = q[3], gold = q[4], fl = q[5];
      const col = pal.seats[s];
      const mine = s === me;
      const walk = fl & 1 && !(fl & 2) && !reduced() ? Math.abs(Math.sin(now / 75 + s)) * 4 : 0;
      g.save();
      if (fl & 4) g.globalAlpha = 0.45 + 0.35 * (Math.sin(now / 55) > 0 ? 1 : 0);
      g.fillStyle = 'rgba(0,0,0,.28)';
      g.beginPath(); g.ellipse(x + 3, y + BODY_R * 0.75, BODY_R * 0.9, BODY_R * 0.32, 0, 0, Math.PI * 2); g.fill();
      if (mine) {
        g.strokeStyle = pal.accent;
        g.lineWidth = 3.5;
        g.beginPath(); g.ellipse(x, y + BODY_R * 0.7, BODY_R * 1.25, BODY_R * 0.5, 0, 0, Math.PI * 2); g.stroke();
      }
      const cy = y - walk;
      // крадіжка: червона дуга прогресу
      if (fl & 8) {
        g.strokeStyle = 'rgba(0,0,0,.35)';
        g.lineWidth = 7;
        g.beginPath(); g.arc(x, cy, BODY_R + 8, 0, Math.PI * 2); g.stroke();
        g.strokeStyle = '#ff6b5a';
        g.beginPath(); g.arc(x, cy, BODY_R + 8, -Math.PI / 2, -Math.PI / 2 + (Math.PI * 2 * (q[7] + 1)) / STEAL_TICKS); g.stroke();
      }
      // перезарядка мого штовхана — тонка дуга
      if (mine && q[6] > 0) {
        g.strokeStyle = 'rgba(255,255,255,.55)';
        g.lineWidth = 3;
        g.beginPath(); g.arc(x, cy, BODY_R + 4, -Math.PI / 2, -Math.PI / 2 + Math.PI * 2 * (1 - q[6] / PUSH_CD)); g.stroke();
      }
      const hit = clamp(1 - (now - st.hit[s]) / 220, 0, 1);
      const R = BODY_R * (1 + 0.15 * hit);
      const gr = g.createRadialGradient(x - R * 0.35, cy - R * 0.4, R * 0.15, x, cy, R);
      gr.addColorStop(0, tint(col, 0.45));
      gr.addColorStop(1, col);
      g.fillStyle = hit > 0 ? '#fff' : gr;
      g.beginPath(); g.arc(x, cy, R, 0, Math.PI * 2); g.fill();
      g.lineWidth = 3;
      g.strokeStyle = tint(col, -0.5);
      g.stroke();
      // очі дивляться, куди йде
      const fx = COS16[face] || 0, fy = SIN16[face] || 0;
      const px = -fy, py = fx;
      for (const sgn of [-1, 1]) {
        const ex = x + fx * R * 0.38 + px * R * 0.32 * sgn, ey = cy + fy * R * 0.38 + py * R * 0.32 * sgn - R * 0.08;
        g.fillStyle = '#fff';
        g.beginPath(); g.arc(ex, ey, R * 0.2, 0, Math.PI * 2); g.fill();
        g.fillStyle = '#1b1b1b';
        if (fl & 2) { g.font = '900 ' + R * 0.36 + 'px sans-serif'; g.textAlign = 'center'; g.textBaseline = 'middle'; g.fillText('×', ex, ey); }
        else { g.beginPath(); g.arc(ex + fx * R * 0.08, ey + fy * R * 0.08, R * 0.1, 0, Math.PI * 2); g.fill(); }
      }
      // руки вперед, коли щойно штовхнув
      if (mine && now - st.pushAt < 180) {
        g.strokeStyle = tint(col, -0.3);
        g.lineWidth = 7;
        g.lineCap = 'round';
        g.beginPath(); g.moveTo(x + fx * R * 0.7, cy + fy * R * 0.7); g.lineTo(x + fx * (R + 22), cy + fy * (R + 22)); g.stroke();
      }
      drawStack(st, g, x, cy - R - 12, carry, gold, now);
      if (fl & 2) {   // приголомшений: зірочки над головою
        for (let k = 0; k < 3; k++) {
          const a = now / 160 + (k * Math.PI * 2) / 3;
          text(g, st, '✦', x + Math.cos(a) * R * 0.9, cy - R - 4 + Math.sin(a) * R * 0.35, 10, '#ffe27a', 900, 'center', false);
        }
      }
      g.restore();
    }
    // імена — поверх усіх тіл
    for (const s of order) {
      const mine = s === me;
      const nm = mine ? 'ти' : shortName(st.ctx, s, 11);
      text(g, st, nm, st.bx[s], st.by[s] + BODY_R + 15, mine ? 12 : 10.5, mine ? pal.accent : '#fff', 800);
    }
  }

  function drawFx(st, g, now, dt) {
    const sec = dt / 1000;
    // кільця штовхана
    for (let i = st.rings.length - 1; i >= 0; i--) {
      const r = st.rings[i], t = (now - r.born) / r.dur;
      if (t >= 1) { st.rings.splice(i, 1); continue; }
      g.strokeStyle = r.color;
      g.globalAlpha = 1 - t;
      g.lineWidth = 5 * (1 - t) + 1;
      g.beginPath(); g.arc(r.x, r.y, BODY_R + (PUSH_R - BODY_R) * t + 10, 0, Math.PI * 2); g.stroke();
      g.globalAlpha = 1;
    }
    // летючі груші
    const ns = pearSprite(st, false), gs = pearSprite(st, true);
    for (let i = st.flyers.length - 1; i >= 0; i--) {
      const fl = st.flyers[i], t = (now - fl.born) / fl.dur;
      if (t < 0) continue;
      if (t >= 1) { st.flyers.splice(i, 1); continue; }
      let x1 = fl.x1, y1 = fl.y1;
      if (fl.seat >= 0 && st.on[fl.seat]) { x1 = st.bx[fl.seat]; y1 = st.by[fl.seat] - BODY_R - 12; }
      const e = t * (2 - t);
      blit(g, fl.gold ? gs : ns, fl.x0 + (x1 - fl.x0) * e, fl.y0 + (y1 - fl.y0) * e - fl.h * 4 * t * (1 - t), 0.75, t * 5);
    }
    // частинки
    for (let i = st.parts.length - 1; i >= 0; i--) {
      const p = st.parts[i];
      p.age += dt;
      if (p.age >= p.life) { st.parts.splice(i, 1); continue; }
      const k = 1 - p.age / p.life;
      p.x += p.vx * sec;
      p.y += p.vy * sec;
      p.rot += p.vr * sec;
      if (p.kind === 'leaf') { p.vy += 60 * sec; p.vx *= 1 - 1.5 * sec; }
      else { p.vx *= 1 - 3 * sec; p.vy *= 1 - 3 * sec; }
      g.globalAlpha = Math.min(1, k * 1.5);
      g.fillStyle = p.color;
      if (p.kind === 'leaf') {
        g.save(); g.translate(p.x, p.y); g.rotate(p.rot);
        g.beginPath(); g.ellipse(0, 0, p.size, p.size * 0.45, 0, 0, Math.PI * 2); g.fill();
        g.restore();
      } else if (p.kind === 'star') {
        g.save(); g.translate(p.x, p.y); g.rotate(p.rot);
        const r = p.size * k + 1;
        g.beginPath();
        for (let j = 0; j < 8; j++) { const a = (j * Math.PI) / 4, rr = j % 2 ? r * 0.4 : r; g.lineTo(Math.cos(a) * rr, Math.sin(a) * rr); }
        g.closePath(); g.fill();
        g.restore();
      } else if (p.kind === 'dust') {
        g.beginPath(); g.arc(p.x, p.y, p.size * (1.6 - k * 0.6), 0, Math.PI * 2); g.fill();
      } else {
        g.beginPath(); g.arc(p.x, p.y, p.size * k + 0.5, 0, Math.PI * 2); g.fill();
      }
      g.globalAlpha = 1;
    }
    // «+3», «−1», «БАХ!»
    for (let i = st.pops.length - 1; i >= 0; i--) {
      const p = st.pops[i], t = (now - p.born) / p.dur;
      if (t >= 1) { st.pops.splice(i, 1); continue; }
      if (t < 0) continue;
      const s = t < 0.15 ? 0.6 + (t / 0.15) * 0.5 : 1.1 - Math.min(0.1, (t - 0.15));
      g.globalAlpha = t > 0.7 ? (1 - t) / 0.3 : 1;
      text(g, st, p.text, p.x, Math.max(20, p.y - t * 40), p.size * s, p.color, 900);
      g.globalAlpha = 1;
    }
  }

  function shade(g, a) {
    g.fillStyle = 'rgba(10,20,8,' + a + ')';
    g.fillRect(0, 0, W, W);
  }

  function overlays(st, g, f, ph, now, me) {
    const ctx = st.ctx, v = ctx.view || {};
    const pal = st.pal;
    if (ph === 4 || (v.phase === 'lobby')) {
      shade(g, 0.35);
      text(g, st, '🍐 Крадії груш', 500, 410, 30, '#fff', 900);
      text(g, st, 'Носи груші у свою комору · кради з чужих', 500, 480, 13, '#f3f7e8', 700);
      text(g, st, 'штовхан висипає чужу ношу', 500, 525, 13, '#f3f7e8', 700);
      return;
    }
    if (ph === 0 && f) {
      const secs = Math.max(1, Math.ceil((f.left * TICK_MS) / 1000));
      const frac = ((f.left * TICK_MS) % 1000) / 1000;
      shade(g, 0.28);
      const k = reduced() ? 1 : 1 + 0.35 * frac * frac;
      text(g, st, String(secs), 500, 470, 64 * k, '#fff', 900);
      text(g, st, me >= 0 ? 'Неси груші у свою комору!' : 'Зараз почнеться', 500, 570, 15, '#fffbe0', 800);
      if (me >= 0) pointMine(st, g, now);
      return;
    }
    if (ph === 1 && f) {
      const since = now - st.goAt;
      if (since < 900 && st.goAt) {
        g.globalAlpha = 1 - since / 900;
        text(g, st, 'Гайда! 🍐', 500, 480, 40 * (1 + since / 1800), '#fff', 900);
        g.globalAlpha = 1;
      }
      if (me >= 0 && since < 3500) pointMine(st, g, now);
      const secs = Math.ceil((f.left * TICK_MS) / 1000);
      if (secs <= 5 && secs > 0) {
        g.globalAlpha = 0.55;
        text(g, st, String(secs), 500, 500, 90, '#fff', 900);
        g.globalAlpha = 1;
      }
      return;
    }
    if (ph === 3 || v.phase === 'over') results(st, g, v, now, me, pal);
  }
  /// Де моя комора: стрілка від мене і пульс на хатці.
  function pointMine(st, g, now) {
    const me = mySeat(st), L = st.larders[me];
    if (!L) return;
    const p = reduced() ? 0.5 : 0.5 + 0.5 * Math.sin(now / 140);
    g.strokeStyle = alpha(st.pal.accent.startsWith('#') ? st.pal.accent : '#f4a340', 0.5 + 0.4 * p);
    g.lineWidth = 6;
    g.beginPath(); g.arc(L[0], L[1], LARDER_R + 10 + p * 8, 0, Math.PI * 2); g.stroke();
    if (st.on[me]) {
      const x = st.bx[me], y = st.by[me], a = Math.atan2(L[1] - y, L[0] - x), d = Math.hypot(L[0] - x, L[1] - y);
      if (d > LARDER_R + 30) {
        const r0 = BODY_R + 26, r1 = Math.min(d - LARDER_R - 6, r0 + 60);
        g.save();
        g.translate(x, y); g.rotate(a);
        g.strokeStyle = st.pal.accent; g.fillStyle = st.pal.accent; g.lineWidth = 6; g.lineCap = 'round';
        g.beginPath(); g.moveTo(r0, 0); g.lineTo(r1, 0); g.stroke();
        g.beginPath(); g.moveTo(r1 + 14, 0); g.lineTo(r1 - 4, -11); g.lineTo(r1 - 4, 11); g.closePath(); g.fill();
        g.restore();
      }
    }
  }
  function results(st, g, v, now, me, pal) {
    const stats = v.stats || [];
    const rows = [];
    for (let s = 0; s < 8; s++) if (stats[s]) rows.push(s);
    if (!rows.length) return;
    rows.sort((a, b) => stats[b][0] - stats[a][0] || a - b);
    const fade = clamp((now - st.phAt - 500) / 400, 0, 1);
    if (fade <= 0) return;
    g.globalAlpha = fade;
    shade(g, 0.55);
    const ws = v.winners || [];
    const head = ws.length ? '🏆 ' + ws.map((s) => (s === me ? 'ти' : shortName(st.ctx, s, 12))).join(', ') : 'Нічия — ні груші';
    text(g, st, head, 500, 150, 20, '#ffe27a', 900);
    const lh = rows.length > 6 ? 72 : 84;
    const top = 500 - ((rows.length - 1) * lh) / 2 + 30;
    let place = 0, prevN = null;
    rows.forEach((s, i) => {
      const n = stats[s][0];
      if (n !== prevN) { place = i + 1; prevN = n; }
      const y = top + i * lh;
      g.fillStyle = s === me ? 'rgba(255,255,255,.16)' : 'rgba(255,255,255,.07)';
      roundRect(g, 150, y - lh * 0.4, 700, lh * 0.8, 18); g.fill();
      text(g, st, place + '.', 200, y, 14, '#fff', 800);
      g.fillStyle = pal.seats[s];
      g.beginPath(); g.arc(250, y, 16, 0, Math.PI * 2); g.fill();
      text(g, st, s === me ? 'ти' : shortName(st.ctx, s, 14), 285, y, 13, '#fff', 800, 'left');
      text(g, st, '🍐 ' + n, 700, y, 15, '#fffbe0', 900, 'right');
      text(g, st, stats[s][1] ? '🏹 ' + stats[s][1] : '', 820, y, 11, '#ffc2b8', 700, 'right');
    });
    g.globalAlpha = 1;
  }

  function draw(st, now) {
    const c = st.cv;
    const g = c.ctx;
    const pal = palette(st);
    const dt = st.lastDraw ? Math.min(100, now - st.lastDraw) : 16;
    st.lastDraw = now;
    const pxk = c.el.width / W;
    if (pxk !== st.sprK) { st.sprK = pxk; st.spr.clear(); }
    if (now - st.lkAt > 500) {
      st.lkAt = now;
      const cw = c.el.clientWidth || SIZE;
      st.lk = clamp(SIZE / Math.max(200, cw), 1, 1.7);
    }
    const f = blend(st);
    const ctx = st.ctx;
    const v = ctx.view || {};
    const ph = v.phase === 'lobby' ? 4 : f ? f.ph : 4;
    const me = mySeat(st);
    g.setTransform(1, 0, 0, 1, 0, 0);
    g.drawImage(background(st, c), 0, 0);
    g.setTransform(pxk, 0, 0, pxk, 0, 0);
    const sk = now - st.shake;
    if (sk < 260 && !reduced()) {
      const a = st.shakeAmp * (1 - sk / 260);
      g.translate((Math.random() - 0.5) * a * 2, (Math.random() - 0.5) * a * 2);
    }
    drawShakeZone(st, g, f, now);
    drawLarders(st, g, f, now, me);
    drawTrees(st, g, f, now);
    drawPears(st, g, f, now);
    if (f && f.p) drawBodies(st, g, f, now, me);
    drawFx(st, g, now, dt);
    overlays(st, g, f, ph, now, me);
  }

  // ---- ввід ----
  /// Аналоговий стік пада — першим: HPad з того самого стіка шле ще й стрілки, а ті знають лише 4 напрямки.
  function currentWant(st) {
    if (st.padA != null) return st.padA;
    const k = keySector();
    if (k >= 0) return k;
    if (st.stickA != null) return st.stickA;
    if (st.ptrA != null) return st.ptrA;
    return -1;
  }
  function canSend(st) {
    const ctx = st.ctx;
    const f = st.last;
    return !!(ctx && ctx.mine && ctx.playing && f && (f.ph === 0 || f.ph === 1));
  }
  function send(st, force, keep) {
    if (!canSend(st)) return;
    const a = currentWant(st);
    const now = performance.now();
    if (!force && a === st.sent) return;
    if (!force && now - st.sentAt < SEND_MS) return;   // цикл дошле, щойно мине SEND_MS
    if (keep && a < 0) return;
    st.sent = a;
    st.sentAt = now;
    st.ctx.input('move', { a });
  }
  function shove(st) {
    if (!canSend(st)) return;
    const f = st.last, me = mySeat(st);
    const q = f && f.p && f.p[me];
    if (!q || f.ph !== 1) return;
    if (q[6] > 0 || q[5] & 2) { st.btnNo = performance.now(); return; }   // перезарядку показує кнопка, тост ні до чого
    st.pushAt = performance.now();
    st.ctx.input('push');
    spin(st);
  }
  function ptrSector(st) {
    const me = mySeat(st);
    if (me < 0 || !st.on[me] || !st.cv) return null;
    const r = st.cv.el.getBoundingClientRect();
    if (!r.width) return null;
    const wx = ((st.ptrX - r.left) / r.width) * W, wy = ((st.ptrY - r.top) / r.height) * W;
    const dx = wx - st.bx[me], dy = wy - st.by[me];
    return dx * dx + dy * dy < 24 * 24 ? -1 : sectorOf(dx, dy);
  }
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
    if (a !== st.padA) { st.padA = a; send(st); }
  }

  function wireCanvas(st) {
    const el = st.cv.el;
    if (el._grWired) return;
    el._grWired = true;
    el.addEventListener('contextmenu', (e) => { if (st.ctx.mine) e.preventDefault(); });
    el.addEventListener('pointerdown', (e) => {
      if (!st.ctx.mine || !st.ctx.playing) return;
      if (e.button === 2) { e.preventDefault(); shove(st); return; }
      if (e.button !== 0) return;
      e.preventDefault();
      st.ptrDown = true;
      st.ptrX = e.clientX;
      st.ptrY = e.clientY;
      try { el.setPointerCapture(e.pointerId); } catch { /* без capture */ }
      st.ptrA = ptrSector(st);
      send(st);
    });
    el.addEventListener('pointermove', (e) => {
      if (!st.ptrDown) return;
      st.ptrX = e.clientX;
      st.ptrY = e.clientY;
    });
    const up = () => {
      if (!st.ptrDown) return;
      st.ptrDown = false;
      st.ptrA = null;
      send(st);
    };
    el.addEventListener('pointerup', up);
    el.addEventListener('pointercancel', up);
  }

  function controls(root, st) {
    const ctx = st.ctx;
    let el = st.ctl;
    const want = ctx.mine && HGames.ui.coarse() && ctx.view && ctx.view.phase !== 'lobby';
    if (!want) {
      if (el) { el.remove(); st.ctl = null; st.stickA = null; send(st); }
      st.wrap.classList.remove('hasctl');
      return;
    }
    if (!el) {
      el = st.ctl = document.createElement('div');
      el.className = 'grctl';
      el.innerHTML = '<div class="grstick" aria-label="стік: куди йти"><div class="grknob"></div></div>'
        + '<button type="button" class="grbtn" aria-label="штовхан"><span>👊</span></button>';
      const stick = el.querySelector('.grstick'), knob = el.querySelector('.grknob');
      let pid = null;
      const move = (e) => {
        const r = stick.getBoundingClientRect();
        const dx = e.clientX - (r.left + r.width / 2), dy = e.clientY - (r.top + r.height / 2);
        const m = Math.hypot(dx, dy), lim = r.width / 2 - 22;
        const k = m > lim ? lim / m : 1;
        knob.style.transform = 'translate(' + (dx * k).toFixed(1) + 'px,' + (dy * k).toFixed(1) + 'px)';
        const a = m < 12 ? null : sectorOf(dx, dy);
        if (a !== st.stickA) { st.stickA = a; send(st); }
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
        st.stickA = null;
        send(st);
      };
      stick.addEventListener('pointerup', up);
      stick.addEventListener('pointercancel', up);
      el.querySelector('.grbtn').addEventListener('pointerdown', (e) => { e.preventDefault(); shove(st); });
      st.stage.after(el);
      st.wrap.classList.add('hasctl');
    }
    const me = mySeat(st);
    const q = st.last && st.last.p && me >= 0 ? st.last.p[me] : null;
    const cd = q ? q[6] / PUSH_CD : 0;
    const btn = el.querySelector('.grbtn');
    const cds = cd.toFixed(2);
    if (btn._cd !== cds) { btn._cd = cds; btn.style.setProperty('--cd', cds); btn.classList.toggle('ready', cd === 0 && !(q && q[5] & 2)); }
    const kn = el.querySelector('.grknob');
    if (me >= 0 && kn._c !== me) { kn._c = me; kn.style.background = 'var(' + SEAT_VARS[me][0] + ', ' + SEAT_VARS[me][1] + ')'; }
  }

  // ---- рядок над садом і табличка комор ----
  function hud(st) {
    const ctx = st.ctx, v = ctx.view || {}, f = st.last;
    const me = mySeat(st);
    const lobby = v.phase === 'lobby' || !f;
    let top = '';
    if (!lobby) {
      const left = f.ph === 1 ? f.left : f.ph === 0 ? (v.len || 0) * 25 : 0;
      const low = f.ph === 1 && f.left * TICK_MS <= 10000;
      top += '<span class="grchip grtime' + (low ? ' low' : '') + '">⏱ <b>' + clock(left) + '</b></span>';
      if (me >= 0 && f.p && f.p[me] && f.ph <= 1) {
        const q = f.p[me];
        const n = f.l && f.l[me] != null ? f.l[me] : 0;
        top += '<span class="grchip grmine' + (q[5] & 16 ? ' rob' : '') + '" style="--c:var(' + SEAT_VARS[me][0] + ')">🏠 <b>' + n + '</b>'
          + (q[5] & 16 ? ' <em>крадуть!</em>' : '') + '</span>';
        let pears = '';
        for (let i = 0; i < CARRY_MAX; i++) pears += '<i class="' + (i < q[4] ? 'gold' : i < q[3] ? 'on' : '') + '"></i>';
        top += '<span class="grchip grcarry' + (q[3] >= CARRY_MAX ? ' full' : '') + '" title="ноша">🧺 ' + pears + '</span>';
        const cd = q[6];
        top += '<span class="grchip grpush' + (cd === 0 ? ' ok' : '') + '">👊 ' + (cd === 0 ? 'готовий' : ((cd * TICK_MS) / 1000).toFixed(1).replace('.', ',') + ' с') + '</span>';
      }
    }
    let rows = '';
    const lar = f && f.l;
    if (!lobby && lar) {
      const seats = [];
      for (let s = 0; s < 8; s++) if (lar[s] != null) seats.push(s);
      seats.sort((a, b) => lar[b] - lar[a] || a - b);
      const best = seats.length ? lar[seats[0]] : 0;
      for (const s of seats) {
        const gone = !(f.p && f.p[s]);
        const nm = s === me ? 'ти' : shortName(ctx, s, 12);
        rows += '<span class="grsc' + (s === me ? ' me' : '') + (gone ? ' gone' : '') + '" style="--c:var(' + SEAT_VARS[s][0] + ')"><i></i>'
          + (best > 0 && lar[s] === best ? '👑 ' : '') + ctx.esc(nm) + ' <b>' + lar[s] + '</b></span>';
      }
    }
    const sig = top + '|' + rows;
    if (sig === st.hudSig) return;
    st.hudSig = sig;
    st.hudEl.innerHTML = top;
    st.hudEl.hidden = !top;
    st.scoreEl.innerHTML = rows;
    st.scoreEl.hidden = !rows;
  }

  /// Телефон, своя картка: на старті партії підкручуємо сторінку, щоб сад і стік стали між шапкою й низом.
  /// У вечірці (embedded) сторінкою керує господар — не чіпаємо.
  function fitView(st) {
    if (!st.cv || !HGames.ui.coarse() || st.ctx.embedded || !HGames.ui.fit) return;
    const a = st.cv.el.getBoundingClientRect();
    const bottomEl = st.ctl ? st.ctl.getBoundingClientRect().bottom : a.bottom;
    const top0 = st.hudEl.hidden ? a.top : st.hudEl.getBoundingClientRect().top;
    const h = bottomEl - top0;
    const fr = HGames.ui.fit();
    const top = fr.top + 4, bottom = fr.h - fr.dock - 4;
    const want = h <= bottom - top ? top + (bottom - top - h) / 2 : top;
    const d = top0 - want;
    if (Math.abs(d) > 24) window.scrollBy({ top: d, behavior: reduced() ? 'auto' : 'smooth' });
  }

  function spin(st) {
    st.awakeUntil = performance.now() + AWAKE_MS;
    if (st.raf) return;
    const loop = () => {
      if (!st.cv || !st.cv.el.isConnected) { st.raf = 0; return; }
      const now = performance.now();
      if (!(st.ctx && st.ctx.playing) && now > st.awakeUntil) { st.raf = 0; draw(st, now); return; }
      st.raf = requestAnimationFrame(loop);
      readPad(st);
      if (st.ptrDown) { const a = ptrSector(st); if (a !== st.ptrA) st.ptrA = a; }
      if (canSend(st)) {
        if (st.sent !== currentWant(st) && now - st.sentAt >= SEND_MS) send(st, true);
        else if (st.sent != null && st.sent >= 0 && now - st.sentAt >= KEEP_MS) send(st, true, true);
      }
      if (!st.cv.el.offsetParent || document.hidden) return;
      draw(st, now);
    };
    st.raf = requestAnimationFrame(loop);
  }

  function statusText(ctx) {
    const st = stOf(ctx);
    const v = ctx.view || {};
    const f = (st && st.last) || ctx.frame || v.frame;
    if (!ctx.playing) {
      if (ctx.room && ctx.room.status === 'lobby') {
        const host = ctx.room.host && ctx.me && String(ctx.room.host).toLowerCase() === String(ctx.me.nick).toLowerCase();
        return host ? 'Тисни «Почати», коли всі сіли (2–8) · самому — «🤖 + бот»' : 'Груші чекають. Стартує господар, коли зібралось 2–8';
      }
      return '';
    }
    if (!f) return '';
    if (st && st.lastFrameAt && f.ph === 1 && performance.now() - st.lastFrameAt > 1200) return '⏳ зв\'язок…';
    if (!ctx.mine) return 'Дивишся збоку';
    if (f.ph === 0) return 'Готуйсь… твоя комора — з обвідкою, неси груші туди';
    const q = f.p && f.p[ctx.seat];
    if (q) {
      if (q[5] & 2) return '💫 Тебе штовхнули — ноша на землі, підбирай назад!';
      if (q[5] & 16) return '🚨 Крадуть з твоєї комори — біжи й штовхни злодія!';
      if (q[5] & 8) return '🏹 Крадеш… стій у колі чужої комори — по груші щосекунди';
      if (q[3] >= CARRY_MAX) return '🧺 Ноша повна — неси до своєї комори';
    }
    return padOn() ? 'Стік — ходити, Ⓐ — штовхан'
      : HGames.ui.coarse() ? 'Стік — ходити, 👊 — штовхан (висипає чужу ношу)'
        : 'Стрілки/WASD чи мишка — ходити, пробіл — штовхан (висипає чужу ношу)';
  }

  function shell(root, st) {
    if (st.wrap && st.wrap.isConnected) return;
    root.innerHTML = '';
    const wrap = st.wrap = document.createElement('div');
    wrap.className = 'grwrap';
    wrap.innerHTML = '<div class="grhud"></div><div class="grstage"></div><div class="grscore"></div>';
    root.appendChild(wrap);
    st.hudEl = wrap.querySelector('.grhud');
    st.stage = wrap.querySelector('.grstage');
    st.scoreEl = wrap.querySelector('.grscore');
    const K = (window.devicePixelRatio || 1) >= 2 ? 1 : 2;   // на DPR 1 малюємо вдвічі щільніше
    st.cv = HGames.ui.canvas(st.stage, { w: SIZE * K, h: SIZE * K, cls: 'grboard' });
    wireCanvas(st);
  }

  HGames.register({
    id: 'grushi',
    added: '2026-10-06',
    icon: ICON,
    seatNames: ['синій', 'рудий', 'зелений', 'жовтий', 'бузковий', 'м’ятний', 'рожевий', 'сірий'],
    seatClass: ['gr0', 'gr1', 'gr2', 'gr3', 'gr4', 'gr5', 'gr6', 'gr7'],
    pad: { dirs: true, a: 'Space', hint: '{dpad} ходити · {a} штовхан' },

    mount(root, ctx) {
      const st = state(root, ctx);
      shell(root, st);
      takeView(st, ctx.view);
      if (ctx.view && ctx.view.frame) { st.last = ctx.view.frame; st.interp.push(st.last); }
      st.onResize = () => { st.lkAt = 0; spin(st); };
      window.addEventListener('resize', st.onResize);
      if (HGames.ui.onFit) HGames.ui.onFit(root, () => st.onResize());
      hud(st);
      controls(root, st);
      spin(st);
    },

    update(root, ctx) {
      const st = state(root, ctx);
      shell(root, st);
      const v = ctx.view;
      takeView(st, v);
      // Вид — правда поза грою (лобі, кінець, F5) і на новій партії (кадр почався з нуля).
      const vf = v && v.frame;
      if (vf && (!ctx.playing || !st.last || st.last.ph >= 3 || vf.t < st.last.t - 2)) {
        if (!st.last || st.last.ph !== vf.ph) st.phAt = performance.now();
        st.last = vf;
        st.interp.reset();
        st.interp.push(vf);
        st.pearAnim.clear();
        st.pearsPrev = new Map((vf.g || []).map((q) => [q[0], [q[1], q[2], q[3], q[4]]]));
      }
      st.cv.el.classList.toggle('play', !!ctx.mine);
      st.cv.resize();
      hud(st);
      controls(root, st);
      // «Ще раз» і F5: сервер не знає про клавішу, яку не відпускали — досилаємо намір
      if (ctx.playing && ctx.mine && !st.was) { st.sent = null; send(st, true); setTimeout(() => fitView(st), 80); }
      if (!ctx.playing) st.sent = null;
      st.was = !!(ctx.playing && ctx.mine);
      spin(st);
    },

    frame(root, ctx, f) {
      const st = state(root, ctx);
      if (!f) return;
      if (st.last && f.t < st.last.t - 2) { st.interp.reset(); st.pearAnim.clear(); }
      if (!st.last || st.last.ph !== f.ph) {
        st.phAt = performance.now();
        if (f.ph === 1 && st.last && st.last.ph === 0) st.goAt = st.phAt;
      }
      events(st, f);
      st.last = f;
      st.lastFrameAt = performance.now();
      st.interp.push(f);
      hud(st);
      if (st.ctl || (ctx.mine && HGames.ui.coarse())) controls(root, st);
      spin(st);
    },

    onKey(e, ctx) {
      const st = stOf(ctx);
      if (!st || !ctx.mine) return false;
      const k = keyOf(e);
      // утримання пам'ятаємо й поза грою: затиснув стрілку на відліку — підеш зі старту
      if (k && !held[k]) { held[k] = true; send(st); }
      if (!ctx.playing) return false;
      if (k) return true;
      if (isPush(e)) { if (!e.repeat) shove(st); return true; }
      return false;
    },

    status: statusText,

    unmount(root) {
      const st = root._grushi;
      if (!st) return;
      cancelAnimationFrame(st.raf);
      st.raf = 0;
      if (st.onResize) window.removeEventListener('resize', st.onResize);
      if (HGames.ui.onFit) HGames.ui.onFit(root, null);
      live.delete(st);
      root._grushi = null;
    },
  });
})();
