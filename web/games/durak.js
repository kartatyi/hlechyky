/*
  Дурень підкидний на двох. Правила живуть на сервері (Impl/Durak.cs) — тут лише рендер і наміри.

  Вид (Hidden, свій на кожне місце):
    { turn, attacker, defender, phase: 'attack'|'defend'|'taking'|'done', trump: '♥', trumpCard: '7♥'|null,
      deck, table: [{ attack: '7♥', defend: '9♥'|null }], hand: string[]|null, counts: [n, n],
      discard, canAdd,
      result: null | { winner: 0|1|null, reason: 'out'|'both'|'left', foolNick: string|null } }
    foolNick є лише тоді, коли хтось встав з-за столу: його місце вже порожнє, і ctx.nickOf імені не дасть.

  Наміри: act('attack', { card }), act('defend', { attack, card }), act('take'), act('done').

  Захист двокроковий і в обидва боки: клік по своїй карті підсвічує атаки, які нею б'ються, клік по
  атаці — карти, якими її взяти. Другий клік ходить. Так само зручно і мишею, і пальцем.
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<rect x="1.2" y="4.2" width="6.4" height="9.4" rx="1.5" fill="var(--panel2)" stroke="var(--muted)" stroke-width="1.3"/>'
    + '<rect x="7.4" y="2.6" width="7.2" height="11" rx="1.5" fill="var(--panel)" stroke="var(--accent)" stroke-width="1.3"/>'
    + '<path d="M11 5.4 12.9 8.1 11 10.8 9.1 8.1Z" fill="var(--danger)"/></svg>';

  const RANKS = ['6', '7', '8', '9', '10', 'J', 'Q', 'K', 'A'];
  const RED = ['♥', '♦'];

  const suitOf = (c) => String(c || '').slice(-1);
  const rankTextOf = (c) => String(c || '').slice(0, -1);
  const rankOf = (c) => RANKS.indexOf(rankTextOf(c));
  const isRed = (c) => RED.indexOf(suitOf(c)) >= 0;

  /// Ті самі правила старшинства, що й на сервері — але лише щоб гасити недоступні карти.
  /// Сервер усе одно перевіряє сам: клієнт тут нічого не вирішує.
  const beats = (card, against, trump) => (suitOf(card) === suitOf(against)
    ? rankOf(card) > rankOf(against)
    : suitOf(card) === trump);

  function cardHtml(ctx, card, cls) {
    const extra = (isRed(card) ? ' red' : '') + (cls ? ' ' + cls : '');
    return '<span class="dcard' + extra + '"><b>' + ctx.esc(rankTextOf(card)) + '</b><i>' + ctx.esc(suitOf(card)) + '</i></span>';
  }

  function state(root) {
    if (!root._durak) root._durak = { sel: null, atk: null };
    return root._durak;
  }

  /// Скелет ставимо раз: далі кожна частина перемальовується окремо, щоб клік не гасив :hover сусідів.
  function skeleton(root) {
    let el = root.querySelector(':scope > .durak');
    if (el) return el;
    el = document.createElement('div');
    el.className = 'durak';
    el.innerHTML = '<div class="dtop"></div><div class="dtable"></div><div class="dhand"></div><div class="dbtns"></div>';
    root.appendChild(el);
    return el;
  }

  function setHtml(el, html) {
    if (el.dataset.sig !== html) { el.dataset.sig = html; el.innerHTML = html; }
  }

  function top(host, ctx, v) {
    const other = ctx.seat === 0 ? 1 : 0;
    const counts = v.counts || [0, 0];
    const bits = [];
    // Козирну карту показуємо, поки вона лежить під колодою; забрали — лишається сама масть.
    const trumpChip = v.trumpCard
      ? cardHtml(ctx, v.trumpCard, 'sm')
      : '<span class="dsuit' + (isRed(v.trump) ? ' red' : '') + '">' + ctx.esc(v.trump || '') + '</span>';
    bits.push('<span class="dchip">козир ' + trumpChip + '</span>');
    bits.push('<span class="dchip">колода <b>' + (v.deck || 0) + '</b></span>');
    bits.push('<span class="dchip">відбій <b>' + (v.discard || 0) + '</b></span>');
    if (ctx.mine) bits.push('<span class="dchip">у суперника <b>' + (counts[other] || 0) + '</b></span>');
    else bits.push('<span class="dchip">на руках <b>' + counts[0] + '</b> : <b>' + counts[1] + '</b></span>');
    setHtml(host, bits.join(''));
  }

  function board(host, ctx, v, st, hot) {
    const table = v.table || [];
    if (!table.length) {
      setHtml(host, '<span class="dempty">стіл порожній</span>');
      return;
    }
    const html = table.map((p) => {
      const pick = hot.indexOf(p.attack) >= 0;
      const cls = 'dpair' + (p.defend ? ' beaten' : '') + (pick ? ' hot' : '') + (p.attack === st.atk ? ' pick' : '');
      return '<div class="' + cls + '" data-c="' + ctx.esc(p.attack) + '">'
        + cardHtml(ctx, p.attack, 'atk')
        + (p.defend ? cardHtml(ctx, p.defend, 'def') : '')
        + '</div>';
    }).join('');
    setHtml(host, html);
  }

  function buttons(host, ctx, v, iAttack, iDefend, live) {
    const out = [];
    if (live && iAttack && v.phase === 'attack' && (v.table || []).length) out.push('<button class="primary" data-do="done">Біто</button>');
    if (live && iAttack && v.phase === 'taking') out.push('<button class="primary" data-do="done">Досить</button>');
    if (live && iDefend && v.phase === 'defend') out.push('<button class="ghost" data-do="take">Беру</button>');
    setHtml(host, out.join(''));
  }

  function paint(root, ctx) {
    const v = ctx.view || {};
    const st = state(root);
    const el = skeleton(root);
    const hand = v.hand || [];
    const table = v.table || [];
    const counts = v.counts || [0, 0];
    const trump = v.trump || '';
    const live = ctx.playing && !v.result;
    const iAttack = ctx.mine && ctx.seat === v.attacker;
    const iDefend = ctx.mine && ctx.seat === v.defender;
    const defending = live && iDefend && v.phase === 'defend';
    const attacking = live && iAttack && (v.phase === 'attack' || v.phase === 'taking') && !!v.canAdd;

    // Карти роздають на старті: поки стіл порожній і колоди нема — партія ще не почалась.
    if (!v.result && !v.deck && !counts[0] && !counts[1] && !table.length) {
      setHtml(el.querySelector('.dtop'), '');
      setHtml(el.querySelector('.dtable'), '<span class="dempty">карти ще не роздані</span>');
      setHtml(el.querySelector('.dbtns'), '');
      HGames.ui.hand(el.querySelector('.dhand'), [], {});
      return;
    }

    // Вибір міг протухнути: карту вже зіграно, атаку вже побито.
    if (st.sel && hand.indexOf(st.sel) < 0) st.sel = null;
    const open = table.filter((p) => !p.defend).map((p) => p.attack);
    if (st.atk && open.indexOf(st.atk) < 0) st.atk = null;
    if (!defending) { st.sel = null; st.atk = null; }

    // Що зараз можна класти: атакуючому — свій номінал, захисникові — те, чим б'ється хоч одна атака.
    const ranksOnTable = {};
    for (const p of table) { ranksOnTable[rankTextOf(p.attack)] = 1; if (p.defend) ranksOnTable[rankTextOf(p.defend)] = 1; }
    const usable = (card) => {
      if (attacking) return !table.length || ranksOnTable[rankTextOf(card)] === 1;
      if (defending) return st.atk ? beats(card, st.atk, trump) : open.some((a) => beats(card, a, trump));
      return false;
    };
    // Підсвічуємо атаки, які беруться обраною картою (або всі живі, поки нічого не обрано).
    const hot = defending ? (st.sel ? open.filter((a) => beats(st.sel, a, trump)) : open) : [];

    top(el.querySelector('.dtop'), ctx, v);
    board(el.querySelector('.dtable'), ctx, v, st, hot);
    buttons(el.querySelector('.dbtns'), ctx, v, iAttack, iDefend, live);

    const items = hand.map((c) => ({
      card: c,
      cls: [isRed(c) ? 'red' : '', c === st.sel ? 'sel' : ''].filter(Boolean).join(' '),
      disabled: !usable(c),
    }));
    HGames.ui.hand(el.querySelector('.dhand'), items, {
      render: (it) => '<b>' + ctx.esc(rankTextOf(it.card)) + '</b><i>' + ctx.esc(suitOf(it.card)) + '</i>',
      onItem: (it) => {
        if (attacking) { ctx.act('attack', { card: it.card }); return; }
        if (!defending) return;
        if (st.atk && beats(it.card, st.atk, trump)) { ctx.act('defend', { attack: st.atk, card: it.card }); st.sel = null; st.atk = null; return; }
        st.sel = st.sel === it.card ? null : it.card;
        paint(root, ctx);
      },
    });

    // Слухач столу вішаємо раз, а свіжий стан беремо з елемента — так само, як це робить core.js.
    const tb = el.querySelector('.dtable');
    tb._on = (attack) => {
      // Побиту пару чіпати нема сенсу: сервер відповів би «Цю карту вже побито».
      if (!defending || open.indexOf(attack) < 0) return;
      if (st.sel && beats(st.sel, attack, trump)) { ctx.act('defend', { attack, card: st.sel }); st.sel = null; st.atk = null; return; }
      st.atk = st.atk === attack ? null : attack;
      paint(root, ctx);
    };
    if (!tb._wired) {
      tb._wired = 1;
      tb.addEventListener('click', (e) => {
        const pair = e.target.closest('.dpair');
        if (pair && tb._on) tb._on(pair.dataset.c);
      });
    }
    const bt = el.querySelector('.dbtns');
    bt._act = ctx.act;
    if (!bt._wired) {
      bt._wired = 1;
      bt.addEventListener('click', (e) => {
        const b = e.target.closest('[data-do]');
        if (b && bt._act) bt._act(b.dataset.do);
      });
    }
  }

  HGames.register({
    id: 'durak',
    icon: ICON,
    seatNames: ['перший', 'другий'],
    seatClass: ['x', 'o'],

    mount(root, ctx) { paint(root, ctx); },
    update(root, ctx) { paint(root, ctx); },
    unmount(root) { root._durak = null; },

    status(ctx) {
      const v = ctx.view || {};
      if (!ctx.playing && !v.result) return '';
      if (v.result) {
        if (v.result.reason === 'both') return 'Вийшли разом — нічия';
        if (v.result.winner == null) return '';
        const fool = v.result.winner === 0 ? 1 : 0;
        // Той, хто встав, уже не сидить — його нік бережемо у виді, інакше вийшло б «Дурень — перший».
        const name = v.result.foolNick || ctx.nickOf(fool) || ctx.seatName(fool);
        return ctx.seat === fool ? 'Дурень цього разу ти' : 'Дурень — ' + name;
      }
      if (!ctx.mine) return 'Дивишся збоку';
      const iAttack = ctx.seat === v.attacker;
      if (v.phase === 'defend') return iAttack ? 'Суперник відбивається' : 'Відбивайся або бери';
      if (v.phase === 'taking') return iAttack ? (v.canAdd ? 'Підкидай або «Досить»' : 'Досить') : 'Суперник добирає, що підкинути';
      // Фаза attack із непорожнім столом — це «усе побито, атакуючий думає, чи підкидати».
      if (!iAttack) return (v.table || []).length ? 'Відбився. Чекай, чи підкине' : 'Чекай, суперник заходить';
      return (v.table || []).length ? 'Підкидай або «Біто»' : 'Заходь картою';
    },
  });
})();
