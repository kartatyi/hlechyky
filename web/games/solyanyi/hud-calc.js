// «Соляний шлях» — чисті розрахунки картки столу (без DOM): позначки шляхів, «що зараз», оплата й порада до обраного
// шляху, рядки списку 🧭, поради Глека, звання, графік підсумку, контур мапи. Правил тут нема — законні дії рахує сервер
// (view.me.claims, view.me.can); тут лише як це показати людині. Тест: dev/hud-calc.test.mjs (node --test).

import {
  CARGO, CARDS, CARGO_NAME, CARGO_GEN, CARGO_ACC, ICON_CARGO, OX, WAGON_WORD, CARD_WORD, MAP,
  plural, route, routeName, city, ticket, ticketName, ROUTES, CITIES, LONGEST_BONUS, GREY, WILD,
} from './consts.js';

const clean = (s) => String(s == null ? '' : s).replace(/\s*🤖\s*/g, '').trim();
const pl = (n, W) => plural(n, W[0], W[1], W[2]);
export const wagons = (n) => n + ' ' + pl(n, WAGON_WORD);
export const cards = (n) => n + ' ' + pl(n, CARD_WORD);

/** Гравець за місцем. */
export const playerOf = (v, seat) => ((v && v.players) || []).find((p) => p.seat === seat) || null;
export const nickOf = (v, seat) => { const p = playerOf(v, seat); return p ? p.nick : 'місце ' + (seat + 1); };

/** Скільки всього карт у руці. */
export function handTotal(hand) {
  let n = 0;
  for (const k of CARDS) n += (hand && hand[k]) || 0;
  return n;
}

/** Позначки шляхів для sc.setMarks: можу зайняти / бракує 1 / на шляху мого замовлення. */
export function marks(v) {
  const me = v && v.me, out = {};
  if (!me) return out;
  for (const t of me.tickets || []) for (const id of t.path || []) out[id] = 'plan';
  const cl = me.claims || {};
  for (const id of Object.keys(cl)) {
    const c = cl[id];
    if (c.ok) out[id] = 'ok';
    else if (c.short === 1) out[id] = 'near';
  }
  return out;
}

/** Шляхи, які можна зайняти просто зараз (setLegal і курсор). */
export function legalRoutes(v) {
  const me = v && v.me;
  if (!me || me.must !== 'act') return [];
  const out = [];
  for (const id of Object.keys(me.claims || {})) if (me.claims[id].ok) out.push(+id);
  return out.sort((a, b) => a - b);
}

/** Кільця навколо міст моїх замовлень (sc.setTickets). */
export function ticketRings(v) {
  const me = v && v.me;
  return ((me && me.tickets) || []).map((t) => { const x = ticket(t.id); return { a: x.a, b: x.b, done: !!t.done, id: t.id }; });
}

/** Замовлення для показу. */
export function ticketLine(t) {
  const x = ticket(t.id), a = city(x.a).name, b = city(x.b).name;
  const left = t.done ? 'виконано' : t.dead || t.need == null ? 'шлях перекрито' : 'ще ' + t.need + ' 🛞';
  return { id: t.id, a, b, name: a + ' — ' + b, pts: x.pts, done: !!t.done, dead: !!t.dead || (!t.done && t.need == null), need: t.need, left, path: t.path || [], d: x.d };
}

/** Скільки зроблено з замовлення: d — найкоротше на порожній мапі, need — лишилось. Частка 0…1 для смужки. */
export function ticketProgress(t) {
  const x = ticket(t.id);
  if (t.done) return 1;
  if (t.need == null) return 0;
  return Math.max(0, Math.min(1, 1 - t.need / (x.d || 1)));
}

/** Ланцюг міст, якими йдуть вільні шляхи замовлення: «Кропивницький → Кривий Ріг → Херсон». */
export function pathCities(path) {
  if (!path || !path.length) return '';
  const names = new Set();
  const out = [];
  for (const id of path) {
    const r = route(id);
    for (const c of [r.a, r.b]) if (!names.has(c)) { names.add(c); out.push(city(c).name); }
  }
  return out.join(' → ');
}

