// «Колія» — склеювання: цикл фіксованих тиків, локальні гравці, бот, рендер, звук, панель, мережа.
// Власник: оркестратор. Інші задачі чіпають лише позначені ГАЧКИ (один рядок імпорту/виклику), решту зводить оркестратор.
// Адреса: proto.html?seed=7&p=2&bot=1&view=2d&net=<стіл>
//  seed — сід смуги; p — локальних гравців (1..2); bot=N — додати N ботів; view=2d — налагоджувальний вид без three
import * as R from './rules.js';
import { createInput } from './input.js';        // K2
import { createBot } from './bot.js';            // K2
import { createAudio } from './audio.js';        // K4
import { createPanel } from './panel.js';        // K4
import { createDebug2d } from './debug2d.js';
import { createHud, dailySeed, kyivDate } from './hud.js';   // K9; K23: маршрут дня
import { createNet, createDriver } from './net.js';   // K3: ?net=<стіл>|new|auto&nick=…&drive=1
import { createTips } from './tips.js';          // K10: підказки новачкові (?tips=0 — вимкнено)
import { drawExtras2d } from './extras-render.js';   // K11
import { mountLookUi } from './looks.js';            // K5: «🎨 Вигляд» у панелі
import { createTouch, isTouchDevice } from './touch.js';   // K27: телефон і планшет

const q = new URLSearchParams(location.search);
const PARAMS_KEY = 'kol-params';
const U_PER_TICK = R.U / R.TPS;   // 1 клітинка/с у підодиницях за тик

// K21: у localStorage можуть лежати ключі й значення попередніх версій — беремо лише відомі, числа в межах повзунка,
// варіант — лише зі списку (інакше newGame отримав би, скажімо, stripLen 5 чи overheat 'smoke')
function loadParams() {
  const p = R.defaultParams();
  let o = null;
  try { o = JSON.parse(localStorage.getItem(PARAMS_KEY) || '{}'); } catch { }
  if (o && typeof o === 'object') {
    for (const k in R.PARAM_META) {
      const m = R.PARAM_META[k], v = o[k];
      if (typeof v === 'number' && Number.isFinite(v)) p[k] = Math.min(m.max, Math.max(m.min, v));
    }
    for (const k in R.VARIANTS) if (R.VARIANTS[k].values.includes(o[k])) p[k] = o[k];
  }
  p.mode = q.get('tut') && q.get('tut') !== '0' ? 'tutorial' : 'normal';   // K22: навчання — лише ?tut=1 чи game.startTutorial(), не з пам'яті
  return p;
}

const LOCAL = Math.max(1, Math.min(2, (+q.get('p') | 0) || 1));   // K21: ?p=abc давав NaN
const game = {
  R,
  params: loadParams(),
  seed: (q.get('seed') ? +q.get('seed') : (Math.random() * 1e9) | 0) >>> 0,
  local: LOCAL,
  bots: Math.max(0, Math.min(R.MAX_PLAYERS - LOCAL, +q.get('bot') | 0)),   // K21: p=2&bot=3 — бот №5 без гравця в стані
  state: null,
  bot: [],          // [{ p, think(state) → { mx, my, use } }]
  cmds: [],         // черга команд наступного тику (мережа/панель кладуть сюди)
  paused: false,
  speed: 1,         // множник тиків (для перевірок: KOL.game.speed = 10)
};
window.KOL = { game, R, run: (n) => { for (let i = 0; i < n; i++) tickOnce(); return game.state.status; }, hash: () => R.hash(game.state) };

const canvas = document.getElementById('kol-view');
const hud = document.getElementById('kol-hud');
let view = null;
async function makeView() {
  if (q.get('view') === '2d') return createDebug2d(canvas);
  try { const m = await import('./render.js'); return m.createRenderer(canvas); }   // K1
  catch (e) { console.warn('render.js ще нема — налагоджувальний вид', e); return createDebug2d(canvas); }
}

