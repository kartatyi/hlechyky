// «Сорочинський ярмарок» — підробні види для стендів (dev/hud.html, dev/scene.html), поки сервера нема.
// Форма — рівно як у контракті docs/games/specs/sorochyntsi.md, §5 (вид) і §7 (події). Сценарій описує «ядро» (руки,
// крамниці, бронь з id), а viewOf(core, seat) робить із нього вид для місця — так само, як сервер: me.cards (pay/need/short/why)
// за §2.5, me.take/can/why за §2.3–§2.8, me.bulava, приховане (чужа бронь наосліп — id: null). Правила тут — лише для стенда.
//
//   import { makeView, makeEvent, SCENARIOS } from '/games/sorochyntsi/dev/fixture.js';
//   const v = makeView('mid', 0);   // lobby | start | mid | discard | guest | last | other | empty | over | spectator | two | four

import {
  GOODS, BONUSES, TOKENS, GOOD_GEN, DUCAT, PILE, GOLD, HAND_MAX, RESERVE_MAX, TWO_MIN, TIER_SIZE, TIER_ROMAN, GUESTS,
  GUEST_POINTS, STARSHYNA, card, plural,
} from '../consts.js';

export const SCENARIOS = ['lobby', 'start', 'mid', 'discard', 'guest', 'last', 'other', 'empty', 'over', 'spectator', 'two', 'four'];

const zeroT = () => ({ linen: 0, fish: 0, herbs: 0, clay: 0, wool: 0, gold: 0 });
const T = (a) => { const o = zeroT(); TOKENS.forEach((g, i) => { o[g] = a[i] || 0; }); return o; };
const BOTNAME = 'Глек 🤖';

function player(seat, nick, color, tokens, cards, reserved, extra = {}) {
  return { seat, nick, bot: extra.bot ? BOTNAME : null, color, tokens: T(tokens), cards: cards.slice(), reserved: reserved.map((r) => ({ ...r })),
    guests: (extra.guests || []).slice(), away: !!extra.away };
}

function bonusOf(p) {
  const b = { linen: 0, fish: 0, herbs: 0, clay: 0, wool: 0, fern: 0 };
  for (const id of p.cards) b[card(id).bonus]++;
  return b;
}
const iconsOf = (p) => p.cards.reduce((a, id) => a + card(id).k, 0);
const ntokOf = (p) => TOKENS.reduce((a, g) => a + (p.tokens[g] || 0), 0);

function pointsOf(core, p) {
  return p.cards.reduce((a, id) => a + card(id).pts, 0) + GUEST_POINTS * p.guests.length + (core.starshyna && core.starshyna.seat === p.seat ? STARSHYNA.points : 0);
}

const joinUk = (l) => (l.length <= 1 ? l.join('') : l.slice(0, -1).join(', ') + ' й ' + l[l.length - 1]);

/** Ціна за §2.5: { ok, pay } або { ok: false, need, short, why }. */
export function priceFor(p, id) {
  const c = card(id), b = bonusOf(p);
  const pay = {}, need = {};
  let gap = 0;
  for (const g of GOODS) {
    const req = Math.max(0, c.cost[g] - b[g]);
    const own = Math.min(req, p.tokens[g] || 0);
    if (own) pay[g] = own;
    if (req - own > 0) { need[g] = req - own; gap += req - own; }
  }
  const gold = p.tokens.gold || 0;
  const short = Math.max(0, gap - gold);
  if (!short) { if (gap) pay.gold = gap; return { ok: true, pay }; }
  const list = joinUk(GOODS.filter((g) => need[g]).map((g) => need[g] + ' ' + GOOD_GEN[g]));
  return { ok: false, need, short, why: 'бракує ' + list + (gold >= 1 ? ', дукатів у тебе: ' + gold : '') };
}

function rowIds(core) { return core.rows.flat().filter((x) => x != null); }

