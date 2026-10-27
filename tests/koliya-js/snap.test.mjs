// «Колія» K44 — знімок світу: snapText/snapParse/encodeSnap/decodeSnap. Світ після розпаковки має той самий hash і
// далі з тими самими командами йде так само (знімок не впливає на детермінізм); стиснутий — малий.
import test from 'node:test';
import assert from 'node:assert/strict';
import * as R from '../../web/games/koliya/rules.js';
import { encodeSnap, decodeSnap, snapText, snapParse } from '../../web/games/koliya/net.js';
import { createBot } from '../../web/games/koliya/bot.js';

const PARAMS = { ...R.defaultParams(), geese: 'on', geeseSec: 15, throw: 'on', bump: 'on', stun: 'on', pass: 'on',
  stationMode: 'endless', stripLen: 40, zoneLen: 14, startSec: 8, overheat: 'stop', speedStart: 0.3 };

function play(s, bots, n) {
  for (let i = 0; i < n && s.status === 'run'; i++) {
    const cmds = [];
    bots.forEach((b, p) => { const v = b.think(s, p); cmds.push({ p, k: 'in', mx: v.mx | 0, my: v.my | 0 }); if (v.use) cmds.push({ p, k: 'use' }); });
    R.step(s, cmds);
  }
}

for (const [name, prm] of [['типова смуга', R.defaultParams()], ['нескінченна з додатками', PARAMS]]) {
  test(`K44 знімок: ${name} — той самий hash і той самий світ далі, стиснутий малий`, async () => {
    const s = R.newGame(11, prm, 3), bots = [0, 1, 2].map((p) => createBot(p));
    play(s, bots, 1500);
    const { z, b } = await encodeSnap(s);
    assert.equal(z, 1, 'CompressionStream є в Node 24');
    const raw = new TextEncoder().encode(snapText(s)).length;
    if (process.env.K44_LOG) console.log(name, 'сирий', raw, 'стиснутий', b.length);
    assert.ok(b.length < 64 * 1024, `знімок ${b.length} Б`);
    assert.ok(b.length < raw / 2, 'gzip не стиснув');
    const r = await decodeSnap(Buffer.from(b).toString('base64'), z);
    assert.equal(r.tick, s.tick);
    assert.equal(R.hash(r), R.hash(s));
    for (const k of ['ground', 'obj', 'hp', 'drop', 'dropN']) assert.ok(r[k] instanceof Uint8Array && r[k].length === s[k].length, k);
    // далі — однакові команди, однаковий світ (і все, що не в hash, теж)
    const cmds = (t) => [{ p: 0, k: 'in', mx: t % 7 === 0 ? 100 : -40, my: (t * 13) % 200 - 100 }, ...(t % 5 ? [] : [{ p: 1, k: 'use' }])];
    for (let i = 0; i < 400; i++) { R.step(s, cmds(i)); R.step(r, cmds(i)); }
    assert.equal(R.hash(r), R.hash(s));
    assert.equal(snapText(r), snapText(s));
  });
}

test('K44 знімок: інші типізовані масиви й незжатий шлях', async () => {
  const x = { a: new Int16Array([-5, 300]), b: new Float64Array([0.1, -2.5]), c: [1, { d: new Uint8Array([9]) }] };
  const y = snapParse(snapText(x));
  assert.ok(y.a instanceof Int16Array && y.b instanceof Float64Array && y.c[1].d instanceof Uint8Array);
  assert.deepEqual([...y.a, ...y.b, ...y.c[1].d], [-5, 300, 0.1, -2.5, 9]);
  const u = await decodeSnap(Buffer.from(snapText(x)).toString('base64'), 0);
  assert.deepEqual([...u.a], [-5, 300]);
});