/** Стрічка «моя сума»: шляхи · виконано · під ризиком. */
export function scoreLine(score) {
  if (!score) return '';
  return 'шляхи ' + score.routes + ' · виконано +' + score.done + ' · під ризиком −' + score.open;
}

/** Смуга «що зараз»: kicker (коло), t (заголовок), s (що робити), short (для телефона), mine, imp. Тексти — §12. */
export function whatNow(v, ctxInfo) {
  const me = (v && v.me) || null;
  if (!v || !v.phase) return { kicker: '', t: 'Стіл збирається', s: 'Чекаємо на початок…', short: 'Чекаємо…', mine: false };
  if (v.over || v.phase === 'over') {
    const w = (v.over && v.over.winners) || [];
    const t = w.length > 1 ? '🤝 Валку довели разом' : w.length ? 'Голова валки — ' + clean(nickOf(v, w[0])) : 'Партію скінчено';
    return { kicker: 'Кінець', t, s: 'Підсумок — на полі', short: t, mine: false };
  }
  const kicker = v.round ? 'Коло ' + v.round : '';
  const lastLine = v.last && v.left != null ? lastText(v, me) : '';
  if (v.phase === 'setup') {
    const waiting = (v.players || []).filter((p) => !p.ready).map((p) => clean(p.nick));
    if (me && me.must === 'setup') return { kicker: 'Початок', t: 'Обери замовлення', s: '📜 Обери замовлення: лиши щонайменше ' + (me.keepMin || 2), short: 'Обери замовлення', mine: true, imp: true };
    return { kicker: 'Початок', t: 'Обирають замовлення', s: waiting.length ? 'Чекаємо на ' + waiting.join(', ') : 'Усі обрали', short: 'Обирають замовлення', mine: false };
  }
  if (me && me.must === 'act') {
    const nick = clean(nickOf(v, me.seat));
    const cant = me.can && !me.can.draw && !me.can.claim && !me.can.tickets;
    return { kicker, t: 'Твій хід, ' + nick + '!', s: lastLine || (cant ? 'Нема що робити — пропусти хід' : 'Візьми карти, займи шлях чи візьми замовлення'), short: 'Твій хід', mine: true };
  }
  if (me && me.must === 'draw') return { kicker, t: 'Ще одна карта', s: lastLine || 'Бери другу: вола з ринку другою не беруть', short: 'Ще одна карта', mine: true };
  if (me && me.must === 'keep') return { kicker, t: 'Лиши замовлення', s: '📜 Лиши щонайменше ' + (me.keepMin || 1), short: 'Лиши замовлення', mine: true, imp: true };
  if (v.turn != null) {
    const p = playerOf(v, v.turn) || {};
    const n = clean(p.nick);
    const away = p.away && !p.bot ? ' (за нього Глек)' : '';
    return { kicker, t: 'Зараз їде ' + n + '…', s: lastLine || (p.bot || p.away ? '🤖 Глек думає…' + away : me ? 'Чекаємо на хід…' : 'Ти дивишся збоку'), short: 'Їде ' + n, mine: false };
  }
  return { kicker, t: 'Чекаємо…', s: '', short: 'Чекаємо…', mine: false };
}

/** «🏁 Останнє коло: ще 3 ходи, твій — останній». Місця ходять по order від turn; left — ще не зроблених ходів. */
export function lastText(v, me) {
  const n = v.left;
  const base = '🏁 Останнє коло: ще ' + n + ' ' + plural(n, 'хід', 'ходи', 'ходів');
  if (!me) return base;
  const order = v.order || [];
  const i0 = order.indexOf(v.turn);
  if (i0 < 0) return base;
  let last = -1;
  for (let k = 0; k < n; k++) if (order[(i0 + k) % order.length] === me.seat) last = k;
  if (last < 0) return base + ', твій уже був';
  return base + (last === n - 1 ? ', твій — останній' : last === 0 ? ', твій — зараз' : ', твій — ' + (last + 1) + '-й');
}

