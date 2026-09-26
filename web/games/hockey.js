/*
  Аерохокей — стіл, шайба, біти (Impl/Hockey.cs, HockeyCore.cs; spec docs/games/specs/hockey.md).

  Вид (раз на подію): { phase, score: [сині, руді], target, teams: [[місця синіх], [місця рудих]], goals[4], own[4],
    left, golden, winner (0 сині / 1 руді / null), series, table: { w, h, goal: [40, 80], puckR, padR, padSpeed }, frame }.
  Кадр (25 Гц): { t, ph, left, x, y, vx, vy, p: [x0, y0, … x3, y3 | null], s: [сині, руді], n, serveIn, startIn,
    hit, goal, rally, nudge, golden }. ph: 0 відлік, 1 гра, 3 кінець, 4 лобі.
  Ввід: Input('to', { x, y }) — центр біти у світі (палець/миша), Input('move', { dx, dy }) — клавіші у світових осях.

  Світ: x уздовж стола 0..200 (сині захищають x = 0), y 0..120 униз. Кожен бачить свої ворота ліворуч (ландшафт)
  або внизу (портрет); таблиця перетворень — у toScreen(). Своя біта передбачена тим самим правилом, що й на
  сервері (stepPad = HockeyCore.StepPad, тест Prediction_matches_the_browser_fixture + docs/games/dev/arena-predict.py);
  шайба легко екстраполюється вперед; чужі біти — інтерполяція на один інтервал позаду.
*/
(() => {
  // ---- фізика біти: ті самі числа й порядок операцій, що в HockeyCore ----
  const L = 200, WD = 120, MID = 100, PAD_R = 8, PUCK_R = 4.5, GOAL_LO = 40, GOAL_HI = 80;
  const H = 0.008, SUB_MS = 8, PAD_STEP = 420 * H, D = 0.7071067811865476;
  const TICK_MS = 40, E_WALL = 0.92;
  const minX = (team) => (team === 0 ? PAD_R : MID + PAD_R);
  const maxX = (team) => (team === 0 ? MID - PAD_R : L - PAD_R);
  const clamp = (v, a, b) => Math.min(Math.max(v, a), b);

  /// Ціль біти — у свою половину (HockeyCore.ClampAim).
  function clampAim(team, t) {
    t.x = clamp(t.x, minX(team), maxX(team));
    t.y = clamp(t.y, PAD_R, WD - PAD_R);
  }
  /// Один підкрок біти (HockeyCore.StepPad): до цілі не далі за PAD_STEP або за напрямком клавіш, потім у свою половину.
  function stepPad(p, team, aim, tx, ty, dx, dy) {
    if (aim) {
      const ex = tx - p.x, ey = ty - p.y, d2 = ex * ex + ey * ey;
      if (d2 <= PAD_STEP * PAD_STEP) { p.x = tx; p.y = ty; }
      else {
        const d = Math.sqrt(d2);
        p.x += ex / d * PAD_STEP;
        p.y += ey / d * PAD_STEP;
      }
    } else if (dx !== 0 || dy !== 0) {
      const k = dx !== 0 && dy !== 0 ? PAD_STEP * D : PAD_STEP;
      p.x += dx * k;
      p.y += dy * k;
    }
    p.x = clamp(p.x, minX(team), maxX(team));
    p.y = clamp(p.y, PAD_R, WD - PAD_R);
  }
  window.HockeySim = { stepPad, clampAim, PAD_STEP };

  // ---- вигляд ----
  const LW = 800, LH = 480;                 // логічний канвас у ландшафті (у портреті — навпаки)
  const MARGIN = 14;
  const SEND_MS = 50;
  const TRAIL = 10;
  const TEAM_VARS = [['--hk-blue', '#5aa9ff'], ['--clay', '#d9825b']];
  const TEAM_NAME = ['сині', 'руді'];
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<rect x="1" y="3" width="14" height="10" rx="2" fill="var(--ok)"/>'
    + '<path d="M8 3v10" stroke="var(--text)" stroke-opacity=".55" stroke-width="1"/>'
    + '<circle cx="4.2" cy="8" r="2" fill="var(--hk-blue, #5aa9ff)"/><circle cx="11.8" cy="8" r="2" fill="var(--clay)"/>'
    + '<circle cx="8" cy="6.3" r="1.1" fill="var(--text)"/></svg>';

  const reduced = () => !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
  const scale = () => ((window.devicePixelRatio || 1) >= 2 ? 1 : 2);
  const clock = (ticks) => {
    const s = Math.max(0, Math.ceil((ticks * TICK_MS) / 1000));
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  };

  // ---- звук: клацання по швидкості, дзвін борта, ріжок гола, свисток ----
  const Snd = {
    on: (() => { try { return localStorage.getItem('hockey.sound') !== '0'; } catch { return true; } })(),
    ctx: null,
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
    hit(k) { this.beep(380 + 420 * k, 45, 'square', 0.045); },
    wall() { this.beep(240, 30, 'triangle', 0.035); },
    goal() { this.beep(440, 350, 'sawtooth', 0.04, 220); this.beep(330, 300, 'square', 0.025, 0, 0.05); },
    whistle() { this.beep(1800, 120, 'sine', 0.03); this.beep(1800, 120, 'sine', 0.03, 0, 0.17); },
    win() { [523, 659, 784, 1046].forEach((f, i) => this.beep(f, 150, 'square', 0.04, 0, i * 0.11)); },
    set(on) {
      this.on = on;
      try { localStorage.setItem('hockey.sound', on ? '1' : '0'); } catch { /* приватне вікно */ }
    },
  };

  // ---- клавіатура ----
  const live = new Set();
  const held = { up: false, down: false, left: false, right: false };
  const KEYS = { ArrowUp: 'up', KeyW: 'up', ArrowDown: 'down', KeyS: 'down', ArrowLeft: 'left', KeyA: 'left', ArrowRight: 'right', KeyD: 'right' };
  const KEYS_BY_KEY = { w: 'up', s: 'down', a: 'left', d: 'right', ц: 'up', і: 'down', ф: 'left', в: 'right' };
  const keyOf = (e) => KEYS[e.code] || KEYS_BY_KEY[String(e.key || '').toLowerCase()];
  const holding = () => held.up || held.down || held.left || held.right;
  document.addEventListener('keyup', (e) => {
    const k = keyOf(e);
    if (!k || !held[k]) return;
    held[k] = false;
    for (const st of live) pushMove(st);
  });
  window.addEventListener('blur', () => {
    held.up = held.down = held.left = held.right = false;
    for (const st of live) pushMove(st);
  });

  function state(root, ctx) {
    let st = root._hockey;
    if (!st) {
      st = root._hockey = {
        ctx, cv: null, K: scale(), raf: 0, interp: HGames.ui.Interp(), last: null, prevF: null,
        land: true, turn: 0, k: 3.8, ox: 0, oy: 0, cw: LW, ch: LH, table: null, tableKey: '', pal: null, palAt: 0,
        // своя біта
        mine: { x: 0, y: 0 }, mineOk: false, acc: 0, at: 0, aim: false, tx: 0, ty: 0, mdx: 0, mdy: 0, rtt: 60,
        // шайба
        vis: { x: MID, y: WD / 2 }, lastAt: 0, trail: [], lastN: -1,
        // ввід
        sentMove: null, want: null, sentTo: null, sentAt: 0, was: false,
        // соки
        sparks: new Float32Array(SP * SF), spN: 0, flash: new Float64Array(4), rail: [0, 0, 0, 0], goalAt: 0, goalTeam: 0,
        shake: 0, drawMs: [], lastDraw: 0, nicks: [],
        px: new Float64Array(4), py: new Float64Array(4),
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
      felt: c('--hk-felt', '#1f5f4a'), felt2: c('--hk-felt2', '#1a5140'), rail: c('--hk-rail', '#c8d4cf'),
      rail2: c('--hk-rail2', '#8fa39b'), goal: c('--hk-goal', '#0b1712'), line: c('--hk-line', 'rgba(236,241,234,.35)'),
      text: c('--text', '#ecf1ea'), muted: c('--muted', '#9db3a5'), accent: c('--accent', '#f4c542'), danger: c('--danger', '#ff6b5a'),
      shade: c('--gshade', 'rgba(15, 31, 24, .62)'), font: c('--font', 'system-ui, sans-serif'), bg2: c('--bg2', '#16291f'),
      team: TEAM_VARS.map(([n, d]) => c(n, d)),
    };
    st.palAt = now;
    return st.pal;
  }

  // ---- хто я й куди дивлюсь ----
  function teamOf(st, seat) {
    const v = st.ctx && st.ctx.view;
    const teams = (v && v.teams) || [[0, 2], [1, 3]];
    if (seat == null) return null;
    if (teams[0] && teams[0].includes(seat)) return 0;
    if (teams[1] && teams[1].includes(seat)) return 1;
    return null;
  }
  function myTeam(st) {
    const ctx = st.ctx;
    return ctx && ctx.mine ? teamOf(st, ctx.seat) : null;
  }
  function mySeat(st) {
    const ctx = st.ctx;
    return ctx && ctx.mine && myTeam(st) != null ? ctx.seat : -1;
  }

  /// Орієнтація й розміри: ландшафт — стіл лежить (мої ворота ліворуч), портрет — стоїть (мої ворота внизу).
  function layout(root, st) {
    const land = (root.clientWidth || 800) >= 640 && window.innerWidth > window.innerHeight;
    const turn = myTeam(st) === 1 ? 1 : 0;     // глядач — як сині
    if (st.cv && land === st.land && turn === st.turn) return;
    st.land = land;
    st.turn = turn;
    st.cw = land ? LW : LH;
    st.ch = land ? LH : LW;
    const sw = land ? L : WD, sh = land ? WD : L;
    st.k = Math.min((st.cw - 2 * MARGIN) / sw, (st.ch - 2 * MARGIN) / sh);
    st.ox = (st.cw - sw * st.k) / 2;
    st.oy = (st.ch - sh * st.k) / 2;
    st.cv = HGames.ui.canvas(root, { w: st.cw * st.K, h: st.ch * st.K, cls: 'hkboard' + (land ? '' : ' port') });
    st.table = null;
    st.trail.length = 0;
    st.interp.reset();
    if (st.last) st.interp.push(st.last);
    wireCanvas(root, st);
  }

  /// Світ → екран (у логічних пікселях канваса). Таблиця зі spec §6.1:
  ///   ландшафт: сині/глядач (x, y), руді (L − x, Wd − y); портрет: сині/глядач (y, L − x), руді (Wd − y, x).
  function toScreen(st, x, y, out) {
    let sx, sy;
    if (st.land) {
      if (st.turn === 0) { sx = x; sy = y; } else { sx = L - x; sy = WD - y; }
    } else if (st.turn === 0) { sx = y; sy = L - x; } else { sx = WD - y; sy = x; }
    out[0] = st.ox + sx * st.k;
    out[1] = st.oy + sy * st.k;
    return out;
  }
  function toWorld(st, px, py) {
    const sx = (px - st.ox) / st.k, sy = (py - st.oy) / st.k;
    if (st.land) return st.turn === 0 ? [sx, sy] : [L - sx, WD - sy];
    return st.turn === 0 ? [L - sy, sx] : [sy, WD - sx];
  }
  /// Екранний напрямок стрілок → світовий (та сама матриця без зсуву).
  function dirToWorld(st, sdx, sdy) {
    if (st.land) return st.turn === 0 ? [sdx, sdy] : [-sdx, -sdy];
    return st.turn === 0 ? [-sdy, sdx] : [sdy, -sdx];
  }

  // ---- ввід ----
  function canSend(st) {
    const ctx = st.ctx;
    return !!(ctx && ctx.mine && ctx.playing && mySeat(st) >= 0);
  }
  function pushMove(st, force) {
    const sdx = (held.right ? 1 : 0) - (held.left ? 1 : 0), sdy = (held.down ? 1 : 0) - (held.up ? 1 : 0);
    const [dx, dy] = dirToWorld(st, sdx, sdy);
    const key = dx + ',' + dy;
    if (!holding() && st.sentMove == null && !force) return;
    if (!canSend(st)) { st.sentMove = null; return; }
    if (!force && key === st.sentMove) return;
    st.sentMove = holding() ? key : null;
    st.aim = false;
    st.mdx = dx;
    st.mdy = dy;
    st.ctx.input('move', { dx, dy });
    st.echoAt = performance.now();
  }
  /// Палець/миша: ціль у світі — локально одразу, на сервер — не частіше 20/с і лише коли зсунулась ≥ 0.5.
  function aimAt(st, ev) {
    if (!canSend(st) || !st.cv || holding()) return;
    const r = st.cv.el.getBoundingClientRect();
    if (!r.width) return;
    let cx = ev.clientX, cy = ev.clientY;
    if (ev.pointerType === 'touch') {
      // біта ховається під пальцем — ставимо її на 14 px ближче до центру стола
      const mx = r.left + r.width / 2, my = r.top + r.height / 2, d = Math.hypot(mx - cx, my - cy) || 1;
      cx += ((mx - cx) / d) * 14;
      cy += ((my - cy) / d) * 14;
    }
    const w = toWorld(st, ((cx - r.left) / r.width) * st.cw, ((cy - r.top) / r.height) * st.ch);
    const t = { x: w[0], y: w[1] };
    clampAim(myTeam(st), t);
    st.aim = true;
    st.tx = t.x;
    st.ty = t.y;
    st.want = t;
    st.sentMove = null;
  }
  function flush(st, now) {
    if (!st.want || now - st.sentAt < SEND_MS || !canSend(st)) return;
    const t = st.want;
    if (st.sentTo && Math.abs(st.sentTo.x - t.x) < 0.5 && Math.abs(st.sentTo.y - t.y) < 0.5) { st.want = null; return; }
    st.want = null;
    st.sentAt = now;
    st.sentTo = t;
    st.ctx.input('to', { x: Math.round(t.x * 100) / 100, y: Math.round(t.y * 100) / 100 });
  }

  // ---- передбачення своєї біти ----
  function correct(st, f) {
    const s = mySeat(st), team = myTeam(st);
    if (s < 0 || !f.p || f.p[2 * s] == null || f.ph === 3 || f.ph === 4) { st.mineOk = false; return; }
    const srv = { x: f.p[2 * s], y: f.p[2 * s + 1] };
    // де сервер буде «зараз»: той самий крок із поточним наміром на пів дороги мережею
    const lead = Math.max(0, Math.min(100, (st.rtt - 20) / 2));
    const n = Math.round(lead / SUB_MS);
    for (let i = 0; i < n; i++) stepPad(srv, team, st.aim, st.tx, st.ty, st.mdx, st.mdy);
    if (!st.mineOk || f.hit === s) { st.mine.x = srv.x; st.mine.y = srv.y; st.mineOk = true; return; }
    st.mine.x += (srv.x - st.mine.x) * 0.3;
    st.mine.y += (srv.y - st.mine.y) * 0.3;
    const dx = st.mine.x - srv.x, dy = st.mine.y - srv.y, d = Math.hypot(dx, dy);
    if (d > 12) { st.mine.x = srv.x + (dx / d) * 12; st.mine.y = srv.y + (dy / d) * 12; }
  }
  function predict(st, now) {
    const dt = Math.min(100, now - st.at);
    st.at = now;
    if (!st.mineOk || now - st.lastAt > 400) return;
    st.acc += dt;
    const team = myTeam(st);
    while (st.acc >= SUB_MS) {
      stepPad(st.mine, team, st.aim, st.tx, st.ty, st.mdx, st.mdy);
      st.acc -= SUB_MS;
    }
  }

  // ---- шайба: легка екстраполяція проти затримки ----
  function puckNow(st, f, now) {
    if (f.ph !== 1 || f.serveIn > 0 || f.startIn > 0) { st.vis.x = f.x; st.vis.y = f.y; return st.vis; }
    let dt = (now - st.lastAt) / 1000;
    if (dt > 0.2) dt = 0;                 // зв'язок пропав — шайба стоїть
    dt = Math.min(dt, 0.05);
    let x = f.x + f.vx * dt, y = f.y + f.vy * dt;
    if (y < PUCK_R) y = 2 * PUCK_R - y; else if (y > WD - PUCK_R) y = 2 * (WD - PUCK_R) - y;
    if (y < GOAL_LO || y > GOAL_HI) { if (x < PUCK_R) x = 2 * PUCK_R - x; else if (x > L - PUCK_R) x = 2 * (L - PUCK_R) - x; }
    if (f.n !== st.lastN) { st.vis.x = x; st.vis.y = y; st.lastN = f.n; st.trail.length = 0; return st.vis; }
    st.vis.x += (x - st.vis.x) * 0.5;
    st.vis.y += (y - st.vis.y) * 0.5;
    return st.vis;
  }

  // ---- іскри ----
  const SP = 120, SF = 6;
  function spark(st, x, y, n, color, speed) {
    n = Math.max(1, Math.round(n * (reduced() ? 0.5 : 1)));
    for (let i = 0; i < n; i++) {
      let k = st.spN;
      if (k >= SP) k = Math.floor(Math.random() * SP); else st.spN++;
      const o = k * SF, a = Math.random() * Math.PI * 2, v = speed * (0.4 + Math.random() * 0.9);
      st.sparks[o] = x; st.sparks[o + 1] = y; st.sparks[o + 2] = Math.cos(a) * v; st.sparks[o + 3] = Math.sin(a) * v;
      st.sparks[o + 4] = 350; st.sparks[o + 5] = color;
    }
  }
  function drawSparks(st, g, pal, dt) {
    const p = st.sparks;
    let n = st.spN;
    for (let i = 0; i < n; i++) {
      const o = i * SF;
      p[o + 4] -= dt;
      if (p[o + 4] <= 0) { n--; if (i !== n) { for (let k = 0; k < SF; k++) p[o + k] = p[n * SF + k]; i--; } }
    }
    st.spN = n;
    const sec = dt / 1000;
    for (let i = 0; i < n; i++) {
      const o = i * SF;
      p[o] += p[o + 2] * sec;
      p[o + 1] += p[o + 3] * sec;
      p[o + 2] *= 0.92;
      p[o + 3] *= 0.92;
      g.globalAlpha = Math.max(0, p[o + 4] / 350);
      g.fillStyle = p[o + 5] === 2 ? pal.accent : pal.team[p[o + 5]] || pal.text;
      g.fillRect(p[o] - 1.5, p[o + 1] - 1.5, 3, 3);
    }
    g.globalAlpha = 1;
  }

  // ---- події кадру ----
  const tmp = [0, 0], tmp2 = [0, 0];
  function events(st, f) {
    const prev = st.prevF;
    st.prevF = f;
    if (!prev || !st.cv || !st.cv.el.offsetParent) return;
    const now = performance.now();
    const sp = Math.hypot(f.vx || 0, f.vy || 0);
    if (prev.startIn > 0 && f.startIn === 0 && f.ph === 1) Snd.whistle();
    if (f.hit != null && f.hit >= 0) {
      st.flash[f.hit] = now;
      Snd.hit(Math.min(1, sp / 1000));
      toScreen(st, f.x, f.y, tmp);
      spark(st, tmp[0], tmp[1], 8 + Math.min(6, sp / 120), teamOf(st, f.hit) || 0, 90 + sp * 0.15);
    } else if (f.ph === 1 && !f.serveIn && !prev.serveIn && sp > 0 && Math.hypot(prev.vx || 0, prev.vy || 0) > 0) {
      // борт: знак швидкості змінився без удару біти
      if ((prev.vy > 0) !== (f.vy > 0) && f.vy !== 0) { st.rail[f.y < WD / 2 ? 0 : 1] = now; Snd.wall(); }
      else if ((prev.vx > 0) !== (f.vx > 0) && f.vx !== 0) { st.rail[f.x < MID ? 2 : 3] = now; Snd.wall(); }
    }
    if (f.goal != null && f.goal >= 0) {
      st.goalAt = now;
      st.goalTeam = f.goal;
      st.shake = now;
      Snd.goal();
      // іскри з прорізу тих, хто пропустив
      toScreen(st, f.goal === 0 ? L : 0, WD / 2, tmp);
      spark(st, tmp[0], tmp[1], 24, f.goal, 160);
      st.trail.length = 0;
    }
  }

  // ---- стіл: офскрін, раз на розмір і орієнтацію ----
  function table(st, pal) {
    const key = [st.land, st.turn, st.K, pal.felt, pal.rail].join('|');
    if (st.table && st.tableKey === key) return st.table;
    const dpr = Math.min(3, window.devicePixelRatio || 1) * st.K;
    const cv = document.createElement('canvas');
    cv.width = Math.round(st.cw * dpr);
    cv.height = Math.round(st.ch * dpr);
    const g = cv.getContext('2d');
    g.scale(dpr, dpr);
    g.fillStyle = pal.bg2;
    g.fillRect(0, 0, st.cw, st.ch);
    const a = toScreen(st, 0, 0, [0, 0]), b = toScreen(st, L, WD, [0, 0]);
    const x0 = Math.min(a[0], b[0]), y0 = Math.min(a[1], b[1]), x1 = Math.max(a[0], b[0]), y1 = Math.max(a[1], b[1]);
    const rail = 6;
    // борт із фаскою
    g.fillStyle = pal.rail2;
    g.beginPath();
    g.roundRect(x0 - rail - 2, y0 - rail - 2, x1 - x0 + 2 * rail + 4, y1 - y0 + 2 * rail + 4, 14);
    g.fill();
    g.fillStyle = pal.rail;
    g.beginPath();
    g.roundRect(x0 - rail, y0 - rail, x1 - x0 + 2 * rail, y1 - y0 + 2 * rail, 12);
    g.fill();
    // поле з легким градієнтом
    const grad = g.createLinearGradient(x0, y0, x1, y1);
    grad.addColorStop(0, pal.felt);
    grad.addColorStop(1, pal.felt2);
    g.fillStyle = grad;
    g.beginPath();
    g.roundRect(x0, y0, x1 - x0, y1 - y0, 8);
    g.fill();
    // дірочки повітряного стола
    g.fillStyle = 'rgba(0,0,0,0.13)';
    const step = 10 * st.k;
    for (let yy = y0 + step / 2; yy < y1; yy += step) for (let xx = x0 + step / 2; xx < x1; xx += step) g.fillRect(xx - 0.6, yy - 0.6, 1.2, 1.2);
    // центральна лінія й коло
    g.strokeStyle = pal.line;
    g.lineWidth = 2;
    const c1 = toScreen(st, MID, 0, [0, 0]), c2 = toScreen(st, MID, WD, [0, 0]), cc = toScreen(st, MID, WD / 2, [0, 0]);
    g.beginPath(); g.moveTo(c1[0], c1[1]); g.lineTo(c2[0], c2[1]); g.stroke();
    g.beginPath(); g.arc(cc[0], cc[1], 20 * st.k, 0, Math.PI * 2); g.stroke();
    g.fillStyle = pal.line;
    g.beginPath(); g.arc(cc[0], cc[1], 3, 0, Math.PI * 2); g.fill();
    // воротарські півкола кольорами команд
    for (let team = 0; team < 2; team++) {
      const gx = team === 0 ? 0 : L;
      const p = toScreen(st, gx, WD / 2, [0, 0]);
      g.strokeStyle = pal.team[team];
      g.globalAlpha = 0.55;
      g.lineWidth = 2;
      g.beginPath();
      // півколо всередину стола: напрям на центр
      const toC = toScreen(st, MID, WD / 2, [0, 0]);
      const ang = Math.atan2(toC[1] - p[1], toC[0] - p[0]);
      g.arc(p[0], p[1], 26 * st.k, ang - Math.PI / 2, ang + Math.PI / 2);
      g.stroke();
      g.globalAlpha = 1;
      // проріз воріт у борту: темний, із сіткою
      const q1 = toScreen(st, gx, GOAL_LO, [0, 0]), q2 = toScreen(st, gx, GOAL_HI, [0, 0]);
      const ox = Math.sign(p[0] - toC[0]) * (rail + 7), oy = Math.sign(p[1] - toC[1]) * (rail + 7);
      g.fillStyle = pal.goal;
      const gx0 = Math.min(q1[0], q2[0], q1[0] + ox), gx1 = Math.max(q1[0], q2[0], q1[0] + ox);
      const gy0 = Math.min(q1[1], q2[1], q1[1] + oy), gy1 = Math.max(q1[1], q2[1], q1[1] + oy);
      g.beginPath();
      g.roundRect(gx0, gy0, Math.max(gx1 - gx0, 3), Math.max(gy1 - gy0, 3), 3);
      g.fill();
      g.strokeStyle = 'rgba(255,255,255,0.18)';
      g.lineWidth = 1;
      g.beginPath();
      for (let t = 0; t <= 6; t++) {
        if (ox) { const yy = gy0 + ((gy1 - gy0) * t) / 6; g.moveTo(gx0, yy); g.lineTo(gx1, yy); }
        else { const xx = gx0 + ((gx1 - gx0) * t) / 6; g.moveTo(xx, gy0); g.lineTo(xx, gy1); }
      }
      g.stroke();
      // лінія воріт і стійки кольору команди
      g.strokeStyle = pal.team[team];
      g.globalAlpha = 0.85;
      g.lineWidth = 3;
      g.beginPath(); g.moveTo(q1[0], q1[1]); g.lineTo(q2[0], q2[1]); g.stroke();
      g.globalAlpha = 1;
      g.fillStyle = pal.team[team];
      for (const q of [q1, q2]) { g.beginPath(); g.arc(q[0], q[1], 4, 0, Math.PI * 2); g.fill(); }
    }
    st.table = cv;
    st.tableKey = key;
    return cv;
  }

  // ---- малювання ----
  function blend(st) {
    const at = st.interp.at();
    const b = (at && at.b) || st.last;
    if (!b || !b.p) return b;
    const a = at && at.a;
    const smooth = a && a !== b && a.p && a.ph === b.ph && a.t <= b.t && b.t - a.t <= 3 && a.n === b.n;
    const t = at ? at.t : 1;
    for (let i = 0; i < 4; i++) {
      const x = b.p[2 * i], y = b.p[2 * i + 1];
      if (x == null) continue;
      if (smooth && a.p[2 * i] != null) {
        st.px[i] = a.p[2 * i] + (x - a.p[2 * i]) * t;
        st.py[i] = a.p[2 * i + 1] + (y - a.p[2 * i + 1]) * t;
      } else { st.px[i] = x; st.py[i] = y; }
    }
    return b;
  }

  function puckColor(pal, sp) {
    if (sp < 200) return pal.text;
    if (sp < 600) return pal.accent;
    return pal.danger;
  }

  function txt(g, pal, t, x, y, size, color, weight, align) {
    g.font = (weight || 800) + ' ' + size + 'px ' + pal.font;
    g.textAlign = align || 'center';
    g.textBaseline = 'middle';
    g.lineWidth = Math.max(3, size / 9);
    g.strokeStyle = 'rgba(8, 20, 16, 0.7)';
    g.strokeText(t, x, y);
    g.fillStyle = color || pal.text;
    g.fillText(t, x, y);
  }

  function nick(st, s) {
    const n = st.ctx && st.ctx.nickOf(s);
    if (n) { st.nicks[s] = n; return n; }
    return st.nicks[s] || (st.ctx && st.ctx.seatName(s)) || String(s + 1);
  }
  function teamNames(st, team) {
    const v = st.ctx && st.ctx.view;
    const seats = (v && v.teams && v.teams[team]) || [];
    return seats.length ? seats.map((s) => nick(st, s)).join(' і ') : TEAM_NAME[team];
  }

  function draw(st, now) {
    const c = st.cv;
    if (!c) return;
    const pal = palette(st);
    const g = c.ctx;
    const f = blend(st);
    const dt = st.lastDraw ? Math.min(100, now - st.lastDraw) : 16;
    st.lastDraw = now;
    const k = st.k;
    g.save();
    g.scale(st.K, st.K);
    const sh = now - st.shake;
    if (sh < 250 && !reduced()) {
      const a = 6 * (1 - sh / 250);
      g.translate((Math.random() - 0.5) * a, (Math.random() - 0.5) * a);
    }
    g.drawImage(table(st, pal), 0, 0, st.cw, st.ch);
    if (!f) { g.restore(); return; }
    const v = (st.ctx && st.ctx.view) || {};
    const ph = v.phase === 'over' ? 3 : v.phase === 'lobby' ? 4 : f.ph;
    // спалах борту
    for (let r = 0; r < 4; r++) {
      const lit = 1 - (now - st.rail[r]) / 300;
      if (lit <= 0) continue;
      const a = r === 0 ? [0, 0, L, 0] : r === 1 ? [0, WD, L, WD] : r === 2 ? [0, 0, 0, WD] : [L, 0, L, WD];
      toScreen(st, a[0], a[1], tmp);
      toScreen(st, a[2], a[3], tmp2);
      g.strokeStyle = pal.accent;
      g.globalAlpha = 0.7 * lit;
      g.lineWidth = 5;
      g.beginPath(); g.moveTo(tmp[0], tmp[1]); g.lineTo(tmp2[0], tmp2[1]); g.stroke();
      g.globalAlpha = 1;
    }
    scoreboard(st, g, pal, f, v);
    // шайба зі слідом
    const puck = puckNow(st, f, now);
    const sp = Math.hypot(f.vx || 0, f.vy || 0);
    const flying = ph === 1 && !f.serveIn && !f.startIn;
    if (!flying) st.trail.length = 0;
    else {
      st.trail.push(puck.x, puck.y);
      if (st.trail.length > TRAIL * 2) st.trail.splice(0, 2);
    }
    const colr = puckColor(pal, sp);
    g.fillStyle = colr;
    const nT = st.trail.length / 2;
    for (let i = 0; i < nT; i++) {
      toScreen(st, st.trail[2 * i], st.trail[2 * i + 1], tmp);
      g.globalAlpha = 0.05 + 0.22 * (i / nT);
      g.beginPath();
      g.arc(tmp[0], tmp[1], PUCK_R * k * (0.4 + 0.6 * (i / nT)), 0, Math.PI * 2);
      g.fill();
    }
    g.globalAlpha = 1;
    // гол: шайба ще мить видна в прорізі
    const goalK = 1 - (now - st.goalAt) / 900;
    if (!(goalK > 0.78 && ph === 1)) {
      toScreen(st, puck.x, puck.y, tmp);
      g.shadowColor = colr;
      g.shadowBlur = 10;
      g.fillStyle = colr;
      g.beginPath();
      g.arc(tmp[0], tmp[1], PUCK_R * k, 0, Math.PI * 2);
      g.fill();
      g.shadowBlur = 0;
      g.fillStyle = 'rgba(0,0,0,0.25)';
      g.beginPath();
      g.arc(tmp[0], tmp[1], PUCK_R * k * 0.45, 0, Math.PI * 2);
      g.fill();
    }
    // біти
    const me = mySeat(st);
    for (let i = 0; i < 4; i++) {
      if (!f.p || f.p[2 * i] == null) continue;
      const team = teamOf(st, i);
      const x = i === me && st.mineOk && ph !== 4 ? st.mine.x : st.px[i], y = i === me && st.mineOk && ph !== 4 ? st.mine.y : st.py[i];
      paddle(st, g, pal, i, team == null ? i % 2 : team, x, y, now, i === me);
    }
    drawSparks(st, g, pal, dt);
    overlays(st, g, pal, f, v, ph, now);
    if (ph === 1 && st.ctx && st.ctx.playing && st.lastAt && now - st.lastAt > 1000) txt(g, pal, '⏳ зв’язок…', st.cw / 2, st.ch - 26, 18, pal.text, 700);
    g.restore();
  }

  function paddle(st, g, pal, seat, team, x, y, now, mine) {
    toScreen(st, x, y, tmp);
    const R = PAD_R * st.k, color = pal.team[team];
    const lit = Math.max(0, 1 - (now - st.flash[seat]) / 180);
    g.fillStyle = 'rgba(0,0,0,0.28)';
    g.beginPath();
    g.arc(tmp[0] + 2, tmp[1] + 3, R, 0, Math.PI * 2);
    g.fill();
    if (mine || lit > 0) { g.shadowColor = mine ? pal.accent : color; g.shadowBlur = mine ? 12 : 16 * lit; }
    g.fillStyle = color;
    g.beginPath();
    g.arc(tmp[0], tmp[1], R, 0, Math.PI * 2);
    g.fill();
    g.shadowBlur = 0;
    g.fillStyle = 'rgba(255,255,255,0.28)';
    g.beginPath();
    g.arc(tmp[0], tmp[1], R - 3, 0, Math.PI * 2);
    g.fill();
    g.fillStyle = color;
    g.beginPath();
    g.arc(tmp[0], tmp[1], R * 0.45, 0, Math.PI * 2);
    g.fill();
    g.strokeStyle = lit > 0 ? '#ffffff' : 'rgba(0,0,0,0.35)';
    g.lineWidth = lit > 0 ? 3 : 1.5;
    g.beginPath();
    g.arc(tmp[0], tmp[1], R, 0, Math.PI * 2);
    g.stroke();
    g.fillStyle = '#ffffff';
    g.font = '800 ' + Math.round(R * 0.62) + 'px ' + pal.font;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillText(String(seat + 1), tmp[0], tmp[1] + 0.5);
    if (mine) {
      g.strokeStyle = pal.accent;
      g.lineWidth = 1.5;
      g.setLineDash([3, 3]);
      g.beginPath();
      g.arc(tmp[0], tmp[1], R + 5, 0, Math.PI * 2);
      g.stroke();
      g.setLineDash([]);
    }
  }

  function scoreboard(st, g, pal, f, v) {
    const s = f.s || v.score || [0, 0];
    // своя команда (чи сині для глядача) — біля своїх воріт: ліворуч у ландшафті, унизу в портреті
    const near = st.turn, far = 1 - st.turn;
    const left = f.left != null ? f.left : v.left;
    const hot = left != null && left * TICK_MS <= 30000;
    const clk = left != null && f.ph === 1 ? (f.golden ? '⚡ ' : '⏱ ') + clock(left) : '';
    g.globalAlpha = 0.9;
    if (st.land) {
      txt(g, pal, String(s[near]), st.ox + 40, st.oy + 34, 46, pal.team[near], 800);
      txt(g, pal, String(s[far]), st.cw - st.ox - 40, st.oy + 34, 46, pal.team[far], 800);
      g.globalAlpha = 1;
      if (clk) txt(g, pal, clk, st.cw / 2, st.oy + 18, 16, hot ? pal.danger : pal.muted, 700);
      if (f.golden) txt(g, pal, 'золотий гол', st.cw / 2, st.oy + 38, 15, pal.accent, 700);
      if (f.rally >= 6 && f.ph === 1) txt(g, pal, 'серія ' + f.rally + (f.rally >= 15 ? ' 🔥' : ''), st.cw / 2, st.ch - st.oy - 16, 16, pal.muted, 600);
    } else {
      // портрет: рахунок збоку від центральної лінії — суперник над нею, свої під нею (як на справжньому столі)
      const cy = st.ch / 2, x = st.ox + 30;
      txt(g, pal, String(s[far]), x, cy - 28, 38, pal.team[far], 800);
      txt(g, pal, String(s[near]), x, cy + 30, 38, pal.team[near], 800);
      g.globalAlpha = 1;
      const rx = st.cw - st.ox - 12;
      if (clk) txt(g, pal, clk, rx, cy - 16, 15, hot ? pal.danger : pal.muted, 700, 'right');
      if (f.golden) txt(g, pal, 'золотий гол', rx, cy + 16, 14, pal.accent, 700, 'right');
      else if (f.rally >= 6 && f.ph === 1) txt(g, pal, 'серія ' + f.rally, rx, cy + 16, 14, pal.muted, 600, 'right');
    }
    g.globalAlpha = 1;
  }

  function overlays(st, g, pal, f, v, ph, now) {
    const ctx = st.ctx;
    const waiting = !ctx || !ctx.playing;
    if (ph === 0 && !waiting && f.startIn > 0) {
      g.fillStyle = pal.shade;
      g.fillRect(0, 0, st.cw, st.ch);
      txt(g, pal, String(Math.ceil((f.startIn * TICK_MS) / 1000)), st.cw / 2, st.ch / 2, 84);
      const me = mySeat(st);
      if (me >= 0 && f.p && f.p[2 * me] != null) {
        toScreen(st, st.mineOk ? st.mine.x : f.p[2 * me], st.mineOk ? st.mine.y : f.p[2 * me + 1], tmp);
        const how = HGames.ui.coarse() ? 'палець' : 'мишка / стрілки';
        const t = 'це ти · ' + how;
        g.font = '700 15px ' + pal.font;
        const w = g.measureText(t).width;
        txt(g, pal, t, clamp(tmp[0], w / 2 + 8, st.cw - w / 2 - 8), tmp[1] - PAD_R * st.k - 16, 15, pal.accent, 700);
      }
      return;
    }
    // «ГОЛ!» — 900 мс кольором команди, що забила
    const gk = (now - st.goalAt) / 900;
    if (ph === 1 && gk < 1) {
      const sc = reduced() ? 1 : 1.2 - 0.2 * Math.min(1, gk * 3);
      g.save();
      g.globalAlpha = 1 - Math.max(0, gk - 0.6) / 0.4;
      g.translate(st.cw / 2, st.ch / 2);
      g.scale(sc, sc);
      txt(g, pal, 'ГОЛ!', 0, 0, 72, pal.team[st.goalTeam]);
      g.restore();
    }
    if (ph === 3) {
      g.fillStyle = pal.shade;
      g.fillRect(0, 0, st.cw, st.ch);
      const w = v.winner;
      const s = v.score || f.s || [0, 0];
      if (w === 0 || w === 1) {
        txt(g, pal, '🏆 ' + teamNames(st, w), st.cw / 2, st.ch / 2 - 60, 34, pal.team[w]);
        txt(g, pal, s[w] + ':' + s[1 - w], st.cw / 2, st.ch / 2 - 10, 44, pal.text);
      } else {
        txt(g, pal, 'Нічия', st.cw / 2, st.ch / 2 - 60, 34);
        txt(g, pal, s[0] + ':' + s[1], st.cw / 2, st.ch / 2 - 10, 44, pal.text);
      }
      // особисті голи
      const rows = [];
      for (let i = 0; i < 4; i++) if (v.goals && v.goals[i] != null) rows.push(i);
      let y = st.ch / 2 + 40;
      for (const i of rows) {
        const team = teamOf(st, i);
        const own = v.own && v.own[i] ? ' (авто ' + v.own[i] + ')' : '';
        txt(g, pal, nick(st, i) + ' — ⚽ ' + v.goals[i] + own, st.cw / 2, y, 17, team == null ? pal.text : pal.team[team], 700);
        y += 26;
      }
    }
  }

  // ---- рядок над полем ----
  function hud(root, st) {
    const ctx = st.ctx;
    let el = root.querySelector(':scope > .hkhud');
    if (!el) {
      el = document.createElement('div');
      el.className = 'hkhud';
      el.addEventListener('click', (e) => {
        if (!e.target.closest('[data-snd]')) return;
        Snd.set(!Snd.on);
        if (Snd.on) Snd.hit(0.4);
        el.dataset.sig = '';
        const s = root._hockey;
        if (s) hud(root, s);
      });
      root.insertBefore(el, root.firstChild);
    }
    const v = ctx.view || {};
    const f = st.last || v.frame || {};
    const s = f.s || v.score || [0, 0];
    const narrow = (root.clientWidth || 800) < 480;
    let html = '';
    for (let i = 0; i < 4; i++) {
      const n = ctx.nickOf(i);
      if (!n) continue;
      st.nicks[i] = n;
      const team = teamOf(st, i);
      const g = v.goals && v.goals[i] != null && v.phase !== 'lobby' ? v.goals[i] : null;
      const own = v.own && v.own[i] ? ' <span class="hkown">(авто ' + v.own[i] + ')</span>' : '';
      const short = narrow && i !== ctx.seat;
      html += '<span class="hkchip t' + (team == null ? 'x' : team) + (i === ctx.seat ? ' me' : '') + '" title="' + ctx.esc(n) + '"><i>' + (i + 1) + '</i>'
        + (short ? '' : ctx.esc(n)) + (g != null ? ' <b>⚽ ' + g + '</b>' + own : '') + '</span>';
    }
    if (v.phase !== 'lobby') html += '<span class="hkchip hkscore" aria-live="polite">' + s[0] + ':' + s[1] + ' · до ' + (v.target || 7) + '</span>';
    html += '<button type="button" class="hkchip hksnd" data-snd data-pad-skip title="' + (Snd.on ? 'Вимкнути звук' : 'Увімкнути звук') + '">' + (Snd.on ? '🔊' : '🔇') + '</button>';
    if (el.dataset.sig !== html) {
      el.dataset.sig = html;
      el.innerHTML = html;
    }
  }

  function wireCanvas(root, st) {
    const el = st.cv.el;
    if (el._hkWired) return;
    el._hkWired = true;
    const S = () => root._hockey;
    const on = (e) => {
      const s = S();
      if (!s) return;
      aimAt(s, e);
      // над своєю половиною курсор ховаємо — біта і є курсор
      if (e.pointerType === 'mouse' && s.cv) {
        const r = s.cv.el.getBoundingClientRect();
        const w = toWorld(s, ((e.clientX - r.left) / r.width) * s.cw, ((e.clientY - r.top) / r.height) * s.ch);
        const t = myTeam(s);
        const own = t != null && canSend(s) && (t === 0 ? w[0] < MID : w[0] > MID);
        el.classList.toggle('own', own);
      }
    };
    el.addEventListener('pointermove', on);
    el.addEventListener('pointerdown', (e) => { if (S() && canSend(S())) { e.preventDefault(); try { el.setPointerCapture(e.pointerId); } catch { /* без capture */ } } on(e); });
    el.addEventListener('pointerleave', () => el.classList.remove('own'));
  }

  /// Телефон: на старті партії підкручуємо сторінку так, щоб стіл цілком став між шапкою й нижніми панелями.
  /// Раз на партію (і після F5) — далі людина гортає сама, ми не воюємо.
  function fitView(el) {
    if (!el || !HGames.ui.coarse()) return;
    const r = el.getBoundingClientRect();
    const top = 60, bottom = window.innerHeight - 116;
    const band = bottom - top;
    const want = r.height <= band ? top + (band - r.height) / 2 : top;
    const d = r.top - want;
    if (Math.abs(d) > 24) window.scrollBy({ top: d, behavior: reduced() ? 'auto' : 'smooth' });
  }

  function spin(root, st) {
    if (st.raf) return;
    const loop = () => {
      if (!st.cv || !st.cv.el.isConnected) { st.raf = 0; return; }
      st.raf = requestAnimationFrame(loop);
      const now = performance.now();
      flush(st, now);
      predict(st, now);
      if (!st.cv.el.offsetParent || document.hidden) return;
      const t0 = performance.now();
      draw(st, now);
      st.drawMs.push(performance.now() - t0);
      if (st.drawMs.length > 300) st.drawMs.shift();
    };
    st.raf = requestAnimationFrame(loop);
  }

  HGames.register({
    id: 'hockey',
    icon: ICON,
    seatNames: ['синій', 'рудий', 'синій', 'рудий'],
    seatClass: ['hkb', 'c', 'hkb', 'c'],
    pad: { dirs: true, hint: '{dpad} біта' },
    news: {
      v: '2026-09-27',
      title: 'Нова гра: Аерохокей',
      items: [
        '🏒 Стіл, шайба, біти: води біту мишкою чи пальцем у своїй половині й не пропусти',
        '🥅 Ворота вузькі — гол береться кутом від борта, а не силою',
        '👥 На двох — класика, на чотирьох — двоє на двоє на одній половині',
        '⏱ До 7 (або 5 чи 10); чотири хвилини без переможця — золотий гол',
        '🔊 Шайба клацає, борти дзвенять, на гол стіл труситься',
      ],
    },

    mount(root, ctx) {
      const st = state(root, ctx);
      if (ctx.view && ctx.view.frame) st.last = ctx.view.frame;
      hud(root, st);
      layout(root, st);
      st.onResize = () => { const s = root._hockey; if (s) { layout(root, s); s.cv.resize(); } };
      window.addEventListener('resize', st.onResize);
      spin(root, st);
    },

    update(root, ctx) {
      const st = state(root, ctx);
      const v = ctx.view;
      const vf = v && v.frame;
      if (vf && (!ctx.playing || !st.last || v.phase === 'over' || v.phase === 'lobby' || vf.t < (st.last.t || 0) - 5)) {
        st.last = vf;
        st.interp.reset();
        st.interp.push(vf);
        st.mineOk = false;
      }
      layout(root, st);
      if (!st.cv) return;
      st.cv.el.classList.toggle('play', !!(ctx.mine && ctx.playing));
      st.cv.resize();
      hud(root, st);
      if (ctx.playing && ctx.mine && !st.was) {
        setTimeout(() => fitView(st.cv && st.cv.el), 60);
        // «Ще раз» і F5: сервер не знає, що клавішу так і не відпускали
        st.sentMove = null;
        st.sentTo = null;
        if (holding()) pushMove(st, true);
      }
      if (!ctx.playing) { st.mineOk = false; st.sentMove = null; }
      st.was = !!(ctx.playing && ctx.mine);
      spin(root, st);
    },

    frame(root, ctx, f) {
      const st = state(root, ctx);
      if (!f) return;
      if (st.last && (f.t < st.last.t - 2 || f.n !== st.last.n)) st.interp.reset();
      events(st, f);
      if (f.ph === 3 && st.last && st.last.ph !== 3) {
        const t = myTeam(st);
        if (ctx.view && ctx.view.winner != null && t === ctx.view.winner) Snd.win();
      }
      st.last = f;
      st.lastAt = performance.now();
      st.interp.push(f);
      // відлуння наміру клавішами: скільки йде дорога туди й назад (для «пів дороги» передбачення)
      if (st.echoAt && f.p && mySeat(st) >= 0) {
        const s = mySeat(st), prevX = st.prevX, x = f.p[2 * s];
        if (prevX != null && x !== prevX) { st.rtt = st.rtt * 0.7 + Math.min(400, performance.now() - st.echoAt) * 0.3; st.echoAt = 0; }
        st.prevX = x;
      }
      correct(st, f);
      hud(root, st);
    },

    onKey(e, ctx) {
      const st = [...live].find((s) => s.ctx === ctx);
      if (!st || !ctx.mine) return false;
      const k = keyOf(e);
      if (!k) return false;
      if (!held[k]) { held[k] = true; pushMove(st); }
      return !!ctx.playing;
    },

    status(ctx) {
      const st = [...live].find((s) => s.ctx === ctx);
      const v = ctx.view || {};
      const f = ctx.frame || v.frame;
      const target = v.target || 7;
      const s = (f && f.s) || v.score || [0, 0];
      if (!ctx.playing) {
        if (ctx.room && ctx.room.status === 'lobby') {
          const n = (ctx.room.seats || []).filter((x) => x.nick).length;
          if (n === 3) return 'Троє — не порівну: потрібен четвертий, або хай хтось встане';
          return 'Стіл на двох або двоє на двоє. Стартує господар';
        }
        return '';
      }
      if (st && st.lastAt && performance.now() - st.lastAt > 1000 && f && f.ph === 1) return '⏳ зв’язок…';
      if (!f || f.startIn > 0) return 'Готуйсь…';
      if (f.golden) return '⚡ Золотий гол — хто заб’є, той і взяв';
      if (!ctx.mine || !st || mySeat(st) < 0) return 'Дивишся збоку · до ' + target + ' · ' + s[0] + ':' + s[1];
      const t = myTeam(st);
      const where = st.land ? 'ліворуч' : 'внизу';
      const mineScore = s[t] + ':' + s[1 - t];
      return (HGames.ui.coarse() ? 'Тягни біту пальцем' : 'Мишка або стрілки') + ' · твої ворота ' + where + ' · до ' + target + ' (' + mineScore + ')';
    },

    unmount(root) {
      const st = root._hockey;
      if (!st) return;
      cancelAnimationFrame(st.raf);
      st.raf = 0;
      if (st.onResize) window.removeEventListener('resize', st.onResize);
      live.delete(st);
      root._hockey = null;
    },
  });
})();
