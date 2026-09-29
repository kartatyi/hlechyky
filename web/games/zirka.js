/*
  Китайські шашки на зірці (Impl/Zirka.cs) — «шашки на трьох». Правила на сервері; тут лише малюнок і наміри.

  Вид: { cells: 121 символ ('.' порожньо, '0'…'2' — фішка місця), n, turn, homes: [{seat, axis, sign}],
         home: [скільки вже в цілі], moves: { "<звідки>": [куди…] } — лише для того, чия черга, last: [шлях], result }
  Хід: act('move', { from, to }) — номери лунок. Нумерація та сама, що на сервері: рядки r = −8…8, у рядку q = −8…8,
  лунка є, якщо (x, y, z) = (q, −q−r, r) лежить у зірці.
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M8 1 10 5.2h4.6L11 8l3.6 2.8H10L8 15l-2-4.2H1.4L5 8 1.4 5.2H6z" fill="none" stroke="var(--accent)" stroke-width="1.3" stroke-linejoin="round"/>'
    + '<circle cx="8" cy="8" r="1.7" fill="var(--ok)"/></svg>';

  const CELLS = [];
  for (let r = -8; r <= 8; r++) {
    for (let q = -8; q <= 8; q++) {
      const x = q, z = r, y = -q - r;
      if ((x <= 4 && y <= 4 && z <= 4) || (x >= -4 && y >= -4 && z >= -4)) CELLS.push([q, r]);
    }
  }
  const S3 = Math.sqrt(3) / 2;
  const XY = CELLS.map(([q, r]) => [q + r / 2, r * S3]);
  const COLORS = ['#e8b64a', '#4f9be0', '#e0604f'];
  const NAMES = ['жовті', 'сині', 'червоні'];
  const coord = (i, axis) => { const [q, r] = CELLS[i]; return axis === 0 ? q : axis === 1 ? -q - r : r; };

  const setHtml = (el, html) => { if (el._h !== html) { el._h = html; el.innerHTML = html; } };

  function state(root) {
    if (!root._zk) root._zk = { sel: null };
    return root._zk;
  }

  /// Кут повертаємо так, щоб моя домівка була внизу: кут центроїда домівки → 90° (униз екрана).
  function turnOf(v, seat) {
    const h = (v.homes || []).find((x) => x.seat === seat);
    if (!h) return 0;
    let sx = 0, sy = 0;
    CELLS.forEach((_, i) => { if (coord(i, h.axis) * h.sign >= 5) { sx += XY[i][0]; sy += XY[i][1]; } });
    return 90 - Math.atan2(sy, sx) * 180 / Math.PI;
  }

  function cornerOf(i, homes) {
    for (const h of homes || []) {
      if (coord(i, h.axis) * h.sign >= 5) return { seat: h.seat, home: true };
      if (coord(i, h.axis) * h.sign <= -5) return { seat: h.seat, home: false };
    }
    return null;
  }

  function paint(root, ctx) {
    const v = ctx.view || {};
    const st = state(root);
    const cells = v.cells || '';
    const moves = v.moves || {};
    if (!ctx.myTurn || (st.sel != null && !moves[st.sel])) st.sel = null;
    const targets = new Set(st.sel != null ? moves[st.sel] || [] : []);
    const canPick = ctx.myTurn ? new Set(Object.keys(moves).map(Number)) : new Set();
    const rot = ctx.seat != null ? turnOf(v, ctx.seat) : 0;

    let holes = '';
    let pegs = '';
    CELLS.forEach((_, i) => {
      const [x, y] = XY[i];
      const c = cornerOf(i, v.homes);
      const tint = c ? ' style="fill:' + COLORS[c.seat] + ';fill-opacity:' + (c.home ? '.16' : '.3') + '"' : '';
      holes += '<circle class="zk-hole' + (targets.has(i) ? ' zk-to' : '') + '" data-i="' + i + '" cx="' + x.toFixed(3) + '" cy="' + y.toFixed(3) + '" r="' + (targets.has(i) ? '.44' : '.34') + '"' + tint + '/>';
      const ch = cells[i];
      if (ch && ch !== '.') {
        const s = +ch;
        pegs += '<circle class="zk-peg' + (i === st.sel ? ' zk-sel' : '') + (canPick.has(i) ? ' zk-can' : '') + '" data-i="' + i
          + '" cx="' + x.toFixed(3) + '" cy="' + y.toFixed(3) + '" r=".4" fill="' + COLORS[s] + '"/>';
      }
    });
    let trail = '';
    if (v.last && v.last.length > 1) {
      trail = '<polyline class="zk-trail" points="' + v.last.map((i) => XY[i][0].toFixed(3) + ',' + XY[i][1].toFixed(3)).join(' ') + '"/>';
    }
    let board = root.querySelector(':scope > .zk-board');
    if (!board) {
      board = document.createElement('div');
      board.className = 'zk-board';
      root.appendChild(board);
    }
    // Невидимі «зони дотику» на всю відстань між лунками: сама лунка на телефоні — 16 px, пальцем не влучиш.
    const hits = XY.map(([x, y], i) => '<circle class="zk-hit" data-i="' + i + '" cx="' + x.toFixed(3) + '" cy="' + y.toFixed(3) + '" r=".5"/>').join('');
    // viewBox — впритул до крайніх зон дотику (зірка вістрям донизу: ±6 по x, ±6,93 по y, плюс .5): порожні поля
    // по боках з'їдали п'яту частину ширини, а на телефоні кожен піксель лунки — влучання пальцем.
    setHtml(board, '<svg viewBox="-6.55 -7.45 13.1 14.9" role="img" aria-label="Зірка"><g transform="rotate(' + rot.toFixed(1) + ')">'
      + holes + trail + pegs + hits + '</g></svg>');

    // Хто є хто: колір, нік, скільки вже вдома.
    let info = root.querySelector(':scope > .zk-info');
    if (!info) {
      info = document.createElement('div');
      info.className = 'zk-info';
      root.appendChild(info);
    }
    const res = v.result && ctx.room.status === 'finished'
      ? '<div class="zk-res">' + (v.result.reason === 'rounds' ? '⌛ Стеля кіл — перемога за тим, у кого більше вдома'
        : v.result.reason === 'left' ? 'Хтось встав з-за столу' : '🏁 Усі вдома!') + '</div>' : '';
    setHtml(info, (v.homes || []).map((h, k) => '<span class="zk-p' + (v.turn === h.seat ? ' on' : '') + '"><i style="background:' + COLORS[h.seat] + '"></i>'
      + ctx.esc(ctx.nickOf(h.seat) || NAMES[h.seat]) + (h.seat === ctx.seat ? ' (ти)' : '') + ' · вдома <b>' + ((v.home || [])[k] || 0) + '</b>/10</span>').join('') + res);
  }

  function onClick(root, ctx, e) {
    const t = e.target.closest('[data-i]');
    const st = state(root);
    if (!t || !ctx.myTurn) { st.sel = null; paint(root, ctx); return; }
    const i = +t.dataset.i;
    const moves = (ctx.view || {}).moves || {};
    if (st.sel != null && (moves[st.sel] || []).includes(i)) {
      const from = st.sel;
      st.sel = null;
      ctx.act('move', { from, to: i });
      return;
    }
    st.sel = moves[i] ? i : null;
    paint(root, ctx);
  }

  HGames.register({
    id: 'zirka',
    icon: ICON,
    added: '2026-09-29',
    seatClass: ['x', 'o', 'c'],

    mount(root, ctx) {
      state(root);
      root._zkCtx = ctx;
      root._zkClick = (e) => onClick(root, root._zkCtx, e);
      root.addEventListener('click', root._zkClick);
      paint(root, ctx);
    },

    update(root, ctx) { root._zkCtx = ctx; paint(root, ctx); },

    status(ctx) {
      const v = ctx.view || {};
      if (!ctx.playing || !ctx.myTurn) return '';
      return (v.count || 0) < 6 ? 'Твій хід: тягни фішки в протилежний кут — стрибати можна ланцюжком через будь-які' : '';
    },

    unmount(root) {
      if (root._zkClick) root.removeEventListener('click', root._zkClick);
      root._zkClick = null;
      root._zkCtx = null;
      root._zk = null;
    },
  });
})();
