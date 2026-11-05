// «Соляний шлях» — підробні види для стендів (dev/hud.html, dev/scene.html), поки сервера нема. Форма — рівно контракт
// docs/games/specs/solyanyi.md: §5 (вид), §7 (події), §9 (кінець). Законні оплати й найкоротші шляхи рахує крихітний
// «сервер-заглушка» — лише щоб стенд був живий; картка в грі питає справжній сервер.
//
//   import { makeView, makeEvent, SCENARIOS, applyAct } from '/games/solyanyi/dev/fixture.js';
//   const view = makeView('mid', 0);   // lobby setup start mid draw2 keep other last empty over spectator two five

import { CARGO, CARDS, ROUTES, TICKETS, CARGO_GEN, CARGO_ACC, ROUTE_POINTS, route, ticket, routeName, ticketName, city, MAP_SIG, WILD, GREY } from '../consts.js';

export const SCENARIOS = ['lobby', 'setup', 'start', 'mid', 'draw2', 'keep', 'other', 'last', 'empty', 'over', 'spectator', 'two', 'five'];

const NICKS = ['Smaug', 'Глек 🤖', 'Оксана', 'Макітра 🤖', 'Тарас'];
const emptyHand = () => ({ salt: 0, fish: 0, grain: 0, tar: 0, honey: 0, wool: 0, hides: 0, linen: 0, wild: 0 });

// ─── Заглушка правил ────────────────────────────────────────────────────
const cost = (S, seat, id) => {
  const r = route(id), own = S.claimed.find((c) => c.route === id);
  if (own) return own.seat === seat ? 0 : Infinity;
  if (r.twin !== null) {
    const tw = S.claimed.find((c) => c.route === r.twin);
    if (tw && (S.players.length <= 3 || tw.seat === seat)) return Infinity;
  }
  return r.len;
};
function dijkstra(S, seat, a, b) {
  const D = new Map([[a, 0]]), P = new Map(), Q = new Set([a]), done = new Set();
  while (Q.size) {
    let u = null, best = Infinity;
    for (const x of Q) if (D.get(x) < best) { best = D.get(x); u = x; }
    Q.delete(u); done.add(u);
    if (u === b) break;
    for (const rr of ROUTES) {
      if (rr[1] !== u && rr[2] !== u) continue;
      const v = rr[1] === u ? rr[2] : rr[1], c = cost(S, seat, rr[0]);
      if (c === Infinity || done.has(v)) continue;
      if (!D.has(v) || D.get(v) > best + c) { D.set(v, best + c); P.set(v, [u, rr[0]]); Q.add(v); }
    }
  }
  if (!D.has(b)) return null;
  const path = [];
  for (let x = b; x !== a; x = P.get(x)[0]) if (cost(S, seat, P.get(x)[1]) > 0) path.push(P.get(x)[1]);
  return { need: D.get(b), path: path.sort((x, y) => x - y) };
}
function road(routes) {
  const adj = {};
  for (const id of routes) { const r = route(id); (adj[r.a] = adj[r.a] || []).push([r.b, id, r.len]); (adj[r.b] = adj[r.b] || []).push([r.a, id, r.len]); }
  let best = 0;
  const go = (u, used, len) => {
    best = Math.max(best, len);
    for (const [v, id, l] of adj[u] || []) if (!used.has(id)) { used.add(id); go(v, used, len + l); used.delete(id); }
  };
  for (const c of Object.keys(adj)) go(+c, new Set(), 0);
  return best;
}
function legal(S, seat) {
  const P = S.players.find((p) => p.seat === seat), hand = P.handCards || emptyHand(), out = {};
  const taken = new Set(S.claimed.map((c) => c.route));
  for (const rr of ROUTES) {
    const id = rr[0], r = route(id);
    if (taken.has(id)) continue;
    const twSeat = r.twin !== null ? (S.claimed.find((c) => c.route === r.twin) || {}).seat : undefined;
    if (twSeat !== undefined && S.players.length <= 3) { out[id] = { ok: false, short: 0, why: 'За столом на 2–3 парний шлях один, і його вже зайнято' }; continue; }
    if (twSeat === seat) { out[id] = { ok: false, short: 0, why: 'Обидва парні шляхи одному не можна' }; continue; }
    if (P.wagons < r.len) { out[id] = { ok: false, short: 0, why: 'Бракує возів: треба ' + r.len + ', є ' + P.wagons }; continue; }
    const L = r.len, wild = hand.wild || 0, combos = [];
    for (const c of r.cargo === GREY ? CARGO : [r.cargo]) {
      const h = hand[c] || 0;
      for (let n = Math.min(h, L); n >= 1; n--) if (L - n <= wild) combos.push({ c, n, w: L - n });
    }
    if (wild >= L) combos.push({ c: WILD, n: 0, w: L });
    combos.sort((a, b) => a.w - b.w || (a.c === WILD) - (b.c === WILD) || CARGO.indexOf(a.c) - CARGO.indexOf(b.c));
    if (combos.length) { out[id] = { ok: true, combos, best: 0 }; continue; }
    if (r.cargo === GREY) {
      const big = Math.max(...CARGO.map((c) => hand[c] || 0)), sh = L - big - wild;
      out[id] = { ok: false, short: sh, why: 'бракує ' + sh + ' ' + (sh === 1 ? 'карти' : 'карт') + ' одного вантажу' };
    } else {
      const sh = L - (hand[r.cargo] || 0) - wild;
      out[id] = { ok: false, short: sh, why: 'бракує ' + sh + ' ' + CARGO_GEN[r.cargo] };
    }
  }
  return out;
}
function connected(S, seat, a, b) {
  const mine = S.claimed.filter((c) => c.seat === seat).map((c) => route(c.route));
  const seen = new Set([a]), q = [a];
  while (q.length) { const u = q.pop(); for (const r of mine) { const v = r.a === u ? r.b : r.b === u ? r.a : null; if (v !== null && !seen.has(v)) { seen.add(v); q.push(v); } } }
  return seen.has(b);
}

