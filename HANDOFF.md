# K11 «Додатки за перемикачем» — передача

- `web/games/koliya/extras.js` — гуси 🪿 (`geese: 'off'|'on'`, geeseSec/geeseMax/geeseSpeed) і проміжна станція 🏪
  (`stationMode: 'end'|'upgrade'`). Обидва типово вимкнені; вимкнене не чіпає rng і світ (hash як без додатків).
- Гачки в rules.js (вже вставлено, позначені `K11`): імпорт; `extrasInit(s, params)` перед `return s` у newGame;
  `if (!extrasStep(s, cmds)) stepTrain(s);` у step; `if (extrasUse(s, n, f)) { pl.cd = ACT_CD; return; }` у use після facingCell.
  Перемикачі злито рядками в PARAM_META (група «Додатки») і VARIANTS. extras.js ↔ rules.js — цикл імпорту, тому в
  extras.js на верхньому рівні лише літерали (не спредити EXTRA_* у rules — TDZ).
- `extras-render.js`: `createExtrasRender(scene, THREE)` → { update(s, alpha), dispose() } — ще НЕ підключено в render.js
  (K1): після `const scene = new THREE.Scene()` → `const extras = createExtrasRender(scene, THREE);`, у draw → `extras.update(s, alpha);`.
  `drawExtras2d(canvas, s)` — підключено в main.js (?view=2d).
- Нові події: `honk` (зграя/схопила), `scare` (налякали, p), `goose_lost` (річ упала попереду), `upgrade` (p); `whistle` при прибутті на проміжну.
- Стан: s.geese, s.geeseT, s.geeseId, s.mid { x0,y0,x1,y1, signs[{x,y,k}], pick, wait, len1 }. У hash гусей нема (rng ловить розбіжність) — для мережі варто додати.
- Upgrade-режим: смуга = дві згенеровані (друга — сід ^ 0x5EED5EED, без інструментів); s.w подвоюється, params.stripLen ні.
  Поки потяг стоїть на проміжній — stepTrain не крутиться (котел теж «відпочиває»); speedGrowth рахує й час стоянки.
- Далі: тост у HUD на `upgrade`/`scare` з UPGRADES[k].label; бот не знає про гусей і таблички; звуки для honk.
- Знімки: _kolia/shots/k11-geese.png, k11-station.png, k11-picked.png, k11-3d.png. Тести: tests/koliya-js/extras.test.mjs (8).
