// «Колія» K26 — нові біоми: зима ❄ (сніг, лід із ковзанням) і яр 🏞 (урвище, міст дерево + залізо).
// node --test tests/koliya-js/biome.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import * as R from '../../web/games/koliya/rules.js';
import { generate, findRoute, MAX_WATER_RUN, BIOMES_NEW } from '../../web/games/koliya/gen.js';
import { createBot } from '../../web/games/koliya/bot.js';

const { U, G, O, I, TPS } = R;
const ON = { winter: 'on', yar: 'on' };
const P = (o = {}) => ({ ...R.defaultParams(), ...ON, ...o });

/** Порожня смуга: без лісу/скель/води */
function blank(params = {}, n = 1) {
  const s = R.newGame(7, P({ forest: 0, rocks: 0, water: 0, startSec: 90, winter: 'off', yar: 'off', ...params }), n);
  for (let i = 0; i < s.w * s.h; i++) {
    const y = (i / s.w) | 0;
    if (y > 0 && y < s.h - 1) { s.obj[i] = O.NONE; s.hp[i] = 0; s.drop[i] = I.NONE; s.dropN[i] = 0; s.ground[i] = G.GRASS; }
  }
  return s;
}
const at = (s, n, cx, cy, fx = 1, fy = 0) => { const pl = s.players[n]; pl.x = cx * U + U / 2; pl.y = cy * U + U / 2; pl.fx = fx; pl.fy = fy; return pl; };
const id = (s, x, y) => y * s.w + x;
const fill = (s, x0, x1, y0, y1, g) => { for (let y = y0; y <= y1; y++) for (let x = x0; x <= x1; x++) s.ground[id(s, x, y)] = g; };
const inp = (mx, my, p = 0) => ({ p, k: 'in', mx, my });
const use = (p = 0) => ({ p, k: 'use' });

function play(seed, nBots, params, maxMin) {
  const s = R.newGame(seed, P(params), nBots);
  const bots = Array.from({ length: nBots }, (_, i) => createBot(i));
  while (s.status === 'run' && s.tick < maxMin * 60 * TPS) {
    const cmds = [];
    for (const b of bots) { const v = b.think(s); cmds.push(inp(v.mx | 0, v.my | 0, b.p)); if (v.use) cmds.push(use(b.p)); }
    R.step(s, cmds);
  }
  return { s, headX: Math.floor(R.wagonPos(s, 0).x / U) };
}

// ---------------- перемикачі й детермінізм ----------------
test('перемикачі: вимкнені — у черзі біомів їх нема й світ той самий; частота 0 — теж нема', () => {
  for (let seed = 1; seed <= 20; seed++) {
    const off = generate(seed, P({ winter: 'off', yar: 'off' }));
    assert.ok(!off.zones.some((z) => BIOMES_NEW.includes(z.kind)));
    for (let i = 0; i < off.ground.length; i++) assert.ok(off.ground[i] <= G.SWAMP);
    const z0 = generate(seed, P({ newBiomes: 0 }));
    assert.deepEqual(z0.ground, off.ground, `сід ${seed}: частота 0 = вимкнено`);
    assert.deepEqual(z0.obj, off.obj);
  }
  const d = R.defaultParams();
  for (const k of ['winter', 'yar']) assert.equal(d[k], R.VARIANTS[k].values[0]);
  for (const k of ['newBiomes', 'snowSlow', 'iceSlide']) assert.equal(R.PARAM_META[k].group, 'Світ');
});

