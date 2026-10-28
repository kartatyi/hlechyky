// node --test web/games/sorochyntsi/dev/hud-calc.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import { DECK, DECK_SIG, deckSig, GOODS, TIER_SIZE, card } from '../consts.js';
import { makeView, makeEvent, SCENARIOS, priceFor } from './fixture.js';
import {
  takeState, addPick, subPick, overflowText, suggestDiscard, discardOk, guestProgress, marks, costAfter, closes, goodsPhrase,
  takePhrase, tipText, tipPick, turnsLeft, titles, chart, starshynaNeed,
} from '../hud-calc.js';

test('колода: відбиток і розміри — як у сервера (§1.1)', () => {
  assert.equal(deckSig(), DECK_SIG);
  assert.equal(DECK.length, 90);
  for (const t of [1, 2, 3]) assert.equal(DECK.filter((d) => d[1] === t).length, TIER_SIZE[t]);
  DECK.forEach((d, i) => assert.equal(d[0], i));
  for (const d of DECK) assert.equal(d[5].length, 5);   // ціни в папороті не буває
});

test('вид §5: me.cards фікстури збігається з прикладом контракту', () => {
  const v = makeView('mid', 0);
  const c = v.me.cards;
  assert.deepEqual(c['0'], { ok: true, pay: { clay: 1, gold: 1 } });
  assert.deepEqual(c['38'], { ok: true, pay: { linen: 1 } });
  assert.deepEqual(c['30'], { ok: false, need: { wool: 3 }, short: 2, why: 'бракує 3 вовни, дукатів у тебе: 1' });
  assert.deepEqual(c['41'], { ok: false, need: { herbs: 2, clay: 2 }, short: 3, why: 'бракує 2 зілля й 2 глини, дукатів у тебе: 1' });
  assert.deepEqual(c['74'], { ok: false, need: { herbs: 5, wool: 3 }, short: 7, why: 'бракує 5 зілля й 3 вовни, дукатів у тебе: 1' });
  assert.deepEqual(v.me.take, { piles: GOODS, need: 3, two: [] });
  assert.deepEqual(v.me.bulava, { points: 14, goods: ['clay', 'wool', 'fern'] });
  assert.equal(v.players[0].points, 2);
  assert.equal(v.players[2].points, 5);
  // Бронь Оксани наосліп Smaug не бачить; Оксана — бачить.
  assert.deepEqual(v.players[2].reserved[1], { id: null, tier: 3, blind: true });
  assert.equal(makeView('mid', 2).players[2].reserved[1].id, 80);
  assert.equal(JSON.stringify(v).includes('"id":80'), false);
});

test('усі сценарії будуються; глядач — без me', () => {
  for (const s of SCENARIOS) {
    const v = makeView(s, 0);
    assert.ok(v, s);
    if (s === 'spectator' || s === 'lobby') assert.equal(v.me, null, s);
  }
  assert.equal(makeView('discard', 0).me.discard, 2);
  assert.deepEqual(makeView('guest', 0).me.guests, [1, 6]);
  assert.equal(makeEvent('buy', { seq: 5 }).k, 'buy');
});

test('takeState: три різні, два однакові, скорочені', () => {
  const take = { piles: GOODS, need: 3, two: ['clay'] };
  const bank = { linen: 2, fish: 2, herbs: 2, clay: 4, wool: 3 };
  assert.equal(takeState(take, {}, bank).ok, false);
  assert.equal(takeState(take, { fish: 1, herbs: 1 }, bank).hint, 'ще 1 різний');
  assert.deepEqual(takeState(take, { fish: 1, herbs: 1, clay: 1 }, bank), { ok: true, mode: 'diff', total: 3, goods: ['fish', 'herbs', 'clay'], why: '', hint: '', rule: takeState(take, {}, bank).rule });
  assert.equal(takeState(take, { clay: 2 }, bank).mode, 'two');
  assert.equal(takeState(take, { wool: 2 }, bank).why, 'з купи 3 два не беруть (треба ≥ 4)');
  assert.equal(takeState(take, { wool: 2, fish: 1 }, bank).ok, false);
  const k2 = { piles: ['fish', 'wool'], need: 2, two: [] };
  assert.equal(takeState(k2, { fish: 1 }, {}).ok, false);
  assert.equal(takeState(k2, { fish: 1, wool: 1 }, {}).ok, true);
});

