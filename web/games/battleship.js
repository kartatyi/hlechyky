/*
  Морський бій. Два поля поряд: своє (з кораблями) і чуже (самі влучання й промахи).

  Вид (Impl/Battleship.cs):
    { phase: 'placing'|'battle'|'done', turn, placeUntil,
      me: { ships, ready, hits, misses } | null,
      enemy: { hits, misses, sunk, ready, left },
      boards: [enemyLike, enemyLike] | null,   // тільки глядачеві
      shots, result }
  Кадр: { phase, turn, placeLeft, ready, left, shots, winner } — кораблів там нема й бути не може:
  кадр летить усім одразу, зокрема суперникові.

  Розстановка живе в браузері: поки гравець совгає кораблі, сервер про них не знає. На сервер іде
  готовий флот ('place'), і саме сервер вирішує, чи він законний — тут ті самі перевірки лише для
  того, щоб не сварити людину тостом на кожен другий клік.
*/
(() => {
  const W = 10, H = 10, CELLS = W * H;
  const FLEET = [4, 3, 3, 2, 2, 2, 1, 1, 1, 1];
  const COLS = 'abcdefghij';
  const PLACE_MS = 120000;

  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M1.6 9.6h12.8l-1.9 3.5a2 2 0 0 1-1.8 1H5.3a2 2 0 0 1-1.8-1L1.6 9.6Z" fill="var(--clay)"/>'
    + '<path d="M8 1.4v7.2M8 3 12 5 8 7" fill="none" stroke="var(--accent)" stroke-width="1.4" stroke-linejoin="round"/></svg>';

  // ---------------------------------------------------------------------------------------------
  // Геометрія: та сама, що на сервері, тільки коротша
  // ---------------------------------------------------------------------------------------------

  function halo(cell) {
    const x = cell % W, y = (cell / W) | 0, out = [];
    for (let dy = -1; dy <= 1; dy++) {
      for (let dx = -1; dx <= 1; dx++) {
        const nx = x + dx, ny = y + dy;
        if ((dx || dy) && nx >= 0 && nx < W && ny >= 0 && ny < H) out.push(ny * W + nx);
      }
    }
    return out;
  }

  /// Куди новий корабель ставити не можна: чужі клітинки і все, що до них тулиться.
  function blocked(plan, skip) {
    const no = new Set();
    plan.forEach((ship, i) => {
      if (i === skip) return;
      for (const c of ship.cells) { no.add(c); for (const n of halo(c)) no.add(n); }
    });
    return no;
  }

  /// Клітинки корабля від anchor; null — не влазить у поле.
  function span(anchor, size, horiz) {
    const x = anchor % W, y = (anchor / W) | 0;
    if (horiz ? x + size > W : y + size > H) return null;
    const cells = [];
    for (let i = 0; i < size; i++) cells.push(horiz ? y * W + x + i : (y + i) * W + x);
    return cells;
  }

  function fits(plan, anchor, size, horiz, skip) {
    const cells = span(anchor, size, horiz);
    if (!cells) return null;
    const no = blocked(plan, skip);
    return cells.some((c) => no.has(c)) ? null : cells;
  }

  const isHoriz = (cells) => cells.length < 2 || cells[1] === cells[0] + 1;

  /// Скільки кораблів ще не поставлено — по одному розміру на штуку.
  function rest(plan) {
    const left = FLEET.slice();
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
        pending: new Set(), // клітинки, куди постріл уже полетів, а відповідь ще ні
        phase: '',
        round: 0,
      };
    }
    return root._bs;
  }

  // ---------------------------------------------------------------------------------------------
  // Поле
  // ---------------------------------------------------------------------------------------------

  function ensureGrid(side) {
    let g = side.querySelector(':scope > .bs-grid');
    if (g) return g;
    g = document.createElement('div');
    g.className = 'bs-grid';
    let html = '<span class="bs-lab"></span>';
    for (let x = 0; x < W; x++) html += '<span class="bs-lab">' + COLS[x] + '</span>';
    for (let y = 0; y < H; y++) {
      html += '<span class="bs-lab">' + (y + 1) + '</span>';
      for (let x = 0; x < W; x++) {
        const i = y * W + x;
        html += '<button type="button" class="bs-cell" data-i="' + i + '" aria-label="' + COLS[x] + (y + 1) + '"></button>';
      }
    }
    g.innerHTML = html;
    // Слухач вішається раз, а колбек модуль дає новий на кожен update — тримаємо свіжий на елементі.
    g.addEventListener('click', (e) => {
      const b = e.target.closest('.bs-cell');
      if (b && !b.disabled && g._onCell) g._onCell(+b.dataset.i);
    });
    g._cells = [...g.querySelectorAll('.bs-cell')];
    side.appendChild(g);
    return g;
  }

  function ensureSide(host, key, caption) {
    let side = host.querySelector(':scope > .bs-side[data-side="' + key + '"]');
    if (!side) {
      side = document.createElement('div');
      side.className = 'bs-side';
      side.dataset.side = key;
      side.innerHTML = '<div class="bs-cap"></div>';
      host.appendChild(side);
    }
    const cap = side.querySelector(':scope > .bs-cap');
    if (cap.innerHTML !== caption) cap.innerHTML = caption;
    ensureGrid(side);
    return side;
  }

  function paintGrid(side, cls, onCell, can) {
    const g = ensureGrid(side);
    g._onCell = onCell || null;
    for (let i = 0; i < CELLS; i++) {
      const b = g._cells[i];
      const want = 'bs-cell' + (cls(i) ? ' ' + cls(i) : '');
      if (b.className !== want) b.className = want;
      const dis = !can || !can(i);
      if (b.disabled !== dis) b.disabled = dis;
    }
  }

  /// Мітки чужого поля: промах, влучання, потоплений.
  function knownMarks(known) {
    const m = new Map();
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
  // Розстановка
  // ---------------------------------------------------------------------------------------------

  /// Тримає сервер у курсі: повний флот — 'place', неповний — 'clear', щоб таймер не повів у бій зі
  /// старим флотом, який людина вже розібрала.
  function sendPlan(s, ctx) {
    if (s.plan.length === FLEET.length) {
      s.sent = true;
      ctx.act('place', { ships: s.plan.map((sh) => ({ cells: sh.cells })) });
      return;
    }
    if (!s.sent) return;
    s.sent = false;
    ctx.act('clear');
  }

  /// Клік по своєму полю у фазі розстановки: поставити обраний корабель або підняти той, що вже стоїть.
  function placeClick(root, ctx, cell) {
    const s = state(root);
    const at = s.plan.findIndex((sh) => sh.cells.includes(cell));
    if (at >= 0) {
      const ship = s.plan[at];
      // Однопалубний крутити нема як: для нього клік означає одразу «забрати назад», інакше корабель
      // «повернувся» б сам у себе і зняти його з поля не вийшло б нічим.
      const turned = ship.cells.length > 1
        ? fits(s.plan, ship.cells[0], ship.cells.length, !isHoriz(ship.cells), at)
        : null;
      if (turned) s.plan[at] = { cells: turned };
      else {
        // Повернути нема куди (або це однопалубний) — забираємо корабель назад у список.
        s.plan.splice(at, 1);
        s.pick = ship.cells.length;
        ctx.toast('Забрав корабель назад', '');
      }
    } else {
      const left = rest(s.plan);
      if (!left.length) return;
      const size = left.includes(s.pick) ? s.pick : left[0];
      const cells = fits(s.plan, cell, size, s.horiz, -1);
      if (!cells) { ctx.toast('Сюди він не стане: кораблі не торкаються навіть кутами', 'err'); return; }
      s.plan.push({ cells });
      const still = rest(s.plan);
      if (!still.includes(s.pick)) s.pick = still[0] || 0;
    }
    sendPlan(s, ctx);
    paint(root, ctx);
  }

  function tools(root, ctx, v) {
    let host = root.querySelector(':scope > .bs-tools');
    const placing = (v.phase || 'placing') === 'placing' && ctx.mine && ctx.playing;
    if (!placing) {
      if (host) {
        const arc = host.querySelector(':scope > .garc');
        if (arc && arc._arc) arc._arc.stop();
        host.remove();
      }
      return;
    }
    if (!host) {
      host = document.createElement('div');
      host.className = 'bs-tools';
      root.appendChild(host);
    }
    const s = state(root);
    const ready = !!(v.me && v.me.ready);
    const left = rest(s.plan);
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
      acts.innerHTML = '<button type="button" data-bs="random">Випадково</button>'
        + '<button type="button" data-bs="clear" class="ghost">Скинути</button>'
        + '<button type="button" data-bs="ready" class="primary">Готово</button>';
      acts.addEventListener('click', (e) => {
        const b = e.target.closest('[data-bs]');
        if (!b || b.disabled) return;
        const cur = state(root), o = acts._o || {};
        if (b.dataset.bs === 'random') { cur.adopt = true; o.ctx.act('random'); }
        if (b.dataset.bs === 'clear') {
          // adopt знімаємо: інакше найближче ж малювання наллє план назад із серверного виду.
          cur.plan = [];
          cur.pick = FLEET[0];
          cur.adopt = false;
          sendPlan(cur, o.ctx);
          paint(root, o.ctx);
        }
        if (b.dataset.bs === 'ready') o.ctx.act('ready');
      });
      host.insertBefore(acts, host.firstChild);
    }
    acts._o = { ctx };
    const full = s.plan.length === FLEET.length;
    acts.querySelectorAll('[data-bs]').forEach((b) => {
      const off = ready || (b.dataset.bs === 'ready' && !full);
      if (b.disabled !== off) b.disabled = off;
    });

    // Дуга-таймер каркаса сама крутиться на rAF; нам лишається дати їй кінець фази.
    if (v.placeUntil) HGames.ui.timerArc(host, v.placeUntil, PLACE_MS);
  }

  // ---------------------------------------------------------------------------------------------
  // Малювання картки
  // ---------------------------------------------------------------------------------------------

  function paint(root, ctx) {
    const s = state(root);
    const v = ctx.view || {};
    const phase = v.phase || 'placing';
    if (s.phase !== phase) { s.phase = phase; s.pending.clear(); }
    // «Ще раз» — це нова партія: старий флот у браузері треба забути, інакше він так і висітиме
    // на полі, хоч сервер про нього вже нічого не знає.
    const round = (ctx.room && ctx.room.round) || 1;
    if (s.round !== round) {
      s.round = round;
      s.plan = [];
      s.pick = FLEET[0];
      s.horiz = true;
      s.adopt = true;
      s.sent = false;
      s.pending.clear();
    }

    let host = root.querySelector(':scope > .bs');
    if (!host) {
      host = document.createElement('div');
      host.className = 'bs';
      root.insertBefore(host, root.firstChild);
    }

    if (ctx.seat == null) { paintWatcher(host, ctx, v); tools(root, ctx, v); return; }

    const me = v.me || {};
    const enemy = v.enemy || {};
    // У розстановці на своєму полі показуємо те, що людина совгає зараз; далі — те, що прийняв сервер.
    if (phase === 'placing' && s.adopt && (me.ships || []).length) {
      s.plan = me.ships.map((cells) => ({ cells: cells.slice() }));
      s.adopt = false;
      s.sent = true;
    }
    const ships = phase === 'placing' ? s.plan.map((sh) => sh.cells) : (me.ships || []);

    const mine = myMarks(me, ships);
    const theirs = knownMarks(enemy);
    for (const c of s.pending) if (theirs.has(c)) s.pending.delete(c);

    // Кораблі можна совгати, лише поки не сказано «Готово».
    const editable = phase === 'placing' && ctx.playing && !me.ready;
    const left = rest(s.plan);
    const pick = left.includes(s.pick) ? s.pick : left[0];
    const no = editable ? blocked(s.plan, -1) : null;
    const free = (i) => {
      if (!pick) return false;
      const cells = span(i, pick, s.horiz);
      return !!cells && !cells.some((c) => no.has(c));
    };
    const sunkMine = ships.filter((sh) => sh.every((c) => mine.get(c) === 'ship sunk')).length;

    const meSide = ensureSide(host, 'me', phase === 'placing'
      ? 'Моє поле <b>' + ships.length + '</b> з 10'
      : 'Моє поле <b>' + (ships.length - sunkMine) + '</b>');
    paintGrid(meSide, (i) => mine.get(i) || '',
      editable ? (cell) => placeClick(root, ctx, cell) : null,
      (i) => editable && (mine.has(i) || free(i)));

    const foeSide = ensureSide(host, 'foe', 'Чуже поле <b>' + (enemy.left == null ? '' : enemy.left) + '</b>');
    paintGrid(foeSide, (i) => theirs.get(i) || (s.pending.has(i) ? 'wait' : ''),
      (cell) => {
        s.pending.add(cell);
        // Види в цієї гри приходять з тиком, тож до відповіді сервера тримаємо клітинку «в польоті».
        ctx.act('shoot', { cell }).then((r) => {
          if (r && r.ok) return;
          s.pending.delete(cell);
          paint(root, ctx);
        });
        paint(root, ctx);
      },
      // Поки постріл у польоті, поле замкнене цілком: вид (а з ним і ctx.myTurn) прийде аж із тиком, тож
      // інакше другий клік поспіль летів би на сервер і повертався червоним «Зараз не твій хід».
      (i) => ctx.myTurn && !s.pending.size && !theirs.has(i));

    tools(root, ctx, v);
  }

  /// Глядач бачить обидва поля в тому самому вигляді, що й суперники бачать одне одного — без кораблів.
  function paintWatcher(host, ctx, v) {
    const boards = v.boards || [v.enemy || {}, v.enemy || {}];
    for (let seat = 0; seat < 2; seat++) {
      const known = boards[seat] || {};
      const cap = ctx.esc(ctx.nickOf(seat) || ctx.seatName(seat)) + ' <b>' + (known.left == null ? '' : known.left) + '</b>';
      const side = ensureSide(host, seat === 0 ? 'me' : 'foe', cap);
      const marks = knownMarks(known);
      paintGrid(side, (i) => marks.get(i) || '', null, () => false);
    }
  }

  HGames.register({
    id: 'battleship',
    icon: ICON,
    seatNames: ['синій', 'червоний'],
    seatClass: ['x', 'o'],

    mount(root, ctx) { paint(root, ctx); },
    update(root, ctx) { paint(root, ctx); },

    /// Кадр несе лічильники й відлік — оновлюємо тільки підписи, поля з нього не малюються.
    frame(root, ctx, f) {
      // у розстановці підпис свого поля рахує поставлені кораблі, а не живі — кадру там нема що сказати
      if (!f || !f.left || f.phase === 'placing') return;
      const host = root.querySelector(':scope > .bs');
      if (!host) return;
      const seat = ctx.seat;
      const nums = [
        seat == null ? f.left[0] : f.left[seat],
        seat == null ? f.left[1] : f.left[1 - seat],
      ];
      host.querySelectorAll('.bs-cap b').forEach((b, i) => {
        const s = String(nums[i]);
        if (b.textContent !== s) b.textContent = s;
      });
    },

    status(ctx) {
      if (!ctx.playing) return '';
      // фаза й відлік реалтайму живуть у кадрах, а не у видах — беремо свіжіше
      const f = (ctx.frame && ctx.frame.phase) ? ctx.frame : (ctx.view || {});
      if (f.phase !== 'placing') return '';   // у бою «Твій хід» каркас напише сам із view.turn
      if (ctx.seat == null) return 'Розставляють кораблі';
      const ready = !!(ctx.view && ctx.view.me && ctx.view.me.ready);
      const left = f.placeLeft;
      return (ready ? 'Чекаю на суперника' : 'Розстав кораблі, тоді «Готово»')
        + (left == null ? '' : ' · ' + left + ' с');
    },

    unmount(root) {
      const arc = root.querySelector('.garc');
      if (arc && arc._arc) arc._arc.stop();
      root._bs = null;
    },
  });
})();
