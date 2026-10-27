// «Колія» K12 — пад: меню (гравці стоять, навігація з автоповтором), swallow, керма не пади, вібрація.
import test from 'node:test';
import assert from 'node:assert/strict';

globalThis.addEventListener = () => { };
globalThis.removeEventListener = () => { };
let gp = [];
Object.defineProperty(globalThis, 'navigator', { value: { getGamepads: () => gp }, configurable: true, writable: true });
let t = 0;
Object.defineProperty(globalThis, 'performance', { value: { now: () => t }, configurable: true, writable: true });
const { createInput } = await import('../../web/games/koliya/input.js');

const shakes = [];
const pad = (index, axes, pressed = [], id = 'pad' + index) => ({ index, id, connected: true, axes,
  buttons: Array.from({ length: 17 }, (_, i) => ({ pressed: pressed.includes(i), value: pressed.includes(i) ? 1 : 0 })),
  vibrationActuator: { playEffect: (k, o) => { shakes.push([index, o.duration]); return Promise.resolve(); } } });

test('Select відкриває меню; у меню гравець стоїть, хрестовина й A йдуть у меню; Start не рестартить', () => {
  const a = createInput({ players: 1 });
  let opened = 0; a.onMenuKey(() => opened++);
  gp = [pad(0, [0, 0])]; a.poll();
  gp = [pad(0, [0, 0], [8])]; a.poll();
  assert.equal(opened, 1);
  const keys = [];
  a.setMenu((k) => keys.push(k));
  gp = [pad(0, [1, 0], [0, 13])];
  assert.deepEqual(a.poll(), [{ mx: 0, my: 0, use: false }], 'у меню — стоїмо');
  assert.deepEqual(keys, ['a', 'down']);
  t += 100; a.poll(); assert.equal(keys.length, 2, 'до автоповтору');
  t += 400; a.poll(); assert.equal(keys.at(-1), 'down', 'автоповтор');
  gp = [pad(0, [0, 0], [9])]; a.poll();
  assert.equal(a.restartPressed(), false);
  assert.equal(keys.at(-1), 'start');
  // закрили меню з затиснутою A — гра її не бачить до відпускання
  gp = [pad(0, [0, 0], [0])]; a.poll();
  a.setMenu(null);
  assert.equal(a.poll()[0].use, false);
  gp = [pad(0, [0, 0])]; a.poll();
  gp = [pad(0, [0, 0], [0])];
  assert.equal(a.poll()[0].use, true);
});

test('кермо й педалі — не пади; вібрація за гравцем і всім на поразку; вимикач', () => {
  const a = createInput({ players: 2 });
  gp = [pad(0, [0, 0]), pad(1, [0, -1], [], 'Logitech G29 Driving Force Racing Wheel')];
  assert.equal(a.padCount(), 1);
  shakes.length = 0;
  a.rumble(1, 'hit');                 // p=2, один пад — він у гравця 1
  assert.deepEqual(shakes, [[0, 55]]);
  gp = [pad(0, [0, 0]), pad(1, [0, 0])];
  shakes.length = 0; a.rumble(-1, 'lost');
  assert.deepEqual(shakes, [[0, 420], [1, 420]]);
  a.setRumble(false); shakes.length = 0; a.rumble(0, 'hit');
  assert.equal(shakes.length, 0);
});