// ─── Стан → вид ─────────────────────────────────────────────────────────
function toView(S, seat) {
  const players = S.players.map((p) => {
    const routes = S.claimed.filter((c) => c.seat === p.seat).sort((a, b) => a.at - b.at).map((c) => c.route);
    const used = routes.reduce((n, id) => n + route(id).len, 0);
    p.wagons = S.wagons - used;
    return {
      seat: p.seat, nick: p.nick, bot: p.bot ? p.nick : null, color: p.seat,
      hand: p.handCards ? CARDS.reduce((n, k) => n + (p.handCards[k] || 0), 0) : p.handN, tickets: p.tickets.length,
      wagons: p.wagons, points: routes.reduce((n, id) => n + ROUTE_POINTS[route(id).len], 0), road: road(routes), routes,
      ready: S.phase === 'setup' ? !!p.ready : true, away: !!p.away,
    };
  });
  const claimed = S.claimed.map((c) => ({ route: c.route, seat: c.seat })).sort((a, b) => a.route - b.route);
  const closed = S.players.length <= 3 ? claimed.map((c) => route(c.route).twin).filter((t) => t !== null) .sort((a, b) => a - b) : [];
  const maxRoad = Math.max(0, ...players.map((p) => p.road));
  const v = {
    v: 1, phase: S.phase, board: { sig: MAP_SIG, doubles: S.players.length <= 3 ? 'one' : 'both' },
    round: S.round, turns: S.turns, wagons: S.wagons, last: !!S.last, left: S.last ? S.left : null,
    players, order: S.players.map((p) => p.seat), first: S.first, turn: S.turn, claimed, closed,
    market: S.market.slice(), deck: S.deck, discard: S.discard, ticketsLeft: S.ticketsLeft,
    longest: { len: maxRoad, seats: maxRoad ? players.filter((p) => p.road === maxRoad).map((p) => p.seat) : [] },
    events: (S.events || []).slice(-40), seq: S.seq,
    deadline: S.timer === false || S.over ? null : new Date(Date.now() + (S.leftMs || 42000)).toISOString(), timerMs: S.timer === false ? null : 60000,
    bots: S.players.some((p) => p.bot) ? [0, 1, 2, 3, 4].map((i) => (S.players.find((p) => p.seat === i && p.bot) || {}).nick || null) : null,
    botIn: null, log: (S.log || []).slice(-12), over: S.over || null, me: null,
  };
  const P = seat == null ? null : S.players.find((p) => p.seat === seat && !p.bot);
  if (P) {
    const claims = legal(S, seat);
    const tickets = P.tickets.map((id) => {
      const t = ticket(id);
      if (connected(S, seat, t.a, t.b)) return { id, done: true, need: 0, path: [], dead: false };
      const d = dijkstra(S, seat, t.a, t.b);
      return d ? { id, done: false, need: d.need, path: d.path, dead: false } : { id, done: false, need: null, path: [], dead: true };
    });
    const pts = (l) => l.reduce((n, t) => n + ticket(t.id).pts, 0);
    const score = { routes: players.find((p) => p.seat === seat).points, done: pts(tickets.filter((t) => t.done)), open: pts(tickets.filter((t) => !t.done)) };
    score.total = score.routes + score.done - score.open;
    let must = null;
    if (S.phase === 'setup' && !P.ready) must = 'setup';
    else if (S.turn === seat && S.phase === 'play') must = 'act';
    else if (S.turn === seat && S.phase === 'draw') must = 'draw';
    else if (S.turn === seat && S.phase === 'keep') must = 'keep';
    const mine = must === 'act' || must === 'draw';
    const can = { draw: false, deck: false, market: [], tickets: false, claim: false, pass: false };
    const why = {};
    if (mine) {
      can.market = S.market.map((c, i) => (c && (must === 'act' || c !== WILD) ? i : -1)).filter((i) => i >= 0);
      can.deck = S.deck + S.discard > 0;
      can.draw = can.deck || can.market.length > 0;
      if (!can.draw) why.draw = 'Карт ніде нема';
    }
    if (must === 'act') {
      can.tickets = S.ticketsLeft > 0;
      can.claim = Object.values(claims).some((c) => c.ok);
      can.pass = !can.draw && !can.tickets && !can.claim;
      if (!can.tickets) why.tickets = 'Замовлень більше нема';
      if (!can.claim) why.claim = 'Жодного шляху зараз не потягнеш';
      if (!can.pass) why.pass = 'Пропускати не можна — є що робити';
    }
    let tip = null;
    if (must === 'act') {
      const plan = tickets.flatMap((t) => t.path);
      const id = plan.find((x) => claims[x] && claims[x].ok);
      if (id != null) { const c = claims[id].combos[claims[id].best]; tip = { act: 'claim', route: id, c: c.c, w: c.w }; }
      else {
        const slot = S.market.findIndex((c) => c && c !== WILD && plan.some((x) => route(x).cargo === c));
        tip = slot >= 0 ? { act: 'draw', slot } : can.deck ? { act: 'draw', deck: true } : can.tickets ? { act: 'tickets' } : { act: 'pass' };
      }
    } else if (must === 'draw') {
      const slot = can.market[0];
      tip = slot != null ? { act: 'draw', slot } : { act: 'draw', deck: true };
    } else if (must === 'setup' || must === 'keep') {
      const min = must === 'setup' ? 2 : 1;
      tip = { act: 'keep', ids: P.offer.slice().sort((a, b) => ticket(a).d - ticket(b).d).slice(0, min) };
    }
    v.me = {
      seat, hand: { ...emptyHand(), ...P.handCards }, tickets, score, must,
      offer: must === 'setup' || must === 'keep' ? P.offer.map((id) => { const t = ticket(id), d = dijkstra(S, seat, t.a, t.b); return { id, need: d ? d.need : null }; }) : [],
      keepMin: must === 'setup' ? 2 : must === 'keep' ? 1 : 0,
      can, why, claims, tip,
    };
  }
  Object.defineProperty(v, '_S', { value: S, enumerable: false });
  return v;
}

