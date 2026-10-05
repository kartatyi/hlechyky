/*
  Ярмарковий тир (tyr) і «Тир дня» (tyr-daily) — один модуль. Spec: docs/games/specs/tyr.md (§4 вид і кадр, §9 клієнт).

  У кожного своя копія тиру з однаковими мішенями: розклад стенду приходить у кадрі вікном на 2 с наперед
  (tg = [[id, вид, t0, тривалість, x0, y0, vx, r]]), ми накопичуємо його в мапі й малюємо мішені за своїм годинником
  стенду. Влучання рахує сервер за часом пострілу, тож постріл ми показуємо одразу (передбачення тими самими правилами з
  view.rules), а правду беремо з кадру: p[місце] = [рахунок, рахунок стенду, набоїв, reloadAt, smokeUntil, n],
  h[місце] = збиті id. n — скільки наших дій сервер уже прийняв: решту ще в дорозі переграємо поверх правди.

  Годинник: offset = performance.now() − now, найменший з останніх кадрів (кадр, що летів найшвидше); мить стенду
  T = performance.now() − offset. З цим T і шлемо постріл: сервер приймає now − 800 … now + 150.

  Ввід: клік / дотик — постріл у точку; правий клік, R, «⟳» — перезарядка; стрілки/WASD — приціл, пробіл — постріл;
  пад — стік (аналоговий, якщо браузер дає getGamepads), Ⓐ постріл, Ⓧ перезарядка. Звуку нема навмисно.
  У вечірці модуль живе всередині HGames.embed: нічого поза ctx і своїм root, усе прибираємо в unmount.
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<circle cx="8" cy="8" r="6.4" fill="none" stroke="var(--danger, #e57373)" stroke-width="1.4"/>'
    + '<path d="M8 .8v3M8 12.2v3M.8 8h3M12.2 8h3" stroke="var(--text)" stroke-width="1.3" stroke-linecap="round"/>'
    + '<path d="M6.3 6.2h3.4l.2 1c1.2.6 1.6 1.7 1.3 2.8-.4 1.3-1.6 1.8-3.2 1.8s-2.8-.5-3.2-1.8c-.3-1.1.1-2.2 1.3-2.8z" fill="var(--clay)"/></svg>';

  const ICON_DAY = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<circle cx="8" cy="8" r="3.4" fill="var(--accent)"/>'
    + '<path d="M8 .9v2.2M8 12.9v2.2M.9 8h2.2M12.9 8h2.2M3 3l1.5 1.5M11.5 11.5 13 13M13 3l-1.5 1.5M4.5 11.5 3 13" stroke="var(--accent)" stroke-width="1.3" stroke-linecap="round"/>'
    + '<circle cx="8" cy="8" r="1.3" fill="var(--danger, #e57373)"/></svg>';

  /// Полотно 800×480 логічних пікселів на поле 1000×600 одиниць: різкість без мільйонів пікселів на телефоні з dpr 3.
  const VW = 800, VH = 480;
  const JUG = 0, DUCK = 1, GOLD = 2, KEG = 3, POT = 4;
  const KIND_NAME = ['глек', 'качка', 'золотий глек', 'діжка з порохом', 'бабин горщик'];
  const STAND_TIP = [
    'Глеки вискакують з-за полиці — лови, поки не сховались',
    'Качки пливуть — цілься трохи наперед',
    'Усе разом, і на мить блискає золотий глек!',
  ];
  /// Запас, якщо вид ще без правил (spec §2) — справжні завжди беремо з view.rules.
  const DEF = {
    w: 1000, h: 600, standMs: 45000, readyMs: 3000, graceMs: 400, drum: 6, reloadMs: 1000, smokeMs: 1500, minGap: 90,
    slack: 6, maxLag: 800, ahead: 150, points: [1, 2, 3, -3, -2], radius: [36, 36, 30, 38, 38], shelfY: [150, 290],
    laneY: [420, 520], slotX: [125, 232, 339, 446, 553, 660, 767, 874],
  };
  const SEAT_COL = ['#f4c542', '#7bd389', '#ef7a6f', '#64b5f6', '#c58af0', '#ff9f43', '#4dd0e1', '#f48fb1'];
  /// Непідтверджена дія старша за це — сервер її відхилив (запізнилась, інший стенд): викидаємо з передбачення.
  const LOST_MS = 1100;
  const AIM_SPEED = 820;           // одиниць поля за секунду — приціл стрілками й стіком

  const live = new Set();
  const stOf = (ctx) => [...live].find((s) => s.ctx === ctx) || null;
  /// Хто зараз керує падом: останній живий стіл, що оновлювався (вечірка може тримати лише один).
  let lastSt = null;

  const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
  const ease = (u) => 1 - Math.pow(1 - clamp(u, 0, 1), 3);
  const back = (u) => { u = clamp(u, 0, 1); const c = 1.6; return 1 + (c + 1) * Math.pow(u - 1, 3) + c * Math.pow(u - 1, 2); };
  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const sign = (n) => (n > 0 ? '+' + n : n < 0 ? '−' + Math.abs(n) : '0');

  function ochok(n) {
    const a = Math.abs(n), d = a % 10, h = a % 100;
    return d === 1 && h !== 11 ? 'очко' : d >= 2 && d <= 4 && (h < 12 || h > 14) ? 'очки' : 'очок';
  }

  // ---------- правила копії (дзеркало TyrCore.Shoot/Reload) ----------

  const xAt = (tg, t) => tg.x0 + tg.vx * (t - tg.t0) / 1000;
  const alive = (tg, t) => t >= tg.t0 && t < tg.t0 + tg.dur;

  function settle(s, t, R) { if (s.reloadAt > 0 && t >= s.reloadAt) { s.ammo = R.drum; s.reloadAt = 0; } }
  const reloading = (s, t) => s.reloadAt > 0 && t < s.reloadAt;

  function findHit(s, tgs, t, x, y, R) {
    let best = null, bestK = Infinity;
    for (const tg of tgs.values()) {
      if (!alive(tg, t) || s.hits.has(tg.id)) continue;
      const dx = xAt(tg, t) - x, dy = tg.y0 - y, rr = tg.r + R.slack, d2 = dx * dx + dy * dy;
      if (d2 > rr * rr) continue;
      const k = d2 / (tg.r * tg.r);
      if (k < bestK) { bestK = k; best = tg; }
    }
    return best;
  }

  function simShot(s, tgs, t, x, y, R) {
    if (t < s.lastT + R.minGap) return null;
    settle(s, t, R);
    if (reloading(s, t) || t < s.smoke) return null;
    s.lastT = t;
    s.n++;
    if (s.ammo <= 0) { s.reloadAt = t + R.reloadMs; return { reload: true }; }
    s.ammo--;
    const hit = findHit(s, tgs, t, x, y, R);
    if (!hit) return { miss: true };
    s.hits.add(hit.id);
    const d = R.points[hit.kind];
    s.score += d;
    s.sscore += d;
    if (hit.kind === KEG) s.smoke = t + R.smokeMs;
    return { hit, d };
  }

  function simReload(s, t, R) {
    if (t < s.lastT + R.minGap) return false;
    settle(s, t, R);
    if (reloading(s, t) || t < s.smoke || s.ammo >= R.drum) return false;
    s.lastT = t;
    s.n++;
    s.reloadAt = t + R.reloadMs;
    return true;
  }

  const cloneCopy = (s) => Object.assign({}, s, { hits: new Set(s.hits) });
  const freshCopy = (R) => ({ score: 0, sscore: 0, ammo: R.drum, reloadAt: 0, smoke: -1, n: 0, lastT: -Infinity, hits: new Set() });

  // ---------- стан столу ----------

  function state(root, ctx) {
    let st = root._tyr;
    if (!st) {
      st = root._tyr = {
        root, ctx, R: DEF, last: null, stand: -1, ph: 3, k: 0,
        offs: [], offset: null, frameAt: 0, frameNow: 0,
        tg: new Map(), srv: null, base: 0, sent: [], pred: freshCopy(DEF), score0: 0,
        fx: [], holes: [], seen: new Set(), follow: null,
        aim: { x: 500, y: 300, on: false, mode: '' }, held: { l: 0, r: 0, u: 0, d: 0 }, padAt: 0, analogAt: 0,
        nudge: null, banner: null, flash: {}, prevScores: [], hudAt: 0, lastDraw: 0, raf: 0, bg: null, bgKey: '',
      };
      live.add(st);
    }
    st.ctx = ctx;
    if (ctx.view && ctx.view.rules) st.R = Object.assign({}, DEF, ctx.view.rules);
    return st;
  }

  const meOf = (st) => (st.ctx && st.ctx.mine && st.ctx.seat != null ? st.ctx.seat : null);
  /// Чию копію показуємо: свою, а глядачеві — обраного стрільця (за замовчуванням — лідера).
  function shown(st) {
    const me = meOf(st);
    if (me != null) return me;
    const p = (st.last && st.last.p) || [];
    if (st.follow != null && p[st.follow]) return st.follow;
    let best = null;
    p.forEach((x, i) => { if (x && (best == null || x[0] > p[best][0])) best = i; });
    return best;
  }

  function nameOf(ctx, i) {
    return (ctx.nameOf && ctx.nameOf(i)) || (ctx.seatName && ctx.seatName(i)) || 'місце ' + (i + 1);
  }

  /// Мить стенду за нашим годинником.
  function clock(st) {
    if (st.offset == null) return st.frameNow;
    return performance.now() - st.offset;
  }

  function newStand(st, f) {
    st.stand = f.st;
    st.k = f.k;
    st.tg.clear();
    st.sent = [];
    st.base = 0;
    st.seen.clear();
    st.holes.length = 0;
    st.offs.length = 0;
    st.offset = null;
    st.srv = null;
    st.pred = freshCopy(st.R);
  }

  function resetGame(st) {
    st.stand = -1;
    st.last = null;
    st.fx.length = 0;
    st.banner = null;
    st.prevScores = [];
    newStand(st, { st: -1, k: 0 });
  }

  /// Кадр сервера: годинник, розклад, правда про копії, звірка передбачення.
  function onFrame(st, f) {
    if (!f) return;
    const was = st.last;
    if (f.ph === 3) { if (!was || was.ph !== 3) resetGame(st); st.last = f; st.ph = 3; return; }
    if (was && was.ph === 2 && f.ph !== 2) resetGame(st);            // «Ще раз»
    if (f.st !== st.stand) {
      const prev = st.stand;
      // Кадр нового стенду приходить раніше за вид, тож очки минулого стенду беремо зі своєї копії до скидання.
      const prevScore = prev >= 0 && meOf(st) != null && st.srv ? st.pred.sscore : null;
      newStand(st, f);
      if (f.ph === 0) st.banner = { at: performance.now(), stand: f.st, prev, prevScore };
    }
    st.last = f;
    st.ph = f.ph;
    st.k = f.k;
    st.frameNow = f.now;
    st.frameAt = performance.now();
    if (f.ph !== 2) {
      st.offs.push(st.frameAt - f.now);
      if (st.offs.length > 12) st.offs.shift();
      st.offset = Math.min.apply(null, st.offs);
    }
    for (const a of f.tg || []) {
      if (!st.tg.has(a[0])) st.tg.set(a[0], { id: a[0], kind: a[1], t0: a[2], dur: a[3], x0: a[4], y0: a[5], vx: a[6], r: a[7] });
    }
    const T = clock(st);
    for (const [id, tg] of st.tg) if (tg.t0 + tg.dur < T - 3000) st.tg.delete(id);
    reconcile(st);
    // Чужі влучання (глядач дивиться чиюсь копію) і ті наші, що сервер зарахував без передбачення, — теж бахкають.
    const who = shown(st);
    const h = who != null && f.h ? f.h[who] : null;
    if (h) {
      for (const id of h) {
        if (st.seen.has(id)) continue;
        st.seen.add(id);
        // Після F5 чи переходу на іншого стрільця старі влучання не бахкають — лише свіжі.
        const tg = st.tg.get(id);
        if (tg && T - (tg.t0 + tg.dur) < 300) burst(st, tg, clamp(T, tg.t0, tg.t0 + tg.dur - 1), st.R.points[tg.kind], false);
      }
    }
    scoresChanged(st, f);
  }

  /// Правда з кадру + наші дії, яких сервер ще не врахував.
  function reconcile(st) {
    const me = meOf(st), f = st.last;
    if (me == null || !f || !f.p || !f.p[me]) { st.pred = freshCopy(st.R); return; }
    const p = f.p[me], n = p[5];
    // Після F5 сервер знає дії, яких ми не слали з цієї вкладки: місця під них займаємо порожніми.
    while (st.sent.length < n) st.sent.unshift({ t: -Infinity, ghost: true });
    const now = performance.now();
    while (st.sent.length > n && now - st.sent[n].at > LOST_MS) st.sent.splice(n, 1);
    const s = {
      score: p[0], sscore: p[1], ammo: p[2], reloadAt: p[3], smoke: p[4], n,
      lastT: n > 0 ? st.sent[n - 1].t : -Infinity, hits: new Set((f.h && f.h[me]) || []),
    };
    st.srv = s;
    const pr = cloneCopy(s);
    for (let i = n; i < st.sent.length; i++) {
      const a = st.sent[i];
      if (a.kind === 'reload') simReload(pr, a.t, st.R); else simShot(pr, st.tg, a.t, a.x, a.y, st.R);
    }
    st.pred = pr;
  }

  function scoresChanged(st, f) {
    const p = f.p || [];
    const me = meOf(st);
    p.forEach((x, i) => {
      if (!x || i === me) return;
      const old = st.prevScores[i];
      if (old != null && old !== x[0]) st.flash[i] = { at: performance.now(), up: x[0] > old };
      st.prevScores[i] = x[0];
    });
  }

  // ---------- постріл ----------

  function fieldPoint(st, ev) {
    const r = st.cv.el.getBoundingClientRect();
    return { x: (ev.clientX - r.left) / r.width * st.R.w, y: (ev.clientY - r.top) / r.height * st.R.h };
  }

  function canShoot(st) {
    const c = st.ctx;
    return !!(c && c.mine && c.playing && st.ph === 1 && st.last && st.last.ph === 1);
  }

  function fire(st, x, y) {
    const R = st.R;
    if (st.ph === 3 || !st.last) return practice(st, x, y);
    if (!st.ctx.mine || !st.ctx.playing) return;
    if (st.ph === 0 || clock(st) < 0) { say(st, 'Ще не стріляємо — готуйсь!'); return; }
    if (!canShoot(st)) return;
    const T = Math.round(clock(st));
    if (T < 0 || T >= R.standMs) return;
    const s = cloneCopy(st.pred);
    const res = simShot(s, st.tg, T, x, y, R);
    if (!res) {
      if (T < st.pred.smoke) say(st, 'Дим — нічого не видно!');
      else if (reloading(st.pred, T)) say(st, 'Перезаряджаєш…');
      return;
    }
    st.sent.push({ kind: 'shot', t: T, x, y, at: performance.now() });
    st.pred = s;
    st.ctx.input('shot', { s: st.stand, t: T, x: Math.round(x * 10) / 10, y: Math.round(y * 10) / 10 });
    recoil(st);
    if (res.reload) { say(st, 'Клац! Порожньо — перезаряджаю'); return; }
    if (res.miss) { hole(st, x, y); return; }
    st.seen.add(res.hit.id);
    burst(st, res.hit, T, res.d, true, x, y);
  }

  function reload(st) {
    if (!canShoot(st)) return;
    const T = Math.round(clock(st));
    const s = cloneCopy(st.pred);
    if (!simReload(s, T, st.R)) {
      if (st.pred.ammo >= st.R.drum && !reloading(st.pred, T)) say(st, 'Барабан і так повний');
      return;
    }
    st.sent.push({ kind: 'reload', t: T, at: performance.now() });
    st.pred = s;
    st.ctx.input('reload', { s: st.stand, t: T });
  }

  /// У лобі можна пристрілятись по іграшкових мішенях — нічого на сервер не летить.
  function practice(st, x, y) {
    const T = performance.now();
    const hit = demoTargets(T).find((tg) => (xAt(tg, T) - x) ** 2 + (tg.y0 - y) ** 2 <= (tg.r + 6) ** 2 && !st.seen.has(tg.id));
    recoil(st);
    if (!hit) { hole(st, x, y); return; }
    st.seen.add(hit.id);
    burst(st, hit, T, st.R.points[hit.kind], true, x, y, T);
  }

  function say(st, text) { st.nudge = { text, at: performance.now() }; }
  function recoil(st) { st.kick = performance.now(); }

  // ---------- ефекти ----------

  const SHARD = [['#c5763a', '#8a4a22', '#e8a46a'], ['#ffd23f', '#f29f05', '#fff1a8'], ['#f4c542', '#fff3b0', '#c99a1a'],
    ['#6b3e1f', '#3b2210', '#f08a24'], ['#2f5fa8', '#f4f1e8', '#d8402f']];

  function hole(st, x, y) {
    st.holes.push({ x, y, at: performance.now() });
    if (st.holes.length > 24) st.holes.shift();
    st.fx.push({ type: 'dust', x, y, at: performance.now(), life: 350 });
  }

  /// Мішень розлетілась: черепки, спалах, напис з очками. Діжка — ще й вибух.
  function burst(st, tg, t, d, mine, px, py, clockAt) {
    const now = performance.now();
    const x = xAt(tg, clockAt != null ? clockAt : t), y = tg.y0;
    const cols = SHARD[tg.kind];
    const n = tg.kind === KEG ? 16 : 11;
    for (let i = 0; i < n; i++) {
      const a = Math.random() * Math.PI * 2, v = 160 + Math.random() * 320;
      st.fx.push({ type: 'shard', x, y, vx: Math.cos(a) * v + tg.vx * 0.5, vy: Math.sin(a) * v - 260, rot: Math.random() * 6,
        vr: (Math.random() - 0.5) * 18, size: 6 + Math.random() * 9, col: cols[i % cols.length], at: now, life: 900 });
    }
    st.fx.push({ type: 'ring', x, y, at: now, life: 320, col: d > 0 ? '#fff6c8' : '#ff8a65' });
    if (tg.kind === KEG) {
      st.fx.push({ type: 'boom', x, y, at: now, life: 600 });
      for (let i = 0; i < 9; i++) st.fx.push({ type: 'puff', x: x + (Math.random() - 0.5) * 90, y: y + (Math.random() - 0.5) * 60,
        vx: (Math.random() - 0.5) * 60, vy: -30 - Math.random() * 40, size: 40 + Math.random() * 40, at: now, life: 1400 });
    }
    const txt = sign(d) + (tg.kind === POT ? ' ой, бабин!' : tg.kind === KEG ? ' бабах!' : tg.kind === GOLD ? ' золотий!' : '');
    st.fx.push({ type: 'text', x, y: y - tg.r - 6, text: txt, good: d > 0, big: mine, at: now, life: 950 });
    if (st.fx.length > 220) st.fx.splice(0, st.fx.length - 220);
  }

  // ---------- іграшкові мішені лобі ----------

  function hash(n) { n = (n ^ 61) ^ (n >>> 16); n = n + (n << 3); n ^= n >>> 4; n = Math.imul(n, 0x27d4eb2d); return (n ^ (n >>> 15)) >>> 0; }

  /// Лобі живе: глеки вискакують, качки пливуть — за годинником сторінки, детерміновано, щоб не мерехтіло.
  function demoTargets(T) {
    const out = [], P = 1700;
    for (let j = 0; j < 3; j++) {
      const cyc = Math.floor((T + j * 567) / P), hsh = hash(cyc * 7 + j * 131);
      const slot = hsh % 8, row = (hsh >> 4) % 2, roll = (hsh >> 8) % 10;
      const kind = roll === 0 ? POT : roll === 1 ? GOLD : roll === 2 ? KEG : JUG;
      out.push({ id: -1 - (cyc * 3 + j), kind, t0: cyc * P - j * 567, dur: 1300, x0: DEF.slotX[slot], y0: DEF.shelfY[row], vx: 0, r: DEF.radius[kind] });
    }
    for (let lane = 0; lane < 2; lane++) {
      const v = lane === 0 ? 150 : -150, span = 1300;
      for (let k = 0; k < 3; k++) {
        const ph = ((T / 1000) * Math.abs(v) + k * (span / 3) + lane * 200) % span;
        const cyc = Math.floor(((T / 1000) * Math.abs(v) + k * (span / 3) + lane * 200) / span);
        const kind = hash(cyc * 11 + k + lane * 5) % 6 === 0 ? KEG : DUCK;
        const x = lane === 0 ? -150 + ph : 1150 - ph;
        out.push({ id: -1000 - (cyc * 6 + lane * 3 + k), kind, t0: T - 1, dur: 9e9, x0: x, y0: DEF.laneY[lane], vx: 0, dir: v, r: DEF.radius[kind] });
      }
    }
    return out;
  }

  // ---------- малювання ----------

  /// Тло (тент, стіна, полиці, вода без хвиль) малюємо раз на розмір у позаекранне полотно.
  function background(st) {
    const el = st.cv.el, key = el.width + 'x' + el.height;
    if (st.bg && st.bgKey === key) return st.bg;
    const bg = st.bg || document.createElement('canvas');
    bg.width = el.width;
    bg.height = el.height;
    const g = bg.getContext('2d'), R = st.R, s = el.width / R.w;
    g.setTransform(s, 0, 0, s, 0, 0);
    // задня стіна — дошки
    const wall = g.createLinearGradient(0, 0, 0, 380);
    wall.addColorStop(0, '#5b2d1b');
    wall.addColorStop(1, '#3a1d12');
    g.fillStyle = wall;
    g.fillRect(0, 0, R.w, R.h);
    g.strokeStyle = 'rgba(0,0,0,.22)';
    g.lineWidth = 2;
    for (let x = 0; x <= R.w; x += 62) { g.beginPath(); g.moveTo(x, 60); g.lineTo(x, 380); g.stroke(); }
    // мішені-кружала на стіні — для настрою
    for (const [cx, cy] of [[70, 230], [930, 230]]) {
      for (let i = 4; i > 0; i--) { g.fillStyle = i % 2 ? '#e9dcc0' : '#b5372b'; g.beginPath(); g.arc(cx, cy, i * 11, 0, 7); g.fill(); }
    }
    // тент: червоно-кремові смуги з фестонами
    const sw = 1000 / 14;
    for (let i = 0; i < 14; i++) {
      g.fillStyle = i % 2 ? '#f3e6c9' : '#c2392b';
      g.fillRect(i * sw, 0, sw + 1, 58);
      g.beginPath();
      g.arc(i * sw + sw / 2, 58, sw / 2, 0, Math.PI);
      g.fill();
    }
    g.fillStyle = 'rgba(0,0,0,.18)';
    g.fillRect(0, 50, R.w, 8);
    // вода
    const wat = g.createLinearGradient(0, 380, 0, 600);
    wat.addColorStop(0, '#1f6f8b');
    wat.addColorStop(1, '#0d3b52');
    g.fillStyle = wat;
    g.fillRect(0, 380, R.w, 220);
    // прилавок унизу
    g.fillStyle = '#7a4a26';
    g.fillRect(0, 578, R.w, 22);
    g.fillStyle = '#a0683a';
    g.fillRect(0, 578, R.w, 4);
    // боки-завіси
    for (const left of [true, false]) {
      const x0 = left ? 0 : R.w - 34;
      const cg = g.createLinearGradient(x0, 0, x0 + 34, 0);
      cg.addColorStop(left ? 0 : 1, '#7d1e16');
      cg.addColorStop(left ? 1 : 0, '#b5372b');
      g.fillStyle = cg;
      g.fillRect(x0, 58, 34, 520);
    }
    st.bg = bg;
    st.bgKey = key;
    return bg;
  }

  function plank(g, y, R) {
    g.fillStyle = '#8b5a2b';
    g.fillRect(30, y, R.w - 60, 24);
    g.fillStyle = '#b07a42';
    g.fillRect(30, y, R.w - 60, 5);
    g.fillStyle = 'rgba(0,0,0,.25)';
    g.fillRect(30, y + 24, R.w - 60, 6);
  }

  function waves(g, y, T, amp, col, R) {
    g.fillStyle = col;
    g.beginPath();
    g.moveTo(0, R.h);
    for (let x = 0; x <= R.w; x += 25) g.lineTo(x, y + Math.sin(x / 38 + T / 420) * amp + Math.sin(x / 17 - T / 300) * amp * 0.4);
    g.lineTo(R.w, R.h);
    g.closePath();
    g.fill();
  }

  /// Мішень у точці (x, y) радіусом r. dir — куди дивиться качка.
  function sprite(g, kind, x, y, r, T, dir) {
    g.save();
    g.translate(x, y);
    const k = r / 36;
    g.scale(k, k);
    if (kind === JUG || kind === GOLD) {
      if (kind === GOLD) {
        const glow = g.createRadialGradient(0, 4, 8, 0, 4, 62);
        glow.addColorStop(0, 'rgba(255,236,140,.75)');
        glow.addColorStop(1, 'rgba(255,236,140,0)');
        g.fillStyle = glow;
        g.beginPath(); g.arc(0, 4, 62, 0, 7); g.fill();
      }
      const body = kind === GOLD ? ['#ffe27a', '#d9a21b', '#fff6c9'] : ['#d9884a', '#9a5226', '#f0b783'];
      // вушко
      g.strokeStyle = body[1];
      g.lineWidth = 6;
      g.beginPath(); g.arc(20, -6, 12, -1.2, 1.5); g.stroke();
      // тулуб
      const bg = g.createRadialGradient(-10, 0, 4, 0, 8, 34);
      bg.addColorStop(0, body[2]);
      bg.addColorStop(0.45, body[0]);
      bg.addColorStop(1, body[1]);
      g.fillStyle = bg;
      g.beginPath();
      g.moveTo(-12, -22);
      g.bezierCurveTo(-14, -12, -32, -8, -30, 12);
      g.bezierCurveTo(-28, 32, -14, 36, 0, 36);
      g.bezierCurveTo(14, 36, 28, 32, 30, 12);
      g.bezierCurveTo(32, -8, 14, -12, 12, -22);
      g.closePath();
      g.fill();
      // шийка й вінця
      g.fillStyle = body[1];
      g.fillRect(-13, -30, 26, 9);
      g.fillStyle = body[0];
      g.fillRect(-15, -33, 30, 5);
      // поясок
      g.strokeStyle = kind === GOLD ? '#fff3b0' : '#f3e6c9';
      g.lineWidth = 3;
      g.beginPath(); g.moveTo(-27, 6); g.quadraticCurveTo(0, 12, 27, 6); g.stroke();
      if (kind === GOLD) {
        const a = T / 160;
        g.fillStyle = '#fffbe6';
        for (let i = 0; i < 2; i++) {
          const sx = Math.cos(a + i * 3) * 24, sy = -6 + Math.sin(a * 1.3 + i * 2) * 18, sr = 4 + 2 * Math.sin(a * 2 + i);
          g.beginPath();
          g.moveTo(sx, sy - sr * 2); g.lineTo(sx + sr * 0.5, sy); g.lineTo(sx, sy + sr * 2); g.lineTo(sx - sr * 0.5, sy); g.closePath();
          g.moveTo(sx - sr * 2, sy); g.lineTo(sx, sy + sr * 0.5); g.lineTo(sx + sr * 2, sy); g.lineTo(sx, sy - sr * 0.5); g.closePath();
          g.fill();
        }
      }
    } else if (kind === DUCK) {
      if (dir < 0) g.scale(-1, 1);
      g.fillStyle = '#f2b705';
      g.beginPath(); g.ellipse(-4, 10, 30, 18, 0, 0, 7); g.fill();          // тулуб
      g.beginPath(); g.moveTo(-30, 4); g.lineTo(-42, -6); g.lineTo(-30, 14); g.fill();   // хвіст
      g.fillStyle = '#ffd23f';
      g.beginPath(); g.ellipse(-2, 6, 26, 14, 0, 0, 7); g.fill();
      g.beginPath(); g.arc(18, -14, 14, 0, 7); g.fill();                   // голова
      g.fillStyle = '#f08a24';
      g.beginPath(); g.moveTo(30, -16); g.lineTo(44, -12); g.lineTo(30, -8); g.fill();   // дзьоб
      g.fillStyle = '#1b1b1b';
      g.beginPath(); g.arc(22, -18, 2.6, 0, 7); g.fill();
      // кружало на боці — ярмаркова качка
      g.fillStyle = '#c2392b'; g.beginPath(); g.arc(-6, 8, 10, 0, 7); g.fill();
      g.fillStyle = '#f3e6c9'; g.beginPath(); g.arc(-6, 8, 6, 0, 7); g.fill();
      g.fillStyle = '#c2392b'; g.beginPath(); g.arc(-6, 8, 2.6, 0, 7); g.fill();
    } else if (kind === KEG) {
      // гніт із вогником
      g.strokeStyle = '#3b2a1a';
      g.lineWidth = 3;
      g.beginPath(); g.moveTo(4, -32); g.quadraticCurveTo(10, -42, 6, -48); g.stroke();
      const fl = 4 + 2.5 * Math.sin(T / 45);
      g.fillStyle = '#ffd54f'; g.beginPath(); g.arc(6, -49, fl, 0, 7); g.fill();
      g.fillStyle = '#ff7043'; g.beginPath(); g.arc(6, -49, fl * 0.55, 0, 7); g.fill();
      // діжка
      const kg = g.createLinearGradient(-30, 0, 30, 0);
      kg.addColorStop(0, '#4a2a14'); kg.addColorStop(0.45, '#8a5a2e'); kg.addColorStop(1, '#3b2210');
      g.fillStyle = kg;
      g.beginPath();
      g.moveTo(-24, -32); g.quadraticCurveTo(-36, 0, -24, 34); g.lineTo(24, 34); g.quadraticCurveTo(36, 0, 24, -32); g.closePath();
      g.fill();
      g.fillStyle = '#2a2a2a';
      g.fillRect(-29, -20, 58, 6);
      g.fillRect(-29, 16, 58, 6);
      // чорна мітка пороху
      g.fillStyle = '#111';
      g.beginPath(); g.arc(0, -1, 11, 0, 7); g.fill();
      g.strokeStyle = '#f3e6c9';
      g.lineWidth = 2.6;
      g.beginPath(); g.moveTo(-6, -7); g.lineTo(6, 5); g.moveTo(6, -7); g.lineTo(-6, 5); g.stroke();
    } else if (kind === POT) {
      // бабин горщик: синя полива, квіти, накривка з ґудзиком
      const pg = g.createRadialGradient(-10, -2, 4, 0, 6, 38);
      pg.addColorStop(0, '#6f9be0'); pg.addColorStop(0.5, '#2f5fa8'); pg.addColorStop(1, '#1b3a6e');
      g.fillStyle = pg;
      g.beginPath();
      g.moveTo(-26, -20); g.bezierCurveTo(-42, -10, -40, 30, -18, 34); g.lineTo(18, 34);
      g.bezierCurveTo(40, 30, 42, -10, 26, -20); g.closePath();
      g.fill();
      g.fillStyle = '#1b3a6e';
      g.fillRect(-28, -26, 56, 8);
      g.fillStyle = '#3a6cc0';
      g.beginPath(); g.ellipse(0, -28, 24, 7, 0, Math.PI, 0); g.fill();
      g.fillStyle = '#f4c542'; g.beginPath(); g.arc(0, -36, 4.5, 0, 7); g.fill();
      for (const [fx, fy, fr] of [[-14, 6, 7], [14, 8, 6], [0, 18, 5]]) {
        g.fillStyle = '#f4f1e8';
        for (let i = 0; i < 5; i++) { const a = i * 1.2566; g.beginPath(); g.arc(fx + Math.cos(a) * fr * 0.8, fy + Math.sin(a) * fr * 0.8, fr * 0.5, 0, 7); g.fill(); }
        g.fillStyle = '#d8402f'; g.beginPath(); g.arc(fx, fy, fr * 0.42, 0, 7); g.fill();
      }
    }
    g.restore();
  }

  /// Мішені одного ряду полиці (вискакують з-за дошки) — обрізані по верх дошки.
  function shelfRow(st, g, list, T, y, R) {
    const top = y + 40;
    g.save();
    g.beginPath();
    g.rect(0, y - 140, R.w, 180);
    g.clip();
    for (const tg of list) {
      const rise = back((T - tg.t0) / 170), sink = ease((tg.t0 + tg.dur - T) / 150);
      const vis = Math.min(rise, sink);
      const dy = (1 - vis) * tg.r * 2.4;
      sprite(g, tg.kind, tg.x0, tg.y0 + dy, tg.r, T, 1);
    }
    g.restore();
    plank(g, top, R);
  }

  function draw(st) {
    const cv = st.cv, g = cv.ctx, R = st.R, el = cv.el;
    if (el.width === 0) return;
    const s = el.width / R.w, now = performance.now();
    g.setTransform(1, 0, 0, 1, 0, 0);
    g.drawImage(background(st), 0, 0);
    g.setTransform(s, 0, 0, s, 0, 0);
    const lobby = st.ph === 3 || !st.last;
    const T = lobby ? now : clock(st);
    const who = shown(st);
    const me = meOf(st);
    const hits = me != null ? st.pred.hits : new Set((who != null && st.last && st.last.h && st.last.h[who]) || []);
    const list = lobby ? demoTargets(T).filter((t) => !st.seen.has(t.id)) : [...st.tg.values()].filter((t) => alive(t, T) && !hits.has(t.id));
    // гірлянда
    for (let i = 0; i < 16; i++) {
      const x = 40 + i * 61, y = 74 + Math.sin(i * 0.9) * 5, on = ((Math.floor(now / 380) + i) % 3) !== 0;
      g.fillStyle = on ? ['#ffd54f', '#ff8a65', '#81d4fa', '#aed581'][i % 4] : 'rgba(255,255,255,.15)';
      g.beginPath(); g.arc(x, y, 5, 0, 7); g.fill();
    }
    // діри від куль
    for (let i = st.holes.length - 1; i >= 0; i--) {
      const h = st.holes[i], a = 1 - (now - h.at) / 2600;
      if (a <= 0) { st.holes.splice(i, 1); continue; }
      g.fillStyle = 'rgba(20,10,5,' + (0.85 * a) + ')';
      g.beginPath(); g.arc(h.x, h.y, 4.5, 0, 7); g.fill();
      g.strokeStyle = 'rgba(255,230,190,' + (0.35 * a) + ')';
      g.lineWidth = 1.5;
      g.beginPath(); g.arc(h.x, h.y, 7, 0, 7); g.stroke();
    }
    // полиці
    const rows = [[], []];
    const water = [[], []];
    for (const tg of list) {
      if (tg.vx !== 0 || tg.dir) water[Math.abs(tg.y0 - R.laneY[0]) < Math.abs(tg.y0 - R.laneY[1]) ? 0 : 1].push(tg);
      else rows[Math.abs(tg.y0 - R.shelfY[0]) < Math.abs(tg.y0 - R.shelfY[1]) ? 0 : 1].push(tg);
    }
    shelfRow(st, g, rows[0], T, R.shelfY[0], R);
    shelfRow(st, g, rows[1], T, R.shelfY[1], R);
    // вода: хвиля за качками, качки, хвиля попереду
    waves(g, R.laneY[0] - 34, now, 4, 'rgba(255,255,255,.06)', R);
    for (let lane = 0; lane < 2; lane++) {
      for (const tg of water[lane]) {
        const bob = Math.sin((now + tg.id * 137) / 260) * 3;
        sprite(g, tg.kind, lobby ? tg.x0 : xAt(tg, T), tg.y0 + bob, tg.r, now, tg.dir || tg.vx);
      }
      waves(g, R.laneY[lane] + 22, now + lane * 900, 5, lane ? 'rgba(13,59,82,.92)' : 'rgba(25,95,122,.9)', R);
    }
    g.fillStyle = '#7a4a26';
    g.fillRect(0, 578, R.w, 22);
    g.fillStyle = '#a0683a';
    g.fillRect(0, 578, R.w, 4);
    drawFx(st, g, now);
    // дим від діжки
    const sm = who != null ? (me != null ? st.pred.smoke : (st.last && st.last.p && st.last.p[who] ? st.last.p[who][4] : -1)) : -1;
    // smokeUntil = −1 — диму нема; на «Готуйсь» мить стенду від'ємна, тож без T ≥ 0 «дим» висів би весь відлік.
    if (!lobby && st.ph === 1 && T >= 0 && T < sm) {
      // Дим справді застилає: мішені ледь видно, стріляти однаково не дадуть.
      const left = sm - T, a = Math.min(1, left / 350);
      g.fillStyle = 'rgba(105,105,100,' + (0.9 * a) + ')';
      g.fillRect(0, 0, R.w, R.h);
      for (let i = 0; i < 9; i++) {
        const cx = ((i * 173 + now * 0.04 * (i % 2 ? 1 : -1)) % 1200 + 1200) % 1200 - 100, cy = 90 + (i * 71) % 440, rr = 110 + (i % 3) * 35;
        const pg = g.createRadialGradient(cx, cy, 10, cx, cy, rr);
        pg.addColorStop(0, 'rgba(190,190,182,' + (0.75 * a) + ')');
        pg.addColorStop(1, 'rgba(150,150,145,0)');
        g.fillStyle = pg;
        g.beginPath(); g.arc(cx, cy, rr, 0, 7); g.fill();
      }
      big(g, '💨 Дим! ' + (left / 1000).toFixed(1).replace('.', ',') + ' с', 500, 300, 46, '#fff', R);
    }
    overlay(st, g, T, now, R);
    drawAim(st, g, T, now, R);
  }

  function drawFx(st, g, now) {
    for (let i = st.fx.length - 1; i >= 0; i--) {
      const f = st.fx[i], u = (now - f.at) / f.life;
      if (u >= 1) { st.fx.splice(i, 1); continue; }
      const dt = (now - f.at) / 1000;
      if (f.type === 'shard') {
        const x = f.x + f.vx * dt, y = f.y + f.vy * dt + 900 * dt * dt;
        g.save();
        g.globalAlpha = 1 - u * u;
        g.translate(x, y); g.rotate(f.rot + f.vr * dt);
        g.fillStyle = f.col;
        g.beginPath(); g.moveTo(-f.size / 2, -f.size / 3); g.lineTo(f.size / 2, -f.size / 4); g.lineTo(0, f.size / 2); g.closePath(); g.fill();
        g.restore();
      } else if (f.type === 'ring') {
        g.strokeStyle = f.col; g.globalAlpha = 1 - u; g.lineWidth = 4;
        g.beginPath(); g.arc(f.x, f.y, 20 + u * 50, 0, 7); g.stroke(); g.globalAlpha = 1;
      } else if (f.type === 'boom') {
        const rr = 30 + ease(u) * 120;
        const bg = g.createRadialGradient(f.x, f.y, 0, f.x, f.y, rr);
        bg.addColorStop(0, 'rgba(255,250,200,' + (1 - u) + ')');
        bg.addColorStop(0.4, 'rgba(255,152,0,' + (0.9 * (1 - u)) + ')');
        bg.addColorStop(1, 'rgba(200,40,0,0)');
        g.fillStyle = bg; g.beginPath(); g.arc(f.x, f.y, rr, 0, 7); g.fill();
      } else if (f.type === 'puff') {
        g.fillStyle = 'rgba(90,90,90,' + (0.55 * (1 - u)) + ')';
        g.beginPath(); g.arc(f.x + f.vx * dt, f.y + f.vy * dt, f.size * (0.6 + u), 0, 7); g.fill();
      } else if (f.type === 'dust') {
        g.fillStyle = 'rgba(230,210,180,' + (0.6 * (1 - u)) + ')';
        g.beginPath(); g.arc(f.x, f.y, 6 + u * 16, 0, 7); g.fill();
      } else if (f.type === 'text') {
        g.save();
        g.globalAlpha = u < 0.7 ? 1 : 1 - (u - 0.7) / 0.3;
        const sz = (f.big ? 40 : 28) * (u < 0.15 ? 0.6 + u / 0.15 * 0.4 : 1);
        g.font = '800 ' + sz + 'px ' + 'system-ui, sans-serif';
        g.textAlign = 'center'; g.textBaseline = 'middle';
        g.lineWidth = 6; g.strokeStyle = 'rgba(0,0,0,.65)';
        const y = f.y - ease(u) * 70;
        g.strokeText(f.text, clamp(f.x, 110, 890), y);
        g.fillStyle = f.good ? '#b9f6ca' : '#ff8a80';
        g.fillText(f.text, clamp(f.x, 110, 890), y);
        g.restore();
      }
    }
  }

  function big(g, text, x, y, size, col, R) {
    g.save();
    g.font = '800 ' + size + 'px system-ui, sans-serif';
    g.textAlign = 'center'; g.textBaseline = 'middle';
    g.lineWidth = Math.max(4, size / 7); g.strokeStyle = 'rgba(0,0,0,.7)';
    g.strokeText(text, clamp(x, 60, R.w - 60), y);
    g.fillStyle = col;
    g.fillText(text, clamp(x, 60, R.w - 60), y);
    g.restore();
  }

  /// Відлік «Готуйсь», «Пли!», «Стоп!», підсумок і смужка часу стенду.
  function overlay(st, g, T, now, R) {
    const v = st.ctx.view || {};
    if (st.ph === 3 || !st.last) return;
    const stands = v.stands || 1;
    if (st.ph === 2) {
      g.fillStyle = 'rgba(10,20,15,.55)'; g.fillRect(0, 0, R.w, R.h);
      const w = (v.winners || []);
      const best = w.length === 1 ? nameOf(st.ctx, w[0]) : '';
      big(g, 'Ярмарок закрито!', 500, 250, 64, '#ffe082', R);
      if (best && (v.mode !== 'daily')) big(g, '🎯 ' + best, 500, 335, 40, '#fff', R);
      if (v.mode === 'daily') {
        const me = meOf(st);
        const sc = v.score && me != null ? v.score[me] : null;
        if (sc != null) big(g, sc + ' ' + ochok(sc), 500, 335, 44, '#fff', R);
      }
      return;
    }
    // смужка часу під тентом
    if (T >= 0) {
      const left = clamp(1 - T / R.standMs, 0, 1);
      g.fillStyle = 'rgba(0,0,0,.35)'; g.fillRect(40, 92, R.w - 80, 10);
      g.fillStyle = left < 0.15 ? '#ff7043' : '#f4c542'; g.fillRect(40, 92, (R.w - 80) * left, 10);
    }
    if (T < 0) {
      g.fillStyle = 'rgba(10,20,15,.5)'; g.fillRect(0, 0, R.w, R.h);
      const n = Math.ceil(-T / 1000);
      const sub = (st.stand + 1) + ' з ' + stands;
      big(g, (stands > 1 ? 'Стенд ' + sub + ': ' : '') + ((v.standNames || [])[st.stand] || ''), 500, 170, 40, '#ffe082', R);
      big(g, STAND_TIP[st.k] || '', 500, 225, 26, '#fff', R);
      const u = (-T % 1000) / 1000;
      big(g, String(clamp(n, 1, 9)), 500, 340, 90 + 40 * u, '#fff', R);
      const b = st.banner;
      if (b && b.prevScore != null && b.prev >= 0) big(g, 'За стенд: ' + sign(b.prevScore), 500, 440, 30, '#b9f6ca', R);
      if (v.max) big(g, 'Хорошими мішенями можна взяти до ' + v.max, 500, 490, 22, 'rgba(255,255,255,.8)', R);
    } else if (T < 600) {
      g.save(); g.globalAlpha = 1 - T / 600;
      big(g, 'Пли!', 500, 300, 110 + T / 10, '#ffe082', R);
      g.restore();
    } else if (T >= R.standMs) {
      big(g, 'Стоп!', 500, 300, 80, '#ffe082', R);
    }
    if (st.nudge && now - st.nudge.at < 1100) {
      g.save(); g.globalAlpha = 1 - Math.max(0, (now - st.nudge.at - 700) / 400);
      big(g, st.nudge.text, 500, 548, 30, '#fff', R);
      g.restore();
    }
  }

  function drawAim(st, g, T, now, R) {
    const a = st.aim;
    // Свій приціл — лише стрільцеві посеред партії; у лобі й на підсумку досить звичайного курсора.
    if (!a.on || !(st.ctx.mine && st.ctx.playing) || st.ph === 2 || st.ph === 3) return;
    const kick = st.kick ? Math.max(0, 1 - (now - st.kick) / 140) : 0;
    const rr = 22 + kick * 10;
    const p = st.pred;
    const rel = me(st) && reloading(p, T);
    g.save();
    g.strokeStyle = 'rgba(0,0,0,.6)'; g.lineWidth = 6;
    g.beginPath(); g.arc(a.x, a.y, rr, 0, 7); g.stroke();
    g.strokeStyle = rel ? '#90a4ae' : '#fff'; g.lineWidth = 2.6;
    g.beginPath(); g.arc(a.x, a.y, rr, 0, 7); g.stroke();
    g.beginPath();
    g.moveTo(a.x - rr - 10, a.y); g.lineTo(a.x - 8, a.y); g.moveTo(a.x + 8, a.y); g.lineTo(a.x + rr + 10, a.y);
    g.moveTo(a.x, a.y - rr - 10); g.lineTo(a.x, a.y - 8); g.moveTo(a.x, a.y + 8); g.lineTo(a.x, a.y + rr + 10);
    g.stroke();
    g.fillStyle = '#ff5252'; g.beginPath(); g.arc(a.x, a.y, 3, 0, 7); g.fill();
    if (rel) {
      const u = 1 - (p.reloadAt - T) / R.reloadMs;
      g.strokeStyle = '#f4c542'; g.lineWidth = 5;
      g.beginPath(); g.arc(a.x, a.y, rr + 9, -Math.PI / 2, -Math.PI / 2 + u * Math.PI * 2); g.stroke();
    }
    g.restore();
  }
  const me = (st) => meOf(st) != null;

  // ---------- HTML навколо полотна ----------

  function shell(root, st) {
    if (st.hud && st.hud.isConnected) return;
    root.classList.add('tyr');
    const hud = document.createElement('div');
    hud.className = 'tyrhud';
    hud.innerHTML = '<span class="tyrstand"></span><span class="tyrscore" title="Твої очки"></span><span class="tyrtime"></span>';
    const ctl = document.createElement('div');
    ctl.className = 'tyrctl';
    ctl.innerHTML = '<span class="tyrdrum" aria-label="набої"></span>'
      + '<button type="button" class="tyrrel" title="Перезарядити: правий клік, R або Ⓧ">⟳ Перезарядити</button>';
    const board = document.createElement('div');
    board.className = 'tyrboard';
    const leg = document.createElement('div');
    leg.className = 'tyrleg';
    const sum = document.createElement('div');
    sum.className = 'tyrsum';
    sum.hidden = true;
    root.prepend(hud);
    st.cv = HGames.ui.canvas(root, { w: VW, h: VH, cls: 'tyrfield' });
    root.insertBefore(st.cv.el, hud.nextSibling);
    st.cv.el.after(ctl, board, leg, sum);
    st.hud = hud; st.ctl = ctl; st.board = board; st.leg = leg; st.sum = sum;
    legend(st);
    wire(st);
  }

  function legend(st) {
    st.leg.innerHTML = '';
    const R = st.R;
    for (let k = 0; k < 5; k++) {
      const item = document.createElement('span');
      item.className = 'tyrlegi' + (R.points[k] < 0 ? ' bad' : '');
      const c = document.createElement('canvas');
      const dpr = Math.min(3, window.devicePixelRatio || 1);
      c.width = 34 * dpr; c.height = 34 * dpr;
      const g = c.getContext('2d');
      g.setTransform(dpr * 34 / 100, 0, 0, dpr * 34 / 100, 0, 0);
      sprite(g, k, 50, 54, 34, 600, 1);
      item.appendChild(c);
      const t = document.createElement('span');
      t.innerHTML = esc(KIND_NAME[k]) + ' <b>' + sign(R.points[k]) + '</b>' + (k === KEG ? ' і дим' : '');
      item.appendChild(t);
      st.leg.appendChild(item);
    }
  }

  function wire(st) {
    const el = st.cv.el;
    el.addEventListener('pointerdown', (e) => {
      if (!HGames.ui.human(e)) return;
      const p = fieldPoint(st, e);
      if (e.pointerType === 'mouse') { st.aim.x = p.x; st.aim.y = p.y; st.aim.on = true; st.aim.mode = 'mouse'; }
      if (e.button === 2) { e.preventDefault(); reload(st); return; }
      if (e.button !== 0) return;
      if (st.ctx.mine && st.ctx.playing) e.preventDefault();
      fire(st, p.x, p.y);
    });
    el.addEventListener('pointermove', (e) => {
      if (e.pointerType !== 'mouse') return;
      const p = fieldPoint(st, e);
      st.aim.x = p.x; st.aim.y = p.y; st.aim.on = true; st.aim.mode = 'mouse';
    });
    el.addEventListener('pointerleave', (e) => { if (e.pointerType === 'mouse' && st.aim.mode === 'mouse') st.aim.on = false; });
    el.addEventListener('contextmenu', (e) => e.preventDefault());
    st.ctl.querySelector('.tyrrel').addEventListener('click', () => reload(st));
    st.board.addEventListener('click', (e) => {
      const b = e.target.closest('[data-seat]');
      if (!b || meOf(st) != null) return;
      st.follow = +b.dataset.seat;
      st.seen = new Set((st.last && st.last.h && st.last.h[st.follow]) || []);
      paintBoard(st, true);
    });
    st.keyup = (e) => { const k = dirKey(e); if (k) st.held[k] = 0; };
    document.addEventListener('keyup', st.keyup);
    st.blur = () => { st.held.l = st.held.r = st.held.u = st.held.d = 0; };
    window.addEventListener('blur', st.blur);
  }

  function dirKey(e) {
    switch (e.code) {
      case 'ArrowLeft': case 'KeyA': return 'l';
      case 'ArrowRight': case 'KeyD': return 'r';
      case 'ArrowUp': case 'KeyW': return 'u';
      case 'ArrowDown': case 'KeyS': return 'd';
      default: return '';
    }
  }

  /// Шапка й набої — раз на ~100 мс і лише коли текст справді змінився.
  function paintHud(st, force) {
    const now = performance.now();
    if (!force && now - st.hudAt < 100) return;
    st.hudAt = now;
    const v = st.ctx.view || {}, R = st.R, ctx = st.ctx;
    const lobby = st.ph === 3 || !st.last;
    const T = clock(st);
    const stands = v.stands || 1;
    const standTxt = lobby ? (v.mode === 'daily' ? '☀ Тир дня' : '🎯 Ярмарковий тир') : st.ph === 2 ? 'Партію зіграно'
      : (stands > 1 ? 'Стенд ' + (st.stand + 1) + '/' + stands + ' · ' : '') + ((v.standNames || [])[st.stand] || '');
    const who = shown(st), mine = meOf(st) != null;
    const p = st.last && st.last.p && who != null ? st.last.p[who] : null;
    const score = mine ? st.pred.score : p ? p[0] : null;
    const scoreTxt = score == null ? '' : (mine ? '' : nameOf(ctx, who) + ': ') + '🎯 ' + score;
    const left = lobby || st.ph === 2 ? '' : T < 0 ? 'за ' + Math.ceil(-T / 1000) + ' с' : '⏱ ' + Math.max(0, Math.ceil((R.standMs - T) / 1000));
    set(st.hud.children[0], standTxt);
    set(st.hud.children[1], scoreTxt);
    set(st.hud.children[2], left);
    st.hud.children[2].classList.toggle('hot', !lobby && st.ph === 1 && T > R.standMs - 6000);
    // набої
    const showCtl = mine && ctx.playing && st.ph !== 2 && !lobby;
    st.ctl.hidden = !showCtl;
    if (showCtl) {
      const pr = st.pred, tt = Math.max(0, T);
      const rel = reloading(pr, tt);
      const ammo = pr.reloadAt > 0 && tt >= pr.reloadAt ? R.drum : pr.ammo;
      const key = ammo + '|' + (rel ? 1 : 0) + '|' + R.drum;
      const drum = st.ctl.children[0];
      if (drum.dataset.k !== key) {
        drum.dataset.k = key;
        let h = '';
        for (let i = 0; i < R.drum; i++) h += '<i class="' + (i < ammo ? 'on' : '') + '"></i>';
        drum.innerHTML = h + (rel ? '<span class="tyrrl">перезаряджаю…</span>' : ammo === 0 ? '<span class="tyrrl">порожньо!</span>' : '');
        drum.classList.toggle('empty', ammo === 0 && !rel);
      }
      st.ctl.children[1].disabled = rel || ammo >= R.drum || st.ph !== 1;
    }
    st.leg.hidden = !(lobby || st.ph === 0);
    paintBoard(st, false);
  }

  function set(el, txt) { if (el.textContent !== txt) el.textContent = txt; }

  /// Живі рахунки всіх, від кращого; свій — з передбачення, щоб не чекати сервера.
  function paintBoard(st, force) {
    const ctx = st.ctx, f = st.last, lobby = st.ph === 3 || !f;
    const v = ctx.view || {};
    const mine = meOf(st);
    let rows = [];
    if (!lobby && f.p) f.p.forEach((x, i) => { if (x) rows.push({ i, s: i === mine ? st.pred.score : x[0] }); });
    if (rows.length < 2 && v.mode === 'daily') { st.board.hidden = true; return; }
    st.board.hidden = rows.length === 0;
    rows.sort((a, b) => b.s - a.s || a.i - b.i);
    const now = performance.now();
    const who = shown(st);
    const html = rows.map((r, k) => {
      const fl = st.flash[r.i] && now - st.flash[r.i].at < 600 ? (st.flash[r.i].up ? ' up' : ' down') : '';
      return '<button type="button" class="tyrchip ty' + r.i + (r.i === mine ? ' me' : '') + (mine == null && r.i === who ? ' watch' : '') + fl
        + '" data-seat="' + r.i + '"' + (mine == null ? '' : ' tabindex="-1"') + '><i></i><span class="n">' + (k === 0 && r.s > 0 && (rows.length < 2 || rows[1].s < r.s) ? '👑 ' : '')
        + esc(nameOf(ctx, r.i)) + '</span><b>' + r.s + '</b></button>';
    }).join('');
    if (force || st.board._h !== html) { st.board._h = html; st.board.innerHTML = html; }
  }

  /// Підсумок партії: місця, очки за стенди, влучність; у «Тирі дня» — таблиця дня й «поділитись».
  function paintSum(st) {
    const v = st.ctx.view || {}, ctx = st.ctx;
    const over = v.phase === 'over';
    st.sum.hidden = !over;
    if (!over) { st.sum._k = ''; return; }
    const key = JSON.stringify([v.score, v.standScores, v.stats, v.daily, v.winners]);
    if (st.sum._k === key) return;
    st.sum._k = key;
    const rows = [];
    (v.score || []).forEach((s, i) => { if (s != null) rows.push(i); });
    rows.sort((a, b) => v.score[b] - v.score[a] || a - b);
    const mine = meOf(st);
    let h = '<table class="tyrtab"><thead><tr><th></th><th>Стрілець</th><th>Очки</th>'
      + ((v.stands || 1) > 1 ? '<th>' + (v.standNames || []).map((n, i) => 'С' + (i + 1)).join(' · ') + '</th>' : '')
      + '<th title="влучив / пострілів">Влучність</th><th title="золоті · діжки · бабині горщики">✨ · 💥 · 👵</th></tr></thead><tbody>';
    let place = 0, prev = null;
    rows.forEach((i, k) => {
      const s = v.score[i];
      if (s !== prev) { place = k + 1; prev = s; }
      const x = (v.stats && v.stats[i]) || [0, 0, 0, 0, 0, 0];
      const acc = x[0] > 0 ? Math.round(x[1] * 100 / x[0]) + '%' : '—';
      h += '<tr class="' + (i === mine ? 'me' : '') + '"><td>' + (place === 1 ? '🏆' : place) + '</td><td class="ty' + i + '"><i></i>' + esc(nameOf(ctx, i))
        + '</td><td><b>' + s + '</b></td>'
        + ((v.stands || 1) > 1 ? '<td class="muted">' + ((v.standScores && v.standScores[i]) || []).join(' · ') + '</td>' : '')
        + '<td>' + x[1] + '/' + x[0] + ' <span class="muted">' + acc + '</span></td><td>' + x[5] + ' · ' + x[3] + ' · ' + x[4] + '</td></tr>';
    });
    h += '</tbody></table>';
    const d = v.daily;
    if (d) {
      h += '<div class="tyrday"><div><b>☀ Тир дня №' + esc(d.no) + '</b>' + (d.place ? ' · ти ' + d.place + '-й з ' + d.players : '') + '</div>';
      if (d.board && d.board.length) {
        h += '<ol>' + d.board.map((r) => '<li' + (ctx.me && r.nick === ctx.me.nick ? ' class="me"' : '') + '><span>' + esc(r.nick) + '</span><b>' + r.points
          + '</b><span class="muted"> ' + r.hits + '/' + r.shots + '</span></li>').join('') + '</ol>';
      }
      if (d.share) h += '<button type="button" class="ghost tyrshare">📋 Поділитись</button>';
      h += '<div class="muted small">Нові мішені — завтра. Сьогодні в усіх ті самі.</div></div>';
    }
    st.sum.innerHTML = h;
    const sh = st.sum.querySelector('.tyrshare');
    if (sh) sh.addEventListener('click', () => {
      const txt = (v.daily && v.daily.share) || '';
      const ok = () => ctx.toast && ctx.toast('Скопійовано — кидай друзям');
      try { navigator.clipboard.writeText(txt).then(ok, () => ctx.toast && ctx.toast(txt)); } catch { if (ctx.toast) ctx.toast(txt); }
    });
  }

  // ---------- цикл ----------

  function step(st, now) {
    const dt = Math.min(0.05, (now - (st.stepAt || now)) / 1000);
    st.stepAt = now;
    const a = st.aim, R = st.R;
    let mx = (st.held.r ? 1 : 0) - (st.held.l ? 1 : 0), my = (st.held.d ? 1 : 0) - (st.held.u ? 1 : 0);
    // Аналоговий стік, якщо браузер його дає: приціл по діагоналі й тонко, а не чотирма стрілками pad.js.
    if (now - st.padAt < 60000 && navigator.getGamepads) {
      let pads = [];
      try { pads = navigator.getGamepads() || []; } catch { pads = []; }
      for (const gp of pads) {
        if (!gp || !gp.axes || gp.axes.length < 2) continue;
        const ax = gp.axes[0], ay = gp.axes[1], m = Math.hypot(ax, ay);
        // Крива: легкий нахил — тонке доведення, до упору — швидкий переліт через поле.
        if (m > 0.2) { const sp = Math.pow(Math.min(1, (m - 0.2) / 0.7), 1.6) * 1.3 / m; mx = ax * sp; my = ay * sp; st.analogAt = now; }
        break;
      }
    }
    if (mx || my) {
      if (!a.on || a.mode === 'mouse') { a.on = true; a.mode = 'keys'; }
      a.x = clamp(a.x + mx * AIM_SPEED * dt, 0, R.w);
      a.y = clamp(a.y + my * AIM_SPEED * dt, 0, R.h);
    }
  }

  function loop(st) {
    st.raf = 0;
    if (!st.cv || !st.cv.el.isConnected || st.root._tyr !== st) return;
    // Картка під display:none (пішли на «Ефір», інший стіл) чи вкладка схована — дрімаємо таймером, а не rAF.
    if (!st.cv.el.offsetParent || document.hidden) { st.tm = setTimeout(() => { st.tm = 0; loop(st); }, 300); st.raf = -1; return; }
    st.raf = requestAnimationFrame(() => loop(st));
    const now = performance.now();
    step(st, now);
    // у лобі й на підсумку — 30 кадрів на секунду вистачить
    if ((st.ph === 3 || st.ph === 2) && now - st.lastDraw < 32) return;
    st.lastDraw = now;
    // Сервер мовчить, а наш постріл так і не підтвердився — переграємо передбачення без нього (відхилено).
    if (st.sent.length > (st.srv ? st.srv.n : 0) && now - st.sent[st.sent.length - 1].at > LOST_MS) reconcile(st);
    draw(st);
    paintHud(st, false);
  }

  function padOn(btn, ctx) {
    const st = stOf(ctx) || lastSt;
    if (!st || !ctx.mine || !ctx.playing) return false;
    st.padAt = performance.now();
    if (btn === 'a') {
      if (!st.aim.on) { st.aim.on = true; st.aim.mode = 'keys'; }
      fire(st, st.aim.x, st.aim.y);
      return true;
    }
    if (btn === 'x') { reload(st); return true; }
    return false;
  }

  /// Один модуль на дві гри: стіл (tyr) і «Тир дня» (tyr-daily, Client: "tyr") — різняться лише id та іконкою.
  const MOD = {
    added: '2026-10-06',
    seatClass: ['ty0', 'ty1', 'ty2', 'ty3', 'ty4', 'ty5', 'ty6', 'ty7'],
    pad: { dirs: true, on: padOn, hint: '{dpad} приціл · {a} постріл · {x} перезарядка' },

    mount(root, ctx) {
      const st = state(root, ctx);
      lastSt = st;
      shell(root, st);
      if (ctx.view && ctx.view.frame) onFrame(st, ctx.view.frame);
      st.raf = requestAnimationFrame(() => loop(st));
    },

    update(root, ctx) {
      const st = state(root, ctx);
      lastSt = st;
      shell(root, st);
      const vf = ctx.view && ctx.view.frame;
      // Вид шлеться разом із кадром на зміні фази чи стенду; свіжіші кадри посеред стенду — окремо, тож старіший вид
      // не відкочує годинник.
      if (vf) {
        const l = st.last;
        if (!l || vf.ph === 3 || vf.ph === 2 || l.ph === 2 || vf.st !== l.st || vf.ph !== l.ph || vf.now >= l.now) onFrame(st, vf);
      }
      st.cv.el.classList.toggle('play', !!(ctx.mine && ctx.playing));
      st.cv.resize();
      paintHud(st, true);
      paintSum(st);
      if (!st.raf) st.raf = requestAnimationFrame(() => loop(st));
    },

    frame(root, ctx, f) {
      const st = state(root, ctx);
      onFrame(st, f);
    },

    onKey(e, ctx) {
      const st = stOf(ctx) || lastSt;
      if (!st || !ctx.mine || !ctx.playing) return false;
      const k = dirKey(e);
      if (k) {
        // Стік дає і аналог (getGamepads), і стрілки від pad.js — друге тоді зайве.
        if (e.hpad && performance.now() - st.analogAt < 400) return true;
        if (e.hpad) st.padAt = performance.now();
        st.held[k] = 1;
        return true;
      }
      if (e.code === 'Space' || e.code === 'Enter' || e.code === 'KeyF') {
        if (e.repeat) return true;
        if (!st.aim.on) { st.aim.on = true; st.aim.mode = 'keys'; }
        fire(st, st.aim.x, st.aim.y);
        return true;
      }
      if (e.code === 'KeyR') { reload(st); return true; }
      return false;
    },

    status(ctx) {
      const f = ctx.frame || (ctx.view && ctx.view.frame) || null;
      const v = ctx.view || {};
      if (!f || f.ph === 3) {
        if (ctx.room && ctx.room.status === 'lobby') return 'Клацни по мішенях — пристріляйся, поки чекаєте';
        return '';
      }
      if (f.ph === 2) return '';
      if (f.ph === 0) return 'Готуйсь: ' + ((v.standNames || [])[f.st] || 'стенд');
      if (!ctx.mine) return 'Дивишся збоку — клацни на рахунок, щоб стежити за стрільцем';
      return ctx.ui && ctx.ui.coarse && ctx.ui.coarse() ? 'Тапай по мішенях · ⟳ — перезарядка' : 'Клацай по мішенях · правий клік чи R — перезарядка';
    },

    unmount(root) {
      const st = root._tyr;
      if (!st) return;
      if (st.raf > 0) cancelAnimationFrame(st.raf);
      clearTimeout(st.tm);
      st.raf = 0;
      if (st.keyup) document.removeEventListener('keyup', st.keyup);
      if (st.blur) window.removeEventListener('blur', st.blur);
      live.delete(st);
      if (lastSt === st) lastSt = null;
      root._tyr = null;
      root.classList.remove('tyr');
    },
  };
  HGames.register(Object.assign({}, MOD, { id: 'tyr', icon: ICON }));
  HGames.register(Object.assign({}, MOD, { id: 'tyr-daily', icon: ICON_DAY }));
})();
