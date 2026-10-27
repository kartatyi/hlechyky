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
import { createHud } from './hud.js';            // K9
import { createTips } from './tips.js';          // K10: підказки новачкові (?tips=0 — вимкнено)
import { drawExtras2d } from './extras-render.js';   // K11

const q = new URLSearchParams(location.search);
const PARAMS_KEY = 'kol-params';
const U_PER_TICK = R.U / R.TPS;   // 1 клітинка/с у підодиницях за тик

function loadParams() {
  const p = R.defaultParams();
  try { Object.assign(p, JSON.parse(localStorage.getItem(PARAMS_KEY) || '{}')); } catch { }
  return p;
}

const game = {
  R,
  params: loadParams(),
  seed: (q.get('seed') ? +q.get('seed') : (Math.random() * 1e9) | 0) >>> 0,
  local: Math.max(1, Math.min(2, +(q.get('p') || 1))),
  bots: Math.max(0, Math.min(3, +(q.get('bot') || 0))),
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

const input = createInput();
const audio = createAudio();
// K12: пристрій (якість, вібрація) — не правила, діють одразу
const PREFS_KEY = 'kol-prefs';
const prefs = { quality: 'high', rumble: true };
try { Object.assign(prefs, JSON.parse(localStorage.getItem(PREFS_KEY) || '{}')); } catch { }
input.setRumble(prefs.rumble);
const nextRestart = () => restart(game.state?.status === 'won' ? game.seed + 1 : game.seed);
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
  },
  // K12: поки панель відкрита — гравці стоять, пад водить по панелі (Start — рестарт і закрити)
  onToggle: (open) => input.setMenu(open ? (k) => (k === 'start' ? (panel.toggle(false), nextRestart()) : panel.nav(k)) : null),
});
input.onMenuKey(() => panel.nav?.('open'));   // K12: Select/View — панель

function restart(seed = game.seed) {
  if (game.wantLocal) { game.local = game.wantLocal; game.wantLocal = 0; game.bots = Math.min(game.bots, R.MAX_PLAYERS - game.local); input.setPlayers(game.local); }   // K12
  game.seed = seed >>> 0;
  panel?.setSeed?.(game.seed);
  game.state = R.newGame(game.seed, game.params, game.local + game.bots);
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
  }
  for (const b of game.bot) {
    const v = b.think(s);
    cmds.push({ p: b.p, k: 'in', mx: v.mx | 0, my: v.my | 0 });
    if (v.use) cmds.push({ p: b.p, k: 'use' });
  }
  R.step(s, cmds);
  audio.play(s.ev);
  for (const e of s.ev) {   // K12: вібрація пада — удар і поразка
    if ((e.t === 'chop' || e.t === 'rock') && e.p < game.local) input.rumble(e.p, 'hit');
    else if (e.t === 'lost') input.rumble(-1, 'lost');
  }
  audio.setTrain?.(s.status === 'run' ? Math.min(1, s.train.speed / 1000 / (s.params.speedMax * U_PER_TICK)) : 0);
  view?.events?.(s.ev);
}

// Тестовий режим (?test або headless-автоматизація): не більше 10 к/с — headless Chrome малює WebGL програмно й їсть CPU проду
const TEST = q.has('test') || navigator.webdriver;
const nextFrame = () => TEST ? setTimeout(() => requestAnimationFrame(frame), 100) : requestAnimationFrame(frame);
let acc = 0, last = performance.now();
function frame(now) {
  const dt = Math.min(250, now - last); last = now;
  if (input.restartPressed?.()) nextRestart();
  if (game.local === 1 && !game.wantLocal && input.padCount() >= 2) joinP2();   // K12: другий пад — П2
  if (!game.paused) {
    acc += dt * game.speed;
    while (acc >= R.TICK_MS) { tickOnce(); acc -= R.TICK_MS; }
  }
  view?.draw(game.state, acc / R.TICK_MS);
  if (q.get('view') === '2d') drawExtras2d(canvas, game.state);   // K11: 🪿 й таблички
  drawHud();
  (game.tips ??= createTips(document.body, { enabled: q.get('tips') !== '0' })).update(game.state, game, view);   // K10
  nextFrame();
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
  hudUi ??= createHud(hud, { players: game.local + game.bots, onStart: () => input.swallow(), onRestart: nextRestart });   // K9, K12
  hudUi.update(game.state, game);
}

(async () => {
  view = game.view = await makeView();
  view?.setQuality?.(prefs.quality);   // K12
  restart();
  requestAnimationFrame(frame);
})();