const input = game.input = createInput();   // K16: game.input.hasPad(p) — підказки
const audio = createAudio();
// K12: пристрій (якість, вібрація) — не правила, діють одразу
const PREFS_KEY = 'kol-prefs';
const prefs = { quality: 'high', rumble: true, tips: true };   // K16: tips — «Підказки: так/ні»
try {   // K21: лише відомі значення (старе 'medium' чи рядок 'false' не ламають slowCheck, вібрацію й підказки)
  const o = JSON.parse(localStorage.getItem(PREFS_KEY) || '{}') || {};
  if (o.quality === 'low') prefs.quality = 'low';
  else if (o.quality !== 'high' && isTouchDevice()) prefs.quality = 'low';   // K27: телефон — «низька», поки не обрали самі
  if (o.rumble === false) prefs.rumble = false;
  if (o.tips === false) prefs.tips = false;
} catch { }
input.setRumble(prefs.rumble);
input.setTouch(game.touch = createTouch({ canvas }));   // K27
const nextRestart = () => {
  if (game.state?.status === 'won' && game.params.mode === 'tutorial') return game.stopTutorial();   // K22: навчання пройдено — далі справжня смуга
  restart(game.state?.status === 'won' ? game.seed + 1 : game.seed);
};
// K22: навчальна смуга (tutorial.js) — кнопка «🎓 Навчання» на стартовому екрані HUD кличе game.startTutorial()
const setMode = (mode) => { game.params = { ...game.params, mode }; panel?.set?.({ mode }); restart(); };
game.startTutorial = () => setMode('tutorial');
game.stopTutorial = () => setMode('normal');
const panel = createPanel(document.getElementById('kol-panel'), {
  meta: R.PARAM_META, variants: R.VARIANTS, values: game.params,
  onChange(values) { game.params = { ...values }; try { localStorage.setItem(PARAMS_KEY, JSON.stringify(values)); } catch { } },
  onRestart: () => restart(),
  seed: game.seed, onSeed: (seed) => restart(seed),
  audio,
  prefs, onPref(k, v) {   // K12
    prefs[k] = v; try { localStorage.setItem(PREFS_KEY, JSON.stringify(prefs)); } catch { }
    if (k === 'quality') view?.setQuality?.(v);
    if (k === 'rumble') { input.setRumble(v); if (v) input.rumble(0, 'hit'); }
    if (k === 'tips') game.tips?.setEnabled(v);   // K16
  },
  // K12: поки панель відкрита — гравці стоять, пад водить по панелі (Start — рестарт і закрити)
  onToggle: (open) => input.setMenu(open ? (k) => (k === 'start' ? (panel.toggle(false), nextRestart()) : panel.nav(k)) : null),
});
const net = q.get('net') ? createNet(game, { table: q.get('net'), nick: q.get('nick') || '', input,   // K3
  bot: q.get('drive') ? createDriver() : null, makeBot: game.bots ? (p) => createBot(p) : null, onWorld: (s) => view?.setWorld?.(s), onStep: (s) => afterStep(s) }) : null;
window.KOL.net = net;
input.onMenuKey(() => panel.nav?.('open'));   // K12: Select/View — панель

// K23: «📅 Маршрут дня» — сід від дати (Київ), типові params (панель — для звичайної гри); «ще раз» лишає маршрут дня
game.startDaily = () => { const date = kyivDate(); game.daily = { date, seed: dailySeed(date) }; restart(game.daily.seed); };
// K30: стартове меню (menu.js через hud.js) — гравці/боти, режим ('play'|'tutorial'|'daily'|'endless') і сід; фінал — game.restart
game.restart = (seed) => restart(seed);
game.startMenu = (act, o) => {
  game.local = Math.max(1, Math.min(2, o.players | 0 || 1)); game.wantLocal = 0; input.setPlayers(game.local);
  game.bots = Math.max(0, Math.min(R.MAX_PLAYERS - game.local, o.bots | 0));
  if (act === 'daily') return game.startDaily();
  const sm = game.params.stationMode === 'endless' ? (game.smWas ?? 'end') : game.params.stationMode;
  if (act === 'endless') game.smWas = sm;
  game.params = { ...game.params, mode: act === 'tutorial' ? 'tutorial' : 'normal', stationMode: act === 'endless' ? 'endless' : sm };
  panel?.set?.({ mode: game.params.mode, stationMode: game.params.stationMode }); game.daily = null; restart(o.seed ?? game.seed);
};
function restart(seed = game.seed) {
  if (game.daily && game.daily.seed !== seed >>> 0) game.daily = null;   // K23
  const P = game.daily ? R.defaultParams() : game.params;   // K23
  if (net) return net.restart(seed, P);   // K3
  if (game.wantLocal) { game.local = game.wantLocal; game.wantLocal = 0; game.bots = Math.min(game.bots, R.MAX_PLAYERS - game.local); input.setPlayers(game.local); }   // K12
  game.seed = seed >>> 0;
  panel?.setSeed?.(game.seed);
  game.state = R.newGame(game.seed, P, game.local + game.bots);
  game.bot = [];
  for (let b = 0; b < game.bots; b++) game.bot.push(createBot(game.local + b));
  view?.setWorld?.(game.state);
}

function tickOnce() {
  const s = game.state;
  const cmds = game.cmds.splice(0);
  const ins = input.poll();
  for (let i = 0; i < game.local; i++) {
    const v = ins[i]; if (!v) continue;
    cmds.push({ p: i, k: 'in', mx: Math.round(v.mx * 100), my: Math.round(v.my * 100) });
    if (v.use) cmds.push({ p: i, k: 'use' });
    if (v.throw) cmds.push({ p: i, k: 'throw' });   // K18 (кнопки — input.js, K16)
    if (v.horn) cmds.push({ p: i, k: 'horn' });
  }
  for (const b of game.bot) {
    const v = b.think(s);
    cmds.push({ p: b.p, k: 'in', mx: v.mx | 0, my: v.my | 0 });
    if (v.use) cmds.push({ p: b.p, k: 'use' });
  }
  R.step(s, cmds);
  afterStep(s);
}
function afterStep(s) {
  audio.play(s.ev, s);   // K16: зі станом — кроки за підлогою
  for (const e of s.ev) {   // K12: вібрація пада — удар і поразка
    if ((e.t === 'chop' || e.t === 'rock') && e.p < game.local) input.rumble(e.p, 'hit');
    else if (e.t === 'lost') input.rumble(-1, 'lost');
  }
  audio.setTrain?.(s.status === 'run' ? Math.min(1, s.train.speed / 1000 / (s.params.speedMax * U_PER_TICK)) : 0);
  view?.events?.(s.ev);
  hudUi?.events?.(s.ev, s);   // K16: тости подій
  if (s.endless && s.ev.some((e) => e.t === 'shift')) view?.setWorld?.(s);   // K19: вікно нескінченної колії зсунулось — сцену заново
}

