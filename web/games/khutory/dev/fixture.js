// «Хутори» — підробні види для стендів (dev/scene.html, dev/hud.html), поки сервера ще нема.
// Форма — рівно як у контракті docs/games/specs/khutory.md, розділ «Вид». Геометрія — тим самим алгоритмом, що й на
// сервері (KhutoryBoard), але клієнт її ніколи не рахує сам: у грі все приходить у view.board.
//
//   import { makeView, SCENARIOS } from '/games/khutory/dev/fixture.js';
//   const view = makeView('main');   // setup | main | other | robber | steal | discard | trade | road2 | over | spectator

const S3 = Math.sqrt(3);

/** Геометрія поля радіуса 2: 19 гексів, 54 вершини, 72 ребра (див. контракт, «Геометрія»). */
export function geometry() {
  const hexes = [];
  for (let r = -2; r <= 2; r++)
    for (let q = Math.max(-2, -2 - r); q <= Math.min(2, 2 - r); q++) hexes.push({ q, r, v: [] });
  const vx = [], vkey = new Map(), edges = [], ekey = new Map(), ecount = [];
  for (const h of hexes) {
    const cx = S3 * (h.q + h.r / 2), cy = 1.5 * h.r;
    for (let i = 0; i < 6; i++) {
      const a = (-90 + 60 * i) * Math.PI / 180;
      const x = cx + Math.cos(a), y = cy + Math.sin(a);
      const k = Math.round(x * 1000) + ',' + Math.round(y * 1000);
      if (!vkey.has(k)) { vkey.set(k, vx.length); vx.push([round4(x), round4(y)]); }
      h.v.push(vkey.get(k));
    }
    for (let i = 0; i < 6; i++) {
      const a = Math.min(h.v[i], h.v[(i + 1) % 6]), b = Math.max(h.v[i], h.v[(i + 1) % 6]);
      const k = a + '-' + b;
      if (!ekey.has(k)) { ekey.set(k, edges.length); edges.push([a, b]); ecount.push(0); }
      ecount[ekey.get(k)]++;
    }
  }
  const coast = [];
  ecount.forEach((n, e) => { if (n === 1) coast.push(e); });
  const ang = (e) => {
    const [a, b] = edges[e];
    const mx = (vx[a][0] + vx[b][0]) / 2, my = (vx[a][1] + vx[b][1]) / 2;
    return (Math.atan2(my, mx) + Math.PI / 2 + 4 * Math.PI) % (2 * Math.PI);
  };
  coast.sort((a, b) => ang(a) - ang(b));
  return { hexes, vx, edges, coast };
}

function round4(x) { const r = Math.round(x * 10000) / 10000; return r === 0 ? 0 : r; }

/** Стартова карта (опція map=starter) — та сама, що на сервері. */
export const STARTER_T = ['forest', 'pasture', 'field', 'field', 'pit', 'pasture', 'mountain', 'mountain', 'field', 'steppe',
  'forest', 'pit', 'forest', 'mountain', 'pasture', 'field', 'pit', 'forest', 'pasture'];
export const STARTER_N = [10, 2, 9, 12, 6, 4, 10, 9, 11, null, 3, 8, 8, 3, 4, 5, 5, 6, 11];
export const PORT_SLOTS = [0, 3, 7, 10, 13, 17, 20, 23, 27];
export const STARTER_PORTS = ['any', 'wool', 'any', 'ore', 'grain', 'any', 'clay', 'wood', 'any'];

export function board() {
  const g = geometry();
  return {
    id: 'fx01',
    hexes: g.hexes.map((h, i) => ({ q: h.q, r: h.r, t: STARTER_T[i], num: STARTER_N[i], v: h.v.slice() })),
    vx: g.vx,
    edges: g.edges,
    ports: PORT_SLOTS.map((slot, i) => ({ e: g.coast[slot], kind: STARTER_PORTS[i], rate: STARTER_PORTS[i] === 'any' ? 3 : 2 })),
  };
}

export const SCENARIOS = ['setup', 'main', 'other', 'robber', 'steal', 'discard', 'trade', 'road2', 'over', 'spectator'];
// Торг і увага (khutory-look.md §1, §10): вхідна пропозиція, від Глека, обміняй і будуй, усі відмовились.
SCENARIOS.push('incoming', 'incbot', 'fixbuild', 'refused');

