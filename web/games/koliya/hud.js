// «Колія» — HUD і екрани (DOM поверх канви). Власник: K9.
// Контракт: createHud(root, { players: n, onStart() }) → { update(state, game), dispose() }
//  root — <div id="kol-hud"> (стилі тут, класи з префіксом kol-); update — щокадру з main.js.
//  Стартовий екран тримає game.paused = true до першого натиску клавіші/кнопки пада/кліку, тоді onStart().
//  Поля стану читаються обережно (?? 0): правила пишуться паралельно.
//  K12: стартовий екран з пада закривається A (або Start); opts.onRestart?() — велика кнопка «Ще раз» на кінці (для
//  сенсора Deck); say(text, ms?) — коротка плашка зверху («П2 приєднався»). DOM оновлюється лише коли змінився тик чи
//  статус (20 разів/с, не щокадру).
//  K16: events(ev, s) — гачок з main.js щотику: тости подій («🪿 Гуска вкрала сокиру!», покращення станції, «💫»);
//  тости — не більше 2 одночасно (новий витісняє найстаріший). Біля смуги прогресу — біом під паротягом (s.zones від
//  K7; з проміжною станцією — і зони другої смуги) і позначка 🏪 проміжної; паротяг на проміжній — банер «оберіть».
//  K23: нескінченна (s.endless) — «🚉 N · ділянка», до наступної, відлік зупинки; рекорди kol-best (станції / час сіду),
//  kol-daily (маршрут дня: dailySeed/kyivDate для main.js); підсумок по гравцях із s.ev і звання; пауза Esc/⏸
//  (opts.input — пад у меню, opts.onSettings(), opts.panelOpen()); на старті «📅 Маршрут дня» / «🎓 Навчання», якщо в game
//  є startDaily / startTutorial.
import { TPS, U, G, I, wagonPos } from './rules.js';
import { BIOME_NAMES, generate } from './gen.js';   // K16: біоми
import { UPGRADES, CIST_MAX, BRAKE_SEC } from './extras.js';   // K16: тост покращення; K56: тости й значки K48
import { t as tt, isChumak, upName } from './theme.js';      // K25: слова теми «Чумацький шлях»; K50: «Чумак»
import { achievements } from './achievements.js';   // K50: досягнення 🏆 — тости з s.ev
import { createMenu, BIOME_SHORT } from './menu.js';   // K30: стартове меню, короткі назви біомів
import { A11Y, pcol, pmark } from './panel.js';   // K49: кольори для дальтоніків і значки форми
import { createHelp, introSteps } from './help.js';   // K65: «❓ Як грати» — з меню, паузи й клавішею H; картка збору

// K49: колір гравця — pcol(i) з panel.js (з урахуванням «Кольори для дальтоніків»)
const HOLD_ICON = { [I.WOOD]: '🪵', [I.IRON]: '⛓', [I.RAIL]: '🛤', [I.AXE]: '🪓', [I.PICK]: '⛏', [I.BUCKET]: '🪣', [I.BUCKET_FULL]: '💧' };
const HOLD_NAME = { [I.WOOD]: 'дерево', [I.IRON]: 'залізо', [I.RAIL]: 'рейки', [I.AXE]: 'сокира', [I.PICK]: 'кайло', [I.BUCKET]: 'відро', [I.BUCKET_FULL]: 'повне відро' };
const ALARM_SEC = 3;
const BIOME_ICON = { glade: '🌱', steppe: '🌾', grove: '🌳', stone: '🪨', river: '🌊', swamp: '🐸', pass: '⛰', winter: '❄', yar: '🏞' };
const STEAL_NAME = { [I.AXE]: 'сокиру', [I.PICK]: 'кайло', [I.BUCKET]: 'відро', [I.BUCKET_FULL]: 'відро з водою' };
const TOASTS = 2;
const BEST_KEY = 'kol-best';   // K23: рекорди { 'endless|<гравців>': станцій, 'time|<режим>|<сід>|<гравців>': тиків до станції }

// K23: рекорди в localStorage (приватне вікно / заблоковане сховище — просто без рекорду)
function loadBest() { try { const b = JSON.parse(localStorage.getItem(BEST_KEY) || '{}'); return b && typeof b === 'object' ? b : {}; } catch { return {}; } }
function saveBest(b) { try { localStorage.setItem(BEST_KEY, JSON.stringify(b)); } catch { } }
const plural = (n, a, b, c) => { const m = n % 10, h = n % 100; return m === 1 && h !== 11 ? a : m >= 2 && m <= 4 && (h < 12 || h > 14) ? b : c; };
const whoLabel = (n) => n <= 1 ? 'соло' : `${n} ${plural(n, 'гравець', 'гравці', 'гравців')}`;
// K23: «📅 Маршрут дня» — дата за Києвом (YYYY-MM-DD) і сід від неї, однаковий у всіх; рекорд дня — kol-daily
const DAILY_KEY = 'kol-daily';
export function kyivDate(d = new Date()) {
  try { return new Intl.DateTimeFormat('en-CA', { timeZone: 'Europe/Kyiv', year: 'numeric', month: '2-digit', day: '2-digit' }).format(d); }
  catch { return d.toISOString().slice(0, 10); }
}
export function dailySeed(date = kyivDate()) {
  let h = (Number(String(date).replace(/\D/g, '')) ^ 0x4B4F4C49) >>> 0;   // YYYYMMDD ^ «KOLI»
  h = Math.imul(h ^ (h >>> 16), 0x85EBCA6B); h = Math.imul(h ^ (h >>> 13), 0xC2B2AE35);
  return (h ^ (h >>> 16)) >>> 0;
}
function loadDaily() { try { const b = JSON.parse(localStorage.getItem(DAILY_KEY) || '{}'); return b && typeof b === 'object' ? b : {}; } catch { return {}; } }
/** Рекорд дня: { kind: 'dst'|'dtime', val, prev, isNew, n } — час до станції (менше краще) чи станції нескінченної */
function judgeDaily(s, date, save = true) {
  const n = s.players?.length ?? 1, b = loadDaily(), day = b[date] ?? {};
  const kind = s.endless ? 'dst' : 'dtime', key = `${s.endless ? 'e' : 't'}${n}`, prev = +day[key] || 0;
  let val = 0, isNew = false;
  if (s.endless) { val = s.endless.stations | 0; isNew = val > prev; }
  else if (s.status === 'won') { val = s.tick | 0; isNew = !prev || val < prev; }
  if (isNew && save) {
    b[date] = { ...day, [key]: val };
    for (const d of Object.keys(b).sort().slice(0, -7)) delete b[d];   // тримаємо тиждень
    try { localStorage.setItem(DAILY_KEY, JSON.stringify(b)); } catch { }
  }
  return { kind, val, prev, isNew, n };
}

/** Підсумок рекорду партії, що скінчилась: { kind: 'st'|'time', val, prev, isNew } або null (рахувати нема що) */
function judgeRecord(s) {
  const n = s.players?.length ?? 1;
  if (s.endless) {
    const key = `endless|${n}`, val = s.endless.stations | 0, b = loadBest(), prev = +b[key] || 0;
    const isNew = val > prev;
    if (isNew) { b[key] = val; saveBest(b); }
    return { kind: 'st', val, prev, isNew, n };
  }
  const key = `time|${s.params?.stationMode ?? 'end'}|${s.seed >>> 0}|${n}`, b = loadBest(), prev = +b[key] || 0;
  if (s.status !== 'won') return prev ? { kind: 'time', val: 0, prev, isNew: false, n } : null;
  const val = s.tick | 0, isNew = !prev || val < prev;
  if (isNew) { b[key] = val; saveBest(b); }
  return { kind: 'time', val, prev, isNew, n };
}

