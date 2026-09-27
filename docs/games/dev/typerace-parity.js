/*
  Звірка кодувальника журналу Клавоперегонів: браузер (web/games/typerace.js → TyperaceCore) проти C#
  (tests/Hlechyky.Tests/Games/TyperaceLogs.cs → FromJs). Вставити в консоль сторінки з завантаженим модулем
  (або прогнати: python qa/scen_parity.py) — надрукує чотири пари { k, d }; вони мають побайтно збігатися з FromJs.
  Сценарії — ті самі, що ScenarioClean / ScenarioErrors / ScenarioPause / ScenarioSwallows у TyperaceLogs.cs.
*/
(() => {
  const clean = [812, 143, 201, 97, 166, 250, 131, 178, 222, 190, 145, 305, 99, 160, 188, 173, 240, 121, 134, 207, 169, 158, 196, 187, 212, 264]
    .map((ms) => ['c', ms]);
  const errors = [['c', 1030], ['c', 150], ['x', 90], ['s', 60], ['b', 420], ['c', 180], ['c', 170], ['c', 210], ['c', 190], ['c', 140],
    ['b', 330], ['c', 260], ['c', 200], ['c', 150], ['c', 180], ['x', 110], ['b', 380], ['c', 160], ['c', 170], ['c', 190],
    ['c', 200], ['c', 150], ['c', 160], ['c', 170], ['c', 180], ['c', 150], ['c', 160], ['c', 170], ['c', 190], ['c', 200],
    ['c', 210], ['c', 180], ['c', 190]];
  const pause = [['c', 500], ['c', 150], ['c', 160], ['c', 20000], ['C', 140], ['c', 150], ['c', 160], ['c', 170], ['c', 180], ['c', 190],
    ['c', 200], ['c', 210], ['c', 220], ['c', 230], ['c', 240], ['c', 250], ['c', 160], ['c', 170], ['c', 180], ['c', 190],
    ['c', 200], ['c', 210], ['c', 220], ['c', 230], ['c', 240], ['c', 250]];
  const out = [clean, errors, pause].map((s) => window.TyperaceCore.log(s));
  // четвертий: неохайний друкар — проковтнуті натиски клієнт пише не більше чотирьох на одну червону літеру
  // (TyperaceCore.capped ↔ TyperaceLogs.AsClientWrites, сценарій ScenarioSwallows, текст на 40 знаків)
  const swallows = [];
  const miss = { 5: 6, 12: 2, 20: 9, 33: 5 };
  for (let i = 0; i < 40; i++) {
    if (miss[i]) {
      swallows.push(['x', 100 + i]);
      for (let s = 0; s < miss[i]; s++) swallows.push(['s', 50 + 7 * s]);
      swallows.push(['b', 300]);
    }
    swallows.push(['c', 120 + (i * 37) % 90]);
  }
  out.push(window.TyperaceCore.capped(swallows, 40));
  console.log(JSON.stringify(out));
  return out;
})();
