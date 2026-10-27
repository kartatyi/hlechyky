// «Колія» K27 — логіка дотикового стіка (без DOM) і гачок input.setTouch.
import test from 'node:test';
import assert from 'node:assert/strict';

globalThis.addEventListener ??= () => { };
globalThis.removeEventListener ??= () => { };
if (!globalThis.navigator?.getGamepads) Object.defineProperty(globalThis, 'navigator', { value: { getGamepads: () => [] }, configurable: true, writable: true });
const { stickVec, follow, DEAD, FULL } = await import('../../web/games/koliya/touch.js');
const { createInput } = await import('../../web/games/koliya/input.js');

const near = (a, b, eps = 1e-9) => assert.ok(Math.abs(a - b) < eps, `${a} ≈ ${b}`);

test('мертва зона: дрібний зсув пальця — стоїмо', () => {
  assert.deepEqual(stickVec(0, 0, 60), { mx: 0, my: 0 });
  assert.deepEqual(stickVec(60 * DEAD * 0.99, 0, 60), { mx: 0, my: 0 });
  assert.deepEqual(stickVec(-5, 5, 60), { mx: 0, my: 0 });
  assert.deepEqual(stickVec(NaN, 0, 60), { mx: 0, my: 0 });
});

test('нормалізація: довжина ≤ 1, напрям зберігається, з FULL — повна швидкість', () => {
  for (const [dx, dy] of [[100, 0], [0, -300], [70, 70], [-1000, 3]]) {
    const v = stickVec(dx, dy, 60);
    near(Math.hypot(v.mx, v.my), 1, 1e-9);
    near(Math.atan2(v.my, v.mx), Math.atan2(dy, dx), 1e-9);
  }
  const f = stickVec(60 * FULL, 0, 60);
  near(f.mx, 1);
  const half = stickVec(60 * (DEAD + (FULL - DEAD) / 2), 0, 60);
  near(half.mx, 0.5, 1e-9);
  // монотонно росте з відстанню
  let prev = -1;
  for (let d = 0; d <= 80; d += 2) { const m = stickVec(0, d, 60).my; assert.ok(m >= prev - 1e-12); prev = m; }
});

test('плаваючий центр тягнеться за пальцем лише за межею радіуса', () => {
  assert.deepEqual(follow(100, 100, 130, 100, 50), [100, 100]);
  const [cx, cy] = follow(100, 100, 200, 100, 50);
  near(cx, 150); near(cy, 100);
  const [ax, ay] = follow(0, 0, 30, 40, 20);   // палець на відстані 50 → центр на 30 ближче
  near(Math.hypot(30 - ax, 40 - ay), 20);
});

test('input.setTouch: гравець 0 бере дотик, кидок раз, меню глушить, вібрація за перемикачем', () => {
  let t = { mx: 0.5, my: -1, use: true, throw: true, horn: false };
  let vib = null, swallowed = 0;
  const buzz = [];
  const fake = { read() { const r = t; t = { ...t, throw: false }; return r; }, swallow() { swallowed++; }, setVibrate(on) { vib = on; }, buzz(k) { buzz.push(k); } };
  const a = createInput({ players: 2 });
  a.setTouch(fake);
  assert.equal(vib, true);
  let v = a.poll();
  assert.deepEqual([v[0].mx, v[0].my, v[0].use, v[0].throw], [0.5, -1, true, true]);
  assert.deepEqual([v[1].mx, v[1].use], [0, false]);   // другому гравцю дотик не йде
  v = a.poll();
  assert.equal(v[0].throw, false);
  a.setMenu(() => { });
  const s0 = swallowed;
  v = a.poll();
  assert.deepEqual([v[0].mx, v[0].use], [0, false]);
  assert.ok(swallowed > s0);
  a.setMenu(null);
  a.rumble(0, 'hit'); a.rumble(1, 'hit'); a.rumble(-1, 'lost');
  assert.deepEqual(buzz, ['hit', 'lost']);
  a.setRumble(false);
  assert.equal(vib, false);
  a.rumble(0, 'hit');
  assert.deepEqual(buzz, ['hit', 'lost']);
  a.setTouch(null);
  t = { mx: 1, my: 1, use: true };
  assert.deepEqual([a.poll()[0].mx, a.poll()[0].use], [0, false]);
  a.dispose();
});