test('детермінізм: той самий сід — той самий світ; гра з ботами на льоду/яру — той самий hash', () => {
  for (const seed of [3, 5, 17]) {
    const a = generate(seed, P()), b = generate(seed, P());
    assert.deepEqual(a.ground, b.ground); assert.deepEqual(a.obj, b.obj); assert.deepEqual(a.zones, b.zones);
  }
  const A = play(5, 2, { newBiomes: 3 }, 2), B = play(5, 2, { newBiomes: 3 }, 2);
  assert.equal(R.hash(A.s), R.hash(B.s));
  // стан гравця на льоду — лише цілі
  const s = blank(); fill(s, 20, 40, 5, 12, G.ICE);
  const pl = at(s, 0, 22, 8);
  for (let t = 0; t < 30; t++) R.step(s, [inp(t < 10 ? 100 : 0, t < 10 ? 37 : 0)]);
  for (const k of ['x', 'y', 'ivx', 'ivy']) assert.ok(Number.isInteger(pl[k]), `${k}=${pl[k]}`);
});

// ---------------- прохідність ----------------
test('прохідність на 50 сідах з новими біомами: маршрут є, мости поспіль ≤ 3, яр — з перешийком, у зимі є вода', () => {
  const seen = {};
  for (let seed = 1; seed <= 50; seed++) {
    const wd = generate(seed, P({ newBiomes: 2 })), { w, h, ground, obj } = wd;
    const L = `сід ${seed}`;
    for (let i = 0; i < w * h; i++) {
      assert.ok(ground[i] <= G.FRAME, `${L}: підлога ${ground[i]}`);
      if (ground[i] === G.ICE || ground[i] === G.CHASM) assert.equal(obj[i], O.NONE, `${L}: предмет на льоду/урвищі`);
      if (ground[i] === G.FRAME) assert.fail(`${L}: каркас із генератора`);
    }
    for (const sp of wd.spawn) assert.ok(!R.noFoot(ground[sp.y * w + sp.x]), L);
    const route = findRoute(wd);
    assert.ok(route, `${L}: маршруту нема`);
    let run = 0;
    for (const c of route) { run = R.noFoot(ground[c]) ? run + 1 : 0; assert.ok(run <= MAX_WATER_RUN, `${L}: мостів поспіль ${run}`); }
    for (const z of wd.zones) {
      if (!BIOMES_NEW.includes(z.kind)) continue;
      seen[z.kind] = (seen[z.kind] | 0) + 1;
      let cnt = 0, wat = 0;
      for (let y = 1; y < h - 1; y++) for (let x = z.x0; x < z.x1; x++) {
        const g = ground[y * w + x];
        if (g === (z.kind === 'yar' ? G.CHASM : G.ICE)) cnt++;
        if (g === G.WATER) wat++;
      }
      assert.ok(cnt > 0, `${L}: ${z.kind} без своєї підлоги`);
      if (z.kind === 'winter') assert.ok(wat > 0, `${L}: зима без води для відра`);
      if (z.kind === 'yar') {
        // урвище ширини 1–2 (по рядку поспіль)
        for (let y = 1; y < h - 1; y++) {
          let runC = 0, maxRun = 0;
          for (let x = z.x0; x < z.x1; x++) { if (ground[y * w + x] === G.CHASM) { runC++; if (runC > maxRun) maxRun = runC; } else runC = 0; }
          assert.ok(maxRun <= 2, `${L}: яр ширший за 2 (${maxRun})`);
        }
        // перешийки: пішки (ліс і скелі — прорубати можна, WALL/вода/урвище — ні) з лівого краю ділянки на правий
        const seenC = new Uint8Array(w * h), q = [];
        const ok = (x, y) => x >= z.x0 && x < z.x1 && y > 0 && y < h - 1 && obj[y * w + x] !== O.WALL && !R.noFoot(ground[y * w + x]);
        for (let y = 1; y < h - 1; y++) if (ok(z.x0, y)) { seenC[y * w + z.x0] = 1; q.push(y * w + z.x0); }
        let across = false;
        for (let k = 0; k < q.length && !across; k++) {
          const x = q[k] % w, y = (q[k] / w) | 0;
          if (x === z.x1 - 1) across = true;
          for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
            const m = (y + dy) * w + x + dx;
            if (ok(x + dx, y + dy) && !seenC[m]) { seenC[m] = 1; q.push(m); }
          }
        }
        assert.ok(across, `${L}: яр не перейти пішки`);
      }
    }
    // сніг лише в зимі, урвище лише в яру
    for (let x = 0; x < w; x++) {
      const z = wd.zones.find((q) => x >= q.x0 && x < q.x1);
      for (let y = 1; y < h - 1; y++) {
        const g = ground[y * w + x];
        if (g === G.SNOW || g === G.ICE) assert.equal(z?.kind, 'winter', `${L}: ${g} поза зимою x=${x}`);
        if (g === G.CHASM) assert.equal(z?.kind, 'yar', `${L}: урвище поза яром x=${x}`);
      }
    }
  }
  assert.ok(seen.winter >= 10 && seen.yar >= 10, JSON.stringify(seen));
});

