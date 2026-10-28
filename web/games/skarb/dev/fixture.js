// «Скарб Полуботка» — підробні види для стендів (dev/hud.html, dev/scene.html), поки сервера нема.
// Форма — рівно як у контракті docs/games/specs/skarb.md, §5 (вид), §7 (події), §9 (кінець). Законні місця (`legal`)
// тут рахує крихітний «сервер-заглушка» за §2.3 — лише щоб стенд був живий; картка в грі правил не рахує ніколи.
//
//   import { makeView, makeEvent, SCENARIOS, applyAct } from '/games/skarb/dev/fixture.js';
//   const view = makeView('play', 0);   // lobby reveal play broken other near round over spectator ten

import { SHAPES, GRID, STEP, OPP, BIT, DIRS, rot180, TOOLS, ROLE_PACK, HAND } from '../consts.js';

export const SCENARIOS = ['lobby', 'reveal', 'play', 'broken', 'other', 'near', 'round', 'over', 'spectator', 'ten'];

const NICKS = ['Smaug', 'Глек 🤖', 'Оксана', 'Макітра 🤖', 'Тарас', 'Ліна', 'Богдан', 'Остап', 'Марічка', 'Данило'];
const BOTS10 = ['Глек 🤖', 'Макітра 🤖', 'Глечик 🤖', 'Куманець 🤖', 'Збанок 🤖', 'Ринка 🤖', 'Барильце 🤖', 'Кухлик 🤖', 'Миска 🤖'];

/** Карта колоди за id (§1.2). */
export function cardById(id) {
  const P = [[0, 'x'], [5, 'tv'], [10, 'th'], [15, 'v'], [19, 'h'], [22, 'ce'], [26, 'cw'], [31, 'dx'], [32, 'dtv'], [33, 'dth'],
    [34, 'dv'], [35, 'dh'], [36, 'dce'], [37, 'dcw'], [38, 'ds'], [39, 'de'], [40, null]];
  if (id < 40) { let s = 'x'; for (const [a, ss] of P) if (id >= a && ss) s = ss; return { id, k: 'path', s }; }
  if (id < 49) return { id, k: 'break', tool: TOOLS[Math.floor((id - 40) / 3)] };
  if (id < 55) return { id, k: 'fix', tools: [TOOLS[Math.floor((id - 49) / 2)]] };
  if (id === 55) return { id, k: 'fix', tools: ['pick', 'lamp'] };
  if (id === 56) return { id, k: 'fix', tools: ['pick', 'cart'] };
  if (id === 57) return { id, k: 'fix', tools: ['lamp', 'cart'] };
  if (id < 64) return { id, k: 'map' };
  return { id, k: 'rockfall' };
}

const card = (x, y, s, r, by, id) => {
  const m = r ? rot180(SHAPES[s].m) : SHAPES[s].m;
  return { x, y, s, r: (SHAPES[s].m === rot180(SHAPES[s].m)) ? 0 : r, m, c: SHAPES[s].c, by, id };
};
const START = () => ({ x: 0, y: 0, s: 'start', r: 0, m: 15, c: true, by: null, id: null });
const closedGoals = () => GRID.goals.map(([x, y], i) => ({ i, x, y, open: false, kind: null, s: null, r: null, m: null, c: null }));
const grid = () => JSON.parse(JSON.stringify(GRID));

// ─── Заглушка правил (§2.3–§2.4) — лише для стенду ───────────────────────────
const key = (x, y) => x + ',' + y;
const inB = (x, y) => x >= GRID.x0 && x <= GRID.x1 && y >= GRID.y0 && y <= GRID.y1;
const isShaft = (x, y) => GRID.shaft.some(([a, b]) => a === x && b === y);
const goalAt = (v, x, y) => v.goals.find((g) => g.x === x && g.y === y) || null;

function cardsMap(v) {
  const m = new Map();
  for (const c of v.board) m.set(key(c.x, c.y), c);
  for (const g of v.goals) if (g.open) m.set(key(g.x, g.y), g);
  return m;
}

