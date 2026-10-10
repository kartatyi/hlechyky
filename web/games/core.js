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
  const quietW = new Map();     // приставка причини → скільки модулів просять тиші (слоти: 'slot-' на кожен оберт)
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
    { id: 'azart', title: 'Азарт', icon: '🎰' },   // лише на клієнті: розділ гри дає THEMES, серверна group не міняється
  ];

  // Родини: одна гра в кількох режимах на різну кількість людей. У каталозі — одна плитка, режим обирається у
  // вікні «поставити стіл». Сервер про родини не знає (там окремі ігри зі своїми таблицями), це лише показ.
  // Щоденні головоломки (Сапер дня, Глек-слово) сюди не йдуть: у них своя смужка «Сьогодні».
  const FAMILIES = [
    { id: 'ttt', title: 'Хрестики-нолики', games: [['ttt', 'Класика'], ['ttt3', 'Зникаючі'], ['ttt9', 'Ультимативні']],
      hint: 'Хто перший виставить три в ряд. У зникаючих у кожного на полі лише три мітки — четверта стирає першу. Ультимативні — дев’ять полів в одному: куди сходив, туди йде суперник.' },
    { id: 'c4', title: 'Чотири в ряд', games: [['c4', 'Удвох'], ['c4x', 'Компанія на 3–4']],
      hint: 'Кидаєш фішку в колонку, вона падає вниз. Виграє той, хто першим збере чотири в ряд.' },
    // Шахи: удвох на рейтинг або проти 🤖 Глека (соло — стіл відкривається одразу). Задача дня — у «Сьогодні», не тут.
    { id: 'chess', title: 'Шахи', games: [['chess', 'Удвох'], ['chess-glek', 'З Глеком 🤖']],
      hint: 'Шахи удвох на рейтинг — або сам проти Дядька Глека 🤖, легкого чи середнього, для розминки.' },
    // Реверсі: удвох на рейтинг або проти 🤖 Глека (соло — стіл відкривається одразу).
    { id: 'reversi', title: 'Реверсі', games: [['reversi', 'Удвох'], ['reversi-glek', 'З Глеком 🤖']],
      hint: 'Отелло 8×8: затисни чужі фішки між своїми — і вони перевертаються на твій колір. Удвох на рейтинг або проти Дядька Глека 🤖 — легкого, звичайного чи сильного.' },
    { id: 'checkers', title: 'Шашки', games: [['checkers', 'Удвох'], ['zirka', 'Китайські на 2–3']],
      hint: 'Класичні шашки удвох — або китайські на зірці для двох-трьох: стрибай через фішки й переведи своїх у протилежний куток.' },
    { id: 'bricks', title: 'Цеглини', games: [['bricks', 'Гуртом 2–4'], ['bricks-duel', 'Дуель на рейтинг']],
      hint: 'Складай ряди з фігурок — кожен знесений ряд летить сміттям під стіну суперника. Чия стіна вистоїть, той і переміг.' },
    { id: 'duel', title: 'Дуель', games: [['duel', 'Двоє'], ['shootout', 'Перестрілка на 3–4'], ['duelcup', 'Турнір на 3–8']],
      hint: '«Готуйсь… цільсь…» — і на слово ВОГОНЬ тисни першим. Поспішив — куля в небо.' },
    { id: 'snake', title: 'Змійка', games: [['snake', 'Дуель'], ['snake-party', 'Гуртом'], ['snake-coop', 'Одна на всіх']],
      hint: 'Класична змійка: дуель двох, гуртом до чотирьох або одна змійка на всіх, де кожен крутить свої стрілки.' },
    { id: 'tron', title: 'Мотоцикли', games: [['tron', 'Удвох'], ['tron-party', 'Гуртом 2–4']],
      hint: 'За тобою тягнеться стіна, яка не зникає. Хто врізався — програв. Стрілки або WASD.' },
    // Юрма: одна забава «серед натовпу селян сховались друзі» в семи місцях — у кожного місця свої правила й хитрощі.
    { id: 'crowd', title: 'Юрма', games: [['crowd', '🎪 Ярмарок'], ['skate', '⛸ Ковзанка'], ['dance', '💃 Вечорниці'],
      ['tavern', '🍺 Корчма'], ['potato', '🔥 Гарячий горщик'], ['kupala', '🌙 Купальська ніч'], ['freeze', '🧍 Замри!']],
      hint: 'Повно селян — і десь серед них твої друзі. Ніхто не знає, хто живий: вдавай селянина й вистежуй інших. Сім місць — сім забав.' },
    // Бігуни: той самий забіг «хто останній на ногах» — по кризі від лавини або в небі лелекою. Забіг дня — окремо, у «Сьогодні».
    { id: 'dino', title: 'Стрибозаври', games: [['dino', '🧊 Крига'], ['storks', '🪽 Небо (лелеки)']],
      hint: 'Одна траса для всіх: динозавром по кризі від лавини або лелекою над селом. Хто останній на ногах чи в небі — бере раунд.' },
  ];
  const familyOf = {};
  for (const f of FAMILIES) for (const [id] of f.games) familyOf[id] = f;

  // Теми: кожен розділ каталогу ділиться на підрозділи (рішення 09.10). Порядок тут — порядок на екрані.
  // У ids — id гри, а якщо гра в родині (FAMILIES), то id родини. Розділ, де тут стоїть гра, головніший за серверну
  // group: так рулетка з покером переїхали в «🎰 Азарт», а сервер про це не знає. Ігор, яких ще нема (слоти, лелька),
  // тут не боїмося — порожня тема просто не показується. Гра, якої в THEMES нема (нова), не зникає: стає в «✨ Інше»
  // в кінці свого серверного розділу, а в консоль летить нагадування розробнику — впиши її в тему.
  const THEMES = {
    board: [
      { id: 'chess', title: 'Шахи й шашки', icon: '♟', ids: ['chess', 'checkers', 'reversi'] },
      { id: 'cards', title: 'Карти й доміно', icon: '🃏', ids: ['durak', 'domino'] },
      { id: 'field', title: 'На полі', icon: '⭕', ids: ['ttt', 'c4', 'battleship', 'mines'] },
      { id: 'words', title: 'Слова', icon: '🔤', ids: ['scrabble'] },
    ],
    live: [
      { id: 'shoot', title: 'Стрілялки', icon: '🎯', ids: ['duel', 'tyr', 'tanks', 'bomber', 'glekomet'] },
      { id: 'race', title: 'Перегони й м’яч', icon: '🏎', ids: ['rally', 'hockey', 'pong', 'typerace'] },
      { id: 'arcade', title: 'Аркадна класика', icon: '🕹', ids: ['snake', 'tron', 'bricks', 'dino', 'curve', 'territory'] },
      { id: 'stand', title: 'Хто вистоїть', icon: '🧊', ids: ['icefloe', 'thinice', 'brid', 'bakhne'] },
      { id: 'grab', title: 'Хапай і збирай', icon: '🧺', ids: ['grushi', 'hostyntsi', 'skyrta', 'sklei'] },
      { id: 'crowd', title: 'Юрма', icon: '🎭', ids: ['crowd'] },
      { id: 'coop', title: 'Разом', icon: '🤝', ids: ['vohnyk'] },
    ],
    party: [
      { id: 'draw', title: 'Слова й малюнки', icon: '✏', ids: ['pictionary', 'telephone', 'hangman', 'wordle-race', 'pozyvni', 'dotepy'] },
      { id: 'bluff', title: 'Хитрість і блеф', icon: '🕵', ids: ['mafia', 'spy', 'bluff', 'dice'] },
      { id: 'quiz', title: 'Вікторини', icon: '🧠', ids: ['svoya', 'melody', 'skilky', 'geo', 'geese'] },
      { id: 'party', title: 'Вечірка', icon: '🎲', ids: ['vechirka'] },
    ],
    solo: [
      { id: 'clicker', title: 'Гончарне коло', icon: '🏺', ids: ['clicker'] },
      { id: 'train', title: 'Тренування', icon: '🏃', ids: ['bricks-sprint', 'typerace-solo', 'geo-solo'] },
      // Щоденні є й у смужці «☀ Сьогодні», але в каталозі теж стоять — тут вони разом, а не врозсип по Соло.
      { id: 'daily', title: 'Щоденні', icon: '☀', ids: ['wordle', 'mines-daily', 'chess-daily', 'bricks-daily', 'dino-daily',
        'tyr-daily', 'geese-daily', 'geo-daily', 'skilky-daily'] },
    ],
    azart: [
      { id: 'slots', title: 'Слоти', icon: '🍒', ids: ['slot-glek', 'slot-cascade', 'slot-hold', 'slot-cluster'] },
      { id: 'roulette', title: 'Рулетка', icon: '🎡', ids: ['roulette', 'roulette-solo'] },
      { id: 'bets', title: 'Ставки', icon: '🎲', ids: [] },   // не гра: плитку «Ставки на події» ставить панель web/bets.js (tile)
      { id: 'quick', title: 'Швидкі', icon: '📈', ids: ['lelka'] },
      { id: 'cards', title: 'Карти', icon: '🃏', ids: ['poker', 'blackjack', 'blackjack-solo'] },
    ],
  };
  const OTHER_THEME = { id: 'other', title: 'Інше', icon: '✨' };
  const themeOf = {};   // id гри чи родини → { group, theme }
  for (const [group, list] of Object.entries(THEMES)) for (const t of list) for (const id of t.ids) themeOf[id] = { group, theme: t.id };
  const themeWarned = new Set();
  /// Де гра в каталозі: розділ і тема. key — id родини чи гри; srvGroup — серверна group (на випадок «✨ Інше»).
  function placeOf(key, id, srvGroup) {
    const p = themeOf[key] || themeOf[id] || (familyOf[id] && themeOf[familyOf[id].id]);
    if (p) return p;
    if (!themeWarned.has(key)) {
      themeWarned.add(key);
      console.warn('[ігри] «' + key + '» нема в THEMES (core.js) — стоїть у «✨ Інше» розділу ' + srvGroup + '. Впиши її в тему.');
    }
    return { group: srvGroup, theme: OTHER_THEME.id };
  }
  /// Теми розділу з їхніми плитками, порожні відкинуто: [{ t, items }]. items — у тому ж порядку, що й part.
  function themesIn(group, part) {
    return (THEMES[group] || []).concat([OTHER_THEME])
      .map((t) => ({ t, items: part.filter((e) => e.theme === t.id) }))
      .filter((x) => x.items.length);
  }

  /// «🆕 нова гра» — 14 днів від дати, яку модуль каже полем added, і лише тим, хто в неї ще не грав.
  /// «оновлено» — 7 днів від news.v і лише тим, хто вже грав: новенькому все одно все нове.
  /// Нова гра мусить казати added: без нього нема «🆕», а її «Нова гра: …» вилазить як «оновлено».
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
  let anthem = null;                                              // app.js: playAnthem(a, opt) — гімн переможця
  let stopAnthem = () => {};                                      // app.js: замовкнути гімн
  const anthemsPlayed = new Set();                                // 'стіл:раунд', що вже звучали в цій вкладці
  let anthemAt = null;                                            // гімн столу, що звучить: { id, round, a, here, started }
  let fx = null;                                                  // app.js: playFx(host, id, opt) — святкування на картці
  const anthemsWaiting = new Set();                               // 'стіл:раунд' гімнів, що ще заявляються між вкладками
  const anthemClaims = new Map();                                 // 'стіл:раунд' → обіцянка заявки гімну: за нею йде й прокльон
  let curseAt = null;                                             // прокльон, що звучить: { id, round, c, here, started }
  let curseNext = null;                                           // прокльон у черзі за гімном: { k, c, timer }
  const fxOn = {};                                                // стіл → { k, stop, timer }: святкування, що йде
  let online = () => [];                                          // app.js: хто зараз на сайті
  let askNick = () => {};                                         // app.js: картка «Хто прийшов?»
  let filter = localStorage.getItem('gamesFilter') || 'all';
  let theme = '';                                                 // тема в обраному розділі; '' — «Усе в розділі»
  try { theme = localStorage.getItem('gamesTheme') || ''; } catch { /* приватне вікно */ }
  let find = '';
  try { ['gamesPanel', 'gamesTimePeriod', 'gamesLbPeriod'].forEach((k) => localStorage.removeItem(k)); } catch { /* переїхало в «Хто скільки» */ }

  const sameNick = (a, b) => String(a || '').toLowerCase() === String(b || '').toLowerCase();
  /// Колір ніка — той самий, що в балачках (web/people.js вантажиться після нас, але малюємо ми вже після всіх).
  const hueOf = (n) => (window.HPeople ? window.HPeople.hue(n) : 0);
  /// ctx.css — CSS-змінна кольору чи розміру. getComputedStyle на кожен виклик коштував реалтайм-іграм сотні викликів
  /// за секунду (палітра на кожен кадр), тож пам'ятаємо непорожнє. Скидаємо, коли стилі могли змінитись: розмір вікна
  /// (@media перевизначає змінні), нова таблиця стилів гри чи її підміна після деплою.
  const cssCache = new Map();
  const cssVar = (name, fallback) => {
    let v = cssCache.get(name);
    if (v === undefined) {
      v = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
      if (v) cssCache.set(name, v);
    }
    return v || fallback;
  };
  window.addEventListener('resize', () => cssCache.clear());
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
  /// Бот гри на місці без людини («🤖 Глек»): сервер кладе його в seats[i].bot, поки йде чи дограна партія.
  /// Старий сервер його не шле — тоді місце, як і було, «вільно».
  function botAt(room, i) {
    const s = room && room.seats && room.seats[i];
    return s && typeof s === 'object' && !s.nick && s.bot ? s.bot : null;
  }
  const seatCount = (room) => (room.seats ? room.seats.length : room.maxPlayers || 0);
  const takenSeats = (room) => { let n = 0; for (let i = 0; i < seatCount(room); i++) if (nickAt(room, i)) n++; return n; };
  /// «✋ Готовий»: сервер шле room.ready — місця готових людей (Room.Summary), лише в лобі й після партії.
  const readyAt = (room, i) => !!(room && room.ready && room.ready.indexOf(i) >= 0);
  const freeSeat = (room) => { for (let i = 0; i < seatCount(room); i++) if (!nickAt(room, i)) return i; return -1; };
  /// Стіл у лобі стартує з руки господаря: гра «byHost»; гра «одразу», яку «⚙ Налаштування» вернули в лобі; і повний
  /// стіл гри «коли всі сіли» — так буває лише після «⚙ Налаштувань» (Rooms.Reconfigure), підсісти вже нікому.
  const hostStarts = (room) => {
    const s = (gameOf(room.game) || {}).start;
    return s === 'byHost' || s === 'immediate' || freeSeat(room) < 0;
  };
  function seatOfMe(room) {
    for (let i = 0; i < seatCount(room); i++) if (sameNick(nickAt(room, i), me.nick)) return i;
    return null;
  }
  /// Інший стіл, за яким ми вже сидимо, або null. Сервер тримає нас щонайбільше за одним
  /// мультиплеєрним столом (Rooms.Join, Say.Seated), соло не рахується — тож він завжди один.
  const seatedAt = (exceptId) => rooms.find((r) => r.id !== exceptId && r.maxPlayers > 1 && seatOfMe(r) !== null) || null;

  const gameOf = (id) => byId[id] || null;
  const titleOf = (id) => (byId[id] && byId[id].title) || id;
  /// Модуль гри, а поки він не приїхав (лінивий вантаж, п. 241), — те, що він сказав про себе минулого разу (gamesMeta).
  const infoOf = (id) => modules[id] || metaById[id] || null;
  const iconOf = (id) => { const m = infoOf(id); return (m && m.icon) || '<span class="gemo">' + (NO_ICON[id] || '🎲') + '</span>'; };
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
    el.style.setProperty('--gar', String(w / h));   // core.css стелить полотно за висотою (g-land, ⛶)
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
    // Поворот — на дотик, а не на click: той приходить лише після відпускання пальця, +50–120 мс на телефоні,
    // і в змійці чи мотоциклах цього вистачало, щоб врізатись. click лишається для Enter/пробілу з клавіатури.
    const fire = (e) => {
      // Лише стрілки: гра може підкласти в хрестовину свою кнопку (💥), і вона слала поворот NaN.
      const b = e.target.closest('button[data-dir]');
      const d = b ? +b.dataset.dir : NaN;
      if (Number.isFinite(d) && el._onDir) el._onDir(d);
    };
    el.addEventListener('pointerdown', (e) => { e.preventDefault(); fire(e); });
    el.addEventListener('click', (e) => { if (e.detail === 0) fire(e); });
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
        if (ri === 2) row = '<button type="button" class="gkey wide" data-k="Enter" aria-label="Enter" title="Enter">↵</button>' + row + '<button type="button" class="gkey wide" data-k="Backspace">⌫</button>';
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

  /// Мить кадру для руху. У колбеку rAF — мітка кадру від браузера, а не performance.now(): колбек нерідко
  /// починається на 2–7 мс пізніше (перед ним обробився кадр сервера чи щось інше), і рух, порахований на «коли дійшла
  /// черга», смикався б саме на стільки. Поза rAF (обробник кадру, клавіша) — звичайний performance.now().
  /// frameTime(ts) — коли мітку кадру гра вже має сама.
  let rafTs = 0, inRaf = 0;
  if (window.requestAnimationFrame && !window.requestAnimationFrame.__hframe) {
    const raf = window.requestAnimationFrame.bind(window);
    const wrapped = (cb) => raf((ts) => {
      rafTs = ts;
      inRaf++;
      try { return cb(ts); } finally { inRaf--; }
    });
    wrapped.__hframe = true;
    window.requestAnimationFrame = wrapped;
  }
  function frameTime(ts) {
    const now = performance.now();
    const t = Number.isFinite(ts) ? ts : inRaf ? rafTs : now;
    return t > 0 && t <= now ? t : now;
  }

  /// Годинник сервера для реалтайм-ігор: де «зараз» у тиках сервера, з запасом на джитер мережі.
  /// Тик t сервер рахує о base + t·tickMs нашого годинника; base — найменше (прихід − t·tickMs) з повільним
  /// дрейфом угору. Кадр, що прийшов пізно (черга, Wi-Fi, два в одному повідомленні), годинник не штовхає —
  /// тож нерівний прихід не стає нерівним рухом. Запас (delay, у тиках) — 1 тик + 95-й перцентиль спізнень
  /// за ~3 с: на тихій мережі 1,25 тика, як і було, на гикавій — до 3,5; росте швидко, спадає повільно й плавно.
  function Clock(tickMs) {
    const T = tickMs || 40, LATE = [], N = 75, MIN = 1.25, MAX = 3.5;
    // base повзе вгору неперервно (rate мс на мс), а не стрибком на приході кадру — інакше після затику картинка сіпалась назад
    let base = null, baseAt = 0, rate = 0.01, lastT = 0, delay = MIN, delayAt = 0, want = MIN, run = 0, runAt = 0;
    // Пауза сервера (див. hold): held — тик, на якому стоїть, holds — скільки разів він повторився, hb — коли тик held
    // приходить «зараз» найкоротшим шляхом; floor — нижче цього моменту картинку після паузи не відкочуємо.
    let held = null, holds = 0, hb = 0, floor = -Infinity;
    function sync(o) { base = o; rate = 0.01; LATE.length = 0; want = MIN; run = 0; }
    const cur = (now) => base + (now - baseAt) * rate;
    return {
      /// Кадр тика t прийшов о now.
      in(t, now) {
        // Сервер рушив після паузи: годинник — від останнього повтору, а не від тика до паузи. Інакше перший кадр
        // здавався б спізнілим на всю паузу, і пів секунди (поки годинник не переставився) рух ішов би сходинками.
        if (held != null && holds >= 2 && t > held && base != null && hb - held * T > cur(now)) {
          base = hb - held * T;
          baseAt = now;
          rate = 0.01;
          run = 0;
          floor = held;
        }
        held = null;
        holds = 0;
        hb = now;
        const o = now - t * T;
        const b = base == null ? 0 : cur(now);
        if (base == null || t < lastT - 2 || o < b - 300) sync(o);
        else {
          // Спізнюються на чверть секунди й більше підряд, та ще й рівним кроком (не пачкою після затику) —
          // сервер стояв з тим самим t (пауза) чи мережа стала інша: годинник ставимо наново, а не доганяємо хвилину.
          if (o - b > 250) { if (!run++) runAt = now; } else run = 0;
          if (run >= 8 && now - runAt >= 250) sync(o);
          else {
            base = Math.min(b, o);
            LATE.push(o - base);
            if (LATE.length > N) LATE.shift();
            // навіть найшвидші кадри за ~1 с спізнюються — мережа стала повільнішою: годинник доганяє швидше
            let lo = Infinity;
            for (let i = Math.max(0, LATE.length - 25); i < LATE.length; i++) lo = Math.min(lo, LATE[i]);
            rate = LATE.length >= 25 && lo > 20 ? 0.08 : 0.01;
            if (LATE.length >= 10) {
              const s = LATE.slice().sort((x, y) => x - y);
              const p95 = s[Math.min(s.length - 1, Math.floor(s.length * 0.95))];
              want = Math.max(MIN, Math.min(MAX, 1 + (p95 + 4) / T));
            }
          }
        }
        baseAt = now;
        lastT = t;
      },
      /// Кадр того самого тика t прийшов знову, вже наступним тиком: сервер стоїть (відлік перед раундом, пауза між
      /// раундами). Сам годинник не чіпаємо — картинка спокійно стоїть на останньому кадрі; лише рахуємо, коли тик t
      /// приходить «зараз» (найраніший серед повторів, прокручений на їхні тики вперед), — це знадобиться, щойно сервер рушить.
      hold(t, now) {
        if (base == null) return;
        if (held !== t) { held = t; holds = 0; }
        hb = Math.min(hb + Math.max(1, Math.round((now - hb) / T)) * T, now);
        holds++;
      },
      reset() { base = null; rate = 0.01; LATE.length = 0; want = MIN; run = 0; delay = MIN; delayAt = 0; held = null; holds = 0; floor = -Infinity; },
      get ready() { return base != null; },
      /// Коли кадр тика t приходить найкоротшим шляхом (без черги й гикавок мережі) — на нашому годиннику. Від цієї
      /// миті, а не від справжнього приходу, й ведуть передбачення: кадр, що спізнився, його не штовхає.
      when(t, now) {
        if (base == null) return null;
        return held != null && t >= held ? hb + (t - held) * T : cur(now) + t * T;   // сервер стоїть — тик held саме «зараз»
      },
      /// Запас зараз, у тиках (для заміру й підказок).
      get delay() { return delay; },
      /// Момент, який малюємо, у тиках сервера (дробовий). До першого кадру — +∞.
      at(now) {
        if (base == null) return 1e9;
        const dt = delayAt ? Math.min(100, Math.max(0, now - delayAt)) : 0;
        delayAt = now;
        // +2 тика/с і −0,3 тика/с: картинка на мить сповільнюється на ~8 % чи прискорюється на ~1 %, а не стрибає
        delay = want > delay ? Math.min(want, delay + dt * 0.002) : Math.max(want, delay - dt * 0.0003);
        const rt = (now - cur(now)) / T - delay;
        if (rt < floor) return floor;
        floor = -Infinity;
        return rt;
      },
    };
  }

  /// Інтерполятор для реалтайм-ігор: тримає стрічку кадрів і каже, між якими двома ми зараз — за годинником
  /// сервера (див. Clock), а не за часом приходу, тож нерівна мережа не смикає рух. tickMs — тик гри (40 за
  /// замовчуванням); t кадру — номер тика (без t рахуємо кадри підряд).
  function Interp(tickMs) {
    const clk = Clock(tickMs), fr = [], T = tickMs || 40;
    return {
      clock: clk,
      push(f) {
        const last = fr[fr.length - 1], now = performance.now();
        const k = f && Number.isFinite(f.t) ? f.t : (last ? last.k + 1 : 0);
        if (last && k < last.k - 2) { fr.length = 0; clk.reset(); }
        else if (last && k <= last.k) {
          if (k !== last.k) return;   // запізнілий — його момент уже пройдено
          last.f = f;
          // той самий t через тик — сервер стоїть (див. Clock.hold); вид, що прийшов посеред тика, — ні
          if (now - last.at >= T * 0.75) { clk.hold(k, now); last.at = now; }
          return;
        }
        clk.in(k, now);
        fr.push({ k, f, at: now });
        if (fr.length > 40) fr.shift();
      },
      reset() { fr.length = 0; clk.reset(); },
      /// { a: старіший кадр, b: новіший, t: 0..1 } на мить now (без неї — мить кадру, див. frameTime).
      at(now) {
        const n = fr.length;
        if (!n) return null;
        if (n === 1) return { a: fr[0].f, b: fr[0].f, t: 1 };
        const rt = clk.at(Number.isFinite(now) ? now : frameTime());
        if (rt >= fr[n - 1].k) return { a: fr[n - 2].f, b: fr[n - 1].f, t: 1 };
        if (rt <= fr[0].k) return { a: fr[0].f, b: fr[1].f, t: 0 };
        let j = n - 2;
        while (j > 0 && fr[j].k > rt) j--;
        const a = fr[j], b = fr[j + 1];
        return { a: a.f, b: b.f, t: (rt - a.k) / (b.k - a.k) };
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
    // Прохід №3, п. 243: без rAF. Дугу веде CSS-перехід (браузер сам, без JS щокадру), а цифру — таймер раз на секунду.
    // Схована картка (display:none) перехід губить — тоді на найближчому тику, коли її знову видно, заводимо наново.
    const st = { until: Date.parse(untilIso) || Date.now(), total: totalMs || 1000, t: 0, shown: false };
    const leftMs = () => Math.max(0, st.until - Date.now());
    function run() {
      const left = leftMs();
      const k = Math.max(0, Math.min(1, left / st.total));
      fg.style.transition = 'none';
      fg.style.strokeDashoffset = LEN * (1 - k);
      st.shown = el.offsetParent !== null;
      if (left > 0 && st.shown) {
        void fg.getBoundingClientRect();   // зафіксувати старт, інакше перехід стрибне одразу в кінець
        fg.style.transition = 'stroke-dashoffset ' + left + 'ms linear';
        fg.style.strokeDashoffset = LEN;
      }
    }
    function tick() {
      st.t = 0;
      if (!el.isConnected) return;
      const left = leftMs();
      const s = String(Math.ceil(left / 1000));
      if (num.textContent !== s) num.textContent = s;
      if (!st.shown && el.offsetParent !== null) run();
      else if (st.shown && el.offsetParent === null) st.shown = false;
      // На нулі дуга вже порожня: далі — тиша, поки фаза чекає сервера; новий час принесе set().
      // Час сплив, поки картка ховалась, — перехід так і не пішов; спорожнюємо дугу самі, щоб не застигла на півдорозі.
      if (left > 0) st.t = setTimeout(tick, (left % 1000) || 1000);
      else if (!st.shown) { fg.style.transition = 'none'; fg.style.strokeDashoffset = LEN; }
    }
    function start() { clearTimeout(st.t); run(); tick(); }
    start();
    const handle = {
      el,
      set(u, total) {
        const until = Date.parse(u) || Date.now(), tot = total || st.total;
        if (until === st.until && tot === st.total && (st.t || leftMs() === 0)) return;   // той самий хід — не смикаємо дугу
        st.until = until; st.total = tot; start();
      },
      stop() { clearTimeout(st.t); st.t = 0; if (el._arc === handle) el._arc = null; },
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
    setHtml(el, html);   // свій рядок пам'ятаємо властивістю, а не атрибутом: рука карт у data-sig роздувала DOM
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

  // ---------- підгонка під екран телефона (прохід 30.09) ----------
  /// Скільки екрана справді є для столу: зверху липка шапка сайту, знизу вкладки, міні-плеєр і згорнута шторка
  /// «💬 Стіл». Каркас тримає це в змінних :root (--gtop-h, --gdock-h, --gfit-h) і кличе підписників ui.onFit,
  /// коли щось змінилось: поворот, resize, ⛶, аркада, шторка, маршрут. Опис API — D:/or-wt/_mobile/fix-core.md.
  let fitLast = '', fitRaf = 0, fitProbe = null, fitBooted = false;
  const fitHosts = new Set();
  /// Сховані картки («на складі», display: none у предка) колбеків не отримують — лише позначку; колбек прийде,
  /// щойно картку знову видно (наступний applyFit чи повторний onFit із update гри).
  const fitStale = new Set();
  const fitHidden = (host) => !host.getClientRects().length;
  function safeBottom() {
    if (!fitProbe) {
      fitProbe = document.createElement('div');
      fitProbe.style.cssText = 'position:fixed;left:0;bottom:0;width:0;height:env(safe-area-inset-bottom,0px);visibility:hidden;pointer-events:none';
      document.body.appendChild(fitProbe);
    }
    return fitProbe.offsetHeight || 0;
  }
  function boxOf(el) {
    if (!el) return null;
    const cs = getComputedStyle(el);
    if (cs.display === 'none' || cs.visibility === 'hidden') return null;
    const r = el.getBoundingClientRect();
    return r.height > 0 ? r : null;
  }
  function fit() {
    const w = window.innerWidth, h = window.innerHeight;
    const he = document.querySelector('header'), hd = boxOf(he);
    const top = hd && getComputedStyle(he).position !== 'static' ? Math.max(0, Math.round(hd.bottom)) : 0;
    // Знизу: вкладки, міні-плеєр і згорнута шторка — що з них видно, те й займає низ.
    let low = h;
    for (const el of document.querySelectorAll('nav.mtabs, header .mini, .tchat.drawer:not(.open)')) {
      const r = boxOf(el);
      if (r && r.top > h / 2) low = Math.min(low, r.top);   // усе це fixed знизу; на ПК міні-плеєр — у шапці, угорі
    }
    const dock = low < h ? Math.round(h - low) : safeBottom();
    const b = document.body.classList;
    return { w, h, top, dock, imm: b.contains('g-imm'), land: b.contains('g-land'), coarse: coarse() };
  }
  /// Занурений режим на телефоні: ⛶ — будь-яка гра, або сама аркада, коли телефон лежить. style.css/core.css
  /// тоді ховають шапку, вкладки, міні-плеєр і шторку; g-land — ще й поле ліворуч, керування праворуч.
  function syncImm() {
    const b = document.body.classList;
    const land = window.innerWidth > window.innerHeight;
    const imm = !!(shown && view.kind === 'room' && coarse()
      && (full || (b.contains('g-arcade') && land && window.innerHeight <= 500)));
    if (b.contains('g-imm') !== imm) {
      b.toggle('g-imm', imm);
      if (imm) window.scrollTo(0, 0);   // шапки вже нема — стіл стає під верх екрана
    }
    if (b.contains('g-land') !== (imm && land)) b.toggle('g-land', imm && land);
  }
  function applyFit() {
    fitRaf = 0;
    syncImm();
    const f = fit();
    const key = [f.w, f.h, f.top, f.dock, f.imm, f.land].join();
    const changed = key !== fitLast;
    if (!changed && !fitStale.size) return;
    if (changed) {
      fitLast = key;
      const rs = document.documentElement.style;
      rs.setProperty('--gtop-h', f.top + 'px');
      rs.setProperty('--gdock-h', f.dock + 'px');
      rs.setProperty('--gfit-h', Math.max(0, f.h - f.top - f.dock) + 'px');
    }
    // місце змінилось — усім видимим; ні — лише тим, хто пропустив зміну схованим і тепер знову на екрані
    for (const host of changed ? fitHosts : fitStale) {
      if (!host.isConnected) { fitHosts.delete(host); fitStale.delete(host); continue; }
      if (fitHidden(host)) { fitStale.add(host); continue; }
      fitStale.delete(host);
      try { if (host._hgFit) host._hgFit(f); } catch (e) { console.error(e); }
    }
    if (changed) document.dispatchEvent(new CustomEvent('hgames:fit', { detail: f }));
  }
  function refit() {
    if (!fitBooted) fitBoot();
    if (!fitRaf) fitRaf = requestAnimationFrame(applyFit);
  }
  function fitBoot() {
    fitBooted = true;
    window.addEventListener('resize', refit);
    window.addEventListener('orientationchange', refit);
    if (window.visualViewport) window.visualViewport.addEventListener('resize', refit);
    // миша ↔ дотик (планшет із клавіатурою, емулятор) — розміри ті самі, а режим інший
    const mq = window.matchMedia('(pointer: coarse)');
    if (mq.addEventListener) mq.addEventListener('change', refit);
    const ro = window.ResizeObserver ? new ResizeObserver(refit) : null;
    const watch = () => { if (ro) document.querySelectorAll('header, nav.mtabs, .tchat').forEach((el) => ro.observe(el)); };
    watch();
    // класи body (g-room, g-arcade, gfull, маршрут, kbd) і шторка, що з'являється в body пізніше
    new MutationObserver((ms) => { if (ms.some((m) => m.type === 'childList')) watch(); refit(); })
      .observe(document.body, { attributes: true, attributeFilter: ['class'], childList: true });
  }
  /// ui.onFit(host, fn): fn(fit) на кожну зміну місця. Ідемпотентно (з mount і кожного update); відписка — сама,
  /// коли host вийшов із документа, або onFit(host, null).
  function onFit(host, fn) {
    if (!host) return;
    const had = fitHosts.has(host);
    host._hgFit = fn || null;
    if (!fn) { fitHosts.delete(host); fitStale.delete(host); return; }
    fitHosts.add(host);
    if (!fitBooted) refit();
    if (!had || fitStale.has(host)) requestAnimationFrame(() => {
      if (!host.isConnected || !host._hgFit) return;
      if (fitHidden(host)) { fitStale.add(host); return; }
      fitStale.delete(host);
      host._hgFit(fit());
    });
  }

  const ui = { grid, canvas, dpad, keyboardUa, lerp, frameTime, Clock, Interp, timerArc, hand, css: cssVar, coarse, human, html: setHtml, fit, onFit };

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

  const linked = () => !!conn && conn.state === 'Connected';
  const wait = (ms) => new Promise((done) => setTimeout(done, ms));

  /// Зв'язок рветься на ~2 с, поки сервер перезапускається (деплой): хід за цей час не лаємо, а чекаємо зв'язку.
  async function connected(ms) {
    const until = Date.now() + ms;
    while (!linked()) {
      if (Date.now() > until) return false;
      await wait(100);
    }
    return true;
  }

  /// Сервер саме заморозив столи перед перезапуском (Rooms.Freeze): хід не прийнято. Чекаємо, поки старий процес
  /// зникне (зв'язок урветься), а новий підніметься з тими самими столами, — і повторюємо хід уже йому.
  const RESTARTING = '⏳ Сайт оновлюється';
  async function afterRestart() {
    const until = Date.now() + 3000;
    while (linked() && Date.now() < until) await wait(100);
    return connected(15000);
  }

  /// Виклик хаба, що повертає RoomReply: помилку показуємо тостом, успіх — лише якщо є що сказати.
  async function call(method, ...args) {
    if (!(await connected(6000))) { toast('Халепа: зв\'язку з сервером нема', 'err'); return { ok: false, message: '' }; }
    try {
      let r = await conn.invoke(method, ...args);
      if (r && !r.ok && String(r.message || '').startsWith(RESTARTING) && (await afterRestart())) r = await conn.invoke(method, ...args);
      if (!r) return { ok: true, message: '' };
      // «Не всі готові» (✋) — не помилка: питаємо «почати все одно?» (startAsked) тут, щоб і «Ще раз» з модулів ігор
      // (ралі з трасою, Вогник, Цеглинки…) не мовчав. Відповідь — уже повторного виклику (чи ця, якщо «Чекати»).
      if (!r.ok && r.notReady && r.notReady.length && (method === 'Rematch' || method === 'StartRoom')) return await startAsked(args[0], method, r);
      if (!r.ok) { if (!(r.notReady && r.notReady.length)) errToast(r.message || 'От халепа — не вийшло'); }
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

  /// «Не готові: Оля, Петро. Почати все одно?» — після відмови StartRoom / Rematch з notReady (Rooms.Unready).
  /// [Почати] повторює той самий виклик без питань (…Anyway), [Чекати] — просто закриває вікно.
  async function startAsked(id, method, r) {
    const ok = await ask({
      title: 'Не всі готові',
      html: 'Не готові: <b>' + r.notReady.map(esc).join(', ') + '</b>.<br>Почати все одно?',
      ok: 'Почати', cancel: 'Чекати',
    });
    if (!ok) return r;
    const again = method === 'Rematch' ? 'RematchAnyway' : 'StartRoomAnyway';
    return await call(again, id);
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

  /// «?v=<відбиток>» із каталогу: такий файл сервер дозволяє кешувати назавжди (Program.cs), і повторне відкриття
  /// «Ігор» не перепитує про кожен із ~95 файлів. Старий сервер відбитків не дає — тоді без ?v=, як раніше.
  const verQ = (rel) => { const v = catalog.files && catalog.files[rel]; return v ? '?v=' + encodeURIComponent(v) : ''; };

  const loadFile = (f) => {
    if (!loadedFiles.has(f)) loadedFiles.set(f, loadScript('/games/' + f + '.js' + verQ('games/' + f + '.js')));
    return loadedFiles.get(f);
  };

  function addCss(g, f) {
    if (!g.hasCss || document.querySelector('link[data-game="' + f + '"]')) return;
    const l = document.createElement('link');
    l.rel = 'stylesheet';
    l.href = '/games/' + f + '.css' + verQ('games/' + f + '.css');
    l.dataset.game = f;
    l.onload = () => cssCache.clear();   // змінні гри з'явились лише тепер — забуваємо порожні відповіді до неї
    document.head.appendChild(l);
  }

  /// Іконку гри знає лише її модуль, а балачки згадують стіл ще до того, як людина зайшла в «Ігри».
  /// Тягнемо рівно один файл (разом із його css, інакше пізній loadModules його проґавить) і кличемо
  /// ready(), коли модуль зареєструвався. Поки він летить, на кнопці стоїть 🎲 — і це не помилка.
  function ensureIcon(gameId, ready) {
    if (modules[gameId] || (metaById[gameId] && metaById[gameId].icon)) return;   // іконка вже є з gamesMeta (п. 241)
    // Спершу каталог: без нього ми не знаємо навіть, у якому файлі ця гра живе.
    ensureNames().then(() => {
      const g = byId[gameId];
      if (!g || modules[gameId]) return;
      addCss(g, moduleOf(g));
      return loadFile(moduleOf(g));
    }).then(() => { if (modules[gameId] && ready) ready(); })
      .catch(() => { /* каталог не прочитався — лишається 🎲, і це не привід шуміти */ });
  }

  // ---------------------------------------------------------------------------------------------
  // Лінивий вантаж модулів (прохід №3, п. 241). Колись «Ігри» тягнули всі ~60 модулів одразу — з колом і його
  // частинами під 4 МБ і десятки мілісекунд розбору на телефоні, хоча людина відкриє один-два столи. Тепер:
  //  • модуль столу — коли стіл відкрили (картка сама кличе loadGame), а плитки — ще при наведенні чи дотику;
  //  • лобі малюється одразу: іконку, added і news кожен модуль сам лишає в localStorage (gamesMeta) з відбитком
  //    свого файлу, і наступного разу плитка бере їх звідти;
  //  • у тиші (requestIdleCallback) довантажуємо лише ті модулі, чиї записи застаріли (файл змінився — могли прийти
  //    «що нового») чи яких ще нема, і ті, що вішають панелі (Своя гра — «📦 Пакети»);
  //  • важкі (HEAVY — Гончарне коло тягне ще дев'ять частин) заздалегідь не тягнемо ніколи: лише стіл чи наведення.
  // Старий модуль про це нічого не знає: register() той самий, запис робить каркас.
  // ---------------------------------------------------------------------------------------------
  const META_LS = 'gamesMeta1';
  const HEAVY = new Set(['clicker']);
  const NO_ICON = { clicker: '🏺' };   // іконка-заглушка, поки важкий модуль жодного разу не приїжджав
  let meta = {};                        // файл модуля → { v: відбиток, g: { id: { icon, added, news } }, p: 1 — має панель }
  let metaById = {};
  function reindexMeta() {
    metaById = {};
    for (const f in meta) for (const id in (meta[f] && meta[f].g) || {}) metaById[id] = meta[f].g[id];
  }
  try { meta = JSON.parse(localStorage.getItem(META_LS) || '{}') || {}; } catch { meta = {}; }
  reindexMeta();
  let metaSaveT = 0;
  function saveMetaLater() {
    reindexMeta();
    if (metaSaveT) return;
    metaSaveT = setTimeout(() => {
      metaSaveT = 0;
      try { localStorage.setItem(META_LS, JSON.stringify(meta)); } catch { /* приватне вікно — наступного разу потягнемо ще раз */ }
    }, 800);
  }
  /// Який файл зараз виконується (register/registerPanel кличуть на верхньому рівні модуля) і з яким відбитком.
  function scriptNow() {
    const src = document.currentScript && document.currentScript.src;
    if (!src) return null;
    const u = new URL(src, location.href);
    const m = /^\/games\/([^/]+)\.js$/.exec(u.pathname);
    return m ? { f: m[1], v: u.searchParams.get('v') || (catalog.files && catalog.files['games/' + m[1] + '.js']) || '' } : null;
  }
  function metaFile(f, v) {
    let e = meta[f];
    if (!e || e.v !== v) e = meta[f] = { v, g: {} };
    return e;
  }
  function rememberModule(mod) {
    const s = scriptNow();
    const g = byId[mod.id];
    const f = s ? s.f : g ? moduleOf(g) : null;
    if (!f) return;
    const v = s ? s.v : (catalog.files && catalog.files['games/' + f + '.js']) || '';
    const rec = {};
    if (typeof mod.icon === 'string') rec.icon = mod.icon;
    if (mod.added) rec.added = String(mod.added);
    if (mod.talk) rec.talk = String(mod.talk);
    if (mod.news && mod.news.v) rec.news = { v: String(mod.news.v), title: String(mod.news.title || ''), items: (mod.news.items || []).map(String) };
    metaFile(f, v).g[mod.id] = rec;
    saveMetaLater();
  }
  function rememberPanel() {
    const s = scriptNow();
    if (!s) return;
    metaFile(s.f, s.v).p = 1;
    saveMetaLater();
  }
  /// Запис про файл застарів: файлу ще не бачили, він змінився, або старий сервер відбитків не дає.
  const metaStale = (f) => {
    const e = meta[f], v = catalog.files && catalog.files['games/' + f + '.js'];
    return !e || !v || e.v !== v;
  };

  /// Файл довантажився (або ні): ігри з нього, що так і не зареєструвались, — «не завантажився».
  function settleFile(f, ok) {
    let bad = false;
    for (const g of catalog.games) {
      if (moduleOf(g) !== f || modules[g.id] || failed.has(g.id)) continue;
      failed.add(g.id);
      bad = true;
      console.warn('[games] модуль ' + g.id + ' не завантажився'
        + (ok ? ' (є ' + f + '.js, але register(' + g.id + ') не викликано)' : ' (нема ' + f + '.js)'));
    }
    if (bad) refreshAll();
  }
  /// Модуль однієї гри (і всієї його родини в тому ж файлі). Двічі той самий файл не тягнемо (loadFile).
  function loadGame(id) {
    if (modules[id]) return Promise.resolve(true);
    const g = byId[id];
    if (!g || failed.has(id)) return Promise.resolve(false);   // каталогу ще нема — loadModules прийде сюди ще раз
    const f = moduleOf(g);
    for (const x of catalog.games) if (moduleOf(x) === f) addCss(x, f);
    return loadFile(f).then((ok) => { settleFile(f, ok); return !!modules[id]; });
  }

  let idleRun = null;
  /// Фонове довантаження: по кілька файлів за раз, коли браузерові нічого робити. Вертає проміс «усе, що треба, є».
  function idleLoad() {
    if (idleRun) return idleRun;
    const files = [...new Set(catalog.games.map(moduleOf))]
      .filter((f) => !HEAVY.has(f) && !loadedFiles.has(f) && (metaStale(f) || (meta[f] && meta[f].p)));
    if (!files.length) return Promise.resolve();
    const later = window.requestIdleCallback ? (fn) => requestIdleCallback(fn, { timeout: 1500 }) : (fn) => setTimeout(fn, 120);
    idleRun = new Promise((resolve) => {
      const step = () => {
        const batch = files.splice(0, 4);
        if (!batch.length) { idleRun = null; resolve(); return; }   // наступний каталог (оновлення без F5) перевірить знову
        Promise.all(batch.map((f) => {
          for (const x of catalog.games) if (moduleOf(x) === f) addCss(x, f);
          return loadFile(f).then((ok) => settleFile(f, ok));
        })).then(() => later(step));
      };
      later(step);
    });
    return idleRun;
  }

  /// Після каталогу: модулі столів, що вже відкриті (F5 за столом), — одразу; решта — у тиші.
  function loadModules() {
    for (const id in cards) if (views[id]) loadGame(views[id].room.game);
    return idleLoad();
  }

  /// Наведення чи дотик до плитки (data-pre="id id…") — модуль уже летить, поки людина тисне «Грати».
  function prefetchFrom(e) {
    const t = e.target && e.target.closest && e.target.closest('[data-pre]');
    if (!t || !catalog.games.length) return;
    for (const id of t.dataset.pre.split(' ')) if (id) loadGame(id);
  }

  /// Самі назви ігор, без двох десятків модулів: стільки треба балачкам, щоб написати «Мафія», а не
  /// «mafia». Запит той самий і кешується разом із повним ensureCatalog().
  function ensureNames() {
    if (names) return names;
    names = api('GET', '/api/games/catalog').then((c) => {
      catalog = { games: (c && c.games) || [], stakes: (c && c.stakes) || [0], files: (c && c.files) || {}, added: (c && c.added) || {} };
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
    if (l && v) { l.onload = () => cssCache.clear(); l.href = '/games/' + f + '.css?v=' + encodeURIComponent(v); }
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
  // Версія буває з літерою ('2026-09-24b' — друге оновлення за день): дата — лише перші десять знаків, інакше NaN і «оновлено» довіку.
  const daysSince = (iso) => (Date.now() - Date.parse(String(iso).slice(0, 10) + 'T12:00:00')) / 86400000;
  /// «Нова гра: …» з тією ж датою, що added, — знайомство, а не оновлення: ні «оновлено» на плитці, ні вікна.
  const newsOf = (id) => {
    const m = infoOf(id);
    if (!m || !m.news || !m.news.v || !(m.news.items || []).length) return null;
    const a = addedOf(id);
    return a && m.news.v <= a ? null : m.news;
  };
  /// Коли гра з'явилась: added у модулі, а як автор забув — день, коли сервер уперше її побачив (каталог, GameAdded.cs).
  const addedOf = (id) => {
    const m = infoOf(id);
    return (m && m.added) || (catalog.added && catalog.added[id]) || null;
  };
  const playedIt = (id) => played == null || played.has(id);
  /// Оновлення, якого людина ще не бачила, у грі, в яку вона вже грала (вікно «що нового» — без терміну давності).
  const unseenNews = (id) => { const n = newsOf(id); return !!n && newsSeen != null && newsSeen[id] !== n.v && playedIt(id); };
  /// Позначка «оновлено» на плитці — лише перший тиждень: місячної давнини «оновлено» вже нічого не каже.
  const hasNews = (id) => unseenNews(id) && !(daysSince(newsOf(id).v) > UPD_DAYS);
  /// Нова гра: модуль каже added, минуло менше двох тижнів, і я в неї ще не грав.
  const isNewGame = (id) => {
    const a = addedOf(id);
    return !!a && daysSince(a) <= NEW_DAYS && !(played && played.has(id));
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
  let walletHold = 0;
  function releaseWallet() {
    if (walletHold) { clearTimeout(walletHold); walletHold = 0; }
    paintWallet();
  }

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
    // #games/new/<id> — відкрити «+ Стіл» для гри за id, навіть якої нема в каталозі лобі (стенди: mgprobe)
    if (t.startsWith('new/')) {
      const id = decodeURIComponent(t.slice(4));
      go('#games');
      Promise.resolve(ensureCatalog()).then(() => {
        const g = gameOf(id);
        if (!g) { toast('Гри «' + id + '» нема', 'err'); return; }
        if (!me.nick) { askNick(); return; }
        openCreate(g, null);
      });
      return;
    }
    // Панель може мати й свою адресу всередині: #games/x:<id>/<що завгодно> — те, що після «/», панель отримує в ctx.sub.
    const xi = t.indexOf('/');
    const next = t.startsWith('room/') ? { kind: 'room', id: decodeURIComponent(t.slice(5)) }
      : t.startsWith('x:') ? { kind: 'panel', id: xi < 0 ? t : t.slice(0, xi), sub: xi < 0 ? '' : t.slice(xi + 1) }
        : { kind: 'lobby', id: '' };
    const same = next.kind === view.kind && next.id === view.id;
    // Та сама панель, інша адреса всередині: панель з route() перемикається сама (без перемонтування й втрати полів),
    // решта — малюється наново.
    if (same && next.kind === 'panel' && next.sub !== view.sub) {
      const p = extraPanels.find((x) => 'x:' + x.id === next.id);
      view = next;
      if (p && p.route && chromeFor === next.id) { p.route(next.sub); return; }
      chromeFor = null;
    }
    view = next;
    if (view.kind !== 'room') setFull(false);
    if (!same) chromeFor = null;          // повернулись у підрозділ — перечитуємо профіль/таблицю
    renderShell();
    renderView();
  }

  function setFull(on) {
    full = !!on && view.kind === 'room';
    document.body.classList.toggle('gfull', full);
    // На телефоні ⛶ — справжній повний екран (Android: без рядка адреси й системних панелей). На айфоні API нема —
    // там досить g-imm (сховані шапка й вкладки). Вийшли жестом «назад» — знімаємо й ⛶ (fullscreenchange нижче).
    const de = document.documentElement;
    if (full && coarse() && !document.fullscreenElement && de.requestFullscreen) {
      fsMine = true;
      try { const p = de.requestFullscreen({ navigationUI: 'hide' }); if (p && p.catch) p.catch(() => { fsMine = false; }); } catch { fsMine = false; }
    } else if (!full && fsMine) {
      fsMine = false;
      if (document.fullscreenElement && document.exitFullscreen) document.exitFullscreen().catch(() => {});
    }
    syncImm();
    refit();
    notifyTable();   // на весь екран панелі нема — балачка столу переїжджає в шторку
  }
  let fsMine = false;
  document.addEventListener('fullscreenchange', () => {
    if (document.fullscreenElement || !fsMine) return;
    fsMine = false;
    if (full) { setFull(false); if (view.kind === 'room' && view.id) renderRoomHead(view.id); }
  });

  /// Стіл, біля якого людина зараз стоїть: лише сторінка столу й лише стіл на кількох (соло говорити нема з ким).
  /// main — гра, де розмова і є гра (мафія): балачку столу там розгортаємо самі.
  function tableInfo() {
    if (!shown || view.kind !== 'room' || !view.id) return null;
    const rv = views[view.id];
    if (!rv || !rv.room || rv.loose || (rv.room.maxPlayers || 0) <= 1) return null;
    const g = rv.room.game;
    return { id: rv.room.id, game: g, title: titleOf(g), main: !!(infoOf(g) && infoOf(g).talk === 'main'), seat: rv.seat, status: rv.room.status,
      max: rv.room.maxPlayers || 0 };
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
    anthemPage();
    notifyTable();
    if (staleMods.size) flushStale();   // людина встала з-за столу, чий модуль тим часом оновився
  }

  function renderViewNow() {
    const v = root && root.querySelector('.gview');
    if (!v) return;
    const room = view.kind === 'room' ? view.id : null;
    placeCards(room);
    syncShown();
    root.querySelector('.gbar').hidden = view.kind !== 'panel';
    root.querySelector('.groom').hidden = !room;
    v.hidden = !!room;
    // На телефоні за столом міні-плеєр і так нікому не потрібен — style.css ховає його за цим класом.
    document.body.classList.toggle('g-room', !!room && shown);
    syncArcade();
    // Лобі малює свої секції-панелі саме, а панелі ігор (турнір, пакети) — просто вміст,
    // тож панель під них дає сам контейнер.
    // Панель зі своїми секціями (bare: ставки) — без спільної підкладки, як і лобі.
    const xp = view.kind === 'panel' ? extraPanels.find((p) => 'x:' + p.id === view.id) : null;
    v.classList.toggle('boxed', view.kind === 'panel' && !(xp && xp.bare));
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
  /// «Рахунок вечора за столом» (Room.Evening на сервері): хто скільки перемог узяв за всі «Ану ще раз».
  /// Показуємо з другої дограної партії (після першої це те саме, що «Перемога: …»). Старий сервер evening не шле — тоді й рядка нема.
  /// short — для лобі: лише трійка перших.
  function eveningText(r, short) {
    const ev = r && r.evening;
    // Сам за столом (проти ботів гри) — нема з ким мірятись: рядок лише з двох людей.
    if (!ev || !(ev.games >= 2) || !((ev.rows || []).length >= 2)) return '';
    const rows = ev.rows.slice(0, short ? 3 : 6);
    const pts = ev.rows.some((x) => x.points != null && x.points !== 0);
    const top = ev.rows[0].wins;
    const line = rows.map((x, i) => x.nick + ' ' + x.wins + (x.wins > 0 && x.wins === top ? '🏆' : '')
      + (pts && x.points != null && !short ? ' (' + x.points + ')' : '')).join(' · ');
    return '🌙 Вечір: ' + line + (ev.rows.length > rows.length ? ' · …' : '');
  }
  function eveningTitle(r) {
    const ev = r && r.evening;
    if (!ev) return '';
    const n = ev.games;
    const pl = n % 10 === 1 && n % 100 !== 11 ? 'партію' : n % 10 >= 2 && n % 10 <= 4 && (n % 100 < 12 || n % 100 > 14) ? 'партії' : 'партій';
    return 'За цим столом зіграли ' + n + ' ' + pl + ':\n' + ev.rows.map((x) => x.nick + ' — перемог ' + x.wins + ' з ' + x.games
      + (x.points != null ? ', очок ' + x.points : '')).join('\n');
  }

  function roomSummaryHtml(r) {
    const rv = views[r.id];
    const seat = seatOfMe(r);
    const mine = seat != null;
    const took = takenSeats(r), all = seatCount(r);
    const nicks = [];
    for (let i = 0; i < all; i++) { const n = nickAt(r, i); if (n) nicks.push(n); }
    let bots = '';
    let nb = 0;
    for (let i = 0; i < all; i++) { const b = botAt(r, i); if (b) { nb++; bots += ', <span class="gbot">' + esc(b) + '</span>'; } }
    const free = all - took - nb;
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
      + '<div class="gs-who">' + (nicks.length ? who + bots : '<span class="muted">поки ні душі</span>')
      + (free > 0 && all > 1 ? ' <span class="muted">· вільно ' + free + '</span>' : '')
      + ' · ' + status + (r.watchers ? ' <span class="muted">· 👁 ' + r.watchers + '</span>' : '') + '</div>'
      + (eveningText(r, true) ? '<div class="gs-ev muted small" title="' + esc(eveningTitle(r)) + '">' + esc(eveningText(r, true)) + '</div>' : '')
      + '<div class="gs-btns">' + btns + '</div></div>';
  }

  // ---------------------------------------------------------------------------------------------
  // Каталог родинами: одна плитка на гру, скільки б режимів у неї не було (FAMILIES)
  // ---------------------------------------------------------------------------------------------

  /// Записи каталогу: { kind: 'game'|'family', ids, group, theme, title, hint, icon, min, max, solo, g?, f?, list? }.
  function entries() {
    const out = [];
    const seen = new Set();
    for (const g of catalog.games) {
      if (g.unlisted) continue;   // стенди розробника (mgprobe) — лише посиланням #games/new/<id>
      if (g.off) continue;        // вимкнено в конфігу сайту (Games:Off) — сервер однаково не відкриє
      if (me.slots === false && SLOT_INFO[g.id]) continue;   // автомати на перерві (Slots:Enabled) — тема «🍒 Слоти» зникає
      if (me.lelka === false && g.id === 'lelka') continue;  // Лелека відпочиває (Lelka:Enabled) — плитки нема
      const f = familyOf[g.id];
      if (f) {
        if (seen.has(f.id)) continue;
        seen.add(f.id);
        const list = f.games.map(([id, label]) => ({ g: byId[id], label })).filter((x) => x.g && !x.g.off);
        if (list.length > 1) {
          const at = placeOf(f.id, list[0].g.id, list[0].g.group);
          out.push({
            kind: 'family', f, list, ids: list.map((x) => x.g.id), group: at.group, theme: at.theme, title: f.title, hint: f.hint,
            min: Math.min(...list.map((x) => x.g.minPlayers)), max: Math.max(...list.map((x) => x.g.maxPlayers)), solo: false,
          });
          continue;
        }
      }
      const at = placeOf(g.id, g.id, g.group);
      out.push({ kind: 'game', g, ids: [g.id], group: at.group, theme: at.theme, title: g.title, hint: g.hint || '', min: g.minPlayers, max: g.maxPlayers, solo: g.maxPlayers === 1 });
    }
    // Плитка-посилання на панель (registerPanel з tile: { group, theme, title?, hint?, live? }): стоїть у темі каталогу
    // поруч з іграми, шукається за назвою, але не гра — ні «часто граємо» (ids порожні), ні «N ігор», ні «+ Стіл».
    for (const p of extraPanels) {
      if (!p.tile || (p.visible && !p.visible())) continue;
      out.push({ kind: 'link', p, ids: [], group: p.tile.group, theme: p.tile.theme, title: p.tile.title || p.title, hint: p.tile.hint || '', min: 0, max: 0, solo: false });
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
    // Спільний стіл на сайт (Лелека, shared у каталозі): не «ставити», а сісти за той, що вже літає (сервер сам знайде чи поставить).
    if (e.g && e.g.shared) return '<button class="primary" data-shared="' + esc(e.g.id) + '">' + (e.g.id === 'lelka' ? 'Сісти до Лелеки' : 'Сісти за стіл') + '</button>';
    return '<button data-new="' + esc(e.kind === 'family' ? 'f:' + e.f.id : e.g.id) + '">+ Стіл</button>';
  }
  // ---------------------------------------------------------------------------------------------
  // Азарт: афіші слотів і шапка розділу (Скарбничка Глека + «Заноси тижня» з /api/slots/feed)
  // Афіша — статичний svg у web/games/slots/posters/<id>.svg (вирізаний з SlotArt[id].poster), щоб лобі не тягло арт.
  const SLOT_INFO = {
    'slot-glek': { mech: '3 барабани · 5 ліній · Ворожка ×2', vol: 1 },
    'slot-cascade': { mech: 'від 8 однакових · каскади · писанки-множники', vol: 3 },
    'slot-hold': { mech: '20 ліній · утримуй і вигравай · 4 скарби', vol: 3 },
    'slot-cluster': { mech: 'поле 7×7 · кластери від 5 · шкала папороті', vol: 2 },
  };
  const VOL = ['', 'низька', 'середня', 'висока'];
  function posterHtml(e) {
    const id = e.g.id, inf = SLOT_INFO[id] || { mech: e.hint, vol: 2 };
    const now = playingIn(id);
    return '<div class="gposter' + (isNewGame(id) ? ' fresh' : '') + '" data-pre="' + esc(id) + '">'
      + '<img class="gp-img" src="/games/slots/posters/' + esc(id) + '.svg" alt="" loading="lazy" decoding="async">'
      + '<div class="gp-txt"><div class="gp-t"><b>' + esc(e.title) + '</b>' + badgeOf(e) + '</div>'
      + '<div class="gp-m">' + esc(inf.mech) + '</div>'
      + (now.length ? '<div class="gp-now" title="' + esc(whoTitle(now)) + '"><i class="gdot"></i>' + esc(whoShort(now, 2)) + ' <span class="muted">крутить</span></div>' : '')
      + '<div class="gp-f"><span class="gp-v" title="волатильність: ' + VOL[inf.vol] + '">' + '<i>⚡</i>'.repeat(inf.vol)
      + '<i class="off">⚡</i>'.repeat(3 - inf.vol) + '<span class="muted"> ' + VOL[inf.vol] + '</span></span>'
      + '<button class="primary gp-play" data-solo="' + esc(id) + '">Грати</button></div></div></div>';
  }
  let azFeed = null, azAt = 0, azShown = null, azT = 0, azN = 0;
  const azFmt = (n) => Math.round(n || 0).toLocaleString('uk-UA').replace(/,/g, ' ');
  const AZ_TITLES = { 'slot-glek': 'Однорукий Глек', 'slot-cascade': 'Розбиті глеки', 'slot-hold': 'Козацький скарб', 'slot-cluster': 'Цвіт папороті' };
  function azWinsHtml() {
    const w = (azFeed && azFeed.wins) || [];
    if (!w.length) return '<div class="muted small gaz-empty">Цього тижня ще ніхто не заносив. Може, ти перший?</div>';
    return '<ol class="gaz-list">' + w.slice(0, 6).map((x) => '<li><b>' + esc(x.nick) + '</b><span class="muted">'
      + esc(x.jackpot ? 'Скарбничка' : (byId[x.game] && byId[x.game].title) || AZ_TITLES[x.game] || x.game) + '</span>'
      + '<span class="gaz-x">' + (x.jackpot ? '🏺' : '×' + (Math.round(x.mult * 10) / 10).toLocaleString('uk-UA', { maximumFractionDigits: 1 })) + '</span>'
      + '<span class="gaz-sum">' + azFmt(x.jackpot || x.win) + ' 🏺</span></li>').join('') + '</ol>';
  }
  function azartHeadHtml() {
    if (me.slots === false) return '';
    return '<div class="gaz-head"><div class="gaz-jp"><span class="gaz-jp-l">🏺 Скарбничка Глека</span>'
      + '<b class="gaz-jp-v">' + (azShown != null ? azFmt(azShown) : '…') + '</b>'
      + '<small class="gaz-must">' + (azFeed && azFeed.mustHit > 0 ? 'впаде до ' + azFmt(azFeed.mustHit) : '') + '</small>'
      + '<span class="muted small">з кожної ставки — дещиця сюди; будь-який оберт може її розбити</span></div>'
      + '<div class="gaz-wins"><h6>🔥 Заноси тижня</h6><div class="gaz-wl">' + azWinsHtml() + '</div></div></div>';
  }
  async function azFetch() {
    try {
      const f = await api('GET', '/api/slots/feed');
      if (!f) return;
      const fresh = !azFeed || JSON.stringify(azFeed.wins) !== JSON.stringify(f.wins);
      azFeed = f; azAt = Date.now();
      if (azShown == null || f.jackpot < azShown) azShown = f.jackpot;
      const el = root && root.querySelector('.gaz-head');
      if (!el) return;
      el.querySelector('.gaz-must').textContent = f.mustHit > 0 ? 'впаде до ' + azFmt(f.mustHit) : '';
      if (fresh) el.querySelector('.gaz-wl').innerHTML = azWinsHtml();
    } catch (e) { /* мережа — наступного разу */ }
  }
  /// Опитування лише поки шапку Азарту видно (лобі, вкладка): раз на 7 с запит, раз на секунду сума м'яко доростає.
  function azartWatch() {
    if (azT || !(root && root.querySelector('.gaz-head'))) return;
    if (Date.now() - azAt > 6000) azFetch();
    azT = setInterval(() => {
      const el = root && root.querySelector('.gaz-head');
      if (!el || !shown || view.kind !== 'lobby') { clearInterval(azT); azT = 0; return; }
      if (document.hidden) return;
      if (++azN % 7 === 0) azFetch();
      if (azFeed && azShown != null && azShown < azFeed.jackpot) {
        azShown = Math.min(azFeed.jackpot, azShown + Math.max(1, Math.ceil((azFeed.jackpot - azShown) / 4)));
      }
      const v = el.querySelector('.gaz-jp-v'), t = azShown != null ? azFmt(azShown) : '…';
      if (v.textContent !== t) v.textContent = t;
    }, 1000);
  }

  function tileHtml(e) {
    if (e.kind === 'link') {
      let live = '';
      try { live = e.p.tile.live ? e.p.tile.live() || '' : ''; } catch (err) { console.warn('[games] плитка ' + e.p.id, err); }
      const href = '#games/x:' + e.p.id;
      // Панель — не гра, «грав» про неї не знаємо: «🆕» усім два тижні від tile.added.
      const fresh = !!e.p.tile.added && daysSince(e.p.tile.added) <= NEW_DAYS;
      return '<div class="gtile gt-link' + (fresh ? ' fresh' : '') + '">'
        + '<div class="gt-head"><span class="gemo">' + (e.p.tile.icon || e.p.icon || '📋') + '</span><b>' + esc(e.title) + '</b>'
        + (fresh ? '<span class="gnew" title="Нове на сайті — глянь">🆕 нове</span>' : '') + '</div>'
        + '<div class="gt-hint muted small"><span>' + esc(e.hint) + '</span></div>'
        + '<div class="gt-btns"><span class="gt-pl muted small">' + live + '</span>'
        + '<button class="primary" data-go="' + esc(href) + '">Відкрити</button></div></div>';
    }
    const now = e.solo ? playingIn(e.g.id) : [];
    const fresh = isNewGame(e.ids[0]);
    const extra = e.ids.includes('svoya') && extraPanels.some((p) => p.id === 'svoya')
      ? '<button class="ghost gt-extra" data-go="#games/x:svoya" title="Пакети запитань: грати свої, збирати нові">📦 Пакети</button>' : '';
    return '<div class="gtile' + (fresh ? ' fresh' : '') + (now.length ? ' live' : '') + '" data-pre="' + esc(e.ids.join(' ')) + '">'
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
  // «🍳 Падельня» поруч із турніром: живі матчі з рахунком, живий турнір, найближчий збір. Дані — /api/padel/lobby
  // (раз на рендер лобі, не частіше ніж раз на 20 с) і подія головного хаба padelLive (app.js → HGames.padel).
  let padelLobby = null, padelAt = 0;
  function loadPadel() {
    if (me.padel === false || Date.now() - padelAt < 20000) return;
    padelAt = Date.now();
    api('GET', '/api/padel/lobby').then((x) => HGames.padel(x)).catch(() => { /* сервер без Падельні — лишається тиха картка */ });
  }
  function padelWhen(local) {
    const d = new Date(local);
    if (Number.isNaN(d.getTime())) return '';
    const key = (x) => x.getFullYear() + '-' + x.getMonth() + '-' + x.getDate();
    const now = new Date(), tmr = new Date(Date.now() + 864e5);
    const day = key(d) === key(now) ? 'сьогодні' : key(d) === key(tmr) ? 'завтра' : d.toLocaleDateString('uk-UA', { weekday: 'short' });
    return day.charAt(0).toUpperCase() + day.slice(1) + ' ' + String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0');
  }
  function padelCard() {
    if (me.padel === false) return '';   // Падельню вимкнено в конфігу сайту (Padel:Enabled)
    const x = padelLobby || {};
    const live = x.live || [], tours = x.tours || [], next = x.next;
    if (!live.length && !tours.length && !next) {
      return '<button type="button" class="gdc gdc-link" data-go="#padel" title="Табло з рахунком, турніри американо, збори на гру й хто кому за корт">'
        + '<span class="gemo">🍳</span><b>Падельня</b><span class="muted small">табло й турніри для падела →</span></button>';
    }
    const lines = live.slice(0, 2).map((m) => '🎾 ' + (m.teams || []).map((t) => t.join(' і ')).join(' — ') + ' · ' + (m.score || ''))
      .concat(tours.slice(0, 1).map((t) => '🏆 ' + t.title + ' · раунд ' + t.round + ' з ' + t.of))
      .concat(next ? ['📅 ' + padelWhen(next.local) + ' · ' + next.going + '/' + next.slots + (next.place ? ' · ' + next.place : '')] : []);
    return '<div class="gdc tour"><span class="gemo">🍳</span><div><b>Падельня</b>'
      + lines.map((l) => '<span class="muted small" title="' + esc(l) + '">' + esc(l) + '</span>').join('')
      + '</div><button class="primary" data-go="#padel">Відкрити</button></div>';
  }
  function todayHtml() {
    const list = ((daily && daily.puzzles) || []).filter((p) => !(byId[p.game] && byId[p.game].off));
    const cards = list.map((p) => {
      const solved = p.me && p.me.solved;
      // Щоденні «більше — краще» (Скільки? дня) міряються очками, а не спробами: старий сервер points не шле.
      const what = solved && p.me.points != null
        ? '✓ зіграно · ' + points(p.me.points)
        : solved
        ? '✓ розгадано ' + (p.me.attempts ? 'за ' + tries(p.me.attempts) : '') + (p.me.ms ? ' · ' + secs(p.me.ms) : '')
        : 'ще не розгадано' + (p.solvedCount ? ' · ' + p.solvedCount + ' вже розгадали' : '');
      return '<div class="gdc' + (solved ? ' done' : '') + '">' + iconOf(p.game) + '<div><b>' + esc(p.title || titleOf(p.game)) + '</b>'
        + '<span class="muted small">' + esc(what) + (p.streak ? ' · 🔥 ' + p.streak : '') + '</span></div>'
        + '<button class="' + (solved ? 'ghost' : 'primary') + '" data-solo="' + esc(p.game) + '"'
        + (solved ? '' : ' title="Розгадай — і хапай щоденний глек"') + '>' + (solved ? 'Глянути' : 'Грати') + '</button></div>';
    }).join('');
    const tour = tourCard() + padelCard();
    if (!cards && !tour) return '';
    return '<section class="gpanel gtoday"><h3>☀ Сьогодні' + (daily && daily.no ? ' <span class="muted small">· щоденний глек №' + daily.no + '</span>' : '') + '</h3>'
      + '<div class="gtoday-row">' + cards + tour + '</div></section>';
  }

  function renderLobby(box) {
    loadDaily(false);
    loadPopular();
    loadPadel();
    const mineFirst = rooms.slice().sort((a, b) => (seatOfMe(b) != null ? 1 : 0) - (seatOfMe(a) != null ? 1 : 0));
    const want = find.trim().toLowerCase();
    const all = entries();
    // Розділ, якого вже нема (усі його ігри вимкнено), — назад на «Усі»; тема, якої в розділі нема, — на «Усе в розділі».
    if (filter !== 'all' && !all.some((e) => e.group === filter)) filter = 'all';
    const match = (e) => (filter === 'all' || e.group === filter)
      && (!want || (e.title + ' ' + e.hint + ' ' + (e.list || []).map((x) => x.g.title + ' ' + x.label).join(' ')
        + (SLOT_INFO[e.ids[0]] ? ' слот слоти автомат автомати 🍒' : '')).toLowerCase().includes(want));   // назви автоматів без «слот»
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
        const act = e.solo ? 'data-solo="' + esc(e.g.id) + '"' : e.g && e.g.shared ? 'data-shared="' + esc(e.g.id) + '"' : 'data-new="' + esc(e.kind === 'family' ? 'f:' + e.f.id : e.g.id) + '"';
        return '<button class="gfav" data-pre="' + esc(e.ids.join(' ')) + '" ' + act + ' title="' + esc(playsOf(e) + ' ' + (playsOf(e) % 10 >= 2 && playsOf(e) % 10 <= 4 && (playsOf(e) % 100 < 12 || playsOf(e) % 100 > 14) ? 'партії' : 'партій') + ' за місяць') + '">'
          + iconOf(e.ids[0]) + '<b>' + esc(e.title) + '</b><span class="muted small">' + (e.solo ? 'грати' : e.g && e.g.shared ? 'сісти' : '+ стіл') + '</span></button>';
      }).join('') + '</div>'
      : '';

    // Каталог: на «Усі» без пошуку — розділами з заголовками, а в розділі — темами з підзаголовками (у темі спершу те,
    // у що грають). Обраний розділ — другий ряд чипів тем: «Усе в розділі» — так само темами, обрана тема — лише її плитки.
    // Пошук — просто знайдене, без тем.
    // Тема «🍒 Слоти» — афіші автоматів, а не звичайні плитки
    const tilesOf = (items, tid) => tid === 'slots'
      ? '<div class="gposters">' + items.map(posterHtml).join('') + '</div>'
      : '<div class="gtiles">' + items.map(tileHtml).join('') + '</div>';
    // Теми розділу — блоки в одній сітці з колонками плитки: дрібні стають поруч, якщо влазять (--n — скільки плиток).
    const byThemes = (ths) => '<div class="gthemegrid">' + ths.map(({ t, items }) => '<div class="gtblock' + (t.id === 'slots' ? ' gt-slots' : '') + '" style="--n:' + items.length + '">'
      + '<h5 class="gtheme">' + t.icon + ' ' + esc(t.title) + ' <span class="gtheme-n">· ' + items.length + '</span></h5>'
      + tilesOf(items, t.id) + '</div>').join('') + '</div>';
    const ths = filter !== 'all' && !want ? themesIn(filter, list) : [];
    if (theme && !want && (ths.length < 2 || !ths.some((x) => x.t.id === theme))) theme = '';   // на пошуку тему не губимо
    const themeRow = ths.length > 1
      ? '<div class="gthemes" role="group" aria-label="Теми розділу">'
        + [{ t: { id: '', icon: '', title: 'Усе в розділі' }, items: list }].concat(ths)
          .map(({ t, items }) => '<button class="chip gtchip' + (theme === t.id ? ' on' : '') + '" data-theme="' + t.id + '">'
            + (t.icon ? t.icon + ' ' : '') + esc(t.title) + '<span class="gtc-n">' + items.length + '</span></button>').join('')
        + '</div>'
      : '';
    const tiles = !list.length ? ''
      : want ? tilesOf(list)
        : filter === 'all'
          ? GROUPS.filter((g) => g.id !== 'all').map((g) => {
            const part = list.filter((e) => e.group === g.id);
            return part.length ? '<h4 class="ggroup">' + g.icon + ' ' + esc(g.title) + ' <span class="muted small">· ' + part.length + '</span></h4>'
              + (g.id === 'azart' ? azartHeadHtml() : '') + byThemes(themesIn(g.id, part)) : '';
          }).join('')
          : (filter === 'azart' ? azartHeadHtml() : '') + (theme ? tilesOf(ths.find((x) => x.t.id === theme).items, theme) : byThemes(ths));

    const links = [['#stats/games', '🏆 Таблиці ігор'], ['#stats/time', '⏱ Хто скільки грав'], ['#lavka', '🛍 Лавка Дядька Глека']]
      .concat(extraPanels.filter((p) => p.id !== 'svoya' && (!p.visible || p.visible())).map((p) => ['#games/x:' + p.id, (p.icon || '📋') + ' ' + p.title]))
      .concat(extraPanels.some((p) => p.id === 'svoya') ? [['#games/x:svoya', '🎯 Пакети Своєї гри']] : []);

    box.innerHTML = todayHtml()
      + '<section class="gpanel"><h3>🔥 Живі столи'
      + (rooms.length ? ' <span class="muted small">· ' + rooms.length + '</span>' : '') + '</h3>'
      + live + soloLine + '</section>'
      + '<section class="gpanel"><h3>Каталог <span class="muted small">· ' + all.filter((e) => e.kind !== 'link').length + ' ігор</span></h3>'
      + favRow
      + '<div class="gfilters">'
      + GROUPS.filter((g) => g.id === 'all' || all.some((x) => x.group === g.id))
        .map((g) => '<button class="chip gchip' + (filter === g.id ? ' on' : '') + '" data-filter="' + g.id + '">'
          + (g.icon ? g.icon + ' ' : '') + esc(g.title) + '</button>').join('')
      + '<input class="gfind" type="search" placeholder="знайти гру" value="' + esc(find) + '" autocomplete="off">'
      + '</div>'
      + themeRow
      + (tiles || '<div class="gempty">Овва, нічого схожого не знайшлось. Спробуй інакше або зніми фільтр.</div>')
      + '<div class="glinks">' + links.map(([h, l]) => '<a href="' + h + '">' + esc(l) + '</a>').join('<span>·</span>') + '</div>'
      + '</section>';

    box.querySelectorAll('[data-filter]').forEach((b) => b.onclick = () => {
      if (filter !== b.dataset.filter) theme = '';   // інший розділ — з «Усе в розділі»
      filter = b.dataset.filter;
      try { localStorage.setItem('gamesFilter', filter); localStorage.setItem('gamesTheme', theme); } catch { /* приватне вікно */ }
      renderView();
    });
    box.querySelectorAll('[data-theme]').forEach((b) => b.onclick = () => {
      theme = b.dataset.theme;
      try { localStorage.setItem('gamesTheme', theme); } catch { /* приватне вікно */ }
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
    box.querySelectorAll('[data-shared]').forEach((b) => b.onclick = (e) => {
      if (!me.nick) { askNick(); return; }
      busy(e.currentTarget, 'мить…', async () => {
        const r = await openRoom('CreateRoom', b.dataset.shared, {});
        if (r.ok && r.roomId) go('#games/room/' + encodeURIComponent(r.roomId));
      });
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
    azartWatch();
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

  const panelCtx = () => ({ me, esc, toast, busy, api, call, ui, css: cssVar, catalog, sub: view.kind === 'panel' ? view.sub || '' : '' });

  // =============================================================================================
  // Попап створення столу
  // =============================================================================================

  /// Ставки є лише там, де є що ділити: рівно двоє і партія рейтингова (ARCHITECTURE §4.4).
  /// Те саме правило на сервері (Rooms.ReadStake), тому змійка й дуель теж зі ставками.
  const stakeable = (g) => !!g && g.maxPlayers === 2 && !!g.rated;

  /// Пари [значення, підпис] опції — з каталогу вони приходять масивами, але терпимо й {value, label}.
  const optPairs = (o) => (o.values || []).map((v) => Array.isArray(v) ? v : [v.value, v.label || v.value]);

  /// Опція в попапі: звичайна — випадайка, multi — чипи, де можна ввімкнути кілька. cur — що стоїть зараз
  /// (попап «⚙ Налаштування» вже поставленого столу); без нього — типове.
  function optHtml(o, cur) {
    const now = cur != null ? String(cur) : String(o.default || '');
    if (o.multi) {
      const on = now.split(',');
      return '<div class="gopt"><span class="muted small">' + esc(o.label) + '</span>'
        + '<div class="gpicks" data-key="' + esc(o.key) + '" data-any="' + esc(o.default || '') + '">'
        + optPairs(o).map(([val, lab]) => '<button type="button" class="gpick' + (on.includes(val) ? ' on' : '')
          + '" data-val="' + esc(val) + '">' + esc(lab) + '</button>').join('')
        + '</div></div>';
    }
    return '<label class="gopt"><span class="muted small">' + esc(o.label) + '</span>'
      + '<select data-key="' + esc(o.key) + '">'
      + optPairs(o).map(([val, lab]) => '<option value="' + esc(val) + '"' + (val === now ? ' selected' : '') + '>' + esc(lab) + '</option>').join('')
      + '</select></label>';
  }

  /// Що обрано в попапі: випадайки — значенням, multi — значеннями через кому (як чекає Rooms.Effective).
  function optValues(box) {
    const out = {};
    box.querySelectorAll('select[data-key]').forEach((s) => out[s.dataset.key] = s.value);
    box.querySelectorAll('.gpicks').forEach((p) => out[p.dataset.key] =
      [...p.querySelectorAll('.gpick.on')].map((x) => x.dataset.val).join(','));
    return out;
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
      // Соло-режим родини (Шахи з Глеком) — не «стіл», а гра, що відкривається одразу.
      const go = wrap.querySelector('[data-go]');
      if (go) go.textContent = g.maxPlayers === 1 ? 'Грати' : 'Поставити стіл';
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
      const payload = optValues(box);
      const st = box.querySelector('.gstake.on');
      if (st) payload.stake = +st.dataset.stake;
      const r = await openRoom('CreateRoom', g.id, payload);
      if (r.ok) { close(); if (r.roomId) go('#games/room/' + encodeURIComponent(r.roomId)); }
    });
  }

  /// «⚙ Налаштування» поставленого столу (господар, між партіями): ті самі опції, що в попапі створення, з тим, що
  /// стоїть зараз. За дограним столом сервер (Rooms.Reconfigure) вертає стіл у лобі з новою партією — кажемо це наперед.
  function openSettings(id) {
    const rv = views[id];
    const g = rv && gameOf(rv.room.game);
    if (!g || !(g.options || []).length) return;
    const room = rv.room;
    const wrap = document.createElement('div');
    wrap.className = 'modal gmodal';
    wrap.innerHTML = '<div class="card">'
      + '<h3>⚙ ' + iconOf(g.id) + esc(g.title) + '</h3>'
      + (room.status !== 'lobby' || room.startedAt
        ? '<div class="muted small">Стіл знову стане в лобі — далі «Почати», коли всі готові. Рахунок вечора лишається.</div>'
        : '')
      + '<div class="gvar">' + g.options.map((o) => optHtml(o, room.options ? room.options[o.key] : null)).join('') + '</div>'
      + '<div class="grow"><button class="primary" data-go>Зберегти</button><button class="ghost" data-close>Скасувати</button></div>'
      + '</div>';
    document.body.appendChild(wrap);
    const close = () => wrap.remove();
    wrap.addEventListener('click', (e) => { if (e.target === wrap) close(); });
    wrap.querySelector('[data-close]').onclick = close;
    wrap.querySelectorAll('.gpicks').forEach((p) => p.querySelectorAll('.gpick').forEach((b) => b.onclick = () => togglePick(p, b)));
    wrap.querySelector('[data-go]').onclick = (e) => busy(e.currentTarget, 'мить…', async () => {
      const r = await call('ConfigureRoom', id, optValues(wrap.querySelector('.gvar')));
      if (r.ok) close();
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
    // .gextra — місце під кнопками для панелей лобі й «після партії» (🎲 ставки столу); порожнє не займає місця.
    el.innerHTML = '<div class="gseats"></div><div class="gbody"></div><div class="gstatus"></div><div class="gbtns"></div><div class="gextra"></div>';
    const card = {
      id, el,
      head: el.querySelector('.gseats'),
      body: el.querySelector('.gbody'),
      statusEl: el.querySelector('.gstatus'),
      btns: el.querySelector('.gbtns'),
      extra: el.querySelector('.gextra'),
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

  // ---------------------------------------------------------------------------------------------
  // Реакції-емодзі за настільним столом (прохід №3, п. 231): настільні мовчазні, а підколоти хочеться.
  // Загальна дія каркаса: сервер (Rooms.TableReact) лише пересилає номер групі столу, гра про це не знає.
  // ---------------------------------------------------------------------------------------------
  const REACTS = ['😂', '🔥', '🤯', '👏', '😱'];
  const REACT_GAMES = new Set(['chess', 'checkers', 'domino', 'durak', 'c4', 'c4x', 'ttt', 'ttt3', 'ttt9', 'zirka', 'reversi']);
  const REACT_GAP_MS = 1500;   // = Rooms.ReactGapMs
  let reactUntil = 0;
  function paintReacts(card, rv) {
    const want = REACT_GAMES.has(rv.room.game) && rv.room.maxPlayers > 1 && !!me.nick;
    if (!want) {
      if (card.rx) { card.rx.remove(); card.rx = null; card.el.classList.remove('grx-on'); }
      return;
    }
    if (card.rx) return;
    const bar = document.createElement('div');
    bar.className = 'grx';
    bar.innerHTML = REACTS.map((x, i) => '<button type="button" class="ghost grx-b" data-rx="' + i + '" title="Кинути ' + x
      + ' усім за столом">' + x + '</button>').join('');
    bar.onclick = (ev) => { const b = ev.target.closest('[data-rx]'); if (b) sendReact(card, +b.dataset.rx); };
    card.btns.after(bar);
    card.el.classList.add('grx-on');
    card.rx = bar;
  }
  function sendReact(card, e) {
    const now = Date.now();
    if (now < reactUntil) return;
    reactUntil = now + REACT_GAP_MS;
    const bar = card.rx;
    if (bar) { bar.classList.add('wait'); setTimeout(() => bar.classList.remove('wait'), REACT_GAP_MS); }
    if (!conn || conn.state !== 'Connected') return;
    // Хаб відповідає рядком помилки або null; старий сервер методу не знає — мовчки нічого.
    conn.invoke('TableReact', card.id, e).then((err) => { if (err) errToast(err); }).catch(() => {});
  }
  function flyReact(x) {
    const card = x && cards[x.id];
    const emo = card && REACTS[x.e | 0];
    if (!emo || !card.el.isConnected || card.el.offsetParent === null || document.hidden) return;
    if (card.el.querySelectorAll('.grx-fly').length >= 12) return;   // хай і завалили — дошку не ховаємо
    const s = document.createElement('span');
    s.className = 'grx-fly';
    s.style.left = (12 + Math.random() * 76).toFixed(1) + '%';
    s.innerHTML = emo + '<i>' + esc(x.nick || '') + '</i>';
    card.el.appendChild(s);
    const done = () => s.remove();
    s.addEventListener('animationend', done);
    setTimeout(done, 3000);
  }

  // ---------------------------------------------------------------------------------------------
  // 🎺 Гімн переможця (docs/games/specs/anthem.md §4). Сервер шле 'anthem' групі столу (гравці й глядачі) і тим, хто
  // за ним сидить, але зараз деінде на сайті; грає його app.js. Тут — лише чи грати: людина за цим столом (сторінка
  // столу відкрита або вона сидить за ним), а партія ще не звучала ні в цій вкладці (дубль із сервера), ні в сусідній
  // вкладці того самого браузера (claimAnthem). Смужка «🎺 Гімн: Оля — «Трембіта»» з'являється, лише коли звук справді
  // пішов (браузер міг і не дати), — у потоці картки просто над рядком «Перемога: …»: партія щойно скінчилась, тож
  // невеликий зсув там нікому не заважає, а назва гри, місця й дошка лишаються на виду.
  // Гімн замовкає, коли за столом почалась нова партія або людина пішла зі сторінки цього столу.
  // ---------------------------------------------------------------------------------------------
  function onTablePage(id) { return shown && view.kind === 'room' && view.id === id; }
  function atTable(id) { return onTablePage(id) || !!(views[id] && views[id].seat != null); }

  // Кілька вкладок одного браузера отримують ту саму подію — грати має одна. Ключ «стіл:раунд» у localStorage зі
  // штампом часу й позначкою вкладки: хто записав останнім, той і грає (решта, перечитавши, бачать чужу позначку).
  // Вкладка, де відкрито сам стіл, заявляється одразу, решта — трохи згодом: так гімн звучить там, де на нього дивляться.
  // Заявки, старші за хвилину, — сміття від закритих вкладок: не заважають і прибираються.
  // Прокльон іде за гімном своєї партії: грає та вкладка, що взяла гімн; без гімну — заявляється сам, своєю приставкою.
  // Дзвінок — теж своєю; його заявка живе 8 с — наступний заклик тієї самої людини за той самий стіл має задзвеніти знову.
  const ANTHEM_CLAIM = 'anthemClaim:';
  const CURSE_CLAIM = 'curseClaim:';
  const RING_CLAIM = 'ringClaim:';
  const ANTHEM_CLAIM_MS = 60 * 1000;
  const RING_CLAIM_MS = 8 * 1000;
  const tabMark = Math.random().toString(36).slice(2, 10);
  function readClaim(key, now, ttl) {
    const v = localStorage.getItem(key);
    if (!v) return null;
    const [at, tab] = v.split(' ');
    return now - (+at || 0) < (ttl || ANTHEM_CLAIM_MS) ? tab || '' : null;
  }
  function claimAnthem(k, here, prefix, ttl) {
    prefix = prefix || ANTHEM_CLAIM;
    return new Promise((done) => {
      const key = prefix + k;
      const step = (fn, ms) => setTimeout(() => { try { fn(); } catch { done(true); } }, ms);   // приватне вікно — граємо самі
      step(() => {
        const now = Date.now();
        for (let i = localStorage.length - 1; i >= 0; i--) {
          const n = localStorage.key(i);
          if (n && n.startsWith(prefix) && n !== key && readClaim(n, now, ttl) == null) localStorage.removeItem(n);
        }
        const owner = readClaim(key, now, ttl);
        if (owner != null && owner !== tabMark) { done(false); return; }
        localStorage.setItem(key, now + ' ' + tabMark);
        step(() => done(readClaim(key, Date.now(), ttl) === tabMark), 60);
      }, here ? 0 : 150);
    });
  }

  // Подія може нести святкування (fx, flair.md §3), а url — бути null (святкування без гімну). Святкування — тихе: його
  // видно й тоді, коли гімни вимкнено, і в кожній вкладці, де стіл на екрані (заявка між вкладками — лише про звук).
  async function onAnthem(a) {
    if (!a || !a.id || (!a.url && !a.fx)) return;
    const k = a.id + ':' + (a.round | 0);
    if (anthemsPlayed.has(k) || !atTable(a.id)) return;
    anthemsPlayed.add(k);
    // len — скільки звучить гімн (сервер міряє файл): святкування триває стільки ж у кожній вкладці, а не лише в тій,
    // де гімн справді грає
    if (a.fx) startFx(a.id, k, a.fx, a.url ? a.len : null);
    if (!a.url || !anthem) { curseGo(k); return; }
    anthemsWaiting.add(k);
    const claim = claimAnthem(k, onTablePage(a.id));
    anthemClaims.set(k, claim);
    setTimeout(() => anthemClaims.delete(k), ANTHEM_CLAIM_MS);
    const mine = await claim;
    anthemsWaiting.delete(k);
    if (!mine || !atTable(a.id)) { curseGo(k); return; }
    const cur = { id: a.id, round: a.round | 0, k, a, here: onTablePage(a.id), started: false };
    const prev = anthemAt;
    anthemAt = cur;                 // до виклику: onEnd може прийти одразу, якщо браузер не дав звуку
    let ok = false;
    // Смужка — лише коли play() справді пішов (onStart); браузер відмовив — onEnd без onStart, і смужки не буде.
    const onStart = () => { if (anthemAt === cur) { cur.started = true; paintAnthem(); } };
    try { ok = !!anthem(a, { onStart, onEnd: () => anthemEnded(cur) }); } catch (e) { console.warn('[games] anthem', e); }
    if (!ok && anthemAt === cur) anthemAt = prev;
    if (!ok) curseGo(k);
  }
  function anthemEnded(cur) {
    if (cur.started) fxStop(cur.id, cur.k);
    if (anthemAt === cur) { anthemAt = null; paintAnthem(); }
    curseGo(cur.k);                 // прокльон тієї самої партії чекав, поки гімн доспіває
  }
  /// Нова партія за столом, де звучить гімн чи прокльон, — «Ще раз» уже почався, минула партія не перекрикує гру.
  function anthemRound(r) {
    if (!r) return;
    const round = typeof r.round === 'number' ? r.round : null;
    const old = (x) => x && r.id === x.id && ((round != null && round > x.round) || (r.status === 'playing' && round !== x.round));
    if (curseNext && old(curseNext.c)) curseDrop();
    if (old(anthemAt) || old(curseAt)) stopAnthem();
    const f = fxOn[r.id];
    if (f && (round != null && round > f.round || r.status === 'playing' && round !== f.round)) fxStop(r.id);
  }
  /// Пішов зі сторінки столу, на якій гімн звучав, — він замовкає (і прокльон за ним). Хто сидить за столом деінде
  /// (Ефір, лобі), чує гімн своєї партії й так, аж до кінця.
  function anthemPage() {
    if (curseNext && !atTable(curseNext.c.id)) curseDrop();
    if (curseAt) {
      if (onTablePage(curseAt.id)) { curseAt.here = true; if (curseAt.started) paintCurse(); }
      else if (curseAt.here) { if (curseNext && curseNext.c.id === curseAt.id) curseDrop(); stopAnthem(); }
    }
    if (!anthemAt) return;
    if (onTablePage(anthemAt.id)) { anthemAt.here = true; if (anthemAt.started) paintAnthem(); }   // картку могли щойно створити
    else if (anthemAt.here) { if (curseNext && curseNext.c.id === anthemAt.id) curseDrop(); stopAnthem(); }
  }

  // 🎉 Святкування переможця (flair.md §3): шар поверх картки столу (app.js playFx), 6 с без гімну; із гімном — стільки,
  // скільки він звучить (len з події), але не довше 15 с. Картки нема (людина сидить, а дивиться Ефір) — і святкувати
  // нема на чому.
  const FX_MS = 6000;
  const FX_MAX_MS = 15000;
  function startFx(id, k, what, len) {
    const card = cards[id];
    if (!fx || !card) return;
    fxStop(id);
    const ms = +len > 0 ? Math.min(FX_MAX_MS, Math.round(+len * 1000)) : FX_MS;
    let stop = () => {};
    try { stop = fx(card.el, what, { ms }) || stop; } catch (e) { console.warn('[games] fx', e); }
    const round = +k.slice(k.lastIndexOf(':') + 1) || 0;
    const f = fxOn[id] = { k, round, stop, timer: 0 };
    f.timer = setTimeout(() => fxStop(id, k), ms);
  }
  function fxStop(id, k) {
    const f = fxOn[id];
    if (!f || (k && f.k !== k)) return;
    delete fxOn[id];
    clearTimeout(f.timer);
    try { f.stop(); } catch { /* шар уже зник */ }
  }

  // ---------------------------------------------------------------------------------------------
  // 😈 Прокльон (flair.md §1): подія 'curse' { id, round, nick, title, emoji, url, left } — той, хто програв, і що
  // звучить. Ті самі правила, що й для гімну: лише за цим столом, раз на партію, одна вкладка (та сама, що взяла гімн
  // цієї партії; без гімну — своя заявка), вимикач 'anthemSound'. Гімн і прокльон однієї партії не перекрикують одне одного: прокльон чекає в черзі з одного,
  // поки гімн доспіває; гімн, що ще не прийшов, чекаємо 1,5 с — далі прокльон грає сам.
  // ---------------------------------------------------------------------------------------------
  const CURSE_WAIT_MS = 1500;
  const cursesPlayed = new Set();
  async function onCurse(c) {
    if (!c || !c.id || !c.url || !anthem) return;
    const k = c.id + ':' + (c.round | 0);
    if (cursesPlayed.has(k) || !atTable(c.id)) return;
    cursesPlayed.add(k);
    // Гімн цієї партії вже заявлявся — прокльон іде за ним: грає вкладка, що взяла гімн, а програла — мовчить і про
    // прокльон (інакше гімн звучав би в одній вкладці, а прокльон — в іншій). Гімну нема — прокльон заявляється сам.
    const anthemClaim = anthemClaims.get(k);
    const mine = anthemClaim ? await anthemClaim : await claimAnthem(k, onTablePage(c.id), CURSE_CLAIM);
    if (!mine || !atTable(c.id)) return;
    if (curseNext) curseDrop();
    const anthemHere = anthemAt && anthemAt.k === k;
    if (anthemHere || anthemsWaiting.has(k)) { curseNext = { k, c, timer: 0 }; return; }   // гімн звучить чи от-от
    if (anthemsPlayed.has(k)) { playCurse(c); return; }                                   // гімн уже був (чи не грав)
    curseNext = { k, c, timer: setTimeout(() => curseGo(k), CURSE_WAIT_MS) };            // гімн може ще прийти
  }
  /// Гімн цієї партії доспівав (чи не заграв, чи не прийшов за 1,5 с) — черга прокльону рушає.
  function curseGo(k) {
    const n = curseNext;
    if (!n || n.k !== k) return;
    if (anthemAt && anthemAt.k === k) return;     // гімн іще звучить — дочекаємось його кінця
    if (anthemsWaiting.has(k)) return;            // гімн ще заявляється — curseGo покличе onAnthem
    curseDrop();
    if (atTable(n.c.id)) playCurse(n.c);
  }
  function curseDrop() {
    if (!curseNext) return;
    clearTimeout(curseNext.timer);
    curseNext = null;
  }
  function playCurse(c) {
    const cur = { id: c.id, round: c.round | 0, c, here: onTablePage(c.id), started: false };
    const prev = curseAt;
    curseAt = cur;
    let ok = false;
    const onStart = () => { if (curseAt === cur) { cur.started = true; paintCurse(); } };
    const onEnd = () => { if (curseAt === cur) { curseAt = null; paintCurse(); } };
    try { ok = !!anthem({ url: c.url, title: c.title, emoji: c.emoji, nick: c.nick }, { onStart, onEnd }); } catch (e) { console.warn('[games] curse', e); }
    if (!ok && curseAt === cur) curseAt = prev;
  }
  /// Смужка «😈 Прокльон «Цап» · ціль: Петро · лишилось 2» там само, де й гімнова, з ⏹ і 🔇. Нік — лише в називному
  /// і без «програв/програла»: рід із ніка не вгадати.
  function paintCurse() {
    const on = curseAt && curseAt.started ? curseAt : null;
    document.querySelectorAll('.gcurse').forEach((el) => { if (!on || el.dataset.room !== on.id) el.remove(); });
    if (!on) return;
    const card = cards[on.id];
    if (!card || card.el.querySelector(':scope > .gcurse')) return;
    const c = on.c;
    const left = c.left | 0;
    const el = document.createElement('div');
    el.className = 'ganthem gcurse';
    el.dataset.room = on.id;
    el.setAttribute('role', 'status');
    el.innerHTML = '<span class="ganth-t"><span class="ganth-e" aria-hidden="true">😈</span>Прокльон' + (c.title ? ' «' + esc(c.title) + '»' : '')
      + (c.nick ? ' · ціль: ' + esc(c.nick) : '') + ' · ' + (left > 0 ? 'лишилось ' + left : 'розрядився') + '</span>'
      + '<button type="button" class="ganth-stop" title="Зупинити цей прокльон" aria-label="Зупинити цей прокльон">⏹</button>'
      + '<button type="button" class="ganth-off" title="Більше не грати гімнів, прокльонів і дзвінків. Увімкнути — у Лавці, на полиці «🎺 Гімни»">🔇 Тиша</button>';
    el.querySelector('.ganth-stop').onclick = () => stopAnthem();
    el.querySelector('.ganth-off').onclick = soundOff;
    card.el.insertBefore(el, card.statusEl.parentNode === card.el ? card.statusEl : null);
  }
  function soundOff() {
    try { localStorage.setItem('anthemSound', '0'); } catch { /* приватне вікно — вимкнемо хоч цей */ }
    curseDrop();
    stopAnthem();
    toast('🔇 Гімни, прокльони й дзвінки вимкнено. Увімкнути — у Лавці, на полиці «🎺 Гімни»');
  }
  /// Смужка на картці столу, поки звучить гімн (лише після справжнього старту): хто й що, ⏹ і «🔇 Без гімнів».
  function paintAnthem() {
    const on = anthemAt && anthemAt.started ? anthemAt : null;
    document.querySelectorAll('.ganthem:not(.gcurse)').forEach((el) => { if (!on || el.dataset.room !== on.id) el.remove(); });
    if (!on) return;
    const card = cards[on.id];
    if (!card || card.el.querySelector(':scope > .ganthem:not(.gcurse)')) return;
    const a = on.a;
    // Нік — лише в називному («Гімн: Оля»): родовий від ніка з кількох слів ламається («Гімн Теста Оля»)
    const who = a.nick ? ': ' + esc(a.nick) : '';
    const el = document.createElement('div');
    el.className = 'ganthem';
    el.dataset.room = on.id;
    el.setAttribute('role', 'status');
    el.innerHTML = '<span class="ganth-t"><span class="ganth-e" aria-hidden="true">🎺</span>Гімн' + who
      + (a.title ? ' — «' + esc(a.title) + '»' : '') + '</span>'
      + '<button type="button" class="ganth-stop" title="Зупинити цей гімн" aria-label="Зупинити цей гімн">⏹</button>'
      + '<button type="button" class="ganth-off" title="Більше не грати гімнів, прокльонів і дзвінків. Увімкнути — у Лавці, на полиці «🎺 Гімни»">🔇 Без гімнів</button>';
    el.querySelector('.ganth-stop').onclick = () => stopAnthem();
    el.querySelector('.ganth-off').onclick = soundOff;
    // просто над «Перемога: …» (на телефоні .gstatus стоїть над полем — order у CSS тримає смужку поруч)
    card.el.insertBefore(el, card.statusEl.parentNode === card.el ? card.statusEl : null);
  }

  // ---------------------------------------------------------------------------------------------
  // Картку сховано / показано (прохід №3, п. 240). Стіл, за яким сидиш, лишається змонтованим на складі, коли йдеш
  // у лобі, в інший розділ сайту чи в іншу вкладку браузера, — і реалтайм-гра крутила rAF у порожнечу або сама
  // опитувала offsetParent / ставила IntersectionObserver. Тепер каркас каже сам: ctx.shown — чи картку видно зараз,
  // а необов'язковий mod.visible(root, ctx, on) кличеться лише на зміну (після mount; сам mount бачить ctx.shown).
  // Старий модуль без visible нічого не помітить.
  // ---------------------------------------------------------------------------------------------
  const cardOn = (c) => shown && !document.hidden && view.kind === 'room' && view.id === c.id && !c.el.hidden
    && c.el.isConnected;
  function syncShown() {
    for (const id in cards) {
      const c = cards[id];
      if (!c.mounted || !c.ctx) continue;
      const on = cardOn(c);
      if (on === c.vis) continue;
      c.vis = c.ctx.shown = on;
      if (c.mod && c.mod.visible) { try { c.mod.visible(c.body, c.ctx, on); } catch (e) { console.warn('[games] visible', e); } }
    }
  }
  document.addEventListener('visibilitychange', syncShown);

  function dropCard(id) {
    const c = cards[id];
    if (!c) return;
    if (c.mounted && c.mod && c.mod.unmount) { try { c.mod.unmount(c.body, c.ctx); } catch (e) { console.warn('[games] unmount', e); } }
    if (window.HTableBets) HTableBets.drop(id);
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
    // Ім'я на місці: людина або бот гри (SeatBot, «🤖 бот»), — для підписів на полі, де nickOf бота не знає.
    ctx.nameOf = (i) => nickAt(room, i) || botAt(room, i);
    ctx.act = (action, payload) => call('Act', room.id, action, payload === undefined ? null : payload);
    ctx.input = (action, payload) => send('Input', room.id, action, payload === undefined ? null : payload);
    // Повний вид (Game.Snapshot) ще раз, лише мені — коли в легкому виді розсилки бракує того, чого модуль не має
    // (прохід №3, п. 247). Не частіше ніж раз на 1,5 с: вид сам прийде подією 'room'.
    ctx.resync = () => {
      const now = Date.now();
      if (now - (card.resyncAt || 0) < 1500) return;
      card.resyncAt = now;
      send('SnapshotRoom', room.id);
    };
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
      for (let i = 0; i < seatCount(room); i++) if (!nickAt(room, i) && !botAt(room, i)) free++;
      const fold = free > 2;
      // Від п'яти гравців на телефоні чіпи ніків стояли 4–5 рядками над грою. Там лишаємо свій чіп, чий хід
      // і «👥 N» — дотик розгортає всіх (core.css, .gseats.many). На широкому екрані видно всіх, як і було.
      const taken = seatCount(room) - free;
      for (let i = 0; i < seatCount(room); i++) {
        const nick = nickAt(room, i) || botAt(room, i);
        if (fold && !nick) continue;
        const turn = room.status === 'playing' && turnOf(rv) === i;
        // data-nick — людям (не ботам): за ним 🎙 Посиденьки (web/voice.js) підсвічують, хто за столом говорить.
        const human = nickAt(room, i);
        // ✋ — готовий до партії (Rooms.SetReady); бот готовий завжди. Посеред партії позначки нема: питати вже пізно.
        const ready = room.status !== 'playing' && takenSeats(room) > 1 && (human ? readyAt(room, i) : !!nick);
        chips.push('<span class="gseat ' + seatClassOf(rv, i) + (nick ? '' : ' free') + (turn ? ' turn' : '')
          + (i === rv.seat ? ' me' : '') + (ready ? ' ready' : '') + '"' + (human ? ' data-nick="' + esc(human) + '"' : '')
          + (ready ? ' title="готовий"' : '') + '>' + (ready ? '<b class="gready-mark" aria-label="готовий">✋</b>' : '')
          + '<i>' + esc(seatNameOf(rv, i)) + '</i>' + esc(nick || 'вільно') + '</span>');
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
      + (room.watchers ? '<span class="gwatchers" title="Скільки дивиться">👁 ' + room.watchers + '</span>' : '')
      // Рахунок вечора — між партіями (лобі столу й підсумок); посеред гри шапку не ширимо.
      + (room.status !== 'playing' && eveningText(room) ? '<span class="gevening" title="' + esc(eveningTitle(room)) + '">'
        + esc(eveningText(room)) + '</span>' : '');
  }

  function defaultStatus(rv) {
    const r = rv.room;
    const solo = r.maxPlayers === 1;
    if (r.status === 'finished') {
      const res = r.result;
      if (!res) return 'Партію зіграно';
      // Кооп і партії з ботами: порожні winners там — «без нагород», а не нічия; гра сама каже, чим скінчилось.
      if (res.verdict) return res.verdict;
      // соло: «перемога над собою» звучить дивно, тому беремо те, що написала гра
      if (solo) return res.text || (res.draw ? 'Цього разу не вийшло' : 'Є! Готово');
      if (res.draw || !(res.winners || []).length) return 'Нічия';
      // «Є!» — лише переможцеві: суперник і глядач бачать просто, чия перемога.
      return (rv.seat != null && res.winners.includes(rv.seat) ? 'Є! ' : '')
        + 'Перемога: ' + res.winners.map((i) => nickAt(r, i) || botAt(r, i) || seatNameOf(rv, i)).join(', ');
    }
    if (r.status === 'lobby') {
      if (solo) return '';
      // Стіл, що стартує з руки господаря: коли людей уже досить, «Чекаємо, хто підсяде» вводило в оману —
      // господар сидів і чекав, хоча міг тиснути «Почати».
      // Сам за столом гри з ботом: без «🤖 + бот» партія не почнеться (LiveBots.AloneText) — кажемо це одразу.
      if (rv.view && rv.view.botOffer && takenSeats(r) === 1) {
        if (!sameNick(r.host, me.nick)) return 'Чекаємо, поки ' + (r.host || 'господар') + ' почне';
        return rv.view.botWanted ? 'Бот сидить навпроти — тисни «Почати». Без нагород'
          : 'Сам за столом: поклич «🤖 + бот» або зачекай друга';
      }
      if (hostStarts(r) && takenSeats(r) >= r.minPlayers) {
        return sameNick(r.host, me.nick) ? 'Можна рушати: тисни «Почати»'
          + (freeSeat(r) >= 0 ? ' або зачекай ще когось' : '') : 'Чекаємо, поки ' + (r.host || 'господар') + ' почне';
      }
      return freeSeat(r) >= 0 ? 'Чекаємо, хто підсяде' : 'Чекаємо на старт';
    }
    const t = turnOf(rv);
    if (t != null) return t === rv.seat ? 'Твій хід' : 'Ходить ' + (nickAt(r, t) || botAt(r, t) || seatNameOf(rv, t));
    return rv.seat == null ? 'Дивишся збоку' : '';
  }

  /// Кнопка «🤖 + бот»: господар, сидить, партія не йде, а гра пропонує бота (сам за столом чи бота вже кликали).
  function botOffered(rv) {
    const r = rv.room, v = rv.view;
    return !!(v && v.botOffer) && rv.seat != null && r.status !== 'playing' && sameNick(r.host, me.nick);
  }

  function ownLeave(rv) {
    const m = modules[rv.room.game];
    if (!m || typeof m.ownLeave !== 'function') return false;
    try { return !!m.ownLeave(rv); } catch (e) { console.warn('[games] ownLeave', e); return false; }
  }

  function btnsHtml(rv) {
    const r = rv.room;
    const solo = r.maxPlayers === 1;
    const out = [];
    // Дограний стіл із вільним місцем сервер віддає новому гравцеві (Rooms.Join, гілка reopen),
    // тож статус тут не питаємо — інакше стіл висів би в лобі до прибиральника, і сісти нікому.
    const canSit = !solo && rv.seat == null && freeSeat(r) >= 0 && r.status !== 'playing';
    if (canSit) out.push('<button class="primary" data-do="JoinRoom">Сісти</button>');
    // 🏆 Стіл щойно дограної гри турніру: відлік до наступного столу (tournament.js) замість «Ану ще раз» — нова
    // партія тут посадила б усіх знову, і турнір не зміг би поставити наступну гру.
    const tour = window.HTournament && HTournament.roomBar ? HTournament.roomBar(r.id, panelCtx()) : null;
    if (tour) out.push(tour.html);
    // «Ану ще раз» пропонуємо лише коли є з ким: інакше кнопка є, а сервер відповідає «Замало гравців»
    if (!solo && !(tour && tour.hold) && rv.seat != null && r.status === 'finished' && takenSeats(r) >= r.minPlayers)
      out.push('<button class="primary" data-do="Rematch">Ану ще раз</button>');
    // щоденна головоломка одна на день — «Ану ще раз» там не пропонуємо
    if (solo && r.status === 'finished' && !(gameOf(r.game) || {}).daily) out.push('<button class="primary" data-do="Rematch">Ану ще раз</button>');
    // Бот уже сидить навпроти: «Почати» і в грі, що стартує сама, коли стіл повний (змійка, дуель), — StartByHost
    // каркаса режиму старту не питає, а без кнопки сам із ботом так і сидів би.
    // Повний стіл у лобі буває лише тоді, коли його вернули туди «⚙ Налаштуваннями» (Rooms.Reconfigure): гра, що
    // стартує сама, коли всі сіли, уже не стартує — підсісти нікому, тож починає господар.
    if (rv.seat != null && r.status === 'lobby' && sameNick(r.host, me.nick) && takenSeats(r) >= r.minPlayers
      && (hostStarts(r) || (botOffered(rv) && rv.view.botWanted)))
      out.push('<button class="primary" data-do="StartRoom">Почати</button>');
    // «✋ Готовий» — у лобі й після партії, коли за столом є ще хтось живий: «Почати» і «Ще раз» спитають тих, хто ні.
    // На столі турніру — ні: наступну гру турнір ставить сам і нікого не питає. Поруч — скільки людей уже готові.
    if (!solo && !(tour && tour.hold) && rv.seat != null && r.status !== 'playing' && takenSeats(r) > 1) {
      const on = readyAt(r, rv.seat);
      let n = 0;
      for (let i = 0; i < seatCount(r); i++) if (nickAt(r, i) && readyAt(r, i)) n++;
      out.push('<button type="button" class="ghost gready' + (on ? ' on' : '') + '" data-ready="' + (on ? '0' : '1') + '" aria-pressed="'
        + on + '" title="' + (on ? 'Зняти «готовий»' : 'Сказати столу, що ти готовий') + '">✋ Готовий'
        + '<span class="gready-n">' + n + '/' + takenSeats(r) + '</span></button>');
    }
    // «⚙ Налаштування» — опції столу між партіями, без «встати й поставити новий». Лише господареві.
    // На столі турніру між іграми — ні: наступну гру ставить турнір (tour.hold), як і з «Ану ще раз».
    if (!solo && !(tour && tour.hold) && rv.seat != null && r.status !== 'playing' && sameNick(r.host, me.nick) && ((gameOf(r.game) || {}).options || []).length)
      out.push('<button class="ghost" data-set="1">⚙ Налаштування</button>');
    // «🤖 + бот» живих ігор (LiveBots.cs): господар сам за столом кличе суперника; гра каже botOffer у виді.
    if (botOffered(rv))
      out.push('<button class="ghost" data-bot="1">' + (rv.view.botWanted ? '🤖 Прогнати бота' : '🤖 + бот') + '</button>');
    // Гра може малювати «Встати» сама (mod.ownLeave(rv) → true; покер-кеш — із сумою й підтвердженням) — тоді каркасної нема.
    if (rv.seat != null) { if (!ownLeave(rv)) out.push('<button class="ghost" data-do="LeaveRoom">' + (solo ? 'Закрити' : 'Встати') + '</button>'); }
    // сісти нема куди (або сидиш за іншим столом) — хоч скажемо, чому кнопок нема
    else if (!solo && !canSit) out.push('<span class="muted small">Дивлюсь збоку</span>');
    return out.join('');
  }

  /// Шапка/статус/кнопки — окремо від .gbody: тіло чіпає лише модуль.
  /// Сиджу за аркадою, і партія йде: на телефоні шторка «💬 Стіл» лягала на кнопки керування (style.css ховає
  /// згорнуту шторку за body.g-arcade). Після партії й у лобі столу вона знову на місці.
  function syncArcade() {
    const rv = shown && view.kind === 'room' ? views[view.id] : null;
    const g = rv && gameOf(rv.room.game);
    // Аркада — жива гра (group live) або соло-реалтайм, що сказав про себе `arcade: true` у register (забіг, цеглини).
    const mod = rv && modules[rv.room.game];
    const on = !!(g && (g.group === 'live' || (mod && mod.arcade)) && rv.seat != null && rv.room.status === 'playing');
    if (document.body.classList.contains('g-arcade') !== on) document.body.classList.toggle('g-arcade', on);
    // Заклики «Х кличе… [Сісти]» лишались висіти над хрестовиною, коли вже сів: посеред аркади й за столом на
    // телефоні їх прибираємо (особисті — ні: «кличе тебе» важливіше).
    if (on || (rv && window.matchMedia('(max-width: 900px)').matches)) {
      document.querySelectorAll('#toasts .ginvite:not(.personal)').forEach((e) => e.remove());
    }
    syncImm();
    refit();
  }

  function refreshCard(id) {
    const card = cards[id], rv = views[id];
    if (!card || !rv) return;
    const ctx = makeCtx(card, rv);

    const mod = modules[rv.room.game] || null;
    if (mod && card.mod !== mod) card.mod = mod;
    if (!mod) loadGame(rv.room.game);   // лінивий вантаж (п. 241): модуль приїде — register() перемалює картку

    const sig = JSON.stringify([rv.room.status, rv.room.seats, rv.room.seatNames, rv.room.watchers, rv.room.stake,
      rv.room.options, rv.room.result, rv.room.evening, rv.room.ready, rv.seat, turnOf(rv), rv.room.host, me.nick, !!card.mod,
      botOffered(rv), !!(rv.view && rv.view.botWanted), ownLeave(rv), window.HTournament && HTournament.barSig ? HTournament.barSig(id) : '']);
    const roomChanged = sig !== card.sig;
    if (roomChanged) {
      card.sig = sig;
      card.head.innerHTML = headHtml(rv);
      // 🎙 голос столу: хто в ньому, хто говорить, кнопка «Говорити» (web/voice.js). Шапку щойно перемальовано.
      if (window.HVoice) { try { HVoice.decorate(card.el); } catch (e) { console.warn('[games] voice', e); } }
      card.head.classList.toggle('many', !!card.head.querySelector('[data-many]'));
      card.btns.innerHTML = btnsHtml(rv);
      card.btns.querySelectorAll('[data-do]').forEach((b) => b.onclick = async (e) => {
        // «Сісти» йде через joinRoom: він сам спитає, чи вставати з попереднього столу, і сам крутить кнопку.
        if (b.dataset.do === 'JoinRoom') { await joinRoom(id, e.currentTarget); return; }
        const r = await busy(e.currentTarget, '…', async () => {
          const r = await call(b.dataset.do, id);
          if (r.ok && b.dataset.do === 'LeaveRoom') {
            // приватну соло-кімнату сервер із лобі не прибере — прибираємо картку самі
            if (views[id] && views[id].loose) { dropCard(id); delete views[id]; pinned.delete(id); }
            if (view.kind === 'room' && view.id === id) go('#games'); else renderView();
          }
          return r;
        });
      });
      card.btns.querySelectorAll('[data-ready]').forEach((b) => b.onclick = (e) =>
        busy(e.currentTarget, '…', () => call('ReadyRoom', id, b.dataset.ready === '1')));
      card.btns.querySelectorAll('[data-set]').forEach((b) => b.onclick = () => openSettings(id));
      card.btns.querySelectorAll('[data-bot]').forEach((b) => b.onclick = (e) => busy(e.currentTarget, '…', () => {
        const v = views[id] && views[id].view;
        return call('Act', id, 'bot', { on: !(v && v.botWanted) });
      }));
      card.el.classList.toggle('mine', rv.seat != null);
      // 🎲 Ставки столу (web/games/bets-table.js) — у .gextra під кнопками; дані панель тягне сама з хаба.
      if (window.HTableBets) { try { HTableBets.paint(card.extra, rv); } catch (e) { console.warn('[games] bets', e); } }
    }

    if (!card.mod) {
      card.body.innerHTML = failed.has(rv.room.game)
        ? '<div class="gwait err">Ой-йой, модуль гри не завантажився</div>'
        : '<div class="gwait"><span class="spin"></span> мить…</div>';
    } else if (rv.view !== undefined) {
      if (!card.mounted) {
        card.body.innerHTML = '';
        card.mounted = true;
        // Модуль бачить, чи його видно, вже в mount; далі про зміну скаже visible() (п. 240).
        card.vis = ctx.shown = cardOn(card);
        try { if (card.mod.mount) card.mod.mount(card.body, ctx); }
        catch (e) { console.warn('[games] mount ' + rv.room.game, e); }
      }
      // Кожна новина лобі ('rooms') кличе нас для всіх відкритих столів — із тим самим, уже баченим видом. Реалтайм-гра
      // живе кадрами, тож старий вид відкидав бомберів на старти й будив привидів вибухів. Той самий вид і той самий
      // стіл (місця, статус, хід…) — модулю нічого нового, update не кличемо.
      if (roomChanged || card.lastView !== rv.view) {
        card.lastView = rv.view;
        try { if (card.mod.update) card.mod.update(card.body, ctx); }
        catch (e) { console.warn('[games] update ' + rv.room.game, e); }
      }
      maybeNews(rv);
    }

    paintStatus(card, rv);
    paintReacts(card, rv);
    if (view.kind === 'room' && view.id === id) syncArcade();
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
  /// «1 очко», «3 очки», «250 очок».
  function points(n) {
    const t = Math.abs(n) % 100, o = Math.abs(n) % 10;
    return n + (t > 10 && t < 20 ? ' очок' : o === 1 ? ' очко' : o >= 2 && o <= 4 ? ' очки' : ' очок');
  }
  function tries(n) {
    const t = n % 100, o = n % 10;
    if (t > 10 && t < 20) return n + ' спроб';
    if (o === 1) return n + ' спробу';
    if (o >= 2 && o <= 4) return n + ' спроби';
    return n + ' спроб';
  }

  // =============================================================================================
  // Вбудована гра (вечірка): модуль іншої гри всередині свого
  // =============================================================================================

  /// HGames.embed(host, gameId, opts) — змонтувати модуль гри gameId у власний root усередині host (контракт —
  /// docs/games/specs/party-minigame.md). opts: { view, frame, seat (місце в підгрі або null), names[], nicks[],
  /// seatNames[], status ('playing'|'finished'), result ({winners}), options, act(a, p), input(a, p) }. act/input
  /// модуль-господар шле у свою дію (напр. ctx.act('mg', {a, p})). Вертає handle:
  /// { ready (проміс: модуль змонтовано), update(o), frame(f), onKey(e), status(), pad, root, unmount() }.
  /// Клавіші й пад каркас вкладеному модулю НЕ роздає: господар кличе handle.onKey зі свого onKey, а свій `pad`
  /// бере з handle.pad (той уже прив'язаний до ctx підгри).
  function embed(host, gameId, opts) {
    const box = document.createElement('div');
    box.className = 'gembed';
    box.dataset.game = gameId;
    host.appendChild(box);
    let o = Object.assign({}, opts || {});
    let mod = null;
    let dead = false;
    const ctx = { frame: null };
    const n = () => Math.max((o.names || []).length, (o.nicks || []).length);
    function build() {
      const names = o.names || [], nicks = o.nicks || [], sn = o.seatNames || [];
      const seats = [];
      for (let i = 0; i < n(); i++) seats.push(nicks[i] ? { i, nick: nicks[i] } : { i, nick: null, bot: names[i] || '🤖 бот' });
      const status = o.status || 'playing';
      ctx.room = {
        id: (o.roomId || 'embed') + ':' + gameId, game: gameId, status, seats, seatNames: sn, host: o.host || '',
        minPlayers: n(), maxPlayers: n(), options: o.options || {}, round: 1, stake: 0, watchers: 0,
        result: status === 'finished' ? Object.assign({ winners: [], draw: false }, o.result || {}) : null,
      };
      ctx.seat = o.seat == null ? null : o.seat;
      ctx.view = o.view || null;
      if (o.frame !== undefined) ctx.frame = o.frame;
      ctx.me = me;
      ctx.playing = status === 'playing';
      ctx.mine = ctx.seat != null;
      ctx.myTurn = ctx.mine && ctx.playing && !!ctx.view && ctx.view.turn === ctx.seat;
      ctx.embedded = true;
      ctx.esc = esc;
      ctx.toast = toast;
      ctx.ui = ui;
      ctx.css = cssVar;
      ctx.seatName = (i) => sn[i] || (i === 0 ? 'перший' : i === 1 ? 'другий' : 'гравець ' + (i + 1));
      ctx.nickOf = (i) => nicks[i] || null;
      ctx.nameOf = (i) => nicks[i] || names[i] || null;
      ctx.act = (a, p) => (o.act ? o.act(a, p === undefined ? null : p) : Promise.resolve({ ok: false }));
      ctx.input = (a, p) => { if (o.input) o.input(a, p === undefined ? null : p); };
      ctx.resync = () => { if (o.resync) o.resync(); };
      return ctx;
    }
    const h = {
      root: box,
      gameId,
      get mod() { return mod; },
      get ctx() { return ctx; },
      /// Пад підгри, прив'язаний до її ctx: господар віддає його як свій `pad` (getter), pad.js кличе when/on.
      get pad() {
        const p = mod && mod.pad;
        if (!p) return null;
        return Object.assign({}, p, {
          when: () => (p.when ? p.when(ctx) : ctx.mine && ctx.playing),
          on: p.on ? (btn) => p.on(btn, ctx) : undefined,
        });
      },
      update(next) {
        if (next) o = Object.assign(o, next);
        if (dead || !mod) return;
        build();
        try { if (mod.update) mod.update(box, ctx); } catch (e) { console.warn('[games] embed update ' + gameId, e); }
      },
      frame(f) {
        ctx.frame = f;
        o.frame = f;
        if (dead || !mod || !mod.frame || !f) return;
        try { mod.frame(box, ctx, f); } catch (e) { console.warn('[games] embed frame ' + gameId, e); }
      },
      onKey(e) {
        if (dead || !mod || !mod.onKey) return false;
        try { return !!mod.onKey(e, ctx); } catch (err) { console.warn('[games] embed onKey', err); return false; }
      },
      status() {
        if (dead || !mod || !mod.status) return '';
        try { return mod.status(ctx) || ''; } catch { return ''; }
      },
      unmount() {
        if (dead) return;
        dead = true;
        try { if (mod && mod.unmount) mod.unmount(box); } catch (e) { console.warn('[games] embed unmount', e); }
        box.remove();
      },
    };
    h.ready = Promise.resolve(byId[gameId] ? null : ensureCatalog()).then(() => loadGame(gameId)).then((ok) => {
      if (dead) return false;
      mod = modules[gameId] || null;
      if (!ok || !mod) { box.innerHTML = '<div class="gempty">Модуль гри «' + esc(gameId) + '» не завантажився</div>'; return false; }
      build();
      try {
        mod.mount(box, ctx);
        if (mod.update) mod.update(box, ctx);
        if (ctx.frame && mod.frame) mod.frame(box, ctx, ctx.frame);
      } catch (e) { console.warn('[games] embed mount ' + gameId, e); return false; }
      return true;
    });
    return h;
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
    // На телефоні заклики не громадяться стосом на чверть екрана: новий замінює старі (особистий — усі).
    if (window.matchMedia('(max-width: 900px)').matches) {
      box.querySelectorAll('.ginvite' + (personal ? '' : ':not(.personal)')).forEach((e) => e.remove());
    }
    // 🔔 Свій дзвінок того, хто кличе (flair.md §2): лише особистий заклик і лише там, де й загальний тост показали б
    // (не в ⛶ і не на телефоні посеред своєї партії); вимикач гімнів і гімн, що звучить, теж мовчать за нього.
    const quiet = full || (view.kind === 'room' && window.matchMedia('(max-width: 900px)').matches);
    if (personal && inv.ring && inv.ring.url && !quiet) ringFor(inv);
    else if (personal) ping();
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
  /// Дзвінок — перші 4 с уривка з м'яким згасанням, тим самим плеєром, що й гімни; кілька вкладок — дзвенить одна
  /// (та, що на виду, заявляється першою). Не заграв (вимкнено, гімн звучить, браузер не дав) — звичайне «дзінь».
  const RING_SEC = 4;
  async function ringFor(inv) {
    const r = inv.ring;
    const k = inv.roomId + ':' + String(inv.by || '').toLowerCase();
    let mine = true;
    try { mine = await claimAnthem(k, !document.hidden, RING_CLAIM, RING_CLAIM_MS); } catch { /* граємо самі */ }
    if (!mine) return;
    let ok = false;
    // ring: дзвінок поступається гімнові й прокльонові столу (вони його переб'ють), а сам їх не перебиває
    try { ok = !!(anthem && anthem({ url: r.url, title: r.title, emoji: r.emoji }, { from: 0, len: RING_SEC, fade: true, ring: true })); } catch (e) { console.warn('[games] ring', e); }
    if (!ok) ping();
  }

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

  let lobbyT = 0;
  const HGames = {
    ui,

    /// Подія головного хаба padelLive (і відповідь /api/padel/lobby): картка «🍳 Падельня» в «Сьогодні».
    /// Лобі перемальовуємо, лише коли картка справді змінилась — матч шле це щоочка.
    padel(x) {
      const was = padelCard();
      padelLobby = x || null;
      padelAt = Date.now();
      if (shown && view.kind === 'lobby' && padelCard() !== was) renderView();
    },

    /// Гаманець шапки (живий, з події wallet і /api/me); null — ще не прийшов. Слоти беруть його за баланс автомата.
    get wallet() { return wallet; },

    /// Тихий гаманець: поки модуль просить (on), тости гаманця з причиною на prefix не вилазять (шапка оновлюється).
    quietWallet(prefix, on) {
      const n = (quietW.get(prefix) || 0) + (on ? 1 : -1);
      if (n > 0) quietW.set(prefix, n); else { quietW.delete(prefix); releaseWallet(); }
    },

    /// Притриманий виграш (тихий гаманець) — у шапку зараз: автомат доказав оберт.
    releaseWallet() { releaseWallet(); },

    register(mod) {
      if (!mod || !mod.id) { console.warn('[games] register без id'); return; }
      modules[mod.id] = mod;
      failed.delete(mod.id);
      try { rememberModule(mod); } catch (e) { console.warn('[games] gamesMeta', e); }
      for (const id in cards) if (views[id] && views[id].room.game === mod.id) refreshCard(id);
      // Модулі тепер приїжджають по одному у тиші — лобі перемальовуємо раз на пачку, а не на кожен.
      if (!lobbyT) lobbyT = setTimeout(() => { lobbyT = 0; if (shown && root && root.querySelector('.gtiles')) renderView(); }, 150);
      if (shown && view.kind === 'room') { renderRoomHead(view.id); syncArcade(); }   // arcade: модуль міг приїхати пізніше
      notifyTable();   // модуль міг приїхати пізніше за стіл — і сказати, що розмова тут головна (talk: 'main')
    },

    registerPanel(p) {
      if (!p || !p.id || !p.mount) { console.warn('[games] registerPanel без id/mount'); return; }
      try { rememberPanel(); } catch { /* не з модуля гри (tournament.js з index.html) — і не треба */ }
      const i = extraPanels.findIndex((x) => x.id === p.id);
      if (i >= 0) extraPanels[i] = p; else extraPanels.push(p);
      renderShell();
      // Після F5 на вкладці панелі тіло малюється ще до модуля («Панель зникла.») і chromeFor уже
      // стоїть — без скидання renderView вважав би його намальованим і лишив напис назавжди.
      if (view.kind === 'panel' && view.id === 'x:' + p.id) { chromeFor = null; renderView(); }
    },

    has: (id) => !!modules[id],

    /// Панель змінила свою плитку в каталозі (з'явилась, зникла, нове число) — лобі перемальовується, якщо його видно.
    panelTileChanged() { if (shown && view.kind === 'lobby' && root && root.querySelector('.gtiles')) renderView(); },

    /// Лобі на розділі (чип «🎰 Азарт» тощо) — з «Що нового на сайті» (web/sitenews.js).
    openSection(id) {
      if (!GROUPS.some((g) => g.id === id)) return;
      filter = id;
      theme = '';
      try { localStorage.setItem('gamesFilter', filter); localStorage.setItem('gamesTheme', theme); } catch { /* приватне вікно */ }
      if (shown && view.kind === 'lobby' && root) renderView();
      go('#games');
      // Каталог нижче за «Сьогодні» й живі столи — гортаємо до нього, а то розділ відкрився, а видно не його.
      setTimeout(() => {
        const chip = root && root.querySelector('[data-filter="' + id + '"]');
        const sec = chip && chip.closest('section');
        if (sec) sec.scrollIntoView({ block: 'start', behavior: 'smooth' });
      }, 250);
    },

    /// Новий знімок турніру (tournament.js): смужка відліку на столі щойно дограної гри.
    tournamentChanged() { for (const id in cards) refreshCard(id); },
    /// Модуль гри всередині іншого модуля (вечірка, mgprobe) — див. function embed вище.
    embed,

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
      if (o.anthem) anthem = o.anthem;
      if (o.stopAnthem) stopAnthem = o.stopAnthem;
      if (o.fx) fx = o.fx;
      if (o.online) online = o.online;
      if (o.askNick) askNick = o.askNick;
      root = o.root || (o.$ ? o.$('games') : document.getElementById('games'));
      if (root) { root.addEventListener('pointerover', prefetchFrom, { passive: true }); root.addEventListener('focusin', prefetchFrom); }
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
        // Але якщо адресу вже змінили (турнір щойно пересадив за новий стіл, а hashchange ще не дійшов) — не
        // перебиваємо: інакше подія 'rooms' без старого столу, що прийшла слідом за 'tournament', кидала всіх у лобі.
        if (shown && view.kind === 'room' && view.id && !views[view.id] && !pinned.has(view.id)) {
          let to = null;
          try { to = location.hash.startsWith('#games/room/') ? decodeURIComponent(location.hash.slice(12)) : null; } catch { /* крива адреса — у лобі, як і раніше */ }
          if (!to || to === view.id) go('#games');
          return;
        }
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
        else if (cards[id]) {
          refreshCard(id);
          // Шапку столу малювали ще до першого виду (соло, вхід за посиланням) — там лишалось «Стіл» замість назви гри,
          // а на ≤480 назва тепер лише в шапці. Перемальовуємо на першому виді й коли міняється те, що вона показує.
          if (view.kind === 'room' && view.id === id
            && (!old || old.room.status !== rv.room.status || old.room.watchers !== rv.room.watchers)) renderRoomHead(id);
        }
        else if (view.kind === 'lobby') renderView();      // «твій хід» на резюме в лобі
        syncWatch();
        notifyTable();                                      // сів, встав, партія почалась — балачці столу це важливо
        checkTurns();                                       // «🎲 Твій хід» у заголовку вкладки й на «Іграх»
        anthemRound(rv.room);                               // «Ще раз» — гімн минулої партії замовкає
      });
      c.on('tableReact', flyReact);
      c.on('tableBets', (x) => { if (window.HTableBets && x) HTableBets.changed(x.room); });
      c.on('anthem', onAnthem);
      c.on('curse', onCurse);
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
        // Виграш автомата шапка показує, коли барабани спинились (releaseWallet зі слота), а не за 0,3 с після «крутити» —
        // інакше результат видно наперед. Списання ставки — одразу.
        const hold = w.delta > 0 && [...quietW.keys()].some((p) => String(w.reason || '').startsWith(p));
        if (hold) { clearTimeout(walletHold); walletHold = setTimeout(releaseWallet, 20000); }
        else if (!walletHold) paintWallet();
        // сервер уже присилає готовий рядок «+5 черепків: перемога — Хрестики-нолики»;
        // своє число ліпимо лише тоді, коли тексту нема, інакше виходило «+5 🏺 +5 черепків: …».
        // Прихід — «Лови +5 …» (якщо сервер сам уже не сказав «Лови»), витрата — як є.
        const line = w.text || (w.delta > 0 ? '+' : '') + w.delta;
        document.dispatchEvent(new CustomEvent('hgames:wallet', { detail: w }));   // слоти тримають свій баланс за ним
        const hush = [...quietW.keys()].some((p) => String(w.reason || '').startsWith(p));   // автомат сам показує ставку й виграш
        if (w.delta && !hush) toast('🏺 ' + (w.delta > 0 && !/^лови/i.test(line) ? 'Лови ' + line : line), w.delta > 0 ? 'ok' : '');
      });
      c.on('achievement', (a) => {
        if (!a) return;
        const title = String(a.title || a.key || '');
        longToast('<span class="gemo">' + esc(a.icon || '🏅') + '</span> ' + (/^овва/i.test(title) ? '' : 'Овва! ') + '<b>' + esc(title) + '</b>'
          + (a.reward ? ' — лови +' + a.reward + ' 🏺' : '') + (a.text ? '<br><span class="muted small">' + esc(a.text) + '</span>' : ''), 6000);
      });
      c.on('toast', (t) => { if (t && t.text) toast(t.text, t.kind || ''); });
      // Дзвоник цеху Гончарного кола: другові щось надіслали — відкрите коло саме спитає пошту (clicker-guild.js).
      c.on('clkMail', (m) => document.dispatchEvent(new CustomEvent('hgames:clkMail', { detail: m || {} })));
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
      anthemPage();
      syncShown();
      setFull(false);
      document.body.classList.remove('g-room', 'g-arcade');
      syncWatch();
      checkTurns();
    },

    /// Каталог і модулі ігор (іконки, назви) — для «Хто скільки» й профілів: проміс, що каталог уже є.
    /// Модулі тепер довантажуються в тиші (п. 241): чекаємо їх (заради справжніх іконок при першому заході) щонайбільше 1,5 с.
    ready: () => Promise.race([ensureCatalog() || Promise.resolve(),
      ensureNames().then(() => new Promise((r) => setTimeout(r, 1500)))]),
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