// Тестовий режим (?test або headless-автоматизація): не більше 10 к/с — headless Chrome малює WebGL програмно й їсть CPU проду
const TEST = q.has('test') || navigator.webdriver;
const nextFrame = () => TEST ? setTimeout(() => requestAnimationFrame(frame), 100) : requestAnimationFrame(frame);
let acc = 0, last = performance.now();
function frame(now) {
  const dt = Math.min(250, now - last); last = now;
  if (input.restartPressed?.()) nextRestart();
  if (!net && game.local === 1 && !game.wantLocal && (++padPoll & 15) === 0 && input.padCount() >= 2) joinP2();   // K12: другий пад — П2
  // K21: пауза — стартовий екран (HUD ставить game.paused) або відкрита панель (лише локально: мережа не чекає)
  const hold = game.paused || (!net && panel.isOpen());
  if (!hold && dt < 250) slowCheck(dt);   // K12
  if (net) net.update(now);   // K3
  else if (!hold) {
    acc += dt * game.speed;
    while (acc >= R.TICK_MS) { tickOnce(); acc -= R.TICK_MS; }
  }
  if (!game.state) return nextFrame();   // K3: мережа ще бере журнал
  if (net) net.draw(view); else view?.draw(game.state, acc / R.TICK_MS);   // K6: мережа малює гравців згладжено
  if (q.get('view') === '2d') drawExtras2d(canvas, game.state);   // K11: 🪿 й таблички
  drawHud();
  (game.tips ??= createTips(document.body, { enabled: q.get('tips') !== '0' && prefs.tips !== false })).update(game.state, game, view);   // K10
  nextFrame();
}

// K12: перші 6 с гри тягне повільно (середній кадр > 24 мс, < ~40 к/с) — сам вмикаємо «Якість: низька», раз
let padPoll = 0, slowN = 0, slowSum = 0;
function slowCheck(dt) {
  if (slowN < 0 || TEST || prefs.quality !== 'high' || typeof view?.setQuality !== 'function') return;
  slowN++; slowSum += dt;
  if (slowSum < 6000) return;
  const avg = slowSum / slowN; slowN = -1;
  game.fpsStart = Math.round(1000 / avg);
  if (avg <= 24) return;
  prefs.quality = 'low'; view.setQuality('low'); panel.setPrefs?.({ quality: 'low' });
  try { localStorage.setItem(PREFS_KEY, JSON.stringify(prefs)); } catch { }
  hudUi?.say?.(`🐢 ${game.fpsStart} к/с — увімкнув «Якість: низька» (⚙ / ⧉ View — повернути)`, 6000);
}

// K12: другий пад під'єднано при p=1 — П2 одразу (правила вміють 'join'), а з ботами — з наступного рестарту
function joinP2() {
  const s = game.state;
  if (!s || s.tick === 0 || s.status !== 'run') { game.wantLocal = 2; restart(); hudUi?.say?.('🎮 П2 приєднався'); return; }
  if (game.bots === 0 && s.players.length < R.MAX_PLAYERS) { game.local = 2; input.setPlayers(2); game.cmds.push({ k: 'join' }); hudUi?.say?.('🎮 П2 приєднався'); return; }
  game.wantLocal = 2;
  hudUi?.say?.('🎮 П2 приєднався — з’явиться з наступного рестарту (≡ Start)', 5000);
}

let hudUi = null;
function drawHud() {
  hudUi ??= createHud(hud, { players: game.local + game.bots, onStart: () => input.swallow(), onRestart: nextRestart,   // K9, K12
    input, onSettings: () => panel.toggle(true), panelOpen: () => !!panel.isOpen?.() });   // K23: пауза
  game.hud = hudUi;   // K12: для замірів у консолі
  hudUi.update(game.state, game);
}

(async () => {
  view = game.view = await makeView();
  mountLookUi(document.getElementById('kol-panel'), view);   // K5
  view?.setQuality?.(prefs.quality);   // K12
  if (net) net.start().catch((e) => {   // K3; K21: без сервера — не чорний екран мовчки
    console.warn('Колія: мережа', e);
    if (!game.state) { const u = new URL(location.href); u.searchParams.delete('net'); hud.innerHTML = `⚠ Не вдалося підключитись до столу. <a href="${u.pathname + u.search}" style="pointer-events:auto;color:#ffd23f">🏠 Грати без мережі</a>`; }   // K30
  }); else restart();
  requestAnimationFrame(frame);
})();