/** Вузли «клітинка|край», з'єднані з колодязем. */
export function connected(v) {
  const cm = cardsMap(v);
  const seen = new Set(), q = [];
  const push = (x, y, d) => { const k = key(x, y) + '|' + d; if (!seen.has(k)) { seen.add(k); q.push([x, y, d]); } };
  for (const d of DIRS) push(0, 0, d);
  while (q.length) {
    const [x, y, d] = q.shift();
    const c = cm.get(key(x, y));
    if (!c) continue;
    if (c.c) for (const d2 of DIRS) if (c.m & BIT[d2]) push(x, y, d2);
    const nx = x + STEP[d][0], ny = y + STEP[d][1];
    const n = cm.get(key(nx, ny));
    if (n && (n.m & BIT[OPP[d]]) && !(x === 0 && y === 0 && d === 'N')) push(nx, ny, OPP[d]);
  }
  return seen;
}

export function linkedCells(v) {
  const s = connected(v), out = [];
  for (const c of v.board) if (DIRS.some((d) => s.has(key(c.x, c.y) + '|' + d))) out.push([c.x, c.y]);
  return out;
}

function canPlace(v, cm, con, x, y, m) {
  if (!inB(x, y) || cm.has(key(x, y)) || goalAt(v, x, y) || isShaft(x, y) || (x === 0 && y === 0)) return false;
  let link = false;
  for (const d of DIRS) {
    const nx = x + STEP[d][0], ny = y + STEP[d][1];
    const n = cm.get(key(nx, ny));
    if (!n) continue;
    const mine = !!(m & BIT[d]), theirs = !!(n.m & BIT[OPP[d]]);
    if (mine !== theirs) return false;
    if (mine && con.has(key(nx, ny) + '|' + OPP[d])) link = true;
  }
  return link;
}

/** legal і why для руки місця seat — як сервер (§5). */
export function legalFor(v, seat, hand) {
  const cm = cardsMap(v), con = connected(v);
  const me = v.players.find((p) => p.seat === seat) || { broken: [] };
  const legal = {}, why = {};
  const bt = TOOLS.find((t) => me.broken.includes(t));
  for (const c of hand) {
    let L = [];
    if (c.k === 'path') {
      if (bt) { why[c.id] = { pick: 'Кирку зламано', lamp: 'Лампу розбито', cart: 'Тачка без колеса' }[bt] + ' — спершу полагодь'; legal[c.id] = []; continue; }
      const sh = SHAPES[c.s], sym = rot180(sh.m) === sh.m;
      for (let y = GRID.y0; y <= GRID.y1; y++) for (let x = GRID.x0; x <= GRID.x1; x++) for (const r of sym ? [0] : [0, 1]) {
        if (canPlace(v, cm, con, x, y, r ? rot180(sh.m) : sh.m)) L.push([x, y, r]);
      }
      if (!L.length) why[c.id] = 'Цій карті нема де лягти';
    } else if (c.k === 'break') {
      L = v.players.filter((p) => p.seat !== seat && !p.broken.includes(c.tool)).map((p) => p.seat);
      if (!L.length) why[c.id] = 'Усім, кому можна, вже зламано';
    } else if (c.k === 'fix') {
      for (const p of v.players) for (const t of c.tools) if (p.broken.includes(t)) L.push([p.seat, t]);
      if (!L.length) why[c.id] = 'Нема що лагодити';
    } else if (c.k === 'map') {
      L = v.goals.filter((g) => !g.open).map((g) => g.i);
      if (!L.length) why[c.id] = 'Усі мітки вже відкриті';
    } else if (c.k === 'rockfall') {
      L = v.board.filter((b) => b.s !== 'start').sort((a, b) => a.y - b.y || a.x - b.x).map((b) => [b.x, b.y]);
      if (!L.length) why[c.id] = 'Нема що завалювати';
    }
    legal[c.id] = L;
  }
  return { legal, why };
}

// ─── Вид ─────────────────────────────────────────────────────────────────────
function players(n, botSeats, opts = {}) {
  const out = [];
  for (let s = 0; s < n; s++) {
    const bot = botSeats.includes(s) ? (opts.botNames ? opts.botNames[botSeats.indexOf(s)] : NICKS[s]) : null;
    out.push({ seat: s, nick: bot || NICKS[s], bot, color: s, hand: opts.hand || HAND[n] || 5, broken: [], gold: 0, role: null, away: false });
  }
  return out;
}

