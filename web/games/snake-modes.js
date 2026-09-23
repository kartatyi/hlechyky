/*
  Режими змійки в одному файлі: «Мотоцикли» (tron), «Змійка на всіх» (snake-coop) і «…гуртом» на 2–4.
  Каркас дозволяє кілька register в одному модулі, тому обидві гри кажуть Client: "snake-modes"
  (Impl/SnakeModes.cs), і окремих tron.js / snake-coop.js не існує.

  Мотоцикли. Вид (подія 'room'): { width, height, a: int[], b: int[], dirA, dirB, winsA, winsB, startIn, winner }.
  Кадр (подія 'frame', 10 на секунду) — ДЕЛЬТА: { ha, hb, startIn, winner }. Сліди ростуть до сотень
  клітинок, тож повний стан ганяти щотика не можна: масиви тримає клієнт, дописуючи голови з кадрів, а на
  кожну подію 'room' перемальовує поле з нуля за видом. Тому й множина побачених клітинок — щоб кадр, який
  прилетів після свіжішого виду, не дописав голову вдруге.

  Змійка на всіх. Вид і кадр однакові: { s: int[], apple, dir, startIn, len, winner }, у виді ще keys — маска
  стрілок кожного місця (біт 0 праворуч … біт 3 вгору). Змійка одна й росте лише з яблук — повний стан у кадрі коштує дешево.

  Гуртом (tron-party, snake-party, 2–4 гравці) — окремий розділ унизу файла, там і його формат.

  Ввід усіх: Input('turn', { dir }) — 0 праворуч, 1 вниз, 2 ліворуч, 3 вгору.
*/
(() => {
  const W = 26, H = 18, PX = 16;
  const TRON_MS = 100, COOP_MS = 120;

  const DIRS = { ArrowRight: 0, KeyD: 0, ArrowDown: 1, KeyS: 1, ArrowLeft: 2, KeyA: 2, ArrowUp: 3, KeyW: 3 };
  /// Пара за замовчуванням (старий сервер без view.keys): 0 крутить вертикаль, 1 — горизонталь.
  const PAIR_KEYS = [(1 << 1) | (1 << 3), (1 << 0) | (1 << 2), 0, 0];
  /// Маска стрілок місця у «Змійці на всіх» (біт 0 праворуч … біт 3 вгору). Правило живе на сервері
  /// (CoopSnakeCore.Layout), а вид приносить готові маски.
  const keysOf = (ctx) => {
    const v = ctx.view;
    const keys = v && Array.isArray(v.keys) ? v.keys : PAIR_KEYS;
    return ctx.seat == null ? 0 : (keys[ctx.seat] || 0);
  };
  const ownKey = (ctx, dir) => (keysOf(ctx) & (1 << dir)) !== 0;
  /// Кнопки хрестовини для маски: вертикаль перша, як у каркаса; усі чотири — типова хрестовина.
  const padDirs = (mask) => (mask === 15 ? undefined : [3, 1, 2, 0].filter((d) => mask & (1 << d)));
  const ARROW = { 0: '→', 1: '↓', 2: '←', 3: '↑' };

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
    // view — той вид, з якого вже перекладено поле. Каркас віддає в ctx.view КЕШОВАНИЙ об'єкт останньої
    // події 'room', а update() смикається ще й на кожну 'rooms' (будь-хто на сайті створив чи покинув стіл).
    // Під час раунду 'room' не приходить узагалі — Tick віддає самі кадри, — тож без цієї позначки слід
    // відкочувався б до трьох стартових клітинок, а середину його вже ніхто б не домалював: кадр несе лише голови.
    if (!root._tron) root._tron = { cv: null, view: null, a: [], b: [], sa: new Set(), sb: new Set(), startIn: 0, winner: null, css: ctx.css };
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
    pad: { dirs: true, hint: '{dpad} куди їхати' },

    mount(root, ctx) {
      const st = tronState(root, ctx);
      st.cv = HGames.ui.canvas(root, { w: W * PX, h: H * PX });
    },

    update(root, ctx) {
      const st = tronState(root, ctx);
      if (!st.cv) return;
      pad(root, ctx);
      const v = ctx.view;
      // Новий вид (подія 'room': старт раунду, кінець, рематч, підключення глядача) — перекладаємо поле з
      // нуля. Той самий об'єкт удруге — це вже застарілий кеш, і чіпати ним живий слід не можна.
      if (v && Array.isArray(v.a) && v !== st.view) {
        st.view = v;
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
  // Змійка на всіх (1–4 за кермом однієї змійки)
  // =============================================================================================

  function coopState(root, ctx) {
    // view — вид, який уже застосовано (див. пояснення в tronState): кадр коопа завжди свіжіший за
    // кешований вид, тож давати виду перебивати його на кожну 'rooms' означало б смикати змійку назад.
    if (!root._coop) root._coop = { cv: null, view: null, last: null, css: ctx.css };
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

  const axisWord = (ctx) => { const m = keysOf(ctx); return [3, 1, 2, 0].filter((d) => m & (1 << d)).map((d) => ARROW[d]).join(''); };

  HGames.register({
    id: 'snake-coop',
    icon: COOP_ICON,
    seatNames: ['вгору-вниз', 'вліво-вправо', 'вліво', 'вправо'],
    seatClass: ['x', 'o', 'c', 'd'],
    pad: { dirs: true, hint: '{dpad} свої стрілки' },
    news: {
      v: '2026-09-24',
      title: 'Змійка на всіх: від одного до чотирьох',
      items: [
        '🐍 «Змійка на двох» тепер «Змійка на всіх»: за кермом від одного до чотирьох',
        '🎮 Стрілки діляться порівну: удвох — вгору-вниз і вліво-вправо, утрьох — вертикаль, ліво й право, учотирьох — по одній',
        '▶️ Стіл стартує кнопкою «Почати» — можна й самому потренуватись',
        '🤝 Хтось встав посеред раунду — змійка повзе далі, а його стрілки дістаються решті',
      ],
    },

    mount(root, ctx) {
      const st = coopState(root, ctx);
      st.cv = HGames.ui.canvas(root, { w: W * PX, h: H * PX });
    },

    update(root, ctx) {
      const st = coopState(root, ctx);
      if (!st.cv) return;
      // на пальці показуємо лише свої кнопки: чужі сервер усе одно не прийме
      pad(root, ctx, padDirs(keysOf(ctx)));
      if (ctx.view && Array.isArray(ctx.view.s) && ctx.view !== st.view) {
        st.view = ctx.view;
        st.last = ctx.view;
      }
      const f = st.last;
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
      if (dir === undefined || !ctx.mine || !ctx.playing) return false;
      // Чужа вісь — не помилка, а домовленість; на сервер її не шлемо. Але клавішу все одно з'їдаємо:
      // віддати браузеру ↑ чи ↓ посеред живого раунду означає прокрутити сторінку і зігнати поле з екрана.
      if (!ownKey(ctx, dir)) return true;
      ctx.input('turn', { dir });
      return true;
    },

    status(ctx) {
      // кінець раунду: у кооперативі нема ні переможців, ні нічиєї — є довжина, до якої дотягли разом
      if (ctx.room.status === 'finished' && ctx.room.result && ctx.view && ctx.view.len)
        return 'Змійка доросла до ' + ctx.view.len;
      if (!ctx.playing) return '';
      const f = ctx.frame && ctx.frame.startIn != null ? ctx.frame
        : (ctx.view && ctx.view.startIn != null ? ctx.view : null);
      // на відліку теж нагадуємо, що крутиш саме ти: учотирьох у кожного своя одна стрілка
      if (f && f.startIn > 0) return ctx.mine ? 'Готуйсь… ти крутиш: ' + axisWord(ctx) : 'Готуйсь…';
      return ctx.mine ? 'Ти крутиш: ' + axisWord(ctx) : 'Дивишся збоку';
    },

    unmount(root) { root._coop = null; },
  });
  // =============================================================================================
  // Гуртом: «Мотоцикли гуртом» (tron-party) і «Змійки гуртом» (snake-party), 2–4 гравці
  // =============================================================================================
  //
  // Вид (Impl/SnakeParty.cs): { width, height, mode: 'tron'|'snake', t: int[][] (тіло на кожне місце),
  //   dirs, present: bool[], al: маска живих, crash: int[] (-1 — ще їде), place: (int|null)[], wins: int[],
  //   ap: int[] (яблука), startIn, winner: null|'win'|'draw', winners: int[] }.
  // Кадр мотоциклів — дельта: { h: int[] (голова на місце, -1 — нема), al, startIn, winner }; змійок — повний:
  //   { t, ap, al, startIn, winner }. Логіка виду й кадру та сама, що в дуелі мотоциклів: вид перекладаємо
  //   рівно раз за посиланням, кадри дописують голови з фільтром побачених клітинок.

  const PARTY_MS = { 'tron-party': 100, 'snake-party': 120 };
  /// Кольори місць і фігурки на голові: колір — для ока, фігурка — для тих, у кого кольори зливаються.
  const RIDERS = [
    { v: '--accent', f: '#f4c542', shape: 'circle', mark: '●' },
    { v: '--ok', f: '#7bd389', shape: 'square', mark: '■' },
    { v: '--moto2', f: '#6fb3e8', shape: 'triangle', mark: '▲' },
    { v: '--moto3', f: '#e88ac0', shape: 'diamond', mark: '◆' },
  ];

  function partyState(root, ctx) {
    if (!root._party) {
      root._party = {
        cv: null, view: null, w: W, h: H, mode: 'tron', css: ctx.css,
        t: [[], [], [], []], seen: [new Set(), new Set(), new Set(), new Set()],
        ap: [], al: 0, crash: [-1, -1, -1, -1], place: [], wins: [], present: [], startIn: 0, winner: null, winners: [],
      };
    }
    return root._party;
  }

  /// Поле буває більшим для трьох-чотирьох: розмір беремо з виду, канвас каркаса перелаштовується сам.
  function partyCanvas(root, st) {
    st.cv = HGames.ui.canvas(root, { w: st.w * PX, h: st.h * PX, cls: 'arenaboard' + (st.w > W ? ' big' : '') });
  }

  function applyPartyView(st, v) {
    st.view = v;
    st.w = v.width || W;
    st.h = v.height || H;
    st.mode = v.mode || 'tron';
    st.t = (v.t || []).map((b) => (b || []).slice());
    st.seen = st.t.map((b) => new Set(b));
    st.ap = (v.ap || []).slice();
    st.al = v.al || 0;
    st.crash = (v.crash || []).slice();
    st.place = (v.place || []).slice();
    st.wins = (v.wins || []).slice();
    st.present = (v.present || []).slice();
    st.startIn = v.startIn || 0;
    st.winner = v.winner == null ? null : v.winner;
    st.winners = (v.winners || []).slice();
  }

  /// Фігурка місця на голові — темним по кольору сліду, щоб читалась і без кольору.
  function shapeAt(g, x, y, shape, color) {
    const cx = x + PX / 2, cy = y + PX / 2, r = PX / 2 - 3.5;
    g.fillStyle = color;
    g.beginPath();
    if (shape === 'circle') g.arc(cx, cy, r, 0, Math.PI * 2);
    else if (shape === 'square') g.rect(cx - r, cy - r, r * 2, r * 2);
    else if (shape === 'triangle') { g.moveTo(cx, cy - r - 0.5); g.lineTo(cx + r + 0.5, cy + r); g.lineTo(cx - r - 0.5, cy + r); g.closePath(); }
    else { g.moveTo(cx, cy - r - 1); g.lineTo(cx + r + 1, cy); g.lineTo(cx, cy + r + 1); g.lineTo(cx - r - 1, cy); g.closePath(); }
    g.fill();
  }

  function drawParty(st, ctx, waiting) {
    if (!st.cv) return;
    const g = st.cv.ctx, w = st.w, cw = st.cv.w, ch = st.cv.h;
    const px = (cell) => [(cell % w) * PX, Math.floor(cell / w) * PX];
    g.fillStyle = st.css('--bg2', '#16291f');
    g.fillRect(0, 0, cw, ch);

    g.fillStyle = st.css('--clay', '#c5763a');
    for (const a of st.ap) {
      const [x, y] = px(a);
      g.beginPath();
      g.arc(x + PX / 2, y + PX / 2, PX / 2 - 2.5, 0, Math.PI * 2);
      g.fill();
    }

    const dark = st.css('--bg', '#0f1f18');
    st.t.forEach((cells, s) => {
      if (!cells || !cells.length) return;
      const r = RIDERS[s] || RIDERS[0];
      const alive = (st.al & (1 << s)) !== 0;
      g.globalAlpha = alive || st.winner != null ? 1 : 0.4;   // розбитий слід лишається стіною, але блідою
      g.fillStyle = st.css(r.v, r.f);
      if (st.mode === 'snake') {
        cells.forEach((cell, i) => {
          const [x, y] = px(cell);
          g.beginPath();
          g.roundRect(x + 1, y + 1, PX - 2, PX - 2, i ? 3 : 6);
          g.fill();
        });
      } else {
        for (const cell of cells) { const [x, y] = px(cell); g.fillRect(x, y, PX, PX); }
      }
      const [hx, hy] = px(cells[0]);
      shapeAt(g, hx, hy, r.shape, dark);
      g.globalAlpha = 1;
    });

    // хрестик там, де хтось розбився — поверх затемнення, щоб і в кінці раунду було видно, хто де злетів
    const crosses = () => {
      g.strokeStyle = st.css('--danger', '#e57373');
      g.lineWidth = 2.5;
      st.crash.forEach((cell, s) => {
        if (cell == null || cell < 0 || !st.present[s]) return;
        const [x, y] = px(cell);
        g.beginPath();
        g.moveTo(x + 3, y + 3); g.lineTo(x + PX - 3, y + PX - 3);
        g.moveTo(x + PX - 3, y + 3); g.lineTo(x + 3, y + PX - 3);
        g.stroke();
      });
    };

    const me = ctx.seat != null && st.present[ctx.seat] ? ctx.seat : null;
    const counting = st.startIn > 0 && !waiting && st.winner == null;
    if (!counting && st.winner == null) { crosses(); return; }

    g.fillStyle = st.css('--gshade', 'rgba(15, 31, 24, .62)');
    g.fillRect(0, 0, cw, ch);
    crosses();
    g.fillStyle = st.css('--text', '#ecf1ea');
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    if (counting) {
      // «ти тут»: на великому полі вчотирьох себе треба знайти за три секунди — кільце поверх затемнення
      if (me != null && st.t[me] && st.t[me].length) {
        const [x, y] = px(st.t[me][0]);
        g.strokeStyle = st.css('--text', '#ecf1ea');
        g.lineWidth = 2;
        g.beginPath();
        g.arc(x + PX / 2, y + PX / 2, PX * 1.15, 0, Math.PI * 2);
        g.stroke();
      }
      // велике поле на телефоні стискається майже вдвічі — підписи ростуть разом із ним, щоб лишитись читабельними
      const k = Math.max(1, cw / (W * PX));
      g.font = '700 ' + Math.round(46 * k) + 'px system-ui, sans-serif';
      g.fillText(String(Math.ceil((st.startIn * (PARTY_MS[ctx.room.game] || 100)) / 1000)), cw / 2, ch / 2);
      if (me != null) {
        const r = RIDERS[me];
        g.font = '600 ' + Math.round(19 * k) + 'px system-ui, sans-serif';
        g.fillStyle = st.css(r.v, r.f);
        g.fillText('ти — ' + r.mark + ' ' + ctx.seatName(me), cw / 2, ch / 2 + 42 * k);
      }
      return;
    }
    const who = (st.winners || []).map((s) => ctx.nickOf(s) || ctx.seatName(s));
    g.font = '700 ' + Math.round(26 * Math.max(1, cw / (W * PX))) + 'px system-ui, sans-serif';
    g.fillText(st.winner === 'draw' || !who.length ? 'Нічия' : '🏆 ' + who.join(' і '), cw / 2, ch / 2, cw - 24);
  }

  /// Табло: фігурка, нік і перемоги в серії на кожне місце; хто вибув — блідий і закреслений.
  function partyScore(root, ctx, st) {
    let el = root.querySelector(':scope > .ascore');
    if (!el) {
      el = document.createElement('div');
      el.className = 'ascore';
      root.insertBefore(el, root.firstChild);
    }
    const parts = [];
    for (let s = 0; s < 4; s++) {
      if (!st.present[s]) continue;
      const out = st.startIn <= 0 && !(st.al & (1 << s)) && st.place[s] !== 1;
      const nick = ctx.nickOf(s) || ctx.seatName(s);
      const cls = 'ar' + s + (out ? ' out' : '') + (ctx.seat === s ? ' me' : '');
      parts.push('<span class="' + cls + '"><i>' + RIDERS[s].mark + '</i>' + ctx.esc(nick)
        + ' <b>' + (st.wins[s] || 0) + '</b></span>');
    }
    const html = parts.join('');
    if (el.innerHTML !== html) el.innerHTML = html;
  }

  function registerParty(id, o) {
    HGames.register({
      id,
      icon: o.icon,
      seatNames: o.seats,
      seatClass: ['ar0', 'ar1', 'ar2', 'ar3'],
      pad: { dirs: true, hint: o.hint },
      news: o.news,

      mount(root, ctx) {
        const st = partyState(root, ctx);
        partyCanvas(root, st);
      },

      update(root, ctx) {
        const st = partyState(root, ctx);
        if (!st.cv) return;
        pad(root, ctx);
        const v = ctx.view;
        if (v && Array.isArray(v.t) && v !== st.view) applyPartyView(st, v);
        partyCanvas(root, st);
        partyScore(root, ctx, st);
        drawParty(st, ctx, !ctx.playing);
      },

      frame(root, ctx, f) {
        const st = partyState(root, ctx);
        if (!st.cv || !f) return;
        if (Array.isArray(f.h)) f.h.forEach((cell, s) => { if (cell >= 0 && st.t[s]) addHead(st.t[s], st.seen[s], cell); });
        if (Array.isArray(f.t)) st.t = f.t.map((b) => (b || []).slice());
        if (Array.isArray(f.ap)) st.ap = f.ap.slice();
        const was = st.al;
        st.al = f.al || 0;
        st.startIn = f.startIn || 0;
        st.winner = f.winner == null ? null : f.winner;
        if (was !== st.al) partyScore(root, ctx, st);
        drawParty(st, ctx, !ctx.playing);
      },

      onKey(e, ctx) {
        const dir = DIRS[e.code];
        if (dir === undefined || !ctx.mine || !ctx.playing) return false;
        ctx.input('turn', { dir });
        return true;
      },

      status(ctx) {
        if (!ctx.playing) return '';
        const f = ctx.frame && ctx.frame.startIn != null ? ctx.frame
          : (ctx.view && ctx.view.startIn != null ? ctx.view : null);
        if (f && f.startIn > 0) return 'Готуйсь…';
        if (!ctx.mine) return 'Дивишся збоку';
        const al = f && f.al != null ? f.al : 15;
        return al & (1 << ctx.seat) ? o.play : o.out;
      },

      unmount(root) { root._party = null; },
    });
  }

  registerParty('tron-party', {
    icon: '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
      + '<path d="M2 13h5V8h7" fill="none" stroke="var(--accent)" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>'
      + '<path d="M14 3H9v3" fill="none" stroke="var(--ok)" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>'
      + '<circle cx="14" cy="8" r="1.7" fill="var(--clay)"/></svg>',
    seats: ['жовтий', 'зелений', 'синій', 'рожевий'],
    hint: '{dpad} куди їхати',
    play: 'Стрілки або WASD — і не наїдь на чужий слід',
    out: 'Ти вибув — дивись, хто кого пережене',
    news: {
      v: '2026-09-24',
      title: 'Мотоцикли гуртом: до чотирьох на трасі',
      items: [
        '🏍 Нова плитка «Мотоцикли гуртом»: 2–4 гравці, у кожного свій колір і фігурка на голові',
        '💥 Врізався — вибув, але раунд триває: бере його останній, хто ще їде',
        '🗺 Утрьох і вчотирьох поле більше, 34×24; розмір можна обрати й самому',
        '🏆 Рахунок серії їде з вами через «Ще раз», а дуель «Мотоцикли» зі ставками лишилась як була',
      ],
    },
  });

  registerParty('snake-party', {
    icon: '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
      + '<path d="M2 13h4.2a2.6 2.6 0 0 0 0-5.2H5.2a2.6 2.6 0 0 1 0-5.2H9" fill="none" stroke="var(--accent)" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>'
      + '<path d="M13.5 14V9.5" fill="none" stroke="var(--ok)" stroke-width="2" stroke-linecap="round"/>'
      + '<circle cx="12.6" cy="3.4" r="1.9" fill="var(--clay)"/></svg>',
    seats: ['жовта', 'зелена', 'синя', 'рожева'],
    hint: '{dpad} куди повзти',
    play: 'Стрілки або WASD — їж яблука й не врізайся',
    out: 'Твоя змійка вибула — дивись, хто переживе решту',
    news: {
      v: '2026-09-24',
      title: 'Змійки гуртом: до чотирьох на полі',
      items: [
        '🐍 Нова плитка «Змійки гуртом»: 2–4 змійки, у кожної свій колір і фігурка',
        '🍎 Утрьох і вчотирьох на полі два яблука, а саме поле більше',
        '💥 Врізалась — вибула, раунд бере остання жива, а за три хвилини — найдовша',
        '🏆 Рахунок серії їде з вами через «Ще раз»',
      ],
    },
  });
})();
