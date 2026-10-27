// «Колія» — тема (K25): 'koliya' (паротяг, рейки, вокзал) | 'chumak' («Чумацький шлях»: воли тягнуть мажу, гать і битий
// шлях, ярмарок у Криму). Лише вигляд і слова — правила ті самі (rules.js про тему не знає).
//  theme() → 'koliya'|'chumak'; isChumak(); setTheme(name) — зберігає kol-theme і кличе слухачів; onTheme(cb) → відписка.
//  ?theme=chumak|koliya в адресі — лише на цю сторінку (не зберігається).
//  t(key, def, ...args) — рядок теми: у 'chumak' — CHUMAK[key] (рядок або функція від args), інакше def (у 'koliya' рядки
//   лишаються там, де були, — hud.js/tips.js передають їх як def).
//  mountThemeUi(panelRoot) — перемикач «Тема» в секції «🎨 Вигляд» панелі (looks.js, K5); нема секції — своя.
//  themeSfx(name) — заміна звуку audio.js у темі (воли «му» замість свистка й гудка) або null. Звук сам не вмикає.
// Без DOM на імпорті — node-тести (tips.test.mjs) імпортують tips.js → theme.js.

export const THEMES = { koliya: { label: '🚂 Колія' }, chumak: { label: '🐂 Чумацький шлях' } };
const KEY = 'kol-theme';

function initial() {
  try {
    const q = new URLSearchParams(globalThis.location?.search || '').get('theme');
    if (q && THEMES[q]) return q;
  } catch { }
  try { const v = globalThis.localStorage?.getItem(KEY); if (v && THEMES[v]) return v; } catch { }
  return 'koliya';
}
let cur = initial();
const subs = new Set();

export const theme = () => cur;
export const isChumak = () => cur === 'chumak';
export function setTheme(name) {
  if (!THEMES[name] || name === cur) return;
  cur = name;
  try { globalThis.localStorage?.setItem(KEY, name); } catch { }
  for (const cb of [...subs]) { try { cb(cur); } catch (e) { console.warn('Колія: тема', e); } }
}
export function onTheme(cb) { subs.add(cb); return () => subs.delete(cb); }

// ---------- словник «Чумацького шляху» ----------
const CHUMAK = {
  // піктограми
  'icon.loco': '🐂', 'icon.rails': '🪜', 'icon.station': '🎪', 'icon.heat': '💦',
  // HUD
  alarm: '⚠ Гать кінчається — мостіть далі!',
  count: 'воли рушають',
  fire: (sec) => `😩 ВОЛИ ЗНЕСИЛИЛИСЬ! Відро води волам · ${sec}`,
  overheatStop: '💦 Воли запріли — стали! Відро води волам',
  midWait: '🏪 Корчма при шляху!',
  'hold.rails': 'гать',
  'end.won': '🎪 Доїхали до ярмарку!',
  'end.fire': '😩 Воли знесилились',
  'end.derail': '💥 Віз загруз у бездоріжжі',
  'sum.rails': '🪜 Гаті вимощено',
  'start.title': '🐂 Чумацький шлях',
  'start.p1': 'Воли самі тягнуть мажу до ярмарку 🎪 — встигайте мостити гать перед ними.',
  'start.p2': 'Рубайте ліс 🪓, бийте скелі ⛏, несіть на віз-комору — кузня кує гать 🪜. Воли пріють: відро води з криниці 💧 волам, інакше знесиляться.',
  // підказки (tips.js)
  'tip.heatFull': (key) => `Воли пріють! Повне відро — волам, ${key} 💦`,
  'tip.heatEmpty': (key) => `Воли пріють! До криниці й ${key} — набрати відро`,
  'tip.heatDrop': 'Воли пріють! Кинь що в руках — і бігом по відро 🪣',
  'tip.heat': 'Воли пріють! Відро → криниця → воли',
  'tip.dead': (key) => `Глухий кут? Розбери останню гать: порожні руки й ${key}`,
  'tip.bridge': 'Річка: кидай дрова у воду — буде хмизова гребля',
  'tip.ford': 'Брід: тут мілина — гать мостиш без хмизу',
  'tip.roleChop': (kids) => `Вас ${kids}: один рубає, другий мостить. Ти — рубаєш 🪓`,
  'tip.roleLay': (kids) => `Вас ${kids}: один рубає, другий мостить. Ти — мостиш гать 🪜`,
  'tip.layBlocked': 'Мости гать перед шляхом — та спершу розчисть місце',
  'tip.lay': (key) => `Мости гать перед шляхом: стань поруч і ${key}`,
  'tip.store': (what) => `Неси ${what} на віз-комору — другий віз`,
  'tip.throw': (key) => `Можна кинути на віз: стань лицем до нього й ${key} 🎯`,
  'tip.railsReady': (key) => `Гать готова — візьми з воза-кузні (${key} біля воза)`,
  'tip.swamp': 'Болото — повільно, але гать мостити можна',
  pauseNet: '🌐 Гра в мережі — світ не стоїть, воли йдуть далі!',
  'tip.pick': 'Тепер кайло — скелі дають залізо, без нього кузня не скує гаті',
};