function base(nicks, opts = {}) {
  return {
    players: nicks.map((n, i) => ({ seat: i, nick: n, bot: /🤖/.test(n), handN: 6, tickets: [], ready: true, wagons: opts.wagons || 45 })),
    claimed: [], market: ['grain', 'wild', 'salt', 'honey', 'fish'], deck: 58, discard: 12, ticketsLeft: 28, wagons: opts.wagons || 45,
    phase: 'play', round: 1, turns: 0, first: 0, turn: 0, seq: 100, events: [], log: [], leftMs: 42000, ...opts,
  };
}
const put = (S, seat, ids) => { for (const id of ids) S.claimed.push({ route: id, seat, at: S.claimed.length }); };

// ─── Сценарії ───────────────────────────────────────────────────────────
function scenario(name) {
  switch (name) {
    case 'lobby': return null;
    case 'mid': case 'spectator': case 'other': case 'draw2': case 'keep': case 'empty': {
      // Вид із §5: 3 гравці, коло 14.
      const S = base(['Smaug', 'Глек 🤖', 'Оксана'], { round: 14, turns: 40, first: 2, turn: 0, deck: 45, discard: 38, ticketsLeft: 31, seq: 196, market: ['wild', 'fish', 'honey', 'wild', 'honey'] });
      put(S, 0, [57, 19, 21, 39, 41, 22]); put(S, 1, [9, 33, 1, 83, 84, 3]); put(S, 2, [36, 51, 48, 60, 85, 53]);
      S.players[0].handCards = { grain: 1, wool: 1, hides: 1, linen: 2, wild: 1 };
      S.players[0].tickets = [34, 15, 9];
      S.players[1].handN = 9; S.players[1].tickets = [1, 2, 3];
      S.players[2].handN = 7; S.players[2].handCards = { salt: 2, fish: 1, tar: 1, honey: 1, wool: 1, wild: 1 }; S.players[2].tickets = [24, 25, 26];
      S.log = ['Оксана займає Херсон — Перекоп (2 вози, +2 ⭐)', 'Глек 🤖 бере мед і карту наосліп', 'Smaug займає Біла Церква — Умань (2 вози, +2 ⭐)', 'Оксана бере сіль і карту наосліп'];
      // Перші дві виконані в макеті не з'єднані: тут — правда (34 Біла Церква — Кропивницький через 39 + 41).
      if (name === 'other') { S.turn = 1; S.leftMs = 18000; }
      if (name === 'draw2') { S.phase = 'draw'; S.market = ['wild', 'wild', 'honey', 'grain', 'tar']; S.leftMs = 12000; }
      if (name === 'keep') { S.phase = 'keep'; S.players[0].offer = [36, 27, 7]; S.leftMs = 15000; }
      if (name === 'empty') { S.deck = 0; S.discard = 0; S.market = [null, 'fish', null, null, 'tar']; }
      return S;
    }
    case 'setup': {
      const S = base(['Smaug', 'Глек 🤖', 'Оксана', 'Тарас'], { phase: 'setup', round: 0, turn: null, seq: 12, deck: 89, discard: 0, ticketsLeft: 28, market: ['salt', 'honey', 'wool', 'wild', 'tar'], leftMs: 54000 });
      S.players.forEach((p, i) => { p.handN = 4; p.tickets = []; p.ready = i === 1; });
      S.players[0].handCards = { salt: 1, grain: 1, tar: 1, wild: 1 }; S.players[0].offer = [8, 22, 37];
      S.log = ['Глек 🤖 обрав замовлення: лишає 2'];
      return S;
    }
    case 'start': {
      const S = base(['Smaug', 'Глек 🤖', 'Оксана', 'Макітра 🤖'], { round: 1, turns: 0, turn: 0, first: 0, seq: 30, deck: 89, discard: 0, ticketsLeft: 28 });
      S.players.forEach((p) => { p.handN = 4; p.tickets = [10, 11]; });
      S.players[0].handCards = { fish: 1, honey: 2, wild: 1 }; S.players[0].tickets = [19, 30];
      S.log = ['Глек 🤖 обрав замовлення: лишає 2', 'Оксана обрав замовлення: лишає 3', 'Макітра 🤖 обрав замовлення: лишає 2', 'Smaug обрав замовлення: лишає 2'];
      return S;
    }
    case 'last': {
      const S = base(['Smaug', 'Глек 🤖', 'Оксана'], { round: 38, turns: 110, first: 2, turn: 2, deck: 12, discard: 24, ticketsLeft: 8, last: true, left: 2, seq: 900, market: ['tar', 'fish', 'wild', 'linen', 'grain'] });
      put(S, 0, [0, 1, 3, 5, 6, 10, 12, 13, 15, 20, 24, 29, 28, 31]); put(S, 1, [59, 62, 64, 65, 70, 74, 73, 79, 75, 69, 67, 42, 40, 35]); put(S, 2, [37, 34, 32, 36, 44, 47, 46, 53, 56, 58, 54, 55, 48, 49, 52]);
      S.players[0].handCards = { fish: 3, grain: 2, wool: 4, wild: 2 }; S.players[0].tickets = [34, 15, 21, 32];
      S.players[1].handN = 3; S.players[1].tickets = [1, 2]; S.players[2].handN = 6; S.players[2].tickets = [24, 26];
      S.log = ['У Оксана лишилось 2 вози — кожен має ще один хід', 'Оксана займає Вознесенськ — Одеса (3 вози, +4 ⭐)'];
      return S;
    }
    case 'two': {
      const S = base(['Smaug', 'Оксана'], { round: 9, turns: 18, first: 1, turn: 0, seq: 150, deck: 60, discard: 14, ticketsLeft: 34 });
      put(S, 0, [38, 39, 34, 32]); put(S, 1, [59, 65, 53, 56]);
      S.players[0].handCards = { fish: 3, hides: 2, wool: 3, linen: 1, wild: 1 }; S.players[0].tickets = [16, 26];
      S.players[1].handN = 7; S.players[1].tickets = [13, 14];
      S.log = ['Оксана займає Перекоп — Сімферополь (3 вози, +4 ⭐)'];
      return S;
    }
    case 'five': case 'over': {
      const S = base(['Smaug', 'Глек 🤖', 'Оксана', 'Макітра 🤖', 'Тарас'], { round: 29, turns: 140, first: 3, turn: 0, seq: 1400, deck: 17, discard: 21, ticketsLeft: 22, market: ['honey', 'grain', 'tar', 'wool', 'wild'] });
      const budget = [36, 36, 36, 36, 36];
      let seat = 0;
      const pair = {};
      for (const rr of ROUTES) {
        const id = rr[0], r = route(id);
        if (budget.every((b) => b <= 0)) break;
        let s = seat;
        for (let k = 0; k < 5 && (budget[s] < r.len || (r.twin !== null && pair[r.twin] === s)); k++) s = (s + 1) % 5;
        if (budget[s] < r.len || (r.twin !== null && pair[r.twin] === s)) continue;
        S.claimed.push({ route: id, seat: s, at: S.claimed.length });
        budget[s] -= r.len; pair[id] = s;
        seat = (s + 1) % 5;
      }
      S.players[0].handCards = { salt: 2, grain: 3, honey: 1, wool: 2, linen: 1, wild: 2 }; S.players[0].tickets = [4, 19, 26, 33];
      S.players.forEach((p, i) => { if (i) { p.handN = 5 + i; p.tickets = [20 + i, 30 + i]; } });
      S.log = ['Тарас займає Вознесенськ — Миколаїв (2 вози, +2 ⭐)', 'Макітра 🤖 бере мед і карту наосліп', 'Глек 🤖 бере 2 карти наосліп'];
      if (name === 'over') {
        const V = toView(S, null);
        S.phase = 'over'; S.turn = null;
        const final = V.players.map((p, i) => {
          const tk = (p.seat === 0 ? [4, 19, 26, 33] : [20 + i, 30 + i]).map((id, k) => ({ id, done: k !== 1 }));
          const done = tk.filter((t) => t.done).reduce((n, t) => n + ticket(t.id).pts, 0), open = tk.filter((t) => !t.done).reduce((n, t) => n + ticket(t.id).pts, 0);
          const bonus = i === 2 ? 10 : 0;
          return { seat: p.seat, routes: p.points, done, open, bonus, total: p.points + done - open + bonus, road: p.road, tickets: tk };
        });
        const pts = final.map((f) => f.total);
        const win = final.slice().sort((a, b) => b.total - a.total)[0].seat;
        S.over = {
          winners: [win], reason: 'wagons', lastBy: 2, points: pts, final,
          longest: { len: V.players[2].road, seats: [2], routes: { 2: V.players[2].routes.slice(0, 4) } },
          stats: {
            rounds: 29, turns: 140, ms: 2840000,
            timeline: Array.from({ length: 30 }, (_, r) => [0, 1, 2, 3, 4].map((s) => Math.round(V.players[s].points * Math.pow(r / 29, 1.15 + s * 0.08)))),
            seats: V.players.map((p, i) => ({ seat: p.seat, claims: p.routes.length, wagonsUsed: 45 - p.wagons, biggest: Math.max(...p.routes.map((id) => route(id).len), 0), draws: 11 + i * 2, drawn: 22 + i * 3, facedown: 12 + i, faceWild: i, wildsPaid: 5 - i, ticketDraws: i ? 1 : 2, ticketsKept: i === 0 ? 4 : 2, blocks: i === 1 ? 2 : 0, crimea: i === 0 ? 2 : 0, firstClaim: 2 + i })),
          },
        };
        S.log = ['Найдовша валка — Оксана: ' + V.players[2].road + ' возів (+10 ⭐)', ...S.log];
      }
      return S;
    }
    default: throw new Error('нема сценарію ' + name);
  }
}