/** Статус для рядка столу (§12, status). */
export function statusText(v, ctx) {
  const room = (ctx && ctx.room) || {};
  if (room.status === 'lobby') {
    let people = 0;
    if (ctx.nickOf) for (let i = 0; i < 5; i++) if (ctx.nickOf(i)) people++;
    const bots = +((room.options || {}).bots || 0);
    return people + Math.min(bots, 5 - people) < 2 ? 'Самому не доїхати: поклич когось або додай 🤖 Глека' : '';
  }
  if (!v || !v.players) return '';
  const me = v.me || null;
  if (v.over || v.phase === 'over') {
    const w = (v.over && v.over.winners) || [];
    return me && w.includes(me.seat) ? 'Перемога! 🏆' : w.length ? 'Партію скінчено' : '';
  }
  if (me && me.must === 'setup') return 'Обери замовлення';
  if (me && me.must === 'keep') return 'Обери замовлення';
  if (me && me.must === 'act') return 'Твій хід';
  if (me && me.must === 'draw') return 'Ще одна карта';
  if (v.turn != null) return 'Їде ' + clean(nickOf(v, v.turn));
  return '';
}

/** Текст оплати для кнопки й aria: «🧵2 🐂1». */
export function comboText(c) {
  return (c.n ? ICON_CARGO[c.c] + c.n : '') + (c.n && c.w ? ' ' : '') + (c.w ? ICON_CARGO.wild + c.w : '');
}

/** Стос, на який варто дивитись для шляху: кольоровий — свій, сірий — найбільший у руці (рівність — за CARGO). */
export function stackFor(cargo, hand) {
  if (cargo !== GREY) return cargo;
  let best = null, n = -1;
  for (const c of CARGO) { const h = (hand && hand[c]) || 0; if (h > n) { n = h; best = c; } }
  return best;
}

/**
 * Усе про обраний шлях для панелі: хто власник, чим платити, що є, чого бракує і яка найкраща дія.
 * pick — індекс обраної оплати (null — best). Повертає { id, name, cargo, len, pts, owner, closed, ok, why, combos, pick,
 * combo, pips: [{ k, have }], have, need, short, stack, twin, twinText, advice, canClaim }.
 */