test('addPick / subPick: +1, другий однаковий лише з купи ≥ 4, не понад трійку', () => {
  const take = { piles: GOODS, need: 3, two: ['clay'] };
  const bank = { clay: 4, wool: 3 };
  let r = addPick(take, {}, 'clay', bank);
  assert.deepEqual(r.pick, { clay: 1 });
  r = addPick(take, r.pick, 'clay', bank);
  assert.deepEqual(r.pick, { clay: 2 });
  r = addPick(take, { wool: 1 }, 'wool', bank);
  assert.deepEqual(r.pick, { wool: 1 });
  assert.match(r.why, /з купи 3/);
  r = addPick(take, { linen: 1, fish: 1, herbs: 1 }, 'wool', bank);
  assert.deepEqual(r.pick, { linen: 1, fish: 1, herbs: 1 });
  assert.ok(r.why);
  assert.equal(addPick(take, {}, 'gold', bank).why, 'Дукат — лише разом із бронею');
  assert.deepEqual(subPick({ clay: 2 }, 'clay'), { clay: 1 });
  assert.deepEqual(subPick({ clay: 1 }, 'clay'), {});
  assert.equal(overflowText(9, 3), 'стане 12 жетонів — 2 доведеться повернути');
  assert.equal(overflowText(7, 3), '');
});

test('suggestDiscard: найчисленніші понад потребу, дукати останні', () => {
  const tok = { linen: 3, fish: 3, herbs: 1, clay: 3, wool: 0, gold: 2 };
  const g = suggestDiscard(tok, 2, { fish: 3 });
  assert.equal(Object.values(g).reduce((a, b) => a + b, 0), 2);
  assert.equal(g.fish, undefined);
  assert.equal(g.gold, undefined);
  assert.ok(discardOk(tok, g, 2));
  assert.deepEqual(suggestDiscard({ gold: 3, fish: 0 }, 2), { gold: 2 });
  assert.equal(discardOk(tok, { wool: 1, linen: 1 }, 2), false);
});

test('guestProgress, marks, costAfter, closes', () => {
  const p = guestProgress(1, { fish: 4, herbs: 2 });
  assert.deepEqual(p.items, [{ g: 'fish', have: 4, need: 4, ok: true }, { g: 'herbs', have: 2, need: 4, ok: false }]);
  assert.equal(p.left, 2);
  assert.equal(guestProgress(1, { fish: 5, herbs: 4 }).done, true);
  const v = makeView('mid', 0);
  const m = marks(v.me.cards);
  assert.equal(m['38'], 'buy');
  assert.equal(m['0'], 'gold');
  assert.equal(m['30'], 'near');
  assert.equal(m['70'], undefined);
  assert.deepEqual(costAfter(41, v.players[0].bonus), { herbs: 2, clay: 3 });
  const cl = closes(85, v, 0).map((x) => x.kind);
  assert.ok(cl.includes('bulava'));
  assert.ok(cl.includes('starshyna'));
  assert.equal(starshynaNeed(v, 0), 4);
  assert.equal(starshynaNeed(v, 2), 0);
});

test('тексти: «бракує», журнал take, порада', () => {
  assert.equal(goodsPhrase({ herbs: 2, clay: 2 }), '2 зілля й 2 глини');
  assert.equal(goodsPhrase({ fish: 1, herbs: 1, wool: 3, gold: 2 }), '1 риби, 1 зілля, 3 вовни й 2 дукати');
  assert.equal(takePhrase({ fish: 1, herbs: 1, clay: 1 }), 'рибу, зілля й глину');
  assert.equal(takePhrase({ wool: 2 }), '2 вовни');
  assert.equal(tipText({ act: 'take', goods: ['fish', 'herbs', 'clay'] }), 'взяв 🐟🌿🏺');
  assert.equal(tipText({ act: 'buy', card: 38 }), 'купив «Валянки»');
  assert.equal(tipText({ act: 'reserve', tier: 3 }), 'забронював наосліп з колоди III');
  assert.deepEqual(tipPick({ act: 'take', goods: ['clay', 'clay'] }), { clay: 2 });
  assert.deepEqual(priceFor({ tokens: { linen: 0, fish: 0, herbs: 0, clay: 0, wool: 0, gold: 0 }, cards: [] }, 6).short, 3);
  assert.equal(card(38).name, 'Валянки');
});

test('turnsLeft: в останньому колі', () => {
  const v = { order: [0, 1, 2], first: 1, turn: 2 };
  assert.equal(turnsLeft(v, 0), 1);   // коло 1 → 2 → 0
  assert.equal(turnsLeft(v, 1), 0);
  assert.equal(turnsLeft(v, 2), 1);
});

test('звання: чесний лідер, ≤ 2 на людину; графік', () => {
  const v = makeView('over', 0);
  const T = titles(v.over.stats);
  const by = Object.fromEntries(T.map((t) => [t.key, t.seat]));
  assert.equal(by.tokens, 1);
  assert.equal(by.reserves, 0);
  assert.equal(by.guests, 2);
  assert.equal(by.icons, 2);
  assert.equal(by.firstT3, undefined);   // у seat 2 уже два звання
  for (const s of [0, 1, 2]) assert.ok(T.filter((t) => t.seat === s).length <= 2);
  const tie = titles({ seats: [{ seat: 0, tokens: 5 }, { seat: 1, tokens: 5 }] });
  assert.equal(tie.length, 0);
  const g = chart(v.over.stats.timeline, [0, 1, 2], 320, 170, 16);
  assert.equal(g.lines.length, 3);
  assert.equal(g.lines[0].pts.length, v.over.stats.timeline.length);
});
