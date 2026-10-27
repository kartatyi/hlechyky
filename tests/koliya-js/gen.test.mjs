// «Колія» — генератор смуги (K1): детермінізм, прохідність, межі параметрів.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { generate, findRoute, MAX_WATER_RUN } from '../../web/games/koliya/gen.js';
import { defaultParams, PARAM_META, G, O, I, WAGONS } from '../../web/games/koliya/rules.js';

const P = (o = {}) => ({ ...defaultParams(), ...o });

function check(wd, p, label) {
  const { w, h, ground, obj, hp, drop, path, spawn } = wd;
  assert.equal(w, p.stripLen | 0, label); assert.equal(h, p.stripWidth | 0, label);
  for (const a of [ground, obj, hp, drop, wd.dropN]) assert.equal(a.length, w * h, label);
  for (let x = 0; x < w; x++) { assert.equal(obj[x], O.WALL, label); assert.equal(obj[(h - 1) * w + x], O.WALL, label); }
  // колія: ≥ вагонів + 2, суміжні клітинки, чисті
  assert.ok(path.length >= WAGONS.length + 2, label);
  for (let k = 0; k < path.length; k++) {
    const c = path[k]; assert.equal(obj[c], O.NONE, label); assert.notEqual(ground[c], G.WATER, label);
    if (k) { const a = path[k - 1]; assert.equal(Math.abs(a % w - c % w) + Math.abs(((a / w) | 0) - ((c / w) | 0)), 1, label); }
  }
  // інструменти на підлозі, місця появи вільні
  for (const k of [I.AXE, I.PICK, I.BUCKET]) assert.ok(drop.includes(k), `${label}: нема ${k}`);
  assert.ok(spawn.length >= 4, label);
  for (const s of spawn) { const i = s.y * w + s.x; assert.equal(obj[i], O.NONE, label); assert.notEqual(ground[i], G.WATER, label); }
  // станція є біля кінця
  let st = 0; for (let i = 0; i < w * h; i++) if (ground[i] === G.STATION) { st++; assert.ok(i % w >= w - 4, label); }
  assert.ok(st >= 3, label);
  // hp
  for (let i = 0; i < w * h; i++) {
    if (obj[i] === O.TREE) assert.equal(hp[i], p.treeHits, label);
    else if (obj[i] === O.ROCK) assert.equal(hp[i], p.rockHits, label);
  }
  // маршрут і ресурси на нього
  const route = findRoute(wd);
  assert.ok(route, `${label}: нема маршруту`);
  let run = 0;
  for (const c of route) { run = ground[c] === G.WATER ? run + 1 : 0; assert.ok(run <= MAX_WATER_RUN, label); }
  const bridges = route.filter((c) => ground[c] === G.WATER).length;
  let trees = 0, rocks = 0;
  for (let i = 0; i < w * h; i++) { if (obj[i] === O.TREE) trees++; else if (obj[i] === O.ROCK) rocks++; }
  assert.ok(trees * p.woodPerTree >= route.length + bridges, `${label}: дерева ${trees} на ${route.length}+${bridges}`);
  assert.ok(rocks * p.ironPerRock >= route.length, `${label}: заліза ${rocks} на ${route.length}`);
  return { route, trees, rocks };
}

test('детермінізм: той самий сід — той самий світ, інший — інший', () => {
  const p = P();
  const a = generate(123, p), b = generate(123, p), c = generate(124, p);
  for (const k of ['ground', 'obj', 'hp', 'drop', 'dropN']) assert.deepEqual(a[k], b[k]);
  assert.deepEqual(a.path, b.path);
  assert.notDeepEqual(a.obj, c.obj);
});

test('без Math.random', () => {
  const orig = Math.random;
  Math.random = () => { throw new Error('Math.random у генераторі'); };
  try { generate(5, P()); } finally { Math.random = orig; }
});

test('прохідність і ресурси на 30 сідах (типові параметри)', () => {
  const p = P();
  for (let s = 1; s <= 30; s++) check(generate(s * 7919, p), p, `сід ${s * 7919}`);
});

test('ліс і скелі кластерами, частки близькі до заданих', () => {
  const p = P();
  const wd = generate(42, p), { w, h, obj, ground } = wd;
  let t = 0, same = 0, wat = 0;
  for (let y = 1; y < h - 1; y++) for (let x = 1; x < w - 1; x++) {
    const i = y * w + x;
    if (ground[i] === G.WATER) wat++;
    if (obj[i] !== O.TREE) continue;
    t++;
    if (obj[i - 1] === O.TREE || obj[i + 1] === O.TREE || obj[i - w] === O.TREE || obj[i + w] === O.TREE) same++;
  }
  const area = (w - 15) * (h - 2);
  assert.ok(t > area * p.forest * 0.85 && t < area * p.forest * 1.3, `дерев ${t}`);
  assert.ok(same / t > 0.85, `самотніх дерев забагато: ${same}/${t}`);
  assert.ok(wat > area * p.water * 0.6, `води ${wat}`);
});

test('межі параметрів «Світ»', () => {
  const keys = ['stripLen', 'stripWidth', 'forest', 'rocks', 'water'];
  const combos = [];
  for (const k of keys) for (const v of [PARAM_META[k].min, PARAM_META[k].max]) combos.push({ [k]: v });
  const lo = {}, hi = {};
  for (const k of keys) { lo[k] = PARAM_META[k].min; hi[k] = PARAM_META[k].max; }
  combos.push(lo, hi, { ...hi, stripLen: 150 }, { forest: 0, rocks: 0, water: 0.4 }, { woodPerTree: 5, ironPerRock: 5, treeHits: 10 });
  for (const o of combos) for (const s of [3, 77, 1001]) check(generate(s, P(o)), P(o), `${JSON.stringify(o)} сід ${s}`);
});