export function routePanel(v, id, pick) {
  const r = route(id);
  if (!r) return null;
  const me = (v && v.me) || null;
  const out = { id, r, name: routeName(id), cargo: r.cargo, len: r.len, pts: r.pts, owner: null, closed: false, ok: false, why: '', combos: [], pick: null, combo: null, pips: [], have: 0, need: r.len, short: 0, stack: null, twin: r.twin, twinText: '', advice: null, canClaim: false };
  const owned = ((v && v.claimed) || []).find((x) => x.route === id);
  if (owned) out.owner = owned.seat;
  out.closed = ((v && v.closed) || []).includes(id);
  if (r.twin != null) {
    const tw = ((v && v.claimed) || []).find((x) => x.route === r.twin);
    out.twinText = tw
      ? 'Пара: ' + CARGO_NAME[route(r.twin).cargo] + ' — у ' + clean(nickOf(v, tw.seat))
      : 'Пара: ' + CARGO_NAME[route(r.twin).cargo] + ' — вільна';
  }
  if (owned) { out.why = 'Цей шлях уже зайняв ' + clean(nickOf(v, owned.seat)); return out; }
  const c = me && me.claims && me.claims[id];
  if (!c) { out.why = out.closed ? 'За столом на 2–3 парний шлях один, і його вже зайнято' : ''; out.pips = emptyPips(r); return out; }
  if (c.ok) {
    out.ok = true;
    out.combos = c.combos || [];
    out.pick = pick != null && out.combos[pick] ? pick : (c.best || 0);
    out.combo = out.combos[out.pick] || out.combos[0] || null;
    out.have = r.len;
    out.pips = out.combo ? comboPips(out.combo) : [];
    out.canClaim = me.must === 'act';
    return out;
  }
  out.why = c.why || '';
  out.short = c.short || 0;
  if (out.short > 0) {
    const stack = stackFor(r.cargo, me.hand);
    const n = Math.min(r.len, (me.hand && me.hand[stack]) || 0);
    const w = Math.min(r.len - n, (me.hand && me.hand.wild) || 0);
    out.stack = stack;
    out.have = n + w;
    for (let i = 0; i < n; i++) out.pips.push({ k: stack, have: true });
    for (let i = 0; i < w; i++) out.pips.push({ k: WILD, have: true });
    for (let i = n + w; i < r.len; i++) out.pips.push({ k: stack, have: false });
    out.advice = advice(v, r, stack);
  } else {
    out.pips = emptyPips(r);
  }
  return out;
}
function emptyPips(r) { const a = []; for (let i = 0; i < r.len; i++) a.push({ k: r.cargo === GREY ? GREY : r.cargo, have: false }); return a; }
function comboPips(c) {
  const a = [];
  for (let i = 0; i < c.n; i++) a.push({ k: c.c, have: true });
  for (let i = 0; i < c.w; i++) a.push({ k: WILD, have: true });
  return a;
}
/** Найкраща дія, коли не вистачає карт: узяти потрібне з ринку, вола цілим ходом або наосліп. */
export function advice(v, r, stack) {
  const me = v.me;
  if (!me || (me.must !== 'act' && me.must !== 'draw')) return null;
  const can = me.can || {};
  const mk = can.market || [];
  const m = v.market || [];
  const cargoOk = (k) => (r.cargo === GREY ? k === stack : k === r.cargo);
  for (const slot of mk) {
    if (m[slot] && m[slot] !== WILD && cargoOk(m[slot])) return { act: 'draw', slot, c: m[slot], text: 'Узяти ' + CARGO_ACC[m[slot]] + ' з ринку' };
  }
  if (r.cargo === GREY) for (const slot of mk) if (m[slot] && m[slot] !== WILD) return { act: 'draw', slot, c: m[slot], text: 'Узяти ' + CARGO_ACC[m[slot]] + ' з ринку' };
  if (can.deck) return { act: 'draw', deck: true, text: 'Узяти наосліп з колоди' };
  for (const slot of mk) if (m[slot] === WILD) return { act: 'draw', slot, c: WILD, text: 'Узяти вола з ринку (цілий хід)' };
  return null;
}

/** Текст поради: «Глек би: зайняв Харків — Краматорськ». */
export function tipText(v, tip) {
  if (!tip) return '';
  const m = (v && v.market) || [];
  switch (tip.act) {
    case 'claim': return 'Глек би: зайняв ' + routeName(tip.route) + (tip.c ? ' (' + comboText({ c: tip.c, n: route(tip.route).len - (tip.w || 0), w: tip.w || 0 }) + ')' : '');
    case 'draw':
      if (tip.deck) return 'Глек би: узяв карту наосліп';
      return 'Глек би: узяв ' + (CARGO_ACC[m[tip.slot]] || 'карту') + ' з ринку';
    case 'tickets': return 'Глек би: узяв нові замовлення';
    case 'keep': return 'Глек би: лишив ' + (tip.ids || []).map((id) => ticketName(id)).join(', ');
    case 'pass': return 'Глек би: пропустив хід — нема що робити';
    default: return '';
  }
}

