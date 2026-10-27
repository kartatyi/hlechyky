// «Колія» K68 — стрес, короткий варіант: 4 боти, усі додатки, нескінченна колія, варіанти ядра по черзі (stress-lib.mjs).
// Без винятків, детерміновано (два прогони — той самий hash), стан не пухне (JSON наприкінці ≈ як на старті).
// Повний (30 хв гри × 3 сіди, ~2 хв): node tests/koliya-js/stress-lib.mjs --full
import test from 'node:test';
import assert from 'node:assert/strict';
import { stress } from './stress-lib.mjs';

for (const [i, seed] of [11, 222, 3333].entries()) {
  test(`K68: сід ${seed}, варіант ${i} — 1500 тиків без винятків, hash сталий, стан не росте`, () => {
    const a = stress(seed, i, 1500, { blocks: 3 }), b = stress(seed, i, 1500, { blocks: 3 });
    assert.equal(a.hash, b.hash);
    assert.ok(a.maxSize < a.sizes[0] * 1.15, `JSON стану ${a.sizes[0]} → макс ${a.maxSize}`);
    assert.ok(a.maxEv < 64, `подій за тик ${a.maxEv}`);
    for (const pl of a.state.players) assert.ok(Number.isFinite(pl.x) && Number.isFinite(pl.y));
    assert.ok((a.state.flying?.length ?? 0) < 16 && (a.state.geese?.length ?? 0) < 16);
  });
}
