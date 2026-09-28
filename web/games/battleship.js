/*
  Морський бій на 2–4. Удвох — класика: своє поле і чуже. Утрьох і вчотирьох — кожен проти кожного:
  своє поле і стільки чужих, скільки суперників; у свій хід б'єш по будь-якому живому.

  Вид (Impl/Battleship.cs):
    { phase: 'lobby'|'placing'|'battle'|'done', turn, placeUntil, turnUntil, placeSeconds, turnSeconds,
      sea: { key, w, h, fleet }, players: [місця в грі],
      me: { ships, ready, hits, misses } | null,
      enemy: known,                              // старе «чуже поле» на двох
      boards: [known | null] × 4,                // публічне знання про кожне поле; null — місце не грає
      feed: [{ by, at, cell, res: 'miss'|'hit'|'sunk'|'out'|'left'|'mine', size, auto, revenge, quip, rx: [4]|null,
               tool, cells, n, boom }],
      revenge: { by, on } | null,                 // остання помста: вибулий by має один постріл по on
      react: [4], reactN: [4],                     // остання реакція місця і лічильник (бульбашка — коли росте)
      bots, arsenal,
      shots, result: { winner, shots[], hits[], sank[], places[] } | null }
    known = { hits, misses, sunk, ready, left, out, bot, reveal, boom, patched }   // reveal — увесь флот, лише після кінця
    bot — ім'я Глека (з 🤖), якщо місце зайняв бот «Глек підсідає»
  Кадр: { phase, turn, placeLeft, ready[], left[], shots, winner } — кораблів там нема й бути не може:
  кадр летить усім одразу.

  Розстановка живе в браузері: поки гравець совгає кораблі, сервер про них не знає. На сервер іде
  готовий флот ('place'), і саме сервер вирішує, чи він законний — тут ті самі перевірки лише для
  того, щоб не сварити людину тостом на кожен другий клік.
*/
(() => {
  const COLS = 'abcdefghij';
  const CLASSIC = { key: 'classic', w: 10, h: 10, fleet: [4, 3, 3, 2, 2, 2, 1, 1, 1, 1] };
  /// Позначка місця поруч із кольором: дальтонік розрізнить форму, навіть коли колір зливається.
  const MARK = ['●', '▲', '■', '◆'];
  const DECKS = ['', 'однопалубний', 'двопалубний', 'трипалубний', 'чотирипалубний'];

  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M1.6 9.6h12.8l-1.9 3.5a2 2 0 0 1-1.8 1H5.3a2 2 0 0 1-1.8-1L1.6 9.6Z" fill="var(--clay)"/>'
    + '<path d="M8 1.4v7.2M8 3 12 5 8 7" fill="none" stroke="var(--accent)" stroke-width="1.4" stroke-linejoin="round"/></svg>';

  const reduced = () => window.matchMedia && matchMedia('(prefers-reduced-motion: reduce)').matches;

  /// Порівнюємо з тим рядком, що клали самі: innerHTML браузер серіалізує по-своєму (&#39; → ', title="…"),
  /// і порівняння з ним майже ніколи не каже «однаково» — підписи й стрічка перебудовувались на кожен вид.
  const setHtml = (el, html) => { if (el._h !== html) { el._h = html; el.innerHTML = html; } };

  // ---------------------------------------------------------------------------------------------
  // Геометрія: та сама, що на сервері, тільки коротша. g — море з виду: { w, h, fleet }
  // ---------------------------------------------------------------------------------------------

  function seaOf(v) {
    const s = (v && v.sea) || {};
    return { key: s.key || CLASSIC.key, w: s.w || CLASSIC.w, h: s.h || CLASSIC.h, fleet: s.fleet || CLASSIC.fleet };
  }

  function halo(g, cell) {
    const x = cell % g.w, y = (cell / g.w) | 0, out = [];
    for (let dy = -1; dy <= 1; dy++) {
      for (let dx = -1; dx <= 1; dx++) {
        const nx = x + dx, ny = y + dy;
        if ((dx || dy) && nx >= 0 && nx < g.w && ny >= 0 && ny < g.h) out.push(ny * g.w + nx);
      }
    }
    return out;
  }

  /// Куди новий корабель ставити не можна: чужі клітинки і все, що до них тулиться.
  function blocked(g, plan, skip) {
    const no = new Set();
    plan.forEach((ship, i) => {
      if (i === skip) return;
      for (const c of ship.cells) { no.add(c); for (const n of halo(g, c)) no.add(n); }
    });
    return no;
  }

  /// Клітинки корабля від anchor; null — не влазить у поле.
  function span(g, anchor, size, horiz) {
    const x = anchor % g.w, y = (anchor / g.w) | 0;
    if (horiz ? x + size > g.w : y + size > g.h) return null;
    const cells = [];
    for (let i = 0; i < size; i++) cells.push(horiz ? y * g.w + x + i : (y + i) * g.w + x);
    return cells;
  }

  function fits(g, plan, anchor, size, horiz, skip) {
    const cells = span(g, anchor, size, horiz);
    if (!cells) return null;
    const no = blocked(g, plan, skip);
    return cells.some((c) => no.has(c)) ? null : cells;
  }

  const isHoriz = (cells) => cells.length < 2 || cells[1] === cells[0] + 1;

  /// Скільки кораблів ще не поставлено — по одному розміру на штуку.
  function rest(g, plan) {
    const left = g.fleet.slice();
    for (const ship of plan) {
      const i = left.indexOf(ship.cells.length);
      if (i >= 0) left.splice(i, 1);
    }
    return left;
  }

  // ---------------------------------------------------------------------------------------------
  // Стан картки
  // ---------------------------------------------------------------------------------------------

  function state(root) {
    if (!root._bs) {
      root._bs = {
        plan: [],           // мої кораблі, поки їх не прийняв сервер
        pick: 4,            // розмір обраного корабля зі списку
        horiz: true,
        adopt: true,        // взяти розстановку з сервера (після F5 або «Випадково»)
        sent: false,        // сервер тримає мій флот (тобто є що скидати)
        pending: new Set(), // "поле:клітинка", куди постріл уже полетів, а відповідь ще ні
        phase: '',
        round: 0,
        sea: '',
        fx: '',             // останній постріл, який уже анімували (щоб не блимати на кожен вид)
        rxSeen: null,       // лічильники реакцій з минулого виду
        rxTimers: [],
        rxTimer: 0,
        rxUntil: 0,
        arm: '',            // штука з трюму, якою цілюсь зараз (замість пострілу)
      };
    }
    return root._bs;
  }

  function resetPlan(s, g) {
    s.plan = [];
    s.pick = g.fleet[0];
    s.horiz = true;
    s.adopt = true;
    s.sent = false;
    s.pending.clear();
  }

  // ---------------------------------------------------------------------------------------------
  // Поле
  // ---------------------------------------------------------------------------------------------

  function ensureGrid(side, g) {
    let grid = side.querySelector(':scope > .bs-grid');
    const dims = g.w + 'x' + g.h;
    if (grid && grid.dataset.dims === dims) return grid;
    if (grid) grid.remove();
    grid = document.createElement('div');
    grid.className = 'bs-grid';
    grid.dataset.dims = dims;
    grid.style.setProperty('--bs-cols', g.w);
    let html = '<span class="bs-lab"></span>';
    for (let x = 0; x < g.w; x++) html += '<span class="bs-lab">' + COLS[x] + '</span>';
    for (let y = 0; y < g.h; y++) {
      html += '<span class="bs-lab">' + (y + 1) + '</span>';
      for (let x = 0; x < g.w; x++) {
        const i = y * g.w + x;
        html += '<button type="button" class="bs-cell" data-i="' + i + '" aria-label="' + COLS[x] + (y + 1) + '"></button>';
      }
    }
    grid.innerHTML = html;
    // Слухач вішається раз, а колбек модуль дає новий на кожен update — тримаємо свіжий на елементі.
    grid.addEventListener('click', (e) => {
      const b = e.target.closest('.bs-cell');
      if (b && !b.disabled && grid._onCell) grid._onCell(+b.dataset.i);
    });
    // Мишею в розстановці видно корабель цілком ще до кліку (пальцем наведення нема — там тап одразу ставить).
    grid.addEventListener('pointerover', (e) => {
      if (e.pointerType !== 'mouse' || !grid._onHover) return;
      const b = e.target.closest('.bs-cell');
      grid._onHover(b ? +b.dataset.i : -1);
    });
    grid.addEventListener('pointerleave', () => { if (grid._onHover) grid._onHover(-1); });
    grid._cells = [...grid.querySelectorAll('.bs-cell')];
    side.appendChild(grid);
    return grid;
  }

  function ensureSide(host, key, caption, g, cls) {
    let side = host.querySelector(':scope > .bs-side[data-side="' + key + '"]');
    if (!side) {
      side = document.createElement('div');
      side.dataset.side = key;
      side.innerHTML = '<div class="bs-cap"></div>';
      host.appendChild(side);
    }
    const want = 'bs-side' + (cls ? ' ' + cls : '');
    if (side.className !== want) side.className = want;
    const cap = side.querySelector(':scope > .bs-cap');
    setHtml(cap, caption);
    ensureGrid(side, g);
    return side;
  }

  /// Прибрати поля, яких у цьому виді вже нема (хтось устав, фаза змінилась).
  function prune(host, keep) {
    host.querySelectorAll(':scope > .bs-side').forEach((el) => { if (!keep.has(el.dataset.side)) el.remove(); });
  }

  function paintGrid(side, g, cls, onCell, can, lab) {
    const grid = ensureGrid(side, g);
    grid._onCell = onCell || null;
    for (let i = 0; i < g.w * g.h; i++) {
      const b = grid._cells[i];
      const c = cls(i);
      const want = 'bs-cell' + (c ? ' ' + c : '');
      if (b.className !== want) b.className = want;
      const dis = !can || !can(i);
      if (b.disabled !== dis) b.disabled = dis;
      // Число радара (скільки цілих палуб у квадраті) — лише на своїх розвідданих клітинках.
      const n = lab ? lab(i) : '';
      if ((b.dataset.n || '') !== n) { if (n) b.dataset.n = n; else delete b.dataset.n; }
    }
  }

  /// Тінь корабля під мишею в розстановці: де він ляже, якщо клікнути; червонувата — сюди не стане.
  function preview(grid, g, s, size, at) {
    for (const c of grid._pv || []) grid._cells[c].classList.remove('pv', 'pvbad');
    grid._pv = [];
    grid._hoverAt = at == null ? -1 : at;
    if (!(at >= 0) || !size || s.plan.some((sh) => sh.cells.includes(at))) return;
    const x = at % g.w, y = (at / g.w) | 0, show = [];
    for (let i = 0; i < size; i++) {
      const nx = s.horiz ? x + i : x, ny = s.horiz ? y : y + i;
      if (nx < g.w && ny < g.h) show.push(ny * g.w + nx);
    }
    const cls = fits(g, s.plan, at, size, s.horiz, -1) ? 'pv' : 'pvbad';
    for (const c of show) grid._cells[c].classList.add(cls);
    grid._pv = show;
  }

  /// Мітки чужого поля: промах, влучання, потоплений; після кінця — ще й кораблі, що вціліли.
  function knownMarks(known) {
    const m = new Map();
    for (const ship of (known && known.reveal) || []) for (const c of ship) m.set(c, 'ghost');
    for (const c of (known && known.misses) || []) m.set(c, 'miss');
    for (const c of (known && known.hits) || []) m.set(c, 'hit');
    for (const ship of (known && known.sunk) || []) for (const c of ship) m.set(c, 'sunk');
    for (const c of (known && known.boom) || []) m.set(c, 'miss boom');
    for (const c of (known && known.patched) || []) if (!m.has(c)) m.set(c, 'patch');
    return m;
  }

  /// Мітки свого поля: те саме плюс власні кораблі, з яких видно потоплені цілком.
  function myMarks(me, ships) {
    const m = new Map();
    const hits = new Set((me && me.hits) || []);
    for (const c of (me && me.misses) || []) m.set(c, 'miss');
    for (const ship of ships) {
      const dead = ship.every((c) => hits.has(c));
      for (const c of ship) m.set(c, dead ? 'ship sunk' : hits.has(c) ? 'ship hit' : 'ship');
    }
    for (const c of hits) if (!m.has(c)) m.set(c, 'hit');
    for (const c of (me && me.mines) || []) m.set(c, (m.get(c) || '') + ' mine');
    return m;
  }

  // ---------------------------------------------------------------------------------------------
  // Люди й події
  // ---------------------------------------------------------------------------------------------

  const players = (v) => (Array.isArray(v.players) && v.players.length ? v.players : [0, 1]);

  /// Імена Глеків за столом: місця ботів у каркасі порожні, тож ім'я береться з виду.
  let BOTS = [];
  const nameOf = (ctx, seat) => BOTS[seat] || ctx.nickOf(seat) || ctx.seatName(seat);

  /// Реакції на свіжий постріл (№62): 😱 😂 🎯.
  const REACTS = [['😱', 'ой-йой'], ['😂', 'ха!'], ['🎯', 'снайпер']];

  function who(ctx, seat, bold) {
    const name = ctx.esc(nameOf(ctx, seat));
    return '<span class="bs-who s' + seat + '">' + MARK[seat] + ' ' + (bold ? '<b>' + name + '</b>' : name) + '</span>';
  }

  /// Один рядок стрічки: хто, по кому і що вийшло. Без дієслів у минулому часі — рід ніка ми не знаємо.
  function feedLine(ctx, f) {
    const auto = f.auto ? '⏰ ' : '';
    const by = who(ctx, f.by), at = who(ctx, f.at);
    const tail = f.auto ? ' <i>(гармата вистрілила сама)</i>' : '';
    if (f.tool && TOOL_LINE[f.tool]) return TOOL_LINE[f.tool](ctx, f, by, at);
    if (f.res === 'mine') return '💥 ' + by + ' → ' + at + ': міна! Рикошет — ' + (BOOM[f.boom] || 'мимо');
    if (f.revenge) {
      const r = { miss: 'мимо', hit: 'влучання!', sunk: (DECKS[f.size] || 'корабель') + ' на дні!', out: 'флот на дні!' }[f.res] || '';
      return auto + '💀 Остання помста: ' + by + ' → ' + at + ': ' + r + tail;
    }
    switch (f.res) {
      case 'miss': return auto + '💦 ' + by + ' → ' + at + ': мимо' + tail;
      case 'hit': return auto + '🎯 ' + by + ' → ' + at + ': влучання!' + tail;
      case 'sunk': return auto + '🔥 ' + by + ' → ' + at + ': ' + (DECKS[f.size] || 'корабель') + ' на дні' + tail;
      case 'out': return auto + '☠️ ' + at + ': увесь флот на дні, останній постріл — ' + by + tail;
      case 'left': return '🚪 ' + by + ' — з-за столу, флот на дно';
      default: return '';
    }
  }

  /// Реакції столу на постріл: смайлик кожного, хто відгукнувся (нік — у підказці).
  function rxLine(ctx, f) {
    if (!f.rx) return '';
    const out = [];
    f.rx.forEach((e, seat) => {
      if (e >= 0 && REACTS[e]) out.push('<span class="bs-rxi" title="' + ctx.esc(nameOf(ctx, seat)) + '">' + REACTS[e][0] + '</span>');
    });
    return out.length ? ' <span class="bs-rxs">' + out.join('') + '</span>' : '';
  }

  /// Рядки стрічки для штук з арсеналу — див. блок «Арсенал» нижче.
  const cellName = (g, c) => COLS[c % g.w] + (((c / g.w) | 0) + 1);
  let SEA = CLASSIC;
  const outcome = (f) => (f.res === 'mine' ? '💥 збито міною, рикошет — ' + (BOOM[f.boom] || 'мимо')
    : { miss: 'усе мимо', hit: 'влучання!', sunk: (DECKS[f.size] || 'корабель') + ' на дні!', out: 'флот на дні!' }[f.res] || '');
  const TOOL_LINE = {
    plane: (ctx, f, by, at) => '✈️ ' + by + ' → ' + at + ': літак над рядком ' + (((f.cell / SEA.w) | 0) + 1) + ' — ' + outcome(f),
    torpedo: (ctx, f, by, at) => '🚀 ' + by + ' → ' + at + ': торпеда стовпцем ' + COLS[f.cell % SEA.w] + ' — ' + outcome(f),
    bomb: (ctx, f, by, at) => '💣 ' + by + ' → ' + at + ': бомба на ' + cellName(SEA, (f.cells || [f.cell])[0]) + ' — ' + outcome(f),
    radar: (ctx, f, by, at) => '📡 ' + by + ' → ' + at + ': радар на ' + cellName(SEA, f.cell)
      + (f.n == null ? '' : ' — цілих палуб: <b>' + f.n + '</b>'),
    repair: (ctx, f, by) => '🔧 ' + by + ' латає свій корабель',
  };
  const BOOM = { hit: 'влучання по ньому самому', sunk: 'власний корабель на дні', out: 'власний флот на дні', none: 'мимо' };

  function feed(root, ctx, v) {
    let el = root.querySelector(':scope > .bs-feed');
    // Удвох — два останні рядки (там слово Глека, реакції й арсенал), у компанії — три: без них не зрозуміти, хто кого.
    const items = (v.feed || []).slice(players(v).length > 2 ? -3 : -2).reverse();
    const show = (v.phase === 'battle' || v.phase === 'done') && items.length;
    if (!show) { if (el) el.remove(); return; }
    if (!el) {
      el = document.createElement('div');
      el.className = 'bs-feed';
      el.innerHTML = '<div class="bs-lines" aria-live="polite"></div><div class="bs-react" hidden>'
        + REACTS.map((r, i) => '<button type="button" class="ghost bs-rxb" data-rx="' + i + '" title="' + r[1] + '">' + r[0] + '</button>').join('')
        + '</div>';
      el.querySelector('.bs-react').addEventListener('click', (e) => {
        const b = e.target.closest('[data-rx]');
        const o = el._o;
        if (!b || !o || b.disabled) return;
        const st = state(root);
        st.rxUntil = performance.now() + 1250;
        el.querySelectorAll('.bs-rxb').forEach((x) => { x.disabled = true; });
        clearTimeout(st.rxTimer);
        st.rxTimer = setTimeout(() => el.querySelectorAll('.bs-rxb').forEach((x) => { x.disabled = false; }), 1300);
        o.ctx.act('react', { e: +b.dataset.rx }).catch(() => {});
      });
      root.insertBefore(el, root.firstChild);
    }
    el._o = { ctx };
    const html = items.map((f, i) => {
      const quip = f.quip ? '<div class="bs-quip">🏺 ' + ctx.esc(f.quip) + '</div>' : '';
      return '<div class="' + (i ? 'old' : 'new') + '">' + feedLine(ctx, f) + (i ? '' : rxLine(ctx, f)) + (i ? '' : quip) + '</div>';
    }).join('');
    setHtml(el.firstElementChild, html);
    // Реагувати може кожен, хто сидить за столом, — і той, чий флот уже на дні: йому саме є що робити.
    const rx = el.querySelector('.bs-react');
    const can = !!(ctx.mine && ctx.seat != null && v.boards && v.boards[ctx.seat]);
    if (rx.hidden === can) rx.hidden = !can;
  }

  /// Бульбашка реакції над полем того, хто відреагував: лише коли його лічильник виріс.
  function bubbles(root, ctx, v) {
    const st = state(root);
    const n = v.reactN || [], e = v.react || [];
    const seen = st.rxSeen;
    st.rxSeen = n.slice();
    if (!seen) return;
    for (let seat = 0; seat < 4; seat++) {
      if (!((n[seat] | 0) > (seen[seat] | 0))) continue;
      const key = seat === ctx.seat && ctx.mine ? 'me' : 's' + seat;
      const cap = root.querySelector('.bs-side[data-side="' + key + '"] > .bs-cap');
      const r = REACTS[e[seat] | 0];
      if (!cap || !r) continue;
      const side = cap.parentElement;
      const old = side.querySelector(':scope > .bs-rx');
      if (old) old.remove();
      const b = document.createElement('span');
      b.className = 'bs-rx';
      b.innerHTML = '<b>' + r[0] + '</b><small>' + r[1] + '</small>';
      side.appendChild(b);
      const t = setTimeout(() => { b.remove(); st.rxTimers = st.rxTimers.filter((x) => x !== t); }, 1900);
      st.rxTimers.push(t);
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Розстановка
  // ---------------------------------------------------------------------------------------------

  /// Тримає сервер у курсі: повний флот — 'place', неповний — 'clear', щоб таймер не повів у бій зі
  /// старим флотом, який людина вже розібрала.
  function sendPlan(s, g, ctx) {
    if (s.plan.length === g.fleet.length) {
      s.sent = true;
      ctx.act('place', { ships: s.plan.map((sh) => ({ cells: sh.cells })) });
      return;
    }
    if (!s.sent) return;
    s.sent = false;
    ctx.act('clear');
  }

  /// Клік по своєму полю у фазі розстановки: поставити обраний корабель або підняти той, що вже стоїть.
  function placeClick(root, ctx, g, cell) {
    const s = state(root);
    const at = s.plan.findIndex((sh) => sh.cells.includes(cell));
    if (at >= 0) {
      const ship = s.plan[at];
      // Однопалубний крутити нема як: для нього клік означає одразу «забрати назад», інакше корабель
      // «повернувся» б сам у себе і зняти його з поля не вийшло б нічим.
      const turned = ship.cells.length > 1
        ? fits(g, s.plan, ship.cells[0], ship.cells.length, !isHoriz(ship.cells), at)
        : null;
      if (turned) s.plan[at] = { cells: turned };
      else {
        // Повернути нема куди (або це однопалубний) — забираємо корабель назад у список.
        s.plan.splice(at, 1);
        s.pick = ship.cells.length;
        ctx.toast('Гоп — корабель знову в списку', '');
      }
    } else {
      const left = rest(g, s.plan);
      if (!left.length) return;
      const size = left.includes(s.pick) ? s.pick : left[0];
      const cells = fits(g, s.plan, cell, size, s.horiz, -1);
      if (!cells) { ctx.toast('Халепа: сюди він не стане — кораблі не торкаються навіть кутами', 'err'); return; }
      s.plan.push({ cells });
      const still = rest(g, s.plan);
      if (!still.includes(s.pick)) s.pick = still[0] || 0;
    }
    sendPlan(s, g, ctx);
    paint(root, ctx);
  }

  function dropTools(root) {
    const host = root.querySelector(':scope > .bs-tools');
    if (!host) return;
    const arc = host.querySelector('.garc');
    if (arc && arc._arc) arc._arc.stop();
    host.remove();
  }

  function tools(root, ctx, v, g) {
    const placing = v.phase === 'placing' && ctx.playing;
    const mine = placing && ctx.mine && v.me;
    if (!placing) { dropTools(root); return; }
    let host = root.querySelector(':scope > .bs-tools');
    if (!host) {
      host = document.createElement('div');
      host.className = 'bs-tools';
      root.appendChild(host);
    }
    const s = state(root);
    const ready = !!(v.me && v.me.ready);

    // Хто вже розставився — щоб не гадати, чого чекаємо.
    let crew = host.querySelector(':scope > .bs-crew');
    if (!crew) {
      crew = document.createElement('div');
      crew.className = 'bs-crew';
      host.appendChild(crew);
    }
    const boards = v.boards || [];
    const crewHtml = players(v).map((seat) => {
      const b = boards[seat] || {};
      return '<span class="bs-mate' + (b.ready ? ' ok' : '') + '">' + who(ctx, seat)
        + (b.ready ? ' ✓' : ' <i>розставляє…</i>') + '</span>';
    }).join('');
    setHtml(crew, crewHtml);

    if (mine) {
      const left = rest(g, s.plan);
      const pick = left.includes(s.pick) ? s.pick : left[0];
      // Список кораблів — платформена «рука»: клік обирає, повторний клік по обраному кладе його боком.
      const items = left.map((size) => ({ size, cls: !ready && size === pick ? 'sel' : '', disabled: ready }));
      HGames.ui.hand(host, items, {
        render: (it) => '<span class="bs-ship' + (s.horiz ? '' : ' vert') + '">'
          + new Array(it.size).fill('<i></i>').join('') + '</span>',
        onItem: (it) => {
          if (ready) return;
          if (it.size === s.pick) s.horiz = !s.horiz;
          else s.pick = it.size;
          paint(root, ctx);
        },
      });

      let acts = host.querySelector(':scope > .bs-acts');
      if (!acts) {
        acts = document.createElement('div');
        acts.className = 'bs-acts';
        acts.innerHTML = '<button type="button" data-bs="random">🎲 Випадково</button>'
          + '<button type="button" data-bs="turn" class="ghost" title="Повернути корабель, що ставиш (клавіша R)">↻ Боком</button>'
          + '<button type="button" data-bs="clear" class="ghost">Скинути</button>'
          + '<button type="button" data-bs="ready" class="primary" data-pad-first>Готово</button>';
        acts.addEventListener('click', (e) => {
          const b = e.target.closest('[data-bs]');
          if (!b || b.disabled) return;
          const cur = state(root), o = acts._o || {};
          if (b.dataset.bs === 'random') { cur.adopt = true; o.ctx.act('random'); }
          if (b.dataset.bs === 'turn') { cur.horiz = !cur.horiz; paint(root, o.ctx); }
          if (b.dataset.bs === 'clear') {
            // adopt знімаємо: інакше найближче ж малювання наллє план назад із серверного виду.
            cur.plan = [];
            cur.pick = o.g.fleet[0];
            cur.adopt = false;
            sendPlan(cur, o.g, o.ctx);
            paint(root, o.ctx);
          }
          if (b.dataset.bs === 'ready') o.ctx.act('ready');
        });
        host.insertBefore(acts, host.firstChild);
      }
      acts._o = { ctx, g };
      const full = s.plan.length === g.fleet.length;
      acts.querySelectorAll('[data-bs]').forEach((b) => {
        const off = ready || (b.dataset.bs === 'ready' && !full) || (b.dataset.bs === 'turn' && !rest(g, s.plan).length);
        if (b.disabled !== off) b.disabled = off;
      });
    }

    // Дуга-таймер каркаса сама крутиться на rAF; нам лишається дати їй кінець фази.
    if (v.placeUntil) HGames.ui.timerArc(host, v.placeUntil, (v.placeSeconds || 120) * 1000);
  }

  /// Смуга бою: чий хід і дуга, скільки лишилось думати. Удвох — те саме, лише без підказки «по будь-кому».
  function battleBar(root, ctx, v) {
    let el = root.querySelector(':scope > .bs-bar');
    if (v.phase !== 'battle' || !ctx.playing) {
      if (el) { const arc = el.querySelector('.garc'); if (arc && arc._arc) arc._arc.stop(); el.remove(); }
      return;
    }
    if (!el) {
      el = document.createElement('div');
      el.className = 'bs-bar';
      el.innerHTML = '<span class="bs-say"></span>';
      root.appendChild(el);
    }
    const many = players(v).length > 2;
    const meOut = ctx.mine && v.boards && v.boards[ctx.seat] && v.boards[ctx.seat].out;
    const rv = v.revenge;
    // «Твій хід» / «Ходить …» каркас пише сам у статусі — тут лише те, чого він не знає.
    let say = '';
    if (rv && rv.by === ctx.seat && ctx.mine) say = '💀 Остання помста: один постріл по полю ' + who(ctx, rv.on, true);
    else if (rv) say = '💀 ' + who(ctx, rv.by, true) + ' мститься: останній постріл по ' + who(ctx, rv.on);
    else if (meOut) say = '☠️ Твій флот на дні. Дивись, хто кого';
    else if (ctx.myTurn && many) say = '🎯 Тисни клітинку на будь-якому чужому полі';
    // Удвох «Твій хід» пише каркас під карткою, але на телефоні смуга липне до низу екрана, а статус — ні.
    else if (ctx.myTurn) say = '<span class="bs-narrow">🎯 Твій постріл</span>';
    else if (!ctx.myTurn) say = '⏳ ' + who(ctx, v.turn, true) + ' цілиться';
    const sayEl = el.querySelector('.bs-say');
    setHtml(sayEl, say);
    el.classList.toggle('mine', !!ctx.myTurn && (!meOut || !!(rv && rv.by === ctx.seat)));
    if (v.turnUntil) HGames.ui.timerArc(el, v.turnUntil, (v.turnSeconds || 40) * 1000);
  }

  /// Підсумок партії: місця, потоплені кораблі й влучність. Короткий, але з ним є про що поговорити.
  function summary(root, ctx, v) {
    let el = root.querySelector(':scope > .bs-sum');
    const r = v.result;
    if (v.phase !== 'done' || !r) { if (el) el.remove(); return; }
    if (!el) {
      el = document.createElement('div');
      el.className = 'bs-sum';
      root.appendChild(el);
    }
    const medals = ['🥇', '🥈', '🥉', '4.'];
    const rows = (r.places || [r.winner]).map((seat, i) => {
      const shots = (r.shots || [])[seat] || 0, hits = (r.hits || [])[seat] || 0, sank = (r.sank || [])[seat] || 0;
      const pct = shots ? Math.round(hits * 100 / shots) : 0;
      return '<tr' + (seat === ctx.seat ? ' class="me"' : '') + '><td>' + medals[i] + '</td><td>' + who(ctx, seat) + '</td>'
        + '<td>🔥 ' + sank + '</td><td>🎯 ' + hits + '/' + shots + ' <i>' + pct + '%</i></td></tr>';
    }).join('');
    const led = v.arsenal && v.arsenal.ledger && ctx.mine ? v.arsenal.ledger[ctx.seat] : null;
    const money = led ? '<div class="bs-coins">⚓ Шеляги: витрачено 🪙 ' + led[0] + ', за бій 🪙 ' + led[1]
      + ' → у гаманці <b>🪙 ' + led[2] + '</b>'
      + (led[3] > 0 ? '<div class="muted small">🧺 На сьогодні досить: більше 🪙 120 за день бої не платять — завтра знову</div>' : '')
      + '</div>' : '';
    const html = '<table><thead><tr><th></th><th>капітан</th><th title="Скільки чужих кораблів пущено на дно">потоплено</th>'
      + '<th title="Влучань із пострілів">влучність</th></tr></thead><tbody>' + rows + '</tbody></table>' + money;
    setHtml(el, html);
  }

  // ---------------------------------------------------------------------------------------------
  // Малювання картки
  // ---------------------------------------------------------------------------------------------

  function lobby(root, ctx, v, g) {
    let el = root.querySelector(':scope > .bs-lobby');
    if (!el) {
      el = document.createElement('div');
      el.className = 'bs-lobby';
      root.insertBefore(el, root.firstChild);
    }
    const html = '<b>⚓ ' + (g.key === 'quick' ? 'Швидке море 8×8, шість кораблів' : 'Класичне море 10×10, десять кораблів') + '</b>'
      + '<span>Удвох — дуель. Утрьох-учетверох — кожен проти кожного: б\'єш по кому хочеш, чий флот на дні — дивиться далі, останній на плаву виграв.</span>'
      + (v.arsenal ? '<span>⚓ <b>Арсенал:</b> на розстановці купуєш за 🪙 шеляги до трьох штук — радар, бомбу, торпеду, літак, міну чи ремонт — і в бою пускаєш замість пострілу. Хто переміг ощадливо, той отримає більше; за день — не більше 🪙 120.</span>' : '')
      + (v.bots ? '<span>🤖 На порожні місця підсяде Глек (' + v.bots + '): добиває підбите, шукає шаховим візерунком. Черепків за перемогу над ним нема.</span>' : '')
      + '<span class="muted">Від двох до чотирьох капітанів (разом із Глеками); починає господар кнопкою «Почати». Утрьох-учетверох потоплений має останній постріл по кривднику.</span>';
    setHtml(el, html);
  }

  function paint(root, ctx) {
    const s = state(root);
    const v = ctx.view || {};
    const g = seaOf(v);
    ctx._bsRoot = root;                   // onKey бачить лише ctx, а розстановка живе на картці
    const status = (ctx.room && ctx.room.status) || 'playing';
    // Стіл у лобі (зокрема після «дограли й хтось підсів») — жодних полів, лише що тут буде.
    const inLobby = status === 'lobby' || v.phase === 'lobby';
    const phase = inLobby ? 'lobby' : (v.phase || 'placing');
    if (s.phase !== phase) { s.phase = phase; s.pending.clear(); }
    // «Ще раз» — це нова партія: старий флот у браузері треба забути, інакше він так і висітиме
    // на полі, хоч сервер про нього вже нічого не знає. Те саме — інше море.
    const round = (ctx.room && ctx.room.round) || 1;
    if (s.round !== round || s.sea !== g.key) {
      s.round = round;
      s.sea = g.key;
      resetPlan(s, g);
    }

    // .bs міряє сам себе (container-type), а поля лягають у сітку .bs-seas усередині: так кількість
    // колонок залежить від ширини картки, а не вікна.
    let wrap = root.querySelector(':scope > .bs');
    if (!wrap) {
      wrap = document.createElement('div');
      wrap.className = 'bs';
      wrap.innerHTML = '<div class="bs-seas"></div>';
      root.insertBefore(wrap, root.firstChild);
    }
    const host = wrap.firstElementChild;

    if (inLobby) {
      prune(host, new Set());
      lobby(root, ctx, v, g);
      feed(root, ctx, {});
      tools(root, ctx, {}, g);
      battleBar(root, ctx, {});
      summary(root, ctx, {});
      return;
    }
    const lob = root.querySelector(':scope > .bs-lobby');
    if (lob) lob.remove();

    const seats = players(v);
    const boards = v.boards || [];
    BOTS = boards.map((b) => (b && b.bot) || null);
    const knownOf = (seat) => boards[seat] || (seat !== ctx.seat ? v.enemy : null) || {};
    const iPlay = ctx.mine && !!v.me;
    const keep = new Set();
    host.className = 'bs-seas n' + (phase === 'placing' ? (iPlay ? 1 : seats.length) : seats.length) + ' ' + phase;

    // Анімація останнього пострілу: лише коли він справді новий, а не на кожен вид.
    const last = (v.feed || [])[(v.feed || []).length - 1];
    const lastKey = last ? last.by + ':' + last.at + ':' + last.cell + ':' + (v.shots || 0) : '';
    const fresh = lastKey && lastKey !== s.fx && !reduced();
    s.fx = lastKey;
    const hitCells = last && last.tool !== 'radar' && last.tool !== 'repair' ? (last.cells || [last.cell]) : [];
    const fxOf = (seat, i) => (last && last.at === seat && hitCells.includes(i) ? (fresh ? ' last fx ' + last.res : ' last') : '');
    SEA = g;

    if (iPlay) {
      const me = v.me || {};
      // У розстановці на своєму полі показуємо те, що людина совгає зараз; далі — те, що прийняв сервер.
      if (phase === 'placing' && s.adopt && (me.ships || []).length) {
        s.plan = me.ships.map((cells) => ({ cells: cells.slice() }));
        s.adopt = false;
        s.sent = true;
      }
      const ships = phase === 'placing' ? s.plan.map((sh) => sh.cells) : (me.ships || []);
      const mine = myMarks(me, ships);
      // Кораблі можна совгати, лише поки не сказано «Готово».
      const editable = phase === 'placing' && ctx.playing && !me.ready;
      const left = rest(g, s.plan);
      const pick = left.includes(s.pick) ? s.pick : left[0];
      const no = editable ? blocked(g, s.plan, -1) : null;
      const free = (i) => {
        if (!pick) return false;
        const cells = span(g, i, pick, s.horiz);
        return !!cells && !cells.some((c) => no.has(c));
      };
      const sunkMine = ships.filter((sh) => sh.every((c) => mine.get(c) === 'ship sunk')).length;
      const out = boards[ctx.seat] && boards[ctx.seat].out;
      const cap = phase === 'placing'
        ? 'Моє поле <b>' + ships.length + '</b> з ' + g.fleet.length
        : who(ctx, ctx.seat) + ' · моє поле ' + (out ? '<b class="dead">на дні</b>' : '<b>' + (ships.length - sunkMine) + '</b>');
      const meSide = ensureSide(host, 'me', cap, g,
        's' + ctx.seat + ' mine' + (v.turn === ctx.seat && phase === 'battle' ? ' turn' : '') + (out ? ' out' : ''));
      keep.add('me');
      // Ремонт: у свій хід тиснеш підбиту палубу свого ще живого корабля.
      const fixing = phase === 'battle' && s.arm === 'repair' && ctx.myTurn && !v.revenge && !out;
      const fixable = (i) => mine.get(i) === 'ship hit';
      paintGrid(meSide, g, (i) => (mine.get(i) || '') + fxOf(ctx.seat, i) + (fixing && fixable(i) ? ' fix' : ''),
        editable ? (cell) => placeClick(root, ctx, g, cell)
          : fixing ? (cell) => { s.arm = ''; ctx.act('use', { item: 'repair', cell, at: ctx.seat }); paint(root, ctx); } : null,
        (i) => (editable && (mine.has(i) || free(i))) || (fixing && fixable(i)));
      const grid = meSide.querySelector(':scope > .bs-grid');
      grid._onHover = editable ? (i) => preview(grid, g, s, pick, i) : null;
      // paintGrid щойно переписав класи — тінь під мишею, що стоїть на місці, треба покласти знову.
      preview(grid, g, s, editable ? pick : 0, editable ? grid._hoverAt : -1);
    }

    // Чужі поля: у розстановці їх не малюємо (там нема на що дивитись), лише список, хто готовий.
    if (phase !== 'placing' || !iPlay) {
      const meOut = iPlay && boards[ctx.seat] && boards[ctx.seat].out;
      // Постріли, на які вже прийшла відповідь, знімаємо з «польоту» ДО малювання: інакше після влучання
      // (хід лишається в мене, нового виду не буде) поля так і стояли б замкнені до годинника.
      // Штука з трюму може й не зачепити клітинку, яку тиснули (літак збили раніше, радар не стріляє), —
      // тож її «політ» знімаємо, щойно в стрічці з'явився новий запис.
      if (s.toolKey != null && s.toolKey !== lastKey) { s.pending.clear(); s.toolKey = null; }
      for (const k of [...s.pending]) {
        const [seat, cell] = k.split(':').map(Number);
        const known = knownOf(seat);
        if ((known.hits || []).includes(cell) || (known.misses || []).includes(cell)) s.pending.delete(k);
      }
      for (const seat of seats) {
        if (iPlay && seat === ctx.seat) continue;
        const known = knownOf(seat);
        const key = 's' + seat;
        keep.add(key);
        const marks = knownMarks(known);
        const dead = !!known.out;
        const rv = v.revenge;
        const canShoot = phase === 'battle' && ctx.myTurn && iPlay && !dead
          && (rv ? rv.by === ctx.seat && rv.on === seat : !meOut);
        const cap = who(ctx, seat) + ' ' + (dead ? '<b class="dead">на дні</b>'
          : '<b title="Кораблів на плаву">🚢 ' + (known.left == null ? '' : known.left) + '</b>');
        const side = ensureSide(host, key, cap, g,
          key + (v.turn === seat && phase === 'battle' ? ' turn' : '') + (dead ? ' out' : '') + (canShoot ? ' aim' : '')
            + (known.bot ? ' bot' : ''));
        const arm = canShoot && !rv && s.arm && s.arm !== 'repair' ? s.arm : '';
        const intel = new Map();
        for (const it of ((v.arsenal && v.arsenal.intel) || [])) if (it[0] === seat) intel.set(it[1], String(it[2]));
        paintGrid(side, g,
          (i) => (marks.get(i) || (s.pending.has(seat + ':' + i) ? 'wait' : '')) + fxOf(seat, i) + (intel.has(i) ? ' rd' : ''),
          canShoot ? (cell) => {
            s.pending.add(seat + ':' + cell);
            // Види в цієї гри приходять з тиком, тож до відповіді сервера тримаємо клітинку «в польоті».
            const go = arm ? ctx.act('use', { item: arm, cell, at: seat }) : ctx.act('shoot', { cell, at: seat });
            if (arm) { s.arm = ''; s.toolKey = lastKey; }
            go.then((r) => {
              if (r && r.ok) return;
              s.pending.delete(seat + ':' + cell);
              s.toolKey = null;
              paint(root, ctx);
            });
            paint(root, ctx);
          } : null,
          // Поки постріл у польоті, усі поля замкнені: вид (а з ним і ctx.myTurn) прийде аж із тиком, тож
          // інакше другий клік поспіль летів би на сервер і повертався червоним «Зараз не твій хід».
          // Штука з арсеналу б'є рядком/стовпцем/хрестом, тож цілитись нею можна й в обстріляну клітинку.
          (i) => canShoot && !s.pending.size && (!marks.has(i) || (!!arm && arm !== 'radar')),
          (i) => intel.get(i) || '');
        side.classList.toggle('armed', !!arm);
      }
    }
    prune(host, keep);

    feed(root, ctx, v);
    tools(root, ctx, v, g);
    shop(root, ctx, v);
    battleBar(root, ctx, v);
    armory(root, ctx, v);
    summary(root, ctx, v);
    bubbles(root, ctx, v);
    if (fresh && last && last.cells && (last.tool === 'plane' || last.tool === 'torpedo')) {
      const key = iPlay && last.at === ctx.seat ? 'me' : 's' + last.at;
      const side = host.querySelector(':scope > .bs-side[data-side="' + key + '"]');
      if (side) fly(side, last);
    }
  }

  // ---------------------------------------------------------------------------------------------
  // ⚓ Арсенал: крамниця на розстановці, штуки в бою, політ літака й торпеди
  // ---------------------------------------------------------------------------------------------

  const itemsOf = (v) => (v.arsenal && v.arsenal.items) || [];

  function shop(root, ctx, v) {
    let el = root.querySelector(':scope > .bs-shop');
    const a = v.arsenal;
    const show = a && v.phase === 'placing' && ctx.playing && ctx.mine && v.me;
    if (!show) { if (el) el.remove(); return; }
    if (!el) {
      el = document.createElement('div');
      el.className = 'bs-shop';
      el.addEventListener('click', (e) => {
        const b = e.target.closest('[data-buy],[data-sell]');
        const o = el._o;
        if (!b || b.disabled || !o) return;
        if (b.dataset.buy) o.ctx.act('buy', { item: b.dataset.buy });
        else o.ctx.act('sell', { item: b.dataset.sell });
      });
      const tools = root.querySelector(':scope > .bs-tools');
      root.insertBefore(el, tools ? tools.nextSibling : null);
    }
    el._o = { ctx };
    const bought = a.bought || {};
    const count = Object.values(bought).reduce((x, y) => x + y, 0);
    const cost = itemsOf(v).reduce((x, it) => x + it.price * (bought[it.key] || 0), 0);
    const ready = !!v.me.ready;
    const purse = a.purse | 0;
    const cards = itemsOf(v).map((it) => {
      const n = bought[it.key] || 0;
      const can = !ready && n < it.max && count < a.hold && cost + it.price <= purse;
      return '<div class="bs-it' + (n ? ' got' : '') + '">'
        + '<button type="button" data-buy="' + it.key + '"' + (can ? '' : ' disabled') + ' title="' + ctx.esc(it.text) + '">'
        + '<span class="bs-ico">' + it.icon + '</span><span class="bs-nm">' + it.name + '</span><b>🪙 ' + it.price + '</b></button>'
        + (n ? '<button type="button" class="ghost bs-sell" data-sell="' + it.key + '"' + (ready ? ' disabled' : '')
          + ' title="Повернути в крамницю">×' + n + ' ✕</button>' : '')
        + '<small>' + ctx.esc(it.text) + (it.max > 1 ? ' (до ' + it.max + ')' : '') + '</small></div>';
    }).join('');
    const pay = a.pay || { base: 10, win: 10, cap: 30 };
    const html = '<div class="bs-shop-h"><b>⚓ Арсенал</b> · у гаманці <b>🪙 ' + purse + '</b> шелягів · у трюмі <b>' + count + '/' + a.hold + '</b>'
      + (cost ? ' · беру на 🪙 ' + cost : '') + '</div>'
      + '<div class="bs-its">' + cards + '</div>'
      + '<div class="bs-shop-f">Платиш лише за те, що пустиш у хід. За бій — 🪙 ' + pay.base + ' кожному, переможцю ще '
      + pay.win + ' і скільки лишилось від ' + pay.cap + ' після витраченого: перемога без арсеналу — найщедріша.</div>';
    setHtml(el, html);
  }

  function armory(root, ctx, v) {
    let el = root.querySelector(':scope > .bs-arm');
    const a = v.arsenal;
    const s = state(root);
    const meOut = ctx.mine && v.boards && v.boards[ctx.seat] && v.boards[ctx.seat].out;
    const show = a && v.phase === 'battle' && ctx.playing && ctx.mine && v.me && !meOut;
    if (!show) { if (el) el.remove(); s.arm = ''; return; }
    const left = itemsOf(v).filter((it) => it.target !== 'none')
      .map((it) => ({ it, n: ((a.bought || {})[it.key] || 0) - ((a.used || {})[it.key] || 0) }))
      .filter((x) => x.n > 0);
    const mines = (v.me.mines || []).length;
    if (!left.length && !mines) { if (el) el.remove(); s.arm = ''; return; }
    if (!el) {
      el = document.createElement('div');
      el.className = 'bs-arm';
      el.addEventListener('click', (e) => {
        const b = e.target.closest('[data-arm]');
        const o = el._o;
        if (!b || b.disabled || !o) return;
        const st = state(root);
        st.arm = st.arm === b.dataset.arm ? '' : b.dataset.arm;
        paint(root, o.ctx);
      });
      const bar = root.querySelector(':scope > .bs-bar');
      root.insertBefore(el, bar ? bar.nextSibling : null);
    }
    el._o = { ctx };
    const can = ctx.myTurn && !v.revenge;
    if (!can) s.arm = '';
    if (s.arm && !left.some((x) => x.it.key === s.arm)) s.arm = '';
    const cur = left.find((x) => x.it.key === s.arm);
    const HINT = {
      plane: 'тисни клітинку чужого поля — літак полетить її рядком від ближчого краю',
      torpedo: 'тисни клітинку чужого поля — торпеда піде її стовпцем від ближчого краю',
      bomb: 'тисни клітинку чужого поля — бомба вдарить хрестом',
      radar: 'тисни клітинку чужого поля — радар порахує цілі палуби в квадраті 3×3',
      repair: 'тисни підбиту палубу на своєму полі',
    };
    const html = left.map((x) => '<button type="button" class="' + (x.it.key === s.arm ? 'primary' : 'ghost') + '" data-arm="' + x.it.key + '"'
      + (can ? '' : ' disabled') + ' title="' + ctx.esc(x.it.text) + '">' + x.it.icon + ' ' + x.it.name + (x.n > 1 ? ' ×' + x.n : '') + '</button>').join('')
      + (mines ? '<span class="bs-mines" title="Міни на твоїй воді: хто в них стрельне — отримає рикошет">🧨 ×' + mines + '</span>' : '')
      + '<span class="bs-arm-h">' + (cur ? cur.it.icon + ' ' + HINT[cur.it.key] + ' · ще раз — скасувати'
        : can && left.length ? 'Замість пострілу можна пустити штуку з трюму' : '') + '</span>';
    setHtml(el, html);
  }

  /// Літак чи торпеда пролітає своїм шляхом над полем — від першої клітинки до тієї, де вибухнув.
  function fly(side, f) {
    const grid = side.querySelector(':scope > .bs-grid');
    const cells = f.cells || [];
    if (!grid || !cells.length || !grid.animate) return;
    const a = grid._cells[cells[0]], b = grid._cells[cells[cells.length - 1]];
    if (!a || !b) return;
    const sp = document.createElement('span');
    sp.className = 'bs-fly';
    sp.textContent = f.tool === 'plane' ? '✈️' : '🚀';
    const x0 = a.offsetLeft + a.offsetWidth / 2, y0 = a.offsetTop + a.offsetHeight / 2;
    const x1 = b.offsetLeft + b.offsetWidth / 2, y1 = b.offsetTop + b.offsetHeight / 2;
    // Емодзі дивиться на північний схід (−45°): повертаємо його туди, куди летить.
    const deg = (x1 === x0 && y1 === y0) ? 0 : Math.atan2(y1 - y0, x1 - x0) * 180 / Math.PI + 45;
    const dx = x1 - x0, dy = y1 - y0;
    // Стартуємо на клітинку раніше, щоб було видно, звідки заходить.
    const len = Math.max(1, Math.hypot(dx, dy)), ux = dx / len, uy = dy / len, step = a.offsetWidth || 24;
    const from = 'translate(' + (x0 - ux * step) + 'px,' + (y0 - uy * step) + 'px) translate(-50%,-50%) rotate(' + deg + 'deg)';
    const to = 'translate(' + x1 + 'px,' + y1 + 'px) translate(-50%,-50%) rotate(' + deg + 'deg)';
    grid.appendChild(sp);
    const anim = sp.animate([{ transform: from, opacity: 0.2 }, { transform: from, opacity: 1, offset: 0.08 }, { transform: to, opacity: 1, offset: 0.92 }, { transform: to, opacity: 0 }],
      { duration: Math.max(420, 110 * cells.length + 200), easing: 'linear' });
    anim.onfinish = () => sp.remove();
  }

  HGames.register({
    id: 'battleship',
    icon: ICON,
    seatNames: ['синій', 'червоний', 'зелений', 'жовтий'],
    // Свої класи чіпів (battleship.css): колір збігається з назвою місця, чого x/o/c/d каркаса не дають.
    seatClass: ['bsc0', 'bsc1', 'bsc2', 'bsc3'],

    mount(root, ctx) { paint(root, ctx); },
    update(root, ctx) { paint(root, ctx); },

    /// Кадр несе лічильники й відлік — оновлюємо тільки підписи «на плаву», поля з нього не малюються.
    frame(root, ctx, f) {
      if (!f || !f.left || f.phase !== 'battle') return;
      const host = root.querySelector(':scope > .bs > .bs-seas');
      if (!host) return;
      host.querySelectorAll('.bs-side[data-side^="s"] .bs-cap b[title]').forEach((b) => {
        const seat = +b.closest('.bs-side').dataset.side.slice(1);
        const t = '🚢 ' + f.left[seat];
        if (b.textContent === t) return;
        b.textContent = t;
        b.closest('.bs-cap')._h = null;   // підпис уже не той, що клав вид, — наступний вид перепише його чесно
      });
    },

    /// R — повернути корабель, що ставиш (те саме, що «↻ Боком»), поки розстановка не скінчилась.
    onKey(e, ctx) {
      const root = ctx._bsRoot, v = ctx.view || {};
      if (e.code !== 'KeyR' || e.ctrlKey || e.metaKey || e.altKey || !root || !root._bs) return false;
      if (!ctx.playing || !ctx.mine || v.phase !== 'placing' || !v.me || v.me.ready) return false;
      const t = e.target;
      if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable)) return false;
      root._bs.horiz = !root._bs.horiz;
      paint(root, ctx);
      return true;
    },

    status(ctx) {
      const room = ctx.room || {};
      if (room.status === 'lobby') {
        // MinPlayers = 1 заради «Глек підсідає», тож каркас сам-на-сам каже «Можна рушати», а старт
        // без Глека відмовить. Поки людей менше двох і Глека не кликали, кажемо, як є.
        const bots = +((room.options && room.options.bots) || 0);
        let people = 0;
        for (let i = 0; i < 4; i++) if (ctx.nickOf(i)) people++;
        return people < 2 && !bots ? 'Чекаємо, хто підсяде (або відкрий стіл з «🤖 Глек підсідає»)' : '';
      }
      if (!ctx.playing) return '';
      // фаза й відлік розстановки живуть у кадрах, а не у видах — беремо свіжіше
      const f = (ctx.frame && ctx.frame.phase) ? ctx.frame : (ctx.view || {});
      const v = ctx.view || {};
      if (f.phase === 'battle' && v.phase === 'battle') {
        // Місце Глека в каркасі порожнє — «Ходить …» він би не назвав; помсту вибулого — теж.
        const b = v.boards && v.boards[v.turn];
        if (v.revenge) return v.revenge.by === ctx.seat ? '💀 Твоя остання помста' : '💀 Остання помста: ' + nameOf(ctx, v.revenge.by);
        if (b && b.bot) return 'Цілиться ' + b.bot;
      }
      if (f.phase !== 'placing') return '';   // у бою «Твій хід» каркас напише сам із view.turn
      if (ctx.seat == null) return 'Розставляють кораблі';
      const ready = !!(ctx.view && ctx.view.me && ctx.view.me.ready);
      const left = f.placeLeft;
      return (ready ? 'Чекаю на решту' : 'Розстав кораблі, тоді «Готово»')
        + (left == null ? '' : ' · ' + left + ' с');
    },

    unmount(root) {
      root.querySelectorAll('.garc').forEach((arc) => { if (arc._arc) arc._arc.stop(); });
      if (root._bs) { root._bs.rxTimers.forEach(clearTimeout); clearTimeout(root._bs.rxTimer); }
      root._bs = null;
    },

    news: {
      v: '2026-09-29',
      title: 'Морський бій: Арсенал, Глек за столом і остання помста',
      items: [
        '⚓ Новий режим «Арсенал»: за 🪙 шеляги — радар, бомба, торпеда, літак, що бомбить рядок до першого корабля, міна й ремонт',
        '🤖 «Глек підсідає» — бот на порожнє місце: добиває підбите й шукає шаховим візерунком',
        '💀 Утрьох-учетверох потоплений має останній постріл по тому, хто його потопив',
        '😱 😂 🎯 Реакції на постріл, слово Глека на кожне потоплення, годинник ходу 20/40/60 с',
        '🔕 Тости — лише на потоплення й перемогу: поле більше не засипає «Бульк — мимо»',
      ],
    },
  });
})();
