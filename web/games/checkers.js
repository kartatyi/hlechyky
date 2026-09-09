/*
  Шашки (російські). Дошка 8×8, правила — тільки на сервері: тут ми лише малюємо позицію
  й збираємо намір гравця. Клік по своїй шашці підсвічує перші кроки, клік далі добудовує
  ланцюг; щойно він збігся з повним легальним ходом — летить act('move', { path }).

  Вид із сервера (Impl/Checkers.cs):
  { board: 64 символи a8..h1 ('w'|'W'|'b'|'B'|'.'|' '), turn, toMove, legal: string[][],
    mustCapture, lastPath, count: {w,b}, drawOffer, result }
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<circle cx="6" cy="10" r="4.4" fill="var(--clay)"/>'
    + '<circle cx="10.4" cy="5.6" r="4.4" fill="var(--accent)" stroke="var(--bg)" stroke-width="1"/></svg>';

  /// Поле 0..63 → «c3»: 0 — a8, 63 — h1, як у рядку board.
  const nameOf = (i) => String.fromCharCode(97 + (i % 8)) + (8 - Math.floor(i / 8));
  /// Темні поля — ті, де сума ряду й колонки непарна (a1 темне).
  const dark = (i) => ((Math.floor(i / 8) + (i % 8)) % 2) === 1;

  function pieceHtml(ch) {
    if (ch !== 'w' && ch !== 'W' && ch !== 'b' && ch !== 'B') return '';
    return '<i class="ckp ' + (ch === 'w' || ch === 'W' ? 'w' : 'b') + (ch === 'W' || ch === 'B' ? ' k' : '') + '"></i>';
  }

  function state(root) {
    // sure — «Здатись» натиснули раз і чекаємо на підтвердження: здача незворотна й коштує партії.
    if (!root._ck) root._ck = { path: [], sig: '', sure: false };
    return root._ck;
  }

  /// Смужка над дошкою і кнопки під нею: створюємо один раз, далі лише переписуємо вміст.
  function strip(root, cls, top) {
    let el = root.querySelector(':scope > .' + cls);
    if (!el) {
      el = document.createElement('div');
      el.className = cls;
      if (top) root.insertBefore(el, root.firstChild); else root.appendChild(el);
    }
    return el;
  }

  function setHtml(el, html) { if (el.innerHTML !== html) el.innerHTML = html; }

  function paint(root, ctx) {
    const v = ctx.view || {};
    const board = v.board || '';
    const legal = v.legal || [];
    const last = v.lastPath || [];
    const st = state(root);
    ctx._ckRoot = root;                      // щоб Escape із onKey знав, де лежить вибір

    // Позиція змінилась (хтось походив) — недобудований ланцюг більше ні до чого.
    const sig = board + '|' + v.turn + '|' + last.join('');
    if (st.sig !== sig) { st.sig = sig; st.path = []; st.sure = false; }

    // Ланцюги, що починаються з уже набраного шляху; з них і беремо, куди можна далі.
    const fit = legal.filter((c) => st.path.every((s, i) => c[i] === s));
    const next = new Set();
    if (ctx.myTurn) for (const c of fit) if (c.length > st.path.length) next.add(c[st.path.length]);

    const count = v.count || {};
    setHtml(strip(root, 'ckbar', true),
      '<span class="gscore"><i class="ckp w"></i><b>' + (count.w != null ? count.w : 12) + '</b>'
      + ' : <b>' + (count.b != null ? count.b : 12) + '</b><i class="ckp b"></i></span>'
      + (v.mustCapture && !v.result ? '<span class="ckmust">Бити обов\'язково</span>' : ''));

    // Чорним показуємо дошку з їхнього боку: поворот на 180° — це дзеркало обох осей, тобто 63 - i.
    const flip = ctx.seat === 1;
    HGames.ui.grid(root, {
      cols: 8,
      rows: 8,
      cls: 'ck',
      cell: (i) => {
        const idx = flip ? 63 - i : i;
        const name = nameOf(idx);
        const cls = [dark(idx) ? 'dark' : 'light'];
        if (st.path.indexOf(name) >= 0) cls.push('sel');
        if (next.has(name)) cls.push('pick');
        if (last.indexOf(name) >= 0) cls.push('last');
        return { html: pieceHtml(board[idx]), cls: cls.join(' '), disabled: !ctx.myTurn || !dark(idx) };
      },
      onCell: (i) => tap(root, ctx, nameOf(flip ? 63 - i : i)),
    });

    const acts = strip(root, 'ckacts', false);
    const offer = v.drawOffer;
    const mine = ctx.mine && ctx.playing;
    let html = '';
    if (mine && st.path.length) html += '<button class="ghost" data-ck="reset">Скинути вибір</button>';
    if (mine && offer != null && offer !== ctx.seat) {
      html += '<span class="muted small">Пропонують нічию</span>'
        + '<button class="primary" data-ck="draw">Згода</button><button class="ghost" data-ck="decline">Ні</button>';
    } else if (mine) {
      html += '<button class="ghost" data-ck="draw"' + (offer === ctx.seat ? ' disabled' : '') + '>Нічия?</button>'
        + '<button class="' + (st.sure ? 'primary' : 'ghost') + '" data-ck="resign">'
        + (st.sure ? 'Точно здатись?' : 'Здатись') + '</button>';
    }
    setHtml(acts, html);
    acts.querySelectorAll('[data-ck]').forEach((b) => b.onclick = () => {
      const what = b.dataset.ck;
      if (what === 'reset') { st.path = []; st.sure = false; paint(root, ctx); return; }
      // Здача — єдина незворотна дія модуля, тож у два дотики. Без таймерів: прапорець знімає
      // будь-який хід, клік по дошці, «Скинути вибір» або Escape.
      if (what === 'resign' && !st.sure) { st.sure = true; paint(root, ctx); return; }
      st.sure = false;
      ctx.act(what);
    });
  }

  /// Клік по полю: добудовуємо ланцюг, а коли він збігся з повним легальним ходом — шлемо його.
  function tap(root, ctx, name) {
    if (!ctx.myTurn) return;
    const st = state(root);
    st.sure = false;                         // рука пішла на дошку — «Точно здатись?» більше не висить
    const legal = (ctx.view && ctx.view.legal) || [];
    const fit = legal.filter((c) => st.path.every((s, i) => c[i] === s));
    if (fit.some((c) => c[st.path.length] === name)) {
      st.path.push(name);
      // Повний ланцюг ніколи не є початком іншого повного, тож збіг довжини — це кінець ходу.
      const done = legal.some((c) => c.length === st.path.length && c.every((s, i) => s === st.path[i]));
      if (done) {
        const path = st.path.slice();
        st.path = [];
        paint(root, ctx);
        ctx.act('move', { path });
        return;
      }
      paint(root, ctx);
      return;
    }
    // Клік повз ланцюг: тицьнув у вибрану шашку вдруге — знімаємо вибір (так поводиться будь-яка
    // дошка, і на телефоні це єдиний спосіб передумати без кнопки); інша своя — вибір із неї.
    st.path = st.path.length === 1 && st.path[0] === name
      ? []
      : legal.some((c) => c[0] === name) ? [name] : [];
    paint(root, ctx);
  }

  HGames.register({
    id: 'checkers',
    icon: ICON,
    seatNames: ['білі', 'чорні'],
    seatClass: ['c', 'd'],
    mount(root, ctx) { paint(root, ctx); },
    update(root, ctx) { paint(root, ctx); },
    onKey(e, ctx) {
      if (e.key !== 'Escape' || !ctx._ckRoot) return false;
      const st = state(ctx._ckRoot);
      if (!st.path.length && !st.sure) return false;
      st.path = [];
      st.sure = false;
      paint(ctx._ckRoot, ctx);
      return true;
    },
    unmount(root) { root._ck = null; },
  });
})();
