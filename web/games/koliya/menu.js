// «Колія» — стартове меню (K30). Власник: K30. Малює вміст стартової картки HUD (hud.js кличе createMenu).
// Контракт: createMenu(box, { game: () => game, onPick(act, o), say?(text) }) →
//   { key(e) → bool (клавіатура: ↑↓/WS — вибір, ←→/AD — змінити, Enter/Space/E — обрати, Esc — назад у гру),
//     pad(k) ('up'|'down'|'left'|'right'|'a'|'b'|'start' з input.setMenu), sync(s) — щокадру, поки меню видно,
//     open(canResume) — показати знову (з паузи чи фіналу), opts() → { players, bots, seed } }
//  act: 'play' | 'tutorial' | 'daily' | 'endless' | 'net' | 'resume' | 'copy' | 'offline'; o = { players, bots, seed }.
//  Вибране пам'ятає localStorage 'kol-menu' { players, bots, seed: 'r' | число, last }; ?p= / ?bot= / ?seed= в адресі
//  переважують пам'ять лише на цей запуск (K46: у пам'ять не пишуться, поки їх не змінять у меню). У мережі (KOL.net) — лише «Грати», «Копіювати посилання для друга» і «Без мережі».
import { BIOME_NAMES, generate } from './gen.js';
import { MAX_PLAYERS } from './rules.js';
import { t } from './theme.js';   // K42: слова «Чумацького шляху»
import { achievements } from './achievements.js';   // K50: «🏆 Досягнення» — сірі/відкриті

const KEY = 'kol-menu';
export const PICKS = [8, 19, 23, 53, 43];   // K22: добірка сідів (_kolia/k22-seeds.md)
const PICK_TAG = { 8: 'м’який', 19: 'річка рано', 23: 'старт у лісі', 53: 'прохід рано', 43: 'найважчий' };
const ICON = { glade: '🌱', steppe: '🌾', grove: '🌳', stone: '🪨', river: '🌊', swamp: '🐸', pass: '⛰' };
export const BIOME_SHORT = { steppe: 'степ', grove: 'гай', stone: 'каміння', river: 'річка', swamp: 'болото', pass: 'прохід', glade: 'галявина' };
const COLORS = ['#ffd23f', '#ff5fa2'];

const CSS = `
.kol-menu { display: flex; flex-direction: column; gap: .3em; width: min(100%, 24em); margin: .5em auto 0; }
.kol-menu .kol-mi { pointer-events: auto; display: flex; align-items: center; justify-content: center; gap: .5em; min-height: 46px; margin: 0;
  padding: .2em 1em; font: inherit; font-size: 1.05em; color: #fff; background: #2c3a22; border: 1px solid #ffffff2a; border-radius: 12px;
  cursor: pointer; touch-action: manipulation; text-align: center; }
.kol-menu .kol-mi.kol-main { background: #4a6a2a; border-color: #9bc25a; font-size: 1.25em; min-height: 54px; }
.kol-menu .kol-mi small { font-size: .62em; opacity: .8; font-weight: 500; }
.kol-menu .kol-mi.kol-mf { outline: 3px solid #ffd23f; outline-offset: 2px; background: #3f5a28; }
.kol-menu .kol-mi.kol-main.kol-mf { background: #5a8034; }
.kol-menu .kol-opt { justify-content: space-between; min-height: 40px; font-size: .95em; background: #1d2418; }
.kol-menu .kol-opt b { flex: 1; text-align: center; }
.kol-menu .kol-arr { pointer-events: auto; min-width: 40px; min-height: 36px; font: inherit; color: #ffd23f; background: none; border: 0; cursor: pointer;
  touch-action: manipulation; padding: 0 .2em; }
.kol-msub { font-size: .75em; opacity: .75; margin: -.1em 0 .1em; text-align: center; min-height: 1.1em; font-weight: 500; }
.kol-mkeys { font-size: .72em; opacity: .75; margin-top: .6em; line-height: 1.45; font-weight: 500; }
.kol-mkeys b { opacity: 1; }
.kol-mlink { pointer-events: auto; width: 100%; font: 500 .7em/1.3 ui-monospace, monospace; color: #cfe; background: #0b0d09; border: 1px solid #ffffff2a;
  border-radius: 6px; padding: .3em .5em; text-align: center; }
.kol-ach { display: grid; grid-template-columns: 1fr 1fr; gap: .3em; margin: .2em 0; text-align: left; }   /* K50 */
.kol-ach div { display: flex; gap: .45em; align-items: center; padding: .25em .45em; border-radius: 8px; background: #1d2418; border: 1px solid #ffffff1a;
  font-size: .78em; line-height: 1.2; font-weight: 500; }
.kol-ach div > span { font-size: 1.5em; }
.kol-ach div small { display: block; opacity: .75; font-size: .85em; }
.kol-ach .kol-ach-off { filter: grayscale(1); opacity: .45; }
.kol-ach .kol-ach-on { border-color: #ffd23f88; background: #3a3414; }
@media (max-width: 520px) { .kol-ach { grid-template-columns: 1fr; } }
@media (max-height: 820px) { .kol-menu .kol-mi { min-height: 42px; } .kol-menu .kol-mi.kol-main { min-height: 48px; } .kol-menu .kol-opt { min-height: 36px; } }
`;

