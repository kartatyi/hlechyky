// node --test web/games/solyanyi/dev/hud-calc.test.mjs — без браузера й звуку.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mapSig, MAP_SIG, ROUTES, TICKETS, routeSlots, route, ticketPoints, LANE } from '../consts.js';
import { marks, whatNow, routePanel, pickerGroups, tipText, titles, chart, parseGeo, legalRoutes, lastText, statusText, cycleRoute, homeCrop, freshEvents } from '../hud-calc.js';
import { makeView, makeEvent, SCENARIOS } from './fixture.js';

test('відбиток мапи збігається', () => assert.equal(mapSig(), MAP_SIG));
test('слотів стільки, скільки возів', () => { for (const r of ROUTES) assert.equal(routeSlots(r[0]).length, r[4]); });
test('очки замовлень за формулою', () => { for (const t of TICKETS) assert.equal(t[4], ticketPoints(t[3])); });
test('слоти різних шляхів не торкаються (центри ≥ 28)', () => {
  const S = ROUTES.map((r) => routeSlots(r[0]));
  for (let a = 0; a < S.length; a++) for (let b = a + 1; b < S.length; b++) {
    if (route(a).twin === b) continue;
    for (const p of S[a]) for (const q of S[b]) assert.ok(Math.hypot(p.x - q.x, p.y - q.y) >= 28, a + '/' + b);
  }
  assert.ok(LANE > 0);
});
test('усі сценарії й події будуються', () => {
  for (const s of SCENARIOS) assert.ok(makeView(s, 0));
  for (const k of ['round', 'deal', 'setup', 'turn', 'draw', 'refill', 'wipe', 'shuffle', 'claim', 'tickets', 'keep', 'done', 'last', 'pass', 'longest', 'over']) assert.equal(makeEvent(k).k, k);
});
test('вид §5: шляхи 2, 4, 10 можна зайняти, 0 бракує 2 солі, 91 закрито', () => {
  const v = makeView('mid', 0);
  const c = v.me.claims;
  assert.ok(c[2].ok && c[4].ok && c[10].ok);
  assert.equal(c[0].why, 'бракує 2 солі');
  assert.equal(c[91].short, 0);
  const m = marks(v);
  assert.equal(m[2], 'ok');
  assert.deepEqual(v.closed, [91, 96]);
  assert.ok(legalRoutes(v).includes(15));
});
test('панель шляху: чого бракує і найкраща дія', () => {
  const v = makeView('mid', 0);
  const ok = routePanel(v, 10, null);
  assert.ok(ok.canClaim && ok.combos.length === 2 && ok.pips.length === 2);
  const no = routePanel(v, 0, null);
  assert.equal(no.short, 2);
  assert.equal(no.pips.filter((p) => !p.have).length, 2);
  assert.ok(no.advice && no.advice.deck, 'солі на ринку нема — наосліп');
  const honey = routePanel(v, 5, null);
  assert.equal(honey.advice.slot, 2, 'мед лежить на ринку');
  const taken = routePanel(v, 19, null);
  assert.equal(taken.owner, 0);
});
test('смуга «що зараз»', () => {
  assert.match(whatNow(makeView('mid', 0)).t, /Твій хід/);
  assert.equal(whatNow(makeView('draw2', 0)).t, 'Ще одна карта');
  assert.match(whatNow(makeView('keep', 0)).s, /Лиши щонайменше 1/);
  assert.match(whatNow(makeView('setup', 0)).s, /щонайменше 2/);
  assert.match(whatNow(makeView('other', 0)).t, /Зараз їде Глек/);
  assert.match(lastText(makeView('last', 0), makeView('last', 0).me), /ще 2 ходи, твій — останній/);
  assert.equal(statusText(makeView('mid', 0), { room: { status: 'playing' } }), 'Твій хід');
});
test('список 🧭: спершу мої замовлення, потім можу зайняти', () => {
  const v = makeView('mid', 0);
  const g = pickerGroups(v, '');
  assert.equal(g[0].key, 'plan');
  assert.ok(g.find((x) => x.key === 'ok'));
  const q = pickerGroups(v, 'київ');
  assert.ok(q[0].rows.length > 0);
});
test('поради й звання', () => {
  const v = makeView('mid', 0);
  assert.match(tipText(v, { act: 'claim', route: 15, c: 'linen', w: 1 }), /Глек би: зайняв Харків — Краматорськ/);
  const o = makeView('over', 0).over;
  const T = titles(o, () => true);
  assert.ok(T.length > 0);
  const per = {};
  for (const t of T) { per[t.seat] = (per[t.seat] || 0) + 1; assert.ok(per[t.seat] <= 2); }
  const C = chart(o.stats.timeline, [0, 1, 2, 3, 4], 420, 170);
  assert.equal(C.lines.length, 5);
});
test('контур мапи й курсор', () => {
  const g = parseGeo({ arcs: [[0, 0, 10, 0], [10, 0, 0, 0]], regions: [{ rings: [[1, 2]] }] });
  assert.ok(g.land.startsWith('M'));
  assert.equal(cycleRoute([1, 5, 9], 9, 1), 1);
  assert.equal(cycleRoute([1, 5, 9], null, -1), 9);
});
test('події: done береться за own, нічого не губиться й не повторюється, зокрема після обриву зв\'язку', () => {
  const v1 = { seq: 11, me: { own: 1 }, events: [{ seq: 10, k: 'draw' }, { seq: 11, k: 'claim' }, { seq: 11, own: 1, k: 'done' }] };
  // Новий після claim — і claim, і done, у цьому порядку.
  const a = freshEvents(v1, 10, 0);
  assert.deepEqual(a.list.map((e) => e.k), ['claim', 'done']);
  assert.deepEqual([a.seq, a.own], [11, 1]);
  // Той самий вид удруге (локальна перемальовка, повторна розсилка) — порожньо.
  assert.deepEqual(freshEvents(v1, a.seq, a.own).list, []);
  // Новий done у виді, де спільний seq не рухався: усе одно новий; суперник нічого такого не отримує й seq у нього той самий.
  const v2 = { seq: 11, me: { own: 2 }, events: [...v1.events, { seq: 11, own: 2, k: 'done' }] };
  assert.deepEqual(freshEvents(v2, 11, 1).list.map((e) => e.own), [2]);
  // Відпав на кілька подій і вернувся: усе, що пропустив, — раз і по порядку.
  const v3 = { seq: 13, me: { own: 3 }, events: [...v2.events, { seq: 12, k: 'turn' }, { seq: 13, k: 'claim' }, { seq: 13, own: 3, k: 'done' }] };
  assert.deepEqual(freshEvents(v3, 10, 0).list.map((e) => e.k + (e.own || '')), ['claim', 'done1', 'done2', 'turn', 'claim', 'done3']);
  // Глядач: me нема, лічильник свій не росте.
  assert.deepEqual(freshEvents({ seq: 13, events: [{ seq: 13, k: 'claim' }] }, 12, 0).own, 0);
});
test('телефон відкриває мапу навколо замовлень', () => {
  assert.equal(homeCrop([], 1150, 1300, 1.5), null);
  // Близькі замовлення — посередині них, на робочому масштабі.
  const near = homeCrop([{ a: 0, b: 2 }], 1150, 1300, 1.5);        // Київ — Ніжин
  assert.equal(near.w, 1150);
  assert.ok(near.x > 1884 && near.x < 2171 && near.y > 482 && near.y < 682);
  // Усе не влазить — найкоротше.
  const far = homeCrop([{ a: 44, b: 9 }, { a: 0, b: 2 }], 1150, 1300, 1.5);   // Ужгород — Луганськ і Київ — Ніжин
  assert.ok(far.x > 1884 && far.x < 2171);
  // Найкоротше теж не влазить — його перший кінець.
  assert.deepEqual([homeCrop([{ a: 44, b: 9 }], 1150, 1300, 1.5).x, homeCrop([{ a: 44, b: 9 }], 1150, 1300, 1.5).w], [79, 1150]);
  // Трохи ширше робочого — розширюємо, а не губимо.
  const wide = homeCrop([{ a: 0, b: 5 }], 900, 900, 1.5);          // Київ — Полтава
  assert.ok(wide.w > 900 && wide.w <= 1350);
});
