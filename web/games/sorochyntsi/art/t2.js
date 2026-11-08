// Сцени крамниць рівня II («Майстерні», id 40–69). Див. art/lib.js — поле 100 × 102, головне — у центрі й угорі.
import {
  C, at, ic, sky, fill, sun, hills, floor, wall, river, grass, flowers, hata, yatka, tree, poplar, willow, tyn,
  jug, makitra, bowl, shelf, basket, rushnyk, rope, shirt, svyta, kilim, cart, ox, sheep, boat, fire, pich,
  mountains, sunflower, pumpkin, bottle, bunch, person,
} from './lib.js';

const hoop = (x, y, r = 6) => `<circle cx="${x}" cy="${y}" r="${r}" fill="${C.linen}" stroke="${C.wood}" stroke-width="1.6"/><path d="M${x - 3} ${y}l1.5-1.5 1.5 1.5 1.5-1.5 1.5 1.5" stroke="${C.red}" stroke-width="1" fill="none"/>`;
const r2 = (n) => Math.round(n * 100) / 100;
function wheel(x, y, r = 14) {
  let o = `<circle cx="${x}" cy="${y}" r="${r}" fill="none" stroke="${C.woodD}" stroke-width="2.4"/>`;
  for (const a of [0, 45, 90, 135]) { const c = Math.cos(a * Math.PI / 180) * r, s = Math.sin(a * Math.PI / 180) * r; o += `<path d="M${r2(x + c)} ${r2(y + s)}L${r2(x - c)} ${r2(y - s)}" stroke="${C.woodD}" stroke-width="1.4"/>`; }
  for (let a = 0; a < 360; a += 30) { const px = r2(x + (r + 1.5) * Math.cos(a * Math.PI / 180)), py = r2(y + (r + 1.5) * Math.sin(a * Math.PI / 180)); o += `<rect x="${r2(px - 2)}" y="${r2(py - 1.5)}" width="4" height="3" fill="${C.wood}" transform="rotate(${a} ${px} ${py})"/>`; }
  return o;
}
const smoke = (x, y) => `<path d="M${x} ${y}q4-6 0-12t2-12" stroke="#cfc6c0" stroke-width="3" fill="none" opacity=".6"/>`;
const hive = (x, y, s = 1) => at(x, y, s, `<path d="M-7 0v-9q0-8 7-8t7 8v9z" fill="#d9a85a"/><path d="M-7 -5h14M-7 -10h14" stroke="#a8763a" stroke-width="1"/><rect x="-1.6" y="-3" width="3.2" height="2.4" fill="${C.woodD}"/>`);
const bee = (x, y) => `<ellipse cx="${x}" cy="${y}" rx="1.6" ry="1.1" fill="${C.yellow}"/><path d="M${x - 0.4} ${y - 1}v2" stroke="${C.black}" stroke-width=".6"/><ellipse cx="${x}" cy="${y - 1.4}" rx="1" ry=".7" fill="#fff" opacity=".8"/>`;
const tile = (x, y, s = 6, c = C.blue) => `<rect x="${x}" y="${y}" width="${s}" height="${s}" fill="#fbf3e1" stroke="${c}" stroke-width=".7"/><path d="M${r2(x + s / 2)} ${y + 1}l${r2(s / 2 - 1)} ${r2(s / 2 - 1)}-${r2(s / 2 - 1)} ${r2(s / 2 - 1)}-${r2(s / 2 - 1)}-${r2(s / 2 - 1)}z" fill="${c}"/>`;
const toy = (x, y, s = 1, c = '#f6efdc') => at(x, y, s, `<ellipse cx="0" cy="-5" rx="6" ry="3.6" fill="${c}"/><path d="M-4 -2v3M3 -2v3" stroke="${c}" stroke-width="2"/><circle cx="6" cy="-8" r="2.6" fill="${c}"/><path d="M-3 -6q3-2 6 0M5 -10l1-2" stroke="${C.red}" stroke-width="1" fill="none"/><circle cx="-2" cy="-5" r="1" fill="${C.green}"/>`);
const hat = (x, y, s = 1, c = C.black) => at(x, y, s, `<ellipse cx="0" cy="0" rx="9" ry="2" fill="${c}"/><path d="M-5 0v-6q5-3 10 0v6z" fill="${c}"/><path d="M-5 -1.6h10" stroke="${C.red}" stroke-width="1"/>`);
/** Рибина вертикально (висить на гачку): x, y — верх. */
const hangFish = (x, y, s, o) => `<g transform="translate(${x},${y}) rotate(90)">${ic('fish', s / 2, 0, s, o)}</g>`;

