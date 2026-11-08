// Сцени крамниць рівня I («Ятки», id 0–39). Див. art/lib.js — поле 100 × 102, головне — у центрі й угорі.
import {
  C, at, ic, sky, fill, sun, hills, floor, wall, river, grass, flowers, hata, yatka, tree, willow, tyn,
  jug, makitra, bowl, shelf, basket, sack, rushnyk, rope, kilim, sheep, boat, fire, sunflower, bunch, vinok, person,
} from './lib.js';

const r2 = (n) => Math.round(n * 100) / 100;
const hangFish = (x, y, s, o) => `<g transform="translate(${x},${y}) rotate(90)">${ic('fish', s / 2, 0, s, o)}</g>`;
const DRY = { fill: '#c0a070', detail: '#5a3a1a' };
const spool = (x, y, c, s = 1) => at(x, y, s, `<rect x="-4" y="-10" width="8" height="1.6" fill="${C.wood}"/><rect x="-3" y="-8.4" width="6" height="7" fill="${c}"/><path d="M-3 -6.5h6M-3 -4h6" stroke="#fff" stroke-width=".5" opacity=".5"/><rect x="-4" y="-1.6" width="8" height="1.6" fill="${C.wood}"/>`);
const skein = (x, y, c = C.linen, s = 1) => at(x, y, s, `<path d="M-4 0q-3 10 0 20h8q3-10 0-20z" fill="${c}"/><path d="M-3 4q3 1 6 0M-3 9q3 1 6 0M-3 14q3 1 6 0" stroke="#b9a273" stroke-width=".7" fill="none"/><rect x="-5" y="-1" width="10" height="2" fill="${C.red}"/>`);
const kerchief = (x, y, c, d = '#fff', s = 1) => at(x, y, s, `<path d="M-9 0h18l-9 13z" fill="${c}"/><circle cx="-4" cy="3" r="1" fill="${d}"/><circle cx="3" cy="3" r="1" fill="${d}"/><circle cx="0" cy="7" r="1" fill="${d}"/><path d="M-9 0h18" stroke="${d}" stroke-width=".8" stroke-dasharray="1.4 1"/>`);
const crayfish = (x, y, s = 1, rot = 0) => at(x, y, s, `<g transform="rotate(${rot})"><ellipse cx="0" cy="0" rx="7" ry="3" fill="#d64541"/><path d="M-7 0l-4-3v6z" fill="#b3262e"/><path d="M6 -2l6-4 2 2-5 3M6 2l6 4 2-2-5-3" stroke="#d64541" stroke-width="2" fill="none"/><path d="M-3 -3v-2M0 -3v-2M3 -3v-2M-3 3v2M0 3v2M3 3v2" stroke="#b3262e" stroke-width=".8"/><circle cx="5" cy="-1" r=".7" fill="${C.black}"/></g>`);
const verša = (x, y, s = 1) => at(x, y, s, `<path d="M0 -6l22 6-22 6z" fill="none" stroke="#c8955a" stroke-width="1.6"/><path d="M5 -4.6v9.2M10 -3.3v6.6M15 -2v4" stroke="#c8955a" stroke-width="1.2"/><ellipse cx="0" cy="0" rx="2" ry="6" fill="none" stroke="#a8763a" stroke-width="1.6"/>`);
const garlic = (x, y, s = 1, c = '#f3ead7') => at(x, y, s, `<path d="M0 0v30" stroke="#c8a86a" stroke-width="1.6"/>` + [4, 10, 16, 22, 28].map((yy, i) => `<ellipse cx="${i % 2 ? 3 : -3}" cy="${yy}" rx="4" ry="3.6" fill="${c}"/><path d="M${i % 2 ? 3 : -3} ${yy - 3.6}v-2" stroke="#c8a86a" stroke-width="1"/>`).join(''));
const chamomile = (x, y, r = 2.4) => `<circle cx="${x}" cy="${y}" r="${r}" fill="#fff"/><circle cx="${x}" cy="${y}" r="${r * 0.42}" fill="${C.yellow}"/>`;
const yarrow = (x, y, s = 1) => at(x, y, s, `<path d="M0 0v-16M0 -10l-4-3M0 -12l4-2" stroke="#4f7a3a" stroke-width="1.2"/>` + [[-3, -17], [0, -18], [3, -17], [-1.5, -15.5], [1.5, -15.5]].map(([a, b]) => `<circle cx="${a}" cy="${b}" r="1.6" fill="#f6efdc"/>`).join(''));
const rooster = (x, y, s = 1, c = '#f2e2c0') => at(x, y, s, `<path d="M-8 0q0-10 8-10 3-6 7-4-1 3-3 4 2 4 0 10z" fill="${c}"/><path d="M-8 -6q-6-6-3-12 2 6 6 8" fill="${C.red}"/><path d="M1 -14l1-3 2 2 1-3 1 3" fill="${C.red}"/><circle cx="3" cy="-11" r=".8" fill="${C.black}"/><path d="M-4 -4q3 2 6 0" stroke="${C.green}" stroke-width="1" fill="none"/><circle cx="-1" cy="-7" r="1" fill="${C.blue}"/><rect x="-3" y="0" width="6" height="3" fill="${c}"/>`);
const cup = (x, y, s = 1, c = C.clayL) => at(x, y, s, `<path d="M-4 0v-8h8v8z" fill="${c}"/><path d="M4 -6q3 0 3 2t-3 2" stroke="${c}" stroke-width="1.4" fill="none"/><path d="M-4 -6h8" stroke="#f6efdc" stroke-width=".8"/>`);
const potLid = (x, y, s = 1, c = C.clay) => at(x, y, s, `<path d="M-8 0q-2-10 0-14h16q2 4 0 14z" fill="${c}"/><ellipse cx="0" cy="-14" rx="8.6" ry="2" fill="#8a3a1e"/><path d="M-7 -16q7-5 14 0z" fill="${C.clayL}"/><circle cx="0" cy="-18.4" r="1.4" fill="${C.clayL}"/><path d="M-7 -6h14" stroke="#f6efdc" stroke-width=".9"/>`);
const kumanets = (x, y, s = 1) => at(x, y, s, `<circle cx="0" cy="-12" r="10" fill="${C.clayL}"/><circle cx="0" cy="-12" r="4.6" fill="#f2e7d0"/><path d="M-4 -2h8l-1 2h-6z" fill="${C.clay}"/><path d="M6 -20l3-6h3l-2 6" fill="${C.clay}"/><path d="M-7 -12a7 7 0 0 1 14 0" stroke="${C.green}" stroke-width="1.2" fill="none"/><circle cx="0" cy="-20" r="1" fill="${C.red}"/>`);
const sock = (x, y, c, s = 1) => at(x, y, s, `<path d="M-3 0h6v14l5 3v3h-8l-3-3z" fill="${c}"/><path d="M-3 3h6M-3 6h6" stroke="#fbf3e1" stroke-width="1.2"/>`);
const boot = (x, y, c = '#5a5048', s = 1) => at(x, y, s, `<path d="M-5 -22h9v18l9 1v3h-18z" fill="${c}"/><path d="M-5 -19h9" stroke="${C.red}" stroke-width="1.4"/>`);
const keptar = (x, y, s = 1) => at(x, y, s, `<path d="M-10 0h7l3 4 3-4h7l2 26h-24z" fill="#c8955a"/><path d="M-10 0q-2 14 0 26M10 0q2 14 0 26" stroke="#3a2a1a" stroke-width="2.4" fill="none"/><path d="M-6 8l1.4-1.4 1.4 1.4-1.4 1.4zM5 8l1.4-1.4 1.4 1.4-1.4 1.4zM-6 15l1.4-1.4 1.4 1.4-1.4 1.4zM5 15l1.4-1.4 1.4 1.4-1.4 1.4z" fill="${C.red}"/><path d="M-6 12h3M4 12h3M-6 19h3M4 19h3" stroke="${C.green}" stroke-width="1.2"/>`);
const furHat = (x, y, s = 1) => at(x, y, s, `<path d="M-9 0v-12q9-8 18 0v12z" fill="#3a3330"/>` + Array.from({ length: 12 }, (_, i) => `<circle cx="${r2(-7 + (i % 4) * 4.6)}" cy="${r2(-2 - Math.floor(i / 4) * 4)}" r="1.6" fill="#5a5048"/>`).join('') + `<path d="M-3 -16q6-4 6 2" fill="${C.red}"/>`);

