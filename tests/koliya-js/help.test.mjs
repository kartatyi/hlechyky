// K65: довідка «❓ Як грати» — усі розділи є, покращення з UPGRADES, тема «Чумацький шлях» міняє слова (DOM — у Chrome)
import test from 'node:test';
import assert from 'node:assert/strict';
import { helpSections } from '../../web/games/koliya/help.js';
import { UPGRADES } from '../../web/games/koliya/extras.js';
import { setTheme } from '../../web/games/koliya/theme.js';

test('розділи довідки: мета, цикл, міст, кооп, біоми, покращення, керування, поради', () => {
  const s = helpSections();
  assert.deepEqual(s.map((x) => x.id), ['goal', 'loop', 'bridge', 'coop', 'biomes', 'ups', 'keys', 'tips']);
  for (const x of s) assert.ok(x.icon && x.title && x.html.length > 40, x.id);
  const all = s.map((x) => x.html).join('\n');
  for (const k of ['WASD', 'Space', 'Enter', 'Q', 'стік', '✋', '🎯', '📯', 'Болото', 'Лід', 'Яр', 'Передача', 'Кидок', 'Гудок', 'Реакції'])
    assert.ok(all.includes(k), k);
  for (const u of Object.values(UPGRADES)) assert.ok(s.find((x) => x.id === 'ups').html.includes(u.icon), u.short);
  const t = s.find((x) => x.id === 'keys').html;
  assert.equal((t.match(/<tr>/g) ?? []).length, 5, 'шапка + П1, П2, пад, телефон');
});

test('тема «Чумацький шлях» — свої слова в довідці', () => {
  const k = helpSections().map((x) => x.html).join('');
  setTheme('chumak');
  try {
    const c = helpSections().filter((x) => x.id !== 'ups').map((x) => x.html).join('');   // покращення — назви з extras/панелі
    assert.notEqual(c, k);
    assert.ok(c.includes('гать') && c.includes('віз-комор') && c.includes('🐂'), 'гать, віз-комора, воли');
    assert.ok(!c.includes('паротяг'), 'без паротяга');
  } finally { setTheme('koliya'); }
});

test('K69: покращення в «Чумацькому шляху» — чумацькими словами (довідка), у «Колії» — як в extras.js', async () => {
  const { upName } = await import('../../web/games/koliya/theme.js');
  const ko = helpSections().find((x) => x.id === 'ups').html;
  for (const [k, u] of Object.entries(UPGRADES)) assert.deepEqual(upName(k, u), u, k);
  setTheme('chumak');
  try {
    const c = helpSections().find((x) => x.id === 'ups').html;
    assert.notEqual(c, ko);
    for (const [k, u] of Object.entries(UPGRADES)) {
      const v = upName(k, u);
      assert.ok(v.label !== u.label && v.short && c.includes(v.label), `чумацька назва ${k}`);
      assert.ok(!/паротяг|котел|потяг|майстерн|склад|вагон/i.test(v.label + v.short), `без залізниці: ${v.label}`);
      assert.equal(v.icon, u.icon, 'значок як на табличці в 3D');
    }
  } finally { setTheme('koliya'); }
});
