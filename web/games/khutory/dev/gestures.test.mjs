// node --test web/games/khutory/dev/gestures.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import { wheelAction, wheelZoomFactor, wrapAngle, twoFinger, hintDue } from '../gestures.js';

test('колесо: без Ctrl/⌘ — прокрутка сторінки, з ними (і щипок тачпада) — наближення', () => {
  assert.equal(wheelAction({ deltaY: 100 }), 'scroll');
  assert.equal(wheelAction({ deltaY: 100, shiftKey: true }), 'scroll');
  assert.equal(wheelAction({ deltaY: 100, ctrlKey: true }), 'zoom');
  assert.equal(wheelAction({ deltaY: -3, metaKey: true }), 'zoom');
  assert.equal(wheelAction(null), 'scroll');
});

test('множник колеса: вниз — віддаляє, вгору — наближає, рядки грубіші за пікселі', () => {
  assert.ok(wheelZoomFactor(100) < 1);
  assert.ok(wheelZoomFactor(-100) > 1);
  assert.equal(wheelZoomFactor(0), 1);
  assert.ok(wheelZoomFactor(3, 1) < wheelZoomFactor(3, 0));
  const notch = wheelZoomFactor(100);   // клацання колеса — помітний, але не стрибок
  assert.ok(notch > 0.8 && notch < 0.9, String(notch));
  const pinch = wheelZoomFactor(-5);    // щипок тачпада — дрібні кроки, але не мертві
  assert.ok(pinch > 1.02 && pinch < 1.05, String(pinch));
});

test('wrapAngle тримає кут у (-π, π]', () => {
  assert.ok(Math.abs(wrapAngle(2 * Math.PI + 0.1) - 0.1) < 1e-9);
  assert.ok(Math.abs(wrapAngle(-2 * Math.PI - 0.1) + 0.1) < 1e-9);
  assert.equal(wrapAngle(Math.PI), Math.PI);
  assert.equal(wrapAngle(-Math.PI), Math.PI);
});

test('два пальці: щипок, зсув, поворот', () => {
  const a = { x: 100, y: 100 }, b = { x: 200, y: 100 };
  const spread = twoFinger(a, b, { x: 50, y: 100 }, { x: 250, y: 100 });
  assert.equal(spread.scale, 2); assert.equal(spread.rot, 0); assert.equal(spread.dx, 0); assert.equal(spread.dy, 0);
  const pan = twoFinger(a, b, { x: 110, y: 130 }, { x: 210, y: 130 });
  assert.equal(pan.scale, 1); assert.equal(pan.dx, 10); assert.equal(pan.dy, 30); assert.equal(pan.rot, 0);
  // за годинниковою на екрані (y донизу): b опускається — кут додатний
  const cw = twoFinger(a, b, { x: 150 - 50 * Math.cos(0.3), y: 100 - 50 * Math.sin(0.3) }, { x: 150 + 50 * Math.cos(0.3), y: 100 + 50 * Math.sin(0.3) });
  assert.ok(Math.abs(cw.rot - 0.3) < 1e-9, String(cw.rot));
  assert.ok(Math.abs(cw.scale - 1) < 1e-9);
  // перехід через ±π не дає стрибка на 2π
  const wrap = twoFinger({ x: 0, y: 0 }, { x: -100, y: 1 }, { x: 0, y: 0 }, { x: -100, y: -1 });
  assert.ok(Math.abs(wrap.rot) < 0.05, String(wrap.rot));
  // пальці в одній точці — без ділення на нуль
  assert.ok(Number.isFinite(twoFinger(a, a, a, a).scale));
});

test('підказка не частіше ніж раз на gap', () => {
  assert.equal(hintDue(1000, 0, 5000), true);
  assert.equal(hintDue(3000, 1000, 5000), false);
  assert.equal(hintDue(6000, 1000, 5000), true);
});