export const S2 = {
  // Ткацький верстат — рама з основою, ткаля, полотно з узором
  40: () => wall(82, '#efe2c6') + `<rect x="34" y="14" width="3" height="70" fill="${C.wood}"/><rect x="88" y="14" width="3" height="70" fill="${C.wood}"/><rect x="32" y="14" width="61" height="3.4" fill="${C.woodD}"/>`
    + Array.from({ length: 16 }, (_, i) => `<path d="M${39 + i * 3.1} 17v28" stroke="#d9cdb8" stroke-width=".7"/>`).join('')
    + `<rect x="38" y="45" width="49" height="28" fill="${C.linen}"/>` + [50, 56, 62].map((y) => `<path d="M40 ${y}${'l2-2 2 2'.repeat(11)}" stroke="${C.red}" stroke-width="1" fill="none"/>`).join('')
    + `<rect x="34" y="73" width="57" height="3" fill="${C.woodD}"/>` + person(22, 86, 1, { w: 1, hat: 'ochipok', arms: 'work', skirt: '#2a5a9a' }),
  // Вибійка — дошки-штампи, полотно з набивним візерунком
  41: () => wall(80, '#e9dcc0') + `<rect x="26" y="8" width="66" height="44" fill="#f3eadb"/>`
    + Array.from({ length: 12 }, (_, i) => { const x = 32 + (i % 6) * 11, y = 18 + Math.floor(i / 6) * 20; return `<circle cx="${x}" cy="${y}" r="4" fill="none" stroke="#2a5a9a" stroke-width="1.6"/><circle cx="${x}" cy="${y}" r="1.4" fill="#2a5a9a"/><path d="M${x + 5.5} ${y + 10}l1.5-1.5 1.5 1.5" stroke="#2a5a9a" stroke-width="1" fill="none"/>`; }).join('')
    + `<rect x="24" y="62" width="70" height="4" fill="${C.wood}"/>` + [40, 60, 78].map((x) => `<rect x="${x - 6}" y="54" width="12" height="8" rx="1" fill="${C.woodD}"/><rect x="${x - 2}" y="48" width="4" height="6" fill="${C.wood}"/>`).join('')
    + `<rect x="30" y="58" width="6" height="4" fill="#2a5a9a"/>`,
  // Сорочечниця — шиє сорочку біля вікна
  42: () => wall(80) + `<rect x="56" y="12" width="30" height="28" fill="#7fb2df" stroke="${C.wood}" stroke-width="2.4"/><path d="M71 12v28M56 26h30" stroke="${C.wood}" stroke-width="1.8"/>` + rushnyk(52, 9, 38, 6)
    + `<rect x="26" y="66" width="44" height="4" fill="${C.wood}"/>` + person(46, 82, 1.05, { w: 1, hat: 'khustka', hatC: C.red, arms: 'work', skirt: C.black, apron: '#2a5a9a' })
    + shirt(72, 46, 0.95),
  // Кролевецькі рушники — червоно-білі рушники на стіні
  43: () => wall(84, '#f3eadb') + `<rect x="6" y="8" width="88" height="2.4" fill="${C.wood}"/>`
    + [8, 38, 68].map((x, i) => rushnyk(x, 10, 24, 58) + `<rect x="${x + 2}" y="${16 + i * 2}" width="20" height="10" fill="${C.red}"/><path d="M${x + 4} ${21 + i * 2}l4-4 4 4 4-4 4 4" stroke="#fbf6ea" stroke-width="1.2" fill="none"/>`).join(''),
  // Полотняний віз — віз із сувоями на шляху
  44: (id) => sky(id, C.sky) + sun(16, 14, 7) + hills(52, C.grassL, 5, 44) + fill('#c9a46a', 64, 38) + poplar(80, 64, 1.1) + poplar(92, 62, 0.9)
    + ox(30, 74, 1.05) + cart(60, 76, 1.2, `<g>${[-12, -4, 4, 12].map((x) => `<rect x="${x - 3.6}" y="-26" width="7.2" height="14" rx="3" fill="${C.linen}"/><path d="M${x - 3.6} -19h7.2" stroke="${C.red}" stroke-width="1"/>`).join('')}<rect x="-8" y="-33" width="16" height="7" rx="3" fill="#fbf6ea"/></g>`),
  // Вишивальниці — дві дівчини з п'яльцями на лаві
  45: (id) => sky(id, C.sky) + fill('#86a35a', 60, 42) + tree(88, 62, 1.6, '#4a7a3a') + hata(4, 60, 34, { win: 1 })
    + `<rect x="26" y="70" width="58" height="4" fill="${C.wood}"/><path d="M30 74v8M80 74v8" stroke="${C.woodD}" stroke-width="2"/>`
    + person(40, 82, 0.95, { w: 1, hat: 'vinok', arms: 'work', skirt: C.red }) + hoop(50, 62, 5)
    + person(68, 82, 0.95, { w: 1, hat: 'vinok', arms: 'work', skirt: '#2a5a9a', braid: 1 }) + hoop(78, 62, 5),
  // Рибна лава — лава з рибою, рибалка
  46: (id) => sky(id, C.sky) + river(46, 20, 46) + fill('#c9a46a', 66, 36) + `<rect x="26" y="62" width="70" height="4.4" fill="${C.wood}"/><path d="M30 66v14M92 66v14" stroke="${C.woodD}" stroke-width="2.4"/>`
    + [36, 50, 64, 78].map((x, i) => ic('fish', x, 57, 16, i % 2 ? { fill: '#9ab5cc', detail: '#3c5670' } : {})).join('')
    + person(16, 84, 1.05, { hat: 'brylj', arms: 'down', pants: '#5a3a1a' }) + basket(70, 92, 20),
  // Чумацька тараня — віз із в'язками тарані, чумак
  47: (id) => sky(id, ['#f2b06c', '#fbe6c0']) + fill('#d9b46a', 60, 42) + `<path d="M0 60h100" stroke="#b8904a" stroke-width="1.5"/>`
    + `<path d="M40 40h46" stroke="${C.woodD}" stroke-width="1.6"/><path d="M42 40v16M84 40v16" stroke="${C.woodD}" stroke-width="1.4"/>` + [48, 56, 64, 72, 80].map((x) => hangFish(x, 41, 11, { fill: '#c0a070', detail: '#5a3a1a' })).join('')
    + cart(62, 80, 1.3, `<path d="M-16 -14q16 -8 32 0z" fill="#f3f0e8"/>`)
    + person(22, 84, 1.05, { hat: 'brylj', pants: '#f3ead7', arms: 'work', hold: `<path d="M7 -21l14 -18" stroke="${C.woodL}" stroke-width="1.6"/>` }),
  // Ставок сотника — ставок, млинове колесо, верба, качки
  48: (id) => sky(id, C.sky) + fill('#86a35a', 44, 58) + river(54, 30, 48) + `<rect x="62" y="22" width="30" height="34" fill="#a7744a"/><path d="M58 24l19-12 19 12z" fill="${C.thatch}"/>`
    + wheel(60, 50, 13) + willow(18, 56, 1.4)
    + at(40, 66, 1, `<ellipse cx="0" cy="0" rx="4" ry="2.2" fill="#f6efdc"/><circle cx="3.6" cy="-2" r="1.6" fill="#3f6b38"/><path d="M5 -2h2" stroke="${C.yellow}" stroke-width="1"/>`)
    + at(50, 72, 0.8, `<ellipse cx="0" cy="0" rx="4" ry="2.2" fill="#f6efdc"/><circle cx="3.6" cy="-2" r="1.6" fill="#f6efdc"/><path d="M5 -2h2" stroke="${C.yellow}" stroke-width="1"/>`),
  // Коптильня — хатинка з димом, риба на жердинах
  49: (id) => sky(id, C.sky) + fill('#86a35a', 70, 32) + `<rect x="54" y="34" width="34" height="36" fill="#8a6a4a"/><path d="M50 36l21-16 21 16z" fill="#5a4a3a"/><rect x="66" y="50" width="10" height="20" fill="${C.woodD}"/>` + smoke(74, 18) + smoke(64, 22)
    + `<path d="M24 30v40M48 30v40" stroke="${C.woodD}" stroke-width="2"/><path d="M22 32h28M22 48h28" stroke="${C.wood}" stroke-width="1.6"/>`
    + [28, 36, 44].map((x) => hangFish(x, 33, 11, { fill: '#b8864a', detail: '#4a2e1a' }) + hangFish(x, 49, 11, { fill: '#c09a5a', detail: '#4a2e1a' })).join(''),
  // Рибальська артіль — гурт тягне невід
  50: (id) => sky(id, C.eve) + sun(78, 20, 9) + river(40, 34, 50) + fill('#c9a46a', 74, 28)
    + `<path d="M20 74q30-34 76-20" stroke="${C.linen}" stroke-width="1.2" stroke-dasharray="2 1.6" fill="none"/>` + Array.from({ length: 6 }, (_, i) => `<circle cx="${40 + i * 9}" cy="${r2(58 - i * 1.6)}" r="1.6" fill="#e8c97a"/>`).join('')
    + person(30, 82, 0.95, { hat: 'brylj', arms: 'work', pants: '#5a3a1a' }) + person(46, 84, 0.95, { hat: 'shapka', arms: 'work' }) + person(62, 86, 0.95, { hat: 'none', arms: 'work', pants: '#2c3e66' }),
  // Осетер із Дніпра — велетенський осетер у човні
  51: (id) => sky(id, C.sky) + `<path d="M70 40l8-16 22 2V40z" fill="#b98a5a"/>` + river(40, 62, 51) + boat(48, 62, 1.6)
    + `<path d="M24 56c10-10 32-12 48-4l10-6-2 8 2 8-10-6c-16 8-38 6-48-2z" fill="#6a7a86"/><path d="M34 52l2 4M42 50l2 4M50 50l2 4M58 51l2 4" stroke="#d9dde2" stroke-width="1.2"/><circle cx="30" cy="54" r="1.2" fill="${C.black}"/>`
    + person(80, 62, 0.8, { hat: 'shapka', arms: 'up' }),
  // Знахарка — бабця з пучками, казанок на вогні
  52: () => wall(82, '#e2d0ae') + `<rect x="10" y="8" width="84" height="2" fill="${C.wood}"/>` + [18, 30, 42, 54, 66, 78, 90].map((x, i) => bunch(x, 10, 0.7, [C.green, '#7aa34a', '#9a8a3a', C.green, '#5a8a3a', '#7aa34a', C.green][i])).join('')
    + person(34, 86, 1.05, { w: 1, hat: 'khustka', hatC: '#5a3a1a', skirt: '#3a2a1a', apron: '#6a5a3a', arms: 'work', shirt: '#e8dcc8' })
    + fire(72, 88, 0.9) + `<path d="M62 62h20l-2 10h-16z" fill="#2a2a2a"/><path d="M66 60q6-6 12 0" stroke="#5a5a5a" stroke-width="1.2" fill="none"/><path d="M70 58q2-4 0-8" stroke="#cfe0c0" stroke-width="2" fill="none" opacity=".6"/>`,
  // Сушарня трав — горище, крокви, багато пучків
  53: () => `<rect width="100" height="102" fill="#6e4523"/><path d="M0 40L50 4 100 40" stroke="${C.woodD}" stroke-width="4" fill="none"/><path d="M50 4v98" stroke="${C.woodD}" stroke-width="3"/>`
    + `<path d="M8 50h84M14 30h72" stroke="${C.wood}" stroke-width="2.4"/>`
    + [20, 32, 44, 56, 68, 80].map((x, i) => bunch(x, 30, 0.65, i % 2 ? '#7aa34a' : C.green)).join('') + [14, 26, 38, 62, 74, 86].map((x, i) => bunch(x, 50, 0.75, ['#9a8a3a', C.green, '#5a8a3a', '#7aa34a', C.green, '#9a8a3a'][i])).join('')
    + `<path d="M80 18l12-10" stroke="#ffcf6b" stroke-width="6" opacity=".3"/>`,
  // Травник-аптекар — полиці з пляшечками, ступка
  54: () => wall(80, '#e9dcc0') + shelf(10, 22, 82, C.woodD) + shelf(10, 46, 82, C.woodD)
    + [16, 26, 36, 46, 56, 66, 76, 86].map((x, i) => bottle(x, 22, 0.9, ['#5aa070', '#7a4a9a', '#c0a050', '#4a7ab0', '#a0503a', '#5aa070', '#7a4a9a', '#c0a050'][i])).join('')
    + [20, 34, 48, 62, 76, 88].map((x, i) => `<rect x="${x - 4}" y="37" width="8" height="9" rx="1.5" fill="${i % 2 ? '#d9c49a' : '#c8a87a'}"/><rect x="${x - 4.6}" y="35" width="9.2" height="2.6" fill="${C.wood}"/>`).join('')
    + `<rect x="30" y="66" width="60" height="4" fill="${C.wood}"/><path d="M48 66l2-8h12l2 8z" fill="#8a8a8a"/><path d="M58 58l8-10" stroke="${C.woodD}" stroke-width="2.4" stroke-linecap="round"/>` + bunch(80, 56, 0.5, C.green),
  // Город попаді — соняшники, гарбузи, тин, хата
  55: (id) => sky(id, C.sky) + hata(54, 50, 40, { win: 1 }) + fill('#6b8a3e', 50, 52) + tyn(0, 100, 62, 10)
    + sunflower(30, 64, 1.3) + sunflower(42, 66, 1.1) + sunflower(18, 66, 1.15) + pumpkin(64, 82, 1.2) + pumpkin(82, 86, 1) + flowers(55, 70, 98, 6),
  // Медова м'ята — вулики, м'ята, бджоли
  56: (id) => sky(id, C.sky) + sun(84, 14, 7) + fill('#86a35a', 52, 50) + tree(16, 54, 1.5, '#4a7a3a')
    + hive(44, 64, 1.4) + hive(68, 60, 1.2) + hive(88, 66, 1.3) + grass(56, 102, 14, '#5aa070', 14)
    + bee(52, 32) + bee(60, 40) + bee(76, 30) + bee(36, 40) + `<path d="M50 34q4-4 8 2M58 42q6 2 6-4" stroke="${C.black}" stroke-width=".5" stroke-dasharray="1 1" fill="none"/>`,
  // Зелений ряд — ятки з зеленню
  57: (id) => sky(id, C.sky) + floor(76, '#c9a46a', '#a7804f') + yatka(28, 76, 68, C.green, { h: 54 })
    + [36, 46, 56, 66, 76, 86].map((x, i) => bunch(x, 30, 0.6, i % 2 ? '#7aa34a' : C.green)).join('')
    + [38, 50, 62, 74, 86].map((x, i) => `<ellipse cx="${x}" cy="56" rx="5" ry="3.4" fill="${['#5aa070', '#7aa34a', '#e08a2e', '#5aa070', '#d64541'][i]}"/>`).join('')
    + person(14, 80, 1.05, { w: 1, hat: 'khustka', hatC: C.green, arms: 'work' }),
  // Гончарний круг — майстерня, круг із глечиком, полиці, жар печі
  58: () => `<rect width="100" height="102" fill="#e9d6b4"/><path d="M70 102V44a15 15 0 0 1 30 0v58z" fill="#7a4f2c"/><path d="M76 102V52a9 9 0 0 1 18 0v50z" fill="#ffb347"/><path d="M78 102V60a7 7 0 0 1 14 0v42z" fill="#ff7a2f" opacity=".8"/>`
    + shelf(4, 14, 56) + shelf(4, 38, 56) + jug(13, 14, 15) + jug(30, 14, 13, C.clayL) + jug(47, 14, 15, '#a8431f', '#f6efdc') + jug(16, 38, 13, C.clayL) + jug(36, 38, 15) + jug(52, 38, 12, '#a8431f', '#f6efdc')
    + `<rect y="84" width="100" height="18" fill="#8a5a35"/><path d="M22 84l6-18h12l6 18z" fill="#6e4523"/><ellipse cx="34" cy="64" rx="18" ry="4" fill="#5e3b21"/><ellipse cx="34" cy="62.5" rx="18" ry="4" fill="#9a6a40"/>`
    + `<path d="M27 62c-3-6-1-12 3-14v-4h8v4c4 2 6 8 3 14z" fill="#c4512f"/><path d="M28 56h12" stroke="#f6efdc" stroke-width="1.2"/><path d="M18 54c4 0 7 2 9 4M50 54c-4 0-7 2-9 4" stroke="#e8c4a0" stroke-width="4" stroke-linecap="round" fill="none"/>`,
  // Гончарна піч — піч із жаром, майстер із глечиком
  59: () => wall(84, '#dcc6a0') + pich(40, 86, 56, 62, { c: '#a8643a' }) + `<path d="M40 24q28-12 56 0" stroke="#7a4a2a" stroke-width="3" fill="none"/>` + smoke(68, 20)
    + person(22, 88, 1.05, { hat: 'brylj', arms: 'work', apron: false, pants: '#5a3a1a', hold: jug(10, -16, 10) }),
  // Розписані миски — миски з розписом на полиці й столі
  60: () => wall(82, '#efe2c6') + shelf(8, 26, 86) + [20, 40, 60, 80].map((x, i) => `<circle cx="${x}" cy="15" r="9" fill="#f2e2c0" stroke="${C.clayD}" stroke-width="1"/><circle cx="${x}" cy="15" r="5" fill="none" stroke="${[C.red, C.green, C.blue, C.red][i]}" stroke-width="1.6"/><circle cx="${x}" cy="15" r="1.8" fill="${[C.yellow, C.red, C.yellow, C.green][i]}"/>`).join('')
    + `<rect x="24" y="64" width="72" height="4" fill="${C.wood}"/>` + bowl(36, 64, 18, '#f2e2c0', C.green) + bowl(58, 64, 20) + bowl(82, 64, 17, '#f2e2c0', C.blue),
  // Кахлярня — стоси кахлів, сушильні полиці
  61: () => wall(82, '#e9dcc0') + shelf(8, 30, 84) + shelf(8, 56, 84)
    + Array.from({ length: 10 }, (_, i) => tile(12 + i * 8, 22, 7, i % 3 === 0 ? C.green : C.blue)).join('') + Array.from({ length: 10 }, (_, i) => tile(12 + i * 8, 48, 7, i % 2 ? C.blue : '#c4512f')).join('')
    + Array.from({ length: 4 }, (_, k) => tile(54, r2(88 - k * 7.5), 9, C.blue)).join('') + Array.from({ length: 3 }, (_, k) => tile(70, r2(88 - k * 7.5), 9, C.green)).join(''),
  // Глиняний ряд — ятка, повна посуду
  62: (id) => sky(id, C.sky) + floor(78, '#c9a46a', '#a7804f') + yatka(26, 78, 70, C.clay, { h: 56 })
    + [34, 46, 58, 70, 82, 92].map((x, i) => (i % 3 === 1 ? makitra(x, 56, 12) : jug(x, 56, 12 + (i % 2) * 2, i % 2 ? C.clayL : C.clay))).join('')
    + rope(30, 94, 30, 2) + [40, 56, 72, 88].map((x) => jug(x, 44, 9, '#a8431f', '#f6efdc')).join('')
    + person(14, 82, 1.05, { hat: 'shapka', arms: 'work', coat: '#5a3a1a' }),
  // Опішнянські майстри — розписні баранці й коники
  63: () => wall(82, '#f3eadb') + `<rect x="10" y="56" width="86" height="4.4" fill="${C.wood}"/>`
    + toy(28, 56, 1.4) + toy(52, 56, 1.6, '#f2c94c') + toy(76, 56, 1.4, '#e8a0a0') + toy(40, 34, 1, '#f6efdc') + toy(64, 34, 1, '#9ab5cc')
    + `<path d="M30 34h44" stroke="${C.wood}" stroke-width="2.4"/>` + jug(90, 56, 12),
  // Сукновальня — млинове колесо, сукно сушиться
  64: (id) => sky(id, C.sky) + fill('#86a35a', 44, 58) + river(66, 36, 64) + `<rect x="10" y="26" width="34" height="40" fill="#a7744a"/><path d="M6 28l21-14 21 14z" fill="${C.thatch}"/>` + wheel(48, 60, 14)
    + `<path d="M62 22v44M96 22v44" stroke="${C.woodD}" stroke-width="2"/><path d="M60 24h38" stroke="${C.woodD}" stroke-width="1.6"/><rect x="66" y="25" width="12" height="34" fill="#5a3a1a"/><rect x="80" y="25" width="12" height="30" fill="#8e2a24"/>`,
  // Кожухар — кожух на опудалі, майстер
  65: () => wall(82, '#e2d0ae') + `<rect x="60" y="20" width="2.4" height="64" fill="${C.woodD}"/>` + svyta(61, 22, 1.4, '#c8a06a', '#f3ead7')
    + `<path d="M53 22h16" stroke="#f3ead7" stroke-width="4"/>` + person(24, 86, 1.05, { hat: 'shapka', arms: 'work', coat: '#8a6a4a', pants: '#2c3e66' })
    + `<rect x="80" y="64" width="16" height="10" rx="3" fill="#e8dcc0"/><path d="M82 68h12M82 71h12" stroke="#c8b898" stroke-width="1"/>`,
  // Килимарня — верстат з напівтканим килимом
  66: () => wall(84, '#efe2c6') + `<rect x="18" y="8" width="3" height="76" fill="${C.wood}"/><rect x="82" y="8" width="3" height="76" fill="${C.wood}"/><rect x="16" y="8" width="71" height="3.4" fill="${C.woodD}"/>`
    + Array.from({ length: 20 }, (_, i) => `<path d="M${23 + i * 3} 11v24" stroke="#d9cdb8" stroke-width=".7"/>`).join('') + kilim(22, 35, 59, 40) + `<rect x="18" y="74" width="67" height="3" fill="${C.woodD}"/>`,
  // Отара з полонини — гірська стежка, вівці, пес
  67: (id) => sky(id, C.sky) + mountains(50, '#7f8faa', '#5f6f8a') + fill('#7a9a4e', 62, 40) + `<path d="M60 62q-10 14-30 40" stroke="#c9a46a" stroke-width="10" fill="none"/>`
    + sheep(56, 72, 0.9) + sheep(46, 80, 0.85, '#ece4d2') + sheep(64, 82, 0.9, '#d9cdb8') + sheep(52, 90, 0.95)
    + at(78, 84, 1, `<ellipse cx="0" cy="-5" rx="6" ry="3" fill="#f3ead7"/><circle cx="6" cy="-8" r="2.6" fill="#f3ead7"/><path d="M-4 -2v3M3 -2v3M-6 -6l-3-3" stroke="#f3ead7" stroke-width="1.6"/><circle cx="7" cy="-8.6" r=".6" fill="${C.black}"/>`) + tree(90, 64, 1, '#2f5f3a'),
  // Шаповал — капелюхи стосом
  68: () => wall(82, '#e9dcc0') + `<rect x="26" y="64" width="70" height="4.4" fill="${C.wood}"/>`
    + [0, 1, 2, 3, 4].map((k) => hat(44, 62 - k * 4, 1.1)).join('') + [0, 1, 2].map((k) => hat(70, 62 - k * 4, 1.1, '#5a3a1a')).join('') + hat(88, 62, 1, '#3a3a3a')
    + `<path d="M20 18h76" stroke="${C.wood}" stroke-width="2"/>` + [30, 50, 70, 90].map((x, i) => `<path d="M${x} 18v6" stroke="${C.woodD}" stroke-width="1"/>` + hat(x, 30, 0.9, i % 2 ? '#5a3a1a' : C.black)).join(''),
  // Свити на продаж — свити на мотузці в ятці
  69: (id) => sky(id, C.sky) + floor(80, '#c9a46a', '#a7804f') + yatka(26, 80, 70, C.wood, { h: 60, counter: '#8a5a35' })
    + rope(30, 94, 24, 3) + svyta(42, 25, 0.95, '#8e2a24', C.gold) + svyta(62, 27, 0.95, '#5a3a1a') + svyta(82, 25, 0.95, '#2c3e66')
    + person(14, 84, 1.05, { hat: 'shapka', shlyk: 1, arms: 'down', coat: '#8e2a24' }),
};
