// «Колія» — генератор смуги (K1 → K7): детермінізм, прохідність, межі повзунків, біоми, ресурси вздовж маршруту.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { generate, findRoute, MAX_WATER_RUN, RES_R, BIOMES, BIOMES_NEW, GLADE, STATION_W } from '../../web/games/koliya/gen.js';
import { defaultParams, newGame, step, PARAM_META, G, O, I, U, WAGONS } from '../../web/games/koliya/rules.js';

const P = (o = {}) => ({ ...defaultParams(), stripLen: 150, ...o });   // K13: типова смуга тепер 100 — властивості генератора міряємо на 150
const SEEDS50 = Array.from({ length: 50 }, (_, k) => (k + 1) * 7919);

function check(wd, p, label) {
  const { w, h, ground, obj, hp, drop, path, spawn } = wd;
  assert.equal(w, p.stripLen | 0, label); assert.equal(h, p.stripWidth | 0, label);
  for (const a of [ground, obj, hp, drop, wd.dropN]) assert.equal(a.length, w * h, label);
  for (let x = 0; x < w; x++) { assert.equal(obj[x], O.WALL, label); assert.equal(obj[(h - 1) * w + x], O.WALL, label); }
  for (let i = 0; i < w * h; i++) assert.ok(ground[i] <= G.CHASM, `${label}: невідома підлога ${ground[i]}`);   // K26: + лід, сніг, урвище
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

/** Відстань (BFS крізь усе, крім WALL) від кожної клітинки до найближчого obj = kind */
function distTo(wd, kind) {
  const { w, h, obj } = wd, n = w * h, d = new Int32Array(n).fill(1e9), q = [];
  for (let i = 0; i < n; i++) if (obj[i] === kind) { d[i] = 0; q.push(i); }
  for (let k = 0; k < q.length; k++) {
    const c = q[k], x = c % w, y = (c / w) | 0;
    for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
      const nx = x + dx, ny = y + dy;
      if (nx < 0 || ny < 1 || nx >= w || ny > h - 2) continue;
      const m = ny * w + nx;
      if (obj[m] === O.WALL || d[m] <= d[c] + 1) continue;
      d[m] = d[c] + 1; q.push(m);
    }
  }
  return d;
}

test('детермінізм: той самий сід — той самий світ, інший — інший', () => {
  const p = P();
  const a = generate(123, p), b = generate(123, p), c = generate(124, p);
  for (const k of ['ground', 'obj', 'hp', 'drop', 'dropN']) assert.deepEqual(a[k], b[k]);
  assert.deepEqual(a.path, b.path);
  assert.deepEqual(a.zones, b.zones);
  assert.notDeepEqual(a.obj, c.obj);
});

test('без Math.random', () => {
  const orig = Math.random;
  Math.random = () => { throw new Error('Math.random у генераторі'); };
  try { generate(5, P()); generate(6, P({ hardGrow: 1.5, swamp: 0.4 })); } finally { Math.random = orig; }
});

test('прохідність і ресурси на 50 сідах (типові параметри)', () => {
  const p = P();
  for (const s of SEEDS50) check(generate(s, p), p, `сід ${s}`);
});

test('ліс і скелі кластерами, частки близькі до заданих; заліза менше за дерево', () => {
  const p = P();
  let T = 0, R = 0;
  for (const seed of [42, 43, 44, 45]) {
    const wd = generate(seed, p), { w, h, obj, ground } = wd;
    let t = 0, same = 0, wat = 0, q = 0;
    for (let y = 1; y < h - 1; y++) for (let x = 1; x < w - 1; x++) {
      const i = y * w + x;
      if (ground[i] === G.WATER) wat++;
      if (obj[i] === O.ROCK) q++;
      if (obj[i] !== O.TREE) continue;
      t++;
      if (obj[i - 1] === O.TREE || obj[i + 1] === O.TREE || obj[i - w] === O.TREE || obj[i + w] === O.TREE) same++;
    }
    const area = (w - 15) * (h - 2);
    // частка за біомами гуляє (гай густий, степ рідкий) — окремий сід у межах 0,5..1,6, середнє — нижче
    assert.ok(t > area * p.forest * 0.5 && t < area * p.forest * 1.6, `сід ${seed}: дерев ${t}`);
    assert.ok(same / t > 0.8, `сід ${seed}: самотніх дерев забагато: ${same}/${t}`);
    assert.ok(wat > area * p.water * 0.5, `сід ${seed}: води ${wat}`);
    T += t; R += q;
  }
  const A = 4 * (p.stripLen - 15) * (p.stripWidth - 2);
  assert.ok(T > A * p.forest * 0.7 && T < A * p.forest * 1.35, `у середньому дерев ${T / 4}`);
  assert.ok(R < T, `заліза ${R} не менше за дерева ${T}`);
});