function meFor(core, seat) {
  const p = core.players.find((x) => x.seat === seat);
  if (!p || p.bot) return null;
  const mine = core.turn === seat && core.phase !== 'over';
  const must = !mine ? null : core.phase === 'play' ? 'act' : core.phase === 'discard' ? 'discard' : core.phase === 'guest' ? 'guest' : null;
  const piles = GOODS.filter((g) => core.bank[g] > 0);
  const two = GOODS.filter((g) => core.bank[g] >= TWO_MIN);
  const act = must === 'act';
  const cards = {};
  for (const id of rowIds(core)) cards[String(id)] = priceFor(p, id);
  for (const r of p.reserved) cards[String(r.id)] = priceFor(p, r.id);
  const can = { take: act && piles.length > 0, reserve: act && p.reserved.length < RESERVE_MAX && (rowIds(core).length > 0 || core.decks.some((d) => d > 0)),
    reserveDeck: [1, 2, 3].map((t) => act && p.reserved.length < RESERVE_MAX && core.decks[t - 1] > 0), pass: false };
  const anyBuy = Object.values(cards).some((c) => c.ok);
  can.pass = act && !can.take && !can.reserve && !anyBuy;
  const why = {};
  if (act) {
    if (!can.take) why.take = 'Купи порожні';
    if (p.reserved.length >= RESERVE_MAX) { why.reserve = 'Бронювати можна щонайбільше 3 крамниці'; for (const t of [1, 2, 3]) why['deck' + t] = why.reserve; }
    else for (const t of [1, 2, 3]) if (!core.decks[t - 1]) why['deck' + t] = 'Колода ' + TIER_ROMAN[t] + ' рівня скінчилась';
    if (!can.pass) why.pass = 'Пропускати не можна — є що робити';
  }
  const b = bonusOf(p), pts = pointsOf(core, p);
  return {
    seat,
    reserved: p.reserved.map((r) => ({ ...r })),
    must,
    discard: must === 'discard' ? Math.max(0, ntokOf(p) - HAND_MAX) : 0,
    guests: must === 'guest' ? core.guestChoice.slice() : [],
    take: { piles: act ? piles : [], need: act ? Math.min(3, piles.length) : 0, two: act ? two : [] },
    can, why, cards,
    bulava: core.bulava != null ? null : { points: Math.max(0, core.target - pts), goods: BONUSES.filter((g) => !b[g]) },
    tip: must ? (core.tip || null) : null,
  };
}

/** Вид для місця seat (null — глядач). */
export function viewOf(core, seat) {
  if (core.phase == null) {
    return { v: 1, phase: null, target: core.target, round: 0, maxRounds: 40, last: false, players: [], order: [], first: null, turn: null,
      bank: null, rows: [[], [], []], decks: [0, 0, 0], guests: [], starshyna: null, bulava: null, discard: null, events: [], seq: core.seq || 0,
      deadline: null, timerMs: 0, bots: null, botIn: null, log: [], over: null, me: null };
  }
  const players = core.players.map((p) => ({
    seat: p.seat, nick: p.nick, bot: p.bot, color: p.color,
    tokens: { ...p.tokens }, ntok: ntokOf(p), bonus: bonusOf(p), cards: p.cards.slice(),
    reserved: p.reserved.map((r) => (r.blind && p.seat !== seat ? { id: null, tier: r.tier, blind: true } : { id: r.id, tier: r.tier, blind: !!r.blind })),
    guests: p.guests.slice(), icons: iconsOf(p), points: pointsOf(core, p),
    starshyna: !!(core.starshyna && core.starshyna.seat === p.seat), bulava: core.bulava === p.seat, away: p.away,
  }));
  const dl = core.deadlineIn != null ? new Date(Date.now() + core.deadlineIn).toISOString() : null;
  const bots = core.players.some((p) => p.bot) ? [0, 1, 2, 3].map((s) => { const p = core.players.find((x) => x.seat === s); return p && p.bot ? BOTNAME : null; }) : null;
  const disc = core.phase === 'discard' ? { seat: core.turn, n: ntokOf(core.players.find((p) => p.seat === core.turn)) - HAND_MAX } : null;
  const events = (core.events || []).map((e) => hideEv(e, seat));
  return {
    v: 1, phase: core.phase, target: core.target, round: core.round, maxRounds: 40, last: !!core.last,
    players, order: core.players.map((p) => p.seat), first: core.first, turn: core.phase === 'over' ? null : core.turn,
    bank: { ...core.bank }, rows: core.rows.map((r) => r.slice()), decks: core.decks.slice(),
    guests: core.guests.map((g) => ({ ...g })), starshyna: core.starshyna ? { ...core.starshyna } : null, bulava: core.bulava,
    discard: disc, events, seq: core.seq || 0, deadline: dl, timerMs: dl ? core.timerMs || 60000 : 0, bots,
    botIn: core.botIn != null ? core.botIn : null, log: (core.log || []).slice(), over: core.over || null,
    me: seat == null ? null : meFor(core, seat),
  };
}

