// «Колія» — керування: клавіатура й пади (Gamepad API), до 2 локальних гравців. Власник: K2 (пад і меню — K12).
// Контракт: createInput(opts?) → { poll(): [{ mx, my (-1..1), use (затиснуто) }] для гравців 0..1,
//   restartPressed(): true раз на натиск R / Start, setPlayers(n), dispose(),
//   padCount(): скільки справжніх падів (керма й педалі не рахуються),
//   setMenu(fn | null): поки меню відкрите — гравці стоять (poll дає нулі), а натиски пада йдуть у fn(key):
//     'up'|'down'|'left'|'right' (з автоповтором), 'a', 'b', 'menu' (Select/View), 'start';
//   onMenuKey(fn): Select/View, коли меню закрите (відкрити панель),
//   swallow(): кнопки, затиснуті зараз, гра не бачить до відпускання (A, що закрив екран, не стає дією),
//   rumble(p | -1, 'hit' | 'lost'): коротка вібрація пада гравця p (-1 — усіх), setRumble(on) }
//   opts.players — скільки локальних гравців (1..2); без нього — з адреси ?p= (як у main.js).
// Клавіші: гравець 0 — WASD + Space/E; гравець 1 — стрілки + Enter/правий Shift; R — рестарт.
// K16 (кооп K18): poll() дає ще throw/horn — true раз на натиск: П1 Q / F, П2 правий Ctrl, Num0 / «/», Num.; пад B / Y.
//   hasPad(p) — чи має локальний гравець p пад (за тим самим розподілом), для підказок.
// Пади: лівий стік або хрестовина, A — дія, Start — рестарт, Select/View — панель; мертва зона 0,25; осі, відхилені
//   вже при першому погляді на пад (педаль/ручка газу стоїть на -1), ігноруємо назавжди — інакше «гравець сам іде».
//   Керма, педалі, КПП (як sim() у web/static/pad.js) — не пади: їх не читаємо взагалі.
// Розподіл: p=1 — гравцю 0 уся клавіатура й перший пад; p=2 і два пади — по паду кожному (плюс своя половина
//   клавіатури); p=2 і один пад — гравцю 0 уся клавіатура, гравцю 1 пад; без падів — половини клавіатури.
const DEAD = 0.25, STUCK = 0.5;
const BA = 0, BB = 1, SELECT = 8, START = 9, UP = 12, DOWN = 13, LEFT = 14, RIGHT = 15;
const REPEAT_FIRST = 380, REPEAT_NEXT = 110;
const BY = 3;   // K16: кооп K18 — B (BB) кидок, Y гудок
// throw/horn — кооп-фішки K18 (K16 переніс з coop-keys.js): на натиск, не затиснуто
const K0 = { up: ['KeyW'], down: ['KeyS'], left: ['KeyA'], right: ['KeyD'], use: ['Space', 'KeyE'], throw: ['KeyQ'], horn: ['KeyF'] };
const K1 = { up: ['ArrowUp'], down: ['ArrowDown'], left: ['ArrowLeft'], right: ['ArrowRight'], use: ['Enter', 'NumpadEnter', 'ShiftRight'],
  throw: ['ControlRight', 'Numpad0'], horn: ['Slash', 'NumpadDecimal'] };
const KALL = {};
for (const k in K0) KALL[k] = [...K0[k], ...K1[k]];
const GAME_KEYS = new Set([...Object.values(KALL).flat(), 'KeyR']);

// керма й педалі — не пади (той самий список, що в web/static/pad.js)
const SIM_NAME = /wheel|racing|driving force|steering|pedal|shifter|handbrake|hotas|throttle|flight|yoke|rudder|simucube|simagic|cammus/i;
const SIM_VENDOR = ['0eb7', '346e'];
const SIM_PRODUCT = ['046d:c24f', '046d:c260', '046d:c262', '046d:c266', '046d:c26e', '046d:c29b', '046d:c299', '046d:c29a',
  '046d:c298', '046d:c294', '046d:c295', '046d:ca03', '044f:b65d', '044f:b65e', '044f:b669', '044f:b66e', '044f:b677',
  '044f:b67f', '044f:b696'];
function sim(p) {
  const id = String(p.id || '');
  if (SIM_NAME.test(id)) return true;
  const m = /Vendor:\s*([0-9a-f]{1,4})\s+Product:\s*([0-9a-f]{1,4})/i.exec(id) || /^([0-9a-f]{1,4})-([0-9a-f]{1,4})-/i.exec(id);
  if (!m) return false;
  const u = m[1].toLowerCase().padStart(4, '0') + ':' + m[2].toLowerCase().padStart(4, '0');
  return SIM_VENDOR.includes(u.slice(0, 4)) || SIM_PRODUCT.includes(u);
}