test('межі повзунків «Світ» (і крайні разом)', () => {
  const keys = Object.keys(PARAM_META).filter((k) => PARAM_META[k].group === 'Світ');
  for (const k of ['zoneLen', 'hardGrow', 'swamp', 'swampSlow']) assert.ok(keys.includes(k), `нема повзунка ${k}`);
  const combos = [];
  for (const k of keys) for (const v of [PARAM_META[k].min, PARAM_META[k].max]) combos.push({ [k]: v });
  const lo = {}, hi = {};
  for (const k of keys) { lo[k] = PARAM_META[k].min; hi[k] = PARAM_META[k].max; }
  combos.push(lo, hi, { ...hi, stripLen: 150 }, { forest: 0, rocks: 0, water: 0.4 }, { woodPerTree: 5, ironPerRock: 5, treeHits: 10 },
    { zoneLen: 12, hardGrow: 1.5, stripWidth: 10 }, { zoneLen: 12, hardGrow: 1.5, swamp: 0.4, water: 0.4, rocks: 0.6 });
  for (const o of combos) for (const s of [3, 77, 1001]) check(generate(s, P(o)), P(o), `${JSON.stringify(o)} сід ${s}`);
});

test('біоми: суцільні ділянки, перша легка, поспіль різні, складність росте, усі шість трапляються', () => {
  const p = P();
  const seen = {};
  for (const s of SEEDS50) {
    const wd = generate(s, p), z = wd.zones;
    assert.ok(z.length >= 3, `сід ${s}: ділянок ${z.length}`);
    assert.equal(z[0].x0, GLADE); assert.equal(z[z.length - 1].x1, wd.w - STATION_W - 3);
    assert.ok(['steppe', 'grove'].includes(z[0].kind), `сід ${s}: перша ${z[0].kind}`);
    for (let k = 0; k < z.length; k++) {
      assert.ok(BIOMES.includes(z[k].kind) || BIOMES_NEW.includes(z[k].kind));   // K26
      seen[z[k].kind] = (seen[z[k].kind] || 0) + 1;
      if (k) {
        assert.equal(z[k].x0, z[k - 1].x1);
        assert.notEqual(z[k].kind, z[k - 1].kind, `сід ${s}: двічі ${z[k].kind}`);
        assert.ok(z[k].d >= z[k - 1].d);
      }
      assert.ok(z[k].x1 - z[k].x0 >= 15, `сід ${s}: коротка ділянка ${z[k].x1 - z[k].x0}`);
    }
    assert.ok(new Set(z.map((q) => q.kind)).size >= Math.min(3, z.length), `сід ${s}: мало різних біомів`);
  }
  for (const b of BIOMES) assert.ok(seen[b] >= 10, `біом ${b} лише ${seen[b] | 0} раз на 50 сідах`);
  // довжина ділянки керує їх кількістю
  assert.ok(generate(9, P({ zoneLen: 12 })).zones.length > generate(9, P({ zoneLen: 60 })).zones.length);
  assert.equal(generate(9, P({ zoneLen: 80 })).zones.length, 2);
});

test('ресурси вздовж будь-якого маршруту: дерево й залізо не далі за RES_R від кожної прохідної клітинки', () => {
  for (const o of [{}, { forest: 0, rocks: 0 }, { forest: 0.05, rocks: 0.02, hardGrow: 1.5 }, { stripWidth: 40, zoneLen: 12 }]) {
    const p = P(o);
    for (const s of SEEDS50.slice(0, o.forest === undefined ? 50 : 10)) {
      const wd = generate(s, p), { w, h, obj, ground } = wd;
      const dT = distTo(wd, O.TREE), dR = distTo(wd, O.ROCK);
      for (let y = 1; y < h - 1; y++) for (let x = GLADE; x < w; x++) {
        const i = y * w + x;
        if (obj[i] === O.WALL || ground[i] === G.WATER) continue;
        assert.ok(dT[i] <= RES_R, `${JSON.stringify(o)} сід ${s}: до дерева ${dT[i]} з (${x},${y})`);
        assert.ok(dR[i] <= RES_R, `${JSON.stringify(o)} сід ${s}: до заліза ${dR[i]} з (${x},${y})`);
      }
    }
  }
});