function hideEv(e, seat) {
  if (e.k === 'reserve' && e.slot == null && e.seat !== seat) return { ...e, card: null };
  return { ...e };
}

// ─── Сценарії ────────────────────────────────────────────────────────────────────────────────────────────────────

/** Рівно вид із §5 (3 гравці, коло 9, хід Smaug). Бронь Оксани наосліп — справжній id 80 (бачить лише вона). */
function midCore() {
  return {
    phase: 'play', target: 16, round: 9, last: false, first: 1, turn: 0,
    players: [
      player(0, 'Smaug', 0, [1, 2, 0, 1, 0, 1], [3, 8, 13, 22, 44], [{ id: 74, tier: 3, blind: true }]),
      player(1, BOTNAME, 1, [0, 1, 2, 0, 1, 0], [10, 26, 33, 36], [], { bot: true }),
      player(2, 'Оксана', 2, [2, 0, 1, 1, 2, 1], [1, 16, 24, 35, 46, 58], [{ id: 62, tier: 2, blind: false }, { id: 80, tier: 3, blind: true }]),
    ],
    bank: T([2, 2, 2, 3, 2, 3]),
    rows: [[0, 17, 30, 38], [41, 52, 60, 66], [70, 77, 85, 88]],
    decks: [24, 22, 14],
    guests: [{ id: 0, by: null }, { id: 3, by: null }, { id: 6, by: null }, { id: 8, by: null }],
    starshyna: { seat: 2, icons: 3 }, bulava: null, seq: 212, deadlineIn: 42000, timerMs: 60000,
    log: ['Оксана купує «Глиняний ряд» (+1 ⭐)', 'Січова старшина — у Оксана (3 ⚔)', 'Глек 🤖 бере рибу, зілля й вовну'],
    events: [{ seq: 210, k: 'buy', seat: 2, card: 58, from: 'row', slot: 1, pay: { linen: 2, herbs: 3 }, bonus: 'clay', pts: 1 },
      { seq: 211, k: 'take', seat: 1, got: { fish: 1, herbs: 1, wool: 1 } }, { seq: 212, k: 'turn', seat: 0 }],
    tip: { act: 'buy', card: 38 },
  };
}

function startCore() {
  return {
    phase: 'play', target: 16, round: 1, last: false, first: 0, turn: 0,
    players: [player(0, 'Smaug', 0, [], [], []), player(1, BOTNAME, 1, [], [], [], { bot: true }),
      player(2, 'Оксана', 2, [], [], []), player(3, BOTNAME, 3, [], [], [], { bot: true })],
    bank: T([7, 7, 7, 7, 7, 5]),
    rows: [[5, 12, 28, 36], [44, 50, 57, 68], [70, 79, 86, 83]], decks: [36, 26, 16],
    guests: [{ id: 1, by: null }, { id: 4, by: null }, { id: 5, by: null }, { id: 7, by: null }, { id: 9, by: null }],
    starshyna: null, bulava: null, seq: 3, deadlineIn: 58000, timerMs: 60000, log: ['Ярмарок відкрито — перший торгує Smaug'],
    events: [{ seq: 1, k: 'round', n: 1, first: 0 }, { seq: 2, k: 'turn', seat: 0 }],
    tip: { act: 'take', goods: ['fish', 'herbs', 'clay'] },
  };
}

function discardCore() {
  const c = midCore();
  c.phase = 'discard';
  c.players[0].tokens = T([3, 3, 1, 3, 0, 2]);   // 12
  c.bank = T([0, 0, 1, 1, 2, 2]);
  c.players[2].tokens = T([2, 2, 1, 1, 2, 1]);
  c.players[1].tokens = T([0, 0, 2, 0, 1, 0]);
  c.deadlineIn = 14000; c.timerMs = 15000;
  c.tip = { act: 'discard', give: { linen: 1, clay: 1 } };
  c.log.push('Smaug бере 2 риби');
  return c;
}

