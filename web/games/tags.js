/*
  Теги й пошук каталогу ігор (лобі в core.js). Сервер про теги не знає — це лише показ і пошук.

  GAMES: id гри → 'теги | синоніми'. Гра з родини (FAMILIES у core.js: хрестики, юрма, шахи…) — за id родини, теги
  родини йдуть усім її режимам; щоденна чи тренувальна окрема гра (chess-daily) — своїм id.
  - теги — з TAGS, через пробіл, у порядку важливості: на плитці видно перші три, натиск фільтрує каталог;
  - синоніми — лише для пошуку, не видно: англійські й розмовні назви, на що схоже («тетріс» → Цеглини).
  Автоматичні теги (🤖 з ботом, 🏆 рейтинг, ☀ щоденна) core.js рахує з каталогу — сюди їх не писати.
  Нова гра без рядка тут не зникає: шукається за назвою й підказкою, просто без тегів.

  Пошук (search): кілька слів — мають збігтись усі; число до 12 чи «удвох / сам» — скільки нас; апострофи й ґ/г
  однакові. Не знайшлось — пробуємо іншу розкладку (if[b → шахи), далі — з однією одруківкою (мафя → мафія).
*/
(() => {
  'use strict';

  // Ключ — як пишеться в GAMES (пробіл у назві — підкреслення), icon і назва — для чипа.
  const TAGS = {
    'карти': '🃏', 'слова': '🔤', 'малювання': '✏', 'вікторина': '🧠', 'музика': '🎵', 'географія': '🗺',
    'блеф': '🕵', 'стратегія': '♟', 'логіка': '🧩', 'реакція': '⚡', 'стрілялка': '🎯', 'перегони': '🏎',
    'спорт': '🏒', 'разом': '🤝', 'команди': '👥', 'хто_вистоїть': '🧊', 'хованки': '🎭', 'сміх': '😂',
    'казино': '🎰', 'удача': '🍀', 'класика': '🏛', 'уважність': '👀', 'хапай': '🧺', 'аркада': '🕹',
    'числа': '🔢', 'друк': '⌨', 'міні_ігри': '🎲', 'клікер': '🏺',
  };
  // Автоматичні — з каталогу (core.js), але виглядають і фільтрують так само.
  const AUTO = {
    'з_ботом': { icon: '🤖', hint: 'Можна з ботами — Глек підсяде на порожні місця' },
    'рейтинг': { icon: '🏆', hint: 'На рейтинг: кожна партія рухає Ело' },
    'щоденна': { icon: '☀', hint: 'Одна на всіх на добу — таблиця дня' },
  };

  const SLOT = 'казино удача | slots слоти слот автомат автомати однорукий бандит барабани оберти casino';
  const GAMES = {
    // ♟ Настільні
    battleship: 'стратегія класика логіка | battleship sea battle морський бій кораблики кораблі флот море',
    c4: 'логіка класика стратегія | connect four connect 4 чотири в ряд фішки колонки',
    checkers: 'стратегія класика | checkers draughts шашки піддавки китайські шашки chinese checkers halma зірка дамка',
    chess: 'стратегія класика логіка | chess шахи фішер chess960 піддавки мат король ферзь глек бот',
    domino: 'класика удача стратегія | domino dominoes доміно козел кістки',
    durak: 'карти класика удача | durak дурень підкидний переводний козир колода',
    mines: 'логіка удача | minesweeper сапер міни дуель',
    poker: 'карти казино блеф | poker покер texas holdem холдем фішки банк турнір',
    reversi: 'стратегія логіка класика | reversi othello отелло реверсі фішки глек бот',
    scrabble: 'слова стратегія класика | scrabble скрабл ерудит літери дошка',
    ttt: 'логіка класика | tic tac toe tictactoe xo хрестики нолики ультимативні зникаючі',
    // ⚡ Жваві
    bakhne: 'уважність реакція хто_вистоїть | simon says memory память стрілки такт танці',
    bomber: 'аркада хто_вистоїть | bomberman бомбермен бомби вибухи ящики',
    bricks: 'аркада класика хто_вистоїть | tetris тетріс tetris 99 фігурки ряди цеглинки',
    brid: 'уважність хто_вистоїть удача | squid game гра в кальмара камені річка міст скляний міст',
    crowd: 'хованки блеф хто_вистоїть | hidden in plain sight натовп юрма вистеж ярмарок ковзанка вечорниці корчма горщик купала замри селяни',
    curve: 'аркада реакція хто_вистоїть | achtung die kurve curve fever кривуля лінії слід',
    dino: 'реакція аркада хто_вистоїть | chrome dino t rex динозавр раннер runner flappy bird лелеки стрибки',
    duel: 'стрілялка реакція | western вестерн ковбої шериф перестрілка quick draw постріл',
    glekomet: 'стрілялка стратегія аркада | worms вормс gunbound scorched earth pocket tanks артилерія гармата вітер',
    grushi: 'хапай аркада | груші крадії збирай комора',
    hockey: 'спорт реакція аркада | air hockey аерохокей хокей шайба біта',
    hostyntsi: 'хапай реакція | catch кошик лови гостинці',
    icefloe: 'хто_вистоїть аркада спорт | sumo сумо king of the hill штовхай крига сніжки',
    pong: 'спорт класика аркада | pong пінг понг ping pong теніс ракетка',
    rally: 'перегони аркада | racing гонки ралі машинки трактор запорожець micro machines траса',
    sklei: 'логіка реакція | puzzle jigsaw пазл пазли картинки черепки',
    skyrta: 'реакція аркада | stack stacker tower вежа сніп скирта',
    snake: 'аркада класика | snake змійка slither nokia яблука',
    tanks: 'стрілялка аркада команди | battle city танки танчики dendy денді tank 1990 фраги',
    territory: 'аркада стратегія | paper io paperio splix xonix територія земля',
    thinice: 'хто_вистоїть аркада стратегія | spleef тонкий лід ставок',
    tron: 'аркада класика реакція | tron трон light cycles мотоцикли стіна',
    typerace: 'друк перегони | typeracer typing клавогонки клавоперегони клавіатура швидкість друку набір',
    tyr: 'стрілялка реакція | shooting gallery duck hunt тир мішені качки',
    vohnyk: 'разом логіка аркада | fireboy and watergirl fireboy watergirl вогонь і вода платформер кооп',
    // 🎉 Компанія
    bluff: 'блеф сміх слова | balderdash fibbage байки брехня правда',
    dice: 'блеф удача | liars dice perudo перудо кості кубики брешеш',
    dotepy: 'сміх слова | quiplash jackbox жарти дотепи смішне',
    geese: 'уважність числа | count рахувати гуси худоба тварини',
    geo: 'географія вікторина | geoguessr геогесер мапа фото україна',
    hangman: 'слова класика | hangman шибениця віселиця літери вгадай слово',
    mafia: 'блеф команди класика | mafia werewolf мафія вбивця шериф лікар ведучий',
    melody: 'музика вікторина | guess the song пісні пісня мелодія songs радіо',
    memy: 'вікторина сміх | memes meme guess the meme мем меми мемологія вгадай мем картинки пікселі приколи',
    pictionary: 'малювання слова сміх | pictionary skribbl gartic крокодил малювати',
    pozyvni: 'слова команди логіка | codenames кодові імена капітан шпигуни',
    skilky: 'числа вікторина | wits and wagers скільки якого року наближено',
    spy: 'блеф слова | spyfall шпигун локація',
    svoya: 'вікторина реакція | jeopardy своя гра брейн ринг питання пакети',
    telephone: 'малювання сміх слова | gartic phone broken telephone telestrations телефон зіпсований',
    vechirka: 'міні_ігри удача сміх | mario party pummel party вечірка настолка шеляги',
    'wordle-race': 'слова логіка | wordle вордл слово наввипередки',
    // 🏺 Соло
    clicker: 'клікер | clicker cookie clicker idle ідлер клікер гончар глина глеки ферма',
    'bricks-sprint': 'аркада класика | tetris тетріс 40 lines sprint спринт',
    'typerace-solo': 'друк | typing typeracer клавоперегони тренування',
    'geo-solo': 'географія вікторина | geoguessr геогесер мапа тренування',
    'memy-solo': 'вікторина сміх | memes meme мем меми мемологія вгадай мем картинки тренування',
    wordle: 'слова логіка | wordle вордл пять літер слово дня',
    'mines-daily': 'логіка класика | minesweeper сапер міни',
    'chess-daily': 'стратегія логіка | chess puzzle шахи задача мат',
    'bricks-daily': 'аркада класика | tetris тетріс 40 lines',
    'dino-daily': 'реакція аркада | chrome dino динозавр раннер забіг',
    'tyr-daily': 'стрілялка реакція | shooting тир мішені',
    'geese-daily': 'уважність числа | гуси рахувати',
    'geo-daily': 'географія вікторина | geoguessr мапа фото',
    'skilky-daily': 'числа вікторина | скільки питання',
    // 🎰 Азарт
    blackjack: 'карти казино удача | blackjack блекджек 21 двадцять одно очко крупє',
    'blackjack-solo': 'карти казино удача | blackjack блекджек 21 двадцять одно очко',
    roulette: 'казино удача класика | roulette рулетка зеро кулька',
    'roulette-solo': 'казино удача класика | roulette рулетка зеро кулька',
    kolo: 'казино удача | wheel of fortune колесо фортуни колесо',
    lelka: 'казино удача реакція | crash aviator авіатор краш множник лелека',
    kavuny: 'казино удача реакція | fruit ninja кавуни гарбузи серп crash множник',
    'slot-glek': SLOT, 'slot-cascade': SLOT, 'slot-hold': SLOT, 'slot-cluster': SLOT, 'slot-slidy': SLOT,
    // Плитка-панель «Ставки на події» (bets.js): ключ — 'x:' + id панелі
    'x:bets': 'удача | bets ставки прогнози події букмекер polymarket',
  };

  // ---------- нормалізація ----------

  const label = (k) => k.replace(/_/g, ' ');
  /// Нижній регістр, без апострофів (м'яч = мяч = м’яч), ґ як г, дефіси й розділові — пробіли, емодзі геть.
  function norm(s) {
    return String(s == null ? '' : s).toLowerCase()
      .replace(/['’ʼ`´‘]/g, '').replace(/ґ/g, 'г').replace(/ё/g, 'е')
      .replace(/[^0-9a-zа-яєії]+/g, ' ').trim();
  }

  // Набрав не в тій розкладці: if[b → шахи, і навпаки (еуекші → tetris).
  const EN = "qwertyuiop[]asdfghjkl;'zxcvbnm,.`";
  const UK = "йцукенгшщзхїфівапролджєячсмитьбю'";
  const swap = (s, from, to) => Array.from(String(s).toLowerCase(), (ch) => { const i = from.indexOf(ch); return i < 0 ? ch : to[i]; }).join('');

  // ---------- що питають ----------

  const STOP = new Set(['на', 'в', 'у', 'з', 'із', 'зі', 'для', 'і', 'й', 'та', 'гра', 'ігри', 'гру', 'ігор', 'людей', 'люди', 'людини',
    'гравців', 'гравці', 'гравця', 'осіб', 'особи', 'чоловік', 'нас', 'хочу', 'грати', 'пограти', 'зіграти', 'щось', 'можна', 'де', 'що']);
  const COUNT = {
    'сам': 1, 'сама': 1, 'самому': 1, 'самій': 1, 'одному': 1, 'одній': 1, 'соло': 1, 'solo': 1,
    'удвох': 2, 'вдвох': 2, 'двох': 2, 'двоє': 2, 'пара': 2, 'утрьох': 3, 'втрьох': 3, 'трьох': 3, 'троє': 3,
    'учотирьох': 4, 'вчотирьох': 4, 'чотирьох': 4, 'четверо': 4, 'пятьох': 5, 'пятеро': 5, 'шістьох': 6, 'шестеро': 6,
    'сімох': 7, 'семеро': 7, 'вісьмох': 8, 'восьмеро': 8,
  };
  /// Слова запиту й «скільки нас», якщо назвали: число 1–12 або «удвох», «самому»…
  function parse(q) {
    let players = null;
    const words = [];
    for (const w of norm(q).split(' ')) {
      if (!w || STOP.has(w)) continue;
      if (/^\d+$/.test(w) && +w >= 1 && +w <= 12) { players = +w; continue; }
      if (COUNT[w]) { players = COUNT[w]; continue; }
      words.push(w);
    }
    return { words, players };
  }

  // ---------- збіг і вага ----------

  const starts = (text, w) => text.startsWith(w) || text.includes(' ' + w);
  /// Вага слова в документі (0 — нема). doc — з index().
  function weigh(d, w) {
    let s = 0;
    if (d.title.startsWith(w)) s = 120;
    else if (starts(d.title, w)) s = 100;
    else if (w.length >= 4 && d.title.includes(w)) s = 70;   // середина слова — лише довге: «кар» не про «Байкарі»
    if (s < 60 && starts(d.names, w)) s = 60;
    else if (s < 45 && w.length >= 4 && d.names.includes(w)) s = 45;
    if (s < 50 && starts(d.tags, w)) s = 50;
    if (s < 40 && (starts(d.other, w) || starts(d.syn, w))) s = 40;
    // Середина слова в тегах, синонімах і підказці — лише від чотирьох літер: «кар» інакше знаходив «лікар» і «скарб».
    if (w.length >= 4) {
      if (s < 35 && d.tags.includes(w)) s = 35;
      if (s < 25 && d.syn.includes(w)) s = 25;
      if (s < 10 && d.hint.includes(w)) s = 10;
    } else if (s < 10 && starts(d.hint, w)) s = 10;
    return s;
  }

  /// Відстань Дамерау–Левенштейна (сусідні переставлені — одна правка), з раннім виходом за межею k.
  function dist(a, b, k) {
    if (Math.abs(a.length - b.length) > k) return k + 1;
    let p2 = null, p = Array.from({ length: b.length + 1 }, (_, j) => j);
    for (let i = 1; i <= a.length; i++) {
      const c = [i];
      let best = i;
      for (let j = 1; j <= b.length; j++) {
        let v = Math.min(p[j] + 1, c[j - 1] + 1, p[j - 1] + (a[i - 1] === b[j - 1] ? 0 : 1));
        if (p2 && i > 1 && j > 1 && a[i - 1] === b[j - 2] && a[i - 2] === b[j - 1]) v = Math.min(v, p2[j - 2] + 1);
        c.push(v);
        if (v < best) best = v;
      }
      if (best > k) return k + 1;
      p2 = p; p = c;
    }
    return p[b.length];
  }
  /// Одруківка: слово запиту схоже на слово назви, тегу чи синоніма (або на його початок — ще не дописав).
  function weighFuzzy(d, w) {
    if (w.length < 4) return 0;
    const k = w.length >= 7 ? 2 : 1;
    let s = 0;
    for (const [list, v] of [[d.titleWords, 30], [d.tagWords, 20], [d.synWords, 15]]) {
      for (const x of list) {
        if (x.length < 3) continue;
        if (dist(w, x, k) <= k || (x.length > w.length && dist(w, x.slice(0, w.length), 1) <= 1)) { s = Math.max(s, v); break; }
      }
    }
    return s;
  }

  /// Документ для пошуку з полів плитки (core.js): title, names (режими), tags (назви тегів), syn, other (розділ,
  /// тема, автотеги), hint — усе нормалізоване один раз.
  function index(o) {
    const d = {
      title: norm(o.title), names: norm((o.names || []).join(' ')), tags: norm((o.tags || []).map(label).join(' ')),
      syn: norm(o.syn || ''), other: norm((o.other || []).join(' ')), hint: norm(o.hint || ''),
    };
    const split = (s) => s.split(' ').filter(Boolean);
    d.titleWords = split(d.title + ' ' + d.names);
    d.tagWords = split(d.tags + ' ' + d.other);
    d.synWords = split(d.syn);
    return d;
  }

  /// Знайти: docs — [{ d: index(...), ...що завгодно }]. Повертає { hits: [{ doc, score }], how, words, players, fixed }:
  /// how — 'exact' | 'layout' (fixed — як прочитали) | 'fuzzy'; players — «скільки нас» із запиту або null.
  function search(docs, q) {
    const run = (words, fuzzy) => {
      const hits = [];
      for (const doc of docs) {
        let score = 0;
        for (const w of words) {
          const s = fuzzy ? weigh(doc.d, w) || weighFuzzy(doc.d, w) : weigh(doc.d, w);
          if (!s) { score = 0; break; }
          score += s;
        }
        if (score) hits.push({ doc, score });
      }
      return hits;
    };
    const p = parse(q);
    if (!p.words.length) return { hits: docs.map((doc) => ({ doc, score: 1 })), how: 'exact', words: [], players: p.players, fixed: '' };
    let hits = run(p.words, false);
    if (hits.length) return { hits, how: 'exact', words: p.words, players: p.players, fixed: '' };
    for (const alt of [swap(q, EN, UK), swap(q, UK, EN)]) {
      const a = parse(alt);
      if (!a.words.length || a.words.join(' ') === p.words.join(' ')) continue;
      hits = run(a.words, false);
      if (hits.length) return { hits, how: 'layout', words: a.words, players: a.players ?? p.players, fixed: a.words.join(' ') };
    }
    return { hits: run(p.words, true), how: 'fuzzy', words: p.words, players: p.players, fixed: '' };
  }

  // ---------- що бачить core.js ----------

  const parsed = {};
  for (const [id, line] of Object.entries(GAMES)) {
    const [t, s] = line.split('|');
    parsed[id] = { tags: (t || '').trim().split(/\s+/).filter((x) => TAGS[x]), syn: (s || '').trim() };
  }
  window.HGameFind = {
    TAGS, AUTO, label, norm, parse, search, index, dist,
    /// Теги гри чи родини (порожньо, коли не вписано).
    tagsOf: (key) => (parsed[key] ? parsed[key].tags : []),
    synOf: (key) => (parsed[key] ? parsed[key].syn : ''),
    /// Іконка тегу (жанрового чи автоматичного).
    iconOf: (k) => TAGS[k] || (AUTO[k] && AUTO[k].icon) || '#',
  };
})();
