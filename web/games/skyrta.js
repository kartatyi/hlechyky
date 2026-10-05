// Скирта (skyrta) — клієнт; правила й числа — docs/games/specs/skyrta.md. Над скиртою їздить сніп: тап — і він
// падає, що звисає за край — обрізає. Свій сніп браузер веде сам (рушій нижче — дослівно SkyrtaCore.cs, цілими
// числами), тап летить на сервер миттєво, а сервер повторює той самий розрахунок; правда — кадр. Чужі скирти
// малюються з кадру тим самим рушієм. Усе малює одне полотно: своя скирта велика, чужі — поруч (на телефоні —
// мініатюри зверху). Модуль живе й у «Глечиковій вечірці» (HGames.embed): лише ctx і свій root.
(() => {
  'use strict';

  // ---------- рушій: дослівно SkyrtaCore.cs (ділення лише над невід'ємними — Math.floor = цілочислове C#) ----------
  const FIELD = 1000, LO = 100, HI = 900, SPAN = HI - LO, BASE_W = 400, BASE_L = (FIELD - BASE_W) / 2;
  const V0 = 420, VK = 28, VMAX = 1100, GAP = 350, MISS_GAP = 900;
  const SWAY_FROM = 15, SWAY_A = 22, SWAY_P = 1400, GROW = 6, GROW_BIG = 16, BIG_FROM = 3;
  const CUT = 0, PERFECT = 1, MISS = 2;

  const speed = (h) => Math.min(VMAX, V0 + VK * Math.max(0, h));
  const tol = (v) => 6 + Math.floor(v / 40);
  const fromLeft = (n) => (n & 1) === 1;
  const sways = (h) => h >= SWAY_FROM;
  function windOverlap(s0, t, wind) {
    let sum = 0;
    const end = s0 + t;
    if (wind) for (let i = 0; i + 1 < wind.length; i += 2) {
      const a = Math.max(wind[i], s0), b = Math.min(wind[i + 1], end);
      if (b > a) sum += b - a;
    }
    return sum;
  }
  function sway(t) {
    const half = SWAY_P / 2, q = (t + SWAY_P / 4) % SWAY_P;
    return q < half ? -SWAY_A + Math.floor(2 * SWAY_A * q / half) : 3 * SWAY_A - Math.floor(2 * SWAY_A * q / half);
  }
  function center(t, v, fl, sw, s0, wind) {
    if (t < 0) t = 0;
    const eff2 = 2 * t + windOverlap(s0, t, wind);
    const d = Math.floor(v * eff2 / 2000);
    const p = d % (2 * SPAN);
    const e = p <= SPAN ? p : 2 * SPAN - p;
    let c = fl ? LO + e : HI - e;
    if (sw) c += sway(t);
    return c;
  }
  const leftOf = (c, w) => c - (w >> 1);
  function land(pl, pw, l, v, streak) {
    if (Math.abs(l - pl) <= tol(v)) {
      const g = streak + 1 >= BIG_FROM ? GROW_BIG : GROW;
      const nw = Math.min(BASE_W, pw + g);
      return [pl - ((nw - pw) >> 1), nw, PERFECT];
    }
    const ol = Math.max(l, pl), or = Math.min(l + pw, pl + pw);
    return or - ol <= 0 ? [pl, pw, MISS] : [ol, or - ol, CUT];
  }
  /// Лівий край снопа n (виїхав о s0, ширина w, висота h) на мить ms гри.
  const sheafLeft = (n, s0, w, h, ms, wind) => leftOf(center(Math.round(ms - s0), speed(h), fromLeft(n), sways(h), s0, wind), w);
  // для docs/games/dev/skyrta-parity.js
  window.SkyrtaCore = { speed, tol, fromLeft, sways, windOverlap, center, sway, left: leftOf, land };

  // ---------- оформлення ----------
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<rect x="2.5" y="12.4" width="11" height="2.4" rx=".8" fill="var(--clay)"/>'
    + '<rect x="3.5" y="9.6" width="9" height="2.6" rx="1.1" fill="var(--sk-hay, #e8b84a)"/>'
    + '<rect x="4.6" y="6.8" width="7" height="2.6" rx="1.1" fill="var(--sk-hay, #e8b84a)"/>'
    + '<rect x="7.4" y="1.6" width="7" height="2.6" rx="1.1" fill="var(--sk-hay2, #f6d77c)"/>'
    + '<path d="M1.2 2.4h4.4M2.2 4.4h3.4" stroke="var(--accent)" stroke-width="1" stroke-linecap="round"/></svg>';
  /// Колір місця — шпагат на снопах і крапка біля імені: вісім скирт мають розрізнятись і на мініатюрах.
  const COLS = ['#e0503a', '#3a7fe0', '#2f9e55', '#9b5de5', '#e5871a', '#16a8a3', '#d6459a', '#6b7a8f'];
  const clamp = (x, a, b) => Math.max(a, Math.min(b, x));
  const coarse = () => !!(HGames.ui.coarse && HGames.ui.coarse());

  function mix(a, b, t) {
    const pa = parseInt(a.slice(1), 16), pb = parseInt(b.slice(1), 16);
    const c = (sh) => Math.round(((pa >> sh) & 255) + ((((pb >> sh) & 255) - ((pa >> sh) & 255)) * t));
    return 'rgb(' + c(16) + ',' + c(8) + ',' + c(0) + ')';
  }
  function rr(g, x, y, w, h, r) {
    r = Math.max(0, Math.min(r, w / 2, h / 2));
    g.beginPath();
    g.moveTo(x + r, y);
    g.arcTo(x + w, y, x + w, y + h, r);
    g.arcTo(x + w, y + h, x, y + h, r);
    g.arcTo(x, y + h, x, y, r);
    g.arcTo(x, y, x + w, y, r);
    g.closePath();
  }
  function txt(g, s, x, y, size, col, align, base) {
    g.font = '800 ' + Math.round(size) + 'px system-ui, -apple-system, "Segoe UI", sans-serif';
    g.textAlign = align || 'center';
    g.textBaseline = base || 'middle';
    g.lineJoin = 'round';
    g.lineWidth = Math.max(2, size / 5);
    g.strokeStyle = 'rgba(30,20,8,.75)';
    g.strokeText(s, x, y);
    g.fillStyle = col || '#fff';
    g.fillText(s, x, y);
  }
  function fitText(g, s, max) {
    if (g.measureText(s).width <= max) return s;
    while (s.length > 1 && g.measureText(s + '…').width > max) s = s.slice(0, -1);
    return s + '…';
  }

  /// Сніп: соломʼяний валок із двома шпагатами кольору місця. seed — щоб соломинки не мерехтіли від кадру до кадру.
  function drawSheaf(g, x, y, w, h, seed, col) {
    if (w < 0.5) return;
    const r = Math.min(h * 0.42, w / 2, 8);
    const gr = g.createLinearGradient(0, y, 0, y + h);
    gr.addColorStop(0, '#fbe08e');
    gr.addColorStop(0.45, '#e9b94f');
    gr.addColorStop(1, '#b7832b');
    g.fillStyle = gr;
    rr(g, x, y, w, h, r);
    g.fill();
    if (h >= 9 && w >= 14) {
      g.strokeStyle = 'rgba(128,84,20,.38)';
      g.lineWidth = 1;
      g.beginPath();
      const cnt = Math.min(16, Math.floor(w / 8));
      for (let j = 0; j < cnt; j++) {
        const fx = (((seed + 3) * 7919 + j * 104729) % 997) / 997;
        const xx = x + 3 + fx * (w - 6);
        g.moveTo(xx, y + h * 0.18);
        g.lineTo(xx + (j & 1 ? 2.5 : -2.5), y + h * 0.86);
      }
      g.stroke();
      const bw = Math.max(1.5, h * 0.13);
      g.fillStyle = col;
      g.fillRect(x + w * 0.24 - bw / 2, y, bw, h);
      g.fillRect(x + w * 0.76 - bw / 2, y, bw, h);
    }
    g.fillStyle = 'rgba(255,255,255,.3)';
    g.fillRect(x + r * 0.7, y + 1, Math.max(0, w - r * 1.4), Math.max(1, h * 0.13));
    g.strokeStyle = 'rgba(92,58,12,.6)';
    g.lineWidth = 1;
    rr(g, x + 0.5, y + 0.5, w - 1, h - 1, r);
    g.stroke();
  }

  // ---------- стан ----------
  const live = new Set();
  const stOf = (ctx) => [...live].find((s) => s.ctx === ctx) || null;
  const mySeat = (st) => (st.ctx && st.ctx.mine ? st.ctx.seat : null);

  function nameOf(st, s) {
    const c = st.ctx;
    return (c.nameOf && c.nameOf(s)) || (c.nickOf && c.nickOf(s)) || (c.seatName && c.seatName(s)) || ('місце ' + (s + 1));
  }

  function msNow(st) {
    const f = st.f;
    if (!f) return 0;
    if (f.ph === 1) return Math.max(f.ms, performance.now() + st.off);
    if (f.ph === 0) return Math.min(0, performance.now() + st.off);
    return f.ms;
  }

  function newRound(st) {
    st.me = null;
    st.myLocal = [];
    st.fx = [];
    st.cam = [];
    st.anim.clear();
    st.windFx = [];
  }

  function fxOf(st, s) {
    return st.fx[s] || (st.fx[s] = { parts: [], texts: [], glow: [], shake: 0 });
  }

  /// Скирта місця s у нас: висота h, верх [l, w). Довша — обрізати, коротша — дотягти верхом (вид із повною скиртою
  /// прийде слідом).
  function fixTop(st, s, h, l, w) {
    let a = st.stk[s];
    if (!a) a = st.stk[s] = [[BASE_L, BASE_W]];
    if (a.length - 1 > h) a.length = h + 1;
    while (a.length - 1 < h) a.push([l, w]);
    if (h > 0) a[h] = [l, w];
  }

  function onFrame(st, f, fromView) {
    if (!f || !f.p) return;
    const prev = st.f;
    // вид міг прийти пізніше за свіжіший кадр — старе не беремо
    if (prev && prev.ph === f.ph && f.ms < prev.ms) return;
    const now = performance.now();
    if (!prev || prev.ph !== f.ph) {
      st.offs = [];
      if ((f.ph === 0 || f.ph === 1) && prev) toView(st, f.ph === 0 ? 350 : 60);
      if (!prev || f.ph === 0 || f.ph === 4) newRound(st);
      if (!prev) st.evSeen = Math.max(st.evSeen, ...(f.ev || []).map((e) => e[1]), 0);   // F5: старих подій не програємо
    }
    // Годинник гри: зсув беремо найбільший за 3 с — це кадр із найменшою затримкою.
    st.offs.push([f.ms - now, now]);
    while (st.offs.length > 1 && now - st.offs[0][1] > 3000) st.offs.shift();
    st.off = Math.max(...st.offs.map((o) => o[0]));
    st.f = f;
    const me = mySeat(st);
    for (let s = 0; s < 8; s++) {
      const q = f.p[s];
      if (!q) continue;
      if (s === me) syncMe(st, f, q);
      else fixTop(st, s, q[4], q[2], q[3]);
    }
    events(st, f);
    if (!fromView) st.hudKey = '';
  }

  /// Свій сніп: кадр із тим самим (чи новішим) номером — правда сервера, беремо її; старіший — сервер ще не отримав
  /// тап, лишаємо прогноз. Тап загубився (1,5 с без відповіді) — повертаємось до серверного.
  function syncMe(st, f, q) {
    const me = st.me;
    const wind = (f.wind && f.wind[st.ctx.seat]) || [];
    if (!me || q[0] >= me.n || performance.now() - me.tapAt > 1500) {
      st.me = { n: q[0], s0: q[1], l: q[2], w: q[3], h: q[4], streak: q[5], done: !!(q[6] & 1), gone: !!(q[6] & 4), wind, tapAt: 0 };
      st.myLocal = [];
      fixTop(st, st.ctx.seat, q[4], q[2], q[3]);
    } else {
      me.wind = wind;
    }
  }

  function takeView(st, v) {
    st.view = v;
    if (!v) return;
    if (v.frame) onFrame(st, v.frame, true);
    const me = mySeat(st);
    for (let s = 0; s < 8; s++) {
      const vs = v.stacks && v.stacks[s];
      if (!vs) { st.stk[s] = null; continue; }
      const a = vs.map((x) => x.slice());
      if (s === me && st.me && st.me.h > a.length - 1) {
        for (let r = a.length; r <= st.me.h; r++) a.push(st.myLocal[r] || [st.me.l, st.me.w]);
      } else {
        const q = st.f && st.f.p && st.f.p[s];
        if (q && s !== me && q[4] > a.length - 1) {
          st.stk[s] = a;
          fixTop(st, s, q[4], q[2], q[3]);
          continue;
        }
      }
      st.stk[s] = a;
    }
  }

  // ---------- події кадру: обрізки, «рівно!», вітер, мета ----------
  function events(st, f) {
    const me = mySeat(st);
    for (const e of f.ev || []) {
      if (e[1] <= st.evSeen) continue;
      st.evSeen = e[1];
      if (e[0] === 'l') {
        const [, , s, n, , l, w, nl, nw, kind] = e;
        if (s === me && st.anim.has(n)) continue;
        const a = st.stk[s];
        const top = a ? a.length - 1 : 0;
        const q = f.p[s];
        fxLand(st, s, kind === MISS ? top + 1 : top, l, w, nl, nw, kind, q ? q[5] : 0);
      } else if (e[0] === 'w') {
        const [, , from, to, a, b] = e;
        st.windFx[to] = { from, a, b };
        fxOf(st, from).texts.push({ s: '💨 → ' + nameOf(st, to), row: null, t0: performance.now(), c: '#cfe9ff', big: true });
      } else if (e[0] === 'g') {
        fxGoal(st, e[2]);
      }
    }
  }

  function fxLand(st, s, row, l, w, nl, nw, kind, streak) {
    const fx = fxOf(st, s);
    const t0 = performance.now();
    const isMe = s === mySeat(st);
    if (kind === MISS) {
      const side = l + w / 2 < FIELD / 2 ? -1 : 1;
      fx.parts.push({ x: l, w, y: row, vx: side * 140, vy: 3, rot: 0, vr: side * (2 + Math.random() * 2), t0 });
      fx.texts.push({ s: 'мимо!', row, t0, c: '#ff8a70' });
      fx.shake = t0;
      if (isMe) buzz(40);
    } else if (kind === CUT) {
      const side = l < nl ? -1 : 1;
      const px = side < 0 ? l : nl + nw, pw = w - nw;
      if (pw > 0) fx.parts.push({ x: px, w: pw, y: row, vx: side * (70 + Math.random() * 60), vy: 2.5, rot: 0, vr: side * (3 + Math.random() * 3), t0 });
      if (pw > w / 2) fx.texts.push({ s: 'ой…', row, t0, c: '#ffd0a0' });
    } else {
      fx.glow.push({ row, l: nl, w: nw, t0 });
      const k = streak || 1;
      fx.texts.push({ s: k >= 2 ? 'рівно! ×' + k : 'рівно!', short: 'рівно!', row, t0, c: k >= BIG_FROM ? '#ffe36b' : '#fff6c9' });
      for (let i = 0; i < 12; i++) {
        const sd = i & 1 ? 1 : -1;
        fx.parts.push({ spark: true, x: sd < 0 ? nl : nl + nw, w: 0, y: row + 0.5, vx: sd * (80 + Math.random() * 220), vy: 2 + Math.random() * 6, t0 });
      }
      if (isMe) buzz(12);
    }
  }

  function fxGoal(st, s) {
    const fx = fxOf(st, s);
    const t0 = performance.now();
    fx.texts.push({ s: '🏁 Докладено!', row: null, t0, c: '#ffe36b', big: true, long: true });
    for (let i = 0; i < 28; i++) {
      fx.parts.push({ spark: true, conf: COLS[i % 8], x: 200 + Math.random() * 600, w: 0, y: null, vx: (Math.random() - 0.5) * 300, vy: 4 + Math.random() * 8, t0 });
    }
  }

  function buzz(ms) {
    const ua = navigator.userActivation;
    try { if (coarse() && navigator.vibrate && (!ua || ua.hasBeenActive)) navigator.vibrate(ms); } catch { /* без вібро */ }
  }

  // ---------- тап ----------
  function tap(st) {
    const ctx = st.ctx, f = st.f, me = st.me;
    if (!ctx.mine || !ctx.playing || !f || f.ph !== 1 || !me || me.done || me.gone) return false;
    const ms = msNow(st);
    const t = Math.round(ms - me.s0);
    if (t < 0) return false;   // сніп ще не виїхав — обрізок падає
    const v = speed(me.h);
    const l = leftOf(center(t, v, fromLeft(me.n), sways(me.h), me.s0, me.wind), me.w);
    const w0 = me.w;
    const [nl, nw, kind] = land(me.l, w0, l, v, me.streak);
    ctx.input('tap', { n: me.n, t });
    st.anim.add(me.n);
    const s = ctx.seat;
    if (kind !== MISS) {
      me.h++;
      me.streak = kind === PERFECT ? me.streak + 1 : 0;
      me.l = nl;
      me.w = nw;
      st.myLocal[me.h] = [nl, nw];
      fixTop(st, s, me.h, nl, nw);
    } else {
      me.streak = 0;
    }
    fxLand(st, s, kind === MISS ? me.h + 1 : me.h, l, w0, nl, nw, kind, me.streak);
    me.n++;
    me.s0 = me.s0 + t + (kind === MISS ? MISS_GAP : GAP);
    me.tapAt = performance.now();
    const goal = (st.view && st.view.goal) || 25;
    if (me.h >= goal) { me.done = true; fxGoal(st, s); }
    st.hudKey = '';
    return true;
  }

  // ---------- розкладка ----------
  function layout(st) {
    const W = Math.max(200, Math.floor(st.wrap.clientWidth || 0));
    if (!st.wrap.clientWidth) return;
    const fit = st.fit || (HGames.ui.fit ? HGames.ui.fit() : null);
    const fh = fit ? fit.h - fit.top - fit.dock : 720;
    const phone = W < 640;
    const emb = !!st.ctx.embedded;
    let H = phone ? clamp(fh - (emb ? 190 : 150), 360, 760) : clamp(fh - (emb ? 260 : 210), 340, 640);
    const f = st.f;
    const seats = [];
    for (let s = 0; s < 8; s++) if (st.stk[s] || (f && f.p && f.p[s])) seats.push(s);
    const me = mySeat(st);
    const mine = me != null && seats.includes(me);
    const P = [];
    const gap = 8;
    if (mine) {
      const others = seats.filter((s) => s !== me);
      const k = others.length;
      if (!k) {
        const bw = Math.min(W, 700);
        P.push({ seat: me, x: (W - bw) / 2, y: 0, w: bw, h: H, big: true });
      } else if (!phone) {
        const bw = Math.round(W * (k > 3 ? 0.56 : 0.62));
        P.push({ seat: me, x: 0, y: 0, w: bw, h: H, big: true });
        const cols = k > 3 ? 2 : 1, rows = Math.ceil(k / cols);
        const cw = (W - bw - gap - (cols - 1) * gap) / cols, ch = (H - (rows - 1) * gap) / rows;
        others.forEach((s, i) => P.push({ seat: s, x: bw + gap + (i % cols) * (cw + gap), y: Math.floor(i / cols) * (ch + gap), w: cw, h: ch }));
      } else {
        const cols = Math.min(k, 4), rows = Math.ceil(k / 4);
        const mh = rows > 1 ? 96 : 124;
        const strip = rows * mh + (rows - 1) * 6;
        H = Math.max(H, strip + 320);
        const cw = (W - (cols - 1) * 6) / cols;
        others.forEach((s, i) => P.push({ seat: s, x: (i % cols) * (cw + 6), y: Math.floor(i / cols) * (mh + 6), w: cw, h: mh }));
        P.push({ seat: me, x: 0, y: strip + gap, w: W, h: H - strip - gap, big: true });
      }
    } else if (seats.length) {
      const n = seats.length;
      const cols = phone ? Math.min(2, n) : Math.min(4, n), rows = Math.ceil(n / cols);
      if (phone && rows > 2) H = Math.max(H, rows * 170);
      const cw = (W - (cols - 1) * gap) / cols, ch = (H - (rows - 1) * gap) / rows;
      seats.forEach((s, i) => P.push({ seat: s, x: (i % cols) * (cw + gap), y: Math.floor(i / cols) * (ch + gap), w: cw, h: ch, big: n === 1 }));
    }
    for (const p of P) {
      p.row = clamp(Math.min(p.w * 0.075, p.h / 11), 5, 44);
      p.ground = p.row * 0.9;
    }
    st.panels = P;
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    if (st.W !== W || st.H !== H || st.dpr !== dpr) {
      st.W = W; st.H = H; st.dpr = dpr;
      st.cv.width = Math.round(W * dpr);
      st.cv.height = Math.round(H * dpr);
      st.cv.style.height = H + 'px';
    }
    st.layKey = seats.join() + '|' + me;
  }

  /// Телефон, партія почалась: докрутити сторінку, щоб поле стало цілим між шапкою й нижніми вкладками.
  function toView(st, delay) {
    if (st.ctx.embedded || !st.ctx.mine || st.W >= 640) return;
    // ще раз — коли відлік змінився грою: каркас міг перемалювати картку й скинути прокрутку
    setTimeout(() => {
      if (!st.el.isConnected) return;
      const fit = st.fit || (HGames.ui.fit ? HGames.ui.fit() : null);
      if (!fit) return;
      const r = st.el.getBoundingClientRect();
      const over = r.bottom - (fit.h - fit.dock) + 6;
      if (over > 2) window.scrollBy({ top: Math.min(over, r.top - fit.top - 4), behavior: 'smooth' });
    }, delay);
  }

  // ---------- малювання ----------
  function loop(st) {
    st.raf = requestAnimationFrame(() => loop(st));
    if (!st.cv.isConnected || document.hidden) return;
    const now = performance.now();
    const dt = Math.min(0.05, (now - st.last) / 1000);
    st.last = now;
    const f = st.f;
    const busy = f && (f.ph === 0 || f.ph === 1) || st.fx.some((x) => x && (x.parts.length || x.texts.length));
    // між партіями полотно стоїть — малюємо рідше (лобі крутить показовий сніп, йому вистачить 30 к/с)
    if (!busy && (st.idle = (st.idle + 1) % 2)) return;
    let key = '';
    for (let s = 0; s < 8; s++) if (st.stk[s] || (f && f.p && f.p[s])) key += s + ',';
    if (key.slice(0, -1) + '|' + mySeat(st) !== st.layKey) layout(st);
    draw(st, now, busy ? dt : dt * 2);
    if (now - st.hudAt > 200) { st.hudAt = now; hud(st); }
  }

  function draw(st, now, dt) {
    const g = st.g;
    g.setTransform(st.dpr, 0, 0, st.dpr, 0, 0);
    g.clearRect(0, 0, st.W, st.H);
    const ms = msNow(st);
    for (const P of st.panels) drawPanel(st, g, P, ms, now, dt);
    const f = st.f;
    if (f && f.ph === 0) {
      const big = st.panels.find((p) => p.big) || { x: 0, y: 0, w: st.W, h: st.H };
      const left = -ms;
      const n = Math.ceil(left / 1000);
      const frac = (left % 1000) / 1000;
      g.save();
      g.globalAlpha = 0.35 + 0.65 * frac;
      txt(g, n > 0 ? String(n) : 'Кидай!', big.x + big.w / 2, big.y + big.h * 0.38, Math.min(big.w, big.h) * (0.22 + 0.08 * frac), '#fff');
      g.restore();
      txt(g, 'До ' + ((st.view && st.view.goal) || 25) + ' снопів — хто перший', big.x + big.w / 2, big.y + big.h * 0.38 + Math.min(big.w, big.h) * 0.2, Math.max(13, big.w / 28), '#ffe9a8');
    }
  }

  function drawPanel(st, g, P, ms, now, dt) {
    const s = P.seat, f = st.f, v = st.view || {};
    const q = f && f.p ? f.p[s] : null;
    const isMe = s === mySeat(st);
    const stk = st.stk[s] || [[BASE_L, BASE_W]];
    const top = stk.length - 1;
    let n = 1, s0 = 0, w = BASE_W, h = top, fl = 0, wind = null, streak = 0;
    if (q) { n = q[0]; s0 = q[1]; w = q[3]; h = q[4]; streak = q[5]; fl = q[6]; wind = f.wind && f.wind[s]; }
    if (isMe && st.me) { n = st.me.n; s0 = st.me.s0; w = st.me.w; h = st.me.h; streak = st.me.streak; wind = st.me.wind; if (st.me.done) fl |= 1; }
    const k = P.w / 1200, R = P.row;
    const want = Math.max(0, top + 2 - (P.h * 0.7 - P.ground) / R);
    let cam = st.cam[s];
    cam = cam == null ? want : cam + (want - cam) * Math.min(1, dt * 5);
    st.cam[s] = cam;
    const fx = fxOf(st, s);
    let sx = 0;
    if (fx.shake && now - fx.shake < 300) sx = Math.sin((now - fx.shake) / 18) * 4 * (1 - (now - fx.shake) / 300) * (P.big ? 1 : 0.5);
    const X = (u) => P.x + sx + (u + 100) * k;
    const yb = (i) => P.y + P.h - P.ground - (i - cam) * R;   // низ ряду i (ряд 0 — підвалина)
    const goal = v.goal || 25;
    const gone = !!(fl & 4), done = !!(fl & 1);

    g.save();
    rr(g, P.x, P.y, P.w, P.h, P.big ? 14 : 9);
    g.clip();
    // небо: що вища скирта, то глибше синє
    const up = clamp(cam / 30, 0, 1);
    const sky = g.createLinearGradient(0, P.y, 0, P.y + P.h);
    sky.addColorStop(0, mix('#6fa9e2', '#22325f', up));
    sky.addColorStop(1, mix('#dcefff', '#86a9d8', up));
    g.fillStyle = sky;
    g.fillRect(P.x, P.y, P.w, P.h);
    if (P.w > 120) {
      // сонце й хмари (хмари пливуть повільно і їдуть униз, коли камера вгору)
      g.fillStyle = 'rgba(255,236,160,.85)';
      g.beginPath();
      g.arc(P.x + P.w * 0.84, P.y + P.h * 0.13 + cam * R * 0.05, Math.max(10, P.w * 0.045), 0, 7);
      g.fill();
      g.fillStyle = 'rgba(255,255,255,.75)';
      for (let i = 0; i < 3; i++) {
        const cx = P.x + ((i * 0.37 * P.w + now * 0.008 * (i + 1) + s * 50) % (P.w + 140)) - 70;
        const cy = P.y + ((P.h * (0.12 + i * 0.22) + cam * R * 0.35) % (P.h * 0.8));
        const cr = Math.max(8, P.w * 0.03);
        g.beginPath();
        g.arc(cx, cy, cr, 0, 7); g.arc(cx + cr * 1.1, cy - cr * 0.4, cr * 1.2, 0, 7); g.arc(cx + cr * 2.3, cy, cr * 0.9, 0, 7);
        g.fill();
      }
    }
    // земля: пагорби, поле, підвалина
    const gy = yb(0);
    if (gy - R * 3 < P.y + P.h) {
      g.fillStyle = '#7fae5a';
      g.beginPath();
      g.moveTo(P.x, gy - R * 0.6);
      g.quadraticCurveTo(P.x + P.w * 0.25, gy - R * 2.6, P.x + P.w * 0.55, gy - R * 0.9);
      g.quadraticCurveTo(P.x + P.w * 0.8, gy - R * 2.1, P.x + P.w, gy - R * 0.7);
      g.lineTo(P.x + P.w, P.y + P.h); g.lineTo(P.x, P.y + P.h);
      g.fill();
      const fg = g.createLinearGradient(0, gy, 0, gy + P.ground);
      fg.addColorStop(0, '#5e9440'); fg.addColorStop(1, '#3f6f2c');
      g.fillStyle = fg;
      g.fillRect(P.x, gy - R * 0.15, P.w, P.h);
      // підвалина — дерев'яний поміст
      const bx = X(BASE_L), bw = BASE_W * k, by = gy - R;
      g.fillStyle = '#6d4526';
      rr(g, bx, by + R * 0.15, bw, R * 0.85, Math.min(4, R * 0.2));
      g.fill();
      g.strokeStyle = 'rgba(30,16,6,.45)';
      g.lineWidth = 1;
      g.beginPath();
      for (let j = 1; j < 4; j++) { g.moveTo(bx + bw * j / 4, by + R * 0.2); g.lineTo(bx + bw * j / 4, by + R); }
      g.stroke();
    }
    // мета — пунктир із прапорцем
    const gyl = yb(goal) - R;
    if (gyl > P.y - 4 && gyl < P.y + P.h) {
      g.strokeStyle = 'rgba(255,255,255,.7)';
      g.setLineDash([6, 5]);
      g.lineWidth = 1.5;
      g.beginPath(); g.moveTo(P.x, gyl); g.lineTo(P.x + P.w, gyl); g.stroke();
      g.setLineDash([]);
      // під самою шапкою панелі підпис мети лягає під пунктир, щоб не налазити на ім'я
      if (P.w > 90) txt(g, '🏁 ' + goal, P.x + 6, gyl < P.y + 44 ? gyl + 10 : gyl - 9, Math.max(10, R * 0.45), '#fff', 'left');
    }
    // скирта
    const col = COLS[s % 8];
    const from = Math.max(1, Math.floor(cam) - 1);
    for (let i = from; i <= top; i++) {
      const y = yb(i) - R;
      if (y > P.y + P.h) continue;
      const [l, sw] = stk[i];
      drawSheaf(g, X(l), y, sw * k, R, i, col);
    }
    // «рівно!» — спалах на верхньому снопі
    for (let i = fx.glow.length - 1; i >= 0; i--) {
      const o = fx.glow[i], a = 1 - (now - o.t0) / 450;
      if (a <= 0) { fx.glow.splice(i, 1); continue; }
      g.save();
      g.globalAlpha = a;
      g.shadowColor = '#fff3a0';
      g.shadowBlur = 18 * a;
      g.strokeStyle = '#fff7c2';
      g.lineWidth = 2.5;
      rr(g, X(o.l) - 2, yb(o.row) - R - 2, o.w * k + 4, R + 4, 6);
      g.stroke();
      g.restore();
    }
    // сніп, що їде
    const ph = f ? f.ph : 4;
    if (!gone && !done && (ph === 0 || ph === 1 || ph === 4)) {
      let t = ms - s0;
      let hh = h, nn = n, ss = s0, ww = w, wd = wind;
      if (ph === 4) { t = (now % 60000); hh = 0; nn = 1; ss = 0; ww = BASE_W; wd = null; }
      if (ph === 0) t = 0;
      if (t >= 0) {
        const l = sheafLeft(nn, ss, ww, hh, ss + t, wd);
        const y = yb(top + 1) - R;
        g.save();
        if (ph !== 1) g.globalAlpha = 0.75;
        // тінь на верх скирти — видно, куди ляже
        g.fillStyle = 'rgba(40,24,6,.18)';
        g.fillRect(X(l), y + R, ww * k, Math.max(2, R * 0.18));
        drawSheaf(g, X(l), y, ww * k, R, nn + 7, col);
        g.restore();
        if (isMe && ph === 1 && h === 0 && ms < 4500 && P.big) {
          txt(g, coarse() ? '👆 Тапни, коли сніп над скиртою' : 'Пробіл чи клік — коли сніп над скиртою', P.x + P.w / 2, y - R * 1.1, Math.max(13, P.w / 30), '#fff');
        }
      }
    }
    // обрізки, промахи, іскри
    for (let i = fx.parts.length - 1; i >= 0; i--) {
      const o = fx.parts[i];
      const age = (now - o.t0) / 1000;
      if (age > (o.spark ? 0.9 : 2.2)) { fx.parts.splice(i, 1); continue; }
      o.vy -= (o.spark ? 18 : 30) * dt;
      o.x += o.vx * dt;
      if (o.y == null) o.y = cam + (P.h - P.ground) / R - 1;
      o.y += o.vy * dt;
      if (o.spark) {
        g.fillStyle = o.conf || '#fff3a0';
        g.globalAlpha = 1 - age / 0.9;
        const sz = o.conf ? Math.max(2, R * 0.22) : Math.max(1.5, R * 0.12);
        g.fillRect(X(o.x) - sz / 2, yb(o.y) - sz / 2, sz, sz);
        g.globalAlpha = 1;
      } else {
        o.rot += o.vr * dt;
        const cx = X(o.x + o.w / 2), cy = yb(o.y) - R / 2;
        g.save();
        g.translate(cx, cy);
        g.rotate(o.rot);
        g.globalAlpha = clamp(2.2 - age, 0, 1);
        drawSheaf(g, -o.w * k / 2, -R / 2, o.w * k, R, 3, col);
        g.restore();
      }
    }
    // вітер: смуги й 💨; налітає — попередження
    const wf = st.windFx[s];
    let wa = -1, wb = -1;
    if (wind) for (let i = 0; i + 1 < wind.length; i += 2) if (wind[i + 1] > ms) { wa = wind[i]; wb = wind[i + 1]; break; }
    if (wa < 0 && wf && wf.b > ms) { wa = wf.a; wb = wf.b; }
    if (wa >= 0 && ph === 1) {
      if (ms >= wa) {
        g.strokeStyle = 'rgba(255,255,255,.55)';
        g.lineWidth = 2;
        g.beginPath();
        for (let i = 0; i < 7; i++) {
          const yy = P.y + P.h * (0.12 + i * 0.12);
          const xx = P.x + ((now * 0.9 + i * 173) % (P.w + 120)) - 60;
          g.moveTo(xx, yy); g.lineTo(xx + P.w * 0.18, yy);
        }
        g.stroke();
        txt(g, '💨 ' + Math.ceil((wb - ms) / 1000), P.x + P.w - 8, P.y + (P.big ? 46 : 30), P.big ? 20 : 12, '#e6f4ff', 'right');
      } else if (Math.floor(now / 160) % 2) {
        txt(g, '💨 вітер!', P.x + P.w / 2, P.y + P.h * 0.3, P.big ? 26 : 13, '#e6f4ff');
      }
    }
    // написи над скиртою
    for (let i = fx.texts.length - 1; i >= 0; i--) {
      const o = fx.texts[i];
      const life = o.long ? 2200 : 950;
      const a = (now - o.t0) / life;
      if (a >= 1) { fx.texts.splice(i, 1); continue; }
      const y = o.row == null ? P.y + P.h * 0.4 - a * 20 : yb(o.row) - R * 1.5 - a * R * 1.4;
      g.globalAlpha = a < 0.75 ? 1 : (1 - a) * 4;
      const small = P.w < 130;
      const size = (o.big ? (P.big ? 26 : small ? 11 : 13) : (P.big ? 22 : small ? 10 : 12)) * (a < 0.1 ? 0.7 + a * 3 : 1);
      txt(g, small && o.short ? o.short : o.s, P.x + P.w / 2, y, size, o.c);
      g.globalAlpha = 1;
    }
    // переможцю — 🏆 над скиртою
    if (ph === 3 && (v.winners || []).includes(s)) txt(g, '🏆', X(stk[top][0] + stk[top][1] / 2), yb(top) - R * 1.6, Math.max(16, R * 1.1), '#fff');
    if (gone) {
      g.fillStyle = 'rgba(20,20,20,.45)';
      g.fillRect(P.x, P.y, P.w, P.h);
      txt(g, 'пішов', P.x + P.w / 2, P.y + P.h / 2, P.big ? 22 : 12, '#ddd');
    }
    // підпис: крапка кольору місця, ім'я, висота
    const fs = P.big ? 15 : (P.w < 100 ? 10 : 12);
    g.fillStyle = 'rgba(20,14,6,.45)';
    rr(g, P.x + 4, P.y + 4, P.w - 8, fs + 10, 7);
    g.fill();
    g.fillStyle = col;
    g.beginPath(); g.arc(P.x + 12, P.y + 9 + fs / 2, fs * 0.32, 0, 7); g.fill();
    const hs = (done ? '🏁 ' : '') + h + (P.w > 110 ? ' / ' + goal : '');
    g.font = '800 ' + fs + 'px system-ui, -apple-system, "Segoe UI", sans-serif';
    const hw = g.measureText(hs).width;
    g.fillStyle = '#fff';
    g.textBaseline = 'middle';
    g.textAlign = 'right';
    g.fillText(hs, P.x + P.w - 10, P.y + 9 + fs / 2);
    g.textAlign = 'left';
    g.font = '600 ' + fs + 'px system-ui, -apple-system, "Segoe UI", sans-serif';
    // на мініатюрі «🤖 бот овес» → «🤖овес»: інакше від імені лишається «бот…»
    const nm = isMe ? 'ти' : P.w < 130 ? nameOf(st, s).replace(/^🤖\s*бот\s+/, '🤖') : nameOf(st, s);
    g.fillText(fitText(g, nm, P.w - hw - 34), P.x + 19, P.y + 9 + fs / 2);
    if (streak >= 2 && ph === 1 && !done && P.w >= 130) txt(g, '🔥×' + streak, P.x + 8, P.y + fs + 26, P.big ? 15 : 10, '#ffd36b', 'left');
    g.restore();
    // рамка: своя — акцентом, докладена — золотом
    g.lineWidth = isMe ? 2.5 : 1;
    g.strokeStyle = done ? '#f1c541' : isMe ? (HGames.ui.css ? HGames.ui.css('--accent', '#e0a040') : '#e0a040') : 'rgba(127,127,127,.35)';
    rr(g, P.x + 0.5, P.y + 0.5, P.w - 1, P.h - 1, P.big ? 14 : 9);
    g.stroke();
  }

  // ---------- рядок над полем, підказка, підсумок ----------
  function hud(st) {
    const v = st.view, f = st.f;
    if (!v) return;
    const me = st.me;
    const ms = msNow(st);
    const s = mySeat(st);
    const goal = v.goal || 25;
    const h = me ? me.h : (s != null && st.stk[s] ? st.stk[s].length - 1 : 0);
    const left = !f ? v.limit : f.ph === 1 ? Math.max(0, f.left - (ms - f.ms)) : f.ph === 0 ? v.limit : f.ph === 3 ? 0 : v.limit;
    const sec = Math.ceil(left / 1000);
    const stats = s != null && v.stats ? v.stats[s] : null;
    const streak = me && f && f.ph === 1 ? me.streak : 0;
    const key = [h, goal, sec, streak, stats && stats.perfects, f && f.ph, s].join();
    if (key === st.hudKey) return;
    st.hudKey = key;
    st.hH.innerHTML = s != null ? '🌾 <b>' + h + '</b> / ' + goal : '🌾 до ' + goal;
    st.hT.textContent = '⏱ ' + Math.floor(sec / 60) + ':' + String(sec % 60).padStart(2, '0');
    st.hBar.style.width = (100 * left / (v.limit || 90000)).toFixed(1) + '%';
    st.hBar.parentElement.classList.toggle('low', f && f.ph === 1 && left < 10000);
    st.hS.textContent = streak >= 2 ? '🔥 ×' + streak : stats && stats.perfects ? '✨ ' + stats.perfects : '';
    st.hint.textContent = hintText(st);
  }

  function hintText(st) {
    const v = st.view || {}, f = st.f, ctx = st.ctx;
    const how = coarse() ? 'тап по полю' : 'пробіл чи клік';
    if (!f || f.ph === 4) return 'Сніп їздить над скиртою — ' + how + ', і він падає. Що звисає — обріже; рівно — скирта ширшає, тричі рівно — 💨 вітер сусідові';
    if (f.ph === 3) return v.party ? '' : 'Скирти складено';
    if (!ctx.mine) return 'Дивишся збоку: скирти всіх гравців';
    if (st.me && st.me.done) return '🏁 Докладено! Чекаємо решту';
    return f.ph === 0 ? how + ' — кинути сніп · рівно тричі поспіль — 💨 вітер сусідові' : '';
  }

  function overlay(st) {
    const v = st.view;
    const el = st.over;
    if (!v || v.phase !== 'over' || v.party) { el.hidden = true; st.overKey = ''; return; }
    const rows = [];
    for (let s = 0; s < 8; s++) {
      const a = v.stacks && v.stacks[s];
      if (!a) continue;
      const top = a[a.length - 1];
      rows.push({ s, h: a.length - 1, w: a.length > 1 ? top[1] : 0, st: v.stats && v.stats[s] });
    }
    rows.sort((x, y) => y.h - x.h || y.w - x.w);
    const win = v.winners || [];
    const key = JSON.stringify([rows.map((r) => [r.s, r.h]), win]);
    if (key === st.overKey) return;
    st.overKey = key;
    const esc = st.ctx.esc || ((x) => String(x));
    const title = win.length ? '🏆 ' + win.map((s) => esc(nameOf(st, s))).join(', ') + (win.length > 1 ? ' — скирти рівні!' : ' — перша скирта на селі!') : 'Нічия — скирт ніхто не доклав';
    el.innerHTML = '<div class="sk-ot">' + title + '</div>' + rows.map((r, i) => '<div class="sk-or' + (win.includes(r.s) ? ' win' : '') + '">'
      + '<span class="sk-op">' + (i + 1) + '</span><i style="background:' + COLS[r.s] + '"></i><span class="sk-on">' + esc(nameOf(st, r.s)) + '</span>'
      + '<span class="sk-ov">🌾 ' + r.h + '</span><span class="sk-ox">' + (r.st ? '✨ ' + r.st.perfects + ' · 🔥 ' + r.st.best : '') + '</span></div>').join('');
    el.hidden = false;
  }

  function shell(root, st) {
    root.innerHTML = '<div class="sk"><div class="sk-hud"><span class="sk-h"></span><span class="sk-time"><span class="sk-bar"><i></i></span>'
      + '<span class="sk-t"></span></span><span class="sk-s"></span></div><div class="sk-wrap"><canvas class="sk-cv"></canvas>'
      + '<div class="sk-over" hidden></div></div><div class="sk-hint"></div></div>';
    const el = root.firstChild;
    st.el = el;
    st.hH = el.querySelector('.sk-h');
    st.hT = el.querySelector('.sk-t');
    st.hBar = el.querySelector('.sk-bar i');
    st.hS = el.querySelector('.sk-s');
    st.wrap = el.querySelector('.sk-wrap');
    st.cv = el.querySelector('.sk-cv');
    st.g = st.cv.getContext('2d');
    st.over = el.querySelector('.sk-over');
    st.hint = el.querySelector('.sk-hint');
  }

  function statusText(ctx) {
    const st = stOf(ctx);
    if (!ctx.playing) return '';
    if (!ctx.mine) return 'Дивишся збоку';
    if (st && st.me && st.me.done) return '🏁 Докладено — чекаємо решту';
    return (coarse() ? 'Тапай по полю' : 'Пробіл чи клік') + ', коли сніп над скиртою';
  }

  HGames.register({
    id: 'skyrta',
    added: '2026-10-06',
    icon: ICON,
    seatClass: ['sk0', 'sk1', 'sk2', 'sk3', 'sk4', 'sk5', 'sk6', 'sk7'],
    pad: { a: 'Space', anyBtn: true, hint: '{a} — кинути сніп' },

    mount(root, ctx) {
      const st = {
        root, ctx, stk: [], fx: [], cam: [], windFx: [], anim: new Set(), myLocal: [], me: null, f: null, view: null,
        off: 0, offs: [], evSeen: 0, panels: [], W: 0, H: 0, dpr: 1, raf: 0, last: performance.now(), idle: 0, hudAt: 0,
        hudKey: '', overKey: '', layKey: '', fit: null,
      };
      root._sk = st;
      live.add(st);
      shell(root, st);
      st.cv.addEventListener('pointerdown', (e) => {
        if (e.button > 0) return;
        if (tap(st)) e.preventDefault();
      });
      st.ro = window.ResizeObserver ? new ResizeObserver(() => layout(st)) : null;
      if (st.ro) st.ro.observe(st.wrap);
      takeView(st, ctx.view);
      if (HGames.ui.onFit) HGames.ui.onFit(root, (fit) => { st.fit = fit; layout(st); });
      layout(st);
      hud(st);
      overlay(st);
      loop(st);
    },

    update(root, ctx) {
      const st = root._sk;
      if (!st) return;
      st.ctx = ctx;
      takeView(st, ctx.view);
      if (HGames.ui.onFit) HGames.ui.onFit(root, (fit) => { st.fit = fit; layout(st); });
      st.hudKey = '';
      hud(st);
      overlay(st);
    },

    frame(root, ctx, f) {
      const st = root._sk;
      if (!st || !f) return;
      st.ctx = ctx;
      onFrame(st, f, false);
    },

    onKey(e, ctx) {
      const st = stOf(ctx);
      if (!st || !ctx.mine || !ctx.playing) return false;
      if (e.code !== 'Space' && e.code !== 'ArrowDown') return false;
      if (!e.repeat) tap(st);
      return true;
    },

    status: statusText,

    unmount(root) {
      const st = root._sk;
      if (!st) return;
      cancelAnimationFrame(st.raf);
      st.raf = 0;
      if (st.ro) st.ro.disconnect();
      if (HGames.ui.onFit) HGames.ui.onFit(root, null);
      live.delete(st);
      root._sk = null;
    },
  });
})();