export function t(key, def, ...args) {
  if (cur !== 'chumak') return def;
  const v = CHUMAK[key];
  if (v === undefined) return def;
  return typeof v === 'function' ? v(...args) : v;
}

// ---------- звук: воли «му» (синтез, той самий інтерфейс SFX audio.js: (t, A) → тривалість) ----------
// «м» — закритий низький гук (синус), «у-у» — пилка з плавним спадом, і повітря крізь ніздрі (шум).
function moo(t, A, f = 128, len = 0.85) {
  A.tone(t, f * 0.92, 0.22, { vol: 0.11, a: 0.05, to: f });
  A.tone(t + 0.12, f, len, { type: 'sawtooth', vol: 0.05, a: 0.12, to: f * 0.74 });
  A.tone(t + 0.12, f * 2, len * 0.8, { type: 'triangle', vol: 0.03, a: 0.12, to: f * 1.5 });
  A.noise(t + 0.1, len * 0.9, { vol: 0.025, f: 480, q: 1.5, a: 0.15 });
  return 0.12 + len;
}
const SFX = {
  whistle: (t, A) => { moo(t, A, 132, 0.95); return moo(t + 0.25, A, 104, 0.9) + 0.25; },   // пара волів рушає/вітає
  horn: (t, A) => moo(t, A, 118, 0.6),                                                       // гравець гукнув — «му!»
};
export function themeSfx(name) { return cur === 'chumak' ? SFX[name] ?? null : null; }

// ---------- перемикач у панелі ----------
export function mountThemeUi(panelRoot) {
  if (!panelRoot || typeof document === 'undefined') return null;
  const el = (tag, cls, txt) => { const e = document.createElement(tag); if (cls) e.className = cls; if (txt != null) e.textContent = txt; return e; };
  let host = [...panelRoot.querySelectorAll('details.kol-grp')].find((d) => d.querySelector('summary')?.textContent.includes('Вигляд'));
  if (!host) { host = el('details', 'kol-grp'); host.open = true; host.append(el('summary', null, '🎨 Вигляд')); (panelRoot.querySelector('.kol-pan') || panelRoot).append(host); }
  const box = el('div', 'kol-p'), seg = el('div', 'kol-seg');
  const btns = Object.entries(THEMES).map(([k, v]) => {
    const b = el('button', null, v.label);
    b.onclick = () => { setTheme(k); mark(); };
    seg.append(b); return [k, b];
  });
  const mark = () => { for (const [k, b] of btns) b.classList.toggle('kol-on', k === cur); };
  mark();
  box.append(el('div', 'kol-lab', 'Тема'), seg);
  host.querySelector('summary').after(box);
  const off = onTheme(mark);
  return { dispose() { off(); box.remove(); } };
}
