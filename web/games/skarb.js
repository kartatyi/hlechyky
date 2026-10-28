/*
  «Скарб Полуботка» — приховані ролі на 3–10 (id skarb). Правила живуть на сервері (Impl/Skarb*.cs) — тут лише картка:
  що бачить і натискає гравець. Контракт виду й дій — docs/games/specs/skarb.md (§5–§9, §12).

  Поле — 3D-розріз (/games/skarb/scene.js) або пласка SVG-мапа (/games/skarb/board2d.js) з тим самим API; обидві
  вантажаться через import(). Сталі — /games/skarb/consts.js, чисті розрахунки — /games/skarb/hud-calc.js.

  Головне — новачок без правил у кожну мить бачить, що робити: смуга «що зараз», рука віялом, тап по карті підсвічує,
  куди її можна зіграти (законні місця рахує сервер — me.legal), «⟲ 180°», «🗑 Скинути», «💡 Глек радить».
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M2.5 7.5h11v6h-11z" fill="#8f5a2a" stroke="var(--accent)" stroke-width="1.1" stroke-linejoin="round"/>'
    + '<path d="M2.5 7.5Q8 2.5 13.5 7.5" fill="#a86a32" stroke="var(--accent)" stroke-width="1.1"/>'
    + '<circle cx="8" cy="10.4" r="1.4" fill="var(--accent)"/><path d="M2.5 10h11" stroke="var(--accent)" stroke-width=".8"/></svg>';

  let M = null;           // { C: consts, K: hud-calc, S: sound | null } — після load()
  let loading = null;
  let cur = null;         // root змонтованої картки (onKey, pad)

  function load() {
    if (!loading) {
      loading = Promise.all([import('/games/skarb/consts.js'), import('/games/skarb/hud-calc.js'), import('/games/skarb/sound.js').catch(() => null)])
        .then(([C, K, S]) => { M = { C, K, S }; return M; })
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
  const sess = {
    get(k) { try { return JSON.parse(sessionStorage.getItem(k) || 'null'); } catch (e) { return null; } },
    set(k, v) { try { sessionStorage.setItem(k, JSON.stringify(v)); } catch (e) { /* без пам'яті */ } },
  };
  /// «⛏» без VS16 частина шрифтів малює текстовою стрілкою — просимо емодзі-вигляд.
  const emo = (html) => String(html).replace(/⛏(?!\uFE0F)/g, '⛏\uFE0F');
  function setHtml(el, html) { if (el && el._h !== html) { el._h = html; el.innerHTML = emo(html); } }

  // ─── Хто є хто ───────────────────────────────────────────────────────────
  const playerOf = (v, seat) => ((v && v.players) || []).find((p) => p.seat === seat) || null;
  const nickOf = (v, seat) => { const p = playerOf(v, seat); return p ? p.nick : 'місце ' + (seat + 1); };
  const colorOf = (v, seat) => { const p = playerOf(v, seat); return (M ? M.C.SEAT_COLOR : [])[p ? p.color : seat] || '#999'; };
  const sameNick = (a, b) => String(a || '').toLowerCase() === String(b || '').toLowerCase();
  /// Моє місце, за яке копає Глек, поки я дивлюсь збоку (вийшов чи випав): сервер тримає його (Game.Rejoin).
  const ownAway = (v, nick) => (v && v.phase !== 'over' && !v.over && nick
    && ((v.players || []).find((p) => !p.bot && p.away && sameNick(p.nick, nick)))) || null;
  const REJOIN = '⛏ Повернутись до своєї лопати';
  const dot = (v, seat) => '<i class="skb-pdot" style="--pc:' + colorOf(v, seat) + '"></i>';
  const who = (v, seat) => dot(v, seat) + '<b>' + esc(nickOf(v, seat)) + '</b>';
  const plural = (n, a, b, c) => M.C.plural(n, a, b, c);

  function state(root) {
    return root._sk || (root._sk = {
      built: false, ctx: null, view: null, sc: null, scKind: null, scLoading: false, no3d: false, gridSig: null, legalSig: null,
      sel: null, cur: null, arm: null, fixPick: null, seq: null, queue: [], running: false, hover: null,
      logOpen: store.get('skb.log') !== '0', tick: 0, nt: 0, toKey: null, toAt: 0, nextKey: null, ro: null,
      reveal: null, revealT: 0, roleHold: false, sumAnim: null, lp: null, drag: null, noClick: 0,
    });
  }

  // ─── Що зараз ───────────────────────────────────────────────────────────
  function whatNow(v, me, st) {
    const w = M.K.whatNow(v, me, selCard(st, v), (s) => nickOf(v, s), esc);
    const p = me && v && v.phase === 'play' ? playerOf(v, me.seat) : null;
    if (p && p.away) w.l = '<b>🤖 За тебе зараз копає Глек</b> (ти двічі проспав хід). Зроби будь-що сам — і копатимеш далі.<br>' + w.l;
    const ctx = st && st.ctx;
    if (!me && ctx && ctx.seat == null && ctx.playing && ownAway(v, ctx.me && ctx.me.nick)) {
      w.s = 'Твоя лопата чекає';
      w.l = '<b>⛏ Твоя лопата на місці:</b> поки тебе не було, за тебе копав Глек. Повертайся — роль, рука й злитки ті самі.'
        + ' <button type="button" class="primary skb-back" data-a="rejoin">' + REJOIN + '</button><br>' + w.l;
    }
    return w;
  }

  /// Вибрана карта (ще є в руці, мій хід) — { card, r } або null.
  function selCard(st, v) {
    const me = v && v.me;
    if (!st.sel || !me || me.must !== 'play') return null;
    const card = (me.hand || []).find((c) => c.id === st.sel.id);
    return card ? { card, r: st.sel.r } : null;
  }

  // ─── Каркас DOM ─────────────────────────────────────────────────────────
  function build(root, st) {
    root.innerHTML = '<div class="skb">'
      + '<div class="skb-players"></div>'
      + '<div class="skb-sum" hidden></div>'
      + '<div class="skb-say" aria-live="polite"></div>'
      + '<div class="skb-fieldwrap"><div class="skb-field"></div><div class="skb-tip" hidden></div><div class="skb-flash" hidden></div>'
      + '<div class="skb-tools"><button type="button" class="skb-view" data-a="view"></button><button type="button" class="skb-view" data-a="reset" title="Показати все поле" aria-label="Показати все поле">⤢</button>'
      + '<button type="button" class="skb-view skb-snd" data-a="snd"></button></div>'
      + '<div class="skb-rolebadge" hidden></div><div class="skb-dlg" hidden></div></div>'
      + '<div class="skb-bar"><div class="skb-acts"></div><div class="skb-hand"></div></div>'
      + '<div class="skb-log"></div>'
      + '<div class="skb-reveal" hidden></div><div class="skb-fx"></div>'
      + '</div>';
    st.el = {};
    for (const k of ['players', 'sum', 'say', 'fieldwrap', 'field', 'tip', 'flash', 'rolebadge', 'fx', 'dlg', 'bar', 'acts', 'hand', 'log', 'reveal', 'snd'])
      st.el[k] = root.querySelector('.skb-' + k);
    st.el.view = root.querySelector('.skb-view[data-a="view"]');
    st.el.root = root.querySelector('.skb');
    try { st.snd = M.S && M.S.createSound ? M.S.createSound() : null; } catch (e) { st.snd = null; }
    paintSnd(st);
    st.onClick = (e) => onClick(root, e);
    root.addEventListener('click', st.onClick);
    // Довгий тап / права кнопка на чипі — моя позначка підозри; «👁 Моя роль» — натисни й тримай.
    st.onDown = (e) => onDown(root, e);
    st.onUp = (e) => onUp(root, e);
    st.onMove = (e) => onMove(root, e);
    st.onCtx = (e) => { const chip = e.target.closest('.skb-pl'); if (chip && root.contains(chip)) { e.preventDefault(); cycleSus(root, +chip.dataset.seat); } };
    root.addEventListener('pointerdown', st.onDown);
    window.addEventListener('pointerup', st.onUp);
    window.addEventListener('pointercancel', st.onUp);
    window.addEventListener('pointermove', st.onMove);
    root.addEventListener('contextmenu', st.onCtx);
    st.ro = typeof ResizeObserver === 'function' ? new ResizeObserver(() => { if (st.sc) st.sc.resize(); }) : null;
    if (st.ro) st.ro.observe(st.el.field);
    st.tick = setInterval(() => tick(root), 250);
    st.built = true;
  }

  // ─── Звук ───────────────────────────────────────────────────────────────
  function sfx(st, name, opts) { if (st.snd) { try { st.snd.play(name, opts); } catch (e) { /* звук не головне */ } } }
  function paintSnd(st) {
    const b = st.el && st.el.snd;
    if (!b) return;
    b.hidden = !st.snd;
    const on = !!(st.snd && st.snd.on);
    b.textContent = on ? '🔊' : '🔇';
    b.title = on ? 'Звук увімкнено — вимкнути' : 'Звук вимкнено — увімкнути';
    b.setAttribute('aria-label', b.title);
    b.setAttribute('aria-pressed', on ? 'true' : 'false');
  }
  function tickSound(st, v) {
    const me = v.me;
    if (!st.snd || !st.snd.on || !me || me.must !== 'play' || !v.deadline) return;
    const left = Date.parse(v.deadline) - Date.now();
    if (!(left > 0 && left <= 5000)) return;
    const key = v.deadline + '|' + Math.ceil(left / 1000);
    if (st.tickKey === key) return;
    st.tickKey = key;
    sfx(st, 'tick');
  }

  // ─── Поле: 3D або пласке ────────────────────────────────────────────────
  async function ensureScene(root, st) {
    if (st.sc || st.scLoading) return;
    st.scLoading = true;
    const host = st.el.field;
    const opts = {
      onPick: (k, a, b) => onPick(root, k, a, b),
      onHover: (k, a, b) => onHover(root, k, a, b),
      quality: coarse() || (navigator.hardwareConcurrency || 8) <= 4 ? 'low' : 'high',
      reducedMotion: reduced(),
    };
    let sc = null, kind = null;
    if (store.get('skb.view') !== '2d') {
      try {
        const m = await import('/games/skarb/scene.js');
        sc = await m.createScene(host, opts);
        kind = '3d';
      } catch (e) {
        console.warn('Скарб: 3D не вийшло — малюю пласку мапу', e);
        host.textContent = '';
        st.no3d = true;
      }
    }
    if (!sc) {
      try {
        const m = await import('/games/skarb/board2d.js');
        sc = await m.createBoard2d(host, opts);
        kind = '2d';
      } catch (e) {
        st.scLoading = false;
        host.innerHTML = '<p class="skb-err">Не вдалося намалювати поле. Онови сторінку.</p>';
        throw e;
      }
    }
    st.scLoading = false;
    if (root._sk !== st) { sc.dispose(); return; }
    st.sc = sc; st.scKind = kind; st.gridSig = null; st.legalSig = null; st.curSig = null;
    paint(root, st.ctx);
  }

  function switchView(root, st) {
    const to = st.scKind === '3d' ? '2d' : '3d';
    store.set('skb.view', to);
    st.no3d = false;
    if (st.sc) { try { st.sc.dispose(); } catch (e) { /* уже прибрано */ } }
    st.sc = null; st.el.field.textContent = '';
    hideTip(st);
    ensureScene(root, st);
  }

  /// Що підсвітити на полі й куди може ходити курсор для вибраної карти.
  function legalFor(st, v) {
    const s = selCard(st, v);
    if (!s) return null;
    const me = v.me, list = (me.legal || {})[s.card.id] || [];
    if (!list.length) return { kind: null, targets: [] };
    const c = s.card;
    if (c.k === 'path') {
      const T = M.K.pathTargets(list, c.s, s.r);
      return { kind: 'place', cells: T.cells, alt: T.alt, ghost: T.ghost,
        targets: T.cells.map(([x, y]) => ({ t: 'cell', x, y, alt: false })).concat(T.alt.map(([x, y]) => ({ t: 'cell', x, y, alt: true }))).sort((a, b) => a.y - b.y || a.x - b.x) };
    }
    if (c.k === 'rockfall') return { kind: 'rockfall', cells: list, targets: list.map(([x, y]) => ({ t: 'cell', x, y })) };
    if (c.k === 'map') return { kind: 'goal', goals: list, targets: list.map((i) => ({ t: 'goal', i, x: v.goals[i].x, y: v.goals[i].y })) };
    // Злам і ремонт — цілі на чипах гравців (x — місце в ряду, щоб стрілки ходили вздовж).
    const seats = [...M.K.seatTargets(c, list).keys()];
    const idx = (s2) => v.players.findIndex((p) => p.seat === s2);
    return { kind: null, seats, targets: seats.map((s2) => ({ t: 'seat', s: s2, x: idx(s2), y: 0 })) };
  }

  const busy = (st) => st.running || st.queue.length > 0;

  function syncScene(root, st) {
    const v = st.view, sc = st.sc;
    if (!sc || !v || !v.grid) return;
    const gs = JSON.stringify(v.grid);
    if (gs !== st.gridSig) { sc.setGrid(v.grid); st.gridSig = gs; }
    if (busy(st)) return;   // анімації ще йдуть: сцена сама в стані «після події», вид — коли черга спорожніє
    sc.setState(v);
    const L = legalFor(st, v);
    const arg = L && L.kind ? { kind: L.kind, cells: L.cells || [], alt: L.alt || [], goals: L.goals || [], ghost: L.ghost || null } : { kind: null };
    const sig = JSON.stringify(arg);
    if (sig !== st.legalSig) { st.legalSig = sig; sc.setLegal(arg); }
    const c = st.cur;
    const cs = c && c.t !== 'seat' ? c.x + ',' + c.y : '';
    if (cs !== st.curSig) { st.curSig = cs; if (c && c.t !== 'seat') sc.setCursor(c.x, c.y); else sc.setCursor(null); }
  }

  // ─── Вибір і гра карти ──────────────────────────────────────────────────
  function select(root, st, id) {
    const v = st.view, me = v && v.me;
    if (!me || me.must !== 'play') return;
    const card = (me.hand || []).find((c) => c.id === id);
    if (!card) return;
    if (st.sel && st.sel.id === id) {
      // Повторний тап: хід — перевернути, решта — скасувати.
      if (card.k === 'path' && !M.K.isSym(card.s)) { rotate(root, st); return; }
      clearSel(st);
    } else {
      st.sel = { id, r: 0 };
      st.cur = null; st.arm = null; st.fixPick = null;
      const tip = tipFor(st, v);
      if (tip && tip.card.id === id) {
        if (tip.kind === 'cell') { st.sel.r = tip.r; st.cur = { t: 'cell', x: tip.x, y: tip.y }; }
        else if (tip.kind === 'goal') st.cur = { t: 'goal', i: tip.goal, x: v.goals[tip.goal].x, y: v.goals[tip.goal].y };
        else if (tip.kind === 'seat') st.cur = { t: 'seat', s: tip.seat, tool: tip.tool };
      }
    }
    hideTip(st);
    paint(root, st.ctx);
  }
  function clearSel(st) { st.sel = null; st.cur = null; st.arm = null; st.fixPick = null; hideTip(st); }
  function rotate(root, st) {
    const s = selCard(st, st.view);
    if (!s || s.card.k !== 'path' || M.K.isSym(s.card.s)) return false;
    st.sel.r = 1 - st.sel.r;
    st.arm = null;
    paint(root, st.ctx);
    return true;
  }

  function play(root, st, target) {
    const v = st.view, s = selCard(st, v), ctx = st.ctx;
    if (!s || !ctx || !target) return false;
    const c = s.card, me = v.me, list = (me.legal || {})[c.id] || [];
    let p = null;
    if (c.k === 'path' && target.t === 'cell') {
      const r = M.K.rotFor(list, c.s, s.r, target.x, target.y);
      if (r == null) return false;
      p = { card: c.id, x: target.x, y: target.y, r };
    } else if (c.k === 'rockfall' && target.t === 'cell') {
      if (!list.some(([x, y]) => x === target.x && y === target.y)) return false;
      p = { card: c.id, x: target.x, y: target.y };
    } else if (c.k === 'map' && target.t === 'goal') {
      if (!list.includes(target.i)) return false;
      p = { card: c.id, goal: target.i };
    } else if (c.k === 'break' && target.t === 'seat') {
      if (!list.includes(target.s)) return false;
      p = { card: c.id, seat: target.s };
    } else if (c.k === 'fix' && target.t === 'seat') {
      const tools = M.K.seatTargets(c, list).get(target.s) || [];
      if (!tools.length) return false;
      const tool = target.tool && tools.includes(target.tool) ? target.tool : tools.length === 1 ? tools[0] : null;
      if (!tool) { st.fixPick = { seat: target.s, tools }; paint(root, ctx); return true; }
      p = { card: c.id, seat: target.s, tool };
    }
    if (!p) return false;
    clearSel(st);
    ctx.act('play', p);
    paint(root, ctx);
    return true;
  }

  function pass(root, st) {
    const s = selCard(st, st.view);
    if (!s || !st.ctx) return false;
    clearSel(st);
    st.ctx.act('pass', { card: s.card.id });
    paint(root, st.ctx);
    return true;
  }

  /// «💡 Глек радить»: ввімкнено — показуємо (типово — перші два раунди, далі як вирішив гравець).
  function tipOn(v) {
    const s = store.get('skb.tip');
    return s == null ? !!(v && v.round && v.round <= 2) : s === '1';
  }
  function tipFor(st, v) {
    const me = v && v.me;
    if (!me || me.must !== 'play' || !me.tip || !(st.tipNow || tipOn(v))) return null;
    return M.K.tipTarget(me.tip, me.hand);
  }

  // ─── Клік по полю й підказка ────────────────────────────────────────────
  function onPick(root, kind, a, b) {
    const st = root._sk;
    if (!st || !st.view) return;
    const L = legalFor(st, st.view);
    if (!L || !L.targets.length) return;
    const t = kind === 'goal' ? L.targets.find((x) => x.t === 'goal' && x.i === a) : L.targets.find((x) => x.t === 'cell' && x.x === a && x.y === b);
    if (!t) return;
    // На дотику перший тиц — показати (привид і підказку), другий туди ж — зіграти. Завал — завжди перепитує.
    const need = coarse() || L.kind === 'rockfall';
    const same = st.arm && st.arm.t === t.t && st.arm.x === t.x && st.arm.y === t.y && st.arm.i === t.i;
    if (need && !same && (coarse() || !st.hover)) {
      st.arm = t; st.cur = t;
      syncScene(root, st);
      showTip(st, t, true);
      return;
    }
    st.arm = null;
    hideTip(st);
    play(root, st, t);
  }

  function onHover(root, kind, a, b) {
    const st = root._sk;
    if (!st) return;
    if (kind == null) { st.hover = null; if (!st.arm) hideTip(st); return; }
    const t = kind === 'goal' ? { t: 'goal', i: a } : { t: 'cell', x: a, y: b };
    st.hover = t;
    if (st.arm && !(st.arm.t === t.t && st.arm.x === t.x && st.arm.y === t.y && st.arm.i === t.i)) st.arm = null;
    showTip(st, t, !!st.arm);
  }

  function tipText(st, t) {
    const v = st.view, s = selCard(st, v);
    if (!s) return '';
    const c = s.card, C = M.C;
    if (c.k === 'path' && t.t === 'cell') {
      const r = M.K.rotFor((v.me.legal || {})[c.id], c.s, s.r, t.x, t.y);
      if (r == null) return '';
      return '⛏ Сюди ляже «' + esc(C.SHAPE_NAME[c.s]) + '»' + (r !== s.r ? ' — перевернутим ⟲' : '');
    }
    if (c.k === 'rockfall' && t.t === 'cell') {
      const b = (v.board || []).find((q) => q.x === t.x && q.y === t.y);
      return '🪨 Засипати цей хід?' + (b ? ' <span class="muted">(' + esc(C.SHAPE_NAME[b.s] || '') + (b.by != null ? ', клав ' + esc(nickOf(v, b.by)) : '') + ')</span>' : '');
    }
    if (c.k === 'map' && t.t === 'goal') {
      const pk = (v.me.peeks || {})[t.i];
      return '🗺 Зазирнути під ' + C.GOAL_PLACE[t.i] + 'у' + (pk ? ' <span class="muted">(ти вже бачив: ' + (pk === 'gold' ? 'скарб' : 'каміння') + ')</span>' : '');
    }
    return '';
  }

  function showTip(st, t, armed) {
    const tip = st.el && st.el.tip;
    if (!tip || !st.sc) return;
    const html = tipText(st, t);
    if (!html) { hideTip(st); return; }
    const s = selCard(st, st.view);
    tip.innerHTML = emo(html) + (armed ? '<br><b class="skb-gold">Тицни ще раз — ' + (s && s.card.k === 'rockfall' ? 'засипати' : s && s.card.k === 'map' ? 'зазирнути' : 'покласти') + '</b>' : '');
    tip.hidden = false;
    const p = t.t === 'goal' ? st.sc.screenPos('goal', t.i) : st.sc.screenPos('cell', t.x, t.y);
    if (!p) return;
    const W = st.el.fieldwrap.clientWidth;
    const w = tip.offsetWidth;
    const x = Math.max(w / 2 + 6, Math.min(W - w / 2 - 6, p.x));
    const below = p.y < tip.offsetHeight + 40;
    tip.style.left = Math.round(x) + 'px';
    tip.style.top = Math.round(below ? p.y + 30 : p.y - 28) + 'px';
    tip.classList.toggle('below', below);
  }
  function hideTip(st) { if (st.el && st.el.tip) st.el.tip.hidden = true; }

  // ─── Малювання ──────────────────────────────────────────────────────────
  function paint(root, ctx) {
    const st = state(root);
    st.ctx = ctx;
    st.view = ctx && ctx.view && ctx.view.grid ? ctx.view : null;
    if (!M) {
      if (!root.firstChild) root.innerHTML = '<p class="skb-wait muted">Копаємо до скарбу…</p>';
      load().then(() => { if (root._sk === st) paint(root, st.ctx); }, (e) => { root.innerHTML = '<p class="skb-err">Не вдалося завантажити гру. Онови сторінку.</p>'; console.warn(e); });
      return;
    }
    if (!st.built) build(root, st);
    const v = st.view;
    if (!v) { setHtml(st.el.say, '<b>Чекаємо на початок…</b>'); return; }
    const me = v.me || null;
    if (!selCard(st, v)) { st.sel = null; st.cur = null; st.arm = null; st.fixPick = null; }
    if (st.turnKey !== v.turn + '|' + v.round) { st.turnKey = v.turn + '|' + v.round; st.tipNow = false; }
    const w = whatNow(v, me, st);
    setHtml(st.el.say, '<div class="skb-say-t">' + w.l + '</div>' + (st.sel ? '<button type="button" class="skb-x" data-a="cancel">✕ Скасувати</button>' : ''));
    st.el.say.hidden = !!v.over;
    st.el.say.classList.toggle('mine', !!(me && me.must === 'play'));
    renderPlayers(st, v, me);
    renderHand(st, v, me);
    renderActs(st, v, me);
    renderDlg(st, v, me);
    renderSum(st, v, me);
    renderLog(st, v);
    renderBadge(st, v, me);
    st.el.view.textContent = st.scKind === '3d' ? '🗺 Пласка' : '🧊 3D';
    st.el.view.title = st.scKind === '3d' ? 'Показати пласку мапу (легша для слабких пристроїв)' : (st.no3d ? '3D не вдалося — спробувати ще' : 'Показати об\'ємний розріз');
    queueEvents(root, st, v);
    if (!st.sc) ensureScene(root, st);
    else syncScene(root, st);
    nudge(root, st);
  }

  // ─── Гравці ─────────────────────────────────────────────────────────────
  const susKey = (st, v) => 'skb.sus.' + ((st.ctx && st.ctx.room && st.ctx.room.id) || 'x') + '.' + (v.round || 0);

  function renderPlayers(st, v, me) {
    const C = M.C;
    const L = legalFor(st, v);
    const tgt = new Set((L && L.seats) || []);
    const sus = sess.get(susKey(st, v)) || {};
    const showRole = v.phase === 'round' || v.phase === 'over';
    const ARC = 2 * Math.PI * 15;
    const curSeat = st.cur && st.cur.t === 'seat' ? st.cur.s : null;
    const tip = tipFor(st, v);
    const h = v.players.map((p) => {
      const mine = me && me.seat === p.seat;
      const turn = v.turn === p.seat && v.phase === 'play';
      const isT = tgt.has(p.seat);
      const tag = isT ? 'button' : 'div';
      const tools = C.TOOLS.map((t) => '<i class="skb-tl' + ((p.broken || []).includes(t) ? ' off' : '') + '" title="' + esc(C.TOOL_NAME[t]) + ((p.broken || []).includes(t) ? ' — зламано' : ' ціла') + '">' + C.TOOL_ICON[t] + '</i>').join('');
      const role = showRole && p.role ? '<span class="skb-role ' + p.role + '" title="' + esc(C.ROLE_NAME[p.role]) + '">' + C.ROLE_ICON[p.role] + '</span>' : '';
      const sm = sus[p.seat] === 1 ? '<span class="skb-sus" title="Твоя позначка: підозрюю">🤔</span>' : sus[p.seat] === 2 ? '<span class="skb-sus" title="Твоя позначка: свій">✅</span>' : '';
      const ally = me && me.allies && me.allies.includes(p.seat) && v.phase === 'play' ? '<span class="skb-sus" title="Свій кріт">🕳</span>' : '';
      const ready = v.phase === 'round' && ((v.ready || []).includes(p.seat) || p.bot || p.away) ? '<span class="skb-ok" title="Готовий">✔</span>' : '';
      const tipM = tip && tip.kind === 'seat' && tip.seat === p.seat && st.sel && st.sel.id === tip.card.id ? '<span class="skb-tipm" title="Глек радить">💡</span>' : '';
      const fin = v.over && v.over.winners && v.over.winners.includes(p.seat) ? ' win' : '';
      return '<' + tag + (isT ? ' type="button" data-a="tgt" data-seat="' + p.seat + '"' : '') + ' class="skb-pl' + (turn ? ' turn' : '') + (mine ? ' me' : '') + (isT ? ' tgt' : L && L.seats ? ' dim' : '')
        + (curSeat === p.seat ? ' cur' : '') + fin + '" data-seat="' + p.seat + '" style="--pc:' + colorOf(v, p.seat) + '">'
        + '<span class="skb-av"><svg viewBox="0 0 36 36" aria-hidden="true"><circle class="bg" cx="18" cy="18" r="15"/>'
        + (turn && v.deadline ? '<circle class="fg" cx="18" cy="18" r="15" transform="rotate(-90 18 18)" stroke-dasharray="' + ARC.toFixed(1) + '"/>' : '')
        + '</svg><b>' + (p.bot ? '🤖' : esc(String(p.nick || '?').trim().charAt(0).toUpperCase())) + '</b>' + role + '</span>'
        + '<span class="skb-pi"><span class="skb-pn">' + esc(p.nick) + (!p.bot && p.away ? ' <span title="Вийшов або проспав — за нього копає Глек">🤖</span>' : '') + (mine ? ' <em>(ти)</em>' : '') + '</span>'
        + '<span class="skb-ps"><span class="skb-tls">' + tools + '</span><b title="Злитки за партію">🪙' + (p.gold || 0) + '</b><span class="skb-hn" title="Карт у руці">🂠' + (p.hand || 0) + '</span></span></span>'
        + sm + ally + ready + tipM + (turn && v.deadline ? '<span class="skb-sec" data-sec></span>' : '')
        + '</' + tag + '>';
    }).join('');
    const head = v.phase ? '<div class="skb-meta"><b>Раунд ' + (v.round || 1) + '/' + (v.rounds || 3) + '</b><span title="Кротів у колоді ролей (може бути й на одного менше)">🕳 до ' + ((v.pack || {}).moles || 0) + '</span>'
      + '<span title="Карт у колоді">🂠 ' + (v.deck || 0) + '</span></div>' : '';
    setHtml(st.el.players, head + '<div class="skb-chips n' + v.players.length + '">' + h + '</div>');
    tickArcs(st);
  }

  function cycleSus(root, seat) {
    const st = root._sk, v = st && st.view;
    if (!v || !v.phase || (v.me && v.me.seat === seat)) return;
    const k = susKey(st, v), m = sess.get(k) || {};
    m[seat] = ((m[seat] || 0) + 1) % 3;
    if (!m[seat]) delete m[seat];
    sess.set(k, m);
    st.noClick = Date.now();
    paint(root, st.ctx);
  }

  // ─── Рука ───────────────────────────────────────────────────────────────
  /// Міні-малюнок тунелю за маскою (SVG 40×40).
  function tunnelSvg(m, c, cls) {
    const C = M.C;
    let s = '<svg class="' + (cls || 'skb-mini') + '" viewBox="0 0 40 40" aria-hidden="true"><rect width="40" height="40" rx="4" class="dirt"/>';
    const seg = { N: [20, 20, 20, 0], E: [20, 20, 40, 20], S: [20, 20, 20, 40], W: [20, 20, 0, 20] };
    for (const d of C.DIRS) {
      if (!(m & C.BIT[d])) continue;
      let [x1, y1, x2, y2] = seg[d];
      if (!c) { x1 = 20 + (x2 - 20) * 0.45; y1 = 20 + (y2 - 20) * 0.45; }
      s += '<line x1="' + x1 + '" y1="' + y1 + '" x2="' + x2 + '" y2="' + y2 + '" class="tun"/>';
    }
    if (c) s += '<rect x="13.5" y="13.5" width="13" height="13" class="tunf"/><circle cx="20" cy="19" r="2.6" class="lamp"/>';
    else s += '<circle cx="20" cy="20" r="6.5" class="rock"/>';
    return s + '</svg>';
  }

  function renderHand(st, v, me) {
    if (st.drag && st.drag.ghost) return;   // тягнуть карту: DOM руки не чіпаємо (дотик тримає саме цей елемент)
    if (!me || v.phase !== 'play') { setHtml(st.el.hand, ''); return; }
    const C = M.C, K = M.K;
    const myTurn = me.must === 'play';
    const tip = tipFor(st, v);
    const hand = me.hand || [];
    if (!hand.length) { setHtml(st.el.hand, '<p class="skb-note">Руки порожні — далі копають інші</p>'); return; }
    const n = hand.length;
    const h = hand.map((c, i) => {
      const inf = K.cardInfo(c);
      const list = myTurn ? (me.legal || {})[c.id] || [] : null;
      const off = myTurn && !list.length;
      const on = st.sel && st.sel.id === c.id;
      const r = on ? st.sel.r : 0;
      const ang = n > 1 ? (i - (n - 1) / 2) * 3.2 : 0;
      let face;
      if (c.k === 'path') face = tunnelSvg(K.maskOf(c.s, r), C.SHAPES[c.s] ? C.SHAPES[c.s].c : true) + '<span class="skb-cn">' + esc(inf.name) + '</span>';
      else face = '<span class="skb-ci' + (c.k === 'break' ? ' brk' : '') + '">' + inf.icon + '</span><span class="skb-cn">' + esc(inf.name) + '</span>';
      const why = off ? (me.why || {})[c.id] || 'Нікуди не зіграєш' : '';
      return '<button type="button" class="skb-card k-' + c.k + (on ? ' on' : '') + (off ? ' off' : '') + (c.k === 'path' && !C.SHAPES[c.s].c ? ' dead' : '') + '" data-a="card" data-id="' + c.id + '"'
        + ' style="--ang:' + ang.toFixed(1) + 'deg" title="' + esc(inf.name + ' — ' + (why || inf.hint)) + '"' + (myTurn ? '' : ' aria-disabled="true"') + '>'
        + '<span class="skb-ck">' + (i + 1) + '</span>' + face
        + (why ? '<span class="skb-cw">' + esc(why) + '</span>' : '')
        + (tip && tip.card.id === c.id ? '<span class="skb-tipm" title="Глек радить цю карту">💡</span>' : '')
        + (on && r ? '<span class="skb-rot">⟲</span>' : '')
        + '</button>';
    }).join('');
    setHtml(st.el.hand, '<div class="skb-fan">' + h + '</div>');
  }

  function renderActs(st, v, me) {
    if (!me || v.phase !== 'play') { setHtml(st.el.acts, ''); return; }
    const s = selCard(st, v);
    const myTurn = me.must === 'play';
    let h = '';
    if (myTurn && s) {
      const c = s.card;
      if (c.k === 'path' && !M.K.isSym(c.s)) h += '<button type="button" class="skb-btn" data-a="rot" title="Перевернути карту (R)">⟲ 180°</button>';
      h += '<button type="button" class="skb-btn warn" data-a="pass" title="Скинути цю карту сорочкою догори й пропустити хід (Del)">🗑 Скинути й пропустити</button>';
      h += '<button type="button" class="skb-btn ghost" data-a="cancel" title="Esc">✕</button>';
    } else if (myTurn) {
      h += '<span class="skb-note">Тицни карту в руці' + (coarse() ? '' : ' (1–' + (me.hand || []).length + ')') + '</span>';
    } else {
      h += '<span class="skb-note">Твоя рука · ' + (me.hand || []).length + ' ' + plural((me.hand || []).length, 'карта', 'карти', 'карт') + '</span>';
    }
    if (myTurn && me.tip) {
      const on = st.tipNow || tipOn(v);
      h += '<button type="button" class="skb-btn tipb' + (on ? ' on' : '') + '" data-a="tip" aria-pressed="' + on + '" title="Що зробив би Глек на твоєму місці">💡 Глек радить</button>';
    }
    setHtml(st.el.acts, h);
  }

  function renderDlg(st, v, me) {
    const C = M.C;
    let h = '';
    if (st.fixPick && selCard(st, v)) {
      h = '<h3>Полагодити ' + who(v, st.fixPick.seat) + ':</h3><div class="skb-row">'
        + st.fixPick.tools.map((t) => '<button type="button" class="skb-btn big" data-a="fixtool" data-tool="' + t + '">' + C.TOOL_ICON[t] + ' ' + C.TOOL_ACC[t] + '</button>').join('')
        + '</div><button type="button" class="skb-link" data-a="fixno">не зараз</button>';
    }
    setHtml(st.el.dlg, h ? '<div class="skb-dlgin">' + h + '</div>' : '');
    st.el.dlg.hidden = !h;
  }

  // ─── Роль: розкриття на старті раунду й «👁 Моя роль» ───────────────────
  function renderBadge(st, v, me) {
    const b = st.el.rolebadge;
    const show = !!(me && me.role && v.phase === 'play' && !st.reveal);
    b.hidden = !show;
    if (!show) return;
    const C = M.C;
    const allies = me.allies && me.allies.length ? '<small>Свої: ' + me.allies.map((s) => esc(nickOf(v, s))).join(', ') + '</small>' : '';
    setHtml(b, st.roleHold
      ? '<span class="skb-rb-open ' + me.role + '">' + C.ROLE_ICON[me.role] + ' ' + C.ROLE_NAME[me.role] + allies + '</span>'
      : '<span class="skb-rb-shut" title="Натисни й тримай — побачиш свою роль">👁 Моя роль</span>');
    b.classList.toggle('open', st.roleHold);
  }

  function showReveal(root, st) {
    const v = st.view, me = v && v.me;
    if (!me || !me.role) return;
    const C = M.C;
    const allies = me.allies && me.allies.length ? '<p class="skb-allies">Свої: ' + me.allies.map((s) => who(v, s)).join(', ') + '</p>' : '';
    st.reveal = { at: Date.now() };
    setHtml(st.el.reveal, '<div class="skb-rv-card ' + me.role + (reduced() ? ' still' : '') + '" data-a="revealx"><div class="skb-rv-in">'
      + '<div class="skb-rv-back">🏺</div>'
      + '<div class="skb-rv-face"><div class="skb-rv-ic">' + C.ROLE_ICON[me.role] + '</div><h2>' + esc(M.K.roleLine(me.role)) + '</h2>'
      + '<p>' + esc(C.ROLE_HINT[me.role]) + '</p>' + allies + '<p class="skb-rv-n">Раунд ' + v.round + ' з ' + v.rounds + ' · кротів у колоді — до ' + ((v.pack || {}).moles || 0) + '</p></div>'
      + '</div></div>');
    st.el.reveal.hidden = false;
    st.el.reveal.classList.remove('shrink');
    clearTimeout(st.revealT);
    st.revealT = setTimeout(() => hideReveal(root, st), 2500);
    if (me.role === 'mole') sfx(st, 'mole'); else sfx(st, 'turn');
    renderBadge(st, v, me);
  }
  function hideReveal(root, st) {
    clearTimeout(st.revealT);
    if (!st.reveal) return;
    st.el.reveal.classList.add('shrink');
    st.revealT = setTimeout(() => { st.el.reveal.hidden = true; st.el.reveal.classList.remove('shrink'); st.reveal = null; if (st.view) renderBadge(st, st.view, st.view.me); }, reduced() ? 0 : 420);
  }

  // ─── Підсумки ───────────────────────────────────────────────────────────
  function renderSum(st, v, me) {
    const el = st.el.sum;
    if (v.over) { setHtml(el, overHtml(st, v, me)); el.hidden = false; return; }
    if (v.phase === 'round' && v.roundEnd) { setHtml(el, roundHtml(st, v, me)); el.hidden = false; tickNext(st); return; }
    setHtml(el, ''); el.hidden = true;
  }

  function goalsLine(re) {
    const C = M.C;
    return (re.goals || []).map((g) => C.GOAL_PLACE[g.i] + ' — ' + (g.kind === 'gold' ? '💰 скарб' : '🪨 каміння')).join(' · ');
  }

  function roundHtml(st, v, me) {
    const C = M.C, K = M.K, re = v.roundEnd;
    const rows = K.goldRows(re, v.order);
    const side = re.side;
    let h = '<div class="skb-sumh"><h3>' + (side === 'diggers' ? '💰' : side === 'moles' ? '🕳' : '🪨') + ' Раунд ' + re.n + ': ' + esc(C.SIDE_NAME[side] || '') + '</h3></div>';
    const bits = [];
    if (side === 'diggers' && re.finisher != null) bits.push('скриню відкрив ' + who(v, re.finisher) + (re.roles && re.roles[re.finisher] === 'mole' ? ' <span class="muted">(сам кріт!)</span>' : ''));
    const moles = Object.keys(re.roles || {}).filter((s) => re.roles[s] === 'mole').map(Number);
    bits.push(moles.length ? 'кроти: ' + moles.map((s) => who(v, s)).join(', ') : 'кротів не було');
    bits.push('набік лягла роль: ' + C.ROLE_ICON[re.aside] + ' ' + esc(C.ROLE_NAME[re.aside] || '?'));
    h += '<p class="skb-sump">' + bits.join(' · ') + '</p><p class="skb-sump muted">' + esc(goalsLine(re)) + '</p>';
    h += '<div class="skb-tbl"><table class="skb-stats"><thead><tr><th></th><th>роль</th><th>цей раунд</th><th>усього</th></tr></thead><tbody>'
      + rows.slice().sort((a, b) => b.n - a.n || (b.total || 0) - (a.total || 0)).map((r) => '<tr' + (me && r.seat === me.seat ? ' class="me"' : '') + '><td>' + who(v, r.seat) + '</td><td>' + (r.role ? C.ROLE_ICON[r.role] + ' ' + esc(C.ROLE_NAME[r.role]) : '') + '</td>'
        + '<td>' + (r.n ? '<b class="skb-gold">+' + r.n + ' 🪙</b>' + (r.cards && r.cards.length > 1 ? ' <small class="muted">(' + r.cards.join('+') + ')</small>' : '') : '<span class="muted">—</span>') + '</td>'
        + '<td><b>' + (r.total != null ? r.total : '') + '</b></td></tr>').join('')
      + '</tbody></table></div>';
    const last = v.round >= v.rounds;
    const ready = (v.ready || []).length;
    h += '<div class="skb-next"><span class="skb-cd" data-cd></span>'
      + (ready ? '<span class="muted small">✔ готові: ' + (v.ready || []).map((s) => esc(nickOf(v, s))).join(', ') + '</span>' : '')
      + (me && me.must === 'next' ? '<button type="button" class="skb-btn big primary" data-a="next">' + (last ? 'Далі ▶' : 'Далі ▶ раунд ' + (v.round + 1)) + '</button>'
        : me ? '<span class="skb-ok">✔ ти готовий — чекаємо решту</span>' : '') + '</div>';
    return h;
  }

  function overHtml(st, v, me) {
    const C = M.C, K = M.K, o = v.over, s = o.stats || {};
    const gold = o.gold || [];
    const order = v.order || v.players.map((p) => p.seat);
    const idx = (seat) => order.indexOf(seat);
    const rank = order.slice().sort((a, b) => (gold[idx(b)] || 0) - (gold[idx(a)] || 0));
    const w = o.winners || [];
    const again = st.ctx && st.ctx.mine ? '<button type="button" class="skb-btn primary skb-again" data-a="again">🔁 Ще раз</button>' : '';
    let h = '<div class="skb-sumh"><h3>' + (o.reason === 'left' ? '🏁 Партію скінчено <span class="muted">(усі вийшли)</span>'
      : w.length > 1 ? '🤝 Скарб поділили: ' + w.map((x) => who(v, x)).join(', ') + ' — по ' + K.goldText(gold[idx(w[0])] || 0)
        : w.length ? '🏆 Найбагатший — ' + who(v, w[0]) + ': ' + K.goldText(gold[idx(w[0])] || 0) : '🏁 Партію скінчено') + '</h3>' + again + '</div>';
    h += '<div class="skb-sumgrid"><ol class="skb-rank">' + rank.map((x) => '<li' + (w.includes(x) ? ' class="win"' : '') + '>' + who(v, x) + ' <b class="g">' + (gold[idx(x)] || 0) + ' 🪙</b></li>').join('') + '</ol>';
    // Стовпчики злитків за раундами.
    const tl = s.timeline || (o.rounds || []).map((r) => r.totals);
    if (tl && tl.length) {
      const B = K.roundBars(tl, order.length);
      const W = 26 * order.length + 30, H = 150, bw = 16, base = H - 24;
      let svg = '<svg class="skb-chart" viewBox="0 0 ' + W + ' ' + H + '" role="img" aria-label="Злитки за раундами">';
      for (let g = 0; g <= B.max; g += Math.max(1, Math.ceil(B.max / 4))) { const y = base - g / B.max * (base - 10); svg += '<line x1="20" x2="' + (W - 4) + '" y1="' + y.toFixed(1) + '" y2="' + y.toFixed(1) + '" class="grid"/><text x="15" y="' + (y + 3).toFixed(1) + '" class="ax" text-anchor="end">' + g + '</text>'; }
      B.bars.forEach((parts, i) => {
        let y = base;
        const x = 26 + i * 26, col = colorOf(v, order[i]);
        parts.forEach((n, r) => {
          if (!n) return;
          const hh = n / B.max * (base - 10);
          y -= hh;
          svg += '<rect x="' + x + '" y="' + y.toFixed(1) + '" width="' + bw + '" height="' + hh.toFixed(1) + '" style="fill:' + col + ';opacity:' + (0.45 + r * 0.27).toFixed(2) + '"><title>' + esc(nickOf(v, order[i])) + ': раунд ' + (r + 1) + ' +' + n + '</title></rect>';
        });
        svg += '<text x="' + (x + bw / 2) + '" y="' + (H - 9) + '" class="ax" text-anchor="middle">' + esc(String(nickOf(v, order[i])).charAt(0)) + '</text>';
      });
      svg += '</svg>';
      h += '<div><div class="skb-trl">Злитки за раундами <span class="muted">(світліше — раніше)</span></div>' + svg + '</div>';
    }
    const T = K.titles(s);
    if (T.length) h += '<div><div class="skb-trl">Звання</div><ul class="skb-titles">' + T.map((t) => '<li>' + t.icon + ' <b>' + esc(t.name) + '</b> — ' + who(v, t.seat) + ' <span class="muted">(' + esc(t.why) + ')</span></li>').join('') + '</ul>'
      + (s.turns ? '<p class="muted small">' + s.turns + ' ' + plural(s.turns, 'хід', 'ходи', 'ходів') + (s.ms ? ' · ' + Math.round(s.ms / 60000) + ' хв' : '') + '</p>' : '') + '</div>';
    h += '</div>';
    // Ролі за раундами.
    const R = o.rounds || [];
    if (R.length) {
      h += '<div class="skb-tbl"><table class="skb-stats"><thead><tr><th></th>' + R.map((r) => '<th>Раунд ' + r.n + '<br><small>' + esc(C.SIDE_NAME[r.side] || '') + '</small></th>').join('') + '</tr></thead><tbody>'
        + order.map((x) => '<tr' + (me && x === me.seat ? ' class="me"' : '') + '><td>' + who(v, x) + '</td>' + R.map((r) => {
          const role = r.roles && r.roles[x];
          const won = role && ((role === 'digger' && r.side === 'diggers') || (role === 'mole' && r.side === 'moles'));
          const g = (r.gold || []).find((q) => q.seat === x);
          return '<td class="' + (won ? 'won' : '') + '">' + (role ? C.ROLE_ICON[role] : '') + (g && g.n ? ' <small class="skb-gold">+' + g.n + '</small>' : '') + '</td>';
        }).join('') + '</tr>').join('') + '</tbody></table></div>';
    }
    return h;
  }

  function tickNext(st) {
    const v = st.view;
    const el = st.el && st.el.sum && st.el.sum.querySelector('[data-cd]');
    if (!el || !v || v.phase !== 'round') return;
    const left = v.nextAt ? Math.max(0, Date.parse(v.nextAt) - Date.now()) : null;
    const t = left == null ? '' : left > 0 ? (v.round >= v.rounds ? 'Підсумок партії' : 'Раунд ' + (v.round + 1)) + ' за ' + Math.ceil(left / 1000) + ' с' : 'Починаємо…';
    if (el.textContent !== t) el.textContent = t;
  }

  // ─── Журнал ─────────────────────────────────────────────────────────────
  function renderLog(st, v) {
    const lines = (v.log || []).slice(-6);
    if (!lines.length) { setHtml(st.el.log, ''); return; }
    setHtml(st.el.log, '<button type="button" class="skb-logh" data-a="log" aria-expanded="' + st.logOpen + '">📜 Що було ' + (st.logOpen ? '▾' : '▸') + '</button>'
      + (st.logOpen ? '<ol>' + lines.map((l) => '<li>' + esc(l) + '</li>').join('') + '</ol>' : '<p class="skb-last">' + esc(lines[lines.length - 1]) + '</p>'));
  }

  // ─── Таймер, Глеки, «час минув», «далі» ────────────────────────────────
  function tickArcs(st) {
    const v = st.view;
    if (!v || !st.el) return;
    const left = v.deadline ? Math.max(0, Date.parse(v.deadline) - Date.now()) : null;
    const total = v.timerMs || 60000;
    const L = 2 * Math.PI * 15;
    for (const fg of st.el.players.querySelectorAll('.skb-av .fg')) {
      fg.style.strokeDashoffset = left == null ? 0 : (L * (1 - Math.min(1, left / total))).toFixed(1);
      fg.classList.toggle('late', left != null && left < 10000);
    }
    for (const s of st.el.players.querySelectorAll('[data-sec]')) {
      const t = left == null ? '' : Math.ceil(left / 1000) + ' с';
      if (s.textContent !== t) s.textContent = t;
    }
  }

  function myRank(v, ctx) {
    let rank = 0;
    for (const p of v.players || []) if (p.seat < ctx.seat && !p.bot && !p.away) rank++;
    return rank;
  }

  function tick(root) {
    const st = root._sk;
    if (!st || !st.view) return;
    tickArcs(st);
    tickNext(st);
    tickSound(st, st.view);
    const v = st.view, ctx = st.ctx;
    if (!ctx || !ctx.mine || !ctx.playing || !ctx.room || v.over || !HGames.send) return;
    const now = Date.now();
    // Пауза між раундами: через nextAt + 0,5 с — «далі» сам.
    if (v.phase === 'round' && v.nextAt) {
      const due = Date.parse(v.nextAt) + 500 + myRank(v, ctx) * 300;
      if (now >= due && !(st.nextKey === v.nextAt && now - st.nextAt0 < 4000)) { st.nextKey = v.nextAt; st.nextAt0 = now; HGames.send('Act', ctx.room.id, 'next', {}); }
      return;
    }
    // Строк минув — будь-хто за столом штовхає сервер (той звірить із годинником і спасує за того, хто тягне).
    if (!v.deadline || v.phase !== 'play') return;
    const due = Date.parse(v.deadline) + 500 + myRank(v, ctx) * 700;
    if (now < due) return;
    if (st.toKey === v.deadline && now - st.toAt < 4000) return;
    st.toKey = v.deadline; st.toAt = now;
    HGames.send('Act', ctx.room.id, 'timeout', {});
  }

  /// Глеки ходять на штовхан клієнта людини: перша людина за столом — першою, решта — із запасом.
  function nudge(root, st) {
    const v = st.view || {}, ctx = st.ctx;
    clearTimeout(st.nt);
    st.nt = 0;
    if (!ctx || !ctx.playing || !ctx.mine || typeof v.botIn !== 'number' || !HGames.send || !ctx.room) return;
    const fire = () => {
      if (root._sk !== st || !st.ctx || !st.ctx.room) return;
      HGames.send('Act', st.ctx.room.id, 'bot', null);
      st.nt = setTimeout(fire, 1500);
    };
    st.nt = setTimeout(fire, Math.max(0, v.botIn) + 60 + myRank(v, ctx) * 700);
  }

  // ─── Події: анімації по черзі ───────────────────────────────────────────
  function queueEvents(root, st, v) {
    if (typeof v.seq !== 'number') return;
    // Нова партія («Ще раз»): seq не росте або over → play. Без анімацій старого.
    const fresh0 = st.seq != null && (v.seq < st.seq || (st.seqPhase === 'over' && v.phase !== 'over'));
    st.seqPhase = v.phase;
    if (fresh0) { st.seq = v.seq; st.queue.length = 0; return; }
    if (st.seq == null) { st.seq = v.seq; return; }   // перший mount — нічого не анімуємо
    const fresh = (v.events || []).filter((e) => e.seq > st.seq).sort((a, b) => a.seq - b.seq);
    st.seq = Math.max(st.seq, v.seq);
    if (!fresh.length) return;
    st.queue.push(...fresh);
    if (st.queue.length > 8) {
      // Відстали (вкладка спала) — показуємо лише свіже; початок раунду не губимо (роль!).
      const dropped = st.queue.splice(0, st.queue.length - 8);
      const r = dropped.filter((e) => e.k === 'round').pop();
      if (r) st.queue.unshift(r);
    }
    runQueue(root, st);
  }

  const cap = (p, ms) => Promise.race([Promise.resolve(p).catch(() => {}), new Promise((r) => setTimeout(r, ms))]);

  async function runQueue(root, st) {
    if (st.running) return;
    st.running = true;
    try {
      while (st.queue.length && root._sk === st) {
        const e = st.queue.shift();
        await cap(playEvent(root, st, e), 2800);
      }
    } finally {
      st.running = false;
      if (root._sk === st) syncScene(root, st);
    }
  }

  async function playEvent(root, st, e) {
    const v = st.view, sc = st.sc, me = v && v.me;
    if (!v) return;
    const C = M.C, mine = (s) => me && s === me.seat;
    const N = (s) => esc(nickOf(v, s));
    switch (e.k) {
      case 'round':
        clearSel(st);
        if (me && me.role) showReveal(root, st);
        else flash(st, '⛏ Раунд ' + e.n + ' — копаємо!');
        if (sc) try { sc.setState(v); } catch (err) { /* нічого */ }
        break;
      case 'turn':
        if (mine(e.seat)) { sfx(st, 'turn'); if (!st.reveal) flash(st, '👉 Твій хід!'); }
        break;
      case 'place':
        sfx(st, 'dig');
        if (sc) await cap(sc.dig(e), 1200);
        if (!e.c) flash(st, '🪨 Глухий хід — від ' + N(e.seat));
        break;
      case 'goal':
        sfx(st, e.kind === 'gold' ? 'gold' : 'rock');
        if (sc) { try { sc.focus(e.x, e.y); } catch (err) { /* ок */ } await cap(sc.flip(e), 1900); }
        flash(st, e.kind === 'gold' ? '💰 Скарб Полуботка! Докопав хід ' + N(e.by) : '🪨 ' + esc(C.GOAL_PLACE[e.i]) + ': порожньо — лише каміння');
        break;
      case 'break':
        sfx(st, 'break');
        flash(st, '🔨 ' + N(e.seat) + ' → ' + (mine(e.target) ? 'тобі' : N(e.target)) + ': ' + esc(C.BREAK_NAME[e.tool].toLowerCase()) + ' ' + C.TOOL_ICON[e.tool]);
        bump(st, e.target, 'hurt');
        break;
      case 'fix':
        sfx(st, 'fix');
        flash(st, '🔧 ' + N(e.seat) + (e.seat === e.target ? ' лагодить собі ' : ' лагодить ' + (mine(e.target) ? 'тобі ' : N(e.target) + ' ')) + C.TOOL_ACC[e.tool] + ' ' + C.TOOL_ICON[e.tool]);
        bump(st, e.target, 'heal');
        break;
      case 'map': {
        sfx(st, 'map');
        const kind = mine(e.seat) ? e.kind : null;
        if (sc) await cap(sc.peek(e, kind), 1600);
        flash(st, mine(e.seat) && kind ? '🗺 Під ' + C.GOAL_PLACE[e.goal].replace('мітка', 'міткою') + ': ' + (kind === 'gold' ? '💰 скарб Полуботка!' : '🪨 лише каміння')
          : '🗺 ' + N(e.seat) + ' дивиться під ' + C.GOAL_PLACE[e.goal] + 'у');
        break;
      }
      case 'rockfall':
        sfx(st, 'rock');
        if (sc) await cap(sc.rockfall(e), 1200);
        flash(st, '🪨 Завал! Хід засипає ' + N(e.seat));
        break;
      case 'pass':
        if (!mine(e.seat)) flash(st, '🂠 ' + N(e.seat) + ' пасує — карта в скид');
        break;
      case 'deckout': flash(st, '🂠 Колода скінчилась — дограємо з рук'); break;
      case 'skip': flash(st, '🤲 У ' + N(e.seat) + ' порожні руки'); break;
      case 'end':
        sfx(st, e.side === 'diggers' ? 'gold' : e.side === 'moles' ? 'mole' : 'rock');
        if (sc) await cap(sc.finale(e.side), 2700);
        coinsToChips(st, v);
        break;
      case 'over':
        flash(st, '🏆 Партію скінчено!');
        break;
      default:
    }
  }

  function flash(st, html) {
    const f = st.el && st.el.flash;
    if (!f) return;
    f.innerHTML = emo(html);
    f.hidden = false;
    f.classList.remove('go'); void f.offsetWidth; f.classList.add('go');
    clearTimeout(st.flashT);
    st.flashT = setTimeout(() => { f.hidden = true; }, 2600);
  }

  function bump(st, seat, cls) {
    const chip = st.el.players.querySelector('.skb-pl[data-seat="' + seat + '"]');
    if (!chip || reduced()) return;
    chip.classList.remove(cls); void chip.offsetWidth; chip.classList.add(cls);
    setTimeout(() => chip.classList.remove(cls), 700);
  }

  /// Злитки летять із поля до чипів отримувачів, на чипі — «+n».
  function coinsToChips(st, v) {
    const re = v.roundEnd;
    if (!re || !re.gold) return;
    const wrap = st.el.fieldwrap.getBoundingClientRect(), base = st.el.root.getBoundingClientRect();
    const x0 = wrap.left - base.left + wrap.width / 2, y0 = wrap.top - base.top + wrap.height / 2;
    re.gold.forEach((g, k) => {
      const chip = st.el.players.querySelector('.skb-pl[data-seat="' + g.seat + '"]');
      if (!chip) return;
      const b = document.createElement('span');
      b.className = 'skb-gain';
      b.textContent = '+' + g.n + ' 🪙';
      setTimeout(() => { chip.appendChild(b); setTimeout(() => b.remove(), 1900); }, reduced() ? 0 : 200 + k * 160);
      if (reduced()) return;
      const r = chip.getBoundingClientRect();
      for (let i = 0; i < Math.min(5, g.n); i++) {
        const c = document.createElement('i');
        c.className = 'skb-coin';
        c.style.left = x0 + 'px';
        c.style.top = y0 + 'px';
        c.style.setProperty('--dx', (r.left - base.left + r.width / 2 - x0) + 'px');
        c.style.setProperty('--dy', (r.top - base.top + r.height / 2 - y0) + 'px');
        c.style.animationDelay = (k * 160 + i * 70) + 'ms';
        st.el.fx.appendChild(c);
        setTimeout(() => c.remove(), 1400 + k * 160 + i * 70);
      }
      setTimeout(() => sfx(st, 'coin', { n: g.n }), 300 + k * 160);
    });
  }

  // ─── Указівник: тягнути карту, довгий тап по чипу, тримати роль ─────────
  function onDown(root, e) {
    const st = root._sk;
    if (!st || !st.view) return;
    const badge = e.target.closest('.skb-rolebadge');
    if (badge) { st.roleHold = true; renderBadge(st, st.view, st.view.me); e.preventDefault(); return; }
    const chip = e.target.closest('.skb-pl');
    if (chip && e.button === 0) {
      const seat = +chip.dataset.seat;
      const x = e.clientX, y = e.clientY;
      clearTimeout(st.lp && st.lp.t);
      st.lp = { x, y, t: setTimeout(() => { st.lp = null; cycleSus(root, seat); }, 550) };
      return;
    }
    const card = e.target.closest('.skb-card');
    if (card && e.button === 0 && st.view.me && st.view.me.must === 'play') {
      const c = (st.view.me.hand || []).find((q) => q.id === +card.dataset.id);
      if (c && (c.k === 'path' || c.k === 'rockfall' || c.k === 'map')) st.drag = { id: c.id, x: e.clientX, y: e.clientY, el: card, ghost: null, pid: e.pointerId };
    }
  }

  function dragTarget(st, cx, cy) {
    const L = legalFor(st, st.view);
    if (!L || !L.targets.length || !st.sc) return null;
    const host = st.el.field.getBoundingClientRect();
    const px = cx - host.left, py = cy - host.top;
    if (px < 0 || py < 0 || px > host.width || py > host.height) return null;
    // Розмір клітинки на екрані — з двох сусідніх клітинок.
    const a = st.sc.screenPos('cell', 0, 0), b = st.sc.screenPos('cell', 1, 0);
    const cell = a && b ? Math.max(20, Math.hypot(b.x - a.x, b.y - a.y)) : 40;
    let best = null, bd = cell * 0.75;
    for (const t of L.targets) {
      if (t.t === 'seat') continue;
      const p = t.t === 'goal' ? st.sc.screenPos('goal', t.i) : st.sc.screenPos('cell', t.x, t.y);
      if (!p) continue;
      const d = Math.hypot(p.x - px, p.y - py);
      if (d < bd) { bd = d; best = t; }
    }
    return best;
  }

  function onMove(root, e) {
    const st = root._sk;
    if (!st) return;
    if (st.lp && Math.hypot(e.clientX - st.lp.x, e.clientY - st.lp.y) > 8) { clearTimeout(st.lp.t); st.lp = null; }
    const d = st.drag;
    if (!d || d.pid !== e.pointerId) return;
    if (!d.ghost) {
      if (Math.hypot(e.clientX - d.x, e.clientY - d.y) < 12) return;
      const src = root.querySelector('.skb-card[data-id="' + d.id + '"]') || d.el;
      d.ghost = src.cloneNode(true);
      d.ghost.classList.add('skb-dragging');
      d.ghost.style.width = src.offsetWidth + 'px';
      d.ghost.style.height = src.offsetHeight + 'px';
      document.body.appendChild(d.ghost);
      src.classList.add('lifted');
      if (!st.sel || st.sel.id !== d.id) { st.sel = { id: d.id, r: 0 }; st.cur = null; st.arm = null; }
      paint(root, st.ctx);
    }
    d.ghost.style.transform = 'translate(' + (e.clientX - d.ghost.offsetWidth / 2) + 'px,' + (e.clientY - d.ghost.offsetHeight * 0.8) + 'px)';
    const t = dragTarget(st, e.clientX, e.clientY);
    d.t = t;
    st.cur = t;
    if (st.sc && !busy(st)) { if (t) st.sc.setCursor(t.x, t.y); else st.sc.setCursor(null); st.curSig = t ? t.x + ',' + t.y : ''; }
    if (t) showTip(st, t, false); else hideTip(st);
  }

  function onUp(root, e) {
    const st = root._sk;
    if (!st) return;
    if (st.lp) { clearTimeout(st.lp.t); st.lp = null; }
    if (st.roleHold) { st.roleHold = false; if (st.view) renderBadge(st, st.view, st.view.me); }
    const d = st.drag;
    if (!d || (e.pointerId != null && d.pid !== e.pointerId)) return;
    st.drag = null;
    if (!d.ghost) return;
    d.ghost.remove();
    const src = root.querySelector('.skb-card.lifted');
    if (src) src.classList.remove('lifted');
    st.noClick = Date.now();
    hideTip(st);
    if (d.t && e.type !== 'pointercancel') play(root, st, d.t);
    else { st.cur = null; paint(root, st.ctx); }   // поза законним — карта вертається
  }

  // ─── Кліки ──────────────────────────────────────────────────────────────
  function onClick(root, e) {
    const st = root._sk;
    const b = e.target.closest('[data-a]');
    if (!b || !st || !root.contains(b) || b.disabled) return;
    if (Date.now() - st.noClick < 350 && (b.dataset.a === 'card' || b.dataset.a === 'tgt')) return;   // щойно тягнули / довгий тап
    const ctx = st.ctx, v = st.view;
    switch (b.dataset.a) {
      case 'view': switchView(root, st); return;
      case 'reset': if (st.sc) st.sc.resetView(); return;
      case 'snd': if (st.snd) { st.snd.setOn(!st.snd.on); paintSnd(st); } return;
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
      case 'card': select(root, st, +b.dataset.id); return;
      case 'rot': rotate(root, st); return;
      case 'pass': pass(root, st); return;
      case 'cancel': clearSel(st); break;
      case 'tip': {
        // Ввімкнено й карту поради вже обрано — вимкнути; інакше — показати пораду (обрати карту, курсор на місце).
        const t0 = v && v.me ? M.K.tipTarget(v.me.tip, v.me.hand) : null;
        const on = st.tipNow || tipOn(v);
        if (on && t0 && st.sel && st.sel.id === t0.card.id) { store.set('skb.tip', '0'); st.tipNow = false; clearSel(st); break; }
        st.tipNow = true; store.set('skb.tip', '1');
        if (t0) {
          if (t0.kind === 'pass') flash(st, '💡 Глек радить скинути «' + esc(M.K.cardInfo(t0.card).name) + '»');
          st.sel = null;
          select(root, st, t0.card.id);
          return;
        }
        break;
      }
      case 'tgt': play(root, st, { t: 'seat', s: +b.dataset.seat }); return;
      case 'fixtool': if (st.fixPick) { const s = st.fixPick.seat; st.fixPick = null; play(root, st, { t: 'seat', s, tool: b.dataset.tool }); } return;
      case 'fixno': st.fixPick = null; break;
      case 'next': if (ctx) ctx.act('next', {}); return;
      case 'revealx': hideReveal(root, st); return;
      case 'log': st.logOpen = !st.logOpen; store.set('skb.log', st.logOpen ? '1' : '0'); break;
      default: return;
    }
    paint(root, ctx);
  }

  // ─── Клавіатура й джойстик ──────────────────────────────────────────────
  function moveCursor(root, st, how) {
    const L = legalFor(st, st.view);
    if (!L || !L.targets.length) return false;
    const T = L.targets.map((t) => [t.x, t.y, t]);
    const c = st.cur ? [st.cur.x != null ? st.cur.x : (L.targets.find((t) => t.t === 'seat' && t.s === st.cur.s) || {}).x, st.cur.y != null ? st.cur.y : 0] : null;
    const pts = T.map((t) => [t[0], t[1]]);
    const nx = typeof how === 'number' ? M.K.cycle(pts, c, how) : M.K.stepCursor(pts, c, how);
    if (!nx) return false;
    const hit = T.find((t) => t[0] === nx[0] && t[1] === nx[1]);
    st.cur = hit ? hit[2] : null;
    st.arm = null;
    paint(root, st.ctx);
    if (st.cur && st.cur.t !== 'seat') showTip(st, st.cur, false);
    return true;
  }

  function keyDown(root, e) {
    const st = root._sk, v = st && st.view;
    if (!st || !v || !M) return false;
    const me = v.me;
    const k = e.key, code = e.code || '';
    if (k === 'Escape') {
      if (st.reveal) { hideReveal(root, st); return true; }
      if (st.sel || st.fixPick) { clearSel(st); paint(root, st.ctx); return true; }
      return false;
    }
    if (!me || me.must !== 'play') return false;
    const digit = /^Digit([1-9])$/.exec(code) || /^([1-9])$/.exec(k);
    if (digit) {
      const c = (me.hand || [])[+digit[1] - 1];
      if (!c) return false;
      select(root, st, c.id);
      return true;
    }
    if (code === 'KeyR' || k === 'r' || k === 'R') return rotate(root, st);
    if (k === 'Delete' || k === 'Backspace') return pass(root, st);
    const dir = { ArrowUp: 'N', ArrowDown: 'S', ArrowLeft: 'W', ArrowRight: 'E' }[k];
    if (dir) return moveCursor(root, st, dir);
    if (k === 'Tab' && st.sel) return moveCursor(root, st, e.shiftKey ? -1 : 1);
    if (k === 'Enter' && st.sel && st.cur) return play(root, st, st.cur);
    return false;
  }

  // ─── Реєстрація ─────────────────────────────────────────────────────────
  HGames.register({
    id: 'skarb',
    icon: ICON,
    seatClass: ['sk0', 'sk1', 'sk2', 'sk3', 'sk4', 'sk5', 'sk6', 'sk7', 'sk8', 'sk9'],
    added: '2026-10-28',
    /// Вийшов посеред партії й дивлюсь збоку — кнопка в шапці столу садить назад на своє місце (core.js rejoinLabel).
    rejoin: (rv, nick) => (ownAway(rv && rv.view, nick) ? REJOIN : ''),

    mount(root, ctx) { cur = root; paint(root, ctx); },
    update(root, ctx) { cur = root; paint(root, ctx); },
    unmount(root) {
      const st = root._sk;
      if (st) {
        clearInterval(st.tick); clearTimeout(st.nt); clearTimeout(st.flashT); clearTimeout(st.revealT);
        if (st.lp) clearTimeout(st.lp.t);
        if (st.drag && st.drag.ghost) st.drag.ghost.remove();
        if (st.ro) st.ro.disconnect();
        if (st.onClick) root.removeEventListener('click', st.onClick);
        if (st.onDown) root.removeEventListener('pointerdown', st.onDown);
        if (st.onCtx) root.removeEventListener('contextmenu', st.onCtx);
        if (st.onUp) { window.removeEventListener('pointerup', st.onUp); window.removeEventListener('pointercancel', st.onUp); }
        if (st.onMove) window.removeEventListener('pointermove', st.onMove);
        if (st.sc) { try { st.sc.dispose(); } catch (e) { /* уже прибрано */ } }
        if (st.snd) { try { st.snd.dispose(); } catch (e) { /* уже прибрано */ } st.snd = null; }
        st.queue.length = 0;
      }
      root._sk = null;
      if (cur === root) cur = null;
    },
    onKey(e) { return cur && cur._sk ? keyDown(cur, e) : false; },
    status(ctx) {
      const room = (ctx && ctx.room) || {};
      if (room.status === 'lobby') {
        let people = 0;
        for (let i = 0; i < 10; i++) if (ctx.nickOf(i)) people++;
        const bots = +((room.options || {}).bots || 0);
        return people + Math.min(bots, 10 - people) < 3 ? 'Утрьох щонайменше: поклич когось або додай 🤖 Глеків' : '';
      }
      const v = ctx && ctx.view;
      if (!v || !v.players || !M) return '';
      const st = cur && cur._sk;
      return whatNow(v, v.me || null, st || { sel: null }).s;
    },
    pad: {
      when: (ctx) => ctx.mine && ctx.playing,
      hint: '{lb}{rb} куди · {a} покласти · {x} ⟲ · {y} скинути',
      on(btn) {
        const root = cur, st = root && root._sk, v = st && st.view;
        if (!st || !v || !v.me || v.me.must !== 'play' || !M) return false;
        if (btn === 'a') { if (st.sel && st.cur) return play(root, st, st.cur); return false; }
        if (btn === 'x') return rotate(root, st);
        if (btn === 'y') return pass(root, st);
        if (btn === 'lb' || btn === 'rb') { if (!st.sel) return true; moveCursor(root, st, btn === 'lb' ? -1 : 1); return true; }
        return false;
      },
    },
  });
})();
