// «Колія» — досягнення 🏆 (K50). Власник: K50.
// Локально (localStorage 'kol-ach'), рахуються лише з s.ev і стану ПІСЛЯ кроку — правил і hash не чіпають.
// Ядро без DOM (тестується в Node):
//   ACH — список { id, icon, name, desc, goal? } (goal — накопичувальні «за всі партії»)
//   createAch({ store? }) → { tick(ev, s, ctx) → [нові ACH], list() → [{ ...a, got, n }], count() → [відкрито, усього], reset() }
//   ctx = { bot(p) → true, якщо p — бот; daily: bool (маршрут дня цього сіду); chumak: bool (тема «Чумацький шлях») }
//   store = { get() → рядок | null, set(рядок) } (типово — localStorage у try/catch; приватне вікно — просто без пам'яті)
// Тик рахуємо раз (мережевий відкат тики не подвоює); нова партія (інший s чи тик назад) — скидаємо «за партію».
// Гачки: hud.js (events → тост «🏆 …»), menu.js («🏆 Досягнення» — сірі/відкриті), main.js — позначено «K50».
import { G, I, TPS } from './rules.js';

export const STORE_KEY = 'kol-ach';
export const ACH = [
  { id: 'first', icon: '🏁', name: 'Перша станція', desc: 'Доїхати до станції' },
  { id: 'bridge', icon: '🌉', name: 'Міст через річку', desc: 'Покласти дерево на воду — місток' },
  { id: 'dunk', icon: '🏀', name: 'Данк у вагон', desc: 'Кинути дерево чи залізо просто в склад' },
  { id: 'fireman', icon: '🧯', name: 'Пожежник', desc: 'Залити котел 5 разів (за всі партії)', goal: 5 },
  { id: 'nofire', icon: '❄', name: 'Без пожеж', desc: 'Доїхати, жодного разу не перегрівши котел' },
  { id: 'railman', icon: '🛤', name: 'Колійник', desc: 'Покласти 100 рейок (за всі партії)', goal: 100 },
  { id: 'endless5', icon: '♾', name: 'Нескінченні 5 станцій', desc: 'У нескінченній — 5 станцій за партію' },
  { id: 'daily', icon: '📅', name: 'Маршрут дня', desc: 'Доїхати маршрутом дня' },
  { id: 'tutorial', icon: '🎓', name: 'Навчання пройдено', desc: 'Пройти навчальну смугу' },
  { id: 'goose', icon: '🪿', name: 'Гуска не вкрала нічого', desc: 'Доїхати з гусьми, і жодна нічого не вкрала' },
  { id: 'bonk', icon: '💫', name: 'Бац!', desc: 'Приголомшити друга інструментом' },
  { id: 'duo5', icon: '👥', name: 'Удвох за 5 хв', desc: 'Двоє живих гравців доїхали за 5 хвилин' },
  { id: 'chumak', icon: '🐂', name: 'Чумак', desc: 'Доїхати в темі «Чумацький шлях»' },
  { id: 'catch', icon: '🧤', name: 'Воротар', desc: 'Зловити кинуте другом' },
  { id: 'scare', icon: '🪿', name: 'Гусятник', desc: 'Налякати гуску й відбити крадене' },
];
const BY_ID = Object.fromEntries(ACH.map((a) => [a.id, a]));
const DUO_TICKS = 5 * 60 * TPS;

function lsStore() {
  return {
    get() { try { return localStorage.getItem(STORE_KEY); } catch { return null; } },
    set(v) { try { localStorage.setItem(STORE_KEY, v); } catch { } },
  };
}