function guestCore() {
  const c = midCore();
  c.phase = 'guest';
  // Smaug: 4 риби, 4 зілля, 3 глини — і Хівря (🐟4 🌿4), і Циган-міняйло (🐟3 🌿3 🏺3).
  c.players[0].cards = [3, 8, 9, 13, 14, 18, 19, 20, 22, 25, 27, 28];
  c.guests = [{ id: 0, by: null }, { id: 1, by: null }, { id: 6, by: null }, { id: 8, by: 2 }];
  c.players[2].guests = [8];
  c.guestChoice = [1, 6];
  c.tip = { act: 'guest', guest: 6 };
  c.deadlineIn = 15000; c.timerMs = 15000;
  return c;
}

function lastCore() {
  const c = midCore();
  c.round = 22; c.last = true; c.bulava = 2;
  c.players[2].cards = [1, 16, 24, 35, 46, 58, 45, 86, 57];
  c.players[1].cards = [10, 26, 33, 36, 48, 54];
  c.players[2].guests = [8];
  c.players[0].cards = [3, 8, 13, 22, 44, 40, 47, 53, 59, 65, 71];
  c.guests = [{ id: 0, by: null }, { id: 3, by: null }, { id: 6, by: null }, { id: 8, by: 2 }];
  c.decks = [12, 6, 4];
  c.tip = { act: 'take', goods: ['linen', 'herbs', 'wool'] };
  c.log.push('Оксана бере гетьманську булаву! Останнє коло');
  c.events = [{ seq: 300, k: 'bulava', seat: 2 }, { seq: 301, k: 'last', reason: 'bulava' }, { seq: 302, k: 'turn', seat: 0 }];
  c.seq = 302;
  return c;
}

function otherCore() {
  const c = midCore();
  c.turn = 2; c.tip = null; c.botIn = null;
  return c;
}

function emptyCore() {
  const c = midCore();
  c.rows = [[0, null, 30, null], [null, 52, null, 66], [70, null, null, 88]];
  c.decks = [0, 0, 0];
  c.bank = T([0, 1, 0, 0, 1, 0]);
  c.players[0].tokens = T([2, 2, 1, 2, 1, 2]);
  c.players[1].tokens = T([1, 1, 2, 1, 1, 1]);
  c.players[2].tokens = T([2, 1, 2, 2, 2, 2]);
  c.tip = { act: 'take', goods: ['fish', 'wool'] };
  return c;
}

function overCore() {
  const c = lastCore();
  c.phase = 'over'; c.turn = null; c.deadlineIn = null; c.tip = null;
  const tl = [];
  const fin = [11, 4, 17];
  for (let r = 0; r <= 23; r++) tl.push([Math.round(fin[0] * Math.pow(r / 23, 1.3)), Math.round(fin[1] * Math.pow(r / 23, 1.5)), Math.round(fin[2] * Math.pow(r / 23, 1.2)), 0]);
  c.over = {
    winners: [2], reason: 'bulava', bulava: 2, points: fin, cards: [11, 6, 9],
    stats: { rounds: 23, turns: 69, ms: 1840000, timeline: tl,
      seats: [
        { seat: 0, takes: 12, tokens: 33, reserves: 3, blind: 1, buys: [5, 5, 1], goldSpent: 4, returned: 2, guests: 0, icons: 3, fern: 0, firstT3: 18 },
        { seat: 1, takes: 13, tokens: 36, reserves: 1, blind: 0, buys: [6, 4, 0], goldSpent: 1, returned: 0, guests: 0, icons: 1, fern: 0, firstT3: null },
        { seat: 2, takes: 10, tokens: 28, reserves: 2, blind: 1, buys: [4, 7, 2], goldSpent: 3, returned: 1, guests: 1, icons: 4, fern: 1, firstT3: 14 },
      ] },
  };
  c.events = [...c.events, { seq: 303, k: 'over', winners: [2], reason: 'bulava', points: fin }];
  c.seq = 303;
  return c;
}

