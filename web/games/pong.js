/*
  Понг. Сервер тикає 25 разів на секунду і шле кадр із дробовими координатами; ми його не малюємо
  «як є», а проганяємо через HGames.ui.Interp і домальовуємо на requestAnimationFrame — інакше на
  144-герцовому екрані м'яч смикався б двадцять п'ять разів на секунду.

  Два поля (Impl/Pong.cs):
  - класика на двох, 160 × 100: { mode: 'duo'?, t, bx, by, vx, vy, p: [y0, y1], s: [s0, s1], serveIn,
    startIn, winner, seats: [ліве місце, праве місце], hit, rally };
  - арена на трьох-чотирьох, 120 × 120: { mode: 'arena', t, bx, by, vx, vy, p: [4 × позиція вздовж своєї
    стіни | null], l: [4 × життя | null], serveIn, startIn, winner, hit, rally, lost, from, n }.
    Стіни: 0 ліва, 1 права, 2 верхня, 3 нижня. Кожен гравець бачить арену поверненою так, що його стіна
    внизу, — тож і керування в усіх однакове: ← →.
  Вісь y дивиться вниз. Правила й стан — тільки на сервері, звідси летять самі наміри:
    Input('move', { dir: -1|0|1 })  — клавішу тримають, поки не прийде наступний dir;
    Input('to',   { y })            — палець або миша просять центр ракетки отут (на арені — вздовж стіни).
*/
(() => {
  const DUO = { W: 160, H: 100, SC: 3.5 };   // 560 × 350: канвас не розтягується, тож і не милиться
  const ARENA = { S: 120, SC: 4, CORNER: 14, OFF: 4, L: 22, PW: 2, R: 1.8 };
  const TICK_MS = 40;
  const TRAIL = 10;              // скільки слідів тягне за собою м'яч
  const SEND_MS = 40;            // не частіше 25 вводів на секунду: хаб пускає 30

  /// Кольори й позначки місць: колір — для ока, фігура — для тих, кому кольори зливаються.
  const SEAT_VARS = [['--accent', '#f4c542'], ['--ok', '#7bd389'], ['--clay', '#d9825b'], ['--pong-blue', '#6fb3e8']];
  const SHAPES = ['●', '■', '▲', '◆'];

  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<rect x="1.5" y="4" width="2" height="8" rx="1" fill="var(--accent)"/>'
    + '<rect x="12.5" y="4" width="2" height="8" rx="1" fill="var(--ok)"/>'
    + '<circle cx="8" cy="8" r="1.8" fill="var(--text)"/></svg>';

  // Розкладконезалежно читаємо e.code, але терпимо й e.key: синтетичний keydown із панелі
  // браузера приходить без code (INTEGRATION-NOTES §3).
  const KEYS = {
    ArrowUp: 'up', KeyW: 'up', w: 'up', W: 'up', ArrowDown: 'down', KeyS: 'down', s: 'down', S: 'down',
    ArrowLeft: 'left', KeyA: 'left', a: 'left', A: 'left', ArrowRight: 'right', KeyD: 'right', d: 'right', D: 'right',
  };
  const keyOf = (e) => KEYS[e.code] || KEYS[e.key];

  const isArena = (f) => !!f && f.mode === 'arena';
  /// На арені «праворуч на екрані» — це плюс уздовж стіни для лівої й нижньої, мінус для правої й верхньої.
  const along = (side) => (side === 0 || side === 3 ? 1 : -1);
  /// Поворот арени, щоб стіна side опинилась унизу (canvas.rotate, y униз).
  const TURN = [-Math.PI / 2, Math.PI / 2, Math.PI, 0];

  const reduced = () => !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);

  // ---- звук: коротенькі клацання, щоб м'яч «чувся». Вимикач — у картці ----
  const Snd = {
    on: (() => { try { return localStorage.getItem('pong.sound') !== '0'; } catch { return true; } })(),
    ctx: null,
    ensure() {
      if (!this.on) return null;
      const ua = navigator.userActivation;
      if (!this.ctx) {
        const AC = window.AudioContext || window.webkitAudioContext;
        // AudioContext — лише після жесту людини, інакше браузер його глушить і сварить у консоль.
        if (!AC || (ua && !ua.hasBeenActive)) return null;
        try { this.ctx = new AC(); } catch { return null; }
      }
      if (this.ctx.state === 'suspended') this.ctx.resume().catch(() => {});
      return this.ctx;
    },
    /// Один тон: частота, тривалість, форма хвилі, гучність, куди ковзнути частотою.
    beep(freq, ms, type, vol, to) {
      const c = this.ensure();
      if (!c) return;
      const t = c.currentTime, o = c.createOscillator(), g = c.createGain();
      o.type = type || 'square';
      o.frequency.setValueAtTime(freq, t);
      if (to) o.frequency.exponentialRampToValueAtTime(to, t + ms / 1000);
      g.gain.setValueAtTime(vol || 0.05, t);
      g.gain.exponentialRampToValueAtTime(0.0001, t + ms / 1000);
      o.connect(g).connect(c.destination);
      o.start(t);
      o.stop(t + ms / 1000 + 0.02);
    },
    hit(speedK) { this.beep(420 + 260 * speedK, 50, 'square', 0.045); },
    wall() { this.beep(260, 35, 'triangle', 0.04); },
    goal() { this.beep(520, 260, 'sawtooth', 0.04, 110); },
    out() { this.beep(300, 520, 'sawtooth', 0.05, 60); },
    win() { [523, 659, 784, 1046].forEach((f, i) => setTimeout(() => this.beep(f, 140, 'square', 0.04), i * 110)); },
    set(on) {
      this.on = on;
      try { localStorage.setItem('pong.sound', on ? '1' : '0'); } catch { /* приватне вікно */ }
    },
  };

  // ---- клавіатура: одна на всі картки, бо тиснуть її не в картку, а в документ ----
  const live = new Set();        // стани карток понга, які зараз на екрані
  const held = { up: false, down: false, left: false, right: false };
  const holding = () => held.up || held.down || held.left || held.right;

  /// Куди їхати моїй ракетці на сервері: у класиці — по y, на арені — вздовж стіни, з поправкою на поворот.
  function dirFor(st) {
    if (isArena(st.last)) return along(st.ctx.seat) * ((held.right ? 1 : 0) - (held.left ? 1 : 0));
    return (held.down ? 1 : 0) - (held.up ? 1 : 0);
  }

  /// Шлемо не кожне натискання, а лише зміну напрямку: автоповтор клавіші інакше з'їв би всю квоту.
  function pushDir() {
    for (const st of live) {
      const ctx = st.ctx;
      if (!ctx || !ctx.mine || !ctx.playing) { st.sent = null; continue; }
      const dir = dirFor(st);
      if (st.sent === dir) continue;
      st.sent = dir;
      ctx.input('move', { dir });
    }
  }
  function setHeld(k, on) {
    if (!k || held[k] === on) return;
    held[k] = on;
    pushDir();
  }
  document.addEventListener('keyup', (e) => setHeld(keyOf(e), false));
  // Пішли з вкладки із затиснутою клавішею — ракетка має спинитись, а не їхати в стіну.
  window.addEventListener('blur', () => { held.up = held.down = held.left = held.right = false; pushDir(); });

  // ---- стан однієї картки ----
  function state(root, ctx) {
    let st = root._pong;
    if (!st) {
      st = root._pong = {
        cv: null, mode: '', ctx, interp: HGames.ui.Interp(), last: null, prev: null, trail: [],
        raf: 0, want: null, sentAt: 0, sent: null, was: false,
        flash: [0, 0, 0, 0], hurt: [0, 0, 0, 0], shake: 0, pops: [], legend: '',
      };
      live.add(st);
    }
    st.ctx = ctx;
    return st;
  }

  /// Кадр «на зараз»: два останні кадри, змішані інтерполятором. null — ще нічого не приходило.
  function blend(st) {
    const at = st.interp.at();
    if (!at) return st.last;
    const a = at.a || at.b, b = at.b;
    if (!a || !b || !a.p || !b.p || isArena(a) !== isArena(b)) return b;
    // Гол чи нова подача: м'яч стрибнув з-за краю в центр. Плавно вести його через усе поле назад —
    // брехня, тож на такій парі кадрів просто показуємо новіший.
    const jump = isArena(b) ? a.n !== b.n
      : (!a.s || !b.s || a.s[0] !== b.s[0] || a.s[1] !== b.s[1]);
    const L = HGames.ui.lerp, t = at.t;
    const p = b.p.map((y, i) => (y == null || a.p[i] == null ? y : L(a.p[i], y, t)));
    return Object.assign({}, b, {
      bx: jump ? b.bx : L(a.bx, b.bx, t),
      by: jump ? b.by : L(a.by, b.by, t),
      p, jump,
    });
  }

  // ---- події кадру: удар, стіна, гол — звук і спалахи ----
  function events(st, f) {
    const prev = st.prev;
    st.prev = f;
    if (!prev || !f || isArena(prev) !== isArena(f) || !st.cv || !st.cv.el.offsetParent) return;
    const now = performance.now();
    const arena = isArena(f);
    const max = arena ? 140 : 160, min = arena ? 55 : 60;
    const sp = Math.hypot(f.vx || 0, f.vy || 0);
    if (f.hit != null) {
      st.flash[f.hit] = now;
      Snd.hit(Math.max(0, Math.min(1, (sp - min) / (max - min))));
    } else if (!f.serveIn && !f.startIn && sp > 0 && Math.hypot(prev.vx || 0, prev.vy || 0) > 0
      && ((prev.vx > 0) !== (f.vx > 0) || (prev.vy > 0) !== (f.vy > 0))) {
      Snd.wall();
    }
    if (arena) {
      if (f.n !== prev.n && f.lost != null && prev.l && f.l && f.l[f.lost] < prev.l[f.lost]) {
        st.hurt[f.lost] = now;
        st.shake = now;
        const who = st.ctx.nickOf(f.lost) || st.ctx.seatName(f.lost);
        const by = f.from != null ? st.ctx.nickOf(f.from) : null;
        if (f.l[f.lost] === 0) { st.pops.push({ side: f.lost, text: who + ' поза грою', at: now, big: true }); Snd.out(); }
        else { st.pops.push({ side: f.lost, text: '−1 ♥', at: now }); Snd.goal(); }
        if (by) st.pops.push({ side: -1, text: by + ' ➜ ' + who, at: now });
      }
    } else if (prev.s && f.s && (prev.s[0] !== f.s[0] || prev.s[1] !== f.s[1])) {
      const scorer = f.s[0] !== prev.s[0] ? 0 : 1;
      st.hurt[1 - scorer] = now;
      st.shake = now;
      st.pops.push({ side: scorer, text: '+1', at: now });
      Snd.goal();
    }
    if (f.winner != null && prev.winner == null) Snd.win();
  }

  // ---- малювання ----
  function seatColor(css, seat) {
    const v = SEAT_VARS[seat] || SEAT_VARS[0];
    return css(v[0], v[1]);
  }

  /// М'яч «розжарюється» зі швидкістю: білий → жовтий → червоний.
  function ballColor(css, f, arena) {
    const sp = Math.hypot(f.vx || 0, f.vy || 0);
    const k = (sp - (arena ? 55 : 60)) / (arena ? 85 : 100);
    if (k < 0.35) return css('--text', '#ecf1ea');
    if (k < 0.75) return css('--accent', '#f4c542');
    return css('--danger', '#ff6b5a');
  }

  function draw(st) {
    const c = st.cv;
    if (!c) return;
    const g = c.ctx;
    const f = blend(st);
    const css = (n, d) => (st.ctx ? st.ctx.css(n, d) : d);
    const now = performance.now();
    g.save();
    g.clearRect(0, 0, c.w, c.h);
    // Трус поля на гол: 250 мс, кілька пікселів — відчутно, але не нудить.
    const sh = now - st.shake;
    if (sh < 250 && !reduced()) {
      const k = 6 * (1 - sh / 250);
      g.translate((Math.random() - 0.5) * k, (Math.random() - 0.5) * k);
    }
    if (isArena(f)) drawArena(st, g, c, f, css, now);
    else drawDuo(st, g, c, f, css, now);
    g.restore();
  }

  function drawDuo(st, g, c, f, css, now) {
    const { W, SC } = DUO;
    const ctx = st.ctx;
    const waiting = !ctx || !ctx.playing;    // стіл ще чекає на суперника — відлік не крутимо
    g.fillStyle = css('--bg2', '#16291f');
    g.fillRect(0, 0, c.w, c.h);

    // середина поля — пунктиром, щоб було видно, чия половина
    g.strokeStyle = css('--line', '#2f4d3d');
    g.lineWidth = 3;
    g.setLineDash([10, 14]);
    g.beginPath();
    g.moveTo(c.w / 2, 0);
    g.lineTo(c.w / 2, c.h);
    g.stroke();
    g.setLineDash([]);

    if (!f || !f.p) return;
    const seats = f.seats || [0, 1];
    const col = [seatColor(css, seats[0]), seatColor(css, seats[1])];
    // Пропустив — своя половина на мить червоніє.
    [0, 1].forEach((i) => {
      const k = 1 - (now - st.hurt[i]) / 450;
      if (k <= 0) return;
      g.fillStyle = css('--danger', '#ff6b5a');
      g.globalAlpha = 0.18 * k;
      g.fillRect(i === 0 ? 0 : c.w / 2, 0, c.w / 2, c.h);
      g.globalAlpha = 1;
    });
    scoreDuo(g, c, f, css, col);

    // ракетки: ліва й права — кольори ті самі, що в чіпах місць; щойно відбила — спалахує
    const ph = 18 * SC, pw = 2 * SC;
    [0, 1].forEach((i) => {
      const x = (i === 0 ? 4 : W - 4) * SC;
      const lit = Math.max(0, 1 - (now - st.flash[i]) / 180);
      g.fillStyle = col[i];
      if (lit > 0) { g.shadowColor = col[i]; g.shadowBlur = 18 * lit; }
      g.beginPath();
      g.roundRect(x - pw / 2 - lit * 2, f.p[i] * SC - ph / 2, pw + lit * 4, ph, pw / 2);
      g.fill();
      g.shadowBlur = 0;
    });
    // «Це ти» — поки йде відлік, щоб новачок не шукав свою ракетку
    ball(st, g, f, css, SC, false);
    rallyBadge(g, c, f, css);
    pops(st, g, c, css, now, (side) => [side === 0 ? c.w * 0.25 : c.w * 0.75, c.h * 0.5]);
    overlay(g, c, f, ctx, css, waiting, false);
    // «Це ти» — поверх відліку, щоб новачок не шукав свою ракетку
    if (ctx && ctx.mine && f.startIn > 0 && !waiting) {
      const i = seats[0] === ctx.seat ? 0 : seats[1] === ctx.seat ? 1 : -1;
      if (i >= 0) you(g, css, (i === 0 ? 4 + 5 : W - 4 - 5) * SC, f.p[i] * SC, i === 0 ? 'left' : 'right', '↑↓ W/S');
    }
  }

  /// Рахунок великими цифрами по центру вгорі.
  function scoreDuo(g, c, f, css, col) {
    const s = f.s || [0, 0];
    g.font = '700 46px ' + css('--font', 'system-ui, sans-serif');
    g.textBaseline = 'top';
    g.textAlign = 'right';
    g.fillStyle = col[0];
    g.fillText(String(s[0]), c.w / 2 - 22, 14);
    g.textAlign = 'left';
    g.fillStyle = col[1];
    g.fillText(String(s[1]), c.w / 2 + 22, 14);
    g.textAlign = 'center';
    g.fillStyle = css('--muted', '#9db3a5');
    g.fillText(':', c.w / 2, 12);
  }

  function ball(st, g, f, css, SC, arena) {
    // слід живе лише поки м'яч летить
    const flying = !f.startIn && !f.serveIn && f.winner == null;
    if (f.jump || !flying) st.trail.length = 0;
    else {
      st.trail.push([f.bx, f.by]);
      if (st.trail.length > TRAIL) st.trail.shift();
    }
    const r = (arena ? ARENA.R : 1.5) * SC;
    const colr = ballColor(css, f, arena);
    g.fillStyle = colr;
    st.trail.forEach(([x, y], i) => {
      g.globalAlpha = 0.06 + 0.22 * (i / TRAIL);
      g.beginPath();
      g.arc(x * SC, y * SC, r * (0.4 + 0.6 * (i / TRAIL)), 0, Math.PI * 2);
      g.fill();
    });
    g.globalAlpha = 1;
    g.shadowColor = colr;
    g.shadowBlur = 10;
    g.beginPath();
    g.arc(f.bx * SC, f.by * SC, r, 0, Math.PI * 2);
    g.fill();
    g.shadowBlur = 0;
  }

  /// «Серія 8» — коли розіграш затягнувся, хай усі бачать, що це вже подія.
  function rallyBadge(g, c, f, css) {
    if (!f.rally || f.rally < 6 || f.winner != null) return;
    g.font = '600 18px ' + css('--font', 'system-ui, sans-serif');
    g.textAlign = 'center';
    g.textBaseline = 'bottom';
    g.fillStyle = css('--muted', '#9db3a5');
    g.fillText('серія ' + f.rally + (f.rally >= 15 ? ' 🔥' : ''), c.w / 2, c.h - 10);
  }

  /// Спливні написи: «+1», «−1 ♥», «Оля ➜ Петро». Живуть 1.1 с, повільно спливають угору.
  function pops(st, g, c, css, now, where) {
    st.pops = st.pops.filter((p) => now - p.at < 1100);
    st.pops.forEach((p) => {
      const k = (now - p.at) / 1100;
      const [x, y] = p.side < 0 ? [c.w / 2, c.h * 0.32] : where(p.side);
      g.globalAlpha = 1 - k;
      g.font = '800 ' + (p.big ? 26 : p.side < 0 ? 22 : 30) + 'px ' + css('--font', 'system-ui, sans-serif');
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.lineWidth = 4;
      g.strokeStyle = css('--bg2', '#16291f');
      g.strokeText(p.text, x, y - k * 26);
      g.fillStyle = p.side < 0 ? css('--text', '#ecf1ea') : p.text.startsWith('+') ? css('--ok', '#7bd389') : css('--danger', '#ff6b5a');
      g.fillText(p.text, x, y - k * 26);
    });
    g.globalAlpha = 1;
  }

  /// Підказка «це ти» біля своєї ракетки — лише під час відліку.
  function you(g, css, x, y, side, keys) {
    g.font = '700 16px ' + css('--font', 'system-ui, sans-serif');
    g.fillStyle = css('--text', '#ecf1ea');
    g.textBaseline = 'middle';
    g.textAlign = side === 'left' ? 'left' : side === 'right' ? 'right' : 'center';
    const text = side === 'left' ? '← це ти · ' + keys : side === 'right' ? keys + ' · це ти →' : 'це ти ↓ · ' + keys;
    g.fillText(text, x, y);
  }

  // ---- арена ----

  /// Світ арени → пікселі канваса з урахуванням повороту (для підписів, які мусять стояти рівно).
  function toScreen(x, y, turn) {
    const { S, SC } = ARENA;
    const dx = x - S / 2, dy = y - S / 2, cs = Math.cos(turn), sn = Math.sin(turn);
    return [(dx * cs - dy * sn + S / 2) * SC, (dx * sn + dy * cs + S / 2) * SC];
  }

  /// Точка всередині поля біля середини стіни side, на відстані d від краю.
  function wallPoint(side, d) {
    const S = ARENA.S;
    return side === 0 ? [d, S / 2] : side === 1 ? [S - d, S / 2] : side === 2 ? [S / 2, d] : [S / 2, S - d];
  }

  /// Чия стіна внизу: своя, якщо я граю цю партію; глядач бачить арену як є.
  function turnOf(st, f) {
    const ctx = st.ctx;
    if (!ctx || !ctx.mine || ctx.seat == null || !f.l || f.l[ctx.seat] == null) return 0;
    return TURN[ctx.seat];
  }

  function drawArena(st, g, c, f, css, now) {
    const { S, SC, CORNER, OFF, L, PW } = ARENA;
    const ctx = st.ctx;
    const waiting = !ctx || !ctx.playing;
    const turn = turnOf(st, f);
    g.fillStyle = css('--bg2', '#16291f');
    g.fillRect(0, 0, c.w, c.h);

    g.save();
    g.translate(c.w / 2, c.h / 2);
    g.rotate(turn);
    g.translate(-c.w / 2, -c.h / 2);

    // Стіни: жива — тонка лінія кольору господаря, глуха (порожнє місце або вибув) — суцільний бортик.
    const lives = f.l || [null, null, null, null];
    for (let s = 0; s < 4; s++) {
      const alive = lives[s] != null && lives[s] > 0;
      const hurt = Math.max(0, 1 - (now - st.hurt[s]) / 500);
      if (!alive) {
        const T = 2.4;
        const [x, y, w, h] = s === 0 ? [0, 0, T, S] : s === 1 ? [S - T, 0, T, S] : s === 2 ? [0, 0, S, T] : [0, S - T, S, T];
        g.fillStyle = hurt > 0 ? css('--danger', '#ff6b5a') : css('--muted', '#9db3a5');
        g.globalAlpha = hurt > 0 ? 0.5 + 0.5 * hurt : 0.35;
        g.fillRect(x * SC, y * SC, w * SC, h * SC);
        g.globalAlpha = 1;
        continue;
      }
      const [x1, y1, x2, y2] = s === 0 ? [0, 0, 0, S] : s === 1 ? [S, 0, S, S] : s === 2 ? [0, 0, S, 0] : [0, S, S, S];
      g.lineWidth = hurt > 0 ? 12 : 5;
      g.strokeStyle = hurt > 0 ? css('--danger', '#ff6b5a') : seatColor(css, s);
      g.globalAlpha = hurt > 0 ? 0.5 + 0.5 * hurt : 0.4;
      g.beginPath();
      g.moveTo(x1 * SC, y1 * SC);
      g.lineTo(x2 * SC, y2 * SC);
      g.stroke();
      g.globalAlpha = 1;
    }
    // Кутові квадрати — глухі.
    g.fillStyle = css('--line', '#2f4d3d');
    [[0, 0], [S - CORNER, 0], [0, S - CORNER], [S - CORNER, S - CORNER]].forEach(([x, y]) => {
      g.beginPath();
      g.roundRect(x * SC, y * SC, CORNER * SC, CORNER * SC, 6);
      g.fill();
    });
    // Центральне коло — просто щоб поле не було порожнім.
    g.strokeStyle = css('--line', '#2f4d3d');
    g.lineWidth = 2;
    g.setLineDash([6, 10]);
    g.beginPath();
    g.arc(c.w / 2, c.h / 2, 16 * SC, 0, Math.PI * 2);
    g.stroke();
    g.setLineDash([]);

    // Ракетки: лише в живих.
    const p = f.p || [];
    for (let s = 0; s < 4; s++) {
      if (p[s] == null || !(lives[s] > 0)) continue;
      const colr = seatColor(css, s);
      const lit = Math.max(0, 1 - (now - st.flash[s]) / 180);
      g.fillStyle = colr;
      if (lit > 0) { g.shadowColor = colr; g.shadowBlur = 18 * lit; }
      const thick = PW * SC + lit * 4;
      const len = L * SC;
      const off = OFF * SC;
      g.beginPath();
      if (s < 2) g.roundRect((s === 0 ? off : c.w - off) - thick / 2, p[s] * SC - len / 2, thick, len, thick / 2);
      else g.roundRect(p[s] * SC - len / 2, (s === 2 ? off : c.h - off) - thick / 2, len, thick, thick / 2);
      g.fill();
      g.shadowBlur = 0;
    }
    ball(st, g, f, css, SC, true);
    g.restore();

    // Підписи ставимо вже без повороту — текст має стояти рівно. Біля кожної стіни гравця: фігура
    // місця і життя; сам нік — у легенді під полем.
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    for (let s = 0; s < 4; s++) {
      if (lives[s] == null) continue;
      const [x, y] = toScreen(...wallPoint(s, 12), turn);
      g.globalAlpha = lives[s] > 0 ? 0.8 : 0.45;
      g.font = '700 20px ' + css('--font', 'system-ui, sans-serif');
      g.fillStyle = seatColor(css, s);
      g.fillText(SHAPES[s] + ' ' + (lives[s] > 0 ? lives[s] + ' ♥' : '✕'), x, y);
    }
    g.globalAlpha = 1;
    rallyBadge(g, c, f, css);
    pops(st, g, c, css, now, (side) => toScreen(...wallPoint(side, 26), turn));
    overlay(g, c, f, ctx, css, waiting, true);
    if (ctx && ctx.mine && f.startIn > 0 && !waiting && lives[ctx.seat] > 0) {
      you(g, css, c.w / 2, c.h - 20 * SC, 'down', '← → A/D');
    }
  }

  /// Затемнення з написом: «готуйсь» перед першим ударом і підсумок після останнього очка.
  function overlay(g, c, f, ctx, css, waiting, arena) {
    const ready = f.startIn > 0 && !waiting;
    const over = ctx && ctx.room && ctx.room.status === 'finished';
    if (!ready && f.winner == null && !over) return;
    g.fillStyle = css('--gshade', 'rgba(15, 31, 24, .62)');
    g.fillRect(0, 0, c.w, c.h);
    g.fillStyle = css('--text', '#ecf1ea');
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    if (ready) {
      g.font = '700 84px ' + css('--font', 'system-ui, sans-serif');
      g.fillText(String(Math.ceil((f.startIn * TICK_MS) / 1000)), c.w / 2, c.h / 2);
      return;
    }
    const nick = f.winner != null && ctx && ctx.nickOf ? ctx.nickOf(f.winner) : null;
    g.font = '700 36px ' + css('--font', 'system-ui, sans-serif');
    if (nick) g.fillStyle = seatColor(css, f.winner);
    g.fillText(nick ? (arena ? '👑 ' : '🏆 ') + nick : 'Партію зіграно', c.w / 2, c.h / 2 - 16);
    if (!nick) return;
    g.font = '600 20px ' + css('--font', 'system-ui, sans-serif');
    g.fillStyle = css('--text', '#ecf1ea');
    g.fillText(arena ? 'остання ракетка на арені' : 'перемога!', c.w / 2, c.h / 2 + 24);
  }

  // ---- легенда арени під полем: хто якого кольору, скільки життів, скільки влучив ----
  function legend(root, st) {
    let el = root.querySelector(':scope > .ponglegend');
    const f = st.last;
    if (!isArena(f)) { if (el) el.remove(); st.legend = ''; return; }
    if (!el) {
      el = document.createElement('div');
      el.className = 'ponglegend small';
      const cv = st.cv && st.cv.el;
      if (cv && cv.nextSibling) root.insertBefore(el, cv.nextSibling); else root.appendChild(el);
    }
    const ctx = st.ctx;
    const goals = (ctx.view && ctx.view.goals) || [];
    const html = (f.l || []).map((l, s) => {
      if (l == null) return '';
      const nick = ctx.nickOf(s) || ctx.seatName(s);
      const hearts = l > 0 ? '♥'.repeat(Math.min(l, 7)) : 'поза грою';
      return '<span class="pgl s' + s + (l > 0 ? '' : ' out') + (ctx.seat === s ? ' me' : '') + '">'
        + '<i class="sh">' + SHAPES[s] + '</i> ' + ctx.esc(nick) + ' <span class="hp">' + hearts + '</span>'
        + (goals[s] ? ' <span class="muted">· влучань ' + goals[s] + '</span>' : '') + '</span>';
    }).join('');
    if (html !== st.legend) { st.legend = html; el.innerHTML = html; }
  }

  // ---- керування пальцем ----

  /// Позиція пальця на канвасі → бажаний центр ракетки в одиницях поля.
  function aimAt(st, ev) {
    const ctx = st.ctx;
    if (!ctx || !ctx.mine || !ctx.playing || !st.cv) return;
    // Клавіша в руці головніша за мишу: сервер від «to» гасить утримання, і випадковий рух
    // курсором над полем інакше вбивав би керування з клавіатури до наступного натискання.
    if (holding()) return;
    const r = st.cv.el.getBoundingClientRect();
    if (!r.height || !r.width) return;
    if (isArena(st.last)) {
      // Своя стіна завжди внизу, тож палець ліворуч-праворуч — це ракетка ліворуч-праворуч.
      const fx = Math.max(0, Math.min(1, (ev.clientX - r.left) / r.width));
      st.want = along(ctx.seat) > 0 ? fx * ARENA.S : (1 - fx) * ARENA.S;
    } else {
      st.want = Math.max(0, Math.min(DUO.H, ((ev.clientY - r.top) / r.height) * DUO.H));
    }
  }

  /// Останню позицію пальця відправляємо з rAF-циклу: так вона не губиться і не б'є у квоту хаба.
  function flush(st, now) {
    if (st.want == null || now - st.sentAt < SEND_MS) return;
    const ctx = st.ctx;
    st.sentAt = now;
    const y = st.want;
    st.want = null;
    if (ctx && ctx.mine && ctx.playing) { st.sent = null; ctx.input('to', { y }); }
  }

  /// Дві кнопки-утримання під палець: ↑↓ у класиці, ← → на арені. ui.dpad тут не годиться — він на клік.
  function pad(root, st) {
    let el = root.querySelector(':scope > .pongpad');
    if (!st.ctx || !st.ctx.mine) {
      // Кнопку могли тримати в мить, коли гравець устав з-за столу: разом із нею гасимо й утримання.
      if (el) { el.remove(); held.up = held.down = held.left = held.right = false; pushDir(); }
      return;
    }
    const mode = isArena(st.last) ? 'arena' : 'duo';
    if (el && el.dataset.mode === mode) return;
    if (el) el.remove();
    el = document.createElement('div');
    el.className = 'pongpad';
    el.dataset.mode = mode;
    el.innerHTML = mode === 'arena'
      ? '<button type="button" data-k="left" aria-label="ліворуч">←</button><button type="button" data-k="right" aria-label="праворуч">→</button>'
      : '<button type="button" data-k="up" aria-label="вгору">↑</button><button type="button" data-k="down" aria-label="вниз">↓</button>';
    const grab = (e) => {
      const b = e.target.closest('button');
      if (!b) return;
      e.preventDefault();
      const on = e.type === 'pointerdown';
      // Захоплюємо вказівник самі: інакше «натиснув ↑ мишею, з'їхав із кнопки, відпустив» лишав би
      // ракетку їхати в стіну (pointerleave не спливає, тож ловити його на контейнері марно).
      if (on) { try { b.setPointerCapture(e.pointerId); } catch { /* стара миша без capture */ } }
      setHeld(b.dataset.k, on);
    };
    el.addEventListener('pointerdown', grab);
    el.addEventListener('pointerup', grab);
    el.addEventListener('pointercancel', grab);
    const snd = root.querySelector(':scope > .pongsnd');
    if (snd) root.insertBefore(el, snd); else root.appendChild(el);
  }

  /// Вимикач звуку під полем.
  function soundBtn(root) {
    let b = root.querySelector(':scope > .pongsnd');
    if (!b) {
      b = document.createElement('button');
      b.type = 'button';
      b.className = 'pongsnd ghost small';
      b.dataset.padSkip = '';
      b.addEventListener('click', () => { Snd.set(!Snd.on); if (Snd.on) Snd.hit(0.5); soundBtn(root); });
      root.appendChild(b);
    }
    const t = Snd.on ? '🔊 звук' : '🔇 без звуку';
    if (b.textContent !== t) b.textContent = t;
    b.title = Snd.on ? 'Вимкнути клацання м\'яча' : 'Увімкнути клацання м\'яча';
  }

  /// Канвас під поточне поле: класика 16:10, арена квадратна. Той самий елемент, лише інший розмір.
  function field(root, st) {
    const mode = isArena(st.last) ? 'arena' : 'duo';
    if (st.cv && st.mode === mode) return;
    st.mode = mode;
    st.cv = mode === 'arena'
      ? HGames.ui.canvas(root, { w: ARENA.S * ARENA.SC, h: ARENA.S * ARENA.SC, cls: 'pongboard arena' })
      : HGames.ui.canvas(root, { w: DUO.W * DUO.SC, h: DUO.H * DUO.SC, cls: 'pongboard' });
    if (st.ctx) st.cv.el.classList.toggle('play', !!st.ctx.mine);
    st.trail.length = 0;
    st.interp.reset();
    if (!st.cv.el._pongWired) {
      st.cv.el._pongWired = true;
      st.cv.el.addEventListener('pointermove', (e) => { if (root._pong) aimAt(root._pong, e); });
      st.cv.el.addEventListener('pointerdown', (e) => { if (root._pong) aimAt(root._pong, e); });
    }
  }

  HGames.register({
    id: 'pong',
    icon: ICON,
    // Імена місць приходять із сервера (у класиці «ліва/права», на арені — кольори); це запас.
    seatNames: ['ліва', 'права', 'руда', 'синя'],
    seatClass: ['x', 'o', 'c', 'pb'],
    pad: { dirs: true, hint: '{dpad} ракетка' },
    news: {
      v: '2026-09-24',
      title: 'Понг: тепер і на чотирьох',
      items: [
        '🟨🟩🟧🟦 За столом 2–4 гравці: на двох — класика, на трьох-чотирьох — квадратна арена, де кожен стереже свою стіну',
        '♥ На арені в кожного життя: пропустив — мінус одне, скінчились — твоя стіна глухне. Останній на полі бере арену',
        '🔄 Кожен бачить арену так, що його стіна внизу, тож керування в усіх однакове: ← → або палець',
        '🌀 Підкрутка: відбий м\'яч, поки ракетка їде, — і він піде під гострішим кутом',
        '🔊 М\'яч клацає, ракетка спалахує, гол трусить поле, а довгий розіграш має лічильник серії',
        '⏱ Партія — коротка, звичайна чи довга. Стартує господар кнопкою «Почати»',
      ],
    },

    mount(root, ctx) {
      const st = state(root, ctx);
      if (ctx.view && ctx.view.frame) st.last = ctx.view.frame;
      field(root, st);
      const loop = () => {
        if (!st.cv || !st.cv.el.isConnected) { st.raf = 0; return; }
        st.raf = requestAnimationFrame(loop);
        // Пішли на «Ефір» — картка лишається в DOM під display:none. Малювати в невидимий канвас
        // сто разів на секунду означає просто їсти акумулятор; цикл при цьому живий і сам прокинеться.
        if (!st.cv.el.offsetParent) return;
        flush(st, performance.now());
        draw(st);
      };
      st.raf = requestAnimationFrame(loop);
    },

    update(root, ctx) {
      const st = state(root, ctx);
      // Перший вид приходить ще до кадрів — з нього й малюємо поле, поки тик не поїхав. Посеред партії
      // свіжіші за вид кадри, тож вид беремо лише поза грою (лобі, кінець) або коли змінилось саме поле.
      const vf = ctx.view && ctx.view.frame;
      if (vf && (!ctx.playing || !st.last || isArena(vf) !== isArena(st.last))) st.last = vf;
      field(root, st);
      if (!st.cv) return;
      // Скролити сторінку пальцем по канвасу можна лише глядачеві: гравцеві той самий рух — це ракетка.
      st.cv.el.classList.toggle('play', !!ctx.mine);
      soundBtn(root);
      legend(root, st);
      pad(root, st);
      st.cv.resize();
      if (!ctx.playing) { st.interp.reset(); st.trail.length = 0; st.sent = null; }
      // Нова партія («Ще раз») починається з клавішею, яку так і не відпускали: сервер про неї не знає,
      // бо в паузі ми напрямок не слали. Шлемо його заново, щойно за карткою знову можна грати.
      else if (!st.was && holding()) { st.sent = null; pushDir(); }
      st.was = !!ctx.playing;
    },

    frame(root, ctx, f) {
      const st = state(root, ctx);
      if (!f) return;
      const modeChanged = isArena(f) !== isArena(st.last);
      st.last = f;
      if (modeChanged) { field(root, st); pad(root, st); }
      events(st, f);
      st.interp.push(f);
      if (isArena(f)) legend(root, st);
    },

    onKey(e, ctx) {
      const k = keyOf(e);
      if (!k || !ctx.mine || !ctx.playing) return false;
      setHeld(k, true);
      return true;
    },

    status(ctx) {
      // Фаза й відлік реалтайму живуть у кадрах, а не у видах.
      const f = ctx.frame || (ctx.view && ctx.view.frame) || null;
      if (!ctx.playing) {
        if (ctx.room && ctx.room.status === 'lobby') {
          const n = (ctx.room.seats || []).filter((s) => s.nick).length;
          if (n >= 3) return 'Троє й більше — граємо на арені, кожен стереже свою стіну';
          if (n === 2) return 'На двох — класика. Третій і четвертий перетворять її на арену';
        }
        return '';
      }
      if (f && f.startIn > 0) return 'Готуйсь…';
      if (!ctx.mine) return 'Дивишся збоку';
      if (isArena(f)) {
        const l = f.l && f.l[ctx.seat];
        if (!(l > 0)) return 'Ти поза грою — дивись, хто кого';
        return '← → або A/D, чи тягни пальцем · у тебе ' + l + ' ♥';
      }
      const s = (f && f.s) || (ctx.view && ctx.view.scores) || [0, 0];
      const target = (ctx.view && ctx.view.target) || 7;
      return '↑↓ або W/S, чи тягни пальцем · до ' + target + ' (' + s[0] + ':' + s[1] + ')';
    },

    unmount(root) {
      const st = root._pong;
      if (!st) return;
      cancelAnimationFrame(st.raf);
      live.delete(st);
      root._pong = null;
    },
  });
})();