test('річки: є короткі переправи (брід чи міст на 1–2) і довгі плеса; вода для відра щонайменше кожні ~25 стовпців', () => {
  const p = P();
  let rivers = 0, short = 0, long = 0;
  for (const s of SEEDS50) {
    const wd = generate(s, p), { w, h, ground } = wd;
    for (const z of wd.zones) {
      if (z.kind !== 'river') continue;
      rivers++;
      // для кожного рядка — найдовша вода поспіль у межах ділянки
      let mn = 99, mx = 0;
      for (let y = 1; y < h - 1; y++) {
        let run = 0, best = 0;
        for (let x = z.x0; x < z.x1; x++) { run = ground[y * w + x] === G.WATER ? run + 1 : 0; if (run > best) best = run; }
        mn = Math.min(mn, best); mx = Math.max(mx, best);
      }
      if (mn <= 2) short++;
      if (mx >= 3) long++;
    }
    const X1 = w - STATION_W - 3;
    let last = GLADE;
    for (let x = GLADE; x < X1; x++) {
      let has = false; for (let y = 1; y < h - 1; y++) if (ground[y * w + x] === G.WATER) has = true;
      if (has) last = x;
      assert.ok(x - last <= 25, `сід ${s}: без води від ${last} до ${x}`);
    }
  }
  assert.ok(rivers >= 15, `річкових долин ${rivers}`);
  assert.ok(short >= rivers * 0.9, `коротких переправ ${short}/${rivers}`);
  assert.ok(long >= rivers * 0.6, `довгих плес ${long}/${rivers}`);
});

test('болото: є в болотяних ділянках і латками; частка 0 — нема; рейки класти можна', () => {
  let zs = 0;
  for (const s of SEEDS50.slice(0, 20)) {
    const wd = generate(s, P()), { w, h, ground } = wd;
    let all = 0; for (let i = 0; i < w * h; i++) if (ground[i] === G.SWAMP) all++;
    assert.ok(all >= (w - 15) * (h - 2) * 0.06 * 0.9, `сід ${s}: болота ${all}`);
    for (const z of wd.zones) {
      if (z.kind !== 'swamp') continue;
      zs++;
      let c = 0, sw = 0;
      for (let y = 1; y < h - 1; y++) for (let x = z.x0; x < z.x1; x++) { c++; if (ground[y * w + x] === G.SWAMP) sw++; }
      assert.ok(sw > c * 0.35, `сід ${s}: у болоті лише ${sw}/${c}`);
    }
    const none = generate(s, P({ swamp: 0 }));
    assert.ok(!none.ground.includes(G.SWAMP), `сід ${s}: болото при частці 0`);
    assert.ok(!none.zones.some((z) => z.kind === 'swamp'));
  }
  assert.ok(zs >= 5, `болотяних ділянок ${zs}`);
});

test('проходи між скелями: хребет WALL з пастками, а маршрут усе одно є', () => {
  let n = 0;
  for (const s of SEEDS50) {
    const wd = generate(s, P({ hardGrow: 1, winter: 'off' }));   // K26: світ сідів — як до зими (сід 110866 інакше без пастки)
    for (const z of wd.zones) {
      if (z.kind !== 'pass') continue;
      n++;
      let walls = 0;
      for (let y = 1; y < wd.h - 1; y++) for (let x = z.x0; x < z.x1; x++) if (wd.obj[y * wd.w + x] === O.WALL) walls++;
      assert.ok(walls >= (wd.h - 2) * 2, `сід ${s}: хребет ${walls}`);
      assert.ok(z.traps >= 1, `сід ${s}: без глухих кутів`);
    }
    assert.ok(findRoute(wd), `сід ${s}`);
  }
  assert.ok(n >= 10, `проходів ${n}`);
});

test('болото сповільнює гравця на swampSlow', () => {
  const run = (swamp) => {
    const s = newGame(11, P({ swampSlow: 0.5 }), 1);
    const pl = s.players[0];
    const y = Math.floor(pl.y / U);
    for (let x = 0; x < 12; x++) { const i = y * s.w + x; s.obj[i] = O.NONE; s.drop[i] = I.NONE; s.ground[i] = swamp ? G.SWAMP : G.GRASS; }
    pl.x = 1 * U + U / 2;
    const x0 = pl.x;
    for (let t = 0; t < 20; t++) step(s, [{ p: 0, k: 'in', mx: 100, my: 0 }]);
    return pl.x - x0;
  };
  const g = run(false), sw = run(true);
  assert.ok(g > 0 && sw > 0);
  assert.ok(Math.abs(sw / g - 0.5) < 0.05, `болото ${sw} проти трави ${g}`);
});
