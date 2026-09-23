/*
  Морський бій на 2–4. Удвох — класика: своє поле і чуже. Утрьох і вчотирьох — кожен проти кожного:
  своє поле і стільки чужих, скільки суперників; у свій хід б'єш по будь-якому живому.

  Вид (Impl/Battleship.cs):
    { phase: 'lobby'|'placing'|'battle'|'done', turn, placeUntil, turnUntil, placeSeconds, turnSeconds,
      sea: { key, w, h, fleet }, players: [місця в грі],
      me: { ships, ready, hits, misses } | null,
      enemy: known,                              // старе «чуже поле» на двох
      boards: [known | null] × 4,                // публічне знання про кожне поле; null — місце не грає
      feed: [{ by, at, cell, res: 'miss'|'hit'|'sunk'|'out'|'left', size, auto }],
      shots, result: { winner, shots[], hits[], sank[], places[] } | null }
    known = { hits, misses, sunk, ready, left, out, reveal }   // reveal — увесь флот, лише після кінця
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
    if (cap.innerHTML !== caption) cap.innerHTML = caption;
    ensureGrid(side, g);
    return side;
  }

  /// Прибрати поля, яких у цьому виді вже нема (хтось устав, фаза змінилась).
  function prune(host, keep) {
    host.querySelectorAll(':scope > .bs-side').forEach((el) => { if (!keep.has(el.dataset.side)) el.remove(); });
  }

  function paintGrid(side, g, cls, onCell, can) {
    const grid = ensureGrid(side, g);
    grid._onCell = onCell || null;
    for (let i = 0; i < g.w * g.h; i++) {
      const b = grid._cells[i];
      const c = cls(i);
      const want = 'bs-cell' + (c ? ' ' + c : '');
      if (b.className !== want) b.className = want;
      const dis = !can || !can(i);
      if (b.disabled !== dis) b.disabled = dis;
    }
  }

  /// Мітки чужого поля: промах, влучання, потоплений; після кінця — ще й кораблі, що вціліли.
  function knownMarks(known) {
    const m = new Map();
    for (const ship of (known && known.reveal) || []) for (const c of ship) m.set(c, 'ghost');
    for (const c of (known && known.misses) || []) m.set(c, 'miss');
    for (const c of (known && known.hits) || []) m.set(c, 'hit');
    for (const ship of (known && known.sunk) || []) for (const c of ship) m.set(c, 'sunk');
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
    return m;
  }

  // ---------------------------------------------------------------------------------------------
  // Люди й події
  // ---------------------------------------------------------------------------------------------

  const players = (v) => (Array.isArray(v.players) && v.players.length ? v.players : [0, 1]);

  function who(ctx, seat, bold) {
    const name = ctx.esc(ctx.nickOf(seat) || ctx.seatName(seat));
    return '<span class="bs-who s' + seat + '">' + MARK[seat] + ' ' + (bold ? '<b>' + name + '</b>' : name) + '</span>';
  }

  /// Один рядок стрічки: хто, по кому і що вийшло. Без дієслів у минулому часі — рід ніка ми не знаємо.
  function feedLine(ctx, f) {
    const auto = f.auto ? '⏰ ' : '';
    const by = who(ctx, f.by), at = who(ctx, f.at);
    const tail = f.auto ? ' <i>(гармата вистрілила сама)</i>' : '';
    switch (f.res) {
      case 'miss': return auto + '💦 ' + by + ' → ' + at + ': мимо' + tail;
      case 'hit': return auto + '🎯 ' + by + ' → ' + at + ': влучання!' + tail;
      case 'sunk': return auto + '🔥 ' + by + ' → ' + at + ': ' + (DECKS[f.size] || 'корабель') + ' на дні' + tail;
      case 'out': return auto + '☠️ ' + at + ': увесь флот на дні, останній постріл — ' + by + tail;
      case 'left': return '🚪 ' + by + ' — з-за столу, флот на дно';
      default: return '';
    }
  }

  function feed(root, ctx, v) {
    let el = root.querySelector(':scope > .bs-feed');
    const items = (v.feed || []).slice(-3).reverse();
    const show = (v.phase === 'battle' || v.phase === 'done') && items.length && players(v).length > 2;
    // Удвох стрічка — зайва балачка: усе видно на двох полях. У компанії без неї не зрозуміти, хто кого.
    if (!show) { if (el) el.remove(); return; }
    if (!el) {
      el = document.createElement('div');
      el.className = 'bs-feed';
      el.setAttribute('aria-live', 'polite');
      root.insertBefore(el, root.firstChild);
    }
    const html = items.map((f, i) => '<div class="' + (i ? 'old' : 'new') + '">' + feedLine(ctx, f) + '</div>').join('');
    if (el.innerHTML !== html) el.innerHTML = html;
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
        ctx.toast('Забрав корабель назад', '');
      }
    } else {
      const left = rest(g, s.plan);
      if (!left.length) return;
      const size = left.includes(s.pick) ? s.pick : left[0];
      const cells = fits(g, s.plan, cell, size, s.horiz, -1);
      if (!cells) { ctx.toast('Сюди він не стане: кораблі не торкаються навіть кутами', 'err'); return; }
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
    if (crew.innerHTML !== crewHtml) crew.innerHTML = crewHtml;

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
          + '<button type="button" data-bs="turn" class="ghost" title="Повернути корабель, що ставиш">↻ Боком</button>'
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
    // «Твій хід» / «Ходить …» каркас пише сам у статусі — тут лише те, чого він не знає.
    let say = '';
    if (meOut) say = '☠️ Твій флот на дні. Дивись, хто кого';
    else if (ctx.myTurn && many) say = '🎯 Тисни клітинку на будь-якому чужому полі';
    else if (!ctx.myTurn) say = '⏳ ' + who(ctx, v.turn, true) + ' цілиться';
    const sayEl = el.querySelector('.bs-say');
    if (sayEl.innerHTML !== say) sayEl.innerHTML = say;
    el.classList.toggle('mine', !!ctx.myTurn);
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
    const html = '<table><thead><tr><th></th><th>капітан</th><th title="Скільки кораблів потопив">потопив</th>'
      + '<th title="Влучань із пострілів">влучність</th></tr></thead><tbody>' + rows + '</tbody></table>';
    if (el.innerHTML !== html) el.innerHTML = html;
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
      + '<span class="muted">Від двох до чотирьох капітанів; починає господар кнопкою «Почати».</span>';
    if (el.innerHTML !== html) el.innerHTML = html;
  }

  function paint(root, ctx) {
    const s = state(root);
    const v = ctx.view || {};
    const g = seaOf(v);
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
    const knownOf = (seat) => boards[seat] || (seat !== ctx.seat ? v.enemy : null) || {};
    const iPlay = ctx.mine && !!v.me;
    const keep = new Set();
    host.className = 'bs-seas n' + (phase === 'placing' ? (iPlay ? 1 : seats.length) : seats.length) + ' ' + phase;

    // Анімація останнього пострілу: лише коли він справді новий, а не на кожен вид.
    const last = (v.feed || [])[(v.feed || []).length - 1];
    const lastKey = last ? last.by + ':' + last.at + ':' + last.cell + ':' + (v.shots || 0) : '';
    const fresh = lastKey && lastKey !== s.fx && !reduced();
    s.fx = lastKey;
    const fxOf = (seat, i) => (last && last.at === seat && last.cell === i ? (fresh ? ' last fx ' + last.res : ' last') : '');

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
      paintGrid(meSide, g, (i) => (mine.get(i) || '') + fxOf(ctx.seat, i),
        editable ? (cell) => placeClick(root, ctx, g, cell) : null,
        (i) => editable && (mine.has(i) || free(i)));
    }

    // Чужі поля: у розстановці їх не малюємо (там нема на що дивитись), лише список, хто готовий.
    if (phase !== 'placing' || !iPlay) {
      const meOut = iPlay && boards[ctx.seat] && boards[ctx.seat].out;
      // Постріли, на які вже прийшла відповідь, знімаємо з «польоту» ДО малювання: інакше після влучання
      // (хід лишається в мене, нового виду не буде) поля так і стояли б замкнені до годинника.
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
        const canShoot = phase === 'battle' && ctx.myTurn && iPlay && !meOut && !dead;
        const cap = who(ctx, seat) + ' ' + (dead ? '<b class="dead">на дні</b>'
          : '<b title="Кораблів на плаву">🚢 ' + (known.left == null ? '' : known.left) + '</b>');
        const side = ensureSide(host, key, cap, g,
          key + (v.turn === seat && phase === 'battle' ? ' turn' : '') + (dead ? ' out' : '') + (canShoot ? ' aim' : ''));
        paintGrid(side, g,
          (i) => (marks.get(i) || (s.pending.has(seat + ':' + i) ? 'wait' : '')) + fxOf(seat, i),
          canShoot ? (cell) => {
            s.pending.add(seat + ':' + cell);
            // Види в цієї гри приходять з тиком, тож до відповіді сервера тримаємо клітинку «в польоті».
            ctx.act('shoot', { cell, at: seat }).then((r) => {
              if (r && r.ok) return;
              s.pending.delete(seat + ':' + cell);
              paint(root, ctx);
            });
            paint(root, ctx);
          } : null,
          // Поки постріл у польоті, усі поля замкнені: вид (а з ним і ctx.myTurn) прийде аж із тиком, тож
          // інакше другий клік поспіль летів би на сервер і повертався червоним «Зараз не твій хід».
          (i) => canShoot && !s.pending.size && !marks.has(i));
      }
    }
    prune(host, keep);

    feed(root, ctx, v);
    tools(root, ctx, v, g);
    battleBar(root, ctx, v);
    summary(root, ctx, v);
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
        if (b.textContent !== t) b.textContent = t;
      });
    },

    status(ctx) {
      if (!ctx.playing) return '';
      // фаза й відлік розстановки живуть у кадрах, а не у видах — беремо свіжіше
      const f = (ctx.frame && ctx.frame.phase) ? ctx.frame : (ctx.view || {});
      if (f.phase !== 'placing') return '';   // у бою «Твій хід» каркас напише сам із view.turn
      if (ctx.seat == null) return 'Розставляють кораблі';
      const ready = !!(ctx.view && ctx.view.me && ctx.view.me.ready);
      const left = f.placeLeft;
      return (ready ? 'Чекаю на решту' : 'Розстав кораблі, тоді «Готово»')
        + (left == null ? '' : ' · ' + left + ' с');
    },

    unmount(root) {
      root.querySelectorAll('.garc').forEach((arc) => { if (arc._arc) arc._arc.stop(); });
      root._bs = null;
    },

    news: {
      v: '2026-09-24',
      title: 'Морський бій: тепер до чотирьох капітанів',
      items: [
        '⚓ За столом 2–4 гравці: утрьох і вчотирьох — кожен проти кожного, б\'єш по будь-якому чужому полю',
        '☠️ Флот на дні — ти вибув і дивишся далі; останній на плаву забирає перемогу',
        '⚡ Нове «Швидке море» 8×8 на шість кораблів — партія вдвічі коротша',
        '⏰ На постріл 40 секунд, далі гармата стріляє сама — ніхто не тримає стіл',
        '📜 Стрічка «хто кого», підсумок із влучністю, а наприкінці видно всі чужі кораблі',
        '🔧 Поле оновлюється одразу після пострілу, а не із секундною затримкою',
      ],
    },
  });
})();
