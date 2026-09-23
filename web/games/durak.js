/*
  Дурень підкидний на 2–6. Правила живуть на сервері (Impl/Durak.cs) — тут лише рендер і наміри.

  Вид (Hidden, свій на кожне місце; масиви — на всі шість місць, індекс = номер місця):
    { turn, attacker, defender, phase: 'attack'|'defend'|'taking'|'done', trump: '♥', trumpCard: '7♥'|null,
      deck, table: [{ attack: '7♥', defend: '9♥'|null }], hand: string[]|null,
      counts: number[], dealt: bool[], in: bool[], passed: bool[], places: number[], names: (string|null)[],
      discard, room, canAdd,
      result: null | { winner, fool: number|null, reason: 'out'|'both'|'left', foolNick: string|null, places } }
    turn — «чий хід» саме для мене: якщо мені є що підкинути, це я, навіть коли заходив інший.
    foolNick є лише тоді, коли дурень встав з-за столу: його місце вже порожнє, і ctx.nickOf імені не дасть.

  Наміри: act('attack', { card }), act('defend', { attack, card }), act('take'), act('done').
  'done' — це «Біто» головного атакуючого, «Пас» решти, хто підкидає, і «Досить» після «Беру».

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
  const SEATS = ['перший', 'другий', 'третій', 'четвертий', "п'ятий", 'шостий'];
  // Позначка місця — і колір, і форма: так гравців розрізнить і той, хто кольорів не бачить.
  const MARKS = ['●', '▲', '■', '◆', '★', '✚'];

  const suitOf = (c) => String(c || '').slice(-1);
  const rankTextOf = (c) => String(c || '').slice(0, -1);
  const rankOf = (c) => RANKS.indexOf(rankTextOf(c));
  const isRed = (c) => RED.indexOf(suitOf(c)) >= 0;
  const reduced = () => window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

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
    if (!root._durak) root._durak = { sel: null, atk: null, seen: {} };
    return root._durak;
  }

  /// Скелет ставимо раз: далі кожна частина перемальовується окремо, щоб клік не гасив :hover сусідів.
  function skeleton(root) {
    let el = root.querySelector(':scope > .durak');
    if (el) return el;
    el = document.createElement('div');
    el.className = 'durak';
    el.innerHTML = '<div class="dfoes"></div><div class="dtop"></div><div class="dtable"></div>'
      + '<div class="dsay"></div><div class="dhand"></div><div class="dbtns"></div>';
    root.appendChild(el);
    return el;
  }

  function setHtml(el, html) {
    if (el.dataset.sig !== html) { el.dataset.sig = html; el.innerHTML = html; }
  }

  const nameOf = (ctx, i) => ctx.nickOf(i) || ((ctx.view || {}).names || [])[i] || SEATS[i] || ('гравець ' + (i + 1));

  /// Хто за столом: кожен суперник — фішка з ніком, сорочками карт і роллю в цьому відбої.
  function foes(host, ctx, v) {
    const dealt = v.dealt || [];
    const inn = v.in || [];
    const passed = v.passed || [];
    const places = v.places || [];
    const counts = v.counts || [];
    const n = dealt.length || 2;
    const players = dealt.filter(Boolean).length;
    const me = ctx.mine ? ctx.seat : -1;
    const out = [];
    // По колу від мене: так «наступний» справді сидить праворуч, як за живим столом.
    for (let k = 1; k <= n; k++) {
      const i = (me + k + n) % n;
      if (!dealt[i] || i === me) continue;
      const cnt = counts[i] || 0;
      const place = places.indexOf(i);
      let role = '';
      let cls = 'dfoe s' + i;
      if (v.result && v.result.fool === i) { role = '<em class="fool">🃏 дурень</em>'; cls += ' isfool'; }
      else if (place >= 0) { role = '<em class="safe">✓ вийшов' + (players > 2 ? ' ' + (place + 1) + '-м' : '') + '</em>'; cls += ' gone'; }
      else if (!inn[i]) { role = '<em class="left">встав</em>'; cls += ' gone'; }
      else if (!v.result && i === v.defender) { role = '<em class="def">🛡 відбивається</em>'; cls += ' isdef'; }
      else if (!v.result && i === v.attacker) role = '<em class="atk">⚔ заходить</em>';
      if (!v.result && inn[i] && passed[i]) role += '<em class="pass">пас</em>';
      const fan = '<span class="dbacks">' + '<i></i>'.repeat(Math.min(cnt, 8)) + '</span>';
      out.push('<div class="' + cls + (!v.result && v.turn === i ? ' now' : '') + '">'
        + '<span class="dwho"><u>' + MARKS[i] + '</u>' + ctx.esc(nameOf(ctx, i)) + '</span>'
        + '<span class="dcnt">' + fan + '<b>' + cnt + '</b></span>'
        + '<span class="drole">' + role + '</span></div>');
    }
    setHtml(host, out.join(''));
  }

  function top(host, ctx, v) {
    const bits = [];
    // Козирну карту показуємо, поки вона лежить під колодою; забрали — лишається сама масть.
    const trumpChip = v.trumpCard
      ? cardHtml(ctx, v.trumpCard, 'sm')
      : '<span class="dsuit' + (isRed(v.trump) ? ' red' : '') + '">' + ctx.esc(v.trump || '') + '</span>';
    bits.push('<span class="dchip">козир ' + trumpChip + '</span>');
    bits.push('<span class="dchip">колода <b>' + (v.deck || 0) + '</b></span>');
    bits.push('<span class="dchip">відбій <b>' + (v.discard || 0) + '</b></span>');
    if (ctx.mine && ctx.seat != null && (v.dealt || [])[ctx.seat]) {
      bits.push('<span class="dchip me s' + ctx.seat + '"><u>' + MARKS[ctx.seat] + '</u>ти</span>');
    }
    if (!v.result && (v.table || []).length && v.phase !== 'done') {
      bits.push('<span class="dchip" title="Не більше шести карт і не більше, ніж було в того, хто відбивається">ще влізе <b>' + (v.room || 0) + '</b></span>');
    }
    setHtml(host, bits.join(''));
  }

  function board(host, ctx, v, st, hot) {
    const table = v.table || [];
    if (v.result) {
      setHtml(host, summary(ctx, v));
      st.seen = {};
      return;
    }
    if (!table.length) {
      const who = v.attacker === ctx.seat ? 'заходь будь-якою картою' : nameOf(ctx, v.attacker) + ' заходить на ' + nameOf(ctx, v.defender);
      setHtml(host, '<span class="dempty">стіл порожній — ' + ctx.esc(who) + '</span>');
      st.seen = {};
      return;
    }
    // Нові карти злітають на стіл — але лише ті, яких тут ще не було, інакше стіл блимав би щоходу.
    const seen = {};
    const fresh = (c) => { seen[c] = 1; return st.seen[c] || reduced() ? '' : ' fly'; };
    const html = table.map((p) => {
      const pick = hot.indexOf(p.attack) >= 0;
      const cls = 'dpair' + (p.defend ? ' beaten' : '') + (pick ? ' hot' : '') + (p.attack === st.atk ? ' pick' : '');
      return '<div class="' + cls + '" data-c="' + ctx.esc(p.attack) + '">'
        + cardHtml(ctx, p.attack, 'atk' + fresh(p.attack))
        + (p.defend ? cardHtml(ctx, p.defend, 'def' + fresh(p.defend)) : '')
        + '</div>';
    }).join('');
    st.seen = seen;
    setHtml(host, html);
  }

  /// Підсумок партії прямо на столі: хто вийшов першим, хто лишився дурнем.
  function summary(ctx, v) {
    const r = v.result;
    const medals = ['🥇', '🥈', '🥉'];
    const list = (r.places || v.places || []);
    const places = list.length > 1 || (v.dealt || []).filter(Boolean).length > 2
      ? list.map((i, k) => '<span class="dplace">' + (medals[k] || (k + 1) + '.') + ' ' + ctx.esc(nameOf(ctx, i)) + '</span>').join('')
      : '';
    let fool = '';
    if (r.reason === 'both') fool = '<div class="dverdict">Вийшли разом — дурня цього разу нема 🤝</div>';
    else if (r.fool != null) {
      const who = r.foolNick || nameOf(ctx, r.fool);
      fool = '<div class="dverdict">' + (ctx.seat === r.fool ? 'Дурень цього разу — ти 🃏' : '🃏 Дурень — ' + ctx.esc(who))
        + (r.reason === 'left' ? ' <small>(встав з-за столу)</small>' : '') + '</div>';
    }
    return '<div class="dsum">' + fool + (places ? '<div class="dplaces">' + places + '</div>' : '') + '</div>';
  }

  /// Підказка простими словами — що мені робити просто зараз.
  function say(host, ctx, v, iAttack, iDefend, canAdd) {
    let text = '';
    const table = v.table || [];
    const playing = !v.result && ctx.mine && ctx.playing;
    if (playing && (v.in || [])[ctx.seat] === false) {
      if ((v.places || []).indexOf(ctx.seat) >= 0) text = 'Ти вже вийшов — дивись, кому дістанеться дурень';
    } else if (playing) {
      if (v.phase === 'defend' && iDefend) text = 'Тицьни свою карту, потім ту, яку б\'єш. Нема чим — «Беру»';
      else if (v.phase === 'attack' && !table.length && iAttack) text = 'Заходь: тицьни будь-яку карту';
      else if (canAdd && v.phase === 'taking') text = 'Бере! Можна докинути карту того ж номіналу, що на столі';
      else if (canAdd) text = 'Можна підкинути карту того ж номіналу, що на столі, — або «' + (iAttack ? 'Біто' : 'Пас') + '»';
    }
    setHtml(host, text ? ctx.esc(text) : '');
  }

  function buttons(host, ctx, v, iDefend, canAdd, live) {
    const out = [];
    const iAttack = ctx.mine && ctx.seat === v.attacker;
    const table = v.table || [];
    if (live && ctx.mine && !iDefend && table.length && canAdd) {
      if (v.phase === 'taking') out.push('<button class="primary" data-act="done">Досить</button>');
      else if (v.phase === 'attack') out.push('<button class="primary" data-act="done">' + (iAttack ? 'Біто' : 'Пас') + '</button>');
    }
    if (live && iDefend && v.phase === 'defend') out.push('<button class="ghost" data-act="take">Беру</button>');
    setHtml(host, out.join(''));
  }

  function paint(root, ctx) {
    const v = ctx.view || {};
    const st = state(root);
    const el = skeleton(root);
    const hand = v.hand || [];
    const table = v.table || [];
    const counts = v.counts || [];
    const trump = v.trump || '';
    // Стіл у лобі: або ще не грали, або дограний стіл каркас віддав новому гравцеві — тоді вид
    // тримає стару партію, але показувати її новачкові нема чого.
    const lobby = ctx.room && ctx.room.status === 'lobby';
    const live = ctx.playing && !v.result;
    const iAttack = ctx.mine && ctx.seat === v.attacker;
    const iDefend = ctx.mine && ctx.seat === v.defender && (v.in || [])[ctx.seat] !== false;
    const defending = live && iDefend && v.phase === 'defend';
    const canAdd = live && !!v.canAdd;

    if (lobby || (!v.result && !v.deck && !counts.some((c) => c > 0) && !table.length)) {
      const seated = ((ctx.room && ctx.room.seats) || []).filter((s) => s.nick).length;
      setHtml(el.querySelector('.dfoes'), '');
      setHtml(el.querySelector('.dtop'), '');
      setHtml(el.querySelector('.dtable'), '<span class="dempty">За столом ' + seated + ' з 6. Грати можна вдвох і більше — господар тисне «Почати»</span>');
      setHtml(el.querySelector('.dsay'), '');
      setHtml(el.querySelector('.dbtns'), '');
      HGames.ui.hand(el.querySelector('.dhand'), [], {});
      return;
    }

    // Вибір міг протухнути: карту вже зіграно, атаку вже побито.
    if (st.sel && hand.indexOf(st.sel) < 0) st.sel = null;
    const open = table.filter((p) => !p.defend).map((p) => p.attack);
    if (st.atk && open.indexOf(st.atk) < 0) st.atk = null;
    if (!defending) { st.sel = null; st.atk = null; }

    // Що зараз можна класти: хто підкидає — свій номінал, захисникові — те, чим б'ється хоч одна атака.
    const ranksOnTable = {};
    for (const p of table) { ranksOnTable[rankTextOf(p.attack)] = 1; if (p.defend) ranksOnTable[rankTextOf(p.defend)] = 1; }
    const usable = (card) => {
      if (canAdd && !iDefend) return !table.length || ranksOnTable[rankTextOf(card)] === 1;
      if (defending) return st.atk ? beats(card, st.atk, trump) : open.some((a) => beats(card, a, trump));
      return false;
    };
    // Підсвічуємо атаки, які беруться обраною картою (або всі живі, поки нічого не обрано).
    const hot = defending ? (st.sel ? open.filter((a) => beats(st.sel, a, trump)) : open) : [];

    el.classList.toggle('many', (v.dealt || []).filter(Boolean).length > 3);
    el.classList.toggle('idle', !defending && !(canAdd && !iDefend));
    foes(el.querySelector('.dfoes'), ctx, v);
    top(el.querySelector('.dtop'), ctx, v);
    board(el.querySelector('.dtable'), ctx, v, st, hot);
    say(el.querySelector('.dsay'), ctx, v, iAttack, iDefend, canAdd);
    buttons(el.querySelector('.dbtns'), ctx, v, iDefend, canAdd, live);

    const items = hand.map((c) => ({
      card: c,
      cls: [isRed(c) ? 'red' : '', c === st.sel ? 'sel' : ''].filter(Boolean).join(' '),
      disabled: !usable(c),
    }));
    HGames.ui.hand(el.querySelector('.dhand'), items, {
      render: (it) => '<b>' + ctx.esc(rankTextOf(it.card)) + '</b><i>' + ctx.esc(suitOf(it.card)) + '</i>',
      onItem: (it) => {
        if (canAdd && !iDefend) { ctx.act('attack', { card: it.card }); return; }
        if (!defending) return;
        if (st.atk && beats(it.card, st.atk, trump)) { ctx.act('defend', { attack: st.atk, card: it.card }); st.sel = null; st.atk = null; return; }
        // Бити лишилось одну карту, і ця нею б'ється — не змушуємо тицяти двічі.
        if (open.length === 1 && beats(it.card, open[0], trump)) { ctx.act('defend', { attack: open[0], card: it.card }); st.sel = null; return; }
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
        const b = e.target.closest('[data-act]');
        if (b && bt._act) bt._act(b.dataset.act);
      });
    }
  }

  HGames.register({
    id: 'durak',
    icon: ICON,
    seatNames: SEATS,
    seatClass: ['x', 'o', 'c', 'd', 'x', 'o'],
    news: {
      v: '2026-09-24',
      title: 'Дурень: тепер компанією до шести',
      items: [
        '🃏 За стіл сідає 2–6 гравців — господар тисне «Почати», коли всі зібрались',
        '🤲 Підкидають усі, крім того, хто відбивається; кому нема чого додати — тисне «Пас»',
        '🛡 Узяв — пропускаєш хід: заходить наступний за тобою',
        '🏁 Хто скинув карти, той вийшов; останній із картами — дурень, і стіл скаже це вголос',
        '🚪 Встав посеред партії компанією — решта грає далі без тебе',
      ],
    },

    mount(root, ctx) { paint(root, ctx); },
    update(root, ctx) { paint(root, ctx); },
    unmount(root) { root._durak = null; },

    status(ctx) {
      const v = ctx.view || {};
      if (!ctx.playing && !v.result) return '';
      if (v.result) {
        if (v.result.reason === 'both') return 'Вийшли разом — нічия';
        const fool = v.result.fool != null ? v.result.fool : (v.result.winner === 0 ? 1 : 0);
        // Той, хто встав, уже не сидить — його нік бережемо у виді, інакше вийшло б «Дурень — перший».
        const name = v.result.foolNick || ctx.nickOf(fool) || ctx.seatName(fool);
        return ctx.seat === fool ? 'Дурень цього разу ти' : 'Дурень — ' + name;
      }
      if (!ctx.mine) return 'Дивишся збоку';
      if ((v.in || [])[ctx.seat] === false) return 'Ти вийшов — чекай, хто лишиться дурнем';
      const table = v.table || [];
      const att = nameOf(ctx, v.attacker);
      const def = nameOf(ctx, v.defender);
      if (ctx.seat === v.defender) {
        if (v.phase === 'defend') return 'Відбивайся або бери';
        if (v.phase === 'taking') return 'Береш — чекай, чи докинуть';
        return table.length ? 'Відбився! Чекай, чи підкинуть ще' : 'На тебе заходить ' + att;
      }
      if (v.phase === 'defend') return def + ' відбивається';
      if (v.canAdd) {
        if (!table.length) return 'Заходь на ' + def;
        return v.phase === 'taking' ? def + ' бере — докидай або «Досить»' : 'Підкидай або «' + (ctx.seat === v.attacker ? 'Біто' : 'Пас') + '»';
      }
      if (!table.length) return att + ' заходить на ' + def;
      return 'Чекаємо, чи підкинуть інші';
    },
  });
})();
