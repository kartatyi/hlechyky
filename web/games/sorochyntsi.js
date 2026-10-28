/*
  «Сорочинський ярмарок» — карткова гра на 2–4 «збери крамниці» (id sorochyntsi). Правила живуть на сервері
  (Impl/Sorochyntsi*.cs) — тут лише картка: що бачить і натискає гравець. Контракт виду й дій — docs/games/specs/sorochyntsi.md
  (§5–§9, §12).

  Поле — 3D «ярмарковий стіл» (/games/sorochyntsi/scene.js) або пласка SVG-мапа (/games/sorochyntsi/board2d.js) з тим самим
  API; обидві вантажаться через import(). Сталі — /games/sorochyntsi/consts.js, чисті розрахунки — /games/sorochyntsi/hud-calc.js.

  Головне — новачок без правил у кожну мить бачить, що робити: смуга «що зараз», зелені ✓ на крамницях, які можна
  купити, жетони «3 різні / 2 однакові» з поясненням, чому ні, готова пропозиція «повернути зайве», «💡 Глек радить».
  Звук — типово вимкнено (🔇 у куті поля), грає лише після жесту.
*/
(() => {
  let M = null;           // { C: consts, K: hud-calc, S: sound | null } — після load()
  let loading = null;
  let cur = null;         // root змонтованої картки (onKey, pad)

  function load() {
    if (!loading) {
      // Звук — не обов'язковий: не завантажився — граємо мовчки.
      loading = Promise.all([import('/games/sorochyntsi/consts.js'), import('/games/sorochyntsi/hud-calc.js'), import('/games/sorochyntsi/sound.js').catch(() => null)])
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
  function setHtml(el, html) { if (el && el._h !== html) { el._h = html; el.innerHTML = html; } }
  const n0 = (x) => (Number.isFinite(+x) ? +x : 0);
  const plural = (n, a, b, c) => (M ? M.C.plural(n, a, b, c) : (n === 1 ? a : n >= 2 && n <= 4 ? b : c));

  // ─── Хто є хто ───────────────────────────────────────────────────────────
  const playerOf = (v, seat) => ((v && v.players) || []).find((p) => p.seat === seat) || null;
  const nickOf = (v, seat) => { const p = playerOf(v, seat); return p ? p.nick : 'місце ' + (seat + 1); };
  const colorIdx = (v, seat) => { const p = playerOf(v, seat); return p && p.color != null ? p.color : seat; };
  const colorOf = (v, seat) => ((M ? M.C.SEAT_COLOR : ['#d64541', '#3c78d8', '#f1ece0', '#c86fb5'])[colorIdx(v, seat)] || '#999');
  const isBot = (v, seat) => { const p = playerOf(v, seat); return !!(p && (p.bot || p.away)); };
  const sameNick = (a, b) => String(a || '').toLowerCase() === String(b || '').toLowerCase();
  /// Мій прилавок, за який торгує Глек, поки я дивлюсь збоку (вийшов чи двічі проспав): сервер тримає місце (Game.Rejoin).
  const ownAway = (v, nick) => (v && v.phase !== 'over' && !v.over && nick
    && ((v.players || []).find((p) => !p.bot && p.away && sameNick(p.nick, nick)))) || null;
  const REJOIN = '🛒 Повернутись до свого прилавка';
  /// Знак гравця: колір + фігура ●▲■◆ (колір сам дальтоніка не рятує).
  function mark(v, seat) {
    const ci = colorIdx(v, seat), light = !!(M && M.C.SEAT_LIGHT[ci]);
    return '<i class="sor-pm' + (light ? ' light' : '') + '" style="--pc:' + colorOf(v, seat) + '" aria-hidden="true">' + ((M && M.C.SEAT_SHAPE[ci]) || '●') + '</i>';
  }
  const who = (v, seat) => mark(v, seat) + '<b>' + esc(nickOf(v, seat)) + '</b>';
  const cardName = (id) => { const c = M && M.C.card(id); return c ? c.name : 'крамниця'; };

  function state(root) {
    return root._so || (root._so = {
      built: false, ctx: null, view: null, sc: null, scKind: null, scLoading: false, no3d: false,
      seq: null, queue: [], running: false, pick: {}, sel: null, sheet: null, note: '', noteT: 0, tab: null,
      disc: { key: null, plan: null, hidden: false }, tipNow: false, turnKey: null, scSig: {},
      tick: 0, nt: 0, toKey: null, toAt: 0, ro: null, ro2: null, fxq: [], feed: [],
    });
  }

  // ─── Що зараз (смуга й status) ──────────────────────────────────────────
  function whatNow(v, me, st) {
    const w = whatNow0(v, me, st);
    const p = me && v && v.phase !== 'over' ? playerOf(v, me.seat) : null;
    if (p && p.away) w.l = '<b>🤖 За тебе зараз торгує Глек</b> (ти двічі проспав хід). Зроби будь-що сам — і торгуватимеш далі.<br>' + w.l;
    const ctx = st && st.ctx;
    if (!me && ctx && ctx.seat == null && ctx.playing && ownAway(v, ctx.me && ctx.me.nick)) {
      w.s = 'Твій прилавок чекає';
      w.l = '<b>🛒 Твій прилавок на місці:</b> поки тебе не було, за тебе торгував Глек. Повертайся — жетони, крамниці й бронь ті самі.'
        + ' <button type="button" class="primary sor-back" data-a="rejoin">' + REJOIN + '</button><br>' + w.l;
    }
    return w;
  }
  function whatNow0(v, me, st) {
    if (!v || !v.players) return { s: '', l: '' };
    if (!v.players.length || v.phase == null) return { s: '', l: '🪑 Стіл збирається. Сідай на вільне місце — щойно господар натисне «Почати», ярмарок відкриється. Порожні місця займе Глек 🤖, якщо його додали' };
    if (v.phase === 'over' || v.over) {
      const o = v.over || {}, w = o.winners || [];
      if (!w.length) return { s: 'Партію скінчено', l: '🏁 Ярмарок розійшовся' };
      const mine = me && w.includes(me.seat);
      if (w.length > 1) return { s: mine ? 'Спільна перемога 🤝' : 'Ярмарок поділили', l: '🤝 Ярмарок поділили: ' + w.map((s) => esc(nickOf(v, s))).join(', ') };
      return { s: mine ? 'Перемога! 🏆' : 'Переміг ' + nickOf(v, w[0]), l: mine ? '🏆 Перемога! Ти — голова ярмарку' : '🏆 Голова ярмарку — ' + esc(nickOf(v, w[0])) };
    }
    const t = v.turn, tn = esc(nickOf(v, t));
    const think = isBot(v, t) ? ' 🤖 думає…' : '…';
    let last = '';
    if (v.last) {
      const left = me ? M.K.turnsLeft(v, me.seat) : 0;
      last = (v.bulava != null ? '⚜ Булава — у ' + esc(nickOf(v, v.bulava)) + ': останнє коло' : '🔔 Коло ' + v.round + ' — останнє')
        + (me ? (me.must ? ': це твій останній хід' : ': ' + (left ? 'ще 1 хід у тебе' : 'твоїх ходів більше нема')) : '') + '. ';
    }
    if (me && me.must) {
      if (me.must === 'discard') return { s: 'Поверни ' + me.discard + ' ' + plural(me.discard, 'жетон', 'жетони', 'жетонів'), l: last + '🪙 Поверни ' + me.discard + ' ' + plural(me.discard, 'жетон', 'жетони', 'жетонів') + ' — у руці щонайбільше 10' };
      if (me.must === 'guest') return { s: 'Обери гостя', l: last + '🏠 До тебе хочуть одразу кілька гостей — обери одного (+3 ⭐). Решта прийдуть пізніше, якщо їх не переманять' };
      const can = me.can || {};
      if (can.pass) return { s: 'Пропусти хід', l: last + '🤷 Нема що робити: купи порожні, бронь повна, купити нічого — пропусти хід' };
      return { s: 'Твій хід', l: last + '🛒 <b>Твій хід:</b> візьми товари, купи чи забронюй крамницю' };
    }
    if (t == null) return { s: '', l: last };
    if (v.phase === 'discard') return { s: 'Повертає ' + nickOf(v, t), l: last + '⏳ ' + tn + ' повертає ' + ((v.discard && v.discard.n) || '') + ' ' + plural((v.discard && v.discard.n) || 0, 'жетон', 'жетони', 'жетонів') + ' — у руці понад 10' };
    if (v.phase === 'guest') return { s: 'Гостя обирає ' + nickOf(v, t), l: last + '⏳ ' + tn + ' обирає гостя' };
    if (!me) return { s: 'Торгує ' + nickOf(v, t), l: last + '👀 Дивишся збоку: торгує ' + tn + think };
    return { s: 'Торгує ' + nickOf(v, t), l: last + '⏳ Зараз торгує ' + tn + think };
  }

  // ─── Каркас DOM ─────────────────────────────────────────────────────────
  function build(root, st) {
    // ПК (картка ≥ 700 px): плашки липнуть угорі; поле ліворуч на висоту екрана; праворуч колонка: «що зараз» → панель
    // ходу → мій прилавок → бронь → гості й нагороди → вкладки. Телефон: .sor-main/.sor-side — display: contents,
    // порядок — CSS order; знизу липнуть мій прилавок стрічкою й панель дій, над ними — шторка (товари / крамниця).
    root.innerHTML = '<div class="sor">'
      + '<div class="sor-players"></div><div class="sor-pop" role="tooltip" hidden></div>'
      + '<div class="sor-sum" hidden></div>'
      + '<div class="sor-main">'
      + '<div class="sor-fieldwrap"><div class="sor-field"></div><div class="sor-fx"></div><div class="sor-feed" aria-live="polite"></div>'
      + '<div class="sor-tip" hidden></div><div class="sor-flash" hidden></div><div class="sor-banner" hidden></div>'
      + '<div class="sor-tools"><button type="button" class="sor-view" data-a="view"></button><button type="button" class="sor-view sor-snd" data-a="snd"></button></div>'
      + '<div class="sor-dlg" hidden></div></div>'
      + '<div class="sor-side"><div class="sor-say" aria-live="polite"></div><div class="sor-panel"></div><div class="sor-mine"></div><div class="sor-res"></div>'
      + '<div class="sor-awards"></div>'
      + '<div class="sor-tabs"><div class="sor-tabh" role="tablist"></div>'
      + '<div class="sor-tp" data-t="log" role="tabpanel"><div class="sor-log"></div></div>'
      + '<div class="sor-tp" data-t="help" role="tabpanel" hidden><div class="sor-help"></div></div></div>'
      + '</div></div>'
      + '<div class="sor-strip"></div><div class="sor-bar"></div>'
      + '</div>';
    st.el = {};
    for (const k of ['players', 'pop', 'sum', 'fieldwrap', 'field', 'fx', 'feed', 'tip', 'flash', 'banner', 'dlg', 'say', 'panel', 'mine', 'res', 'awards', 'tabs', 'tabh', 'log', 'help', 'strip', 'bar', 'snd'])
      st.el[k] = root.querySelector('.sor-' + k);
    st.el.view = root.querySelector('.sor-view[data-a="view"]');
    st.el.root = root.querySelector('.sor');
    st.el.tps = [...root.querySelectorAll('.sor-tp')];
    try { st.snd = M.S && M.S.createSound ? M.S.createSound() : null; } catch (e) { st.snd = null; }
    paintSnd(st);
    st.onClick = (e) => onClick(root, e);
    root.addEventListener('click', st.onClick);
    st.ro = typeof ResizeObserver === 'function' ? new ResizeObserver(() => { if (st.sc) st.sc.resize(); placeFx(st); }) : null;
    if (st.ro) st.ro.observe(st.el.field);
    st.ro2 = typeof ResizeObserver === 'function' ? new ResizeObserver(() => sizeVars(root, st)) : null;
    if (st.ro2) for (const k of ['players', 'strip', 'bar']) st.ro2.observe(st.el[k]);
    st.tick = setInterval(() => tick(root), 250);
    st.built = true;
  }

  /// Висоти плашок, стрічки й панелі — у змінні картки: ПК рахує з них висоту поля, телефон — відступи липкого низу.
  function sizeVars(root, st) {
    const k = st.el && st.el.root;
    if (!k) return;
    const hOf = (el) => (el && el.getClientRects().length ? Math.round(el.getBoundingClientRect().height) : 0);
    k.style.setProperty('--sor-plh', hOf(st.el.players) + 'px');
    k.style.setProperty('--sor-striph', (st.el.strip.firstChild ? hOf(st.el.strip) : 0) + 'px');
    k.style.setProperty('--sor-barh', (st.el.bar.firstChild ? hOf(st.el.bar) : 0) + 'px');
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
  /// Останні 10 с МОГО строку — «тік» раз на секунду (вимкнений звук play сам глушить).
  function tickSound(st, v) {
    const me = v.me;
    if (!st.snd || !st.snd.on || !me || !me.must || !v.deadline) return;
    const left = Date.parse(v.deadline) - Date.now();
    if (!(left > 0 && left <= 10000)) return;
    const key = Math.ceil((Date.parse(v.deadline) - 500) / 1000) + '|' + Math.ceil(left / 1000);
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
      onPick: (k, id) => onPick(root, k, id),
      onHover: (k, id) => onHover(root, k, id),
      quality: coarse() || (navigator.hardwareConcurrency || 8) <= 4 ? 'low' : 'high',
      reducedMotion: reduced(),
    };
    let sc = null, kind = null;
    if (store.get('sor.view') !== '2d') {
      try {
        const m = await import('/games/sorochyntsi/scene.js');
        sc = await m.createScene(host, opts);
        kind = '3d';
      } catch (e) {
        console.warn('Сорочинці: 3D не вийшло — малюю пласке поле', e);
        host.textContent = '';
        st.no3d = true;
      }
    }
    if (!sc) {
      try {
        const m = await import('/games/sorochyntsi/board2d.js');
        sc = await m.createBoard2d(host, opts);
        kind = '2d';
      } catch (e) {
        st.scLoading = false;
        host.innerHTML = '<p class="sor-err">Не вдалося намалювати поле. Онови сторінку.</p>';
        throw e;
      }
    }
    st.scLoading = false;
    if (root._so !== st) { try { sc.dispose(); } catch (e) { /* уже */ } return; }   // картку вже зняли, поки вантажилось
    st.sc = sc; st.scKind = kind; st.scSig = {};
    paint(root, st.ctx);
  }

  function switchView(root, st) {
    const to = st.scKind === '3d' ? '2d' : '3d';
    store.set('sor.view', to);
    st.no3d = false;
    if (st.sc) { try { st.sc.dispose(); } catch (e) { /* уже прибрано */ } }
    st.sc = null; st.el.field.textContent = '';
    hideTip(st);
    ensureScene(root, st);
  }

  const busy = (st) => st.running || st.queue.length > 0;
  const rowIds = (v) => ((v && v.rows) || []).flat().filter((x) => x != null);
  const myReserve = (v) => (v && v.me && v.me.reserved) || [];

  /// Що підсвітити на полі: лише в мій хід (§12).
  function legalFor(v) {
    const me = v && v.me;
    if (!me || !me.must) return null;
    if (me.must === 'guest') return { cards: [], decks: [], piles: [], guests: (me.guests || []).slice() };
    if (me.must !== 'act') return null;
    const can = me.can || {};
    return {
      cards: Object.keys(me.cards || {}).map(Number),
      decks: [1, 2, 3].filter((t) => (can.reserveDeck || [])[t - 1]),
      piles: ((me.take || {}).piles || []).slice(),
      guests: [],
    };
  }

  function tipOn(st, v) {
    const n = +(store.get('sor.games') || 0);
    return st.tipNow || (n < 2 && !!(v && v.me && v.me.must));
  }

  function syncScene(root, st) {
    const v = st.view, sc = st.sc;
    if (!sc || !v || !v.rows) return;
    if (busy(st)) return;   // анімації ще йдуть: сцена сама в стані «після події», вид — коли черга спорожніє
    const call = (name, arg) => {
      const s = JSON.stringify(arg === undefined ? null : arg);
      if (st.scSig[name] === s) return;
      st.scSig[name] = s;
      try { sc[name](arg); } catch (e) { console.warn('Сорочинці: сцена ' + name, e); }
    };
    try { sc.setState(v); } catch (e) { console.warn('Сорочинці: setState', e); }
    call('setLegal', legalFor(v));
    call('setMarks', v.me ? M.K.marks(v.me.cards) : {});
    const ts = v.me ? M.K.compact(st.pick) : {};
    call('setPick', Object.keys(ts).length ? ts : null);
    call('setSelected', st.sel && st.sel.k === 'card' ? st.sel.id : null);
  }

  // ─── Вибір: товари, крамниця, колода ────────────────────────────────────
  function note(st, s) {
    st.note = s || '';
    clearTimeout(st.noteT);
    if (s) st.noteT = setTimeout(() => { st.note = ''; if (st.view && st.el) renderPanel(st, st.view, st.view.me); }, 3500);
  }
  function addGood(root, st, g) {
    const v = st.view, me = v && v.me;
    if (!me || me.must !== 'act') return false;
    const r = M.K.addPick(me.take, st.pick, g, v.bank);
    st.pick = r.pick;
    note(st, r.why);
    st.sel = null;
    st.sheet = 'goods';
    paint(root, st.ctx);
    return true;
  }
  function subGood(root, st, g) {
    st.pick = M.K.subPick(st.pick, g);
    note(st, '');
    paint(root, st.ctx);
    return true;
  }
  function selectCard(root, st, id) {
    if (st.sel && st.sel.k === 'card' && st.sel.id === id) { st.sel = null; st.sheet = null; }
    else { st.sel = { k: 'card', id }; st.pick = {}; st.sheet = 'card'; note(st, ''); }
    hideTip(st);
    paint(root, st.ctx);
  }
  function selectDeck(root, st, tier) {
    st.sel = { k: 'deck', tier }; st.pick = {}; st.sheet = 'card';
    paint(root, st.ctx);
  }
  function clearSel(st) { st.sel = null; st.pick = {}; st.sheet = null; note(st, ''); hideTip(st); }

  function doTake(root, st) {
    const v = st.view, me = v && v.me;
    if (!me || me.must !== 'act') return false;
    const ts = M.K.takeState(me.take, st.pick, v.bank);
    if (!ts.ok) { note(st, ts.why || ts.hint); paint(root, st.ctx); return false; }
    act(st, 'take', { goods: ts.goods });
    clearSel(st);
    paint(root, st.ctx);
    return true;
  }
  function doBuy(root, st, id) {
    const v = st.view, me = v && v.me;
    const c = me && (me.cards || {})[id];
    if (!me || me.must !== 'act' || !c || !c.ok) return false;
    act(st, 'buy', { card: id });
    clearSel(st);
    paint(root, st.ctx);
    return true;
  }
  function doReserve(root, st) {
    const v = st.view, me = v && v.me, s = st.sel;
    if (!me || me.must !== 'act' || !s) return false;
    const can = me.can || {};
    if (s.k === 'card') {
      if (!can.reserve || !rowIds(v).includes(s.id)) return false;
      act(st, 'reserve', { card: s.id });
    } else if (s.k === 'deck') {
      if (!(can.reserveDeck || [])[s.tier - 1]) return false;
      act(st, 'reserve', { tier: s.tier });
    } else return false;
    clearSel(st);
    paint(root, st.ctx);
    return true;
  }
  function act(st, name, p) { if (st.ctx && st.ctx.act) st.ctx.act(name, p); }

  /// «💡 Глек радить»: показати пораду на полі й у панелі (вибір — як від руки, натиснути лишається тобі).
  function applyTip(root, st) {
    const v = st.view, me = v && v.me, t = me && me.tip;
    st.tipNow = true;
    if (!t) { paint(root, st.ctx); return; }
    if (t.act === 'take') { st.pick = M.K.tipPick(t); st.sel = null; st.sheet = 'goods'; }
    else if ((t.act === 'buy' || t.act === 'reserve') && t.card != null) { st.sel = { k: 'card', id: t.card }; st.pick = {}; st.sheet = 'card'; }
    else if (t.act === 'reserve' && t.tier) { st.sel = { k: 'deck', tier: t.tier }; st.pick = {}; st.sheet = 'card'; }
    else if (t.act === 'discard' && t.give) { st.disc.plan = M.K.compact(t.give); st.disc.hidden = false; }
    else if (t.act === 'pass') flash(st, '💡 Глек радить пропустити хід — нема що робити');
    paint(root, st.ctx);
  }

  // ─── Клік по полю й підказка ────────────────────────────────────────────
  function onPick(root, kind, id) {
    const st = root._so;
    if (!st || !st.view || !M) return;
    const me = st.view.me;
    if (!me || !me.must) return;
    if (kind === 'pile') addGood(root, st, id);
    else if (kind === 'card' && me.must === 'act') selectCard(root, st, +id);
    else if (kind === 'deck' && me.must === 'act') selectDeck(root, st, +id);
    else if (kind === 'guest' && me.must === 'guest' && (me.guests || []).includes(+id)) act(st, 'guest', { guest: +id });
  }

  function onHover(root, kind, id) {
    const st = root._so;
    if (!st || !st.el) return;
    if (kind !== 'card' || coarse()) { hideTip(st); return; }
    showTip(st, +id);
  }
  function showTip(st, id) {
    const tip = st.el.tip, v = st.view, sc = st.sc;
    if (!tip || !sc || !v) return;
    const c = M.C.card(id);
    if (!c) { hideTip(st); return; }
    const info = v.me && (v.me.cards || {})[id];
    let h = '<b>' + esc(c.name) + '</b> · ' + M.C.GOOD_ICON[c.bonus] + (c.pts ? ' +' + c.pts + ' ⭐' : '') + (c.k ? ' ⚔' : '');
    if (info) h += '<br>' + (info.ok ? '<span class="sor-ok">✓ можна купити' + (info.pay && Object.keys(info.pay).length ? ': ' + M.K.iconsText(info.pay) : ' задарма') + '</span>' : '<span class="sor-no">' + esc(info.why) + '</span>');
    tip.innerHTML = h;
    tip.hidden = false;
    const p = sc.screenPos('card', id);
    if (!p) return;
    const W = st.el.fieldwrap.clientWidth, w = tip.offsetWidth;
    const x = Math.max(w / 2 + 6, Math.min(W - w / 2 - 6, p.x));
    const below = p.y < tip.offsetHeight + 70;
    tip.style.left = Math.round(x) + 'px';
    tip.style.top = Math.round(below ? p.y + 64 : p.y - 60) + 'px';
    tip.classList.toggle('below', below);
  }
  function hideTip(st) { if (st.el && st.el.tip) st.el.tip.hidden = true; }

  // ─── Малювання ──────────────────────────────────────────────────────────
  function paint(root, ctx) {
    const st = state(root);
    st.ctx = ctx;
    st.view = ctx && ctx.view && ctx.view.players ? ctx.view : null;
    if (!M) {
      if (!root.firstChild) root.innerHTML = '<p class="sor-wait muted">Розкладаємо ятки…</p>';
      load().then(() => { if (root._so === st) paint(root, st.ctx); }, (e) => { root.innerHTML = '<p class="sor-err">Не вдалося завантажити гру. Онови сторінку.</p>'; console.warn(e); });
      return;
    }
    if (!st.built) build(root, st);
    const v = st.view;
    if (!v) { setHtml(st.el.say, '<b>Чекаємо на початок…</b>'); return; }
    const me = v.me || null;
    validate(st, v, me);
    const w = whatNow(v, me, st);
    const tipLine = me && me.must && me.tip && tipOn(st, v) ? '<div class="sor-tipl">💡 Глек би: ' + esc(M.K.tipText(me.tip, cardName)) + '</div>' : '';
    setHtml(st.el.say, '<div class="sor-say-t">' + w.l + tipLine + '</div>'
      + (me && me.must && me.tip ? '<button type="button" class="sor-tipb' + (st.tipNow ? ' on' : '') + '" data-a="tip" title="Що зробив би Глек на твоєму місці">💡<span class="sor-w"> Глек радить</span></button>' : ''));
    st.el.say.hidden = !!v.over;
    st.el.say.classList.toggle('mine', !!(me && me.must));
    renderPlayers(st, v, me);
    renderPanel(st, v, me);
    renderMine(st, v, me);
    renderRes(st, v, me);
    renderAwards(st, v, me);
    renderDlg(st, v, me);
    renderSum(st, v, me);
    renderTabs(st, v);
    renderStrip(st, v, me);
    renderBar(st, v, me);
    attention(st, v, me);
    setHtml(st.el.view, st.scKind === '3d' ? '🗺<span class="sor-w"> Пласка</span>' : '🧊<span class="sor-w"> 3D</span>');
    st.el.view.title = st.scKind === '3d' ? 'Показати пласке поле (легше для слабких пристроїв)' : (st.no3d ? '3D не вдалося — спробувати ще' : 'Показати об\'ємний стіл');
    queueEvents(root, st, v);
    if (!st.sc) ensureScene(root, st);
    else syncScene(root, st);
    nudge(root, st);
  }

  /// Вибране, якого вже нема (купили, хід минув), — геть.
  function validate(st, v, me) {
    const key = v.turn + '|' + v.round + '|' + (me ? me.must : '');
    if (st.turnKey !== key) { st.turnKey = key; st.tipNow = false; st.pick = {}; if (st.sel && st.sel.k === 'deck') st.sel = null; st.sheet = null; }
    if (!me || me.must !== 'act') st.pick = {};
    if (st.sel && st.sel.k === 'card' && !rowIds(v).includes(st.sel.id) && !myReserve(v).some((r) => r.id === st.sel.id)) st.sel = null;
    if (st.sel && st.sel.k === 'deck' && !(me && me.must === 'act')) st.sel = null;
    if (st.sheet === 'card' && !st.sel) st.sheet = null;
    if (me && me.must !== 'discard') { st.disc.key = null; st.disc.hidden = false; }
  }

  // ─── Плашки гравців ─────────────────────────────────────────────────────
  const PLATE_TIPS = {
    pts: (v) => '⭐ <b>Очки:</b> ⭐ куплених крамниць + 3 за кожного гостя + 3 за Січову старшину. Булаву бере той, у кого ≥ ' + v.target + ' ⭐ і знижка кожним товаром разом із 🌸.',
    bonus: () => '🧵🐟🌿🏺🐑🌸 <b>Знижки</b> — куплені крамниці кожного товару: стільки цього товару менше платиш за кожну наступну. Тьмяні з пунктиром — ще нема (бракує для булави).',
    tok: () => '🪙 <b>Дукати й жетони в руці.</b> Більше 10 наприкінці ходу не можна — зайві повертаєш у банк. Дукат іде за будь-який товар; береться лише разом із бронею.',
    res: (v, p) => {
      const rs = p.reserved || [];
      if (!rs.length) return '📌 <b>Бронь</b> — до 3 відкладених крамниць (купити зможе лише власник). Поки порожньо.';
      const open = rs.filter((r) => r.id != null).map((r) => '«' + esc(cardName(r.id)) + '»' + (r.blind ? ' 🂠' : ''));
      const blind = rs.filter((r) => r.id == null).map((r) => M.C.TIER_ROMAN[r.tier]);
      return '📌 <b>Бронь ' + esc(p.nick) + ':</b> ' + [open.join(', '), blind.length ? 'наосліп з колоди ' + blind.join(', ') : ''].filter(Boolean).join(' · ');
    },
    guests: (v, p) => '🏠 <b>Гості:</b> ' + ((p.guests || []).length ? (p.guests || []).map((g) => esc(M.K.guestName(g))).join(', ') + ' — по +3 ⭐' : 'ще ніхто не завітав. Гість приходить сам, щойно знижки покривають його вимогу.'),
    icons: (v, p) => '⚔ <b>Козацькі позначки</b> на крамницях: ' + (p.icons || 0) + '. Хто першим має 3 — бере 🎖 Січову старшину (+3 ⭐); перебирає той, у кого строго більше.'
      + (v.starshyna ? ' Зараз — у ' + esc(nickOf(v, v.starshyna.seat)) + ' (' + v.starshyna.icons + ' ⚔).' : ''),
    bulava: (v) => '⚜ <b>Гетьманська булава</b> — у ' + esc(nickOf(v, v.bulava)) + '. Догравається коло, переможе той, у кого найбільше ⭐.',
    shape: () => '●▲■◆ <b>Фігура гравця</b> — поруч із кольором скрізь: на плашці, на полі, у підсумку.',
  };

  function renderPlayers(st, v, me) {
    const C = M.C, K = M.K;
    const A = 2 * Math.PI * 17;
    const tip = (k, html, title) => '<span class="sor-ic" data-tip="' + k + '" title="' + esc(title) + '">' + html + '</span>';
    const h = (v.players || []).map((p) => {
      const mine = me && me.seat === p.seat;
      const think = v.turn === p.seat && v.phase !== 'over' && v.phase != null;
      const ci = colorIdx(v, p.seat), light = !!C.SEAT_LIGHT[ci];
      const fin = v.over && v.over.points ? v.over.points[(v.order || []).indexOf(p.seat)] : null;
      const pts = fin != null ? fin : p.points || 0;
      const bonus = C.BONUSES.map((g) => {
        const n = (p.bonus || {})[g] || 0;
        return '<i class="sor-bc' + (n ? '' : ' miss') + '" style="--gc:' + C.GOOD_COLOR[g] + ';--gi:' + C.GOOD_INK[g] + '" title="' + esc(C.GOOD_NAME[g]) + ': ' + n + '">' + (n || '') + '</i>';
      }).join('');
      const nt = K.ntokOf(p), gold = (p.tokens || {}).gold || 0;
      const won = v.over && (v.over.winners || []).includes(p.seat);
      const name = esc(p.nick) + (p.bot ? '' : p.away ? ' <span title="Вийшов або проспав — за нього торгує Глек">🤖</span>' : '');
      return '<div class="sor-pl' + (think ? ' turn think' : '') + (mine ? ' me' : '') + (light ? ' light' : '') + (won ? ' win' : '') + '" data-seat="' + p.seat + '" style="--pc:' + colorOf(v, p.seat) + '">'
        + '<span class="sor-av"><svg viewBox="0 0 40 40" aria-hidden="true"><circle class="bg" cx="20" cy="20" r="17"/>'
        + (think && v.deadline ? '<circle class="fg" cx="20" cy="20" r="17" transform="rotate(-90 20 20)" stroke-dasharray="' + A.toFixed(1) + '"/>' : '')
        + '</svg><b>' + (p.bot ? '🤖' : esc(String(p.nick || '?').trim().charAt(0).toUpperCase())) + '</b>'
        + '<span class="sor-shp" data-tip="shape" title="Фігура гравця">' + (C.SEAT_SHAPE[ci] || '●') + '</span></span>'
        + '<span class="sor-pn">' + name + (mine ? ' <em>(ти)</em>' : '') + (p.bulava ? ' ' + tip('bulava', '⚜', 'Гетьманська булава') : '') + '</span>'
        + '<span class="sor-pp">' + tip('pts', '⭐ <b>' + pts + '</b><span class="sor-w"> з ' + v.target + '</span>', 'Очки (булава — від ' + v.target + ')')
        + '<span class="sor-mb"><i style="width:' + Math.min(100, Math.round(pts / (v.target || 16) * 100)) + '%"></i></span></span>'
        + '<span class="sor-bns">' + tip('bonus', bonus, 'Знижки') + '</span>'
        + '<span class="sor-ps">' + tip('tok', '🪙' + gold + ' · <b class="' + (nt >= 10 ? 'full' : '') + '">' + nt + '</b>/10', 'Дукатів ' + gold + ', жетонів ' + nt + ' з 10')
        + ((p.reserved || []).length ? tip('res', '📌' + p.reserved.length, 'Бронь') : '')
        + ((p.guests || []).length ? tip('guests', '🏠' + p.guests.length, 'Гості') : '')
        + tip('icons', (p.starshyna ? '🎖' : '⚔') + (p.icons || 0), p.starshyna ? 'Січова старшина' : 'Козацькі позначки') + '</span>'
        + (think && v.deadline ? '<span class="sor-sec" data-sec></span><span class="sor-tm"><i></i></span>' : '')
        + '</div>';
    }).join('');
    setHtml(st.el.players, h);
    tickArcs(st);
  }

  function onPlateTap(root, e) {
    const st = root._so;
    if (!st || !st.el) return false;
    const pop = st.el.pop;
    const t = e.target.closest('[data-tip]');
    if (!t || !st.el.players.contains(t) || !st.view) { if (!pop.hidden) pop.hidden = true; return false; }
    const f = PLATE_TIPS[t.dataset.tip];
    const pl = t.closest('[data-seat]');
    if (!f || !pl) return false;
    const key = t.dataset.tip + pl.dataset.seat;
    if (!pop.hidden && pop._for === key) { pop.hidden = true; return true; }
    pop._for = key;
    pop.innerHTML = f(st.view, playerOf(st.view, +pl.dataset.seat) || {});
    pop.hidden = false;
    const host = st.el.root.getBoundingClientRect(), r = t.getBoundingClientRect();
    const w = Math.min(280, host.width - 16);
    pop.style.width = w + 'px';
    pop.style.left = Math.max(8, Math.min(host.width - w - 8, r.left - host.left + r.width / 2 - w / 2)) + 'px';
    pop.style.top = (r.bottom - host.top + 6) + 'px';
    clearTimeout(st.popT);
    st.popT = setTimeout(() => { pop.hidden = true; }, 7000);
    return true;
  }

  // ─── Крамниця (HTML) ────────────────────────────────────────────────────
  function costDots(cost, cls) {
    const C = M.C;
    return C.GOODS.filter((g) => cost[g]).map((g) => '<i class="sor-cd' + (cls ? ' ' + cls : '') + '" style="--gc:' + C.GOOD_COLOR[g] + ';--gi:' + C.GOOD_INK[g] + '" title="' + esc(C.GOOD_NAME[g]) + '">' + cost[g] + '</i>').join('');
  }
  function cardHtml(id, opt = {}) {
    const C = M.C, c = C.card(id);
    if (!c) return '';
    const m = opt.mark ? '<span class="sor-cm ' + opt.mark + '">' + (opt.mark === 'buy' ? '✓' : opt.mark === 'gold' ? '🪙' : '≈') + '</span>' : '';
    return '<span class="sor-card' + (opt.small ? ' small' : '') + '" style="--gc:' + C.GOOD_COLOR[c.bonus] + ';--gi:' + C.GOOD_INK[c.bonus] + '">'
      + '<span class="sor-cb"><b>' + (c.pts || '') + '</b>' + (c.pts ? '<small>⭐</small>' : '') + (c.k ? '<span class="sor-ck">⚔</span>' : '') + '<span class="sor-cg">' + C.GOOD_ICON[c.bonus] + '</span></span>'
      + '<span class="sor-cn">' + esc(c.name) + '</span>'
      + '<span class="sor-cc">' + costDots(c.cost) + '</span>'
      + '<span class="sor-ct">' + C.TIER_ROMAN[c.tier] + '</span>' + m
      + (opt.blind ? '<span class="sor-cbl" title="Наосліп — бачиш лише ти">🂠</span>' : '') + '</span>';
  }

  // ─── Панель ходу: товари й обрана крамниця ──────────────────────────────
  /// ПК: товари (у мій хід) і обрана крамниця — у колонці. Телефон: те саме — шторкою знизу, лише коли її відкрито.
  function renderPanel(st, v, me) {
    let h = '';
    const phone = phoneLayout(st);
    if (st.sel) h += selHtml(st, v, me);
    if (me && me.must === 'act' && (!phone || st.sheet === 'goods' || !st.sel)) h += goodsHtml(st, v, me);
    const open = !!h && (!phone || st.sheet != null);
    setHtml(st.el.panel, h ? '<div class="sor-box">' + (phone && st.sheet ? '<button type="button" class="sor-shx" data-a="sheet-x" aria-label="Згорнути">▾</button>' : '') + h + '</div>' : '');
    st.el.panel.classList.toggle('open', open);
  }
  /// Телефонна розкладка (картка < 700 px): шторки замість колонки.
  const phoneLayout = (st) => !!(st.el && st.el.root && st.el.root.clientWidth && st.el.root.clientWidth < 700);

  function goodsHtml(st, v, me) {
    const C = M.C, K = M.K, bank = v.bank || {};
    const take = me.take || {}, ts = K.takeState(take, st.pick, bank);
    const can = me.can || {};
    if (!can.take) return '<div class="sor-gh"><b>🧺 Товари</b> <span class="muted small">' + esc((me.why || {}).take || 'купи порожні') + '</span></div>';
    const tip = me.tip && me.tip.act === 'take' && tipOn(st, v) ? K.tipPick(me.tip) : null;
    const btns = C.GOODS.map((g, i) => {
      const n = bank[g] || 0, pk = n0(st.pick[g]);
      const ok = (take.piles || []).includes(g);
      return '<span class="sor-gw' + (pk ? ' on' : '') + '">'
        + '<button type="button" class="sor-g" data-a="g+" data-g="' + g + '" style="--gc:' + C.GOOD_COLOR[g] + ';--gi:' + C.GOOD_INK[g] + '"' + (ok ? '' : ' disabled')
        + ' title="' + esc(C.GOOD_NAME[g]) + ': у банку ' + n + (coarse() ? '' : ' (' + (i + 1) + ')') + '" aria-label="' + esc(C.GOOD_NAME[g]) + ', у банку ' + n + '">'
        + '<span class="i">' + C.GOOD_ICON[g] + '</span><span class="n">' + n + '</span>'
        + (pk ? '<span class="pk">+' + pk + '</span>' : '') + (tip && tip[g] ? '<span class="sor-tipm" title="Глек радить">💡</span>' : '') + '</button>'
        + (pk ? '<button type="button" class="sor-gm" data-a="g-" data-g="' + g + '" aria-label="менше ' + esc(C.GOOD_GEN[g]) + '">−</button>' : '')
        + '</span>';
    }).join('')
      + '<span class="sor-gw gold"><span class="sor-g off" style="--gc:' + C.GOOD_COLOR.gold + ';--gi:' + C.GOOD_INK.gold + '" title="Дукати: беруться лише з бронею"><span class="i">🪙</span><span class="n">' + (bank.gold || 0) + '</span></span></span>';
    const total = K.total(st.pick);
    const warn = total ? K.overflowText(K.ntokOf(playerOf(v, me.seat)), total) : '';
    const line = st.note ? '<span class="sor-no">' + esc(st.note) + '</span>'
      : ts.ok ? '<span class="sor-ok">✓ можна: ' + (ts.mode === 'two' ? 'два однакові' : ts.goods.length === 3 ? 'три різні' : 'по одному з кожної купи') + '</span>'
        : ts.why ? '<span class="sor-no">' + esc(ts.why) + '</span>' : '<span class="muted">' + esc(ts.hint) + '</span>';
    return '<div class="sor-gh"><b>🧺 Товари</b> <span class="muted small">' + esc(ts.rule) + '</span></div>'
      + '<div class="sor-goods">' + btns + '</div>'
      + '<div class="sor-gl">' + line + (warn ? ' <span class="sor-warn">⚠ ' + esc(warn) + '</span>' : '') + '</div>'
      + '<div class="sor-row"><button type="button" class="sor-btn primary" data-a="take"' + (ts.ok ? '' : ' disabled') + '>🧺 Взяти' + (ts.ok ? ' ' + K.iconsRepeat(st.pick) : '') + '</button>'
      + (total ? '<button type="button" class="sor-btn ghost" data-a="pick-x" title="Скинути вибір (Esc)">✕</button>' : '') + '</div>';
  }

  function selHtml(st, v, me) {
    const C = M.C, K = M.K, s = st.sel;
    const myAct = me && me.must === 'act';
    const can = (me && me.can) || {};
    const goldNote = (v.bank || {}).gold > 0 ? ' (+🪙)' : '';
    if (s.k === 'deck') {
      const ok = myAct && (can.reserveDeck || [])[s.tier - 1];
      const why = (me && me.why || {})['deck' + s.tier];
      return '<div class="sor-sel"><div class="sor-selh"><span class="sor-deck">' + C.TIER_ROMAN[s.tier] + '</span><div><b>Колода ' + C.TIER_ROMAN[s.tier] + ' — «' + esc(C.TIER_NAME[s.tier]) + '»</b>'
        + '<div class="muted small">' + ((v.decks || [])[s.tier - 1] || 0) + ' крамниць · наосліп: верхню бачиш лише ти</div></div></div>'
        + (ok ? '' : '<div class="sor-no small">' + esc(why || 'не зараз') + '</div>')
        + '<div class="sor-row"><button type="button" class="sor-btn primary" data-a="reserve"' + (ok ? '' : ' disabled') + '>📌 Забронювати наосліп з колоди ' + C.TIER_ROMAN[s.tier] + goldNote + '</button>'
        + '<button type="button" class="sor-btn ghost" data-a="sel-x" title="Esc">✕</button></div></div>';
    }
    const id = s.id, c = C.card(id);
    if (!c) return '';
    const info = me && (me.cards || {})[id];
    const inRow = rowIds(v).includes(id);
    const mine = myReserve(v).find((r) => r.id === id);
    const after = me ? K.costAfter(id, (playerOf(v, me.seat) || {}).bonus) : c.cost;
    const cl = me ? K.closes(id, v, me.seat) : [];
    let h = '<div class="sor-sel"><div class="sor-selh">' + cardHtml(id, { mark: info ? K.marks({ [id]: info })[id] : null, blind: mine && mine.blind })
      + '<div class="sor-seli"><b>' + esc(c.name) + '</b><div class="muted small">' + C.TIER_ROMAN[c.tier] + ' · ' + esc(C.TIER_NAME[c.tier]) + (mine ? ' · 📌 твоя бронь' : '') + '</div>'
      + '<div class="small">знижка ' + C.GOOD_ICON[c.bonus] + ' ' + esc(C.GOOD_NAME[c.bonus]) + (c.pts ? ' · <b>+' + c.pts + ' ⭐</b>' : '') + (c.k ? ' · ⚔ козак' : '') + '</div>'
      + '<div class="small sor-cost">ціна ' + costDots(c.cost) + '</div>'
      + (me ? '<div class="small sor-cost">тобі ' + (Object.keys(after).length ? costDots(after) : '<b class="sor-ok">задарма</b>') + '</div>' : '') + '</div></div>';
    if (info) h += info.ok ? '<div class="sor-pay">заплатиш: <b>' + (Object.keys(info.pay || {}).length ? K.iconsText(info.pay) : 'нічого') + '</b></div>'
      : '<div class="sor-no small">' + esc(info.why) + '</div>';
    if (cl.length) h += '<div class="sor-cl small">закриє: ' + cl.map((x) => esc(x.text)).join(' · ') + '</div>';
    if (myAct) {
      h += '<div class="sor-row">';
      h += '<button type="button" class="sor-btn primary" data-a="buy" data-id="' + id + '"' + (info && info.ok ? '' : ' disabled') + '>🛒 Купити</button>';
      if (inRow) h += '<button type="button" class="sor-btn" data-a="reserve"' + (can.reserve ? '' : ' disabled title="' + esc((me.why || {}).reserve || '') + '"') + '>📌 Забронювати' + goldNote + '</button>';
      h += '<button type="button" class="sor-btn ghost" data-a="sel-x" title="Esc">✕</button></div>';
      if (inRow && !can.reserve && (me.why || {}).reserve) h += '<div class="muted small">' + esc(me.why.reserve) + '</div>';
    } else h += '<div class="sor-row"><span class="muted small">' + (me ? 'Купити — у свій хід' : '') + '</span><button type="button" class="sor-btn ghost" data-a="sel-x">✕</button></div>';
    return h + '</div>';
  }

  // ─── Мій прилавок ───────────────────────────────────────────────────────
  function renderMine(st, v, me) {
    if (!me || v.phase == null) { setHtml(st.el.mine, ''); return; }
    const C = M.C, K = M.K, p = playerOf(v, me.seat) || {};
    const b = p.bonus || {}, t = p.tokens || {};
    const miss = new Set(me.bulava ? me.bulava.goods : []);
    const col = (g) => '<th class="' + (miss.has(g) ? 'miss' : '') + '" style="--gc:' + C.GOOD_COLOR[g] + '" title="' + esc(C.GOOD_NAME[g]) + (miss.has(g) ? ' — бракує для булави' : '') + '">' + C.GOOD_ICON[g] + '</th>';
    const cell = (g, n, cls) => '<td class="' + (miss.has(g) ? 'miss ' : '') + (cls || '') + (n ? '' : ' z') + '">' + n + '</td>';
    let h = '<div class="sor-mh"><b>Мій прилавок</b> · ' + (p.cards || []).length + ' ' + plural((p.cards || []).length, 'крамниця', 'крамниці', 'крамниць')
      + ' · <span class="' + (K.ntokOf(p) >= 10 ? 'sor-no' : '') + '">🧺 ' + K.ntokOf(p) + '/10</span></div>';
    if (me.bulava) {
      const g = me.bulava.goods.map((x) => C.GOOD_ICON[x]).join(' ');
      h += '<div class="sor-bl">⚜ до булави: ' + (me.bulava.points ? '<b>' + me.bulava.points + ' ⭐</b>' : '<b class="sor-ok">⭐ досить</b>') + (g ? ' і ' + g : ' — товари всі є') + '</div>';
    }
    h += '<table class="sor-tbl"><thead><tr><th></th>' + C.BONUSES.map(col).join('') + '<th style="--gc:' + C.GOOD_COLOR.gold + '" title="Дукати">🪙</th></tr></thead><tbody>'
      + '<tr><th>знижка</th>' + C.BONUSES.map((g) => cell(g, b[g] || 0, 'b')).join('') + '<td class="na">—</td></tr>'
      + '<tr><th>жетони</th>' + C.BONUSES.map((g) => (g === 'fern' ? '<td class="na' + (miss.has(g) ? ' miss' : '') + '">—</td>' : cell(g, t[g] || 0))).join('') + cell('gold', t.gold || 0) + '</tr>'
      + '<tr class="sum"><th>разом</th>' + C.BONUSES.map((g) => cell(g, (b[g] || 0) + (g === 'fern' ? 0 : t[g] || 0))).join('') + cell('gold', t.gold || 0) + '</tr>'
      + '</tbody></table>';
    setHtml(st.el.mine, h);
  }

  function renderRes(st, v, me) {
    if (!me || v.phase == null) { setHtml(st.el.res, ''); return; }
    const K = M.K, rs = me.reserved || [];
    const mk = K.marks(me.cards);
    let h = '<div class="sor-mh"><b>📌 Бронь</b> · ' + rs.length + '/3</div>';
    if (!rs.length) h += '<p class="sor-note">Порожньо. Забронюй крамницю (з ряду чи наосліп з колоди) — вона чекатиме лише на тебе, а з банку прийде 🪙 дукат.</p>';
    else h += '<div class="sor-rl">' + rs.map((r) => (r.id == null ? '' : '<button type="button" class="sor-rc' + (st.sel && st.sel.k === 'card' && st.sel.id === r.id ? ' on' : '') + '" data-a="sel" data-id="' + r.id + '" title="' + esc(cardName(r.id)) + ((me.cards || {})[r.id] && !me.cards[r.id].ok ? ' — ' + esc(me.cards[r.id].why) : '') + '">'
      + cardHtml(r.id, { small: true, mark: mk[r.id], blind: r.blind }) + (r.blind ? '<small class="muted">бачиш лише ти</small>' : '') + '</button>')).join('') + '</div>';
    setHtml(st.el.res, h);
  }

  // ─── Гості й нагороди ───────────────────────────────────────────────────
  function renderAwards(st, v, me) {
    if (v.phase == null) { setHtml(st.el.awards, ''); return; }
    const C = M.C, K = M.K;
    const p = me ? playerOf(v, me.seat) : null;
    const b = (p && p.bonus) || {};
    let h = '<div class="sor-mh"><b>🏠 Гості ярмарку</b> <span class="muted small">— приходять самі за знижки, +3 ⭐</span></div><div class="sor-gl2">';
    h += (v.guests || []).map((gs) => {
      const gp = K.guestProgress(gs.id, b);
      const taken = gs.by != null;
      const prog = gp.items.map((x) => '<span class="sor-gq' + (p && x.ok ? ' ok' : '') + '" style="--gc:' + C.GOOD_COLOR[x.g] + '">' + C.GOOD_ICON[x.g] + ' ' + (p && !taken ? x.have + '/' : '') + x.need + '</span>').join('');
      const tipG = me && me.tip && me.tip.act === 'guest' && me.tip.guest === gs.id;
      return '<div class="sor-gu' + (taken ? ' taken' : p && gp.done ? ' ready' : '') + '"><div class="sor-gn"><b>' + esc(K.guestName(gs.id)) + '</b>'
        + (taken ? ' <span class="small">у ' + who(v, gs.by) + '</span>' : p && gp.done ? ' <span class="sor-ok small">✓ прийде до тебе</span>' : p ? ' <span class="muted small">бракує ' + gp.left + '</span>' : '')
        + (tipG ? ' <span title="Глек радить">💡</span>' : '') + '</div><div class="sor-gr">' + prog + '</div></div>';
    }).join('') + '</div>';
    // Старшина.
    const s = v.starshyna;
    const need = me ? K.starshynaNeed(v, me.seat) : 0;
    h += '<div class="sor-aw"><span class="i">🎖</span><div><b>Січова старшина</b> (+3 ⭐) — ' + (s ? 'у ' + who(v, s.seat) + ' (' + s.icons + ' ⚔)' : 'ще нічия: першому з 3 ⚔')
      + (me ? '<div class="small muted">' + (s && s.seat === me.seat ? 'Твоя! Перебере лише той, у кого стане більше ⚔' : 'Тобі, щоб ' + (s ? 'перебрати' : 'взяти') + ': ' + need + ' ⚔ (є ' + ((p && p.icons) || 0) + ')') + '</div>' : '') + '</div></div>';
    h += '<div class="sor-aw"><span class="i">⚜</span><div><b>' + esc(C.BULAVA_NAME) + '</b> — ' + (v.bulava != null ? 'у ' + who(v, v.bulava) + ': догравається коло' : 'ще нічия')
      + '<div class="small muted">Бере перший, у кого ≥ ' + v.target + ' ⭐ і знижка кожним із шести товарів (разом із 🌸 цвітом папороті).</div></div></div>';
    setHtml(st.el.awards, h);
  }

  // ─── Діалоги над полем: повернути зайве, гість на вибір ────────────────
  function renderDlg(st, v, me) {
    const C = M.C, K = M.K;
    let h = '';
    if (me && me.must === 'discard' && me.discard > 0 && !st.disc.hidden) {
      const p = playerOf(v, me.seat) || {}, tok = p.tokens || {};
      const key = me.discard + ':' + JSON.stringify(tok);
      if (st.disc.key !== key) {
        st.disc.key = key;
        const tg = me.tip && me.tip.act === 'discard' && me.tip.give && K.discardOk(tok, me.tip.give, me.discard) ? K.compact(me.tip.give) : null;
        st.disc.plan = tg || K.suggestDiscard(tok, me.discard);
      }
      const plan = st.disc.plan, sum = K.total(plan), need = me.discard;
      h = '<h3>🪙 Поверни ' + need + ' ' + plural(need, 'жетон', 'жетони', 'жетонів') + '</h3>'
        + '<p>У руці щонайбільше 10. Глек уже підібрав — з тих, яких найбільше (дукати — останніми). Можна поправити.</p>'
        + '<div class="sor-disc">' + C.TOKENS.map((g) => {
          const has = tok[g] || 0, n = plan[g] || 0;
          return '<div class="sor-dr' + (has ? '' : ' z') + '" style="--gc:' + C.GOOD_COLOR[g] + ';--gi:' + C.GOOD_INK[g] + '"><span class="sor-dt">' + C.GOOD_ICON[g] + '</span><small>є ' + has + '</small>'
            + '<button type="button" data-a="d+" data-g="' + g + '"' + (n < has && sum < need ? '' : ' disabled') + ' aria-label="повернути більше ' + esc(C.GOOD_GEN[g] || 'дукатів') + '">+</button>'
            + '<b>' + n + '</b>'
            + '<button type="button" data-a="d-" data-g="' + g + '"' + (n ? '' : ' disabled') + ' aria-label="повернути менше">−</button></div>';
        }).join('') + '</div>'
        + '<button type="button" class="sor-btn big primary" data-a="discard"' + (sum === need ? '' : ' disabled') + '>'
        + (sum === need ? '↩ Повернути ' + K.iconsText(plan) : sum < need ? 'Обери ще ' + (need - sum) : 'Забагато на ' + (sum - need)) + '</button>'
        + '<button type="button" class="sor-link" data-a="disc-hide">глянути на поле</button>';
    } else if (me && me.must === 'guest') {
      const tg = me.tip && me.tip.act === 'guest' ? me.tip.guest : null;
      h = '<h3>🏠 Кого покликати?</h3><p>Прийти хочуть кілька — за хід приходить один (+3 ⭐). Решта лишаються на столі.</p><div class="sor-gch">'
        + (me.guests || []).map((id) => {
          const gp = K.guestProgress(id, (playerOf(v, me.seat) || {}).bonus);
          const near = (v.players || []).filter((x) => x.seat !== me.seat).map((x) => ({ s: x.seat, left: K.guestProgress(id, x.bonus).left })).sort((a, b) => a.left - b.left)[0];
          return '<button type="button" class="sor-gcb' + (tg === id ? ' tip' : '') + '" data-a="guest" data-id="' + id + '"><b>' + esc(K.guestName(id)) + '</b>'
            + '<span class="sor-gr">' + gp.items.map((x) => '<span class="sor-gq ok" style="--gc:' + C.GOOD_COLOR[x.g] + '">' + C.GOOD_ICON[x.g] + ' ' + x.need + '</span>').join('') + '</span>'
            + '<small class="muted">' + esc(C.GUEST_HINT[id] || '') + '</small>'
            + (near ? '<small>найближче до нього: ' + esc(nickOf(v, near.s)) + ' (бракує ' + near.left + ')</small>' : '')
            + (tg === id ? '<span class="sor-tipg">⭐ Глек радить</span>' : '') + '</button>';
        }).join('') + '</div>';
    }
    setHtml(st.el.dlg, h ? '<div class="sor-dlgin" role="dialog">' + h + '</div>' : '');
    st.el.dlg.hidden = !h;
  }

  // ─── Підсумок ───────────────────────────────────────────────────────────
  function renderSum(st, v, me) {
    const o = v.over;
    if (!o) { setHtml(st.el.sum, ''); st.el.sum.hidden = true; return; }
    st.el.sum.hidden = false;
    const C = M.C, K = M.K, s = o.stats || {};
    const order = v.order || v.players.map((p) => p.seat);
    const at = (arr, seat) => (arr || [])[order.indexOf(seat)];
    const rank = order.slice().sort((a, b) => (at(o.points, b) || 0) - (at(o.points, a) || 0) || (at(o.cards, a) || 0) - (at(o.cards, b) || 0));
    const w = o.winners || [];
    const again = st.ctx && st.ctx.mine ? '<button type="button" class="sor-btn primary sor-again" data-a="again">🔁 Ще раз</button>' : '';
    const reason = o.reason === 'bulava' ? '⚜ булава в ' + esc(nickOf(v, o.bulava != null ? o.bulava : v.bulava)) : o.reason === 'rounds' ? 'ярмарок зачинився після 40 кіл' : o.reason === 'stall' ? 'товари скінчились' : '';
    let h = '<div class="sor-sumh"' + (w.length === 1 ? ' style="--pc:' + colorOf(v, w[0]) + '"' : '') + '><span class="sor-cup">' + (w.length > 1 ? '🤝' : '🏆') + '</span><div>'
      + '<div class="muted small">' + (w.length > 1 ? 'Ярмарок поділили' : 'Голова ярмарку') + (reason ? ' · ' + reason : '') + '</div>'
      + '<h3>' + (w.length ? w.map((x) => who(v, x)).join(', ') + ' — ' + (at(o.points, w[0]) || 0) + ' ⭐' : 'Партію скінчено')
      + (s.rounds ? ' <span class="muted small">за ' + s.rounds + ' ' + plural(s.rounds, 'коло', 'кола', 'кіл') + '</span>' : '') + '</h3></div>' + again + '</div>';
    const T = K.titles(s);
    if (T.length) h += '<div class="sor-titles">' + T.map((t) => '<div class="sor-ti" style="--pc:' + colorOf(v, t.seat) + '"><span class="i">' + t.icon + '</span><div><div class="n">' + esc(t.name) + '</div>'
      + '<div class="w">' + who(v, t.seat) + '</div><div class="d">' + esc(t.why) + '</div></div></div>').join('') + '</div>';
    h += '<div class="sor-sumgrid"><ol class="sor-rank">' + rank.map((x) => '<li' + (w.includes(x) ? ' class="win"' : '') + '>' + who(v, x) + ' <b class="g">' + (at(o.points, x) || 0) + ' ⭐</b> <span class="muted small">· ' + (at(o.cards, x) || 0) + ' ' + plural(at(o.cards, x) || 0, 'крамниця', 'крамниці', 'крамниць') + '</span></li>').join('') + '</ol>';
    if (s.timeline && s.timeline.length > 1) {
      const W = 320, H = 170, g = K.chart(s.timeline, order, W, H, v.target);
      let svg = '<svg class="sor-chart" viewBox="0 0 ' + W + ' ' + H + '" role="img" aria-label="Очки за колами">';
      for (let q = 0; q <= g.max; q += Math.max(2, Math.ceil(g.max / 5 / 2) * 2)) svg += '<line x1="24" x2="' + (W - 26) + '" y1="' + g.y(q) + '" y2="' + g.y(q) + '" class="grid"/><text x="18" y="' + (g.y(q) + 4) + '" class="ax" text-anchor="end">' + q + '</text>';
      if (g.targetY != null) svg += '<line x1="24" x2="' + (W - 26) + '" y1="' + g.targetY + '" y2="' + g.targetY + '" class="goal"/><text x="28" y="' + (g.targetY - 4) + '" class="ax">булава ' + v.target + ' ⭐</text>';
      for (const ln of g.lines) svg += '<polyline points="' + ln.pts.map((p) => p.join(',')).join(' ') + '" style="stroke:' + colorOf(v, ln.seat) + '"' + (w.includes(ln.seat) ? ' class="win"' : '') + '/>';
      const used = {};
      for (const ln of g.lines) {
        const [x, y] = ln.pts[ln.pts.length - 1];
        const k = Math.round(y / 6);
        const dx = (used[k] = (used[k] || 0) + 1) - 1;
        svg += '<text x="' + (x + 3 + dx * 10) + '" y="' + (y + 4) + '" class="shp" style="fill:' + colorOf(v, ln.seat) + '">' + C.SEAT_SHAPE[colorIdx(v, ln.seat)] + '</text>';
      }
      svg += '<text x="' + (W / 2) + '" y="' + (H - 4) + '" class="ax" text-anchor="middle">кола →</text></svg>';
      h += '<div><div class="sor-trl">⭐ за колами</div>' + svg + '</div>';
    }
    h += '</div>';
    if (s.seats && s.seats.length) {
      h += '<div class="sor-tbw"><table class="sor-stats"><thead><tr><th></th><th>🧺<br><small>взяв жетонів</small></th><th>📌<br><small>броні (наосліп)</small></th><th>🛒<br><small>I · II · III</small></th>'
        + '<th>🪙<br><small>дукатів</small></th><th>↩<br><small>повернув</small></th><th>🏠</th><th>⚔</th><th>🌸</th></tr></thead><tbody>'
        + s.seats.map((x) => '<tr' + (me && x.seat === me.seat ? ' class="me"' : '') + '><td>' + who(v, x.seat) + '</td><td>' + (x.tokens || 0) + '</td><td>' + (x.reserves || 0) + (x.blind ? ' (' + x.blind + ')' : '') + '</td>'
          + '<td>' + (x.buys || []).join(' · ') + '</td><td>' + (x.goldSpent || 0) + '</td><td>' + (x.returned || 0) + '</td><td>' + (x.guests || 0) + '</td><td>' + (x.icons || 0) + '</td><td>' + (x.fern || 0) + '</td></tr>').join('')
        + '</tbody></table></div>';
    }
    setHtml(st.el.sum, h);
    // Новачкам (перші 2 партії) Глек радить сам: рахуємо зіграні.
    if (me && st.countedOver !== v.seq) {
      st.countedOver = v.seq;
      const key = 'sor.gseen';
      if (store.get(key) !== String(v.seq)) { store.set(key, String(v.seq)); store.set('sor.games', String(+(store.get('sor.games') || 0) + 1)); }
    }
  }

  // ─── Вкладки «📜 Журнал · ❔ Правила» ───────────────────────────────────
  const HELP = '<ol>'
    + '<li><b>Твій хід — одна дія</b>: 🧺 взяти товари, 🛒 купити крамницю або 📌 забронювати її</li>'
    + '<li><b>Товари:</b> три різні жетони або два однакові — з купи, де щонайменше 4. Порожніх куп менше трьох — по одному з кожної</li>'
    + '<li><b>Наприкінці ходу</b> в руці щонайбільше <b>10 жетонів</b>, решту повертаєш</li>'
    + '<li><b>Крамниця</b> дає назавжди <b>знижку</b> одним товаром (і часом ⭐): кожна наступна дешевша. Платиш спершу своїми жетонами, бракує — 🪙 дукатами</li>'
    + '<li><b>📌 Бронь</b> — до 3 крамниць (з ряду або наосліп з колоди) і 🪙 дукат на додачу, якщо є. Купити заброньовану можеш лише ти</li>'
    + '<li><b>🏠 Гості</b> приходять самі, щойно знижки покривають їхню вимогу: +3 ⭐, щонайбільше один за хід</li>'
    + '<li><b>🎖 Січова старшина</b> (+3 ⭐) — першому з 3 ⚔ на крамницях; перебирає той, у кого ⚔ строго більше</li>'
    + '<li><b>⚜ Гетьманська булава:</b> хто першим наприкінці ходу має досить ⭐ і знижку кожним із шести товарів (разом із 🌸 цвітом папороті — лише з крамниць III рівня), бере булаву. Коло догравається</li>'
    + '<li><b>Перемагає</b> той, у кого найбільше ⭐; рівність — у кого менше крамниць; далі — спільна перемога</li></ol>';

  function renderTabs(st, v) {
    const t = st.tab || 'log';
    const tab = (k, label) => '<button type="button" role="tab" class="sor-tab' + (t === k ? ' on' : '') + '" data-a="tab" data-t="' + k + '" aria-selected="' + (t === k) + '">' + label + '</button>';
    setHtml(st.el.tabh, tab('log', '📜 Журнал') + tab('help', '❔ Правила'));
    for (const p of st.el.tps) p.hidden = p.dataset.t !== t;
    const lines = v.log || [];
    setHtml(st.el.log, lines.length ? '<ol>' + lines.map((l) => '<li>' + esc(l) + '</li>').join('') + '</ol>' : '<p class="sor-note">Поки тихо — тут буде, хто що зробив.</p>');
    setHtml(st.el.help, HELP);
  }

  // ─── Телефон: мій прилавок стрічкою й панель дій ────────────────────────
  function renderStrip(st, v, me) {
    if (!me || v.phase == null || v.over) { setHtml(st.el.strip, ''); return; }
    const C = M.C, K = M.K, p = playerOf(v, me.seat) || {};
    const b = p.bonus || {}, t = p.tokens || {};
    const miss = new Set(me.bulava ? me.bulava.goods : []);
    const h = C.BONUSES.map((g) => {
      const tg = g === 'fern' ? 'gold' : g;
      return '<span class="sor-sc' + (miss.has(g) ? ' miss' : '') + '" style="--gc:' + C.GOOD_COLOR[g] + ';--gi:' + C.GOOD_INK[g] + ';--tc:' + C.GOOD_COLOR[tg] + ';--ti:' + C.GOOD_INK[tg] + '" title="' + esc(C.GOOD_NAME[g]) + ': знижка ' + (b[g] || 0) + ', ' + (tg === 'gold' ? 'дукатів ' : 'жетонів ') + (t[tg] || 0) + '">'
        + '<b class="bn">' + C.GOOD_ICON[g] + (b[g] || 0) + '</b><b class="tk">' + (tg === 'gold' ? '🪙' : '') + (t[tg] || 0) + '</b></span>';
    }).join('');
    setHtml(st.el.strip, '<div class="sor-sl">' + h + '<span class="sor-sn' + (K.ntokOf(p) >= 10 ? ' full' : '') + '">' + K.ntokOf(p) + '<small>/10</small></span></div>');
  }

  function renderBar(st, v, me) {
    let h = '';
    const bb = (a, icon, label, o = {}) => '<button type="button" class="sor-bb' + (o.cls ? ' ' + o.cls : '') + '" data-a="' + a + '"' + (o.id != null ? ' data-id="' + o.id + '"' : '') + (o.off ? ' disabled' : '')
      + (o.title ? ' title="' + esc(o.title) + '"' : '') + '><span class="i">' + icon + '</span><small>' + label + '</small></button>';
    if (me && me.must === 'act') {
      const can = me.can || {};
      if (can.pass) h = bb('pass', '🤷', 'Пропустити хід', { cls: 'primary wide' });
      else {
        h += bb('sheet-goods', '🧺', 'Товари', { cls: st.sheet === 'goods' ? 'on' : '', off: !can.take });
        if (st.sel && st.sel.k === 'card') {
          const info = (me.cards || {})[st.sel.id];
          h += bb('buy', '🛒', 'Купити', { id: st.sel.id, cls: info && info.ok ? 'primary' : '', off: !(info && info.ok), title: info && !info.ok ? info.why : '' });
          h += bb('reserve', '📌', 'Бронь', { off: !(can.reserve && rowIds(v).includes(st.sel.id)) });
        } else if (st.sel && st.sel.k === 'deck') h += bb('reserve', '📌', 'Наосліп ' + M.C.TIER_ROMAN[st.sel.tier], { cls: 'primary', off: !(can.reserveDeck || [])[st.sel.tier - 1] });
        else {
          const ts = M.K.takeState(me.take, st.pick, v.bank);
          h += bb('take', '✅', 'Взяти', { cls: ts.ok ? 'primary' : '', off: !ts.ok });
        }
        if (me.tip) h += bb('tip', '💡', 'Глек радить', { cls: st.tipNow ? 'on' : '' });
      }
    } else if (me && me.must === 'discard') h = bb('disc-open', '↩', 'Повернути ' + me.discard, { cls: 'primary wide' });
    setHtml(st.el.bar, h);
  }

  // ─── Таймер, Глеки, «час минув» ─────────────────────────────────────────
  function tickArcs(st) {
    const v = st.view;
    if (!v || !st.el) return;
    const left = v.deadline ? Math.max(0, Date.parse(v.deadline) - Date.now()) : null;
    const total = v.timerMs || 60000;
    const L = 2 * Math.PI * 17;
    const frac = left == null ? 1 : Math.min(1, left / total);
    const late = left != null && left > 0 && left < 10000;
    for (const fg of st.el.players.querySelectorAll('.sor-av .fg')) { fg.style.strokeDashoffset = (L * (1 - frac)).toFixed(1); fg.classList.toggle('late', late); }
    for (const tm of st.el.players.querySelectorAll('.sor-tm i')) tm.style.width = (frac * 100).toFixed(1) + '%';
    for (const pl of st.el.players.querySelectorAll('.sor-pl.think')) pl.classList.toggle('late', late);
    for (const s of st.el.players.querySelectorAll('[data-sec]')) {
      const t = left == null ? '' : (late ? '⏱ ' : '') + Math.ceil(left / 1000) + ' с';
      if (s.textContent !== t) s.textContent = t;
    }
  }

  function myRank(v, ctx) {
    let rank = 0;
    for (const p of v.players || []) if (p.seat < ctx.seat && !p.bot && !p.away) rank++;
    return rank;
  }

  function tick(root) {
    const st = root._so;
    if (!st || !st.view) return;
    tickArcs(st);
    tickSound(st, st.view);
    tickAttn(st);
    // Строк минув (+0,5 с) — будь-хто за столом штовхає сервер (той звірить із годинником і доробить хід Глеком).
    const v = st.view, ctx = st.ctx;
    if (!v.deadline || !ctx || !ctx.mine || !ctx.playing || !ctx.room || v.phase === 'over' || !HGames.send) return;
    const due = Date.parse(v.deadline) + 500 + myRank(v, ctx) * 700;
    const now = Date.now();
    if (now < due) return;
    if (st.toKey === v.deadline && now - st.toAt < 4000) return;
    st.toKey = v.deadline; st.toAt = now;
    HGames.send('Act', ctx.room.id, 'timeout', {});
  }

  /// Глеки ходять на штовхан клієнта людини: першим штовхає перша людина за столом, решта — із запасом.
  function nudge(root, st) {
    const v = st.view || {}, ctx = st.ctx;
    clearTimeout(st.nt);
    st.nt = 0;
    if (!ctx || !ctx.playing || !ctx.mine || typeof v.botIn !== 'number' || !HGames.send || !ctx.room) return;
    const fire = () => {
      if (root._so !== st || !st.ctx || !st.ctx.room) return;
      HGames.send('Act', st.ctx.room.id, 'bot', null);
      st.nt = setTimeout(fire, 1500);
    };
    st.nt = setTimeout(fire, Math.max(0, v.botIn) + 60 + myRank(v, ctx) * 700);
  }

  // ─── Увага без звуку ────────────────────────────────────────────────────
  function attention(st, v, me) {
    const live = me && v.phase !== 'over' && !v.over;
    const mine = !!(live && me.must);
    const first = st.attn == null;
    const was = st.attn || false;
    st.attn = mine;
    if (!first && mine && !was) {
      const now = Date.now();
      if (!(now - (st.vibAt || 0) < 5000)) {
        st.vibAt = now;
        try { if (navigator.vibrate && (!navigator.userActivation || navigator.userActivation.hasBeenActive)) navigator.vibrate(120); } catch (e) { /* не всюди можна */ }
      }
    }
    st.attnTitle = mine ? (me.must === 'discard' ? '🪙 Поверни жетони — Сорочинський ярмарок' : '🛒 Твій хід — Сорочинський ярмарок') : null;
  }
  function tickAttn(st) {
    if (typeof document === 'undefined') return;
    const want = document.hidden && st.attnTitle ? st.attnTitle : null;
    if (want) {
      const t = document.title;
      if (t !== want && t !== st.titleBase) st.titleBase = t;
      st.blinkT = want;
      const on = Math.floor(Date.now() / 1000) % 2 === 0;
      const next = on ? want : st.titleBase;
      if (next != null && document.title !== next) document.title = next;
    } else stopBlink(st);
  }
  function stopBlink(st) {
    if (!st || !st.blinkT) return;
    if (typeof document !== 'undefined' && document.title === st.blinkT && st.titleBase != null) document.title = st.titleBase;
    st.blinkT = null;
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
    fieldEvents(st, v, fresh);
    st.queue.push(...fresh);
    if (st.queue.length > 10) {
      // Відстали (вкладка спала) — лише свіже; булаву й кінець не губимо.
      const dropped = st.queue.splice(0, st.queue.length - 10);
      for (const k of ['bulava', 'over']) { const e = dropped.filter((x) => x.k === k).pop(); if (e) st.queue.unshift(e); }
    }
    runQueue(root, st);
  }

  const cap = (p, ms) => Promise.race([Promise.resolve(p).catch(() => {}), new Promise((r) => setTimeout(r, ms))]);

  async function runQueue(root, st) {
    if (st.running) return;
    st.running = true;
    try {
      while (st.queue.length && root._so === st) {
        const e = st.queue.shift();
        await cap(playEvent(root, st, e), 2800);
        // Після кожної події — вид (сцена після анімації вже в стані «після події»; setState ідемпотентний).
        if (st.sc && st.view) { try { st.sc.setState(st.view); } catch (err) { /* нічого */ } }
      }
    } finally {
      st.running = false;
      if (root._so === st) { st.scSig = {}; syncScene(root, st); }
    }
  }

  async function playEvent(root, st, e) {
    const v = st.view, sc = st.sc, me = v && v.me;
    if (!v) return;
    const mine = (s) => !!me && s === me.seat;
    const N = (s) => esc(nickOf(v, s));
    const run = (name, ms) => (sc && typeof sc[name] === 'function' ? cap(sc[name](e), ms) : null);
    switch (e.k) {
      case 'round':
        if (e.n === 1) flash(st, '🧺 Ярмарок відкрито! Першим торгує ' + N(e.first));
        break;
      case 'turn':
        if (mine(e.seat)) { sfx(st, 'turn'); flash(st, '👉 Твій хід!'); }
        break;
      case 'take': sfx(st, 'token', { n: M.K.total(e.got) }); await run('take', 1000); break;
      case 'reserve': sfx(st, 'reserve'); await run('reserve', 1000); break;
      case 'buy': sfx(st, 'buy'); await run('buy', 1200); break;
      case 'refill': await run('refill', 700); break;
      case 'discard': sfx(st, 'token', { n: 2 }); await run('discard', 800); break;
      case 'guest':
        sfx(st, 'guest');
        await run('guest', 1300);
        flash(st, '🏠 До ' + (mine(e.seat) ? 'тебе' : N(e.seat)) + ' у гості — ' + esc(M.K.guestName(e.guest)) + ' (+3 ⭐)');
        break;
      case 'starshyna':
        sfx(st, 'starshyna');
        await run('starshyna', 1100);
        flash(st, '🎖 Січова старшина — у ' + (mine(e.seat) ? 'тебе' : N(e.seat)) + ' (' + e.icons + ' ⚔)' + (e.from != null ? ', від ' + N(e.from) : ''));
        break;
      case 'bulava':
        sfx(st, 'bulava');
        banner(st, '⚜ ' + (mine(e.seat) ? 'Ти береш' : N(e.seat) + ' бере') + ' гетьманську булаву!<small>Останнє коло</small>');
        await run('bulava', 1700);
        break;
      case 'last':
        if (e.reason === 'rounds') flash(st, '🔔 Коло ' + v.maxRounds + ' — останнє: ярмарок зачиняється');
        break;
      case 'over':
        if (sc && typeof sc.finale === 'function') await cap(sc.finale(v.over || e), 2600);
        break;
      default:
    }
  }

  function flash(st, html) {
    const f = st.el && st.el.flash;
    if (!f) return;
    f.innerHTML = html;
    f.hidden = false;
    f.classList.remove('go'); void f.offsetWidth; f.classList.add('go');
    clearTimeout(st.flashT);
    st.flashT = setTimeout(() => { f.hidden = true; }, 2600);
  }
  function banner(st, html) {
    const b = st.el && st.el.banner;
    if (!b) return;
    b.innerHTML = '<div>' + html + '</div>';
    b.hidden = false;
    b.classList.remove('go'); void b.offsetWidth; b.classList.add('go');
    clearTimeout(st.banT);
    st.banT = setTimeout(() => { b.hidden = true; }, 2500);
  }

  /// Чужі ходи: бульбашка над прилавком (по одній, ≤ 3 с; у черзі — 1,6 с) і стрічка 3 останніх подій (гасне за 6 с).
  function fieldEvents(st, v, list) {
    const me = v.me ? v.me.seat : null, K = M.K, C = M.C;
    for (const e of list) {
      if (e.seat == null || e.seat === me) continue;
      const pc = colorOf(v, e.seat), nk = esc(nickOf(v, e.seat));
      let bub = null, feed = null;
      if (e.k === 'take') { bub = '🧺 +' + K.iconsRepeat(e.got); feed = '🧺 ' + nk + ' бере ' + esc(K.takePhrase(e.got)); }
      else if (e.k === 'reserve') {
        bub = e.slot == null ? '📌 наосліп з колоди ' + C.TIER_ROMAN[e.tier] : '📌 «' + esc(cardName(e.card)) + '»';
        feed = '📌 ' + nk + (e.slot == null ? ' бронює наосліп з колоди ' + C.TIER_ROMAN[e.tier] : ' бронює «' + esc(cardName(e.card)) + '»') + (e.gold ? ' +🪙' : '');
      } else if (e.k === 'buy') { const c = C.card(e.card); bub = '🛒 ' + esc(cardName(e.card)) + (c && c.pts ? ' +' + c.pts + ' ⭐' : ''); feed = '🛒 ' + nk + ' купує «' + esc(cardName(e.card)) + '»'; }
      else if (e.k === 'guest') { bub = '🏠 ' + esc(K.guestName(e.guest)) + ' +3 ⭐'; feed = '🏠 до ' + nk + ' — ' + esc(K.guestName(e.guest)); }
      else if (e.k === 'discard') { bub = '↩ повертає ' + K.iconsText(e.gave); feed = '↩ ' + nk + ' повертає ' + K.iconsText(e.gave); }
      else if (e.k === 'pass') { bub = '🤷 пропускає'; feed = '🤷 ' + nk + ' пропускає хід'; }
      else if (e.k === 'starshyna') feed = '🎖 ' + nk + ' — Січова старшина';
      if (feed) addFeed(st, feed, pc);
      if (bub) st.fxq.push({ seat: e.seat, text: bub, pc });
    }
    if (st.fxq.length > 3) st.fxq.splice(0, st.fxq.length - 3);
    runFx(st);
  }
  function addFeed(st, html, pc) {
    st.feed.push({ html, pc, t: Date.now() });
    if (st.feed.length > 3) st.feed.splice(0, st.feed.length - 3);
    renderFeed(st);
  }
  function renderFeed(st) {
    if (!st.el || !st.el.feed) return;
    const now = Date.now();
    st.feed = st.feed.filter((x) => now - x.t < 6000);
    setHtml(st.el.feed, st.feed.slice().reverse().map((x, i) => '<div class="' + (i ? 'old' : '') + '" style="--pc:' + x.pc + ';animation-delay:-' + (now - x.t) + 'ms">' + x.html + '</div>').join(''));
    clearTimeout(st.feedT);
    if (st.feed.length) st.feedT = setTimeout(() => renderFeed(st), Math.max(60, 6000 - (now - st.feed[0].t)));
  }
  function runFx(st) {
    if (st.fxCur || !st.fxq.length || !st.el || !st.sc) return;
    const f = st.fxq.shift();
    st.fxCur = f;
    const b = document.createElement('div');
    b.className = 'sor-bub';
    b.style.setProperty('--pc', f.pc);
    b.innerHTML = f.text;
    st.el.fx.textContent = '';
    st.el.fx.appendChild(b);
    f.el = b;
    placeFx(st);
    clearTimeout(st.fxT);
    st.fxT = setTimeout(() => { st.fxCur = null; if (st.el) st.el.fx.textContent = ''; runFx(st); }, st.fxq.length ? 1600 : 3000);
  }
  function placeFx(st) {
    const f = st.fxCur;
    if (!f || !f.el || !st.sc || !st.el) return;
    const p = st.sc.screenPos('seat', f.seat);
    f.el.style.display = p ? '' : 'none';
    if (!p) return;
    const W = st.el.fieldwrap.clientWidth, bw = f.el.offsetWidth || 120;
    const below = p.y < 50;
    f.el.style.left = Math.round(Math.max(bw / 2 + 6, Math.min(W - bw / 2 - 6, p.x))) + 'px';
    f.el.style.top = Math.round(below ? p.y + 16 : p.y - 10) + 'px';
    f.el.classList.toggle('below', below);
  }

  // ─── Кліки ──────────────────────────────────────────────────────────────
  function onClick(root, e) {
    const st = root._so;
    if (!st) return;
    if (onPlateTap(root, e)) return;
    const b = e.target.closest('[data-a]');
    if (!b || !root.contains(b) || b.disabled) return;
    const ctx = st.ctx, v = st.view, me = v && v.me;
    const a = b.dataset.a, g = b.dataset.g;
    switch (a) {
      case 'view': switchView(root, st); return;
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
      case 'tab': st.tab = b.dataset.t; break;
      case 'g+': addGood(root, st, g); return;
      case 'g-': subGood(root, st, g); return;
      case 'pick-x': st.pick = {}; note(st, ''); break;
      case 'take': doTake(root, st); return;
      case 'buy': doBuy(root, st, b.dataset.id != null ? +b.dataset.id : st.sel && st.sel.id); return;
      case 'reserve': doReserve(root, st); return;
      case 'pass': if (me && me.must === 'act' && (me.can || {}).pass) { act(st, 'pass', {}); clearSel(st); } break;
      case 'sel': { const id = +b.dataset.id; if (st.sel && st.sel.k === 'card' && st.sel.id === id) { st.sel = null; st.sheet = null; } else { st.sel = { k: 'card', id }; st.pick = {}; st.sheet = 'card'; } break; }
      case 'sel-x': st.sel = null; if (st.sheet === 'card') st.sheet = null; break;
      case 'sheet-x': st.sheet = null; break;
      case 'sheet-goods': st.sheet = st.sheet === 'goods' ? null : 'goods'; if (st.sheet) st.sel = null; break;
      case 'tip': applyTip(root, st); return;
      case 'd+': case 'd-':
        if (st.disc.plan) { st.disc.plan[g] = Math.max(0, (st.disc.plan[g] || 0) + (a === 'd+' ? 1 : -1)); st.disc.plan = M.K.compact(st.disc.plan); }
        break;
      case 'disc-hide': st.disc.hidden = true; break;
      case 'disc-open': st.disc.hidden = false; break;
      case 'discard': if (st.disc.plan && me && me.must === 'discard') act(st, 'discard', { give: M.K.compact(st.disc.plan) }); break;
      case 'guest': if (me && me.must === 'guest') act(st, 'guest', { guest: +b.dataset.id }); break;
      default: return;
    }
    paint(root, ctx);
  }

  // ─── Клавіатура й джойстик ──────────────────────────────────────────────
  /// Крамниці під курсор: ряди (III → I, зліва направо), далі моя бронь.
  function cursorList(v) {
    const L = legalFor(v);
    if (!L) return [];
    const set = new Set(L.cards);
    const rows = [2, 1, 0].flatMap((i) => ((v.rows || [])[i] || []).filter((x) => x != null && set.has(x)));
    return rows.concat(myReserve(v).map((r) => r.id).filter((x) => x != null && set.has(x) && !rows.includes(x)));
  }
  function moveSel(root, st, d) {
    const list = cursorList(st.view);
    if (!list.length) return false;
    const i = st.sel && st.sel.k === 'card' ? list.indexOf(st.sel.id) : -1;
    const n = list[((i < 0 ? (d > 0 ? -1 : 0) : i) + d + list.length) % list.length];
    st.sel = { k: 'card', id: n }; st.pick = {}; st.sheet = 'card';
    paint(root, st.ctx);
    return true;
  }

  function keyDown(root, e) {
    const st = root._so, v = st && st.view;
    if (!st || !v || !M) return false;
    const me = v.me, k = e.key, code = e.code || '';
    if (k === 'Escape') {
      if (st.el && st.el.pop && !st.el.pop.hidden) { st.el.pop.hidden = true; return true; }
      if (st.sel || M.K.total(st.pick) || st.sheet) { clearSel(st); paint(root, st.ctx); return true; }
      return false;
    }
    if (!me || !me.must) return false;
    if (me.must === 'discard') {
      if (k === 'Enter' && st.disc.plan && M.K.discardOk((playerOf(v, me.seat) || {}).tokens, st.disc.plan, me.discard)) { act(st, 'discard', { give: M.K.compact(st.disc.plan) }); return true; }
      return false;
    }
    if (me.must === 'guest') {
      const d = /^Digit([1-9])$/.exec(code);
      if (d && me.guests[+d[1] - 1] != null) { act(st, 'guest', { guest: me.guests[+d[1] - 1] }); return true; }
      return false;
    }
    if (me.must !== 'act') return false;
    const d = /^Digit([1-5])$/.exec(code) || (/^[1-5]$/.test(k) ? [k, k] : null);
    if (d) {
      const g = M.C.GOODS[+d[1] - 1];
      return e.shiftKey ? subGood(root, st, g) : addGood(root, st, g);
    }
    if (k === 'Enter') {
      if (M.K.total(st.pick)) return doTake(root, st) || true;
      if (st.sel && st.sel.k === 'card') return doBuy(root, st, st.sel.id) || true;
      if (st.sel && st.sel.k === 'deck') return doReserve(root, st) || true;
      return false;
    }
    if (code === 'KeyB' || k === 'b' || k === 'B' || k === 'и' || k === 'И') return doReserve(root, st);
    if (k === 'ArrowRight' || k === 'ArrowDown') return moveSel(root, st, 1);
    if (k === 'ArrowLeft' || k === 'ArrowUp') return moveSel(root, st, -1);
    return false;
  }

  // ─── Реєстрація ─────────────────────────────────────────────────────────
  HGames.register({
    id: 'sorochyntsi',
    icon: '🧺',
    seatClass: ['s0', 's1', 's2', 's3'],
    added: '2026-10-28',
    /// Вийшов посеред партії й дивлюсь збоку — кнопка в шапці столу садить назад на своє місце (core.js rejoinLabel).
    rejoin: (rv, nick) => (ownAway(rv && rv.view, nick) ? REJOIN : ''),

    mount(root, ctx) { cur = root; paint(root, ctx); },
    update(root, ctx) { cur = root; paint(root, ctx); },
    unmount(root) {
      const st = root._so;
      if (st) {
        clearInterval(st.tick); clearTimeout(st.nt); clearTimeout(st.flashT); clearTimeout(st.banT); clearTimeout(st.fxT); clearTimeout(st.feedT);
        clearTimeout(st.popT); clearTimeout(st.noteT);
        if (st.ro) st.ro.disconnect();
        if (st.ro2) st.ro2.disconnect();
        if (st.onClick) root.removeEventListener('click', st.onClick);
        if (st.sc) { try { st.sc.dispose(); } catch (e) { /* уже прибрано */ } }
        if (st.snd) { try { st.snd.dispose(); } catch (e) { /* уже прибрано */ } st.snd = null; }
        stopBlink(st);
        st.queue.length = 0;
      }
      root._so = null;
      if (cur === root) cur = null;
    },
    onKey(e) { return cur && cur._so ? keyDown(cur, e) : false; },
    status(ctx) {
      const room = (ctx && ctx.room) || {};
      if (room.status === 'lobby') {
        let people = 0;
        for (let i = 0; i < 4; i++) if (ctx.nickOf && ctx.nickOf(i)) people++;
        const bots = +((room.options || {}).bots || 0);
        return people + Math.min(bots, 4 - people) < 2 ? 'Самому нема з ким торгувати: поклич когось або додай 🤖 Глека' : '';
      }
      const v = ctx && ctx.view;
      if (!v || !v.players || !M) return '';
      const st = cur && cur._so;
      return whatNow(v, v.me || null, st || null).s;
    },
    pad: {
      dirs: false,
      when: (ctx) => ctx.mine && ctx.playing,
      hint: '{a} обрати · {x} бронь · {y} скинути',
      on(btn) {
        const root = cur, st = root && root._so, v = st && st.view;
        if (!st || !v || !v.me || !v.me.must || !M) return false;
        if (btn === 'x') return doReserve(root, st) || true;
        if (btn === 'y') { clearSel(st); paint(root, st.ctx); return true; }
        if (btn === 'lb' || btn === 'rb') { moveSel(root, st, btn === 'lb' ? -1 : 1); return true; }
        return false;   // Ⓐ — кільце тисне кнопку чи підсвічене на полі під собою
      },
    },
  });
})();
