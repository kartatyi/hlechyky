/*
  Режими змійки в одному файлі: «Мотоцикли» (tron), «Змійка на всіх» (snake-coop) і «…гуртом» на 2–4.
  Каркас дозволяє кілька register в одному модулі, тому обидві гри кажуть Client: "snake-modes"
  (Impl/SnakeModes.cs), і окремих tron.js / snake-coop.js не існує.

  Мотоцикли (tron, з проходу №3) — той самий вид і кадр, що й «…гуртом», лише на двох (див. розділ унизу):
  сервер їх веде на одному ядрі. Кадр — ДЕЛЬТА (голови), сліди тримає клієнт, вид перекладаємо рівно раз
  за посиланням, а множина побачених клітинок не дає кадру, що розминувся зі свіжішим видом, дописати голову вдруге.

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

  /// Палітра з CSS-змінних: getComputedStyle — раз на колір і вид, а не на кожен кадр (десять разів на
  /// секунду по десятку змінних). update() кожної картки бере свіжу — так нова тема підхопиться з першою ж подією.
  function palette(css) {
    const m = new Map();
    return (name, fallback) => {
      let v = m.get(name);
      if (v === undefined) { v = css(name, fallback); m.set(name, v); }
      return v;
    };
  }

  /// «Що нового» (прохід 28.09): поворот на екрані одразу, свайп і хрестовина на дотик, автоповтор не губить поворотів.
  const NEWS_TRON = {
    v: '2026-09-29',
    title: 'Мотоцикли: серія до 3/5, мапи й турбо',
    items: [
      '🏁 Партія «до 3/5 перемог» — одна ставка на всю серію, раунди йдуть самі, без «Ще раз»',
      '🏛 Мапи «Колони», «Хрест», «Тор» (край наскрізь) або випадкова щораунду',
      '🚀 Турбо: пробіл, Ⓐ чи подвійний тап — секунда подвійної швидкості, потім 5 с перезарядки',
      '✂ Хто кого підрізав — під табло і в Журналі, а ⏪ після аварії — повільний повтор (тап двічі по полю — ще раз)',
    ],
  };

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

  /// HTML елемента — лише коли рядок справді змінився. Порівнювати з el.innerHTML не можна: браузер
  /// серіалізує його по-своєму (апостроф із esc → «'», а не «&#39;»), і «однаково» не виходило б ніколи —
  /// табло перебудовувалось би на кожен кадр.
  function html(el, s) {
    if (el._h === s) return;
    el._h = s;
    el.innerHTML = s;
  }

  /// Рядок над полем: рахунок серії в мотоциклах, довжина змійки в коопі.
  function score(root, s) {
    let el = root.querySelector(':scope > .gscore');
    if (!el) {
      el = document.createElement('div');
      el.className = 'gscore';
      root.insertBefore(el, root.firstChild);
    }
    html(el, s);
  }

  /// Хрестовина потрібна лише тому, хто грає: сів глядач за стіл — вона з'явиться на наступному 'room'.
  /// Кнопки — каркасні (ui.dpad спрацьовує на дотик, а не на відпускання); поворот іде через turn гри, щоб
  /// своя голова повертала на екрані одразу.
  function pad(root, ctx, turn, dirs) {
    if (!ctx.mine) { const d = root.querySelector(':scope > .dpad'); if (d) d.remove(); return; }
    const el = HGames.ui.dpad(root, turn, dirs);
    // без подвійного тапу-зуму, коли швидко тиснуть сусідні стрілки
    if (el && el.style.touchAction !== 'manipulation') el.style.touchAction = 'manipulation';
  }

  /// Свайп по полю: провів пальцем щонайменше SWIPE_PX — поворот у бік переважної осі. Не відриваючи пальця,
  /// можна крутити далі: відлік іде від точки останнього повороту. turnOf() — живий обробник картки (null —
  /// зараз не граєш), бо канвас переживає стан картки. Прокрутку пальцем по полю забираємо лише в того, хто
  /// грає (touch-action: none), — глядач гортає сторінку як завжди.
  const SWIPE_PX = 18;
  function swipe(el, turnOf, tapOf) {
    if (el._swipe) return;
    el._swipe = true;
    let from = null, lastTap = 0;
    el.addEventListener('pointerdown', (e) => {
      if (e.button > 0) return;
      from = { x: e.clientX, y: e.clientY, id: e.pointerId, at: performance.now(), moved: false };
      if (!turnOf()) return;
      try { el.setPointerCapture(e.pointerId); } catch { /* стара миша без capture */ }
    });
    el.addEventListener('pointermove', (e) => {
      if (!from || e.pointerId !== from.id || !turnOf()) return;
      const dx = e.clientX - from.x, dy = e.clientY - from.y;
      if (Math.max(Math.abs(dx), Math.abs(dy)) < SWIPE_PX) return;
      from.x = e.clientX;
      from.y = e.clientY;
      from.moved = true;
      const turn = turnOf();
      if (turn) turn(Math.abs(dx) > Math.abs(dy) ? (dx > 0 ? 0 : 2) : (dy > 0 ? 1 : 3));
    });
    // подвійний тап (без свайпу між ними) — турбо в раунді чи повтор після нього
    el.addEventListener('pointerup', (e) => {
      if (!from || e.pointerId !== from.id) return;
      const tap = !from.moved && performance.now() - from.at < 280;
      from = null;
      if (!tap || !tapOf) return;
      const now = performance.now();
      if (now - lastTap < 330) { lastTap = 0; tapOf(); } else lastTap = now;
    });
    el.addEventListener('pointercancel', (e) => { if (from && e.pointerId === from.id) from = null; });
  }
  function touchable(el, on) {
    const want = on ? 'none' : '';
    if (el.style.touchAction !== want) el.style.touchAction = want;
  }

  /// Куди дивиться голова — з голови й «шиї»; null, поки тіло коротше за дві клітинки.
  function heading(cells, w) {
    if (!cells || cells.length < 2) return null;
    const a = cells[0], b = cells[1];
    const dx = (a % w) - (b % w), dy = Math.floor(a / w) - Math.floor(b / w);
    return dx === 1 ? 0 : dy === 1 ? 1 : dx === -1 ? 2 : dy === -1 ? 3 : null;
  }

  /// Дзеркало серверної черги поворотів (SnakeCore.Turn: розворот і повтор не беремо, у черзі — до двох).
  /// Лише для малюнка: своя голова «дивиться» туди, куди щойно натиснули, ще до того, як сервер зробив крок і
  /// кадр доїхав, — так поворот відчувається одразу, а не за тик плюс дорогу. Суддя — сервер: кадр, у якому
  /// голова справді повернула, знімає поворот із черги, а непідтверджений за 400 мс просто забувається.
  function queueTurn(st, cur, dir) {
    const now = performance.now();
    st.q = st.q.filter((t) => now - t.at < 400);
    const last = st.q.length ? st.q[st.q.length - 1].dir : cur;
    if (st.q.length >= 2 || last == null || dir === last || (dir + 2) % 4 === last) return;
    st.q.push({ dir, at: now });
  }
  /// Кадр приїхав: голова тепер дивиться в cur. Збігся з першим у черзі — поворот підтверджено.
  function settleTurns(st, cur) {
    const now = performance.now();
    st.q = st.q.filter((t) => now - t.at < 400);
    if (st.q.length && st.q[0].dir === cur) st.q.shift();
  }
  function facing(st, cur) {
    const t = st.q[0];
    return t && performance.now() - t.at < 400 ? t.dir : cur;
  }

  const STEP = [[1, 0], [0, 1], [-1, 0], [0, -1]];
  /// «Фара» на голові: трикутник вістрям до переднього краю клітинки — куди вершник зараз їде.
  function nose(g, x, y, dir, color, size) {
    const c = PX / 2, r = PX * (size || 0.3);
    const [dx, dy] = STEP[dir];
    const tx = x + c + dx * (c - 2), ty = y + c + dy * (c - 2);
    const bx = tx - dx * r * 1.6, by = ty - dy * r * 1.6;
    g.fillStyle = color;
    g.beginPath();
    g.moveTo(tx, ty);
    g.lineTo(bx - dy * r, by + dx * r);
    g.lineTo(bx + dy * r, by - dx * r);
    g.closePath();
    g.fill();
  }

  /// Голова з кадру. Множина побачених клітинок ловить два випадки: відлік (голова стоїть на місці) і
  /// кадр, що розминувся зі свіжішим видом. Слід не зникає, тож двічі в одну клітинку мотоцикл не заїде.
  function addHead(cells, seen, cell) {
    if (cell == null || seen.has(cell)) return;
    seen.add(cell);
    cells.unshift(cell);
  }

  // =============================================================================================
  // Змійка на всіх (1–4 за кермом однієї змійки)
  // =============================================================================================

  function coopState(root, ctx) {
    // view — вид, який уже застосовано (див. пояснення в applyPartyView і partyState): кадр коопа завжди свіжіший за
    // кешований вид, тож давати виду перебивати його на кожну 'rooms' означало б смикати змійку назад.
    if (!root._coop) root._coop = { cv: null, view: null, last: null, css: palette(ctx.css) };
    root._coop.ctx = ctx;
    return root._coop;
  }

  /// Поворот у коопі: лише свої стрілки — чужі сервер усе одно не прийме.
  function coopTurn(ctx, dir) {
    if (ctx && ctx.mine && ctx.playing && ownKey(ctx, dir)) ctx.input('turn', { dir });
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
      v: '2026-09-28',
      title: 'Змійка на всіх: свайпом по полю',
      items: [
        '👆 На телефоні крути свайпом просто по полю — чужі напрямки змійка й так пропустить повз вуха',
        '⚡ Кнопки під полем спрацьовують на дотик, а не на відпускання',
        '⌨️ Затиснута стрілка більше не з\'їдає наступного повороту',
      ],
    },

    mount(root, ctx) {
      const st = coopState(root, ctx);
      st.cv = HGames.ui.canvas(root, { w: W * PX, h: H * PX, cls: 'arenaboard' });
      swipe(st.cv.el, () => { const s = root._coop; return s && s.ctx.mine && s.ctx.playing ? (d) => coopTurn(s.ctx, d) : null; });
    },

    update(root, ctx) {
      const st = coopState(root, ctx);
      if (!st.cv) return;
      st.css = palette(ctx.css);
      // на пальці показуємо лише свої кнопки: чужі сервер усе одно не прийме
      pad(root, ctx, (d) => coopTurn(ctx, d), padDirs(keysOf(ctx)));
      touchable(st.cv.el, ctx.mine && ctx.playing);
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
      // Автоповтор затиснутої клавіші — теж: він лише їв би квоту Input (див. мотоцикли).
      if (!ownKey(ctx, dir) || e.repeat) return true;
      coopTurn(ctx, dir);
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
  // Мотоцикли (tron — удвох, на ставку й Ело) і «…гуртом» (tron-party, snake-party, 1–4 гравці)
  // =============================================================================================
  //
  // Вид (Impl/SnakeParty.cs): { width, height, mode: 'tron'|'snake', t: int[][] (тіло на кожне місце),
  //   dirs, present: bool[], al: маска живих, crash: int[] (-1 — ще їде), place: (int|null)[], wins: int[],
  //   ap: int[] (яблука), startIn, winner, winners: int[] }. Мотоцикли ще: cuts (підрізи), ko/kt («хто кого»
  //   цього раунду: [жертва, чий слід, як] і готові рядки), map, walls, wrap; з опціями столу — ser/round/over
  //   (серія до N), nx (табло між раундами), tb (турбо: >0 діє, <0 перезарядка, 0 готове), rg/sq (кілець
  //   звуження і кроків до наступного), bots (імена 🤖 на місцях), teams (команда місця). Дуель (tron) — той
  //   самий вид + duel, winsA/winsB і winner 'x'|'o'|'draw' (гурт каже 'win'|'draw').
  // Кадр мотоциклів — дельта: { h: int[] (голова на місце, -1 — нема), h2? (перша клітинка подвійного кроку
  //   турбо), al, startIn, winner, tb?, rg?, sq?, nx? }; змійок — повний: { t, ap, al, startIn, winner }.
  //   Логіка виду й кадру: вид перекладаємо рівно раз за посиланням, кадри дописують голови з фільтром
  //   побачених клітинок (слід не зникає — двічі в одну клітинку мотоцикл не заїде).
  // Повтор (прохід №3): клієнт пам'ятає довжину кожного сліду на кожен кадр; після аварії, що закрила раунд,
  //   програє останні 2 с удвічі повільніше, обрізаючи сліди до тодішньої довжини. Тап по полю — ще раз.

  const PARTY_MS = { tron: TRON_MS, 'tron-party': TRON_MS, 'snake-party': COOP_MS };
  /// Кольори місць і фігурки на голові: колір — для ока, фігурка — для тих, у кого кольори зливаються.
  const RIDERS = [
    { v: '--accent', f: '#f4c542', shape: 'circle', mark: '●' },
    { v: '--ok', f: '#7bd389', shape: 'square', mark: '■' },
    { v: '--moto2', f: '#6fb3e8', shape: 'triangle', mark: '▲' },
    { v: '--moto3', f: '#e88ac0', shape: 'diamond', mark: '◆' },
  ];
  const TEAMS = ['🔥 Жар', '❄️ Іній'];
  /// Повтор: кадр гри — 100 мс, повтор — 200 (удвічі повільніше). Скільки кадрів: 2 с, а між раундами серії,
  /// де табло стоїть лише 2 с, — остання секунда.
  const REPLAY_MS = 200, REPLAY_LONG = 20, REPLAY_SHORT = 8, HIST_MAX = 32;

  function partyState(root, ctx) {
    if (!root._party) {
      root._party = {
        cv: null, view: null, w: W, h: H, mode: 'tron', css: palette(ctx.css),
        t: [[], [], [], []], seen: [new Set(), new Set(), new Set(), new Set()],
        ap: [], al: 0, crash: [-1, -1, -1, -1], place: [], wins: [], cuts: [], present: [], startIn: 0, winner: null, winners: [],
        walls: [], wrap: false, rg: 0, sq: -1, tb: null, nx: 0, ser: 0, round: 0, over: false, bots: [], teams: null, kt: [],
        hist: [], rp: null, rpT: 0, rpDone: false, duel: false,
        q: [], waiting: true,
      };
    }
    root._party.ctx = ctx;
    ctx._moto = root._party;
    return root._party;
  }

  /// Ім'я місця: нік, 🤖 або колір.
  const nameOf = (st, ctx, s) => ctx.nickOf(s) || (st.bots && st.bots[s]) || ctx.seatName(s);

  /// Моє тіло в цьому раунді (null — глядач, мене нема на полі або я вже вибув).
  function partyMine(st) {
    const s = st.ctx.mine ? st.ctx.seat : null;
    return s != null && st.present[s] && (st.al & (1 << s)) ? st.t[s] : null;
  }

  /// Поворот гурту — на сервер завжди, у дзеркало черги — для «фари» на своїй голові.
  function partyTurn(st, dir) {
    const ctx = st.ctx;
    if (!ctx || !ctx.mine || !ctx.playing || !(dir >= 0 && dir <= 3)) return;
    ctx.input('turn', { dir });
    const mine = partyMine(st);
    if (!mine || st.winner != null) return;
    queueTurn(st, heading(mine, st.w), dir);
    drawParty(st, ctx, st.waiting);
  }

  /// Турбо: лише коли стіл із турбо, ти їдеш і воно заряджене (суддя все одно сервер).
  function partyBoost(st) {
    const ctx = st.ctx;
    if (!ctx || !ctx.mine || !ctx.playing || !st.tb || !partyMine(st) || st.winner != null) return false;
    if ((st.tb[ctx.seat] || 0) !== 0) return true;
    ctx.input('turbo', {});
    return true;
  }

  /// Поле буває більшим для трьох-чотирьох: розмір беремо з виду, канвас каркаса перелаштовується сам.
  function partyCanvas(root, st) {
    st.cv = HGames.ui.canvas(root, { w: st.w * PX, h: st.h * PX, cls: 'arenaboard' + (st.w > W ? ' big' : '') });
  }

  /// Довжини слідів зараз — одна «фотографія» для повтору.
  function snap(st) {
    return { len: st.t.map((b) => (b ? b.length : 0)), al: st.al, rg: st.rg };
  }
  /// Кадр чи вид змінив поле — дописуємо фотографію. Сліди коротшають лише з новим раундом — тоді історія з нуля.
  function remember(st) {
    const s = snap(st), last = st.hist[st.hist.length - 1];
    if (last && s.len.some((n, i) => n < (last.len[i] || 0))) st.hist = [];
    if (last && st.hist.length && s.al === last.al && s.rg === last.rg && s.len.every((n, i) => n === last.len[i])) return;
    st.hist.push(s);
    if (st.hist.length > HIST_MAX) st.hist.shift();
  }

  function stopReplay(st) {
    if (st.rpT) clearTimeout(st.rpT);
    st.rpT = 0;
    st.rp = null;
  }
  /// Повільний повтор останніх секунд раунду. Лише мотоцикли: у змійок хвіст зникає, довжини не досить.
  function replay(st, ctx) {
    stopReplay(st);
    if (st.mode !== 'tron' || st.hist.length < 3 || document.hidden) return;
    const n = st.ser && !st.over ? REPLAY_SHORT : REPLAY_LONG;
    const end = st.hist.length - 1;
    st.rp = { i: Math.max(0, end - n), end };
    const step = () => {
      if (!st.rp || st.ctx !== ctx) { st.rpT = 0; return; }
      drawParty(st, ctx, st.waiting);
      if (st.rp.i >= st.rp.end) { st.rp = null; st.rpT = 0; drawParty(st, ctx, st.waiting); return; }
      st.rp.i++;
      st.rpT = setTimeout(step, REPLAY_MS);
    };
    st.rpT = setTimeout(step, 250);   // мить на «ой» — і повтор
  }
  /// Раунд щойно скінчився — раз на раунд пускаємо повтор.
  function maybeReplay(st, ctx, was) {
    if (st.winner == null) { st.rpDone = false; return; }
    if (was == null && !st.rpDone) { st.rpDone = true; replay(st, ctx); }
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
    st.cuts = (v.cuts || []).slice();
    st.present = (v.present || []).slice();
    st.startIn = v.startIn || 0;
    st.winner = v.winner == null ? null : v.winner;
    st.winners = (v.winners || []).slice();
    st.walls = (v.walls || []).slice();
    st.wrap = !!v.wrap;
    st.rg = v.rg || 0;
    st.sq = v.sq == null ? -1 : v.sq;
    st.tb = Array.isArray(v.tb) ? v.tb.slice() : null;
    st.nx = v.nx || 0;
    st.ser = v.ser || 0;
    st.round = v.round || 0;
    st.over = !!v.over;
    st.bots = (v.bots || []).slice();
    st.teams = Array.isArray(v.teams) ? v.teams.slice() : null;
    st.noteams = !!v.noteams;
    st.kt = (v.kt || []).slice();
    st.duel = !!v.duel;
    st.mapName = v.map || '';
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

  /// Кільце k звуження — рамка клітинок завтовшки одну клітинку.
  function ring(g, st, k) {
    const x = k * PX, y = k * PX, w = (st.w - 2 * k) * PX, h = (st.h - 2 * k) * PX;
    if (w <= 0 || h <= 0) return;
    g.fillRect(x, y, w, PX);
    g.fillRect(x, y + h - PX, w, PX);
    g.fillRect(x, y + PX, PX, h - 2 * PX);
    g.fillRect(x + w - PX, y + PX, PX, h - 2 * PX);
  }

  function drawParty(st, ctx, waiting) {
    if (!st.cv) return;
    st.waiting = waiting;
    const rp = st.rp ? st.hist[st.rp.i] : null;
    const al = rp ? rp.al : st.al, rg = rp ? rp.rg : st.rg;
    const g = st.cv.ctx, w = st.w, cw = st.cv.w, ch = st.cv.h;
    // клітинка → пікселі без масиву на кожну клітинку: кадр малює сотні клітинок десять разів на секунду
    const cx = (cell) => (cell % w) * PX, cy = (cell) => Math.floor(cell / w) * PX;
    g.fillStyle = st.css('--bg2', '#16291f');
    g.fillRect(0, 0, cw, ch);

    // мапа й звуження — тим самим «камінням»: колір тексту, приглушений (у світлій темі — сірий, у темній — світлий)
    const stone = st.css('--text', '#ecf1ea');
    if (st.walls.length || rg > 0) {
      g.fillStyle = stone;
      g.globalAlpha = 0.3;
      for (const c of st.walls) g.fillRect(cx(c), cy(c), PX, PX);
      for (let k = 0; k < rg; k++) ring(g, st, k);
      g.globalAlpha = 1;
    }
    // наступне кільце блимає в останню секунду перед тим, як вирости (кадр щотика — блимаємо парністю)
    if (!rp && st.winner == null && st.sq > 0 && st.sq <= 10) {
      g.fillStyle = st.css('--danger', '#e57373');
      g.globalAlpha = st.sq % 2 ? 0.45 : 0.18;
      ring(g, st, rg);
      g.globalAlpha = 1;
    }
    if (st.wrap) {
      // тор: краю нема — пунктирна рамка каже «проїжджай наскрізь»
      g.strokeStyle = stone;
      g.globalAlpha = 0.45;
      g.lineWidth = 2;
      g.setLineDash([6, 6]);
      g.strokeRect(1, 1, cw - 2, ch - 2);
      g.setLineDash([]);
      g.globalAlpha = 1;
    }

    g.fillStyle = st.css('--clay', '#c5763a');
    for (const a of st.ap) {
      g.beginPath();
      g.arc(cx(a) + PX / 2, cy(a) + PX / 2, PX / 2 - 2.5, 0, Math.PI * 2);
      g.fill();
    }

    const dark = st.css('--bg', '#0f1f18');
    const light = st.css('--text', '#ecf1ea');
    const mine = partyMine(st);
    for (let s = 0; s < st.t.length; s++) {
      const cells = st.t[s];
      if (!cells || !cells.length) continue;
      const from = rp ? Math.max(0, cells.length - (rp.len[s] || 0)) : 0;
      if (from >= cells.length) continue;
      const r = RIDERS[s] || RIDERS[0];
      const alive = (al & (1 << s)) !== 0;
      g.globalAlpha = alive || (!rp && st.winner != null) ? 1 : 0.4;   // розбитий слід лишається стіною, але блідою
      g.fillStyle = st.css(r.v, r.f);
      if (st.mode === 'snake') {
        for (let i = 0; i < cells.length; i++) {
          g.beginPath();
          g.roundRect(cx(cells[i]) + 1, cy(cells[i]) + 1, PX - 2, PX - 2, i ? 3 : 6);
          g.fill();
        }
      } else {
        for (let i = from; i < cells.length; i++) g.fillRect(cx(cells[i]), cy(cells[i]), PX, PX);
      }
      const head = cells[from];
      const hx = cx(head), hy = cy(head);
      // турбо — світле сяйво довкола голови, поки діє
      if (!rp && alive && st.tb && st.tb[s] > 0) {
        g.strokeStyle = light;
        g.lineWidth = 2;
        g.strokeRect(hx - 2, hy - 2, PX + 4, PX + 4);
      }
      if (st.duel && alive) {
        // дуель: фара на обох — видно, куди їде суперник; своя — туди, куди щойно натиснув
        const d0 = heading(from ? cells.slice(from, from + 2) : cells, w);
        const d = !rp && cells === mine && st.winner == null ? facing(st, d0) : d0;
        if (d != null) nose(g, hx, hy, d, light, 0.34);
        else shapeAt(g, hx, hy, r.shape, dark);
      } else {
        shapeAt(g, hx, hy, r.shape, dark);
        // своя голова — ще й зі світлою «фарою» туди, куди щойно натиснув (не чекаючи тика сервера)
        if (!rp && cells === mine && st.winner == null) {
          const d = facing(st, heading(cells, w));
          if (d != null) nose(g, hx, hy, d, light, 0.24);
        }
      }
      g.globalAlpha = 1;
    }

    const k = Math.max(1, cw / (W * PX));   // велике поле на телефоні стискається — підписи ростуть разом із ним
    g.textBaseline = 'middle';
    if (rp) {
      g.textAlign = 'left';
      g.font = '700 ' + Math.round(15 * k) + 'px system-ui, sans-serif';
      g.fillStyle = light;
      g.globalAlpha = 0.85;
      g.fillText('⏪ повтор ×½', 8, 14 * k);
      g.globalAlpha = 1;
      return;
    }

    // хрестик там, де хтось розбився — поверх затемнення, щоб і в кінці раунду було видно, хто де злетів
    const crosses = () => {
      g.strokeStyle = st.css('--danger', '#e57373');
      g.lineWidth = 2.5;
      st.crash.forEach((cell, s) => {
        if (cell == null || cell < 0 || !st.present[s]) return;
        const x = cx(cell), y = cy(cell);
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
    g.fillStyle = light;
    g.textAlign = 'center';
    if (counting) {
      // «ти тут»: на великому полі вчотирьох себе треба знайти за три секунди — кільце поверх затемнення
      if (me != null && st.t[me] && st.t[me].length) {
        const x = cx(st.t[me][0]), y = cy(st.t[me][0]);
        g.strokeStyle = light;
        g.lineWidth = 2;
        g.beginPath();
        g.arc(x + PX / 2, y + PX / 2, PX * 1.15, 0, Math.PI * 2);
        g.stroke();
      }
      g.font = '700 ' + Math.round(46 * k) + 'px system-ui, sans-serif';
      g.fillText(String(Math.ceil((st.startIn * (PARTY_MS[ctx.room.game] || 100)) / 1000)), cw / 2, ch / 2);
      g.font = '600 ' + Math.round(17 * k) + 'px system-ui, sans-serif';
      const top = [];
      if (st.ser) top.push('Раунд ' + st.round + ' · до ' + st.ser + ' перемог');
      if (st.mapName && st.mapName !== 'empty') top.push(MAPS[st.mapName] || '');
      if (top.length) g.fillText(top.filter(Boolean).join(' · '), cw / 2, 20 * k, cw - 16);   // угорі — щоб не лізти на кільце «ти тут»
      if (me != null) {
        const r = RIDERS[me];
        g.font = '600 ' + Math.round(19 * k) + 'px system-ui, sans-serif';
        g.fillStyle = st.css(r.v, r.f);
        const team = st.teams && st.teams[me] >= 0 ? ' · ' + TEAMS[st.teams[me]] : '';
        g.fillText('ти — ' + r.mark + ' ' + ctx.seatName(me) + team, cw / 2, ch / 2 + 42 * k, cw - 16);
      }
      return;
    }
    const who = (st.winners || []).map((s) => nameOf(st, ctx, s));
    const team = st.teams && st.winners.length && st.teams[st.winners[0]] >= 0 ? TEAMS[st.teams[st.winners[0]]] : '';
    const title = st.winner === 'draw' || !who.length ? 'Нічия' : '🏆 ' + (team ? team + ': ' : '') + who.join(' і ');
    g.font = '700 ' + Math.round(26 * k) + 'px system-ui, sans-serif';
    g.fillText(title, cw / 2, ch / 2 - (st.ser ? 16 * k : 0), cw - 24);
    if (st.ser) {
      g.font = '600 ' + Math.round(16 * k) + 'px system-ui, sans-serif';
      const line = st.present.map((p, s) => (p ? nameOf(st, ctx, s) + ' ' + (st.wins[s] || 0) : null)).filter(Boolean).join(' · ');
      g.fillText(st.over ? 'серію взято! ' + line : line + ' — до ' + st.ser, cw / 2, ch / 2 + 20 * k, cw - 24);
    }
  }
  const MAPS = { columns: '🏛 колони', cross: '➕ хрест', torus: '🍩 тор: край наскрізь' };

  /// Табло: фігурка, нік, перемоги (у серії — раунди серії), ✂ підрізи, 🚀 турбо; хто вибув — блідий і закреслений.
  function partyScore(root, ctx, st) {
    let el = root.querySelector(':scope > .ascore');
    if (!el) {
      el = document.createElement('div');
      el.className = 'ascore';
      root.insertBefore(el, root.firstChild);
    }
    const parts = [];
    for (let s = 0; s < st.present.length; s++) {
      if (!st.present[s]) continue;
      const out = st.startIn <= 0 && !(st.al & (1 << s)) && st.place[s] !== 1;
      const cls = 'ar' + s + (out ? ' out' : '') + (ctx.seat === s ? ' me' : '');
      const team = st.teams && st.teams[s] >= 0 ? TEAMS[st.teams[s]].split(' ')[0] + ' ' : '';
      const cut = st.cuts[s] ? ' <em title="підрізів">✂' + st.cuts[s] + '</em>' : '';
      const tb = st.tb ? (st.tb[s] > 0 ? ' <u class="moto-tb on">🚀</u>' : st.tb[s] < 0 ? ' <u class="moto-tb">🚀</u>' : ' <u class="moto-tb ok">🚀</u>') : '';
      parts.push('<span class="' + cls + '"><i>' + RIDERS[s].mark + '</i>' + team + ctx.esc(nameOf(st, ctx, s))
        + ' <b>' + (st.wins[s] || 0) + '</b>' + cut + tb + '</span>');
    }
    html(el, parts.join(''));
    // стрічка «хто кого» цього раунду — під табло; місце під неї тримаємо завжди, щоб поле не стрибало
    if (st.mode !== 'tron') return;
    let feed = root.querySelector(':scope > .moto-feed');
    if (!feed) {
      feed = document.createElement('div');
      feed.className = 'moto-feed';
      el.after(feed);
    }
    const note = st.noteams ? '<span>команд не вийшло — кожен сам</span>' : '';
    html(feed, note + st.kt.slice(-3).map((t) => '<span>' + ctx.esc(t) + '</span>').join(''));
  }

  /// Кнопка турбо для пальця (на клавіатурі — пробіл, на паді — Ⓐ, ще — подвійний тап по полю). Живе в порожній
  /// верхній правій клітинці хрестовини: окремий рядок під полем зсував хрестовину під нижнє меню телефона.
  function boostButton(root, st, on) {
    let b = root.querySelector('.moto-boost');
    const dpad = root.querySelector(':scope > .dpad');
    if (!on || !dpad) { if (b) b.remove(); return; }
    if (!b || b.parentElement !== dpad) {
      if (b) b.remove();
      b = document.createElement('button');
      b.type = 'button';
      b.className = 'moto-boost';
      b.textContent = '🚀';
      b.title = 'Турбо';
      b.setAttribute('aria-label', 'Турбо');
      b.addEventListener('pointerdown', (e) => { e.preventDefault(); e.stopPropagation(); const s = root._party; if (s) partyBoost(s); });
      b.addEventListener('click', (e) => e.stopPropagation());   // хрестовина слухає кліки своїх кнопок — ця не її
      dpad.appendChild(b);
    }
    const ready = st.tb && (st.tb[st.ctx.seat] || 0) === 0;
    if (b.disabled === ready) b.disabled = !ready;
  }

  function registerParty(id, o) {
    HGames.register({
      id,
      icon: o.icon,
      seatNames: o.seats,
      seatClass: ['ar0', 'ar1', 'ar2', 'ar3'],
      pad: { dirs: true, a: 'Space', hint: o.hint },
      news: o.news,

      mount(root, ctx) {
        const st = partyState(root, ctx);
        partyCanvas(root, st);
        swipe(st.cv.el, () => { const s = root._party; return s && s.ctx.mine && s.ctx.playing ? (d) => partyTurn(s, d) : null; },
          () => {
            const s = root._party;
            if (!s) return;
            // подвійний тап: турбо в живому раунді, повтор — після нього
            if (s.winner != null) replay(s, s.ctx); else partyBoost(s);
          });
      },

      update(root, ctx) {
        const st = partyState(root, ctx);
        if (!st.cv) return;
        st.css = palette(ctx.css);
        pad(root, ctx, (d) => partyTurn(st, d));
        const v = ctx.view;
        if (v && Array.isArray(v.t) && v !== st.view) {
          const was = st.winner;
          applyPartyView(st, v);
          if (st.startIn > 0 || st.winner != null) st.q = [];
          if (st.winner == null) stopReplay(st);
          remember(st);
          maybeReplay(st, ctx, was);
        }
        partyCanvas(root, st);
        touchable(st.cv.el, ctx.mine && ctx.playing);
        partyScore(root, ctx, st);
        boostButton(root, st, !!st.tb && ctx.mine && ctx.playing && st.winner == null && !!partyMine(st));
        if (!st.rp) drawParty(st, ctx, !ctx.playing);
      },

      frame(root, ctx, f) {
        const st = partyState(root, ctx);
        if (!st.cv || !f) return;
        if (Array.isArray(f.h)) {
          f.h.forEach((cell, s) => {
            if (cell < 0 || !st.t[s]) return;
            if (Array.isArray(f.h2) && f.h2[s] >= 0) addHead(st.t[s], st.seen[s], f.h2[s]);
            addHead(st.t[s], st.seen[s], cell);
          });
        }
        if (Array.isArray(f.t)) st.t = f.t.map((b) => (b || []).slice());
        if (Array.isArray(f.ap)) st.ap = f.ap.slice();
        const was = st.al, wasWinner = st.winner, wasTb = st.tb ? st.tb.map((n) => Math.sign(n)).join() : '';
        st.al = f.al || 0;
        st.startIn = f.startIn || 0;
        st.winner = f.winner == null ? null : f.winner;
        if (f.rg != null) st.rg = f.rg;
        st.sq = f.sq == null ? -1 : f.sq;
        st.nx = f.nx || 0;
        if (Array.isArray(f.tb)) st.tb = f.tb;
        const nowTb = st.tb ? st.tb.map((n) => Math.sign(n)).join() : '';
        if (was !== st.al || wasTb !== nowTb) {
          partyScore(root, ctx, st);
          boostButton(root, st, !!st.tb && ctx.mine && ctx.playing && st.winner == null && !!partyMine(st));
        }
        const mine = partyMine(st);
        if (mine) settleTurns(st, heading(mine, st.w));
        remember(st);
        maybeReplay(st, ctx, wasWinner);
        if (!st.rp) drawParty(st, ctx, !ctx.playing);
      },

      onKey(e, ctx) {
        if (e.code === 'Space') {
          const st = ctx._moto;
          if (!st || !st.tb || !ctx.mine || !ctx.playing) return false;
          if (!e.repeat) partyBoost(st);
          return true;
        }
        const dir = DIRS[e.code];
        if (dir === undefined || !ctx.mine || !ctx.playing) return false;
        if (e.repeat) return true;   // автоповтор лише їв би квоту Input (затиснута стрілка — ~30 на секунду)
        if (ctx._moto) partyTurn(ctx._moto, dir); else ctx.input('turn', { dir });
        return true;
      },

      status(ctx) {
        if (!ctx.playing) return '';
        const st = ctx._moto;
        const f = ctx.frame && ctx.frame.startIn != null ? ctx.frame
          : (ctx.view && ctx.view.startIn != null ? ctx.view : null);
        if (f && f.winner != null && f.nx > 0) return 'Табло — і наступний раунд сам, без «Ще раз»';
        if (f && f.startIn > 0) return st && st.ser ? 'Готуйсь… раунд ' + st.round + ', граємо до ' + st.ser + ' перемог' : 'Готуйсь…';
        if (!ctx.mine) return 'Дивишся збоку';
        const al = f && f.al != null ? f.al : 15;
        if (!(al & (1 << ctx.seat))) return o.out;
        if (st && st.tb) {
          const t = st.tb[ctx.seat] || 0;
          return o.play + (t > 0 ? ' · 🚀 жени!' : t < 0 ? ' · 🚀 за ' + Math.ceil((-t * TRON_MS) / 1000) + ' с' : ' · 🚀 пробіл / Ⓐ / подвійний тап — турбо');
        }
        return o.play;
      },

      unmount(root) {
        if (root._party) stopReplay(root._party);
        root._party = null;
      },
    });
  }

  const TRON_PARTY_ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M2 13h5V8h7" fill="none" stroke="var(--accent)" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>'
    + '<path d="M14 3H9v3" fill="none" stroke="var(--ok)" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>'
    + '<circle cx="14" cy="8" r="1.7" fill="var(--clay)"/></svg>';

  registerParty('tron', {
    icon: TRON_ICON,
    seats: ['жовтий', 'зелений'],
    hint: '{dpad} куди їхати · {a} турбо',
    play: 'Стрілки або WASD — і не наїдь на слід',
    out: 'Аварія!',
    news: NEWS_TRON,
  });

  registerParty('tron-party', {
    icon: TRON_PARTY_ICON,
    seats: ['жовтий', 'зелений', 'синій', 'рожевий'],
    hint: '{dpad} куди їхати · {a} турбо',
    play: 'Стрілки або WASD — і не наїдь на чужий слід',
    out: 'Аварія! Дивись, хто кого пережене',
    news: {
      v: '2026-09-29',
      title: 'Мотоцикли гуртом: серії, мапи, турбо й боти',
      items: [
        '🏁 Партія «до 3/5 перемог»: після аварії 2 с табло — і наступний раунд сам, без «Ще раз»',
        '✂ Видно, хто кого підрізав: стрічка під табло, лічильник ✂ біля ніка й рядок у Журналі',
        '🏛 Мапи «Колони», «Хрест», «Тор» (край наскрізь) або випадкова щораунду; 🧱 край, що обростає стіною після 40 с',
        '🚀 Турбо: пробіл, Ⓐ чи подвійний тап — секунда подвійної швидкості, щоб підрізати чи втекти',
        '🤖 Боти на порожні місця (сам — теж можна) і 🔥❄️ команди 2×2, де слід напарника не вбиває; ⏪ повтор аварії',
      ],
    },
  });
  // «Змійки гуртом» (snake-party) з проходу №3 малює свій модуль — web/games/snake-party.js.
})();
