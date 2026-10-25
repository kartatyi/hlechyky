// Керування фурою з трьох джерел разом: кермо (осі й кнопки — з калібрування, бо в кожного керма й браузера
// вони свої), пад зі стандартною розкладкою (Steam Deck через Steam Input, Xbox, DualSense) і клавіатура.
// Кермо й педалі не обмежуємо швидкістю — людина кермує сама; пад і клавіатуру на швидкості «стишуємо», інакше
// фура на 80 км/год від одного натиску лягає на бік.

const SIM = /wheel|racing|driving force|steering|g2[0-9]|g9[0-9]{2}|pedal|shifter|fanatec|thrustmaster|moza|simagic|simucube|logitech g/i;
const KEY = 'fura.calib:';

/** Дії, які можна посадити на кнопку керма (у калібруванні) — ключ і що показати людині. */
export const ACTIONS = [
  ['couple', 'Зчепити / відчепити причіп'],
  ['camera', 'Перемкнути камеру'],
  ['horn', 'Сигнал'],
  ['handbrake', 'Ручник'],
  ['gearUp', 'Передача вгору (пелюстка)'],
  ['gearDown', 'Передача вниз (пелюстка)'],
  ['reset', 'Поставити фуру на колеса'],
  ['menu', 'Меню / пауза'],
];
export const H_GEARS = ['1', '2', '3', '4', '5', '6', 'R'];

