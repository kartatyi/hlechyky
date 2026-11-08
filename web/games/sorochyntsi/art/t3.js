// Сцени крамниць рівня III («Славні майстри», id 70–89). Див. art/lib.js — поле 100 × 102, головне — у центрі й угорі.
import {
  C, at, ic, sky, fill, sun, moon, stars, hills, floor, wall, river, grass, flowers, hata, yatka, tree, poplar, willow, tyn,
  jug, makitra, bowl, shelf, basket, sack, rushnyk, rope, shirt, svyta, kilim, cart, ox, sheep, boat, fire, pich, skrynia,
  church, mountains, sunflower, bottle, bunch, vinok, person, chort, fernGlow, coins,
} from './lib.js';

export const S3 = {
  // Рушникова світлиця — жердка з рушниками, скриня, дівчина у вінку
  70: () => wall(78) + `<rect x="6" y="11" width="88" height="2.6" rx="1.3" fill="${C.wood}"/>`
    + rushnyk(10, 13, 20, 34) + rushnyk(40, 13, 20, 40, { e: C.black }) + rushnyk(70, 13, 20, 34)
    + skrynia(66, 92, 30) + person(46, 92, 1.05, { w: 1, hat: 'vinok', arms: 'work', skirt: '#2a5a9a', hold: `<rect x="5" y="-23" width="9" height="13" fill="${C.linen}"/><path d="M5 -13h9" stroke="${C.red}" stroke-width="1.2"/>` }),
  // Вишиванки на весь ярмарок — ятка, сорочки на мотузці
  71: (id) => sky(id, C.sky) + sun(84, 14, 7) + hills(62, C.grassL, 4, 7) + floor(80, '#c9a46a', '#a7804f')
    + yatka(6, 80, 88, C.red, { h: 58 })
    + rope(8, 92, 32, 3) + shirt(20, 30, 0.95) + shirt(40, 33, 0.95, '#fbf6ea', C.black) + shirt(60, 33, 0.95, '#fbf6ea', C.blue) + shirt(80, 30, 0.95, '#f3e2c8', C.red),
  // Купець полотняний — купець біля воза з сувоями
  72: (id) => sky(id, C.sky) + hills(56, C.grassL, 5, 3) + fill('#c9a46a', 70, 32) + poplar(90, 70, 1.1)
    + cart(56, 76, 1.25, `<g>${[-12, -4, 4, 12].map((x, i) => `<rect x="${x - 3.5}" y="-27" width="7" height="15" rx="3" fill="${i % 2 ? '#fbf6ea' : C.linen}"/><path d="M${x - 3.5} -21h7" stroke="${C.red}" stroke-width="1"/>`).join('')}<rect x="-14" y="-33" width="26" height="7" rx="3" fill="${C.linen}"/></g>`)
    + person(30, 82, 1.15, { hat: 'kapelyukh', coat: '#2c3e66', pants: '#5a3a1a', arms: 'down' }),
  // Дніпровські рибалки — кручі, два човни, невід
  73: (id) => sky(id, C.eve) + sun(30, 22, 8) + `<path d="M62 52l6-22 14-6 18 4V52z" fill="#b98a5a"/><path d="M62 52l6-22 14-6 18 4" fill="none" stroke="#8a6a3a" stroke-width="1.2"/>` + poplar(84, 26, 0.7)
    + river(50, 52, 9) + boat(40, 62, 0.85) + person(36, 62, 0.62, { hat: 'brylj', arms: 'work', pants: '#5a3a1a' }) + person(46, 62, 0.62, { hat: 'none', arms: 'up' })
    + boat(78, 70, 0.75) + person(78, 70, 0.55, { hat: 'shapka', arms: 'work' })
    + `<path d="M50 62q8 6 20 8" stroke="${C.linen}" stroke-width="1" stroke-dasharray="2 1.5" fill="none"/>`
    + `<path d="M12 18q3-3 6 0q3-3 6 0M70 12q2-2 4 0q2-2 4 0" stroke="#5a4a3a" stroke-width="1" fill="none"/>`,
  // Рибний ряд — ятка з рибою, ваги, перекупка в хустці
  74: (id) => sky(id, C.sky) + floor(78, '#c9a46a', '#a7804f') + yatka(30, 78, 66, C.blue, { h: 56 })
    + rope(34, 92, 32, 2) + [42, 54, 66, 78].map((x) => `<path d="M${x} 33v4" stroke="${C.woodD}" stroke-width=".8"/>` + at(x, 46, 1, `<g transform="rotate(90)">${ic('fish', 0, 0, 16, { fill: '#9ab5cc', detail: '#3c5670' })}</g>`)).join('')
    + ic('fish', 52, 54, 13, { fill: '#c0a070', detail: '#5a3a1a' }) + ic('fish', 66, 55, 13, { fill: '#9ab5cc', detail: '#3c5670' }) + ic('fish', 80, 54, 13)
    + `<path d="M88 50v8M82 50h12M82 50l-2 5h6zM94 50l-2 5h6z" stroke="${C.woodD}" stroke-width="1" fill="${C.gold}"/>`
    + person(20, 80, 1.1, { w: 1, hat: 'khustka', hatC: C.blue, skirt: '#5a3a1a', apron: C.red, arms: 'work' }),
  // Обоз із Криму — воли, вози з сіллю, степ
  75: (id) => sky(id, ['#f2b06c', '#fbe6c0']) + sun(80, 30, 10, '#ffd27a') + fill('#d9b46a', 56, 46) + `<path d="M0 56h100" stroke="#b8904a" stroke-width="1.5"/>`
    + ox(26, 66, 0.9) + cart(52, 66, 0.9, `<path d="M-16 -12q16 -14 32 0z" fill="#f3f0e8"/>`) + ox(66, 58, 0.6) + cart(84, 58, 0.6, `<path d="M-16 -12q16 -14 32 0z" fill="#f3f0e8"/>`)
    + person(40, 74, 0.85, { hat: 'brylj', arms: 'down', pants: '#f3ead7', shirt: '#fbf6ea' }) + grass(75, 102, 10, '#b8904a', 8),
  // Знахарська хата — хата в лісі, пучки під стріхою, кіт
  76: (id) => sky(id, C.dusk) + tree(10, 70, 1.6, '#3a5a3a') + tree(92, 66, 1.4, '#2f4f33') + fill('#3f5f2e', 66, 36)
    + hata(32, 70, 44, { lit: true, win: 1 }) + [36, 44, 52, 62, 70].map((x, i) => bunch(x, 50, 0.6, [C.green, '#7aa34a', C.purple, C.green, '#9a8a3a'][i])).join('')
    + `<path d="M66 31q3-6 0-12t2-12" stroke="#cfc6d8" stroke-width="2.4" fill="none" opacity=".6"/>`
    + `<path d="M84 70c0-6 1-9 4-10l-1-3 2 2 2-2-1 3c2 1 3 4 3 10z" fill="${C.black}"/><path d="M93 68q4-2 3-7" stroke="${C.black}" stroke-width="1.2" fill="none"/>`,
  // Купальське зілля — ніч, ріка, вінки зі свічками, вогнище
  77: (id) => sky(id, C.night) + stars(77, 12, 40) + moon(80, 14, 7, '#151a3a') + hills(46, '#25402a', 4, 4) + river(52, 50, 5)
    + [30, 52, 74].map((x, i) => vinok(x, 62 + i * 6, 6, false) + `<rect x="${x - 0.8}" y="${56 + i * 6}" width="1.6" height="5" fill="#fbf3e1"/><path d="M${x} ${56 + i * 6}q-1.2-2 0-3.4 1.2 1.4 0 3.4z" fill="#ffcf6b"/>`).join('')
    + fire(88, 50, 0.8) + at(14, 52, 1, `<path d="M0 0q-2-10 2-16M0 0q4-8 10-10M0 0q-6-6-10-6" stroke="#3f6b38" stroke-width="1.6" fill="none"/>`),
  // Монастирський сад — мур, бані, грядки, соняшники
  78: (id) => sky(id, C.sky) + church(56, 46, 1.15) + `<rect y="44" width="100" height="8" fill="#efe6cf"/><path d="M0 44h100" stroke="#cfc0a0" stroke-width="1.2"/>`
    + fill('#6b8a3e', 52, 50) + [58, 66, 74, 82, 90].map((y) => `<path d="M0 ${y}h100" stroke="#4a6a2e" stroke-width="3" stroke-dasharray="4 3"/>`).join('')
    + sunflower(86, 70, 1) + sunflower(94, 72, 0.85) + tree(18, 52, 1.1, '#4a7a3a') + `<circle cx="16" cy="34" r="1.6" fill="${C.redL}"/><circle cx="21" cy="38" r="1.6" fill="${C.redL}"/>`,
  // Гончарний кут — десятки глечиків
  79: () => wall(80, '#ead8b6') + shelf(4, 28, 92) + shelf(4, 54, 92)
    + [12, 26, 40, 54, 68, 82].map((x, i) => jug(x, 28, [12, 14, 11, 15, 12, 13][i], [C.clay, C.clayL, '#a8431f', C.clay, C.clayL, '#a8431f'][i], i % 2 ? '#6b2614' : '#f6efdc')).join('')
    + [16, 34, 52, 70, 88].map((x, i) => (i % 2 ? makitra(x, 54, 14) : bowl(x, 54, 15))).join('')
    + jug(40, 96, 20) + jug(62, 96, 24, C.clayL) + jug(84, 96, 18, '#a8431f', '#f6efdc'),
  // Майстер-кахляр — піч у розписних кахлях, майстер
  80: () => wall(82, '#efe2c6') + pich(46, 84, 46, 64, { tiles: true, c: '#f6efdc' }) + `<rect x="44" y="18" width="50" height="4" fill="#d9c9a4"/>`
    + person(26, 86, 1.05, { hat: 'shapka', arms: 'work', apron: false, pants: '#5a3a1a', hold: `<rect x="7" y="-25" width="8" height="8" fill="#fbf3e1" stroke="${C.blue}" stroke-width=".8"/><circle cx="11" cy="-21" r="1.8" fill="${C.blue}"/>` }),
  // Цех гончарів — корогва цеху, майстри, стіл із посудом
  81: (id) => sky(id, C.sky) + floor(76, '#c9a46a', '#a7804f')
    + `<path d="M64 6v66" stroke="${C.woodD}" stroke-width="1.8"/><path d="M64 8h30v26l-15 6-15-6z" fill="#8e2a24"/><path d="M66 10h26v22l-13 5-13-5z" fill="none" stroke="${C.gold}" stroke-width="1"/>` + jug(79, 30, 14, C.gold, '#8e2a24')
    + `<rect x="24" y="58" width="40" height="4" fill="${C.wood}"/><path d="M28 62v14M60 62v14" stroke="${C.woodD}" stroke-width="2"/>` + jug(32, 58, 11) + makitra(44, 58, 12) + jug(56, 58, 12, C.clayL)
    + person(84, 78, 1, { hat: 'shapka', coat: '#5a3a1a', arms: 'down', pants: '#2c3e66' }) + person(14, 78, 0.95, { hat: 'brylj', arms: 'up' }),
  // Полонинська отара — гори, вівці, вівчар із трембітою
  82: (id) => sky(id, C.sky) + mountains(58) + fill('#86a35a', 70, 32) + hills(70, '#7a9a4e', 3, 5)
    + sheep(34, 80, 0.9) + sheep(50, 76, 0.8, '#ece4d2') + sheep(64, 82, 0.95) + sheep(46, 88, 0.85, '#d9cdb8')
    + person(82, 84, 1, { hat: 'kapelyukh', coat: '#8e2a24', pants: '#f3ead7', arms: 'work', hold: `<path d="M8 -20l22 -26" stroke="${C.woodL}" stroke-width="2.2" stroke-linecap="round"/>` }),
  // Решетилівські килими — килими на тину
  83: (id) => sky(id, C.sky) + sun(14, 12, 6) + tree(90, 54, 1.2) + fill('#86a35a', 56, 46)
    + tyn(4, 96, 80, 14) + kilim(10, 22, 26, 44) + kilim(40, 18, 26, 48, [C.black, C.red, C.yellow]) + kilim(70, 26, 24, 40, ['#2a5a9a', C.red, '#f3ead7'])
    + `<path d="M6 22h32M38 18h30M68 26h28" stroke="${C.woodD}" stroke-width="1.6"/>`,
  // Цех кравців — свити на кілках, ножиці, стіл із сукном
  84: () => wall(80, '#efe2c6') + `<rect x="8" y="12" width="88" height="2.4" fill="${C.wood}"/>`
    + svyta(26, 14, 1, '#5a3a1a') + svyta(52, 14, 1, '#2c3e66') + svyta(78, 14, 1, C.red, C.gold)
    + `<rect x="30" y="54" width="62" height="4" fill="${C.wood}"/><rect x="36" y="48" width="44" height="6" fill="#8a5a35"/>`
    + at(72, 46, 1, `<path d="M0 0l12 6M0 6l12-6" stroke="#9aa8b8" stroke-width="1.8"/><circle cx="-2" cy="-1" r="2.2" fill="none" stroke="#5a6a7a" stroke-width="1.2"/><circle cx="-2" cy="7" r="2.2" fill="none" stroke="#5a6a7a" stroke-width="1.2"/>`)
    + `<path d="M40 52q20 4 34-2" stroke="${C.yellow}" stroke-width="1.2" stroke-dasharray="1.5 1" fill="none"/>`,
  // Червона свитка — ніч, порожня ятка, червона свитка на кілку, свиняче рило у вікні
  85: (id) => sky(id, C.night) + stars(85, 10, 36) + moon(18, 14, 7, '#151a3a') + fill('#2a2a3a', 74, 28)
    + hata(56, 74, 40, { lit: true, win: 1, door: false }) + `<ellipse cx="85" cy="52" rx="3.6" ry="2.6" fill="#e8a0a0"/><circle cx="84" cy="52" r=".7" fill="#7a3a3a"/><circle cx="86.4" cy="52" r=".7" fill="#7a3a3a"/>`
    + `<rect x="28" y="20" width="2.4" height="56" fill="${C.woodD}"/>` + svyta(29, 22, 1.05, '#c0201f', '#1a0d0d')
    + `<ellipse cx="29" cy="40" rx="22" ry="26" fill="#ff3a2a" opacity=".12"/>`,
  // Ворожка з Диканьки — свічка, карти, сова, бабця в хустці
  86: (id) => sky(id, ['#2a1d2e', '#4a2e3a']) + `<ellipse cx="54" cy="50" rx="40" ry="32" fill="#ffcf6b" opacity=".12"/>`
    + shelf(56, 18, 40, C.woodD) + at(84, 18, 1, `<ellipse cx="0" cy="-7" rx="5" ry="6.5" fill="#8a6a4a"/><circle cx="-2" cy="-9" r="1.8" fill="#ffd86b"/><circle cx="2" cy="-9" r="1.8" fill="#ffd86b"/><path d="M-1 -6l1 1.6 1-1.6z" fill="#3a2414"/>`) + bottle(66, 18, 0.9, '#7a4a9a')
    + `<rect x="18" y="62" width="70" height="5" fill="${C.wood}"/>` + [36, 46, 56].map((x, i) => `<rect x="${x}" y="54" width="8" height="11" rx="1" fill="#fbf3e1" transform="rotate(${(i - 1) * 12} ${x + 4} 60)"/><path d="M${x + 4} 57v5" stroke="${i === 1 ? C.red : C.black}" stroke-width="1.6" transform="rotate(${(i - 1) * 12} ${x + 4} 60)"/>`).join('')
    + `<rect x="72" y="48" width="3" height="14" fill="#fbf3e1"/><path d="M73.5 48q-2.4-4 0-7 2.4 3 0 7z" fill="#ffcf6b"/>`
    + person(24, 64, 1, { w: 1, hat: 'khustka', hatC: '#4a2e5a', skirt: '#2a1d17', apron: '#5a3a5a', arms: 'work', shirt: '#e8dcc8' }),
  // Скарб під папороттю — ліс уночі, цвіт, скриня з дукатами
  87: (id) => sky(id, C.night) + stars(87, 8, 30) + tree(10, 60, 1.8, '#1f3324') + tree(90, 58, 1.7, '#1f3324') + fill('#1f3324', 58, 44)
    + at(50, 74, 1, `<path d="M0 0q-12-6-22-4M0 0q12-6 22-4M0 0q-8-12-16-16M0 0q8-12 16-16" stroke="#3f6b38" stroke-width="2" fill="none"/>`)
    + fernGlow(id + 'g', 50, 40, 16) + skrynia(60, 84, 30, '#5a3a1a') + `<path d="M60 67.5l3-6h24l3 6" fill="#6e4523"/>` + coins(74, 66, 6),
  // Чорт у корчмі — ніч, корчма зі світлими вікнами, чорт за рогом, цвіт у траві
  88: (id) => fill('#1f2347') + moon(80, 16, 9, '#1f2347') + stars(88, 6, 30)
    + `<path d="M14 56 40 30l26 26z" fill="#c9a86a"/><path d="M14 56 40 30l26 26" stroke="#8a6a3a" stroke-width="1.5" fill="none"/>`
    + `<rect x="18" y="55" width="44" height="30" fill="#efe6cf"/><rect x="24" y="62" width="10" height="9" fill="#ffcf6b"/><rect x="46" y="62" width="10" height="9" fill="#ffcf6b"/><path d="M29 62v9M51 62v9" stroke="#8a6a3a" stroke-width="1"/><rect x="37" y="68" width="7" height="17" fill="#6e4523"/>`
    + `<rect x="62" y="40" width="3" height="20" fill="${C.woodD}"/><rect x="54" y="42" width="11" height="7" fill="#7a4f2c"/><path d="M57 45.5h5" stroke="${C.gold}" stroke-width="1"/>`
    + chort(72, 85, 1.1) + fill('#2c4a2a', 85, 17) + grass(88, 102, 6, '#3f6b38', 10) + fernGlow(id + 'g', 44, 92, 16),
  // Купальський вогонь — ніч, велике багаття, хлопець перестрибує
  89: (id) => sky(id, C.night) + stars(89, 12, 40) + fill('#25402a', 74, 28) + `<ellipse cx="50" cy="66" rx="40" ry="26" fill="#ff8a3a" opacity=".16"/>`
    + fire(50, 78, 2.1) + person(52, 44, 0.9, { arms: 'up', hat: 'none', pants: '#2c3e66' }).replace('<g ', '<g opacity=".95" ')
    + person(18, 82, 0.85, { w: 1, hat: 'vinok', arms: 'up', skirt: '#2a5a9a' }) + person(84, 82, 0.85, { w: 1, hat: 'vinok', arms: 'up', skirt: C.red, braid: 1 }),
};
