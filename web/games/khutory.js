/*
  «Хутори» — Катан по-нашому на 2–4 (id khutory). Правила живуть на сервері (Impl/Khutory*.cs) — тут лише картка:
  що бачить і натискає гравець. Контракт виду й дій — docs/games/specs/khutory.md (§5–§10, §12).

  Поле — 3D-сцена (/games/khutory/scene.js) або пласка SVG-карта (/games/khutory/board2d.js) з тим самим API;
  обидві вантажаться через import(). Сталі — /games/khutory/consts.js, чисті розрахунки — /games/khutory/hud-calc.js.

  Головне — новачок без правил у кожну мить бачить, що робити: смуга «що зараз» над полем, світлі місця на полі,
  підказка «Хата тут дасть …», ціни кружечками з тим, чого бракує, і готові пропозиції (скидання, банк).
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M8 1.2 14 4.6v6.8L8 14.8 2 11.4V4.6Z" fill="var(--panel2)" stroke="var(--ok)" stroke-width="1.3" stroke-linejoin="round"/>'
    + '<path d="M5 11V8.2L8 5.8l3 2.4V11Z" fill="var(--accent)" stroke="var(--accent-ink, #2a1e03)" stroke-width=".8" stroke-linejoin="round"/></svg>';

  const RES = ['wood', 'clay', 'wool', 'grain', 'ore'];
  const RES_ICON = { wood: '🪵', clay: '🧱', wool: '🧶', grain: '🌾', ore: '⛏️' };
  const DEV_ICON = { knight: '⚔️', vp: '💰', road: '🛤', plenty: '🌻', mono: '🎪' };
  const BUILD_ICON = { road: '🛤', town: '🏠', city: '🏛', dev: '📜' };

  let M = null;           // { C: consts, K: hud-calc, S: sound | null } — після load()
  let loading = null;
  let cur = null;         // root змонтованої картки (для onKey)

  function load() {
    if (!loading) {
      // Звук — не обов'язковий: не завантажився — граємо мовчки.
      loading = Promise.all([import('/games/khutory/consts.js'), import('/games/khutory/hud-calc.js'), import('/games/khutory/sound.js').catch(() => null)])
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

  // ─── Хто є хто ───────────────────────────────────────────────────────────
  const playerOf = (v, seat) => ((v && v.players) || []).find((p) => p.seat === seat) || null;
  const nickOf = (v, seat) => { const p = playerOf(v, seat); return p ? p.nick : 'місце ' + (seat + 1); };
  const colorOf = (v, seat) => {
    const p = playerOf(v, seat);
    const pal = M ? M.C.SEAT_COLOR : ['#d64541', '#3c78d8', '#f1ece0', '#c86fb5'];
    return pal[p ? p.color : seat] || '#999';
  };
  const isBot = (v, seat) => { const p = playerOf(v, seat); return !!(p && (p.bot || p.away)); };
  const sameNick = (a, b) => String(a || '').toLowerCase() === String(b || '').toLowerCase();
  /// Мій хутір, за який ходить Глек, поки я дивлюсь збоку (випав за Grace чи встав): сервер тримає крісло (Game.Rejoin).
  const ownAway = (v, nick) => (v && v.phase !== 'over' && !v.over && nick
    && ((v.players || []).find((p) => !p.bot && p.away && sameNick(p.nick, nick)))) || null;
  const REJOIN = '🏡 Повернутись на свій хутір';
  const dot = (v, seat) => '<i class="khu-pdot" style="--pc:' + colorOf(v, seat) + '"></i>';
  const who = (v, seat) => dot(v, seat) + '<b>' + esc(nickOf(v, seat)) + '</b>';

  function state(root) {
    return root._kh || (root._kh = {
      built: false, ctx: null, view: null, sc: null, scKind: null, scLoading: false, boardId: null, legalSig: null, legal: null,
      pick: null, arm: null, seq: null, queue: [], running: false,
      trade: { open: false, give: {}, get: {}, to: null }, bankGive: null,
      disc: { key: null, plan: null }, dlg: null, tab: null,
      prevHand: null, pulse: {}, tick: 0, nt: 0, toKey: null, toAt: 0, ro: null, hover: null, marks: [],
    });
  }

  // ─── Що зараз робити (смуга над полем і status) ─────────────────────────
  function whatNow(v, me, st) {
    const w = whatNow0(v, me, st);
    const p = me && v && v.phase !== 'over' ? playerOf(v, me.seat) : null;
    if (p && p.away) w.l = '<b>🤖 За тебе зараз ходить Глек</b> (ти двічі проспав хід). Зроби будь-що сам — і гратимеш далі.<br>' + w.l;
    const ctx = st && st.ctx;
    if (!me && ctx && ctx.seat == null && ctx.playing && ownAway(v, ctx.me && ctx.me.nick)) {
      w.s = 'Твій хутір чекає';
      w.l = '<b>🏡 Твій хутір чекає:</b> поки тебе не було, за тебе ходить Глек. Повертайся — рука й грамоти ті самі.'
        + ' <button type="button" class="primary khu-back" data-a="rejoin">' + REJOIN + '</button><br>' + w.l;
    }
    return w;
  }
  function whatNow0(v, me, st) {
    if (!v || !v.players) return { s: '', l: '' };
    // До старту сервер шле вид без гравців (phase setup, turn 0) — це ще збори, а не «хтось ставить хату».
    if (!v.players.length) return { s: '', l: '🪑 Стіл збирається. Сідай на вільне місце — щойно господар натисне «Почати», почнемо ставити хати. Порожні місця займе Глек 🤖, якщо його додали' };
    if (v.phase === 'over' || v.over) {
      const o = v.over || {};
      if (o.winner == null) return { s: 'Партію скінчено', l: '🏁 Партію скінчено' };
      const w = nickOf(v, o.winner);
      const mine = me && me.seat === o.winner;
      return { s: mine ? 'Перемога! 🏆' : 'Переміг ' + w, l: mine ? '🏆 Перемога! Хутори твої' : '🏆 Переміг ' + esc(w) };
    }
    const t = v.turn, tn = esc(nickOf(v, t));
    const think = isBot(v, t) ? ' 🤖 думає…' : '';
    if (me) {
      const m = me.must;
      if (m === 'town') {
        const r = v.setup && v.setup.round === 2;
        return { s: 'Став хату', l: '🏠 Постав ' + (r ? 'другу' : 'першу') + ' хату: клацни світлий кружечок на перехресті.'
          + ' <span class="khu-gold">⭐ Глек радить</span> — де родить найбільше' + (r ? '. Друга хата одразу дасть по карті з сусідніх земель' : '') };
      }
      if (m === 'road' && v.phase === 'setup') return { s: 'Став шлях', l: '🛤 Тепер шлях від щойно поставленої хати: клацни світлу смугу поруч' };
      if (m === 'road') return { s: 'Толока: став шлях', l: '🛤 Толока — шляхи задарма: клацни світлу смугу' };
      if (m === 'roll') return { s: 'Кидай кубики', l: '🎲 Твій хід! Кинь кубики — земля з випалим числом дасть карти всім, у кого там хата' };
      if (m === 'discard') {
        return { s: 'Скинь ' + me.discard + ' ' + plural(me.discard, 'карту', 'карти', 'карт'),
          l: '🃏 Випала сімка, а в тебе понад 7 карт — віддай половину: ' + me.discard };
      }
      if (m === 'robber') return { s: 'Перенеси Кармелюка', l: '🥷 Перенеси Кармелюка: клацни підсвічений гекс. Там числа не родитимуть, а ти забереш карту в сусіда' };
      if (m === 'steal') return { s: 'Обери, кого обібрати', l: '🥷 Кого обібрати? Кармелюк забере одну випадкову карту' };
      if (v.phase === 'discard') {
        const left = Object.keys(v.discard || {}).map((s) => esc(nickOf(v, +s))).join(', ');
        return { s: 'Чекаємо скидання', l: '⏳ Сімка: чекаємо, поки скинуть карти — ' + left };
      }
      if (t === me.seat && v.phase === 'main') {
        if (st && st.pick) {
          const name = { town: 'хати', road: 'шляху', city: 'містечка' }[st.pick];
          return { s: 'Обери місце', l: '👆 Обери місце для ' + name + ' — світлі позначки на полі. Esc або ✕ — передумати' };
        }
        const can = me.can || {}, sp = me.spots || {};
        const ok = ['town', 'city', 'road', 'dev'].filter((k) => can[k] && (k === 'dev' || (sp[k] || []).length));
        const names = { town: 'хату', city: 'містечко', road: 'шлях' };
        const bld = ok.filter((k) => k !== 'dev').map((k) => names[k]);
        const can1 = (bld.length ? 'збудувати ' + bld.join(', ') : '') + (ok.includes('dev') ? (bld.length ? ' або ' : '') + 'купити грамоту 📜' : '');
        if (ok.length) return { s: 'Будуй, торгуй або завершуй хід', l: '🔨 Можеш ' + can1 + '. Або поторгуйся — і «Завершити хід»' };
        return { s: 'Торгуй або завершуй хід', l: '🤝 На будівлі поки бракує — спробуй обмін або завершуй хід' };
      }
    }
    if (t == null) return { s: '', l: '' };
    const doing = {
      setup: v.setup && v.setup.step === 'road' ? 'ставить шлях' : 'ставить хату',
      roll: 'кидає кубики', main: 'будує й торгується', road2: 'кладе Толоку',
      robber: 'переносить Кармелюка', steal: 'обирає, кого обібрати', discard: 'чекає, поки скинуть карти',
    }[v.phase] || 'ходить';
    if (!me) return { s: 'Хід ' + nickOf(v, t), l: '👀 Дивишся збоку: ' + tn + ' ' + doing + think };
    return { s: 'Хід ' + nickOf(v, t), l: '⏳ Хід ' + tn + ': ' + doing + think };
  }

  function plural(n, a, b, c) { return M ? M.C.plural(n, a, b, c) : (n === 1 ? a : n >= 2 && n <= 4 ? b : c); }

  // ─── Каркас DOM ─────────────────────────────────────────────────────────
  function build(root, st) {
    // Розкладка (khutory-look.md §7). ПК: плашки липнуть угорі; поле ліворуч на висоту екрана; праворуч колонка не довша
    // за поле — «що зараз» + кубики, смуга чисел, рука одразу під кнопками, вкладки «🤝 Торг · 📜 Журнал · ❔ Правила».
    // Телефон (< 700 px): .khu-main/.khu-side — display: contents, порядок — CSS order; знизу липнуть рука й панель дій.
    root.innerHTML = '<div class="khu">'
      + '<div class="khu-players"></div>'
      + '<div class="khu-sum" hidden></div>'
      + '<div class="khu-main">'
      + '<div class="khu-fieldwrap"><div class="khu-field"></div><div class="khu-marks"></div><div class="khu-tip" hidden></div>'
      + '<div class="khu-rb"></div><div class="khu-fx"></div><div class="khu-feed" aria-live="polite"></div><div class="khu-rbask" role="alertdialog" hidden></div>'
      + '<div class="khu-flash" hidden></div><div class="khu-tools"><button type="button" class="khu-view" data-a="view"></button><button type="button" class="khu-view khu-snd" data-a="snd"></button></div>'
      + '<div class="khu-inc"></div>'
      + '<div class="khu-ask" hidden></div>'
      + '<div class="khu-dlg" hidden></div></div>'
      + '<div class="khu-side"><div class="khu-say" aria-live="polite"></div><div class="khu-act"></div><div class="khu-hand"></div>'
      + '<div class="khu-tabs"><div class="khu-tabh" role="tablist"></div>'
      + '<div class="khu-tp" data-t="trade" role="tabpanel"><div class="khu-trade"></div><div class="khu-offers"></div><p class="khu-note khu-tpe"></p></div>'
      + '<div class="khu-tp" data-t="log" role="tabpanel" hidden><div class="khu-log"></div></div>'
      + '<div class="khu-tp" data-t="help" role="tabpanel" hidden><div class="khu-help"></div></div></div>'
      + '</div></div>'
      + '<div class="khu-incm"></div>'
      + '<div class="khu-bar"></div>'
      + '</div>';
    st.el = {};
    for (const k of ['players', 'sum', 'say', 'fieldwrap', 'field', 'marks', 'tip', 'flash', 'dlg', 'act', 'trade', 'offers', 'log', 'help', 'hand', 'view', 'snd', 'tabs', 'tabh', 'tpe', 'bar'])
      st.el[k] = root.querySelector('.khu-' + k);
    st.el.tps = [...root.querySelectorAll('.khu-tp')];
    // Смуга «що дасть кожне число» — під рукою (рука — одразу під кнопками ходу, §7); поповер значків плашки — над картою.
    st.el.hand.insertAdjacentHTML('afterend', '<div class="khu-nums" hidden></div>');
    st.el.nums = root.querySelector('.khu-nums');
    st.el.nums.addEventListener('click', (e) => onNums(root, e));
    st.el.players.insertAdjacentHTML('afterend', '<div class="khu-pop" role="tooltip" hidden></div>');
    st.el.pop = root.querySelector('.khu-pop');
    st.el.players.parentElement.addEventListener('click', (e) => onPlateTap(root, e));
    for (const k of ['inc', 'incm', 'ask']) st.el[k] = root.querySelector('.khu-' + k);
    // ask — «🔊 Увімкнути звук?»; rbask — перепитування «тут і твоя хата» перед Кармелюком.
    for (const k of ['rb', 'fx', 'feed', 'rbask']) st.el[k] = root.querySelector('.khu-' + k);
    st.el.rbask.addEventListener('click', (e) => onAsk(root, e));
    // Звук: типово вимкнено, стан пам'ятає сам модуль; AudioContext — лише на першому play() при ввімкненому.
    try { st.snd = M.S && M.S.createSound ? M.S.createSound() : null; } catch (e) { st.snd = null; }
    paintSnd(st);
    st.onClick = (e) => onClick(root, e);
    root.addEventListener('click', st.onClick);
    st.ro = typeof ResizeObserver === 'function' ? new ResizeObserver(() => { if (st.sc) st.sc.resize(); placeMarks(st); }) : null;
    if (st.ro) st.ro.observe(st.el.field);
    // Висоти плашок (ПК: липка смуга й висота поля), руки й нижньої панелі (телефон: липнуть знизу) — у змінні картки.
    st.ro2 = typeof ResizeObserver === 'function' ? new ResizeObserver(() => sizeVars(root, st)) : null;
    if (st.ro2) for (const k of ['players', 'hand', 'bar']) st.ro2.observe(st.el[k]);
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
  /// Останні 10 с МОГО строку — «тік» раз на секунду (лише коли звук увімкнено: вимкнений play мовчить).
  function tickSound(st, v) {
    const me = v.me;
    if (!st.snd || !me || !v.deadline || v.phase === 'over') return;   // вимкнено — play сам мовчить (дешево)
    const mine = !!me.must || (v.turn === me.seat && (v.phase === 'main' || v.phase === 'road2'));
    if (!mine) return;
    const left = Date.parse(v.deadline) - Date.now();
    if (!(left > 0 && left <= 10000)) return;
    // Ключ — секунда до строку (не рядок deadline: легкий і повний вид можуть писати час по-різному).
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
      quality: coarse() ? 'low' : 'high',
      reducedMotion: reduced(),
    };
    let sc = null, kind = null;
    if (store.get('kh.view') !== '2d') {
      try {
        const m = await import('/games/khutory/scene.js');
        sc = await m.createScene(host, opts);
        kind = '3d';
      } catch (e) {
        console.warn('Хутори: 3D не вийшло — малюю пласку карту', e);
        host.textContent = '';
        st.no3d = true;
      }
    }
    if (!sc) {
      try {
        const m = await import('/games/khutory/board2d.js');
        sc = await m.createBoard2d(host, opts);
        kind = '2d';
      } catch (e) {
        st.scLoading = false;
        host.innerHTML = '<p class="khu-err">Не вдалося намалювати поле. Онови сторінку.</p>';
        throw e;
      }
    }
    st.scLoading = false;
    if (root._kh !== st) { sc.dispose(); return; }   // картку вже зняли, поки вантажилось
    st.sc = sc; st.scKind = kind; st.boardId = null; st.legalSig = null;
    paint(root, st.ctx);
  }

  function switchView(root, st) {
    const to = st.scKind === '3d' ? '2d' : '3d';
    store.set('kh.view', to);
    st.no3d = false;
    if (st.sc) { try { st.sc.dispose(); } catch (e) { /* уже прибрано */ } }
    st.sc = null; st.el.field.textContent = '';
    hideTip(st);
    ensureScene(root, st);
  }

  function legalFor(v, me, st) {
    if (!me || !v) return null;
    const sp = me.spots || {};
    if (me.must === 'town') return { kind: 'town', v: sp.town || [], must: true };
    if (me.must === 'road') return { kind: 'road', e: sp.road || [], must: true };
    if (me.must === 'robber') return { kind: 'robber', h: me.robber || [], must: true };
    if (st.pick && v.phase === 'main' && v.turn === me.seat) {
      const list = sp[st.pick] || [];
      if (!list.length) { st.pick = null; return null; }
      return st.pick === 'road' ? { kind: 'road', e: list } : { kind: st.pick, v: list };
    }
    st.pick = null;
    return null;
  }

  function syncScene(root, st) {
    const v = st.view, sc = st.sc;
    if (!sc || !v || !v.board) return;
    if (v.board.id !== st.boardId) { sc.setBoard(v.board); st.boardId = v.board.id; }
    // Кидок ще не зіграно (черга анімацій зайнята) — кубики лишаємо старі, інакше сцена спершу покаже результат, а тоді покотить.
    if (rollHeld(st)) sc.setState(Object.assign({}, v, { dice: st.diceShown || null }));
    else { sc.setState(v); st.diceShown = v.dice || null; }
    const L = legalFor(v, v.me, st);
    const sig = L ? JSON.stringify(L) : '';
    if (sig !== st.legalSig) {
      st.legalSig = sig; st.legal = L; st.arm = null;
      sc.setLegal(L ? { kind: L.kind, v: L.v || [], e: L.e || [], h: L.h || [] } : { kind: null });
      hideTip(st);
      closeAsk(st);
    }
    st.fxRob = v.robber;   // звідки стрибатиме Кармелюк у наступній події (дуга, §8)
    robBadges(st, v);
    // «Глек радить» — 3 найкращі вершини в розстановці.
    st.marks = [];
    if (v.me && v.me.must === 'town' && v.phase === 'setup') {
      const have = {};
      for (const t of v.towns || []) if (t.seat === v.me.seat) for (const it of M.K.vertexYield(v.board, t.v).items) have[it.res] = 1;
      st.marks = M.K.adviseTowns(v.board, (v.me.spots || {}).town || [], 3, Object.keys(have).length ? have : null);
    }
    // Позначки — лише зірки (підпис «⭐ Глек радить» — у смузі над полем): довгий напис ховав сусідню зірку.
    setHtml(st.el.marks, st.marks.map((id, i) => '<span class="khu-mark' + (i === 0 ? ' best' : '') + '" data-i="' + i + '" title="Глек радить">⭐</span>').join(''));
    placeMarks(st);
    followMarks(st);
  }

  const rollHeld = (st) => !!st.rolling || st.queue.some((e) => e.k === 'roll');

  /// Поки є позначки (зірки, бейджі Кармелюка, подія на полі) — ставимо їх щокадру (камеру крутять мишею/пальцем;
  /// таймер на 250 мс відставав). Пишемо лише transform/атрибут і лише коли змінився — без innerHTML щокадру.
  const following = (st) => !!(st.marks.length || st.rob || st.fxCur);
  function followMarks(st) {
    if (st.mrAF || !following(st)) return;
    const loop = () => {
      st.mrAF = 0;
      if (!st.el || !following(st) || !st.sc) return;
      placeMarks(st);
      st.mrAF = requestAnimationFrame(loop);
    };
    st.mrAF = requestAnimationFrame(loop);
  }

  function placeMarks(st) {
    if (!st.sc || !st.el) return;
    const kids = st.el.marks.children;
    st.marks.forEach((id, i) => {
      const p = st.sc.screenPos('v', id), m = kids[i];
      if (!m) return;
      const W = st.el.fieldwrap.clientWidth, H = st.el.fieldwrap.clientHeight;
      // Поза полем (камеру відвели) — ховаємо, а не вилазимо за край.
      if (!p || p.x < 8 || p.x > W - 8 || p.y < 28 || p.y > H) { m.hidden = true; return; }
      m.hidden = false;
      m.style.transform = 'translate(' + Math.round(p.x) + 'px,' + Math.round(p.y - 16) + 'px) translate(-50%,-100%)';
    });
    placeRob(st);
    placeFx(st);
  }

  // ─── Клік по полю й підказка ────────────────────────────────────────────
  function onPick(root, kind, id) {
    const st = root._kh;
    if (!st || !st.legal || !st.ctx) return;
    const L = st.legal;
    const want = L.kind === 'road' ? 'e' : L.kind === 'robber' ? 'h' : 'v';
    if (kind !== want) return;
    // На дотику перший тиц — показати, що дасть місце; другий тиц туди ж — поставити.
    if (coarse() && L.kind !== 'road' && !(st.arm && st.arm.id === id)) {
      st.arm = { kind, id };
      showTip(st, kind, id, true);
      return;
    }
    st.arm = null;
    hideTip(st);
    // Своя хата на гексі — не забороняємо (правила дозволяють), а перепитуємо (§2).
    if (L.kind === 'robber' && (st.rob || []).some((x) => x.h === id && x.own)) { robAsk(st, id); return; }
    if (L.kind === 'road') st.ctx.act('road', { e: id });
    else if (L.kind === 'robber') st.ctx.act('robber', { h: id });
    else st.ctx.act(L.kind, { v: id });
    if (!L.must) { st.pick = null; paint(root, st.ctx); }
  }

  function onHover(root, kind, id) {
    const st = root._kh;
    if (!st) return;
    if (kind == null) { if (!st.arm) hideTip(st); return; }
    if (st.arm && st.arm.id !== id) st.arm = null;
    showTip(st, kind, id, false);
  }

  function tipText(st, kind, id) {
    const v = st.view, L = st.legal;
    if (!v || !L || !M) return '';
    const K = M.K, C = M.C;
    if (kind === 'v' && (L.kind === 'town' || L.kind === 'city')) {
      const y = K.vertexYield(v.board, id, v.robber);
      const port = K.portAt(v.board, id);
      let s = L.kind === 'city' ? '🏛 Містечко родитиме вдвічі: ' : '🏠 Хата тут дасть: ';
      s += esc(K.yieldText(y));
      if (port) s += ' · ⚓ пристань ' + (port.kind === 'any' ? '3:1' : '2:1 (' + C.RES_NAME[port.kind] + ')');
      if (st.marks.includes(id)) s += ' · <span class="khu-gold">⭐ Глек радить</span>';
      return s;
    }
    if (kind === 'h' && L.kind === 'robber') {
      const hx = v.board.hexes[id];
      const vs = new Set(hx.v);
      const hit = [...new Set((v.towns || []).filter((t) => vs.has(t.v) && (!v.me || t.seat !== v.me.seat)).map((t) => t.seat))];
      let s = '🥷 ' + esc(C.TERRAIN_NAME[hx.t]) + (hx.num ? ' ' + hx.num : '') + ' · ';
      s += hit.length ? 'зачепить: ' + hit.map((x) => esc(nickOf(v, x)) + ' (' + ((playerOf(v, x) || {}).cards || 0) + ' 🂠)').join(', ') : 'нікого не зачепить';
      return s;
    }
    if (kind === 'e' && L.kind === 'road') return '🛤 Шлях сюди';
    return '';
  }

  function showTip(st, kind, id, armed) {
    const tip = st.el && st.el.tip;
    if (!tip || !st.sc) return;
    const html = tipText(st, kind, id);
    if (!html) { hideTip(st); return; }
    tip.innerHTML = html + (armed ? '<br><b class="khu-gold">Тицни ще раз — поставити</b>' : '');
    tip.hidden = false;
    st.hover = { kind, id };
    const p = st.sc.screenPos(kind, id);
    if (!p) return;
    const W = st.el.fieldwrap.clientWidth;
    const w = tip.offsetWidth;
    const x = Math.max(w / 2 + 6, Math.min(W - w / 2 - 6, p.x));
    const below = p.y < tip.offsetHeight + 34;
    tip.style.left = Math.round(x) + 'px';
    tip.style.top = Math.round(below ? p.y + 26 : p.y - 22) + 'px';
    tip.classList.toggle('below', below);
  }
  function hideTip(st) { if (st.el && st.el.tip) { st.el.tip.hidden = true; st.hover = null; } }

  // ─── Малювання ──────────────────────────────────────────────────────────
  function paint(root, ctx) {
    const st = state(root);
    st.ctx = ctx;
    st.view = ctx && ctx.view && ctx.view.players ? ctx.view : null;
    if (!M) {
      if (!root.firstChild) root.innerHTML = '<p class="khu-wait muted">Розкладаємо хутори…</p>';
      load().then(() => { if (root._kh === st) paint(root, st.ctx); }, (e) => { root.innerHTML = '<p class="khu-err">Не вдалося завантажити гру. Онови сторінку.</p>'; console.warn(e); });
      return;
    }
    if (!st.built) build(root, st);
    const v = st.view;
    if (!v) { setHtml(st.el.say, '<b>Чекаємо на початок…</b>'); return; }
    const me = v.me || null;
    if (me && me.must !== 'discard') { st.disc.key = null; st.disc.hidden = false; }
    if (st.dlg && !(me && v.turn === me.seat && v.phase === 'main')) st.dlg = null;
    if (st.trade.open && !(me && v.phase === 'main')) st.trade.open = false;

    pulseHand(st, me);
    const w = whatNow(v, me, st);
    setHtml(st.el.say, '<div class="khu-say-t">' + w.l + '</div>' + (st.pick ? '<button type="button" class="khu-x" data-a="pick-cancel">✕ Передумав</button>' : ''));
    st.el.say.hidden = !!v.over;
    st.el.say.classList.toggle('mine', !!(me && (me.must || v.turn === me.seat) && v.phase !== 'over'));
    renderPlayers(st, v, me);
    renderAct(st, v, me);
    renderTrade(st, v, me);
    renderOffers(st, v, me);
    renderLog(st, v);
    renderHand(st, v, me);
    renderDlg(st, v, me);
    renderInc(st, v, me);
    attention(st, v, me);
    renderSum(st, v);
    renderNumbers(st, v, me);
    st.el.view.textContent = st.scKind === '3d' ? '🗺 Пласка' : '🧊 3D';
    renderTabs(st, v, me);
    renderBar(st, v, me);
    st.el.view.title = st.scKind === '3d' ? 'Показати пласку карту (легша для слабких пристроїв)' : (st.no3d ? '3D не вдалося — спробувати ще' : 'Показати об\'ємне поле');
    queueEvents(root, st, v);   // до syncScene: кидок у черзі притримує кубики сцени
    if (!st.sc) ensureScene(root, st);
    else syncScene(root, st);
    nudge(root, st);
  }

  function pulseHand(st, me) {
    const now = Date.now();
    if (me && me.hand && st.prevHand) {
      for (const r of RES) if ((me.hand[r] || 0) > (st.prevHand[r] || 0)) st.pulse[r] = now + 1800;
    }
    st.prevHand = me && me.hand ? { ...me.hand } : null;
  }

  function costDots(k, hand) {
    const cost = M.C.COST[k] || {};
    let h = '';
    for (const r of RES) {
      const n = cost[r] || 0, have = (hand && hand[r]) || 0;
      for (let i = 0; i < n; i++) {
        h += '<i class="khu-dot' + (i >= have ? ' miss' : '') + '" style="--rc:' + M.C.RES_COLOR[r] + '" title="' + M.C.RES_NAME[r] + (i >= have ? ' — бракує' : '') + '">' + RES_ICON[r] + '</i>';
      }
    }
    return h;
  }

  function diceHtml(d) {
    if (!d) return '';
    const cell = { 1: [4], 2: [0, 8], 3: [0, 4, 8], 4: [0, 2, 6, 8], 5: [0, 2, 4, 6, 8], 6: [0, 2, 3, 5, 6, 8] };
    const die = (n, red) => '<span class="khu-die' + (red ? ' red' : '') + '">' + [0, 1, 2, 3, 4, 5, 6, 7, 8].map((i) => '<i' + (cell[n].includes(i) ? ' class="on"' : '') + '></i>').join('') + '</span>';
    return die(d[0]) + die(d[1], true) + '<b class="khu-dsum' + (d[0] + d[1] === 7 ? ' seven' : '') + '">= ' + (d[0] + d[1]) + '</b>';
  }

  /// Пояснення значків плашки — поповер на дотик (title на телефоні не видно).
  const PLATE_TIPS = {
    pts: (v) => '⭐ <b>Очки.</b> Хата — 1, містечко — 2, «Чумацький шлях» і «Найсильніша сотня» — по 2, скарб — 1. Хто першим набере ' + v.target + ' у свій хід — переміг.',
    cards: () => '🂠 <b>Карти в руці.</b> Понад 7 — на сімці Кармелюк забере половину (🥷).',
    dev: () => '📜 <b>Грамоти:</b> козак, скарб, Толока, Врожайний рік, Ярмарок. Що саме — бачить лише власник.',
    road: (v) => '🛤 <b>Чумацький шлях</b> (+2 ⭐): найдовший безперервний шлях — щонайменше 5 відтинків і довший, ніж у власника'
      + (v.longest ? ' (зараз ' + esc(nickOf(v, v.longest.seat)) + ', ' + v.longest.len + ')' : '') + '.',
    army: (v) => '⚔ <b>Найсильніша сотня</b> (+2 ⭐): щонайменше 3 зіграні козаки і більше, ніж у власника'
      + (v.army ? ' (зараз ' + esc(nickOf(v, v.army.seat)) + ', ' + v.army.n + ')' : '') + '.',
    port: () => '⚓ <b>Пристані</b> біля хат: «3:1» — будь-які 3 однакові на 1, «2:1» — свій ресурс 2 на 1.',
    shape: () => '●▲■◆ <b>Фігура гравця</b> — поруч із кольором скрізь: на плашці, на полі, у підсумку.',
  };

  function renderPlayers(st, v, me) {
    const C = M.C, K = M.K;
    const thinking = new Set();
    if (v.phase === 'discard' && v.discard) for (const s of Object.keys(v.discard)) thinking.add(+s);
    else if (v.turn != null && v.phase !== 'over') thinking.add(v.turn);
    const L = 2 * Math.PI * 19;
    const tip = (k, html, title) => '<span class="khu-ic" data-tip="' + k + '" title="' + esc(title) + '">' + html + '</span>';
    const h = v.players.map((p) => {
      const mine = me && me.seat === p.seat;
      const fin = v.over && v.over.points ? v.over.points[p.seat] : null;
      const pts = fin != null ? fin : mine && me.points != null ? me.points : p.points;
      const secret = fin == null && mine && me.points > p.points ? '<small title="Скарби в руці бачиш лише ти">+' + (me.points - p.points) + ' потай</small>' : '';
      const aw = K.awardNeed(v.longest, v.army, p);
      const bar = (a) => '<span class="khu-mb"><i style="width:' + a.pct + '%"></i></span>';
      const award = (k, icon, name, a) => a.has
        ? tip(k, '<span class="khu-aw has">' + icon + ' <span class="khu-w">' + name + ' </span>+2</span>', name + ': +2 очки')
        : tip(k, '<span class="khu-aw' + (a.near ? ' near' : '') + '">' + icon + ' ' + a.n + '/' + a.need + bar(a) + '</span>',
          name + ': ' + a.n + ' з ' + a.need + ' потрібних');
      const ports = K.seatPorts(v.board, v.towns, p.seat).map((k) => k === 'any' ? '⚓3:1' : RES_ICON[k] + '2:1');
      const think = thinking.has(p.seat);
      const ci = p.color != null ? p.color : p.seat;
      const light = !!(C.SEAT_LIGHT && C.SEAT_LIGHT[ci]);
      const name = esc(p.nick) + (p.bot ? '' : p.away ? ' <span title="Вийшов або проспав — за нього грає Глек">🤖</span>' : '');
      return '<div class="khu-pl' + (v.turn === p.seat && v.phase !== 'over' ? ' turn' : '') + (mine ? ' me' : '') + (light ? ' light' : '') + (think && v.deadline ? ' think' : '')
        + '" data-seat="' + p.seat + '" style="--pc:' + colorOf(v, p.seat) + '">'
        + '<span class="khu-av"><svg viewBox="0 0 44 44" aria-hidden="true"><circle class="bg" cx="22" cy="22" r="19"/>'
        + (think && v.deadline ? '<circle class="fg" cx="22" cy="22" r="19" transform="rotate(-90 22 22)" stroke-dasharray="' + L.toFixed(1) + '"/>' : '')
        + '</svg><b>' + (p.bot ? '🤖' : esc(String(p.nick || '?').trim().charAt(0).toUpperCase())) + '</b>'
        + '<span class="khu-shp" data-tip="shape" title="Фігура гравця">' + ((C.SEAT_SHAPE || [])[ci] || '●') + '</span></span>'
        + '<span class="khu-pn">' + name + (mine ? ' <em>(ти)</em>' : '') + '</span>'
        + '<span class="khu-pp">' + tip('pts', '⭐ <b>' + pts + '</b><span class="khu-w"> з ' + v.target + '</span>', 'Очки (до ' + v.target + ')')
        + '<span class="khu-mb gold"><i style="width:' + Math.min(100, Math.round(pts / (v.target || 10) * 100)) + '%"></i></span>' + secret + '</span>'
        + '<span class="khu-ps">' + tip('cards', '🂠 <b>' + p.cards + '</b><span class="khu-w"> ' + plural(p.cards, 'карта', 'карти', 'карт') + '</span>'
          + (p.cards > 7 ? '<span class="khu-warn">🥷</span>' : ''), p.cards + ' ' + plural(p.cards, 'карта', 'карти', 'карт') + ' у руці' + (p.cards > 7 ? ' — понад 7, світить Кармелюк' : ''))
        + tip('dev', '📜 <b>' + p.dev + '</b><span class="khu-w"> ' + plural(p.dev, 'грамота', 'грамоти', 'грамот') + '</span>', p.dev + ' ' + plural(p.dev, 'грамота', 'грамоти', 'грамот'))
        + '</span>'
        + '<span class="khu-ps khu-aws">' + award('road', '🛤', 'Шлях', aw.road) + award('army', '⚔', 'Сотня', aw.army)
        + (ports.length ? tip('port', '<span class="khu-port">' + ports.join(' ') + '</span>', 'Пристані: ' + ports.join(', ')) : '') + '</span>'
        + (think && v.deadline ? '<span class="khu-sec" data-sec></span><span class="khu-tm"><i></i></span>' : '')
        + '</div>';
    }).join('');
    setHtml(st.el.players, '<div class="khu-target" title="Хто першим набере стільки очок у свій хід — переміг">до ' + v.target + ' ⭐</div>' + h);
    tickArcs(st);
  }

  /// Дотик по значку плашки (🂠/📜/🛤/⚔/⭐/⚓/фігура) — коротке пояснення під ним; дотик деінде — сховати.
  function onPlateTap(root, e) {
    const st = root._kh;
    if (!st || !st.el || !st.el.pop) return;
    const pop = st.el.pop;
    const t = e.target.closest('[data-tip]');
    if (!t || !st.el.players.contains(t) || !st.view) { if (!pop.hidden) pop.hidden = true; return; }
    const f = PLATE_TIPS[t.dataset.tip];
    if (!f) return;
    if (!pop.hidden && pop._for === t.dataset.tip + t.closest('[data-seat]').dataset.seat) { pop.hidden = true; return; }
    pop._for = t.dataset.tip + t.closest('[data-seat]').dataset.seat;
    pop.innerHTML = f(st.view);
    pop.hidden = false;
    const host = st.el.players.parentElement.getBoundingClientRect(), r = t.getBoundingClientRect();
    const w = Math.min(260, host.width - 16);
    pop.style.width = w + 'px';
    pop.style.left = Math.max(8, Math.min(host.width - w - 8, r.left - host.left + r.width / 2 - w / 2)) + 'px';
    pop.style.top = (r.bottom - host.top + 6) + 'px';
    clearTimeout(st.popT);
    st.popT = setTimeout(() => { pop.hidden = true; }, 6000);
  }

  function renderAct(st, v, me) {
    let h = '';
    if (v.phase === 'over' || v.phase === 'setup') { setHtml(st.el.act, ''); return; }
    const myTurn = me && v.turn === me.seat;
    const dice = rollHeld(st) ? null : v.dice;
    h += '<div class="khu-dice">' + (dice ? diceHtml(dice) : '<span class="muted">' + (v.dice ? '🎲 котяться…' : 'кубики ще не кинуто') + '</span>') + '</div>';
    if (me && me.must === 'roll') {
      h += '<button type="button" class="khu-big primary" data-a="roll">🎲 Кинути кубики</button>';
      if ((me.dev || []).some((d) => d.k === 'knight' && !d.fresh) && !me.playedDev) h += '<p class="khu-note">Можна спершу зіграти ⚔️ Козака з руки — прогнати Кармелюка зі своєї землі</p>';
    } else if (myTurn && v.phase === 'main') {
      const can = me.can || {}, why = me.why || {}, sp = me.spots || {};
      const left = (playerOf(v, me.seat) || {}).left || {};
      h += '<div class="khu-builds">';
      for (const k of ['road', 'town', 'city', 'dev']) {
        const room = k === 'dev' || (sp[k] || []).length > 0;
        const ok = !!can[k] && room;
        const reason = why[k] || (!room && can[k] ? 'нема де поставити' : !can[k] && k === 'dev' && !v.devLeft ? 'грамоти скінчились' : '');
        const on = st.pick === k;
        // «🔁 обміняй і будуй»: сірій будівлі бракує 1–2 карт, а банк їх покриває (і місце та фігура є).
        const fix = !ok && !on && room && (k === 'dev' ? v.devLeft > 0 : left[k] == null || left[k] > 0) ? M.K.bankFix(me.hand, M.C.COST[k], me.rates) : null;
        const btn = '<button type="button" class="khu-b' + (on ? ' on' : '') + (ok ? ' ok' : '') + '" data-a="build" data-k="' + k + '"' + (ok || on ? '' : ' disabled')
          + ' title="' + esc(k === 'dev' ? 'Грамота: козак, скарб, Толока, Врожайний рік або Ярмарок' : M.C.BUILD_NAME[k]) + '">'
          + '<span class="khu-bn">' + BUILD_ICON[k] + ' ' + (on ? '✕ Скасувати' : M.C.BUILD_NAME[k]) + '</span>'
          + '<span class="khu-cost">' + costDots(k, me.hand) + '</span>'
          + (ok || on ? '<span class="khu-why ok">' + (on ? 'обери місце на полі' : k === 'dev' ? 'купити' : 'можна!') + '</span>' : '<span class="khu-why">' + esc(reason || 'не зараз') + '</span>')
          + '</button>';
        h += fix ? '<div class="khu-bw">' + btn + '<button type="button" class="khu-fix" data-a="fix" data-k="' + k + '"' + (st.fixing ? ' disabled' : '')
          + ' title="Обміняти в банку ' + esc(fix.map((t) => M.K.resPhraseAcc(t.give, t.n * t.rate) + ' на ' + M.K.resPhraseAcc(t.get, t.n)).join(' і ')) + (k === 'dev' ? '' : ' й одразу обрати місце') + '">🔁 '
          + fix.map((t) => t.n * t.rate + RES_ICON[t.give] + '→' + (t.n > 1 ? t.n : '') + RES_ICON[t.get]).join(' ') + (k === 'dev' ? ' обміняй' : ' і будуй') + '</button></div>' : btn;
      }
      h += '</div>';
      h += bankRow(st, v, me);
      h += '<div class="khu-row"><button type="button" class="khu-mid' + (st.trade.open ? ' on' : '') + '" data-a="trade">🤝 Торгувати' + incBadge(v, me) + '</button>'
        + '<button type="button" class="khu-mid primary" data-a="end">✅ Завершити хід</button></div>';
    } else if (me && v.phase === 'main' && v.turn != null && !isBot(v, v.turn) && v.rolled !== false) {
      h += '<button type="button" class="khu-mid' + (st.trade.open ? ' on' : '') + '" data-a="trade">🤝 Запропонувати обмін ' + esc(nickOf(v, v.turn)) + incBadge(v, me) + '</button>';
    } else if (me && me.must === 'discard') {
      h += '<button type="button" class="khu-big primary" data-a="disc-open">🃏 Скинути карти</button>';
    }
    if (me && v.phase === 'main' && v.turn != null && isBot(v, v.turn) && !myTurn && !incList(v, me).length) h += '<p class="khu-note">🤖 Зараз ходить Глек — він міняє лише з банком. Твоя черга торгуватись — у твій хід</p>';
    setHtml(st.el.act, h);
  }

  function resStepper(side, counts, hand, limitByHand) {
    return '<div class="khu-steps">' + RES.map((r) => {
      const n = counts[r] || 0;
      const max = limitByHand ? (hand[r] || 0) : 19;
      return '<span class="khu-step" style="--rc:' + M.C.RES_COLOR[r] + '"><span class="khu-ri" title="' + M.C.RES_NAME[r] + '">' + RES_ICON[r] + '</span>'
        + '<button type="button" data-a="tr-sub" data-side="' + side + '" data-r="' + r + '"' + (n ? '' : ' disabled') + ' aria-label="менше ' + M.C.RES_GEN[r] + '">−</button>'
        + '<b>' + n + '</b>'
        + '<button type="button" data-a="tr-add" data-side="' + side + '" data-r="' + r + '"' + (n < max ? '' : ' disabled') + ' aria-label="більше ' + M.C.RES_GEN[r] + '">+</button></span>';
    }).join('') + '</div>';
  }

  function renderTrade(st, v, me) {
    const T = st.trade;
    if (!T.open || !me) { setHtml(st.el.trade, ''); return; }
    const K = M.K;
    const myTurn = v.turn === me.seat;
    const hand = me.hand || {};
    const give = K.compact(T.give), get = K.compact(T.get);
    const ready = Object.keys(give).length && Object.keys(get).length;
    let h = '<div class="khu-box"><div class="khu-boxh">🤝 ' + (myTurn ? 'Обмін із сусідами' : 'Зустрічна для ' + esc(nickOf(v, v.turn)))
      + '<button type="button" class="khu-x" data-a="trade" aria-label="Закрити">✕</button></div>';
    h += '<div class="khu-trl">Даю</div>' + resStepper('give', T.give, hand, true);
    h += '<div class="khu-trl">Хочу</div>' + resStepper('get', T.get, hand, false);
    if (myTurn) {
      const others = v.players.filter((p) => p.seat !== me.seat);
      h += '<div class="khu-to">Кому: <button type="button" class="khu-chip' + (T.to == null ? ' on' : '') + '" data-a="tr-to" data-to="">Усім</button>'
        + others.map((p) => '<button type="button" class="khu-chip' + (T.to === p.seat ? ' on' : '') + '" data-a="tr-to" data-to="' + p.seat + '">' + whoS(v, p.seat) + '</button>').join('') + '</div>';
    }
    h += '<button type="button" class="khu-mid primary" data-a="offer"' + (ready ? '' : ' disabled') + '>'
      + (ready ? 'Запропонувати: даю ' + esc(K.resListAcc(give)) + ' ⇄ хочу ' + esc(K.resListAcc(get)) : 'Обери, що даєш і що хочеш') + '</button>';
    // Банк — тепер завжди в панелі ходу (bankRow), тут лише нагадка.
    if (myTurn) h += '<p class="khu-note">🏦 З банком — чипами «4🪵→1» у панелі ходу, без пропозицій</p>';
    setHtml(st.el.trade, h + '</div>');
  }

  function renderOffers(st, v, me) {
    const list = v.offers || [];
    if (!list.length) { setHtml(st.el.offers, ''); return; }
    const K = M.K;
    const hand = (me && me.hand) || {};
    const h = list.map((o) => {
      const mine = me && o.from === me.seat;
      const fromCur = o.from === v.turn;
      const toMe = me && !mine && fromCur && (o.to == null || o.to === me.seat);
      const counterToMe = me && !mine && !fromCur && me.seat === v.turn;
      let s = '<div class="khu-offer' + (toMe || counterToMe ? ' forme' : '') + '">';
      const acc = o.acc || [], dec = o.dec || [];
      const refused = mine && me.seat === v.turn && K.allRefused(o, v.players.map((p) => p.seat));
      s += '<div class="khu-ot">' + (refused ? '<b>🙅 Усі відмовились</b>' : mine ? '<b>Твоя пропозиція</b>' : whoS(v, o.from) + (fromCur ? '' : ' <span class="muted">(зустрічна)</span>'))
        + (fromCur ? ' <span class="muted">→ ' + (o.to == null ? 'усім' : esc(nickOf(v, o.to))) + '</span>' : '') + '</div>';
      s += '<div class="khu-od">' + (mine ? 'даєш' : 'дає') + ' <b>' + esc(K.resListAcc(o.give)) + '</b> · ' + (mine ? 'хочеш' : 'хоче') + ' <b>' + esc(K.resListAcc(o.get)) + '</b></div>';
      if (fromCur && (acc.length || dec.length)) {
        s += '<div class="khu-oa">' + acc.map((x) => '<span class="ok">✅ ' + esc(nickOf(v, x)) + '</span>').join(' ') + ' '
          + dec.map((x) => '<span class="no">❌ ' + esc(nickOf(v, x)) + '</span>').join(' ') + '</div>';
      }
      const btn = [];
      if (refused) {
        // Ніхто не хоче — не «чекаємо» до кінця ходу, а що робити далі.
        const want = Object.keys(K.compact(o.get));
        const bankOk = want.length === 1 && K.bankChips(hand, me.rates).some((c) => c.res !== want[0] && c.max >= 1);
        btn.push('<button type="button" class="primary" data-a="of-edit" data-id="' + o.id + '">✏ Змінити</button>');
        btn.push('<button type="button" data-a="cancel" data-id="' + o.id + '">🗑 Прибрати</button>');
        if (bankOk) btn.push('<button type="button" data-a="of-bank" data-id="' + o.id + '" data-r="' + want[0] + '">🏦 У банк</button>');
      } else if (mine && me.seat === v.turn) {
        for (const x of acc) btn.push('<button type="button" class="primary" data-a="deal" data-id="' + o.id + '" data-seat="' + x + '">🤝 Обміняти з ' + esc(nickOf(v, x)) + '</button>');
        if (!acc.length) btn.push('<span class="muted small">чекаємо, хто погодиться…</span>');
      }
      if (mine && !refused) btn.push('<button type="button" data-a="cancel" data-id="' + o.id + '">Скасувати</button>');
      if (toMe) {
        const lack = K.lacking(hand, o.get);
        const can = !Object.keys(lack).length;
        const said = acc.includes(me.seat) ? 'ok' : dec.includes(me.seat) ? 'no' : '';
        if (said !== 'ok') btn.push('<button type="button" class="primary" data-a="ans" data-id="' + o.id + '" data-ok="1"' + (can ? '' : ' disabled title="бракує ' + esc(K.resList(lack)) + '"') + '>✅ Згода</button>');
        if (said !== 'no') btn.push('<button type="button" data-a="ans" data-id="' + o.id + '" data-ok="0">❌ Ні</button>');
        btn.push('<button type="button" data-a="counter" data-id="' + o.id + '">↩ Зустрічна</button>');
        if (said === 'ok') btn.unshift('<span class="ok small">Ти згоден — чекаємо ' + esc(nickOf(v, o.from)) + '</span>');
        if (!can && said !== 'ok') btn.push('<span class="muted small">бракує ' + esc(K.resList(lack)) + '</span>');
      }
      if (counterToMe) {
        const lack = K.lacking(hand, o.get);
        btn.push('<button type="button" class="primary" data-a="deal" data-id="' + o.id + '" data-seat="' + o.from + '"' + (Object.keys(lack).length ? ' disabled' : '') + '>🤝 Обміняти з ' + esc(nickOf(v, o.from)) + '</button>');
        btn.push('<button type="button" data-a="ans" data-id="' + o.id + '" data-ok="0">❌ Ні</button>');
        if (Object.keys(lack).length) btn.push('<span class="muted small">бракує ' + esc(K.resList(lack)) + '</span>');
      }
      if (btn.length) s += '<div class="khu-ob">' + btn.join('') + '</div>';
      return s + '</div>';
    }).join('');
    setHtml(st.el.offers, '<div class="khu-box"><div class="khu-boxh">📨 Пропозиції</div>' + h + '</div>');
  }

  // ─── Вхідна пропозиція, банк чипами, увага без звуку (khutory-look.md §1, §10, §13) ─────
  /// Гравець із фігурою ●▲■◆ (колір сам не розрізняє всіх); на світлому — темний знак.
  function whoS(v, seat) {
    const p = playerOf(v, seat), ci = p ? p.color : seat;
    const light = M && M.C.SEAT_LIGHT ? M.C.SEAT_LIGHT[ci] : false;
    const shp = (M && M.C.SEAT_SHAPE ? M.C.SEAT_SHAPE[ci] : '') || '●';
    return '<i class="khu-shp' + (light ? ' light' : '') + '" style="--pc:' + colorOf(v, seat) + '" aria-hidden="true">' + shp + '</i><b>' + esc(nickOf(v, seat)) + '</b>';
  }
  const incList = (v, me) => (me && v && v.phase === 'main' ? M.K.incomingOffers(v.offers, me.seat, v.turn) : []);
  const incBadge = (v, me) => { const n = incList(v, me).length; return n ? ' <span class="khu-badge" title="Чекають твоєї відповіді">' + n + '</span>' : ''; };
  const resIcons = (o) => RES.filter((r) => o && o[r] > 0).map((r) => RES_ICON[r] + '&nbsp;' + o[r]).join(' ');

  /// 🏦 Банк завжди під рукою: чипи «4🪵→1» з найкращим курсом; дотик — що береш і скільки (×1/×2…).
  function bankRow(st, v, me) {
    const K = M.K, hand = me.hand || {};
    const ch = K.bankChips(hand, me.rates);
    if (st.bankGive && !ch.some((c) => c.res === st.bankGive && c.max >= 1)) st.bankGive = null;
    let h = '<div class="khu-bankr"><span class="khu-bl" title="Банк: віддай однакові — візьми 1 будь-яку">🏦 Банк</span>'
      + ch.map((c) => '<button type="button" class="khu-bchip' + (st.bankGive === c.res ? ' on' : '') + (c.port ? ' port' : '') + '" data-a="bank-give" data-r="' + c.res + '"'
        + (c.max >= 1 ? '' : ' disabled') + ' title="' + esc(M.C.RES_NAME[c.res]) + ': ' + c.rate + ' на 1' + (c.port ? ' (пристань)' : '') + (c.max >= 1 ? '' : ' — є ' + c.have) + '">'
        + c.rate + RES_ICON[c.res] + '→1' + (c.port ? '<small>⚓</small>' : '') + '</button>').join('') + '</div>';
    if (st.bankGive) {
      const g = ch.find((c) => c.res === st.bankGive), n = Math.min(st.bankN || 1, g.max);
      h += '<div class="khu-bpick"><span class="khu-bl">за ' + n * g.rate + RES_ICON[g.res] + ' бери:</span>'
        + RES.filter((r) => r !== g.res).map((r) => '<button type="button" class="khu-bget' + (st.bankWant === r ? ' want' : '') + '" data-a="bank-get" data-r="' + r + '" style="--rc:' + M.C.RES_COLOR[r] + '"'
          + ' title="' + esc(M.K.resPhraseAcc(r, n)) + '"' + ((v.bank || {})[r] === 0 ? ' disabled' : '') + '>' + RES_ICON[r] + (n > 1 ? '×' + n : '') + '</button>').join('')
        + (g.max > 1 ? [1, 2, 3].filter((x) => x <= g.max).map((x) => '<button type="button" class="khu-bn2' + (x === n ? ' on' : '') + '" data-a="bank-n" data-n="' + x + '">×' + x + '</button>').join('') : '')
        + '</div>';
    }
    return h;
  }

  /// Що показати у вхідній картці: найновіша (чи обрана «ще N ▸») і скільки ще.
  function incPick(st, v, me) {
    const list = incList(v, me);
    if (!list.length) return null;
    let i = list.findIndex((x) => x.o.id === st.incPick);
    if (i < 0) i = 0;
    return { cur: list[i], i, n: list.length, list };
  }

  /// До коли пропозиція жива: строк ходу (deadline) або кінець очікування Глека (botIn, KhutoryBot.OfferWaitMs) — що ближче.
  function incEnd(st, v, o) {
    let end = v.deadline ? Date.parse(v.deadline) : Infinity;
    if (o.from === v.turn && isBot(v, o.from) && st.botAt) end = Math.min(end, st.botAt);
    return end;
  }

  function renderInc(st, v, me) {
    if (st.botV !== v) { st.botV = v; st.botAt = typeof v.botIn === 'number' ? Date.now() + v.botIn : null; }
    const P = incPick(st, v, me);
    st.inc = null;
    if (!P) { setHtml(st.el.inc, ''); setHtml(st.el.incm, ''); return; }
    const { o, kind } = P.cur, K = M.K, hand = me.hand || {};
    st.incSeen = st.incSeen || {};
    if (!st.incSeen[o.id]) st.incSeen[o.id] = Date.now();
    st.inc = { id: o.id, t0: st.incSeen[o.id], end: incEnd(st, v, o) };
    const lack = K.lacking(hand, o.get), can = !Object.keys(lack).length;
    const head = kind === 'counter' ? whoS(v, o.from) + '&nbsp;відповідає зустрічною' : whoS(v, o.from) + '&nbsp;пропонує обмін' + (o.to === me.seat ? ' тобі' : '');
    const bar = st.inc.end < Infinity ? '<div class="khu-ibar"><i data-ibar></i></div>' : '';
    let h;
    if (st.incHid === o.id) {
      h = '<div class="khu-incc mini"><button type="button" class="khu-istrip" data-a="inc-show" aria-label="Розгорнути пропозицію">📨 ' + head
        + ' <span class="khu-ideal1">' + resIcons(o.give) + ' ⇄ ' + resIcons(o.get) + '</span><span class="khu-isec" data-isec></span> ▴</button>' + bar + '</div>';
    } else {
      const after = RES.filter((r) => (o.get[r] || 0) || (o.give[r] || 0))
        .map((r) => RES_ICON[r] + ' ' + (hand[r] || 0) + ' → ' + ((hand[r] || 0) - (o.get[r] || 0) + (o.give[r] || 0))).join(' · ');
      const yes = kind === 'counter'
        ? '<button type="button" class="primary" data-a="deal" data-id="' + o.id + '" data-seat="' + o.from + '"' + (can ? '' : ' disabled') + '>🤝 Обміняти</button>'
        : '<button type="button" class="primary" data-a="ans" data-id="' + o.id + '" data-ok="1"' + (can ? '' : ' disabled') + '>✅ Згоден</button>';
      h = '<div class="khu-incc" role="alertdialog" aria-label="Пропозиція обміну">'
        + '<div class="khu-inch"><span class="khu-bell" aria-hidden="true">🔔</span><span class="khu-inw">' + head + '</span><span class="khu-isec" data-isec></span>'
        + '<button type="button" class="khu-ilater" data-a="inc-hide" title="Згорнути — відповім пізніше">▾ пізніше</button></div>'
        + '<div class="khu-ideal"><span class="side"><small>отримаєш</small>' + resIcons(o.give) + '</span><span class="arr">⇄</span><span class="side"><small>віддаси</small>' + resIcons(o.get) + '</span></div>'
        + '<div class="khu-ileft">у тебе: ' + after + '</div>'
        + (can ? '' : '<div class="khu-ileft no">не вистачає: ' + resIcons(lack) + '</div>')
        + '<div class="khu-ib">' + yes + '<button type="button" data-a="ans" data-id="' + o.id + '" data-ok="0">❌ Ні</button>'
        + '<button type="button" data-a="counter" data-id="' + o.id + '">↩ Інакше</button></div>'
        + (P.n > 1 ? '<button type="button" class="khu-imore" data-a="inc-next">ще ' + (P.n - 1) + ' ▸</button>' : '')
        + bar + '</div>';
    }
    setHtml(st.el.inc, h);
    setHtml(st.el.incm, h);
    tickInc(st);
  }

  /// Смужка часу й секунди вхідної картки — щотіку (без перемальовування).
  function tickInc(st) {
    const I = st.inc;
    if (!I || !st.el) return;
    const now = Date.now(), left = Math.max(0, I.end - now);
    const f = I.end < Infinity ? Math.max(0, Math.min(1, left / Math.max(1, I.end - I.t0))) : 1;
    for (const host of [st.el.inc, st.el.incm]) {
      for (const b of host.querySelectorAll('[data-ibar]')) { b.style.transform = 'scaleX(' + f.toFixed(3) + ')'; b.classList.toggle('late', left < 4000); }
      const t = I.end < Infinity ? Math.ceil(left / 1000) + ' с' : '';
      for (const s of host.querySelectorAll('[data-isec]')) if (s.textContent !== t) s.textContent = t;
    }
  }

  /// Увага без звуку: вібрація на «твій хід» і нову пропозицію, мигання вкладки у фоні, раз — «увімкнути звук?».
  /// Каркас (app.js paintTitle) уже ставить «🎲 Твій хід ·» у заголовок, коли хід мій; тут — мигання й пропозиції/скидання.
  function attention(st, v, me) {
    const live = me && v.phase !== 'over' && !v.over;
    const mine = !!(live && (me.must || (v.turn === me.seat && (v.phase === 'main' || v.phase === 'road2'))));
    const inc = live ? incList(v, me) : [];
    const top = inc.length ? inc[0].o.id : -1;
    const first = st.attn == null;
    const was = st.attn || { mine: false, top: -1 };
    st.attn = { mine, top: Math.max(top, was.top) };
    if (!first && ((mine && !was.mine) || top > was.top)) {
      const now = Date.now();
      if (!(now - (st.vibAt || 0) < 5000)) {
        st.vibAt = now;
        // До першого дотику браузер вібрацію блокує (і лається в консоль) — тоді й не пробуємо.
        try { if (navigator.vibrate && (!navigator.userActivation || navigator.userActivation.hasBeenActive)) navigator.vibrate(top > was.top ? [90, 60, 90] : 120); } catch (e) { /* не всюди можна */ }
      }
      if (top > was.top) sfx(st, 'trade');
    }
    st.attnTitle = inc.length ? '🤝 Тобі пропонують обмін — Хутори'
      : mine ? (me.must === 'discard' ? '🃏 Скинь карти — Хутори' : '🎲 Твій хід — Хутори') : null;
    // «🔊 Увімкнути звук гри?» — один раз, коли щось чекає саме від мене, а звук вимкнено.
    const ask = !!(st.snd && !st.snd.on && (mine || inc.length) && store.get('kh.sndAsk') == null);
    if (ask && !st.askAt) st.askAt = Date.now();
    setHtml(st.el.ask, ask ? '<span>🔊 Увімкнути звук гри?</span><button type="button" class="primary" data-a="snd-yes">Так</button><button type="button" data-a="snd-no">Ні, тихо</button>' : '');
    st.el.ask.hidden = !ask;
  }

  function tickAttn(st) {
    tickInc(st);
    // Питання про звук висить 20 с — далі вважаємо, що бачили, і більше не питаємо.
    if (st.askAt && !st.el.ask.hidden && Date.now() - st.askAt > 20000) { store.set('kh.sndAsk', 'seen'); st.el.ask.hidden = true; st.askAt = 0; }
    if (typeof document === 'undefined') return;
    const want = document.hidden && st.attnTitle ? st.attnTitle : null;
    if (want) {
      const t = document.title;
      if (t !== want && t !== st.titleBase) st.titleBase = t;   // app.js міг переписати назву (трек, непрочитане)
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

  const HELP = '<ol>'
    + '<li><b>Кидай кубики</b> 🎲 — кожна земля з цим числом дає карту всім, чия хата стоїть на її куті. 🔴 6 і 8 випадають найчастіше</li>'
    + '<li><b>Будуй</b> за карти: 🛤 шлях веде до нових місць, 🏠 хата = 1 ⭐, 🏛 містечко = 2 ⭐ і вдвічі більше карт</li>'
    + '<li><b>Правило відстані:</b> хату ставиш на перехресті, не ближче ніж через одне перехрестя від будь-якої іншої хати. Після розстановки — лише біля свого шляху</li>'
    + '<li><b>Торгуйся</b> 🤝 з сусідами або з банком: 4 однакові → 1 будь-яка. ⚓ <b>Пристані</b> на березі: хата біля «3:1» міняє 3 однакові на 1, біля пристані з ресурсом — 2 цього ресурсу на 1</li>'
    + '<li>Випала <b>сімка</b> — приходить Кармелюк 🥷: хто має понад 7 карт, віддає половину; той, хто кинув, ставить Кармелюка на землю (вона не родить) і забирає карту в сусіда</li>'
    + '<li><b>Нагороди</b> по +2 ⭐: 🛤 <b>Чумацький шлях</b> — найдовший шлях без розривів, щонайменше 5; ⚔ <b>Найсильніша сотня</b> — найбільше зіграних козаків, щонайменше 3. Хто переважить — забирає</li>'
    + '<li>📜 <b>Грамоти</b> (🧶 + 🌾 + ⛏️): ⚔️ Козак — пересунь Кармелюка й візьми карту; 💰 Скарб — +1 ⭐ потай; 🛤 Толока — два шляхи задарма; 🌻 Врожайний рік — дві карти з банку; 🎪 Ярмарок — усі віддають тобі названий ресурс. Одна грамота за хід, куплену — не того ж ходу. Тицни грамоту в руці — побачиш, що вона робить</li>'
    + '<li>Перший, хто набере потрібні ⭐ у свій хід, — <b>переміг</b></li></ol>';

  /// Журнал і правила — кожне у своїй вкладці (khutory-look.md §7): журнал — усі рядки з виду, найновіший знизу.
  function renderLog(st, v) {
    const lines = v.log || [];
    setHtml(st.el.log, lines.length ? '<ol>' + lines.map((l) => '<li>' + esc(l) + '</li>').join('') + '</ol>'
      : '<p class="khu-note">Поки тихо — тут буде, хто що зробив.</p>');
    setHtml(st.el.help, HELP);
  }

  // ─── Вкладки «🤝 Торг · 📜 Журнал · ❔ Правила» ──────────────────────────
  /// Обрана вкладка: сам обрав — та; інакше розстановка — правила, торг відкрито чи є пропозиції — торг, решта — журнал.
  function tabNow(st, v) {
    if (st.tab) return st.tab;
    if (st.trade.open || (v.offers || []).length) return 'trade';
    return v.phase === 'setup' ? 'help' : 'log';
  }

  function renderTabs(st, v, me) {
    const t = tabNow(st, v);
    const nOff = (v.offers || []).length, nInc = incList(v, me).length;
    const badge = nInc ? ' <span class="khu-badge" title="Чекають твоєї відповіді">' + nInc + '</span>'
      : nOff && t !== 'trade' ? ' <span class="khu-badge mute" title="Пропозицій на столі">' + nOff + '</span>' : '';
    const tab = (k, label) => '<button type="button" role="tab" class="khu-tab' + (t === k ? ' on' : '') + '" data-a="tab" data-t="' + k + '" aria-selected="' + (t === k) + '">' + label + '</button>';
    setHtml(st.el.tabh, tab('trade', '🤝 Торг' + badge) + tab('log', '📜 Журнал') + tab('help', '❔ Правила'));
    for (const p of st.el.tps) p.hidden = p.dataset.t !== t;
    const empty = !st.trade.open && !nOff;
    setHtml(st.el.tpe, !empty ? '' : me && v.phase === 'main' && v.turn === me.seat
      ? 'Обмін із сусідами — «🤝 Торгувати» вище; з банком — чипами «4🪵→1». Тут з’являться пропозиції.'
      : 'Тут з’являться пропозиції обміну. Свою — у свій хід кнопкою «🤝 Торгувати».');
    st.el.tpe.hidden = !empty;
    // Торг відкрито з нижньої панелі телефона чи з кнопки — показати його. ПК: гортаємо лише колонку (вкладки — до її верху;
    // сторінку не чіпаємо, бо плашки липнуть угорі й закрили б початок); телефон: сторінку — до вкладок під шапкою.
    if (st.toTrade) {
      st.toTrade = false;
      requestAnimationFrame(() => {
        const side = st.el.tabs.parentElement, tabs = st.el.tabs, behavior = reduced() ? 'auto' : 'smooth';
        try {
          if (side && getComputedStyle(side).overflowY === 'auto') side.scrollTo({ top: side.scrollTop + tabs.getBoundingClientRect().top - side.getBoundingClientRect().top, behavior });
          else tabs.scrollIntoView({ block: 'start', behavior });
        } catch (e) { /* не головне */ }
      });
    }
  }

  // ─── Телефон: липка нижня панель дій ────────────────────────────────────
  /// 🎲 Кинути · 🛤 · 🏠 · 🏛 · 📜 · 🤝 · ➡ Завершити — ті самі data-a, що й у панелі ходу (renderAct); на ПК схована CSS-ом.
  function renderBar(st, v, me) {
    let h = '';
    const myTurn = me && v.turn === me.seat;
    const bb = (a, icon, label, opts) => '<button type="button" class="khu-bb' + (opts.cls ? ' ' + opts.cls : '') + '" data-a="' + a + '"'
      + (opts.k ? ' data-k="' + opts.k + '"' : '') + (opts.off ? ' disabled' : '') + (opts.title ? ' title="' + esc(opts.title) + '"' : '')
      + '><span class="i">' + icon + '</span><small>' + label + '</small></button>';
    if (!me || v.phase === 'over' || v.phase === 'setup') h = '';
    else if (me.must === 'roll') h = bb('roll', '🎲', 'Кинути кубики', { cls: 'primary wide' });
    else if (me.must === 'discard') h = bb('disc-open', '🃏', 'Скинути карти', { cls: 'primary wide' });
    else if (myTurn && v.phase === 'main') {
      const can = me.can || {}, why = me.why || {}, sp = me.spots || {};
      for (const k of ['road', 'town', 'city', 'dev']) {
        const room = k === 'dev' || (sp[k] || []).length > 0;
        const ok = !!can[k] && room, on = st.pick === k;
        h += bb('build', on ? '✕' : BUILD_ICON[k], on ? 'Скасувати' : M.C.BUILD_NAME[k], { k, cls: on ? 'on' : ok ? 'ok' : '', off: !ok && !on, title: ok || on ? M.C.BUILD_NAME[k] : (why[k] || 'не зараз') });
      }
      h += bb('trade', '🤝', 'Торг' + incBadge(v, me), { cls: st.trade.open ? 'on' : '' });
      h += bb('end', '➡', 'Завершити', { cls: 'primary' });
    } else if (v.phase === 'main' && v.turn != null && !isBot(v, v.turn) && v.rolled !== false) {
      h = bb('trade', '🤝', 'Зустрічна ' + esc(nickOf(v, v.turn)) + incBadge(v, me), { cls: 'wide' + (st.trade.open ? ' on' : '') });
    }
    setHtml(st.el.bar, h);
  }

  /// Висоти плашок, руки й панелі — у змінні картки: ПК рахує з них висоту поля, телефон — відступи липких шторок.
  function sizeVars(root, st) {
    const k = root.querySelector('.khu');
    if (!k || !st.el) return;
    const hOf = (el) => (el && el.getClientRects().length ? Math.round(el.getBoundingClientRect().height) : 0);
    k.style.setProperty('--khu-plh', hOf(st.el.players) + 'px');
    k.style.setProperty('--khu-handh', hOf(st.el.hand) + 'px');
    k.style.setProperty('--khu-barh', (st.el.bar.firstChild ? hOf(st.el.bar) : 0) + 'px');
  }

  function devState(v, me, d) {
    const myTurn = v.turn === me.seat;
    if (d.k === 'vp') return { ok: false, why: '+1 очко потай — грати не треба' };
    if (d.fresh) return { ok: false, why: 'куплена цього ходу — зіграєш наступного' };
    if (!myTurn) return { ok: false, why: 'граєш у свій хід' };
    if (me.playedDev) return { ok: false, why: 'одна грамота за хід — уже зіграно' };
    if (v.phase === 'roll') return d.k === 'knight' ? { ok: true } : { ok: false, why: 'спершу кинь кубики' };
    if (v.phase === 'main') return { ok: true };
    return { ok: false, why: 'не зараз' };
  }

  function renderHand(st, v, me) {
    if (!me) { setHtml(st.el.hand, ''); return; }
    const C = M.C, now = Date.now();
    const hand = me.hand || {};
    const n = M.K.total(hand);
    let h = '<div class="khu-hh"><b>Твоя рука</b> · ' + n + ' ' + plural(n, 'карта', 'карти', 'карт')
      + (n > 7 ? ' <span class="khu-warn" title="Коли випаде сімка, віддаси половину">⚠ понад 7 — на сімці скинеш половину</span>' : '') + '</div>';
    if (!n && !(me.dev || []).length) {
      setHtml(st.el.hand, h + '<p class="khu-note">Карт поки нема. Вони прийдуть самі: коли кубики покажуть число на землі біля твоєї хати.</p>');
      return;
    }
    h += '<div class="khu-cards">' + RES.map((r) => '<div class="khu-card' + ((hand[r] || 0) ? '' : ' zero') + ((st.pulse[r] || 0) > now ? ' new' : '') + '" style="--rc:' + C.RES_COLOR[r] + '">'
      + '<span class="khu-ci">' + RES_ICON[r] + '</span><b>' + (hand[r] || 0) + '</b><span class="khu-cn">' + C.RES_NAME[r] + '</span></div>').join('');
    // Грамота: тиц — розгорнути пояснення; «▶ Зіграти» — окрема кнопка в розгорнутій (не грати випадковим дотиком).
    if (st.devOpen != null && st.devOpen >= (me.dev || []).length) st.devOpen = null;
    for (let i = 0; i < (me.dev || []).length; i++) {
      const d = me.dev[i];
      const s = devState(v, me, d);
      const open = st.devOpen === i;
      h += '<div class="khu-card dev' + (s.ok ? ' ok' : ' off') + (open ? ' open' : '') + '">'
        + '<button type="button" class="khu-dtap" data-a="dev-open" data-i="' + i + '" aria-expanded="' + open + '" title="' + esc(open ? 'Згорнути' : 'Що робить ця грамота') + '">'
        + '<span class="khu-ci">' + (DEV_ICON[d.k] || '📜') + '</span><span class="khu-dn">' + esc(C.DEV_NAME[d.k] || d.k) + ' <i class="khu-dinfo">' + (open ? '▴' : 'ⓘ') + '</i></span></button>'
        + (open ? '<span class="khu-dh">' + esc(C.DEV_HINT[d.k] || '') + '</span>' : '')
        + (s.ok ? (open ? '<button type="button" class="primary" data-a="play" data-k="' + d.k + '">▶ Зіграти</button>' : '<span class="khu-cw">можна зіграти — тицни</span>')
          : '<span class="khu-cw">' + esc(s.why) + '</span>')
        + '</div>';
    }
    h += '</div>';
    setHtml(st.el.hand, h);
  }

  function renderDlg(st, v, me) {
    const C = M.C, K = M.K;
    let h = '';
    if (me && me.must === 'discard' && me.discard > 0 && !st.disc.hidden) {
      const key = me.discard + ':' + JSON.stringify(me.hand);
      if (st.disc.key !== key) { st.disc.key = key; st.disc.plan = K.discardPlan(me.hand, me.discard); }
      const plan = st.disc.plan, sum = K.total(plan), need = me.discard;
      h = '<h3>🃏 Сімка! Скинь ' + need + ' ' + plural(need, 'карту', 'карти', 'карт') + '</h3>'
        + '<p>Глек уже підібрав — з тих, яких у тебе найбільше. Можна поправити.</p>'
        + '<div class="khu-disc">' + RES.map((r) => {
          const has = (me.hand || {})[r] || 0, n = plan[r] || 0;
          return '<div class="khu-dr" style="--rc:' + C.RES_COLOR[r] + '"><span class="khu-ri">' + RES_ICON[r] + '</span><small>є ' + has + '</small>'
            + '<button type="button" data-a="disc-add" data-r="' + r + '"' + (n < has && sum < need ? '' : ' disabled') + ' aria-label="скинути більше">+</button>'
            + '<b>' + n + '</b>'
            + '<button type="button" data-a="disc-sub" data-r="' + r + '"' + (n ? '' : ' disabled') + ' aria-label="скинути менше">−</button></div>';
        }).join('') + '</div>'
        + '<button type="button" class="khu-big primary" data-a="discard"' + (sum === need ? '' : ' disabled') + '>'
        + (sum === need ? 'Скинути ' + esc(K.resList(K.compact(plan))) : sum < need ? 'Обери ще ' + (need - sum) : 'Забагато на ' + (sum - need)) + '</button>'
        + '<button type="button" class="khu-link" data-a="disc-hide">глянути на поле</button>';
    } else if (me && me.must === 'steal') {
      h = '<h3>🥷 Кого обібрати?</h3><p>Кармелюк забере одну випадкову карту.</p><div class="khu-vict">'
        + (me.steal || []).map((s) => {
          const p = playerOf(v, s) || {};
          return '<button type="button" class="khu-big" data-a="steal" data-seat="' + s + '">' + who(v, s) + ' <span class="muted">· ' + (p.cards || 0) + ' 🂠 · ⭐ ' + (p.points || 0) + '</span></button>';
        }).join('') + '</div>';
    } else if (st.dlg && st.dlg.k === 'plenty') {
      const picks = st.dlg.res;
      h = '<h3>🌻 Врожайний рік</h3><p>Візьми з банку будь-які дві карти (можна однакові).</p>'
        + '<div class="khu-pickres">' + RES.map((r) => '<button type="button" class="khu-chip big" data-a="dlg-res" data-r="' + r + '" style="--rc:' + C.RES_COLOR[r] + '"' + (picks.length < 2 && (v.bank || {})[r] !== 0 ? '' : ' disabled') + '>' + RES_ICON[r] + ' ' + C.RES_NAME[r] + '</button>').join('') + '</div>'
        + '<p class="khu-picked">Береш: ' + (picks.length ? picks.map((r, i) => '<button type="button" class="khu-chip on" data-a="dlg-pop" data-i="' + i + '">' + RES_ICON[r] + ' ' + C.RES_NAME[r] + ' ✕</button>').join(' ') : '<span class="muted">ще нічого</span>') + '</p>'
        + '<div class="khu-row"><button type="button" class="khu-mid" data-a="dlg-close">Не зараз</button><button type="button" class="khu-mid primary" data-a="dlg-ok"' + (picks.length === 2 ? '' : ' disabled') + '>Взяти</button></div>';
    } else if (st.dlg && st.dlg.k === 'mono') {
      const r0 = st.dlg.res;
      h = '<h3>🎪 Ярмарок</h3><p>Назви ресурс — усі віддадуть тобі весь свій.</p>'
        + '<div class="khu-pickres">' + RES.map((r) => '<button type="button" class="khu-chip big' + (r0 === r ? ' on' : '') + '" data-a="dlg-res" data-r="' + r + '" style="--rc:' + C.RES_COLOR[r] + '">' + RES_ICON[r] + ' ' + C.RES_NAME[r] + '</button>').join('') + '</div>'
        + '<div class="khu-row"><button type="button" class="khu-mid" data-a="dlg-close">Не зараз</button><button type="button" class="khu-mid primary" data-a="dlg-ok"' + (r0 ? '' : ' disabled') + '>' + (r0 ? 'Збирай ' + C.RES_GEN[r0] + '!' : 'Обери ресурс') + '</button></div>';
    }
    setHtml(st.el.dlg, h ? '<div class="khu-dlgin">' + h + '</div>' : '');
    st.el.dlg.hidden = !h;
  }

  function renderSum(st, v) {
    const o = v.over;
    if (!o) { setHtml(st.el.sum, ''); st.el.sum.hidden = true; return; }
    st.el.sum.hidden = false;
    const K = M.K, s = o.stats || {};
    const pts = o.points || [];
    const seats = v.players.map((p) => p.seat);
    const order = [...seats].sort((a, b) => (pts[b] || 0) - (pts[a] || 0));
    const whoM = (x) => seatMark(v, x) + '<b>' + esc(nickOf(v, x)) + '</b>';
    // «Ану ще раз» каркаса — під полем і журналом, новачок його не бачить: дублюємо в шапці підсумку (тисне ту саму кнопку).
    const again = st.ctx && st.ctx.mine ? '<button type="button" class="khu-mid primary khu-again" data-a="again">🔁 Ану ще раз</button>' : '';
    let h = '<div class="khu-sumh"' + (o.winner != null ? ' style="--pc:' + colorOf(v, o.winner) + '"' : '') + '><span class="khu-cup">' + (o.winner != null ? '🏆' : '🏁') + '</span><div>'
      + (o.winner != null ? '<div class="muted small">Голова громади</div><h3>' + whoM(o.winner) + ' — ' + (pts[o.winner] || 0) + ' ⭐'
        + (s.rounds ? ' <span class="muted small">за ' + s.rounds + ' ' + plural(s.rounds, 'коло', 'кола', 'кіл') + '</span>' : '') + '</h3>' : '<h3>Партію скінчено</h3>')
      + (o.reason === 'left' ? '<span class="muted">(решта вийшли)</span>' : '') + '</div>' + again + '</div>';
    // Звання (не більше двох на людину) і «твій момент».
    const titles = K.sumTitles(o, v);
    if (titles.length) {
      h += '<div class="khu-titles">' + titles.map((t) => '<div class="khu-ti"' + (t.seat != null ? ' style="--pc:' + colorOf(v, t.seat) + '"' : '') + '>'
        + '<span class="i">' + t.icon + '</span><div><div class="n">' + esc(t.name) + '</div>'
        + (t.seat != null ? '<div class="w">' + whoM(t.seat) + '</div>' : '') + '<div class="d">' + esc(t.desc) + '</div></div></div>').join('') + '</div>';
    }
    const me = v.me && seats.includes(v.me.seat) ? v.me.seat : null;
    const mo = me != null ? K.myMoment(s.timeline, me, seats, s.rounds) : null;
    if (mo) {
      const place = ['', 'перше', 'друге', 'третє', 'четверте'][mo.place] || mo.place + '-е';
      h += '<div class="khu-moment">✨ <b>Твій момент</b>: коло ~' + mo.round + ' — '
        + (mo.up > 0 ? (mo.place === 1 && !mo.tie ? 'ти вирвався вперед' : mo.place === 1 ? 'ти наздогнав лідера' : 'ти обійшов суперника і вийшов на ' + place + ' місце') : '+' + mo.gain + ' ⭐ за коло')
        + (mo.up > 0 && mo.gain > 0 ? ' (+' + mo.gain + ' ⭐)' : '') + '. '
        + (o.winner === me ? 'Далі — перемога!' : 'Наприкінці — ' + (pts[me] || 0) + ' ⭐' + (v.target > (pts[me] || 0) ? ', до перемоги бракувало ' + (v.target - (pts[me] || 0)) : '') + '.') + '</div>';
    }
    h += '<div class="khu-sumgrid"><div><ol class="khu-rank">' + order.map((x) => '<li>' + whoM(x) + ' <b>' + (pts[x] || 0) + ' ⭐</b></li>').join('') + '</ol></div>';
    // Графік очок за колами. timeline — за НОМЕРАМИ місць (з нулями на порожніх), тож лінію беремо за p.seat.
    if (s.timeline && s.timeline.length > 1) {
      const W = 320, H = 170;
      const g = K.chartGeom(s.timeline, W, H, v.target);
      let svg = '<svg class="khu-chart" viewBox="0 0 ' + W + ' ' + H + '" role="img" aria-label="Очки за колами">';
      for (let p = 0; p <= g.max; p += 2) svg += '<line x1="22" x2="' + (W - 22) + '" y1="' + g.y(p) + '" y2="' + g.y(p) + '" class="grid"/><text x="16" y="' + (g.y(p) + 4) + '" class="ax">' + p + '</text>';
      if (g.targetY != null) svg += '<line x1="22" x2="' + (W - 22) + '" y1="' + g.targetY + '" y2="' + g.targetY + '" class="goal"/><text x="' + (W - 22) + '" y="' + (g.targetY - 4) + '" class="ax" text-anchor="end">мета ' + v.target + '</text>';
      const lines = K.chartLines(g, seats);
      for (const ln of lines) {
        svg += '<polyline points="' + ln.pts.map((p) => p.join(',')).join(' ') + '" style="stroke:' + colorOf(v, ln.seat) + '"' + (ln.seat === o.winner ? ' class="win"' : '') + '/>';
      }
      // Фігура гравця на кінці лінії (колір сам не розрізняє всіх); рівні кінці — розсунуті вбік.
      const used = {};
      for (const ln of lines) {
        const [x, y] = ln.pts[ln.pts.length - 1];
        const k = Math.round(y);
        const dx = (used[k] = (used[k] || 0) + 1) - 1;
        const ci = (playerOf(v, ln.seat) || {}).color;
        svg += '<text x="' + (x + 2 + dx * 11) + '" y="' + (y + 4) + '" class="shp" style="fill:' + colorOf(v, ln.seat) + '">' + (M.C.SEAT_SHAPE || [])[ci != null ? ci : ln.seat] + '</text>';
      }
      svg += '<text x="' + (W / 2) + '" y="' + (H - 4) + '" class="ax" text-anchor="middle">кола →</text></svg>';
      h += '<div><div class="khu-trl">Очки за колами</div>' + svg + '</div>';
    }
    // Гістограма кубиків.
    if (s.dice && s.dice.length === 11) {
      const mx = Math.max(1, ...s.dice);
      h += '<div><div class="khu-trl">Що випадало</div><div class="khu-hist">' + s.dice.map((n, i) => '<span class="' + (i === 5 ? 'seven' : '') + '" title="' + (i + 2) + ': ' + n + ' раз"><i style="height:' + Math.round(n / mx * 100) + '%"></i><b>' + (i + 2) + '</b></span>').join('')
        + '</div><p class="muted small">сімка — Кармелюк приходив ' + (s.sevens != null ? s.sevens : s.dice[5]) + ' ' + plural(s.sevens != null ? s.sevens : s.dice[5], 'раз', 'рази', 'разів') + '</p></div>';
    }
    h += '</div>';
    if (s.seats && s.seats.length) {
      const hasRobbed = s.seats.some((x) => x.robbed != null);
      h += '<div class="khu-tbl"><table class="khu-stats"><thead><tr><th></th><th>🤝<br><small>обмінів</small></th><th>🂠<br><small>отримав карт</small></th><th>🥷<br><small>вкрав</small></th>'
        + (hasRobbed ? '<th>😿<br><small>обкрали</small></th>' : '') + '<th>🛤<br><small>шляхів</small></th><th>🏠<br><small>хат</small></th><th>🏛<br><small>містечок</small></th></tr></thead><tbody>'
        + s.seats.map((x) => '<tr><td>' + whoM(x.seat) + '</td><td>' + x.trades + '</td><td>' + x.got + '</td><td>' + x.stolen + '</td>' + (hasRobbed ? '<td>' + (x.robbed || 0) + '</td>' : '')
          + '<td>' + ((x.built || {}).road || 0) + '</td><td>' + ((x.built || {}).town || 0) + '</td><td>' + ((x.built || {}).city || 0) + '</td></tr>').join('')
        + '</tbody></table></div>';
    }
    setHtml(st.el.sum, h);
  }

  /// Знак гравця: його колір + фігура ●▲■◆ (на світлому — темна фігура й кант).
  function seatMark(v, seat) {
    const p = playerOf(v, seat), ci = p && p.color != null ? p.color : seat;
    const light = !!(M.C.SEAT_LIGHT && M.C.SEAT_LIGHT[ci]);
    return '<i class="khu-pmark' + (light ? ' light' : '') + '" style="--pc:' + colorOf(v, seat) + '" aria-hidden="true">' + ((M.C.SEAT_SHAPE || [])[ci] || '●') + '</i>';
  }

  /// Смуга «що дасть кожне число» 2…12 (khutory-look.md §9): крапки ймовірності, що дасть МЕНІ, останні кидки.
  function renderNumbers(st, v, me) {
    const el = st.el.nums;
    if (!el) return;
    if (!me || !v.board || v.over || v.phase === 'setup' || v.phase === 'over') {
      if (st.numSel != null) glow(st, null);
      setHtml(el, ''); el.hidden = true; return;
    }
    el.hidden = false;
    if (st.numsOpen == null) st.numsOpen = store.get('kh.nums') !== '0';
    const { cells, avg } = M.K.numberYield(v, me.seat);
    const held = rollHeld(st);
    const cur = !held && v.rolled && v.dice ? v.dice[0] + v.dice[1] : null;
    const rolls = held && v.rolled ? (v.rolls || []).slice(0, -1) : (v.rolls || []);   // кубики ще котяться — не підглядати
    const avgTxt = avg.toFixed(2).replace('.', ',');
    let h = '<div class="khu-numh"><button type="button" class="khu-numt" data-nums="fold" aria-expanded="' + st.numsOpen + '">🎲 Що дасть число ' + (st.numsOpen ? '▴' : '▾') + '</button>'
      + '<span class="muted small">за кидок тобі в середньому ≈ <b>' + avgTxt + '</b> карти</span></div>';
    if (st.numsOpen) {
      h += '<div class="khu-nrow">' + cells.map((c) => {
        const y = c.n === 7 ? '🥷' : RES.filter((r) => c.res[r]).map((r) => RES_ICON[r] + (c.res[r] > 1 ? '<sup>×' + c.res[r] + '</sup>' : '')).join('');
        const t = c.n === 7 ? 'Сімка: Кармелюк — у кого понад 7 карт, скидає половину'
          : (c.cnt ? 'Тобі: ' + M.K.resList(c.res) : 'Тобі нічого') + (c.robbed ? ' (твою землю з цим числом тримає Кармелюк)' : '') + ' — ' + c.pips + ' з 36 кидків';
        return '<button type="button" class="khu-num' + (c.cnt ? ' mine' : '') + (c.n === 7 ? ' seven' : '') + (c.n === cur ? ' cur' : '') + (c.n === st.numSel ? ' sel' : '') + (c.robbed ? ' robbed' : '')
          + '" data-num="' + c.n + '" title="' + esc(t) + '">'
          + '<span class="n' + (c.n === 6 || c.n === 8 ? ' hot' : '') + '">' + c.n + '</span><span class="p">' + '<i></i>'.repeat(c.pips) + '</span>'
          + '<span class="y">' + (y || (c.robbed ? '🥷' : '<span class="muted">·</span>')) + '</span></button>';
      }).join('') + '</div>';
      if (rolls.length) {
        h += '<div class="khu-rolls"><span class="muted small">Останні кидки:</span>' + rolls.map((n, i) => '<span class="' + (n === 7 ? 's7' : cells[n - 2] && cells[n - 2].cnt ? 'my' : '')
          + (i === rolls.length - 1 ? ' last' : '') + '">' + n + '</span>').join('') + '</div>';
      }
    }
    setHtml(el, h);
  }

  /// Підсвітити на полі землі з числом n (null — зняти). Метод сцени може ще не існувати (старий scene.js) — тоді тихо.
  function glow(st, n) {
    st.numSel = n;
    const ids = n == null || !st.view ? [] : st.view.board.hexes.map((hx, i) => (hx.num === n ? i : -1)).filter((i) => i >= 0);
    if (st.sc && typeof st.sc.glowHexes === 'function') { try { st.sc.glowHexes(ids); } catch (e) { /* підсвітка не головне */ } }
    clearTimeout(st.numT);
    if (n != null) st.numT = setTimeout(() => { if (st.numSel === n) { glow(st, null); if (st.view) renderNumbers(st, st.view, st.view.me || null); } }, 8000);
  }

  function onNums(root, e) {
    const st = root._kh;
    if (!st || !st.view) return;
    const b = e.target.closest('[data-num],[data-nums]');
    if (!b) return;
    if (b.dataset.nums === 'fold') {
      st.numsOpen = !st.numsOpen;
      store.set('kh.nums', st.numsOpen ? '1' : '0');
      if (!st.numsOpen && st.numSel != null) glow(st, null);
    } else {
      const n = Number(b.dataset.num);
      glow(st, st.numSel === n ? null : n);
    }
    renderNumbers(st, st.view, st.view.me || null);
  }

  // ─── Таймер, боти, «час минув» ──────────────────────────────────────────
  function tickArcs(st) {
    const v = st.view;
    if (!v || !st.el) return;
    const left = v.deadline ? Math.max(0, Date.parse(v.deadline) - Date.now()) : null;
    const total = v.timerMs || 90000;
    const L = 2 * Math.PI * 19;
    const frac = left == null ? 1 : Math.min(1, left / total);
    const late = left != null && left > 0 && left < 10000;
    for (const fg of st.el.players.querySelectorAll('.khu-av .fg')) {
      fg.style.strokeDashoffset = (L * (1 - frac)).toFixed(1);
      fg.classList.toggle('late', late);
    }
    // Смужка на всю ширину плашки того, хто думає; останні 10 с — червона рамка пульсує, секунди великим.
    for (const tm of st.el.players.querySelectorAll('.khu-tm i')) tm.style.width = (frac * 100).toFixed(1) + '%';
    for (const pl of st.el.players.querySelectorAll('.khu-pl.think')) pl.classList.toggle('late', late);
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
    const st = root._kh;
    if (!st || !st.view) return;
    tickArcs(st);
    if (st.marks.length) placeMarks(st);
    tickSound(st, st.view);
    tickAttn(st);
    // Строк минув — будь-хто за столом штовхає сервер (той звірить із годинником і зробить автохід).
    const v = st.view, ctx = st.ctx;
    if (!v.deadline || !ctx || !ctx.mine || !ctx.playing || !ctx.room || v.phase === 'over') return;
    const due = Date.parse(v.deadline) + 500 + myRank(v, ctx) * 700;
    const now = Date.now();
    if (now < due) return;
    if (st.toKey === v.deadline && now - st.toAt < 4000) return;
    st.toKey = v.deadline; st.toAt = now;
    if (HGames.send) HGames.send('Act', ctx.room.id, 'timeout', {});
  }

  /// Глеки ходять на штовхан нашого клієнта (як у «Дурні»): першим штовхає перша людина за столом, решта — із запасом.
  function nudge(root, st) {
    const v = st.view || {}, ctx = st.ctx;
    clearTimeout(st.nt);
    st.nt = 0;
    if (!ctx || !ctx.playing || !ctx.mine || typeof v.botIn !== 'number' || !HGames.send || !ctx.room) return;
    const fire = () => {
      if (root._kh !== st || !st.ctx || !st.ctx.room) return;
      HGames.send('Act', st.ctx.room.id, 'bot', null);
      st.nt = setTimeout(fire, 1500);
    };
    // Хід «задрімалої» людини (away): 5 с на повернення дає сервер — botIn тоді вже ≥ 5000.
    st.nt = setTimeout(fire, v.botIn + 60 + myRank(v, ctx) * 700);
  }

  // ─── Події: анімації по черзі ───────────────────────────────────────────
  function queueEvents(root, st, v) {
    if (typeof v.seq !== 'number') return;
    // Нова партія («Ще раз»): seq міг початись з 0, карта інша, або over → setup. Без анімацій старого, черга — геть.
    const bid = v.board && v.board.id, fresh0 = st.seq != null && (v.seq < st.seq || (st.seqBoard != null && bid !== st.seqBoard)
      || (st.seqPhase === 'over' && v.phase !== 'over'));
    st.seqBoard = bid; st.seqPhase = v.phase;
    if (fresh0) { st.seq = v.seq; st.queue.length = 0; st.diceShown = null; return; }
    if (st.seq == null) { st.seq = v.seq; return; }   // перший mount — нічого не анімуємо
    const fresh = (v.events || []).filter((e) => e.seq > st.seq).sort((a, b) => a.seq - b.seq);
    st.seq = Math.max(st.seq, v.seq);
    if (!fresh.length) return;
    fieldEvents(st, v, fresh);
    st.queue.push(...fresh);
    if (st.queue.length > 6) st.queue.splice(0, st.queue.length - 6);   // відстали — показуємо лише свіже
    runQueue(root, st);
  }

  const cap = (p, ms) => Promise.race([Promise.resolve(p).catch(() => {}), new Promise((r) => setTimeout(r, ms))]);

  async function runQueue(root, st) {
    if (st.running) return;
    st.running = true;
    try {
      while (st.queue.length && root._kh === st) {
        const e = st.queue.shift();
        await cap(playEvent(root, st, e), 1500);
      }
    } finally { st.running = false; }
  }

  async function playEvent(root, st, e) {
    const v = st.view, sc = st.sc, me = v && v.me;
    if (!v) return;
    const C = M.C;
    if (e.k === 'roll') {
      const sum = (e.d || [0, 0])[0] + (e.d || [0, 0])[1];
      st.rolling = true;
      sfx(st, 'dice');
      if (me) {
        const n = (e.gains || []).filter((g) => g.seat === me.seat).reduce((a, g) => a + (g.n || 0), 0);
        if (n > 0) setTimeout(() => { if (st.view) sfx(st, 'coin', { n }); }, sc ? 1300 : 300);
      }
      try { if (sc) await cap(sc.roll(e.d[0], e.d[1]), 1400); } finally { st.rolling = false; st.diceShown = e.d; }
      flash(st, '🎲 ' + esc(nickOf(v, e.seat)) + ' кидає ' + sum + (sum === 7 ? ' — Кармелюк іде!' : ''));
      if (st.view) renderAct(st, st.view, st.view.me);
      gainsToChips(st, v, e.gains || []);
      if (sc && e.gains && e.gains.length) await sc.produce(e.gains);
    } else if (e.k === 'build') {
      sfx(st, e.what === 'road' ? 'road' : e.what === 'city' ? 'city' : 'place');
      if (sc) sc.pop(e.what === 'road' ? 'e' : 'v', e.at);
    } else if (e.k === 'robber') {
      sfx(st, 'robber');
    } else if (e.k === 'trade') {
      if (me && (e.seat === me.seat || e.with === me.seat)) sfx(st, 'trade');
    } else if (e.k === 'dev' && !e.play) {
      if (me && e.seat === me.seat) sfx(st, 'card');
    } else if (e.k === 'steal' && me && (e.seat === me.seat || e.from === me.seat)) {
      sfx(st, 'steal');
      const r = e.res ? ' ' + RES_ICON[e.res] + ' ' + C.RES_NAME[e.res] : ' карту';
      flash(st, e.seat === me.seat ? '🥷 Ти забрав у ' + esc(nickOf(v, e.from)) + r : '🥷 Кармелюк забрав у тебе' + r + ' для ' + esc(nickOf(v, e.seat)));
    } else if (e.k === 'dev' && e.play) {
      // §7 каже «dev: seat, k», але k уже зайнято типом події — беремо назву грамоти з card/dev (питання оркестратору).
      const dk = e.card || e.dev || null;
      flash(st, (DEV_ICON[dk] || '📜') + ' ' + esc(nickOf(v, e.seat)) + ' грає ' + esc(C.DEV_NAME[dk] || 'грамоту'));
    } else if (e.k === 'award') {
      if (e.seat != null) sfx(st, 'award');
      if (e.seat != null) flash(st, (e.kind === 'longest' ? '🛤 Чумацький шлях' : '⚔ Найсильніша сотня') + ' — у ' + esc(nickOf(v, e.seat)) + ' (+2 ⭐)');
    } else if (e.k === 'turn' && me && e.seat === me.seat) {
      sfx(st, 'turn');
      flash(st, '👉 Твій хід!');
    } else if (e.k === 'win') {
      if (st.wonSeq !== e.seq) { st.wonSeq = e.seq; sfx(st, 'win'); }
      flash(st, '🏆 ' + esc(nickOf(v, e.seat)) + ' перемагає!');
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

  function gainsToChips(st, v, gains) {
    const by = {};
    for (const g of gains) (by[g.seat] = by[g.seat] || {})[g.res] = ((by[g.seat] || {})[g.res] || 0) + g.n;
    for (const s of Object.keys(by)) {
      const chip = st.el.players.querySelector('.khu-pl[data-seat="' + s + '"]');
      if (!chip) continue;
      const b = document.createElement('span');
      b.className = 'khu-gain';
      b.textContent = Object.keys(by[s]).map((r) => '+' + by[s][r] + ' ' + RES_ICON[r]).join(' ');
      chip.appendChild(b);
      setTimeout(() => b.remove(), 1900);
    }
  }

  // ─── Поле: бейджі Кармелюка й чужі ходи (khutory-look.md §2, §8) ─────────
  const hexName = (v, h) => { const x = v.board && v.board.hexes[h]; return x ? M.C.TERRAIN_NAME[x.t] + (x.num ? ' ' + x.num : '') : ''; };
  /// Знак гравця: кружечок його кольору з фігурою ●▲■◆ (колір сам дальтоніка не рятує, §6); n — число поруч.
  function seatDot(v, seat, n) {
    const p = playerOf(v, seat), k = p ? p.color : seat;
    return '<i class="khu-sd' + (M.C.SEAT_LIGHT[k] ? ' lt' : '') + '" style="--pc:' + colorOf(v, seat) + '">' + (M.C.SEAT_SHAPE[k] || '') + '</i>'
      + (n != null ? '<b>' + n + '</b>' : '');
  }

  /// Бейдж під кожним дозволеним гексом, поки я переношу Кармелюка: кого зачепить і скільки в нього карт, «⚠ твоя»,
  /// «⭐ Глек радить». Текст міняється лише разом із видом (setHtml), позицію веде placeRob.
  function robBadges(st, v) {
    const me = v.me;
    st.rob = me && me.must === 'robber' && st.legal && st.legal.kind === 'robber' ? M.K.robberInfo(v, me.seat) : null;
    if (st.rob && !st.rob.length) st.rob = null;
    setHtml(st.el.rb, (st.rob || []).map((x) => {
      let h = '';
      if (x.best) h += '<span class="best">⭐ Глек радить</span>';
      if (x.own) h += '<span class="own">⚠ твоя</span>';
      if (x.victims.length) h += '<span class="vi">' + x.victims.map((y) => seatDot(v, y.seat, y.cards)).join('') + '</span>';
      else if (!x.own) h += '<span class="none">нікого</span>';
      return '<div class="khu-rbx" hidden>' + h + '</div>';
    }).join(''));
    if (!st.rob) closeAsk(st);
  }
  function placeRob(st) {
    const v = st.view;
    if (!st.rob || !v || !st.el) return;
    const kids = st.el.rb.children, W = st.el.fieldwrap.clientWidth, H = st.el.fieldwrap.clientHeight;
    st.rob.forEach((x, i) => {
      const el = kids[i]; if (!el) return;
      const p = st.sc.screenPos('h', x.h), hx = v.board.hexes[x.h];
      const c = hx ? st.sc.screenPos('v', hx.v[0]) : null;
      const R = p && c ? Math.hypot(c.x - p.x, c.y - p.y) : 0;
      // Поза полем або камеру відвели так далеко, що гекс дрібний, — ховаємо.
      const ok = !!p && R > 14 && p.x > 10 && p.x < W - 10 && p.y > 10 && p.y < H - 10;
      if (el.hidden === ok) el.hidden = !ok;
      if (!ok) return;
      const tf = 'translate(' + Math.round(p.x) + 'px,' + Math.round(p.y + R * 0.58) + 'px) translate(-50%,-50%)';
      if (el._tf !== tf) { el._tf = tf; el.style.transform = tf; }
    });
  }
  function robAsk(st, h) {
    const v = st.view, a = st.el && st.el.rbask;
    if (!v || !a) return;
    st.ask = { h };
    a.innerHTML = '<b>⚠ Тут стоїть і твоя хата</b><p>Кармелюк на «' + esc(hexName(v, h)) + '» — і ти сам не отримуватимеш звідси карт, поки його не проженуть.</p>'
      + '<div class="row"><button type="button" data-k="rob-yes">Все одно сюди</button><button type="button" class="primary" data-k="rob-no">Обрати інший</button></div>';
    a.hidden = false;
    const b = a.querySelector('[data-k="rob-no"]');
    if (b) b.focus({ preventScroll: true });
  }
  function closeAsk(st) { if (st.ask) st.ask = null; if (st.el && st.el.rbask && !st.el.rbask.hidden) st.el.rbask.hidden = true; }
  function onAsk(root, e) {
    const st = root._kh, b = e.target.closest('[data-k]');
    if (!st || !b || !st.ask) return;
    const h = st.ask.h;
    closeAsk(st);
    if (b.dataset.k === 'rob-yes' && st.ctx && st.legal && st.legal.kind === 'robber') st.ctx.act('robber', { h });
  }

  /// Нові події → стрічка в куті поля (3 останні, гаснуть за 6 с) і по одній «бульбашці» на полі (≤ 3 с): лише чужі ходи.
  const feedIcons = (o) => Object.keys(o || {}).filter((r) => o[r] > 0).map((r) => o[r] + (RES_ICON[r] || r)).join(' ') || '—';
  function fieldEvents(st, v, list) {
    const me = v.me ? v.me.seat : null;
    const q = st.fxq || (st.fxq = []);
    const stealTxt = (e) => (me != null && e.from === me
      ? { feed: 'забрав карту в <b>тебе</b>', bub: '↘ карта від тебе', red: true }
      : { feed: 'забрав карту в ' + esc(nickOf(v, e.from)), bub: '↘ карта від ' + esc(nickOf(v, e.from)), red: false });
    for (let i = 0; i < list.length; i++) {
      const e = list[i];
      const mine = me != null && (e.seat === me || (e.k === 'trade' && e.with === me));
      const pc = colorOf(v, e.seat), nk = esc(nickOf(v, e.seat));
      if (e.k === 'robber') {
        const from = st.fxRob != null && st.fxRob !== e.hex ? st.fxRob : null;
        st.fxRob = e.hex;
        if (mine) continue;
        const nx = list[i + 1], steal = nx && nx.k === 'steal' && nx.seat === e.seat ? nx : null;
        if (steal) i++;
        let feed = '🥷 ' + nk + ' → ' + esc(hexName(v, e.hex)), bub = '🥷 → ' + esc(hexName(v, e.hex)), red = false;
        if (steal) { const s = stealTxt(steal); feed += ' · ' + s.feed; bub += ' · ' + s.bub; red = s.red; }
        addFeed(st, feed, pc, red);
        q.push({ kind: 'h', id: e.hex, from, text: bub, pc, red });
      } else if (mine) {
        continue;
      } else if (e.k === 'build') {
        const t = e.what === 'road' ? '🛤 ' + nk + ': новий шлях' : e.what === 'city' ? '🏛 ' + nk + ': містечко' : '🏠 ' + nk + ': нова хата';
        addFeed(st, t, pc);
        q.push({ kind: e.what === 'road' ? 'e' : 'v', id: e.at, text: t, pc, wave: true });
      } else if (e.k === 'steal') {
        const s = stealTxt(e);
        addFeed(st, '🥷 ' + nk + ' ' + s.feed, pc, s.red);
        if (v.robber != null) q.push({ kind: 'h', id: v.robber, text: s.bub, pc, red: s.red });
      } else if (e.k === 'trade') {
        addFeed(st, '🤝 ' + nk + ' ⇄ ' + (e.with === 'bank' ? 'банк' : esc(nickOf(v, e.with))) + ': ' + feedIcons(e.give) + ' → ' + feedIcons(e.get), pc);
      } else if (e.k === 'award' && e.seat != null) {
        addFeed(st, (e.kind === 'longest' ? '🛤 Чумацький шлях' : '⚔ Найсильніша сотня') + ' — у ' + nk, pc);
      }
    }
    if (q.length > 3) q.splice(0, q.length - 3);   // відстали — лише свіже
    runFx(st);
  }

  function addFeed(st, html, pc, red) {
    const f = st.feed || (st.feed = []);
    f.push({ html, pc, red: !!red, t: Date.now() });
    if (f.length > 3) f.splice(0, f.length - 3);
    renderFeed(st);
  }
  function renderFeed(st) {
    if (!st.el || !st.el.feed) return;
    const now = Date.now();
    st.feed = (st.feed || []).filter((x) => now - x.t < 6000);
    // Найсвіжіше — згори; анімація згасання продовжується з віком рядка (від'ємна затримка), а не починається наново.
    setHtml(st.el.feed, st.feed.slice().reverse().map((x, i) => '<div class="' + (i ? 'old' : '') + (x.red ? ' red' : '') + '" style="--pc:' + x.pc
      + ';animation-delay:-' + (now - x.t) + 'ms">' + x.html + '</div>').join(''));
    clearTimeout(st.feedT);
    if (st.feed.length) st.feedT = setTimeout(() => renderFeed(st), Math.max(60, 6000 - (now - st.feed[0].t)));
  }

  /// По одній події на полі: хвиля (будова), дуга звідки→куди (Кармелюк), бульбашка. ≤ 3 с; черга — швидше.
  function runFx(st) {
    if (st.fxCur || !st.fxq || !st.fxq.length || !st.el || !st.sc) return;
    const f = st.fxq.shift();
    st.fxCur = f;
    const box = st.el.fx, NS = 'http://www.w3.org/2000/svg';
    box.textContent = '';
    f.els = {};
    if (f.from != null) {
      const svg = document.createElementNS(NS, 'svg');
      svg.innerHTML = '<path/><path class="c"/><circle r="6"/>';
      box.appendChild(svg);
      f.els.svg = svg;
    }
    if (f.wave && !reduced()) {
      const r = document.createElement('div');
      r.className = 'khu-rip'; r.style.setProperty('--pc', f.pc);
      box.appendChild(r);
      f.els.rip = r;
    }
    const b = document.createElement('div');
    b.className = 'khu-bub' + (f.red ? ' red' : '');
    b.style.setProperty('--pc', f.pc);
    b.innerHTML = f.text;
    box.appendChild(b);
    f.els.bub = b;
    placeFx(st);
    followMarks(st);
    clearTimeout(st.fxT);
    st.fxT = setTimeout(() => {
      st.fxCur = null;
      if (st.el) st.el.fx.textContent = '';
      runFx(st);
    }, st.fxq.length ? 1600 : 3000);
  }
  function placeFx(st) {
    const f = st.fxCur;
    if (!f || !f.els || !st.sc || !st.el) return;
    const p = st.sc.screenPos(f.kind, f.id);
    const vis = !!p;
    for (const k in f.els) if (f.els[k].style.display !== (vis ? '' : 'none')) f.els[k].style.display = vis ? '' : 'none';
    if (!p) return;
    const W = st.el.fieldwrap.clientWidth;
    const { rip, bub, svg } = f.els;
    if (rip) { const t = 'translate(' + Math.round(p.x) + 'px,' + Math.round(p.y) + 'px)'; if (rip._tf !== t) { rip._tf = t; rip.style.transform = t; } }
    const bw = f.bw || (f.bw = bub.offsetWidth || 120), top = f.kind === 'h' ? p.y - 14 : p.y;
    const below = top < 54;
    const x = Math.max(bw / 2 + 6, Math.min(W - bw / 2 - 6, p.x)), y = below ? p.y + 14 : top;
    const pos = Math.round(x) + ',' + Math.round(y) + below;
    if (bub._pos !== pos) { bub._pos = pos; bub.style.left = Math.round(x) + 'px'; bub.style.top = Math.round(y) + 'px'; bub.classList.toggle('below', below); }
    if (svg) {
      const a = st.sc.screenPos('h', f.from);
      if (a) {
        const ey = p.y - 14, mx = (a.x + p.x) / 2, my = Math.min(a.y, ey) - 40 - Math.abs(a.x - p.x) * 0.1;
        const d = 'M' + Math.round(a.x) + ',' + Math.round(a.y) + ' Q' + Math.round(mx) + ',' + Math.round(my) + ' ' + Math.round(p.x) + ',' + Math.round(ey);
        if (svg._d !== d) {
          svg._d = d;
          svg.children[0].setAttribute('d', d); svg.children[1].setAttribute('d', d);
          svg.children[2].setAttribute('cx', Math.round(p.x)); svg.children[2].setAttribute('cy', Math.round(ey));
        }
      }
    }
  }

  // ─── Кліки ──────────────────────────────────────────────────────────────
  function onClick(root, e) {
    const b = e.target.closest('[data-a]');
    const st = root._kh;
    if (!b || !st || !root.contains(b) || b.disabled) return;
    const ctx = st.ctx, v = st.view, me = v && v.me;
    const a = b.dataset.a, r = b.dataset.r, T = st.trade;
    const id = b.dataset.id != null ? Number(b.dataset.id) : null;
    const act = (name, p) => ctx && ctx.act(name, p);
    switch (a) {
      case 'view': switchView(root, st); return;
      case 'snd': if (st.snd) { st.snd.setOn(!st.snd.on); paintSnd(st); } return;
      case 'again': {
        const rb = (root.closest('.groom') || document).querySelector('[data-do="Rematch"]');
        if (rb) rb.click(); else if (ctx && ctx.toast) ctx.toast('Ще раз зараз не вийде — замало гравців за столом');
        return;
      }
      case 'rejoin': {
        // Та сама кнопка, що в шапці столу (core.js, mod.rejoin): JoinRoom через каркас — він спитає, чи встати з іншого столу.
        const card = root.closest('.gtable');
        const jb = card && card.querySelector('.gbtns [data-do="JoinRoom"]');
        if (jb) jb.click(); else if (ctx && ctx.room) HGames.sitAt(ctx.room.id, b);
        return;
      }
      case 'tab': case 'help': case 'log': st.tab = a === 'tab' ? b.dataset.t : a; break;
      case 'roll': act('roll', {}); break;
      case 'end': T.open = false; st.pick = null; st.bankGive = null; st.tab = null; act('end', {}); break;
      case 'pick-cancel': st.pick = null; break;
      case 'build': {
        const k = b.dataset.k;
        if (k === 'dev') { act('buy', {}); break; }
        st.pick = st.pick === k ? null : k;
        T.open = false;
        break;
      }
      case 'trade':
        T.open = !T.open; st.bankGive = null;
        if (T.open) { st.pick = null; T.give = {}; T.get = {}; T.to = null; st.tab = 'trade'; st.toTrade = true; } else st.tab = null;
        break;
      case 'tr-add': case 'tr-sub': {
        const side = b.dataset.side, other = side === 'give' ? 'get' : 'give';
        const n = (T[side][r] || 0) + (a === 'tr-add' ? 1 : -1);
        T[side][r] = Math.max(0, n);
        if (a === 'tr-add') T[other][r] = 0;   // той самий ресурс і даю, і хочу — безглуздо
        break;
      }
      case 'tr-to': T.to = b.dataset.to === '' ? null : Number(b.dataset.to); break;
      case 'offer': {
        const K = M.K;
        act('offer', { give: K.compact(T.give), get: K.compact(T.get), to: v && me && v.turn === me.seat ? T.to : null });
        T.give = {}; T.get = {};
        if (!(v && me && v.turn === me.seat)) T.open = false;
        break;
      }
      case 'bank-give': st.bankGive = st.bankGive === r ? null : r; st.bankN = 1; if (!st.bankGive) st.bankWant = null; break;
      case 'bank-n': st.bankN = Number(b.dataset.n) || 1; break;
      case 'bank-get': {
        if (st.bankGive && me) {
          const g = M.K.bankChips(me.hand, me.rates).find((c) => c.res === st.bankGive);
          const n = Math.max(1, Math.min(st.bankN || 1, g ? g.max : 1));
          act('bank', { give: st.bankGive, get: r, n });
        }
        st.bankGive = null; st.bankN = 1; st.bankWant = null;
        break;
      }
      case 'fix': {
        // «🔁 обміняй і будуй»: обміни з банком по черзі, тоді — вибір місця для цієї будови.
        const k = b.dataset.k, fix = me && M.K.bankFix(me.hand, M.C.COST[k], me.rates);
        if (!fix || !ctx || st.fixing) break;
        st.fixing = true;
        (async () => {
          let ok = true;
          for (const t of fix) {
            const res = await Promise.resolve(ctx.act('bank', { give: t.give, get: t.get, n: t.n })).catch(() => null);
            if (!res || res.ok === false) { ok = false; break; }
          }
          st.fixing = false;
          if (root._kh !== st) return;
          if (ok && k !== 'dev') { st.pick = k; T.open = false; st.bankGive = null; }
          paint(root, st.ctx);
        })();
        break;
      }
      case 'of-edit': case 'of-bank': {
        // Усі відмовились: «Змінити» — прибрати й відкрити ту саму в «Торгувати»; «У банк» — прибрати й показати банк.
        const o = ((v && v.offers) || []).find((x) => x.id === id);
        if (!o) break;
        act('cancel', { id });
        if (a === 'of-edit') { T.open = true; T.give = { ...o.give }; T.get = { ...o.get }; T.to = o.to; st.pick = null; st.tab = 'trade'; st.toTrade = true; }
        else {
          const ch = M.K.bankChips(me.hand, me.rates).filter((c) => c.res !== r && c.max >= 1).sort((x, y) => x.rate - y.rate || y.have - x.have);
          st.bankGive = ch.length ? ch[0].res : null; st.bankN = 1; st.bankWant = r; T.open = false;
        }
        break;
      }
      case 'inc-hide': st.incHid = st.inc ? st.inc.id : null; break;
      case 'inc-show': st.incHid = null; break;
      case 'inc-next': {
        const P = incPick(st, v, me);
        if (P) { st.incPick = P.list[(P.i + 1) % P.n].o.id; st.incHid = null; }
        break;
      }
      case 'snd-yes': case 'snd-no':
        store.set('kh.sndAsk', a === 'snd-yes' ? 'on' : 'off');
        if (a === 'snd-yes' && st.snd) { st.snd.setOn(true); paintSnd(st); }
        st.askAt = 0;
        break;
      case 'dev-open': { const i = Number(b.dataset.i); st.devOpen = st.devOpen === i ? null : i; break; }
      case 'ans': act('answer', { id, ok: b.dataset.ok === '1' }); break;
      case 'counter': {
        const o = ((v && v.offers) || []).find((x) => x.id === id);
        if (o) {
          T.open = true; T.give = { ...o.get }; T.get = { ...o.give };
          T.to = me && v.turn === me.seat ? o.from : null;   // у свій хід — відповідь автору зустрічної
          st.incHid = id;                                     // картку — у смужку, щоб не закривала торг
          st.tab = 'trade'; st.toTrade = true;                // вкладка «🤝 Торг» і підвезти до неї (renderTabs)
        }
        break;
      }
      case 'deal': act('deal', { id, seat: Number(b.dataset.seat) }); break;
      case 'cancel': act('cancel', { id }); break;
      case 'play': {
        st.devOpen = null;
        const k = b.dataset.k;
        if (k === 'plenty') st.dlg = { k, res: [] };
        else if (k === 'mono') st.dlg = { k, res: null };
        else act('play', { k });
        break;
      }
      case 'dlg-res':
        if (st.dlg && st.dlg.k === 'plenty' && st.dlg.res.length < 2) st.dlg.res.push(r);
        else if (st.dlg && st.dlg.k === 'mono') st.dlg.res = r;
        break;
      case 'dlg-pop': if (st.dlg && st.dlg.k === 'plenty') st.dlg.res.splice(Number(b.dataset.i), 1); break;
      case 'dlg-close': st.dlg = null; break;
      case 'dlg-ok':
        if (st.dlg && st.dlg.k === 'plenty' && st.dlg.res.length === 2) act('play', { k: 'plenty', res: st.dlg.res.slice() });
        else if (st.dlg && st.dlg.k === 'mono' && st.dlg.res) act('play', { k: 'mono', res: st.dlg.res });
        st.dlg = null;
        break;
      case 'disc-add': case 'disc-sub':
        if (st.disc.plan) st.disc.plan[r] = Math.max(0, (st.disc.plan[r] || 0) + (a === 'disc-add' ? 1 : -1));
        break;
      case 'disc-hide': st.disc.hidden = true; break;
      case 'disc-open': st.disc.hidden = false; break;
      case 'discard': if (st.disc.plan) act('discard', { res: M.K.compact(st.disc.plan) }); break;
      case 'steal': act('steal', { seat: Number(b.dataset.seat) }); break;
      default: return;
    }
    paint(root, ctx);
  }

  // ─── Реєстрація ─────────────────────────────────────────────────────────
  HGames.register({
    id: 'khutory',
    icon: ICON,
    seatClass: ['x', 'o', 'c', 'd'],
    added: '2026-10-28',
    /// Випав посеред партії й дивлюсь збоку — кнопка в шапці столу садить назад на свій хутір (core.js rejoinLabel).
    rejoin: (rv, nick) => (ownAway(rv && rv.view, nick) ? REJOIN : ''),

    mount(root, ctx) { cur = root; paint(root, ctx); },
    update(root, ctx) { cur = root; paint(root, ctx); },
    unmount(root) {
      const st = root._kh;
      if (st) {
        clearInterval(st.tick); clearTimeout(st.nt); clearTimeout(st.flashT); clearTimeout(st.fxT); clearTimeout(st.feedT); cancelAnimationFrame(st.mrAF);
        if (st.ro) st.ro.disconnect();
        if (st.onClick) root.removeEventListener('click', st.onClick);
        if (st.sc) { try { st.sc.dispose(); } catch (e) { /* уже прибрано */ } }
        if (st.snd) { try { st.snd.dispose(); } catch (e) { /* уже прибрано */ } st.snd = null; }
        stopBlink(st);
        st.queue.length = 0;
      }
      root._kh = null;
      if (cur === root) cur = null;
    },
    onKey(e) {
      if (e.key !== 'Escape' || !cur || !cur._kh) return false;
      const st = cur._kh;
      if (st.ask) { closeAsk(st); return true; }   // перепитування «тут і твоя хата» — Esc = «Обрати інший»
      if (st.pick || st.trade.open || st.dlg || st.bankGive || st.arm) {
        st.pick = null; st.trade.open = false; st.dlg = null; st.bankGive = null; st.arm = null;
        hideTip(st);
        paint(cur, st.ctx);
        return true;
      }
      return false;
    },
    status(ctx) {
      const room = (ctx && ctx.room) || {};
      if (room.status === 'lobby') {
        let people = 0;
        for (let i = 0; i < 4; i++) if (ctx.nickOf(i)) people++;
        const bots = +((room.options || {}).bots || 0);
        return people + bots < 2 ? 'Треба щонайменше двоє: поклич когось або додай 🤖 Глека' : '';
      }
      const v = ctx && ctx.view;
      if (!v || !v.players) return '';
      const st = cur && cur._kh;
      return whatNow(v, v.me || null, st).s;
    },
  });
})();