/** Рядок списку 🧭. */
export function pickerRow(v, id) {
  const me = v.me, r = route(id), c = me && me.claims && me.claims[id];
  return { id, name: routeName(id), cargo: r.cargo, len: r.len, pts: r.pts, ok: !!(c && c.ok), why: c && !c.ok ? (c.why || '') : '' };
}
/** Групи списку 🧭 «Вибрати шлях»: для моїх замовлень, можу зайняти зараз, усі (за довжиною). query — назва міста. */
export function pickerGroups(v, query) {
  const me = v && v.me;
  const free = me && me.claims ? Object.keys(me.claims).map(Number) : freeRoutes(v);
  const rowOf = (id) => pickerRow(v, id);
  const out = [];
  const q = String(query || '').trim().toLowerCase();
  if (q) {
    const cs = CITIES.filter((c) => c[2].toLowerCase().includes(q)).map((c) => c[0]);
    const list = free.filter((id) => { const r = route(id); return cs.includes(r.a) || cs.includes(r.b); }).sort((a, b) => route(b).len - route(a).len || a - b);
    return [{ key: 'city', title: '🔍 ' + (cs.length ? CITIES.filter((c) => cs.includes(c[0])).map((c) => c[2]).slice(0, 3).join(', ') : 'Такого міста нема'), rows: list.map(rowOf) }];
  }
  const planIds = [];
  for (const t of (me && me.tickets) || []) for (const id of t.path || []) if (!planIds.includes(id) && free.includes(id)) planIds.push(id);
  if (planIds.length) out.push({ key: 'plan', title: '📜 Для моїх замовлень', rows: planIds.map(rowOf) });
  const okIds = free.filter((id) => me && me.claims && me.claims[id].ok && !planIds.includes(id)).sort((a, b) => route(b).len - route(a).len || a - b);
  if (okIds.length) out.push({ key: 'ok', title: '✅ Можу зайняти зараз', rows: okIds.map(rowOf) });
  out.push({ key: 'all', title: 'Усі', rows: free.slice().sort((a, b) => route(b).len - route(a).len || a - b).map(rowOf) });
  return out;
}
function freeRoutes(v) {
  const taken = new Set(((v && v.claimed) || []).map((x) => x.route));
  const closed = new Set((v && v.closed) || []);
  return ROUTES.map((r) => r[0]).filter((id) => !taken.has(id) && !closed.has(id));
}
/** Усі шляхи з міста — для «Звідси: …». */
export function routesFrom(cityIdx) { return ROUTES.filter((r) => r[1] === cityIdx || r[2] === cityIdx).map((r) => r[0]); }

/** Наступний законний шлях для курсора клавіатури й джойстика. */
export function cycleRoute(list, cur, dir) {
  if (!list || !list.length) return null;
  const i = list.indexOf(cur);
  if (i < 0) return dir > 0 ? list[0] : list[list.length - 1];
  return list[(i + dir + list.length) % list.length];
}

/** Журнал: місце гравця за початком рядка (для цятки кольору). */
export function logSeat(v, line) {
  let best = null, n = 0;
  for (const p of (v && v.players) || []) {
    const name = String(p.nick);
    if (String(line).startsWith(name) && name.length > n) { best = p.seat; n = name.length; }
  }
  return best;
}

/** Правила коротко (§12). doubles — 'one' | 'both'. */
export function rulesHtml(doubles) {
  const twin = doubles === 'both'
    ? 'Парні шляхи: за цим столом можна зайняти обидва, але один гравець не може мати обидва.'
    : 'Парні шляхи: за цим столом один — хто перший зайняв, другий закрито для всіх.';
  return '<ul>'
    + '<li><b>За хід — одне:</b> узяти карти, зайняти шлях або взяти замовлення.</li>'
    + '<li><b>Карти:</b> дві — з ринку чи наосліп. Вола з ринку — цілий хід, другою картою його не беруть; віл наосліп — звичайна карта.</li>'
    + '<li><b>Шлях:</b> плати стільки карт одного вантажу, скільки на ньому возів; воли йдуть за будь-який вантаж. Сірий — будь-який один вантаж.</li>'
    + '<li>' + twin + '</li>'
    + '<li><b>Замовлення</b> таємні: з\'єднав міста своєю мережею — +очки, не встиг — стільки ж мінус наприкінці.</li>'
    + '<li><b>Останнє коло:</b> коли в когось ≤ 2 вози, кожен має ще рівно один хід.</li>'
    + '<li><b>Найдовша валка</b> — найдовша безперервна дорога: +' + LONGEST_BONUS + ' ⭐ (рівність — усім).</li>'
    + '<li><b>Нічия:</b> більше виконаних замовлень, далі Найдовша валка, далі перемога спільна.</li></ul>';
}

/**
 * Звання підсумку (§12): чесний лідер > 0, без нічиєї, ≤ 2 на людину. seats — over.stats.seats, seatsOf — людей лише.
 * Повертає [{ icon, name, seat, why }].
 */
