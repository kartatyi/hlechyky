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
  const setHtml = (el, html) => { if (el.innerHTML !== html) el.innerHTML = html; };

  /// Куди впаде фішка в колонці col: найнижча вільна клітинка, або -1.
  function landing(cells, w, h, col) {
    for (let r = h - 1; r >= 0; r--) if (!cells[r * w + col]) return r * w + col;
    return -1;
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
    let html = '';
    for (let i = 0; i < (ctx.room.maxPlayers || 4); i++) {
      const nick = ctx.nickOf(i);
      if (!nick) continue;
      const out = ctx.playing && act.length > i && act[i] === false;
      html += '<span class="c4who' + (out ? ' out' : '') + (ctx.playing && v.turn === i ? ' turn' : '') + '">'
        + '<b class="c4dot ' + LETTERS[i] + '">' + (shapes ? ctx.esc(marks[i] || '') : '') + '</b>' + ctx.esc(nick) + '</span>';
    }
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
      html += '<span class="gserie" title="Скільки партій виграв кожен за цим столом">Серія: ' + parts.join(ctx.room.maxPlayers > 2 ? ' · ' : ' : ')
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
    mount(root, ctx) { paint(root, ctx); },
    update(root, ctx) {
      paint(root, ctx);
      // Коли фішка долетить — перемалювати без класу drop, щоб наступний кадр її вже не смикав.
      const st = state(root);
      clearTimeout(st.t);
      if (performance.now() < st.dropUntil) st.t = setTimeout(() => root._c4 && paint(root, ctx), DROP_MS + 30);
    },
    unmount(root) { const st = root._c4; if (st) clearTimeout(st.t); root._c4 = null; },
    /// Клавіатура: 1–9 (і 0 — десята) кидають у колонку, ←/→ водять привид, Enter/пробіл кидають туди.
    onKey(e, ctx) {
      const root = ctx._c4root;
      if (!root || !root._c4 || !ctx.myTurn || e.ctrlKey || e.metaKey || e.altKey) return false;
      const t = e.target;
      if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable)) return false;
      const w = (ctx.view && ctx.view.width) || 7;
      const st = state(root);
      if (/^[0-9]$/.test(e.key)) {
        const col = e.key === '0' ? 9 : +e.key - 1;
        if (col >= w) return false;
        drop(root, ctx, col);
        return true;
      }
      if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
        const d = e.key === 'ArrowLeft' ? -1 : 1;
        st.hover = st.hover < 0 ? Math.floor(w / 2) : Math.max(0, Math.min(w - 1, st.hover + d));
        paint(root, ctx);
        return true;
      }
      if (e.key === 'Enter' && st.hover >= 0) { drop(root, ctx, st.hover); return true; }
      return false;
    },
  });

  HGames.register(Object.assign(mod('c4', ICON, ['жовті', 'зелені']), {
    news: {
      v: '2026-09-24',
      title: 'Чотири в ряд: фішки падають, а за стіл — хоч учотирьох',
      items: [
        '🎉 Нова гра поруч — «Чотири в ряд: компанія»: 3–4 гравці на ширшому полі, кожен своїм кольором і позначкою',
        '⬇️ Фішка тепер справді падає згори, а остання кинута підсвічена — видно, хто куди сходив',
        '👻 Наведи мишу на колонку — побачиш, куди ляже фішка; з клавіатури — цифри 1–9 або ←/→ і Enter',
        '🏳️ Кнопка «Здатись» і рахунок серії, якщо тиснете «Ще раз»',
      ],
    },
  }));
  HGames.register(Object.assign(mod('c4x', ICON_PARTY, ['жовті', 'зелені', 'руді', 'білі']), {
    news: {
      v: '2026-09-24',
      title: 'Чотири в ряд — тепер на компанію',
      items: [
        '👥 Троє — поле 9×7, четверо — 10×8. Стіл ставить господар і тисне «Почати»',
        '🎨 У кожного свій колір і позначка: ● ▲ ■ ◆ — не сплутаєш навіть без кольорів',
        '🧱 Блокують тут усі: поки ти пильнуєш одного сусіда, інший уже складає свою четвірку',
        '🏳️ Хто здався чи встав — випадає з черги, його фішки лишаються на полі. Останній, хто лишився, виграє',
      ],
    },
  }));
})();
