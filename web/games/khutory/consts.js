// «Хутори» — спільні сталі клієнта: імена, кольори, ціни. Їх імпортують і 3D-сцена (scene.js), і пласка карта
// (board2d.js), і картка столу (khutory.js через import()), щоб назви й кольори ніде не розійшлись.
// Правила гри живуть на сервері (src/Hlechyky/Games/Impl/Khutory*.cs) — тут лише те, що треба для малювання й підказок.

/** Ресурси в незмінному порядку (так їх показує рука). */
export const RES = ['wood', 'clay', 'wool', 'grain', 'ore'];

export const RES_NAME = { wood: 'дерево', clay: 'глина', wool: 'вовна', grain: 'зерно', ore: 'залізо' };
/** Родовий відмінок — «бракує 2 глини». */
export const RES_GEN = { wood: 'дерева', clay: 'глини', wool: 'вовни', grain: 'зерна', ore: 'заліза' };
/** Знахідний відмінок однини — «дає 1 глину», «хоче 1 вовну» (2–4 і 5+ — як називний: «2 глини», «5 глин»). */
export const RES_ACC = { wood: 'дерево', clay: 'глину', wool: 'вовну', grain: 'зерно', ore: 'залізо' };

/** Землі: що родять і як звуться. steppe — Дике поле, нічого не родить, там починає Кармелюк. */
export const TERRAIN = ['forest', 'pit', 'pasture', 'field', 'mountain', 'steppe'];
export const TERRAIN_RES = { forest: 'wood', pit: 'clay', pasture: 'wool', field: 'grain', mountain: 'ore', steppe: null };
export const TERRAIN_NAME = { forest: 'ліс', pit: 'глинище', pasture: 'пасовище', field: 'лан', mountain: 'гори', steppe: 'Дике поле' };

/** Кольори земель (пласка карта, рука, піктограми). 3D-сцена бере їх за основу, але може тонувати світлом. */
export const TERRAIN_COLOR = {
  forest: '#4f7f2e', pit: '#c4663a', pasture: '#8fc25a', field: '#e7b84a', mountain: '#8a8f98', steppe: '#d9c99a',
};
export const RES_COLOR = { wood: '#4f7f2e', clay: '#c4663a', wool: '#8fc25a', grain: '#e7b84a', ore: '#8a8f98' };
export const SEA_COLOR = '#5b8fc7';

/** Кольори гравців за полем players[i].color (0..3). Білі й рожеві замість помаранчевих і фіолетових: червоний з
 *  помаранчевим для дейтеранопа зливались в один бурий (khutory-look.md §6). */
export const SEAT_COLOR = ['#d64541', '#3c78d8', '#f1ece0', '#c86fb5'];
export const SEAT_COLOR_NAME = ['червоні', 'сині', 'білі', 'рожеві'];
/** Фігура гравця — поруч із кольором скрізь (аватарка, бейджі, прапорець на даху): колір сам не розрізняє всіх. */
export const SEAT_SHAPE = ['●', '▲', '■', '◆'];
/** Чи світлий колір гравця (на ньому — темний текст і темний кант). */
export const SEAT_LIGHT = [false, false, true, false];

/** Ціни будівель — для кнопок і підказок «бракує …». Сервер рахує сам; тут лише показати. */
export const COST = {
  road: { wood: 1, clay: 1 },
  town: { wood: 1, clay: 1, wool: 1, grain: 1 },
  city: { grain: 2, ore: 3 },
  dev: { wool: 1, grain: 1, ore: 1 },
};
export const BUILD_NAME = { road: 'Шлях', town: 'Хата', city: 'Містечко', dev: 'Грамота' };

/** Грамоти (карти розвитку). */
export const DEV_NAME = { knight: 'Козак', vp: 'Скарб', road: 'Толока', plenty: 'Врожайний рік', mono: 'Ярмарок' };
export const DEV_HINT = {
  knight: 'Пересунь Кармелюка й забери карту в сусіда. Три козаки — «Найсильніша сотня» (+2 очки)',
  vp: '+1 очко. Лежить у руці потай до кінця партії',
  road: 'Толока: два шляхи задарма',
  plenty: 'Врожайний рік: візьми з банку будь-які дві карти',
  mono: 'Ярмарок: назви ресурс — усі віддають тобі весь свій',
};

/** Нагороди. */
export const AWARD_NAME = { road: 'Чумацький шлях', army: 'Найсильніша сотня' };

/** Ймовірність числа на двох кубиках (кількість комбінацій із 36) — «крапки» під числом. */
export const PIPS = { 2: 1, 3: 2, 4: 3, 5: 4, 6: 5, 8: 5, 9: 4, 10: 3, 11: 2, 12: 1 };

/** Відмінювання: 1 карта, 2 карти, 5 карт. */
export function plural(n, one, few, many) {
  const a = Math.abs(n) % 100, b = a % 10;
  if (a > 10 && a < 20) return many;
  if (b === 1) return one;
  if (b >= 2 && b <= 4) return few;
  return many;
}
