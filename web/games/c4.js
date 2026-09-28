/*
  Чотири в ряд — класика на двох ('c4') і стіл на компанію ('c4x', 3–4 гравці, поле ширше). Правила
  однакові (Impl/GridGame.cs), тож і малює їх один модуль: хід — це колонка, фішку на дно кладе сервер.
  Клікабельна вся колонка, поки її верхня клітинка порожня.

  Вид із сервера: { cells: ('x'|'o'|'c'|'d'|null)[], width, height, need, turn, line, marks, winner,
                    last?, active?, series? } — last/active/series з'явились 24.09 і необов'язкові.
  Що тут є крім дошки:
  - фішка падає згори в ту клітинку, куди лягла (last) — видно, хто куди щойно кинув;
  - під мишею в колонці світиться «привид» фішки там, куди вона впаде; клавіші 1–9/0 і ←/→ + Enter;
  - на компанію — у кожного свій колір і своя позначка (● ▲ ■ ◆), щоб розрізняв і дальтонік;
  - рахунок серії «Ще раз» і кнопка «Здатись» (у два дотики).
  Прохід №3 (29.09): годинник ходу (clock/clockIn — простояв, і фішка падає сама), «Глек підсідає» в c4x
  (bots — імена по місцях, botIn — коли Глек «подумав»; ходить на штовхан нашого клієнта) і пари 2×2 (pairs).
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<circle cx="2.6" cy="13.4" r="2.1" fill="var(--accent)"/>'
    + '<circle cx="6.4" cy="9.6" r="2.1" fill="var(--accent)"/>'
    + '<circle cx="10.2" cy="5.8" r="2.1" fill="var(--accent)"/>'
    + '<circle cx="14" cy="2" r="2.1" fill="var(--accent)"/>'
    + '<circle cx="2.6" cy="5.8" r="2.1" fill="var(--ok)"/>'
    + '<circle cx="6.4" cy="2" r="2.1" fill="var(--ok)"/></svg>';
  const ICON_PARTY = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<circle cx="3" cy="13" r="2.3" fill="var(--accent)"/>'
    + '<circle cx="8" cy="13" r="2.3" fill="var(--ok)"/>'
    + '<circle cx="13" cy="13" r="2.3" fill="var(--clay)"/>'
    + '<circle cx="8" cy="8" r="2.3" fill="var(--text)"/>'
    + '<circle cx="3" cy="8" r="2.3" fill="var(--ok)"/>'
    + '<circle cx="8" cy="3" r="2.3" fill="var(--clay)"/></svg>';

  const LETTERS = ['x', 'o', 'c', 'd'];
  const clsOf = (c) => (typeof c === 'number' ? LETTERS[c] || '' : LETTERS.indexOf(c) >= 0 ? c : '');
  const DROP_MS = 520;

  function state(root) {
    if (!root._c4) root._c4 = { hover: -1, lastSeen: undefined, dropUntil: 0, sure: false, sig: '' };
    return root._c4;
  }

  function ensure(root, cls) {
    let el = root.querySelector(':scope > .' + cls);
    if (!el) { el = document.createElement('div'); el.className = cls; root.appendChild(el); }
    return el;
  }
  /// Порівнюємо з тим рядком, що клали самі: innerHTML браузер серіалізує по-своєму (&#39; → ', data-x → data-x=""),
  /// і порівняння з ним майже ніколи не каже «однаково» — DOM перебудовувався б на кожен вид.
  const setHtml = (el, html) => { if (el._h !== html) { el._h = html; el.innerHTML = html; } };

  /// Куди впаде фішка в колонці col: найнижча вільна клітинка, або -1.
  function landing(cells, w, h, col) {
    for (let r = h - 1; r >= 0; r--) if (!cells[r * w + col]) return r * w + col;
    return -1;
  }

  /// Ім'я на місці: нік людини або «Глек 🤖» (місце бота в каркасі порожнє).
  const nameAt = (ctx, i) => ctx.nickOf(i) || (ctx.view && ctx.view.bots && ctx.view.bots[i]) || '';

  /// Сервер без тика: Глека, чия черга, і годинник того, хто задумався, «штовхаємо» ми. Першим штовхає перший
  /// гравець-людина за столом, решта — із запасом, на випадок коли його вкладка спить. Зарано чи вдруге —
  /// сервер відмовить, а send шле без тосту.
  function nudge(root, ctx) {
    const st = state(root);
    const v = ctx.view || {};
    clearTimeout(st.nt);
    st.nt = 0;
    const bots = v.bots || [];
    if (!ctx.playing || ctx.seat == null || bots[ctx.seat] || !HGames.send) return;
    let rank = 0;
    for (let i = 0; i < ctx.seat; i++) if (ctx.nickOf(i)) rank++;
    const action = typeof v.botIn === 'number' ? 'bot' : typeof v.clockIn === 'number' ? 'timeout' : '';
    if (!action) return;
    // За себе годинник штовхає сам той, хто задумався, — без запасу.
    const mine = action === 'timeout' && v.turn === ctx.seat;
    const wait = (action === 'bot' ? v.botIn : v.clockIn) + 60 + (mine ? 0 : rank * 700 + (action === 'timeout' ? 400 : 0));
    const fire = () => {
      if (!root._c4 || !ctx.room) return;
      HGames.send('Act', ctx.room.id, action, null);
      st.nt = setTimeout(fire, 1500);                        // відповідь — новий вид, і nudge заведе все наново
    };
    st.nt = setTimeout(fire, Math.max(0, wait));
  }

  /// Годинник ходу над полем: дуга каркаса й підпис, чий час спливає.
  function clock(root, ctx) {
    const v = ctx.view || {};
    const el = ensure(root, 'c4clk');
    const on = !!(ctx.playing && v.clock > 0 && typeof v.clockIn === 'number');
    el.hidden = !on;
    if (!on) return;
    const st = state(root);
    const key = v.turn + '|' + (v.cells || []).join(',');
    if (st.clkKey !== key) { st.clkKey = key; st.clkUntil = new Date(Date.now() + v.clockIn).toISOString(); }
    if (HGames.ui.timerArc) HGames.ui.timerArc(el, st.clkUntil, v.clock * 1000);
    let lab = el.querySelector(':scope > .c4clk-t');
    if (!lab) { lab = document.createElement('span'); lab.className = 'c4clk-t'; el.appendChild(lab); }
    const txt = v.turn === ctx.seat ? 'Твій час на хід — простоїш, і фішка впаде сама' : 'Годинник: ходить ' + nameAt(ctx, v.turn);
    if (lab.textContent !== txt) lab.textContent = txt;
  }

  const reduced = () => window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  function paint(root, ctx) {
    const v = ctx.view || {};
    const st = state(root);
    const party = !!(ctx.room && ctx.room.maxPlayers > 2);
    const cells = v.cells || [];
    const w = v.width || (party ? 9 : 7);
    const h = cells.length ? Math.ceil(cells.length / w) : (v.height || (party ? 7 : 6));
    const line = v.line || [];
    const marks = v.marks || [];
    // Позначки малюємо, лише коли вони різні (стіл на компанію): у класиці обидві — «●».
    const shapes = marks.length > 1 && marks.some((m) => m !== marks[0]);
    ctx._c4root = root;

    // Нова фішка з'явилась — хай падає. Першу картинку (відкрили стіл посеред партії) не анімуємо.
    const last = typeof v.last === 'number' ? v.last : -1;
    if (st.lastSeen !== undefined && last >= 0 && last !== st.lastSeen && !reduced()) st.dropUntil = performance.now() + DROP_MS;
    st.lastSeen = last;
    const dropping = performance.now() < st.dropUntil;

    // Позиція змінилась — «Точно здатись?» більше не висить.
    const sig = cells.join(',') + '|' + v.turn;
    if (st.sig !== sig) { st.sig = sig; st.sure = false; }

    const ghost = ctx.myTurn && st.hover >= 0 ? landing(cells, w, h, st.hover) : -1;
    const myCls = ctx.seat != null ? LETTERS[ctx.seat] : '';

    const board = HGames.ui.grid(root, {
      cols: w,
      rows: h,
      cls: 'discs c4b' + (party ? ' party' : ''),
      cell: (i) => {
        const c = clsOf(cells[i]);
        const k = ['c4c'];
        if (c) k.push(c);
        if (line.includes(i)) k.push('win');
        if (i === last && !v.winner) k.push('last');
        if (i === last && dropping) k.push('drop');
        if (!c && i === ghost) k.push('ghost', myCls);
        const seatIdx = c ? LETTERS.indexOf(c) : (i === ghost ? ctx.seat : -1);
        const html = seatIdx >= 0 && shapes ? '<i>' + ctx.esc(marks[seatIdx] || '') + '</i>' : '';
        return { html, cls: k.join(' '), disabled: !(ctx.myTurn && !cells[i % w]) };
      },
      // сервер (GridGame) чекає на { cell }; у грі з гравітацією cell — це номер колонки, а не клітинки
      onCell: (i) => drop(root, ctx, i % w),
    });
    // Рядок падіння для анімації: CSS знає, з якої висоти падати, лише з інлайн-змінної.
    if (last >= 0 && board.children[last]) board.children[last].style.setProperty('--row', String(Math.floor(last / w) + 1));
    hoverWiring(board, root, ctx, w);

    legend(root, ctx, party, marks, shapes);
    footer(root, ctx, st);
    clock(root, ctx);
    // Порядок: хто яким кольором — над полем, серія й «Здатись» — під ним. Каркас перебудовує дошку, коли
    // міняється її розмір (у лобі компанії поле 9×7, а вчотирьох — 10×8), і нова лягала в самий низ — тоді
    // «Здатись» опинявся над полем, а рахунок серії — під легендою.
    const leg = root.querySelector(':scope > .c4legend'), foot = root.querySelector(':scope > .c4foot');
    const clk = root.querySelector(':scope > .c4clk');
    if (board.previousElementSibling !== clk) root.insertBefore(clk, board);
    if (clk.previousElementSibling !== leg) root.insertBefore(leg, clk);
    if (board.nextElementSibling !== foot) board.after(foot);
  }

  /// Привид фішки під мишею. Слухачі вішаємо один раз на елемент дошки (grid() його перевикористовує).
  function hoverWiring(board, root, ctx, w) {
    board._c4 = { root, ctx, w };
    if (board._c4wired) return;
    board._c4wired = true;
    board.addEventListener('pointermove', (e) => {
      if (e.pointerType === 'touch') return;             // на пальці наведення нема — тап одразу кидає фішку
      const b = e.target.closest('.cell');
      const o = board._c4;
      const col = b ? (+b.dataset.i) % o.w : -1;
      const st = state(o.root);
      if (st.hover !== col) { st.hover = col; paint(o.root, o.ctx); }
    });
    board.addEventListener('pointerleave', () => {
      const o = board._c4;
      const st = state(o.root);
      if (st.hover !== -1) { st.hover = -1; paint(o.root, o.ctx); }
    });
  }

  function drop(root, ctx, col) {
    if (!ctx.myTurn) return;
    state(root).sure = false;
    ctx.act('move', { cell: col });
  }

  /// Хто яким кольором — на компанію без цього не розібратись; вибулих закреслено.
  function legend(root, ctx, party, marks, shapes) {
    const el = ensure(root, 'c4legend');
    if (!party) { setHtml(el, ''); el.hidden = true; return; }
    el.hidden = false;
    const v = ctx.view || {};
    const act = v.active || [];
    const who = (i) => {
      const nick = nameAt(ctx, i);
      if (!nick) return '';
      const out = ctx.playing && act.length > i && act[i] === false;
      return '<span class="c4who' + (out ? ' out' : '') + (ctx.playing && v.turn === i ? ' turn' : '') + '">'
        + '<b class="c4dot ' + LETTERS[i] + '">' + (shapes ? ctx.esc(marks[i] || '') : '') + '</b>' + ctx.esc(nick) + '</span>';
    };
    let html = '';
    if (v.pairs && ctx.playing) {
      // Пари через одного: жовті з рудими проти зелених з білими.
      html = '<span class="c4pair">' + who(0) + '<i>+</i>' + who(2) + '</span><span class="c4vs">⚔</span>'
        + '<span class="c4pair">' + who(1) + '<i>+</i>' + who(3) + '</span>';
    } else for (let i = 0; i < (ctx.room.maxPlayers || 4); i++) html += who(i);
    setHtml(el, html);
  }

  /// Рахунок серії і «Здатись». Серія — лише коли за столом уже дограли хоч одну партію.
  function footer(root, ctx, st) {
    const v = ctx.view || {};
    const el = ensure(root, 'c4foot');
    let html = '';
    const s = v.series;
    if (s && s.wins) {
      const parts = [];
      for (let i = 0; i < s.wins.length; i++) {
        const nick = ctx.nickOf(i);
        if (nick) parts.push('<span class="' + LETTERS[i] + '">' + ctx.esc(nick) + ' <b>' + s.wins[i] + '</b></span>');
      }
      html += '<span class="c4-serie" title="Скільки партій виграв кожен за цим столом">Серія: ' + parts.join(ctx.room.maxPlayers > 2 ? ' · ' : ' : ')
        + (s.draws ? ' · нічиїх <b>' + s.draws + '</b>' : '') + '</span>';
    }
    const inGame = ctx.mine && ctx.playing && (!v.active || v.active[ctx.seat] !== false);
    if (inGame) {
      html += '<button type="button" class="ghost' + (st.sure ? ' danger' : '') + '" data-c4="resign">'
        + (st.sure ? 'Точно здатись?' : 'Здатись') + '</button>';
    }
    setHtml(el, html);
    const b = el.querySelector('[data-c4]');
    if (b) b.onclick = () => {
      if (!st.sure) { st.sure = true; paint(root, ctx); return; }
      st.sure = false;
      ctx.act('resign');
    };
  }

  const mod = (id, icon, seatNames) => ({
    id,
    icon,
    seatNames,
    seatClass: LETTERS,
    mount(root, ctx) { paint(root, ctx); nudge(root, ctx); },
    update(root, ctx) {
      const prev = state(root).lastSeen;
      paint(root, ctx);
      nudge(root, ctx);
      const v = ctx.view || {};
      // Хід за годинником — скажемо всім, чому фішка впала «сама».
      if (v.auto && typeof v.last === 'number' && prev !== undefined && prev !== v.last) {
        const who = v.cells && v.cells[v.last] ? LETTERS.indexOf(v.cells[v.last]) : -1;
        if (who >= 0) ctx.toast(who === ctx.seat ? '⏰ Час вийшов — фішка впала сама' : '⏰ ' + nameAt(ctx, who) + ' задумався — фішка впала сама');
      }
      // Коли фішка долетить — перемалювати без класу drop, щоб наступний кадр її вже не смикав.
      const st = state(root);
      clearTimeout(st.t);
      if (performance.now() < st.dropUntil) st.t = setTimeout(() => root._c4 && paint(root, ctx), DROP_MS + 30);
    },
    unmount(root) { const st = root._c4; if (st) { clearTimeout(st.t); clearTimeout(st.nt); } root._c4 = null; },
    /// Місце Глека в каркасі порожнє — «Ходить …» він би не назвав; у лобі MinPlayers = 1 заради Глека.
    status(ctx) {
      const room = ctx.room || {};
      const v = ctx.view || {};
      const opt = room.options || {};
      if (room.status === 'lobby' && room.maxPlayers > 2) {
        let people = 0;
        for (let i = 0; i < 4; i++) if (ctx.nickOf(i)) people++;
        const bots = +(opt.bots || 0);
        if (opt.teams === '1' && people + bots < 4) return '👥 Пари 2×2 — чекаємо, поки сяде четверо (або відкрий стіл з 🤖 Глеком)';
        return people + bots < 3 ? 'Чекаємо, хто підсяде: треба троє (або відкрий стіл з «🤖 Глек підсідає»)' : '';
      }
      if (!ctx.playing || v.turn == null) return '';
      if (v.bots && v.bots[v.turn]) return v.bots[v.turn] + ' думає…';
      return '';
    },
    /// Клавіатура: 1–9 (і 0 — десята) кидають у колонку, ←/→ або A/D водять привид, Enter чи пробіл кидають туди.
    /// Джойстик шле ті самі ←/→ і Enter (pad нижче), тож на Деці це працює без жодної правки.
    onKey(e, ctx) {
      const root = ctx._c4root;
      if (!root || !root._c4 || !ctx.mine || !ctx.playing || e.ctrlKey || e.metaKey || e.altKey) return false;
      const t = e.target;
      if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable)) return false;
      const w = (ctx.view && ctx.view.width) || 7;
      const st = state(root);
      const code = e.code || '';
      const digit = /^(?:Digit|Numpad)([0-9])$/.exec(code) || /^([0-9])$/.exec(e.key || '');
      if (digit) {
        if (!ctx.myTurn) return false;
        const col = digit[1] === '0' ? 9 : +digit[1] - 1;
        if (col >= w) return false;
        drop(root, ctx, col);
        return true;
      }
      const left = code === 'ArrowLeft' || code === 'KeyA', right = code === 'ArrowRight' || code === 'KeyD';
      if (left || right) {
        // Прицілитись можна й поза своїм ходом: привид з'явиться в тій колонці, щойно хід прийде.
        st.hover = st.hover < 0 ? Math.floor(w / 2) : Math.max(0, Math.min(w - 1, st.hover + (left ? -1 : 1)));
        paint(root, ctx);
        return true;
      }
      if (code === 'Enter' || code === 'NumpadEnter' || code === 'Space') {
        if (!ctx.myTurn) return code === 'Space';            // пробіл поза ходом не гортає сторінку
        if (st.hover < 0) { st.hover = Math.floor(w / 2); paint(root, ctx); return true; }
        drop(root, ctx, st.hover);
        return true;
      }
      return false;
    },
    // Дека: стік чи хрестовина водять привид по колонках, Ⓐ кидає фішку.
    pad: { dirs: 'x', a: 'Enter', hint: '{dpad} колонка · {a} кинути фішку' },
  });

  HGames.register(Object.assign(mod('c4', ICON, ['жовті', 'зелені']), {
    news: {
      v: '2026-09-29',
      title: 'Чотири в ряд: годинник ходу',
      items: [
        '⏱ Опція столу «20 с на хід»: хто задумався, за того фішка падає сама у випадкову колонку',
        '🎮 На Деці стік чи хрестовина водять фішку по колонках, Ⓐ кидає',
      ],
    },
  }));
  HGames.register(Object.assign(mod('c4x', ICON_PARTY, ['жовті', 'зелені', 'руді', 'білі']), {
    news: {
      v: '2026-09-29',
      title: 'Чотири в ряд на компанію: пари, Глек і годинник',
      items: [
        '👥 Учотирьох — пари 2×2 через одного: четвірка з фішок напарника теж рахується',
        '🤖 Бракує людей — на порожнє місце підсяде Глек (без черепків і рейтингу)',
        '⏱ Годинник 20 с на хід: хто задумався, за того фішка падає сама',
      ],
    },
  }));
})();
