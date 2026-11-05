/*
  «Соляний шлях» — прокладай шляхи за замовленнями, на 2–5 (id solyanyi). Правила живуть на сервері (Impl/Solyanyi*.cs) —
  тут лише картка столу: що бачить і натискає гравець. Контракт виду й дій — docs/games/specs/solyanyi.md (§5–§7, §9, §12).

  Поле — 3D-мапа (/games/solyanyi/scene.js) або пласка (/games/solyanyi/board2d.js) з тим самим API (§10); обидві тягнемо
  через import(). Сталі — consts.js, вигляди — themes.js, чисті розрахунки — hud-calc.js, малюнки — theme-art.js.

  Три вигляди на вибір гравця («🎨 Вигляд», localStorage 'sol.theme', ?theme=), кожен зі своєю розкладкою (solyanyi.css):
  parchment «Стара мапа» (типова; шкіряна колонка ліворуч, торба внизу) · board «Настілка на столі» (горіховий стіл,
  рахівниця довкола мапи, паперові картки праворуч, рука віялом) · petryk «Петриківка» (чорний лак, колонка праворуч,
  карти-малюнки, вишиті шви). Усі частини — прямі діти .sol, сітка кожного вигляду кладе їх у свої клітинки.
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true"><path d="M2 8C1.5 3 14.5 3 14 8Z" fill="#f7efd9" stroke="var(--accent)" stroke-width="1"/><rect x="1.5" y="8" width="13" height="2" rx=".6" fill="#8f5a2a" stroke="var(--accent)" stroke-width=".7"/><circle cx="4.6" cy="12" r="2" fill="none" stroke="var(--accent)" stroke-width="1.1"/><circle cx="11.4" cy="12" r="2" fill="none" stroke="var(--accent)" stroke-width="1.1"/></svg>';

  let M = null;           // { C: consts, T: themes, K: hud-calc, A: theme-art, S: sound | null } — після load()
  let loading = null;
  let cur = null;         // root змонтованої картки (onKey, pad)

  function load() {
    if (!loading) {
      loading = Promise.all([import('/games/solyanyi/consts.js'), import('/games/solyanyi/themes.js'), import('/games/solyanyi/hud-calc.js'), import('/games/solyanyi/theme-art.js'), import('/games/solyanyi/sound.js').catch(() => null)])
        .then(([C, T, K, A, S]) => { M = { C, T, K, A, S }; return M; })
        .catch((e) => { loading = null; throw e; });
    }
    return loading;
  }

  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
  const reduced = () => !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
  const coarse = () => !!(HGames.ui && HGames.ui.coarse ? HGames.ui.coarse() : window.matchMedia && window.matchMedia('(pointer: coarse)').matches);
  const store = {
    get(k) { try { return localStorage.getItem(k); } catch (e) { return null; } },
    set(k, v) { try { localStorage.setItem(k, v); } catch (e) { /* приватне вікно — без пам'яті */ } },
  };
  function setHtml(el, html) { if (el && el._h !== html) { el._h = html; el.innerHTML = html; } }
  const clamp = (x, a, b) => Math.max(a, Math.min(b, x));
  const px = (n) => Math.round(n) + 'px';
  function setVar(el, k, v) { if (el.style.getPropertyValue(k) !== v) el.style.setProperty(k, v); }
  const cap = (p, ms) => Promise.race([Promise.resolve(p).catch(() => {}), new Promise((r) => setTimeout(r, ms))]);
  const wait = (ms) => new Promise((r) => setTimeout(r, reduced() ? Math.min(ms, 60) : ms));
  const DOUBLE_TAP_MS = 500, DRAW_LOCK_MS = 5000;

  // ─── Вигляд ─────────────────────────────────────────────────────────────
  function themeNow() {
    try { const q = new URLSearchParams(location.search).get('theme'); if (q && M.T.THEMES.some((t) => t.id === q)) store.set('sol.theme', q); } catch (e) { /* без адреси */ }
    const s = store.get('sol.theme');
    return M.T.THEMES.some((t) => t.id === s) ? s : M.T.DEFAULT_THEME;
  }
  const seatColor = (st, color) => M.T.themeOf(st.theme).pal.seat[color] || M.C.SEAT_COLOR[color] || '#999';

  // ─── Хто є хто ───────────────────────────────────────────────────────────
  const playerOf = (v, seat) => (v && v.players || []).find((p) => p.seat === seat) || null;
  const cleanNick = (s) => String(s || '').replace(/\s*🤖\s*/g, '').trim();
  const nickOf = (v, seat) => { const p = playerOf(v, seat); return p ? p.nick : 'місце ' + (seat + 1); };
  const sameNick = (a, b) => String(a || '').toLowerCase() === String(b || '').toLowerCase();
  /// Моє місце, за яке їде Глек, поки я дивлюсь збоку (вийшов чи випав): сервер тримає його (Game.Rejoin).
  const ownAway = (v, nick) => (v && v.phase !== 'over' && !v.over && nick
    && ((v.players || []).find((p) => !p.bot && p.away && sameNick(p.nick, nick)))) || null;
  const REJOIN = '🛞 Повернутись до своєї валки';
  const shapeTag = (st, p) => '<i class="sol-sh" style="color:' + seatColor(st, p.color) + '">' + M.C.SEAT_SHAPE[p.color] + '</i>';

  function state(root) {
    return root._sl || (root._sl = {
      built: false, ctx: null, view: null, sc: null, scKind: null, scLoading: false, scGen: 0, no3d: false, boardSig: null,
      sel: null, pick: null, cur: null, pathT: null, seq: null, seqPhase: null, queue: [], running: false, tick: 0, nt: 0, toKey: null, toAt: 0,
      theme: null, mode: null, fitRaf: 0, u: 1, sheet: null, pickQ: '', tab: 'log', keep: null, keepKey: null, pop: false, tipNow: false, turnKey: null,
      sumMin: false, sumKey: null, fin: null, geo: null, ticker: [], railSig: null, tableSig: null, hoverT: 0, whichAt: null, attn: 0, titleT: 0, noteOverDone: null,
      own: 0, ownSeat: null, drawLock: null, drawSlot: null, drawAt: 0, ntView: null, ntMine: false, homeSig: null,
    });
  }

  // ─── Каркас DOM ─────────────────────────────────────────────────────────
  const PARTS = ['bg', 'deco', 'title', 'banner', 'players', 'bar', 'fieldwrap', 'seam', 'market', 'hand', 'tickets', 'route', 'log', 'glek', 'keep', 'sheet', 'sum', 'pop', 'fx'];
  function build(root, st) {
    root.innerHTML = '<div class="sol">'
      + '<div class="sol-bg" aria-hidden="true"></div><div class="sol-deco" aria-hidden="true"></div>'
      + '<div class="sol-fieldwrap"><div class="sol-railh" aria-hidden="true"></div><div class="sol-field"></div><div class="sol-load" hidden><span class="sol-loadin"><i class="sol-loadd"></i>Розгортаємо мапу…</span></div>'
      + '<div class="sol-tools"></div><div class="sol-zoom"></div><div class="sol-mini" hidden></div>'
      + '<div class="sol-bubble" hidden></div><div class="sol-ticker" aria-live="polite"></div><div class="sol-lastban" hidden></div><div class="sol-flash" hidden></div><div class="sol-hov" hidden></div><div class="sol-which" hidden></div></div>'
      + '<div class="sol-title"></div><div class="sol-seam" aria-hidden="true"></div>'
      + '<div class="sol-banner" aria-live="polite"></div><div class="sol-players"></div><div class="sol-bar"></div>'
      + '<div class="sol-market"></div><div class="sol-hand"></div><div class="sol-tickets"></div><div class="sol-log"></div>'
      + '<div class="sol-route" hidden></div><div class="sol-glek"></div>'
      + '<div class="sol-keep" hidden></div><div class="sol-sheet" hidden></div><div class="sol-sum" hidden></div><div class="sol-pop" hidden></div><div class="sol-fx" aria-hidden="true"></div>'
      + '</div>';
    st.el = {};
    for (const k of PARTS) st.el[k] = root.querySelector('.sol-' + k);
    st.el.root = root.querySelector('.sol');
    st.el.field = root.querySelector('.sol-field');
    for (const k of ['tools', 'zoom', 'mini', 'bubble', 'ticker', 'lastban', 'flash', 'hov', 'which', 'railh', 'load']) st.el[k] = root.querySelector('.sol-' + k);
    try { st.snd = M.S && M.S.createSound ? M.S.createSound() : null; } catch (e) { st.snd = null; }
    st.onClick = (e) => onClick(root, e);
    root.addEventListener('click', st.onClick);
    st.onInput = (e) => { if (e.target.classList && e.target.classList.contains('sol-search')) { st.pickQ = e.target.value; renderPickRows(st); } };
    root.addEventListener('input', st.onInput);
    st.onPtr = (e) => { st.lastPt = { x: e.clientX, y: e.clientY, t: Date.now() }; };
    st.el.field.addEventListener('pointerdown', st.onPtr, true);
    st.onDocDown = (e) => { if (st.pop && !e.target.closest('.sol-pop, [data-a="pop"]')) { st.pop = false; renderPop(st); renderTools(st); } };
    document.addEventListener('pointerdown', st.onDocDown, true);
    st.ro = typeof ResizeObserver === 'function' ? new ResizeObserver(() => { if (st.sc) st.sc.resize(); }) : null;
    if (st.ro) st.ro.observe(st.el.field);
    st.ro2 = typeof ResizeObserver === 'function' ? new ResizeObserver(() => refit(root, st)) : null;
    if (st.ro2) {
      st.ro2.observe(st.el.root);
      const gt = root.closest('.gtable');
      if (gt) st.ro2.observe(gt);
    }
    if (HGames.ui && HGames.ui.onFit) HGames.ui.onFit(root, () => fitLayout(root, st));
    else window.addEventListener('resize', st.onWin = () => refit(root, st));
    st.tick = setInterval(() => tick(root), 250);
    st.built = true;
    fetch(M.C.MAP.url).then((r) => r.json()).then((g) => { st.geo = M.K.parseGeo(g); if (st.view) paint(root, st.ctx); }).catch(() => { /* міні-мапи без контуру */ });
    paintTheme(st);
  }

  /// Те, що не залежить від виду: тло, назва, оздоби, символи карт.
  function paintTheme(st) {
    const A = M.A, t = st.theme, k = st.el.root;
    for (const x of M.T.THEMES) k.classList.toggle('sol-t-' + x.id, x.id === t);
    const old = k.querySelector('.sol-defs');
    if (old) old.remove();
    k.insertAdjacentHTML('afterbegin', A.defs(t));
    setHtml(st.el.title, A.titleHtml(t));
    setHtml(st.el.deco, t === 'board' ? '<span class="sol-tea">' + A.teaHtml(138) + '</span><span class="sol-salt">' + A.saltHtml(116, 74, 150, 5) + '</span>' : '');
    setVar(k, '--sol-stitch', t === 'petryk' ? A.stitchUrl(10) : 'none');
    setVar(k, '--sol-leather', A.LEATHER);
    st.tableSig = null; st.railSig = null;
    if (t !== 'board') setHtml(st.el.bg, '');
    if (t !== 'board') setHtml(st.el.railh, '');
  }

  // ─── Звук (типово вимкнено) ─────────────────────────────────────────────
  function sfx(st, name, opts) { if (st.snd) { try { st.snd.play(name, opts); } catch (e) { /* звук не головне */ } } }

  // ─── «На весь екран» ────────────────────────────────────────────────────
  function refit(root, st) {
    if (st.fitRaf) return;
    st.fitRaf = requestAnimationFrame(() => { st.fitRaf = 0; if (root._sl === st) fitLayout(root, st); });
  }

  /// Висота гри (--sol-h) = вікно − усе над карткою − що під нею − низ (ПК: нижнє поле <main>; телефон: --gdock-h). Режим — від
  /// ширини картки й вікна. Далі все від геометрії, не від змісту (поле не стрибає щоходу): масштаб панелей --sol-u, ширини
  /// колонок вигляду, дошка «Настілки» з рахівницею.
  function fitLayout(root, st) {
    const k = st.el && st.el.root;
    if (!k || !k.isConnected) return;
    const gt = root.closest('.gtable');
    const W = k.clientWidth || root.clientWidth;
    if (!W) return;
    const vw = window.innerWidth, vh = window.innerHeight;
    const mode = vw > vh && vh <= 560 ? 'ls' : W >= 700 ? 'pc' : 'ph';
    const was = st.mode;
    st.mode = mode;
    k.classList.add('sol-on');
    for (const m of ['pc', 'ph', 'ls']) k.classList.toggle('sol-m-' + m, m === mode);
    if (gt) {
      gt.classList.add('sol-gt');
      for (const x of M.T.THEMES) gt.classList.toggle('sol-gt-' + x.id, x.id === st.theme);
      gt.classList.toggle('sol-gt-play', headCompact(st.ctx));
    }
    if (HGames.ui && HGames.ui.headIn) HGames.ui.headIn(root, true);
    const de = getComputedStyle(document.documentElement), bs = getComputedStyle(document.body);
    const dock = parseFloat(de.getPropertyValue('--gdock-h')) || parseFloat(bs.getPropertyValue('--gdock-h')) || 0;
    const kr = k.getBoundingClientRect();
    const box = gt || k, br = box.getBoundingClientRect();
    const page = box.closest('main');
    const below = Math.max(0, br.bottom - kr.bottom);
    const y0 = kr.top + window.scrollY;
    const tail = mode === 'pc' ? (page ? parseFloat(getComputedStyle(page).paddingBottom) || 0 : 0) : dock + (gt ? 6 : 0);
    const minH = mode === 'pc' ? 480 : mode === 'ls' ? 240 : 460;
    let H = Math.max(minH, Math.floor(vh - tail - y0 - below));
    // Телефон лежачи: картка каркаса — вікно з прокруткою, гра — до її нижнього поля (як у Скарбі).
    const gcs = mode === 'ls' && gt ? getComputedStyle(gt) : null;
    const gmax = gcs && /auto|scroll/.test(gcs.overflowY) ? parseFloat(gcs.maxHeight) : NaN;
    if (gmax > 0) {
      const inner = br.top + gmax - (parseFloat(gcs.paddingBottom) || 0) - (parseFloat(gcs.borderBottomWidth) || 0);
      H = Math.max(minH, Math.min(H, Math.floor(inner - (kr.top + gt.scrollTop))));
    }
    setVar(k, '--sol-h', px(H));
    st.H = H; st.W = W;
    const th = st.theme;
    if (mode === 'pc') {
      const u = clamp(Math.min(W / 1600, H / 860), 0.7, 1.15);
      st.u = u;
      setVar(k, '--sol-u', u.toFixed(3));
      k.classList.toggle('sol-tight', u < 0.9);
      k.classList.toggle('sol-short', H < 580);
      if (th === 'parchment') setVar(k, '--sol-lw', px(332 * u));
      if (th === 'petryk') setVar(k, '--sol-rw', px(346 * u));
      if (th === 'board') fitBoard(st, k, W, H, u);
    } else {
      st.u = 1;
      setVar(k, '--sol-u', '1');
      k.classList.remove('sol-tight', 'sol-short');
      if (mode === 'ls') setVar(k, '--sol-rw', px(clamp(W * 0.36, 250, 320)));
      if (mode === 'ph') {
        // Мапа важливіша за карти: ринок і рука — тонкі рядки, а поле ≈ W · 1,12. Лише на високому екрані решта йде картам.
        const spare = H - 44 - 48 - 16 - W * 1.12;
        setVar(k, '--sol-mh', px(clamp(spare * 0.56, 44, 76)));
        setVar(k, '--sol-hh', px(clamp(spare * 0.44, 30, 60)));
      } else {
        const spare = H - 40 - 52 - 20;
        setVar(k, '--sol-mh', px(clamp(spare * 0.52, 42, 96)));
        setVar(k, '--sol-hh', px(clamp(spare * 0.48, 40, 90)));
      }
    }
    if (th === 'board') {
      const sig = Math.round(kr.width) + 'x' + Math.round(H);
      if (st.tableSig !== sig) { st.tableSig = sig; setHtml(st.el.bg, M.A.walnut(kr.width, H)); }
    }
    if (st.view) renderRail(st, st.view);
    if (was !== mode && st.view) paint(root, st.ctx);
    if (st.sc) { syncFrame(st); st.sc.resize(); }
  }

  /// Шапка столу компактна й під час гри, і над підсумком (чипи місць потрібні лише в лобі, §12).
  function headCompact(ctx) {
    const r = ctx && ctx.room;
    return !!r && (r.status === 'playing' || r.status === 'finished');
  }

  /// Що лежить поверх поля постійно: кадр мапи це обходить (§11.2). Перехідне (тости, панель шляху, порада Глека) — ні, кадр від нього не смикається.
  function hudRects(st) {
    const f = st.el.field.getBoundingClientRect(), out = [];
    const add = (el, minW, minH) => {
      if (!el || el.hidden) return;
      const b = el.getBoundingClientRect();
      if (!b.width || !b.height) return;
      const x0 = Math.max(0, b.left - f.left), y0 = Math.max(0, b.top - f.top);
      const x1 = Math.min(f.width, Math.max(b.right, b.left + (minW || 0)) - f.left), y1 = Math.min(f.height, Math.max(b.bottom, b.top + (minH || 0)) - f.top);
      if (x1 > x0 && y1 > y0) out.push({ x0, y0, x1, y1 });
    };
    // Телефон: кадр вони не рухають (там він — ділянка замовлень), лише назви не лягають під кнопки й міні-мапу.
    if (st.mode !== 'pc') {
      add(st.el.tools); add(st.el.zoom);
      if (st.mode === 'ph') out.push({ x0: 0, y0: Math.max(0, f.height - 78), x1: 106, y1: f.height });
      return out;
    }
    add(st.el.title); add(st.el.tools); add(st.el.zoom); add(st.el.hand);
    // У Петриківці «що зараз» лежить на мапі, а текст міняється щоходу: місце під нього тримаємо стале.
    if (st.theme === 'petryk') add(st.el.banner, 330 * st.u, 62 * st.u);
    if (st.theme === 'board') for (const el of st.el.deco.children) add(el);
    return out;
  }
  function syncFrame(st) {
    const sc = st.sc;
    if (!sc) return;
    if (sc.setCrop) sc.setCrop(st.mode === 'ph', st.mode !== 'pc');
    if (sc.setHud) sc.setHud(hudRects(st));
  }
  /// Відкриті замовлення — навколо них телефон відкриває мапу (§11.3).
  const homeOf = (v) => ((v && v.me && v.me.tickets) || []).filter((t) => !t.done).map((t) => { const x = M.C.ticket(t.id); return { a: x.a, b: x.b }; });

  /// «Настілка»: дошка з рахівницею тримає пропорцію мапи; що лишилось — колонці паперових карток.
  function fitBoard(st, k, W, H, u) {
    const t = Math.round(31 * u), inner = 3, gap = 12;
    const bh0 = H - 8;
    const mapW0 = (bh0 - 2 * t - 2 * inner) * 4020 / 2744;
    let bw = mapW0 + 2 * t + 2 * inner, bh = bh0;
    const rwMin = 356 * u;
    if (W - bw - gap * 2 < rwMin) {
      bw = W - rwMin - gap * 2;
      bh = (bw - 2 * t - 2 * inner) * 2744 / 4020 + 2 * t + 2 * inner;
    }
    setVar(k, '--sol-bw', px(bw));
    setVar(k, '--sol-bh', px(bh));
    setVar(k, '--sol-tk', px(t));
    setVar(k, '--sol-rw', px(clamp(W - bw - gap * 2, rwMin, 520 * u)));
    st.boardBox = { w: Math.round(bw), h: Math.round(bh), t };
  }

  function renderRail(st, v) {
    const el = st.el.railh;
    if (st.theme !== 'board' || st.mode !== 'pc' || !st.boardBox) { if (el._h) setHtml(el, ''); st.railSig = null; return; }
    const b = st.boardBox;
    const scores = (v.players || []).map((p) => ({ seat: p.seat, color: seatColor(st, p.color), shape: p.color, pts: p.points || 0, nick: cleanNick(p.nick) }));
    const sig = b.w + 'x' + b.h + '|' + scores.map((s) => s.seat + ':' + s.pts).join(',');
    if (sig === st.railSig) return;
    st.railSig = sig;
    setHtml(el, M.A.railSvg(b.w, b.h, b.t, scores));
  }

  // ─── Поле: 3D або пласке ────────────────────────────────────────────────
  async function ensureScene(root, st) {
    if (st.sc || st.scLoading) return;
    st.scLoading = true;
    const gen = st.scGen;
    const host = st.el.field;
    st.el.load.hidden = false;
    const opts = {
      onPick: (k, id) => onPick(root, k, id),
      onHover: (k, id) => onHover(root, k, id),
      quality: coarse() || (navigator.hardwareConcurrency || 8) <= 4 ? 'low' : 'high',
      reducedMotion: reduced(),
      theme: st.theme,
      crop: st.mode === 'ph',
      dense: st.mode !== 'pc',
      hud: hudRects(st),
      home: homeOf(st.view),
    };
    let sc = null, kind = null;
    if (store.get('sol.view') !== '2d') {
      try {
        const m = await import('/games/solyanyi/scene.js');
        sc = await m.createScene(host, opts);
        kind = '3d';
      } catch (e) {
        console.warn('Соляний шлях: 3D не вийшло — малюю пласку мапу', e);
        host.textContent = '';
        st.no3d = true;
      }
    }
    if (!sc) {
      try {
        const m = await import('/games/solyanyi/board2d.js');
        sc = await m.createBoard2d(host, opts);
        kind = '2d';
      } catch (e) {
        st.scLoading = false;
        st.el.load.hidden = true;
        host.innerHTML = '<p class="sol-err">Не вдалося намалювати поле. Онови сторінку.</p>';
        throw e;
      }
    }
    st.scLoading = false;
    if (root._sl !== st || gen !== st.scGen) {
      try { sc.dispose(); } catch (e) { /* уже прибрано */ }
      host.textContent = '';
      if (root._sl === st) ensureScene(root, st);   // поки вантажили — змінився вид
      return;
    }
    st.sc = sc; st.scKind = kind;
    st.el.load.hidden = true;
    st.el.root.classList.toggle('sol-sc2d', kind === '2d');
    st.el.root.classList.toggle('sol-sc3d', kind === '3d');
    resetSigs(st);
    renderTools(st);
    if (st.ctx) paint(root, st.ctx);
  }
  function resetSigs(st) { st.homeSig = null; st.boardSig = null; st.stateSig = null; st.legalSig = null; st.marksSig = null; st.tkSig = null; st.selSig = undefined; st.curSig = undefined; st.pathSig = undefined; }
  function resetScene(root, st) {
    st.scGen++;
    if (st.sc) { try { st.sc.dispose(); } catch (e) { /* уже прибрано */ } }
    st.sc = null; st.el.field.textContent = ''; st.el.field.style.background = '';
    ensureScene(root, st).catch((e) => console.error(e));
  }
  function switchView(root, st, to) {
    to = to || (st.scKind === '3d' ? '2d' : '3d');
    if (to === st.scKind && st.sc) return;
    store.set('sol.view', to);
    st.no3d = false;
    resetScene(root, st);
    renderTools(st);
  }
  /// Вибір теми: сцена міняє вигляд сама (setTheme), обраний шлях, кільця й позначки лишаються. Не вийшло — нова сцена.
  function switchTheme(root, st, t) {
    if (!M.T.THEMES.some((x) => x.id === t) || t === st.theme) return;
    st.theme = t;
    store.set('sol.theme', t);
    for (const k of PARTS) if (st.el[k]) st.el[k]._h = null;
    paintTheme(st);
    fitLayout(root, st);
    resetSigs(st);
    const sc = st.sc;
    if (sc && sc.setTheme) {
      // Будова асинхронна: не вийшло зараз чи згодом — нова сцена, якщо це все ще та сама сцена й та сама тема.
      const fail = (e) => {
        console.warn('Соляний шлях: setTheme', e);
        if (root._sl === st && st.sc === sc && st.theme === t) { resetSigs(st); resetScene(root, st); }
      };
      try { Promise.resolve(sc.setTheme(t)).catch(fail); } catch (e) { fail(e); }
    } else resetScene(root, st);   // сцена ще будується в старій темі чи не вміє міняти її: нова (старе покоління прибере ensureScene)
    paint(root, st.ctx);
  }

  const busy = (st) => st.running || st.queue.length > 0;

  /// Все, що картка каже сцені: без дій — лише малюнок. Поки йдуть анімації — сцена сама в стані «після події» (§10).
  function syncScene(root, st) {
    const v = st.view, sc = st.sc;
    if (!sc) return;
    const K = M.K;
    const board = (v && v.board) || { sig: M.C.MAP_SIG, doubles: 'one' };
    const bs = board.sig + '|' + board.doubles;
    if (bs !== st.boardSig) { st.boardSig = bs; sc.setBoard(board); }
    if (!v || busy(st)) return;
    const hs = v.me ? (v.me.tickets || []).map((t) => t.id).join() : '';
    if (hs !== st.homeSig) { st.homeSig = hs; if (sc.setHome) sc.setHome(homeOf(v)); }
    const ss = v.seq + '|' + v.turn + '|' + v.phase + '|' + (v.claimed || []).length;
    if (ss !== st.stateSig) { st.stateSig = ss; sc.setState(v); }
    const legal = v.me && v.me.must === 'act' ? { routes: K.legalRoutes(v), cities: [] } : null;
    const ls = JSON.stringify(legal);
    if (ls !== st.legalSig) { st.legalSig = ls; sc.setLegal(legal); }
    const mk = K.marks(v), ms = JSON.stringify(mk);
    if (ms !== st.marksSig) { st.marksSig = ms; sc.setMarks(mk); }
    const rings = K.ticketRings(v);
    if (st.keep && v.me) for (const id of st.keep) { const x = M.C.ticket(id); rings.push({ a: x.a, b: x.b, done: false }); }
    const ts = JSON.stringify(rings);
    if (ts !== st.tkSig) { st.tkSig = ts; sc.setTickets(rings.map((r) => ({ a: r.a, b: r.b, done: r.done }))); }
    if (st.sel !== st.selSig) { st.selSig = st.sel; sc.setSelected(st.sel); }
    const curS = st.cur == null ? null : st.cur;
    if (curS !== st.curSig) { st.curSig = curS; if (curS == null) sc.setCursor(null); else sc.setCursor('route', curS); }
    if (st.fin && st.fin.path !== undefined) return;
    const t = st.pathT != null && v.me ? (v.me.tickets || []).find((x) => x.id === st.pathT) : null;
    const path = t ? pathOfTicket(v, t) : null;
    const ps = path ? path.join(',') : null;
    if (ps !== st.pathSig) { st.pathSig = ps; sc.setPath(path); }
  }
  /// Шлях обраного замовлення: ще не зайняті (path) + свої, що вже на ньому.
  function pathOfTicket(v, t) {
    if (t.done || !(t.path && t.path.length)) {
      const x = M.C.ticket(t.id), p = playerOf(v, v.me.seat);
      return netPath(p ? p.routes : [], x.a, x.b);
    }
    return t.path;
  }
  /// Шлях між містами лише своїми шляхами (для показу виконаного й підсумку).
  function netPath(routes, a, b) {
    const R = (routes || []).map((id) => M.C.route(id));
    const prev = new Map([[a, null]]), q = [a];
    while (q.length) {
      const u = q.shift();
      if (u === b) break;
      for (const r of R) {
        const w = r.a === u ? r.b : r.b === u ? r.a : null;
        if (w !== null && !prev.has(w)) { prev.set(w, [u, r.id]); q.push(w); }
      }
    }
    if (!prev.has(b)) return null;
    const out = [];
    for (let x = b; prev.get(x); x = prev.get(x)[0]) out.push(prev.get(x)[1]);
    return out;
  }

  // ─── Вибір шляху, оплата, дії ───────────────────────────────────────────
  function selectRoute(root, st, id, how) {
    if (id == null) { clearSel(st); paint(root, st.ctx); return; }
    st.sel = id; st.pick = null; st.cur = null;
    if (how !== 'map' && st.sc) { try { st.sc.focus('route', id); } catch (e) { /* ок */ } }
    if (st.sheet === 'picker') st.sheet = null;
    st.el.which.hidden = true;
    paint(root, st.ctx);
  }
  function clearSel(st) { st.sel = null; st.pick = null; st.cur = null; if (st.el) { st.el.which.hidden = true; st.el.hov.hidden = true; } }
  /// Взяти карту. Подвійний дотик не має забрати те, що приїхало на місце взятої: дія в дорозі — наступної не шлемо, доки не прийде
  /// новий seq (чи відмова), той самий слот одразу після взяття теж ігноруємо, а сервер звіряє карту, яку людина бачила (§6).
  function doDraw(root, st, p) {
    const v = st.view, me = v && v.me;
    if (!me || !st.ctx) return false;
    if (st.drawLock != null && v.seq <= st.drawLock) return true;
    if (p.deck && !(me.can && me.can.deck)) return false;
    if (p.slot != null && !(me.can && (me.can.market || []).includes(p.slot))) return false;
    const now = Date.now();
    if (p.slot != null && st.drawSlot === p.slot && now - st.drawAt < DOUBLE_TAP_MS) return true;
    const card = p.slot != null ? (v.market || [])[p.slot] : null;
    const seq = v.seq;
    st.drawLock = seq; st.drawSlot = p.slot != null ? p.slot : null; st.drawAt = now;
    const free = () => { if (st.drawLock === seq) st.drawLock = null; };
    setTimeout(free, DRAW_LOCK_MS);   // відповідь загубилась разом зі зв'язком — не лишаємо ринок мертвим
    Promise.resolve(st.ctx.act('draw', card ? { slot: p.slot, card } : p)).then((r) => { if (!r || !r.ok) free(); }, free);
    return true;
  }
  function doClaim(root, st) {
    const v = st.view, P = v && st.sel != null ? M.K.routePanel(v, st.sel, st.pick) : null;
    if (!P || !P.canClaim || !P.combo || !st.ctx) return false;
    const id = st.sel;
    st.ctx.act('claim', { route: id, c: P.combo.c, w: P.combo.w });
    clearSel(st);
    if (st.pathT != null) { const t = (v.me.tickets || []).find((x) => x.id === st.pathT); if (t && !(t.path || []).includes(id)) st.pathT = null; }
    paint(root, st.ctx);
    return true;
  }
  function doTickets(root, st) {
    const me = st.view && st.view.me;
    if (!me || !me.can || !me.can.tickets || !st.ctx) return false;
    st.ctx.act('tickets', {});
    return true;
  }

  // ─── 💡 Глек радить ─────────────────────────────────────────────────────
  /// Новачкам (перші 2 партії) порада показана сама; далі — як вирішив гравець.
  function tipOn(st) {
    const s = store.get('sol.tip');
    if (s != null) return s === '1';
    return (+store.get('sol.games') || 0) < 2;
  }
  const tipOf = (st, v) => (v && v.me && v.me.must && v.me.tip && (st.tipNow || tipOn(st)) ? v.me.tip : null);
  function applyTip(root, st) {
    const v = st.view, me = v && v.me, tip = me && me.tip;
    if (!tip) return;
    st.tipNow = true;
    if (tip.act === 'claim') {
      st.sel = tip.route;
      const c = me.claims[tip.route];
      const i = c && c.combos ? c.combos.findIndex((x) => x.c === tip.c && x.w === (tip.w || 0)) : -1;
      st.pick = i >= 0 ? i : null;
      if (st.sc) { try { st.sc.focus('route', tip.route); } catch (e) { /* ок */ } }
    } else if (tip.act === 'keep' && st.keep) {
      st.keep = new Set(tip.ids || []);
    }
    paint(root, st.ctx);
  }

  // ─── Сцена повідомляє ───────────────────────────────────────────────────
  function onPick(root, kind, id) {
    const st = root._sl;
    if (!st || !st.view) return;
    if (st.fin && st.fin.stage < 9) return;
    if (kind === 'route') {
      // Під пальцем кілька шляхів — міні-список «Який шлях?» на місці (§11.3).
      if (st.sc && st.sc.routeAt && st.lastPt && Date.now() - st.lastPt.t < 1500 && st.mode !== 'pc') {
        const host = st.el.field.getBoundingClientRect();
        const list = (st.sc.routeAt(st.lastPt.x - host.left, st.lastPt.y - host.top) || []).slice(0, 5);
        if (list.length > 1) { showWhich(st, list, st.lastPt.x - host.left, st.lastPt.y - host.top); return; }
      }
      selectRoute(root, st, id, 'map');
    } else if (kind === 'city') {
      // Місто — усі шляхи з нього: той самий список 🧭.
      st.pickQ = M.C.city(id).name; st.sheet = 'picker'; paint(root, st.ctx);
      const r = st.el.sheet.querySelector('.sol-pick-from');
      if (r) r.textContent = 'Звідси: ' + M.C.city(id).name;
    } else { clearSel(st); paint(root, st.ctx); }
  }
  function showWhich(st, list, x, y) {
    const K = M.K, v = st.view;
    const rows = list.map((id) => {
      const r = K.pickerRow(v, id);
      return '<button type="button" class="sol-pk" data-a="pick" data-id="' + id + '">' + M.A.tile(st.theme, r.cargo) + '<span><b>' + esc(r.name) + '</b><small>' + r.len + ' 🛞 · +' + r.pts + ' ⭐</small></span></button>';
    }).join('');
    setHtml(st.el.which, '<b class="sol-whichh">Який шлях?</b>' + rows);
    const f = st.el.fieldwrap.getBoundingClientRect();
    st.el.which.hidden = false;
    st.el.which.style.left = px(clamp(x - 110, 4, Math.max(4, f.width - 228)));
    st.el.which.style.top = px(clamp(y - 30, 4, Math.max(4, f.height - st.el.which.offsetHeight - 4)));
  }
  function onHover(root, kind, id) {
    const st = root._sl;
    if (!st || !st.view || coarse() || st.mode !== 'pc') return;
    const h = st.el.hov;
    if (kind !== 'route' || id == null) { h.hidden = true; return; }
    const r = M.C.route(id), c = st.view.me && st.view.me.claims && st.view.me.claims[id];
    const own = (st.view.claimed || []).find((x) => x.route === id);
    h.innerHTML = '<b>' + esc(M.C.routeName(id)) + '</b> ' + M.C.ICON_CARGO[r.cargo] + ' ' + r.len + ' 🛞 · +' + r.pts + ' ⭐'
      + (own ? '<small>' + esc(cleanNick(nickOf(st.view, own.seat))) + '</small>' : c && !c.ok ? '<small>' + esc(c.why) + '</small>' : '');
    h.hidden = false;
    const p = st.sc && st.sc.screenPos ? st.sc.screenPos('route', id) : null;
    if (p) { h.style.left = px(p.x); h.style.top = px(p.y); }
  }

  // ─── Малюємо вид ────────────────────────────────────────────────────────
  function paint(root, ctx) {
    const st = state(root);
    st.ctx = ctx;
    st.view = ctx && ctx.view && ctx.view.board ? ctx.view : null;
    const gt0 = root.closest('.gtable');
    if (gt0 && gt0.classList.contains('sol-gt')) gt0.classList.toggle('sol-gt-play', headCompact(ctx));
    if (!M) {
      if (!root.firstChild) root.innerHTML = '<p class="sol-wait">Запрягаємо волів…</p>';
      load().then(() => { if (root._sl === st) paint(root, st.ctx); }, (e) => { root.innerHTML = '<p class="sol-err">Не вдалося завантажити гру. Онови сторінку.</p>'; console.error(e); });
      return;
    }
    if (!st.theme) st.theme = themeNow();
    if (!st.built) {
      build(root, st);
      fitLayout(root, st);
    }
    const v = st.view;
    if (v && v.board && v.board.sig !== M.C.MAP_SIG && !st.sigWarn) { st.sigWarn = true; console.warn('Соляний шлях: мапа сервера не збігається з клієнтом', v.board.sig, M.C.MAP_SIG); }
    if (v && M.C.mapSig && !st.sigChk) { st.sigChk = true; if (M.C.mapSig() !== M.C.MAP_SIG) console.warn('Соляний шлях: consts.js розійшовся з MAP_SIG'); }
    const me = v ? v.me || null : null;
    // Живий кінець: подія over ще в дорозі — підсумок чекає її, а не показує все відразу (§12). Зайшли в уже скінчену — stage 9 (renderSum).
    if (v && v.over && !st.fin && st.seq != null && (v.events || []).some((e) => e.k === 'over' && e.seq > st.seq)) st.fin = { stage: 0, shown: {}, pending: true };
    if (st.turnKey !== (v ? v.turn + '|' + v.turns + '|' + v.phase : '')) { st.turnKey = v ? v.turn + '|' + v.turns + '|' + v.phase : ''; st.tipNow = false; }
    if (st.cur != null && (!me || me.must !== 'act')) st.cur = null;
    syncKeep(st, v, me);
    st.el.root.classList.toggle('sol-spect', !me);
    st.el.root.classList.toggle('sol-lobby', !v || !v.phase);
    st.el.root.classList.toggle('sol-mine', !!(me && me.must));
    renderTools(st);
    renderBanner(st, v, me);
    if (v) {
      renderPlayers(st, v, me);
      renderBar(st, v, me);
      renderMarket(st, v, me);
      renderHand(st, v, me);
      renderTickets(st, v, me);
      renderRoute(st, v, me);
      renderLog(st, v);
      renderGlek(st, v, me);
      renderKeep(st, v, me);
      renderSheet(st, v, me);
      renderSum(root, st, v, me);
      renderRail(st, v);
      queueEvents(root, st, v);
    }
    renderPop(st);
    if (!st.sc) ensureScene(root, st).catch((e) => console.error(e));
    else syncScene(root, st);
    if (v) { nudge(root, st); attention(st, v, me); }
    refit(root, st);
  }

  // ─── Смуга «що зараз» ───────────────────────────────────────────────────
  function timerHtml(st, v) {
    if (!v || !v.deadline || v.over) return '';
    if (st.theme === 'petryk') return '<span class="sol-timer sol-trose">' + M.A.timerRose() + '<b data-sec></b></span>';
    return '<span class="sol-timer">' + M.A.hourglass() + '<b data-sec></b></span>';
  }
  function renderBanner(st, v, me) {
    const w = M.K.whatNow(v, null);
    const K = M.K, ctx = st.ctx;
    const p = me && v && playerOf(v, me.seat);
    let sub = w.s, tt = w.t;
    // Поки рахуємо, смуга не вибовкує переможця.
    if (v && v.over && st.fin && st.fin.stage < 3) { tt = '🧂 Рахуємо сіль…'; sub = ''; w.short = tt; }
    if (p && p.away) sub = '🤖 За тебе зараз їде Глек — зроби будь-що сам, і поведеш валку далі';
    if (!me && ctx && ctx.seat == null && ctx.playing && ownAway(v, ctx.me && ctx.me.nick)) sub = '🛞 Твоя валка на місці — Глек вів її замість тебе';
    const disc = st.theme === 'board' && v && v.turn != null ? '<span class="sol-tdisc" style="background:' + seatColor(st, (playerOf(v, v.turn) || {}).color) + '">' + esc(cleanNick(nickOf(v, v.turn)).charAt(0).toUpperCase()) + '</span>' : '';
    const k2 = w.kicker ? '<small>' + esc(w.kicker) + '</small>' : '';
    const burn = v && v.deadline && !v.over ? '<i class="sol-burn"><i></i></i>' : '';
    const tmr = timerHtml(st, v);
    const html = st.mode === 'pc'
      ? (st.theme === 'petryk'
        ? tmr + '<span class="sol-btx"><h2>' + esc(tt) + '</h2><p>' + esc(sub) + '</p></span>'
        : disc + '<span class="sol-btx">' + k2 + '<h2>' + esc(tt) + '</h2><p>' + esc(sub) + '</p></span>' + tmr + burn)
      : '<span class="sol-btx"><b class="sol-bs">' + esc(w.short) + '</b></span>';
    setHtml(st.el.banner, html);
    st.el.banner.classList.toggle('mine', !!w.mine);
    st.el.banner.title = sub || '';
    st.bannerSub = sub;
  }

  // ─── Гравці ─────────────────────────────────────────────────────────────
  function renderPlayers(st, v, me) {
    const A = M.A, th = st.theme, pc = st.mode === 'pc';
    const lead = new Set((v.longest && v.longest.seats) || []);
    const tt = (s) => ' data-a="hint" data-hint="' + esc(s) + '" title="' + esc(s) + '"';
    const stat = (cls, ic, n, s) => '<span class="sol-st ' + cls + '"' + tt(s) + '>' + ic + '<b>' + n + '</b></span>';
    const rows = (v.players || []).map((p) => {
      const turn = v.turn === p.seat, mine = me && me.seat === p.seat;
      const av = A.avatarHtml(th, p, seatColor(st, p.color));
      const low = p.wagons <= 5 ? ' low' : '';
      const nick = esc(cleanNick(p.nick)) + (p.bot ? ' <span class="sol-botm" title="Глек">🤖</span>' : p.away ? ' <span class="sol-botm" title="За нього їде Глек">💤</span>' : '');
      const lg = lead.has(p.seat) && v.longest.len > 0 ? '<small class="sol-lg" title="Найдовша валка: ' + v.longest.len + '">🐂 ' + v.longest.len + '</small>' : '';
      const marks = (mine ? '<small class="sol-ti">ти</small>' : '') + (th === 'board' ? '' : lg) + (v.phase === 'setup' && p.ready ? '<small class="sol-rd" title="Уже обрав замовлення">✔</small>' : '');
      const pts = '<span class="sol-pts"' + tt('⭐ лише за шляхи; замовлення таємні до кінця') + '><b>' + p.points + '</b>' + (th === 'board' && pc ? '<i>очок</i>' : '') + '</span>';
      const wag = stat('w' + low, A.IC.cart, p.wagons, 'Возів лишилось: ' + p.wagons + (p.wagons <= 5 ? '. Мало!' : ''));
      const crd = stat('c', A.IC.cards, p.hand, 'Карт у руці: ' + p.hand);
      const tk = stat('t', A.IC.scroll, p.tickets, 'Замовлень: ' + p.tickets + ' (таємні)');
      const cls = 'sol-pl' + (turn ? ' turn' : '') + (mine ? ' me' : '') + (lead.has(p.seat) ? ' lead' : '') + (p.away ? ' away' : '');
      if (!pc) {
        return '<div class="' + cls + '" data-seat="' + p.seat + '" style="--pc:' + seatColor(st, p.color) + '">' + av + '<span class="sol-ptxt"><b class="sol-pn">' + p.points + '</b><span class="sol-pw' + low + '">' + A.IC.cart + p.wagons + '</span></span>' + (v.phase === 'setup' && p.ready ? '<small class="sol-rd">✔</small>' : '') + '</div>';
      }
      if (th === 'parchment') return '<div class="' + cls + '" data-seat="' + p.seat + '">' + av + '<div class="sol-pnm">' + nick + marks + '</div>' + pts + wag + crd + tk + '</div>';
      if (th === 'board') return '<div class="' + cls + '" data-seat="' + p.seat + '" style="--pc:' + seatColor(st, p.color) + '"><i class="sol-band"></i>' + av + '<div class="sol-pnm">' + nick + marks + '</div>' + pts + '<div class="sol-sts">' + wag + crd + tk + lg + '</div></div>';
      return '<div class="' + cls + '" data-seat="' + p.seat + '">' + av + '<div class="sol-who"><div class="sol-pnm">' + nick + marks + '</div><div class="sol-sts">' + wag + crd + tk + '</div></div>' + pts + '</div>';
    }).join('');
    let head = '';
    if (pc && th === 'parchment') head = '<div class="sol-thead"><span class="sol-h3">Чумаки</span><span>очки</span><span title="Вози">' + A.IC.cart + '</span><span title="Карти">' + A.IC.cards + '</span><span title="Замовлення">' + A.IC.scroll + '</span></div>';
    if (pc && th === 'petryk') head = '<div class="sol-h3 sol-lgd">Гравці<span>' + A.IC.cart + 'вози ' + A.IC.cards + 'карти ' + A.IC.scroll + 'замовл.</span></div>';
    const timer = !pc ? timerHtml(st, v) : '';
    setHtml(st.el.players, head + '<div class="sol-pls">' + rows + '</div>' + timer + (pc && th !== 'parchment' ? '' : ''));
    tickArcs(st);
  }

  // ─── Дії ────────────────────────────────────────────────────────────────
  function renderBar(st, v, me) {
    const A = M.A, tip = me && me.must && me.tip;
    const can = (me && me.can) || {};
    const pc = st.mode === 'pc';
    const open = (me && me.tickets) || [];
    const seals = open.slice(0, 4).map((t) => { const x = M.C.ticket(t.id); return '<i class="sol-sd' + (t.done ? ' d' : t.dead || t.need == null ? ' x' : '') + '">' + x.pts + '</i>'; }).join('');
    let h = '';
    if (!pc && me) h += '<button type="button" class="sol-bb sol-bbt" data-a="sheet" data-s="tickets"><span class="sol-sds">' + seals + '</span><span>Замовлення</span></button>';
    h += '<button type="button" class="sol-bb" data-a="sheet" data-s="picker" title="Список шляхів без мапи">🧭<span class="sol-bl">' + (pc ? ' Вибрати шлях' : ' Шлях') + '</span></button>';
    if (tip) h += '<button type="button" class="sol-bb sol-bbg' + (st.tipNow || tipOn(st) ? ' on' : '') + '" data-a="tip" title="Глек радить">💡<span class="sol-bl">' + (pc ? ' Глек радить' : '') + '</span></button>';
    if (can.pass) h += '<button type="button" class="sol-bb sol-primary" data-a="pass">Пропустити</button>';
    if (!pc) h += '<button type="button" class="sol-bb sol-bbj" data-a="sheet" data-s="log" title="Журнал і правила" aria-label="Журнал і правила">📖<span class="sol-bl"> Журнал</span></button>';
    if (me && me.why && me.must === 'act' && me.why.claim && !pc) h += '';
    setHtml(st.el.bar, h);
  }

  // ─── Ринок ──────────────────────────────────────────────────────────────
  function renderMarket(st, v, me) {
    const A = M.A, C = M.C, th = st.theme, compact = st.mode !== 'pc' && th !== 'board';
    const can = (me && me.can) || { market: [], deck: false };
    const tip = tipOf(st, v);
    const P = st.sel != null ? M.K.routePanel(v, st.sel, st.pick) : null;
    const want = P && P.advice;
    const firstPick = !me || me.must !== 'draw';
    const cards = (v.market || []).map((k, i) => {
      if (!k) return '<div class="sol-mc empty" data-slot="' + i + '"><span>порожньо</span></div>';
      const legal = (can.market || []).includes(i);
      const isWant = (tip && tip.act === 'draw' && tip.slot === i) || (want && want.slot === i);
      const wildTag = k === C.WILD && me && (me.must === 'act' || me.must === 'draw')
        ? (me.must === 'act' ? '<span class="sol-wtag"><i>🐂 </i>Цілий хід</span>' : '') : '';
      const title = k === C.WILD && me && me.must === 'draw' ? 'Вола другою не беруть' : C.CARGO_NAME[k] + (legal ? ' — узяти' : '');
      const wantTag = isWant ? '<span class="sol-wanttag">' + (th === 'petryk' ? '+1' : 'бери!') + '</span>' : '';
      return '<button type="button" class="sol-mc k-' + k + (isWant ? ' want' : '') + (legal ? ' can' : ' off') + (k === C.WILD && !firstPick ? ' nogo' : '') + '" data-a="slot" data-slot="' + i + '"' + (legal ? '' : ' disabled') + ' title="' + esc(title) + '" aria-label="' + esc(title) + '">' + A.cardHtml(th, k, { compact }) + wildTag + wantTag + (me && me.must && !coarse() && st.mode === 'pc' ? '<kbd>' + (i + 1) + '</kbd>' : '') + '</button>';
    }).join('');
    const deckWant = (tip && tip.act === 'draw' && tip.deck) || (want && want.deck);
    const deckLabel = th === 'parchment' ? 'наосліп' : 'колода';
    const deck = '<button type="button" class="sol-mc sol-deck' + (deckWant ? ' want' : '') + (can.deck ? ' can' : ' off') + '" data-a="deck"' + (can.deck ? '' : ' disabled') + ' title="Узяти наосліп' + (v.deck ? '' : ' (колода порожня)') + '" aria-label="Узяти наосліп: ' + (v.deck || 0) + ' у колоді">' + A.backHtml(th) + '<b class="sol-dn">' + (v.deck || 0) + '</b><span class="sol-dl">' + deckLabel + '</span></button>';
    const pc = st.mode === 'pc';
    const hdr = pc ? (th === 'parchment' ? '<div class="sol-h3">Торг <em>відкриті карти і колода</em></div>'
      : th === 'board' ? '<div class="sol-hd"><span class="sol-lbl">Ринок</span><span class="sol-lbl2">дві карти або одного вола</span></div>'
        : '<div class="sol-h3">Ринок <em>візьми 2 карти, або одного вола</em></div>') : '<div class="sol-rl">' + (th === 'parchment' ? 'Торг' : 'Ринок') + '</div>';
    const note = pc && th === 'parchment' ? '<p class="sol-mnote"><b>За хід — дві карти.</b> Вола з торгу — лише одного.<small>🗑 у скиді ' + (v.discard || 0) + '</small></p>' : '';
    const disc = !(pc && th === 'parchment') ? '<span class="sol-disc" title="У скиді">🗑 ' + (v.discard || 0) + '</span>' : '';
    setHtml(st.el.market, hdr + '<div class="sol-mrow">' + cards + deck + note + '</div>' + disc);
  }

  // ─── Рука ───────────────────────────────────────────────────────────────
  function renderHand(st, v, me) {
    const A = M.A, C = M.C, th = st.theme, pc = st.mode === 'pc';
    if (!me) { setHtml(st.el.hand, ''); st.el.hand.hidden = true; return; }
    st.el.hand.hidden = false;
    const hand = me.hand || {};
    const total = M.K.handTotal(hand);
    const P = st.sel != null ? M.K.routePanel(v, st.sel, st.pick) : null;
    const use = new Set();
    if (P && P.combo) { if (P.combo.n) use.add(P.combo.c); if (P.combo.w) use.add(C.WILD); }
    else if (P && P.stack) { use.add(P.stack); if (hand.wild) use.add(C.WILD); }
    const myP = playerOf(v, me.seat) || {};
    const label = (k) => C.CARGO_NAME[k] + ': ' + (hand[k] || 0);
    if (pc && th === 'board') {
      // Віялом — лише те, що є (порожні стовпчики ринок і так показує).
      const items = C.CARDS.filter((k) => hand[k] > 0);
      const n = items.length, cw = 86, step = n > 1 ? Math.min(50, (380 - cw) / (n - 1)) : 0;
      const fan = items.map((k, i) => {
        const cnt = hand[k], c = i - (n - 1) / 2, rot = c * 2.6, up = c * c * 0.9, pay = use.has(k);
        let stk = '';
        for (let j = Math.min(cnt, 4) - 1; j > 0; j--) stk += '<i class="sol-stk" style="transform:translate(' + (j * 2.4).toFixed(1) + 'px,' + (-j * 2.6).toFixed(1) + 'px) rotate(' + (j * 1.4).toFixed(1) + 'deg)">' + A.cardHtml(th, k) + '</i>';
        return '<div class="sol-hc k-' + k + (pay ? ' use' : '') + '" data-k="' + k + '" title="' + esc(label(k)) + '" style="left:' + (i * step).toFixed(0) + 'px;top:' + (up - (pay ? 18 : 0)).toFixed(0) + 'px;transform:rotate(' + rot.toFixed(1) + 'deg);z-index:' + (i + 1) + '">' + stk + '<span class="sol-top">' + A.cardHtml(th, k) + '</span><span class="sol-cnt">' + cnt + '</span></div>';
      }).join('');
      const payTag = use.size ? '<span class="sol-paytag">піде на шлях ↓</span>' : '';
      setHtml(st.el.hand, '<div class="sol-handtag"><b>Твоя рука</b> <span>' + M.K.cards(total) + '</span>' + payTag + '</div><div class="sol-fan" style="width:' + (n ? (n - 1) * step + cw : cw) + 'px">' + fan + '</div>');
      return;
    }
    const compact = !pc;
    const cells = C.CARDS.map((k) => {
      const n = hand[k] || 0;
      const stack = pc && n > 1 ? (th === 'parchment' ? '<i class="sol-pile"></i>' : '<i class="sol-st1"></i>' + (n > 2 ? '<i class="sol-st2"></i>' : '')) : '';
      return '<div class="sol-hc k-' + k + (n ? '' : ' zero') + (use.has(k) ? ' use' : '') + '" data-k="' + k + '" title="' + esc(label(k)) + '">' + stack + '<span class="sol-top">' + A.cardHtml(th, k, { compact, slice: compact }) + '</span><span class="sol-cnt">' + n + '</span></div>';
    }).join('');
    const wag = pc && th === 'parchment' ? '<div class="sol-hc sol-wag">' + A.IC.cart + '<b>' + (myP.wagons != null ? myP.wagons : '') + '</b><small>' + (myP.wagons != null ? M.C.plural(myP.wagons, 'віз', 'вози', 'возів') : '') + ' лишилось</small></div>' : '';
    const hdr = pc ? (th === 'parchment' ? '<div class="sol-flap"><span class="sol-h3">Моя торба</span><span class="sol-tot">' + A.IC.cards + ' ' + M.K.cards(total) + '</span></div>'
      : '<div class="sol-h3 sol-hl">Моя рука<em>' + M.K.cards(total) + (use.size ? ' · золотим — чим платити' : '') + '</em></div>')
      : '<div class="sol-rl">' + (th === 'parchment' ? 'Торба' : 'Рука') + '<small>' + total + '</small></div>';
    setHtml(st.el.hand, hdr + '<div class="sol-hgrid">' + cells + wag + '</div>');
  }

  // ─── Замовлення ─────────────────────────────────────────────────────────
  function ticketsHtml(st, v, me) {
    const A = M.A, K = M.K, th = st.theme;
    const list = ((me && me.tickets) || []).map((t, i) => {
      const L = K.ticketLine(t), on = st.pathT === t.id;
      const seg = Math.max(1, Math.min(14, L.d));
      const fill = Math.round(K.ticketProgress(t) * seg);
      const pips = '<span class="sol-pips">' + Array.from({ length: seg }, (_, j) => '<i class="' + (j < fill ? 'on' : '') + '"></i>').join('') + '</span>';
      const hint = L.done ? '<span class="sol-hint ok">✅ +' + L.pts + ' · виконано</span>'
        : L.dead ? '<span class="sol-hint bad">шлях перекрито</span>'
          : '<span class="sol-hint">' + pips + '<span>ще ' + L.need + ' 🛞</span></span>';
      const cls = 'sol-tk' + (L.done ? ' done' : '') + (L.dead ? ' dead' : '') + (on ? ' on' : '');
      const dash = th === 'board' ? ' <span class="sol-dash">→</span> ' : ' <span class="sol-dash">—</span> ';
      const name = esc(L.a) + dash + esc(L.b);
      const aria = esc(L.name + ' · ' + L.pts + ' ⭐ · ' + L.left);
      if (th === 'parchment') {
        return '<button type="button" class="' + cls + '" data-a="ticket" data-id="' + t.id + '" style="--r:' + [-0.5, 0.4, -0.3, 0.3, -0.4][i % 5] + 'deg" aria-label="' + aria + '">' + A.sealSvg(L.pts, L.done ? '#3e7a44' : L.dead ? '#5a4a3e' : '#a51d16') + '<span class="sol-tkb"><span class="sol-rt">' + name + '</span>' + hint + '</span></button>';
      }
      if (th === 'board') {
        return '<button type="button" class="' + cls + '" data-a="ticket" data-id="' + t.id + '" aria-label="' + aria + '"><span class="sol-seal">' + (L.done ? '✓' : L.pts) + '</span><span class="sol-tkb"><span class="sol-rt">' + name + (L.done ? ' <em>+' + L.pts + '</em>' : '') + '</span>' + hint + '</span></button>';
      }
      const via = !L.done && !L.dead && t.path && t.path.length ? '<small class="sol-via">лишилось ' + L.need + ' 🛞: ' + esc(K.pathCities(t.path)) + '</small>' : '';
      return '<button type="button" class="' + cls + '" data-a="ticket" data-id="' + t.id + '" aria-label="' + aria + '"><span class="sol-seal">' + (L.done ? '✓' : 'АБВГҐДЕЄЖЗ'[i % 10]) + '</span><b class="sol-rt">' + esc(L.a) + ' — ' + esc(L.b) + '</b><span class="sol-tpts">' + (L.done ? '+' : '') + L.pts + '</span>'
        + (L.done ? '' : L.dead ? '<span class="sol-hint bad">шлях перекрито</span>' : '<span class="sol-prog">' + Array.from({ length: seg }, (_, j) => '<i class="' + (j < fill ? 'on' : '') + '"></i>').join('') + '</span>' + via) + '</button>';
    }).join('');
    return list;
  }
  function renderTickets(st, v, me) {
    if (!me) { setHtml(st.el.tickets, ''); st.el.tickets.hidden = true; return; }
    st.el.tickets.hidden = false;
    const K = M.K, th = st.theme, can = (me.can) || {};
    const tipT = tipOf(st, v);
    const newBtn = '<button type="button" class="sol-newt' + (tipT && tipT.act === 'tickets' ? ' want' : '') + '" data-a="tickets"' + (can.tickets ? '' : ' disabled') + ' title="' + esc((me.why && me.why.tickets) || 'Взяти 3 нові замовлення (T)') + '">+ нове' + (th === 'petryk' ? ' замовлення' : '') + '</button>';
    const done = (me.tickets || []).filter((t) => t.done).length;
    const hdr = th === 'board'
      ? '<div class="sol-hd"><span class="sol-lbl">Мої замовлення</span><span class="sol-lbl2">виконано ' + done + ' з ' + (me.tickets || []).length + '</span>' + newBtn + '</div>'
      : th === 'parchment'
        ? '<div class="sol-h3">Мої замовлення <em>печатка — це очки</em>' + newBtn + '</div>'
        : '<div class="sol-h3 sol-lgd">Мої замовлення' + newBtn + '</div>';
    setHtml(st.el.tickets, hdr + '<div class="sol-tklist">' + ticketsHtml(st, v, me) + '</div><div class="sol-tsum" title="Рахунок замовлень таємний до кінця">' + esc(K.scoreLine(me.score)) + '</div>');
  }

  // ─── Шлях і оплата ──────────────────────────────────────────────────────
  function routeHtml(st, v, me, P) {
    const A = M.A, th = st.theme, C = M.C;
    const pips = P.pips.map((p) => A.tile(th, p.k, !p.have)).join('');
    const head = '<div class="sol-rh"><h3>' + A.tile(th, P.cargo) + esc(P.name) + '</h3><span class="sol-rm">' + P.len + ' 🛞 · <b>+' + P.pts + '</b> ⭐</span><button type="button" class="sol-x" data-a="cancel" aria-label="Закрити" title="Закрити (Esc)">✕</button></div>';
    const twin = P.twinText ? '<div class="sol-twin">' + esc(P.twinText) + '</div>' : '';
    let body = '';
    if (P.owner != null) {
      const p = playerOf(v, P.owner) || {};
      body = '<div class="sol-rw"><span class="sol-ownr" style="color:' + seatColor(st, p.color) + '">' + C.SEAT_SHAPE[p.color] + '</span><span class="sol-why">' + esc(P.why) + '</span></div>';
    } else if (P.ok) {
      const multi = P.combos.length > 1;
      const combos = multi ? '<div class="sol-combos" role="group" aria-label="Чим платити">' + P.combos.map((c, i) => '<button type="button" class="sol-combo' + (i === P.pick ? ' on' : '') + '" data-a="combo" data-i="' + i + '" aria-pressed="' + (i === P.pick) + '" aria-label="' + esc(M.K.comboText(c)) + '">' + A.comboHtml(th, c) + '</button>').join('') + '</div>' : '';
      const noteMine = P.canClaim ? 'Є ' + P.len + ' із ' + P.len + ': усе є' : me && me.must ? 'Спершу заверши дію' : 'Поза своїм ходом — лише перегляд';
      body = '<div class="sol-rw">' + pips + '<span class="sol-note ok">' + esc(noteMine) + '</span></div>' + combos
        + (P.canClaim ? '<div class="sol-btns"><button type="button" class="sol-btn sol-primary sol-claim" data-a="claim">🚩 Зайняти</button></div>' : '');
    } else if (P.short > 0) {
      const adv = P.advice;
      const note = '<span class="sol-note"><b>' + esc(P.why.charAt(0).toUpperCase() + P.why.slice(1)) + '</b>є ' + P.have + ' із ' + P.len + (P.stack ? ': ' + esc(C.CARGO_NAME[P.stack]) + (me.hand.wild ? ' і ' + C.OX[0] : '') : '') + '</span>';
      const lock = '<button type="button" class="sol-btn sol-locked" disabled>🔒 Зайняти · ' + P.have + '/' + P.len + '</button>';
      const go = adv ? '<button type="button" class="sol-btn sol-primary" data-a="advice">' + esc(adv.text) + '</button>' : '';
      body = '<div class="sol-rw">' + pips + note + '</div><div class="sol-btns">' + lock + go + '</div>';
    } else {
      body = '<div class="sol-rw">' + pips + '<span class="sol-why">' + esc(P.why) + '</span></div>';
    }
    return head + body + twin;
  }
  function renderRoute(st, v, me) {
    const el = st.el.route;
    if (st.sel == null || st.sheet) { el.hidden = true; setHtml(el, ''); return; }
    const P = M.K.routePanel(v, st.sel, st.pick);
    if (!P) { st.sel = null; el.hidden = true; return; }
    st.pick = P.pick;
    el.hidden = false;
    el.style.setProperty('--rc', M.T.themeOf(st.theme).pal.cargo[P.cargo] || '#888');
    setHtml(el, routeHtml(st, v, me, P));
  }

  // ─── Журнал і правила ───────────────────────────────────────────────────
  function logHtml(st, v, n) {
    const lines = (v.log || []).slice(-n).reverse();
    return '<ol class="sol-loglist">' + lines.map((l) => {
      const seat = M.K.logSeat(v, l), p = seat != null ? playerOf(v, seat) : null;
      const txt = esc(l).replace(/\(([^()]*\+\d+ ⭐)\)/, '(<em>$1</em>)');
      const named = p ? txt.replace(esc(p.nick), () => '<b>' + esc(cleanNick(p.nick)) + '</b>') : txt;
      const dot = p ? '<i class="sol-dot" style="background:' + seatColor(st, p.color) + '">' + M.C.SEAT_SHAPE[p.color] + '</i>' : '<i class="sol-dot"></i>';
      return '<li>' + dot + '<span>' + named + '</span></li>';
    }).join('') + '</ol>';
  }
  function tabsHtml(st, v) {
    const d = (v.board || {}).doubles;
    return '<div class="sol-tabs" role="tablist"><button type="button" class="sol-tab' + (st.tab === 'log' ? ' on' : '') + '" data-a="tab" data-t="log" role="tab" aria-selected="' + (st.tab === 'log') + '">📜 Журнал</button><button type="button" class="sol-tab' + (st.tab === 'rules' ? ' on' : '') + '" data-a="tab" data-t="rules" role="tab" aria-selected="' + (st.tab === 'rules') + '">❔ Правила</button></div>'
      + (st.tab === 'rules' ? '<div class="sol-rules">' + M.K.rulesHtml(d) + '</div>' : logHtml(st, v, 12));
  }
  function renderLog(st, v) {
    if (st.mode !== 'pc') { setHtml(st.el.log, ''); return; }
    setHtml(st.el.log, tabsHtml(st, v));
  }

  // ─── Дядько Глек ────────────────────────────────────────────────────────
  function renderGlek(st, v, me) {
    const A = M.A, ctx = st.ctx;
    const away = !me && ctx && ctx.seat == null && ctx.playing && ownAway(v, ctx.me && ctx.me.nick);
    let txt = '';
    if (away) txt = '<b>Твоя валка на місці.</b> Поки тебе не було, її вів Глек. Рука, замовлення й вози — ті самі. <button type="button" class="sol-btn sol-primary sol-back" data-a="rejoin">' + REJOIN + '</button>';
    else {
      const tip = tipOf(st, v);
      if (tip) txt = (st.theme === 'parchment' ? '<b>Дядько Глек радить:</b> ' : '💡 ') + esc(M.K.tipText(v, tip));
    }
    st.el.glek.hidden = !txt || (st.mode !== 'pc' && st.sel != null);
    setHtml(st.el.glek, txt ? '<span class="sol-glk">' + A.glekSvg() + '</span><div class="sol-say">' + txt + '</div>' : '');
  }

  // ─── Вибір замовлень ────────────────────────────────────────────────────
  function syncKeep(st, v, me) {
    const key = me && (me.must === 'setup' || me.must === 'keep') ? me.must + ':' + (me.offer || []).map((o) => o.id).join(',') : null;
    if (key !== st.keepKey) { st.keepKey = key; st.keep = key ? new Set() : null; }
  }
  function renderKeep(st, v, me) {
    const el = st.el.keep, A = M.A, K = M.K;
    if (!st.keep || !me) { el.hidden = true; setHtml(el, ''); return; }
    const land = st.geo ? st.geo.land : '';
    const n = st.keep.size;
    const rows = (me.offer || []).map((o) => {
      const x = M.C.ticket(o.id), on = st.keep.has(o.id);
      const A1 = M.C.city(x.a), B1 = M.C.city(x.b);
      return '<button type="button" class="sol-off' + (on ? ' on' : '') + '" data-a="offer" data-id="' + o.id + '" aria-pressed="' + on + '">' + A.miniMap(land, A1, B1) + '<span class="sol-offb"><b>' + esc(A1.name) + ' — ' + esc(B1.name) + '</b><small>' + (o.need == null ? 'шляху вже нема' : 'ще ' + o.need + ' 🛞') + '</small></span><span class="sol-offp">' + x.pts + ' ⭐</span><i class="sol-chk">' + (on ? '✔' : '') + '</i></button>';
    }).join('');
    let foot = '';
    if (me.must === 'setup') {
      const done = (v.players || []).filter((p) => p.ready).map((p) => cleanNick(p.nick));
      const wait2 = (v.players || []).filter((p) => !p.ready).map((p) => cleanNick(p.nick));
      foot = '<p class="sol-who2">' + (done.length ? '✔ Обрали: ' + esc(done.join(', ')) + '. ' : '') + (wait2.length ? 'Чекаємо на ' + esc(wait2.join(', ')) : '') + '</p>';
    }
    const left = M.C.plural(me.keepMin, 'одне', 'два', 'п\'ять');
    setHtml(el, '<h3>📜 Лиши щонайменше ' + me.keepMin + '</h3><p class="sol-keeph">' + (me.must === 'setup' ? 'Виконане додасть очки, невиконане — відніме. Обери хоч ' + left + '.' : 'Решту покладемо під низ колоди.') + '</p><div class="sol-offs">' + rows + '</div>' + foot
      + '<div class="sol-btns"><button type="button" class="sol-btn sol-primary" data-a="keepgo"' + (n >= me.keepMin ? '' : ' disabled') + '>Лишити ' + n + '</button>' + (me.tip ? '<button type="button" class="sol-btn" data-a="tip">💡 Глек радить</button>' : '') + '</div>');
    el.hidden = false;
  }

  // ─── Шторки: 🧭, замовлення, журнал ─────────────────────────────────────
  function renderSheet(st, v, me) {
    const el = st.el.sheet;
    if (!st.sheet) { el.hidden = true; setHtml(el, ''); st.el.root.classList.remove('sol-sheeton'); return; }
    st.el.root.classList.add('sol-sheeton');
    const close = '<button type="button" class="sol-x" data-a="sheetx" aria-label="Закрити">✕</button>';
    if (st.sheet === 'tickets') {
      setHtml(el, '<div class="sol-shh"><b>📜 Мої замовлення</b>' + close + '</div><div class="sol-tklist sol-shb">' + ticketsHtml(st, v, me) + '</div><div class="sol-tsum">' + esc(M.K.scoreLine(me && me.score)) + '</div>'
        + (me && me.can && me.can.tickets ? '<div class="sol-btns"><button type="button" class="sol-btn sol-primary" data-a="tickets">📜 Взяти нові замовлення</button></div>' : ''));
    } else if (st.sheet === 'log') {
      setHtml(el, '<div class="sol-shh"><b>📖 Журнал і правила</b>' + close + '</div><div class="sol-shb">' + tabsHtml(st, v) + '</div>');
    } else if (st.sheet === 'picker') {
      if (el.dataset.k !== 'picker' || !el.querySelector('.sol-search')) {
        el.dataset.k = 'picker';
        el._h = null;
        el.innerHTML = '<div class="sol-shh"><b class="sol-pick-from">🧭 Вибрати шлях</b>' + close + '</div><input type="search" class="sol-search" placeholder="🔍 Місто…" aria-label="Пошук за містом" value="' + esc(st.pickQ) + '"><div class="sol-pkrows sol-shb"></div>';
      }
      renderPickRows(st);
      el.hidden = false;
      return;
    }
    el.dataset.k = st.sheet;
    el.hidden = false;
  }
  function renderPickRows(st) {
    const box = st.el.sheet.querySelector('.sol-pkrows');
    if (!box) return;
    const groups = M.K.pickerGroups(st.view, st.pickQ);
    setHtml(box, groups.map((g) => '<div class="sol-pkg">' + esc(g.title) + '</div>' + g.rows.map((r) => '<button type="button" class="sol-pk' + (r.ok ? ' ok' : '') + '" data-a="pick" data-id="' + r.id + '">' + M.A.tile(st.theme, r.cargo) + '<span><b>' + esc(r.name) + '</b><small>' + r.len + ' 🛞 · +' + r.pts + ' ⭐' + (r.why ? ' · <i>' + esc(r.why) + '</i>' : '') + '</small></span></button>').join('')).join(''));
  }

  // ─── Кнопки поля й «🎨 Вигляд» ─────────────────────────────────────────
  function renderTools(st) {
    if (!st.el) return;
    const k3 = st.scKind === '3d';
    const snd = !!(st.snd && st.snd.on);
    setHtml(st.el.tools, '<span class="sol-seg" role="group" aria-label="Поле">'
      + '<button type="button" class="sol-view' + (k3 ? ' on' : '') + '" data-a="view3d" aria-pressed="' + k3 + '" title="' + (st.no3d ? '3D не вдалося — спробувати ще' : 'Об\'ємна мапа') + '">🧊 3D</button>'
      + '<button type="button" class="sol-view' + (st.scKind === '2d' ? ' on' : '') + '" data-a="view2d" aria-pressed="' + (st.scKind === '2d') + '" title="Пласка мапа (легша для слабких пристроїв)">🗺<span class="sol-bl"> Пласка</span></button></span>'
      + (st.snd ? '<button type="button" class="sol-view sol-snd" data-a="snd" aria-pressed="' + snd + '" title="' + (snd ? 'Звук увімкнено — вимкнути' : 'Звук вимкнено — увімкнути') + '" aria-label="Звук">' + (snd ? '🔊' : '🔇') + '</button>' : '')
      + (M.T.THEMES.length > 1 ? '<button type="button" class="sol-view sol-thb" data-a="pop" aria-expanded="' + !!st.pop + '" title="Вигляд гри" aria-label="Вигляд гри">🎨<span class="sol-bl"> Вигляд</span></button>' : ''));
    const home = st.mode === 'ph' ? 'Мої замовлення / уся мапа' : 'Уся мапа';
    setHtml(st.el.zoom, '<button type="button" data-a="zoomin" aria-label="Ближче" title="Ближче (+)">+</button><button type="button" data-a="zoomout" aria-label="Далі" title="Далі (−)">−</button><button type="button" data-a="reset" aria-label="' + home + '" title="' + home + ' (0)">⌂</button>');
  }
  function renderPop(st) {
    const p = st.el && st.el.pop;
    if (!p) return;
    if (!st.pop) { p.hidden = true; setHtml(p, ''); return; }
    const A = M.A, k3 = st.scKind === '3d', snd = !!(st.snd && st.snd.on);
    setHtml(p, '<div class="sol-popin" role="dialog" aria-label="Вигляд гри"><div class="sol-poph"><b>🎨 Вигляд</b><button type="button" class="sol-x" data-a="popx" aria-label="Закрити">✕</button></div>'
      + '<div class="sol-thl">' + M.T.THEMES.map((t) => '<button type="button" class="sol-tho' + (t.id === st.theme ? ' on' : '') + '" data-a="theme" data-t="' + t.id + '" aria-pressed="' + (t.id === st.theme) + '">' + A.previewSvg(t.id) + '<span><b>' + esc(t.name) + '</b><small>' + esc(t.hint) + '</small></span></button>').join('') + '</div>'
      + '<div class="sol-poprow"><span>Поле</span><span class="sol-seg"><button type="button" class="sol-view' + (k3 ? ' on' : '') + '" data-a="view3d">🧊 3D</button><button type="button" class="sol-view' + (k3 ? '' : ' on') + '" data-a="view2d">🗺 Пласка</button></span></div>'
      + (st.snd ? '<div class="sol-poprow"><span>Звук</span><button type="button" class="sol-view" data-a="snd" aria-pressed="' + snd + '">' + (snd ? '🔊 увімкнено' : '🔇 вимкнено') + '</button></div>' : '')
      + '</div>');
    p.hidden = false;
    const k = st.el.root.getBoundingClientRect();
    const fromEl = st.el.tools.querySelector('[data-a="pop"]');
    const b = fromEl && fromEl.getClientRects().length ? fromEl.getBoundingClientRect() : null;
    const w = p.offsetWidth, h = p.offsetHeight;
    let x = b ? b.right - k.left - w : (k.width - w) / 2, y = b ? b.bottom - k.top + 8 : 60;
    if (y + h > k.height - 4) y = b ? b.top - k.top - h - 8 : 40;
    p.style.left = px(clamp(x, 6, Math.max(6, k.width - w - 6)));
    p.style.top = px(clamp(y, 4, Math.max(4, k.height - h - 4)));
  }

  // ─── Підсумок ───────────────────────────────────────────────────────────
  function renderSum(root, st, v, me) {
    const el = st.el.sum;
    const over = v.over;
    const key = over ? 'over' + v.seq : null;
    if (!!key !== !!st.sumKey) { st.sumKey = key; st.sumMin = false; }
    if (!over) { el.hidden = true; setHtml(el, ''); st.fin = null; return; }
    if (!st.fin) st.fin = { stage: 9 };   // зайшли в уже скінчену партію — усе відразу (живий кінець вмикає подія over)
    countGame(st, over);
    if (st.fin.pending) { el.hidden = true; setHtml(el, ''); return; }   // подія over ще в черзі: спершу дограємо анімації
    el.classList.toggle('min', !!st.sumMin);
    setHtml(el, overHtml(st, v, me));
    el.hidden = false;
    // Вміщаємо без прокрутки: не влізло — ущільнюємо (менший графік, вужчі рядки).
    el.classList.remove('tight');
    if (st.mode === 'pc' && !st.sumMin && el.scrollHeight > el.clientHeight + 1) el.classList.add('tight');
  }
  function countGame(st, over) {
    const key = JSON.stringify(over.points || []);
    if (st.noteOverDone === key) return;
    st.noteOverDone = key;
    if (st.view.me) store.set('sol.games', String((+store.get('sol.games') || 0) + 1));
  }
  function overHtml(st, v, me) {
    const K = M.K, C = M.C, A = M.A, o = v.over, s = o.stats || {};
    const w = o.winners || [];
    const stage = st.fin ? st.fin.stage : 9;
    const final = (o.final || []).slice();
    const rows = final.slice().sort((a, b) => (stage >= 3 ? b.total - a.total : b.routes - a.routes));
    const again = st.ctx && st.ctx.mine ? '<button type="button" class="sol-btn sol-primary" data-a="again">🔁 Ще раз</button>' : '';
    const minBtn = '<button type="button" class="sol-btn" data-a="summin" aria-expanded="' + !st.sumMin + '">' + (st.sumMin ? '📊 Підсумок ▾' : '🗺 Подивитись мапу') + '</button>';
    const skip = stage < 9 ? '<button type="button" class="sol-btn" data-a="finskip">Пропустити ⏭</button>' : '';
    const title = stage < 3 ? '🧂 Рахуємо сіль…' : w.length > 1 ? '🤝 Валку довели разом' : w.length ? '🏆 Голова валки — ' + esc(cleanNick(nickOf(v, w[0]))) : '🏁 Партію скінчено';
    const why = o.reason === 'stall' ? 'карти й замовлення скінчились' : '🏁 вози в ' + esc(cleanNick(nickOf(v, o.lastBy))) + ' скінчились';
    let h = '<div class="sol-sumh"><h3>' + title + '</h3><div class="sol-sumb">' + skip + minBtn + (stage >= 9 ? again : '') + '</div></div>';
    h += '<p class="sol-sump">' + why + (s.rounds ? ' · ' + s.rounds + ' ' + C.plural(s.rounds, 'коло', 'кола', 'кіл') + ' · ' + (s.turns || 0) + ' ' + C.plural(s.turns || 0, 'хід', 'ходи', 'ходів') + ' · ' + K.minutes(s.ms) : '') + '</p>';
    const num = (n, sign, on) => (on ? (sign && n ? sign : '') + n : '·');
    h += '<div class="sol-tbl"><table class="sol-stats"><thead><tr><th></th><th title="шляхи">🛞<span class="sol-bl"> шляхи</span></th><th title="виконано">✅<span class="sol-bl"> виконано</span></th><th title="зірвано">❌<span class="sol-bl"> зірвано</span></th><th title="Найдовша валка">🐂<span class="sol-bl"> валка</span></th><th title="разом">⭐<span class="sol-bl"> разом</span></th></tr></thead><tbody>'
      + rows.map((f) => {
        const p = playerOf(v, f.seat) || { color: f.seat, nick: '?' };
        const cls = (w.includes(f.seat) && stage >= 3 ? 'win ' : '') + (me && me.seat === f.seat ? 'me' : '');
        const cur2 = st.fin && st.fin.seat === f.seat && stage === 1 ? ' cur' : '';
        const tks = (f.tickets || []).map((t) => { const x = C.ticket(t.id); const seen = stage >= 2 || (st.fin && st.fin.shown && st.fin.shown[f.seat + ':' + t.id]); return seen ? '<li class="' + (t.done ? 'ok' : 'no') + '">' + (t.done ? '✅ +' : '❌ −') + x.pts + ' ' + esc(C.ticketName(t.id)) + '</li>' : '<li class="hid">📜 ?</li>'; }).join('');
        return '<tr class="' + cls + cur2 + '"><td><span class="sol-sumn">' + shapeTag(st, p) + '<b>' + esc(cleanNick(p.nick)) + '</b></span><ul class="sol-sumt">' + tks + '</ul></td><td>' + f.routes + '</td><td class="ok">' + num(f.done, '+', stage >= 2) + '</td><td class="no">' + num(f.open, '−', stage >= 2) + '</td><td>' + num(f.bonus, '+', stage >= 3) + '</td><td class="tot">' + num(f.total, '', stage >= 3) + '</td></tr>';
      }).join('') + '</tbody></table></div>';
    if (stage >= 3) {
      const seats = (v.players || []).map((p) => p.seat);
      const CH = K.chart(s.timeline, seats, 420, 170);
      let svg = '<svg class="sol-chart" viewBox="0 0 420 170" role="img" aria-label="Очки шляхів за колами">';
      for (const t of CH.ticks) svg += '<line x1="' + CH.x0 + '" x2="' + CH.x1 + '" y1="' + t.y + '" y2="' + t.y + '" class="sol-gl"/><text x="' + (CH.x0 - 4) + '" y="' + (t.y + 3) + '" class="sol-ax" text-anchor="end">' + t.v + '</text>';
      for (const l of CH.lines) {
        const p = playerOf(v, l.seat);
        svg += '<path d="' + l.d + '" fill="none" stroke="' + seatColor(st, p.color) + '" stroke-width="2.2" stroke-linejoin="round"/><text x="' + l.end[0] + '" y="' + (l.end[1] + 4) + '" text-anchor="middle" class="sol-chs" fill="' + seatColor(st, p.color) + '">' + C.SEAT_SHAPE[p.color] + '</text>';
      }
      svg += '</svg>';
      const T = K.titles(o, (x) => { const p = playerOf(v, x); return p && !p.bot; });
      const tt = T.length ? '<div><div class="sol-trl">Звання</div><ul class="sol-titles">' + T.map((t) => '<li>' + t.icon + ' <b>' + esc(t.name) + '</b> — ' + esc(cleanNick(nickOf(v, t.seat))) + ' <span class="sol-mut">(' + esc(t.why) + ')</span></li>').join('') + '</ul></div>' : '';
      h += '<div class="sol-sumgrid"><div><div class="sol-trl">⭐ Шляхи за колами</div>' + svg + '</div>' + tt + '</div>';
    }
    return h;
  }

  /// Живий кінець партії: покрокове відкриття (шляхи → замовлення кожного → Найдовша валка → фініш).
  async function runFinale(root, st) {
    const v = st.view, o = v && v.over;
    if (!o) return;
    const sc = st.sc;
    // «Ще раз» посеред відкриття скидає st.fin: цей запуск має тихо зійти, а не читати чуже.
    const fin = { stage: 0, shown: {}, path: null, skip: false };
    st.fin = fin;
    const gone = () => root._sl !== st || st.fin !== fin;
    const redraw = () => { if (!gone() && st.view) { renderBanner(st, st.view, st.view.me || null); renderSum(root, st, st.view, st.view.me || null); } };
    redraw();
    await wait(reduced() ? 50 : 700);
    if (gone()) return;
    fin.stage = Math.max(fin.stage, 1); redraw();
    const stepMs = reduced() ? 40 : Math.min(900, 7000 / Math.max(1, (o.final || []).reduce((n, f) => n + (f.tickets || []).length, 0)));
    for (const f of o.final || []) {
      fin.seat = f.seat;
      const p = playerOf(v, f.seat) || { routes: [] };
      for (const t of f.tickets || []) {
        if (fin.skip || gone()) break;
        fin.shown[f.seat + ':' + t.id] = true;
        const x = M.C.ticket(t.id);
        try { if (sc) { sc.setTickets([{ a: x.a, b: x.b, done: !!t.done }]); sc.setPath(t.done ? netPath(p.routes, x.a, x.b) : null); } } catch (e) { /* ок */ }
        sfx(st, t.done ? 'done' : 'ticket');
        redraw();
        await wait(stepMs);
      }
    }
    if (gone()) return;
    fin.stage = Math.max(fin.stage, 2); fin.seat = null; redraw();
    try { if (sc) { sc.setPath(null); sc.setTickets([]); } } catch (e) { /* ок */ }
    const L = o.longest;
    if (L && L.seats && L.seats.length && sc && !fin.skip) {
      sfx(st, 'longest');
      await cap(sc.longest({ seats: L.seats, len: L.len, routes: L.routes }), 2400);
    }
    if (gone()) return;
    fin.stage = 3; redraw();
    if (sc && !fin.skip) await cap(sc.finale(o), 2800);
    if (gone()) return;
    st.fin = { stage: 9 };
    if (st.view) { renderBanner(st, st.view, st.view.me || null); renderSum(root, st, st.view, st.view.me || null); }
  }

  // ─── Таймер, Глеки, «час минув» ─────────────────────────────────────────
  function fmt(ms) { const s = Math.max(0, Math.ceil(ms / 1000)); return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0'); }
  function tickArcs(st) {
    const v = st.view;
    if (!v || !st.el) return;
    const left = v.deadline && !v.over ? Math.max(0, Date.parse(v.deadline) - Date.now()) : null;
    const total = v.timerMs || 60000;
    const frac = left == null ? 1 : Math.min(1, left / total);
    const late = left != null && left < 10000 && v.phase !== 'over';
    const k = st.el.root;
    k.classList.toggle('sol-late', !!late);
    for (const s of k.querySelectorAll('[data-sec]')) { const t = left == null ? '' : fmt(left); if (s.textContent !== t) s.textContent = t; }
    for (const b of k.querySelectorAll('.sol-burn > i')) b.style.width = (frac * 100).toFixed(1) + '%';
    const lit = Math.round(frac * 24);
    for (const r of k.querySelectorAll('.sol-rose')) { if (r._lit === lit) continue; r._lit = lit; for (const g of r.querySelectorAll('.sol-pt')) g.classList.toggle('off', +g.dataset.i >= lit); }
  }
  function myRank(v, ctx) {
    let rank = 0;
    for (const p of v.players || []) if (p.seat < ctx.seat && !p.bot && !p.away) rank++;
    return rank;
  }
  function tick(root) {
    const st = root._sl;
    if (!st || !st.view) return;
    tickArcs(st);
    tickSound(st, st.view);
    tickMini(st);
    const v = st.view, ctx = st.ctx;
    if (!ctx || !ctx.mine || !ctx.playing || !ctx.room || v.over || !HGames.send) return;
    if (!v.deadline) return;
    const now = Date.now();
    const due = Date.parse(v.deadline) + 500 + myRank(v, ctx) * 700;
    if (now < due) return;
    if (st.toKey === v.deadline && now - st.toAt < 4000) return;
    st.toKey = v.deadline; st.toAt = now;
    HGames.send('Act', ctx.room.id, 'timeout', {});
  }
  function tickSound(st, v) {
    const me = v.me;
    if (!st.snd || !st.snd.on || !me || !me.must || !v.deadline) return;
    const left = Date.parse(v.deadline) - Date.now();
    if (!(left > 0 && left <= 5000)) return;
    const key = v.deadline + '|' + Math.ceil(left / 1000);
    if (st.tickKey === key) return;
    st.tickKey = key;
    sfx(st, 'tick');
  }
  function tickMini(st) {
    const el = st.el.mini;
    if (!el) return;
    if (st.mode === 'pc' || !st.sc || !st.sc.screenPos || st.scLoading) { if (!el.hidden) el.hidden = true; return; }
    const f = st.el.field.getBoundingClientRect();
    const r = M.K.viewportOf((k, id) => st.sc.screenPos(k, id), f.width, f.height);
    const show = !!(r && r.zoom >= 2);
    if (el.hidden === show) { el.hidden = !show; st.el.root.classList.toggle('sol-minion', show); }
    if (!show) return;
    const land = st.geo ? st.geo.land : '';
    setHtml(el, '<button type="button" data-a="reset" aria-label="Уся мапа"><svg viewBox="-40 -40 4080 2810" aria-hidden="true"><path d="' + land + '" class="sol-mml"/><rect x="' + r.x.toFixed(0) + '" y="' + r.y.toFixed(0) + '" width="' + r.w.toFixed(0) + '" height="' + r.h.toFixed(0) + '" class="sol-mmv"/></svg></button>');
  }
  /// Глеки ходять на штовхан клієнта людини: перша людина за столом — першою, решта — із запасом.
  function nudge(root, st) {
    const v = st.view || {}, ctx = st.ctx;
    // Перемальовка без нового виду: botIn у ньому застарів, а таймер уже стоїть на правильний час — не чіпаємо.
    const mine = !!(ctx && ctx.playing && ctx.mine);
    if (st.view && st.ntView === st.view && st.ntMine === mine) return;
    st.ntView = st.view; st.ntMine = mine;
    clearTimeout(st.nt);
    st.nt = 0;
    if (!ctx || !ctx.playing || !ctx.mine || typeof v.botIn !== 'number' || !HGames.send || !ctx.room) return;
    const fire = () => {
      if (root._sl !== st || !st.ctx || !st.ctx.room) return;
      HGames.send('Act', st.ctx.room.id, 'bot', null);
      st.nt = setTimeout(fire, 1500);
    };
    st.nt = setTimeout(fire, Math.max(0, v.botIn) + 60 + myRank(v, ctx) * 700);
  }
  /// Увага без звуку: вібрація ≤ раз на 5 с, заголовок мигає, поки вкладка у фоні.
  function attention(st, v, me) {
    const must = me && me.must;
    const key = must ? must + '|' + v.turns : null;
    if (key && key !== st.attnKey) {
      st.attnKey = key;
      const now = Date.now();
      if (now - st.attn > 5000 && navigator.vibrate) { st.attn = now; try { navigator.vibrate(60); } catch (e) { /* ок */ } }
    }
    if (!key) st.attnKey = null;
    clearInterval(st.titleT);
    st.titleT = 0;
    if (st.title0 != null && (document.visibilityState === 'visible' || !must)) { document.title = st.title0; st.title0 = null; }
    if (must && document.visibilityState !== 'visible') {
      if (st.title0 == null) st.title0 = document.title;
      const msg = must === 'setup' || must === 'keep' ? '📜 Обери замовлення — Соляний шлях' : '🛞 Твій хід — Соляний шлях';
      let on = false;
      st.titleT = setInterval(() => { on = !on; document.title = on ? msg : (st.title0 || 'Соляний шлях'); if (document.visibilityState === 'visible') { clearInterval(st.titleT); st.titleT = 0; document.title = st.title0 || document.title; st.title0 = null; } }, 1000);
    }
  }

  // ─── Події: анімації по черзі ───────────────────────────────────────────
  function queueEvents(root, st, v) {
    if (typeof v.seq !== 'number') return;
    // Два лічильники (§7): спільний seq і мій own — події «лише власнику» в спільному seq нового номера не беруть.
    const seat = v.me ? v.me.seat : null, own = v.me && typeof v.me.own === 'number' ? v.me.own : 0;
    const fresh0 = st.seq != null && (v.seq < st.seq || (st.seqPhase === 'over' && v.phase !== 'over'));
    st.seqPhase = v.phase;
    if (fresh0) { st.seq = v.seq; st.own = own; st.ownSeat = seat; st.queue.length = 0; st.fin = null; return; }
    if (st.seq == null) { st.seq = v.seq; st.own = own; st.ownSeat = seat; return; }   // перший mount — нічого не анімуємо
    if (seat !== st.ownSeat) { st.ownSeat = seat; st.own = own; }   // сів на інше місце: його минуле — не новина
    const F = M.K.freshEvents(v, st.seq, st.own), fresh = F.list;
    st.seq = F.seq; st.own = F.own;
    if (!fresh.length) return;
    st.queue.push(...fresh);
    if (st.queue.length > 14) {
      // Відстали (вкладка спала) — лише свіже; кінець партії не губимо.
      const dropped = st.queue.splice(0, st.queue.length - 14);
      const o = dropped.filter((e) => e.k === 'over' || e.k === 'longest');
      if (o.length) st.queue.unshift(...o);
    }
    runQueue(root, st);
  }
  async function runQueue(root, st) {
    if (st.running) return;
    st.running = true;
    try {
      while (st.queue.length && root._sl === st) {
        const e = st.queue.shift();
        await cap(playEvent(root, st, e), 3000);
      }
    } finally {
      st.running = false;
      if (root._sl === st) {
        if (st.fin && st.fin.pending && st.view) { st.fin = { stage: 9 }; renderSum(root, st, st.view, st.view.me || null); }   // страховка: подія over так і не прийшла
        syncScene(root, st);
      }
    }
  }

  const chipOf = (st, seat) => st.el.root.querySelector('.sol-pl[data-seat="' + seat + '"]');
  /// Карта летить від → до (рамки екрана). HTML-анімація ≤ 400 мс; reducedMotion — без неї.
  function fly(st, from, to, html, ms = 380) {
    if (!from || !to || reduced()) return Promise.resolve();
    const k = st.el.root.getBoundingClientRect();
    const a = from.getBoundingClientRect(), b = to.getBoundingClientRect();
    if (!a.width || !b.width) return Promise.resolve();
    const el = document.createElement('div');
    el.className = 'sol-fly';
    el.innerHTML = html;
    const w = clamp(a.width, 36, 90), h = clamp(a.height, 40, 120);
    el.style.width = px(w); el.style.height = px(h);
    el.style.left = px(a.left - k.left); el.style.top = px(a.top - k.top);
    st.el.fx.appendChild(el);
    const dx = b.left + b.width / 2 - w / 2 - a.left, dy = b.top + b.height / 2 - h / 2 - a.top, s = Math.max(0.5, Math.min(1, b.width / w));
    const an = el.animate([{ transform: 'none', opacity: 1 }, { transform: 'translate(' + dx.toFixed(0) + 'px,' + dy.toFixed(0) + 'px) scale(' + s.toFixed(2) + ')', opacity: 0.85 }], { duration: ms, easing: 'cubic-bezier(.3,.7,.3,1)' });
    return an.finished.catch(() => {}).then(() => el.remove());
  }
  function bump(st, seat, text) {
    const chip = chipOf(st, seat);
    if (!chip || !text) return;
    const b = document.createElement('span');
    b.className = 'sol-gain';
    b.textContent = text;
    chip.appendChild(b);
    setTimeout(() => b.remove(), reduced() ? 900 : 1500);
  }
  function slotFx(st, slot, cls) {
    const el = st.el.market.querySelector('.sol-mc[data-slot="' + slot + '"]');
    if (!el || reduced()) return;
    el.classList.remove(cls); void el.offsetWidth; el.classList.add(cls);
    setTimeout(() => el.classList.remove(cls), 420);
  }
  function tickerAdd(st, html) {
    const box = st.el.ticker;
    const el = document.createElement('div');
    el.className = 'sol-tkr';
    el.innerHTML = html;
    box.appendChild(el);
    while (box.children.length > (st.mode === 'pc' ? 3 : 1)) box.firstChild.remove();   // на телефоні мапа важливіша: один рядок
    setTimeout(() => el.classList.add('out'), 5400);
    setTimeout(() => el.remove(), 6000);
  }
  /// Бульбашка над мапою: над шляхом; шлях поза вікном — у кутку зі стрілкою.
  function bubble(st, id, html, ms) {
    const el = st.el.bubble;
    el.innerHTML = html;
    el.hidden = false;
    el.classList.remove('corner', 'go'); void el.offsetWidth; el.classList.add('go');
    const f = st.el.field.getBoundingClientRect();
    const p = id != null && st.sc && st.sc.screenPos ? st.sc.screenPos('route', id) : null;
    const inside = p && p.x >= 0 && p.y >= 0 && p.x <= f.width && p.y <= f.height;
    // Довгий напис («… — Кам'янець-Подільський +2 ⭐») не влазить у 90 px від краю: тримаємо за справжню ширину.
    const hw = Math.ceil(el.offsetWidth / 2) + 4;
    if (inside) {
      el.style.left = px(clamp(p.x, hw, Math.max(hw, f.width - hw))); el.style.top = px(Math.max(Math.ceil(el.offsetHeight * 1.15) + 4, p.y));
    } else {
      el.classList.add('corner');
      el.style.left = px(clamp(110, hw, Math.max(hw, f.width - hw))); el.style.top = px(44);
    }
    clearTimeout(st.bubT);
    st.bubT = setTimeout(() => { el.hidden = true; }, ms);
  }
  function flash(st, html, ms = 2400) {
    const f = st.el && st.el.flash;
    if (!f) return;
    f.innerHTML = html;
    f.hidden = false;
    f.classList.remove('go'); void f.offsetWidth; f.classList.add('go');
    clearTimeout(st.flashT);
    st.flashT = setTimeout(() => { f.hidden = true; }, ms);
  }

  async function playEvent(root, st, e) {
    const v = st.view, sc = st.sc, me = v && v.me;
    if (!v) return;
    const C = M.C, A = M.A, K = M.K, th = st.theme, mine = (s) => me && s === me.seat;
    const N = (s) => esc(cleanNick(nickOf(v, s)));
    const queued = () => st.queue.length > 0;
    switch (e.k) {
      case 'turn':
        if (mine(e.seat)) sfx(st, 'turn');
        break;
      case 'draw': {
        sfx(st, 'card');
        const slot = e.from === 'market' ? st.el.market.querySelector('.sol-mc[data-slot="' + e.slot + '"]') : st.el.market.querySelector('.sol-deck');
        const face = e.card ? A.cardHtml(th, e.card) : A.backHtml(th);
        if (mine(e.seat)) {
          const to = (e.card && st.el.hand.querySelector('.sol-hc[data-k="' + e.card + '"]')) || st.el.hand;
          await fly(st, slot, to, '<span class="sol-top">' + face + '</span>');
        } else {
          const chip = chipOf(st, e.seat);
          bump(st, e.seat, '🂠 +1');
          await fly(st, slot, chip, '<span class="sol-top">' + face + '</span>');
          tickerAdd(st, N(e.seat) + ' бере ' + (e.card ? C.ICON_CARGO[e.card] + ' ' + C.CARGO_ACC[e.card] : 'карту наосліп'));
        }
        break;
      }
      case 'refill':
        slotFx(st, e.slot, 'in');
        await wait(120);
        break;
      case 'wipe': {
        sfx(st, 'wipe');
        flash(st, '🐂🐂🐂 На ринку три воли — карти змінено');
        for (let i = 0; i < 5; i++) slotFx(st, i, 'outin');
        await wait(420);
        break;
      }
      case 'shuffle':
        tickerAdd(st, '🔀 Скид перетасовано' + (e.n ? ': ' + K.cards(e.n) : ''));
        break;
      case 'claim': {
        sfx(st, 'claim');
        const name = esc(C.routeName(e.route));
        if (!mine(e.seat)) {
          bubble(st, e.route, '🚩 ' + name + ' <b>+' + e.pts + ' ⭐</b>', queued() ? 1600 : 3000);
          tickerAdd(st, N(e.seat) + ' займає ' + name + ' <b>+' + e.pts + ' ⭐</b>');
        }
        if (sc) await cap(sc.claim(e), 1500);
        if (st.sc) try { st.stateSig = null; } catch (err) { /* ок */ }
        break;
      }
      case 'tickets':
        if (!mine(e.seat)) { bump(st, e.seat, '📜 +' + e.n); tickerAdd(st, N(e.seat) + ' бере замовлення'); }
        else sfx(st, 'ticket');
        break;
      case 'keep': if (!mine(e.seat)) tickerAdd(st, N(e.seat) + ' лишає замовлень: ' + e.kept); break;
      case 'setup': if (!mine(e.seat)) tickerAdd(st, '✔ ' + N(e.seat) + ' обрав замовлення'); break;
      case 'done': {
        if (!mine(e.seat)) break;
        sfx(st, 'done');
        const x = C.ticket(e.ticket);
        bubble(st, null, '✅ ' + esc(C.ticketName(e.ticket)) + ' <b>+' + x.pts + ' ⭐</b>', 2800);
        if (sc) await cap(sc.ticketDone(e), 1200);
        break;
      }
      case 'last': {
        sfx(st, 'last');
        const el = st.el.lastban;
        const n = e.wagons;
        el.innerHTML = '🏁 У ' + N(e.seat) + ' ' + K.wagons(n) + ': кожен має ще один хід';
        el.hidden = false;
        el.classList.remove('go'); void el.offsetWidth; el.classList.add('go');
        clearTimeout(st.lastT);
        st.lastT = setTimeout(() => { el.hidden = true; }, 2500);
        if (sc) await cap(sc.last(e), 1200);
        break;
      }
      case 'pass': if (!mine(e.seat)) tickerAdd(st, N(e.seat) + ' пропускає хід'); break;
      case 'over':
        if (root._sl === st && st.view && st.view.over) runFinale(root, st);
        break;
      case 'longest':
        // Котить runFinale разом із підсумком; подія без over — окремо (стенд).
        if (sc && !(st.view && st.view.over)) { sfx(st, 'longest'); await cap(sc.longest(e), 2400); }
        break;
      default:
    }
  }

  // ─── Кліки ──────────────────────────────────────────────────────────────
  function onClick(root, e) {
    const st = root._sl;
    const b = e.target.closest('[data-a]');
    if (!b || !st || !root.contains(b) || b.disabled) return;
    const ctx = st.ctx, v = st.view, me = v && v.me;
    switch (b.dataset.a) {
      case 'view3d': switchView(root, st, '3d'); renderPop(st); return;
      case 'view2d': switchView(root, st, '2d'); renderPop(st); return;
      case 'reset': if (st.sc) st.sc.resetView(); return;
      case 'zoomin': if (st.sc) st.sc.zoomBy(1.4); return;
      case 'zoomout': if (st.sc) st.sc.zoomBy(1 / 1.4); return;
      case 'snd': if (st.snd) { st.snd.setOn(!st.snd.on); renderTools(st); renderPop(st); } return;
      case 'pop': st.pop = !st.pop; renderTools(st); renderPop(st); return;
      case 'popx': st.pop = false; renderTools(st); renderPop(st); return;
      case 'theme': st.pop = true; switchTheme(root, st, b.dataset.t); renderTools(st); renderPop(st); return;
      case 'hint': if (b.dataset.hint && coarse()) flash(st, esc(b.dataset.hint)); return;
      case 'slot': doDraw(root, st, { slot: +b.dataset.slot }); return;
      case 'deck': doDraw(root, st, { deck: true }); return;
      case 'tickets': if (doTickets(root, st)) { st.sheet = null; } return;
      case 'pass': if (me && me.can && me.can.pass && ctx) ctx.act('pass', {}); return;
      case 'claim': doClaim(root, st); return;
      case 'combo': st.pick = +b.dataset.i; break;
      case 'advice': {
        const P = st.sel != null ? M.K.routePanel(v, st.sel, st.pick) : null;
        if (P && P.advice) doDraw(root, st, P.advice.deck ? { deck: true } : { slot: P.advice.slot });
        return;
      }
      case 'cancel': clearSel(st); break;
      case 'ticket': {
        const id = +b.dataset.id;
        st.pathT = st.pathT === id ? null : id;
        if (st.pathT != null && st.sc) {
          // ПК і так бачить усю мапу; на телефоні — обидва міста замовлення (§11.3).
          const x = M.C.ticket(id);
          try { if (st.mode === 'pc') st.sc.focus('all'); else st.sc.focus('cities', [x.a, x.b]); } catch (err) { /* ок */ }
        }
        if (st.sheet === 'tickets' && st.mode !== 'pc' && st.pathT != null) st.sheet = null;
        break;
      }
      case 'offer': {
        const id = +b.dataset.id;
        if (!st.keep) return;
        if (st.keep.has(id)) st.keep.delete(id); else st.keep.add(id);
        break;
      }
      case 'keepgo':
        if (st.keep && me && st.keep.size >= me.keepMin && ctx) { ctx.act('keep', { ids: [...st.keep] }); st.keep = new Set(); }
        return;
      case 'tip': {
        const on = st.tipNow || tipOn(st);
        if (on && !st.keep && !(me && me.tip && me.tip.act === 'claim' && st.sel !== me.tip.route)) { store.set('sol.tip', '0'); st.tipNow = false; break; }
        store.set('sol.tip', '1');
        applyTip(root, st);
        return;
      }
      case 'sheet': {
        const s = b.dataset.s;
        st.sheet = st.sheet === s ? null : s;
        if (s === 'picker') { st.pickQ = ''; st.el.sheet.dataset.k = ''; }
        break;
      }
      case 'sheetx': st.sheet = null; break;
      case 'pick': selectRoute(root, st, +b.dataset.id); return;
      case 'tab': st.tab = b.dataset.t; break;
      case 'summin': st.sumMin = !st.sumMin; break;
      case 'finskip': if (st.fin) { st.fin.skip = true; st.fin.stage = Math.max(st.fin.stage, 3); } break;
      case 'again': {
        const rb = (root.closest('.groom') || document).querySelector('[data-do="Rematch"]');
        if (rb) rb.click(); else if (ctx && ctx.toast) ctx.toast('Ще раз зараз не вийде — замало гравців за столом');
        return;
      }
      case 'rejoin': {
        const card = root.closest('.gtable');
        const jb = card && card.querySelector('.gbtns [data-do="JoinRoom"]');
        if (jb) jb.click(); else if (ctx && ctx.room && HGames.sitAt) HGames.sitAt(ctx.room.id, b);
        return;
      }
      default: return;
    }
    paint(root, ctx);
  }

  // ─── Клавіатура й джойстик ──────────────────────────────────────────────
  function moveCursor(root, st, dir) {
    const list = M.K.legalRoutes(st.view);
    if (!list.length) return false;
    st.cur = M.K.cycleRoute(list, st.cur != null ? st.cur : st.sel, dir);
    if (st.sc && st.cur != null) { try { st.sc.focus('route', st.cur); } catch (e) { /* ок */ } }
    syncScene(root, st);
    flash(st, esc(M.C.routeName(st.cur)) + ' · Enter — відкрити', 1400);
    return true;
  }
  function nextCombo(root, st, dir) {
    if (st.sel == null) return false;
    const P = M.K.routePanel(st.view, st.sel, st.pick);
    if (!P || !P.ok || P.combos.length < 2) return false;
    st.pick = (P.pick + dir + P.combos.length) % P.combos.length;
    paint(root, st.ctx);
    return true;
  }
  function enterKey(root, st) {
    if (st.cur != null && st.cur !== st.sel) { selectRoute(root, st, st.cur, 'map'); return true; }
    if (st.sel != null) return doClaim(root, st);
    return false;
  }
  function keyDown(root, e) {
    const st = root._sl, v = st && st.view;
    if (!st || !v || !M) return false;
    if (e.target && /^(input|textarea|select)$/i.test(e.target.tagName || '')) return false;
    const me = v.me, k = e.key, code = e.code || '';
    if (k === 'Escape') {
      if (st.pop) { st.pop = false; renderTools(st); renderPop(st); return true; }
      if (st.sheet) { st.sheet = null; paint(root, st.ctx); return true; }
      if (st.sel != null || st.cur != null || st.pathT != null) { st.pathT = null; clearSel(st); paint(root, st.ctx); return true; }
      return false;
    }
    if (k === '+' || k === '=') { if (st.sc) st.sc.zoomBy(1.4); return true; }
    if (k === '-' || k === '_') { if (st.sc) st.sc.zoomBy(1 / 1.4); return true; }
    if (k === '0') { if (st.sc) st.sc.resetView(); return true; }
    if (!me || !me.must) return false;
    const digit = /^Digit([1-5])$/.exec(code) || /^([1-5])$/.exec(k);
    if (digit) return doDraw(root, st, { slot: +digit[1] - 1 });
    if (code === 'KeyD' || k === 'd' || k === 'D') return doDraw(root, st, { deck: true });
    if (code === 'KeyT' || k === 't' || k === 'T') return doTickets(root, st);
    if (k === 'ArrowRight' || k === 'ArrowDown') return moveCursor(root, st, 1);
    if (k === 'ArrowLeft' || k === 'ArrowUp') return moveCursor(root, st, -1);
    if (k === 'Enter') return enterKey(root, st);
    if (k === '[') return nextCombo(root, st, -1);
    if (k === ']') return nextCombo(root, st, 1);
    return false;
  }

  // ─── Реєстрація ─────────────────────────────────────────────────────────
  HGames.register({
    id: 'solyanyi',
    icon: '🧂',
    seatClass: ['sol0', 'sol1', 'sol2', 'sol3', 'sol4'],
    added: '2026-11-05',
    /// Вийшов посеред партії й дивлюсь збоку — кнопка в шапці столу садить назад на своє місце (core.js rejoinLabel).
    rejoin: (rv, nick) => (ownAway(rv && rv.view, nick) ? REJOIN : ''),
    mount(root, ctx) { cur = root; paint(root, ctx); },
    update(root, ctx) { cur = root; paint(root, ctx); },
    unmount(root) {
      const st = root._sl;
      if (st) {
        clearInterval(st.tick); clearTimeout(st.nt); clearTimeout(st.flashT); clearTimeout(st.bubT); clearTimeout(st.lastT); clearInterval(st.titleT);
        if (st.title0 != null) document.title = st.title0;
        if (st.ro) st.ro.disconnect();
        if (st.ro2) st.ro2.disconnect();
        cancelAnimationFrame(st.fitRaf); st.fitRaf = 0;
        if (st.onWin) window.removeEventListener('resize', st.onWin);
        if (HGames.ui && HGames.ui.onFit) HGames.ui.onFit(root, null);
        const gt = root.closest('.gtable');
        if (gt && M) { gt.classList.remove('sol-gt', 'sol-gt-play'); for (const x of M.T.THEMES) gt.classList.remove('sol-gt-' + x.id); }   // картку каркаса може взяти інша гра
        if (HGames.ui && HGames.ui.headIn) HGames.ui.headIn(root, false);
        if (st.onClick) root.removeEventListener('click', st.onClick);
        if (st.onInput) root.removeEventListener('input', st.onInput);
        if (st.onDocDown) document.removeEventListener('pointerdown', st.onDocDown, true);
        st.scGen++;
        if (st.sc) { try { st.sc.dispose(); } catch (e) { /* уже прибрано */ } }
        if (st.snd) { try { st.snd.dispose(); } catch (e) { /* уже прибрано */ } st.snd = null; }
        st.queue.length = 0;
      }
      root._sl = null;
      if (cur === root) cur = null;
    },
    onKey(e) { return cur && cur._sl ? keyDown(cur, e) : false; },
    status(ctx) {
      if (!M) return '';
      return M.K.statusText(ctx && ctx.view, ctx);
    },
    pad: {
      dirs: false,
      when: (ctx) => ctx.mine && ctx.playing,
      hint: '{lb}{rb} шлях · {a} обрати · {x} оплата · {y} скасувати',
      on(btn) {
        const root = cur, st = root && root._sl, v = st && st.view;
        if (!st || !v || !v.me || !v.me.must || !M) return false;
        if (btn === 'lb' || btn === 'rb') { moveCursor(root, st, btn === 'lb' ? -1 : 1); return true; }
        if (btn === 'x') { nextCombo(root, st, 1); return true; }
        if (btn === 'y') { clearSel(st); st.sheet = null; paint(root, st.ctx); return true; }
        if (btn === 'a' && (st.cur != null || st.sel != null)) return enterKey(root, st);
        return false;   // Ⓐ — кільце тисне кнопку під собою
      },
    },
  });
})();