const NICKS = ['Smaug', 'владік', 'микола ( справжній )', null];
const BOT = 'Глек 🤖';

/** Сусіди вершини (через ребра) і ребра при вершині. */
function adjacency(b) {
  const nb = b.vx.map(() => []), ve = b.vx.map(() => []);
  b.edges.forEach(([a, c], e) => { nb[a].push(c); nb[c].push(a); ve[a].push(e); ve[c].push(e); });
  return { nb, ve };
}

/**
 * Вид для стенду. opts: { seat: 0 (хто дивиться; null — глядач), players: 4 }.
 * Фігури розставлено «як посеред партії»; правила відстані дотримано, решта — правдоподібно, не більше.
 */
export function makeView(scenario = 'main', opts = {}) {
  if (EXTRA[scenario]) return EXTRA[scenario](opts);
  const b = board();
  const { nb, ve } = adjacency(b);
  const seat = scenario === 'spectator' ? null : (opts.seat ?? 0);
  const n = opts.players ?? 4;
  const early = scenario === 'setup';

  // Хати й містечка: [гекс, кут, місце, містечко?]
  const placed = early
    ? [[4, 0, 1, false], [13, 3, 2, false]]
    : [[4, 0, 0, false], [7, 1, 0, false], [10, 2, 1, true], [16, 0, 1, false], [13, 4, 2, false], [18, 2, 2, true],
      [1, 1, 3, false], [6, 3, 3, false]].filter((p) => p[2] < n);
  const towns = placed.map(([h, c, s, city]) => ({ v: b.hexes[h].v[c], seat: s, city }));
  // Шляхи: від кожної хати по ребру, а дехто ще на крок далі.
  const roads = [];
  const taken = new Set();
  for (const t of towns) {
    const e = ve[t.v].find((x) => !taken.has(x));
    if (e === undefined) continue;
    taken.add(e); roads.push({ e, seat: t.seat });
    if (!early && t.seat !== 3) {
      const far = b.edges[e][0] === t.v ? b.edges[e][1] : b.edges[e][0];
      const e2 = ve[far].find((x) => !taken.has(x));
      if (e2 !== undefined) { taken.add(e2); roads.push({ e: e2, seat: t.seat }); }
    }
  }

  const townAt = new Map(towns.map((t) => [t.v, t]));
  const freeV = (v) => !townAt.has(v) && nb[v].every((u) => !townAt.has(u));
  const mineV = (v, s) => townAt.get(v)?.seat === s;
  const roadsOf = (s) => roads.filter((r) => r.seat === s);
  const touchesMine = (v, s) => mineV(v, s) || roadsOf(s).some((r) => b.edges[r.e].includes(v));

  const players = [];
  for (let s = 0; s < n; s++) {
    const nick = NICKS[s];
    players.push({
      seat: s, nick: nick ?? BOT, bot: nick ? null : BOT, color: s,
      cards: [9, 6, 5, 4][s], dev: [2, 1, 0, 1][s], knights: [1, 0, 2, 0][s],
      road: [3, 2, 6, 1][s], points: early ? 0 : [3, 4, 6, 2][s],
      left: { road: 15 - roadsOf(s).length, town: 5 - towns.filter((t) => t.seat === s && !t.city).length,
        city: 4 - towns.filter((t) => t.seat === s && t.city).length },
      away: false,
    });
  }
  const order = [0, 1, 2, 3].filter((s) => s < n);

  const turn = scenario === 'other' ? 1 : scenario === 'over' ? null : 0;
  const phase = { setup: 'setup', main: 'main', other: 'main', robber: 'robber', steal: 'steal', discard: 'discard',
    trade: 'main', road2: 'road2', over: 'over', spectator: 'main' }[scenario];

  const view = {
    v: 1,
    board: b,
    players, order, turn, phase,
    setup: early ? { step: 'town', round: 1 } : null,
    dice: early ? null : scenario === 'robber' || scenario === 'steal' || scenario === 'discard' ? [3, 4] : [4, 2],
    rolled: !early && scenario !== 'other',
    robber: scenario === 'steal' ? 13 : 9,
    towns, roads,
    bank: { wood: 12, clay: 14, wool: 11, grain: 9, ore: 15 },
    devLeft: 19,
    longest: early ? null : { seat: 2, len: 6 },
    army: null,
    target: 8,
    me: null,
    discard: scenario === 'discard' ? { 0: 4, 2: 3 } : null,
    offers: [],
    events: early ? [] : [
      { seq: 39, k: 'build', seat: 2, what: 'road', at: roads.find((r) => r.seat === 2)?.e ?? 0 },
      { seq: 40, k: 'roll', seat: 0, d: [4, 2], gains: [
        { hex: 4, seat: 0, res: 'clay', n: 1 }, { hex: 17, seat: 1, res: 'wood', n: 1 }, { hex: 17, seat: 2, res: 'wood', n: 1 }] },
    ],
    seq: early ? 2 : 40,
    deadline: scenario === 'over' ? null : new Date(Date.now() + 75000).toISOString(),
    timerMs: 90000,
    bots: n > 3 ? [null, null, null, BOT] : null,
    botIn: null,
    over: null,
    log: early ? ['Глек: «Ставте хати, хлопці. Перший — Smaug»'] : [
      'Smaug кидає 4 + 2 = 6', 'Smaug бере 1 глину', 'владік бере 1 дерево', 'микола ( справжній ) бере 1 дерево',
    ],
  };

  if (scenario === 'trade') {
    view.offers = [
      { id: 1, from: 0, to: null, give: { wood: 1 }, get: { ore: 1 }, acc: [1], dec: [3] },
      { id: 2, from: 2, to: 0, give: { grain: 2 }, get: { wood: 1 }, acc: [], dec: [] },
    ];
  }

  if (scenario === 'over') {
    view.over = {
      winner: 2, reason: 'points',
      points: [6, 5, 8, 3].slice(0, n),
      stats: {
        dice: [1, 3, 4, 6, 7, 9, 6, 5, 4, 2, 1],   // скільки разів випало 2..12
        sevens: 9,
        rounds: 14,
        timeline: [[0, 0, 0, 0], [2, 2, 2, 2], [3, 2, 3, 2], [3, 3, 4, 2], [4, 3, 4, 3], [5, 4, 6, 3], [6, 5, 8, 3]]
          .map((r) => r.slice(0, n)),
        seats: order.map((s) => ({ seat: s, trades: [4, 2, 6, 1][s], got: [31, 26, 38, 19][s], stolen: [1, 2, 0, 3][s],
          built: { road: [5, 4, 9, 2][s], town: [3, 2, 2, 2][s], city: [0, 1, 2, 0][s] } })),
      },
    };
  }

  if (seat !== null && seat < n) {
    const me = {
      seat,
      hand: scenario === 'discard' ? { wood: 3, clay: 2, wool: 1, grain: 2, ore: 1 } : { wood: 3, clay: 2, wool: 2, grain: 2, ore: 0 },
      dev: [{ k: 'knight', fresh: false }, { k: 'vp', fresh: true }],
      points: (players[seat]?.points ?? 0) + 1,
      playedDev: false,
      rates: { wood: 4, clay: 3, wool: 3, grain: 3, ore: 4 },
      must: null,
      spots: null,
      can: { road: true, town: true, city: false, dev: false },
      why: { city: 'бракує 2 зерна й 3 заліза', dev: 'бракує 1 заліза' },
      discard: 0,
      robber: null,
      steal: null,
    };
    const myTurn = turn === seat;
    if (scenario === 'setup') {
      me.must = 'town';
      me.spots = { town: b.vx.map((_, v) => v).filter(freeV), road: [], city: [] };
      me.hand = { wood: 0, clay: 0, wool: 0, grain: 0, ore: 0 };
      me.dev = [];
      me.can = { road: false, town: false, city: false, dev: false };
      me.why = {};
    } else if (myTurn && (scenario === 'main' || scenario === 'trade' || scenario === 'road2')) {
      me.must = scenario === 'road2' ? 'road' : null;
      me.spots = {
        town: b.vx.map((_, v) => v).filter((v) => freeV(v) && touchesMine(v, seat) && !mineV(v, seat)),
        road: b.edges.map((_, e) => e).filter((e) => !taken.has(e) && b.edges[e].some((v) => touchesMine(v, seat))),
        city: towns.filter((t) => t.seat === seat && !t.city).map((t) => t.v),
      };
    } else if (scenario === 'robber') {
      me.must = 'robber';
      me.robber = b.hexes.map((_, h) => h).filter((h) => h !== view.robber);
    } else if (scenario === 'steal') {
      me.must = 'steal';
      me.steal = [1, 2].filter((s) => s < n);
    } else if (scenario === 'discard') {
      me.must = 'discard';
      me.discard = 4;
    } else if (scenario === 'main' && !myTurn) {
      me.must = null;
    }
    if (scenario === 'other' || scenario === 'over') { me.spots = null; me.must = null; }
    if (scenario === 'main' && myTurn) {
      // Ще й нерозіграний козак — показати кнопку «Зіграти».
      me.dev = [{ k: 'knight', fresh: false }, { k: 'vp', fresh: true }, { k: 'plenty', fresh: false }];
    }
    view.me = me;
  }
  return view;
}

