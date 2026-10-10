/*
  Гончарне коло. Соло-клікер: тиснеш на коло — ліпиш глеки, купуєш верстати й віхи, ловиш розписні глеки та глеки,
  що падають з полиці, збираєш розписи, обпалюєш майстерню за клейма майстра, міняєш глеки на черепки.

  Правила рахує сервер (Impl/Clicker.cs). Клієнт понад малювання робить рівно п'ять речей:
  1) батчить кліки — збирає відбитки справжніх натисків і шле Act('spin', { c: [[dt, press, x, y, src], …] }) раз на
     700 мс, а не двадцять разів за секунду. Рахуються лише isTrusted-натиски на коло (pointerdown → pointerup) і
     пробіл без автоповтору (keydown → keyup): el.click() чи dispatchEvent зі скрипта кліком не стають, а сервер за
     відбитками впізнає мишачий софт (Impl/ClickerGuard.cs);
  2) доліковує лічильник між подіями 'room' — за view.baseSecond і ярмарком, зі стелею офлайну, як на сервері.
     Простій беремо серверний (view.now − view.lastSync) і додаємо лише те, що натікало ВІД отримання виду, —
     так збитий годинник у гравця не малює неіснуючих глеків. Прийшов новий вид — беремо його число, а не своє;
  3) веде той самий рахунок розгону, що й сервер (view.heat спадає за heatTau, множник — від heatFull і momentumMax),
     щоб «+N» над колом і лічильник обіцяли те, що сервер справді дорахує;
  4) показує розписний глек у його вікні (view.golden) і глек з полиці (view.fall) у його три секунди польоту; коли
     той чи той утік/розбився — питає наступний розклад Act('look');
  5) показує Око майстра (view.guard): інструкцію з кнопкою «Показати полицю», за нею полицю-картинку, де треба
     торкнутись усіх глечиків (Act('answer', { taps })), або паузу кола з відліком. Доки кнопку не натиснуто,
     торкання картинки не рахуються (інакше швидкі кліки по колу проклацували полиці наосліп). Де глечики —
     клієнт не знає: це знає лише сервер.

  Вид (Impl/Clicker.cs): { pots, total, perClick, clickBase, perSecond, baseSecond,
    upgrades: { key: { level, price, name, desc, max, kind, gain, growth, marks, open } }, marks: [...],
    canSellToday, soldToday, cap, rate, lastSync, now, offlineHours, golden: { at, until, x, y }, caught,
    fair: { until, mult, span, held }, inspire: { until, mult, share, span, held }, allMult, stamps, stampsFree, stampsReady, stampsExtra,
    nextStampAt, stampBonus, stampSoft, stampMult, stampIron, science: { top, who, share, cap, readyAt },
    stampCap, firings, secrets: [...], styles: [...], wear,
    heat, heatFull, heatTau, momentum, momentumMax, fall: { at, until, x, streak, gain, bonus }, grabbed,
    lucky, starWish, news: null | "v9.2", newsSeen: null | "<що бачив востаннє>",
    events: { cat: { at, until, dir }, star: { at, until, x, y }, wind: { at, until, mult }, petted },
    guard: null | { serial, count, png, width, height, misses, maxMisses, lockUntil, why, pays, gain },
    titles: { mine, earned, show, badges, values, progress, secrets, gift } — звання (clicker-titles.js) }.
  Дії: spin { c }, buy { key, n }, mark { key }, sell { pots }, catch, grab, look, fire, secret { key }, paint { key },
    wear { key }, answer { taps: [[x, y], …] }, pet, wish, news { v }.
*/
(() => {
  /// Натиск на джойстику — теж людина, просто не мишею: шар пада (web/static/pad.js) ставить
  /// своїм подіям позначку, а ui.human() її впізнає. Скрізь, де Око майстра питало `isTrusted`,
  /// тепер стоїть human() — скрипт зі сторони від цього ближче не став.
  const human = HGames.ui.human;

  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<ellipse cx="8" cy="12.2" rx="6.2" ry="2.3" fill="none" stroke="var(--muted)" stroke-width="1.3"/>'
    + '<path d="M5.6 10.8V7.4c0-1 .8-1.3.8-2.1V3.6h3.2v1.7c0 .8.8 1.1.8 2.1v3.4z" fill="var(--clay)"/></svg>';

  const BATCH_MS = 700;                   // як часто злітає накопичена пачка кліків
  const MAX_BATCH = 12;                   // рівно стільки сервер приймає за секунду
  const MAX_BUY = 1000;                   // стільки рівнів сервер купує одним натиском
  const STAMP_UNIT = 1e9;                 // клейма = ⌊√(усього / мільярд)⌋, як на сервері
  const STAMPS_PER_CAP = 10;              // +1 черепок до денної стелі за кожні 10 клейм
  const CATCH_GRACE_MS = 2000;            // той самий запас, що й на сервері: після нього глек уже не спіймати
  const BOARD_MS = 60 * 1000;             // як часто перепитуємо таблицю «Гончарне коло» для рядка про суперника
  /// Кнопки полиць і прилавка, таймери бонусів, прогрес клейм — чотири рази на секунду й одним кадром (v10 §10): кожна
  /// зміна тексту поза сценою — це розкладка сторінки, і зміни одного такту мусять злитися в одну.
  const SLOW_MS = 250;
  const RIVAL_MS = 1000;                  // «суперник попереду на …» — не частіше, ніж так (різниця міняється щотакту)
  const HOLD_MS = 3000;                   // тримали довше — це вже не клік
  const RING = 295.3;                     // довжина кільця розгону (2π · 47)
  const EVENT_GAP_MS = 2 * 60 * 1000;     // довший простій — гончаря не було: сервер випадковостей йому не рахує
  const CLOCK_KEEP_MS = 60 * 1000;        // серверне «зараз» — від найменш запізнілого виду за стільки (див. update)
  const NEWS_VERSION = 'v11.2';           // яку версію «Що нового» знає цей клієнт (те саме, що Clicker.NewsVersion)
  const PV = 10;                          // версія протоколу (Clicker.ProtocolVersion): ми вміємо доповнювати худий вид
  /// Чим клацнули: ті самі номери, що й ClickerGuard.Source на сервері.
  const SRC = { mouse: 0, touch: 1, pen: 2, key: 3 };

  /// Розділи Майстерні: колишні вкладки, що тепер згортаються всередині неї. `open` — коли розділ варто
  /// розгорнути самому (доки гравець не вирішив інакше й не лишив по собі SEC_KEY<key>). F10 (08.10): куплене
  /// назавжди (глина, знаряддя, розписи) на телефоні займало тисячі пікселів між верстатами, тож розділ розгорнутий,
  /// лише поки там є що купити, а так — згорнутий рядок-підсумок. Ключ новий: старий clk.sec.* ставило й саме
  /// розгортання (подія toggle приходить уже після прапорця _auto), тож відрізнити вибір гравця від автомата не можна.
  const SEC_KEY = 'clk.sec2.';
  const SECTIONS = [
    { key: 'house', title: '🏠 Хата', open: (st) => st.tools.some((t) => !t.owned) || st.clays.some((c) => !c.owned && c.price > 0) },
    { key: 'styles', title: '🎨 Розписи', open: (st) => st.styleList.some((x) => !x.owned) },
    { key: 'orders', title: '🐴 Вклад купцям', open: (st) => st.taken.length > 0 },
  ];

  // ---------- частини (сьоме оновлення, docs/games/specs/clicker-v7.md §3) ----------

  /// Ремесло, жива хата, горно, альбом, ярмарок і цех живуть в окремих файлах clicker-<id>.js (+ .css): інакше
  /// цей файл виріс би втричі, а паралельні роботи бились би в одному місці. Частина кличе HClicker.part({...}) і
  /// дістає ті самі st, що й ядро, плюс спільний api. Каркас ігор знає лише clicker.js — частини вантажимо самі.
  const PART_IDS = ['craft', 'scene', 'kiln', 'album', 'fair', 'guild', 'titles', 'guests', 'toloka'];
  const H = window.HClicker = window.HClicker || { parts: [], mounted: new Set(), loaded: false };

  /// Одна частина впала — решта гри живе далі: помилку в консоль, а не білу картку.
  function callPart(p, hook, ...args) {
    if (typeof p[hook] !== 'function') return;
    try { p[hook](...args); } catch (e) { console.error('[clicker:' + p.id + '] ' + hook, e); }
  }

  function mountPart(st, p) {
    if (st.parts.has(p.id)) return;
    st.parts.add(p.id);
    callPart(p, 'mount', st, H.api);
    // Частина догнала вже відкриту картку: віддати їй останній вид і перемалювати картку — ремесло й хата дають
    // іншим частинам силуети й значки, і без цього гравець без дій так і дивився б на заглушки.
    // Недоповнений худий вид частинам не віддаємо — назв розписів і верстатів у ньому ще нема.
    if (st.lastView && (st.shopCat || Object.values(st.lastView.upgrades || {}).some((u) => u && u.name))) {
      callPart(p, 'update', st, st.lastView, H.api);
      refreshCard(st);
    }
  }

  /// Перемалювати картку з останнім видом: скинути підписи swap() і прогнати update ядра й частин. Раз на пачку запізнілих.
  function refreshCard(st) {
    if (st.refreshT) return;
    st.refreshT = setTimeout(() => {
      st.refreshT = 0;
      if (!st.el || !st.ctx || !st.lastView || !st.root) return;
      for (const el of st.el.querySelectorAll('*')) if (el._sig !== undefined) el._sig = null;
      if (st.jugBox) st.jugBox._wear = null;
      // Вид той самий — update сам по собі його пропустив би (див. update): тут перемалювати треба.
      st.again = true;
      MOD.update(st.root, st.ctx);
    }, 60);
  }

  H.part = (p) => {
    if (!p || !p.id || H.parts.some((x) => x.id === p.id)) return;
    H.parts.push(p);
    H.parts.sort((a, b) => (a.order || 50) - (b.order || 50));
    for (const st of H.mounted) if (st.el) mountPart(st, p);
  };

  if (!H.loaded) {
    H.loaded = true;
    for (const id of PART_IDS) {
      if (!document.querySelector('link[data-clk-part="' + id + '"]')) {
        const l = document.createElement('link');
        l.rel = 'stylesheet';
        l.href = '/games/clicker-' + id + '.css';
        l.dataset.clkPart = id;
        document.head.appendChild(l);
      }
      const sc = document.createElement('script');
      sc.async = false;                // виконуються в порядку PART_IDS: ремесло й хата раніше за тих, хто малює їхнім api
      sc.src = '/games/clicker-' + id + '.js';
      sc.onerror = () => console.warn('[clicker] частина ' + id + ' не завантажилась');
      document.head.appendChild(sc);
    }
  }

  // ---------- числа й слова ----------

  /// Форматери Intl — дорогі в створенні (toLocaleString будує новий щоразу), а числа малюються щокадру: кешуємо
  /// за кількістю знаків після коми (десяте оновлення, docs/games/specs/clicker-v10.md §10).
  const NF = [];
  const nf = (digits) => NF[digits] || (NF[digits] = new Intl.NumberFormat('uk-UA', { maximumFractionDigits: digits }));
  const num = (n) => nf(0).format(Math.round(n));
  /// «0,5» замість «0.5»: десяткова кома в нас усюди українська.
  const dec = (n) => nf(1).format(Math.round(n * 10) / 10);
  const plural = (n, one, few, many) => {
    n = Math.floor(Math.abs(n));
    return n % 100 >= 11 && n % 100 <= 14 ? many : n % 10 === 1 ? one : n % 10 >= 2 && n % 10 <= 4 ? few : many;
  };
  const shards = (n) => plural(n, 'черепок', 'черепки', 'черепків');
  const stampsWord = (n) => plural(n, 'клеймо', 'клейма', 'клейм');
  /// Скільки «повних» клейм важать n клейм — та сама крива, що Clicker.StampWeight на сервері: до тисячі (soft)
  /// усі, далі корінь — кожне нове клеймо важить дедалі менше (на 4000 — половину, на 16 000 — чверть), а після
  /// коліна (knee, 4 млн, десяте оновлення) — логарифм: удесятеро більше клейм додають ту саму вагу.
  function stampWeight(n, soft, knee) {
    if (n <= soft) return Math.max(0, n);
    if (!knee || n <= knee) return soft * (2 * Math.sqrt(n / soft) - 1);
    return soft * (2 * Math.sqrt(knee / soft) - 1) + Math.sqrt(soft * knee) * Math.log(n / knee);
  }
  /// Бонус клейм «до всього» у відсотках для n клейм.
  const stampPct = (st, n) => st.stampBonus * stampWeight(n, st.stampSoft, st.stampKnee) * 100;
  /// «2,23 млн клейм», «211 клейм»: після скорочення слово узгоджується з «млн».
  const stampsShort = (n) => count(n) + ' ' + (Math.abs(n) >= 1e6 ? 'клейм' : stampsWord(n));
  const potsWord = (n) => (n % 1 ? 'глека' : plural(n, 'глек', 'глеки', 'глеків'));

  /// Десяте оновлення: від квадрильйона глеків суми показуються в гривнях (1 ₴ = 10¹⁵ глеків), від 10²⁷ — у червоних
  /// золотих (1 золотий = 10¹² ₴). Це лише показ — гаманець один. Ті самі пороги й слова, що Clicker.Short на сервері.
  const HRYVNIA = 1e15;
  const GOLD = 1e27;
  /// Назви великих чисел — ті самі, що на сервері (Impl/Clicker.cs, BigNames): лише знайомі слова. Далі за трильйоном
  /// гривні й золоті, а за трильйонами одиниці — «1,2e15».
  const BIG = ['млн', 'млрд', 'трлн'];
  /// «1,2e36»: степінь із українською комою, як у сервера («0.#e0»).
  function expo(n) {
    let e = Math.floor(Math.log10(Math.abs(n)));
    let m = Math.round((n / Math.pow(10, e)) * 10) / 10;
    if (Math.abs(m) >= 10) { m /= 10; e += 1; }            // 9,99e36 — це 1e37, а не «10e36»
    return nf(1).format(m) + 'e' + e;
  }
  /// Відтинаємо, а не округлюємо (як сервер): «999,999 трлн» не стає «1000 трлн». Запас у трильйонну частку —
  /// від похибки double (999·10²⁴ / 10¹⁵ = 998,99999…).
  const cut = (v, digits) => Math.trunc(v * Math.pow(10, digits) * (1 + 1e-12)) / Math.pow(10, digits);
  /// Число без одиниці: до мільйона — повне, далі «1,09 млн» … «999 трлн», а за трильйонами — «1,2e15».
  function count(n) {
    if (!Number.isFinite(n)) return '∞';
    // Від тисячі дробова частина — шум («14 091,8 ₴»): лише цілі, відтяті.
    if (Math.abs(n) < 1e6) return n % 1 && Math.abs(n) < 1000 ? dec(n) : num(cut(n, 0));
    const i = Math.floor(Math.log10(Math.abs(n)) / 3) - 2;
    if (i >= BIG.length) return expo(n);
    const v = n / Math.pow(1000, i + 2);
    const digits = Math.abs(v) < 10 ? 2 : Math.abs(v) < 100 ? 1 : 0;
    return nf(digits).format(cut(v, digits)) + ' ' + BIG[i];
  }
  /// Те саме на знак коротше — для бейджа вкладки: «37 млн», «3,7 млн». На телефоні бейдж має ~58 px (09.10).
  function few(n) {
    if (!Number.isFinite(n) || Math.abs(n) < 1e6) return count(n);
    const i = Math.floor(Math.log10(Math.abs(n)) / 3) - 2;
    if (i >= BIG.length) return expo(n);
    const v = n / Math.pow(1000, i + 2);
    const digits = Math.abs(v) < 10 ? 1 : 0;
    return nf(digits).format(cut(v, digits)) + ' ' + BIG[i];
  }
  /// Золотий / золоті / золотих; дробове — «золотого». Після скорочення («1,2 млн») — «золотих», як і глеки.
  const goldWord = (g) => {
    if (!Number.isFinite(g) || Math.abs(g) >= 1e6) return 'золотих';
    // Слово — за тим, що видно: до тисячі — один знак після коми, від тисячі — ціле відтяте.
    const shown = Math.abs(g) >= 1000 ? cut(g, 0) : Math.round(g * 10) / 10;
    return shown % 1 ? 'золотого' : plural(shown, 'золотий', 'золоті', 'золотих');
  };
  /// Сума глеків коротко: до квадрильйона — число («5,5 трлн»), далі «5,93 млн ₴», від 10²⁷ — «60 000 золотих».
  /// Одиниця — частина тексту, тож «ціна: short(x)» читається правильно без слова поруч.
  function short(n) {
    if (!Number.isFinite(n)) return '∞';
    const a = Math.abs(n);
    if (a < HRYVNIA) return count(n);
    if (a < GOLD) return count(n / HRYVNIA) + ' ₴';
    const g = n / GOLD;
    return count(g) + ' ' + goldWord(g);
  }
  /// «1,47 млн глеків», а не «1,47 млн глеки»: після скорочення слово узгоджується з «млн», а не з останньою цифрою.
  /// У гривнях і золотих слово вже є.
  const potsShort = (n) => (Math.abs(n) >= HRYVNIA ? short(n)
    : short(n) + ' ' + (Math.abs(n) >= 1e6 ? 'глеків' : potsWord(n)));
  /// Одиниця для великого лічильника: у чому зараз рахуємо і на що ділити.
  const unit = (n) => (n >= GOLD ? { div: GOLD, word: 'золотих', key: 'gold' }
    : n >= HRYVNIA ? { div: HRYVNIA, word: '₴', key: 'hryvnia' } : { div: 1, word: 'глеків', key: 'pots' });
  /// Великий лічильник (без одиниці — її пише мітка поруч, unit()): до трильйона глеків кожна цифра (видно, як коло
  /// крутиться; «3 млрд» стояло б годинами), до квадрильйона — коротко з трьома знаками. У гривнях і золотих: до тисячі —
  /// два знаки після коми (щоб число жило), до мільярда — усі цифри, далі — з трьома знаками й назвою.
  function big(n) {
    if (!Number.isFinite(n)) return '∞';
    const u = unit(n);
    const v = n / u.div;
    if (u.div === 1 && v < 1e12) return num(v);
    if (u.div > 1 && v < 1000) return nf(2).format(cut(v, 2));
    if (u.div > 1 && v < 1e9) return num(cut(v, 0));
    const i = Math.floor(Math.log10(v) / 3) - 2;
    if (i >= BIG.length) return expo(v);
    return nf(3).format(cut(v / Math.pow(1000, i + 2), 3)) + ' ' + BIG[i];
  }

  /// «за 40 с», «за 12 хв», «за 3 год», «за 2 дні».
  function span(sec) {
    if (!Number.isFinite(sec) || sec <= 0) return '';
    if (sec < 90) return Math.ceil(sec) + ' с';
    if (sec < 90 * 60) return Math.round(sec / 60) + ' хв';
    if (sec < 36 * 3600) return dec(sec / 3600) + ' год';
    const d = Math.round(sec / 86400);
    // Окупність верстата в пізній грі — це мільярди днів: цифрами їх ніхто не читає, та й JS написав би «1e+26».
    // Після скорочення слово узгоджується з «млн», а не з останньою цифрою: «11,5 млн днів», не «дні». Дні — не гроші,
    // тож count, а не short: інакше «5 ₴ днів».
    return d >= 1e6 ? count(d) + ' днів' : num(d) + ' ' + plural(d, 'день', 'дні', 'днів');
  }

  /// Довгий абзац у значок ⓘ: прочитати можна, займати екран — не мусить. Тим самим користуються частини.
  const info = (text) => '<details class="clk-info"><summary>і</summary><p>' + text + '</p></details>';

  const storeGet = (k, dflt) => { try { return localStorage.getItem(k) || dflt; } catch { return dflt; } };
  const storeSet = (k, v) => { try { localStorage.setItem(k, v); } catch { /* приватне вікно — пам'ятати нема де */ } };

  // ---------- глеки й розписи ----------

  /// Силует глечика в квадраті 100×100: вінця, тонка шийка, пузо, дно. Центр — x = 50.
  const JUG = 'M43 25h14v2.5c0 1.6-1.4 2.2-1.4 4 0 2.6 9.4 5.6 9.4 16.5 0 8.3-4.6 13.8-7.3 16H42.3'
    + 'C39.6 61.8 35 56.3 35 48c0-10.9 9.4-13.9 9.4-16.5 0-1.8-1.4-2.4-1.4-4z';

  const dots = (y, from, to, step, r, fill) => {
    let s = '';
    for (let x = from; x <= to + 0.01; x += step) s += '<circle cx="' + x.toFixed(1) + '" cy="' + y + '" r="' + r + '"/>';
    return '<g fill="' + fill + '">' + s + '</g>';
  };
  const flower = (red, heart, leaf) =>
    '<g fill="' + red + '"><circle cx="50" cy="44.6" r="2.4"/><circle cx="46.2" cy="47.4" r="2.4"/>'
    + '<circle cx="53.8" cy="47.4" r="2.4"/><circle cx="47.6" cy="51.6" r="2.4"/><circle cx="52.4" cy="51.6" r="2.4"/></g>'
    + '<circle cx="50" cy="48.6" r="1.7" fill="' + heart + '"/>'
    + '<path d="M50 54.5c0 3-2 5.5-5 6.5M50 54.5c0 3 2 5.5 5 6.5" stroke="' + leaf + '" stroke-width="1.2" fill="none"/>'
    + '<path d="M40.5 57c1.5-3 4-3.6 5.2-2.5-1 1.6-3.1 3-5.2 2.5zM59.5 57c-1.5-3-4-3.6-5.2-2.5 1 1.6 3.1 3 5.2 2.5z" fill="' + leaf + '"/>';

  /// Розписи з реальних осередків: колір тіла глека й орнамент поверх нього (обрізається по силуету).
  const STYLE = {
    '': { body: 'var(--clay)', decor: '<path d="M44 29.5h12" stroke="rgba(0,0,0,.18)" stroke-width="1"/>' },
    gavarets: {
      body: '#2b2a2f',
      decor: '<path d="M35.5 41l4.3 3.5 4.3-3.5 4.3 3.5 4.3-3.5 4.3 3.5 4.3-3.5 4.3 3.5" fill="none" stroke="#a19eab" stroke-width="1.1"/>'
        + '<path d="M35 53h30M35 55.6h30" stroke="#74717d" stroke-width=".8"/><path d="M44 29.5h12" stroke="#7d7a86" stroke-width=".9"/>',
    },
    vasylkiv: {
      body: '#f2e9d6',
      decor: '<path d="M35 38.5h30M35 60h30" stroke="#2f5fa8" stroke-width="2"/>'
        + '<g fill="#2f5fa8"><ellipse cx="50" cy="44" rx="1.8" ry="3"/><ellipse cx="50" cy="54" rx="1.8" ry="3"/>'
        + '<ellipse cx="45" cy="49" rx="3" ry="1.8"/><ellipse cx="55" cy="49" rx="3" ry="1.8"/></g>'
        + '<circle cx="50" cy="49" r="2.4" fill="#d99a2b"/>'
        + '<path d="M40.5 55c2-3 4-3 5-6M59.5 55c-2-3-4-3-5-6" stroke="#4f8a3a" stroke-width="1.1" fill="none"/>'
        + '<path d="M44 29.5h12" stroke="#d99a2b" stroke-width="1.2"/>',
    },
    bubnivka: {
      body: '#8b3a22',
      decor: dots(40.5, 37, 63, 3.25, 1.05, '#f4ead6')
        + '<path d="M35 49q3.75-4.2 7.5 0t7.5 0 7.5 0 7.5 0" stroke="#63a543" stroke-width="1.7" fill="none"/>'
        + dots(55.5, 38.5, 61.5, 4.6, 1.1, '#efc13a') + '<path d="M44 29.5h12" stroke="#f4ead6" stroke-width="1"/>',
    },
    kosiv: {
      body: '#ecdfc2',
      decor: '<path d="M35 39h30M35 59.5h30" stroke="#6b3b1b" stroke-width="1.4"/>'
        + '<path d="M35 42.4q3-2.2 6 0t6 0 6 0 6 0 6 0" stroke="#d6a21e" stroke-width="1.3" fill="none"/>'
        + '<path d="M37.5 57l3.2-11 3.2 11zM46.8 57l3.2-11 3.2 11zM56.1 57l3.2-11 3.2 11z" fill="#3f7d3a" stroke="#6b3b1b" stroke-width=".7"/>'
        + '<path d="M44 29.5h12" stroke="#3f7d3a" stroke-width="1.1"/>',
    },
    opishnia: {
      body: '#c56b35',
      decor: '<path d="M50 43v14" stroke="#f6efe2" stroke-width="1.2"/><ellipse cx="50" cy="41.5" rx="2" ry="3.1" fill="#3e7c3a"/>'
        + '<path d="M41 51.5c0-4.2 5.4-4.2 5.4 0 0 2.4-3.2 2.8-3.2.4M59 51.5c0-4.2-5.4-4.2-5.4 0 0 2.4 3.2 2.8 3.2.4" stroke="#f6efe2" stroke-width="1.3" fill="none"/>'
        + '<g fill="#4a2513"><circle cx="39.5" cy="44.5" r=".95"/><circle cx="60.5" cy="44.5" r=".95"/><circle cx="50" cy="59.5" r=".95"/></g>'
        + '<path d="M35 61.5h30" stroke="#f6efe2" stroke-width="1.1" stroke-dasharray="2 1.5"/>',
    },
    mezhyhirya: {
      body: '#f6f5ef',
      decor: '<path d="M35 37.5h30M35 61h30" stroke="#2b4f9e" stroke-width="2.2"/><path d="M35 40.6h30M35 58h30" stroke="#2b4f9e" stroke-width=".7"/>'
        + '<path d="M42.5 52.5c3.2-6.4 11.8-6.4 15 0-3.2-2-11.8-2-15 0z" fill="#2b4f9e"/><circle cx="50" cy="45.6" r="1.7" fill="#2b4f9e"/>',
    },
    petrykivka: {
      body: '#1e1c1d',
      decor: flower('#d7372b', '#f2c230', '#4c9a3f')
        + '<g fill="#f2c230"><circle cx="40" cy="42.4" r="1"/><circle cx="60" cy="42.4" r="1"/><circle cx="42.2" cy="39.8" r=".8"/><circle cx="57.8" cy="39.8" r=".8"/></g>',
    },
    trypillia: {
      body: '#d9884a',
      decor: '<path d="M35 36.5h30M35 62h30" stroke="#2a1a12" stroke-width="1.6"/><path d="M35 39.8h30M35 58.7h30" stroke="#f1e4cc" stroke-width=".8"/>'
        + '<path d="M37.6 49.4c0-5.2 7.2-5.2 7.2 0 0 3-4 3.4-4 .8M62.4 48.6c0 5.2-7.2 5.2-7.2 0 0-3 4-3.4 4-.8" stroke="#2a1a12" stroke-width="1.5" fill="none"/>'
        + '<path d="M44.8 49.4c2.6-4.4 7.8 3.6 10.4-.8" stroke="#2a1a12" stroke-width="1.5" fill="none"/>',
    },
    // Розписи світу (одинадцяте оновлення, пакет C): та сама сітка — пояси 36…62, середина на 50.
    /// Цзиндечжень: кобальт на білій порцеляні — лотос у кучерях пагонів між подвійними поясами.
    jingdezhen: {
      body: '#f3f5f8',
      decor: '<path d="M35 37.8h30M35 61.2h30" stroke="#1f3f9a" stroke-width="1.6"/><path d="M35 40.1h30M35 58.9h30" stroke="#1f3f9a" stroke-width=".5"/>'
        + '<circle cx="50" cy="50" r="7.2" fill="#9fb4de" opacity=".4"/>'
        + '<path d="M50 43.6c-2.5 2.7-2.5 6.1 0 8.6 2.5-2.5 2.5-5.9 0-8.6zM50 52.2c-3.1-1.1-6.2-.6-7.8 1.7 2.7 1.4 5.8 1 7.8-1.7zM50 52.2c3.1-1.1 6.2-.6 7.8 1.7-2.7 1.4-5.8 1-7.8-1.7z" fill="#1f3f9a"/>'
        + '<path d="M37.4 49.6c1.8-3.6 5.6-3.8 6.2-.4-.2 1.8-2.3 2.1-2.7.6M62.6 49.6c-1.8-3.6-5.6-3.8-6.2-.4.2 1.8 2.3 2.1 2.7.6" stroke="#1f3f9a" stroke-width=".9" fill="none"/>'
        + '<path d="M44 29.5h12" stroke="#1f3f9a" stroke-width="1"/>',
    },
    /// Ізнік: біле тіло, коралово-червоний тюльпан на бірюзовому пагоні, кобальтові пояси.
    iznik: {
      body: '#fbf8f1',
      decor: '<path d="M35 38h30M35 61h30" stroke="#1d4e9e" stroke-width="1.8"/><path d="M35 40.2h30M35 58.8h30" stroke="#2aa198" stroke-width=".8"/>'
        + '<path d="M50 58.2V47.5" stroke="#2aa198" stroke-width="1.1"/>'
        + '<path d="M50 57.6c-4.3-.9-7.3-4-8.3-8.3 3.3.7 6.3 3.5 8.3 8.3zM50 55.4c4-1 6.6-3.7 7.6-7.7-3.1.8-5.7 3.3-7.6 7.7z" fill="#2aa198"/>'
        + '<path d="M46.4 46.8c0-3 1.2-5 1.9-6.1.5 1.6 1.1 2.7 1.7 2.7s1.2-1.1 1.7-2.7c.7 1.1 1.9 3.1 1.9 6.1 0 2-1.6 3.1-3.6 3.1s-3.6-1.1-3.6-3.1z" fill="#c8372d"/>'
        + '<g fill="#c8372d"><circle cx="39.4" cy="45" r="1.3"/><circle cx="60.6" cy="45" r="1.3"/></g>'
        + '<g fill="#1d4e9e"><circle cx="39.4" cy="54.6" r=".9"/><circle cx="60.6" cy="54.6" r=".9"/></g>'
        + '<path d="M44 29.5h12" stroke="#1d4e9e" stroke-width="1"/>',
    },
    /// Делфт: синім по олов'яній поливі — вітряк над берегом, пташки й рамка.
    delft: {
      body: '#eef2f6',
      decor: '<path d="M35 37.6h30M35 61.6h30" stroke="#2c5aa0" stroke-width="1.5"/><path d="M35 39.8h30M35 59.4h30" stroke="#2c5aa0" stroke-width=".5" stroke-dasharray="1.4 1"/>'
        + '<path d="M36 56.6q7-2 14 0t14 0" stroke="#2c5aa0" stroke-width=".9" fill="none"/>'
        + '<path d="M47.6 56.4l.9-7.4h3l.9 7.4z" fill="#2c5aa0"/>'
        + '<path d="M50 48.6l-5.2-5.2M50 48.6l5.2-5.2M50 48.6l-5.2 5.2M50 48.6l5.2 5.2" stroke="#2c5aa0" stroke-width="1.5" stroke-linecap="round"/>'
        + '<circle cx="50" cy="48.6" r="1" fill="#eef2f6"/>'
        + '<path d="M39 44.4l1.2 1 1.2-1M58.6 42.6l1.1.9 1.1-.9" stroke="#2c5aa0" stroke-width=".6" fill="none"/>'
        + '<path d="M44 29.5h12" stroke="#2c5aa0" stroke-width="1"/>',
    },
    /// Майсен: біла тверда порцеляна, сині схрещені мечі й квіткова гілочка, золото на плечі.
    meissen: {
      body: '#fdfdfa',
      decor: '<path d="M35 38.2h30" stroke="#c9a13a" stroke-width="1.1"/><path d="M35 61.4h30" stroke="#c9a13a" stroke-width=".8"/>'
        + '<path d="M45.2 55.6l9.6-10.2M54.8 55.6l-9.6-10.2" stroke="#2745a3" stroke-width="1.3" stroke-linecap="round"/>'
        + '<path d="M45.9 48.5l2.4-2.2M51.7 46.3l2.4 2.2" stroke="#2745a3" stroke-width="1.1" stroke-linecap="round"/>'
        + '<g fill="#d9607a"><circle cx="40" cy="44.8" r="1.7"/><circle cx="60.2" cy="57" r="1.3"/></g>'
        + '<g fill="#f7c3cf"><circle cx="40" cy="44.8" r=".7"/></g><circle cx="61.4" cy="44.2" r="1.1" fill="#3f63c4"/>'
        + '<path d="M41.4 46.2c1.6.8 2.3 2.3 2 3.8M58.8 45.4c-1.2.9-1.6 2.4-1.2 3.6" stroke="#5a8a3a" stroke-width=".7" fill="none"/>'
        + '<path d="M44 29.5h12" stroke="#c9a13a" stroke-width="1.1"/>',
    },
    /// Севр: густа «королівська блакить» і золото — білий медальйон із трояндою в золотій рамці.
    sevres: {
      body: '#1f3f91',
      decor: '<path d="M35 37.8h30M35 61.4h30" stroke="#d4af37" stroke-width="1.5"/>'
        + '<path d="M35 40.8q3.75 2.4 7.5 0t7.5 0 7.5 0 7.5 0" stroke="#d4af37" stroke-width=".8" fill="none"/>'
        + '<ellipse cx="50" cy="50.4" rx="6.6" ry="7.8" fill="#fbf7ee" stroke="#d4af37" stroke-width="1.3"/>'
        + '<circle cx="50" cy="49.6" r="2" fill="#d9607a"/><circle cx="50" cy="49.6" r=".8" fill="#f7c3cf"/>'
        + '<path d="M50 51.6v2.8M48 53.4c1 .1 1.6-.4 2-1.2" stroke="#5a8a3a" stroke-width=".7" fill="none"/>'
        + '<g fill="#d4af37"><circle cx="39.6" cy="50.4" r=".9"/><circle cx="60.4" cy="50.4" r=".9"/><circle cx="39.6" cy="56" r=".6"/><circle cx="60.4" cy="56" r=".6"/></g>'
        + '<path d="M44 29.5h12" stroke="#d4af37" stroke-width="1.1"/>',
    },
    /// Раку: темна полива з кракелюром і мідним відблиском, який лишає вогонь і тирса.
    raku: {
      body: '#2a2522',
      decor: '<ellipse cx="45" cy="47" rx="8" ry="6" fill="#b8733a" opacity=".55"/><ellipse cx="56" cy="55" rx="7" ry="4.5" fill="#3f8f84" opacity=".4"/>'
        + '<ellipse cx="44" cy="45.4" rx="3.4" ry="1.9" fill="#f0c27a" opacity=".5"/>'
        + '<path d="M36 42l5 3 3-4 6 5 4-3 5 4 5-2M35 51.6l6-2 4 4 5-3 6 3 5-2M37 59.6l4-3 5 2 4-3 6 3 5-1M41 45l-1 6.6M50 46l1 5.4M55 43l-.6 7M46 53.6l-1 5M57 52l1 6.4" stroke="#d8cfc0" stroke-width=".35" fill="none" opacity=".75"/>'
        + '<path d="M44 29.5h12" stroke="#b8733a" stroke-width="1"/>',
    },
    /// Той, що з'являється на колі й чекає, щоб його впіймали: золотий із петриківською квіткою.
    golden: {
      body: '#f2c14e',
      decor: flower('#c62f25', '#fff3c4', '#2f7d3a') + '<path d="M35 38.5h30" stroke="#c62f25" stroke-width="1.4"/>',
    },
  };

  /// Глек у SVG-групі: тіло, орнамент по силуету, обведення й відблиск. <slot> — постійне ім'я місця
  /// (колесо, картка розпису, розписний глек, полиця): id для clipPath мусить бути сталим, інакше HTML полиці
  /// щоразу виходив би новим і swap() перемальовував би її на кожну пачку кліків.
  /// <body> — колір глини на колі: простий глек без розпису беруть саме її кольору (червона, біла, чорна).
  function jug(style, slot, body) {
    const s = STYLE[style] || STYLE[''];
    const id = 'clkjug-' + slot;
    return '<g class="clk-jug"><clipPath id="' + id + '"><path d="' + JUG + '"/></clipPath>'
      + '<path d="' + JUG + '" fill="' + (!style && body ? body : s.body) + '"/>'
      + '<g clip-path="url(#' + id + ')">' + s.decor + '</g>'
      + '<path d="' + JUG + '" fill="none" stroke="rgba(0,0,0,.28)" stroke-width=".8"/>'
      + '<path d="M39.6 42.5c-1.6 3.8-1.6 10.6.4 15" stroke="rgba(255,255,255,.22)" stroke-width="1.8" fill="none" stroke-linecap="round"/></g>';
  }
  /// Окремий глек (картка розпису, розписний глек, глек з полиці): видноколо обрізане по самому глеку.
  const jugSvg = (style, cls, slot, body) => '<svg class="' + (cls || '') + '" viewBox="31 22 38 45" aria-hidden="true">' + jug(style, slot, body) + '</svg>';

  /// «12:34» для відліків купців і глини.
  function mmss(ms) {
    const s = Math.max(0, Math.ceil(ms / 1000));
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  }

  // ---------- стан модуля ----------

  function state(root) {
    if (!root._clk) {
      root._clk = {
        el: null, count: null, rate: null, rival: null, sign: null, stage: null, wheelBox: null, wheel: null, turn: null,
        jugBox: null, heatRing: null, pops: null, sparks: null, fx: null, buffs: null, gold: null, fallEl: null, fallJug: null,
        shelfJugs: null, one: null, all: null, left: null, tabs: null, panes: {}, shop: null, modes: null, marks: null,
        styles: null, fire: null,
        buys: [], markBtns: [], styleBtns: [], secretBtns: [],
        base: 0, total: 0, lastSync: 0, viewNow: 0, recvAt: Date.now(), offlineMs: 8 * 3600 * 1000,
        clickBase: 1, baseSecond: 0, fairUntil: 0, fairMult: 7, inspireUntil: 0, inspireMult: 25,
        rateOf: 100, canSell: 0, mine: false, wear: null,
        golden: null, goldenGone: 0, lookedFor: 0,
        fall: null, fallGone: 0, fallBroke: 0, fallLooked: 0, fallGain: 0, fallStreak: 0, streakBonus: 0,
        // Дев'яте оновлення: вітер із поля (пасив ×3), натхнення з відсотком пасиву в кліку, щасливі кліки,
        // кіт-мандрівник, зірка й бажання, «що нового».
        windAt: 0, windUntil: 0, windMult: 3, inspireShare: 0,
        lucky: 0, luckySeen: -1, starWish: false,
        events: null, evBox: null, catEl: null, starEl: null, windEl: null, catGone: 0, starGone: 0, windRun: 0,
        news: '', newsAsked: false, newsT: 0, eyeWas: null,
        // Хата: глина, знаряддя, прикраси, дошка купців (view.house) і сцена, що від них росте.
        house: null, housePane: null, ordersPane: null, clays: [], clay: '', clayBody: '', clayRestUntil: 0,
        tools: [], decorList: [], orders: [], taken: [], paidSeen: null, payLooked: new Set(), refreshAt: 0, maxTaken: 3,
        houseBtns: [], clayBtns: [], orderBtns: [], cds: [],
        heat: 0, heatAt: Date.now(), heatFull: 18, heatTau: 3, momentumMax: 1,
        angle: 0, angleAt: 0, ringOff: -1, glow: -1, jugScale: -1,
        stamps: 0, stampsFree: 0, stampBonus: 0.02, stampsExtra: 0, stampSoft: 1000, stampIron: 0, science: null, fireArmed: 0,
        ups: {}, markList: [], styleList: [], secretList: [],
        tab: storeGet('clk.tab', 'shop'), mode: storeGet('clk.mode', '1'),
        hands: [], handsGain: 0, inflight: 0, inflightGain: 0, tokens: MAX_BATCH, tokensAt: Date.now(), shown: -1, slowAt: 0,
        downs: new Map(), keyDown: 0, lastDown: 0, onKeyUp: null,
        guard: null, eye: null, taps: [], eyeBusy: false, eyeKey: '', eyeOpen: false, eyeAt: 0, eyeArm: 0,
        raf: 0, timer: 0, boardAt: 0, board: null, ctx: null,
        // Частини (clicker-<id>.js): які вже змонтовані, підписи їхніх вкладок, останній вид для запізнілих.
        parts: new Set(), tabText: {}, lastView: null, catalog: null, front: null, back: null, ov: null,
        // Десяте оновлення (§10): видимість від IntersectionObserver, полиці чотири рази на секунду, хата з каталогу,
        // дозапит каталогу, коли в ньому чогось бракує.
        io: null, onScreen: true, secEls: null, knockAnim: null, rivalAt: 0, rivalKey: '',
        houseView: null, houseSig: '', houseCat: null, catalogGap: '', catalogAskAt: 0, catalogTries: {},
      };
    }
    return root._clk;
  }

  /// Картку справді видно: вона в документі, вкладка браузера на передньому плані й картка хоч краєм у вікні.
  /// Розкладку тут не питаємо (десяте оновлення, docs/games/specs/clicker-v10.md §10): visible() кличуть щокадру, а
  /// getClientRects() після будь-якої зміни DOM у тому самому кадрі змушував браузер синхронно перераховувати всю
  /// сторінку (11–13 тис. вузлів) — чверть часу головного потоку. «У вікні» каже IntersectionObserver (watchCard): він
  /// відповідає сам, після розкладки, яку браузер і так робить, а схована картка (hidden на столі) для нього «поза».
  const visible = (st) => !!st.el && st.el.isConnected && !document.hidden
    && (st.io ? st.onScreen : st.el.getClientRects().length > 0);

  /// Спостерігач для visible(). Картку каркас монтує ще до вставки в сторінку й потім лише ховає (hidden), тож
  /// спостерігаємо від mount до unmount: відповідь приходить і на вставку, і на кожне «сховали/показали». Запас 200 px —
  /// щоб лічильник ожив ще до того, як картку догорнули до краю екрана. Щойно знову видно — полиці й повільні рядки
  /// малюються одразу, а не за чверть секунди.
  function watchCard(st) {
    if (st.io) st.io.disconnect();
    st.io = null;
    st.onScreen = true;
    if (!window.IntersectionObserver || !st.el) return;
    st.io = new IntersectionObserver((entries) => {
      const on = entries[entries.length - 1].isIntersecting;
      if (on === st.onScreen) return;
      st.onScreen = on;
      if (on) st.slowAt = 0;
    }, { rootMargin: '200px 0px' });
    st.io.observe(st.el);
  }

  /// Серверне «зараз» у мс: мітка з виду плюс те, що минуло на нашому годиннику від його отримання.
  const serverNow = (st) => st.viewNow + (Date.now() - st.recvAt);

  /// Пасив від мітки сервера, округлений УНИЗ: у сервера ще лежить дробовий залишок, тож це чесна нижня межа.
  /// Ярмарок множить лише ту частину проміжку, яку він справді тривав, — рівно як Sync() на сервері.
  function passive(st) {
    const to = serverNow(st);
    const idle = Math.min(Math.max(0, to - st.lastSync), st.offlineMs);
    let fair = st.fairUntil > st.lastSync ? Math.min(st.fairUntil, to) - st.lastSync : 0;
    fair = Math.min(Math.max(0, fair), idle);
    // Вітер із поля (v9): потроює пасив рівно ті секунди, які справді віяв. Сервер рахує його лише за
    // короткий проміжок (гончар був біля кола) — тут той самий поріг, інакше лічильник обіцяв би зайве.
    let wind = idle <= EVENT_GAP_MS ? Math.min(st.windUntil, to) - Math.max(st.windAt, st.lastSync) : 0;
    wind = Math.min(Math.max(0, wind), idle);
    return Math.floor(((idle + (st.fairMult - 1) * fair + (st.windMult - 1) * wind) / 1000) * st.baseSecond);
  }

  /// Те, що сервер уже точно має: його число плюс пасив. Від нього рахуємо продаж.
  const firm = (st) => st.base + passive(st);

  /// Клік просто зараз. Під натхненням до нього додається ще три відсотки пасиву — і все це множиться на ×25:
  /// рівно як PerClick на сервері (v9 §A.7).
  function clickNow(st) {
    const now = serverNow(st);
    const ins = now < st.inspireUntil;
    return (st.clickBase + (ins ? st.baseSecond * st.inspireShare : 0)) * (ins ? st.inspireMult : 1)
      * (now < st.fairUntil ? st.fairMult : 1);
  }
  const windOn = (st, sn) => sn >= st.windAt && sn < st.windUntil;
  const secondNow = (st) => {
    const sn = serverNow(st);
    return st.baseSecond * (sn < st.fairUntil ? st.fairMult : 1) * (windOn(st, sn) ? st.windMult : 1);
  };

  /// Розгін просто зараз — той самий спад, що й на сервері: у e разів за heatTau секунд.
  const heatNow = (st) => st.heat * Math.exp(-Math.max(0, Date.now() - st.heatAt) / 1000 / st.heatTau);
  /// Множник кліка від розгону: ×1 на холодному колі, стеля маховика — на heatFull гарячих кліках.
  const momentumOf = (st, heat) => 1 + (st.momentumMax - 1) * Math.min(1, Math.max(0, heat) / st.heatFull);
  const heatFrac = (st) => Math.min(1, heatNow(st) / st.heatFull);

  /// Скільки рівнів влазить у глеки і скільки вони коштують. Геометрична сума: на сервері кожна ціна
  /// округлюється вгору, тож тут це оцінка — купує однаково сервер, і рівно стільки, скільки влізе.
  function afford(u, pots, want) {
    const g = u.growth || 1.5;
    const room = u.max > 0 ? Math.max(0, u.max - u.level) : MAX_BUY;
    let n;
    if (want === 'max') {
      n = pots < u.price ? 0 : Math.floor(Math.log(1 + (pots * (g - 1)) / u.price) / Math.log(g));
    } else n = +want;
    n = Math.max(0, Math.min(n, room, MAX_BUY));
    const cost = n ? (u.price * (Math.pow(g, n) - 1)) / (g - 1) : u.price;
    return { n, cost };
  }

  // ---------- малювання ----------

  /// Кличеться на кожен кадр, але щокадру живуть лише лічильник і коло (розписний глек, глек з полиці, frame частин).
  /// Кнопки полиць і прилавка — чотири рази на секунду (paintShop), рядки й бонуси — п'ять (paintSlow): «вже по
  /// кишені» око швидше однаково не ловить, а 24 верстати × afford() × short() щокадру на телефоні з'їдали десяту
  /// частину часу (десяте оновлення, docs/games/specs/clicker-v10.md §10).
  function paint(st) {
    const now0 = Date.now();
    // Підтверджене число рахуємо один раз: від нього і лічильник (з нашими ще не відправленими кліками),
    // і кнопки прилавка (уже без них).
    const sure = firm(st);
    let n = sure + st.handsGain + st.inflightGain;
    // Дрібний відкат — це не витрата, а різниця округлень між нашим доліком і сервером: не смикаємо число.
    if (st.shown >= 0 && n < st.shown && st.shown - n <= 2) n = st.shown;
    if (n !== st.shown) {
      st.shown = n;
      const text = big(n);
      countText(st, text);
      // Одиниця поруч із числом: глеки, гривні чи золоті (десяте оновлення). Міняється рідко — лише на порогах.
      const u = unit(n);
      if (u.key !== st.unitKey) { st.unitKey = u.key; st.unit.textContent = u.word; }
      // «999 999 999 999» на телефоні не влазить у звичний кегль — зменшуємо, а не переносимо.
      // Висоту шапки .long більше не міняє (див. .clk-head у clicker.css: вона стала від --clk-num),
      // а на картці від 900 px css узагалі лишає кегль незмінним — сцена під числом не ворухнеться.
      const long = text.length > 11;
      if (st.count.classList.contains('long') !== long) st.count.classList.toggle('long', long);
    }

    paintWheel(st);
    paintGolden(st);
    paintFall(st);
    for (const p of H.parts) if (st.parts.has(p.id)) callPart(p, 'frame', st, H.api, now0);

    const now = Date.now();
    if (now - st.slowAt >= SLOW_MS) {
      st.slowAt = now;
      paintShop(st, n, sure);
      paintSlow(st, n);
    }
  }

  /// Число лічильника — в окремому абсолютному шарі .clk-cnum усередині .clk-count, а місце під нього тримає
  /// «найширший текст розряду» (data-shape → ::before, clicker.css, блок «швидкість»). Шар — межа розкладки: нова
  /// цифра перекладає лише його. Раніше кожна зміна числа протікала крізь флекс і грід до кореня й коштувала повної
  /// розкладки документа — ~9 мс на ПК, а число міняється до 60 разів на секунду (десяте оновлення, §10).
  /// Розряд — текст без цифр і без дробу: цифри однакової ширини (tabular-nums), а дріб big() відкидає нулі в кінці
  /// («1,5 млн» → «1,523 млн»), тож місце тримаємо під найдовший дріб, який уже бачили в цьому розряді.
  function countText(st, text) {
    const c = st.count;
    let t = st.countNum;
    if (!t || t.parentNode !== c) {
      c.textContent = '';
      t = st.countNum = document.createElement('span');
      t.className = 'clk-cnum';
      c.appendChild(t);
      st.countKey = null;
      st.countShape = null;
    }
    t.textContent = text;
    const z = text.replace(/\d/g, '0');
    const key = z.replace(/,0*/, '');
    const shape = key === st.countKey && st.countShape && st.countShape.length >= z.length ? st.countShape : z;
    st.countKey = key;
    if (shape !== st.countShape) { st.countShape = shape; c.dataset.shape = shape; }
  }

  /// Кнопки полиць і прилавка: «по кишені чи ні», ціна за ×1/×10/макс, смужка «скільки ціни вже є». Лише те, що
  /// видно: на «Майстерні» — верстати й віхи, а розділи хати, розписів і купців — коли розгорнуті; решту вкладок
  /// малюють їхні частини. Прилавок (лівий стовпчик) видно завжди. Перемкнули вкладку чи розгорнули розділ —
  /// st.slowAt скидається, і кнопки оживають того ж кадру.
  function paintShop(st, n, sure) {
    if (st.tab === 'shop') {
      for (const b of st.buys) {
        const u = st.ups[b.dataset.buy];
        if (!u) continue;
        const maxed = u.max > 0 && u.level >= u.max;
        const a = afford(u, n, st.mode);
        const off = maxed || !st.mine || a.n < 1 || (st.mode !== 'max' && n < a.cost);
        if (b.disabled !== off) b.disabled = off;
        const label = maxed ? 'досить' : (a.n > 1 ? '×' + a.n + ' · ' : '') + short(Math.ceil(a.cost));
        if (b._price.textContent !== label) b._price.textContent = label;
        // Смужка «скільки ціни вже є» — кроком у 2 %, щоб не писати стиль на кожну дрібницю. Масштаб, а не ширина:
        // transform не чіпає розкладки (clicker.css, блок «швидкість»).
        if (b._bar) {
          const pct = maxed ? 100 : Math.min(100, Math.floor((n / Math.max(1, st.mode === 'max' ? u.price : a.cost)) * 50) * 2);
          if (b._pct !== pct) { b._pct = pct; b._bar.style.transform = 'scaleX(' + pct / 100 + ')'; }
        }
      }
      for (const b of st.markBtns) {
        const off = !st.mine || n < +b.dataset.price;
        if (b.disabled !== off) b.disabled = off;
      }
      if (secOpen(st, 'styles')) {
        for (const b of st.styleBtns) {
          const off = !st.mine || (b.dataset.owned !== '1' && n < +b.dataset.price);
          if (b.disabled !== off) b.disabled = off;
        }
      }
      // Знаряддя й прикраси — одноразові: куплене лишається сірим, некуплене чекає глеків.
      if (secOpen(st, 'house')) {
        for (const b of st.houseBtns) {
          const off = !st.mine || b.dataset.owned === '1' || n < +b.dataset.price;
          if (b.disabled !== off) b.disabled = off;
        }
      }
      // Купці: замовлення на розпис — лише за розпис із колекції; купців у дорозі — не більше трьох.
      if (secOpen(st, 'orders')) {
        for (const b of st.orderBtns) {
          const invest = b.dataset.kind === 'invest';
          const off = !st.mine || b.dataset.can !== '1' || n < +b.dataset.need || (invest && st.taken.length >= st.maxTaken);
          if (b.disabled !== off) b.disabled = off;
        }
      }
    }

    // Продаж — від підтвердженого числа, а не від намальованого: у st.hands може лежати хвіст кліків,
    // які цієї миті ще не долетіли, і кнопка обіцяла б сервером не наліплені глеки.
    const ready = Math.floor(sure / st.rateOf);
    const many = Math.min(ready, st.canSell);
    const one = !(st.mine && ready >= 1 && st.canSell >= 1);
    if (st.one.disabled !== one) st.one.disabled = one;
    const all = !(st.mine && many > 1);
    if (st.all.disabled !== all) st.all.disabled = all;
    const pots = String(many * st.rateOf);
    if (st.all.dataset.pots !== pots) st.all.dataset.pots = pots;
    const label = 'Обміняти все (' + num(many) + ' 🏺)';
    if (st.all.textContent !== label) st.all.textContent = label;
  }

  /// Розділи Майстерні (<details>), знайдені раз на картку. Розгорнули розділ — його кнопки й відліки оживають
  /// того ж кадру: toggle скидає st.slowAt.
  function secEls(st) {
    if (!st.secEls) {
      st.secEls = {};
      for (const x of SECTIONS) {
        const el = st.el.querySelector('.clk-sec[data-sec="' + x.key + '"]');
        st.secEls[x.key] = el;
        if (el) el.addEventListener('toggle', () => { st.slowAt = 0; });
      }
    }
    return st.secEls;
  }
  /// Розділ Майстерні розгорнутий? Згорнутого гравець не бачить — і кнопок у ньому не малюємо.
  const secOpen = (st, key) => { const el = secEls(st)[key]; return !el || el.open; };

  /// Круг кола (диск, борозни, цятка) — в окремому <svg> під рештою кола, окремим шаром композитора (will-change у
  /// clicker.css, блок «швидкість»), і крутиться сам шар. Поворот SVG-групи всередині спільного <svg> щокадру міняв
  /// дерево властивостей малювання: Chrome перекомпоновував шари всієї сторінки й перемальовував коло — ~5 % головного
  /// потоку навіть без кліків (десяте оновлення, §10). Глек, руки гончаря й «пружина» глини лишаються в першому
  /// <svg> кола, як і були, — тож і querySelector('svg') частин знаходить саме його: круг додаємо після нього.
  function discLayer(st) {
    if (st.discSvg && st.discSvg.parentNode === st.wheel) return st.discSvg;
    const turn = st.wheel && st.wheel.querySelector('.clk-turn');
    if (!turn) return null;
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('viewBox', '0 0 100 100');
    svg.setAttribute('aria-hidden', 'true');
    svg.setAttribute('class', 'clk-disc-layer');
    turn.style.transform = '';
    svg.appendChild(turn);
    st.wheel.appendChild(svg);
    st.discSvg = svg;
    return svg;
  }

  /// Коло крутиться від пасиву й від розгону, кільце навколо нього — це розгін, сяйво — теж. Усе за кадр і
  /// лише різницями: стилі пишемо тоді, коли число справді зрушило.
  function paintWheel(st) {
    const t = performance.now();
    const dt = st.angleAt ? Math.min(0.1, (t - st.angleAt) / 1000) : 0;
    st.angleAt = t;
    const frac = heatFrac(st);
    const sec = secondNow(st);
    // Градусів за секунду: без підмайстрів коло стоїть, з піччю повільно пливе, а від швидких кліків розкручується.
    const speed = (sec > 0 ? 30 + 30 * Math.log10(1 + sec) : 0) + 420 * frac;
    if (speed > 0 && dt > 0) {
      st.angle = (st.angle + speed * dt) % 360;
      const disc = discLayer(st) || st.turn;
      disc.style.transform = 'rotate(' + st.angle.toFixed(1) + 'deg)';
    }
    const off = Math.round(RING * (1 - frac) * 10) / 10;
    if (off !== st.ringOff) { st.ringOff = off; st.heatRing.style.strokeDashoffset = off; }
    const glow = Math.round(frac * 50) / 50;
    if (glow !== st.glow) {
      st.glow = glow;
      st.wheel.style.setProperty('--clk-glow', glow);
      st.wheelBox.classList.toggle('hot', frac >= 0.98 && st.momentumMax > 1);
    }
    const scale = Math.round((1 + 0.1 * frac) * 100) / 100;
    if (scale !== st.jugScale) { st.jugScale = scale; st.jugBox.style.transform = 'scale(' + scale + ')'; }
  }

  // ---------- плашки бафів під колом ----------
  // Записки Smaug (27.09): «ярмарок, натхнення, розгін, серія — кожне в новому рядку, бо зараз усе в купі й не видно,
  // скільки секунд до кінця». Було: один рядок пігулок із «…» — на ПК при п'яти бафах лишалось «🎪 Я…». Стало: кожен
  // баф — своя клітинка сітки; ліворуч великими цифрами множник і секунди (tabular-nums і стала ширина — число не
  // стрибає), праворуч дрібно назва — обрізатись може лише вона; під ними смужка часу, що тане. Порядок — що скоро
  // скінчиться, те першим; останні п'ять секунд плашка світиться. Розмітка плашок складається раз: щосекунди
  // міняється лише текст числа (коли він справді інший), а смужка тане сама — WAAPI на transform, без JS щокадру.
  // Назва — слово й хвіст (tail): на маку «Серія 886» різалась до «Серія …» (09.10), і саме число серії, яке варто
  // бачити, зникало першим. Тепер хвіст стоїть окремо й не ріжеться ніколи; вузька клітинка ховає значок, ще вужча —
  // слово (clicker.css, @container clkbuff).
  const BUFF_END_MS = 5000;
  const BUFF_KINDS = [
    { key: 'fair', icon: '🎪', name: () => 'Ярмарок', what: (st) => 'Ярмарок: усе ×' + dec(st.fairMult) },
    { key: 'inspire', icon: '✨', name: () => 'Натхнення', what: (st) => 'Натхнення: клік ×' + st.inspireMult },
    { key: 'wind', icon: '🌬', name: () => 'Вітер із поля', what: (st) => 'Вітер із поля: без тебе все ×' + dec(st.windMult) },
    { key: 'heat', icon: '🌀', name: () => 'Розгін', what: () => 'Розгін кола: що частіше клацаєш, то більший клік; смужка — наскільки коло гаряче' },
    { key: 'streak', icon: '🤲', name: () => 'Серія', tail: (st) => count(st.fallStreak), what: (st) => 'Серія спійманих глеків з полиці: наступний дасть на '
      + Math.round(st.streakBonus * 100) + ' % більше' },
    { key: 'wish', icon: '🌠', name: () => 'Бажання', what: () => 'Бажання на зірку: наступний спійманий глек з полиці ×3' },
    // Бафи від друзів (цех, clicker-guild.js): з відліком і від кого — отримувач бачить їх просто під колом.
    { key: 'flend', icon: '🧑‍🎓', name: (st) => 'від ' + friendBuffFrom(st, 'lend'), what: (st) => 'Підмайстер від ' + friendBuffFrom(st, 'lend') + ' у гостях: ліплення вдвічі швидше' },
    { key: 'fcheer', icon: '👏', name: (st) => 'від ' + friendBuffFrom(st, 'cheer'), what: (st) => 'Похвала від ' + friendBuffFrom(st, 'cheer') + ': +10 % до всього' },
  ];

  const friendBuffFrom = (st, kind) => {
    const b = st.guild && st.guild.buffs && st.guild.buffs[kind];
    return (b && b.from) || 'друга';
  };
  /// Довгий відлік на плашці (бафи друзів тривають години): «23 год», «47 хв».
  const longLeft = (min) => (min >= 60 ? Math.floor(min / 60) + ' год' : min + ' хв');

  /// Що з бафів діє саме зараз: множник, до коли й скільки триває весь (для смужки). Лише читає стан.
  function buffsNow(st, sn, mom) {
    const out = [];
    // Під полицею Ока майстра ярмарок і натхнення стоять (05.10): плашка лишається з тим самим залишком, а не зникає —
    // інакше виглядало б, що полиця бафи скинула.
    if (st.fairHeld > 0) out.push({ key: 'fair', mult: '×' + dec(st.fairMult), held: st.fairHeld, span: st.fairSpan || 66000 });
    else if (sn < st.fairUntil) out.push({ key: 'fair', mult: '×' + dec(st.fairMult), until: st.fairUntil, span: st.fairSpan || 66000 });
    if (st.inspireHeld > 0) out.push({ key: 'inspire', mult: '×' + st.inspireMult, held: st.inspireHeld, span: st.inspireSpan || 20000 });
    else if (sn < st.inspireUntil) out.push({ key: 'inspire', mult: '×' + st.inspireMult, until: st.inspireUntil, span: st.inspireSpan || 20000 });
    if (windOn(st, sn)) out.push({ key: 'wind', mult: '×' + dec(st.windMult), until: st.windUntil, span: Math.max(1000, st.windUntil - st.windAt) });
    if (st.momentumMax > 1 && mom > 1.05) out.push({ key: 'heat', mult: '×' + dec(mom), level: (mom - 1) / (st.momentumMax - 1) });
    // Серія без стелі (v9 §A.3): +10 % за кожен до десятого, до сотні +2 %, далі +1 % — відсоток рахує сервер (fall.bonus).
    if (st.fallStreak > 1) out.push({ key: 'streak', mult: '+' + Math.round(st.streakBonus * 100) + ' %' });
    if (st.starWish) out.push({ key: 'wish', mult: '×3' });
    const gb = st.guild && st.guild.enabled && st.guild.buffs;
    if (gb) {
      const lend = gb.lend ? Date.parse(gb.lend.until) || 0 : 0;
      const cheer = gb.cheer ? Date.parse(gb.cheer.until) || 0 : 0;
      if (sn < lend) out.push({ key: 'flend', mult: '×2', until: lend, span: Math.max(864e5, lend - sn), long: true });
      if (sn < cheer) out.push({ key: 'fcheer', mult: '+10 %', until: cheer, span: Math.max(36e5, cheer - sn), long: true });
    }
    return out;
  }

  /// Плашки складаємо раз на хост (.clk-buffs нового mount — нові плашки): далі лише текст, клас і смужка.
  function buffEls(st) {
    if (st.buffEls && st.buffEls.host === st.buffs) return st.buffEls;
    const els = { host: st.buffs };
    st.buffs.textContent = '';
    for (const k of BUFF_KINDS) {
      const el = document.createElement('span');
      el.className = 'clk-buff ' + k.key;
      el.hidden = true;
      el.innerHTML = '<span class="clk-bico" aria-hidden="true">' + k.icon + '</span>'
        + '<b class="clk-bnum"><span class="clk-bmul"></span><span class="clk-bsec"></span></b>'
        + '<span class="clk-bname"><span class="clk-bw"></span><span class="clk-bt"></span></span><i class="clk-bbar" aria-hidden="true"></i>';
      st.buffs.appendChild(el);
      els[k.key] = { el, kind: k, mul: el.querySelector('.clk-bmul'), sec: el.querySelector('.clk-bsec'),
        word: el.querySelector('.clk-bw'), tail: el.querySelector('.clk-bt'), name: '',
        bar: el.querySelector('.clk-bbar'), until: 0, anim: null, level: -1, order: '', secs: -1 };
    }
    st.buffEls = els;
    return els;
  }

  /// Смужка часу: від частки, що лишилась, до нуля рівно за залишок. Під prefers-reduced-motion — сходинками раз на
  /// секунду (paintBuffs), без безперервного руху.
  function buffBar(b, left, span) {
    const f = Math.max(0, Math.min(1, left / span));
    if (b.anim) { b.anim.cancel(); b.anim = null; }
    if ((REDUCED_MQ && REDUCED_MQ.matches) || !b.bar.animate) { b.bar.style.transform = 'scaleX(' + f.toFixed(3) + ')'; return; }
    try {
      b.anim = b.bar.animate([{ transform: 'scaleX(' + f.toFixed(4) + ')' }, { transform: 'scaleX(0)' }],
        { duration: Math.max(1, left), easing: 'linear', fill: 'forwards' });
    } catch { b.bar.style.transform = 'scaleX(' + f.toFixed(3) + ')'; }
  }

  function paintBuffs(st, sn, mom) {
    if (!st.buffs) return;
    const els = buffEls(st);
    const now = buffsNow(st, sn, mom);
    const on = new Set(now.map((x) => x.key));
    // Що скоро скінчиться — першим; безстрокові (розгін, серія, бажання) — за ними, завжди в тому самому порядку.
    const timed = now.filter((x) => x.until).sort((a, b) => a.until - b.until).map((x) => x.key);
    const stepped = !!(REDUCED_MQ && REDUCED_MQ.matches);
    for (const k of BUFF_KINDS) {
      const b = els[k.key];
      if (!on.has(k.key)) {
        if (!b.el.hidden) { b.el.hidden = true; if (b.anim) { b.anim.cancel(); b.anim = null; } b.until = 0; b.level = -1; }
        continue;
      }
      const x = now.find((y) => y.key === k.key);
      if (b.el.hidden) b.el.hidden = false;
      if (b.mul.textContent !== x.mult) { b.mul.textContent = x.mult; b.el.title = k.what(st); }
      // Стоїть під Оком майстра — «⏸» хвостом (css ставить його перед словом): у найвужчій клітинці лишається саме він.
      const word = x.held ? 'чекає' : k.name(st);
      const tail = x.held ? '⏸' : k.tail ? k.tail(st) : '';
      if (b.name !== word + '|' + tail) {
        b.name = word + '|' + tail;
        b.word.textContent = word;
        b.tail.textContent = tail;
        b.el.title = k.what(st) + (x.held ? ' — стоїть, поки не відповіси майстрові' : '');
      }
      if (b.el.classList.contains('held') !== !!x.held) b.el.classList.toggle('held', !!x.held);
      const order = String(x.until ? timed.indexOf(k.key) : 10 + BUFF_KINDS.indexOf(k));
      if (b.order !== order) { b.order = order; b.el.style.order = order; }
      if (x.held) {
        // Стоїть: число й смужка не рухаються, а після відповіді смужка стартує наново від того самого залишку.
        const secs = Math.max(0, Math.ceil(x.held / 1000));
        if (b.secs !== secs) { b.secs = secs; b.sec.textContent = String(secs); b.sec.classList.toggle('w3', secs >= 100); }
        if (b.until !== -1) {
          b.until = -1;
          if (b.anim) { b.anim.cancel(); b.anim = null; }
          b.bar.style.transform = 'scaleX(' + Math.max(0, Math.min(1, x.held / x.span)).toFixed(3) + ')';
        }
        if (b.el.classList.contains('end')) b.el.classList.remove('end');
      } else if (x.until) {
        const left = x.until - sn;
        const secs = Math.max(0, Math.ceil(left / (x.long ? 60000 : 1000)));
        if (b.secs !== secs) {
          b.secs = secs;
          b.sec.textContent = x.long ? longLeft(secs) : String(secs);
          b.sec.classList.toggle('w3', x.long || secs >= 100);
          if (stepped) buffBar(b, left, x.span);
        }
        // Новий баф чи той самий, але подовжений (ще один розписний глек) — смужка стартує наново від свого залишку.
        if (Math.abs(b.until - x.until) > 50) { b.until = x.until; buffBar(b, left, x.span); }
        const end = left <= BUFF_END_MS;
        if (b.el.classList.contains('end') !== end) b.el.classList.toggle('end', end);
      } else {
        if (b.secs !== -1) { b.secs = -1; b.sec.textContent = ''; b.el.classList.remove('end'); }
        // Розгін — смужка показує, наскільки коло гаряче (спадає сама, щойно перестаєш клацати); серія й бажання — без смужки.
        const level = x.level != null ? Math.round(Math.max(0, Math.min(1, x.level)) * 100) / 100 : -1;
        if (b.level !== level) { b.level = level; b.bar.style.transform = 'scaleX(' + Math.max(0, level) + ')'; }
      }
    }
    const any = now.length > 0;
    if (st.buffs.hidden === any) st.buffs.hidden = !any;
  }

  /// Те, що не мусить жити шістдесят разів на секунду: рядок швидкості, бонуси, суперник, прогрес клейм.
  function paintSlow(st, shown) {
    const sn = serverNow(st);
    const sec = secondNow(st);
    const heat = heatNow(st);
    const mom = momentumOf(st, heat);
    // Одиниця та сама, що в лічильнику над рядком, — без слова: «+17,8 золотого» двічі довшало рядок до 466 px, і на
    // макбуці (стовпчик 465 px) його хвіст різали «…» (Smaug, 08.10). Інша одиниця («+500 ₴» при золотих) — зі словом.
    const bare = (n) => { const u = unit(n); return Number.isFinite(n) && u.key === st.unitKey ? count(n / u.div) : short(n); };
    const rate = 'за клік +' + bare(clickNow(st))
      + (st.momentumMax > 1 ? ' · розгін до ×' + dec(st.momentumMax) : '')
      + (sec > 0 ? ' · без тебе +' + bare(sec) + ' за секунду' : ' · підмайстрів ще нема');
    if (st.rate.textContent !== rate) st.rate.textContent = rate;

    paintBuffs(st, sn, mom);
    const fair = sn < st.fairUntil, inspire = sn < st.inspireUntil;
    if (st.stage.classList.contains('fair') !== fair) st.stage.classList.toggle('fair', fair);
    if (st.stage.classList.contains('inspire') !== inspire) st.stage.classList.toggle('inspire', inspire);

    const liveTotal = st.total + (shown - st.base);
    if (visible(st)) loadBoard(st);
    paintRival(st, liveTotal);
    if (st.tab === 'fire') paintFire(st, liveTotal);
    // Хата й дошка купців — розділи Майстерні (v8), а не свої вкладки: ціни глини, «замісити» й відліки
    // малюються, поки відкрита Майстерня й розгорнутий їхній розділ.
    if (st.tab === 'shop') paintCountdowns(st, sn, shown);
    // Купець повернувся, а гончар нічого не робив: сервер рахує повернення лише при дії чи виді, тож питаємо вид
    // самі — раз на купця, з запасом у дві секунди й лише коли картку видно (як look для глеків).
    for (const t of st.taken) {
      if (sn < t.payAt + 2000 || st.payLooked.has(t.id) || !st.ctx || !visible(st)) continue;
      st.payLooked.add(t.id);
      st.ctx.act('look');
    }
    paintEye(st);
    paintEvents(st, sn);
    for (const p of H.parts) if (st.parts.has(p.id)) callPart(p, 'slow', st, H.api, sn);
  }

  /// Відліки в хаті й на дошці: глина відлежується, купець повертається, дошка оновлюється.
  function paintCountdowns(st, sn, shown) {
    const house = secOpen(st, 'house');
    const orders = secOpen(st, 'orders');
    for (const el of st.cds) {
      if (!(el._sec === 'house' ? house : orders)) continue;
      const at = +el.dataset.at;
      const left = at - sn;
      const text = left > 0 ? mmss(left) : el.dataset.done || '0:00';
      if (el.textContent !== text) el.textContent = text;
    }
    if (!house) return;
    const resting = sn < st.clayRestUntil;
    for (const b of st.clayBtns) {
      const owned = b.dataset.owned === '1';
      const on = b.dataset.on === '1';
      const off = !st.mine || on || (owned ? resting : shown < +b.dataset.price);
      if (b.disabled !== off) b.disabled = off;
      const label = on ? 'на колі' : owned ? (resting ? 'відлежується ' + mmss(st.clayRestUntil - sn) : 'замісити') : short(+b.dataset.price);
      if (b._price.textContent !== label) b._price.textContent = label;
    }
  }

  // ---------- Око майстра ----------

  /// Майстер щось хоче: коло стоїть або чекає відповіді на полицю. Кліки тоді не рахуються — і не малюються.
  const guardOn = (st) => !!st.guard;

  /// Скільки після появи полиці кнопка «Показати полицю» ще не тисне: черга швидких кліків по колу, на місце
  /// якого стала панель, не має ні натиснути її, ні тим паче потрапити в картинку.
  const EYE_ARM_MS = 1000;

  /// За що майстер питає не в чергу (why з виду) — перед інструкцією, як пройти полицю.
  const DOUBT = {
    rhythm: 'Кліки йшли надто рівно, мов під метроном, — так клацає автоклікер. Покажи майстрові, що це рука. ',
    press: 'Кнопку відпускали миттєво, раз за разом, — так клацає автоклікер (або тачпад). Покажи майстрові, що це рука. ',
  };

  /// Панель майстра замість сцени: відлік паузи, або інструкція з кнопкою, або полиця, де треба торкнутись глечиків.
  /// Полиця відкривається лише кнопкою: доти картинка за завісою й торкань не приймає. Інакше той, хто швидко
  /// клацав коло, проклацував три полиці поспіль, не встигши їх побачити, і діставав десять хвилин паузи.
  function paintEye(st) {
    const e = st.eye;
    const g = st.guard;
    const on = guardOn(st) && st.mine;
    if (st.stage.hidden !== on) st.stage.hidden = on;
    if (e.el.hidden === on) e.el.hidden = !on;
    if (!on) return;

    const left = g.lockUntil ? g.lockUntil - serverNow(st) : 0;
    const locked = left > 0;
    // Нова полиця (чи та сама після паузи): завіса знову опущена, торкання скинуто, кнопка озброїться за секунду.
    const key = g.serial + (locked ? ':lock' : ':shelf');
    if (st.eyeKey !== key) {
      st.eyeKey = key;
      st.eyeOpen = false;
      st.eyeAt = Date.now();
      st.taps = [];
      st.eyeBusy = false;
      // Озброїти кнопку своїм таймером, а не лише циклом малювання: у схованій вкладці rAF не крутиться.
      clearTimeout(st.eyeArm);
      clearTimeout(st.refreshT);
      st.eyeArm = setTimeout(() => paintEye(st), EYE_ARM_MS + 30);
    }
    const open = st.eyeOpen && !locked;

    let text;
    if (locked) {
      const s = Math.ceil(left / 1000);
      text = '🔒 Коло стоїть ще ' + Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0') + '. Три полиці поспіль — не ті глеки.'
        + ' Пасив, покупки й прилавок працюють; кліки й глеки — ні. Після паузи майстер спитає ще раз.';
    } else if (!open) {
      text = (DOUBT[g.why] || 'Майстер дивиться, чи коло крутить рука, а не автоклікер. ')
        + 'Як пройти: натисни «Показати полицю», а тоді торкнись на картинці кожного глечика — їх там ' + g.count
        + '. Глечик — той, що з вузькою шийкою; горщики, миски й черепки не чіпай. Торкнувся не туди — «Скинути торкання».'
        + ' Три полиці поспіль не ті — коло стане на 10 хвилин. Поки не відповіси, кліки не рахуються.' + eyePay(st, g) + eyeHeld(st);
    } else {
      text = 'Торкнись кожного глечика — їх тут ' + g.count + '. Глечик — той, що з вузькою шийкою. Торкнувся не туди — «Скинути торкання».'
        + eyePay(st, g) + eyeHeld(st);
    }
    if (e.text.textContent !== text) e.text.textContent = text;
    const tries = locked ? '' : g.misses ? 'не ті — ось інша полиця · спроба ' + (g.misses + 1) + ' з ' + g.maxMisses : '';
    if (e.tries.textContent !== tries) e.tries.textContent = tries;
    if (e.pic.hidden !== locked) e.pic.hidden = locked;
    if (e.veil.hidden !== open) e.veil.hidden = open;
    if (e.pic.classList.contains('veiled') === open) e.pic.classList.toggle('veiled', !open);
    if (e.reset.hidden !== !open) e.reset.hidden = !open;
    // Кнопка озброюється за секунду після появи: черга кліків, що летіла в коло, не має її натиснути.
    const armed = !locked && !open && Date.now() - st.eyeAt >= EYE_ARM_MS;
    if (e.go.disabled !== !armed) e.go.disabled = !armed;

    // Картинку міняємо лише на нову полицю: вид летить на кожну дію, а base64 полиці між ними той самий.
    if (!locked && g.png && e.img._serial !== g.serial) {
      e.img._serial = g.serial;
      e.img.src = g.png;
    }
    const marks = st.taps.map((t, i) => '<i style="left:' + (t[0] / g.width * 100).toFixed(2) + '%;top:'
      + (t[1] / g.height * 100).toFixed(2) + '%">' + (i + 1) + '</i>').join('');
    if (e.marks._html !== marks) { e.marks._html = marks; e.marks.innerHTML = marks; }
    const off = st.eyeBusy || !st.taps.length;
    if (e.reset.disabled !== off) e.reset.disabled = off;
    e.pic.classList.toggle('busy', st.eyeBusy);
  }

  /// Чи заплатить майстер за цю полицю (v9 §A.1). Промахи ріжуть платню навпіл — сервер уже це врахував у gain.
  function eyePay(st, g) {
    if (!g.pays || !(g.gain > 0)) return ' Цього разу без платні: майстер ще пильнує.';
    return ' 🪙 За чесну руку майстер відсипле ' + potsShort(g.gain) + ' — дві години роботи й десять тисяч кліків'
      + (g.misses ? ' (половину: рука вже раз промахнулась).' : '.');
  }

  /// Ярмарок і натхнення під полицею стоять (05.10) — кажемо про це, щоб полицю не квапились проклацати.
  function eyeHeld(st) {
    const fair = st.fairHeld > 0, ins = st.inspireHeld > 0;
    if (!fair && !ins) return '';
    if (fair && ins) return ' ⏸ Ярмарок і натхнення чекають: їхні секунди підуть далі, щойно відповіси.';
    return ' ⏸ ' + (fair ? 'Ярмарок' : 'Натхнення') + ' чекає: ' + Math.ceil((fair ? st.fairHeld : st.inspireHeld) / 1000)
      + ' с підуть далі, щойно відповіси.';
  }

  /// Кнопка «Показати полицю»: лише справжній натиск і лише озброєної кнопки. Відтак торкання картинки рахуються.
  function openShelf(st, ev) {
    if (!human(ev) || !st.guard || st.eye.go.disabled) return;
    st.eyeOpen = true;
    paintEye(st);
  }

  /// Торкання полиці. Лише справжні (isTrusted): скрипт, що тицяє в картинку dispatchEvent-ом, сюди не дійде —
  /// хоча він однаково не знає, куди тицяти. І лише відкритої кнопкою полиці: під завісою торкань нема.
  /// Координати — у пікселях картинки, як їх чекає сервер.
  function tapShelf(st, ev) {
    const g = st.guard;
    if (!human(ev) || !g || !g.png || !st.eyeOpen || st.eyeBusy || !st.ctx) return;
    if (g.lockUntil && g.lockUntil > serverNow(st)) return;
    if (ev.pointerType === 'mouse' && ev.button !== 0) return;
    ev.preventDefault();
    const r = st.eye.img.getBoundingClientRect();
    if (!r.width || !r.height) return;
    const x = Math.round(((ev.clientX - r.left) / r.width) * g.width * 10) / 10;
    const y = Math.round(((ev.clientY - r.top) / r.height) * g.height * 10) / 10;
    st.taps.push([x, y]);
    if (st.taps.length >= g.count) {
      st.eyeBusy = true;
      const serial = g.serial;
      const taps = st.taps.slice();
      st.ctx.act('answer', { taps }).then((res) => {
        // Невдача без нової полиці (зіпсовані торкання, зв'язок) — дати спробувати ще раз ту саму.
        if (st.guard && st.guard.serial === serial && !(res && res.ok)) { st.taps = []; st.eyeBusy = false; }
        paintEye(st);
      }, () => { st.taps = []; st.eyeBusy = false; paintEye(st); });
    }
    paintEye(st);
  }

  // ---------- розписний глек і глек з полиці ----------

  /// Розписний глек: стоїть у своєму вікні; утік — один раз питаємо сервер про наступний.
  function paintGolden(st) {
    const g = st.golden;
    const b = st.gold;
    // Поки майстер чекає, глек не ловиться (сервер відмовить) — тож і не показуємо.
    if (!g || !st.mine || guardOn(st)) { if (!b.hidden) b.hidden = true; flyGold(st, false); return; }
    const now = serverNow(st);
    const show = now >= g.at && now <= g.until && st.goldenGone !== g.at;
    // Сцени не видно — той самий глек стоїть поверх усього (записка №29); на сцені його тоді нема: двох глеків не буває.
    const away = show && aside(st);
    flyGold(st, away, g, now);
    const here = show && !away;
    if (b.hidden === here) {
      b.hidden = !here;
      if (here) {
        b.style.left = g.x + '%';
        b.style.top = g.y + '%';
        b.style.setProperty('--clk-left', Math.max(0, (g.until - now) / 1000) + 's');
        b.classList.remove('run');
        void b.offsetWidth;            // перезапустити кільце-таймер для нового глека
        b.classList.add('run');
      }
    }
    // Один раз на глек і з запасом у п'ять секунд: наш «серверний час» — оцінка, і сервер мусить уже точно
    // вважати глек утеклим, інакше розкладу не змінить. І лише коли картку видно: схована картка не підписана
    // на кімнату, нового виду не дочекається, а кожен look — це запис у базу й «живий» гончар, якого
    // прибиральник ніколи не прибере.
    if (now > g.until + CATCH_GRACE_MS + 5000 && st.lookedFor !== g.until && st.ctx && visible(st)) {
      st.lookedFor = g.until;
      st.ctx.act('look');
    }
  }

  /// Глек з полиці: три секунди летить від полиці до долівки (CSS-анімація, а від'ємна затримка дає стати в
  /// політ посередині, якщо картку відкрили пізно). Спіймали — «+N» і золоті бризки; долетів — черепки.
  function paintFall(st) {
    const f = st.fall;
    const b = st.fallEl;
    if (!f || !st.mine || guardOn(st)) { if (!b.hidden) b.hidden = true; flyFall(st, false); pinCatch(st); return; }
    const now = serverNow(st);
    const show = now >= f.at && now <= f.until && st.fallGone !== f.at;
    const wasFly = !!st.flyFall && !st.flyFall.hidden;
    // Сцени не видно (прокрутили, відкрите вікно) — глек летить поверх усього, у смузі справа (записка №29).
    const away = show && aside(st);
    flyFall(st, away, f, now);
    pinCatch(st);
    const here = show && !away;
    if (b.hidden === here) {
      b.hidden = !here;
      if (here) {
        b.style.left = f.x + '%';
        b.style.setProperty('--clk-fallms', (f.until - f.at) + 'ms');
        // Летить від полиці (її висоту задає css) до долівки: центр глека стає на 92 % висоти сцени.
        b.style.setProperty('--clk-drop', Math.max(40, st.stage.clientHeight * 0.92 - b.offsetTop - b.offsetHeight / 2) + 'px');
        b.style.animationDelay = (-(now - f.at)) + 'ms';
        b.classList.remove('run');
        void b.offsetWidth;
        b.classList.add('run');
      }
    }
    // Розбився — але лише той, що розбився щойно і на очах: після довгої відсутності черепків не малюємо.
    if (now > f.until && now - f.until < 1500 && st.fallGone !== f.at && st.fallBroke !== f.at) {
      st.fallBroke = f.at;
      shatter(st, f.x, wasFly);
    }
    if (now > f.until + CATCH_GRACE_MS + 5000 && st.fallLooked !== f.until && st.ctx && visible(st)) {
      st.fallLooked = f.until;
      st.ctx.act('look');
    }
  }

  // ---------- глек поверх усього (записка Smaug №29) ----------
  //
  // На телефоні розпис, ремесло під колом, вікно дарунка — усе, що робиться поза колом, ховає сцену, і глек з полиці
  // розбивався непоміченим (сервер бачить дії — гончар «біля кола», тож серія обривалась). Тепер, поки сцени не видно,
  // той самий глек (той самий вид, той самий залишок часу) летить у фіксованому шарі вздовж правого краю екрана й
  // ловиться тією самою дією. Шар — дві кнопки в body: картка кола — container (layout containment), і fixed усередині
  // неї був би прив'язаний до картки, а не до екрана. Кіт і зірка лишаються на сцені: вони нічого не рвуть.

  /// Сцену зараз не видно: її прокрутили з екрана або над нею відкрите вікно кола. Картку взагалі сховали (інша
  /// сторінка сайту) — це не «не видно», а «не тут»: тоді й шару нема (сервер у такій відсутності серію не рве).
  const aside = (st) => !!st.stageLaid && (!st.stageSeen || H.api.overlayOpen(st));

  /// «Чи видно сцену» — від IntersectionObserver (не getBoundingClientRect щокадру, див. visible()). Видно — коли хоч
  /// половина сцени між липкою шапкою сайту й нижнім меню; поля спостерігача переставляємо лише з розміром вікна.
  /// «Сцена на сторінці» (stageLaid) каже ResizeObserver: IntersectionObserver мовчить, коли вже прокручену з екрана
  /// сцену ще й сховали (перейшли на «Ефір» — «не видно» → «не видно»), і шар глека літав би на чужій сторінці.
  function watchStage(st) {
    let io = null, ro = null, sig = '';
    const set = () => {
      const band = viewBand();
      const s = Math.round(band.top) + ':' + Math.round(window.innerHeight - band.bottom);
      if (s === sig && io) return;
      sig = s;
      if (io) io.disconnect();
      io = new IntersectionObserver((es) => {
        const e = es[es.length - 1];
        st.stageLaid = e.boundingClientRect.height > 0;
        st.stageSeen = e.isIntersecting && e.intersectionRatio >= 0.5;
      }, { rootMargin: '-' + Math.round(band.top) + 'px 0px -' + Math.round(window.innerHeight - band.bottom) + 'px 0px', threshold: [0, 0.5, 1] });
      io.observe(st.stage);
    };
    st.stageSeen = true;
    st.stageLaid = false;
    if (!window.IntersectionObserver || !st.stage) return null;
    set();
    window.addEventListener('resize', set);
    if (window.ResizeObserver) {
      ro = new ResizeObserver((es) => {
        const r = es[es.length - 1].contentRect;
        const laid = r.width > 0 && r.height > 0;
        if (laid === st.stageLaid) return;
        st.stageLaid = laid;
        if (!laid) flyOff(st);
      });
      ro.observe(st.stage);
    }
    return { stop() { window.removeEventListener('resize', set); if (io) io.disconnect(); if (ro) ro.disconnect(); } };
  }

  /// Сцени нема на сторінці (картку сховали чи вийняли): шар поверх і «🏺 лови!» гаснуть одразу, а не висять.
  function flyOff(st) {
    if (st.flyFall && !st.flyFall.hidden) st.flyFall.hidden = true;
    if (st.flyGold && !st.flyGold.hidden) st.flyGold.hidden = true;
    pinCatch(st);
  }

  /// Кнопки шару (у body; прибирає unmount). Ловлять тими самими діями, що й на сцені.
  function mountFly(st) {
    const mk = (cls, label, jugHtml) => {
      const b = document.createElement('button');
      b.type = 'button';
      b.className = 'clk-fly ' + cls;
      b.hidden = true;
      b.setAttribute('aria-label', label);
      b.title = 'Лови!';
      b.innerHTML = '<svg class="clk-gold-ring" viewBox="0 0 40 40" aria-hidden="true"><circle cx="20" cy="20" r="18"/></svg>' + jugHtml;
      b.addEventListener('contextmenu', (e) => e.preventDefault());
      document.body.appendChild(b);
      return b;
    };
    st.flyFall = mk('clk-fly-fall', 'Глек падає з полиці — лови!', '<span class="clk-fly-jug"></span>');
    st.flyFallJug = st.flyFall.querySelector('.clk-fly-jug');
    st.flyGold = mk('clk-fly-gold', 'Розписний глек — лови!', jugSvg('golden', 'clk-gold-jug', 'flygold'));
    st.flyFall.addEventListener('pointerdown', (e) => grabFall(st, e));
    st.flyGold.addEventListener('click', (e) => catchGolden(st, e));
  }

  /// Глек з полиці в шарі поверх: з'являється з тим самим залишком часу (від'ємна затримка анімації, як на сцені).
  function flyFall(st, on, f, now) {
    const b = st.flyFall;
    if (!b) return;
    if (!on) { if (!b.hidden) b.hidden = true; return; }
    if (!b.hidden && st.flyFallAt === f.at) return;
    st.flyFallAt = f.at;
    const band = viewBand();
    st.flyBand = band;
    b.hidden = false;
    const h = b.offsetHeight || 68;
    // Телефон: угорі під шапкою липне мініплашка з «🏺 лови!» — смуга глека починається під нею, щоб не налазити.
    let top = band.top + (window.innerWidth <= 560 ? 56 : 10);
    // Над відкритим вікном кола «✕» лишається вільним, а під мініплашкою — ряд значків вкладок (pinTabs) і «🏺 лови!»:
    // якщо смуга глека йде крізь них (телефон), глек починає політ під ними. Міряємо раз на глек, не щокадру.
    const br = b.getBoundingClientRect();
    const ovx = H.api.overlayOpen(st) && st.ov && st.ov.x;
    const busy = [ovx, ...(st.el ? st.el.querySelectorAll('.clk-pin .clk-pintabs, .clk-pin .clk-pinbtn') : [])];
    for (const el of busy) {
      if (!el) continue;
      const r = el.getBoundingClientRect();
      if (r.height && r.bottom > band.top && r.top < band.bottom && r.right > br.left && r.left < br.right) {
        top = Math.max(top, Math.round(r.bottom) + 8);
      }
    }
    b.style.setProperty('--clk-flytop', top + 'px');
    b.style.setProperty('--clk-flydrop', Math.max(40, band.bottom - 12 - h - top) + 'px');
    b.style.setProperty('--clk-fallms', (f.until - f.at) + 'ms');
    b.style.setProperty('--clk-left', Math.max(0, (f.until - now) / 1000) + 's');
    b.style.animationDelay = (-(now - f.at)) + 'ms';
    b.classList.remove('run');
    void b.offsetWidth;
    b.classList.add('run');
    flySignal(st, 'f' + f.at);
  }

  /// Розписний глек у шарі поверх: стоїть посередині видимої смуги, кільце показує, скільки лишилось.
  function flyGold(st, on, g, now) {
    const b = st.flyGold;
    if (!b) return;
    if (!on) { if (!b.hidden) b.hidden = true; return; }
    if (!b.hidden && st.flyGoldAt === g.at) return;
    st.flyGoldAt = g.at;
    const band = viewBand();
    b.hidden = false;
    b.style.setProperty('--clk-flymid', Math.round((band.top + band.bottom) / 2 - 34) + 'px');
    b.style.setProperty('--clk-left', Math.max(0, (g.until - now) / 1000) + 's');
    b.classList.remove('run');
    void b.offsetWidth;
    b.classList.add('run');
    flySignal(st, 'g' + g.at);
  }

  /// Тихий сигнал, що глек з'явився, а сцени не видно: раз на глек, звуком гри (він сам мовчить, коли звук вимкнено).
  function flySignal(st, key) {
    if (st.flySaid === key) return;
    st.flySaid = key;
    H.api.sfx('fall-aside');
  }

  /// Мініплашка (телефон, коло прокручене з екрана) сама ловить, поки в шарі летить глек: поруч із «⤒ до кола»
  /// з'являється «🏺 лови!» — палець уже вгорі, до смуги справа тягнутись не треба. Окрема кнопка в .clk-pin, а не
  /// правка pinView: видно її рівно тоді, коли видно плашку (вона дитина .clk-pin).
  function pinCatch(st) {
    const b = st.pinLovy;
    if (!b) return;
    const kind = st.flyFall && !st.flyFall.hidden ? 'fall' : st.flyGold && !st.flyGold.hidden ? 'gold' : '';
    if (b.dataset.kind === kind) return;          // щокадру — DOM лише на зміні
    b.dataset.kind = kind;
    b.hidden = !kind;
  }

  function mountPinLovy(st) {
    const pin = st.el.querySelector('.clk-pin');
    if (!pin) return;
    const b = document.createElement('button');
    b.type = 'button';
    b.className = 'clk-pinlovy';
    b.hidden = true;
    b.dataset.kind = '';
    b.textContent = '🏺 лови!';
    b.setAttribute('aria-label', 'Лови глек!');
    b.addEventListener('click', (e) => {
      if (b.dataset.kind === 'fall') grabFall(st, e);
      else if (b.dataset.kind === 'gold') catchGolden(st, e);
    });
    pin.appendChild(b);
    st.pinLovy = b;
  }

  /// Напис біля шару поверх: «+N» над спійманим, «трісь» унизу смуги.
  function flyPop(st, text, cls, x, y) {
    const el = document.createElement('span');
    el.className = 'clk-flypop ' + cls;
    el.textContent = text;
    el.style.left = Math.round(Math.max(60, Math.min(window.innerWidth - 60, x))) + 'px';
    el.style.top = Math.round(y) + 'px';
    fleeting(document.body, el, 1600);
  }

  // ---------- випадковості на сцені: кіт, зірка, вітер (v9 §A.6) ----------

  /// Кіт збоку: хвіст, тіло, голова з вухами й чотири лапи. Колір веде currentColor — на сцені він рудий.
  const CAT_SVG = '<svg viewBox="0 0 52 34" aria-hidden="true">'
    + '<path class="clk-cat-tail" d="M9 21c-5.5.6-7.6-4.6-4.2-7.6" fill="none" stroke="currentColor" stroke-width="3.2" stroke-linecap="round"/>'
    + '<path d="M9 22h24c4.4 0 8-2.2 8-5.2S37.4 11 33 11H15c-3.6 0-6 2.2-6 5z" fill="currentColor"/>'
    + '<circle cx="42.5" cy="12" r="6.8" fill="currentColor"/>'
    + '<path d="M36.6 7.6l-1.2-5.2 4.9 2.7zM48.4 7.6l1.2-5.2-4.9 2.7z" fill="currentColor"/>'
    + '<g class="clk-cat-legs" fill="currentColor"><rect x="12" y="20" width="3.4" height="10" rx="1.7"/>'
    + '<rect x="19" y="20" width="3.4" height="10" rx="1.7"/><rect x="28" y="20" width="3.4" height="10" rx="1.7"/>'
    + '<rect x="34.5" y="20" width="3.4" height="10" rx="1.7"/></g>'
    + '<circle cx="45.2" cy="10.8" r="1.2" fill="#12100e"/><circle cx="39.6" cy="10.8" r="1.2" fill="#12100e"/>'
    + '<path d="M42.5 14.2l-1.6 1.4h3.2z" fill="#12100e"/></svg>';

  const STAR_SVG = '<svg viewBox="0 0 32 32" aria-hidden="true">'
    + '<path d="M16 1.5l2.8 11.7L30 16l-11.2 2.8L16 30.5l-2.8-11.7L2 16l11.2-2.8z" fill="currentColor"/></svg>';

  /// Один шар на всі три події: кіт і зірка — кнопки (їх ловлять), листя вітру — просто листя.
  function eventsBox(st) {
    if (st.evBox && st.evBox.isConnected) return st.evBox;
    const box = H.api.layer(st, 'front', 'events');
    box.className = 'clk-events';
    box.innerHTML = '<button type="button" class="clk-cat" hidden title="Кіт-мандрівник — погладь його" aria-label="Погладити кота">'
      + CAT_SVG + '</button>'
      + '<button type="button" class="clk-star" hidden title="Зірка впала — загадай бажання" aria-label="Загадати бажання на зірку">'
      + STAR_SVG + '</button>'
      + '<div class="clk-wind" hidden aria-hidden="true">' + '<i></i>'.repeat(9) + '</div>';
    st.evBox = box;
    st.catEl = box.querySelector('.clk-cat');
    st.starEl = box.querySelector('.clk-star');
    st.windEl = box.querySelector('.clk-wind');
    st.catEl.addEventListener('pointerdown', (e) => petCat(st, e));
    st.starEl.addEventListener('pointerdown', (e) => makeWish(st, e));
    // Enter/пробіл з клавіатури дають click без pointerdown; повторний pet після миші відсіює st.catGone / st.starGone.
    st.catEl.addEventListener('click', (e) => petCat(st, e));
    st.starEl.addEventListener('click', (e) => makeWish(st, e));
    for (const b of [st.catEl, st.starEl]) b.addEventListener('contextmenu', (e) => e.preventDefault());
    return box;
  }

  /// Показати чи сховати кожну з трьох подій у її вікно. Розклад знає лише сервер (view.events).
  function paintEvents(st, sn) {
    if (!st.events || !st.el || !st.front) return;
    eventsBox(st);
    const on = st.mine && !guardOn(st);
    const ev = st.events;

    const c = ev.cat;
    const catOn = on && !!c && sn >= c.at && sn <= c.until && st.catGone !== c.at;
    if (st.catEl.hidden === catOn) {
      st.catEl.hidden = !catOn;
      if (catOn) {
        st.catEl.style.setProperty('--clk-catms', (c.until - c.at) + 'ms');
        st.catEl.classList.toggle('back', !!c.dir);
        st.catEl.style.animationDelay = (-(sn - c.at)) + 'ms';
        st.catEl.classList.remove('run');
        void st.catEl.offsetWidth;
        st.catEl.classList.add('run');
        H.api.sfx('cat');
      }
    }

    const s = ev.star;
    const starOn = on && !!s && sn >= s.at && sn <= s.until && st.starGone !== s.at;
    if (st.starEl.hidden === starOn) {
      st.starEl.hidden = !starOn;
      if (starOn) {
        st.starEl.style.left = s.x + '%';
        st.starEl.style.top = s.y + '%';
        st.starEl.style.setProperty('--clk-starms', (s.until - s.at) + 'ms');
        st.starEl.style.animationDelay = (-(sn - s.at)) + 'ms';
        st.starEl.classList.remove('run');
        void st.starEl.offsetWidth;
        st.starEl.classList.add('run');
        H.api.sfx('star');
      }
    }

    const w = ev.wind;
    const blowing = on && !!w && sn >= w.at && sn <= w.until;
    if (st.windEl.hidden === blowing) st.windEl.hidden = !blowing;
    if (blowing && st.windRun !== w.at) {
      st.windRun = w.at;
      H.api.sfx('wind');
      H.api.feed(st, '🌬 Вітер із поля — глина сохне, коло само крутиться', 'wind');
    }
  }

  /// Погладити кота: ховаємо одразу (другий натиск — лише червоний тост), гостинець назве сервер.
  function petCat(st, ev) {
    if (!human(ev) || !st.events || !st.events.cat || !st.mine || guardOn(st)) return;
    if (ev.pointerType === 'mouse' && ev.button !== 0) return;
    ev.preventDefault();
    const c = st.events.cat;
    const sn = serverNow(st);
    if (sn < c.at - 1000 || sn > c.until + CATCH_GRACE_MS) return;
    st.catGone = c.at;
    const r = st.catEl.getBoundingClientRect();
    const sr = st.stage.getBoundingClientRect();
    st.catEl.hidden = true;
    if (sr.width && sr.height) {
      const x = Math.min(88, Math.max(12, ((r.left + r.width / 2 - sr.left) / sr.width) * 100));
      popAt(st, 'мур-р-р…', 'big', x, 68);
      sparks(st, st.fx, 8, false, x, 76);
    }
    order(st, 'pet');
  }

  function makeWish(st, ev) {
    if (!human(ev) || !st.events || !st.events.star || !st.mine || guardOn(st)) return;
    if (ev.pointerType === 'mouse' && ev.button !== 0) return;
    ev.preventDefault();
    const s = st.events.star;
    const sn = serverNow(st);
    if (sn < s.at - 1000 || sn > s.until + CATCH_GRACE_MS) return;
    st.starGone = s.at;
    st.starEl.hidden = true;
    popAt(st, 'загадав!', 'big', Math.min(84, Math.max(14, s.x)), Math.max(6, s.y - 6));
    sparks(st, st.fx, 14, true, s.x, s.y);
    order(st, 'wish');
  }

  /// Майстер кивнув і заплатив: золотий дощ над колом і велике «+N» (v9 §A.1).
  function eyeRain(st, gain) {
    popAt(st, '+' + short(gain), 'big', 50, 26);
    for (let i = 0; i < 24; i++) {
      const el = document.createElement('i');
      el.className = 'clk-rain';
      el.style.left = (4 + Math.random() * 92) + '%';
      el.style.animationDelay = Math.round(Math.random() * 700) + 'ms';
      el.style.setProperty('--clk-rainms', Math.round(900 + Math.random() * 700) + 'ms');
      fleeting(st.fx, el, 2400);
    }
    sparks(st, st.fx, 14, true, 50, 42);
    H.api.sfx('eye-pay');
  }

  function paintRival(st, liveTotal) {
    const rows = st.board;
    let text = '';
    if (rows && rows.length) {
      const me = (st.ctx && st.ctx.me && st.ctx.me.nick || '').trim().toLowerCase();
      const others = rows.filter((r) => (r.nick || '').trim().toLowerCase() !== me && r.best > 0)
        .sort((a, b) => b.best - a.best);
      if (others.length) {
        const above = others.filter((r) => r.best > liveTotal);
        const place = above.length + 1;
        const medal = ['🥇', '🥈', '🥉'][place - 1] || '#' + place;
        // Нік не відмінюється («до Микола»), тож будуємо речення з ним у називному.
        if (above.length) {
          const next = above[above.length - 1];
          text = medal + ' ' + next.nick + ' попереду на ' + short(next.best - liveTotal);
        } else {
          text = medal + ' ти перший · ' + others[0].nick + ' позаду на ' + short(liveTotal - others[0].best);
        }
      }
    }
    // Різниця з суперником міняється щотакту, а кожен новий текст над сценою — розкладка сторінки: пишемо не частіше
    // разу на секунду. Змінилось місце чи суперник (ключ — усе до «на …») — одразу.
    const key = text.replace(/ на .*$/, '');
    const now = Date.now();
    if (st.rival.textContent !== text && (key !== st.rivalKey || now - st.rivalAt >= RIVAL_MS)) {
      st.rivalKey = key;
      st.rivalAt = now;
      st.rival.textContent = text;
      if (st.rival.hidden !== !text) st.rival.hidden = !text;
    }
  }

  function paintFire(st, liveTotal) {
    const all = Math.floor(Math.sqrt(Math.max(0, liveTotal) / STAMP_UNIT));
    // Приріст — від клейм за глеки: Тавро й наука лежать окремо (stampsExtra), і обпал їх не забирає.
    const gain = Math.max(0, all - Math.max(0, st.stamps - st.stampsExtra));
    const from = all * all * STAMP_UNIT;
    const to = (all + 1) * (all + 1) * STAMP_UNIT;
    const pct = Math.max(0, Math.min(100, ((liveTotal - from) / (to - from)) * 100));
    const f = st.fire;
    // Масштабом, а не шириною (clicker.css, блок «швидкість»): смужка тягнеться щотакту, а ширина — це розкладка.
    const bar = 'scaleX(' + (pct / 100).toFixed(3) + ')';
    if (f._bar.style.transform !== bar) f._bar.style.transform = bar;
    const next = 'наступне клеймо — на ' + potsShort(to) + ' за весь час (зараз ' + short(liveTotal) + ')';
    if (f._next.textContent !== next) f._next.textContent = next;

    const armed = st.fireArmed && Date.now() < st.fireArmed;
    if (!armed) st.fireArmed = 0;
    const label = gain < 1 ? '🔥 Почати наново — ще рано'
      : armed ? 'Точно? Глеки й верстати згорять — ще раз'
      : '🔥 Почати наново: +' + stampsShort(gain);
    if (f._btn.textContent !== label) f._btn.textContent = label;
    const off = !st.mine || gain < 1;
    if (f._btn.disabled !== off) f._btn.disabled = off;
    f._btn.classList.toggle('armed', !!armed);
    const iron = gain < 1 ? 0 : st.stampIron;
    const sci = gain < 1 ? 0 : scienceNow(st, all, st.stampsExtra + iron);
    const after = gain < 1 ? '' : afterFire(st, gain + iron + sci, sci);
    if (f._after.textContent !== after) f._after.textContent = after;
  }

  /// Що обпал зробить із бонусом: «після обпалу: +174 % → +245 % до всього (дохід +40 %)» — щоб видно було, чи
  /// варто палити, а не лише скільки клейм упаде. Після тисячі кожне нове клеймо важить дедалі менше.
  function afterFire(st, add, sci) {
    const was = stampPct(st, st.stamps);
    const will = stampPct(st, st.stamps + add);
    const ratio = (100 + will) / (100 + was);
    const grow = ratio >= 2 ? '×' + dec(ratio) : '+' + dec((ratio - 1) * 100) + ' %';
    return 'після обпалу: +' + dec(was) + ' % → +' + dec(will) + ' % до всього (дохід ' + grow + ')'
      + (sci > 0 ? ' · 🎓 і ще +' + stampsShort(sci) + ' від науки майстра' : '');
  }

  /// Скільки дасть наука майстра на обпалі просто зараз — та сама формула, що Clicker.ScienceFor на сервері:
  /// чверть різниці з найкращим гончарем округи, не більше ніж двічі по клеймах за глеки. 0 — ще не минуло
  /// 20 годин, нема від кого вчитись або ти й так попереду.
  function scienceNow(st, natural, extra) {
    const sc = st.science;
    if (!sc || !sc.top) return 0;
    if (sc.readyAt && Date.parse(sc.readyAt) > serverNow(st)) return 0;
    const gap = sc.top - natural - extra;
    if (!(gap > 0)) return 0;
    return Math.min(Math.floor(gap * (sc.share || 0)), (sc.cap || 0) * natural);
  }

  /// Цикл живе від mount до unmount. Картку каркас монтує ще до того, як вставить у сторінку (повторне
  /// відкриття з готовим видом), тож «не в документі» — це не кінець, а «ще не видно»: чекаємо, не малюючи.
  /// Раніше цикл на цьому й зупинявся — і пасив між діями не тікав, а розписний глек так і не з'являвся б.
  function loop(st) {
    if (!st.el) { st.raf = 0; return; }
    if (visible(st)) paint(st);
    // Картку догорнули далеко з екрана (але вона на сторінці) — глеки однаково летять, поверх усього (записка №29).
    else if (st.stageLaid && st.el.isConnected && !document.hidden) { paintGolden(st); paintFall(st); }
    // Картки нема на сторінці (інша сторінка сайту) — шару теж нема (aside()).
    else if (!document.hidden) flyOff(st);
    st.raf = requestAnimationFrame(() => loop(st));
  }

  /// Елемент, що живе рівно доти, доки триває його анімація. У фоновій вкладці анімації не крутяться, а отже й
  /// animationend не прилетить — прибираємо і за часом, інакше «+N» назбирувались би там сотнями до самого повернення.
  function fleeting(host, el, ms) {
    el.addEventListener('animationend', () => el.remove());
    setTimeout(() => el.remove(), ms);
    host.appendChild(el);
  }

  /// «+12», що злітає над колом; при розкрученому колі — гарячіше й більше.
  function pop(st, amount, cls) {
    if (st.pops.childElementCount > 12) return;   // палець швидший за око: більше однаково не роздивитись
    const el = document.createElement('span');
    el.className = 'clk-pop' + (cls ? ' ' + cls : '');
    el.textContent = '+' + short(amount);
    el.style.left = (32 + Math.random() * 36) + '%';
    fleeting(st.pops, el, 2000);
  }

  /// Напис у довільному місці сцени (x, y — у відсотках): «+N» над спійманим глеком, «трісь» над розбитим.
  function popAt(st, text, cls, x, y) {
    const el = document.createElement('span');
    el.className = 'clk-pop ' + cls;
    el.textContent = text;
    el.style.left = x + '%';
    el.style.top = y + '%';
    fleeting(st.fx, el, 2500);
  }

  /// Бризки глини (або золоті іскри) з точки (x, y у відсотках host-а; без них — з центру кола).
  function sparks(st, host, count, gold, x, y) {
    if (host.childElementCount > 48) return;
    for (let i = 0; i < count; i++) {
      const el = document.createElement('i');
      el.className = 'clk-spark' + (gold ? ' gold' : '');
      if (x != null) { el.style.left = x + '%'; el.style.top = y + '%'; }
      const a = Math.random() * Math.PI * 2;
      const d = (gold ? 50 : 34) + Math.random() * (gold ? 70 : 40);
      el.style.setProperty('--dx', (Math.cos(a) * d).toFixed(0) + 'px');
      el.style.setProperty('--dy', (Math.sin(a) * d - 24).toFixed(0) + 'px');
      fleeting(host, el, 1200);
    }
  }

  /// Глек долетів до долівки: черепки навсібіч і тихе «трісь».
  /// Що сталось із серією, каже сервер наперед (fall.miss, ті самі гілки, що в Sync): обірвалась, фартух уберіг
  /// чи серії й не було (записка №29). fly — глек летів у шарі поверх: напис там, де його й бачили.
  /// Гончаря не було (глек злетів після його останньої справжньої дії, а від неї вже минуло понад EVENT_GAP_MS) —
  /// сервер серію не рве й фартуха не чіпає (Sync, гілка «порожня хата»): тоді просто «трісь».
  function shatter(st, x, fly) {
    const n = st.fallStreak || 0;
    const f = st.fall;
    const empty = st.fallSeen > 0 && f && f.at > st.fallSeen && serverNow(st) - st.fallSeen > EVENT_GAP_MS;
    const text = empty ? 'трісь'
      : st.fallMiss === 'apron' ? 'трісь — фартух уберіг серію'
      : st.fallMiss === 'streak' && n > 0 ? 'трісь… серія ' + n + ' обірвалась' : 'трісь';
    if (fly) {
      H.api.sfx('break', { x });
      const band = st.flyBand || viewBand();
      flyPop(st, text, 'miss', window.innerWidth - 90, band.bottom - 70);
      return;
    }
    for (let i = 0; i < 8; i++) {
      const el = document.createElement('i');
      el.className = 'clk-shard';
      el.style.left = x + '%';
      el.style.top = '90%';
      const a = -Math.PI * (0.15 + Math.random() * 0.7);
      const d = 24 + Math.random() * 46;
      el.style.setProperty('--dx', (Math.cos(a) * d).toFixed(0) + 'px');
      el.style.setProperty('--dy', (Math.sin(a) * d + 40).toFixed(0) + 'px');
      el.style.setProperty('--rot', Math.round(Math.random() * 360 - 180) + 'deg');
      fleeting(st.fx, el, 1500);
    }
    H.api.sfx('break', { x });
    popAt(st, text, 'miss', Math.min(70, Math.max(20, x)), 78);
  }

  // ---------- дії ----------

  function flush(st) {
    if (!st.hands.length || !st.ctx) return;
    // Поки майстер чекає, сервер кліків однаково не зарахує: не шлемо і не обіцяємо їх на лічильнику.
    if (guardOn(st)) { st.hands.length = 0; st.handsGain = 0; return; }
    const c = st.hands.splice(0, MAX_BATCH);
    const n = c.length;
    // Скільки з обіцяного на лічильнику полетіло з цією пачкою: усе, якщо це був увесь хвіст, інакше частка.
    const g = st.hands.length ? Math.min(st.handsGain, c.reduce((s, h) => s + (h[5] || 0), 0)) : st.handsGain;
    st.handsGain = Math.max(0, st.handsGain - g);
    // Серверу — лише п'ять полів відбитка; шосте (наша оцінка глеків за клік) лишається тут.
    const wire = c.map((h) => h.slice(0, 5));
    st.inflight += n;
    st.inflightGain += g;
    const back = () => { st.inflight = Math.max(0, st.inflight - n); st.inflightGain = Math.max(0, st.inflightGain - g); };
    // Кліки, що вже полетіли, знімає з рахунку сам вид (див. update): вид і відповідь приходять різними
    // кадрами вебсокета, і якби ми чекали відповіді, між ними лічильник встигав би показати їх двічі.
    // Лишається тільки невдача: тоді виду не буде взагалі, і порахувати назад мусимо ми.
    // pv — сервер тоді шле худий вид (тексти магазину — раз, у shopCatalog).
    st.ctx.act('spin', { c: wire, pv: PV }).then((r) => { if (!r || !r.ok) back(); }, back);
  }

  /// Точка на колі 0…1000 — так її чекає сервер.
  function spot(st, ev) {
    const r = st.wheel.getBoundingClientRect();
    const at = (v, from, size) => Math.max(0, Math.min(1000, Math.round(((v - from) / (size || 1)) * 1000)));
    return [at(ev.clientX, r.left, r.width), at(ev.clientY, r.top, r.height)];
  }

  /// Натиснули на коло: запам'ятовуємо мить і точку, а кліком це стане, коли відпустять.
  function pressWheel(st, ev) {
    if (!human(ev) || (ev.pointerType === 'mouse' && ev.button !== 0)) return;
    const [x, y] = spot(st, ev);
    st.downs.set(ev.pointerId, { t: ev.timeStamp, x, y, src: SRC[ev.pointerType] ?? SRC.mouse });
  }

  /// Відпустили: це клік, якщо натискали саме на коло, справжньою рукою й не довше за HOLD_MS. Два пальці по черзі —
  /// два кліки: кожен палець має свій pointerId.
  function releaseWheel(st, ev) {
    const d = st.downs.get(ev.pointerId);
    if (!d) return;
    st.downs.delete(ev.pointerId);
    if (!human(ev) || ev.type === 'pointercancel' || ev.timeStamp - d.t > HOLD_MS) return;
    spin(st, d.t, ev.timeStamp - d.t, d.x, d.y, d.src);
  }

  /// Один справжній клік: відбиток у пачку, розгін +1, «+N» над колом і бризки. downAt і press — у мс шкали event.timeStamp.
  function spin(st, downAt, press, x, y, src) {
    if (!st.ctx || !st.mine || guardOn(st)) return;
    const dt = st.lastDown ? Math.max(0, Math.min(60000, Math.round(downAt - st.lastDown))) : 60000;
    st.lastDown = downAt;
    // Те саме відро дозволів, що й на сервері: понад дванадцять кліків за секунду він однаково не візьме,
    // тож і малювати їх не варто — інакше лічильник обіцяв би те, чого потім не дорахується.
    const now = Date.now();
    st.tokens = Math.min(MAX_BATCH, st.tokens + ((now - st.tokensAt) / 1000) * MAX_BATCH);
    st.tokensAt = now;
    if (st.tokens < 1) return;
    st.tokens -= 1;
    // Розгін — як на сервері: спад від останнього кліка, множник на півкліка вперед, потім +1 гарячий.
    const heat = heatNow(st);
    const mult = momentumOf(st, heat + 0.5);
    st.heat = heat + 1;
    st.heatAt = now;
    const gain = clickNow(st) * mult;
    st.hands.push([dt, Math.max(0, Math.round(press)), x, y, src, gain]);
    st.handsGain += gain;
    // Більше трьох пачок не копимо: якщо зв'язок завис, хвіст однаково не долетів би.
    if (st.hands.length > MAX_BATCH * 3) {
      const dropped = st.hands.splice(0, st.hands.length - MAX_BATCH * 3);
      st.handsGain = Math.max(0, st.handsGain - dropped.reduce((s, h) => s + (h[5] || 0), 0));
    }
    const hot = st.momentumMax > 1 && mult >= 1 + (st.momentumMax - 1) * 0.9;
    pop(st, gain, hot ? 'hot' : '');
    sparks(st, st.sparks, hot ? 5 : 3, hot);
    H.api.sfx('clay', { hot, heat: Math.min(1, st.heat / st.heatFull) });
    // Сервер ціною кліка вважає мить, коли пачка ДОЛЕТІЛА. Під кінець натхнення чи ярмарку 700 мс чекання
    // перетворили б «+25×» на екрані на «+1×» на сервері — тож останні півтори секунди бонусу шлемо одразу.
    const sn = serverNow(st);
    const ends = [st.inspireUntil, st.fairUntil].filter((t) => t > sn);
    if (ends.length && Math.min(...ends) - sn < 1500) flush(st);
    knock(st);
    paint(st);
  }

  const REDUCED_MQ = window.matchMedia ? window.matchMedia('(prefers-reduced-motion: reduce)') : null;
  /// «Стук» кола на клік — ті самі кадри, що clkhit у clicker.css, але через WAAPI: перезапуск CSS-класу вимагав
  /// void offsetWidth, тобто синхронної розкладки всієї сторінки на кожен клік (десяте оновлення, §10).
  const KNOCK = [{ transform: 'none', easing: 'ease-out' }, { transform: 'scale(1.07)', offset: 0.4, easing: 'ease-out' }, { transform: 'none' }];
  function knock(st) {
    const w = st.wheel;
    if (!w || !w.animate || (REDUCED_MQ && REDUCED_MQ.matches)) return;
    try {
      if (st.knockAnim) st.knockAnim.cancel();
      st.knockAnim = w.animate(KNOCK, { duration: 180 });
    } catch { /* браузер без WAAPI — коло просто без стуку */ }
  }

  /// Який звук дає дія гравця (жива хата озвучує; без неї — тиша).
  const ORDER_SFX = {
    buy: 'buy', mark: 'buy', paint: 'buy', tool: 'buy', adorn: 'buy', knead: 'buy', secret: 'buy',
    sell: 'coins', take: 'coins', bazaar: 'coins', wear: 'tap', form: 'tap', catch: 'catch', grab: 'grab', fire: 'prestige',
  };

  /// Покупка й продаж рахуються від того, що вже долетіло до сервера, тож накопичені кліки шлемо першими.
  function order(st, action, payload) {
    if (!st.ctx || !st.mine) return;
    if (ORDER_SFX[action]) H.api.sfx(ORDER_SFX[action], { action });
    flush(st);
    st.ctx.act(action, payload);
  }

  function catchGolden(st, ev) {
    if (!human(ev) || !st.golden || !st.mine || guardOn(st)) return;
    st.goldenGone = st.golden.at;      // ховаємо одразу: другий клік по тому самому глеку — лише червоний тост
    st.gold.hidden = true;
    if (st.flyGold && !st.flyGold.hidden) {
      const r = st.flyGold.getBoundingClientRect();
      st.flyGold.hidden = true;
      flyPop(st, '✨', 'big', r.left + r.width / 2, r.top - 10);
    }
    order(st, 'catch');
  }

  /// Спіймали глек з полиці: ховаємо, малюємо «+N» (суму знає вид — fall.gain) і золоті іскри там, де він був.
  function grabFall(st, ev) {
    if (!human(ev) || !st.fall || !st.mine || guardOn(st)) return;
    if (ev.pointerType === 'mouse' && ev.button !== 0) return;
    ev.preventDefault();
    const f = st.fall;
    st.fallGone = f.at;
    // Спіймали в шарі поверх (чи плашкою «лови!»): «+N» там, де його бачили, а не на схованій сцені.
    if (st.flyFall && !st.flyFall.hidden) {
      const r = st.flyFall.getBoundingClientRect();
      st.flyFall.hidden = true;
      flyPop(st, '+' + short(st.fallGain), 'big', r.left + r.width / 2, r.top - 10);
      order(st, 'grab');
      return;
    }
    const sr = st.stage.getBoundingClientRect();
    const r = st.fallEl.getBoundingClientRect();
    st.fallEl.hidden = true;
    if (sr.width && sr.height) {
      const x = ((r.left + r.width / 2 - sr.left) / sr.width) * 100;
      const y = ((r.top + r.height / 2 - sr.top) / sr.height) * 100;
      popAt(st, '+' + short(st.fallGain), 'big', x, Math.max(6, y - 8));
      sparks(st, st.fx, 14, true, x, y);
    }
    order(st, 'grab');
  }

  function fire(st) {
    if (!st.mine) return;
    if (!st.fireArmed || Date.now() > st.fireArmed) {
      // Обпал не відкотиш — тож перший натиск лише питає.
      st.fireArmed = Date.now() + 4000;
      paintFire(st, st.total + (st.shown - st.base));
      return;
    }
    st.fireArmed = 0;
    order(st, 'fire');
  }

  function setTab(st, tab, remember = true) {
    if (!st.panes[tab] || isGated(st, tab)) tab = 'shop';
    st.tab = tab;
    if (remember) storeSet('clk.tab', tab);
    for (const b of st.tabs.querySelectorAll('[data-tab]')) b.classList.toggle('active', b.dataset.tab === tab);
    for (const [k, p] of Object.entries(st.panes)) p.hidden = k !== tab;
    // Подивився — світитись більше не треба.
    const b = st.tabs.querySelector('[data-tab="' + tab + '"]');
    if (b && b.classList.contains('fresh')) { b.classList.remove('fresh'); storeSet('clk.seen.' + tab, '1'); }
    st.slowAt = 0;
  }

  const isGated = (st, key) => {
    const b = st.tabs && st.tabs.querySelector('[data-tab="' + key + '"]');
    return !!b && b.hidden;
  };

  /// Підпис ярлика = базова назва вкладки плюс короткі нотатки частин, за абеткою їхніх ключів.
  /// «🏛», «🏗» і «♨» без VS16 Windows малює чорно-білим значком тексту — у золотому бейджі вкладки це «▬1» (09.10).
  /// Дописуємо селектор кольорового емодзі, якщо його ще нема.
  const colorEmoji = (s) => s.replace(/[\u2668\u{1F3D7}\u{1F3DB}](?!\uFE0F)/gu, '$&\uFE0F');

  function labelTab(st, key) {
    const b = st.tabs && st.tabs.querySelector('[data-tab="' + key + '"]');
    if (!b) return;
    const notes = st.tabNotes[key] || {};
    const best = Object.keys(notes).sort().map((k) => notes[k]).filter((n) => n.text)
      .sort((a, b) => a.prio - b.prio)[0];
    const label = st.tabText[key] || key;
    const note = best ? colorEmoji(best.text) : '';
    if (b._label === label && b._note === note) return;
    b._label = label;
    b._note = note;
    // Ярлик — значок · слово · нотатка окремими шматками (F8): на широкій полиці це той самий рядок «🏺 Ремесло · 📜3»,
    // а у вузькій (телефон, низький ПК) нотатка стає бейджем над значком, і п'ять вкладок лягають в один ряд.
    const sp = label.indexOf(' ');
    const ico = sp > 0 ? label.slice(0, sp) : '';
    const word = sp > 0 ? label.slice(sp + 1) : label;
    if (!b._parts) {
      b.textContent = '';
      b._parts = ['clk-tico', 'clk-tword', 'clk-tnote'].map((c) => {
        const e = document.createElement('span');
        e.className = c;
        b.appendChild(e);
        return e;
      });
      b._parts[0].setAttribute('aria-hidden', 'true');
    }
    b._parts[0].textContent = ico;
    b._parts[1].textContent = word;
    b._parts[2].textContent = note;
    b.title = note ? word + ' · ' + note : word;
  }

  /// Перебрати гейти: закриті вкладки ховаємо, щойно відкриті — світимо й кажемо про це в стрічці подій.
  /// Перший прохід (гравець із прогресом) нічого не оголошує: у нього все й так уже відкрите.
  function gateTabs(st, v) {
    if (!st.tabs) return;
    for (const [key, fn] of Object.entries(st.gates)) {
      const b = st.tabs.querySelector('[data-tab="' + key + '"]');
      if (!b) continue;
      let on = true;
      try { on = !!fn(st, v || {}); } catch (e) { console.error('[clicker] гейт ' + key, e); on = true; }
      if (b.hidden !== !on) {
        b.hidden = !on;
        if (!on) { if (st.tab === key) setTab(st, 'shop', false); continue; }
        if (storeGet('clk.seen.' + key, '') !== '1') {
          b.classList.add('fresh');
          if (st.gateReady) H.api.feed(st, 'відкрилось: ' + (st.tabText[key] || key), 'open');
        }
      }
    }
    st.gateReady = true;
  }

  function setMode(st, mode) {
    st.mode = mode;
    storeSet('clk.mode', mode);
    for (const b of st.modes.querySelectorAll('[data-mode]')) b.classList.toggle('active', b.dataset.mode === mode);
    st.slowAt = 0;                     // ціни за ×1/×10/макс — того ж кадру
    paint(st);
  }

  // ---------- полиці ----------

  /// Перемалювати секцію лише тоді, коли її HTML справді змінився: кнопки під пальцем не мають зникати щопачки.
  /// Розгорнуте ▾ (<details>) лишається розгорнутим: раніше кожна зміна числа всередині згортала його — і все,
  /// що нижче, стрибало. Ключ — data-key, а без нього — клас і порядковий номер серед таких самих.
  function swap(el, html) {
    if (el._sig === html) return false;
    el._sig = html;
    const keyOf = (d, i) => d.dataset.key || d.className + '#' + i;
    const keys = (fn) => {
      const seen = {};
      for (const d of el.querySelectorAll('details')) { const i = (seen[d.className] = (seen[d.className] || 0) + 1); fn(d, keyOf(d, i)); }
    };
    const open = new Set();
    keys((d, k) => { if (d.open) open.add(k); });
    el.innerHTML = html;
    if (open.size) keys((d, k) => { if (open.has(k)) d.open = true; });
    return true;
  }

  function shop(st, ctx) {
    const esc = ctx.esc;
    const ups = st.ups;
    const keys = Object.keys(ups);
    // «Найвигідніше» — найкоротша окупність серед того, що видно й ще можна купити.
    let best = '', bestPay = Infinity;
    for (const k of keys) {
      const u = ups[k];
      if (!u.open || !(u.gain > 0) || (u.max > 0 && u.level >= u.max)) continue;
      const pay = u.price / u.gain;
      if (pay < bestPay) { bestPay = pay; best = k; }
    }
    let teaser = '';
    // Докачане до межі («досить») не купиш — у пізній грі шість таких рядків (~330 px на телефоні) стояли між тим, що
    // купується щохвилини. Їх — в одну згортку в кінці списку; відкрита чи ні — пам'ятає swap() за data-key.
    const done = [];
    const cards = keys.filter((k) => {
      // !== false, а не просто open: старий сервер (хвилина деплою) цього поля не шле — показуємо все.
      if (ups[k].open !== false) return true;
      if (!teaser && ups[k].kind === 'idle') teaser = k;
      return false;
    }).map((k) => {
      const u = ups[k];
      const maxed = u.max > 0 && u.level >= u.max;
      const pay = u.gain > 0 && !maxed ? span(u.price / u.gain) : '';
      const x2 = u.boost > 1 ? '<span class="clk-x2">×' + u.boost + '</span> · ' : '';
      // Наступна віха (v10): «віха на 150» — ціль, до якої варто докупити рівні.
      const nm = u.nextMark;
      const next = nm ? ' · наступна віха на ' + nm.level + ': «' + nm.name + '» — ' + nm.desc : '';
      // Компактний рядок: значок · назва з рівнем і описом · ціна; смужка знизу — скільки ціни вже назбирано.
      const icon = H.api.upIcon ? H.api.upIcon(k) : '';
      const html = '<button type="button" class="clk-up' + (k === best ? ' best' : '') + (u.kind === 'skill' ? ' skill' : '') + (maxed ? ' maxed' : '')
        + '" data-buy="' + esc(k) + '" title="' + esc(u.name + ' — ' + u.desc + (pay ? ' · окупиться за ' + pay : '') + next) + '" disabled>'
        // Рівень — плашкою на значку (як лічильник будівель), щоб назва мала весь рядок.
        + '<span class="clk-uico">' + icon + '<span class="clk-lvl' + (u.level ? '' : ' zero') + '">' + (u.level ? u.level + (u.max > 0 ? '/' + u.max : '') : '0') + '</span></span>'
        + '<span class="clk-umain"><span class="clk-uname"><b>' + esc(u.name) + '</b></span>'
        + '<span class="clk-udesc">' + x2 + esc(u.desc) + (nm && u.level >= nm.level * 0.6
          ? ' · <span class="clk-nextmark">віха на ' + nm.level + '</span>' : '') + '</span></span>'
        // Праворуч ціна, під нею дрібно — за скільки окупиться (★ — найвигідніше зараз).
        + '<span class="clk-uright"><span class="clk-price"></span>'
        + (pay ? '<span class="clk-pay">' + (k === best ? '★ ' : '') + 'окуп. ' + pay + '</span>' : '') + '</span>'
        + '<i class="clk-ubar"><i></i></i>'
        + '</button>';
      if (maxed) { done.push(html); return ''; }
      return html;
    }).join('');
    // Гончарі світу (v11): щабель відмикає будова Толоки, а не попередній рівень.
    const gate = teaser && st.shopCat && st.shopCat.gates && st.shopCat.gates[teaser];
    const more = teaser
      ? '<div class="clk-teaser muted small">' + (H.api.upIcon ? '<span class="clk-uico locked">' + H.api.upIcon(teaser) + '</span>' : '')
        + (gate && ups[prevIdle(ups, teaser)].level > 0
          ? '⚓ «' + esc(ups[teaser].name || teaser) + '» відкриє будова Толоки «' + esc(gate.name) + '» — вкладка «🤝 Село»'
          : 'Далі на драбині ще є верстати: наступний відкриється після першого рівня «' + esc(ups[prevIdle(ups, teaser)].name) + '»')
        + '</div>'
      : '';
    const fold = done.length
      ? '<details class="clk-done" data-key="done"><summary>Докачано · ' + done.length + '</summary><div class="clk-donebody">'
        + done.join('') + '</div></details>'
      : '';
    if (swap(st.shop, cards + more + fold)) {
      st.buys = [...st.shop.querySelectorAll('[data-buy]')];
      for (const b of st.buys) {
        b._price = b.querySelector('.clk-price');
        b._bar = b.querySelector('.clk-ubar i');
        b._pct = -1;
        b.onclick = () => {
          const u = st.ups[b.dataset.buy];
          const a = afford(u, st.shown, st.mode);
          order(st, 'buy', { key: b.dataset.buy, n: st.mode === 'max' ? MAX_BUY : Math.max(1, a.n) });
        };
      }
      paint(st);
    }

    const marks = st.markList.slice().sort((a, b) => a.price - b.price);
    const mhtml = marks.length
      ? '<div class="clk-sub">Віхи' + (st.marksAll ? ' · ' + num(st.marksOwned) + ' з ' + num(st.marksAll) : '')
        + '<span class="muted small"> · одноразово, назавжди (до обпалу; з «Пам\'яттю рук» лишаються)</span></div><div class="clk-marks">'
        + marks.map((m) => '<button type="button" class="clk-mark" data-mark="' + esc(m.key) + '" data-price="' + m.price + '" title="'
          + esc(m.name + ' — ' + m.desc) + '" disabled>'
          + (H.api.upIcon ? '<span class="clk-uico">' + H.api.upIcon(m.on || String(m.key).split(':')[0]) + '</span>' : '')
          + '<span class="clk-umain"><b>' + esc(m.name) + '</b><span class="clk-udesc">' + esc(m.desc) + '</span></span>'
          + '<span class="clk-price">' + short(m.price) + '</span></button>').join('')
        + '</div>'
      : '';
    if (swap(st.marks, mhtml)) {
      st.markBtns = [...st.marks.querySelectorAll('[data-mark]')];
      for (const b of st.markBtns) b.onclick = () => order(st, 'mark', { key: b.dataset.mark });
      paint(st);
    }
  }

  function prevIdle(ups, key) {
    const keys = Object.keys(ups);
    for (let i = keys.indexOf(key) - 1; i >= 0; i--) if (ups[keys[i]].kind === 'idle') return keys[i];
    return key;
  }

  function styles(st, ctx) {
    const esc = ctx.esc;
    const list = st.styleList;
    const owned = list.filter((s) => s.owned).length;
    // Одинадцяте оновлення: розписи світу (s.tier — щабель гончарів світу) — окремим рядком. Поки нема першого рівня
    // свого щабля, розпис не купиш: сіра картка «привезуть із …» без кнопки (назва щабля — з каталогу магазину).
    const home = list.filter((s) => !s.tier);
    const world = list.filter((s) => s.tier);
    // Довгі назви світу («Цзиндечженська») на телефоні не влазять у картку: м'який перенос перед «-ська/-цька».
    const name = (s) => (s.tier ? esc(s.name).replace(/([^\s&;]{5,})(ськ|цьк)/g, '$1&shy;$2') : esc(s.name));
    const card = (s) => {
      const on = st.wear === s.key;
      const u = st.ups && st.ups[s.tier];
      if (s.tier && !s.owned && !(u && u.level > 0)) {
        return '<div class="clk-style clk-far" title="' + esc(s.name + ' — продадуть, щойно матимеш перший рівень щабля') + '">'
          + jugSvg(s.key, 'clk-mini locked', 's-' + s.key) + '<b>' + name(s) + '</b>'
          + '<span class="clk-price done clk-farnote">🚢 привезуть із «' + esc((u && u.name) || s.tier) + '»</span></div>';
      }
      return '<button type="button" class="clk-style' + (s.owned ? ' owned' : '') + (on ? ' on' : '') + '" data-style="' + esc(s.key)
        + '" data-owned="' + (s.owned ? 1 : 0) + '" data-price="' + s.price + '" disabled>'
        // Некуплений розпис видно приглушеним: купують те, що бачать, а не сірий силует.
        + jugSvg(s.key, 'clk-mini' + (s.owned ? '' : ' locked'), 's-' + s.key)
        + '<b>' + name(s) + '</b>'
        + '<span class="clk-price' + (s.owned ? ' done' : '') + '">' + (on ? 'на колі' : s.owned ? 'поставити' : short(s.price)) + '</span>'
        + '</button>';
    };
    const worldOwned = world.filter((s) => s.owned).length;
    const html = '<div class="clk-sub">Розписи · ' + (owned - worldOwned) + ' з ' + home.length
      + '<span class="muted small"> · кожен +5 % до всього, лишаються й після обпалу</span></div>'
      + '<div class="clk-styles">' + home.map(card).join('') + '</div>'
      + (world.length ? '<div class="clk-sub clk-worldsub">🌍 Розписи світу · ' + worldOwned + ' з ' + world.length
        + '<span class="muted small"> · теж +5 % до всього; кожен привозять, коли маєш перший рівень його щабля</span></div>'
        + '<div class="clk-styles clk-styles-world">' + world.map(card).join('') + '</div>' : '')
      + (owned ? '<button type="button" class="ghost small clk-plain"' + (st.wear ? '' : ' disabled') + '>Простий глиняний на колі</button>' : '');
    if (swap(st.styles, html)) {
      st.styleBtns = [...st.styles.querySelectorAll('[data-style]')];
      for (const b of st.styleBtns) {
        b.onclick = () => (b.dataset.owned === '1'
          ? order(st, 'wear', { key: st.wear === b.dataset.style ? '' : b.dataset.style })
          : order(st, 'paint', { key: b.dataset.style }));
      }
      const plain = st.styles.querySelector('.clk-plain');
      if (plain) plain.onclick = () => order(st, 'wear', { key: '' });
      paint(st);
    }
  }

  function firePane(st, ctx) {
    const esc = ctx.esc;
    const v = ctx.view || {};
    const bonus = dec(stampPct(st, st.stamps));
    const cap = v.stampCap || 0;
    // Записка #28 (Smaug, 05.10): велике число — клейма за весь час (від них бонус, витрати його не чіпають), а скільки
    // ще можна витратити, стояло дрібним сірим рядком. Тепер вільні — поруч і того самого розміру.
    const head = '<div class="clk-stamps"><b>🔖 ' + stampsShort(st.stamps) + '</b>'
      + '<span>за весь час · +' + bonus + ' % до всього</span>'
      + '<span class="clk-free" title="Скільки ще можна витратити на секрети, реліквії й оздобу хати">вільних <b class="clk-freen">'
      + '</b></span>'
      + (v.firings ? '<span class="muted small">починав наново: ' + v.firings + '</span>' : '') + '</div>'
      + info('Почати наново — це спалити глеки, верстати й віхи, а натомість узяти клейма майстра за все, що наліпив '
        + 'за весь час. Перша тисяча клейм дає по +' + dec(st.stampBonus * 100) + ' % до всього назавжди, далі кожне нове '
        + 'клеймо важить дедалі менше: на 4 000 — половину, на 16 000 — чверть, а після 4 млн бонус росте зовсім '
        + 'повільно — удесятеро більше клейм додають ту саму частку. Розписи, секрети, альбом і таблиця '
        + 'лишаються. Кожні ' + STAMPS_PER_CAP + ' клейм — ще один черепок до денної стелі обміну'
        + (cap ? ' (зараз +' + cap + ')' : '') + '.')
      + scienceLine(st, esc);
    // Два кола секретів (v9 §A.5): родинні — з першого дня, дідівські — на сотні клейм.
    // Записка #28: вільні клейма й «бракує N 🔖» малює stampButtons() поверх готової розмітки — витрата чи нове клеймо
    // не перемальовує всю вкладку (розмітка від вільних клейм не залежить, як і оздоба з lookButtons).
    const card = (s) => '<button type="button" class="clk-secret' + (s.owned ? ' owned' : '') + '" data-secret="' + esc(s.key)
      + '" data-price="' + s.price + '" data-shut="' + (s.owned ? 1 : 0) + '"' + (s.owned || !st.mine ? ' disabled' : '') + '>'
      + '<b>' + esc(s.name) + '</b><span class="muted small">' + esc(s.desc) + '</span>'
      + (s.owned ? '' : SHORT_HTML)
      + '<span class="clk-price stamp' + (s.owned ? ' done' : '') + '">' + (s.owned ? '✓ знаєш' : '🔖 ' + count(s.price)) + '</span></button>';
    const ring = (n) => st.secretList.filter((s) => (s.ring || 1) === n);
    const block = (title, note, list) => (list.length
      ? '<div class="clk-sub">' + title + '<span class="muted small"> · ' + note + '</span>' + FREE_HTML + '</div>'
        + '<div class="clk-secrets">' + list.map(card).join('') + '</div>'
      : '');
    // Третє коло (v10 §8) — коли перші два вже знаєш або клейм від 20 тисяч: новачкові мільярди лише лякали б.
    const firstTwo = ring(1).concat(ring(2));
    const third = firstTwo.every((s) => s.owned) || st.stamps >= 20000 ? ring(3) : [];
    const secrets = block('Родинні секрети', 'за клейма, назавжди', ring(1))
      + block('Дідівські секрети', 'друге коло — те, що дід тримав у скрині', ring(2))
      + block('Прадідівські секрети', 'третє коло — на мільйони клейм, для тих, хто пройшов усе', third)
      + '<div class="muted small clk-secnote">Клейма на секрети не згорають і бонус не гублять: він лишається, хоч витрать усі.</div>';
    if (swap(st.fire._static, head + secrets + relics(st, esc))) {
      st.secretBtns = [...st.fire._static.querySelectorAll('[data-secret]')];
      for (const b of st.secretBtns) b.onclick = () => order(st, 'secret', { key: b.dataset.secret });
      st.relicBtns = [...st.fire._static.querySelectorAll('[data-relic]')];
      for (const b of st.relicBtns) b.onclick = () => order(st, 'relic', { key: b.dataset.relic });
      st.freeNums = [...st.fire._static.querySelectorAll('.clk-freen')];
      st.stampSig = '';
    }
    stampButtons(st);
    st.slowAt = 0;
  }

  /// Вільні клейма в заголовках і «бракує N 🔖» на кнопках витрати (записка #28).
  const FREE_HTML = '<span class="clk-subfree"> · 🔖 вільних <b class="clk-freen"></b></span>';
  const SHORT_HTML = '<span class="clk-short" hidden></span>';
  const shortText = (n) => 'бракує ' + count(n) + ' 🔖';

  /// Наживо, без перемальовування: числа вільних клейм, вимкнені кнопки секретів і реліквій і чому вони вимкнені.
  /// Кличеться з firePane на кожен вид; DOM чіпаємо лише тоді, коли вільних клейм чи «можна» стало інше.
  function stampButtons(st) {
    const free = st.stampsFree || 0;
    const sig = free + '|' + (st.mine ? 1 : 0);
    if (st.stampSig === sig) return;
    st.stampSig = sig;
    const t = count(free);
    for (const el of st.freeNums || []) if (el.textContent !== t) el.textContent = t;
    for (const b of (st.secretBtns || []).concat(st.relicBtns || [])) {
      const price = +b.dataset.price || 0;
      const shut = b.dataset.shut === '1';                     // знаєш секрет / реліквія на межі
      const lack = st.mine && !shut && price > free;
      const off = !st.mine || shut || lack;
      if (b.disabled !== off) b.disabled = off;
      const note = b.querySelector('.clk-short');
      if (!note) continue;
      const txt = lack ? shortText(price - free) : '';
      if (note.hidden === lack) note.hidden = !lack;
      if (note.textContent !== txt) note.textContent = txt;
    }
  }

  /// Скарбниця роду (v11 §4): реліквії з рівнями за клейма. Кнопка — «наступний рівень за N клейм»; що дає — з
  /// каталогу магазину (крок і межа), сума зараз — з виду.
  const RELIC_ICON = { basket3: '🧺', cat3: '🐈', fiddle: '🎻', towel: '🌾', ember3: '🔥', seal2: '🏛', hands: '🧑‍🎓', toloka: '🏗' };
  function relics(st, esc) {
    const list = st.relicList;
    const cat = st.shopCat && st.shopCat.relics;
    if (!list || !cat) return '';
    const pct = (x) => dec(Math.round(x * 1000) / 10);
    return '<div class="clk-sub">🗝 Скарбниця роду<span class="muted small"> · реліквії за клейма: рівні без стелі, кожен утричі дорожчий</span>'
      + FREE_HTML + '</div>'
      + '<div class="clk-secrets clk-relics">' + list.map((r) => {
        const c = cat.find((x) => x.key === r.key);
        if (!c) return '';
        // Межа рівнів: сервер каже can = вистачає клейм і рівень не останній — тож «не можна» при достатніх клеймах = межа.
        const capped = (c.cap > 0 && r.sum >= c.cap - 1e-9) || (!r.can && (st.stampsFree || 0) >= r.price);
        return '<button type="button" class="clk-secret clk-relic' + (r.level ? ' owned' : '') + '" data-relic="' + esc(r.key) + '"'
          + ' data-price="' + r.price + '" data-shut="' + (capped ? 1 : 0) + '"' + (!st.mine || capped ? ' disabled' : '') + '>'
          + '<b>' + (RELIC_ICON[r.key] || '🗝') + ' ' + esc(c.name) + (r.level ? ' · рівень ' + r.level : '') + '</b>'
          + '<span class="muted small">' + esc(c.desc) + (r.level ? ' · зараз ' + pct(r.sum) + ' %' : '') + '</span>'
          + (capped ? '' : SHORT_HTML)
          + '<span class="clk-price stamp">' + (capped ? '✓ на межі' : '🔖 ' + count(r.price)) + '</span></button>';
      }).join('') + '</div>';
  }

  /// Наука майстра у вкладці Клейма: від кого вчимось, скільки в нього клейм і коли наука знову готова. Хто сам
  /// попереду всіх — тому вчитись нема в кого: це від нього вчиться решта. Без цеху (і округи) рядка нема.
  function scienceLine(st, esc) {
    const sc = st.science;
    if (!sc || !sc.top || !sc.who) return '';
    if (sc.top <= st.stamps) {
      return '<div class="muted small clk-science">🎓 Ти найкращий гончар округи: від тебе вчиться решта — раз на '
        + '20 годин їхній обпал підтягує їх на чверть різниці з тобою.</div>';
    }
    const left = sc.readyAt ? (Date.parse(sc.readyAt) - serverNow(st)) / 1000 : 0;
    return '<div class="muted small clk-science">🎓 <b>Наука майстра.</b> Найкращий гончар округи — ' + esc(sc.who) + ': '
      + stampsShort(sc.top) + '. Раз на 20 годин обпал дає ще чверть різниці з ним, але не більше, '
      + 'ніж удвічі твоїх клейм за глеки. ' + (left > 0 ? 'Знову — через ' + span(left) + '.' : 'Наступний обпал її принесе.')
      + '</div>';
  }

  /// Глек на колі, глек на полиці й глечики над полицею: перемальовуємо лише тоді, коли гончар поставив інший
  /// розпис, замісив іншу глину чи добудував гончарню (полиця повніша).
  function wheelJug(st) {
    const shelf = Math.min(9, 3 + Math.floor(((st.ups.workshop && st.ups.workshop.level) || 0) / 4));
    const sig = st.wear + '|' + st.clayBody + '|' + shelf + '|' + (st.craftWheel ? 1 : 0) + (st.craftShelf ? 1 : 0);
    if (st.jugBox._wear === sig) return;
    st.jugBox._wear = sig;
    const w = st.wear || '';
    const body = st.clayBody;
    // Коло й полицю малює ремесло (clicker-craft.js), щойно воно завантажилось: виріб на колі й сирці на полиці.
    if (!st.craftWheel) st.jugBox.innerHTML = jug(w, 'wheel', body);
    st.fallJug.innerHTML = jugSvg(w, 'clk-fall-jug', 'fall', body);
    // Свій slot: градієнти глека на сцені сховані разом із ним (hidden), і шар поверх на них не спирається.
    if (st.flyFallJug) st.flyFallJug.innerHTML = jugSvg(w, 'clk-fall-jug', 'flyfall', body);
    if (st.craftShelf) return;
    // На полиці — глечики: у розписі, що на колі, і прості (кольору глини); що більша гончарня, то повніша полиця.
    let s = '';
    for (let i = 0; i < shelf; i++) s += jugSvg(i % 2 ? '' : w, '', 'shelf-' + i, body);
    st.shelfJugs.innerHTML = s;
  }

  // ---------- хата: сцена, що росте від покупок ----------

  /// Саму хату (стіни, знаряддя, прикраси, підмайстрів, світ за хатою, день і ніч) малює жива хата — clicker-scene.js
  /// у свій шар .clk-house. Тут лишились тільки запасні значки для полиці «Хата», поки та частина не завантажилась.
  const TOOL_ICON = { paddle: '🥄', string: '🧵', sponge: '🧽', ribs: '📏', lantern: '🏮', apron: '🥼', bucket: '🪣', whistle: '🎶', scales: '⚖️', iron: '🔖' };

  // ---------- хата: полиця з глиною, знаряддям і прикрасами ----------

  /// Підписи згорнутих розділів Майстерні: скільки там уже є, і чи варто розгорнути самим.
  function paintSections(st, ownedStyles) {
    const count = {
      house: st.tools.filter((t) => t.owned).length + st.clays.filter((c) => c.owned && c.price > 0).length,
      styles: ownedStyles ? ownedStyles + '/' + (st.styleList.length || 8) : '',
      orders: st.taken.length ? '🐴' + st.taken.length : '',
    };
    for (const x of SECTIONS) {
      const sec = secEls(st)[x.key];
      if (!sec) continue;
      const sum = String(count[x.key] || '');
      const text = x.title + (sum ? ' · ' + sum : '');
      const sm = sec.firstElementChild;
      if (sm.textContent !== text) sm.textContent = text;
      // Гравець розділ ще не чіпав — розгорнутий, поки там є що купити, і згорнутий, коли все куплено.
      if (storeGet(SEC_KEY + x.key, '') === '') {
        let want = false;
        try { want = !!x.open(st); } catch { want = sec.open; }
        if (sec.open !== want) { sec._want = want; sec.open = want; }
      }
    }
  }

  /// Хата для полиць і сцени: каталог (назви, описи, ціни) + стан із виду (куплене, обране, знайдене) у тих самих
  /// рядках, що й до десятого оновлення, — st.clays / st.tools / st.decorList і st.houseView (оздоба, дивовижі,
  /// вивіска). Перебудовуємо лише тоді, коли стан чи каталог справді змінились (st.houseVer росте): вид летить щопачки
  /// кліків, а хата міняється раз на хвилини. Старий сервер (хвилина деплою) шле повні рядки прямо у виді — беремо як є.
  function houseFrom(st, hs) {
    const cat = (st.catalog && st.catalog.house) || null;
    const sig = JSON.stringify(hs);
    if (sig === st.houseSig && cat === st.houseCat) return;
    st.houseSig = sig;
    st.houseCat = cat;
    st.houseVer = (st.houseVer || 0) + 1;
    const own = hs.own;
    if (!own) {
      st.clays = hs.clays || [];
      st.tools = hs.tools || [];
      st.decorList = hs.decor || [];
      st.houseView = { named: hs.named || '', nameMax: hs.nameMax || 24, looks: hs.looks || [],
        wonders: hs.wonders && hs.wonders.list ? hs.wonders : null };
      return;
    }
    const has = (list) => { const set = new Set(list || []); return (k) => set.has(k); };
    const clay = has(own.clays), tool = has(own.tools), decor = has(own.decor), look = has(own.looks);
    const c = cat || {};
    st.clays = (c.clays || []).map((x) => ({ key: x.key, name: x.name, desc: x.desc, price: x.price, body: x.body,
      owned: !x.key || clay(x.key), on: (hs.clay || '') === x.key }));
    st.tools = (c.tools || []).map((x) => ({ key: x.key, name: x.name, desc: x.desc, price: x.price, owned: tool(x.key) }));
    st.decorList = (c.decor || []).map((x) => ({ key: x.key, name: x.name, desc: x.desc, price: x.price, bonus: x.bonus, owned: decor(x.key) }));
    const chosen = hs.look || {};
    const found = hs.wonders || {};
    // Назва й байка є в каталозі лише знайдених (решта — секрет); свіжознайдену дочекаємось із новим каталогом.
    const list = (c.wonders || []).map((w) => ({ key: w.key, found: !!found[w.key], at: found[w.key] || null,
      name: found[w.key] ? w.name || '' : '', tale: found[w.key] ? w.tale || '' : '', from: w.from || '' }));
    st.houseView = {
      named: hs.named || '',
      nameMax: c.nameMax || 24,
      looks: (c.looks || []).map((g) => {
        const opts = g.options || [];
        return { key: g.key, name: g.name, desc: g.desc, value: chosen[g.key] || (opts[0] && opts[0].value) || '',
          options: opts.map((o) => ({ value: o.value, name: o.name, price: o.price, owned: !(o.price > 0) || look(g.key + ':' + o.value) })) };
      }),
      wonders: cat ? { found: Object.keys(found).length, total: list.length, bonus: c.wonderBonus || 0.01, list } : null,
    };
  }

  function housePane(st, ctx) {
    const esc = ctx.esc;
    // Розмітку складаємо лише тоді, коли хата змінилась (houseFrom), гончар уперше обпалився (відкрилась оздоба) чи
    // хтось скинув підпис панелі — запізніла частина принесла значки знарядь і дивовиж.
    const inputs = st.houseVer + '|' + (st.stamps > 0 ? 1 : 0) + '|' + (H.api.toolIcon ? 1 : 0) + (H.api.decorIcon ? 1 : 0) + (H.api.wonderIcon ? 1 : 0);
    if (st.housePane._sig != null && st.housePane._in === inputs) { lookButtons(st); return; }
    st.housePane._in = inputs;
    const clays = '<div class="clk-sub">Глина на колі<span class="muted small"> · купується раз; замішана відлежується 10 хв</span></div>'
      + '<div class="clk-clays">' + st.clays.map((c) => '<button type="button" class="clk-clay' + (c.on ? ' on' : '') + (c.owned ? ' owned' : '')
        + '" data-clay="' + esc(c.key) + '" data-price="' + c.price + '" data-owned="' + (c.owned ? 1 : 0) + '" data-on="' + (c.on ? 1 : 0) + '" disabled>'
        + jugSvg('', 'clk-mini', 'clay-' + (c.key || 'plain'), c.body || '')
        + '<b>' + esc(c.name) + '</b><span class="muted small">' + esc(c.desc) + '</span>'
        + '<span class="clk-price' + (c.owned ? ' done' : '') + '"></span></button>').join('') + '</div>';
    const tools = '<div class="clk-sub">Знаряддя гончаря<span class="muted small"> · раз і назавжди, видно на стіні</span></div>'
      + '<div class="clk-tools">' + st.tools.map((t) => '<button type="button" class="clk-tool' + (t.owned ? ' owned' : '')
        + '" data-house="tool" data-key="' + esc(t.key) + '" data-price="' + t.price + '" data-owned="' + (t.owned ? 1 : 0) + '" disabled>'
        + '<span class="clk-ticon">' + ((H.api.toolIcon && H.api.toolIcon(t.key)) || TOOL_ICON[t.key] || '🔧') + '</span><b>' + esc(t.name) + '</b><span class="muted small">' + esc(t.desc) + '</span>'
        + '<span class="clk-price' + (t.owned ? ' done' : '') + '">' + (t.owned ? '✓ на стіні' : short(t.price)) + '</span></button>').join('') + '</div>';
    const decor = '<div class="clk-sub">Прикраси хати<span class="muted small"> · +2 % до всього кожна, лишаються назавжди</span></div>'
      + '<div class="clk-tools">' + st.decorList.map((d) => '<button type="button" class="clk-tool decor' + (d.owned ? ' owned' : '')
        + '" data-house="adorn" data-key="' + esc(d.key) + '" data-price="' + d.price + '" data-owned="' + (d.owned ? 1 : 0) + '" disabled>'
        + (H.api.decorIcon ? '<span class="clk-ticon">' + H.api.decorIcon(d.key) + '</span>' : '') + '<b>' + esc(d.name) + '</b><span class="muted small">' + esc(d.desc) + '</span>'
        + '<span class="clk-price' + (d.owned ? ' done' : '') + '">' + (d.owned ? '✓ у хаті' : short(d.price)) + '</span></button>').join('') + '</div>';
    if (swap(st.housePane, clays + tools + decor + looksHtml(st, esc) + wondersHtml(st, esc))) {
      st.clayBtns = [...st.housePane.querySelectorAll('[data-clay]')];
      for (const b of st.clayBtns) {
        b._price = b.querySelector('.clk-price');
        b.onclick = () => order(st, 'knead', { kind: b.dataset.clay });
      }
      st.houseBtns = [...st.housePane.querySelectorAll('[data-house]')];
      for (const b of st.houseBtns) b.onclick = () => order(st, b.dataset.house, { key: b.dataset.key });
      bindLooks(st);
      collectCountdowns(st);
      st.slowAt = 0;
    }
    lookButtons(st);
  }

  /// Клейма міняються рідко, але розмітку оздоби вони не чіпають: інакше кожне клеймо стирало б недописану вивіску.
  function lookButtons(st) {
    const free = st.housePane && st.housePane.querySelector('.clk-lookfree b');
    if (free) { const t = count(st.stampsFree || 0); if (free.textContent !== t) free.textContent = t; }
    for (const b of st.lookBtns || []) {
      const lack = st.mine && b.dataset.on !== '1' && +b.dataset.stamp > (st.stampsFree || 0);
      const off = !st.mine || b.dataset.on === '1' || lack;
      if (b.disabled !== off) b.disabled = off;
      // Записка #28: вимкнена через клейма — дрібно каже, скільки бракує (а не лише в title).
      const note = b.querySelector('.clk-short');
      if (!note) continue;
      const txt = lack ? shortText(+b.dataset.stamp - (st.stampsFree || 0)) : '';
      if (note.hidden === lack) note.hidden = !lack;
      if (note.textContent !== txt) note.textContent = txt;
    }
  }

  // ---------- Хата: оздоба за клейма, вивіска й дивовижі (дев'яте оновлення) ----------

  /// Значок дивовижі малює жива хата (clicker-scene.js); поки її нема — проста зірочка.
  const wonderIcon = (key) => (H.api.wonderIcon && H.api.wonderIcon(key)) || '✨';

  /// Оздоба: гурт (стріха, стіни, тин…) — рядок вибору. Куплений варіант вдягається безплатно, новий бере клейма.
  function looksHtml(st, esc) {
    const hs = st.houseView || {};
    const looks = hs.looks || [];
    if (!looks.length) return '';
    // Оздоба коштує клейм, тож новачкові, який ще не палив, показуємо саму вивіску: вона безплатна.
    const paid = st.stamps > 0 || looks.some((g) => (g.options || []).some((o) => o.owned && o.price > 0));
    const rows = !paid
      ? '<div class="muted small">Хату можна перебрати під себе — стріха, стіни, тин, дерево, колір кола, масть кота з собакою. Платиться клеймами, тож спершу обпал.</div>'
      : looks.map((g) => '<div class="clk-lookrow"><span class="clk-lookname">' + esc(g.name)
      + '<span class="muted small"> · ' + esc(g.desc) + '</span></span><span class="clk-lookopts">'
      + (g.options || []).map((o) => '<button type="button" class="clk-lookopt' + (g.value === o.value ? ' on' : '')
        + (o.owned ? ' owned' : '') + '" data-look="' + esc(g.key) + '" data-val="' + esc(o.value)
        + '" data-stamp="' + (o.owned ? 0 : o.price) + '" data-on="' + (g.value === o.value ? 1 : 0) + '" disabled>'
        + esc(o.name) + (o.owned ? '' : '<span class="clk-lookprice">🔖' + o.price + '</span><span class="clk-short" hidden></span>') + '</button>').join('')
      + '</span></div>').join('');
    const sign = '<div class="clk-lookrow sign"><span class="clk-lookname">Вивіска<span class="muted small"> · як зветься твоя хата</span></span>'
      + '<span class="clk-signbox"><input class="clk-signin" type="text" maxlength="' + (hs.nameMax || 24)
      + '" value="' + esc(hs.named || '') + '" placeholder="Хата гончаря" aria-label="Ім\'я хати">'
      + '<button type="button" class="ghost small clk-signgo">Написати</button></span></div>';
    return '<div class="clk-sub">🎨 Оздоба<span class="muted small"> · за клейма, раз і назавжди</span>'
      + (paid ? '<span class="clk-lookfree"> · 🔖 вільних <b>' + count(st.stampsFree || 0) + '</b></span>' : '')
      + info('Оздоба міняє вигляд хати на сцені й нічого не додає до доходу. Клейма на неї, як і на секрети, '
        + 'не згорають і бонусу не гублять — просто їх стає менше на секрети. Вивіска безплатна, міняй скільки хочеш.')
      + '</div><div class="clk-looks">' + rows + sign + '</div>';
  }

  /// Дивовижі: знайдене — з байкою, решта — силуети з підказкою, звідки їх ждати.
  function wondersHtml(st, esc) {
    const w = (st.houseView && st.houseView.wonders) || null;
    // Дивовижі приходять із рідкісних подій пізньої гри: поки гончар не палив жодного разу, це просто шум.
    if (!w || !w.list || (!w.found && !st.stamps)) return '';
    const pct = Math.round((w.bonus || 0.01) * 100 * w.found);
    const cells = w.list.map((x) => '<div class="clk-wonder' + (x.found ? ' found' : '') + '">'
      + '<div class="clk-wtop"><span class="clk-wicon' + (x.found ? '' : ' sil') + '">' + wonderIcon(x.key) + '</span>'
      + '<b>' + (x.found && x.name ? esc(x.name) : '· · ·') + '</b></div>'
      + '<span class="muted small">' + esc(x.found ? x.tale : x.from) + '</span></div>').join('');
    return '<div class="clk-sub">✨ Дивовижі · ' + w.found + '/' + w.total
      + (w.found ? '<span class="muted small"> · +' + pct + ' % до всього</span>' : '')
      + info('Дивовижі знаходяться самі, коли в хаті стається щось рідкісне: добрий обпал, довга серія, щедрий віз, '
        + 'гість на свято. Під силуетом — звідки вона може прийти: це як пощастить, а не щоразу (Люстро в знаряддях — удвічі '
        + 'частіше). Лише Скалка з неба приходить напевно — з першою ж спійманою зіркою. Кожна дивовижа додає +1 % до всього '
        + 'й лишається в хаті назавжди.')
      + '</div><div class="clk-wonders">' + cells + '</div>';
  }

  /// Кнопки оздоби й вивіски: вибір гурту летить дією look, ім'я — дією name.
  function bindLooks(st) {
    st.lookBtns = [...st.housePane.querySelectorAll('[data-look]')];
    for (const b of st.lookBtns) {
      b.onclick = () => { H.api.sfx('buy'); order(st, 'look', { key: b.dataset.look, value: b.dataset.val }); };
    }
    const input = st.housePane.querySelector('.clk-signin');
    const go = st.housePane.querySelector('.clk-signgo');
    if (!input || !go) return;
    // Недописану вивіску розмітка переживає: купівля оздоби перемальовує панель, а стерти чуже слово шкода.
    if (st.signDraft != null) input.value = st.signDraft;
    const write = () => { H.api.sfx('tap'); st.signDraft = null; order(st, 'name', { text: input.value }); input.blur(); };
    input.oninput = () => { st.signDraft = input.value; };
    go.onclick = write;
    input.onkeydown = (e) => { if (e.key === 'Enter') { e.preventDefault(); write(); } };
  }

  // ---------- дошка купців ----------

  function ordersPane(st, ctx) {
    const esc = ctx.esc;
    // Як і хата: розмітку складаємо лише на новий стан дошки (він — частина хати) чи на щойно приїжджий каталог розписів.
    const inputs = st.houseVer + '|' + (st.catalog ? 1 : 0);
    if (st.ordersPane._sig != null && st.ordersPane._in === inputs) return;
    st.ordersPane._in = inputs;
    const note = info('Купець у дорозі повертає більше, ніж узяв: що довша дорога, то щедріше (5 хв — ×1,4, 30 хв — ×2,2). '
      + 'За розпис із колекції платить одразу ×1,6. Дошка оновлюється раз на 4 хвилини, кого не взяв — поїхав. '
      + 'Клейма спалюють купців у дорозі разом із глеками.'
      + (st.tools.some((t) => t.key === 'scales' && t.owned) ? ' Ваги купця: +20 % до кожної плати.' : ''));
    const head = '<div class="clk-sub">Дошка купців<span class="muted small"> · нові купці через <span class="clk-cd" data-at="' + st.refreshAt + '" data-done="ось-ось"></span></span>' + note + '</div>';
    const board = st.orders.length
      ? '<div class="clk-orders">' + st.orders.map((o) => {
        const invest = o.kind === 'invest';
        const text = invest
          ? 'Візьме ' + potsShort(o.need) + ' у дорогу і за ' + o.minutes + ' хв поверне <b>' + short(o.pay) + '</b>'
          : 'Купить ' + potsShort(o.need) + ' у розписі «' + esc(o.styleName || styleNameOf(st, o.style)) + '» за <b>' + short(o.pay) + '</b> одразу'
            + (o.can ? '' : ' <span class="clk-no">(цього розпису ще нема)</span>');
        return '<div class="clk-order' + (invest ? '' : ' style') + '"><div class="clk-oname">' + (invest ? '🐴 ' : '🧺 ') + esc(o.merchant) + '</div>'
          + '<div class="small clk-otext">' + text + '</div>'
          + '<button type="button" class="primary small clk-take" data-take="' + o.id + '" data-kind="' + esc(o.kind) + '" data-need="' + o.need
          + '" data-can="' + (o.can ? 1 : 0) + '" disabled>' + (invest ? 'Відправити' : 'Продати') + ' · ' + short(o.need) + (o.need >= HRYVNIA ? '' : ' 🏺') + '</button></div>';
      }).join('') + '</div>'
      : '<div class="clk-teaser muted small">Усіх купців уже взято — нові прийдуть із новою дошкою</div>';
    const taken = st.taken.length
      ? '<div class="clk-sub">У дорозі · ' + st.taken.length + ' з ' + st.maxTaken + '</div><div class="clk-takens">'
        + st.taken.map((t) => '<div class="clk-taken"><span>🐴 ' + esc(t.merchant) + '</span><span class="muted small">повернеться через <span class="clk-cd" data-at="'
          + t.payAt + '" data-done="уже на порозі"></span> з <b>' + short(t.pay) + '</b></span></div>').join('') + '</div>'
      : '';
    if (swap(st.ordersPane, head + board + taken)) {
      st.orderBtns = [...st.ordersPane.querySelectorAll('[data-take]')];
      for (const b of st.orderBtns) b.onclick = () => order(st, 'take', { id: +b.dataset.take });
      collectCountdowns(st);
      st.slowAt = 0;
    }
  }

  /// Назва розпису за ключем: вид купця шле лише ключ. З каталогу розписів, а без нього — з полиці розписів у виді.
  function styleNameOf(st, key) {
    const find = (list) => (Array.isArray(list) ? list.find((s) => s && s.key === key) : null);
    const s = find(st.catalog && st.catalog.styles) || find(st.styleList);
    return (s && s.name) || key || '';
  }

  // ---------- каталог: чого бракує й дозапит ----------

  /// Чого бракує закешованому каталогу для цього виду: самого каталогу (після F5 сервер шле його лише на прохання),
  /// хати чи прокачки ремесла в ньому (каталог зі старого сервера) або назви й байки щойно знайденої дивовижі — їх
  /// каталог шле лише знайдених, решта секрет. '' — усього досить.
  function catalogGap(st, v) {
    const c = st.catalog;
    if (!c) return 'all';
    const hs = v.house;
    if (hs && hs.own) {
      if (!c.house) return 'house';
      const known = new Set((c.house.wonders || []).filter((w) => w.tale).map((w) => w.key));
      for (const key of Object.keys(hs.wonders || {})) if (!known.has(key)) return 'wonder:' + key;
    }
    const ups = v.craft && v.craft.ups;
    if (ups && ups.length && ups[0].name == null && !c.craftUps) return 'craft';
    return '';
  }

  /// Попросити каталог (look { catalog: true }): на кожну нестачу не більше трьох разів і не частіше, ніж раз на 5 с —
  /// відповідь могла розминутись із пачкою кліків, а сервер без клієнта сам каталогу не пришле.
  function askCatalog(st, ctx, gap) {
    if (!gap || !ctx.mine || !ctx.act) return;
    const now = Date.now();
    if (st.catalogGap === gap && now - st.catalogAskAt < 5000) return;
    const tries = st.catalogTries[gap] || 0;
    if (tries >= 3) return;
    st.catalogTries[gap] = tries + 1;
    st.catalogGap = gap;
    st.catalogAskAt = now;
    ctx.act('look', { catalog: true, pv: PV });
  }

  /// Усі відліки обох панелей — щоб paintCountdowns не шукав їх щоп'ятої секунди. _sec — чий розділ: згорнутий не малюємо.
  function collectCountdowns(st) {
    const of = (pane, sec) => [...pane.querySelectorAll('.clk-cd')].map((el) => { el._sec = sec; return el; });
    st.cds = [...of(st.housePane, 'house'), ...of(st.ordersPane, 'orders')];
  }

  /// Купець повернувся між видами: «+N» над сценою й тост. Перший вид лише запам'ятовує, що вже було.
  function paidLately(st, ctx, paid) {
    if (!st.paidSeen) { st.paidSeen = new Set(paid.map((p) => p.id)); return; }
    for (const p of paid) {
      if (st.paidSeen.has(p.id)) continue;
      st.paidSeen.add(p.id);
      const at = Date.parse(p.at);
      if (!Number.isFinite(at) || serverNow(st) - at > 120000) continue;
      popAt(st, '+' + short(p.pay), 'big', 50, 40);
      H.api.sfx('coins');
      sparks(st, st.fx, 10, true, 50, 44);
      if (ctx.toast) ctx.toast('🐴 ' + p.merchant + ' повернувся: +' + potsShort(p.pay), 'ok');
    }
  }

  /// Вивіска над лічильником: найвищий верстат драбини, що вже куплений.
  function paintSign(st) {
    let text = '';
    for (const k of Object.keys(st.ups)) {
      const u = st.ups[k];
      if (u.kind === 'idle' && u.level > 0) text = u.name + ' · ' + u.level;
    }
    // Своє ім'я хати (Хата → Оздоба → Вивіска) заступає і верстат, і типовий підпис.
    const named = (st.lastView && st.lastView.house && st.lastView.house.named) || '';
    text = named ? '🏠 ' + named : text ? '🏠 ' + text : '🏠 Хата гончаря';
    // Значки звань біля імені хати (clicker-titles.js кладе їх у st.titleIcons): окремого рядка шапка не має.
    if (st.titleIcons) text += ' · ' + st.titleIcons;
    if (st.sign.textContent !== text) st.sign.textContent = text;
  }

  /// Таблицю тягнемо з paintSlow — тобто лише тоді, коли картку видно: схована картка суперника однаково не
  /// покаже, а таймер крутився б і на вкладці «Ефір».
  function loadBoard(st) {
    if (Date.now() - st.boardAt < BOARD_MS) return;
    st.boardAt = Date.now();
    const nick = (st.ctx && st.ctx.me && st.ctx.me.nick) || '';
    fetch('/api/games/leaderboard?game=clicker&period=all', { headers: { 'X-Nick': encodeURIComponent(nick) } })
      .then((r) => (r.ok ? r.json() : null))
      .then((d) => { if (d && Array.isArray(d.rows)) { st.board = d.rows; st.slowAt = 0; } })
      .catch(() => { /* без таблиці просто не буде рядка про суперника */ });
  }

  // ---------- «Що нового» раз на гравця (v9 §A.9) ----------

  /// Текст показується один раз на гончаря: керує цим сервер (view.news), тож і з телефона, і з ноутбука вікно
  /// відкриється рівно раз. «v10» — «Глек на весь світ» (docs/games/specs/clicker-v10.md §12); закриття вікна забирає
  /// подарунок. Хто пропустив «v9.2» (звання округи) чи «v9.1» (клейма після тисячі), тому ті рядки йдуть слідом —
  /// сервер каже, що гончар бачив востаннє (view.newsSeen), а подарунок v9.2 дасть сам, якщо його ще не забрано.
  /// «v10.1» (28.09) — правки за записками «💡 Розробнику», без подарунка; хто пропустив v10 — бачить і його рядки,
  /// і подарунок v10 забирає тим самим закриттям.
  /// «v11.1» (08.10) — дошліфовка за записками й на всі екрани, без подарунка; хто пропустив «v11» — бачить і його
  /// рядки, а подарунок v11 забирає тим самим закриттям (сервер сам знає, чи вже давав).
  /// «v11.2» (10.10) — «хвилина гри», комора під полицею, перероблені віхи й ачівки за тиждень, без подарунка; хто
  /// пропустив «v11.1» — бачить і його рядки.
  const NEWS = {
    title: '✨ Що нового в Гончарному колі',
    lead: 'Усе, що платило копійки, тепер платить твоєю грою, мовчазні віхи заговорили, а ачівки — не довше тижня.',
    lines: [
      ['⏱', '<b>Хвилина гри.</b> Купець, Око майстра, гості, села, купці хати, віз цеху, гостинці й поміч на толоці платять тим, скільки ти сам ліпиш за хвилину біля кола (глеки з полиці й клік), а не годинами пасиву. У пізній грі — в десятки разів більше, ніж було; на початку — не менше, ніж було.'],
      ['🫙', '<b>Комора під полицею.</b> Коло й так крутиться без тебе цілу добу, тож кожна віха Ночі тепер ще й ловить у комору 3 % глеків з полиці, що падали, поки тебе не було.'],
      ['🎪', '<b>Віхи, що мовчали, заговорили.</b> «Гарт» (розгін і так повний) тепер подовжує ярмарок і натхнення на 10 % кожна; «стоїть довше» — розписні й глеки з полиці приходять на 4 % частіше; купець і Око — плюс хвилини гри.'],
      ['🐈', '<b>Дрібні радощі важать.</b> Кіт приносить п\'ять глеків з полиці, сорока — щонайменше один, пригоди й гостинці сіл — чверть хвилини гри за хвилину. А ведмідь забирає не більше двох хвилин гри.'],
      ['🏆', '<b>Ачівки — за тиждень.</b> «Дзвінке горно»: власноруч, від шести виробів, без жодної тріщини і щонайменше 40 % дзвінких. Звання «Бездоганне горно» — десять таких обпалів, не поспіль. «Золоті руки» — 3 500 обпалених виробів одного виду, десята шана села й гостя — ближче, а дивовижі приходять і з толоки.'],
    ],
    ok: 'До кола!',
    okGift: 'Забрати подарунок',
  };

  /// «v11.1» (08.10) — для тих, хто пропустив: дошліфовка за записками й на всі екрани.
  const NEWS_111 = {
    lead: 'Дошліфовка: глек не розбивається за спиною, друзям помагати простіше, а стіл зручний на будь-якому екрані.',
    lines: [
      ['🏺', '<b>Глек з полиці не пропадає.</b> Поки розписуєш, даруєш чи гортаєш униз, глек летить поверх усього, справа, — лови тапом. Під час розпису й ручного обпалу глеки чекають, поки закінчиш. А «трісь» чесно каже, чи справді обірвалась серія, чи її вберіг фартух.'],
      ['🤝', '<b>Картка друга.</b> Усе, що можеш для друга зробити, — в одному вікні: гостинець (і скільки йому ще влізе), підмайстер, похвала, виріб, толока. Нагорі «Села» — хто зараз чекає допомоги, і стрічка цеху: хто кому що дав.'],
      ['🧑‍🎓', '<b>Допомога складається.</b> Підмайстер і похвала від різних друзів тепер продовжують одне одного, а не перезаписують. Бафи від друзів видно в плашках під колом, а за допомогу можна подякувати. «Село» відчиняється з першого обпалу.'],
      ['🏗', '<b>Толока наперед.</b> Поки етап будується, видно, що знадобиться на наступний, — і в тебе, і в хаті друга.'],
      ['📦', '<b>Лад у коморі.</b> Коли місця нема, на базар іде найдешевше з того, що лежить, а не нове з горна. Що просить толока чи гості — не продається само, а толока підкаже, чого бракує, і кнопкою «🏺 Ліпити» поставить потрібне на коло.'],
      ['🔖', '<b>Вільні клейма</b> видно всюди, де їх витрачаєш, а на кожному секреті, реліквії й оздобі — скільки бракує.'],
      ['🖥', '<b>Стіл на будь-якому екрані.</b> На 2K і 4K стіл більший і без порожнечі обабіч, на ноутбуках коло помітно більше, на телефоні вкладки в один ряд і перемикаються з будь-якої глибини, а лежачи коло й «Далі» видно без прокрутки.'],
      ['🧺', '<b>Менше гортати.</b> Вибір розпису — компактною сіткою з «🎲 Навмання» нагорі, докачане ховається в згортку «Докачано», сушарня — коротка смуга, а в «Далі» можна одразу здати замовлення чи продати повну комору.'],
    ],
    ok: 'До кола!',
    okGift: 'Забрати подарунок',
  };
  /// «v11» — «Толока», для тих, хто його пропустив (закриття вікна забирає подарунок v11).
  const NEWS_11 = {
    lead: 'А ще — з минулого оновлення «Толока»: усім селом будуємо Опішню — а вона відчиняє двері гончарям усього світу.',
    lines: [
      ['🏗', '<b>Толока.</b> Дванадцять будов на майдані — від криниці з журавлем до Глека на майдані. Етап закладаєш глеками й виробами з горна, а далі він будується годинами — і ніякий множник цього не пришвидшить. Мала толока — з гривні, велика — з червоного золотого. Кожна будова — +5 % до всього і своя вічна пільга.'],
      ['🤝', '<b>Друзі на толоці.</b> Піднеси виріб другові з цеху — його етап будується на 15 % швидше (до трьох друзів), а тобі — гостинець.'],
      ['🏯', '<b>Гончарі світу.</b> Пристань, музей, інститут, зала й фестиваль відчиняють шість нових щаблів: Цзиндечжень, Ізнік, Делфт, Майсен, Севр і раку. Кожен привозить свій розпис — альбом росте до п\'ятнадцяти стовпчиків, а все, що вже зібрано, лишається зібраним.'],
      ['🗝', '<b>Скарбниця роду.</b> Клеймам нарешті є куди йти: вісім реліквій з рівнями без стелі — полиця, кіт, скрипка, рушник, жар, печатка, руки й толока.'],
      ['🎉', '<b>Фестиваль.</b> Коли збудуєш фестивальну сцену — раз на добу година ×2 до всього.'],
      ['🎁', '<b>Подарунок:</b> три години твого «без тебе» і двадцять в\'язок соломи.'],
    ],
  };
  /// «v10.1» — правки за записками (без подарунка): для тих, хто його не бачив.
  const NEWS_101 = {
    lead: 'А ще — правки за вашими записками в «💡 Розробнику»:',
    lines: [
      ['🔥', '<b>Горно.</b> Відлік обпалу більше не скидається й не завмирає. Ручний обпал — на одному екрані: кнопки, жар і «💨 порив вітру» видно весь час. Пічка в «Ремеслі» менша, розписи — компактною сіткою під «🎨 Розпис».'],
      ['🖌', '<b>Розпис.</b> Пензель рахує зафарбовані пелюстки, а не натиски: зайві тики нічого не закривають, а в кінці видно «Краса · пелюсток з».'],
      ['⏱', '<b>Плашки під колом.</b> Кожна окремо: секунди великими цифрами, смужка часу, що тане, а що скоро скінчиться — першим.'],
      ['🛒', '<b>«Усе на віз»</b> — уся комора одним натиском, крім того, чого чекають гості й села. А на возі тепер лежать справжні вироби.'],
      ['⭐', '<b>Зірки в альбомі</b> пояснено легендою й підказками. А перша ж спіймана 🌠 падаюча зірка напевно дає «Скалку з неба».'],
    ],
  };
  /// «v10» — для тих, хто його пропустив (закриття вікна забирає подарунок v10).
  const NEWS_10 = {
    lead: 'А ще — з минулого оновлення «Глек на весь світ»: після Січі гончарня виходить у світ.',
    lines: [
      ['🌍', '<b>Дванадцять нових щаблів.</b> Від Батуринської кахельні й Корецької порцеляни — через Одеський порт, кругосвітнє плавання, пароплав за океан і Всесвітню виставку в Парижі — до Опішні, гончарної столиці світу. Кожен щабель видно на сцені.'],
      ['₴', '<b>Гривні замість «скстлн».</b> Від квадрильйона глеків великі суми рахуються в гривнях: 1 ₴ = 1 квадрильйон глеків. Гаманець той самий, просто без зайвих нулів. А далі будуть і червоні золоті.'],
      ['🪧', '<b>Віхи після сотні.</b> На 75–300 рівнях — віхи, що дають не ×2, а свою силу: пасив +25 %, глек з полиці, ярмарок, щедрий купець, довша ніч, Око майстра. У «Швидшого кола» — аж до «Обома руками».'],
      ['🏛', '<b>Гостинний двір.</b> Одеський порт привозить заморських гостей: царградських і кантонських купців, діаспору з Канади, лондонських торговців, паризьких колекціонерів. Виконуй їхні замовлення — кожні гості шанують тебе по-своєму.'],
      ['🔖', '<b>Прадідівські секрети</b> — третє коло за мільйони клейм. А після 4 млн клейм бонус росте повільніше: удесятеро більше клейм — та сама надбавка. Нікому з тих, хто грає, це не зменшило жодного відсотка.'],
      ['⚡', '<b>Легше й рівніше.</b> Коло більше не смикається на айфоні, на ПК стіл уміщається в екран, а гра менше навантажує комп\'ютер.'],
      ['🎁', '<b>Подарунок:</b> чотири години твого «без тебе» глеками одразу.'],
    ],
  };
  /// «v9.2» — для тих, хто його пропустив.
  const NEWS_92 = {
    lead: 'А ще — з минулого оновлення:',
    lines: [
      ['🎖', '<b>Звання округи.</b> Тринадцять «перших в окрузі», звання дня, рідкісні й таємні; значки біля ніка обираєш у «🤝 Селі» → «Звання».'],
      ['🎁', '<b>Подарунок округи:</b> ще три години «без тебе» — і пам\'ятний глечик «Округа» на стіні звань.'],
    ],
  };
  /// «v9.1» — для тих, хто пропустив і його.
  const NEWS_OLD = {
    lead: 'І ще раніше:',
    lines: [
      ['🔖', '<b>Клейма після тисячі важать менше.</b> Перша тисяча — +2 % до всього за клеймо (з Родовим клеймом +3 %), далі кожне нове важить дедалі менше.'],
      ['🎓', '<b>Наука майстра.</b> Раз на 20 годин обпал дає ще чверть різниці з найкращим гончарем округи — хто позаду, той наздоганяє.'],
    ],
  };

  // ---------- гривня й червоні золоті: вікно-церемонія (v10 §6) ----------

  /// Уперше доріс до гривень чи золотих — одне вікно з поясненням. Сервер шле coin (до чого доріс) і coinSeen (що вже
  /// бачив); хто бачив «Що нового» v10, тому сервер записав coinSeen сам — гривню там уже пояснено.
  const COINS = [
    null,
    { kind: 'hryvnia', title: '📜 Гетьманський універсал', text: 'Глеків у тебе вже стільки, що рахувати їх поштучно — як рахувати зерно в мішку. '
      + 'Відтепер великі гроші рахуються в <b>гривнях</b>: 1 ₴ = 1 квадрильйон глеків. Гривня — ще з княжих часів: так звали срібний злиток. '
      + 'Гаманець той самий, просто без зайвих нулів.' },
    { kind: 'gold', title: '💰 Червоні золоті', text: 'Гривень стало як піску над Дніпром. Великі статки рахують <b>червоними золотими</b>: '
      + '1 золотий = 1 трильйон гривень. Хто б міг подумати, що все почалося з одного глечика.' },
  ];

  function coinLater(st) {
    clearTimeout(st.coinT);
    st.coinT = setTimeout(() => {
      if (!st.el || !(st.coin > st.coinSeen) || st.news === NEWS_VERSION) return;
      if (!showCoin(st)) coinLater(st);
    }, 1200);
  }

  function showCoin(st) {
    if (!st.el || !st.ctx || !st.mine || !visible(st) || guardOn(st) || H.api.overlayOpen(st)) return false;
    const c = COINS[Math.min(2, st.coin)];
    if (!c) return true;
    const svg = H.api.coinSvg ? H.api.coinSvg(c.kind) : '';
    const html = '<div class="clk-news clk-coin">' + (svg ? '<div class="clk-coin-pic">' + svg + '</div>' : '')
      + '<h3>' + c.title + '</h3><p>' + c.text + '</p>'
      + '<button type="button" class="primary clk-news-ok">Зрозуміло</button></div>';
    const v = st.coin;
    const body = H.api.overlay(st, html, { cls: 'clk-newsbox', onClose: () => order(st, 'coin', { v }) });
    const ok = body.querySelector('.clk-news-ok');
    if (ok) ok.onclick = () => H.api.closeOverlay(st);
    H.api.sfx('rare');
    return true;
  }

  /// Вікно чекає своєї черги: «поки тебе не було», мінігра чи Око майстра важливіші за новини. Пробуємо, доки
  /// не покажемо (чи доки сервер не скаже, що гончар уже бачив), — інакше той, хто хвилину читав «поки тебе не
  /// було», новин так і не побачив би до першого кліка.
  function newsLater(st) {
    clearTimeout(st.newsT);
    st.newsT = setTimeout(() => {
      if (!st.el || st.news !== NEWS_VERSION) return;
      if (!showNews(st)) newsLater(st);
    }, 800);
  }

  function showNews(st) {
    if (!st.el || !st.ctx || !st.mine || !visible(st) || guardOn(st) || H.api.overlayOpen(st)) return false;
    const li = (l) => '<li><span class="clk-news-ico">' + l[0] + '</span><span>' + l[1] + '</span></li>';
    // Хто пропустив «v10» — ті рядки; хто й «v9.2» — ще й ті; хто й «v9.1» — і ті (newsSeen — остання версія, яку
    // гончар бачив; порожньо — ще старіше за v9.1: тоді v9.1 уже й не згадуємо, як і було).
    const seen = st.newsSeen || '';
    const block = (n) => '<p class="muted small">' + n.lead + '</p><ul>' + n.lines.map(li).join('') + '</ul>';
    // v11.1: хто бачив v11 — лише нове. Хто ні — ще й «Толока» з подарунком, а далі ланцюжок, як був у v11: хто бачив
    // v10.1 — нічого; хто v10 — ще правки v10.1; хто й v10 не бачив — ще й v10, і так далі.
    // v11.2: хто бачив v11.1 — лише нове; хто ні — ще й рядки v11.1, а далі той самий ланцюжок.
    const miss111 = seen !== 'v11.1';
    const miss11 = miss111 && seen !== 'v11';
    const miss10 = miss11 && seen !== 'v10' && seen !== 'v10.1';
    const old = (miss111 ? block(NEWS_111) : '') + (!miss11 ? '' : block(NEWS_11)
      + (seen !== 'v10.1' ? block(NEWS_101) : '')
      + (miss10 ? block(NEWS_10) : '')
      + (miss10 && seen !== 'v9.2' ? block(NEWS_92) : '')
      + (seen && miss10 && seen !== 'v9.2' && seen !== 'v9.1' ? block(NEWS_OLD) : ''));
    const html = '<div class="clk-news"><h3>' + NEWS.title + '</h3><p class="muted small">' + NEWS.lead + '</p><ul>'
      + NEWS.lines.map(li).join('') + '</ul>' + old
      + '<button type="button" class="primary clk-news-ok">' + (miss11 ? NEWS.okGift : NEWS.ok) + '</button></div>';
    // Закрили кнопкою, хрестиком чи затемненням — байдуже: сервер однаково запише «бачив», і вдруге не покаже.
    const body = H.api.overlay(st, html, { cls: 'clk-newsbox', onClose: () => order(st, 'news', { v: NEWS_VERSION }) });
    const ok = body.querySelector('.clk-news-ok');
    if (ok) ok.onclick = () => H.api.closeOverlay(st);
    return true;
  }

  // ---------- худий вид (v10 §10) ----------

  /// Вид приходить «худим»: назви, описи й ціни верстатів, віх, секретів і розписів лежать у каталозі
  /// (view.shopCatalog — лише до першої дії й на look { catalog: true }). Доповнюємо вид тут, до того як його побачать
  /// ядро й частини: для них усе як і було. false — каталогу ще нема (перше відкриття після перезапуску сервера).
  function hydrate(st, v) {
    if (v.shopCatalog) st.shopCat = v.shopCatalog;
    const c = st.shopCat;
    // Повний вид (сервер ще до v10 — хвилина деплою — або сервер ще не знає, що ми нові): доповнювати нема чого.
    const fat = Object.values(v.upgrades || {}).some((u) => u && u.name);
    if (!c) return fat;
    const ups = v.upgrades || {};
    for (const k of Object.keys(ups)) {
      const u = ups[k];
      const s = c.upgrades && c.upgrades[k];
      if (!s) continue;
      u.name = s.name;
      u.desc = s.desc;
      u.kind = s.kind;
      u.growth = s.growth;
      // Вид міг прийти вдруге (refreshCard) уже доповненим — тоді nextMark уже об'єкт, лишаємо як є.
      if (typeof u.nextMark === 'number') u.nextMark = (s.marks || []).find((m) => m.level === u.nextMark) || null;
    }
    if (Array.isArray(v.marks)) {
      v.marks = v.marks.map((m) => {
        const [on, lv] = String(m.key).split(':');
        const s = c.upgrades && c.upgrades[on];
        const mk = s && (s.marks || []).find((x) => x.level === +lv);
        if (m.name) return m;                                   // повний вид — віха вже з назвою й ціною
        return mk ? { key: m.key, on, level: +lv, name: mk.name, desc: mk.desc, price: mk.price, effect: mk.effect, amount: mk.amount } : null;
      }).filter(Boolean);
    }
    const owned = (list) => new Set((list || []).filter((x) => x.owned).map((x) => x.key));
    if (Array.isArray(v.secrets) && c.secrets) {
      const own = owned(v.secrets);
      v.secrets = c.secrets.map((s) => ({ ...s, owned: own.has(s.key) }));
    }
    if (Array.isArray(v.styles) && c.styles) {
      const own = owned(v.styles);
      v.styles = c.styles.map((s) => ({ ...s, owned: own.has(s.key) }));
    }
    return true;
  }

  // ---------- api для частин ----------

  /// Новий вузол вмісту вікна щоразу: відповідь сервера, що запізнилась (хата друга, мінігра), перевіряє
  /// body.isConnected — і більше не перепише чуже вікно, відкрите за цей час.
  function freshBody(st) {
    const nb = document.createElement('div');
    nb.className = 'clk-ov-body';
    st.ov.body.replaceWith(nb);
    st.ov.body = nb;
  }

  H.api = {
    short, num, dec, big, span, plural, potsWord, potsShort, shards, mmss, jug, jugSvg, STYLE, swap, fleeting, serverNow, visible,
    // Десяте оновлення: число без одиниці (лічильники виробів, клейм) і одиниця грошей для великих сум.
    count, unit,
    storeGet, storeSet,
    guardOn: (st) => guardOn(st),
    info,
    esc: (st, x) => ((st.ctx && st.ctx.esc) || ((y) => String(y)))(x),
    order: (st, action, payload) => order(st, action, payload),
    /// Дія з відповіддю (Promise): для мінігор, яким треба знати результат. Накопичені кліки летять першими.
    act: (st, action, payload) => { if (!st.ctx || !st.mine) return Promise.resolve(null); flush(st); return st.ctx.act(action, payload); },
    popAt: (st, text, cls, x, y) => popAt(st, text, cls, x, y),
    sparks: (st, host, n, gold, x, y) => sparks(st, host || st.fx, n, gold, x, y),
    toast: (st, text, kind) => { if (st.ctx && st.ctx.toast) st.ctx.toast(text, kind || 'ok'); },
    /// Звук: без живої хати (clicker-scene.js) — тиша; сцена підміняє цю функцію.
    sfx: () => {},
    /// Силует виробу: малює ремесло (clicker-craft.js); доти — глечик.
    wareSvg: (ware, o) => jugSvg((o && o.style) || '', (o && o.cls) || '', (o && o.slot) || 'w-' + ware, o && o.clay),
    /// Вкладка частини: кнопка стає за порядком order серед наявних, панель — у праву колонку. Повертає панель.
    tab(st, key, label, order) {
      if (st.panes[key]) return st.panes[key];
      const b = document.createElement('button');
      b.type = 'button';
      b.className = 'ghost';
      b.dataset.tab = key;
      b.dataset.order = String(order || 50);
      b.textContent = label;
      const after = [...st.tabs.querySelectorAll('[data-tab]')].find((x) => +(x.dataset.order || 50) > (order || 50));
      st.tabs.insertBefore(b, after || null);
      st.tabText[key] = label;
      labelTab(st, key);
      b.onclick = () => { H.api.sfx('tap'); setTab(st, key); };
      const pane = document.createElement('div');
      pane.className = 'clk-pane';
      pane.dataset.pane = key;
      pane.hidden = true;
      st.el.querySelector('.clk-side').appendChild(pane);
      st.panes[key] = pane;
      st.tabText[key] = label;
      // Гравець лишив цю вкладку відкритою минулого разу — повернути, щойно вона з'явилась.
      if (storeGet('clk.tab', 'shop') === key) setTab(st, key);
      return pane;
    },
    tabLabel(st, key, text) {
      st.tabText[key] = text;
      labelTab(st, key);
    },
    /// Коротка нотатка на ярлику («🔥0:12», «📜2», «🛒»): кожна частина пише свою, а показуємо лише найважливішу —
    /// п'ять ярликів мусять улізти в 375 px, тож два-три хвости на одному з них цього не варті.
    tabNote(st, key, id, text, prio) {
      const notes = st.tabNotes[key] || (st.tabNotes[key] = {});
      const was = notes[id];
      if (was && was.text === (text || '') && was.prio === (prio || 5)) return;
      notes[id] = { text: text || '', prio: prio || 5 };
      labelTab(st, key);
    },
    /// Гейт вкладки: поки функція каже «ще ні» — ярлика нема. Умова читається з виду, тож гравець із прогресом
    /// бачить усе своє одразу, а новачок — лише те, що вже може робити.
    showWhen(st, key, fn) {
      st.gates[key] = fn;
      gateTabs(st, st.lastView);
    },
    /// Стрічка подій під смугою (її веде ярмарок, clicker-fair.js): ядро лише каже, що відкрилось.
    feed: () => {},
    /// Перемалювати вивіску (звання кладуть свої значки в st.titleIcons і просять показати їх одразу).
    sign: (st) => { if (st.sign) paintSign(st); },
    /// Іменоване місце всередині чужої панелі: так горно, комора й замовлення живуть в одному «Ремеслі».
    slot: (st, name) => (st.el ? st.el.querySelector('[data-slot="' + name + '"]') : null),
    showTab: (st, key) => setTab(st, key),
    /// Свій шар частини: back — <g> у SVG під колом (viewBox 360×450, опорні точки — api.scene з clicker-scene.js),
    /// front — <div> над сценою (координати у відсотках сцени).
    layer(st, name, id) {
      const host = name === 'back' ? st.back : st.front;
      let el = host.querySelector('[data-part="' + id + '"]');
      if (!el) {
        el = name === 'back' ? document.createElementNS('http://www.w3.org/2000/svg', 'g') : document.createElement('div');
        el.setAttribute('data-part', id);
        host.appendChild(el);
      }
      return el;
    },
    /// Модальна панель поверх картки. Одна за раз: нова закриває попередню (з її onClose).
    /// opts.keep — функція «зараз не на часі закривати» (мінігра в розпалі): Око майстра її перечекає.
    overlay(st, html, opts) {
      if (!st.ov.el.hidden) H.api.closeOverlay(st);
      freshBody(st);
      st.ovDownBack = true;
      st.ov.body.innerHTML = html;
      st.ov.el.className = 'clk-overlay' + (opts && opts.cls ? ' ' + opts.cls : '');
      st.ov.onClose = (opts && opts.onClose) || null;
      st.ov.keep = (opts && opts.keep) || null;
      st.ov.el.hidden = false;
      H.api.placeOverlay(st);
      return st.ov.body;
    },
    /// Де стоїть вікно і скільки йому висоти — одне правило на всі вікна (F5, 08.10; горно кличе його ж, коли
    /// домалювало вміст). Вікно — у тій частині картки, яку справді видно: між липкою шапкою сайту й нижнім меню
    /// телефона і не нижче за саму картку. Не влазить — гортається саме вікно, а не шар під ним (раніше горно
    /// рахувало висоту від екрана, і на макбуку низ вікна обрізав край картки разом із «🎲 Навмання»).
    /// Картка вища за екран (телефон), а вікно не влазить під її верх — сторінку підгортаємо рівно на різницю.
    /// Усе міряне — у видимих пікселях, а пишемо в пікселях картки: під масштабом 2K/4K ділимо на z.
    placeOverlay(st) {
      if (!st.ov || st.ov.el.hidden) return;
      const box = st.ov.el.firstElementChild;
      const z = zoomOf(st);
      const pad = 12 * z;                                     // відступ .clk-overlay (css), у видимих px
      const band = viewBand();
      box.style.marginTop = '0px';
      box.style.maxHeight = 'none';
      let r = st.el.getBoundingClientRect();
      if (!st.el.classList.contains('fit')) {
        const want = Math.min(box.getBoundingClientRect().height, band.bottom - band.top - 16);
        const d = Math.min(r.top + pad + want - (band.bottom - 8), r.top + pad - (band.top + 8));
        if (d > 0) { window.scrollBy(0, d); r = st.el.getBoundingClientRect(); }
      }
      const lo = r.top + pad;                                 // верх вікна при marginTop 0
      const hi = Math.min(band.bottom - 8, r.bottom - pad);   // нижче — або меню, або край картки
      const mt = Math.max(0, Math.min(band.top + 8 - lo, hi - lo - 240 * z));
      const mh = Math.max(Math.min(240 * z, r.height - 2 * pad), hi - lo - mt);
      box.style.marginTop = Math.round(mt / z) + 'px';
      box.style.maxHeight = Math.floor(mh / z) + 'px';
    },
    /// Масштаб столу на 2K/4K (1 — без нього): частинам, що пишуть у картку пікселі, зміряні getBoundingClientRect.
    zoom: (st) => zoomOf(st),
    closeOverlay(st) {
      if (!st.ov || st.ov.el.hidden) return;
      st.ov.el.hidden = true;
      const f = st.ov.onClose;
      st.ov.onClose = null;
      st.ov.keep = null;
      freshBody(st);
      if (f) try { f(); } catch (e) { console.error(e); }
    },
    overlayOpen: (st) => !!st.ov && !st.ov.el.hidden,
    /// Вікно просить не чіпати себе (мінігра, де вже водять пальцем).
    overlayBusy(st) {
      if (!st.ov || st.ov.el.hidden || !st.ov.keep) return false;
      try { return !!st.ov.keep(); } catch (e) { console.error(e); return false; }
    },
  };

  // ---------- компонування стола (десяте оновлення, docs/games/specs/clicker-v10.md §9) ----------

  /// Картка стола (.gtable), у .gbody якої каркас змонтував коло.
  const cardOf = (st) => (st.root && st.root.closest && st.root.closest('.gtable')) || null;

  const FIT_W = 900;          // картка від 900 px — є що ділити на два стовпці (той самий поріг, що @container clk у css)
  const FIT_H = 700;          // вікно нижче 700 px — одна прокрутка сторінки, як і було (@media (max-height: 699px))
  /// Стіл нижчий за це (1536×864, 1366×768) — смуга «Шлях виробу» переходить нагору правої колонки. Під сценою вона
  /// з'їдала б ~100 px висоти, а сцена 4:5 за кожен піксель висоти віддає 0,8 px ширини: на 768 px заввишки коло
  /// лишилось би 113 px, а так — 150 px.
  const SIDE_PATH_H = 800;
  const PHONE_NEXT_H = 6 + 50 + 6 + 4;   // телефон: зазор, рядок «Далі» (50 px), зазор сцени й запас над меню
  /// Телефон лежачи (844×390, 932×430): вікно нижче FIT_H, тож стіл ішов однією прокруткою, і коло (104 px) з'являлось
  /// лише після прокрутки, а «Далі» лежало під нижнім меню. Тут свій режим .land: сцена ліворуч липне між шапкою й
  /// меню на всю їхню відстань, права колонка гортається сторінкою (див. .clk.land у clicker.css).
  const LAND_MQ = window.matchMedia ? window.matchMedia('(pointer: coarse) and (max-height: 500px) and (min-width: 561px)') : null;
  const LAND_W = 440;         // вужче — сцені поруч із лічильником нема місця (з 640 px ще й два стовпці, див. css)

  /// Обгортка сцени тримає лише сцену й Око майстра. Частини ставлять свої рядки «одразу після сцени»
  /// (st.stage.insertAdjacentElement('afterend', …) — так робить смуга «Шлях виробу» в clicker-craft.js), і такий
  /// рядок опинився б усередині обгортки, яка на ПК забирає всю вільну висоту стовпчика. Тож усе, що з'явилось в
  /// обгортці поруч зі сценою, одразу переносимо за неї в тому самому порядку: MutationObserver встигає до малювання.
  function keepBox(st) {
    const box = st.stageBox;
    const own = (e) => e.classList.contains('clk-stage') || e.classList.contains('clk-eye');
    const mo = new MutationObserver(() => {
      const extra = [...box.children].filter((e) => !own(e));
      if (extra.length) box.after(...extra);
    });
    mo.observe(box, { childList: true });
    return mo;
  }

  /// Кличеться на кожен вид. Рядки, що переходять між стовпцями, ставимо на місце щоразу (частини вантажаться пізніше
  /// за ядро й кладуть свої рядки самі), а міряємо стіл лише тоді, коли картка щойно стала видною: каркас монтує її
  /// раз і далі лише ховає, тож міра — не mount, а мить появи. Далі стіл стежить сам (fitWatch).
  function placeInView(st) {
    const card = cardOf(st);
    if (!card) return;
    // Стежимо з першого виду, навіть схованої картки: коли її покажуть, ResizeObserver сам покличе fitTable.
    if (!st.fitWatch) st.fitWatch = fitWatch(st);
    if (card.hidden || !card.isConnected) { st.inView = false; return; }
    placeRows(st);
    if (st.inView) return;
    st.inView = true;
    fitTable(st);
  }

  /// ПК: стіл рівно в один екран, одна прокрутка. Висота .clk-lay — це висота вікна (100dvh у css) мінус те, що
  /// сторінка має над столом (шапка сайту, рядок «← Лобі», відступи) і під ним (рядок «Закрити», відступ main). Обидва
  /// числа міряємо, а не вгадуємо: шапка буває вищою, над столом може стати плашка нічного відбою, у «⛶» відступи
  /// інші. Решту робить css: сцена забирає всю висоту, що лишилась у стовпчику, а полиця гортається сама в собі.
  /// Вузьке (< 900) чи низьке (< 700) вікно — стіл як був: одна прокрутка сторінки.
  function fitTable(st) {
    const card = cardOf(st);
    if (!st.el || !card || card.hidden || !card.isConnected) { setZoom(st, 1); return; }
    // Спершу дешеві перевірки (без перерахунку розкладки): телефон сюди потрапляє на кожну зміну висоти сторінки.
    let fit = window.innerWidth >= FIT_W && window.innerHeight >= FIT_H;
    if (fit) {
      // Вкладка «Ефір» чи інший розділ — міряти нема чого, а сторінці, що не стіл кола, ширша стеля ні до чого.
      if (!st.el.getClientRects().length) { setZoom(st, 1); return; }
      // Масштаб — до міри ширини: від нього залежить, скільки картки лишається в її власних пікселях.
      setZoom(st, bigZoom());
      fit = st.el.clientWidth >= FIT_W;
    }
    if (!fit) setZoom(st, 1);
    if (st.el.classList.contains('fit') !== fit) st.el.classList.toggle('fit', fit);
    if (card.classList.contains('clk-fit') !== fit) card.classList.toggle('clk-fit', fit);
    placeStatus(st, card, fit);
    let side = false;
    if (fit) {
      const lr = st.layEl.getBoundingClientRect();
      const above = Math.ceil(lr.top + window.scrollY);
      // Під столом: від низу .clk-lay до низу картки (рядок «Закрити»), далі — від картки до main (обгортки каркаса),
      // і нижній відступ самого main. Саму висоту main не беремо: її може задавати колонка балачок, а не стіл.
      let below = card.getBoundingClientRect().bottom - lr.bottom;
      let e = card;
      for (; e.parentElement && e.parentElement.tagName !== 'MAIN' && e.parentElement !== document.body; e = e.parentElement) {
        below += e.parentElement.getBoundingClientRect().bottom - e.getBoundingClientRect().bottom;
      }
      if (e.parentElement && e.parentElement.tagName === 'MAIN') {
        const cs = getComputedStyle(e.parentElement);
        below += (parseFloat(cs.paddingBottom) || 0) + (parseFloat(cs.borderBottomWidth) || 0);
      }
      below = Math.ceil(below);
      // Угору — з запасом у піксель: дробова частина дала б сторінці 1 px прокрутки, а з нею і смугу прокрутки.
      if (st.fitAbove !== above) { st.fitAbove = above; st.el.style.setProperty('--clk-above', above + 'px'); }
      if (st.fitBelow !== below) { st.fitBelow = below; st.el.style.setProperty('--clk-below', below + 'px'); }
      // Поріг — у пікселях самої картки: під масштабом 2K стіл 1305 px заввишки — це «1004 px» стола.
      side = (window.innerHeight - above - below) / zoomOf(st) < SIDE_PATH_H;
    }
    if (st.el.classList.contains('pathside') !== side) st.el.classList.toggle('pathside', side);
    landFit(st, card, !fit && !!LAND_MQ && LAND_MQ.matches && st.el.clientWidth >= LAND_W);
    if (!fit) phoneFit(st);
    placeRows(st);
  }

  /// Режим «лежачи»: висоту сцени дає смуга між липкою шапкою й нижнім меню (--clk-land-top/--clk-land-bot). Щойно
  /// стіл став лежачим (відкрили коло чи повернули телефон), сторінка доїжджає так, щоб сцена стала під шапку: над нею
  /// лише рядок «← Лобі», і без цього коло з «Далі» лягали б під меню. Далі прокрутку веде гравець.
  function landFit(st, card, land) {
    const was = st.el.classList.contains('land');
    if (was !== land) { st.el.classList.toggle('land', land); card.classList.toggle('clk-land', land); }
    if (!land) return;
    const band = viewBand();
    const top = Math.round(band.top), bot = Math.round(window.innerHeight - band.bottom);
    if (st.landTop !== top) { st.landTop = top; st.el.style.setProperty('--clk-land-top', top + 'px'); }
    if (st.landBot !== bot) { st.landBot = bot; st.el.style.setProperty('--clk-land-bot', bot + 'px'); }
    if (!was) {
      requestAnimationFrame(() => {
        if (!st.el || !st.el.classList.contains('land')) return;
        const y = window.scrollY + st.layEl.getBoundingClientRect().top - top - 4;
        if (y > window.scrollY + 1) window.scrollTo({ top: y, behavior: 'auto' });
      });
    }
  }

  /// 2K і 4K (F1, 08.10): стеля сторінки 1600 px лишала стіл 1152 px посеред 2560 чи 3840, а кегль і коло — як на
  /// Full HD (по 480 і 1120 px порожнечі обабіч). Тепер стіл ширшає разом із вікном, а все в ньому — рівно в z разів:
  /// zoom на тілі картки (clicker.css, .gtable.clk-big), стеля main — 1600·z (style.css, body.clk-big). Усередині
  /// картка живе у своїх пікселях: стіл 3840×2160 — це стіл 1920×1080, збільшений удвічі, тож уся верстка й медіазапити
  /// контейнера лишаються ті самі, що на Full HD. Крок 0,05, щоб дрібна зміна вікна не перемальовувала все; до 1,05 —
  /// без zoom зовсім (макбуки й Full HD не міняються ні на піксель).
  const ZOOM_W = 1920, ZOOM_H = 1000, ZOOM_MAX = 2;
  let zoomOk = null;
  /// Стандартний zoom (Chrome від 128, Firefox від 126, Safari): прямокутники й clientX — у видимих пікселях, тож
  /// частка кліку на колі, глеку чи полотні горна сходиться. Старий Chrome рахував прямокутники всередині zoom інакше —
  /// там краще без масштабу, ніж коло, що ловить клік не там.
  function zoomWorks() {
    if (zoomOk !== null) return zoomOk;
    zoomOk = false;
    try {
      if (window.CSS && CSS.supports && CSS.supports('zoom', '2')) {
        const d = document.createElement('div');
        d.style.cssText = 'zoom:2;position:absolute;left:-9999px;top:0;visibility:hidden';
        d.innerHTML = '<div style="width:100px;height:10px"></div>';
        document.body.appendChild(d);
        zoomOk = Math.round(d.firstChild.getBoundingClientRect().width) === 200;
        d.remove();
      }
    } catch { zoomOk = false; }
    return zoomOk;
  }
  function bigZoom() {
    const z = Math.floor(Math.min(window.innerWidth / ZOOM_W, window.innerHeight / ZOOM_H, ZOOM_MAX) * 20) / 20;
    return z >= 1.05 && zoomWorks() ? z : 1;
  }
  const zoomOf = (st) => st.z || 1;
  function setZoom(st, z) {
    if (zoomOf(st) === z) return;
    st.z = z;
    const card = cardOf(st);
    const big = z > 1;
    if (big) document.body.style.setProperty('--clk-z', String(z));
    else document.body.style.removeProperty('--clk-z');
    document.body.classList.toggle('clk-big', big);
    if (card) card.classList.toggle('clk-big', big);
  }

  /// Рядок «усього наліплено … · обміняно сьогодні …» (статус каркаса) на ПК — у підвал правої колонки (F4): під
  /// карткою він забирав цілий рядок висоти сцені. Сам елемент той самий (каркас пише в нього за посиланням), лише
  /// переходить у підвал і назад.
  function placeStatus(st, card, fit) {
    const s = card.querySelector(':scope > .gstatus') || (st.shards && st.shards.parentElement.querySelector(':scope > .gstatus'));
    if (!s) return;
    if (fit) { if (s.parentElement !== st.sideEl) st.sideEl.appendChild(s); }
    else if (s.parentElement !== card) { const b = card.querySelector(':scope > .gbody'); if (b) b.after(s); }
  }

  /// Частина вікна, яку не закривають липка шапка сайту згори й нижнє меню (телефон) знизу, — у px від верху вікна.
  function viewBand() {
    const h = window.innerHeight;
    let top = 0, bottom = h;
    const hdr = document.querySelector('body > header, header');
    if (hdr && hdr.getClientRects().length && /sticky|fixed/.test(getComputedStyle(hdr).position)) {
      top = Math.max(0, Math.min(h / 3, hdr.getBoundingClientRect().bottom));
    }
    const nav = document.querySelector('nav.mtabs');
    if (nav && nav.getClientRects().length && getComputedStyle(nav).position === 'fixed') {
      bottom = Math.max(h * 2 / 3, Math.min(h, nav.getBoundingClientRect().top));
    }
    return { top, bottom };
  }

  /// Телефон (≤ 560 px): сцена така, щоб під нею над нижнім меню сайту вміщались смуга «Шлях виробу» й рядок «Далі»
  /// з кнопкою кроку. Міряємо, де сцена починається на сторінці і скільки заввишки смуга; висоту вікна й меню бере css
  /// (100svh і --gdock-h, див. кінець clicker.css). Рядок «Далі» то є, то нема — місце під нього тримаємо завжди,
  /// інакше сцена стрибала б щоразу, як він з'являється.
  function phoneFit(st) {
    if (window.innerWidth > 560) return;
    const box = st.el.querySelector('.clk-stagebox');
    if (!box || !box.getClientRects().length) return;
    const top = Math.round(box.getBoundingClientRect().top + window.scrollY);
    const steps = st.el.querySelector('.clk-path .clk-steps');
    const path = steps && steps.getClientRects().length ? Math.ceil(steps.getBoundingClientRect().height) + PHONE_NEXT_H : 0;
    if (st.phTop !== top) { st.phTop = top; st.el.style.setProperty('--clk-ph-top', top + 'px'); }
    if (st.phPath !== path) { st.phPath = path; st.el.style.setProperty('--clk-ph-path', path + 'px'); }
  }

  /// Стіл міряє себе знову, коли щось зрушило: вікно, картка (балачки згорнули, «⛶»), висота сторінки (над столом
  /// з'явився рядок). Через кадр, а не в самому ResizeObserver: зміна висоти стола там же викликала б його знову.
  function fitWatch(st) {
    let raf = 0;
    const again = () => { if (!raf) raf = requestAnimationFrame(() => { raf = 0; fitTable(st); }); };
    const ro = window.ResizeObserver ? new ResizeObserver(again) : null;
    const card = cardOf(st);
    if (ro) { ro.observe(document.body); if (card) ro.observe(card); }
    window.addEventListener('resize', again);
    return { stop() { if (ro) ro.disconnect(); window.removeEventListener('resize', again); cancelAnimationFrame(raf); } };
  }

  /// Рядки, що переходять між стовпцями, коли стіл стоїть в один екран: прилавок черепків — у підвал правої колонки
  /// (обмін на черепки буває раз на день, а сцені це ще 60 px висоти), смуга «Шлях виробу» на низькому вікні —
  /// нагору правої колонки. Поза цим режимом усе вертається під сцену, як було.
  function placeRows(st) {
    if (!st.el || !st.sellRow || !st.one) return;
    // Лежачи сцена — лише коло з шапкою й смугою: прилавок, плашки й стрічка йдуть у праву колонку, як і на ПК.
    const fit = st.el.classList.contains('fit') || st.el.classList.contains('land');
    // Сам .clk-sell лишається під сценою: перед ним ставить себе стрічка подій (clicker-fair.js). Ходять лише кнопки
    // й рядок «сьогодні ще …» — ті самі елементи, тож обробники й st.one / st.all не міняються.
    if (fit) {
      if (st.one.parentElement !== st.shards) st.shards.append(st.one, st.all, st.left);
    } else if (st.one.parentElement !== st.sellRow) {
      st.sellRow.append(st.one, st.all);
      st.sellRow.after(st.left);
    }
    // Стіл в один екран: плашки бафів і стрічка подій ідуть у праву колонку (F3) — плашки нагору (під смугу «Шлях
    // виробу», коли вона там), стрічка в підвал над прилавком. Під сценою вони з'їдали ~100 px висоти, а сцена за
    // кожен піксель висоти віддає 0,8 px ширини. Плашки переходять цілим контейнером: що в них — не чіпаємо.
    const side = fit && st.el.classList.contains('pathside');
    const buffs = st.buffsEl || (st.buffsEl = st.el.querySelector('.clk-buffs'));
    const feed = st.el.querySelector('.clkf-feed');
    if (fit) {
      if (feed && feed.parentElement !== st.sideEl) st.shards.before(feed);
      if (buffs && !side && buffs.parentElement !== st.sideEl) st.sideEl.prepend(buffs);
    } else {
      if (buffs && buffs.parentElement !== st.sceneEl) st.sellRow.before(buffs);
      if (feed && feed.parentElement !== st.sceneEl) st.sellRow.before(feed);
    }
    const path = st.sceneEl.querySelector(':scope > .clk-path') || st.sideEl.querySelector(':scope > .clk-path');
    if (side) {
      if (path && path.parentElement !== st.sideEl) st.sideEl.prepend(path);
      if (buffs && buffs.previousElementSibling !== path) { if (path) path.after(buffs); else if (buffs.parentElement !== st.sideEl) st.sideEl.prepend(buffs); }
    } else if (path && path.previousElementSibling !== st.stageBox) st.stageBox.after(path);
  }

  /// Погляд стоїть на місці, коли вище щось виросло чи зникло. Chrome і Firefox тримають його самі (scroll anchoring),
  /// а Safari — ні: на айфоні палій розпалював горно, «підготовка» й «Останнє горно» вгорі «Ремесла» ховались
  /// (~500 px), після обпалу вертались — і комора з ярмарком, які людина гортала внизу, підстрибували вгору.
  /// Тут те саме вручну: на кожну прокрутку запам'ятовуємо якір посеред екрана з предками до картки й де кожен стояв.
  /// Зміна вище міняє розмір когось із предків — ResizeObserver кличе нас ще до малювання, і ми вертаємо першого
  /// живого й видного з ланцюжка на його місце. Там, де браузер уміє сам, — нічого не робимо.
  ///
  /// Якір — лише нерухомий HTML-блок (stillAnchor). До v10 ним ставав будь-який елемент під 40 % екрана, а там,
  /// коли людина грає, якраз коло: диск, що крутиться, виріб, що ліпиться, руки гончаря, глек, що росте від розгону.
  /// Їхній getBoundingClientRect ходить разом з анімацією, і кожна зміна висоти поруч (бафи з'явились чи зникли)
  /// «вирівнювала» сторінку на висоту анімації — коло смикалось саме по собі на 5–26 px.
  function steadyView(st) {
    if (!window.ResizeObserver || (window.CSS && CSS.supports && CSS.supports('overflow-anchor', 'auto'))) return null;
    let chain = [];                                   // [[елемент, top у вікні]] від якоря до картки
    let watched = [];                                 // якір і всі предки до body: зсув НАД карткою теж ловимо
    const shown = (e) => e.isConnected && e.getClientRects().length > 0;
    const ro = new ResizeObserver(() => {
      const link = chain.find(([e]) => shown(e));
      if (!link) return;
      const d = link[0].getBoundingClientRect().top - link[1];
      if (Math.abs(d) < 1) return;
      window.scrollBy(0, d);
      for (const l of chain) if (shown(l[0])) l[1] = l[0].getBoundingClientRect().top;
    });
    function pick() {
      const card = cardOf(st) || st.root;
      // Нагорі сторінки якір не потрібен (так само й у Chrome), а на прихованій картці — нема чого тримати.
      const hit = window.scrollY > 0 && !card.hidden && card.isConnected
        ? document.elementFromPoint(window.innerWidth / 2, window.innerHeight * 0.4) : null;
      const anchor = hit && card.contains(hit) ? stillAnchor(hit, card) : null;
      if (!anchor) { if (watched.length) { ro.disconnect(); watched = []; } chain = []; return; }
      if (anchor === (chain[0] && chain[0][0])) {
        // Той самий якір (гортаємо далі по тому самому блоку) — лише нові координати, без переписки спостерігача.
        for (const l of chain) l[1] = l[0].getBoundingClientRect().top;
        return;
      }
      ro.disconnect();
      chain = [];
      watched = [];
      for (let e = anchor; e; e = e.parentElement) {
        if (e === document.documentElement) break;
        if (card.contains(e)) chain.push([e, e.getBoundingClientRect().top]);
        watched.push(e);
        ro.observe(e);
      }
    }
    // Синхронно, не через кадр: app.js на зміну адреси спершу гортає вгору, а вже потім ховає картку.
    window.addEventListener('scroll', pick, { passive: true });
    return { stop() { window.removeEventListener('scroll', pick); ro.disconnect(); chain = []; watched = []; } };
  }

  /// Що може бути якорем прокрутки. Клік усередині сцени — сама .clk-stage: під пальцем там крутиться коло, ліпиться
  /// виріб, бігає кіт, і жоден із них не стоїть на місці. Поза сценою — найглибший HTML-елемент, у якого ні сам він,
  /// ні предки до картки не мають transform чи анімації (рядок верстата, що блимає після купівлі, — ні; його полиця — так)
  /// і не липнуть. Усередині блоку, що гортається сам (полиця на ПК, вікно частини), якорем стає сам цей блок:
  /// його вміст рухається прокруткою, а не розкладкою, і тримати там нема чого.
  function stillAnchor(hit, card) {
    const stage = hit.closest && hit.closest('.clk-stage');
    const from = stage && card.contains(stage) ? stage : hit;
    const path = [];
    for (let e = from; e && e !== card; e = e.parentElement) path.push(e);
    const kind = (e) => {
      if (!(e instanceof HTMLElement)) return 'moving';               // SVG: диск, руки, виріб на колі
      const cs = getComputedStyle(e);
      if (cs.transform !== 'none' || cs.animationName !== 'none' || cs.position === 'sticky' || cs.position === 'fixed'
        || (cs.translate && cs.translate !== 'none') || (cs.rotate && cs.rotate !== 'none') || (cs.scale && cs.scale !== 'none')) return 'moving';
      return /auto|scroll/.test(cs.overflowY) ? 'scroller' : 'still';
    };
    if (kind(card) === 'moving') return null;
    let anchor = card;
    for (let i = path.length - 1; i >= 0; i--) {
      const k = kind(path[i]);
      if (k === 'moving') break;
      anchor = path[i];
      if (k === 'scroller') break;
    }
    return anchor;
  }

  /// Ряд вкладок під мініплашкою (F7, 08.10): на телефоні «Майстерня» пізньої гри — 9 000 px, «Село» — 6 900, і щоб
  /// перейти на іншу вкладку з глибини, треба було гортати назад тисячі пікселів (плашка вела лише до кола). Тепер під
  /// плашкою — значки вкладок: дотик перемикає вкладку й ставить її початок під плашку. Ряд живе окремо від pinView:
  /// стежить лише за тим, чи плашку показали, і щоразу збирає значки з наявних ярликів (гейти, активна).
  function pinTabs(st) {
    const pin = st.el.querySelector('.clk-pin');
    if (!pin || !window.MutationObserver) return null;
    const row = document.createElement('div');
    row.className = 'clk-pintabs';
    row.setAttribute('role', 'tablist');
    row.setAttribute('aria-label', 'Вкладки');
    pin.appendChild(row);
    const build = () => {
      const tabs = [...st.tabs.querySelectorAll('[data-tab]')].filter((b) => !b.hidden);
      row.replaceChildren(...tabs.map((t) => {
        const ico = (t.querySelector('.clk-tico') || {}).textContent || '';
        const word = (t.querySelector('.clk-tword') || t).textContent || '';
        const x = document.createElement('button');
        x.type = 'button';
        x.className = 'ghost' + (t.dataset.tab === st.tab ? ' active' : '');
        x.dataset.tab = t.dataset.tab;
        x.title = word;
        x.setAttribute('aria-label', word);
        x.textContent = ico || word.slice(0, 1);
        return x;
      }));
    };
    row.addEventListener('click', (e) => {
      const b = e.target.closest('[data-tab]');
      if (!b) return;
      H.api.sfx('tap');
      setTab(st, b.dataset.tab);
      build();
      // Початок вкладки — під плашкою з рядом (шапка сайту + ~100 px), а не під самою шапкою: інакше її закриє ряд.
      // Міряємо в наступному кадрі: нова панель іншої висоти вже стала на місце. Поки гортаємо, браузер не тримає
      // якір прокрутки в картці: панель, що домальовує себе (Альбом), інакше «доправляла» сторінку ще на 100+ px.
      st.el.style.overflowAnchor = 'none';
      clearTimeout(st.pinAnchorT);
      st.pinAnchorT = setTimeout(() => { st.el.style.overflowAnchor = ''; }, 1500);
      requestAnimationFrame(() => {
        const head = document.querySelector('body > header');
        const top = (head ? Math.max(0, head.getBoundingClientRect().bottom) : 0) + 100;
        const y = window.scrollY + st.tabs.getBoundingClientRect().top - top;
        const calm = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        window.scrollTo({ top: Math.max(0, y), behavior: calm ? 'auto' : 'smooth' });
      });
    });
    const mo = new MutationObserver(() => { if (!pin.hidden) build(); });
    mo.observe(pin, { attributes: true, attributeFilter: ['hidden'] });
    return { stop() { mo.disconnect(); row.remove(); } };
  }

  /// Мініплашка: коло прокрутили з екрана (гравець пішов до полиць) — згори липне «🏺 число · ⤒ до кола», і дотик
  /// вертає до кола. Число плашка не рахує: копіює текст лічильника (st.count / st.unit), щойно той змінився, і лише
  /// поки її видно. На ПК, де стіл стоїть в один екран, коло з екрана не зникає — плашки там і не буде.
  function pinView(st) {
    const el = st.el.querySelector('.clk-pin');
    if (!el || !window.IntersectionObserver) return null;
    const btn = el.querySelector('.clk-pinbtn');
    const num = el.querySelector('.clk-pinnum');
    const unitEl = el.querySelector('.clk-pinunit');
    let io = null, mo = null, on = false, top = -1, copyT = 0;
    // Число на плашці — не щокадру: плашка липка й межею розкладки не буває, тож кожна нова цифра — розкладка
    // сторінки, а лічильник міняється до 60 разів на секунду (пакет B, §10). Чотири рази на секунду оку досить.
    const soon = () => { if (!copyT) copyT = setTimeout(() => { copyT = 0; if (on) copy(); }, 250); };
    const copy = () => {
      const n = st.count.textContent;
      const u = st.unit.textContent;
      if (num.textContent !== n) num.textContent = n;
      if (unitEl.textContent !== u) unitEl.textContent = u;
    };
    const show = (v) => {
      if (on === v) return;
      on = v;
      el.hidden = !v;
      if (!v) { if (mo) mo.disconnect(); return; }
      copy();
      mo = mo || new MutationObserver(soon);
      for (const x of [st.count, st.unit]) mo.observe(x, { childList: true, characterData: true, subtree: true });
    };
    // Шапка сайту липне згори — плашка стає під нею. Висота шапки міняється хіба з розміром вікна.
    function watch() {
      const head = document.querySelector('body > header');
      const t = head ? Math.max(0, Math.round(head.getBoundingClientRect().bottom)) : 0;
      if (t === top && io) return;
      top = t;
      el.style.setProperty('--clk-pin-top', t + 'px');
      if (io) io.disconnect();
      // Коло «з екрана» — обгортка сцени вся вище за шапку. Зникла з розкладки (картку сховали) — плашки теж нема.
      io = new IntersectionObserver(([en]) => {
        const r = en.boundingClientRect;
        const rootTop = en.rootBounds ? en.rootBounds.top : t;
        show(!en.isIntersecting && r.height > 0 && r.bottom <= rootTop + 1);
      }, { rootMargin: '-' + t + 'px 0px 0px 0px' });
      io.observe(st.stageBox);
    }
    btn.addEventListener('click', () => {
      H.api.sfx('tap');
      const calm = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
      const y = window.scrollY + st.sceneEl.getBoundingClientRect().top - top - 8;
      window.scrollTo({ top: Math.max(0, y), behavior: calm ? 'auto' : 'smooth' });
    });
    watch();
    window.addEventListener('resize', watch);
    return {
      stop() { window.removeEventListener('resize', watch); if (io) io.disconnect(); if (mo) mo.disconnect(); clearTimeout(copyT); },
    };
  }

  // ---------- модуль ----------

  const MOD = {
    id: 'clicker',
    icon: ICON,
    seatNames: ['гончар'],
    seatClass: ['c'],

    /// Джойстик. Напрямки собі не забираємо (`dirs` нема): стіком тут ходять по кнопках майстерні,
    /// а коло крутить Ⓐ — воно позначене data-pad="press", тож натиск приходить парою pointerdown/up
    /// зі справжньою тривалістю, як від пальця. Розписний глек і глек з полиці літають самі по собі
    /// й кільцем їх не спіймаєш — вони на Ⓧ.
    pad: {
      hint: '{a} крутити коло · {x} ловити глек · {dpad} по майстерні',
      on(btn, ctx) {
        const st = ctx.clk;
        if (btn !== 'x' || !st) return false;
        const ev = { hpad: true, pointerType: 'mouse', button: 0, preventDefault() {} };
        if (st.fall && (!st.fallEl.hidden || (st.flyFall && !st.flyFall.hidden))) { grabFall(st, ev); return true; }
        if (st.golden && (!st.gold.hidden || (st.flyGold && !st.flyGold.hidden))) { catchGolden(st, ev); return true; }
        // Кіт і зірка теж літають самі по собі — кільцем їх не спіймаєш, тож вони на тому самому Ⓧ.
        if (st.starEl && !st.starEl.hidden) { makeWish(st, ev); return true; }
        if (st.catEl && !st.catEl.hidden) { petCat(st, ev); return true; }
        return false;                       // ловити нема чого — хай Ⓧ відкриє балачки, як усюди
      },
    },

    mount(root, ctx) {
      const st = state(root);
      // Картка — на всю ширину сітки столів (див. .clk-wide у css): інакше сцена й полиці лягали б одним стовпчиком.
      const card = root.closest && root.closest('.gtable');
      if (card) card.classList.add('clk-wide');
      root.innerHTML = '<div class="clk">'
        // Мініплашка (v10 §9): коло прокрутили з екрана — згори липне «🏺 число · ⤒ до кола». Сама нуль заввишки,
        // тож поява плашки нічого під нею не зсуває. Число вона не рахує, а бере з лічильника (pinView).
        + '<div class="clk-pin" hidden><button type="button" class="clk-pinbtn" aria-label="Прокрутити до кола">'
        + '<span class="clk-pinico" aria-hidden="true">🏺</span><b class="clk-pinnum"></b><span class="clk-pinunit"></span>'
        + '<span class="clk-pingo">⤒ до кола</span></button></div>'
        + '<div class="clk-lay">'
        // Ліворуч (або зверху на телефоні): вивіска, лічильник, сцена з полицею й колом, бонуси, прилавок.
        + '<div class="clk-scene">'
        + '<div class="clk-sign"></div>'
        + '<div class="clk-head"><b class="clk-count">0</b><span class="muted small clk-unit">глеків</span></div>'
        // Швидкість і суперник: окремі рядки скрізь (обгортка display: contents), а на низькому ПК (.pathside) — один рядок.
        + '<div class="clk-rr"><div class="clk-rate muted small"></div>'
        + '<div class="clk-rival small" hidden></div></div>'
        // Обгортка сцени: на ПК вона забирає всю висоту, що лишилась у стовпчику, а сцена 4:5 вписується в неї
        // (container-type: size у css). У ній лише сцена й Око майстра — решту тримає keepBox.
        + '<div class="clk-stagebox">'
        + '<div class="clk-stage">'
        // Хата, що росте від покупок: шар під полицею й колом (viewBox 360×450 — сцена 4:5; малює clicker-scene.js).
        + '<svg class="clk-house" viewBox="0 0 360 450" preserveAspectRatio="none" aria-hidden="true"></svg>'
        // Шари для частин (api.layer): back — SVG під колом у тих самих координатах, що й хата; front — DOM над сценою.
        + '<svg class="clk-layer-back" viewBox="0 0 360 450" preserveAspectRatio="none" aria-hidden="true"></svg>'
        + '<div class="clk-shelf"><div class="clk-shelf-jugs"></div></div>'
        + '<div class="clk-wheelbox">'
        + '<svg class="clk-heat" viewBox="0 0 100 100" aria-hidden="true"><circle class="bg" cx="50" cy="50" r="47"/>'
        + '<circle class="fg" cx="50" cy="50" r="47"/></svg>'
        + '<button type="button" class="clk-wheel" aria-label="Крутити коло" data-pad="press" data-pad-first>'
        // Крутиться сам круг із борознами й цяткою (без неї обертання ідеального кола не видно),
        // а глек стоїть рівно: гончар його тримає.
        + '<svg viewBox="0 0 100 100" aria-hidden="true">'
        + '<g class="clk-turn"><circle class="clk-disc" cx="50" cy="50" r="46"/>'
        + '<circle class="clk-ring" cx="50" cy="50" r="35"/>'
        + '<circle class="clk-ring" cx="50" cy="50" r="24"/>'
        + '<circle class="clk-speck" cx="50" cy="12" r="2.6"/></g>'
        // Обгортка для «пружини» глини на клік (жива хата): у самого .clk-jugbox transform уже зайнятий розгоном.
        + '<g class="clk-squash"><g class="clk-jugbox"></g></g>'
        + '</svg></button>'
        + '<div class="clk-sparks"></div><div class="clk-pops"></div></div>'
        + '<button type="button" class="clk-gold" hidden aria-label="Розписний глек — лови!" title="Розписний глек — лови!">'
        + '<svg class="clk-gold-ring" viewBox="0 0 40 40" aria-hidden="true"><circle cx="20" cy="20" r="18"/></svg>'
        + jugSvg('golden', 'clk-gold-jug', 'gold') + '</button>'
        + '<button type="button" class="clk-fall" hidden aria-label="Глек падає з полиці — лови!" title="Лови!"><span class="clk-fall-box"></span></button>'
        + '<div class="clk-fx"></div>'
        + '<div class="clk-layer-front"></div>'
        + '</div>'
        // Око майстра стає на місце сцени: відлік паузи або полиця з глечиками.
        + '<div class="clk-eye" hidden><div class="clk-eye-head"><b>👁 Око майстра</b><span class="clk-eye-tries small"></span></div>'
        + '<div class="clk-eye-text small"></div>'
        + '<div class="clk-eye-pic veiled"><img alt="Полиця з глечиками, горщиками, мисками й черепками" draggable="false">'
        + '<div class="clk-eye-marks"></div>'
        // Завіса над полицею: доки не натиснуто кнопку, картинки не видно й торкання в неї не йдуть.
        + '<div class="clk-eye-veil"><span class="small">Полиця відкриється після кнопки — випадкові кліки не рахуються</span>'
        + '<button type="button" class="primary clk-eye-go" disabled>👁 Показати полицю</button></div></div>'
        + '<button type="button" class="ghost small clk-eye-reset" hidden disabled>Скинути торкання</button></div>'
        + '</div>'
        + '<div class="clk-buffs" hidden></div>'
        + '<div class="clk-sell"><button type="button" class="primary clk-one" disabled></button>'
        + '<button type="button" class="ghost clk-all" data-pots="0" disabled></button></div>'
        + '<div class="clk-left muted small"></div>'
        + '</div>'
        // Праворуч (або нижче): вкладки з верстатами, розписами й обпалом.
        + '<div class="clk-side">'
        // П'ять вкладок (v8): Ремесло · Майстерня · Альбом · Село · Клейма. «Ремесло», «Альбом» і «Село»
        // додають частини, тож тут — лише свої дві; ярлики зайвого гравець не бачить, поки не доросте (gateTabs).
        + '<div class="clk-tabs" role="tablist">'
        + '<button type="button" class="ghost" data-tab="shop" data-order="20">🔨 Майстерня</button>'
        + '<button type="button" class="ghost" data-tab="fire" data-order="50">🔥 Клейма</button></div>'
        + '<div class="clk-pane" data-pane="shop">'
        + '<div class="clk-modes"><span class="muted small">купувати</span>'
        + '<button type="button" class="ghost" data-mode="1">×1</button>'
        + '<button type="button" class="ghost" data-mode="10">×10</button>'
        + '<button type="button" class="ghost" data-mode="max">макс</button></div>'
        + '<div class="clk-markbox"></div><div class="clk-shop"></div>'
        // Колишні вкладки «Хата», «Розписи» й «Купці» — згорнуті розділи Майстерні: усе, що купують за глеки, в одному місці.
        + SECTIONS.map((x) => '<details class="clk-sec" data-sec="' + x.key + '"><summary>' + x.title + '</summary></details>').join('')
        + '</div>'
        + '<div class="clk-pane" data-pane="house" hidden></div>'
        + '<div class="clk-pane" data-pane="orders" hidden></div>'
        + '<div class="clk-pane" data-pane="styles" hidden></div>'
        + '<div class="clk-pane" data-pane="fire" hidden>'
        + '<div class="clk-firebox"><div class="clk-bar"><i></i></div><div class="clk-nextstamp muted small"></div>'
        + '<button type="button" class="primary clk-fire" disabled></button><div class="clk-after small"></div></div>'
        + '<div class="clk-firestatic"></div></div>'
        // Підвал правої колонки: на ПК, коли стіл стоїть в один екран, сюди переходить прилавок черепків (fitTable).
        + '<div class="clk-shards"></div>'
        + '</div>'
        + '</div>'
        // Модальна панель частин (мінігри, дарунки, хата друга): одна за раз, поверх усієї картки.
        + '<div class="clk-overlay" hidden><div class="clk-ov-box" role="dialog"><button type="button" class="ghost clk-ov-x" aria-label="Закрити">✕</button>'
        + '<div class="clk-ov-body"></div></div></div>'
        + '</div>';
      const q = (s) => root.querySelector(s);
      st.el = q('.clk');
      st.layEl = q('.clk-lay');
      st.sceneEl = q('.clk-scene');
      st.sideEl = q('.clk-side');
      st.stageBox = q('.clk-stagebox');
      st.sellRow = q('.clk-sell');
      st.shards = q('.clk-shards');
      st.count = q('.clk-count');
      st.unit = q('.clk-unit');
      st.unitKey = 'pots';
      st.rate = q('.clk-rate');
      st.rival = q('.clk-rival');
      st.sign = q('.clk-sign');
      st.stage = q('.clk-stage');
      st.wheelBox = q('.clk-wheelbox');
      st.wheel = q('.clk-wheel');
      st.turn = q('.clk-turn');
      st.jugBox = q('.clk-jugbox');
      st.jugBox._wear = null;
      st.heatRing = q('.clk-heat .fg');
      st.pops = q('.clk-pops');
      st.sparks = q('.clk-sparks');
      st.fx = q('.clk-fx');
      st.buffs = q('.clk-buffs');
      st.gold = q('.clk-gold');
      st.fallEl = q('.clk-fall');
      st.fallJug = q('.clk-fall-box');
      st.shelfJugs = q('.clk-shelf-jugs');
      st.one = q('.clk-one');
      st.all = q('.clk-all');
      st.left = q('.clk-left');
      st.tabs = q('.clk-tabs');
      st.modes = q('.clk-modes');
      st.marks = q('.clk-markbox');
      st.shop = q('.clk-shop');
      st.panes = { shop: q('[data-pane="shop"]'), fire: q('[data-pane="fire"]') };
      st.styles = q('[data-pane="styles"]');
      st.housePane = q('[data-pane="house"]');
      st.ordersPane = q('[data-pane="orders"]');
      st.tabNotes = {};
      st.gates = {};
      st.gateReady = false;
      // Хата, розписи й вклад купцям — усередину Майстерні. Елементи ті самі (їх малює той самий код),
      // але вони більше не вкладки, тож setTab їх не ховає.
      for (const x of SECTIONS) {
        const sec = q('.clk-sec[data-sec="' + x.key + '"]');
        const pane = q('[data-pane="' + x.key + '"]');
        pane.hidden = false;
        pane.classList.remove('clk-pane');
        pane.classList.add('clk-secbody');
        sec.appendChild(pane);
        sec.open = storeGet(SEC_KEY + x.key, '') === '1';
        // Подія toggle приходить після того, як розділ розгорнули чи згорнули: свою (paintSections) пізнаємо за _want.
        sec.addEventListener('toggle', () => {
          if (sec._want !== undefined && sec.open === sec._want) { sec._want = undefined; return; }
          sec._want = undefined;
          storeSet(SEC_KEY + x.key, sec.open ? '1' : '0');
        });
      }
      st.house = q('.clk-house');
      st.fire = q('.clk-firebox');
      st.fire._bar = q('.clk-bar i');
      st.fire._next = q('.clk-nextstamp');
      st.fire._btn = q('.clk-fire');
      st.fire._after = q('.clk-after');
      st.fire._static = q('.clk-firestatic');
      st.eye = { el: q('.clk-eye'), text: q('.clk-eye-text'), tries: q('.clk-eye-tries'), pic: q('.clk-eye-pic'),
        img: q('.clk-eye-pic img'), marks: q('.clk-eye-marks'), veil: q('.clk-eye-veil'), go: q('.clk-eye-go'), reset: q('.clk-eye-reset') };
      st.eyeKey = '';
      st.eyeOpen = false;
      st.ringOff = -1;
      st.glow = -1;
      st.jugScale = -1;
      st.angleAt = 0;
      st.ctx = ctx;
      ctx.clk = st;                     // щоб onKey дістався до стану: там є лише ctx
      // Клік — це пара справжніх pointerdown/pointerup на колі, а не подія click: її дає і el.click() зі скрипта,
      // і клавіатура, і в ній нема ні миті натискання, ні тривалості.
      st.wheel.addEventListener('pointerdown', (e) => pressWheel(st, e));
      st.wheel.addEventListener('pointerup', (e) => releaseWheel(st, e));
      st.wheel.addEventListener('pointercancel', (e) => releaseWheel(st, e));
      st.wheel.addEventListener('contextmenu', (e) => e.preventDefault());   // довгий тап на телефоні — не меню
      st.gold.addEventListener('click', (e) => catchGolden(st, e));
      // Глек, що падає, ловимо на pointerdown: за час між натиском і відпусканням він устигає посунутись, і click
      // на рухомій кнопці міг би не спрацювати.
      st.fallEl.addEventListener('pointerdown', (e) => grabFall(st, e));
      st.fallEl.addEventListener('contextmenu', (e) => e.preventDefault());
      st.eye.go.addEventListener('click', (e) => openShelf(st, e));
      st.eye.img.addEventListener('pointerdown', (e) => tapShelf(st, e));
      st.eye.img.addEventListener('contextmenu', (e) => e.preventDefault());
      st.eye.reset.onclick = () => { st.taps = []; paintEye(st); };
      // Пробіл: натиснули (onKey) → відпустили (тут). Слухаємо весь документ: фокус між ними міг утекти.
      if (st.onKeyUp) document.removeEventListener('keyup', st.onKeyUp);
      st.onKeyUp = (e) => {
        if (e.code !== 'Space' || !st.keyDown) return;
        const down = st.keyDown;
        st.keyDown = 0;
        if (!human(e) || e.timeStamp - down > HOLD_MS) return;
        spin(st, down, e.timeStamp - down, -1, -1, SRC.key);
      };
      document.addEventListener('keyup', st.onKeyUp);
      st.one.onclick = () => order(st, 'sell', { pots: st.rateOf });
      st.all.onclick = () => order(st, 'sell', { pots: +st.all.dataset.pots });
      st.fire._btn.onclick = () => fire(st);
      for (const b of st.tabs.querySelectorAll('[data-tab]')) b.onclick = () => { H.api.sfx('tap'); setTab(st, b.dataset.tab); };
      for (const b of st.modes.querySelectorAll('[data-mode]')) b.onclick = () => setMode(st, b.dataset.mode);
      // Вкладка частини (горно, альбом…) з'явиться, коли частина завантажиться: доти — майстерня, а пам'ять не чіпаємо.
      // Клейма (престиж) показуємо тоді, коли до них лишилось кілька кроків, а не з нульового рахунку.
      H.api.showWhen(st, 'fire', (st2) => st2.stamps > 0 || (st2.total || 0) + Math.max(0, st2.shown - st2.base) >= 1e8);
      setTab(st, st.panes[st.tab] ? st.tab : 'shop', false);
      setMode(st, ['1', '10', 'max'].includes(st.mode) ? st.mode : '1');
      st.timer = setInterval(() => flush(st), BATCH_MS);
      st.front = q('.clk-layer-front');
      st.back = q('.clk-layer-back');
      st.ov = { el: q('.clk-overlay'), body: q('.clk-ov-body'), x: q('.clk-ov-x'), onClose: null };
      st.ov.x.onclick = () => H.api.closeOverlay(st);
      // Закриваємо на click, а не на pointerdown: інакше на телефоні вікно зникало від дотику, а click того ж тапу
      // натискав кнопку, що була під затемненням (продати виріб, відкрити іншу клітинку).
      // Але click зринає на спільному предку натиснення й відпускання: штрих мінігри, що почався на полотні й
      // з'їхав за край коробки, давав click саме на затемненні — і розпис обривався посеред роботи. Тож закриваємо
      // лише тоді, коли палець і ліг на затемнення.
      st.ov.el.addEventListener('pointerdown', (e) => { st.ovDownBack = e.target === st.ov.el; });
      st.ov.el.addEventListener('click', (e) => { if (e.target === st.ov.el && st.ovDownBack !== false) H.api.closeOverlay(st); });
      st.root = root;
      st.boxWatch = keepBox(st);
      st.steady = steadyView(st);
      mountFly(st);
      mountPinLovy(st);
      st.stageWatch = watchStage(st);
      st.pinBar = pinView(st);
      st.pinTabs = pinTabs(st);
      watchCard(st);
      H.mounted.add(st);
      for (const p of H.parts) mountPart(st, p);
      if (!st.raf) loop(st);
    },

    update(root, ctx) {
      const st = state(root);
      if (!st.el) return;
      placeInView(st);
      st.ctx = ctx;
      ctx.clk = st;
      const v = ctx.view;
      // Той самий вид удруге — не новина (записка Smaug №2: «обпал залагує, і час або скидається на початок, або
      // зависає на місці»). Каркас кличе update не лише на новий вид, а й на КОЖНУ зміну лобі (подія 'rooms' →
      // refreshAll: хтось на сайті поставив стіл чи встав із-за нього) — з тим самим видом, що вже був. Раніше ми брали
      // з нього «правду сервера» вдруге: серверне «зараз» відкочувалось до миті, коли вид складено, — у ручному обпалі
      // це мить розпалу, тож відлік горна скакав назад на 0:30 і стояв, поки в лобі метушились, жар не рухався, а горно
      // не відкривалось; лічильник глеків, розгін і робота підмайстрів теж відкочувались. Свіжий вид — лише новий
      // об'єкт від сервера. Перемалювати картку зі старим (refreshCard, частина догнала) — st.again, свій nick — mine.
      const fresh = !!v && v !== st.lastView;
      const mine = !!ctx.mine;
      if (v && !fresh && !st.again && mine === st.mine) return;
      st.again = false;
      st.mine = mine;
      // Худий вид: без каталогу магазину (перше відкриття після перезапуску сервера) назв ще нема — просимо каталог і
      // цей вид малюємо без магазину й частин; наступний прийде вже з назвами.
      const ready = !v || v.pots == null || hydrate(st, v);
      if (v && v.pots != null && fresh) {
        // Сервер — джерело правди: беремо його число і його мітку часу, від них доліковуємо далі.
        // Усе, що вже полетіло, у цьому числі вже враховано — свій запас відпущених кліків обнуляємо.
        st.inflight = 0;
        st.inflightGain = 0;
        st.base = v.pots;
        st.total = v.total || 0;
        // Серверне «зараз» — від найменш запізнілого з недавніх видів. Вид каже «на сервері було now», а до нас доїхав
        // із затримкою: мережа, а на повільному ПК ще й зайнятий головний потік (вид обробляється на сотні мс пізніше).
        // Тож now − Date.now() — нижня межа справжнього зсуву годинників, і найбільша з них — найточніша; з кожним
        // запізнілим видом відлік горна смикався назад (заміряно: −200…−345 мс на процесорі ×6). Беремо найкращу за
        // останні 60 с — щоб переведений годинник ПК не тягнувся за нами довше.
        const at = Date.now();
        const now = Date.parse(v.now);
        const offs = (st.clockOffs || []).filter((x) => at - x[1] < CLOCK_KEEP_MS).slice(-11);
        offs.push([(Number.isFinite(now) ? now : at) - at, at]);
        st.clockOffs = offs;
        st.viewNow = at + Math.max(...offs.map((x) => x[0]));
        const sync = Date.parse(v.lastSync);
        st.lastSync = Number.isFinite(sync) ? sync : st.viewNow;
        st.recvAt = at;
        st.offlineMs = (v.offlineHours || 8) * 3600 * 1000;
        st.clickBase = v.clickBase || v.perClick || 1;
        st.baseSecond = v.baseSecond != null ? v.baseSecond : v.perSecond || 0;
        st.fairUntil = (v.fair && Date.parse(v.fair.until)) || 0;
        st.fairMult = (v.fair && v.fair.mult) || 7;
        st.inspireUntil = (v.inspire && Date.parse(v.inspire.until)) || 0;
        st.inspireMult = (v.inspire && v.inspire.mult) || 25;
        st.inspireShare = (v.inspire && v.inspire.share) || 0;
        // Скільки триває весь баф (з «Довгим ярмарком» — удвічі): від цього смужка під плашкою знає, з якої частки танути.
        st.fairSpan = ((v.fair && v.fair.span) || 0) * 1000;
        st.inspireSpan = ((v.inspire && v.inspire.span) || 0) * 1000;
        // Скільки ярмарку й натхнення чекає, поки висить полиця Ока майстра (05.10): під полицею вони стоять.
        st.fairHeld = ((v.fair && v.fair.held) || 0) * 1000;
        st.inspireHeld = ((v.inspire && v.inspire.held) || 0) * 1000;
        st.rateOf = v.rate || 100;
        st.canSell = v.canSellToday || 0;
        st.ups = v.upgrades || {};
        st.markList = v.marks || [];
        st.marksOwned = v.marksOwned || 0;
        st.marksAll = v.marksAll || 0;
        st.styleList = v.styles || [];
        st.secretList = v.secrets || [];
        // Скарбниця роду (v11): null — ще не відкрилась; тексти — у каталозі магазину.
        st.relicList = v.relics || null;
        st.wear = v.wear || '';
        st.stamps = v.stamps || 0;
        st.stampsFree = v.stampsFree || 0;
        st.stampBonus = v.stampBonus || 0.02;
        st.stampsExtra = v.stampsExtra || 0;
        st.stampSoft = v.stampSoft || 1000;
        st.stampKnee = v.stampKnee || 0;
        st.stampIron = v.stampIron || 0;
        st.science = v.science || null;
        // Розгін — серверний, плюс наші кліки, що ще не полетіли (сервер про них не знає).
        st.heatFull = v.heatFull || 18;
        st.heatTau = v.heatTau || 3;
        st.momentumMax = v.momentumMax || 1;
        if (v.heat != null) { st.heat = Math.max(0, v.heat) + st.hands.length; st.heatAt = Date.now(); }
        if (v.golden) {
          const at = Date.parse(v.golden.at);
          const until = Date.parse(v.golden.until);
          if (Number.isFinite(at) && Number.isFinite(until)) st.golden = { at, until, x: v.golden.x || 0, y: v.golden.y || 0 };
        }
        if (v.fall) {
          const at = Date.parse(v.fall.at);
          const until = Date.parse(v.fall.until);
          if (Number.isFinite(at) && Number.isFinite(until)) st.fall = { at, until, x: v.fall.x || 40 };
          st.fallGain = v.fall.gain || 0;
          st.fallStreak = v.fall.streak || 0;
          st.fallMiss = v.fall.miss || 'streak';
          // Коли гончар востаннє справді був біля кола (сервер, Clicker._seenAt): від неї «трісь» знає, чи серія ціла.
          st.fallSeen = Date.parse(v.fall.seen) || 0;
          st.streakBonus = v.fall.bonus || 0;
        }
        // Дев'яте оновлення: кіт, зірка, вітер, щасливі кліки, бажання й «що нового».
        const evs = v.events;
        if (evs) {
          const row = (r) => (r ? { at: Date.parse(r.at) || 0, until: Date.parse(r.until) || 0 } : null);
          const cat = row(evs.cat);
          const star = row(evs.star);
          const wind = row(evs.wind);
          st.events = {
            cat: cat && { ...cat, dir: evs.cat.dir || 0 },
            star: star && { ...star, x: evs.star.x || 40, y: evs.star.y || 16 },
            wind: wind && { ...wind, mult: evs.wind.mult || 3 },
          };
          st.windAt = wind ? wind.at : 0;
          st.windUntil = wind ? wind.until : 0;
          st.windMult = (evs.wind && evs.wind.mult) || 3;
        }
        st.starWish = !!v.starWish;
        const lucky = v.lucky || 0;
        // «✨ ×50» малюємо за приростом серверного лічильника: кидок робить сервер, клієнт його не вгадує.
        if (st.luckySeen >= 0 && lucky > st.luckySeen && visible(st)) {
          for (let i = 0; i < Math.min(3, lucky - st.luckySeen); i++) {
            popAt(st, '✨ ×50', 'big lucky', 24 + Math.random() * 52, 30 + Math.random() * 10);
          }
          sparks(st, st.fx, 10, true, 50, 44);
          H.api.sfx('rare');
        }
        st.luckySeen = lucky;
        // Каталоги (тексти виробів, хати, подій…) сервер шле лише до першої дії — кешуємо. Спершу каталог, потім
        // хата: її назви й ціни (десяте оновлення, §10) потрібні вже цьому виду.
        if (v.catalog) st.catalog = v.catalog;
        // Хата: глина, знаряддя, прикраси й купці. Старий сервер (хвилина деплою) house не шле — тоді все порожнє.
        const hs = v.house || {};
        st.clay = hs.clay || '';
        st.clayBody = hs.clayBody || '';
        st.clayRestUntil = Date.parse(hs.clayRestUntil) || 0;
        houseFrom(st, hs);
        const od = hs.orders || {};
        st.orders = od.board || [];
        st.taken = (od.taken || []).map((t) => ({ id: t.id, merchant: t.merchant, pay: t.pay, payAt: Date.parse(t.payAt) || 0 }));
        st.refreshAt = Date.parse(od.refreshAt) || 0;
        st.maxTaken = od.maxTaken || 3;
        paidLately(st, ctx, od.paid || []);
        const g = v.guard;
        // Полиця зникла, а перед тим обіцяла платню — майстер кивнув і заплатив (v9 §A.1): золотий дощ над колом.
        const was = st.eyeWas;
        if (!g && was && was.pays && was.gain > 0 && visible(st)) eyeRain(st, was.gain);
        st.eyeWas = g && g.pays ? { pays: true, gain: g.gain || 0 } : null;
        st.guard = g ? {
          serial: g.serial || 0, count: g.count || 0, png: g.png || '', width: g.width || 400, height: g.height || 250,
          misses: g.misses || 0, maxMisses: g.maxMisses || 3, lockUntil: (g.lockUntil && Date.parse(g.lockUntil)) || 0, why: g.why || '',
          pays: !!g.pays, gain: g.gain || 0,
        } : null;
        // Майстер спитав — усе, що ще не полетіло, однаково не зарахується: не малюємо цих глеків на лічильнику.
        if (st.guard) { st.hands.length = 0; st.handsGain = 0; }
        // Під вікном частини Око майстра було б невидиме, а кліки — не зараховані: майстер важливіший за вікно.
        // Виняток — мінігра розпису, де вже водять пальцем: вона однаково скінчиться за кілька секунд, а обірвати
        // її посеред штриха означало б згаяти всю роботу. Майстер зачекає — кола ми в ці секунди й не крутимо.
        if (st.guard && H.api.overlayOpen(st) && !H.api.overlayBusy(st)) H.api.closeOverlay(st);
        // Каталоги (тексти виробів, подій…) сервер шле лише до першої дії — кешуємо (st.catalog уже взято вище, до хати);
        // нема в кеші — просимо раз.
        if ((!st.catalog || !st.shopCat) && !st.catalogAsked && ctx.mine && ctx.act) {
          // pv — ми клієнт v10, що вміє доповнювати худий вид. Каталог міг загубитись (ліміт дій, клік з іншого
          // пристрою між look і видом) — тоді за три секунди питаємо знову.
          st.catalogAsked = true;
          ctx.act('look', { catalog: true, pv: PV });
          clearTimeout(st.catalogT);
          st.catalogT = setTimeout(() => { if (!st.catalog || !st.shopCat) st.catalogAsked = false; }, 3000);
        } else if (st.catalog && st.shopCat) {
          // Каталог є, та в ньому бракує хати чи прокачки ремесла (каталог від сервера до v10) або назви й байки щойно
          // знайденої дивовижі (каталог шле лише знайдені) — просимо ще (пакет B, §10).
          askCatalog(st, ctx, catalogGap(st, v));
        }
        st.lastView = v;
        // «Що нового» — раз на гончаря; сервер шле поле, поки не бачив. Чекаємо, поки картка стане видною:
        // під час Ока майстра чи чужого вікна лізти поперед батька нема куди.
        st.news = v.news || '';
        st.newsSeen = v.newsSeen || '';
        st.coin = v.coin || 0;
        st.coinSeen = v.coinSeen || 0;
        if (st.coin > st.coinSeen && st.news !== NEWS_VERSION && ctx.mine && !st.coinAsked) {
          st.coinAsked = true;
          coinLater(st);
        }
        if (!(st.coin > st.coinSeen)) st.coinAsked = false;
        if (st.news === NEWS_VERSION && !st.newsAsked && ctx.mine) {
          st.newsAsked = true;
          newsLater(st);
        }
      }
      const one = 'Обміняти ' + num(st.rateOf) + ' → 🏺1';
      if (st.one.textContent !== one) st.one.textContent = one;
      const left = st.canSell > 0
        ? 'сьогодні ще ' + num(st.canSell) + ' ' + shards(st.canSell) + ', по ' + num(st.rateOf) + ' глеків за черепок'
        : 'на сьогодні черепки скінчились, приходь завтра';
      if (st.left.textContent !== left) st.left.textContent = left;

      const owned = st.styleList.filter((s) => s.owned).length;
      st.tabText.shop = '🔨 Майстерня';
      st.tabText.fire = '🔥 Клейма';
      // Вільні клейма — голим числом (без «🔖»: вкладка й так «Клейма») і коротко: «🔖37,2 млн» на телефоні різався
      // до «🔖37,…». Нуль не показуємо — бейдж «0» нічого не каже.
      H.api.tabNote(st, 'fire', 'stamps', st.stamps && st.stampsFree > 0 ? few(st.stampsFree) : '', 1);
      labelTab(st, 'shop');
      labelTab(st, 'fire');
      paintSections(st, owned);
      wheelJug(st);
      paintSign(st);
      if (ready) {
        shop(st, ctx);
        housePane(st, ctx);
        ordersPane(st, ctx);
        styles(st, ctx);
        firePane(st, ctx);
      }
      if (ready && v && v.pots != null) for (const p of H.parts) if (st.parts.has(p.id)) callPart(p, 'update', st, v, H.api);
      gateTabs(st, v);
      // Новий вид — і кнопки, і повільні рядки одразу: полиці могли щойно перемалюватись із вимкненими кнопками.
      st.slowAt = 0;
      paint(st);
    },

    onKey(e, ctx) {
      if (e.code !== 'Space' || !ctx.mine || !ctx.clk) return false;
      const st = ctx.clk;
      // Фокус на іншій кнопці картки — пробіл належить їй: на верстаті чи прилавку ми б крутили коло замість
      // покупки й продажу. Сама кнопка кола — наша: її власний click ми кліком не рахуємо.
      const on = document.activeElement;
      if (on && on !== st.wheel && st.el && st.el.contains(on) && on.closest('button,summary,a,input,select,textarea,[tabindex]')) return false;
      if (H.api.overlayOpen(st)) return false;
      // Затиснутий пробіл сипле keydown з repeat — це не клацання, а автоповтор клавіатури.
      if (!human(e) || e.repeat || guardOn(st)) return true;
      if (!st.keyDown) st.keyDown = e.timeStamp;
      return true;
    },

    status(ctx) {
      const v = ctx.view || {};
      if (v.total == null) return '';
      // Нулі не пишемо (F14): у новачка рядок був із самих «0» і на телефоні займав два рядки над числом.
      return ['усього наліплено ' + short(v.total)]
        .concat(v.caught ? ['розписних спіймано ' + num(v.caught)] : [])
        .concat(v.grabbed ? ['з полиці ' + num(v.grabbed)] : [])
        .concat(v.soldToday ? ['обміняно сьогодні ' + num(v.soldToday)] : [])
        .join(' · ');
    },

    unmount(root) {
      const st = root._clk;
      if (!st) return;
      clearInterval(st.timer);
      clearTimeout(st.eyeArm);
      clearTimeout(st.newsT);
      clearTimeout(st.coinT);
      cancelAnimationFrame(st.raf);
      if (st.onKeyUp) document.removeEventListener('keyup', st.onKeyUp);
      if (st.steady) st.steady.stop();
      if (st.fitWatch) st.fitWatch.stop();
      // Рядок статусу — каркаса: вертаємо його на місце до того, як каркас перемалює тіло картки; і стелю сторінки теж.
      const home = cardOf(st);
      if (home) placeStatus(st, home, false);
      setZoom(st, 1);
      if (st.pinBar) st.pinBar.stop();
      if (st.pinTabs) st.pinTabs.stop();
      if (st.stageWatch) st.stageWatch.stop();
      for (const b of [st.flyFall, st.flyGold]) if (b) b.remove();
      st.flyFall = st.flyGold = st.flyFallJug = null;
      if (st.boxWatch) st.boxWatch.disconnect();
      if (st.io) { st.io.disconnect(); st.io = null; }
      for (const p of H.parts) if (st.parts && st.parts.has(p.id)) callPart(p, 'unmount', st, H.api);
      H.mounted.delete(st);
      if (st.ov) H.api.closeOverlay(st);
      const card = root.closest && root.closest('.gtable');
      if (card) card.classList.remove('clk-wide', 'clk-fit', 'clk-big');
      st.raf = 0;
      st.el = null;
      root._clk = null;
    },
  };
  HGames.register(MOD);
})();