function playersFromUrl() {
  try { return Math.max(1, Math.min(2, +(new URLSearchParams(location.search).get('p') || 1))); } catch { return 1; }
}
const now = () => (globalThis.performance ? performance.now() : Date.now());

export function createInput(opts = {}) {
  let players = opts.players || playersFromUrl();
  const down = new Set();
  let restart = false;
  let menu = null, onMenu = null, rumbleOn = true;
  const typing = (e) => { const t = e.target; return t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT' || t.isContentEditable); };
  const hits = new Set();         // K16: клавіші, натиснуті з минулого poll (кидок/гудок — на натиск)
  const kd = (e) => {
    if (typing(e) || (e.ctrlKey && e.code !== 'ControlRight') || e.metaKey || e.altKey) return;
    if (!GAME_KEYS.has(e.code)) return;
    e.preventDefault();
    if (e.code === 'KeyR' && !e.repeat) restart = true;
    if (!e.repeat && !menu) hits.add(e.code);
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
    throw: m.throw.some((c) => hits.has(c)),
    horn: m.horn.some((c) => hits.has(c)),
  });
  const padHits = new Map();      // K16: index+id → Set('throw'|'horn') — фронти B/Y з минулого poll

  // --- пади ---
  const stuck = new Map();        // index+id → [bool × осі] відхилені при першому погляді
  const was = new Map();          // index+id → [bool × кнопки] попередній погляд (фронти натисків)
  const muted = new Map();        // index+id → Set кнопок, яких не видно до відпускання (swallow)
  const rep = new Map();          // index+id → { dir, next } автоповтор напрямку в меню
  function pads() {
    let list = [];
    try { list = navigator.getGamepads ? [...navigator.getGamepads()] : []; } catch { }
    return list.filter((p) => p && p.connected !== false && !sim(p)).sort((a, b) => a.index - b.index);
  }
  const btn = (p, i) => { const b = p.buttons && p.buttons[i]; return !!b && (b.pressed || b.value > 0.5); };
  const keyOf = (p) => p.index + ' ' + p.id;
  function stick(p, key) {
    const a = p.axes || [];
    let st = stuck.get(key);
    if (!st) { st = [0, 1].map((i) => Math.abs(a[i] || 0) > STUCK); stuck.set(key, st); }
    let ax = st[0] ? 0 : a[0] || 0, ay = st[1] ? 0 : a[1] || 0;
    const mag = Math.hypot(ax, ay);
    if (mag < DEAD) { ax = 0; ay = 0; }
    else { const k = Math.min(1, (mag - DEAD) / (1 - DEAD)) / mag; ax *= k; ay *= k; }
    return [ax, ay];
  }
  // Фронти кнопок: Start → рестарт (меню закрите), Select → панель, у меню — навігація. Викликається з poll і
  // restartPressed — тобто й щокадру, коли гра на паузі (стартовий екран, відкрита панель).
  function scan() {
    const list = pads(), t = now();
    for (const p of list) {
      const key = keyOf(p);
      const prev = was.get(key) || [];
      const cur = [];
      const n = Math.max(17, p.buttons ? p.buttons.length : 0);
      for (let i = 0; i < n; i++) cur[i] = btn(p, i);
      const mu = muted.get(key);
      if (mu) for (const i of [...mu]) if (!cur[i]) mu.delete(i);
      const hit = (i) => cur[i] && !prev[i];
      if (menu) {
        if (hit(SELECT)) menu('menu');
        if (hit(START)) menu('start');
        if (hit(BA)) menu('a');
        if (hit(BB)) menu('b');
        // напрямок: хрестовина або стік (поріг 0,6), з автоповтором
        const [ax, ay] = stick(p, key);
        let dir = cur[UP] || ay < -0.6 ? 'up' : cur[DOWN] || ay > 0.6 ? 'down' : cur[LEFT] || ax < -0.6 ? 'left' : cur[RIGHT] || ax > 0.6 ? 'right' : '';
        const r = rep.get(key);
        if (!dir) rep.delete(key);
        else if (!r || r.dir !== dir) { rep.set(key, { dir, next: t + REPEAT_FIRST }); menu(dir); }
        else if (t >= r.next) { r.next = t + REPEAT_NEXT; menu(dir); }
      } else {
        rep.delete(key);
        if (hit(START)) restart = true;
        if (hit(BB) || hit(BY)) { const h = padHits.get(key) || new Set(); if (hit(BB)) h.add('throw'); if (hit(BY)) h.add('horn'); padHits.set(key, h); }
        if (hit(SELECT)) onMenu?.();
      }
      was.set(key, cur);
    }
    return list;
  }
  function readPad(p) {
    const key = keyOf(p);
    let [ax, ay] = stick(p, key);
    const mu = muted.get(key);
    const b = (i) => btn(p, i) && !(mu && mu.has(i));
    // хрестовина (стандартна розкладка: 12 ↑, 13 ↓, 14 ←, 15 →)
    const dx = (b(RIGHT) ? 1 : 0) - (b(LEFT) ? 1 : 0), dy = (b(DOWN) ? 1 : 0) - (b(UP) ? 1 : 0);
    if (dx || dy) { ax = dx; ay = dy; }
    const h = padHits.get(key);
    return { mx: ax, my: ay, use: b(BA), throw: !!h?.has('throw'), horn: !!h?.has('horn') };
  }

  const merge = (...src) => {
    const o = { mx: 0, my: 0, use: false, throw: false, horn: false };
    for (const v of src) { if (!v) continue; o.mx += v.mx; o.my += v.my; o.use = o.use || v.use; o.throw = o.throw || v.throw; o.horn = o.horn || v.horn; }
    o.mx = Math.max(-1, Math.min(1, o.mx)); o.my = Math.max(-1, Math.min(1, o.my));
    return o;
  };
  const still = () => ({ mx: 0, my: 0, use: false });
  // який пад у якого гравця (як у poll)
  function padOf(pl, list) {
    if (players <= 1) return pl === 0 ? list[0] : null;
    if (list.length >= 2) return list[pl];
    if (list.length === 1) return pl === 1 ? list[0] : null;
    return null;
  }
  function shake(p, kind) {
    const a = p && p.vibrationActuator;
    if (!a || typeof a.playEffect !== 'function') return;
    const fx = kind === 'lost' ? { duration: 420, strongMagnitude: 0.9, weakMagnitude: 0.6 }
      : { duration: 55, strongMagnitude: 0.15, weakMagnitude: 0.45 };
    try { const r = a.playEffect('dual-rumble', { startDelay: 0, ...fx }); r?.catch?.(() => { }); } catch { }
  }

  return {
    poll() {
      const list = scan();
      if (menu) { hits.clear(); padHits.clear(); return players <= 1 ? [still()] : [still(), still()]; }
      const ps = list.map(readPad);     // читаємо всі — щоб Start з будь-якого пада рестартив
      let out;
      if (players <= 1) out = [merge(keyb(KALL), ps[0])];
      else if (ps.length >= 2) out = [merge(keyb(K0), ps[0]), merge(keyb(K1), ps[1])];
      else if (ps.length === 1) out = [merge(keyb(KALL)), merge(ps[0])];
      else out = [keyb(K0), keyb(K1)];
      hits.clear(); padHits.clear();   // натиски кидка/гудка — один раз
      return out;
    },
    /** K16: у кого з локальних гравців пад (для підказок «A» чи «Space») — як розподіляє poll */
    hasPad(pl) { const n = pads().length; return players <= 1 ? pl === 0 && n > 0 : n >= 2 ? pl < 2 : n === 1 && pl === 1; },
    restartPressed() { scan(); const r = restart; restart = false; return r; },
    setPlayers(n) { players = Math.max(1, Math.min(2, n | 0)); },
    padCount() { return pads().length; },
    setMenu(fn) { menu = typeof fn === 'function' ? fn : null; rep.clear(); if (!menu) this.swallow(); },
    onMenuKey(fn) { onMenu = fn; },
    swallow() {
      for (const p of pads()) {
        const key = keyOf(p), s = muted.get(key) || new Set();
        for (let i = 0; i < (p.buttons ? p.buttons.length : 0); i++) if (btn(p, i)) s.add(i);
        muted.set(key, s);
      }
    },
    rumble(pl, kind = 'hit') {
      if (!rumbleOn) return;
      const list = pads();
      if (pl < 0) { for (const p of list) shake(p, kind); return; }
      shake(padOf(pl, list), kind);
    },
    setRumble(on) { rumbleOn = !!on; },
    get rumbleOn() { return rumbleOn; },
    dispose() { removeEventListener('keydown', kd); removeEventListener('keyup', ku); removeEventListener('blur', blur); },
  };
}