function base(n, botSeats, opts) {
  const ps = players(n, botSeats, opts);
  const bots = Array(10).fill(null);
  for (const p of ps) if (p.bot) bots[p.seat] = p.bot;
  const [moles, diggers] = ROLE_PACK[n];
  return {
    v: 1, grid: grid(), phase: 'play', round: 1, rounds: 3, pack: { moles, diggers }, players: ps,
    order: ps.map((p) => p.seat), first: 0, turn: 0, board: [START()], linked: [[0, 0]], goals: closedGoals(),
    deck: 67 - n * (HAND[n] || 5), goldLeft: 28, roundEnd: null, nextAt: null, ready: [], events: [], seq: 10,
    deadline: new Date(Date.now() + 52000).toISOString(), timerMs: 60000, bots: bots.some(Boolean) ? bots : null,
    botIn: null, log: [], over: null, me: null,
  };
}

function withMe(v, seat, hand, extra = {}) {
  if (seat == null || !v.players.find((p) => p.seat === seat) || v.players.find((p) => p.seat === seat).bot) { v.me = null; return v; }
  const myTurn = v.phase === 'play' && v.turn === seat;
  const L = myTurn ? legalFor(v, seat, hand) : { legal: null, why: {} };
  v.me = { seat, role: extra.role || 'digger', allies: extra.allies || null, hand, peeks: extra.peeks || {},
    must: v.phase === 'round' ? (v.ready.includes(seat) ? null : 'next') : myTurn ? 'play' : null,
    legal: myTurn ? L.legal : null, why: L.why || {}, tip: extra.tip !== undefined ? extra.tip : null };
  const p = v.players.find((x) => x.seat === seat);
  p.hand = hand.length;
  return v;
}

/** Поле з §5. */
function board5() {
  return [START(), card(1, 0, 'h', 0, 1, 19), card(2, 0, 'x', 0, 2, 3), card(3, 0, 'h', 0, 0, 20), card(2, 1, 'v', 0, 3, 15),
    card(2, 2, 'dv', 0, 4, 34), card(4, 0, 'tv', 1, 1, 6), card(4, -1, 'cw', 0, 2, 27)];
}
const HAND5 = [{ id: 12, k: 'path', s: 'th' }, { id: 33, k: 'path', s: 'dth' }, { id: 44, k: 'break', tool: 'lamp' },
  { id: 55, k: 'fix', tools: ['pick', 'lamp'] }, { id: 60, k: 'map' }, { id: 65, k: 'rockfall' }];

function roundEnd(n, side, finisher, roles, aside, goldIdx, gold, totals) {
  return { n, side, finisher, roles, aside, goals: [0, 1, 2].map((i) => ({ i, kind: i === goldIdx ? 'gold' : 'stone' })), gold, totals };
}

function revealGoals(v, goldIdx) {
  const stones = [0, 1, 2].filter((i) => i !== goldIdx);   // stoneA — на меншому індексі (§2.1)
  for (const g of v.goals) {
    g.kind = g.i === goldIdx ? 'gold' : 'stone';
    if (!g.open) g.s = g.kind === 'gold' ? 'gold' : g.i === stones[0] ? 'stoneA' : 'stoneB';
  }
}