function twoCore() {
  return {
    phase: 'play', target: 16, round: 6, last: false, first: 0, turn: 0,
    players: [player(0, 'Smaug', 0, [1, 0, 2, 1, 1, 0], [6, 14, 29], []), player(1, 'Оксана', 1, [0, 2, 1, 0, 2, 1], [11, 21, 38], [{ id: 49, tier: 2, blind: false }])],
    bank: T([3, 2, 1, 3, 1, 4]), rows: [[0, 17, 30, 39], [41, 52, 60, 66], [70, 77, 85, 88]], decks: [30, 25, 16],
    guests: [{ id: 2, by: null }, { id: 5, by: null }, { id: 9, by: null }], starshyna: null, bulava: null, seq: 40, deadlineIn: 50000, timerMs: 60000,
    log: ['Оксана бронює «Коптильня» і бере дукат'], events: [], tip: { act: 'take', goods: ['linen', 'clay', 'wool'] },
  };
}

function fourCore() {
  return {
    phase: 'play', target: 18, round: 12, last: false, first: 3, turn: 0,
    players: [
      player(0, 'Smaug', 0, [1, 1, 0, 2, 1, 3], [4, 12, 20, 27, 31, 37, 42], [{ id: 63, tier: 2, blind: false }, { id: 81, tier: 3, blind: true }, { id: 15, tier: 1, blind: true }]),
      player(1, BOTNAME, 1, [2, 0, 1, 1, 0, 1], [0, 9, 16, 33, 48], [{ id: 55, tier: 2, blind: true }], { bot: true }),
      player(2, 'Оксана', 2, [0, 2, 2, 0, 1, 1], [1, 8, 25, 35, 46, 60], [{ id: 75, tier: 3, blind: false }]),
      player(3, 'Тарас', 3, [1, 1, 1, 1, 1, 0], [2, 10, 19, 32, 58, 64], [], { away: true }),
    ],
    bank: T([3, 3, 3, 3, 4, 0]), rows: [[5, 13, 30, 38], [41, 52, 66, 68], [70, 77, 85, 89]], decks: [17, 14, 9],
    guests: [{ id: 0, by: null }, { id: 2, by: null }, { id: 4, by: null }, { id: 6, by: null }, { id: 7, by: null }],
    starshyna: { seat: 3, icons: 3 }, bulava: null, seq: 160, deadlineIn: 35000, timerMs: 60000, botIn: null,
    log: ['Тарас задрімав — Глек торгує за нього', 'Глек 🤖 бронює наосліп з колоди II і бере дукат'], events: [], tip: { act: 'buy', card: 13 },
  };
}

const CORES = { start: startCore, mid: midCore, discard: discardCore, guest: guestCore, last: lastCore, other: otherCore, empty: emptyCore,
  over: overCore, spectator: midCore, two: twoCore, four: fourCore, lobby: () => ({ phase: null, target: 16, seq: 0 }) };

export function makeCore(name) { return (CORES[name] || midCore)(); }

/** Вид сценарію для місця (null — глядач; spectator — завжди глядач). */
export function makeView(name, seat = 0) {
  const c = makeCore(name);
  return viewOf(c, name === 'spectator' ? null : seat);
}

/** Подія §7 з розумними типовими полями. */
export function makeEvent(k, o = {}) {
  const base = {
    round: { n: 2, first: 0 }, turn: { seat: 0 }, take: { seat: 1, got: { fish: 1, herbs: 1, clay: 1 } },
    reserve: { seat: 1, tier: 2, slot: 1, card: 52, gold: 1 }, buy: { seat: 1, card: 38, from: 'row', slot: 3, pay: { linen: 1 }, bonus: 'wool', pts: 0 },
    refill: { tier: 1, slot: 3, card: 39 }, discard: { seat: 1, gave: { fish: 1 } }, guest: { seat: 1, guest: 6 },
    starshyna: { seat: 1, from: null, icons: 3 }, bulava: { seat: 1 }, last: { reason: 'bulava' }, pass: { seat: 1 },
    over: { winners: [1], reason: 'bulava', points: [12, 17, 9] },
  }[k] || {};
  return { seq: 0, k, ...base, ...o };
}

export { TIER_SIZE, PILE, GOLD, plural };