const CSS = `
#kol-hud.kol-root { position: fixed; inset: 0; left: 0; top: 0; padding: 0; background: none; border-radius: 0;
  pointer-events: none; font: 600 clamp(14px, 1.6vw, 22px)/1.25 system-ui, sans-serif; color: #f4efe2; z-index: 5;
  text-shadow: 0 1px 2px #000a; }
.kol-root * { box-sizing: border-box; }
.kol-box { background: #0f120dcc; border: 1px solid #ffffff22; border-radius: 10px; padding: .35em .7em; }
.kol-top { position: absolute; left: 12px; right: 72px; top: 10px; display: flex; gap: .6em; align-items: center; }
.kol-prog { flex: 1; position: relative; height: 1.5em; padding: 0; overflow: hidden; }
.kol-prog-track { position: absolute; top: 0; bottom: 0; left: 0; background: #8a7a5a55; }
.kol-prog-head { position: absolute; top: 50%; transform: translate(-50%, -50%); font-size: 1.1em; transition: left .2s linear; }
.kol-prog-end { position: absolute; top: 0; bottom: 0; width: 3px; background: #ffd23f; transform: translateX(-1px); }
.kol-prog-st { position: absolute; right: .3em; top: 50%; transform: translateY(-50%); }
.kol-prog-ahead { position: absolute; left: 50%; top: 50%; transform: translate(-50%, -50%); white-space: nowrap; font-size: .8em; opacity: .9; }
.kol-stat { white-space: nowrap; }
.kol-res { position: absolute; left: 12px; top: calc(10px + 2.6em); display: flex; flex-direction: column; gap: .4em; }
.kol-row { display: flex; gap: .7em; align-items: center; white-space: nowrap; }
.kol-full { color: #ffb347; }
.kol-up { margin-left: .35em; white-space: nowrap; }
.kol-up:empty { display: none; }
.kol-up.kol-up-on { color: #9be7ff; }
.kol-craft { display: inline-block; width: 3em; height: .5em; background: #ffffff22; border-radius: 4px; overflow: hidden; vertical-align: middle; }
.kol-craft > i { display: block; height: 100%; width: 0; background: #b6ff5f; }
.kol-heat { display: inline-block; width: 7em; height: .7em; background: #ffffff22; border-radius: 5px; overflow: hidden; vertical-align: middle; }
.kol-heat > i { display: block; height: 100%; width: 0; background: #ffd23f; }
.kol-hot .kol-heat > i { background: #ff3b2f; }
.kol-hot { animation: kol-blink .6s steps(2) infinite; }
@keyframes kol-blink { 50% { border-color: #ff3b2f; background: #5a0f0bcc; } }
.kol-banner { position: absolute; left: 50%; top: 22%; transform: translateX(-50%); text-align: center; font-size: 2.1em;
  padding: .3em .9em; border-radius: 14px; display: none; white-space: nowrap; }
.kol-fire { background: #a3160ee6; border: 2px solid #ffb347; animation: kol-pulse .5s ease-in-out infinite alternate; }
.kol-fire { font-size: 1.6em; }   /* K61: на 2,1em банер (≈ 540 px) лягав на лівий стовпчик — саме на шкалу котла */
.kol-alarm { top: 36%; font-size: 1.6em; background: #8a5a00e6; border: 2px solid #ffd23f; animation: kol-pulse .4s ease-in-out infinite alternate; }
@keyframes kol-pulse { to { transform: translateX(-50%) scale(1.06); } }
.kol-count { position: absolute; left: 50%; top: 30%; transform: translate(-50%, -50%); text-align: center; display: none; }
.kol-count b { display: block; font-size: 5em; line-height: 1; color: #ffd23f; text-shadow: 0 3px 10px #000; }
/* картка «Як грати» на час збору (introSteps з help.js) */
.kol-intro { position: absolute; left: 50%; bottom: 3.4em; transform: translateX(-50%); width: min(94%, 31em); display: none; pointer-events: auto;
  font-size: max(12px, .82em); font-weight: 500; line-height: 1.35; text-align: left; background: #0f120de8; padding: .5em .8em .6em; }
.kol-intro h3 { margin: 0 0 .25em; font-size: 1.15em; color: #ffd23f; display: flex; align-items: center; }
.kol-intro h3 span { flex: 1; }
.kol-intro ol { margin: 0; padding-left: 1.3em; display: grid; gap: .15em; }
.kol-intro .kol-in-keys { margin-top: .35em; opacity: .8; }
.kol-intro .kol-in-row { display: flex; gap: .4em; flex-wrap: wrap; margin-top: .45em; }
.kol-intro button { font: inherit; color: #fff; background: #2c3a22; border: 1px solid #ffffff2a; border-radius: 10px; padding: .2em .7em; min-height: 36px;
  cursor: pointer; touch-action: manipulation; }
.kol-intro .kol-in-x { background: none; border: 0; min-width: 36px; padding: 0; opacity: .8; }
.kol-touchonly { display: none; }
.kol-touch .kol-touchonly { display: contents; }
.kol-touch .kol-intro .kol-kbonly, .kol-touch .kol-intro .kol-padonly { display: none; }
.kol-pls { position: absolute; left: 12px; bottom: 12px; display: flex; gap: .5em; flex-wrap: wrap; }
.kol-pl { border-left: 5px solid; min-width: 5.5em; }
.kol-screen { position: absolute; inset: 0; display: none; align-items: center; justify-content: center; background: #0b0d09c4; pointer-events: auto; }
.kol-card { max-width: min(92vw, 40em); background: #171b13f2; border: 1px solid #ffffff2a; border-radius: 16px; padding: 1.2em 1.6em; text-align: center; }
.kol-card h1 { margin: 0 0 .2em; font-size: 2.6em; color: #ffd23f; }
.kol-card p { margin: .4em 0; font-weight: 500; }
.kol-keys { display: grid; grid-template-columns: auto 1fr; gap: .25em .9em; text-align: left; margin: .8em auto; width: fit-content; font-weight: 500; }
.kol-keys b { font-weight: 700; }
.kol-hint { opacity: .7; font-size: .85em; }
.kol-go { margin-top: .8em; font-size: 1.2em; color: #b6ff5f; animation: kol-blink2 1.2s ease-in-out infinite; }
@keyframes kol-blink2 { 50% { opacity: .35; } }
.kol-sum { display: grid; grid-template-columns: auto auto; gap: .2em 1em; width: fit-content; margin: .6em auto; text-align: left; font-weight: 500; }
.kol-again { pointer-events: auto; margin-top: .6em; min-height: 48px; padding: .3em 1.4em; font: inherit; font-size: 1.1em; color: #fff;
  background: #4a6a2a; border: 1px solid #9bc25a; border-radius: 12px; cursor: pointer; touch-action: manipulation; }
.kol-toasts { position: absolute; left: 50%; top: calc(10px + 2.6em); transform: translateX(-50%); display: flex; flex-direction: column;
  align-items: center; gap: .3em; }
.kol-toast { white-space: nowrap; background: #1f3a12ee; border-color: #9bc25a; animation: kol-in .2s ease-out; }
.kol-toast.kol-t-warn { background: #4a2c08ee; border-color: #ffb347; }
@keyframes kol-in { from { opacity: 0; transform: translateY(-.4em); } }
.kol-biome { white-space: nowrap; font-size: .85em; }
.kol-prog-mid { position: absolute; top: 50%; transform: translate(-50%, -50%); font-size: .9em; }
.kol-prog-midl { position: absolute; top: 0; bottom: 0; width: 2px; background: #5fd3ff; transform: translateX(-1px); opacity: .8; }
.kol-mid { top: 36%; font-size: 1.4em; background: #0f3a4ae6; border: 2px solid #5fd3ff; white-space: normal; max-width: 90vw; }
.kol-padonly { display: none; }
.kol-haspad .kol-padonly { display: contents; }
.kol-haspad .kol-kbonly { display: none; }
.kol-endl { white-space: nowrap; background: #123a4acc; border-color: #5fd3ff88; }
.kol-big { font-size: 1.6em; margin: .2em 0; color: #fff; }
.kol-rec { margin: .3em 0; font-weight: 600; }
.kol-rec.kol-new { color: #ffd23f; font-size: 1.3em; animation: kol-pulse2 .7s ease-in-out infinite alternate; }
@keyframes kol-pulse2 { to { transform: scale(1.08); } }
.kol-card .kol-sum { grid-template-columns: auto auto auto auto; }
.kol-ptab { margin: .5em auto .2em; border-collapse: collapse; font-size: .85em; font-weight: 500; }
.kol-ptab th, .kol-ptab td { padding: .15em .45em; text-align: center; white-space: nowrap; }
.kol-ptab tr + tr { border-top: 1px solid #ffffff1a; }
.kol-ptab td:first-child { text-align: left; }
.kol-ptab td.kol-ttl { text-align: left; font-weight: 700; color: #ffe9a8; }
.kol-ptab i { opacity: .35; font-style: normal; }
#kol-hud .kol-top { right: 124px; }
/* K37: верхня смуга компактніша й напівпрозора — бульбашки-реакції й ніки гравців біля верхнього краю видно крізь неї */
#kol-hud .kol-top { top: 6px; font-size: .85em; gap: .45em; }
#kol-hud .kol-top .kol-box { background: #0f120d70; border-color: #ffffff16; padding: .15em .5em; }
#kol-hud .kol-top .kol-prog { height: 1.35em; padding: 0; }
#kol-hud .kol-top .kol-prog-track { background: #8a7a5a40; }
#kol-hud .kol-res, #kol-hud .kol-toasts { top: calc(6px + 2.1em); }
.kol-pausebtn { position: fixed; right: 62px; top: 8px; width: 46px; height: 46px; border-radius: 10px; border: 1px solid #fff3;
  background: rgba(20,24,18,.7); color: #f4efe2; font-size: 20px; cursor: pointer; pointer-events: auto; touch-action: manipulation; }
.kol-modes { display: flex; gap: .6em; justify-content: center; flex-wrap: wrap; }
.kol-modes .kol-again { font-size: 1em; }
.kol-pmenu { display: flex; flex-direction: column; align-items: stretch; gap: .1em; width: fit-content; margin: .4em auto; }
.kol-pmenu .kol-again.kol-pf, .kol-pmenu .kol-again:focus-visible { outline: 3px solid #ffd23f; outline-offset: 2px; }
@media (max-width: 1400px) { .kol-top .kol-wd { display: none; } }
@media (max-width: 640px) { .kol-card .kol-sum { grid-template-columns: auto auto; } .kol-ptab { font-size: .72em; } .kol-ptab th, .kol-ptab td { padding: .1em .25em; } }
@media (max-height: 820px) { .kol-card { padding: .8em 1.2em; } .kol-card h1 { font-size: 2.1em; } .kol-keys { margin: .5em auto; } }
/* K41: меню вище за вікно (ноутбук ~620 px, Deck у браузері) — картка не обрізається згори, екран гортається */
.kol-screen { overflow-y: auto; } .kol-screen > .kol-card { margin: auto; }
/* K49: «Великий текст HUD» ×1.25; «Менше блимань» — тривоги статичні, червоні */
html.kol-a-big #kol-hud.kol-root { font-size: clamp(17.5px, 2vw, 27.5px); }
html.kol-a-calm .kol-root *, html.kol-a-calm .kol-root { animation: none !important; transition: none !important; }
html.kol-a-calm .kol-hot { border-color: #ff3b2f; background: #5a0f0bcc; }
html.kol-a-calm .kol-alarm { background: #a3160ee6; border-color: #ff3b2f; }
/* K69: телефон — тости переносяться (довгі покращення вилазили за край), підказки й таблиця званнь ≥ 12 px (таблиця гортається
   вбік, а не обрізається), картка паузи/фіналу тонша; ландшафт (висота ≤ 500) — менший заголовок і кнопки, екран гортається */
.kol-toast { white-space: normal; max-width: min(92vw, 34em); text-align: center; }
.kol-hint { font-size: max(12px, .85em); }
.kol-ptab { font-size: max(12px, .85em); display: block; max-width: 100%; width: fit-content; overflow-x: auto; }
@media (max-width: 640px) { .kol-ptab { font-size: 12px; } .kol-card { padding: .8em .7em; max-width: calc(100vw - 16px); } }
@media (max-height: 500px) { .kol-screen { align-items: flex-start; padding: 6px 0; } .kol-card { padding: .5em 1em; } .kol-card h1 { font-size: 1.6em; }
  .kol-card .kol-again { min-height: 42px; margin-top: .3em; } .kol-card .kol-sum { grid-template-columns: repeat(4, auto); margin: .3em auto; } }
`;

