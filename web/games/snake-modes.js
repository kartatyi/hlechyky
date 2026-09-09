/*
  Два режими змійки в одному файлі: «Мотоцикли» (tron) і «Змійка на двох» (snake-coop).
  Каркас дозволяє кілька register в одному модулі, тому обидві гри кажуть Client: "snake-modes"
  (Impl/SnakeModes.cs), і окремих tron.js / snake-coop.js не існує.

  Мотоцикли. Вид (подія 'room'): { width, height, a: int[], b: int[], dirA, dirB, winsA, winsB, startIn, winner }.
  Кадр (подія 'frame', 10 на секунду) — ДЕЛЬТА: { ha, hb, startIn, winner }. Сліди ростуть до сотень
  клітинок, тож повний стан ганяти щотика не можна: масиви тримає клієнт, дописуючи голови з кадрів, а на
  кожну подію 'room' перемальовує поле з нуля за видом. Тому й множина побачених клітинок — щоб кадр, який
  прилетів після свіжішого виду, не дописав голову вдруге.

  Змійка на двох. Вид і кадр однакові: { s: int[], apple, dir, startIn, len, winner }. Змійка одна й росте
  лише з яблук — повний стан у кадрі коштує дешево.

  Ввід обох: Input('turn', { dir }) — 0 праворуч, 1 вниз, 2 ліворуч, 3 вгору.
*/
(() => {
  const W = 26, H = 18, PX = 16;
  const TRON_MS = 100, COOP_MS = 120;

  const DIRS = { ArrowRight: 0, KeyD: 0, ArrowDown: 1, KeyS: 1, ArrowLeft: 2, KeyA: 2, ArrowUp: 3, KeyW: 3 };
  /// Вісь місця: 0 крутить вертикаль, 1 — горизонталь. Те саме правило живе на сервері (CoopSnakeCore.OwnAxis).
  const ownAxis = (seat, dir) => (seat === 0 ? dir === 1 || dir === 3 : seat === 1 && (dir === 0 || dir === 2));

  const TRON_ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M2 12h5V7h7" fill="none" stroke="var(--accent)" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>'
    + '<circle cx="14" cy="7" r="1.9" fill="var(--ok)"/></svg>';
  const COOP_ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M2 12h4.4a2.6 2.6 0 0 0 0-5.2H5.4a2.6 2.6 0 0 1 0-5.2H9" fill="none" stroke="var(--accent)" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>'
    + '<circle cx="4" cy="12" r="1.7" fill="var(--ok)"/><circle cx="12.5" cy="1.9" r="1.7" fill="var(--clay)"/></svg>';

  const x0 = (cell) => (cell % W) * PX;
  const y0 = (cell) => Math.floor(cell / W) * PX;

  function field(st) {
    const c = st.cv, g = c.ctx;
    g.fillStyle = st.css('--bg2', '#16291f');
    g.fillRect(0, 0, c.w, c.h);
    return g;
  }

  /// Затемнення поля з великою цифрою відліку. waiting — стіл ще чекає на другого: відлік не показуємо,
  /// бо він і не йде (сервер тикає лише в партії).
  function shade(st, startIn, winner, waiting, tickMs) {
    if (!((startIn > 0 && !waiting) || winner != null)) return;
    const c = st.cv, g = c.ctx;
    g.fillStyle = st.css('--gshade', 'rgba(15, 31, 24, .62)');
    g.fillRect(0, 0, c.w, c.h);
    if (!(startIn > 0)) return;
    g.fillStyle = st.css('--text', '#ecf1ea');
    g.font = '700 46px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillText(String(Math.ceil((startIn * tickMs) / 1000)), c.w / 2, c.h / 2);
  }

  /// Рядок над полем: рахунок серії в мотоциклах, довжина змійки в коопі.
  function score(root, html) {
    let el = root.querySelector(':scope > .gscore');
    if (!el) {
      el = document.createElement('div');
      el.className = 'gscore';
      root.insertBefore(el, root.firstChild);
    }
    if (el.innerHTML !== html) el.innerHTML = html;
  }

  /// Хрестовина потрібна лише тому, хто грає: сів глядач за стіл — вона з'явиться на наступному 'room'.
  function pad(root, ctx, dirs) {
    if (ctx.mine) HGames.ui.dpad(root, (d) => ctx.input('turn', { dir: d }), dirs);
    else { const d = root.querySelector(':scope > .dpad'); if (d) d.remove(); }
  }

  // =============================================================================================
  // Мотоцикли
  // =============================================================================================

  function tronState(root, ctx) {
    if (!root._tron) root._tron = { cv: null, a: [], b: [], sa: new Set(), sb: new Set(), startIn: 0, winner: null, css: ctx.css };
    return root._tron;
  }

  /// Слід — суцільна стіна (клітинка в клітинку, без зазорів), голова тим самим кольором плюс світла
  /// цятка мотоцикліста: так видно, куди саме він зараз їде.
  function trail(st, cells, color) {
    if (!cells.length) return;
    const g = st.cv.ctx;
    g.fillStyle = color;
    for (const cell of cells) g.fillRect(x0(cell), y0(cell), PX, PX);
    const h = cells[0];
    g.fillStyle = st.css('--text', '#ecf1ea');
    g.beginPath();
    g.arc(x0(h) + PX / 2, y0(h) + PX / 2, PX / 4, 0, Math.PI * 2);
    g.fill();
  }

  function drawTron(st, waiting) {
    if (!st.cv) return;
    field(st);
    trail(st, st.a, st.css('--accent', '#f4c542'));
    trail(st, st.b, st.css('--ok', '#7bd389'));
    shade(st, st.startIn, st.winner, waiting, TRON_MS);
  }

  /// Голова з кадру. Множина побачених клітинок ловить два випадки: відлік (голова стоїть на місці) і
  /// кадр, що розминувся зі свіжішим видом. Слід не зникає, тож двічі в одну клітинку мотоцикл не заїде.
  function addHead(cells, seen, cell) {
    if (cell == null || seen.has(cell)) return;
    seen.add(cell);
    cells.unshift(cell);
  }

  HGames.register({
    id: 'tron',
    icon: TRON_ICON,
    seatNames: ['жовтий', 'зелений'],
    seatClass: ['x', 'o'],

    mount(root, ctx) {
      const st = tronState(root, ctx);
      st.cv = HGames.ui.canvas(root, { w: W * PX, h: H * PX });
    },

    update(root, ctx) {
      const st = tronState(root, ctx);
      if (!st.cv) return;
      pad(root, ctx);
      const v = ctx.view;
      // 'room' приходить рідше за кадри, але вид завжди свіжіший за них: перекладаємо поле з нуля
      if (v && Array.isArray(v.a)) {
        st.a = v.a.slice();
        st.b = (v.b || []).slice();
        st.sa = new Set(st.a);
        st.sb = new Set(st.b);
        st.startIn = v.startIn || 0;
        st.winner = v.winner == null ? null : v.winner;
        score(root, '<b>' + (v.winsA || 0) + '</b> : <b>' + (v.winsB || 0) + '</b>');
      }
      st.cv.resize();
      drawTron(st, !ctx.playing);
    },

    frame(root, ctx, f) {
      const st = tronState(root, ctx);
      if (!st.cv || !f) return;
      addHead(st.a, st.sa, f.ha);
      addHead(st.b, st.sb, f.hb);
      st.startIn = f.startIn || 0;
      st.winner = f.winner == null ? null : f.winner;
      drawTron(st, !ctx.playing);
    },

    onKey(e, ctx) {
      const dir = DIRS[e.code];
      if (dir === undefined || !ctx.mine || !ctx.playing) return false;
      ctx.input('turn', { dir });
      return true;
    },

    status(ctx) {
      if (!ctx.playing) return '';
      // фаза й відлік реалтайму живуть у кадрах, а не у видах — інакше «Готуйсь…» висіло б довго після старту
      const f = ctx.frame && ctx.frame.startIn != null ? ctx.frame
        : (ctx.view && ctx.view.startIn != null ? ctx.view : null);
      if (f && f.startIn > 0) return 'Готуйсь…';
      return ctx.mine ? 'Стрілки або WASD — і не наїдь на слід' : 'Дивишся збоку';
    },

    unmount(root) { root._tron = null; },
  });

  // =============================================================================================
  // Змійка на двох
  // =============================================================================================

  function coopState(root, ctx) {
    if (!root._coop) root._coop = { cv: null, last: null, css: ctx.css };
    return root._coop;
  }

  function drawCoop(st, f, waiting) {
    if (!st.cv || !f) return;
    const g = field(st);
    if (f.apple != null && f.apple >= 0) {
      g.fillStyle = st.css('--clay', '#c5763a');
      g.beginPath();
      g.arc(x0(f.apple) + PX / 2, y0(f.apple) + PX / 2, PX / 2 - 2.5, 0, Math.PI * 2);
      g.fill();
    }
    (f.s || []).forEach((cell, i) => {
      g.fillStyle = i ? st.css('--accent2', '#d9a92f') : st.css('--accent', '#f4c542');
      g.beginPath();
      g.roundRect(x0(cell) + 1, y0(cell) + 1, PX - 2, PX - 2, i ? 3 : 6);
      g.fill();
    });
    shade(st, f.startIn, f.winner, waiting, COOP_MS);
  }

  const axisWord = (seat) => (seat === 0 ? '↑↓' : '←→');

  HGames.register({
    id: 'snake-coop',
    icon: COOP_ICON,
    seatNames: ['вгору-вниз', 'вліво-вправо'],
    seatClass: ['x', 'o'],

    mount(root, ctx) {
      const st = coopState(root, ctx);
      st.cv = HGames.ui.canvas(root, { w: W * PX, h: H * PX });
    },

    update(root, ctx) {
      const st = coopState(root, ctx);
      if (!st.cv) return;
      // на пальці показуємо лише свої дві кнопки: чужу вісь сервер усе одно не прийме
      pad(root, ctx, ctx.seat === 0 ? [3, 1] : [2, 0]);
      const f = (ctx.view && Array.isArray(ctx.view.s)) ? ctx.view : st.last;
      if (f) st.last = f;
      score(root, 'довжина <b>' + ((f && f.len) || 0) + '</b>');
      st.cv.resize();
      drawCoop(st, f, !ctx.playing);
    },

    frame(root, ctx, f) {
      const st = coopState(root, ctx);
      if (!st.cv || !f) return;
      st.last = f;
      score(root, 'довжина <b>' + (f.len || 0) + '</b>');
      drawCoop(st, f, !ctx.playing);
    },

    onKey(e, ctx) {
      const dir = DIRS[e.code];
      // чужа вісь — не наша клавіша: віддаємо її далі, а не з'їдаємо мовчки
      if (dir === undefined || !ctx.mine || !ctx.playing || !ownAxis(ctx.seat, dir)) return false;
      ctx.input('turn', { dir });
      return true;
    },

    status(ctx) {
      if (!ctx.playing) return '';
      const f = ctx.frame && ctx.frame.startIn != null ? ctx.frame
        : (ctx.view && ctx.view.startIn != null ? ctx.view : null);
      if (f && f.startIn > 0) return 'Готуйсь…';
      return ctx.mine ? 'Ти крутиш: ' + axisWord(ctx.seat) : 'Дивишся збоку';
    },

    unmount(root) { root._coop = null; },
  });
})();
