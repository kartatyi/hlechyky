/*
  Хрестики-нолики: класика ('ttt') і зникаючі ('ttt3') — два модулі в одному файлі.
  Правила однакові, різниця лише в тому, що в зникаючих одна мітка на полі підморгує:
  саме її змете наступний хід свого ж господаря. Каркас дозволяє кілька register в одному
  файлі, тому окремого ttt3.js не існує — завантажувач його пропустить, побачивши has('ttt3').

  Вид із сервера (Impl/GridGame.cs): { cells: ('x'|'o'|null)[], width, turn, line, fading,
  last?, series? } — last і series з'явились 24.09 і необов'язкові.
  24.09: щойно поставлена мітка «вистрибує», остання підсвічена, виграшний ряд перекреслено лінією,
  під полем — рахунок серії «Ще раз»; у зникаючих ще й «Здатись» (нічиїх там нема, партія може тягтись).
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M2.3 2.3 6.9 6.9 M6.9 2.3 2.3 6.9" stroke="var(--accent)" stroke-width="1.8" stroke-linecap="round" fill="none"/>'
    + '<circle cx="11.1" cy="11.1" r="3" stroke="var(--ok)" stroke-width="1.8" fill="none"/></svg>';
  const MARK = ['✕', '◯'];
  const POP_MS = 400;

  /// Мітка місця: сервер каже 'x'/'o', але поле може прийти й числом місця.
  const markOf = (c) => (c === 'x' || c === 0 ? MARK[0] : c === 'o' || c === 1 ? MARK[1] : '');
  const clsOf = (c) => (c === 'x' || c === 0 ? 'x' : c === 'o' || c === 1 ? 'o' : '');

  function state(root) {
    if (!root._ttt) root._ttt = { lastSeen: undefined, popUntil: 0, sure: false, sig: '' };
    return root._ttt;
  }

  function ensure(root, cls) {
    let el = root.querySelector(':scope > .' + cls);
    if (!el) { el = document.createElement('div'); el.className = cls; root.appendChild(el); }
    return el;
  }
  const setHtml = (el, html) => { if (el.innerHTML !== html) el.innerHTML = html; };

  function paint(root, ctx, fading) {
    const v = ctx.view || {};
    const st = state(root);
    const cells = v.cells || [];
    const w = v.width || Math.round(Math.sqrt(cells.length)) || 3;
    const line = v.line || [];
    const last = typeof v.last === 'number' ? v.last : -1;
    if (st.lastSeen !== undefined && last >= 0 && last !== st.lastSeen) st.popUntil = performance.now() + POP_MS;
    st.lastSeen = last;
    const popping = performance.now() < st.popUntil;

    const sig = cells.join(',') + '|' + v.turn;
    if (st.sig !== sig) { st.sig = sig; st.sure = false; }

    const board = HGames.ui.grid(root, {
      cols: w,
      // поки партія не почалась, cells порожній: малюємо квадрат w×w, а не смужку в один ряд
      rows: cells.length ? Math.ceil(cells.length / w) : w,
      cls: 'tttb',
      cell: (i) => ({
        html: markOf(cells[i]),
        cls: [clsOf(cells[i]), line.includes(i) ? 'win' : '', i === v.fading ? 'fading' : '',
          i === last && !line.length ? 'last' : '', i === last && popping ? 'pop' : ''].filter(Boolean).join(' '),
        disabled: !(ctx.myTurn && !cells[i]),
      }),
      onCell: (i) => { st.sure = false; ctx.act('move', { cell: i }); },
    });
    strike(board, line, w);
    footer(root, ctx, st, fading);
  }

  /// Лінія через виграшний ряд — поверх клітинок, від центру першої до центру останньої.
  function strike(board, line, w) {
    let el = board.querySelector(':scope > .tttline');
    if (line.length < 2) { if (el) el.remove(); return; }
    const a = line[0], b = line[line.length - 1];
    const rows = board.querySelectorAll(':scope > .cell').length / w;
    const x1 = ((a % w) + .5) / w * 100, y1 = (Math.floor(a / w) + .5) / rows * 100;
    const x2 = ((b % w) + .5) / w * 100, y2 = (Math.floor(b / w) + .5) / rows * 100;
    const key = [x1, y1, x2, y2].join(',');
    if (el && el.dataset.k === key) return;
    if (el) el.remove();
    el = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    el.setAttribute('class', 'tttline');
    el.setAttribute('viewBox', '0 0 100 100');
    el.setAttribute('preserveAspectRatio', 'none');
    el.dataset.k = key;
    el.innerHTML = '<line x1="' + x1 + '" y1="' + y1 + '" x2="' + x2 + '" y2="' + y2 + '" pathLength="1"/>';
    board.appendChild(el);
  }

  function footer(root, ctx, st, fading) {
    const v = ctx.view || {};
    const el = ensure(root, 'tttfoot');
    let html = '';
    const s = v.series;
    if (s && s.wins) {
      const parts = [];
      for (let i = 0; i < 2; i++) {
        const nick = ctx.nickOf(i);
        if (nick) parts.push('<span class="' + (i ? 'o' : 'x') + '">' + ctx.esc(nick) + ' <b>' + (s.wins[i] || 0) + '</b></span>');
      }
      html += '<span class="gserie" title="Скільки партій виграв кожен за цим столом">Серія: ' + parts.join(' : ')
        + (s.draws ? ' · нічиїх <b>' + s.draws + '</b>' : '') + '</span>';
    }
    // Здатись — лише в зникаючих: у класиці партія на дев'ять ходів, а там нічиєї не буває й гра може тягтись.
    if (fading && ctx.mine && ctx.playing) {
      html += '<button type="button" class="ghost' + (st.sure ? ' danger' : '') + '" data-t="resign">'
        + (st.sure ? 'Точно здатись?' : 'Здатись') + '</button>';
    }
    setHtml(el, html);
    const b = el.querySelector('[data-t]');
    if (b) b.onclick = () => {
      if (!st.sure) { st.sure = true; paint(root, ctx, fading); return; }
      st.sure = false;
      ctx.act('resign');
    };
  }

  const mod = (id, fading) => ({
    id,
    icon: ICON,
    seatNames: ['✕', '◯'],
    seatClass: ['x', 'o'],
    mount(root, ctx) { paint(root, ctx, fading); },
    update(root, ctx) {
      paint(root, ctx, fading);
      const st = state(root);
      clearTimeout(st.t);
      if (performance.now() < st.popUntil) st.t = setTimeout(() => root._ttt && paint(root, ctx, fading), POP_MS + 30);
    },
    unmount(root) { const st = root._ttt; if (st) clearTimeout(st.t); root._ttt = null; },
  });

  const NEWS_ITEMS = [
    '✨ Щойно поставлена мітка «вистрибує», а остання підсвічена — видно, куди сходив суперник',
    '✏️ Виграшний ряд тепер перекреслено лінією',
    '🏆 Рахунок серії під полем, якщо граєте «Ще раз» тим самим складом',
  ];
  HGames.register(Object.assign(mod('ttt', false), {
    news: { v: '2026-09-24', title: 'Хрестики-нолики: живіше поле і рахунок серії', items: NEWS_ITEMS },
  }));
  HGames.register(Object.assign(mod('ttt3', true), {
    news: {
      v: '2026-09-24',
      title: 'Зникаючі хрестики: живіше поле, серія і «Здатись»',
      items: NEWS_ITEMS.concat(['🏳️ З\'явилась кнопка «Здатись» — нічиїх тут не буває, і партія, буває, ходить по колу']),
    },
  }));
})();