/** Вид сценарію для місця seat (null — глядач). */
export function makeView(name, seat = 0) {
  if (name === 'lobby') return { v: 1, phase: null, board: { sig: MAP_SIG, doubles: 'one' }, players: [], order: [], claimed: [], closed: [], market: [null, null, null, null, null], log: [], events: [], seq: 0, me: null, over: null };
  const S = scenario(name);
  const who = name === 'spectator' ? null : seat;
  return toView(S, who);
}

/** Подія §7: makeEvent('claim', { seq, seat, route, … }). Поля, яких не дали, — типові. */
export function makeEvent(k, o = {}) {
  const D = {
    round: { n: 2, first: 0 }, deal: { seat: 0, cards: 4, tickets: 3 }, setup: { seat: 1, kept: 2 }, turn: { seat: 0 },
    draw: { seat: 1, from: 'deck', slot: null, pick: 1, card: null }, refill: { slot: 0, card: 'salt' }, wipe: { cards: ['wild', 'wild', 'wild', 'salt', 'fish'] }, shuffle: { n: 38 },
    claim: { seat: 1, route: 34, c: 'hides', n: 3, w: 0, pts: 4, wagons: 30, road: 7 }, tickets: { seat: 1, n: 3, ids: null }, keep: { seat: 1, kept: 2, back: 1 }, done: { seat: 0, ticket: 34 },
    last: { seat: 2, wagons: 2 }, pass: { seat: 1 }, longest: { seats: [2], len: 12, routes: { 2: [36, 48, 51, 53] } }, over: { winners: [0], reason: 'wagons', points: [] },
  };
  return { k, seq: 0, ...(D[k] || {}), ...o };
}

