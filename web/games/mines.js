/*
  Сапер: дуель ('mines') і Сапер дня ('mines-daily') — два модулі в одному файлі. Правила малювання
  однакові, різниця лише в рядку над полем (рахунок проти таймера) і в кнопці «Спробувати ще».
  Каркас дозволяє кілька register в одному файлі, тому окремого mines-daily.js не існує: у паспорті
  щоденного стоїть Client: "mines", і завантажувач іде по цей самий файл.

  Вид із сервера (Impl/Mines.cs):
    { w, h, mines, turn, cells, scores, left, lastOpen, result }
    + для дня: { day, attempts, startedAt, solved, ms, elapsedMs }
  cells — рядок на w*h символів: '#' закрито, 'F' прапорець, '0'..'8' відкрито, '*' міна (після кінця).
  Дії: act('open', { cell }), act('flag', { cell }), act('resign'), act('restart').

  Правила тут не рахуються взагалі: клієнт лише малює те, що прийшло, і шле наміри.
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<circle cx="6.8" cy="9.8" r="4.7" fill="var(--accent)"/>'
    + '<path d="M10.2 6.4 12.1 4.5" stroke="var(--clay)" stroke-width="1.7" stroke-linecap="round" fill="none"/>'
    + '<path d="M12.6 4.1 14.6 2.1" stroke="var(--danger)" stroke-width="1.7" stroke-linecap="round" fill="none"/></svg>';

  /// Довгий тап (мс), після якого клік стає прапорцем — на телефоні правої кнопки нема.
  const LONG_MS = 500;

  const closed = (ch) => ch === '#' || ch === 'F';

  function state(root) {
    if (!root._mines) root._mines = { flagMode: false, base: 0, at: 0, frozen: true, timer: 0 };
    return root._mines;
  }

  /// Як виглядає клітинка. Кольори цифр — у mines.css, тут лише клас.
  function face(ch, last, live) {
    const mark = last ? ' last' : '';
    if (ch === '*') return { html: '💣', cls: 'bomb' + mark, disabled: true };
    if (ch === 'F') return { html: '🚩', cls: 'closed flag' + mark, disabled: !live };
    if (ch === undefined || ch === '#') return { html: '', cls: 'closed' + mark, disabled: !live };
    const n = +ch || 0;
    return { html: n ? String(n) : '', cls: 'open n' + n + mark, disabled: true };
  }

  function timeText(ms) {
    const s = Math.max(0, ms) / 1000;
    if (s < 60) return s.toFixed(1) + ' с';
    return Math.floor(s / 60) + ':' + String(Math.floor(s % 60)).padStart(2, '0');
  }

  /// Рядок над полем: скільки мін лишилось незакритими прапорцями, час (день) або рахунок (дуель).
  function bar(root, ctx, daily) {
    let el = root.querySelector(':scope > .mbar');
    if (!el) {
      el = document.createElement('div');
      el.className = 'mbar';
      root.insertBefore(el, root.firstChild);
    }
    const v = ctx.view || {};
    const cells = v.cells || '';
    let flags = 0;
    for (let i = 0; i < cells.length; i++) if (cells[i] === 'F') flags++;
    const left = '<span>🚩 <b>' + ((v.mines || 0) - flags) + '</b></span>';
    const html = daily
      ? left + '<span class="mtime">⏱ <b>' + timeText(v.solved ? (v.ms || 0) : (v.elapsedMs || 0)) + '</b></span>'
        + (v.attempts > 1 ? '<span>спроба ' + v.attempts + '</span>' : '')
      : left + '<span><b class="mseat0">' + ((v.scores && v.scores[0]) || 0) + '</b> : '
        + '<b class="mseat1">' + ((v.scores && v.scores[1]) || 0) + '</b></span>'
        + '<span>лишилось ' + (v.left == null ? '?' : v.left) + '</span>';
    if (el.innerHTML !== html) el.innerHTML = html;
    return el;
  }

  /// Таймер дня цокає локально: вид приходить рідко, а секунди мають бігти.
  function clock(root, ctx) {
    const st = state(root);
    const v = ctx.view || {};
    st.base = v.solved ? (v.ms || 0) : (v.elapsedMs || 0);
    st.at = Date.now();
    // Підірвався — спроба скінчилась, час стоїть; розв'язав — теж.
    st.frozen = !!v.solved || !!(v.result && v.result.reason === 'boom') || !ctx.playing;
    if (!st.timer) st.timer = setInterval(() => tickClock(root), 100);
    tickClock(root);
  }

  function tickClock(root) {
    const st = root._mines;
    const el = root.querySelector('.mtime b');
    if (!st || !el) return;
    if (!el.isConnected) return;
    const ms = st.frozen ? st.base : st.base + (Date.now() - st.at);
    const text = timeText(ms);
    if (el.textContent !== text) el.textContent = text;
  }

  /// Кнопки під полем: режим прапорця (для пальця), «Спробувати ще» в дні, «Здаюсь» у дуелі.
  function buttons(root, ctx, daily) {
    const st = state(root);
    const v = ctx.view || {};
    let el = root.querySelector(':scope > .mbtns');
    if (!el) {
      el = document.createElement('div');
      el.className = 'mbtns';
      root.appendChild(el);
    }
    // Поле каркас міг перебудувати (змінився розмір) — тоді воно опиниться після кнопок.
    if (root.lastElementChild !== el) root.appendChild(el);

    const dead = !!(v.result && v.result.reason === 'boom');
    const out = [];
    if (ctx.mine && ctx.playing && !dead)
      out.push('<button type="button" class="ghost mflag' + (st.flagMode ? ' on' : '') + '" data-m="flag">🚩 Прапорець</button>');
    if (daily && ctx.mine && dead) out.push('<button type="button" class="primary" data-m="restart">Спробувати ще</button>');
    if (!daily && ctx.mine && ctx.playing) out.push('<button type="button" class="ghost" data-m="resign">Здаюсь</button>');
    const html = out.join('');
    if (el.innerHTML !== html) el.innerHTML = html;
    el.querySelectorAll('[data-m]').forEach((b) => b.onclick = () => {
      if (b.dataset.m === 'flag') { st.flagMode = !st.flagMode; buttons(root, ctx, daily); return; }
      ctx.act(b.dataset.m);
    });
  }

  /// Права кнопка й довгий тап ставлять прапорець. Слухачі вішаємо на поле один раз, а свіжий
  /// колбек кладемо на сам елемент — так само, як це робить ui.grid зі своїм onCell.
  function wire(board) {
    if (board._mwired) return;
    board._mwired = true;
    let timer = 0, from = null;
    const cellOf = (e) => (e.target.closest ? e.target.closest('.cell') : null);
    const stop = () => { clearTimeout(timer); timer = 0; from = null; };

    board.addEventListener('contextmenu', (e) => {
      const b = cellOf(e);
      if (!b) return;
      e.preventDefault();
      if (!b.disabled && board._mflag) board._mflag(+b.dataset.i);
    });
    board.addEventListener('pointerdown', (e) => {
      const b = cellOf(e);
      if (!b || b.disabled) return;
      from = { x: e.clientX, y: e.clientY };
      clearTimeout(timer);
      timer = setTimeout(() => {
        timer = 0;
        board._meat = true;              // клік після довгого тапу — то вже не клік
        if (board._mflag) board._mflag(+b.dataset.i);
      }, LONG_MS);
    });
    // Палець поїхав — це гортання сторінки, а не прапорець.
    board.addEventListener('pointermove', (e) => {
      if (!from) return;
      if (Math.abs(e.clientX - from.x) > 8 || Math.abs(e.clientY - from.y) > 8) stop();
    });
    board.addEventListener('pointerup', stop);
    board.addEventListener('pointercancel', stop);
    board.addEventListener('pointerleave', stop);
    // Перехоплення до того, як клік дійде до кнопки й повернеться в слухач ui.grid.
    board.addEventListener('click', (e) => {
      if (!board._meat) return;
      board._meat = false;
      e.stopPropagation();
      e.preventDefault();
    }, true);
  }

  function paint(root, ctx, daily) {
    const v = ctx.view || {};
    const w = v.w || (daily ? 16 : 9);
    const cells = v.cells || '';
    const st = state(root);
    const live = !!ctx.myTurn;
    if (!live) st.flagMode = false;      // не твій хід — режим прапорця нема сенсу тримати

    bar(root, ctx, daily);
    if (daily) clock(root, ctx);

    const board = HGames.ui.grid(root, {
      cols: w,
      // до першого виду cells порожній: малюємо поле повного розміру, а не смужку в один ряд
      rows: cells.length ? Math.ceil(cells.length / w) : (v.h || w),
      cls: 'mines' + (w > 9 ? ' tiny' : ''),
      cell: (i) => face(cells[i], i === v.lastOpen, live),
      onCell: (i) => {
        if (st.flagMode) { if (closed(cells[i])) ctx.act('flag', { cell: i }); return; }
        ctx.act('open', { cell: i });
      },
    });
    board._mflag = (i) => { if (closed(cells[i])) ctx.act('flag', { cell: i }); };
    wire(board);
    buttons(root, ctx, daily);
  }

  const mod = (id, daily) => ({
    id,
    icon: ICON,
    seatNames: ['жовтий', 'зелений'],
    seatClass: ['x', 'o'],
    mount(root, ctx) { paint(root, ctx, daily); },
    update(root, ctx) { paint(root, ctx, daily); },
    unmount(root) {
      const st = root._mines;
      if (st && st.timer) clearInterval(st.timer);
      root._mines = null;
    },
    status(ctx) {
      const v = ctx.view || {};
      if (!daily) return '';                       // дуелі вистачає «Твій хід» / «Ходить …» від каркаса
      if (v.solved) return 'Розмінував за ' + timeText(v.ms || 0) + (v.attempts > 1 ? ' · спроба ' + v.attempts : '');
      if (v.result && v.result.reason === 'boom') return 'Бабах! Це була міна — тисни «Спробувати ще»';
      if (!ctx.playing) return '';
      return 'Лишилось клітинок: ' + (v.left == null ? '?' : v.left);
    },
  });

  HGames.register(mod('mines', false));
  HGames.register(mod('mines-daily', true));
})();
