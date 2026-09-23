/*
  Сапер: дуель ('mines') і Сапер дня ('mines-daily') — два модулі в одному файлі. Правила малювання
  однакові, різниця лише в рядку над полем (рахунок проти таймера) і в кнопці «Спробувати ще».
  Каркас дозволяє кілька register в одному файлі, тому окремого mines-daily.js не існує: у паспорті
  щоденного стоїть Client: "mines", і завантажувач іде по цей самий файл.

  Вид із сервера (Impl/Mines.cs):
    { w, h, mines, turn, cells, scores, left, lastOpen, result }
    + для дуелі на 2–4: { mode: 'boom'|'hunt', players, out: [null|'boom'|'resign'|'left'] × 4, lastBy,
      unclaimed, owners: рядок '0'..'3'/'.' (чия міна, лише в мисливців), result.winners, result.places }
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
  /// Позначка місця поруч із кольором: форма — для тих, кому кольори зливаються.
  const MARK = ['●', '▲', '■', '◆'];
  const OUT = { boom: '💥', resign: '🏳', left: '🚪' };

  function state(root) {
    if (!root._mines) root._mines = { flagMode: false, base: 0, at: 0, frozen: true, timer: 0 };
    return root._mines;
  }

  /// Як виглядає клітинка. Кольори цифр — у mines.css, тут лише клас. last — клас останнього ходу
  /// (з кольором того, хто ходив), owner — чия це міна в мисливців.
  function face(ch, last, live, owner, chord) {
    const mark = last ? ' last' + last : '';
    if (ch === '*') return { html: '💣', cls: 'bomb' + (owner != null ? ' own own' + owner : '') + mark, disabled: true };
    if (ch === 'F') return { html: '🚩', cls: 'closed flag' + mark, disabled: !live };
    if (ch === undefined || ch === '#') return { html: '', cls: 'closed' + mark, disabled: !live };
    const n = +ch || 0;
    // У дні відкрите число клікабельне: тиск по ньому відкриває сусідів, якщо прапорців довкола вже досить.
    return { html: n ? String(n) : '', cls: 'open n' + n + mark + (chord && n ? ' chord' : ''), disabled: !(chord && n && live) };
  }

  /// «42,3 с» до хвилини, далі «2:07». Кома, бо рядок читає людина українською.
  function timeText(ms) {
    const s = Math.max(0, ms) / 1000;
    if (s < 60) return s.toFixed(1).replace('.', ',') + ' с';
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
    const hunt = v.mode === 'hunt';
    const left = hunt
      ? '<span title="Мін ще ніхто не знайшов">💣 <b>' + (v.unclaimed == null ? v.mines : v.unclaimed) + '</b></span>'
      : '<span>🚩 <b>' + ((v.mines || 0) - flags) + '</b></span>';
    const html = daily
      ? left + '<span class="mtime">⏱ <b>' + timeText(v.solved ? (v.ms || 0) : (v.elapsedMs || 0)) + '</b></span>'
        + (v.attempts > 1 ? '<span>спроба ' + v.attempts + '</span>' : '')
      : left + scoresHtml(ctx, v) + (hunt ? '' : '<span>лишилось ' + (v.left == null ? '?' : v.left) + '</span>');
    if (el.innerHTML !== html) el.innerHTML = html;
    return el;
  }

  /// Рахунок дуелі: удвох — «3 : 5», як було; у компанії — кожен своїм кольором і позначкою, вибулі закреслені.
  function scoresHtml(ctx, v) {
    const seats = Array.isArray(v.players) && v.players.length ? v.players : [0, 1];
    const sc = v.scores || [];
    const out = v.out || [];
    if (seats.length === 2 && !out.some(Boolean)) {
      return '<span><b class="mseat' + seats[0] + '">' + (sc[seats[0]] || 0) + '</b> : '
        + '<b class="mseat' + seats[1] + '">' + (sc[seats[1]] || 0) + '</b></span>';
    }
    return seats.map((i) => {
      const nick = ctx.esc(ctx.nickOf(i) || ctx.seatName(i));
      const gone = out[i];
      return '<span class="mp mseat' + i + (gone ? ' gone' : '') + (v.turn === i ? ' now' : '') + '" title="' + nick + '">'
        + MARK[i] + ' <i>' + nick + '</i> <b>' + (sc[i] || 0) + '</b>' + (gone ? ' ' + (OUT[gone] || '') : '') + '</span>';
    }).join('');
  }

  /// Одне речення про правила над полем — у мисливців і в компанії вони не ті, що в класичному сапері.
  function rule(root, ctx, v) {
    let el = root.querySelector(':scope > .mrule');
    const seats = (v.players || []).length;
    let text = '';
    if (ctx.playing && v.mode === 'hunt') text = '💣 Мисливці: знайшов міну — очко і ходиш ще; порожня клітинка — хід далі';
    else if (ctx.playing && seats > 2) text = '💥 Міна — вибув. Останній, хто вцілів, або найбільше очок на чистому полі — перемога';
    else if (!ctx.playing && v.result && (v.result.places || []).length > 2) {
      const medals = ['🥇', '🥈', '🥉', '4.'];
      text = v.result.places.map((i, n) => medals[n] + ' ' + ctx.esc(ctx.nickOf(i) || ctx.seatName(i))
        + ' ' + ((v.scores || [])[i] || 0)).join(' · ');
    }
    if (!text) { if (el) el.remove(); return; }
    if (!el) {
      el = document.createElement('div');
      el.className = 'mrule';
      const bar = root.querySelector(':scope > .mbar');
      root.insertBefore(el, bar ? bar.nextSibling : root.firstChild);
    }
    if (el.textContent !== text) el.textContent = text;
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

    // у дуелі «мертвий» — той, хто вибув сам; у дні — спроба, що підірвалась
    const dead = daily ? !!(v.result && v.result.reason === 'boom') : !!(ctx.mine && v.out && v.out[ctx.seat]);
    const out = [];
    if (ctx.mine && ctx.playing && !dead)
      out.push('<button type="button" class="ghost mflag' + (st.flagMode ? ' on' : '') + '" data-m="flag">🚩 Прапорець</button>');
    if (daily && ctx.mine && dead) out.push('<button type="button" class="primary" data-m="restart">Спробувати ще</button>');
    if (!daily && ctx.mine && ctx.playing && !dead) out.push('<button type="button" class="ghost" data-m="resign">Здаюсь</button>');
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
    /// Прапорець поставив жест, а не клік — тож клік, який іде слідом, треба проковтнути:
    /// інакше та сама клітинка ще й відкриється.
    const flagByGesture = (b) => {
      board._meat = true;
      if (board._mflag) board._mflag(+b.dataset.i);
    };

    board.addEventListener('contextmenu', (e) => {
      const b = cellOf(e);
      if (!b) return;
      e.preventDefault();
      // На сенсорі браузер шле contextmenu приблизно тоді ж, коли спрацьовує наш довгий тап. Хто
      // перший — той і ставить прапорець; другий лише гасить таймер, щоб не перемкнути прапорець назад.
      const already = board._meat;
      stop();
      if (already || b.disabled) return;
      flagByGesture(b);
    });
    board.addEventListener('pointerdown', (e) => {
      // Новий жест — старий «з'їдач кліку» більше не діє. Скидаємо до всіх перевірок: після
      // contextmenu кліку не буває, і зведений прапор інакше проковтнув би наступний тап.
      board._meat = false;
      const b = cellOf(e);
      if (!b || b.disabled) return;
      // Миші довгий тап не потрібен — у неї є права кнопка. А головне: повільний клік лівою (на
      // клітинці 16×16 цілитись доводиться саме так) мусить лишатись кліком, а не ставати прапорцем.
      if (e.pointerType === 'mouse') return;
      from = { x: e.clientX, y: e.clientY };
      clearTimeout(timer);
      timer = setTimeout(() => {
        timer = 0;
        flagByGesture(b);
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

  /// Поле живе у власній обгортці, а не просто в корені картки: на телефоні велике поле краще дати
  /// прокрутити вбік, ніж стиснути до клітинки в палець завтовшки (решта — у mines.css).
  function boardHost(root) {
    let el = root.querySelector(':scope > .mwrap');
    if (!el) {
      el = document.createElement('div');
      el.className = 'mwrap';
      root.appendChild(el);
    }
    return el;
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
    else rule(root, ctx, v);
    const owners = v.owners || '';
    const lastCls = v.lastBy != null ? ' by' + v.lastBy : '';

    const board = HGames.ui.grid(boardHost(root), {
      cols: w,
      // до першого виду cells порожній: малюємо поле повного розміру, а не смужку в один ряд
      rows: cells.length ? Math.ceil(cells.length / w) : (v.h || w),
      cls: 'mines' + (w > 9 ? ' tiny' : '') + (w > 12 ? ' huge' : ''),
      cell: (i) => face(cells[i], i === v.lastOpen ? lastCls || ' ' : '', live,
        owners[i] && owners[i] !== '.' ? +owners[i] : null, daily),
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
    seatNames: ['жовтий', 'зелений', 'глиняний', 'сірий'],
    seatClass: ['x', 'o', 'c', 'd'],
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

  HGames.register(Object.assign(mod('mines', false), {
    news: {
      v: '2026-09-24',
      title: 'Сапер-дуель: тепер на компанію',
      items: [
        '👥 За одним полем 2–4 сапери, ходите по черзі — кожен своїм кольором і позначкою',
        '💥 Підірвався — вибув, решта грають далі; останній, хто вцілів, виграв',
        '💣 Новий режим «Мисливці»: міна — твоє очко і ще хід, хто назбирав більше — той і переміг',
        '📐 Середнє поле 12×12 — якраз на трьох-чотирьох',
      ],
    },
  }));
  HGames.register(Object.assign(mod('mines-daily', true), {
    news: {
      v: '2026-09-24',
      title: 'Сапер дня: швидше по числах',
      items: [
        '👆 Тисни на відкрите число, довкола якого вже стоять усі прапорці, — решта сусідів відкриється одним махом',
        '💥 Прапорець стояв не там — бабах, як у справжньому сапері, тож став їх чесно',
        '🔧 На ПК поле більше не стискається в дрібну сітку',
      ],
    },
  }));
})();
