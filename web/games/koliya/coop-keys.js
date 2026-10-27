// «Колія» K18 — тимчасові кнопки кооп-фішок, доки їх нема в input.js (input.js — K12/K2; оркестратор може перенести
// сюди описане й прибрати цей файл). Лише кидок і гудок — решта фішок на наявних кнопках.
// createCoopKeys() → { poll(cmds, local) — дописує в cmds { p, k:'throw' } / { p, k:'horn' } на натиск, dispose() }
// Клавіші: гравець 0 — Q кидок, F гудок; гравець 1 — правий Ctrl / Num0 кидок, «/» (укр. «.») / Num. гудок.
//   p=1 — усі клавіші гравцю 0. Пади: B — кидок, Y — гудок; розподіл падів як у input.js
//   (p=1 — перший пад гравцю 0; p=2 і два пади — по паду кожному; p=2 і один пад — пад гравцю 1).
const KEYS = {
  KeyQ: [0, 'throw'], KeyF: [0, 'horn'],
  ControlRight: [1, 'throw'], Numpad0: [1, 'throw'], Slash: [1, 'horn'], NumpadDecimal: [1, 'horn'],
};
const PAD = [[1, 'throw'], [3, 'horn']];   // кнопка стандартної розкладки → команда

export function createCoopKeys() {
  const queue = [];
  const was = new Map();   // пад+кнопка → чи був затиснутий
  const typing = (e) => { const t = e.target; return t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT' || t.isContentEditable); };
  const kd = (e) => {
    const m = KEYS[e.code];
    if (!m || typing(e) || e.metaKey || e.altKey || (e.ctrlKey && e.code !== 'ControlRight')) return;
    e.preventDefault();
    if (!e.repeat) queue.push(m);
  };
  addEventListener('keydown', kd);
  return {
    poll(cmds, local = 1) {
      for (const [p, k] of queue.splice(0)) cmds.push({ p: local <= 1 ? 0 : p, k });
      let pads = [];
      try { pads = navigator.getGamepads ? [...navigator.getGamepads()].filter((g) => g && g.connected !== false).sort((a, b) => a.index - b.index) : []; } catch { }
      pads.forEach((g, j) => {
        const p = local <= 1 ? (j === 0 ? 0 : -1) : pads.length >= 2 ? (j < 2 ? j : -1) : 1;
        for (const [b, k] of PAD) {
          const key = g.index + ' ' + b, btn = g.buttons && g.buttons[b];
          const now = !!btn && (btn.pressed || btn.value > 0.5);
          if (now && !was.get(key) && p >= 0) cmds.push({ p, k });
          was.set(key, now);
        }
      });
    },
    dispose() { removeEventListener('keydown', kd); },
  };
}