export function makeView(name = 'play', seat = 0) {
  if (typeof seat === 'object' && seat) seat = seat.seat;   // як у Хуторах: makeView(name, { seat })
  let v;
  const ev = [];
  const now = Date.now();
  switch (name) {
    case 'lobby': {
      v = base(3, []);
      Object.assign(v, { phase: null, players: [], order: [], turn: null, deadline: null, round: 0, bots: null });
      v.me = null;
      return v;
    }
    case 'reveal': {
      v = base(7, [1, 3]);
      v.first = 0; v.turn = 0; v.seq = 30;
      v.deadline = new Date(now + 64000).toISOString();
      v.events = [{ seq: 30, k: 'round', n: 1, first: 0, hand: 5 }];
      v.log = ['Раунд 1: кротів у колоді — до 3'];
      const hand = [{ id: 0, k: 'path', s: 'x' }, { id: 23, k: 'path', s: 'ce' }, { id: 16, k: 'path', s: 'v' }, { id: 41, k: 'break', tool: 'pick' }, { id: 59, k: 'map' }];
      return withMe(v, seat, hand, { role: 'mole', allies: [5], tip: { card: 0, x: 1, y: 0, r: 0 } });
    }
    case 'play': case 'broken': case 'spectator': case 'other': {
      v = base(7, [1, 3]);
      v.round = 2; v.first = 3; v.turn = name === 'other' ? 4 : 0; v.seq = 118; v.deck = 23; v.goldLeft = 19;
      v.board = board5();
      v.goals[2] = { i: 2, x: 8, y: 2, open: true, kind: 'stone', s: 'stoneB', r: 0, m: 12, c: true };
      v.players.forEach((p, i) => { p.gold = [4, 3, 6, 5, 9, 3, 2][i]; p.hand = 5; });
      v.players[3].broken = ['lamp'];
      if (name === 'broken') v.players[0].broken = ['pick'];
      v.players[5].away = true;
      v.linked = linkedCells(v);
      v.log = ['Ліна задрімала — Глек копає замість неї', 'Тарас прокопав глухий хід', 'Глек 🤖 прокопав хід', 'Оксана прокопала хід', 'Макітра 🤖 зазирнув під верхню мітку', 'Smaug прокопав хід'];
      v.events = [{ seq: 117, k: 'place', seat: 2, card: 27, x: 4, y: -1, s: 'cw', r: 0, m: 12, c: true }, { seq: 118, k: 'turn', seat: v.turn }];
      if (name === 'other') v.botIn = null;
      v = withMe(v, name === 'spectator' ? null : seat, HAND5.map((c) => ({ ...c, ...(c.tools ? { tools: c.tools.slice() } : {}) })), {
        peeks: { 0: 'stone' }, tip: name === 'broken' ? { card: 55, seat: 0, tool: 'pick' } : { card: 12, x: 4, y: 1, r: 1 },
      });
      if (name === 'broken' && v.me) {
        v.me.hand[3] = { id: 49, k: 'fix', tools: ['pick'] };
        const L = legalFor(v, seat, v.me.hand);
        v.me.legal = L.legal; v.me.why = L.why;
        v.me.tip = { card: 49, seat: 0, tool: 'pick' };
      }
      if (name === 'spectator') v.me = null;
      return v;
    }
    case 'near': {
      v = base(5, [2]);
      v.round = 3; v.seq = 200; v.deck = 6;
      v.board = [START(), card(1, 0, 'h', 0, 0, 19), card(2, 0, 'h', 0, 1, 20), card(3, 0, 'x', 0, 2, 0), card(4, 0, 'h', 0, 3, 21),
        card(5, 0, 'h', 0, 4, 2), card(6, 0, 'x', 0, 0, 1), card(6, -1, 'v', 0, 1, 15), card(6, -2, 'ce', 0, 2, 22),
        card(6, 1, 'v', 0, 3, 16), card(6, 2, 'cw', 1, 4, 26), card(3, 1, 'dv', 0, 1, 34)];
      v.linked = linkedCells(v);
      v.players.forEach((p, i) => { p.gold = [5, 7, 4, 6, 3][i]; });
      v.log = ['Глек 🤖 прокопав хід', 'Тарас прокопав хід'];
      v.events = [{ seq: 200, k: 'turn', seat: 0 }];
      return withMe(v, seat, [{ id: 4, k: 'path', s: 'x' }, { id: 24, k: 'path', s: 'ce' }, { id: 36, k: 'path', s: 'dce' }, { id: 61, k: 'map' }, { id: 64, k: 'rockfall' }, { id: 47, k: 'break', tool: 'cart' }],
        { peeks: { 0: 'stone', 1: 'gold' }, tip: { card: 4, x: 7, y: 0, r: 0 } });
    }
    case 'round': {
      v = base(7, [1, 3]);
      v.round = 2; v.phase = 'round'; v.turn = null; v.deadline = null; v.seq = 160;
      v.board = board5().concat([card(5, 0, 'h', 0, 4, 21), card(6, 0, 'h', 0, 0, 20), card(7, 0, 'x', 0, 4, 2)]);
      v.goals[2] = { i: 2, x: 8, y: 2, open: true, kind: 'stone', s: 'stoneB', r: 0, m: 12, c: true };
      v.goals[1] = { i: 1, x: 8, y: 0, open: true, kind: 'gold', s: 'gold', r: 0, m: 15, c: true };
      revealGoals(v, 1);
      v.board[6] = card(4, 0, 'x', 0, 1, 4);
      v.linked = linkedCells(v);
      const roles = { 0: 'digger', 1: 'mole', 2: 'digger', 3: 'digger', 4: 'digger', 5: 'mole', 6: 'digger' };
      v.roundEnd = roundEnd(2, 'diggers', 4, roles, 'digger', 1,
        [{ seat: 4, n: 5, cards: [3, 2] }, { seat: 3, n: 3, cards: [2, 1] }, { seat: 2, n: 2, cards: [1, 1] }, { seat: 0, n: 1, cards: [1] }, { seat: 6, n: 1, cards: [1] }],
        [5, 3, 8, 8, 14, 3, 3]);
      v.players.forEach((p, i) => { p.gold = v.roundEnd.totals[i]; p.role = roles[p.seat]; p.hand = 2; });
      v.nextAt = new Date(now + 12000).toISOString();
      v.ready = [2];
      v.log = ['Тарас докопався до скарбу Полуботка!', 'Копачі ділять злитки'];
      v.events = [{ seq: 159, k: 'goal', i: 1, x: 8, y: 0, kind: 'gold', s: 'gold', r: 0, m: 15, c: true, by: 4 }, { seq: 160, k: 'end', n: 2, side: 'diggers', finisher: 4 }];
      return withMe(v, seat, [], { role: 'digger', peeks: { 0: 'stone' } });
    }
    case 'over': {
      v = base(7, [1, 3]);
      v.round = 3; v.phase = 'over'; v.turn = null; v.deadline = null; v.seq = 240;
      v.board = board5();
      revealGoals(v, 0);
      v.goals[0].open = true; v.goals[0].r = 0; v.goals[0].m = 15; v.goals[0].c = true;
      v.linked = linkedCells(v);
      const R1 = roundEnd(1, 'moles', null, { 0: 'digger', 1: 'digger', 2: 'mole', 3: 'digger', 4: 'digger', 5: 'digger', 6: 'digger' }, 'mole', 2,
        [{ seat: 2, n: 4, cards: null }], [0, 0, 4, 0, 0, 0, 0]);
      const R2 = roundEnd(2, 'diggers', 4, { 0: 'digger', 1: 'mole', 2: 'digger', 3: 'digger', 4: 'digger', 5: 'mole', 6: 'digger' }, 'digger', 1,
        [{ seat: 4, n: 5, cards: [3, 2] }, { seat: 3, n: 3, cards: [2, 1] }, { seat: 2, n: 2, cards: [1, 1] }, { seat: 0, n: 1, cards: [1] }, { seat: 6, n: 1, cards: [1] }],
        [1, 0, 6, 3, 5, 0, 1]);
      const R3 = roundEnd(3, 'diggers', 0, { 0: 'digger', 1: 'digger', 2: 'digger', 3: 'mole', 4: 'digger', 5: 'digger', 6: 'mole' }, 'digger', 0,
        [{ seat: 0, n: 5, cards: [3, 2] }, { seat: 6, n: 3, cards: [2, 1] }, { seat: 5, n: 2, cards: [1, 1] }, { seat: 4, n: 2, cards: [1, 1] }, { seat: 2, n: 1, cards: [1] }, { seat: 1, n: 1, cards: [1] }],
        [6, 1, 7, 3, 7, 2, 1]);
      R3.totals = [6, 1, 7, 3, 7, 2, 4];
      v.roundEnd = R3;
      const tl = [R1.totals, R2.totals, R3.totals];
      v.players.forEach((p, i) => { p.gold = R3.totals[i]; p.role = R3.roles[p.seat]; p.hand = 0; });
      v.over = {
        winners: [2, 4], reason: 'gold', gold: R3.totals.slice(), rounds: [R1, R2, R3],
        stats: { turns: 141, ms: 1834000, timeline: tl, seats: v.players.map((p, i) => ({ seat: p.seat, paths: [9, 7, 6, 8, 11, 5, 4][i], deadEnds: [1, 0, 2, 0, 1, 1, 2][i],
          breaks: [2, 1, 3, 0, 1, 2, 1][i], fixes: [1, 2, 0, 3, 1, 0, 0][i], maps: [1, 1, 0, 2, 1, 0, 1][i], rockfalls: [0, 1, 2, 0, 0, 1, 1][i], passes: [3, 2, 4, 2, 1, 5, 3][i],
          moleRounds: [0, 1, 1, 1, 0, 1, 1][i], won: [2, 1, 3, 2, 2, 1, 2][i], moleGold: [0, 0, 4, 0, 0, 0, 0][i], finished: [1, 0, 0, 0, 1, 0, 0][i] })) },
      };
      v.log = ['Smaug докопався до скарбу Полуботка!', 'Скарб поділили: Оксана і Тарас — по 7 злитків'];
      v.events = [{ seq: 240, k: 'over', winners: [2, 4], gold: R3.totals.slice() }];
      return withMe(v, seat, [], { role: 'digger' });
    }
    case 'ten': {
      v = base(10, [1, 2, 3, 4, 5, 6, 7, 8, 9], { botNames: BOTS10, hand: 4 });
      v.round = 1; v.seq = 50; v.deck = 27;
      v.board = [START(), card(1, 0, 'h', 0, 3, 19), card(2, 0, 'tv', 0, 5, 5), card(2, 1, 'v', 0, 7, 15), card(-1, 0, 'h', 0, 9, 20)];
      v.linked = linkedCells(v);
      v.players.forEach((p, i) => { p.gold = 0; if (i === 4) p.broken = ['pick', 'cart']; if (i === 7) p.broken = ['lamp']; });
      v.log = ['Збанок 🤖 зламав кирку й тачку… ой, лише кирку', 'Кухлик 🤖 прокопав хід'];
      v.events = [{ seq: 50, k: 'turn', seat: 0 }];
      return withMe(v, seat, [{ id: 7, k: 'path', s: 'tv' }, { id: 27, k: 'path', s: 'cw' }, { id: 56, k: 'fix', tools: ['pick', 'cart'] }, { id: 66, k: 'rockfall' }],
        { tip: { card: 56, seat: 4, tool: 'pick' } });
    }
    default: return makeView('play', seat);
  }
}