function load() { try { const o = JSON.parse(localStorage.getItem(KEY) || '{}'); return o && typeof o === 'object' ? o : {}; } catch { return {}; } }
function save(o) { try { localStorage.setItem(KEY, JSON.stringify(o)); } catch { } }
/** K46: що писати в пам'ять — поля, взяті з адреси (?p= / ?bot= / ?seed=) і не змінені руками, лишаються як були в пам'яті */
export function memToSave(st, mem, fromUrl, last) {
  const o = { players: st.players, bots: st.bots, seed: st.seed, last };
  for (const k of fromUrl) { if (mem?.[k] === undefined) delete o[k]; else o[k] = mem[k]; }
  return o;
}

/** Ділянки сіду коротко: { icons: '🌾🌳⛰', names: 'степ → гай → прохід' } (те саме generate, що й гра) */
const zoneCache = new Map();
export function seedZones(seed, params) {
  const key = `${seed}|${params?.stripLen}|${params?.hardGrow}`;
  if (zoneCache.has(key)) return zoneCache.get(key);
  let z = [];
  try { z = generate(seed >>> 0, { ...params, mode: 'normal' }).zones ?? []; } catch { }
  const r = { icons: z.map((q) => ICON[q.kind] ?? '').join(''), names: z.map((q) => (ICON[q.kind] ?? '') + ' ' + (BIOME_SHORT[q.kind] ?? BIOME_NAMES[q.kind] ?? q.kind)).join(' → ') };
  zoneCache.set(key, r);
  return r;
}

