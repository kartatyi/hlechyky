// «Скарб Полуботка» — спільні сталі клієнта. Імпортують 3D-сцена (scene.js), пласке поле (board2d.js) і картка
// (skarb.js через import()). Правила — лише на сервері (src/Hlechyky/Games/Impl/Skarb*.cs); тут — що треба малювати.
// Контракт: docs/games/specs/skarb.md.

export const BIT = { N: 1, E: 2, S: 4, W: 8 };
export const DIRS = ['N', 'E', 'S', 'W'];
export const STEP = { N: [0, -1], E: [1, 0], S: [0, 1], W: [-1, 0] };
export const OPP = { N: 'S', E: 'W', S: 'N', W: 'E' };
/** Поворот маски на 180°. */
export const rot180 = (m) => ((m & 1) << 2) | ((m & 4) >> 2) | ((m & 2) << 2) | ((m & 8) >> 2);

/** Поле: межі включно, колодязь, мітки (індекс = номер мітки), шахта над колодязем. Сервер шле те саме у view.grid. */
export const GRID = { x0: -4, x1: 12, y0: -4, y1: 4, start: [0, 0],
  goals: [[8, -2], [8, 0], [8, 2]], shaft: [[0, -1], [0, -2], [0, -3], [0, -4]] };

/** Форми карт (маска при r = 0, c — чи з'єднані краї через середину). */
export const SHAPES = {
  start: { m: 15, c: true }, x: { m: 15, c: true }, tv: { m: 7, c: true }, th: { m: 14, c: true },
  v: { m: 5, c: true }, h: { m: 10, c: true }, ce: { m: 6, c: true }, cw: { m: 12, c: true },
  dx: { m: 15, c: false }, dtv: { m: 7, c: false }, dth: { m: 14, c: false }, dv: { m: 5, c: false },
  dh: { m: 10, c: false }, dce: { m: 6, c: false }, dcw: { m: 12, c: false }, ds: { m: 4, c: false }, de: { m: 2, c: false },
  gold: { m: 15, c: true }, stoneA: { m: 9, c: true }, stoneB: { m: 12, c: true },
};
export const SHAPE_NAME = {
  start: 'Колодязь', x: 'Перехрестя', tv: 'Трійник стоячий', th: 'Трійник лежачий', v: 'Хід униз', h: 'Хід уздовж',
  ce: 'Коліно праве', cw: 'Коліно ліве', dx: 'Глухе перехрестя', dtv: 'Глухий трійник', dth: 'Глухий трійник',
  dv: 'Глухий хід', dh: 'Глухий хід', dce: 'Глухе коліно', dcw: 'Глухе коліно', ds: 'Нора', de: 'Нора',
  gold: 'Скарб Полуботка', stoneA: 'Камінь', stoneB: 'Камінь',
};

export const TOOLS = ['pick', 'lamp', 'cart'];
export const TOOL_NAME = { pick: 'кирка', lamp: 'лампа', cart: 'тачка' };
export const TOOL_ACC = { pick: 'кирку', lamp: 'лампу', cart: 'тачку' };
export const TOOL_ICON = { pick: '⛏', lamp: '🏮', cart: '🛒' };
export const BREAK_NAME = { pick: 'Зламана кирка', lamp: 'Розбита лампа', cart: 'Тачка без колеса' };
export const FIX_NAME = { pick: 'Нова кирка', lamp: 'Нова лампа', cart: 'Колесо до тачки',
  'pick+lamp': 'Кирка й лампа', 'pick+cart': 'Кирка й тачка', 'lamp+cart': 'Лампа й тачка' };
export const ACTION_NAME = { map: 'Стара мапа', rockfall: 'Завал' };
export const ACTION_HINT = {
  break: 'Зламай інструмент іншому — поки не полагодить, ходу не покладе',
  fix: 'Полагодь зламане собі чи іншому',
  map: 'Таємно глянь, що під однією з міток',
  rockfall: 'Засип будь-який хід на полі (не колодязь і не мітку)',
  path: 'Поклади хід, що тягнеться від колодязя. Краї мусять сходитись із сусідами',
};

export const ROLE_NAME = { digger: 'Копач', mole: 'Кріт' };
export const ROLE_ICON = { digger: '⛏', mole: '🕳' };
export const ROLE_HINT = {
  digger: 'Докопайся до скарбу Полуботка разом з іншими копачами. Стережись кротів',
  mole: 'Хай золото лишиться в землі: заводь хід у глухий кут, ламай кирки. Але не видай себе',
};
export const GOAL_NAME = { gold: 'Скарб Полуботка', stone: 'Порожньо — лише каміння' };
export const GOAL_PLACE = ['верхня мітка', 'середня мітка', 'нижня мітка'];
export const SIDE_NAME = { diggers: 'Копачі докопались!', moles: 'Кроти взяли своє', none: 'Скарб лишився в землі' };

/** Ролі (кроти, копачі) і карт у руці за кількістю гравців; злитки кротам за їх кількістю. */
export const ROLE_PACK = { 3: [1, 3], 4: [1, 4], 5: [2, 4], 6: [2, 5], 7: [3, 5], 8: [3, 6], 9: [3, 7], 10: [4, 7] };
export const HAND = { 3: 6, 4: 6, 5: 6, 6: 5, 7: 5, 8: 4, 9: 4, 10: 4 };
export const MOLE_GOLD = { 1: 4, 2: 3, 3: 3, 4: 2 };
export const ROUNDS = 3;

/** Кольори гравців за players[i].color (= місце, 0..9). */
export const SEAT_COLOR = ['#d64541', '#3c78d8', '#e8912d', '#8e6bd8', '#2e9e6a', '#e05fa8', '#1fb5c4', '#b8a02a', '#7d5a3c', '#9aa3ad'];
export const SEAT_COLOR_NAME = ['червоні', 'сині', 'помаранчеві', 'фіолетові', 'зелені', 'рожеві', 'бірюзові', 'гірчичні', 'брунатні', 'сірі'];

/** Палітра розрізу (3D бере за основу, 2D — як є). Шари ґрунту — за рядком y (включно). */
export const STRATA = [
  { y0: -4, y1: -4, color: '#3b2a1e', name: 'чорнозем' },
  { y0: -3, y1: -2, color: '#5a3d26', name: 'суглинок' },
  { y0: -1, y1: 1, color: '#8a5a32', name: 'глина' },
  { y0: 2, y1: 3, color: '#a77b4a', name: 'пісок' },
  { y0: 4, y1: 4, color: '#6d6a66', name: 'камінь' },
];
export const PAL = {
  tunnel: '#1e140d', wood: '#9a6b3c', lamp: '#ffcf6b', gold: '#f2c14e', stone: '#8d8f94', grass: '#5f8f3a',
  skyTop: '#22304f', skyLow: '#e8a56b', wall: '#f4efe4', thatch: '#d9b45a', legal: '#ffd35a', alt: '#ffe9a8',
  bad: '#d64541', map: '#e9d6a8',
};

/** 1 злиток, 2 злитки, 5 злитків. */
export function plural(n, one, few, many) {
  const a = Math.abs(n) % 100, b = a % 10;
  if (a > 10 && a < 20) return many;
  if (b === 1) return one;
  if (b >= 2 && b <= 4) return few;
  return many;
}
