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
    const pal = M ? M.C.SEAT_COLOR : ['#d64541', '#3c78d8', '#e8912d', '#8e6bd8'];
    return pal[p ? p.color : seat] || '#999';
  };
  const isBot = (v, seat) => { const p = playerOf(v, seat); return !!(p && (p.bot || p.away)); };
  const dot = (v, seat) => '<i class="kh-pdot" style="--pc:' + colorOf(v, seat) + '"></i>';
  const who = (v, seat) => dot(v, seat) + '<b>' + esc(nickOf(v, seat)) + '</b>';

  function state(root) {
    return root._kh || (root._kh = {
      built: false, ctx: null, view: null, sc: null, scKind: null, scLoading: false, boardId: null, legalSig: null, legal: null,
      pick: null, arm: null, seq: null, queue: [], running: false,
      trade: { open: false, give: {}, get: {}, to: null }, bankGive: null,
      disc: { key: null, plan: null }, dlg: null, logOpen: store.get('kh.log') !== '0',
      prevHand: null, pulse: {}, tick: 0, nt: 0, toKey: null, toAt: 0, ro: null, hover: null, marks: [],
    });
  }

  // ─── Що зараз робити (смуга над полем і status) ─────────────────────────
  function whatNow(v, me, st) {
    const w = whatNow0(v, me, st);
    const p = me && v && v.phase !== 'over' ? playerOf(v, me.seat) : null;
    if (p && p.away) w.l = '<b>🤖 За тебе зараз ходить Глек</b> (ти двічі проспав хід). Зроби будь-що сам — і гратимеш далі.<br>' + w.l;
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
          + ' <span class="kh-gold">⭐ Глек радить</span> — де родить найбільше' + (r ? '. Друга хата одразу дасть по карті з сусідніх земель' : '') };
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
    root.innerHTML = '<div class="kh">'
      + '<div class="kh-players"></div>'
      + '<div class="kh-sum" hidden></div>'
      + '<div class="kh-say" aria-live="polite"></div>'
      + '<div class="kh-main">'
      + '<div class="kh-fieldwrap"><div class="kh-field"></div><div class="kh-marks"></div><div class="kh-tip" hidden></div>'
      + '<div class="kh-flash" hidden></div><div class="kh-tools"><button type="button" class="kh-view" data-a="view"></button><button type="button" class="kh-view kh-snd" data-a="snd"></button></div>'
      + '<div class="kh-fab"></div>'
      + '<div class="kh-dlg" hidden></div></div>'
      + '<div class="kh-side"><div class="kh-act"></div><div class="kh-trade"></div><div class="kh-offers"></div><div class="kh-log"></div></div>'
      + '</div>'
      + '<div class="kh-hand"></div>'
      + '</div>';
    st.el = {};
    for (const k of ['players', 'sum', 'say', 'fieldwrap', 'field', 'marks', 'tip', 'flash', 'dlg', 'act', 'trade', 'offers', 'log', 'hand', 'view', 'fab', 'snd'])
      st.el[k] = root.querySelector('.kh-' + k);
    // Звук: типово вимкнено, стан пам'ятає сам модуль; AudioContext — лише на першому play() при ввімкненому.
    try { st.snd = M.S && M.S.createSound ? M.S.createSound() : null; } catch (e) { st.snd = null; }
    paintSnd(st);
    st.onClick = (e) => onClick(root, e);
    root.addEventListener('click', st.onClick);
    st.ro = typeof ResizeObserver === 'function' ? new ResizeObserver(() => { if (st.sc) st.sc.resize(); placeMarks(st); }) : null;
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
  /// Останні 5 с МОГО строку — «тік» раз на секунду.
  function tickSound(st, v) {
    const me = v.me;
    if (!st.snd || !st.snd.on || !me || !v.deadline || v.phase === 'over') return;
    const mine = !!me.must || (v.turn === me.seat && (v.phase === 'main' || v.phase === 'road2'));
    if (!mine) return;
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
        host.innerHTML = '<p class="kh-err">Не вдалося намалювати поле. Онови сторінку.</p>';
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
    }
    // «Глек радить» — 3 найкращі вершини в розстановці.
    st.marks = [];
    if (v.me && v.me.must === 'town' && v.phase === 'setup') {
      const have = {};
      for (const t of v.towns || []) if (t.seat === v.me.seat) for (const it of M.K.vertexYield(v.board, t.v).items) have[it.res] = 1;
      st.marks = M.K.adviseTowns(v.board, (v.me.spots || {}).town || [], 3, Object.keys(have).length ? have : null);
    }
    // Позначки — лише зірки (підпис «⭐ Глек радить» — у смузі над полем): довгий напис ховав сусідню зірку.
    setHtml(st.el.marks, st.marks.map((id, i) => '<span class="kh-mark' + (i === 0 ? ' best' : '') + '" data-i="' + i + '" title="Глек радить">⭐</span>').join(''));
    placeMarks(st);
    followMarks(st);
  }

  const rollHeld = (st) => !!st.rolling || st.queue.some((e) => e.k === 'roll');

  /// Поки є позначки — ставимо їх щокадру (камеру крутять мишею/пальцем; таймер на 250 мс відставав).
  function followMarks(st) {
    if (st.mrAF || !st.marks.length) return;
    const loop = () => {
      st.mrAF = 0;
      if (!st.el || !st.marks.length || !st.sc) return;
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
      if (st.marks.includes(id)) s += ' · <span class="kh-gold">⭐ Глек радить</span>';
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
    tip.innerHTML = html + (armed ? '<br><b class="kh-gold">Тицни ще раз — поставити</b>' : '');
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
      if (!root.firstChild) root.innerHTML = '<p class="kh-wait muted">Розкладаємо хутори…</p>';
      load().then(() => { if (root._kh === st) paint(root, st.ctx); }, (e) => { root.innerHTML = '<p class="kh-err">Не вдалося завантажити гру. Онови сторінку.</p>'; console.warn(e); });
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
    setHtml(st.el.say, '<div class="kh-say-t">' + w.l + '</div>' + (st.pick ? '<button type="button" class="kh-x" data-a="pick-cancel">✕ Передумав</button>' : ''));
    st.el.say.hidden = !!v.over;
    st.el.say.classList.toggle('mine', !!(me && (me.must || v.turn === me.seat) && v.phase !== 'over'));
    renderPlayers(st, v, me);
    renderAct(st, v, me);
    renderTrade(st, v, me);
    renderOffers(st, v, me);
    renderLog(st, v);
    renderHand(st, v, me);
    renderDlg(st, v, me);
    renderSum(st, v);
    st.el.view.textContent = st.scKind === '3d' ? '🗺 Пласка' : '🧊 3D';
    // Телефон: панель дій — під полем, тож головну дію ходу дублюємо кнопкою на самому полі (не гортати щоходу).
    const fab = me && me.must === 'roll' ? '<button type="button" class="kh-big primary" data-a="roll">🎲 Кинути</button>'
      : me && me.must === 'discard' && st.disc.hidden ? '<button type="button" class="kh-big primary" data-a="disc-open">🃏 Скинути</button>' : '';
    setHtml(st.el.fab, fab);
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
        h += '<i class="kh-dot' + (i >= have ? ' miss' : '') + '" style="--rc:' + M.C.RES_COLOR[r] + '" title="' + M.C.RES_NAME[r] + (i >= have ? ' — бракує' : '') + '">' + RES_ICON[r] + '</i>';
      }
    }
    return h;
  }

  function diceHtml(d) {
    if (!d) return '';
    const cell = { 1: [4], 2: [0, 8], 3: [0, 4, 8], 4: [0, 2, 6, 8], 5: [0, 2, 4, 6, 8], 6: [0, 2, 3, 5, 6, 8] };
    const die = (n, red) => '<span class="kh-die' + (red ? ' red' : '') + '">' + [0, 1, 2, 3, 4, 5, 6, 7, 8].map((i) => '<i' + (cell[n].includes(i) ? ' class="on"' : '') + '></i>').join('') + '</span>';
    return die(d[0]) + die(d[1], true) + '<b class="kh-dsum' + (d[0] + d[1] === 7 ? ' seven' : '') + '">= ' + (d[0] + d[1]) + '</b>';
  }

  function renderPlayers(st, v, me) {
    const thinking = new Set();
    if (v.phase === 'discard' && v.discard) for (const s of Object.keys(v.discard)) thinking.add(+s);
    else if (v.turn != null && v.phase !== 'over') thinking.add(v.turn);
    const L = 2 * Math.PI * 19;
    const h = v.players.map((p) => {
      const mine = me && me.seat === p.seat;
      const fin = v.over && v.over.points ? v.over.points[p.seat] : null;
      const pts = fin != null ? fin : mine && me.points != null ? me.points : p.points;
      const secret = fin == null && mine && me.points > p.points ? '<small title="Скарби в руці бачиш лише ти">+' + (me.points - p.points) + ' потай</small>' : '';
      const awards = (v.longest && v.longest.seat === p.seat ? '<span class="kh-aw" title="Чумацький шлях: найдовший шлях (' + v.longest.len + '), +2 очки">🛤 +2</span>' : '')
        + (v.army && v.army.seat === p.seat ? '<span class="kh-aw" title="Найсильніша сотня: найбільше козаків (' + v.army.n + '), +2 очки">⚔ +2</span>' : '');
      const think = thinking.has(p.seat);
      const name = esc(p.nick) + (p.bot ? '' : p.away ? ' <span title="Вийшов або проспав — за нього грає Глек">🤖</span>' : '');
      return '<div class="kh-pl' + (v.turn === p.seat && v.phase !== 'over' ? ' turn' : '') + (mine ? ' me' : '') + '" data-seat="' + p.seat + '" style="--pc:' + colorOf(v, p.seat) + '">'
        + '<span class="kh-av"><svg viewBox="0 0 44 44" aria-hidden="true"><circle class="bg" cx="22" cy="22" r="19"/>'
        + (think && v.deadline ? '<circle class="fg" cx="22" cy="22" r="19" transform="rotate(-90 22 22)" stroke-dasharray="' + L.toFixed(1) + '"/>' : '')
        + '</svg><b>' + (p.bot ? '🤖' : esc(String(p.nick || '?').trim().charAt(0).toUpperCase())) + '</b></span>'
        + '<span class="kh-pi"><span class="kh-pn">' + name + (mine ? ' <em>(ти)</em>' : '') + '</span>'
        + '<span class="kh-ps"><b title="Очки (до ' + v.target + ')">⭐ ' + pts + '</b>' + secret
        + '<span title="Карт у руці">🂠 ' + p.cards + '</span><span title="Грамот">📜 ' + p.dev + '</span>'
        + (p.knights ? '<span title="Зіграних козаків">⚔ ' + p.knights + '</span>' : '') + '</span>'
        + (awards ? '<span class="kh-ps">' + awards + '</span>' : '') + '</span>'
        + (think ? '<span class="kh-sec" data-sec></span>' : '')
        + '</div>';
    }).join('');
    setHtml(st.el.players, '<div class="kh-target" title="Хто першим набере стільки очок у свій хід — переміг">до ' + v.target + ' ⭐</div>' + h);
    tickArcs(st);
  }

  function renderAct(st, v, me) {
    let h = '';
    if (v.phase === 'over' || v.phase === 'setup') { setHtml(st.el.act, ''); return; }
    const myTurn = me && v.turn === me.seat;
    const dice = rollHeld(st) ? null : v.dice;
    h += '<div class="kh-dice">' + (dice ? diceHtml(dice) : '<span class="muted">' + (v.dice ? '🎲 котяться…' : 'кубики ще не кинуто') + '</span>') + '</div>';
    if (me && me.must === 'roll') {
      h += '<button type="button" class="kh-big primary" data-a="roll">🎲 Кинути кубики</button>';
      if ((me.dev || []).some((d) => d.k === 'knight' && !d.fresh) && !me.playedDev) h += '<p class="kh-note">Можна спершу зіграти ⚔️ Козака з руки — прогнати Кармелюка зі своєї землі</p>';
    } else if (myTurn && v.phase === 'main') {
      const can = me.can || {}, why = me.why || {}, sp = me.spots || {};
      h += '<div class="kh-builds">';
      for (const k of ['road', 'town', 'city', 'dev']) {
        const room = k === 'dev' || (sp[k] || []).length > 0;
        const ok = !!can[k] && room;
        const reason = why[k] || (!room && can[k] ? 'нема де поставити' : !can[k] && k === 'dev' && !v.devLeft ? 'грамоти скінчились' : '');
        const on = st.pick === k;
        h += '<button type="button" class="kh-b' + (on ? ' on' : '') + (ok ? ' ok' : '') + '" data-a="build" data-k="' + k + '"' + (ok || on ? '' : ' disabled')
          + ' title="' + esc(k === 'dev' ? 'Грамота: козак, скарб, Толока, Врожайний рік або Ярмарок' : M.C.BUILD_NAME[k]) + '">'
          + '<span class="kh-bn">' + BUILD_ICON[k] + ' ' + (on ? '✕ Скасувати' : M.C.BUILD_NAME[k]) + '</span>'
          + '<span class="kh-cost">' + costDots(k, me.hand) + '</span>'
          + (ok || on ? '<span class="kh-why ok">' + (on ? 'обери місце на полі' : k === 'dev' ? 'купити' : 'можна!') + '</span>' : '<span class="kh-why">' + esc(reason || 'не зараз') + '</span>')
          + '</button>';
      }
      h += '</div>';
      h += '<div class="kh-row"><button type="button" class="kh-mid' + (st.trade.open ? ' on' : '') + '" data-a="trade">🤝 Торгувати</button>'
        + '<button type="button" class="kh-mid primary" data-a="end">✅ Завершити хід</button></div>';
    } else if (me && v.phase === 'main' && v.turn != null && !isBot(v, v.turn) && v.rolled !== false) {
      h += '<button type="button" class="kh-mid' + (st.trade.open ? ' on' : '') + '" data-a="trade">🤝 Запропонувати обмін ' + esc(nickOf(v, v.turn)) + '</button>';
    } else if (me && me.must === 'discard') {
      h += '<button type="button" class="kh-big primary" data-a="disc-open">🃏 Скинути карти</button>';
    }
    if (me && v.phase === 'main' && v.turn != null && isBot(v, v.turn) && !myTurn) h += '<p class="kh-note">Глек торгується сам — з ним обмін не відкриваємо</p>';
    if (myTurn && v.phase === 'main') {
      const rates = me.rates || {};
      const best = Math.min(...RES.map((r) => rates[r] || 4));
      h += '<p class="kh-note kh-bnote">Банк міняє ' + best + ' однакові на 1 будь-яку' + (best < 4 ? ' (твої пристані)' : '') + ' — у «Торгувати»</p>';
    }
    setHtml(st.el.act, h);
  }

  function resStepper(side, counts, hand, limitByHand) {
    return '<div class="kh-steps">' + RES.map((r) => {
      const n = counts[r] || 0;
      const max = limitByHand ? (hand[r] || 0) : 19;
      return '<span class="kh-step" style="--rc:' + M.C.RES_COLOR[r] + '"><span class="kh-ri" title="' + M.C.RES_NAME[r] + '">' + RES_ICON[r] + '</span>'
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
    let h = '<div class="kh-box"><div class="kh-boxh">🤝 ' + (myTurn ? 'Обмін із сусідами' : 'Зустрічна для ' + esc(nickOf(v, v.turn)))
      + '<button type="button" class="kh-x" data-a="trade" aria-label="Закрити">✕</button></div>';
    h += '<div class="kh-trl">Даю</div>' + resStepper('give', T.give, hand, true);
    h += '<div class="kh-trl">Хочу</div>' + resStepper('get', T.get, hand, false);
    if (myTurn) {
      const others = v.players.filter((p) => p.seat !== me.seat);
      h += '<div class="kh-to">Кому: <button type="button" class="kh-chip' + (T.to == null ? ' on' : '') + '" data-a="tr-to" data-to="">Усім</button>'
        + others.map((p) => '<button type="button" class="kh-chip' + (T.to === p.seat ? ' on' : '') + '" data-a="tr-to" data-to="' + p.seat + '">' + who(v, p.seat) + '</button>').join('') + '</div>';
    }
    h += '<button type="button" class="kh-mid primary" data-a="offer"' + (ready ? '' : ' disabled') + '>'
      + (ready ? 'Запропонувати: ' + esc(K.resList(give)) + ' ⇄ ' + esc(K.resList(get)) : 'Обери, що даєш і що хочеш') + '</button>';
    if (myTurn) {
      const ch = K.bankChoices(hand, me.rates);
      h += '<div class="kh-bank"><div class="kh-trl">🏦 Банк — віддай однакові, візьми 1 будь-яку</div><div class="kh-bankrow">'
        + ch.map((c) => '<button type="button" class="kh-chip' + (st.bankGive === c.res ? ' on' : '') + '" data-a="bank-give" data-r="' + c.res + '"' + (c.can ? '' : ' disabled')
          + ' title="' + (c.can ? '' : 'є ' + c.have + ' з ' + c.rate) + '">' + c.rate + ' ' + RES_ICON[c.res] + ' → 1</button>').join('') + '</div>';
      if (st.bankGive) {
        const r0 = st.bankGive, rate = (me.rates || {})[r0] || 4;
        h += '<div class="kh-trl">Що береш за ' + esc(K.resPhrase(r0, rate)) + '?</div><div class="kh-bankrow">'
          + RES.filter((r) => r !== r0).map((r) => '<button type="button" class="kh-chip big" data-a="bank-get" data-r="' + r + '" style="--rc:' + M.C.RES_COLOR[r] + '">' + RES_ICON[r] + ' ' + M.C.RES_NAME[r] + '</button>').join('')
          + '</div>';
      }
      h += '</div>';
    }
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
      let s = '<div class="kh-offer' + (toMe || counterToMe ? ' forme' : '') + '">';
      s += '<div class="kh-ot">' + (mine ? '<b>Твоя пропозиція</b>' : who(v, o.from) + (fromCur ? '' : ' <span class="muted">(зустрічна)</span>'))
        + (fromCur ? ' <span class="muted">→ ' + (o.to == null ? 'усім' : esc(nickOf(v, o.to))) + '</span>' : '') + '</div>';
      s += '<div class="kh-od">' + (mine ? 'даєш' : 'дає') + ' <b>' + esc(K.resList(o.give)) + '</b> · ' + (mine ? 'хочеш' : 'хоче') + ' <b>' + esc(K.resList(o.get)) + '</b></div>';
      const acc = o.acc || [], dec = o.dec || [];
      if (fromCur && (acc.length || dec.length)) {
        s += '<div class="kh-oa">' + acc.map((x) => '<span class="ok">✅ ' + esc(nickOf(v, x)) + '</span>').join(' ') + ' '
          + dec.map((x) => '<span class="no">❌ ' + esc(nickOf(v, x)) + '</span>').join(' ') + '</div>';
      }
      const btn = [];
      if (mine && me.seat === v.turn) {
        for (const x of acc) btn.push('<button type="button" class="primary" data-a="deal" data-id="' + o.id + '" data-seat="' + x + '">🤝 Обміняти з ' + esc(nickOf(v, x)) + '</button>');
        if (!acc.length) btn.push('<span class="muted small">чекаємо, хто погодиться…</span>');
      }
      if (mine) btn.push('<button type="button" data-a="cancel" data-id="' + o.id + '">Скасувати</button>');
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
      if (btn.length) s += '<div class="kh-ob">' + btn.join('') + '</div>';
      return s + '</div>';
    }).join('');
    setHtml(st.el.offers, '<div class="kh-box"><div class="kh-boxh">📨 Пропозиції</div>' + h + '</div>');
  }

  const HELP = '<div class="kh-box kh-help"><div class="kh-boxh">❔ Як грати<button type="button" class="kh-x" data-a="help" aria-label="Сховати">✕</button></div><ol>'
    + '<li><b>Кидай кубики</b> 🎲 — кожна земля з цим числом дає карту всім, чия хата стоїть на її куті. 🔴 6 і 8 випадають найчастіше</li>'
    + '<li><b>Будуй</b> за карти: 🛤 шлях веде до нових місць, 🏠 хата = 1 ⭐, 🏛 містечко = 2 ⭐ і вдвічі більше карт</li>'
    + '<li><b>Торгуйся</b> 🤝 з сусідами або з банком (4 однакові → 1 будь-яка; біля пристані ⚓ — вигідніше)</li>'
    + '<li>Випала <b>сімка</b> — приходить Кармелюк 🥷: хто має понад 7 карт, віддає половину</li>'
    + '<li>Перший, хто набере потрібні ⭐ у свій хід, — <b>переміг</b></li></ol></div>';

  function renderLog(st, v) {
    const lines = (v.log || []).slice(-6);
    const help = st.helpOpen != null ? st.helpOpen : v.phase === 'setup';
    const hb = help ? HELP : '<button type="button" class="kh-logh" data-a="help">❔ Як грати</button>';
    if (!lines.length) { setHtml(st.el.log, hb); return; }
    setHtml(st.el.log, hb + '<button type="button" class="kh-logh" data-a="log" aria-expanded="' + st.logOpen + '">📜 Що було ' + (st.logOpen ? '▾' : '▸') + '</button>'
      + (st.logOpen ? '<ol>' + lines.map((l) => '<li>' + esc(l) + '</li>').join('') + '</ol>' : '<p class="kh-last">' + esc(lines[lines.length - 1]) + '</p>'));
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
    let h = '<div class="kh-hh"><b>Твоя рука</b> · ' + n + ' ' + plural(n, 'карта', 'карти', 'карт')
      + (n > 7 ? ' <span class="kh-warn" title="Коли випаде сімка, віддаси половину">⚠ понад 7 — на сімці скинеш половину</span>' : '') + '</div>';
    if (!n && !(me.dev || []).length) {
      setHtml(st.el.hand, h + '<p class="kh-note">Карт поки нема. Вони прийдуть самі: коли кубики покажуть число на землі біля твоєї хати.</p>');
      return;
    }
    h += '<div class="kh-cards">' + RES.map((r) => '<div class="kh-card' + ((hand[r] || 0) ? '' : ' zero') + ((st.pulse[r] || 0) > now ? ' new' : '') + '" style="--rc:' + C.RES_COLOR[r] + '">'
      + '<span class="kh-ci">' + RES_ICON[r] + '</span><b>' + (hand[r] || 0) + '</b><span class="kh-cn">' + C.RES_NAME[r] + '</span></div>').join('');
    for (let i = 0; i < (me.dev || []).length; i++) {
      const d = me.dev[i];
      const s = devState(v, me, d);
      h += '<div class="kh-card dev' + (s.ok ? ' ok' : ' off') + '" title="' + esc(C.DEV_HINT[d.k] || '') + '">'
        + '<span class="kh-ci">' + (DEV_ICON[d.k] || '📜') + '</span><span class="kh-dn">' + esc(C.DEV_NAME[d.k] || d.k) + '</span>'
        + (s.ok ? '<button type="button" class="primary" data-a="play" data-k="' + d.k + '">Зіграти</button>' : '<span class="kh-cw">' + esc(s.why) + '</span>')
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
        + '<div class="kh-disc">' + RES.map((r) => {
          const has = (me.hand || {})[r] || 0, n = plan[r] || 0;
          return '<div class="kh-dr" style="--rc:' + C.RES_COLOR[r] + '"><span class="kh-ri">' + RES_ICON[r] + '</span><small>є ' + has + '</small>'
            + '<button type="button" data-a="disc-add" data-r="' + r + '"' + (n < has && sum < need ? '' : ' disabled') + ' aria-label="скинути більше">+</button>'
            + '<b>' + n + '</b>'
            + '<button type="button" data-a="disc-sub" data-r="' + r + '"' + (n ? '' : ' disabled') + ' aria-label="скинути менше">−</button></div>';
        }).join('') + '</div>'
        + '<button type="button" class="kh-big primary" data-a="discard"' + (sum === need ? '' : ' disabled') + '>'
        + (sum === need ? 'Скинути ' + esc(K.resList(K.compact(plan))) : sum < need ? 'Обери ще ' + (need - sum) : 'Забагато на ' + (sum - need)) + '</button>'
        + '<button type="button" class="kh-link" data-a="disc-hide">глянути на поле</button>';
    } else if (me && me.must === 'steal') {
      h = '<h3>🥷 Кого обібрати?</h3><p>Кармелюк забере одну випадкову карту.</p><div class="kh-vict">'
        + (me.steal || []).map((s) => {
          const p = playerOf(v, s) || {};
          return '<button type="button" class="kh-big" data-a="steal" data-seat="' + s + '">' + who(v, s) + ' <span class="muted">· ' + (p.cards || 0) + ' 🂠 · ⭐ ' + (p.points || 0) + '</span></button>';
        }).join('') + '</div>';
    } else if (st.dlg && st.dlg.k === 'plenty') {
      const picks = st.dlg.res;
      h = '<h3>🌻 Врожайний рік</h3><p>Візьми з банку будь-які дві карти (можна однакові).</p>'
        + '<div class="kh-pickres">' + RES.map((r) => '<button type="button" class="kh-chip big" data-a="dlg-res" data-r="' + r + '" style="--rc:' + C.RES_COLOR[r] + '"' + (picks.length < 2 && (v.bank || {})[r] !== 0 ? '' : ' disabled') + '>' + RES_ICON[r] + ' ' + C.RES_NAME[r] + '</button>').join('') + '</div>'
        + '<p class="kh-picked">Береш: ' + (picks.length ? picks.map((r, i) => '<button type="button" class="kh-chip on" data-a="dlg-pop" data-i="' + i + '">' + RES_ICON[r] + ' ' + C.RES_NAME[r] + ' ✕</button>').join(' ') : '<span class="muted">ще нічого</span>') + '</p>'
        + '<div class="kh-row"><button type="button" class="kh-mid" data-a="dlg-close">Не зараз</button><button type="button" class="kh-mid primary" data-a="dlg-ok"' + (picks.length === 2 ? '' : ' disabled') + '>Взяти</button></div>';
    } else if (st.dlg && st.dlg.k === 'mono') {
      const r0 = st.dlg.res;
      h = '<h3>🎪 Ярмарок</h3><p>Назви ресурс — усі віддадуть тобі весь свій.</p>'
        + '<div class="kh-pickres">' + RES.map((r) => '<button type="button" class="kh-chip big' + (r0 === r ? ' on' : '') + '" data-a="dlg-res" data-r="' + r + '" style="--rc:' + C.RES_COLOR[r] + '">' + RES_ICON[r] + ' ' + C.RES_NAME[r] + '</button>').join('') + '</div>'
        + '<div class="kh-row"><button type="button" class="kh-mid" data-a="dlg-close">Не зараз</button><button type="button" class="kh-mid primary" data-a="dlg-ok"' + (r0 ? '' : ' disabled') + '>' + (r0 ? 'Збирай ' + C.RES_GEN[r0] + '!' : 'Обери ресурс') + '</button></div>';
    }
    setHtml(st.el.dlg, h ? '<div class="kh-dlgin">' + h + '</div>' : '');
    st.el.dlg.hidden = !h;
  }

  function renderSum(st, v) {
    const o = v.over;
    if (!o) { setHtml(st.el.sum, ''); st.el.sum.hidden = true; return; }
    st.el.sum.hidden = false;
    const K = M.K, s = o.stats || {};
    const pts = o.points || [];
    const order = v.players.map((p) => p.seat).sort((a, b) => (pts[b] || 0) - (pts[a] || 0));
    // «Ану ще раз» каркаса — під полем і журналом, новачок його не бачить: дублюємо в шапці підсумку (тисне ту саму кнопку).
    const again = st.ctx && st.ctx.mine ? '<button type="button" class="kh-mid primary kh-again" data-a="again">🔁 Ану ще раз</button>' : '';
    let h = '<div class="kh-sumh"><h3>' + (o.winner != null ? '🏆 Переміг ' + who(v, o.winner) + ' — ' + (pts[o.winner] || 0) + ' ⭐' : '🏁 Партію скінчено')
      + (o.reason === 'left' ? ' <span class="muted">(решта вийшли)</span>' : '') + '</h3>' + again + '</div>';
    h += '<div class="kh-sumgrid"><div><ol class="kh-rank">' + order.map((x) => '<li>' + who(v, x) + ' <b>' + (pts[x] || 0) + ' ⭐</b></li>').join('') + '</ol>';
    if (s.rounds) h += '<p class="muted small">' + s.rounds + ' ' + plural(s.rounds, 'коло', 'кола', 'кіл') + '</p>';
    h += '</div>';
    // Графік очок за колами.
    if (s.timeline && s.timeline.length > 1) {
      const W = 320, H = 170;
      const g = K.chartGeom(s.timeline, W, H, v.target);
      let svg = '<svg class="kh-chart" viewBox="0 0 ' + W + ' ' + H + '" role="img" aria-label="Очки за колами">';
      for (let p = 0; p <= g.max; p += 2) svg += '<line x1="22" x2="' + (W - 22) + '" y1="' + g.y(p) + '" y2="' + g.y(p) + '" class="grid"/><text x="16" y="' + (g.y(p) + 4) + '" class="ax">' + p + '</text>';
      if (g.targetY != null) svg += '<line x1="22" x2="' + (W - 22) + '" y1="' + g.targetY + '" y2="' + g.targetY + '" class="goal"/><text x="' + (W - 22) + '" y="' + (g.targetY - 4) + '" class="ax" text-anchor="end">мета ' + v.target + '</text>';
      g.lines.forEach((pl, i) => {
        const seat = v.players[i] ? v.players[i].seat : i;
        svg += '<polyline points="' + pl.map((p) => p.join(',')).join(' ') + '" style="stroke:' + colorOf(v, seat) + '"' + (seat === o.winner ? ' class="win"' : '') + '/>';
      });
      svg += '<text x="' + (W / 2) + '" y="' + (H - 4) + '" class="ax" text-anchor="middle">кола →</text></svg>';
      h += '<div><div class="kh-trl">Очки за колами</div>' + svg + '</div>';
    }
    // Гістограма кубиків.
    if (s.dice && s.dice.length === 11) {
      const mx = Math.max(1, ...s.dice);
      h += '<div><div class="kh-trl">Що випадало</div><div class="kh-hist">' + s.dice.map((n, i) => '<span class="' + (i === 5 ? 'seven' : '') + '" title="' + (i + 2) + ': ' + n + ' раз"><i style="height:' + Math.round(n / mx * 100) + '%"></i><b>' + (i + 2) + '</b></span>').join('')
        + '</div><p class="muted small">сімка — Кармелюк приходив ' + (s.sevens != null ? s.sevens : s.dice[5]) + ' ' + plural(s.sevens != null ? s.sevens : s.dice[5], 'раз', 'рази', 'разів') + '</p></div>';
    }
    h += '</div>';
    if (s.seats && s.seats.length) {
      h += '<div class="kh-tbl"><table class="kh-stats"><thead><tr><th></th><th>🤝<br><small>обмінів</small></th><th>🂠<br><small>отримав карт</small></th><th>🥷<br><small>вкрав</small></th><th>🛤<br><small>шляхів</small></th><th>🏠<br><small>хат</small></th><th>🏛<br><small>містечок</small></th></tr></thead><tbody>'
        + s.seats.map((x) => '<tr><td>' + who(v, x.seat) + '</td><td>' + x.trades + '</td><td>' + x.got + '</td><td>' + x.stolen + '</td><td>' + ((x.built || {}).road || 0) + '</td><td>' + ((x.built || {}).town || 0) + '</td><td>' + ((x.built || {}).city || 0) + '</td></tr>').join('')
        + '</tbody></table></div>';
    }
    setHtml(st.el.sum, h);
  }

  // ─── Таймер, боти, «час минув» ──────────────────────────────────────────
  function tickArcs(st) {
    const v = st.view;
    if (!v || !st.el) return;
    const left = v.deadline ? Math.max(0, Date.parse(v.deadline) - Date.now()) : null;
    const total = v.timerMs || 90000;
    const L = 2 * Math.PI * 19;
    for (const fg of st.el.players.querySelectorAll('.kh-av .fg')) {
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
    const st = root._kh;
    if (!st || !st.view) return;
    tickArcs(st);
    if (st.marks.length) placeMarks(st);
    tickSound(st, st.view);
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
      const chip = st.el.players.querySelector('.kh-pl[data-seat="' + s + '"]');
      if (!chip) continue;
      const b = document.createElement('span');
      b.className = 'kh-gain';
      b.textContent = Object.keys(by[s]).map((r) => '+' + by[s][r] + ' ' + RES_ICON[r]).join(' ');
      chip.appendChild(b);
      setTimeout(() => b.remove(), 1900);
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
      case 'help': st.helpOpen = !(st.helpOpen != null ? st.helpOpen : v && v.phase === 'setup'); break;
      case 'log': st.logOpen = !st.logOpen; store.set('kh.log', st.logOpen ? '1' : '0'); break;
      case 'roll': act('roll', {}); break;
      case 'end': T.open = false; st.pick = null; st.bankGive = null; act('end', {}); break;
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
        if (T.open) { st.pick = null; T.give = {}; T.get = {}; T.to = null; }
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
      case 'bank-give': st.bankGive = st.bankGive === r ? null : r; break;
      case 'bank-get': if (st.bankGive) act('bank', { give: st.bankGive, get: r, n: 1 }); st.bankGive = null; break;
      case 'ans': act('answer', { id, ok: b.dataset.ok === '1' }); break;
      case 'counter': {
        const o = ((v && v.offers) || []).find((x) => x.id === id);
        if (o) { T.open = true; T.give = { ...o.get }; T.get = { ...o.give }; T.to = null; }
        break;
      }
      case 'deal': act('deal', { id, seat: Number(b.dataset.seat) }); break;
      case 'cancel': act('cancel', { id }); break;
      case 'play': {
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

    mount(root, ctx) { cur = root; paint(root, ctx); },
    update(root, ctx) { cur = root; paint(root, ctx); },
    unmount(root) {
      const st = root._kh;
      if (st) {
        clearInterval(st.tick); clearTimeout(st.nt); clearTimeout(st.flashT); cancelAnimationFrame(st.mrAF);
        if (st.ro) st.ro.disconnect();
        if (st.onClick) root.removeEventListener('click', st.onClick);
        if (st.sc) { try { st.sc.dispose(); } catch (e) { /* уже прибрано */ } }
        if (st.snd) { try { st.snd.dispose(); } catch (e) { /* уже прибрано */ } st.snd = null; }
        st.queue.length = 0;
      }
      root._kh = null;
      if (cur === root) cur = null;
    },
    onKey(e) {
      if (e.key !== 'Escape' || !cur || !cur._kh) return false;
      const st = cur._kh;
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
