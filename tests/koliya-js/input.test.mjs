// «Колія» — керування без браузера: заглушки addEventListener і navigator.getGamepads.
import test from 'node:test';
import assert from 'node:assert/strict';

const handlers = {};
globalThis.addEventListener = (t, f) => { handlers[t] = f; };
globalThis.removeEventListener = () => { };
let gp = [];
Object.defineProperty(globalThis, 'navigator', { value: { getGamepads: () => gp }, configurable: true, writable: true });
const { createInput } = await import('../../web/games/koliya/input.js');

const mmu = ({ mx, my, use }) => ({ mx, my, use });   // K16: poll дає ще throw/horn
const key = (t, code) => handlers[t]({ code, target: {}, preventDefault() { }, repeat: false });
const pad = (index, axes, pressed = []) => ({ index, id: 'pad' + index, connected: true, axes,
  buttons: Array.from({ length: 16 }, (_, i) => ({ pressed: pressed.includes(i), value: pressed.includes(i) ? 1 : 0 })) });

test('клавіатура: p=1 — уся клавіатура гравцю 0; p=2 — половини; R — рестарт раз', () => {
  gp = [];
  const a = createInput({ players: 1 });
  key('keydown', 'ArrowLeft'); key('keydown', 'Enter');
  let v = a.poll();
  assert.equal(v.length, 1);
  assert.deepEqual(mmu(v[0]), { mx: -1, my: 0, use: true });
  a.setPlayers(2);
  v = a.poll();
  assert.deepEqual(mmu(v[0]), { mx: 0, my: 0, use: false });
  assert.deepEqual(mmu(v[1]), { mx: -1, my: 0, use: true });
  key('keyup', 'ArrowLeft'); key('keyup', 'Enter');
  key('keydown', 'KeyR');
  assert.equal(a.restartPressed(), true);
  assert.equal(a.restartPressed(), false);
  key('keyup', 'KeyR');
});

test('пади: мертва зона, хрестовина, відхилена при підключенні вісь ігнорується, розподіл', () => {
  gp = [pad(0, [0.1, -1])];                    // вісь 1 — педаль на -1 при першому погляді
  const a = createInput({ players: 1 });
  let v = a.poll()[0];
  assert.equal(v.mx, 0); assert.equal(v.my, 0);
  gp = [pad(0, [0.8, 1], [0])];
  v = a.poll()[0];
  assert.ok(v.mx > 0.6 && v.mx <= 1);
  assert.equal(v.my, 0, 'залипла вісь лишається вимкненою');
  assert.equal(v.use, true);
  gp = [pad(0, [0, 0], [12])];
  assert.equal(a.poll()[0].my, -1, 'хрестовина ↑');
  // p=2, один пад: гравцю 0 уся клавіатура, гравцю 1 пад
  const b = createInput({ players: 2 });
  gp = [pad(0, [0, 0]), pad(1, [0, 0])]; b.poll();   // перший погляд — стіки в спокої
  gp = [pad(0, [0, 0.9])];
  key('keydown', 'ArrowRight');
  let w = b.poll();
  assert.equal(w[0].mx, 1);
  assert.ok(w[1].my > 0.8);
  key('keyup', 'ArrowRight');
  // два пади — по паду кожному
  gp = [pad(0, [-1, 0]), pad(1, [0, 0], [0, 9])];
  w = b.poll();
  assert.equal(w[0].mx, -1);
  assert.equal(w[1].use, true);
  assert.equal(b.restartPressed(), true, 'Start — рестарт');
  w = b.poll();
  assert.equal(b.restartPressed(), false, 'Start затиснутий — не повторює');
});

test('K16: кидок/гудок — раз на натиск; клавіші П1/П2, пад B/Y; hasPad', () => {
  gp = [];
  const a = createInput({ players: 2 });
  key('keydown', 'KeyQ'); key('keydown', 'Slash');
  let v = a.poll();
  assert.equal(v[0].throw, true); assert.equal(v[0].horn, false);
  assert.equal(v[1].horn, true); assert.equal(v[1].throw, false);
  v = a.poll();
  assert.equal(v[0].throw, false, 'затиснуто — не повторює');
  key('keyup', 'KeyQ'); key('keyup', 'Slash');
  assert.equal(a.hasPad(0), false);
  gp = [pad(0, [0, 0])];
  a.poll();
  assert.equal(a.hasPad(0), false); assert.equal(a.hasPad(1), true, 'p=2 і один пад — пад у П2');
  gp = [pad(0, [0, 0], [1, 3])];
  v = a.poll();
  assert.equal(v[1].throw, true); assert.equal(v[1].horn, true);
  assert.equal(a.poll()[1].throw, false);
});