/** Пад зі стандартною розкладкою й не кермо — йому калібрування не треба. */
export const isPad = (gp) => !!gp && gp.mapping === 'standard' && !SIM.test(gp.id);
export const looksLikeWheel = (gp) => !!gp && SIM.test(gp.id);
export const pads = () => {
  try { return [...(navigator.getGamepads ? navigator.getGamepads() : [])].filter(Boolean); } catch { return []; }
};
export const shortName = (id) => String(id || '').replace(/\s*\((?:STANDARD GAMEPAD\s*)?Vendor:.*$/i, '').replace(/\s+/g, ' ').trim() || 'пристрій';

export function loadCalib(id) {
  try { const s = localStorage.getItem(KEY + id); return s ? JSON.parse(s) : null; } catch { return null; }
}
export function saveCalib(id, c) {
  try { localStorage.setItem(KEY + id, JSON.stringify(c)); } catch { /* приватне вікно — житиме до закриття */ }
}

const clamp = (v, a, b) => (v < a ? a : v > b ? b : v);
const dz = (v, d) => (Math.abs(v) < d ? 0 : (v - Math.sign(v) * d) / (1 - d));

/** Нормоване кермо −1…1 (мінус — ліворуч) за трьома точками калібрування. */
function steerOf(gp, s) {
  const v = gp.axes[s.i];
  if (v == null) return 0;
  const left = (v - s.c) / (s.l - s.c);       // 0 у центрі, 1 біля лівого упору (знак осі нам байдужий)
  const right = (v - s.c) / (s.r - s.c);
  return left > 0 ? -clamp(left, 0, 1) : right > 0 ? clamp(right, 0, 1) : 0;
}
/** Педаль 0…1 за двома точками: відпущена й до підлоги. */
function pedalOf(gp, p) {
  if (!p) return 0;
  const v = gp.axes[p.i];
  if (v == null) return 0;
  const t = (v - p.rest) / (p.full - p.rest);
  return t < 0.03 ? 0 : clamp((t - 0.03) / 0.97, 0, 1);
}
const btn = (gp, i) => i != null && i >= 0 && !!(gp.buttons[i] && gp.buttons[i].pressed);

// Клавіші гри (див. poll і main.js). Поки гра відкрита, вони лише її: пробіл не тисне кнопку у фокусі (📻 чи ☰ у панелі
// після кліку мишею, кнопку сайту під грою), Enter теж, стрілки не гортають сторінку, Esc не виводить каркас зі столу.
const GAME_KEYS = new Set(['KeyW', 'KeyA', 'KeyS', 'KeyD', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Space', 'Enter',
  'KeyH', 'KeyT', 'KeyC', 'KeyE', 'KeyQ', 'KeyR', 'KeyL', 'KeyF', 'KeyM', 'Escape']);
/** Друкують: балачка, пошук — там клавіші людини, не фури. */
const typing = (t) => !!t && ((t.matches && t.matches('input, textarea, select')) || t.isContentEditable);
/** Вікно гри: там Enter і Пробіл тиснуть кнопку у фокусі самі (↑↓ і Esc вікну дає main.js navModal). */
const inBox = (t) => !!(t && t.closest && t.closest('.fura-modal'));

// Стандартна розкладка пада (W3C): 0 Ⓐ 1 Ⓑ 2 Ⓧ 3 Ⓨ 4 LB 5 RB 6 LT 7 RT 8 Back 9 Start 10 L3 11 R3 12–15 хрестовина
const PAD = { couple: 0, handbrake: 1, camera: 2, horn: 3, gearDown: 4, gearUp: 5, reset: 8, menu: 9 };

export class Input {
  constructor() {
    this.keys = new Set();
    this.kbSteer = 0;
    this.prev = new Map();        // id пристрою → набір натиснутих дій (для «натиснув щойно»)
    this.source = 'kbd';
    this.sourceAt = 0;
    this.uncal = [];              // керма/невідомі пристрої без калібрування
    this.enabled = true;
    this.lastKeyAt = 0;
    this.onKey = (e) => {
      // Лише справжні натиски: pad.js на час гри теж шле стрілки — ми читаємо пад самі, вдруге не треба
      if (!e.isTrusted || !this.enabled) return;
      const t = e.target;
      if (typing(t)) { if (e.type === 'keyup') this.keys.delete(e.code); return; }
      if (GAME_KEYS.has(e.code) && !e.ctrlKey && !e.metaKey && !e.altKey && !((e.code === 'Space' || e.code === 'Enter') && inBox(t))) e.preventDefault();
      if (e.type === 'keydown') {
        if (!this.keys.has(e.code)) this.pressed.add(e.code);
        this.keys.add(e.code);
        this.lastKeyAt = performance.now();
      } else this.keys.delete(e.code);
    };
    this.onBlur = () => this.keys.clear();
    this.pressed = new Set();
    window.addEventListener('keydown', this.onKey, true);
    window.addEventListener('keyup', this.onKey, true);
    window.addEventListener('blur', this.onBlur);
  }

  dispose() {
    window.removeEventListener('keydown', this.onKey, true);
    window.removeEventListener('keyup', this.onKey, true);
    window.removeEventListener('blur', this.onBlur);
  }

  /** Натиск клавіші, що стався з минулого опитування (для меню, F, M та інших). */
  took(code) { return this.pressed.has(code); }

  /**
   * Один знімок керування. kmh — швидкість (стишує пад і клавіатуру), dt — с між опитуваннями.
   * Повертає осі (steer −1…1, throttle/brake/clutch 0…1), утримувані (handbrake, horn), щойно натиснуті
   * (hit: Set дій) і вибрану передачу H-перемикача (hGear: '1'…'6' | 'R' | 'N' | null — перемикача нема).
   */
  poll(dt, kmh) {
    const k = this.keys;
    const out = { steer: 0, throttle: 0, brake: 0, clutch: 0, handbrake: false, horn: false, hit: new Set(),
      hGear: null, lookX: 0, lookY: 0, source: this.source, wheelSteer: null };
    const now = performance.now();
    const limit = 1 / (1 + Math.max(0, Math.abs(kmh)) / 45);     // 0 км/год — 1, 45 — ½, 90 — ⅓
    const use = (src, active) => { if (active) { this.source = src; this.sourceAt = now; } };

    // ---- клавіатура
    const kl = k.has('KeyA') || k.has('ArrowLeft');
    const kr = k.has('KeyD') || k.has('ArrowRight');
    const target = (kr ? 1 : 0) - (kl ? 1 : 0);
    const rate = target === 0 ? 3.2 : Math.sign(target) !== Math.sign(this.kbSteer) && this.kbSteer !== 0 ? 4.5 : 1.9;
    const goal = target * limit;
    this.kbSteer += clamp(goal - this.kbSteer, -rate * dt, rate * dt);
    out.steer += this.kbSteer;
    if (k.has('KeyW') || k.has('ArrowUp')) out.throttle = 1;
    if (k.has('KeyS') || k.has('ArrowDown')) out.brake = 1;
    if (k.has('Space')) out.handbrake = true;
    if (k.has('KeyH')) out.horn = true;
    if (this.took('KeyT') || this.took('Enter')) out.hit.add('couple');
    if (this.took('KeyC')) out.hit.add('camera');
    if (this.took('KeyE')) out.hit.add('gearUp');
    if (this.took('KeyQ')) out.hit.add('gearDown');
    if (this.took('KeyR')) out.hit.add('reset');
    if (this.took('Escape')) out.hit.add('menu');
    use('kbd', target !== 0 || out.throttle || out.brake);

    // ---- пади й керма
    const uncal = [];
    for (const gp of pads()) {
      const held = new Set();
      if (isPad(gp)) {
        const sx = dz(gp.axes[0] || 0, 0.1);
        const st = Math.sign(sx) * Math.pow(Math.abs(sx), 1.6) * limit;
        const th = gp.buttons[7] ? gp.buttons[7].value : 0;
        const br = gp.buttons[6] ? gp.buttons[6].value : 0;
        out.steer += st;
        out.throttle = Math.max(out.throttle, th > 0.04 ? th : 0);
        out.brake = Math.max(out.brake, br > 0.04 ? br : 0);
        out.lookX += dz(gp.axes[2] || 0, 0.15);
        out.lookY += dz(gp.axes[3] || 0, 0.15);
        for (const [a, i] of Object.entries(PAD)) if (btn(gp, i)) held.add(a);
        if (btn(gp, 12)) out.throttle = Math.max(out.throttle, 1);     // хрестовина — запасний газ і гальмо
        if (btn(gp, 13)) out.brake = Math.max(out.brake, 1);
        use('pad', Math.abs(sx) > 0 || th > 0.04 || br > 0.04);
      } else {
        const c = loadCalibCached(gp.id);
        if (!c || !c.steer) { uncal.push(gp); continue; }
        const st = steerOf(gp, c.steer);
        out.steer += st * (c.sens || 1);
        out.wheelSteer = st;
        const g = pedalOf(gp, c.gas), b = pedalOf(gp, c.brake), cl = pedalOf(gp, c.clutch);
        out.throttle = Math.max(out.throttle, g);
        out.brake = Math.max(out.brake, b);
        out.clutch = Math.max(out.clutch, cl);
        for (const [a] of ACTIONS) if (c.buttons && btn(gp, c.buttons[a])) held.add(a);
        if (c.gears && Object.keys(c.gears).length) {
          out.hGear = 'N';
          for (const gName of H_GEARS) if (btn(gp, c.gears[gName])) out.hGear = gName;
        }
        use('wheel', Math.abs(st) > 0.04 || g > 0 || b > 0);
      }
      const before = this.prev.get(gp.id) || new Set();
      for (const a of held) if (!before.has(a)) out.hit.add(a);
      if (held.has('handbrake')) out.handbrake = true;
      if (held.has('horn')) out.horn = true;
      this.prev.set(gp.id, held);
    }
    this.uncal = uncal;
    this.pressed.clear();
    out.steer = clamp(out.steer, -1, 1);
    out.source = this.source;
    return out;
  }
}

// Калібрування читаємо щокадру — з localStorage лише раз, далі з пам'яті (скидається після нового калібрування).
const cache = new Map();
function loadCalibCached(id) {
  if (!cache.has(id)) cache.set(id, loadCalib(id));
  return cache.get(id);
}
export function forgetCalibCache(id) { if (id) cache.delete(id); else cache.clear(); }
