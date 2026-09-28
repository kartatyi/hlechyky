/*
  Ультимативні хрестики-нолики (№214, прохід №3): дев'ять малих полів 3×3 у великому полі 3×3.
  Куди сходив у малому полі — у те поле великого йде суперник; поле, куди можна ходити, підсвічене.
  Виграв мале поле — на ньому велика мітка; три взяті поля в ряд — партія.

  Вид із сервера (Impl/UltimateTicTacToe.cs): { cells[81] ('x'|'o'|null, індекс = поле*9 + клітинка),
  boards[9] ('x'|'o'|'draw'|null), small[9] (ряд у взятому полі або null), next (-1 — будь-де), legal[],
  turn, last, line (ряд великого поля), winner, won[], series }.
  Свій модуль, а не ttt.js: у тому модулі стоїть свій news, а в цього — added (нова гра в лобі).
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M5.7 1.5v13M10.3 1.5v13M1.5 5.7h13M1.5 10.3h13" stroke="var(--muted)" stroke-width="1" fill="none"/>'
    + '<path d="M2.2 2.2 4.6 4.6M4.6 2.2 2.2 4.6" stroke="var(--accent)" stroke-width="1.4" stroke-linecap="round"/>'
    + '<circle cx="8" cy="8" r="1.3" stroke="var(--ok)" stroke-width="1.3" fill="none"/>'
    + '<path d="M11.4 11.4 13.8 13.8M13.8 11.4 11.4 13.8" stroke="var(--accent)" stroke-width="1.4" stroke-linecap="round"/></svg>';
  const MARK = { x: '✕', o: '◯' };
  const POP_MS = 400;

  const setHtml = (el, html) => {
    if (HGames.ui && HGames.ui.html) HGames.ui.html(el, html);
    else if (el._h !== html) { el._h = html; el.innerHTML = html; }
  };

  function build(root) {
    let big = root.querySelector(':scope > .t9-big');
    if (big) return big;
    big = document.createElement('div');
    big.className = 't9-big';
    let html = '';
    for (let b = 0; b < 9; b++) {
      html += '<div class="t9-sub" data-b="' + b + '">';
      for (let i = 0; i < 9; i++) html += '<button type="button" class="t9-c" data-i="' + (b * 9 + i) + '"></button>';
      html += '<span class="t9-own" aria-hidden="true"></span></div>';
    }
    big.innerHTML = html;
    big.addEventListener('click', (e) => {
      const btn = e.target.closest('.t9-c');
      const st = root._t9;
      if (!btn || btn.disabled || !st || !st.ctx) return;
      st.sure = false;
      st.ctx.act('move', { cell: +btn.dataset.i });
    });
    root.appendChild(big);
    const foot = document.createElement('div');
    foot.className = 't9-foot';
    root.appendChild(foot);
    return big;
  }

  function paint(root, ctx) {
    const st = root._t9 || (root._t9 = { lastSeen: undefined, popUntil: 0, sure: false });
    st.ctx = ctx;
    const v = ctx.view || {};
    const cells = v.cells || [];
    const boards = v.boards || [];
    const small = v.small || [];
    const line = v.line || [];
    const legal = new Set(v.legal || []);
    const last = typeof v.last === 'number' ? v.last : -1;
    if (st.lastSeen !== undefined && last >= 0 && last !== st.lastSeen) st.popUntil = performance.now() + POP_MS;
    st.lastSeen = last;
    const popping = performance.now() < st.popUntil;
    const myTurn = !!ctx.myTurn;

    const big = build(root);
    big.classList.toggle('t9-my', myTurn);
    const subs = big.children;
    for (let b = 0; b < 9; b++) {
      const sub = subs[b];
      const own = boards[b] || '';
      const open = !v.winner && typeof v.turn === 'number' && (v.next === b || (v.next < 0 && !own));
      const want = 't9-sub' + (own ? ' t9-' + own + ' t9-closed' : '') + (open ? ' t9-open' : '')
        + (line.includes(b) ? ' t9-winb' : '');
      if (sub.className !== want) sub.className = want;
      const ownEl = sub.lastElementChild;
      const mark = MARK[own] || '';
      if (ownEl.textContent !== mark) ownEl.textContent = mark;
      const row = small[b] || [];
      for (let i = 0; i < 9; i++) {
        const idx = b * 9 + i;
        const btn = sub.children[i];
        const c = cells[idx];
        const cls = 't9-c' + (c ? ' t9-' + c : '') + (row.includes(i) ? ' t9-row' : '')
          + (idx === last ? ' t9-last' : '') + (idx === last && popping ? ' t9-pop' : '')
          + (myTurn && legal.has(idx) ? ' t9-ok' : '');
        if (btn.className !== cls) btn.className = cls;
        const txt = MARK[c] || '';
        if (btn.textContent !== txt) btn.textContent = txt;
        const dis = !(myTurn && legal.has(idx));
        if (btn.disabled !== dis) btn.disabled = dis;
      }
    }
    footer(root, ctx, st);
  }

  function footer(root, ctx, st) {
    const v = ctx.view || {};
    const el = root.querySelector(':scope > .t9-foot');
    let html = '';
    if (!v.winner && typeof v.turn === 'number') {
      const where = v.next >= 0 ? 'у підсвічене поле' : 'у будь-яке відкрите поле';
      html += '<span class="t9-hint">' + (ctx.myTurn
        ? '<b>Твій хід</b> — ' + where + '. Куди поставиш у малому полі, туди на великому піде суперник'
        : (ctx.mine ? 'Хід суперника — ' : 'Ходять ') + where) + '</span>';
    }
    const s = v.series;
    if (s && s.wins) {
      const parts = [];
      for (let i = 0; i < 2; i++) {
        const nick = ctx.nickOf(i);
        if (nick) parts.push('<span class="t9-' + (i ? 'o' : 'x') + '">' + ctx.esc(nick) + ' <b>' + (s.wins[i] || 0) + '</b></span>');
      }
      html += '<span class="t9-serie" title="Скільки партій виграв кожен за цим столом">Серія: ' + parts.join(' : ')
        + (s.draws ? ' · нічиїх <b>' + s.draws + '</b>' : '') + '</span>';
    }
    // Партія на 40–60 ходів — здатись можна, щоб не тягти безнадійне.
    if (ctx.mine && ctx.playing && !v.winner) {
      html += '<button type="button" class="ghost' + (st.sure ? ' t9-danger' : '') + '" data-t="resign">'
        + (st.sure ? 'Точно здатись?' : 'Здатись') + '</button>';
    }
    setHtml(el, html);
    const b = el.querySelector('[data-t]');
    if (b) b.onclick = () => {
      if (!st.sure) { st.sure = true; paint(root, ctx); return; }
      st.sure = false;
      ctx.act('resign');
    };
  }

  HGames.register({
    id: 'ttt9',
    added: '2026-09-29',
    icon: ICON,
    seatNames: ['✕', '◯'],
    seatClass: ['x', 'o'],
    mount(root, ctx) { paint(root, ctx); },
    update(root, ctx) {
      paint(root, ctx);
      const st = root._t9;
      clearTimeout(st.t);
      if (performance.now() < st.popUntil) st.t = setTimeout(() => root._t9 && paint(root, st.ctx), POP_MS + 30);
    },
    unmount(root) { const st = root._t9; if (st) clearTimeout(st.t); root._t9 = null; },
  });
})();
