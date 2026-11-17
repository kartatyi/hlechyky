/*
  Крок прикрас (09.11). Вічні прикраси сайту — нік «🌈 Веселка» й рамка «Жива» з Лавки, крапка «в ефірі», «твій хід»,
  «грають зараз», «що нового» — крутились плавно, тобто на частоті монітора: 180 разів на секунду у владіка. Колір, тло,
  тінь і кут градієнта браузер не вміє вести на композиторі, тож КОЖЕН кадр перемальовував і перерастровував ці ніки — на
  будь-якій сторінці, бо Балачки праворуч є всюди. GPU-процес у Chrome один на всі вікна, і YouTube на іншому моніторі
  губив кадри, поки вкладка Глечиків була на екрані (сховаєш вкладку — анімації стають, і лаг зникає; скарга 09.11 — та
  сама, що 08.11 про коло, тільки без кола).

  Заміри (RTX 5060 Ti, 2560×1440, 180 Гц; головна, шість «Веселок» у Балачках): GPU-процес 80–412 мс/с і ~170
  перемальовувань на секунду — проти 6 мс/с і 10 з кроком; головний потік вкладки ~600 → ~20 мс/с.

  Як (той самий прийом, що в колі, games/clicker.js paintDecor): CSS-анімацію зі списку ставимо на паузу (Web Animations)
  і самі пересуваємо їй час 10 разів на секунду — усім разом, тож «Веселки» в стрічці ще й переливаються в лад.
  `steps()` у CSS теж знімав навантаження з GPU, але браузер однаково обслуговував анімацію щокадру (головний потік
  ~105 мс/с). Нові анімації ловимо за animationstart. Коло крокує своє саме — його картку (.clk) не чіпаємо.
  Обертання (крутилки «зачекай», Глек у «Хто скільки») сюди не беремо: 10 кроків на секунду їх смикали б, а крутилка
  живе недовго. Хто просив «менше руху» (prefers-reduced-motion) — у того цих анімацій і так нема.
*/
(() => {
  'use strict';
  if (!document.getAnimations || !window.Animation) return;

  /// Назви @keyframes, які ведемо кроком. Лише нескінченні й лише ті, що не рухають, а переливаються чи пульсують.
  const NAMES = new Set([
    'lv-rainbow', 'lv-slide', 'lv-spin', // Лавка: нік «Веселка» (дрібний і великий) і рамка «Жива» (static/style.css)
    'blink', // крапка «в ефірі» на ▶ у шапці; клітинки, що згасають, у настільних іграх (games/core.css)
    'turnGlow', 'gdk-pulse', // «твій хід» — у шапці й на доку гри
    'gdot', // «грають зараз» у лобі ігор
    'sn-blink', // крапка «що нового» (static/stats-news.css)
    'recpulse', // запис голосу
    'sky-rain', 'sky-snow', // дощ і сніг у сцені «Обрій» на головній (static/polyva.css)
    'play-rings', // кола від ▶, поки ефір грає
  ]);
  const STEP_MS = 100;
  const list = new Set();
  let timer = 0;
  const now = () => (document.timeline && document.timeline.currentTime) || performance.now();

  function take(a) {
    if (list.has(a) || !NAMES.has(a.animationName) || !a.effect) return;
    const t = a.effect.target;
    if (!t || (t.closest && t.closest('.clk'))) return;
    if (a.effect.getTiming().iterations !== Infinity) return;
    a.pause();
    a.currentTime = now();
    list.add(a);
    if (!timer) timer = setInterval(step, STEP_MS);
  }

  function step() {
    if (document.hidden) return;
    // Спершу всі читання, потім усі записи (як у колі): playState перераховує стилі, якщо після запису щось змінилось.
    // Скасовану (ніка вже нема, клас зняли, елемент сховали) прибираємо: запис currentTime її б «воскресив».
    for (const a of list) if (a.playState === 'idle') list.delete(a);
    if (!list.size) { clearInterval(timer); timer = 0; return; }
    const t = now();
    for (const a of list) a.currentTime = t;
  }

  document.addEventListener('animationstart', (e) => {
    if (!NAMES.has(e.animationName) || e.pseudoElement) return;
    const el = e.target;
    if (!el || !el.getAnimations) return;
    for (const a of el.getAnimations()) if (a.animationName === e.animationName) take(a);
  }, true);

  // Те, що встигло стартувати до цього скрипта (розмітка index.html).
  for (const a of document.getAnimations()) if (a.animationName) take(a);
})();