export function createAch(opts = {}) {
  const store = opts.store ?? lsStore();
  let mem = { got: {}, n: {} };
  try { const o = JSON.parse(store.get() || '{}'); if (o && typeof o === 'object') mem = { got: o.got ?? {}, n: o.n ?? {} }; } catch { }
  const save = () => { try { store.set(JSON.stringify(mem)); } catch { } };

  // «за партію»
  let gs = null, lastTick = -1, g = null;
  const fresh = () => ({ hot: false, geese: false, stole: false, railMax: 0, fly: [], done: false });

  function tick(ev, s, ctx = {}) {
    if (!s || !ev) return [];
    const tk = s.tick ?? 0;
    if (s !== gs || tk < lastTick) { gs = s; g = fresh(); lastTick = -1; g.railMax = Math.max(0, s.stats?.railsLaid ?? 0); }
    if (tk <= lastTick) return [];
    lastTick = tk;
    const bot = (p) => p === undefined || p < 0 || !!ctx.bot?.(p);
    const tut = s.params?.mode === 'tutorial' || !!s.tutorial;
    const out = [];
    let dirty = false;
    const give = (id) => { if (mem.got[id]) return; mem.got[id] = Date.now(); out.push(BY_ID[id]); dirty = true; };
    const add = (id, k = 1) => { const a = BY_ID[id]; mem.n[id] = (mem.n[id] | 0) + k; dirty = true; if (mem.n[id] >= a.goal) give(id); };

    // кинуте, що приземлилось цього тику (було в польоті й зникло): данк — 'drop' у тій самій клітинці тим самим гравцем
    const nowFly = s.flying ?? [];
    const landed = g.fly.filter((f) => !nowFly.includes(f));
    for (const e of ev) {
      switch (e.t) {
        case 'rail':
          if (!bot(e.p) && s.ground?.[e.y * s.w + e.x] === G.BRIDGE) give('bridge');
          break;
        case 'drop':
          if (!bot(e.p) && landed.some((f) => f.tx === e.x && f.ty === e.y && f.p === e.p && (f.k === I.WOOD || f.k === I.IRON))) give('dunk');
          break;
        case 'douse': if (!bot(e.p)) add('fireman'); break;
        case 'stun': if (!bot(e.by) && e.p !== e.by) give('bonk'); break;
        case 'catch': if (!bot(e.p) && e.from !== e.p) give('catch'); break;
        case 'scare': if (!bot(e.p)) give('scare'); break;
        default: break;
      }
    }
    // ті самі об'єкти (правила мутують їх на місці й вирізають при приземленні); k/tx/ty/p у польоті не міняються
    g.fly = [...nowFly];

    // рейки за всі партії — «найвища вода» railsLaid за партію (розібрав і поклав знову — не рахується двічі)
    const rl = s.stats?.railsLaid ?? 0;
    if (rl > g.railMax) { if (!tut) add('railman', rl - g.railMax); g.railMax = rl; }
    if ((s.train?.heat ?? 0) >= 1000 || (s.train?.fire ?? 0) > 0) g.hot = true;
    if (s.geese?.length) g.geese = true;
    if (s.geese?.some((q) => q.hold)) g.stole = true;
    if (s.endless && (s.endless.stations ?? 0) >= 5 && !tut) give('endless5');

    if (s.status === 'won' && !g.done) {
      g.done = true;
      if (tut) give('tutorial');
      else {
        give('first');
        if (!g.hot) give('nofire');
        if (ctx.daily) give('daily');
        if (g.geese && !g.stole) give('goose');
        if (ctx.chumak) give('chumak');
        const humans = (s.players ?? []).filter((pl, i) => pl && !pl.out && !bot(i)).length;
        if (humans >= 2 && tk <= DUO_TICKS) give('duo5');
      }
    }
    // нескінченна: кожна станція — теж «станція»
    if (!tut && s.endless && ev.some((e) => e.t === 'station')) give('first');
    if (dirty) save();
    return out;
  }

  return {
    tick,
    list: () => ACH.map((a) => ({ ...a, got: !!mem.got[a.id], n: Math.min(a.goal ?? 0, mem.n[a.id] | 0) })),
    count: () => [ACH.filter((a) => mem.got[a.id]).length, ACH.length],
    reset() { mem = { got: {}, n: {} }; save(); },
  };
}

// один на сторінку — і HUD (тости), і меню (список) бачать ту саму пам'ять
let shared = null;
export const achievements = () => (shared ??= createAch());
