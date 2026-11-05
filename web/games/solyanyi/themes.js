// «Соляний шлях» — три вигляди на вибір гравця (§11.1, solyanyi-look.md). Цей файл читають 3D-сцена (scene.js),
// пласке поле (board2d.js) і картка столу (solyanyi.js): нова тема — новий запис, без нової гілки API.
//   pal   — кольори мапи й вантажів: land, landEdge, water, river, region, city, cityEdge, cityInk, slot, slotEdge,
//           legal, legalEdge, near, plan, bad, sky, cargo { 8 вантажів + wild + grey }, seat [5];
//           плюс cargoInk (колір значка на плашці вантажу) і sel (обраний шлях).
//   style — вільні прапорці для сцени: tone, exposure, frame ('paper' | 'wood' | 'lacquer'), trees, labels і т. ін.
//           Сцена читає те, що знає; решту ігнорує.

import { CARGO_COLOR, CARGO_INK, SEAT_COLOR } from './consts.js';

export const DEFAULT_THEME = 'parchment';

export const THEMES = [
  {
    id: 'parchment',
    name: 'Стара мапа',
    hint: 'пергамент, шкіра й воскові печатки',
    pal: {
      land: '#ead9ad', landEdge: '#5a3a1e', water: '#a9c2b4', river: '#5f928f', region: '#b79a6e',
      city: '#f7efd9', cityEdge: '#2b1a0d', cityInk: '#24140a',
      slot: '#f3e7c8', slotEdge: '#2b1a0d',
      legal: '#ffd95c', legalEdge: '#8e1a14', near: '#d97a1e', plan: '#b3261e', bad: '#8e1a14', sel: '#fff0a8',
      sky: '#1b1009',
      cargo: { salt: '#b3261e', fish: '#1c5a9e', grain: '#2c7a3a', tar: '#231c17', honey: '#e6ad12', wool: '#f8f3e4', hides: '#c95f12', linen: '#d5689a', wild: '#b78a22', grey: '#d9c79c' },
      cargoInk: { salt: '#fff7e6', fish: '#fff7e6', grain: '#fff7e6', tar: '#f7efd9', honey: '#2b1a0d', wool: '#2b1a0d', hides: '#fff7e6', linen: '#2b1a0d', wild: '#2b1a0d', grey: '#2b1a0d' },
      seat: ['#a51d16', '#2d66a8', '#2f8050', '#d68a1c', '#7a4ea6'],
    },
    style: {
      look: 'parchment', tone: 'aces', exposure: 1.08, frame: 'paper', frameCol: '#2b1a0f', frameCol2: '#5d3f22',
      tableA: '#3f2714', tableB: '#1e1109', trees: 'cone', labels: 'serif', sea: 'engraved',
      labelFont: "Georgia, 'Noto Serif', 'Times New Roman', serif", labelInk: '#24140a', labelHalo: '#f6ebca',
    },
  },
  {
    id: 'board',
    name: 'Настілка на столі',
    hint: 'горіховий стіл, рахівниця довкола мапи, паперові картки',
    pal: {
      land: '#a8d868', landEdge: '#355a2d', water: '#3aa8e4', river: '#2c9fd4', region: '#7fb04f',
      city: '#ffffff', cityEdge: '#2a160b', cityInk: '#2a160b',
      slot: '#efe3c0', slotEdge: '#2a160b',
      legal: '#ffe066', legalEdge: '#2a160b', near: '#ff9f1c', plan: '#c0392b', bad: '#b3261e', sel: '#fff36a',
      sky: '#3c2413',
      cargo: { salt: '#d8352b', fish: '#1f73d1', grain: '#0f7f3f', tar: '#2b2521', honey: '#fbcb1e', wool: '#fbf8ee', hides: '#f07f16', linen: '#f573ae', wild: '#f4c542', grey: '#d9cfb6' },
      cargoInk: { salt: '#ffffff', fish: '#ffffff', grain: '#fff3b0', tar: '#ffffff', honey: '#3a2500', wool: '#2a160b', hides: '#3a1600', linen: '#3f0a26', wild: '#2a160b', grey: '#2a160b' },
      seat: ['#d83a2e', '#2a6fd0', '#2c9b4c', '#f09a17', '#8e5bb5'],
    },
    style: {
      look: 'board', tone: 'neutral', exposure: 1.0, frame: 'wood', frameCol: '#5d3518', frameCol2: '#d8a868',
      tableA: '#6e4324', tableB: '#4a2b15', trees: 'round', labels: 'plate', sea: 'printed',
      labelFont: "Onest, system-ui, sans-serif", labelInk: '#2a160b', labelHalo: '#fffaf0',
    },
  },
  {
    id: 'petryk',
    name: 'Петриківка',
    hint: 'чорний лак, соняшник, калина й вишиті шви',
    pal: {
      land: '#241e42', landEdge: '#f6bf26', water: '#121a58', river: '#58d0f0', region: '#0a0812',
      city: '#fbf1da', cityEdge: '#0a0812', cityInk: '#fbf1da',
      slot: '#9b917d', slotEdge: '#0a0812',
      legal: '#ffd34d', legalEdge: '#0a0812', near: '#ef7d1a', plan: '#ff6a3d', bad: '#d9301f', sel: '#ffe080',
      sky: '#08070f',
      cargo: { salt: '#e8341f', fish: '#2474e8', grain: '#12a862', tar: '#121019', honey: '#fbc924', wool: '#f6eeda', hides: '#f5851c', linen: '#f46fab', wild: '#2a1450', grey: '#9b917d' },
      cargoInk: { salt: '#ffffff', fish: '#ffffff', grain: '#ffffff', tar: '#f6bf26', honey: '#1b0f06', wool: '#1b0f06', hides: '#1b0f06', linen: '#1b0f06', wild: '#ffe08a', grey: '#1b0f06' },
      seat: ['#ef4a3a', '#3f8ff0', '#2fbe62', '#ffa820', '#b57bea'],
    },
    style: {
      look: 'petryk', tone: 'neutral', exposure: 1.0, frame: 'lacquer', frameCol: '#0b0806', frameCol2: '#d6a62e',
      tableA: '#33261c', tableB: '#17100b', trees: 'cone', labels: 'halo', sea: 'lacquer', regionsDark: true,
      labelFont: "Onest, system-ui, sans-serif", labelInk: '#fbf1da', labelHalo: '#0a0812',
    },
  },
];

/** Запис теми за id; невідомий — типова. */
export function themeOf(id) {
  return THEMES.find((t) => t.id === id) || THEMES.find((t) => t.id === DEFAULT_THEME);
}

/** Палітра-запас на випадок запису без кольору: той самий CARGO_COLOR і SEAT_COLOR, що в consts.js. */
export const FALLBACK = { cargo: CARGO_COLOR, cargoInk: CARGO_INK, seat: SEAT_COLOR };
