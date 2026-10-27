// «Колія» K34 — бот-напарник (createBot(p, { buddy: true })): доповнює людину, а не грає за себе.
import test from 'node:test';
import assert from 'node:assert/strict';
import * as R from '../../web/games/koliya/rules.js';
import { createBot } from '../../web/games/koliya/bot.js';
import { playMix } from './bot-human.mjs';

const { I, O, U, TPS } = R;
const cellOf = (s, q) => Math.floor(q.y / U) * s.w + Math.floor(q.x / U);

test('напарник з імітацією людини: перебиває в рази менше за старого бота й допомагає (далі за соло)', () => {
  let old = 0, bud = 0, xo = 0, xb = 0, xs = 0;
  for (const seed of [1, 2, 3]) {
    const a = playMix(seed, 'old', { maxMin: 8 }), b = playMix(seed, 'buddy', { maxMin: 8 }), c = playMix(seed, 'none', { maxMin: 8 });
    old += a.sum; bud += b.sum; xo += a.headX; xb += b.headX; xs += c.headX;
    console.log(`сід ${seed}: старий ${a.status} x=${a.headX} перебив ${a.sum}; напарник ${b.status} x=${b.headX} перебив ${b.sum} ${JSON.stringify(b.it)}; соло x=${c.headX}`);
  }
  assert.ok(bud * 3 <= old, `перебивань: напарник ${bud}, старий ${old}`);
  assert.ok(xb > xs, `напарник має допомагати: x ${xb} проти соло ${xs}`);
});

test('напарник не б\'є дерево, яке рубає людина, і не бере сокиру з-під її рук', () => {
  const s = R.newGame(7, R.defaultParams(), 2);
  const b = createBot(1, { buddy: true });
  // людина з сокирою стоїть біля дерева й б'є його; поруч лежить ще одна сокира — її не чіпати
  const hp = s.players[0];
  let tree = -1;
  for (let c = 0; c < s.obj.length && tree < 0; c++) {
    const x = c % s.w, y = (c / s.w) | 0;
    if (s.obj[c] === O.TREE && x > 6 && x + 1 < s.w && s.obj[c - 1] === O.NONE && s.ground[c - 1] === R.G.GRASS && s.drop[c - 1] === I.NONE && s.obj[c - 2] === O.NONE && s.drop[c - 2] === I.NONE && s.ground[c - 2] === R.G.GRASS) tree = c;
  }
  assert.ok(tree >= 0);
  hp.x = ((tree - 1) % s.w) * U + U / 2; hp.y = (((tree - 1) / s.w) | 0) * U + U / 2; hp.fx = 1; hp.fy = 0;
  hp.hold = { k: I.AXE, n: 1 };
  s.drop[tree - 2] = I.AXE; s.dropN[tree - 2] = 1;
  const bp = s.players[1];
  bp.x = hp.x - 4 * U; bp.y = hp.y;
  let botHit = 0, tookAxe = 0;
  for (let t = 0; t < 10 * TPS && s.status === 'run'; t++) {
    const cmds = [];
    if (t % 8 === 0) cmds.push({ p: 0, k: 'use' });
    const v = b.think(s);
    cmds.push({ p: 1, k: 'in', mx: v.mx | 0, my: v.my | 0 });
    if (v.use) cmds.push({ p: 1, k: 'use' });
    R.step(s, cmds);
    for (const e of s.ev) if (e.p === 1) {
      if ((e.t === 'chop') && e.y * s.w + e.x === tree) botHit++;
      if (e.t === 'pick' && e.y * s.w + e.x === tree - 2) tookAxe++;
    }
  }
  assert.equal(botHit, 0, 'напарник бив дерево людини');
  assert.equal(tookAxe, 0, 'напарник узяв сокиру біля людини');
});

test('напарник не стоїть на клітинці перед людиною — відходить убік', () => {
  const s = R.newGame(7, R.defaultParams(), 2);
  const b = createBot(1, { buddy: true });
  const hp = s.players[0], bp = s.players[1];
  bp.x = hp.x + hp.fx * U; bp.y = hp.y + hp.fy * U;     // бот рівно перед людиною
  const front = () => (Math.floor(hp.y / U) + hp.fy) * s.w + Math.floor(hp.x / U) + hp.fx;
  let inFront = 0;
  for (let t = 0; t < 3 * TPS; t++) {
    const v = b.think(s);
    R.step(s, [{ p: 1, k: 'in', mx: v.mx | 0, my: v.my | 0 }]);
    if (t >= TPS && cellOf(s, bp) === front()) inFront++;
  }
  assert.equal(inFront, 0, `бот стояв перед людиною ${inFront} тиків`);
});

test('гудок людини поруч — напарник іде до неї', () => {
  const s = R.newGame(11, R.defaultParams(), 2);
  const b = createBot(1, { buddy: true });
  const hp = s.players[0], bp = s.players[1];
  // людина відходить на 8 клітинок уздовж смуги (без бота), потім гуде
  for (let t = 0; t < 40 && s.status === 'run'; t++) R.step(s, [{ p: 0, k: 'in', mx: 100, my: 0 }]);
  R.step(s, [{ p: 0, k: 'in', mx: 0, my: 0 }]);
  const dist = () => Math.abs(cellOf(s, hp) % s.w - cellOf(s, bp) % s.w) + Math.abs(((cellOf(s, hp) / s.w) | 0) - ((cellOf(s, bp) / s.w) | 0));
  const d0 = dist();
  assert.ok(d0 >= 4 && d0 <= 12, `відстань ${d0}`);
  R.step(s, [{ p: 0, k: 'horn' }]);
  let near = false;
  for (let t = 0; t < 6 * TPS && s.status === 'run'; t++) {
    const v = b.think(s);
    R.step(s, [{ p: 1, k: 'in', mx: v.mx | 0, my: v.my | 0 }, ...(v.use ? [{ p: 1, k: 'use' }] : [])]);
    if (dist() <= 2) { near = true; break; }
  }
  assert.ok(near, `бот не підійшов (було ${d0}, стало ${dist()})`);
});

test('напарник детермінований і без людей грає як звичайний бот', () => {
  const a = playMix(5, 'buddy', { maxMin: 2 }), b = playMix(5, 'buddy', { maxMin: 2 });
  assert.equal(R.hash(a.s), R.hash(b.s));
  const run = (opts) => {
    const s = R.newGame(9, R.defaultParams(), 2);
    const bots = [createBot(0, opts), createBot(1, opts)];
    for (let t = 0; t < 60 * TPS && s.status === 'run'; t++) {
      const cmds = [];
      for (const q of bots) { const v = q.think(s); cmds.push({ p: q.p, k: 'in', mx: v.mx | 0, my: v.my | 0 }); if (v.use) cmds.push({ p: q.p, k: 'use' }); }
      R.step(s, cmds);
    }
    return R.hash(s);
  };
  assert.equal(run({ buddy: true }), run({}));
});
