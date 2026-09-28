/*
  Сапер: дуель ('mines') і Сапер дня ('mines-daily') — два модулі в одному файлі. Правила малювання
  однакові, різниця лише в рядку над полем (рахунок проти таймера) і в кнопці «Ану ще раз».
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

  /// Порівнюємо з тим рядком, що клали самі: innerHTML браузер серіалізує по-своєму (&#39; → ', data-x → data-x=""),
  /// і порівняння з ним майже ніколи не каже «однаково» — DOM перебудовувався б на кожен вид.
  const setHtml = (el, html) => { if (el._h !== html) { el._h = html; el.innerHTML = html; } };

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
    let el = root.querySelector(':scope > .mn-bar');
    if (!el) {
      el = document.createElement('div');
      el.className = 'mn-bar';
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
      ? left + '<span class="mn-time">⏱ <b>' + timeText(v.solved ? (v.ms || 0) : (v.elapsedMs || 0)) + '</b></span>'
        + (v.attempts > 1 ? '<span>спроба ' + v.attempts + '</span>' : '')
      : left + scoresHtml(ctx, v) + (hunt ? '' : '<span>лишилось ' + (v.left == null ? '?' : v.left) + '</span>');
    setHtml(el, html);
    return el;
  }

  /// Рахунок дуелі: удвох — «3 : 5», як було; у компанії — кожен своїм кольором і позначкою, вибулі закреслені.
  function scoresHtml(ctx, v) {
    const seats = Array.isArray(v.players) && v.players.length ? v.players : [0, 1];
    const sc = v.scores || [];
    const out = v.out || [];
    if (seats.length === 2 && !out.some(Boolean)) {
      return '<span><b class="mn-seat' + seats[0] + '">' + (sc[seats[0]] || 0) + '</b> : '
        + '<b class="mn-seat' + seats[1] + '">' + (sc[seats[1]] || 0) + '</b></span>';
    }
    return seats.map((i) => {
      const nick = ctx.esc(ctx.nickOf(i) || ctx.seatName(i));
      const gone = out[i];
      return '<span class="mn-p mn-seat' + i + (gone ? ' gone' : '') + (v.turn === i ? ' now' : '') + '" title="' + nick + '">'
        + MARK[i] + ' <i>' + nick + '</i> <b>' + (sc[i] || 0) + '</b>' + (gone ? ' ' + (OUT[gone] || '') : '') + '</span>';
    }).join('');
  }

  /// Одне речення про правила над полем — у мисливців і в компанії вони не ті, що в класичному сапері.
  function rule(root, ctx, v) {
    let el = root.querySelector(':scope > .mn-rule');
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
      el.className = 'mn-rule';
      const bar = root.querySelector(':scope > .mn-bar');
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
    // Цокає лише живий час: розв'язане чи підірване поле стоїть, і таймер на 100 мс тоді ні до чого.
    if (!st.frozen && !st.timer) st.timer = setInterval(() => { if (!document.hidden) tickClock(root); }, 100);
    else if (st.frozen && st.timer) { clearInterval(st.timer); st.timer = 0; }
    tickClock(root);
  }

  function tickClock(root) {
    const st = root._mines;
    const el = root.querySelector('.mn-time b');
    if (!st || !el) return;
    if (!el.isConnected) return;
    const ms = st.frozen ? st.base : st.base + (Date.now() - st.at);
    const text = timeText(ms);
    if (el.textContent !== text) el.textContent = text;
  }

  const escHtml = (s) => String(s == null ? '' : s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);

  /// Табло дня просто над полем: «Сьогодні: Оля 1:23 🔥4 · Петро 2:10 · ти — 2-й» і хто цього тижня грав, а сьогодні ще ні.
  /// Дані — з виду (сервер тримає їх у пам'яті), тож тут лише рядок; порожнє табло — запрошення бути першим.
  function dayBoard(root, ctx) {
    let el = root.querySelector(':scope > .mn-day');
    if (!el) {
      el = document.createElement('div');
      el.className = 'mn-day';
      const bar = root.querySelector(':scope > .mn-bar');
      root.insertBefore(el, bar ? bar.nextSibling : root.firstChild);
    }
    const v = ctx.view || {};
    const b = v.board;
    const me = String((ctx.nickOf && ctx.seat != null && ctx.nickOf(ctx.seat)) || '').trim().toLowerCase();
    let html = '';
    if (b) {
      const rows = b.rows || [];
      const mine = rows.findIndex((r) => (r.n || '').trim().toLowerCase() === me);
      const fire = (n) => (n >= 2 ? ' <span class="mn-fire" title="Днів поспіль">🔥' + n + '</span>' : '');
      const cell = (r, i) => '<span class="mn-dr' + (i === mine ? ' me' : '') + '">' + (i === 0 ? '🥇 ' : '')
        + '<i>' + escHtml(r.n) + '</i> ' + timeText(r.ms) + (r.a > 1 ? '<small> ×' + r.a + '</small>' : '') + fire(r.st) + '</span>';
      html = rows.length
        ? '<span class="mn-dl">Сьогодні:</span> ' + rows.slice(0, 5).map(cell).join(' · ')
          + (mine >= 5 ? ' · <span class="mn-dr me">ти — ' + (mine + 1) + '-й</span>' : '')
          + (rows.length > 5 && mine < 5 ? ' <small>і ще ' + (rows.length - 5) + '</small>' : '')
        : '<span class="mn-dl">Сьогодні ще ніхто не розмінував — будь першим</span>';
      const wait = (b.wait || []).filter((w) => (w.n || '').trim().toLowerCase() !== me);
      if (wait.length) html += '<br><span class="mn-dl">Ще не проходили:</span> '
        + wait.map((w) => '<i>' + escHtml(w.n) + '</i>' + (w.st >= 2 ? ' <span class="mn-fire dim" title="Вогник згасне опівночі">🔥' + w.st + '</span>' : '')).join(', ');
    }
    el.hidden = !html;
    setHtml(el, html);
  }

  /// «👻 Привид найшвидшого»: розібрати запис ходів («сотні секунди, o/f/c, клітинка») у список кроків.
  function ghostMoves(mv) {
    const out = [];
    for (const t of String(mv || '').split(' ')) {
      const m = /^(\d+)([ofc])(\d+)$/.exec(t);
      if (m) out.push({ t: +m[1] * 10, k: m[2], c: +m[3] });
    }
    return out;
  }

  /// Програти привида на своєму (вже розв'язаному) полі: числа й міни відомі з виду, відкриття з повінню — як на сервері.
  function ghostStart(root, ctx) {
    const v = ctx.view || {};
    const g = v.ghost;
    if (!g || !v.cells) return;
    const st = state(root);
    ghostStop(root);
    const w = v.w || 16, n = v.cells.length, h = Math.ceil(n / w);
    st.ghost = { nick: g.n, ms: g.ms, moves: ghostMoves(g.mv), i: 0, t0: Date.now(), speed: 1, at: 0, base: 0,
      w, h, num: v.cells, open: new Uint8Array(n), flag: new Uint8Array(n), last: null, cells: '' };
    ghostCells(st.ghost);
    st.ghost.timer = setInterval(() => ghostStep(root), 50);
    ghostStep(root);
  }

  function ghostStop(root) {
    const st = root._mines;
    if (!st || !st.ghost) return;
    clearInterval(st.ghost.timer);
    st.ghost = null;
  }

  function ghostNear(g, c) {
    const x = c % g.w, y = (c / g.w) | 0, out = [];
    for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) {
      if (!dx && !dy) continue;
      const nx = x + dx, ny = y + dy;
      if (nx >= 0 && ny >= 0 && nx < g.w && ny < g.h) out.push(ny * g.w + nx);
    }
    return out;
  }

  function ghostOpen(g, c) {
    const stack = [c];
    while (stack.length) {
      const i = stack.pop();
      if (g.open[i] || g.flag[i]) continue;
      g.open[i] = 1;
      if (g.num[i] === '0') for (const j of ghostNear(g, i)) if (!g.open[j]) stack.push(j);
    }
  }

  function ghostCells(g) {
    let s = '';
    for (let i = 0; i < g.num.length; i++) s += g.open[i] ? g.num[i] : g.flag[i] ? 'F' : '#';
    g.cells = s;
  }

  function ghostStep(root) {
    const st = root._mines;
    const g = st && st.ghost;
    if (!g) return;
    if (document.hidden) { g.t0 = Date.now() - g.at / g.speed; return; }   // у схованій вкладці привид чекає
    g.at = (Date.now() - g.t0) * g.speed;
    let moved = false;
    while (g.i < g.moves.length && g.moves[g.i].t <= g.at) {
      const m = g.moves[g.i++];
      if (m.k === 'f') g.flag[m.c] ^= 1;
      else if (m.k === 'c') { for (const j of ghostNear(g, m.c)) if (!g.open[j] && !g.flag[j]) ghostOpen(g, j); }
      else ghostOpen(g, m.c);
      g.last = m.c;
      moved = true;
    }
    const done = g.i >= g.moves.length;
    if (done) { g.at = g.ms; clearInterval(g.timer); g.timer = 0; }
    if (moved || done) { ghostCells(g); paint(root, st.ctx, true); }
    const el = root.querySelector('.mn-time b');
    if (el) { const t = timeText(Math.min(g.at, g.ms)); if (el.textContent !== t) el.textContent = t; }
  }

  /// Кнопки під полем: режим прапорця (для пальця), «Ану ще раз» в дні, «Здаюсь» у дуелі.
  function buttons(root, ctx, daily) {
    const st = state(root);
    const v = ctx.view || {};
    let el = root.querySelector(':scope > .mn-btns');
    if (!el) {
      el = document.createElement('div');
      el.className = 'mn-btns';
      root.appendChild(el);
    }
    // Поле каркас міг перебудувати (змінився розмір) — тоді воно опиниться після кнопок.
    if (root.lastElementChild !== el) root.appendChild(el);

    // у дуелі «мертвий» — той, хто вибув сам; у дні — спроба, що підірвалась
    const dead = daily ? !!(v.result && v.result.reason === 'boom') : !!(ctx.mine && v.out && v.out[ctx.seat]);
    const out = [];
    if (ctx.mine && ctx.playing && !dead)
      out.push('<button type="button" class="ghost mn-flag' + (st.flagMode ? ' on' : '') + '" data-m="flag">🚩 Прапорець</button>');
    if (daily && ctx.mine && dead) out.push('<button type="button" class="primary" data-m="restart">Ану ще раз</button>');
    if (!daily && ctx.mine && ctx.playing && !dead) out.push('<button type="button" class="ghost" data-m="resign">Здаюсь</button>');
    if (daily && v.solved && v.ghost) {
      const g = st.ghost;
      if (!g) out.push('<button type="button" class="primary" data-m="ghost">👻 Привид: ' + escHtml(v.ghost.n) + ' ' + timeText(v.ghost.ms) + '</button>');
      else {
        out.push('<button type="button" class="ghost" data-m="gspeed">' + (g.speed === 1 ? '⏩ Швидше ×4' : '▶ Звичайно') + '</button>');
        out.push('<button type="button" class="ghost" data-m="gstop">⏹ Годі</button>');
      }
    }
    const html = out.join('');
    setHtml(el, html);
    el.querySelectorAll('[data-m]').forEach((b) => b.onclick = () => {
      if (b.dataset.m === 'flag') { st.flagMode = !st.flagMode; buttons(root, ctx, daily); return; }
      if (b.dataset.m === 'ghost') { ghostStart(root, ctx); buttons(root, ctx, daily); return; }
      if (b.dataset.m === 'gstop') { ghostStop(root); paint(root, ctx, daily); return; }
      if (b.dataset.m === 'gspeed') {
        const g = st.ghost;
        if (g) { g.speed = g.speed === 1 ? 4 : 1; g.t0 = Date.now() - g.at / g.speed; if (!g.timer) g.timer = setInterval(() => ghostStep(root), 50); }
        buttons(root, ctx, daily);
        return;
      }
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
    let el = root.querySelector(':scope > .mn-wrap');
    if (!el) {
      el = document.createElement('div');
      el.className = 'mn-wrap';
      root.appendChild(el);
    }
    return el;
  }

  function paint(root, ctx, daily) {
    const st = state(root);
    st.ctx = ctx;
    // привид грає лише на розв'язаному полі; нова спроба/інший вид — привида геть
    if (st.ghost && !(ctx.view && ctx.view.solved)) ghostStop(root);
    const g = st.ghost;
    const v = g ? Object.assign({}, ctx.view, { cells: g.cells, lastOpen: g.last }) : (ctx.view || {});
    const w = v.w || (daily ? 16 : 9);
    const cells = v.cells || '';
    const live = !g && !!ctx.myTurn;
    if (!live) st.flagMode = false;      // не твій хід — режим прапорця нема сенсу тримати

    bar(root, g ? Object.assign({}, ctx, { view: v }) : ctx, daily);
    if (daily) dayBoard(root, ctx);
    if (daily && !g) clock(root, ctx);
    else if (!daily) rule(root, ctx, v);
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
    mount(root, ctx) { ctx._root = root; paint(root, ctx, daily); },
    update(root, ctx) { ctx._root = root; paint(root, ctx, daily); },
    unmount(root) {
      const st = root._mines;
      if (st && st.timer) clearInterval(st.timer);
      ghostStop(root);
      root._mines = null;
    },
    status(ctx) {
      const v = ctx.view || {};
      if (!daily) return '';                       // дуелі вистачає «Твій хід» / «Ходить …» від каркаса
      const g = ctx._root && ctx._root._mines && ctx._root._mines.ghost;
      if (v.solved && g) return '👻 Привид: ' + g.nick + ' розміновує за ' + timeText(g.ms) + (g.speed > 1 ? ' · ×4' : '');
      if (v.solved) return 'Є! Поле чисте за ' + timeText(v.ms || 0) + (v.attempts > 1 ? ' · спроба ' + v.attempts : '')
        + (v.streak >= 2 ? ' · 🔥 ' + v.streak + ' дн. поспіль' : '');
      if (v.result && v.result.reason === 'boom') return 'Бабах! Це була міна — тисни «Ану ще раз»';
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
      v: '2026-09-29',
      title: 'Сапер дня: табло, вогник і привид',
      items: [
        '🏆 Над полем — табло дня: хто сьогодні вже розмінував і за скільки, а хто цього тижня грав, та сьогодні ще ні',
        '🔥 Вогник біля ніка — скільки днів поспіль людина розміновує поле дня',
        '👻 Розмінував — тисни «Привид» і дивись, як найшвидший сьогодні пройшов те саме поле, хід за ходом (можна ×4)',
      ],
    },
  }));
})();
