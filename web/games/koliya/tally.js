// «Колія» — підсумок по гравцях для фінальної таблиці «хто що робив» (hud.js playersHtml). Чисте й детерміноване.
// Рахують самі правила (кінець step), тож у мережі цифри однакові в усіх і переживають відкат. До 28.10 рахував HUD з
// подій лише «свіжих» тиків: дії інших гравців, що доходили мережею із запізненням, губилися при перерахунку, а кожна
// перебудова світу (новий об'єкт стану) обнуляла лік — у таблиці були нулі й «🛋 Пасажир» у тих, хто працював.
// s.stats.pl[p] = { chop, rock, store, rail, douse, water, carry, throw, catch, pass, stun, hurt, honk, scare, idle, ticks }
//   chop/rock — удари; store — штук дров і заліза, складених у потяг; rail — рейки й містки; carry — взяв/поклав;
//   stun — бац колегу, hurt — бац отримав; idle/ticks — тики «стояв без діла» з тих, поки паротяг їде.
// stats у hash не йде (K6), тож hash і мережеві звірки від цього не змінюються.

const KEYS = 'chop rock store rail douse water carry throw catch pass stun hurt honk scare idle ticks'.split(' ');
const DIRECT = new Set(['chop', 'rock', 'rail', 'douse', 'water', 'throw', 'catch', 'pass', 'honk', 'scare']);

/** Рядок гравця p (створюється, коли треба: гравці приходять посеред партії) */
export function tallyRow(s, p) {
  const a = (s.stats.pl ??= []);
  while (a.length <= p) a.push(null);
  return (a[p] ??= Object.fromEntries(KEYS.map((k) => [k, 0])));
}

/** Кінець step: події тику → лічильники; хто не зрушив і нічого не зробив, поки паротяг їде, — «стояв» */
export function tallyStep(s, pre) {
  const acted = [];
  for (const e of s.ev) {
    const p = e.t === 'stun' ? e.by : e.p;
    if (p === undefined || p < 0 || p > 3) continue;
    const c = tallyRow(s, p);
    if (DIRECT.has(e.t)) c[e.t]++;
    else if (e.t === 'pick' || e.t === 'drop') c.carry++;
    else if (e.t === 'stun') { c.stun++; if (e.p !== undefined) tallyRow(s, e.p).hurt++; }
    else continue;
    acted[p] = 1;
  }
  if (s.status !== 'run' || !s.train.started) return;
  for (let i = 0; i < s.players.length; i++) {
    const pl = s.players[i];
    if (!pl || pl.out) continue;
    const c = tallyRow(s, i);
    c.ticks++;
    if (pl.x === pre[2 * i] && pl.y === pre[2 * i + 1] && !acted[i]) c.idle++;
  }
}