/** Подія §7 з розумними типовими полями: makeEvent('place', { seq, x, y, s, r }). */
export function makeEvent(k, o = {}) {
  const seq = o.seq != null ? o.seq : 1;
  const D = {
    round: { n: 1, first: 0, hand: 5 },
    turn: { seat: 0 },
    place: { seat: 0, card: 12, x: 2, y: -1, s: 'th', r: 0, m: 14, c: true },
    goal: { i: 1, x: 8, y: 0, kind: 'gold', s: 'gold', r: 0, m: 15, c: true, by: 0 },
    break: { seat: 1, card: 41, target: 0, tool: 'pick' },
    fix: { seat: 0, card: 49, target: 0, tool: 'pick' },
    map: { seat: 0, card: 60, goal: 0, kind: 'stone' },
    rockfall: { seat: 2, card: 65, x: 2, y: 2, s: 'dv' },
    pass: { seat: 3 },
    deckout: {},
    skip: { seat: 4 },
    end: { n: 1, side: 'diggers', finisher: 0 },
    over: { winners: [0], gold: [9, 3, 4] },
  }[k] || {};
  const e = { seq, k, ...D, ...o };
  if (k === 'place' && o.m == null && SHAPES[e.s]) e.m = e.r ? rot180(SHAPES[e.s].m) : SHAPES[e.s].m;
  if (k === 'place' && o.c == null && SHAPES[e.s]) e.c = SHAPES[e.s].c;
  return e;
}

