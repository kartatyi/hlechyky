/*
  Каркас ігор у браузері. Один глобал — window.HGames.

  app.js про ігри більше нічого не знає: він кличе init() на старті, attach(conn) у connect(),
  reconnected() після реконекту і show()/hide() при перемиканні вкладок. Навзаєм каркас каже йому через
  init({ onTable, onTurn }), біля якого столу ми стоїмо і за якими столами мій хід. Усе інше — тут:
  каталог із сервера, завантаження модулів, лобі (смужка «Сьогодні», живі столи, «часто граємо», каталог
  родинами), спільна картка кімнати, «покликати» за стіл, гаманець. Профіль, таблиці й «⏱ Час» переїхали
  в «📊 Хто скільки» й профіль людини — web/people.js.

  Правила, за якими це живе:
  - Картка кімнати створюється рівно один раз (mount) і далі тільки оновлюється (update),
    інакше модуль щоразу губив би свій канвас, таймери й половину стану.
  - Шапка, статус і кнопки перемальовуються окремо від .gbody — тіло належить модулю.
  - Подія 'frame' (до 25 на секунду) не чіпає DOM каркаса взагалі: тільки module.frame().
  Контракт — docs/games/PROTOCOL.md §2–§3, задум — docs/games/ARCHITECTURE.md §10.
*/
(() => {
  'use strict';

  // ---------- те, що дає app.js ----------
  let esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  let toast = (t) => console.log('[games]', t);
  let busy = async (btn, label, fn) => fn();
  let api = async () => { throw new Error('api ще не підключено'); };
  let me = { nick: '', role: 'member' };
  let root = null;

  // ---------- стан ----------
  let conn = null;
  let shown = false;
  let booted = false;
  let catalog = { games: [], stakes: [0] };
  const byId = {};              // id гри → запис каталогу
  const modules = {};           // id гри → модуль (HGames.register)
  const extraPanels = [];       // registerPanel
  const failed = new Set();     // модулі, які не завантажились
  let loading = null;           // проміс завантаження каталогу з модулями (ensureCatalog)
  let names = null;             // проміс самого лише каталогу — назви для балачок (ensureNames)

  let rooms = [];               // останній 'rooms'
  const views = {};             // id кімнати → { room, seat, view }
  const cards = {};             // id кімнати → картка на екрані
  const watched = new Set();    // на що зараз підписані WatchRoom
  const pinned = new Set();     // щойно відкриті соло/приватні кімнати: їх нема в лобі, дивимось за roomId
  let soloNow = [];             // останній 'solo': хто зараз у своїй соло-грі — [{ game, nick }]
  let focusSent;                // що востаннє сказали FocusRoom: id кімнати або null; undefined — ще нічого
  let away = false;             // вкладка давно схована або людина давно нічого не чіпала
  let wallet = null;            // баланс черепків, null — ще не питали
  let newsSeen = null;          // гра → версія «що нового», яку вже бачили; null — ще не питали сервер
  let played = null;            // Set ігор, у які я хоч раз грав (сервер, /api/games/news); null — не знаємо
  const newsShown = new Set();  // кому вже показали в цій вкладці (щоб не вискакувало двічі, поки летить POST)
  let popular = null;           // гра → скільки столів дограли за 30 днів (/api/games/popular); null — не знаємо
  let daily = null;             // останнє /api/games/daily — для смужки «Сьогодні»

  // Групи — чипи-фільтри каталогу; на «Усі» вони ж стають заголовками секцій.
  const GROUPS = [
    { id: 'all', title: 'Усі', icon: '' },
    { id: 'board', title: 'Настільні', icon: '♟' },
    { id: 'live', title: 'Швидкі', icon: '⚡' },
    { id: 'party', title: 'Компанія', icon: '🎉' },
    { id: 'solo', title: 'Соло', icon: '🏺' },
  ];

  // Родини: одна гра в кількох режимах на різну кількість людей. У каталозі — одна плитка, режим обирається у
  // вікні «поставити стіл». Сервер про родини не знає (там окремі ігри зі своїми таблицями), це лише показ.
  // Щоденні головоломки (Сапер дня, Глек-слово) сюди не йдуть: у них своя смужка «Сьогодні».
  const FAMILIES = [
    { id: 'ttt', title: 'Хрестики-нолики', games: [['ttt', 'Класика'], ['ttt3', 'Зникаючі']],
      hint: 'Хто перший виставить три в ряд. У зникаючих у кожного на полі лише три мітки — четверта стирає першу.' },
    { id: 'c4', title: 'Чотири в ряд', games: [['c4', 'Удвох'], ['c4x', 'Компанія на 3–4']],
      hint: 'Кидаєш фішку в колонку, вона падає вниз. Виграє той, хто першим збере чотири в ряд.' },
    { id: 'duel', title: 'Дуель', games: [['duel', 'Двоє'], ['shootout', 'Перестрілка на 3–4']],
      hint: '«Готуйсь… цільсь…» — і на слово ВОГОНЬ тисни першим. Поспішив — куля в небо.' },
    { id: 'snake', title: 'Змійка', games: [['snake', 'Дуель'], ['snake-party', 'Гуртом'], ['snake-coop', 'Одна на всіх']],
      hint: 'Класична змійка: дуель двох, гуртом до чотирьох або одна змійка на всіх, де кожен крутить свої стрілки.' },
    { id: 'tron', title: 'Мотоцикли', games: [['tron', 'Удвох'], ['tron-party', 'Гуртом 2–4']],
      hint: 'За тобою тягнеться стіна, яка не зникає. Хто врізався — програв. Стрілки або WASD.' },
  ];
  const familyOf = {};
  for (const f of FAMILIES) for (const [id] of f.games) familyOf[id] = f;

  /// «🆕 нова гра» — 14 днів від дати, яку модуль каже полем added, і лише тим, хто в неї ще не грав.
  /// «оновлено» — 7 днів від news.v і лише тим, хто вже грав: новенькому все одно все нове.
  const NEW_DAYS = 14;
  const UPD_DAYS = 7;

  // Що зараз на екрані. Адресу дає app.js через HGames.show(tail): '' — лобі,
  // 'room/<id>' — сторінка столу, 'x:<id>' — панель (турнір, пакети Своєї гри).
  let view = { kind: 'lobby', id: '' };
  let full = false;                                               // ⛶ «на весь екран»
  let go = (hash) => { location.hash = hash; };                   // app.js підміняє своїм у init()
  let onTable = null;                                             // app.js: біля якого столу ми стоїмо (балачка столу)
  let onOpenTable = null;                                         // app.js: розгорнути балачку столу
  let onTurn = null;                                              // app.js: за якими столами мій хід, поки я деінде
  let ping = () => {};                                            // app.js: коротке «дзінь»
  let online = () => [];                                          // app.js: хто зараз на сайті
  let askNick = () => {};                                         // app.js: картка «Хто прийшов?»
  let filter = localStorage.getItem('gamesFilter') || 'all';
  let find = '';
  try { ['gamesPanel', 'gamesTimePeriod', 'gamesLbPeriod'].forEach((k) => localStorage.removeItem(k)); } catch { /* переїхало в «Хто скільки» */ }

  const sameNick = (a, b) => String(a || '').toLowerCase() === String(b || '').toLowerCase();
  /// Колір ніка — той самий, що в балачках (web/people.js вантажиться після нас, але малюємо ми вже після всіх).
  const hueOf = (n) => (window.HPeople ? window.HPeople.hue(n) : 0);
  const cssVar = (name, fallback) => getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
  const coarse = () => window.matchMedia('(pointer: coarse)').matches;
  /// Вписати html, лише коли він справді інший. Порівнювати з el.innerHTML не можна: браузер серіалізує по-своєму
  /// (апостроф з esc — &#39; проти ', &quot;, <br/>, лапки атрибутів), і «однаково» майже не траплялось — DOM
  /// перебудовувався на кожен вид. Тому пам'ятаємо свій рядок. Хто міняє дітей елемента сам — не для нього.
  const setHtml = (el, html) => {
    html = html == null ? '' : String(html);
    if (el._gh === html) return false;
    el._gh = html;
    el.innerHTML = html;
    return true;
  };

  /// Місця в RoomSummary приходять як [{ i, nick }]; терпимо і простий масив ніків.
  function nickAt(room, i) {
    const s = room && room.seats && room.seats[i];
    if (s == null) return null;
    return typeof s === 'string' ? (s || null) : (s.nick || null);
  }
  const seatCount = (room) => (room.seats ? room.seats.length : room.maxPlayers || 0);
  const takenSeats = (room) => { let n = 0; for (let i = 0; i < seatCount(room); i++) if (nickAt(room, i)) n++; return n; };
  const freeSeat = (room) => { for (let i = 0; i < seatCount(room); i++) if (!nickAt(room, i)) return i; return -1; };
  function seatOfMe(room) {
    for (let i = 0; i < seatCount(room); i++) if (sameNick(nickAt(room, i), me.nick)) return i;
    return null;
  }
  /// Інший стіл, за яким ми вже сидимо, або null. Сервер тримає нас щонайбільше за одним
  /// мультиплеєрним столом (Rooms.Join, Say.Seated), соло не рахується — тож він завжди один.
  const seatedAt = (exceptId) => rooms.find((r) => r.id !== exceptId && r.maxPlayers > 1 && seatOfMe(r) !== null) || null;

  const gameOf = (id) => byId[id] || null;
  const titleOf = (id) => (byId[id] && byId[id].title) || id;
  const iconOf = (id) => (modules[id] && modules[id].icon) || '<span class="gemo">🎲</span>';
  const groupOf = (id) => (byId[id] && byId[id].group) || 'board';

  // =============================================================================================
  // Хелпери для модулів — HGames.ui (PROTOCOL §3)
  // =============================================================================================

  /// Кнопкова сітка як у хрестиків. Геометрія та сама — оновлюємо клітинки, а не перебудовуємо
  /// сітку: інакше кожен хід гасив би :hover і ламав фокус.
  function grid(host, o) {
    o = o || {};
    const cols = o.cols || 3, rows = o.rows || o.cols || 3, n = cols * rows;
    const cls = 'board' + (o.cls ? ' ' + o.cls : '');
    let el = host.querySelector(':scope > .board');
    if (!el || el.className !== cls || +el.dataset.n !== n) {
      if (el) el.remove();
      el = document.createElement('div');
      el.className = cls;
      el.dataset.n = String(n);
      let html = '';
      for (let i = 0; i < n; i++) html += '<button class="cell" data-i="' + i + '"></button>';
      el.innerHTML = html;
      el.addEventListener('click', (e) => {
        const b = e.target.closest('.cell');
        const opt = el._o || {};
        if (b && !b.disabled && opt.onCell) opt.onCell(+b.dataset.i, b);
      });
      host.appendChild(el);
    }
    // Слухач вішається один раз, а колбек модуль дає новий на кожен update — тримаємо
    // свіжі опції на елементі, інакше кліки б назавжди пішли в onCell із першого виклику.
    el._o = o;
    el.style.setProperty('--cols', cols);
    if (o.cell) {
      const kids = el.children;
      for (let i = 0; i < n; i++) {
        const c = o.cell(i);
        const v = (c && typeof c === 'object') ? c : { html: c == null ? '' : String(c) };
        const b = kids[i];
        const want = 'cell' + (v.cls ? ' ' + v.cls : '');
        if (b.className !== want) b.className = want;
        setHtml(b, v.html);
        const dis = !!v.disabled;
        if (b.disabled !== dis) b.disabled = dis;
      }
    }
    return el;
  }

  /// Канвас із логічними координатами w×h: модуль малює в них, а DPR і розтяг по ширині — наша справа.
  function canvas(host, o) {
    o = o || {};
    const w = o.w || 320, h = o.h || 200;
    const cls = 'gcanvas' + (o.cls ? ' ' + o.cls : '');
    let el = host.querySelector(':scope > canvas.gcanvas');
    if (!el) {
      el = document.createElement('canvas');
      host.appendChild(el);
    }
    if (el.className !== cls) el.className = cls;
    el.style.aspectRatio = w + ' / ' + h;
    const c = { el, ctx: el.getContext('2d'), w, h, resize };
    function resize() {
      const dpr = Math.min(3, window.devicePixelRatio || 1);
      const pw = Math.round(w * dpr), ph = Math.round(h * dpr);
      if (el.width !== pw || el.height !== ph) { el.width = pw; el.height = ph; }
      c.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    }
    resize();
    return c;
  }

  /// Хрестовина під палець. На миші її нема взагалі — там стрілки зручніші.
  function dpad(host, onDir, dirs) {
    let el = host.querySelector(':scope > .dpad');
    if (!coarse()) { if (el) el.remove(); return null; }
    const want = (dirs || [3, 2, 1, 0]).join(',');
    if (el && el.dataset.dirs === want) { el._onDir = onDir; return el; }
    if (el) el.remove();
    el = document.createElement('div');
    el.className = 'dpad';
    el.dataset.dirs = want;
    const label = { 0: '→', 1: '↓', 2: '←', 3: '↑' };
    const aria = { 0: 'праворуч', 1: 'вниз', 2: 'ліворуч', 3: 'вгору' };
    el.innerHTML = (dirs || [3, 2, 1, 0]).map((d) => '<button type="button" data-dir="' + d + '" aria-label="' + aria[d] + '">' + label[d] + '</button>').join('');
    el.addEventListener('click', (e) => {
      const b = e.target.closest('button');
      if (b && el._onDir) el._onDir(+b.dataset.dir);
    });
    el._onDir = onDir;                 // колбек — завжди з останнього виклику, не з першого
    host.appendChild(el);
    return el;
  }

  const KB_ROWS = ['йцукенгшщзхї', 'фівапролджєґ', 'ячсмитьбю'];

  /// Екранна українська клавіатура. state — { 'а': 'G'|'Y'|'B' }, як у Глек-слова.
  function keyboardUa(host, onKey, state) {
    let el = host.querySelector(':scope > .gkbd');
    if (!el) {
      el = document.createElement('div');
      el.className = 'gkbd';
      el.innerHTML = KB_ROWS.map((r, ri) => {
        let row = r.split('').map((ch) => '<button type="button" class="gkey" data-k="' + ch + '">' + ch + '</button>').join('');
        if (ri === 2) row = '<button type="button" class="gkey wide" data-k="Enter">Enter</button>' + row + '<button type="button" class="gkey wide" data-k="Backspace">⌫</button>';
        return '<div class="gkrow">' + row + '</div>';
      }).join('');
      el.addEventListener('click', (e) => {
        const b = e.target.closest('.gkey');
        if (b && el._onKey) el._onKey(b.dataset.k);
      });
      host.appendChild(el);
    }
    el._onKey = onKey;                 // так само, як у grid: живий колбек, а не той, що був на створенні
    const st = state || {};
    el.querySelectorAll('.gkey').forEach((b) => {
      const k = b.dataset.k;
      const s = k.length === 1 ? (st[k] || '') : '';
      const want = 'gkey' + (k.length > 1 ? ' wide' : '') + (s ? ' ' + s.toLowerCase() : '');
      if (b.className !== want) b.className = want;
    });
    return el;
  }

  const lerp = (a, b, t) => a + (b - a) * t;

  /// Інтерполятор для ігор на 25 Гц: тримає два останні кадри й каже, де ми між ними
  /// на «зараз мінус один інтервал» — так рух не смикається на кожному повідомленні.
  function Interp() {
    let prev = null, last = null, prevAt = 0, lastAt = 0;
    return {
      push(f) {
        const t = performance.now();
        prev = last; prevAt = lastAt;
        last = f; lastAt = t;
        if (!prev) { prev = f; prevAt = t; }
      },
      reset() { prev = last = null; prevAt = lastAt = 0; },
      /// { a: старіший кадр, b: новіший, t: 0..1 }
      at() {
        if (!last) return null;
        const span = Math.max(1, lastAt - prevAt);
        const target = performance.now() - span;      // навмисно відстаємо на один інтервал
        const t = Math.max(0, Math.min(1, (target - prevAt) / span));
        return { a: prev, b: last, t };
      },
    };
  }

  /// Дуга-таймер фази: сама крутиться на rAF і сама вмирає, коли картку прибрали.
  /// Кликати можна з кожного update(): другий виклик лише переставляє час, а не заводить
  /// ще один rAF-цикл на тому самому елементі.
  function timerArc(host, untilIso, totalMs) {
    let el = host.querySelector(':scope > .garc');
    if (el && el._arc) { el._arc.set(untilIso, totalMs); return el._arc; }
    if (!el) {
      el = document.createElement('div');
      el.className = 'garc';
      el.innerHTML = '<svg viewBox="0 0 40 40"><circle class="bg" cx="20" cy="20" r="17"></circle>'
        + '<circle class="fg" cx="20" cy="20" r="17" transform="rotate(-90 20 20)"></circle></svg><b>0</b>';
      host.appendChild(el);
    }
    const fg = el.querySelector('.fg'), num = el.querySelector('b');
    const LEN = 2 * Math.PI * 17;
    fg.style.strokeDasharray = LEN;
    const st = { until: Date.parse(untilIso) || Date.now(), total: totalMs || 1000, raf: 0 };
    function step() {
      if (!el.isConnected) { st.raf = 0; return; }
      const left = Math.max(0, st.until - Date.now());
      const k = Math.max(0, Math.min(1, left / st.total));
      fg.style.strokeDashoffset = LEN * (1 - k);
      const s = String(Math.ceil(left / 1000));
      if (num.textContent !== s) num.textContent = s;
      // На нулі дуга вже порожня: далі крутити кадри — лише палити батарею, поки фаза чекає сервера.
      // Новий час принесе set(), і він заведе цикл знову.
      st.raf = left > 0 ? requestAnimationFrame(step) : 0;
    }
    st.raf = requestAnimationFrame(step);
    const handle = {
      el,
      set(u, total) { st.until = Date.parse(u) || Date.now(); st.total = total || st.total; if (!st.raf) st.raf = requestAnimationFrame(step); },
      stop() { cancelAnimationFrame(st.raf); st.raf = 0; if (el._arc === handle) el._arc = null; },
    };
    el._arc = handle;
    return handle;
  }

  /// Віяло карт (дурень) або кісток (доміно). Рука міняється щохода, тож актуальні картки
  /// й колбек живуть на елементі: інакше клік віддавав би модулю карту з першого виклику.
  function hand(host, items, o) {
    o = o || {};
    items = items || [];
    let el = host.querySelector(':scope > .ghand');
    if (!el) {
      el = document.createElement('div');
      el.className = 'ghand';
      el.addEventListener('click', (e) => {
        const c = e.target.closest('.gcard');
        if (!c || c.classList.contains('off')) return;
        const list = el._items || [], opt = el._o || {};
        const cb = opt.onItem || opt.onCard;      // onCard — старе ім'я з PROTOCOL §3
        const i = +c.dataset.i;
        if (opt.selectable) {
          const on = c.classList.toggle('sel');
          if (!opt.multi) el.querySelectorAll('.gcard.sel').forEach((x) => { if (x !== c) x.classList.remove('sel'); });
          if (cb) cb(list[i], i, on);
        } else if (cb) cb(list[i], i, true);
      });
      host.appendChild(el);
    }
    el._items = items;
    el._o = o;
    const render = o.render || ((it) => esc(typeof it === 'object' ? (it.label || it.text || '') : it));
    const html = items.map((it, i) => {
      const off = it && typeof it === 'object' && it.disabled ? ' off' : '';
      const cls = it && typeof it === 'object' && it.cls ? ' ' + it.cls : '';
      return '<div class="gcard' + off + cls + '" data-i="' + i + '">' + render(it, i) + '</div>';
    }).join('');
    if (el.dataset.sig !== html) { el.dataset.sig = html; el.innerHTML = html; }
    return {
      el,
      selected: () => [...el.querySelectorAll('.gcard.sel')].map((c) => (el._items || [])[+c.dataset.i]),
      clear: () => el.querySelectorAll('.gcard.sel').forEach((c) => c.classList.remove('sel')),
    };
  }

  /// Подію зробила людина? Натиск на джойстику — теж людина, просто не мишею: шар пада
  /// (web/static/pad.js) ставить своїм подіям позначку `hpad`. Скрізь, де захист від скриптів
  /// питав саме `ev.isTrusted` (Око майстра Гончарного кола), має стояти оце.
  const human = (ev) => !!ev && (ev.isTrusted || ev.hpad === true);

  const ui = { grid, canvas, dpad, keyboardUa, lerp, Interp, timerArc, hand, css: cssVar, coarse, human, html: setHtml };

  // =============================================================================================
  // Хаб
  // =============================================================================================

  /// Та сама відмова раз за разом (коло під нічним відбоєм шле пачку кліків за пачкою) — один тост на кілька секунд, а не стос.
  let lastErr = { text: '', at: 0 };
  function errToast(text) {
    const now = Date.now();
    if (text === lastErr.text && now - lastErr.at < 4000) return;
    lastErr = { text, at: now };
    toast(text, 'err');
  }

  /// Виклик хаба, що повертає RoomReply: помилку показуємо тостом, успіх — лише якщо є що сказати.
  async function call(method, ...args) {
    if (!conn || conn.state !== 'Connected') { toast('Халепа: зв\'язку з сервером нема', 'err'); return { ok: false, message: '' }; }
    try {
      const r = await conn.invoke(method, ...args);
      if (!r) return { ok: true, message: '' };
      if (!r.ok) errToast(r.message || 'От халепа — не вийшло');
      else if (r.message) toast(r.message, 'ok');
      return r;
    } catch (e) {
      toast('Ой-йой, не вийшло: ' + e.message, 'err');
      return { ok: false, message: e.message };
    }
  }
  const send = (method, ...args) => { if (conn && conn.state === 'Connected') conn.invoke(method, ...args).catch(() => {}); };

  const PIN_TTL = 5000;

  /// Соло і щоденні кімнати приватні: у списку лобі їх нема, тож єдиний спосіб їх побачити —
  /// підписатись за roomId із відповіді. Тримаємо його, поки не з'явиться картка.
  async function openRoom(method, ...args) {
    const r = await call(method, ...args);
    if (r.ok && r.roomId) {
      const id = r.roomId;
      pinned.add(id);
      syncWatch();
      if (method !== 'CreateRoom') go('#games/room/' + encodeURIComponent(id));
      // Кімнати може й не бути (сервер її вже прибрав, WatchRoom відмовив): щоб не тримати
      // підписку на мертвий id вічно, знімаємо шпильку, якщо 'room' так і не прийшла.
      setTimeout(() => {
        if (pinned.has(id) && !cards[id] && !views[id]) { pinned.delete(id); syncWatch(); }
      }, PIN_TTL);
    }
    return r;
  }

  /// Так/ні окремим вікном. Проміс: true — натиснули дію, false — передумали (Скасувати, Esc, клік повз картку).
  /// `html` уже екранований тим, хто його зібрав; `text` екрануємо самі.
  function ask(o) {
    return new Promise((done) => {
      const wrap = document.createElement('div');
      wrap.className = 'modal gmodal gask';
      wrap.innerHTML = '<div class="card">'
        + '<h3>' + esc(o.title || 'Точно?') + '</h3>'
        + '<div class="gask-text">' + (o.html || esc(o.text || '')) + '</div>'
        + '<div class="grow"><button class="primary" type="button" data-yes data-pad-first>' + esc(o.ok || 'Гаразд') + '</button>'
        + '<button class="ghost" type="button" data-close>' + esc(o.cancel || 'Скасувати') + '</button></div></div>';
      const close = (v) => {
        if (!wrap.isConnected) return;
        wrap.remove();
        document.removeEventListener('keydown', onKey, true);
        done(v);
      };
      // Ловимо Esc раніше за всіх: інакше він вийшов би ще й зі столу, над яким висить це вікно.
      const onKey = (e) => { if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); close(false); } };
      wrap.addEventListener('click', (e) => { if (e.target === wrap) close(false); });
      wrap.querySelector('[data-close]').onclick = () => close(false);
      wrap.querySelector('[data-yes]').onclick = () => close(true);
      document.body.appendChild(wrap);
      document.addEventListener('keydown', onKey, true);
      wrap.querySelector('[data-yes]').focus();
    });
  }

  /// Сісти за стіл. Сидиш за іншим — раніше кнопки просто не було, і доводилось іти назад, вставати,
  /// вертатись і сідати знову. Тепер питаємо тут і встаємо самі. true — сіли.
  async function joinRoom(id, btn) {
    const other = seatedAt(id);
    if (other) {
      const playing = other.status === 'playing';
      const stake = playing && other.stake ? ', а ставка 🏺' + other.stake + ' лишиться суперникові' : '';
      const ok = await ask({
        title: 'Ти вже за столом',
        html: '<b>' + iconOf(other.game) + esc(titleOf(other.game)) + '</b>'
          + (playing
            ? ' — там іде партія. Якщо встанеш зараз, вона зарахується як поразка' + stake + '.'
            : ' — партія ще не почалась, встати можна без наслідків.'),
        ok: playing ? 'Все одно встати й сісти' : 'Встати і сісти сюди',
        cancel: 'Лишитись там',
      });
      if (!ok) return false;
    }
    // Кнопку крутимо лише навколо самих викликів: поки людина читає питання, «сідаю…» на ній недоречне.
    const sit = async () => {
      if (other && !(await call('LeaveRoom', other.id)).ok) return false;
      return !!(await call('JoinRoom', id)).ok;
    };
    return btn ? !!(await busy(btn, 'підсідаю…', sit)) : sit();
  }

  /// Кадри просимо лише для відкритого столу, щойно відкритих приватних кімнат і тих, де сидимо:
  /// за останніми треба стежити й з лобі («твій хід» на резюме), решта — десятки повідомлень на секунду дарма.
  function syncWatch() {
    const want = new Set();
    if (shown) {
      if (view.kind === 'room' && view.id) want.add(view.id);
      for (const id of pinned) want.add(id);
      for (const id in views) if (views[id].seat != null || views[id].loose) want.add(id);
    } else {
      // Поза «Іграми» стежимо лише за покроковими столами, де сидимо: щоб сказати «🎲 Твій хід» з ефіру чи бібліотеки.
      // Реалтайм сюди не беремо — там 25 кадрів на секунду, а хід і так не чекає.
      for (const id in views) {
        const rv = views[id];
        const g = rv && rv.room && byId[rv.room.game];
        if (rv.seat != null && rv.room.maxPlayers > 1 && g && !(g.tickMs > 0)) want.add(id);
      }
    }
    for (const id of [...watched]) if (!want.has(id)) { watched.delete(id); send('UnwatchRoom', id); }
    for (const id of want) if (!watched.has(id)) { watched.add(id); send('WatchRoom', id); }
    syncFocus();
    syncHere();
  }

  // ---------------------------------------------------------------------------------------------
  // «Хто зараз грає» у соло. Соло-кімнати приватні, у лобі їх нема, а підписка на свою тримається й з лобі —
  // тож сервер знає це лише з наших слів (FocusRoom): на екрані власна соло-гра, і людина справді тут.
  // Лобі, інший підрозділ, радіо, вкладка, схована понад хвилину, чи п'ять хвилин без жодного руху — уже ні.
  // ---------------------------------------------------------------------------------------------

  const AWAY_HIDDEN_MS = 60 * 1000;
  const AWAY_IDLE_MS = 5 * 60 * 1000;
  let lastInput = Date.now();
  let lastAct = Date.now();     // як lastInput, але без руху мишки: клік, дотик, клавіша, коліщатко, пад
  let hiddenAt = document.hidden ? Date.now() : 0;

  function syncFocus() {
    const rv = shown && !away && view.kind === 'room' ? views[view.id] : null;
    const want = rv && rv.seat != null && rv.room.maxPlayers === 1 ? rv.room.id : null;
    if (want === focusSent) return;
    focusSent = want;
    send('FocusRoom', want);
  }

  function checkAway() {
    const now = Date.now();
    const was = away;
    away = (!!hiddenAt && now - hiddenAt >= AWAY_HIDDEN_MS) || now - lastInput >= AWAY_IDLE_MS;
    if (away !== was) syncFocus();
    syncHere();
  }
  // Ловимо на спуску (capture): гра може зупинити свою подію, а пад (web/static/pad.js) шле ті самі keydown і pointer*.
  ['pointerdown', 'pointermove', 'keydown', 'wheel', 'touchstart'].forEach((type) =>
    document.addEventListener(type, () => {
      lastInput = Date.now();
      if (type !== 'pointermove') lastAct = lastInput;
      if (away) checkAway();
      else if (hereQuiet) syncHere();   // облік вимкнувся за тишею — вмикаємо з першим же рухом, а не за 15 с
    }, { capture: true, passive: true }));
  document.addEventListener('visibilitychange', () => {
    hiddenAt = document.hidden ? Date.now() : 0;
    if (!document.hidden) lastInput = Date.now();      // повернувся у вкладку — отже, тут
    checkAway();
  });
  setInterval(checkAway, 15000);

  // ---------------------------------------------------------------------------------------------
  // Де людина (Here) — для сторінки «⏱ Час»: стіл на екрані, розділ «Ігри» поза столом, решта сайту — або ніде.
  // Рахується лише видима вкладка, і лише поки людина щось робить: схована вкладка вимикається одразу, а не за
  // хвилину, як «соло зараз». Коли вимикаємось через бездіяльність, кажемо, скільки її вже було, — цей хвіст
  // сервер не зарахує. Сидимо за столом чи дивимось — сервер вирішує сам; ми лише шлемо заново, коли місце змінилось.
  // ---------------------------------------------------------------------------------------------

  const HERE_IDLE_SOLO_MS = 5 * 60 * 1000;
  const HERE_IDLE_LOBBY_MS = 5 * 60 * 1000;
  const HERE_IDLE_LONG_MS = 10 * 60 * 1000;   // за столом і на радіо можна довго лише дивитись і слухати
  // Гончарне коло: глек раз на кілька хвилин — ще не гра. Рахуємо, лише поки кліки йдуть частіше ніж раз на 30 с,
  // і мишка, що просто ворушиться над столом, тут не рахується.
  const HERE_IDLE_CLICKER_MS = 30 * 1000;
  let hereSent;                               // останній підпис сказаного Here; undefined — ще нічого
  let hereQuiet = false;                      // Here вимкнули за тишею — перший рух має ввімкнути одразу

  function syncHere() {
    let where = null;
    let room = null;
    let limit = HERE_IDLE_LONG_MS;
    let since = lastInput;
    let seated = false;
    if (!document.hidden) {
      if (!shown) where = 'page';
      else if (view.kind === 'room' && view.id) {
        const rv = views[view.id];
        where = 'room';
        room = view.id;
        seated = !!(rv && rv.seat != null);
        if (seated && rv.room && rv.room.game === 'clicker') { limit = HERE_IDLE_CLICKER_MS; since = lastAct; }
        else if (seated && rv.room && rv.room.maxPlayers === 1) limit = HERE_IDLE_SOLO_MS;
      } else { where = 'lobby'; limit = HERE_IDLE_LOBBY_MS; }
    }
    let idle = 0;
    const quiet = Date.now() - since;
    hereQuiet = !!where && quiet >= limit;
    if (hereQuiet) { where = null; room = null; idle = quiet; }
    const sig = where ? where + '|' + (room || '') + '|' + (seated ? 1 : 0) : '';
    if (sig === hereSent) return;
    hereSent = sig;
    send('Here', where, room, Math.round(idle));
  }

  /// Хто зараз у соло-грі gameId — ніки з останньої події 'solo'.
  const playingIn = (gameId) => soloNow.filter((p) => p.game === gameId).map((p) => p.nick);
  /// «Оля, Петро, Ганна і ще 2»: на плитку довгий список не влазить, повний — у підказці.
  const whoShort = (nicks, max) => (nicks.length <= max ? nicks.join(', ')
    : nicks.slice(0, max).join(', ') + ' і ще ' + (nicks.length - max));
  const whoTitle = (nicks) => (nicks.length > 1 ? 'Зараз грають: ' : 'Зараз грає: ') + nicks.join(', ');

  // =============================================================================================
  // Завантажувач модулів
  // =============================================================================================

  function loadScript(src) {
    return new Promise((resolve) => {
      const s = document.createElement('script');
      s.src = src;
      s.onload = () => resolve(true);
      s.onerror = () => resolve(false);
      document.head.appendChild(s);
    });
  }

  /// Файл модуля гри: типово <id>.js, але родина ігор може жити в одному файлі
  /// (зникаючі хрестики — у ttt.js), і тоді сервер каже це полем module каталогу.
  const moduleOf = (g) => g.module || g.id;

  const loadedFiles = new Map();   // файл модуля → проміс завантаження: двічі той самий не тягнемо

  const loadFile = (f) => {
    if (!loadedFiles.has(f)) loadedFiles.set(f, loadScript('/games/' + f + '.js'));
    return loadedFiles.get(f);
  };

  function addCss(g, f) {
    if (!g.hasCss || document.querySelector('link[data-game="' + f + '"]')) return;
    const l = document.createElement('link');
    l.rel = 'stylesheet';
    l.href = '/games/' + f + '.css';
    l.dataset.game = f;
    document.head.appendChild(l);
  }

  /// Іконку гри знає лише її модуль, а балачки згадують стіл ще до того, як людина зайшла в «Ігри».
  /// Тягнемо рівно один файл (разом із його css, інакше пізній loadModules його проґавить) і кличемо
  /// ready(), коли модуль зареєструвався. Поки він летить, на кнопці стоїть 🎲 — і це не помилка.
  function ensureIcon(gameId, ready) {
    if (modules[gameId]) return;
    // Спершу каталог: без нього ми не знаємо навіть, у якому файлі ця гра живе.
    ensureNames().then(() => {
      const g = byId[gameId];
      if (!g || modules[gameId]) return;
      addCss(g, moduleOf(g));
      return loadFile(moduleOf(g));
    }).then(() => { if (modules[gameId] && ready) ready(); })
      .catch(() => { /* каталог не прочитався — лишається 🎲, і це не привід шуміти */ });
  }

  /// Вантажимо всі модулі одразу: у каталозі їх буде два десятки, а послідовні await —
  /// це два десятки round-trip-ів поспіль. Один файл вантажимо рівно раз, скільки б ігор у ньому
  /// не реєструвалось. Вердикт «не завантажився» ставимо лише коли все відстрілялось.
  async function loadModules() {
    const want = catalog.games.filter((g) => !modules[g.id]);
    const files = [...new Set(want.map(moduleOf))];
    for (const g of want) addCss(g, moduleOf(g));
    const res = await Promise.all(files.map(async (f) => [f, await loadFile(f)]));
    const loaded = Object.fromEntries(res);
    for (const g of want) {
      if (modules[g.id]) continue;
      failed.add(g.id);
      const f = moduleOf(g);
      console.warn('[games] модуль ' + g.id + ' не завантажився'
        + (loaded[f] ? ' (є ' + f + '.js, але register(' + g.id + ') не викликано)' : ' (нема ' + f + '.js)'));
    }
    refreshAll();
  }

  /// Самі назви ігор, без двох десятків модулів: стільки треба балачкам, щоб написати «Мафія», а не
  /// «mafia». Запит той самий і кешується разом із повним ensureCatalog().
  function ensureNames() {
    if (names) return names;
    names = api('GET', '/api/games/catalog').then((c) => {
      catalog = { games: (c && c.games) || [], stakes: (c && c.stakes) || [0] };
      for (const g of catalog.games) byId[g.id] = g;
      return catalog;
    }).catch((e) => {
      names = null;
      console.warn('[games] каталог не прочитався', e);
      throw e;
    });
    return names;
  }

  function ensureCatalog() {
    if (loading) return loading;
    loadNews();
    loading = ensureNames().then(() => {
      renderShell();
      renderView();
      return loadModules();
    }).catch((e) => {
      loading = null;
      console.warn('[games] каталог не прочитався', e);
      // Помилку пишемо лише в тіло: знести шапку разом із кнопками означало б «повертайся через F5».
      renderShell();
      const v = root && root.querySelector('.gview');
      if (!v) return;
      v.innerHTML = '<div class="gempty">Ой-йой, каталог ігор не прочитався: ' + esc(e.message)
        + ' <button class="ghost" data-retry>Ану ще раз</button></div>';
      const b = v.querySelector('[data-retry]');
      if (b) b.onclick = (ev) => busy(ev.currentTarget, 'мить…', () => ensureCatalog());
    });
    return loading;
  }

  // ---------------------------------------------------------------------------------------------
  // Оновлення без F5 (app.js, checkFront → HGames.refresh). Каталог перечитуємо — нові ігри з'являються самі.
  // Змінений модуль гри вантажимо ще раз (?v=відбиток, щоб браузер не взяв старий), а його приховані картки знімаємо:
  // відкриє стіл — змонтується вже новим модулем. Стіл, що зараз перед очима, не чіпаємо (старе тіло з новим update
  // не живе) — чекаємо, поки людина з нього піде. Модуль із власними частинами (коло тягне clicker-*.js, своя гра —
  // svoya-packs.js) так не перезбереш — його, як і каркас, app.js лишає плашці «Сайт оновився».
  // ---------------------------------------------------------------------------------------------

  const staleMods = new Map();   // файл модуля → { js, css }: нові відбитки, що чекають, поки людина встане з його столу

  const isModuleFile = (f) => catalog.games.some((g) => moduleOf(g) === f);
  const fileOfRoom = (id) => { const g = views[id] && byId[views[id].room.game]; return g ? moduleOf(g) : null; };
  const openNow = (f) => shown && view.kind === 'room' && !!cards[view.id] && fileOfRoom(view.id) === f;
  /// Модуль сам довантажив свої частини: games/<файл>-щось.js, що не є окремим модулем.
  const hasParts = (f) => [...document.scripts].some((s) => {
    const m = /^\/games\/([^/]+)\.js$/.exec(new URL(s.src || '', location.href).pathname);
    return !!m && m[1].startsWith(f + '-') && !isModuleFile(m[1]);
  });

  function swapCss(f, v) {
    const l = document.querySelector('link[data-game="' + f + '"]');
    if (l && v) l.href = '/games/' + f + '.css?v=' + encodeURIComponent(v);
  }
  function swapModule(f, ver) {
    for (const id in cards) if (fileOfRoom(id) === f) dropCard(id);
    swapCss(f, ver.css);
    loadedFiles.set(f, loadScript('/games/' + f + '.js?v=' + encodeURIComponent(ver.js)));
  }
  function flushStale() {
    for (const [f, ver] of staleMods) if (!openNow(f)) { staleMods.delete(f); swapModule(f, ver); }
  }

  /// changed — змінені файли web/ і їхні нові відбитки. Вертає ті, що підхоплено тут.
  function refreshFront(changed) {
    const taken = [];
    const byFile = new Map();
    for (const path in changed) {
      const m = /^games\/([^/]+)\.(js|css)$/.exec(path);
      if (!m || !loadedFiles.has(m[1]) || !isModuleFile(m[1]) || hasParts(m[1])) continue;
      taken.push(path);
      const ver = byFile.get(m[1]) || staleMods.get(m[1]) || {};
      ver[m[2]] = changed[path];
      byFile.set(m[1], ver);
    }
    for (const [f, ver] of byFile) {
      // Лише стиль — міняємо одразу, навіть на відкритому столі: код той самий, що його чекає.
      if (!ver.js) swapCss(f, ver.css);
      else staleMods.set(f, ver);
    }
    flushStale();
    // Каталог наново — могли з'явитись ігри чи нові варіанти в старих. Лише якщо його вже брали.
    if (names) {
      const full = !!loading;
      names = null;
      loading = null;
      if (full) ensureCatalog(); else ensureNames().catch(() => { /* назви підтягнуться з наступним 'rooms' */ });
    }
    return taken;
  }

  // ---------------------------------------------------------------------------------------------
  // «Що нового»: модуль гри каже news: { v, title, items }, сервер пам'ятає, яку версію нік бачив
  // (/api/games/news). Вікно — один раз при першому заході на стіл цієї гри; на плитці лобі — «✨ нове».
  // Гість (без збереження на сервері) пам'ятає в localStorage.
  // ---------------------------------------------------------------------------------------------

  const NEWS_LS = 'gamesNewsSeen';
  function localNews() {
    try { return JSON.parse(localStorage.getItem(NEWS_LS) || '{}') || {}; } catch { return {}; }
  }
  async function loadNews() {
    let server = {};
    try {
      const r = await api('GET', '/api/games/news');
      server = (r && r.seen) || {};
      // Старий сервер played не знає — тоді й «оновлено» показуємо, як раніше, усім.
      played = r && Array.isArray(r.played) ? new Set(r.played) : null;
    } catch { /* нема сервера — хоч локальне */ }
    newsSeen = Object.assign(localNews(), server);
    if (shown && view.kind === 'lobby') renderView();
    if (shown && view.kind === 'room' && views[view.id]) maybeNews(views[view.id]);
  }
  const daysSince = (iso) => (Date.now() - Date.parse(iso + 'T12:00:00')) / 86400000;
  const newsOf = (id) => { const m = modules[id]; return m && m.news && m.news.v && (m.news.items || []).length ? m.news : null; };
  const playedIt = (id) => played == null || played.has(id);
  /// Оновлення, якого людина ще не бачила, у грі, в яку вона вже грала (вікно «що нового» — без терміну давності).
  const unseenNews = (id) => { const n = newsOf(id); return !!n && newsSeen != null && newsSeen[id] !== n.v && playedIt(id); };
  /// Позначка «оновлено» на плитці — лише перший тиждень: місячної давнини «оновлено» вже нічого не каже.
  const hasNews = (id) => unseenNews(id) && !(daysSince(newsOf(id).v) > UPD_DAYS);
  /// Нова гра: модуль каже added, минуло менше двох тижнів, і я в неї ще не грав.
  const isNewGame = (id) => {
    const m = modules[id];
    return !!(m && m.added) && daysSince(m.added) <= NEW_DAYS && !(played && played.has(id));
  };

  function markNews(id, v) {
    if (newsSeen) newsSeen[id] = v;
    try { const l = localNews(); l[id] = v; localStorage.setItem(NEWS_LS, JSON.stringify(l)); } catch { /* приватне вікно */ }
    api('POST', '/api/games/news', { game: id, v }).catch(() => { /* наступного разу покажемо ще раз — не біда */ });
  }

  /// Показати «що нового» для столу, що зараз на екрані. Посеред власної партії не лізе поперед гри:
  /// дочекається, поки вона скінчиться (refreshCard кличе нас на кожне оновлення).
  function maybeNews(rv) {
    if (!rv || !rv.room || !shown || view.kind !== 'room' || view.id !== rv.room.id) return;
    const id = rv.room.game;
    // Хто в цю гру ще не грав, тому «було так, стало так» ні до чого: тихо позначаємо, що бачив, і не заважаємо.
    const n0 = newsOf(id);
    if (n0 && newsSeen != null && newsSeen[id] !== n0.v && !playedIt(id)) { markNews(id, n0.v); return; }
    if (!unseenNews(id) || newsShown.has(id)) return;
    // Посеред партії — нікому, не лише тим, хто сидить: після F5 місце впізнається не одразу (вид ще з лобі),
    // і вікно вискакувало поверх «Я знаю!». Глядачеві воно теж закриває гру. Дочекаємось кінця партії.
    if (rv.view === undefined) return;
    if (rv.room.status === 'playing' && rv.room.maxPlayers > 1) return;
    if (document.querySelector('.modal.gmodal')) return;          // інше вікно вже висить — наступного разу
    const n = newsOf(id);
    newsShown.add(id);
    const wrap = document.createElement('div');
    wrap.className = 'modal gmodal gnews';
    wrap.innerHTML = '<div class="card">'
      + '<div class="gnews-kick">✨ Що нового</div>'
      + '<h3>' + iconOf(id) + esc(n.title || titleOf(id)) + '</h3>'
      + '<ul class="gnews-list">' + n.items.map((t) => '<li>' + esc(t) + '</li>').join('') + '</ul>'
      + '<div class="grow"><button class="primary" type="button" data-ok data-pad-first>Ясно, гайда грати!</button></div></div>';
    const close = () => {
      if (!wrap.isConnected) return;
      wrap.remove();
      document.removeEventListener('keydown', onKey, true);
    };
    const onKey = (e) => { if (e.key === 'Escape' || e.key === 'Enter') { e.preventDefault(); e.stopPropagation(); close(); } };
    wrap.addEventListener('click', (e) => { if (e.target === wrap) close(); });
    wrap.querySelector('[data-ok]').onclick = close;
    document.body.appendChild(wrap);
    document.addEventListener('keydown', onKey, true);
    wrap.querySelector('[data-ok]').focus();
    markNews(id, n.v);                         // показали — значить бачив, навіть якщо закриє F5-ом
    if (root && root.querySelector('.gtiles')) renderView();
  }

  async function loadWallet() {
    try {
      const w = await api('GET', '/api/games/wallet');
      wallet = typeof w === 'number' ? w : (w && (w.balance != null ? w.balance : w.shards));
      paintWallet();
    } catch { /* економіки ще нема — рядок гаманця просто мовчить */ }
  }
  /// Гаманець живе в шапці сайту (#hdrWallet), а не в лобі: черепки витрачають і поза іграми.
  /// Поки балансу нема — малюємо «—», а не ховаємо рядок: інакше шапка стрибала б на кожному вході.
  function paintWallet() {
    const el = document.querySelector('#hdrWallet b');
    if (el) el.textContent = wallet == null ? '—' : String(wallet);
  }

  // =============================================================================================
  // Каркас сторінки
  // =============================================================================================

  function renderShell() {
    if (!root) return;
    if (!root.querySelector('.gbar')) {
      // .gtables — склад змонтованих карток. Картка кімнати створюється один раз і далі лише
      // переїжджає складу ↔ сторінка столу: перестворити її означало б відібрати в модуля
      // канвас, таймери й половину стану (ARCHITECTURE §10).
      // Рядка вкладок більше нема: профіль, таблиці й час переїхали в «📊 Хто скільки» і профіль людини,
      // щоденне — у смужку «Сьогодні» в лобі. .gbar лишився шапкою панелей (турнір, пакети Своєї гри).
      root.innerHTML = '<div class="gbar" hidden></div>'
        + '<div class="groom" hidden><div class="grhead"></div><div class="grbox"></div></div>'
        + '<div class="gview"></div><div class="gtables" hidden></div>';
    }
    const bar = root.querySelector('.gbar');
    const panel = view.kind === 'panel' ? extraPanels.find((p) => 'x:' + p.id === view.id) : null;
    bar.hidden = view.kind !== 'panel';
    if (view.kind === 'panel') {
      bar.innerHTML = '<button class="ghost grback" type="button" title="Назад у лобі — Esc">← Лобі</button>'
        + '<span class="grtitle">' + (panel ? '<span class="gemo">' + (panel.icon || '📋') + '</span>' + esc(panel.title) : '') + '</span>';
      bar.querySelector('.grback').onclick = () => go('#games');
    }
    paintWallet();
    paintRoomCount();
  }

  /// Скільки живих столів — видно ще з шапки сайту, не заходячи в «Ігри».
  function paintRoomCount() {
    const el = document.getElementById('navGames');
    if (!el) return;
    el.textContent = rooms.length ? String(rooms.length) : '';
    el.hidden = !rooms.length;
  }

  /// Адреса всередині розділу: '' — лобі, 'room/<id>' — стіл, решта — підрозділ.
  function route(tail) {
    const t = String(tail || '');
    const next = t.startsWith('room/') ? { kind: 'room', id: decodeURIComponent(t.slice(5)) }
      : t.startsWith('x:') ? { kind: 'panel', id: t }
        : { kind: 'lobby', id: '' };
    const same = next.kind === view.kind && next.id === view.id;
    view = next;
    if (view.kind !== 'room') setFull(false);
    if (!same) chromeFor = null;          // повернулись у підрозділ — перечитуємо профіль/таблицю
    renderShell();
    renderView();
  }

  function setFull(on) {
    full = !!on && view.kind === 'room';
    document.body.classList.toggle('gfull', full);
    notifyTable();   // на весь екран панелі нема — балачка столу переїжджає в шторку
  }

  /// Стіл, біля якого людина зараз стоїть: лише сторінка столу й лише стіл на кількох (соло говорити нема з ким).
  /// main — гра, де розмова і є гра (мафія): балачку столу там розгортаємо самі.
  function tableInfo() {
    if (!shown || view.kind !== 'room' || !view.id) return null;
    const rv = views[view.id];
    if (!rv || !rv.room || rv.loose || (rv.room.maxPlayers || 0) <= 1) return null;
    const g = rv.room.game;
    return { id: rv.room.id, game: g, title: titleOf(g), main: !!(modules[g] && modules[g].talk === 'main'), seat: rv.seat, status: rv.room.status };
  }
  let tableSig = null;
  /// Сказати app.js, що змінився стіл (або його стан, або ⛶). Однакове двічі не кажемо.
  function notifyTable() {
    const t = tableInfo();
    const sig = JSON.stringify(t) + '|' + full;
    if (sig === tableSig) return;
    tableSig = sig;
    if (onTable) { try { onTable(t, { full }); } catch (e) { console.warn('[games] onTable', e); } }
  }

  /// Картки лише переносяться між складом і сторінкою столу й ховаються через hidden.
  function placeCards(openId) {
    const box = root.querySelector('.grbox');
    const store = root.querySelector('.gtables');
    if (openId) ensureCard(openId, box);
    for (const id in cards) {
      const c = cards[id];
      const here = id === openId;
      const host = here ? box : store;
      if (c.el.parentElement !== host) host.appendChild(c.el);
      c.el.hidden = !here;
    }
  }

  function renderView() {
    renderViewNow();
    notifyTable();
    if (staleMods.size) flushStale();   // людина встала з-за столу, чий модуль тим часом оновився
  }

  function renderViewNow() {
    const v = root && root.querySelector('.gview');
    if (!v) return;
    const room = view.kind === 'room' ? view.id : null;
    placeCards(room);
    root.querySelector('.gbar').hidden = view.kind !== 'panel';
    root.querySelector('.groom').hidden = !room;
    v.hidden = !!room;
    // На телефоні за столом міні-плеєр і так нікому не потрібен — style.css ховає його за цим класом.
    document.body.classList.toggle('g-room', !!room && shown);
    // Лобі малює свої секції-панелі саме, а панелі ігор (турнір, пакети) — просто вміст,
    // тож панель під них дає сам контейнер.
    v.classList.toggle('boxed', view.kind === 'panel');
    syncWatch();
    if (room) {
      // Кімнати ще не знаємо (зайшли за посиланням): підписка принесе її сама, а як не принесе —
      // не тримаємо людину на порожньому столі.
      if (!views[room] && !pinned.has(room)) {
        pinned.add(room);
        syncWatch();
        setTimeout(() => {
          if (!views[room] && view.kind === 'room' && view.id === room) {
            pinned.delete(room);
            toast('Отакої — цього столу вже нема', 'err');
            go('#games');
          }
        }, PIN_TTL);
      }
      renderRoomHead(room);
      chromeFor = null;   // після столу панель (турнір) малюємо наново: там уже інші цифри
      return;
    }
    // Лобі малюємо щоразу (столи живі), а панелі — лише коли справді перемкнулись:
    // інакше кожна зміна в лобі перезбирала б турнір чи конструктор пакетів посеред набору.
    const key = view.kind === 'lobby' ? null : view.id;
    if (view.kind === 'lobby' || chromeFor !== key) {
      chromeFor = key;
      // Лобі перемальовується від кожної новини про столи — поле «знайти гру» не має від цього
      // губити ні фокус, ні курсор.
      const fi = v.querySelector('.gfind');
      const keep = fi && document.activeElement === fi ? fi.selectionStart : null;
      v.innerHTML = '';
      if (view.kind === 'panel') renderExtra(v, view.id.slice(2));
      else renderLobby(v);
      if (keep != null) { const n = v.querySelector('.gfind'); if (n) { n.focus(); n.setSelectionRange(keep, keep); } }
    }
  }
  let chromeFor = null;

  // ---------------------------------------------------------------------------------------------
  // Сторінка столу
  // ---------------------------------------------------------------------------------------------

  /// Кутики «на весь екран» малюємо самі: юнікодний ⛶ є не в кожному шрифті і подекуди
  /// падає в порожній квадрат.
  const FULL_ICON = (on) => '<svg class="gfico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="' + (on ? 'M6 2v4H2M10 2v4h4M6 14v-4H2M10 14v-4h4' : 'M2 6V2h4M14 6V2h-4M2 10v4h4M14 10v4h-4') + '"/></svg>';

  function renderRoomHead(id) {
    const head = root.querySelector('.grhead');
    const rv = views[id];
    const r = rv && rv.room;
    // Інші мої столи — чипами поруч: не треба вертатись у лобі, щоб перескочити на свій хід.
    const others = Object.keys(views).filter((x) => x !== id && views[x].seat != null)
      .map((x) => {
        const turn = views[x].room.status === 'playing' && turnOf(views[x]) === views[x].seat;
        return '<button class="chip grother' + (turn ? ' turn' : '') + '" data-room="' + esc(x) + '">'
          + iconOf(views[x].room.game) + esc(titleOf(views[x].room.game)) + (turn ? ' · твій хід' : '') + '</button>';
      }).join('');
    // Сидиш за столом, де ще є вільні місця й партія не йде, — можна кликати людей (усіх або когось особисто).
    const canCall = !!(rv && r && rv.seat != null && r.maxPlayers > 1 && r.status !== 'playing' && freeSeat(r) >= 0);
    head.innerHTML = '<button class="ghost grback" title="Назад у лобі — Esc">← Лобі</button>'
      + '<span class="grtitle">' + (r ? iconOf(r.game) + esc(titleOf(r.game)) : 'Стіл') + '</span>'
      + (r && r.watchers ? '<span class="gwatchers" title="Скільки дивиться">👁 ' + r.watchers + '</span>' : '')
      + (canCall ? '<button class="grcall" type="button" title="Гукнути когось за цей стіл" aria-haspopup="true">📣 Гукнути</button>' : '')
      + '<span class="grsp"></span>' + others
      + '<button class="ghost grfull" title="' + (full ? 'Повернути балачки й підрозділи' : 'На весь екран') + '"'
      + ' aria-label="' + (full ? 'Повернути балачки й підрозділи' : 'На весь екран') + '">' + FULL_ICON(full) + '</button>';
    head.querySelector('.grback').onclick = () => go('#games');
    head.querySelector('.grfull').onclick = () => { setFull(!full); renderRoomHead(id); };
    head.querySelectorAll('[data-room]').forEach((b) => b.onclick = () => go('#games/room/' + encodeURIComponent(b.dataset.room)));
    const cb = head.querySelector('.grcall');
    if (cb) cb.onclick = (e) => { e.stopPropagation(); callMenu(id, cb); };
  }

  // ---------------------------------------------------------------------------------------------
  // «📣 Гукнути»: особисто когось із тих, хто на сайті, або всіх ще раз (хаб: InviteTo, CallAgain)
  // ---------------------------------------------------------------------------------------------

  let callEl = null;
  function closeCall() { if (callEl) { callEl.remove(); callEl = null; } }
  function callMenu(roomId, anchor) {
    if (callEl) { closeCall(); return; }
    const rv = views[roomId];
    const r = rv && rv.room;
    if (!r) return;
    const seated = new Set();
    for (let i = 0; i < seatCount(r); i++) { const n = nickAt(r, i); if (n) seated.add(n.toLowerCase()); }
    const people = online().filter((n) => !seated.has(String(n).toLowerCase()) && !sameNick(n, me.nick));
    const el = document.createElement('div');
    el.className = 'gcall';
    el.innerHTML = '<div class="muted small">' + (people.length ? 'Гукнути особисто — прилетить лише цій людині:' : 'На сайті ні душі, крім тих, хто вже за столом.') + '</div>'
      + (people.length ? '<div class="gcall-list">' + people.map((n) => '<button type="button" class="gcall-p" data-nick="' + esc(n) + '">'
        + (window.HPeople ? window.HPeople.ava(n, 'ava sm') : '') + '<span style="--h:' + hueOf(n) + '">' + esc(n) + '</span></button>').join('') + '</div>' : '')
      + (r.status === 'lobby' ? '<button type="button" class="primary gcall-all" title="Усім на сайті — тост і рядок у балачках">📣 Гукнути всіх ще раз</button>' : '');
    document.body.appendChild(el);
    callEl = el;
    const rect = anchor.getBoundingClientRect();
    el.style.left = Math.max(8, Math.min(rect.left, window.innerWidth - el.offsetWidth - 8)) + 'px';
    el.style.top = (rect.bottom + 6) + 'px';
    el.querySelectorAll('.gcall-p').forEach((b) => b.onclick = async (e) => {
      const btn = e.currentTarget;
      const res = await busy(btn, 'гукаю…', () => call('InviteTo', roomId, b.dataset.nick));
      if (res && res.ok && btn.isConnected) { btn.disabled = true; btn.classList.add('done'); btn.insertAdjacentHTML('beforeend', ' ✓'); }
    });
    const all = el.querySelector('.gcall-all');
    if (all) all.onclick = async (e) => { const res = await busy(e.currentTarget, 'гукаю…', () => call('CallAgain', roomId)); if (res && res.ok) closeCall(); };
  }
  document.addEventListener('click', (e) => { if (callEl && !e.target.closest('.gcall, .grcall')) closeCall(); });
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && callEl) { closeCall(); e.stopPropagation(); } }, true);
  window.addEventListener('hashchange', closeCall);

  // ---------------------------------------------------------------------------------------------
  // Лобі: смужка «Сьогодні», живі столи, «часто граємо» й каталог родинами
  // ---------------------------------------------------------------------------------------------

  /// Резюме столу в лобі — окремий елемент, а НЕ копія картки: картка з модулем гри
  /// живе лише на сторінці столу.
  function roomSummaryHtml(r) {
    const rv = views[r.id];
    const seat = seatOfMe(r);
    const mine = seat != null;
    const took = takenSeats(r), all = seatCount(r);
    const nicks = [];
    for (let i = 0; i < all; i++) { const n = nickAt(r, i); if (n) nicks.push(n); }
    const free = all - took;
    // Ніки клікабельні: картка людини (web/people.js ловить data-who).
    const who = nicks.map((n) => '<span class="who-n" data-who="' + esc(n) + '" style="--h:' + hueOf(n) + '">' + esc(n) + '</span>').join(', ');
    const myTurn = mine && r.status === 'playing' && rv && turnOf(rv) === seat;
    const status = r.status === 'playing' ? (myTurn ? '<b class="turn">твій хід</b>' : 'іде партія')
      : r.status === 'finished' ? 'дограли'
        : free ? 'чекає, хто підсяде' : 'ось-ось почнуть';
    const btns = mine
      ? '<button class="primary" data-open="' + esc(r.id) + '">Відкрити</button>'
      : (free > 0 && r.status !== 'playing' ? '<button class="primary" data-sit="' + esc(r.id) + '">Сісти</button>' : '')
        + '<button data-open="' + esc(r.id) + '">Дивитись</button>';
    return '<div class="gsum' + (mine ? ' mine' : '') + '">'
      + '<div class="gs-head"><span class="gtitle">' + iconOf(r.game) + esc(titleOf(r.game)) + '</span>'
      + (r.stake ? '<span class="gmode stake">🏺' + r.stake + '</span>' : '')
      + '<span class="chip">' + (all > 1 ? took + '/' + all : 'соло') + '</span></div>'
      + '<div class="gs-who">' + (nicks.length ? who : '<span class="muted">поки ні душі</span>')
      + (free > 0 && all > 1 ? ' <span class="muted">· вільно ' + free + '</span>' : '')
      + ' · ' + status + (r.watchers ? ' <span class="muted">· 👁 ' + r.watchers + '</span>' : '') + '</div>'
      + '<div class="gs-btns">' + btns + '</div></div>';
  }

  // ---------------------------------------------------------------------------------------------
  // Каталог родинами: одна плитка на гру, скільки б режимів у неї не було (FAMILIES)
  // ---------------------------------------------------------------------------------------------

  /// Записи каталогу: { kind: 'game'|'family', ids, group, title, hint, icon, min, max, solo, g?, f?, list? }.
  function entries() {
    const out = [];
    const seen = new Set();
    for (const g of catalog.games) {
      const f = familyOf[g.id];
      if (f) {
        if (seen.has(f.id)) continue;
        seen.add(f.id);
        const list = f.games.map(([id, label]) => ({ g: byId[id], label })).filter((x) => x.g);
        if (list.length > 1) {
          out.push({
            kind: 'family', f, list, ids: list.map((x) => x.g.id), group: list[0].g.group, title: f.title, hint: f.hint,
            min: Math.min(...list.map((x) => x.g.minPlayers)), max: Math.max(...list.map((x) => x.g.maxPlayers)), solo: false,
          });
          continue;
        }
      }
      out.push({ kind: 'game', g, ids: [g.id], group: g.group, title: g.title, hint: g.hint || '', min: g.minPlayers, max: g.maxPlayers, solo: g.maxPlayers === 1 });
    }
    return out;
  }
  /// Скільки столів у цю гру (разом з усіма режимами) дограли за 30 днів — нею сортуємо й збираємо «часто граємо».
  const playsOf = (e) => e.ids.reduce((s, id) => s + ((popular && popular[id]) || 0), 0);
  const byPlays = (a, b) => playsOf(b) - playsOf(a) || a.title.localeCompare(b.title, 'uk');
  const playersOf = (e) => (e.solo ? 'соло' : (e.min === e.max ? e.max : e.min + '–' + e.max) + ' 👤');
  /// Режим родини, який пропонуємо першим: той, у який найбільше грають (або перший).
  function defaultMode(e) {
    if (e.kind !== 'family') return e.g.id;
    let best = e.ids[0], n = -1;
    for (const id of e.ids) { const k = (popular && popular[id]) || 0; if (k > n) { best = id; n = k; } }
    return best;
  }
  function badgeOf(e) {
    // Нова гра — коли нова вся родина (перший режим); новий режим у старій грі — це вже «оновлено».
    if (isNewGame(e.ids[0])) return '<span class="gnew" title="Лови нову гру на сайті — спробуй">🆕 нова гра</span>';
    const upd = e.ids.find((id) => hasNews(id));
    return upd ? '<span class="gupd" title="' + esc('Оновлення: ' + (newsOf(upd).title || titleOf(upd))) + '">оновлено</span>' : '';
  }
  function tileBtn(e) {
    if (e.solo) return '<button class="primary" data-solo="' + esc(e.g.id) + '">Грати</button>';
    return '<button data-new="' + esc(e.kind === 'family' ? 'f:' + e.f.id : e.g.id) + '">+ Стіл</button>';
  }
  function tileHtml(e) {
    const now = e.solo ? playingIn(e.g.id) : [];
    const fresh = isNewGame(e.ids[0]);
    const extra = e.ids.includes('svoya') && extraPanels.some((p) => p.id === 'svoya')
      ? '<button class="ghost gt-extra" data-go="#games/x:svoya" title="Пакети запитань: грати свої, збирати нові">📦 Пакети</button>' : '';
    return '<div class="gtile' + (fresh ? ' fresh' : '') + (now.length ? ' live' : '') + '">'
      + '<div class="gt-head">' + iconOf(e.ids[0]) + '<b>' + esc(e.title) + '</b>' + badgeOf(e) + '</div>'
      + (now.length ? '<div class="gt-now" title="' + esc(whoTitle(now)) + '"><i class="gdot"></i><span>' + esc(whoShort(now, 3))
        + ' <span class="muted">' + (now.length > 1 ? 'грають' : 'грає') + '</span></span></div>' : '')
      + '<div class="gt-hint muted small"><span>' + esc(e.hint) + '</span></div>'
      + (e.kind === 'family' ? '<div class="gt-modes muted small">' + e.list.map((x) => esc(x.label)).join(' · ') + '</div>' : '')
      + '<div class="gt-btns"><span class="gt-pl muted small">' + playersOf(e) + '</span>' + extra + tileBtn(e) + '</div></div>';
  }

  // ---------------------------------------------------------------------------------------------
  // Смужка «Сьогодні»: щоденні головоломки й турнір — те, що буває раз на день чи на вечір
  // ---------------------------------------------------------------------------------------------

  let dailyAt = 0;
  function loadDaily(force) {
    if (!me.nick || (!force && Date.now() - dailyAt < 60000)) return;
    dailyAt = Date.now();
    api('GET', '/api/games/daily').then((d) => {
      daily = d || null;
      if (shown && view.kind === 'lobby') renderView();
    }).catch(() => { dailyAt = 0; });
  }
  let popularAt = 0;
  function loadPopular() {
    if (Date.now() - popularAt < 10 * 60000) return;
    popularAt = Date.now();
    api('GET', '/api/games/popular?days=30').then((r) => {
      const map = {};
      for (const x of (r && r.games) || []) map[x.game] = x.rooms || 0;
      popular = map;
      if (shown && view.kind === 'lobby') renderView();
    }).catch(() => { /* старий сервер цього не вміє — сортуємо за назвою, «часто граємо» не показуємо */ });
  }
  function tourCard() {
    const T = window.HTournament;
    const s = T && T.state;
    if (!extraPanels.some((p) => p.id === 'tournament')) return '';
    if (!s || !s.active) {
      return '<button type="button" class="gdc gdc-link" data-go="#games/x:tournament" title="Кілька ігор поспіль, очки за місця, корона чемпіону">'
        + '<span class="gemo">👑</span><b>Турнір на вечір</b><span class="muted small">гайда збирати →</span></button>';
    }
    const stage = { gathering: 'збираємось', playing: 'іде гра ' + ((s.index || 0) + 1) + ' з ' + (s.games || []).length, between: 'перерва між іграми', done: 'дограли' }[s.stage] || '';
    return '<div class="gdc tour"><span class="gemo">👑</span><div><b>Турнір</b><span class="muted small">' + esc(stage)
      + ((s.players || []).length ? ' · ' + (s.players || []).length + ' у грі' : '') + '</span></div>'
      + '<button class="primary" data-go="#games/x:tournament">Відкрити</button></div>';
  }
  function todayHtml() {
    const list = (daily && daily.puzzles) || [];
    const cards = list.map((p) => {
      const solved = p.me && p.me.solved;
      const what = solved
        ? '✓ розгадано ' + (p.me.attempts ? 'за ' + tries(p.me.attempts) : '') + (p.me.ms ? ' · ' + secs(p.me.ms) : '')
        : 'ще не розгадано' + (p.solvedCount ? ' · ' + p.solvedCount + ' вже розгадали' : '');
      return '<div class="gdc' + (solved ? ' done' : '') + '">' + iconOf(p.game) + '<div><b>' + esc(p.title || titleOf(p.game)) + '</b>'
        + '<span class="muted small">' + esc(what) + (p.streak ? ' · 🔥 ' + p.streak : '') + '</span></div>'
        + '<button class="' + (solved ? 'ghost' : 'primary') + '" data-solo="' + esc(p.game) + '"'
        + (solved ? '' : ' title="Розгадай — і хапай щоденний глек"') + '>' + (solved ? 'Глянути' : 'Грати') + '</button></div>';
    }).join('');
    const tour = tourCard();
    if (!cards && !tour) return '';
    return '<section class="gpanel gtoday"><h3>☀ Сьогодні' + (daily && daily.no ? ' <span class="muted small">· щоденний глек №' + daily.no + '</span>' : '') + '</h3>'
      + '<div class="gtoday-row">' + cards + tour + '</div></section>';
  }

  function renderLobby(box) {
    loadDaily(false);
    loadPopular();
    const mineFirst = rooms.slice().sort((a, b) => (seatOfMe(b) != null ? 1 : 0) - (seatOfMe(a) != null ? 1 : 0));
    const want = find.trim().toLowerCase();
    const all = entries();
    const match = (e) => (filter === 'all' || e.group === filter)
      && (!want || (e.title + ' ' + e.hint + ' ' + (e.list || []).map((x) => x.g.title + ' ' + x.label).join(' ')).toLowerCase().includes(want));
    const list = all.filter(match).sort(byPlays);

    // Соло-ігри в каталозі стоять останніми, тож хто в них зараз, видно й тут, нагорі.
    // Натиск відкриває свою таку саму: побачив, що Оля крутить коло, — сів і собі.
    const soloGames = [...new Set(soloNow.map((p) => p.game))];
    const soloLine = soloGames.length
      ? '<div class="gsolo"><span class="muted small">🏺 Хто тусить у соло:</span>' + soloGames.map((id) => {
        const who = playingIn(id);
        return '<button class="chip gsolo-g" data-solo="' + esc(id) + '" title="' + esc(whoTitle(who) + ' — ану й ти') + '">'
          + iconOf(id) + '<b>' + esc(titleOf(id)) + '</b><span class="gw">· ' + esc(whoShort(who, 3)) + '</span></button>';
      }).join('') + '</div>'
      : '';

    // Живі столи. Хто лише дивиться (ще не назвався), хаба не має — і столів не бачить: так і кажемо.
    const live = !me.nick
      ? '<div class="gempty glek">Хто за якими столами — видно, щойно назвешся. <button class="primary" data-nick>Назватись</button></div>'
      : mineFirst.length
        ? '<div class="gsums">' + mineFirst.map(roomSummaryHtml).join('') + '</div>'
        : '<div class="gempty glek">Столів нема. Гайда, постав перший із каталогу нижче — друзям прилетить заклик.</div>';

    // «Часто граємо» — швидкий запуск того, у що компанія грає найбільше, без гортання каталогу.
    const favs = popular ? all.filter((e) => playsOf(e) >= 2).sort(byPlays).slice(0, 6) : [];
    const favRow = favs.length && filter === 'all' && !want
      ? '<div class="gfavs"><span class="muted small">⭐ Часто граємо:</span>' + favs.map((e) => {
        const act = e.solo ? 'data-solo="' + esc(e.g.id) + '"' : 'data-new="' + esc(e.kind === 'family' ? 'f:' + e.f.id : e.g.id) + '"';
        return '<button class="gfav" ' + act + ' title="' + esc(playsOf(e) + ' ' + (playsOf(e) % 10 >= 2 && playsOf(e) % 10 <= 4 && (playsOf(e) % 100 < 12 || playsOf(e) % 100 > 14) ? 'партії' : 'партій') + ' за місяць') + '">'
          + iconOf(e.ids[0]) + '<b>' + esc(e.title) + '</b><span class="muted small">' + (e.solo ? 'грати' : '+ стіл') + '</span></button>';
      }).join('') + '</div>'
      : '';

    // Каталог: на «Усі» без пошуку — групами з заголовками (у групі спершу те, у що грають), інакше — просто знайдене.
    const grouped = filter === 'all' && !want;
    const tiles = !list.length ? ''
      : grouped
        ? GROUPS.filter((g) => g.id !== 'all').map((g) => {
          const part = list.filter((e) => e.group === g.id);
          return part.length ? '<h4 class="ggroup">' + g.icon + ' ' + esc(g.title) + ' <span class="muted small">· ' + part.length + '</span></h4>'
            + '<div class="gtiles">' + part.map(tileHtml).join('') + '</div>' : '';
        }).join('')
        : '<div class="gtiles">' + list.map(tileHtml).join('') + '</div>';

    const links = [['#stats/games', '🏆 Таблиці ігор'], ['#stats/time', '⏱ Хто скільки грав'], ['#lavka', '🛍 Лавка Дядька Глека']]
      .concat(extraPanels.filter((p) => p.id !== 'svoya').map((p) => ['#games/x:' + p.id, (p.icon || '📋') + ' ' + p.title]))
      .concat(extraPanels.some((p) => p.id === 'svoya') ? [['#games/x:svoya', '🎯 Пакети Своєї гри']] : []);

    box.innerHTML = todayHtml()
      + '<section class="gpanel"><h3>🔥 Живі столи'
      + (rooms.length ? ' <span class="muted small">· ' + rooms.length + '</span>' : '') + '</h3>'
      + live + soloLine + '</section>'
      + '<section class="gpanel"><h3>Каталог <span class="muted small">· ' + all.length + ' ігор</span></h3>'
      + favRow
      + '<div class="gfilters">'
      + GROUPS.filter((g) => g.id === 'all' || all.some((x) => x.group === g.id))
        .map((g) => '<button class="chip gchip' + (filter === g.id ? ' on' : '') + '" data-filter="' + g.id + '">'
          + (g.icon ? g.icon + ' ' : '') + esc(g.title) + '</button>').join('')
      + '<input class="gfind" type="search" placeholder="знайти гру" value="' + esc(find) + '" autocomplete="off">'
      + '</div>'
      + (tiles || '<div class="gempty">Овва, нічого схожого не знайшлось. Спробуй інакше або зніми фільтр.</div>')
      + '<div class="glinks">' + links.map(([h, l]) => '<a href="' + h + '">' + esc(l) + '</a>').join('<span>·</span>') + '</div>'
      + '</section>';

    box.querySelectorAll('[data-filter]').forEach((b) => b.onclick = () => {
      filter = b.dataset.filter;
      try { localStorage.setItem('gamesFilter', filter); } catch { /* приватне вікно */ }
      renderView();
    });
    box.querySelector('.gfind').oninput = (e) => { find = e.target.value; renderView(); };
    box.querySelectorAll('[data-new]').forEach((b) => b.onclick = () => {
      if (!me.nick) { askNick(); return; }
      const k = b.dataset.new;
      if (k.startsWith('f:')) {
        const e = all.find((x) => x.kind === 'family' && x.f.id === k.slice(2));
        if (e) openCreate(gameOf(defaultMode(e)), e);
      } else openCreate(gameOf(k), null);
    });
    box.querySelectorAll('[data-solo]').forEach((b) => b.onclick = (e) => {
      if (!me.nick) { askNick(); return; }
      busy(e.currentTarget, 'мить…', () => openRoom('OpenSolo', b.dataset.solo, null));
    });
    box.querySelectorAll('[data-open]').forEach((b) => b.onclick = () => go('#games/room/' + encodeURIComponent(b.dataset.open)));
    box.querySelectorAll('[data-sit]').forEach((b) => b.onclick = async (e) => {
      if (await joinRoom(b.dataset.sit, e.currentTarget)) go('#games/room/' + encodeURIComponent(b.dataset.sit));
    });
    box.querySelectorAll('[data-go]').forEach((b) => b.onclick = () => go(b.dataset.go));
    box.querySelectorAll('[data-nick]').forEach((b) => b.onclick = () => askNick());
  }

  function renderExtra(box, id) {
    const p = extraPanels.find((x) => x.id === id);
    box.innerHTML = '';
    if (!p) { box.innerHTML = '<div class="gempty">Отакої — панель зникла.</div>'; return; }
    const host = document.createElement('div');
    host.className = 'gxpanel';
    box.appendChild(host);
    const c = panelCtx();
    try { p.mount(host, c); if (p.update) p.update(host, c); }
    catch (e) { console.warn('[games] панель ' + id, e); host.innerHTML = '<div class="gempty">Ой-йой, панель зламалась.</div>'; }
  }

  const panelCtx = () => ({ me, esc, toast, busy, api, call, ui, css: cssVar, catalog });

  // =============================================================================================
  // Попап створення столу
  // =============================================================================================

  /// Ставки є лише там, де є що ділити: рівно двоє і партія рейтингова (ARCHITECTURE §4.4).
  /// Те саме правило на сервері (Rooms.ReadStake), тому змійка й дуель теж зі ставками.
  const stakeable = (g) => !!g && g.maxPlayers === 2 && !!g.rated;

  /// Пари [значення, підпис] опції — з каталогу вони приходять масивами, але терпимо й {value, label}.
  const optPairs = (o) => (o.values || []).map((v) => Array.isArray(v) ? v : [v.value, v.label || v.value]);

  /// Опція в попапі: звичайна — випадайка, multi — чипи, де можна ввімкнути кілька.
  function optHtml(o) {
    if (o.multi) {
      const on = String(o.default || '').split(',');
      return '<div class="gopt"><span class="muted small">' + esc(o.label) + '</span>'
        + '<div class="gpicks" data-key="' + esc(o.key) + '" data-any="' + esc(o.default || '') + '">'
        + optPairs(o).map(([val, lab]) => '<button type="button" class="gpick' + (on.includes(val) ? ' on' : '')
          + '" data-val="' + esc(val) + '">' + esc(lab) + '</button>').join('')
        + '</div></div>';
    }
    return '<label class="gopt"><span class="muted small">' + esc(o.label) + '</span>'
      + '<select data-key="' + esc(o.key) + '">'
      + optPairs(o).map(([val, lab]) => '<option value="' + esc(val) + '"' + (val === o.default ? ' selected' : '') + '>' + esc(lab) + '</option>').join('')
      + '</select></label>';
  }

  /// Клік по чипу multi-опції. Типове значення — «усе»: воно гасить решту, а будь-який інший чип гасить
  /// його; вимкнули останній — «усе» повертається само (сервер робить із порожнім вибором те саме).
  function togglePick(box, chip) {
    const chips = [...box.querySelectorAll('.gpick')];
    const any = chips.find((x) => x.dataset.val === box.dataset.any);
    if (chip === any) { chips.forEach((x) => x.classList.toggle('on', x === any)); return; }
    chip.classList.toggle('on');
    if (any) any.classList.toggle('on', !chips.some((x) => x !== any && x.classList.contains('on')));
  }

  /// Попап «поставити стіл». fam — родина (кілька режимів однієї гри): тоді згори чипи режимів, і опції,
  /// підказка та ставки під ними міняються разом із режимом. Сервер про родини не знає — ставимо стіл вибраної гри.
  function openCreate(g, fam) {
    if (!g) return;
    const wrap = document.createElement('div');
    wrap.className = 'modal gmodal';
    const modes = fam && fam.list && fam.list.length > 1 ? fam.list : null;
    const pl = (x) => (x.minPlayers === x.maxPlayers ? x.maxPlayers : x.minPlayers + '–' + x.maxPlayers) + ' 👤';
    wrap.innerHTML = '<div class="card">'
      + '<h3>' + iconOf(g.id) + esc(modes ? fam.title : g.title) + '</h3>'
      + (modes ? '<div class="gopt"><span class="muted small">Режим</span><div class="gmodes">' + modes.map((x) =>
        '<button type="button" class="gpick' + (x.g.id === g.id ? ' on' : '') + '" data-mode="' + esc(x.g.id) + '">'
        + esc(x.label) + ' <span class="muted small">' + pl(x.g) + '</span></button>').join('') + '</div></div>' : '')
      + '<div class="gvar"></div>'
      + '<div class="grow"><button class="primary" data-go>Поставити стіл</button><button class="ghost" data-close>Скасувати</button></div>'
      + '</div>';
    document.body.appendChild(wrap);
    const close = () => wrap.remove();
    wrap.addEventListener('click', (e) => { if (e.target === wrap) close(); });
    wrap.querySelector('[data-close]').onclick = close;
    // Частина, що залежить від режиму: підказка, опції, ставка.
    const paintVar = () => {
      const opts = g.options || [];
      const stakes = stakeable(g) ? (catalog.stakes || []) : [];
      const box = wrap.querySelector('.gvar');
      box.innerHTML = (g.hint ? '<div class="muted small">' + esc(g.hint) + '</div>' : '')
        + opts.map(optHtml).join('')
        + (stakes.length > 1 ? '<div class="gopt"><span class="muted small">Ставка з кожного</span><div class="gstakes">'
          + stakes.map((s, i) => '<button type="button" class="gstake' + (i === 0 ? ' on' : '') + '" data-stake="' + s + '">🏺' + s + '</button>').join('')
          + '</div></div>' : '');
      box.querySelectorAll('.gstake').forEach((b) => b.onclick = () => {
        box.querySelectorAll('.gstake').forEach((x) => x.classList.toggle('on', x === b));
      });
      box.querySelectorAll('.gpicks').forEach((p) => p.querySelectorAll('.gpick').forEach((b) => b.onclick = () => togglePick(p, b)));
    };
    paintVar();
    wrap.querySelectorAll('[data-mode]').forEach((b) => b.onclick = () => {
      const next = gameOf(b.dataset.mode);
      if (!next) return;
      g = next;
      wrap.querySelectorAll('[data-mode]').forEach((x) => x.classList.toggle('on', x === b));
      paintVar();
    });
    wrap.querySelector('[data-go]').onclick = (e) => busy(e.currentTarget, 'ставлю…', async () => {
      const box = wrap.querySelector('.gvar');
      const payload = {};
      box.querySelectorAll('select[data-key]').forEach((s) => payload[s.dataset.key] = s.value);
      box.querySelectorAll('.gpicks').forEach((p) => payload[p.dataset.key] =
        [...p.querySelectorAll('.gpick.on')].map((x) => x.dataset.val).join(','));
      const st = box.querySelector('.gstake.on');
      if (st) payload.stake = +st.dataset.stake;
      const r = await openRoom('CreateRoom', g.id, payload);
      if (r.ok) { close(); if (r.roomId) go('#games/room/' + encodeURIComponent(r.roomId)); }
    });
  }
  // =============================================================================================
  // Картка кімнати
  // =============================================================================================

  function ensureCard(id, host) {
    if (cards[id]) return cards[id];
    const el = document.createElement('div');
    el.className = 'gtable';
    el.dataset.room = id;
    el.innerHTML = '<div class="gseats"></div><div class="gbody"></div><div class="gstatus"></div><div class="gbtns"></div>';
    const card = {
      id, el,
      head: el.querySelector('.gseats'),
      body: el.querySelector('.gbody'),
      statusEl: el.querySelector('.gstatus'),
      btns: el.querySelector('.gbtns'),
      mod: null, mounted: false, ctx: null,
      sig: '',
    };
    cards[id] = card;
    // «👥 N» на телефоні розгортає всіх гравців; клас живе на .gseats, тож переживає перемальовування чіпів.
    card.head.addEventListener('click', (e) => { if (e.target.closest('[data-many]')) card.head.classList.toggle('open'); });
    if (host) host.appendChild(el);   // у DOM до першого mount: модуль, що міряє ширину, має що міряти
    // Вид прийде з першою подією 'room' після WatchRoom; поки що вистачить того, що є в лобі.
    if (!views[id]) {
      const r = rooms.find((x) => x.id === id);
      if (r) views[id] = { room: r, seat: seatOfMe(r), view: undefined };
    }
    if (views[id]) refreshCard(id);
    else card.body.innerHTML = '<div class="gwait"><span class="spin"></span> мить…</div>';
    return card;
  }

  function dropCard(id) {
    const c = cards[id];
    if (!c) return;
    if (c.mounted && c.mod && c.mod.unmount) { try { c.mod.unmount(c.body, c.ctx); } catch (e) { console.warn('[games] unmount', e); } }
    c.el.remove();
    delete cards[id];
  }

  function seatNameOf(rv, i) {
    const fromServer = rv.room.seatNames && rv.room.seatNames[i];
    if (fromServer) return fromServer;
    const m = cards[rv.room.id] && cards[rv.room.id].mod;
    const sn = m && m.seatNames;
    if (typeof sn === 'function') return sn(i, rv.room);
    if (Array.isArray(sn) && sn[i]) return sn[i];
    return i === 0 ? 'перший' : i === 1 ? 'другий' : 'гравець ' + (i + 1);
  }
  function seatClassOf(rv, i) {
    const m = cards[rv.room.id] && cards[rv.room.id].mod;
    const sc = m && m.seatClass;
    if (Array.isArray(sc) && sc[i]) return sc[i];
    return ['x', 'o', 'c', 'd'][i % 4];
  }
  const turnOf = (rv) => (rv.view && typeof rv.view.turn === 'number' ? rv.view.turn : null);

  function makeCtx(card, rv) {
    const room = rv.room;
    const ctx = card.ctx || {};
    ctx.room = room;
    ctx.seat = rv.seat;
    ctx.view = rv.view;
    if (ctx.frame === undefined) ctx.frame = null;   // останній 'frame'; кадри не скидають вид і навпаки
    ctx.me = me;
    ctx.playing = room.status === 'playing';
    ctx.mine = rv.seat != null;
    ctx.myTurn = ctx.mine && ctx.playing && turnOf(rv) === rv.seat;
    ctx.esc = esc;
    ctx.toast = toast;
    ctx.ui = ui;
    ctx.css = cssVar;
    ctx.seatName = (i) => seatNameOf(rv, i);
    ctx.nickOf = (i) => nickAt(room, i);
    ctx.act = (action, payload) => call('Act', room.id, action, payload === undefined ? null : payload);
    ctx.input = (action, payload) => send('Input', room.id, action, payload === undefined ? null : payload);
    card.ctx = ctx;
    return ctx;
  }

  function headHtml(rv) {
    const room = rv.room;
    const g = gameOf(room.game);
    const solo = room.maxPlayers === 1;
    const chips = [];
    if (!solo) {
      // На великих столах (мафія, піктіонарі — до 12) чіпи вільних місць займали на телефоні три рядки над грою:
      // від трьох вільних показуємо їх одним «вільно ×N».
      let free = 0;
      for (let i = 0; i < seatCount(room); i++) if (!nickAt(room, i)) free++;
      const fold = free > 2;
      // Від п'яти гравців на телефоні чіпи ніків стояли 4–5 рядками над грою. Там лишаємо свій чіп, чий хід
      // і «👥 N» — дотик розгортає всіх (core.css, .gseats.many). На широкому екрані видно всіх, як і було.
      const taken = seatCount(room) - free;
      for (let i = 0; i < seatCount(room); i++) {
        const nick = nickAt(room, i);
        if (fold && !nick) continue;
        const turn = room.status === 'playing' && turnOf(rv) === i;
        chips.push('<span class="gseat ' + seatClassOf(rv, i) + (nick ? '' : ' free') + (turn ? ' turn' : '')
          + (i === rv.seat ? ' me' : '') + '"><i>' + esc(seatNameOf(rv, i)) + '</i>' + esc(nick || 'вільно') + '</span>');
      }
      if (fold) chips.push('<span class="gseat free gfreeall">вільно ×' + free + '</span>');
      if (taken > 4) chips.push('<button type="button" class="gseat gmany" data-many title="Показати всіх за столом">👥 '
        + taken + '</button>');
    }
    // Варіант, обраний при створенні (зникаючі хрестики, розмір поля) — підписуємо, якщо він не типовий.
    const modes = [];
    for (const o of (g && g.options) || []) {
      const v = room.options && room.options[o.key];
      if (v == null || v === o.default) continue;
      // multi-опція: «ukraine,science» → «Україна · Наука, природа й тіло» (кома вже буває в самих підписах)
      const label = (o.multi ? v.split(',') : [v])
        .map((x) => { const pair = optPairs(o).find((p) => p[0] === x); return pair ? pair[1] : x; }).join(' · ');
      modes.push('<span class="gmode">' + esc(label) + '</span>');
    }
    return '<span class="gtitle">' + iconOf(room.game) + esc(titleOf(room.game)) + '</span>'
      + modes.join('')
      + (room.stake ? '<span class="gmode stake">🏺' + room.stake + '</span>' : '')
      + chips.join('')
      + (room.watchers ? '<span class="gwatchers" title="Скільки дивиться">👁 ' + room.watchers + '</span>' : '');
  }

  function defaultStatus(rv) {
    const r = rv.room;
    const solo = r.maxPlayers === 1;
    if (r.status === 'finished') {
      const res = r.result;
      if (!res) return 'Партію зіграно';
      // соло: «перемога над собою» звучить дивно, тому беремо те, що написала гра
      if (solo) return res.text || (res.draw ? 'Цього разу не вийшло' : 'Є! Готово');
      if (res.draw || !(res.winners || []).length) return 'Нічия';
      // «Є!» — лише переможцеві: суперник і глядач бачать просто, чия перемога.
      return (rv.seat != null && res.winners.includes(rv.seat) ? 'Є! ' : '')
        + 'Перемога: ' + res.winners.map((i) => nickAt(r, i) || seatNameOf(rv, i)).join(', ');
    }
    if (r.status === 'lobby') return solo ? '' : freeSeat(r) >= 0 ? 'Чекаємо, хто підсяде' : 'Чекаємо на старт';
    const t = turnOf(rv);
    if (t != null) return t === rv.seat ? 'Твій хід' : 'Ходить ' + (nickAt(r, t) || seatNameOf(rv, t));
    return rv.seat == null ? 'Дивишся збоку' : '';
  }

  function btnsHtml(rv) {
    const r = rv.room;
    const solo = r.maxPlayers === 1;
    const out = [];
    // Дограний стіл із вільним місцем сервер віддає новому гравцеві (Rooms.Join, гілка reopen),
    // тож статус тут не питаємо — інакше стіл висів би в лобі до прибиральника, і сісти нікому.
    const canSit = !solo && rv.seat == null && freeSeat(r) >= 0 && r.status !== 'playing';
    if (canSit) out.push('<button class="primary" data-do="JoinRoom">Сісти</button>');
    // «Ану ще раз» пропонуємо лише коли є з ким: інакше кнопка є, а сервер відповідає «Замало гравців»
    if (!solo && rv.seat != null && r.status === 'finished' && takenSeats(r) >= r.minPlayers)
      out.push('<button class="primary" data-do="Rematch">Ану ще раз</button>');
    // щоденна головоломка одна на день — «Ану ще раз» там не пропонуємо
    if (solo && r.status === 'finished' && !(gameOf(r.game) || {}).daily) out.push('<button class="primary" data-do="Rematch">Ану ще раз</button>');
    if (rv.seat != null && r.status === 'lobby' && sameNick(r.host, me.nick) && (gameOf(r.game) || {}).start === 'byHost'
      && takenSeats(r) >= r.minPlayers)
      out.push('<button class="primary" data-do="StartRoom">Почати</button>');
    if (rv.seat != null) out.push('<button class="ghost" data-do="LeaveRoom">' + (solo ? 'Закрити' : 'Встати') + '</button>');
    // сісти нема куди (або сидиш за іншим столом) — хоч скажемо, чому кнопок нема
    else if (!solo && !canSit) out.push('<span class="muted small">Дивлюсь збоку</span>');
    return out.join('');
  }

  /// Шапка/статус/кнопки — окремо від .gbody: тіло чіпає лише модуль.
  function refreshCard(id) {
    const card = cards[id], rv = views[id];
    if (!card || !rv) return;
    const ctx = makeCtx(card, rv);

    const mod = modules[rv.room.game] || null;
    if (mod && card.mod !== mod) card.mod = mod;

    const sig = JSON.stringify([rv.room.status, rv.room.seats, rv.room.seatNames, rv.room.watchers, rv.room.stake,
      rv.room.options, rv.room.result, rv.seat, turnOf(rv), rv.room.host, me.nick, !!card.mod]);
    if (sig !== card.sig) {
      card.sig = sig;
      card.head.innerHTML = headHtml(rv);
      card.head.classList.toggle('many', !!card.head.querySelector('[data-many]'));
      card.btns.innerHTML = btnsHtml(rv);
      card.btns.querySelectorAll('[data-do]').forEach((b) => b.onclick = async (e) => {
        // «Сісти» йде через joinRoom: він сам спитає, чи вставати з попереднього столу, і сам крутить кнопку.
        if (b.dataset.do === 'JoinRoom') { await joinRoom(id, e.currentTarget); return; }
        await busy(e.currentTarget, '…', async () => {
          const r = await call(b.dataset.do, id);
          if (r.ok && b.dataset.do === 'LeaveRoom') {
            // приватну соло-кімнату сервер із лобі не прибере — прибираємо картку самі
            if (views[id] && views[id].loose) { dropCard(id); delete views[id]; pinned.delete(id); }
            if (view.kind === 'room' && view.id === id) go('#games'); else renderView();
          }
        });
      });
      card.el.classList.toggle('mine', rv.seat != null);
    }

    if (!card.mod) {
      card.body.innerHTML = failed.has(rv.room.game)
        ? '<div class="gwait err">Ой-йой, модуль гри не завантажився</div>'
        : '<div class="gwait"><span class="spin"></span> мить…</div>';
    } else if (rv.view !== undefined) {
      if (!card.mounted) {
        card.body.innerHTML = '';
        card.mounted = true;
        try { if (card.mod.mount) card.mod.mount(card.body, ctx); }
        catch (e) { console.warn('[games] mount ' + rv.room.game, e); }
      }
      try { if (card.mod.update) card.mod.update(card.body, ctx); }
      catch (e) { console.warn('[games] update ' + rv.room.game, e); }
      maybeNews(rv);
    }

    paintStatus(card, rv);
  }

  /// Рядок статусу — тільки textContent, тому його не шкода перерахувати і на кожен кадр:
  /// у реалтайм-іграх фаза й відлік живуть у кадрах, а не у видах.
  function paintStatus(card, rv) {
    let text = '';
    if (card.mod && card.mod.status) { try { text = card.mod.status(card.ctx) || ''; } catch { text = ''; } }
    if (!text) text = defaultStatus(rv);
    if (card.statusEl.textContent !== text) card.statusEl.textContent = text;
    const cls = 'gstatus' + (rv.room.status === 'finished' ? ' done' : '') + (card.ctx && card.ctx.myTurn ? ' my' : '');
    if (card.statusEl.className !== cls) card.statusEl.className = cls;
  }

  const refreshAll = () => { for (const id in cards) refreshCard(id); };

  // =============================================================================================
  // Дрібні підписи для лобі (профіль, таблиці й «⏱ Час» переїхали в web/people.js)
  // =============================================================================================

  const secs = (ms) => (ms == null ? '' : (ms / 1000).toFixed(ms < 10000 ? 1 : 0) + ' с');

  /// «за 1 спробу», «за 3 спроби», «за 6 спроб».
  function tries(n) {
    const t = n % 100, o = n % 10;
    if (t > 10 && t < 20) return n + ' спроб';
    if (o === 1) return n + ' спробу';
    if (o >= 2 && o <= 4) return n + ' спроби';
    return n + ' спроб';
  }

  // =============================================================================================
  // Клавіатура
  // =============================================================================================

  /// Активна кімната — та, що зараз на екрані. Решта карток лежать змонтовані на складі
  /// під hidden, і клавіші до них іти не мають.
  function activeCard() {
    const list = (root ? [...root.querySelectorAll('.grbox > .gtable')] : [])
      .map((el) => cards[el.dataset.room]).filter((c) => c && c.ctx && !c.el.hidden);
    return list.find((c) => c.ctx.mine && c.ctx.playing) || list[0] || null;
  }
  document.addEventListener('keydown', (e) => {
    if (!shown || e.metaKey || e.ctrlKey || e.altKey) return;
    // target може бути й самим document (подія, яку хтось згенерував сам) — у нього нема matches()
    const t = e.target;
    if (t && ((t.matches && t.matches('input, textarea, select')) || t.isContentEditable)) return;
    const c = activeCard();
    if (!c || !c.mod || !c.mod.onKey) return;
    let handled = false;
    try { handled = c.mod.onKey(e, c.ctx); } catch (err) { console.warn('[games] onKey', err); }
    if (handled) e.preventDefault();
  });
  // Esc — назад зі столу. Слухач другий, тож гра, яка Esc уже з'їла (скрабл, шашки),
  // позначила подію preventDefault, і ми в неї не лізимо.
  document.addEventListener('keydown', (e) => {
    if (!shown || e.defaultPrevented || e.key !== 'Escape' || (view.kind !== 'room' && view.kind !== 'panel')) return;
    const t = e.target;
    if (t && ((t.matches && t.matches('input, textarea, select')) || t.isContentEditable)) return;
    if (document.querySelector('.modal:not([hidden])')) return;   // спершу попап, потім стіл
    e.preventDefault();
    if (full) { setFull(false); renderRoomHead(view.id); } else go('#games');
  });

  // =============================================================================================
  // Столи в балачках (PLAN.md §7.4): кнопка в рядку Журналу, заклик тостом, відповідь на /столи
  // =============================================================================================

  /// Стіл так, як він виглядає в рядку балачок: іконка з назвою, склад, стан людською мовою і те, що з
  /// ним можна зробити зараз. null — такого столу вже нема: дограли й прибрали, або він приватний.
  /// Джерело — та сама подія 'rooms', що малює лобі, тож кнопка не бреше про вчорашній склад.
  function roomLink(id) {
    const r = rooms.find((x) => x.id === id) || (views[id] && views[id].room);
    if (!r) return null;
    const all = seatCount(r), took = takenSeats(r), free = all - took;
    const mine = seatOfMe(r) != null;
    const canSit = !mine && r.status === 'lobby' && free > 0;
    return {
      id: r.id,
      game: r.game,
      icon: iconOf(r.game),                                    // готовий HTML: 🎲, поки модуль гри не прийшов
      title: iconOf(r.game) + esc(titleOf(r.game)),            // іконка з назвою — для рядка, який гри не називає
      who: (all > 1 ? took + '/' + all : 'соло') + ' · '
        + (r.status === 'playing' ? 'іде партія' : r.status === 'finished' ? 'дограли'
          : free ? 'чекає, хто підсяде' : 'ось-ось почнуть'),
      canSit,
      mine,
      label: mine ? 'До столу' : canSit ? 'Сісти' : 'Дивитись',
    };
  }

  /// Де людина сидить (мультиплеєрний стіл) — для картки й профілю: { id, game, state, canSit } або null.
  function roomOf(nick) {
    const r = rooms.find((x) => x.maxPlayers > 1 && Array.from({ length: seatCount(x) }, (_, i) => nickAt(x, i)).some((n) => sameNick(n, nick)));
    if (!r) return null;
    const link = roomLink(r.id);
    return { id: r.id, game: r.game, state: link ? link.who.replace(/^[^·]+·\s*/, '') : '', canSit: !!(link && link.canSit) };
  }
  /// У яку соло-гру людина зараз грає (подія 'solo'), або null.
  const soloOf = (nick) => { const p = soloNow.find((x) => sameNick(x.nick, nick)); return p ? p.game : null; };
  /// Мій стіл, що чекає гравців і має вільне місце, — туди можна кликати: { id, game } або null.
  function myWaitingRoom() {
    const r = rooms.find((x) => x.maxPlayers > 1 && seatOfMe(x) != null && x.status !== 'playing' && freeSeat(x) >= 0);
    return r ? { id: r.id, game: r.game } : null;
  }

  // ---------------------------------------------------------------------------------------------
  // «Твій хід»: за якими столами чекають на мене, поки я дивлюсь деінде (інший розділ, інший стіл, схована вкладка)
  // ---------------------------------------------------------------------------------------------

  let turnSig = '';
  function checkTurns() {
    const list = [];
    for (const id in views) {
      const rv = views[id];
      if (!rv || rv.seat == null || !rv.room || rv.room.status !== 'playing' || (rv.room.maxPlayers || 0) <= 1) continue;
      if (turnOf(rv) !== rv.seat) continue;
      if (shown && view.kind === 'room' && view.id === id && !document.hidden) continue;   // якраз на нього й дивлюсь
      list.push({ id, game: rv.room.game, title: titleOf(rv.room.game) });
    }
    const sig = list.map((x) => x.id).join(',');
    if (sig === turnSig) return;
    turnSig = sig;
    if (onTurn) { try { onTurn(list); } catch (e) { console.warn('[games] onTurn', e); } }
  }
  document.addEventListener('visibilitychange', checkTurns);

  async function sitAt(id, btn) {
    const ok = await joinRoom(id, btn);
    if (ok) go('#games/room/' + encodeURIComponent(id));
    return { ok, message: '' };
  }

  const openAt = (id) => go('#games/room/' + encodeURIComponent(id));

  /// «Влад кличе в Мафію» — десять секунд і кнопка «Сісти». Мовчимо, коли кличемо самі себе, коли за
  /// тим столом уже нема куди сідати і коли тост закрив би пів партії: на весь екран або на вузькому
  /// екрані просто під час гри. Другий заклик за той самий стіл замінює перший, а не громадиться.
  /// Особистий заклик (personal: «кличе тебе») важливіший: висить 20 секунд, дзенькає і не мовчить на телефоні за столом.
  function inviteToast(inv) {
    if (!inv || !inv.roomId || sameNick(inv.by, me.nick)) return;
    const link = roomLink(inv.roomId);
    if (!link || !link.canSit) return;
    const personal = !!inv.personal;
    if (!personal && (full || (view.kind === 'room' && window.matchMedia('(max-width: 900px)').matches))) return;
    const box = document.getElementById('toasts');
    if (!box) { toast(inv.text, 'ok'); return; }
    const was = box.querySelector('.ginvite[data-room="' + CSS.escape(inv.roomId) + '"]');
    if (was) was.remove();
    if (personal) ping();
    const el = document.createElement('div');
    el.className = 'toast ok ginvite' + (personal ? ' personal' : '');
    el.dataset.room = inv.roomId;
    el.innerHTML = '<span class="gi-what">' + link.icon + '</span>'
      + '<span class="gi-text">' + esc(inv.text) + '<br><span class="muted small">' + esc(link.who) + '</span></span>'
      + '<button class="primary gi-sit">Сісти</button>'
      + '<button class="ghost gi-no" title="Не зараз" aria-label="Не зараз">✕</button>';
    el.querySelector('.gi-sit').onclick = async (e) => {
      if ((await sitAt(inv.roomId, e.currentTarget)).ok) el.remove();
    };
    el.querySelector('.gi-no').onclick = () => el.remove();
    box.appendChild(el);
    ensureIcon(link.game, () => { const w = el.querySelector('.gi-what'); if (w) w.innerHTML = iconOf(link.game); });
    setTimeout(() => el.remove(), personal ? INVITE_MS * 2 : INVITE_MS);
  }

  /// Десять секунд: досить, щоб прочитати й натиснути, і не досить, щоб набриднути.
  const INVITE_MS = 10000;

  // =============================================================================================
  // Тости гаманця й ачівок
  // =============================================================================================

  /// Ачівка заслуговує довшого тоста, ніж «трек закинуто»: 6 секунд, щоб устигли прочитати.
  function longToast(html, ms) {
    const box = document.getElementById('toasts');
    if (!box) { toast(String(html).replace(/<[^>]*>/g, ''), 'ok'); return; }
    const el = document.createElement('div');
    el.className = 'toast ok gbig';
    el.innerHTML = html;
    box.appendChild(el);
    setTimeout(() => el.remove(), ms || 6000);
  }

  // =============================================================================================
  // Публічний API
  // =============================================================================================

  const HGames = {
    ui,

    register(mod) {
      if (!mod || !mod.id) { console.warn('[games] register без id'); return; }
      modules[mod.id] = mod;
      failed.delete(mod.id);
      for (const id in cards) if (views[id] && views[id].room.game === mod.id) refreshCard(id);
      if (shown && root && root.querySelector('.gtiles')) renderView();
      if (shown && view.kind === 'room') renderRoomHead(view.id);
      notifyTable();   // модуль міг приїхати пізніше за стіл — і сказати, що розмова тут головна (talk: 'main')
    },

    registerPanel(p) {
      if (!p || !p.id || !p.mount) { console.warn('[games] registerPanel без id/mount'); return; }
      const i = extraPanels.findIndex((x) => x.id === p.id);
      if (i >= 0) extraPanels[i] = p; else extraPanels.push(p);
      renderShell();
      // Після F5 на вкладці панелі тіло малюється ще до модуля («Панель зникла.») і chromeFor уже
      // стоїть — без скидання renderView вважав би його намальованим і лишив напис назавжди.
      if (view.kind === 'panel' && view.id === 'x:' + p.id) { chromeFor = null; renderView(); }
    },

    has: (id) => !!modules[id],

    init(o) {
      o = o || {};
      if (o.esc) esc = o.esc;
      if (o.toast) toast = o.toast;
      if (o.busy) busy = o.busy;
      if (o.api) api = o.api;
      if (o.me) me = o.me;
      if (o.go) go = o.go;
      if (o.onTable) onTable = o.onTable;
      if (o.openTable) onOpenTable = o.openTable;
      if (o.onTurn) onTurn = o.onTurn;
      if (o.ping) ping = o.ping;
      if (o.online) online = o.online;
      if (o.askNick) askNick = o.askNick;
      root = o.root || (o.$ ? o.$('games') : document.getElementById('games'));
      booted = true;
      renderShell();
      // Каталог і модуль кожної гри тягнемо в show(): слухачеві, який у «Ігри» не заходить,
      // ці два десятки запитів ні до чого.
    },

    attach(c) {
      conn = c;
      watched.clear();
      focusSent = undefined;
      hereSent = undefined;
      c.on('solo', (list) => {
        soloNow = Array.isArray(list) ? list : [];
        if (shown && view.kind === 'lobby') renderView();
      });
      c.on('rooms', (list) => {
        rooms = list || [];
        // Щойно столи взагалі є — беремо назви ігор: без них балачки писали б «mafia» замість «Мафія».
        // Модулі при цьому не тягнемо: слухачеві, який у «Ігри» не заходить, вони ні до чого.
        if (rooms.length) ensureNames().catch(() => { /* напишемо id, це не привід шуміти */ });
        for (const r of rooms) {
          const rv = views[r.id];
          if (rv) { rv.room = r; rv.seat = seatOfMe(r); rv.loose = false; }
          // Відкритий стіл (зайшли за посиланням або після F5): назву й місця беремо з лобі
          // одразу, а справжній вид домалює 'room' після WatchRoom. Стіл, за яким я сиджу, — теж: після F5 на
          // «Ефірі» каркас інакше не знав би про нього, і «твій хід» мовчав би, поки не зайдеш в «Ігри».
          else if (r.id === view.id || cards[r.id] || (r.maxPlayers > 1 && seatOfMe(r) != null)) views[r.id] = { room: r, seat: seatOfMe(r), view: undefined, loose: false };
        }
        // кімнати з лобі, яких уже нема, забираємо разом із видом; приватні соло тут не рахуються
        for (const id in views) if (!views[id].loose && !rooms.some((r) => r.id === id)) { dropCard(id); delete views[id]; }
        // стіл, на сторінці якого ми стоїмо, закрився — вертаємось у лобі, а не дивимось у порожнечу
        if (shown && view.kind === 'room' && view.id && !views[view.id] && !pinned.has(view.id)) { go('#games'); return; }
        renderShell();
        renderView();
        refreshAll();
        checkTurns();
        if (window.HPeople) window.HPeople.refreshWhere();
      });
      c.on('room', (rv) => {
        if (!rv || !rv.room) return;
        const id = rv.room.id;
        const old = views[id];
        views[id] = {
          room: rv.room,
          seat: rv.seat != null ? rv.seat : seatOfMe(rv.room),
          view: rv.view,
          // приватна соло-кімната в 'rooms' не приходить — тягнемо її за собою по панелях
          loose: old ? old.loose : !rooms.some((r) => r.id === id),
        };
        pinned.delete(id);
        if (view.kind === 'room' && view.id === id && !cards[id]) renderView();
        else if (cards[id]) refreshCard(id);
        else if (view.kind === 'lobby') renderView();      // «твій хід» на резюме в лобі
        syncWatch();
        notifyTable();                                      // сів, встав, партія почалась — балачці столу це важливо
        checkTurns();                                       // «🎲 Твій хід» у заголовку вкладки й на «Іграх»
      });
      c.on('frame', (f) => {
        if (!f || !f.id) return;
        const card = cards[f.id];
        if (!card || !card.mounted) return;
        // Останній кадр кладемо в ctx: фаза й відлік реалтайм-ігор живуть саме тут,
        // і без цього status() бачив би лише застарілий вид із рідкої події 'room'.
        if (card.ctx) card.ctx.frame = f.f;
        if (card.mod && card.mod.frame) {
          try { card.mod.frame(card.body, card.ctx, f.f); } catch (e) { console.warn('[games] frame', e); }
        }
        if (views[f.id]) paintStatus(card, views[f.id]);
      });
      c.on('wallet', (w) => {
        if (!w) return;
        wallet = w.balance;
        paintWallet();
        // сервер уже присилає готовий рядок «+5 черепків: перемога — Хрестики-нолики»;
        // своє число ліпимо лише тоді, коли тексту нема, інакше виходило «+5 🏺 +5 черепків: …».
        // Прихід — «Лови +5 …» (якщо сервер сам уже не сказав «Лови»), витрата — як є.
        const line = w.text || (w.delta > 0 ? '+' : '') + w.delta;
        if (w.delta) toast('🏺 ' + (w.delta > 0 && !/^лови/i.test(line) ? 'Лови ' + line : line), w.delta > 0 ? 'ok' : '');
      });
      c.on('achievement', (a) => {
        if (!a) return;
        const title = String(a.title || a.key || '');
        longToast('<span class="gemo">' + esc(a.icon || '🏅') + '</span> ' + (/^овва/i.test(title) ? '' : 'Овва! ') + '<b>' + esc(title) + '</b>'
          + (a.reward ? ' — лови +' + a.reward + ' 🏺' : '') + (a.text ? '<br><span class="muted small">' + esc(a.text) + '</span>' : ''), 6000);
      });
      c.on('toast', (t) => { if (t && t.text) toast(t.text, t.kind || ''); });
      c.on('invite', inviteToast);
      loadWallet();          // черепки видно в шапці з будь-якого розділу, тож питаємо їх одразу
    },

    /// Сайт оновився без F5 (app.js, checkFront): changed — { 'games/runner.js': відбиток, … }. Перечитує каталог,
    /// перевантажує змінені модулі ігор; вертає шляхи, які підхопив сам, — решту app.js віддає плашці.
    refresh: refreshFront,

    /// Після реконекту підписки на сервері вже нема — просимо заново для видимих кімнат (і кажемо, що на екрані).
    reconnected() {
      watched.clear();
      focusSent = undefined;
      hereSent = undefined;
      syncWatch();
      loadWallet();
    },

    /// tail — те, що в адресі після #games/: '' (лобі), 'room/<id>', 'x:<id>' (панель)…
    show(tail) {
      const first = !shown;
      shown = true;
      if (!booted) return;
      ensureCatalog();
      if (first) chromeFor = null;   // повернувся в розділ — панелі перечитуються
      if (first) loadDaily(true);
      route(tail);
      if (first) loadWallet();
      checkTurns();
    },

    hide() {
      shown = false;
      setFull(false);
      document.body.classList.remove('g-room');
      syncWatch();
      checkTurns();
    },

    /// Каталог і модулі ігор (іконки, назви) — для «Хто скільки» й профілів: проміс, що каталог уже є.
    ready: () => ensureCatalog() || Promise.resolve(),
    iconOf,
    titleOf,
    roomOf,
    soloOf,
    myWaitingRoom,

    /// Активний стіл — той, що зараз на екрані (шар джойстика питає, чи не забрала гра напрямки собі).
    /// null — ми не за столом або модуль гри ще не приїхав.
    active() {
      const c = activeCard();
      return c && c.ctx ? { id: c.ctx.room.game, mod: c.mod, ctx: c.ctx, el: c.el } : null;
    },

    /// Стіл для рядка балачок: { id, game, icon, title, who, canSit, label } або null, якщо столу вже нема.
    roomLink,
    /// Підтягнути модуль однієї гри заради її іконки; ready() — коли вона вже справжня.
    ensureIcon,
    /// Сісти за стіл прямо з балачок і піти до нього.
    sitAt,
    /// Просто відкрити сторінку столу.
    openAt,
    /// Розгорнути балачку столу, біля якого стоїмо (вкладку «🎲 Стіл» або шторку) — кнопка «До суперечки» в мафії.
    openTable() { if (onOpenTable) onOpenTable(); },

    /// Для модулів, панелей і людей (web/people.js кличе InviteTo), яким треба смикнути хаб самим.
    call,
    send,
    get catalog() { return catalog; },
  };

  window.HGames = HGames;
})();