/** Ділянки-біоми всієї смуги: s.zones (перша смуга) + з проміжною станцією — зони другої (той самий сід, що в extras) */
function allZones(s) {
  const z = [...(s.zones ?? [])];
  if (s.mid?.len1) {
    try {
      const b = generate((s.seed ^ 0x5EED5EED) >>> 0, s.params);
      for (const q of b.zones ?? []) z.push({ ...q, x0: q.x0 + s.mid.len1, x1: q.x1 + s.mid.len1 });
    } catch { }
  }
  return z;
}
const zoneAt = (zones, x) => zones.find((z) => x >= z.x0 && x < z.x1) ?? null;

function el(tag, cls, parent, html) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (html != null) e.innerHTML = html;
  if (parent) parent.appendChild(e);
  return e;
}
const esc = (v) => String(v).replace(/[&<>"']/g, (c) => `&#${c.charCodeAt(0)};`);
/** K37: підпис гравця, як над головою (render.js): у мережі — нік (game.names, до 10 символів, «нік ²»; відпав — «📶?»),
 *  інакше П1…П4; локальний бот — 🤖 спереду. Екранований для innerHTML (raw — для textContent). */
export function playerLabel(game, i, n = 0, raw = false) {
  const nk = game?.names?.[i];
  if (nk) return pmark(i) + (raw ? nk : esc(nk)) +(game?.lost?.[i] ? ' 📶?' : '');   // K49: значок форми спереду
  const net = !!(game?.net || globalThis.KOL?.net);
  const bot = !net && (game?.bot?.some?.((b) => b.p === i) || i >= (game?.local ?? n));
  return `${pmark(i)}${bot ? '🤖' : ''}П${i + 1}`;
}
const fmtTime = (sec) => { sec = Math.max(0, Math.floor(sec)); return `${Math.floor(sec / 60)}:${String(sec % 60).padStart(2, '0')}`; };

export function createHud(root, opts = {}) {
  const style = el('style', null, document.head); style.textContent = CSS;
  root.classList.add('kol-root');
  root.textContent = '';
  const cache = new Map();   // пишемо в DOM лише зміни
  const set = (node, key, v) => { if (cache.get(key) !== v) { cache.set(key, v); node[key.split('|')[1]] = v; } };
  const txt = (node, id, v) => set(node, id + '|textContent', v);

  // — верх: прогрес, час, швидкість
  const top = el('div', 'kol-top', root);
  const prog = el('div', 'kol-box kol-prog', top);
  const progTrack = el('div', 'kol-prog-track', prog);
  const progEnd = el('div', 'kol-prog-end', prog);
  const progAhead = el('div', 'kol-prog-ahead', prog);
  const progSt = el('div', 'kol-prog-st', prog, '🏁');
  const progMidL = el('div', 'kol-prog-midl', prog); const progMid = el('div', 'kol-prog-mid', prog, '🏪');
  progMidL.style.display = progMid.style.display = 'none';
  const progHead = el('div', 'kol-prog-head', prog, '🚂');
  const endlEl = el('div', 'kol-box kol-endl', top); endlEl.style.display = 'none';   // K23: 🚉 станцій · ділянка
  const biomeEl = el('div', 'kol-box kol-biome', top);
  const timeEl = el('div', 'kol-box kol-stat', top);
  const speedEl = el('div', 'kol-box kol-stat', top);
  const speedT = el('span', null, speedEl), brakeEl = el('span', 'kol-up', speedEl);   // K56: 🐢 гальма біля швидкості

  // — ресурси й котел
  const res = el('div', 'kol-res', root);
  const store = el('div', 'kol-box kol-row', res);
  const sWood = el('span', null, store), sIron = el('span', null, store);
  const magEl = el('span', 'kol-up', store);   // K56: 🧲 магніт тягне в склад
  const craft = el('div', 'kol-box kol-row', res);
  const cRails = el('span', null, craft);
  const cBar = el('span', 'kol-craft', craft); const cFill = el('i', null, cBar);
  const boiler = el('div', 'kol-box kol-row', res);
  const hIcon = el('span', null, boiler, '♨');   // K25
  const hBar = el('span', 'kol-heat', boiler); const hFill = el('i', null, hBar);
  const hPct = el('span', null, boiler);
  const cistEl = el('span', 'kol-up', boiler);   // K56: 🚿 заряди цистерни біля котла

  // — банери
  const fire = el('div', 'kol-banner kol-fire', root);
  const alarm = el('div', 'kol-banner kol-alarm', root, '⚠ Рейки закінчуються!');
  const count = el('div', 'kol-count', root);
  const countN = el('b', null, count); const countT = el('div', 'kol-box', count, 'паротяг рушає');
  const midEl = el('div', 'kol-banner kol-mid', root);
  // Картка «Як грати» (introSteps): поки стіл чекає «▶ Почали» (у мережі світ тоді стоїть), а новачкові — і на першому
  // відліку без мережі. ✕ — сховати до наступної партії; «🎓» — навчальна смуга (у мережі — для всіх за столом).
  const intro = el('div', 'kol-box kol-intro', root);
  let introKey = '', introOff = null, freshTok = null, freshV = false;
  const toasts = el('div', 'kol-toasts', root);
  const timers = new Set();
  // коротка плашка; не більше TOASTS одночасно — новий витісняє найстаріший; той самий текст не дублюється
  const say = (text, ms = 3500, warn = false) => {
    for (const t of [...toasts.children]) if (t.textContent === text) t.remove();
    while (toasts.children.length >= TOASTS) toasts.firstChild.remove();
    const t = el('div', `kol-box kol-toast${warn ? ' kol-t-warn' : ''}`, toasts); t.textContent = text;
    const id = setTimeout(() => { t.remove(); timers.delete(id); }, ms); timers.add(id);
  };

  // — таблички гравців
  const pls = el('div', 'kol-pls', root);
  const plEls = [];

  // — старт
  const start = el('div', 'kol-screen', root);
  // K30: стартовий екран — меню (menu.js): режими, гравці, боти, смуга; котел — за params (пожежа чи зупинка)
  const startCard = el('div', 'kol-card', start, `
    <h1>${tt('start.title', '🚂 Колія')}</h1>
    <p>${tt('start.p', 'Паротяг сам повзе до станції 🏁 — кладіть рейки перед ним: ліс 🪓 і скелі ⛏ → склад → майстерня робить рейки 🛤.')}
      <span class="kol-boil"></span></p>
    <div class="kol-mbox"></div>
    <div class="kol-hint kol-dayrec"></div>`);
  const dayRec = start.querySelector('.kol-dayrec'), boilEl = start.querySelector('.kol-boil');

  // — пауза (K23): Esc або ⏸; у мережі — лише меню, світ не стоїть
  const pauseBtn = el('button', 'kol-pausebtn', root, '⏸'); pauseBtn.type = 'button'; pauseBtn.title = 'Пауза (Esc)';
  pauseBtn.style.display = 'none';
  const pause = el('div', 'kol-screen', root);
  const pauseCard = el('div', 'kol-card', pause, `<h1>⏸ Пауза</h1><p class="kol-hint kol-pnet"></p>
    <div class="kol-pmenu"><button class="kol-again" type="button" data-act="resume">▶ Продовжити</button>
    <button class="kol-again" type="button" data-act="restart">↻ Почати заново</button>
    <button class="kol-again" type="button" data-act="settings">⚙ Налаштування</button>
    <button class="kol-again" type="button" data-act="help">❓ Як грати</button>
    <button class="kol-again" type="button" data-act="menu">☰ В меню</button></div>
    <p class="kol-hint">Esc — продовжити</p>`);
  const pauseNet = pauseCard.querySelector('.kol-pnet'), pauseBtns = [...pauseCard.querySelectorAll('button')];

  // — кінець
  const end = el('div', 'kol-screen', root);
  end.style.pointerEvents = 'none';   // не заступати ⚙ панелі
  const endCard = el('div', 'kol-card', end);

  let started = false, lastState = null, game0 = null, stationX = -1, zones = [], zonesShift;
  const menu = createMenu(startCard.querySelector('.kol-mbox'), { game: () => game0 ?? window.KOL?.game, say: (t) => say(t), onPick: (act, o) => menuPick(act, o) });
  let recState = null, rec = null;   // K23: рекорд рахуємо раз на партію
  const begin = () => {
    if (started) return;
    started = true; start.style.display = 'none';
    if (menuOwned) { menuOwned = false; opts.input?.setMenu?.(null); }   // K30: setMenu(null) ще й ковтає затиснуті кнопки
    if (game0) game0.paused = false;
    opts.onStart?.();
  };
  // K65: довідка «❓ Як грати» — поверх меню/паузи/гри; локально гра стоїть, у мережі — ні
  let helpState = null, helpWasPaused = false;
  const help = createHelp(root, { input: opts.input, isNet: () => isNet(), onClose: (f) => {
    if (game0 && !isNet()) game0.paused = helpWasPaused || !started || paused;
    if (f === 'menu') menuOwned = false;   // меню забере пад наступним кадром (pauseTick)
    else if (f === 'pause' && paused) { opts.input?.setMenu?.(pausePad); pauseFocus(pauseIdx); }
  } });
  function openHelp(from) {
    if (help.isOpen()) return;
    helpState = lastState; helpWasPaused = !!game0?.paused;
    if (game0 && !isNet()) game0.paused = true;
    document.activeElement?.blur?.();
    help.open(from);
  }
  // Картка «Як грати»: щокадру (стан у мережі — той самий об'єкт і в новій партії, тож партію впізнаємо за епохою)
  function introTick(s) {
    const g = game0, nt = isNet() ? window.KOL?.net : null, tok = nt ? 'e' + (nt.epoch ?? 0) : s;
    const left = (s.params?.startSec ?? 0) - (s.tick ?? 0) / TPS;
    if (freshTok !== tok) {   // новачок: навчання не пройдене й рейок за всі партії ще жменька
      freshTok = tok;
      const l = achievements().list();
      freshV = !l.find((a) => a.id === 'tutorial')?.got && (l.find((a) => a.id === 'railman')?.n | 0) < 20;
    }
    const wait = nt ? !!nt.wait : !((s.train?.speed ?? 0) > 0) && left > 0 && freshV;
    const on = started && s.status === 'run' && wait && introOff !== tok && !help.isOpen() && !g?.replay?.active;
    set(intro.style, 'intro|display', on ? 'block' : 'none');
    if (!on) return;
    const tut = s.params?.mode === 'tutorial', k = `${tut}|${isChumak()}|${!!nt}`;
    if (k === introKey) return;
    introKey = k;
    const keys = '<span class="kol-kbonly">WASD — ходити · Space / E — узяти, рубати, покласти</span>'
      + '<span class="kol-padonly">стік — ходити · A — узяти, рубати, покласти</span>'
      + '<span class="kol-touchonly">палець ліворуч — ходити · ✋ — узяти, рубати, покласти</span>';
    const tutTxt = tt('menu.tutorial', '🎓 Навчання');
    intro.innerHTML = `<h3><span>Як грати</span><button class="kol-in-x" type="button" data-in="x" aria-label="Сховати">✕</button></h3>`
      + `<ol>${introSteps().map((x) => `<li>${x}</li>`).join('')}</ol><div class="kol-in-keys">${keys}</div>`
      + (tut ? `<div class="kol-in-keys">${tutTxt}: ${isChumak() ? 'воли йдуть поволі' : 'паротяг повзе поволі'}, а підказки над головою ведуть крок за кроком</div>` : '')
      + `<div class="kol-in-row">${tut ? '' : `<button type="button" data-in="tut">${tutTxt}${nt ? ' гуртом' : ''}</button>`}`
      + '<button type="button" data-in="help">❓ Докладніше <small>H</small></button></div>';
    intro._tok = tok;
  }
  intro.addEventListener('click', (e) => {
    const b = e.target.closest('[data-in]');
    if (!b) return;
    e.preventDefault(); b.blur();
    if (b.dataset.in === 'x') { introOff = intro._tok; set(intro.style, 'intro|display', 'none'); }
    else if (b.dataset.in === 'help') openHelp('game');
    else if (b.dataset.in === 'tut') game0?.startTutorial?.();   // у мережі — перезапуск столу в навчання для всіх
  });
  const onKey = (e) => {
    if (help.isOpen()) { help.key(e); return; }   // K65
    if (e.code === 'KeyH' && !e.repeat && !e.ctrlKey && !e.altKey && !e.metaKey && !opts.panelOpen?.() && !game0?.replay?.active) {
      const f = !started ? 'menu' : paused ? 'pause' : lastState?.status === 'run' ? 'game' : null;
      if (f) { e.preventDefault(); openHelp(f); return; }
    }
    if (!started) { if (!opts.panelOpen?.() && !e.ctrlKey && !e.altKey && !e.metaKey) menu.key(e); }   // K30: меню
    else if (lastState && (lastState.status === 'won' || lastState.status === 'lost')) endKey(e);   // K30: фінал
    else pauseKey(e);   // K23
  };
  addEventListener('keydown', onKey);
  // K30: вибір у меню. Мережа — стіл уже є: «Грати» лише закриває меню; «🌐» — перезавантаження з ?net=new
  function menuPick(act, o) {
    const g = game0;
    if (act === 'net') {
      try { const u = new URL(location.href); u.searchParams.set('net', 'new'); u.searchParams.set('p', o.players); if (o.bots) u.searchParams.set('bot', o.bots); else u.searchParams.delete('bot'); u.searchParams.delete('tut'); location.href = u; } catch { }
      return;
    }
    if (act === 'replays') { g?.replay?.paste?.(); return; }   // K35
    if (act === 'help') { openHelp('menu'); return; }   // K65: «📼 Повтори» — свої й за кодом
    if (act === 'offline') { try { const u = new URL(location.href); u.searchParams.delete('net'); location.href = u; } catch { } return; }
    if (act === 'tutorial' && isNet()) { g?.startTutorial?.(); begin(); return; }   // навчання гуртом: стіл — у навчальну смугу для всіх
    if (act !== 'resume' && !isNet()) g?.startMenu?.(act, o);
    else if (act === 'resume' && g) g.paused = false;
    begin();
  }
  let menuOwned = false;   // пад веде меню (input.setMenu) — повертаємо собі після панелі
  function toMenu() {
    if (!started) return;
    const resume = lastState?.status === 'run';
    closePause(false);
    started = false; menuOwned = false;
    if (game0) game0.paused = true;
    menu.open(resume);
  }

  // K23: пауза. Локально — game.paused (тики стоять); мережа (KOL.net) — лише меню. Пад у меню: ✚ — вибір, A — так, B — назад
  let paused = false, pauseState = null, waitPanel = false, pauseIdx = 0;
  const isNet = () => !!(game0?.net || window.KOL?.net);
  const canPause = () => started && lastState?.status === 'run' && !opts.panelOpen?.();
  const pauseFocus = (i) => { pauseIdx = (i + pauseBtns.length) % pauseBtns.length; pauseBtns.forEach((x, j) => x.classList.toggle('kol-pf', j === pauseIdx)); pauseBtns[pauseIdx].focus({ preventScroll: true }); };
  function openPause() {
    if (paused || !canPause()) return;
    paused = true; pauseState = lastState;
    if (!isNet() && game0) game0.paused = true;
    pauseNet.textContent = isNet() ? tt('pauseNet', '🌐 Гра в мережі — світ не стоїть, паротяг їде далі!') : '';
    pauseBtns[2].style.display = opts.onSettings ? '' : 'none';
    pause.style.display = 'flex'; pauseFocus(0);
    opts.input?.setMenu?.(pausePad);
  }
  function pausePad(k) {   // K65: окремо — довідка з паузи повертає пад сюди
    if (k === 'up' || k === 'left') pauseFocus(pauseIdx - 1);
    else if (k === 'down' || k === 'right') pauseFocus(pauseIdx + 1);
    else if (k === 'a') pauseAct(pauseBtns[pauseIdx].dataset.act);
    else if (k === 'b') pauseAct('resume');
    else if (k === 'start') pauseAct('restart');
    else if (k === 'menu') pauseAct('settings');
  }
  function closePause(resume = true) {
    if (!paused) return;
    paused = false; pause.style.display = 'none';
    document.activeElement?.blur?.();
    opts.input?.setMenu?.(null);
    if (resume && game0 && started) game0.paused = false;
  }
  function pauseAct(act) {
    if (act === 'resume') closePause();
    else if (act === 'restart') { closePause(); opts.onRestart?.(); }
    else if (act === 'settings' && opts.onSettings) { closePause(false); waitPanel = !isNet(); opts.onSettings(); }
    else if (act === 'menu') toMenu();   // K30
    else if (act === 'help') openHelp('pause');   // K65
  }
  function pauseKey(e) {
    if (e.key === 'Escape') { if (paused) closePause(); else if (!opts.panelOpen?.()) openPause(); return; }
    if (!paused) return;
    const k = e.code;
    if (k === 'ArrowUp' || k === 'KeyW' || k === 'ArrowLeft' || k === 'KeyA') pauseFocus(pauseIdx - 1);
    else if (k === 'ArrowDown' || k === 'KeyS' || k === 'ArrowRight' || k === 'KeyD') pauseFocus(pauseIdx + 1);
    else if (k === 'Enter' || k === 'Space' || k === 'KeyE' || k === 'NumpadEnter') { e.preventDefault(); if (!e.repeat) pauseAct(pauseBtns[pauseIdx].dataset.act); }
  }
  pauseBtn.addEventListener('click', () => { pauseBtn.blur(); if (paused) closePause(); else openPause(); });
  pauseCard.addEventListener('click', (e) => { const b = e.target.closest('[data-act]'); if (b) { e.preventDefault(); pauseAct(b.dataset.act); } });
  // кадром: панель закрилась після «Налаштувань» — назад у паузу; нова партія (R/Start) — пауза геть
  function pauseTick(s) {
    if (help.isOpen() && (help.from === 'game' ? s !== helpState || s.status !== 'run' : help.from === 'pause' ? !paused : started)) help.close();   // K65: R/нова партія — довідка геть
    if (waitPanel && !opts.panelOpen?.()) { waitPanel = false; if (s.status === 'run' && s === pauseState) { openPause(); } else if (game0) game0.paused = false; }
    if (paused && s !== pauseState) closePause();
    if (paused && s.status !== 'run') closePause(false);
    if (waitPanel && s !== pauseState) { waitPanel = false; if (game0) game0.paused = false; }
    const show = started && s.status === 'run' ? 'block' : 'none';
    set(pauseBtn.style, 'pbtn|display', show);
    endNav((s.status === 'won' || s.status === 'lost') && started, s);   // K30: пад на фіналі
    if (!started) {   // K30: меню — пад (поки панель закрита), котел за params того, що обрано
      const pOpen = !!opts.panelOpen?.();
      if (pOpen) menuOwned = false;
      else if (!menuOwned && !help.isOpen()) { menuOwned = true; opts.input?.setMenu?.((k) => menu.pad(k)); }
      menu.sync(s);
      const f = menu.focus(), oh = f === 'resume' ? s.params?.overheat : f === 'tutorial' ? 'stop' : f === 'daily' ? 'fire' : game0?.params?.overheat ?? s.params?.overheat;
      txt(boilEl, 'boil', tt('boil', `Котел гріється: відро води 💧 на паротяг, інакше ${oh === 'stop' ? 'він стане' : 'пожежа 🔥'}.`, oh));
    }
    if (!started && typeof game0?.startDaily === 'function' && !cache.has('dayrec|textContent')) {
      const d = kyivDate(), r = judgeDaily({ players: s.players, status: '', endless: null }, d, false);
      txt(dayRec, 'dayrec', `📅 ${d.slice(8, 10)}.${d.slice(5, 7)} — однаковий маршрут у всіх${r.prev ? ` · рекорд дня (${whoLabel(r.n)}): ${fmtTime(r.prev / TPS)}` : ''}`);
    }
  }

  // A або Start; повертає ще й «чи є пад» — для підказок
  let hasPad = false;
  function padPressed() {
    let hit = false, any = false;
    try {
      for (const p of navigator.getGamepads?.() || []) {
        if (!p || p.connected === false) continue;
        any = true;
        if (p.buttons[0]?.pressed || p.buttons[9]?.pressed) hit = true;
      }
    } catch { }
    if (any !== hasPad) { hasPad = any; root.classList.toggle('kol-haspad', any); }
    return hit;
  }

  // K23: рядок рекорду на фіналі
  function recHtml(r) {
    if (!r) return '';
    const who = whoLabel(r.n);
    if (r.kind === 'dst' || r.kind === 'dtime') {
      const f = (v) => r.kind === 'dst' ? `${v} ${plural(v, 'станція', 'станції', 'станцій')}` : fmtTime(v / TPS);
      if (r.isNew) return `<div class="kol-rec kol-new">📅 Новий рекорд дня! ${f(r.val)}${r.prev ? ` (було ${f(r.prev)})` : ''}</div>`;
      return `<div class="kol-rec">📅 Рекорд дня (${who}): ${r.prev ? f(r.prev) : 'ще нема — доїдьте першими'}</div>`;
    }
    if (r.kind === 'st') return r.isNew ? `<div class="kol-rec kol-new">🏆 Новий рекорд! (${who})</div>`
      : !r.prev ? '<div class="kol-rec">🏆 Рекорду ще нема — доїдьте до першої 🚉</div>' : `<div class="kol-rec">🏆 Рекорд (${who}): ${r.prev} ${plural(r.prev, 'станція', 'станції', 'станцій')}</div>`;
    if (r.isNew) return `<div class="kol-rec kol-new">🏆 Новий рекорд сіду! ${fmtTime(r.val / TPS)}${r.prev ? ` (було ${fmtTime(r.prev / TPS)})` : ''}</div>`;
    return `<div class="kol-rec">🏆 Рекорд сіду (${who}): ${fmtTime(r.prev / TPS)}</div>`;
  }

  function findStation(s) {
    if (s.ground) for (let x = 0; x < s.w; x++) for (let y = 0; y < s.h; y++) if (s.ground[y * s.w + x] === G.STATION) return x;
    return (s.w ?? 1) - 1;
  }

  let lastKey = '';
  function update(s, game) {
    if (!s) return;
    game0 = game;
    if (!started) { if (game) game.paused = true; padPressed(); }   // K30: пад веде меню (pauseTick), тут — лише «чи є пад»
    pauseTick(s);   // K23
    introTick(s);
    // DOM — лише коли щось могло змінитись (тик, статус, старт), а не щокадру
    const key = `${s.tick}|${s.status}|${started}|${s.players?.length}|${A11Y.v}`;   // K49: зміна доступності — перемалювати
    if (s === lastState && key === lastKey) return;
    lastKey = key;
    set(start.style, 'start|display', started ? 'none' : 'flex');
    const E = s.endless;   // K23: нескінченна колія — вікно зсувається, станції міняються
    if (s !== lastState || (E && E.shift !== zonesShift)) { lastState = s; zonesShift = E?.shift; stationX = findStation(s); zones = allZones(s); }
    if (E) { const f = E.st?.[E.stations - E.base]; stationX = f ? f.x0 : (s.w ?? 1) - 1; }

    const p = s.params || {}, t = s.train || {};
    const w = s.w || 1, path = s.path || [];
    const sec = (s.tick ?? 0) / TPS;
    set(endlEl.style, 'endl|display', E ? 'block' : 'none');
    if (E) set(endlEl, 'endl|innerHTML', `🚉 ${E.stations} · <span class="kol-wd">ділянка </span>📍${E.segment}`);   // K30: вузько — без слів
    txt(progSt, 'progSt', E ? '🚉' : tt('icon.station', '🏁'));

    // прогрес: голова, кінець колії, станція (за x уздовж смуги)
    let headX = 0;
    try { headX = wagonPos(s, 0).x / U; } catch { }
    const lastCell = path.length ? path[path.length - 1] : 0;
    const endX = (lastCell % w) + 0.5;
    const span = Math.max(1, stationX + 1);
    set(progTrack.style, 'track|width', `${Math.min(100, endX / span * 100).toFixed(1)}%`);
    set(progEnd.style, 'end|left', `${Math.min(100, endX / span * 100).toFixed(1)}%`);
    set(progHead.style, 'head|left', `${Math.min(100, headX / span * 100).toFixed(1)}%`);
    const ahead = Math.max(0, path.length - 1 - Math.floor((t.pos ?? 0) / U));
    set(progAhead, 'ahead|innerHTML', E ? `${tt('icon.rails', '🛤')} ${ahead} · <span class="kol-wd">до наступної </span>🚉 ${E.toNext}` : `${tt('icon.rails', '🛤')} ${ahead}<span class="kol-wd"> попереду</span> · <span class="kol-wd">до </span>${tt('icon.station', '🏁')} ${Math.max(0, Math.round(stationX - headX))}`);
    txt(progHead, 'headI', tt('icon.loco', '🚂')); txt(hIcon, 'hIcon', tt('icon.heat', '♨'));   // K25
    txt(alarm, 'alarmT', tt('alarm', '⚠ Рейки закінчуються!')); txt(countT, 'countT', tt('count', 'паротяг рушає'));
    // K16: проміжна станція — позначка на смузі й банер, поки паротяг чекає вибору
    const mid = s.mid;
    const midX = mid ? (mid.x0 + mid.x1 + 1) / 2 : -1;
    set(progMid.style, 'mid|display', mid ? 'block' : 'none'); set(progMidL.style, 'midl|display', mid ? 'block' : 'none');
    if (mid) {
      const pc = `${Math.min(100, midX / span * 100).toFixed(1)}%`;
      set(progMid.style, 'mid|left', pc); set(progMidL.style, 'midl|left', pc);
      txt(progMid, 'midT', mid.pick ? (UPGRADES[mid.pick]?.icon ?? '✔') : '🏪');
    }
    const signs = () => (mid?.signs ?? []).map((g) => { const u = upName(g.k, UPGRADES[g.k]); return `${u?.icon} ${u?.short}`; }).join(' · ');
    const eStop = !!(E && E.stop > 0 && s.status === 'run');   // K23: зупинка на станції нескінченної — відлік
    const midWait = eStop || !!(mid && mid.wait && !mid.pick && s.status === 'run');
    let midT = '';
    if (eStop) midT = `🚉 Станція ${E.stations}! ${mid && !mid.pick ? `Оберіть покращення — дія на табличку: ${signs()}` : 'Рушаємо'} · ⏳ ${Math.ceil(E.stop / TPS)} с`;
    else if (midWait) midT = `${tt('midWait', '🏪 Проміжна станція!')} Оберіть покращення — дія на табличку: ${signs()}`;
    txt(midEl, 'midE', midT);
    set(midEl.style, 'midE|display', midWait ? 'block' : 'none');
    // K16: біом під паротягом (і наступний, якщо колія вже туди дійшла)
    const zH = zoneAt(zones, headX), zE = zoneAt(zones, endX);
    const bName = (z, w) => `${BIOME_ICON[z?.kind ?? 'glade'] ?? ''}<span class="${w ? 'kol-wd' : ''}"> ${z ? BIOME_SHORT[z.kind] ?? BIOME_NAMES[z.kind] ?? z.kind : 'галявина'}</span>`;   // K30: коротко
    set(biomeEl, 'biome|innerHTML', zE !== zH ? `${bName(zH, true)} → ${bName(zE)}` : bName(zH));
    set(biomeEl, 'biome|title', zE !== zH ? `${BIOME_NAMES[zH?.kind] ?? 'галявина'} → ${BIOME_NAMES[zE?.kind] ?? 'галявина'}` : BIOME_NAMES[zH?.kind] ?? 'галявина');

    const cps = (t.speed ?? 0) / 1000 / U * TPS;   // клітинок/с
    txt(timeEl, 'time', `⏱ ${fmtTime(sec)}`);
    txt(speedT, 'speed', `💨 ${cps.toFixed(2)}`);
    // K56: значки покращень K48 (s.ups з'являється лише після вибору)
    const ups = s.ups;
    const bt = ups?.bt ?? 0;
    txt(brakeEl, 'brake', ups?.brake ? (bt > 0 ? `🐢 ${Math.ceil(bt / TPS)}с` : `🐢×${ups.bn ?? 0}`) : '');
    set(brakeEl, 'brake|className', `kol-up${bt > 0 ? ' kol-up-on' : (ups?.bn ?? 0) ? '' : ' kol-full'}`);
    set(brakeEl, 'brake|title', ups?.brake ? (bt > 0 ? 'Гальма: паротяг їде вдвічі повільніше' : `Гальма: ${ups.bn ?? 0} до наступної станції — дія на паротяг`) : '');
    txt(cistEl, 'cist', ups?.cist ? `🚿 ${ups.cw ?? 0}/${ups.cist * CIST_MAX}` : '');
    set(cistEl, 'cist|className', `kol-up${ups?.cist && !(ups.cw ?? 0) ? ' kol-full' : ''}`);
    set(cistEl, 'cist|title', ups?.cist ? 'Вагон-цистерна: поливів води (повне відро у склад — +2)' : '');
    txt(magEl, 'mag', ups?.mag ? '🧲' : '');
    set(magEl, 'mag|title', ups?.mag ? 'Магніт: склад сам тягне дрова й залізо поруч' : '');

    // склад і майстерня
    const sm = p.storeMax ?? 0;
    txt(sWood, 'wood', `🪵 ${t.wood ?? 0}/${sm}`); set(sWood, 'wood|className', (t.wood ?? 0) >= sm ? 'kol-full' : '');
    txt(sIron, 'iron', `⛓ ${t.iron ?? 0}/${sm}`); set(sIron, 'iron|className', (t.iron ?? 0) >= sm ? 'kol-full' : '');
    txt(cRails, 'rails', `${tt('icon.rails', '🛤')} ${t.rails ?? 0}/${p.railsMax ?? 0}`);
    const craftTot = Math.max(1, (p.craftSec ?? 1) * TPS);
    const cT = t.craftT ?? 0;
    set(cFill.style, 'craft|width', cT > 0 ? `${Math.round((1 - cT / craftTot) * 100)}%` : '0%');
    set(cBar.style, 'cbar|opacity', cT > 0 ? '1' : '.35');

    // котел
    const heat = Math.max(0, Math.min(1000, t.heat ?? 0));
    set(hFill.style, 'heat|width', `${heat / 10}%`);
    txt(hPct, 'hpct', `${Math.round(heat / 10)}%`);
    set(boiler, 'boiler|className', `kol-box kol-row${heat > 750 ? ' kol-hot' : ''}`);

    const run = s.status === 'run';
    const fireT = t.fire ?? 0;
    let fireTxt = '';
    const fireLeft = Math.max(0, Math.ceil((p.fireSec ?? 0) - fireT / TPS));
    if (run && fireT > 0) fireTxt = tt('fire', `🔥 ПОЖЕЖА! Відро води на паротяг · ${fireLeft}`, fireLeft);
    else if (run && heat >= 1000 && p.overheat === 'stop') fireTxt = tt('overheatStop', '♨ Котел перегрівся — паротяг стоїть! Відро води');
    txt(fire, 'fire', fireTxt); set(fire.style, 'fire|display', fireTxt ? 'block' : 'none');

    // тривога: до кінця колії менше ALARM_SEC ходу
    const toEnd = cps > 0 ? (ahead - ((t.pos ?? 0) % U) / U) / cps : Infinity;
    set(alarm.style, 'alarm|display', run && !fireTxt && !midWait && toEnd < ALARM_SEC ? 'block' : 'none');

    // відлік до рушання
    const left = (p.startSec ?? 0) - sec;
    const counting = run && (t.speed ?? 0) === 0 && left > 0;
    set(count.style, 'count|display', counting && started ? 'block' : 'none');
    if (counting) txt(countN, 'countN', String(Math.ceil(left)));

    // гравці
    const players = s.players || [];
    const local = game?.local ?? players.length;
    while (plEls.length < players.length) {
      const i = plEls.length, d = el('div', 'kol-box kol-pl', pls);
      d.style.borderColor = pcol(i);
      plEls.push(d);
    }
    for (let i = 0; i < plEls.length; i++) {
      const pl = players[i], d = plEls[i];
      set(d.style, `pl${i}|display`, pl ? 'block' : 'none');
      set(d.style, `pl${i}|borderColor`, pcol(i));   // K49: палітра може змінитись на ходу
      if (!pl) continue;
      const k = pl.hold?.k ?? 0, nn = pl.hold?.n ?? 0;
      const what = k ? `${k === I.RAIL ? tt('icon.rails', HOLD_ICON[k]) : HOLD_ICON[k] ?? '?'}${nn > 1 ? ' ×' + nn : ''}` : '✋';
      const name = playerLabel(game, i, local);   // K37: у мережі — нік
      set(d, `pl${i}|innerHTML`, `<b style="color:${pcol(i)}">${name}</b> ${what}`);
      set(d, `pl${i}|title`, k === I.RAIL ? tt('hold.rails', HOLD_NAME[k]) : HOLD_NAME[k] ?? 'порожні руки');
    }

    // кінець
    const over = (s.status === 'won' || s.status === 'lost') && started;   // K30: у меню фінал не заступає
    set(end.style, 'end|display', over ? 'flex' : 'none');
    if (over) {
      const st = s.stats || {};
      const head = s.status === 'won' ? tt('end.won', '🏁 Доїхали!') : s.reason === 'fire' ? tt('end.fire', '🔥 Згоріли') : tt('end.derail', '💥 Зійшли з колії');
      const maxCps = (st.maxSpeed ?? 0) / 1000 / U * TPS;
      if (recState !== s) {
        recState = s;
        const day = game?.daily && game.daily.seed === s.seed ? game.daily.date : '';
        rec = s.tutorial || game?.tutorial ? null : day ? judgeDaily(s, day) : judgeRecord(s);
      }
      set(endCard, 'endc|innerHTML', `<h1>${head}</h1>${E ? `<div class="kol-big">🚉 Станцій: <b>${E.stations}</b></div>` : ''}${recHtml(rec)}
        <div class="kol-sum">
          <span>⏱ Час</span><b>${fmtTime(sec)}</b>
          <span>${tt('sum.rails', '🛤 Рейок покладено')}</span><b>${st.railsLaid ?? 0}</b>
          <span>🪓 Ударів</span><b>${st.hits ?? 0}</b>
          <span>💨 Найшвидше</span><b>${maxCps.toFixed(2)} кл/с</b>
        </div>${playersHtml(s)}
        <div class="kol-pmenu kol-endm">${endActs(s).map(([a, t]) => `<button class="kol-again" type="button" data-end="${a}">${t}</button>`).join('')}</div>
        <div class="kol-hint">R / ≡ Start — ${s.status === 'won' ? 'далі' : 'ще раз'} · ↑↓ / ✚ — вибір</div>`);
      endFocus(endIdx);
    }
  }
  // K30: фінал — «Ще раз» / «Наступний сід» / «В меню»; клавіші й пад — через 0,8 с (щоб затиснута дія не тиснула кнопку)
  const tut = (s) => s.params?.mode === 'tutorial' || s.tutorial;
  const endActs = (s) => {
    const next = ['next', tut(s) ? (s.status === 'won' ? '▶ Справжня смуга' : '⏭ Звичайна смуга') : s.status === 'won' ? '▶ Наступний сід' : '⏭ Наступний сід'];
    const again = ['again', '↻ Ще раз'], m = ['menu', '☰ В меню'];
    const rp = game0?.replay?.has?.() ? [['replay', '📼 Повтор'], ['rcode', '📋 Код повтору']] : [];   // K35
    return s.status === 'won' ? [next, again, ...rp, m] : [again, next, ...rp, m];
  };
  let endIdx = 0, endOn = false, endOwned = false, endAt = 0;
  const endBtns = () => [...endCard.querySelectorAll('[data-end]')];
  const endFocus = (i) => { const b = endBtns(); if (!b.length) return; endIdx = (i + b.length) % b.length; b.forEach((x, j) => x.classList.toggle('kol-pf', j === endIdx)); };
  function endAct(a) {
    const g = game0, s = lastState;
    if (!g || !s) return;
    if (a === 'menu') return toMenu();
    if (a === 'replay') return g.replay?.open?.();   // K35
    if (a === 'rcode') return g.replay?.copyCode?.();   // K35
    if (a === 'again') return g.restart ? g.restart(g.seed) : opts.onRestart?.();
    if (a === 'next') return tut(s) ? g.stopTutorial?.() : g.restart ? g.restart(g.seed + 1) : opts.onRestart?.();
  }
  const endReady = () => performance.now() - endAt > 800;
  function endNav(on, s) {
    if (on && !endOn) { endOn = true; endAt = performance.now(); endIdx = 0; endOwned = false; }
    else if (!on && endOn) { endOn = false; if (endOwned && started) opts.input?.setMenu?.(null); endOwned = false; }
    if (!endOn) return;
    if (opts.panelOpen?.()) { endOwned = false; return; }
    if (!endOwned) {
      endOwned = true;
      opts.input?.setMenu?.((k) => {
        if (k === 'up' || k === 'left') endFocus(endIdx - 1);
        else if (k === 'down' || k === 'right') endFocus(endIdx + 1);
        else if (k === 'a' && endReady()) endAct(endBtns()[endIdx]?.dataset.end);
        else if (k === 'start') opts.onRestart?.();
        else if (k === 'menu') opts.onSettings?.();
      });
    }
  }
  function endKey(e) {
    const k = e.code;
    if (k === 'ArrowUp' || k === 'ArrowLeft') endFocus(endIdx - 1);
    else if (k === 'ArrowDown' || k === 'ArrowRight') endFocus(endIdx + 1);
    else if (k === 'Enter' || k === 'NumpadEnter' || k === 'Space') { e.preventDefault(); if (!e.repeat && endReady()) endAct(endBtns()[endIdx]?.dataset.end); }
    else if (k === 'Escape') toMenu();
  }
  endCard.addEventListener('click', (e) => { const b = e.target.closest('[data-end]'); if (b) { b.blur(); endAct(b.dataset.end); } });

  // K16: тости подій тику (гачок у main.js afterStep — бачить кожен тик, не лише кадр)
  let seenGeese = new Set(), evState = null;
  let magTick = -1e9;                 // K56: тост магніту — не частіше ніж раз на MAG_TOAST тиків (тягне раз на 0,5 с)
  const MAG_TOAST = 20 * TPS;
  const pName = (s, p, raw) => playerLabel(game0, p, s.players?.length ?? 0, raw);   // K37: ніки й у тостах, і в таблиці звань
  // K23: підсумок по гравцях — лічильники з s.ev (тик рахуємо раз: мережевий відкат тики не подвоює)
  let tally = [], tallyTick = -1, prevPos = [];
  function tallyTick1(ev, s) {
    if ((s.tick ?? 0) <= tallyTick) return;
    tallyTick = s.tick ?? 0;
    const acted = new Set();
    const T = (p) => (tally[p] ??= { chop: 0, rock: 0, rail: 0, douse: 0, water: 0, carry: 0, throw: 0, catch: 0, pass: 0, stun: 0, hurt: 0, honk: 0, bump: 0, scare: 0, idle: 0, ticks: 0 });
    for (const e of ev) {
      const p = e.t === 'stun' ? e.by : e.p;
      if (p === undefined || p < 0 || p > 3) continue;
      const c = T(p);
      switch (e.t) {
        case 'chop': case 'rock': case 'rail': case 'douse': case 'water': case 'throw': case 'catch': case 'pass': case 'honk': case 'scare': c[e.t]++; break;
        case 'pick': case 'drop': c.carry++; break;
        case 'stun': c.stun++; if (e.p !== undefined) T(e.p).hurt++; break;
        case 'bump': continue;   // p — кого штовхнули, не дія
        default: continue;
      }
      acted.add(p);
    }
    if (s.status !== 'run' || !s.train?.started) return;   // «стояв» — лише поки паротяг їде
    (s.players ?? []).forEach((pl, i) => {
      if (!pl) return;
      const c = T(i), pp = prevPos[i];
      c.ticks++;
      if (pp && pp[0] === pl.x && pp[1] === pl.y && !acted.has(i)) c.idle++;
      prevPos[i] = [pl.x, pl.y];
    });
  }
  // жартівливі звання: найбільший у категорії (≥ min) — звання; кожному до 2, нікому — «🧩 На підхваті»
  const TITLES = [
    ['rail', '🛤 Колійник', 3], ['chop', '🪓 Лісоруб', 5], ['rock', '⛏ Каменяр', 5], ['douse', '💧 Пожежник', 1],
    ['stun', '🤕 Небезпечний', 1], ['catch', '🧤 Воротар', 2], ['throw', '🎯 Метальник', 3], ['pass', '🤝 Передавальник', 3],
    ['carry', '🚚 Вантажник', 8], ['scare', '🪿 Гусятник', 1], ['honk', '📯 Горлань', 5], ['hurt', '🌀 Груша для биття', 2],
  ];
  function titles(n) {
    const cand = Array.from({ length: n }, () => []);
    const row = (i) => tally[i] ?? {};
    for (const [k, name, min] of TITLES) {
      let best = 0, sum = 0;
      for (let i = 0; i < n; i++) { const v = row(i)[k] ?? 0; best = Math.max(best, v); sum += v; }
      if (best < min) continue;
      for (let i = 0; i < n; i++) if ((row(i)[k] ?? 0) === best) cand[i].push([best / sum, name]);
    }
    // кожному — 2 найвиразніші (частка від усіх у категорії)
    const out = cand.map((c) => c.sort((a, b) => b[0] - a[0]).slice(0, 2).map((x) => x[1]));
    // пасажир: найбільша частка простою (≥ 25 %), удвох і більше — навіть якщо щось інше вже має
    const share = (i) => { const r = row(i); return r.ticks ? r.idle / r.ticks : 0; };
    let ps = 0.25;
    for (let i = 0; i < n; i++) ps = Math.max(ps, share(i));
    for (let i = 0; i < n; i++) {
      if (share(i) < ps || (n === 1 && out[i].length)) continue;
      out[i].unshift('🛋 Пасажир'); out[i].length = Math.min(2, out[i].length);
    }
    return out.map((a) => a.length ? a : ['🧩 На підхваті']);
  }
  function playersHtml(s) {
    const n = s.players?.length ?? 0;
    if (!n) return '';
    const tt = titles(n), z = (v) => v ? v : '<i>·</i>';
    let h = '<table class="kol-ptab"><tr><th></th><th title="нарубав">🪓</th><th title="набив каміння">⛏</th><th title="поклав рейок">🛤</th><th title="полив котел">💧</th><th title="кинув / зловив">🎯</th><th title="бац колегу">💫</th><th title="стояв без діла">🛋</th><th></th></tr>';
    for (let i = 0; i < n; i++) {
      const r = tally[i] ?? {};
      const idle = r.ticks ? Math.round(r.idle / r.ticks * 100) : 0;
      h += `<tr><td><b style="color:${pcol(i)}">${pName(s, i)}</b></td><td>${z(r.chop)}</td><td>${z(r.rock)}</td><td>${z(r.rail)}</td><td>${z(r.douse)}</td>`
        + `<td>${z(r.throw || r.catch ? `${r.throw ?? 0}/${r.catch ?? 0}` : 0)}</td><td>${z(r.stun)}</td><td>${idle}%</td><td class="kol-ttl">${tt[i].join(' ')}</td></tr>`;
    }
    return h + '</table>';
  }
  function events(ev, s) {
    if (!ev || !s) return;
    if (game0?.replay?.active && game0.state && s !== game0.state) return;   // K59: світ повтору — ні ачівок, ні тостів, ні звань
    if (s !== evState) { evState = s; seenGeese = new Set(); tally = []; tallyTick = -1; prevPos = []; magTick = -1e9; }
    tallyTick1(ev, s);
    // K50: досягнення — бот не відкриває; маршрут дня — сід дня; тост «🏆 …» через наявні тости
    try {
      const g = game0, net = !!(g?.net || globalThis.KOL?.net);
      const bot = (p) => !net && (g?.bot?.some?.((b) => b.p === p) || p >= (g?.local ?? (s.players?.length ?? 1)));
      for (const a of achievements().tick(ev, s, { bot, daily: !!(g?.daily && g.daily.seed === s.seed), chumak: isChumak() })) say(`🏆 ${a.name}!`, 5000);
    } catch { }
    for (const e of ev) {
      if (e.u) {   // K56: події покращень K48 (поле u) — свій тост
        const u = s.ups ?? {}, max = (u.cist ?? 0) * CIST_MAX;
        if (e.u === 'cist' && e.t === 'douse') say(`🚿 Цистерна полила котел${max ? ` · води ${u.cw ?? 0}/${max}` : ''}`, 3500);
        else if (e.u === 'cist' && e.t === 'drop') say(`🚿 ${e.p !== undefined ? pName(s, e.p, true) + ' долив' : 'Долито'} цистерну · води ${u.cw ?? 0}/${max}`, 3000);
        else if (e.u === 'brake' && e.t === 'whistle') say(`🐢 Гальма! −50 % на ${BRAKE_SEC} с${e.p !== undefined ? ` · ${pName(s, e.p, true)}` : ''}`, 4000);
        else if (e.u === 'magnet' && (s.tick ?? 0) - magTick >= MAG_TOAST) { magTick = s.tick ?? 0; say('🧲 Магніт підтягнув у склад', 2500); }
        continue;
      }
      if (e.t === 'upgrade') {
        const u = upName(s.mid?.pick, UPGRADES[s.mid?.pick]);   // K69: слова теми
        if (u) say(`${u.icon} ${u.label}`, 5000);
      } else if (e.t === 'honk' && e.p === undefined) {
        // гуска щойно вхопила річ: нова «run» з річчю в лапах
        for (const g of s.geese ?? []) {
          if (g.mode !== 'run' || !g.hold || seenGeese.has(g.id)) continue;
          seenGeese.add(g.id);
          say(`🪿 Гуска вкрала ${STEAL_NAME[g.hold] ?? 'річ'}! Наздожени — дія поруч`, 4000, true);
        }
      } else if (e.t === 'goose_lost') say('🪿 Гуска впустила крадене десь попереду', 3500);
      else if (e.t === 'stun' && e.by !== undefined) say(`💫 ${pName(s, e.by, true)} приголомшив ${pName(s, e.p, true)}`, 2500, true);
    }
  }

  return {
    update,
    say,
    events,
    help,   // K65
    dispose() { help.dispose(); removeEventListener('keydown', onKey); for (const id of timers) clearTimeout(id); style.remove(); root.textContent = ''; root.classList.remove('kol-root'); },
  };
}
