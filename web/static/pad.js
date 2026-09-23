/*
  Джойстик на весь сайт. Один глобал — window.HPad.

  Навіщо: «Глечики» відкривають зі Steam Deck, де мишу заміняє тачпад, а зручно — стіком і кнопками.
  Шар нікому нічого не ламає: миша, палець і клавіатура працюють, як працювали, а пад додається зверху.

  Три режими, між якими шар перемикається сам:
  - «навігація» — стік/хрестовина водять кільце по кнопках (просторово, за геометрією), Ⓐ натискає;
  - «гра» — активний стіл віддав напрямки собі (реалтайм-ігри оголошують `pad` у HGames.register):
    стік стає стрілками, Ⓐ — клавішею гри. Шлемо справжні KeyboardEvent із `code`, тож жодна гра
    про пад не знає й нічого в ній міняти не треба;
  - «клавіатура» — своя екранна клавіатура для полів вводу (нік, балачки, відповіді в іграх).

  Правила, за якими це живе:
  - Кільце фокуса — окремий елемент поверх сторінки, а не клас на кнопці: лобі й ігрові сітки
    перемальовують свої кнопки десятки разів на хвилину, і будь-який клас із них злетів би.
  - Натиск пада — справжній фізичний натиск людини, тож подіям, які ми з нього робимо, ставимо
    позначку `hpad`. `HPad.human(ev)` — те, що має стояти там, де раніше було саме `ev.isTrusted`
    (Око майстра Гончарного кола). Автоповтору в Ⓐ нема й не буде: рівний ритм — це якраз те,
    за чим сервер упізнає автоклікер.
  - Підказки самі не набридають: смужка внизу з'являється, коли в руках пад, і ховається Select'ом.
    Велика довідка сама відкривається один раз і лише на Деку (див. `deck()`).
*/
(() => {
  'use strict';

  // ---------- стандартна розкладка Gamepad API ----------
  const A = 0, B = 1, X = 2, Y = 3, LB = 4, RB = 5, LT = 6, RT = 7, SELECT = 8, START = 9,
    R3 = 11, UP = 12, DOWN = 13, LEFT = 14, RIGHT = 15;
  const NBUT = 17;

  const DEAD_ON = 0.55, DEAD_OFF = 0.35;     // гістерезис стіка: увійти важче, ніж вийти
  const REPEAT_FIRST = 380, REPEAT_NEXT = 120;
  const SCROLL_SPEED = 13;                   // пікселів за кадр на повністю відхиленому правому стіку

  const KEY_NAME = { Space: ' ', Enter: 'Enter', Escape: 'Escape', ArrowUp: 'ArrowUp', ArrowDown: 'ArrowDown', ArrowLeft: 'ArrowLeft', ArrowRight: 'ArrowRight' };
  const DIR_KEY = { up: 'ArrowUp', down: 'ArrowDown', left: 'ArrowLeft', right: 'ArrowRight' };

  const store = {
    get(k, d) { try { const v = localStorage.getItem(k); return v == null ? d : v; } catch { return d; } },
    set(k, v) { try { localStorage.setItem(k, v); } catch { /* приватне вікно */ } },
  };

  // ---------- стан ----------
  let pads = 0;                    // скільки падів бачимо
  let padNames = '';               // їхні id — за ними впізнаємо Valve/Steam
  let raf = 0;
  let on = false;                  // режим пада ввімкнено (був натиск на паді)
  let prev = new Array(NBUT).fill(false);
  let navDir = '';                 // напрямок, який зараз тримають (для автоповтору навігації)
  let navAt = 0;
  let gameDir = '';                // напрямок, відданий грі (щоб зняти клавішу на відпусканні)
  let aKey = '';                   // клавіша гри, яку зараз тримає кнопка дії
  let aBtn = -1;                   // яка саме кнопка її тримає (з anyBtn це будь-яка з восьми)
  let pressed = null;              // елемент, на якому Ⓐ тримає pointerdown (коло Гончарного)
  let cur = null;                  // елемент під кільцем
  let curBox = null;               // де кільце було востаннє — щоб знайти заміну після перемальовки
  let ring = null;
  let hintBar = null;
  let helpBox = null;
  let kbd = null;                  // { el, target, layout, shift, cur }
  let cursor = null;               // { el, x, y, down, target } — режим «стік замість миші»
  let hintsOff = store.get('padHintsOff', '') === '1';
  let hintAt = 0;                  // коли востаннє перечитували смужку підказок
  let scopeAt = 0;                 // коли востаннє звіряли, чи кільце в тій самій частині екрана

  const el = (tag, cls, html) => {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (html != null) e.innerHTML = html;
    return e;
  };

  // ---------- чи це Steam Deck ----------

  /// Деку впізнаємо трьома способами, бо жодного певного нема: сам Steam у рядку браузера, падом
  /// від Valve (вендор 28de — і віртуальний Steam Input, і сирий контролер Деки) і екраном 1280×800
  /// на Linux — саме стільки в Деки і саме так вона підписується в Game Mode.
  /// `?deck=1` / `?deck=0` в адресі — ручний важіль: щоб подивитись підказки з будь-якого комп'ютера.
  let deckForced = null;
  try {
    const q = new URLSearchParams(location.search);
    if (q.has('deck')) { deckForced = q.get('deck') !== '0'; store.set('padDeck', deckForced ? '1' : '0'); }
    else { const s = store.get('padDeck', ''); if (s) deckForced = s === '1'; }
  } catch { /* адреса дивна — не біда */ }

  function deck() {
    if (deckForced !== null) return deckForced;
    const ua = navigator.userAgent || '';
    if (/SteamOS|Steam Deck|Valve Steam GameOverlay|Valve Steam Client/i.test(ua)) return true;
    if (/valve|steam|28de/i.test(padNames)) return true;
    const linux = /Linux|X11/i.test(ua) && !/Android/i.test(ua);
    const w = Math.max(screen.width, screen.height), h = Math.min(screen.width, screen.height);
    return linux && w === 1280 && h === 800;
  }

  // ---------- позначені події: «це натиснула людина падом» ----------

  function fire(node, ev) {
    ev.hpad = true;                // HPad.human() шукає саме це
    node.dispatchEvent(ev);
    return ev;
  }
  const mouseInit = (r, more) => Object.assign({
    bubbles: true, cancelable: true, composed: true, view: window, button: 0,
    clientX: Math.round(r.left + r.width / 2), clientY: Math.round(r.top + r.height / 2),
  }, more || {});

  function key(type, code, node) {
    return fire(node || document, new KeyboardEvent(type, {
      key: KEY_NAME[code] || code, code, bubbles: true, cancelable: true, view: window,
    }));
  }

  // ---------- опитування пада ----------

  function scan() {
    const list = (navigator.getGamepads && navigator.getGamepads()) || [];
    let n = 0, names = '';
    for (const p of list) { if (p && p.connected) { n++; names += ' ' + (p.id || ''); } }
    if (n !== pads || names !== padNames) { pads = n; padNames = names; if (!pads) sleep(); }
    if (pads && !raf) raf = requestAnimationFrame(poll);
    paintTip();
    return list;
  }

  /// 🎮 у шапці — вхід у довідку без жодної кнопки пада. Показуємо його лише тим, кому він потрібен:
  /// або ми впізнали Деку, або пад уже підключений.
  function paintTip() {
    const tip = document.getElementById('padTip');
    if (!tip) return;
    const want = deck() || pads > 0;
    if (tip.hidden !== !want) tip.hidden = !want;
    if (want && !tip._pad) { tip._pad = true; tip.onclick = openHelp; }
  }

  function sleep() {
    if (raf) cancelAnimationFrame(raf);
    raf = 0;
    setOn(false);
    releaseAll();
  }

  /// Відпустити все, що пад зараз тримає: інакше гра лишиться їхати в стіну, коли пад від'єднали.
  function releaseAll() {
    if (gameDir) { key('keyup', DIR_KEY[gameDir]); gameDir = ''; }
    if (aKey) { key('keyup', aKey); aKey = ''; aBtn = -1; }
    if (pressed) { padUp(pressed); pressed = null; }
    navDir = '';
  }

  function poll() {
    raf = 0;
    const list = (navigator.getGamepads && navigator.getGamepads()) || [];
    const down = new Array(NBUT).fill(false);
    let ax = 0, ay = 0, rx = 0, ry = 0, live = 0;
    for (const p of list) {
      if (!p || !p.connected) continue;
      live++;
      const bs = p.buttons || [];
      for (let i = 0; i < NBUT && i < bs.length; i++) if (bs[i] && bs[i].pressed) down[i] = true;
      const a = p.axes || [];
      if (Math.abs(a[0] || 0) > Math.abs(ax)) ax = a[0] || 0;
      if (Math.abs(a[1] || 0) > Math.abs(ay)) ay = a[1] || 0;
      if (Math.abs(a[2] || 0) > Math.abs(rx)) rx = a[2] || 0;
      if (Math.abs(a[3] || 0) > Math.abs(ry)) ry = a[3] || 0;
    }
    if (!live) { pads = 0; sleep(); return; }

    const now = performance.now();
    // Спершу вмикаємо режим пада (він може поставити кільце на перше місце) і лише потім
    // роздаємо натиски: інакше найперший рух стіка рахувався б від порожнечі.
    const dirNow = down[UP] ? 'up' : down[DOWN] ? 'down' : down[LEFT] ? 'left' : down[RIGHT] ? 'right' : stick(ax, ay);
    let acted = !!dirNow;
    for (let i = 0; i < NBUT; i++) if (down[i] !== prev[i]) { acted = true; break; }
    if (acted) setOn(true);

    for (let i = 0; i < NBUT; i++) {
      if (down[i] === prev[i]) continue;
      if (down[i]) buttonDown(i, now); else buttonUp(i, now);
    }
    prev = down;

    if (cursor) {
      // У режимі курсора хрестовина — повільне підведення, стік — швидке.
      const fine = 0.7;
      moveCursor(ax + (down[RIGHT] ? fine : 0) - (down[LEFT] ? fine : 0), ay + (down[DOWN] ? fine : 0) - (down[UP] ? fine : 0));
    } else {
      // Напрямок: хрестовина важить більше за стік, бо нею цілять точніше.
      direction(dirNow, now);
    }
    scrollBy(rx, ry);

    if (on) { paintRing(); if (now - hintAt > 400) { hintAt = now; paintHints(); } }
    raf = requestAnimationFrame(poll);
  }

  function stick(ax, ay) {
    const mag = Math.hypot(ax, ay);
    if (mag < (navDir || gameDir ? DEAD_OFF : DEAD_ON)) return '';
    return Math.abs(ax) > Math.abs(ay) ? (ax > 0 ? 'right' : 'left') : (ay > 0 ? 'down' : 'up');
  }

  // ---------- режими ----------

  let gCache = null, gCacheAt = 0;

  /// Гра, яка щось забрала собі: модуль оголосив `pad`, партія йде і ми в ній граємо.
  /// `dirs` — стік стає стрілками (реалтайм), без нього кільце лишається навігації,
  /// а гра бере лише окремі кнопки (`a`, `x`, `on`) — як Гончарне коло.
  /// HGames.active() лазить по DOM, тож відповідь тримаємо чверть секунди.
  function claim() {
    const now = performance.now();
    if (now - gCacheAt < 250) return gCache;
    gCacheAt = now;
    gCache = null;
    try {
      const g = window.HGames && HGames.active && HGames.active();
      if (g && g.mod && g.mod.pad && g.ctx) {
        const p = g.mod.pad;
        const ok = p.when ? p.when(g.ctx) : (g.ctx.mine && g.ctx.playing);
        if (ok) gCache = { p, ctx: g.ctx, id: g.id };
      }
    } catch { /* модуль ще не приїхав */ }
    return gCache;
  }

  function mode() {
    if (kbd) return 'kbd';
    if (helpBox) return 'help';
    if (cursor) return 'cursor';
    const g = claim();
    return g && g.p.dirs ? 'game' : 'nav';
  }

  function setOn(v) {
    if (on === v) return;
    on = v;
    document.body.classList.toggle('pad-on', on);
    if (on) {
      if (!cur || !cur.isConnected) setCur(firstTarget());
      helpOnce();
    }
    paintHints();
    paintRing();
  }

  // ---------- кнопки ----------

  const BTN_NAME = { 0: 'a', 1: 'b', 2: 'x', 3: 'y', 4: 'lb', 5: 'rb', 6: 'lt', 7: 'rt', 8: 'select', 9: 'start', 11: 'r3' };
  /// Кнопки під великим пальцем — усі, крім Ⓑ: її гра собі не забирає навіть із `anyBtn`.
  const FIRE = [A, X, Y, LB, RB, LT, RT];

  function buttonDown(i, now) {
    const m = mode();
    if (m === 'kbd') return kbdButton(i, true);
    if (m === 'help') {
      if (i === B || i === Y || i === A || i === START) closeHelp();
      return;
    }
    if (m === 'cursor') return cursorButton(i);

    const g = claim();
    if (g) {
      // Гра може з'їсти будь-яку кнопку сама (Гончарне коло ловить глеки на Ⓧ).
      try { if (g.p.on && g.p.on(BTN_NAME[i], g.ctx)) return; } catch (e) { console.warn('[pad] on', e); }
      // Гра без своєї дії на Ⓐ (понг, змійка) лишає кнопку навігації: нею й далі тиснуть кнопки картки.
      // `anyBtn` — дія на будь-якій із восьми кнопок під великим пальцем: посеред партії шукати «ту саму»
      // ніколи. Ⓑ лишаємо собі в будь-якому разі, інакше зі столу нічим було б вийти.
      if (g.p.a && (i === A || (g.p.anyBtn && FIRE.includes(i)))) {
        if (!aKey) { aKey = g.p.a; aBtn = i; key('keydown', aKey); }
        return;
      }
      if (i === X && g.p.x) { key('keydown', g.p.x); key('keyup', g.p.x); return; }
    }
    switch (i) {
      case A: return activate();
      case B: return back();
      case X: return chat();
      case Y: return openHelp();
      case LB: return section(-1);
      case RB: return section(1);
      case LT: return tab(-1);
      case RT: return tab(1);
      case START: return start();
      case SELECT: return toggleHints();
      case R3: return openCursor();
      default:
    }
  }

  function buttonUp(i) {
    // Клавішу гри відпускає та сама кнопка, що її натиснула: з `anyBtn` це не конче Ⓐ.
    if (aKey && i === aBtn) { key('keyup', aKey); aKey = ''; aBtn = -1; }
    if (i !== A) return;
    if (cursor) return cursorUp();
    if (pressed) { padUp(pressed); pressed = null; }
  }

  // ---------- курсор: стік замість миші ----------

  /// Є місця, де кнопками не обійтися: полиця Ока майстра (де глечики — знає лише сервер), розпис у горні,
  /// малювання в Піктіонарі. Там вмикають курсор (R3) і водять його стіком — далі це звичайний вказівник.

  function openCursor() {
    if (cursor) return closeCursor();
    const r = cur && cur.isConnected ? cur.getBoundingClientRect() : null;
    cursor = {
      el: el('div', 'padcursor'),
      x: r ? r.left + r.width / 2 : innerWidth / 2,
      y: r ? r.top + r.height / 2 : innerHeight / 2,
      down: null, moved: 0,
    };
    document.body.appendChild(cursor.el);
    paintCursor();
    paintRing();
    paintHints();
  }

  function closeCursor() {
    if (!cursor) return;
    cursorUp();
    cursor.el.remove();
    cursor = null;
    paintRing();
    paintHints();
  }

  function moveCursor(ax, ay) {
    if (!cursor) return;
    const push = (v) => {
      const a = Math.abs(v);
      if (a < DEAD_OFF) return 0;
      const t = (a - DEAD_OFF) / (1 - DEAD_OFF);
      return Math.sign(v) * t * t * 18;          // біля краю — швидко, біля центру — по пікселю
    };
    const dx = push(ax), dy = push(ay);
    if (!dx && !dy) return;
    cursor.x = Math.max(2, Math.min(innerWidth - 2, cursor.x + dx));
    cursor.y = Math.max(2, Math.min(innerHeight - 2, cursor.y + dy));
    cursor.moved += Math.abs(dx) + Math.abs(dy);
    if (cursor.down) {
      const init = mouseInit({ left: cursor.x, top: cursor.y, width: 0, height: 0 },
        { buttons: 1, pointerId: 1, pointerType: 'mouse', isPrimary: true, pressure: 0.5 });
      fire(cursor.down, window.PointerEvent ? new PointerEvent('pointermove', init) : new MouseEvent('mousemove', init));
    }
    paintCursor();
  }

  function paintCursor() {
    if (!cursor) return;
    cursor.el.style.transform = 'translate(' + Math.round(cursor.x) + 'px,' + Math.round(cursor.y) + 'px)';
    cursor.el.classList.toggle('down', !!cursor.down);
  }

  function cursorButton(i) {
    if (i === A) {
      const node = document.elementFromPoint(cursor.x, cursor.y);
      if (!node) return;
      cursor.down = node;
      cursor.moved = 0;
      const init = mouseInit({ left: cursor.x, top: cursor.y, width: 0, height: 0 },
        { buttons: 1, pointerId: 1, pointerType: 'mouse', isPrimary: true, pressure: 0.5 });
      fire(node, window.PointerEvent ? new PointerEvent('pointerdown', init) : new MouseEvent('mousedown', init));
      paintCursor();
      return;
    }
    if (i === B || i === R3) return closeCursor();
    if (i === Y) return openHelp();
    if (i === SELECT) return toggleHints();
  }

  function cursorUp() {
    if (!cursor || !cursor.down) return;
    const node = cursor.down;
    cursor.down = null;
    const box = { left: cursor.x, top: cursor.y, width: 0, height: 0 };
    const init = mouseInit(box, { buttons: 0, pointerId: 1, pointerType: 'mouse', isPrimary: true, pressure: 0 });
    fire(node, window.PointerEvent ? new PointerEvent('pointerup', init) : new MouseEvent('mouseup', init));
    // Провів по екрану — це був штрих, а не натиск: click такому не належить.
    if (cursor.moved < 6) {
      const under = document.elementFromPoint(cursor.x, cursor.y) || node;
      fire(under, new MouseEvent('click', mouseInit(box, { buttons: 0 })));
    }
    paintCursor();
  }

  // ---------- напрямки ----------

  function direction(dir, now) {
    const m = mode();
    if (m === 'game') {
      const g = claim();
      const axis = g.p.dirs;                       // true — усі чотири, 'x' / 'y' — лише своя вісь
      let d = dir;
      if (axis === 'x' && (d === 'up' || d === 'down')) d = '';
      if (axis === 'y' && (d === 'left' || d === 'right')) d = '';
      if (d === gameDir) return;
      if (gameDir) key('keyup', DIR_KEY[gameDir]);
      gameDir = d;
      if (d) key('keydown', DIR_KEY[d]);
      navDir = '';
      return;
    }
    if (gameDir) { key('keyup', DIR_KEY[gameDir]); gameDir = ''; }

    if (dir !== navDir) {
      navDir = dir;
      navAt = now + REPEAT_FIRST;
      if (dir) step(dir);
    } else if (dir && now >= navAt) {
      navAt = now + REPEAT_NEXT;
      step(dir);
    }
  }

  function step(dir) {
    if (kbd) return kbdMove(dir);
    // Повзунок гучності: ліворуч-праворуч крутять його, а не тікають на сусідню кнопку.
    if (cur && cur.matches('input[type=range]') && (dir === 'left' || dir === 'right')) {
      const st = +cur.step || 1, d = dir === 'right' ? st : -st;
      cur.value = Math.max(+cur.min || 0, Math.min(+cur.max || 100, (+cur.value || 0) + d));
      cur.dispatchEvent(new Event('input', { bubbles: true }));
      cur.dispatchEvent(new Event('change', { bubbles: true }));
      return;
    }
    const next = findNext(dir);
    if (next) setCur(next);
    else scrollPage(dir);
  }

  // ---------- де зараз кільце ----------

  const FOCUSABLE = 'a[href], button, summary, input, select, textarea, [tabindex], [data-pad-focus], .result';

  function scope() {
    // Модальне вікно (нік, плейлист) чи накладка Гончарного кола забирають увагу цілком.
    const boxes = [...document.querySelectorAll('.modal, .clk-overlay, [data-pad-scope]')].filter(vis);
    return boxes.length ? boxes[boxes.length - 1] : document.body;
  }

  function vis(e) {
    if (!e || !e.isConnected) return false;
    if (e.disabled || e.hidden) return false;
    const r = e.getBoundingClientRect();
    if (r.width < 6 || r.height < 6) return false;             // [hidden] і display:none дають нуль
    if (e.getAttribute('aria-hidden') === 'true') return false;
    const cs = getComputedStyle(e);
    if (cs.visibility === 'hidden' || +cs.opacity === 0 || cs.pointerEvents === 'none') return false;
    return true;
  }

  function targets() {
    const root = scope();
    const out = [];
    for (const e of root.querySelectorAll(FOCUSABLE)) {
      if (e.matches('[tabindex="-1"], input[type=hidden]')) continue;
      if (e.closest('[data-pad-skip]')) continue;      // службові панелі (стенди) кільце оминає
      if (!vis(e)) continue;
      out.push(e);
    }
    return out;
  }

  const mid = (r) => ({ x: r.left + r.width / 2, y: r.top + r.height / 2 });

  /// Просторовий вибір: беремо те, що справді лежить у цьому боці, і з нього — найближче,
  /// з поблажкою до тих, хто стоїть із поточним в одному рядку (чи в одному стовпці).
  function findNext(dir) {
    const list = targets();
    if (!list.length) return null;
    if (!cur || !cur.isConnected || !vis(cur)) return nearest(list, curBox);
    const a = cur.getBoundingClientRect(), am = mid(a);
    let best = null, bestScore = Infinity;
    for (const e of list) {
      if (e === cur) continue;
      const b = e.getBoundingClientRect(), bm = mid(b);
      let main, cross, overlap;
      if (dir === 'left' || dir === 'right') {
        main = dir === 'right' ? b.left - a.right : a.left - b.right;
        if (main < -Math.min(a.width, b.width) / 2) continue;
        overlap = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top);
        cross = Math.abs(am.y - bm.y);
      } else {
        main = dir === 'down' ? b.top - a.bottom : a.top - b.bottom;
        if (main < -Math.min(a.height, b.height) / 2) continue;
        overlap = Math.min(a.right, b.right) - Math.max(a.left, b.left);
        cross = Math.abs(am.x - bm.x);
      }
      const s = Math.max(0, main) + (overlap > 2 ? cross * 0.25 : cross * 3);
      if (s < bestScore) { bestScore = s; best = e; }
    }
    return best;
  }

  function nearest(list, box) {
    const c = box ? mid(box) : { x: innerWidth / 2, y: innerHeight / 3 };
    let best = null, bestScore = Infinity;
    for (const e of list) {
      const m = mid(e.getBoundingClientRect());
      const s = Math.hypot(m.x - c.x, m.y - c.y);
      if (s < bestScore) { bestScore = s; best = e; }
    }
    return best;
  }

  /// Перша ціль при вході: те, що просить бути першим, інакше — найближче до верху видимого.
  function firstTarget() {
    const root = scope();
    const want = root.querySelector('[data-pad-first]');
    if (want && vis(want)) return want;
    const list = targets().filter((e) => {
      const r = e.getBoundingClientRect();
      return r.bottom > 0 && r.top < innerHeight;
    });
    return list.length ? list[0] : targets()[0] || null;
  }

  function setCur(e) {
    cur = e || null;
    if (!cur) { paintRing(); return; }
    curBox = cur.getBoundingClientRect();
    // Фокус ставимо справжній: так працюють і Enter, і клавіатура Steam, і прокрутка до елемента.
    try { cur.focus({ preventScroll: true }); } catch { /* не всім можна */ }
    try { cur.scrollIntoView({ block: 'nearest', inline: 'nearest' }); } catch { /* старий браузер */ }
    paintRing();
  }

  function paintRing() {
    if (!ring) {
      ring = el('div', 'padring');
      document.body.appendChild(ring);
    }
    // У живій партії й під курсором кільце ні до чого: там стік — це рух, а не вибір кнопки.
    const m = mode();
    if (!on || !cur || m === 'game' || m === 'cursor') { ring.hidden = true; return; }
    if (!cur.isConnected || !vis(cur)) {
      // Лобі перемалювалось і кнопка зникла — стаємо на найближчу до того місця, де стояли.
      const back2 = nearest(targets(), curBox);
      if (!back2) { ring.hidden = true; return; }
      setCur(back2);
      return;
    }
    // Відкрилось вікно (нік, ставка столу, накладка клікера) — кільце має перескочити в нього,
    // а коли вікно закрили — повернутись на сторінку. Перевіряємо не щокадру: пошук по атрибуту
    // обходить весь документ, а вікна з'являються не двадцять разів на секунду.
    const t = performance.now();
    if (t - scopeAt > 200) {
      scopeAt = t;
      const root = scope();
      if (!root.contains(cur)) { setCur(firstTarget()); return; }
    }
    const r = cur.getBoundingClientRect();
    curBox = r;
    ring.hidden = false;
    ring.style.transform = 'translate(' + Math.round(r.left - 4) + 'px,' + Math.round(r.top - 4) + 'px)';
    ring.style.width = Math.round(r.width + 8) + 'px';
    ring.style.height = Math.round(r.height + 8) + 'px';
    const rad = getComputedStyle(cur).borderRadius;
    ring.style.borderRadius = rad && rad !== '0px' ? 'calc(' + rad.split(' ')[0] + ' + 4px)' : '10px';
  }

  // ---------- Ⓐ: натиснути ----------

  function activate() {
    if (!cur || !cur.isConnected) { setCur(firstTarget()); return; }
    if (cur.matches('input[type=range]')) return;                 // ним крутять, а не тиснуть
    if (cur.isContentEditable || cur.matches('textarea, input:not([type=button]):not([type=submit]):not([type=reset]):not([type=checkbox]):not([type=radio]):not([type=range])')) {
      openKbd(cur);
      return;
    }
    if (cur.matches('select')) {
      const s = cur;
      s.selectedIndex = (s.selectedIndex + 1) % Math.max(1, s.options.length);
      s.dispatchEvent(new Event('change', { bubbles: true }));
      return;
    }
    // Коло Гончарного й інші, хто рахує саме pointerdown/pointerup: тримаємо натиск, поки тримають Ⓐ,
    // щоб довжина натиску була справжня — за нею Око майстра відрізняє руку від скрипта.
    if (cur.matches('[data-pad="press"]')) { pressed = cur; padDown(cur); return; }
    const r = cur.getBoundingClientRect();
    fire(cur, new MouseEvent('click', mouseInit(r, { buttons: 0 })));
  }

  function padDown(node) {
    const r = node.getBoundingClientRect();
    const init = mouseInit(r, { buttons: 1, pointerId: 1, pointerType: 'mouse', isPrimary: true, width: 1, height: 1, pressure: 0.5 });
    fire(node, window.PointerEvent ? new PointerEvent('pointerdown', init) : new MouseEvent('mousedown', init));
  }

  function padUp(node) {
    if (!node.isConnected) return;
    const r = node.getBoundingClientRect();
    const init = mouseInit(r, { buttons: 0, pointerId: 1, pointerType: 'mouse', isPrimary: true, width: 1, height: 1, pressure: 0 });
    fire(node, window.PointerEvent ? new PointerEvent('pointerup', init) : new MouseEvent('mouseup', init));
  }

  // ---------- Ⓑ: назад ----------

  function back() {
    const box = [...document.querySelectorAll('.modal')].filter(vis).pop();
    if (box) {
      const x = box.querySelector('#nickLater, #plClose, .gx, .ghost[type=button], [data-close]');
      if (x && vis(x)) { fire(x, new MouseEvent('click', mouseInit(x.getBoundingClientRect(), { buttons: 0 }))); return; }
    }
    // Esc уміють і каркас ігор (вийти зі столу, згорнути «на весь екран»), і самі ігри.
    key('keydown', 'Escape', document.activeElement && document.activeElement !== document.body ? document.activeElement : document);
  }

  // ---------- розділи, вкладки, балачки ----------

  const ROUTES = ['efir', 'lib', 'games'];

  function section(d) {
    const now = ROUTES.findIndex((r) => document.body.classList.contains('route-' + r));
    const next = ROUTES[(Math.max(0, now) + d + ROUTES.length) % ROUTES.length];
    const b = document.querySelector('#mainNav button[data-route="' + next + '"], .mtabs button[data-route="' + next + '"]');
    if (b) { b.click(); setTimeout(() => setCur(firstTarget()), 60); }
  }

  /// Вкладка всередині розділу: меню бібліотеки, чипи-фільтри ігор, вкладки балачок.
  function tab(d) {
    // Спершу вкладки того розділу, де ми стоїмо, і лише потім балачки: інакше розгорнута
    // панель балачок забирала б LT/RT собі з будь-якого місця сайту.
    const cls = document.body.classList;
    const rows = cls.contains('route-games') ? ['.gfilters', '.gnav', '#chatTabs']
      : cls.contains('route-lib') ? ['#libTabs', '#chatTabs'] : ['#chatTabs'];
    let list = null;
    for (const sel of rows) {
      const host = document.querySelector(sel);
      if (!host || !vis(host)) continue;
      const bs = [...host.querySelectorAll('button')].filter(vis);
      if (bs.length > 1) { list = bs; break; }
    }
    if (!list) return;
    let i = list.findIndex((b) => b.classList.contains('on'));
    if (i < 0) i = 0;
    const b = list[(i + d + list.length) % list.length];
    b.click();
    setTimeout(() => setCur(b), 60);
  }

  function chat() {
    const b = document.getElementById('chatToggle') || document.getElementById('mtabChat');
    if (b) b.click();
    setTimeout(() => {
      const inp = document.getElementById('chatInput');
      if (inp && vis(inp)) setCur(inp);
    }, 80);
  }

  /// ☰ — «на весь екран», коли ми за столом; у решті місць веде в Ігри.
  function start() {
    const f = document.querySelector('.grfull');
    if (f && vis(f)) { f.click(); return; }
    const g = document.querySelector('#mainNav button[data-route="games"], .mtabs button[data-route="games"]');
    if (g) { g.click(); setTimeout(() => setCur(firstTarget()), 60); }
  }

  // ---------- прокрутка правим стіком ----------

  function scroller(node) {
    let e = node;
    while (e && e !== document.body) {
      const cs = getComputedStyle(e);
      if (/(auto|scroll)/.test(cs.overflowY) && e.scrollHeight > e.clientHeight + 4) return e;
      e = e.parentElement;
    }
    return null;
  }

  function scrollBy(rx, ry) {
    if (Math.abs(ry) < DEAD_OFF && Math.abs(rx) < DEAD_OFF) return;
    const box = scroller(cur) || scroller(document.querySelector('.messages:not([hidden])'));
    const dy = Math.abs(ry) < DEAD_OFF ? 0 : ry * SCROLL_SPEED;
    const dx = Math.abs(rx) < DEAD_OFF ? 0 : rx * SCROLL_SPEED;
    if (box) box.scrollBy(dx, dy); else window.scrollBy(dx, dy);
  }

  /// Кільцю нема куди йти — гортаємо сторінку самі, щоб хвіст довгого списку не лишався за кадром.
  function scrollPage(dir) {
    if (dir !== 'up' && dir !== 'down') return;
    const box = scroller(cur);
    const dy = dir === 'down' ? 140 : -140;
    if (box) box.scrollBy(0, dy); else window.scrollBy(0, dy);
  }

  // ---------- екранна клавіатура ----------

  const LAYOUTS = {
    ua: ['йцукенгшщзхїґ', 'фівапролджє', 'ячсмитьбю', "0123456789-'"],
    en: ['qwertyuiop', 'asdfghjkl', 'zxcvbnm', '0123456789-_'],
    sym: ['1234567890', '!?.,:;()', '@#№%$&*+=', '/\\"\'«»—'],
  };
  const LAYOUT_ORDER = ['ua', 'en', 'sym'];
  const LAYOUT_NAME = { ua: 'укр', en: 'lat', sym: '123' };

  function openKbd(target) {
    if (kbd) closeKbd();
    closeCursor();
    const box = el('div', 'padkbd');
    box.setAttribute('data-pad-scope', '');
    kbd = { el: box, target, layout: store.get('padKbdLayout', 'ua'), shift: false, r: 0, c: 0 };
    document.body.appendChild(box);
    document.body.classList.add('pad-kbd');
    drawKbd();
    try { target.focus({ preventScroll: true }); } catch { /* буває */ }
    try { target.scrollIntoView({ block: 'center' }); } catch { /* старий браузер */ }
    paintHints();
    paintRing();
  }

  function closeKbd() {
    if (!kbd) return;
    kbd.el.remove();
    kbd = null;
    document.body.classList.remove('pad-kbd');
    paintHints();
    paintRing();
  }

  function rows() {
    const r = LAYOUTS[kbd.layout] || LAYOUTS.ua;
    return r.map((s) => s.split('').map((ch) => (kbd.shift ? ch.toUpperCase() : ch)));
  }

  function drawKbd() {
    const rs = rows();
    kbd.r = Math.min(kbd.r, rs.length - 1);
    kbd.c = Math.min(kbd.c, rs[kbd.r].length - 1);
    kbd.el.innerHTML =
      '<div class="pk-head"><span class="pk-txt"></span></div>'
      + rs.map((row, ri) => '<div class="pk-row">' + row.map((ch, ci) =>
        '<span class="pk-key' + (ri === kbd.r && ci === kbd.c ? ' on' : '') + '">' + ch + '</span>').join('') + '</div>').join('')
      + '<div class="pk-legend">'
      + glyph('a') + ' літера ' + glyph('x') + ' стерти ' + glyph('y') + ' пробіл '
      + glyph('lb') + '/' + glyph('rb') + ' ' + LAYOUT_NAME[kbd.layout] + ' · ⇧ '
      + glyph('start') + ' готово ' + glyph('b') + ' закрити</div>';
    const t = kbd.el.querySelector('.pk-txt');
    const v = kbd.target.isContentEditable ? kbd.target.textContent : kbd.target.value;
    t.textContent = v || kbd.target.placeholder || '';
    t.classList.toggle('muted', !v);
  }

  function kbdMove(dir) {
    if (!kbd) return;
    const rs = rows();
    if (dir === 'up') kbd.r = (kbd.r - 1 + rs.length) % rs.length;
    else if (dir === 'down') kbd.r = (kbd.r + 1) % rs.length;
    else if (dir === 'left') kbd.c = (kbd.c - 1 + rs[kbd.r].length) % rs[kbd.r].length;
    else kbd.c = (kbd.c + 1) % rs[kbd.r].length;
    kbd.c = Math.min(kbd.c, rs[kbd.r].length - 1);
    drawKbd();
  }

  function kbdButton(i) {
    if (!kbd) return;
    if (i === A) { const rs = rows(); type(rs[kbd.r][kbd.c]); return; }
    if (i === X) { erase(); return; }
    if (i === Y) { type(' '); return; }
    if (i === B) { closeKbd(); return; }
    if (i === START) { submit(); return; }
    if (i === LB || i === RB) {
      const j = LAYOUT_ORDER.indexOf(kbd.layout);
      kbd.layout = LAYOUT_ORDER[(j + (i === RB ? 1 : LAYOUT_ORDER.length - 1)) % LAYOUT_ORDER.length];
      store.set('padKbdLayout', kbd.layout);
      kbd.c = 0; kbd.r = 0;
      drawKbd();
      return;
    }
    if (i === LT || i === RT) { kbd.shift = !kbd.shift; drawKbd(); }
  }

  function type(ch) {
    const t = kbd.target;
    if (t.isContentEditable) { t.textContent += ch; }
    else {
      const max = +t.maxLength;
      if (max > 0 && t.value.length >= max) return;
      const s = t.selectionStart == null ? t.value.length : t.selectionStart;
      const e = t.selectionEnd == null ? s : t.selectionEnd;
      t.value = t.value.slice(0, s) + ch + t.value.slice(e);
      try { t.selectionStart = t.selectionEnd = s + ch.length; } catch { /* type=email тощо */ }
    }
    t.dispatchEvent(new Event('input', { bubbles: true }));
    drawKbd();
  }

  function erase() {
    const t = kbd.target;
    if (t.isContentEditable) t.textContent = t.textContent.slice(0, -1);
    else {
      const s = t.selectionStart == null ? t.value.length : t.selectionStart;
      const e = t.selectionEnd == null ? s : t.selectionEnd;
      if (s === e && s === 0) return;
      const from = s === e ? s - 1 : s;
      t.value = t.value.slice(0, from) + t.value.slice(e);
      try { t.selectionStart = t.selectionEnd = from; } catch { /* буває */ }
    }
    t.dispatchEvent(new Event('input', { bubbles: true }));
    drawKbd();
  }

  /// «Готово» — те саме, що Enter: спершу даємо полю обробити клавішу (пошук, балачки, ігри),
  /// і лише якщо ніхто її не з'їв, відправляємо форму.
  function submit() {
    const t = kbd.target;
    const ev = key('keydown', 'Enter', t);
    key('keyup', 'Enter', t);
    if (!ev.defaultPrevented && t.form && t.form.requestSubmit) {
      try { t.form.requestSubmit(); } catch { /* форма без кнопки */ }
    }
    closeKbd();
  }

  // ---------- смужка підказок ----------

  const GLYPH = {
    a: 'A', b: 'B', x: 'X', y: 'Y', lb: 'LB', rb: 'RB', lt: 'LT', rt: 'RT',
    start: '☰', select: '⧉', dpad: '✥', rstick: '◉', r3: 'R3',
  };
  const glyph = (k) => '<i class="pb pb-' + k + '">' + GLYPH[k] + '</i>';

  function hintText() {
    const m = mode();
    if (m === 'kbd') return glyph('dpad') + ' літери ' + glyph('a') + ' ввести ' + glyph('x') + ' стерти ' + glyph('start') + ' готово ' + glyph('b') + ' закрити';
    if (m === 'cursor') return glyph('dpad') + ' вести ' + glyph('a') + ' тиснути й малювати ' + glyph('r3') + '/' + glyph('b') + ' назад до кнопок';
    // Модуль пише підказку словами й кнопками у фігурних дужках: '{dpad} рух · {a} бомба'.
    const g = claim();
    const own = g && g.p.hint ? g.p.hint.replace(/\{(\w+)\}/g, (all, k) => (GLYPH[k] ? glyph(k) : all)) : '';
    if (m === 'game') return (own || glyph('dpad') + ' рух') + ' ' + glyph('b') + ' вийти ' + glyph('y') + ' підказки';
    const room = document.querySelector('.grbox .gtable');
    if (room && vis(room)) {
      return (own || glyph('dpad') + ' вибір ' + glyph('a') + ' хід')
        + ' ' + glyph('b') + ' до лобі ' + glyph('start') + ' на весь екран ' + glyph('y') + ' підказки';
    }
    return glyph('dpad') + ' вибір ' + glyph('a') + ' натиснути ' + glyph('b') + ' назад '
      + glyph('x') + ' балачки ' + glyph('lb') + glyph('rb') + ' розділ ' + glyph('y') + ' підказки';
  }

  function paintHints() {
    if (!hintBar) {
      hintBar = el('div', 'padhints');
      document.body.appendChild(hintBar);
    }
    const show = on && !hintsOff;
    hintBar.hidden = !show;
    if (!show) return;
    const html = hintText();
    if (hintBar.innerHTML !== html) hintBar.innerHTML = html;   // інакше смужка мерехтіла б щокадру
  }

  function toggleHints() {
    hintsOff = !hintsOff;
    store.set('padHintsOff', hintsOff ? '1' : '');
    paintHints();
  }

  // ---------- велика довідка ----------

  const HELP = () => ''
    + '<div class="ph-card" data-pad-scope>'
    + '<h3>🎮 Джойстик' + (deck() ? ' на Steam Deck' : '') + '</h3>'
    + '<table class="ph-map">'
    + row('dpad', 'Лівий стік або хрестовина', 'ходити по кнопках; у швидких іграх — рух')
    + row('a', 'A', 'натиснути те, що в рамці; у полі вводу — відкрити клавіатуру; у швидких іграх — дія (бомба, постріл, поворот)')
    + row('b', 'B', 'назад: закрити вікно, вийти зі столу, згорнути «на весь екран»')
    + row('x', 'X', 'балачки')
    + row('y', 'Y', 'оця підказка')
    + row('lb', 'LB / RB', 'розділ: Ефір · Бібліотека · Ігри')
    + row('lt', 'LT / RT', 'вкладка всередині розділу (Бібліотека, фільтри ігор, Журнал)')
    + row('start', '☰ Start', 'за столом — «на весь екран», інакше — до Ігор')
    + row('select', '⧉ Select', 'сховати чи показати смужку підказок унизу')
    + row('r3', 'R3 (натиснути правий стік)', 'курсор: стік водить вказівник по екрану — ним малюють у Піктіонарі, розписують у горні й проходять полицю Ока майстра')
    + row('rstick', 'Правий стік', 'гортати сторінку й балачки')
    + '</table>'
    + (deck() ? ''
      + '<h4>Щоб пад бачив сайт</h4>'
      + '<ol class="ph-steps">'
      + '<li>У Game Mode: <b>Бібліотека → Додати не-Steam гру</b> → браузер. Найпростіше — <b>Firefox</b> із Discover: пад бачить одразу.</li>'
      + '<li><b>Chrome</b> із Discover у Game Mode пада не бачить, поки йому не дозволити. Один раз у Desktop Mode, в Konsole:'
      + '<br><code>flatpak --user override --filesystem=/run/udev:ro com.google.Chrome</code></li>'
      + '<li>У картці браузера — <b>шестірня → Controller Layout</b> → шаблон <b>Gamepad</b>. Саме він шле стіки в сторінку; «Web Browser» лишає самі тачпади.</li>'
      + '<li>Правий тачпад лишається мишею — обидва способи працюють разом, можна будь-коли ткнути пальцем.</li>'
      + '<li>Текст можна вводити і нашою клавіатурою (A на полі), і стімівською — <b>STEAM + X</b>.</li>'
      + '</ol>' : '')
    + '<div class="ph-foot"><button type="button" class="primary" data-close>Зрозумів</button>'
    + '<span class="muted small">' + glyph('y') + ' відкриває це будь-коли</span></div>'
    + '</div>';

  const row = (g, k, t) => '<tr><td>' + glyph(g) + '</td><th>' + k + '</th><td>' + t + '</td></tr>';

  function openHelp() {
    if (helpBox) return closeHelp();
    helpBox = el('div', 'padhelp', HELP());
    document.body.appendChild(helpBox);
    helpBox.querySelector('[data-close]').onclick = closeHelp;
    helpBox.addEventListener('click', (e) => { if (e.target === helpBox) closeHelp(); });
    store.set('padHelpSeen', '1');
    paintHints();
    paintRing();
  }

  function closeHelp() {
    if (!helpBox) return;
    helpBox.remove();
    helpBox = null;
    paintHints();
    paintRing();
  }

  /// На Деку довідку показуємо самі — один раз, коли пад справді взяли в руки.
  function helpOnce() {
    if (!deck() || store.get('padHelpSeen', '') === '1') return;
    openHelp();
  }

  // ---------- миша й палець забирають кільце назад ----------

  const wake = () => { if (on) { setOn(false); closeKbd(); closeCursor(); } };
  addEventListener('pointerdown', (e) => { if (e.isTrusted && !e.hpad) wake(); }, true);
  addEventListener('pointermove', (e) => { if (e.isTrusted && !e.hpad && (e.movementX || e.movementY)) wake(); }, true);
  addEventListener('keydown', (e) => { if (e.isTrusted && !e.hpad) wake(); }, true);

  addEventListener('gamepadconnected', () => scan());
  addEventListener('gamepaddisconnected', () => scan());
  addEventListener('blur', releaseAll);
  addEventListener('resize', paintRing);
  // Chrome віддає паді лише після першого дотику до сторінки, і події gamepadconnected можна не дочекатись,
  // тож раз на секунду перепитуємо самі. Поки пада нема, це один дешевий виклик.
  setInterval(scan, 1000);
  scan();

  // ---------- публічне ----------

  window.HPad = {
    /// Подію натиснула людина? Пад — теж людина, просто не мишею.
    /// Саме це має стояти там, де раніше було `ev.isTrusted` (Око майстра).
    human: (ev) => !!ev && (ev.isTrusted || ev.hpad === true),
    get on() { return on; },
    get pads() { return pads; },
    get deck() { return deck(); },
    help: openHelp,
    hints: paintHints,
    focus: setCur,
  };
})();
