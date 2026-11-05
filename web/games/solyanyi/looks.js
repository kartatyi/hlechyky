// «Соляний шлях» — вигляд 3D-сцени в кожній темі: світло, тон, рамка, дерева, міста, підписи. Кольори вантажів, місць і підсвіток
// беремо з themes.js (спільні з 2D і карткою); тут — лише те, що стосується самої сцени.
import { themeOf } from './themes.js';

const SERIF = "Georgia, 'Noto Serif', 'Times New Roman', serif";
const SANS = 'Onest, system-ui, sans-serif';

const LOOKS = {
  parchment: {
    tone: 'aces', exp: 1.12, bg: '#120b07',
    hemi: ['#fff3df', '#8a6c4a', 2.0], sun: ['#ffe2b8', 1.9],
    tableA: '#3f2714', tableB: '#1e1109', tableLight: 1.0, frame: 'paper', frameCol: '#2b1a0f', frameCol2: '#5d3f22',
    plateRim: '#2a1a0c', plateSide: '#2a1a0c',
    river: { a: '#5f928f', b: '#9abfae', glint: '#fff4d8', bank: '#4a3018' }, seaGlint: '#fff3cf', seaGlintK: 0.09, seaDark: 0.07,
    smoke: '#d9cfc0',
    trees: { a: '#5a5a30', b: '#6e6538', c: '#47492a', kind: 'cone', cap: [420, 180] },
    town: { wall: '#ece0c6', roof: '#5e4426', plinth: '#a68e68', door: '#4a3520', win: '#3d4b52', chim: '#6d5136', churchWall: '#b9975f', churchRoof: '#3d2c18', dome: '#4c3a20', gold: '#c8a033', towerWall: '#d6c7a8', towerRoof: '#7b4a32', mill: '#b59468', millBlade: '#ece0c6', plaza: '#efe0ba', plazaRim: '#2a1a0c', plazaBig: '#f6ebcc' },
    cart: { cartWheel: '#33241a', cartWood: '#7d5c34', oxA: '#b9a988', oxB: '#9a8a6c', oxD: '#2c221a' },
    kurgan: '#a48c5c', cityMark: 'town',
    label: { font: `italic 600 11.5px/1.05 ${SERIF}`, big: `italic 700 13px/1.05 ${SERIF}`, color: '#24140a', halo: 'transparent', bg: 'rgba(248,238,208,.84)', pad: '0 3px 1px', radius: 3 },
    vignette: 'radial-gradient(ellipse at 50% 46%, rgba(0,0,0,0) 62%, rgba(14,6,0,.42) 100%)',
  },
  board: {
    tone: 'neutral', exp: 1.0, bg: '#3c2413',
    hemi: ['#ffffff', '#b8a488', 2.25], sun: ['#fff6e4', 1.5],
    tableA: '#6e4324', tableB: '#4a2b15', tableLight: 1.0, frame: 'wood', frameCol: '#5d3518', frameCol2: '#d8a868',
    plateRim: '#3a2614', plateSide: '#3a2614',
    river: { a: '#2c9fd4', b: '#6ccbe8', glint: '#ffffff', bank: '#2e6f3e' }, seaGlint: '#ffffff', seaGlintK: 0.26, seaDark: 0.05,
    smoke: '#ffffff',
    trees: { a: '#3f9a4a', b: '#62b34d', c: '#2f8348', kind: 'round', cap: [520, 220] },
    town: { wall: '#fffaf0', roof: '#d9913f', plinth: '#caa97e', door: '#6b4426', win: '#4da3d0', chim: '#a0522d', churchWall: '#fff4dc', churchRoof: '#3f8a58', dome: '#3f8a58', gold: '#f2bf2c', towerWall: '#f4e6c8', towerRoof: '#c4562f', mill: '#caa06a', millBlade: '#fffaf0', plaza: '#fff5dc', plazaRim: '#8a5a2c', plazaBig: '#ffefc8' },
    cart: { cartWheel: '#4a2e16', cartWood: '#a87a44', oxA: '#c9b593', oxB: '#a99676', oxD: '#3a2a1c' },
    kurgan: '#93b64e', cityMark: 'town',
    label: { font: `600 10.5px/1 ${SANS}`, big: `800 12px/1 ${SANS}`, color: '#27384a', halo: 'transparent', bg: 'rgba(255,253,244,.92)', pad: '1px 4px', radius: 6, shadow: '0 1px 2px rgba(30,30,10,.42)' },
    vignette: 'radial-gradient(ellipse at 50% 46%, rgba(0,0,0,0) 66%, rgba(20,8,0,.3) 100%)',
  },
  petryk: {
    tone: 'neutral', exp: 1.0, bg: '#08070f',
    hemi: ['#fff4e2', '#6e5a48', 2.55], sun: ['#ffe9c8', 0.95],
    tableA: '#1d1812', tableB: '#0c0906', tableLight: 1.0, frame: 'lacquer', frameCol: '#0b0806', frameCol2: '#d6a62e',
    plateRim: '#f6bf26', plateSide: '#0a0812',
    river: { a: '#2fa8d8', b: '#58d0f0', glint: '#e8fbff', bank: '#06060e' }, seaGlint: '#8aa4ff', seaGlintK: 0.08, seaDark: 0.5,
    smoke: '#e8d8b8',
    trees: { a: '#10493f', b: '#1a5c4c', c: '#0d3b39', kind: 'cone', cap: [120, 60] },
    town: { wall: '#f6ead0', roof: '#b23a2c', plinth: '#3d2c1c', door: '#2b1e12', win: '#e7b53f', chim: '#3a2a1c', churchWall: '#f6ead0', churchRoof: '#2a5e8c', dome: '#2a5e8c', gold: '#eabf3f', towerWall: '#ecd9b2', towerRoof: '#b23a2c', mill: '#c19a4b', millBlade: '#f6ead0', plaza: '#fbf1da', plazaRim: '#0a0812', plazaBig: '#fbf1da', petal: '#fbf1da', heart: '#d9301f' },
    cart: { cartWheel: '#1a120b', cartWood: '#b08838', oxA: '#d9c9a4', oxB: '#b9a67e', oxD: '#20170f' },
    kurgan: '#3a3020', cityMark: 'flower',
    label: { font: `600 10.5px/1.05 ${SANS}`, big: `800 12px/1.05 ${SANS}`, color: '#fbf1da', halo: 'transparent', bg: 'rgba(10,8,18,.74)', pad: '0 4px 1px', radius: 4 },
    vignette: 'none',
  },
};

/** Вигляд теми для сцени: своє + кольори з themes.js. Невідома тема — типова. */
export function lookOf(id) {
  const t = themeOf(id), name = LOOKS[t.style?.look] ? t.style.look : LOOKS[t.id] ? t.id : 'parchment';
  return {
    ...LOOKS[name], id: t.id, name,
    routes: t.pal.cargo, routeInk: t.pal.cargoInk, players: t.pal.seat,
    hl: { legal: t.pal.legal, ok: t.pal.legal, near: t.pal.near, plan: t.pal.plan, bad: t.pal.bad, sel: t.pal.sel || t.pal.legal, edge: t.pal.legalEdge || '#000000' },
  };
}
