// «Колія» — керування: клавіатура й пади (Gamepad API), до 2 локальних гравців. Власник: K2.
// Контракт: createInput(opts?) → { poll(): [{ mx, my (-1..1), use (затиснуто) }] для гравців 0..1,
//   restartPressed(): true раз на натиск R / Start, setPlayers(n), dispose() }
//   opts.players — скільки локальних гравців (1..2); без нього — з адреси ?p= (як у main.js).
// Клавіші: гравець 0 — WASD + Space/E; гравець 1 — стрілки + Enter/правий Shift; R — рестарт.
// Пади: лівий стік або хрестовина, A — дія, Start — рестарт; мертва зона 0,25; осі, відхилені вже при першому
//   погляді на пад (педаль/ручка газу стоїть на -1), ігноруємо назавжди — інакше «гравець сам іде».
// Розподіл: p=1 — гравцю 0 уся клавіатура й перший пад; p=2 і два пади — по паду кожному (плюс своя половина
//   клавіатури); p=2 і один пад — гравцю 0 уся клавіатура, гравцю 1 пад; без падів — половини клавіатури.
const DEAD = 0.25, STUCK = 0.5;
const K0 = { up: ['KeyW'], down: ['KeyS'], left: ['KeyA'], right: ['KeyD'], use: ['Space', 'KeyE'] };
const K1 = { up: ['ArrowUp'], down: ['ArrowDown'], left: ['ArrowLeft'], right: ['ArrowRight'], use: ['Enter', 'NumpadEnter', 'ShiftRight'] };
const KALL = { up: [...K0.up, ...K1.up], down: [...K0.down, ...K1.down], left: [...K0.left, ...K1.left], right: [...K0.right, ...K1.right], use: [...K0.use, ...K1.use] };
const GAME_KEYS = new Set([...Object.values(KALL).flat(), 'KeyR']);

function playersFromUrl() {
  try { return Math.max(1, Math.min(2, +(new URLSearchParams(location.search).get('p') || 1))); } catch { return 1; }
}

export function createInput(opts = {}) {
  let players = opts.players || playersFromUrl();
  const down = new Set();
  let restart = false;
  const typing = (e) => { const t = e.target; return t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT' || t.isContentEditable); };
  const kd = (e) => {
    if (typing(e) || e.ctrlKey || e.metaKey || e.altKey) return;
    if (!GAME_KEYS.has(e.code)) return;
    e.preventDefault();
    if (e.code === 'KeyR' && !e.repeat) restart = true;
    down.add(e.code);
  };
  const ku = (e) => { if (GAME_KEYS.has(e.code) && !typing(e)) e.preventDefault(); down.delete(e.code); };
  const blur = () => down.clear();
  addEventListener('keydown', kd); addEventListener('keyup', ku); addEventListener('blur', blur);

  const anyDown = (codes) => codes.some((c) => down.has(c));
  const keyb = (m) => ({
    mx: (anyDown(m.right) ? 1 : 0) - (anyDown(m.left) ? 1 : 0),
    my: (anyDown(m.down) ? 1 : 0) - (anyDown(m.up) ? 1 : 0),
    use: anyDown(m.use),
  });

  // --- пади ---
  const stuck = new Map();        // index+id → [bool × осі] відхилені при першому погляді
  const startWas = new Map();     // index+id → чи був Start затиснутий (фронт натиску)
  function pads() {
    let list = [];
    try { list = navigator.getGamepads ? [...navigator.getGamepads()] : []; } catch { }
    return list.filter((p) => p && p.connected !== false).sort((a, b) => a.index - b.index);
  }
  const btn = (p, i) => { const b = p.buttons && p.buttons[i]; return !!b && (b.pressed || b.value > 0.5); };
  function readPad(p) {
    const key = p.index + ' ' + p.id;
    const a = p.axes || [];
    let st = stuck.get(key);
    if (!st) { st = [0, 1].map((i) => Math.abs(a[i] || 0) > STUCK); stuck.set(key, st); }
    let ax = st[0] ? 0 : a[0] || 0, ay = st[1] ? 0 : a[1] || 0;
    const mag = Math.hypot(ax, ay);
    if (mag < DEAD) { ax = 0; ay = 0; }
    else { const k = Math.min(1, (mag - DEAD) / (1 - DEAD)) / mag; ax *= k; ay *= k; }
    // хрестовина (стандартна розкладка: 12 ↑, 13 ↓, 14 ←, 15 →)
    const dx = (btn(p, 15) ? 1 : 0) - (btn(p, 14) ? 1 : 0), dy = (btn(p, 13) ? 1 : 0) - (btn(p, 12) ? 1 : 0);
    if (dx || dy) { ax = dx; ay = dy; }
    const start = btn(p, 9);
    if (start && !startWas.get(key)) restart = true;
    startWas.set(key, start);
    return { mx: ax, my: ay, use: btn(p, 0) };
  }

  const merge = (...src) => {
    const o = { mx: 0, my: 0, use: false };
    for (const v of src) { if (!v) continue; o.mx += v.mx; o.my += v.my; o.use = o.use || v.use; }
    o.mx = Math.max(-1, Math.min(1, o.mx)); o.my = Math.max(-1, Math.min(1, o.my));
    return o;
  };

  return {
    poll() {
      const ps = pads().map(readPad);     // читаємо всі — щоб Start з будь-якого пада рестартив
      if (players <= 1) return [merge(keyb(KALL), ps[0])];
      if (ps.length >= 2) return [merge(keyb(K0), ps[0]), merge(keyb(K1), ps[1])];
      if (ps.length === 1) return [keyb(KALL), ps[0]];
      return [keyb(K0), keyb(K1)];
    },
    restartPressed() { const r = restart; restart = false; return r; },
    setPlayers(n) { players = Math.max(1, Math.min(2, n | 0)); },
    dispose() { removeEventListener('keydown', kd); removeEventListener('keyup', ku); removeEventListener('blur', blur); },
  };
}
