/*
  Шахи: класика, шахи Фішера (960) і піддавки — одна дошка на три варіанти, бо правила живуть на сервері,
  а тут лише рендер і наміри. Клацнув фігуру — сервер уже прислав список її легальних ходів у view.legal,
  тож підсвітити їх можна не рахуючи нічого: жодних правил у браузері нема і бути не повинно.

  Вид із сервера (Impl/Chess.cs):
    { variant, board: 64 символи a8..h1, fen, turn, toMove, legal: [{from,to,promo,castle}],
      lastMove, check, captured: {w,b}, moves: SAN[], halfmove, fullmove, drawOffer, result }
  Хід: act('move', { from: 'e2', to: 'e4', promo?: 'q', castle?: 'K'|'Q' }).
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M8 1.2v3.2M6.4 2.6h3.2" stroke="var(--accent)" stroke-width="1.5" stroke-linecap="round" fill="none"/>'
    + '<path d="M8 4.4 4.6 7.9c0 1.9 1.3 3 1.3 3h4.2s1.3-1.1 1.3-3L8 4.4z" fill="var(--accent)"/>'
    + '<path d="M4.6 11.6h6.8l.8 2.6H3.8l.8-2.6z" fill="var(--ok)"/></svg>';

  // Суцільні гліфи беремо для обох кольорів: контурні ♔ на темному тлі просто зникають, а колір
  // і обведення роблять різницю між білими й чорними видною навіть на телефоні.
  const GLYPH = { k: '♚', q: '♛', r: '♜', b: '♝', n: '♞', p: '♟' };
  const NAMES = { q: 'ферзь', r: 'тура', b: 'слон', n: 'кінь', k: 'король' };
  const FILES = 'abcdefgh';

  const nameOf = (sq) => FILES[sq % 8] + (8 - (sq / 8 | 0));
  const dark = (sq) => ((sq % 8) + (sq / 8 | 0)) % 2 === 1;

  function state(root) {
    // resign — не прапорець, а FEN тієї позиції, у якій кнопку звели: щойно на дошці щось змінилось,
    // перепитування знімається саме собою. Інакше один випадковий клік лишав би кнопку зведеною до кінця
    // партії, і через десять ходів наступний дотик віддав би її без жодного питання.
    if (!root._chess) root._chess = { sel: null, promo: null, resign: null };
    return root._chess;
  }

  /// Куди поставити елемент, якого ще нема. Порядок задає перший малюнок: смуга — дошка — решта.
  function ensure(root, cls, tag) {
    let el = root.querySelector(':scope > ' + cls.trim().split(/\s+/).map((c) => '.' + c).join(''));
    if (!el) {
      el = document.createElement(tag || 'div');
      el.className = cls;
      root.appendChild(el);
    }
    return el;
  }

  const setHtml = (el, html) => { if (el.innerHTML !== html) el.innerHTML = html; };

  /// Поле короля того, хто зараз ходить — щоб підсвітити шах червоним.
  function kingSquare(board, toMove) {
    const want = toMove === 'b' ? 'k' : 'K';
    return board.indexOf(want);
  }

  function pieces(str, cls) {
    if (!str) return '<span class="muted small">—</span>';
    return [...str].map((ch) => '<i class="' + cls + '">' + GLYPH[ch.toLowerCase()] + '</i>').join('');
  }

  function movesHtml(sans) {
    let html = '';
    for (let i = 0; i < sans.length; i += 2) {
      html += '<span><b>' + (i / 2 + 1) + '.</b> ' + sans[i] + (sans[i + 1] ? ' ' + sans[i + 1] : '') + '</span>';
    }
    return html;
  }

  function paint(root, ctx) {
    const v = ctx.view || {};
    const st = state(root);
    const board = v.board || '.'.repeat(64);
    const legal = v.legal || [];
    // Дошку повертають лише чорні; глядач і білі дивляться однаково — знизу білі.
    const flip = ctx.seat === 1;
    const at = (i) => (flip ? 63 - i : i);

    // Куди можна піти з вибраної фігури. Рокіровка приходить двома записами (поле тури й поле короля),
    // тож клацнути можна по будь-якому з них.
    const targets = {};
    if (st.sel) for (const m of legal) if (m.from === st.sel && m.to !== m.from) (targets[m.to] = targets[m.to] || []).push(m);

    const last = v.lastMove || null;
    const checkSq = v.check ? kingSquare(board, v.toMove) : -1;

    const cap = v.captured || { w: '', b: '' };
    // Смугу «що я взяв» малюємо з мого боку дошки, а чужу — навпроти.
    setHtml(ensure(root, 'chesscap top'), pieces(flip ? cap.w : cap.b, flip ? 'bp' : 'wp'));

    HGames.ui.grid(root, {
      cols: 8,
      rows: 8,
      cls: 'chessb',
      cell: (i) => {
        const sq = at(i);
        const ch = board[sq] || '.';
        const nm = nameOf(sq);
        const cls = [dark(sq) ? 'dk' : 'lt'];
        if (ch !== '.') cls.push(ch === ch.toUpperCase() ? 'wp' : 'bp');
        if (nm === st.sel) cls.push('sel');
        if (last && (nm === last.from || nm === last.to)) cls.push('lm');
        if (sq === checkSq) cls.push('chk');
        if (targets[nm]) cls.push(ch === '.' ? 'dot' : 'cap');
        return {
          html: ch === '.' ? '' : GLYPH[ch.toLowerCase()],
          cls: cls.join(' '),
          disabled: !ctx.myTurn,
        };
      },
      onCell: (i) => tap(root, ctx, nameOf(at(i))),
    });

    setHtml(ensure(root, 'chesscap bottom'), pieces(flip ? cap.b : cap.w, flip ? 'wp' : 'bp'));
    promoBar(root, ctx);
    // Список ходів вищий за своє віконце вже з десятого ходу, тож після кожного нового ходу дотягуємо
    // прокрутку донизу: цікавий рівно останній рядок, а не початок партії.
    const mv = ensure(root, 'chessmoves');
    const before = mv.innerHTML;
    setHtml(mv, movesHtml(v.moves || []));
    if (mv.innerHTML !== before) mv.scrollTop = mv.scrollHeight;
    buttons(root, ctx);
  }

  /// Клац по клітинці: або хід (якщо фігуру вже вибрано і туди можна), або новий вибір.
  function tap(root, ctx, nm) {
    const st = state(root);
    if (st.promo) return;                       // спершу скажи, у кого перетворити
    const legal = (ctx.view && ctx.view.legal) || [];
    const here = st.sel ? legal.filter((m) => m.from === st.sel && m.to === nm && m.to !== m.from) : [];

    if (here.length) {
      const promos = here.filter((m) => m.promo);
      if (promos.length > 1) {
        st.promo = { from: st.sel, to: nm, opts: promos };
        st.sel = null;
      } else {
        const payload = { from: st.sel, to: nm };
        // Рокіровку називаємо словом: у 960 пара полів сама по собі буває неоднозначною, а з castle
        // серверу нема чого вгадувати. Ставимо тільки тоді, коли на це поле інших сенсів нема.
        if (here.every((m) => m.castle)) payload.castle = here[0].castle;
        if (promos.length === 1) payload.promo = promos[0].promo;
        st.sel = null;
        ctx.act('move', payload);
      }
    } else if (legal.some((m) => m.from === nm)) {
      st.sel = nm;
    } else {
      st.sel = null;
    }
    paint(root, ctx);
  }

  function promoBar(root, ctx) {
    const st = state(root);
    const el = ensure(root, 'chesspromo');
    if (!st.promo) { setHtml(el, ''); el.hidden = true; return; }
    el.hidden = false;
    setHtml(el, '<span class="muted small">У кого перетворити?</span>'
      + st.promo.opts.map((m) => '<button type="button" data-p="' + m.promo + '" title="' + NAMES[m.promo] + '">'
        + GLYPH[m.promo] + '</button>').join(''));
    el.querySelectorAll('button').forEach((b) => b.onclick = () => {
      const want = st.promo;
      st.promo = null;
      paint(root, ctx);
      ctx.act('move', { from: want.from, to: want.to, promo: b.dataset.p });
    });
  }

  function buttons(root, ctx) {
    const v = ctx.view || {};
    const st = state(root);
    const el = ensure(root, 'chessacts');
    if (!ctx.mine || !ctx.playing) { setHtml(el, ''); st.resign = null; return; }
    const fen = v.fen || '';
    const armed = st.resign !== null && st.resign === fen;   // звели в цій самій позиції — питання ще живе

    const offer = v.drawOffer;
    let html;
    if (offer != null && offer !== ctx.seat) {
      st.resign = null;                       // кнопки зникли — зведене питання разом із ними
      html = '<span class="muted small">Пропонують нічию</span>'
        + '<button type="button" class="primary" data-act="draw">Згода</button>'
        + '<button type="button" class="ghost" data-act="decline">Ні</button>';
    } else {
      html = '<button type="button" class="ghost" data-act="draw"' + (offer === ctx.seat ? ' disabled' : '') + '>'
        + (offer === ctx.seat ? 'Нічию запропоновано' : 'Нічия?') + '</button>'
        // Здатись з одного кліку — надто легко втратити партію мізинцем: питаємо ще раз.
        + '<button type="button" class="ghost danger" data-act="' + (armed ? 'resign' : 'ask') + '">'
        + (armed ? 'Точно здатись?' : 'Здатись') + '</button>';
    }
    setHtml(el, html);
    el.querySelectorAll('button').forEach((b) => b.onclick = () => {
      if (b.dataset.act === 'ask') { st.resign = fen; paint(root, ctx); return; }
      st.resign = null;
      ctx.act(b.dataset.act);
    });
  }

  HGames.register({
    id: 'chess',
    icon: ICON,
    seatNames: ['білі', 'чорні'],
    seatClass: ['x', 'd'],

    mount(root, ctx) { state(root); paint(root, ctx); },

    update(root, ctx) {
      const st = state(root);
      // Партія скінчилась або пішов чужий хід — недовибраний намір тримати нема сенсу.
      if (!ctx.myTurn) { st.sel = null; st.promo = null; }
      paint(root, ctx);
    },

    status(ctx) {
      const v = ctx.view || {};
      if (!ctx.playing || !v.check) return '';   // порожньо — каркас напише «Твій хід» сам
      const base = ctx.myTurn ? 'Твій хід' : 'Ходить ' + (ctx.nickOf(v.turn) || ctx.seatName(v.turn));
      return base + ' — шах!';
    },

    unmount(root) { root._chess = null; },
  });
})();