export function titles(over, isHuman) {
  const S = (over && over.stats && over.stats.seats) || [];
  if (!S.length) return [];
  const lead = (f, min = 1) => {
    let best = -Infinity, who = null, tie = false;
    for (const s of S) {
      const x = f(s);
      if (x > best) { best = x; who = s.seat; tie = false; } else if (x === best) tie = true;
    }
    return !tie && best >= min ? { seat: who, n: best } : null;
  };
  const cand = [];
  const push = (icon, name, r, why) => { if (r) cand.push({ icon, name, seat: r.seat, why: why(r.n) }); };
  push('🛞', 'Далекобійник', lead((s) => (s.biggest || 0) * 1000 + (s.wagonsUsed || 0)), () => {
    const s = S.find((x) => x.seat === lead((y) => (y.biggest || 0) * 1000 + (y.wagonsUsed || 0)).seat);
    return 'шлях на ' + s.biggest + ', ' + wagons(s.wagonsUsed || 0);
  });
  push('🧂', 'Чумак', lead((s) => s.crimea || 0), (n) => n + ' із Криму');
  push('📜', 'Погонич', lead((s) => s.ticketsKept || 0), (n) => n + ' ' + plural(n, 'замовлення', 'замовлення', 'замовлень'));
  push('🚧', 'Перехресник', lead((s) => s.blocks || 0), (n) => n + ' ' + plural(n, 'блок', 'блоки', 'блоків'));
  const thrifty = S.filter((s) => (s.claims || 0) >= 10);
  if (thrifty.length) {
    const min = Math.min(...thrifty.map((s) => s.wildsPaid || 0));
    const w = thrifty.filter((s) => (s.wildsPaid || 0) === min);
    if (w.length === 1) cand.push({ icon: '🐂', name: 'Скупник волів', seat: w[0].seat, why: 'лише ' + min + ' ' + pl(min, OX) });
  }
  push('🧺', 'Торгівець', lead((s) => s.draws || 0), (n) => n + ' разів по карти');
  const count = {}, out = [];
  for (const t of cand) {
    if (isHuman && !isHuman(t.seat)) continue;
    count[t.seat] = (count[t.seat] || 0) + 1;
    if (count[t.seat] <= 2) out.push(t);
  }
  return out;
}

/**
 * Графік очок шляхів за колами: по лінії на місце (timeline[i][seat]). Повертає лінії в координатах w × h.
 * Порожні колонки — нулі; вісь y — від 0 до max.
 */
export function chart(timeline, seats, w, h, pad = 22) {
  const T = timeline || [];
  const n = Math.max(1, T.length - 1);
  let max = 1;
  for (const row of T) for (const s of seats) max = Math.max(max, row[s] || 0);
  const X = (i) => pad + (w - pad - 12) * i / n, Y = (x) => h - pad + 6 - (h - pad - 8) * x / max;
  const lines = seats.map((s) => {
    const pts = T.map((row, i) => [+X(i).toFixed(1), +Y(row[s] || 0).toFixed(1)]);
    return { seat: s, pts, d: pts.length ? 'M' + pts.map((p) => p[0] + ' ' + p[1]).join('L') : '', end: pts[pts.length - 1] || [X(0), Y(0)], val: T.length ? T[T.length - 1][s] || 0 : 0 };
  });
  const ticks = [];
  const step = Math.max(1, Math.ceil(max / 4 / 5) * 5);
  for (let g = 0; g <= max; g += step) ticks.push({ v: g, y: +Y(g).toFixed(1) });
  return { lines, ticks, max, rounds: T.length, x0: pad, x1: w - 12 };
}

/** Тривалість партії: «47 хв». */
export const minutes = (ms) => Math.max(1, Math.round((ms || 0) / 60000)) + ' хв';