/** Нові сценарії — латками поверх наявних (самі наявні не міняються). */
const EXTRA = {
  // Хід Smaug (місце 0); дивиться владік (місце 1): дві пропозиції від Smaug — усім і особисто йому.
  incoming(opts) {
    const v = makeView('main', { ...opts, seat: 0 });
    const seat = opts.seat ?? 1;
    v.offers = [
      { id: 7, from: 0, to: null, give: { grain: 1 }, get: { wood: 1 }, acc: [], dec: [] },
      { id: 9, from: 0, to: seat, give: { ore: 1 }, get: { clay: 1, wool: 1 }, acc: [], dec: [] },
    ];
    return seatView(v, seat);
  },
  // Хід Глека (місце 3) — він пропонує обмін усім і чекає відповіді (botIn — до кінця очікування, 10 с).
  incbot(opts) {
    const v = makeView('main', { ...opts, seat: 0 });
    v.turn = 3;
    v.botIn = 8000;
    v.offers = [{ id: 11, from: 3, to: null, give: { wool: 1 }, get: { grain: 1 }, acc: [], dec: [] }];
    return seatView(v, opts.seat ?? 1);
  },
  // Мій хід: хаті бракує 1 вовни, зерна 4 при курсі 3 — «🔁 3🌾→🧶 і будуй»; містечку бракує 2 заліза — дерева 9 (4:1).
  fixbuild(opts) {
    const v = makeView('main', opts);
    if (v.me) {
      v.me.hand = { wood: 9, clay: 1, wool: 0, grain: 4, ore: 1 };
      v.me.rates = { wood: 4, clay: 4, wool: 4, grain: 3, ore: 4 };
      v.me.can = { road: true, town: false, city: false, dev: false };
      v.me.why = { town: 'бракує 1 вовни', city: 'бракує 2 заліза', dev: 'бракує 1 вовни' };
    }
    return v;
  },
  // Моя пропозиція — усі відмовились.
  refused(opts) {
    const v = makeView('main', opts);
    v.offers = [{ id: 3, from: 0, to: null, give: { clay: 1 }, get: { wool: 1 }, acc: [], dec: [1, 2, 3] }];
    if (v.me) v.me.hand = { wood: 2, clay: 1, wool: 0, grain: 4, ore: 0 };   // 3 зерна — у банк на вовну
    return v;
  },
};

/** Той самий стіл очима іншого місця: me — з руки «чужого» гравця, без своїх місць і без ходу. */
function seatView(v, seat) {
  if (seat == null) { v.me = null; return v; }
  const me = { ...v.me, seat, must: null, spots: null, can: { road: false, town: false, city: false, dev: false }, why: {},
    hand: { wood: 3, clay: 2, wool: 1, grain: 2, ore: 0 }, dev: [{ k: 'knight', fresh: false }], points: (v.players[seat]?.points ?? 0) };
  v.me = me;
  return v;
}