// ---------------- лід і сніг ----------------
test('лід: відпустив — ковзаєш далі (≈ 2 клітинки), на траві — стоїш одразу; розворот не миттєвий; у стіну — стоп', () => {
  const glide = (g) => {
    const s = blank(); fill(s, 15, 60, 3, 14, g);
    const pl = at(s, 0, 20, 8);
    for (let t = 0; t < 20; t++) R.step(s, [inp(100, 0)]);
    const x0 = pl.x;
    for (let t = 0; t < 60; t++) R.step(s, [inp(0, 0)]);
    return { d: pl.x - x0, pl, s };
  };
  const grass = glide(G.GRASS), ice = glide(G.ICE);
  assert.equal(grass.d, 0);
  assert.ok(ice.d > 1.2 * U && ice.d < 4 * U, `ковзання ${ice.d}`);
  assert.equal(ice.pl.ivx, 0, 'зрештою зупинився');
  // розворот: тримаєш назад — ще кілька тиків їдеш уперед
  const s = blank(); fill(s, 15, 60, 3, 14, G.ICE);
  const pl = at(s, 0, 20, 8);
  for (let t = 0; t < 20; t++) R.step(s, [inp(100, 0)]);
  let x = pl.x; R.step(s, [inp(-100, 0)]);
  assert.ok(pl.x > x, 'перший тик після розвороту ще несе вперед');
  for (let t = 0; t < 40; t++) R.step(s, [inp(-100, 0)]);
  assert.ok(pl.x < x, 'зрештою їде назад');
  assert.equal(pl.fx, -1, 'дивиться куди тягне');
  // стіна: скеля на шляху — швидкість по x у нуль
  const s2 = blank(); fill(s2, 15, 60, 3, 14, G.ICE); s2.obj[id(s2, 26, 8)] = O.ROCK;
  const p2 = at(s2, 0, 20, 8);
  for (let t = 0; t < 40; t++) R.step(s2, [inp(100, 0)]);
  assert.ok(p2.x < 26 * U, 'крізь скелю не проїхав');
  assert.equal(p2.ivx, 0);
  // з льоду на траву — інерція зникає
  const s3 = blank(); fill(s3, 15, 22, 3, 14, G.ICE);
  const p3 = at(s3, 0, 16, 8);
  for (let t = 0; t < 40; t++) R.step(s3, [inp(100, 0)]);
  const x3 = p3.x; R.step(s3, [inp(0, 0)]);
  assert.ok(Math.floor(p3.x / U) > 22 && p3.x === x3 && !p3.ivx, 'на траві зупинився одразу');
});

test('лід без зими не чіпає гравця: hash без нових полів; move=grid — без ковзання', () => {
  const s = blank();
  const pl = at(s, 0, 20, 8);
  for (let t = 0; t < 20; t++) R.step(s, [inp(100, 0)]);
  assert.equal(pl.ivx, undefined); assert.equal(pl.ion, undefined);
  const g = blank({ move: 'grid' }); fill(g, 15, 60, 3, 14, G.ICE);
  const pg = at(g, 0, 20, 8);
  for (let t = 0; t < 20; t++) R.step(g, [inp(100, 0)]);
  assert.equal(pg.ivx, undefined);
});