/** Заглушка дій — лише щоб стенд ходив: той самий S, новий вид. { view } або { error }. */
export function applyAct(v, seat, action, p) {
  const S = v._S;
  if (!S) return { error: 'нема стану' };
  const P = S.players.find((x) => x.seat === seat);
  const ev = (k, o) => { S.events.push(makeEvent(k, { ...o, seq: ++S.seq })); };
  const refill = (slot) => { const pool = [...CARGO, 'wild']; const card = S.deck > 0 ? pool[(S.seq * 7 + slot) % pool.length] : null; S.market[slot] = card; if (card) S.deck--; ev('refill', { slot, card }); };
  const endTurn = () => {
    S.phase = 'play'; S.turns++;
    const i = S.players.indexOf(P);
    S.turn = S.players[(i + 1) % S.players.length].seat;
    ev('turn', { seat: S.turn });
  };
  switch (action) {
    case 'draw': {
      const first = S.phase === 'play';
      if (p.deck) {
        const pool = [...CARGO, 'wild'], card = pool[(S.seq * 3) % pool.length];
        P.handCards[card] = (P.handCards[card] || 0) + 1; S.deck--;
        ev('draw', { seat, from: 'deck', slot: null, pick: first ? 1 : 2, card });
        if (first) { S.phase = 'draw'; } else endTurn();
      } else {
        const card = S.market[p.slot];
        if (!card) return { error: 'На цьому місці порожньо' };
        if (!first && card === WILD) return { error: 'Вола з ринку другою картою не беруть' };
        P.handCards[card] = (P.handCards[card] || 0) + 1;
        ev('draw', { seat, from: 'market', slot: p.slot, pick: first ? 1 : 2, card });
        refill(p.slot);
        if (first && card !== WILD) S.phase = 'draw'; else endTurn();
      }
      S.log.push(P.nick + ' бере ' + (CARGO_ACC[p.slot != null ? 'salt' : 'salt']));
      break;
    }
    case 'claim': {
      const r = route(p.route), n = r.len - p.w;
      if (p.c !== WILD) P.handCards[p.c] -= n;
      P.handCards.wild -= p.w;
      S.claimed.push({ route: p.route, seat, at: S.claimed.length });
      ev('claim', { seat, route: p.route, c: p.c, n, w: p.w, pts: ROUTE_POINTS[r.len], wagons: P.wagons - r.len, road: 0 });
      S.log.push(P.nick + ' займає ' + routeName(p.route) + ' (' + r.len + ' возів, +' + ROUTE_POINTS[r.len] + ' ⭐)');
      for (const id of P.tickets) { const t = ticket(id); if (connected(S, seat, t.a, t.b) && !(S.doneT = S.doneT || new Set()).has(id)) { S.doneT.add(id); ev('done', { seat, ticket: id }); } }
      endTurn();
      break;
    }
    case 'tickets': {
      S.phase = 'keep'; P.offer = TICKETS.map((t) => t[0]).filter((id) => !P.tickets.includes(id)).slice(36 - (S.turns % 7), 39 - (S.turns % 7));
      S.ticketsLeft -= 3;
      ev('tickets', { seat, n: 3, ids: P.offer });
      break;
    }
    case 'keep': {
      P.tickets.push(...p.ids);
      if (S.phase === 'setup') { P.ready = true; ev('setup', { seat, kept: p.ids.length }); if (S.players.every((x) => x.ready || x.bot)) { S.phase = 'play'; S.round = 1; S.turn = 0; ev('round', { n: 1, first: 0 }); ev('turn', { seat: 0 }); } }
      else { ev('keep', { seat, kept: p.ids.length, back: 3 - p.ids.length }); S.log.push(P.nick + ' бере замовлення: лишає ' + p.ids.length + ' з 3'); endTurn(); }
      break;
    }
    case 'pass': ev('pass', { seat }); endTurn(); break;
    default: return { error: 'Тут так не ходять' };
  }
  return { view: toView(S, seat) };
}

/** Перелік для dev/hud-calc.test.mjs і стендів: кожне замовлення за назвою. */
export const ticketNames = () => TICKETS.map((t) => ticketName(t[0]));
export const cityName = (i) => city(i).name;