export function createMenu(box, opts = {}) {
  const style = document.createElement('style'); style.textContent = CSS; document.head.appendChild(style);
  const G = () => opts.game?.() ?? null;
  const q = new URLSearchParams(location.search);
  const mem = load();
  const g0 = G();
  // що вибрано: адреса > пам'ять > типове
  const st = {
    players: q.get('p') ? (g0?.local ?? 1) : Math.max(1, Math.min(2, mem.players | 0 || 1)),
    bots: q.get('bot') ? (g0?.bots ?? 0) : Math.max(0, Math.min(3, mem.bots | 0)),
    seed: q.get('seed') ? (g0?.seed ?? +q.get('seed')) >>> 0 : (mem.seed === 'r' || mem.seed == null ? 'r' : mem.seed >>> 0),
  };
  const fromUrl = new Set([q.get('p') && 'players', q.get('bot') && 'bots', q.get('seed') && 'seed'].filter(Boolean));   // K46: адреса — лише на цей запуск
  const seeds = () => { const l = ['r', ...PICKS]; if (st.seed !== 'r' && !l.includes(st.seed)) l.splice(1, 0, st.seed); return l; };
  const net = () => !!(G()?.net || window.KOL?.net);
  let canResume = false, idx = 0, lastLocal = g0?.local, keysTwo = null;

  box.innerHTML = '';
  const list = document.createElement('div'); list.className = 'kol-menu'; box.appendChild(list);
  const sub = document.createElement('div'); sub.className = 'kol-msub';
  const keys = document.createElement('div'); keys.className = 'kol-mkeys';
  box.appendChild(keys);

  const ITEMS = [
    { id: 'resume', label: () => '▶ Продовжити', show: () => canResume },
    { id: 'play', main: true, label: () => `▶ ${t('menu.play', 'Грати')} <small>${st.seed === 'r' ? t('menu.newStrip', '🎲 нова смуга') : t('menu.strip', `смуга №${st.seed}`, st.seed)}</small>`, show: () => true },
    { id: 'seed', opt: true, label: () => {
      if (st.seed === 'r') return t('menu.seedRandom', '🗺 Смуга: 🎲 випадкова');
      return `${t('menu.seed', `🗺 Смуга №${st.seed}`, st.seed)}${PICK_TAG[st.seed] ? ` · ${PICK_TAG[st.seed]}` : ''}`;
    }, show: () => !net(), step: (d) => { fromUrl.delete('seed'); const l = seeds(); st.seed = l[(l.indexOf(st.seed) + d + l.length) % l.length]; } },
    { id: 'players', opt: true, label: () => `${t('menu.players', '👥 Гравці')}: ${st.players === 1 ? '1' : '2 на одному ПК'}`, show: () => !net(),
      step: () => { fromUrl.delete('players'); st.players = st.players === 1 ? 2 : 1; st.bots = Math.min(st.bots, MAX_PLAYERS - st.players); } },
    { id: 'bots', opt: true, label: () => `${t('menu.bots', '🤖 Боти-помічники')}: ${st.bots}`, show: () => !net(),
      step: (d) => { fromUrl.delete('bots'); const m = Math.min(3, MAX_PLAYERS - st.players) + 1; st.bots = (st.bots + d + m) % m; } },
    { id: 'tutorial', label: () => t('menu.tutorial', '🎓 Навчання'), show: () => !net() },
    { id: 'daily', label: () => t('menu.daily', '📅 Маршрут дня'), show: () => !net() },
    { id: 'endless', label: () => t('menu.endless', '♾ Нескінченна'), show: () => !net() },
    { id: 'net', label: () => `${t('menu.net', '🌐 Грати з друзями')} <small>посилання для друга</small>`, show: () => !net() },
    { id: 'replays', label: () => '📼 Повтори <small>свої й за кодом</small>', show: () => !net() },   // K35
    { id: 'ach', label: () => { const [a, n] = achievements().count(); return `🏆 Досягнення <small>${a}/${n}</small>`; }, show: () => true },   // K50
    { id: 'copy', label: () => '📋 Копіювати посилання для друга', show: () => net() },
    { id: 'offline', label: () => '🏠 Грати без мережі', show: () => net() },
  ];
  const nodes = new Map();
  for (const it of ITEMS) {
    const b = document.createElement(it.opt ? 'div' : 'button');
    b.className = `kol-mi${it.main ? ' kol-main' : ''}${it.opt ? ' kol-opt' : ''}`;
    if (!it.opt) b.type = 'button';
    b.dataset.mi = it.id;
    if (it.opt) b.innerHTML = '<button class="kol-arr" type="button" data-d="-1" aria-label="менше">◀</button><b></b><button class="kol-arr" type="button" data-d="1" aria-label="більше">▶</button>';
    list.appendChild(b); nodes.set(it.id, b);
    if (it.id === 'seed') list.appendChild(sub);
  }
  // K50: список досягнень під пунктом «🏆» — відкрите кольорове, решта сіра (накопичувальні — з поступом)
  const achBox = document.createElement('div'); achBox.className = 'kol-ach'; achBox.style.display = 'none';
  nodes.get('ach').after(achBox);
  const esc = (v) => String(v).replace(/[&<>"']/g, (c) => `&#${c.charCodeAt(0)};`);
  function achToggle(on = achBox.style.display === 'none') {
    achBox.style.display = on ? '' : 'none';
    if (on) achBox.innerHTML = achievements().list().map((a) => `<div class="${a.got ? 'kol-ach-on' : 'kol-ach-off'}" title="${esc(a.desc)}"><span>${a.icon}</span><b>${esc(a.name)}<small>${esc(a.desc)}${a.goal && !a.got ? ` · ${a.n}/${a.goal}` : ''}</small></b></div>`).join('');
  }
  const link = document.createElement('input'); link.className = 'kol-mlink'; link.readOnly = true; link.style.display = 'none';
  list.appendChild(link);

  const visible = () => ITEMS.filter((it) => it.show());
  function friendLink() {
    const room = window.KOL?.net?.room;
    if (!room) return '';
    try {
      const u = new URL(location.href);
      for (const k of ['nick', 'drive', 'lag', 'jit', 'test', 'seed']) u.searchParams.delete(k);
      u.searchParams.set('net', room); u.searchParams.delete('p');
      return u.toString();
    } catch { return ''; }
  }
  function render() {
    const vis = visible();
    if (idx >= vis.length) idx = 0;
    for (const it of ITEMS) {
      const b = nodes.get(it.id), on = vis.includes(it);
      b.style.display = on ? '' : 'none';
      if (!on) continue;
      const html = it.label();
      if (b._h !== html) { b._h = html; (it.opt ? b.querySelector('b') : b).innerHTML = html; }   // DOM — лише зміни
      b.classList.toggle('kol-mf', vis[idx] === it);
    }
    const z = !net() && st.seed !== 'r' ? seedZones(st.seed, G()?.params).names : '';
    sub.textContent = net() ? '' : z ? `${t('menu.zones', 'ділянки')}: ${z}` : t('menu.zonesNone', 'щоразу нова смуга — або оберіть із добірки ◀ ▶');
    sub.style.display = net() ? 'none' : '';
    const L = friendLink();
    link.style.display = net() ? '' : 'none';
    if (net()) { const v = L || `🌐 ${window.KOL?.net?.status ?? 'підключаюсь…'}`; if (link.value !== v) link.value = v; }
    const two = st.players === 2;
    if (keysTwo === two) return;
    keysTwo = two;
    keys.innerHTML = `<span class="kol-kbonly"><b style="color:${COLORS[0]}">П1</b> WASD + Space/E${two ? ` · <b style="color:${COLORS[1]}">П2</b> стрілки + Enter` : ''} · Q — кинути · F — гудок · Esc — пауза<br></span>`
      + `🎮 стік — ходити · <b>A</b> — дія · <b>B</b> — кинути · <b>Y</b> — гудок${two ? ' · другий пад — П2' : ''}<br>`
      + `<span class="kol-kbonly">меню: ↑↓ — вибір · ←→ — змінити · Enter — так · F1 — налаштування</span><span class="kol-padonly">меню: ✚ — вибір · ◀▶ — змінити · <b>A</b> — так</span>`;
  }
  function remember(last) { save(memToSave(st, mem, fromUrl, last)); }
  function pick(id) {
    const it = ITEMS.find((x) => x.id === id);
    if (!it || !it.show()) return;
    if (it.opt) { it.step(1); remember(); render(); return; }
    if (id === 'copy') { copy(); return; }
    if (id === 'ach') { achToggle(); return; }   // K50
    if (['play', 'tutorial', 'daily', 'endless'].includes(id)) remember(id);
    const seed = st.seed === 'r' ? (Math.random() * 1e9) >>> 0 : st.seed;
    opts.onPick?.(id, { players: st.players, bots: st.bots, seed });
  }
  function copy() {
    const L = friendLink();
    if (!L) { opts.say?.('🌐 Стіл ще не готовий — зачекайте секунду'); return; }
    link.select?.();
    const ok = () => opts.say?.('📋 Посилання скопійовано — надішліть другу');
    try { navigator.clipboard.writeText(L).then(ok, () => { try { document.execCommand('copy'); ok(); } catch { } }); }
    catch { try { document.execCommand('copy'); ok(); } catch { } }
  }
  function move(d) { const n = visible().length; idx = (idx + d + n) % n; render(); }
  function side(d) { const it = visible()[idx]; if (it?.opt) { it.step(d); remember(); render(); } else if (it?.id === 'play' && !net()) { ITEMS[2].step(d); remember(); render(); } }
  function focusId(id) { const i = visible().findIndex((x) => x.id === id); if (i >= 0) idx = i; }

  list.addEventListener('click', (e) => {
    const arr = e.target.closest('.kol-arr'), row = e.target.closest('[data-mi]');
    if (!row) return;
    e.preventDefault();
    const it = ITEMS.find((x) => x.id === row.dataset.mi);
    focusId(row.dataset.mi);
    if (arr && it?.opt) { it.step(+arr.dataset.d); remember(); render(); return; }
    row.blur?.();
    pick(row.dataset.mi);
  });

  // початковий фокус: навчання з ?tut=1, інакше «Грати»
  focusId(q.get('tut') && q.get('tut') !== '0' ? 'tutorial' : 'play');
  render();

  return {
    opts: () => ({ ...st }),
    focus: () => visible()[idx]?.id,
    open(resume) { canResume = !!resume; focusId(resume ? 'resume' : 'play'); achToggle(false); render(); },
    sync(s) {
      const g = G();
      if (g && g.local !== lastLocal) { lastLocal = g.local; if (g.local === 2 && st.players === 1) { st.players = 2; st.bots = Math.min(st.bots, MAX_PLAYERS - 2); } }   // другий пад → П2
      if (canResume && s?.status !== 'run') { canResume = false; }
      render();
    },
    key(e) {
      const k = e.code;
      if (k === 'ArrowUp' || k === 'KeyW') move(-1);
      else if (k === 'ArrowDown' || k === 'KeyS') move(1);
      else if (k === 'ArrowLeft' || k === 'KeyA') side(-1);
      else if (k === 'ArrowRight' || k === 'KeyD') side(1);
      else if (k === 'Enter' || k === 'NumpadEnter' || k === 'Space' || k === 'KeyE') { if (!e.repeat) pick(visible()[idx]?.id); }
      else if (k === 'Escape') { if (canResume) pick('resume'); }
      else return false;
      e.preventDefault();
      return true;
    },
    pad(k) {
      if (k === 'up') move(-1); else if (k === 'down') move(1);
      else if (k === 'left') side(-1); else if (k === 'right') side(1);
      else if (k === 'a') pick(visible()[idx]?.id);
      else if (k === 'b' && canResume) pick('resume');
      else if (k === 'start') pick(canResume ? 'resume' : 'play');
    },
    dispose() { style.remove(); },
  };
}