test('сніг сповільнює на snowSlow — слабше за болото', () => {
  const dist = (g) => {
    const s = blank(); fill(s, 0, 30, 3, 14, g);
    const pl = at(s, 0, 2, 8); const x0 = pl.x;
    for (let t = 0; t < 20; t++) R.step(s, [inp(100, 0)]);
    return pl.x - x0;
  };
  const gr = dist(G.GRASS), sn = dist(G.SNOW), sw = dist(G.SWAMP);
  assert.ok(Math.abs(sn / gr - R.PARAM_META.snowSlow.def) < 0.05, `сніг ${sn} / трава ${gr}`);
  assert.ok(sw < sn && sn < gr);
});

test('рейки на лід і сніг класти можна', () => {
  const s = blank({ startRails: 4 });
  const last = s.path[s.path.length - 1], lx = last % s.w, ly = (last / s.w) | 0;
  s.ground[id(s, lx + 1, ly)] = G.ICE; s.ground[id(s, lx + 2, ly)] = G.SNOW;
  assert.ok(R.canLay(s, lx + 1, ly));
  const pl = at(s, 0, lx + 1, ly - 1, 0, 1);
  pl.hold.k = I.RAIL; pl.hold.n = 2;
  R.step(s, [use()]);
  assert.equal(s.path[s.path.length - 1], id(s, lx + 1, ly));
});

// ---------------- яр ----------------
test('міст через яр: дерево → каркас (не пройти, рейку не покласти), залізо → міст (пройти, рейку можна)', () => {
  const s = blank();
  const last = s.path[s.path.length - 1], lx = last % s.w, ly = (last / s.w) | 0;
  const cx = lx + 1, c = id(s, cx, ly);
  for (let y = 1; y < s.h - 1; y++) s.ground[id(s, cx, y)] = G.CHASM;
  assert.ok(!R.canLay(s, cx, ly));
  assert.ok(R.cellBlocked(s, cx, ly));
  const pl = at(s, 0, lx, ly - 1, 1, 0);      // стоїмо над кінцем колії, дивимось на урвище (рядок вище)
  const top = id(s, cx, ly - 1);
  // залізо на голе урвище — нічого
  pl.hold.k = I.IRON; pl.hold.n = 2;
  R.step(s, [use()]);
  assert.equal(s.ground[top], G.CHASM); assert.equal(pl.hold.n, 2);
  // дерево → каркас
  pl.hold.k = I.WOOD; pl.hold.n = 2; pl.cd = 0;
  R.step(s, [use()]);
  assert.equal(s.ground[top], G.FRAME); assert.equal(pl.hold.n, 1);
  assert.ok(R.cellBlocked(s, cx, ly - 1), 'каркасом не пройти');
  // друге дерево на каркас — нічого (і не кладеться на підлогу)
  for (let t = 0; t < 5; t++) R.step(s, []);
  R.step(s, [use()]);
  assert.equal(s.ground[top], G.FRAME); assert.equal(pl.hold.n, 1); assert.equal(s.drop[top], I.NONE);
  // залізо → міст
  pl.hold.k = I.IRON; pl.hold.n = 1;
  for (let t = 0; t < 5; t++) R.step(s, []);
  const br = s.stats.bridges;
  R.step(s, [use()]);
  assert.equal(s.ground[top], G.BRIDGE); assert.equal(pl.hold.k, I.NONE); assert.equal(s.stats.bridges, br + 1);
  assert.ok(!R.cellBlocked(s, cx, ly - 1), 'мостом пройти');
  // рейки: на урвище — ні, на міст — так (міст на рядку колії)
  s.ground[c] = G.FRAME; assert.ok(!R.canLay(s, cx, ly));
  s.ground[c] = G.BRIDGE; assert.ok(R.canLay(s, cx, ly));
  // покласти річ на урвище не можна
  pl.hold.k = I.WOOD; pl.hold.n = 1; pl.fx = 0; pl.fy = -1;
  s.ground[id(s, lx, ly - 2)] = G.CHASM;
  for (let t = 0; t < 5; t++) R.step(s, []);
  R.step(s, [use()]);
  assert.equal(s.ground[id(s, lx, ly - 2)], G.FRAME, 'дерево на урвище — каркас, а не річ на підлозі');
  assert.equal(s.drop[id(s, lx, ly - 2)], I.NONE);
});