/** Контур мапи з geo-map.json: land — кільця всіх областей (d), edge — лише зовнішні дуги, regions — кордони областей. */
export function parseGeo(g) {
  if (!g || !g.arcs) return { land: '', edge: '' };
  const ptsOf = (i) => { const a = g.arcs[Math.abs(i) - 1]; const o = []; for (let k = 0; k + 1 < a.length; k += 2) o.push([a[k], a[k + 1]]); return i < 0 ? o.reverse() : o; };
  const refs = {};
  let land = '';
  for (const reg of g.regions || []) for (const ring of reg.rings || []) {
    let first = true;
    for (const i of ring) {
      refs[Math.abs(i)] = (refs[Math.abs(i)] || 0) + 1;
      const p = ptsOf(i);
      land += (first ? 'M' : 'L') + p.map((q) => q[0] + ' ' + q[1]).join('L');
      first = false;
    }
    land += 'Z';
  }
  let edge = '';
  for (const k of Object.keys(refs)) if (refs[k] === 1) edge += 'M' + ptsOf(+k).map((q) => q[0] + ' ' + q[1]).join('L');
  return { land, edge };
}

/**
 * Прямокутник видимого (одиниці мапи) і наближення — зі screenPos двох далеких міст: сцена не каже свого стану, а цього досить
 * для міні-мапи. w, h — розмір поля в CSS px; sp(kind, id) — sc.screenPos. null — не вдалось.
 */
export function viewportOf(sp, w, h) {
  const a = sp('city', 44), b = sp('city', 9);   // Ужгород і Луганськ
  if (!a || !b || !(w > 0) || !(h > 0)) return null;
  const A = city(44), B = city(9);
  const k = Math.hypot(b.x - a.x, b.y - a.y) / Math.hypot(B.x - A.x, B.y - A.y);
  if (!(k > 0)) return null;
  const x0 = A.x - a.x / k, y0 = A.y - a.y / k;
  const fit = Math.min(w / (MAP.w * 1.04), h / (MAP.h * 1.04));
  return { x: x0, y: y0, w: w / k, h: h / k, zoom: k / fit };
}

/**
 * Куди дивитись телефону типово (§11.3): list — відкриті замовлення { a, b } (номери міст). wU × hU — ділянка мапи, яку видно на
 * робочому масштабі, grow — наскільки її можна розширити, щоб умістити все. Вертає центр і ширину ділянки в одиницях мапи.
 * Усі замовлення не влазять — найкоротше; і воно не влазить — один його кінець, далі видно міні-мапу. Нема замовлень — null (уся мапа).
 */
export function homeCrop(list, wU, hU, grow = 1) {
  const L = (list || []).map((t) => ({ A: city(t.a), B: city(t.b) })).filter((t) => t.A && t.B);
  if (!L.length) return null;
  const pad = 120;
  const fits = (items) => {
    const xs = items.flatMap((t) => [t.A.x, t.B.x]), ys = items.flatMap((t) => [t.A.y, t.B.y]);
    const x0 = Math.min(...xs), x1 = Math.max(...xs), y0 = Math.min(...ys), y1 = Math.max(...ys);
    const need = Math.max((x1 - x0 + 2 * pad) / wU, (y1 - y0 + 2 * pad) / hU, 1);
    return need <= grow ? { x: (x0 + x1) / 2, y: (y0 + y1) / 2, w: wU * need } : null;
  };
  const short = L.slice().sort((p, q) => Math.hypot(p.A.x - p.B.x, p.A.y - p.B.y) - Math.hypot(q.A.x - q.B.x, q.A.y - q.B.y))[0];
  return fits(L) || fits([short]) || { x: short.A.x, y: short.A.y, w: wU };
}

/**
 * Що в цьому виді ще не анімовано (§7). Два лічильники: спільний seq і мій own; подія «лише власнику» нового seq не бере, тож
 * нова лише за own. Порядок: за seq, подія з own — слідом за спільною зі своїм seq. Вертає й нові значення лічильників.
 */
export function freshEvents(view, seenSeq, seenOwn) {
  const own = view && view.me && typeof view.me.own === 'number' ? view.me.own : 0;
  const list = ((view && view.events) || []).filter((e) => (e.own != null ? e.own > seenOwn : e.seq > seenSeq));
  list.sort((a, b) => a.seq - b.seq || (a.own || 0) - (b.own || 0));
  return { list, seq: Math.max(seenSeq, view && typeof view.seq === 'number' ? view.seq : seenSeq), own: Math.max(seenOwn, own) };
}
