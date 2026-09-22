/*
  Фейковий джойстик для стендів (pad.html, mock.html). Підміняє navigator.getGamepads своїм падом,
  малює збоку панельку з кнопками й стіками і дає кілька функцій для перевірок із консолі:

    padHit(i, ms)      — натиснути кнопку i й відпустити (типово 90 мс)
    padHold(i, on)     — тримати / відпустити
    padAxis(i, v)      — поставити вісь (-1…1): 0 LX, 1 LY, 2 RX, 3 RY
    padNudge(i, v, ms) — штовхнути вісь і відпустити

  id пада — такий самий, як у Steam Deck (вендор 28de), щоб перевірялись і підказки для Деки.
  Підключати ДО web/static/pad.js.
*/
(() => {
  'use strict';

  const FAKE = {
    connected: true, index: 0, mapping: 'standard', timestamp: 0,
    id: 'Microsoft X-Box 360 pad 0 (STANDARD GAMEPAD Vendor: 28de Product: 11ff)',
    buttons: Array.from({ length: 17 }, () => ({ pressed: false, touched: false, value: 0 })),
    axes: [0, 0, 0, 0],
  };
  let live = true;
  navigator.getGamepads = () => [live ? FAKE : null, null, null, null];

  // Опитування пада крутиться на requestAnimationFrame, а він мовчить, поки браузер не малює сторінку:
  // схована вкладка, згорнуте чи перекрите вікно. У проді це саме те, що треба (пад не має керувати тим,
  // чого не видно), а стенду ламає всі перевірки — і document.hidden тут не рятує, він лишається false.
  // Тому в стенді rAF — звичайний таймер. Тільки в стенді: у web/static/pad.js жодних підмін нема.
  window.requestAnimationFrame = (fn) => setTimeout(() => fn(performance.now()), 16);
  window.cancelAnimationFrame = (id) => clearTimeout(id);

  const touch = () => { FAKE.timestamp = performance.now(); };
  const set = (i, on) => { FAKE.buttons[i].pressed = on; FAKE.buttons[i].value = on ? 1 : 0; touch(); };

  window.padHold = set;
  window.padHit = (i, ms) => { set(i, true); setTimeout(() => set(i, false), ms || 90); };
  window.padAxis = (i, v) => { FAKE.axes[i] = +v; touch(); };
  window.padNudge = (i, v, ms) => { window.padAxis(i, v); setTimeout(() => window.padAxis(i, 0), ms || 120); };
  window.padOff = () => { live = false; touch(); };

  const NAMES = [['A', 0], ['B', 1], ['X', 2], ['Y', 3], ['LB', 4], ['RB', 5], ['LT', 6], ['RT', 7],
    ['Sel', 8], ['Start', 9], ['R3', 11], ['↑', 12], ['↓', 13], ['←', 14], ['→', 15]];

  addEventListener('DOMContentLoaded', () => {
    const box = document.createElement('div');
    box.className = 'fakepad';
    box.setAttribute('data-pad-skip', '');   // кільце пада по самому паду не ходить
    box.innerHTML = '<b>Фейковий пад</b><div class="r">'
      + NAMES.map(([n, i]) => '<button type="button" data-b="' + i + '">' + n + '</button>').join('')
      + '</div>'
      + ['LX', 'LY', 'RX', 'RY'].map((n, i) => '<label>' + n
        + '<input type="range" min="-100" max="100" value="0" data-ax="' + i + '"></label>').join('');
    document.body.appendChild(box);
    box.querySelectorAll('[data-b]').forEach((b) => b.onclick = () => window.padHit(+b.dataset.b));
    box.querySelectorAll('[data-ax]').forEach((s) => {
      s.oninput = () => window.padAxis(+s.dataset.ax, s.value / 100);
      // Відпустив повзунок — стік повертається в центр, як справжній.
      s.onchange = () => { s.value = 0; window.padAxis(+s.dataset.ax, 0); };
    });
    const css = document.createElement('style');
    css.textContent = '.fakepad{position:fixed;right:12px;bottom:12px;z-index:90;width:290px;display:grid;gap:6px;'
      + 'padding:10px;border-radius:12px;background:var(--panel,#1c3328);border:1px solid var(--accent,#f4c542);'
      + 'box-shadow:0 16px 40px rgba(0,0,0,.6);font-size:12px}'
      + '.fakepad .r{display:flex;gap:4px;flex-wrap:wrap}.fakepad button{padding:4px 8px;font-size:12px}'
      + '.fakepad label{display:flex;align-items:center;gap:6px}.fakepad input{flex:1}';
    document.head.appendChild(css);
  });
})();