export const S1 = {
  // ── полотно
  // Прядка: біла хата, вікно з рушником, прядка, кошик полотна
  0: () => `<rect width="100" height="102" fill="#f2e7d0"/><rect y="74" width="100" height="28" fill="#a7744a"/><rect y="74" width="100" height="3" fill="#7d5233"/><path d="M0 74h100" stroke="#3d7cc9" stroke-width="2.4"/>`
    + `<rect x="52" y="10" width="34" height="30" rx="2" fill="#7fb2df" stroke="#6e4523" stroke-width="2.5"/><path d="M69 10v30M52 25h34" stroke="#6e4523" stroke-width="2"/>` + rushnyk(48, 8, 42, 8)
    + `<g fill="none" stroke="#6e4523" stroke-width="2.2" stroke-linecap="round"><circle cx="40" cy="48" r="17"/><path d="M40 31v34M23 48h34M28 36l24 24M52 36 28 60"/><path d="M40 48 52 82M40 48 28 82M22 82h36M56 64l8 18"/></g><circle cx="40" cy="48" r="3.4" fill="#6e4523"/>`
    + basket(68, 94, 24) + `<path d="M71 82c0-6 18-6 18 0" fill="#efe6cf"/>`,
  // Мотки льону — мотки на жердці, льон у полі
  1: (id) => sky(id, C.sky) + fill('#86a35a', 60, 42) + Array.from({ length: 18 }, (_, i) => `<path d="M${4 + i * 5.5} 62v-8" stroke="#6a8a3a" stroke-width="1"/><circle cx="${4 + i * 5.5}" cy="53" r="1.4" fill="#7a9ae0"/>`).join('')
    + `<path d="M30 12v52M92 12v52" stroke="${C.woodD}" stroke-width="2.4"/><path d="M28 16h66" stroke="${C.wood}" stroke-width="2.4"/>` + [40, 52, 64, 76, 86].map((x, i) => skein(x, 17, i % 2 ? '#e6d9b8' : C.linen, 1.1)).join(''),
  // Ятка ниток — кольорові котушки на полицях
  2: (id) => sky(id, C.sky) + floor(80, '#c9a46a', '#a7804f') + yatka(24, 80, 72, '#9a5cf0', { h: 60 })
    + [32, 42, 52, 62, 72, 82, 90].map((x, i) => spool(x, 58, [C.red, C.yellow, C.blue, C.green, C.black, '#e8a0a0', C.redL][i], 1.2)).join('')
    + rope(28, 94, 30, 2) + [38, 54, 70, 86].map((x, i) => skein(x, 31, [C.red, C.blue, C.yellow, C.green][i], 0.8)).join(''),
  // Білильниця — полотно стелиться на траві біля річки
  3: (id) => sky(id, C.sky) + sun(84, 12, 7) + river(36, 16, 3) + fill('#86a35a', 52, 50) + willow(14, 50, 1.2)
    + `<path d="M28 60l66-6v14l-66 6z" fill="#fbf6ea"/><path d="M28 64l66-6M28 68l66-6" stroke="#e6d9b8" stroke-width=".8"/>`
    + person(80, 92, 0.95, { w: 1, hat: 'khustka', hatC: '#fbf6ea', arms: 'work', skirt: '#2a5a9a' }) + `<circle cx="70" cy="70" r="1.2" fill="${C.waterL}"/><circle cx="66" cy="66" r=".9" fill="${C.waterL}"/>`,
  // Хустки — кольорові хустки на мотузці
  4: (id) => sky(id, C.sky) + floor(84, '#c9a46a', '#a7804f') + rope(4, 96, 20, 6)
    + kerchief(18, 23, C.red, C.yellow, 1.3) + kerchief(40, 27, '#2a5a9a', '#fff', 1.3) + kerchief(62, 27, C.green, C.red, 1.3) + kerchief(84, 23, C.black, C.redL, 1.3)
    + yatka(30, 84, 66, C.red, { h: 30 }) + person(14, 88, 1, { w: 1, hat: 'khustka', hatC: C.red, arms: 'work' }),
  // Ткаля з хутора — несе сувій полотна, хутір вдалині
  5: (id) => sky(id, C.sky) + hills(50, C.grassL, 6, 5) + hata(66, 50, 26, { win: 1 }) + tree(94, 50, 0.9) + fill('#c9a46a', 66, 36) + `<path d="M0 74q50-14 100-6" stroke="#b8904a" stroke-width="7" fill="none"/>`
    + person(42, 86, 1.15, { w: 1, hat: 'ochipok', arms: 'work', skirt: C.red, hold: `<rect x="2" y="-30" width="16" height="9" rx="4" fill="${C.linen}"/><path d="M6 -30v9M12 -30v9" stroke="${C.red}" stroke-width="1"/>` }),
  // Рушничок — рушник на кілочку біля вікна
  6: () => wall(80) + `<rect x="18" y="12" width="28" height="26" fill="#7fb2df" stroke="${C.wood}" stroke-width="2.4"/><path d="M32 12v26M18 25h28" stroke="${C.wood}" stroke-width="1.8"/>`
    + `<circle cx="70" cy="12" r="2" fill="${C.woodD}"/>` + rushnyk(58, 14, 24, 52) + `<path d="M60 22l4-4 4 4 4-4 4 4 4-4" stroke="${C.red}" stroke-width="1.2" fill="none"/>` + vinok(32, 46, 5, false),
  // Полотняний рядок — сувої на прилавку
  7: (id) => sky(id, C.sky) + floor(80, '#c9a46a', '#a7804f') + yatka(10, 80, 86, '#d9c49a', { h: 58 })
    + [24, 38, 52, 66, 80].map((x, i) => `<rect x="${x - 6}" y="${46 - (i % 2) * 4}" width="12" height="${16 + (i % 2) * 4}" rx="2" fill="${i % 2 ? '#fbf6ea' : C.linen}"/><ellipse cx="${x}" cy="${46 - (i % 2) * 4}" rx="6" ry="2" fill="#e6d9b8"/><path d="M${x - 6} ${52 - (i % 2) * 4}h12" stroke="${C.red}" stroke-width="1"/>`).join(''),
  // ── риба
  // Рибалка з Псла: вечір, річка, човен, верби, рибина вистрибує
  8: (id) => sky(id, C.eve) + `<circle cx="74" cy="26" r="11" fill="#ffe39a"/><path d="M0 50c12-10 22-14 36-8s24-6 36-4 20 6 28 2v14H0z" fill="#5e7d43"/><g fill="#3f5f2e"><ellipse cx="14" cy="40" rx="9" ry="14"/><ellipse cx="88" cy="42" rx="8" ry="12"/></g><path d="M14 54V40M88 54V42" stroke="#4a2e1a" stroke-width="2"/>`
    + river(52, 50, 8) + `<path d="M30 70h36l-6 8H36z" fill="#7a4f2c"/><path d="M30 70h36" stroke="#4a2e1a" stroke-width="1.5"/><path d="M44 70v-11" stroke="#2a1d17" stroke-width="5" stroke-linecap="round"/><circle cx="44" cy="55" r="3.6" fill="#e8c4a0"/><path d="M38.5 52.5h11l-2-4h-7z" fill="#2a1d17"/>`
    + `<path d="M46 61q14-14 30-12" stroke="#4a2e1a" stroke-width="1.3" fill="none"/><path d="M76 49v22" stroke="#f6efdc" stroke-width=".6"/>` + ic('fish', 82, 62, 16, { fill: '#c0d4e6', detail: '#2c4a6e' }) + grass(8, 102, 6, '#5e7d43', 18),
  // Тараня — в'язки на мотузці проти неба
  9: (id) => sky(id, C.sky) + sun(86, 14, 6) + fill('#c9a46a', 76, 26) + `<path d="M10 20v60M92 20v60" stroke="${C.woodD}" stroke-width="2.4"/>` + rope(10, 92, 22, 3)
    + [20, 30, 40, 50, 60, 70, 80].map((x, i) => hangFish(x, 24 + Math.sin(i / 6 * Math.PI) * 5, 15, DRY)).join('') + rope(10, 92, 48, 2) + [26, 42, 58, 74].map((x) => hangFish(x, 50, 14, DRY)).join(''),
  // Карасі в кошику — кошик із золотими карасями
  10: (id) => sky(id, C.sky) + river(30, 28, 10) + fill('#86a35a', 58, 44) + basket(26, 86, 50, '#c8955a')
    + [36, 48, 60, 70].map((x, i) => ic('fish', x, 58 + (i % 2) * 3, 16, { fill: '#e2b23a', detail: '#7a5400' })).join('') + willow(86, 58, 1.1),
  // Вудильник — хлопчик із вудкою на ставку
  11: (id) => sky(id, C.sky) + fill('#86a35a', 48, 54) + river(56, 26, 11) + willow(16, 56, 1.3)
    + person(40, 72, 0.9, { hat: 'brylj', arms: 'work', pants: '#5a3a1a', hold: `<path d="M8 -20q20-20 40-14" stroke="${C.woodD}" stroke-width="1.4" fill="none"/>` })
    + `<path d="M76 48v18" stroke="#f6efdc" stroke-width=".6"/><circle cx="76" cy="66" r="1.6" fill="${C.redL}"/><path d="M70 68q6-2 12 0" stroke="${C.waterL}" stroke-width="1" fill="none"/>`,
  // В'ялена риба — сушильня на сонці
  12: (id) => sky(id, ['#f6d27a', '#fbeccc']) + sun(18, 16, 9, '#ffd27a') + fill('#d9b46a', 70, 32)
    + `<path d="M34 24v50M90 24v50" stroke="${C.woodD}" stroke-width="2.2"/><path d="M32 26h60M32 44h60" stroke="${C.wood}" stroke-width="1.8"/>`
    + [40, 50, 60, 70, 80].map((x) => hangFish(x, 27, 13, DRY)).join('') + [44, 56, 68, 80].map((x) => hangFish(x, 45, 13, { fill: '#b88a50', detail: '#4a2e1a' })).join(''),
  // Раки — відро з раками на березі
  13: (id) => sky(id, C.eve) + river(40, 24, 13) + fill('#a7804f', 60, 42)
    + `<path d="M34 56h36l-4 30h-28z" fill="#8a8a8a"/><ellipse cx="52" cy="56" rx="18" ry="3" fill="#5a5a5a"/><path d="M34 56q18-18 36 0" stroke="#6a6a6a" stroke-width="1.4" fill="none"/>`
    + crayfish(48, 54, 1.1, -20) + crayfish(58, 53, 1, 15) + crayfish(78, 82, 1, 30),
  // Сітки й верші — сітка сушиться, верші на березі
  14: (id) => sky(id, C.sky) + river(48, 20, 14) + fill('#c9a46a', 66, 36) + `<path d="M28 14v54M90 14v54" stroke="${C.woodD}" stroke-width="2.2"/><path d="M28 16q31 12 62 0" stroke="${C.woodD}" stroke-width="1.2" fill="none"/>`
    + Array.from({ length: 8 }, (_, i) => `<path d="M${32 + i * 7.5} ${r2(18 + Math.sin(i / 7 * Math.PI) * 5)}v36" stroke="#d9c49a" stroke-width=".7"/>`).join('') + Array.from({ length: 6 }, (_, i) => `<path d="M30 ${26 + i * 6}q30 6 58 0" stroke="#d9c49a" stroke-width=".7" fill="none"/>`).join('')
    + verša(34, 80, 1.1) + verša(62, 86, 1),
  // Юшка в казанку — казанок на вогні біля ріки
  15: (id) => sky(id, C.dusk) + river(46, 22, 15) + fill('#4a5a3a', 64, 38) + `<path d="M40 30l14 44M80 30l-14 44" stroke="${C.woodD}" stroke-width="2.2"/><path d="M48 32h26" stroke="${C.woodD}" stroke-width="1.6"/>`
    + `<path d="M60 32v10" stroke="#5a5a5a" stroke-width="1"/><path d="M48 42h24l-3 14h-18z" fill="#2a2a2a"/><path d="M50 40q10-8 20 0" stroke="#cfc6c0" stroke-width="2" fill="none" opacity=".6"/>` + fire(60, 74, 1.1)
    + person(84, 90, 0.9, { hat: 'brylj', arms: 'work' }),
  // ── зілля
  // Травниця — збирає трави в кошик на луці
  16: (id) => sky(id, C.sky) + sun(82, 12, 6) + fill('#86a35a', 52, 50) + flowers(16, 54, 98, 14) + grass(16, 102, 16, '#5aa070', 12)
    + person(48, 82, 1.1, { w: 1, hat: 'vinok', arms: 'work', skirt: C.red, hold: basket(4, -14, 14) }) + tree(14, 54, 1.2, '#4a7a3a'),
  // Пучки м'яти — пучки на прилавку
  17: (id) => sky(id, C.sky) + floor(80, '#c9a46a', '#a7804f') + yatka(12, 80, 84, C.green, { h: 56 }) + rope(16, 92, 30, 2)
    + [24, 36, 48, 60, 72, 84].map((x, i) => bunch(x, 31, 0.75, i % 2 ? '#5aa070' : '#7ac08a')).join(''),
  // Ромашка — поле ромашок
  18: (id) => sky(id, C.sky) + sun(18, 14, 7) + hills(42, C.grassL, 4, 18) + fill('#7aa34a', 50, 52)
    + Array.from({ length: 34 }, (_, i) => chamomile(r2((i * 37) % 100), r2(52 + ((i * 53) % 48)), 1.8 + ((i * 7) % 5) * 0.4)).join(''),
  // Ятка насіння — мішечки з насінням, соняшник
  19: (id) => sky(id, C.sky) + floor(80, '#c9a46a', '#a7804f') + yatka(10, 80, 86, C.yellow, { h: 52 })
    + sack(32, 66, 1.3, '#d9c49a') + sack(52, 66, 1.2, '#c8a87a') + sack(72, 66, 1.3, '#e6d9b8') + `<ellipse cx="32" cy="46" rx="6" ry="1.6" fill="#3a2a1a"/><ellipse cx="72" cy="46" rx="6" ry="1.6" fill="#e8c97a"/>` + sunflower(90, 48, 0.9),
  // Часник і цибуля — вінки на стіні
  20: () => wall(84, '#e9dcc0') + `<path d="M8 10h84" stroke="${C.wood}" stroke-width="2.4"/>` + garlic(22, 12, 1) + garlic(40, 12, 1, '#c8955a') + garlic(58, 12, 1) + garlic(76, 12, 1, '#c8955a')
    + `<rect x="26" y="66" width="70" height="4" fill="${C.wood}"/>` + basket(60, 66, 22),
  // Деревій — білі щитки деревію в полі
  21: (id) => sky(id, C.sky) + fill('#86a35a', 50, 52) + Array.from({ length: 12 }, (_, i) => yarrow(r2(8 + i * 8), r2(70 + (i % 3) * 8), 1.3 + (i % 2) * 0.3)).join('') + bunch(78, 18, 0.9, '#5a8a3a') + `<path d="M70 18h16" stroke="${C.woodD}" stroke-width="1.6"/>`,
  // Віночки — вінки на ятці
  22: (id) => sky(id, C.sky) + floor(80, '#c9a46a', '#a7804f') + yatka(8, 80, 88, '#e8a0a0', { h: 56 }) + rope(12, 92, 32, 2)
    + [26, 46, 66, 86].map((x) => vinok(x, 40, 7, true)).join(''),
  // Квітчаний ряд — букети на прилавку
  23: (id) => sky(id, C.sky) + floor(80, '#c9a46a', '#a7804f') + yatka(10, 80, 86, C.redL, { h: 56 })
    + [28, 46, 64, 82].map((x, i) => `<path d="M${x - 4} 62h8l-1-8h-6z" fill="#c8955a"/>` + flowers(23 + i, 34, 54, 12).replace(/cx="([\d.]+)"/g, (m, v) => `cx="${r2(x - 7 + (+v % 14))}"`)).join(''),
  // ── глина
  // Глечики — глечики на кілках тину
  24: (id) => sky(id, C.sky) + hata(60, 56, 36, { win: 1 }) + fill('#86a35a', 56, 46) + tyn(0, 100, 74, 14)
    + [8, 26, 44, 62, 80].map((x, i) => `<rect x="${x}" y="42" width="2" height="18" fill="${C.woodD}"/>` + at(x + 1, 44, 1, `<g transform="rotate(180)">${jug(0, 0, 16, i % 2 ? C.clayL : C.clay)}</g>`)).join(''),
  // Макітри — макітри стосами
  25: () => wall(82, '#efe2c6') + `<rect x="10" y="70" width="86" height="4" fill="${C.wood}"/>`
    + [0, 1, 2].map((k) => makitra(34, 70 - k * 9, 24 - k * 2)).join('') + [0, 1].map((k) => makitra(66, 70 - k * 9, 26 - k * 2, C.clay)).join('') + makitra(88, 70, 14, '#a8431f'),
  // Свищики-півники — глиняні півники-свистки
  26: () => wall(82, '#f3eadb') + `<rect x="10" y="62" width="86" height="4" fill="${C.wood}"/>` + rooster(32, 62, 1.5) + rooster(56, 62, 1.7, '#f2c94c') + rooster(80, 62, 1.4, '#e8a0a0')
    + `<path d="M58 30q4-2 6 0M62 26q4-2 6 0" stroke="${C.wood}" stroke-width="1" fill="none"/>`,
  // Миски — стос мисок
  27: () => wall(82, '#efe2c6') + `<rect x="14" y="70" width="82" height="4" fill="${C.wood}"/>` + [0, 1, 2, 3].map((k) => bowl(44, 70 - k * 5, 30, '#f2e2c0', [C.red, C.green, C.blue, C.red][k])).join('')
    + bowl(78, 70, 22, C.clayL, '#f6efdc') + bowl(78, 63, 20, '#f2e2c0', C.green),
  // Глиняні іграшки — іграшки на рядні
  28: (id) => sky(id, C.sky) + fill('#86a35a', 50, 52) + kilim(18, 58, 78, 24, ['#e8d6a8', C.red, C.green])
    + rooster(36, 64, 1, '#f2c94c') + at(56, 64, 1, `<ellipse cx="0" cy="-5" rx="6" ry="3.6" fill="#f6efdc"/><path d="M-4 -2v3M3 -2v3" stroke="#f6efdc" stroke-width="2"/><circle cx="6" cy="-8" r="2.6" fill="#f6efdc"/><circle cx="-2" cy="-5" r="1" fill="${C.red}"/>`) + rooster(76, 64, 0.9, '#9ab5cc'),
  // Горнята — горнята рядком
  29: () => wall(82, '#efe2c6') + shelf(10, 34, 84) + shelf(10, 60, 84)
    + [18, 30, 42, 54, 66, 78, 88].map((x, i) => cup(x, 34, 1.2, i % 2 ? C.clayL : C.clay)).join('') + [24, 40, 56, 72, 88].map((x, i) => cup(x, 60, 1.3, i % 2 ? '#a8431f' : C.clayL)).join(''),
  // Ринки й покришки — горщики з покришками
  30: () => wall(82, '#e9dcc0') + `<rect x="12" y="72" width="84" height="4" fill="${C.wood}"/>` + potLid(34, 72, 1.5) + potLid(62, 72, 1.3, C.clayL) + potLid(86, 72, 1) + `<ellipse cx="62" cy="34" rx="9" ry="2" fill="#8a3a1e"/>`,
  // Куманці — кільцеві куманці
  31: () => wall(82, '#f3eadb') + `<rect x="12" y="68" width="84" height="4" fill="${C.wood}"/>` + kumanets(40, 68, 1.6) + kumanets(74, 68, 1.3) + shelf(12, 24, 84) + jug(30, 24, 12) + jug(80, 24, 12, C.clayL),
  // ── вовна
  // Вівчар — вівчар із ґирлигою, вівці
  32: (id) => sky(id, C.sky) + hills(50, C.grassL, 6, 32) + fill('#86a35a', 60, 42)
    + sheep(56, 74, 1) + sheep(74, 80, 1.1, '#ece4d2') + sheep(88, 72, 0.8)
    + person(36, 84, 1.15, { hat: 'kapelyukh', coat: '#5a3a1a', arms: 'work', hold: `<path d="M8 -20v24M8 -20q0-5 4-5" stroke="${C.woodD}" stroke-width="1.8" fill="none"/>` }),
  // Мішок вовни — мішок із вовною, ваги
  33: () => wall(82, '#e2d0ae') + sack(40, 82, 2.4, '#d9c49a') + `<ellipse cx="40" cy="38" rx="13" ry="5" fill="#f1ece0"/><circle cx="34" cy="36" r="4" fill="#f1ece0"/><circle cx="44" cy="35" r="4.6" fill="#f1ece0"/>`
    + `<path d="M78 30v40M66 30h24" stroke="${C.woodD}" stroke-width="2"/><path d="M66 30l-4 10h8zM90 30l-4 10h8z" fill="${C.gold}"/><circle cx="66" cy="38" r="2.6" fill="#f1ece0"/>`,
  // Смушкова шапка — шапка на підставці
  34: () => wall(82, '#e9dcc0') + `<rect x="46" y="44" width="4" height="38" fill="${C.woodD}"/><rect x="38" y="80" width="20" height="3" fill="${C.wood}"/>` + furHat(48, 46, 2) + `<rect x="66" y="66" width="28" height="4" fill="${C.wood}"/>` + furHat(80, 66, 1.1),
  // Панчохи — вовняні шкарпетки на мотузці
  35: (id) => sky(id, C.sky) + fill('#86a35a', 70, 32) + `<path d="M10 14v60M92 14v60" stroke="${C.woodD}" stroke-width="2.2"/>` + rope(10, 92, 18, 4)
    + [22, 36, 50, 64, 78].map((x, i) => sock(x, 20 + Math.sin(i / 4 * Math.PI) * 3, [C.red, '#f1ece0', '#3a3330', C.blue, C.red][i], 1.4)).join(''),
  // Пастух з ягням — хлопчик тримає ягня
  36: (id) => sky(id, C.sky) + hills(52, C.grassL, 5, 36) + fill('#86a35a', 62, 40) + tree(86, 62, 1.3)
    + person(48, 88, 1.25, { hat: 'brylj', arms: 'work', pants: '#5a3a1a' }) + sheep(56, 64, 0.85, '#f8f4ea') + sheep(18, 86, 0.9),
  // Ліжник — гуцульський ліжник на лаві
  37: () => wall(80, '#efe2c6') + `<rect x="12" y="64" width="84" height="5" fill="${C.wood}"/><path d="M18 69v12M90 69v12" stroke="${C.woodD}" stroke-width="2.4"/>`
    + `<path d="M20 20h70v50h-70z" fill="#f1ece0"/><path d="M20 30h70M20 40h70M20 50h70M20 60h70" stroke="${C.red}" stroke-width="2.4"/><path d="M20 34h70M20 54h70" stroke="${C.black}" stroke-width="1.4"/>`
    + Array.from({ length: 10 }, (_, i) => `<path d="M${22 + i * 7} 70v4" stroke="#e6dcc8" stroke-width="1.4"/>`).join(''),
  // Валянки — пара валянок
  38: () => wall(82, '#e9dcc0') + `<rect x="14" y="80" width="82" height="3" fill="${C.wood}"/>` + boot(42, 80, '#5a5048', 1.5) + boot(66, 80, '#6a6058', 1.5) + `<path d="M30 30h50" stroke="${C.wood}" stroke-width="2"/>` + sock(50, 32, C.red, 1) + sock(64, 32, '#f1ece0', 1),
  // Кептарик — вишитий кептарик
  39: () => wall(82, '#efe2c6') + `<path d="M54 12v8" stroke="${C.woodD}" stroke-width="1.6"/><path d="M42 20h24" stroke="${C.wood}" stroke-width="2.4"/>` + keptar(54, 20, 1.6),
};
