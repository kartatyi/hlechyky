// K30: стартове меню — добірка сідів і короткі назви ділянок (DOM-частину перевіряє headless Chrome)
import test from 'node:test';
import assert from 'node:assert/strict';
import { PICKS, seedZones, BIOME_SHORT, memToSave } from '../../web/games/koliya/menu.js';
import { defaultParams } from '../../web/games/koliya/rules.js';

test('добірка K22 — 5 сідів, у кожного ділянки з короткими назвами', () => {
  assert.deepEqual(PICKS, [8, 19, 23, 53, 43]);
  for (const s of PICKS) {
    const z = seedZones(s, defaultParams());
    assert.ok(z.names.split(' → ').length >= 3, `сід ${s}: ${z.names}`);
    for (const part of z.names.split(' → ')) assert.ok(Object.values(BIOME_SHORT).some((n) => part.endsWith(n)), part);
  }
  assert.match(seedZones(19, defaultParams()).names, /^🌾 степ → 🌊 річка/);
});

test('K46: ?p= / ?bot= / ?seed= з адреси не переписують пам’ять меню', () => {
  const st = { players: 2, bots: 1, seed: 19 };
  assert.deepEqual(memToSave(st, { players: 1, bots: 0, seed: 'r' }, new Set(['players', 'seed']), 'play'), { players: 1, bots: 1, seed: 'r', last: 'play' });
  assert.deepEqual(memToSave(st, {}, new Set(['players']), undefined), { bots: 1, seed: 19, last: undefined });
  assert.deepEqual(memToSave(st, { players: 1 }, new Set(), 'daily'), { players: 2, bots: 1, seed: 19, last: 'daily' });
});
