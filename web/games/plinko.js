/*
  Горохова дошка — плінко по-нашому (Азарт → 📈 Миттєві). Дядько Глек сипле горох на дошку з кілочками: на кожному
  ряду горошина відскакує вліво чи вправо й падає в глечик із множником. Правила, шляхи й гроші — на сервері
  (Impl/Plinko.cs, PlinkoCore.cs, docs/games/specs/plinko.md); тут дошка, кнопки й наміри.

  Вид: { on, bets, bet, rows, risk, balls, maxBalls, balance, seq, hash,
         recent: [{ seq, bet, rows, risk, hash, seed, win, balls: [{ p, s, x, win }] }],
         tables: { low|mid|high: [[десяті, від лівого краю] × 9 (8…16 рядів)] }, rtp, best, dayBest, drops, staked, won }
  Наміри: act('drop', { bet, n, rows, risk, hash }) — hash: відбиток, який людина бачила (сервер звіряє).

  Горошина котиться шляхом сервера (p: «0»/«1» на ряд, 1 — вправо), а не фізикою: куди впаде — вирішено чесно наперед.
  Запит — один за раз: кліки підряд стають у чергу й ідуть, щойно дійде свіжий відбиток. Поки горох котиться, ряди й
  ризик не міняються, а виграш у шапці притримано (тихий гаманець). rAF — лише поки є що малювати. Звук — WebAudio-синт,
  типово вимкнений (🔊).
*/
(() => {
  'use strict';

  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<g fill="var(--muted)"><circle cx="8" cy="2.6" r="1"/><circle cx="5.5" cy="5.6" r="1"/><circle cx="10.5" cy="5.6" r="1"/>'
    + '<circle cx="3" cy="8.6" r="1"/><circle cx="8" cy="8.6" r="1"/><circle cx="13" cy="8.6" r="1"/></g>'
    + '<path d="M1.6 11.4h3.6l-.4 4H2zM6.2 11.4h3.6l-.4 4H6.6zM10.8 11.4h3.6l-.4 4h-2.8z" fill="var(--clay)"/>'
    + '<circle cx="9.6" cy="4" r="1.9" fill="#86d04a" stroke="#2f5a17" stroke-width=".7"/></svg>';

  const RISKS = [
    { id: 'low', label: 'Низький', icon: '🌱' },
    { id: 'mid', label: 'Середній', icon: '🔥' },
    { id: 'high', label: 'Високий', icon: '💥' },
  ];
  const RISK_OF = { low: 'низький', mid: 'середній', high: 'високий' };
  const NS = [1, 5, 10];
  const MIN_ROWS = 8, MAX_ROWS = 16;
  const SEG_MS = 110, FAST_MS = 55;           // ряд за стільки
  const STAGGER = 140, FAST_STAGGER = 70;     // горошини одного кидка — одна за одною
  const AUTO_GAP = 600, FAST_AUTO_GAP = 320;  // авто: кидок не частіше
  const QUEUE_MAX = 5;
  const STRIP = 14;
  const POPS_MAX = 14;
  const SAY_MS = 2600;
  const SAYS = ['Оце горох!', 'Хто розсипав горох? А, це на щастя!', 'Глек аж підстрибнув!', 'Ану ще жменю!', 'Такий горох — та на базар!'];
  // кольори глечиків: множник → колір (між точками — по логарифму)
  const STOPS = [[0.1, [64, 78, 108]], [0.5, [92, 106, 138]], [1, [212, 162, 58]], [3, [236, 134, 40]], [10, [232, 88, 42]],
    [100, [208, 40, 70]], [1000, [170, 20, 122]]];

  const reduced = () => !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
  const store = {
    get(k, d) { try { const v = localStorage.getItem('plinko_' + k); return v == null ? d : v; } catch { return d; } },
    set(k, v) { try { localStorage.setItem('plinko_' + k, String(v)); } catch { /* приватне вікно */ } },
  };
  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
  const fmtN = (n) => Math.round(+n || 0).toString().replace(/\B(?=(\d{3})+(?!\d))/g, ' ');
  /// Десяті → «1,5», «0,2», «1000».
  const fmtM = (x) => (x % 10 === 0 ? String(x / 10) : (x / 10).toFixed(1).replace('.', ','));
  const fmtX = (x) => '×' + fmtM(x);
  const tier = (x) => (x < 10 ? 0 : x < 20 ? 1 : x < 100 ? 2 : x < 1000 ? 3 : 4);
  const cur = (st) => st.ctx && st.ctx.view;
  const tableOf = (v, rows, risk) => (v && v.tables && v.tables[risk] && v.tables[risk][rows - MIN_ROWS]) || [];
  const quiet = (on) => { if (window.HGames && HGames.quietWallet) HGames.quietWallet('plinko-', on); };

  function potRgb(x) {
    const m = Math.max(STOPS[0][0], x / 10);
    for (let i = 1; i < STOPS.length; i++) {
      if (m > STOPS[i][0] && i < STOPS.length - 1) continue;
      const [m0, c0] = STOPS[i - 1], [m1, c1] = STOPS[i];
      const t = Math.max(0, Math.min(1, Math.log(m / m0) / Math.log(m1 / m0)));
      return c0.map((v, j) => Math.round(v + (c1[j] - v) * t));
    }
    return STOPS[STOPS.length - 1][1];
  }
  const rgb = (c, a) => (a == null ? 'rgb(' + c.join(',') + ')' : 'rgba(' + c.join(',') + ',' + a + ')');
  const shade = (c, k) => c.map((v) => Math.max(0, Math.min(255, Math.round(k > 0 ? v + (255 - v) * k : v * (1 + k)))));

  // ---------------------------------------------------------------------------------------------
  // Стан і каркас DOM
  // ---------------------------------------------------------------------------------------------

  function setup(root, ctx) {
    let st = root._gor;
    if (!st) {
      st = root._gor = {
        root, ctx, offs: [], balls: [], flashes: new Map(), bumps: [], strip: [], pre: new Set(), raf: 0,
        busy: false, queued: 0, auto: false, autoT: 0, sentHash: null, sentAt: 0, nextStart: 0, pendingWin: 0,
        fast: store.get('fast', '0') === '1', sound: store.get('snd', '0') === '1',
        seen: null, inited: false, quiet: false, shown: ctx.shown !== false, tickAt: 0, chipSig: '',
        bet: 10, rows: 12, risk: 'low', n: 1,
      };
      st.ctx = ctx; ctx._gor = st;
      skeleton(root, st);
      if (window.ResizeObserver) {
        st.ro = new ResizeObserver(() => { clearTimeout(st.roT); st.roT = setTimeout(() => layout(st), 60); });
        st.ro.observe(st.el.stage);
      }
    }
    st.ctx = ctx; ctx._gor = st;
    return st;
  }

  function skeleton(root, st) {
    root.innerHTML = '<div class="gor">'
      + '<div class="gor-stage"><canvas class="gor-cv"></canvas>'
      + '<img class="gor-glek" src="/static/glek.svg" alt="" draggable="false">'
      + '<div class="gor-ov"><div class="gor-strip" aria-label="Останні глечики"></div>'
      + '<button type="button" class="gor-ib" title="Правила й чесність" data-pad-skip>ⓘ</button>'
      + '<div class="gor-say"></div><div class="gor-fx"></div>'
      + '<div class="gor-off" hidden>Горохова дошка на перерві — Глек збирає розсипаний горох</div></div></div>'
      + '<div class="gor-panel">'
      + '<button type="button" class="gor-go" data-pad-first><span class="gor-gt">Кинути</span><small class="gor-gs"></small></button>'
      + '<div class="gor-box gor-bet"><div class="gor-lab">ставка за горошину, 🏺</div><div class="gor-chips gor-bets"></div></div>'
      + '<div class="gor-box gor-n"><div class="gor-lab">горошин за раз</div><div class="gor-chips gor-ns"></div></div>'
      + '<div class="gor-box gor-risk"><div class="gor-lab">ризик</div><div class="gor-chips gor-risks"></div></div>'
      + '<div class="gor-box gor-rows"><div class="gor-lab">ряди <b class="gor-rv"></b></div><div class="gor-row">'
      + '<button type="button" class="gor-pm" data-r="-1" aria-label="Менше рядів">−</button>'
      + '<input type="range" class="gor-range" min="' + MIN_ROWS + '" max="' + MAX_ROWS + '" step="1" aria-label="Скільки рядів">'
      + '<button type="button" class="gor-pm" data-r="1" aria-label="Більше рядів">+</button></div></div>'
      + '<div class="gor-tools"><button type="button" class="gor-auto">▶ Авто</button>'
      + '<button type="button" class="gor-fast" title="Швидко котиться">⚡</button>'
      + '<button type="button" class="gor-snd" title="Звук">🔈</button></div>'
      + '<div class="gor-foot"><span class="gor-wal"></span><span class="gor-stat"></span>'
      + '<button type="button" class="gor-hash" title="Перевірка чесності" data-pad-skip></button></div>'
      + '</div><div class="gor-info" hidden></div></div>';
    const q = (s) => root.querySelector(s);
    st.el = {
      box: q('.gor'), stage: q('.gor-stage'), cv: q('.gor-cv'), glek: q('.gor-glek'), strip: q('.gor-strip'), ib: q('.gor-ib'), say: q('.gor-say'),
      fx: q('.gor-fx'), off: q('.gor-off'), go: q('.gor-go'), gt: q('.gor-gt'), gs: q('.gor-gs'), bets: q('.gor-bets'), ns: q('.gor-ns'),
      risks: q('.gor-risks'), rv: q('.gor-rv'), range: q('.gor-range'), rowsBox: q('.gor-rows'), auto: q('.gor-auto'), fast: q('.gor-fast'),
      snd: q('.gor-snd'), wal: q('.gor-wal'), stat: q('.gor-stat'), hash: q('.gor-hash'), info: q('.gor-info'),
    };
    st.el.risks.innerHTML = RISKS.map((r) => '<button type="button" data-k="' + r.id + '"><i>' + r.icon + '</i> ' + r.label + '</button>').join('');
    wire(st);
  }

  function wire(st) {
    const el = st.el;
    const on = (n, ev, fn, o) => { n.addEventListener(ev, fn, o); st.offs.push(() => n.removeEventListener(ev, fn, o)); };
    on(el.go, 'click', () => drop(st, false));
    on(el.bets, 'click', (e) => { const b = e.target.closest('button[data-b]'); if (b) { st.bet = +b.dataset.b; paintPanel(st); } });
    on(el.ns, 'click', (e) => { const b = e.target.closest('button[data-n]'); if (b) { st.n = +b.dataset.n; paintPanel(st); } });
    on(el.risks, 'click', (e) => { const b = e.target.closest('button[data-k]'); if (b) setBoard(st, st.rows, b.dataset.k); });
    on(el.rowsBox, 'click', (e) => { const b = e.target.closest('button[data-r]'); if (b) setBoard(st, st.rows + +b.dataset.r, st.risk); });
    on(el.range, 'input', () => setBoard(st, +el.range.value, st.risk));
    on(el.auto, 'click', () => setAuto(st, !st.auto));
    on(el.fast, 'click', () => { st.fast = !st.fast; store.set('fast', st.fast ? 1 : 0); paintPanel(st); });
    on(el.snd, 'click', () => { st.sound = !st.sound; store.set('snd', st.sound ? 1 : 0); if (st.sound) audio(st); paintPanel(st); });
    on(el.ib, 'click', () => info(st, true));
    on(el.hash, 'click', () => info(st, true));
    on(el.info, 'click', (e) => {
      if (e.target === el.info || e.target.closest('.gor-x0')) info(st, false);
      else if (e.target.closest('.gor-chk')) check(st);
    });
  }

  /// Ряди й ризик — лише поки горох не котиться (інакше горошини в повітрі опинились би на чужій дошці).
  function locked(st) { return st.busy || st.balls.length > 0 || st.queued > 0; }
  function setBoard(st, rows, risk) {
    rows = Math.max(MIN_ROWS, Math.min(MAX_ROWS, rows | 0));
    if (!RISK_OF[risk]) risk = 'low';
    if (rows === st.rows && risk === st.risk) return;
    if (locked(st)) { paintPanel(st); say(st, 'Хай спершу горох докотиться'); return; }
    st.rows = rows; st.risk = risk;
    relayout(st);
    paintPanel(st);
  }

  // ---------------------------------------------------------------------------------------------
  // Вид сервера → дошка
  // ---------------------------------------------------------------------------------------------

  function sync(st) {
    const v = cur(st);
    if (!v || !v.tables) return;
    if (!st.inited) {
      st.inited = true;
      st.bet = v.bet; st.rows = v.rows; st.risk = v.risk; st.n = Math.max(1, Math.min(v.balls || 1, v.maxBalls || 1));
      st.seen = v.seq;
      st.strip = [];
      for (const d of v.recent || []) for (const b of d.balls) if (st.strip.length < STRIP) st.strip.push(b.x);
      paintStrip(st);
      relayout(st);
    }
    if (v.bets && v.bets.length && !v.bets.includes(st.bet)) st.bet = v.bets.includes(v.bet) ? v.bet : v.bets[0];
    if (st.n > (v.maxBalls || 1)) st.n = v.maxBalls || 1;
    const fresh = (v.recent || []).filter((d) => d.seq > st.seen).sort((a, b) => a.seq - b.seq);
    for (const d of fresh) launch(st, d);
    if (v.seq > st.seen) st.seen = v.seq;
    paintPanel(st);
    pump(st);
  }

  function launch(st, d) {
    if (!st.shown || !st.geo) { for (const b of d.balls) addStrip(st, b.x); paintStrip(st); return; }
    if (d.rows !== st.rows || d.risk !== st.risk) {
      // кидок з іншої вкладки на іншій дошці: поки тут котиться своє — лише в стрічку
      if (st.balls.length) { for (const b of d.balls) addStrip(st, b.x); paintStrip(st); return; }
      st.rows = d.rows; st.risk = d.risk;
      relayout(st);
    }
    const now = performance.now();
    const seg = st.fast || reduced() ? FAST_MS : SEG_MS, stag = st.fast ? FAST_STAGGER : STAGGER;
    const t0 = Math.max(now, st.nextStart);
    d.balls.forEach((b, i) => {
      const R = [0];
      for (let j = 0; j < b.p.length; j++) R.push(R[j] + (b.p[j] === '1' ? 1 : 0));
      st.balls.push({ p: b.p, s: b.s, x: b.x, win: b.win, bet: d.bet, rows: d.rows, t0: t0 + i * stag, seg, R, jit: jitter(b.p, d.seq * 16 + i), hit: -1 });
      st.pendingWin += b.win;
    });
    st.nextStart = t0 + d.balls.length * stag;
    syncQuiet(st);
    kick(st);
  }

  /// Дрібний розкид по кілочках (частки проміжку), щоб горошини одного шляху не котились одна в одній. Детермінований.
  function jitter(p, salt) {
    let h = 2166136261 ^ salt;
    const out = [];
    for (let i = 0; i <= p.length; i++) {
      h = Math.imul(h ^ (p.charCodeAt(i % p.length) + i), 16777619) >>> 0;
      out.push(((h % 1000) / 1000 - 0.5) * 0.12);
    }
    return out;
  }

  // ---------------------------------------------------------------------------------------------
  // Кидок: черга, авто, відбиток
  // ---------------------------------------------------------------------------------------------

  /// Ще не дійшов вид після попереднього кидка — відбиток той самий, кидати під нього не можна.
  function waiting(st) { const v = cur(st); return !!(st.sentHash && v && v.hash === st.sentHash); }
  function cost(st) { return st.bet * st.n; }

  function drop(st, quietly) {
    const v = cur(st);
    if (!v || !v.on || !st.ctx.mine) return;
    if (st.busy || waiting(st)) {
      if (!quietly && st.queued < QUEUE_MAX) { st.queued++; paintPanel(st); }
      return;
    }
    send(st);
  }

  function send(st) {
    const v = cur(st);
    if (!v || !v.on || !v.hash) return;
    if ((v.balance || 0) < cost(st)) {
      say(st, 'Бракує черепків: кидок коштує ' + fmtN(cost(st)));
      st.queued = 0;
      setAuto(st, false);
      return;
    }
    st.busy = true; st.sentHash = v.hash; st.sentAt = performance.now();
    st.pre.add(v.hash);
    syncQuiet(st);
    pour(st);
    st.ctx.act('drop', { bet: st.bet, n: st.n, rows: st.rows, risk: st.risk, hash: v.hash })
      .catch(() => null)
      .then((r) => {
        st.busy = false;
        if (!r || !r.ok) { st.sentHash = null; st.queued = 0; setAuto(st, false); }
        syncQuiet(st);
        paintPanel(st);
        pump(st);
      });
    paintPanel(st);
  }

  /// Наступний кидок, якщо є черга чи авто, — щойно попередній дійшов і прийшов свіжий відбиток.
  function pump(st) {
    if (st.busy || !st.ctx || !st.ctx.mine) return;
    if (waiting(st)) {
      // відповідь була, а виду нема довго — попросити ще раз (сам прийде подією 'room')
      if (performance.now() - st.sentAt > 2000 && st.ctx.resync) st.ctx.resync();
      return;
    }
    if (st.queued > 0) { st.queued--; send(st); return; }
    if (!st.auto || st.autoT) return;
    const gap = st.fast ? FAST_AUTO_GAP : AUTO_GAP;
    st.autoT = setTimeout(() => {
      st.autoT = 0;
      if (!st.auto) return;
      if (st.busy || waiting(st)) { pump(st); return; }
      send(st);
    }, Math.max(0, st.sentAt + gap - performance.now()));
  }

  function setAuto(st, on) {
    on = !!on && !!st.ctx && st.ctx.mine;
    if (st.auto === on) return;
    st.auto = on;
    clearTimeout(st.autoT); st.autoT = 0;
    paintPanel(st);
    if (on) pump(st);
  }

  /// Тихий гаманець: поки запит у дорозі чи горох котиться, виграш у шапці притримано — щоб не випередив горошину.
  function syncQuiet(st) {
    const want = st.busy || st.balls.length > 0;
    if (want === st.quiet) return;
    st.quiet = want;
    quiet(want);
  }

  // ---------------------------------------------------------------------------------------------
  // Панель
  // ---------------------------------------------------------------------------------------------

  function paintPanel(st) {
    const v = cur(st), el = st.el;
    if (!v || !el) return;
    const sig = (v.bets || []).join(',') + '|' + v.maxBalls;
    if (sig !== st.chipSig) {
      st.chipSig = sig;
      el.bets.innerHTML = (v.bets || []).map((b) => '<button type="button" data-b="' + b + '">' + fmtN(b) + '</button>').join('');
      el.ns.innerHTML = NS.filter((n) => n <= (v.maxBalls || 1)).map((n) => '<button type="button" data-n="' + n + '">' + n + '</button>').join('');
    }
    el.bets.querySelectorAll('button').forEach((b) => b.classList.toggle('on', +b.dataset.b === st.bet));
    el.ns.querySelectorAll('button').forEach((b) => b.classList.toggle('on', +b.dataset.n === st.n));
    const lock = locked(st);
    el.risks.querySelectorAll('button').forEach((b) => { b.classList.toggle('on', b.dataset.k === st.risk); b.classList.toggle('dim', lock && b.dataset.k !== st.risk); });
    el.rv.textContent = st.rows;
    el.range.value = st.rows;
    el.range.disabled = lock;
    el.rowsBox.classList.toggle('dim', lock);
    const c = cost(st), mine = !!st.ctx.mine;
    const can = v.on && mine && (v.balance || 0) >= c;
    el.go.disabled = !v.on || !mine;
    el.go.classList.toggle('short', !can && v.on && mine);
    el.gt.textContent = st.n > 1 ? 'Кинути ' + st.n : 'Кинути';
    el.gs.textContent = (st.n > 1 ? st.n + ' × ' + fmtN(st.bet) + ' = ' : '') + fmtN(c) + ' 🏺' + (st.queued ? ' · ще ' + st.queued + ' у черзі' : '');
    el.auto.textContent = st.auto ? '⏹ Стоп' : '▶ Авто';
    el.auto.classList.toggle('on', st.auto);
    el.auto.disabled = !v.on || !mine;
    el.fast.classList.toggle('on', st.fast);
    el.snd.textContent = st.sound ? '🔊' : '🔈';
    el.snd.classList.toggle('on', st.sound);
    el.wal.innerHTML = mine ? 'у гаманці <b>' + fmtN(Math.max(0, (v.balance || 0) - st.pendingWin)) + '</b> 🏺' : '';
    el.stat.textContent = v.drops ? 'кинуто ' + fmtN(v.drops) + (v.best ? ' · найкраща ×' + String(v.best).replace('.', ',') : '') : '';
    el.hash.textContent = v.hash ? '🔒 ' + v.hash.slice(0, 8) + '…' : '';
    el.off.hidden = !!v.on;
  }

  function addStrip(st, x) {
    st.strip.unshift(x);
    if (st.strip.length > STRIP) st.strip.length = STRIP;
  }
  function paintStrip(st) {
    st.el.strip.innerHTML = st.strip.map((x, i) => '<i class="c' + tier(x) + (i === 0 ? ' new' : '') + '">' + fmtM(x) + '</i>').join('');
  }

  function say(st, text) {
    const el = st.el.say;
    el.textContent = text;
    el.classList.add('on');
    clearTimeout(st.sayT);
    st.sayT = setTimeout(() => el.classList.remove('on'), SAY_MS);
  }

  function pour(st) {
    const g = st.el.glek;
    g.classList.remove('pour');
    void g.offsetWidth;   // перезапустити анімацію
    g.classList.add('pour');
  }

  // ---------------------------------------------------------------------------------------------
  // Полотно: геометрія, фон, кадр
  // ---------------------------------------------------------------------------------------------

  function layout(st) {
    const b = st.el.stage.getBoundingClientRect();
    const W = Math.max(220, Math.round(b.width)), H = Math.max(220, Math.round(b.height));
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    if (st.g && st.g.W === W && st.g.H === H && st.g.dpr === dpr) return;
    st.g = { W, H, dpr };
    const cv = st.el.cv;
    cv.width = Math.round(W * dpr); cv.height = Math.round(H * dpr);
    cv.style.width = W + 'px'; cv.style.height = H + 'px';
    st.c2 = cv.getContext('2d');
    relayout(st);
  }

  /// Дошка на st.rows: кілочки трикутником (ряд j — j + 3 кілочки), під ними n + 1 глечиків.
  function geom(st) {
    const { W, H } = st.g, n = st.rows;
    const padX = Math.max(8, W * 0.03);
    const top = Math.max(54, H * 0.14);   // місце під Глека
    const gapW = (W - 2 * padX) / (n + 1.3);
    const gapH = (H - top - 8) / ((n - 1) * 0.88 + 0.75 + 1.05);
    const gap = Math.max(10, Math.min(gapW, gapH, 62));
    const rowGap = gap * 0.88;
    const boardH = (n - 1) * rowGap + rowGap * 0.75 + gap * 1.05;
    const y0 = top + Math.max(0, (H - top - 8 - boardH) * 0.4);
    const potY = y0 + (n - 1) * rowGap + rowGap * 0.75;
    return {
      n, gap, rowGap, cx: W / 2, y0, potY, potW: gap * 0.9, potH: gap * 1.05,
      pr: Math.max(1.8, gap * 0.1), br: Math.max(3.2, gap * 0.22), mouthY: y0 - rowGap * 1.5,
    };
  }
  const potX = (G, k) => G.cx + (k - G.n / 2) * G.gap;
  const pegX = (G, j, k) => G.cx + (k - (j + 2) / 2) * G.gap;

  function relayout(st) {
    if (!st.g) return;
    st.geo = geom(st);
    st.bumps = [];
    st.flashes.clear();
    paintBack(st);
    placeGlek(st);
    st.font = getComputedStyle(st.el.box).getPropertyValue('--display').trim() || 'system-ui, sans-serif';
    draw(st, performance.now());
  }

  function placeGlek(st) {
    const G = st.geo, img = st.el.glek;
    const w = Math.max(42, Math.min(84, G.gap * 2.6));
    img.style.width = w + 'px';
    img.style.left = Math.round(G.cx - G.gap * 0.6 - w) + 'px';
    img.style.top = Math.round(G.mouthY - w * 0.78) + 'px';
  }

  function paintBack(st) {
    const g = st.g, G = st.geo;
    const cv = st.bg || (st.bg = document.createElement('canvas'));
    cv.width = Math.round(g.W * g.dpr); cv.height = Math.round(g.H * g.dpr);
    const c = cv.getContext('2d');
    c.setTransform(g.dpr, 0, 0, g.dpr, 0, 0);
    // дерев'яна дошка: темний горіх, світліше за трикутником кілочків
    const bg = c.createLinearGradient(0, 0, 0, g.H);
    bg.addColorStop(0, '#3d2617'); bg.addColorStop(1, '#22140b');
    c.fillStyle = bg; c.fillRect(0, 0, g.W, g.H);
    c.globalAlpha = 0.07; c.strokeStyle = '#f0c890'; c.lineWidth = 1;
    for (let y = 9; y < g.H; y += 17) {
      c.beginPath(); c.moveTo(0, y);
      c.bezierCurveTo(g.W * 0.3, y + 5, g.W * 0.6, y - 5, g.W, y + 2);
      c.stroke();
    }
    c.globalAlpha = 1;
    const glow = c.createRadialGradient(G.cx, G.y0 + (G.potY - G.y0) * 0.55, G.gap, G.cx, G.y0 + (G.potY - G.y0) * 0.55, (G.n + 2) * G.gap * 0.7);
    glow.addColorStop(0, 'rgba(255, 214, 150, .12)'); glow.addColorStop(1, 'rgba(255, 214, 150, 0)');
    c.fillStyle = glow; c.fillRect(0, 0, g.W, g.H);
    // кілочки
    for (let j = 0; j < G.n; j++) {
      const y = G.y0 + j * G.rowGap;
      for (let k = 0; k < j + 3; k++) {
        const x = pegX(G, j, k);
        c.fillStyle = 'rgba(0, 0, 0, .45)';
        c.beginPath(); c.arc(x + 0.6, y + 1.2, G.pr, 0, Math.PI * 2); c.fill();
        c.fillStyle = '#f2e4c6';
        c.beginPath(); c.arc(x, y, G.pr, 0, Math.PI * 2); c.fill();
      }
    }
  }

  function kick(st) {
    if (!st.raf && st.shown && st.c2) st.raf = requestAnimationFrame((t) => frame(st, t));
  }

  function frame(st, now) {
    st.raf = 0;
    step(st, now);
    draw(st, now);
    if (st.balls.length || fxAlive(st, now)) kick(st);
  }

  function fxAlive(st, now) {
    for (const t of st.flashes.values()) if (now - t < 260) return true;
    for (const t of st.bumps) if (t && now - t < 360) return true;
    return false;
  }

  /// Ключова точка шляху: -1 — з-під Глека, 0…n−1 — торкання кілочка, n — у глечику.
  function keyPt(G, b, i) {
    if (i < 0) return { x: G.cx, y: G.mouthY };
    if (i >= b.rows) return { x: potX(G, b.s), y: G.potY + G.potH * 0.42 };
    return { x: G.cx + (2 * b.R[i] - i) * G.gap / 2 + b.jit[i] * G.gap, y: G.y0 + i * G.rowGap - G.pr - G.br * 0.85 };
  }

  /// Де горошина на мить now: { x, y, k — остання досягнута ключова точка, done }.
  function where(G, b, now) {
    const t = now - b.t0;
    if (t < 0) return null;
    const first = b.seg * 1.6, last = b.seg * 1.4, n = b.rows;
    let i, u;
    if (t < first) { i = -1; u = t / first; }
    else {
      const tt = t - first, j = Math.floor(tt / b.seg);
      if (j < n - 1) { i = j; u = (tt - j * b.seg) / b.seg; }
      else {
        const tl = tt - (n - 1) * b.seg;
        if (tl >= last) { const p = keyPt(G, b, n); return { x: p.x, y: p.y, k: n, done: true }; }
        i = n - 1; u = tl / last;
      }
    }
    const A = keyPt(G, b, i), B = keyPt(G, b, i + 1);
    const x = A.x + (B.x - A.x) * u;
    const y = i < 0 ? A.y + (B.y - A.y) * u * u                         // вільне падіння з-під Глека
      : A.y + (B.y - A.y) * u - G.rowGap * (i === n - 1 ? 0.2 : 0.34) * 4 * u * (1 - u);   // відскок: трохи вгору, далі вниз
    return { x, y, k: i, done: false };
  }

  function step(st, now) {
    const G = st.geo;
    if (!G) return;
    let landed = false;
    for (const b of st.balls) {
      const w = where(G, b, now);
      if (!w) continue;
      const upto = w.done ? b.rows - 1 : w.k;
      while (b.hit < upto) {
        b.hit++;
        if (b.hit < 0) continue;
        st.flashes.set(b.hit + ':' + (b.R[b.hit] + 1), now);
        tick(st, b.hit);
      }
      if (w.done) { b.landed = true; landed = true; land(st, b, now); }
    }
    if (landed) {
      st.balls = st.balls.filter((b) => !b.landed);
      paintStrip(st);
      paintPanel(st);
      if (!st.balls.length) { st.nextStart = 0; syncQuiet(st); }
    }
  }

  function land(st, b, now) {
    st.pendingWin -= b.win;
    st.bumps[b.s] = now;
    addStrip(st, b.x);
    chime(st, b.x);
    const G = st.geo;
    const late = now - (b.t0 + b.seg * (b.rows + 2)) > 800;   // повернулись на вкладку — не сиплемо купу написів
    if (!late && b.x >= 20 && st.el.fx.childElementCount < POPS_MAX) {
      const p = document.createElement('div');
      p.className = 'gor-pop t' + tier(b.x);
      p.textContent = b.x >= 100 ? '+' + fmtN(b.win) : fmtX(b.x);
      p.style.left = potX(G, b.s) + 'px';
      p.style.top = (G.potY - G.gap * 0.2) + 'px';
      st.el.fx.appendChild(p);
      setTimeout(() => p.remove(), 1500);
    }
    if (b.x >= 1000) {
      say(st, SAYS[Math.floor(Math.random() * SAYS.length)]);
      const s = st.el.stage;
      s.classList.remove('shake'); void s.offsetWidth; s.classList.add('shake');
      pour(st);
    } else if (b.x >= 100 && !late) {
      const s = st.el.stage;
      s.classList.remove('glow'); void s.offsetWidth; s.classList.add('glow');
    }
  }

  function draw(st, now) {
    const c = st.c2, g = st.g, G = st.geo;
    if (!c || !G) return;
    c.setTransform(g.dpr, 0, 0, g.dpr, 0, 0);
    c.drawImage(st.bg, 0, 0, g.W, g.H);
    // спалахи кілочків
    for (const [key, t] of st.flashes) {
      const a = 1 - (now - t) / 260;
      if (a <= 0) { st.flashes.delete(key); continue; }
      const [j, k] = key.split(':').map(Number);
      const x = pegX(G, j, k), y = G.y0 + j * G.rowGap;
      c.fillStyle = 'rgba(255, 220, 120,' + (0.35 * a) + ')';
      c.beginPath(); c.arc(x, y, G.pr * (1.6 + 2.2 * (1 - a)), 0, Math.PI * 2); c.fill();
      c.fillStyle = 'rgba(255, 246, 214,' + a + ')';
      c.beginPath(); c.arc(x, y, G.pr * 1.15, 0, Math.PI * 2); c.fill();
    }
    // глечики
    const t = tableOf(cur(st), st.rows, st.risk);
    const small = G.gap < 30;
    for (let k = 0; k <= G.n; k++) {
      const x = t[k] == null ? 10 : t[k];
      const bt = st.bumps[k], dt = bt ? now - bt : 1e9;
      const bump = dt < 360 ? Math.sin((dt / 360) * Math.PI) : 0;
      jug(c, potX(G, k), G.potY + bump * G.potH * 0.16, G.potW, G.potH, potRgb(x), bump);
      const fs = Math.max(7, Math.min(15, G.gap * (fmtM(x).length > 3 ? 0.27 : 0.34)));
      c.font = '800 ' + fs.toFixed(1) + 'px ' + st.font;
      c.textAlign = 'center'; c.textBaseline = 'middle';
      c.lineWidth = Math.max(2, fs * 0.28); c.strokeStyle = 'rgba(30, 12, 4, .85)'; c.lineJoin = 'round';
      const label = (small ? '' : '×') + fmtM(x);
      const ly = G.potY + bump * G.potH * 0.16 + G.potH * 0.62;
      c.strokeText(label, potX(G, k), ly);
      c.fillStyle = x >= 10 ? '#fff8e6' : '#d8deea';
      c.fillText(label, potX(G, k), ly);
    }
    // горох
    for (const b of st.balls) {
      const w = where(G, b, now);
      if (!w) continue;
      pea(c, w.x, w.y, G.br);
    }
  }

  /// Глечик: шийка з вінцями, округлий тулуб; bump (0…1) — підстрибнув від горошини, світиться.
  function jug(c, x, y, w, h, col, bump) {
    const nw = w * 0.5, nh = h * 0.2;
    c.beginPath();
    c.moveTo(x - nw / 2, y + nh * 0.35);
    c.quadraticCurveTo(x - nw / 2, y + nh * 1.25, x - w / 2, y + nh * 1.9);
    c.quadraticCurveTo(x - w / 2 - w * 0.06, y + h * 0.85, x - w * 0.3, y + h);
    c.lineTo(x + w * 0.3, y + h);
    c.quadraticCurveTo(x + w / 2 + w * 0.06, y + h * 0.85, x + w / 2, y + nh * 1.9);
    c.quadraticCurveTo(x + nw / 2, y + nh * 1.25, x + nw / 2, y + nh * 0.35);
    c.closePath();
    const gr = c.createLinearGradient(x - w / 2, y, x + w / 2, y + h);
    gr.addColorStop(0, rgb(shade(col, 0.28 + bump * 0.3)));
    gr.addColorStop(1, rgb(shade(col, -0.35)));
    if (bump > 0) { c.shadowColor = rgb(shade(col, 0.4), 0.9); c.shadowBlur = 14 * bump; }
    c.fillStyle = gr; c.fill();
    c.shadowBlur = 0;
    c.lineWidth = 1; c.strokeStyle = 'rgba(0, 0, 0, .4)'; c.stroke();
    // вінця
    c.fillStyle = rgb(shade(col, 0.45));
    c.beginPath(); c.ellipse(x, y + nh * 0.35, nw * 0.62, Math.max(1.4, nh * 0.42), 0, 0, Math.PI * 2); c.fill();
    c.fillStyle = 'rgba(20, 8, 2, .55)';
    c.beginPath(); c.ellipse(x, y + nh * 0.35, nw * 0.4, Math.max(0.8, nh * 0.22), 0, 0, Math.PI * 2); c.fill();
    // відблиск
    c.fillStyle = 'rgba(255, 255, 255, .16)';
    c.beginPath(); c.ellipse(x - w * 0.2, y + h * 0.52, w * 0.07, h * 0.2, 0.2, 0, Math.PI * 2); c.fill();
  }

  function pea(c, x, y, r) {
    c.fillStyle = 'rgba(0, 0, 0, .3)';
    c.beginPath(); c.arc(x + r * 0.2, y + r * 0.35, r, 0, Math.PI * 2); c.fill();
    const gr = c.createRadialGradient(x - r * 0.35, y - r * 0.4, r * 0.1, x, y, r);
    gr.addColorStop(0, '#c8f08a'); gr.addColorStop(0.55, '#7cc63e'); gr.addColorStop(1, '#3f7f1d');
    c.fillStyle = gr;
    c.beginPath(); c.arc(x, y, r, 0, Math.PI * 2); c.fill();
    c.lineWidth = Math.max(0.8, r * 0.18); c.strokeStyle = '#2b5513'; c.stroke();
  }

  // ---------------------------------------------------------------------------------------------
  // Звук (WebAudio-синт; лише коли гравець увімкнув)
  // ---------------------------------------------------------------------------------------------

  function audio(st) {
    if (!st.sound) return null;
    if (!st.ac) {
      const AC = window.AudioContext || window.webkitAudioContext;
      if (!AC) return null;
      st.ac = new AC();
      st.master = st.ac.createGain(); st.master.gain.value = 0.45; st.master.connect(st.ac.destination);
    }
    if (st.ac.state === 'suspended') st.ac.resume();
    return st.ac;
  }

  function blip(st, f, dur, type, vol, at) {
    const ac = audio(st);
    if (!ac) return;
    const o = ac.createOscillator(), g = ac.createGain(), t = ac.currentTime + (at || 0);
    o.type = type; o.frequency.setValueAtTime(f, t);
    g.gain.setValueAtTime(0.0001, t);
    g.gain.exponentialRampToValueAtTime(vol, t + 0.006);
    g.gain.exponentialRampToValueAtTime(0.0001, t + dur);
    o.connect(g); g.connect(st.master);
    o.start(t); o.stop(t + dur + 0.02);
  }

  /// «Тік» об кілочок: що нижче — то трохи вище; не частіше ніж раз на 28 мс (10 горошин разом — не тріскіт).
  function tick(st, row) {
    if (!st.sound) return;
    const now = performance.now();
    if (now - st.tickAt < 28) return;
    st.tickAt = now;
    blip(st, 1300 + row * 45 + Math.random() * 120, 0.045, 'triangle', 0.035);
  }

  /// «Дзень» у глечик: програш — глухо, виграш — вище, великий — акорд.
  function chime(st, x) {
    if (!st.sound) return;
    if (x < 10) { blip(st, 190, 0.12, 'sine', 0.05); return; }
    const base = 440 * Math.pow(2, Math.min(2, Math.log10(x / 10)) / 2);
    blip(st, base, 0.22, 'sine', 0.07);
    if (x >= 30) blip(st, base * 1.25, 0.22, 'sine', 0.06, 0.07);
    if (x >= 100) blip(st, base * 1.5, 0.3, 'sine', 0.06, 0.14);
    if (x >= 1000) blip(st, base * 2, 0.5, 'triangle', 0.06, 0.24);
  }

  // ---------------------------------------------------------------------------------------------
  // ⓘ Правила й «чесно наперед»
  // ---------------------------------------------------------------------------------------------

  function choose(n, k) { let c = 1; for (let i = 1; i <= k; i++) c = c * (n - k + i) / i; return c; }

  function info(st, open) {
    const el = st.el.info;
    if (!open) { el.hidden = true; return; }
    const v = cur(st) || {};
    const n = st.rows, t = tableOf(v, n, st.risk);
    const half = [];
    for (let k = 0; k <= Math.floor(n / 2); k++) {
      const p = (k === n - k ? 1 : 2) * choose(n, k) / Math.pow(2, n);
      half.push('<tr><td><i class="gor-dot c' + tier(t[k]) + '"></i>' + (k === 0 ? 'край' : k === Math.floor(n / 2) ? 'центр' : (k + 1) + '-й від краю') + '</td>'
        + '<td>' + fmtX(t[k]) + '</td><td>' + (p >= 0.01 ? (p * 100).toFixed(1).replace('.', ',') + ' %' : '1 з ' + fmtN(Math.round(1 / p))) + '</td></tr>');
    }
    const d = (v.recent || [])[0];
    el.innerHTML = '<div class="gor-card"><button type="button" class="gor-x0" aria-label="Закрити">✕</button>'
      + '<h3>Горохова дошка</h3><ul>'
      + '<li>Обери ставку за горошину, скільки горошин кинути, ряди й ризик — і тисни «Кинути» (пробіл, Ⓐ).</li>'
      + '<li>На кожному кілочку горошина відскакує вліво чи вправо — навпіл. Де впала, такий і виграш: <b>ставка × множник глечика</b>.</li>'
      + '<li>Більше рядів і вищий ризик — далекі краї: на 16 рядах із високим ризиком крайній глечик дає ×1000, зате середні — ×0,2.</li>'
      + '<li>Повернення — ' + (v.rtp || 98) + ' % на будь-якій дошці. Горошина від ×20 — у «Заносах тижня», від ×100 — Глек розкаже в Балачках.</li>'
      + '<li>▶ Авто кидає сам, поки не зупиниш чи не скінчаться черепки; ⚡ — горох котиться швидше.</li></ul>'
      + '<h3>Ця дошка: ' + n + ' рядів, ризик ' + RISK_OF[st.risk] + '</h3>'
      + '<table class="gor-tab"><tr><th>глечик</th><th>множник</th><th>шанс</th></tr>' + half.join('') + '</table>'
      + '<p class="gor-f">Шанс — разом для обох однакових глечиків, лівого й правого.</p>'
      + '<h3>Чесно наперед</h3><div class="gor-f">Перед кидком Глек показує відбиток наступної жмені — <code>sha256(seed)</code>, після кидка — сам seed. '
      + 'Горошина №i: біти <code>sha256("seed:i")</code> згори вниз, 1 — вправо; глечик — скільки разів управо. Шлях не залежить ні від ставки, ні від ризику.</div>'
      + '<div class="gor-kv"><span>наступний кидок</span><code>' + esc(v.hash || '—') + '</code></div>'
      + (d ? '<div class="gor-kv"><span>кидок №' + d.seq + ' (' + d.balls.length + ' × ' + fmtN(d.bet) + ' 🏺, ' + d.rows + ' рядів, ' + RISK_OF[d.risk] + '), seed</span><code>' + esc(d.seed) + '</code>'
        + '<span>відбиток</span><code>' + esc(d.hash) + '</code></div>'
        + '<button type="button" class="gor-chk">Перевірити</button><div class="gor-res"></div>' : '')
      + '</div>';
    el.hidden = false;
  }

  async function sha256(s) {
    return new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(s)));
  }
  const hex = (b) => Array.from(b, (x) => x.toString(16).padStart(2, '0')).join('');
  /// Шлях горошини №i — як PlinkoCore.Path на сервері.
  async function path(seed, i, rows) {
    const h = await sha256(seed + ':' + i);
    let p = '';
    for (let j = 0; j < rows; j++) p += (h[j >> 3] >> (7 - (j & 7))) & 1;
    return p;
  }

  async function check(st) {
    const v = cur(st), d = v && v.recent && v.recent[0], out = st.el.info.querySelector('.gor-res');
    if (!d || !out) return;
    if (!window.crypto || !crypto.subtle) { out.textContent = 'Цей браузер не рахує sha256 (потрібен https)'; return; }
    out.textContent = 'Рахую…';
    try {
      const okH = hex(await sha256(d.seed)) === String(d.hash).toLowerCase();
      const t = tableOf(v, d.rows, d.risk);
      let okB = true;
      for (let i = 0; i < d.balls.length; i++) {
        const b = d.balls[i], p = await path(d.seed, i + 1, d.rows);
        const s = p.split('').filter((c) => c === '1').length;
        if (p !== b.p || s !== b.s || t[s] !== b.x || Math.floor(d.bet * t[s] / 10) !== b.win) okB = false;
      }
      out.innerHTML = (okH ? '✅ sha256(seed) збігся з відбитком' + (st.pre.has(d.hash) ? ', який ти бачив до кидка' : '') : '❌ відбиток НЕ збігся')
        + '<br>' + (okB ? '✅ ' : '❌ ') + d.balls.length + ' горошин(и) за формулою — ' + (okB ? 'ті самі глечики й виграш' : 'НЕ ті глечики');
    } catch (e) { out.textContent = 'Не вийшло перевірити: ' + e.message; }
  }

  // ---------------------------------------------------------------------------------------------
  // Клавіші, пад, реєстрація
  // ---------------------------------------------------------------------------------------------

  function stepBet(st, dir) {
    const v = cur(st), bets = (v && v.bets) || [];
    const i = bets.indexOf(st.bet);
    st.bet = bets[Math.max(0, Math.min(bets.length - 1, (i < 0 ? 0 : i) + dir))] || st.bet;
    paintPanel(st);
  }
  function stepRisk(st) {
    const i = RISKS.findIndex((r) => r.id === st.risk);
    setBoard(st, st.rows, RISKS[(i + 1) % RISKS.length].id);
  }

  function onKey(e, ctx) {
    const st = ctx._gor;
    if (!st || !st.el) return false;
    const tag = e.target && e.target.tagName;
    if (tag === 'INPUT' && e.target.type !== 'range') return false;
    if (tag === 'TEXTAREA') return false;
    if (!st.el.info.hidden) {
      if (e.code === 'Escape' || e.code === 'Enter' || e.code === 'Space') { info(st, false); return true; }
      return false;
    }
    if (!ctx.mine) return false;
    const code = e.code;
    if (code === 'Space' || code === 'Enter') { if (!e.repeat) drop(st, false); return true; }
    if (e.repeat) return false;
    if (code === 'KeyA') { setAuto(st, !st.auto); return true; }
    if (code === 'ArrowLeft' || code === 'ArrowRight') { setBoard(st, st.rows + (code === 'ArrowRight' ? 1 : -1), st.risk); return true; }
    if (code === 'ArrowUp' || code === 'ArrowDown') { stepBet(st, code === 'ArrowUp' ? 1 : -1); return true; }
    if (code === 'KeyR') { stepRisk(st); return true; }
    return false;
  }

  function status(ctx) {
    const st = ctx && ctx._gor, v = st && cur(st);
    if (!v) return 'Горохова дошка';
    if (!v.on) return 'Дошка на перерві';
    if (st.auto) return '▶ Авто: ' + st.rows + ' рядів, ризик ' + RISK_OF[st.risk];
    const d = (v.recent || [])[0];
    if (d && d.win) return 'Останній кидок: +' + fmtN(d.win) + ' 🏺';
    return 'Кидай горох — куди впаде?';
  }

  function stop(st) {
    if (st.raf) cancelAnimationFrame(st.raf);
    st.raf = 0;
  }

  HGames.register({
    id: 'plinko',
    added: '2026-10-20',
    icon: ICON,
    seatNames: () => 'гравець',
    pad: {
      dirs: 'x',
      a: 'Space',
      x: 'KeyA',
      hint: '{a} кинути · {x} авто · {dpad} ряди · {lb}{rb} ставка · {y} ризик',
      on(btn, ctx) {
        const st = ctx && ctx._gor;
        if (!st) return false;
        if (btn === 'lb' || btn === 'rb') { stepBet(st, btn === 'rb' ? 1 : -1); return true; }
        if (btn === 'y') { stepRisk(st); return true; }
        return false;
      },
      when: (ctx) => ctx.mine,
    },
    mount(root, ctx) {
      const st = setup(root, ctx);
      layout(st);
      sync(st);
    },
    update(root, ctx) {
      const st = setup(root, ctx);
      if (!st.g) layout(st);
      sync(st);
    },
    visible(root, ctx, on) {
      const st = root._gor;
      if (!st) return;
      st.shown = on;
      if (on) { layout(st); relayout(st); sync(st); kick(st); return; }
      stop(st);
      setAuto(st, false);
      // сховали посеред падіння — горох «докотився»: у стрічку, а виграш у шапку зараз, а не коли повернешся
      for (const b of st.balls) { st.pendingWin -= b.win; addStrip(st, b.x); }
      st.balls = []; st.nextStart = 0;
      paintStrip(st);
      syncQuiet(st);
    },
    onKey,
    status,
    unmount(root) {
      const st = root._gor;
      if (!st) return;
      stop(st);
      clearTimeout(st.roT); clearTimeout(st.sayT); clearTimeout(st.autoT);
      st.auto = false;
      if (st.ro) st.ro.disconnect();
      st.offs.forEach((f) => f()); st.offs = [];
      if (st.quiet) { st.quiet = false; quiet(false); }
      if (st.ac) { try { st.ac.close(); } catch { /* уже закрито */ } }
      if (st.ctx) st.ctx._gor = null;
      root._gor = null;
    },
    // для перевірок: формула шляху й кольори без DOM
    qa: { path, fmtM, potRgb, where, keyPt },
  });
})();