test('урвищем не пройти пішки: гравець упирається', () => {
  const s = blank();
  for (let y = 1; y < s.h - 1; y++) s.ground[id(s, 30, y)] = G.CHASM;
  const pl = at(s, 0, 27, 8);
  for (let t = 0; t < 60; t++) R.step(s, [inp(100, 0)]);
  assert.ok(pl.x + R.PR <= 30 * U, `x=${pl.x}`);
});

// ---------------- бот ----------------
test('бот: 20 сідів з новими біомами — як далеко їде і скільки нових ділянок проходить', () => {
  let reached = 0, passed = 0, won = 0, sumX = 0;
  const out = [];
  for (let seed = 1; seed <= 20; seed++) {
    const r = play(seed, 2, { newBiomes: 2 }, 12);
    const st = r.s.status;
    if (st === 'won') won++;
    sumX += r.headX;
    for (const z of r.s.zones) {
      if (!BIOMES_NEW.includes(z.kind) || (r.headX < z.x0 + 2 && st !== 'won')) continue;
      reached++;
      if (r.headX >= z.x1 || st === 'won') passed++; else out.push(`${seed}:${z.kind}@${r.headX}/${r.s.reason}`);
    }
  }
  console.log(`K26 бот (2): перемог ${won}/20, голова в середньому x=${(sumX / 20).toFixed(1)}, нових ділянок пройдено ${passed}/${reached}; застряг: ${out.join(' ') || '—'}`);
  assert.ok(reached >= 10, `нових ділянок досягнуто ${reached}`);
  assert.ok(passed / reached >= 0.6, `пройдено ${passed}/${reached}`);
  assert.ok(sumX / 20 >= 90, `в середньому x=${sumX / 20}`);
});

// ---------------- нескінченна колія ----------------
test('нескінченна колія: нові біоми в ділянках, бот із ними їде через станції, ніхто не стоїть на урвищі', async () => {
  const E = await import('../../web/games/koliya/endless.js');
  const EASY = { stationMode: 'endless', stripLen: 60, speedStart: 0.12, speedGrowth: 0, heatSec: 240, startSec: 20, newBiomes: 3 };
  const kinds = new Set();
  for (let k = 0; k < 8; k++) for (const z of E.segment(42, P(EASY), k).zones) kinds.add(z.kind);
  assert.ok(kinds.has('winter') && kinds.has('yar'), [...kinds].join(','));
  const s = R.newGame(42, P(EASY), 2);
  const bots = [createBot(0), createBot(1)];
  let bad = 0, seenNew = 0;
  while (s.status === 'run' && s.tick < 20 * 60 * TPS) {   // як endless.test: 0,12 клітинки/с — 2 станції ≈ 17 хв
    const cmds = [];
    for (const b of bots) { const v = b.think(s); cmds.push(inp(v.mx | 0, v.my | 0, b.p)); if (v.use) cmds.push(use(b.p)); }
    R.step(s, cmds);
    for (const pl of s.players) if (R.noFoot(s.ground[Math.floor(pl.y / U) * s.w + Math.floor(pl.x / U)])) bad++;
    if (s.tick % 200 === 0) seenNew += s.zones.filter((z) => BIOMES_NEW.includes(z.kind)).length;
  }
  console.log(`K26 нескінченна: ${s.status}/${s.reason} за ${Math.round(s.tick / TPS)} с, станцій ${s.endless.stations}, зсувів ${s.endless.shift}`);
  assert.equal(bad, 0, 'гравець стояв на урвищі/воді');
  assert.ok(seenNew > 0);
  assert.ok(s.endless.stations >= 2, `станцій ${s.endless.stations}`);
});