// ─── Стенд: «сервер» на одну дію (лише щоб клацати) ──────────────────────────
const GOLD_AT = 1;
let nextCard = 0;
function drawCard() { nextCard = (nextCard + 7) % 40; return cardById(nextCard); }

/** Дія гравця seat → { view, error } (новий вид з подіями). Примітивно: без кінця раунду від колоди. */
export function applyAct(v0, seat, action, p) {
  const v = JSON.parse(JSON.stringify(v0));
  const me = v.me;
  if (!me || v.turn !== seat) return { view: v0, error: 'Зараз копає інший' };
  const c = me.hand.find((x) => x.id === (p && p.card));
  if (!c) return { view: v0, error: 'Такої карти в тебе нема' };
  let seq = v.seq;
  const evs = [];
  const nick = (s) => (v.players.find((x) => x.seat === s) || {}).nick;
  if (action === 'pass') { evs.push({ seq: ++seq, k: 'pass', seat }); v.log.push(nick(seat) + ' скинув карту'); }
  else if (c.k === 'path') {
    const L = (me.legal[c.id] || []).find(([x, y, r]) => x === p.x && y === p.y && r === (p.r | 0)) || (me.legal[c.id] || []).find(([x, y]) => x === p.x && y === p.y);
    if (!L) return { view: v0, error: 'Хід не сходиться з сусідньою картою' };
    const b = card(p.x, p.y, c.s, p.r | 0, seat, c.id);
    v.board.push(b);
    evs.push({ seq: ++seq, k: 'place', seat, card: c.id, x: b.x, y: b.y, s: b.s, r: b.r, m: b.m, c: b.c });
    v.log.push(nick(seat) + (b.c ? ' прокопав хід' : ' прокопав глухий хід'));
    const con = connected(v);
    for (const g of v.goals) {
      if (g.open) continue;
      for (const d of ['W', 'N', 'S', 'E']) {
        const nx = g.x + STEP[d][0], ny = g.y + STEP[d][1];
        const n = v.board.find((q) => q.x === nx && q.y === ny);
        if (n && (n.m & BIT[OPP[d]]) && con.has(key(nx, ny) + '|' + OPP[d])) {
          const kind = g.i === GOLD_AT ? 'gold' : 'stone', s = kind === 'gold' ? 'gold' : g.i === 0 ? 'stoneA' : 'stoneB';
          const m0 = SHAPES[s].m, r = (m0 & BIT[d]) ? 0 : (rot180(m0) & BIT[d]) ? 1 : 0;
          Object.assign(g, { open: true, kind, s, r, m: r ? rot180(m0) : m0, c: true });
          evs.push({ seq: ++seq, k: 'goal', i: g.i, x: g.x, y: g.y, kind, s, r, m: g.m, c: true, by: seat });
          v.log.push(kind === 'gold' ? nick(seat) + ' докопався до скарбу!' : 'Під міткою — лише каміння');
          break;
        }
      }
    }
  } else if (c.k === 'break') {
    const t = v.players.find((x) => x.seat === p.seat); t.broken.push(c.tool); t.broken.sort((a, b) => TOOLS.indexOf(a) - TOOLS.indexOf(b));
    evs.push({ seq: ++seq, k: 'break', seat, card: c.id, target: p.seat, tool: c.tool }); v.log.push(nick(seat) + ' зламав ' + c.tool + ' ' + nick(p.seat));
  } else if (c.k === 'fix') {
    const t = v.players.find((x) => x.seat === p.seat), tool = p.tool || c.tools.find((x) => t.broken.includes(x));
    t.broken = t.broken.filter((x) => x !== tool);
    evs.push({ seq: ++seq, k: 'fix', seat, card: c.id, target: p.seat, tool }); v.log.push(nick(seat) + ' полагодив ' + tool + ' ' + nick(p.seat));
  } else if (c.k === 'map') {
    const kind = p.goal === GOLD_AT ? 'gold' : 'stone';
    me.peeks[p.goal] = kind;
    evs.push({ seq: ++seq, k: 'map', seat, card: c.id, goal: p.goal, kind }); v.log.push(nick(seat) + ' зазирнув під мітку');
  } else if (c.k === 'rockfall') {
    const i = v.board.findIndex((b) => b.x === p.x && b.y === p.y && b.s !== 'start');
    if (i < 0) return { view: v0, error: 'Тут нема ходу, щоб завалити' };
    const [b] = v.board.splice(i, 1);
    evs.push({ seq: ++seq, k: 'rockfall', seat, card: c.id, x: b.x, y: b.y, s: b.s }); v.log.push(nick(seat) + ' засипав хід');
  }
  me.hand = me.hand.filter((x) => x.id !== c.id);
  if (v.deck > 0) { me.hand.push(drawCard()); v.deck--; }
  v.linked = linkedCells(v);
  // Хід мені ж (стенд — щоб клацати далі).
  const L = legalFor(v, seat, me.hand);
  me.legal = L.legal; me.why = L.why; me.tip = null;
  v.players.find((x) => x.seat === seat).hand = me.hand.length;
  evs.push({ seq: ++seq, k: 'turn', seat });
  v.events = [...v.events, ...evs].slice(-40);
  v.seq = seq;
  v.log = v.log.slice(-12);
  v.deadline = new Date(Date.now() + 60000).toISOString();
  return { view: v, error: null };
}
